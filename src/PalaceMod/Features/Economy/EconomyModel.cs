using System;
using System.Collections.Generic;
using System.Linq;

/*
    The economy planner's model and simulation. Game-independent (no UnityEngine), so it's unit tested in
    tests/PalaceMod.Tests; EconomySnapshot fills an EconWorld from the live level.

    Timeline. "Day N" is the day before night N; "dawn N" ends night N. The game's order, which the simulation follows:
      day N      the player builds and upgrades (EconAction, in the plan's order). Gold can't be earned by day.
      dusk N     OnDuskEarly: Builder's Guild upgrades the first level-1 house for free.
                 OnDusk: Interest pays min(max, ceil(gold / each)) (InterestPerk.InterestAmount, on Balance).
      night N    enemies drop the wave's gold (WaveInfo.goldReward; Loan and Rat God are already folded into it).
      dawn N     every built building with an interactor pays its GoldIncome unless knocked out tonight
                 (BuildingInteractor.OnDawn_AfterSunrise -> Harvest), then its income modifiers run:
                   mine     -incomeReductionPerTurn, floored (higher floor with Sustainable Mining); knocked out with
                            Sustainable Mining: reset instead
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
      - slots that follow their activator's tree (ActivatorUpgradesThis, e.g. a mill's fields) step up with it, taking
        their own next step's first branch (the game picks one at random; they have one);
      - the branch's income change is added; the slot's children unlock once it passes their activatorLevel;
      - Castle Up (UpgradeCastleUp) cuts walls' and towers' next upgrade costs when chosen, and again whenever one of
        them reaches level 1 or 2 afterwards (CaslteUpReciever);
      - Builder's Guild (UpgradeBuildersGuild) upgrades a house when chosen and at every dusk after.

    Not modelled: future knock-outs (a knocked-out building pays nothing that dawn), coins left uncollected (interest
    counts only gold in hand), shrines unlocking from nearby deaths and Temple prayers, God of Choice locks on slots
    that aren't unlocked yet, and which house Builder's Guild picks among houses built in the plan (the game uses its
    tag list order; planned houses are assumed to join the end).
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
    }

    public sealed class EconStep
    {
        public int Cost;
        public bool Disabled;
        /// <summary>The game's description of a one-branch step, if it has one.</summary>
        public string Text;
        public List<EconBranch> Branches = [];
        public bool IsChoice => Branches.Count > 1;
    }

    /// <summary>One build slot: its upgrade tree and its state now.</summary>
    public sealed class EconSlot
    {
        public int Index;
        /// <summary>Stable id across sessions (the slot's SaveLoadEntity GUID); saved in plans.</summary>
        public string Id;
        public string TypeName;
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
        public bool IsHouse, IsWallOrTower, IsCastle;
        public List<EconModifier> Modifiers = [];

        // State now.
        public int Level;
        public int Income;
        public bool Active;
        public bool KnockedOut;
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
        public bool Interest;
        public int InterestEach = 3, InterestMax = 20;
        public bool TreasureHunter;
        public int Treasure1 = 10, Treasure2 = 20, Treasure3 = 30;
        public bool GuildActive, GuildUsed;
        /// <summary>Houses in the order Builder's Guild looks at them.</summary>
        public List<int> HouseOrder = [];
        public bool CastleUpActive;
        public int CastleUpDiscount2 = 2, CastleUpDiscount3 = 4;

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
                // Costs change during a run (Castle Up), so steps are copied; branches are read-only.
                c.Steps = s.Steps.Select(st => new EconStep { Cost = st.Cost, Disabled = st.Disabled, Text = st.Text, Branches = st.Branches }).ToList();
                return c;
            }).ToList();
            return w;
        }
    }

    /// <summary>One planned build or upgrade: on <see cref="Day"/>, take <see cref="Slot"/> to <see cref="Level"/>.</summary>
    public sealed class EconAction
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public int Day;
        public string Slot;
        public int Level;
        /// <summary>Branch key (choice name); null for one-branch steps.</summary>
        public string Choice;

        public EconAction Copy() => (EconAction)MemberwiseClone();
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
        /// <summary>Change in what the slot (and slots following it) pays at the next dawn.</summary>
        public int IncomeDelta;
        /// <summary>Gold missing when it was paid (Short).</summary>
        public int Shortfall;
        /// <summary>Dawn by which this step's extra income has repaid its cost, or null.</summary>
        public int? PaybackDawn;
        /// <summary>Extra income the step brings by the last dawn.</summary>
        public int ExtraIncome;
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
        public int AfterSpend => Start - Spent;
        public int Interest;
        public int Drops;
        /// <summary>What buildings pay at dawn (the night's knock-outs aside).</summary>
        public int Buildings;
        public int Treasure;
        public int Income => Interest + Drops + Buildings + Treasure;
        /// <summary>Gold after dawn (the next day's Start).</summary>
        public int AtDawn;
        public int Shortfall => Math.Max(0, -AfterSpend);
        public int Actions;
    }

    public sealed class EconResult
    {
        public readonly List<DayResult> Days = [];
        public readonly List<ActionResult> Actions = [];
        /// <summary>The pending dawn (night in progress): what it pays, before the first plannable day.</summary>
        public int PendingDrops, PendingBuildings, PendingTreasure;
        public int EndGold;
        public int TotalSpent => Days.Sum(d => d.Spent);
        public int TotalIncome => Days.Sum(d => d.Income) + PendingDrops + PendingBuildings + PendingTreasure;
        /// <summary>Building pay at the next dawn and at the last one.</summary>
        public int NextDawnBuildings, LastDawnBuildings;
        public DayResult FirstShortDay => Days.FirstOrDefault(d => d.Shortfall > 0);
        /// <summary>Pay per slot per dawn, [slot][dawn - (FirstDay - 1)].</summary>
        internal int[][] Pay;
        internal int FirstDawn;
        /// <summary>The world as it is after the last dawn.</summary>
        public EconWorld Final;

        public int PayOf(int slot, int dawn) =>
            Pay == null || slot < 0 || slot >= Pay.Length || dawn - FirstDawn < 0 || dawn - FirstDawn >= Pay[slot].Length ? 0 : Pay[slot][dawn - FirstDawn];
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
                    if (!r.Counts || r.Slot == null) continue;
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

        /// <summary>The slot's state on <paramref name="day"/> after that day's planned actions (for the picker).</summary>
        public static EconWorld StateOn(EconWorld world, IList<EconAction> actions, int day)
        {
            var before = Ordered(actions).Where(a => a.Day <= day).ToList();
            var r = Simulate(world, before, guild: true, stopAfterDay: day);
            return r.Final;
        }

        private static EconResult Simulate(EconWorld start, IList<EconAction> actions, bool guild, int stopAfterDay = int.MaxValue)
        {
            var w = start.Clone();
            if (!guild) w.GuildActive = false;
            var s = new State(w, guild);
            var result = new EconResult { FirstDawn = w.FirstDay - 1 };
            int dawns = Math.Max(0, w.LastNight - (w.FirstDay - 1) + 1);
            result.Pay = w.Slots.Select(_ => new int[dawns]).ToArray();
            var ordered = Ordered(actions);

            foreach (var a in ordered.Where(a => a.Day < w.FirstDay))
                result.Actions.Add(Past(w, a));

            if (w.DawnPending)
            {
                int n = w.FirstDay - 1;
                w.Gold += w.TonightGold;
                result.PendingDrops = w.TonightGold;
                Dawn(s, n, result, out int buildings, out int treasure);
                result.PendingBuildings = buildings;
                result.PendingTreasure = treasure;
                result.NextDawnBuildings = buildings;
            }

            for (int day = w.FirstDay; day <= w.LastNight && day <= stopAfterDay; day++)
            {
                var d = new DayResult { Day = day, Start = w.Gold };
                foreach (var a in ordered.Where(a => a.Day == day))
                {
                    var r = Apply(s, a);
                    result.Actions.Add(r);
                    if (r.Counts) { d.Spent += r.Cost; d.Actions++; }
                }
                if (day == stopAfterDay) break;
                // Dusk.
                if (guild && w.GuildActive) s.GuildUpgrade();
                if (w.Interest)
                {
                    d.Interest = Math.Min(w.InterestMax, (int)Math.Ceiling(Math.Max(0, w.Gold) / (double)Math.Max(1, w.InterestEach)));
                    w.Gold += d.Interest;
                }
                // Night.
                d.Drops = day - 1 < w.WaveGold.Length && day >= 1 ? w.WaveGold[day - 1] : 0;
                w.Gold += d.Drops;
                // Dawn.
                Dawn(s, day, result, out d.Buildings, out d.Treasure);
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
            var index = actions.Select((a, i) => (a, i)).ToDictionary(t => t.a, t => t.i);
            result.Actions.Sort((x, y) => index[x.Action].CompareTo(index[y.Action]));
            return result;
        }

        private static ActionResult Past(EconWorld w, EconAction a)
        {
            var slot = Resolve(w, a.Slot);
            bool done = slot != null && slot.Level >= a.Level;
            return new ActionResult
            {
                Action = a, Slot = slot, Status = done ? ActionStatus.AlreadyDone : ActionStatus.Past,
                Reason = done ? "Done" : $"Day {a.Day} is over",
            };
        }

        private static void Dawn(State s, int night, EconResult result, out int buildings, out int treasure)
        {
            var w = s.World;
            bool knockouts = w.DawnPending && night == w.FirstDay - 1;
            buildings = 0;
            foreach (var slot in w.Slots)
            {
                if (slot.Level < 1 || !slot.Pays || slot.Income <= 0 || (knockouts && slot.KnockedOut)) continue;
                buildings += slot.Income;
                int i = night - result.FirstDawn;
                if (i >= 0 && i < result.Pay[slot.Index].Length) result.Pay[slot.Index][i] = slot.Income;
            }
            w.Gold += buildings;
            foreach (var slot in w.Slots)
            {
                bool ko = knockouts && slot.KnockedOut;
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
            if (!slot.Active)
            {
                var act = slot.Activator >= 0 ? w.Slots[slot.Activator] : null;
                return Block(r, act != null ? $"Unlocks when {act.TypeName} reaches level {slot.ActivatorLevel + 1}" : "Not unlocked");
            }
            var step = slot.Level < slot.Steps.Count ? slot.Steps[slot.Level] : null;
            if (step == null) return Block(r, "Already at its highest level");
            if (step.Disabled) return Block(r, "Not available in this mode");
            if (!s.CanUpgrade(slot))
            {
                var root = w.Slots[slot.Root];
                return Block(r, $"Needs {root.TypeName} level {slot.Level + slot.RootLevelDifference + 1}");
            }
            int branchIndex = BranchIndex(step, a.Choice);
            var branch = step.Branches.Count > 0 ? step.Branches[branchIndex] : null;
            r.Branch = branch;
            if (branch != null && branch.Locked) return Block(r, $"{branch.Name} is locked in this run");
            r.Cost = step.Cost;
            if (w.Gold < r.Cost)
            {
                r.Status = ActionStatus.Short;
                r.Shortfall = r.Cost - Math.Max(0, w.Gold);
                r.Reason = $"Short by {r.Shortfall}";
            }
            w.Gold -= r.Cost;
            int before = s.FamilyIncome(slot);
            s.StepUp(slot, branchIndex);
            r.IncomeDelta = s.FamilyIncome(slot) - before;
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
            bool Later(EconAction a, int i) => i > at && Resolve(world, a.Slot)?.Index == slot.Index;
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

            public int LevelOf(EconSlot slot) => slot.Owner >= 0 ? World.Slots[slot.Owner].Level : slot.Level;

            public bool CanUpgrade(EconSlot slot)
            {
                if (slot.Owner >= 0) return CanUpgrade(World.Slots[slot.Owner]);
                if (slot.Level >= slot.Steps.Count || slot.Steps[slot.Level].Disabled) return false;
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

            public void StepUp(EconSlot slot, int branchIndex)
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
                    }
                }
                bool built = slot.Level == 0;
                slot.Level++;
                slot.Applied.Add(branchIndex);
                slot.Income += change;
                if (built && slot.IsHouse && !World.HouseOrder.Contains(slot.Index)) World.HouseOrder.Add(slot.Index);

                foreach (var child in World.Slots)
                    if (child.Activator == slot.Index && !child.Active && slot.Level > child.ActivatorLevel) child.Active = true;

                if (World.CastleUpActive && slot.IsWallOrTower) Discount(slot);
                if (branch != null && branch.StartsCastleUp && !World.CastleUpActive)
                {
                    World.CastleUpActive = true;
                    foreach (var s in World.Slots) if (s.IsWallOrTower) Discount(s);
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
                    if (CanUpgrade(house) && house.Active) StepUp(house, 0);
                    return;
                }
            }
        }
    }
}
