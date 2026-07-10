using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
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
    // replicated chest to sync contents (see ConfirmMove / PollClientMove). Contents travel via
    // the vanilla Message_Storage_Close path. Only the player moving a chest needs the mod.

    [BepInPlugin(Guid, "Pick Up & Move", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.cyace84.pickupmove";

        public static ConfigEntry<KeyboardShortcut> MoveKey;
        public static ConfigEntry<bool> RelayLogs;
        public static ConfigEntry<bool> LogToConsole;
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
        private static readonly List<Canvas> _hiddenCanvases = new List<Canvas>();   // device screens (Reciever radar) aren't Renderers


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
        // [t] timing probes (OPEN BUG: ~5s first-move-of-type delay) + refusal-relay state
        private static float _cmSentTime;          // Time.realtimeSinceStartup when the request left
        private static bool _cmOrigGoneLogged;
        private static bool _cmSeenLogged;
        private static Steamworks.CSteamID _hostReqSender; // valid = current verify is a client request
        private static float _hostReqRecvTime;
        private static readonly HashSet<uint> _preExisting = new HashSet<uint>();
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
        private static float _hostVerifyDeadlineTime; // WALL-CLOCK deadline: frame-based (120f) was ~2s@60fps
        // but 4s@30fps (CrossOver host), and the recycler settles at ~119-121f - exactly ON the frame
        // deadline (observed: '+121f' fail vs 4.01s success on identical moves).
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

            LogToConsole = Config.Bind(
                "Logging",
                "LogToConsole",
                false,
                "Show this mod's diagnostic lines in the BepInEx console / LogOutput.log. Off by default " +
                "for a quiet game; turn on to watch what the mod is doing. Warnings and errors always show, " +
                "and the one load line (with the build stamp) always shows.");
            LogConsole = LogToConsole.Value;

            RelayLogs = Config.Bind(
                "Logging",
                "RelayLogs",
                false,
                "Debug aid, off by default. Write this mod's lines to per-session files " +
                "(BepInEx/PickUpMoveLogs/) and, when playing as a client, relay them to the host so a " +
                "co-op issue can be diagnosed from one machine. Only this mod's own lines are sent.");
            LogRelay.Init(RelayLogs.Value);

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
            catch (System.Exception ex) { Warn("Harmony patch failed (Move hint disabled, core feature unaffected): " + ex.Message); }

            Announce($"{Info.Metadata.Name} {Info.Metadata.Version} (build {BuildStamp.Value}) loaded. Move key = {MoveKey.Value}.");
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
                    if (go != null && (go.name == "RMS_Ticker" || go.name == "PickUpMove_Note" || go.name == "PUM_GhostPreview")) UnityEngine.Object.Destroy(go);
            }
            catch { }
            _tickerGo = null;
            moving = null; movingItem = null; movingSlots = null;
            _hostVerifying = false;
            _pickupScan = null; _reqScan = null; _carryDeps.Clear();
        }

        // Info = user-facing milestones; Trace = diagnostic non-events (missed raycast, bad spot).
        // Note() was LOG-ONLY until tp6; the in-game channel went through two dead ends before
        // landing here (all confirmed by live eval, not theory):
        //   chat tp6: LocalDebugChatMessage never wakes the faded panel (no UpdateChatVisiblity);
        //   chat tp7: CreateLocalChatMessage wakes it - but ONLY if GeneralSettingsBox.ChatVisible,
        //             and the user (and likely most players) has chat OFF -> _ChatCanvas alpha==0.
        // So: DisplayTextManager, the same HUD slot that draws 'M Move' and provably renders.
        // Note() stores the line; LateTick paints it at displayIndex 1 for ~2.5s.
        // TWO channels (tp9): Note = status chatter, LOG ONLY ('Carrying...', 'moved to...', '[t]');
        // NoteHud = player-actionable feedback (refusals, declines, timeouts) - log + 2.5s HUD line.
        // tp8 painted EVERY note on the HUD and the chatter drowned the screen.
        private static string _hudNote; private static float _hudNoteUntil;
        // Two INDEPENDENT log sinks, each config-gated:
        //   console (LogToConsole)  -> BepInEx console / LogOutput.log. Off by default for a quiet
        //                              release; warnings+errors ALWAYS surface so a bug report keeps them.
        //   relay   (RelayLogs)     -> per-session files + client->host relay (see LogRelay). Off by default.
        // Note/Trace/Warn/Err all funnel through Emit; nothing else in the mod touches Log directly
        // (except Announce, the one always-on load banner that build-stamp verification reads back).
        internal static bool LogConsole;   // = LogToConsole.Value, set in Awake
        internal static void Note(string msg)  => Emit(LogLevel.Info, msg);
        internal static void Trace(string msg) => Emit(LogLevel.Debug, msg);
        internal static void Warn(string msg)  => Emit(LogLevel.Warning, msg);
        internal static void Err(string msg)   => Emit(LogLevel.Error, msg);
        private static void Emit(LogLevel lvl, string msg)
        {
            if (LogConsole || lvl >= LogLevel.Warning) Log?.Log(lvl, msg);
            LogRelay.Record(lvl, msg);
        }
        // Always visible regardless of LogToConsole: the single load line (raft-ship reads the build
        // stamp back from it) and anything a supporter must see even in a quiet install.
        internal static void Announce(string msg) { Log?.Log(LogLevel.Info, msg); LogRelay.Record(LogLevel.Info, msg); }
        internal static void NoteHud(string msg)
        {
            Note(msg); // log part gated by LogToConsole; the HUD line below is player feedback, never gated
            _hudNote = msg; _hudNoteUntil = Time.realtimeSinceStartup + 2.5f;
        }

        // Per-frame logic, driven by Ticker.
        internal static void Tick()
        {
            LogRelay.Tick();
            if (Raft_Network.IsHost) { PollMoveRequests(); ProcessTeleportResends(); } else { PollMoveRefusals(); SendHello(); }
            // vanilla-style dependent scans need a settle frame between collider toggles and IsStable
            // reads (DestroyBlock does 'yield return null' for the same reason) - step them before
            // anything below can early-return.
            if (_reqScan != null) StepDepScan(_reqScan);
            if (_pickupScan != null) StepDepScan(_pickupScan);
            if (_awaitingHostMove) PollClientMove();
            if (_tpVerifying) { PollTeleportVerify(); return; }
            if (_hostVerifying) { PollHostVerify(); return; }

            if (MoveKey.Value.IsDown())
            {
                if (moving != null) CancelMove();
                // Don't start a new move while the previous one is still resolving: the hide-bookkeeping
                // (_hidden* lists) and the single pending-original fields are shared, so overlapping moves
                // corrupt them - the prior original loses its restore info (stays invisible on the client)
                // or its removal reference (never removed => duplicate). Moves are near-instant when they
                // work, and every pending state self-resolves on a <=10s timeout, so this never locks up.
                else if (_awaitingHostMove || _hostVerifying || _tpVerifying || _reqScan != null)
                    NoteHud(Loc.T("busy"));
                else TryBeginMove();
                return;
            }

            if (moving == null) { _rearmBuild = false; return; }

            // re-arm the ghost a refusal suppressed last frame (carry continues, see SuppressVanillaPlaceThisFrame)
            if (_rearmBuild)
            {
                _rearmBuild = false;
                ComponentManager<Network_Player>.Value?.BlockCreator?.SetBlockTypeToBuild(movingItem);
            }

            // carrying: right-click cancels, left-click confirms placement at the ghost
            if (Input.GetMouseButtonDown(1)) { Trace("rmb: cancel carry."); CancelMove(); return; }
            if (Input.GetMouseButtonDown(0))
            {
                // don't confirm while the pickup dep-scan is still settling (a 2-3 frame window):
                // _carryDeps would be incomplete and the stack would be left behind
                if (_pickupScan != null) { NoteHud(Loc.T("busy")); return; }
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
            // STACK: snapshot neighbours' stability BEFORE hiding the original (flip-test dep scan;
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
            bc?.SetBlockTypeToBuild(movingItem);

            Note($"Carrying '{movingItem.UniqueName}'" + ((movingSlots?.Length ?? 0) > 0 ? $" ({movingSlots.Length} slots)" : "") + ". LMB to place.");
        }

        internal static void CancelMove()
        {
            if (moving == null) return;
            if (_pickupScan != null) FinishDepScan(_pickupScan, abort: true);
            _carryDeps.Clear();
            // restore the hidden original (BUG1)
            RestoreHidden();
            ExitBuildMode();
            moving = null;
            movingItem = null;
            movingSlots = null;
            movingText = null;
            movingRgd = null;
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
            // ghost previews FIRST: the hud-note branch below early-returns while a note is shown,
            // and the previews must keep following the ghost through refusal notes mid-carry.
            UpdateGhostPreviews();
            // transient user-feedback line first: it must show even mid-carry (refusals fire then)
            if (_hudNote != null)
            {
                if (Time.realtimeSinceStartup < _hudNoteUntil)
                {
                    // OWN label under the prompt bar. Vanilla DisplayText slots can't do 'a line
                    // below': eval showed slot 0 = middle-of-screen, slots 1-3 = ONE horizontal
                    // bottom row (2 and 3 even share x=287) - any slot we take reflows/steals the
                    // vanilla 'X' prompt in that same row (observed tp8-tp11).
                    try
                    {
                        EnsureHudLabel();
                        if (_hudLabel != null)
                        {
                            if (_hudLabel.text != _hudNote) _hudLabel.text = _hudNote;
                            if (!_hudLabel.gameObject.activeSelf) _hudLabel.gameObject.SetActive(true);
                        }
                    }
                    catch { }
                    return;
                }
                _hudNote = null;
                try { if (_hudLabel != null) _hudLabel.gameObject.SetActive(false); } catch { }
            }
            if (moving != null || _hostVerifying) { ClearHintIfShown(); return; }
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
            dtm.ShowText(Loc.T("move"), MoveKey.Value.MainKey, 1, 0, false);
            _hintShown = true;
        }
        // One legacy-uGUI Text ABOVE the vanilla bottom-prompt row, styled from the prompt font.
        // Recreated lazily per world (scene unload destroys it -> Unity fake-null -> rebuilt).
        // Placement measured live (monolab eval): DisplayTextBottom occupies world-y 154..241 on a
        // 945px screen and nearly TOUCHES the hotbar (~16px gap) - there is NO clean band BELOW the
        // prompts, so the note goes ABOVE them. Canvas ref height 1080 -> scale .875; NoteY canvas
        // units * .875 = world y. 300 -> world ~262, just above the prompt row, far above the hotbar,
        // clear of the centre text (world ~521). Bottom-centre anchored, independent of the bar.
        private const float NoteY = 300f; // canvas units above screen bottom; single tuning knob
        private static UnityEngine.UI.Text _hudLabel;
        private static void EnsureHudLabel()
        {
            if (_hudLabel != null) return;
            var dtm = ComponentManager<DisplayTextManager>.Value;
            if (dtm == null) return;
            var arr = (DisplayText[])AccessTools.Field(typeof(DisplayTextManager), "displayTexts").GetValue(dtm);
            if (arr == null || arr.Length < 2 || arr[1] == null) return;
            var template = arr[1].GetComponentInChildren<UnityEngine.UI.Text>(true);
            var bottom = arr[1].transform.parent as RectTransform; // DisplayTextBottom
            if (template == null || bottom == null) return;
            var go = new GameObject("PickUpMove_Note", typeof(RectTransform));
            go.transform.SetParent(bottom.parent, false); // sibling of the bar - no layout group touches us
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f); // bottom-centre of the screen
            rt.anchoredPosition = new Vector2(0f, NoteY);
            rt.sizeDelta = new Vector2(1400f, 44f);
            _hudLabel = go.AddComponent<UnityEngine.UI.Text>();
            _hudLabel.font = template.font;
            _hudLabel.fontSize = template.fontSize;
            _hudLabel.color = template.color;
            _hudLabel.alignment = TextAnchor.MiddleCenter;
            _hudLabel.raycastTarget = false;
            go.SetActive(false);
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
                // The game persists per-instance state ONLY through Serialize_Save (-> an RGD_Block).
                // So scan every RGD any component on this block would save: a plain RGD_Block (paint/
                // health, covered by base restore + ApplyPaint) or null means nothing to lose; a SUBTYPE
                // we can replay is fine; a stateful subtype we don't handle is the only thing we refuse.
                // This lets stateless-but-networked decor (chairs, etc.) move, without ever guessing.
                foreach (var rgd in CollectSavedRgds(b))
                {
                    if (rgd == null || rgd.GetType() == typeof(RGD_Block)) continue;
                    if (CanRestoreDevice(rgd)) continue;
                    return true;                                                      // stateful subtype, unhandled
                }
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
                || rgd is RGD_Cropplot || rgd is RGD_Block_Sprinkler || rgd is RGD_Block_Recycler
                || rgd is RGD_ResearchTable || rgd is RGD_Reciever || rgd is RGDTrophyHolder
                || rgd is RGD_Sail || rgd is RGD_SteeringWheel || rgd is RGD_MotorWheel
                || rgd is RGD_AnchorStationary || rgd is RGD_AnchorThrowable);

        // Deregister the networkIDs of every plant on a block's cropplot(s) BEFORE the block is
        // removed. Nothing in the game does this on block destruction (MonoBehaviour_ID.OnDestroy is
        // empty; RemovePickupItem runs only on the pickup path), so destroyed plants leave fake-null
        // entries in NetworkIDManager under the SAME ObjectIndex the restored plants re-use.
        // GetNetworkIDFromObjectIndex returns the FIRST index match, and the caller's '!= null' gate
        // is Unity's overloaded operator - a dead entry shadows the live plant and harvest silently
        // breaks. The HOST performs exactly that lookup when a CLIENT harvests (Message_HarvestPlant),
        // so the host bucket must hold only live entries.
        private static void DeregisterCropplotPlants(Block b)
        {
            if (b == null) return;
            try
            {
                var plots = b.GetComponentsInChildren<Cropplot>(true);
                if (plots == null) return;
                int removed = 0;
                foreach (var plot in plots)
                {
                    if (plot == null || plot.plantationSlots == null) continue;
                    foreach (var slot in plot.plantationSlots)
                    {
                        var nid = slot?.plant?.pickupComponent?.networkID;
                        if (nid != null) { NetworkIDManager.RemoveNetworkID(nid, typeof(PickupItem_Networked)); removed++; }
                    }
                }
                if (removed > 0) Note($"[cropdiag] dereg {removed} plant networkID(s) before removing block #{b.ObjectIndex}");
            }
            catch (System.Exception ex) { Warn("[cropdiag] dereg " + ex.Message); }
        }

        // HOST -> clients: re-announce every plant on the moved cropplot(s) through the game's own
        // planting pipeline, AFTER the original block was removed (reliable Channel_Game keeps the
        // order: remove-original arrives first, so the re-used plantObjectIndex has a single holder on
        // the client). Message_PlantSeed makes the client create+REGISTER the plant (the same path
        // vanilla uses when the host plants/refills - client harvest provably works there), and
        // Message_Plant_Complete matures the fully-grown ones. Partially-grown plants re-grow client-
        // side and sync at maturity via the host's own Plant.Grow broadcast.
        private static void FlushPlantBroadcasts(Network_Player player)
        {
            if (_plantBroadcastPlots.Count == 0) return;
            try
            {
                var net = player?.Network;
                var pm = player?.PlantManager;
                if (net == null || pm == null) { _plantBroadcastPlots.Clear(); return; }
                foreach (var plot in _plantBroadcastPlots)
                {
                    if (plot == null || plot.plantationSlots == null) continue;
                    // Message_PlantSeed carries NO slot index: the client's handler plants into the
                    // FIRST FREE slot of the (fresh, empty) replica, so the k-th seed we send lands in
                    // slot k-1 - regardless of which slots the plants occupy on the host (harvested
                    // gaps survive the restore there). Address Message_Plant_Complete at the CLIENT's
                    // slot, not the host's, or a completion aimed at a gap silently no-ops and the
                    // plant stays a seedling on the client (observed: 1 of 3 flowers reset).
                    int clientSlot = 0;
                    foreach (var slot in plot.plantationSlots)
                    {
                        var plant = slot?.plant;
                        var nid = plant?.pickupComponent?.networkID;
                        if (plant == null || nid == null) continue;
                        // FRESH index - the invariant vanilla maintains (Deserialize(PlantSeed) always
                        // assigns a new unique index per planting). Re-using the original's index left
                        // DEAD registry entries on the CLIENT (its original plants die with the removed
                        // block and nothing deregisters them), and GetNetworkIDFromObjectIndex hits the
                        // dead entry first -> Unity-fake-null -> the moved plant can't be interacted with.
                        // The registry stores the object; its index is a field, so reassigning is enough
                        // host-side too.
                        nid.ObjectIndex = SaveAndLoad.GetUniqueObjectIndex();
                        var seed = new Message_PlantSeed(Messages.PlantManager_PlantSeed, pm, plot, plant, slot.hasWater);
                        seed.plantObjectIndex = nid.ObjectIndex; // client registers under the host's fresh index -> harvest resolves
                        net.RPC(seed, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        if (plant.FullyGrown())
                        {
                            var done = new Message_Plant_Complete(Messages.PlantManager_PlantComplete, pm, plot, plant);
                            done.plantationSlotIndex = clientSlot; // the slot the client's first-free fill just used
                            net.RPC(done, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        }
                        clientSlot++;
                    }
                }
            }
            catch (System.Exception ex) { Warn("plant broadcast: " + ex.Message); }
            _plantBroadcastPlots.Clear();
        }

        // [stab] dump per-gizmo support for a block that reads !IsStable (multi-cell devices like the
        // recycler have several gizmo boxes; requireAll means ONE unsupported cell fails the whole block).
        private static void LogStability(Block b)
        {
            if (b == null) return;
            try
            {
                foreach (var sc in b.GetComponentsInChildren<StableComponent>(true))
                {
                    int total = sc.requiredGizmoColliders?.Length ?? 0;
                    int hits = 0;
                    var miss = new System.Text.StringBuilder();
                    for (int i = 0; i < total; i++)
                    {
                        bool hit = false;
                        try { hit = sc.requiredGizmoColliders[i].IsColliding(false, LayerMasks.MASK_Block, 0.99f); } catch { }
                        if (hit) hits++; else { miss.Append(i); miss.Append(' '); }
                    }
                    Note($"[stab] '{b.buildableItem?.UniqueName}' comp '{sc.gameObject.name}': hits={hits}/{total}"
                        + $" requireAll={sc.requireAll} need={sc.requiredHitCount}" + (miss.Length > 0 ? $" missing: {miss}" : ""));
                }
            }
            catch (System.Exception ex) { Warn("[stab] " + ex.Message); }
        }

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
                        // CLIENT: do NOT re-plant locally. A client-side PlantSeed never lands in the
                        // registry the harvest lookup reads (observed: plantPlaced=True, correct index,
                        // registered=False even after an explicit AddNetworkID) - the un-harvestable-plant
                        // bug. Instead the HOST re-broadcasts every restored plant through the game's own
                        // Message_PlantSeed/_Plant_Complete after the original is removed (FlushPlantBroadcasts),
                        // the exact path vanilla uses to show host plantings to clients.
                        if (!Raft_Network.IsHost) break;
                        var plot = nb.GetComponent<Cropplot>();
                        var pm = ComponentManager<Network_Player>.Value?.PlantManager;
                        if (plot != null && pm != null)
                        {
                            rc.RestoreCropplot(plot, pm);

                            // Harvest resolves plants by index: PlantManager.Deserialize(HarvestPlant) ->
                            // NetworkIDManager.GetNetworkIDFromObjectIndex<PickupItem_Networked>(idx), and the
                            // '!= null' gate there is Unity's overloaded operator. Nothing deregisters a plant
                            // when its block is destroyed, so a DESTROYED original plant keeps a stale entry
                            // under the SAME ObjectIndex and shadows the restored one (harvest silently breaks).
                            // Purge DEAD same-index entries (live ones - the hidden original pre-removal - are
                            // kept: an undone move must not lose the surviving original), then register ours.
                            try
                            {
                                var dictField = typeof(NetworkIDManager).GetField("networkIDs",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                var byTag = (dictField?.GetValue(null) as System.Collections.IDictionary)
                                    ?[typeof(PickupItem_Networked)] as System.Collections.IDictionary;
                                if (rc.plantationSlots != null)
                                    foreach (var ps in rc.plantationSlots)
                                    {
                                        if (ps == null || ps.uniquePlantIndex == -1) continue;
                                        Plant placed = (ps.plantationSlotIndex >= 0 && ps.plantationSlotIndex < (plot.plantationSlots?.Count ?? 0))
                                            ? plot.plantationSlots[ps.plantationSlotIndex].plant : null;
                                        var nid = placed?.pickupComponent?.networkID;
                                        if (nid == null) continue;
                                        uint want = ps.plantObjectIndex;
                                        if (byTag != null)
                                        {
                                            var stale = new List<MonoBehaviour_ID_Network>();
                                            foreach (System.Collections.DictionaryEntry de in byTag)
                                                foreach (MonoBehaviour_ID_Network item in (System.Collections.IEnumerable)de.Value)
                                                {
                                                    if (object.ReferenceEquals(item, nid) || item.ObjectIndex != want) continue;
                                                    if (item == null) stale.Add(item); // Unity-fake-null: destroyed shadow
                                                }
                                            foreach (var s in stale) NetworkIDManager.RemoveNetworkID(s, typeof(PickupItem_Networked));
                                        }
                                        NetworkIDManager.AddNetworkID(nid); // idempotent - HashSet add
                                    }
                            }
                            catch (System.Exception ex) { Warn("plant re-register: " + ex.Message); }

                            // queue for the post-removal broadcast to clients (see FlushPlantBroadcasts)
                            _plantBroadcastPlots.Add(plot);
                        }
                        break;
                    case RGD_Block_Sprinkler rs:
                        var spr = nb.GetComponent<Sprinkler>();
                        if (spr != null) rs.rgdSprinkler?.RestoreSprinkler(spr);
                        break;
                    case RGD_Block_Recycler rr:
                        var ext = nb.GetComponent<Placeable_Extractor>();
                        if (ext != null) rr.rgdRecycler?.RestoreExtractor(ext);
                        break;
                    case RGD_ResearchTable rt:
                        var table = nb.GetComponent<ResearchTable>();
                        if (table != null) rt.RestoreResearchTable(table);
                        break;
                    case RGDTrophyHolder th:
                        var holder = nb.GetComponentInChildren<TrophyHolder>();
                        if (holder != null) th.RestoreTrophyHolder(holder);
                        break;
                    case RGD_Sail sa:
                        var sail = nb.GetComponentInChildren<Sail>();
                        if (sail != null) sa.RestoreSail(sail);
                        break;
                    case RGD_SteeringWheel sw:
                        var wheel = nb.GetComponentInChildren<SteeringWheel>();
                        if (wheel != null) wheel.RestoreWheel(sw);
                        break;
                    case RGD_MotorWheel mw:
                        var motor = nb.GetComponentInChildren<MotorWheel>();
                        if (motor != null) motor.RestoreMotor(mw);   // fuel + throttle/heading settings
                        break;
                    case RGD_AnchorStationary asta:
                        var anchorS = nb.GetComponentInChildren<Anchor_Stationary>();
                        if (anchorS != null) asta.RestoreAnchor(anchorS);
                        break;
                    case RGD_AnchorThrowable ath:
                        var anchorT = nb.GetComponentInChildren<Anchor_Throwable_Stand>();
                        if (anchorT != null) ath.RestoreAnchorThrowable(nb, anchorT);
                        break;
                    case RGD_Reciever rcv:
                        // frequency + battery + antenna links. Restore directly NOW (sets frequency and
                        // battery synchronously while we hold context - the delayed coroutine alone was
                        // unreliable and dropped both), then a short coroutine re-runs it so antenna links
                        // reconnect after everything has registered.
                        var recv = nb.GetComponent<Reciever>();
                        if (recv != null)
                        {
                            try { recv.Restore(rcv); } catch (System.Exception rex) { Warn("reciever restore: " + rex.Message); }
                            try { recv.StartCoroutine(rcv.RestoreLate(0.5f, recv)); } catch { }
                        }
                        break;
                }
            }
            catch (System.Exception ex) { Warn("device restore failed: " + ex.Message); }
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
            try { foreach (var r in CollectSavedRgds(b)) if (r != null && CanRestoreDevice(r)) return r; }
            catch { }
            return null;   // nothing we specifically replay (stateless, or an unhandled subtype the gate blocks)
        }

        // Every RGD_Block any component on this block would persist: the Block component itself plus each
        // sibling MonoBehaviour_Network with its own Serialize_Save (cooking/sprinkler/extractor/research
        // keep theirs there, not on Block). Best-effort: a throwing/odd component is skipped, not fatal.
        private static System.Collections.Generic.List<RGD_Block> CollectSavedRgds(Block b)
        {
            var list = new System.Collections.Generic.List<RGD_Block>();
            try { list.Add(b.Serialize_Save() as RGD_Block); } catch { }
            // GetComponentsInChildren, not GetComponents: some blocks keep their stateful save component
            // on a CHILD (e.g. TrophyHolder - the mounted head), and missing it would let the gate treat
            // the block as stateless and silently drop that state on move.
            foreach (var comp in b.GetComponentsInChildren<MonoBehaviour>())
            {
                if (comp == null || comp is Block) continue;
                var m = comp.GetType().GetMethod("Serialize_Save", System.Type.EmptyTypes);
                if (m == null || !typeof(RGD).IsAssignableFrom(m.ReturnType)) continue;
                try { list.Add(m.Invoke(comp, null) as RGD_Block); } catch { }
            }
            return list;
        }

        private static RGD_Block TryCaptureRgd(Block b) => CaptureRestorableRgd(b);

        // ---- group move (a stack of placeables resting on the moved block) ----
        // One captured piece: its buildable + full state + transform RELATIVE to the base block, so it
        // can be re-placed by the same rigid base->base' offset (the whole stack moves as one body).
        private sealed class Carried
        {
            public Item_Base item;
            public DPS dps;
            public RGD_Slot[] slots;
            public string text;
            public RGD_Block rgd;
            public Paint paint;
            public Block original;
            public Vector3 localPos;     // position in the base block's local frame (raft-local)
            public Quaternion localRot;  // rotation relative to the base block (raft-local)
        }

        private static readonly System.Collections.Generic.List<Block> _depOriginals = new System.Collections.Generic.List<Block>();
        private static readonly System.Collections.Generic.List<Block> _newDependents = new System.Collections.Generic.List<Block>();
        private static readonly System.Collections.Generic.List<Collider> _depColliderDisabled = new System.Collections.Generic.List<Collider>();
        private static int _depMovedCount;
        // deps of the currently carried block (filled by the pickup dep-scan; teleported along at placement)
        private static readonly System.Collections.Generic.List<Block> _carryDeps = new System.Collections.Generic.List<Block>();
        // BlockCreator was INACTIVE when the move started (no hammer in hands): we enabled it for the
        // ghost, so we must disable it again on exit. Vanilla gates the whole build UI (the 'BuildMenu'
        // prompt + RMB opening the menu, BlockCreator.Update:268-283) purely on this component being
        // enabled while the hammer is equipped - leaving it on gave a build-materials button while
        // holding a water cup. isRotating is reset BEFORE deactivation, so the frozen-Update hotbar
        // lock can't recur (it freezes at false).
        private static bool _bcWasInactive;
        // Cropplots restored on the HOST whose plants must be re-broadcast to clients AFTER the
        // original block is removed (game-native Message_PlantSeed/_Plant_Complete; see FlushPlantBroadcasts).
        private static readonly System.Collections.Generic.List<Cropplot> _plantBroadcastPlots = new System.Collections.Generic.List<Cropplot>();

        private static Carried Capture(Block b, Block relativeTo)
        {
            var s = (b is Storage_Small ss && ss.GetInventoryReference() != null) ? ss.GetInventoryReference().GetRGDSlots() : null;
            return new Carried
            {
                item = b.buildableItem,
                dps = b.dpsType,
                slots = s,
                text = TryGetSignText(b),
                rgd = TryCaptureRgd(b),
                paint = CapturePaint(b),
                original = b,
                // express the piece RELATIVE to the base block in raft-local space, so re-placing it by
                // the base's new local pos/rot moves the whole stack as one rigid body (CreateBlockCheat
                // takes raft-local position + euler, like ghost.localPosition/localEulerAngles).
                localPos = Quaternion.Inverse(relativeTo.transform.localRotation) * (b.transform.localPosition - relativeTo.transform.localPosition),
                localRot = Quaternion.Inverse(relativeTo.transform.localRotation) * b.transform.localRotation,
            };
        }

        // Apply a block's carried state onto a freshly created block (storages sync to peers, devices
        // replay, paint+sign). Shared by the moved block and every dependent in its stack.
        private static void ApplyState(Block nb, RGD_Slot[] slots, RGD_Block rgd, Paint paint, string text, Network_Player player)
        {
            if (nb is Storage_Small ns && slots != null)
            {
                ns.GetInventoryReference()?.SetSlotsFromRGD(slots);
                if (player?.Network != null && player.StorageManager != null)
                {
                    try
                    {
                        var sync = new Message_Storage_Close(Messages.StorageManager_Close, player.StorageManager, ns);
                        if (Raft_Network.IsHost)
                            player.Network.RPC(sync, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        else
                            player.SendP2P(sync, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game); // client -> host, like the old storage path
                    }
                    catch (System.Exception ex) { Warn("content-sync send failed: " + ex.Message); }
                }
            }
            ApplyDeviceState(rgd, nb);
            ApplyPaint(nb, paint, player);
            ApplySignText(nb, text);
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
            var nbDisabled = new System.Collections.Generic.List<Collider>();
            foreach (var c in nb.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; nbDisabled.Add(c); }
            try
            {
                var captured = new System.Collections.Generic.List<Carried>();
                // frontier = blocks whose colliders are OFF (original is already hidden from carry). For each,
                // any nearby block now reading !IsStable() rested on it -> capture it, turn ITS colliders off
                // to expose deeper pieces. Mirrors DestroyBlock's recursive cascade exactly.
                var frontier = new System.Collections.Generic.List<Block> { original };
                for (int i = 0; i < frontier.Count && captured.Count < 128; i++)
                {
                    var hb = frontier[i];
                    float dist = hb.buildableItem?.settings_buildable != null ? hb.buildableItem.settings_buildable.UnstableCheckDistance : 3.5f;
                    foreach (var pb in BlockCreator.GetPlacedBlocks())
                    {
                        if (pb == null || pb == original || pb == nb || frontier.Contains(pb)) continue;
                        if (captured.Exists(c => c.original == pb)) continue;
                        if (Vector3.Distance(pb.transform.position, hb.transform.position) > dist) continue;
                        if (pb.IsStable()) continue;                       // still supported elsewhere - not ours
                        var pbi = pb.buildableItem;
                        // [dep] diag: GENUINE dependent (stable again once the original's colliders are
                        // restored) vs ALREADY unstable before our move (chronic neighbour - suspected
                        // recycler false-positive: a fence whose gizmo only clips the big collider).
                        bool preUnstable = false;
                        try
                        {
                            var restored = new System.Collections.Generic.List<Collider>();
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
                    var localPos = nb.transform.localPosition + nb.transform.localRotation * c.localPos;
                    var localEuler = (nb.transform.localRotation * c.localRot).eulerAngles;
                    Block nd;
                    try { nd = bc.CreateBlockCheat(c.item, localPos, localEuler, c.dps, -1); }
                    catch (System.Exception ex) { failMsg = "Didn't move the stack: " + ex.Message; return false; }
                    if (nd == null) { failMsg = "Didn't move the stack - couldn't recreate a piece on top."; return false; }
                    _newDependents.Add(nd);
                    ApplyState(nd, c.slots, c.rgd, c.paint, c.text, player);
                    _depOriginals.Add(c.original);
                }
                _depMovedCount = captured.Count;
                return true;
            }
            catch (System.Exception ex) { failMsg = "Didn't move the stack: " + ex.Message; return false; }
            finally { foreach (var c in nbDisabled) if (c != null) c.enabled = true; }
        }

        // ---- dependent scan: what ACTUALLY rests on a block -------------------------------------
        // HISTORY: the first teleport-era detector took every block within UnstableCheckDistance that
        // read !IsStable() on the SAME frame it toggled colliders. IsStable is recomputed per frame,
        // so those reads were STALE: it swept whatever was ALREADY unstable nearby (chronic wall
        // decor, junk from earlier moves). Co-op 2026-07-05: a hammock 'carried 92 on top' and
        // displaced half the raft. Vanilla DestroyBlock (BlockCreator.cs decompile) does it right:
        // TempActivateCellAndNeighbours -> deactivate the block -> 'yield return null' -> only THEN
        // read IsStable, recursing per newly-unstable block. This scan copies that frame discipline
        // and adds a flip-test (dep must have been STABLE in the pre-toggle snapshot), so
        // pre-existing floaters can never be swept again.
        private sealed class DepScan
        {
            public Block B;
            public bool OwnB;          // scan disabled B's colliders itself (request path) -> restores them
            public Vector3 Origin;
            public int WaitUntilFrame;
            public int? Cell;          // BlockCollisionConsolidator temp-activation handle
            public System.Action<DepScan> Done;
            public readonly System.Collections.Generic.List<Block> Deps = new System.Collections.Generic.List<Block>();
            public readonly System.Collections.Generic.Dictionary<Block, bool> Before = new System.Collections.Generic.Dictionary<Block, bool>();
            public readonly System.Collections.Generic.List<Collider> OffB = new System.Collections.Generic.List<Collider>();
            public readonly System.Collections.Generic.List<Collider> OffDeps = new System.Collections.Generic.List<Collider>();
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
            foreach (var d in _newDependents) if (d != null) { DeregisterCropplotPlants(d); try { BlockCreator.RemoveBlockNetwork(d, null, true); } catch { } }
            _newDependents.Clear(); _depOriginals.Clear();
        }

        private static void RestoreDependentColliders()
        {
            foreach (var c in _depColliderDisabled) if (c != null) c.enabled = true;
            _depColliderDisabled.Clear();
        }

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
            catch (System.Exception ex) { Warn("sign text restore failed: " + ex.Message); }
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
            catch (System.Exception ex) { Warn("paint apply failed: " + ex.Message); }

            if (player?.Network == null) return;
            var pos = nb.transform.localPosition;
            // Message_PaintBlock's ctor reads BOTH color.uniqueColorIndex and patternColor.uniqueColorIndex,
            // so a null patternColor (solid paint, no pattern) can't be sent as-is - but silently skipping
            // the side meant a solid-painted block lost its colour on every other peer (the client-move
            // chest regression). Substitute the main colour: with the block's own patternIndex the
            // pattern colour is inert when no pattern is set.
            try
            {
                if (p.cA != null) SendPaint(player, nb.ObjectIndex, pos, p.cA, p.pcA != null ? p.pcA : p.cA, 1, p.pi1);
                if (p.cB != null) SendPaint(player, nb.ObjectIndex, pos, p.cB, p.pcB != null ? p.pcB : p.cB, 2, p.pi2);
            }
            catch (System.Exception ex) { Warn("paint network failed: " + ex.Message); }
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

        // Leave build mode WITHOUT placing extra copies (BUG3), the vanilla way: clear the selected
        // buildable + destroy the ghost. With selectedBlock null, BlockCreator.Update early-returns
        // before any placement, so LMB can't spawn another copy.
        // CRITICAL: never SetActive(false) the BlockCreator GameObject. Vanilla keeps it ALWAYS active -
        // its Update both drives the build-menu prompt and resets isRotating. Deactivating it froze
        // isRotating=true, and Hotbar gates number-key slot selection on BlockCreator.IsRotating, so the
        // hotbar got stuck on one slot until an inventory action (observed: Hotbar.Update line 366).
        // ---- GHOST PREVIEW --------------------------------------------------------------------
        // Cosmetic only: the vanilla ghost is the bare PREFAB (empty table, empty charger), so the
        // carried stack (items on top = _carryDeps) and live contents (batteries, pot food = active
        // child models of the original) are invisible while carrying. These previews are pure
        // visual clones built from scratch — an empty GameObject plus copied meshes/materials and
        // NOTHING else. No Instantiate, so no MonoBehaviour/Collider/network component can ever
        // exist on them; they are never registered with BlockCreator/save/network and are destroyed
        // in ExitBuildMode (every carry-end path funnels there). Worst possible failure = stray
        // visuals, never a duplicate.
        private sealed class GhostPreview { public GameObject Go; public Vector3 LPos; public Quaternion LRot; public bool PruneAgainstGhost; }
        private static readonly System.Collections.Generic.List<GhostPreview> _ghostPreviews = new System.Collections.Generic.List<GhostPreview>();
        private static Material _previewMat; // last ghost material applied (green/red sync)

        private static void AddGhostPreview(Block src, Block anchor, bool pruneAgainstGhost = false)
        {
            if (src == null || anchor == null) return;
            try
            {
                var root = new GameObject("PUM_GhostPreview");
                root.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
                // LODGroup keeps ALL levels' renderers enabled (culling is the group's job); cloning
                // every level stacked LOD0+LOD1+LOD2 on itself = the 'black flicker' z-fight. Clone
                // LOD0 only: skip renderers that appear in levels >=1 but not in level 0.
                var lodSkip = new System.Collections.Generic.HashSet<Renderer>();
                foreach (var lg in src.GetComponentsInChildren<LODGroup>())
                {
                    var lods = lg.GetLODs();
                    for (int i = 1; i < lods.Length; i++)
                        foreach (var lr in lods[i].renderers) if (lr != null) lodSkip.Add(lr);
                    if (lods.Length > 0)
                        foreach (var lr in lods[0].renderers) if (lr != null) lodSkip.Remove(lr);
                }
                int n = 0;
                foreach (var mf in src.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf == null || mf.sharedMesh == null || !mf.gameObject.activeInHierarchy) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled || lodSkip.Contains(mr)) continue;
                    var child = new GameObject("m");
                    child.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
                    child.transform.localScale = mf.transform.lossyScale; // root scale is 1
                    child.transform.SetParent(root.transform, true);
                    child.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                    var r = child.AddComponent<MeshRenderer>();
                    r.sharedMaterials = mr.sharedMaterials;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    n++;
                }
                if (n == 0) { UnityEngine.Object.Destroy(root); return; }
                _ghostPreviews.Add(new GhostPreview
                {
                    Go = root,
                    LPos = anchor.transform.InverseTransformPoint(src.transform.position),
                    LRot = Quaternion.Inverse(anchor.transform.rotation) * src.transform.rotation,
                    PruneAgainstGhost = pruneAgainstGhost,
                });
                root.SetActive(false); // shown by UpdateGhostPreviews once the ghost is live
            }
            catch (System.Exception ex) { Warn("ghost preview build: " + ex.Message); }
        }

        private static void UpdateGhostPreviews()
        {
            if (_ghostPreviews.Count == 0) return;
            Block ghost = null;
            if (moving != null)
                try { ghost = ComponentManager<Network_Player>.Value?.BlockCreator?.selectedBlock; } catch { }
            bool show = ghost != null && ghost.gameObject.activeInHierarchy;
            // Red/green state of the ghost. Vanilla's MaterialRendConnection.SetMaterial (decompile)
            // has TWO modes: paint-shader surfaces get a per-renderer MaterialPropertyBlock
            // '_BuildingEmission' = shaderGreen/shaderRed (material asset UNCHANGED - why the old
            // sharedMaterial==ghostMaterialRed check never saw red), everything else gets the ghost
            // material swapped in. Detect via both channels.
            Material mat = null;
            GameManager gmr = null;
            if (show)
            {
                try
                {
                    gmr = SingletonGeneric<GameManager>.Singleton;
                    if (gmr != null)
                    {
                        mat = gmr.ghostMaterialGreen;
                        var mpb = new MaterialPropertyBlock();
                        foreach (var gr in ghost.GetComponentsInChildren<Renderer>())
                        {
                            if (gr == null) continue;
                            var shared = gr.sharedMaterials;
                            for (int i = 0; i < shared.Length; i++)
                            {
                                if (shared[i] == null) continue;
                                if (shared[i] == gmr.ghostMaterialRed) { mat = gmr.ghostMaterialRed; break; }
                                if (shared[i].shader == gmr.blockPaintShader)
                                {
                                    gr.GetPropertyBlock(mpb, i);
                                    if (mpb.GetColor("_BuildingEmission") == gmr.shaderRed) { mat = gmr.ghostMaterialRed; break; }
                                }
                            }
                            if (mat == gmr.ghostMaterialRed) break;
                        }
                    }
                }
                catch { }
            }
            bool remat = mat != null && mat != _previewMat;
            if (remat) _previewMat = mat;
            foreach (var p in _ghostPreviews)
            {
                if (p.Go == null) continue;
                if (show && p.PruneAgainstGhost)
                {
                    // Drop clone meshes the vanilla ghost already renders (same prefab meshes at the
                    // same pose = z-fighting flicker). What survives is exactly the LIVE extras the
                    // prefab ghost lacks: batteries, pot contents, planted crops... One-shot.
                    p.PruneAgainstGhost = false;
                    try
                    {
                        // ONLY meshes the ghost actually RENDERS: the prefab also contains the
                        // inactive content models (battery, purifier water, cooked food) that the
                        // live original has ACTIVE - includeInactive=true pruned exactly the extras
                        // this preview exists for (batteries/water invisible, first test).
                        var ghostMeshes = new System.Collections.Generic.HashSet<Mesh>();
                        foreach (var gmf in ghost.GetComponentsInChildren<MeshFilter>())
                        {
                            if (gmf.sharedMesh == null || !gmf.gameObject.activeInHierarchy) continue;
                            var gr2 = gmf.GetComponent<MeshRenderer>();
                            if (gr2 != null && gr2.enabled) ghostMeshes.Add(gmf.sharedMesh);
                        }
                        foreach (var cmf in p.Go.GetComponentsInChildren<MeshFilter>())
                            if (cmf.sharedMesh != null && ghostMeshes.Contains(cmf.sharedMesh))
                                UnityEngine.Object.Destroy(cmf.gameObject);
                    }
                    catch { }
                }
                if (p.Go.activeSelf != show) p.Go.SetActive(show);
                if (!show) continue;
                p.Go.transform.SetPositionAndRotation(
                    ghost.transform.TransformPoint(p.LPos), ghost.transform.rotation * p.LRot);
                if (remat)
                    foreach (var r in p.Go.GetComponentsInChildren<Renderer>())
                        TintPreviewRenderer(r, mat, gmr);
            }
        }

        // Vanilla-faithful tint (mirror of MaterialRendConnection.SetMaterial): paint-shader slots
        // keep their textured material and glow green/red via '_BuildingEmission'; other slots get
        // the ghost material swapped in. Green<->red re-tints work: paint slots re-emit, swapped
        // slots just swap green<->red assets.
        private static void TintPreviewRenderer(Renderer r, Material mat, GameManager gm)
        {
            if (r == null || mat == null) return;
            try
            {
                var shared = r.sharedMaterials;
                MaterialPropertyBlock mpb = null;
                for (int i = 0; i < shared.Length; i++)
                {
                    var m = shared[i]; if (m == null) continue;
                    if (gm != null && m.shader == gm.blockPaintShader)
                    {
                        if (mpb == null) mpb = new MaterialPropertyBlock();
                        r.GetPropertyBlock(mpb, i);
                        mpb.SetColor("_BuildingEmission", mat == gm.ghostMaterialGreen ? gm.shaderGreen : gm.shaderRed);
                        r.SetPropertyBlock(mpb, i);
                    }
                    else shared[i] = mat;
                }
                r.sharedMaterials = shared;
            }
            catch { }
        }

        private static void DestroyGhostPreviews()
        {
            foreach (var p in _ghostPreviews)
                if (p.Go != null) try { UnityEngine.Object.Destroy(p.Go); } catch { }
            _ghostPreviews.Clear();
            _previewMat = null;
        }

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

        // A placement-time refusal MUST NOT leave the vanilla ghost alive this frame. BlockCreator.
        // Update runs after our Ticker and, seeing BuildError.None + the SAME LMB press, PLACES A
        // REAL BLOCK itself (and eats its build cost) - that was the charger dup of 07-06 03:00:
        // 'surface' refusal returned with a live ghost, vanilla built a new charger every click,
        // M-cancel then restored the hidden original next to it. Success paths are safe because
        // ExitBuildMode nulls selectedBlock in the same frame. This kills the ghost NOW and re-arms
        // it next Tick, so the carry continues but the click can't double-place.
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
                // SAME PREFAB VARIANT -> teleport the existing block: nothing recreated, nothing
                // removed, no state to carry, no replication lifecycle to lose. Variant identity is
                // compared by prefab NAME (instances are 'PrefabName(Clone)') - the dpsType enum on a
                // variant prefab does NOT reliably equal the surface we send (observed: wall chest,
                // req dps=Wall, went recreate), names are truth by construction. The stack found by
                // the pickup dep-scan (_carryDeps) teleports along; RestoreHidden un-hides the SAME
                // object at its new spot.
                // Block_Pipe (segments AND devices - the charger IS Block_Pipe) teleports too:
                // BeginTeleport replays the autotile lifecycle around the move (see PipeLifecycle).
                // The old blanket 'pipes always recreate' exclusion locked the charger out entirely
                // (stateful -> recreate refused -> 'surface' forever, 07-07).
                if (SameVariant(original, item, dps))
                {
                    BeginTeleport(original, pos, rot, default, _carryDeps);
                    _carryDeps.Clear();
                    moving = null; movingItem = null; movingSlots = null;
                    RestoreHidden();
                    ExitBuildMode();
                    return;
                }
                // Variant differs -> RECREATE. Recreation replays state through our RGD adapters;
                // a stateful type we have no adapter for must not be rebuilt (state would be lost).
                // Those are teleport-only: same surface type keeps the same prefab -> same object.
                if (HasUnhandledState(original))
                { NoteHud(Loc.T("surface")); SuppressVanillaPlaceThisFrame(bc); return; }
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
                _hostNb = nb;
                _hostOriginal = original;
                _hostSlots = slots;
                _hostText = movingText;
                _hostRgd = movingRgd;
                _hostPaint = movingPaint;
                _hostVerifyStart = Time.frameCount;
                _hostVerifyDeadlineTime = Time.realtimeSinceStartup + 6f; // wall-clock; slow settlers (recycler ~4s) fit
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
                // CLIENT: hand the whole move to the HOST (path a, both run the mod) - STORAGES INCLUDED.
                // The legacy storage path (vanilla Message_BlockCreator_PlaceBlock) is gone: the host
                // validated that placement against the ORIGINAL chest still standing there with colliders
                // ON, so any move shorter than the chest's own footprint was silently rejected -> 10s
                // timeout -> "snap back" (observed 02/07: short chest moves fail, >=chest-size moves ok).
                // The move-request path hides the original's colliders on the host and places via
                // CreateBlockCheat, so short-distance moves work like they do for devices.
                {
                    // same teleport-only gate as the host branch, checked locally to save the round
                    // trip (the host enforces it authoritatively anyway via refusal relay)
                    if (!SameVariant(original, item, dps) && HasUnhandledState(original))
                    { NoteHud(Loc.T("surface")); SuppressVanillaPlaceThisFrame(bc); return; }
                    if (player.Network == null) { Warn("client move: no Network."); AbortKeepOriginal(); return; }
                    if (!SendMoveRequest(player, original.ObjectIndex, pos, rot, dps))
                    { NoteHud(Loc.T("no_host")); AbortKeepOriginal(); return; }
                    _cmSentTime = Time.realtimeSinceStartup; _cmOrigGoneLogged = false; _cmSeenLogged = false;
                    _cmAcked = false; _cmProbeSent = false;
                    // remember our captured state + snapshot existing blocks so we can find the new one
                    // and restore it on our own view (host's restore doesn't replicate device state back).
                    _clientMoveRgd = movingRgd; _clientMoveSlots = movingSlots;
                    _clientMovePaint = movingPaint; _clientMoveText = movingText;
                    _clientMovePos = pos; _clientMoveRestored = false;
                    _preExisting.Clear();
                    foreach (var b in BlockCreator.GetPlacedBlocks()) if (b != null) _preExisting.Add(b.ObjectIndex);
                    _pendingClientMoveOriginal = original;
                    _awaitingHostMove = true;
                    _clientMoveDeadlineFrame = Time.frameCount + 600; // ~10s failsafe
                    Note($"client: asked host to move '{item.UniqueName}'; original kept until the host confirms.");
                    moving = null; movingItem = null; movingSlots = null; ExitBuildMode();
                    return;
                }

            }
        }

        // CLIENT content-sync: after we asked the host to place the chest, watch StorageManager for
        // the new storage (object index not in our pre-place snapshot) nearest the placed spot, then
        // push our carried contents to the host via the vanilla Message_Storage_Close path and apply
        // them to our own view. Times out so we never poll forever.
        // HOST place-first verify: wait for the freshly-placed chest to settle to IsStable() (the
        // same-frame check reads stale - see DIAG), then remove the original. A genuinely unsupported
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
        // 'PrefabName(Clone)'; enum dpsType on the variant prefab is NOT a reliable discriminator
        // (observed mismatch on a wall chest re-placed on the same wall).
        private static bool SameVariant(Block original, Item_Base item, DPS dps)
        {
            try
            {
                if (original == null) { Warn("SameVariant: original is null/destroyed -> false"); return false; }
                var reqPrefab = item?.settings_buildable?.GetBlockPrefab(dps);
                // reqPrefab == null => the item has NO distinct prefab for this dps, i.e. it is a
                // SINGLE-VARIANT placeable (BatteryCharger, most devices: one floor prefab, dpsType
                // Default/None). GetBlockPrefab is keyed on surface enums it doesn't register, so the
                // round-trip GetBlockPrefab(ghost.dpsType) returns null. There is no OTHER variant to
                // recreate into -> it is the same variant by definition -> teleport. The old code fell
                // to 'return false' here with NO log, so HasUnhandledState refused every charger move
                // with 'surface' forever (observed 07-06 02:54-03:05: charger refused ~10x, never a
                // 'variant differs' line - proof the null branch, not a name mismatch, was taken).
                if (reqPrefab == null) { Note($"[t] '{original.name}' has no variant prefab for dps={dps} -> single-variant, teleport"); return true; }
                string origBase = original.name.Replace("(Clone)", "").Trim();
                bool same = origBase == reqPrefab.name;
                if (!same) Note($"[t] variant differs: orig='{origBase}' req='{reqPrefab.name}' (dps={dps}, origDps={original.dpsType}) -> recreate path");
                return same;
            }
            // a silent 'catch -> false' here ATE the evidence of the 07-07 charger refusal (live
            // eval proved every legit input returns true; only a swallowed throw could refuse) -
            // never mute this catch again
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
            if (!to.IsValid()) return;
            var payload = BuildTeleportPayload(idx, pos, rot);
            try { Steamworks.SteamNetworking.SendP2PPacket(to, payload, (uint)payload.Length, Steamworks.EP2PSend.k_EP2PSendReliable, MoveChannel); }
            catch (System.Exception ex) { Warn("send teleport: " + ex.Message); }
            // idempotent - repeat twice against session drops (a lost transform packet otherwise waits
            // for the probe to converge)
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
        // relay - the relay batches keep this fresh the whole session). This is the ONLY trustworthy
        // address source: vanilla 'SendP2P' is PLAYFAB PARTY, not Steam - Network_UserId/remoteUsers
        // keys are PlayFab EntityKey ids (hex), so CSteamID((ulong)kv.Key) addressed NOBODY. That is
        // why every type-7 notify was lost (0/3 in session 07-04) while ACK/refusal/probe-reply -
        // all sent to req.Sender, a real CSteamID - always arrived. (Raft_Network.cs:1858 SendP2P
        // resolves Network_UserId against PlayFabMultiplayerManager.RemotePlayers.)
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
        // block component IS Block_Pipe (eval-proven 07-07, the 'charger can never move' regression
        // of the 00d30a0 blanket pipe exclusion). Such a block wires itself into the world at
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
                NoteHud(Loc.T("no_support"));
                if (_tpReqSender.IsValid()) SendMoveRefusal(_tpReqSender, _tpBlock.ObjectIndex, "no_support");
                _tpBlock = null; _tpDeps.Clear(); _tpDepsOldPos.Clear(); _tpDepsOldRot.Clear();
                _tpVerifying = false; _tpReqSender = default;
            }
        }

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
        // PRESENCE BEACON: the host learns peer Steam ids ONLY from packets the mod sends (vanilla
        // addressing is PlayFab - the tp4 lesson). A quiet client (no move requests; and RelayLogs
        // defaults OFF now, so no relay batches either) was never in _knownPeers and MISSED every
        // host-initiated teleport (observed 07-06 05:24: host moved a chest, client kept seeing it
        // under the stairs next to the newly crafted one). A 9-byte hello every 10s fixes the roster;
        // PollMoveRequests RegisterPeer()s the sender of ANY packet, so kind 8 needs no handler.
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

            while (!_hostVerifying && !_tpVerifying && _reqScan == null && moving == null && _moveReqQueue.Count > 0)
            {
                var req = _moveReqQueue.Dequeue();
                uint reqIdx = 0;
                try { using var r = new BinaryReader(new MemoryStream(req.Buf)); r.ReadUInt32(); r.ReadByte(); reqIdx = r.ReadUInt32(); } catch { }
                if (_canceledReqs.Remove(reqIdx)) { Note($"[t] skipped canceled move request #{reqIdx}"); continue; }
                HandleMoveRequest(req);
                break;
            }
            if ((_hostVerifying || moving != null) && _moveReqQueue.Count > 0 && Time.frameCount % 300 == 0)
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
            _hostLastLoggedStable = -1; _hostVerifying = true;
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
                    var player = ComponentManager<Network_Player>.Value;
                    try { ApplyState(best, _clientMoveSlots, _clientMoveRgd, _clientMovePaint, _clientMoveText, player); }
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

        private static void PollHostVerify()
        {
            if (_hostNb == null)
            {
                RestoreHidden();
                if (_hostReqSender.IsValid()) SendMoveRefusal(_hostReqSender, _hostOriginal != null ? _hostOriginal.ObjectIndex : 0u, "r_move_failed");
                _hostVerifying = false; _hostOriginal = null; _hostSlots = null; _hostReqSender = default;
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

                // the moved block's own state (slots / device / paint / sign).
                ApplyState(nb, slots, _hostRgd, _hostPaint, _hostText, player);

                // GROUP MOVE: anything resting on the original (decor on a table, a stack) would be
                // cascaded away when we remove it. Detect that exact cascade set with the game's own
                // predicate - with the original's colliders disabled (as they've been all carry) a block
                // it supported reads !IsStable() - then re-create each on the moved block by the same rigid
                // A->A' offset and replay its state. Atomic: if any piece has state we can't carry or a
                // re-create fails, undo EVERYTHING (discard nb + new pieces, restore the original) so the
                // stack is never half-moved or lost.
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
                    _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null; _hostText = null; _hostRgd = null; _hostReqSender = default;
                    return;
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
                _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null; _hostText = null; _hostRgd = null; _hostReqSender = default;
                return;
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
                _hostVerifying = false; _hostNb = null; _hostOriginal = null; _hostSlots = null; _hostText = null; _hostRgd = null; _hostReqSender = default;
            }
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
                dtm.ShowText(Loc.T("move"), Plugin.MoveKey.Value.MainKey, 1, 0, false);
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
