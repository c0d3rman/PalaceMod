using UnityEngine;
using HarmonyLib;
using Rewired;

namespace PalaceMod
{
    public class BuildPreviewManager : BaseManager
    {
        public override string FeatureSection => "Build previews";
        public override string FeatureDescription => "Shows what every empty build slot can become: toggled with the game's preview button and switched on automatically at dawn.";
        protected override void OnFeatureDisabled()
        {
            previewsEnabled = false;
            BuildingInteractor.displayAllBuildPreviews = false;
        }

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
