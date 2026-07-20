using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Same-variant teleport move: transform-as-state, pipe lifecycle replay, peer notifies.
    public partial class Plugin
    {
        // ---- TELEPORT MOVE (same-surface) ----------------------------------------------------
        // Move the EXISTING block by setting its transform - nothing is removed or recreated, so no
        // state can possibly be lost (same ObjectIndex, same inventory, same paint, same plants) and
        // there is no create/remove replication to lose on a flaky link (the zombie/dupe class dies).
        // Position is STATE, not an event: the notify packet (type 7) is idempotent and is re-sent a
        // couple of times; even a total loss self-heals via the probe (which now carries the current
        // transform). Only a surface change (Floor->Wall = different prefab variant) still uses the
        // old recreate pipeline. RGD_Block reads block.transform at save time (RGD_Block ctor:300),
        // so saves are correct; placeables sit in no position-keyed cache (walkable-grid is structure).
        private static Block _tpBlock;
        private static Vector3 _tpOldPos, _tpOldRot;
        private static readonly List<Block> _tpDeps = new List<Block>();
        private static readonly List<Vector3> _tpDepsOldPos = new List<Vector3>();
        private static readonly List<Quaternion> _tpDepsOldRot = new List<Quaternion>();
        private static bool _tpVerifying;
        private static int _tpVerifyStart;
        private static float _tpVerifyDeadlineTime;
        private static Steamworks.CSteamID _tpReqSender;
        private sealed class TpSend { public Steamworks.CSteamID To; public byte[] Payload; public float Next; public int Left; }
        private static readonly List<TpSend> _tpSends = new List<TpSend>();

        // Teleport is only legal when the target placement uses the SAME prefab variant as the
        // existing block (a Floor->Wall move instantiates a different prefab - holder, gizmos - and
        // must go through the recreate pipeline). Compare by prefab name: the live block is
        // 'PrefabName(Clone)'; enum dpsType on the variant prefab is not a reliable discriminator
        // (a wall chest re-placed on the same wall can report a mismatched dps).
        private static bool SameVariant(Block original, Item_Base item, DPS dps)
        {
            try
            {
                if (original == null) { Warn("SameVariant: original is null/destroyed -> false"); return false; }
                var reqPrefab = item?.settings_buildable?.GetBlockPrefab(dps);
                // reqPrefab == null => the item has no distinct prefab for this dps, i.e. it is a
                // single-variant placeable (BatteryCharger, most devices: one floor prefab, dpsType
                // Default/None). GetBlockPrefab is keyed on surface enums it doesn't register, so
                // the round-trip GetBlockPrefab(ghost.dpsType) returns null. There is no OTHER
                // variant to recreate into -> same variant by definition -> teleport. Falling to
                // 'return false' here (silently) would make HasUnhandledState refuse every such
                // device with 'surface' forever - hence the explicit branch and log line.
                if (reqPrefab == null) { Note($"[t] '{original.name}' has no variant prefab for dps={dps} -> single-variant, teleport"); return true; }
                string origBase = original.name.Replace("(Clone)", "").Trim();
                bool same = origBase == reqPrefab.name;
                if (!same) Note($"[t] variant differs: orig='{origBase}' req='{reqPrefab.name}' (dps={dps}, origDps={original.dpsType}) -> recreate path");
                return same;
            }
            // a silent 'catch -> false' here once ate the only evidence of a refusal bug (every
            // legit input returned true; only a swallowed throw could refuse) - never mute this
            // catch again
            catch (System.Exception ex) { Warn($"SameVariant threw for '{original?.name}' dps={dps}: {ex}"); return false; }
        }

        private static byte[] BuildTeleportPayload(uint idx, Vector3 pos, Vector3 rot)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(MoveMagic); w.Write((byte)7); w.Write(idx);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            w.Write(rot.x); w.Write(rot.y); w.Write(rot.z);
            return ms.ToArray();
        }

        private static void SendTeleport(Steamworks.CSteamID to, uint idx, Vector3 pos, Vector3 rot)
        {
            QueueModSend(to, BuildTeleportPayload(idx, pos, rot));
        }

        // Fire-and-forget for any idempotent mod-channel payload (teleport kind 7, paint kind 9,
        // move-done kind 10): send now, then repeat twice against session drops. Safe ONLY for
        // notifies whose re-apply is a no-op; request/ack kinds must NOT go through here.
        // payload layout: magic(4) kind(1) idx(4) ...
        private static (byte kind, uint idx) PayloadKindIdx(byte[] p)
            => (p == null || p.Length < 9) ? ((byte)0, 0u) : (p[4], System.BitConverter.ToUInt32(p, 5));

        private static void QueueModSend(Steamworks.CSteamID to, byte[] payload)
        {
            if (!to.IsValid()) return;
            try { Steamworks.SteamNetworking.SendP2PPacket(to, payload, (uint)payload.Length, Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel); }
            catch (System.Exception ex) { Warn("mod-channel send: " + ex.Message); }
            // R4: keep ONE queued resend per (peer, kind, idx) - the newest payload replaces an older
            // queued one. ProcessTeleportResends walks _tpSends BACKWARD, so two entries for the same
            // key maturing in one frame (a 2-moves-in-one-hitch burst) would re-send newest->oldest
            // and leave the ordered reliable channel ending on the STALE position (permanent once
            // Left hits 0). Deduping the queue removes that ordering hazard entirely.
            var (k, idx) = PayloadKindIdx(payload);
            for (int i = 0; i < _tpSends.Count; i++)
            {
                var e = _tpSends[i];
                if (e.To.m_SteamID != to.m_SteamID) continue;
                var (ek, ei) = PayloadKindIdx(e.Payload);
                if (ek != k || ei != idx) continue;
                e.Payload = payload; e.Next = Time.realtimeSinceStartup + 1f; e.Left = 2;
                return;
            }
            _tpSends.Add(new TpSend { To = to, Payload = payload, Next = Time.realtimeSinceStartup + 1f, Left = 2 });
        }

        private static void ProcessTeleportResends()
        {
            for (int i = _tpSends.Count - 1; i >= 0; i--)
            {
                var s = _tpSends[i];
                if (Time.realtimeSinceStartup < s.Next) continue;
                try { Steamworks.SteamNetworking.SendP2PPacket(s.To, s.Payload, (uint)s.Payload.Length, Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel); } catch { }
                s.Left--; s.Next = Time.realtimeSinceStartup + 2f;
                if (s.Left <= 0) _tpSends.RemoveAt(i);
            }
        }

        // Real Steam ids of remote peers, learned from ReadP2PPacket senders (move channel + log
        // relay - the relay batches keep this fresh the whole session). This is the only
        // trustworthy address source: vanilla 'SendP2P' is PlayFab Party, not Steam -
        // Network_UserId/remoteUsers keys are PlayFab EntityKey ids (hex), so casting one to
        // CSteamID addresses nobody and the packet vanishes silently. Packets sent to a real
        // ReadP2PPacket sender id always arrive. (Raft_Network.cs:1858 SendP2P resolves
        // Network_UserId against PlayFabMultiplayerManager.RemotePlayers.)
        private static readonly Dictionary<ulong, float> _knownPeers = new Dictionary<ulong, float>();
        internal static void RegisterPeer(Steamworks.CSteamID id)
        {
            if (id.IsValid()) _knownPeers[id.m_SteamID] = Time.realtimeSinceStartup;
        }

        private static void BroadcastTeleport(Block b)
        {
            if (b == null) return;
            if (_tpReqSender.IsValid()) RegisterPeer(_tpReqSender); // requester always covered
            foreach (var sid in _knownPeers.Keys)
                SendTeleport(new Steamworks.CSteamID(sid), b.ObjectIndex, b.transform.localPosition, b.transform.localEulerAngles);
        }

        // HOST: teleport `b` (+ deps found by the flip-test scan, same rigid delta) to pos/rot, then
        // verify support; a spot that never settles is undone by simply putting the transforms back.
        // deps may be null/empty: the block moves alone.
        // Block_Pipe covers pipe SEGMENTS and pipe-network DEVICES - Placeable_BatteryCharger's
        // block component IS Block_Pipe, so excluding pipes wholesale would make the charger
        // unmovable. Such a block wires itself into the world at
        // placement (OnFinishedPlacementLate: TileBitmaskManager.AddTile + Refresh(neighbours) +
        // OnPipePlaced -> fluid-group merge) and unwinds at removal (OnDestroy: OnPipeRemoved +
        // RemoveTile + RefreshNeighbours). A bare transform teleport left the tile registered at the
        // OLD cell and the pipe in the OLD group. Replaying those exact calls around the move makes
        // teleport VALID for pipes again: same object, state (charger batteries/fuel) intact.
        // Vanilla gating mirrored: tiles on ALL sides, OnPipePlaced/Removed host-only. The
        // OnMeshChange/OnCompleteVisual handlers stay subscribed - the BitmaskTile instance is never
        // destroyed - so no double-wiring. OnPipePlaced re-reads transform.position, so call it AFTER
        // the transform is set (and OnPipeRemoved BEFORE, while snappedBuildingPosition still matches).
        private static void PipeLifecycle(Block b, bool place)
        {
            var bp = b as Block_Pipe;
            if (bp == null) return;
            try
            {
                var cons = Traverse.Create(bp).Field("pipeBitmaskConnections").GetValue<Block_PipeBitmask[]>();
                if (cons == null) return;
                foreach (var con in cons)
                {
                    if (con == null) continue;
                    if (!place)
                    {
                        if (Raft_Network.IsHost && con.pipe != null) con.pipe.OnPipeRemoved();
                        if (con.bitmaskTile != null)
                        { TileBitmaskManager.RemoveTile(con.bitmaskTile); con.bitmaskTile.RefreshNeighbours(); }
                    }
                    else
                    {
                        if (con.bitmaskTile != null)
                        { TileBitmaskManager.AddTile(con.bitmaskTile); con.bitmaskTile.Refresh(refreshNeighbour: true); }
                        if (Raft_Network.IsHost && con.pipe != null) con.pipe.OnPipePlaced();
                    }
                }
            }
            catch (System.Exception ex) { Warn($"pipe lifecycle ({(place ? "place" : "remove")}) '{b.name}': {ex.Message}"); }
        }

        // ---- ANTENNA WIRE RE-LAY ---------------------------------------------------------
        // Vanilla lays the reciever<->antenna wire ONCE, in Reciever_Antenna.ConnectToReciever:
        //   wire.SetPosition(0, antenna.transform.position);
        //   wire.SetPosition(1, reciever.sockets[antenna.socketNumber].position);
        // World points, baked at connect (decomp Reciever_Antenna). Vanilla never moves placed
        // blocks, so it never re-lays - move a reciever (or a table carrying one as a dep) and
        // the wires stay at the old spot. Fix: re-issue the same two calls after every transform
        // change (teleport apply, rollback, remote notify, probe-sync). The RECREATE path needs
        // nothing: OnDestroy disconnects, the fresh block reconnects itself in Update.
        private static readonly AccessTools.FieldRef<Reciever_Antenna, Rope> _antWire =
            AccessTools.FieldRefAccess<Reciever_Antenna, Rope>("wire");
        private static readonly AccessTools.FieldRef<Reciever_Antenna, Reciever> _antRecv =
            AccessTools.FieldRefAccess<Reciever_Antenna, Reciever>("reciever");

        internal static void RefreshWires(Block b)
        {
            if (b == null) return;
            try
            {
                var recv = b.GetComponent<Reciever>();
                if (recv != null && recv.antennas != null)
                    foreach (var ant in recv.antennas) ReLayWire(ant);
                ReLayWire(b.GetComponent<Reciever_Antenna>());
            }
            catch (System.Exception ex) { Warn($"antenna wire refresh '{b.name}': {ex.Message}"); }
            try
            {
                foreach (var mpb in b.GetComponentsInChildren<MeshPathBase>(true)) ReLayZiplines(mpb);
            }
            catch (System.Exception ex) { Warn($"zipline refresh '{b.name}': {ex.Message}"); }
        }

        // Zipline rope = MeshPath: mesh + collider generated ONCE from world connectPoints
        // (decomp MeshPath.CreateMeshPath), never refreshed - same disease as antenna wires.
        // Re-issue vanilla's own CreatePath with the stored slack; replicating:true mutes the
        // creation SFX. Deterministic from world positions, so every peer converges by itself.
        internal static void ReLayZiplines(MeshPathBase mpb)
        {
            if (mpb == null || MeshPath.PathConnections == null) return;
            foreach (var conn in MeshPath.PathConnections)
            {
                if (conn == null || !conn.IsValid()) continue;
                if (conn.baseA != mpb && conn.baseB != mpb) continue;
                ReLayPath(conn.path, conn.baseA.connectPoint.position, conn.baseB.connectPoint.position);
            }
        }

        // CreatePath allocates two fresh Meshes (rope visual + ride collider) and orphans the old
        // ones every call - fine once, a leak when the live rope preview calls it every frame.
        // Destroy the previous meshes explicitly (they are runtime-generated, never prefab assets).
        private static readonly AccessTools.FieldRef<MeshPath, MeshFilter> _mpMeshFilter =
            AccessTools.FieldRefAccess<MeshPath, MeshFilter>("meshFilter");

        // withCollider=false: preview mode - the ride collider would land ON the ghost's connect
        // point and fail CanBuildBlock (red ghost, observed), so it is disabled while previewing
        // and re-enabled by the final real re-lay.
        internal static void ReLayPath(MeshPath path, Vector3 a, Vector3 b, bool withCollider = true)
        {
            if (path == null) return;
            var mf = _mpMeshFilter(path);
            var col = mf != null ? mf.GetComponentInChildren<MeshCollider>() : null;
            Mesh oldVis = mf != null ? mf.sharedMesh : null;
            Mesh oldCol = col != null ? col.sharedMesh : null;
            path.CreatePath(a, b, path.currentSlack, replicating: true);
            if (oldVis != null && mf.sharedMesh != oldVis) Object.Destroy(oldVis);
            if (oldCol != null && col != null && col.sharedMesh != oldCol) Object.Destroy(oldCol);
            if (col != null) col.enabled = withCollider;
        }

        private static void ReLayWire(Reciever_Antenna ant)
        {
            if (ant == null) return;
            var recv = _antRecv(ant);
            if (recv == null) return;                 // not connected - wire is inactive
            var wire = _antWire(ant);
            if (wire == null || recv.sockets == null) return;
            if (ant.socketNumber < 0 || ant.socketNumber >= recv.sockets.Length) return;
            wire.SetPosition(0, ant.transform.position);
            wire.SetPosition(1, recv.sockets[ant.socketNumber].position);
        }

        private static void BeginTeleport(Block b, Vector3 pos, Vector3 rot, Steamworks.CSteamID reqSender, List<Block> deps)
        {
            _tpBlock = b; _tpOldPos = b.transform.localPosition; _tpOldRot = b.transform.localEulerAngles;
            _tpReqSender = reqSender;
            _tpDeps.Clear(); _tpDepsOldPos.Clear(); _tpDepsOldRot.Clear();

            var dR = Quaternion.Euler(rot) * Quaternion.Inverse(Quaternion.Euler(_tpOldRot));
            PipeLifecycle(b, place: false);
            if (deps != null) foreach (var d in deps)
            {
                if (d == null) continue;
                _tpDeps.Add(d);
                _tpDepsOldPos.Add(d.transform.localPosition);
                _tpDepsOldRot.Add(d.transform.localRotation);
                PipeLifecycle(d, place: false);
                d.transform.localPosition = pos + dR * (d.transform.localPosition - _tpOldPos);
                d.transform.localRotation = dR * d.transform.localRotation;
                PipeLifecycle(d, place: true);
            }
            b.transform.localPosition = pos;
            b.transform.localEulerAngles = rot;
            PipeLifecycle(b, place: true);
            Physics.SyncTransforms();
            RefreshWires(b);
            foreach (var d in _tpDeps) RefreshWires(d);
            _tpVerifyStart = Time.frameCount;
            _tpVerifyDeadlineTime = Time.realtimeSinceStartup + 6f;
            _tpVerifying = true;
        }

        private static void PollTeleportVerify()
        {
            if (_tpBlock == null) { _tpVerifying = false; _tpDeps.Clear(); _tpReqSender = default; return; }
            bool stable = false;
            try { stable = _tpBlock.IsStable(); } catch { }
            if (stable)
            {
                int delta = Time.frameCount - _tpVerifyStart;
                // moved-epoch: stamp the commit so any request RECEIVED before this moment (aimed at
                // the pre-move world) is refused instead of silently re-moving the block (see
                // HandleMoveRequest). Deps moved along get stamped too - same reasoning.
                _movedAt[_tpBlock.ObjectIndex] = Time.realtimeSinceStartup;
                foreach (var d in _tpDeps) if (d != null) _movedAt[d.ObjectIndex] = Time.realtimeSinceStartup;
                BroadcastTeleport(_tpBlock);
                foreach (var d in _tpDeps) if (d != null) BroadcastTeleport(d);
                Note($"moved to {_tpBlock.transform.localPosition.ToString("F2")} (+{delta}f, teleport)"
                    + (_tpDeps.Count > 0 ? $"; carried {_tpDeps.Count} on top" : ""));
                _tpBlock = null; _tpDeps.Clear(); _tpDepsOldPos.Clear(); _tpDepsOldRot.Clear();
                _tpVerifying = false; _tpReqSender = default;
                return;
            }
            if (Time.realtimeSinceStartup > _tpVerifyDeadlineTime)
            {
                LogStability(_tpBlock);
                for (int i = 0; i < _tpDeps.Count; i++)
                    if (_tpDeps[i] != null)
                    {
                        PipeLifecycle(_tpDeps[i], place: false);
                        _tpDeps[i].transform.localPosition = _tpDepsOldPos[i]; _tpDeps[i].transform.localRotation = _tpDepsOldRot[i];
                        PipeLifecycle(_tpDeps[i], place: true);
                    }
                PipeLifecycle(_tpBlock, place: false);
                _tpBlock.transform.localPosition = _tpOldPos;
                _tpBlock.transform.localEulerAngles = _tpOldRot;
                PipeLifecycle(_tpBlock, place: true);
                Physics.SyncTransforms();
                RefreshWires(_tpBlock);
                foreach (var d in _tpDeps) RefreshWires(d);
                NoteHud(Loc.T("no_support"));
                if (_tpReqSender.IsValid()) SendMoveRefusal(_tpReqSender, _tpBlock.ObjectIndex, "no_support");
                _tpBlock = null; _tpDeps.Clear(); _tpDepsOldPos.Clear(); _tpDepsOldRot.Clear();
                _tpVerifying = false; _tpReqSender = default;
            }
        }
    }
}
