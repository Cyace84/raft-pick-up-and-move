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
    //
    // The Plugin class is split across partial files by responsibility:
    //   Plugin.cs             entry point, config, logging, per-frame dispatcher (Tick)
    //   Plugin.Carry.cs       pick up / cancel / confirm placement, build-mode enter/exit
    //   Plugin.Hud.cs         in-world 'Move' hint + transient HUD note line
    //   Plugin.BlockState.cs  capture/restore: paint, sign text, slots, device RGDs, plants
    //   Plugin.Deps.cs        dependent detection (what rests on a block) + group move
    //   Plugin.GhostPreview.cs visual clones of live contents shown on the placement ghost
    //   Plugin.Teleport.cs    same-variant teleport move + pipe lifecycle replay
    //   Plugin.Net.cs         move-channel protocol: requests, host verify, refusals, probes

    [BepInPlugin(Guid, "Pick Up & Move", "1.0.0")]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.cyace84.pickupmove";

        public static ConfigEntry<KeyboardShortcut> MoveKey;
        public static ConfigEntry<bool> RelayLogs;
        public static ConfigEntry<bool> LogToConsole;
        public static ManualLogSource Log;

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
            // Without this, stacked reloads leave multiple tickers, each with its own `Moving` static,
            // fighting over the hotkey (the cause of flaky cancel). Belt-and-suspenders over _tickerGo.
            try
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go != null && (go.name == "RMS_Ticker" || go.name == "PickUpMove_Note" || go.name == "PUM_GhostPreview")) UnityEngine.Object.Destroy(go);
            }
            catch { }
            _tickerGo = null;
            Moving = null; _movingItem = null; _movingSlots = null;
            _hostVerifying = false;
            _pickupScan = null; _reqScan = null; _carryDeps.Clear();
        }

        // Info = user-facing milestones; Trace = diagnostic non-events (missed raycast, bad spot).
        // The in-game channel is DisplayTextManager - the same HUD slot that draws 'M Move' and
        // provably renders. Chat was a dead end twice: LocalDebugChatMessage never wakes the faded
        // panel, and CreateLocalChatMessage only shows if the player has chat enabled (most don't).
        // Two channels: Note = status chatter, log only ('Carrying...', 'moved to...', '[t]');
        // NoteHud = player-actionable feedback (refusals, declines, timeouts) - log + 2.5s HUD line.
        // Painting every note on the HUD drowned the screen; keep the split.
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
            PollRestoreWatches(); // restore watchdog: must run even while other moves verify
            if (_tpVerifying) { PollTeleportVerify(); return; }
            if (_hostVerifying) { PollHostVerify(); return; }

            if (MoveKey.Value.IsDown())
            {
                if (Moving != null) CancelMove();
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

            if (Moving == null) { _rearmBuild = false; return; }

            // re-arm the ghost a refusal suppressed last frame (carry continues, see SuppressVanillaPlaceThisFrame)
            if (_rearmBuild)
            {
                _rearmBuild = false;
                ComponentManager<Network_Player>.Value?.BlockCreator?.SetBlockTypeToBuild(_movingItem);
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
    }

    // Our own guaranteed per-frame ticker, independent of BaseUnityPlugin.Update, surviving scene loads.
    public sealed class Ticker : MonoBehaviour
    {
        private void Update() => Plugin.Tick();
        private void LateUpdate() => Plugin.LateTick();
    }
}
