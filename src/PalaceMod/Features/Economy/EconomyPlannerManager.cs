using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using PalaceMod.Economy;

/*
    Economy planner: the Economy tab of Run Analytics. The player lays out builds and upgrades on the days ahead ("on
    day 4, build three houses; on day 6, take the castle to level 3 with Builder's Guild"), the buildings they expect to
    lose on a night, temple prayers, and perk loadouts to try, and sees the gold that leaves at every dawn up to the
    level's last night, against doing nothing, another plan or a branch of this one, with shortfalls and paybacks.

      EconomyModel.cs     the simulation and perk what-ifs (game-independent, unit tested)
      EconomySnapshot.cs  reads the live level into it
      PerkCapture.cs      perk numbers the game throws away at level load, recorded by the patches below
      PlannerStore.cs     plans and branches, saved per level under BepInEx/PalaceMod/EconomyPlanner/
      PlannerPages.cs     the tab and the screens on top of it
      PlannerVisuals.cs   the graphs, the map and the income-sources chart

    Read-only planning: it never changes the run, so it has no Enabled switch and doesn't mark gameplay impact (the
    patches only read). Accuracy check: the snapshot's projection of the coming dawns with nothing planned must match
    IncomeCalculator, slot costs NextUpgradeOrBuildCost, and the waves' gold rebuilt from their pre-perk coins the
    game's; mismatches are logged as warnings, and `econ check` lists them.
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

        // ---- Perk capture (read-only) -----------------------------------------------------------------------------

        private static void Capture(Action record)
        {
            try { record(); }
            catch (Exception e) { Log.For<EconomyPlannerManager>().Warn($"Couldn't record a perk's numbers: {e.Message}"); }
        }

        [HarmonyPatch(typeof(PerkCostModifyer), "Start")]
        private static class CostCapturePatch
        {
            private static void Prefix(PerkCostModifyer __instance) => Capture(() => PerkCapture.Cost(__instance));
        }

        [HarmonyPatch(typeof(PerkIncomeModifyer), "Execute")]
        private static class IncomeCapturePatch
        {
            private static void Prefix(PerkIncomeModifyer __instance) => Capture(() => PerkCapture.Income(__instance));
        }

        [HarmonyPatch(typeof(PerkUnrestrictedUpgrading), "Start")]
        private static class UnrestrictedCapturePatch
        {
            private static void Prefix(PerkUnrestrictedUpgrading __instance) => Capture(() => PerkCapture.Unrestricted(__instance));
        }

        [HarmonyPatch(typeof(InterestPerk), "Start")]
        private static class InterestCapturePatch
        {
            private static void Prefix(InterestPerk __instance) => Capture(() => PerkCapture.InterestPerk(__instance));
        }

        // Rat God and Loan are folded into the spawns' coins further down Start; the waves are the level's here.
        [HarmonyPatch(typeof(EnemySpawner), "Start")]
        private static class SpawnCapturePatch
        {
            private static void Prefix(EnemySpawner __instance) => Capture(() => PerkCapture.Spawns(__instance));
        }

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
                string key = snap == null ? null : $"{snap.World.Level}:{snap.World.FirstDay}:{snap.World.DawnPending}";
                if (snap != null && checkedLevel != key)
                {
                    // Once per day (and per night): the projection of doing nothing has to agree with the game.
                    checkedLevel = key;
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

        /// <summary>A new plan, starting with the castle's build on day 1 (every run's first step).</summary>
        internal static PlannerScenario NewPlan(PlannerFile f, EconWorld w, string name)
        {
            var plan = new PlannerScenario { Name = f.FreeName(name) };
            var castle = w.Slots.FirstOrDefault(s => s.IsCastle && !s.IsDependent && s.Steps.Count > 0);
            if (castle != null) plan.Actions.Add(new EconAction { Day = 1, Slot = castle.Id, Level = 1 });
            return plan;
        }

        /// <summary>A branch of <paramref name="parent"/> from <paramref name="day"/> on (int.MaxValue: all its steps,
        /// e.g. to try other perks), shown and compared with its parent.</summary>
        internal PlannerScenario Branch(PlannerFile f, PlannerScenario parent, int day, string name = null)
        {
            var b = new PlannerScenario
            {
                Name = f.FreeName(name ?? (day == int.MaxValue ? "perks" : $"from day {day}")), Parent = parent.Id, ForkDay = day,
            };
            f.Scenarios.Add(b);
            f.Current = b.Id;
            f.Compare = parent.Id;
            f.Normalize();
            return b;
        }

        internal sealed class Projection
        {
            public EconSnapshot Snap;
            /// <summary>The world with the plan's perks.</summary>
            public EconWorld World;
            public PlannerFile File;
            public PlannerScenario Plan, Other;
            public List<EconAction> Actions, OtherActions;
            public EconResult Result, Compare, Baseline;
            public List<string> PerkNotes = [];
            public string PlanName => File.FullName(Plan);
            public string OtherName => Other != null ? File.FullName(Other) : "doing nothing";
            public bool InheritedFrom(EconAction a) => !Plan.Actions.Contains(a);
        }

        internal Projection Project()
        {
            var s = Snap();
            if (s == null) return null;
            var f = Plans(s.World.Level);
            if (f.Fresh)
            {
                f.Fresh = false;
                if (f.Scenarios.Count == 1 && f.Scenarios[0].Actions.Count == 0)
                {
                    var seeded = NewPlan(f, s.World, f.Scenarios[0].Name);
                    f.Scenarios[0].Actions.AddRange(seeded.Actions);
                    Save();
                }
            }
            var p = new Projection { Snap = s, File = f, Plan = f.CurrentScenario, Other = f.CompareScenario };
            p.Actions = f.Effective(p.Plan);
            p.World = EconPerks.Apply(s.World, f.EffectivePerks(p.Plan), p.PerkNotes);
            p.Result = EconSim.Run(p.World, p.Actions);
            p.Baseline = p.Actions.Count == 0 ? p.Result : EconSim.Run(p.World, [], paybacks: false);
            if (p.Other != null)
            {
                p.OtherActions = f.Effective(p.Other);
                p.Compare = EconSim.Run(EconPerks.Apply(s.World, f.EffectivePerks(p.Other)), p.OtherActions, paybacks: false);
            }
            else p.Compare = p.Baseline;
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
            foreach (var e in w.Slots)
            {
                var slot = s.Objects[e.Index];
                if (slot == null) continue;
                // The game indexes a follower's own tree with its own level; a shorter tree throws mid-upgrade.
                if (e.IsDependent && e.Steps.Count < w.Slots[e.Owner].MaxLevel)
                    problems.Add($"{e.Label} #{e.Index} follows #{e.Owner} but has {e.Steps.Count} steps for its {w.Slots[e.Owner].MaxLevel} levels");
                if (e.IsDependent || !e.Active) continue;
                bool live = slot.CanBeUpgraded, mine = EconSim.CanUpgrade(w, e);
                if (live != mine) problems.Add($"{e.Label} #{e.Index}: can upgrade {mine}, game says {live}");
                else if (live && e.Steps[e.Level].Cost != slot.NextUpgradeOrBuildCost)
                    problems.Add($"{e.Label} #{e.Index}: costs {e.Steps[e.Level].Cost}, game says {slot.NextUpgradeOrBuildCost}");
            }
            // Perk what-ifs rebuild the waves' gold from their coins before Rat God and Loan: it must give the game's.
            if (w.SpawnGold != null)
            {
                var rat = w.Perks.FirstOrDefault(p => p.Kind == EconPerkKind.RatGod);
                var loan = w.Perks.FirstOrDefault(p => p.Kind == EconPerkKind.Loan);
                var rebuilt = EconPerks.WaveGold(w.SpawnGold, rat != null && rat.Equipped ? rat.Factor : (float?)null, loan != null && loan.Equipped ? loan.Amount2 : 0);
                for (int i = 0; i < rebuilt.Length && i < w.WaveGold.Length; i++)
                    if (rebuilt[i] != w.WaveGold[i]) { problems.Add($"night {i + 1}'s gold rebuilt as {rebuilt[i]}, game says {w.WaveGold[i]}"); break; }
            }
            return problems;
        }

        // ---- Dev command ------------------------------------------------------------------------------------------

        private const string Usage =
            "econ [open|check|dump [slot]|slots [all|income|text]|add <day> <slot> [level] [choice]|addmany <day> <type> <n>|ko <night> <slot>|" +
            "gold <night> <amount>|wisdom <n> <amount|auto>|remove <n>|move <n> <day>|order <n> up|down|level <n> <L>|choice <n> <key|#>|" +
            "branch <day|all> [name]|perk [list|<key|#> on|off|reset]|sources [day]|plan list|new [name]|copy|rename <name>|delete|use <n|name>|clear|" +
            "compare <n|name|none>|picker [day] [build|ko|gold]|editor <n>|plans|perks|sourcesview]   economy planner";

        private string Command(string[] args)
        {
            var s = Snap(fresh: true) ?? throw new InvalidOperationException("not in a level");
            var w = s.World;
            var f = Plans(w.Level);
            Project(); // seeds a fresh file
            var plan = f.CurrentScenario;
            string verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            int Int(int i) => args.Length > i ? int.Parse(args[i]) : throw new ArgumentException($"usage: {Usage}");
            EconAction Nth(int i)
            {
                var ordered = EconSim.Ordered(f.Effective(plan));
                int n = Int(i);
                if (n < 1 || n > ordered.Count) throw new ArgumentException($"the plan has {ordered.Count} items");
                return ordered[n - 1];
            }
            PlannerScenario Holder(EconAction a) => f.OwnerOf(a) ?? plan;
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
                        (problems.Count == 0 ? "OK: matches IncomeCalculator, every slot's cost and the waves' gold" : "MISMATCH:\n  " + string.Join("\n  ", problems)) +
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
                    var state = EconSim.StateOn(Project().World, f.Effective(plan), day).Slots[slot.Index];
                    int level = args.Length > 3 ? int.Parse(args[3]) : state.Level + 1;
                    var a = new EconAction { Day = day, Slot = slot.Id, Level = level, Choice = ChoiceArg(slot, level, args.Length > 4 ? args[4] : null) };
                    plan.Actions.Add(a);
                    Done();
                    return Describe(Project(), a);
                }
                case "addmany":
                {
                    int day = Int(1);
                    string type = args.Length > 3 ? string.Join(" ", args.Skip(2).Take(args.Length - 3)) : throw new ArgumentException("addmany <day> <type> <n>");
                    int n = int.Parse(args[args.Length - 1]);
                    var p = Project();
                    var picked = AddSeveral(p, type, day, n);
                    Done();
                    return $"added {picked} of {n}\n" + Summary(Project());
                }
                case "ko":
                case "gold":
                {
                    int night = Int(1);
                    var a = verb == "ko"
                        ? new EconAction { Kind = EconActionKind.KnockOut, Day = night, Slot = SlotArg(w, args.Length > 2 ? args[2] : throw new ArgumentException("ko <night> <slot>")).Id }
                        : new EconAction { Kind = EconActionKind.Gold, Day = night, Amount = Int(2) };
                    plan.Actions.Add(a);
                    Done();
                    return Describe(Project(), a);
                }
                case "wisdom":
                {
                    var a = Nth(1);
                    a.Amount = args.Length > 2 && args[2] != "auto" ? int.Parse(args[2]) : null;
                    Done();
                    return Describe(Project(), a);
                }
                case "remove":
                {
                    var a = Nth(1);
                    Holder(a).Actions.Remove(a);
                    Done();
                    return $"removed; {f.Effective(plan).Count} items left";
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
                    if (!Reorder(Holder(a), a, args.Length > 2 && args[2] == "up" ? -1 : 1)) return "nothing to swap with that day";
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
                case "branch":
                {
                    int day = args.Length > 1 && args[1] != "all" ? int.Parse(args[1]) : int.MaxValue;
                    Branch(f, plan, day, args.Length > 2 ? string.Join(" ", args.Skip(2)) : null);
                    Done();
                    return "\n" + Summary(Project());
                }
                case "perk":
                    return PerkCommand(f, plan, args.Skip(1).ToArray(), Done);
                case "sources":
                {
                    var p = Project();
                    int day = args.Length > 1 ? int.Parse(args[1]) : w.FirstDay;
                    return "\n" + string.Join("\n", Sources(p, day).Select(src =>
                        $"  {src.Name}: " + string.Join("; ", src.Levels.Select(e =>
                            $"lv{e.Level} cost {e.Cost} first {e.FirstDawn} last {e.LastDawn} earned {e.Earned} net {e.Net} payback {(e.PaybackDawn?.ToString() ?? "-")}{(e.Blocked != null ? $" [{e.Blocked}]" : "")}"))));
                }
                case "plan":
                    return PlanCommand(f, w, args.Skip(1).ToArray(), Done);
                case "compare":
                {
                    string which = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "none";
                    f.Compare = which == "none" ? null : ScenarioArg(f, which).Id;
                    f.Normalize();
                    Done();
                    return "\n" + Summary(Project());
                }
                case "picker":
                {
                    Open();
                    pickMode = args.Length > 2 ? args[2] switch { "ko" => 1, "gold" => 2, _ => 0 } : 0;
                    OpenPicker(args.Length > 1 ? int.Parse(args[1]) : w.FirstDay, null);
                    return pickerFrame.IsOpen ? $"picker open on day {pickDay}, {pickerRows} buildings listed" : throw new InvalidOperationException("didn't open");
                }
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
                case "perks":
                    Open();
                    perksFrame.Open();
                    return perksFrame.IsOpen ? "perks open" : throw new InvalidOperationException("didn't open");
                case "sourcesview":
                    Open();
                    OpenSources();
                    return sourcesFrame.IsOpen ? "income sources open" : throw new InvalidOperationException("didn't open");
                default:
                    throw new ArgumentException($"usage: {Usage}");
            }
        }

        private static string PerkCommand(PlannerFile f, PlannerScenario plan, string[] args, Action done)
        {
            var w = Feature<EconomyPlannerManager>.Instance.Snap().World;
            var effective = f.EffectivePerks(plan);
            string Line(EconPerk p, int i) => $"  {i + 1}. {p.Name} ({p.Key}, {p.Kind}): equipped {p.Equipped}" +
                (effective.TryGetValue(p.Key, out bool v) ? $", this plan: {(v ? "on" : "off")}" : "");
            if (args.Length == 0 || args[0] == "list") return "\n" + string.Join("\n", w.Perks.Select(Line));
            var perk = int.TryParse(args[0], out int n) && n >= 1 && n <= w.Perks.Count ? w.Perks[n - 1]
                : w.Perks.FirstOrDefault(p => p.Key == args[0] || p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"no perk \"{args[0]}\"");
            string what = args.Length > 1 ? args[1] : "reset";
            if (what == "reset") plan.Perks.Remove(perk.Key);
            else plan.Perks[perk.Key] = what == "on";
            done();
            return "\n" + Summary(Feature<EconomyPlannerManager>.Instance.Project());
        }

        private string PlanCommand(PlannerFile f, EconWorld w, string[] args, Action done)
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
                    var s = NewPlan(f, w, rest.Length > 0 ? rest : "Plan");
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
                    f.Delete(plan);
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
                $"  {i + 1}. {(s.Id == f.Current ? "*" : " ")}{(s.Id == f.Compare ? "~" : " ")} {f.FullName(s)}: {s.Actions.Count} own steps" +
                (s.Parent != null ? $", from day {(s.ForkDay == int.MaxValue ? "∞" : s.ForkDay.ToString())}" : "") + (s.Perks.Count > 0 ? $", {s.Perks.Count} perk changes" : "")));
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
            var sameDay = EconSim.Ordered(plan.Actions).Where(x => x.Day == a.Day && x.IsStep).ToList();
            int at = sameDay.IndexOf(a), to = at + direction;
            if (at < 0 || to < 0 || to >= sameDay.Count) return false;
            var other = sameDay[to];
            int i = plan.Actions.IndexOf(a), j = plan.Actions.IndexOf(other);
            plan.Actions[i] = other;
            plan.Actions[j] = a;
            return true;
        }

        /// <summary>Builds <paramref name="count"/> more of a building type on <paramref name="day"/>: the ones that can
        /// be built that day, nearest the castle first. Returns how many it found.</summary>
        internal int AddSeveral(Projection p, string type, int day, int count)
        {
            var state = EconSim.StateOn(p.World, p.Actions, day);
            var picks = state.Slots.Where(e => !e.IsDependent && e.Level == 0 && e.Active && e.Steps.Count > 0
                    && string.Equals(e.TypeName, type, StringComparison.OrdinalIgnoreCase) && EconSim.CanUpgrade(state, e))
                .OrderBy(e => e.MapX * e.MapX + e.MapY * e.MapY).Take(count).ToList();
            foreach (var e in picks)
                p.Plan.Actions.Add(new EconAction { Day = day, Slot = e.Id, Level = 1, Choice = ChoiceArg(e, 1, null) });
            return picks.Count;
        }

        // ---- Income sources ---------------------------------------------------------------------------------------

        internal sealed class Source
        {
            public string Name;
            public EconSlot Slot;
            public List<SourceEstimate> Levels = [];
            public string Note;
        }

        /// <summary>Every kind of income building on the map, built (or taken further) on <paramref name="day"/> on top
        /// of the plan: one slot of each type, the nearest the castle that can still go up.</summary>
        internal static List<Source> Sources(Projection p, int day)
        {
            var w = p.World;
            var state = EconSim.StateOn(w, p.Actions, day);
            var list = new List<Source>();
            foreach (var group in w.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0 && (e.EverPays || w.Slots.Any(d => d.Owner == e.Index && d.EverPays)))
                         .GroupBy(e => e.TypeName))
            {
                var pick = group.Select(e => state.Slots[e.Index]).Where(e => e.Level < e.MaxLevel)
                    .OrderBy(e => e.Level == 0 ? 0 : 1).ThenBy(e => e.MapX * e.MapX + e.MapY * e.MapY).FirstOrDefault();
                if (pick == null) continue;
                var src = new Source { Name = group.Key, Slot = w.Slots[pick.Index], Levels = EconSim.Evaluate(w, p.Actions, pick.Index, day) };
                var mods = src.Slot.Modifiers;
                if (mods.Any(m => m.Kind == EconModifierKind.Mine)) src.Note = "pays less each dawn";
                else if (mods.FirstOrDefault(m => m.Kind == EconModifierKind.Harbour) is { } h) src.Note = $"pays more each dawn, up to {h.Max}";
                else if (w.Slots.Any(d => d.Owner == src.Slot.Index && d.EverPays)) src.Note = "with its fields";
                else if (src.Slot.IsShrine) src.Note = "only once unlocked by deaths nearby";
                if (pick.Level > 0) src.Note = (src.Note != null ? src.Note + "; " : "") + $"next {group.Key} is already level {pick.Level}";
                if (src.Levels.Count > 0) list.Add(src);
            }
            return list.OrderByDescending(s => s.Levels.Max(e => e.Net)).ToList();
        }

        // ---- Text summaries (dev command) -------------------------------------------------------------------------

        private static string Summary(Projection p)
        {
            var w = p.World;
            var lines = new List<string>
            {
                $"{w.Level}: day {w.FirstDay} of {w.LastNight}{(w.DawnPending ? " (night/sunrise: dawn pending)" : "")}, gold {w.Gold}. Plan \"{p.PlanName}\" vs {p.OtherName}",
            };
            if (p.PerkNotes.Count > 0) lines.Add("  perk notes: " + string.Join("; ", p.PerkNotes));
            if (w.DawnPending)
                lines.Add($"  pending dawn {w.FirstDay - 1}: drops +{p.Result.PendingDrops} extra +{p.Result.PendingExtra} buildings +{p.Result.PendingBuildings} treasure +{p.Result.PendingTreasure}");
            lines.Add("  day  start  spent  harv  int  drops  extra  bldg  treas  ko  dawn   vs");
            foreach (var d in p.Result.Days)
            {
                var c = p.Compare.Days.FirstOrDefault(x => x.Day == d.Day);
                lines.Add($"  {d.Day,3}  {d.Start,5}  {d.Spent,5}  {d.Harvest,4}  {d.Interest,3}  {d.Drops,5}  {d.Extra,5}  {d.Buildings,4}  {d.Treasure,5}  {d.KnockedOut,2}  {d.AtDawn,5}  {(c == null ? "" : Signed(d.AtDawn - c.AtDawn))}" +
                    (d.Shortfall > 0 ? $"  SHORT {d.Shortfall}" : ""));
            }
            lines.Add($"  end gold {p.Result.EndGold} ({Signed(p.Result.EndGold - p.Baseline.EndGold)} vs doing nothing" +
                (p.Other != null ? $", {Signed(p.Result.EndGold - p.Compare.EndGold)} vs {p.OtherName}" : "") +
                $"); spent {p.Result.TotalSpent}; buildings pay {p.Result.LastDawnBuildings} at the last dawn");
            var ordered = EconSim.Ordered(p.Actions);
            for (int i = 0; i < ordered.Count; i++) lines.Add($"  {i + 1}. " + Describe(p, ordered[i]) + (p.InheritedFrom(ordered[i]) ? "  (inherited)" : ""));
            return string.Join("\n", lines);
        }

        private static string Describe(Projection p, EconAction a)
        {
            var r = p.Result.Actions.FirstOrDefault(x => x.Action == a);
            if (r == null) return "?";
            string when = a.IsStep ? $"day {a.Day}" : $"night {a.Day}";
            return $"{when}: {Title(r)} — {r.Status}" + (r.Reason != null ? $" ({r.Reason})" : "") +
                (r.Counts && a.IsStep ? $", cost {r.Cost}, income {Signed(r.IncomeDelta)}/dawn" + (r.IncomeDelta != 0 ? $", payback {(r.PaybackDawn is int d ? $"dawn {d}" : $"none ({r.ExtraIncome}/{r.Cost} by the end)")}" : "") : "") +
                (r.Counts && !a.IsStep ? $", {Signed(r.IncomeDelta)}" : "") + (r.Wisdom != 0 ? $", wisdom +{r.Wisdom}" : "");
        }

        private static string Dump(EconSnapshot s, int only)
        {
            var w = s.World;
            var lines = new List<string>
            {
                $"{w.Level}: gold {w.Gold}, first day {w.FirstDay}, last night {w.LastNight}, dawn pending {w.DawnPending} (tonight's gold {w.TonightGold})",
                $"  waves gold: {string.Join(" ", w.WaveGold)}{(w.SpawnGold != null ? $" (before perks: {string.Join(" ", w.SpawnGold.Select(x => x.Sum()))})" : " (before perks: not recorded)")}",
                $"  interest {(w.Interest ? $"1 per {w.InterestEach}, max {w.InterestMax}" : "no")}, treasure hunter {(w.TreasureHunter ? $"{w.Treasure1}/{w.Treasure2}/{w.Treasure3}" : "no")}, " +
                $"guild {w.GuildActive}, castle up {w.CastleUpActive} (-{w.CastleUpDiscount2}/-{w.CastleUpDiscount3}), outpost {w.Outpost}, houses [{string.Join(",", w.HouseOrder)}]",
                $"  perks: {string.Join("; ", w.Perks.Select(p => $"{p.Name}={p.Key} {p.Kind}{(p.Equipped ? " (equipped)" : "")}"))}",
            };
            foreach (var e in w.Slots)
            {
                if (only >= 0 && e.Index != only) continue;
                string steps = string.Join(" | ", e.Steps.Select((st, i) => $"{i + 1}:{st.Cost}{(st.Disabled ? "x" : "")}[{string.Join(",", st.Branches.Select(b => $"{b.Key}{(b.IncomeChange != 0 ? Signed(b.IncomeChange) : "")}{(b.Locked ? " L" : "")}{(b.StartsGuild ? " G" : "")}{(b.StartsCastleUp ? " C" : "")}{(b.Prayers ? " P" : "")}{(b.IsWisdom ? $" W{b.WisdomGold}/{b.WisdomDrain:0.###}" : "")}{(b.BusyUntilDawn ? " T" : "")}{(b.PlowTree >= 0 ? $" plow#{b.PlowTree}" : "")}"))}]"));
                lines.Add($"  #{e.Index} {e.Label} ({e.ObjectName}) id={e.Id} lv {e.Level} inc {e.Income} active {e.Active} pays {e.Pays} ko {e.KnockedOut} " +
                    $"owner {e.Owner} act {e.Activator}>{e.ActivatorLevel} root {e.Root}{e.RootLevelDifference:+#;-#;0} " +
                    $"{(e.IsHouse ? "house " : "")}{(e.IsWallOrTower ? $"wall/tower {e.CastleUpTags}t{e.CastleUpReceivers}r " : "")}{(e.HarvestedToday ? "harvested " : "")}{(e.Busy ? "busy " : "")}" +
                    $"{(e.IsCastle ? "castle " : "")}{(e.IsShrine ? "shrine " : "")}{(e.IsBuilding ? "building " : "")}{(e.OutpostTowers > 0 ? $"outpost:{e.OutpostTowers} " : "")}" +
                    $"mods [{string.Join(",", e.Modifiers.Select(m => $"{m.Kind}:{m.Step}/{m.Min}/{m.Max}{(m.Sustainable ? "S" : "")}{(m.PerkKey != null ? $"({m.PerkKey})" : "")}"))}] harbourField {e.HarbourField}" +
                    (e.PerkMods.Count > 0 ? $" perkMods [{string.Join(",", e.PerkMods.Select(m => $"{m.Key}->#{m.Tree} c[{string.Join(" ", m.Cost)}] i[{string.Join(" ", m.Income)}]{(m.RootDifference != null ? $" root{m.RootDifference}" : "")}{(m.Applied ? " on" : " off")}{(m.Pending ? " pending" : "")}"))}]" : "") +
                    $"\n      at ({e.MapX:0},{e.MapY:0}) steps {steps}");
            }
            if (s.Notes.Count > 0) lines.Add("  notes: " + string.Join("; ", s.Notes));
            return string.Join("\n", lines);
        }

        internal static string Signed(int v) => v > 0 ? $"+{v}" : v < 0 ? $"{v}" : "±0";
    }
}
