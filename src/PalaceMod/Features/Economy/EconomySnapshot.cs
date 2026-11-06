using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Reads the live level into an EconWorld (EconomyModel.cs): every build slot of the map, built or not, with its
    upgrade tree, costs and income, plus the run's gold, nights and the perks that move gold.

    Slots that aren't unlocked yet haven't run some of their components, so their data still lacks what those will add
    once they do: PerkCostModifyer / PerkIncomeModifyer / PerkUnrestrictedUpgrading (Start) and Mill_ImprovedPlow
    (OnEnable) are applied here when they're still waiting to run, and a harbour that hasn't started gets its perk's
    extra boats. Each destroys itself after running, so "still there" means "still to run".

    Slots are named so that same-type ones can be told apart: numbered by distance from the castle, with the direction
    as seen on screen (north is up the screen) and a nearby landmark (a building there's only one or two of).
*/
namespace PalaceMod.Economy
{
    internal sealed class EconSnapshot
    {
        public EconWorld World;
        public readonly Dictionary<int, BuildSlot> Objects = [];
        public Scene Scene;
        /// <summary>Things the snapshot couldn't read exactly (for the dev check).</summary>
        public readonly List<string> Notes = [];
    }

    internal static class EconomySnapshot
    {
        private static readonly Log log = Log.For<EconomyPlannerManager>();

        /// <summary>True in a level the planner works in.</summary>
        public static bool Available =>
            PlayerInteraction.instance != null && EnemySpawner.instance != null && DayNightCycle.Instance != null && TagManager.instance != null
            && LocalGamestate.Instance != null && EnemySpawner.instance.WaveCount > 0;

        public static EconSnapshot Take()
        {
            if (!Available) return null;
            var snap = new EconSnapshot { Scene = EnemySpawner.instance.gameObject.scene };
            var w = new EconWorld { Level = snap.Scene.name };
            snap.World = w;

            var slots = Object.FindObjectsOfType<BuildSlot>(true).Where(s => s != null && s.gameObject.scene == snap.Scene).ToList();
            Vector3 castle = CastleCenter.instance != null ? CastleCenter.instance.transform.position : CastleCenter.CastleCenterPosition;
            Orientation(out var right, out var up);
            string Name(BuildSlot s) => Translate(s.LOCIDENTIFIER_NAME, s.buildingName);
            // Stable order: by type, then nearest the castle first.
            slots = slots.OrderBy(Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => Flat(s.transform.position - castle).sqrMagnitude).ThenBy(s => s.transform.position.x).ToList();
            var index = new Dictionary<BuildSlot, int>();
            for (int i = 0; i < slots.Count; i++) index[slots[i]] = i;
            // What pays at dawn is what IncomeCalculator (and the game) walk: the registered interactors' buildings.
            var registered = new HashSet<BuildSlot>(TagManager.instance.playerBuildingInteractors
                .Where(i => i != null && i.targetBuilding != null).Select(i => i.targetBuilding));

            foreach (var slot in slots)
            {
                var e = new EconSlot { Index = index[slot], TypeName = Name(slot) };
                snap.Objects[e.Index] = slot;
                var offset = Flat(slot.transform.position - castle);
                e.MapX = Vector3.Dot(offset, right);
                e.MapY = Vector3.Dot(offset, up);
                ReadSlot(slot, e, index, registered, snap);
                w.Slots.Add(e);
            }
            // After every slot is read: a follower's pending perk changes land on its owner's tree.
            foreach (var slot in slots)
                if (!slot.gameObject.activeInHierarchy) ApplyPending(slot, w.Slots[index[slot]], w, snap);
            // Ids must be unique for saved plans to find their slot.
            foreach (var group in w.Slots.GroupBy(s => s.Id).Where(g => g.Count() > 1))
                foreach (var s in group.Skip(1)) s.Id += $"#{s.Index}";
            Label(w);

            var player = PlayerInteraction.instance;
            var cycle = DayNightCycle.Instance;
            var spawner = EnemySpawner.instance;
            w.Gold = player.TrueBalance;
            w.LastNight = spawner.WaveCount;
            bool night = cycle.CurrentTimestate == DayNightCycle.Timestate.Night;
            w.FirstDay = spawner.Wavenumber + 2;
            w.DawnPending = night || !cycle.AfterSunrise;
            if (night) w.TonightGold = TonightGold(snap);
            w.WaveGold = Enumerable.Range(0, w.LastNight).Select(k => EnemySpawner.GetWaveInfoByNumber(k)?.goldReward ?? 0).ToArray();

            if (InterestPerk.instance != null)
            {
                var t = Traverse.Create(InterestPerk.instance);
                w.Interest = true;
                w.InterestEach = Mathf.Max(1, t.Field("interestForEach").GetValue<int>());
                w.InterestMax = t.Field("maximumInterest").GetValue<int>();
            }
            var perks = PerkManager.instance;
            if (perks != null && perks.TreasureHunterActive)
            {
                w.TreasureHunter = true;
                w.Treasure1 = perks.treasureHunterGoldAmountWave1;
                w.Treasure2 = perks.treasureHunterGoldAmountWave2;
                w.Treasure3 = perks.treasureHunterGoldAmountWave3;
            }

            var guild = UpgradeBuildersGuild.Instance;
            w.GuildActive = w.GuildUsed = guild != null && guild.isActiveAndEnabled && guild.gameObject.scene == snap.Scene;
            HouseOrder(w, index);
            var castleUp = UpgradeCastleUp.instance;
            w.CastleUpActive = castleUp != null && castleUp.isActiveAndEnabled && castleUp.gameObject.scene == snap.Scene;
            if (castleUp != null) CastleUpDiscounts(castleUp, w);
            return snap;
        }

        // ---- One slot ---------------------------------------------------------------------------------------------

        private static void ReadSlot(BuildSlot slot, EconSlot e, Dictionary<BuildSlot, int> index, HashSet<BuildSlot> registered, EconSnapshot snap)
        {
            var save = slot.GetComponent<SaveLoadEntity>();
            e.Id = save != null && !string.IsNullOrEmpty(save.GUID)
                ? save.GUID
                : $"{slot.buildingName}@{Mathf.RoundToInt(slot.transform.position.x)},{Mathf.RoundToInt(slot.transform.position.z)}";
            var t = Traverse.Create(slot);
            e.Level = t.Field("level").GetValue<int>(); // own level: Level is the activator's for slots that follow it
            e.Income = slot.GoldIncome;
            e.Active = slot.gameObject.activeInHierarchy;
            e.Applied = [.. slot.AppliedUpgrades];
            if (slot.ActivatorBuilding != null && index.TryGetValue(slot.ActivatorBuilding, out int act))
            {
                e.Activator = act;
                e.ActivatorLevel = slot.ActivatorLevel;
                if (slot.ActivatorUpgradesThis) e.Owner = act;
            }
            var root = slot;
            for (int guard = 0; root.ActivatorBuilding != null && guard < 16; guard++) root = root.ActivatorBuilding;
            e.Root = index.TryGetValue(root, out int r) ? r : e.Index;
            e.RootLevelDifference = slot.RequiredRootLevelDifference;

            var interactor = slot.Interactor != null ? slot.Interactor : slot.buildingInteractor;
            // A slot that isn't unlocked yet registers its interactor when it is.
            e.Pays = registered.Contains(slot) || (!slot.gameObject.activeInHierarchy && interactor != null && interactor.targetBuilding == slot);
            e.KnockedOut = interactor != null && interactor.KnockedOutTonight;
            e.HarvestedToday = interactor != null && Traverse.Create(interactor).Field("harvestedToday").GetValue<bool>();
            e.Busy = interactor != null && interactor.buildingIsCurrentlyBusyAndCantBeUpgraded;
            e.IsCastle = slot.GetComponentInChildren<CastleCenter>(true) != null;
            e.IsHouse = Owned<TaggedObject>(slot).Any(o => o.Tags.Contains(TagManager.ETag.House));
            // UpgradeCastleUp finds walls and towers through the tag manager (TaggedObject -> its parent BuildSlot).
            e.CastleUpTags = slot.GetComponentsInChildren<TaggedObject>(true)
                .Count(o => o != null && o.Tags.Contains(TagManager.ETag.WallOrTower) && o.GetComponentInParent<BuildSlot>(true) == slot);
            e.CastleUpReceivers = Owned<CaslteUpReciever>(slot, c => Traverse.Create(c).Field("buildSlot").GetValue<BuildSlot>()).Count();

            var upgrades = slot.OwnUpgrades;
            for (int i = 0; i < upgrades.Count; i++)
            {
                var u = upgrades[i];
                var step = new EconStep { Cost = u.cost, Disabled = u.disableInThisMode };
                string text = Translate(slot.GET_LOCIDENTIFIER_UPGRADE(i), "");
                step.Text = string.IsNullOrEmpty(text) ? null : Clean(text);
                bool choice = u.upgradeBranches.Count > 1;
                for (int b = 0; b < u.upgradeBranches.Count; b++)
                {
                    var branch = u.upgradeBranches[b];
                    var c = branch.choiceDetails;
                    step.Branches.Add(new EconBranch
                    {
                        Key = c != null && !string.IsNullOrEmpty(c.name) ? c.name : $"#{b}",
                        Name = c != null ? Translate(slot.GET_LOCIDENTIFIER_CHOICENAME(c), c.name) : $"Option {b + 1}",
                        Description = c != null ? Clean(Translate(slot.GET_LOCIDENTIFIER_CHOICEDESCRIPTION(c), c.tooltip)) : "",
                        IncomeChange = branch.goldIncomeChange,
                        Locked = choice && UpgradeInfo.IsLocked(c),
                        StartsGuild = Activates<UpgradeBuildersGuild>(branch),
                        StartsCastleUp = Activates<UpgradeCastleUp>(branch),
                        DoublesHarbour = Activates<FishingHarbourUpgrade>(branch),
                        BusyUntilDawn = Activates<TempleUpgrade>(branch),
                    });
                    var castleUp = ActivatedComponent<UpgradeCastleUp>(branch);
                    if (castleUp != null) CastleUpDiscounts(castleUp, snap.World);
                }
                e.Steps.Add(step);
            }
            ReadModifiers(slot, e);
            if (e.Modifiers.Any(m => m.Kind == EconModifierKind.Harbour) && upgrades.Count > 1 && upgrades[1].upgradeBranches.Count > 0)
                e.HarbourField = upgrades[1].upgradeBranches[0].goldIncomeChange;
        }

        private static void ReadModifiers(BuildSlot slot, EconSlot e)
        {
            var mine = slot.GetComponent<MineShaft>();
            if (mine != null)
            {
                e.Modifiers.Add(new EconModifier
                {
                    Kind = EconModifierKind.Mine, Step = mine.incomeReductionPerTurn, Min = mine.minimumIncome,
                    SustainableMin = mine.minimumIncomeWithSustainableMining, SustainableReset = mine.resetIncomeSustainableMining,
                    Sustainable = mine.sustainableMiningPerk != null && PerkManager.IsEquipped(mine.sustainableMiningPerk),
                });
            }
            foreach (var harbour in slot.GetComponentsInChildren<FishingHarbour>(true))
            {
                if (harbour.buildSlot != slot) continue;
                var t = Traverse.Create(harbour);
                int max = harbour.maximumIncome;
                // Start adds the perk's boats; a harbour that hasn't started yet (maximumBoats still 0) will.
                if (t.Field("maximumBoats").GetValue<int>() == 0)
                {
                    var perk = t.Field("perk").GetValue<Equippable>();
                    if (perk != null && PerkManager.instance != null && PerkManager.instance.CurrentlyEquipped.Contains(perk))
                        max += t.Field("additionalBoatCapacity").GetValue<int>();
                }
                e.Modifiers.Add(new EconModifier { Kind = EconModifierKind.Harbour, Step = harbour.incomeIncreasePerTurn, Max = max });
            }
            if (slot.GetComponent<IncreaseIncomeDaily>() != null) e.Modifiers.Add(new EconModifier { Kind = EconModifierKind.Daily });
        }

        // Components on a slot that's not unlocked yet that will change its data when it is. They work on
        // BuildSlot.Upgrades, which for a slot following its owner's tree is the owner's: that's where they land.
        private static void ApplyPending(BuildSlot slot, EconSlot e, EconWorld w, EconSnapshot snap)
        {
            var tree = e.Owner >= 0 ? w.Slots[e.Owner] : e;
            var upgrades = slot.Upgrades;
            foreach (var c in Owned<PerkCostModifyer>(slot).Where(c => WillRun(c, slot)))
            {
                if (!PerkManager.IsEquipped(c.requiredPerk)) continue;
                // Steps already taken don't matter any more.
                for (int i = tree.Level; i < upgrades.Count && i < tree.Steps.Count && i < c.upgradeCostChange.Length; i++)
                    if (!slot.UpgradesWithCostsLoadedFromSave.Contains(upgrades[i])) tree.Steps[i].Cost += c.upgradeCostChange[i];
                snap.Notes.Add($"{e.TypeName} #{e.Index}: perk cost change still to apply (to #{tree.Index})");
            }
            foreach (var c in slot.GetComponents<PerkIncomeModifyer>().Where(c => WillRun(c, slot)))
            {
                if (!PerkManager.IsEquipped(c.requiredPerk)) continue;
                for (int i = tree.Level; i < tree.Steps.Count && i < c.upgradeIncomeChange.Length; i++)
                    foreach (var b in tree.Steps[i].Branches) b.IncomeChange += c.upgradeIncomeChange[i];
                snap.Notes.Add($"{e.TypeName} #{e.Index}: perk income change still to apply (to #{tree.Index})");
            }
            foreach (var c in Owned<PerkUnrestrictedUpgrading>(slot, c => Traverse.Create(c).Field("buildSlot").GetValue<BuildSlot>()).Where(c => WillRun(c, slot)))
            {
                if (PerkManager.IsEquipped(Traverse.Create(c).Field("requiredEquippable").GetValue<Equippable>())) e.RootLevelDifference = -100;
            }
            foreach (var plow in Owned<Mill_ImprovedPlow>(slot, c => Traverse.Create(c).Field("buildSlot").GetValue<BuildSlot>()).Where(c => WillRun(c, slot)))
            {
                for (int i = Math.Max(1, tree.Level); i <= 2 && i < tree.Steps.Count; i++)
                    foreach (var b in tree.Steps[i].Branches) b.IncomeChange++;
                snap.Notes.Add($"{e.TypeName} #{e.Index}: Improved Plow still to apply (to #{tree.Index})");
            }
        }

        // Active itself and on every parent up to the slot, so it runs when the slot is activated.
        private static bool WillRun(Component c, BuildSlot slot)
        {
            if (c == null) return false;
            for (var t = c.transform; t != null && t != slot.transform; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        /// <summary>Components under the slot that belong to it (not to a slot nested inside it).</summary>
        private static IEnumerable<T> Owned<T>(BuildSlot slot, Func<T, BuildSlot> target = null) where T : Component =>
            slot.GetComponentsInChildren<T>(true).Where(c => c != null && (target != null ? target(c) == slot : c.GetComponentInParent<BuildSlot>(true) == slot));

        private static bool Activates<T>(BuildSlot.UpgradeBranch branch) where T : Component => ActivatedComponent<T>(branch) != null;

        private static T ActivatedComponent<T>(BuildSlot.UpgradeBranch branch) where T : Component
        {
            foreach (var go in branch.objectsToActivate)
            {
                if (go == null) continue;
                var c = go.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }

        private static void CastleUpDiscounts(UpgradeCastleUp c, EconWorld w)
        {
            var t = Traverse.Create(c);
            w.CastleUpDiscount2 = t.Field("immediateWallAndTowerDiscount2").GetValue<int>();
            w.CastleUpDiscount3 = t.Field("immediateWallAndTowerDiscount3").GetValue<int>();
        }

        // UpgradeBuildersGuild looks at houses in the tag manager's order (it sorts a copy by distance, then reads the
        // unsorted list).
        private static void HouseOrder(EconWorld w, Dictionary<BuildSlot, int> index)
        {
            var found = new List<TaggedObject>();
            TagManager.instance.FindAllTaggedObjectsWithTags(found, [TagManager.ETag.House], []);
            foreach (var o in found)
            {
                var slot = o != null ? o.GetComponentInParent<BuildSlot>() : null;
                if (slot != null && index.TryGetValue(slot, out int i) && !w.HouseOrder.Contains(i)) w.HouseOrder.Add(i);
            }
            foreach (var s in w.Slots)
                if (s.IsHouse && s.Level >= 1 && !w.HouseOrder.Contains(s.Index)) w.HouseOrder.Add(s.Index);
        }

        // ---- Tonight's gold ---------------------------------------------------------------------------------------

        /// <summary>Enemy gold still to come tonight: coins of enemies not spawned yet, plus those carried by living
        /// enemies (coins already dropped are in TrueBalance).</summary>
        private static int TonightGold(EconSnapshot snap)
        {
            var spawner = EnemySpawner.instance;
            int full = EnemySpawner.GetWaveInfoByNumber(spawner.Wavenumber)?.goldReward ?? 0;
            try
            {
                int total = 0;
                var wave = spawner.waves[Mathf.Clamp(spawner.Wavenumber, 0, spawner.waves.Count - 1)];
                foreach (var spawn in wave.spawns)
                {
                    var t = Traverse.Create(spawn);
                    var coins = t.Field("goldCoinsPerEnemy").GetValue<int[]>();
                    int spawned = t.Field("spawnedUnits").GetValue<int>();
                    if (coins == null) { total += spawned == 0 ? spawn.goldCoins : 0; continue; }
                    for (int i = Mathf.Max(0, spawned); i < coins.Length; i++) total += coins[i];
                }
                var enemies = new List<TaggedObject>();
                TagManager.instance.FindAllTaggedObjectsWithTag(enemies, TagManager.ETag.EnemyOwned);
                var seen = new HashSet<Hp>();
                foreach (var o in enemies)
                {
                    if (o == null) continue;
                    var hp = o.GetComponent<Hp>() ?? o.GetComponentInParent<Hp>() ?? o.GetComponentInChildren<Hp>();
                    if (hp != null && hp.HpValue > 0f && seen.Add(hp)) total += hp.coinCount;
                }
                return total;
            }
            catch (Exception ex)
            {
                snap.Notes.Add($"Tonight's remaining enemy gold unreadable ({ex.Message}); assumed the whole wave's {full}");
                log.Warn($"Couldn't read tonight's remaining enemy gold: {ex.Message}");
                return spawner.SpawningInProgress || spawner.NumberOfEnemiesOnTheMap > 0 ? full : 0;
            }
        }

        // ---- Names ------------------------------------------------------------------------------------------------

        private static readonly string[] Directions = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];

        private static void Label(EconWorld w)
        {
            var pickable = w.Slots.Where(s => !s.IsDependent && s.Steps.Count > 0).ToList();
            var counts = pickable.GroupBy(s => s.TypeName).ToDictionary(g => g.Key, g => g.Count());
            float far = pickable.Count > 0 ? pickable.Max(s => Dist(s)) : 1f;
            var landmarks = pickable.Where(s => counts[s.TypeName] <= 2 && !s.IsWallOrTower && !s.IsCastle).ToList();
            foreach (var group in w.Slots.GroupBy(s => s.TypeName))
            {
                int n = 0;
                foreach (var s in group.OrderBy(Dist))
                {
                    if (s.IsDependent || s.Steps.Count == 0) { s.Label = s.TypeName; continue; }
                    n++;
                    if (counts[s.TypeName] == 1) { s.Label = s.TypeName; continue; }
                    string where = Dist(s) < far * 0.12f ? "by the castle" : Directions[Sector(s)];
                    var near = landmarks.Where(l => l.TypeName != s.TypeName)
                        .OrderBy(l => Sq(l.MapX - s.MapX) + Sq(l.MapY - s.MapY)).FirstOrDefault();
                    if (near != null && Math.Sqrt(Sq(near.MapX - s.MapX) + Sq(near.MapY - s.MapY)) < far * 0.22f && where != "by the castle")
                        where += $", by the {near.TypeName}";
                    s.Label = $"{s.TypeName} {n} · {where}";
                }
            }
        }

        private static float Dist(EconSlot s) => (float)Math.Sqrt(Sq(s.MapX) + Sq(s.MapY));
        private static float Sq(float v) => v * v;

        private static int Sector(EconSlot s)
        {
            double angle = Math.Atan2(s.MapX, s.MapY) * 180.0 / Math.PI; // 0 = up the screen, clockwise
            return (int)Math.Round((angle + 360.0) % 360.0 / 45.0) % 8;
        }

        // The camera's ground-plane axes, so the map and directions match the screen.
        private static void Orientation(out Vector3 right, out Vector3 up)
        {
            right = Vector3.right;
            up = Vector3.forward;
            var cam = Camera.main;
            if (cam == null) return;
            var f = Flat(cam.transform.forward);
            var r = Flat(cam.transform.right);
            if (f.sqrMagnitude < 1e-4f || r.sqrMagnitude < 1e-4f) return;
            up = f.normalized;
            right = r.normalized;
        }

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        internal static string Translate(string key, string fallback)
        {
            string text = TextTranslator.Translate(key);
            return string.IsNullOrEmpty(text) || text == key ? fallback : text;
        }

        // The game's texts carry tooltip markup; the planner shows them plain.
        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new System.Text.StringBuilder(text.Length);
            bool tag = false;
            foreach (char ch in text)
            {
                if (ch == '<') { tag = true; continue; }
                if (ch == '>') { tag = false; continue; }
                if (!tag) sb.Append(ch);
            }
            return sb.ToString().Replace('\n', ' ').Trim();
        }
    }
}
