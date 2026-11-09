using System.Collections.Generic;
using System.Linq;
using Pathfinding;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    World queries for expressions (docs/builder-engine.md, "World queries"): groundY, walkable, ground, waterDepth,
    waterLevel, sight and raycast. Answers come from what units and the king really use: the terrain grid (heights,
    paints, water, ice), the Ground-layer colliders (the map's own ground and walkable decks), the pathfinding graphs
    (with surface tags for a given unit) and the colliders that block movement and shots.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class WorldQueryService : IWorldQueries
    {
        public static readonly WorldQueryService Instance = new();

        private const int GroundLayer6 = 6, ObstacleLayer = 7, WaterLayer = 4;
        private const float Eye = 1.5f;
        private readonly RaycastHit[] hits = new RaycastHit[16];

        private static readonly string[] PaintNames = ["grass", "highGrass", "lowGrass", "earth", "sand", "road", "rock"];

        /// <summary>The highest Ground-layer surface under p (decks included), or null with nothing there.</summary>
        private RaycastHit? TopGround(Vector3 p)
        {
            int n = Physics.RaycastNonAlloc(new Vector3(p.x, 300f, p.z), Vector3.down, hits, 700f, 1 << GroundLayer6, QueryTriggerInteraction.Ignore);
            RaycastHit? best = null;
            for (int i = 0; i < n; i++)
                if (best == null || hits[i].point.y > best.Value.point.y) best = hits[i];
            return best;
        }

        public double GroundY(Vec v)
        {
            var p = v.ToVector3();
            if (TopGround(p) is RaycastHit h) return h.point.y;
            if (GroundLayer.TryHeight(p, out float y)) return y;
            return LayoutScene.Ground(p).y;
        }

        private static bool IsDeck(Collider c) => c != null && c.name.Contains("walkable deck");

        public string Ground(Vec v)
        {
            var p = v.ToVector3();
            var top = TopGround(p);
            if (top is RaycastHit h && IsDeck(h.collider)) return "deck";
            var g = GroundLayer.Grid;
            if (GroundLayer.Active && g.CellAt(p.x, p.z, out int cx, out int cz) && (GroundLayer.Flat || g.Sculpted(cx, cz)))
            {
                int i = g.CellIndex(cx, cz);
                if (g.Water[i]) return g.Frozen[i] ? "ice" : "water";
                if (g.Blocked[i]) return "blocked";
                if (g.CellSlope(cx, cz, out _, out _) > GroundLayer.WalkSlopeDegrees) return "rock";
                return PaintNames[(int)g.MaterialAt(p.x, p.z)];
            }
            if (HostWater(p, out _)) return "water";
            if (top == null) return "none";
            return HostPaint(top.Value) ?? "grass";
        }

        /// <summary>A map's own water (rivers, lakes, the sea) over a point, from its water renderers.</summary>
        private bool HostWater(Vector3 p, out float surface)
        {
            surface = 0f;
            foreach (var r in WaterRenderers())
            {
                if (r == null || !r.enabled) continue;
                var b = r.bounds;
                if (p.x < b.min.x || p.x > b.max.x || p.z < b.min.z || p.z > b.max.z) continue;
                if (OnMesh(r, p, out float y)) { surface = y; return true; }
            }
            return false;
        }

        private List<MeshRenderer> waterCache;
        private UnityEngine.SceneManagement.Scene waterScene;
        private float waterAt = -10f;

        private List<MeshRenderer> WaterRenderers()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (waterCache != null && waterScene == scene && Time.time - waterAt < 30f) return waterCache;
            var mats = new HashSet<Material>(new[] { GroundMaterials.Get("waterMaterial"), GroundMaterials.Get("oceanMaterial") }.Where(m => m != null));
            waterCache = Object.FindObjectsOfType<MeshRenderer>()
                .Where(r => r.gameObject.layer == WaterLayer || r.sharedMaterials.Any(m => m != null && mats.Contains(m)))
                .Where(r => !r.name.Contains("ice")).ToList();
            waterScene = scene;
            waterAt = Time.time;
            return waterCache;
        }

        /// <summary>Whether p (XZ) lies on a renderer's mesh, and the mesh's height there (readable meshes only).</summary>
        private static bool OnMesh(MeshRenderer r, Vector3 p, out float y)
        {
            y = 0f;
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) { y = r.bounds.max.y; return true; }
            var t = r.transform;
            var v = mesh.vertices;
            var tris = mesh.triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = t.TransformPoint(v[tris[i]]), b = t.TransformPoint(v[tris[i + 1]]), c = t.TransformPoint(v[tris[i + 2]]);
                if (InTriangle(p, a, b, c, out y)) return true;
            }
            return false;
        }

        private static bool InTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            y = 0f;
            float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(den) < 1e-6f) return false;
            float u = ((b.z - c.z) * (p.x - c.x) + (c.x - b.x) * (p.z - c.z)) / den;
            float w = ((c.z - a.z) * (p.x - c.x) + (a.x - c.x) * (p.z - c.z)) / den;
            float s = 1f - u - w;
            if (u < 0f || w < 0f || s < 0f) return false;
            y = u * a.y + w * b.y + s * c.y;
            return true;
        }

        /// <summary>The paint of a map's own ground under a hit (the hit triangle's material), or null if unknown.</summary>
        private static string HostPaint(RaycastHit h)
        {
            var r = h.collider.GetComponent<MeshRenderer>();
            var mc = h.collider as MeshCollider;
            Material m = null;
            if (r != null && mc != null && mc.sharedMesh != null && mc.sharedMesh.isReadable && h.triangleIndex >= 0)
            {
                var mesh = mc.sharedMesh;
                int tri = h.triangleIndex * 3, start = 0;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    int count = (int)mesh.GetIndexCount(s);
                    if (tri < start + count) { m = s < r.sharedMaterials.Length ? r.sharedMaterials[s] : null; break; }
                    start += count;
                }
            }
            else if (r != null) m = r.sharedMaterial;
            if (m == null) return null;
            var all = GroundMaterials.All();
            string[] fields = ["groundMaterial", "groundHigh", "groundLow", "earthMaterial", "sandMaterial", "roadMaterial", "rockMaterial"];
            for (int i = 0; i < fields.Length; i++)
                if (all.TryGetValue(fields[i], out var f) && f == m) return PaintNames[i];
            if (all.TryGetValue("groundPatches", out var patches) && patches == m) return "grass";
            return null;
        }

        public double WaterDepth(Vec v)
        {
            var p = v.ToVector3();
            var g = GroundLayer.Grid;
            if (GroundLayer.Active && g.CellAt(p.x, p.z, out int cx, out int cz) && (GroundLayer.Flat || g.Sculpted(cx, cz) || g.Water[g.CellIndex(cx, cz)]))
            {
                int i = g.CellIndex(cx, cz);
                if (!g.OpenWater(i)) return 0;
                return Mathf.Max(0f, g.Y + g.WaterLevel - g.SurfaceAt(p.x, p.z));
            }
            if (!HostWater(p, out float surface)) return 0;
            float bed = TopGround(p) is RaycastHit h ? h.point.y : surface - 1f;
            return Mathf.Max(0f, surface - bed);
        }

        public double WaterLevel => GroundLayer.Active && GroundLayer.Grid.AnyWater ? GroundLayer.Grid.Y + GroundLayer.Grid.WaterLevel : double.NaN;

        // ---- walkability ----------------------------------------------------------------------------------------

        private static string GraphFor(string who) => (who ?? "small").ToLowerInvariant() switch
        {
            "large" => "Enemy Units L",
            "air" or "flying" or "flyer" => "Enemy Units Air",
            "player" or "king" or "troop" or "troops" => "Player Units S",
            _ => "Enemy Units S",
        };

        public bool Walkable(Vec v, string who, LogicEntity unit)
        {
            var astar = AstarPath.active;
            if (astar == null) return true;
            var p = v.ToVector3();
            // A ground position from vec(x, z) has y 0: ask at the ground there.
            if (Mathf.Abs(p.y) < 0.001f) p.y = (float)GroundY(v);
            var nn = new NNConstraint { constrainWalkability = true, walkable = true };
            var go = unit?.Game as GameObject;
            var seeker = go != null ? go.GetComponent<Seeker>() : null;
            if (seeker != null)
            {
                nn.graphMask = seeker.graphMask;
                if (seeker.traversableTags != -1) { nn.constrainTags = true; nn.tags = seeker.traversableTags; }
            }
            else
            {
                var g = astar.data.graphs.FirstOrDefault(x => x != null && x.name == GraphFor(who));
                if (g == null) return true;
                nn.graphMask = GraphMask.FromGraph(g);
            }
            var hit = astar.GetNearest(p, nn);
            if (hit.node == null) return false;
            var d = new Vector2(hit.position.x - p.x, hit.position.z - p.z);
            bool air = seeker == null && GraphFor(who) == "Enemy Units Air";
            return d.sqrMagnitude <= 0.6f * 0.6f && (air || Mathf.Abs(hit.position.y - p.y) < 2.5f);
        }

        // ---- sight -----------------------------------------------------------------------------------------------

        private static int SightMask
        {
            get
            {
                int m = (1 << GroundLayer6) | (1 << ObstacleLayer);
                foreach (var name in new[] { "Building", "Buildings", "Wall", "Walls" })
                {
                    int l = LayerMask.NameToLayer(name);
                    if (l >= 0) m |= 1 << l;
                }
                return m;
            }
        }

        private Vector3 Eyes(Vec v)
        {
            var p = v.ToVector3();
            float ground = (float)GroundY(v);
            return new Vector3(p.x, Mathf.Max(p.y, ground) + Eye, p.z);
        }

        public Vec? Raycast(Vec a, Vec b)
        {
            Vector3 from = Eyes(a), to = Eyes(b);
            var dir = to - from;
            float len = dir.magnitude;
            if (len < 0.01f) return null;
            int n = Physics.RaycastNonAlloc(from, dir / len, hits, len, SightMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3? at = null;
            for (int i = 0; i < n; i++)
            {
                // Things standing at either end (the looker's own building, the target's) don't hide them.
                if (hits[i].distance < 0.5f || hits[i].distance > len - 0.5f) continue;
                if (hits[i].distance < best) { best = hits[i].distance; at = hits[i].point; }
            }
            return at?.ToVec();
        }
    }
}
