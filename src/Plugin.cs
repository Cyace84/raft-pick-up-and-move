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
    //   * Therefore ALL per-frame logic lives on our own DontDestroyOnLoad `Ticker` GameObject, and we
    //     use NO Harmony at all. Placement is handled by reading the vanilla ghost (BlockCreator.selectedBlock)
    //     directly on left-click. Vanilla cannot double-place the chest because the storage item is not in
    //     the player's inventory while it is being carried.
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

    [BepInPlugin(Guid, "Movable Storages", "1.2.7")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.cyace84.raftmovablestorage";

        public static ConfigEntry<KeyboardShortcut> MoveKey;
        public static ManualLogSource Log;

        // carry-mode state
        internal static Storage_Small moving;
        internal static Item_Base movingItem;
        internal static DPS movingDps;
        internal static RGD_Slot[] movingSlots;
        // renderers/colliders we disabled to hide the original while carrying (restored on cancel)
        private static readonly List<Collider> _hiddenColliders = new List<Collider>();
        private static readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

        // CLIENT (non-host) placement is async: we ask the host to place the chest, then must wait
        // for the host's reply to spawn it locally before we can push its contents. State for that.
        private static bool _awaitingClientChest;
        private static RGD_Slot[] _syncSlots;
        private static Vector3 _syncPos;
        private static int _syncDeadlineFrame;
        private static readonly HashSet<uint> _preExisting = new HashSet<uint>();
        // the original kept ALIVE (only hidden) on the client until the replicated chest is confirmed
        // STABLE; removed only then, restored on failure/timeout -> never lost (DATA-LOSS FIX)
        private static Storage_Small _pendingClientOriginal;
        // HOST place-first verify: nb is created but the original is removed ONLY once nb settles to
        // IsStable() over a few physics steps (the same-frame check reads stale - see DIAG). The
        // original stays hidden until then, so a never-settling spot is undone with nothing lost.
        private static bool _hostVerifying;
        private static Storage_Small _hostNb;
        private static Block _hostOriginal;
        private static RGD_Slot[] _hostSlots;
        private static int _hostVerifyStart;
        private static int _hostVerifyDeadline;
        private static int _hostLastLoggedStable; // -1 unknown, 0 false, 1 true (log only on change)

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

            Note($"{Info.Metadata.Name} {Info.Metadata.Version} loaded. Move key = {MoveKey.Value}.");
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
                if (moving != null) { Trace("hotkey: cancel carry."); CancelMove(); }
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

            var storage = hit.collider.GetComponentInParent<Storage_Small>();
            if (storage == null)
            { Trace($"begin: hit '{hit.collider.name}' but no Storage_Small in parents."); return; }
            if (storage.buildableItem == null)
            { Trace("begin: storage has null buildableItem."); return; }

            var inv = storage.GetInventoryReference();
            movingSlots = inv != null ? inv.GetRGDSlots() : null;
            movingItem = storage.buildableItem;
            movingDps = storage.dpsType;
            moving = storage;

            // BUG1: hide the original while carrying so its mesh+collider don't block placing the
            // ghost on/near its own spot. NOTE: do NOT SetActive(false) the GameObject — that
            // de-registers the block and makes RemoveBlockNetwork silently fail (proven via eval:
            // it caused the duplicate). Disabling only Renderers+Colliders keeps registration intact.
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            foreach (var c in storage.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; _hiddenColliders.Add(c); }
            foreach (var r in storage.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            // make sure the build creator is active so the ghost shows, then engage it
            var bc = player.BlockCreator;
            if (bc != null && !bc.gameObject.activeSelf) bc.gameObject.SetActive(true);
            bc?.SetBlockTypeToBuild(movingItem);

            Note($"Carrying '{movingItem.UniqueName}' ({(movingSlots?.Length ?? 0)} slots). LMB to place.");
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
        }

        private static void RestoreHidden()
        {
            foreach (var c in _hiddenColliders) if (c != null) c.enabled = true;
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
        }

        // Clear carry state + leave build mode. The original is already removed by the time we call
        // this, so there is nothing to restore.
        private static void ClearCarry()
        {
            moving = null;
            movingItem = null;
            movingSlots = null;
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

                if (!(nb is Storage_Small ns))
                {
                    if (nb != null) { try { BlockCreator.RemoveBlockNetwork(nb, null, true); } catch { } }
                    Log?.LogWarning($"place produced no storage (nb={(nb == null ? "null" : nb.GetType().Name)}); chest left where it was.");
                    AbortKeepOriginal();
                    return;
                }

                // place-first + DEFERRED stability gate: nb exists, but the original stays hidden
                // until nb settles to IsStable() over a few physics steps (same-frame reads stale -
                // SyncTransforms wasn't enough; OverlapBox needs real FixedUpdate steps). No removal
                // happens yet, so no cascade yet. PollHostVerify finishes the swap or undoes it.
                _hostNb = ns;
                _hostOriginal = original;
                _hostSlots = slots;
                _hostVerifyStart = Time.frameCount;
                _hostVerifyDeadline = Time.frameCount + 120; // ~2s @60fps for physics to settle
                _hostLastLoggedStable = -1;
                _hostVerifying = true;
                Trace($"placing nb@{ns.transform.localPosition.ToString("F2")}; verifying support...");

                // leave build mode but KEEP hidden bookkeeping + pending fields for the poll
                moving = null;
                movingItem = null;
                movingSlots = null;
                ExitBuildMode();
            }
            else
            {
                // CLIENT: can't create authoritatively, so KEEP the original (hidden) as a safety net,
                // ask the host to place the new chest, and in PollClientChest remove the original ONLY
                // once the replicated chest is confirmed stable. Unstable/timeout -> restore original.
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
                _pendingClientOriginal = original;        // removed ONLY after the new chest is confirmed stable
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
                var ns = _hostNb;
                var original = _hostOriginal;
                var slots = _hostSlots;
                var player = ComponentManager<Network_Player>.Value;

                if (slots != null) ns.GetInventoryReference()?.SetSlotsFromRGD(slots);
                _hiddenColliders.Clear();
                _hiddenRenderers.Clear();
                if (original != null) { try { BlockCreator.RemoveBlockNetwork(original, null, true); } catch { } }

                if (slots != null && player?.Network != null && player.StorageManager != null)
                {
                    try
                    {
                        var sync = new Message_Storage_Close(Messages.StorageManager_Close, player.StorageManager, ns);
                        player.Network.RPC(sync, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                    }
                    catch (System.Exception ex) { Log?.LogWarning("host content-sync RPC failed: " + ex.Message); }
                }
                Note($"placed at {ns.transform.localPosition.ToString("F2")}; restored {slots?.Length ?? 0} slots after settling (+{delta}f, host).");
                _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null;
                return;
            }

            if (Time.frameCount > _hostVerifyDeadline)
            {
                try { BlockCreator.RemoveBlockNetwork(_hostNb, null, true); } catch { }
                RestoreHidden();
                Note("can't place there - it never became supported (+" + delta + "f). Chest left where it was.");
                _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null;
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

    // Our own guaranteed per-frame ticker, independent of BaseUnityPlugin.Update, surviving scene loads.
    public class Ticker : MonoBehaviour
    {
        private void Update() => Plugin.Tick();
    }
}
