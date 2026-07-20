using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // In-world 'Move' hint + the transient HUD note line.
    public partial class Plugin
    {
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
            UpdateRopePreview(); // strung zipline rope follows the carry ghost (see Plugin.Carry.cs)
            UpdateWirePreview(); // antenna/receiver wires follow the carry ghost too
            // transient user-feedback line first: it must show even mid-carry (refusals fire then)
            if (_hudNote != null)
            {
                if (Time.realtimeSinceStartup < _hudNoteUntil)
                {
                    // Own label under the prompt bar. Vanilla DisplayText slots can't do 'a line
                    // below': slot 0 is middle-of-screen and slots 1-3 share one horizontal bottom
                    // row, so any slot we take reflows/steals the vanilla 'X' prompt in that row.
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
            if (Moving != null || _hostVerifying) { ClearHintIfShown(); return; }
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
        // Placement measured live: DisplayTextBottom occupies world-y 154..241 on a 945px screen
        // and nearly touches the hotbar (~16px gap) - there is no clean band BELOW the prompts,
        // so the note goes ABOVE them. Canvas ref height 1080 -> scale .875; NoteY canvas
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
                if (Plugin.Moving != null) return;                       // already carrying one
                if (CanvasHelper.ActiveMenu != MenuType.None) return;    // a menu is open
                if (__instance == null || __instance.IsOpen) return;    // chest is open
                var dtm = ComponentManager<DisplayTextManager>.Value;
                if (dtm == null) return;
                dtm.ShowText(Loc.T("move"), Plugin.MoveKey.Value.MainKey, 1, 0, false);
            }
            catch { }
        }
    }
}
