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
        private static Vector3 _clientMovePos;
        private static bool _clientMoveRestored;
        // [t] timing probes (first-move-of-type can take seconds on the host) + refusal-relay state
        private static float _cmSentTime;          // Time.realtimeSinceStartup when the request left
        private static bool _cmOrigGoneLogged;
        private static bool _cmSeenLogged;
        private static Steamworks.CSteamID _hostReqSender; // valid = current verify is a client request
        private static float _hostReqRecvTime;
        private static readonly HashSet<uint> _preExisting = new HashSet<uint>();
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
                                _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
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
                            }
                            if (mine) // our pending move completed as a teleport - un-hide the SAME object at its new spot
                            {
                                RestoreHidden();
                                _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                                _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
                                Note($"[t] teleported {Time.realtimeSinceStartup - _cmSentTime:F2}s after request");
                                Note("client: host moved it.");
                            }
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
                                    }
                                    catch { }
                                    RestoreHidden();
                                    _pendingClientMoveOriginal = null; _awaitingHostMove = false;
                                    _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
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

        private sealed class MoveReq { public byte[] Buf; public Steamworks.CSteamID Sender; public float RecvTime; }
        private static readonly Queue<MoveReq> _moveReqQueue = new Queue<MoveReq>();
        // origIndexes whose client canceled the request before we started it (packet types: 1=request,
        // 2=refusal, 3=ack, 4=cancel, 5=probe, 6=probe-reply). A newer request for the same index
        // supersedes an older cancel (per-peer FIFO ordering on the reliable channel guarantees the
        // cancel always drains before the retry).
        private static readonly HashSet<uint> _canceledReqs = new HashSet<uint>();
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
                            _canceledReqs.Remove(idx); // a retry supersedes any older cancel
                            SendMoveCtl(sender, 3, idx);
                            _moveReqQueue.Enqueue(new MoveReq { Buf = buf, Sender = sender, RecvTime = Time.realtimeSinceStartup });
                            break;
                        case 4: // cancel: drop the request if we haven't started it
                            _canceledReqs.Add(idx);
                            Note($"[t] client canceled move request for block #{idx}");
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
                uint reqIdx = 0;
                try { using var r = new BinaryReader(new MemoryStream(req.Buf)); r.ReadUInt32(); r.ReadByte(); reqIdx = r.ReadUInt32(); } catch { }
                if (_canceledReqs.Remove(reqIdx)) { Note($"[t] skipped canceled move request #{reqIdx}"); continue; }
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
            if (original == null) { SendMoveRefusal(req.Sender, origIndex, "r_not_found"); return; }
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

            _hostNb = nb; _hostOriginal = original; _hostSlots = slots; _hostText = text; _hostRgd = rgd; _hostPaint = paint;
            _hostReqSender = req.Sender; _hostReqRecvTime = req.RecvTime;
            _hostVerifyStart = Time.frameCount; _hostVerifyDeadlineTime = Time.realtimeSinceStartup + 6f;
            _hostLastLoggedStable = -1;
            _hostBaseVerified = false; _hostDepsMoved = false; _hostRestoreWait = null; _pendingDepRestores.Clear();
            _hostVerifying = true;
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
                        _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
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
                        _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
                        NoteHud(Loc.T("no_answer"));
                    }
                }
                return;
            }

            // PHASE 2: original is gone - find the freshly-replicated block near where we placed the ghost
            // and restore our local view on it.
            if (!_clientMoveRestored)
            {
                if (!_cmOrigGoneLogged) { Note($"[t] original removed {Time.realtimeSinceStartup - _cmSentTime:F2}s after request"); _cmOrigGoneLogged = true; }
                Block best = null; float bestSqr = 1f; // within ~1 unit of where we placed the ghost
                foreach (var b in BlockCreator.GetPlacedBlocks())
                {
                    if (b == null || _preExisting.Contains(b.ObjectIndex)) continue;
                    float d = (b.transform.localPosition - _clientMovePos).sqrMagnitude;
                    if (d < bestSqr) { bestSqr = d; best = b; }
                }
                if (best != null && !_cmSeenLogged) { Note($"[t] new block first seen {Time.realtimeSinceStartup - _cmSentTime:F2}s after request (stable={best.IsStable()})"); _cmSeenLogged = true; }
                bool graceOver = Time.frameCount > _clientMoveDeadlineFrame + 180;
                if (best != null && (best.IsStable() || graceOver))
                {
                    // grace expiring with the block present but never reading stable: restore anyway -
                    // silently dropping the restore here is what lost paint/contents on the client's view.
                    if (!best.IsStable()) Warn("client: new block never read stable locally; restoring its state anyway.");
                    // storage restore gate, client view: wait for the final inventory + read back
                    // before calling it restored; keep polling until the grace runs out, then apply loudly.
                    if (!TryRestoreSlotsVerified(best, _clientMoveSlots, out string cw) && !graceOver)
                    {
                        if (cw != _hostRestoreWait) { Trace($"client restore wait: {cw}"); _hostRestoreWait = cw; }
                        return; // retry next tick
                    }
                    if (cw != null) Warn($"client: storage restore unverified ({cw}); applying anyway.");
                    var player = ComponentManager<Network_Player>.Value;
                    try { ApplyState(best, _clientMoveSlots, _clientMoveRgd, _clientMovePaint, _clientMoveText, player); WatchRestore(best, _clientMoveSlots); }
                    catch (System.Exception ex) { Warn("client local restore: " + ex.Message); }
                    _clientMoveRestored = true;
                }
                else if (!graceOver)
                {
                    return; // grace: give the new block a moment to arrive/settle
                }
                else
                {
                    // the host removed the original (phase 1 passed) but no new block ever showed up
                    // near our ghost position - say so instead of claiming success.
                    Warn($"client: original removed but no new block appeared within 1m of {_clientMovePos.ToString("F2")}; local state not re-applied.");
                }
            }

            _hiddenColliders.Clear(); _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
            _clientMoveRgd = null; _clientMoveSlots = null; _clientMoveText = null;
            _awaitingHostMove = false;
            Note($"[t] client move total {Time.realtimeSinceStartup - _cmSentTime:F2}s (request -> restored)");
            Note("client: host moved it.");
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

        private static void PollHostVerify()
        {
            if (_hostNb == null)
            {
                RestoreHidden();
                if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, _hostOriginal != null ? _hostOriginal.ObjectIndex : 0u, "r_move_failed");
                ResetHostVerify();
                Warn("host verify: placed chest vanished before settling; original restored, nothing lost.");
                return;
            }

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

                // PHASE A - the moved block's own state, slots read-back verified (storage restore
                // gate): wait for vanilla's OnFinishedPlacement, write, read back. Until verified
                // the original is untouched, so the deadline revert below loses nothing.
                if (!_hostBaseVerified)
                {
                    if (!TryRestoreSlotsVerified(nb, slots, out string wait))
                    {
                        if (wait != _hostRestoreWait) { Trace($"restore wait: {wait}"); _hostRestoreWait = wait; }
                        if (Time.realtimeSinceStartup <= _hostVerifyDeadlineTime) return; // retry next frame
                        Warn($"restore never verified ({wait}) - reverting the move, original kept.");
                        stable = false; // fall through to the deadline revert below
                    }
                    else
                    {
                        ApplyState(nb, slots, _hostRgd, _hostPaint, _hostText, player);
                        WatchRestore(nb, slots);
                        _hostBaseVerified = true;
                    }
                }

                if (stable)
                {

                // GROUP MOVE: anything resting on the original (decor on a table, a stack) would be
                // cascaded away when we remove it. Detect that exact cascade set with the game's own
                // predicate - with the original's colliders disabled (as they've been all carry) a block
                // it supported reads !IsStable() - then re-create each on the moved block by the same rigid
                // A->A' offset and replay its state. Atomic: if any piece has state we can't carry or a
                // re-create fails, undo EVERYTHING (discard nb + new pieces, restore the original) so the
                // stack is never half-moved or lost.
                if (!_hostDepsMoved)
                {
                    if (!TryMoveDependents(original, nb, player, out string depFail))
                    {
                        DiscardNewDependents();
                        RestoreDependentColliders();
                        _plantBroadcastPlots.Clear(); // undone move - nothing to announce
                        DeregisterCropplotPlants(nb); // its restored plants are registered - don't shadow the surviving original
                        try { BlockCreator.RemoveBlockNetwork(nb, null, true); } catch { }
                        RestoreHidden();
                        Note(depFail);
                        if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, original != null ? original.ObjectIndex : 0u, depFail);
                        ResetHostVerify();
                        return;
                    }
                    _hostDepsMoved = true;
                }

                // PHASE B - dependent storages (a chest in the carried stack) go through the SAME
                // read-back gate before any original is removed. Entries that needed a retry get
                // their content-sync re-sent (the one ApplyState sent may have carried an empty
                // inventory). Deadline -> full undo, stack intact.
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
                        if (Time.realtimeSinceStartup <= _hostVerifyDeadlineTime) return; // retry next frame
                        Warn($"dep restore never verified ({depWait}) - reverting the whole move.");
                        DiscardNewDependents();
                        RestoreDependentColliders();
                        _plantBroadcastPlots.Clear();
                        DeregisterCropplotPlants(nb);
                        try { BlockCreator.RemoveBlockNetwork(nb, null, true); } catch { }
                        RestoreHidden();
                        if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, original != null ? original.ObjectIndex : 0u, "r_move_failed");
                        ResetHostVerify();
                        return;
                    }
                    foreach (var pr in _pendingDepRestores)
                    {
                        if (pr.Retried && pr.Nd is Storage_Small prs) SendStorageSync(prs, player);
                        WatchRestore(pr.Nd, pr.Slots);
                    }
                    _pendingDepRestores.Clear();
                }

                _hiddenColliders.Clear();
                _hiddenRenderers.Clear();
                _hiddenCanvases.Clear();
                // remove originals: dependents first (their colliders were disabled during detection),
                // then the base block - nothing rests on it now, so no cascade victims.
                foreach (var d in _depOriginals) if (d != null) { DeregisterCropplotPlants(d); try { BlockCreator.RemoveBlockNetwork(d, null, true); } catch { } }
                _depOriginals.Clear(); _depColliderDisabled.Clear(); _newDependents.Clear();
                if (original != null) { DeregisterCropplotPlants(original); try { BlockCreator.RemoveBlockNetwork(original, null, true); } catch { } }

                // originals are gone on every peer (reliable ordered channel) - now announce the moved
                // plants so clients recreate them through the game's own planting path (harvest-linked).
                FlushPlantBroadcasts(player);

                int restored = slots?.Length ?? 0;
                Note($"placed at {nb.transform.localPosition.ToString("F2")} after settling (+{delta}f, host)"
                    + (restored > 0 ? $"; restored {restored} slots" : "")
                    + (_depMovedCount > 0 ? $"; moved {_depMovedCount} on top" : ""));
                if (_hostReqSender.IsValid()) Note($"[t] client move total {Time.realtimeSinceStartup - _hostReqRecvTime:F2}s (recv -> original removed)");
                ResetHostVerify();
                return;
                }
            }

            if (Time.realtimeSinceStartup > _hostVerifyDeadlineTime)
            {
                _plantBroadcastPlots.Clear(); // undone move - nothing to announce
                DeregisterCropplotPlants(_hostNb); // undo path: the new block's registered plants must not shadow the original
                try { BlockCreator.RemoveBlockNetwork(_hostNb, null, true); } catch { }
                RestoreHidden();
                LogStability(_hostNb); // which gizmo cell(s) never found support
                Note("host place fail at +" + delta + "f"); NoteHud(Loc.T("no_support"));
                if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, _hostOriginal != null ? _hostOriginal.ObjectIndex : 0u, "no_support");
                ResetHostVerify();
            }
        }
    }
}
