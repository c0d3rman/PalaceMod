using HarmonyLib;
using PalaceMod.Builder;

// Progress isolation for custom sessions (owned by WP3; see SessionLauncher). Active only while a session runs.
namespace PalaceMod
{
    public partial class BuilderManager
    {
        private static bool InSession => Launcher?.Current != null;

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

        [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveGame))]
        private static class SaveGame_Patch
        {
            private static bool Prefix()
            {
                if (!InSession) return true;
                Plugin.Logger.LogInfo("Builder: skipped writing the save file during a custom run");
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
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
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
    }
}
