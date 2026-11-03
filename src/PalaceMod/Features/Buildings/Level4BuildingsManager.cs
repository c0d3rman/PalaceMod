using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Unlocks every building's hidden top upgrade tier.

    Each BuildSlot in a level scene carries its full upgrade list, including a final tier that the game ships but
    marks Upgrade.disableInThisMode on every map (game 2.13): level 4 for the castle center (Empire upgrades),
    defense towers (Insane Damage/Range/Health, Hunting Equipment), barracks, archery ranges and hero's quarters,
    and level 3 for houses and walls. Totend MM1 is the one map that ships with the flag cleared; its tier data
    (costs, meshes, HP, income, choices) is identical to every other map's, and the effect scripts are present on
    every prefab, so unlocking is just clearing that flag. Only a slot's last tier is unlocked, and only when it
    is the slot's sole disabled tier, so a map that caps a building lower on purpose keeps that cap. The usual
    castle-level requirement still applies (a level-4 tower needs a level-4 castle).

    Applied when a level loads (SceneManager.sceneLoaded fires after Awake/OnEnable and before Start, so the
    interactors see the unlocked tiers from their first update). Toggling the setting mid-level also applies or
    re-locks tiers that haven't been bought. Buying an unlocked tier, or loading a save that has one, marks the
    run's GameplayImpact.
*/
namespace PalaceMod
{
    public class Level4BuildingsManager : BaseManager
    {
        public override string FeatureSection => "Level 4 buildings";
        public override bool AffectsRanking => true; // off while "Gameplay-changing features" is off
        public override string FeatureDescription =>
            "Unlocks the game's hidden top building tier on every map: level 4 castle, towers and unit buildings, level 3 houses and walls. Applies when a level loads.";
        protected override bool EnabledByDefault => false;

        // Patches stay applied so a save with an unlocked tier still marks gameplay impact while the feature is off.
        protected override bool UnpatchWhenDisabled => false;

        private const string ImpactReason = "Level 4 buildings";

        // Tiers this manager unlocked in the current level (slot + upgrade index). Slots die with their scene.
        private static readonly List<(BuildSlot slot, int index)> unlocked = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            SceneManager.sceneLoaded += OnSceneLoaded;
            DevCommands.Register("l4", "l4 [dump|max [vanilla]|goto <building>|offer <building>|save]   level-4 buildings: list unlocked tiers, dump slot upgrades, upgrade built slots to max (free), move the king to / open the upgrade choice of a building whose next level is an unlocked tier", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                string building = string.Join(" ", args.Skip(1));
                return sub switch
                {
                    "dump" => Dump(),
                    "max" => UpgradeAllToMax(building == "vanilla"),
                    "goto" => GoTo(building),
                    "offer" => Offer(building),
                    "save" => SaveMatch(),
                    _ => Describe(),
                };
            });
        }

        public override void OnDestroy() => SceneManager.sceneLoaded -= OnSceneLoaded;

        protected override void OnFeatureEnabled() => Unlock();

        protected override void OnFeatureDisabled() => Relock();

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            unlocked.RemoveAll(u => u.slot == null);
            if (Feature<Level4BuildingsManager>.On) Unlock();
        }

        // The index of the slot's hidden top tier, or -1 if it has none we can safely unlock.
        private static int HiddenTopTier(BuildSlot slot)
        {
            var upgrades = slot.OwnUpgrades;
            int last = upgrades.Count - 1;
            if (last < 1 || !upgrades[last].disableInThisMode) return -1;
            for (int i = 0; i < last; i++)
                if (upgrades[i].disableInThisMode) return -1;   // the map caps this building lower on purpose
            var branches = upgrades[last].upgradeBranches;
            if (branches == null || branches.Count == 0 || branches.Any(b => b == null || b.choiceDetails == null)) return -1;
            return last;
        }

        private static void Unlock()
        {
            var slots = UnityEngine.Object.FindObjectsOfType<BuildSlot>(true);
            var tiers = new Dictionary<BuildSlot, int>();
            foreach (var slot in slots)
            {
                int tier = HiddenTopTier(slot);
                if (tier >= 0) tiers[slot] = tier;
            }
            // A slot upgraded by its activator (wall segments) indexes its own list at the activator's level, so the
            // activator may only gain a tier if every such dependant gains the same one.
            foreach (var slot in slots)
            {
                var activator = slot.ActivatorBuilding;
                if (!slot.ActivatorUpgradesThis || activator == null || !tiers.TryGetValue(activator, out int tier)) continue;
                if (!tiers.TryGetValue(slot, out int own) || own != tier) tiers.Remove(activator);
            }
            int count = 0;
            foreach (var pair in tiers)
            {
                pair.Key.OwnUpgrades[pair.Value].disableInThisMode = false;
                unlocked.Add((pair.Key, pair.Value));
                count++;
            }
            if (count == 0) return;
            Log.For<Level4BuildingsManager>().Info($"Unlocked the top tier of {count} build slots in {SceneManager.GetActiveScene().name}");
            RefreshInteractors();
        }

        // Puts back the flag on tiers that weren't bought, so turning the feature off mid-level restores the cap.
        private static void Relock()
        {
            int count = 0;
            foreach (var (slot, index) in unlocked)
            {
                if (slot == null || index >= slot.OwnUpgrades.Count) continue;
                if (slot.Level <= index)
                {
                    slot.OwnUpgrades[index].disableInThisMode = true;
                    count++;
                }
            }
            unlocked.Clear();
            if (count > 0) RefreshInteractors();
        }

        private static void RefreshInteractors()
        {
            foreach (var interactor in UnityEngine.Object.FindObjectsOfType<BuildingInteractor>())
                interactor.UpdateInteractionState();
            if (TooltipManager.instance != null) TooltipManager.instance.SetInteractorRefreshFlag();
        }

        private static bool IsUnlockedTier(BuildSlot slot, int index)
        {
            foreach (var u in unlocked)
                if (u.slot == slot && u.index == index) return true;
            return false;
        }

        // After a slot gains a level, mark the run if that level came from an unlocked tier.
        private static void AfterLevelGained(BuildSlot slot)
        {
            int index = Traverse.Create(slot).Field("level").GetValue<int>() - 1;
            if (index >= 0 && IsUnlockedTier(slot, index)) GameplayImpact.Mark(ImpactReason);
        }

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnUpgradeChoiceComplete))]
        private class BuildSlot_OnUpgradeChoiceComplete_Patch
        {
            private static void Postfix(BuildSlot __instance, Choice _choiceMade)
            {
                if (_choiceMade != null) AfterLevelGained(__instance);
            }
        }

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.ForceManualUpgradeWithoutFeedbackEffects))]
        private class BuildSlot_ForceManualUpgrade_Patch
        {
            private static void Postfix(BuildSlot __instance, BuildSlot.UpgradeBranch upgBranch)
            {
                if (upgBranch != null) AfterLevelGained(__instance);
            }
        }

        // A continued match keeps its bought tiers; the impact record lives in memory only, so re-mark it on load.
        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnLoad))]
        private class BuildSlot_OnLoad_Patch
        {
            private static void Postfix(BuildSlot __instance)
            {
                int level = Traverse.Create(__instance).Field("level").GetValue<int>();
                var upgrades = __instance.OwnUpgrades;
                if (level < 1 || level > upgrades.Count) return;
                int index = level - 1;
                if (upgrades[index].disableInThisMode || IsUnlockedTier(__instance, index)) GameplayImpact.Mark(ImpactReason);
            }
        }

        // --- Dev commands ---

        private static string Describe()
        {
            unlocked.RemoveAll(u => u.slot == null);
            var sb = new StringBuilder();
            sb.AppendLine($"enabled={Feature<Level4BuildingsManager>.On} unlocked={unlocked.Count} in {SceneManager.GetActiveScene().name}");
            foreach (var group in unlocked.GroupBy(u => (u.slot.buildingName, u.index)))
            {
                var bought = group.Count(u => u.slot.Level > u.index);
                sb.AppendLine($"  {group.Key.buildingName}: level {group.Key.index + 1} unlocked on {group.Count()} slots, bought on {bought}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string UpgradeAllToMax(bool vanillaOnly)
        {
            var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
            int upgraded = 0;
            // Several passes: the castle has to rise before the buildings that depend on its level can follow.
            for (int pass = 0; pass < 6; pass++)
            {
                foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
                {
                    if (slot.Level == 0 || slot.ActivatorUpgradesThis || !slot.gameObject.activeInHierarchy) continue;
                    while (slot.CanBeUpgraded && !ChoiceManager.instance.ChoiceCoroutineRunning && !(vanillaOnly && IsUnlockedTier(slot, slot.Level)))
                    {
                        int before = slot.Level;
                        slot.TryToBuildOrUpgradeAndPay(player, false);
                        if (slot.Level == before) break;
                        upgraded++;
                    }
                }
            }
            if (upgraded > 0) GameplayImpact.Mark("Dev: free upgrades");
            return $"upgraded {upgraded} levels";
        }

        // A built slot of that building whose next upgrade is a tier this manager unlocked.
        private static BuildSlot NextUnlocked(string building)
        {
            unlocked.RemoveAll(u => u.slot == null);
            foreach (var (slot, index) in unlocked)
                if (slot.Level == index && slot.CanBeUpgraded && !slot.ActivatorUpgradesThis && slot.gameObject.activeInHierarchy &&
                    slot.buildingName.StartsWith(building, StringComparison.OrdinalIgnoreCase))
                    return slot;
            throw new InvalidOperationException($"no '{building}' ready for its unlocked tier (try 'l4 max')");
        }

        private static string GoTo(string building)
        {
            var slot = NextUnlocked(building);
            var movement = UnityEngine.Object.FindObjectOfType<PlayerMovement>() ?? throw new InvalidOperationException("no player");
            var pos = slot.transform.position;
            movement.TeleportTo(pos + new Vector3(0f, 0f, -4f));
            return $"king at {slot.buildingName} [{slot.name}] {pos}";
        }

        private static string Offer(string building)
        {
            var slot = NextUnlocked(building);
            var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
            string what = $"level {slot.Level + 1} of {slot.buildingName} [{slot.name}] (cost {slot.NextUpgradeOrBuildCost})";
            slot.TryToBuildOrUpgradeAndPay(player, true);
            return slot.NextUpgradeIsChoice || ChoiceManager.instance.ChoiceCoroutineRunning ? $"offered {what}" : $"upgraded to {what}";
        }

        private static string SaveMatch()
        {
            var saver = UnityEngine.Object.FindObjectOfType<LocalMatchSaveLoad>() ?? throw new InvalidOperationException("not in a level");
            AccessTools.Method(typeof(LocalMatchSaveLoad), "Save").Invoke(saver, null);
            return "match saved";
        }

        private static string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"scene {SceneManager.GetActiveScene().name}");
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>(true).OrderBy(s => s.buildingName))
            {
                sb.Append($"{slot.buildingName} [{slot.name}] lvl={slot.Level} n={slot.OwnUpgrades.Count} rootDiff={slot.RequiredRootLevelDifference} actUp={slot.ActivatorUpgradesThis} canUp={slot.CanBeUpgraded}:");
                for (int i = 0; i < slot.OwnUpgrades.Count; i++)
                {
                    var u = slot.OwnUpgrades[i];
                    sb.Append($" | {i}: c{u.cost}{(u.disableInThisMode ? " DIS" : "")} ");
                    sb.Append(string.Join("/", u.upgradeBranches.Select(b =>
                        $"{(b.choiceDetails != null ? b.choiceDetails.name : "-")}(m={(b.replacementMesh ? b.replacementMesh.name : "-")},g{b.goldIncomeChange},hp{b.hpChange})")));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
