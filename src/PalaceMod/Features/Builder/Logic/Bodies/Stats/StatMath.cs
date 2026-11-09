using System;
using System.Collections.Generic;

/*
    Pure modifier and stacking math for stats and statuses (docs/builder-engine.md, "Statuses"). No Unity, no logic
    world: tests/ drives it directly. LogicStatsManager keeps one StatSheet per entity and writes the results to the
    game's fields.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    /// <summary>One stat change in force: from `setStat` (Count 1) or a status's `mods` (Count = its stacks).</summary>
    internal sealed class Mod
    {
        public string Stat;
        public double? Set, Add, Mult;
        public string Key;                    // same key on the same entity replaces the earlier one
        public double ExpireAt = double.PositiveInfinity;   // world time
        public string Until;                  // "night" (ends at the next dawn) | "day" (ends at the next dusk) | null
        public StatusState Owner;             // the status it belongs to (its stacks count), or null for setStat
        public object Source;                 // who applied it (a LogicEntity in game)
        /// <summary>Has `from` / `types` / `chance` / `when`: only damage evaluation applies it, never Get.</summary>
        public bool Conditional;
        public object Condition;              // the compiled condition (opaque here)
        public string[] Types;                // `types`: damage types it applies to (null = any)
        public double Chance = 1;
        public long Order;                    // insertion order: a later `set` wins

        /// <summary>How many times it applies: its status's stacks, else 1.</summary>
        public int Count => Owner?.Stacks ?? 1;
    }

    internal static class StatMath
    {
        /// <summary>
        /// The stats a status or `setStat` can change. Multiplier stats (base 1) apply to anything with a body;
        /// the others exist only where the game object has the matching field.
        /// </summary>
        public static readonly string[] Names =
        [
            "maxHp", "hp", "moveSpeed", "attackCooldown", "attackRange", "damageMult", "damageTaken", "projectileSpeed",
            "scale", "keepDistance", "aggroTime", "income", "timeScale",
        ];

        public static readonly HashSet<string> Multipliers = new(StringComparer.Ordinal) { "damageMult", "damageTaken", "projectileSpeed", "scale", "timeScale" };

        public static bool IsStat(string name) => Array.IndexOf(Names, name) >= 0;

        /// <summary>
        /// Combine modifiers over a base: the latest `set` replaces the base, then every `add` sums (n x add), then
        /// every `mult` multiplies (mult^n). <paramref name="count"/> gives a modifier's stack count (1 for setStat).
        /// </summary>
        public static double Combine(double baseValue, IEnumerable<Mod> mods, Func<Mod, int> count, Func<Mod, bool> include = null)
        {
            double value = baseValue, add = 0, mult = 1;
            long setOrder = long.MinValue;
            foreach (var m in mods)
            {
                if (include != null && !include(m)) continue;
                int n = Math.Max(0, count(m));
                if (n == 0) continue;
                if (m.Set.HasValue && m.Order >= setOrder) { value = m.Set.Value; setOrder = m.Order; }
                if (m.Add.HasValue) add += m.Add.Value * n;
                if (m.Mult.HasValue) mult *= Math.Pow(m.Mult.Value, n);
            }
            return (value + add) * mult;
        }

        /// <summary>Whether a conditional modifier's `types` match the damage's types (no `types` = any damage).</summary>
        public static bool TypesMatch(string[] wanted, ICollection<string> types)
        {
            if (wanted == null || wanted.Length == 0) return true;
            if (types == null) return false;
            foreach (var w in wanted) if (types.Contains(w)) return true;
            return false;
        }
    }

    /// <summary>One active status on one entity: its stacks and timers.</summary>
    internal sealed class StatusState
    {
        public string Id;
        public string Stacking = "refresh";   // refresh | add | extend | ignore
        public int MaxStacks = 1;
        public double Tick;                   // seconds between statusTick (0 = none)
        public double NextTick = double.PositiveInfinity;
        public double ExpireAt = double.PositiveInfinity;   // refresh / extend / ignore: one shared timer
        public readonly List<double> StackExpiry = [];     // add: one timer per stack
        public int Stacks;
        public object Source;                 // the applier (a LogicEntity in game)
        public string Until;                  // "night" | "day": also ends at the next dawn / dusk
        public object Ext;                    // the game side's per-status state (rules handle, drift, tint)

        public bool Active => Stacks > 0;

        /// <summary>
        /// Apply <paramref name="n"/> stacks for <paramref name="duration"/> seconds (null = until removed) at
        /// <paramref name="now"/>. Returns true when it was inactive before (statusStart).
        /// refresh: stacks add up to maxStacks and the shared timer restarts. extend: stacks add up and the remaining
        /// time grows by the duration. add: every stack keeps its own timer; past maxStacks the soonest-ending ones go.
        /// ignore: no change while active.
        /// </summary>
        public bool Apply(double now, double? duration, int n)
        {
            n = Math.Max(1, n);
            int max = Math.Max(1, MaxStacks);
            double until = duration.HasValue ? now + Math.Max(0, duration.Value) : double.PositiveInfinity;
            bool started = !Active;
            if (started)
            {
                StackExpiry.Clear();
                if (Stacking == "add")
                {
                    for (int i = 0; i < Math.Min(n, max); i++) StackExpiry.Add(until);
                    Stacks = StackExpiry.Count;
                    ExpireAt = until;
                }
                else
                {
                    Stacks = Math.Min(n, max);
                    ExpireAt = until;
                }
                NextTick = Tick > 0 ? now + Tick : double.PositiveInfinity;
                return true;
            }
            switch (Stacking)
            {
                case "ignore":
                    break;
                case "add":
                    for (int i = 0; i < n; i++) StackExpiry.Add(until);
                    StackExpiry.Sort();
                    while (StackExpiry.Count > max) StackExpiry.RemoveAt(0);
                    Stacks = StackExpiry.Count;
                    ExpireAt = StackExpiry[StackExpiry.Count - 1];
                    break;
                case "extend":
                    Stacks = Math.Min(Stacks + n, max);
                    if (!double.IsPositiveInfinity(ExpireAt)) ExpireAt = duration.HasValue ? ExpireAt + Math.Max(0, duration.Value) : double.PositiveInfinity;
                    break;
                default:   // refresh
                    Stacks = Math.Min(Stacks + n, max);
                    ExpireAt = until;
                    break;
            }
            return false;
        }

        /// <summary>Remove <paramref name="n"/> stacks (all when n &lt;= 0); the soonest-ending go first.</summary>
        public void RemoveStacks(int n)
        {
            if (n <= 0 || n >= Stacks) { Stacks = 0; StackExpiry.Clear(); return; }
            Stacks -= n;
            if (StackExpiry.Count > 0)
            {
                StackExpiry.Sort();
                StackExpiry.RemoveRange(0, Math.Min(n, StackExpiry.Count));
            }
        }

        /// <summary>Drop expired stacks at <paramref name="now"/>. Returns true if the stack count changed.</summary>
        public bool Expire(double now)
        {
            if (!Active) return false;
            int before = Stacks;
            if (StackExpiry.Count > 0)
            {
                StackExpiry.RemoveAll(t => t <= now + 1e-9);
                Stacks = StackExpiry.Count;
                ExpireAt = Stacks > 0 ? StackExpiry[StackExpiry.Count - 1] : now;
            }
            else if (ExpireAt <= now + 1e-9) Stacks = 0;
            return Stacks != before;
        }

        /// <summary>How many statusTick events are due by <paramref name="now"/> (advances the tick timer).</summary>
        public int TicksDue(double now)
        {
            if (!Active || Tick <= 0) return 0;
            int k = 0;
            while (NextTick <= now + 1e-9 && k < 100) { NextTick += Tick; k++; }
            return k;
        }

        /// <summary>Seconds left (the longest stack for "add"); +inf when it doesn't end on its own.</summary>
        public double Remaining(double now) => Math.Max(0, ExpireAt - now);
    }
}
