using HarmonyLib;

// Custom runs never reach the platform leaderboards (the end screen uploads the score under the host's scene name).
namespace PalaceMod
{
    public partial class BuilderManager
    {
        [HarmonyPatch(typeof(Thronefall.GameServices.SteamGameServices), nameof(Thronefall.GameServices.SteamGameServices.UploadHighscore))]
        private static class Leaderboard_Patch
        {
            private static bool Prefix(int score, string leaderboardTableName)
            {
                if (!InSession) return true;
                Plugin.Logger.LogInfo($"Builder: skipped leaderboard upload ({leaderboardTableName}: {score}) for a custom run");
                return false;
            }
        }
    }
}
