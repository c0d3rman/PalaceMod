using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PalaceMod.Builder.Model.Logic
{
    /// <summary>
    /// Presets (docs/builder-engine.md, "Presets"): saved, parameterised rule sets. A rules entry
    /// {"preset": id, "with": {...}} or a place's "preset" is expanded when the pack is compiled: {param} is
    /// substituted (a whole-string "{p}" by the typed value, else as text), the preset's vars, labels and components are
    /// merged under the owner's own, and every produced rule gets "_src" (the path it came from) for errors and the
    /// trace. Lookup order: the pack's presets, the library's (Library), then the built-in ones (Assets/logic-presets.json).
    /// </summary>
    public static class Presets
    {
        public const int MaxDepth = 8;
        public const string SourceField = "_src";

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        /// <summary>Set by the game to look up library presets (PalaceBuilder/library/presets). Null in tests.</summary>
        public static Func<string, PresetDef> Library;

        private static List<PresetDef> builtIn;
        private static string builtInError;

        /// <summary>The built-in presets (the logic objects and common abilities), from the embedded JSON.</summary>
        public static IReadOnlyList<PresetDef> BuiltIn
        {
            get
            {
                if (builtIn != null) return builtIn;
                try
                {
                    var asm = typeof(Presets).Assembly;
                    string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("logic-presets.json", StringComparison.Ordinal));
                    if (name == null) { builtInError = "logic-presets.json isn't embedded"; return builtIn = []; }
                    using var s = asm.GetManifestResourceStream(name);
                    using var r = new StreamReader(s);
                    builtIn = ParseBuiltIn(r.ReadToEnd());
                }
                catch (Exception e) when (e is JsonException || e is IOException)
                {
                    builtInError = e.Message;
                    builtIn = [];
                }
                return builtIn;
            }
        }

        public static string BuiltInError { get { _ = BuiltIn; return builtInError; } }

        public static List<PresetDef> ParseBuiltIn(string json) => (JObject.Parse(json)["presets"] as JArray ?? []).ToObject<List<PresetDef>>(Serializer);

        public static PresetDef Find(DesignPack pack, string id)
        {
            if (id == null) return null;
            return pack?.FindPreset(id) ?? Library?.Invoke(id) ?? BuiltIn.FirstOrDefault(p => p.Id == id);
        }

        public static IEnumerable<string> KnownIds(DesignPack pack) =>
            (pack?.Presets ?? []).Select(p => p.Id).Concat(BuiltIn.Select(p => p.Id)).Where(i => i != null).Distinct();

        // ---- Expansion ----------------------------------------------------------------------------------------

        /// <summary>A copy of the pack with every preset use expanded. Issues get paths into the original pack.</summary>
        public static DesignPack Expand(DesignPack pack, List<ValidationIssue> issues)
        {
            if (pack == null) return null;
            var root = JObject.FromObject(pack, Serializer);
            ExpandJson(root, pack, issues);
            return root.ToObject<DesignPack>(Serializer);
        }

        /// <summary>Expands preset uses in a pack's JSON in place.</summary>
        public static void ExpandJson(JObject root, DesignPack pack, List<ValidationIssue> issues)
        {
            var ctx = new Ctx(pack, issues);
            Each(root["units"], (o, p) => ctx.Owner(o, "units" + p, true));
            Each(root["entities"], (o, p) => ctx.Owner(o, "entities" + p, true));
            Each(root["statuses"], (o, p) => ctx.Owner(o, "statuses" + p, false));
            Each(root["levels"], (lv, lp) =>
            {
                if (lv["logic"] is not JObject logic) return;
                string p = "levels" + lp + ".logic";
                ctx.Owner(logic, p, false);
                Each(logic["places"], (o, pp) => ctx.Owner(o, p + ".places" + pp, true));
                if (logic["attach"] is JObject attach)
                    foreach (var prop in attach.Properties().ToList())
                        if (prop.Value is JObject ao) ctx.Owner(ao, $"{p}.attach[\"{prop.Name}\"]", true);
            });
        }

        private static void Each(JToken arr, Action<JObject, string> f)
        {
            if (arr is not JArray a) return;
            for (int i = 0; i < a.Count; i++) if (a[i] is JObject o) f(o, $"[{i}]");
        }

        private sealed class Ctx(DesignPack pack, List<ValidationIssue> issues)
        {
            private void Error(string path, string msg) => issues?.Add(new ValidationIssue(Severity.Error, path, msg));
            private void Warn(string path, string msg) => issues?.Add(new ValidationIssue(Severity.Warning, path, msg));

            public void Owner(JObject owner, string path, bool components)
            {
                var stack = new List<string>();
                // The owner's own rules first, so their _src paths keep their own indexes.
                if (owner["rules"] is JArray own) owner["rules"] = ExpandRules(own, path + ".rules", owner, components, stack, 0, top: true);
                if (owner["preset"] is JValue pv && pv.Type == JTokenType.String && owner["when"] == null)
                {
                    string id = (string)pv;
                    var inst = Instantiate(id, owner["with"] as JObject, path, stack);
                    owner.Remove("preset");
                    owner.Remove("with");
                    owner["_preset"] = id;
                    if (inst != null)
                    {
                        if (owner["kind"] == null && inst.Kind != null) owner["kind"] = inst.Kind;
                        MergeInto(owner, inst, path, components, stack, 0);
                        if (inst.Rules != null) owner["rules"] = Concat(ExpandRules(inst.Rules, path + $" (preset {id})", owner, components, stack, 1), owner["rules"] as JArray);
                    }
                }
            }

            private static JArray Concat(JArray a, JArray b)
            {
                var r = new JArray();
                foreach (var x in a ?? []) r.Add(x);
                foreach (var x in b ?? []) r.Add(x);
                return r;
            }

            private JArray ExpandRules(JArray rules, string path, JObject owner, bool components, List<string> stack, int depth, bool top = false)
            {
                var result = new JArray();
                for (int i = 0; i < rules.Count; i++)
                {
                    string rp = top ? $"{path}[{i}]" : $"{path}.rules[{i}]";
                    if (rules[i] is not JObject rule) { result.Add(rules[i]); continue; }
                    if (rule["preset"] is JValue pv && pv.Type == JTokenType.String)
                    {
                        string id = (string)pv;
                        if (depth >= MaxDepth) { Error(rp, $"presets nest deeper than {MaxDepth}"); continue; }
                        if (stack.Contains(id)) { Error(rp, $"preset \"{id}\" uses itself ({string.Join(" > ", stack.Append(id))})"); continue; }
                        var inst = Instantiate(id, rule["with"] as JObject, rp, stack);
                        if (inst == null) continue;
                        stack.Add(id);
                        MergeInto(owner, inst, rp, components, stack, depth);
                        foreach (var r in ExpandRules(inst.Rules ?? [], rp + $" (preset {id})", owner, components, stack, depth + 1)) result.Add(r);
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    var copy = (JObject)rule.DeepClone();
                    copy[SourceField] ??= rp;
                    result.Add(copy);
                }
                return result;
            }

            private void MergeInto(JObject owner, Inst inst, string path, bool components, List<string> stack, int depth)
            {
                if (inst.Vars != null)
                {
                    var vars = owner["vars"] as JObject ?? new JObject();
                    foreach (var v in inst.Vars.Properties()) if (vars[v.Name] == null) vars[v.Name] = v.Value.DeepClone();
                    owner["vars"] = vars;
                }
                if (inst.Labels != null)
                {
                    var labels = owner["labels"] as JArray ?? new JArray();
                    foreach (var l in inst.Labels) if (!labels.Any(x => JToken.DeepEquals(x, l))) labels.Add(l.DeepClone());
                    owner["labels"] = labels;
                }
                if (inst.Components == null || inst.Components.Count == 0) return;
                if (!components) { Warn(path, $"preset \"{inst.Id}\" carries components ({string.Join(", ", inst.Components.Properties().Select(p => p.Name))}), which only apply on a unit, entity or place"); return; }
                MergeUnder(owner, inst.Components);
            }

            /// <summary>Copies from's fields into to where to has none; objects merge recursively (to wins); the
            /// statuses and attacks lists are concatenated.</summary>
            private static void MergeUnder(JObject to, JObject from)
            {
                foreach (var p in from.Properties())
                {
                    var mine = to[p.Name];
                    if (mine == null || mine.Type == JTokenType.Null) to[p.Name] = p.Value.DeepClone();
                    else if (mine is JObject mo && p.Value is JObject fo) MergeUnder(mo, fo);
                    else if (mine is JArray ma && p.Value is JArray fa && (p.Name == "statuses" || p.Name == "attacks"))
                        foreach (var x in fa) if (!ma.Any(y => JToken.DeepEquals(x, y))) ma.Add(x.DeepClone());
                }
            }

            private sealed class Inst
            {
                public string Id, Kind;
                public JArray Rules, Labels;
                public JObject Vars, Components;
            }

            private Inst Instantiate(string id, JObject with, string path, List<string> stack)
            {
                var preset = Find(pack, id);
                if (preset == null)
                {
                    Error(path, $"preset \"{id}\" not found" + ExprChecker.Suggest(id, KnownIds(pack)));
                    return null;
                }
                var values = new Dictionary<string, JToken>(StringComparer.Ordinal);
                var ps = preset.Params ?? [];
                foreach (var w in with?.Properties() ?? [])
                    if (!ps.ContainsKey(w.Name)) Error($"{path}.with.{w.Name}", $"preset \"{id}\" has no param \"{w.Name}\"" + ExprChecker.Suggest(w.Name, ps.Keys));
                foreach (var kv in ps)
                {
                    var spec = kv.Value ?? new PresetParamDef();
                    var v = with?[kv.Key];
                    string vp = $"{path}.with.{kv.Key}";
                    if (v == null || v.Type == JTokenType.Null)
                    {
                        v = spec.Default;
                        vp = $"{path} (preset {id}, param {kv.Key} default)";
                    }
                    if (v == null) { Error(path, $"preset \"{id}\" needs \"{kv.Key}\" ({spec.Type}{(spec.Description != null ? ": " + spec.Description : "")})"); continue; }
                    CheckValue(spec, kv.Key, v, vp);
                    values[kv.Key] = v;
                }
                var json = JObject.FromObject(preset, Serializer);
                var inst = new Inst
                {
                    Id = id,
                    Kind = preset.Kind,
                    Rules = Subst(json["rules"], values) as JArray,
                    Labels = Subst(json["labels"], values) as JArray,
                    Vars = Subst(json["vars"], values) as JObject,
                    Components = new JObject(),
                };
                foreach (var c in preset.Components ?? new Dictionary<string, JToken>())
                    inst.Components[c.Key] = Subst(c.Value, values);
                return inst;
            }

            private void CheckValue(PresetParamDef spec, string name, JToken v, string path)
            {
                string type = spec.Type ?? "number";
                bool isExpr = v.Type == JTokenType.String && ((string)v).StartsWith("=");
                switch (type)
                {
                    case "number": case "int":
                        if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)
                        {
                            double d = (double)v;
                            if (spec.Min is double lo && d < lo || spec.Max is double hi && d > hi) Error(path, $"{name} must be between {spec.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {spec.Max?.ToString(CultureInfo.InvariantCulture) ?? "∞"}");
                            if (type == "int" && d != Math.Floor(d)) Error(path, $"{name} must be a whole number");
                        }
                        else if (v.Type != JTokenType.String) Error(path, $"\"{name}\" expects a number, got {v.Type.ToString().ToLowerInvariant()} {v.ToString(Formatting.None)}");
                        else if (!isExpr && !LooksLikeExpression((string)v)) Error(path, $"\"{name}\" expects a number: \"{v}\" isn't a number or an expression");
                        break;
                    case "bool":
                        if (v.Type != JTokenType.Boolean && v.Type != JTokenType.String) Error(path, $"\"{name}\" expects true or false");
                        break;
                    case "enum":
                        if (spec.Options != null && v.Type == JTokenType.String && !isExpr && !spec.Options.Contains((string)v))
                            Error(path, $"\"{name}\" must be one of {string.Join(", ", spec.Options)}");
                        break;
                    default:
                        if (v.Type != JTokenType.String && v.Type != JTokenType.Array && v.Type != JTokenType.Object && v.Type != JTokenType.Integer && v.Type != JTokenType.Float && v.Type != JTokenType.Boolean)
                            Error(path, $"\"{name}\" has an unusable value");
                        break;
                }
            }
        }

        /// <summary>Whether a string parses and checks as an expression in a permissive scope (any roles; variables
        /// and names unchecked): "night - 2" passes, "two" doesn't.</summary>
        private static bool LooksLikeExpression(string s)
        {
            var issues = new List<ExprIssue>();
            ExprChecker.Check(s, new CheckScope { Event = LogicSchema.Events["damaged"], HasIt = true, HasSpawned = true }, issues, out _);
            return issues.Count == 0;
        }

        /// <summary>Substitutes {param} in every string of a JSON tree: a whole-string "{p}" becomes the value itself
        /// (so numbers stay numbers); inside a longer string the value is inserted as text (an "=expr" value as
        /// "(expr)").</summary>
        public static JToken Subst(JToken t, IReadOnlyDictionary<string, JToken> values)
        {
            if (t == null) return null;
            switch (t)
            {
                case JObject o:
                {
                    var r = new JObject();
                    foreach (var p in o.Properties()) r[p.Name] = Subst(p.Value, values);
                    return r;
                }
                case JArray a: return new JArray(a.Select(x => Subst(x, values)));
                case JValue v when v.Type == JTokenType.String:
                {
                    string s = (string)v;
                    if (s.IndexOf('{') < 0) return v.DeepClone();
                    if (s.Length > 2 && s[0] == '{' && s[s.Length - 1] == '}' && values.TryGetValue(s.Substring(1, s.Length - 2), out var whole)) return whole.DeepClone();
                    foreach (var kv in values)
                    {
                        string key = "{" + kv.Key + "}";
                        if (s.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                        s = s.Replace(key, AsText(kv.Value));
                    }
                    return new JValue(s);
                }
                default: return t.DeepClone();
            }
        }

        private static string AsText(JToken v) => v.Type switch
        {
            JTokenType.String => ((string)v).StartsWith("=") ? "(" + ((string)v).Substring(1) + ")" : (string)v,
            JTokenType.Integer => ((long)v).ToString(CultureInfo.InvariantCulture),
            JTokenType.Float => ((double)v).ToString(CultureInfo.InvariantCulture),
            JTokenType.Boolean => (bool)v ? "true" : "false",
            _ => v.ToString(Formatting.None),
        };

        /// <summary>A preset's description with its params filled in (for cards and pickers).</summary>
        public static string Describe(PresetDef preset, JObject with)
        {
            if (preset?.Description == null) return preset?.Name;
            var values = new Dictionary<string, JToken>();
            foreach (var kv in preset.Params ?? []) values[kv.Key] = with?[kv.Key] ?? kv.Value?.Default ?? "?";
            return (string)Subst(new JValue(preset.Description), values);
        }
    }
}
