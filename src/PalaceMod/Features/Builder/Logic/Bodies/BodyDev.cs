using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Dev tools for bodies: `logic-l2 status | build <template|json> <x> <z> [rotY] | signal <entity> <input> [value]`.
    `build` takes a template id from the session pack, or an inline EntityDef as JSON (no spaces needed: the args are
    joined back).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyDev
    {
        public static string Status() =>
            $"world {(LogicWorld.Current != null ? "on" : "off")}; entities {LogicWorld.Current?.Entities.Count() ?? 0}; " +
            $"projectiles: builder {BuilderProjectile.Live.Count}, vanilla {VanillaProjectiles.Count} (scaled {VanillaProjectiles.SlowedTotal}, drifted {VanillaProjectiles.DriftedTotal}, redirected {VanillaProjectiles.RedirectedTotal}); areas {BuilderArea.All.Count}; movers {BuilderMover.All.Count}; " +
            $"pickups {BuilderPickup.All.Count}; {Surfaces.Describe()}; {Walkables.Describe()}; {Spaces.Describe()}; drifts {(Drifts.Any ? Drifts.Drifting.Count() : 0)}; " +
            $"last impacts: {string.Join(" ", BuilderProjectile.LastImpacts)}";

        /// <summary>`logic-l2 look [filter]`: every live unit entity (id, template or object name containing filter) and
        /// how it would draw: active, layer (and whether the main camera culls it), scale, and each renderer's state,
        /// materials and bounds. For checking looks headless, where no pixels are drawn.</summary>
        public static string Look(string filter)
        {
            var world = LogicWorld.Current;
            if (world == null) return "no logic world";
            var cam = Camera.main;
            bool Seen(int layer) => cam == null || (cam.cullingMask & (1 << layer)) != 0;
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var e in world.Entities.ToList())
            {
                var go = e.Go();
                if (e.Kind != "unit" || go == null) continue;
                if (filter != null && !(e.Id ?? "").Contains(filter) && !(e.Template ?? "").Contains(filter) && !go.name.Contains(filter)) continue;
                n++;
                sb.Append($"{e.Id} [{e.Template}] \"{go.name}\" active={go.activeInHierarchy} layer={LayerMask.LayerToName(go.layer)}{(Seen(go.layer) ? "" : "(culled)")} " +
                          $"scale={go.transform.lossyScale.x:0.##} pos={go.transform.position}\n");
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("PalaceBuilder"))
                        sb.Append($"  [{t.name}] localPos={t.localPosition} localScale={t.localScale} lossy={t.lossyScale}\n");
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                    string mats = string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name + "#" + m.GetInstanceID() + (m.shader == null ? "/noshader" : "")));
                    sb.Append($"  {r.name}: {(r.enabled ? "on" : "OFF")} {(r.gameObject.activeInHierarchy ? "active" : "INACTIVE")} " +
                              $"layer={LayerMask.LayerToName(r.gameObject.layer)}{(Seen(r.gameObject.layer) ? "" : "(culled)")} " +
                              $"forceOff={r.forceRenderingOff} mats=[{mats}] bounds={r.bounds.center} size {r.bounds.size}{Instanced(r)}\n");
                }
            }
            return n == 0 ? "no matching unit" : sb.ToString();
        }

        /// <summary>`logic-l2 bodies [filter]`: every live non-unit entity (projectiles, areas, props...) with its position,
        /// visible renderers and what it draws (area look, landing marker), then every live particle effect in the scene
        /// (playing, particles, position). For checking flights and effects headless, where no pixels are drawn.</summary>
        public static string Bodies(string filter)
        {
            var world = LogicWorld.Current;
            var sb = new System.Text.StringBuilder();
            sb.Append($"t={Time.time:0.00}\n");
            if (world != null)
                foreach (var e in world.Entities.ToList())
                {
                    var go = e.Go();
                    if (e.Kind == "unit" || go == null) continue;
                    if (filter != null && !(e.Id ?? "").Contains(filter) && !(e.Template ?? "").Contains(filter)) continue;
                    var rs = go.GetComponentsInChildren<Renderer>(false);
                    int on = rs.Count(r => r.enabled);
                    string parts = string.Join(",", go.transform.Cast<Transform>().Where(t => t.gameObject.activeSelf).Select(t => t.name.Replace("PalaceBuilder ", "")));
                    var p = go.transform.position;
                    var size = on > 0 ? BodyVisual.Bounds(go).size : Vector3.zero;
                    sb.Append($"{e.Id} [{e.Kind} {e.Template}] active={go.activeInHierarchy} pos=({p.x:0.0},{p.y:0.0},{p.z:0.0}) renderers {on}/{rs.Length} size={size.x:0.0#}x{size.y:0.0#}x{size.z:0.0#} children=[{parts}]\n");
                }
            foreach (var m in Object.FindObjectsOfType<LandingMarker>())
            {
                var p = m.transform.position;
                var fill = m.transform.Find("Fill");
                int on = m.GetComponentsInChildren<Renderer>().Count(r => r.enabled);
                sb.Append($"  landing marker pos=({p.x:0.0},{p.y:0.0},{p.z:0.0}) renderers {on} fill={(fill != null ? fill.localScale.x : 0f):0.00}\n");
            }
            foreach (var ps in Object.FindObjectsOfType<ParticleSystem>())
            {
                if (ps.transform.parent != null && ps.transform.parent.GetComponentInParent<ParticleSystem>() != null) continue;   // roots only
                string root = ps.transform.root.name;
                if (!root.Contains("(Clone)") && !root.StartsWith("PalaceBuilder") && !root.StartsWith("PalaceMod")) continue;   // spawned effects, not the map's own
                var p = ps.transform.position;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                sb.Append($"  fx \"{root}\"/{ps.name} playing={ps.isPlaying} alive={ps.IsAlive(true)} particles={ps.particleCount} " +
                          $"renderer={(r == null ? "none" : r.enabled ? "on" : "OFF")} pos=({p.x:0.0},{p.y:0.0},{p.z:0.0})\n");
            }
            return sb.ToString();
        }

        /// <summary>A GPUInstanced renderer (the game draws unit pieces through GPUInstancingManager, with the renderer
        /// itself off): the material it thinks it's drawn with, and the (mesh, material) entries its transform is in.</summary>
        private static string Instanced(Renderer r)
        {
            var gi = r.GetComponent<GPUInstanced>();
            if (gi == null) return "";
            var t = HarmonyLib.Traverse.Create(gi);
            var mat = t.Field("mat").GetValue<Material>();
            string where = "no manager";
            if (GPUInstancingManager.Instance != null &&
                HarmonyLib.Traverse.Create(GPUInstancingManager.Instance).Field("matMeshPairToRenderersDictionary").GetValue() is System.Collections.IDictionary dict)
            {
                var hits = new System.Collections.Generic.List<string>();
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    var list = HarmonyLib.Traverse.Create(kv.Value).Field("transforms").GetValue<System.Collections.Generic.List<Transform>>();
                    if (list == null || !list.Contains(r.transform)) continue;
                    var m = HarmonyLib.Traverse.Create(kv.Key).Field("material").GetValue<Material>();
                    hits.Add(m == null ? "null" : $"{m.name}#{m.GetInstanceID()}{(m.enableInstancing ? "" : "(no instancing)")}");
                }
                where = hits.Count == 0 ? "NOT REGISTERED" : string.Join(",", hits);
            }
            return $" gpui[started={t.Field("started").GetValue<bool>()} enabled={t.Field("eenabled").GetValue<bool>()} " +
                   $"mat={(mat == null ? "null" : mat.name + "#" + mat.GetInstanceID())} in={where}]";
        }

        public static string Build(string[] a)
        {
            if (a.Length < 3) return "build <template|{json}> <x> <z> [rotY] [id]";
            LogicWorld.Current ??= new LogicWorld();
            // The JSON may have been split on spaces: everything before the last numeric args is the template.
            int n = a.Length;
            int numStart = n;
            while (numStart > 0 && float.TryParse(a[numStart - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) numStart--;
            string id = null;
            if (numStart == n && n >= 4) { id = a[n - 1]; n--; numStart = n; while (numStart > 0 && float.TryParse(a[numStart - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) numStart--; }
            string template = string.Join(" ", a.Take(numStart));
            var nums = a.Skip(numStart).Take(n - numStart).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (nums.Length < 2) return "need x and z";
            var pos = new Vector3(nums[0], 0f, nums[1]);
            pos.y = BodyGround.Below(pos, out float g) ? g : 0f;
            float rot = nums.Length > 2 ? nums[2] : 0f;
            var e = new LogicEntity { Id = id };
            if (template.StartsWith("{"))
            {
                var def = JObject.Parse(template);
                e.Def = def;
                e.Template = (string)def["id"];
                e.Name = (string)def["name"];
            }
            else
            {
                e.Template = template;
                e.Def = EntityFactory.Instance.EntityDef(template)?.DeepClone() as JObject;
            }
            if (!EntityFactory.Instance.Build(e, pos.ToVec(), rot, out string error)) return "failed: " + error;
            return $"built {e.Id} ({e.Kind}, {e.Team}) at {pos}";
        }

        private static readonly System.Collections.Generic.Queue<string> events = new();

        /// <summary>Every event a body raises goes through here (kept for `logic-l2 events`).</summary>
        public static void Raise(string name, LogicEntity subject, LogicEntity other = null, double value = 0, Vec? pos = null, string text = null)
        {
            events.Enqueue($"{name}:{subject?.Id}{(other != null ? "<-" + other.Id : "")}{(text != null ? "/" + text : "")}");
            while (events.Count > 30) events.Dequeue();
            LogicEvents.Raise(name, subject, other, value, pos, text);
        }

        public static string Events()
        {
            var s = string.Join(" | ", events);
            events.Clear();
            return s;
        }

        /// <summary>vp drift &lt;x&gt; &lt;z&gt; &lt;m/s&gt; | vp redirect &lt;role&gt; | vp point &lt;x&gt; &lt;z&gt; | vp list: act on every vanilla projectile in flight.</summary>
        public static string Vanilla(string[] a)
        {
            var list = new System.Collections.Generic.List<LogicEntity>();
            VanillaProjectiles.Query(Vector3.zero, 0f, list);
            string sub = a.Length > 0 ? a[0] : "list";
            if (sub == "field")
            {
                // field <x> <z> <r> <drift dx dz speed | point px pz | off>: acts on every vanilla projectile that enters
                if (a.Length > 1 && a[1] == "off") { field = null; return $"field off; it acted on {fieldHits} projectiles"; }
                fieldCenter = new Vector3(F(a[1]), 0f, F(a[2]));
                fieldRadius = F(a[3]);
                field = a.Skip(4).ToArray();
                fieldHits = 0;
                fieldSeen.Clear();
                return "field on";
            }
            int n = 0;
            foreach (var e in list)
            {
                switch (sub)
                {
                    case "drift":
                        var v = new Vector3(F(a[1]), 0f, F(a[2])).normalized * F(a[3]);
                        Drifts.Set(e, "dev", () => v, 0);
                        n++;
                        break;
                    case "redirect":
                        var t = DevParams.Resolve(a[1], new LogicContext { Self = e })?.FirstOrDefault();
                        if (BodyLaunch.Redirect(e, t, null)) n++;
                        break;
                    case "point":
                        if (BodyLaunch.Redirect(e, null, new Vector3(F(a[1]), 0f, F(a[2])))) n++;
                        break;
                    case "destroy":
                        if (VanillaProjectiles.Destroy(e)) n++;
                        break;
                }
            }
            return $"{list.Count} vanilla projectiles in flight ({string.Join(", ", list.Take(6).Select(e => $"{e.Template} by {e.Owner?.Id} at {e.Position}"))}); {sub} on {n}";
        }

        private static string[] field;
        private static Vector3 fieldCenter;
        private static float fieldRadius;
        private static int fieldHits;
        private static readonly System.Collections.Generic.HashSet<LogicEntity> fieldSeen = [];
        private static readonly System.Collections.Generic.List<LogicEntity> fieldList = [];

        /// <summary>The dev field's 10 Hz step.</summary>
        public static void FieldStep()
        {
            if (field == null || field.Length == 0) return;
            VanillaProjectiles.Query(fieldCenter, fieldRadius, fieldList);
            foreach (var e in fieldList)
            {
                if (!fieldSeen.Add(e)) continue;
                fieldHits++;
                if (field[0] == "drift") { var v = new Vector3(F(field[1]), 0f, F(field[2])).normalized * F(field[3]); Drifts.Set(e, "dev", () => v, 0); }
                else if (field[0] == "point") BodyLaunch.Redirect(e, null, new Vector3(F(field[1]), 0f, F(field[2])));
            }
            fieldList.Clear();
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>interact &lt;entity&gt; [holdFrames]: the king focuses it, presses interact and holds (pays) like the
        /// player. An entity without an interact block that is a build slot (attached) uses the slot's own build prompt.</summary>
        public static string Interact(string[] a)
        {
            var world = LogicWorld.Current;
            var e = a.Length > 0 ? world?.FindNamed(a[0].TrimStart('@')) ?? world?.Find(a[0]) : null;
            var go = e?.Go();
            InteractorBase i = go == null ? null : go.GetComponentInChildren<BuilderInteractor>();
            if (i == null && go != null) i = go.GetComponentInChildren<BuildingInteractor>(true) ?? go.GetComponentInParent<BuildSlot>(true)?.buildingInteractor;
            var player = PlayerInteraction.instance;
            if (i == null || player == null) return "no interactable / no king";
            int frames = a.Length > 1 ? int.Parse(a[1]) : 600;
            Plugin.Instance.StartCoroutine(Hold(i, player, frames));
            var at = i.transform.position;
            return $"{e.Id}: can interact {i.CanBeInteractedWith}, prompt \"{i.ReturnTooltip()}\", interactor at ({at.x:0.##}, {at.z:0.##}), entity at {e.Position}";
        }

        /// <summary>focus: what the king's own interaction has focused now (the game's proximity pick).</summary>
        public static string Focus()
        {
            var player = PlayerInteraction.instance;
            if (player == null) return "no king";
            var f = player.FocussedInteractor;
            if (f == null) return "nothing focused";
            var e = f is BuilderInteractor bi ? bi.Entity : LogicGame.EntityOf(f.gameObject);
            return $"focused {f.GetType().Name} of {e?.Id ?? f.name}, prompt \"{f.ReturnTooltip()}\"";
        }

        private static System.Collections.IEnumerator Hold(InteractorBase i, PlayerInteraction player, int frames)
        {
            i.Focus(player);
            i.InteractionBegin(player);
            for (int f = 0; f < frames && i != null; f++) { i.InteractionHold(player); yield return null; }
            if (i != null) { i.InteractionEnd(player); i.Unfocus(player); }
        }

        public static string Signal(string[] a)
        {
            if (a.Length < 2) return "signal <entity id|@name> <input> [value]";
            var world = LogicWorld.Current;
            var e = world?.FindNamed(a[0].TrimStart('@')) ?? world?.Find(a[0]);
            if (e == null) return $"no entity {a[0]}";
            double v = a.Length > 2 ? double.Parse(a[2], CultureInfo.InvariantCulture) : 0;
            return BodyActions.Input(e, a[1], v) ? $"{e.Id} handled {a[1]}" : $"{e.Id} has no input {a[1]} (inputs: {string.Join(", ", BodyActions.InputsOf(e))})";
        }
    }
}
