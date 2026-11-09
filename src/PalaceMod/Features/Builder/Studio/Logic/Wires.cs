using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    Wires (docs/builder-engine.md, "Signals, wires and built-in inputs"): a wire is exactly a rule on the source,
    { when: <event>, do: [{ do: signal, name: <input>, to: @target }] }, plus a listener on the target
    ({ when: { on: signal, name: <input> }, do: [] }) unless the input is built in. There is no wire table: the Studio's
    wire overlay reads the rules, so agents' wires show up and Studio wires are plain rules. Pure C# (unit tested).
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class Wire
    {
        public OwnerKind FromKind;
        public string FromId;
        public int Rule, Action;
        public string Event;
        public string Signal;
        public string ToName;
        /// <summary>Resolved target owner (null if the name isn't in the level).</summary>
        public OwnerKind? ToKind;
        public string ToId;
        public bool Delayed;

        public string Label => $"{Event} → {Signal}";
        public override string ToString() => $"{FromId}.{Label} → @{ToName}";
    }

    internal static class Wires
    {
        /// <summary>Inputs every entity (or some components) has without a listener rule.</summary>
        public static readonly (string Name, string Desc)[] BuiltIns =
        [
            ("enable", "switches it on"), ("disable", "switches it off"), ("toggle", "on if off, off if on"), ("destroy", "removes it"),
            ("open", "a door's open pose"), ("closed", "a door's closed pose"), ("start", "a mover starts"), ("stop", "a mover stops"),
            ("reverse", "a mover turns back"), ("show", "an area shows"), ("hide", "an area hides"), ("lock", "can't be used"), ("unlock", "can be used again"),
        ];

        public static bool IsBuiltIn(string input) => BuiltIns.Any(b => b.Name == input);

        /// <summary>Every wire in a level's logic: top-level signal actions sent to one @name.</summary>
        public static List<Wire> Scan(JObject logic)
        {
            var list = new List<Wire>();
            if (logic == null) return list;
            var byName = new Dictionary<string, (OwnerKind, string)>(StringComparer.Ordinal);
            foreach (var (kind, id, node) in LogicDoc.Owners(logic))
            {
                if (kind == OwnerKind.Place) { byName[id] = (kind, id); if ((string)node["name"] is string pn) byName[pn] = (kind, id); }
                else if (kind == OwnerKind.Attach && (string)node["name"] is string an) byName[an] = (kind, id);
            }
            foreach (var (kind, id, node) in LogicDoc.Owners(logic))
            {
                var rules = LogicDoc.Rules(node);
                for (int r = 0; r < (rules?.Count ?? 0); r++)
                {
                    if (rules[r] is not JObject rule) continue;
                    var acts = rule["do"] as JArray;
                    for (int a = 0; a < (acts?.Count ?? 0); a++)
                    {
                        if (acts[a] is not JObject act || (string)act["do"] != "signal") continue;
                        string to = (string)act["to"];
                        if (to == null || !to.StartsWith("@") || to.StartsWith("@@")) continue;
                        string name = to.Substring(1);
                        var w = new Wire
                        {
                            FromKind = kind, FromId = id, Rule = r, Action = a, Event = LogicDoc.EventOf(rule) ?? "?",
                            Signal = (string)act["name"] ?? "?", ToName = name, Delayed = act["delay"] != null,
                        };
                        if (byName.TryGetValue(name, out var target)) { w.ToKind = target.Item1; w.ToId = target.Item2; }
                        list.Add(w);
                    }
                }
            }
            return list;
        }

        /// <summary>Writes a wire: a rule on the source and, for a signal that isn't built in, a listener on the target
        /// (unless it has one). Returns the target's listener rule index (or -1).</summary>
        public static int Add(JObject source, JObject target, string targetName, string ev, JObject eventParams, string input, JToken value = null, double delay = 0)
        {
            var signal = new JObject { ["do"] = "signal", ["name"] = input, ["to"] = "@" + targetName };
            if (value != null) signal["value"] = value;
            if (delay > 0) signal["delay"] = delay;
            var rules = LogicDoc.EnsureRules(source);
            JToken when = ev;
            if (eventParams != null && eventParams.Properties().Any()) { var o = (JObject)eventParams.DeepClone(); o["on"] = ev; when = o; }
            // Joins a source rule with the same event and no condition whose actions are all signals (or none yet: a
            // logic object's output), so "dies → open A, open B" stays one rule.
            var same = rules.OfType<JObject>().FirstOrDefault(r => JToken.DeepEquals(Normal(r["when"]), Normal(when)) && r["if"] == null
                && ((r["do"] as JArray)?.All(a => (string)a["do"] == "signal") ?? true));
            if (same != null)
            {
                if (same["do"] is not JArray list) same["do"] = list = new JArray();
                list.Add(signal);
            }
            else rules.Add(new JObject { ["when"] = when, ["do"] = new JArray(signal) });
            if (IsBuiltIn(input)) return -1;
            var trules = LogicDoc.EnsureRules(target);
            for (int i = 0; i < trules.Count; i++)
                if (trules[i] is JObject r && LogicDoc.EventOf(r) == "signal" && (string)(r["when"] as JObject)?["name"] == input) return i;
            trules.Add(new JObject { ["when"] = new JObject { ["on"] = "signal", ["name"] = input }, ["do"] = new JArray() });
            return trules.Count - 1;
        }

        private static JToken Normal(JToken when) => when is JValue v ? new JObject { ["on"] = (string)v } : when;

        /// <summary>Removes a wire's signal action (and its rule if nothing is left) and, if no other wire sends that signal
        /// to the target, the target's empty listener.</summary>
        public static void Remove(JObject logic, Wire w)
        {
            var source = LogicDoc.Node(logic, w.FromKind, w.FromId, false);
            if (LogicDoc.Rules(source) is not JArray rules || w.Rule >= rules.Count || rules[w.Rule] is not JObject rule) return;
            if (rule["do"] is JArray acts && w.Action < acts.Count) acts.RemoveAt(w.Action);
            // An emptied rule goes, except on placed logic objects, whose empty rules are their outputs.
            if ((rule["do"] as JArray)?.Count == 0 && w.FromKind != OwnerKind.Place) rules.RemoveAt(w.Rule);
            if (w.ToKind == null || IsBuiltIn(w.Signal)) return;
            if (Scan(logic).Any(o => o.ToKind == w.ToKind && o.ToId == w.ToId && o.Signal == w.Signal)) return;
            var target = LogicDoc.Node(logic, w.ToKind.Value, w.ToId, false);
            var trules = LogicDoc.Rules(target);
            for (int i = (trules?.Count ?? 0) - 1; i >= 0; i--)
                if (trules[i] is JObject r && LogicDoc.EventOf(r) == "signal" && (string)(r["when"] as JObject)?["name"] == w.Signal && ((r["do"] as JArray)?.Count ?? 0) == 0)
                    trules.RemoveAt(i);
        }

        /// <summary>A stable colour per signal name (hue from its hash).</summary>
        public static float Hue(string signal)
        {
            uint h = 2166136261;
            foreach (char c in signal ?? "") h = (h ^ c) * 16777619;
            return (h % 360) / 360f;
        }
    }
}
