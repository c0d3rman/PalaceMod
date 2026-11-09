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
            public string Detail;
            public List<Vector3> Points = [];
            /// <summary>The line's middle, where the route is measured from.</summary>
            public Vector3 From;
            /// <summary>Where the line's middle lands on the graph (its nearest walkable point), if anywhere.</summary>
            public Vector3? Start;
            /// <summary>Flat distance from the line's middle to Start, m: how far off the walkable graph the line is.</summary>
            public float Snap;
            /// <summary>Route length in metres from the line itself (Snap plus the path on the graph); 0 when not computed.</summary>
            public float Length;
            public bool OffGraph => Snap > OffGraphSnap;
        }

        /// <summary>A line further than this from the walkable graph is off it: its enemies appear somewhere else.</summary>
        public const float OffGraphSnap = 10f;

        /// <summary>Rescans every graph. Returns the time it took in ms, or -1 without pathfinding.</summary>
        public static long Rescan()
        {
            // As baked: with the map's invisible walls and flier surface (a plain Scan loses both).
            return GroundGraphs.Scan();
        }

        /// <summary>The castle's position on the loaded map (the castle slot, built or not).</summary>
        public static Vector3? Castle(Scene scene)
        {
            if (CastleCenter.instance != null && CastleCenter.instance.gameObject.scene == scene) return CastleCenter.instance.transform.position;
            var slot = LayoutScene.Slots(scene).FirstOrDefault(LayoutScene.IsCastle);
            return slot != null ? slot.transform.position : null;
        }

        /// <summary>Checks each ground spawn line (at its current place) for a path to the castle.</summary>
        public static List<Route> Check(Scene scene, Func<EnemySpawnLine, Vector3> lineStart = null, bool withPoints = false, Func<EnemySpawnLine, bool> include = null)
        {
            var routes = new List<Route>();
            var astar = AstarPath.active;
            var castle = Castle(scene);
            if (astar == null || castle == null) return routes;
            foreach (var line in LayoutScene.Lines(scene).Where(LayoutScene.IsGroundLine))
            {
                if (include != null && !include(line)) continue;
                var start = lineStart?.Invoke(line) ?? LayoutScene.LineCenter(line.transform);
                // Large enemies need the wider graph; a line that only spawns small ones is checked on the small graph.
                string graph = line.canSpawnBigGround ? "Enemy Units L" : "Enemy Units S";
                routes.Add(FindRoute(line.name, graph, start, castle.Value, withPoints));
            }
            return routes;
        }

        private static Route FindRoute(string name, string graph, Vector3 from, Vector3 to, bool withPoints)
        {
            var route = new Route { Line = name, Graph = graph, From = from };
            try
            {
                var nn = NearestNodeConstraint.Walkable;
                nn.graphMask = GraphMask.FromGraphName(graph);
                var na = AstarPath.active.GetNearest(from, nn);
                var a = na.node;
                var b = AstarPath.active.GetNearest(to, nn).node;
                if (a != null)
                {
                    route.Start = na.position;
                    var flat = na.position - from;
                    flat.y = 0f;
                    route.Snap = flat.magnitude;
                }
                route.Reachable = a != null && b != null && PathUtilities.IsPathPossible(a, b);
                route.Detail = $"from {from} -> {(a != null ? na.position.ToString() : "nothing")} ({route.Snap:0.0} m) area {a?.Area}; to {to} -> node {(Vector3?)b?.position} area {b?.Area}";
                // The nearest walkable node can lie outside a ring of props that closes in the spawn point (the inside
                // is too small to be walkable), which would make a walled-in spawn look connected.
                if (route.Reachable && a != null && WalledIn(from, (Vector3)a.position))
                {
                    route.Reachable = false;
                    route.Detail += "; spawn point walled in by props";
                }
                if (withPoints && a != null && b != null)
                {
                    var path = ABPath.Construct(from, to);
                    path.traversalConstraint.graphMask = nn.graphMask;
                    AstarPath.StartPath(path);
                    path.BlockUntilCalculated();
                    if (!path.error)
                    {
                        // The raw vectorPath runs through triangle centres, and a flat field's navmesh triangles are
                        // tens of metres across: two lines in one triangle got the same route and length, and moving
                        // a line inside it changed nothing. String-pull it from the exact start (the line's middle,
                        // or the walkable point nearest it) to the castle, as the movement code does, and add the
                        // walk from the line onto the graph.
                        var pts = Pulled(path);
                        float length = route.Snap;
                        for (int i = 1; i < pts.Count; i++) length += Vector3.Distance(pts[i - 1], pts[i]);
                        route.Length = length;
                        route.Points = pts;
                        if (route.Snap > OnGraphDistance) route.Points.Insert(0, from);
                    }
                }
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"path check for {name} failed: {e.Message}");
            }
            return route;
        }

        /// <summary>The path's corners, string-pulled through its navmesh triangles (what FunnelModifier does) from
        /// its snapped start point to its snapped end point.</summary>
        private static List<Vector3> Pulled(ABPath path)
        {
            var result = new List<Vector3>();
            if (path.path == null || path.path.Count == 0 || path.vectorPath == null || path.vectorPath.Count == 0) return result;
            path.vectorPath[0] = path.startPoint;
            path.vectorPath[path.vectorPath.Count - 1] = path.endPoint;
            var parts = Funnel.SplitIntoParts(path);
            foreach (var part in parts)
            {
                if (part.type == Funnel.PartType.NodeSequence && path.path[part.startIndex] is TriangleMeshNode)
                {
                    var funnel = Funnel.ConstructFunnelPortals(path.path, part);
                    result.AddRange(Funnel.Calculate(funnel, false));
                }
                else
                {
                    result.Add(part.startPoint);
                    result.Add(part.endPoint);
                }
            }
            if (result.Count == 0) result.AddRange(path.vectorPath);
            return result;
        }

        public sealed class Reachability
        {
            public string Graph;
            public bool Reachable;
            public float Snap, SnapTo;
        }

        /// <summary>Whether each ground graph connects two points (each snapped to its nearest walkable point).</summary>
        public static List<Reachability> Reach(Vector3 a, Vector3 b)
        {
            var list = new List<Reachability>();
            var astar = AstarPath.active;
            if (astar == null) return list;
            foreach (var g in astar.data.graphs)
            {
                if (g == null || g.name == "Enemy Units Air") continue;
                var nn = NearestNodeConstraint.Walkable;
                nn.graphMask = GraphMask.FromGraph(g);
                var na = astar.GetNearest(a, nn);
                var nb = astar.GetNearest(b, nn);
                float Flat(Vector3 p, Vector3 q) { var d = p - q; d.y = 0f; return d.magnitude; }
                list.Add(new Reachability
                {
                    Graph = g.name,
                    Reachable = na.node != null && nb.node != null && PathUtilities.IsPathPossible(na.node, nb.node),
                    Snap = na.node != null ? Flat(na.position, a) : -1f,
                    SnapTo = nb.node != null ? Flat(nb.position, b) : -1f,
                });
            }
            return list;
        }

        // A spawn this close to its nearest node is on the walkable area itself (vanilla spawns are, or nearly).
        private const float OnGraphDistance = 1.5f;

        /// <summary>Whether a solid layout prop stands between the spawn point and the walkable node it snaps to, i.e.
        /// the spawn's own spot is cut off from the graph. Only the editor's prop blockers count, never the map's own
        /// scenery, so a vanilla spawn just off the walkable area still passes.</summary>
        private static bool WalledIn(Vector3 spawn, Vector3 node)
        {
            var flat = node - spawn;
            flat.y = 0f;
            if (flat.magnitude <= OnGraphDistance) return false;
            Physics.SyncTransforms();
            // Cast from the node back to the spawn, so a spawn inside a prop still hits that prop's face.
            var lift = Vector3.up * 0.5f;
            var origin = node + lift;
            var toSpawn = spawn + lift - origin;
            return Physics.RaycastAll(origin, toSpawn.normalized, toSpawn.magnitude, 1 << LayoutScene.ObstacleLayer, QueryTriggerInteraction.Collide)
                .Any(h => h.collider != null && h.collider.name == LayoutScene.BlockerName);
        }

        public const string AirGraph = "Enemy Units Air";

        /// <summary>A flier line further than this from the fliers' graph is worth a warning.</summary>
        public const float AirOffGraphSnap = 30f;

        public sealed class AirLine
        {
            public string Line;
            /// <summary>The line's point furthest from the fliers' graph and its flat distance to it, m (-1: no graph).</summary>
            public Vector3 Worst;
            public float Snap;
            /// <summary>Where a flier from Worst starts (the nearest point of the fliers' graph).</summary>
            public Vector3? Start;
            /// <summary>Further than the pathfinding's nearest-node limit: the game alone would put its fliers at an
            /// infinite position (BuilderLevelManager.SnapToNavmesh_Patch moves them onto the graph instead).</summary>
            public bool Lost;
            /// <summary>Further than AirOffGraphSnap: worth a warning (the maps' own flier lines sit up to ~15 m off a flat
            /// field's surface, which only shifts where their fliers appear).</summary>
            public bool OffGraph => Snap < 0f || Snap > AirOffGraphSnap;
        }

        /// <summary>Each flier spawn line's distance from the fliers' graph, measured at its points (a 40 m line
        /// can reach the graph at one end and miss it at the other).</summary>
        public static List<AirLine> CheckAir(Scene scene)
        {
            var list = new List<AirLine>();
            var astar = AstarPath.active;
            if (astar == null || !astar.data.graphs.Any(g => g != null && g.name == AirGraph)) return list;
            var nn = new NNConstraint { constrainDistance = false, graphMask = GraphMask.FromGraphName(AirGraph) };
            float limit = astar.maxNearestNodeDistance;
            foreach (var line in LayoutScene.Lines(scene).Where(l => l.canSpawnFlying))
            {
                var points = new List<Vector3>();
                for (int i = 0; i < line.transform.childCount; i++) points.Add(line.transform.GetChild(i).position);
                if (points.Count == 0) points.Add(line.transform.position);
                points.Add(LayoutScene.LineCenter(line.transform));
                var result = new AirLine { Line = line.name, Snap = 0f };
                foreach (var p in points)
                {
                    var hit = astar.GetNearest(p, nn);
                    if (hit.node == null) { result.Snap = -1f; result.Worst = p; result.Start = null; break; }
                    float d = Flat(hit.position, p);
                    if (d >= result.Snap) { result.Snap = d; result.Worst = p; result.Start = hit.position; }
                    if ((hit.position - p).magnitude > limit) result.Lost = true;
                }
                list.Add(result);
            }
            return list;
        }

        private static float Flat(Vector3 a, Vector3 b) { var d = a - b; d.y = 0f; return d.magnitude; }

        /// <summary>What's wrong with the graphs for this level, in a few words each: an empty ground or flier graph,
        /// a ground line with no route to the castle, a flier line off the fliers' graph.</summary>
        public static List<string> Problems(Scene scene)
        {
            var problems = new List<string>();
            var astar = AstarPath.active;
            if (astar == null || !scene.IsValid()) return problems;
            foreach (var g in astar.data.graphs)
            {
                if (g == null) continue;
                int walkable = 0;
                g.GetNodes(n => { if (n.Walkable) walkable++; });
                if (walkable == 0) problems.Add($"graph {g.name} is empty");
            }
            // Standing buildings cut the navmesh (a wall closing a road is meant to): a route check only judges the layout.
            if (BuiltCutters(scene) == 0)
                foreach (var r in Check(scene).Where(r => !r.Reachable))
                    problems.Add($"{r.Line}: no route to the castle on {r.Graph}");
            foreach (var a in CheckAir(scene).Where(a => a.OffGraph))
                problems.Add(a.Snap < 0f ? $"{a.Line}: no fliers' graph" : $"{a.Line}: {a.Snap:0} m from the fliers' graph, fliers start at its edge");
            return problems;
        }

        /// <summary>
        /// How many standing buildings (the castle aside) cut the navmesh. The game's buildings carry NavmeshCuts that
        /// are on while the building is alive (Hp.SetAlive), so built walls and towers close the roads they stand on and
        /// enemies attack them instead; a route check is only about the layout while none stand (a fresh start). A
        /// resumed run, a retried day or a loaded savestate brings its buildings back before the level-start check.
        /// </summary>
        public static int BuiltCutters(Scene scene)
        {
            if (!scene.IsValid()) return 0;
            int n = 0;
            foreach (var slot in LayoutScene.Slots(scene))
            {
                if (slot == null || slot.Level <= 0 || LayoutScene.IsCastle(slot)) continue;
                if (slot.GetComponentsInChildren<NavmeshCut>().Any(c => c.enabled)) n++;
            }
            return n;
        }

        /// <summary>What each recast graph rasterizes (dev).</summary>
        public static string DescribeGraphs() => AstarPath.active == null ? "no pathfinding" : string.Join("\n", AstarPath.active.data.graphs.OfType<RecastGraph>().Select(g =>
            $"{g.name}: mode={g.collectionSettings.collectionMode} tags=[{string.Join(",", g.collectionSettings.tagMask)}] " +
            $"bounds={g.forcedBoundsCenter}/{g.forcedBoundsSize} " +
            $"colliders={g.collectionSettings.rasterizeColliders} meshes={g.collectionSettings.rasterizeMeshes} " +
            $"layers={string.Join(",", Enumerable.Range(0, 32).Where(l => (g.collectionSettings.layerMask.value & (1 << l)) != 0).Select(l => LayerMask.LayerToName(l)))} " +
            $"cell={g.cellSize} radius={g.characterRadius} climb={g.walkableClimb} slope={g.maxSlope} minRegion={g.minRegionSize}"));

        public static string Describe(List<Route> routes) =>
            routes.Count == 0 ? "no ground spawn lines to check" : string.Join(", ", routes.Select(r => $"{r.Line} {(r.Reachable ? "ok" : "BLOCKED")}"));
    }
}
