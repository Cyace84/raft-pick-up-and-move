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
            // Vanilla-log tap. The 07-13 zombie-chest incident was undiagnosable because the
            // client's BepInEx config captured no Unity-source lines at all: the game's own
            // markers (BlockCreator.Deserialize logs "Could not remove block because no block
            // with index N" on a failed remove lookup) never reached her disk. Subscribing to
            // Application.logMessageReceived (CoreModule decomp: Application.cs:363, delegate
            // LogCallback(condition, stackTrace, type):40) is independent of BepInEx's log
            // config, so the relay sees them regardless of the peer's BepInEx.cfg. Filtered
            // hard (errors/exceptions + known block/storage markers) - channel 121 stays quiet.
            try { Application.logMessageReceived += OnUnityLog; } catch { }
        }

        // Only these vanilla Info-level lines matter to move diagnosis; everything else Info/Warning
        // from Unity is noise and stays local. Errors/exceptions always travel.
        private static readonly string[] UnityMarkers =
        {
            "Could not remove block",   // BlockCreator.cs:1732 - remove-by-index lookup failed
            "Could not close storage",  // StorageManager:193  - slots-sync lookup failed
            "Could not open storage",
        };

        private static string _lastUnityLine;
        private static float _lastUnityTime;
        private static int _unityRepeats;

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (!_enabled) return;
            try
            {
                bool always = type == LogType.Exception || type == LogType.Error || type == LogType.Assert;
                if (!always)
                {
                    if (condition == null) return;
                    bool marked = false;
                    foreach (var m in UnityMarkers) if (condition.Contains(m)) { marked = true; break; }
                    if (!marked) return;
                }
                // per-frame spam guard: identical line within 5s collapses into one repeat counter
                float now = Time.realtimeSinceStartup;
                if (condition == _lastUnityLine && now - _lastUnityTime < 5f) { _unityRepeats++; return; }
                if (_unityRepeats > 0)
                {
                    Record(LogLevel.Debug, $"[unity] last line repeated {_unityRepeats} more time(s)");
                    _unityRepeats = 0;
                }
                _lastUnityLine = condition; _lastUnityTime = now;

                var line = $"[unity/{type}] {condition}";
                if (type == LogType.Exception && !string.IsNullOrEmpty(stackTrace))
                {
                    // top of the stack is enough to localize the throw; keep relay packets small
                    int cut = 0, newlines = 0;
                    for (int i = 0; i < stackTrace.Length && newlines < 3; i++)
                        if (stackTrace[i] == '\n') { newlines++; cut = i; }
                    var top = cut > 0 ? stackTrace.Substring(0, cut) : stackTrace;
                    if (top.Length > 400) top = top.Substring(0, 400);
                    line += " | " + top.Replace('\n', ';');
                }
                Record(LogLevel.Info, line);
            }
            catch { }
        }

        // Fed directly by Plugin.Emit (no BepInEx log-bus hook), so the file/relay sink is INDEPENDENT
        // of whether lines also go to the console. No-op unless RelayLogs enabled this session.
        internal static void Record(LogLevel level, string data)
        {
            if (!_enabled) return;
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] [{level,-7}] {data}";
                if (_selfFile == null) _selfFile = OpenWriter($"self-{_stamp}.log");
                _selfFile?.WriteLine(line);
                if (_outbox.Count < MaxQueued) _outbox.Enqueue(line); // role decided at flush time
            }
            catch { }
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
