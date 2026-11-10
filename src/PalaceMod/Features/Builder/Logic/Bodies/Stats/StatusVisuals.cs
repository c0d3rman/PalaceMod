using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    A status's `visual`: a tint over the carrier's renderers (tinted material clones, swapped back when it ends) and an
    effect parented to it while the status lasts. Several tinted statuses: the latest one shows; when it ends the
    previous one comes back. `marker`: a small ring under the carrier (AuraRings' look), by default on a status that
    changes something (mods or flags) but has no tint and no fx, so an aura's effect shows on whoever has it; true,
    false or a colour. Its default colour is the aura colours': harm for a status that hinders, else buff.
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
            public GameObject Marker;
        }

        /// <summary>
        /// Effect lookup: (fx name, parent) → a live effect object parented to it, or null. The effects catalog
        /// (Runtime/Catalog.cs, the `effect` action) installs it; until then statuses show only their tint.
        /// </summary>
        public static Func<string, Transform, GameObject> SpawnFx = (_, _) => null;

        private static readonly Dictionary<LogicEntity, List<Applied>> tints = [];
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor"), ColorId = Shader.PropertyToID("_Color"), ColorDim = Shader.PropertyToID("_ColorDim");

        public static Applied Apply(LogicEntity e, JObject visual, JObject def = null)
        {
            var go = e.Go();
            if (go == null) return null;
            var a = new Applied { E = e };
            if (MarkerColor(visual, def) is Color mc) a.Marker = AuraRings.Make(go.transform, MarkerRadius(go), mc, "ring");
            if (visual == null) return a.Marker != null ? a : null;
            if ((string)visual["tint"] is { } hex && WorldCommon.TryColor(hex, out var c)) a.Tint = c;
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
            if (a.Marker != null) UnityEngine.Object.Destroy(a.Marker);
            if (!a.Tint.HasValue || !tints.TryGetValue(a.E, out var list)) return;
            list.Remove(a);
            if (list.Count == 0) tints.Remove(a.E);
            var go = a.E.Go();
            if (go != null) Paint(go, list.Count > 0 ? list[list.Count - 1].Tint : null);
        }

        private static readonly string[] Hindering = ["stunned", "disarmed", "rooted", "silenced", "noIncome"];

        /// <summary>The marker's colour, or null for none (see the header).</summary>
        private static Color? MarkerColor(JObject visual, JObject def)
        {
            var m = visual?["marker"];
            if (m is JValue { Type: JTokenType.String } ms && WorldCommon.TryColor((string)ms, out var mc)) return mc;
            bool on = m is JValue { Type: JTokenType.Boolean } mb
                ? (bool)mb
                : visual?["tint"] == null && visual?["fx"] == null && (def?["mods"] is JObject { Count: > 0 } || def?["flags"] is JArray { Count: > 0 });
            if (!on) return null;
            return Hinders(def) ? AuraRings.Harm : AuraRings.Buff;
        }

        private static bool Hinders(JObject def)
        {
            if (def?["flags"] is JArray flags && flags.Any(f => Array.IndexOf(Hindering, (string)f) >= 0)) return true;
            if (def?["mods"] is not JObject mods) return false;
            foreach (var p in mods.Properties())
            {
                if (p.Value is not JObject mo || mo["mult"] is not JValue { Type: JTokenType.Integer or JTokenType.Float } mult) continue;
                double x = (double)mult;
                if (p.Name is "damageTaken" or "attackCooldown" ? x > 1 : x < 1) return true;
            }
            return false;
        }

        private static float MarkerRadius(GameObject go)
        {
            var cc = go.GetComponentInChildren<CharacterController>();
            float r = cc != null ? cc.radius * Mathf.Abs(cc.transform.lossyScale.x) : 0.6f;
            return Mathf.Clamp(r + 0.35f, 0.6f, 3f);
        }

        public static void Clear()
        {
            tints.Clear();
            originals.Clear();
        }

        // Untinted materials per renderer while a tint is on, and tinted clones per (material, colour). Units are drawn
        // through the game's GPU instancing, which follows renderer.sharedMaterial every frame (GPUInstanced.Update),
        // so swapping materials works for instanced and plain renderers alike (property blocks don't reach instancing).
        private static readonly Dictionary<Renderer, Material[]> originals = [];
        private static readonly Dictionary<(Material, Color), Material> clones = [];
        private static readonly System.Reflection.FieldInfo flasherPairs = HarmonyLib.AccessTools.Field(typeof(MaterialFlasherFX), "targetRendererMaterialPairs");

        private static void Paint(GameObject go, Color? tint)
        {
            var pairs = new Dictionary<Renderer, MaterialFlasherFX.RendererMaterialPair>();
            foreach (var f in go.GetComponentsInChildren<MaterialFlasherFX>(true))
                if (flasherPairs?.GetValue(f) is List<MaterialFlasherFX.RendererMaterialPair> list)
                    foreach (var pr in list) if (pr?.renderer != null) pairs[pr.renderer] = pr;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                pairs.TryGetValue(r, out var pair);
                if (!originals.TryGetValue(r, out var orig))
                {
                    if (tint == null) continue;
                    orig = r.sharedMaterials;
                    if (pair != null && orig.Length > 0) orig[0] = pair.originalMaterial;   // not a flash in progress
                    originals[r] = orig;
                }
                Material[] mats;
                if (tint is Color t)
                {
                    mats = new Material[orig.Length];
                    for (int i = 0; i < orig.Length; i++) mats[i] = Tinted(orig[i], t);
                }
                else
                {
                    mats = orig;
                    originals.Remove(r);
                }
                r.sharedMaterials = mats;
                if (pair != null && mats.Length > 0) pair.originalMaterial = mats[0];
            }
        }

        private static Material Tinted(Material m, Color t)
        {
            if (m == null) return null;
            int prop = m.HasProperty(BaseColor) ? BaseColor : m.HasProperty(ColorId) ? ColorId : -1;
            if (prop < 0) return m;
            if (clones.TryGetValue((m, t), out var c) && c != null) return c;
            c = new Material(m) { name = m.name };
            var orig = m.GetColor(prop);
            var tinted = Color.Lerp(orig, t, 0.55f);
            tinted.a = orig.a;
            c.SetColor(prop, tinted);
            if (c.HasProperty(ColorDim))
            {
                var dim = m.GetColor(ColorDim);
                var td = Color.Lerp(dim, t * 0.7f, 0.45f);
                td.a = dim.a;
                c.SetColor(ColorDim, td);
            }
            return clones[(m, t)] = c;
        }
    }
}
