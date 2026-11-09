using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/*
    Rules: JSON (docs/builder-engine.md, "Rules") -> CompiledRule, once per template/place/attach; RuleInstance is a
    compiled rule on one owner, with its per-owner state (enabled, once, cooldown, the 30/s cap, `near` membership,
    `hpBelow` arming, timer phase).
*/
namespace PalaceMod.Builder.Logic
{
    internal sealed class CompiledRule
    {
        public string Id;                    // the rule's id, or null
        public int Index;                    // position in its rules list
        public string Path;                  // JSON path for errors and the trace
        public string Note;
        public string Event;                 // when.on
        public JObject When;                 // when params (seconds, filter, name, ...)
        public Func<LogicContext, LogicEntity, bool> Of;   // `of` filter on the subject (relative to the owner); null = own events
        public string OfSource;
        public float OfRange;                // 0 = anywhere
        public List<Func<LogicContext, bool>> Conditions = [];
        public ActionList Do = ActionList.Empty;
        public bool EnabledAtStart = true;
        public bool Once;
        public double Cooldown;
        public string CompileError;          // the rule didn't compile; it never fires and the error is traced once

        // Parsed event params (the common ones; the rest are read from When).
        public double Seconds, Jitter, Phase;
        public string During;                // night | day | always
        public Func<LogicContext, LogicEntity, bool> NearFilter;
        public float NearRange;
        public string NearEdge;              // enter | exit | both
        public double Pct = -1;

        /// <summary>"rules[i]": the index in its owner's own rules list as written (the trace's Rule; Path is exact).</summary>
        public string Label => $"rules[{Index}]";
    }

    internal sealed class RuleInstance
    {
        public CompiledRule Rule;
        public LogicEntity Owner;
        public bool Enabled;
        public bool Done;                    // a `once` rule that fired
        public double CooldownUntil;
        public double WindowStart;           // the 30-per-second cap
        public int WindowCount;
        public bool HpArmed = true;
        public HashSet<LogicEntity> NearInside;
        public double NextTime = double.NaN; // every/after: next due time
        public int Ticks;                    // every: tick count
        public bool ErrorTraced;
        public object AttachedBy;            // AttachRules handle (status rules), or null

        public string OwnerLabel => Owner?.Id ?? "?";
    }

    internal static class RuleCompiler
    {
        /// <summary>Events that are not about one entity: every rule with that `when` hears them, wherever it lives.</summary>
        public static readonly HashSet<string> GlobalEvents = new(StringComparer.Ordinal)
        {
            "levelStart", "levelEnd", "dusk", "nightStart", "dawn", "day", "waveCleared", "goldChanged", "kingAction",
        };

        /// <summary>Events driven by the world's own timers or pollers (delivered directly to one rule instance).</summary>
        public static readonly HashSet<string> TimerEvents = new(StringComparer.Ordinal) { "every", "after", "near" };

        /// <summary>Params of `when` compared to event.name/text (the signal name, the attack id, ...).</summary>
        private static readonly string[] TextParams = ["name", "attack", "status", "action", "line", "result"];
        /// <summary>Params of `when` compared to event.value.</summary>
        private static readonly string[] ValueParams = ["night", "level"];

        /// <summary>Compile a rules array. Preset entries are expanded first. Rules that fail are kept with CompileError.</summary>
        public static List<CompiledRule> CompileAll(JArray rules, string path, IReadOnlyDictionary<string, JObject> presets, List<string> errors)
        {
            var list = new List<CompiledRule>();
            if (rules == null) return list;
            int i = 0;
            foreach (var tok in Presets.Expand(rules, presets, path, errors))
            {
                var (rule, rulePath) = tok;
                list.Add(Compile(rule, rulePath, i++, errors));
            }
            return list;
        }

        public static CompiledRule Compile(JObject json, string path, int index, List<string> errors)
        {
            // `_src`: the rule's path in the original pack when a preset produced it (L4's expansion).
            if (json["_src"] is JValue { Type: JTokenType.String } src) path = (string)src;
            var r = new CompiledRule { Index = index, Path = path };
            try
            {
                r.Id = (string)json["id"];
                r.Note = (string)json["note"];
                r.EnabledAtStart = (bool?)json["enabled"] ?? true;
                r.Once = (bool?)json["once"] ?? false;
                r.Cooldown = (double?)json["cooldown"] ?? 0;

                var when = json["when"];
                if (when is JValue { Type: JTokenType.String } s) r.When = new JObject { ["on"] = (string)s };
                else if (when is JObject wo) r.When = wo;
                else throw new LogicError("`when` is missing");
                r.Event = (string)r.When["on"] ?? throw new LogicError("`when.on` is missing");

                if (r.When["of"] is JValue { Type: JTokenType.String } of)
                {
                    r.OfSource = (string)of;
                    r.Of = ExprCompiler.CompileFilter((string)of, path + ".when.of");
                }
                r.OfRange = (float?)r.When["range"] ?? 0;

                r.Seconds = (double?)r.When["seconds"] ?? 1;
                if (r.Event == "every") r.Seconds = Math.Max(0.1, r.Seconds);
                r.Jitter = (double?)r.When["jitter"] ?? 0;
                r.Phase = (double?)r.When["phase"] ?? 0;
                r.During = (string)r.When["during"] ?? "always";
                if (r.Event == "near")
                {
                    r.NearFilter = ExprCompiler.CompileFilter((string)r.When["filter"] ?? "foe", path + ".when.filter");
                    r.NearRange = (float?)r.When["range"] ?? 5;
                    r.NearEdge = (string)r.When["edge"] ?? "enter";
                    r.OfRange = 0; // `range` here is the near radius
                }
                if (r.Event == "hpBelow") r.Pct = (double?)r.When["pct"] ?? 0.5;

                switch (json["if"])
                {
                    case null: break;
                    case JArray arr:
                        for (int i = 0; i < arr.Count; i++) r.Conditions.Add(Cond(arr[i], $"{path}.if[{i}]"));
                        break;
                    default:
                        r.Conditions.Add(Cond(json["if"], path + ".if"));
                        break;
                }
                r.Do = ActionCompiler.CompileList(json["do"] as JArray, path + ".do");
            }
            catch (Exception e) when (e is LogicError or FormatException or ArgumentException or InvalidCastException)
            {
                r.CompileError = e.Message;
                errors?.Add($"{path}: {e.Message}");
            }
            return r;
        }

        private static Func<LogicContext, bool> Cond(JToken tok, string path)
        {
            if (tok.Type == JTokenType.Boolean) { bool b = (bool)tok; return _ => b; }
            var f = ExprCompiler.Compile((string)tok, path);
            return ctx => Values.ToBool(f(ctx));
        }

        /// <summary>Whether the event's params (`name`, `attack`, `night`, `min`, ...) accept this event.</summary>
        public static bool ParamsMatch(CompiledRule r, LogicEvent e)
        {
            var w = r.When;
            foreach (var p in TextParams)
                if (w[p] is JValue v && v.Type == JTokenType.String && !string.Equals((string)v, e.Text, StringComparison.Ordinal)) return false;
            foreach (var p in ValueParams)
                if (w[p] is JValue v && (v.Type is JTokenType.Integer or JTokenType.Float) && Math.Abs((double)v - e.Value) > 1e-9) return false;
            if (w["min"] is JValue m && (m.Type is JTokenType.Integer or JTokenType.Float) && e.Value < (double)m) return false;
            return true;
        }
    }

    /// <summary>Preset references in rule lists: `{ "preset": id, "with": { ... } }` -> the preset's rules with params substituted.</summary>
    internal static class Presets
    {
        public static IEnumerable<(JObject rule, string path)> Expand(JArray rules, IReadOnlyDictionary<string, JObject> presets, string path, List<string> errors)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i] is not JObject r) continue;
                string at = $"{path}[{i}]";
                if (r["preset"] is not JValue pv) { yield return (r, at); continue; }
                string id = (string)pv;
                if (presets == null || !presets.TryGetValue(id ?? "", out var preset))
                {
                    errors?.Add($"{at}: preset \"{id}\" not found");
                    continue;
                }
                var args = Args(preset, r["with"] as JObject);
                if (preset["rules"] is JArray pr)
                    for (int j = 0; j < pr.Count; j++)
                        if (pr[j] is JObject rule) yield return ((JObject)Substitute(rule.DeepClone(), args), $"{at}(preset {id}).rules[{j}]");
            }
        }

        /// <summary>The preset's vars with params substituted (merged under an entity's own vars).</summary>
        public static JObject Vars(JObject preset, JObject with) => preset["vars"] is JObject v ? (JObject)Substitute(v.DeepClone(), Args(preset, with)) : null;

        private static Dictionary<string, JToken> Args(JObject preset, JObject with)
        {
            var args = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (preset["params"] is JObject ps)
                foreach (var p in ps.Properties())
                    args[p.Name] = with?[p.Name] ?? (p.Value as JObject)?["default"] ?? JValue.CreateNull();
            if (with != null)
                foreach (var p in with.Properties()) args[p.Name] = p.Value;
            return args;
        }

        public static JToken Substitute(JToken t, Dictionary<string, JToken> args)
        {
            switch (t)
            {
                case JObject o:
                    foreach (var p in o.Properties()) p.Value = Substitute(p.Value, args);
                    return o;
                case JArray a:
                    for (int i = 0; i < a.Count; i++) a[i] = Substitute(a[i], args);
                    return a;
                case JValue { Type: JTokenType.String } v:
                    string s = (string)v;
                    if (s.IndexOf('{') < 0) return v;
                    // A whole-string reference keeps the param's JSON type ("{count}" -> 3).
                    if (s.Length > 2 && s[0] == '{' && s[^1] == '}' && args.TryGetValue(s.Substring(1, s.Length - 2), out var whole)) return whole.DeepClone();
                    foreach (var kv in args)
                    {
                        string key = "{" + kv.Key + "}";
                        if (s.Contains(key)) s = s.Replace(key, kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    return new JValue(s);
                default:
                    return t;
            }
        }
    }
}
