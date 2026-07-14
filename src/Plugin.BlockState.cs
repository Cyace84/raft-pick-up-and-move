using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Capture/restore of block state: paint, sign text, storage slots, device RGDs, plants.
    public partial class Plugin
    {
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

        // Restore watchdog: one historical content wipe passed a same-frame 'restored N slots' claim,
        // so after every verified restore the chest is watched for 20s. Contents vanishing while the
        // chest was never opened cannot be the player (emptying requires opening) -> loud warn +
        // re-apply from the captured slots. Opening the chest ends the watch (the player owns the
        // state from then on; re-applying after a legit take would dupe items).
        private sealed class RestoreWatch { public Storage_Small Ns; public RGD_Slot[] Slots; public float Until; public int Expected; }
        private static readonly List<RestoreWatch> _restoreWatches = new List<RestoreWatch>();

        private static void WatchRestore(Block nb, RGD_Slot[] slots)
        {
            var ns = nb as Storage_Small;
            if (ns == null || slots == null) return;
            int expected = 0; foreach (var s in slots) if (s != null && s.HasItem) expected++;
            if (expected == 0) return;
            _restoreWatches.Add(new RestoreWatch { Ns = ns, Slots = slots, Until = Time.realtimeSinceStartup + 20f, Expected = expected });
        }

        private static void PollRestoreWatches()
        {
            if (_restoreWatches.Count == 0) return;
            for (int i = _restoreWatches.Count - 1; i >= 0; i--)
            {
                var w = _restoreWatches[i];
                if (w.Ns == null) { _restoreWatches.RemoveAt(i); continue; }        // destroyed/moved again
                if (w.Ns.IsOpen) { _restoreWatches.RemoveAt(i); continue; }         // player took over
                if (Time.realtimeSinceStartup > w.Until) { _restoreWatches.RemoveAt(i); continue; }
                try
                {
                    var inv = w.Ns.GetInventoryReference();
                    if (inv == null) continue;
                    int filled = 0; foreach (var sl in inv.allSlots) if (sl != null && !sl.IsEmpty) filled++;
                    if (filled == 0)
                    {
                        Warn($"watchdog: '{w.Ns.name}' lost its {w.Expected} restored slots without ever being opened"
                            + $" ({(w.Until - Time.realtimeSinceStartup):F1}s left on watch) - re-applied from the move snapshot. Please report this line.");
                        inv.SetSlotsFromRGD(w.Slots);
                        SendStorageSync(w.Ns, ComponentManager<Network_Player>.Value);
                    }
                }
                catch (System.Exception ex) { Warn("watchdog: " + ex.Message); _restoreWatches.RemoveAt(i); }
            }
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
        private static List<RGD_Block> CollectSavedRgds(Block b)
        {
            var list = new List<RGD_Block>();
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

        // Cropplots restored on the HOST whose plants must be re-broadcast to clients AFTER the
        // original block is removed (game-native Message_PlantSeed/_Plant_Complete; see FlushPlantBroadcasts).
        private static readonly List<Cropplot> _plantBroadcastPlots = new List<Cropplot>();

        // Apply a block's carried state onto a freshly created block (storages sync to peers, devices
        // replay, paint+sign). Shared by the moved block and every dependent in its stack.
        private static void ApplyState(Block nb, RGD_Slot[] slots, RGD_Block rgd, Paint paint, string text, Network_Player player)
        {
            if (nb is Storage_Small ns && slots != null)
            {
                ns.GetInventoryReference()?.SetSlotsFromRGD(slots);
                SendStorageSync(ns, player);
            }
            ApplyDeviceState(rgd, nb);
            ApplyPaint(nb, paint, player);
            ApplySignText(nb, text);
        }

        private static void SendStorageSync(Storage_Small ns, Network_Player player)
        {
            if (player?.Network == null || player.StorageManager == null) return;
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

        // The storage restore gate. Storage_Small.OnFinishedPlacement REPLACES inventoryReference
        // with a fresh empty Instantiate (decomp Storage_Small.cs:80); on some variants it runs a
        // frame or two after placement, so an early SetSlotsFromRGD writes into an inventory object
        // that gets thrown away - 'restored N slots' logs success while the chest ends up empty.
        // Deterministic order, no timing guesses:
        //   1. registration gate: allStorages.Contains(ns) marks OnFinishedPlacement done - the
        //      inventory it swapped in is the final one. Never write before that.
        //   2. write, then read back: filled slots in the live inventory must equal the carried
        //      filled count. Only then may the caller destroy the original.
        // Callers poll this across frames; on their deadline they revert the whole move.
        private static bool TryRestoreSlotsVerified(Block nb, RGD_Slot[] slots, out string why)
        {
            why = null;
            var ns = nb as Storage_Small;
            if (ns == null || slots == null) return true; // nothing to restore -> trivially verified
            try
            {
                if (StorageManager.allStorages == null || !StorageManager.allStorages.Contains(ns))
                { why = "storage not registered yet (OnFinishedPlacement pending)"; return false; }
                var inv = ns.GetInventoryReference();
                if (inv == null) { why = "inventory not created yet"; return false; }
                if (inv.allSlots.Count == 0)
                {
                    // With the HUD hidden (InventoryParent inactive - any hide-HUD mod or tool)
                    // the freshly Instantiated inventory never gets Awake -> InitializeSlots ->
                    // allSlots stays empty and every slot write is a silent no-op; that was the
                    // historical content wipe. Wake it manually: borrow the nearest ACTIVE
                    // ancestor as parent for one synchronous activate (Awake runs inside
                    // SetActive), then put everything back exactly as it was.
                    var t = inv.transform; var home = t.parent;
                    Transform refuge = home;
                    while (refuge != null && !refuge.gameObject.activeInHierarchy) refuge = refuge.parent;
                    if (refuge != null && refuge != home)
                    {
                        var lp = t.localPosition; var ls = t.localScale;
                        bool wasActive = inv.gameObject.activeSelf;
                        t.SetParent(refuge, false);
                        inv.gameObject.SetActive(true);      // synchronous Awake -> InitializeSlots
                        inv.gameObject.SetActive(wasActive); // restore state before anything renders
                        t.SetParent(home, false);
                        t.localPosition = lp; t.localScale = ls;
                        Note($"inventory UI woken under inactive HUD ({inv.allSlots.Count} slots initialized).");
                    }
                    if (inv.allSlots.Count == 0) { why = "inventory UI never initialized (HUD hidden?)"; return false; }
                }
                inv.SetSlotsFromRGD(slots);
                // DEEP read-back (R5): the old count-only check passed whenever the FILLED-slot count
                // matched, so a truncated stack, a wrong item, or a mis-indexed slot slipped through as
                // 'restored'. GetRGDSlots reflects the live inventory; RGD_Slot.Equals compares
                // slotIndex+itemIndex+amount+uses+exclusiveString (RGD_Slot.cs:41-48). We match each
                // carried filled slot to the read-back slot with the same slotIndex and deep-compare.
                // A false negative only costs a few retry frames then a loud apply-anyway (the caller's
                // deadline path) - never data loss - so this is strictly safer than the count check.
                var back = inv.GetRGDSlots();
                int expected = 0; foreach (var s in slots) if (s != null && s.HasItem) expected++;
                if (back == null) { why = $"read-back returned no slots (expected {expected})"; return false; }
                // strict cardinality: GetRGDSlots returns ONLY filled slots, so any extra entry is a
                // slot the restore should have cleared (SetSlotsFromRGD SetItem(null)s unmatched ones).
                if (back.Length != expected) { why = $"read-back has {back.Length} filled slots, expected {expected}"; return false; }
                int mismatch = 0;
                foreach (var want in slots)
                {
                    if (want == null || !want.HasItem) continue;
                    RGD_Slot got = null;
                    foreach (var b in back) if (b != null && b.slotIndex == want.slotIndex) { got = b; break; }
                    if (got == null || !want.Equals(got)) mismatch++;
                }
                if (mismatch != 0) { why = $"read-back {mismatch}/{expected} carried slots differ (deep compare)"; return false; }
                return true;
            }
            catch (System.Exception ex) { why = "verify threw: " + ex.Message; return false; }
        }

        // Order-independent deep equality of two slot snapshots (filled slots matched by slotIndex,
        // fields compared by RGD_Slot.Equals: itemIndex, amount, uses, exclusiveString). Used by the
        // commit gate to detect content drift between capture and commit.
        private static bool SlotsEqual(RGD_Slot[] a, RGD_Slot[] b)
        {
            try
            {
                int fa = 0, fb = 0;
                if (a != null) foreach (var s in a) if (s != null && s.HasItem) fa++;
                if (b != null) foreach (var s in b) if (s != null && s.HasItem) fb++;
                if (fa != fb) return false;
                if (fa == 0) return true;
                foreach (var s in a)
                {
                    if (s == null || !s.HasItem) continue;
                    RGD_Slot m = null;
                    foreach (var t in b) if (t != null && t.slotIndex == s.slotIndex) { m = t; break; }
                    if (m == null || !s.Equals(m)) return false;
                }
                return true;
            }
            // treat a throw as 'not equal': the caller then re-applies the fresh capture, which is
            // idempotent - strictly safer than assuming the stale snapshot still matches.
            catch { return false; }
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
        // block, so it shows), and to peers via a MOD-CHANNEL notify (kind 9), NOT the vanilla
        // Message_PaintBlock. The vanilla path is unusable for a synthetic (brushless) paint: the
        // receiver's Network_Player.Deserialize (:1127) runs PaintBrush.PaintBlock on the SENDER'S
        // replica, and PaintBrush lives on the equipped-brush GameObject - inactive unless that
        // player is actually holding the brush, so UsableTool.Start (:29), which assigns
        // playerNetwork, never ran -> NRE at PaintBrush.PaintBlock:298 (playerNetwork.IsLocalPlayer).
        // The receive pipeline has ZERO try/catch (NetworkUpdateManager, Raft_Network's compound
        // foreach), so that one throw kills the REST of the same batch too: paint was always lost
        // on peers, and any remove/sync coalesced behind it died with it (the 07-13 zombie chest).
        // Runtime artifact: client relay 07-14 01:55:17 [unity/Exception] NRE PaintBrush.PaintBlock
        // <- Network_Player.Deserialize. Tradeoff of the mod channel: peers WITHOUT this mod see
        // default colour until their next world (re)load - paint persists via the host-side Block
        // fields, so the save is always right.
        private static void ApplyPaint(Block nb, Paint p, Network_Player player)
        {
            if (nb == null || !p.Any) return;
            try
            {
                nb.SetInstanceColorAndPattern(p.cA, p.pcA, 1, p.pi1);
                nb.SetInstanceColorAndPattern(p.cB, p.pcB, 2, p.pi2);
            }
            catch (System.Exception ex) { Warn("paint apply failed: " + ex.Message); }
            try { SendPaintNotify(nb.ObjectIndex, p); }
            catch (System.Exception ex) { Warn("paint network failed: " + ex.Message); }
        }

        // ---- kind 9 wire format (after magic+kind+idx): cA, pcA, pi1, cB, pcB, pi2. Colors travel
        // as SO_ColorValue.uniqueColorIndex (uint field, SO_ColorValue:8), 0xFFFFFFFF = null side,
        // resolved back via static ColorPicker.GetColorFromUniqueIndex(uint) (ColorPicker:87).
        private const uint NoColor = 0xFFFFFFFFu;

        private static byte[] BuildPaintPayload(uint idx, Paint p)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(MoveMagic); w.Write((byte)9); w.Write(idx);
            w.Write(p.cA != null ? p.cA.uniqueColorIndex : NoColor);
            w.Write(p.pcA != null ? p.pcA.uniqueColorIndex : NoColor);
            w.Write(p.pi1);
            w.Write(p.cB != null ? p.cB.uniqueColorIndex : NoColor);
            w.Write(p.pcB != null ? p.pcB.uniqueColorIndex : NoColor);
            w.Write(p.pi2);
            return ms.ToArray();
        }

        // Host -> every known peer; client -> host (which re-broadcasts to the others on receive).
        // Rides the same idempotent resend queue as teleports (2 repeats over ~3s): a notify that
        // beats the vanilla create replication just no-ops on the peer and a resend converges it.
        private static void SendPaintNotify(uint idx, Paint p)
        {
            var payload = BuildPaintPayload(idx, p);
            if (Raft_Network.IsHost)
            {
                foreach (var sid in _knownPeers.Keys)
                    QueueModSend(new Steamworks.CSteamID(sid), payload);
            }
            else
            {
                var net = ComponentManager<Raft_Network>.Value;
                QueueModSend(net != null ? net.CurrentSteamHost : default, payload);
            }
        }

        // Apply a kind-9 notify to our local replica: the exact calls the mover makes on its side.
        // Reader sits right after idx. Missing block = not replicated yet; a resend converges it.
        private static void ApplyPaintNotify(BinaryReader r, uint idx)
        {
            SO_ColorValue Rd() { uint i = r.ReadUInt32(); return i == NoColor ? null : ColorPicker.GetColorFromUniqueIndex(i); }
            var cA = Rd(); var pcA = Rd(); uint pi1 = r.ReadUInt32();
            var cB = Rd(); var pcB = Rd(); uint pi2 = r.ReadUInt32();
            var b = BlockCreator.GetBlockByObjectIndex(idx);
            if (b == null) return;
            try
            {
                b.SetInstanceColorAndPattern(cA, pcA, 1, pi1);
                b.SetInstanceColorAndPattern(cB, pcB, 2, pi2);
            }
            catch (System.Exception ex) { Warn("paint notify apply failed: " + ex.Message); }
        }
    }
}
