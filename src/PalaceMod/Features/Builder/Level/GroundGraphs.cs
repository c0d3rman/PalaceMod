using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

/*
    Rescanning the host's pathfinding graphs at runtime the way the game baked them.

    The maps ship four pre-baked recast graphs (one tile each, so a local GraphUpdateObject rebuilds the whole graph
    and costs as much as a full scan, ~100 ms on Nordfels). They rasterize renderer meshes, and two things the bake saw
    are gone at runtime:
    - the invisible boundary walls (NavmeshBakeHelper.invisibleWalls) hide most of their renderers on Start
      (PathMesher._hideOnStart), so a plain rescan lets the walkable area spill past the map's edge;
    - the flier surface (NavmeshBakeHelper.flyerSurface, layer "Flyer Surface") is inactive in play, so a plain rescan
      leaves "Enemy Units Air" empty and fliers stuck.
    Scan() turns both back on for the duration of the (synchronous) scan, invisibly, and restores them after.
*/
namespace PalaceMod.Builder.Level
{
    internal static class GroundGraphs
    {
        /// <summary>Extra renderers to rasterize while scanning (the ground layer's own walls and air surface).</summary>
        public static readonly List<MeshRenderer> Extra = [];

        /// <summary>Asks for a rescan in a moment (BuilderLevelManager runs it), e.g. once a copied bridge is
        /// built and its deck is up.</summary>
        public static void ScanSoon(string why)
        {
            ScanAt = Time.realtimeSinceStartup + 1.5f; // after the build animation (the deck grows in)
            scanReason = why;
        }

        public static float ScanAt = -1f;
        private static string scanReason;

        /// <summary>Runs the rescan ScanSoon asked for, once its frame has come.</summary>
        public static void RunPendingScan()
        {
            if (ScanAt < 0f || Time.realtimeSinceStartup < ScanAt) return;
            ScanAt = -1f;
            if (AstarPath.active == null) return;
            long ms = Scan();
            Log.For<BuilderManager>().Info($"rescan after {scanReason} was built: {ms} ms");
        }

        /// <summary>
        /// Runs inside every Scan once the map's walls (and <see cref="Extra"/>) are shown for it, before anything is
        /// rasterized: a handler may change what the scan sees and must add the undo of each change to the list (run
        /// in reverse order after the scan). Walkable decks use it to open the walls they cross.
        /// </summary>
        public static event System.Action<List<System.Action>> Rasterizing;

        /// <summary>The map's invisible boundary walls (what Scan shows for the bake's outline), unless the level's ground
        /// replaces them.</summary>
        public static IEnumerable<MeshRenderer> HostWalls()
        {
            if (GroundLayer.ReplacesWalls) yield break;
            foreach (var helper in Object.FindObjectsOfType<NavmeshBakeHelper>())
            {
                var walls = Traverse.Create(helper).Field("invisibleWalls").GetValue<GameObject>();
                if (walls == null) continue;
                foreach (var r in walls.GetComponentsInChildren<MeshRenderer>(true)) yield return r;
            }
        }

        /// <summary>
        /// Whether a point is on (or within <paramref name="tolerance"/> m of) the walkable ground the map was baked with,
        /// on any ground graph but the large enemies' one, and that ground's height there. False before the first Scan
        /// in this scene (the bake is taken then) and on a field that replaces the host's ground.
        /// </summary>
        public static bool OnBake(Vector3 p, float tolerance, out float y)
        {
            y = 0f;
            if (GroundLayer.Flat || bakedTris.Count == 0) return false;
            float best = float.MaxValue;
            foreach (var t in bakedTris)
            {
                if (p.x < t.min.x - tolerance || p.x > t.max.x + tolerance || p.z < t.min.z - tolerance || p.z > t.max.z + tolerance) continue;
                float d = FlatDistance(p, t.a, t.b, t.c, out float ty);
                if (d < best && d <= tolerance) { best = d; y = ty; }
            }
            return best <= tolerance;
        }

        /// <summary>XZ distance from p to triangle abc (0 inside), and the triangle's height at the nearest point.</summary>
        private static float FlatDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            var q = new Vector2(p.x, p.z);
            Vector2 A = new(a.x, a.z), B = new(b.x, b.z), C = new(c.x, c.z);
            float den = (B.y - C.y) * (A.x - C.x) + (C.x - B.x) * (A.y - C.y);
            if (Mathf.Abs(den) > 1e-6f)
            {
                float u = ((B.y - C.y) * (q.x - C.x) + (C.x - B.x) * (q.y - C.y)) / den;
                float v = ((C.y - A.y) * (q.x - C.x) + (A.x - C.x) * (q.y - C.y)) / den;
                float w = 1f - u - v;
                if (u >= 0f && v >= 0f && w >= 0f) { y = u * a.y + v * b.y + w * c.y; return 0f; }
            }
            float best = float.MaxValue; y = a.y;
            foreach (var (s0, s1, y0, y1) in new[] { (A, B, a.y, b.y), (B, C, b.y, c.y), (C, A, c.y, a.y) })
            {
                var e = s1 - s0;
                float t = e.sqrMagnitude > 1e-9f ? Mathf.Clamp01(Vector2.Dot(q - s0, e) / e.sqrMagnitude) : 0f;
                float d = (s0 + e * t - q).magnitude;
                if (d < best) { best = d; y = Mathf.Lerp(y0, y1, t); }
            }
            return best;
        }

        /// <summary>minRegionSize the ground graphs are rescanned with (see Scan).</summary>
        private const float SmallRegion = 50f;

        private const string AirGraph = "Enemy Units Air";

        /// <summary>Rescans every graph as baked. Returns the time it took in ms, or -1 without pathfinding.</summary>
        public static long Scan()
        {
            var astar = AstarPath.active;
            if (astar == null) return -1;
            RememberBake();
            var sw = Stopwatch.StartNew();
            long prepared = 0, ground = 0, airMs = 0, pruned = 0;
            var restore = new List<System.Action>();
            try
            {
                Prepare(restore, flyer: true);
                prepared = sw.ElapsedMilliseconds;
                // Blocked cells, solid props and cliff walls are walls for ground units only: fliers cross them. They sit
                // on the Obstacle layer, which the fliers' graph rasterizes too, so it is scanned without them (otherwise
                // a forest of solid trees or a blocked area cuts the fliers' surface into scraps and recast drops them).
                var air = astar.data.graphs.Where(g => g != null && g.name == AirGraph).ToArray();
                if (air.Length == 0) astar.Scan();
                else
                {
                    astar.Scan(astar.data.graphs.Where(g => g != null && g.name != AirGraph).ToArray());
                    ground = sw.ElapsedMilliseconds;
                    var hidden = new List<MeshRenderer>();
                    foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
                        if (r.enabled && LayoutScene.GroundOnlyWall(r.gameObject)) { r.enabled = false; hidden.Add(r); }
                    try { astar.Scan(air); }
                    finally { foreach (var r in hidden) if (r != null) r.enabled = true; }
                    airMs = sw.ElapsedMilliseconds;
                }
            }
            finally
            {
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
            }
            PruneIslands();
            pruned = sw.ElapsedMilliseconds;
            MinimapRefresh.Rebuild();
            long ms = sw.ElapsedMilliseconds;
            ReportProblems();
            LastTimings = $"scan {ms} ms: prepare {prepared}, ground graphs {ground - prepared}, fliers {airMs - ground}, prune {pruned - airMs}, minimap {ms - pruned}; checks {sw.ElapsedMilliseconds - ms}";
            return ms;
        }

        /// <summary>How many UpdateArea calls ran, and the last one's ms (dev benchmarks).</summary>
        public static int AreaUpdates;
        public static long LastAreaMs;

        /// <summary>Where the last Scan or UpdateArea spent its time (dev).</summary>
        public static string LastTimings = "";

        /// <summary>
        /// Rebuilds the ground graphs (not the fliers') only where they touch the areas, with the scene set
        /// up as for Scan (walls shown, hooks run), then prunes islands and redraws the minimap. On the maps' one-tile
        /// graphs that is the whole graph; on tiled ones (<see cref="UseTiles"/>) only the tiles the area touches.
        /// Returns ms, or -1 without pathfinding.
        /// </summary>
        public static long UpdateArea(params Bounds[] areas)
        {
            var astar = AstarPath.active;
            if (astar == null) return -1;
            RememberBake();
            var sw = Stopwatch.StartNew();
            long prepared = 0, updated = 0, pruned = 0;
            var restore = new List<System.Action>();
            try
            {
                Prepare(restore, flyer: false);
                prepared = sw.ElapsedMilliseconds;
                GraphMask mask = default;
                bool any = false;
                foreach (var g in astar.data.graphs)
                    if (g is RecastGraph && g.name != AirGraph) { mask = any ? mask | GraphMask.FromGraph(g) : GraphMask.FromGraph(g); any = true; }
                if (any)
                {
                    foreach (var area in areas) astar.UpdateGraphs(new GraphUpdateObject(area) { updatePhysics = true, graphMask = mask });
                    astar.FlushGraphUpdates();
                }
                updated = sw.ElapsedMilliseconds;
            }
            finally
            {
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
            }
            PruneIslands();
            pruned = sw.ElapsedMilliseconds;
            MinimapRefresh.Rebuild();
            long ms = sw.ElapsedMilliseconds;
            LastAreaMs = ms;
            AreaUpdates++;
            LastTimings = $"area update ({areas.Length}) {ms} ms: prepare {prepared}, ground graphs {updated - prepared}, prune {pruned - updated}, minimap {ms - pruned}";
            return ms;
        }

        /// <summary>
        /// Splits the ground graphs (not the fliers') into tiles of <paramref name="voxels"/> cells, or back to one tile
        /// (0), for the next Scan. A tiled graph lets <see cref="UpdateArea"/> rebuild only the tiles it touches.
        /// </summary>
        public static void UseTiles(int voxels)
        {
            var astar = AstarPath.active;
            if (astar == null) return;
            foreach (var g in astar.data.graphs.OfType<RecastGraph>())
            {
                if (g.name == AirGraph) continue;
                g.useTiles = voxels > 0;
                if (voxels > 0) g.editorTileSize = voxels;
            }
        }

        /// <summary>Whether the ground graphs are tiled (more than one tile).</summary>
        public static bool Tiled => AstarPath.active != null && AstarPath.active.data.graphs.OfType<RecastGraph>().Any(g => g.name != AirGraph && g.useTiles && g.tileXCount * g.tileZCount > 1);

        /// <summary>Sets the scene up as the bake saw it (and runs <see cref="Rasterizing"/>); undo with restore.</summary>
        private static void Prepare(List<System.Action> restore, bool flyer)
        {
            var astar = AstarPath.active;
            foreach (var helper in Object.FindObjectsOfType<NavmeshBakeHelper>())
            {
                var t = Traverse.Create(helper);
                var walls = t.Field("invisibleWalls").GetValue<GameObject>();
                if (walls != null && !GroundLayer.ReplacesWalls)
                    foreach (var r in walls.GetComponentsInChildren<MeshRenderer>(true))
                        ShowForScan(r, restore);
                var surface = flyer ? t.Field("flyerSurface").GetValue<GameObject>() : null;
                if (surface != null && !surface.activeSelf && !GroundLayer.ReplacesAir)
                {
                    // Its colliders are on a layer the king collides with: only active during the scan.
                    foreach (var r in surface.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        bool force = r.forceRenderingOff;
                        r.forceRenderingOff = true;
                        restore.Add(() => { if (r != null) r.forceRenderingOff = force; });
                    }
                    surface.SetActive(true);
                    restore.Add(() => { if (surface != null) surface.SetActive(false); });
                }
            }
            foreach (var r in Extra) if (r != null) ShowForScan(r, restore);
            // Others' temporary changes for this scan (walkable decks open the walls they cross); undone with ours.
            try { Rasterizing?.Invoke(restore); }
            catch (System.Exception e) { Log.For<BuilderManager>().Error("A rescan hook failed", e); }
            // The maps' graphs drop every region under minRegionSize (300000 on Uferwind: ~12000 m² at 0.2 m cells).
            // The bake kept the islands anyway, but a one-tile rescan doesn't: Uferwind's bridge islands vanished, and
            // so did both banks of a river dug through a map's own ground. Keep every region here; PruneIslands
            // then drops what the bake didn't have (rock and wall tops).
            foreach (var g in astar.data.graphs.OfType<RecastGraph>())
            {
                if (g.name == AirGraph || g.minRegionSize <= SmallRegion) continue;
                var graph = g;
                float before = graph.minRegionSize;
                graph.minRegionSize = SmallRegion;
                restore.Add(() => graph.minRegionSize = before);
            }
        }

        /// <summary>What was wrong with the graphs after the last rescan ("" when nothing): an empty ground graph, a
        /// ground spawn line with no route to the castle, a flier line off the fliers' graph.</summary>
        public static string LastProblem { get; private set; } = "";

        /// <summary>A rescan can leave the ground graphs empty or cut a line off without any error from the pathfinding
        /// (a layout that walls everything in, or a recast limit): say so in the log, once per distinct problem.</summary>
        private static void ReportProblems()
        {
            try
            {
                var scene = Builder.Studio.StudioShell.Editing ? Builder.Studio.StudioScene.Scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var problems = PathCheck.Problems(scene);
                string text = string.Join("; ", problems);
                if (text != LastProblem && problems.Count > 0) Log.For<BuilderManager>().Warn($"after the rescan: {text}");
                LastProblem = text;
            }
            catch (System.Exception e)
            {
                Log.For<BuilderManager>().Warn($"checking the graphs after the rescan failed: {e.Message}");
            }
        }

        // The baked graphs' walkable node centres per graph, taken before the first rescan in a scene.
        private static int bakedScene = -1;
        private static readonly Dictionary<string, List<Vector3>> baked = [];
        private static readonly List<(Vector3 a, Vector3 b, Vector3 c, Vector3 min, Vector3 max)> bakedTris = [];

        private static void RememberBake()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (bakedScene == scene.handle) return;
            bakedScene = scene.handle;
            baked.Clear();
            bakedTris.Clear();
            foreach (var g in AstarPath.active.data.graphs)
            {
                if (g == null) continue;
                var points = new List<Vector3>();
                bool tris = g.name != AirGraph && g.name != "Enemy Units L" && !g.name.StartsWith(Logic.Bodies.Spaces.Prefix);
                g.GetNodes(n =>
                {
                    if (!n.Walkable) return;
                    points.Add((Vector3)n.position);
                    if (tris && n is TriangleMeshNode t)
                    {
                        Vector3 a = (Vector3)t.GetVertex(0), b = (Vector3)t.GetVertex(1), c = (Vector3)t.GetVertex(2);
                        bakedTris.Add((a, b, c, Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c))));
                    }
                });
                baked[g.name] = points;
            }
        }

        /// <summary>The bake kept only the areas the map uses; a rescan also finds cliff, rock and wall tops. Those
        /// islands are made unwalkable on the ground graphs (enemies would snap onto them, and the minimap would draw
        /// them). Kept: every area the bake had (on the host's ground), and the areas of the castle and of every spawn
        /// line, so a walled-in spawn still shows as cut off.</summary>
        /// <summary>What the last prune kept (dev).</summary>
        public static string LastPrune = "";

        private static void PruneIslands()
        {
            var astar = AstarPath.active;
            var anchors = Anchors();
            if (astar == null || anchors.Count == 0) return;
            var drop = new List<GraphNode>();
            LastPrune = "";
            foreach (var g in astar.data.graphs)
            {
                // Off-map spaces (Logic/Bodies/Spaces.cs) have no castle or spawn of their own: keep them whole.
                if (g == null || g.name == "Enemy Units Air" || g.name.StartsWith(Logic.Bodies.Spaces.Prefix)) continue;
                var keep = KeptAreas(g, anchors, out int matched, out int total, out float far);
                LastPrune += $"{g.name}: anchors {anchors.Count}, baked {matched}/{total} (worst miss {far:0.0} m), areas kept {keep.Count}; ";
                g.GetNodes(n => { if (n.Walkable && !keep.Contains(n.Area)) drop.Add(n); });
            }
            if (drop.Count == 0) return;
            astar.AddWorkItem(new AstarWorkItem(ctx =>
            {
                foreach (var n in drop) n.Walkable = false;
                ctx.QueueFloodFill();
            }));
            astar.FlushWorkItems();
            Log.For<BuilderManager>().Debug($"pruned {drop.Count} island nodes after the rescan");
        }

        /// <summary>The areas of a ground graph that are the map's real ground (what <see cref="PruneIslands"/> keeps),
        /// or null when every area counts: no rescan in this scene (the bake has no islands), the fliers' graph,
        /// off-map spaces, nothing to anchor on. A navmesh cut update (a building going up) rebuilds its tiles from
        /// the last scan's mesh, islands included, so pruned rock and wall tops are walkable again until the next
        /// scan; whatever places things on the graph checks this instead of trusting the nearest node.</summary>
        public static HashSet<uint> RealAreas(NavGraph g)
        {
            if (g == null || AstarPath.active == null || g.name == AirGraph || g.name.StartsWith(Logic.Bodies.Spaces.Prefix)) return null;
            if (bakedScene != UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle) return null;
            var anchors = Anchors();
            return anchors.Count == 0 ? null : KeptAreas(g, anchors, out _, out _, out _);
        }

        private static List<Vector3> Anchors()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var anchors = new List<Vector3>();
            var castle = CastleCenter.instance != null ? CastleCenter.instance.transform.position : PathCheck.Castle(scene);
            if (castle != null) anchors.Add(castle.Value);
            // On the host's ground a spawn point lies just off the walkable area, and its nearest node can be a rock top:
            // there the bake's areas say what's real. On a flat field the lines' areas count.
            if (GroundLayer.Flat)
                foreach (var line in LayoutScene.Lines(scene).Where(LayoutScene.IsGroundLine))
                    anchors.Add(LayoutScene.LineCenter(line.transform));
            return anchors;
        }

        private static HashSet<uint> KeptAreas(NavGraph g, List<Vector3> anchors, out int matched, out int total, out float far)
        {
            var astar = AstarPath.active;
            var nn = NearestNodeConstraint.Walkable;
            nn.graphMask = GraphMask.FromGraph(g);
            var keep = new HashSet<uint>();
            foreach (var p in anchors)
            {
                var hit = astar.GetNearest(p, nn);
                if (hit.node != null) keep.Add(hit.node.Area);
            }
            // The bake's own areas, unless the host's ground is replaced.
            matched = 0; total = 0; far = 0f;
            if (!GroundLayer.Flat && baked.TryGetValue(g.name, out var points))
                foreach (var p in points)
                {
                    total++;
                    var hit = astar.GetNearest(p, nn);
                    if (hit.node == null) continue;
                    float d = (hit.position - p).sqrMagnitude;
                    if (d < 4f) { keep.Add(hit.node.Area); matched++; }
                    else far = Mathf.Max(far, d);
                }
            far = Mathf.Sqrt(far);
            return keep;
        }

        private static void ShowForScan(MeshRenderer r, List<System.Action> restore)
        {
            if (r.enabled && r.gameObject.activeInHierarchy) return;
            bool enabled = r.enabled, force = r.forceRenderingOff, active = r.gameObject.activeSelf;
            r.forceRenderingOff = true;
            r.enabled = true;
            if (!active) r.gameObject.SetActive(true);
            restore.Add(() =>
            {
                if (r == null) return;
                r.enabled = enabled;
                r.forceRenderingOff = force;
                if (!active) r.gameObject.SetActive(false);
            });
        }
    }
}
