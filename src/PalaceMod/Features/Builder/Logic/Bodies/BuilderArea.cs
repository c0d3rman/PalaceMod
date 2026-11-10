using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `area` (docs/builder-engine.md, "Components"): a zone that reports what's inside it. Polled on the world's 10 Hz
    steps (AreaPoller): `enter`/`exit` (other = the entity), `inside` (for each entity, after every full `tick` it has
    spent inside), `occupied` (the first one in) and `empty` (the last one out). Membership is by position (XZ),
    within the shape relative to the area's position and heading; height is ignored. `show` draws the outline or a
    fill in `color` on the ground. Inputs: show, hide (and the core's enable/disable).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class BuilderArea : MonoBehaviour
    {
        public LogicEntity Entity;
        public string Watch = "unit or king";
        public float Tick = -1f;
        /// <summary>none | outline | fill; unset: outline, except on an area that only marks walkable ground
        /// (`surface`), which stays hidden.</summary>
        public string Show = "none";
        public Color Color = new(0.82f, 0.25f, 0.25f, 1f);
        public AreaShape Shape;

        private readonly Dictionary<LogicEntity, double> inside = [];
        private GameObject drawn;

        public static readonly HashSet<BuilderArea> All = [];

        public void Configure(LogicEntity e, JObject a)
        {
            Entity = e;
            Shape = AreaShape.Parse(a["shape"] as JObject) ?? new AreaShape { Radius = 3f };
            Watch = (string)a["watch"] ?? Watch;
            Tick = a["tick"] is JToken t && t.Type is JTokenType.Integer or JTokenType.Float ? Mathf.Max(0.1f, (float)t) : -1f;
            Show = (string)a["show"] ?? (a["surface"] is JToken sf && sf.Type != JTokenType.Null ? "none" : "outline");
            if ((string)a["color"] is { } c && WorldCommon.TryColor(c, out var col)) Color = col;
            if (a["surface"] is JToken surface && surface.Type != JTokenType.Null) Surfaces.Add(this, surface);
        }

        private void OnDestroy() => Surfaces.Remove(this);

        private void OnEnable() { All.Add(this); Redraw(); Surfaces.Toggled(this); }
        private void OnDisable()
        {
            All.Remove(this);
            Surfaces.Toggled(this);
            // Switched off: everything inside leaves.
            if (Entity != null && LogicWorld.Current != null)
            {
                foreach (var e in inside.Keys.ToList()) BodyDev.Raise("exit", Entity, e);
                if (inside.Count > 0) BodyDev.Raise("empty", Entity);
            }
            inside.Clear();
        }

        public bool Contains(Vector3 p) => Shape.Contains(transform.InverseTransformPoint(new Vector3(p.x, transform.position.y, p.z)), transform.lossyScale.x);

        public bool Contains(LogicEntity e) => e != null && e.Exists && Contains(e.Position.ToVector3());

        public IEnumerable<LogicEntity> Inside => inside.Keys;

        private readonly List<LogicEntity> candidates = [];
        private readonly HashSet<LogicEntity> now = [];

        /// <summary>
        /// Same-frame entries (AreaPoller.Settle, after every drain): what is inside now but wasn't raises `enter` (and
        /// `occupied`) at once, from the shared candidate list of this settle. Exits and `inside` ticks stay on the
        /// 10 Hz poll.
        /// </summary>
        public void Enter(double time, List<LogicEntity> all)
        {
            if (Entity == null || Entity.Destroyed || !Entity.Enabled) return;
            var filter = BodyFilter.Compile(Watch);
            var center = transform.position;
            float reach = Shape.Reach * transform.lossyScale.x + 1f, r2 = reach * reach;
            bool wasEmpty = inside.Count == 0;
            LogicEntity first = null;
            foreach (var c in all)
            {
                if (c == Entity || inside.ContainsKey(c) || !c.Alive) continue;
                var p = c.Position;
                float dx = p.X - center.x, dz = p.Z - center.z;
                if (dx * dx + dz * dz > r2 || !Contains(c) || !filter(Entity, c)) continue;
                inside[c] = time;
                first ??= c;
                BodyDev.Raise("enter", Entity, c);
            }
            if (wasEmpty && first != null) BodyDev.Raise("occupied", Entity, first);
        }

        /// <summary>One 10 Hz step.</summary>
        public void Poll(double time)
        {
            if (Entity == null || Entity.Destroyed || !Entity.Enabled) return;
            BodySpatial.Query(transform.position, Shape.Reach * transform.lossyScale.x + 1f, candidates);
            now.Clear();
            var filter = BodyFilter.Compile(Watch);
            foreach (var c in candidates)
                if (c != Entity && c.Alive && filter(Entity, c) && Contains(c)) now.Add(c);
            bool wasEmpty = inside.Count == 0;
            foreach (var gone in inside.Keys.Where(k => !now.Contains(k)).ToList())
            {
                inside.Remove(gone);
                BodyDev.Raise("exit", Entity, gone);
            }
            foreach (var c in now)
            {
                if (!inside.ContainsKey(c))
                {
                    inside[c] = time;
                    BodyDev.Raise("enter", Entity, c);
                }
                else if (Tick > 0 && time - inside[c] >= Tick - 1e-6)
                {
                    inside[c] = time;
                    BodyDev.Raise("inside", Entity, c);
                }
            }
            if (wasEmpty && inside.Count > 0) BodyDev.Raise("occupied", Entity, now.FirstOrDefault());
            else if (!wasEmpty && inside.Count == 0) BodyDev.Raise("empty", Entity);
        }

        // ---- drawing ---------------------------------------------------------------------------------------------

        public void SetShow(string show) { Show = show; Redraw(); }

        private void Redraw()
        {
            if (drawn != null) Destroy(drawn);
            if (Show is not ("outline" or "fill")) return;
            drawn = AreaDraw.Make(transform, Shape, Color, Show == "fill");
        }
    }

    internal sealed class AreaShape
    {
        public float Radius = -1f;
        public Vector2 Size;
        public Vector2[] Points;
        /// <summary>A sector (cone) along +Z: its radius and width in degrees (Points then hold its outline).</summary>
        public float SectorRadius = -1f, SectorAngle = -1f;

        public static AreaShape Sector(float radius, float angle) => new()
        {
            SectorRadius = radius, SectorAngle = angle,
            Points = LightMath.SectorOutline(radius, angle).Select(q => new Vector2((float)q.x, (float)q.z)).ToArray(),
        };

        public static AreaShape Parse(JObject s)
        {
            if (s == null) return null;
            if (s["sector"] is JObject sec) return Sector(Mathf.Max(0.1f, sec.Value<float?>("radius") ?? 6f), Mathf.Clamp(sec.Value<float?>("angle") ?? 60f, 1f, 360f));
            if (s["circle"] is JObject c) return new AreaShape { Radius = c.Value<float?>("radius") ?? 3f };
            if (s["rect"] is JObject r && r["size"] is JArray size && size.Count == 2) return new AreaShape { Size = new Vector2((float)size[0], (float)size[1]) };
            if (s["polygon"] is JObject p && p["points"] is JArray pts && pts.Count >= 3)
                return new AreaShape { Points = pts.Select(t => new Vector2((float)t[0], (float)t[t.Count() > 2 ? 2 : 1])).ToArray() };
            return null;
        }

        /// <summary>Furthest a point inside can be from the centre.</summary>
        public float Reach => Radius > 0 ? Radius : Points != null ? Points.Max(p => p.magnitude) : Size.magnitude / 2f;

        /// <summary>A point in the area's local space (unscaled XZ).</summary>
        public bool Contains(Vector3 local, float scale)
        {
            var p = new Vector2(local.x, local.z) * scale;
            if (SectorAngle > 0) return LightMath.SectorContains(p.x, p.y, SectorRadius, SectorAngle);
            if (Radius > 0) return p.sqrMagnitude <= Radius * Radius;
            if (Points != null) return InPolygon(p, Points);
            return Mathf.Abs(p.x) <= Size.x / 2f && Mathf.Abs(p.y) <= Size.y / 2f;
        }

        public Vector2[] Outline()
        {
            if (Points != null) return Points;
            if (Radius > 0) return Enumerable.Range(0, 48).Select(i => new Vector2(Mathf.Sin(i * Mathf.PI * 2 / 48), Mathf.Cos(i * Mathf.PI * 2 / 48)) * Radius).ToArray();
            float w = Size.x / 2f, d = Size.y / 2f;
            return [new(-w, -d), new(-w, d), new(w, d), new(w, -d)];
        }

        private static bool InPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }
    }

    internal static class AreaDraw
    {
        private static Material material;

        private static Material Mat => material != null ? material : material = new Material(Shader.Find("Sprites/Default"));

        /// <summary>An outline (line) or a translucent fill on the ground, following the area.</summary>
        public static GameObject Make(Transform parent, AreaShape shape, Color color, bool fill)
        {
            var go = new GameObject("PalaceBuilder Area Look");
            go.transform.SetParent(parent, false);
            var outline = shape.Outline();
            var pts = outline.Select(p => Ground(parent, p)).ToArray();
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            line.positionCount = pts.Length;
            line.SetPositions(pts);
            line.widthMultiplier = 0.15f;
            line.sharedMaterial = Mat;
            line.startColor = line.endColor = color;
            if (fill)
            {
                var mesh = new Mesh { name = "PalaceBuilder Area" };
                var v = new List<Vector3> { Vector3.up * 0.05f };
                v.AddRange(pts);
                var tris = new List<int>();
                for (int i = 0; i < pts.Length; i++) { tris.Add(0); tris.Add(1 + i); tris.Add(1 + (i + 1) % pts.Length); }
                mesh.SetVertices(v);
                mesh.SetTriangles(tris, 0);
                mesh.SetColors(Enumerable.Repeat(new Color(color.r, color.g, color.b, 0.3f), v.Count).ToList());
                mesh.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = Mat;
            }
            return go;
        }

        // Local point lifted to just above the ground under it.
        private static Vector3 Ground(Transform parent, Vector2 p)
        {
            var world = parent.TransformPoint(new Vector3(p.x, 0f, p.y));
            float y = BodyGround.Below(world, out float g) ? g : world.y;
            var local = parent.InverseTransformPoint(new Vector3(world.x, y + 0.08f, world.z));
            return local;
        }
    }

    /// <summary>Drives every area on the world's 10 Hz steps; answers IAreas for `in:@zone`.</summary>
    internal sealed class AreaPoller : IAreas
    {
        public static readonly AreaPoller Instance = new();

        public static void Step(double time)
        {
            foreach (var a in BuilderArea.All.ToList())
                if (a != null) a.Poll(time);
        }

        private static readonly List<LogicEntity> settleAll = [];

        /// <summary>
        /// After every drain (LogicWorld.Settled, so at least once a frame): entries since the last look raise `enter`
        /// now, in the same drain. A unit spawned into an area, an area laid under a unit, or a unit walking in gets
        /// its `enter` rules (e.g. a status) before the game's next Update, so towers never see it in between.
        /// </summary>
        public static void Settle(LogicWorld w)
        {
            if (BuilderArea.All.Count == 0) return;
            BodySpatial.Query(Vector3.zero, 0f, settleAll);
            foreach (var a in BuilderArea.All.ToList())
                if (a != null) a.Enter(w.Time, settleAll);
            settleAll.Clear();
        }

        public bool? Contains(LogicEntity area, LogicEntity e)
        {
            var a = area?.Go()?.GetComponent<BuilderArea>();
            return a == null ? null : a.Contains(e);
        }

        /// <summary>`in:@template`: inside any live, enabled area made from that template.</summary>
        public bool? InAny(string template, LogicEntity e)
        {
            foreach (var a in BuilderArea.All)
                if (a != null && a.Entity is { Destroyed: false, Enabled: true } ae && ae.Template == template && a.Contains(e)) return true;
            return false;
        }
    }
}
