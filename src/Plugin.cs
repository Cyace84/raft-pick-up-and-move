using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RaftMovableStorage
{
    // Move a placed storage chest (with its contents) to a new spot, using the vanilla build
    // placement ghost as the preview. Press the hotkey while aiming at a storage to pick it up
    // into placement mode (ghost follows the cursor); left-click drops it at the new location with
    // its inventory intact; press the hotkey again (or right-click) to cancel.
    //
    // ARCHITECTURE NOTE (learned from runtime diagnostics in this BepInEx 5 + Wine/CrossOver setup):
    //   * BaseUnityPlugin.Update() is NOT pumped here, and the plugin component gets Destroyed a few
    //     seconds after Awake (which would also rip any Harmony patches via UnpatchSelf).
    //   * Therefore ALL per-frame logic lives on our own DontDestroyOnLoad `Ticker` GameObject.
    //     Placement is handled by reading the vanilla ghost (BlockCreator.selectedBlock) directly on
    //     left-click. Vanilla cannot double-place the chest because the storage item is not in the
    //     player's inventory while it is being carried.
    //   * ONE Harmony postfix is used, on Storage_Small.OnIsRayed, only to draw the "Move" key hint
    //     under the vanilla "Open" prompt. This is safe in this env: the patch is invoked by the
    //     game's own (pumped) raycast system, not our Update, and Harmony detours live in the global
    //     patch registry - BaseUnityPlugin has no OnDestroy/UnpatchSelf (verified by decompile), so
    //     the plugin component being destroyed after Awake does NOT remove them.
    //
    // Recon basis (verified by decompiling Assembly-CSharp.dll):
    //   Storage_Small : Block          GetInventoryReference() : Inventory
    //   Inventory.GetRGDSlots() : RGD_Slot[]   /   Inventory.SetSlotsFromRGD(RGD_Slot[])
    //   BlockCreator.SetBlockTypeToBuild(Item_Base)  -> shows ghost (selectedBlock follows cursor)
    //   BlockCreator.CreateBlockCheat(item, pos, rot, dps, -1)  (host: mints authoritative indices,
    //       RPCs Message_BlockCreator_PlaceBlock to all clients, fills inventory synchronously)
    //   BlockCreator.RemoveBlockNetwork(block, player, updateRaftBounds)  (static, networked)
    //
    // SCOPE: single-player + multiplayer (host AND client), all live-verified. Host places via
    // CreateBlockCheat; a client SendP2P's a vanilla place-request to the host and polls the
    // replicated chest to sync contents (see ConfirmMove / PollClientChest). Contents travel via
    // the vanilla Message_Storage_Close path. Only the player moving a chest needs the mod.

    [BepInPlugin(Guid, "Pick Up & Move", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.cyace84.raftmovablestorage";

        public static ConfigEntry<KeyboardShortcut> MoveKey;
        public static ManualLogSource Log;

        // carry-mode state. `moving` is the block being carried (any Placeable block, not just storage).
        internal static Block moving;
        internal static Item_Base movingItem;
        internal static DPS movingDps;
        internal static RGD_Slot[] movingSlots;
        // paint carried with the chest (SO_ColorValue refs + pattern indices); see Paint/CapturePaint
        private static Paint movingPaint;
        // deep per-type state carried beyond paint/contents: sign/plaque text (null unless the block
        // is a TextWriterObject). Restored via the game's own networked setter so it survives + syncs.
        private static string movingText;
        // the block's full save snapshot, captured at pickup. Many device RGD subtypes override
        // RestoreBlock (purifier tank+battery, cooking progress, fuel tank, wind turbine) - calling it
        // on the recreated block re-applies that state for FREE (the game's own load path). Null/base
        // = just paint+health.
        private static RGD_Block movingRgd;

        // A block's full paint state: per-side base + pattern colours and pattern indices. These are
        // plain Block instance fields (currentColorA/B, currentPatternColorA/B, currentPatternIndexN);
        // CreateBlockCheat makes a fresh unpainted block, so we snapshot them and re-apply after the
        // new chest settles. (Bug: paint a chest, move it, it reverted to default.)
        private struct Paint
        {
            public SO_ColorValue cA, cB, pcA, pcB;
            public uint pi1, pi2;
            public bool Any => cA != null || cB != null || pcA != null || pcB != null;
        }
        // renderers/colliders we disabled to hide the original while carrying (restored on cancel)
        private static readonly List<Collider> _hiddenColliders = new List<Collider>();
        private static readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

        // CLIENT (non-host) placement is async: we ask the host to place the chest, then must wait
        // for the host's reply to spawn it locally before we can push its contents. State for that.
        private static bool _awaitingClientChest;
        private static RGD_Slot[] _syncSlots;
        private static Vector3 _syncPos;
        private static Paint _syncPaint;
        private static int _syncDeadlineFrame;
        private static readonly HashSet<uint> _preExisting = new HashSet<uint>();
        // the original kept ALIVE (only hidden) on the client until the replicated chest is confirmed
        // STABLE; removed only then, restored on failure/timeout -> never lost (DATA-LOSS FIX)
        private static Storage_Small _pendingClientOriginal;
        // HOST place-first verify: nb is created but the original is removed ONLY once nb settles to
        // IsStable() over a few physics steps (the same-frame check reads stale - see DIAG). The
        // original stays hidden until then, so a never-settling spot is undone with nothing lost.
        private static bool _hostVerifying;
        private static Block _hostNb;
        private static Block _hostOriginal;
        private static RGD_Slot[] _hostSlots;
        private static string _hostText;
        private static RGD_Block _hostRgd;
        private static int _hostVerifyStart;
        private static int _hostVerifyDeadline;
        private static int _hostLastLoggedStable; // -1 unknown, 0 false, 1 true (log only on change)
        private static Paint _hostPaint;
        private Harmony _harmony;
        private static GameObject _tickerGo;

        private void Awake()
        {
            Log = Logger;
            MoveKey = Config.Bind(
                "General",
                "MoveStorageKey",
                new KeyboardShortcut(KeyCode.M),
                "Aim at a storage and press this to pick it up (with its contents) into placement mode. " +
                "Left-click to drop it at the new spot. Press the key again or right-click to cancel.");

            // Own DontDestroyOnLoad ticker: BaseUnityPlugin.Update is not pumped in this env and the
            // plugin component gets destroyed shortly after Awake.
            var go = new GameObject("RMS_Ticker");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Ticker>();
            _tickerGo = go;

            // ONE Harmony postfix for the in-world "Move" key hint (see architecture note). Invoked by
            // the game's raycast system, independent of our Update; patches persist past Awake.
            try { _harmony = new Harmony(Guid); _harmony.PatchAll(typeof(Plugin).Assembly); }
            catch (System.Exception ex) { Log?.LogWarning("Harmony patch failed (Move hint disabled, core feature unaffected): " + ex.Message); }

            Note($"{Info.Metadata.Name} {Info.Metadata.Version} loaded. Move key = {MoveKey.Value}.");
        }

        // Reload-safe teardown for MonoLab.Hot.Reload (dev only): drop our ticker, remove the Harmony
        // patch, and clear carry state so a hot-reloaded copy doesn't duplicate input handling or the
        // OnIsRayed postfix. No-op cost in production (never called outside the dev reloader).
        public static void __MonoLabUnload()
        {
            try { Harmony.UnpatchID(Guid); } catch { }
            // Destroy EVERY ticker we (or an older hot-reloaded copy) ever spawned. The ticker is
            // HideAndDontSave, which FindObjectsOfType misses - Resources.FindObjectsOfTypeAll sees it.
            // Without this, stacked reloads leave multiple tickers, each with its own `moving` static,
            // fighting over the hotkey (the cause of flaky cancel). Belt-and-suspenders over _tickerGo.
            try
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go != null && go.name == "RMS_Ticker") UnityEngine.Object.Destroy(go);
            }
            catch { }
            _tickerGo = null;
            moving = null; movingItem = null; movingSlots = null;
            _hostVerifying = false; _awaitingClientChest = false;
        }

        // Info = user-facing milestones; Trace = diagnostic non-events (missed raycast, bad spot).
        internal static void Note(string msg) => Log?.LogInfo(msg);
        internal static void Trace(string msg) => Log?.LogDebug(msg);

        // Per-frame logic, driven by Ticker.
        internal static void Tick()
        {
            if (_awaitingClientChest) PollClientChest();
            if (_hostVerifying) { PollHostVerify(); return; }

            if (MoveKey.Value.IsDown())
            {
                if (moving != null) CancelMove();
                else TryBeginMove();
                return;
            }

            if (moving == null) return;

            // carrying: right-click cancels, left-click confirms placement at the ghost
            if (Input.GetMouseButtonDown(1)) { Trace("rmb: cancel carry."); CancelMove(); return; }
            if (Input.GetMouseButtonDown(0))
            {
                var bc = ComponentManager<Network_Player>.Value?.BlockCreator;
                if (bc != null) ConfirmMove(bc);
            }
        }

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

            // SAFE GATE (never lose state): we recreate the block fresh, so any deep device state we
            // don't explicitly carry (desalinator water, blender contents, battery charge, planter
            // crop, cooking progress) would be lost. Only carry blocks we FULLY preserve - storages
            // (slots) and signs (text) - or provably stateless decor. Everything else is refused, not
            // eaten. Verified: each stateful device has its own RGD subtype / networked behaviour.
            if (HasUnhandledState(block))
            { Note($"Can't move '{block.buildableItem.UniqueName}' yet - it has contents/state this version would lose. Not moved."); return; }

            // NOTE: no "dependents on top" gate. An OverlapBox-above heuristic wrongly refused normal
            // blocks (stacked chests, a chest under a shelf or the next floor) because a block ABOVE
            // isn't necessarily SUPPORTED BY this one. Known limitation: moving a surface with loose
            // decor on it (a table) can drop that decor. Revisit with a real support-graph query.

            // storage contents: only storages carry an inventory; other placeables carry no slots.
            movingSlots = (block is Storage_Small storage && storage.GetInventoryReference() != null)
                ? storage.GetInventoryReference().GetRGDSlots() : null;
            movingItem = block.buildableItem;
            movingDps = block.dpsType;
            movingPaint = CapturePaint(block);
            movingText = TryGetSignText(block);
            movingRgd = TryCaptureRgd(block);
            moving = block;
            ClearHintIfShown(); // leaving idle -> drop our hint; build-mode UI takes over

            // BUG1: hide the original while carrying so its mesh+collider don't block placing the
            // ghost on/near its own spot. NOTE: do NOT SetActive(false) the GameObject — that
            // de-registers the block and makes RemoveBlockNetwork silently fail (proven via eval:
            // it caused the duplicate). Disabling only Renderers+Colliders keeps registration intact.
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            foreach (var c in block.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; _hiddenColliders.Add(c); }
            foreach (var r in block.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            // make sure the build creator is active so the ghost shows, then engage it
            var bc = player.BlockCreator;
            if (bc != null && !bc.gameObject.activeSelf) bc.gameObject.SetActive(true);
            bc?.SetBlockTypeToBuild(movingItem);

            Note($"Carrying '{movingItem.UniqueName}'" + ((movingSlots?.Length ?? 0) > 0 ? $" ({movingSlots.Length} slots)" : "") + ". LMB to place.");
        }

        internal static void CancelMove()
        {
            if (moving == null) return;
            // restore the hidden original (BUG1)
            RestoreHidden();
            ExitBuildMode();
            moving = null;
            movingItem = null;
            movingSlots = null;
            movingText = null;
            movingRgd = null;
        }

        private static void RestoreHidden()
        {
            foreach (var c in _hiddenColliders) if (c != null) c.enabled = true;
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
        }

        // Single source of truth for "can this block be moved right now" - used by both the in-world
        // hint and TryBeginMove's gate, so the 'M Move' prompt appears EXACTLY on what will actually move.
        private static bool IsMovable(Block b)
        {
            if (b == null || b.buildableItem == null) return false;
            var sb = b.buildableItem.settings_buildable;
            if (sb == null || !sb.Placeable) return false;
            return !HasUnhandledState(b);
        }

        // In-world 'M Move' hint, driven every idle frame from the Ticker so it shows on ANY movable
        // block (not just storage). Storages keep the Harmony postfix (stacks 'Move' under their own
        // 'Open'); here we handle everything else and manage our own show/hide. Cached on the aimed
        // block so we don't run Serialize_Save()/OverlapBox every frame.
        private static bool _hintShown;
        private static Block _hintLastBlock;
        private static bool _hintLastMovable;
        // Driven from Ticker.LateUpdate (NOT Update) so our ShowText runs AFTER the game's own pickup/
        // remove prompt was set this frame - otherwise undefined Update order lets the game's prompt
        // (clearAllTexts) wipe ours, or ours wipe theirs. LateUpdate guarantees we stack last.
        internal static void LateTick()
        {
            if (moving != null || _hostVerifying || _awaitingClientChest) { ClearHintIfShown(); return; }
            UpdateMoveHint();
        }
        private static void UpdateMoveHint()
        {
            if (CanvasHelper.ActiveMenu != MenuType.None) { ClearHintIfShown(); return; }
            var cam = Camera.main;
            if (cam == null) { ClearHintIfShown(); return; }
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, Player.UseDistance * 2f, LayerMasks.MASK_Block))
            { ClearHintIfShown(); return; }
            var block = hit.collider.GetComponentInParent<Block>();
            if (block == null) { ClearHintIfShown(); return; }

            bool movable;
            if (block == _hintLastBlock) movable = _hintLastMovable;
            else { movable = IsMovable(block); _hintLastBlock = block; _hintLastMovable = movable; }
            if (!movable) { ClearHintIfShown(); return; }

            // storages draw their own 'Open' + the postfix adds 'Move'; don't double-handle here
            if (block is Storage_Small) { _hintShown = false; return; }

            var dtm = ComponentManager<DisplayTextManager>.Value;
            if (dtm == null) return;
            // ADD our line at index 1 WITHOUT clearing - keeps the game's 'X' remove/pickup prompt at
            // index 0 and stacks 'M Move' under it (same slot the storage postfix uses).
            dtm.ShowText("Move", MoveKey.Value.MainKey, 1, 0, false);
            _hintShown = true;
        }
        private static void ClearHintIfShown()
        {
            _hintLastBlock = null;
            if (!_hintShown) return;
            _hintShown = false;
            // hide ONLY our line (index 1); never touch the game's prompts at index 0
            try { ComponentManager<DisplayTextManager>.Value?.HideDisplayTexts(1); } catch { }
        }

        // Deep-state safety: true if the block holds device state we don't explicitly carry, so moving
        // it (recreate-fresh) would lose it. Storage (slots) and sign (TextWriterObject text) are
        // handled => safe. Otherwise a networked behaviour or a non-base block RGD means deep state.
        private static bool HasUnhandledState(Block b)
        {
            if (b is Storage_Small) return false;                                    // slots handled
            if (b.GetComponentInChildren<TextWriterObject>() != null) return false;   // sign text handled
            try
            {
                var rgd = CaptureRestorableRgd(b);
                if (rgd != null && CanRestoreDevice(rgd)) return false;                // device state we replay
                if (rgd != null && rgd.GetType() != typeof(RGD_Block)) return true;    // subtype w/o override => unhandled
                // base RGD_Block (or null): refuse only if it has an unhandled networked stateful
                // sibling whose state we don't carry. Plain stateless decor passes.
                if (b.networkType != NetworkType.None) return true;
                if (b.networkedBehaviour != null || b.networkedIDBehaviour != null) return true;
            }
            catch { return true; }                                                   // unknown => refuse (safe)
            return false;
        }

        // True if the RGD type provides its own RestoreBlock(Block) (self-restoring device state).
        private static bool OverridesRestoreBlock(System.Type t)
        {
            var m = t.GetMethod("RestoreBlock", new[] { typeof(Block) });
            return m != null && m.DeclaringType != typeof(RGD_Block);
        }

        // True if we know how to replay this RGD's device state onto a freshly created block: either it
        // self-restores via a RestoreBlock override, or it's a type we have a dedicated adapter for
        // (cropplot plants, sprinkler water+battery, recycler/extractor tanks). Keep in sync with
        // ApplyDeviceState - a type listed here MUST be restored there, or moving it would lose state.
        private static bool CanRestoreDevice(RGD_Block rgd)
            => rgd != null && (OverridesRestoreBlock(rgd.GetType())
                || rgd is RGD_Cropplot || rgd is RGD_Block_Sprinkler || rgd is RGD_Block_Recycler);

        // Replay captured device state onto the recreated block via the game's own load-path restore
        // methods. Self-restoring subtypes use RestoreBlock; the others keep their save data on a nested
        // RGD reached through a dedicated restore call (these don't override RestoreBlock). All host-local
        // (peers resync via the device / reload). Restores paint+health for the override path too.
        private static void ApplyDeviceState(RGD_Block rgd, Block nb)
        {
            if (rgd == null || nb == null) return;
            try
            {
                if (OverridesRestoreBlock(rgd.GetType())) { rgd.RestoreBlock(nb); return; }
                switch (rgd)
                {
                    case RGD_Cropplot rc:
                        var plot = nb.GetComponent<Cropplot>();
                        var pm = ComponentManager<Network_Player>.Value?.PlantManager;
                        if (plot != null && pm != null) rc.RestoreCropplot(plot, pm);
                        break;
                    case RGD_Block_Sprinkler rs:
                        var spr = nb.GetComponent<Sprinkler>();
                        if (spr != null) rs.rgdSprinkler?.RestoreSprinkler(spr);
                        break;
                    case RGD_Block_Recycler rr:
                        var ext = nb.GetComponent<Placeable_Extractor>();
                        if (ext != null) rr.rgdRecycler?.RestoreExtractor(ext);
                        break;
                }
            }
            catch (System.Exception ex) { Log?.LogWarning("device restore failed: " + ex.Message); }
        }

        // Full save snapshot used to replay self-restoring device state on the recreated block.
        // Networked devices (cooking pot/juicer/grill) keep their save method on a SIBLING component
        // (CookingTable : MonoBehaviour_ID_Network), NOT on Block - Block.Serialize_Save returns null
        // for a networked block. So when the Block-level save isn't a self-restoring subtype, scan the
        // same GameObject's components for a Serialize_Save() -> RGD_Block that overrides RestoreBlock
        // (e.g. CookingTable -> RGD_Block_CookingPot, whose RestoreBlock replays cook timer/portions/
        // recipe/slots via CookingTable.RestoreCookingPot).
        private static RGD_Block CaptureRestorableRgd(Block b)
        {
            try
            {
                var direct = b.Serialize_Save() as RGD_Block;
                if (direct != null && CanRestoreDevice(direct)) return direct;
                foreach (var comp in b.GetComponents<MonoBehaviour>())
                {
                    if (comp == null || comp is Block) continue;
                    var m = comp.GetType().GetMethod("Serialize_Save", System.Type.EmptyTypes);
                    if (m == null || !typeof(RGD).IsAssignableFrom(m.ReturnType)) continue;
                    if (m.Invoke(comp, null) is RGD_Block r && CanRestoreDevice(r)) return r;
                }
                return direct;   // plain RGD_Block, an unhandled subtype, or null
            }
            catch { return null; }
        }

        private static RGD_Block TryCaptureRgd(Block b) => CaptureRestorableRgd(b);

        // Sign/plaque text (and any TextWriterObject-backed block). Null for everything else.
        private static string TryGetSignText(Block b)
        {
            try { var tw = b.GetComponentInChildren<TextWriterObject>(); return tw != null ? tw.GetText() : null; }
            catch { return null; }
        }

        // Re-apply carried text to the freshly-placed sign via the game's networked setter:
        // SetTextNetworked sets locally AND propagates (host RPCs Other, a client P2Ps the host), so
        // the moved sign keeps its text in single-player and multiplayer. (Vanilla X-remove loses it.)
        private static void ApplySignText(Block nb, string text)
        {
            if (nb == null || string.IsNullOrEmpty(text)) return;
            try
            {
                var tw = nb.GetComponentInChildren<TextWriterObject>();
                if (tw != null) tw.SetTextNetworked(text);
            }
            catch (System.Exception ex) { Log?.LogWarning("sign text restore failed: " + ex.Message); }
        }

        private static Paint CapturePaint(Block b) => new Paint
        {
            cA = b.currentColorA, cB = b.currentColorB,
            pcA = b.currentPatternColorA, pcB = b.currentPatternColorB,
            pi1 = b.currentPatternIndex1, pi2 = b.currentPatternIndex2,
        };

        // Re-apply captured paint to the freshly-placed chest: locally via SetInstanceColorAndPattern
        // (sets both the Block fields - so it persists through save/load - and the material property
        // block, so it shows), and over the network via the vanilla Message_PaintBlock so connected
        // peers recolour their replica. Host RPCs to Other; a client SendP2P's to the host (which
        // re-broadcasts), mirroring PaintBrush. A side is networked only when both its colours are
        // non-null (Message_PaintBlock's ctor reads color.uniqueColorIndex and would NPE on null).
        private static void ApplyPaint(Block nb, Paint p, Network_Player player)
        {
            if (nb == null || !p.Any) return;
            try
            {
                nb.SetInstanceColorAndPattern(p.cA, p.pcA, 1, p.pi1);
                nb.SetInstanceColorAndPattern(p.cB, p.pcB, 2, p.pi2);
            }
            catch (System.Exception ex) { Log?.LogWarning("paint apply failed: " + ex.Message); }

            if (player?.Network == null) return;
            var pos = nb.transform.localPosition;
            try
            {
                if (p.cA != null && p.pcA != null) SendPaint(player, nb.ObjectIndex, pos, p.cA, p.pcA, 1, p.pi1);
                if (p.cB != null && p.pcB != null) SendPaint(player, nb.ObjectIndex, pos, p.cB, p.pcB, 2, p.pi2);
            }
            catch (System.Exception ex) { Log?.LogWarning("paint network failed: " + ex.Message); }
        }

        private static void SendPaint(Network_Player player, uint boi, Vector3 pos,
            SO_ColorValue color, SO_ColorValue patternColor, int side, uint patternIndex)
        {
            var msg = new Message_PaintBlock(Messages.PaintBlock, player, boi, pos, color, patternColor, side, patternIndex);
            if (Raft_Network.IsHost)
                player.Network.RPC(msg, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
            else
                player.SendP2P(msg, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
        }

        // Clear carry state + leave build mode. The original is already removed by the time we call
        // this, so there is nothing to restore.
        private static void ClearCarry()
        {
            moving = null;
            movingItem = null;
            movingSlots = null;
            movingText = null;
            movingRgd = null;
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

        // Fully leave build mode so the player can't keep placing extra copies (BUG3).
        private static void ExitBuildMode()
        {
            var bc = ComponentManager<Network_Player>.Value?.BlockCreator;
            if (bc == null) return;
            bc.SetGhostBlockVisibility(false);
            bc.gameObject.SetActive(false);
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
            var item = movingItem;
            // CRITICAL: use the GHOST's surface-matched DPS (Wall/Floor/Ceiling), NOT the original's
            // stored dps. settings_buildable.GetBlockPrefab(dpsType) picks a DIFFERENT prefab per
            // surface (Placeable_Storage_*_Wall has a holder + a wall-facing stability gizmo;
            // *_Floor's gizmo points down). Rebuilding the floor variant on a wall => no holder and a
            // gizmo that never finds support => IsStable() false forever. The ghost already resolved
            // the correct variant (that's why ghostStable=True), so mirror it.
            var dps = ghost.dpsType;
            var slots = movingSlots;
            var original = moving;
            var player = ComponentManager<Network_Player>.Value;
            if (player == null) { Trace("place: no local player."); return; }

            // PLACE-FIRST + STABILITY GATE (the real fix). Create the new chest while the original is
            // still present but with its colliders DISABLED (hidden during carry) - so the new chest's
            // stability gizmos judge it WITHOUT the original, exactly the post-removal situation.
            // DestroyBlock's cascade only takes blocks that are !IsStable() within the removed block's
            // UnstableCheckDistance, so we remove the original ONLY once the new chest is confirmed
            // stable on its own; a stable block can't be cascaded. If the spot is unsupported (a wall
            // with no holder, an edge, clipping) the new chest is !IsStable() -> we discard it and KEEP
            // the original, so nothing is ever lost. (This was the user's big-chest-on-a-wall loss.)
            if (Raft_Network.IsHost)
            {
                Block nb;
                try { nb = bc.CreateBlockCheat(item, pos, rot, dps, -1); }
                catch (System.Exception ex)
                {
                    Log?.LogWarning("place failed (CreateBlockCheat threw): " + ex.Message + "; chest left where it was.");
                    AbortKeepOriginal();
                    return;
                }

                if (nb == null)
                {
                    Log?.LogWarning("place produced no block; original left where it was.");
                    AbortKeepOriginal();
                    return;
                }

                // place-first + DEFERRED stability gate: nb exists, but the original stays hidden
                // until nb settles to IsStable() over a few physics steps (same-frame reads stale -
                // SyncTransforms wasn't enough; OverlapBox needs real FixedUpdate steps). No removal
                // happens yet, so no cascade yet. PollHostVerify finishes the swap or undoes it.
                _hostNb = nb;
                _hostOriginal = original;
                _hostSlots = slots;
                _hostText = movingText;
                _hostRgd = movingRgd;
                _hostPaint = movingPaint;
                _hostVerifyStart = Time.frameCount;
                _hostVerifyDeadline = Time.frameCount + 120; // ~2s @60fps for physics to settle
                _hostLastLoggedStable = -1;
                _hostVerifying = true;
                Trace($"placing nb@{nb.transform.localPosition.ToString("F2")}; verifying support...");

                // leave build mode but KEEP hidden bookkeeping + pending fields for the poll
                moving = null;
                movingItem = null;
                movingSlots = null;
                ExitBuildMode();
            }
            else
            {
                // CLIENT: full support is storage-only for now - we locate the replicated block via
                // StorageManager. Moving other placeables as a client isn't wired yet, so refuse rather
                // than risk losing remote state. (Host AND single-player move everything.)
                if (!(original is Storage_Small))
                { Note($"moving '{item.UniqueName}' as a client isn't supported yet - host can move it, or move storages."); AbortKeepOriginal(); return; }

                // can't create authoritatively, so KEEP the original (hidden) as a safety net, ask the
                // host to place the new chest, and in PollClientChest remove the original ONLY once the
                // replicated chest is confirmed stable. Unstable/timeout -> restore original.
                if (player.Network == null) { Log?.LogWarning("client move: no Network."); AbortKeepOriginal(); return; }

                _preExisting.Clear();
                if (StorageManager.allStorages != null)
                    foreach (var s in StorageManager.allStorages)
                        if (s != null) _preExisting.Add(s.ObjectIndex);

                var req = new Message_BlockCreator_PlaceBlock(
                    Messages.BlockCreator_PlaceBlock, bc, item.UniqueIndex,
                    0u, 0u, 0u, pos, rot, -1, dps);
                player.SendP2P(req, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);

                _syncSlots = slots;
                _syncPos = pos;
                _syncPaint = movingPaint;
                _pendingClientOriginal = original as Storage_Small; // removed ONLY after the new chest is confirmed stable
                _awaitingClientChest = true;
                _syncDeadlineFrame = Time.frameCount + 600; // ~10s @60fps
                Note($"client: asked host to place '{item.UniqueName}'; original kept until the moved chest is confirmed.");

                // leave build mode but KEEP hidden bookkeeping + pending original for the poll
                moving = null;
                movingItem = null;
                movingSlots = null;
                ExitBuildMode();
            }
        }

        // CLIENT content-sync: after we asked the host to place the chest, watch StorageManager for
        // the new storage (object index not in our pre-place snapshot) nearest the placed spot, then
        // push our carried contents to the host via the vanilla Message_Storage_Close path and apply
        // them to our own view. Times out so we never poll forever.
        // HOST place-first verify: wait for the freshly-placed chest to settle to IsStable() (the
        // same-frame check reads stale - see DIAG), then remove the original. A genuinely unsupported
        // spot never settles -> we undo (discard nb, restore original), losing nothing.
        private static void PollHostVerify()
        {
            if (_hostNb == null)
            {
                RestoreHidden();
                _hostVerifying = false; _hostOriginal = null; _hostSlots = null;
                Log?.LogWarning("host verify: placed chest vanished before settling; original restored, nothing lost.");
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

                // storage contents (storages only): restore locally + sync to peers via Storage_Close.
                if (nb is Storage_Small ns && slots != null)
                {
                    ns.GetInventoryReference()?.SetSlotsFromRGD(slots);
                    if (player?.Network != null && player.StorageManager != null)
                    {
                        try
                        {
                            var sync = new Message_Storage_Close(Messages.StorageManager_Close, player.StorageManager, ns);
                            player.Network.RPC(sync, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        }
                        catch (System.Exception ex) { Log?.LogWarning("host content-sync RPC failed: " + ex.Message); }
                    }
                }
                // generic device state: self-restoring subtypes (purifier tank/battery, cooking progress,
                // fuel tank, wind turbine) via RestoreBlock; cropplots via RestoreCropplot. The game's own
                // load path. Restores paint+health too. Host-local (peers resync via the device / reload).
                ApplyDeviceState(_hostRgd, nb);
                // paint (also networks to peers) + sign text for ANY placeable that has them
                ApplyPaint(nb, _hostPaint, player);
                ApplySignText(nb, _hostText);
                _hiddenColliders.Clear();
                _hiddenRenderers.Clear();
                if (original != null) { try { BlockCreator.RemoveBlockNetwork(original, null, true); } catch { } }

                int restored = slots?.Length ?? 0;
                Note($"placed at {nb.transform.localPosition.ToString("F2")} after settling (+{delta}f, host)" + (restored > 0 ? $"; restored {restored} slots" : ""));
                _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null; _hostText = null; _hostRgd = null;
                return;
            }

            if (Time.frameCount > _hostVerifyDeadline)
            {
                try { BlockCreator.RemoveBlockNetwork(_hostNb, null, true); } catch { }
                RestoreHidden();
                Note("can't place there - it never became supported (+" + delta + "f). Block left where it was.");
                _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null; _hostText = null; _hostRgd = null;
            }
        }

        private static void PollClientChest()
        {
            if (Time.frameCount > _syncDeadlineFrame)
            {
                // host never spawned the chest -> un-hide the original we kept; nothing lost
                RestoreHidden();
                _awaitingClientChest = false; _syncSlots = null; _pendingClientOriginal = null;
                Log?.LogWarning("client sync: host didn't place the chest in time; original restored, nothing lost.");
                return;
            }
            if (StorageManager.allStorages == null) return;

            Storage_Small best = null;
            float bestSqr = 1f; // within ~1 unit of where we placed the ghost
            foreach (var s in StorageManager.allStorages)
            {
                if (s == null || _preExisting.Contains(s.ObjectIndex)) continue;
                float d = (s.transform.localPosition - _syncPos).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = s; }
            }
            if (best == null) return; // host's reply hasn't spawned it yet

            var player = ComponentManager<Network_Player>.Value;
            if (player?.Network == null) { RestoreHidden(); _awaitingClientChest = false; _syncSlots = null; _pendingClientOriginal = null; return; }

            if (!best.IsStable())
            {
                // unsupported spot: the chest would be cascaded when we remove the original, so
                // discard it and keep the original instead. Nothing lost.
                try { BlockCreator.RemoveBlockNetwork(best, null, true); } catch { }
                RestoreHidden();
                _awaitingClientChest = false; _syncSlots = null; _pendingClientOriginal = null;
                Note("client: that spot wouldn't be supported; chest left where it was.");
                return;
            }

            if (_syncSlots != null) best.GetInventoryReference()?.SetSlotsFromRGD(_syncSlots); // our local view
            ApplyPaint(best, _syncPaint, player);
            try
            {
                if (player.StorageManager != null)
                {
                    var msg = new Message_Storage_Close(Messages.StorageManager_Close, player.StorageManager, best);
                    player.SendP2P(msg, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                }
                Note($"client sync: pushed {_syncSlots?.Length ?? 0} slots for chest #{best.ObjectIndex}.");
            }
            catch (System.Exception ex) { Log?.LogWarning("client sync RPC failed: " + ex.Message); }

            // stable + filled -> NOW it is safe to delete the original we kept hidden
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            if (_pendingClientOriginal != null)
            {
                try { BlockCreator.RemoveBlockNetwork(_pendingClientOriginal, null, true); } catch { }
                _pendingClientOriginal = null;
            }
            _awaitingClientChest = false; _syncSlots = null;
        }
    }

    // Draws a "<key> Move" hint under the vanilla "Open" prompt whenever the player aims at a closed
    // storage. Postfix so it runs right after Storage_Small.OnIsRayed set its own prompt (deterministic
    // ordering, no flicker); clearAllTexts:false keeps the game's index-0 prompt and stacks ours at
    // index 1. The game's OnRayExit/HideDisplayTexts clears both when you look away.
    [HarmonyPatch(typeof(Storage_Small), nameof(Storage_Small.OnIsRayed))]
    internal static class Patch_StorageMoveHint
    {
        private static void Postfix(Storage_Small __instance)
        {
            try
            {
                if (Plugin.moving != null) return;                       // already carrying one
                if (CanvasHelper.ActiveMenu != MenuType.None) return;    // a menu is open
                if (__instance == null || __instance.IsOpen) return;    // chest is open
                var dtm = ComponentManager<DisplayTextManager>.Value;
                if (dtm == null) return;
                dtm.ShowText("Move", Plugin.MoveKey.Value.MainKey, 1, 0, false);
            }
            catch { }
        }
    }

    // Our own guaranteed per-frame ticker, independent of BaseUnityPlugin.Update, surviving scene loads.
    public class Ticker : MonoBehaviour
    {
        private void Update() => Plugin.Tick();
        private void LateUpdate() => Plugin.LateTick();
    }
}
