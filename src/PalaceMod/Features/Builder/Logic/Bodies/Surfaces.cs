using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Pathfinding;
using UnityEngine;

/*
    Per-unit walkability (docs/builder-engine.md, "Surfaces"). An area (or a walkable body) with a `surface` makes the
    ground inside it a named surface: a dual NavmeshCut splits the ground graphs along its outline and the triangles
    inside get one of the 31 A* node tags (one per surface name per session). Who may walk it:

    - `surface.allow` (a filter, default everything): units that don't match never path over it and are kept off it
      (their Seeker's traversable tags and their movement's snap constraint leave the tag out);
    - `surface.cost` (default 1): a path cost multiplier for those that may (roads < 1, mud > 1);
    - a unit's own `movement.surfaces` ({ "climb": true | false | <cost> }) overrides both for that unit.

    Units get their mask when they spawn (any unit, vanilla included, while a surface exists) and again whenever a
    surface appears, disappears or moves. Tags are re-applied after every graph rescan.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class Surfaces
    {
        internal sealed class Surface
        {
            public string Name;
            public uint Tag;
            public string Allow;
            public float Cost = 1f;
            public BuilderArea Area;      // the outline (an area component on the same entity)
            public NavmeshCut Cut;
            public bool Applied;
        }

        private static readonly Dictionary<string, uint> tags = [];
        private static readonly List<Surface> surfaces = [];
        private static bool dirty;

        public static bool Any => surfaces.Count > 0;

        public static void Clear() { tags.Clear(); surfaces.Clear(); dirty = false; }

        /// <summary>The tag of a surface name (allocated on first use; 1..31).</summary>
        public static uint TagOf(string name)
        {
            if (tags.TryGetValue(name, out var t)) return t;
            if (tags.Count >= 31) throw new LogicError($"surface \"{name}\": at most 31 surface names per level");
            t = (uint)tags.Count + 1;
            tags[name] = t;
            return t;
        }

        /// <summary>Make the area's ground a surface (from `area.surface`).</summary>
        public static void Add(BuilderArea area, JToken def)
        {
            var s = new Surface { Area = area };
            if (def.Type == JTokenType.String) s.Name = (string)def;
            else if (def is JObject o)
            {
                s.Name = (string)o["name"] ?? (string)o["id"];
                s.Allow = (string)o["allow"];
                s.Cost = o.Value<float?>("cost") ?? 1f;
            }
            if (string.IsNullOrEmpty(s.Name)) throw new LogicError("surface: needs a name");
            s.Tag = TagOf(s.Name);
            s.Cut = MakeCut(area);
            surfaces.Add(s);
            dirty = true;
        }

        public static void Remove(BuilderArea area)
        {
            foreach (var s in surfaces.Where(s => s.Area == area).ToList())
            {
                if (s.Cut != null) Object.Destroy(s.Cut);
                surfaces.Remove(s);
                Untag(s);
            }
            dirty = true;
        }

        private static NavmeshCut MakeCut(BuilderArea area)
        {
            var cut = area.gameObject.AddComponent<NavmeshCut>();
            cut.isDual = true;
            cut.graphMask = BodyNav.GroundMask();
            cut.useRotationAndScale = true;
            cut.height = 20f;
            cut.radiusExpansionMode = NavmeshCut.RadiusExpansionMode.DontExpand;
            var shape = area.Shape;
            if (shape.Radius > 0) { cut.type = NavmeshCut.MeshType.Circle; cut.circleRadius = shape.Radius; cut.circleResolution = 16; }
            else if (shape.Points == null) { cut.type = NavmeshCut.MeshType.Rectangle; cut.rectangleSize = shape.Size; }
            else
            {
                cut.type = NavmeshCut.MeshType.CustomMesh;
                var pts = shape.Points;
                var mesh = new Mesh { name = "PalaceBuilder surface outline" };
                mesh.SetVertices(pts.Select(p => new Vector3(p.x, 0f, p.y)).ToList());
                var tris = new List<int>();
                for (int i = 1; i + 1 < pts.Length; i++) { tris.Add(0); tris.Add(i); tris.Add(i + 1); }
                mesh.SetTriangles(tris, 0);
                cut.mesh = mesh;
            }
            return cut;
        }

        /// <summary>Re-tag after cuts have been applied (call once a frame; cheap when nothing changed).</summary>
        public static void Tick()
        {
            if (!dirty || AstarPath.active == null) return;
            dirty = false;
            var astar = AstarPath.active;
            foreach (var s in surfaces) if (s.Cut != null) s.Cut.ForceUpdate();
            astar.navmeshUpdates.ForceUpdate();
            astar.FlushWorkItems();
            foreach (var s in surfaces) Tag(s);
            astar.FlushGraphUpdates();
            ApplyToAll();
        }

        /// <summary>After a rescan the graphs are new: tag again.</summary>
        public static void Rescanned() { if (Any) dirty = true; }

        private static void Tag(Surface s) => Update(s, s.Tag);
        private static void Untag(Surface s) => Update(s, 0);

        private static void Update(Surface s, uint tag)
        {
            if (s.Area == null || AstarPath.active == null) return;   // the level is unloading: no graphs left to untag
            var t = s.Area.transform;
            var pts = s.Area.Shape.Outline().Select(p => t.TransformPoint(new Vector3(p.x, 0f, p.y))).ToArray();
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            b.size = new Vector3(b.size.x, 400f, b.size.z);
            var guo = new GraphUpdateObject(b)
            {
                updatePhysics = false,
                modifyTag = true,
                setTag = tag,
                graphMask = BodyNav.GroundMask(),
                shape = new GraphUpdateShape(pts, convex: s.Area.Shape.Points == null, Matrix4x4.identity, 0f),
            };
            AstarPath.active.UpdateGraphs(guo);
        }

        // ---- units -------------------------------------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<PathfindMovementEnemy, NNConstraint> enemyNN = AccessTools.FieldRefAccess<PathfindMovementEnemy, NNConstraint>("nearestConstraint");
        private static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, NNConstraint> playerNN = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, NNConstraint>("nearestConstraint");

        /// <summary>A unit's traversable mask and per-tag cost multipliers.</summary>
        public static void Apply(GameObject unit)
        {
            if (unit == null) return;
            var seeker = unit.GetComponent<Seeker>();
            if (seeker == null) return;
            var e = LogicGame.EntityOf(unit);
            int mask = -1;
            var costs = new uint[32];
            for (int i = 0; i < 32; i++) costs[i] = 1000;   // A* cost multipliers are x1000
            var own = e?.Def?["movement"]?["surfaces"] as JObject;
            foreach (var s in surfaces)
            {
                bool allowed = s.Allow == null || (e != null && BodyFilter.Matches(s.Allow, e, e));
                float cost = s.Cost;
                if (own?[s.Name] is JToken o)
                {
                    if (o.Type == JTokenType.Boolean) allowed = (bool)o;
                    else if (o.Type is JTokenType.Integer or JTokenType.Float) { allowed = true; cost = (float)o; }
                }
                if (!allowed) mask &= ~(1 << (int)s.Tag);
                else costs[s.Tag] = (uint)Mathf.Clamp(cost * 1000f, 1f, 1000000f);
            }
            seeker.traversableTags = mask;
            SetCosts(seeker, costs);
            var nn = unit.GetComponent<PathfindMovementEnemy>() is { } pe ? enemyNN(pe) : unit.GetComponent<PathfindMovementPlayerunit>() is { } pp ? playerNN(pp) : null;
            if (nn != null) { nn.constrainTags = mask != -1; nn.tags = mask; }
            unit.GetComponent<PathfindMovement>()?.ClearCurrentPath();
        }

        private static void SetCosts(Seeker seeker, uint[] costs)
        {
            var m = new float[32];
            for (int i = 0; i < 32; i++) m[i] = costs[i] / 1000f;
            seeker.tagCostMultipliers = m;
        }

        public static void ApplyToAll()
        {
            foreach (var m in Object.FindObjectsOfType<PathfindMovement>())
                if (m != null && m.GetComponent<Seeker>() != null) Apply(m.gameObject);
        }

        /// <summary>A walkable mover reached a pose (its surface moved with it).</summary>
        public static void PoseReached(BuilderMover m)
        {
            if (surfaces.Any(s => s.Area != null && s.Area.gameObject == m.gameObject)) dirty = true;
            Walkables.PoseReached(m);
        }

        public static string Describe() =>
            $"{surfaces.Count} surfaces ({string.Join(", ", surfaces.Select(s => $"{s.Name}=tag {s.Tag}{(s.Allow != null ? " allow " + s.Allow : "")}{(s.Cost != 1f ? $" cost {s.Cost}" : "")}"))})";
    }
}
