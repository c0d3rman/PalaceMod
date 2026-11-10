using System;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Where the king starts on a Builder level (docs/builder-ux.md, "Play mode", "King start").

    The game starts the king wherever the host scene has him (PlayerMovement.Start makes that his startPosition, where
    he respawns after falling below the map). A layout can cover that spot (a flat or sculpted field with a cliff, water
    or a prop on it), and Studio test play drops him at a point the designer chose, which can be anything the cursor
    ray hit: off the map's graphs, past its edge.

    - Walkable: a point snapped to the walkable ground the player's units walk on ("Player Units S"), or null when
      nothing walkable is that close.
    - Settle (BuilderLevelManager, after the level-start rescan, so on the graphs the level plays on): a start off that
      ground, or on ground the castle can't be reached from, moves to the nearest walkable spot on the castle's side,
      tried along the line from the start to the castle; startPosition moves with it.
*/
namespace PalaceMod.Builder.Level
{
    internal static class KingStart
    {
        private static readonly AccessTools.FieldRef<PlayerMovement, Vector3> StartRef = AccessTools.FieldRefAccess<PlayerMovement, Vector3>("startPosition");

        /// <summary>The graph the king's ground is judged on: the small player units walk where he can.</summary>
        public const string Graph = "Player Units S";

        /// <summary>What the last Settle did (dev).</summary>
        public static string LastSettle = "";

        private static NNInfo NearestNode(Vector3 p)
        {
            var astar = AstarPath.active;
            if (astar == null) return default;
            var nn = NearestNodeConstraint.Walkable;
            nn.graphMask = GraphMask.FromGraphName(Graph);
            return astar.GetNearest(p, nn);
        }

        private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        /// <summary>The walkable point nearest to p (on the king's ground, within maxOffset metres across), or null:
        /// nothing walkable that close, or no pathfinding. With area, only that connected region counts.</summary>
        public static Vector3? Walkable(Vector3 p, float maxOffset, uint? area = null)
        {
            var hit = NearestNode(p);
            if (hit.node == null || !hit.node.Walkable) return null;
            if (area is uint a && hit.node.Area != a) return null;
            return Flat(hit.position, p) <= maxOffset ? hit.position : null;
        }

        /// <summary>The connected region the castle stands in, or null without pathfinding or a castle.</summary>
        public static uint? CastleArea(Scene scene)
        {
            var castle = CastleCenter.instance != null ? CastleCenter.instance.transform : LayoutScene.Castle(scene)?.transform;
            if (castle == null) return null;
            var hit = NearestNode(castle.position);
            return hit.node != null ? hit.node.Area : null;
        }

        /// <summary>A start point near p the king can play from: p snapped onto walkable ground on the castle's side,
        /// else the first such point on the way from p to the castle, else the walkable point nearest the castle.
        /// Null without pathfinding.</summary>
        public static Vector3? Near(Scene scene, Vector3 p, float maxOffset = 1.5f)
        {
            if (AstarPath.active == null) return null;
            uint? area = CastleArea(scene);
            if (Walkable(p, maxOffset, area) is Vector3 here) return here;
            var castle = CastleCenter.instance != null ? CastleCenter.instance.transform.position : LayoutScene.Castle(scene)?.transform.position;
            if (castle is not Vector3 c) return Walkable(p, 8f);
            float length = Flat(p, c);
            for (float d = 2f; d < length; d += 2f)
            {
                var q = Vector3.Lerp(p, c, d / length);
                if (Walkable(q, maxOffset, area) is Vector3 v) return v;
            }
            var near = NearestNode(c);
            return near.node != null ? near.position : null;
        }

        /// <summary>Moves the king's start (and the king, if he's still near it) onto walkable ground on the castle's
        /// side when the level left it elsewhere. Call after the graphs are final. True if it moved.</summary>
        public static bool Settle(Scene scene)
        {
            var king = PlayerMovement.instance;
            LastSettle = "no king";
            if (king == null || king.gameObject.scene != scene || AstarPath.active == null) return false;
            Vector3 start = StartRef(king);
            if (start == Vector3.zero) start = king.transform.position;   // before his Start
            var to = Near(scene, start);
            if (to is not Vector3 v || Flat(v, start) <= 1.5f)
            {
                LastSettle = $"king start {start} is on walkable ground";
                return false;
            }
            var target = LayoutScene.Ground(v) + Vector3.up * 0.1f;
            StartRef(king) = target;
            if (Flat(king.transform.position, start) < 2f)
            {
                var cc = king.GetComponent<CharacterController>();
                if (cc != null && cc.enabled) king.TeleportTo(target);
                else king.transform.position = target;
            }
            LastSettle = $"king start moved from {start} to {target} (off walkable ground)";
            return true;
        }
    }
}
