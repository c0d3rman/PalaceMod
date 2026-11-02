using UnityEngine;
using HarmonyLib;
using Rewired;

namespace PalaceMod
{
    public class BuildPreviewManager : BaseManager
    {
        private bool previewsEnabled = false;
        public bool EnablePreviewsAtDawn = true;  // Configuration flag

        public override void Update()
        {
            if (Plugin.Input?.GetButtonDown("Preview Build Options") == true)
            {
                previewsEnabled = !previewsEnabled;
            }
        }

        [HarmonyPatch(typeof(PlayerInteraction))]
        [HarmonyPatch("RunInteraction")]
        private class PlayerInteraction_RunInteraction_Patch
        {
            static BuildPreviewManager Instance => Plugin.Instance.GetManager<BuildPreviewManager>();

            private static void Postfix()
            {
                BuildingInteractor.displayAllBuildPreviews = Instance.previewsEnabled;
            }
        }

        [HarmonyPatch(typeof(DayNightCycle))]
        [HarmonyPatch("DawnCallAfterSunrise")]
        private class DayNightCycle_DawnCallAfterSunrise_Patch
        {
            static BuildPreviewManager Instance => Plugin.Instance.GetManager<BuildPreviewManager>();

            private static void Postfix()
            {
                if (Instance.EnablePreviewsAtDawn)
                {
                    Instance.previewsEnabled = true;
                    BuildingInteractor.displayAllBuildPreviews = true;
                }
            }
        }
    }
}
