using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

/*
    Where behaviour lives in the pack JSON (docs/builder-engine.md, "Where logic lives"), as the Studio edits it.

    The editor works on JSON (JObject/JArray): a rule card edits exactly the JSON agents write, and nothing here depends
    on the shape of the typed defs. A def field is read through its [JsonProperty] name when the def has one (the
    engine's typed LevelDef.logic, UnitDef.rules...), else from the def's Extra (unknown fields), so this works before
    and after the typed model lands. Edits always happen inside a draft edit (one undo step).

    Owners (who a rule belongs to):
      level            levels[i].logic.rules (the level script; self is the level)
      attach:<key>     levels[i].logic.attach[<Studio key>]   (slot:…, add:…, prop:…, line:…, castle)
      place:<id>       levels[i].logic.places[id == <id>]
      unit             units[i] (rules, vars, labels, targeting, movement, attacks, statuses)
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal enum OwnerKind { Level, Attach, Place, Unit }

    internal static class LogicDoc
    {
        /// <summary>The fields a unit's behaviour uses (UnitDef, docs "Templates").</summary>
        public static readonly string[] UnitFields = ["labels", "vars", "rules", "targeting", "movement", "attacks", "statuses"];

        // ---- Def field access (typed property by JSON name, else Extra) -------------------------------------------

        private static readonly JsonSerializer Json = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private static PropertyInfo Typed(Type t, string jsonName) =>
            t.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName == jsonName && p.CanRead && p.CanWrite);

        private static IDictionary<string, JToken> ExtraOf(object def, bool create)
        {
            var p = def.GetType().GetProperty("Extra");
            var d = p?.GetValue(def) as IDictionary<string, JToken>;
            if (d == null && create && p != null) p.SetValue(def, d = new Dictionary<string, JToken>());
            return d;
        }

        /// <summary>A copy of a def field as JSON (null when absent).</summary>
        public static JToken Get(object def, string field)
        {
            if (def == null) return null;
            var typed = Typed(def.GetType(), field);
            if (typed != null)
            {
                var v = typed.GetValue(def);
                return v == null ? null : JToken.FromObject(v, Json);
            }
            return ExtraOf(def, false) is { } extra && extra.TryGetValue(field, out var t) ? t?.DeepClone() : null;
        }

        /// <summary>Writes a def field from JSON (null or an empty object/array removes it).</summary>
        public static void Set(object def, string field, JToken value)
        {
            if (def == null) return;
            if (IsEmpty(value)) value = null;
            var typed = Typed(def.GetType(), field);
            if (typed != null) { typed.SetValue(def, value?.ToObject(typed.PropertyType, Json)); return; }
            if (value == null) { ExtraOf(def, false)?.Remove(field); return; }
            ExtraOf(def, true)[field] = value;
        }

        public static bool IsEmpty(JToken t) =>
            t == null || t.Type == JTokenType.Null || (t is JObject o && !o.Properties().Any()) || (t is JArray a && a.Count == 0);

        // ---- Level logic -------------------------------------------------------------------------------------------

        public static JObject Logic(LevelDef lv) => Get(lv, "logic") as JObject;

        /// <summary>Edits a level's logic block in place: change gets a JObject (created if missing), which is pruned and
        /// written back.</summary>
        public static void EditLogic(LevelDef lv, Action<JObject> change)
        {
            var logic = Logic(lv) ?? new JObject();
            change(logic);
            Prune(logic);
            Set(lv, "logic", logic);
        }

        /// <summary>Drops empty containers (an attach entry with nothing left, empty rule lists, empty vars).</summary>
        public static void Prune(JObject logic)
        {
            if (logic["attach"] is JObject attach)
            {
                foreach (var p in attach.Properties().ToList())
                {
                    if (p.Value is JObject entry) PruneEmptyFields(entry);
                    if (IsEmpty(p.Value)) p.Remove();
                }
            }
            if (logic["places"] is JArray places)
                foreach (var place in places.OfType<JObject>()) PruneEmptyFields(place, keep: ["id", "kind", "entity", "pos"]);
            PruneEmptyFields(logic);
        }

        private static void PruneEmptyFields(JObject o, string[] keep = null)
        {
            foreach (var p in o.Properties().ToList())
                if ((keep == null || Array.IndexOf(keep, p.Name) < 0) && IsEmpty(p.Value)) p.Remove();
        }

        public static JArray Places(JObject logic) => logic?["places"] as JArray;

        public static JObject Place(JObject logic, string id) =>
            Places(logic)?.OfType<JObject>().FirstOrDefault(p => (string)p["id"] == id);

        public static JObject Attach(JObject logic, string key, bool create)
        {
            if (logic == null) return null;
            var attach = logic["attach"] as JObject;
            if (attach == null) { if (!create) return null; logic["attach"] = attach = new JObject(); }
            var entry = attach[key] as JObject;
            if (entry == null && create) attach[key] = entry = new JObject();
            return entry;
        }

        /// <summary>The attach key for a Studio item key: the castle's slot is "castle" (docs "Where logic lives").</summary>
        public static string AttachKey(string studioKey, bool isCastle) => isCastle ? "castle" : studioKey;

        // ---- Owners ------------------------------------------------------------------------------------------------

        /// <summary>The JSON node an owner's rules, vars, labels and name live in, inside a level's logic block.</summary>
        public static JObject Node(JObject logic, OwnerKind kind, string id, bool create)
        {
            switch (kind)
            {
                case OwnerKind.Level: return logic;
                case OwnerKind.Attach: return Attach(logic, id, create);
                case OwnerKind.Place: return Place(logic, id);
                default: return null;
            }
        }

        /// <summary>A unit's behaviour fields as one JSON object (the same shape as a place's).</summary>
        public static JObject UnitNode(UnitDef u)
        {
            var o = new JObject();
            if (u == null) return o;
            foreach (var f in UnitFields)
                if (Get(u, f) is { } t) o[f] = t;
            return o;
        }

        public static void EditUnit(UnitDef u, Action<JObject> change)
        {
            var node = UnitNode(u);
            change(node);
            foreach (var f in UnitFields) Set(u, f, node[f]);
        }

        public static JArray Rules(JObject node) => node?["rules"] as JArray;

        public static JArray EnsureRules(JObject node)
        {
            if (node["rules"] is JArray a) return a;
            node["rules"] = a = new JArray();
            return a;
        }

        /// <summary>The name other rules use for an owner: "@name" (attach "name", or a place's name or id).</summary>
        public static string RefName(OwnerKind kind, string id, JObject node) => kind switch
        {
            OwnerKind.Place => (string)node?["name"] ?? id,
            OwnerKind.Attach => (string)node?["name"],
            _ => null,
        };

        /// <summary>Every name in use (attach names and place names/ids), for unique names and the @ pickers.</summary>
        public static HashSet<string> Names(JObject logic)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (logic?["attach"] is JObject attach)
                foreach (var p in attach.Properties())
                    if ((string)p.Value?["name"] is string n) set.Add(n);
            foreach (var place in Places(logic)?.OfType<JObject>() ?? [])
            {
                if ((string)place["id"] is string id) set.Add(id);
                if ((string)place["name"] is string n) set.Add(n);
            }
            return set;
        }

        /// <summary>A valid id (Ids: lowercase a-z, 0-9, '-') from a display name, unique in the level: "Tower 3" →
        /// "tower-3", "North gate" → "north-gate". Formulas name it as @tower-3.</summary>
        public static string NameFor(string display, ISet<string> taken)
        {
            string root = Slug(display);
            if (!taken.Contains(root)) return root;
            for (int i = 2; ; i++) if (!taken.Contains($"{root}-{i}")) return $"{root}-{i}";
        }

        private static string Slug(string display)
        {
            var sb = new System.Text.StringBuilder();
            bool gap = false;
            foreach (char c in display ?? "")
            {
                if (c < 128 && char.IsLetterOrDigit(c))
                {
                    if (gap && sb.Length > 0) sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                    gap = false;
                }
                else gap = true;
                if (sb.Length >= 48) break;
            }
            return sb.Length == 0 ? "thing" : sb.ToString();
        }

        /// <summary>A unique place id from a kind or another id: "counter" → "counter1", "pressure-plate" →
        /// "pressure-plate1", a copy of "counter1" → "counter2".</summary>
        public static string NewPlaceId(JObject logic, string prefix)
        {
            var taken = Names(logic);
            string root = Slug(prefix).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-');
            if (root.Length == 0) root = "thing";
            for (int i = 1; ; i++) if (!taken.Contains(root + i)) return root + i;
        }

        // ---- Action lists inside a rule ----------------------------------------------------------------------------

        /// <summary>The action list at a path inside a rule: no steps is the rule's "do"; (1, "then") is the "then" list
        /// of its second action; a field "options/2" is the "do" of that action's third option (choose).</summary>
        public static JArray ActionList(JObject rule, IReadOnlyList<(int Index, string Field)> path, bool create)
        {
            var list = Child(rule, "do", create);
            foreach (var (index, field) in path ?? [])
            {
                if (list == null || index < 0 || index >= list.Count) return null;
                JToken cur = list[index];
                if (field.StartsWith("options/") && int.TryParse(field.Substring(8), out int oi))
                {
                    if (cur["options"] is not JArray opts || oi < 0 || oi >= opts.Count) return null;
                    list = Child(opts[oi], "do", create);
                }
                else list = Child(cur, field, create);
            }
            return list;
        }

        private static JArray Child(JToken parent, string field, bool create)
        {
            if (parent is not JObject o) return null;
            if (o[field] is JArray a) return a;
            if (!create) return null;
            o[field] = a = new JArray();
            return a;
        }

        /// <summary>The event name of a rule's "when" (the shorthand string, or its "on").</summary>
        public static string EventOf(JObject rule) => rule?["when"] switch
        {
            JValue v => (string)v,
            JObject o => (string)o["on"],
            _ => null,
        };

        /// <summary>The rule's "when" as an object (expanding the shorthand), for editing.</summary>
        public static JObject When(JObject rule)
        {
            if (rule["when"] is JObject o) return o;
            o = new JObject { ["on"] = (string)rule["when"] };
            rule["when"] = o;
            return o;
        }

        /// <summary>Writes "when" back as the shorthand when it has nothing but "on".</summary>
        public static void TidyWhen(JObject rule)
        {
            if (rule["when"] is JObject o && o.Properties().All(p => p.Name == "on" || LogicDoc.IsEmpty(p.Value)))
                rule["when"] = o["on"]?.DeepClone();
        }

        /// <summary>The rule's conditions as a list of expression strings ("if": a string or a list).</summary>
        public static List<string> Conditions(JObject rule) => rule?["if"] switch
        {
            JValue v when v.Type == JTokenType.String => [(string)v],
            JArray a => a.Select(t => (string)t).Where(s => s != null).ToList(),
            _ => [],
        };

        public static void SetConditions(JObject rule, IReadOnlyList<string> conds)
        {
            var list = conds.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (list.Count == 0) rule.Remove("if");
            else if (list.Count == 1) rule["if"] = list[0];
            else rule["if"] = new JArray(list);
        }

        /// <summary>A new rule for an event, with the event's required params at their defaults.</summary>
        public static JObject NewRule(string eventName)
        {
            var rule = new JObject();
            var e = LogicCatalog.Event(eventName);
            var required = e?.Params.Where(p => p.Required && p.Default != null).ToList() ?? [];
            if (required.Count == 0) rule["when"] = eventName;
            else
            {
                var when = new JObject { ["on"] = eventName };
                foreach (var p in required) when[p.Name] = Literal(p);
                rule["when"] = when;
            }
            rule["do"] = new JArray();
            return rule;
        }

        /// <summary>A new action with its required params at their defaults.</summary>
        public static JObject NewAction(string actionName)
        {
            var o = new JObject { ["do"] = actionName };
            var a = LogicCatalog.Action(actionName);
            foreach (var p in a?.Params ?? [])
                if (p.Required && p.Default != null && p.Name != "target") o[p.Name] = Literal(p);
            return o;
        }

        /// <summary>A param's default as a JSON literal of its kind.</summary>
        public static JToken Literal(ParamUi p)
        {
            if (p.Default == null) return null;
            if (p.Kind == ParamKind.Number && double.TryParse(p.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d == Math.Floor(d) && Math.Abs(d) < 1e9 ? new JValue((long)d) : new JValue(d);
            if (p.Kind == ParamKind.Bool) return new JValue(p.Default == "true");
            return new JValue(p.Default);
        }

        // ---- Whole-level scans (wires, the badge, rule counts) -----------------------------------------------------

        /// <summary>Every (owner kind, id, node) in a level's logic block, the level script first.</summary>
        public static IEnumerable<(OwnerKind Kind, string Id, JObject Node)> Owners(JObject logic)
        {
            if (logic == null) yield break;
            yield return (OwnerKind.Level, "level", logic);
            if (logic["attach"] is JObject attach)
                foreach (var p in attach.Properties())
                    if (p.Value is JObject o) yield return (OwnerKind.Attach, p.Name, o);
            foreach (var place in Places(logic)?.OfType<JObject>() ?? [])
                if ((string)place["id"] is string id) yield return (OwnerKind.Place, id, place);
        }

        /// <summary>Every action in a list and its nested lists (depth first).</summary>
        public static IEnumerable<JObject> AllActions(JArray list)
        {
            if (list == null) yield break;
            foreach (var a in list.OfType<JObject>())
            {
                yield return a;
                foreach (var p in a.Properties())
                {
                    if (p.Value is JArray nested && nested.Any(x => x is JObject xo && xo["do"] != null))
                        foreach (var n in AllActions(nested)) yield return n;
                }
                if (a["options"] is JArray opts)
                    foreach (var opt in opts.OfType<JObject>())
                        foreach (var n in AllActions(opt["do"] as JArray)) yield return n;
            }
        }
    }
}
