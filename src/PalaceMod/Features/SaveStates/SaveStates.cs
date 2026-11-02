using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Public savestate API. Other features (e.g. "retry the last night") build on this; see CLAUDE.md.

        var dusk = SaveStates.Latest(SaveStates.CurrentLevel, SaveStateKind.AutoDusk);   // right before the last night
        if (dusk != null && SaveStates.CanRestore(dusk, out _)) SaveStates.Restore(dusk);
        SaveStates.AutoCaptured += state => ...;

    States live on disk under BepInEx/PalaceMod/SaveStates/<level>/<id>.json (+ .png thumbnail), never in the game's
    own save folder. Restoring reloads the level from the captured data and then puts back everything the game's save
    format misses; it takes about a second, and Restored fires when it's done.
*/
namespace PalaceMod
{
    /// <summary>A savestate on disk: metadata plus its data, loaded on first use.</summary>
    public sealed class SaveStateInfo
    {
        internal SaveStateData Data { get; private set; }

        public string Id { get; }
        public string Level => Data.level;
        public string Name => Data.name;
        public SaveStateKind Kind { get; }
        /// <summary>EnemySpawner.Wavenumber when captured: -1 on the first day; n during night n+1 and the day after it.</summary>
        public int Wave => Data.wave;
        public int WaveCount => Data.waveCount;
        /// <summary>The night number a player would call it (1-based): the night being fought, or the next one if day.</summary>
        public int NightNumber => Data.night ? Data.wave + 1 : Data.wave + 2;
        public bool IsNight => Data.night;
        public int Gold => Data.gold;
        public int Score => Data.score;
        public int UnitsAlive => Data.unitsAlive;
        public int EnemiesAlive => Data.enemiesAlive;
        public DateTime CreatedUtc { get; }
        public string JsonPath { get; }
        public string ThumbnailPath => Path.ChangeExtension(JsonPath, ".png");

        /// <summary>"Night 3" during a night, "Day 3" during the day before night 3.</summary>
        public string Moment => IsNight ? $"Night {NightNumber}" : $"Day {NightNumber}";

        internal SaveStateInfo(string jsonPath, SaveStateData data)
        {
            JsonPath = jsonPath;
            Id = Path.GetFileNameWithoutExtension(jsonPath);
            Data = data;
            Kind = Enum.TryParse(data.kind, out SaveStateKind kind) ? kind : SaveStateKind.Manual;
            CreatedUtc = DateTime.TryParse(data.createdUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : File.GetLastWriteTimeUtc(jsonPath);
        }

        public override string ToString() => $"{Name} ({Kind}, {Level}, {Moment}, {Gold}g)";
    }

    public static class SaveStates
    {
        /// <summary>Folder holding all savestates (one subfolder per level).</summary>
        public static string Root => Path.Combine(Paths.BepInExRootPath, "PalaceMod", "SaveStates");

        /// <summary>The active level's scene name, or null outside a level.</summary>
        public static string CurrentLevel => DayNightCycle.Instance != null && EnemySpawner.instance != null ? SceneManager.GetActiveScene().name : null;

        /// <summary>Raised after any state is captured and written (all kinds).</summary>
        public static event Action<SaveStateInfo> Captured;
        /// <summary>Raised after an automatic dawn or dusk state is captured.</summary>
        public static event Action<SaveStateInfo> AutoCaptured;
        /// <summary>Raised when a restore starts (before the level reloads).</summary>
        public static event Action<SaveStateInfo> Restoring;
        /// <summary>Raised when a restore has finished and the player is back in control.</summary>
        public static event Action<SaveStateInfo> Restored;
        /// <summary>Raised when states are added, removed or renamed (for UIs).</summary>
        public static event Action Changed;

        /// <summary>True from Restore until the restored level is fully set up.</summary>
        public static bool IsRestoring => StateRestorer.Busy;

        /// <summary>The state most recently captured or loaded in this level this session (for "load last state").</summary>
        public static SaveStateInfo Last { get; internal set; }

        // ---- Queries ------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, (DateTime Stamp, SaveStateInfo Info)> cache = [];

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Formatting = Formatting.None,
        };

        /// <summary>All states for a level (default: the current one), newest first. Unreadable files are skipped.</summary>
        public static IReadOnlyList<SaveStateInfo> List(string level = null)
        {
            level ??= CurrentLevel;
            if (string.IsNullOrEmpty(level)) return [];
            string dir = LevelFolder(level);
            if (!Directory.Exists(dir)) return [];
            var result = new List<SaveStateInfo>();
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                var info = Read(file);
                if (info != null) result.Add(info);
            }
            return result.OrderByDescending(s => s.CreatedUtc).ToList();
        }

        /// <summary>Newest state of a kind for a level (default: the current one), or null.</summary>
        public static SaveStateInfo Latest(string level = null, SaveStateKind? kind = null) =>
            List(level).FirstOrDefault(s => kind == null || s.Kind == kind);

        /// <summary>Finds a state of the current level by id or (case-insensitive) name.</summary>
        public static SaveStateInfo Find(string idOrName, string level = null) =>
            List(level).FirstOrDefault(s => s.Id == idOrName) ??
            List(level).FirstOrDefault(s => string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase));

        // ---- Capture / restore --------------------------------------------------------------------------------------

        /// <summary>Whether the current moment can be captured; reason says why not.</summary>
        public static bool CanCapture(out string reason) => StateCapture.CanCapture(out reason);

        /// <summary>Captures the current moment and writes it to disk. Throws InvalidOperationException with a
        /// player-facing reason when it can't (see CanCapture).</summary>
        /// <param name="name">Display name; null picks one ("State 3", "Quick save", "Dawn 4"...).</param>
        public static SaveStateInfo Capture(string name = null, SaveStateKind kind = SaveStateKind.Manual)
        {
            if (!Feature<SaveStatesManager>.On) throw new InvalidOperationException("Savestates are turned off");
            if (!CanCapture(out string reason)) throw new InvalidOperationException(reason);
            string level = CurrentLevel;
            var data = StateCapture.Capture();
            data.kind = kind.ToString();
            data.name = name ?? DefaultName(level, kind, data);
            var info = Write(level, kind, data);
            Last = info;
            Prune(level);
            Plugin.Logger.LogInfo($"Savestates: captured {info} -> {info.JsonPath}; {StateRestorer.Summary()}");
            Captured?.Invoke(info);
            if (kind is SaveStateKind.AutoDawn or SaveStateKind.AutoDusk) AutoCaptured?.Invoke(info);
            Changed?.Invoke();
            return info;
        }

        /// <summary>Whether a state can be loaded now; reason says why not.</summary>
        public static bool CanRestore(SaveStateInfo state, out string reason) => StateRestorer.CanRestore(state, out reason);

        /// <summary>Starts loading a state into the current level (same level only). Marks the run with
        /// GameplayImpact. Throws InvalidOperationException with a player-facing reason when it can't.</summary>
        public static void Restore(SaveStateInfo state)
        {
            if (!Feature<SaveStatesManager>.On) throw new InvalidOperationException("Savestates are turned off");
            if (!CanRestore(state, out string reason)) throw new InvalidOperationException(reason);
            StateRestorer.Begin(state);
        }

        public static void Delete(SaveStateInfo state)
        {
            TryDelete(state.JsonPath);
            TryDelete(state.ThumbnailPath);
            cache.Remove(state.JsonPath);
            if (Last == state) Last = null;
            Thumbnails.Forget(state);
            Changed?.Invoke();
        }

        public static void Rename(SaveStateInfo state, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty");
            state.Data.name = name.Trim();
            WriteJson(state.JsonPath, state.Data);
            Changed?.Invoke();
        }

        /// <summary>Replaces a state's contents with the current moment, keeping its name and kind.</summary>
        public static SaveStateInfo Overwrite(SaveStateInfo state)
        {
            var fresh = Capture(state.Name, state.Kind);
            if (fresh.JsonPath != state.JsonPath) Delete(state);
            return fresh;
        }

        internal static void RaiseRestoring(SaveStateInfo s) => Restoring?.Invoke(s);
        internal static void RaiseRestored(SaveStateInfo s) => Restored?.Invoke(s);

        // ---- Disk ---------------------------------------------------------------------------------------------------

        internal static string LevelFolder(string level)
        {
            string safe = new(level.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Path.Combine(Root, safe);
        }

        private static SaveStateInfo Read(string file)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (cache.TryGetValue(file, out var hit) && hit.Stamp == stamp) return hit.Info;
                var data = JsonConvert.DeserializeObject<SaveStateData>(File.ReadAllText(file), JsonSettings);
                if (data == null || data.format > SaveStateData.CurrentFormat || string.IsNullOrEmpty(data.level)) return null;
                var info = new SaveStateInfo(file, data);
                cache[file] = (stamp, info);
                return info;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Savestates: skipping unreadable {file}: {e.Message}");
                return null;
            }
        }

        private static SaveStateInfo Write(string level, SaveStateKind kind, SaveStateData data)
        {
            string dir = LevelFolder(level);
            Directory.CreateDirectory(dir);
            // The quick slot is one file that each quick save replaces; everything else gets a new file.
            string id = kind == SaveStateKind.Quick ? "quick" : $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{kind.ToString().ToLowerInvariant()}";
            string json = Path.Combine(dir, id + ".json");
            WriteJson(json, data);
            string png = Path.ChangeExtension(json, ".png");
            TryDelete(png);
            Thumbnails.ForgetPath(png);
            cache.Remove(json);
            // The thumbnail is the frame being drawn now, so it's written at the end of this frame.
            Plugin.Instance.StartCoroutine(StateCapture.WriteThumbnail(png, bytes =>
            {
                if (bytes == null || !File.Exists(json)) return;
                WriteAtomic(png, bytes);
                Thumbnails.ForgetPath(png);
                Changed?.Invoke();
            }));
            return Read(json) ?? throw new IOException($"couldn't read back {json}");
        }

        private static void WriteJson(string path, SaveStateData data) =>
            WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data, JsonSettings)));

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Savestates: couldn't delete {path}: {e.Message}"); }
        }

        // Keeps the newest N automatic states (dawn and dusk together) and the newest few undo states.
        private static void Prune(string level)
        {
            var all = List(level);
            int history = Feature<SaveStatesManager>.Instance?.AutoHistory ?? 10;
            foreach (var old in all.Where(s => s.Kind is SaveStateKind.AutoDawn or SaveStateKind.AutoDusk).Skip(history)) Delete(old);
            foreach (var old in all.Where(s => s.Kind == SaveStateKind.BeforeLoad).Skip(3)) Delete(old);
        }

        private static string DefaultName(string level, SaveStateKind kind, SaveStateData data)
        {
            int night = data.night ? data.wave + 1 : data.wave + 2;
            switch (kind)
            {
                case SaveStateKind.Quick: return "Quick save";
                case SaveStateKind.AutoDawn: return $"Dawn of day {night}";
                case SaveStateKind.AutoDusk: return $"Before night {night}";
                case SaveStateKind.BeforeLoad: return "Before last load";
            }
            var used = new HashSet<string>(List(level).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            for (int i = 1; ; i++)
            {
                string candidate = $"State {i}";
                if (!used.Contains(candidate)) return candidate;
            }
        }
    }
}
