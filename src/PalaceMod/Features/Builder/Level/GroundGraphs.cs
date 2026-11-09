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
            var restore = new List<System.Action>();
            try
            {
                foreach (var helper in Object.FindObjectsOfType<NavmeshBakeHelper>())
                {
                    var t = Traverse.Create(helper);
                    var walls = t.Field("invisibleWalls").GetValue<GameObject>();
                    if (walls != null && !GroundLayer.ReplacesWalls)
                        foreach (var r in walls.GetComponentsInChildren<MeshRenderer>(true))
                            ShowForScan(r, restore);
                    var flyer = t.Field("flyerSurface").GetValue<GameObject>();
                    if (flyer != null && !flyer.activeSelf && !GroundLayer.ReplacesAir)
                    {
                        // Its colliders are on a layer the king collides with: only active during the scan.
                        foreach (var r in flyer.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            bool force = r.forceRenderingOff;
                            r.forceRenderingOff = true;
                            restore.Add(() => { if (r != null) r.forceRenderingOff = force; });
                        }
                        flyer.SetActive(true);
                        restore.Add(() => { if (flyer != null) flyer.SetActive(false); });
                    }
                }
                foreach (var r in Extra) if (r != null) ShowForScan(r, restore);
                // The maps' graphs drop every region under minRegionSize (300000 on Uferwind: ~12000 m² at 0.2 m cells).
                // The bake kept the islands anyway, but a one-tile rescan doesn't: Uferwind's bridge islands vanished, and
                // so did both banks of a river dug through a map's own ground. Keep every region here; PruneIslands
                // then drops what the bake didn't have (rock and wall tops).
                foreach (var g in astar.data.graphs.OfType<RecastGraph>())
                {
                    if (g.name == "Enemy Units Air" || g.minRegionSize <= SmallRegion) continue;
                    var graph = g;
                    float before = graph.minRegionSize;
                    graph.minRegionSize = SmallRegion;
                    restore.Add(() => graph.minRegionSize = before);
                }
                // Blocked cells and solid props are walls for ground units only: fliers cross them. Their blockers sit on
                // the Obstacle layer, which the fliers' graph rasterizes too, so it is scanned without them (otherwise
                // a forest of solid trees or a blocked area cuts the fliers' surface into scraps and recast drops them).
                var air = astar.data.graphs.Where(g => g != null && g.name == AirGraph).ToArray();
                if (air.Length == 0) astar.Scan();
                else
                {
                    astar.Scan(astar.data.graphs.Where(g => g != null && g.name != AirGraph).ToArray());
                    var hidden = new List<MeshRenderer>();
                    foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
                        if (r.enabled && r.gameObject.name == LayoutScene.BlockerName) { r.enabled = false; hidden.Add(r); }
                    try { astar.Scan(air); }
                    finally { foreach (var r in hidden) if (r != null) r.enabled = true; }
                }
            }
            finally
            {
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
            }
            PruneIslands();
            MinimapRefresh.Rebuild();
            return sw.ElapsedMilliseconds;
        }

        // The baked graphs' walkable node centres per graph, taken before the first rescan in a scene.
        private static int bakedScene = -1;
        private static readonly Dictionary<string, List<Vector3>> baked = [];

        private static void RememberBake()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (bakedScene == scene.handle) return;
            bakedScene = scene.handle;
            baked.Clear();
            foreach (var g in AstarPath.active.data.graphs)
            {
                if (g == null) continue;
                var points = new List<Vector3>();
                g.GetNodes(n => { if (n.Walkable) points.Add((Vector3)n.position); });
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
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var anchors = new List<Vector3>();
            var castle = CastleCenter.instance != null ? CastleCenter.instance.transform.position : PathCheck.Castle(scene);
            if (castle != null) anchors.Add(castle.Value);
            // On the host's ground a spawn point lies just off the walkable area, and its nearest node can be a rock top:
            // there the bake's areas say what's real. On a flat field the lines' areas count.
            if (GroundLayer.Flat)
                foreach (var line in LayoutScene.Lines(scene).Where(LayoutScene.IsGroundLine))
                    anchors.Add(LayoutScene.LineCenter(line.transform));
            if (astar == null || anchors.Count == 0) return;
            var drop = new List<GraphNode>();
            LastPrune = "";
            foreach (var g in astar.data.graphs)
            {
                if (g == null || g.name == "Enemy Units Air") continue;
                var nn = NearestNodeConstraint.Walkable;
                nn.graphMask = GraphMask.FromGraph(g);
                var keep = new HashSet<uint>();
                foreach (var p in anchors)
                {
                    var hit = astar.GetNearest(p, nn);
                    if (hit.node != null) keep.Add(hit.node.Area);
                }
                // The bake's own areas, unless the host's ground is replaced.
                int matched = 0, total = 0;
                float far = 0f;
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
                LastPrune += $"{g.name}: anchors {anchors.Count}, baked {matched}/{total} (worst miss {Mathf.Sqrt(far):0.0} m), areas kept {keep.Count}; ";
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
