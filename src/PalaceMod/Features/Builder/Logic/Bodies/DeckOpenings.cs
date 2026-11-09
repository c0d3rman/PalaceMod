using System.Collections.Generic;
using System.Linq;
using Pathfinding;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.Rendering;

/*
    Where a walkable deck meets the map (docs/builder-engine.md, "Walkable surfaces").

    The maps fence their playable ground with invisible walls (a curtain mesh along every edge, rivers included, that
    GroundGraphs.Scan rasterizes as the bake did), and a Builder terrain blocks water and blocked cells with columns
    that reach above the graphs. Both stand where a bridge's deck lies, so a deck over a river was walled off from the
    bank it touches, and recast drops what's left of it as an island. So, in every GroundGraphs.Scan:
    - walls give way under a deck: the obstacle meshes (the host's invisible walls, the terrain's columns, cliff and
      field walls) are rasterized from a copy with the pieces inside the deck's footprint, from just below its top up,
      clipped away (each triangle that reaches the deck is cut along the deck's sides and floor);
    - a deck's edges are classified once, by probing 0.75 m outside each 1 m of edge: on the ground the map was baked
      with (or, on a Builder field, on ground with no obstacle) within 2.5 m of the deck's height, the edge is open
      (with a ramp of at most 25 degrees when the step is over 0.45 m, which the graphs can't climb); anywhere else
      it gets a rail, an invisible 1.5 m wall, so units can't step off the deck into the river or past the map's edge
      through the opening;
    - the king (a CharacterController stopped by the walls' colliders) ignores the opened walls while he stands on
      the deck, and the rails and ramps of a deck that doesn't move have colliders for him.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class DeckOpenings
    {
        private const float Band = 0.6f;       // walls from this far below a deck's top upward give way
        private const float Climb = 0.45f;     // the graphs climb 0.5 m; a bigger step at an open edge gets a ramp
        private const float MaxStep = 2.5f;    // an edge more than this above or below the ground there is closed
        private const float RampSlope = 25f;   // degrees (the graphs' max slope is 30)
        private const float Probe = 0.75f;     // m outside an edge where the ground is tested
        private const float BakeTolerance = 0.4f;
        private const float RailHigh = 1.5f, RailThick = 0.2f, RailOverlap = 0.5f;
        private const int ObstacleLayer = 7, GroundLayerIndex = 6;

        /// <summary>A deck mesh the graphs rasterize (a still deck's proxy or a mover's pose ghost).</summary>
        internal sealed class Top
        {
            public GameObject Mesh;            // the deck box (top face at local y 0.5)
            public bool Live;                  // the body's own deck (colliders for the king), not a pose ghost
            public GameObject Edges;           // rails and ramps, made on the first scan that sees the deck
            public string EdgeSummary = "";
            public Vector3[] Corners = new Vector3[4];
            public float Y;
            public Vector2 Min, Max;

            public bool Update()
            {
                if (Mesh == null) return false;
                var t = Mesh.transform;
                Corners[0] = t.TransformPoint(new Vector3(-0.5f, 0.5f, -0.5f));
                Corners[1] = t.TransformPoint(new Vector3(-0.5f, 0.5f, 0.5f));
                Corners[2] = t.TransformPoint(new Vector3(0.5f, 0.5f, 0.5f));
                Corners[3] = t.TransformPoint(new Vector3(0.5f, 0.5f, -0.5f));
                Y = t.TransformPoint(new Vector3(0f, 0.5f, 0f)).y;
                Min = new Vector2(Corners.Min(c => c.x), Corners.Min(c => c.z));
                Max = new Vector2(Corners.Max(c => c.x), Corners.Max(c => c.z));
                return true;
            }

            public bool Inside(float x, float z)
            {
                // Convex quad: inside if on the same side of every edge.
                bool? sign = null;
                for (int i = 0; i < 4; i++)
                {
                    var a = Corners[i]; var b = Corners[(i + 1) % 4];
                    float cross = (b.x - a.x) * (z - a.z) - (b.z - a.z) * (x - a.x);
                    if (Mathf.Abs(cross) < 1e-6f) continue;
                    bool s = cross > 0f;
                    if (sign == null) sign = s;
                    else if (sign != s) return false;
                }
                return true;
            }
        }

        /// <summary>Colliders of walls a deck opened in the last scan, by the deck tops they cross (for the king).</summary>
        private static readonly List<(Collider wall, List<Top> tops)> opened = [];
        private static readonly HashSet<Collider> ignoring = [];
        public static string LastSummary = "";

        /// <summary>Opened copies by wall, kept while the decks over it don't change (a scan or area update reuses them).</summary>
        private static readonly Dictionary<int, (string key, Mesh mesh, int cut)> carvedCache = [];

        public static void Clear()
        {
            foreach (var c in carvedCache.Values) if (c.mesh != null) Object.Destroy(c.mesh);
            carvedCache.Clear();
            opened.Clear();
            ignoring.Clear();
            LastSummary = "";
        }

        /// <summary>GroundGraphs.Rasterizing: classify new decks' edges, then open the walls under every deck.</summary>
        public static void Apply(List<System.Action> restore)
        {
            var tops = Walkables.NavTops().Where(t => t.Update()).ToList();
            if (tops.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var t in tops) if (t.Edges == null) BuildEdges(t);
            long edgesMs = sw.ElapsedMilliseconds;

            var groundRoot = GameObject.Find(LayoutScene.AddedPrefix + "ground");
            var candidates = new HashSet<MeshRenderer>(GroundGraphs.HostWalls());
            if (groundRoot != null)
                foreach (var r in groundRoot.GetComponentsInChildren<MeshRenderer>())
                    if (r.gameObject.layer == ObstacleLayer) candidates.Add(r);

            opened.Clear();
            int walls = 0, removed = 0;
            foreach (var r in candidates)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var b = r.bounds;
                var hit = tops.Where(t => b.max.x >= t.Min.x && b.min.x <= t.Max.x && b.max.z >= t.Min.y && b.min.z <= t.Max.y && b.max.y >= t.Y - Band).ToList();
                if (hit.Count == 0) continue;
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                string key = string.Join("|", hit.Select(t => $"{t.Y:0.00}:{string.Join(",", t.Corners.Select(c => $"{c.x:0.00},{c.z:0.00}"))}")) + "@" + r.transform.localToWorldMatrix.GetHashCode();
                int id = r.GetInstanceID();
                if (!carvedCache.TryGetValue(id, out var cached) || cached.key != key || cached.mesh == null)
                {
                    if (cached.mesh != null) Object.Destroy(cached.mesh);
                    var fresh = Carve(mesh, r.transform.localToWorldMatrix, hit, out int n);
                    cached = (key, fresh, n);
                    carvedCache[id] = cached;
                }
                var carved = cached.mesh;
                int cut = cached.cut;
                if (cut == 0) continue;
                walls++;
                removed += cut;
                var copy = new GameObject(LayoutScene.AddedPrefix + "opened " + r.name) { layer = r.gameObject.layer };
                copy.AddComponent<MeshFilter>().sharedMesh = carved;
                copy.AddComponent<MeshRenderer>().forceRenderingOff = true;
                bool was = r.enabled;
                r.enabled = false;
                restore.Add(() =>
                {
                    if (r != null) r.enabled = was;
                    Object.DestroyImmediate(copy);
                });
                foreach (var c in r.GetComponents<Collider>()) opened.Add((c, hit));
            }
            LastSummary = $"{tops.Count} deck tops; opened {walls} walls ({removed} pieces) in {sw.ElapsedMilliseconds} ms (new edges {edgesMs} ms); edges: {string.Join("; ", tops.Select(t => t.EdgeSummary))}";
        }

        /// <summary>
        /// The mesh in world space with what lies inside a deck's prism (its footprint, from <see cref="Band"/> below its
        /// top upward) clipped away: each triangle that reaches a prism is cut by the prism's five planes, the parts
        /// outside are kept and the part inside dropped. <paramref name="cut"/> counts the dropped parts.
        /// </summary>
        private static Mesh Carve(Mesh mesh, Matrix4x4 m, List<Top> tops, out int cut)
        {
            var src = mesh.vertices;
            var tris = mesh.triangles;
            var world = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) world[i] = m.MultiplyPoint3x4(src[i]);
            var verts = new List<Vector3>(world.Length);
            var idx = new List<int>(tris.Length);
            var planes = tops.Select(Planes).ToList();
            cut = 0;
            var polys = new List<List<Vector3>>();
            var next = new List<List<Vector3>>();
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                polys.Clear();
                polys.Add([world[tris[i]], world[tris[i + 1]], world[tris[i + 2]]]);
                for (int k = 0; k < tops.Count; k++)
                {
                    next.Clear();
                    foreach (var poly in polys)
                    {
                        if (!Overlaps(poly, tops[k])) { next.Add(poly); continue; }
                        var inside = poly;
                        foreach (var f in planes[k])
                        {
                            Clip(inside, f, out var inPart, out var outPart);
                            if (outPart.Count >= 3) next.Add(outPart);
                            inside = inPart;
                            if (inside.Count < 3) break;
                        }
                        if (inside.Count >= 3) cut++;
                    }
                    (polys, next) = (next, polys);
                }
                foreach (var poly in polys)
                    for (int j = 1; j + 1 < poly.Count; j++)
                    {
                        int n = verts.Count;
                        verts.Add(poly[0]); verts.Add(poly[j]); verts.Add(poly[j + 1]);
                        idx.Add(n); idx.Add(n + 1); idx.Add(n + 2);
                    }
            }
            var result = new Mesh { name = "PalaceBuilder opened wall", indexFormat = IndexFormat.UInt32 };
            result.SetVertices(verts);
            result.SetTriangles(idx, 0);
            result.RecalculateBounds();
            return result;
        }

        private static bool Overlaps(List<Vector3> poly, Top t)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, maxY = float.MinValue;
            foreach (var p in poly)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                maxY = Mathf.Max(maxY, p.y);
            }
            return maxX >= t.Min.x && minX <= t.Max.x && maxZ >= t.Min.y && minZ <= t.Max.y && maxY >= t.Y - Band;
        }

        /// <summary>The deck prism's planes as signed distances (negative inside): its four sides and its floor.</summary>
        private static List<System.Func<Vector3, float>> Planes(Top t)
        {
            var list = new List<System.Func<Vector3, float>>();
            var c = (t.Corners[0] + t.Corners[2]) / 2f;
            for (int i = 0; i < 4; i++)
            {
                var a = t.Corners[i]; var b = t.Corners[(i + 1) % 4];
                var n = new Vector2(b.z - a.z, -(b.x - a.x));
                if (n.sqrMagnitude < 1e-8f) continue;
                n.Normalize();
                if (n.x * (c.x - a.x) + n.y * (c.z - a.z) > 0f) n = -n;   // outward
                float ax = a.x, az = a.z;
                list.Add(p => n.x * (p.x - ax) + n.y * (p.z - az));
            }
            float floor = t.Y - Band;
            list.Add(p => floor - p.y);
            return list;
        }

        /// <summary>Sutherland-Hodgman split of a convex polygon by a plane: the part inside (f &lt;= 0) and outside.</summary>
        private static void Clip(List<Vector3> poly, System.Func<Vector3, float> f, out List<Vector3> inside, out List<Vector3> outside)
        {
            inside = new List<Vector3>(poly.Count + 2);
            outside = new List<Vector3>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                float fp = f(p), fq = f(q);
                if (fp <= 0f) inside.Add(p);
                if (fp >= 0f) outside.Add(p);
                if ((fp < 0f && fq > 0f) || (fp > 0f && fq < 0f))
                {
                    var x = Vector3.Lerp(p, q, fp / (fp - fq));
                    inside.Add(x);
                    outside.Add(x);
                }
            }
        }

        // ---- edges ---------------------------------------------------------------------------------------------------

        private enum EdgeKind { Open, Ramp, Rail }

        private static void BuildEdges(Top t)
        {
            var parent = t.Mesh.transform.parent;
            t.Edges = new GameObject(LayoutScene.AddedPrefix + "deck edges");
            t.Edges.transform.SetParent(parent, true);
            var center = (t.Corners[0] + t.Corners[2]) / 2f;
            int open = 0, ramps = 0, rails = 0;
            for (int e = 0; e < 4; e++)
            {
                var a = t.Corners[e]; var b = t.Corners[(e + 1) % 4];
                var along = b - a; along.y = 0f;
                float len = along.magnitude;
                if (len < 0.05f) continue;
                along /= len;
                var outward = new Vector3(along.z, 0f, -along.x);
                if (Vector3.Dot(outward, new Vector3(a.x - center.x, 0f, a.z - center.z)) < 0f) outward = -outward;
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 1f));
                float seg = len / n;
                var kinds = new EdgeKind[n];
                var steps = new float[n];
                for (int i = 0; i < n; i++)
                {
                    var mid = a + along * (seg * (i + 0.5f));
                    var probe = mid + outward * Probe;
                    probe.y = t.Y;
                    kinds[i] = Classify(probe, t.Y, out steps[i]);
                }
                // Runs of one kind become one rail or one ramp.
                int start = 0;
                for (int i = 1; i <= n; i++)
                {
                    if (i < n && kinds[i] == kinds[start]) continue;
                    float from = seg * start, to = seg * i;
                    switch (kinds[start])
                    {
                        case EdgeKind.Rail:
                            Rail(t, a + along * (from - RailOverlap), a + along * (to + RailOverlap), outward);
                            rails++;
                            break;
                        case EdgeKind.Ramp:
                            float dy = 0f;
                            for (int k = start; k < i; k++) dy += steps[k];
                            Ramp(t, a + along * from, a + along * to, outward, dy / (i - start));
                            ramps++;
                            break;
                        default:
                            open++;
                            break;
                    }
                    start = i;
                }
            }
            t.EdgeSummary = $"{t.Mesh.transform.parent?.name}: {open} open, {ramps} ramps, {rails} rails";
        }

        /// <summary>What a deck at <paramref name="top"/> meets at <paramref name="probe"/>; step = ground y - top.</summary>
        private static EdgeKind Classify(Vector3 probe, float top, out float step)
        {
            step = 0f;
            // Anything solid there (a terrain column, a cliff wall, a tree's collider) closes the edge.
            if (Physics.Raycast(new Vector3(probe.x, top + 40f, probe.z), Vector3.down, out _, 40f + Climb, 1 << ObstacleLayer, QueryTriggerInteraction.Ignore))
                return EdgeKind.Rail;
            float ground;
            if (GroundGraphs.OnBake(probe, BakeTolerance, out float baked)) ground = baked;
            else if (GroundLayer.Flat && Physics.Raycast(new Vector3(probe.x, top + MaxStep + 1f, probe.z), Vector3.down, out var hit, 2f * MaxStep + 1f, 1 << GroundLayerIndex, QueryTriggerInteraction.Ignore))
                ground = hit.point.y;
            else return EdgeKind.Rail;   // past the map's edge, or over water
            step = ground - top;
            if (Mathf.Abs(step) > MaxStep) return EdgeKind.Rail;
            return Mathf.Abs(step) > Climb ? EdgeKind.Ramp : EdgeKind.Open;
        }

        private static void Rail(Top t, Vector3 a, Vector3 b, Vector3 outward)
        {
            var go = new GameObject(LayoutScene.AddedPrefix + "deck rail") { layer = ObstacleLayer };
            var mid = (a + b) / 2f;
            float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
            go.transform.SetPositionAndRotation(new Vector3(mid.x, t.Y + (RailHigh - 0.3f) / 2f, mid.z) - outward * (RailThick / 2f), Quaternion.LookRotation(b - a, Vector3.up));
            go.transform.SetParent(t.Edges.transform, true);
            SetWorldScale(go.transform, new Vector3(RailThick, RailHigh + 0.3f, len));
            go.AddComponent<MeshFilter>().sharedMesh = Walkables.BoxMesh();
            go.AddComponent<MeshRenderer>().forceRenderingOff = true;   // enabled for the graphs, never drawn
            if (t.Live) go.AddComponent<BoxCollider>();
        }

        private static void Ramp(Top t, Vector3 a, Vector3 b, Vector3 outward, float step)
        {
            float run = Mathf.Abs(step) / Mathf.Tan(RampSlope * Mathf.Deg2Rad);
            Vector3 p0, p1, p2, p3;
            if (step < 0f)
            {
                // The ground is lower: the ramp leaves the edge outward, down to the ground.
                float low = t.Y + step + 0.03f;
                p0 = new Vector3(a.x, t.Y, a.z); p1 = new Vector3(b.x, t.Y, b.z);
                p2 = new Vector3(b.x, low, b.z) + outward * run; p3 = new Vector3(a.x, low, a.z) + outward * run;
            }
            else
            {
                // The ground is higher: the ramp lies on the deck, from the edge at the ground's height down to the top.
                float high = t.Y + step + 0.03f;
                p0 = new Vector3(a.x, high, a.z); p1 = new Vector3(b.x, high, b.z);
                p2 = new Vector3(b.x, t.Y + 0.03f, b.z) - outward * run; p3 = new Vector3(a.x, t.Y + 0.03f, a.z) - outward * run;
            }
            var mesh = new Mesh { name = "PalaceBuilder deck ramp" };
            mesh.vertices = [p0, p1, p2, p3];
            // Faces up (clockwise seen from above): recast takes a face's slope from its winding.
            bool up = Vector3.Cross(p1 - p0, p2 - p0).y > 0f;
            mesh.triangles = up ? [0, 1, 2, 0, 2, 3] : [0, 2, 1, 0, 3, 2];
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(LayoutScene.AddedPrefix + "deck ramp") { layer = GroundLayerIndex };
            go.transform.SetParent(t.Edges.transform, false);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            SetWorldScale(go.transform, Vector3.one);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().forceRenderingOff = true;
            if (t.Live) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void SetWorldScale(Transform tr, Vector3 world)
        {
            var p = tr.parent != null ? tr.parent.lossyScale : Vector3.one;
            tr.localScale = new Vector3(world.x / Mathf.Max(1e-4f, p.x), world.y / Mathf.Max(1e-4f, p.y), world.z / Mathf.Max(1e-4f, p.z));
        }

        // ---- the king --------------------------------------------------------------------------------------------------

        /// <summary>Once a frame: the king passes the walls a deck opened while he stands on that deck.</summary>
        public static void Tick()
        {
            if (opened.Count == 0 && ignoring.Count == 0) return;
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<CharacterController>() : null;
            if (king == null) return;
            var p = king.transform.position;
            foreach (var (wall, tops) in opened)
            {
                if (wall == null) continue;
                bool on = tops.Any(t => t.Mesh != null && t.Mesh.activeInHierarchy && Mathf.Abs(p.y - t.Y) < 1.5f && t.Inside(p.x, p.z));
                if (on == ignoring.Contains(wall)) continue;
                Physics.IgnoreCollision(king, wall, on);
                if (on) ignoring.Add(wall); else ignoring.Remove(wall);
            }
        }

        /// <summary>After a scan: decks that aren't on the castle's ground on the small units' graph (a warning).</summary>
        public static List<string> Disconnected()
        {
            var list = new List<string>();
            var astar = AstarPath.active;
            if (astar == null || CastleCenter.instance == null) return list;
            var g = astar.data.graphs.FirstOrDefault(x => x != null && x.name == "Enemy Units S");
            if (g == null) return list;
            var nn = NearestNodeConstraint.Walkable;
            nn.graphMask = GraphMask.FromGraph(g);
            var castle = astar.GetNearest(CastleCenter.instance.transform.position, nn).node;
            if (castle == null) return list;
            foreach (var t in Walkables.NavTops())
            {
                if (!t.Update() || !t.Mesh.activeInHierarchy) continue;
                var c = (t.Corners[0] + t.Corners[2]) / 2f;
                var hit = astar.GetNearest(c, nn);
                bool onDeck = hit.node != null && Mathf.Abs(hit.position.y - t.Y) < 0.6f && t.Inside(hit.position.x, hit.position.z);
                if (!onDeck || hit.node.Area != castle.Area)
                    list.Add($"{t.Mesh.transform.parent?.name} (top {t.Y:0.0} m): {(onDeck ? "not connected to the castle's ground" : "no walkable surface on it (too narrow, or under something)")}");
            }
            return list;
        }
    }
}
