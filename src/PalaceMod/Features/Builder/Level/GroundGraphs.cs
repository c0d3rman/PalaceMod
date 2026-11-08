using System.Collections.Generic;
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

        /// <summary>Rescans every graph as baked. Returns the time it took in ms, or -1 without pathfinding.</summary>
        public static long Scan()
        {
            var astar = AstarPath.active;
            if (astar == null) return -1;
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
                astar.Scan();
            }
            finally
            {
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
            }
            PruneIslands();
            MinimapRefresh.Rebuild();
            return sw.ElapsedMilliseconds;
        }

        /// <summary>The bake kept only the walkable area around the castle; a rescan also finds cliff and rock tops and
        /// the walls' tops. Those islands are made unwalkable on the ground graphs (enemies would snap onto them, and
        /// the minimap would draw them).</summary>
        private static void PruneIslands()
        {
            var astar = AstarPath.active;
            var castle = CastleCenter.instance != null ? CastleCenter.instance.transform.position
                : PathCheck.Castle(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            if (astar == null || castle == null) return;
            var drop = new List<GraphNode>();
            foreach (var g in astar.data.graphs)
            {
                if (g == null || g.name == "Enemy Units Air") continue;
                var nn = NearestNodeConstraint.Walkable;
                nn.graphMask = GraphMask.FromGraph(g);
                var keep = astar.GetNearest(castle.Value, nn).node;
                if (keep == null) continue;
                uint area = keep.Area;
                g.GetNodes(n => { if (n.Walkable && n.Area != area) drop.Add(n); });
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
