using System.Collections.Generic;
using System.Linq;
using Pathfinding;
using UnityEngine;

/*
    Navmesh helpers for bodies: the ground graphs' mask (cuts never touch the fliers' graph), the navmesh update rate
    for moving cuts, and the push-out of units a cut would trap.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyNav
    {
        public const string AirGraph = "Enemy Units Air";

        public static GraphMask GroundMask()
        {
            var astar = AstarPath.active;
            if (astar == null) return GraphMask.everything;
            GraphMask mask = default;
            bool any = false;
            foreach (var g in astar.data.graphs)
                if (g != null && g.name != AirGraph) { mask = any ? mask | GraphMask.FromGraph(g) : GraphMask.FromGraph(g); any = true; }
            return any ? mask : GraphMask.everything;
        }

        /// <summary>Where a unit spawned at a point stands: the nearest free walkable ground node (host graphs and
        /// spaces, or the unit's own graphs) within 20 m, else the point itself. Free: on the map's real ground, not
        /// an island a rescan found on a rock or wall top or inside the map's boundary walls (which a building's
        /// navmesh cut brings back until the next scan; Level/GroundGraphs.RealAreas), and not inside an obstacle's
        /// collider.</summary>
        public static Vector3 UnitGround(Vector3 p, GraphMask? mask = null)
        {
            var hit = NearestFree(p, mask ?? GroundMask());
            if (hit.node == null) return p;
            var flat = new Vector2(hit.position.x - p.x, hit.position.z - p.z);
            return flat.sqrMagnitude < 400f ? hit.position : p;
        }

        /// <summary>The nearest walkable node on the graphs, preferring a free one (see <see cref="UnitGround"/>)
        /// within 20 m of the nearest.</summary>
        public static NNInfo NearestFree(Vector3 p, GraphMask mask)
        {
            var astar = AstarPath.active;
            if (astar == null) return default;
            var nn = NearestNodeConstraint.Walkable;
            nn.graphMask = mask;
            var hit = astar.GetNearest(p, nn);
            if (hit.node == null || Free(hit.node)) return hit;
            nn.filter = Free;
            nn.maxDistance = Vector3.Distance(p, hit.position) + 20f;
            var free = astar.GetNearest(p, nn);
            return free.node != null ? free : hit;
        }

        private static readonly int ObstacleMask = 1 << Level.LayoutScene.ObstacleLayer;
        private static int realFrame = -1;
        private static readonly Dictionary<uint, HashSet<uint>> realAreas = [];

        private static bool Free(GraphNode n)
        {
            if (n == null || !n.Walkable) return false;
            if (realFrame != Time.frameCount) { realFrame = Time.frameCount; realAreas.Clear(); }
            if (!realAreas.TryGetValue(n.GraphIndex, out var real))
                realAreas[n.GraphIndex] = real = Level.GroundGraphs.RealAreas(n.Graph);
            if (real != null && !real.Contains(n.Area)) return false;
            var c = (Vector3)n.position;
            return !Physics.CheckCapsule(c + Vector3.up * 0.5f, c + Vector3.up * 1.5f, 0.3f, ObstacleMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Moving cuts follow at most 4 times a second (docs/builder-engine.md, "Movers").</summary>
        public static void UseMoverRate()
        {
            var astar = AstarPath.active;
            if (astar != null && (astar.navmeshUpdates.updateInterval < 0.25f)) astar.navmeshUpdates.updateInterval = 0.25f;
        }

        /// <summary>Units standing in a cut (further than snapping reaches) are moved to the nearest walkable point.</summary>
        public static int PushOut(Bounds area)
        {
            var astar = AstarPath.active;
            if (astar == null) return 0;
            int moved = 0;
            area.Expand(new Vector3(2f, 4f, 2f));
            foreach (var m in Object.FindObjectsOfType<PathfindMovement>())
            {
                if (m == null || !area.Contains(m.transform.position)) continue;
                var seeker = m.GetComponent<Seeker>();
                var nn = NearestNodeConstraint.Walkable;
                if (seeker != null) nn.graphMask = seeker.graphMask;
                var hit = astar.GetNearest(m.transform.position, nn);
                if (hit.node == null) continue;
                var p = m.transform.position;
                if (new Vector2(hit.position.x - p.x, hit.position.z - p.z).sqrMagnitude < 1.5f * 1.5f) continue;
                m.transform.position = hit.position;
                m.ClearCurrentPath();
                moved++;
            }
            return moved;
        }
    }
}
