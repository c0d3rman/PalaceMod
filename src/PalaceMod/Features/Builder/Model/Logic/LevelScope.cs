using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
    Shared templates checked where they're used (docs/builder-engine.md, "Validation and errors"). Units, entities,
    statuses and presets in the library are shared by every level, and a template may refer to the level it runs in
    ($vars, @names). So a template is validated in the context of each level that uses it (a level uses an item when its
    logic, nights, slots or any item it uses names it, transitively), and an item no level uses is validated alone, with
    its level references unchecked. One level's template never blocks another level.

    Pure C# (no Unity): tests/PalaceMod.Tests/LevelScopeTests.cs. palace.py's `level_scope` is the same closure.
*/
namespace PalaceMod.Builder.Model.Logic
{
    public static class LevelScope
    {
        /// <summary>The kinds of shared item, by the pack field that holds them.</summary>
        public static readonly string[] Fields = ["units", "entities", "statuses", "presets"];

        /// <summary>Where each kind's items live in the library folder.</summary>
        public static string Folder(string field) => field == "units" ? "enemies" : field;

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        // Ids written inside text: is:<id>, @@<id>, has:<id>, stacks('<id>'), and any quoted literal ('id' or "id") in
        // an expression (spawn(pick(['a', 'b'])), a template var's value).
        private static readonly Regex InText = new(@"(?:\bis:|@@|\bhas:)([a-z0-9._-]+)|['""]([a-z0-9._-]+)['""]", RegexOptions.Compiled);

        /// <summary>The ids (field -> ids) of the shared items a level uses: named anywhere in the level or its nights,
        /// or in an item it uses, transitively.</summary>
        public static Dictionary<string, HashSet<string>> Uses(DesignPack pack, LevelDef level)
        {
            var index = Index(pack);
            var used = Fields.ToDictionary(f => f, _ => new HashSet<string>(StringComparer.Ordinal));
            var queue = new Queue<JToken>();
            queue.Enqueue(JObject.FromObject(level, Serializer));
            if (level.WaveSet != null && pack.FindWaveSet(level.WaveSet) is { } ws) queue.Enqueue(JObject.FromObject(ws, Serializer));
            while (queue.Count > 0)
                foreach (string id in Mentions(queue.Dequeue()))
                    foreach (var f in Fields)
                        if (index[f].TryGetValue(id, out var item) && used[f].Add(id)) queue.Enqueue(item);
            return used;
        }

        /// <summary>Every id a JSON tree names: whole strings (a leading '=' or "unit:" dropped) and ids written in text.</summary>
        public static IEnumerable<string> Mentions(JToken root)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in (root is JContainer c ? c.DescendantsAndSelf() : [root]).OfType<JValue>())
            {
                if (v.Type != JTokenType.String) continue;
                string s = ((string)v).Trim();
                found.Add(s.StartsWith("=") ? s.Substring(1).Trim() : s);
                if (s.StartsWith("unit:")) found.Add(s.Substring(5));
                foreach (Match m in InText.Matches(s)) found.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }
            return found;
        }

        private static Dictionary<string, Dictionary<string, JObject>> Index(DesignPack pack)
        {
            var index = Fields.ToDictionary(f => f, _ => new Dictionary<string, JObject>(StringComparer.Ordinal));
            var root = JObject.FromObject(pack, Serializer);
            foreach (var f in Fields)
                foreach (var o in (root[f] as JArray ?? []).OfType<JObject>())
                    if ((string)o["id"] is { } id && !index[f].ContainsKey(id)) index[f][id] = o;
            return index;
        }

        /// <summary>A copy of the pack holding one level, its nights and the shared items it uses; null if there's no
        /// such level. What a library level is validated as.</summary>
        public static DesignPack For(DesignPack pack, string levelId)
        {
            var copy = PackSerializer.Clone(pack);
            var level = copy?.FindLevel(levelId);
            if (level == null) return null;
            var used = Uses(copy, level);
            copy.Levels = [level];
            copy.WaveSets = (copy.WaveSets ?? []).Where(w => w != null && w.Id == level.WaveSet).ToList();
            Keep(copy, used);
            return copy;
        }

        /// <summary>A copy of the pack with no levels and only the shared items no level uses.</summary>
        public static DesignPack Unused(DesignPack pack)
        {
            var copy = PackSerializer.Clone(pack);
            var used = Fields.ToDictionary(f => f, _ => new HashSet<string>(StringComparer.Ordinal));
            foreach (var lv in copy.Levels ?? [])
                if (lv != null)
                    foreach (var kv in Uses(copy, lv)) used[kv.Key].UnionWith(kv.Value);
            var unused = Fields.ToDictionary(f => f, f => new HashSet<string>(IdsOf(copy, f).Where(id => !used[f].Contains(id)), StringComparer.Ordinal));
            copy.Levels = [];
            copy.WaveSets = [];
            Keep(copy, unused);
            return copy;
        }

        private static IEnumerable<string> IdsOf(DesignPack p, string field) => field switch
        {
            "units" => (p.Units ?? []).Where(x => x?.Id != null).Select(x => x.Id),
            "entities" => (p.Entities ?? []).Where(x => x?.Id != null).Select(x => x.Id),
            "statuses" => (p.Statuses ?? []).Where(x => x?.Id != null).Select(x => x.Id),
            _ => (p.Presets ?? []).Where(x => x?.Id != null).Select(x => x.Id),
        };

        private static void Keep(DesignPack p, Dictionary<string, HashSet<string>> ids)
        {
            p.Units = (p.Units ?? []).Where(x => x?.Id != null && ids["units"].Contains(x.Id)).ToList();
            p.Entities = (p.Entities ?? []).Where(x => x?.Id != null && ids["entities"].Contains(x.Id)).ToList();
            p.Statuses = (p.Statuses ?? []).Where(x => x?.Id != null && ids["statuses"].Contains(x.Id)).ToList();
            p.Presets = (p.Presets ?? []).Where(x => x?.Id != null && ids["presets"].Contains(x.Id)).ToList();
        }

        private static readonly Regex ItemPath = new(@"^(units|entities|statuses|presets|levels|waveSets)\[(\d+)\]", RegexOptions.Compiled);

        /// <summary>
        /// An issue of a scoped pack (For/Unused) as the library reports it: the path names the item by id instead of
        /// an index into the copy (units[id=snail].movement.path; a level's own paths start with "level", its nights'
        /// with "nights"), and File names the library file it comes from (enemies/snail.json).
        /// </summary>
        public static ValidationIssue Attribute(DesignPack scoped, ValidationIssue issue)
        {
            var m = ItemPath.Match(issue.Path ?? "");
            if (!m.Success) return issue;
            string field = m.Groups[1].Value, rest = issue.Path.Substring(m.Length);
            int i = int.Parse(m.Groups[2].Value);
            string id = field switch
            {
                "units" => scoped.Units?.ElementAtOrDefault(i)?.Id,
                "entities" => scoped.Entities?.ElementAtOrDefault(i)?.Id,
                "statuses" => scoped.Statuses?.ElementAtOrDefault(i)?.Id,
                "presets" => scoped.Presets?.ElementAtOrDefault(i)?.Id,
                "levels" => scoped.Levels?.ElementAtOrDefault(i)?.Id,
                _ => scoped.WaveSets?.ElementAtOrDefault(i)?.Id,
            };
            if (id == null) return issue;
            if (field == "levels") return new ValidationIssue(issue.Severity, "level" + rest, issue.Message) { File = $"levels/{id}.json" };
            if (field == "waveSets")
            {
                string owner = scoped.Levels?.FirstOrDefault(l => l?.WaveSet == id)?.Id;
                return new ValidationIssue(issue.Severity, (owner != null ? "nights" : $"waveSets[id={id}]") + rest, issue.Message) { File = owner != null ? $"levels/{owner}.json" : null };
            }
            return new ValidationIssue(issue.Severity, $"{field}[id={id}]{rest}", issue.Message) { File = $"{Folder(field)}/{id}.json" };
        }

        /// <summary>
        /// The library's issues, each about one level or one shared item: every level validated with the items it uses
        /// (a template's problem in that level's context names the level), then the items no level uses, alone.
        /// Duplicates (an item used by several levels) are reported once. Attributed (see Attribute).
        /// </summary>
        public static List<ValidationIssue> LibraryIssues(DesignPack library, Func<DesignPack, List<ValidationIssue>> validate)
        {
            var list = new List<ValidationIssue>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(ValidationIssue i)
            {
                if (seen.Add($"{i.Severity}|{i.File}|{i.Path}|{i.Message}")) list.Add(i);
            }
            foreach (var lv in library.Levels ?? [])
            {
                if (lv?.Id == null) continue;
                var scoped = For(library, lv.Id);
                foreach (var i in validate(scoped).Select(x => Attribute(scoped, x)))
                    Add(i.File == null || i.File.StartsWith("levels/") ? i : new ValidationIssue(i.Severity, i.Path, i.Message) { File = i.File, Level = lv.Id });
            }
            var rest = Unused(library);
            foreach (var i in validate(rest)) Add(Attribute(rest, i));
            return list;
        }
    }
}
