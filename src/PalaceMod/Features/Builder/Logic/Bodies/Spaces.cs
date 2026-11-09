using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

/*
    Separate spaces (docs/builder-engine.md, "Spaces"): walkable bodies placed outside the host map's graphs (an arena
    100 m off the map's edge, a "dream stage", a boss room) get pathfinding of their own. The host's graphs are one
    tile over the map, and growing them to reach a far place would multiply their cost, so each ground graph is
    duplicated as "PalaceBuilder space <name>", bounded to the off-map decks, and scanned on its own (small, so
    fast). Every unit's graph masks gain the space copy of each graph it uses, so the same unit walks on the map or in
    a space; `teleport` moves units (and the king) between them, and the camera simply follows the king.
    The fliers' graph isn't copied: fliers in a space fly straight (movement "direct").
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class Spaces
    {
        public const string Prefix = "PalaceBuilder space ";

        private static readonly List<Bounds> decks = [];
        private static readonly Dictionary<uint, uint> copyOf = [];   // source graph index -> space graph index
        private static bool dirty;

        public static bool Any => copyOf.Count > 0;

        public static void Clear()
        {
            decks.Clear();
            dirty = false;
            var astar = AstarPath.active;
            if (astar != null)
                foreach (var g in astar.data.graphs.Where(g => g != null && g.name.StartsWith(Prefix)).ToList())
                    astar.data.RemoveGraph(g);
            copyOf.Clear();
        }

        /// <summary>Whether a point is outside every host ground graph.</summary>
        public static bool Outside(Vector3 p)
        {
            var astar = AstarPath.active;
            if (astar == null) return false;
            foreach (var g in astar.data.graphs.OfType<RecastGraph>())
            {
                if (g.name == BodyNav.AirGraph || g.name.StartsWith(Prefix)) continue;
                var b = new Bounds(g.forcedBoundsCenter, g.forcedBoundsSize);
                if (p.x >= b.min.x - 2f && p.x <= b.max.x + 2f && p.z >= b.min.z - 2f && p.z <= b.max.z + 2f) return false;
            }
            return true;
        }

        /// <summary>A walkable deck off the map: it needs a space.</summary>
        public static void Add(Bounds deck)
        {
            decks.Add(deck);
            dirty = true;
        }

        /// <summary>Builds/rescans the space graphs (once a frame when decks were added). Returns ms, or -1.</summary>
        public static long Tick()
        {
            if (!dirty || AstarPath.active == null) return -1;
            dirty = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var astar = AstarPath.active;
            var all = decks[0];
            foreach (var d in decks) all.Encapsulate(d);
            all.Expand(new Vector3(16f, 0f, 16f));
            var sources = astar.data.graphs.OfType<RecastGraph>().Where(g => g.name != BodyNav.AirGraph && !g.name.StartsWith(Prefix)).ToList();
            var spaces = new List<NavGraph>();
            foreach (var src in sources)
            {
                RecastGraph copy = null;
                if (copyOf.TryGetValue(src.graphIndex, out uint idx)) copy = astar.data.graphs[idx] as RecastGraph;
                if (copy == null)
                {
                    copy = (RecastGraph)astar.data.DuplicateGraph(src);
                    copy.name = Prefix + src.name;
                    copyOf[src.graphIndex] = copy.graphIndex;
                }
                copy.forcedBoundsCenter = new Vector3(all.center.x, src.forcedBoundsCenter.y, all.center.z);
                copy.forcedBoundsSize = new Vector3(all.size.x, Mathf.Max(src.forcedBoundsSize.y, all.size.y + 20f), all.size.z);
                copy.minRegionSize = 0f;
                copy.enableNavmeshCutting = true;
                spaces.Add(copy);
            }
            astar.Scan(spaces.ToArray());
            ApplyToAll();
            return sw.ElapsedMilliseconds;
        }

        // ---- unit masks ----------------------------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<PathfindMovementEnemy, GraphMask> enemyOriginal = AccessTools.FieldRefAccess<PathfindMovementEnemy, GraphMask>("graphMaskOriginal");
        private static readonly AccessTools.FieldRef<PathfindMovementEnemy, GraphMask> enemyBackup = AccessTools.FieldRefAccess<PathfindMovementEnemy, GraphMask>("graphMaskBackup");
        private static readonly AccessTools.FieldRef<PathfindMovementEnemy, NNConstraint> enemyNN = AccessTools.FieldRefAccess<PathfindMovementEnemy, NNConstraint>("nearestConstraint");
        private static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, GraphMask> playerOriginal = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, GraphMask>("graphMaskOriginal");
        private static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, NNConstraint> playerNN = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, NNConstraint>("nearestConstraint");

        /// <summary>A mask plus the space copy of every graph in it.</summary>
        public static GraphMask Widen(GraphMask m)
        {
            if (m.containsAllGraphs) return m;
            var r = m;
            foreach (var kv in copyOf)
                if (m.Contains(kv.Key)) r = r | GraphMask.FromGraphIndex(kv.Value);
            return r;
        }

        public static void ApplySeeker(GameObject unit)
        {
            if (!Any || unit == null) return;
            var seeker = unit.GetComponent<Seeker>();
            if (seeker != null) seeker.graphMask = Widen(seeker.graphMask);
        }

        public static void Apply(GameObject unit)
        {
            if (!Any || unit == null) return;
            var seeker = unit.GetComponent<Seeker>();
            if (seeker != null) seeker.graphMask = Widen(seeker.graphMask);
            if (unit.GetComponent<PathfindMovementEnemy>() is { } e)
            {
                enemyOriginal(e) = Widen(enemyOriginal(e));
                enemyBackup(e) = Widen(enemyBackup(e));
                var nn = enemyNN(e);
                if (nn != null) nn.graphMask = Widen(nn.graphMask);
            }
            if (unit.GetComponent<PathfindMovementPlayerunit>() is { } p)
            {
                playerOriginal(p) = Widen(playerOriginal(p));
                var nn = playerNN(p);
                if (nn != null) nn.graphMask = Widen(nn.graphMask);
            }
        }

        public static void ApplyToAll()
        {
            foreach (var m in Object.FindObjectsOfType<PathfindMovement>()) Apply(m.gameObject);
        }

        public static string Describe() => Any ? $"{copyOf.Count} space graphs over {decks.Count} decks" : "no spaces";
    }
}
