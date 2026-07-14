using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Dependent detection (what rests on a block) and the group move.
    public partial class Plugin
    {
        // ---- group move (a stack of placeables resting on the moved block) ----
        // One captured piece: its buildable + full state + transform RELATIVE to the base block, so it
        // can be re-placed by the same rigid base->base' offset (the whole stack moves as one body).
        private sealed class Carried
        {
            public Item_Base Item;
            public DPS Dps;
            public RGD_Slot[] Slots;
            public string Text;
            public RGD_Block Rgd;
            public Paint Paint;
            public Block Original;
            public Vector3 LocalPos;     // position in the base block's local frame (raft-local)
            public Quaternion LocalRot;  // rotation relative to the base block (raft-local)
        }

        private static readonly List<Block> _depOriginals = new List<Block>();
        private static readonly List<Block> _newDependents = new List<Block>();
        private static readonly List<Collider> _depColliderDisabled = new List<Collider>();
        private static int _depMovedCount;
        // deps of the currently carried block (filled by the pickup dep-scan; teleported along at placement)
        private static readonly List<Block> _carryDeps = new List<Block>();

        private static Carried Capture(Block b, Block relativeTo)
        {
            var s = (b is Storage_Small ss && ss.GetInventoryReference() != null) ? ss.GetInventoryReference().GetRGDSlots() : null;
            return new Carried
            {
                Item = b.buildableItem,
                Dps = b.dpsType,
                Slots = s,
                Text = TryGetSignText(b),
                Rgd = TryCaptureRgd(b),
                Paint = CapturePaint(b),
                Original = b,
                // express the piece RELATIVE to the base block in raft-local space, so re-placing it by
                // the base's new local pos/rot moves the whole stack as one rigid body (CreateBlockCheat
                // takes raft-local position + euler, like ghost.localPosition/localEulerAngles).
                LocalPos = Quaternion.Inverse(relativeTo.transform.localRotation) * (b.transform.localPosition - relativeTo.transform.localPosition),
                LocalRot = Quaternion.Inverse(relativeTo.transform.localRotation) * b.transform.localRotation,
            };
        }

        // Detect the cascade tree of `original` (what removing it would destroy) and re-create each piece
        // on `nb` at the same rigid offset, replaying its state. Returns false (with a reason) if any
        // piece has unhandled state or can't be re-created - the caller then undoes the whole swap.
        // Populates _depOriginals (to remove on success), _newDependents (to discard on abort) and
        // _depColliderDisabled (to re-enable on abort).
        private static bool TryMoveDependents(Block original, Block nb, Network_Player player, out string failMsg)
        {
            failMsg = null;
            _depOriginals.Clear(); _newDependents.Clear(); _depColliderDisabled.Clear(); _depMovedCount = 0;
            if (original == null || nb == null) return true;
            var bc = player?.BlockCreator;
            if (bc == null) return true;

            // ignore nb's own colliders during detection so a moved-block placed near its old spot can't
            // "support" a dependent and hide it from us (which would let it cascade = data loss).
            var nbDisabled = new List<Collider>();
            foreach (var c in nb.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; nbDisabled.Add(c); }
            try
            {
                var captured = new List<Carried>();
                // frontier = blocks whose colliders are OFF (original is already hidden from carry). For each,
                // any nearby block now reading !IsStable() rested on it -> capture it, turn ITS colliders off
                // to expose deeper pieces. Mirrors DestroyBlock's recursive cascade exactly.
                var frontier = new List<Block> { original };
                for (int i = 0; i < frontier.Count && captured.Count < 128; i++)
                {
                    var hb = frontier[i];
                    float dist = hb.buildableItem?.settings_buildable != null ? hb.buildableItem.settings_buildable.UnstableCheckDistance : 3.5f;
                    foreach (var pb in BlockCreator.GetPlacedBlocks())
                    {
                        if (pb == null || pb == original || pb == nb || frontier.Contains(pb)) continue;
                        if (captured.Exists(c => c.Original == pb)) continue;
                        if (Vector3.Distance(pb.transform.position, hb.transform.position) > dist) continue;
                        if (pb.IsStable()) continue;                       // still supported elsewhere - not ours
                        var pbi = pb.buildableItem;
                        // [dep] diag: GENUINE dependent (stable again once the original's colliders are
                        // restored) vs ALREADY unstable before our move (chronic neighbour - suspected
                        // recycler false-positive: a fence whose gizmo only clips the big collider).
                        bool preUnstable = false;
                        try
                        {
                            var restored = new List<Collider>();
                            foreach (var hc in _hiddenColliders) if (hc != null && !hc.enabled) { hc.enabled = true; restored.Add(hc); }
                            foreach (var dc in _depColliderDisabled) if (dc != null && !dc.enabled) { dc.enabled = true; restored.Add(dc); }
                            preUnstable = !pb.IsStable();
                            foreach (var rc in restored) rc.enabled = false;
                        }
                        catch { }
                        Note($"[dep] '{(pbi != null ? pbi.UniqueName : pb.name)}' reads unstable near '{(hb.buildableItem != null ? hb.buildableItem.UniqueName : hb.name)}'"
                            + $" dist={Vector3.Distance(pb.transform.position, hb.transform.position):F2} preUnstable={preUnstable} pos={pb.transform.localPosition.ToString("F2")}");
                        // ALREADY unstable before this move (chronic neighbour - e.g. a fence on a window
                        // whose gizmo never finds support): not resting on us, not our business. Refusing
                        // for these locked whole areas of the raft out of moving (observed:
                        // Block_Wall_Fence_Tier3 vetoing every chest move near it).
                        if (preUnstable) continue;
                        // STRUCTURE can never rest on a placeable (the game won't let you build a wall or
                        // fence ON a chest), so a non-Placeable block here is a false positive of the
                        // overlap heuristic - skip it instead of vetoing the move.
                        if (pbi?.settings_buildable == null || !pbi.settings_buildable.Placeable) continue;
                        if (HasUnhandledState(pb))
                        { failMsg = $"Didn't move - '{pbi.UniqueName}' resting on it has state I can't carry yet."; return false; }
                        captured.Add(Capture(pb, original));
                        frontier.Add(pb);
                        foreach (var c in pb.GetComponentsInChildren<Collider>())
                            if (c.enabled) { c.enabled = false; _depColliderDisabled.Add(c); }
                    }
                }

                foreach (var c in captured)
                {
                    var localPos = nb.transform.localPosition + nb.transform.localRotation * c.LocalPos;
                    var localEuler = (nb.transform.localRotation * c.LocalRot).eulerAngles;
                    Block nd;
                    try { nd = bc.CreateBlockCheat(c.Item, localPos, localEuler, c.Dps, -1); }
                    catch (System.Exception ex) { failMsg = "Didn't move the stack: " + ex.Message; return false; }
                    if (nd == null) { failMsg = "Didn't move the stack - couldn't recreate a piece on top."; return false; }
                    _newDependents.Add(nd);
                    ApplyState(nd, c.Slots, c.Rgd, c.Paint, c.Text, player);
                    // dep storages join the read-back verification queue (storage restore gate);
                    // PollHostVerify won't remove ANY original until every entry verifies.
                    if (nd is Storage_Small && c.Slots != null)
                        _pendingDepRestores.Add(new DepRestore { Nd = nd, Slots = c.Slots });
                    _depOriginals.Add(c.Original);
                }
                _depMovedCount = captured.Count;
                return true;
            }
            catch (System.Exception ex) { failMsg = "Didn't move the stack: " + ex.Message; return false; }
            finally { foreach (var c in nbDisabled) if (c != null) c.enabled = true; }
        }

        // ---- dependent scan: what actually rests on a block -------------------------------------
        // Reading IsStable() on the same frame the colliders were toggled is stale: it sweeps
        // whatever was ALREADY unstable nearby (chronic wall decor, junk from earlier moves) - an
        // early version once 'carried 92 on top' and displaced half the raft. Vanilla DestroyBlock
        // (BlockCreator.cs decompile) does it right: TempActivateCellAndNeighbours -> deactivate
        // the block -> 'yield return null' -> only then read IsStable, recursing per newly-unstable
        // block. This scan copies that frame discipline and adds a flip-test (a dep must have been
        // stable in the pre-toggle snapshot), so pre-existing floaters can never be swept.
        private sealed class DepScan
        {
            public Block B;
            public bool OwnB;          // scan disabled B's colliders itself (request path) -> restores them
            public Vector3 Origin;
            public int WaitUntilFrame;
            public int? Cell;          // BlockCollisionConsolidator temp-activation handle
            public System.Action<DepScan> Done;
            public readonly List<Block> Deps = new List<Block>();
            public readonly Dictionary<Block, bool> Before = new Dictionary<Block, bool>();
            public readonly List<Collider> OffB = new List<Collider>();
            public readonly List<Collider> OffDeps = new List<Collider>();
        }
        private static DepScan _pickupScan;   // local carry: hides the stack + fills _carryDeps
        private static DepScan _reqScan;      // host handling a client request: deps for BeginTeleport

        private static float UnstDist(Block b)
        {
            var sb = b != null && b.buildableItem != null ? b.buildableItem.settings_buildable : null;
            return sb != null ? sb.UnstableCheckDistance : 3.5f;
        }

        // Frame 0. Call while the world is SETTLED - and, for the request path, while b's colliders
        // are still ON (ownB: the scan toggles and restores them). Pickup path calls this right
        // before hiding the original (ownB false - the carry owns those colliders).
        private static DepScan StartDepScan(Block b, bool ownB, System.Action<DepScan> done)
        {
            var s = new DepScan { B = b, OwnB = ownB, Done = done, Origin = b.transform.position };
            try
            {
                var cons = ComponentManager<BlockCollisionConsolidator>.Value;
                if (cons != null) s.Cell = cons.TempActivateCellAndNeighbours(b.transform.position);
            }
            catch { }
            foreach (var pb in BlockCreator.GetPlacedBlocks())
            {
                if (pb == null || pb == b) continue;
                if (Vector3.Distance(pb.transform.position, s.Origin) > 10f) continue;
                var sb = pb.buildableItem != null ? pb.buildableItem.settings_buildable : null;
                if (sb == null || !sb.Placeable) continue; // structure never rests on a placeable
                try { s.Before[pb] = pb.IsStable(); } catch { }
            }
            if (ownB)
                foreach (var c in b.GetComponentsInChildren<Collider>())
                    if (c.enabled) { c.enabled = false; s.OffB.Add(c); }
            s.WaitUntilFrame = Time.frameCount + 1;
            return s;
        }

        // Frames 1..n (from Tick): after each settle frame, a block that WAS stable and now reads
        // !IsStable() within the frontier's UnstableCheckDistance rests on us (directly or via an
        // earlier dep). Each wave disables the new deps' colliders and waits another frame -
        // vanilla's recursion, flattened. A wave with no new flips ends the scan.
        private static void StepDepScan(DepScan s)
        {
            if (s.B == null) { FinishDepScan(s, abort: true); return; }
            if (Time.frameCount < s.WaitUntilFrame) return;
            int found = 0;
            foreach (var kv in s.Before)
            {
                var pb = kv.Key;
                if (!kv.Value || pb == null || pb == s.B || s.Deps.Contains(pb)) continue;
                if (!WithinFrontier(s, pb)) continue;
                bool st = true; try { st = pb.IsStable(); } catch { }
                if (st) continue;
                s.Deps.Add(pb); found++;
                foreach (var c in pb.GetComponentsInChildren<Collider>())
                    if (c.enabled) { c.enabled = false; s.OffDeps.Add(c); }
                if (s.Deps.Count >= 32)
                {
                    Warn($"[dep] scan found 32+ pieces near '{s.B.buildableItem?.UniqueName}' - implausible, moving it alone.");
                    s.Deps.Clear();
                    FinishDepScan(s, abort: false);
                    return;
                }
            }
            if (found > 0) { s.WaitUntilFrame = Time.frameCount + 1; return; } // next wave
            FinishDepScan(s, abort: false);
        }

        private static bool WithinFrontier(DepScan s, Block pb)
        {
            if (s.B != null && Vector3.Distance(pb.transform.position, s.B.transform.position) <= UnstDist(s.B)) return true;
            foreach (var d in s.Deps)
                if (d != null && Vector3.Distance(pb.transform.position, d.transform.position) <= UnstDist(d)) return true;
            return false;
        }

        private static void FinishDepScan(DepScan s, bool abort)
        {
            foreach (var c in s.OffDeps) if (c != null) c.enabled = true;
            foreach (var c in s.OffB) if (c != null) c.enabled = true;
            try { if (s.Cell.HasValue) ComponentManager<BlockCollisionConsolidator>.Value?.RemoveTempActivate(s.Cell.Value); } catch { }
            if (_pickupScan == s) _pickupScan = null;
            if (_reqScan == s) _reqScan = null;
            if (!abort) s.Done?.Invoke(s);
        }
        // ------------------------------------------------------------------------------------------

        private static void DiscardNewDependents()
        {
            foreach (var d in _newDependents) if (d != null) { DeregisterCropplotPlants(d); RemoveBlockChecked(d, "discard new dependents"); }
            _newDependents.Clear(); _depOriginals.Clear();
        }

        private static void RestoreDependentColliders()
        {
            foreach (var c in _depColliderDisabled) if (c != null) c.enabled = true;
            _depColliderDisabled.Clear();
        }
    }
}
