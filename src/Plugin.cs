using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Move a placed storage chest (with its contents) to a new spot, using the vanilla build
    // placement ghost as the preview. Press the hotkey while aiming at a storage to pick it up
    // into placement mode (ghost follows the cursor); left-click drops it at the new location with
    // its inventory intact; press the hotkey again (or right-click) to cancel.
    //
    // ARCHITECTURE NOTE (learned from runtime diagnostics in the BepInEx 5 + Wine/CrossOver setup):
    //   * The host plugin component's Update() is NOT pumped there, and the component itself gets
    //     Destroyed a few seconds after Awake (which would also rip any Harmony patches).
    //   * Therefore ALL per-frame logic lives on our own DontDestroyOnLoad `Ticker` GameObject.
    //     Placement is handled by reading the vanilla ghost (BlockCreator.selectedBlock) directly on
    //     left-click. Vanilla cannot double-place the chest because the storage item is not in the
    //     player's inventory while it is being carried.
    //   * ZERO Harmony patches on game methods. The storage 'Move' hint used to be a postfix on
    //     Storage_Small.OnIsRayed, but under RML (mid-session load into a live process via CrossOver/
    //     Rosetta) patching a HOT method can yield a broken replacement that spuriously NREs every
    //     hovered frame with its own exception handling dead (recon/storage-onisrayed.*, sessions
    //     A/B/C 2026-07-25). All hints now come from our LateTick raycast - plain compiled code.
    //   * The same layout is what makes the mod portable: nothing that matters hangs off the host
    //     component, so swapping BepInEx for the Raft Mod Loader is a matter of swapping one file.
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

    // Severity, ours. Deliberately NOT BepInEx's LogLevel: that one is [Flags] with severity
    // DECREASING as the value grows (Fatal=1, Error=2, Warning=4, Message=8, Info=16, Debug=32),
    // so the natural-looking `lvl >= Warning` filter was silently inverted - it printed the Info /
    // Debug chatter it was meant to hide and swallowed Error, the one line a bug report needs.
    // Ordered by severity here, so the filter in Emit means what it reads.
    internal enum PumLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

    public partial class Plugin
    {
        public const string Guid = "com.cyace84.pickupmove";

        // ---- host contract ---------------------------------------------------------------------
        // The mod runs under two loaders: BepInEx (src/hosts/Host.BepInEx.cs) and Raft Mod Loader
        // (rml/Host.Rml.cs). Every loader-specific capability is funnelled through the six members
        // below, which a host fills in before calling InitCommon(). Nothing else in the mod may
        // touch a loader API - that is what keeps one source tree shippable to both.
        internal static System.Action<PumLevel, string> LogSink; // loader console / log file
        internal static System.Func<bool> MoveKeyDown;           // hotkey went down this frame
        internal static System.Func<KeyCode> MoveKeyMain;        // key to draw in the HUD hint
        internal static string LogDir;                           // where LogRelay writes sessions
        internal static string VersionText;                      // '<name> <version>' for the banner
        // Unpatching is the one Harmony call with no portable spelling: BepInEx ships HarmonyX,
        // whose instance UnpatchAll(id) is [Obsolete(error: true)] in favour of a static UnpatchID,
        // while RML ships stock Lib.Harmony, which has the instance method and no UnpatchID at all.
        // Each host spells it its own way against the shared _harmony instance.
        internal static System.Action UnpatchSelf;

        private static Harmony _harmony;
        private static GameObject _tickerGo;

        // Shared startup. Called by each host once its contract members are filled in.
        internal static void InitCommon()
        {
            // Own DontDestroyOnLoad ticker: the host plugin component's Update is not pumped in this
            // env and the component itself gets destroyed shortly after Awake.
            var go = new GameObject("RMS_Ticker");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Ticker>();
            _tickerGo = go;

            // R3: reset all move-related statics on a full world switch. The Ticker (and thus every
            // static) is DontDestroyOnLoad, so without this a stale pending move / request queue /
            // peer roster leaks into the next world. Single-mode only: story islands load additively
            // and must NOT wipe an in-flight move or the peer roster.
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            // Harmony instance kept for the host contract (UnpatchSelf) and future need, but the
            // assembly deliberately contains NO [HarmonyPatch] classes - PatchAll is a verified no-op
            // (see the patch check line below). Rationale: the architecture note above.
            try { _harmony = new Harmony(Guid); _harmony.PatchAll(typeof(Plugin).Assembly); }
            catch (System.Exception ex) { Warn("Harmony patch failed (Move hint disabled, core feature unaffected): " + ex.Message); }
            // Self-verification (storage-onisrayed recon): counts must all be 0 and owners empty now
            // that we ship no patches - and if some OTHER mod has OnIsRayed patched, owners names it.
            // Whether a patch is live must be a LOG LINE, not an inference from decompiles.
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(Storage_Small), "OnIsRayed");
                var pi = Harmony.GetPatchInfo(m);
                Announce("patch check: OnIsRayed prefixes=" + (pi?.Prefixes?.Count ?? 0)
                    + " postfixes=" + (pi?.Postfixes?.Count ?? 0)
                    + " finalizers=" + (pi?.Finalizers?.Count ?? 0)
                    + " owners=[" + (pi != null ? string.Join(",", System.Linq.Enumerable.ToArray(pi.Owners)) : "") + "]");
            }
            catch (System.Exception ex) { Warn("patch check failed: " + ex.Message); }

            Announce($"{VersionText} (build {BuildStamp.Value}) loaded. Move key = {MoveKeyMain()}.");
        }

        // Reload-safe teardown for MonoLab.Hot.Reload (dev only): drop our ticker, remove the Harmony
        // patch, and clear carry state so a hot-reloaded copy doesn't duplicate input handling or the
        // OnIsRayed postfix. No-op cost in production (never called outside the dev reloader).
        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (mode == UnityEngine.SceneManagement.LoadSceneMode.Single) ResetSessionState();
        }

        public static void __MonoLabUnload()
        {
            try { UnpatchSelf?.Invoke(); } catch { }
            try { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
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
        internal static bool LogConsole;   // set by the host from its own config
        internal static void Note(string msg)  => Emit(PumLevel.Info, msg);
        internal static void Trace(string msg) => Emit(PumLevel.Debug, msg);
        internal static void Warn(string msg)  => Emit(PumLevel.Warning, msg);
        internal static void Err(string msg)   => Emit(PumLevel.Error, msg);
        private static void Emit(PumLevel lvl, string msg)
        {
            if (LogConsole || lvl >= PumLevel.Warning) LogSink?.Invoke(lvl, msg);
            LogRelay.Record(lvl, msg);
        }
        // Always visible regardless of LogToConsole: the single load line (raft-ship reads the build
        // stamp back from it) and anything a supporter must see even in a quiet install.
        internal static void Announce(string msg) { LogSink?.Invoke(PumLevel.Info, msg); LogRelay.Record(PumLevel.Info, msg); }
        internal static void NoteHud(string msg)
        {
            Note(msg); // log part gated by LogToConsole; the HUD line below is player feedback, never gated
            _hudNote = msg; _hudNoteUntil = Time.realtimeSinceStartup + 2.5f;
        }

        // HUD without the log line, for a note that has to be REPEATED to stay on screen (the note
        // lives 2.5s). Logging every repeat would bury the log in the same sentence.
        internal static void HudOnly(string msg)
        {
            _hudNote = msg; _hudNoteUntil = Time.realtimeSinceStartup + 2.5f;
        }

        // Per-frame logic, driven by Ticker.
        internal static void Tick()
        {
            LogRelay.Tick();
            if (Raft_Network.IsHost) PollMoveRequests(); else { PollMoveRefusals(); SendHello(); }
            ProcessTeleportResends(); // both roles: teleport notifies (host) AND paint notifies (either side)
            // vanilla-style dependent scans need a settle frame between collider toggles and IsStable
            // reads (DestroyBlock does 'yield return null' for the same reason) - step them before
            // anything below can early-return.
            if (_reqScan != null) StepDepScan(_reqScan);
            if (_pickupScan != null) StepDepScan(_pickupScan);
            if (_awaitingHostMove) PollClientMove();
            PollRestoreWatches(); // restore watchdog: must run even while other moves verify
            PollRemovalChecks();  // deferred 'did the removal actually take' postcondition (R5)
            PollClaim();          // carry-claim lifecycle: release/heartbeat/TTL (one M per block)
            if (_tpVerifying) { PollTeleportVerify(); return; }
            if (_hostVerifying) { PollHostVerify(); return; }

            // R5: fake-null carry. The block being carried was destroyed by another peer (or a
            // cascade) mid-carry: managed-alive but Unity-null, so every 'Moving == null' guard below
            // reads it as gone and skips BOTH the M and RMB cancel paths - build mode wedges with a
            // live ghost. Detect the managed-alive-but-Unity-dead state and tear the carry down.
            if (!ReferenceEquals(Moving, null) && Moving == null)
            {
                Warn("carry: the block being moved was destroyed by another peer; canceling the carry.");
                NoteHud(Loc.T("r_gone")); // tell the player WHY the ghost vanished, not just the log
                CancelMove();
                return;
            }

            if (MoveKeyDown())
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
