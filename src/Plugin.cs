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

    [BepInPlugin(Guid, "Movable Storages", "1.2.0")]
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
            var dps = movingDps;
            var slots = movingSlots;
            var original = moving;
            var player = ComponentManager<Network_Player>.Value;

            // The original is about to be destroyed; drop the hide-bookkeeping (nothing to restore).
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            // DUPE/REFUND FIX: RemoveBlockCoroutine only refunds (chest item, contents, OR — when
            // itemToReturnOnDestroy is null — 50% of the recipe materials) inside a branch gated on
            // `playerRemovingBlock != null`. Passing NULL skips that branch entirely: no item, no
            // recipe, no contents. DestroyBlock/RemoveUnstableBlockNetworked are null-safe and
            // DestroyImmediate runs unconditionally, so the block is still removed. (Decompile-
            // verified; contents already captured into `slots`.)
            BlockCreator.RemoveBlockNetwork(original, null, true);

            if (Raft_Network.IsHost)
            {
                // HOST fast-path: CreateBlockCheat mints authoritative unique indices and RPCs a
                // Message_BlockCreator_PlaceBlock to all clients (plain CreateBlock(...,0,0,0) is
                // local-only — remotes just saw the original vanish). Then push contents via the
                // vanilla Message_Storage_Close (its ctor grabs GetRGDSlots, receiver SetSlotsFromRGD).
                var nb = bc.CreateBlockCheat(item, pos, rot, dps, -1);
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
                    Note($"placed '{item.UniqueName}' at {pos}; restored {slots.Length} slots (host, networked).");
                }
                else
                {
                    Log?.LogWarning($"placed '{item?.UniqueName}' but new block is not Storage_Small (nb={(nb == null ? "null" : nb.GetType().Name)}).");
                }
            }
            else
            {
                // CLIENT path: a client can't mint authoritative indices, so it does what vanilla
                // co-op building does — SendP2P a place REQUEST to the host (0 indices). The host
                // creates the chest authoritatively and replicates it back to everyone, including us.
                // We don't know the new ObjectIndex yet, so we snapshot existing storages and poll
                // (PollClientChest) for the new one near `pos`, then push its contents.
                if (player?.Network == null) { Log?.LogWarning("client move: no Network_Player/Network."); }
                else
                {
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
                    _awaitingClientChest = slots != null;
                    _syncDeadlineFrame = Time.frameCount + 600; // ~10s @60fps
                    Note($"client: asked host to place '{item.UniqueName}'; awaiting chest to sync {slots?.Length ?? 0} slots.");
                }
            }

            // clear state WITHOUT re-activating the (now removed) original, and exit build mode (BUG3)
            moving = null;
            movingItem = null;
            movingSlots = null;
            ExitBuildMode();
        }

        // CLIENT content-sync: after we asked the host to place the chest, watch StorageManager for
        // the new storage (object index not in our pre-place snapshot) nearest the placed spot, then
        // push our carried contents to the host via the vanilla Message_Storage_Close path and apply
        // them to our own view. Times out so we never poll forever.
        private static void PollClientChest()
        {
            if (Time.frameCount > _syncDeadlineFrame)
            {
                _awaitingClientChest = false; _syncSlots = null;
                Log?.LogWarning("client sync: timed out waiting for the host to spawn the moved chest.");
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
            if (player?.Network == null) { _awaitingClientChest = false; _syncSlots = null; return; }

            best.GetInventoryReference()?.SetSlotsFromRGD(_syncSlots); // our local view
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

            _awaitingClientChest = false; _syncSlots = null;
        }
    }

    // Our own guaranteed per-frame ticker, independent of BaseUnityPlugin.Update, surviving scene loads.
    public class Ticker : MonoBehaviour
    {
        private void Update() => Plugin.Tick();
    }
}
