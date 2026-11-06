using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using PalaceMod.Economy;

/*
    Economy planner: the Economy tab of Run Analytics. The player lays out builds and upgrades on the days ahead ("on
    day 4, build this house; on day 6, take the castle to level 3 with Builder's Guild") and sees the gold that leaves
    at every dawn up to the level's last night, against doing nothing or another plan, with shortfalls and paybacks.

      EconomyModel.cs     the simulation (game-independent, unit tested)
      EconomySnapshot.cs  reads the live level into it
      PlannerStore.cs     plans, saved per level under BepInEx/PalaceMod/EconomyPlanner/
      PlannerPages.cs     the tab, the building picker, the step editor and the plans list
      PlannerVisuals.cs   the graph and the map

    Read-only planning: it never changes the run, so it has no Enabled switch and doesn't mark gameplay impact.
    Accuracy check: the snapshot's projection of the coming dawns with nothing planned must match IncomeCalculator
    (and slot costs NextUpgradeOrBuildCost); mismatches are logged as warnings, and `econ check` lists them.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager : BaseManager
    {
        public override string FeatureSection => "Economy planner";
        public override string FeatureDescription =>
            "Run Analytics > Economy: plan builds and upgrades for the rest of the level and see the gold they leave you at every dawn.";
        protected override bool HasEnabledSwitch => false;

        internal static string Folder => Path.Combine(Paths.BepInExRootPath, "PalaceMod", "EconomyPlanner");

        private Keybind openKey;
        private PlannerFile file;
        private EconSnapshot snap;
        private float snapTime = float.NaN;
        private string checkedLevel;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            openKey = Keybinds.Register(FeatureSection, "open", "Open economy planner", KeyboardShortcut.Empty,
                "Opens Run Analytics on the Economy tab, to plan builds and upgrades.");
            DevCommands.Register("econ", Usage, Command);
            BuildFrames();
        }

        public override void Update()
        {
            if (openKey.Down && EconomySnapshot.Available) Open();
        }

        /// <summary>Opens Run Analytics on the planner.</summary>
        internal void Open() => Feature<AnalyticsManager>.Instance?.OpenTab(AnalyticsManager.EconomyTab);

        // ---- Data -------------------------------------------------------------------------------------------------

        /// <summary>The level as it is now. Reused while the game is paused (Time.time stands still), so a menu
        /// rebuilding its page doesn't read the scene again.</summary>
        internal EconSnapshot Snap(bool fresh = false)
        {
            if (!EconomySnapshot.Available) return null;
            var scene = EnemySpawner.instance.gameObject.scene;
            if (fresh || snap == null || snap.Scene != scene || snapTime != Time.time)
            {
                snap = EconomySnapshot.Take();
                snapTime = Time.time;
                if (snap != null && checkedLevel != $"{snap.World.Level}:{snap.World.FirstDay}:{snap.World.DawnPending}")
                {
                    // Once per day (and per night): the projection of doing nothing has to agree with the game.
                    checkedLevel = $"{snap.World.Level}:{snap.World.FirstDay}:{snap.World.DawnPending}";
                    var problems = Check(snap);
                    if (problems.Count > 0) Log.Warn($"Planner doesn't match the game on {snap.World.Level} day {snap.World.FirstDay}: {string.Join("; ", problems)}");
                    else Log.Debug($"Planner matches the game on {snap.World.Level} day {snap.World.FirstDay}");
                }
            }
            return snap;
        }

        internal PlannerFile Plans(string level)
        {
            if (file != null && file.Level == level) return file;
            file = PlannerStore.Load(Folder, level, out string error);
            if (error != null)
            {
                Log.Warn($"Couldn't read the economy plans for {level} ({error}); starting fresh, the old file is kept as .bad");
                Notify.Show("Your economy plans for this map couldn't be read; started fresh (the old file is kept).", NotifyKind.Warning, key: "econ-load");
            }
            return file;
        }

        private void Save()
        {
            if (file == null) return;
            try { PlannerStore.Save(Folder, file); }
            catch (Exception e)
            {
                Log.Error("Couldn't save the economy plans", e);
                Notify.Show("Couldn't save your economy plans; see the log.", NotifyKind.Error, key: "econ-save");
            }
        }

        internal sealed class Projection
        {
            public EconSnapshot Snap;
            public EconWorld World => Snap.World;
            public PlannerFile File;
            public PlannerScenario Plan, Other;
            public EconResult Result, Compare, Baseline;
            public string OtherName => Other?.Name ?? "doing nothing";
        }

        internal Projection Project()
        {
            var s = Snap();
            if (s == null) return null;
            var f = Plans(s.World.Level);
            var p = new Projection { Snap = s, File = f, Plan = f.CurrentScenario, Other = f.CompareScenario };
            p.Result = EconSim.Run(s.World, p.Plan.Actions);
            p.Baseline = p.Plan.Actions.Count == 0 ? p.Result : EconSim.Run(s.World, [], paybacks: false);
            p.Compare = p.Other == null ? p.Baseline : EconSim.Run(s.World, p.Other.Actions, paybacks: false);
            return p;
        }

        // ---- Accuracy check ---------------------------------------------------------------------------------------

        /// <summary>Differences between the planner's view of "doing nothing" and the game's own numbers.</summary>
        internal List<string> Check(EconSnapshot s)
        {
            var problems = new List<string>();
            var w = s.World;
            var sim = EconSim.Run(w, [], paybacks: false, guild: false);
            var dawns = new List<int>();
            if (w.DawnPending) dawns.Add(sim.PendingBuildings);
            dawns.AddRange(sim.Days.Select(d => d.Buildings));
            // During sunrise IncomeCalculator ignores the night's knock-outs (it's day), the planner doesn't.
            bool sunrise = w.DawnPending && !IncomeCalculator.IsNight;
            for (int k = 0; k < dawns.Count && k < 8 && !sunrise; k++)
            {
                int game = IncomeCalculator.Total(k);
                if (game != dawns[k]) problems.Add($"dawn +{k} pays {dawns[k]}, IncomeCalculator says {game}");
            }
            var registered = new HashSet<BuildSlot>(TagManager.instance.playerBuildingInteractors.Where(i => i != null).Select(i => i.targetBuilding));
            foreach (var e in w.Slots)
            {
                var slot = s.Objects[e.Index];
                if (slot == null) continue;
                if (e.Level >= 1 && e.Pays != registered.Contains(slot) && slot.gameObject.activeInHierarchy)
                    problems.Add($"{e.Label} #{e.Index}: pays={e.Pays} but registered={registered.Contains(slot)}");
                if (e.IsDependent || !e.Active) continue;
                bool live = slot.CanBeUpgraded, mine = EconSim.CanUpgrade(w, e);
                if (live != mine) problems.Add($"{e.Label} #{e.Index}: can upgrade {mine}, game says {live}");
                else if (live && e.Steps[e.Level].Cost != slot.NextUpgradeOrBuildCost)
                    problems.Add($"{e.Label} #{e.Index}: costs {e.Steps[e.Level].Cost}, game says {slot.NextUpgradeOrBuildCost}");
            }
            return problems;
        }

        // ---- Dev command ------------------------------------------------------------------------------------------

        private const string Usage =
            "econ [open|check|dump [slot]|slots [all|income|text]|add <day> <slot> [level] [choice]|remove <n>|move <n> <day>|order <n> up|down|" +
            "level <n> <L>|choice <n> <key|#>|plan list|new [name]|copy|rename <name>|delete|use <n|name>|clear|compare <n|name|none>|picker [day]|editor <n>|plans]   economy planner";

        private string Command(string[] args)
        {
            var s = Snap(fresh: true) ?? throw new InvalidOperationException("not in a level");
            var w = s.World;
            var f = Plans(w.Level);
            var plan = f.CurrentScenario;
            string verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            int Int(int i) => args.Length > i ? int.Parse(args[i]) : throw new ArgumentException($"usage: {Usage}");
            EconAction Nth(int i)
            {
                var ordered = EconSim.Ordered(plan.Actions);
                int n = Int(i);
                if (n < 1 || n > ordered.Count) throw new ArgumentException($"the plan has {ordered.Count} steps");
                return ordered[n - 1];
            }
            void Done()
            {
                Save();
                hostDirty = true;
                RefreshOpen();
            }
            switch (verb)
            {
                case "":
                    return "\n" + Summary(Project());
                case "open":
                    Open();
                    return Feature<AnalyticsManager>.Instance?.FrameOpen == true ? "opened\n" + Summary(Project()) : throw new InvalidOperationException("didn't open; see the log");
                case "check":
                {
                    var problems = Check(s);
                    var sim = EconSim.Run(w, [], paybacks: false, guild: false);
                    string dawns = string.Join(", ", (w.DawnPending ? new[] { sim.PendingBuildings } : []).Concat(sim.Days.Select(d => d.Buildings)).Take(8));
                    string game = string.Join(", ", Enumerable.Range(0, Math.Min(8, sim.Days.Count + (w.DawnPending ? 1 : 0))).Select(k => IncomeCalculator.Total(k)));
                    return $"building pay per dawn, planner: {dawns}\n                    game:    {game}\n" +
                        (problems.Count == 0 ? "OK: matches IncomeCalculator and every slot's cost" : "MISMATCH:\n  " + string.Join("\n  ", problems)) +
                        (s.Notes.Count > 0 ? "\nnotes:\n  " + string.Join("\n  ", s.Notes) : "");
                }
                case "dump":
                    return "\n" + Dump(s, args.Length > 1 ? int.Parse(args[1]) : -1);
                case "slots":
                {
                    string filter = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "";
                    var lines = w.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0)
                        .Where(e => filter == "all" || (filter == "income" ? e.EverPays : filter == "" ? e.Active || e.Activator >= 0 : e.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Select(e => $"  #{e.Index,-3} {e.Label,-44} lv {e.Level}/{e.MaxLevel} income {e.Income,-3} " +
                            (e.Level < e.Steps.Count ? $"next {e.Steps[e.Level].Cost}{(EconSim.CanUpgrade(w, e) && e.Active ? "" : " (blocked)")}" : "max") + (e.Active ? "" : " [locked]"));
                    return "\n" + string.Join("\n", lines);
                }
                case "add":
                {
                    int day = Int(1);
                    var slot = SlotArg(w, args.Length > 2 ? args[2] : throw new ArgumentException("add <day> <slot> [level] [choice]"));
                    var state = EconSim.StateOn(w, plan.Actions, day).Slots[slot.Index];
                    int level = args.Length > 3 ? int.Parse(args[3]) : state.Level + 1;
                    var a = new EconAction { Day = day, Slot = slot.Id, Level = level, Choice = ChoiceArg(slot, level, args.Length > 4 ? args[4] : null) };
                    plan.Actions.Add(a);
                    Done();
                    return Describe(Project(), a);
                }
                case "remove":
                {
                    var a = Nth(1);
                    plan.Actions.Remove(a);
                    Done();
                    return $"removed; {plan.Actions.Count} steps left";
                }
                case "move":
                {
                    var a = Nth(1);
                    a.Day = Mathf.Clamp(Int(2), 1, w.LastNight);
                    Done();
                    return Describe(Project(), a);
                }
                case "order":
                {
                    var a = Nth(1);
                    if (!Reorder(plan, a, args.Length > 2 && args[2] == "up" ? -1 : 1)) return "nothing to swap with that day";
                    Done();
                    return "\n" + Summary(Project());
                }
                case "level":
                {
                    var a = Nth(1);
                    a.Level = Int(2);
                    Done();
                    return Describe(Project(), a);
                }
                case "choice":
                {
                    var a = Nth(1);
                    var slot = w.Find(a.Slot) ?? throw new InvalidOperationException("that step's building isn't on this map");
                    a.Choice = ChoiceArg(slot, a.Level, args.Length > 2 ? args[2] : throw new ArgumentException("choice <n> <key|#>"));
                    Done();
                    return Describe(Project(), a);
                }
                case "plan":
                    return PlanCommand(f, args.Skip(1).ToArray(), Done);
                case "compare":
                {
                    string which = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "none";
                    f.Compare = which == "none" ? null : ScenarioArg(f, which).Id;
                    f.Normalize();
                    Done();
                    return "\n" + Summary(Project());
                }
                case "picker":
                    Open();
                    OpenPicker(args.Length > 1 ? int.Parse(args[1]) : w.FirstDay, null);
                    return pickerFrame.IsOpen ? $"picker open on day {pickDay}, {pickerRows} buildings listed" : throw new InvalidOperationException("didn't open");
                case "editor":
                {
                    var a = Nth(1);
                    Open();
                    OpenEditor(a.Id);
                    return editorFrame.IsOpen ? "editor open\n" + Describe(Project(), a) : throw new InvalidOperationException("didn't open");
                }
                case "plans":
                    Open();
                    OpenPlans();
                    return plansFrame.IsOpen ? $"plans open, {f.Scenarios.Count} plans" : throw new InvalidOperationException("didn't open");
                default:
                    throw new ArgumentException($"usage: {Usage}");
            }
        }

        private string PlanCommand(PlannerFile f, string[] args, Action done)
        {
            var plan = f.CurrentScenario;
            string verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
            string rest = string.Join(" ", args.Skip(1));
            switch (verb)
            {
                case "list":
                    break;
                case "new":
                {
                    var s = new PlannerScenario { Name = f.FreeName(rest.Length > 0 ? rest : "Plan") };
                    f.Scenarios.Add(s);
                    f.Current = s.Id;
                    break;
                }
                case "copy":
                {
                    var s = plan.CopyAs(f.FreeName(plan.Name + " copy"));
                    f.Scenarios.Add(s);
                    f.Current = s.Id;
                    break;
                }
                case "rename":
                    if (rest.Length == 0) throw new ArgumentException("plan rename <name>");
                    plan.Name = rest;
                    break;
                case "delete":
                    DeletePlan(f, plan);
                    break;
                case "clear":
                    plan.Actions.Clear();
                    break;
                case "use":
                    f.Current = ScenarioArg(f, rest).Id;
                    break;
                default:
                    throw new ArgumentException("plan list|new [name]|copy|rename <name>|delete|use <n|name>|clear");
            }
            f.Normalize();
            if (verb != "list") done();
            return "\n" + string.Join("\n", f.Scenarios.Select((s, i) =>
                $"  {i + 1}. {(s.Id == f.Current ? "*" : " ")}{(s.Id == f.Compare ? "~" : " ")} {s.Name}: {s.Actions.Count} steps"));
        }

        private static void DeletePlan(PlannerFile f, PlannerScenario plan)
        {
            f.Scenarios.Remove(plan);
            if (f.Compare == plan.Id) f.Compare = null;
            f.Normalize();
            f.Current = f.Scenarios[0].Id;
        }

        private static PlannerScenario ScenarioArg(PlannerFile f, string which)
        {
            if (int.TryParse(which, out int n) && n >= 1 && n <= f.Scenarios.Count) return f.Scenarios[n - 1];
            return f.Scenarios.FirstOrDefault(s => string.Equals(s.Name, which, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"no plan \"{which}\"");
        }

        private static EconSlot SlotArg(EconWorld w, string arg)
        {
            string key = arg.TrimStart('#');
            if (int.TryParse(key, out int i) && i >= 0 && i < w.Slots.Count) return w.Slots[i];
            return w.Find(arg) ?? w.Slots.FirstOrDefault(s => s.Label.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException($"no slot \"{arg}\" (econ slots lists them)");
        }

        private static string ChoiceArg(EconSlot slot, int level, string arg)
        {
            if (level < 1 || level > slot.Steps.Count) return null;
            var step = slot.Steps[level - 1];
            if (!step.IsChoice) return null;
            if (arg == null) return step.Branches[EconSim.BranchIndex(step, null)].Key;
            if (int.TryParse(arg.TrimStart('#'), out int i) && i >= 0 && i < step.Branches.Count) return step.Branches[i].Key;
            var b = step.Branches.FirstOrDefault(x => string.Equals(x.Key, arg, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase));
            return b?.Key ?? throw new ArgumentException($"choices: {string.Join(", ", step.Branches.Select((x, n) => $"#{n} {x.Name}"))}");
        }

        /// <summary>Moves a step before or after its neighbour on the same day. False if there's none.</summary>
        internal static bool Reorder(PlannerScenario plan, EconAction a, int direction)
        {
            var sameDay = EconSim.Ordered(plan.Actions).Where(x => x.Day == a.Day).ToList();
            int at = sameDay.IndexOf(a), to = at + direction;
            if (at < 0 || to < 0 || to >= sameDay.Count) return false;
            var other = sameDay[to];
            int i = plan.Actions.IndexOf(a), j = plan.Actions.IndexOf(other);
            plan.Actions[i] = other;
            plan.Actions[j] = a;
            return true;
        }

        // ---- Text summaries (dev command) -------------------------------------------------------------------------

        private static string Summary(Projection p)
        {
            var w = p.World;
            var lines = new List<string>
            {
                $"{w.Level}: day {w.FirstDay} of {w.LastNight}{(w.DawnPending ? " (night/sunrise: dawn pending)" : "")}, gold {w.Gold}. Plan \"{p.Plan.Name}\" vs {p.OtherName}",
            };
            if (w.DawnPending)
                lines.Add($"  pending dawn {w.FirstDay - 1}: drops +{p.Result.PendingDrops} buildings +{p.Result.PendingBuildings} treasure +{p.Result.PendingTreasure}");
            lines.Add("  day  start  spent  int  drops  bldg  treas  dawn   vs");
            foreach (var d in p.Result.Days)
            {
                var c = p.Compare.Days.FirstOrDefault(x => x.Day == d.Day);
                lines.Add($"  {d.Day,3}  {d.Start,5}  {d.Spent,5}  {d.Interest,3}  {d.Drops,5}  {d.Buildings,4}  {d.Treasure,5}  {d.AtDawn,5}  {(c == null ? "" : Signed(d.AtDawn - c.AtDawn))}" +
                    (d.Shortfall > 0 ? $"  SHORT {d.Shortfall}" : ""));
            }
            lines.Add($"  end gold {p.Result.EndGold} ({Signed(p.Result.EndGold - p.Baseline.EndGold)} vs doing nothing" +
                (p.Other != null ? $", {Signed(p.Result.EndGold - p.Compare.EndGold)} vs {p.Other.Name}" : "") +
                $"); spent {p.Result.TotalSpent}; buildings pay {p.Result.LastDawnBuildings} at the last dawn");
            var ordered = EconSim.Ordered(p.Plan.Actions);
            for (int i = 0; i < ordered.Count; i++) lines.Add($"  {i + 1}. " + Describe(p, ordered[i]));
            return string.Join("\n", lines);
        }

        private static string Describe(Projection p, EconAction a)
        {
            var r = p.Result.Actions.FirstOrDefault(x => x.Action == a);
            if (r == null) return "?";
            return $"day {a.Day}: {Title(r)} — {r.Status}" + (r.Reason != null ? $" ({r.Reason})" : "") +
                (r.Counts ? $", cost {r.Cost}, income {Signed(r.IncomeDelta)}/dawn" + (r.IncomeDelta != 0 ? $", payback {(r.PaybackDawn is int d ? $"dawn {d}" : $"none ({r.ExtraIncome}/{r.Cost} by the end)")}" : "") : "");
        }

        private static string Dump(EconSnapshot s, int only)
        {
            var w = s.World;
            var lines = new List<string>
            {
                $"{w.Level}: gold {w.Gold}, first day {w.FirstDay}, last night {w.LastNight}, dawn pending {w.DawnPending} (tonight's gold {w.TonightGold})",
                $"  waves gold: {string.Join(" ", w.WaveGold)}",
                $"  interest {(w.Interest ? $"1 per {w.InterestEach}, max {w.InterestMax}" : "no")}, treasure hunter {(w.TreasureHunter ? $"{w.Treasure1}/{w.Treasure2}/{w.Treasure3}" : "no")}, " +
                $"guild {w.GuildActive}, castle up {w.CastleUpActive} (-{w.CastleUpDiscount2}/-{w.CastleUpDiscount3}), houses [{string.Join(",", w.HouseOrder)}]",
            };
            foreach (var e in w.Slots)
            {
                if (only >= 0 && e.Index != only) continue;
                string steps = string.Join(" | ", e.Steps.Select((st, i) => $"{i + 1}:{st.Cost}{(st.Disabled ? "x" : "")}[{string.Join(",", st.Branches.Select(b => $"{b.Key}{(b.IncomeChange != 0 ? Signed(b.IncomeChange) : "")}{(b.Locked ? " L" : "")}{(b.StartsGuild ? " G" : "")}{(b.StartsCastleUp ? " C" : "")}"))}]"));
                lines.Add($"  #{e.Index} {e.Label} id={e.Id} lv {e.Level} inc {e.Income} active {e.Active} pays {e.Pays} ko {e.KnockedOut} " +
                    $"owner {e.Owner} act {e.Activator}>{e.ActivatorLevel} root {e.Root}{e.RootLevelDifference:+#;-#;0} " +
                    $"{(e.IsHouse ? "house " : "")}{(e.IsWallOrTower ? "wall/tower " : "")}{(e.IsCastle ? "castle " : "")}" +
                    $"mods [{string.Join(",", e.Modifiers.Select(m => $"{m.Kind}:{m.Step}/{m.Min}/{m.Max}{(m.Sustainable ? "S" : "")}"))}] harbourField {e.HarbourField}" +
                    $"\n      at ({e.MapX:0},{e.MapY:0}) steps {steps}");
            }
            if (s.Notes.Count > 0) lines.Add("  notes: " + string.Join("; ", s.Notes));
            return string.Join("\n", lines);
        }

        internal static string Signed(int v) => v > 0 ? $"+{v}" : v < 0 ? $"{v}" : "±0";
    }
}
