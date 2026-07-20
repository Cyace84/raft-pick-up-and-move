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

            // Aim from the player's CameraTransform, NOT Camera.main: it's what vanilla
            // BlockCreator uses for the ghost raycast, and Camera.main gets hijacked by freecam
            // mods (UnityExplorer tags UE_Freecam as MainCamera) - M would grab from the freecam
            // view while the ghost follows the player aim. Same transform in normal play.
            var cam = player.CameraTransform;
            if (cam == null) { Trace("begin: player.CameraTransform is null."); return; }

            if (!Physics.Raycast(cam.position, cam.forward,
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
            // (Ziplines with a strung rope used to be excluded here; RefreshWires now re-lays the
            //  MeshPath on the teleport path, so they move freely. The RECREATE path still drops the
            //  rope exactly like a vanilla removal would - acceptable, and same-surface moves never
            //  take that path.)
            if (block is Block_DetailPlank)
            { NoteHud(Loc.T("plank")); return; }

            // STATE GATE moved to placement time: since the teleport pivot, a same-variant move keeps
            // the SAME object - all state (scarecrow integrity, beehive combs, charger batteries+fuel,
            // refiner contents...) survives by construction, so stateful blocks are carryable now.
            // Only the RECREATE path (surface-type change) can lose unhandled state; ConfirmMove /
            // HandleMoveRequest refuse THAT with 'same surface type' instead of refusing M here.

            // NOTE: no "dependents on top" gate. An OverlapBox-above heuristic wrongly refused normal
            // blocks (stacked chests, a chest under a shelf or the next floor) because a block ABOVE
            // isn't necessarily SUPPORTED BY this one. Known limitation: moving a surface with loose
            // decor on it (a table) can drop that decor. Revisit with a real support-graph query.

            // R1 (host): an OPEN storage's edits are invisible until its Message_Storage_Close
            // lands (StorageManager.cs:190 applies slots only on close), so any snapshot taken now
            // is guaranteed stale. Refuse the pickup early; the commit gate in FinishHostMove is
            // the authoritative backstop (a remote open may not have replicated to us yet).
            if (block is Storage_Small sBusy && sBusy.IsOpen)
            { NoteHud(Loc.T("r_busy")); return; }

            // CLAIM: someone else is carrying this block right now - one M per block, no exceptions.
            // (host checks the authoritative table, a client checks its mirror; the in-flight race
            // is arbitrated host-side and the loser's carry is torn down via kind-14.)
            if (IsClaimedByOther(block.ObjectIndex))
            { NoteHud(Loc.T("r_carry")); return; }

            // storage contents: only storages carry an inventory; other placeables carry no slots.
            _movingSlots = (block is Storage_Small storage && storage.GetInventoryReference() != null)
                ? storage.GetInventoryReference().GetRGDSlots() : null;
            _movingItem = block.buildableItem;
            _movingDps = block.dpsType;
            _movingPaint = CapturePaint(block);
            _movingText = TryGetSignText(block);
            _movingRgd = TryCaptureRgd(block);
            Moving = block;
            AcquireCarryClaim(block.ObjectIndex); // claim it for us: everyone else's M now refuses
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
            // ReferenceEquals, not ==: a block destroyed by another peer mid-carry is Unity-null
            // (== null true) but managed-alive. We must still tear the carry down, so only bail when
            // there is genuinely no carry object at all.
            if (ReferenceEquals(Moving, null)) return;
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
            return true; // stateful blocks teleport with their state; recreate path gates itself
        }

        // --- live zipline rope preview ---------------------------------------------------------
        // While a strung zipline pole is carried, re-lay its rope(s) every frame to the GHOST's
        // connect point (bc.selectedBlock is the same prefab, so it carries its own MeshPathBase/
        // connectPoint) - the rope visibly follows the preview like the block ghosts do. When there
        // is no ghost this frame (aiming at void) or the carry ends, park the rope back at the REAL
        // poles; the teleport path then re-lays it at the destination via RefreshWires anyway, so
        // the restore is idempotent. Local-only cosmetics: peers converge on move commit.
        private static MeshPathBase _ropePreviewMpb;

        internal static void UpdateRopePreview()
        {
            MeshPathBase mpb = null;
            if (Moving is ZiplineBase && Moving != null)
                mpb = Moving.GetComponentInChildren<MeshPathBase>(true);
            MeshPathBase ghostMpb = null;
            if (mpb != null)
            {
                var ghost = ComponentManager<Network_Player>.Value?.BlockCreator?.selectedBlock;
                if (ghost != null) ghostMpb = ghost.GetComponentInChildren<MeshPathBase>(true);
            }
            if (mpb == null || ghostMpb == null || ghostMpb.connectPoint == null)
            {
                if (_ropePreviewMpb != null) { ReLayZiplines(_ropePreviewMpb); _ropePreviewMpb = null; }
                return;
            }
            var conns = MeshPath.PathConnections;
            if (conns == null) return;
            foreach (var c in conns)
            {
                if (c == null || !c.IsValid()) continue;
                if (c.baseA != mpb && c.baseB != mpb) continue;
                var other = c.baseA == mpb ? c.baseB : c.baseA;
                ReLayPath(c.path, other.connectPoint.position, ghostMpb.connectPoint.position, withCollider: false);
                _ropePreviewMpb = mpb;
            }
        }

        // --- live antenna wire preview ---------------------------------------------------------
        // Same idea as the rope preview: while a connected antenna OR a receiver is carried, its
        // wire endpoints follow the ghost every frame. The ghost is the same prefab, so a ghost
        // receiver carries its own sockets[] to read positions from; a ghost antenna's wire end is
        // just ghost.transform.position (vanilla uses ant.transform.position). Rope.SetPosition
        // only moves rope-part transforms + retiles the material (decomp Rope.cs) - per-frame safe,
        // no mesh allocation. Park back at the real block whenever the ghost is missing or the
        // carry ends; RefreshWires on the teleport path makes the restore idempotent.
        private static Block _wirePreviewBlock;

        internal static void UpdateWirePreview()
        {
            var mv = Moving;
            Block ghost = null;
            if (!ReferenceEquals(mv, null) && mv != null)
                ghost = ComponentManager<Network_Player>.Value?.BlockCreator?.selectedBlock;
            if (ghost == null)
            {
                if (_wirePreviewBlock != null) { RefreshWires(_wirePreviewBlock); _wirePreviewBlock = null; }
                return;
            }
            try
            {
                // carrying a connected ANTENNA: its wire's antenna end rides the ghost
                var ant = mv.GetComponent<Reciever_Antenna>();
                if (ant != null)
                {
                    var recv = _antRecv(ant); var wire = _antWire(ant);
                    if (recv != null && wire != null && recv.sockets != null
                        && ant.socketNumber >= 0 && ant.socketNumber < recv.sockets.Length)
                    {
                        wire.SetPosition(0, ghost.transform.position);
                        wire.SetPosition(1, recv.sockets[ant.socketNumber].position);
                        _wirePreviewBlock = mv;
                    }
                }
                // carrying a RECEIVER: every connected antenna's socket end rides the ghost's sockets
                var rc = mv.GetComponent<Reciever>();
                var ghostRc = ghost.GetComponent<Reciever>();
                if (rc != null && ghostRc != null && rc.antennas != null && ghostRc.sockets != null)
                {
                    foreach (var a in rc.antennas)
                    {
                        if (a == null) continue;
                        var wire = _antWire(a);
                        if (wire == null) continue;
                        if (a.socketNumber < 0 || a.socketNumber >= ghostRc.sockets.Length) continue;
                        wire.SetPosition(0, a.transform.position);
                        wire.SetPosition(1, ghostRc.sockets[a.socketNumber].position);
                        _wirePreviewBlock = mv;
                    }
                }
            }
            catch (System.Exception ex) { Warn($"wire preview: {ex.Message}"); }
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
        // A variant switch means RECREATE, and recreation has two hard gates, shared by the host
        // and client paths. (1) Recreation replays state through our RGD adapters; a stateful type
        // we have no adapter for must not be rebuilt (state would be lost) - those are teleport-only:
        // same surface type keeps the same prefab -> same object. (2) A carried group cannot ride a
        // conversion (floor table -> wall-mounted table): one such move carried 3 of 4 deps, the 4th
        // detached, and the converted table later cascade-broke since nothing wall-worthy supported
        // it. True = refused; hud note shown, vanilla placement suppressed.
        private static bool RefuseRecreate(BlockCreator bc, Block original)
        {
            if (HasUnhandledState(original))
            { NoteHud(Loc.T("surface")); SuppressVanillaPlaceThisFrame(bc); return true; }
            if (_carryDeps.Count > 0 || _pickupScan != null)
            { NoteHud(Loc.T("group")); SuppressVanillaPlaceThisFrame(bc); return true; }
            return false;
        }

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
            if (Raft_Network.IsHost) ConfirmMoveHost(bc, original, item, dps, pos, rot, slots);
            else ConfirmMoveClient(bc, original, item, dps, pos, rot, player);
        }

        private static void ConfirmMoveHost(BlockCreator bc, Block original, Item_Base item, DPS dps, Vector3 pos, Vector3 rot, RGD_Slot[] slots)
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
            // Variant differs -> RECREATE, if the gates allow it (see RefuseRecreate).
            if (RefuseRecreate(bc, original)) return;
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

        // CLIENT: hand the whole move to the HOST (both run the mod) - storages included.
        // The vanilla place-request path (Message_BlockCreator_PlaceBlock) is no good here:
        // the host validates that placement against the ORIGINAL chest still standing there
        // with colliders on, so any move shorter than the chest's own footprint is silently
        // rejected -> timeout -> "snap back". The move-request path hides the original's
        // colliders on the host and places via CreateBlockCheat, so short-distance moves
        // work like they do for devices.
        private static void ConfirmMoveClient(BlockCreator bc, Block original, Item_Base item, DPS dps, Vector3 pos, Vector3 rot, Network_Player player)
        {
            // same recreate gates as the host branch, checked locally to save the round
            // trip (the host enforces them authoritatively anyway via refusal relay)
            if (!SameVariant(original, item, dps) && RefuseRecreate(bc, original)) return;
            if (player.Network == null) { Warn("client move: no Network."); AbortKeepOriginal(); return; }
            if (!SendMoveRequest(player, original.ObjectIndex, pos, rot, dps))
            { NoteHud(Loc.T("no_host")); AbortKeepOriginal(); return; }
            _cmSentTime = Time.realtimeSinceStartup; _cmOrigGoneLogged = false; _cmSeenLogged = false;
            _cmAcked = false; _cmProbeSent = false;
            // remember our captured state + snapshot existing blocks so we can find the new one
            // and restore it on our own view (host's restore doesn't replicate device state back).
            _clientMoveRgd = _movingRgd; _clientMoveSlots = _movingSlots;
            _clientMovePaint = _movingPaint; _clientMoveText = _movingText;
            _clientMoveRestored = false;
            // R2: remember which block we asked to move; the host answers with kind-10 {orig,new}.
            _clientMoveOrigIndex = original.ObjectIndex; _clientMoveNewIndex = 0;
            _pendingClientMoveOriginal = original;
            _awaitingHostMove = true;
            _clientMoveDeadlineFrame = Time.frameCount + 600; // ~10s failsafe
            Note($"client: asked host to move '{item.UniqueName}'; original kept until the host confirms.");
            Moving = null; _movingItem = null; _movingSlots = null; ExitBuildMode();
        }
    }
}
