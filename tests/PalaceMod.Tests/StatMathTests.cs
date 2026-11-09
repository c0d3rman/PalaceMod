using System;
using System.Collections.Generic;
using PalaceMod.Builder.Logic.Bodies;

namespace PalaceMod.Tests
{
    public static class StatMathTests
    {
        private static void Near(double expected, double actual, string what)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static double Combine(double b, params (Mod m, int n)[] mods)
        {
            var counts = new Dictionary<Mod, int>();
            var list = new List<Mod>();
            long order = 0;
            foreach (var (m, n) in mods) { m.Order = ++order; counts[m] = n; list.Add(m); }
            return StatMath.Combine(b, list, m => counts[m]);
        }

        public static void MultCompoundsPerStack() =>
            Near(10 * 0.8 * 0.8 * 0.8, Combine(10, (new Mod { Mult = 0.8 }, 3)), "0.8^3");

        public static void AddSumsPerStack() =>
            Near(10 + 3 * 2, Combine(10, (new Mod { Add = 2 }, 3)), "n x add");

        public static void AddThenMult() =>
            Near((10 + 5) * 0.5, Combine(10, (new Mod { Mult = 0.5 }, 1), (new Mod { Add = 5 }, 1)), "(base + add) * mult");

        public static void LatestSetReplacesBase()
        {
            Near(7, Combine(10, (new Mod { Set = 3 }, 1), (new Mod { Set = 7 }, 1)), "later set wins");
            Near((7 + 1) * 2, Combine(10, (new Mod { Set = 7 }, 1), (new Mod { Add = 1 }, 1), (new Mod { Mult = 2 }, 1)), "set then add then mult");
        }

        public static void ZeroStacksDoNothing() =>
            Near(10, Combine(10, (new Mod { Mult = 0.5, Add = 3, Set = 1 }, 0)), "no stacks");

        public static void IncludeFilters()
        {
            var a = new Mod { Mult = 0.5, Order = 1 };
            var b = new Mod { Mult = 0.1, Order = 2, Conditional = true };
            Near(5, StatMath.Combine(10, [a, b], _ => 1, m => !m.Conditional), "conditional left out");
        }

        public static void TypesMatching()
        {
            Assert.True(StatMath.TypesMatch(null, null), "no types: any damage");
            Assert.True(StatMath.TypesMatch(["fire"], new HashSet<string> { "fire", "magic" }), "fire matches");
            Assert.True(!StatMath.TypesMatch(["fire"], new HashSet<string> { "melee" }), "melee isn't fire");
            Assert.True(!StatMath.TypesMatch(["fire"], null), "untyped damage isn't fire");
        }

        public static void RefreshStacksAndRestarts()
        {
            var s = new StatusState { Stacking = "refresh", MaxStacks = 3 };
            Assert.True(s.Apply(0, 4, 1), "first apply starts");
            Assert.True(!s.Apply(2, 4, 1), "second doesn't start");
            Assert.Equal(2, s.Stacks, "stacks");
            Near(6, s.ExpireAt, "timer restarted");
            s.Apply(3, 4, 5);
            Assert.Equal(3, s.Stacks, "capped at maxStacks");
            Assert.True(!s.Expire(6.9), "still on");
            Assert.True(s.Expire(7), "ends together");
            Assert.Equal(0, s.Stacks, "all gone");
        }

        public static void AddKeepsOwnTimers()
        {
            var s = new StatusState { Stacking = "add", MaxStacks = 2 };
            s.Apply(0, 4, 1);
            s.Apply(1, 4, 1);
            Assert.Equal(2, s.Stacks, "two stacks");
            s.Apply(2, 4, 1);
            Assert.Equal(2, s.Stacks, "capped; the soonest-ending one went");
            Assert.True(s.Expire(5), "the t=1 stack ends at 5");
            Assert.Equal(1, s.Stacks, "one left");
            s.Expire(6);
            Assert.Equal(0, s.Stacks, "the t=2 stack ends at 6");
        }

        public static void ExtendAddsTime()
        {
            var s = new StatusState { Stacking = "extend", MaxStacks = 1 };
            s.Apply(0, 4, 1);
            s.Apply(1, 4, 1);
            Near(8, s.ExpireAt, "4 + 4");
            Assert.Equal(1, s.Stacks, "one stack");
        }

        public static void IgnoreKeepsFirst()
        {
            var s = new StatusState { Stacking = "ignore", MaxStacks = 5 };
            s.Apply(0, 4, 1);
            s.Apply(3, 10, 2);
            Assert.Equal(1, s.Stacks, "unchanged");
            Near(4, s.ExpireAt, "unchanged timer");
        }

        public static void PermanentUntilRemoved()
        {
            var s = new StatusState();
            s.Apply(0, null, 1);
            Assert.True(!s.Expire(1e6), "no duration: never expires");
            s.RemoveStacks(0);
            Assert.True(!s.Active, "removed");
        }

        public static void TicksAdvance()
        {
            var s = new StatusState { Tick = 1 };
            s.Apply(0, 4, 1);
            Assert.Equal(0, s.TicksDue(0.95), "none before 1 s");
            Assert.Equal(1, s.TicksDue(1.0), "one at 1 s");
            Assert.Equal(2, s.TicksDue(3.05), "two more by 3 s");
        }

        public static void RemoveSomeStacks()
        {
            var s = new StatusState { Stacking = "add", MaxStacks = 5 };
            s.Apply(0, 2, 1);
            s.Apply(0, 9, 1);
            s.Apply(0, 5, 1);
            s.RemoveStacks(1);
            Assert.Equal(2, s.Stacks, "one removed");
            s.Expire(5.5);
            Assert.Equal(1, s.Stacks, "the 2 s one went first, then the 5 s one");
        }
    }
}
