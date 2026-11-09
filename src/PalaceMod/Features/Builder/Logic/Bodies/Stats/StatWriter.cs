using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

/*
    Stats → the game's own fields, for every kind of game object that has them (vanilla and Builder units, towers and
    other buildings, the king). Each field's base value is remembered the first time a modifier touches it, so
    modifiers never compound into the base; when the last modifier goes, the base is written back.

    Base values per component (not per entity), so components added later (Steering's priorities, a second attack)
    get their own base the first time they're written.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class StatWriter
    {
        private sealed class Box { public float V; }
        private static readonly ConditionalWeakTable<object, Dictionary<string, Box>> bases = new();

        /// <summary>The remembered base of a field (remembering <paramref name="current"/> the first time).</summary>
        private static float Base(object owner, string field, float current)
        {
            var d = bases.GetOrCreateValue(owner);
            if (!d.TryGetValue(field, out var b)) d[field] = b = new Box { V = current };
            return b.V;
        }

        private static bool HasBase(object owner, string field, out float v)
        {
            v = 0;
            if (owner == null || !bases.TryGetValue(owner, out var d) || !d.TryGetValue(field, out var b)) return false;
            v = b.V;
            return true;
        }

        /// <summary>The base (unmodified) value: remembered, or the live field when nothing has touched it yet.</summary>
        private static float Peek(object owner, string field, float current) => HasBase(owner, field, out var v) ? v : current;

        // ------------------------------------------------------------------ what an object has

        internal sealed class Parts
        {
            public GameObject Go;
            public Hp Hp;
            public AutoAttack[] Attacks;
            public PathfindMovementEnemy EnemyMove;
            public PathfindMovementPlayerunit UnitMove;
            public PlayerMovement King;
            public ManualAttack[] KingAttacks;
            public MovementFollowPath FollowPath;
            public BuildSlot Slot;
        }

        public static Parts Of(GameObject go)
        {
            if (go == null) return null;
            var p = new Parts
            {
                Go = go,
                Hp = go.GetComponentInChildren<Hp>(true),
                Attacks = go.GetComponentsInChildren<AutoAttack>(true),
                EnemyMove = go.GetComponentInChildren<PathfindMovementEnemy>(true),
                UnitMove = go.GetComponentInChildren<PathfindMovementPlayerunit>(true),
                King = go.GetComponentInChildren<PlayerMovement>(true),
                KingAttacks = go.GetComponentsInChildren<ManualAttack>(true),
                FollowPath = go.GetComponentInChildren<MovementFollowPath>(true),
            };
            // A building: its slot (income), and the tower's attack, which may sit beside the Hp rather than under it.
            if (p.Hp != null && p.Hp.GetComponent<TaggedObject>() is { } t && t.Tags.Contains(TagManager.ETag.Building))
            {
                p.Slot = go.GetComponentInParent<BuildSlot>();
                if (p.Attacks.Length == 0 && p.Slot != null) p.Attacks = p.Slot.GetComponentsInChildren<AutoAttack>(false);
            }
            return p;
        }

        // ------------------------------------------------------------------ base values (the stat with no modifiers)

        /// <summary>The unmodified value of <paramref name="stat"/> on these parts, or NaN when it has no such stat.</summary>
        public static double BaseOf(Parts p, string stat)
        {
            if (p == null) return double.NaN;
            switch (stat)
            {
                case "maxHp": return p.Hp != null ? Peek(p.Hp, "maxHp", p.Hp.maxHp) : double.NaN;
                case "hp": return p.Hp != null ? p.Hp.HpValue : double.NaN;
                case "moveSpeed":
                    if (p.EnemyMove != null) return Peek(p.EnemyMove, "movementSpeed", p.EnemyMove.movementSpeed);
                    if (p.UnitMove != null) return Peek(p.UnitMove, "movementSpeed", p.UnitMove.movementSpeed);
                    if (p.King != null) return Peek(p.King, "speed", p.King.speed);
                    if (p.FollowPath != null) return Peek(p.FollowPath, "speed", p.FollowPath.speed);
                    return double.NaN;
                case "attackCooldown":
                    if (p.Attacks.Length > 0) return Peek(p.Attacks[0], "cooldownDuration", p.Attacks[0].cooldownDuration);
                    if (p.KingAttacks.Length > 0) return Peek(p.KingAttacks[0], "cooldownTime", p.KingAttacks[0].cooldownTime);
                    return double.NaN;
                case "attackRange":
                {
                    if (p.Attacks.Length == 0) return double.NaN;
                    float r = 0;
                    foreach (var tp in p.Attacks[0].targetPriorities) if (tp != null) r = Mathf.Max(r, Peek(tp, "range", tp.range));
                    return r;
                }
                case "keepDistance":
                    if (p.EnemyMove != null) return Peek(p.EnemyMove, "keepDistanceOf", p.EnemyMove.keepDistanceOf);
                    if (p.UnitMove != null) return Peek(p.UnitMove, "keepDistanceOf", p.UnitMove.keepDistanceOf);
                    return double.NaN;
                case "aggroTime":
                    if (p.EnemyMove != null) return Peek(p.EnemyMove, "agroTime", p.EnemyMove.agroTimeWhenAttackedByPlayer);
                    if (p.UnitMove != null) return Peek(p.UnitMove, "agroTime", p.UnitMove.agroTimeWhenAttackedByPlayer);
                    return double.NaN;
                case "income": return p.Slot != null ? p.Slot.GoldIncome : double.NaN;
                default: return double.NaN;
            }
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Write the stats to the parts' fields. <paramref name="get"/> gives a stat's modified value (NaN = none);
        /// only stats that <paramref name="touched"/> (ever had a modifier on this entity, "frozen" for
        /// stunned/rooted) are written, so fields nobody modified stay the game's (and other parts') to change.
        /// <paramref name="frozen"/> (stunned/rooted) stops movement; disarmed is the attack patches' job.
        /// </summary>
        public static void Write(Parts p, System.Func<string, double> get, System.Func<string, bool> touched, bool frozen)
        {
            if (p == null || p.Go == null) return;
            bool tsT = touched("timeScale");
            double ts = get("timeScale");
            if (double.IsNaN(ts) || ts < 0) ts = 1;

            if (p.Hp != null && touched("maxHp"))
            {
                float baseMax = Base(p.Hp, "maxHp", p.Hp.maxHp);
                double max = get("maxHp");
                float newMax = double.IsNaN(max) ? baseMax : Mathf.Max(1f, (float)max);
                if (!Mathf.Approximately(newMax, p.Hp.maxHp))
                {
                    // Keep the hp fraction (a +50 % max hp buff heals by the same share; its end doesn't kill).
                    float frac = p.Hp.maxHp > 0 ? p.Hp.HpValue / p.Hp.maxHp : 1f;
                    p.Hp.maxHp = newMax;
                    if (p.Hp.HpValue > 0) p.Hp.SetHpTo(Mathf.Clamp(frac * newMax, 1f, newMax));
                }
            }

            if (touched("moveSpeed") || tsT || touched("frozen")) WriteMove(p, Ratio(get, "moveSpeed", BaseOf(p, "moveSpeed")) * ts, frozen);
            if (touched("keepDistance")) WriteKeep(p, Ratio(get, "keepDistance", BaseOf(p, "keepDistance")));
            if (touched("aggroTime")) WriteAggro(p, Ratio(get, "aggroTime", BaseOf(p, "aggroTime")));

            if (p.KingAttacks.Length > 0 && p.Attacks.Length == 0 && (touched("attackCooldown") || tsT))
            {
                double kcd = Ratio(get, "attackCooldown", BaseOf(p, "attackCooldown")) / Mathf.Max(0.01f, (float)ts);
                foreach (var m in p.KingAttacks)
                    if (m != null) m.cooldownTime = Mathf.Max(0.02f, Base(m, "cooldownTime", m.cooldownTime) * (float)kcd);
            }

            if (p.Attacks.Length > 0)
            {
                bool cdT = touched("attackCooldown") || tsT, psT = touched("projectileSpeed") || tsT, rT = touched("attackRange");
                double cd = Ratio(get, "attackCooldown", BaseOf(p, "attackCooldown")) / Mathf.Max(0.01f, (float)ts);
                double range = Ratio(get, "attackRange", BaseOf(p, "attackRange"));
                double pspeed = get("projectileSpeed");
                if (double.IsNaN(pspeed)) pspeed = 1;
                foreach (var a in p.Attacks)
                {
                    if (a == null) continue;
                    if (cdT) a.cooldownDuration = Mathf.Max(0.02f, Base(a, "cooldownDuration", a.cooldownDuration) * (float)cd);
                    if (psT) a.ProjectileSpeedMultiplyer = Base(a, "projectileSpeedMultiplyer", a.ProjectileSpeedMultiplyer) * (float)(pspeed * ts);
                    if (rT)
                        foreach (var tp in a.targetPriorities)
                            if (tp != null) tp.range = Base(tp, "range", tp.range) * (float)range;
                }
            }

            double scale = get("scale");
            if (touched("scale") && !double.IsNaN(scale))
            {
                var t = p.Go.transform;
                var b = new Vector3(Base(t, "sx", t.localScale.x), Base(t, "sy", t.localScale.y), Base(t, "sz", t.localScale.z));
                t.localScale = b * Mathf.Max(0.01f, (float)scale);
            }
        }

        private static void WriteMove(Parts p, double moveRatio, bool frozen)
        {
            if (frozen) moveRatio = 0;
            if (p.EnemyMove != null) p.EnemyMove.movementSpeed = Base(p.EnemyMove, "movementSpeed", p.EnemyMove.movementSpeed) * (float)moveRatio;
            if (p.UnitMove != null) p.UnitMove.movementSpeed = Base(p.UnitMove, "movementSpeed", p.UnitMove.movementSpeed) * (float)moveRatio;
            if (p.FollowPath != null) p.FollowPath.speed = Base(p.FollowPath, "speed", p.FollowPath.speed) * (float)moveRatio;
            if (p.King != null)
            {
                var k = p.King;
                k.speed = Base(k, "speed", k.speed) * (float)moveRatio;
                k.speedDuringDay = Base(k, "speedDuringDay", k.speedDuringDay) * (float)moveRatio;
                k.sprintSpeed = Base(k, "sprintSpeed", k.sprintSpeed) * (float)moveRatio;
                k.sprintSpeedDuringDay = Base(k, "sprintSpeedDuringDay", k.sprintSpeedDuringDay) * (float)moveRatio;
            }

        }

        private static void WriteKeep(Parts p, double keep)
        {
            if (p.EnemyMove != null) p.EnemyMove.keepDistanceOf = Base(p.EnemyMove, "keepDistanceOf", p.EnemyMove.keepDistanceOf) * (float)keep;
            if (p.UnitMove != null) p.UnitMove.keepDistanceOf = Base(p.UnitMove, "keepDistanceOf", p.UnitMove.keepDistanceOf) * (float)keep;
        }

        private static void WriteAggro(Parts p, double aggro)
        {
            if (p.EnemyMove != null) p.EnemyMove.agroTimeWhenAttackedByPlayer = Base(p.EnemyMove, "agroTime", p.EnemyMove.agroTimeWhenAttackedByPlayer) * (float)aggro;
            if (p.UnitMove != null) p.UnitMove.agroTimeWhenAttackedByPlayer = Base(p.UnitMove, "agroTime", p.UnitMove.agroTimeWhenAttackedByPlayer) * (float)aggro;
        }

        /// <summary>modified / base, 1 when either is missing or the base is 0 (a 0 base can't be scaled).</summary>
        private static double Ratio(System.Func<string, double> get, string stat, double baseValue)
        {
            double v = get(stat);
            if (double.IsNaN(v) || double.IsNaN(baseValue)) return 1;
            if (Mathf.Approximately((float)baseValue, 0f)) return 1;
            return Mathf.Max(0f, (float)(v / baseValue));
        }
    }
}
