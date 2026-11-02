using HarmonyLib;
using System.Collections.Generic;
using System;

/*
    Displays the attack range of selected player units.
*/
namespace PalaceMod
{
    public class UnitRangeManager : BaseManager
    {
        // Indicator types in order of how they should be displayed (smallest to largest range, prioritizing smallest)
        private readonly List<RangeIndicatorHandler.IndicatorType> orderedIndicatorTypes = [
            RangeIndicatorHandler.IndicatorType.Built,
            RangeIndicatorHandler.IndicatorType.Preview,
            RangeIndicatorHandler.IndicatorType.Special
        ];

        // Reused every frame so showing ranges doesn't allocate.
        private readonly HashSet<float> attackRanges = [];
        private readonly List<float> sortedRanges = [];

        public override void Update()
        {
            // Both singletons are absent outside a level (e.g. on the main menu).
            if (CommandUnits.instance == null || RangeIndicatorHandler.instance == null) return;

            foreach (var unit in CommandUnits.instance.PlayerUnitsCommanding)
            {
                attackRanges.Clear();

                foreach (var autoAttack in unit.autoAttacks)
                {
                    foreach (var targetPriority in autoAttack.targetPriorities)
                    {
                        attackRanges.Add(targetPriority.range);
                    }
                }

                sortedRanges.Clear();
                sortedRanges.AddRange(attackRanges);
                sortedRanges.Sort();

                for (int i = 0; i < Math.Min(sortedRanges.Count, orderedIndicatorTypes.Count); i++)
                {
                    RangeIndicatorHandler.instance.ShowIndicator(unit.transform.position, sortedRanges[i], unit.gameObject, orderedIndicatorTypes[i]);
                }

                if (sortedRanges.Count > orderedIndicatorTypes.Count)
                {
                    Plugin.Logger.LogWarning($"Unit {unit.name} has more than {orderedIndicatorTypes.Count} attack ranges: {string.Join(", ", sortedRanges)}");
                }
            }
        }

        [HarmonyPatch(typeof(CommandUnits), "OnUnitRemove")]
        private class CommandUnits_OnUnitRemove_Patch
        {
            static UnitRangeManager Instance => Plugin.Instance.GetManager<UnitRangeManager>();

            private static void Postfix(object __instance, PathfindMovementPlayerunit _p)
            {
                foreach (var indicatorType in Instance.orderedIndicatorTypes)
                {
                    RangeIndicatorHandler.instance.HideIndicator(_p.gameObject, indicatorType);
                }
            }
        }

        public override void OnDestroy()
        {
            if (CommandUnits.instance == null || RangeIndicatorHandler.instance == null) return;

            foreach (var unit in CommandUnits.instance.PlayerUnitsCommanding)
            {
                foreach (var indicatorType in orderedIndicatorTypes)
                {
                    RangeIndicatorHandler.instance.HideIndicator(unit.gameObject, indicatorType);
                }
            }
        }
    }
}
