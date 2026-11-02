using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using MPUIKIT;
using UnityEngine;

/*
    Harmony patches that remove per-frame work and garbage from game code. Each one produces exactly what the
    original does, and each can be switched off on its own (Optimizations section) in case it ever conflicts with
    a game update or another mod. A disabled patch runs the original code.
*/
namespace PalaceMod
{
    public partial class PerformanceManager
    {
        private const string OptimizationsSection = "Optimizations";

        private static ConfigEntry<bool> cacheUnitLists;
        private static ConfigEntry<bool> reuseFrustumPlanes;
        private static ConfigEntry<bool> cachePowerTowerParts;
        private static ConfigEntry<bool> skipSettledHealthBars;

        private static void BindOptimizations(ConfigFile config)
        {
            cacheUnitLists = config.Bind(OptimizationsSection, "Cache unit lists", true,
                "Reuse one read-only view of the player/enemy unit lists instead of allocating a new one on every access.");
            reuseFrustumPlanes = config.Bind(OptimizationsSection, "Reuse camera frustum planes", true,
                "Fill one reused array with the camera's frustum planes each frame instead of allocating a new one.");
            cachePowerTowerParts = config.Bind(OptimizationsSection, "Cache power tower lookups", true,
                "With the power tower perk, look up the boosted tower's components when the target tower is refreshed " +
                "(5 times a second) instead of every frame.");
            skipSettledHealthBars = config.Bind(OptimizationsSection, "Skip settled health bars", true,
                "Skip a health bar's per-frame update when nothing about it is changing (no drain, flash or damage pulse).");
        }

        // TagManager.PlayerUnits/EnemyUnits/Players wrap their list in a new ReadOnlyCollection on every access.
        // A ReadOnlyCollection is a live view, so handing out one per list is equivalent.
        private static class ReadOnlyViews
        {
            private static readonly ConditionalWeakTable<List<TaggedObject>, ReadOnlyCollection<TaggedObject>> Views = new();
            private static readonly ConditionalWeakTable<List<TaggedObject>, ReadOnlyCollection<TaggedObject>>.CreateValueCallback Create = list => list.AsReadOnly();

            public static bool Replace(List<TaggedObject> list, ref IReadOnlyList<TaggedObject> result)
            {
                if (!cacheUnitLists.Value || list == null) return true;
                result = Views.GetValue(list, Create);
                return false;
            }
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.PlayerUnits), MethodType.Getter)]
        private class TagManager_PlayerUnits_Patch
        {
            private static bool Prefix(List<TaggedObject> ___bufferedPlayerUnits, ref IReadOnlyList<TaggedObject> __result) =>
                ReadOnlyViews.Replace(___bufferedPlayerUnits, ref __result);
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.EnemyUnits), MethodType.Getter)]
        private class TagManager_EnemyUnits_Patch
        {
            private static bool Prefix(List<TaggedObject> ___bufferedEnemyUnits, ref IReadOnlyList<TaggedObject> __result) =>
                ReadOnlyViews.Replace(___bufferedEnemyUnits, ref __result);
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.Players), MethodType.Getter)]
        private class TagManager_Players_Patch
        {
            private static bool Prefix(List<TaggedObject> ___bufferedPlayers, ref IReadOnlyList<TaggedObject> __result) =>
                ReadOnlyViews.Replace(___bufferedPlayers, ref __result);
        }

        // GPUInstancingManager.Update allocates a new Plane[6] every frame; fill one reused array instead.
        [HarmonyPatch(typeof(GPUInstancingManager), "Update")]
        private class GPUInstancingManager_Update_Patch
        {
            private static readonly Plane[] Planes = new Plane[6];

            public static Plane[] CalculateFrustumPlanes(Camera camera)
            {
                if (!reuseFrustumPlanes.Value) return GeometryUtility.CalculateFrustumPlanes(camera);
                GeometryUtility.CalculateFrustumPlanes(camera, Planes);
                return Planes;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools.Method(typeof(GeometryUtility), nameof(GeometryUtility.CalculateFrustumPlanes), [typeof(Camera)]);
                var replacement = AccessTools.Method(typeof(GPUInstancingManager_Update_Patch), nameof(CalculateFrustumPlanes));
                bool replaced = false;
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        instruction.operand = replacement;
                        replaced = true;
                    }
                    yield return instruction;
                }
                if (!replaced) Log.For<PerformanceManager>().Warn("GPUInstancingManager.Update no longer calls CalculateFrustumPlanes; patch skipped");
            }
        }

        // PowerTowerPerk.Update calls GetComponentsInChildren twice per frame at night. Look the components up again
        // whenever the game refreshes its closest tower (every 0.2 s), at nightfall, or when one of them is deactivated.
        [HarmonyPatch(typeof(PowerTowerPerk), "Update")]
        private class PowerTowerPerk_Update_Patch
        {
            private class TowerParts
            {
                public TaggedObject Tower;
                public AutoAttack[] Attacks;
                public HotOilTower[] HotOil;
            }

            private static readonly ConditionalWeakTable<PowerTowerPerk, TowerParts> Cache = new();

            private static bool AllActive(MonoBehaviour[] behaviours)
            {
                if (behaviours == null) return false;
                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null || !behaviour.gameObject.activeInHierarchy) return false;
                }
                return true;
            }

            private static bool Prefix(PowerTowerPerk __instance, TagManager ___tagManager, float ___attackSpeedBonus, ref float ___nextRefreshIn,
                ref TaggedObject ___closestTower, List<TagManager.ETag> ___mustHaveTags, List<TagManager.ETag> ___mayNotHaveTags)
            {
                if (!cachePowerTowerParts.Value) return true;
                var parts = Cache.GetOrCreateValue(__instance);
                if (DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day)
                {
                    parts.Tower = null; // towers can be upgraded during the day, so look again at nightfall
                    return false;
                }
                ___nextRefreshIn -= Time.deltaTime;
                if (___nextRefreshIn <= 0f)
                {
                    ___closestTower = ___tagManager.FindClosestTaggedObjectWithTags(__instance.transform.position, ___mustHaveTags, ___mayNotHaveTags);
                    ___nextRefreshIn = 0.2f;
                    parts.Tower = null; // re-read the components below
                }
                if (___closestTower == null) return false;
                // The original only finds components on active objects; a knocked-out tower deactivates its parts.
                if (!ReferenceEquals(parts.Tower, ___closestTower) || !AllActive(parts.Attacks) || !AllActive(parts.HotOil))
                {
                    parts.Tower = ___closestTower;
                    parts.Attacks = ___closestTower.GetComponentsInChildren<AutoAttack>();
                    parts.HotOil = ___closestTower.GetComponentsInChildren<HotOilTower>();
                }
                float reduction = ___attackSpeedBonus * Time.deltaTime;
                foreach (var attack in parts.Attacks) attack.ReduceCooldownBy(reduction);
                foreach (var oil in parts.HotOil) oil.ReduceCooldownBy(reduction);
                return false;
            }
        }

        // Healthbar.Update runs every frame for every unit and building that has one, and always writes
        // transform.localScale. When the bar is settled (delayed fill caught up, no new damage, damage pulse faded,
        // not flashing) the original changes nothing visible, so skip it.
        [HarmonyPatch(typeof(Healthbar), "Update")]
        private class Healthbar_Update_Patch
        {
            private const float SettledPulse = 1e-4f;

            private static bool Prefix(MPImageBasic ___fill, MPImageBasic ___delayedFill, float ___hpLast, float ___damageFlash,
                bool ___flashWhenBelowThreshold, float ___flashThreshhold, MPImageBasic ___flashTarget, Color ___flashDefaultColor)
            {
                if (!skipSettledHealthBars.Value) return true;
                float hp = ___fill.fillAmount;
                if (hp <= 0f || hp != ___hpLast || ___damageFlash > SettledPulse) return true;
                if (!Mathf.Approximately(hp, ___delayedFill.fillAmount)) return true;
                if (___flashWhenBelowThreshold && (hp <= ___flashThreshhold || ___flashTarget.color != ___flashDefaultColor)) return true;
                return false;
            }
        }
    }
}
