using System;
using System.Collections.Generic;

/*
    The session's logic world: entity registry, event queue, scheduler, vars, random source, budgets and trace.
    Pure C# (driven by LogicManager in game and by tests with a fake world).
    Skeleton for the interface PR: the dispatch core lands next; the public surface is what LogicApi.cs promises.
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicWorld
    {
        /// <summary>The running session's world, or null outside a Builder session.</summary>
        public static LogicWorld Current { get; internal set; }

        /// <summary>Seconds of game time since level start (fixed 0.1 s steps drive timers and polling).</summary>
        public double Time { get; internal set; }
        public int Frame { get; internal set; }
        public LogicCaps Caps { get; internal set; } = new();
        public LogicRandom Random { get; } = new(1);
        /// <summary>The level script's owner (`self` in level rules, `level` in expressions).</summary>
        public LogicEntity Level { get; } = new() { Id = "level", Kind = "level", Team = "neutral" };

        private readonly Dictionary<string, LogicEntity> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LogicEntity> byName = new(StringComparer.Ordinal);
        private readonly Queue<LogicEvent> queue = new();
        private long seq;

        public IEnumerable<LogicEntity> Entities => byId.Values;
        public LogicEntity Find(string id) => id != null && byId.TryGetValue(id, out var e) ? e : null;
        /// <summary>`@name`: a place or attach name (or a place id).</summary>
        public LogicEntity FindNamed(string name) => name != null && byName.TryGetValue(name, out var e) && !e.Destroyed ? e : Find(name);

        /// <summary>Add an entity to the registry (pure part of LogicGame.Register).</summary>
        public void Add(LogicEntity e, bool vanilla = false)
        {
            if (string.IsNullOrEmpty(e.Id)) e.Id = $"{e.Template ?? "entity"}#{++spawnCounter}";
            e.SpawnTime = Time;
            byId[e.Id] = e;
            if (!string.IsNullOrEmpty(e.Name)) byName[e.Name] = e;
        }

        private int spawnCounter;

        /// <summary>The entity's object is gone: drop it from the registry (its handle stays, with Destroyed set).</summary>
        public void Forget(LogicEntity e)
        {
            e.Destroyed = true;
            if (byId.TryGetValue(e.Id, out var cur) && cur == e) byId.Remove(e.Id);
            if (e.Name != null && byName.TryGetValue(e.Name, out cur) && cur == e) byName.Remove(e.Name);
        }

        public void Raise(string name, LogicEntity subject, LogicEntity other, double value, Vec? pos, string text, int depth = 0) =>
            queue.Enqueue(new LogicEvent { Name = name, Subject = subject, Other = other, Value = value, Pos = pos, Text = text, Depth = depth, Seq = ++seq });

        public void Signal(string name, IReadOnlyList<LogicEntity> to, double value, LogicEntity sender, int depth = 0) =>
            queue.Enqueue(new LogicEvent { Name = "signal", Text = name, Other = sender, Value = value, Depth = depth, Seq = ++seq, Target = to });

        internal void RunList(ActionList actions, LogicContext ctx) { }
    }

    /// <summary>xorshift128+ seeded from `logic.seed`; never UnityEngine.Random. Its state is savestated.</summary>
    public sealed class LogicRandom
    {
        private ulong s0, s1;
        public LogicRandom(ulong seed) { Seed(seed); }

        public void Seed(ulong seed)
        {
            ulong z = seed;
            s0 = SplitMix(ref z);
            s1 = SplitMix(ref z);
            if (s0 == 0 && s1 == 0) s1 = 1;
        }

        private static ulong SplitMix(ref ulong z)
        {
            ulong x = z += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        public ulong Next()
        {
            ulong x = s0, y = s1;
            s0 = y;
            x ^= x << 23;
            s1 = x ^ y ^ (x >> 17) ^ (y >> 26);
            return s1 + y;
        }

        /// <summary>[0, 1)</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
        public double Range(double a, double b) => a + (b - a) * NextDouble();
        /// <summary>Inclusive of both ends.</summary>
        public int RangeInt(int a, int b) => b <= a ? a : a + (int)(Next() % (ulong)((long)b - a + 1));
        public bool Chance(double p) => NextDouble() < p;

        public ulong[] State { get => [s0, s1]; set { s0 = value[0]; s1 = value[1]; } }
    }

    internal sealed class CompiledRule { }
    internal sealed class CompiledAction { }
    internal sealed class ActionCursor { }

    public static class Values
    {
        public static string Format(Value v) => v.Kind switch
        {
            ValueKind.None => "none",
            ValueKind.Number => FormatNumber(v.Num),
            ValueKind.Bool => v.Num != 0 ? "true" : "false",
            ValueKind.Text or ValueKind.Template => v.Str,
            ValueKind.Position => v.Pos.ToString(),
            ValueKind.Entity => v.AsEntity?.Id ?? "none",
            ValueKind.Set => $"[{v.AsSet.Count} entities]",
            ValueKind.List => $"[{string.Join(", ", v.AsList)}]",
            _ => "?",
        };

        public static string FormatNumber(double n) =>
            Math.Abs(n - Math.Round(n)) < 1e-9 ? ((long)Math.Round(n)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                               : n.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
