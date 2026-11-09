using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

/*
    Steering's seam to the expression language: filters (targeting, kite/flee `from`), numbers and conditions
    (`score`, `flee.when`, a convert's `team`) and nested action lists (an attack's `do`).

    L1's evaluator (LogicExpr) isn't on main yet, so this holds a small fallback: filters with the full term set
    except `where(...)`, and expressions of the form `[-]role.prop [op value]` joined by and/or. Switching over is
    this file only.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal static class SteerExpr
    {
        private static readonly Dictionary<string, Func<LogicContext, LogicEntity, bool>> filters = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Func<LogicContext, Value>> exprs = new(StringComparer.Ordinal);

        /// <summary>A compiled filter over candidates (`it`), relative to ctx.Self. Throws LogicError on a syntax error.</summary>
        public static Func<LogicContext, LogicEntity, bool> Filter(string src)
        {
            src = (src ?? "").Trim();
            if (filters.TryGetValue(src, out var f)) return f;
            var p = new FilterParser(src);
            f = p.Parse();
            filters[src] = f;
            return f;
        }

        public static bool Matches(string filter, LogicEntity self, LogicEntity it) =>
            Filter(filter)(new LogicContext { World = LogicWorld.Current, Self = self, It = it }, it);

        /// <summary>A compiled expression (a leading `=` is allowed).</summary>
        public static Func<LogicContext, Value> Expr(string src)
        {
            src = (src ?? "").Trim();
            if (src.StartsWith("=")) src = src.Substring(1).Trim();
            if (exprs.TryGetValue(src, out var f)) return f;
            f = new ExprParser(src).Parse();
            exprs[src] = f;
            return f;
        }

        public static double Number(Value v) => v.Kind switch
        {
            ValueKind.Number or ValueKind.Bool => v.Num,
            ValueKind.Set => v.AsSet.Count,
            ValueKind.Entity => v.AsEntity != null ? 1 : 0,
            _ => 0,
        };

        public static bool Bool(Value v) => v.Kind switch
        {
            ValueKind.Number or ValueKind.Bool => v.Num != 0,
            ValueKind.Entity => v.AsEntity != null,
            ValueKind.Set => v.AsSet.Count > 0,
            ValueKind.Text => !string.IsNullOrEmpty(v.Str),
            _ => false,
        };

        /// <summary>A nested action list from JSON (an attack's `do`), or null until the rule compiler is on main.</summary>
        public static ActionList Actions(JArray list, string path) => null;

        /// <summary>Run actions outside a rule (from a MonoBehaviour), in a context with self/other bound.</summary>
        public static void Run(ActionList list, LogicContext ctx)
        {
            if (list == null || list.Count == 0) return;
            ctx.Run(list);
        }

        // ------------------------------------------------------------------ fallback filter grammar

        private static string SelfTeam(LogicContext ctx) => ctx.Self == null || ctx.Self.Kind == "level" ? "player" : ctx.Self.Team;

        internal static bool IsFoe(string self, string other) =>
            other != "neutral" && other != self && (self is "player" or "enemy" or "neutral");

        private sealed class FilterParser(string src)
        {
            private readonly List<string> toks = Tokenize(src);
            private int i;

            private static List<string> Tokenize(string s)
            {
                var l = new List<string>();
                int k = 0;
                while (k < s.Length)
                {
                    char c = s[k];
                    if (char.IsWhiteSpace(c)) { k++; continue; }
                    if (c is '(' or ')') { l.Add(c.ToString()); k++; continue; }
                    int start = k;
                    while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not '(' and not ')') k++;
                    l.Add(s.Substring(start, k - start));
                }
                return l;
            }

            public Func<LogicContext, LogicEntity, bool> Parse()
            {
                if (toks.Count == 0) throw new LogicError("empty filter");
                var f = Or();
                if (i < toks.Count) throw new LogicError($"filter \"{src}\": unexpected \"{toks[i]}\"");
                return f;
            }

            private string Peek => i < toks.Count ? toks[i] : null;

            private Func<LogicContext, LogicEntity, bool> Or()
            {
                var a = And();
                while (Peek == "or") { i++; var b = And(); var x = a; a = (ctx, c) => x(ctx, c) || b(ctx, c); }
                return a;
            }

            private Func<LogicContext, LogicEntity, bool> And()
            {
                var a = Not();
                while (Peek == "and") { i++; var b = Not(); var x = a; a = (ctx, c) => x(ctx, c) && b(ctx, c); }
                return a;
            }

            private Func<LogicContext, LogicEntity, bool> Not()
            {
                if (Peek == "not") { i++; var a = Not(); return (ctx, c) => !a(ctx, c); }
                if (Peek == "(")
                {
                    i++;
                    var a = Or();
                    if (Peek != ")") throw new LogicError($"filter \"{src}\": missing )");
                    i++;
                    return a;
                }
                if (Peek == null) throw new LogicError($"filter \"{src}\": ends early");
                return Term(toks[i++]);
            }

            private Func<LogicContext, LogicEntity, bool> Term(string t)
            {
                if (t.StartsWith("#")) { string label = t.Substring(1); return (_, c) => c.Labels.Contains(label); }
                int colon = t.IndexOf(':');
                if (colon > 0)
                {
                    string head = t.Substring(0, colon), arg = t.Substring(colon + 1);
                    switch (head)
                    {
                        case "is": return (_, c) => c.Template == arg || c.Template == "vanilla:" + arg;
                        case "has": return (_, c) => LogicServices.Stats != null && LogicServices.Stats.Stacks(c, arg) > 0;
                        case "type": return (_, c) => string.Equals(c.Body?.BuildingType, arg, StringComparison.OrdinalIgnoreCase);
                        case "in":
                            {
                                string area = arg.StartsWith("@") ? arg.Substring(1) : arg;
                                return (ctx, c) =>
                                {
                                    var a = area == "self" ? ctx.Self : ctx.World?.FindNamed(area);
                                    return a != null && (LogicServices.Areas?.Contains(a, c) ?? false);
                                };
                            }
                    }
                }
                if (t.StartsWith("where")) throw new LogicError("where(...) filters need the rule engine (not on main yet)");
                switch (t)
                {
                    case "foe": return (ctx, c) => IsFoe(SelfTeam(ctx), c.Team);
                    case "ally": return (ctx, c) => c.Team == SelfTeam(ctx);
                    case "player" or "enemy" or "neutral": return (_, c) => c.Team == t;
                    case "unit": return (_, c) => c.Kind is "unit" or "king";
                    case "building": return (_, c) => c.Kind is "building" or "castle";
                    case "prop" or "projectile" or "area" or "pickup" or "coin" or "king" or "castle" or "logic":
                        return (_, c) => c.Kind == t;
                    case "self": return (ctx, c) => c == ctx.Self;
                    case "melee": return (_, c) => c.Body?.HasTag("MeeleFighter") ?? false;
                    case "ranged": return (_, c) => c.Body?.HasTag("RangedFighter") ?? false;
                    case "alive": return (_, c) => c.Alive;
                    case "dead": return (_, c) => !c.Alive;
                }
                if (!LogicTags.TryParse(t, out _)) throw new LogicError($"filter \"{src}\": unknown term \"{t}\"");
                return (_, c) => c.Body?.HasTag(t) ?? false;
            }
        }

        // ------------------------------------------------------------------ fallback expressions

        private sealed class ExprParser(string src)
        {
            private int k;

            public Func<LogicContext, Value> Parse()
            {
                var f = Or();
                Skip();
                if (k < src.Length) throw new LogicError($"expression \"{src}\": unexpected \"{src.Substring(k)}\" (the full language isn't on main yet)");
                return f;
            }

            private void Skip() { while (k < src.Length && char.IsWhiteSpace(src[k])) k++; }

            private bool Eat(string s)
            {
                Skip();
                if (string.CompareOrdinal(src, k, s, 0, s.Length) != 0) return false;
                if (char.IsLetter(s[0]) && k + s.Length < src.Length && char.IsLetterOrDigit(src[k + s.Length])) return false;
                k += s.Length;
                return true;
            }

            private Func<LogicContext, Value> Or()
            {
                var a = And();
                while (Eat("or")) { var b = And(); var x = a; a = ctx => Value.Bool(Bool(x(ctx)) || Bool(b(ctx))); }
                return a;
            }

            private Func<LogicContext, Value> And()
            {
                var a = Cmp();
                while (Eat("and")) { var b = Cmp(); var x = a; a = ctx => Value.Bool(Bool(x(ctx)) && Bool(b(ctx))); }
                return a;
            }

            private Func<LogicContext, Value> Cmp()
            {
                var a = Sum();
                foreach (var op in new[] { "<=", ">=", "==", "!=", "<", ">" })
                {
                    if (!Eat(op)) continue;
                    var b = Sum();
                    return op switch
                    {
                        "<=" => ctx => Value.Bool(Number(a(ctx)) <= Number(b(ctx))),
                        ">=" => ctx => Value.Bool(Number(a(ctx)) >= Number(b(ctx))),
                        "<" => ctx => Value.Bool(Number(a(ctx)) < Number(b(ctx))),
                        ">" => ctx => Value.Bool(Number(a(ctx)) > Number(b(ctx))),
                        "==" => ctx => Value.Bool(Eq(a(ctx), b(ctx))),
                        _ => ctx => Value.Bool(!Eq(a(ctx), b(ctx))),
                    };
                }
                return a;
            }

            private static bool Eq(Value x, Value y) =>
                x.Kind is ValueKind.Text or ValueKind.Template || y.Kind is ValueKind.Text or ValueKind.Template ? x.Str == y.Str
                : x.Kind == ValueKind.Entity || y.Kind == ValueKind.Entity ? x.AsEntity == y.AsEntity
                : Number(x) == Number(y);

            private Func<LogicContext, Value> Sum()
            {
                var a = Prod();
                while (true)
                {
                    if (Eat("+")) { var b = Prod(); var x = a; a = ctx => Value.Number(Number(x(ctx)) + Number(b(ctx))); }
                    else if (Eat("-")) { var b = Prod(); var x = a; a = ctx => Value.Number(Number(x(ctx)) - Number(b(ctx))); }
                    else return a;
                }
            }

            private Func<LogicContext, Value> Prod()
            {
                var a = Unary();
                while (true)
                {
                    if (Eat("*")) { var b = Unary(); var x = a; a = ctx => Value.Number(Number(x(ctx)) * Number(b(ctx))); }
                    else if (Eat("/")) { var b = Unary(); var x = a; a = ctx => Value.Number(Number(x(ctx)) / Number(b(ctx))); }
                    else return a;
                }
            }

            private Func<LogicContext, Value> Unary()
            {
                if (Eat("-")) { var a = Unary(); return ctx => Value.Number(-Number(a(ctx))); }
                if (Eat("not")) { var a = Unary(); return ctx => Value.Bool(!Bool(a(ctx))); }
                if (Eat("(")) { var a = Or(); if (!Eat(")")) throw new LogicError($"expression \"{src}\": missing )"); return a; }
                return Atom();
            }

            private Func<LogicContext, Value> Atom()
            {
                Skip();
                if (k >= src.Length) throw new LogicError($"expression \"{src}\": ends early");
                char c = src[k];
                if (char.IsDigit(c) || c == '.')
                {
                    int s = k;
                    while (k < src.Length && (char.IsDigit(src[k]) || src[k] == '.')) k++;
                    var v = Value.Number(double.Parse(src.Substring(s, k - s), CultureInfo.InvariantCulture));
                    return _ => v;
                }
                if (c == '\'')
                {
                    int end = src.IndexOf('\'', k + 1);
                    if (end < 0) throw new LogicError($"expression \"{src}\": unclosed text");
                    var v = Value.Text(src.Substring(k + 1, end - k - 1));
                    k = end + 1;
                    return _ => v;
                }
                int st = k;
                while (k < src.Length && (char.IsLetterOrDigit(src[k]) || src[k] is '_' or '.' or '$')) k++;
                string word = src.Substring(st, k - st);
                if (word.Length == 0) throw new LogicError($"expression \"{src}\": unexpected \"{c}\"");
                if (word is "true" or "false") { var b = Value.Bool(word == "true"); return _ => b; }
                var parts = word.Split('.');
                Func<LogicContext, Value> role = parts[0] switch
                {
                    "self" => ctx => Value.Entity(ctx.Self),
                    "other" => ctx => Value.Entity(ctx.Other),
                    "subject" => ctx => Value.Entity(ctx.Subject),
                    "it" => ctx => Value.Entity(ctx.It),
                    "spawned" => ctx => Value.Entity(ctx.Spawned),
                    "king" => _ => Value.Entity(LogicServices.Game?.King),
                    "castle" => _ => Value.Entity(LogicServices.Game?.Castle),
                    "isNight" => _ => Value.Bool(DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Day),
                    "isDay" => _ => Value.Bool(DayNightCycle.Instance == null || DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day),
                    _ => throw new LogicError($"expression \"{src}\": unknown name \"{parts[0]}\" (the full language isn't on main yet)"),
                };
                for (int p = 1; p < parts.Length; p++)
                {
                    var prev = role;
                    string prop = parts[p];
                    role = ctx => Prop(prev(ctx).AsEntity, prop, ctx);
                }
                return role;
            }

            private static Value Prop(LogicEntity e, string prop, LogicContext ctx)
            {
                if (e == null) return Value.None;
                if (prop.StartsWith("$")) return e.Vars.TryGetValue(prop.Substring(1), out var v) ? v : Value.None;
                return prop switch
                {
                    "hp" => Value.Number(e.Body?.Hp ?? 0),
                    "maxHp" => Value.Number(e.Body?.MaxHp ?? 0),
                    "hpPct" => Value.Number(e.Body != null && e.Body.MaxHp > 0 ? e.Body.Hp / e.Body.MaxHp : 0),
                    "alive" => Value.Bool(e.Alive),
                    "team" => Value.Text(e.Team),
                    "kind" => Value.Text(e.Kind),
                    "template" => Value.Template(e.Template),
                    "x" => Value.Number(e.Position.X),
                    "y" => Value.Number(e.Position.Y),
                    "z" => Value.Number(e.Position.Z),
                    "pos" => Value.Position(e.Position),
                    "owner" => Value.Entity(e.Owner),
                    "dist" => Value.Number(ctx.Self != null ? Vec.FlatDistance(ctx.Self.Position, e.Position) : 0),
                    _ => throw new LogicError($"unknown property .{prop} (the full language isn't on main yet)"),
                };
            }
        }
    }
}
