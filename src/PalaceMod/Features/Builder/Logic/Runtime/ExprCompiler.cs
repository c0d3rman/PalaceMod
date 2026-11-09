using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model.Logic;

/*
    Expressions and filters (L4's ExprNode / FilterNode, Model/Logic/Expr.cs) -> closures over a LogicContext.
    Compiled once per source string and cached; evaluation is pure (reads state, never changes it), and `rand`,
    `chance`, `random(...)` and `pointNear` draw from the world's seeded LogicRandom.

    Runtime type errors (an entity where a number was needed, an unknown function) throw LogicError, which the world
    traces against the rule. The validator catches most of them statically (ExprChecker).
*/
namespace PalaceMod.Builder.Logic
{
    internal delegate Value Eval(LogicContext ctx);
    internal delegate bool FilterEval(LogicContext ctx, LogicEntity candidate);

    internal static class ExprCompiler
    {
        private static readonly Dictionary<string, Func<LogicContext, Value>> exprCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Func<LogicContext, LogicEntity, bool>> filterCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Func<LogicContext, string>> textCache = new(StringComparer.Ordinal);

        // ------------------------------------------------------------------ entry points

        public static Func<LogicContext, Value> Compile(string src, string path)
        {
            src ??= "";
            if (exprCache.TryGetValue(src, out var f)) return f;
            var node = Expr.Parse(src, out string error, out int col) ?? throw new LogicError($"{path}: col {col + 1}: {error}");
            var e = Node(node);
            f = ctx => e(ctx);
            exprCache[src] = f;
            return f;
        }

        public static Func<LogicContext, LogicEntity, bool> CompileFilter(string src, string path)
        {
            src ??= "";
            if (filterCache.TryGetValue(src, out var f)) return f;
            var node = Filter.Parse(src, out string error, out int col) ?? throw new LogicError($"{path}: filter col {col + 1}: {error}");
            var fe = FilterNodeEval(node);
            f = (ctx, c) => fe(ctx, c);
            filterCache[src] = f;
            return f;
        }

        public static Func<LogicContext, string> CompileText(string text, string path)
        {
            text ??= "";
            if (textCache.TryGetValue(text, out var f)) return f;
            if (text.IndexOf('{') < 0 && text.IndexOf('}') < 0) { f = _ => text; textCache[text] = f; return f; }
            var parts = Expr.ParseInterp(text, out string error, out int col) ?? throw new LogicError($"{path}: col {col + 1}: {error}");
            var compiled = parts.Select(p => p is string s ? (Eval)(_ => Value.Text(s)) : Node((ExprNode)p)).ToArray();
            f = ctx =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in compiled) sb.Append(Values.Format(c(ctx)));
                return sb.ToString();
            };
            textCache[text] = f;
            return f;
        }

        // ------------------------------------------------------------------ nodes

        private static Eval Node(ExprNode n)
        {
            switch (n.Kind)
            {
                case ExprKind.Number: { var v = Value.Number((double)n.Literal); return _ => v; }
                case ExprKind.Bool: { var v = Value.Bool((bool)n.Literal); return _ => v; }
                case ExprKind.Text: { var v = Value.Text((string)n.Literal); return _ => v; }
                case ExprKind.Template: { var v = Value.Template(n.Name); return _ => v; }
                case ExprKind.LevelVar: { string name = n.Name; return ctx => ctx.World.Level.Vars.TryGetValue(name, out var v) ? v : Value.None; }
                case ExprKind.Named: { string name = n.Name; return ctx => Value.Entity(ctx.World.FindNamed(name)); }
                case ExprKind.Role: return Role(n.Name);
                case ExprKind.Global: return Global(n.Name);
                case ExprKind.Member: return Member(n);
                case ExprKind.Call: return Call(n);
                case ExprKind.Unary:
                    {
                        var a = Node(n.Args[0]);
                        if (n.Op == "not") return ctx => Value.Bool(!Values.ToBool(a(ctx)));
                        return ctx =>
                        {
                            var v = a(ctx);
                            return v.Kind == ValueKind.Position ? Value.Position(v.Pos * -1) : Value.Number(-Values.ToNumber(v, "-"));
                        };
                    }
                case ExprKind.Binary: return Binary(n.Op, Node(n.Args[0]), Node(n.Args[1]));
                case ExprKind.Cond:
                    {
                        var c = Node(n.Args[0]); var a = Node(n.Args[1]); var b = Node(n.Args[2]);
                        return ctx => Values.ToBool(c(ctx)) ? a(ctx) : b(ctx);
                    }
                case ExprKind.ListLit:
                    {
                        var items = n.Args.Select(Node).ToArray();
                        return ctx => Value.List(items.Select(i => i(ctx)).ToList());
                    }
            }
            throw new LogicError($"col {n.Col + 1}: unsupported expression");
        }

        private static string ResolveAlias(string name) => LogicSchema.RoleAliases.TryGetValue(name, out var to) ? to : name;

        private static Eval Role(string name)
        {
            // Aliases (killer, victim, attacker, ...) name `other` or `subject` per event: resolve at run time.
            if (!Expr.Roles.Contains(name))
            {
                var bySubject = Role("subject");
                var byOther = Role("other");
                string fallback = ResolveAlias(name);
                return ctx =>
                {
                    if (ctx.Locals != null && ctx.Locals.TryGetValue(name, out var local)) return local;
                    string ev = ctx.Event?.Name;
                    string role = ev != null && LogicSchema.Events.TryGetValue(ev, out var spec) && spec.Aliases.TryGetValue(name, out var r) ? r : fallback;
                    return role switch
                    {
                        "subject" => bySubject(ctx),
                        "other" => byOther(ctx),
                        _ => throw new LogicError($"\"{name}\" isn't defined here"),
                    };
                };
            }
            string role = name;
            Eval Local(Eval fallback) => ctx => ctx.Locals != null && ctx.Locals.TryGetValue(name, out var v) ? v : fallback(ctx);
            return role switch
            {
                "self" => ctx => Value.Entity(ctx.Self),
                "subject" => Local(ctx => Value.Entity(ctx.Subject)),
                "other" => Local(ctx => Value.Entity(ctx.Other)),
                "spawned" => ctx => Value.Entity(ctx.Spawned),
                "it" => ctx => Value.Entity(ctx.It),
                "king" => ctx => Value.Entity(LogicServices.Game?.King),
                "castle" => ctx => Value.Entity(LogicServices.Game?.Castle),
                "level" => ctx => Value.Entity(ctx.World.Level),
                "event" => ctx => Value.Number(ctx.Event?.Value ?? 0),
                _ => Local(ctx => throw new LogicError($"\"{name}\" isn't defined here")),
            };
        }

        private static Eval Global(string name)
        {
            static IGameState G() => LogicServices.Game ?? throw new LogicError("game state isn't available");
            return name switch
            {
                "night" => _ => Value.Number(G().Night),
                "isNight" => _ => Value.Bool(G().IsNight),
                "isDay" => _ => Value.Bool(!G().IsNight),
                "nights" => _ => Value.Number(G().Nights),
                "time" => ctx => Value.Number(ctx.World.Time),
                "nightTime" => _ => Value.Number(G().NightTime),
                "nightProgress" => _ => Value.Number(G().NightProgress),
                "gold" => _ => Value.Number(G().Gold),
                "income" => _ => Value.Number(G().Income),
                "enemiesAlive" => _ => Value.Number(G().EnemiesAlive),
                "difficulty" => _ => Value.Number(G().Difficulty),
                "waterLevel" => _ => Value.Number(WorldQueries.Service is { } wq && !double.IsNaN(wq.WaterLevel) ? wq.WaterLevel : 0),
                "none" or "null" => _ => Value.None,
                _ => ctx => ctx.Locals != null && ctx.Locals.TryGetValue(name, out var v) ? v : throw new LogicError($"unknown name \"{name}\""),
            };
        }

        // ------------------------------------------------------------------ members

        private static Eval Member(ExprNode n)
        {
            string name = n.Name;
            if (n.Target.Kind == ExprKind.Role && ResolveAlias(n.Target.Name) == "event")
            {
                return name switch
                {
                    "value" => ctx => ctx.Event == null ? Value.None : !ctx.Event.Payload.IsNone ? ctx.Event.Payload : Value.Number(ctx.Event.Value),
                    "pos" => ctx => ctx.Event?.Pos is Vec p ? Value.Position(p) : ctx.Event?.Subject != null ? Value.Position(ctx.Event.Subject.Position) : Value.None,
                    "name" or "choice" or "result" => ctx => ctx.Event?.Text != null ? Value.Text(ctx.Event.Text) : Value.None,
                    _ => throw new LogicError($"col {n.Col + 1}: event has no \"{name}\""),
                };
            }
            var target = Node(n.Target);
            if (name.StartsWith("$"))
            {
                string var = name.Substring(1);
                return ctx =>
                {
                    var t = target(ctx);
                    var e = Values.ToEntity(t);
                    if (e == null) return Value.None;
                    return e.Vars.TryGetValue(var, out var v) ? v : Value.None;
                };
            }
            return ctx => Property(target(ctx), name, ctx);
        }

        internal static Value Property(Value t, string name, LogicContext ctx)
        {
            switch (t.Kind)
            {
                case ValueKind.Position:
                    return name switch
                    {
                        "x" => Value.Number(t.Pos.X), "y" => Value.Number(t.Pos.Y), "z" => Value.Number(t.Pos.Z),
                        _ => throw new LogicError($"a position has no \"{name}\""),
                    };
                case ValueKind.Set:
                    return name switch
                    {
                        "len" => Value.Number(t.AsSet.Count),
                        "first" => Value.Entity(t.AsSet.Count > 0 ? t.AsSet[0] : null),
                        "last" => Value.Entity(t.AsSet.Count > 0 ? t.AsSet[t.AsSet.Count - 1] : null),
                        _ => t.AsSet.Count > 0 ? Property(Value.Entity(t.AsSet[0]), name, ctx) : Value.None,
                    };
                case ValueKind.List:
                    return name switch
                    {
                        "len" => Value.Number(t.AsList.Count),
                        "first" => t.AsList.Count > 0 ? t.AsList[0] : Value.None,
                        "last" => t.AsList.Count > 0 ? t.AsList[t.AsList.Count - 1] : Value.None,
                        _ => throw new LogicError($"a list has no \"{name}\""),
                    };
                case ValueKind.Template:
                    return name switch
                    {
                        "id" => Value.Text(t.Str),
                        _ => throw new LogicError($"a template has no \"{name}\""),
                    };
                case ValueKind.None:
                    return Value.None;
                case ValueKind.Entity:
                    return EntityProperty(t.AsEntity, name, ctx);
                default:
                    throw new LogicError($"{Values.Describe(t)} has no \"{name}\"");
            }
        }

        private static Value EntityProperty(LogicEntity e, string name, LogicContext ctx)
        {
            var b = e.Body;
            switch (name)
            {
                case "hp": return Value.Number(b?.Hp ?? 0);
                case "maxHp": return Value.Number(b?.MaxHp ?? 0);
                case "hpPct": return Value.Number(b != null && b.MaxHp > 0 ? b.Hp / b.MaxHp : 0);
                case "alive": return Value.Bool(e.Alive);
                case "pos": return Value.Position(e.Position);
                case "x": return Value.Number(e.Position.X);
                case "y": return Value.Number(e.Position.Y);
                case "z": return Value.Number(e.Position.Z);
                case "facing": return Value.Number(b?.Facing ?? 0);
                case "team": return Value.Text(e.Team);
                case "kind": return Value.Text(e.Kind);
                case "template": return e.Template != null ? Value.Template(e.Template) : Value.None;
                case "labels": return Value.List(e.Labels.OrderBy(l => l, StringComparer.Ordinal).Select(Value.Text).ToList());
                case "level": return Value.Number(b?.Level ?? 0);
                case "buildingType": return b?.BuildingType != null ? Value.Text(b.BuildingType) : Value.None;
                case "age": return Value.Number(ctx.World.Time - e.SpawnTime);
                case "target": return Value.Entity(LogicServices.TargetOf?.Invoke(e));
                case "owner": return Value.Entity(e.Owner);
                case "id": return Value.Text(e.Id);
                case "name": return Value.Text(e.Name ?? e.Id);
                case "enabled": return Value.Bool(e.Enabled);
                case "elite": return Value.Bool(LogicServices.IsElite?.Invoke(e) ?? false);
            }
            var stats = LogicServices.Stats;
            if (stats != null)
            {
                double v = stats.Get(e, name);
                if (!double.IsNaN(v)) return Value.Number(v);
            }
            throw new LogicError($"\"{name}\" isn't a property of {e.Id}" + (stats == null ? " (stats aren't available yet)" : ""));
        }

        // ------------------------------------------------------------------ operators

        private static Eval Binary(string op, Eval a, Eval b)
        {
            switch (op)
            {
                case "and": return ctx => Value.Bool(Values.ToBool(a(ctx)) && Values.ToBool(b(ctx)));
                case "or": return ctx => Value.Bool(Values.ToBool(a(ctx)) || Values.ToBool(b(ctx)));
                case "==": return ctx => Value.Bool(Values.Same(a(ctx), b(ctx)));
                case "!=": return ctx => Value.Bool(!Values.Same(a(ctx), b(ctx)));
                case "<": return ctx => Value.Bool(Num(a(ctx), op) < Num(b(ctx), op));
                case "<=": return ctx => Value.Bool(Num(a(ctx), op) <= Num(b(ctx), op));
                case ">": return ctx => Value.Bool(Num(a(ctx), op) > Num(b(ctx), op));
                case ">=": return ctx => Value.Bool(Num(a(ctx), op) >= Num(b(ctx), op));
                case "+":
                    return ctx =>
                    {
                        var x = a(ctx); var y = b(ctx);
                        if (x.Kind is ValueKind.Text || y.Kind is ValueKind.Text) return Value.Text(Values.Format(x) + Values.Format(y));
                        if (IsPos(x) && IsPos(y)) return Value.Position(Pos(x) + Pos(y));
                        return Value.Number(Num(x, op) + Num(y, op));
                    };
                case "-":
                    return ctx =>
                    {
                        var x = a(ctx); var y = b(ctx);
                        if (IsPos(x) && IsPos(y)) return Value.Position(Pos(x) - Pos(y));
                        return Value.Number(Num(x, op) - Num(y, op));
                    };
                case "*":
                    return ctx =>
                    {
                        var x = a(ctx); var y = b(ctx);
                        if (x.Kind == ValueKind.Position) return Value.Position(x.Pos * (float)Num(y, op));
                        if (y.Kind == ValueKind.Position) return Value.Position(y.Pos * (float)Num(x, op));
                        return Value.Number(Num(x, op) * Num(y, op));
                    };
                case "/":
                    return ctx =>
                    {
                        var x = a(ctx); double d = Num(b(ctx), op);
                        if (x.Kind == ValueKind.Position) return d == 0 ? Value.Position(Vec.Zero) : Value.Position(x.Pos / (float)d);
                        return Value.Number(d == 0 ? 0 : Num(x, op) / d);   // division by zero is 0, never NaN/inf
                    };
                case "%":
                    return ctx =>
                    {
                        double x = Num(a(ctx), op), d = Num(b(ctx), op);
                        if (d == 0) return Value.Number(0);
                        double r = x % d;
                        return Value.Number(r < 0 ? r + Math.Abs(d) : r);
                    };
            }
            throw new LogicError($"unknown operator \"{op}\"");
        }

        private static double Num(Value v, string op) => Values.ToNumber(v, $"'{op}'");
        private static bool IsPos(Value v) => v.Kind is ValueKind.Position or ValueKind.Entity;
        private static Vec Pos(Value v) => Values.ToPosition(v) ?? Vec.Zero;

        // ------------------------------------------------------------------ calls

        private static readonly HashSet<string> Selectors = new(StringComparer.Ordinal) { "nearest", "farthest", "weakest", "strongest", "random", "all", "best", "count" };

        private static Eval Call(ExprNode n)
        {
            string name = n.Name;
            var args = n.Args;
            Eval Arg(int i) => i < args.Length ? Node(args[i]) : (_ => Value.None);

            if (Selectors.Contains(name)) return Selector(n);

            if (n.Target != null)
            {
                var target = Node(n.Target);
                switch (name)
                {
                    case "stacks":
                        {
                            var s = Arg(0);
                            return ctx =>
                            {
                                var e = Values.ToEntity(target(ctx));
                                return Value.Number(e == null || LogicServices.Stats == null ? 0 : LogicServices.Stats.Stacks(e, Values.Format(s(ctx))));
                            };
                        }
                    case "is":
                        {
                            var f = FilterArg(args, 0);
                            return ctx =>
                            {
                                var e = Values.ToEntity(target(ctx));
                                return Value.Bool(e != null && f(ctx)(ctx.With(it: e), e));
                            };
                        }
                    case "dist":
                        {
                            var o = Arg(0);
                            return ctx => Value.Number(Vec.FlatDistance(Pos(target(ctx)), Pos(o(ctx))));
                        }
                    case "inside":
                        {
                            var area = Arg(0);
                            return ctx =>
                            {
                                var e = Values.ToEntity(target(ctx));
                                var a = Values.ToEntity(area(ctx));
                                return Value.Bool(e != null && a != null && Areas.Contains(a, e));
                            };
                        }
                    case "at":
                        {
                            var i = Arg(0);
                            return ctx =>
                            {
                                var t = target(ctx);
                                int idx = (int)Math.Floor(Num(i(ctx), "at"));
                                if (t.Kind == ValueKind.List) return idx >= 0 && idx < t.AsList.Count ? t.AsList[idx] : Value.None;
                                if (t.Kind == ValueKind.Set) return idx >= 0 && idx < t.AsSet.Count ? Value.Entity(t.AsSet[idx]) : Value.None;
                                return Value.None;
                            };
                        }
                    case "has":
                        {
                            var x = Arg(0);
                            return ctx =>
                            {
                                var t = target(ctx); var v = x(ctx);
                                if (t.Kind == ValueKind.List) return Value.Bool(t.AsList.Any(i => Values.Same(i, v)));
                                if (t.Kind == ValueKind.Set) return Value.Bool(v.AsEntity != null && t.AsSet.Contains(v.AsEntity));
                                return Value.Bool(false);
                            };
                        }
                }
                throw new LogicError($"col {n.Col + 1}: unknown method \"{name}\"");
            }

            Eval A0 = Arg(0), A1 = Arg(1), A2 = Arg(2);
            switch (name)
            {
                case "vec":
                    return args.Length >= 3
                        ? ctx => Value.Position(new Vec((float)Num(A0(ctx), name), (float)Num(A1(ctx), name), (float)Num(A2(ctx), name)))
                        : ctx => Value.Position(new Vec((float)Num(A0(ctx), name), 0, (float)Num(A1(ctx), name)));
                case "pointNear":
                    return ctx =>
                    {
                        var p = Pos(A0(ctx)); float r = (float)Num(A1(ctx), name);
                        var g = LogicServices.Game;
                        if (g != null) return Value.Position(g.PointNear(p, r, ctx.World.Random));
                        double ang = ctx.World.Random.NextDouble() * Math.PI * 2, d = r * Math.Sqrt(ctx.World.Random.NextDouble());
                        return Value.Position(new Vec(p.X + (float)(Math.Sin(ang) * d), p.Y, p.Z + (float)(Math.Cos(ang) * d)));
                    };
                case "groundY":
                    return ctx => Value.Number(WorldQueries.Service?.GroundY(Pos(A0(ctx))) ?? 0);
                case "walkable":
                    return ctx =>
                    {
                        var p = Pos(A0(ctx));
                        var who = args.Length > 1 ? A1(ctx) : Value.None;
                        var unit = Values.ToEntity(who);
                        string kind = unit == null && who.Kind != ValueKind.None ? Values.Format(who) : null;
                        return Value.Bool(WorldQueries.Service?.Walkable(p, kind, unit) ?? true);
                    };
                case "ground":
                    return ctx => Value.Text(WorldQueries.Service?.Ground(Pos(A0(ctx))) ?? "grass");
                case "waterDepth":
                    return ctx => Value.Number(WorldQueries.Service?.WaterDepth(Pos(A0(ctx))) ?? 0);
                case "sight":
                    return ctx => Value.Bool(WorldQueries.Service?.Raycast(Pos(A0(ctx)), Pos(A1(ctx))) == null);
                case "raycast":
                    return ctx => WorldQueries.Service?.Raycast(Pos(A0(ctx)), Pos(A1(ctx))) is Vec hit ? Value.Position(hit) : Value.None;
                case "ahead":
                    return ctx =>
                    {
                        var e = Values.ToEntity(A0(ctx));
                        if (e == null) return Value.None;
                        return Value.Position(e.Position + Dir(e.Body?.Facing ?? 0) * (float)Num(A1(ctx), name));
                    };
                case "toward":
                    return ctx =>
                    {
                        var a = Pos(A0(ctx)); var b = Pos(A1(ctx)); float d = (float)Num(A2(ctx), name);
                        var delta = b - a; float len = delta.Length;
                        return Value.Position(len < 1e-5f ? a : a + delta / len * d);
                    };
                case "lerp":
                    return ctx =>
                    {
                        var a = A0(ctx); var b = A1(ctx); float t = (float)Num(A2(ctx), name);
                        if (IsPos(a) && IsPos(b)) return Value.Position(Pos(a) + (Pos(b) - Pos(a)) * t);
                        return Value.Number(Num(a, name) + (Num(b, name) - Num(a, name)) * t);
                    };
                case "spawnLine":
                    return ctx => Value.Position((LogicServices.Game ?? throw new LogicError("game state isn't available")).SpawnLine(Values.Format(A0(ctx))));
                case "dir": return ctx => Value.Position(Dir((float)Num(A0(ctx), name)));
                case "angle":
                    return ctx =>
                    {
                        var d = Pos(A1(ctx)) - Pos(A0(ctx));
                        double deg = Math.Atan2(d.X, d.Z) * 180 / Math.PI;
                        return Value.Number(deg < 0 ? deg + 360 : deg);
                    };
                case "dist": return ctx => Value.Number(Vec.FlatDistance(Pos(A0(ctx)), args.Length > 1 ? Pos(A1(ctx)) : (ctx.Self?.Position ?? Vec.Zero)));
                case "min":
                case "max":
                    {
                        var all = args.Select(Node).ToArray();
                        bool min = name == "min";
                        return ctx =>
                        {
                            double r = min ? double.MaxValue : double.MinValue;
                            foreach (var a in all) { double v = Num(a(ctx), name); r = min ? Math.Min(r, v) : Math.Max(r, v); }
                            return Value.Number(all.Length == 0 ? 0 : r);
                        };
                    }
                case "abs": return ctx => Value.Number(Math.Abs(Num(A0(ctx), name)));
                case "floor": return ctx => Value.Number(Math.Floor(Num(A0(ctx), name)));
                case "ceil": return ctx => Value.Number(Math.Ceiling(Num(A0(ctx), name)));
                case "round": return ctx => Value.Number(Math.Round(Num(A0(ctx), name), MidpointRounding.AwayFromZero));
                case "sqrt": return ctx => Value.Number(Math.Sqrt(Math.Max(0, Num(A0(ctx), name))));
                case "sin": return ctx => Value.Number(Math.Sin(Num(A0(ctx), name) * Math.PI / 180));
                case "cos": return ctx => Value.Number(Math.Cos(Num(A0(ctx), name) * Math.PI / 180));
                case "clamp": return ctx => Value.Number(Math.Max(Num(A1(ctx), name), Math.Min(Num(A2(ctx), name), Num(A0(ctx), name))));
                case "pow":
                    return ctx =>
                    {
                        double r = Math.Pow(Num(A0(ctx), name), Num(A1(ctx), name));
                        return Value.Number(double.IsNaN(r) || double.IsInfinity(r) ? 0 : r);
                    };
                case "rand":
                    return args.Length >= 2
                        ? ctx => Value.Number(ctx.World.Random.Range(Num(A0(ctx), name), Num(A1(ctx), name)))
                        : ctx => Value.Number(ctx.World.Random.NextDouble());
                case "randInt": return ctx => Value.Number(ctx.World.Random.RangeInt((int)Math.Floor(Num(A0(ctx), name)), (int)Math.Floor(Num(A1(ctx), name))));
                case "chance": return ctx => Value.Bool(ctx.World.Random.Chance(Num(A0(ctx), name)));
                case "waveCount":
                    {
                        Func<LogicContext, string> filterSrc = args.Length > 1 ? (ctx => Values.Format(A1(ctx))) : null;
                        return ctx => Value.Number((LogicServices.Game ?? throw new LogicError("game state isn't available")).WaveCount((int)Num(A0(ctx), name), filterSrc?.Invoke(ctx)));
                    }
                case "waveUnits":
                    return ctx => Value.List((LogicServices.Game ?? throw new LogicError("game state isn't available")).WaveUnits((int)Num(A0(ctx), name)).Select(Value.Template).ToList());
                case "strongestUnit":
                    return ctx => (LogicServices.Game ?? throw new LogicError("game state isn't available")).StrongestUnit((int)Num(A0(ctx), name)) is { } t ? Value.Template(t) : Value.None;
            }
            throw new LogicError($"col {n.Col + 1}: unknown function \"{name}\"");
        }

        /// <summary>A unit vector at <paramref name="deg"/> (0 = north/+Z, 90 = east/+X).</summary>
        public static Vec Dir(float deg)
        {
            double r = deg * Math.PI / 180;
            return new Vec((float)Math.Sin(r), 0, (float)Math.Cos(r));
        }

        /// <summary>A filter argument: compiled once when it's a text literal, else per distinct runtime string.</summary>
        private static Func<LogicContext, Func<LogicContext, LogicEntity, bool>> FilterArg(ExprNode[] args, int i)
        {
            if (i >= args.Length) return _ => (_, _) => true;
            var a = args[i];
            if (a.Kind == ExprKind.Text)
            {
                var f = CompileFilter((string)a.Literal, $"col {a.Col + 1}");
                return _ => f;
            }
            var e = Node(a);
            return ctx => CompileFilter(Values.Format(e(ctx)), $"col {a.Col + 1}");
        }

        private static Eval Selector(ExprNode n)
        {
            string name = n.Name;
            var args = n.Args;
            var filter = FilterArg(args, 0);
            bool wantsSelf = args.Length > 0 && args[0].Kind == ExprKind.Text && FilterMentions((string)args[0].Literal, "self");
            bool wantsDead = args.Length > 0 && args[0].Kind == ExprKind.Text && FilterMentions((string)args[0].Literal, "dead");
            Eval radius = args.Length > 1 ? Node(args[1]) : (_ => Value.Number(0));
            Eval score = name == "best" && args.Length > 2 ? Node(args[2]) : null;
            Eval anchor = n.Target != null ? Node(n.Target) : (ctx => Value.Entity(ctx.Self));

            return ctx =>
            {
                var av = anchor(ctx);
                var anchorEntity = Values.ToEntity(av);
                Vec center = Values.ToPosition(av) ?? ctx.Self?.Position ?? Vec.Zero;
                float r = (float)Num(radius(ctx), name);
                // The level has no position: a selector anchored at it looks everywhere (a radius means nothing).
                bool anchorIsLevel = anchorEntity?.Kind == "level";
                if (anchorIsLevel) r = 0;
                var f = filter(ctx);
                var cands = new List<LogicEntity>();
                ctx.World.Query(center, r, cands);
                var fctx = ctx.With();
                var hits = new List<(LogicEntity e, float d)>();
                foreach (var c in cands)
                {
                    if (c == null || !c.Exists) continue;
                    if (!wantsSelf && (c == ctx.Self || c == anchorEntity)) continue;
                    if (!wantsDead && !c.Alive) continue;
                    float d = anchorIsLevel && n.Target == null ? 0 : Vec.FlatDistance(center, c.Position);
                    if (r > 0 && d > r) continue;
                    fctx.It = c;
                    if (!f(fctx, c)) continue;
                    hits.Add((c, d));
                }
                hits.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : string.CompareOrdinal(x.e.Id, y.e.Id));
                switch (name)
                {
                    case "count": return Value.Number(hits.Count);
                    case "all":
                        {
                            int cap = ctx.World.Caps.Set;
                            var l = new List<LogicEntity>(Math.Min(hits.Count, cap));
                            for (int i = 0; i < hits.Count && i < cap; i++) l.Add(hits[i].e);
                            return Value.Set(l);
                        }
                    case "nearest": return Value.Entity(hits.Count > 0 ? hits[0].e : null);
                    case "farthest": return Value.Entity(hits.Count > 0 ? hits[hits.Count - 1].e : null);
                    case "weakest": return Value.Entity(hits.Count == 0 ? null : hits.OrderBy(h => h.e.Body?.Hp ?? 0).First().e);
                    case "strongest": return Value.Entity(hits.Count == 0 ? null : hits.OrderByDescending(h => h.e.Body?.Hp ?? 0).First().e);
                    case "random": return Value.Entity(hits.Count == 0 ? null : hits[ctx.World.Random.RangeInt(0, hits.Count - 1)].e);
                    case "best":
                        {
                            if (score == null || hits.Count == 0) return Value.Entity(hits.Count > 0 ? hits[0].e : null);
                            LogicEntity best = null; double bestScore = double.MinValue;
                            foreach (var (e, _) in hits)
                            {
                                double s = Num(score(ctx.With(it: e)), "best");
                                if (s > bestScore) { bestScore = s; best = e; }
                            }
                            return Value.Entity(best);
                        }
                }
                return Value.None;
            };
        }

        private static bool FilterMentions(string src, string term)
        {
            var node = Filter.Parse(src, out _, out _);
            return node != null && Filter.Terms(node).Any(t => t.Term == term);
        }

        // ------------------------------------------------------------------ filters

        private static FilterEval FilterNodeEval(FilterNode n)
        {
            switch (n.Kind)
            {
                case FilterKind.And: { var a = FilterNodeEval(n.Args[0]); var b = FilterNodeEval(n.Args[1]); return (ctx, c) => a(ctx, c) && b(ctx, c); }
                case FilterKind.Or: { var a = FilterNodeEval(n.Args[0]); var b = FilterNodeEval(n.Args[1]); return (ctx, c) => a(ctx, c) || b(ctx, c); }
                case FilterKind.Not: { var a = FilterNodeEval(n.Args[0]); return (ctx, c) => !a(ctx, c); }
            }
            string arg = n.Arg;
            switch (n.Term)
            {
                case "foe": return (ctx, c) => IsFoe(SelfTeam(ctx), c.Team);
                case "ally": return (ctx, c) => c.Team == SelfTeam(ctx);
                case "player" or "enemy" or "neutral": { string t = n.Term; return (_, c) => c.Team == t; }
                case "unit": return (_, c) => c.Kind is "unit" or "king";
                case "building": return (_, c) => c.Kind is "building" or "castle";
                case "prop" or "projectile" or "area" or "pickup" or "coin" or "king" or "castle" or "logic" or "line" or "point" or "path":
                    { string k = n.Term; return (_, c) => c.Kind == k; }
                case "self": return (ctx, c) => c == ctx.Self;
                case "melee": return (_, c) => c.Body?.HasTag("MeeleFighter") ?? false;
                case "ranged": return (_, c) => c.Body?.HasTag("RangedFighter") ?? false;
                case "alive": return (_, c) => c.Alive;
                case "elite": return (_, c) => LogicServices.IsElite?.Invoke(c) ?? false;
                case "dead": return (_, c) => !c.Alive;
                case "label": return (_, c) => c.Labels.Contains(arg);
                case "is": return (_, c) => c.Template == arg || c.Template == "vanilla:" + arg;
                case "has": return (_, c) => LogicServices.Stats != null && LogicServices.Stats.Stacks(c, arg) > 0;
                case "type": return (_, c) => string.Equals(c.Body?.BuildingType, arg, StringComparison.OrdinalIgnoreCase);
                case "in":
                    if (arg == "self") return (ctx, c) => ctx.Self != null && Areas.Contains(ctx.Self, c);
                    {
                        string areaName = arg.StartsWith("@") ? arg.Substring(1) : arg;
                        return (ctx, c) => ctx.World.FindNamed(areaName) is { } area && Areas.Contains(area, c);
                    }
                case "where":
                    {
                        var e = Node(n.Where);
                        return (ctx, c) =>
                        {
                            var prev = ctx.It;
                            ctx.It = c;
                            try { return Values.ToBool(e(ctx)); }
                            finally { ctx.It = prev; }
                        };
                    }
                default:
                    {
                        string tag = n.Term;
                        return (_, c) => c.Body?.HasTag(tag) ?? false;
                    }
            }
        }

        /// <summary>The team foe/ally are relative to: self's, or the player's for the level script.</summary>
        private static string SelfTeam(LogicContext ctx) => ctx.Self == null || ctx.Self.Kind == "level" ? "player" : ctx.Self.Team;

        /// <summary>player and enemy are each other's foes; a neutral self treats both sides as foes; nobody's foe is neutral.</summary>
        public static bool IsFoe(string self, string other) =>
            other != "neutral" && other != self && (self is "player" or "enemy" or "neutral");
    }

    /// <summary>Area membership: L2's areas when installed, else the area's `area.shape` (circle, rect, polygon).</summary>
    internal static class Areas
    {
        public static bool Contains(LogicEntity area, LogicEntity e)
        {
            // A dead or removed entity (a `death` dispatched after the unit is gone) is no longer in the area's
            // live membership: judge it by where it was, from the shape.
            if (e.Alive)
            {
                var r = LogicServices.Areas?.Contains(area, e);
                if (r.HasValue) return r.Value;
            }
            var shape = area.Def?["area"]?["shape"] ?? area.Def?["shape"];
            if (shape == null) return false;
            var p = e.Position - area.Position;
            if (shape["circle"] is Newtonsoft.Json.Linq.JObject circle)
            {
                float rad = (float?)circle["radius"] ?? 1;
                return p.X * p.X + p.Z * p.Z <= rad * rad;
            }
            if (shape["rect"] is Newtonsoft.Json.Linq.JObject rect && rect["size"] is Newtonsoft.Json.Linq.JArray size && size.Count >= 2)
            {
                // Rotated with the area's facing.
                double yaw = -(area.Body?.Facing ?? (float?)area.Def?["rotY"] ?? 0) * Math.PI / 180;
                double lx = p.X * Math.Cos(yaw) - p.Z * Math.Sin(yaw), lz = p.X * Math.Sin(yaw) + p.Z * Math.Cos(yaw);
                return Math.Abs(lx) <= (float)size[0] / 2 && Math.Abs(lz) <= (float)size[1] / 2;
            }
            if (shape["polygon"] is Newtonsoft.Json.Linq.JObject poly && poly["points"] is Newtonsoft.Json.Linq.JArray pts && pts.Count >= 3)
            {
                bool inside = false;
                for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                {
                    float xi = (float)pts[i][0], zi = (float)pts[i][pts[i].Count() - 1], xj = (float)pts[j][0], zj = (float)pts[j][pts[j].Count() - 1];
                    if ((zi > p.Z) != (zj > p.Z) && p.X < (xj - xi) * (p.Z - zi) / (zj - zi) + xi) inside = !inside;
                }
                return inside;
            }
            return false;
        }
    }
}
