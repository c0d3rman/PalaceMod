using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Pathfinding after layout edits: one full rescan of the host's graphs (~0.4-1 s, so only at level start or when the
    editor asks), then a reachability check from every ground spawn line to the castle on the graph its enemies use.
*/
namespace PalaceMod.Builder.Level
{
    internal static class PathCheck
    {
        public sealed class Route
        {
            public string Line;
            public string Graph;
            public bool Reachable;
            public List<Vector3> Points = [];
        }

        /// <summary>Rescans every graph. Returns the time it took in ms, or -1 without pathfinding.</summary>
        public static long Rescan()
        {
            var astar = AstarPath.active;
            if (astar == null) return -1;
            var sw = Stopwatch.StartNew();
            astar.Scan();
            return sw.ElapsedMilliseconds;
        }

        /// <summary>The castle's position on the loaded map (the castle slot, built or not).</summary>
        public static Vector3? Castle(Scene scene)
        {
            if (CastleCenter.instance != null && CastleCenter.instance.gameObject.scene == scene) return CastleCenter.instance.transform.position;
            var slot = LayoutScene.Slots(scene).FirstOrDefault(LayoutScene.IsCastle);
            return slot != null ? slot.transform.position : null;
        }

        /// <summary>Checks each ground spawn line (at its current place) for a path to the castle.</summary>
        public static List<Route> Check(Scene scene, Func<EnemySpawnLine, Vector3> lineStart = null, bool withPoints = false)
        {
            var routes = new List<Route>();
            var astar = AstarPath.active;
            var castle = Castle(scene);
            if (astar == null || castle == null) return routes;
            foreach (var line in LayoutScene.Lines(scene).Where(LayoutScene.IsGroundLine))
            {
                var start = lineStart?.Invoke(line) ?? LayoutScene.LineCenter(line.transform);
                // Large enemies need the wider graph; a line that only spawns small ones is checked on the small graph.
                string graph = line.canSpawnBigGround ? "Enemy Units L" : "Enemy Units S";
                routes.Add(FindRoute(line.name, graph, start, castle.Value, withPoints));
            }
            return routes;
        }

        private static Route FindRoute(string name, string graph, Vector3 from, Vector3 to, bool withPoints)
        {
            var route = new Route { Line = name, Graph = graph };
            try
            {
                var nn = NearestNodeConstraint.Walkable;
                nn.graphMask = GraphMask.FromGraphName(graph);
                var a = AstarPath.active.GetNearest(from, nn).node;
                var b = AstarPath.active.GetNearest(to, nn).node;
                route.Reachable = a != null && b != null && PathUtilities.IsPathPossible(a, b);
                if (withPoints && a != null && b != null)
                {
                    var path = ABPath.Construct(from, to);
                    path.traversalConstraint.graphMask = nn.graphMask;
                    AstarPath.StartPath(path);
                    path.BlockUntilCalculated();
                    // The raw path zigzags through triangle centres; every fourth point reads as a route.
                    if (!path.error) route.Points = path.vectorPath.Where((_, i) => i % 4 == 0 || i == path.vectorPath.Count - 1).ToList();
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Builder: path check for {name} failed: {e.Message}");
            }
            return route;
        }

        public static string Describe(List<Route> routes) =>
            routes.Count == 0 ? "no ground spawn lines to check" : string.Join(", ", routes.Select(r => $"{r.Line} {(r.Reachable ? "ok" : "BLOCKED")}"));
    }
}
