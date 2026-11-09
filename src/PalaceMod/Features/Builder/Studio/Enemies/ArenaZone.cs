using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Library;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    The Enemy Studio's fight ground: the stage's platform, well outside the level (EnemyStage.Place: past the east edge
    of every pathfinding graph and prop), with a flat floor and a small grid graph of its own, so a test fight never
    touches the level's geometry, its buildings or the king.

    - Pathfinding: a GridGraph ("PalaceBuilder Arena", flat, no collision checks) is added to AstarPath and scanned on
      its own. Every arena unit's Seeker (and its backup graph) is pointed at it before its Start, which is when the
      game's movement scripts copy the mask into their nearest-node constraint, so they path and snap on the platform
      only.
    - Targeting: the game's target searches (TargetPriority, TagManager) know only where the searcher stands. While the
      zone exists, a search from inside it sees only things inside it, and a search from outside sees nothing inside
      (EnemyStudioManager.ArenaSearch patches). Arena enemies can't pick the king, the castle or a building, and the
      level's troops and towers never notice the fight.
    Remove() takes out the graph and the platform; the level's graphs are never rescanned, so it is left as it was.
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    internal static class ArenaZone
    {
        public const string GraphName = "PalaceBuilder Arena";
        public const int Cells = 44;
        /// <summary>Searches within this of the centre (horizontally) count as inside the arena.</summary>
        public const float Reach = 80f;

        private static GameObject root;
        private static GridGraph graph;

        public static bool Active => root != null;
        public static Vector3 Center { get; private set; }

        /// <summary>Set while a target search runs (EnemyStudioManager.ArenaSearch): where the searcher stands.</summary>
        internal static Vector3? SearchFrom;

        public static bool Inside(Vector3 p)
        {
            float dx = p.x - Center.x, dz = p.z - Center.z;
            return dx * dx + dz * dz < Reach * Reach;
        }

        /// <summary>Drops search results on the other side of the arena's boundary from the searcher.</summary>
        internal static void Filter(List<TaggedObject> found, Vector3 from)
        {
            if (!Active || found == null) return;
            bool inside = Inside(from);
            found.RemoveAll(t => t == null || Inside(t.transform.position) != inside);
        }

        /// <summary>Builds the floor and its graph at <paramref name="center"/> (the stage's centre, outside the
        /// level).</summary>
        public static void Create(Scene scene, Vector3 center)
        {
            Remove();
            var astar = AstarPath.active ?? throw new InvalidOperationException("no pathfinding in this scene");
            Center = center;

            root = new GameObject("PalaceBuilder Enemy arena");
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(root, scene);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            floor.transform.position = Center - new Vector3(0f, 1f, 0f);
            floor.transform.localScale = new Vector3(Cells + 2f, 2f, Cells + 2f);
            floor.transform.position = Center - new Vector3(0f, 1f, 0f);
            int ground = LayerMask.NameToLayer("Ground");
            if (ground >= 0) floor.layer = ground;
            var stone = SceneryLibrary.LiveMaterial("Mountain");
            if (stone != null) floor.GetComponent<MeshRenderer>().sharedMaterial = stone;

            graph = (GridGraph)astar.data.AddGraph(typeof(GridGraph));
            graph.name = GraphName;
            graph.center = Center;
            graph.SetDimensions(Cells, Cells, 1f);
            graph.collision.collisionCheck = false;
            graph.collision.heightCheck = false;
            astar.Scan(graph);
            Log.For<BuilderManager>().Info($"Enemy Studio arena at {Center}");
        }

        /// <summary>Points a unit's pathfinding at the arena's graph. Call before the unit's first Start.</summary>
        public static void Bind(GameObject unit)
        {
            if (graph == null || unit == null) return;
            var mask = GraphMask.FromGraph(graph);
            foreach (var s in unit.GetComponentsInChildren<Seeker>(true)) s.graphMask = mask;
            foreach (var m in unit.GetComponentsInChildren<PathfindMovementEnemy>(true)) m.backupMovementGraph = GraphName;
            foreach (var m in unit.GetComponentsInChildren<PathfindMovementPlayerunit>(true)) m.backupMovementGraph = GraphName;
        }

        /// <summary>A point on the platform: the centre moved by an offset, kept on the floor.</summary>
        public static Vector3 At(Vector3 offset)
        {
            float half = Cells * 0.5f - 1.5f;
            return new Vector3(Center.x + Mathf.Clamp(offset.x, -half, half), Center.y, Center.z + Mathf.Clamp(offset.z, -half, half));
        }

        public static void Remove()
        {
            SearchFrom = null;
            if (graph != null)
            {
                // Only from the pathfinding it was added to (a scene change takes that, and the graph, with it).
                try { if (AstarPath.active != null && Array.IndexOf(AstarPath.active.data.graphs ?? [], graph) >= 0) AstarPath.active.data.RemoveGraph(graph); }
                catch (Exception e) { Log.For<BuilderManager>().Warn($"Enemy Studio: couldn't remove the arena graph: {e.Message}"); }
                graph = null;
            }
            if (root != null) Object.Destroy(root);
            root = null;
        }
    }
}
