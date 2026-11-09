using System;
using System.Collections.Generic;
using System.Globalization;

namespace PalaceMod.Builder.Logic
{
    /// <summary>Value coercions and formatting (docs/builder-engine.md, "Expressions": an entity coerces to its
    /// position, a set to its count in numeric contexts).</summary>
    public static class Values
    {
        public static string Format(Value v) => v.Kind switch
        {
            ValueKind.None => "none",
            ValueKind.Number => FormatNumber(v.Num),
            ValueKind.Bool => v.Num != 0 ? "true" : "false",
            ValueKind.Text or ValueKind.Template => v.Str,
            ValueKind.Position => v.Pos.ToString(),
            ValueKind.Entity => v.AsEntity?.Name ?? v.AsEntity?.Id ?? "none",
            ValueKind.Set => v.AsSet.Count.ToString(CultureInfo.InvariantCulture),
            ValueKind.List => $"[{string.Join(", ", v.AsList)}]",
            _ => "?",
        };

        public static string FormatNumber(double n) =>
            Math.Abs(n - Math.Round(n)) < 1e-9 ? ((long)Math.Round(n)).ToString(CultureInfo.InvariantCulture)
                                               : n.ToString("0.##", CultureInfo.InvariantCulture);

        public static double ToNumber(Value v, string where = null) => v.Kind switch
        {
            ValueKind.Number or ValueKind.Bool => v.Num,
            ValueKind.Set => v.AsSet.Count,
            ValueKind.List => v.AsList.Count,
            ValueKind.None => 0,
            _ => throw new LogicError($"{(where != null ? where + ": " : "")}expected a number, got {Describe(v)}"),
        };

        public static bool ToBool(Value v) => v.Kind switch
        {
            ValueKind.Bool or ValueKind.Number => v.Num != 0,
            ValueKind.None => false,
            ValueKind.Entity => v.AsEntity != null && v.AsEntity.Exists,
            ValueKind.Set => v.AsSet.Count > 0,
            ValueKind.List => v.AsList.Count > 0,
            ValueKind.Text or ValueKind.Template => !string.IsNullOrEmpty(v.Str),
            _ => true,
        };

        public static Vec? ToPosition(Value v) => v.Kind switch
        {
            ValueKind.Position => v.Pos,
            ValueKind.Entity => v.AsEntity?.Position,
            ValueKind.Set => v.AsSet.Count > 0 ? v.AsSet[0].Position : null,
            _ => null,
        };

        public static LogicEntity ToEntity(Value v) => v.Kind switch
        {
            ValueKind.Entity => v.AsEntity,
            ValueKind.Set => v.AsSet.Count > 0 ? v.AsSet[0] : null,
            _ => null,
        };

        public static IReadOnlyList<LogicEntity> ToSet(Value v, int cap, string where = null)
        {
            switch (v.Kind)
            {
                case ValueKind.None: return Array.Empty<LogicEntity>();
                case ValueKind.Entity: return v.AsEntity == null ? Array.Empty<LogicEntity>() : [v.AsEntity];
                case ValueKind.Set:
                    {
                        var s = v.AsSet;
                        if (s.Count <= cap) return s;
                        var l = new List<LogicEntity>(cap);
                        for (int i = 0; i < cap; i++) l.Add(s[i]);
                        return l;
                    }
                case ValueKind.List:
                    {
                        var l = new List<LogicEntity>();
                        foreach (var x in v.AsList) if (x.AsEntity is { } e && l.Count < cap) l.Add(e);
                        return l;
                    }
                default: throw new LogicError($"{(where != null ? where + ": " : "")}expected entities, got {Describe(v)}");
            }
        }

        public static string Describe(Value v) => v.Kind switch
        {
            ValueKind.Text => $"text '{v.Str}'",
            ValueKind.None => "none",
            _ => $"{v.Kind.ToString().ToLowerInvariant()} {Format(v)}",
        };

        /// <summary>Equality as `==` sees it: numbers by value, entities by identity, text by ordinal.</summary>
        public static bool Same(Value a, Value b)
        {
            if (a.Kind is ValueKind.Number or ValueKind.Bool && b.Kind is ValueKind.Number or ValueKind.Bool) return Math.Abs(a.Num - b.Num) < 1e-9;
            if (a.Kind is ValueKind.Text or ValueKind.Template && b.Kind is ValueKind.Text or ValueKind.Template) return string.Equals(a.Str, b.Str, StringComparison.Ordinal);
            if (a.Kind == ValueKind.Set && b.Kind == ValueKind.Entity) return a.AsSet.Count == 1 && a.AsSet[0] == b.AsEntity;
            if (a.Kind == ValueKind.Entity && b.Kind == ValueKind.Set) return Same(b, a);
            return a.Equals(b);
        }

        /// <summary>A value as JSON (savestates, `logic vars`).</summary>
        public static Newtonsoft.Json.Linq.JToken ToJson(Value v) => v.Kind switch
        {
            ValueKind.None => Newtonsoft.Json.Linq.JValue.CreateNull(),
            ValueKind.Number => new Newtonsoft.Json.Linq.JValue(v.Num),
            ValueKind.Bool => new Newtonsoft.Json.Linq.JValue(v.Num != 0),
            ValueKind.Text => new Newtonsoft.Json.Linq.JObject { ["text"] = v.Str },
            ValueKind.Template => new Newtonsoft.Json.Linq.JObject { ["template"] = v.Str },
            ValueKind.Position => new Newtonsoft.Json.Linq.JArray(v.Pos.X, v.Pos.Y, v.Pos.Z),
            ValueKind.Entity => new Newtonsoft.Json.Linq.JObject { ["entity"] = v.AsEntity?.Id },
            ValueKind.Set => new Newtonsoft.Json.Linq.JObject { ["set"] = new Newtonsoft.Json.Linq.JArray(System.Linq.Enumerable.Select(v.AsSet, e => e.Id)) },
            ValueKind.List => new Newtonsoft.Json.Linq.JObject { ["list"] = new Newtonsoft.Json.Linq.JArray(System.Linq.Enumerable.Select(v.AsList, ToJson)) },
            _ => Newtonsoft.Json.Linq.JValue.CreateNull(),
        };

        public static Value FromJson(Newtonsoft.Json.Linq.JToken t, Func<string, LogicEntity> find)
        {
            switch (t)
            {
                case null: return Value.None;
                case Newtonsoft.Json.Linq.JValue jv when jv.Type == Newtonsoft.Json.Linq.JTokenType.Boolean: return Value.Bool((bool)jv);
                case Newtonsoft.Json.Linq.JValue jv when jv.Type is Newtonsoft.Json.Linq.JTokenType.Integer or Newtonsoft.Json.Linq.JTokenType.Float: return Value.Number((double)jv);
                case Newtonsoft.Json.Linq.JArray a when a.Count == 3: return Value.Position(new Vec((float)a[0], (float)a[1], (float)a[2]));
                case Newtonsoft.Json.Linq.JObject o:
                    if (o["text"] != null) return Value.Text((string)o["text"]);
                    if (o["template"] != null) return Value.Template((string)o["template"]);
                    if (o["entity"] != null) return Value.Entity(find((string)o["entity"]));
                    if (o["set"] is Newtonsoft.Json.Linq.JArray s)
                    {
                        var l = new List<LogicEntity>();
                        foreach (var id in s) if (find((string)id) is { } e) l.Add(e);
                        return Value.Set(l);
                    }
                    if (o["list"] is Newtonsoft.Json.Linq.JArray li)
                    {
                        var l = new List<Value>();
                        foreach (var x in li) l.Add(FromJson(x, find));
                        return Value.List(l);
                    }
                    return Value.None;
                default: return Value.None;
            }
        }
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

        public string State
        {
            get => $"{s0:x16}{s1:x16}";
            set { s0 = ulong.Parse(value.Substring(0, 16), NumberStyles.HexNumber); s1 = ulong.Parse(value.Substring(16, 16), NumberStyles.HexNumber); }
        }
    }
}
