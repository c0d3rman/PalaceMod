using System.Collections.Generic;
using UnityEngine;

/*
    Per-building gold income, worked out the way the game pays it.

    Every dawn (BuildingInteractor.OnDawn_AfterSunrise) each player building pays its BuildSlot.GoldIncome unless it was
    knocked out that night, and then its IncomeModifyers adjust GoldIncome for the following day:
      - MineShaft:           -incomeReductionPerTurn, floored at minimumIncome (minimumIncomeWithSustainableMining
                             with Sustainable Mining); a knocked-out mine with Sustainable Mining resets instead.
      - FishingHarbour:      +incomeIncreasePerTurn (one boat), capped at maximumIncome; skipped if knocked out.
                             Big Harbours and the harbour upgrade change those fields, so reading them covers both.
      - IncreaseIncomeDaily: +1 every dawn.
    Everything else (houses, castle, mills and their fields, shrines, perk bonuses on upgrades) is already folded into
    GoldIncome by the time it's read. Income not tied to a building (enemies, Interest, Treasure Hunter, Builder's
    Guild) is left to the game's own TFUIIncomeDisplay.

    "dawnsAhead" counts dawns after the next one: 0 is the coming dawn, 1 the one after, and so on. Knock-outs only
    count for the coming dawn and only at night: knockedOutTonight is stale during the day until dusk resets it.
*/
namespace PalaceMod
{
    public readonly struct BuildingIncome
    {
        public readonly BuildSlot Slot;
        /// <summary>Gold this building pays at the dawn asked for.</summary>
        public readonly int Income;
        /// <summary>What it would pay if it weren't knocked out (equal to Income unless KnockedOut).</summary>
        public readonly int IncomeIfStanding;
        /// <summary>Knocked out tonight, so it pays nothing at the coming dawn.</summary>
        public readonly bool KnockedOut;

        public BuildingIncome(BuildSlot slot, int income, int incomeIfStanding, bool knockedOut)
        {
            Slot = slot;
            Income = income;
            IncomeIfStanding = incomeIfStanding;
            KnockedOut = knockedOut;
        }
    }

    public static class IncomeCalculator
    {
        /// <summary>True during the night, when knock-outs decide whether buildings pay at the coming dawn.</summary>
        public static bool IsNight => DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        /// <summary>Gold <paramref name="slot"/> pays at the coming dawn (or <paramref name="dawnsAhead"/> dawns after it).
        /// 0 for unbuilt slots.</summary>
        public static int ForBuilding(BuildSlot slot, int dawnsAhead = 0) => Compute(slot, dawnsAhead).Income;

        /// <summary>Every built player building's income at the coming dawn (or later), one entry per building.
        /// Empty outside a level.</summary>
        public static List<BuildingIncome> All(int dawnsAhead = 0)
        {
            var result = new List<BuildingIncome>();
            var seen = new HashSet<BuildSlot>();
            var interactors = TagManager.instance?.playerBuildingInteractors;
            if (interactors == null) return result;
            foreach (var interactor in interactors)
            {
                if (interactor == null) continue;
                var slot = interactor.targetBuilding;
                if (slot == null || slot.State != BuildSlot.BuildingState.Built || !seen.Add(slot)) continue;
                result.Add(Compute(slot, dawnsAhead));
            }
            return result;
        }

        /// <summary>Total building income at the coming dawn (or later).</summary>
        public static int Total(int dawnsAhead = 0)
        {
            int total = 0;
            foreach (var b in All(dawnsAhead)) total += b.Income;
            return total;
        }

        /// <summary>Income for one building, with the knock-out details.</summary>
        public static BuildingIncome Compute(BuildSlot slot, int dawnsAhead = 0)
        {
            if (slot == null || slot.State != BuildSlot.BuildingState.Built) return new BuildingIncome(slot, 0, 0, false);
            var interactor = slot.Interactor != null ? slot.Interactor : slot.buildingInteractor;
            bool knockedOut = IsNight && interactor != null && interactor.KnockedOutTonight;
            int income = slot.GoldIncome;
            if (dawnsAhead <= 0)
                return new BuildingIncome(slot, knockedOut ? 0 : income, income, knockedOut);

            // Step through the dawns between now and the one asked for, applying each modifier after the payout.
            for (int dawn = 0; dawn < dawnsAhead; dawn++)
                income = AfterDawn(slot, interactor, income, dawn == 0 && knockedOut);
            return new BuildingIncome(slot, income, income, false);
        }

        private static int AfterDawn(BuildSlot slot, BuildingInteractor interactor, int income, bool knockedOut)
        {
            if (interactor == null) return income;
            foreach (var modifier in interactor.IncomeModifiers)
            {
                switch (modifier)
                {
                    case MineShaft mine:
                        bool sustainable = mine.sustainableMiningPerk != null && PerkManager.IsEquipped(mine.sustainableMiningPerk);
                        if (knockedOut)
                        {
                            if (sustainable) income = mine.resetIncomeSustainableMining;
                        }
                        else
                        {
                            income = Mathf.Max(income - mine.incomeReductionPerTurn,
                                sustainable ? mine.minimumIncomeWithSustainableMining : mine.minimumIncome);
                        }
                        break;
                    case FishingHarbour harbour:
                        if (slot.Level > 0 && !knockedOut)
                            income = Mathf.Min(income + harbour.incomeIncreasePerTurn, harbour.maximumIncome);
                        break;
                    case IncreaseIncomeDaily:
                        income++;
                        break;
                }
            }
            return income;
        }
    }
}
