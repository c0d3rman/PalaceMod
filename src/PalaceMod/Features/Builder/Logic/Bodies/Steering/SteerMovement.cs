using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Movement modes on the game's own movement components (docs/builder-engine.md, `movement`).

    Everything but `direct` steers through the destination: a prefix on FindMoveToTarget (enemies; player units also
    FindTargetPosition) returns where to go (a path waypoint, a moveTo point, a flee/kite point, a wander point, the
    hold point, or the targeting override's target), and the game's A*, RVO avoidance, navmesh snapping and walk
    animation do the rest. `direct` replaces the movement's Update with a straight line through RVO (no navmesh):
    flyers, ghosts.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal static class SteerMovement
    {
        // PathfindMovementEnemy internals
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, bool> E_walkingHome = AccessTools.FieldRefAccess<PathfindMovementEnemy, bool>("currentlyWalkingHome");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, bool> E_chasing = AccessTools.FieldRefAccess<PathfindMovementEnemy, bool>("currentlyChasingPlayer");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, TaggedObject> E_seekObj = AccessTools.FieldRefAccess<PathfindMovementEnemy, TaggedObject>("seekToTaggedObj");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, TargetPriority> E_prio = AccessTools.FieldRefAccess<PathfindMovementEnemy, TargetPriority>("targetPrio");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, Vector3> E_home = AccessTools.FieldRefAccess<PathfindMovementEnemy, Vector3>("homePosition");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, Vector3> E_homeOffset = AccessTools.FieldRefAccess<PathfindMovementEnemy, Vector3>("homeOffset");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, Pathfinding.Seeker> E_seeker = AccessTools.FieldRefAccess<PathfindMovementEnemy, Pathfinding.Seeker>("seeker");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, float> E_slowedFor = AccessTools.FieldRefAccess<PathfindMovementEnemy, float>("slowedFor");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, float> E_speedSlowed = AccessTools.FieldRefAccess<PathfindMovementEnemy, float>("speedWhenSlowed");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, float> E_repath = AccessTools.FieldRefAccess<PathfindMovementEnemy, float>("recalculatePathCooldown");
        internal static readonly AccessTools.FieldRef<PathfindMovementEnemy, float> E_agroTime = AccessTools.FieldRefAccess<PathfindMovementEnemy, float>("remainingPlayerAgroTime");
        // PathfindMovementPlayerunit internals
        internal static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, bool> P_walkingHome = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, bool>("currentlyWalkingHome");
        internal static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, TaggedObject> P_seekObj = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, TaggedObject>("seekToTaggedObj");
        internal static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, TargetPriority> P_prio = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, TargetPriority>("targetPrio");
        internal static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, Vector3> P_homeOriginal = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, Vector3>("homePositionOriginal");
        internal static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, float> P_repath = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, float>("recalculatePathCooldown");
        internal static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, Pathfinding.RVO.RVOController> P_rvo = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, Pathfinding.RVO.RVOController>("rvo");

        private static readonly List<TaggedObject> one = [];
        private const float Arrive = 1.5f;

        public static bool Started(SteerState st) =>
            st.Enemy != null ? E_seeker(st.Enemy) != null : st.Player != null && P_rvo(st.Player) != null;

        /// <summary>Repath now (a new destination should not wait for the game's repath timer).</summary>
        public static void Repath(SteerState st)
        {
            if (st.Enemy != null && Started(st)) E_repath(st.Enemy) = 0;
            else if (st.Player != null && Started(st)) P_repath(st.Player) = 0;
            st.DirectRepath = 0;
        }

        public static Vector3 Home(SteerState st) =>
            st.Enemy != null ? E_home(st.Enemy) : st.Player != null ? st.Player.HomePosition : st.Go.transform.position;

        /// <summary>Once the movement has started (its Start captured the spawn point), write the template's base values.</summary>
        public static void ApplyBase(SteerState st)
        {
            var m = st.Move;
            if (!st.BaseWritten)
            {
                st.BaseWritten = true;
                if (m.Speed is float s)
                {
                    if (st.Enemy) st.Enemy.movementSpeed = s;
                    if (st.Player) st.Player.movementSpeed = s;
                    if (st.Entity != null) StatsService.Instance.BaseChanged(st.Entity, "moveSpeed");   // modifiers re-apply on the new base
                }
                if (m.KeepDistance is float k)
                {
                    if (st.Enemy) st.Enemy.keepDistanceOf = k;
                    if (st.Player) st.Player.keepDistanceOf = k;
                    if (st.Entity != null) StatsService.Instance.BaseChanged(st.Entity, "keepDistance");
                }
            }
            if (!st.HomeApplied && Started(st))
            {
                st.HomeApplied = true;
                if (m.Home != null && m.Home.Type != JTokenType.Null && !(m.Home.Type == JTokenType.String && (string)m.Home == "spawn"))
                {
                    var p = SteerPaths.Position(m.Home, st.Entity);
                    if (p is Vector3 h)
                    {
                        h = Ground(st, h);
                        if (st.Enemy) { E_home(st.Enemy) = h; E_homeOffset(st.Enemy) = Vector3.zero; }
                        if (st.Player) { st.Player.HomePosition = h; P_homeOriginal(st.Player) = h; st.Player.HasReachedHomePositionAlready = false; }
                    }
                }
            }
        }

        public static Vector3 Ground(SteerState st, Vector3 p) =>
            st.Enemy != null ? st.Enemy.GetNearestGroundPosition(p) : st.Player != null ? st.Player.GetNearestGroundPosition(p) : p;

        /// <summary>
        /// Where the unit should go now, or false for the game's own choice. <paramref name="target"/> is the object it
        /// walks to (live-tracked), <paramref name="walkingHome"/> means "don't stop at keepDistance".
        /// </summary>
        public static bool Destination(SteerState st, bool playerUnit, out Vector3 dest, out TaggedObject target, out bool walkingHome)
        {
            dest = default; target = null; walkingHome = true;
            var world = LogicWorld.Current;
            if (world == null) return false;
            ApplyBase(st);
            var tr = st.Go.transform;
            var pos = tr.position;
            var m = st.Move;
            double now = world.Time;

            // 1. moveTo
            if (st.MoveTo is { } mt)
            {
                var at = mt.Point;
                if (Flat(pos, at) <= Arrive)
                {
                    st.MoveTo = null;
                    if (mt.Then == "hold") { st.MovementOverlays.Add((new JObject { ["mode"] = "hold" }, double.PositiveInfinity)); st.MovementChanged(); st.HoldPoint = pos; }
                    LogicEvents.Raise("arrive", st.Entity, null, 0, pos.ToVec());
                }
                else { dest = at; return true; }
            }

            // 2. flee
            if (st.Fleeing)
            {
                var to = m.FleeTo != null ? SteerPaths.Position(m.FleeTo, st.Entity) : null;
                if (to is Vector3 t) { dest = t; return true; }
                var from = Nearest(st, m.FleeFrom, 25f);
                if (from != null) { dest = Away(st, pos, from.Position.ToVector3(), 8f); return true; }
            }

            // 3. kite
            if (st.KiteUntil > now && st.KiteFrom != null && st.KiteFrom.Exists)
            {
                dest = Away(st, pos, st.KiteFrom.Position.ToVector3(), Mathf.Max(m.KiteBelow, 4f));
                return true;
            }

            // 4. follow a leader (formation): its slot, or `then` without one
            if (m.Mode == "follow")
            {
                var leader = FollowLeader(st, now);
                if (leader != null)
                {
                    var slot = FollowSlot(st, leader, pos);
                    st.FollowDest = slot;
                    dest = Flat(pos, slot) <= 0.3f ? pos : slot;
                    walkingHome = true;
                    return true;
                }
                st.FollowDest = null;
                if (m.Then == "hold") { st.HoldPoint ??= pos; dest = st.HoldPoint.Value; walkingHome = Flat(pos, dest) > 0.5f; return true; }
                if (m.Then == "destroy") { LogicSteeringManager.DestroySilently(st.Entity, st.Go); dest = pos; return true; }
                // pathfind: on its own (targeting below, or the game's choice)
            }
            else st.FollowDest = null;

            // 5. path
            if (m.Mode == "path" && !st.PathDone)
            {
                if (st.PathPoints == null)
                {
                    // Points the unit can stand on: a point inside a building, a cut or water would never be reached.
                    st.PathPoints = SteerPaths.Points(m.Path, st.Entity);
                    var seeker = st.Go != null ? st.Go.GetComponent<Pathfinding.Seeker>() : null;
                    if (seeker != null && AstarPath.active != null)
                    {
                        var nn = Pathfinding.NearestNodeConstraint.Walkable;
                        nn.graphMask = seeker.graphMask;
                        for (int i = 0; i < st.PathPoints.Count; i++)
                        {
                            var hit = AstarPath.active.GetNearest(st.PathPoints[i], nn);
                            if (hit.node != null) st.PathPoints[i] = hit.position;
                        }
                    }
                }
                if (st.PathPoints.Count == 0) { SteerLog.Once($"path {st.Entity?.Id}", $"{st.Entity?.Id}: movement path {m.Path} has no points"); st.PathDone = true; }
                else
                {
                    while (st.PathIndex < st.PathPoints.Count && Flat(pos, st.PathPoints[st.PathIndex]) <= Arrive)
                    {
                        LogicEvents.Raise("arrive", st.Entity, null, st.PathIndex, pos.ToVec());
                        st.PathIndex++;
                    }
                    if (st.PathIndex >= st.PathPoints.Count)
                    {
                        st.PathDone = true;
                        LogicEvents.Raise("end", st.Entity, null, st.PathPoints.Count - 1, pos.ToVec());
                        if (m.Then == "destroy") { LogicSteeringManager.DestroySilently(st.Entity, st.Go); dest = pos; return true; }
                        if (m.Then == "hold") st.HoldPoint = pos;
                    }
                    else { dest = st.PathPoints[st.PathIndex]; return true; }
                }
            }

            // 6. hold (mode, or a finished path with then: hold)
            if (m.Mode == "hold" || (m.Mode == "path" && st.PathDone && m.Then == "hold"))
            {
                st.HoldPoint ??= pos;
                dest = st.HoldPoint.Value;
                walkingHome = Flat(pos, dest) > 0.5f;
                return true;
            }

            // 7. targeting (player units do this in FindTargetPosition)
            if (!playerUnit && st.Targeting != null)
            {
                bool handled = TargetChooser.Choose(st, pos, float.PositiveInfinity, 0, 1, one);
                if (handled && one.Count > 0)
                {
                    target = one[0];
                    dest = ClosestPoint(target, pos);
                    walkingHome = false;
                    return true;
                }
                if (!handled) return false;
                // handled, nothing to target: wander or home
                if (Wander(st, pos, now, out dest)) return true;
                dest = Home(st) + (st.Enemy != null ? E_homeOffset(st.Enemy) : Vector3.zero);
                return true;
            }
            return false;
        }

        /// <summary>follow: the leader expression's entity (re-read 10 times a second); null when none or dead.</summary>
        public static LogicEntity FollowLeader(SteerState st, double now)
        {
            var m = st.Move;
            if (m.Leader == null) return null;
            if (now - st.FollowEvalAt >= 0.1 || (st.FollowLeader != null && !st.FollowLeader.Alive))
            {
                st.FollowEvalAt = now;
                LogicEntity l = null;
                try
                {
                    var v = SteerExpr.Expr(m.Leader)(new LogicContext { World = LogicWorld.Current, Self = st.Entity, Subject = st.Entity });
                    l = v.Kind == ValueKind.Entity ? v.AsEntity : v.Kind == ValueKind.Set && v.AsSet.Count > 0 ? v.AsSet[0] : null;
                }
                catch (LogicError ex) { SteerLog.Once($"leader {st.Entity?.Id}", $"{st.Entity?.Id}: movement leader: {ex.Message}"); }
                if (l == st.Entity || l != null && !l.Alive) l = null;
                if (l != st.FollowLeader) { st.FollowLeader = l; Repath(st); }
            }
            return st.FollowLeader != null && st.FollowLeader.Alive ? st.FollowLeader : null;
        }

        /// <summary>follow: where the follower belongs by its leader: `offset` in the leader's frame, or `spacing`
        /// metres behind the leader along the line from the leader to it (a trail).</summary>
        public static Vector3 FollowSlot(SteerState st, LogicEntity leader, Vector3 pos)
        {
            var m = st.Move;
            var lp = leader.Position.ToVector3();
            Vector3 slot;
            if (m.Offset != null) slot = lp + Quaternion.Euler(0f, Heading.Yaw(leader), 0f) * new Vector3(m.Offset[0], 0f, m.Offset[1]);
            else
            {
                // A trail: along the line from the leader to the follower (so a chain snakes round corners), but
                // always behind the leader: a follower ahead of it (it turned, or it was spawned in front) walks round.
                var back = -(Quaternion.Euler(0f, Heading.Yaw(leader), 0f) * Vector3.forward);
                var d = pos - lp; d.y = 0;
                if (d.sqrMagnitude < 0.01f || Vector3.Dot(d.normalized, back) < 0f) d = back;
                slot = lp + d.normalized * m.Spacing;
            }
            return Ground(st, slot);
        }

        /// <summary>When the game found nothing (walking home), wander instead if the block says so.</summary>
        public static bool Wander(SteerState st, Vector3 pos, double now, out Vector3 dest)
        {
            dest = default;
            var m = st.Move;
            if (m.WanderRadius <= 0) return false;
            if (st.WanderPoint is not Vector3 w || Flat(pos, w) <= Arrive || now > st.WanderUntil)
            {
                var rng = LogicWorld.Current.Random;
                var home = Home(st);
                double a = rng.Range(0, Mathf.PI * 2), r = m.WanderRadius * System.Math.Sqrt(rng.NextDouble());
                w = Ground(st, home + new Vector3((float)(System.Math.Cos(a) * r), 0, (float)(System.Math.Sin(a) * r)));
                st.WanderPoint = w;
                st.WanderUntil = now + 4 + rng.Range(0, 3);
            }
            dest = w;
            return true;
        }

        /// <summary>10 Hz: kite and flee triggers, timed overlays.</summary>
        public static void Poll(SteerState st, double now)
        {
            int before = st.MovementOverlays.Count;
            st.MovementOverlays.RemoveAll(o => o.until <= now);
            if (st.MovementOverlays.Count != before) { st.MovementChanged(); Repath(st); }
            var m = st.Move;
            if (m.KiteBelow > 0 && st.KiteUntil <= now)
            {
                var foe = Nearest(st, "foe", m.KiteBelow);
                if (foe != null) { st.KiteUntil = now + m.KiteFor; st.KiteFrom = foe; Repath(st); }
            }
            if (m.FleeWhen != null)
            {
                bool f;
                try { f = SteerExpr.Bool(SteerExpr.Expr(m.FleeWhen)(new LogicContext { World = LogicWorld.Current, Self = st.Entity, Subject = st.Entity })); }
                catch (LogicError ex) { SteerLog.Once($"flee {st.Entity?.Id}", ex.Message); f = false; }
                if (f != st.Fleeing) { st.Fleeing = f; Repath(st); }
            }
            else st.Fleeing = false;
        }

        public static LogicEntity Nearest(SteerState st, string filter, float range)
        {
            var rules = new List<TargetRule> { new() { Filter = filter, Range = range } };
            return TargetChooser.Choose(st, st.Go.transform.position, range, 0, 1, one, rules) && one.Count > 0 ? LogicGame.EntityOf(one[0].gameObject) : null;
        }

        private static Vector3 Away(SteerState st, Vector3 pos, Vector3 from, float dist)
        {
            var d = pos - from; d.y = 0;
            if (d.sqrMagnitude < 0.01f) d = st.Go.transform.forward;
            return Ground(st, pos + d.normalized * dist);
        }

        public static Vector3 ClosestPoint(TaggedObject t, Vector3 from) =>
            t.colliderForBigOjectsToMeasureDistance != null ? t.colliderForBigOjectsToMeasureDistance.ClosestPoint(from) : t.transform.position;

        public static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        // ------------------------------------------------------------------ direct mode

        /// <summary>Straight-line movement through the unit's RVO (no navmesh), replacing the game's Update.</summary>
        public static void DirectUpdate(SteerState st, float speed, float keepDistance, Pathfinding.RVO.RVOController rvo, float dt)
        {
            st.DirectRepath -= dt;
            if (st.DirectRepath <= 0)
            {
                st.DirectRepath = 0.25f;
                if (Destination(st, st.Player != null, out var dest, out var tgt, out bool home))
                {
                    st.DirectDest = dest; st.DirectTarget = tgt; st.DirectWalkingHome = home;
                }
                else
                {
                    // the game's own priorities: closest match of its movement list
                    var prios = st.Enemy != null ? st.Enemy.targetPriorities : st.Player?.targetPriorities;
                    var pos0 = st.Go.transform.position;
                    TaggedObject found = null; Vector3 at = Home(st);
                    if (prios != null)
                        foreach (var p in prios) { found = p.FindTaggedObjectCloseToHome(pos0, Home(st), 1e6f, out at); if (found != null) break; }
                    st.DirectTarget = found; st.DirectDest = found != null ? at : Home(st); st.DirectWalkingHome = found == null;
                }
            }
            var tr = st.Go.transform;
            var pos = tr.position;
            if (st.DirectTarget is not null && !st.DirectTarget) { st.DirectTarget = null; st.DirectRepath = 0; }   // destroyed target
            var goal = st.DirectTarget != null ? ClosestPoint(st.DirectTarget, pos) : st.DirectDest;
            var flat = goal - pos; flat.y = 0;
            float dist = flat.magnitude;
            bool flyer = st.Entity?.Body?.HasTag("Flying") ?? false;
            if (rvo == null) return;
            if (dist < keepDistance && !st.DirectWalkingHome || dist < 0.05f)
            {
                rvo.SetTarget(pos, 0, speed * 1.15f, pos);
                return;
            }
            var target = new Vector3(goal.x, flyer ? pos.y : goal.y, goal.z);
            rvo.SetTarget(target, Mathf.Min(speed, dist / Mathf.Max(dt, 1e-4f)), speed * 1.15f, target);
            var delta = rvo.CalculateMovementDelta(dt);
            if (!flyer) delta.y = Mathf.Clamp(goal.y - pos.y, -speed * dt, speed * dt);
            tr.position = pos + delta;
        }
    }
}
