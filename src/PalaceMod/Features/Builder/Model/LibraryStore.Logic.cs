using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model.Logic;

/*
    The behaviour engine's shared definitions in the library (docs/builder-engine.md, "Presets"): non-unit templates,
    statuses and presets are library items like enemies, one pack-format file each:

        library/entities/<id>.json   { ..., "entities": [ <EntityDef> ], "library": {kind, created, origin} }
        library/statuses/<id>.json   { ..., "statuses": [ <StatusDef> ] }
        library/presets/<id>.json    { ..., "presets":  [ <PresetDef> ] }

    Workspaces and the composed pack hold all of them (as they hold every unit); SaveWorkspace writes the changed ones;
    Export copies the ones the exported levels and enemies use (transitively); Import reuses identical ones, replaces
    ones from the same origin, and renames clashes (rewriting references).
*/
namespace PalaceMod.Builder.Model
{
    public sealed partial class LibraryStore
    {
        /// <summary>A kind of shared logic definition kept in the library.</summary>
        public enum LogicKind { Entity, Status, Preset }

        private static readonly JsonSerializer LogicSerializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private sealed class LogicEntry
        {
            public string Id, File, Json;
            public JObject Def;
            public JObject Meta;
        }

        private readonly Dictionary<LogicKind, Dictionary<string, LogicEntry>> logicItems = new()
        {
            [LogicKind.Entity] = new(StringComparer.Ordinal),
            [LogicKind.Status] = new(StringComparer.Ordinal),
            [LogicKind.Preset] = new(StringComparer.Ordinal),
        };

        private static string Field(LogicKind k) => k switch { LogicKind.Entity => "entities", LogicKind.Status => "statuses", _ => "presets" };
        public string LogicDir(LogicKind k) => Path.Combine(Root, Field(k));
        public string LogicFile(LogicKind k, string id) => Path.Combine(LogicDir(k), id + ".json");

        public IEnumerable<string> LogicIds(LogicKind k) => logicItems[k].Keys;
        public EntityDef Entity(string id) => Get<EntityDef>(LogicKind.Entity, id);
        public StatusDef Status(string id) => Get<StatusDef>(LogicKind.Status, id);
        /// <summary>A library preset (what Presets.Library looks up).</summary>
        public PresetDef Preset(string id) => Get<PresetDef>(LogicKind.Preset, id);
        public IReadOnlyList<PresetDef> AllPresets() => logicItems[LogicKind.Preset].Values.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => e.Def.ToObject<PresetDef>(LogicSerializer)).ToList();

        private T Get<T>(LogicKind k, string id) where T : class =>
            id != null && logicItems[k].TryGetValue(id, out var e) ? e.Def.ToObject<T>(LogicSerializer) : null;

        private void LoadLogic()
        {
            foreach (var k in logicItems.Keys.ToList())
            {
                var map = logicItems[k];
                map.Clear();
                string dir = LogicDir(k);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    string where = $"{Field(k)}/{Path.GetFileName(file)}";
                    try
                    {
                        var root = JObject.Parse(File.ReadAllText(file));
                        if (!Migrations.Upgrade(root, out string err)) { Issues.Add(new ValidationIssue(Severity.Error, where, err)); continue; }
                        if (root[Field(k)] is not JArray list || list.Count != 1 || list[0] is not JObject def)
                        {
                            Issues.Add(new ValidationIssue(Severity.Error, where, $"must hold exactly one item in \"{Field(k)}\""));
                            continue;
                        }
                        def["id"] = id;
                        map[id] = new LogicEntry { Id = id, File = file, Def = def, Json = Compact(def), Meta = root[MetaField] as JObject };
                    }
                    catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
                    {
                        Issues.Add(new ValidationIssue(Severity.Error, where, "can't be read: " + e.Message));
                    }
                }
            }
        }

        /// <summary>Adds every library entity, status and preset to a composed pack or workspace.</summary>
        private void AddLogicTo(DesignPack pack)
        {
            var e = logicItems[LogicKind.Entity].Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Def.ToObject<EntityDef>(LogicSerializer)).ToList();
            var s = logicItems[LogicKind.Status].Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Def.ToObject<StatusDef>(LogicSerializer)).ToList();
            var p = logicItems[LogicKind.Preset].Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Def.ToObject<PresetDef>(LogicSerializer)).ToList();
            if (e.Count > 0) (pack.Entities ??= []).AddRange(e);
            if (s.Count > 0) (pack.Statuses ??= []).AddRange(s);
            if (p.Count > 0) (pack.Presets ??= []).AddRange(p);
        }

        private static IEnumerable<JObject> LogicList(DesignPack pack, LogicKind k)
        {
            IEnumerable<object> items = k switch { LogicKind.Entity => pack?.Entities, LogicKind.Status => pack?.Statuses, _ => pack?.Presets };
            return (items ?? []).Where(x => x != null).Select(x => JObject.FromObject(x, LogicSerializer));
        }

        /// <summary>Writes the entities, statuses and presets that changed since baseline; ones the baseline had and
        /// now doesn't go to trash. Returns the number of files written.</summary>
        private int SaveLogic(DesignPack now, DesignPack baseline)
        {
            int written = 0;
            foreach (var k in logicItems.Keys.ToList())
            {
                var before = LogicList(baseline, k).Where(o => (string)o["id"] != null).GroupBy(o => (string)o["id"]).ToDictionary(g => g.Key, g => Compact(g.First()));
                var after = LogicList(now, k).Where(o => (string)o["id"] != null).GroupBy(o => (string)o["id"]).ToDictionary(g => g.Key, g => g.First());
                foreach (var kv in after)
                {
                    if (!Ids.IsValid(kv.Key)) throw new ArgumentException($"invalid {Field(k)} id \"{kv.Key}\"");
                    string json = Compact(kv.Value);
                    bool changed = !before.TryGetValue(kv.Key, out var b) || b != json;
                    if (!changed && logicItems[k].ContainsKey(kv.Key)) continue;
                    if (logicItems[k].TryGetValue(kv.Key, out var old) && old.Json == json) continue;
                    WriteLogic(k, kv.Value, old?.Meta);
                    written++;
                }
                foreach (var id in before.Keys.Where(id => !after.ContainsKey(id)).ToList())
                    if (TrashLogic(k, id)) written++;
            }
            return written;
        }

        private void WriteLogic(LogicKind k, JObject def, JObject meta)
        {
            string id = (string)def["id"];
            var file = new JObject
            {
                ["format"] = DesignPack.FormatName,
                ["version"] = DesignPack.CurrentVersion,
                ["id"] = id,
                ["name"] = (string)def["name"] ?? id,
                [Field(k)] = new JArray(def.DeepClone()),
            };
            meta = meta == null ? new JObject() : (JObject)meta.DeepClone();
            meta["kind"] = k switch { LogicKind.Entity => "entity", LogicKind.Status => "status", _ => "preset" };
            meta["created"] ??= DateTime.UtcNow.ToString("o");
            file[MetaField] = meta;
            string path = LogicFile(k, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, file.ToString(Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            var copy = (JObject)def.DeepClone();
            logicItems[k][id] = new LogicEntry { Id = id, File = path, Def = copy, Json = Compact(copy), Meta = meta };
        }

        public bool TrashLogic(LogicKind k, string id)
        {
            string file = LogicFile(k, id);
            logicItems[k].Remove(id);
            if (!File.Exists(file)) return false;
            Directory.CreateDirectory(TrashDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.Move(file, Path.Combine(TrashDir, $"{Field(k)}-{id}-{stamp}.json"));
            return true;
        }

        /// <summary>Saves a preset to the library ("Save as preset"): under its id, or a free one if that's taken by
        /// a different preset and replace is false. Returns the id.</summary>
        public string SavePreset(PresetDef preset, bool replace = false)
        {
            var def = JObject.FromObject(preset, LogicSerializer);
            string id = Ids.IsValid(preset.Id) ? preset.Id : Ids.FromName(preset.Name ?? "preset");
            if (!replace) id = Unique(id, x => logicItems[LogicKind.Preset].ContainsKey(x) && logicItems[LogicKind.Preset][x].Json != Compact(WithId(def, x)));
            def["id"] = id;
            WriteLogic(LogicKind.Preset, def, logicItems[LogicKind.Preset].TryGetValue(id, out var old) ? old.Meta : null);
            return id;
        }

        private static JObject WithId(JObject def, string id)
        {
            var c = (JObject)def.DeepClone();
            c["id"] = id;
            return c;
        }

        // ---------------------------------------------------------------- references

        private static readonly Regex TemplateInText = new(@"(?:\bis:|@@)([a-z0-9._-]+)", RegexOptions.Compiled);
        private static readonly Regex StatusInText = new(@"(?:\bhas:|stacks\(\s*['""])([a-z0-9._-]+)", RegexOptions.Compiled);

        /// <summary>Ids of each logic kind a JSON tree refers to: template params, places' entity, launch.entity,
        /// is:/@@ in expressions (entities); status params and lists, has:/stacks() (statuses); "preset" (presets).</summary>
        internal static Dictionary<LogicKind, HashSet<string>> LogicRefsIn(JToken root)
        {
            var r = new Dictionary<LogicKind, HashSet<string>>
            {
                [LogicKind.Entity] = new(StringComparer.Ordinal), [LogicKind.Status] = new(StringComparer.Ordinal), [LogicKind.Preset] = new(StringComparer.Ordinal),
            };
            foreach (var v in LogicRefs.TemplateValues(root)) r[LogicKind.Entity].Add(ContentRef.Parse((string)v).Id ?? (string)v);
            void Walk(JToken t, string prop, string parentProp)
            {
                switch (t)
                {
                    case JObject o:
                        foreach (var p in o.Properties()) Walk(p.Value, p.Name, prop);
                        break;
                    case JArray a:
                        foreach (var x in a) Walk(x, prop, parentProp);
                        break;
                    case JValue v when v.Type == JTokenType.String:
                    {
                        string s = (string)v;
                        if (prop == "entity") r[LogicKind.Entity].Add(s);
                        if (prop == "status" || prop == "statuses") r[LogicKind.Status].Add(s);
                        if (prop == "preset") r[LogicKind.Preset].Add(s);
                        foreach (Match m in TemplateInText.Matches(s)) r[LogicKind.Entity].Add(m.Groups[1].Value);
                        foreach (Match m in StatusInText.Matches(s)) r[LogicKind.Status].Add(m.Groups[1].Value);
                        break;
                    }
                }
            }
            Walk(root, null, null);
            return r;
        }

        /// <summary>Adds to an export the library entities, statuses and presets it uses, and the units those use, until
        /// nothing new is found.</summary>
        private void ExportLogic(DesignPack pack)
        {
            for (int round = 0; round < 32; round++)
            {
                var refs = LogicRefsIn(JObject.FromObject(pack, LogicSerializer));
                bool added = false;
                foreach (var k in logicItems.Keys)
                {
                    IEnumerable<object> have = k switch { LogicKind.Entity => pack.Entities, LogicKind.Status => pack.Statuses, _ => pack.Presets };
                    var ids = new HashSet<string>((have ?? []).Select(x => (string)JObject.FromObject(x, LogicSerializer)["id"]));
                    foreach (var id in refs[k].Where(id => !ids.Contains(id) && logicItems[k].ContainsKey(id)).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var def = logicItems[k][id].Def.DeepClone();
                        switch (k)
                        {
                            case LogicKind.Entity: (pack.Entities ??= []).Add(def.ToObject<EntityDef>(LogicSerializer)); break;
                            case LogicKind.Status: (pack.Statuses ??= []).Add(def.ToObject<StatusDef>(LogicSerializer)); break;
                            default: (pack.Presets ??= []).Add(def.ToObject<PresetDef>(LogicSerializer)); break;
                        }
                        added = true;
                    }
                }
                // Units the logic items (and the levels' logic) spawn.
                var unitRefs = refs[LogicKind.Entity].Where(id => units.ContainsKey(id) && pack.FindUnit(id) == null).ToList();
                foreach (var id in UnitClosure(unitRefs, Unit).Where(id => pack.FindUnit(id) == null).OrderBy(x => x, StringComparer.Ordinal))
                {
                    pack.Units.Add(PackSerializer.Clone(units[id].Pack).Units[0]);
                    added = true;
                }
                if (!added) return;
            }
        }

        /// <summary>Imports a pack's entities, statuses and presets: identical ones are reused, ones from the same
        /// origin replaced, clashes renamed (and the pack's references to them rewritten). Runs before units and
        /// levels are written, so they carry the new ids.</summary>
        private void ImportLogic(DesignPack pack, string origin, LibraryImportResult result)
        {
            foreach (var k in logicItems.Keys.ToList())
            {
                var renames = new Dictionary<string, string>(StringComparer.Ordinal);
                var reuse = new HashSet<string>(StringComparer.Ordinal);
                var defs = LogicList(pack, k).ToList();
                var taken = new HashSet<string>(logicItems[k].Keys, StringComparer.Ordinal);
                foreach (var def in defs)
                {
                    string id = (string)def["id"];
                    if (id == null) continue;
                    if (!logicItems[k].TryGetValue(id, out var existing)) { taken.Add(id); continue; }
                    if (existing.Json == Compact(def)) { reuse.Add(id); result.Reused.Add($"{Field(k)} {id}"); continue; }
                    if ((string)existing.Meta?["origin"] == origin) { result.Replaced.Add($"{Field(k)} {id}"); continue; }
                    string fresh = Unique(id, x => taken.Contains(x) || logicItems[k].ContainsKey(x));
                    taken.Add(fresh);
                    renames[id] = fresh;
                }
                if (renames.Count > 0) RenameLogic(pack, k, renames);
                foreach (var def in LogicList(pack, k))
                {
                    string id = (string)def["id"];
                    if (id == null || reuse.Contains(id)) continue;
                    WriteLogic(k, def, new JObject { ["origin"] = origin });
                }
                foreach (var kv in renames) result.Issues.Add(new ValidationIssue(Severity.Warning, Field(k), $"\"{kv.Key}\" was already in the library, so it was imported as \"{kv.Value}\""));
            }
        }

        /// <summary>Renames entities, statuses or presets in a pack, rewriting every reference.</summary>
        internal static void RenameLogic(DesignPack pack, LogicKind k, IReadOnlyDictionary<string, string> map)
        {
            var root = JObject.FromObject(pack, LogicSerializer);
            if (k == LogicKind.Entity)
                foreach (var v in LogicRefs.TemplateValues(root).ToList())
                    if (map.TryGetValue((string)v, out var to)) v.Value = to;
            void Walk(JToken t, string prop)
            {
                switch (t)
                {
                    case JObject o: foreach (var p in o.Properties().ToList()) Walk(p.Value, p.Name); break;
                    case JArray a: foreach (var x in a) Walk(x, prop); break;
                    case JValue v when v.Type == JTokenType.String:
                    {
                        string s = (string)v;
                        bool idField = prop == "id";
                        if (map.TryGetValue(s, out var to) && (k == LogicKind.Entity && prop == "entity" || k == LogicKind.Status && prop is "status" or "statuses" || k == LogicKind.Preset && prop == "preset"))
                        { v.Value = to; break; }
                        var rx = k == LogicKind.Entity ? TemplateInText : k == LogicKind.Status ? StatusInText : null;
                        if (rx != null && !idField)
                            v.Value = rx.Replace(s, m => map.TryGetValue(m.Groups[1].Value, out var t2) ? m.Value.Substring(0, m.Groups[1].Index - m.Index) + t2 : m.Value);
                        break;
                    }
                }
            }
            Walk(root, null);
            var list = root[Field(k)] as JArray;
            foreach (var item in list?.OfType<JObject>() ?? [])
                if ((string)item["id"] is string id && map.TryGetValue(id, out var to2)) item["id"] = to2;
            var back = root.ToObject<DesignPack>(LogicSerializer);
            pack.Units = back.Units;
            pack.Levels = back.Levels;
            pack.WaveSets = back.WaveSets;
            pack.Entities = back.Entities;
            pack.Statuses = back.Statuses;
            pack.Presets = back.Presets;
        }
    }
}
