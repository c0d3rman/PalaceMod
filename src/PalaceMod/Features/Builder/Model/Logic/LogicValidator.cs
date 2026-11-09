using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PalaceMod.Builder.Model.Logic
{
    /// <summary>
    /// Validator.ValidateLogic (docs/builder-engine.md, "Validation and errors"): checks every rule, action, component,
    /// status, preset use and expression in a pack against LogicSchema, with JSON paths (and a column for expressions)
    /// so the Studio's Problems popup can open the right card. Runs on the preset-expanded pack; rules from a preset
    /// carry the path of their use.
    /// </summary>
    public static class LogicValidator
    {
        public static void Validate(DesignPack pack, IValidationContext game, List<ValidationIssue> issues)
        {
            if (pack == null || !HasLogic(pack)) return;
            var expanded = Presets.Expand(pack, issues);
            if (expanded == null) return;
            new Walker(pack, expanded, game, issues).Run();
        }

        /// <summary>Whether the pack uses the behaviour engine at all (rules, logic, templates, statuses or presets).</summary>
        public static bool HasLogic(DesignPack pack) =>
            (pack.Entities?.Count ?? 0) > 0 || (pack.Statuses?.Count ?? 0) > 0 || (pack.Presets?.Count ?? 0) > 0 ||
            (pack.Units ?? []).Any(u => u != null && (u.Rules != null || u.Vars != null || u.Labels != null || u.Targeting != null || u.Movement != null || u.Attacks != null || u.Statuses != null || u.Counterpart != null)) ||
            (pack.Levels ?? []).Any(l => l?.Logic != null);

        private static readonly Regex VarName = new("^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private sealed class Owner
        {
            public string Path;
            /// <summary>unit, prop, projectile, area, pickup, logic, path, point, building, line, level, status.</summary>
            public string Kind;
            public ISet<string> Vars;
            public bool LevelScript;
            public LevelCtx Level;
            public List<RuleDef> Rules;
        }

        private sealed class LevelCtx
        {
            public string Path;
            public LevelDef Def;
            public HashSet<string> Vars = new(StringComparer.Ordinal);
            public HashSet<string> Names = new(StringComparer.Ordinal);
            public Dictionary<string, ISet<string>> NamedVars = new(StringComparer.Ordinal);
        }

        private sealed class Walker
        {
            private readonly DesignPack original, pack;
            private readonly IValidationContext game;
            private readonly List<ValidationIssue> issues;
            private readonly HashSet<string> units = new(StringComparer.Ordinal), entities = new(StringComparer.Ordinal), statuses = new(StringComparer.Ordinal);
            private readonly HashSet<string> templates = new(StringComparer.Ordinal);
            private readonly HashSet<string> anyEntityVars = new(StringComparer.Ordinal);
            private readonly HashSet<string> allLevelVars = new(StringComparer.Ordinal);
            private readonly HashSet<string> ruleIds = new(StringComparer.Ordinal);
            private readonly Dictionary<string, ISet<string>> templateVars = new(StringComparer.Ordinal);
            private readonly List<LevelCtx> levels = [];
            /// <summary>For the loop check: (owner path, listened signal or null, signals sent without a wait).</summary>
            private readonly List<(string Path, string Hears, HashSet<string> Sends)> signalEdges = [];

            public Walker(DesignPack original, DesignPack pack, IValidationContext game, List<ValidationIssue> issues)
            {
                this.original = original; this.pack = pack; this.game = game; this.issues = issues;
            }

            private void Error(string path, string msg) => issues.Add(new ValidationIssue(Severity.Error, path, msg));
            private void Warn(string path, string msg) => issues.Add(new ValidationIssue(Severity.Warning, path, msg));

            public void Run()
            {
                Collect();
                for (int i = 0; i < (pack.Units?.Count ?? 0); i++)
                {
                    var u = pack.Units[i];
                    if (u == null) continue;
                    string p = $"units[{i}]";
                    var owner = new Owner { Path = p, Kind = "unit", Vars = templateVars.TryGetValue(u.Id ?? "", out var tv) ? tv : null, Rules = u.Rules };
                    CheckLabels(u.Labels, p + ".labels");
                    CheckVarDecls(u.Vars, p + ".vars");
                    CheckTargeting(u.Targeting, p + ".targeting", owner);
                    CheckMovement(u.Movement, p + ".movement", owner);
                    CheckAttacks(u.Attacks, p + ".attacks", owner);
                    CheckStatusRefs(u.Statuses, p + ".statuses");
                    if (u.Counterpart != null) CheckTemplate(u.Counterpart, p + ".counterpart", Scope(owner, null));
                    CheckRules(owner, LogicLowering.LoweredRuleIndex(u, LogicLowering.IsUnitIn(pack)));
                }
                for (int i = 0; i < (pack.Entities?.Count ?? 0); i++)
                {
                    var e = pack.Entities[i];
                    if (e == null) { Error($"entities[{i}]", "is empty"); continue; }
                    CheckTemplateFields(e, $"entities[{i}]", templateVars.TryGetValue(e.Id ?? "", out var tv) ? tv : null, null, place: false);
                }
                for (int i = 0; i < (pack.Statuses?.Count ?? 0); i++) CheckStatus(pack.Statuses[i], $"statuses[{i}]");
                for (int i = 0; i < (pack.Presets?.Count ?? 0); i++) CheckPresetDef(pack.Presets[i], $"presets[{i}]");
                foreach (var lv in levels) CheckLevel(lv);
                CheckLoops();
            }

            // ---- Declarations ---------------------------------------------------------------------------------

            private void Collect()
            {
                foreach (var u in pack.Units ?? [])
                {
                    if (u?.Id == null) continue;
                    units.Add(u.Id);
                    templateVars[u.Id] = VarSet(u.Vars);
                }
                for (int i = 0; i < (pack.Entities?.Count ?? 0); i++)
                {
                    var e = pack.Entities[i];
                    string p = $"entities[{i}].id";
                    if (e == null) continue;
                    if (!Ids.IsValid(e.Id)) { Error(p, "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'"); continue; }
                    if (units.Contains(e.Id)) Error(p, $"\"{e.Id}\" is also a unit's id; templates share one namespace");
                    else if (!entities.Add(e.Id)) Error(p, $"\"{e.Id}\" is used twice");
                    templateVars[e.Id] = VarSet(e.Vars);
                }
                templates.UnionWith(units);
                templates.UnionWith(entities);
                for (int i = 0; i < (pack.Statuses?.Count ?? 0); i++)
                {
                    var s = pack.Statuses[i];
                    if (s == null) continue;
                    if (!Ids.IsValid(s.Id)) Error($"statuses[{i}].id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                    else if (!statuses.Add(s.Id)) Error($"statuses[{i}].id", $"\"{s.Id}\" is used twice");
                }
                var presetIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < (original.Presets?.Count ?? 0); i++)
                {
                    var pr = original.Presets[i];
                    if (pr == null) continue;
                    if (!Ids.IsValid(pr.Id)) Error($"presets[{i}].id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                    else if (!presetIds.Add(pr.Id)) Error($"presets[{i}].id", $"\"{pr.Id}\" is used twice");
                }
                foreach (var set in templateVars.Values) anyEntityVars.UnionWith(set);
                foreach (var r in AllRules()) if (r?.Id != null) ruleIds.Add(r.Id);

                for (int i = 0; i < (pack.Levels?.Count ?? 0); i++)
                {
                    var l = pack.Levels[i];
                    if (l?.Logic == null) continue;
                    var ctx = new LevelCtx { Path = $"levels[{i}].logic", Def = l };
                    foreach (var k in l.Logic.Vars?.Keys ?? Enumerable.Empty<string>()) ctx.Vars.Add(k);
                    allLevelVars.UnionWith(ctx.Vars);
                    foreach (var place in l.Logic.Places ?? [])
                    {
                        if (place == null) continue;
                        var vars = VarSet(place.Vars);
                        if (place.Entity != null && templateVars.TryGetValue(place.Entity, out var tv)) vars.UnionWith(tv);
                        anyEntityVars.UnionWith(vars);
                        foreach (var n in new[] { place.Id, place.Name }.Where(n => n != null))
                        {
                            ctx.Names.Add(n);
                            ctx.NamedVars[n] = vars;
                        }
                    }
                    foreach (var kv in l.Logic.Attach ?? [])
                    {
                        var vars = VarSet(kv.Value?.Vars);
                        anyEntityVars.UnionWith(vars);
                        if (kv.Value?.Name != null) { ctx.Names.Add(kv.Value.Name); ctx.NamedVars[kv.Value.Name] = vars; }
                    }
                    levels.Add(ctx);
                }
            }

            private static HashSet<string> VarSet(Dictionary<string, JToken> vars) => new(vars?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            private IEnumerable<RuleDef> AllRules()
            {
                foreach (var u in pack.Units ?? []) foreach (var r in u?.Rules ?? []) yield return r;
                foreach (var e in pack.Entities ?? []) foreach (var r in e?.Rules ?? []) yield return r;
                foreach (var s in pack.Statuses ?? []) foreach (var r in s?.Rules ?? []) yield return r;
                foreach (var l in pack.Levels ?? [])
                {
                    if (l?.Logic == null) continue;
                    foreach (var r in l.Logic.Rules ?? []) yield return r;
                    foreach (var p in l.Logic.Places ?? []) foreach (var r in p?.Rules ?? []) yield return r;
                    foreach (var a in l.Logic.Attach ?? []) foreach (var r in a.Value?.Rules ?? []) yield return r;
                }
            }

            private void CheckVarDecls(Dictionary<string, JToken> vars, string path)
            {
                foreach (var kv in vars ?? [])
                {
                    string p = $"{path}.{kv.Key}";
                    if (!VarName.IsMatch(kv.Key)) Error(p, "variable names use letters, digits and '_' and don't start with a digit");
                    var v = kv.Value;
                    switch (v?.Type)
                    {
                        case JTokenType.Integer: case JTokenType.Float: case JTokenType.Boolean: case JTokenType.Array: break;
                        case JTokenType.String:
                        {
                            var list = new List<ExprIssue>();
                            ExprChecker.Check((string)v, new CheckScope(), list, out _);
                            Report(p, list);
                            break;
                        }
                        case JTokenType.Object:
                        {
                            string t = (string)v["type"];
                            if (!new[] { "number", "bool", "text", "position", "entity", "set", "list", "template" }.Contains(t))
                                Error(p + ".type", "must be number, bool, text, position, entity, set, list or template");
                            break;
                        }
                        default: Error(p, "start value must be a number, bool, \"expression\", [] or {\"type\", \"value\"}"); break;
                    }
                }
            }

            private void CheckLabels(List<string> labels, string path)
            {
                for (int i = 0; i < (labels?.Count ?? 0); i++)
                    if (string.IsNullOrWhiteSpace(labels[i]) || labels[i].Any(c => char.IsWhiteSpace(c) || c == '#'))
                        Error($"{path}[{i}]", "a label is one word without '#' (filters write it #label)");
            }

            private void CheckStatusRefs(List<string> refs, string path)
            {
                for (int i = 0; i < (refs?.Count ?? 0); i++)
                    if (!statuses.Contains(refs[i])) Error($"{path}[{i}]", $"no status \"{refs[i]}\"" + ExprChecker.Suggest(refs[i], statuses));
            }

            // ---- Scopes ---------------------------------------------------------------------------------------

            private CheckScope Scope(Owner owner, EventSpec ev) => new()
            {
                Event = ev,
                LevelScript = owner.LevelScript,
                LevelVars = owner.Level != null ? owner.Level.Vars : pack.Levels?.Any(l => l?.Logic != null) == true ? allLevelVars : null,
                SelfVars = owner.LevelScript ? null : owner.Vars ?? (owner.Kind == "status" ? anyEntityVars : null),
                AnyEntityVars = anyEntityVars,
                Names = owner.Level?.Names ?? (levels.Count > 0 ? new HashSet<string>(levels.SelectMany(l => l.Names)) : null),
                NamedVars = owner.Level?.NamedVars,
                Templates = templates,
                Statuses = statuses,
            };

            private void Report(string path, List<ExprIssue> list)
            {
                foreach (var i in list)
                    issues.Add(new ValidationIssue(i.Warning ? Severity.Warning : Severity.Error, path, $"col {i.Col + 1}: {i.Message}"));
            }

            private LType Expression(string src, string path, CheckScope scope, LType want, string what = null)
            {
                var list = new List<ExprIssue>();
                var t = ExprChecker.Check(src, scope, list, out var node);
                Report(path, list);
                if (node != null && list.Count == 0 && !ExprChecker.Accepts(want, t) && !(want == LType.Set && t == LType.Level))
                    Error(path, $"{what ?? "this"} needs a {LogicSchema.TypeName(want)}, but \"{src}\" is a {LogicSchema.TypeName(t)}");
                return t;
            }

            private void FilterString(string src, string path, CheckScope scope)
            {
                var list = new List<ExprIssue>();
                ExprChecker.CheckFilter(src, scope, list);
                Report(path, list);
            }

            private void Text(string src, string path, CheckScope scope)
            {
                if (src == null) return;
                if (src.StartsWith("=")) { Expression(src.Substring(1), path, scope, LType.Any); return; }
                var parts = Expr.ParseInterp(src, out string err, out int col);
                if (parts == null) { Error(path, $"col {col + 1}: {err}"); return; }
                foreach (var node in parts.OfType<ExprNode>())
                {
                    var c = new ExprChecker(scope);
                    c.Visit(node);
                    Report(path, c.Issues);
                }
            }

            // ---- Rules ----------------------------------------------------------------------------------------

            private void CheckRules(Owner owner, int skipIndex = -1)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < (owner.Rules?.Count ?? 0); i++)
                {
                    var r = owner.Rules[i];
                    string p = r?.Extra != null && r.Extra.TryGetValue(Presets.SourceField, out var src) ? (string)src : $"{owner.Path}.rules[{i}]";
                    if (r == null) { Error(p, "is empty"); continue; }
                    if (r.Id != null && !ids.Add(r.Id)) Warn(p + ".id", $"another rule here has the id \"{r.Id}\" (enableRule/disableRule act on both)");
                    if (i == skipIndex) continue; // checked as a unit ref by Validator
                    CheckRule(r, p, owner);
                }
            }

            private void CheckRule(RuleDef r, string p, Owner owner)
            {
                if (r.Preset != null) return; // reported by Presets.Expand
                if (r.When?.On == null) { Error(p + ".when", "needs an event (\"when\": \"death\" or {\"on\": ...})"); return; }
                if (!LogicSchema.Events.TryGetValue(r.When.On, out var ev))
                {
                    Error(p + ".when", $"unknown event \"{r.When.On}\"" + ExprChecker.Suggest(r.When.On, LogicSchema.Events.Keys) + " (made with a newer PalaceMod?)");
                    return;
                }
                var scope = Scope(owner, ev);
                var w = r.When;
                if (w.Of != null) FilterString(w.Of, p + ".when.of", scope);
                // "range" is the `of` listener's radius, except on events that take a range param themselves (near).
                bool ownRange = ev.Params.Any(x => x.Name == "range");
                if (w.Range != null) Num(w.Range, p + ".when.range", scope, 0, null);
                if (w.Range != null && w.Of == null && !ownRange) Warn(p + ".when.range", "range only applies with \"of\"");
                foreach (var prop in w.Params.Properties())
                {
                    var spec = ev.Params.FirstOrDefault(x => x.Name == prop.Name);
                    if (spec == null) { Warn($"{p}.when.{prop.Name}", $"the event \"{ev.Name}\" has no param \"{prop.Name}\"" + ExprChecker.Suggest(prop.Name, ev.Params.Select(x => x.Name))); continue; }
                    Param(spec, prop.Value, $"{p}.when.{prop.Name}", scope, owner, null);
                }
                foreach (var spec in ev.Params.Where(x => x.Required && w.Params[x.Name] == null && !(x.Name == "range" && w.Range != null)))
                    Error(p + ".when", $"the event \"{ev.Name}\" needs \"{spec.Name}\" ({spec.Description})");
                if (ev.Name == "every" && w.Params["seconds"] is JValue sec && (sec.Type == JTokenType.Integer || sec.Type == JTokenType.Float) && (double)sec < 0.1)
                    Warn(p + ".when.seconds", "every is clamped to 0.1 s");
                if (owner.LevelScript && !ev.Global && w.Of == null)
                    Error(p + ".when", $"the level script hears \"{ev.Name}\" only from entities: give \"of\" (e.g. {{\"on\": \"{ev.Name}\", \"of\": \"enemy\"}})");
                else if (!owner.LevelScript && w.Of == null && !ev.Owners.Contains("any") && !ev.Owners.Contains(owner.Kind) && owner.Kind != "status")
                    Warn(p + ".when", $"\"{ev.Name}\" fires on {string.Join("/", ev.Owners)} entities, not on this {owner.Kind}; add \"of\" to hear others'");
                if (ev.Name == "kingAction" && owner.Level != null && w.Params["action"] is JValue ka && ka.Type == JTokenType.String &&
                    !(owner.Level.Def.Logic.KingActions ?? []).Any(k => k?.Id == (string)ka))
                    Error(p + ".when.action", $"no king action \"{ka}\" in logic.kingActions");
                if (ev.Name == "varChanged" && w.Params["name"] is JValue vn && vn.Type == JTokenType.String)
                {
                    var known = owner.LevelScript ? scope.LevelVars : owner.Vars;
                    string name = ((string)vn).TrimStart('$');
                    if (known != null && !known.Contains(name)) Error(p + ".when.name", $"unknown variable \"{name}\"" + ExprChecker.Suggest(name, known));
                }
                foreach (var (c, idx) in r.Conditions().Select((c, idx) => (c, idx)))
                    Expression(c, r.If?.Type == JTokenType.Array ? $"{p}.if[{idx}]" : p + ".if", scope, LType.Bool, "a condition");
                if (r.If != null && r.If.Type != JTokenType.String && r.If.Type != JTokenType.Array && r.If.Type != JTokenType.Boolean)
                    Error(p + ".if", "must be an expression or a list of expressions");
                if (r.Cooldown is double cd && cd < 0) Error(p + ".cooldown", "must be 0 or more");
                if (r.Do == null || r.Do.Count == 0) Warn(p + ".do", "does nothing");
                var sends = new HashSet<string>(StringComparer.Ordinal);
                Actions(r.Do, p + ".do", scope, owner, sends);
                signalEdges.Add((p, ev.Name == "signal" ? (string)w.Params["name"] : null, sends));
            }

            private void Actions(List<ActionDef> list, string path, CheckScope scope, Owner owner, HashSet<string> sends)
            {
                bool waited = false;
                for (int i = 0; i < (list?.Count ?? 0); i++)
                {
                    var a = list[i];
                    string p = $"{path}[{i}]";
                    if (a == null) { Error(p, "is empty"); continue; }
                    if (a.Do == "wait") waited = true;
                    Action(a, p, scope, owner, waited ? null : sends);
                }
            }

            private void Action(ActionDef a, string p, CheckScope scope, Owner owner, HashSet<string> sends)
            {
                if (a.Do == null) { Error(p, "needs \"do\": the action's name"); return; }
                if (!LogicSchema.Actions.TryGetValue(a.Do, out var spec))
                {
                    Error(p + ".do", $"unknown action \"{a.Do}\"" + ExprChecker.Suggest(a.Do, LogicSchema.Actions.Keys) + " (made with a newer PalaceMod?)");
                    return;
                }
                foreach (var kv in a.Params ?? new Dictionary<string, JToken>())
                    if (kv.Key != Presets.SourceField && spec.Param(kv.Key) == null)
                        Warn($"{p}.{kv.Key}", $"\"{a.Do}\" has no param \"{kv.Key}\"" + ExprChecker.Suggest(kv.Key, spec.Params.Select(x => x.Name)));
                foreach (var ps in spec.Params.Where(x => x.Required && a.Get(x.Name) == null))
                    Error(p, $"\"{a.Do}\" needs \"{ps.Name}\" ({ps.Description})");
                // Params of an action applied to a set are evaluated per member, with `it` bound.
                var inner = a.Get("target") != null && spec.Targeted ? scope.With(s => s.HasIt = true) : scope;
                foreach (var ps in spec.Params)
                {
                    var v = a.Get(ps.Name);
                    if (v == null || v.Type == JTokenType.Null) continue;
                    Param(ps, v, $"{p}.{ps.Name}", ps.Name == "target" ? scope : inner, owner, a, sends);
                }
                switch (a.Do)
                {
                    case "setStat":
                    {
                        int n = new[] { "value", "add", "mult" }.Count(k => a.Get(k) != null);
                        if (n != 1) Error(p, "setStat needs exactly one of value, add or mult");
                        break;
                    }
                    case "signal":
                        if (sends != null && a.GetString("name") is string sig) sends.Add(sig);
                        break;
                    case "launch":
                    {
                        string e = a.GetString("entity");
                        if (e != null && !e.StartsWith("=") && pack.FindEntity(e) is { } ent && ent.Projectile == null)
                            Warn(p + ".entity", $"\"{e}\" has no projectile block, so it flies with the defaults");
                        break;
                    }
                    case "drift":
                        if (a.Get("dir") == null && a.Get("toward") == null && a.Get("from") == null) Error(p, "drift needs dir, toward or from");
                        break;
                    case "setTargeting":
                        if (a.Get("targeting") == null && a.Get("prepend") == null && a.Get("remove") == null) Error(p, "setTargeting needs targeting, prepend or remove");
                        break;
                    case "listRemove":
                        if (a.Get("value") == null && a.Get("index") == null) Error(p, "listRemove needs value or index");
                        break;
                    case "waves":
                    {
                        string op = a.GetString("op");
                        if (op == "addSpawn" && (a.Get("unit") == null)) Error(p, "addSpawn needs \"unit\"");
                        if (op is "scaleCount" or "scaleHp" && a.Get("value") == null) Error(p, $"{op} needs \"value\"");
                        if (op == "removeUnit" && a.Get("unit") == null) Error(p, "removeUnit needs \"unit\"");
                        break;
                    }
                    case "building":
                    {
                        string op = a.GetString("op");
                        if (op is "setCost" or "setIncome" && a.Get("value") == null) Error(p, $"{op} needs \"value\"");
                        break;
                    }
                }
            }

            private void Param(ParamSpec ps, JToken v, string p, CheckScope scope, Owner owner, ActionDef action, HashSet<string> sends = null)
            {
                bool isString = v.Type == JTokenType.String;
                string s = isString ? (string)v : null;
                switch (ps.Type)
                {
                    case "number": case "int":
                        if (isString) Expression(s.StartsWith("=") ? s.Substring(1) : s, p, scope, LType.Number, ps.Name);
                        else if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)
                        {
                            double d = (double)v;
                            if (ps.Min is double lo && d < lo) { if (ps.Name is "seconds" or "every" or "times") Warn(p, $"clamped to {lo.ToString(CultureInfo.InvariantCulture)}"); else Error(p, $"must be at least {lo.ToString(CultureInfo.InvariantCulture)}"); }
                            if (ps.Max is double hi && d > hi) { if (ps.Name is "times" or "count" or "limit") Warn(p, $"clamped to {hi.ToString(CultureInfo.InvariantCulture)}"); else Error(p, $"must be at most {hi.ToString(CultureInfo.InvariantCulture)}"); }
                            if (ps.Type == "int" && d != Math.Floor(d)) Error(p, "must be a whole number");
                        }
                        else Error(p, "must be a number or an expression");
                        break;
                    case "bool":
                        if (isString) Expression(s, p, scope, LType.Bool, ps.Name);
                        else if (v.Type != JTokenType.Boolean) Error(p, "must be true, false or an expression");
                        break;
                    case "text":
                        if (isString) Text(s, p, scope);
                        else if (v.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Boolean) { }
                        else Error(p, "must be text");
                        break;
                    case "position":
                        if (isString) Expression(s, p, scope, LType.Position, ps.Name);
                        else if (v is JArray arr) { if (arr.Count < 2 || arr.Count > 3 || arr.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float)) Error(p, "must be [x, z] or [x, y, z]"); }
                        else Error(p, "must be an expression (an entity or vec(...)) or [x, y, z]");
                        break;
                    case "entity":
                        if (isString) Expression(s, p, scope, LType.Entity, ps.Name);
                        else Error(p, "must be a selector expression (self, other, @name, nearest('foe', 10)...)");
                        break;
                    case "entities":
                        if (!isString) { Error(p, "must be a selector expression"); break; }
                        if (action?.Do == "signal" && s == "all") break;
                        Expression(s, p, scope, LType.Set, ps.Name);
                        break;
                    case "template":
                        if (!isString) { Error(p, "must be a template id or \"=expression\""); break; }
                        CheckTemplate(s, p, scope);
                        break;
                    case "filter":
                        if (!isString) { Error(p, "must be a filter string"); break; }
                        FilterString(s, p, scope);
                        break;
                    case "status":
                        if (!isString) { Error(p, "must be a status id"); break; }
                        if (s.StartsWith("=")) Expression(s.Substring(1), p, scope, LType.Text);
                        else if (!statuses.Contains(s)) Error(p, $"no status \"{s}\"" + ExprChecker.Suggest(s, statuses) + " (add it to the pack's statuses)");
                        break;
                    case "rule":
                        if (!isString) { Error(p, "must be a rule id"); break; }
                        if (!ruleIds.Contains(s)) Warn(p, $"no rule has the id \"{s}\"" + ExprChecker.Suggest(s, ruleIds));
                        break;
                    case "var":
                    {
                        if (!isString) { Error(p, "must be a variable name"); break; }
                        string name = s.TrimStart('$');
                        string target = action?.GetString("target");
                        bool level = s.StartsWith("$") || target == "level" || owner.LevelScript && target == null;
                        ISet<string> known = level ? scope.LevelVars : target == null || target == "self" ? owner.Vars ?? (owner.Kind == "status" ? anyEntityVars : null) : anyEntityVars;
                        if (!VarName.IsMatch(name)) Error(p, "variable names use letters, digits and '_'");
                        else if (known != null && !known.Contains(name))
                            Error(p, $"unknown variable \"{(level ? "$" : "")}{name}\"" + ExprChecker.Suggest(name, known) + (level ? " (declare it in logic.vars)" : " (declare it in vars)"));
                        break;
                    }
                    case "signal": case "label":
                        if (!isString || string.IsNullOrWhiteSpace(s)) Error(p, $"must be a {ps.Type} name");
                        break;
                    case "enum":
                    {
                        var opts = action?.Do == "setStat" && ps.Name == "stat" ? LogicSchema.Stats.Keys.ToArray() : ps.Options;
                        string val = isString ? s : v.Type == JTokenType.Boolean ? ((bool)v ? "true" : "false") : null;
                        if (val == null || opts != null && !opts.Contains(val))
                            Error(p, $"must be one of {string.Join(", ", opts ?? [])}" + (val != null ? ExprChecker.Suggest(val, opts) : ""));
                        break;
                    }
                    case "actions":
                    {
                        if (v is not JArray va) { Error(p, "must be a list of actions"); break; }
                        var list = ActionList(va);
                        var inner = scope;
                        if (ps.Binds == "spawned")
                        {
                            string ent = action?.GetString("entity") ?? action?.GetString("with");
                            ISet<string> vars = ent != null && !ent.StartsWith("=") && templateVars.TryGetValue(ContentRef.Parse(ent).Id ?? ent, out var tv) ? tv : null;
                            if (ent != null && ent.StartsWith("=")) vars = anyEntityVars;
                            inner = scope.With(x => { x.HasSpawned = true; x.SpawnedVars = vars; if (action?.Do == "launch" && ps.Name == "onImpact" && x.Event?.Other == null) x.ExtraRoles = new() { ["other"] = LType.Entity }; });
                        }
                        if (action?.Do == "forEach") inner = scope.With(x => x.HasIt = true);
                        Actions(list, p, inner, owner, sends);
                        break;
                    }
                    case "options":
                    {
                        if (v is not JArray opts) { Error(p, "must be [{ \"weight\": n, \"do\": [...] }, ...]"); break; }
                        for (int i = 0; i < opts.Count; i++)
                        {
                            if (opts[i] is not JObject o) { Error($"{p}[{i}]", "must be { \"weight\": n, \"do\": [...] }"); continue; }
                            if (o["weight"] != null) Param(new ParamSpec { Name = "weight", Type = "number", Min = 0 }, o["weight"], $"{p}[{i}].weight", scope, owner, null);
                            if (o["do"] is JArray d) Actions(ActionList(d), $"{p}[{i}].do", scope, owner, sends);
                            else Error($"{p}[{i}].do", "needs a list of actions");
                        }
                        break;
                    }
                    case "targeting":
                        if (v is not JArray tlist) { Error(p, "must be a list of filters or {\"filter\", \"range\", \"prefer\"}"); break; }
                        CheckTargeting(tlist.Select(x => x.Type is JTokenType.String or JTokenType.Object ? x.ToObject<TargetingDef>(Serializer) : null).ToList(), p, owner, scope);
                        break;
                    case "movement":
                        if (v is not JObject) { Error(p, "must be a movement block"); break; }
                        CheckMovement(v.ToObject<MovementDef>(Serializer), p, owner, scope);
                        break;
                    case "any":
                        if (isString) Expression(s.StartsWith("=") ? s.Substring(1) : s, p, scope, LType.Any);
                        break;
                    case "for":
                        if (isString && (s == "night" || s == "day")) break;
                        Param(new ParamSpec { Name = ps.Name, Type = "number", Min = 0 }, v, p, scope, owner, action);
                        break;
                    case "night":
                        if (isString && s == "current") break;
                        Param(new ParamSpec { Name = ps.Name, Type = "int", Min = 1 }, v, p, scope, owner, action);
                        break;
                    case "slot":
                        if (!isString) { Error(p, "must be @name, a selector or a Studio key (\"slot:Tower 3\")"); break; }
                        if (s.StartsWith("slot:") || s.StartsWith("add:") || s == "castle") break;
                        Expression(s, p, scope, LType.Entity, "slot");
                        break;
                    case "types":
                        if (v is not JArray ta || ta.Any(x => x.Type != JTokenType.String)) Error(p, "must be a list of labels, e.g. [\"fire\"]");
                        break;
                    case "keep":
                        if (v is not JArray ka || ka.Any(x => x.Type != JTokenType.String || ps.Options != null && !ps.Options.Contains((string)x)))
                            Error(p, $"must be a list of {string.Join(", ", ps.Options ?? [])}");
                        break;
                    case "color":
                        if (!isString || (!s.StartsWith("=") && !Validator.TryParseColor(s, out _))) Error(p, "must be \"#rrggbb\"");
                        break;
                    case "fx": case "sound":
                        if (!isString || string.IsNullOrWhiteSpace(s)) Error(p, $"must be a {(ps.Type == "fx" ? "effect" : "sound")} name");
                        break;
                }
            }

            /// <summary>An action list as defs; an entry that isn't an object becomes null (reported as empty).</summary>
            private static List<ActionDef> ActionList(JArray a) => a.Select(x => x is JObject o ? o.ToObject<ActionDef>(Serializer) : null).ToList();

            private void CheckTemplate(string s, string p, CheckScope scope)
            {
                if (s.StartsWith("=")) { Expression(s.Substring(1), p, scope, LType.Template, "a template"); return; }
                var r = ContentRef.Parse(s);
                switch (r.Kind)
                {
                    case RefKind.Vanilla:
                        if (game != null && !game.HasVanillaUnit(r.Id, null)) Error(p, $"no vanilla unit \"{r.Id}\" (`builder dump` lists them)");
                        break;
                    case RefKind.LocalUnit:
                        if (!templates.Contains(r.Id)) Error(p, $"no template \"{r.Id}\" in this pack" + ExprChecker.Suggest(r.Id, templates) + " (a unit or entity id, or \"vanilla:<id>\")");
                        break;
                    case RefKind.PackUnit:
                        if (r.Pack == pack.Id) goto case RefKind.LocalUnit;
                        if (game != null && !game.HasPackUnit(r.Pack, r.Id)) Error(p, $"pack \"{r.Pack}\" with unit \"{r.Id}\" isn't installed");
                        break;
                    default:
                        Error(p, $"\"{s}\" isn't a template (a unit or entity id, \"vanilla:<id>\", or \"=expression\")");
                        break;
                }
            }

            private void Num(LNum n, string p, CheckScope scope, double? min, double? max)
            {
                if (n == null) return;
                if (n.IsExpr) { Expression(n.Expr, p, scope, LType.Number); return; }
                if (n.Value is double d)
                {
                    if (min is double lo && d < lo) Error(p, $"{d.ToString(CultureInfo.InvariantCulture)} is below {lo.ToString(CultureInfo.InvariantCulture)}");
                    if (max is double hi && d > hi) Error(p, $"{d.ToString(CultureInfo.InvariantCulture)} is outside {(min ?? 0).ToString(CultureInfo.InvariantCulture)}..{hi.ToString(CultureInfo.InvariantCulture)}");
                }
            }

            private void Enum(string v, string p, params string[] options)
            {
                if (v != null && !options.Contains(v)) Error(p, $"must be one of {string.Join(", ", options)}" + ExprChecker.Suggest(v, options));
            }

            // ---- Components -----------------------------------------------------------------------------------

            private void CheckTargeting(List<TargetingDef> list, string p, Owner owner, CheckScope scope = null)
            {
                scope ??= Scope(owner, null);
                for (int i = 0; i < (list?.Count ?? 0); i++)
                {
                    var t = list[i];
                    string tp = $"{p}[{i}]";
                    if (t?.Filter == null) { Error(tp, "needs a filter"); continue; }
                    FilterString(t.Filter, tp + ".filter", scope);
                    Num(t.Range, tp + ".range", scope, 0, null);
                    Enum(t.Prefer, tp + ".prefer", "nearest", "farthest", "weakest", "strongest", "random", "best");
                    if (t.Prefer == "best" && t.Score == null) Error(tp + ".score", "prefer best needs a score (an expression over `it`)");
                    if (t.Score != null) Expression(t.Score, tp + ".score", scope.With(s => s.HasIt = true), LType.Number, "score");
                }
            }

            private void CheckMovement(MovementDef m, string p, Owner owner, CheckScope scope = null)
            {
                if (m == null) return;
                scope ??= Scope(owner, null);
                Enum(m.Mode, p + ".mode", "pathfind", "direct", "hold", "path");
                Enum(m.Then, p + ".then", "pathfind", "hold", "destroy");
                if (m.Mode == "path" && m.Path == null) Error(p + ".path", "mode path needs a path (@name or [[x, z], ...])");
                CheckPath(m.Path, p + ".path", scope);
                Num(m.Speed, p + ".speed", scope, 0, 100);
                Num(m.KeepDistance, p + ".keepDistance", scope, 0, 200);
                if (m.Kite != null) { Num(m.Kite.Below, p + ".kite.below", scope, 0, 200); Num(m.Kite.For, p + ".kite.for", scope, 0, 60); }
                if (m.Flee != null)
                {
                    if (m.Flee.When != null) Expression(m.Flee.When, p + ".flee.when", scope, LType.Bool, "flee.when");
                    if (m.Flee.From != null) FilterString(m.Flee.From, p + ".flee.from", scope);
                    if (m.Flee.To != null) Expression(m.Flee.To, p + ".flee.to", scope, LType.Position, "flee.to");
                }
                if (m.Wander != null) Num(m.Wander.Radius, p + ".wander.radius", scope, 0, 200);
                if (m.Home is JValue hv && hv.Type == JTokenType.String && (string)hv != "spawn") Expression((string)hv, p + ".home", scope, LType.Position, "home");
            }

            private void CheckPath(JToken path, string p, CheckScope scope)
            {
                if (path == null) return;
                if (path.Type == JTokenType.String) { Expression((string)path, p, scope, LType.Entity, "path"); return; }
                if (path is not JArray pts || pts.Count < 1 || pts.Any(x => x is not JArray xa || xa.Count < 2 || xa.Count > 3))
                    Error(p, "must be @name or [[x, z] | [x, y, z], ...]");
            }

            private void CheckAttacks(List<AttackDef> list, string p, Owner owner)
            {
                var scope = Scope(owner, null);
                for (int i = 0; i < (list?.Count ?? 0); i++)
                {
                    var a = list[i];
                    string ap = $"{p}[{i}]";
                    if (a == null) { Error(ap, "is empty"); continue; }
                    Num(a.Cooldown, ap + ".cooldown", scope, 0.05, 600);
                    Num(a.Jitter, ap + ".jitter", scope, 0, 1);
                    Num(a.Range, ap + ".range", scope, 0, 200);
                    Num(a.MinRange, ap + ".minRange", scope, 0, 200);
                    Enum(a.When, ap + ".when", "night", "always", "day");
                    CheckTargeting(a.Targeting, ap + ".targeting", owner);
                    if (a.Launch == null && a.Weapon == null && a.Do == null) Error(ap, "needs launch, weapon or do");
                    if (a.Launch != null)
                    {
                        if (a.Launch.Entity == null) Error(ap + ".launch.entity", "needs a template");
                        else CheckTemplate(a.Launch.Entity, ap + ".launch.entity", scope);
                        if (a.Launch.From != null && a.Launch.From.Length != 3) Error(ap + ".launch.from", "must be [x, y, z]");
                    }
                    if (a.Weapon != null && a.Weapon != "inherit")
                    {
                        var w = ContentRef.Parse(a.Weapon);
                        if (w.Kind != RefKind.Vanilla) Error(ap + ".weapon", "must be \"inherit\" or \"vanilla:<weapon>\"");
                        else if (game != null && !game.HasVanillaWeapon(w.Id)) Error(ap + ".weapon", $"no vanilla weapon \"{w.Id}\"");
                    }
                    if (a.Do != null)
                    {
                        var ev = LogicSchema.Events["attack"];
                        Actions(a.Do, ap + ".do", Scope(owner, ev), owner, new HashSet<string>());
                    }
                }
            }

            private void CheckTemplateFields(TemplateFields e, string p, ISet<string> vars, LevelCtx level, bool place)
            {
                string kind = e.Kind;
                if (!place && kind == null) Error(p + ".kind", $"needs a kind: {string.Join(", ", EntityKinds.Templates)}");
                else if (kind != null) Enum(kind, p + ".kind", place ? EntityKinds.Places : EntityKinds.Templates);
                var owner = new Owner { Path = p, Kind = kind ?? "prop", Vars = vars, Level = level, Rules = e.Rules };
                var scope = Scope(owner, null);
                CheckLabels(e.Labels, p + ".labels");
                CheckVarDecls(e.Vars, p + ".vars");
                Enum(e.Team, p + ".team", Teams.All);
                Num(e.Lifetime, p + ".lifetime", scope, 0, 3600);
                if (e.MaxAlive is int ma && (ma < 1 || ma > LogicCapsDef.Max.PerTemplate)) Error(p + ".maxAlive", $"must be 1..{LogicCapsDef.Max.PerTemplate}");
                if (e.Visual != null)
                {
                    if (e.Visual.Tint != null && !Validator.TryParseColor(e.Visual.Tint, out _)) Error(p + ".visual.tint", "must be \"#rrggbb\"");
                    if (e.Visual.Mesh != null && ModelRef.Kind(e.Visual.Mesh) == ModelKind.Invalid)
                        Error(p + ".visual.mesh", "must be \"asset:<key>\", \"mesh:<scenery mesh>\" or \"vanilla:<unit id>\"");
                    else if (game != null && ModelRef.Kind(e.Visual.Mesh, out string uid) == ModelKind.Unit && !game.HasVanillaUnit(uid, null)) Error(p + ".visual.mesh", $"no vanilla unit \"{uid}\"");
                }
                if (e.Body != null)
                {
                    Num(e.Body.Radius, p + ".body.radius", scope, 0.05, 100);
                    Num(e.Body.Height, p + ".body.height", scope, 0, 100);
                    Num(e.Body.Hp, p + ".body.hp", scope, 1, 1000000);
                    if (e.Body.Box != null && e.Body.Box.Length != 2) Error(p + ".body.box", "must be [width, depth]");
                    if (e.Body.Hp != null && e.Body.Hittable != true) Warn(p + ".body.hp", "hp only matters when hittable is true");
                }
                if (e.CountsAsEnemy == true && (e.Body?.Hittable != true || e.Team != Teams.Enemy))
                    Warn(p + ".countsAsEnemy", "only a hittable enemy-team entity keeps the night open");
                if (e.Projectile is { } pr)
                {
                    Num(pr.Speed, p + ".projectile.speed", scope, 0, 500);
                    Num(pr.Homing, p + ".projectile.homing", scope, 0, 1);
                    Num(pr.Arc, p + ".projectile.arc", scope, 0, 10);
                    Num(pr.Gravity, p + ".projectile.gravity", scope, 0, 200);
                    Num(pr.HitRadius, p + ".projectile.hitRadius", scope, 0, 50);
                    Num(pr.Range, p + ".projectile.range", scope, 0, 1000);
                    Enum(pr.Face, p + ".projectile.face", "travel", "fixed", "spin");
                    Enum(pr.OnGround, p + ".projectile.onGround", "impact", "stick", "bounce", "pass");
                    if (pr.Hits != null) FilterString(pr.Hits, p + ".projectile.hits", scope);
                    if (pr.Pierce is int pi && pi < -1) Error(p + ".projectile.pierce", "must be -1 (all) or more");
                    if (pr.Spin != null && pr.Spin.Length != 3) Error(p + ".projectile.spin", "must be [x, y, z] degrees per second");
                }
                if (e.Mover is { } mv)
                {
                    CheckPath(mv.Path, p + ".mover.path", scope);
                    Enum(mv.Mode, p + ".mover.mode", "loop", "pingpong", "once");
                    Num(mv.Speed, p + ".mover.speed", scope, 0, 100);
                    Num(mv.Wait, p + ".mover.wait", scope, 0, 600);
                    if (e.Body?.Walkable == true && mv.Path != null) Warn(p + ".mover", "a walkable mover only snaps between poses; it can't glide along a path");
                    foreach (var pose in mv.Pose ?? [])
                    {
                        if (LogicSchema.Inputs.Values.Any(x => x.Contains(pose.Key))) Error($"{p}.mover.pose.{pose.Key}", $"\"{pose.Key}\" is a built-in input; name the pose something else");
                        if (pose.Value?.Offset != null && pose.Value.Offset.Length != 3) Error($"{p}.mover.pose.{pose.Key}.offset", "must be [x, y, z]");
                    }
                }
                if (e.Area is { } ar)
                {
                    var sh = ar.Shape;
                    int shapes = sh == null ? 0 : new object[] { sh.Circle, sh.Rect, sh.Polygon }.Count(x => x != null);
                    if (shapes != 1) Error(p + ".area.shape", "needs exactly one of circle {radius}, rect {size: [w, d]} or polygon {points}");
                    if (sh?.Circle != null) Num(sh.Circle.Radius, p + ".area.shape.circle.radius", scope, 0.1, 500);
                    if (sh?.Rect != null && (sh.Rect.Size == null || sh.Rect.Size.Length != 2)) Error(p + ".area.shape.rect.size", "must be [width, depth]");
                    if (sh?.Polygon != null && (sh.Polygon.Points == null || sh.Polygon.Points.Count < 3 || sh.Polygon.Points.Any(x => x == null || x.Length != 2)))
                        Error(p + ".area.shape.polygon.points", "must be at least 3 [x, z] points");
                    if (ar.Watch != null) FilterString(ar.Watch, p + ".area.watch", scope);
                    Enum(ar.Show, p + ".area.show", "none", "outline", "fill");
                    if (ar.Color != null && !Validator.TryParseColor(ar.Color, out _)) Error(p + ".area.color", "must be \"#rrggbb\"");
                    Num(ar.Tick, p + ".area.tick", scope, 0.1, 600);
                }
                else if (kind == EntityKinds.Area && !place) Error(p + ".area", "an area template needs an area block");
                if (e.Pickup is { } pk)
                {
                    if (pk.By != null) FilterString(pk.By, p + ".pickup.by", scope);
                    Num(pk.Radius, p + ".pickup.radius", scope, 0.1, 50);
                    Num(pk.Magnet, p + ".pickup.magnet", scope, 0, 100);
                }
                if (e.Interact is { } it)
                {
                    if (it.Prompt != null) Text(it.Prompt, p + ".interact.prompt", scope.With(x => x.ExtraRoles = new() { ["cost"] = LType.Number }));
                    Num(it.Cost, p + ".interact.cost", scope, 0, 100000);
                    Enum(it.When, p + ".interact.when", "day", "night", "always");
                    Num(it.Radius, p + ".interact.radius", scope, 0.5, 30);
                    if (it.Choices != null)
                    {
                        if (it.Choices.Count < 1 || it.Choices.Count > 4) Error(p + ".interact.choices", "1 to 4 choices");
                        var ids = new HashSet<string>();
                        for (int i = 0; i < it.Choices.Count; i++)
                        {
                            var c = it.Choices[i];
                            if (c?.Id == null || !ids.Add(c.Id)) Error($"{p}.interact.choices[{i}].id", "each choice needs its own id");
                            Num(c?.Cost, $"{p}.interact.choices[{i}].cost", scope, 0, 100000);
                        }
                    }
                }
                CheckAttacks(e.Attacks, p + ".attacks", owner);
                CheckTargeting(e.Targeting, p + ".targeting", owner);
                CheckMovement(e.Movement, p + ".movement", owner);
                CheckStatusRefs(e.Statuses, p + ".statuses");
                CheckRules(owner);
            }

            private void CheckStatus(StatusDef s, string p)
            {
                if (s == null) { Error(p, "is empty"); return; }
                var owner = new Owner { Path = p, Kind = "status", Rules = s.Rules };
                var scope = Scope(owner, null);
                Num(s.Duration, p + ".duration", scope, 0.05, 36000);
                Num(s.Tick, p + ".tick", scope, 0.1, 3600);
                Enum(s.Stacking, p + ".stacking", "refresh", "add", "extend", "ignore");
                if (s.MaxStacks is int ms && (ms < 1 || ms > 100)) Error(p + ".maxStacks", "must be 1..100");
                foreach (var kv in s.Mods ?? [])
                {
                    string mp = $"{p}.mods.{kv.Key}";
                    var m = kv.Value;
                    if (kv.Key == "drift")
                    {
                        if (m?.Toward == null && m?.Away == null && m?.Dir == null) Error(mp, "drift needs toward, away or dir");
                        foreach (var (src, k) in new[] { (m?.Toward, "toward"), (m?.Away, "away"), (m?.Dir, "dir") })
                            if (src != null) Expression(src, $"{mp}.{k}", scope.With(x => x.ExtraRoles = new() { ["other"] = LType.Entity }), LType.Position, k);
                        if (m?.Speed == null) Error(mp + ".speed", "drift needs a speed (m/s)");
                        continue;
                    }
                    if (!LogicSchema.Stats.ContainsKey(kv.Key)) { Error(mp, $"unknown stat \"{kv.Key}\"" + ExprChecker.Suggest(kv.Key, LogicSchema.Stats.Keys.Append("drift"))); continue; }
                    if (m == null) { Error(mp, "is empty"); continue; }
                    int n = new object[] { m.Mult, m.Add, m.Value }.Count(x => x != null);
                    if (n != 1) Error(mp, "needs exactly one of mult, add or value");
                    bool dmg = kv.Key is "damageTaken" or "damageMult";
                    if (!dmg && (m.From != null || m.When != null || m.Types != null || m.Chance != null))
                        Error(mp, "from, types, chance and when only apply to damageTaken and damageMult");
                    var modScope = scope.With(x => x.ExtraRoles = new() { ["attacker"] = LType.Entity, ["victim"] = LType.Entity, ["dist"] = LType.Number });
                    if (m.From != null) FilterString(m.From, mp + ".from", scope);
                    if (m.When != null) Expression(m.When, mp + ".when", modScope, LType.Bool, "when");
                    Num(m.Chance, mp + ".chance", scope, 0, 1);
                }
                for (int i = 0; i < (s.Flags?.Count ?? 0); i++)
                    if (!LogicSchema.Flags.ContainsKey(s.Flags[i])) Error($"{p}.flags[{i}]", $"unknown flag \"{s.Flags[i]}\"" + ExprChecker.Suggest(s.Flags[i], LogicSchema.Flags.Keys));
                if (s.Visual?.Tint != null && !Validator.TryParseColor(s.Visual.Tint, out _)) Error(p + ".visual.tint", "must be \"#rrggbb\"");
                CheckRules(owner);
            }

            private void CheckPresetDef(PresetDef pr, string p)
            {
                if (pr == null) return;
                foreach (var kv in pr.Params ?? [])
                {
                    if (!VarName.IsMatch(kv.Key)) Error($"{p}.params.{kv.Key}", "param names use letters, digits and '_'");
                    if (kv.Value?.Type != null && !LogicSchema.ParamTypes.Contains(kv.Value.Type)) Error($"{p}.params.{kv.Key}.type", $"unknown type \"{kv.Value.Type}\"" + ExprChecker.Suggest(kv.Value.Type, LogicSchema.ParamTypes));
                }
                if (pr.Kind != null) Enum(pr.Kind, p + ".kind", EntityKinds.Places);
                // The preset's rules are checked where it's used (with its params filled in).
            }

            // ---- Levels ---------------------------------------------------------------------------------------

            private void CheckLevel(LevelCtx lv)
            {
                var logic = lv.Def.Logic;
                string p = lv.Path;
                CheckVarDecls(logic.Vars, p + ".vars");
                if (logic.Caps != null)
                {
                    foreach (var (name, value, def, max) in logic.Caps.Fields())
                    {
                        if (value is not int v) continue;
                        if (v < 1) Error($"{p}.caps.{name}", "must be at least 1");
                        else if (v > max) Error($"{p}.caps.{name}", $"{v} is above the hard limit {max}");
                        else if (v > def) Warn($"{p}.caps.{name}", $"{v} is above the default {def}: may lag");
                    }
                }
                var levelOwner = new Owner { Path = p, Kind = "level", LevelScript = true, Level = lv, Rules = logic.Rules };
                var levelScope = Scope(levelOwner, null);
                var hudIds = new HashSet<string>();
                for (int i = 0; i < (logic.Hud?.Count ?? 0); i++)
                {
                    var h = logic.Hud[i];
                    string hp = $"{p}.hud[{i}]";
                    if (h?.Id == null || !hudIds.Add(h.Id)) Error(hp + ".id", "each HUD line needs its own id");
                    if (h?.Text != null) Text(h.Text, hp + ".text", levelScope);
                    if (h?.Visible != null) Expression(h.Visible, hp + ".visible", levelScope, LType.Bool, "visible");
                }
                if ((logic.KingActions?.Count ?? 0) > 3) Error(p + ".kingActions", "at most 3 king actions (three keys)");
                var kaIds = new HashSet<string>();
                for (int i = 0; i < (logic.KingActions?.Count ?? 0); i++)
                {
                    var k = logic.KingActions[i];
                    string kp = $"{p}.kingActions[{i}]";
                    if (k?.Id == null || !kaIds.Add(k.Id)) Error(kp + ".id", "each king action needs its own id");
                    Enum(k?.When, kp + ".when", "day", "night", "always");
                    if (k?.Uses != null) { Enum(k.Uses.Per, kp + ".uses.per", "day", "night", "run"); if (k.Uses.Count < 1) Error(kp + ".uses.count", "must be at least 1"); }
                    if (k?.Cooldown is double c && c < 0) Error(kp + ".cooldown", "must be 0 or more");
                }
                var placeIds = new HashSet<string>(StringComparer.Ordinal);
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < (logic.Places?.Count ?? 0); i++)
                {
                    var pl = logic.Places[i];
                    string pp = $"{p}.places[{i}]";
                    if (pl == null) { Error(pp, "is empty"); continue; }
                    if (!Ids.IsValid(pl.Id)) Error(pp + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                    else if (!placeIds.Add(pl.Id)) Error(pp + ".id", $"\"{pl.Id}\" is used twice");
                    if (pl.Name != null && pl.Name != pl.Id && !names.Add(pl.Name)) Error(pp + ".name", $"another object is named \"{pl.Name}\"");
                    foreach (var (n, key) in new[] { (pl.Id, "id"), (pl.Name, "name") })
                        if (n != null && n.Contains('.')) Warn($"{pp}.{key}", $"\"{n}\" has a '.', so expressions can't name it (@{n} reads as @{n.Split('.')[0]} . {n.Split('.')[1]})");
                    if (pl.Pos == null && pl.Kind != EntityKinds.Path) Error(pp + ".pos", "needs a position [x, y, z]");
                    else if (pl.Pos != null && (pl.Pos.Length < 2 || pl.Pos.Length > 3)) Error(pp + ".pos", "must be [x, z] or [x, y, z]");
                    if (pl.Kind == EntityKinds.Path && (pl.Points == null || pl.Points.Count < 2 || pl.Points.Any(x => x == null || x.Length < 2 || x.Length > 3)))
                        Error(pp + ".points", "a path needs at least 2 [x, z] or [x, y, z] points");
                    var vars = new HashSet<string>(VarSet(pl.Vars));
                    if (pl.Entity != null)
                    {
                        if (!templates.Contains(pl.Entity) && ContentRef.Parse(pl.Entity).Kind != RefKind.Vanilla)
                            Error(pp + ".entity", $"no template \"{pl.Entity}\"" + ExprChecker.Suggest(pl.Entity, templates));
                        if (templateVars.TryGetValue(pl.Entity, out var tv))
                        {
                            foreach (var v in pl.Vars?.Keys ?? Enumerable.Empty<string>())
                                if (!tv.Contains(v)) Warn($"{pp}.vars.{v}", $"\"{pl.Entity}\" doesn't declare \"{v}\"; it's added for this copy only");
                            vars.UnionWith(tv);
                        }
                    }
                    else if (pl.Kind == null && pl.Extra?.ContainsKey("_preset") != true) Error(pp, "needs \"entity\" (a template), \"preset\", or an inline \"kind\"");
                    CheckTemplateFields(pl, pp, vars, lv, place: true);
                }
                foreach (var kv in logic.Attach ?? [])
                {
                    string ap = $"{p}.attach[\"{kv.Key}\"]";
                    var a = kv.Value;
                    if (a == null) { Error(ap, "is empty"); continue; }
                    string kind = AttachKind(kv.Key, lv.Def, out string err);
                    if (err != null) Error(ap, err);
                    if (a.Name != null)
                    {
                        if (!Ids.IsValid(a.Name) && !VarName.IsMatch(a.Name)) Error(ap + ".name", "names use letters, digits, '-' and '_'");
                        if (placeIds.Contains(a.Name) || !names.Add(a.Name)) Error(ap + ".name", $"another object is named \"{a.Name}\"");
                    }
                    CheckLabels(a.Labels, ap + ".labels");
                    CheckVarDecls(a.Vars, ap + ".vars");
                    CheckStatusRefs(a.Statuses, ap + ".statuses");
                    var owner = new Owner { Path = ap, Kind = kind ?? "building", Vars = VarSet(a.Vars), Level = lv, Rules = a.Rules };
                    CheckTargeting(a.Targeting, ap + ".targeting", owner);
                    CheckRules(owner);
                }
                CheckRules(levelOwner);
            }

            /// <summary>The kind of object an attach key names, checking what can be checked offline.</summary>
            private static string AttachKind(string key, LevelDef level, out string error)
            {
                error = null;
                if (key == "castle") return "building";
                int colon = key.IndexOf(':');
                string prefix = colon > 0 ? key.Substring(0, colon) : null, id = colon > 0 ? key.Substring(colon + 1) : null;
                switch (prefix)
                {
                    case "slot": return "building"; // host slot names need the game (the Studio checks them)
                    case "add":
                        if (level.BuildSlots?.Add?.Any(a => a?.Id == id) != true) error = $"no added slot \"{id}\" on this level";
                        return "building";
                    case "prop":
                        if (level.Props?.Any(a => a?.Id == id) != true) error = $"no prop \"{id}\" on this level";
                        return "prop";
                    case "line":
                        return "line";
                }
                error = "attach keys are \"slot:<name>\", \"add:<id>\", \"prop:<id>\", \"line:<name>\" or \"castle\"";
                return null;
            }

            /// <summary>A rule that hears signal A and, without waiting, sends B, where some rule hearing B sends A
            /// without waiting: the loop runs until the chain-depth cap.</summary>
            private void CheckLoops()
            {
                var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var (_, hears, sends) in signalEdges)
                    if (hears != null) { if (!graph.TryGetValue(hears, out var set)) graph[hears] = set = new HashSet<string>(StringComparer.Ordinal); set.UnionWith(sends); }
                var reported = new HashSet<string>();
                foreach (var (path, hears, _) in signalEdges)
                {
                    if (hears == null || reported.Contains(hears)) continue;
                    var seen = new HashSet<string>();
                    var stack = new Stack<string>(graph.TryGetValue(hears, out var first) ? first : []);
                    while (stack.Count > 0)
                    {
                        string s = stack.Pop();
                        if (s == hears)
                        {
                            Warn(path, $"the signal \"{hears}\" leads back to itself with no wait: this loop runs until the chain-depth cap (add a wait, a cooldown or a condition)");
                            reported.Add(hears);
                            break;
                        }
                        if (!seen.Add(s)) continue;
                        if (graph.TryGetValue(s, out var next)) foreach (var x in next) stack.Push(x);
                    }
                }
            }
        }
    }
}
