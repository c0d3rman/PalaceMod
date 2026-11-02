using HarmonyLib;
using System;
using System.Collections.Generic;


/*
    When units with decimal max HP values (like Berserks) are loaded from a save, their HP is rounded down to the nearest integer.
    This causes them to not be at full health even during daytime.
    This patch fixes that.
*/
namespace PalaceMod
{
    public class HealthFixManager : BaseManager
    {
        public override string FeatureSection => "Unit health fix";
        public override string FeatureDescription => "Restores full health to units with fractional max HP (such as berserkers) that a loaded save rounded down.";

        [HarmonyPatch(typeof(Hp), "Start")]
        private class Hp_Start_Patch
        {
            private static void Postfix(Hp __instance)
            {
                try
                {
                    // Only affect player-owned units
                    var taggedObject = AccessTools.Field(typeof(Hp), "taggedObject").GetValue(__instance);
                    var tags = AccessTools.Property(taggedObject.GetType(), "Tags").GetValue(taggedObject) as List<TagManager.ETag>;
                    if (!tags.Contains(TagManager.ETag.PlayerOwned)) return;

                    // Only for save loads
                    if (!(bool)AccessTools.Field(typeof(Hp), "gotLoadedHpValue").GetValue(__instance)) return;

                    var hpField = AccessTools.Field(typeof(Hp), "hp");
                    var hp = (float)hpField.GetValue(__instance);
                    var maxHp = (float)AccessTools.Field(typeof(Hp), "maxHp").GetValue(__instance);


                    // If HP is close to max but not quite there (within 1 HP)
                    if (hp < maxHp && hp > maxHp - 1.01f)
                    {
                        hpField.SetValue(__instance, maxHp);
                        __instance.OnHpChange.Invoke();
                        Plugin.Logger.LogInfo($"Fixed HP for unit {__instance.gameObject.name}: {hp} → {maxHp}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error in HealthFixManager: {ex.Message}");
                }
            }
        }
    }
}
