using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

/*
    Records that the player used something that changes gameplay (debug menu, save states, undo, level-4 buildings...)
    during the current run, so the run's score can be treated differently (see HighscoreGuardManager).

        GameplayImpact.Mark("Undo upgrade");
        if (GameplayImpact.Any) ...

    A run is one level's match in Classic mode, or a whole Eternal Trials run (all of its stages). A fresh run starts
    with an empty record: a Classic level entered without its match save (new run, "fresh start"), or a new Eternal
    Trials run. Retrying a day or continuing a saved match keeps the record.

    Records are saved next to the game's saves, one per run, and follow the game's own saves: a fresh run's record only
    replaces the old one once the game writes the fresh run's first save, so the old save can't be continued without
    its record, and quitting and resuming a mid-run save keeps it. Read-only features (info displays) must never call
    Mark.
*/
namespace PalaceMod
{
    public static class GameplayImpact
    {
        private const string EternalTrialsKey = "Eternal Trials";

        // Records of runs whose save is on disk, by run. Mirrors the record file.
        private static readonly Dictionary<string, List<string>> records = LoadRecords();
        // Fresh runs whose first save hasn't been written yet. Until it is, the old save (and its record) still exists
        // and can be continued, so the old record stays in `records`.
        private static readonly Dictionary<string, List<string>> pending = [];
        private static string currentKey;
        private static List<string> reasons = [];

        /// <summary>Whether anything gameplay-changing was used this run.</summary>
        public static bool Any => reasons.Count > 0;

        /// <summary>Distinct reasons, in the order they were first marked.</summary>
        public static IReadOnlyList<string> Reasons => reasons;

        /// <summary>Raised the first time each reason is marked in a run.</summary>
        public static event Action<string> Marked;

        /// <summary>Raised when the current run changes (a fresh run starts, or a saved run is resumed). Read Any and
        /// Reasons again.</summary>
        public static event Action Reset;

        /// <summary>Records that a gameplay-changing feature was used. Marking the same reason again is a no-op.</summary>
        public static void Mark(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reasons.Contains(reason)) return;
            reasons.Add(reason);
            if (currentKey != null && !pending.ContainsKey(currentKey))
            {
                records[currentKey] = reasons;
                SaveRecords();
            }
            Plugin.Logger.LogInfo($"Gameplay impact: {reason}");
            Marked?.Invoke(reason);
        }

        // Called just before a level scene loads.
        internal static void EnteringLevel(string levelName)
        {
            if (LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.EternalTrial)
            {
                // An Eternal Trials run spans several levels; a new one starts in EternalTrialsRunCreated.
                Switch(EternalTrialsKey);
                return;
            }
            // Mirrors MatchSaveLoadHandler.IsLoadingPermitted for Classic: the level's match save is loaded unless
            // there's none or the player chose to start over.
            var save = MatchSaveLoadHandler.CurrentSave;
            bool continuing = !MatchSaveLoadHandler.OverwriteCurrentSave && save != null;
            if (continuing) pending.Remove(levelName);
            else
            {
                pending[levelName] = [];
                replacedSave = save;
            }
            Switch(levelName, force: !continuing);
        }

        // The save a pending Classic run will replace, and the fresh Eternal Trials run that's pending. Until the game
        // writes a different save object, writes still go to the old run (e.g. a defeat before the first dawn
        // re-saves the old match save).
        private static MatchSave replacedSave;
        private static EternalTrialsRun freshEternalTrialsRun;
        // The game keeps an abandoned Eternal Trials run in memory and can bring it back; its record goes with it.
        private static List<string> discardedEternalTrialsReasons;

        internal static void EternalTrialsRunCreated(EternalTrialsRun run)
        {
            pending[EternalTrialsKey] = [];
            freshEternalTrialsRun = run;
            if (currentKey == EternalTrialsKey) Switch(EternalTrialsKey, force: true);
        }

        internal static void EternalTrialsRunDiscarded()
        {
            var r = pending.TryGetValue(EternalTrialsKey, out var p) ? p : records.TryGetValue(EternalTrialsKey, out var stored) ? stored : [];
            discardedEternalTrialsReasons = [.. r];
        }

        internal static void EternalTrialsRunRestored()
        {
            pending.Remove(EternalTrialsKey);
            var r = discardedEternalTrialsReasons ?? [];
            if (r.Count > 0) records[EternalTrialsKey] = [.. r];
            else records.Remove(EternalTrialsKey);
            SaveRecords();
            if (currentKey == EternalTrialsKey) Switch(EternalTrialsKey, force: true);
        }

        internal static void MatchSaved(MatchSave save)
        {
            if (currentKey == null || currentKey == EternalTrialsKey || save == null || save == replacedSave) return;
            Promote(currentKey);
        }

        internal static void EternalTrialsRunSaved(EternalTrialsRun run)
        {
            if (run != null && run == freshEternalTrialsRun) Promote(EternalTrialsKey);
        }

        // The game wrote the fresh run's first save: the record now belongs to it and the old one is gone.
        private static void Promote(string key)
        {
            if (!pending.TryGetValue(key, out var fresh)) return;
            pending.Remove(key);
            if (fresh.Count > 0) records[key] = fresh;
            else if (!records.Remove(key)) return;
            SaveRecords();
        }

        private static void Switch(string key, bool force = false)
        {
            var next = pending.TryGetValue(key, out var p) ? p : records.TryGetValue(key, out var stored) ? stored : [];
            bool changed = force || key != currentKey || next != reasons;
            currentKey = key;
            reasons = next;
            if (!changed) return;
            if (reasons.Count > 0) Plugin.Logger.LogInfo($"Gameplay impact: resumed {key} ({string.Join(", ", reasons)})");
            Reset?.Invoke();
        }

        // ---- Persistence: one line per run, "<run>\t<reason>\t<reason>...". ----

        private static string RecordsPath => Path.Combine(Application.persistentDataPath, "PalaceMod", "GameplayImpact.txt");

        private static Dictionary<string, List<string>> LoadRecords()
        {
            var result = new Dictionary<string, List<string>>();
            try
            {
                if (!File.Exists(RecordsPath)) return result;
                foreach (string line in File.ReadAllLines(RecordsPath))
                {
                    var parts = line.Split('\t');
                    var list = parts.Skip(1).Where(p => p.Length > 0).Distinct().ToList();
                    if (parts[0].Length > 0 && list.Count > 0) result[parts[0]] = list;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Gameplay impact: couldn't read {RecordsPath}: {e.Message}");
            }
            return result;
        }

        private static void SaveRecords()
        {
            try
            {
                string path = RecordsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = records.Where(r => r.Value.Count > 0)
                    .Select(r => string.Join("\t", new[] { Clean(r.Key) }.Concat(r.Value.Select(Clean))));
                File.WriteAllLines(path + ".tmp", lines);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Gameplay impact: couldn't save {RecordsPath}: {e.Message}");
            }
        }

        private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    // Hosts the run lifecycle patches (Harmony patches only apply inside managers).
    public class GameplayImpactManager : BaseManager
    {
        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromLevelSelectToLevel))]
        private class FromLevelSelect_Patch
        {
            private static void Prefix(string _levelname) => GameplayImpact.EnteringLevel(_levelname);
        }

        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromNullToLevel))]
        private class FromNull_Patch
        {
            private static void Prefix(string _levelname) => GameplayImpact.EnteringLevel(_levelname);
        }

        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.RestartCurrentLevel))]
        private class Restart_Patch
        {
            private static void Prefix() => GameplayImpact.EnteringLevel(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        }

        [HarmonyPatch(typeof(EternalTrialsRunManager), nameof(EternalTrialsRunManager.CreateFreshRun))]
        private class EternalTrialsCreate_Patch
        {
            private static void Postfix() => GameplayImpact.EternalTrialsRunCreated(EternalTrialsRunManager.CurrentRun);
        }

        [HarmonyPatch(typeof(EternalTrialsRunManager), nameof(EternalTrialsRunManager.DiscardActiveRun))]
        private class EternalTrialsDiscard_Patch
        {
            private static void Prefix()
            {
                if (EternalTrialsRunManager.HasOngoingRun) GameplayImpact.EternalTrialsRunDiscarded();
            }
        }

        [HarmonyPatch(typeof(EternalTrialsRunManager), nameof(EternalTrialsRunManager.TurnLastDiscardedRunIntoCurrentRun))]
        private class EternalTrialsRestore_Patch
        {
            private static void Prefix(out EternalTrialsRun __state) => __state = EternalTrialsRunManager.CurrentRun;

            private static void Postfix(EternalTrialsRun __state)
            {
                if (EternalTrialsRunManager.CurrentRun != __state) GameplayImpact.EternalTrialsRunRestored();
            }
        }

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private class MatchSaved_Patch
        {
            private static void Postfix() => GameplayImpact.MatchSaved(MatchSaveLoadHandler.CurrentSave);
        }

        [HarmonyPatch(typeof(EternalTrialsRunManager), nameof(EternalTrialsRunManager.SaveRun))]
        private class EternalTrialsSaved_Patch
        {
            private static void Postfix(EternalTrialsRun runToSave) => GameplayImpact.EternalTrialsRunSaved(runToSave);
        }
    }
}
