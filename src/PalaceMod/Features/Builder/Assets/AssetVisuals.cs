using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    Applies imported assets to a unit template (called from UnitFactory.ApplyVisuals, before the tint, so a tint
    also colours an imported mesh).

    Mesh swap: the unit's "model" is every mesh renderer in it. The biggest one that uses a body material ("Enemy",
    "Player...", "Ally...") keeps its renderer, its GPUInstanced component and its material, and gets the imported
    mesh instead, so the game's elite swap, hit effects and GPU instancing keep working, and the mesh moves with the
    body part's walk animation. The imported mesh is baked into that renderer's local space: fitted to the original
    model's height (or meshScale), turned by meshRotY, and standing where the model stood. The other parts get an
    invisible mesh rather than being disabled, because GPUInstanced draws them even when the renderer is off.
    A skinned body (no MeshFilter) is hidden and the mesh goes on a new static child instead.

    Texture: the body material is cloned (keeping its name, which the elite swap matches) with the PNG as its base map.
    Sounds: a BuilderUnitSounds marker on the template names the pack and asset keys; the UnitAudio.Start patch in
    BuilderManager.Assets.cs resolves them when the spawned clone starts (OGG may still be decoding at build time).
*/
namespace PalaceMod.Builder.Assets
{
    internal static class AssetVisuals
    {
        private static Mesh hiddenMesh;

        public static void Apply(GameObject go, PackEntry entry, VisualsDef v, List<string> notes, List<Object> owned)
        {
            if (v == null) return;
            if (entry == null)
            {
                if (v.Mesh != null || v.Texture != null || v.Sounds != null) notes.Add("pack not installed: imported assets skipped");
                return;
            }
            var assets = BuilderServices.Assets;
            if (v.Mesh != null)
            {
                var mesh = assets.GetMesh(entry, v.Mesh);
                if (mesh == null) notes.Add($"mesh {v.Mesh}: {Reason(entry, v.Mesh, AssetUse.Mesh)}, kept the base mesh");
                else if (!SwapMesh(go, mesh.Value.mesh, v, owned, out string error)) notes.Add($"mesh {v.Mesh}: {error}");
            }
            if (v.Texture != null)
            {
                var tex = assets.GetTexture(entry, v.Texture);
                if (tex == null) notes.Add($"texture {v.Texture}: {Reason(entry, v.Texture, AssetUse.Texture)}");
                else if (!ApplyTexture(go, tex, owned)) notes.Add("texture: no body material with a base map found");
            }
            if (v.Sounds != null && (v.Sounds.Attack ?? v.Sounds.Hurt ?? v.Sounds.Death) != null)
            {
                var marker = go.GetComponent<BuilderUnitSounds>() ?? go.AddComponent<BuilderUnitSounds>();
                marker.pack = entry.Id;
                marker.attack = v.Sounds.Attack;
                marker.hurt = v.Sounds.Hurt;
                marker.death = v.Sounds.Death;
                if (go.GetComponentInChildren<UnitAudio>(true) == null) notes.Add("sounds: the base unit has no UnitAudio, so they won't play");
                assets.Prefetch(entry);
            }
        }

        private static string Reason(PackEntry entry, string assetRef, string use)
        {
            var state = BuilderServices.Assets.GetState(entry, assetRef, use, out string message);
            return state == AssetState.Loading ? "still " + message : message ?? state.ToString().ToLowerInvariant();
        }

        // Unit bodies use the shared "Enemy" material (enemies) or the player colour materials; weapons/FX use others.
        // Keep in step with UnitFactory.IsBodyMaterial.
        internal static bool IsBodyMaterial(Material m) => m != null && (m.name == "Enemy" || m.name.StartsWith("Player") || m.name.StartsWith("Ally"));

        private sealed class Part
        {
            public Renderer Renderer;
            public MeshFilter Filter;     // null for skinned renderers
            public Mesh Mesh;
            public Bounds RootBounds;     // in the unit root's local space
        }

        /// <summary>Visible mesh renderers of the model (not ones on objects the prefab keeps switched off).</summary>
        private static List<Part> ModelParts(GameObject go)
        {
            var root = go.transform;
            var parts = new List<Part>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !ActiveBelow(r.transform, root)) continue;
                Mesh mesh;
                MeshFilter filter = null;
                if (r is MeshRenderer)
                {
                    filter = r.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }
                else if (r is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
                else continue;
                if (mesh == null || mesh == hiddenMesh) continue;
                var toRoot = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
                parts.Add(new Part { Renderer = r, Filter = filter, Mesh = mesh, RootBounds = Transform(mesh.bounds, toRoot) });
            }
            return parts;
        }

        private static bool ActiveBelow(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }

        private static bool SwapMesh(GameObject go, Mesh source, VisualsDef v, List<Object> owned, out string error)
        {
            error = null;
            var parts = ModelParts(go);
            if (parts.Count == 0) { error = "the base unit has no visible mesh to replace"; return false; }
            var model = parts[0].RootBounds;
            foreach (var p in parts.Skip(1)) model.Encapsulate(p.RootBounds);
            var body = parts.Where(p => p.Renderer.sharedMaterials.Any(IsBodyMaterial)).OrderByDescending(p => Volume(p.RootBounds)).FirstOrDefault()
                ?? parts.OrderByDescending(p => Volume(p.RootBounds)).First();

            // Fit: mesh space -> root space. Unless meshScale is given, the mesh is scaled to fit inside the model's
            // box (as tall as the model, or as wide if it's squat). The bottom centre lands on the model's bottom centre.
            var rotation = Quaternion.Euler(0, v.MeshRotY ?? 0, 0);
            var turned = Transform(source.bounds, Matrix4x4.Rotate(rotation));
            float scale = v.MeshScale ?? Mathf.Min(
                model.size.y / Mathf.Max(turned.size.y, 1e-4f),
                Mathf.Max(model.size.x, model.size.z) / Mathf.Max(turned.size.x, turned.size.z, 1e-4f));
            var bottom = new Vector3(turned.center.x, turned.min.y, turned.center.z) * scale;
            var target = new Vector3(model.center.x, model.min.y, model.center.z);
            var fit = Matrix4x4.TRS(target - bottom, rotation, Vector3.one * scale);

            hiddenMesh ??= MakeHiddenMesh();
            var root = go.transform;
            Mesh baked;
            if (body.Filter != null)
            {
                // Into the body renderer's local space, so it keeps animating with the body part.
                var toBody = body.Renderer.transform.worldToLocalMatrix * root.localToWorldMatrix * fit;
                baked = Bake(source, toBody);
                body.Filter.sharedMesh = baked;
                // GPUInstanced and the elite swap read sharedMaterial (slot 0): make it the body material.
                var mats = body.Renderer.sharedMaterials;
                var bodyMat = mats.FirstOrDefault(IsBodyMaterial) ?? mats.FirstOrDefault(m => m != null);
                body.Renderer.sharedMaterials = [bodyMat];
            }
            else
            {
                baked = Bake(source, fit);
                var child = new GameObject("PalaceBuilder Mesh");
                child.transform.SetParent(root, false);
                child.AddComponent<MeshFilter>().sharedMesh = baked;
                var renderer = child.AddComponent<MeshRenderer>();
                var mats = body.Renderer.sharedMaterials;
                renderer.sharedMaterials = [mats.FirstOrDefault(IsBodyMaterial) ?? mats.FirstOrDefault(m => m != null)];
                Hide(body);
            }
            owned.Add(baked);
            foreach (var p in parts) if (p != body) Hide(p);
            return true;
        }

        private static void Hide(Part p)
        {
            if (p.Filter != null) p.Filter.sharedMesh = hiddenMesh;
            else if (p.Renderer is SkinnedMeshRenderer s) { s.sharedMesh = hiddenMesh; s.enabled = false; }
        }

        // One degenerate triangle: draws nothing, but is a valid mesh for GPU instancing and the game's scripts.
        private static Mesh MakeHiddenMesh()
        {
            var m = new Mesh { name = "PalaceBuilder Hidden", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.SetVertices(new List<Vector3> { Vector3.zero, Vector3.zero, Vector3.zero });
            m.SetTriangles(new[] { 0, 1, 2 }, 0);
            m.bounds = new Bounds(Vector3.zero, Vector3.zero);
            return m;
        }

        private static Mesh Bake(Mesh source, Matrix4x4 m)
        {
            var mesh = Object.Instantiate(source);
            mesh.name = source.name;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var normalMatrix = m.inverse.transpose;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = m.MultiplyPoint3x4(vertices[i]);
            for (int i = 0; i < normals.Length; i++) normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
            mesh.vertices = vertices;
            if (normals.Length == vertices.Length) mesh.normals = normals;
            if (m.determinant < 0)
            {
                var tris = mesh.triangles;
                for (int i = 0; i < tris.Length; i += 3) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
                mesh.triangles = tris;
            }
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        private static Bounds Transform(Bounds b, Matrix4x4 m)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.min), Vector3.zero);
            for (int i = 0; i < 8; i++)
                result.Encapsulate(m.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z)));
            return result;
        }

        private static float Volume(Bounds b) => Mathf.Max(b.size.x, 1e-3f) * Mathf.Max(b.size.y, 1e-3f) * Mathf.Max(b.size.z, 1e-3f);

        private static readonly string[] BaseMaps = ["_BaseMap", "_MainTex", "_Albedo"];

        private static bool ApplyTexture(GameObject go, Texture2D tex, List<Object> owned)
        {
            var clones = new Dictionary<Material, Material>();
            bool any = false;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (!IsBodyMaterial(m)) continue;
                    if (!clones.TryGetValue(m, out var clone))
                    {
                        var prop = BaseMaps.FirstOrDefault(m.HasProperty);
                        if (prop == null) { clones[m] = null; continue; }
                        clone = new Material(m) { name = m.name }; // same name: the elite swap looks for "Enemy"
                        clone.SetTexture(prop, tex);
                        // A texture is multiplied by the base colour; start from white so the image shows as painted.
                        foreach (var c in new[] { "_BaseColor", "_Color" }) if (clone.HasProperty(c)) clone.SetColor(c, Color.white);
                        owned.Add(clone);
                        clones[m] = clone;
                    }
                    if (clone == null) continue;
                    mats[i] = clone;
                    changed = true;
                }
                if (changed) { renderer.sharedMaterials = mats; any = true; }
            }
            return any;
        }
    }

    /// <summary>Marks a custom unit template whose sounds come from pack assets. Copied onto every spawned clone.</summary>
    public sealed class BuilderUnitSounds : MonoBehaviour
    {
        public string pack, attack, hurt, death;
    }
}
