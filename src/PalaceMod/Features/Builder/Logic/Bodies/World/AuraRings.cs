using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Aura rings (docs/builder-engine.md, "Aura rings"): the ground rings that show what acts over a radius.

    - Rules: Runtime/AuraScan.cs reads each compiled rule's rings (an `every`/`near` rule with a fixed-radius selector
      anchored at self, or any rule with `ring`). The ring hangs on its owner, so it follows it, and shows while the
      owner is alive and enabled, the rule is enabled and its `during` holds. A unit's rule whose filter names a
      label or template puts the ring on the things it names instead ("mirrors", collected from every template and
      place at Started).
    - Entities: `rings: [ { radius, ... } ]` adds rings of its own; `rings: false` drops all of them.
    - Statuses: StatusVisuals.Marker, a small ring under the carrier.

    The look is the game's own tower-range ring (RangeIndicatorHandler's prefabs: an intersection shader that draws
    the ring where its volume meets the ground, so it hugs slopes), cloned per ring and parented to the owner, with
    one shared tinted material per (style, colour). A plain LineRenderer circle stands in when the handler isn't
    there. Sync is event-driven (LogicWorld.RulesChanged, once per drain); the 10 Hz poll only flips visibility.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class AuraRings
    {
        public static readonly Color Buff = new(0.45f, 0.78f, 0.95f), Harm = new(0.92f, 0.33f, 0.25f), Neutral = new(0.98f, 0.82f, 0.45f);

        /// <summary>Settings > Mods > Builder: draw aura rings at all.</summary>
        public static System.Func<bool> Allowed = () => true;

        private sealed class Ring
        {
            public RingSpec Spec;
            public RuleInstance Rule;   // null: a mirror or an entity's own ring
            public GameObject Go;
            public float Scale = -1f;
        }

        private static readonly HashSet<LogicEntity> dirty = [];
        private static readonly Dictionary<LogicEntity, List<Ring>> rings = [];
        private static readonly List<RingSpec> mirrors = [];
        private static readonly List<LogicEntity> scratch = [];
        private static bool started;

        public static void Changed(LogicEntity e) { if (e != null) dirty.Add(e); }

        public static void Started(LogicWorld w)
        {
            Reset();
            started = true;
            var seen = new HashSet<string>();
            void Collect(IEnumerable<CompiledRule> rules, bool unit)
            {
                foreach (var r in rules)
                    if (r?.Rings != null)
                        foreach (var s in r.Rings)
                            if (Mirrors(s, unit) && seen.Add(s.Key + "|" + s.TargetLabel + "|" + s.TargetTemplate)) mirrors.Add(s);
            }
            foreach (var t in w.Templates.Keys.ToList()) Collect(w.TemplateRules(t), w.IsUnitTemplate(t));
            foreach (var e in w.Entities.ToList())
            {
                Collect(e.Rules.Select(ri => ri.Rule), e.Kind == "unit");
                dirty.Add(e);
            }
        }

        public static void Reset()
        {
            foreach (var list in rings.Values) foreach (var r in list) if (r.Go != null) Object.Destroy(r.Go);
            rings.Clear();
            dirty.Clear();
            mirrors.Clear();
            started = false;
        }

        private static bool Mirrors(RingSpec s, bool ownerIsUnit) =>
            (s.TargetLabel != null || s.TargetTemplate != null) && (s.On == "target" || s.On == "auto" && ownerIsUnit);

        /// <summary>After every drain: (re)build the rings of entities whose rules changed.</summary>
        public static void Settle(LogicWorld w)
        {
            if (!started || dirty.Count == 0) return;
            scratch.Clear();
            scratch.AddRange(dirty);
            dirty.Clear();
            foreach (var e in scratch) Sync(e);
            scratch.Clear();
        }

        private static void Drop(LogicEntity e)
        {
            if (!rings.TryGetValue(e, out var list)) return;
            foreach (var r in list) if (r.Go != null) Object.Destroy(r.Go);
            rings.Remove(e);
        }

        private static void Sync(LogicEntity e)
        {
            Drop(e);
            var go = e.Go();
            if (e.Destroyed || go == null || AuraScan.RingsOff(e.Def?["rings"])) return;
            var want = new List<Ring>();
            var keys = new HashSet<string>();
            void Want(RingSpec s, RuleInstance ri) { if (keys.Add(s.Key + "|" + s.During)) want.Add(new Ring { Spec = s, Rule = ri }); }
            bool unit = e.Kind == "unit";
            foreach (var ri in e.Rules)
                if (ri.Rule.Rings != null)
                    foreach (var s in ri.Rule.Rings)
                        if (!Mirrors(s, unit)) Want(s, ri);
            if (AuraScan.Explicit(e.Def?["rings"]) is { } own) foreach (var s in own) Want(s, null);
            foreach (var m in mirrors)
                if (m.TargetLabel != null && e.Labels.Contains(m.TargetLabel) || m.TargetTemplate != null && e.Template == m.TargetTemplate) Want(m, null);
            if (want.Count == 0) return;
            foreach (var r in want)
            {
                r.Go = Make(go.transform, r.Spec.Radius, ColorOf(r.Spec), r.Spec.Style);
                r.Go.SetActive(Visible(e, r));
            }
            rings[e] = want;
        }

        private static bool Visible(LogicEntity e, Ring r)
        {
            if (!Allowed() || !e.Alive || !e.Enabled) return false;
            if (r.Rule != null && (!r.Rule.Enabled || r.Rule.Done)) return false;
            return r.Spec.During switch
            {
                "night" => WorldCommon.IsNight,
                "day" => !WorldCommon.IsNight,
                _ => true,
            };
        }

        /// <summary>10 Hz: visibility, scale (a unit's `scale` stat), and rings whose owner went away.</summary>
        public static void Poll(LogicWorld w, double time)
        {
            if (rings.Count == 0) return;
            scratch.Clear();
            foreach (var kv in rings)
            {
                var e = kv.Key;
                var go = e.Go();
                if (e.Destroyed || go == null) { scratch.Add(e); continue; }
                float ps = Mathf.Max(0.01f, Mathf.Abs(go.transform.lossyScale.x));
                foreach (var r in kv.Value)
                {
                    if (r.Go == null) continue;
                    bool on = Visible(e, r);
                    if (r.Go.activeSelf != on) r.Go.SetActive(on);
                    if (on && r.Spec.Style != "line" && !Mathf.Approximately(r.Scale, ps))
                    {
                        r.Scale = ps;
                        r.Go.transform.localScale = Vector3.one * (r.Spec.Radius * 2f / ps);
                    }
                }
            }
            foreach (var e in scratch) Drop(e);
            scratch.Clear();
        }

        public static Color ColorOf(RingSpec s) =>
            s.Color != null && WorldCommon.TryColor(s.Color, out var c) ? c : s.Kind switch { "buff" => Buff, "harm" => Harm, _ => Neutral };

        // ---- the look -----------------------------------------------------------------------------------------

        private static readonly Dictionary<(string, Color), Material> mats = [];
        private static Material lineMat;
        private static readonly string[] ColorProps = ["_Main_Color", "_Color", "_BaseColor", "_TintColor", "_MainColor", "_Intersection_Color", "_IntersectionColor", "_EdgeColor"];

        private static GameObject Prefab(string style)
        {
            var h = RangeIndicatorHandler.instance;
            if (h == null) return null;
            return style == "soft" ? h.previewIndicatorPrefab : h.builtIndicatorPrefab;
        }

        /// <summary>A ring of <paramref name="radius"/> metres round <paramref name="parent"/>, following it.</summary>
        public static GameObject Make(Transform parent, float radius, Color color, string style)
        {
            var prefab = style == "line" ? null : Prefab(style);
            GameObject go;
            if (prefab != null && prefab.GetComponentInChildren<MeshRenderer>(true) is { } pr && pr.sharedMaterial != null)
            {
                go = Object.Instantiate(prefab, parent, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                float ps = Mathf.Max(0.01f, Mathf.Abs(parent.lossyScale.x));
                go.transform.localScale = Vector3.one * (radius * 2f / ps);
                foreach (var col in go.GetComponentsInChildren<Collider>(true)) Object.Destroy(col);
                var mr = go.GetComponentInChildren<MeshRenderer>(true);
                mr.sharedMaterial = Tinted(style, pr.sharedMaterial, color);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            else go = Line(parent, radius, color);
            go.name = "PalaceBuilder Aura Ring";
            return go;
        }

        private static Material Tinted(string style, Material src, Color color)
        {
            if (mats.TryGetValue((style, color), out var m) && m != null) return m;
            m = new Material(src) { name = "PalaceBuilder Aura Ring" };
            foreach (var p in ColorProps)
                if (m.HasProperty(p))
                {
                    var orig = m.GetColor(p);
                    m.SetColor(p, new Color(color.r, color.g, color.b, orig.a));
                }
            if (m.HasProperty("_Intersection_Depth")) m.SetFloat("_Intersection_Depth", RangeIndicatorHandler.TARGET_DEPTH);
            return mats[(style, color)] = m;
        }

        private static GameObject Line(Transform parent, float radius, Color color)
        {
            var go = new GameObject();
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.identity;
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            const int n = 48;
            float ps = Mathf.Max(0.01f, Mathf.Abs(parent.lossyScale.x));
            line.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                line.SetPosition(i, new Vector3(Mathf.Sin(a), 0.08f, Mathf.Cos(a)) * (radius / ps));
            }
            line.widthMultiplier = 0.15f;
            line.sharedMaterial = lineMat != null ? lineMat : lineMat = new Material(Shader.Find("Sprites/Default"));
            line.startColor = line.endColor = color;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        // ---- dev ------------------------------------------------------------------------------------------------

        public static string Status(string filter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"mirrors: {string.Join(", ", mirrors.Select(m => $"{m.TargetLabel ?? "is:" + m.TargetTemplate} {m.Radius}m {m.Kind}"))}");
            int n = 0, active = 0;
            foreach (var kv in rings)
                foreach (var r in kv.Value)
                {
                    n++;
                    if (r.Go != null && r.Go.activeInHierarchy) active++;
                    if (filter != null && !kv.Key.Id.Contains(filter) && kv.Key.Template != filter) continue;
                    var p = r.Go != null ? r.Go.transform.position : Vector3.zero;
                    var o = kv.Key.Go() is { } g && g ? g.transform.position : Vector3.zero;
                    var mr = r.Go != null ? r.Go.GetComponentInChildren<Renderer>() : null;
                    float worldR = r.Go != null ? (r.Spec.Style == "line" ? r.Spec.Radius : r.Go.transform.lossyScale.x / 2f) : 0f;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0} r={1} worldR={2:0.00} kind={3} style={4} active={5} ring=({6:0.0},{7:0.0},{8:0.0}) owner=({9:0.0},{10:0.0},{11:0.0}) rule={12} mat={13} color={14}",
                        kv.Key.Id, r.Spec.Radius, worldR, r.Spec.Kind, r.Spec.Style, r.Go != null && r.Go.activeInHierarchy, p.x, p.y, p.z, o.x, o.y, o.z,
                        r.Rule?.Rule.Path ?? "-", mr != null ? mr.sharedMaterial?.shader?.name : "none", ColorUtility.ToHtmlStringRGB(ColorOf(r.Spec))));
                }
            sb.Insert(0, $"rings: {n} ({active} active) on {rings.Count} entities\n");
            return sb.ToString().TrimEnd();
        }

        /// <summary>The game's indicator prefabs: renderers, shaders and their colour/float properties.</summary>
        public static string Prefabs()
        {
            var h = RangeIndicatorHandler.instance;
            if (h == null) return "no RangeIndicatorHandler";
            var sb = new StringBuilder();
            foreach (var (n, p) in new[] { ("built", h.builtIndicatorPrefab), ("preview", h.previewIndicatorPrefab), ("special", h.specialIndicatorPrefab), ("builtAll", h.builtAllIndicatorPrefab), ("previewAll", h.previewAllIndicatorPrefab) })
            {
                if (p == null) { sb.AppendLine($"{n}: null"); continue; }
                sb.AppendLine($"{n}: {p.name} scale={p.transform.localScale} comps={string.Join(",", p.GetComponentsInChildren<Component>(true).Select(c => c.GetType().Name))}");
                foreach (var r in p.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m == null) continue;
                    var props = new List<string>();
                    foreach (var name in m.GetPropertyNames(MaterialPropertyType.Vector)) props.Add($"{name}={m.GetVector(name)}");
                    foreach (var name in m.GetPropertyNames(MaterialPropertyType.Float)) props.Add($"{name}={m.GetFloat(name)}");
                    sb.AppendLine($"  {r.name} mesh={(r.GetComponent<MeshFilter>()?.sharedMesh?.name)} bounds={r.GetComponent<MeshFilter>()?.sharedMesh?.bounds} shader={m.shader.name} {string.Join(" ", props)}");
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
