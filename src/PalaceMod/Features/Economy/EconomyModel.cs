using System;
using System.Collections.Generic;
using System.Linq;

/*
    The economy planner's model and simulation. Game-independent (no UnityEngine), so it's unit tested in
    tests/PalaceMod.Tests; EconomySnapshot fills an EconWorld from the live level.

    Timeline. "Day N" is the day before night N; "dawn N" ends night N. The game's order, which the simulation follows:
      day N      the player builds and upgrades (plan steps, in the plan's order). The only gold earned by day is from
                 slots that follow their owner's tree (a mill's fields) and start earning: they didn't pay at dawn and
                 their own BuildComplete never runs, so they're harvestable, and the night horn harvests everything
                 before dusk. A temple upgrade keeps its building from another step until the next dawn.
      dusk N     every coin is picked up; OnDuskEarly: Builder's Guild upgrades a level-1 house for free;
                 OnDusk: Interest pays min(max, ceil(gold / each)) (InterestPerk.InterestAmount).
                 Temple prayers bought that day knock every shrine out for the night (TemplePrayers).
      night N    enemies drop the wave's gold (WaveInfo.goldReward, with Loan and Rat God). Buildings the plan marks as
                 knocked out that night pay nothing at dawn; with Temple wisdom bought that day, each such building
                 drops goldForDestroyedBuilding (or the plan gives the night's wisdom gold itself). The plan can also
                 add gold found that night.
      dawn N     every built building with an interactor pays its GoldIncome unless knocked out tonight
                 (BuildingInteractor.OnDawn_AfterSunrise -> Harvest), then its income modifiers run:
                   mine     -incomeReductionPerTurn, floored (higher floor with Sustainable Mining); knocked out with
                            Sustainable Mining: reset instead; knocked out without it: unchanged
                   harbour  +incomeIncreasePerTurn up to maximumIncome unless knocked out, and the level-2 upgrade's
                            income change becomes the new income (FishingHarbour writes Upgrades[1].branches[0]);
                            that upgrade doubles the cap and growth (FishingHarbourUpgrade)
                   daily    +1 (IncreaseIncomeDaily, on every registered interactor, built or not)
                 Treasure Hunter pays at dawns N-3, N-2 and N-1 of an N-night level (EnemySpawner.OnStartOfTheDay).
    The final dawn's income counts: the game declares victory after it.

    Building or upgrading (BuildSlot.TryToBuildOrUpgradeAndPay + OnUpgradeChoiceComplete):
      - possible when the slot is active (unlocked: its activator's level > activatorLevel), not maxed, the step isn't
        disabled in this mode, and the castle (the slot's required root) is high enough: with root level <=
        level + requiredRootLevelDifference only the build itself (level 0 -> 1) is allowed;
      - slots that follow their activator's tree (ActivatorUpgradesThis, e.g. a mill's fields) step up with it;
      - the branch's income change is added; the slot's children unlock once it passes their activatorLevel;
      - Castle Up (UpgradeCastleUp) cuts walls' and towers' next upgrade costs when chosen (once per WallOrTower-tagged
        object), and again whenever one reaches level 1 or 2 afterwards (once per CaslteUpReciever);
      - Builder's Guild (UpgradeBuildersGuild) upgrades a house when chosen and at every dusk after;
      - with Outpost, building an outpost builds its farthest towers for free (OutpostPerk).

    Perks (EconPerks.Apply) can be swapped as a what-if: as if the run had started with that loadout, so building
    perks change the costs and income of every step, taken or not, and start-gold perks only count on day 1.

    Not modelled: future knock-outs the plan doesn't mark, shrines unlocking from nearby deaths, and God of Choice
    locks on slots that aren't unlocked yet.
*/
namespace PalaceMod.Economy
{
    public enum EconModifierKind { Mine, Harbour, Daily }

    public sealed class EconModifier
    {
        public EconModifierKind Kind;
        /// <summary>Mine: reduction per dawn. Harbour: increase per dawn.</summary>
        public int Step = 1;
        /// <summary>Mine: income floor (without / with Sustainable Mining).</summary>
        public int Min, SustainableMin;
        /// <summary>Mine: income after a knock-out with Sustainable Mining.</summary>
        public int SustainableReset;
        public bool Sustainable;
        /// <summary>Harbour: income cap.</summary>
        public int Max;
        /// <summary>The perk behind Sustainable (mine) or the extra boats (harbour), for perk what-ifs.</summary>
        public string PerkKey;
        /// <summary>Harbour: extra boats from the perk, and whether they're in Max.</summary>
        public int PerkBoats;
        public bool PerkOn;
        /// <summary>Harbour: its upgrade doubled the cap, so the perk's boats count twice.</summary>
        public bool Doubled;

        internal EconModifier Copy() => (EconModifier)MemberwiseClone();
    }

    public sealed class EconBranch
    {
        /// <summary>Stable id (the choice's name); saved in plans.</summary>
        public string Key;
        public string Name;
        public string Description;
        public int IncomeChange;
        /// <summary>Can't be picked in this run (locked behind an unlock, or disabled by the mode).</summary>
        public bool Locked;
        public bool StartsGuild, StartsCastleUp;
        /// <summary>Doubles the harbour's cap and growth (FishingHarbourUpgrade).</summary>
        public bool DoublesHarbour;
        /// <summary>Keeps the building from another step until the next dawn (TempleUpgrade).</summary>
        public bool BusyUntilDawn;
        /// <summary>Temple prayers: shrines are knocked out that night.</summary>
        public bool Prayers;
        /// <summary>Temple wisdom: gold per building destroyed that night, and the health it drains from every
        /// building per second (a share of max health).</summary>
        public bool IsWisdom;
        public int WisdomGold;
        public float WisdomDrain;
        /// <summary>Improved Plow: +1 income on steps 2 and 3 of this slot's tree (-1: not the plow).</summary>
        public int PlowTree = -1;

        internal EconBranch Copy() => (EconBranch)MemberwiseClone();
    }

    public sealed class EconStep
    {
        public int Cost;
        public bool Disabled;
        /// <summary>The game's description of a one-branch step, if it has one.</summary>
        public string Text;
        public List<EconBranch> Branches = [];
        public bool IsChoice => Branches.Count > 1;

        internal EconStep Copy() => new() { Cost = Cost, Disabled = Disabled, Text = Text, Branches = Branches.Select(b => b.Copy()).ToList() };
    }

    /// <summary>A perk's per-step changes to one upgrade tree (PerkCostModifyer, PerkIncomeModifyer), or to the
    /// castle-level cap (PerkUnrestrictedUpgrading).</summary>
    public sealed class EconPerkMod
    {
        public string Key;
        /// <summary>The slot whose steps change (a follower's perk changes its owner's tree).</summary>
        public int Tree;
        public int[] Cost = [], Income = [];
        /// <summary>Steps whose cost came from the save, which the cost change skips.</summary>
        public bool[] CostSkip = [];
        /// <summary>PerkUnrestrictedUpgrading: the cap without the perk (null: not this kind).</summary>
        public int? RootDifference;
        /// <summary>Whether it's applied in the slot's data.</summary>
        public bool Applied;
        /// <summary>Still to run (its slot isn't unlocked yet): it only ever touches the steps not taken.</summary>
        public bool Pending;

        internal EconPerkMod Copy() => (EconPerkMod)MemberwiseClone();
    }

    /// <summary>One build slot: its upgrade tree and its state now.</summary>
    public sealed class EconSlot
    {
        public int Index;
        /// <summary>Stable id across sessions (the slot's SaveLoadEntity GUID); saved in plans.</summary>
        public string Id;
        public string TypeName;
        /// <summary>The slot's GameObject name (OutpostPerk picks towers by it).</summary>
        public string ObjectName = "";
        /// <summary>Short name that tells same-type slots apart ("House 3 · north-east, by the Mill").</summary>
        public string Label;
        /// <summary>Position on the planner's map, screen-oriented (y up the screen).</summary>
        public float MapX, MapY;
        public List<EconStep> Steps = [];
        /// <summary>Slot whose tree this one follows (ActivatorUpgradesThis), or -1.</summary>
        public int Owner = -1;
        /// <summary>Slot that unlocks this one, and the level it must pass, or -1.</summary>
        public int Activator = -1, ActivatorLevel;
        /// <summary>The slot whose level caps this one's (the top of its activator chain), or its own index.</summary>
        public int Root;
        public int RootLevelDifference = -100;
        /// <summary>Has an interactor, so it's harvested at dawn.</summary>
        public bool Pays;
        public bool IsHouse, IsCastle, IsShrine;
        /// <summary>Tagged Building: Temple wisdom's coins ride on it.</summary>
        public bool IsBuilding;
        /// <summary>Castle Up: WallOrTower-tagged objects (each discounted when it's chosen) and CaslteUpRecievers
        /// (each discounting when the slot reaches level 1 or 2 afterwards).</summary>
        public int CastleUpTags, CastleUpReceivers;
        public bool IsWallOrTower => CastleUpTags > 0 || CastleUpReceivers > 0;
        /// <summary>Outpost perk: towers it builds for free when it's built.</summary>
        public int OutpostTowers;
        public List<EconModifier> Modifiers = [];
        public List<EconPerkMod> PerkMods = [];

        // State now.
        public int Level;
        public int Income;
        public bool Active;
        public bool KnockedOut;
        /// <summary>Already paid today (BuildingInteractor.harvestedToday): a slot that hasn't, and gets income by
        /// day, pays it that day (the night horn collects it).</summary>
        public bool HarvestedToday;
        /// <summary>Can't take another step until the next dawn (a temple upgrade in effect).</summary>
        public bool Busy;
        /// <summary>Knocked out last night: not harvestable until dusk, even if it starts earning by day.</summary>
        public bool DownToday;
        /// <summary>God of Destruction: destroyed and not revived this dawn, so it's knocked out next night too.</summary>
        public bool StillDown;
        /// <summary>A knock-out doesn't stop its pay (PerkRemoveDestroyedAtNightTag).</summary>
        public bool NeverDenied;
        /// <summary>Can't be knocked out (PerkIndestructible).</summary>
        public bool Indestructible;
        /// <summary>Harbour: the level-2 upgrade's current income change.</summary>
        public int HarbourField;
        /// <summary>Branches taken so far, per step.</summary>
        public List<int> Applied = [];

        public bool EverPays => Pays && (Income > 0 || Modifiers.Count > 0 || Steps.Any(s => s.Branches.Any(b => b.IncomeChange != 0)));
        public bool IsDependent => Owner >= 0;
        internal EconSlot Shallow() => (EconSlot)MemberwiseClone();
        public int MaxLevel
        {
            get
            {
                int max = 0;
                while (max < Steps.Count && !Steps[max].Disabled) max++;
                return Math.Max(max, Level);
            }
        }
    }

    public enum EconPerkKind { Interest, TreasureHunter, RoyalMint, Loan, RatGod, Outpost, DestructionGod, Building }

    /// <summary>A perk that moves gold, for loadout what-ifs.</summary>
    public sealed class EconPerk
    {
        public string Key;
        public string Name;
        public EconPerkKind Kind;
        public bool Equipped;
        /// <summary>Royal Mint and Loan: start gold. Loan: also the interest taken out of the first waves' drops.</summary>
        public int Amount, Amount2;
        /// <summary>Rat God: drops multiplier.</summary>
        public float Factor = 1f;
        /// <summary>Interest: 1 per Each gold, at most Max.</summary>
        public int Each = 3, Max = 20;
        /// <summary>Treasure Hunter: the three dawns' gold.</summary>
        public int T1 = 10, T2 = 20, T3 = 30;
    }

    public sealed class EconWorld
    {
        public string Level;
        public List<EconSlot> Slots = [];
        /// <summary>Gold now (TrueBalance: in hand, on the ground and still to spawn).</summary>
        public int Gold;
        /// <summary>The first day the plan can act on (today by day, tomorrow at night).</summary>
        public int FirstDay;
        /// <summary>The level's last night.</summary>
        public int LastNight;
        /// <summary>Night FirstDay-1 hasn't reached its dawn yet (night, or sunrise before the buildings pay).</summary>
        public bool DawnPending;
        /// <summary>Enemy gold still to come tonight (DawnPending only).</summary>
        public int TonightGold;
        /// <summary>Enemy gold per night, index night-1.</summary>
        public int[] WaveGold = [];
        /// <summary>Each wave's spawns' coins before Rat God and Loan, for perk what-ifs (null: unknown).</summary>
        public int[][] SpawnGold;
        public bool Interest;
        public int InterestEach = 3, InterestMax = 20;
        public bool TreasureHunter;
        public int Treasure1 = 10, Treasure2 = 20, Treasure3 = 30;
        public bool GuildActive, GuildUsed;
        /// <summary>Houses in the order Builder's Guild looks at them.</summary>
        public List<int> HouseOrder = [];
        public bool CastleUpActive;
        public int CastleUpDiscount2 = 2, CastleUpDiscount3 = 4;
        public bool Outpost;
        /// <summary>God of Destruction: destroyed buildings stay down a second night.</summary>
        public bool DestructionGod;
        /// <summary>A temple upgrade bought before the plan starts and still to take effect: the night it applies to
        /// (0: none), Prayers, and Wisdom's gold per building lost (-1: not wisdom).</summary>
        public int TempleNight;
        public bool TemplePrayers;
        public int TempleWisdom = -1;
        public List<EconPerk> Perks = [];
        /// <summary>Before anything's happened: start-gold perks still count.</summary>
        public bool StartOfRun => FirstDay <= 1 && !DawnPending;

        public EconSlot Find(string id) => id == null ? null : Slots.FirstOrDefault(s => s.Id == id);

        public EconWorld Clone()
        {
            var w = (EconWorld)MemberwiseClone();
            w.WaveGold = (int[])WaveGold.Clone();
            w.HouseOrder = [.. HouseOrder];
            w.Slots = Slots.Select(s =>
            {
                var c = s.Shallow();
                c.Applied = [.. s.Applied];
                c.Modifiers = s.Modifiers.Select(m => m.Copy()).ToList();
                c.PerkMods = s.PerkMods.Select(m => m.Copy()).ToList();
                // Costs and income changes move during a run (Castle Up) and with perks, so steps are copied.
                c.Steps = s.Steps.Select(st => st.Copy()).ToList();
                return c;
            }).ToList();
            return w;
        }
    }

    public enum EconActionKind
    {
        /// <summary>Build or upgrade <see cref="EconAction.Slot"/> to <see cref="EconAction.Level"/> on day Day.</summary>
        Step,
        /// <summary><see cref="EconAction.Slot"/> is knocked out on night Day.</summary>
        KnockOut,
        /// <summary><see cref="EconAction.Amount"/> gold comes in on night Day (an estimate the plan can't work out).</summary>
        Gold,
    }

    /// <summary>One thing in a plan: a build or upgrade, a building lost on a night, or gold found on a night.</summary>
    public sealed class EconAction
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public EconActionKind Kind;
        public int Day;
        public string Slot;
        public int Level;
        /// <summary>Branch key (choice name); null for one-branch steps.</summary>
        public string Choice;
        /// <summary>Gold: the amount. A Temple wisdom step: the night's wisdom gold, overriding the estimate from
        /// the buildings the plan marks lost (null: work it out).</summary>
        public int? Amount;

        public EconAction Copy() => (EconAction)MemberwiseClone();
        public bool IsStep => Kind == EconActionKind.Step;
    }

    public enum ActionStatus
    {
        /// <summary>Done in the projection.</summary>
        Planned,
        /// <summary>Done, but there wasn't enough gold: the projection goes negative.</summary>
        Short,
        /// <summary>The slot is already at that level by then (built by the player or Builder's Guild).</summary>
        AlreadyDone,
        /// <summary>A day that's already over.</summary>
        Past,
        /// <summary>Can't be done then; skipped.</summary>
        Blocked,
    }

    public sealed class ActionResult
    {
        public EconAction Action;
        public EconSlot Slot;
        public ActionStatus Status;
        public string Reason;
        public int Cost;
        /// <summary>Step: change in what the slot (and slots following it) pays at the next dawn. Knock-out: the pay lost
        /// that dawn (negative). Gold: the gold.</summary>
        public int IncomeDelta;
        /// <summary>Gold missing when it was paid (Short).</summary>
        public int Shortfall;
        /// <summary>Dawn by which this step's extra income has repaid its cost, or null.</summary>
        public int? PaybackDawn;
        /// <summary>Extra income the step brings by the last dawn.</summary>
        public int ExtraIncome;
        /// <summary>Temple wisdom: the night's gold.</summary>
        public int Wisdom;
        public EconBranch Branch;
        public bool Counts => Status is ActionStatus.Planned or ActionStatus.Short;
    }

    public sealed class DayResult
    {
        public int Day;
        /// <summary>Gold at sunrise (before building).</summary>
        public int Start;
        public int Spent;
        /// <summary>Gold after the day's building, before dusk.</summary>
        public int AfterSpend => Start - Spent + Harvest;
        /// <summary>Paid by day by slots that start earning that day without having paid at dawn (a mill's fields).</summary>
        public int Harvest;
        public int Interest;
        public int Drops;
        /// <summary>The night's other gold: Temple wisdom and gold the plan adds.</summary>
        public int Extra;
        /// <summary>What buildings pay at dawn (after knock-outs).</summary>
        public int Buildings;
        public int Treasure;
        public int Income => Harvest + Interest + Drops + Extra + Buildings + Treasure;
        /// <summary>Gold after dawn (the next day's Start).</summary>
        public int AtDawn;
        public int Shortfall => Math.Max(0, -AfterSpend);
        public int Actions;
        /// <summary>Buildings knocked out that night by the plan (and Temple prayers).</summary>
        public int KnockedOut;
    }

    public sealed class EconResult
    {
        public readonly List<DayResult> Days = [];
        public readonly List<ActionResult> Actions = [];
        /// <summary>The pending dawn (night in progress): what it pays, before the first plannable day.</summary>
        public int PendingDrops, PendingBuildings, PendingTreasure, PendingExtra;
        public int EndGold;
        public int TotalSpent => Days.Sum(d => d.Spent);
        public int TotalIncome => Days.Sum(d => d.Income) + PendingDrops + PendingBuildings + PendingTreasure + PendingExtra;
        /// <summary>Building pay at the next dawn and at the last one.</summary>
        public int NextDawnBuildings, LastDawnBuildings;
        public DayResult FirstShortDay => Days.FirstOrDefault(d => d.Shortfall > 0);
        /// <summary>Pay per slot per dawn (gold harvested by day counted with that day's dawn), [slot][dawn - FirstDawn].</summary>
        internal int[][] Pay;
        internal int FirstDawn;
        /// <summary>The world as it is after the last dawn.</summary>
        public EconWorld Final;

        public int PayOf(int slot, int dawn) =>
            Pay == null || slot < 0 || slot >= Pay.Length || dawn - FirstDawn < 0 || dawn - FirstDawn >= Pay[slot].Length ? 0 : Pay[slot][dawn - FirstDawn];
    }

    /// <summary>What building one slot up to a level on a day brings, for the income-sources comparison.</summary>
    public sealed class SourceEstimate
    {
        public int Level;
        /// <summary>Gold spent on its steps.</summary>
        public int Cost;
        /// <summary>Extra pay at the first dawn after, and at the last dawn.</summary>
        public int FirstDawn, LastDawn;
        /// <summary>Extra pay by the last dawn.</summary>
        public int Earned;
        public int Net => Earned - Cost;
        public int? PaybackDawn;
        /// <summary>Why it couldn't be done on that day in this run, if it couldn't (the estimate ignores it).</summary>
        public string Blocked;
        public string Choice;
    }

    public static class EconSim
    {
        /// <summary>Runs the plan over the rest of the level. <paramref name="actions"/> are done in list order
        /// within a day. With <paramref name="paybacks"/>, also works out each income step's payback dawn.</summary>
        public static EconResult Run(EconWorld world, IList<EconAction> actions, bool paybacks = true, bool guild = true)
        {
            var result = Simulate(world, actions, guild);
            if (paybacks)
            {
                var ordered = Ordered(actions);
                foreach (var r in result.Actions)
                {
                    if (!r.Counts || r.Slot == null || !r.Action.IsStep) continue;
                    // Only income buildings (or ones whose followers earn) have a payback.
                    if (!r.Slot.EverPays && !world.Slots.Any(s => s.Owner == r.Slot.Index && s.EverPays)) continue;
                    Payback(world, ordered, r, guild);
                }
            }
            return result;
        }

        /// <summary>Whether the slot's next step can be taken in this state (BuildSlot.CanBeUpgraded).</summary>
        public static bool CanUpgrade(EconWorld world, EconSlot slot) => new State(world, true).CanUpgrade(slot);

        /// <summary>What the slot's next step would change its income by (with choices: per branch).</summary>
        public static int StepIncome(EconSlot slot, int branch)
        {
            if (slot.Level >= slot.Steps.Count) return 0;
            var step = slot.Steps[slot.Level];
            if (step.Branches.Count == 0) return 0;
            bool harbour = slot.Modifiers.Any(m => m.Kind == EconModifierKind.Harbour);
            if (harbour && slot.Level == 1 && branch == 0) return slot.HarbourField;
            return step.Branches[Math.Min(branch, step.Branches.Count - 1)].IncomeChange;
        }

        /// <summary>Actions in the order they're done: by day, then as listed.</summary>
        public static List<EconAction> Ordered(IList<EconAction> actions) =>
            actions.Select((a, i) => (a, i)).OrderBy(t => t.a.Day).ThenBy(t => t.i).Select(t => t.a).ToList();

        /// <summary>The world on <paramref name="day"/> after that day's planned steps (for the picker).</summary>
        public static EconWorld StateOn(EconWorld world, IList<EconAction> actions, int day)
        {
            var before = Ordered(actions).Where(a => a.Day <= day).ToList();
            return Simulate(world, before, guild: true, stopAfterDay: day).Final;
        }

        /// <summary>For the income-sources comparison: <paramref name="slot"/> built up to each of its levels on
        /// <paramref name="day"/> on top of <paramref name="plan"/> (the plan's own steps for it left out), against the
        /// plan without it. Unlocks and the castle's level are ignored, so every building can be compared.</summary>
        public static List<SourceEstimate> Evaluate(EconWorld world, IList<EconAction> plan, int slot, int day)
        {
            var list = new List<SourceEstimate>();
            var target = world.Slots[slot];
            if (target.IsDependent) return list;
            var basePlan = plan.Where(a => Resolve(world, a.Slot)?.Index != slot).ToList();
            var without = Simulate(world, basePlan, guild: true, ignoreRequirements: true);
            var family = world.Slots.Where(s => s.Index == slot || s.Owner == slot).Select(s => s.Index).ToList();
            var steps = new List<EconAction>();
            var state = StateOn(world, basePlan, day).Slots[slot];
            for (int level = state.Level + 1; level <= target.MaxLevel; level++)
            {
                var step = level - 1 < target.Steps.Count ? target.Steps[level - 1] : null;
                if (step == null) break;
                // The branch that pays most.
                int best = 0;
                for (int b = 1; b < step.Branches.Count; b++)
                    if (!step.Branches[b].Locked && step.Branches[b].IncomeChange > step.Branches[best].IncomeChange) best = b;
                steps.Add(new EconAction { Day = day, Slot = target.Id, Level = level, Choice = step.Branches.Count > 0 ? step.Branches[best].Key : null });
                var with = Simulate(world, basePlan.Concat(steps).ToList(), guild: true, ignoreRequirements: true);
                var mine = with.Actions.Where(r => steps.Contains(r.Action)).ToList();
                if (mine.Any(r => !r.Counts)) break;
                var e = new SourceEstimate
                {
                    Level = level, Cost = mine.Sum(r => r.Cost),
                    Choice = step.IsChoice ? step.Branches[best].Name : null,
                };
                var check = Simulate(world, basePlan.Concat(steps).ToList(), guild: true);
                var blocked = check.Actions.FirstOrDefault(r => steps.Contains(r.Action) && r.Status == ActionStatus.Blocked);
                if (blocked != null) e.Blocked = blocked.Reason;
                int total = 0;
                for (int dawn = day; dawn <= world.LastNight; dawn++)
                {
                    int diff = family.Sum(f => with.PayOf(f, dawn) - without.PayOf(f, dawn));
                    if (dawn == day) e.FirstDawn = diff;
                    if (dawn == world.LastNight) e.LastDawn = diff;
                    total += diff;
                    if (e.PaybackDawn == null && e.Cost > 0 && total >= e.Cost) e.PaybackDawn = dawn;
                }
                e.Earned = total;
                list.Add(e);
            }
            return list;
        }

        private static EconResult Simulate(EconWorld start, IList<EconAction> actions, bool guild, int stopAfterDay = int.MaxValue, bool ignoreRequirements = false)
        {
            var w = start.Clone();
            if (!guild) w.GuildActive = false;
            var result = new EconResult { FirstDawn = w.FirstDay - 1 };
            int dawns = Math.Max(0, w.LastNight - (w.FirstDay - 1) + 1);
            result.Pay = w.Slots.Select(_ => new int[dawns]).ToArray();
            var s = new State(w, guild) { Result = result, IgnoreRequirements = ignoreRequirements };
            // By day, buildings God of Destruction left down at dawn are knocked out again tonight.
            if (!w.DawnPending) s.Carry = new HashSet<int>(w.Slots.Where(x => x.StillDown).Select(x => x.Index));
            var ordered = Ordered(actions);
            int firstNight = w.DawnPending ? w.FirstDay - 1 : w.FirstDay;

            foreach (var a in ordered.Where(a => a.IsStep ? a.Day < w.FirstDay : a.Day < firstNight))
                result.Actions.Add(Past(w, a));

            var nightResults = new Dictionary<EconAction, ActionResult>();
            foreach (var a in ordered.Where(a => !a.IsStep && a.Day >= firstNight && a.Day <= w.LastNight))
            {
                var r = new ActionResult { Action = a, Slot = a.Kind == EconActionKind.KnockOut ? w.Find(a.Slot) : null };
                nightResults[a] = r;
                result.Actions.Add(r);
            }

            if (w.DawnPending)
            {
                int n = w.FirstDay - 1;
                w.Gold += w.TonightGold;
                result.PendingDrops = w.TonightGold;
                s.Wisdom = w.TempleWisdom >= 0 && w.TempleNight == n ? (null, w.TempleWisdom) : null;
                result.PendingExtra = Night(s, n, ordered, nightResults, out var knocked);
                Dawn(s, n, result, knocked, out int buildings, out int treasure);
                result.PendingBuildings = buildings;
                result.PendingTreasure = treasure;
                result.NextDawnBuildings = buildings;
            }

            for (int day = w.FirstDay; day <= w.LastNight && day <= stopAfterDay; day++)
            {
                var d = new DayResult { Day = day, Start = w.Gold };
                s.Today = d;
                s.TodayPay = day - result.FirstDawn;
                s.Prayers = w.TemplePrayers && w.TempleNight == day;
                s.Wisdom = w.TempleWisdom >= 0 && w.TempleNight == day ? (null, w.TempleWisdom) : null;
                foreach (var a in ordered.Where(a => a.IsStep && a.Day == day))
                {
                    var r = Apply(s, a);
                    result.Actions.Add(r);
                    if (r.Counts) { d.Spent += r.Cost; d.Actions++; }
                }
                if (day == stopAfterDay) break;
                // Dusk: every coin has been picked up, so interest counts all the gold; last night's knock-outs end.
                foreach (var slot in w.Slots) slot.DownToday = false;
                s.Today = null;
                if (guild && w.GuildActive) s.GuildUpgrade();
                if (w.Interest)
                {
                    d.Interest = Math.Min(w.InterestMax, (int)Math.Ceiling(Math.Max(0, w.Gold) / (double)Math.Max(1, w.InterestEach)));
                    w.Gold += d.Interest;
                }
                // Night.
                d.Drops = day - 1 < w.WaveGold.Length && day >= 1 ? w.WaveGold[day - 1] : 0;
                w.Gold += d.Drops;
                d.Extra = Night(s, day, ordered, nightResults, out var knocked);
                d.KnockedOut = knocked.Count;
                // Dawn.
                Dawn(s, day, result, knocked, out d.Buildings, out d.Treasure);
                if (!w.DawnPending && day == w.FirstDay) result.NextDawnBuildings = d.Buildings;
                d.AtDawn = w.Gold;
                result.Days.Add(d);
                result.LastDawnBuildings = d.Buildings;
            }
            foreach (var a in ordered.Where(a => a.Day > w.LastNight))
                result.Actions.Add(new ActionResult { Action = a, Slot = w.Find(a.Slot), Status = ActionStatus.Blocked, Reason = "After the last night" });
            result.EndGold = w.Gold;
            result.Final = w;
            if (w.DawnPending && result.Days.Count == 0) result.LastDawnBuildings = result.PendingBuildings;
            // Keep the actions in the plan's order for display.
            var index = new Dictionary<EconAction, int>();
            for (int i = 0; i < actions.Count; i++) index[actions[i]] = i;
            result.Actions.Sort((x, y) => index[x.Action].CompareTo(index[y.Action]));
            return result;
        }

        private static ActionResult Past(EconWorld w, EconAction a)
        {
            if (!a.IsStep) return new ActionResult { Action = a, Slot = w.Find(a.Slot), Status = ActionStatus.Past, Reason = $"Night {a.Day} is over" };
            var slot = Resolve(w, a.Slot);
            bool done = slot != null && slot.Level >= a.Level;
            return new ActionResult
            {
                Action = a, Slot = slot, Status = done ? ActionStatus.AlreadyDone : ActionStatus.Past,
                Reason = done ? "Done" : $"Day {a.Day} is over",
            };
        }

        // The night's plan items: which buildings are knocked out, and gold beyond the enemies' (wisdom, estimates).
        private static int Night(State s, int night, List<EconAction> ordered, Dictionary<EconAction, ActionResult> results, out HashSet<int> knocked)
        {
            var w = s.World;
            knocked = [];
            if (w.DawnPending && night == w.FirstDay - 1)
                foreach (var slot in w.Slots) if (slot.KnockedOut) knocked.Add(slot.Index);
            int lostByPlan = 0;
            var destroyed = new HashSet<int>();
            int extra = 0;
            foreach (var a in ordered.Where(a => !a.IsStep && a.Day == night))
            {
                var r = results[a];
                if (a.Kind == EconActionKind.Gold)
                {
                    r.Status = ActionStatus.Planned;
                    r.IncomeDelta = a.Amount ?? 0;
                    extra += r.IncomeDelta;
                    continue;
                }
                var slot = w.Find(a.Slot);
                if (slot == null) { Block(r, "Not on this map"); continue; }
                if (slot.Level < 1) { Block(r, "Not built by then"); continue; }
                if (slot.Indestructible) { Block(r, "It can't be destroyed"); continue; }
                if (knocked.Contains(slot.Index) || destroyed.Contains(slot.Index)) { r.Status = ActionStatus.AlreadyDone; r.Reason = "Already knocked out that night"; continue; }
                r.Status = ActionStatus.Planned;
                destroyed.Add(slot.Index);
                if (slot.IsBuilding) lostByPlan++;
                // A building whose perk keeps it paying is destroyed all the same, but its pay isn't denied.
                if (slot.NeverDenied) { r.Reason = "Still pays (its perk)"; continue; }
                knocked.Add(slot.Index);
                r.IncomeDelta = slot.Pays ? -Math.Max(0, slot.Income) : 0;
            }
            if (s.Prayers)
                foreach (var slot in w.Slots) if (slot.IsShrine && slot.Level >= 1) knocked.Add(slot.Index);
            if (s.Wisdom is (var wr, var each))
            {
                int gold = wr?.Action.Amount ?? each * lostByPlan;
                if (wr != null) wr.Wisdom = gold;
                extra += gold;
            }
            // God of Destruction: what's destroyed tonight isn't revived at dawn and is knocked out again next night.
            if (w.DestructionGod)
            {
                foreach (int i in s.Carry) if (!w.Slots[i].NeverDenied) knocked.Add(i);
                // Tonight's live knock-outs (the night in progress) carry over too.
                if (w.DawnPending && night == w.FirstDay - 1)
                    foreach (var slot in w.Slots) if (slot.KnockedOut && slot.IsBuilding) destroyed.Add(slot.Index);
                s.Carry = destroyed;
            }
            w.Gold += extra;
            return extra;
        }

        private static void Dawn(State s, int night, EconResult result, HashSet<int> knocked, out int buildings, out int treasure)
        {
            var w = s.World;
            buildings = 0;
            foreach (var slot in w.Slots)
            {
                // OnDawn_BeforeSunrise clears harvestedToday and temple locks; the harvest sets it again.
                slot.HarvestedToday = false;
                slot.DownToday = knocked.Contains(slot.Index);
                slot.Busy = false;
                if (slot.Level < 1 || !slot.Pays || slot.Income <= 0 || knocked.Contains(slot.Index)) continue;
                buildings += slot.Income;
                slot.HarvestedToday = true;
                int i = night - result.FirstDawn;
                if (i >= 0 && i < result.Pay[slot.Index].Length) result.Pay[slot.Index][i] += slot.Income;
            }
            w.Gold += buildings;
            foreach (var slot in w.Slots)
            {
                bool ko = knocked.Contains(slot.Index);
                foreach (var m in slot.Modifiers)
                {
                    switch (m.Kind)
                    {
                        case EconModifierKind.Mine:
                            if (slot.Level <= 0) break;
                            if (ko) { if (m.Sustainable) slot.Income = m.SustainableReset; }
                            else slot.Income = Math.Max(slot.Income - m.Step, m.Sustainable ? m.SustainableMin : m.Min);
                            break;
                        case EconModifierKind.Harbour:
                            if (slot.Level <= 0 || ko) break;
                            slot.Income = Math.Min(slot.Income + m.Step, m.Max);
                            slot.HarbourField = slot.Income;
                            break;
                        case EconModifierKind.Daily:
                            if (slot.Active) slot.Income++;
                            break;
                    }
                }
            }
            treasure = 0;
            if (w.TreasureHunter && night < w.LastNight)
            {
                int wave = night - 1, count = w.LastNight;
                treasure = wave == count - 4 ? w.Treasure1 : wave == count - 3 ? w.Treasure2 : wave == count - 2 ? w.Treasure3 : 0;
                w.Gold += treasure;
            }
        }

        private static EconSlot Resolve(EconWorld w, string id)
        {
            var slot = w.Find(id);
            return slot != null && slot.Owner >= 0 ? w.Slots[slot.Owner] : slot;
        }

        private static ActionResult Apply(State s, EconAction a)
        {
            var w = s.World;
            var slot = Resolve(w, a.Slot);
            var r = new ActionResult { Action = a, Slot = slot };
            if (slot == null) return Block(r, "Not on this map");
            if (slot.Level >= a.Level)
            {
                r.Status = ActionStatus.AlreadyDone;
                r.Reason = a.Day == w.FirstDay ? "Done" : $"Already level {slot.Level} by then";
                return r;
            }
            if (slot.Level < a.Level - 1) return Block(r, a.Level - 1 <= 1 ? "Build it first" : $"Needs level {a.Level - 1} first");
            if (!slot.Active && !s.IgnoreRequirements)
            {
                var act = slot.Activator >= 0 ? w.Slots[slot.Activator] : null;
                return Block(r, act != null ? $"Unlocks when {act.TypeName} reaches level {slot.ActivatorLevel + 1}" : "Not unlocked");
            }
            var step = slot.Level < slot.Steps.Count ? slot.Steps[slot.Level] : null;
            if (step == null) return Block(r, "Already at its highest level");
            if (step.Disabled) return Block(r, "Not available in this mode");
            if (slot.Busy) return Block(r, "Busy until the next dawn (its last upgrade is in effect)");
            if (!s.CanUpgrade(slot))
            {
                var root = w.Slots[slot.Root];
                return Block(r, $"Needs {root.TypeName} level {slot.Level + slot.RootLevelDifference + 1}");
            }
            int branchIndex = BranchIndex(step, a.Choice);
            var branch = step.Branches.Count > 0 ? step.Branches[branchIndex] : null;
            r.Branch = branch;
            if (branch != null && branch.Locked && !s.IgnoreRequirements) return Block(r, $"{branch.Name} is locked in this run");
            r.Cost = step.Cost;
            if (w.Gold < r.Cost)
            {
                r.Status = ActionStatus.Short;
                r.Shortfall = r.Cost - Math.Max(0, w.Gold);
                r.Reason = $"Short by {r.Shortfall}";
            }
            w.Gold -= r.Cost;
            int before = s.FamilyIncome(slot);
            s.StepUp(slot, branchIndex, byPlayer: true);
            r.IncomeDelta = s.FamilyIncome(slot) - before;
            if (branch != null && branch.Prayers) s.Prayers = true;
            if (branch != null && branch.IsWisdom) s.Wisdom = (r, branch.WisdomGold);
            return r;
        }

        private static ActionResult Block(ActionResult r, string reason)
        {
            r.Status = ActionStatus.Blocked;
            r.Reason = reason;
            return r;
        }

        /// <summary>The step's branch for a saved choice key (the first one if it's gone).</summary>
        public static int BranchIndex(EconStep step, string key)
        {
            if (step == null || step.Branches.Count == 0) return 0;
            int i = key == null ? -1 : step.Branches.FindIndex(b => b.Key == key);
            if (i >= 0) return i;
            i = step.Branches.FindIndex(b => !b.Locked);
            return i >= 0 ? i : 0;
        }

        // Payback: the plan with this step (and nothing later on the same slot) against the plan without it,
        // counting only what this slot and those following it pay.
        private static void Payback(EconWorld world, List<EconAction> ordered, ActionResult r, bool guild)
        {
            var slot = r.Slot;
            int at = ordered.IndexOf(r.Action);
            bool Later(EconAction a, int i) => i > at && a.IsStep && Resolve(world, a.Slot)?.Index == slot.Index;
            var with = ordered.Where((a, i) => !Later(a, i)).ToList();
            var without = with.Where(a => a != r.Action).ToList();
            var a1 = Simulate(world, with, guild);
            var a0 = Simulate(world, without, guild);
            var family = world.Slots.Where(s => s.Index == slot.Index || s.Owner == slot.Index).Select(s => s.Index).ToList();
            int total = 0;
            for (int dawn = r.Action.Day; dawn <= world.LastNight; dawn++)
            {
                foreach (int f in family) total += a1.PayOf(f, dawn) - a0.PayOf(f, dawn);
                if (r.PaybackDawn == null && r.Cost > 0 && total >= r.Cost) r.PaybackDawn = dawn;
            }
            r.ExtraIncome = total;
        }

        private sealed class State(EconWorld world, bool guild)
        {
            public readonly EconWorld World = world;
            public bool IgnoreRequirements;
            /// <summary>The day being played (null from dusk on), for gold harvested by day.</summary>
            public DayResult Today;
            public int TodayPay;
            public EconResult Result;
            /// <summary>Temple prayers / wisdom bought today.</summary>
            public bool Prayers;
            public (ActionResult, int)? Wisdom;
            /// <summary>God of Destruction: buildings destroyed last night, knocked out again tonight.</summary>
            public HashSet<int> Carry = [];

            public bool CanUpgrade(EconSlot slot)
            {
                if (slot.Owner >= 0) return CanUpgrade(World.Slots[slot.Owner]);
                if (slot.Level >= slot.Steps.Count || slot.Steps[slot.Level].Disabled) return false;
                if (IgnoreRequirements) return true;
                var root = World.Slots[slot.Root];
                if (root.Level <= slot.Level + slot.RootLevelDifference) return slot.Level == 0;
                return true;
            }

            public int FamilyIncome(EconSlot slot)
            {
                int total = slot.Pays ? slot.Income : 0;
                foreach (var s in World.Slots) if (s.Owner == slot.Index && s.Pays) total += s.Income;
                return total;
            }

            public void StepUp(EconSlot slot, int branchIndex, bool byPlayer)
            {
                // Slots following this one's tree step up first, on their own tree (TryToBuildOrUpgradeAndPay).
                foreach (var dep in World.Slots)
                {
                    if (dep.Owner != slot.Index || dep.Level >= dep.Steps.Count) continue;
                    var depStep = dep.Steps[dep.Level];
                    dep.Income += depStep.Branches.Count > 0 ? depStep.Branches[0].IncomeChange : 0;
                    dep.Applied.Add(0);
                    dep.Level++;
                    dep.Active = true;
                    // Their own BuildComplete never runs, so one that didn't pay at dawn (nothing to pay then) can be
                    // harvested now, and the night horn makes sure it is before dusk.
                    if (Today != null && dep.Pays && !dep.HarvestedToday && !dep.DownToday && dep.Income > 0)
                    {
                        dep.HarvestedToday = true;
                        World.Gold += dep.Income;
                        Today.Harvest += dep.Income;
                        if (TodayPay >= 0 && TodayPay < Result.Pay[dep.Index].Length) Result.Pay[dep.Index][TodayPay] += dep.Income;
                    }
                }
                var step = slot.Steps[slot.Level];
                var branch = step.Branches.Count > 0 ? step.Branches[Math.Min(branchIndex, step.Branches.Count - 1)] : null;
                int change = branch?.IncomeChange ?? 0;
                bool harbour = slot.Modifiers.Any(m => m.Kind == EconModifierKind.Harbour);
                if (harbour && slot.Level == 1 && branchIndex == 0) change = slot.HarbourField;
                if (branch != null && branch.DoublesHarbour)
                {
                    foreach (var m in slot.Modifiers.Where(m => m.Kind == EconModifierKind.Harbour))
                    {
                        m.Max *= 2;
                        m.Step *= 2;
                        m.Doubled = true;
                    }
                }
                bool built = slot.Level == 0;
                slot.Level++;
                slot.Applied.Add(branchIndex);
                slot.Income += change;
                // The player's own build marks the building harvested for the day (BuildingInteractor.BuildComplete).
                if (byPlayer) slot.HarvestedToday = true;
                if (branch != null && branch.BusyUntilDawn) slot.Busy = true;
                if (built && slot.IsHouse && !World.HouseOrder.Contains(slot.Index)) World.HouseOrder.Add(slot.Index);

                foreach (var child in World.Slots)
                    if (child.Activator == slot.Index && !child.Active && slot.Level > child.ActivatorLevel) child.Active = true;

                // Each CaslteUpReciever discounts on its own; choosing Castle Up discounts once per tagged object.
                if (World.CastleUpActive)
                    for (int i = 0; i < slot.CastleUpReceivers; i++) Discount(slot);
                if (branch != null && branch.StartsCastleUp && !World.CastleUpActive)
                {
                    World.CastleUpActive = true;
                    foreach (var s in World.Slots)
                        for (int i = 0; i < s.CastleUpTags; i++) Discount(s);
                }
                if (branch != null && branch.StartsGuild && guild && !World.GuildActive)
                {
                    World.GuildActive = true;
                    if (!World.GuildUsed)
                    {
                        World.GuildUsed = true;
                        GuildUpgrade();
                    }
                }
                if (built && slot.OutpostTowers > 0 && World.Outpost) OutpostTowers(slot);
                // Improved Plow (Mill_ImprovedPlow.OnEnable): +1 on the tree's steps 2 and 3, after this step's own change.
                if (branch != null && branch.PlowTree >= 0 && branch.PlowTree < World.Slots.Count)
                {
                    var tree = World.Slots[branch.PlowTree];
                    for (int i = 1; i <= 2 && i < tree.Steps.Count; i++)
                        foreach (var b in tree.Steps[i].Branches) b.IncomeChange++;
                }
            }

            // OutpostPerk.BuildFreeTowers: the farthest of the slot's tower children, built for free if not built.
            private void OutpostTowers(EconSlot outpost)
            {
                var towers = World.Slots.Where(c => c.Activator == outpost.Index && c.ObjectName.Contains("Tower"))
                    .OrderByDescending(c => (c.MapX - outpost.MapX) * (c.MapX - outpost.MapX) + (c.MapY - outpost.MapY) * (c.MapY - outpost.MapY))
                    .Take(outpost.OutpostTowers).ToList();
                foreach (var t in towers)
                {
                    if (t.Level != 0) continue;
                    t.Active = true;
                    if (CanUpgrade(t)) StepUp(t, 0, byPlayer: false);
                }
            }

            // UpgradeCastleUp.DecreasePriceOfWallOrTower: only while the slot can be upgraded (the cost setter's guard).
            private void Discount(EconSlot slot)
            {
                if (slot.Owner >= 0 || !CanUpgrade(slot)) return;
                if (slot.Level == 1) slot.Steps[1].Cost = Math.Max(1, slot.Steps[1].Cost - World.CastleUpDiscount2);
                else if (slot.Level == 2) slot.Steps[2].Cost = Math.Max(1, slot.Steps[2].Cost - World.CastleUpDiscount3);
            }

            // UpgradeBuildersGuild.UpgradeAHouse: the first house at level 1 in its list, if it can be upgraded.
            public void GuildUpgrade()
            {
                foreach (int i in World.HouseOrder)
                {
                    var house = World.Slots[i];
                    if (house.Level != 1) continue;
                    if (CanUpgrade(house) && house.Active) StepUp(house, 0, byPlayer: false);
                    return;
                }
            }
        }
    }

    /// <summary>Perk what-ifs: the world as it would be with some perks swapped in or out.</summary>
    public static class EconPerks
    {
        /// <summary>The world with <paramref name="changes"/> (perk key -> equipped) applied; the same world if none
        /// differ from what's equipped. <paramref name="notes"/> gets what couldn't be applied.</summary>
        public static EconWorld Apply(EconWorld world, IDictionary<string, bool> changes, List<string> notes = null)
        {
            if (changes == null || changes.Count == 0) return world;
            bool Differs(string key, bool now) => key != null && changes.TryGetValue(key, out bool v) && v != now;
            if (!world.Perks.Any(p => Differs(p.Key, p.Equipped))
                && !world.Slots.Any(s => s.PerkMods.Any(m => Differs(m.Key, m.Applied)) || s.Modifiers.Any(m => Differs(m.PerkKey, m.Kind == EconModifierKind.Mine ? m.Sustainable : m.PerkOn))))
                return world;

            var w = world.Clone();
            bool On(EconPerk p) => changes.TryGetValue(p.Key, out bool v) ? v : p.Equipped;
            bool waves = false;
            foreach (var p in w.Perks)
            {
                bool on = On(p);
                if (on == p.Equipped) continue;
                int sign = on ? 1 : -1;
                switch (p.Kind)
                {
                    case EconPerkKind.Interest:
                        w.Interest = on;
                        w.InterestEach = p.Each;
                        w.InterestMax = p.Max;
                        break;
                    case EconPerkKind.TreasureHunter:
                        w.TreasureHunter = on;
                        w.Treasure1 = p.T1;
                        w.Treasure2 = p.T2;
                        w.Treasure3 = p.T3;
                        break;
                    case EconPerkKind.RoyalMint:
                        if (w.StartOfRun) w.Gold += sign * p.Amount;
                        else notes?.Add($"{p.Name} only adds gold at the start");
                        break;
                    case EconPerkKind.Loan:
                        if (w.StartOfRun) w.Gold += sign * p.Amount;
                        else notes?.Add($"{p.Name}'s gold only comes at the start; its cost to the drops still counts");
                        waves = true;
                        break;
                    case EconPerkKind.RatGod:
                        waves = true;
                        break;
                    case EconPerkKind.DestructionGod:
                        w.DestructionGod = on;
                        break;
                    case EconPerkKind.Outpost:
                        w.Outpost = on;
                        break;
                }
            }
            if (waves)
            {
                if (w.SpawnGold == null) notes?.Add("The waves' gold before perks isn't known, so Rat God and Loan can't be swapped");
                else
                {
                    var rat = w.Perks.FirstOrDefault(p => p.Kind == EconPerkKind.RatGod);
                    var loan = w.Perks.FirstOrDefault(p => p.Kind == EconPerkKind.Loan);
                    var gold = WaveGold(w.SpawnGold, rat != null && On(rat) ? rat.Factor : (float?)null, loan != null && On(loan) ? loan.Amount2 : 0);
                    // Waves before tonight's are over; tonight's remaining gold is read live.
                    for (int i = Math.Max(0, w.FirstDay - 1); i < gold.Length && i < w.WaveGold.Length; i++) w.WaveGold[i] = gold[i];
                }
            }
            foreach (var slot in w.Slots)
            {
                foreach (var m in slot.Modifiers)
                {
                    if (m.PerkKey == null || !changes.TryGetValue(m.PerkKey, out bool on)) continue;
                    if (m.Kind == EconModifierKind.Mine)
                    {
                        m.Sustainable = on;
                        // As if it had been on from the start: the mine wouldn't have run below the higher floor.
                        if (on && slot.Level >= 1) slot.Income = Math.Max(slot.Income, m.SustainableMin);
                    }
                    else if (m.Kind == EconModifierKind.Harbour && on != m.PerkOn)
                    {
                        int boats = m.PerkBoats * (m.Doubled ? 2 : 1);
                        m.Max += on ? boats : -boats;
                        m.PerkOn = on;
                    }
                }
                foreach (var pm in slot.PerkMods)
                {
                    if (!changes.TryGetValue(pm.Key, out bool on) || on == pm.Applied) continue;
                    int sign = on ? 1 : -1;
                    pm.Applied = on;
                    if (pm.RootDifference is int without)
                    {
                        slot.RootLevelDifference = on ? -100 : without;
                        continue;
                    }
                    var tree = w.Slots[pm.Tree];
                    for (int i = pm.Pending ? tree.Level : 0; i < tree.Steps.Count; i++)
                    {
                        int cost = i < pm.Cost.Length && !(i < pm.CostSkip.Length && pm.CostSkip[i]) ? pm.Cost[i] : 0;
                        int income = i < pm.Income.Length ? pm.Income[i] : 0;
                        tree.Steps[i].Cost = Math.Max(0, tree.Steps[i].Cost + sign * cost);
                        foreach (var b in tree.Steps[i].Branches) b.IncomeChange += sign * income;
                        // Taken steps: as if the perk had been there from the start.
                        if (i < tree.Level) tree.Income += sign * income;
                    }
                }
            }
            return w;
        }

        /// <summary>Each wave's gold from its spawns' coins before perks: Rat God scales every spawn's coins (rounding
        /// up, carrying the remainder), then Loan takes its interest out of the earliest spawns (EnemySpawner.Start).</summary>
        public static int[] WaveGold(int[][] spawns, float? ratFactor, int loanInterest)
        {
            var coins = spawns.Select(w => (int[])w.Clone()).ToArray();
            if (ratFactor is float f)
            {
                float carry = 0f;
                foreach (var wave in coins)
                    for (int i = 0; i < wave.Length; i++)
                    {
                        float v = wave[i] * f + carry;
                        wave[i] = (int)Math.Ceiling(Math.Max(0f, v));
                        carry = v - wave[i];
                    }
            }
            int left = loanInterest;
            foreach (var wave in coins)
                for (int i = 0; i < wave.Length && left > 0; i++)
                {
                    int take = Math.Min(wave[i], left);
                    wave[i] -= take;
                    left -= take;
                }
            return coins.Select(w => w.Sum()).ToArray();
        }
    }
}
