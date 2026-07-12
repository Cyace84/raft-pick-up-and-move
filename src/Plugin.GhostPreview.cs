using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // Visual-only clones of live contents and the carried stack, shown on the placement ghost.
    public partial class Plugin
    {
        // ---- GHOST PREVIEW --------------------------------------------------------------------
        // Cosmetic only: the vanilla ghost is the bare PREFAB (empty table, empty charger), so the
        // carried stack (items on top = _carryDeps) and live contents (batteries, pot food = active
        // child models of the original) are invisible while carrying. These previews are pure
        // visual clones built from scratch - an empty GameObject plus copied meshes/materials and
        // NOTHING else. No Instantiate, so no MonoBehaviour/Collider/network component can ever
        // exist on them; they are never registered with BlockCreator/save/network and are destroyed
        // in ExitBuildMode (every carry-end path funnels there). Worst possible failure = stray
        // visuals, never a duplicate.
        private sealed class GhostPreview { public GameObject Go; public Vector3 LPos; public Quaternion LRot; public bool PruneAgainstGhost; }
        private static readonly List<GhostPreview> _ghostPreviews = new List<GhostPreview>();
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
                var lodSkip = new HashSet<Renderer>();
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
                        var ghostMeshes = new HashSet<Mesh>();
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
    }
}
