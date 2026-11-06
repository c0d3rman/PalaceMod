using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PalaceMod.Economy;
using Object = UnityEngine.Object;
using V = PalaceMod.Economy.PlannerVisuals;

/*
    The planner's screens.
      Economy tab (in Run Analytics): the plan's items (left/right moves one a day, apply edits it), adding, branching,
        perks and the income-sources comparison, and a day-by-day table; beside it, fixed, the totals and the gold graph.
      Add to plan: a build or upgrade (one, several picked at once, or "build N of these"), a building lost on a night,
        or gold found on a night. Buildings are grouped by type as they'd be that day; the map beside the list shows
        where the selected one is.
      Plan item: day/night, level, choice, wisdom gold, order within the day, building; branch from here; remove.
      Plans: every plan and branch side by side; switch, add, copy, rename, clear or delete.
      Perks: try a loadout (in this plan or a branch of it).
      Income sources: what each kind of income building brings if built on a day, against what it costs.
    They're frames opened on top of Run Analytics, so Back returns to the tab, which then rebuilds.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private ModFrame host, pickerFrame, editorFrame, plansFrame, perksFrame, sourcesFrame;
        private bool hostDirty, editorDirty;

        // Picker state.
        private int pickDay, pickFilter, pickSlot = -1, pickerRows, pickMode, pickGold = 10;
        private bool pickMulti;
        private readonly HashSet<int> pickSelected = [];
        private readonly Dictionary<string, int> pickCounts = [];
        private string replaceId;
        private int lastDay, branchDay, sourcesDay;
        private static readonly string[] Filters = ["Buildings you can add", "Income buildings", "Every slot"];
        private static readonly string[] Modes = ["Build or upgrade", "A building lost that night", "Gold found that night"];

        // Editor and plans state.
        private string editId;
        private string confirm;

        private static readonly string DimHex = ModStyle.Hex(ModStyle.Dim);
        private static readonly string RedHex = ModStyle.Hex(V.Red);

        private void BuildFrames()
        {
            pickerFrame = new ModFrame("Add to plan").SetContent(BuildPicker);
            editorFrame = new ModFrame("Plan item").SetContent(BuildEditor);
            editorFrame.Activated += () =>
            {
                if (!editorDirty) return;
                editorDirty = false;
                editorFrame.Refresh();
            };
            plansFrame = new ModFrame("Plans").SetContent(BuildPlans);
            plansFrame.Activated += () => confirm = null;
            perksFrame = new ModFrame("Perks").SetContent(BuildPerks);
            sourcesFrame = new ModFrame("Income sources").SetContent(BuildSources);
        }

        /// <summary>Run Analytics hands over its frame, so the tab can be rebuilt when a frame on top closes.</summary>
        internal void AttachHost(ModFrame frame)
        {
            if (host == frame) return;
            host = frame;
            frame.Activated += () =>
            {
                if (!hostDirty) return;
                hostDirty = false;
                frame.Refresh();
            };
        }

        private void RefreshOpen()
        {
            foreach (var f in new[] { pickerFrame, editorFrame, plansFrame, perksFrame, sourcesFrame })
                if (f.IsOpen) { f.Refresh(); return; }
            if (host != null && host.IsOpen) { hostDirty = false; host.Refresh(); }
        }

        /// <summary>A change made on the tab itself: save and rebuild it.</summary>
        private void ChangedHere()
        {
            Save();
            hostDirty = false;
            host?.Refresh();
        }

        /// <summary>A change made in a frame on top: save, rebuild that frame, and the tab when it's back.</summary>
        private void ChangedOnTop(ModFrame frame)
        {
            Save();
            hostDirty = true;
            if (frame.IsOpen) frame.Refresh();
        }

        /// <summary>A whole-number field (the UI kit's: stepping that speeds up, Shift x10, typing).</summary>
        private static ModField Num(ModPage page, string label, Func<int> get, Action<int> set, int min, int max, Func<string> tooltip, Func<int, string> show = null) =>
            page.Number(label, get, set, min, Math.Max(min, max), format: show, tooltip: tooltip);

        // ---- Economy tab ------------------------------------------------------------------------------------------

        internal void BuildPage(ModPage page)
        {
            var p = Project();
            if (p == null)
            {
                page.Text("The economy planner works during a level.");
                return;
            }
            var w = p.World;
            BuildAside(page.Aside, p);

            page.Stepper("Plan", () => p.PlanName, d =>
            {
                var list = p.File.Scenarios;
                int i = (list.IndexOf(p.Plan) + Math.Sign(d) + list.Count) % list.Count;
                p.File.Current = list[i].Id;
                p.File.Normalize();
                ChangedHere();
            }, OpenPlans, "Left/right switches between your plans and branches for this map. Select it to see them side by side, and add, copy, rename or delete them.");
            page.Stepper("Compare with", () => Capital(p.OtherName), d =>
            {
                var options = new List<string> { null };
                options.AddRange(p.File.Scenarios.Where(s => s != p.Plan).Select(s => s.Id));
                int i = (options.IndexOf(p.Other?.Id) + Math.Sign(d) + options.Count) % options.Count;
                p.File.Compare = options[i];
                ChangedHere();
            }, null, "What the graph's dim line and the table's last column compare this plan with: doing nothing, or another plan or branch.");
            int changed = p.File.EffectivePerks(p.Plan).Count(kv => w.Perks.Any(x => x.Key == kv.Key && x.Equipped != kv.Value));
            page.Row("Perks", changed == 0 ? "your loadout" : $"{changed} changed", "Try this plan with other perks. Perks that move gold are counted: Interest, Treasure Hunter, Royal Mint, Loan, Rat God, Outpost, and the ones that change buildings' costs and income.", () => perksFrame.Open());

            page.Header(w.FirstDay <= w.LastNight ? $"Plan, day {w.FirstDay} to {w.LastNight}" : "Plan");
            var results = Ordered(p);
            if (results.Count == 0)
                page.Text("Nothing planned yet. Add the builds and upgrades you're weighing up: the graph and the table show the gold they leave you each day.");
            else
                page.Text($"<color=#{DimHex}>Left/right moves an item to another day. Select it to change it, remove it, or branch from it.</color>");
            foreach (var r in results) ActionRow(page, p, r);
            if (w.FirstDay <= w.LastNight || w.DawnPending)
            {
                page.Button("Add to the plan", () =>
                {
                    pickMode = 0;
                    OpenPicker(lastDay >= w.FirstDay ? lastDay : w.FirstDay, null);
                }, "A build or upgrade (or several at once), a building you expect to lose on a night, or gold you expect to find.");
            }
            if (w.FirstDay <= w.LastNight)
            {
                branchDay = Mathf.Clamp(branchDay == 0 ? w.FirstDay : branchDay, w.FirstDay, w.LastNight);
                Num(page, "Branch from day", () => branchDay, v => { branchDay = v; host?.Refresh(); }, w.FirstDay, w.LastNight,
                    () => "A branch shares this plan's items before that day and has its own after: try something else from then on and compare the two.", v => $"Day {v}");
                page.Button($"Make a branch from day {branchDay}", () =>
                {
                    Branch(p.File, p.Plan, branchDay);
                    ChangedHere();
                }, "It's shown at once, compared with this plan.");
            }
            page.Button("Compare income sources", OpenSources, "Every kind of income building on this map: what it costs, what it pays per night and when it pays for itself.");

            page.Header("Day by day");
            DayTable(page, p);
            page.Row("About these numbers", "", About(w));

            var history = Feature<AnalyticsManager>.Instance?.History;
            if (history != null && history.level == w.Level && history.nights.Any(r => r.complete))
            {
                page.Header("Nights so far");
                AnalyticsPages.PastNights(page, history);
            }
        }

        private static List<ActionResult> Ordered(Projection p) =>
            EconSim.Ordered(p.Actions).Select(a => p.Result.Actions.First(r => r.Action == a)).ToList();

        private void ActionRow(ModPage page, Projection p, ActionResult r)
        {
            var a = r.Action;
            var w = p.World;
            bool inherited = p.InheritedFrom(a);
            string title = Title(r);
            var row = page.Stepper(title, () => a.IsStep ? $"Day {a.Day}" : $"Night {a.Day}", d =>
            {
                if (inherited)
                {
                    Notify.Show($"That's part of {p.File.FullName(p.File.OwnerOf(a))}: select it to change it there.", NotifyKind.Info, key: "econ-inherited");
                    return;
                }
                int first = a.IsStep || !w.DawnPending ? w.FirstDay : w.FirstDay - 1;
                int day = Mathf.Clamp(a.Day + Math.Sign(d), Math.Max(1, Math.Min(a.Day, first)), w.LastNight);
                if (day == a.Day) return;
                a.Day = day;
                lastDay = day;
                ChangedHere();
            }, () => OpenEditor(a.Id), Tooltip(r, p));
            // Named by id, so the selection stays on this item when the page is rebuilt after a move.
            row.Button.gameObject.name = "Item " + a.Id;
            string color = r.Status switch
            {
                ActionStatus.Short => RedHex,
                ActionStatus.Blocked or ActionStatus.Past or ActionStatus.AlreadyDone => DimHex,
                _ => inherited ? DimHex : null,
            };
            string main = color == null ? title : $"<color=#{color}>{title}</color>";
            string detail = Detail(r) + (inherited ? $" · from {p.File.FullName(p.File.OwnerOf(a))}" : "");
            row.SetLabel($"{main}\n<size=76%><color=#{(r.Status == ActionStatus.Short ? RedHex : DimHex)}>{detail}</color></size>");
            var le = row.Button.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = 70f;
        }

        /// <summary>"Build House 3 · north-east" / "Castle to level 3: Builder's Guild" / "Lose Mill" / "Gold found: 12".</summary>
        internal static string Title(ActionResult r)
        {
            var a = r.Action;
            string name = r.Slot?.Label ?? "a building not on this map";
            switch (a.Kind)
            {
                case EconActionKind.KnockOut: return $"Lose {name}";
                case EconActionKind.Gold: return $"Gold found: {a.Amount ?? 0}";
            }
            string verb = a.Level <= 1 ? $"Build {name}" : $"{name} to level {a.Level}";
            var step = r.Slot != null && a.Level >= 1 && a.Level <= r.Slot.Steps.Count ? r.Slot.Steps[a.Level - 1] : null;
            if (step != null && step.IsChoice) verb += ": " + step.Branches[EconSim.BranchIndex(step, a.Choice)].Name;
            return verb;
        }

        private static string Detail(ActionResult r)
        {
            switch (r.Status)
            {
                case ActionStatus.AlreadyDone: return r.Reason == "Done" ? "done" : r.Reason;
                case ActionStatus.Past: return $"missed: {r.Reason}";
                case ActionStatus.Blocked: return $"skipped: {r.Reason}";
            }
            switch (r.Action.Kind)
            {
                case EconActionKind.KnockOut: return r.IncomeDelta < 0 ? $"pays nothing that dawn ({Signed(r.IncomeDelta)})" : "pays nothing that dawn";
                case EconActionKind.Gold: return "your estimate, e.g. from Temple wisdom or a cheat";
            }
            return string.Join(" · ", new[]
            {
                r.Status == ActionStatus.Short ? $"{Coin(r.Cost)}, short by {r.Shortfall}" : Coin(r.Cost),
                r.IncomeDelta != 0 ? $"{Signed(r.IncomeDelta)} per dawn" : null,
                r.Branch?.IsWisdom == true ? $"wisdom {Signed(r.Wisdom)} that night{(r.Action.Amount == null ? $" ({r.Branch.WisdomGold} per building lost)" : "")}" : null,
                r.Branch?.Prayers == true ? "shrines pay nothing that dawn" : null,
                Payback(r),
            }.Where(x => x != null));
        }

        private static string Payback(ActionResult r)
        {
            if (!r.Counts || !r.Action.IsStep || r.IncomeDelta <= 0 && r.ExtraIncome <= 0 || r.Cost <= 0) return null;
            return r.PaybackDawn is int d ? $"pays for itself by dawn {d}" : $"earns back {r.ExtraIncome} of {r.Cost} by the end";
        }

        private static string Tooltip(ActionResult r, Projection p)
        {
            var lines = new List<string>();
            switch (r.Status)
            {
                case ActionStatus.Short:
                    lines.Add($"Not enough gold: {r.Shortfall} short on day {r.Action.Day}. The projection carries on as if you'd found it.");
                    break;
                case ActionStatus.Blocked:
                    lines.Add($"Skipped: {r.Reason}.");
                    break;
                case ActionStatus.Past:
                    lines.Add($"{r.Reason}, and this wasn't done.");
                    break;
                case ActionStatus.AlreadyDone:
                    lines.Add(r.Reason == "Done" ? "Done." : $"{r.Reason}, so this costs nothing.");
                    break;
            }
            if (r.Counts && r.Action.IsStep)
            {
                lines.Add($"Costs {r.Cost}; buildings pay {Signed(r.IncomeDelta)} per dawn after it.");
                if (r.IncomeDelta != 0 || r.ExtraIncome != 0)
                    lines.Add(r.PaybackDawn is int d ? $"It has paid for itself by dawn {d}, and brings {r.ExtraIncome} by the last dawn." : $"It brings {r.ExtraIncome} by the last dawn, against its cost of {r.Cost}.");
            }
            if (r.Counts && r.Action.Kind == EconActionKind.KnockOut)
                lines.Add("Knocked out that night: it pays nothing at dawn (a mine with Sustainable Mining resets, a harbour doesn't grow), and with Temple wisdom it drops gold.");
            if (r.Branch?.Description is { Length: > 0 } desc) lines.Add(desc);
            if (p.InheritedFrom(r.Action)) lines.Add($"Part of {p.File.FullName(p.File.OwnerOf(r.Action))}, which this branches from: changing it changes that plan too.");
            else lines.Add("Left/right: move it a day. Select: change it.");
            return string.Join("\n", lines);
        }

        private static void DayTable(ModPage page, Projection p)
        {
            var w = p.World;
            var rows = new List<string[]>();
            if (w.DawnPending)
            {
                int income = p.Result.PendingDrops + p.Result.PendingBuildings + p.Result.PendingTreasure + p.Result.PendingExtra;
                rows.Add([$"Night {w.FirstDay - 1}", Gold(w.Gold), "-", N(income), N(p.Result.PendingBuildings), Gold(w.Gold + income), ""]);
            }
            foreach (var d in p.Result.Days)
            {
                var c = p.Compare.Days.FirstOrDefault(x => x.Day == d.Day);
                string spent = d.Spent == 0 ? "-" : d.Shortfall > 0 ? $"<color=#{RedHex}>{N(d.Spent)} ({N(d.Shortfall)} short)</color>" : N(d.Spent);
                string vs = c == null || p.Compare == p.Result ? "" : Signed(d.AtDawn - c.AtDawn);
                rows.Add([$"Day {d.Day}", Gold(d.Start), spent, N(d.Income), N(d.Buildings + d.Harvest), Gold(d.AtDawn), vs]);
            }
            string vsHeader = p.Compare == p.Result ? "" : "vs " + Short(p.OtherName, 9);
            AnalyticsGraph.Table(page.Custom("Days table", AnalyticsGraph.TableHeight(rows.Count)),
                ["", "Sunrise", "Spent", "Income", "Buildings", "At dawn", vsHeader], rows, [1f, 0.95f, 1.4f, 0.9f, 1f, 0.95f, 1f], rows.Count);
        }

        private static string About(EconWorld w) =>
            "Sunrise: gold when the day starts. Spent: what the plan builds that day (in red, with what's missing, when there isn't enough). " +
            "Income: interest at dusk" + (w.Interest ? "" : " (with the Interest perk)") + ", the night's enemy gold, gold the plan expects (Temple wisdom, estimates), " +
            "and what buildings pay (Buildings, including a mill's fields paying the day they're built)" + (w.TreasureHunter ? " plus Treasure Hunter" : "") + ". At dawn: gold the next day starts with.\n" +
            "Worked out from this map's buildings, costs and your perks: mines run dry, harbours grow, Builder's Guild upgrades a house each dusk, every coin is picked up at dusk. " +
            "Buildings only get knocked out where the plan says so. Not counted: shrines unlocking.";

        private void BuildAside(RectTransform aside, Projection p)
        {
            if (aside == null) return;
            var w = p.World;
            float width = V.AsideWidth(aside), y = 0f;
            var r = p.Result;
            int vsNothing = r.EndGold - p.Baseline.EndGold;
            var lines = new List<string>
            {
                $"<size=125%><b>{Coin(r.EndGold)}</b></size> at the end of the level" +
                    (p.Actions.Count > 0 ? $"  <color=#{DimHex}>{Signed(vsNothing)} vs doing nothing</color>" : ""),
            };
            if (p.Other != null) lines.Add($"{Signed(r.EndGold - p.Compare.EndGold)} vs {p.OtherName}");
            lines.Add($"Now {Coin(w.Gold)}" + (w.DawnPending ? $", {Signed(r.PendingDrops + r.PendingBuildings + r.PendingTreasure + r.PendingExtra)} by dawn" : "") +
                $" · buildings pay {r.NextDawnBuildings} at the next dawn, {r.LastDawnBuildings} at the last" +
                (r.LastDawnBuildings != p.Compare.LastDawnBuildings ? $" ({Signed(r.LastDawnBuildings - p.Compare.LastDawnBuildings)})" : ""));
            var shortDay = r.FirstShortDay;
            int shortDays = r.Days.Count(d => d.Shortfall > 0);
            lines.Add($"Spends {r.TotalSpent}" + (shortDay != null
                ? $" · <color=#{RedHex}>short by {shortDay.Shortfall} on day {shortDay.Day}</color>" + (shortDays > 1 ? $" <color=#{RedHex}>and {shortDays - 1} more</color>" : "")
                : p.Actions.Count > 0 ? " · always affordable" : ""));
            if (p.PerkNotes.Count > 0) lines.Add($"<color=#{DimHex}>{string.Join(". ", p.PerkNotes)}.</color>");
            y += V.Text(aside, string.Join("\n", lines), y, width, 25f) + 14f;

            var days = r.Days;
            if (days.Count == 0) return;
            var labels = days.Select(d => d.Day.ToString()).Append("End").ToArray();
            float[] Values(Func<DayResult, float> f, float end) => days.Select(f).Append(end).ToArray();
            var series = new List<GraphSeries>
            {
                new("At sunrise", ModStyle.Bright, Values(d => d.Start, r.EndGold)),
                new("After building", AnalyticsGraph.Bronze, Values(d => d.AfterSpend, float.NaN)),
            };
            if (p.Compare != r)
                series.Add(new GraphSeries(Capital(Short(p.OtherName, 16)), AnalyticsGraph.Fade(AnalyticsGraph.Dim, 0.9f),
                    days.Select(d => (float)(p.Compare.Days.FirstOrDefault(c => c.Day == d.Day)?.Start ?? float.NaN)).Append(p.Compare.EndGold).ToArray()));
            var shortCols = new HashSet<int>(days.Select((d, i) => (d, i)).Where(t => t.d.Shortfall > 0).Select(t => t.i));
            var actionCols = new HashSet<int>(days.Select((d, i) => (d, i)).Where(t => t.d.Actions > 0 || t.d.KnockedOut > 0).Select(t => t.i));
            float height = Mathf.Clamp(V.AsideHeight(aside) - y - 10f, 240f, 380f);
            V.Gold(V.Holder(aside, "Graph", y, width, height), width, height, labels, series, shortCols, actionCols);
        }

        // ---- Add to plan --------------------------------------------------------------------------------------

        internal void OpenPicker(int day, string replace)
        {
            var s = Snap();
            if (s == null) return;
            int first = pickMode != 0 && s.World.DawnPending ? s.World.FirstDay - 1 : s.World.FirstDay;
            pickDay = Mathf.Clamp(day, first, Math.Max(first, s.World.LastNight));
            replaceId = replace;
            pickSlot = -1;
            pickSelected.Clear();
            pickerFrame.SetTitle(replace == null ? "Add to plan" : "Change building");
            if (pickerFrame.IsOpen) pickerFrame.Refresh();
            else pickerFrame.Open();
        }

        private void BuildPicker(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var w = p.World;
            var others = p.Actions.Where(a => a.Id != replaceId).ToList();
            int first = pickMode != 0 && w.DawnPending ? w.FirstDay - 1 : w.FirstDay;
            pickDay = Mathf.Clamp(pickDay, first, Math.Max(first, w.LastNight));
            var state = EconSim.StateOn(w, others, pickDay);
            var planned = new HashSet<int>(p.Actions.Where(a => a.IsStep).Select(a => w.Find(a.Slot)?.Index ?? -1));
            if (pickSlot >= 0 && pickSlot < state.Slots.Count)
            {
                BuildChoices(page, p, state, state.Slots[pickSlot], planned);
                return;
            }
            if (replaceId == null)
            {
                ModFields.Choice(page, "Add", Modes, () => pickMode, i =>
                {
                    pickMode = i;
                    pickSelected.Clear();
                    pickerFrame.Refresh();
                }, tooltip: () => "A build or upgrade; a building you expect to be knocked out on a night (it pays nothing at dawn); or gold you expect to find on a night.");
            }
            Num(page, pickMode == 0 ? "Day" : "Night", () => pickDay, v => { pickDay = v; pickSelected.Clear(); pickerFrame.Refresh(); }, first, w.LastNight,
                () => pickMode == 0 ? "The day to do it. Buildings are listed as they'd be that day, after the plan's earlier steps." : "The night it happens.",
                v => pickMode == 0 ? $"Day {v}" : $"Night {v}");

            if (pickMode == 2)
            {
                Num(page, "Gold", () => pickGold, v => { pickGold = v; pickerFrame.Refresh(); }, 1, 99999, () => "How much gold you expect that night beyond the enemies' drops.");
                page.Button($"Add {pickGold} gold on night {pickDay}", () =>
                {
                    p.Plan.Actions.Add(new EconAction { Kind = EconActionKind.Gold, Day = pickDay, Amount = pickGold });
                    Committed();
                });
                ShowSlot(page.Aside, p, state, null, planned, pickDay);
                return;
            }
            bool lose = pickMode == 1;
            if (!lose)
            {
                ModFields.Choice(page, "Show", Filters, () => pickFilter, i => { pickFilter = i; pickerFrame.Refresh(); },
                    tooltip: () => "Buildings you can add: the ones that can be built or upgraded that day. Income buildings: everything that earns gold. Every slot: the whole map.");
            }
            if (replaceId == null)
            {
                ModFields.Toggle(page, "Pick several", () => pickMulti, v => { pickMulti = v; pickSelected.Clear(); pickerFrame.Refresh(); },
                    () => "On: selecting a building ticks it, and one button adds every ticked one. Off: selecting one adds it.");
                if (pickMulti && pickSelected.Count > 0)
                    page.Button($"Add the {pickSelected.Count} ticked", () => CommitMany(p, state, lose));
            }

            var slots = state.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0 && (e.Active || e.Activator >= 0)).Where(e => lose
                ? e.Level >= 1
                : pickFilter switch
                {
                    0 => e.Active && e.Level < e.MaxLevel && EconSim.CanUpgrade(state, e),
                    1 => w.Slots[e.Index].EverPays,
                    _ => true,
                }).ToList();
            pickerRows = slots.Count;
            var aside = page.Aside;
            if (slots.Count == 0)
            {
                page.Text(lose ? "Nothing is built by then." : pickFilter == 0 ? "Nothing can be built or upgraded that day. Try another day, or show every slot." : "No buildings.");
                ShowSlot(aside, p, state, null, planned, pickDay);
                return;
            }
            // Income buildings first, then the rest, each by name.
            foreach (var group in slots.GroupBy(e => e.TypeName).OrderBy(g => w.Slots[g.First().Index].EverPays ? 0 : 1).ThenBy(g => g.Key))
            {
                var header = page.Header(group.Count() > 1 ? $"{group.Key} <size=70%><color=#{DimHex}>×{group.Count()}</color></size>" : group.Key);
                header.fontSize = 32f;
                string type = group.Key;
                int buildable = group.Count(e => e.Level == 0 && e.Active && EconSim.CanUpgrade(state, e));
                if (!lose && replaceId == null && buildable >= 2)
                {
                    int Count() => Mathf.Clamp(pickCounts.TryGetValue(type, out int c) ? c : 2, 1, buildable);
                    Num(page, $"How many {type}", Count, v => { pickCounts[type] = v; pickerFrame.Refresh(); }, 1, buildable,
                        () => $"How many to build at once: the {type} slots that can be built on day {pickDay}, nearest the castle first ({buildable} can).");
                    var several = page.Button($"Build {Count()} {type}", () =>
                    {
                        AddSeveral(p, type, pickDay, Count());
                        Committed();
                    });
                    several.gameObject.name = "Several " + type;
                }
                foreach (var e in group)
                {
                    var slot = e;
                    string name = slot.Label.StartsWith(slot.TypeName) && slot.Label.Length > slot.TypeName.Length
                        ? slot.Label.Substring(slot.TypeName.Length).TrimStart(' ', '·').Trim() : slot.Level == 0 ? "Build" : $"Level {slot.Level}";
                    if (name.Length > 0 && char.IsDigit(name[0])) name = "No. " + name;
                    if (pickMulti && pickSelected.Contains(slot.Index)) name = $"<color=#{ModStyle.Hex(ModStyle.Accent)}>✓</color> {name}";
                    string value = lose ? (slot.Pays && slot.Income > 0 ? $"pays {slot.Income} that dawn" : $"<color=#{DimHex}>no income</color>") : NextStep(state, slot);
                    var row = page.Row(name, value, (string)null, () =>
                    {
                        if (pickMulti && replaceId == null)
                        {
                            if (!pickSelected.Remove(slot.Index)) pickSelected.Add(slot.Index);
                            pickerFrame.Refresh();
                        }
                        else if (lose) Lose(p, slot);
                        else Pick(p, state, slot);
                    });
                    row.Button.gameObject.name = "Slot " + slot.Index;
                    row.Button.onSelectionStateChange.AddListener(() =>
                    {
                        if (IsSelected(row.Button)) ShowSlot(aside, p, state, slot, planned, pickDay);
                    });
                }
            }
            ShowSlot(aside, p, state, null, planned, pickDay);
        }

        private static bool IsSelected(TFUITextButton b) =>
            b != null && b.CurrentState is ThronefallUIElement.SelectionState.Selected or ThronefallUIElement.SelectionState.FocussedAndSelected;

        /// <summary>"build · 8 · +2 per dawn", or why it can't go further.</summary>
        private static string NextStep(EconWorld state, EconSlot e)
        {
            if (e.Level >= e.MaxLevel) return $"<color=#{DimHex}>level {e.Level}, highest</color>";
            if (!e.Active)
            {
                var act = e.Activator >= 0 ? state.Slots[e.Activator] : null;
                return $"<color=#{DimHex}>unlocks with {act?.TypeName ?? "?"} level {e.ActivatorLevel + 1}</color>";
            }
            if (!EconSim.CanUpgrade(state, e))
                return $"<color=#{DimHex}>needs {state.Slots[e.Root].TypeName} level {e.Level + e.RootLevelDifference + 1}</color>";
            var step = e.Steps[e.Level];
            var incomes = step.Branches.Select((b, i) => EconSim.StepIncome(e, i)).Distinct().OrderBy(v => v).ToList();
            string income = incomes.All(v => v == 0) ? "" : " · " + string.Join("/", incomes.Select(Signed)) + " per dawn";
            string what = e.Level == 0 ? "build" : $"to level {e.Level + 1}";
            return $"{what} · {Coin(step.Cost)}{income}";
        }

        private void Pick(Projection p, EconWorld state, EconSlot slot)
        {
            if (slot.Level >= slot.MaxLevel || slot.Level >= slot.Steps.Count)
            {
                Notify.Show($"{slot.Label} is already at its highest level by day {pickDay}.", NotifyKind.Info, key: "econ-pick");
                return;
            }
            if (slot.Steps[slot.Level].IsChoice)
            {
                pickSlot = slot.Index;
                pickerFrame.Refresh();
                return;
            }
            Commit(p, slot, slot.Level + 1, null);
        }

        private void Lose(Projection p, EconSlot slot)
        {
            var existing = replaceId == null ? null : p.File.Scenarios.SelectMany(s => s.Actions).FirstOrDefault(a => a.Id == replaceId);
            if (existing != null)
            {
                existing.Slot = slot.Id;
                existing.Day = pickDay;
                editorDirty = true;
            }
            else p.Plan.Actions.Add(new EconAction { Kind = EconActionKind.KnockOut, Day = pickDay, Slot = slot.Id });
            Committed();
        }

        private void CommitMany(Projection p, EconWorld state, bool lose)
        {
            foreach (int i in pickSelected.OrderBy(i => i))
            {
                var e = state.Slots[i];
                if (lose) p.Plan.Actions.Add(new EconAction { Kind = EconActionKind.KnockOut, Day = pickDay, Slot = e.Id });
                else if (e.Level < e.Steps.Count)
                {
                    var step = e.Steps[e.Level];
                    p.Plan.Actions.Add(new EconAction { Day = pickDay, Slot = e.Id, Level = e.Level + 1, Choice = step.IsChoice ? step.Branches[EconSim.BranchIndex(step, null)].Key : null });
                }
            }
            pickSelected.Clear();
            Committed();
        }

        private void BuildChoices(ModPage page, Projection p, EconWorld state, EconSlot slot, HashSet<int> planned)
        {
            var step = slot.Steps[Math.Min(slot.Level, slot.Steps.Count - 1)];
            page.Header(slot.Label);
            page.Text($"Day {pickDay}, {(slot.Level == 0 ? "build" : $"level {slot.Level + 1}")} for {Coin(step.Cost)}: pick one.");
            for (int i = 0; i < step.Branches.Count; i++)
            {
                var b = step.Branches[i];
                int income = EconSim.StepIncome(slot, i);
                var parts = new List<string>();
                if (b.Locked) parts.Add($"<color=#{DimHex}>locked</color>");
                if (income != 0) parts.Add($"{Signed(income)} per dawn");
                if (b.StartsGuild) parts.Add("a free house upgrade each dusk");
                if (b.StartsCastleUp) parts.Add("cheaper walls and towers");
                if (b.Prayers) parts.Add("shrines pay nothing that dawn");
                if (b.IsWisdom) parts.Add($"{b.WisdomGold} per building lost that night, but drains them");
                page.Row(b.Name, string.Join(" · ", parts), b.Description, () =>
                {
                    if (b.Locked)
                    {
                        Notify.Show($"{b.Name} is locked in this run.", NotifyKind.Warning, key: "econ-pick");
                        return;
                    }
                    Commit(p, slot, slot.Level + 1, b.Key);
                });
            }
            page.Button("Back to the list", () =>
            {
                pickSlot = -1;
                pickerFrame.Refresh();
            });
            ShowSlot(page.Aside, p, state, slot, planned, pickDay);
        }

        private void Commit(Projection p, EconSlot slot, int level, string choice)
        {
            var existing = replaceId == null ? null : p.File.Scenarios.SelectMany(s => s.Actions).FirstOrDefault(a => a.Id == replaceId);
            if (existing != null)
            {
                existing.Slot = slot.Id;
                existing.Level = level;
                existing.Choice = choice;
                existing.Day = pickDay;
                editId = existing.Id;
                editorDirty = true;
            }
            else p.Plan.Actions.Add(new EconAction { Day = pickDay, Slot = slot.Id, Level = level, Choice = choice });
            Committed();
        }

        private void Committed()
        {
            lastDay = pickDay;
            Save();
            hostDirty = true;
            // Back to the editor (when changing an item's building) or the tab: both rebuild when they're active again.
            pickerFrame.Close();
        }

        /// <summary>The map with <paramref name="slot"/> lit, and what it is and does on the picked day.</summary>
        private void ShowSlot(RectTransform aside, Projection p, EconWorld state, EconSlot slot, HashSet<int> planned, int day)
        {
            if (aside == null) return;
            foreach (Transform child in aside.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            float width = V.AsideWidth(aside), y = 0f;
            float mapHeight = Mathf.Min(width * 0.62f, 360f);
            var marked = new HashSet<int>(planned);
            foreach (int i in pickSelected) marked.Add(i);
            V.Map(V.Holder(aside, "Map", y, width, mapHeight), width, mapHeight, state, slot?.Index ?? -1, marked);
            y += mapHeight + 6f;
            var legend = V.Holder(aside, "Legend", y, width, 30f);
            V.MapLegend(legend, width);
            y += 36f;
            if (slot == null)
            {
                V.Text(aside, "Select a building to see where it is. North is up the screen.", y, width, 23f, color: ModStyle.Dim);
                return;
            }
            var now = p.World.Slots[slot.Index];
            var lines = new List<string> { $"<size=120%><b>{slot.Label}</b></size>" };
            lines.Add(now.Level == 0 ? "Not built yet." : $"Level {now.Level} of {now.MaxLevel} now, paying {now.Income} per dawn.");
            if (slot.Level != now.Level) lines.Add($"Level {slot.Level} on day {day} with the plan's earlier steps.");
            if (slot.Level < slot.Steps.Count && slot.Level < slot.MaxLevel)
            {
                var step = slot.Steps[slot.Level];
                lines.Add($"Next: {NextStep(state, slot)}");
                if (step.IsChoice)
                    lines.Add($"<color=#{DimHex}>Choices: {string.Join(", ", step.Branches.Select(b => b.Name + (b.Locked ? " (locked)" : "")))}</color>");
                else if (!string.IsNullOrEmpty(step.Text)) lines.Add($"<color=#{DimHex}>{step.Text}</color>");
            }
            if (slot.Modifiers.Any(m => m.Kind == EconModifierKind.Mine)) lines.Add($"<color=#{DimHex}>Mines pay less each dawn.</color>");
            if (slot.Modifiers.FirstOrDefault(m => m.Kind == EconModifierKind.Harbour) is { } h) lines.Add($"<color=#{DimHex}>Harbours pay more each dawn, up to {h.Max}.</color>");
            V.Text(aside, string.Join("\n", lines), y, width, 23f);
        }

        // ---- Plan item --------------------------------------------------------------------------------------------

        internal void OpenEditor(string id)
        {
            editId = id;
            if (editorFrame.IsOpen) editorFrame.Refresh();
            else editorFrame.Open();
        }

        private void BuildEditor(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var w = p.World;
            var a = p.Actions.FirstOrDefault(x => x.Id == editId);
            if (a == null) { page.Text("This isn't in the plan any more."); return; }
            var holder = p.File.OwnerOf(a) ?? p.Plan;
            var r = p.Result.Actions.First(x => x.Action == a);
            var slot = r.Slot ?? w.Find(a.Slot);
            page.Header(a.Kind == EconActionKind.Gold ? "Gold found" : slot?.Label ?? "A building not on this map");
            if (holder != p.Plan)
                page.Text($"<color=#{DimHex}>Part of {p.File.FullName(holder)}, which this branches from: changes here change it there too.</color>");

            int first = a.IsStep || !w.DawnPending ? w.FirstDay : w.FirstDay - 1;
            Num(page, a.IsStep ? "Day" : "Night", () => a.Day, v =>
            {
                a.Day = v;
                lastDay = v;
                ChangedOnTop(editorFrame);
            }, Math.Max(1, Math.Min(a.Day, first)), w.LastNight, () => a.IsStep ? "The day to do it." : "The night it happens.", v => a.IsStep ? $"Day {v}" : $"Night {v}");

            if (a.Kind == EconActionKind.Gold)
            {
                Num(page, "Gold", () => a.Amount ?? 0, v => { a.Amount = v; ChangedOnTop(editorFrame); }, 0, 99999, () => "How much gold you expect that night.");
            }
            else if (a.IsStep && slot != null)
            {
                Num(page, "Level", () => a.Level, v =>
                {
                    a.Level = v;
                    var st = slot.Steps[a.Level - 1];
                    a.Choice = st.IsChoice ? st.Branches[EconSim.BranchIndex(st, a.Choice)].Key : null;
                    ChangedOnTop(editorFrame);
                }, 1, Math.Max(1, slot.MaxLevel), () => "Which level this step takes the building to (1 builds it). A step needs the level before it by then.",
                    v => v <= 1 ? "Build" : $"Upgrade to level {v}");
                var step = a.Level >= 1 && a.Level <= slot.Steps.Count ? slot.Steps[a.Level - 1] : null;
                if (step != null && step.IsChoice)
                {
                    int current = EconSim.BranchIndex(step, a.Choice);
                    page.Stepper("Choice", () =>
                    {
                        var b = step.Branches[EconSim.BranchIndex(step, a.Choice)];
                        return b.Locked ? $"{b.Name} (locked)" : b.Name;
                    }, d =>
                    {
                        int i = (EconSim.BranchIndex(step, a.Choice) + Math.Sign(d) + step.Branches.Count) % step.Branches.Count;
                        a.Choice = step.Branches[i].Key;
                        ChangedOnTop(editorFrame);
                    }, null, step.Branches[current].Description);
                }
                var branch = step != null && step.Branches.Count > 0 ? step.Branches[EconSim.BranchIndex(step, a.Choice)] : null;
                if (branch != null && branch.IsWisdom)
                {
                    Num(page, "Wisdom gold", () => a.Amount ?? r.Wisdom, v => { a.Amount = v; ChangedOnTop(editorFrame); }, 0, 99999,
                        () => $"Gold from buildings destroyed that night: {branch.WisdomGold} each. By default it's worked out from the buildings the plan marks lost that night; type the amount you expect instead.",
                        v => a.Amount == null ? $"{v} (from {(branch.WisdomGold > 0 ? v / branch.WisdomGold : 0)} lost)" : $"{v}");
                    if (a.Amount != null)
                        page.Button("Work it out from the buildings lost", () => { a.Amount = null; ChangedOnTop(editorFrame); },
                            "Use the buildings the plan marks as lost that night instead of the typed amount.");
                    if (branch.WisdomDrain > 0f)
                        page.Text($"<color=#{DimHex}>Wisdom also drains every building (not shrines) by {branch.WisdomDrain * 100f:0.#}% of its health a second: " +
                            $"unrepaired, one dies after about {1f / branch.WisdomDrain:0} seconds of night. Mark the ones you expect to lose.</color>");
                    var night = EconSim.StateOn(w, p.Actions, a.Day);
                    var standing = night.Slots.Where(x => x.Level >= 1 && x.IsBuilding && !x.IsShrine && !x.Indestructible
                        && !p.Actions.Any(k => k.Kind == EconActionKind.KnockOut && k.Day == a.Day && k.Slot == x.Id)).ToList();
                    if (standing.Count > 0)
                        page.Button($"Lose every building that night ({standing.Count})", () =>
                        {
                            foreach (var x in standing) holder.Actions.Add(new EconAction { Kind = EconActionKind.KnockOut, Day = a.Day, Slot = x.Id });
                            ChangedOnTop(editorFrame);
                        }, "Marks every building standing that night as lost: none pays at dawn, and each drops Wisdom's gold. Remove the ones you'd save.");
                }
                page.Row("Cost", r.Counts ? Coin(r.Cost) : "-", "Gold this step costs on its day (Castle Up and perks included).");
                page.Row("Buildings pay", r.Counts ? $"{Signed(r.IncomeDelta)} per dawn" : "-", "How much more the building pays at each dawn after this step.");
                string payback = Payback(r);
                if (payback != null) page.Row("Payback", Capital(payback), "When the extra income has repaid the cost, counting mines running dry and harbours growing.");
            }
            if (!r.Counts || r.Status == ActionStatus.Short)
                page.Row("Status", r.Status == ActionStatus.Short ? $"<color=#{RedHex}>short by {r.Shortfall}</color>" : $"<color=#{DimHex}>{Detail(r)}</color>", Tooltip(r, p));

            if (a.IsStep && holder.Actions.Count(x => x.Day == a.Day && x.IsStep) > 1)
            {
                page.Button("Do it earlier that day", () => { if (Reorder(holder, a, -1)) ChangedOnTop(editorFrame); },
                    "Steps on the same day are done in order; it matters when one unlocks another or gold runs short.");
                page.Button("Do it later that day", () => { if (Reorder(holder, a, 1)) ChangedOnTop(editorFrame); });
            }
            if (a.Kind != EconActionKind.Gold)
            {
                page.Button("Change building", () =>
                {
                    pickMode = a.IsStep ? 0 : 1;
                    OpenPicker(a.Day, a.Id);
                }, "Pick another building for this.");
            }
            if (w.FirstDay <= w.LastNight)
            {
                page.Button($"Branch from day {Math.Max(a.Day, w.FirstDay)}", () =>
                {
                    Branch(p.File, p.Plan, Math.Max(a.Day, w.FirstDay));
                    Save();
                    hostDirty = true;
                    editorFrame.Close();
                }, "A branch keeps this plan's items before that day (this one included only if it's earlier) and goes its own way from then on, compared with this plan.");
            }
            page.Button("Remove from plan", () =>
            {
                holder.Actions.Remove(a);
                Save();
                hostDirty = true;
                editorFrame.Close();
            });

            var planned = new HashSet<int>(p.Actions.Where(x => x.IsStep).Select(x => w.Find(x.Slot)?.Index ?? -1));
            var state = EconSim.StateOn(w, p.Actions.Where(x => x != a).ToList(), Mathf.Max(a.Day, w.FirstDay));
            ShowSlot(page.Aside, p, state, slot == null ? null : state.Slots[slot.Index], planned, a.Day);
        }

        // ---- Plans ------------------------------------------------------------------------------------------------

        internal void OpenPlans()
        {
            confirm = null;
            if (plansFrame.IsOpen) plansFrame.Refresh();
            else plansFrame.Open();
        }

        private void BuildPlans(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var f = p.File;
            var w = p.World;
            var snapWorld = p.Snap.World;
            // Roots, each followed by its branches.
            var order = new List<PlannerScenario>();
            void Add(PlannerScenario s)
            {
                if (order.Contains(s)) return;
                order.Add(s);
                foreach (var c in f.Scenarios.Where(c => c.Parent == s.Id)) Add(c);
            }
            foreach (var s in f.Scenarios.Where(s => s.Parent == null)) Add(s);
            foreach (var s in f.Scenarios) Add(s);
            var results = order.ToDictionary(s => s, s => s == p.Plan ? p.Result : EconSim.Run(EconPerks.Apply(snapWorld, f.EffectivePerks(s)), f.Effective(s), paybacks: false));

            page.Header("Side by side");
            var rows = order.Select(s =>
            {
                var r = results[s];
                var shortDay = r.FirstShortDay;
                return new[]
                {
                    new string(' ', f.Ancestors(s).Count * 2) + (f.Ancestors(s).Count > 0 ? "› " : "") + s.Name + (s == p.Plan ? " ◆" : ""),
                    f.Effective(s).Count.ToString(), N(r.TotalSpent), N(r.LastDawnBuildings), N(r.EndGold),
                    shortDay != null ? $"<color=#{RedHex}>day {shortDay.Day}</color>" : "-",
                };
            }).ToList();
            AnalyticsGraph.Table(page.Custom("Plans table", AnalyticsGraph.TableHeight(rows.Count)),
                ["Plan", "Items", "Spent", "Last dawn", "End gold", "Short"], rows, [2.2f, 0.7f, 0.9f, 1f, 1f, 0.9f], rows.Count);

            page.Header("Show");
            foreach (var s in order)
            {
                var scenario = s;
                string mark = scenario == p.Plan ? $"<color=#{ModStyle.Hex(ModStyle.Accent)}>◆</color> " : "";
                string fork = scenario.Parent == null ? "" : scenario.ForkDay == int.MaxValue ? " · same steps" : $" · from day {scenario.ForkDay}";
                var row = page.Row(mark + f.FullName(scenario), $"ends with {Coin(results[scenario].EndGold)}{fork}",
                    "Select to show this plan.", () =>
                    {
                        f.Current = scenario.Id;
                        if (scenario.Parent != null && f.Compare == null) f.Compare = scenario.Parent;
                        f.Normalize();
                        Save();
                        hostDirty = true;
                        plansFrame.Close();
                    });
                row.Button.gameObject.name = "Plan " + scenario.Id;
            }

            page.Header("This plan");
            page.TextInput("Name", () => p.Plan.Name, text =>
            {
                text = text?.Trim();
                if (string.IsNullOrEmpty(text)) return;
                p.Plan.Name = text;
                ChangedOnTop(plansFrame);
            }, () => "Select and type to rename it.", maxLength: 40);
            page.Button("New plan", () =>
            {
                var s = NewPlan(f, w, "Plan");
                f.Scenarios.Add(s);
                f.Current = s.Id;
                ChangedOnTop(plansFrame);
            }, "A plan of its own, starting with the castle's build.");
            page.Button("Copy this plan", () =>
            {
                var s = p.Plan.CopyAs(f.FreeName(p.Plan.Name + " copy"));
                f.Scenarios.Add(s);
                f.Current = s.Id;
                ChangedOnTop(plansFrame);
            }, "A copy that goes its own way. To share the early days, branch instead (on the Economy tab).");
            if (p.Plan.Actions.Count > 0)
            {
                page.Button(confirm == "clear" ? $"Press again to clear \"{p.Plan.Name}\"" : "Clear its items", () =>
                {
                    if (confirm != "clear") { confirm = "clear"; plansFrame.Refresh(); return; }
                    confirm = null;
                    p.Plan.Actions.Clear();
                    ChangedOnTop(plansFrame);
                });
            }
            page.Button(confirm == "delete" ? $"Press again to delete \"{p.Plan.Name}\"" : "Delete this plan", () =>
            {
                if (confirm != "delete") { confirm = "delete"; plansFrame.Refresh(); return; }
                confirm = null;
                f.Delete(p.Plan);
                ChangedOnTop(plansFrame);
            }, "Its branches keep the items they had from it.");
        }

        // ---- Perks ------------------------------------------------------------------------------------------------

        private void BuildPerks(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var f = p.File;
            var perks = p.Snap.World.Perks;
            page.Text($"Perks that move gold, as {p.PlanName} uses them. Changing one works it out as if the run had started with it: building perks change every step's cost and income, start-gold perks only count on day 1.");
            if (perks.Count == 0) page.Text("No perk on this map changes gold.");
            var effective = f.EffectivePerks(p.Plan);
            foreach (var perk in perks.OrderBy(x => x.Kind == EconPerkKind.Building ? 1 : 0).ThenBy(x => x.Name))
            {
                var k = perk;
                bool On() => effective.TryGetValue(k.Key, out bool v) ? v : k.Equipped;
                ModFields.Toggle(page, k.Name + (k.Equipped ? $" <size=70%><color=#{DimHex}>equipped</color></size>" : ""), On, v =>
                {
                    if (v == k.Equipped && !f.Ancestors(p.Plan).Any(a => a.Perks.ContainsKey(k.Key))) p.Plan.Perks.Remove(k.Key);
                    else p.Plan.Perks[k.Key] = v;
                    ChangedOnTop(perksFrame);
                }, () => PerkText(k, p.Snap.World));
            }
            if (p.PerkNotes.Count > 0) page.Text($"<color=#{DimHex}>{string.Join(". ", p.PerkNotes)}.</color>");
            if (p.Plan.Perks.Count > 0)
                page.Button("Back to your loadout", () => { p.Plan.Perks.Clear(); ChangedOnTop(perksFrame); }, "Drop this plan's perk changes.");
            page.Button("Try a loadout in a branch", () =>
            {
                Branch(f, p.Plan, int.MaxValue, "perks");
                ChangedOnTop(perksFrame);
            }, "A branch with the same steps as this plan, compared with it, where you change perks.");
        }

        private static string PerkText(EconPerk p, EconWorld w) => p.Kind switch
        {
            EconPerkKind.Interest => $"1 gold at dusk per {p.Each} gold you hold, up to {p.Max}.",
            EconPerkKind.TreasureHunter => $"{p.T1}, {p.T2} and {p.T3} gold at the dawns before the last three nights.",
            EconPerkKind.RoyalMint => $"{p.Amount} more gold at the start.",
            EconPerkKind.Loan => $"{p.Amount} gold at the start, paid back with {p.Amount2} of the first nights' enemy gold.",
            EconPerkKind.RatGod => $"Enemies drop {p.Factor * 100f:0}% of their gold.",
            EconPerkKind.Outpost => "Building an outpost builds some of its towers for free.",
            EconPerkKind.DestructionGod => "Destroyed buildings stay down a second night (and pay nothing at either dawn).",
            _ => "Changes some buildings' costs or income on this map.",
        };

        // ---- Income sources ---------------------------------------------------------------------------------------

        internal void OpenSources()
        {
            var s = Snap();
            if (s == null) return;
            sourcesDay = Mathf.Clamp(sourcesDay == 0 ? s.World.FirstDay : sourcesDay, s.World.FirstDay, Math.Max(s.World.FirstDay, s.World.LastNight));
            if (sourcesFrame.IsOpen) sourcesFrame.Refresh();
            else sourcesFrame.Open();
        }

        private void BuildSources(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var w = p.World;
            if (w.FirstDay > w.LastNight) { page.Text("There are no days left to build on."); return; }
            Num(page, "If built on day", () => sourcesDay, v => { sourcesDay = v; sourcesFrame.Refresh(); }, w.FirstDay, w.LastNight,
                () => "Each building is added to your current plan on this day (unlocks and the castle's level ignored), and compared with the plan without it.",
                v => $"Day {v}");
            var sources = Sources(p, sourcesDay);
            if (sources.Count == 0) { page.Text("No income building can be built or upgraded that day."); return; }
            page.Text($"<color=#{DimHex}>Bar: gold it pays by the end (gold) against its cost (copper); the number is what's left over. Each line is one more level, counting the ones before.</color>");
            var bars = new List<V.SourceBar>();
            foreach (var src in sources)
                foreach (var e in src.Levels)
                    bars.Add(new V.SourceBar
                    {
                        Label = $"{src.Name}{(e.Level > 1 ? $" lv {e.Level}" : "")}{(e.Choice != null ? $": {e.Choice}" : "")}",
                        Cost = e.Cost, Earned = e.Earned,
                        Note = (e.PaybackDawn is int d ? $"back by dawn {d}" : "never pays back") + $" · {e.FirstDawn}→{e.LastDawn}/night" + (e.Blocked != null ? " · " + e.Blocked.ToLowerInvariant() : ""),
                        First = e == src.Levels[0],
                    });
            V.SourceBars(page.Custom("Sources chart", V.SourceBarsHeight(bars.Count)), 820f, bars);
            foreach (var src in sources.Where(s => s.Note != null))
                page.Row(src.Name, $"<color=#{DimHex}>{src.Note}</color>", $"{src.Slot.Label}: the one this compares.");
            OtherSources(page.Aside, p);
        }

        private static void OtherSources(RectTransform aside, Projection p)
        {
            if (aside == null) return;
            var w = p.World;
            var r = p.Result;
            float width = V.AsideWidth(aside);
            var lines = new List<string> { "<size=120%><b>Other gold</b></size>" };
            lines.Add($"Enemies: {r.Days.Sum(d => d.Drops) + r.PendingDrops} more by the end ({string.Join(", ", r.Days.Select(d => d.Drops))}).");
            var interest = w.Perks.FirstOrDefault(x => x.Kind == EconPerkKind.Interest);
            if (w.Interest) lines.Add($"Interest: {r.Days.Sum(d => d.Interest)} by the end with this plan; holding {w.InterestEach * w.InterestMax} gold at dusk pays the most, {w.InterestMax}.");
            else if (interest != null) lines.Add($"Interest (not in this plan): up to {interest.Max} a night, for holding {interest.Each * interest.Max} gold at dusk.");
            var th = w.Perks.FirstOrDefault(x => x.Kind == EconPerkKind.TreasureHunter);
            if (w.TreasureHunter) lines.Add($"Treasure Hunter: {r.Days.Sum(d => d.Treasure) + r.PendingTreasure} still to come.");
            else if (th != null) lines.Add($"Treasure Hunter (not in this plan): {th.T1 + th.T2 + th.T3} over the last nights.");
            int shrines = w.Slots.Count(s => s.IsShrine);
            if (shrines > 0) lines.Add($"Shrines: {shrines} here, paying once deaths nearby unlock them (not plannable); Temple prayers stop them for a night.");
            var outposts = w.Slots.Where(s => s.OutpostTowers > 0).ToList();
            if (outposts.Count > 0)
            {
                int value = outposts.Sum(o => w.Slots.Where(c => c.Activator == o.Index && c.ObjectName.Contains("Tower") && c.Level == 0 && c.Steps.Count > 0)
                    .OrderByDescending(c => (c.MapX - o.MapX) * (c.MapX - o.MapX) + (c.MapY - o.MapY) * (c.MapY - o.MapY)).Take(o.OutpostTowers).Sum(c => c.Steps[0].Cost));
                lines.Add($"Outposts{(w.Outpost ? "" : " (with the Outpost perk)")}: build {outposts.Sum(o => o.OutpostTowers)} towers for free, worth {value}.");
            }
            if (w.Slots.Any(s => s.Steps.Any(st => st.Branches.Any(b => b.IsWisdom))))
                lines.Add("Temple wisdom: gold for each building lost that night; plan the losses (Add to plan) to see it.");
            V.Text(aside, string.Join("\n", lines), 0f, width, 23f);
        }

        // ---- Text helpers -----------------------------------------------------------------------------------------

        internal static string Coin(int amount) => $"{(amount < 0 ? "-" : "")}{Mathf.Abs(amount):N0}<sprite name=\"coin\">";
        private static string Gold(int v) => v < 0 ? $"<color=#{RedHex}>{v:N0}</color>" : v.ToString("N0");
        private static string N(int v) => v.ToString("N0");
        private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        private static string Short(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
