using HarmonyLib;
using PalaceMod.Builder;

// Troops package patches (see TroopManager.cs).
namespace PalaceMod
{
    public partial class BuilderManager
    {
        // The game sets the shared unit materials' colours from the level's colour scheme when a level starts, and
        // re-tints their outlines with the daylight. Tinted design materials are clones, so they'd keep whatever the
        // source had when they were made (black for troops cloned on scene load, before the scheme is applied).
        [HarmonyPatch(typeof(ColorAndLightManager))]
        private static class ColorAndLightManager_Tint_Patch
        {
            [HarmonyPostfix]
            [HarmonyPatch(nameof(ColorAndLightManager.ApplyColorScheme))]
            private static void AfterScheme() => UnitFactory.ResyncTints();

            [HarmonyPostfix]
            [HarmonyPatch(nameof(ColorAndLightManager.AdjustAllOutlineColorsBasedOnLight))]
            private static void AfterOutline() => UnitFactory.ResyncTints();
        }
    }
}
