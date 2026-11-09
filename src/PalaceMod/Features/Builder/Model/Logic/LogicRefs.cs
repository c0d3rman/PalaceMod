using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PalaceMod.Builder.Model.Logic
{
    /// <summary>
    /// Template references inside rules: every action param of type "template" (spawn.entity, launch.entity,
    /// spawnGroup.unit, replace.with, waves.unit), plus attacks' launch.entity. Readers that follow unit references
    /// (the library's closure and renames, "used by", the agent kit's waves) use this instead of knowing actions.
    /// </summary>
    public static class LogicRefs
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        /// <summary>The literal template values (JValue strings, rewritable in place) in rules, actions or any JSON
        /// holding them. Expressions ("=self.template") are skipped.</summary>
        public static IEnumerable<JValue> TemplateValues(JToken token)
        {
            switch (token)
            {
                case JArray a:
                    foreach (var item in a) foreach (var v in TemplateValues(item)) yield return v;
                    break;
                case JObject o:
                {
                    if (o["do"] is JValue d && d.Type == JTokenType.String && LogicSchema.Actions.TryGetValue((string)d, out var spec))
                    {
                        foreach (var p in spec.Params)
                        {
                            var val = o[p.Name];
                            if (val == null) continue;
                            if (p.Type == "template" && val is JValue jv && jv.Type == JTokenType.String && !((string)jv).StartsWith("=")) yield return jv;
                            else if (p.Type == "actions") foreach (var v in TemplateValues(val)) yield return v;
                            else if (p.Type == "options" && val is JArray opts)
                                foreach (var opt in opts.OfType<JObject>()) foreach (var v in TemplateValues(opt["do"])) yield return v;
                        }
                        yield break;
                    }
                    if (o["launch"] is JObject launch && launch["entity"] is JValue le && le.Type == JTokenType.String && !((string)le).StartsWith("=")) yield return le;
                    foreach (var key in new[] { "do", "rules", "attacks", "places", "attach", "logic" })
                        if (o[key] is JToken child && child.Type != JTokenType.String)
                            foreach (var v in TemplateValues(child is JObject attach && key == "attach" ? new JArray(attach.Properties().Select(x => x.Value)) : child)) yield return v;
                    break;
                }
            }
        }

        /// <summary>Template refs in a unit's rules and attacks.</summary>
        public static IEnumerable<string> TemplateRefs(UnitDef u)
        {
            if (u == null || u.Rules == null && u.Attacks == null) return [];
            var o = new JObject();
            if (u.Rules != null) o["rules"] = JArray.FromObject(u.Rules, Serializer);
            if (u.Attacks != null) o["attacks"] = JArray.FromObject(u.Attacks, Serializer);
            return TemplateValues(o).Select(v => (string)v).ToList();
        }

        /// <summary>Template refs in a non-unit template's rules and attacks.</summary>
        public static IEnumerable<string> TemplateRefs(TemplateFields e)
        {
            if (e == null || e.Rules == null && e.Attacks == null) return [];
            var o = new JObject();
            if (e.Rules != null) o["rules"] = JArray.FromObject(e.Rules, Serializer);
            if (e.Attacks != null) o["attacks"] = JArray.FromObject(e.Attacks, Serializer);
            return TemplateValues(o).Select(v => (string)v).ToList();
        }

        /// <summary>Rewrites template refs in a rules list (map returns null to keep a value).</summary>
        public static List<RuleDef> Rewrite(List<RuleDef> rules, Func<string, string> map)
        {
            if (rules == null) return null;
            var a = JArray.FromObject(rules, Serializer);
            bool changed = false;
            foreach (var v in TemplateValues(a).ToList())
                if (map((string)v) is string to && to != (string)v) { v.Value = to; changed = true; }
            return changed ? a.ToObject<List<RuleDef>>(Serializer) : rules;
        }

        public static List<AttackDef> Rewrite(List<AttackDef> attacks, Func<string, string> map)
        {
            foreach (var at in attacks ?? [])
                if (at?.Launch?.Entity is string e && !e.StartsWith("=") && map(e) is string to) at.Launch.Entity = to;
            foreach (var at in attacks ?? []) if (at?.Do != null) at.Do = RewriteActions(at.Do, map);
            return attacks;
        }

        private static List<ActionDef> RewriteActions(List<ActionDef> actions, Func<string, string> map)
        {
            var a = JArray.FromObject(actions, Serializer);
            foreach (var v in TemplateValues(a).ToList())
                if (map((string)v) is string to) v.Value = to;
            return a.ToObject<List<ActionDef>>(Serializer);
        }

        /// <summary>Rewrites a unit's template refs in place.</summary>
        public static void Rewrite(UnitDef u, Func<string, string> map)
        {
            if (u == null) return;
            u.Rules = Rewrite(u.Rules, map);
            u.Attacks = Rewrite(u.Attacks, map);
        }

        public static void Rewrite(TemplateFields e, Func<string, string> map)
        {
            if (e == null) return;
            e.Rules = Rewrite(e.Rules, map);
            e.Attacks = Rewrite(e.Attacks, map);
        }
    }

    /// <summary>Whether a template's rules reach beyond the entity and what it fights: they change gold, waves,
    /// buildings, level variables, the HUD or the run's end, signal everyone, or listen to events across the map
    /// (`of` with no range). The Studio shows "this enemy affects the level" for such shared enemies.</summary>
    public static class LevelEffects
    {
        private static readonly HashSet<string> LevelActions = new(StringComparer.Ordinal) { "gold", "dropCoins", "building", "waves", "endLevel", "hud", "spawnGroup" };

        public static List<string> Of(IEnumerable<RuleDef> rules)
        {
            var found = new List<string>();
            var json = JArray.FromObject(rules ?? [], JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            foreach (var r in json.OfType<JObject>())
            {
                if (r["when"] is JObject w && w["of"] != null && w["range"] == null && (string)w["on"] != "near") found.Add($"hears {(string)w["on"]} of {(string)w["of"]} anywhere");
                foreach (var a in r.DescendantsAndSelf().OfType<JObject>().Where(o => o["do"]?.Type == JTokenType.String))
                {
                    string d = (string)a["do"];
                    if (LevelActions.Contains(d)) found.Add(d);
                    else if (d is "setVar" or "addVar" or "listAdd" or "listRemove" or "listClear" &&
                             ((string)a["target"] == "level" || ((string)a["name"])?.StartsWith("$") == true)) found.Add("level variable " + ((string)a["name"])?.TrimStart('$'));
                    else if (d == "signal" && (string)a["to"] == "all") found.Add("signal " + (string)a["name"] + " to all");
                }
            }
            return found.Distinct().ToList();
        }

        public static List<string> Of(UnitDef u) => Of(u?.Rules);
        public static List<string> Of(TemplateFields e) => Of(e?.Rules);
    }

    /// <summary>
    /// Rules the unit factory runs on the game's own machinery instead of the logic runtime, so packs migrated from
    /// version 2 play exactly as before. Today: the first rule of a unit that is exactly
    /// {"when": "death", "do": [{"do": "spawn", "entity": U, "at": "self", "elite": "inherit"}]} (what
    /// "behaviours: SpawnOnDeath U" became) sets Hp.enemyToSpawnOnDeath = U. The logic compiler skips that rule.
    /// </summary>
    public static class LogicLowering
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        /// <summary>The rule v2's SpawnOnDeath becomes.</summary>
        public static JObject SpawnOnDeathJson(string unit) => new()
        {
            ["when"] = "death",
            ["do"] = new JArray(new JObject { ["do"] = "spawn", ["entity"] = unit, ["at"] = "self", ["elite"] = "inherit" }),
        };

        public static RuleDef SpawnOnDeath(string unit) => SpawnOnDeathJson(unit).ToObject<RuleDef>(Serializer);

        /// <summary>The unit a rule spawns on death, if the rule is exactly the lowered shape (an optional id and note
        /// are allowed); otherwise null.</summary>
        public static string SpawnOnDeathUnit(JObject rule)
        {
            if (rule == null) return null;
            foreach (var p in rule.Properties())
            {
                switch (p.Name)
                {
                    case "when":
                        if (!(p.Value.Type == JTokenType.String && (string)p.Value == "death") &&
                            !(p.Value is JObject w && w.Count == 1 && (string)w["on"] == "death")) return null;
                        break;
                    case "do": case "id": case "note": case "_src": break;
                    case "enabled": if (p.Value.Type != JTokenType.Boolean || !(bool)p.Value) return null; break;
                    default: return null;
                }
            }
            if (rule["do"] is not JArray actions || actions.Count != 1 || actions[0] is not JObject a) return null;
            string entity = null;
            foreach (var p in a.Properties())
            {
                switch (p.Name)
                {
                    case "do": if ((string)p.Value != "spawn") return null; break;
                    case "entity": entity = p.Value.Type == JTokenType.String ? (string)p.Value : null; break;
                    case "at": if (p.Value.Type != JTokenType.String || (string)p.Value != "self") return null; break;
                    case "elite": if (p.Value.Type != JTokenType.String || (string)p.Value != "inherit") return null; break;
                    case "count": if (p.Value.Type != JTokenType.Integer || (int)p.Value != 1) return null; break;
                    default: return null;
                }
            }
            return entity == null || entity.StartsWith("=") ? null : entity;
        }

        public static string SpawnOnDeathUnit(RuleDef rule) => rule == null ? null : SpawnOnDeathUnit(JObject.FromObject(rule, Serializer));

        /// <summary>Index of the unit's lowered rule in its rules array, or -1. Only the first matching rule lowers
        /// (the game holds one unit to spawn on death), never one that spawns its own template, and (with isUnit) only
        /// one whose template is a unit (a vanilla unit or a pack unit, not a non-unit entity).</summary>
        public static int LoweredRuleIndex(JArray rules, string ownId, Func<string, bool> isUnit = null)
        {
            if (rules == null) return -1;
            for (int i = 0; i < rules.Count; i++)
            {
                string u = SpawnOnDeathUnit(rules[i] as JObject);
                if (u == null) continue;
                var r = ContentRef.Parse(u);
                if (r.Kind == RefKind.LocalUnit && r.Id == ownId) continue;
                if (r.Kind == RefKind.Invalid || isUnit != null && r.Kind != RefKind.Vanilla && !isUnit(u)) continue;
                return i;
            }
            return -1;
        }

        public static int LoweredRuleIndex(UnitDef u, Func<string, bool> isUnit = null) => u?.Rules == null ? -1 : LoweredRuleIndex(JArray.FromObject(u.Rules, Serializer), u.Id, isUnit);

        /// <summary>isUnit for a pack: a vanilla ref, or a ref that names one of its units and none of its entities.</summary>
        public static Func<string, bool> IsUnitIn(DesignPack pack) => r =>
        {
            var c = ContentRef.Parse(r);
            if (c.Kind == RefKind.Vanilla || c.Kind == RefKind.PackUnit) return true;
            return c.Kind == RefKind.LocalUnit && pack?.FindUnit(c.Id) != null;
        };

        /// <summary>Whether this rule (given as JSON, at index in its unit's rules) is lowered.</summary>
        public static bool IsLoweredSpawnOnDeath(JArray unitRules, int index, string unitId, Func<string, bool> isUnit = null) =>
            index >= 0 && LoweredRuleIndex(unitRules, unitId, isUnit) == index;

        /// <summary>The unit a unit spawns on death through its lowered rule, or null.</summary>
        public static string LoweredSpawnOnDeath(UnitDef u, Func<string, bool> isUnit = null)
        {
            int i = LoweredRuleIndex(u, isUnit);
            return i < 0 ? null : SpawnOnDeathUnit(u.Rules[i]);
        }

        /// <summary>Sets (unit non-null) or removes (null) the unit's simple spawn-on-death rule.</summary>
        public static void SetSpawnOnDeath(UnitDef u, string unit)
        {
            int i = LoweredRuleIndex(u);
            if (unit == null)
            {
                if (i >= 0) u.Rules.RemoveAt(i);
                if (u.Rules?.Count == 0) u.Rules = null;
                return;
            }
            u.Rules ??= [];
            if (i >= 0) u.Rules[i] = SpawnOnDeath(unit);
            else u.Rules.Insert(0, SpawnOnDeath(unit));
        }
    }
}
