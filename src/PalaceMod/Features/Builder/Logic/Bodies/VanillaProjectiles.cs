using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

/*
    The game's own projectiles (AimbotProjectile: arrows, bolts, magic, boulders) as entities of kind "projectile",
    so areas, selectors, statuses and actions act on them like on Builder projectiles (docs/builder-engine.md,
    "Known limits", now lifted):
    - `timeScale` and `projectileSpeed` stats scale the flight (Update runs with its speed scaled);
    - drifts (`drift`, status drift mods) move it; a homing arrow still re-aims at its target, so wind bends its path
      and a strong one makes it miss once it's deflected;
    - Projectiles.Redirect sends it at another target (it then damages that target with the shooter's weapon) or a
      point (it lands there and hits nothing, like a missed shot);
    - `destroy` releases it like the game does.
    Entities are made lazily, the first time a query sees the projectile, and dropped when the game releases it
    (projectiles are pooled: the same object flies again as a new entity).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class VanillaProjectiles
    {
        internal sealed class State
        {
            public LogicEntity Entity;
            public float SavedSpeed;
            public bool Slowed, Drifted;
        }

        /// <summary>Dev counters: projectiles whose flight a stat scaled / a drift moved, and redirects.</summary>
        public static int SlowedTotal, DriftedTotal, RedirectedTotal;

        private static readonly HashSet<AimbotProjectile> live = [];
        private static readonly ConditionalWeakTable<AimbotProjectile, State> states = new();

        private static readonly AccessTools.FieldRef<AimbotProjectile, float> speedRef = AccessTools.FieldRefAccess<AimbotProjectile, float>("speed");
        private static readonly AccessTools.FieldRef<AimbotProjectile, Vector3> linearRef = AccessTools.FieldRefAccess<AimbotProjectile, Vector3>("myLinearPosition");
        private static readonly AccessTools.FieldRef<AimbotProjectile, Vector3> spawnRef = AccessTools.FieldRefAccess<AimbotProjectile, Vector3>("spawnPosition");
        private static readonly AccessTools.FieldRef<AimbotProjectile, Transform> targetRef = AccessTools.FieldRefAccess<AimbotProjectile, Transform>("targetTransform");
        private static readonly AccessTools.FieldRef<AimbotProjectile, Hp> targetHpRef = AccessTools.FieldRefAccess<AimbotProjectile, Hp>("targetHp");
        private static readonly AccessTools.FieldRef<AimbotProjectile, TaggedObject> targetTaggedRef = AccessTools.FieldRefAccess<AimbotProjectile, TaggedObject>("targetTaggedObject");
        private static readonly AccessTools.FieldRef<AimbotProjectile, bool> targetFlyingRef = AccessTools.FieldRefAccess<AimbotProjectile, bool>("targetIsFlying");
        private static readonly AccessTools.FieldRef<AimbotProjectile, float> rangeRef = AccessTools.FieldRefAccess<AimbotProjectile, float>("remainingRange");
        private static readonly AccessTools.FieldRef<AimbotProjectile, TaggedObject> firedByRef = AccessTools.FieldRefAccess<AimbotProjectile, TaggedObject>("firedBy");

        public static int Count => live.Count;

        public static void Fired(AimbotProjectile p)
        {
            if (LogicWorld.Current == null) return;
            Released(p);    // a pooled object flying again is a new entity
            live.Add(p);
        }

        public static void Released(AimbotProjectile p)
        {
            live.Remove(p);
            if (states.TryGetValue(p, out var s))
            {
                if (s.Entity != null && !s.Entity.Destroyed) LogicWorld.Current?.Forget(s.Entity);
                Drifts.Forget(s.Entity);
                states.Remove(p);
            }
        }

        public static void Clear() { live.Clear(); }

        /// <summary>The entity for a vanilla projectile in flight (made on first use).</summary>
        public static LogicEntity EntityOf(AimbotProjectile p)
        {
            var world = LogicWorld.Current;
            if (world == null || p == null) return null;
            var s = states.GetValue(p, _ => new State());
            if (s.Entity != null && !s.Entity.Destroyed) return s.Entity;
            var by = firedByRef(p);
            // a tower fires with no TaggedObject; the hooks remember which attack fired it
            var shooter = by != null ? (Component)by : LogicManager.ShooterOf(p);
            var owner = shooter != null ? LogicGame.EntityOf(shooter.gameObject) : null;
            s.Entity = new LogicEntity
            {
                Template = "vanilla:" + p.name.Replace("(Clone)", "").Trim(),
                Kind = "projectile",
                Team = owner?.Team ?? "neutral",
                Owner = owner,
                Game = p.gameObject,
                Body = new ProjectileBody(p),
                Ext = p,
            };
            world.Add(s.Entity, vanilla: true);
            return s.Entity;
        }

        public static void Query(Vector3 center, float radius, List<LogicEntity> into)
        {
            if (live.Count == 0) return;
            List<AimbotProjectile> dead = null;
            foreach (var p in live)
            {
                if (p == null || !p.isActiveAndEnabled) { (dead ??= []).Add(p); continue; }
                var pos = p.transform.position;
                if (radius > 0 && new Vector2(pos.x - center.x, pos.z - center.z).sqrMagnitude > radius * radius) continue;
                into.Add(EntityOf(p));
            }
            if (dead != null) foreach (var p in dead) { if (p != null) Released(p); else live.Remove(p); }
        }

        // ---- per-frame control (AimbotProjectile.Update prefix/postfix) --------------------------------------------

        public static void BeforeUpdate(AimbotProjectile p)
        {
            if (!states.TryGetValue(p, out var s) || s.Entity == null) return;
            float scale = BodyTime.Scale(s.Entity) * Stat(s.Entity, "projectileSpeed");
            s.SavedSpeed = speedRef(p);
            speedRef(p) = s.SavedSpeed * scale;
            if (!s.Slowed && Mathf.Abs(scale - 1f) > 0.01f) { s.Slowed = true; SlowedTotal++; }
            if (Drifts.Any)
            {
                var d = Drifts.Velocity(s.Entity) * Time.deltaTime * BodyTime.Scale(s.Entity);
                if (d != Vector3.zero)
                {
                    if (!s.Drifted) { s.Drifted = true; DriftedTotal++; }
                    linearRef(p) += d;
                    // Without a target it flies at a remembered point: the wind carries that too.
                    if (targetRef(p) == null) p.rememberTarget += d;
                }
            }
        }

        public static void AfterUpdate(AimbotProjectile p)
        {
            if (!states.TryGetValue(p, out var s) || s.Entity == null) return;
            speedRef(p) = s.SavedSpeed;
        }

        private static float Stat(LogicEntity e, string stat)
        {
            if (LogicServices.Stats == null) return 1f;
            double v = LogicServices.Stats.Get(e, stat);
            return double.IsNaN(v) ? 1f : (float)v;
        }

        // ---- redirect / destroy --------------------------------------------------------------------------------

        public static bool Redirect(LogicEntity e, LogicEntity target, Vector3? point)
        {
            if (e?.Ext is not AimbotProjectile p || p == null || !p.isActiveAndEnabled) return false;
            var hp = target?.Go() != null ? target.Go().GetComponentInChildren<Hp>() : null;
            var start = linearRef(p);
            spawnRef(p) = start;
            if (hp != null)
            {
                targetRef(p) = hp.transform;
                targetHpRef(p) = hp;
                var tagged = hp.GetComponent<TaggedObject>();
                targetTaggedRef(p) = tagged;
                targetFlyingRef(p) = tagged != null && tagged.Tags.Contains(TagManager.ETag.Flying);
                p.rememberTarget = hp.transform.position + hp.hitFeedbackHeight * Vector3.up;
            }
            else
            {
                targetRef(p) = null;
                targetHpRef(p) = null;
                targetTaggedRef(p) = null;
                targetFlyingRef(p) = false;
                p.rememberTarget = point ?? target?.Position.ToVector3() ?? p.rememberTarget;
            }
            rangeRef(p) = Mathf.Max(rangeRef(p), Vector3.Distance(start, p.rememberTarget) + 0.5f);
            RedirectedTotal++;
            return true;
        }

        public static bool Destroy(LogicEntity e)
        {
            if (e?.Ext is not AimbotProjectile p || p == null) return false;
            Released(p);
            AccessTools.Method(typeof(AimbotProjectile), "DestroyProjectile").Invoke(p, null);
            return true;
        }

        private sealed class ProjectileBody(AimbotProjectile p) : IEntityBody
        {
            public bool Exists => p != null && p.isActiveAndEnabled;
            public bool Alive => Exists;
            public Vec Position => p != null ? p.transform.position.ToVec() : Vec.Zero;
            public float Facing => p != null ? p.transform.eulerAngles.y : 0f;
            public float Hp => 0f;
            public float MaxHp => 0f;
            public bool HasTag(string etag) => false;
            public string BuildingType => null;
            public int Level => 0;
        }
    }
}
