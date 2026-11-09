using System;
using System.Collections.Generic;
using System.Linq;

namespace PalaceMod.Builder.Model.Logic
{
    /// <summary>What an expression can see: the event's roles, bound roles (it, spawned), and the names, templates,
    /// statuses and variables the pack declares. A null set means "unknown here": that check is skipped.</summary>
    public sealed class CheckScope
    {
        /// <summary>The rule's event; null for component expressions evaluated at spawn (only self and globals).</summary>
        public EventSpec Event;
        /// <summary>The level script: self is the level.</summary>
        public bool LevelScript;
        public bool HasIt, HasSpawned;
        /// <summary>Roles beyond the standard ones (a damage modifier's attacker, victim, dist).</summary>
        public Dictionary<string, LType> ExtraRoles;

        public ISet<string> LevelVars;
        public ISet<string> SelfVars;
        public ISet<string> SpawnedVars;
        /// <summary>Every entity var any template declares (for other.$x, it.$x...).</summary>
        public ISet<string> AnyEntityVars;
        public IDictionary<string, ISet<string>> NamedVars;
        public ISet<string> Names;
        public ISet<string> Templates;
        public ISet<string> Statuses;

        public CheckScope With(Action<CheckScope> change)
        {
            var c = (CheckScope)MemberwiseClone();
            change(c);
            return c;
        }
    }

    public readonly struct ExprIssue(int col, string message, bool warning = false)
    {
        public readonly int Col = col;
        public readonly string Message = message;
        public readonly bool Warning = warning;
        public override string ToString() => $"col {Col + 1}: {Message}";
    }

    /// <summary>Static type checker for Expr trees (docs/builder-engine.md, "Expressions"). Sets ExprNode.Type and
    /// ExprNode.Filter, and reports errors with columns.</summary>
    public sealed class ExprChecker
    {
        private readonly CheckScope scope;
        public readonly List<ExprIssue> Issues = [];

        public ExprChecker(CheckScope scope) { this.scope = scope ?? new CheckScope(); }

        /// <summary>Parses and checks src; returns its type (Any on a parse error, which is reported).</summary>
        public static LType Check(string src, CheckScope scope, List<ExprIssue> issues, out ExprNode node)
        {
            node = Expr.Parse(src, out string err, out int col);
            if (node == null) { issues.Add(new ExprIssue(col, err)); return LType.Any; }
            var c = new ExprChecker(scope);
            var t = c.Visit(node);
            issues.AddRange(c.Issues);
            return t;
        }

        /// <summary>Parses and checks a filter string.</summary>
        public static FilterNode CheckFilter(string src, CheckScope scope, List<ExprIssue> issues)
        {
            var f = Filter.Parse(src, out string err, out int col);
            if (f == null) { issues.Add(new ExprIssue(col, err)); return null; }
            var c = new ExprChecker(scope);
            c.VisitFilter(f, 0);
            issues.AddRange(c.Issues);
            return f;
        }

        private void Err(ExprNode n, string msg) => Issues.Add(new ExprIssue(n?.Col ?? 0, msg));

        /// <summary>Whether a value of type actual can be used where expected is wanted (with the documented
        /// coercions: an entity is a position, a set counts in numbers, a template is named by text).</summary>
        public static bool Accepts(LType expected, LType actual)
        {
            if (expected == LType.Any || actual == LType.Any || expected == actual) return true;
            return expected switch
            {
                LType.Position => actual == LType.Entity,
                LType.Number => actual == LType.Set,
                LType.Set => actual == LType.Entity,
                LType.Template => actual == LType.Text,
                LType.Text => actual == LType.Template,
                LType.List => actual == LType.Set,
                _ => false,
            };
        }

        public LType Visit(ExprNode n)
        {
            n.Type = VisitInner(n);
            return n.Type;
        }

        private LType VisitInner(ExprNode n)
        {
            switch (n.Kind)
            {
                case ExprKind.Number: return LType.Number;
                case ExprKind.Bool: return LType.Bool;
                case ExprKind.Text: return LType.Text;
                case ExprKind.ListLit:
                    foreach (var a in n.Args) Visit(a);
                    return LType.List;
                case ExprKind.Role: return Role(n);
                case ExprKind.Global:
                    if (LogicSchema.Globals.TryGetValue(n.Name, out var g)) return g.Type;
                    if (scope.ExtraRoles != null && scope.ExtraRoles.TryGetValue(n.Name, out var xr)) return xr;
                    if (scope.LevelVars != null && scope.LevelVars.Contains(n.Name))
                        Err(n, $"unknown name \"{n.Name}\" (level variables need a $: \"${n.Name}\")");
                    else
                        Err(n, $"unknown name \"{n.Name}\"" + Suggest(n.Name, LogicSchema.Globals.Keys.Concat(Expr.Roles)));
                    return LType.Any;
                case ExprKind.LevelVar:
                    if (scope.LevelVars != null && !scope.LevelVars.Contains(n.Name))
                        Err(n, $"unknown variable \"${n.Name}\"" + Suggest(n.Name, scope.LevelVars, "$") + " (declare it in logic.vars)");
                    return LType.Any;
                case ExprKind.Named:
                    if (scope.Names != null && !scope.Names.Contains(n.Name))
                        Err(n, $"nothing is named \"@{n.Name}\"" + Suggest(n.Name, scope.Names, "@"));
                    return LType.Entity;
                case ExprKind.Template:
                    if (scope.Templates != null && !scope.Templates.Contains(n.Name) && !n.Name.StartsWith("vanilla:"))
                        Err(n, $"no template \"{n.Name}\"" + Suggest(n.Name, scope.Templates, "@@"));
                    return LType.Template;
                case ExprKind.Member: return Member(n);
                case ExprKind.Call: return Call(n);
                case ExprKind.Unary:
                {
                    var t = Visit(n.Args[0]);
                    if (n.Op == "not") { Want(n.Args[0], LType.Bool, t, "\"not\""); return LType.Bool; }
                    if (t == LType.Position || t == LType.Entity) return LType.Position;
                    Want(n.Args[0], LType.Number, t, "\"-\"");
                    return LType.Number;
                }
                case ExprKind.Binary: return Binary(n);
                case ExprKind.Cond:
                {
                    Want(n.Args[0], LType.Bool, Visit(n.Args[0]), "a condition");
                    var a = Visit(n.Args[1]);
                    var b = Visit(n.Args[2]);
                    if (a == b) return a;
                    if (a == LType.Any) return b;
                    if (b == LType.Any) return a;
                    if (Accepts(a, b)) return a;
                    if (Accepts(b, a)) return b;
                    Err(n, $"the two branches have different types ({LogicSchema.TypeName(a)} and {LogicSchema.TypeName(b)})");
                    return LType.Any;
                }
            }
            return LType.Any;
        }

        private void Want(ExprNode n, LType expected, LType actual, string what)
        {
            if (!Accepts(expected, actual)) Err(n, $"{what} needs a {LogicSchema.TypeName(expected)}, but \"{n}\" is a {LogicSchema.TypeName(actual)}");
        }

        private LType Role(ExprNode n)
        {
            string name = n.Name;
            var ev = scope.Event;
            if (scope.ExtraRoles != null && scope.ExtraRoles.TryGetValue(name, out var extra)) return extra;
            if (LogicSchema.RoleAliases.ContainsKey(name) && !Expr.Roles.Contains(name))
            {
                if (ev == null || !ev.Aliases.TryGetValue(name, out string role))
                {
                    Err(n, $"\"{name}\" is not defined for {(ev == null ? "this field" : $"the event \"{ev.Name}\"")}" +
                        (ev?.Other != null ? $" (other is the {ev.Other} here)" : ""));
                    return LType.Entity;
                }
                name = role;
            }
            switch (name)
            {
                case "self": return scope.LevelScript ? LType.Level : LType.Entity;
                case "king": case "castle": return LType.Entity;
                case "level": return LType.Level;
                case "subject":
                    if (ev == null) Err(n, "\"subject\" is only defined in a rule");
                    return LType.Entity;
                case "event":
                    if (ev == null) Err(n, "\"event\" is only defined in a rule");
                    return LType.Event;
                case "other":
                    if (ev == null) Err(n, "\"other\" is only defined in a rule");
                    else if (ev.Other == null && !(scope.ExtraRoles?.ContainsKey("other") ?? false))
                        Err(n, $"\"other\" is not defined for the event \"{ev.Name}\"");
                    return LType.Entity;
                case "spawned":
                    if (!scope.HasSpawned) Err(n, "\"spawned\" is only defined in a spawn, launch or replace action's then");
                    return LType.Entity;
                case "it":
                    if (!scope.HasIt) Err(n, "\"it\" is only defined inside where(), best(), forEach, targeting, or an action applied to a set");
                    return LType.Entity;
            }
            return LType.Entity;
        }

        private LType Member(ExprNode n)
        {
            var target = n.Target;
            var tt = Visit(target);
            if (n.Name.StartsWith("$"))
            {
                string v = n.Name.Substring(1);
                if (tt == LType.Level)
                {
                    if (scope.LevelVars != null && !scope.LevelVars.Contains(v))
                        Err(n, $"unknown variable \"${v}\"" + Suggest(v, scope.LevelVars, "$") + " (declare it in logic.vars)");
                    return LType.Any;
                }
                if (!Accepts(LType.Entity, tt)) { Err(n, $"only entities have variables, but \"{target}\" is a {LogicSchema.TypeName(tt)}"); return LType.Any; }
                ISet<string> known = null;
                string whose = target.ToString();
                if (target.Kind == ExprKind.Role && (target.Name == "self") && !scope.LevelScript) known = scope.SelfVars;
                else if (target.Kind == ExprKind.Role && target.Name == "spawned") known = scope.SpawnedVars ?? scope.AnyEntityVars;
                else if (target.Kind == ExprKind.Named && scope.NamedVars != null && scope.NamedVars.TryGetValue(target.Name, out var nv)) known = nv;
                else known = scope.AnyEntityVars;
                if (known != null && !known.Contains(v))
                    Err(n, $"unknown variable \"{whose}.${v}\"" + Suggest(v, known, "$") + (known == scope.AnyEntityVars ? " (no template declares it)" : " (declare it in vars)"));
                return LType.Any;
            }
            if (tt == LType.Any) return LType.Any;
            if (tt == LType.Event)
            {
                var ev = scope.Event;
                switch (n.Name)
                {
                    case "value":
                        if (ev != null && ev.Value == LType.Void && !(ev.Name == "signal")) { Err(n, $"the event \"{ev.Name}\" has no value"); return LType.Any; }
                        return ev?.Value ?? LType.Any;
                    case "pos": return LType.Position;
                    case "name": case "choice": case "result": case "types": case "weapon":
                        if (ev != null && !ev.Fields.ContainsKey(n.Name)) Err(n, $"the event \"{ev.Name}\" has no {n.Name}");
                        return ev != null && ev.Fields.TryGetValue(n.Name, out var ft) ? ft : n.Name == "types" ? LType.List : LType.Text;
                }
                Err(n, $"events have value, pos, name, choice, result, types and weapon, not \"{n.Name}\"");
                return LType.Any;
            }
            var on = tt == LType.Level ? LType.Level : tt;
            var p = LogicSchema.Property(on == LType.Set ? LType.Set : on, n.Name);
            if (p == null && tt == LType.Entity) p = null;
            if (p != null) return p.Returns;
            var options = LogicSchema.Properties.Where(x => x.On == on).Select(x => x.Name);
            Err(n, $"a {LogicSchema.TypeName(tt)} has no property \"{n.Name}\"" + Suggest(n.Name, options) +
                (LogicSchema.Functions.TryGetValue(n.Name, out var f) && f.Method ? $" (call it: .{n.Name}(...))" : ""));
            return LType.Any;
        }

        private LType Call(ExprNode n)
        {
            if (!LogicSchema.Functions.TryGetValue(n.Name, out var f))
            {
                Err(n, $"unknown function \"{n.Name}\"" + Suggest(n.Name, LogicSchema.Functions.Keys));
                foreach (var a in n.Args) Visit(a);
                if (n.Target != null) Visit(n.Target);
                return LType.Any;
            }
            if (n.Target != null)
            {
                var rt = Visit(n.Target);
                if (!f.Method) Err(n, $"\"{n.Name}\" isn't a method; call it as {n.Name}(...)");
                else if (f.OnlyOn is LType only && !Accepts(only, rt) && !(only == LType.List && rt == LType.Set))
                    Err(n, $".{n.Name}() works on a {LogicSchema.TypeName(only)}, but \"{n.Target}\" is a {LogicSchema.TypeName(rt)}");
                else if (f.OnlyOn == null && !Accepts(LType.Position, rt))
                    Err(n, $".{n.Name}() needs an entity or position to anchor at, but \"{n.Target}\" is a {LogicSchema.TypeName(rt)}");
            }
            else if (f.OnlyOn != null)
            {
                Err(n, $"\"{n.Name}\" is a method: call it on a {LogicSchema.TypeName(f.OnlyOn.Value)}, e.g. self.{n.Name}(...)");
            }
            var args = f.Args;
            int min = f.MinArgs, max = args.Contains("...") ? int.MaxValue : args.Length;
            if (n.Name == "dist" && n.Target != null) { min = 1; max = 1; }
            if (n.Name == "rand" && n.Args.Length == 1) Err(n, "rand takes no arguments (0..1) or two (a..b)");
            if (n.Args.Length < min || n.Args.Length > max)
                Err(n, $"{n.Name} takes {(min == max ? min.ToString() : max == int.MaxValue ? $"at least {min}" : $"{min} to {max}")} argument{(max == 1 ? "" : "s")}, got {n.Args.Length}");
            LType first = LType.Any;
            for (int i = 0; i < n.Args.Length; i++)
            {
                string spec = i < args.Length ? args[i] : args.LastOrDefault(a => a != "...") ?? "any";
                if (spec == "...") spec = args[Math.Max(0, Array.IndexOf(args, "...") - 1)];
                spec = spec.TrimEnd('?');
                var a = n.Args[i];
                if (n.Name == "dist" && n.Target != null) spec = "position";
                switch (spec)
                {
                    case "filter":
                    {
                        var t = Visit(a);
                        Want(a, LType.Text, t, n.Name + "'s filter");
                        if (a.Kind == ExprKind.Text)
                        {
                            var fn = Filter.Parse((string)a.Literal, out string err, out int col);
                            if (fn == null) Issues.Add(new ExprIssue(a.Col + 1 + col, "filter: " + err));
                            else { VisitFilter(fn, a.Col + 1); a.Filter = fn; }
                        }
                        break;
                    }
                    case "lazy":
                    {
                        var inner = new ExprChecker(scope.With(s => s.HasIt = true));
                        Want(a, LType.Number, inner.Visit(a), n.Name + "'s score");
                        Issues.AddRange(inner.Issues);
                        break;
                    }
                    case "status":
                    {
                        var t = Visit(a);
                        Want(a, LType.Text, t, "a status id");
                        if (a.Kind == ExprKind.Text && scope.Statuses != null && !scope.Statuses.Contains((string)a.Literal))
                            Err(a, $"no status \"{a.Literal}\"" + Suggest((string)a.Literal, scope.Statuses));
                        break;
                    }
                    default:
                    {
                        var t = Visit(a);
                        if (i == 0) first = t;
                        var want = spec switch
                        {
                            "number" => LType.Number, "position" => LType.Position, "entity" => LType.Entity, "text" => LType.Text,
                            "bool" => LType.Bool, _ => LType.Any,
                        };
                        Want(a, want, t, $"{n.Name}'s argument {i + 1}");
                        break;
                    }
                }
            }
            if (n.Name == "lerp") return first == LType.Position || first == LType.Entity ? LType.Position : LType.Number;
            if (n.Name == "at" && n.Target?.Type == LType.Set) return LType.Entity;
            return f.Returns;
        }

        private LType Binary(ExprNode n)
        {
            var a = Visit(n.Args[0]);
            var b = Visit(n.Args[1]);
            switch (n.Op)
            {
                case "and": case "or":
                    Want(n.Args[0], LType.Bool, a, $"\"{n.Op}\"");
                    Want(n.Args[1], LType.Bool, b, $"\"{n.Op}\"");
                    return LType.Bool;
                case "==": case "!=": return LType.Bool;
                case "<": case "<=": case ">": case ">=":
                    Want(n.Args[0], LType.Number, a, $"\"{n.Op}\"");
                    Want(n.Args[1], LType.Number, b, $"\"{n.Op}\"");
                    return LType.Bool;
                case "+":
                    if (a == LType.Text || b == LType.Text) return LType.Text;
                    goto case "-";
                case "-":
                    if (IsPos(a) || IsPos(b))
                    {
                        if (!(IsPos(a) || a == LType.Any || a == LType.Entity) || !(IsPos(b) || b == LType.Any || b == LType.Entity))
                            Err(n, $"\"{n.Op}\" needs two positions or two numbers ({LogicSchema.TypeName(a)} {n.Op} {LogicSchema.TypeName(b)})");
                        return LType.Position;
                    }
                    Want(n.Args[0], LType.Number, a, $"\"{n.Op}\"");
                    Want(n.Args[1], LType.Number, b, $"\"{n.Op}\"");
                    return LType.Number;
                case "*": case "/":
                    if (a == LType.Position && b != LType.Position) { Want(n.Args[1], LType.Number, b, $"\"{n.Op}\""); return LType.Position; }
                    if (b == LType.Position && n.Op == "*") { Want(n.Args[0], LType.Number, a, "\"*\""); return LType.Position; }
                    Want(n.Args[0], LType.Number, a, $"\"{n.Op}\"");
                    Want(n.Args[1], LType.Number, b, $"\"{n.Op}\"");
                    return LType.Number;
                default:
                    Want(n.Args[0], LType.Number, a, $"\"{n.Op}\"");
                    Want(n.Args[1], LType.Number, b, $"\"{n.Op}\"");
                    return LType.Number;
            }
        }

        private static bool IsPos(LType t) => t == LType.Position;

        // ---- Filters ------------------------------------------------------------------------------------------

        private static readonly HashSet<string> PlainTerms = new(LogicSchema.FilterTerms.Keys.Where(k => k.All(c => char.IsLetter(c))), StringComparer.Ordinal);

        public void VisitFilter(FilterNode f, int baseCol)
        {
            if (f == null) return;
            if (f.Kind != FilterKind.Term) { foreach (var a in f.Args) VisitFilter(a, baseCol); return; }
            int col = baseCol + f.Col;
            switch (f.Term)
            {
                case "label": return;
                case "where":
                {
                    var inner = new ExprChecker(scope.With(s => s.HasIt = true));
                    var t = inner.Visit(f.Where);
                    foreach (var i in inner.Issues) Issues.Add(new ExprIssue(col + 6 + i.Col, "in where(...): " + i.Message));
                    if (!Accepts(LType.Bool, t)) Issues.Add(new ExprIssue(col, $"where(...) needs a bool, but \"{f.Where}\" is a {LogicSchema.TypeName(t)}"));
                    return;
                }
                case "is":
                    if (scope.Templates != null && !scope.Templates.Contains(f.Arg) && !f.Arg.StartsWith("vanilla"))
                        Issues.Add(new ExprIssue(col, $"no template \"{f.Arg}\" (in is:{f.Arg})" + Suggest(f.Arg, scope.Templates)));
                    return;
                case "has":
                    if (scope.Statuses != null && !scope.Statuses.Contains(f.Arg))
                        Issues.Add(new ExprIssue(col, $"no status \"{f.Arg}\" (in has:{f.Arg})" + Suggest(f.Arg, scope.Statuses)));
                    return;
                case "in":
                    if (f.Arg == "self") return;
                    if (!f.Arg.StartsWith("@")) { Issues.Add(new ExprIssue(col, $"in: takes @name or self, not \"{f.Arg}\"")); return; }
                    if (scope.Names != null && !scope.Names.Contains(f.Arg.Substring(1)))
                        Issues.Add(new ExprIssue(col, $"nothing is named \"{f.Arg}\"" + Suggest(f.Arg.Substring(1), scope.Names, "@")));
                    return;
                case "type": return;
            }
            if (PlainTerms.Contains(f.Term) || LogicSchema.Tags.Contains(f.Term)) return;
            var lower = LogicSchema.Tags.FirstOrDefault(t => string.Equals(t, f.Term, StringComparison.OrdinalIgnoreCase));
            Issues.Add(new ExprIssue(col, $"unknown filter term \"{f.Term}\"" +
                (lower != null ? $" (game tags are case-sensitive: \"{lower}\")" : Suggest(f.Term, PlainTerms.Concat(LogicSchema.Tags))) +
                (Expr.Roles.Contains(f.Term) && f.Term != "self" && f.Term != "king" && f.Term != "castle" ? " (filters test each candidate; use where(it == " + f.Term + "))" : "")));
        }

        // ---- Suggestions --------------------------------------------------------------------------------------

        public static string Suggest(string name, IEnumerable<string> options, string prefix = "")
        {
            if (name == null || options == null) return "";
            string best = null;
            int bestD = int.MaxValue;
            foreach (var o in options)
            {
                if (o == null) continue;
                int d = Distance(name.ToLowerInvariant(), o.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = o; }
            }
            return best != null && bestD <= Math.Max(1, name.Length / 3) ? $" (did you mean \"{prefix}{best}\"?)" : "";
        }

        public static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }
    }
}
