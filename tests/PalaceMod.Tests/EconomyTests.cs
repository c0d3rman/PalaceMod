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

        public static void CastleUpCutsWallAndTowerCosts()
        {
            var w = World(gold: 100);
            var castle = w.Slots[0];
            castle.Steps[1] = new EconStep { Cost = 10, Branches = [new EconBranch { Key = "up", Name = "Castle Up", StartsCastleUp = true }, new EconBranch { Key = "other", Name = "Other" }] };
            w.CastleUpDiscount2 = 2;
            w.CastleUpDiscount3 = 4;
            var built = Slot(w, "Tower", 1, 0, Step(3), Step(5), Step(9));
            built.IsWallOrTower = true;
            var later = Slot(w, "Tower", 0, 0, Step(3), Step(5), Step(9));
            later.IsWallOrTower = true;
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
