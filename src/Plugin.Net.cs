using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Move-channel protocol: client requests, host verify pipeline, refusals, probes, hello.
    public partial class Plugin
    {
        // CLIENT-MOVES-ANYTHING (path a): a non-host mover can't authoritatively create/restore a
        // device, so it asks the HOST (who also runs the mod) to do the whole move. We use a private
        // Steam P2P channel (the game's session is already open, channels just multiplex) carrying a
        // tiny request; the host reads the block's OWN authoritative state and runs the normal host
        // pipeline. Original stays hidden on the client until the host's removal replicates back.
        private const int MoveChannel = 120;
        private const uint MoveMagic = 0x504D5645;   // 'PMVE'
        private static bool _awaitingHostMove;
        private static Block _pendingClientMoveOriginal;
        private static int _clientMoveDeadlineFrame;
        // captured from our own (replicated) original so we can restore the moved block on OUR view -
        // the host restores its own view + the save, but device state (cooking pot/juicer/grill) does
        // not auto-replicate back to us after a programmatic restore.
        private static RGD_Block _clientMoveRgd;
        private static RGD_Slot[] _clientMoveSlots;
        private static Paint _clientMovePaint;
        private static string _clientMoveText;
        private static bool _clientMoveRestored;
        // R2: the host names the new block's object index in a kind-10 'move done' packet, so the
        // client targets THAT block instead of guessing by distance (a nearby unrelated block within
        // 1m of the ghost could win the old search and receive our device state + storage echo).
        // _clientMoveOrigIndex survives phase 1 (unlike _pendingClientMoveOriginal, cleared there) so
        // the kind-10 handler can still match the reply to this move.
        private static uint _clientMoveOrigIndex;
        private static uint _clientMoveNewIndex;
        // [t] timing probes (first-move-of-type can take seconds on the host) + refusal-relay state
        private static float _cmSentTime;          // Time.realtimeSinceStartup when the request left
        private static bool _cmOrigGoneLogged;
        private static bool _cmSeenLogged;
        private static Steamworks.CSteamID _hostReqSender; // valid = current verify is a client request
        private static float _hostReqRecvTime;
        // HOST place-first verify: nb is created but the original is removed only once nb settles to
        // IsStable() over a few physics steps (the same-frame check reads stale). The original stays
        // hidden until then, so a never-settling spot is undone with nothing lost.
        private static bool _hostVerifying;
        private static Block _hostNb;
        private static Block _hostOriginal;
        private static RGD_Slot[] _hostSlots;
        private static string _hostText;
        private static RGD_Block _hostRgd;
        private static int _hostVerifyStart;
        private static float _hostVerifyDeadlineTime; // wall-clock, not frames: frame deadlines scale
        // with fps, and slow settlers (recycler, ~4s) landed exactly on the old 120-frame cutoff.
        private static int _hostLastLoggedStable; // -1 unknown, 0 false, 1 true (log only on change)
        private static Paint _hostPaint;
        // Data-loss gate state: a storage restore is read-back verified and the original is removed
        // only after proof. See TryRestoreSlotsVerified for the mechanism.
        private static bool _hostBaseVerified;   // base block's slots restored + read back
        private static bool _hostDepsMoved;      // TryMoveDependents already ran (must not re-run)
        private static string _hostRestoreWait;  // last logged wait reason (log on change only)
        private sealed class DepRestore { public Block Nd; public RGD_Slot[] Slots; public bool Retried; }
        private static readonly List<DepRestore> _pendingDepRestores = new List<DepRestore>();

        // CLIENT content-sync: after we asked the host to place the chest, watch StorageManager for
        // the new storage (object index not in our pre-place snapshot) nearest the placed spot, then
        // push our carried contents to the host via the vanilla Message_Storage_Close path and apply
        // them to our own view. Times out so we never poll forever.
        // HOST place-first verify: wait for the freshly-placed chest to settle to IsStable() (the
        // same-frame check reads stale), then remove the original. A genuinely unsupported
        // spot never settles -> we undo (discard nb, restore original), losing nothing.
        // CLIENT -> HOST: fire a tiny move request on the private channel. Host derives item + all state
        // from the original block itself, so we only send {origIndex, target localPos/localEuler, dps}.
        private static bool SendMoveRequest(Network_Player player, uint origIndex, Vector3 pos, Vector3 rot, DPS dps)
        {
            try
            {
                var host = player.Network.CurrentSteamHost;
                if (!host.IsValid()) return false;
                byte[] data;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(MoveMagic); w.Write((byte)1);
                    w.Write(origIndex);
                    w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                    w.Write(rot.x); w.Write(rot.y); w.Write(rot.z);
                    w.Write((int)dps);
                    data = ms.ToArray();
                }
                return Steamworks.SteamNetworking.SendP2PPacket(host, data, (uint)data.Length,
                    Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel);
            }
            catch (System.Exception ex) { Warn("send move req: " + ex.Message); return false; }
        }

        // HOST -> CLIENT: tell the requester WHY a move was declined so it can restore its original
        // immediately with the real reason instead of sitting out the generic 10s failsafe (observed:
        // recycler "snap back" was two legit host refusals the client never learned about).
        private static void SendMoveRefusal(Steamworks.CSteamID to, uint origIndex, string reason)
        {
            if (!to.IsValid()) return;
            try
            {
                byte[] data;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(MoveMagic); w.Write((byte)2);
                    w.Write(origIndex);
                    w.Write(reason ?? "");
                    data = ms.ToArray();
                }
                Steamworks.SteamNetworking.SendP2PPacket(to, data, (uint)data.Length,
                    Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel);
            }
            catch (System.Exception ex) { Warn("send refusal: " + ex.Message); }
        }

        // CLIENT: drain refusal packets from the host (type 2 on the private channel).
        private static void PollMoveRefusals()
        {
            try
            {
                while (Steamworks.SteamNetworking.IsP2PPacketAvailable(out uint size, MoveChannel))
                {
                    var buf = new byte[size];
                    if (!Steamworks.SteamNetworking.ReadP2PPacket(buf, size, out uint _, out Steamworks.CSteamID _, MoveChannel)) break;
                    try
                    {
                        using var r = new BinaryReader(new MemoryStream(buf));
                        if (r.ReadUInt32() != MoveMagic) continue;
                        byte kind = r.ReadByte();
                        uint origIndex = r.ReadUInt32();
                        bool mine = _awaitingHostMove && _pendingClientMoveOriginal != null
                            && _pendingClientMoveOriginal.ObjectIndex == origIndex;
                        if (kind == 2) // refusal
                        {
                            string reason = r.ReadString();
                            if (mine)
                            {
                                RestoreHidden();
                                _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                                _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null; _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
                                NoteHud(Loc.T(reason));
                            }
                        }
                        else if (kind == 3) // ack: the host has the request; it WILL answer - extend the wait
                        {
                            if (mine && !_cmAcked)
                            {
                                _cmAcked = true;
                                _clientMoveDeadlineFrame = Time.frameCount + 1800; // verdict failsafe, not a guess window
                                float dt = Time.realtimeSinceStartup - _cmSentTime;
                                Note($"[t] host acked after {dt:F2}s");
                                if (dt > 3f) NoteHud(Loc.T("working"));
                            }
                        }
                        else if (kind == 9) // paint notify: recolour our replica (idempotent, resent 2x)
                        {
                            ApplyPaintNotify(r, origIndex);
                        }
                        else if (kind == 7) // teleport notify: the block MOVED (same object, all state intact)
                        {
                            var tpPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            var tpRot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            var tb = BlockCreator.GetBlockByObjectIndex(origIndex);
                            if (tb != null)
                            {
                                PipeLifecycle(tb, place: false); // client-side: refreshes tiles only (group ops are host-gated)
                                tb.transform.localPosition = tpPos;
                                tb.transform.localEulerAngles = tpRot;
                                PipeLifecycle(tb, place: true);
                                RefreshWires(tb);
                            }
                            if (mine) // our pending move completed as a teleport - un-hide the SAME object at its new spot
                            {
                                RestoreHidden();
                                _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                                _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null; _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
                                Note($"[t] teleported {Time.realtimeSinceStartup - _cmSentTime:F2}s after request");
                                Note("client: host moved it.");
                            }
                        }
                        else if (kind == 13) // claim mirror update: idx is (un)claimed by owner
                        {
                            byte claimed = r.ReadByte(); ulong owner = r.ReadUInt64();
                            if (claimed != 0) _claimMirror[origIndex] = owner; else _claimMirror.Remove(origIndex);
                        }
                        else if (kind == 14) // claim denied: someone else already carries it - drop ours
                        {
                            if (!ReferenceEquals(Moving, null) && Moving != null && Moving.ObjectIndex == origIndex)
                            { NoteHud(Loc.T("r_carry")); CancelMove(); }
                            if (_carryClaimIdx == origIndex) _carryClaimIdx = 0; // never got it - nothing to release
                        }
                        else if (kind == 10) // move done: the host names the new block's object index
                        {
                            uint newIdx = r.ReadUInt32();
                            // match on the index we asked to move (survives phase 1, unlike the pending
                            // reference). Idempotent: resends just re-set the same field.
                            if (_awaitingHostMove && origIndex == _clientMoveOrigIndex && _clientMoveOrigIndex != 0)
                                _clientMoveNewIndex = newIdx;
                        }
                        else if (kind == 6) // probe reply: does the original still exist on the host?
                        {
                            bool exists = r.ReadByte() != 0;
                            if (mine && _cmProbeSent)
                            {
                                if (exists)
                                {
                                    // it exists - sync our replica to the host's CURRENT transform (if
                                    // the move was a teleport whose notifies were all lost, this is
                                    // where we converge instead of resurrecting it at the old spot)
                                    try
                                    {
                                        var hp = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                        var hr = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                        _pendingClientMoveOriginal.transform.localPosition = hp;
                                        _pendingClientMoveOriginal.transform.localEulerAngles = hr;
                                        RefreshWires(_pendingClientMoveOriginal);
                                    }
                                    catch { }
                                    RestoreHidden();
                                    _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                                    _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null; _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
                                    Note("client: synced with the host.");
                                }
                                else
                                {
                                    // The move DID happen but its replication was lost mid-outage: our
                                    // original is a zombie (the host doesn't know it - dead E prompt,
                                    // unmovable, Z-fighting dupe). Remove it locally exactly like the
                                    // lost vanilla message would have, then let PHASE 2 pick up the new
                                    // block if its creation replicated.
                                    var z = _pendingClientMoveOriginal;
                                    _pendingClientMoveOriginal = null;
                                    _hiddenColliders.Clear(); _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
                                    try { BlockCreator.RemoveBlock(z, null, true); }
                                    catch (System.Exception ex) { Warn("zombie cleanup: " + ex.Message); }
                                    _clientMoveDeadlineFrame = Time.frameCount; // phase 2 grace runs from now
                                    Note("client: the host moved it but the confirmation was lost - cleaned up the stale copy.");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (System.Exception ex) { Warn("refusal poll: " + ex.Message); }
        }

        private sealed class MoveReq { public byte[] Buf; public Steamworks.CSteamID Sender; public float RecvTime; public uint Idx; }
        private static readonly Queue<MoveReq> _moveReqQueue = new Queue<MoveReq>();
        // idx -> realtime of that block's last committed move (teleport keeps the index alive, so a
        // stale queued request could re-move it; recreate is covered by the zombie gate as well).
        private static readonly Dictionary<uint, float> _movedAt = new Dictionary<uint, float>();

        // ---------------- CARRY CLAIM ("one M per block") ----------------
        // Picking a block up CLAIMS it for the carrier; everyone else's M on it is refused until the
        // carry resolves. Optimistic: M starts the carry instantly, the claim races to the host
        // (kind-11); the host arbitrates ties (first claim wins, host wins simultaneous) and fans the
        // verdict out (kind-13 to mirrors, kind-14 deny tears the loser's carry down). A carrier that
        // vanishes can't wedge the block: client claims expire 30s after the last heartbeat
        // (kind-11 re-sent every 10s while carrying).
        private struct Claim { public ulong Owner; public float At; }
        private static readonly Dictionary<uint, Claim> _claims = new Dictionary<uint, Claim>();      // HOST: authoritative
        private static readonly Dictionary<uint, ulong> _claimMirror = new Dictionary<uint, ulong>(); // CLIENT: local mirror
        private static uint _carryClaimIdx;      // the claim WE hold (both roles)
        private static float _claimNextBeat;     // client heartbeat timer
        private static float _claimsNextPrune;   // host TTL sweep timer
        private const float ClaimTtl = 30f;

        private static ulong SelfId()
        { try { return Steamworks.SteamUser.GetSteamID().m_SteamID; } catch { return 0; } }

        // both roles, called from the pickup gate: is this block claimed by someone else?
        internal static bool IsClaimedByOther(uint idx)
        {
            ulong self = SelfId();
            if (Raft_Network.IsHost)
                return _claims.TryGetValue(idx, out var c) && c.Owner != self;
            return _claimMirror.TryGetValue(idx, out var o) && o != self;
        }

        // both roles, called right after the carry starts (Moving = block)
        internal static void AcquireCarryClaim(uint idx)
        {
            _carryClaimIdx = idx;
            if (Raft_Network.IsHost) HostSetClaim(idx, SelfId());
            else { SendClaimCtl(11, idx); _claimNextBeat = Time.realtimeSinceStartup + 10f; }
        }

        private static void SendClaimCtl(byte kind, uint idx) // CLIENT -> host: 11 claim, 12 release
        {
            try
            {
                var np = ComponentManager<Network_Player>.Value;
                var hostId = np?.Network != null ? np.Network.CurrentSteamHost : default;
                if (!hostId.IsValid()) return;
                using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
                w.Write(MoveMagic); w.Write(kind); w.Write(idx);
                QueueModSend(hostId, ms.ToArray());
            }
            catch (System.Exception ex) { Warn("send claim: " + ex.Message); }
        }

        private static void HostSetClaim(uint idx, ulong owner)
        {
            _claims[idx] = new Claim { Owner = owner, At = Time.realtimeSinceStartup };
            BroadcastClaim(idx, owner, claimed: true);
        }

        private static void HostReleaseClaim(uint idx)
        {
            if (!_claims.Remove(idx)) return;
            BroadcastClaim(idx, 0, claimed: false);
        }

        private static void BroadcastClaim(uint idx, ulong owner, bool claimed) // HOST -> every client
        {
            try
            {
                using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
                w.Write(MoveMagic); w.Write((byte)13); w.Write(idx);
                w.Write((byte)(claimed ? 1 : 0)); w.Write(owner);
                var data = ms.ToArray();
                foreach (var sid in _knownPeers.Keys)
                    if (sid != owner) QueueModSend(new Steamworks.CSteamID(sid), data);
            }
            catch (System.Exception ex) { Warn("claim broadcast: " + ex.Message); }
        }

        // central claim lifecycle, runs every Tick on both roles: releases the claim no matter WHICH
        // path ended the carry (place, RMB/M cancel, fake-null teardown, refusal) - a client's claim
        // is held through the whole pending-move phase so nobody grabs the block mid-resolution.
        internal static void PollClaim()
        {
            float now = Time.realtimeSinceStartup;
            if (_carryClaimIdx != 0)
            {
                bool carrying = !ReferenceEquals(Moving, null) && Moving != null;
                bool resolving = _awaitingHostMove
                    || (Raft_Network.IsHost && (_hostVerifying || _tpVerifying || _reqScan != null || _pickupScan != null));
                if (!carrying && !resolving)
                {
                    if (Raft_Network.IsHost) HostReleaseClaim(_carryClaimIdx);
                    else SendClaimCtl(12, _carryClaimIdx);
                    _carryClaimIdx = 0;
                }
                else if (!Raft_Network.IsHost && now >= _claimNextBeat)
                { SendClaimCtl(11, _carryClaimIdx); _claimNextBeat = now + 10f; }
            }
            // HOST: expire client claims whose carrier stopped heartbeating (crash, quit, lost link)
            if (Raft_Network.IsHost && _claims.Count > 0 && now >= _claimsNextPrune)
            {
                _claimsNextPrune = now + 5f;
                ulong self = SelfId();
                List<uint> drop = null;
                foreach (var kv in _claims)
                    if (kv.Value.Owner != self && now - kv.Value.At > ClaimTtl)
                        (drop ??= new List<uint>()).Add(kv.Key);
                if (drop != null) foreach (var i in drop) { Note($"[t] claim on #{i} expired (carrier silent {ClaimTtl:F0}s)"); HostReleaseClaim(i); }
            }
        }
        // ------------------------------------------------------------------
        // origIndexes whose client canceled the request before we started it (packet types: 1=request,
        // 2=refusal, 3=ack, 4=cancel, 5=probe, 6=probe-reply). A newer request for the same index
        // supersedes an older cancel (per-peer FIFO ordering on the reliable channel guarantees the
        // cancel always drains before the retry).
        private static readonly HashSet<(ulong sender, uint idx)> _canceledReqs = new HashSet<(ulong, uint)>();
        // CLIENT: host acknowledged our pending move request (it WILL answer with success or refusal,
        // so the blind timeout no longer applies); probe = zombie check after a lost verdict.
        private static bool _cmAcked, _cmProbeSent;

        // tiny control packet on the move channel: ack / cancel / probe / probe-reply.
        private static void SendMoveCtl(Steamworks.CSteamID to, byte type, uint origIndex, byte extra = 0)
        {
            if (!to.IsValid()) return;
            try
            {
                byte[] data;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(MoveMagic); w.Write(type); w.Write(origIndex);
                    if (type == 6) w.Write(extra);
                    data = ms.ToArray();
                }
                Steamworks.SteamNetworking.SendP2PPacket(to, data, (uint)data.Length,
                    Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel);
            }
            catch (System.Exception ex) { Warn("send ctl: " + ex.Message); }
        }

        // HOST -> requesting CLIENT: name the new block's object index once the recreate move is
        // committed (R2). Rides the idempotent resend queue (the new block's create may still be
        // replicating); the client stores the index and targets THAT block for its local device
        // restore instead of guessing the nearest block to the ghost.
        private static void SendMoveDone(Steamworks.CSteamID to, uint origIndex, uint newIndex)
        {
            if (!to.IsValid()) return;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(MoveMagic); w.Write((byte)10); w.Write(origIndex); w.Write(newIndex);
            QueueModSend(to, ms.ToArray());
        }

        // HOST: always drain the socket (so packets don't pile up in Steam's buffer), but run only ONE
        // move at a time - each kicks off the place-first verify pipeline. A request that arrives while
        // we're mid-move is QUEUED, not dropped (dropping was the cause of the "client move lagged /
        // didn't land" when moving blocks in quick succession).
        // Presence beacon: the host learns peer Steam ids ONLY from packets the mod sends (vanilla
        // addressing is PlayFab, see _knownPeers). A quiet client (no move requests, RelayLogs off
        // so no relay batches either) is never in _knownPeers and misses every host-initiated
        // teleport - it keeps seeing the moved block at its old spot. A 9-byte hello every 10s
        // fixes the roster; PollMoveRequests RegisterPeer()s the sender of ANY packet, so kind 8
        // needs no handler.
        private static float _nextHello;
        private static void SendHello()
        {
            if (Time.realtimeSinceStartup < _nextHello) return;
            _nextHello = Time.realtimeSinceStartup + 10f;
            try
            {
                var net = ComponentManager<Raft_Network>.Value;
                var host = net != null ? net.CurrentSteamHost : default;
                if (!host.IsValid()) return;
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                w.Write(MoveMagic); w.Write((byte)8); w.Write(0u); // kind 8 = hello
                var data = ms.ToArray();
                Steamworks.SteamNetworking.SendP2PPacket(host, data, (uint)data.Length,
                    Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel);
            }
            catch { }
        }

        private static void PollMoveRequests()
        {
            try
            {
                while (Steamworks.SteamNetworking.IsP2PPacketAvailable(out uint size, MoveChannel))
                {
                    var buf = new byte[size];
                    if (!Steamworks.SteamNetworking.ReadP2PPacket(buf, size, out uint _, out Steamworks.CSteamID sender, MoveChannel)) break;
                    RegisterPeer(sender);
                    if (buf.Length < 9) continue;
                    byte kind; uint idx;
                    try
                    {
                        using var r = new BinaryReader(new MemoryStream(buf));
                        if (r.ReadUInt32() != MoveMagic) continue;
                        kind = r.ReadByte(); idx = r.ReadUInt32();
                    }
                    catch { continue; }
                    switch (kind)
                    {
                        case 1: // move request: ack IMMEDIATELY (even while busy) so the client knows
                                // it's in flight and doesn't blind-timeout into a split brain (observed:
                                // >10s Steam transit -> client gave up + retried while we executed the
                                // stale request anyway -> zombie chest / dupes / 'couldn't find').
                            // CLAIM-lite (observed 07-15: the host lost EVERY collision): a request for
                            // the block the HOST is carrying or mid-moving must be refused NOW, not
                            // queued - a queued request drains right after our own place and silently
                            // overrides it, so the host's move never sticks. Requests for OTHER blocks
                            // still queue behind the current move as before.
                            if ((!ReferenceEquals(Moving, null) && Moving != null && Moving.ObjectIndex == idx)
                                || (_hostVerifying && _hostOriginal != null && _hostOriginal.ObjectIndex == idx)
                                || (_reqScan != null && _reqScan.B != null && _reqScan.B.ObjectIndex == idx)
                                || (_tpVerifying && _tpBlock != null && _tpBlock.ObjectIndex == idx))
                            { SendMoveRefusal(sender, idx, "r_carry"); break; }
                            _canceledReqs.Remove((sender.m_SteamID, idx)); // a retry supersedes any older cancel FROM THE SAME PEER
                            SendMoveCtl(sender, 3, idx);
                            _moveReqQueue.Enqueue(new MoveReq { Buf = buf, Sender = sender, RecvTime = Time.realtimeSinceStartup, Idx = idx });
                            break;
                        case 11: // claim (also heartbeat): first claim wins; the host's own carry wins ties
                        {
                            if (_claims.TryGetValue(idx, out var cl) && cl.Owner != sender.m_SteamID)
                            {
                                // taken - tear the optimistic carry down on the loser
                                using var msD = new MemoryStream(); using var wD = new BinaryWriter(msD);
                                wD.Write(MoveMagic); wD.Write((byte)14); wD.Write(idx);
                                QueueModSend(sender, msD.ToArray());
                            }
                            else HostSetClaim(idx, sender.m_SteamID); // new claim or heartbeat refresh
                            break;
                        }
                        case 12: // release - only the owner can free its claim
                            if (_claims.TryGetValue(idx, out var rl) && rl.Owner == sender.m_SteamID)
                                HostReleaseClaim(idx);
                            break;
                        case 4: // cancel: drop the request if we haven't started it. Keyed by
                                // (sender, idx): two players moving the same object index in different
                                // sub-rafts must not cancel each other (idx is unique per world, but a
                                // 3-player revival scenario can retry an index another peer still owns).
                                // PURGE the queue NOW, not just tombstone: a later retry (case 1) removes
                                // the tombstone, which would RESURRECT a canceled request still sitting
                                // in the queue (cancel -> retry -> stale req1 executes, then req2).
                                // Per-peer FIFO guarantees the cancel drains before any retry, so purging
                                // here catches every request the cancel was aimed at.
                            _canceledReqs.Add((sender.m_SteamID, idx));
                            {
                                int qn = _moveReqQueue.Count, purged = 0;
                                for (int qi = 0; qi < qn; qi++)
                                {
                                    var m = _moveReqQueue.Dequeue();
                                    if (m.Sender.m_SteamID == sender.m_SteamID && m.Idx == idx) { purged++; continue; }
                                    _moveReqQueue.Enqueue(m);
                                }
                                Note($"[t] client canceled move request for block #{idx}" + (purged > 0 ? $" ({purged} purged from queue)" : ""));
                            }
                            break;
                        case 9: // paint notify from a client mover: apply to our authoritative view,
                                // then fan out to every OTHER peer (the mover already painted its own)
                            try
                            {
                                using (var r9 = new BinaryReader(new MemoryStream(buf)))
                                {
                                    r9.ReadUInt32(); r9.ReadByte(); r9.ReadUInt32(); // magic+kind+idx, re-skip
                                    ApplyPaintNotify(r9, idx);
                                }
                                foreach (var sid in _knownPeers.Keys)
                                    if (sid != sender.m_SteamID) QueueModSend(new Steamworks.CSteamID(sid), buf);
                            }
                            catch (System.Exception ex) { Warn("paint notify (host): " + ex.Message); }
                            break;
                        case 5: // probe: does this block still exist here? reply carries the current
                                // transform so the client converges even when every notify was lost
                            var pb = BlockCreator.GetBlockByObjectIndex(idx);
                            if (pb == null) SendMoveCtl(sender, 6, idx, 0);
                            else
                            {
                                try
                                {
                                    using var ms = new MemoryStream();
                                    using var w = new BinaryWriter(ms);
                                    w.Write(MoveMagic); w.Write((byte)6); w.Write(idx); w.Write((byte)1);
                                    var pp = pb.transform.localPosition; var pr = pb.transform.localEulerAngles;
                                    w.Write(pp.x); w.Write(pp.y); w.Write(pp.z);
                                    w.Write(pr.x); w.Write(pr.y); w.Write(pr.z);
                                    var data = ms.ToArray();
                                    Steamworks.SteamNetworking.SendP2PPacket(sender, data, (uint)data.Length, Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel);
                                }
                                catch (System.Exception ex) { Warn("probe reply: " + ex.Message); }
                            }
                            break;
                    }
                }
            }
            catch (System.Exception ex) { Warn("move req poll: " + ex.Message); }

            while (!_hostVerifying && !_tpVerifying && _reqScan == null && Moving == null && _moveReqQueue.Count > 0)
            {
                var req = _moveReqQueue.Dequeue();
                if (_canceledReqs.Remove((req.Sender.m_SteamID, req.Idx))) { Note($"[t] skipped canceled move request #{req.Idx}"); continue; }
                HandleMoveRequest(req);
                break;
            }
            if ((_hostVerifying || Moving != null) && _moveReqQueue.Count > 0 && Time.frameCount % 300 == 0)
                Note($"[t] {_moveReqQueue.Count} move request(s) queued behind the current move");
        }

        // HOST: a client asked us to move a block. Capture its authoritative state and run the SAME
        // place-first verify pipeline the host uses for its own moves (ApplyState + group move + remove
        // original, all replicated). Caller (PollMoveRequests) guarantees we aren't already mid-move.
        private static void HandleMoveRequest(MoveReq req)
        {
            var buf = req.Buf;
            uint origIndex; Vector3 pos, rot; DPS dps;
            try
            {
                using var r = new BinaryReader(new MemoryStream(buf));
                if (r.ReadUInt32() != MoveMagic || r.ReadByte() != 1) return;
                origIndex = r.ReadUInt32();
                pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                rot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                dps = (DPS)r.ReadInt32();
            }
            catch { return; }

            var original = BlockCreator.GetBlockByObjectIndex(origIndex);
            // null in practice means 'removed since the client saw it' (a client can only request a
            // block that existed on its screen), so the honest verdict is 'gone', not 'not found'.
            if (original == null) { SendMoveRefusal(req.Sender, origIndex, "r_gone"); return; }
            // ZOMBIE gate: removal is a coroutine (RemoveBlockCoroutine -> DestroyBlock,
            // BlockCreator.cs:510), so a block committed for removal stays in placedBlocks for a few
            // frames. Observed dup (07-15 02:55:05): host and client both carried the same chest;
            // host's place removed the original, the client's QUEUED request drained the same frame,
            // resolved the zombie and recreated it -> two chests with the same 12 slots. DestroyBlock
            // SetActive(false)s the block SYNCHRONOUSLY before its first yield (BlockCreator.cs:595),
            // so an inactive original = mid-destruction, whoever initiated it (mod or vanilla axe).
            // _removalChecks additionally names every index WE committed for removal (~2s window).
            if (!original.gameObject.activeInHierarchy || _removalChecks.Exists(c => c.Idx == origIndex))
            { SendMoveRefusal(req.Sender, origIndex, "r_gone"); return; }
            // CLAIM gate: the block is claimed by a DIFFERENT player than the requester - someone
            // else is carrying it right now; this move must not run (mirrors the pickup-side refusal
            // for requests that raced past a mirror update).
            if (_claims.TryGetValue(origIndex, out var reqClm) && reqClm.Owner != req.Sender.m_SteamID)
            { SendMoveRefusal(req.Sender, origIndex, "r_carry"); return; }
            // MOVED-EPOCH gate (the teleport twin of the zombie gate; observed 07-15: host places,
            // the client's QUEUED request then drains and re-moves the chest to the client's spot -
            // 'the host always loses'). A request RECEIVED before the block's latest committed move
            // was aimed at a world that no longer exists - refuse it; a fresh M+place afterwards
            // passes (its RecvTime postdates the commit).
            if (_movedAt.TryGetValue(origIndex, out float mvt) && req.RecvTime < mvt)
            { SendMoveRefusal(req.Sender, origIndex, "r_gone"); return; }
            var item = original.buildableItem;
            if (item == null) { SendMoveRefusal(req.Sender, origIndex, "r_no_rebuild"); return; }
            var player = ComponentManager<Network_Player>.Value;
            var bc = player?.BlockCreator;
            if (bc == null) { SendMoveRefusal(req.Sender, origIndex, "r_not_ready"); return; }

            // SAME PREFAB VARIANT -> teleport (see ConfirmMove): the type-7 notify doubles as the
            // success signal for the requesting client. Block_Pipe included - BeginTeleport replays
            // the autotile lifecycle around the move (see PipeLifecycle).
            if (SameVariant(original, item, dps))
            {
                Note($"[t] teleport '{item.UniqueName}' #{origIndex} -> {pos.ToString("F2")}");
                // flip-test dep scan first (the block's colliders are ON here; the scan owns the
                // toggle and needs settle frames) - the teleport starts when the scan lands.
                var sender = req.Sender;
                _reqScan = StartDepScan(original, ownB: true,
                    s => BeginTeleport(original, pos, rot, sender, s.Deps));
                return;
            }
            // teleport-only types must not be rebuilt (unhandled state would be lost)
            if (HasUnhandledState(original))
            { SendMoveRefusal(req.Sender, origIndex, "surface"); return; }

            // R1 (host): an OPEN storage's edits are invisible until Message_Storage_Close lands
            // (StorageManager.cs:190 applies slots only on close) - a snapshot taken now is stale by
            // construction and the editor's close would target a removed index (vanilla drops it,
            // items lost/duped). Refuse the RECREATE; the teleport branch above is safe (same object
            // index survives, the close lands on it). Re-checked at commit in FinishHostMove.
            if (original is Storage_Small stBusy && stBusy.IsOpen)
            { SendMoveRefusal(req.Sender, origIndex, "r_busy"); return; }

            // authoritative capture from the original block (host owns the real state)
            var slots = (original is Storage_Small st && st.GetInventoryReference() != null)
                ? st.GetInventoryReference().GetRGDSlots() : null;
            var paint = CapturePaint(original);
            var text = TryGetSignText(original);
            var rgd = TryCaptureRgd(original);

            // hide the original like a carry: colliders off (so the new block's stability + the group
            // cascade detection judge it as if the original were gone) + renderers/canvases off.
            _hiddenColliders.Clear(); _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
            foreach (var c in original.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; _hiddenColliders.Add(c); }
            HideVisual(original);

            Block nb;
            float tCreate = Time.realtimeSinceStartup;
            try { nb = bc.CreateBlockCheat(item, pos, rot, dps, -1); }
            catch (System.Exception ex)
            {
                Warn("client-move create: " + ex.Message); RestoreHidden();
                SendMoveRefusal(req.Sender, origIndex, "r_no_place");
                return;
            }
            if (nb == null)
            {
                RestoreHidden();
                SendMoveRefusal(req.Sender, origIndex, "r_no_place");
                return;
            }
            // [t] localize the first-move-of-type ~5s: how long the request waited in Steam's buffer/our
            // queue vs how long the actual instantiate took (per-type first-use asset warm-up suspect).
            Note($"[t] req '{item.UniqueName}': queue={tCreate - req.RecvTime:F2}s create={Time.realtimeSinceStartup - tCreate:F2}s");

            // [diag] compare what the client asked for against what we actually built - a recycler is
            // multi-cell so a small pos/rot/dps divergence makes its stability gizmos miss support.
            Note($"[diag] move '{item.UniqueName}': recv pos={pos.ToString("F3")} euler={rot.ToString("F3")} dps={dps}"
                + $" -> built pos={nb.transform.localPosition.ToString("F3")} euler={nb.transform.localEulerAngles.ToString("F3")} dps={nb.dpsType}");

            ArmHostVerify(nb, original, slots, text, rgd, paint, req.Sender, req.RecvTime);
            Trace($"client-requested move: verifying nb@{nb.transform.localPosition.ToString("F2")}");
        }

        // CLIENT: after asking the host to move, watch our (hidden) original. The host's removal
        // replicates -> the reference goes null = success. Timeout -> host didn't act, un-hide it.
        private static void PollClientMove()
        {
            // PHASE 1: wait for the host to REMOVE the original before we restore our own view. Some
            // devices (cropplot) recreate networked sub-objects (plants) under the SAME object index as
            // the original; if we recreate them while the original still exists, the two race and the
            // original's removal can orphan our copy (plant becomes un-harvestable). The host removes
            // first, so mirroring that order - restore only AFTER the original is gone - keeps our copy
            // the sole holder of that index, exactly like the host ends up.
            if (_pendingClientMoveOriginal != null)
            {
                if (Time.frameCount > _clientMoveDeadlineFrame)
                {
                    var np = ComponentManager<Network_Player>.Value;
                    var hostId = np?.Network != null ? np.Network.CurrentSteamHost : default;
                    if (!_cmAcked)
                    {
                        // The request never reached the host (no ack) - CANCEL it before restoring the
                        // original. Per-peer FIFO on the reliable channel means the cancel drains ahead
                        // of any retry, so the host can never execute this stale request later (the
                        // split-brain that made zombie chests: >10s transit -> we gave up + retried ->
                        // host executed the old request anyway).
                        SendMoveCtl(hostId, 4, _pendingClientMoveOriginal.ObjectIndex);
                        RestoreHidden();
                        _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                        _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null; _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
                        NoteHud(Loc.T("no_request"));
                    }
                    else if (!_cmProbeSent)
                    {
                        // acked but the verdict is overdue - ask whether the original still exists there
                        SendMoveCtl(hostId, 5, _pendingClientMoveOriginal.ObjectIndex);
                        _cmProbeSent = true; _clientMoveDeadlineFrame = Time.frameCount + 600;
                        Note("[t] verdict overdue - probing the host for the original");
                    }
                    else
                    {
                        // probe also unanswered - the link is gone; keep the original, nothing lost
                        RestoreHidden();
                        _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                        _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null; _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
                        NoteHud(Loc.T("no_answer"));
                    }
                }
                return;
            }

            // PHASE 2: original is gone - target the new block by the object index the host NAMED
            // (kind-10), never by distance (R2). We restore ONLY device state locally: slots, paint
            // and sign text all arrive from the host's OWN authoritative replication
            // (Message_Storage_Close RPC, kind-9 paint notify, SetTextNetworked). Echoing our
            // pickup-time snapshot back is R1 - the host applies it unconditionally over whatever it
            // holds NOW, so a chest the host edited (or another player opened) during our carry is
            // duped/wiped. The client is never a source of block state anymore.
            if (!_clientMoveRestored)
            {
                if (!_cmOrigGoneLogged) { Note($"[t] original removed {Time.realtimeSinceStartup - _cmSentTime:F2}s after request"); _cmOrigGoneLogged = true; }
                Block best = _clientMoveNewIndex != 0 ? BlockCreator.GetBlockByObjectIndex(_clientMoveNewIndex) : null;
                if (best != null && !_cmSeenLogged) { Note($"[t] new block #{_clientMoveNewIndex} seen {Time.realtimeSinceStartup - _cmSentTime:F2}s after request (stable={best.IsStable()})"); _cmSeenLogged = true; }
                bool graceOver = Time.frameCount > _clientMoveDeadlineFrame + 180;
                if (best != null && (best.IsStable() || graceOver))
                {
                    // device state (cooking progress, fuel, charger batteries...) does NOT replicate
                    // back from the host's programmatic restore, so the client re-applies it locally.
                    // Wait for the block to settle first (device RestoreBlock wants placement finished);
                    // apply anyway once grace runs out. ApplyDeviceState is view-only here (cropplot
                    // re-plant is host-gated inside).
                    if (!best.IsStable()) Warn("client: new block never read stable locally; applying device state anyway.");
                    try { ApplyDeviceState(_clientMoveRgd, best); }
                    catch (System.Exception ex) { Warn("client device restore: " + ex.Message); }
                    _clientMoveRestored = true;
                }
                else if (!graceOver)
                {
                    return; // grace: wait for kind-10, the new block to replicate in, and it to settle
                }
                else
                {
                    // host never named a new block (kind-10 lost AND the recreate path) - say so
                    // instead of guessing. Slots/paint/text still arrive via the host's vanilla sync;
                    // only local device view may be stale until a reload.
                    if (_clientMoveNewIndex == 0) Warn("client: host never confirmed a new block index (kind-10 lost); device view may be stale until reload.");
                    else Warn($"client: new block #{_clientMoveNewIndex} never replicated in; device view not re-applied.");
                }
            }

            _hiddenColliders.Clear(); _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
            _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null; _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
            _awaitingHostMove = false;
            Note($"[t] client move total {Time.realtimeSinceStartup - _cmSentTime:F2}s (request -> restored)");
            Note("client: host moved it.");
        }

        // Arm the settle-then-verify poll for a freshly placed block. Both entries - the local
        // place in ConfirmMove and a client request in HandleMoveRequest - MUST come through here;
        // a field armed in one copy but not the other is the same bug family as the hand-copied
        // resets were. sender/recvTime: default/0 for a local move, the requester for a client one.
        private static void ArmHostVerify(Block nb, Block original, RGD_Slot[] slots, string text, RGD_Block rgd, Paint paint, Steamworks.CSteamID sender, float recvTime)
        {
            _hostNb = nb; _hostOriginal = original; _hostSlots = slots;
            _hostText = text; _hostRgd = rgd; _hostPaint = paint;
            _hostReqSender = sender; _hostReqRecvTime = recvTime;
            _hostVerifyStart = Time.frameCount;
            _hostVerifyDeadlineTime = Time.realtimeSinceStartup + 6f; // wall-clock; slow settlers (recycler ~4s) fit
            _hostLastLoggedStable = -1;
            _hostBaseVerified = false; _hostDepsMoved = false; _hostRestoreWait = null;
            _pendingDepRestores.Clear();
            _hostVerifying = true;
        }

        // R3: the Ticker is DontDestroyOnLoad, so EVERY move-related static survives a world switch
        // (quit to menu, load another save). A fake-null _pendingClientMoveOriginal, a stale
        // _moveReqQueue entry, or a half-finished verify would then run against the NEW world - on a
        // matching object index it can move or remove the wrong block, and a leftover _knownPeers
        // addresses ghosts. Wipe all session state on a full (Single) scene load. The old world's
        // objects are being torn down, so we only clear references (no RestoreHidden - those
        // renderers/colliders are already gone).
        internal static void ResetSessionState()
        {
            // carry
            Moving = null; _movingItem = null; _movingSlots = null; _movingText = null; _movingRgd = null;
            _movingPaint = default;
            _hiddenColliders.Clear(); _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
            _carryDeps.Clear(); _pickupScan = null; _reqScan = null;
            _rearmBuild = false; _bcWasInactive = false;
            try { DestroyGhostPreviews(); } catch { }

            // client-side pending move
            _awaitingHostMove = false; _pendingClientMoveOriginal = null;
            _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
            _clientMovePaint = default; _clientMoveRestored = false;
            _clientMoveOrigIndex = 0; _clientMoveNewIndex = 0;
            _cmAcked = false; _cmProbeSent = false; _cmOrigGoneLogged = false; _cmSeenLogged = false;

            // host verify + request queue
            ResetHostVerify();
            _moveReqQueue.Clear(); _canceledReqs.Clear(); _movedAt.Clear();
            _claims.Clear(); _claimMirror.Clear(); _carryClaimIdx = 0;

            // teleport
            _tpBlock = null; _tpVerifying = false; _tpReqSender = default;
            _tpDeps.Clear(); _tpDepsOldPos.Clear(); _tpDepsOldRot.Clear();
            _tpSends.Clear();

            // dependents / plants / restore watchdog
            _depOriginals.Clear(); _newDependents.Clear(); _depColliderDisabled.Clear(); _depMovedCount = 0;
            _pendingDepRestores.Clear(); _plantBroadcastPlots.Clear();
            _restoreWatches.Clear(); _removalChecks.Clear();

            // roster + beacon relearn from packets in the new session
            _knownPeers.Clear(); _nextHello = 0f;
        }

        // Every exit from a host verify (success, revert, vanish) drops ALL of this state -
        // a field that survives here leaks into the next move's verify.
        private static void ResetHostVerify()
        {
            _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null;
            _hostText = null; _hostRgd = null; _hostPaint = default; _hostReqSender = default;
            _hostBaseVerified = false; _hostDepsMoved = false; _hostRestoreWait = null;
            _pendingDepRestores.Clear();
        }

        // Remove a block and VERIFY it actually went away (R5). RemoveBlockNetwork can silently
        // no-op (a de-registered block, a fake-null shadow) and the callers then claim success -> a
        // duplicate survives. Removal is a COROUTINE (RemoveBlockCoroutine -> DestroyBlock yields,
        // BlockCreator.cs:510), so placedBlocks.Remove happens over the next frames, NOT
        // synchronously - a same-frame postcondition would false-warn on every success. We instead
        // schedule a check ~2s later: GetBlockByObjectIndex(idx) still resolving to this exact live
        // object means the removal never took.
        private sealed class RemovalCheck { public Block B; public uint Idx; public string Where; public float At; }
        private static readonly List<RemovalCheck> _removalChecks = new List<RemovalCheck>();
        private static void RemoveBlockChecked(Block b, string where)
        {
            if (ReferenceEquals(b, null)) return;
            uint idx = b.ObjectIndex;
            try { BlockCreator.RemoveBlockNetwork(b, null, true); }
            catch (System.Exception ex) { Warn($"{where}: RemoveBlockNetwork threw for #{idx}: {ex.Message}"); }
            _removalChecks.Add(new RemovalCheck { B = b, Idx = idx, Where = where, At = Time.realtimeSinceStartup + 2f });
        }

        // Deferred postcondition for RemoveBlockChecked. still != null uses Unity's overload, so a
        // destroyed/fake-null block reads null and does NOT false-trip; only a live, still-registered
        // block (the no-op case) warns.
        private static void PollRemovalChecks()
        {
            if (_removalChecks.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _removalChecks.Count - 1; i >= 0; i--)
            {
                var c = _removalChecks[i];
                if (now < c.At) continue;
                _removalChecks.RemoveAt(i);
                var still = BlockCreator.GetBlockByObjectIndex(c.Idx);
                if (still != null && ReferenceEquals(still, c.B))
                    Warn($"{c.Where}: block #{c.Idx} still present ~2s after RemoveBlockNetwork - the removal no-oped, likely a duplicate. Please report this line.");
            }
        }

        // Full undo of a host move: discard the new copies (base + any moved dependents), restore
        // the hidden original. Safe when no dependents were moved - both discards no-op on empty lists.
        private static void UndoHostMove(Block nb)
        {
            DiscardNewDependents();
            RestoreDependentColliders();
            _plantBroadcastPlots.Clear();   // undone move - nothing to announce
            DeregisterCropplotPlants(nb);   // its restored plants must not shadow the surviving original
            RemoveBlockChecked(nb, "undo host move");
            RestoreHidden();
        }

        // The placed block disappeared before settling (removed by the game, another peer, a
        // cascade). Revert everything; the original was never touched.
        private static void HostVerifyVanished()
        {
            UndoHostMove(null); // also discards any dependents already re-created this verify
            if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, _hostOriginal != null ? _hostOriginal.ObjectIndex : 0u, "r_move_failed");
            ResetHostVerify();
            Warn("host verify: placed chest vanished before settling; original restored, nothing lost.");
        }

        // Deadline hit while the placed block still (or again) reads unstable: remove it, restore
        // the original, tell the requesting client why.
        private static void HostVerifyDeadline(int delta)
        {
            // UndoHostMove also discards dependents: reachable when the block settled, deps were
            // re-created, and THEN it lost support - the old code left those copies standing.
            UndoHostMove(_hostNb);
            LogStability(_hostNb); // which gizmo cell(s) never found support
            Note("host place fail at +" + delta + "f"); NoteHud(Loc.T("no_support"));
            if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, _hostOriginal != null ? _hostOriginal.ObjectIndex : 0u, "no_support");
            ResetHostVerify();
        }

        // Point of no return: every restore verified. Remove the originals (dependents first),
        // announce moved plants, report, reset.
        private static void FinishHostMove(Block nb, Block original, RGD_Slot[] slots, Network_Player player, int delta)
        {
            // capture the requester + indices BEFORE removal/reset: the client targets the new block
            // by index (R2), not by distance. origIndex is the shared networked index the client sent.
            var reqSender = _hostReqSender;
            uint origIndex = original != null ? original.ObjectIndex : 0u;
            uint newIndex = nb != null ? nb.ObjectIndex : 0u;

            // R1 (host): `slots` was captured at request/pickup time; the original lived on through
            // the multi-frame verify and its content can have changed since (edits apply only on
            // Message_Storage_Close, StorageManager.cs:190). Still OPEN at commit -> the editor's
            // close is still in flight and MUST land on the ORIGINAL index -> abort the whole move,
            // nothing lost. Closed -> re-read the original NOW and, if it drifted from the snapshot,
            // commit the fresh content to nb and re-sync peers. This is the last read before the
            // original's index dies.
            if (original != null && original is Storage_Small osCommit)
            {
                if (osCommit.IsOpen)
                {
                    UndoHostMove(nb);
                    if (reqSender.IsValid()) SendMoveRefusal(reqSender, origIndex, "r_busy");
                    else NoteHud(Loc.T("r_busy"));
                    ResetHostVerify();
                    Note("commit aborted: the storage is open right now; move undone, nothing lost.");
                    return;
                }
                var invO = osCommit.GetInventoryReference();
                var fresh = invO != null ? invO.GetRGDSlots() : null;
                if (!SlotsEqual(slots, fresh))
                {
                    slots = fresh;
                    if (nb is Storage_Small nbs && nbs.GetInventoryReference() != null)
                    {
                        try { nbs.GetInventoryReference().SetSlotsFromRGD(fresh); SendStorageSync(nbs, player); }
                        catch (System.Exception ex) { Warn("commit recapture: " + ex.Message); }
                        Note("storage changed during verify; committed the fresh contents.");
                    }
                }
            }

            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            _hiddenCanvases.Clear();
            // remove originals: dependents first (their colliders were disabled during detection),
            // then the base block - nothing rests on it now, so no cascade victims.
            foreach (var d in _depOriginals) if (d != null) { DeregisterCropplotPlants(d); RemoveBlockChecked(d, "finish host move (dep)"); }
            _depOriginals.Clear(); _depColliderDisabled.Clear(); _newDependents.Clear();
            if (original != null) { DeregisterCropplotPlants(original); RemoveBlockChecked(original, "finish host move"); }

            // originals are gone on every peer (reliable ordered channel) - now announce the moved
            // plants so clients recreate them through the game's own planting path (harvest-linked).
            FlushPlantBroadcasts(player);

            // tell the requesting client which block is the move's result (R2)
            if (reqSender.IsValid()) SendMoveDone(reqSender, origIndex, newIndex);
            // moved-epoch: the zombie gate already covers the dead origIndex for its ~2s window;
            // this stamp keeps refusing stale requests after that window too.
            _movedAt[origIndex] = Time.realtimeSinceStartup;

            int restored = slots?.Length ?? 0;
            Note($"placed at {nb.transform.localPosition.ToString("F2")} after settling (+{delta}f, host)"
                + (restored > 0 ? $"; restored {restored} slots" : "")
                + (_depMovedCount > 0 ? $"; moved {_depMovedCount} on top" : ""));
            if (_hostReqSender.IsValid()) Note($"[t] client move total {Time.realtimeSinceStartup - _hostReqRecvTime:F2}s (recv -> original removed)");
            ResetHostVerify();
        }

        // PHASE A - the moved block's own state, slots read-back verified (storage restore gate):
        // wait for vanilla's OnFinishedPlacement, write, read back. Until verified the original is
        // untouched, so the deadline revert loses nothing. False = done for this frame (retrying,
        // or already reverted on deadline).
        private static bool VerifyBaseRestore(Block nb, RGD_Slot[] slots, Network_Player player, int delta)
        {
            if (_hostBaseVerified) return true;
            if (TryRestoreSlotsVerified(nb, slots, out string wait))
            {
                ApplyState(nb, slots, _hostRgd, _hostPaint, _hostText, player);
                WatchRestore(nb, slots);
                _hostBaseVerified = true;
                return true;
            }
            if (wait != _hostRestoreWait) { Trace($"restore wait: {wait}"); _hostRestoreWait = wait; }
            if (Time.realtimeSinceStartup <= _hostVerifyDeadlineTime) return false; // retry next frame
            Warn($"restore never verified ({wait}) - reverting the move, original kept.");
            HostVerifyDeadline(delta);
            return false;
        }

        // GROUP MOVE: anything resting on the original (decor on a table, a stack) would be
        // cascaded away when we remove it. Detect that exact cascade set with the game's own
        // predicate - with the original's colliders disabled (as they've been all carry) a block
        // it supported reads !IsStable() - then re-create each on the moved block by the same rigid
        // A->A' offset and replay its state. Atomic: if any piece has state we can't carry or a
        // re-create fails, undo EVERYTHING (discard nb + new pieces, restore the original) so the
        // stack is never half-moved or lost. False = move undone, refusal sent, state reset.
        private static bool MoveDependentsOnce(Block nb, Block original, Network_Player player)
        {
            if (_hostDepsMoved) return true;
            if (!TryMoveDependents(original, nb, player, out string depFail))
            {
                UndoHostMove(nb);
                Note(depFail);
                if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, original != null ? original.ObjectIndex : 0u, depFail);
                ResetHostVerify();
                return false;
            }
            _hostDepsMoved = true;
            return true;
        }

        // PHASE B - dependent storages (a chest in the carried stack) go through the SAME read-back
        // gate before any original is removed. Entries that needed a retry get their content-sync
        // re-sent (the one ApplyState sent may have carried an empty inventory). False = done for
        // this frame (retrying, or the whole move reverted on deadline).
        private static bool VerifyDepRestores(Block nb, Block original, Network_Player player)
        {
            string depWait = null;
            foreach (var pr in _pendingDepRestores)
            {
                if (pr.Nd == null) continue;
                if (!TryRestoreSlotsVerified(pr.Nd, pr.Slots, out string w)) { pr.Retried = true; depWait = pr.Nd.name + ": " + w; break; }
            }
            if (depWait != null)
            {
                if (depWait != _hostRestoreWait) { Trace($"dep restore wait: {depWait}"); _hostRestoreWait = depWait; }
                if (Time.realtimeSinceStartup <= _hostVerifyDeadlineTime) return false; // retry next frame
                Warn($"dep restore never verified ({depWait}) - reverting the whole move.");
                UndoHostMove(nb);
                if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, original != null ? original.ObjectIndex : 0u, "r_move_failed");
                ResetHostVerify();
                return false;
            }
            foreach (var pr in _pendingDepRestores)
            {
                if (pr.Retried && pr.Nd is Storage_Small prs) SendStorageSync(prs, player);
                WatchRestore(pr.Nd, pr.Slots);
            }
            _pendingDepRestores.Clear();
            return true;
        }

        private static void PollHostVerify()
        {
            if (_hostNb == null) { HostVerifyVanished(); return; }

            int delta = Time.frameCount - _hostVerifyStart;
            bool stable = false;
            try { stable = _hostNb.IsStable(); } catch { }

            int sv = stable ? 1 : 0;
            if (sv != _hostLastLoggedStable) { Trace($"settle: frame+{delta} nbStable={stable}"); _hostLastLoggedStable = sv; }

            if (stable)
            {
                var nb = _hostNb;
                var original = _hostOriginal;
                var slots = _hostSlots;
                var player = ComponentManager<Network_Player>.Value;
                if (!VerifyBaseRestore(nb, slots, player, delta)) return;
                if (!MoveDependentsOnce(nb, original, player)) return;
                if (!VerifyDepRestores(nb, original, player)) return;
                FinishHostMove(nb, original, slots, player, delta);
                return;
            }

            if (Time.realtimeSinceStartup > _hostVerifyDeadlineTime) HostVerifyDeadline(delta);
        }
    }
}
