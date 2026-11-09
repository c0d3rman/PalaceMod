using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Assets;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The look of a non-unit entity (`visual` on an EntityDef): any scenery mesh ("mesh:Tree_02", the map's or the
    cross-map library's), any unit's model ("unit:<catalog id>"), or an imported pack asset, drawn on a child
    "PalaceBuilder Visual" so the body (collider, cut, components) stays on the root. `meshScale` scales it, `tint`
    tints copies of its materials, `meshRotX/Y/Z` turn it, `meshOffset` moves it (metres), and `motion: "spin"`... is the projectile's `spin`.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyVisual
    {
        public const string ChildName = "PalaceBuilder Visual";

        /// <summary>Builds the visual under <paramref name="root"/>. Returns a note when part of it couldn't be made.</summary>
        public static string Build(GameObject root, JObject visual, PackEntry pack)
        {
            if (visual == null || visual.Type == JTokenType.Null) return null;
            var v = visual.ToObject<VisualsDef>();
            if (string.IsNullOrEmpty(v?.Mesh)) return null;
            var parts = new List<AssetVisuals.SourcePart>();
            string why = null;
            switch (ModelRef.Kind(v.Mesh, out string id))
            {
                case ModelKind.Scenery:
                    parts = AssetVisuals.SceneryParts(v.Mesh, out why);
                    break;
                case ModelKind.Unit:
                    parts = AssetVisuals.UnitParts(id, out why);
                    break;
                case ModelKind.Asset:
                    var mesh = pack != null ? BuilderServices.Assets.GetMesh(pack, v.Mesh) : null;
                    if (mesh != null) parts = [new AssetVisuals.SourcePart { Mesh = mesh.Value.mesh, Materials = mesh.Value.materials, Matrix = Matrix4x4.identity }];
                    else why = pack == null ? "pack not installed" : "asset not loaded";
                    break;
                default:
                    why = $"\"{v.Mesh}\" isn't a model reference";
                    break;
            }
            if (parts == null || parts.Count == 0) return $"visual {v.Mesh}: {why ?? "nothing to draw"}";

            var container = new GameObject(ChildName);
            container.transform.SetParent(root.transform, false);
            container.transform.localRotation = Quaternion.Euler(v.MeshRotX ?? 0f, v.MeshRotY ?? 0f, v.MeshRotZ ?? 0f);
            if (v.MeshOffset is { Length: 3 } o) container.transform.localPosition = new Vector3(o[0], o[1], o[2]);
            container.transform.localScale = Vector3.one * (v.MeshScale ?? 1f) * (v.Scale ?? 1f);
            Color? tint = v.Tint != null && ColorUtility.TryParseHtmlString(v.Tint, out var c) ? c : null;
            foreach (var s in parts)
            {
                var child = new GameObject(s.Mesh.name);
                child.transform.SetParent(container.transform, false);
                Set(child.transform, s.Matrix);
                child.AddComponent<MeshFilter>().sharedMesh = s.Mesh;
                var r = child.AddComponent<MeshRenderer>();
                var mats = s.Materials != null && s.Materials.Length > 0 ? (Material[])s.Materials.Clone() : [];
                if (tint != null)
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null) { mats[i] = new Material(mats[i]) { color = tint.Value }; }
                r.sharedMaterials = mats;
            }
            return null;
        }

        private static void Set(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            var f = (Vector3)m.GetColumn(2);
            var u = (Vector3)m.GetColumn(1);
            t.localRotation = f.sqrMagnitude > 1e-12f && u.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(f, u) : Quaternion.identity;
            t.localScale = new Vector3(((Vector3)m.GetColumn(0)).magnitude, u.magnitude, f.magnitude);
        }

        /// <summary>The visual's world bounds (or a small box at the root when it has none).</summary>
        public static Bounds Bounds(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(root.transform.position, Vector3.one * 0.5f);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
