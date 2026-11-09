using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;
using PalaceMod.Builder.Sharing;

namespace PalaceMod.Tests
{
    /// <summary>
    /// Opt-in: PALACE_MIGRATION_CORPUS=dir1:dir2 checks every pack.json, library item (*.json) and .tfpack under those
    /// folders: version 2 packs migrate to 3, every SpawnOnDeath behaviour becomes a rule the unit factory lowers onto
    /// the game's enemyToSpawnOnDeath with the same unit, and the migrated pack has no new validation errors.
    /// </summary>
    public static class MigrationCorpusTests
    {
        public static void CorpusMigratesWithoutChangingPlay()
        {
            string dirs = Environment.GetEnvironmentVariable("PALACE_MIGRATION_CORPUS");
            if (string.IsNullOrEmpty(dirs)) { Console.WriteLine("  (skipped: set PALACE_MIGRATION_CORPUS)"); return; }
            int packs = 0, spawns = 0;
            var failures = new List<string>();
            foreach (var dir in dirs.Split(':').Where(Directory.Exists))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var sources = new List<(string Name, string Json)>();
                    if (file.EndsWith(".tfpack", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = ZipFile.OpenRead(file);
                        foreach (var e in zip.Entries.Where(e => e.Name == "pack.json"))
                            using (var r = new StreamReader(e.Open())) sources.Add((file + "!" + e.FullName, r.ReadToEnd()));
                    }
                    else if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = File.ReadAllText(file);
                        if (text.Contains("\"palace-builder\"")) sources.Add((file, text));
                    }
                    foreach (var (name, json) in sources)
                    {
                        JObject root;
                        try { root = JObject.Parse(json); } catch (Newtonsoft.Json.JsonException) { continue; }
                        if ((string)root["format"] != DesignPack.FormatName) continue;
                        packs++;
                        // What version 2 said each unit spawns on death.
                        var before = new Dictionary<string, string>();
                        foreach (var u in (root["units"] as JArray ?? []).OfType<JObject>())
                            foreach (var b in (u["behaviours"] as JArray ?? []).OfType<JObject>())
                                if ((string)b["type"] == "SpawnOnDeath" && !before.ContainsKey((string)u["id"])) before[(string)u["id"]] = (string)b["unit"];
                        var issues = new List<ValidationIssue>();
                        var pack = PackSerializer.Read(json, issues);
                        if (pack == null) { failures.Add($"{name}: {string.Join("; ", issues)}"); continue; }
                        // A single library item holds one unit; the one it spawns lives in another file. In the composed
                        // library (what the unit factory sees) it's a unit unless this pack says it's an entity.
                        Func<string, bool> isUnit = r => pack.FindEntity(ContentRef.Parse(r).Id ?? r) == null;
                        foreach (var kv in before)
                        {
                            spawns++;
                            string now = LogicLowering.LoweredSpawnOnDeath(pack.FindUnit(kv.Key), isUnit);
                            if (now != kv.Value) failures.Add($"{name}: unit {kv.Key} spawned {kv.Value} on death, now lowers to {now ?? "nothing"}");
                        }
                        var errors = Validator.Validate(pack).Where(i => i.Severity == Severity.Error && (i.Path.Contains(".rules") || i.Path.Contains("logic")) &&
                            !(pack.Units.Count == 1 && pack.Levels.Count == 0 && i.Message.StartsWith("no template"))).ToList();
                        if (errors.Count > 0) failures.Add($"{name}: {string.Join("; ", errors)}");
                    }
                }
            }
            Console.WriteLine($"  migration corpus: {packs} packs, {spawns} spawn-on-death behaviours");
            Assert.True(failures.Count == 0, string.Join("\n    ", failures));
        }
    }
}
