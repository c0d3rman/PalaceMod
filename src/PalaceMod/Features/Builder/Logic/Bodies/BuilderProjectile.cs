using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `projectile` (docs/builder-engine.md, "Components"): any entity can fly. `launch` sets its owner, target and
    start. Flight is integrated per frame in game time scaled by the body's `timeScale`, plus drifts:

    - homing 0: straight at where the target was; 1: always re-aims (the game's arrows); between: the share of the
      angle to the target closed per second.
    - arc: a parabola over the planned distance (the game's parabula factor); gravity: a ballistic shot to the
      aim point (overrides arc).
    - Hits: anything matching `hits` within `hitRadius` of the flight segment (each thing once); `pierce` extra
      targets (-1 = all); the ground per `onGround` (impact | stick | bounce | pass); `range` metres, then `expire`.
    - Each hit raises `impact` (other = what it hit; none for the ground; event.pos) and runs the launch's `onImpact`.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class BuilderProjectile : MonoBehaviour
    {
        public LogicEntity Entity;
        // settings
        public float Speed = 12f, Accel, Homing = 1f, Arc, Gravity, HitRadius = 1f, Range = 60f;
        public Vector3 Spin;
        public string Face = "travel", Hits = "foe", OnGround = "impact";
        public int Pierce;
        // flight
        public LogicEntity Target;
        public Vector3 AimPoint;
        private Vector3 linear, dir, last;
        private Vector3 start;
        private float travelled, plannedDistance, verticalSpeed, height;
        private bool launched, done, stuck;
        private int hitsLeft;
        private readonly HashSet<LogicEntity> struck = [];
        private readonly List<LogicEntity> candidates = [];
        /// <summary>The launching rule's `onImpact` (run deferred in its context with other = the hit).</summary>
        public System.Action<LogicEntity, Vector3> OnImpact;

        public static readonly HashSet<BuilderProjectile> Live = [];
        /// <summary>The last impacts (dev: `logic-l2 status`).</summary>
        public static readonly Queue<string> LastImpacts = new();

        public void Configure(LogicEntity e, JObject p)
        {
            Entity = e;
            Apply(p);
        }

        /// <summary>Field values from JSON (the template's, then a launch's overrides).</summary>
        public void Apply(JObject p)
        {
            if (p == null) return;
            Speed = F(p, "speed", Speed);
            Accel = F(p, "accel", Accel);
            Homing = Mathf.Clamp01(F(p, "homing", Homing));
            Arc = F(p, "arc", Arc);
            Gravity = F(p, "gravity", Gravity);
            HitRadius = F(p, "hitRadius", HitRadius);
            Range = F(p, "range", Range);
            Pierce = p["pierce"] != null ? (int)p["pierce"] : Pierce;
            if (p["spin"] is JArray s && s.Count == 3) Spin = new Vector3((float)s[0], (float)s[1], (float)s[2]);
            Face = (string)p["face"] ?? Face;
            Hits = (string)p["hits"] ?? Hits;
            OnGround = (string)p["onGround"] ?? OnGround;
        }

        private static float F(JObject p, string k, float d) => p[k] is JToken t && t.Type is JTokenType.Integer or JTokenType.Float ? (float)t : d;

        public void Launch(Vector3 from, LogicEntity target, Vector3? point)
        {
            Target = target;
            start = linear = last = transform.position = from;
            AimPoint = point ?? (target != null ? AimOf(target) : from + transform.forward * 10f);
            plannedDistance = Mathf.Max(0.1f, Vector3.Distance(from, AimPoint));
            dir = (AimPoint - from).sqrMagnitude > 1e-6f ? (AimPoint - from).normalized : transform.forward;
            if (Gravity > 0f)
            {
                // Ballistic: horizontal at Speed, vertical solved to land on the aim point.
                var flat = new Vector3(AimPoint.x - from.x, 0f, AimPoint.z - from.z);
                float t = Mathf.Max(0.05f, flat.magnitude / Mathf.Max(0.1f, Speed));
                verticalSpeed = (AimPoint.y - from.y + 0.5f * Gravity * t * t) / t;
                dir = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            }
            hitsLeft = Pierce < 0 ? int.MaxValue : Pierce + 1;
            launched = true;
            Live.Add(this);
        }

        private static Vector3 AimOf(LogicEntity t)
        {
            var go = t.Go();
            if (go == null) return t.Position.ToVector3();
            var hp = go.GetComponentInChildren<Hp>();
            return go.transform.position + Vector3.up * (hp != null ? hp.hitFeedbackHeight : 0.5f);
        }

        private void OnDisable() => Live.Remove(this);

        private void Update()
        {
            if (!launched || done || Entity == null) return;
            float dt = Time.deltaTime * BodyTime.Scale(Entity);
            if (dt <= 0f) return;
            if (stuck) return;
            Speed = Mathf.Max(0f, Speed + Accel * dt);
            float speedStat = Stat("projectileSpeed", 1f);
            float step = Speed * speedStat * dt;

            bool targetAlive = Target != null && Target.Alive;
            if (targetAlive) AimPoint = AimOf(Target);

            Vector3 next;
            if (Gravity > 0f)
            {
                var flat = new Vector3(AimPoint.x - linear.x, 0f, AimPoint.z - linear.z);
                if (Homing > 0f && targetAlive && flat.sqrMagnitude > 1e-4f) dir = Turn(dir, flat.normalized, dt);
                verticalSpeed -= Gravity * dt;
                linear += dir * step + Vector3.up * verticalSpeed * dt;
                next = linear;
            }
            else
            {
                var to = AimPoint - linear;
                if (to.sqrMagnitude > 1e-6f && (targetAlive || Homing >= 1f)) dir = Turn(dir, to.normalized, dt);
                linear += dir * step;
                float s = Mathf.Clamp01((travelled + step) / Mathf.Max(plannedDistance, travelled + Vector3.Distance(linear, AimPoint)));
                height = Arc > 0f ? 4f * Arc * plannedDistance * s * (1f - s) : 0f;
                next = linear + Vector3.up * height;
            }
            var drift = Drifts.Velocity(Entity) * dt;
            if (drift != Vector3.zero) { linear += drift; next += drift; }

            travelled += step;
            transform.position = next;
            Orient(next, dt);
            CheckHits(last, next);
            if (done) return;

            // The aim point reached with nothing to hit there (a point, or a target that's gone): the ground.
            if (Gravity <= 0f && (AimPoint - linear).sqrMagnitude <= step * step && !(targetAlive && BodyFilter.Matches(Hits, Entity.Owner ?? Entity, Target)))
                Ground(next);
            else if (BodyGround.Below(next, out float groundY) && next.y < groundY && (Gravity > 0f || height > 0f || next.y < groundY - 0.2f))
                Ground(new Vector3(next.x, groundY, next.z));
            if (!done && travelled >= Range) { done = true; BodyEnd.Expire(Entity, gameObject); }
            last = transform.position;
        }

        private float Stat(string stat, float fallback)
        {
            if (LogicServices.Stats == null) return fallback;
            double v = LogicServices.Stats.Get(Entity, stat);
            return double.IsNaN(v) ? fallback : (float)v;
        }

        private Vector3 Turn(Vector3 current, Vector3 desired, float dt)
        {
            if (Homing >= 1f) return desired;
            float t = 1f - Mathf.Pow(1f - Homing, dt);
            return Vector3.Slerp(current, desired, t).normalized;
        }

        private void Orient(Vector3 pos, float dt)
        {
            switch (Face)
            {
                case "travel":
                    var v = pos - last;
                    if (v.sqrMagnitude > 1e-8f) transform.rotation = Quaternion.LookRotation(v, Vector3.up);
                    break;
                case "spin":
                    break;
                default:
                    return;
            }
            if (Spin != Vector3.zero)
            {
                var visual = transform.Find(BodyVisual.ChildName);
                if (visual != null) visual.Rotate(Spin * dt, Space.Self);
            }
        }

        private void CheckHits(Vector3 a, Vector3 b)
        {
            var mid = (a + b) / 2f;
            float reach = HitRadius + Vector3.Distance(a, b) / 2f + 2f;
            BodySpatial.Query(mid, reach, candidates);
            var self = Entity.Owner ?? Entity;
            foreach (var c in candidates)
            {
                if (done) return;
                if (c == Entity || c == Entity.Owner || struck.Contains(c) || !c.Alive) continue;
                if (!BodyFilter.Matches(Hits, self, c)) continue;
                var p = AimOf(c);
                float r = HitRadius + BodyRadius(c);
                if (DistToSegment(p, a, b) > r) continue;
                struck.Add(c);
                Impact(c, p);
            }
        }

        private static float BodyRadius(LogicEntity c)
        {
            var go = c.Go();
            if (go == null) return 0.3f;
            if (go.GetComponent<BuilderBody>() is { } bb) return bb.Box is Vector2 box ? Mathf.Max(box.x, box.y) / 2f : bb.Radius;
            var t = go.GetComponent<TaggedObject>();
            if (t != null && t.colliderForBigOjectsToMeasureDistance != null) return t.colliderForBigOjectsToMeasureDistance.bounds.extents.x;
            return 0.5f;
        }

        private static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        private void Impact(LogicEntity other, Vector3 at)
        {
            Log.For<LogicBodiesManager>().Debug($"{Entity.Id} impact {other?.Id ?? "ground"} at {at} after {travelled:0.0} m");
            LastImpacts.Enqueue($"{Entity.Id}->{other?.Id ?? "ground"}@({at.x:0.#},{at.y:0.#},{at.z:0.#})");
            while (LastImpacts.Count > 12) LastImpacts.Dequeue();
            BodyDev.Raise("impact", Entity, other, 0, at.ToVec());
            OnImpact?.Invoke(other, at);
            if (--hitsLeft <= 0) Finish();
        }

        private void Ground(Vector3 at)
        {
            switch (OnGround)
            {
                case "pass":
                    if (Gravity <= 0f && Target == null) { Impact(null, at); Finish(); }
                    return;
                case "stick":
                    BodyDev.Raise("impact", Entity, null, 0, at.ToVec());
                    OnImpact?.Invoke(null, at);
                    transform.position = at;
                    stuck = true;
                    return;
                case "bounce":
                    if (Gravity > 0f && verticalSpeed < -1f)
                    {
                        BodyDev.Raise("impact", Entity, null, 0, at.ToVec());
                        OnImpact?.Invoke(null, at);
                        verticalSpeed = -verticalSpeed * 0.5f;
                        linear.y = at.y + 0.01f;
                        return;
                    }
                    Impact(null, at);
                    Finish();
                    return;
                default:
                    Impact(null, at);
                    Finish();
                    return;
            }
        }

        private void Finish()
        {
            if (done) return;
            done = true;
            Live.Remove(this);
            BodyEnd.Retire(gameObject);
        }

        // ---- redirection (shared with vanilla projectiles' control) -----------------------------------------------

        /// <summary>New target or point (taunt on a projectile, `redirect`): re-plans the arc from here.</summary>
        public void Redirect(LogicEntity target, Vector3? point)
        {
            Target = target;
            AimPoint = point ?? (target != null ? AimOf(target) : AimPoint);
            start = linear;
            travelled = 0f;
            plannedDistance = Mathf.Max(0.1f, Vector3.Distance(linear, AimPoint));
            struck.Clear();
        }
    }

    internal static class BodyGround
    {
        private static readonly int mask = (1 << 6) | (1 << 4);   // Ground, Water

        /// <summary>The ground height under a point (a ray on the ground layers).</summary>
        public static bool Below(Vector3 p, out float y)
        {
            if (Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 200f, mask, QueryTriggerInteraction.Ignore)) { y = hit.point.y; return true; }
            y = 0f;
            return false;
        }
    }
}
