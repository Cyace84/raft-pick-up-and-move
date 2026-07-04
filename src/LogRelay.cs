using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace PickUpMove
{
    // Dev-loop instrumentation. Every line THIS MOD logs is:
    //   (a) mirrored to a PER-SESSION file on the local disk (BepInEx/PickUpMoveLogs/self-<stamp>.log),
    //       so BepInEx's truncate-on-start never eats history again, and
    //   (b) on a CLIENT, relayed to the HOST over a private Steam P2P channel (121), where the host
    //       appends it to a per-session client file (client-<stamp>.log, sender-tagged).
    // Kills the "paste your log over Telegram" debug loop: both sides of a multiplayer bug land on
    // the host machine, timestamped, per session. Only lines from THIS mod travel - a few hundred
    // bytes per minute at worst.
    internal static class LogRelay
    {
        private const int Channel = 121;        // separate from move traffic (120): a chatty log can never delay a move packet
        private const uint Magic = 0x504D564C;  // 'PMVL'
        private const int MaxQueued = 500;      // client backlog cap while no host connection exists
        private const int MaxBatchBytes = 6000;
        private const byte TypeBatch = 1;

        private static readonly Queue<string> _outbox = new Queue<string>();
        private static StreamWriter _selfFile;    // our own lines (host or client role alike)
        private static StreamWriter _clientFile;  // host only: relayed client lines
        private static string _dir, _stamp;
        private static float _nextFlush;
        private static bool _enabled;

        internal static void Init(bool enabled)
        {
            _enabled = enabled;
            if (!_enabled) return;
            _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _dir = Path.Combine(Paths.BepInExRootPath, "PickUpMoveLogs");
            try { Directory.CreateDirectory(_dir); } catch { }
            try { BepInEx.Logging.Logger.Listeners.Add(new Listener()); } catch { }
        }

        private static StreamWriter OpenWriter(string name)
        {
            try
            {
                var w = new StreamWriter(Path.Combine(_dir, name), append: true) { AutoFlush = true };
                return w;
            }
            catch { return null; }
        }

        // NEVER log from inside the listener (feedback loop) - swallow everything.
        private sealed class Listener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs e)
            {
                try
                {
                    if (e?.Source?.SourceName != "Pick Up & Move") return;
                    var line = $"[{DateTime.Now:HH:mm:ss}] [{e.Level,-7}] {e.Data}";
                    if (_selfFile == null) _selfFile = OpenWriter($"self-{_stamp}.log");
                    _selfFile?.WriteLine(line);
                    // queue for relay; role is decided at FLUSH time (host clears, client sends)
                    if (_outbox.Count < MaxQueued) _outbox.Enqueue(line);
                }
                catch { }
            }
            public void Dispose() { try { _selfFile?.Flush(); _clientFile?.Flush(); } catch { } }
        }

        // called from Plugin.Tick every frame (both roles), all failures silent
        internal static void Tick()
        {
            if (!_enabled) return;
            try
            {
                if (Raft_Network.IsHost)
                {
                    if (_outbox.Count > 0) _outbox.Clear(); // host's own lines don't relay anywhere
                    DrainHost();
                }
                else FlushClient();
            }
            catch { }
        }

        private static void FlushClient()
        {
            if (_outbox.Count == 0 || Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + 2f;
            var net = ComponentManager<Raft_Network>.Value;
            var host = net != null ? net.CurrentSteamHost : default;
            if (!host.IsValid()) return; // not connected yet - keep queueing (capped)

            var lines = new List<string>();
            int bytes = 16;
            while (_outbox.Count > 0 && lines.Count < 200)
            {
                var l = _outbox.Peek();
                bytes += l.Length * 2 + 4;
                if (bytes > MaxBatchBytes && lines.Count > 0) break;
                lines.Add(_outbox.Dequeue());
            }
            try
            {
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                w.Write(Magic); w.Write(TypeBatch); w.Write((ushort)lines.Count);
                foreach (var l in lines) w.Write(l);
                var data = ms.ToArray();
                Steamworks.SteamNetworking.SendP2PPacket(host, data, (uint)data.Length,
                    Steamworks.EP2PSend.k_EP2PSendReliable, Channel);
            }
            catch { }
        }

        private static void DrainHost()
        {
            while (Steamworks.SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
            {
                var buf = new byte[size];
                if (!Steamworks.SteamNetworking.ReadP2PPacket(buf, size, out uint _, out Steamworks.CSteamID sender, Channel)) break;
                Plugin.RegisterPeer(sender); // relay batches double as a live roster of real Steam ids
                try
                {
                    using var r = new BinaryReader(new MemoryStream(buf));
                    if (r.ReadUInt32() != Magic || r.ReadByte() != TypeBatch) continue;
                    int n = r.ReadUInt16();
                    if (_clientFile == null) _clientFile = OpenWriter($"client-{_stamp}.log");
                    string tag = (sender.m_SteamID % 100000).ToString();
                    for (int i = 0; i < n && i < 200; i++)
                        _clientFile?.WriteLine($"[recv {DateTime.Now:HH:mm:ss}] [{tag}] {r.ReadString()}");
                }
                catch { }
            }
        }
    }
}
