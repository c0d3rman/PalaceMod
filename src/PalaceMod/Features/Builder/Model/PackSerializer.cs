using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PalaceMod.Builder.Model
{
    /// <summary>
    /// Pack JSON in and out. Reading runs Migrations first, so callers always get a CurrentVersion pack; writing
    /// leaves out nulls so packs stay short and diffable.
    /// </summary>
    public static class PackSerializer
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Formatting = Formatting.Indented,
            FloatParseHandling = FloatParseHandling.Double,
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        /// <summary>Parses, migrates and deserialises a pack. Returns null and adds an error issue on failure.</summary>
        public static DesignPack Read(string json, List<ValidationIssue> issues)
        {
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException e)
            {
                issues?.Add(new ValidationIssue(Severity.Error, "", "isn't valid JSON: " + e.Message));
                return null;
            }
            return FromJObject(root, issues);
        }

        public static DesignPack FromJObject(JObject root, List<ValidationIssue> issues)
        {
            try
            {
                if (!Migrations.Upgrade(root, out string error))
                {
                    issues?.Add(new ValidationIssue(Severity.Error, "version", error));
                    return null;
                }
                return root.ToObject<DesignPack>(Serializer);
            }
            catch (Exception e) when (e is JsonException || e is ArgumentException || e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                issues?.Add(new ValidationIssue(Severity.Error, "", "doesn't match the pack format: " + e.Message));
                return null;
            }
        }

        /// <summary>Indented JSON for pack.json files.</summary>
        public static string Write(DesignPack pack) => JsonConvert.SerializeObject(pack, Settings);

        /// <summary>Single-line JSON (for share codes and hashing).</summary>
        public static string WriteCompact(DesignPack pack)
        {
            var s = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, Formatting = Formatting.None };
            return JsonConvert.SerializeObject(pack, s);
        }

        /// <summary>Deep copy through JSON (unknown fields included).</summary>
        public static DesignPack Clone(DesignPack pack) => Read(WriteCompact(pack), null);
    }

    /// <summary>
    /// Upgrades older pack JSON in place, one version at a time. To change the schema: bump DesignPack.CurrentVersion
    /// to N and add Steps[N - 1] = root =&gt; { ...edit the JObject from version N-1 to N... }.
    /// </summary>
    public static class Migrations
    {
        private static readonly Dictionary<int, Action<JObject>> Steps = new()
        {
            [1] = MoveActivatorsToRequires,
            [2] = BehavioursToRules,
        };

        /// <summary>2 -> 3: each unit's behaviours [{"type": "SpawnOnDeath", "unit": U}] become a rule at the front of
        /// its rules, {"when": "death", "do": [{"do": "spawn", "entity": U, "at": "self", "elite": "inherit"}]}, which
        /// the unit factory still runs on the game's own Hp.enemyToSpawnOnDeath (Logic/LogicLowering), so it plays
        /// the same. Behaviours of other types (none were ever valid) are kept under "behaviours" for the validator.</summary>
        internal static void BehavioursToRules(JObject root)
        {
            if (root["units"] is not JArray units) return;
            foreach (var unit in units.OfType<JObject>())
            {
                if (unit["behaviours"] is not JArray behaviours) { unit.Remove("behaviours"); continue; }
                var rules = new List<JToken>();
                var unknown = new JArray();
                foreach (var b in behaviours)
                {
                    if (b is JObject o && (string)o["type"] == "SpawnOnDeath" && o["unit"]?.Type == JTokenType.String)
                        rules.Add(Logic.LogicLowering.SpawnOnDeathJson((string)o["unit"]));
                    else unknown.Add(b);
                }
                unit.Remove("behaviours");
                if (unknown.Count > 0) unit["behaviours"] = unknown;
                if (rules.Count == 0) continue;
                var existing = unit["rules"] as JArray ?? new JArray();
                for (int i = rules.Count - 1; i >= 0; i--) existing.Insert(0, rules[i]);
                unit["rules"] = existing;
            }
        }

        /// <summary>1 -> 2: an added slot's "activator" (a host slot name or another added slot's id) and
        /// "activatorLevel" become "requires": { "slot": name | "add:&lt;id&gt;", "level" }.</summary>
        internal static void MoveActivatorsToRequires(JObject root)
        {
            if (root["levels"] is not JArray levels) return;
            foreach (var level in levels.OfType<JObject>())
            {
                if (level["buildSlots"]?["add"] is not JArray adds) continue;
                var ids = new HashSet<string>(adds.OfType<JObject>().Select(a => (string)a["id"]).Where(id => id != null), StringComparer.Ordinal);
                foreach (var add in adds.OfType<JObject>())
                {
                    var activator = add["activator"];
                    var activatorLevel = add["activatorLevel"];
                    add.Remove("activator");
                    add.Remove("activatorLevel");
                    if (activator?.Type != JTokenType.String || add["requires"] != null) continue;
                    string name = (string)activator;
                    int lv = activatorLevel?.Type == JTokenType.Integer ? (int)activatorLevel : 0;
                    add["requires"] = new JObject { ["slot"] = ids.Contains(name) ? SlotRefs.Added(name) : name, ["level"] = lv };
                }
            }
        }

        public static bool Upgrade(JObject root, out string error)
        {
            error = null;
            if ((string)root["format"] != DesignPack.FormatName)
            {
                error = $"not a Builder pack (\"format\" must be \"{DesignPack.FormatName}\")";
                return false;
            }
            int version = root["version"]?.Type == JTokenType.Integer ? (int)root["version"] : 0;
            if (version < 1)
            {
                error = "missing \"version\"";
                return false;
            }
            if (version > DesignPack.CurrentVersion)
            {
                error = $"made with a newer PalaceMod (pack version {version}, this one reads up to {DesignPack.CurrentVersion}); update PalaceMod";
                return false;
            }
            while (version < DesignPack.CurrentVersion)
            {
                if (!Steps.TryGetValue(version, out var step))
                {
                    error = $"no migration from version {version}";
                    return false;
                }
                step(root);
                version++;
                root["version"] = version;
            }
            return true;
        }
    }
}
