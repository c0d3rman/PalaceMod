using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

/*
    Results of custom runs, kept in BepInEx/PalaceBuilder/scores.json and never in the game's save. One record per
    design (a pack's wave set or level), keyed "<packId>/<waveSetId>" or "<packId>/level:<levelId>": the best run, the
    last run, and counts. The file is rewritten whole on every result (it's small) through a temp file, so a crash
    mid-write keeps the old one.
*/
namespace PalaceMod.Builder
{
    public sealed class RunResult
    {
        [JsonProperty("score")] public int Score { get; set; }
        [JsonProperty("beaten")] public bool Beaten { get; set; }
        [JsonProperty("dateUtc")] public DateTime DateUtc { get; set; }
        /// <summary>Equipped weapon, perks and mutators (Equippable asset names).</summary>
        [JsonProperty("perks")] public List<string> Perks { get; set; } = [];
        /// <summary>Other gameplay-changing tools used during the run (GameplayImpact reasons besides the Builder's).</summary>
        [JsonProperty("assists", NullValueHandling = NullValueHandling.Ignore)] public List<string> Assists { get; set; }
        /// <summary>The night the run ended on (1-based; the last night for a win).</summary>
        [JsonProperty("nights")] public int Nights { get; set; }
    }

    public sealed class DesignRecord
    {
        [JsonProperty("pack")] public string Pack { get; set; }
        [JsonProperty("waveSet", NullValueHandling = NullValueHandling.Ignore)] public string WaveSet { get; set; }
        [JsonProperty("level", NullValueHandling = NullValueHandling.Ignore)] public string Level { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("plays")] public int Plays { get; set; }
        [JsonProperty("wins")] public int Wins { get; set; }
        [JsonProperty("best")] public RunResult Best { get; set; }
        [JsonProperty("last")] public RunResult Last { get; set; }
        [JsonIgnore] public bool EverBeaten => Wins > 0;
    }

    public static class ScoreStore
    {
        private const int Format = 1;

        private sealed class FileShape
        {
            [JsonProperty("format")] public int Format { get; set; } = ScoreStore.Format;
            [JsonProperty("designs")] public Dictionary<string, DesignRecord> Designs { get; set; } = [];
        }

        private static FileShape data;
        private static string loadedFrom;

        public static string Key(string packId, string waveSetId, string levelId) =>
            levelId != null ? $"{packId}/level:{levelId}" : $"{packId}/{waveSetId}";

        public static string Key(BuilderSessionInfo s) => Key(s.Pack.Id, s.Level == null ? s.WaveSet?.Id : null, s.Level?.Id);

        private static string Path => BuilderServices.Packs.Root is { } root ? System.IO.Path.Combine(root, "scores.json") : null;

        public static DesignRecord Get(string key)
        {
            Load();
            return data.Designs.TryGetValue(key, out var r) ? r : null;
        }

        public static IReadOnlyDictionary<string, DesignRecord> All
        {
            get { Load(); return data.Designs; }
        }

        /// <summary>Adds a finished run; returns the design's record (with Best updated if this run beat it).</summary>
        public static DesignRecord Record(BuilderSessionInfo s, RunResult run)
        {
            Load();
            string key = Key(s);
            if (!data.Designs.TryGetValue(key, out var r))
                data.Designs[key] = r = new DesignRecord { Pack = s.Pack.Id, WaveSet = s.Level == null ? s.WaveSet?.Id : null, Level = s.Level?.Id };
            r.Name = s.Level?.Name ?? s.WaveSet?.Name ?? s.Pack.Name;
            r.Host = s.HostScene;
            r.Plays++;
            if (run.Beaten) r.Wins++;
            r.Last = run;
            // Best = highest total score, as for vanilla highscores (a win's bonuses are already in it).
            if (r.Best == null || run.Score > r.Best.Score) r.Best = run;
            Save();
            return r;
        }

        private static void Load()
        {
            string path = Path;
            if (data != null && loadedFrom == path) return;
            loadedFrom = path;
            data = new FileShape();
            if (path == null || !File.Exists(path)) return;
            try
            {
                data = JsonConvert.DeserializeObject<FileShape>(File.ReadAllText(path)) ?? new FileShape();
                data.Designs ??= [];
            }
            catch (Exception e)
            {
                // Keep the unreadable file for the player and start a new one beside it.
                string aside = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Plugin.Logger.LogWarning($"Builder: couldn't read {path} ({e.Message}); moved it to {aside}");
                try { File.Move(path, aside); } catch { /* left in place; overwritten on next save */ }
                data = new FileShape();
            }
        }

        private static void Save()
        {
            string path = Path;
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Builder: couldn't write {path}: {e}");
            }
        }
    }
}
