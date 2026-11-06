using System.Collections.Generic;
using System.Linq;
using PalaceMod.Economy;

namespace PalaceMod.Tests
{
    /// <summary>The economy planner's simulation (EconomyModel.cs) on small hand-built maps.</summary>
    public static class EconomyTests
    {
        // ---- Map building helpers ----------------------------------------------------------------------------

        private static EconStep Step(int cost, params int[] incomes) => new()
        {
            Cost = cost,
            Branches = (incomes.Length == 0 ? new[] { 0 } : incomes).Select((inc, i) => new EconBranch { Key = $"b{i}", Name = $"Branch {i}", IncomeChange = inc }).ToList(),
        };

        private static EconSlot Slot(EconWorld w, string type, int level, int income, params EconStep[] steps)
        {
            var s = new EconSlot
            {
                Index = w.Slots.Count, Id = $"{type}{w.Slots.Count}", TypeName = type, Label = $"{type} {w.Slots.Count}",
                Level = level, Income = income, Active = true, Pays = true, Steps = steps.ToList(),
            };
            s.Root = w.Slots.Count > 0 ? 0 : s.Index;
            w.Slots.Add(s);
            return s;
        }

        /// <summary>Day 1 of a 5-night level: a castle at level 1, 10 gold, 3 enemy gold per night.</summary>
        private static EconWorld World(int nights = 5, int gold = 10)
        {
            var w = new EconWorld { Level = "Test", Gold = gold, FirstDay = 1, LastNight = nights, WaveGold = Enumerable.Repeat(3, nights).ToArray() };
            var castle = Slot(w, "Castle", 1, 1, Step(0, 1), Step(10), Step(20));
            castle.IsCastle = true;
            castle.Root = 0;
            return w;
        }

        private static EconAction Do(int day, EconSlot slot, int level, string choice = null) => new() { Day = day, Slot = slot.Id, Level = level, Choice = choice };

        // ---- Tests --------------------------------------------------------------------------------------------

        public static void DoingNothingAddsUpEachDay()
        {
            var w = World();
            var r = EconSim.Run(w, []);
            Assert.Equal(5, r.Days.Count, "days");
            // Day 1: 10, +3 drops, +1 castle = 14; each day after +4.
            Assert.Equal(10, r.Days[0].Start, "day 1 start");
            Assert.Equal(14, r.Days[0].AtDawn, "dawn 1");
            Assert.Equal(30, r.EndGold, "end");
            Assert.Equal(1, r.NextDawnBuildings, "next dawn");
        }

        public static void BuildPaysFromThatDawnOn()
        {
            var w = World();
            var house = Slot(w, "House", 0, 0, Step(5, 2), Step(8, 1));
            var r = EconSim.Run(w, [Do(2, house, 1)]);
            var a = r.Actions.Single();
            Assert.Equal(ActionStatus.Planned, a.Status, "status");
            Assert.Equal(5, a.Cost, "cost");
            Assert.Equal(2, a.IncomeDelta, "income delta");
            Assert.Equal(5, r.Days[1].Spent, "spent day 2");
            Assert.Equal(1 + 2, r.Days[1].Buildings, "pays at dawn 2");
            Assert.Equal(1, r.Days[0].Buildings, "not before");
            // 30 - 5 + 2 * 4 dawns (2..5)
            Assert.Equal(33, r.EndGold, "end gold");
            Assert.Equal(4, a.PaybackDawn, "pays back by dawn 4 (2+2+2)");
        }

        public static void OverspendingIsFlaggedAndCarriesOn()
        {
            var w = World(gold: 3);
            var house = Slot(w, "House", 0, 0, Step(5, 1));
            var r = EconSim.Run(w, [Do(1, house, 1)]);
            var a = r.Actions.Single();
            Assert.Equal(ActionStatus.Short, a.Status, "short");
            Assert.Equal(2, a.Shortfall, "shortfall");
            Assert.Equal(2, r.Days[0].Shortfall, "day shortfall");
            Assert.Equal(-2, r.Days[0].AfterSpend, "negative after spending");
            Assert.Equal(1, r.FirstShortDay.Day, "first short day");
        }

        public static void InterestUsesGoldAfterSpending()
        {
            var w = World(gold: 30);
            w.Interest = true;
            w.InterestEach = 3;
            w.InterestMax = 5;
            var house = Slot(w, "House", 0, 0, Step(24, 1));
            var r = EconSim.Run(w, [Do(1, house, 1)]);
            Assert.Equal(2, r.Days[0].Interest, "ceil(6 / 3)");
            var none = EconSim.Run(w, []);
            Assert.Equal(5, none.Days[0].Interest, "capped at 5");
        }

        public static void StepsNeedTheLevelBeforeAndTheCastle()
        {
            var w = World();
            var tower = Slot(w, "Tower", 0, 0, Step(3), Step(4), Step(6));
            tower.RootLevelDifference = 0; // can't be higher than the castle
            var r = EconSim.Run(w, [Do(1, tower, 2), Do(2, tower, 1), Do(3, tower, 2)]);
            Assert.Equal(ActionStatus.Blocked, r.Actions[0].Status, "level 2 before building");
            Assert.Equal(ActionStatus.Planned, r.Actions[1].Status, "build");
            Assert.Equal(ActionStatus.Blocked, r.Actions[2].Status, "castle too low");
            Assert.True(r.Actions[2].Reason.Contains("Castle level 2"), r.Actions[2].Reason);

            // Upgrading the castle first, earlier the same day, unblocks it; the other order doesn't.
            var castle = w.Slots[0];
            var ok = EconSim.Run(w, [Do(1, tower, 1), Do(3, castle, 2), Do(3, tower, 2)]);
            Assert.Equal(ActionStatus.Planned, ok.Actions[2].Status, "castle first");
            var bad = EconSim.Run(w, [Do(1, tower, 1), Do(3, tower, 2), Do(3, castle, 2)]);
            Assert.Equal(ActionStatus.Blocked, bad.Actions[1].Status, "tower first");
        }

        public static void LockedSlotsUnlockWithTheirActivator()
        {
            var w = World(gold: 100);
            var castle = w.Slots[0];
            var far = Slot(w, "Farm", 0, 0, Step(4, 3));
            far.Active = false;
            far.Activator = castle.Index;
            far.ActivatorLevel = 1; // unlocks at castle level 2
            var blocked = EconSim.Run(w, [Do(1, far, 1)]);
            Assert.Equal(ActionStatus.Blocked, blocked.Actions[0].Status, "locked");
            var r = EconSim.Run(w, [Do(1, castle, 2), Do(1, far, 1)]);
            Assert.Equal(ActionStatus.Planned, r.Actions[1].Status, "unlocked by the castle");
            Assert.Equal(1 + 3, r.Days[0].Buildings, "pays at dawn 1");
        }

        public static void AlreadyDoneCostsNothing()
        {
            var w = World();
            var house = Slot(w, "House", 1, 2, Step(5, 2), Step(8, 1));
            var r = EconSim.Run(w, [Do(1, house, 1)]);
            Assert.Equal(ActionStatus.AlreadyDone, r.Actions[0].Status, "done");
            Assert.Equal(0, r.Days[0].Spent, "no cost");
        }

        public static void PastDaysAreNotSimulated()
        {
            var w = World();
            w.FirstDay = 3;
            var house = Slot(w, "House", 0, 0, Step(5, 2));
            var r = EconSim.Run(w, [Do(2, house, 1)]);
            Assert.Equal(ActionStatus.Past, r.Actions[0].Status, "missed");
            Assert.Equal(3, r.Days.Count, "days 3..5");
        }

        public static void MinesRunDryAndHarboursGrow()
        {
            var w = World(gold: 0);
            w.WaveGold = new int[5];
            w.Slots[0].Income = 0;
            var mine = Slot(w, "Mine", 1, 5);
            mine.Modifiers.Add(new EconModifier { Kind = EconModifierKind.Mine, Step = 2, Min = 1 });
            var r = EconSim.Run(w, []);
            Assert.Equal("5,3,1,1,1", string.Join(",", r.Days.Select(d => d.Buildings)), "mine pays");

            var w2 = World(gold: 0);
            w2.WaveGold = new int[5];
            w2.Slots[0].Income = 0;
            var harbour = Slot(w2, "Harbour", 1, 1, Step(3, 1), Step(6, 99));
            harbour.Steps[1].Branches[0].DoublesHarbour = true;
            harbour.HarbourField = 1;
            harbour.Modifiers.Add(new EconModifier { Kind = EconModifierKind.Harbour, Step = 1, Max = 3 });
            var r2 = EconSim.Run(w2, [Do(3, harbour, 2)]);
            // Dawn 1 pays 1 (then 2), dawn 2 pays 2 (then 3, the cap); day 3's upgrade adds the current income (3) and
            // doubles the cap (6) and growth (2).
            Assert.Equal(3, r2.Actions[0].IncomeDelta, "level 2 adds the current income");
            Assert.Equal("1,2,6,6,6", string.Join(",", r2.Days.Select(d => d.Buildings)), "harbour pays");
            Assert.Equal(3, w2.Slots[harbour.Index].Modifiers[0].Max, "the world's own harbour is untouched");
            // Without the doubling, the cap pulls it back down after the first dawn.
            harbour.Steps[1].Branches[0].DoublesHarbour = false;
            Assert.Equal("1,2,6,3,3", string.Join(",", EconSim.Run(w2, [Do(3, harbour, 2)]).Days.Select(d => d.Buildings)), "capped");
        }

        public static void FollowersStepUpWithTheirOwner()
        {
            var w = World();
            var mill = Slot(w, "Mill", 0, 0, Step(6, 1), Step(8, 1));
            var field = Slot(w, "Field", 0, 0, Step(0, 1), Step(0, 1));
            field.Owner = mill.Index;
            field.Activator = mill.Index;
            var r = EconSim.Run(w, [Do(1, mill, 1), Do(2, mill, 2)]);
            Assert.Equal(2, r.Actions[0].IncomeDelta, "mill + field");
            Assert.Equal(1 + 2, r.Days[0].Buildings, "dawn 1");
            Assert.Equal(1 + 4, r.Days[1].Buildings, "dawn 2");
            Assert.Equal(2, r.Final.Slots[field.Index].Level, "field level");
        }

        public static void FollowersThatStartEarningPayThatDay()
        {
            var w = World(gold: 10);
            var mill = Slot(w, "Mill", 0, 0, Step(6, 0), Step(8, 0));
            var field = Slot(w, "Field", 0, 0, Step(0, 2), Step(0, 1));
            field.Owner = mill.Index;
            var r = EconSim.Run(w, [Do(1, mill, 1), Do(2, mill, 2)]);
            // Day 1: the field starts earning without having paid at dawn, so it's harvested that day.
            Assert.Equal(2, r.Days[0].Harvest, "harvested on the build day");
            Assert.Equal(10 - 6 + 2, r.Days[0].AfterSpend, "gold after building");
            // Day 2: it paid at dawn 1, so its extra +1 waits for dawn 2.
            Assert.Equal(0, r.Days[1].Harvest, "already paid that day");
            Assert.Equal(1 + 3, r.Days[1].Buildings, "dawn 2");
            // The build alone: 2 by day 1, 2 at dawn 1, 2 at dawn 2 = its 6 gold.
            Assert.Equal(2, r.Actions[0].PaybackDawn ?? -1, "payback counts the day's harvest");
        }

        public static void TempleUpgradesWaitForDawn()
        {
            var w = World(gold: 100);
            var temple = Slot(w, "Temple", 1, 0, Step(0), Step(5), Step(5));
            temple.Steps[1].Branches[0].BusyUntilDawn = true;
            var r = EconSim.Run(w, [Do(1, temple, 2), Do(1, temple, 3), Do(2, temple, 3)]);
            Assert.Equal(ActionStatus.Planned, r.Actions[0].Status, "first upgrade");
            Assert.Equal(ActionStatus.Blocked, r.Actions[1].Status, "busy the same day");
            Assert.Equal(ActionStatus.Planned, r.Actions[2].Status, "free the next day");
        }

        public static void CastleUpCutsWallAndTowerCosts()
        {
            var w = World(gold: 100);
            var castle = w.Slots[0];
            castle.Steps[1] = new EconStep { Cost = 10, Branches = [new EconBranch { Key = "up", Name = "Castle Up", StartsCastleUp = true }, new EconBranch { Key = "other", Name = "Other" }] };
            w.CastleUpDiscount2 = 2;
            w.CastleUpDiscount3 = 4;
            var built = Slot(w, "Tower", 1, 0, Step(3), Step(5), Step(9));
            built.CastleUpTags = built.CastleUpReceivers = 1;
            var later = Slot(w, "Tower", 0, 0, Step(3), Step(5), Step(9));
            later.CastleUpTags = later.CastleUpReceivers = 1;
            var r = EconSim.Run(w, [Do(1, castle, 2, "up"), Do(1, built, 2), Do(2, later, 1), Do(2, later, 2), Do(3, later, 3)]);
            Assert.Equal(3, r.Actions[1].Cost, "existing tower: 5 - 2");
            Assert.Equal(3, r.Actions[3].Cost, "tower built after: 5 - 2");
            Assert.Equal(5, r.Actions[4].Cost, "and its level 3: 9 - 4");
            var plain = EconSim.Run(w, [Do(1, castle, 2, "other"), Do(1, built, 2)]);
            Assert.Equal(5, plain.Actions[1].Cost, "no discount without Castle Up");
        }

        public static void BuildersGuildUpgradesAHouseEachDusk()
        {
            var w = World(gold: 100);
            var castle = w.Slots[0];
            castle.Steps[1] = new EconStep { Cost = 10, Branches = [new EconBranch { Key = "guild", Name = "Builder's Guild", StartsGuild = true }] };
            var h1 = Slot(w, "House", 1, 1, Step(2, 1), Step(4, 1), Step(8, 1));
            var h2 = Slot(w, "House", 1, 1, Step(2, 1), Step(4, 1), Step(8, 1));
            h1.IsHouse = h2.IsHouse = true;
            w.HouseOrder = [h1.Index, h2.Index];
            var r = EconSim.Run(w, [Do(1, castle, 2, "guild"), Do(2, h1, 2)]);
            // Choosing it upgrades h1 at once; dusk 1 upgrades h2; the plan's day-2 upgrade of h1 is already done.
            Assert.Equal(2, r.Final.Slots[h1.Index].Level, "h1");
            Assert.Equal(2, r.Final.Slots[h2.Index].Level, "h2");
            Assert.Equal(ActionStatus.AlreadyDone, r.Actions[1].Status, "already done by the guild");
            Assert.Equal(1 + 2 + 2, r.Days[0].Buildings, "dawn 1 pays both upgraded houses");
        }

        public static void TreasureHunterPaysBeforeTheLastNights()
        {
            var w = World(nights: 6, gold: 0);
            w.TreasureHunter = true;
            var r = EconSim.Run(w, []);
            // 6 nights: dawns 3, 4, 5 pay 10, 20, 30 (waves 2..4 = count-4..count-2), the last dawn nothing.
            Assert.Equal("0,0,10,20,30,0", string.Join(",", r.Days.Select(d => d.Treasure)), "treasure");
        }

        public static void ANightInProgressPaysItsDawnFirst()
        {
            var w = World();
            w.FirstDay = 2;
            w.DawnPending = true;
            w.TonightGold = 2;
            var house = Slot(w, "House", 1, 3);
            house.KnockedOut = true;
            var r = EconSim.Run(w, []);
            Assert.Equal(2, r.PendingDrops, "tonight's gold");
            Assert.Equal(1, r.PendingBuildings, "knocked-out house doesn't pay");
            Assert.Equal(10 + 2 + 1, r.Days[0].Start, "day 2 starts after that dawn");
            Assert.Equal(4, r.Days[0].Buildings, "pays again the dawn after");
        }

        public static void StateOnADayIncludesThatDaysSteps()
        {
            var w = World(gold: 100);
            var house = Slot(w, "House", 0, 0, Step(5, 1), Step(8, 1));
            var actions = new List<EconAction> { Do(2, house, 1), Do(3, house, 2) };
            Assert.Equal(0, EconSim.StateOn(w, actions, 1).Slots[house.Index].Level, "day 1");
            Assert.Equal(1, EconSim.StateOn(w, actions, 2).Slots[house.Index].Level, "day 2");
            Assert.Equal(2, EconSim.StateOn(w, actions, 3).Slots[house.Index].Level, "day 3");
            Assert.Equal(0, w.Slots[house.Index].Level, "the world itself is untouched");
        }

        public static void LockedChoicesAreSkipped()
        {
            var w = World(gold: 100);
            var barracks = Slot(w, "Barracks", 0, 0, new EconStep
            {
                Cost = 4,
                Branches = [new EconBranch { Key = "knights", Name = "Knights", Locked = true }, new EconBranch { Key = "spears", Name = "Spears" }],
            });
            var r = EconSim.Run(w, [Do(1, barracks, 1, "knights"), Do(2, barracks, 1, "spears")]);
            Assert.Equal(ActionStatus.Blocked, r.Actions[0].Status, "locked choice");
            Assert.Equal(ActionStatus.Planned, r.Actions[1].Status, "open choice");
            Assert.Equal(0, EconSim.BranchIndex(barracks.Steps[0], "knights"), "saved key found");
            Assert.Equal(1, EconSim.BranchIndex(barracks.Steps[0], "gone"), "missing key: first open branch");
        }

        private static EconAction Lose(int night, EconSlot slot) => new() { Kind = EconActionKind.KnockOut, Day = night, Slot = slot.Id };

        public static void KnockedOutBuildingsPayNothingThatDawn()
        {
            var w = World(gold: 0);
            w.WaveGold = new int[5];
            var house = Slot(w, "House", 1, 3);
            var mine = Slot(w, "Mine", 1, 6);
            mine.Modifiers.Add(new EconModifier { Kind = EconModifierKind.Mine, Step = 1, Min = 1, SustainableMin = 3, SustainableReset = 6 });
            var r = EconSim.Run(w, [Lose(2, house), Lose(2, mine), Lose(1, w.Slots[0])]);
            Assert.Equal(-3, r.Actions[0].IncomeDelta, "lost pay");
            // Dawn 1: castle out, house 3, mine 6 (then 5). Dawn 2: castle 1, house and mine out (mine stays 5 without Sustainable).
            Assert.Equal("9,1,9,8,7", string.Join(",", r.Days.Select(d => d.Buildings)), "pay");
            Assert.Equal(2, r.Days[1].KnockedOut, "count");
            mine.Modifiers[0].Sustainable = true;
            Assert.Equal(6 + 1 + 3, EconSim.Run(w, [Lose(2, mine)]).Days[2].Buildings, "sustainable mining resets a knocked-out mine");
            var notBuilt = Slot(w, "Tower", 0, 0, Step(3));
            Assert.Equal(ActionStatus.Blocked, EconSim.Run(w, [Lose(2, notBuilt)]).Actions[0].Status, "can't lose what isn't built");
        }

        public static void TempleWisdomPaysForBuildingsLost()
        {
            var w = World(gold: 100);
            w.WaveGold = new int[5];
            var temple = Slot(w, "Temple", 1, 0, Step(0), new EconStep
            {
                Cost = 5, Branches = [new EconBranch { Key = "wisdom", Name = "Wisdom", IsWisdom = true, WisdomGold = 4, BusyUntilDawn = true }, new EconBranch { Key = "prayers", Name = "Prayers", Prayers = true, BusyUntilDawn = true }],
            });
            var h1 = Slot(w, "House", 1, 2);
            var h2 = Slot(w, "House", 1, 2);
            h1.IsBuilding = h2.IsBuilding = true;
            var wisdom = Do(2, temple, 2, "wisdom");
            var r = EconSim.Run(w, [wisdom, Lose(2, h1), Lose(2, h2)]);
            Assert.Equal(8, r.Days[1].Extra, "4 per building lost");
            Assert.Equal(8, r.Actions[0].Wisdom, "on the temple step");
            Assert.Equal(1, r.Days[1].Buildings, "the lost houses don't pay");
            wisdom.Amount = 15;
            Assert.Equal(15, EconSim.Run(w, [wisdom, Lose(2, h1)]).Days[1].Extra, "typed amount");
            Assert.Equal(0, EconSim.Run(w, [Lose(3, h1)]).Days[2].Extra, "no wisdom, no gold");

            var shrine = Slot(w, "Shrine", 1, 3);
            shrine.IsShrine = true;
            var prayed = EconSim.Run(w, [Do(2, temple, 2, "prayers")]);
            Assert.Equal(1 + 2 + 2, prayed.Days[1].Buildings, "shrines pay nothing the night of the prayers");
            Assert.Equal(1 + 2 + 2 + 3, prayed.Days[2].Buildings, "and pay again after");
        }

        public static void DestructionGodKeepsBuildingsDownTwoNights()
        {
            var w = World(gold: 0);
            w.WaveGold = new int[5];
            var house = Slot(w, "House", 1, 3);
            house.IsBuilding = true;
            w.DestructionGod = true;
            var r = EconSim.Run(w, [Lose(2, house)]);
            Assert.Equal("4,1,1,4,4", string.Join(",", r.Days.Select(d => d.Buildings)), "misses dawns 2 and 3");
            var still = World(gold: 0);
            still.WaveGold = new int[5];
            still.DestructionGod = true;
            var down = Slot(still, "House", 1, 3);
            down.StillDown = true;
            Assert.Equal("1,4,4,4,4", string.Join(",", EconSim.Run(still, []).Days.Select(d => d.Buildings)), "left down at dawn: out tonight too");
        }

        public static void PerksThatKeepBuildingsPaying()
        {
            var w = World(gold: 0);
            var house = Slot(w, "House", 1, 3);
            house.NeverDenied = true;
            var stone = Slot(w, "Wall", 1, 0);
            stone.Indestructible = true;
            var r = EconSim.Run(w, [Lose(2, house), Lose(2, stone)]);
            Assert.Equal(4, r.Days[1].Buildings, "still pays");
            Assert.Equal(ActionStatus.Blocked, r.Actions[1].Status, "can't be destroyed");
        }

        public static void ImprovedPlowAddsToTheMillsLaterSteps()
        {
            var w = World(gold: 100);
            var mill = Slot(w, "Mill", 0, 0, new EconStep { Cost = 5, Branches = [new EconBranch { Key = "plain", Name = "Plain", IncomeChange = 1 }, new EconBranch { Key = "plow", Name = "Plow", IncomeChange = 1 }] }, Step(5, 1), Step(5, 1));
            mill.Steps[0].Branches[1].PlowTree = mill.Index;
            var r = EconSim.Run(w, [Do(1, mill, 1, "plow"), Do(2, mill, 2), Do(3, mill, 3)]);
            Assert.Equal(2, r.Actions[1].IncomeDelta, "level 2: +1 +1");
            Assert.Equal(2, r.Actions[2].IncomeDelta, "level 3: +1 +1");
            Assert.Equal(1, EconSim.Run(w, [Do(1, mill, 1, "plain"), Do(2, mill, 2)]).Actions[1].IncomeDelta, "without it");
        }

        public static void TempleBoughtTodayStillCounts()
        {
            var w = World(gold: 0);
            w.WaveGold = new int[5];
            var shrine = Slot(w, "Shrine", 1, 2);
            shrine.IsShrine = true;
            w.TempleNight = 1;
            w.TemplePrayers = true;
            Assert.Equal("1,3,3,3,3", string.Join(",", EconSim.Run(w, []).Days.Select(d => d.Buildings)), "shrines out on night 1");
            var w2 = World(gold: 0);
            var house = Slot(w2, "House", 1, 2);
            house.IsBuilding = true;
            w2.TempleNight = 1;
            w2.TempleWisdom = 5;
            Assert.Equal(5, EconSim.Run(w2, [Lose(1, house)]).Days[0].Extra, "wisdom bought today");
        }

        public static void KnockedOutFieldsWaitForDusk()
        {
            var w = World(gold: 10);
            var mill = Slot(w, "Mill", 1, 0, Step(0), Step(4, 0));
            var field = Slot(w, "Field", 1, 0, Step(0), Step(0, 2));
            field.Owner = mill.Index;
            field.DownToday = true; // knocked out last night: no pay until dusk
            var r = EconSim.Run(w, [Do(1, mill, 2)]);
            Assert.Equal(0, r.Days[0].Harvest, "not harvested by day");
            Assert.Equal(1 + 2, r.Days[0].Buildings, "pays at dawn");
        }

        public static void GoldFoundIsAdded()
        {
            var w = World(gold: 0);
            var r = EconSim.Run(w, [new EconAction { Kind = EconActionKind.Gold, Day = 3, Amount = 25 }]);
            Assert.Equal(25, r.Days[2].Extra, "night 3");
            Assert.Equal(EconSim.Run(w, []).EndGold + 25, r.EndGold, "end");
        }

        public static void PerksCanBeSwapped()
        {
            var w = World(gold: 30);
            w.Perks.Add(new EconPerk { Key = "interest", Kind = EconPerkKind.Interest, Each = 3, Max = 5 });
            w.Perks.Add(new EconPerk { Key = "treasure", Kind = EconPerkKind.TreasureHunter, T1 = 1, T2 = 2, T3 = 3 });
            w.Perks.Add(new EconPerk { Key = "mint", Kind = EconPerkKind.RoyalMint, Amount = 7 });
            var with = EconPerks.Apply(w, new Dictionary<string, bool> { ["interest"] = true, ["mint"] = true });
            Assert.True(with.Interest, "interest on");
            Assert.Equal(37, with.Gold, "royal mint at the start");
            Assert.Equal(5, EconSim.Run(with, []).Days[0].Interest, "interest pays");
            Assert.True(!w.Interest && w.Gold == 30, "the world itself is untouched");
            w.FirstDay = 2;
            Assert.Equal(30, EconPerks.Apply(w, new Dictionary<string, bool> { ["mint"] = true }).Gold, "royal mint only at the start");
        }

        public static void RatGodAndLoanRebuildTheWaves()
        {
            int[][] spawns = [[3, 3], [5], [4, 1]];
            Assert.Equal("6,5,5", string.Join(",", EconPerks.WaveGold(spawns, null, 0)), "plain");
            // Rat God halves, rounding up with carry: 1.5->2 (c -0.5), 1.0->1 (c 0), 2.5->3 (c -.5), 1.5->2 (c -.5), 0->0.
            Assert.Equal("3,3,2", string.Join(",", EconPerks.WaveGold(spawns, 0.5f, 0)), "rat god");
            Assert.Equal("0,4,5", string.Join(",", EconPerks.WaveGold(spawns, null, 7)), "loan takes 7 from the first coins");

            var w = World();
            w.SpawnGold = spawns.Concat(new[] { new[] { 2 }, new[] { 2 } }).ToArray();
            w.WaveGold = EconPerks.WaveGold(w.SpawnGold, null, 0);
            w.Perks.Add(new EconPerk { Key = "rat", Kind = EconPerkKind.RatGod, Factor = 0.5f });
            var rat = EconPerks.Apply(w, new Dictionary<string, bool> { ["rat"] = true });
            Assert.Equal("3,3,2,1,1", string.Join(",", rat.WaveGold), "applied to the world");
        }

        public static void BuildingPerksChangeEveryStep()
        {
            var w = World();
            var house = Slot(w, "House", 1, 2, Step(4, 2), Step(6, 1), Step(9, 1));
            house.PerkMods.Add(new EconPerkMod { Key = "cheap", Tree = house.Index, Cost = [0, -2, -3], Income = [1, 1, 0], Applied = false });
            var on = EconPerks.Apply(w, new Dictionary<string, bool> { ["cheap"] = true });
            var h = on.Slots[house.Index];
            Assert.Equal(4, h.Steps[1].Cost, "next step cheaper");
            Assert.Equal(2, h.Steps[1].Branches[0].IncomeChange, "and pays more");
            Assert.Equal(3, h.Income, "taken steps count as if it had been there from the start");
            Assert.Equal(6, w.Slots[house.Index].Steps[1].Cost, "the world itself is untouched");
        }

        public static void OutpostBuildsItsFarthestTowers()
        {
            var w = World(gold: 100);
            var outpost = Slot(w, "Outpost", 0, 0, Step(5));
            outpost.OutpostTowers = 1;
            var near = Slot(w, "Tower", 0, 0, Step(3));
            var far = Slot(w, "Tower", 0, 0, Step(3));
            near.ObjectName = far.ObjectName = "Tower Slot";
            near.Activator = far.Activator = outpost.Index;
            near.Active = far.Active = false;
            far.MapX = 10;
            w.Outpost = true;
            var r = EconSim.Run(w, [Do(1, outpost, 1)]);
            Assert.Equal(1, r.Final.Slots[far.Index].Level, "farthest tower built");
            Assert.Equal(0, r.Final.Slots[near.Index].Level, "only as many as it says");
            Assert.Equal(5, r.Days[0].Spent, "for free");
        }

        public static void SourcesCompareBuildingsOnADay()
        {
            var w = World(nights: 6, gold: 0);
            var house = Slot(w, "House", 0, 0, Step(4, 2), Step(6, 1));
            house.Active = false; // ignored: it's a what-if
            var list = EconSim.Evaluate(w, [], house.Index, 2);
            Assert.Equal(2, list.Count, "a line per level");
            Assert.Equal(4, list[0].Cost, "build cost");
            Assert.Equal(2 * 5, list[0].Earned, "pays 2 at dawns 2..6");
            Assert.Equal(3, list[0].PaybackDawn ?? -1, "back by dawn 3");
            Assert.Equal(10, list[1].Cost, "both steps");
            Assert.Equal(3 * 5, list[1].Earned, "3 per dawn");
            Assert.NotNull(list[0].Blocked, "says it isn't unlocked");
        }

        public static void BranchesShareTheirParentsEarlyDays()
        {
            var f = new PlannerFile { Level = "Test" };
            var root = new PlannerScenario { Name = "Main" };
            root.Actions.AddRange([new EconAction { Day = 1, Slot = "a", Level = 1 }, new EconAction { Day = 3, Slot = "b", Level = 1 }]);
            f.Scenarios.Add(root);
            var branch = new PlannerScenario { Name = "Mill", Parent = root.Id, ForkDay = 3 };
            branch.Actions.Add(new EconAction { Day = 3, Slot = "mill", Level = 1 });
            branch.Perks["x"] = true;
            f.Scenarios.Add(branch);
            var sub = new PlannerScenario { Name = "More", Parent = branch.Id, ForkDay = int.MaxValue };
            f.Scenarios.Add(sub);
            f.Normalize();
            Assert.Equal("a,mill", string.Join(",", f.Effective(branch).Select(a => a.Slot)), "branch");
            Assert.Equal("a,mill", string.Join(",", f.Effective(sub).Select(a => a.Slot)), "branch of a branch, all steps");
            Assert.Equal("Main › Mill › More", f.FullName(sub), "name");
            Assert.True(f.EffectivePerks(sub)["x"], "perks inherited");
            root.Actions[0].Day = 2;
            Assert.Equal(2, f.Effective(branch)[0].Day, "shared live");
            f.Delete(branch);
            Assert.Equal("a,mill", string.Join(",", f.Effective(sub).Select(a => a.Slot)), "keeps its items when its parent goes");
            Assert.True(f.EffectivePerks(sub)["x"], "and its perks");
        }

        public static void PlansRoundTrip()
        {
            using var dir = new TempDir();
            var file = PlannerStore.Load(dir.Path, "Neuland", out string error);
            Assert.Null(error, "fresh load");
            Assert.Equal(1, file.Scenarios.Count, "one empty plan");
            file.CurrentScenario.Actions.Add(new EconAction { Day = 3, Slot = "abc", Level = 2, Choice = "knights" });
            var copy = file.CurrentScenario.CopyAs(file.FreeName("Plan 1"));
            file.Scenarios.Add(copy);
            file.Compare = copy.Id;
            PlannerStore.Save(dir.Path, file);
            var back = PlannerStore.Load(dir.Path, "Neuland", out error);
            Assert.Null(error, "reload");
            Assert.Equal(2, back.Scenarios.Count, "plans");
            Assert.Equal("Plan 1 2", back.Scenarios[1].Name, "free name");
            Assert.Equal("knights", back.CurrentScenario.Actions[0].Choice, "choice kept");
            Assert.Equal(copy.Id, back.CompareScenario?.Id, "compare kept");

            System.IO.File.WriteAllText(PlannerStore.FileFor(dir.Path, "Broken"), "{ not json");
            var broken = PlannerStore.Load(dir.Path, "Broken", out error);
            Assert.NotNull(error, "bad file reported");
            Assert.Equal(1, broken.Scenarios.Count, "fresh plan instead");
            Assert.True(System.IO.File.Exists(PlannerStore.FileFor(dir.Path, "Broken") + ".bad"), "bad file kept");
        }
    }
}
