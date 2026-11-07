using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

/*
    Perk numbers the planner needs for loadout what-ifs but the game throws away at level load: PerkCostModifyer,
    PerkIncomeModifyer, PerkUnrestrictedUpgrading, ReduceChildrensBuildRequirementIfPerk and InterestPerk destroy themselves once they've run (whether or not
    their perk is equipped), and EnemySpawner bakes Rat God and Loan into the waves' coins. EconomyPlannerManager's
    patches record them here as they run, read-only; EconomySnapshot reads them back. Values are the ones serialized in
    the level (never the code's defaults).
*/
namespace PalaceMod.Economy
{
    internal static class PerkCapture
    {
        internal sealed class SlotMod
        {
            public BuildSlot Slot;
            public Equippable Perk;
            public int[] Cost = [], Income = [];
            public bool[] CostSkip = [];
            public int? RootDifference;
            /// <summary>ReduceChildrensBuildRequirementIfPerk: the slots it unlocks a level sooner, and their levels before.</summary>
            public BuildSlot[] Children;
            public int[] ChildLevels;
            public bool Applied;
        }

        internal sealed class Interest
        {
            public Equippable Perk;
            public int Each, Max;
            public UnityEngine.SceneManagement.Scene Scene;
        }

        private static readonly List<SlotMod> slotMods = [];
        private static readonly Dictionary<BuildSlot, Equippable> neverDeny = [];

        /// <summary>The perk of the PerkRemoveDestroyedAtNightTag that ran on this slot (it destroys itself), or null.</summary>
        public static Equippable NeverDenyPerk(BuildSlot slot) => slot != null && neverDeny.TryGetValue(slot, out var p) ? p : null;

        public static void NeverDeny(PerkRemoveDestroyedAtNightTag c)
        {
            var slot = c.GetComponentInParent<BuildSlot>();
            var perk = Traverse.Create(c).Field("perkRequirement").GetValue<Equippable>();
            if (slot == null || perk == null) return;
            foreach (var dead in neverDeny.Keys.Where(k => k == null).ToList()) neverDeny.Remove(dead);
            neverDeny[slot] = perk;
        }
        private static Interest interest;
        private static Object spawnOwner;
        private static int[][] spawnGold;

        public static IEnumerable<SlotMod> SlotMods(UnityEngine.SceneManagement.Scene scene)
        {
            slotMods.RemoveAll(m => m.Slot == null);
            return slotMods.Where(m => m.Slot.gameObject.scene == scene);
        }

        public static Interest InterestFor(UnityEngine.SceneManagement.Scene scene) =>
            interest != null && interest.Scene == scene ? interest : null;

        public static int[][] SpawnGold(EnemySpawner spawner) => spawnOwner != null && spawnOwner == spawner ? spawnGold : null;

        // ---- Recorders (called from the patches) --------------------------------------------------------------

        public static void Cost(PerkCostModifyer c)
        {
            var slot = c.GetComponentInParent<BuildSlot>();
            if (slot == null) return;
            var upgrades = slot.Upgrades;
            slotMods.Add(new SlotMod
            {
                Slot = slot, Perk = c.requiredPerk, Cost = (int[])c.upgradeCostChange.Clone(),
                CostSkip = upgrades.Select(u => slot.UpgradesWithCostsLoadedFromSave.Contains(u)).ToArray(),
                Applied = PerkManager.IsEquipped(c.requiredPerk),
            });
        }

        public static void Income(PerkIncomeModifyer c)
        {
            if (Traverse.Create(c).Field("executed").GetValue<bool>()) return;
            var slot = c.GetComponent<BuildSlot>();
            if (slot == null) return;
            slotMods.Add(new SlotMod { Slot = slot, Perk = c.requiredPerk, Income = (int[])c.upgradeIncomeChange.Clone(), Applied = PerkManager.IsEquipped(c.requiredPerk) });
        }

        public static void Unrestricted(PerkUnrestrictedUpgrading c)
        {
            var t = Traverse.Create(c);
            var slot = t.Field("buildSlot").GetValue<BuildSlot>();
            var perk = t.Field("requiredEquippable").GetValue<Equippable>();
            if (slot == null) return;
            slotMods.Add(new SlotMod { Slot = slot, Perk = perk, RootDifference = slot.RequiredRootLevelDifference, Applied = PerkManager.IsEquipped(perk) });
        }

        public static void ReduceChildren(ReduceChildrensBuildRequirementIfPerk c)
        {
            var slot = c.GetComponent<BuildSlot>();
            if (slot == null || slotMods.Any(m => m.Slot == slot && m.Children != null)) return;
            var perk = Traverse.Create(c).Field("perk").GetValue<Equippable>();
            var children = slot.IsRootOf.Where(x => x != null && x.ActivatorBuilding == slot).ToArray();
            slotMods.Add(new SlotMod
            {
                Slot = slot, Perk = perk, Children = children, ChildLevels = children.Select(x => x.ActivatorLevel).ToArray(),
                Applied = perk != null && PerkManager.instance != null && PerkManager.instance.CurrentlyEquipped.Contains(perk),
            });
        }

        public static void InterestPerk(InterestPerk c)
        {
            var t = Traverse.Create(c);
            interest = new Interest
            {
                Scene = c.gameObject.scene, Perk = t.Field("requiredEquippable").GetValue<Equippable>(),
                Each = Mathf.Max(1, t.Field("interestForEach").GetValue<int>()), Max = t.Field("maximumInterest").GetValue<int>(),
            };
        }

        public static void Spawns(EnemySpawner spawner)
        {
            spawnOwner = spawner;
            spawnGold = spawner.waves.Select(w => w.spawns.Select(s => s.goldCoins).ToArray()).ToArray();
        }
    }
}
