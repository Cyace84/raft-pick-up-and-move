using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Carry pipeline: pick up, cancel, confirm placement, build-mode enter/exit.
    public partial class Plugin
    {
        // carry-mode state. `Moving` is the block being carried (any Placeable block, not just storage).
        internal static Block Moving;
        private static Item_Base _movingItem;
        private static DPS _movingDps;
        private static RGD_Slot[] _movingSlots;
        // paint carried with the chest (SO_ColorValue refs + pattern indices); see Paint/CapturePaint
        private static Paint _movingPaint;
        // deep per-type state carried beyond paint/contents: sign/plaque text (null unless the block
        // is a TextWriterObject). Restored via the game's own networked setter so it survives + syncs.
        private static string _movingText;
        // the block's full save snapshot, captured at pickup. Many device RGD subtypes override
        // RestoreBlock (purifier tank+battery, cooking progress, fuel tank, wind turbine) - calling it
        // on the recreated block re-applies that state for FREE (the game's own load path). Null/base
        // = just paint+health.
        private static RGD_Block _movingRgd;

        // renderers/colliders we disabled to hide the original while carrying (restored on cancel)
        private static readonly List<Collider> _hiddenColliders = new List<Collider>();
        private static readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private static readonly List<Canvas> _hiddenCanvases = new List<Canvas>();   // device screens (Reciever radar) aren't Renderers

        private static void TryBeginMove()
        {
            var player = ComponentManager<Network_Player>.Value;
            if (player == null) { Trace("begin: no local Network_Player."); return; }

            var cam = Camera.main;
            if (cam == null) { Trace("begin: Camera.main is null."); return; }

            if (!Physics.Raycast(cam.transform.position, cam.transform.forward,
                    out var hit, Player.UseDistance * 2f, LayerMasks.MASK_Block))
            { Trace("begin: raycast hit nothing on MASK_Block."); return; }

            var block = hit.collider.GetComponentInParent<Block>();
            if (block == null)
            { Trace($"begin: hit '{hit.collider.name}' but no Block in parents."); return; }
            if (block.buildableItem == null)
            { Trace("begin: block has null buildableItem."); return; }

            // WHITELIST: only crafted PLACEABLE objects (devices/decor/storage), never the structural
            // raft skeleton (foundations/walls/floors/pillars/pipes). settings_buildable.Placeable is
            // the game's own flag separating placed objects from structure (verified: ItemInstance_
            // Buildable.Placeable => placeable). One check, no hardcoded type list.
            var sb = block.buildableItem.settings_buildable;
            if (sb == null || !sb.Placeable)
            { Trace($"begin: '{block.buildableItem.UniqueName}' is structure (not Placeable) - not movable."); return; }

            // HARD EXCLUSIONS (mechanics incompatible with carry, not a state problem):
            // - Block_DetailPlank places by STRETCHING from point A to point B; a single-point carry
            //   ghost breaks it (observed: sinks under the floor, looks like it vanished). Excluded.
            // - A zipline with a rope strung: teleporting one end leaves the rope hanging mid-air
            //   (MeshPath connections don't follow). Detach first, then move. (v2 idea: drag the rope.)
            if (block is Block_DetailPlank)
            { NoteHud(Loc.T("plank")); return; }
            if (HasAttachedRope(block))
            { NoteHud(Loc.T("rope")); return; }

            // STATE GATE moved to placement time: since the teleport pivot, a same-variant move keeps
            // the SAME object - all state (scarecrow integrity, beehive combs, charger batteries+fuel,
            // refiner contents...) survives by construction, so stateful blocks are carryable now.
            // Only the RECREATE path (surface-type change) can lose unhandled state; ConfirmMove /
            // HandleMoveRequest refuse THAT with 'same surface type' instead of refusing M here.

            // NOTE: no "dependents on top" gate. An OverlapBox-above heuristic wrongly refused normal
            // blocks (stacked chests, a chest under a shelf or the next floor) because a block ABOVE
            // isn't necessarily SUPPORTED BY this one. Known limitation: moving a surface with loose
            // decor on it (a table) can drop that decor. Revisit with a real support-graph query.

            // storage contents: only storages carry an inventory; other placeables carry no slots.
            _movingSlots = (block is Storage_Small storage && storage.GetInventoryReference() != null)
                ? storage.GetInventoryReference().GetRGDSlots() : null;
            _movingItem = block.buildableItem;
            _movingDps = block.dpsType;
            _movingPaint = CapturePaint(block);
            _movingText = TryGetSignText(block);
            _movingRgd = TryCaptureRgd(block);
            Moving = block;
            ClearHintIfShown(); // leaving idle -> drop our hint; build-mode UI takes over

            // Hide the original while carrying so its mesh+collider don't block placing the
            // ghost on/near its own spot. Do NOT SetActive(false) the GameObject - that
            // de-registers the block and makes RemoveBlockNetwork silently fail (the classic
            // duplicate). Disabling only Renderers+Colliders keeps registration intact.
            // Stack: snapshot neighbours' stability BEFORE hiding the original (flip-test dep scan;
            // vanilla DestroyBlock has the same shape: deactivate -> wait a frame -> read IsStable).
            // Resolves 1-2 frames later in Tick: hides what rests on this block (cosmetic) and
            // remembers it in _carryDeps so placement teleports the stack along.
            _carryDeps.Clear();
            _pickupScan = StartDepScan(block, ownB: false,
                s => { foreach (var d in s.Deps) { AddGhostPreview(d, block); HideVisual(d); } _carryDeps.AddRange(s.Deps); });

            // visual-only clone of the LIVE original (inserted batteries, pot contents...) shown on
            // the ghost. MUST be built before HideVisual disables the renderers we copy from.
            DestroyGhostPreviews();
            AddGhostPreview(block, block, pruneAgainstGhost: true);

            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            _hiddenCanvases.Clear();
            foreach (var c in block.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; _hiddenColliders.Add(c); }
            HideVisual(block);

            // make sure the build creator is active so the ghost shows, then engage it. Remember
            // whether WE enabled it: without the hammer equipped it's inactive, and its Update drives
            // the whole build UI (BuildMenu prompt + RMB opens the menu), so ExitBuildMode must put it
            // back or the build-materials button haunts every held item.
            var bc = player.BlockCreator;
            _bcWasInactive = bc != null && !bc.gameObject.activeSelf;
            if (_bcWasInactive) bc.gameObject.SetActive(true);
            bc?.SetBlockTypeToBuild(_movingItem);

            Note($"Carrying '{_movingItem.UniqueName}'" + ((_movingSlots?.Length ?? 0) > 0 ? $" ({_movingSlots.Length} slots)" : "") + ". LMB to place.");
        }

        internal static void CancelMove()
        {
            if (Moving == null) return;
            if (_pickupScan != null) FinishDepScan(_pickupScan, abort: true);
            _carryDeps.Clear();
            // restore the hidden original
            RestoreHidden();
            ExitBuildMode();
            Moving = null;
            _movingItem = null;
            _movingSlots = null;
            _movingText = null;
            _movingRgd = null;
        }

        // Hide a block's visuals during carry: Renderers AND Canvases (a Reciever's radar screen is a
        // world Canvas, not a Renderer, so a renderer-only hide left it floating at the old spot).
        private static void HideVisual(Block b)
        {
            foreach (var r in b.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }
            foreach (var cv in b.GetComponentsInChildren<Canvas>())
                if (cv.enabled) { cv.enabled = false; _hiddenCanvases.Add(cv); }
        }

        private static void RestoreHidden()
        {
            foreach (var c in _hiddenColliders) if (c != null) c.enabled = true;
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            foreach (var cv in _hiddenCanvases) if (cv != null) cv.enabled = true;
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            _hiddenCanvases.Clear();
        }

        // Single source of truth for "can this block be moved right now" - used by both the in-world
        // hint and TryBeginMove's gate, so the 'M Move' prompt appears EXACTLY on what will actually move.
        private static bool IsMovable(Block b)
        {
            if (b == null || b.buildableItem == null) return false;
            var sb = b.buildableItem.settings_buildable;
            if (sb == null || !sb.Placeable) return false;
            if (b is Block_DetailPlank) return false; // A->B stretch mechanic, incompatible with carry
            if (HasAttachedRope(b)) return false;     // zipline with a rope strung - detach first
            return true; // stateful blocks teleport with their state; recreate path gates itself
        }

        // Zipline endpoints register a MeshPathBase; a strung rope is a MeshPath.PathConnections
        // entry referencing it. (ZiplineBase.meshPath is private but a child component - decomp
        // ZiplineBase.cs / MeshPathConnection.cs.)
        private static bool HasAttachedRope(Block b)
        {
            if (!(b is ZiplineBase)) return false;
            try
            {
                var mp = b.GetComponentInChildren<MeshPathBase>(true);
                if (mp == null) return false;
                var conns = MeshPath.PathConnections;
                if (conns != null)
                    foreach (var c in conns)
                        if (c != null && (c.baseA == mp || c.baseB == mp)) return true;
            }
            catch { return true; } // can't tell -> safer to refuse than strand a rope
            return false;
        }

        // BlockCreator was INACTIVE when the move started (no hammer in hands): we enabled it for the
        // ghost, so we must disable it again on exit. Vanilla gates the whole build UI (the 'BuildMenu'
        // prompt + RMB opening the menu, BlockCreator.Update:268-283) purely on this component being
        // enabled while the hammer is equipped - leaving it on gave a build-materials button while
        // holding a water cup. isRotating is reset BEFORE deactivation, so the frozen-Update hotbar
        // lock can't recur (it freezes at false).
        private static bool _bcWasInactive;

        // Clear carry state + leave build mode. The original is already removed by the time we call
        // this, so there is nothing to restore.
        private static void ClearCarry()
        {
            Moving = null;
            _movingItem = null;
            _movingSlots = null;
            _movingText = null;
            _movingRgd = null;
            ExitBuildMode();
        }

        // Abort WITHOUT data loss: in place-first order the original is never removed until the new
        // chest is confirmed stable, so on any failure we just un-hide the original (still there),
        // leave build mode and clear carry state.
        private static void AbortKeepOriginal()
        {
            RestoreHidden();
            ClearCarry();
        }

        // Leave build mode WITHOUT placing extra copies, the vanilla way: clear the selected
        // buildable + destroy the ghost. With selectedBlock null, BlockCreator.Update early-returns
        // before any placement, so LMB can't spawn another copy.
        // Never SetActive(false) the BlockCreator GameObject. Vanilla keeps it always active -
        // its Update both drives the build-menu prompt and resets isRotating. Deactivating it froze
        // isRotating=true, and Hotbar gates number-key slot selection on BlockCreator.IsRotating, so
        // the hotbar got stuck on one slot until an inventory action (Hotbar.Update line 366).
        private static void ExitBuildMode()
        {
            DestroyGhostPreviews();
            var bc = ComponentManager<Network_Player>.Value?.BlockCreator;
            if (bc == null) return;
            var t = Traverse.Create(bc);
            try
            {
                t.Field("selectedBuildableItem").SetValue(null);
                t.Field("isRotating").SetValue(false);
                t.Method("DestroyGhostBlock").GetValue();
            }
            catch (System.Exception ex) { Warn("exit build: " + ex.Message); }
            bc.SetGhostBlockVisibility(false);
            // If the move ENABLED the BlockCreator (no hammer equipped), disable it again: enabled, its
            // Update draws the 'BuildMenu' prompt and RMB opens the build menu with ANY item in hands.
            // Safe against the old hotbar lock - isRotating was just reset, so it freezes at false.
            if (_bcWasInactive) { _bcWasInactive = false; try { bc.gameObject.SetActive(false); } catch { } }
        }

        // A placement-time refusal must not leave the vanilla ghost alive this frame. BlockCreator.
        // Update runs after our Ticker and, seeing BuildError.None + the SAME LMB press, places a
        // real block itself (and eats its build cost): a refusal that returned with a live ghost
        // let vanilla build a duplicate on every click, and M-cancel then restored the hidden
        // original next to it. Success paths are safe because ExitBuildMode nulls selectedBlock in
        // the same frame. This kills the ghost NOW and re-arms it next Tick, so the carry continues
        // but the click can't double-place.
        private static bool _rearmBuild;
        private static void SuppressVanillaPlaceThisFrame(BlockCreator bc)
        {
            var t = Traverse.Create(bc);
            try
            {
                t.Field("selectedBuildableItem").SetValue(null);
                t.Method("DestroyGhostBlock").GetValue();
            }
            catch (System.Exception ex) { Warn("suppress place: " + ex.Message); }
            _rearmBuild = true;
        }

        // Read the vanilla ghost, remove the original, recreate at the new spot, restore inventory.
        internal static void ConfirmMove(BlockCreator bc)
        {
            var ghost = bc.selectedBlock;
            if (ghost == null) { Trace("place: no ghost (selectedBlock null) - aim at a buildable surface."); return; }

            var err = Traverse.Create(bc).Method("CanBuildBlock", new object[] { ghost }).GetValue<BuildError>();
            if (err != BuildError.None) { Trace($"place: invalid spot ({err})."); return; }

            var pos = ghost.transform.localPosition;
            var rot = ghost.transform.localEulerAngles;
            var item = _movingItem;
            // CRITICAL: use the GHOST's surface-matched DPS (Wall/Floor/Ceiling), NOT the original's
            // stored dps. settings_buildable.GetBlockPrefab(dpsType) picks a DIFFERENT prefab per
            // surface (Placeable_Storage_*_Wall has a holder + a wall-facing stability gizmo;
            // *_Floor's gizmo points down). Rebuilding the floor variant on a wall => no holder and a
            // gizmo that never finds support => IsStable() false forever. The ghost already resolved
            // the correct variant (that's why ghostStable=True), so mirror it.
            var dps = ghost.dpsType;
            var slots = _movingSlots;
            var original = Moving;
            var player = ComponentManager<Network_Player>.Value;
            if (player == null) { Trace("place: no local player."); return; }

            // PLACE-FIRST + STABILITY GATE (the real fix). Create the new chest while the original is
            // still present but with its colliders DISABLED (hidden during carry) - so the new chest's
            // stability gizmos judge it WITHOUT the original, exactly the post-removal situation.
            // DestroyBlock's cascade only takes blocks that are !IsStable() within the removed block's
            // UnstableCheckDistance, so we remove the original ONLY once the new chest is confirmed
            // stable on its own; a stable block can't be cascaded. If the spot is unsupported (a wall
            // with no holder, an edge, clipping) the new chest is !IsStable() -> we discard it and KEEP
            // the original, so nothing is ever lost.
            if (Raft_Network.IsHost)
            {
                // Same prefab variant -> teleport the existing block: nothing recreated, nothing
                // removed, no state to carry, no replication lifecycle to lose. Variant identity is
                // compared by prefab NAME (instances are 'PrefabName(Clone)') - the dpsType enum on
                // a variant prefab does not reliably equal the surface we send, names are truth by
                // construction. The stack found by the pickup dep-scan (_carryDeps) teleports along;
                // RestoreHidden un-hides the SAME object at its new spot.
                // Block_Pipe (segments AND devices - the charger IS Block_Pipe) teleports too:
                // BeginTeleport replays the autotile lifecycle around the move (see PipeLifecycle).
                // A blanket 'pipes always recreate' exclusion would lock stateful pipe devices out
                // entirely (stateful -> recreate refused -> 'surface' forever).
                if (SameVariant(original, item, dps))
                {
                    BeginTeleport(original, pos, rot, default, _carryDeps);
                    _carryDeps.Clear();
                    Moving = null; _movingItem = null; _movingSlots = null;
                    RestoreHidden();
                    ExitBuildMode();
                    return;
                }
                // Variant differs -> RECREATE. Recreation replays state through our RGD adapters;
                // a stateful type we have no adapter for must not be rebuilt (state would be lost).
                // Those are teleport-only: same surface type keeps the same prefab -> same object.
                if (HasUnhandledState(original))
                { NoteHud(Loc.T("surface")); SuppressVanillaPlaceThisFrame(bc); return; }
                // Carried-group gate: a variant switch converts the block (floor table ->
                // wall-mounted table); the stack on top cannot ride that - a conversion once moved
                // 3 of 4 deps, the 4th detached, and the wall-variant table later cascade-broke
                // since nothing wall-worthy supported it. With deps the move is teleport-only;
                // refuse the conversion instead of maiming the group.
                if (_carryDeps.Count > 0 || _pickupScan != null)
                { NoteHud(Loc.T("group")); SuppressVanillaPlaceThisFrame(bc); return; }
                Block nb;
                try { nb = bc.CreateBlockCheat(item, pos, rot, dps, -1); }
                catch (System.Exception ex)
                {
                    Warn("place failed (CreateBlockCheat threw): " + ex.Message + "; chest left where it was.");
                    AbortKeepOriginal();
                    return;
                }

                if (nb == null)
                {
                    Warn("place produced no block; original left where it was.");
                    AbortKeepOriginal();
                    return;
                }

                // place-first + DEFERRED stability gate: nb exists, but the original stays hidden
                // until nb settles to IsStable() over a few physics steps (same-frame reads stale -
                // SyncTransforms wasn't enough; OverlapBox needs real FixedUpdate steps). No removal
                // happens yet, so no cascade yet. PollHostVerify finishes the swap or undoes it.
                ArmHostVerify(nb, original, slots, _movingText, _movingRgd, _movingPaint, default, 0f);
                Trace($"placing nb@{nb.transform.localPosition.ToString("F2")}; verifying support...");

                // leave build mode but KEEP hidden bookkeeping + pending fields for the poll
                Moving = null;
                _movingItem = null;
                _movingSlots = null;
                ExitBuildMode();
            }
            else
            {
                // CLIENT: hand the whole move to the HOST (both run the mod) - storages included.
                // The vanilla place-request path (Message_BlockCreator_PlaceBlock) is no good here:
                // the host validates that placement against the ORIGINAL chest still standing there
                // with colliders on, so any move shorter than the chest's own footprint is silently
                // rejected -> timeout -> "snap back". The move-request path hides the original's
                // colliders on the host and places via CreateBlockCheat, so short-distance moves
                // work like they do for devices.
                {
                    // same teleport-only gate as the host branch, checked locally to save the round
                    // trip (the host enforces it authoritatively anyway via refusal relay)
                    if (!SameVariant(original, item, dps))
                    {
                        if (HasUnhandledState(original))
                        { NoteHud(Loc.T("surface")); SuppressVanillaPlaceThisFrame(bc); return; }
                        // same carried-group gate as the host branch (see comment there)
                        if (_carryDeps.Count > 0 || _pickupScan != null)
                        { NoteHud(Loc.T("group")); SuppressVanillaPlaceThisFrame(bc); return; }
                    }
                    if (player.Network == null) { Warn("client move: no Network."); AbortKeepOriginal(); return; }
                    if (!SendMoveRequest(player, original.ObjectIndex, pos, rot, dps))
                    { NoteHud(Loc.T("no_host")); AbortKeepOriginal(); return; }
                    _cmSentTime = Time.realtimeSinceStartup; _cmOrigGoneLogged = false; _cmSeenLogged = false;
                    _cmAcked = false; _cmProbeSent = false;
                    // remember our captured state + snapshot existing blocks so we can find the new one
                    // and restore it on our own view (host's restore doesn't replicate device state back).
                    _clientMoveRgd = _movingRgd; _clientMoveSlots = _movingSlots;
                    _clientMovePaint = _movingPaint; _clientMoveText = _movingText;
                    _clientMovePos = pos; _clientMoveRestored = false;
                    _preExisting.Clear();
                    foreach (var b in BlockCreator.GetPlacedBlocks()) if (b != null) _preExisting.Add(b.ObjectIndex);
                    _pendingClientMoveOriginal = original;
                    _awaitingHostMove = true;
                    _clientMoveDeadlineFrame = Time.frameCount + 600; // ~10s failsafe
                    Note($"client: asked host to move '{item.UniqueName}'; original kept until the host confirms.");
                    Moving = null; _movingItem = null; _movingSlots = null; ExitBuildMode();
                    return;
                }

            }
        }
    }
}
