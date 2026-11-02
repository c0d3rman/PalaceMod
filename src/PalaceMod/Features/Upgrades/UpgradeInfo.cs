using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    Read-only view of a building's upgrade tree: which branch was taken at each level, what the next level offers and
    what later levels hold. Never changes upgrade state (the undo-upgrade feature owns that).

    BuildSlot.upgrades[0] is the build itself; upgrades[i] takes the building from level i to i+1, and
    AppliedUpgrades[i] is the branch index picked at that step. A slot with ActivatorUpgradesThis (e.g. a wall segment
    tied to a tower) shares its activator's tree, so everything here resolves to the owning slot first.
*/
namespace PalaceMod
{
    internal static class UpgradeInfo
    {
        internal struct Step
        {
            public int Level;                    // the level this step reaches (2 = first upgrade after building)
            public BuildSlot.Upgrade Upgrade;
            public int BranchIndex;              // taken branch, or -1 for steps not taken yet
            public bool IsChoice => Upgrade.upgradeBranches.Count > 1;
            public Choice TakenChoice => BranchIndex >= 0 && BranchIndex < Upgrade.upgradeBranches.Count
                ? Upgrade.upgradeBranches[BranchIndex].choiceDetails : null;
        }

        /// <summary>The slot whose upgrade tree this slot uses.</summary>
        internal static BuildSlot Owner(BuildSlot slot)
        {
            int guard = 0;
            while (slot != null && slot.ActivatorUpgradesThis && slot.ActivatorBuilding != null && guard++ < 8)
                slot = slot.ActivatorBuilding;
            return slot;
        }

        internal static bool IsBuilt(BuildSlot slot) => slot != null && Owner(slot).Level >= 1;

        /// <summary>Steps already applied, oldest first. Level 1 is the build itself, which can be a choice too (e.g. a
        /// barracks' Knights or Spearmen).</summary>
        internal static List<Step> Taken(BuildSlot slot)
        {
            var steps = new List<Step>();
            slot = Owner(slot);
            if (slot == null) return steps;
            var upgrades = slot.OwnUpgrades;
            var applied = slot.AppliedUpgrades;
            int n = Mathf.Min(Mathf.Min(applied.Count, upgrades.Count), slot.Level);
            for (int i = 0; i < n; i++)
                steps.Add(new Step { Level = i + 1, Upgrade = upgrades[i], BranchIndex = applied[i] });
            return steps;
        }

        /// <summary>Choices the player picked (multi-branch steps only), oldest first.</summary>
        internal static List<(Step step, Choice choice)> TakenChoices(BuildSlot slot) =>
            Taken(slot).Where(s => s.IsChoice && s.TakenChoice != null).Select(s => (s, s.TakenChoice)).ToList();

        /// <summary>The next upgrade step, if the building can be upgraded right now.</summary>
        internal static Step? Next(BuildSlot slot)
        {
            slot = Owner(slot);
            if (slot == null || !slot.CanBeUpgraded || slot.Level >= slot.OwnUpgrades.Count) return null;
            return new Step { Level = slot.Level + 1, Upgrade = slot.OwnUpgrades[slot.Level], BranchIndex = -1 };
        }

        /// <summary>Steps after the next one that this mode allows, in order. Includes ones the castle level blocks.</summary>
        internal static List<Step> Future(BuildSlot slot)
        {
            var steps = new List<Step>();
            slot = Owner(slot);
            if (slot == null) return steps;
            var upgrades = slot.OwnUpgrades;
            for (int i = slot.Level + 1; i < upgrades.Count; i++)
            {
                if (upgrades[i].disableInThisMode) break;
                steps.Add(new Step { Level = i + 1, Upgrade = upgrades[i], BranchIndex = -1 });
            }
            return steps;
        }

        /// <summary>Upgrades remaining in this mode, counting the next one, even if blocked by the castle level.</summary>
        internal static bool HasRemaining(BuildSlot slot)
        {
            slot = Owner(slot);
            return slot != null && slot.Level < slot.OwnUpgrades.Count && !slot.OwnUpgrades[slot.Level].disableInThisMode;
        }

        internal static string BuildingName(BuildSlot slot)
        {
            slot = Owner(slot);
            return Translate(slot.LOCIDENTIFIER_NAME, slot.buildingName);
        }

        internal static string ChoiceName(BuildSlot slot, Choice choice) =>
            choice == null ? "" : Translate(Owner(slot).GET_LOCIDENTIFIER_CHOICENAME(choice), choice.name);

        internal static string ChoiceDescription(BuildSlot slot, Choice choice) =>
            choice == null ? "" : Translate(Owner(slot).GET_LOCIDENTIFIER_CHOICEDESCRIPTION(choice), choice.tooltip);

        /// <summary>The game's text for a single-branch step ("+ more health" etc.); the level number when it has none.</summary>
        internal static string StepText(BuildSlot slot, Step step)
        {
            if (step.IsChoice && step.TakenChoice != null) return ChoiceName(slot, step.TakenChoice);
            return Translate(Owner(slot).GET_LOCIDENTIFIER_UPGRADE(step.Level - 1), "");
        }

        /// <summary>Whether a branch can't be picked in this run (locked behind an unlock or disabled by the mode).</summary>
        internal static bool IsLocked(Choice choice) =>
            choice != null && (choice.disabledInThisMode || !SafeCanBePicked(choice));

        private static bool SafeCanBePicked(Choice choice)
        {
            if (LevelProgressManager.instance == null) return choice.requiresUnlocked == null || choice.requiresUnlocked.IsUnlocked;
            return choice.CanBePicked;
        }

        internal static Sprite Icon(Choice choice)
        {
            if (choice == null) return null;
            if (SettingsManager.Instance != null && SettingsManager.Instance.ArachnophobiaMode && choice.hasArachnophobiaMode && choice.altIcon != null)
                return choice.altIcon;
            return choice.icon;
        }

        /// <summary>All built slots that own an upgrade tree, in a stable order.</summary>
        internal static List<BuildSlot> AllBuilt()
        {
            return Object.FindObjectsOfType<BuildSlot>()
                .Where(s => s != null && !s.ActivatorUpgradesThis && s.Level >= 1 && s.OwnUpgrades.Count > 0)
                .OrderBy(s => BuildingName(s)).ThenBy(s => s.transform.position.x).ThenBy(s => s.transform.position.z)
                .ToList();
        }

        private static string Translate(string key, string fallback)
        {
            string text = TextTranslator.Translate(key);
            if (string.IsNullOrEmpty(text) || text == key) return fallback;
            return text;
        }
    }
}
