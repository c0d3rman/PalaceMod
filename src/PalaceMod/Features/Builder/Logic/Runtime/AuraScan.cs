using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model.Logic;

/*
    Aura rings (docs/builder-engine.md, "Aura rings"): what a rule does over a radius, read from its JSON when it's
    compiled. A rule that runs on its own (`every`, `near`) and picks things within a fixed radius, e.g.
    `all('ally and unit', 6)` or `count('#lantern', 8)` anchored at self, or `near`'s range, is an aura: the runtime
    (Bodies/World/AuraRings.cs) draws a ring of that radius on the ground round its owner. When the owner is a unit
    and the filter names a label or template (`#lantern`, `is:totem`), the ring goes on the things it names instead
    (the lantern's 8 m, not a ring round every ghost). The rule's `ring` field overrides all of it.
*/
namespace PalaceMod.Builder.Logic
{
    internal sealed class RingSpec
    {
        public float Radius;
        /// <summary>"buff" | "harm" | "neutral": the default colour.</summary>
        public string Kind = "neutral";
        /// <summary>An explicit colour (#rrggbb[aa]), or null for the kind's.</summary>
        public string Color;
        /// <summary>"ring" (the game's tower-range ring) | "soft" (its lighter preview ring) | "line" (a thin line).</summary>
        public string Style = "ring";
        /// <summary>"auto" | "self" | "target": where the ring goes (auto: the target when the owner is a unit and
        /// the filter names a label or template).</summary>
        public string On = "auto";
        /// <summary>The label or template the filter names (a candidate for On = target), or null.</summary>
        public string TargetLabel, TargetTemplate;
        /// <summary>The rule's `when.during` (night | day | always): the ring shows only then.</summary>
        public string During = "always";

        public string Key => $"{Radius.ToString("0.##", CultureInfo.InvariantCulture)}|{Kind}|{Color}|{Style}";
    }

    internal static class AuraScan
    {
        /// <summary>Selectors whose second argument is a radius round their anchor.</summary>
        private static readonly HashSet<string> Selectors = new(StringComparer.Ordinal)
        {
            "all", "count", "nearest", "farthest", "weakest", "strongest", "random", "best",
        };

        public const float MinRadius = 0.5f, MaxRadius = 60f;

        /// <summary>The rings a rule implies (null when none), from its JSON and its `when.on`.</summary>
        public static List<RingSpec> FromRule(JObject json, string evt, JObject when)
        {
            var ring = json["ring"];
            if (ring != null && (ring.Type == JTokenType.Boolean && !(bool)ring || ring.Type == JTokenType.String && (string)ring is "none" or "hide" or "off")) return null;
            var opt = ring as JObject;
            if (opt != null && opt["show"] is JValue sv && sv.Type == JTokenType.Boolean && !(bool)sv) return null;
            bool forced = ring != null;
            if (!forced && evt is not ("every" or "near")) return null;
            // One-shot rules (`once`, or ones that destroy their owner: a kamikaze's blast) aren't standing auras.
            if (!forced && ((bool?)json["once"] == true || DestroysSelf(json["do"]))) return null;

            var found = new List<(float r, string filter)>();
            if (evt == "near" && when?["range"] is JValue rv && rv.Type is JTokenType.Integer or JTokenType.Float)
                found.Add(((float)rv, (string)when["filter"] ?? "foe"));
            Walk(json["if"], found);
            Walk(json["do"], found);

            bool damages = false, heals = false;
            ActionsUsed(json["do"], ref damages, ref heals);

            var list = new List<RingSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (opt?["radius"] is JValue orad && orad.Type is JTokenType.Integer or JTokenType.Float)
                found = [((float)orad, found.Count > 0 ? found[0].filter : null)];
            foreach (var (r, filter) in found)
            {
                if (r < MinRadius || r > MaxRadius) continue;
                var spec = new RingSpec { Radius = r, During = (string)when?["during"] ?? "always" };
                var f = filter != null ? Filter.Parse(filter, out _, out _) : null;
                bool foe = false, ally = false;
                if (f != null) Terms(f, ref foe, ref ally, ref spec.TargetLabel, ref spec.TargetTemplate);
                spec.Kind = damages || foe && !heals ? "harm" : heals || ally ? "buff" : "neutral";
                if (opt != null)
                {
                    if ((string)opt["kind"] is "buff" or "harm" or "neutral") spec.Kind = (string)opt["kind"];
                    if ((string)opt["color"] is { Length: > 0 } c) spec.Color = c;
                    if ((string)opt["style"] is "ring" or "soft" or "line") spec.Style = (string)opt["style"];
                    if ((string)opt["on"] is "self" or "target" or "auto") spec.On = (string)opt["on"];
                }
                if (seen.Add(spec.Key + "|" + spec.TargetLabel + "|" + spec.TargetTemplate)) list.Add(spec);
            }
            return list.Count > 0 ? list : null;
        }

        /// <summary>An entity's own `rings`: false/"none" turns off the rings its rules imply; a list adds rings.</summary>
        public static bool RingsOff(JToken rings) =>
            rings != null && (rings.Type == JTokenType.Boolean && !(bool)rings || rings.Type == JTokenType.String && (string)rings is "none" or "hide" or "off");

        public static List<RingSpec> Explicit(JToken rings)
        {
            if (rings is not JArray a) return null;
            var list = new List<RingSpec>();
            foreach (var t in a)
            {
                if (t is not JObject o || o["radius"] is not JValue rv || rv.Type is not (JTokenType.Integer or JTokenType.Float)) continue;
                float r = (float)rv;
                if (r < MinRadius || r > MaxRadius * 2) continue;
                var s = new RingSpec { Radius = r, On = "self" };
                if ((string)o["kind"] is "buff" or "harm" or "neutral") s.Kind = (string)o["kind"];
                if ((string)o["color"] is { Length: > 0 } c) s.Color = c;
                if ((string)o["style"] is "ring" or "soft" or "line") s.Style = (string)o["style"];
                if ((string)o["during"] is "night" or "day" or "always") s.During = (string)o["during"];
                list.Add(s);
            }
            return list.Count > 0 ? list : null;
        }

        private static void Walk(JToken t, List<(float, string)> found)
        {
            switch (t)
            {
                case JArray a: foreach (var x in a) Walk(x, found); break;
                case JObject o: foreach (var p in o.Properties()) if (p.Name != "ring") Walk(p.Value, found); break;
                case JValue v when v.Type == JTokenType.String:
                    var s = ((string)v).Trim();
                    if (s.StartsWith("=")) s = s.Substring(1);
                    if (s.IndexOf('(') < 0 || s.IndexOf('{') >= 0 || s.Length > 2000) return;
                    var node = Expr.Parse(s, out _, out _);
                    if (node != null) Calls(node, found);
                    break;
            }
        }

        private static void Calls(ExprNode n, List<(float, string)> found)
        {
            var stack = new Stack<ExprNode>();
            stack.Push(n);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (x.Kind == ExprKind.Call && Selectors.Contains(x.Name) && x.Args.Length >= 2
                    && (x.Target == null || x.Target.Kind == ExprKind.Role && x.Target.Name == "self")
                    && x.Args[1].Kind == ExprKind.Number)
                    found.Add(((float)(double)x.Args[1].Literal, x.Args[0].Kind == ExprKind.Text ? (string)x.Args[0].Literal : null));
                if (x.Target != null) stack.Push(x.Target);
                foreach (var a in x.Args) if (a != null) stack.Push(a);
            }
        }

        // Positive terms only (under and, not under or/not).
        private static void Terms(FilterNode f, ref bool foe, ref bool ally, ref string label, ref string template)
        {
            switch (f.Kind)
            {
                case FilterKind.And:
                    foreach (var a in f.Args) Terms(a, ref foe, ref ally, ref label, ref template);
                    break;
                case FilterKind.Term:
                    if (f.Term is "foe") foe = true;
                    else if (f.Term is "ally") ally = true;
                    else if (f.Term == "label") label ??= f.Arg;
                    else if (f.Term == "is") template ??= f.Arg;
                    break;
            }
        }

        private static bool DestroysSelf(JToken t)
        {
            if (t is not JArray a) return false;
            foreach (var x in a)
                if (x is JObject o && (string)o["do"] == "destroy" && (o["target"] == null || (string)o["target"] == "self")) return true;
            return false;
        }

        private static void ActionsUsed(JToken t, ref bool damages, ref bool heals)
        {
            if (t is JArray a) { foreach (var x in a) ActionsUsed(x, ref damages, ref heals); return; }
            if (t is not JObject o) return;
            var d = (string)o["do"];
            if (d is "damage" or "kill" or "stun") damages = true;
            if (d is "heal") heals = true;
            foreach (var p in o.Properties()) if (p.Value is JArray or JObject) ActionsUsed(p.Value, ref damages, ref heals);
        }
    }
}
