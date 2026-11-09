using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/*
    `bump` (docs/builder-engine.md, "Events"): a knockback stopped short by something solid. Every knockback the game
    starts (KnockbackBehavior.ApplyKnockback: the `push` action, and the game's own knockback weapons) is watched
    until its flight ends. The game flies the unit along the navmesh and stops it where the path leaves walkable
    ground (a wall, a building, a solid Builder body's cut, a cliff, the map's edge), and lands it on the nearest
    walkable point of its target. If it ends more than max(0.5 m, 15 %) short of where the shove aimed, `bump` is raised
    on it: `other` = the first building, wall or Builder body ahead of it (a collider within the distance left plus a
    metre), event.value = the distance left, event.pos = where it stopped.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class Bumps
    {
        private sealed class Flight
        {
            public KnockbackBehavior Kb;
            public GameObject Go;
            public Vector3 Start, Dir;
            public float Force, Began;
        }

        private static readonly List<Flight> flights = [];

        private static readonly AccessTools.FieldRef<KnockbackBehavior, bool> knockingBack = AccessTools.FieldRefAccess<KnockbackBehavior, bool>("knockingBack");
        private static readonly AccessTools.FieldRef<KnockbackBehavior, AnimationCurve> forceByDistance = AccessTools.FieldRefAccess<KnockbackBehavior, AnimationCurve>("forceByDistance");
        private static readonly System.Reflection.FieldInfo phaseField = AccessTools.Field(typeof(KnockbackBehavior), "phase");

        public static void Reset() => flights.Clear();

        private static bool Wanted(LogicWorld w) => w != null && (w.HasListeners("bump") || w.HasOfListeners("bump"));

        public static bool Busy(KnockbackBehavior kb) => kb != null && knockingBack(kb);

        /// <summary>After ApplyKnockback started a flight (it wasn't flying before).</summary>
        public static void Started(KnockbackBehavior kb, Vector3 start, Vector3 direction, float sqrDistance, float maxRange, float maxForce, AnimationCurve curve)
        {
            if (!Wanted(LogicWorld.Current) || kb == null || !knockingBack(kb) || maxForce <= 0) return;
            var dir = new Vector3(direction.x, 0, direction.z);
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            float t = Mathf.Clamp01(Mathf.Sqrt(sqrDistance) / Mathf.Max(maxRange, 1e-4f));
            float force = maxForce * ((curve ?? forceByDistance(kb))?.Evaluate(t) ?? 1f);
            if (force < 0.3f) return;
            flights.RemoveAll(f => f.Kb == kb);
            flights.Add(new Flight { Kb = kb, Go = kb.gameObject, Start = start, Dir = dir, Force = force, Began = Time.time });
        }

        /// <summary>Every frame: flights that ended are measured.</summary>
        public static void Update()
        {
            if (flights.Count == 0) return;
            var w = LogicWorld.Current;
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                var f = flights[i];
                if (f.Kb == null || f.Go == null || w == null) { flights.RemoveAt(i); continue; }
                int phase = System.Convert.ToInt32(phaseField.GetValue(f.Kb));
                // 1 = Arc: still flying (the Drop that follows a stop keeps x/z; Stagger and later have landed)
                if (phase == 1 && knockingBack(f.Kb) && Time.time - f.Began < 3f) continue;
                flights.RemoveAt(i);
                var pos = f.Go.transform.position;
                float along = Vector3.Dot(pos - f.Start, f.Dir);
                float left = f.Force - along;
                if (left < Mathf.Max(0.5f, 0.15f * f.Force)) continue;
                var e = LogicGame.EntityOf(f.Go);
                if (e == null || !e.Alive) continue;
                w.Raise("bump", e, Obstacle(f.Go, pos, f.Dir, left), left, pos.ToVec(), null);
            }
        }

        private static readonly RaycastHit[] hits = new RaycastHit[16];

        /// <summary>The first building, wall or Builder body ahead (not a unit), or null.</summary>
        private static LogicEntity Obstacle(GameObject self, Vector3 pos, Vector3 dir, float left)
        {
            var origin = pos + Vector3.up * 0.6f - dir * 0.3f;
            int n = Physics.SphereCastNonAlloc(origin, 0.4f, dir, hits, Mathf.Min(left + 1.3f, 6f), ~0, QueryTriggerInteraction.Ignore);
            LogicEntity best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = hits[i].collider;
                if (c == null || c.transform.IsChildOf(self.transform)) continue;
                if (c.GetComponentInParent<PathfindMovement>() != null || c.GetComponentInParent<PlayerMovement>() != null) continue;   // units, the king
                bool tagged = c.GetComponentInParent<TaggedObject>() != null || c.GetComponentInParent<BuilderEntity>() != null;
                if (!tagged) continue;
                var e = LogicGame.EntityOf(c.gameObject);
                if (e == null || e.Kind is "unit" or "king" or "projectile" or "coin") continue;
                if (hits[i].distance < bestDist) { bestDist = hits[i].distance; best = e; }
            }
            return best;
        }
    }
}
