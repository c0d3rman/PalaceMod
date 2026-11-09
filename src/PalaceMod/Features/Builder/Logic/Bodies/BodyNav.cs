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
