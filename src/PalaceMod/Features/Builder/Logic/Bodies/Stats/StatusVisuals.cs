using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    A status's `visual`: a tint over the carrier's renderers (per-renderer property blocks, so shared materials stay
    untouched) and an effect parented to it while the status lasts. Several tinted statuses: the latest one shows; when
    it ends the previous one comes back.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class StatusVisuals
    {
        internal sealed class Applied
        {
            public LogicEntity E;
            public Color? Tint;
            public GameObject Fx;
        }

        /// <summary>
        /// Effect lookup: (fx name, parent) → a live effect object parented to it, or null. The effects catalog
        /// (Runtime/Catalog.cs, the `effect` action) installs it; until then statuses show only their tint.
        /// </summary>
        public static Func<string, Transform, GameObject> SpawnFx = (_, _) => null;

        private static readonly Dictionary<LogicEntity, List<Applied>> tints = [];
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor"), ColorId = Shader.PropertyToID("_Color"), ColorDim = Shader.PropertyToID("_ColorDim");

        public static Applied Apply(LogicEntity e, JObject visual)
        {
            var go = e.Go();
            if (go == null || visual == null) return null;
            var a = new Applied { E = e };
            if ((string)visual["tint"] is { } hex && ColorUtility.TryParseHtmlString(hex, out var c)) a.Tint = c;
            if ((string)visual["fx"] is { Length: > 0 } fx)
            {
                try { a.Fx = SpawnFx(fx, go.transform); }
                catch (Exception ex) { Log.For<LogicStatsManager>().Warn($"status fx \"{fx}\": {ex.Message}"); }
            }
            if (a.Tint.HasValue)
            {
                if (!tints.TryGetValue(e, out var list)) tints[e] = list = [];
                list.Add(a);
                Paint(go, a.Tint);
            }
            return a;
        }

        public static void Remove(Applied a)
        {
            if (a == null) return;
            if (a.Fx != null) UnityEngine.Object.Destroy(a.Fx);
            if (!a.Tint.HasValue || !tints.TryGetValue(a.E, out var list)) return;
            list.Remove(a);
            if (list.Count == 0) tints.Remove(a.E);
            var go = a.E.Go();
            if (go != null) Paint(go, list.Count > 0 ? list[list.Count - 1].Tint : null);
        }

        public static void Clear() => tints.Clear();

        private static void Paint(GameObject go, Color? tint)
        {
            var block = new MaterialPropertyBlock();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    block.Clear();
                    if (tint is Color t)
                    {
                        int prop = m.HasProperty(BaseColor) ? BaseColor : m.HasProperty(ColorId) ? ColorId : -1;
                        if (prop < 0) continue;
                        var orig = m.GetColor(prop);
                        var tinted = Color.Lerp(orig, t, 0.55f);
                        tinted.a = orig.a;
                        block.SetColor(prop, tinted);
                        if (m.HasProperty(ColorDim))
                        {
                            var dim = m.GetColor(ColorDim);
                            var td = Color.Lerp(dim, t * 0.7f, 0.45f);
                            td.a = dim.a;
                            block.SetColor(ColorDim, td);
                        }
                        r.SetPropertyBlock(block, i);
                    }
                    else r.SetPropertyBlock(null, i);
                }
            }
        }
    }
}
