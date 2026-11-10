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
    A glTF mesh brings its own colours as a texture (its materials baked by GltfMerge), applied the same way unless
    the unit sets "texture"; then the mesh is painted with that PNG through the model's own first UV set instead.
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
            var kind = ModelRef.Kind(v.Mesh, out string modelId);
            // Scenery and other units' models need no pack files.
            if (kind is ModelKind.Scenery or ModelKind.Unit)
            {
                var source = kind == ModelKind.Scenery ? SceneryParts(v.Mesh, out string why) : UnitParts(modelId, out why);
                if (source == null) notes.Add($"mesh {v.Mesh}: {why}, kept the base model");
                else if (!AttachModel(go, source, v, out string error)) notes.Add($"mesh {v.Mesh}: {error}");
            }
            if (entry == null)
            {
                if (kind == ModelKind.Asset || v.Texture != null || v.Sounds != null) notes.Add("pack not installed: imported assets skipped");
                return;
            }
            var assets = BuilderServices.Assets;
            Texture2D meshColours = null;
            if (kind == ModelKind.Asset)
            {
                var mesh = assets.GetMesh(entry, v.Mesh);
                if (mesh == null) notes.Add($"mesh {v.Mesh}: {Reason(entry, v.Mesh, AssetUse.Mesh)}, kept the base mesh");
                else if (!SwapMesh(go, mesh.Value.mesh, v, owned, out string error)) notes.Add($"mesh {v.Mesh}: {error}");
                else meshColours = (assets as AssetProvider)?.GetMeshTexture(entry, v.Mesh);
            }
            if (v.Texture != null)
            {
                var tex = assets.GetTexture(entry, v.Texture);
                if (tex == null) notes.Add($"texture {v.Texture}: {Reason(entry, v.Texture, AssetUse.Texture)}");
                else if (!ApplyTexture(go, tex, owned)) notes.Add("texture: no body material with a base map found");
            }
            else if (meshColours != null && !ApplyTexture(go, meshColours, owned)) notes.Add("mesh colours: no body material with a base map found");
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

        /// <summary>Visible mesh renderers of the model (not ones on objects the prefab keeps switched off), measured at
        /// full size: a live unit that was just spawned (convert's keepLook) is still shrunk by the game's
        /// UnitSpawnAppear, whose Awake sets its target's scale to 0 before growing it to 1, and measuring that would fit
        /// the new model to nothing.</summary>
        private static List<Part> ModelParts(GameObject go)
        {
            var grown = new List<(Transform t, Vector3 scale)>();
            foreach (var appear in go.GetComponentsInChildren<UnitSpawnAppear>(true))
                if (appear.enabled && appear.target != null && appear.target.localScale != Vector3.one)
                {
                    grown.Add((appear.target, appear.target.localScale));
                    appear.target.localScale = Vector3.one;
                }
            try { return MeasureParts(go); }
            finally { foreach (var (t, scale) in grown) t.localScale = scale; }
        }

        private static List<Part> MeasureParts(GameObject go)
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
            var fit = Fit(model, source.bounds, v);

            hiddenMesh ??= MakeHiddenMesh();
            var root = go.transform;
            Mesh baked;
            if (body.Filter != null)
            {
                // Into the body renderer's local space, so it keeps animating with the body part.
                var toBody = body.Renderer.transform.worldToLocalMatrix * root.localToWorldMatrix * fit;
                baked = Bake(source, toBody, ownUvs: v.Texture != null);
                body.Filter.sharedMesh = baked;
                // GPUInstanced and the elite swap read sharedMaterial (slot 0): make it the body material.
                var mats = body.Renderer.sharedMaterials;
                var bodyMat = mats.FirstOrDefault(IsBodyMaterial) ?? mats.FirstOrDefault(m => m != null);
                body.Renderer.sharedMaterials = [bodyMat];
            }
            else
            {
                baked = Bake(source, fit, ownUvs: v.Texture != null);
                var child = new GameObject("PalaceBuilder Mesh");
                child.transform.SetParent(root, false);
                child.AddComponent<MeshFilter>().sharedMesh = baked;
                var renderer = child.AddComponent<MeshRenderer>();
                var mats = body.Renderer.sharedMaterials;
                renderer.sharedMaterials = [mats.FirstOrDefault(IsBodyMaterial) ?? mats.FirstOrDefault(m => m != null)];
                Hide(body);
                // A static child doesn't follow any animation: give it the procedural walk unless told not to.
                if (v.Motion != ModelRef.MotionNone) AddMotion(go, child.transform, model.size.y);
            }
            owned.Add(baked);
            foreach (var p in parts) if (p != body) Hide(p);
            if (body.Filter != null && v.Motion == ModelRef.MotionBob) AddMotion(go, body.Renderer.transform, model.size.y);
            return true;
        }

        /// <summary>Source space -> unit root space: turned by meshRot X/Y/Z, scaled to fit inside the base model's box
        /// (as tall as the model, or as wide if it's squat) unless meshScale is given, its bottom centre on the model's
        /// bottom centre, then moved by meshOffset.</summary>
        internal static Matrix4x4 Fit(Bounds model, Bounds source, VisualsDef v)
        {
            var rotation = Quaternion.Euler(v.MeshRotX ?? 0, v.MeshRotY ?? 0, v.MeshRotZ ?? 0);
            var turned = Transform(source, Matrix4x4.Rotate(rotation));
            float scale = v.MeshScale ?? Mathf.Min(
                model.size.y / Mathf.Max(turned.size.y, 1e-4f),
                Mathf.Max(model.size.x, model.size.z) / Mathf.Max(turned.size.x, turned.size.z, 1e-4f));
            var bottom = new Vector3(turned.center.x, turned.min.y, turned.center.z) * scale;
            var target = new Vector3(model.center.x, model.min.y, model.center.z);
            var offset = v.MeshOffset is { Length: 3 } o ? new Vector3(o[0], o[1], o[2]) : Vector3.zero;
            return Matrix4x4.TRS(target - bottom + offset, rotation, Vector3.one * scale);
        }

        // ---- Any asset as the body: scenery meshes and other units' models --------------------------------------

        /// <summary>A model to attach: meshes with their materials, placed in the source's own space.</summary>
        internal sealed class SourcePart
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Matrix4x4 Matrix;
        }

        /// <summary>A scenery mesh ("mesh:&lt;name&gt;[@Map]") from the loaded map or the cross-map library, with the
        /// level's own materials, stood upright as the maps place it.</summary>
        internal static List<SourcePart> SceneryParts(string reference, out string why)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var b = Library.SceneryLibrary.Resolve(scene, reference, out why);
            if (b?.Mesh == null) { why ??= "not on this map or in the library"; return null; }
            return [new SourcePart { Mesh = b.Mesh, Materials = b.Materials, Matrix = Matrix4x4.TRS(Vector3.zero, b.Upright, b.Scale) }];
        }

        /// <summary>Every visible mesh of a catalog unit's model (skinned ones in their bind pose), with its materials.</summary>
        internal static List<SourcePart> UnitParts(string unitId, out string why)
        {
            why = null;
            var unit = BuilderServices.Catalog?.FindUnit(unitId);
            if (unit?.Prefab == null) { why = $"no unit \"{unitId}\""; return null; }
            var root = unit.Prefab.transform;
            var list = new List<SourcePart>();
            foreach (var p in ModelParts(unit.Prefab))
                list.Add(new SourcePart { Mesh = p.Mesh, Materials = p.Renderer.sharedMaterials, Matrix = root.worldToLocalMatrix * p.Renderer.transform.localToWorldMatrix });
            if (list.Count == 0) { why = "that unit has no visible mesh"; return null; }
            return list;
        }

        /// <summary>The template needs rebuilding on another map: its model uses that map's materials.</summary>
        internal static bool SceneDependent(VisualsDef v) => ModelRef.Kind(v?.Mesh) == ModelKind.Scenery;

        /// <summary>Puts a source model on a unit in place of its own: the base's parts are hidden, the source goes on a
        /// static child of the root ("PalaceBuilder Model") fitted to the base's size, with its own materials or the
        /// unit's body material (meshLook). It gets the procedural walk unless motion is "none".</summary>
        internal static bool AttachModel(GameObject go, List<SourcePart> source, VisualsDef v, out string error)
        {
            error = null;
            var parts = ModelParts(go);
            if (parts.Count == 0) { error = "the base unit has no visible mesh to replace"; return false; }
            var model = parts[0].RootBounds;
            foreach (var p in parts.Skip(1)) model.Encapsulate(p.RootBounds);
            var bodyMaterial = parts.SelectMany(p => p.Renderer.sharedMaterials).FirstOrDefault(IsBodyMaterial)
                ?? parts[0].Renderer.sharedMaterials.FirstOrDefault(m => m != null);
            int layer = parts[0].Renderer.gameObject.layer;

            var sourceBounds = Transform(source[0].Mesh.bounds, source[0].Matrix);
            foreach (var s in source.Skip(1)) sourceBounds.Encapsulate(Transform(s.Mesh.bounds, s.Matrix));
            var fit = Fit(model, sourceBounds, v);
            bool own = ModelRef.Look(v) == ModelRef.LookOwn;

            hiddenMesh ??= MakeHiddenMesh();
            var container = new GameObject("PalaceBuilder Model");
            container.layer = layer;
            container.transform.SetParent(go.transform, false);
            SetLocal(container.transform, fit);
            foreach (var s in source)
            {
                var child = new GameObject(s.Mesh.name);
                child.layer = layer;
                child.transform.SetParent(container.transform, false);
                SetLocal(child.transform, s.Matrix);
                child.AddComponent<MeshFilter>().sharedMesh = s.Mesh;
                var renderer = child.AddComponent<MeshRenderer>();
                int slots = Mathf.Max(1, s.Mesh.subMeshCount);
                var mats = new Material[slots];
                for (int i = 0; i < slots; i++)
                    mats[i] = own && s.Materials != null && s.Materials.Length > 0 ? s.Materials[Mathf.Min(i, s.Materials.Length - 1)] : bodyMaterial;
                renderer.sharedMaterials = mats;
            }
            foreach (var p in parts) Hide(p);
            if (v.Motion != ModelRef.MotionNone) AddMotion(go, container.transform, model.size.y);
            return true;
        }

        private static void SetLocal(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            var forward = (Vector3)m.GetColumn(2);
            var up = (Vector3)m.GetColumn(1);
            t.localRotation = forward.sqrMagnitude > 1e-12f && up.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(forward, up) : Quaternion.identity;
            t.localScale = new Vector3(((Vector3)m.GetColumn(0)).magnitude, up.magnitude, forward.magnitude) * (m.determinant < 0 ? -1f : 1f);
        }

        private static void AddMotion(GameObject go, Transform target, float height)
        {
            var motion = go.GetComponent<BuilderVisualMotion>() ?? go.AddComponent<BuilderVisualMotion>();
            motion.target = target;
            motion.height = Mathf.Max(0.2f, height);
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

        /// <param name="ownUvs">Use the model's own UVs (a glTF mesh keeps them in UV2; its UV1 addresses its baked
        /// colour texture), for painting it with the unit's "texture".</param>
        private static Mesh Bake(Mesh source, Matrix4x4 m, bool ownUvs)
        {
            var mesh = Object.Instantiate(source);
            mesh.name = source.name;
            if (ownUvs)
            {
                var own = new List<Vector2>();
                mesh.GetUVs(1, own);
                if (own.Count == mesh.vertexCount) mesh.SetUVs(0, own);
            }
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

    /// <summary>
    /// A procedural walk for a body no rig moves (a tree, a rock, another unit's static model): while the unit moves,
    /// the target hops (6 % of its height) and waddles (±6°) at a step rate that follows the speed; standing still it
    /// breathes slowly. Copied onto every spawned clone with the template (fields are serialized).
    /// </summary>
    public sealed class BuilderVisualMotion : MonoBehaviour
    {
        public Transform target;
        public float height = 1f;
        /// <summary>Set by the Enemy Studio's stage: walk in place at this speed (m/s) instead of measuring movement.</summary>
        public float previewSpeed = -1f;

        private Vector3 basePos, lastPos;
        private Quaternion baseRot;
        private float phase, speed;
        private bool ready;

        private void Start()
        {
            if (target == null) { enabled = false; return; }
            basePos = target.localPosition;
            baseRot = target.localRotation;
            lastPos = transform.position;
            phase = Random.value * 10f;
            ready = true;
        }

        private void LateUpdate()
        {
            if (!ready || target == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var pos = transform.position;
            float measured = new Vector2(pos.x - lastPos.x, pos.z - lastPos.z).magnitude / dt;
            lastPos = pos;
            float wanted = previewSpeed >= 0f ? previewSpeed : measured;
            speed = Mathf.Lerp(speed, Mathf.Min(wanted, 30f), 1f - Mathf.Exp(-dt * 10f));
            float walk = Mathf.Clamp01(speed / 1.5f);
            // About two steps per body height walked, at least 2.2 steps a second while moving.
            phase += dt * Mathf.Max(2.2f * walk, speed / Mathf.Max(height, 0.2f) * 2f) * Mathf.PI;
            float breathe = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.2f);
            float hop = Mathf.Abs(Mathf.Sin(phase)) * 0.06f * height * walk + breathe * 0.01f * height * (1f - walk);
            float waddle = Mathf.Sin(phase) * 6f * walk;
            target.localPosition = basePos + Vector3.up * (hop / Mathf.Max(transform.lossyScale.y, 1e-3f));
            target.localRotation = Quaternion.Euler(0f, 0f, waddle) * baseRot;
        }
    }
}
