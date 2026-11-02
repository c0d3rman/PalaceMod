using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace PalaceMod
{
    /*
    When you retry day with God of Destruction active,
    if any buildings are destroyed, they will replay their destruction effects and sounds.
    This manager fixes that bug.

    Also prevents shrine particles from playing when loading from save or restarting day.
    */
    public class BuildingDestructionEffectBugFixManager : BaseManager
    {
        public override string FeatureSection => "Destruction effect fix";
        public override string FeatureDescription => "Stops destroyed buildings and shrines from replaying their destruction or activation effects when a day is retried or a save is loaded.";

        [HarmonyPatch(typeof(BuildingDestructionHandler), "OnEnable")]
        private class BuildingDestructionHandler_OnEnable_Patch
        {
            static bool Prefix(BuildingDestructionHandler __instance)
            {
                // Skip effects if this building is marked as destroyed
                var taggedObject = __instance.GetComponentInParent<TaggedObject>();
                if (taggedObject != null &&
                    taggedObject.Tags.Contains(TagManager.ETag.AUTO_KnockedOutAndHealOnDawn) &&
                    taggedObject.Tags.Contains(TagManager.ETag.AUTO_NoReviveNextMorning))
                {
                    Plugin.Logger.LogInfo($"Suppressing destruction effects for {__instance.name} on save load");
                    return false; // Skip original method
                }
                return true; // Run original method
            }
        }

        [HarmonyPatch(typeof(Shrine), "ActivateShrine")]
        private class Shrine_ActivateShrine_Patch
        {
            static void Prefix(Shrine __instance, bool _calledFromSaveLoad, ref ParticleSystem __state)
            {
                if (_calledFromSaveLoad)
                {
                    Plugin.Logger.LogInfo($"Suppressing shrine activation particles on save load");

                    var particleSystem = AccessTools.Field(typeof(Shrine), "upgradeParticles").GetValue(__instance) as ParticleSystem;
                    particleSystem.gameObject.SetActive(false);
                }
            }

            static void Postfix(Shrine __instance, bool _calledFromSaveLoad, ParticleSystem __state)
            {
                if (_calledFromSaveLoad && __state != null)
                {
                    var particleSystem = AccessTools.Field(typeof(Shrine), "upgradeParticles").GetValue(__instance) as ParticleSystem;
                    particleSystem.gameObject.SetActive(true);
                }
            }
        }
    }
}
