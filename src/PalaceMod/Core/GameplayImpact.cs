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

    A run is one level's match in Classic mode, or a whole Eternal Trials run (all of its stages). The record is cleared
    when a run starts fresh: a Classic level is entered without a match save to continue (new run, "fresh start", or
    the last run finished), or a new Eternal Trials run is created. Retrying a day or continuing a saved match keeps it.

    Records are saved next to the game's saves, one per run, so quitting and resuming a mid-run save doesn't clear
    them. Read-only features (info displays) must never call Mark.
*/
namespace PalaceMod
{
    public static class GameplayImpact
    {
        private const string EternalTrialsKey = "Eternal Trials";
        private const string DiscardedEternalTrialsKey = "Eternal Trials (discarded)";

        private static readonly List<string> NoReasons = [];
        private static readonly Dictionary<string, List<string>> records = LoadRecords();
        private static string currentKey;
        private static List<string> reasons = [];

        /// <summary>Whether anything gameplay-changing was used this run.</summary>
        public static bool Any => reasons.Count > 0;

        /// <summary>Distinct reasons, in the order they were first marked.</summary>
        public static IReadOnlyList<string> Reasons => reasons;

        /// <summary>Raised the first time each reason is marked in a run.</summary>
        public static event Action<string> Marked;

        /// <summary>Raised when the current run changes (a fresh run starts, or a saved run with its own record is
        /// resumed). Read Any and Reasons again.</summary>
        public static event Action Reset;

        /// <summary>Records that a gameplay-changing feature was used. Marking the same reason again is a no-op.</summary>
        public static void Mark(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reasons.Contains(reason)) return;
            reasons.Add(reason);
            if (currentKey != null)
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
                // An Eternal Trials run spans several levels; it's only cleared when a new run is created.
                Switch(EternalTrialsKey, fresh: false);
                return;
            }
            var save = MatchSaveLoadHandler.CurrentSave;
            bool continuing = !MatchSaveLoadHandler.OverwriteCurrentSave && save != null && !save.runComplete;
            Switch(levelName, fresh: !continuing);
        }

        internal static void EternalTrialsRunCreated()
        {
            if (records.ContainsKey(EternalTrialsKey) || currentKey == EternalTrialsKey) Switch(EternalTrialsKey, fresh: true, force: true);
        }

        // The game keeps the abandoned run around so "back" on the new-run screen can bring it back.
        internal static void EternalTrialsRunDiscarded()
        {
            if (records.TryGetValue(EternalTrialsKey, out var r)) records[DiscardedEternalTrialsKey] = [.. r];
            else records.Remove(DiscardedEternalTrialsKey);
            SaveRecords();
        }

        internal static void EternalTrialsRunRestored()
        {
            if (records.TryGetValue(DiscardedEternalTrialsKey, out var r)) records[EternalTrialsKey] = [.. r];
            else records.Remove(EternalTrialsKey);
            SaveRecords();
            if (currentKey == EternalTrialsKey) Switch(EternalTrialsKey, fresh: false, force: true);
        }

        private static void Switch(string key, bool fresh, bool force = false)
        {
            if (fresh)
            {
                if (records.Remove(key))
                {
                    Plugin.Logger.LogInfo($"Gameplay impact: cleared for a new run of {key}");
                    SaveRecords();
                }
            }
            var next = records.TryGetValue(key, out var stored) ? stored : [];
            bool changed = force || key != currentKey || fresh || !next.SequenceEqual(reasons);
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

    // Hosts the run-start patches (Harmony patches only apply inside managers).
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
            private static void Postfix() => GameplayImpact.EternalTrialsRunCreated();
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
                var current = EternalTrialsRunManager.CurrentRun;
                if (current != __state && current == EternalTrialsRunManager.LastDiscardedRun) GameplayImpact.EternalTrialsRunRestored();
            }
        }
    }
}
