using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    Rules as plain sentences, for cards, hovers, the trace and wires: "When this dies → Spawn Slime × 2 at this".
    Pure C# (unit tested).
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class RuleText
    {
        /// <summary>Optional display names for templates and @names (the Studio fills these from the pack).</summary>
        public static Func<string, string> TemplateName = id => id;
        public static Func<string, string> PlaceName = name => name;

        public static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>"this", "the killer", "Gate 1", "nearest foe within 12 m", "ƒ self.hp * 2".</summary>
        public static string Value(JToken t, ParamUi p = null, EventUi ev = null)
        {
            if (t == null || t.Type == JTokenType.Null) return p?.Default != null ? Value(new JValue(p.Default), null, ev) : "";
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float: return Num((double)t);
                case JTokenType.Boolean: return (bool)t ? "yes" : "no";
                case JTokenType.Array: return string.Join(", ", t.Select(x => Value(x, null, ev)));
                case JTokenType.Object: return t["do"] != null ? Action((JObject)t, ev) : "{…}";
            }
            string s = (string)t ?? "";
            if (p?.Kind is ParamKind.Text or ParamKind.Signal or ParamKind.Var or ParamKind.Rule or ParamKind.Status or ParamKind.Choice)
                return s.StartsWith("=") ? "ƒ " + s.Substring(1) : s;
            if (p?.Kind == ParamKind.Template && !s.StartsWith("=")) return TemplateName(s.StartsWith("@@") ? s.Substring(2) : s);
            if (p?.Kind == ParamKind.Filter) return Filter(s);
            if (p?.Kind is ParamKind.Condition) return Formula(s, ev);
            return Expr(s.StartsWith("=") ? s.Substring(1) : s, ev);
        }

        /// <summary>A short reading of an expression: roles and @names in words, selectors spelled out; anything more
        /// complex stays as the formula, marked ƒ.</summary>
        public static string Expr(string s, EventUi ev = null)
        {
            s = s.Trim();
            if (Role(s, ev) is string role) return role;
            if (s.StartsWith("@") && IsIdent(s.Substring(1))) return PlaceName(s.Substring(1));
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return Num(d);
            if (s.Length > 1 && s[0] == '\'' && s[s.Length - 1] == '\'') return "\"" + s.Substring(1, s.Length - 2) + "\"";
            // nearest('foe and Tower', 20) → nearest foe Tower within 20 m
            foreach (var fn in new[] { "nearest", "farthest", "weakest", "strongest", "random", "all", "count" })
            {
                if (!s.StartsWith(fn + "(") || !s.EndsWith(")")) continue;
                var args = SplitArgs(s.Substring(fn.Length + 1, s.Length - fn.Length - 2));
                if (args.Count is < 1 or > 2 || !IsQuoted(args[0])) break;
                string f = Filter(args[0].Substring(1, args[0].Length - 2));
                string range = args.Count == 2 ? $" within {Expr(args[1], ev)} m" : "";
                string word = fn switch { "all" => "every", "count" => "number of", _ => fn };
                return $"{word} {f}{range}";
            }
            return "ƒ " + s;
        }

        private static string Role(string s, EventUi ev) => s switch
        {
            "self" => "this",
            "other" => ev?.Other != null ? "the " + ev.Other.Replace("the ", "") : "the other",
            "subject" => "the one it happened to",
            "spawned" => "the spawned",
            "it" => "each",
            "king" => "the king",
            "castle" => "the castle",
            "level" => "the level",
            "all" => "everyone",
            "event.pos" => "where it happened",
            "event.value" => ev?.Value ?? "the value",
            "killer" or "victim" or "attacker" or "target" or "entrant" or "collector" or "sender" => "the " + s,
            _ => null,
        };

        /// <summary>"foe and Building and not Wall" → "foe building, not Wall".</summary>
        public static string Filter(string f)
        {
            if (string.IsNullOrWhiteSpace(f)) return "anything";
            var words = f.Split([' '], StringSplitOptions.RemoveEmptyEntries).ToList();
            var outWords = new List<string>();
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i];
                if (w == "and") continue;
                if (w == "not" && i + 1 < words.Count) { outWords.Add("not " + Term(words[++i])); continue; }
                outWords.Add(Term(w));
            }
            return string.Join(" ", outWords);
        }

        private static string Term(string t) => t switch
        {
            "unit" or "building" or "prop" or "projectile" or "area" or "pickup" or "coin" or "king" or "castle" => t,
            _ when t.StartsWith("#") => t,
            _ when t.StartsWith("is:") => TemplateName(t.Substring(3)),
            _ when t.StartsWith("in:@") => "in " + PlaceName(t.Substring(4)),
            _ when t.StartsWith("has:") => "with " + t.Substring(4),
            _ when t.StartsWith("type:") => t.Substring(5),
            _ => t,
        };

        private static bool IsQuoted(string s) => s.Length >= 2 && ((s[0] == '\'' && s[s.Length - 1] == '\'') || (s[0] == '"' && s[s.Length - 1] == '"'));

        private static bool IsIdent(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');

        /// <summary>Splits function arguments at top-level commas.</summary>
        public static List<string> SplitArgs(string s)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') quote = c;
                else if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == ',' && depth == 0) { list.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            list.Add(s.Substring(start).Trim());
            return list;
        }

        // ---- Events -----------------------------------------------------------------------------------------------

        /// <summary>"this dies", "every 8 s at night", "a foe unit within 12 m dies".</summary>
        public static string When(JObject rule, OwnerKind owner = OwnerKind.Attach)
        {
            string name = LogicDoc.EventOf(rule);
            if (name == null) return "(pick an event)";
            var ev = LogicCatalog.Event(name);
            var when = rule["when"] as JObject;
            string phrase = ev?.Phrase ?? name;
            phrase = Fill(phrase, h => when?[h] is { } v ? Value(v, ev?.Params.FirstOrDefault(p => p.Name == h), ev) : Value(null, ev?.Params.FirstOrDefault(p => p.Name == h), ev), out _);
            if (when?["during"] is { } during && (string)during is "night" or "day") phrase += $" at {(string)during}";
            string of = (string)when?["of"];
            if (of != null)
            {
                string range = when["range"] is { } r ? $" within {Value(r)} m" : "";
                // "dies" about another: "a foe unit within 12 m dies"
                return $"a {Filter(of)}{range} {Unthis(phrase)}";
            }
            bool selfPhrase = ev != null && ev.CanOf && owner != OwnerKind.Level;
            return selfPhrase ? "this " + phrase : phrase;
        }

        private static string Unthis(string phrase) => phrase;

        // ---- Actions ----------------------------------------------------------------------------------------------

        /// <summary>"Spawn Slime × 2 at this".</summary>
        public static string Action(JObject a, EventUi ev = null)
        {
            string name = (string)a?["do"];
            var ui = LogicCatalog.Action(name);
            if (ui == null) return name == null ? "(empty)" : LogicCatalog.Humanize(name);
            string s;
            if (name == "setStat") s = SetStat(a, ev);
            else
            {
                s = Fill(ui.Sentence, h =>
                {
                    var p = ui.Params.FirstOrDefault(x => x.Name == h);
                    var v = a[h];
                    if (v == null && h == "target") return "this";
                    if (v == null && h == "to") return "this";
                    return Value(v, p, ev);
                }, out bool missing);
                // Only a needed field left empty is a problem; an optional one just drops out of the sentence.
                if (missing && ui.Params.Any(p => p.Required && p.Default == null && p.Name is not ("target" or "to") && LogicDoc.IsEmpty(a[p.Name]))) s += " (!)";
            }
            int nested = ui.Nested.Sum(n => (a[n] as JArray)?.Count ?? 0) + ((a["options"] as JArray)?.Count ?? 0);
            if (nested > 0) s += $" (+{nested})";
            if (a["delay"] is { } delay && name == "signal") s += $" after {Value(delay)} s";
            return Cap(s);
        }

        private static string SetStat(JObject a, EventUi ev)
        {
            string target = a["target"] == null ? "this" : Value(a["target"], null, ev);
            string stat = (string)a["stat"] ?? "?";
            string how = a["mult"] != null ? "× " + Value(a["mult"]) : a["add"] != null ? ((a["add"].Type is JTokenType.Integer or JTokenType.Float && (double)a["add"] < 0) ? "− " + Num(-(double)a["add"]) : "+ " + Value(a["add"])) : a["value"] != null ? "= " + Value(a["value"]) : "";
            string dur = a["for"] is { } f ? ((string)f is "night" or "day" ? $" until {((string)f == "night" ? "dawn" : "dusk")}" : $" for {Value(f)} s") : "";
            return $"{LogicCatalog.Humanize(stat)} of {target} {how}{dur}";
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>Fills {hole}s; a hole with no value drops it and the connective word before it ("at", "for", "×").
        /// missing is set when a hole is empty.</summary>
        public static string Fill(string template, Func<string, string> value, out bool missing)
        {
            missing = false;
            var tokens = template.Split(' ').ToList();
            var outTokens = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                string tok = tokens[i];
                int open = tok.IndexOf('{');
                if (open < 0) { outTokens.Add(tok); continue; }
                string result = tok;
                bool empty = false;
                while ((open = result.IndexOf('{')) >= 0)
                {
                    int close = result.IndexOf('}', open);
                    if (close < 0) break;
                    string hole = result.Substring(open + 1, close - open - 1);
                    string v = value(hole) ?? "";
                    if (v.Length == 0) empty = true;
                    result = result.Substring(0, open) + v + result.Substring(close + 1);
                }
                if (empty && result.Trim('"', '#', '×', ' ').Length == 0)
                {
                    missing = true;
                    // Drop a lowercase connective right before it ("at", "for", "every", "×").
                    if (outTokens.Count > 0 && IsConnective(outTokens[outTokens.Count - 1])) outTokens.RemoveAt(outTokens.Count - 1);
                    continue;
                }
                outTokens.Add(result);
            }
            return string.Join(" ", outTokens);
        }

        private static bool IsConnective(string w) => w is "at" or "for" or "every" or "×" or "to" or "of" or "on" or "from" or "by" or "in" or "with";

        // ---- Whole rules ------------------------------------------------------------------------------------------

        /// <summary>One line: "When this dies → Spawn Slime × 2 at this; Effect smoke".</summary>
        public static string Summary(JObject rule, OwnerKind owner = OwnerKind.Attach)
        {
            if (rule == null) return "";
            if (rule["preset"] != null) return $"Preset {(string)rule["preset"]}";
            var ev = LogicCatalog.Event(LogicDoc.EventOf(rule));
            var conds = LogicDoc.Conditions(rule);
            string cond = conds.Count == 0 ? "" : " if " + string.Join(" and ", conds.Select(c => Formula(c, ev)));
            var acts = (rule["do"] as JArray)?.OfType<JObject>().ToList() ?? [];
            string then = acts.Count == 0 ? "(nothing yet)" : string.Join("; ", acts.Take(2).Select(a => Action(a, ev))) + (acts.Count > 2 ? $"; +{acts.Count - 2} more" : "");
            return $"When {When(rule, owner)}{cond} → {then}";
        }

        /// <summary>A formula where one is expected (a condition): roles in words when it's just a role, else as written.</summary>
        public static string Formula(string s, EventUi ev = null)
        {
            string e = Expr(s, ev);
            return e.StartsWith("ƒ ") ? e.Substring(2) : e;
        }

        /// <summary>Options shown under the card: "once · cooldown 5 s · off".</summary>
        public static string Options(JObject rule)
        {
            var parts = new List<string>();
            if (rule?["once"]?.Type == JTokenType.Boolean && (bool)rule["once"]) parts.Add("once");
            if (rule?["cooldown"] is { } cd && cd.Type is JTokenType.Integer or JTokenType.Float && (double)cd > 0) parts.Add($"cooldown {Num((double)cd)} s");
            if (rule?["enabled"]?.Type == JTokenType.Boolean && !(bool)rule["enabled"]) parts.Add("starts off");
            return string.Join(" · ", parts);
        }
    }
}
