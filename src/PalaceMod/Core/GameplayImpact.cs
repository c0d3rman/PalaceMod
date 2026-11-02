using System;
using System.Collections.Generic;
using HarmonyLib;

/*
    Records that the player used something that changes gameplay (debug menu, save states, undo, level-4 buildings...)
    during the current run, so the run's score can be treated differently.

        GameplayImpact.Mark("Undo upgrade");
        if (GameplayImpact.Any) ...

    The record is cleared when a run starts fresh (the game is about to overwrite the level's match save, i.e. a new
    run or "fresh start") or a different level is entered. Retrying a day or continuing the same level's saved match
    keeps it. It lives in memory only, so it doesn't survive quitting the game.
*/
namespace PalaceMod
{
    public static class GameplayImpact
    {
        private static readonly List<string> reasons = [];

        /// <summary>Whether anything gameplay-changing was used this run.</summary>
        public static bool Any => reasons.Count > 0;

        /// <summary>Distinct reasons, in the order they were first marked.</summary>
        public static IReadOnlyList<string> Reasons => reasons;

        /// <summary>Raised the first time each reason is marked in a run.</summary>
        public static event Action<string> Marked;

        /// <summary>Raised when a new run starts and the record is cleared.</summary>
        public static event Action Reset;

        /// <summary>Records that a gameplay-changing feature was used. Marking the same reason again is a no-op.</summary>
        public static void Mark(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reasons.Contains(reason)) return;
            reasons.Add(reason);
            Plugin.Logger.LogInfo($"Gameplay impact: {reason}");
            Marked?.Invoke(reason);
        }

        private static string level;

        // Called just before a level scene loads.
        internal static void EnteringLevel(string levelName)
        {
            bool fresh = MatchSaveLoadHandler.OverwriteCurrentSave || levelName != level;
            level = levelName;
            if (fresh) StartRun();
        }

        private static void StartRun()
        {
            if (reasons.Count > 0) Plugin.Logger.LogInfo($"Gameplay impact: cleared for a new run ({string.Join(", ", reasons)})");
            reasons.Clear();
            Reset?.Invoke();
        }
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
    }
}
