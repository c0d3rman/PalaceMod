using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PalaceMod.Builder;

/*
    Custom sessions (WP3): progress isolation while a session runs, and the pre-level menu's armed state. See
    SessionLauncher for the lifecycle and SessionMenu for the menu. Every patch is a no-op unless a session runs or a
    design is armed, so vanilla play is untouched; they stay applied while the Builder is switched off so a run in
    progress keeps its isolation.
*/
namespace PalaceMod
{
    public partial class BuilderManager
    {
        private static bool InSession => Launcher?.Current != null;

        // ------------------------------------------------------------------ isolation

        // Scores, networth, "beaten" and quest state for the host go to the session's own LevelData.
        [HarmonyPatch(typeof(LevelProgressManager), nameof(LevelProgressManager.GetLevelDataForScene))]
        private static class LevelData_Patch
        {
            private static bool Prefix(string _sceneName, ref LevelData __result)
            {
                var s = Launcher?.Current;
                if (s == null || _sceneName != s.HostScene) return true;
                __result = s.Scores;
                return false;
            }
        }

        // The stand-in's beatenBest stays the host's vanilla value: campaign unlocks read it (Equippable.IsUnlocked),
        // so a custom win must not grant them, nor an unbeaten design hide them. Custom wins are in ScoreStore.
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.SaveScoreAndStatsToBestIfBest))]
        private static class KeepVanillaBeaten_Patch
        {
            private static void Postfix(LevelData __instance)
            {
                var l = Launcher;
                if (l?.Current != null && __instance == l.Current.Scores) __instance.beatenBest = l.VanillaBeaten;
            }
        }

        [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveGame))]
        private static class SaveGame_Patch
        {
            private static bool Prefix()
            {
                if (!InSession) return true;
                Log.For<BuilderManager>().Debug("Skipped writing the save file during a custom run");
                return false;
            }
        }

        // Keep the in-memory match save working (retry day), but never write it over the host's file.
        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private static class SaveRun_Patch
        {
            private static bool Prefix()
            {
                if (!InSession) return true;
                MatchSaveLoadHandler.CurrentSave?.ConvertObjectDataToStrings();
                return false;
            }
        }

        [HarmonyPatch]
        private static class Achievements_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in new[]
                {
                    nameof(AchievementManager.UnlockAchievement), nameof(AchievementManager.LevelBeaten), nameof(AchievementManager.LevelAllQuestsComplete),
                    nameof(AchievementManager.GiveCrownsAchievement), nameof(AchievementManager.UnlockEternalTrialsAchievementForBeating),
                })
                    yield return AccessTools.Method(typeof(AchievementManager), name);
            }

            private static bool Prefix() => !InSession;
        }

        // Online leaderboards are keyed by the host map: a custom run must never post to them. Every platform's
        // service (Steam, EGS, ...) implements UploadHighscore(int, string).
        [HarmonyPatch]
        private static class Leaderboard_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.GetTypesFromAssembly(typeof(LevelProgressManager).Assembly)
                    .Where(t => !t.IsInterface && !t.IsAbstract)
                    .Select(t => t.GetMethod("UploadHighscore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, [typeof(int), typeof(string)], null))
                    .Where(m => m != null && m.GetMethodBody() != null);

            private static bool Prefix() => !InSession;
        }

        // The end screen: no XP and no "map beaten" unlock rewards for a custom run (the perk level/XP are also
        // restored when the session ends, as a backstop).
        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), "FillUpXPBar")]
        private static class NoXp_Patch
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                if (!InSession) return true;
                __result = Empty();
                return false;
            }
        }

        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), "ShowCampaignLevelBeatenRewards")]
        private static class NoUnlockRewards_Patch
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                if (!InSession) return true;
                __result = Empty();
                return false;
            }
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        // ------------------------------------------------------------------ results and restarts

        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromGameplayToEndScreen))]
        private static class EndScreen_Patch
        {
            private static void Postfix(SceneTransitionManager __instance)
            {
                if (!InSession || LocalGamestate.SelectedGameMode != LocalGamestate.GameMode.Classic) return;
                try { Launcher.RecordResult(__instance.TotalScoreFromLastMatch); }
                catch (Exception e) { Log.For<BuilderManager>().Error($"Couldn't record the result", e); }
            }
        }

        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.RestartCurrentLevel))]
        private static class Restart_Patch
        {
            private static void Prefix()
            {
                if (InSession) Launcher.OnRestart();
            }
        }

        // A night's "warning" text (the game stores Wave.warningText but never shows it) as the night starts.
        [HarmonyPatch(typeof(EnemySpawner), nameof(EnemySpawner.StartSpawning))]
        private static class NightWarning_Patch
        {
            private static void Postfix(EnemySpawner __instance)
            {
                var nights = Launcher?.Current?.WaveSet?.Nights;
                if (nights == null) return;
                int n = __instance.Wavenumber;
                if (n < 0 || n >= nights.Count || string.IsNullOrWhiteSpace(nights[n].Warning)) return;
                Notify.Show(nights[n].Warning, NotifyKind.Warning, 6f, "builder-night");
            }
        }

        // ------------------------------------------------------------------ pre-level menu

        // Play in an armed pre-level menu starts the custom session; the game's own transition then loads the host.
        // Runs first so GameplayImpact sees the fresh-run flag the session sets.
        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromLevelSelectToLevel))]
        private static class Play_Patch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(string _levelname)
            {
                var l = Launcher;
                if (l?.Armed == null) return true;
                if (!Feature<BuilderManager>.On)
                {
                    // Switched off while a design was loaded: don't quietly start a vanilla run in its place.
                    l.Disarm();
                    Notify.Show("The Builder was turned off, so the custom design was unloaded.", NotifyKind.Warning, 6f, "builder-menu");
                    UIFrameManager.instance?.CloseAllFrames();
                    return false;
                }
                if (l.Armed.Host != _levelname)
                {
                    l.Disarm();
                    return true;
                }
                if (l.BeginArmed(out string error)) return true;
                Notify.Show($"Couldn't start the custom run: {error}", NotifyKind.Error, 6f, "builder-menu");
                return false;
            }
        }

        [HarmonyPatch(typeof(LevelSelectUIFrameHelper), nameof(LevelSelectUIFrameHelper.OnShow))]
        private static class PreLevelShow_Patch
        {
            private static void Postfix(LevelSelectUIFrameHelper __instance)
            {
                if (Launcher == null) return;
                try { SessionMenu.OnPreLevelShow(__instance); }
                catch (Exception e) { Log.For<BuilderManager>().Error($"Pre-level menu", e); }
            }
        }

        // Coming back from the Custom Runs list, the game re-selects what was selected before it opened, which may
        // now be hidden (arming swaps "New Game / Continue" for "Play"). Fall back to the menu's first element.
        [HarmonyPatch(typeof(UIFrame), nameof(UIFrame.Activate))]
        private static class PreLevelSelection_Patch
        {
            private static void Postfix(UIFrame __instance)
            {
                if (Launcher == null || __instance.GetComponent<LevelSelectUIFrameHelper>() == null) return;
                var sel = __instance.CurrentSelection;
                if ((sel == null || !sel.gameObject.activeInHierarchy) && __instance.firstSelected != null) __instance.Select(__instance.firstSelected);
            }
        }

        // Walking onto a map opens its pre-level menu fresh, without a design.
        [HarmonyPatch(typeof(LevelInteractor), nameof(LevelInteractor.InteractionBegin))]
        private static class Interact_Patch
        {
            private static void Prefix() => Launcher?.Disarm();
        }

        // Leaving the menus (back to walking around the overworld) forgets the armed design.
        [HarmonyPatch(typeof(UIFrameManager), "ProcessFrameChange")]
        private static class FramesClosed_Patch
        {
            private static void Postfix(UIFrame nextFrame)
            {
                if (nextFrame == null) Launcher?.Disarm();
            }
        }

        // The design's name under the map's title: in the armed pre-level menu and in the in-match pause menu.
        [HarmonyPatch(typeof(LevelNameDisplay), nameof(LevelNameDisplay.UpdateLevelTitle))]
        private static class Title_Patch
        {
            private static void Postfix(LevelNameDisplay __instance)
            {
                var l = Launcher;
                string name = l?.Current != null ? SessionLauncher.SessionName(l.Current) : l?.Armed?.Name;
                if (name == null || __instance.levelTitle == null) return;
                __instance.levelTitle.text += $"\n<size={__instance.untertitleSize}><style=Subheader>Custom: {name}</style></size>";
            }
        }

        // The highscore box shows the design's custom best; friends' ranks are for the vanilla map.
        [HarmonyPatch(typeof(HighscorePreviewUI), "OnEnable")]
        private static class HighscorePreview_Patch
        {
            private static void Postfix(HighscorePreviewUI __instance)
            {
                var l = Launcher;
                if (l?.Armed == null && !InSession) return;
                int? best = l.Armed != null ? l.Armed.Best : l.Current.Scores.highscoreBest;
                if (__instance.highscore != null)
                    __instance.highscore.text = "Custom best: <style=Body Light>" + (best is > 0 ? best.ToString() : "-");
                if (__instance.friendsRank != null) __instance.friendsRank.text = "<style=Body Light>Custom run";
            }
        }

        [HarmonyPatch(typeof(HighscorePreviewUI), nameof(HighscorePreviewUI.RefreshUI))]
        private static class FriendsRank_Patch
        {
            private static bool Prefix() => Launcher?.Armed == null && !InSession;
        }
    }
}
