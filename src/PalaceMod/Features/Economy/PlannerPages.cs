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
      Economy tab (in Run Analytics): the plan's steps (left/right moves one a day, apply edits it), a button to add one,
        and a day-by-day table; beside it, fixed, the totals and the gold graph.
      Add to plan: pick the day, then a building, grouped by type, as it would be that day; choices come next for a
        step that has them. The map beside the list shows where the selected building is.
      Plan step: change the day, level or choice, the order within the day, the building, or remove it.
      Plans: switch, add, copy, rename, clear or delete this map's plans.
    The three are frames opened on top of Run Analytics, so Back returns to the tab, which then rebuilds.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private ModFrame host, pickerFrame, editorFrame, plansFrame;
        private bool hostDirty;

        // Picker state.
        private int pickDay, pickFilter, pickSlot = -1, pickerRows;
        private string replaceId;
        private int lastDay;
        private static readonly string[] Filters = ["Buildings you can add", "Income buildings", "Every slot"];

        // Editor and plans state.
        private string editId;
        private string confirm;

        private static readonly string DimHex = ModStyle.Hex(ModStyle.Dim);
        private static readonly string RedHex = ModStyle.Hex(V.Red);

        private void BuildFrames()
        {
            pickerFrame = new ModFrame("Add to plan").SetContent(BuildPicker);
            editorFrame = new ModFrame("Plan step").SetContent(BuildEditor);
            plansFrame = new ModFrame("Plans").SetContent(BuildPlans);
            plansFrame.Activated += () => confirm = null;
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
            if (pickerFrame.IsOpen) pickerFrame.Refresh();
            else if (editorFrame.IsOpen) editorFrame.Refresh();
            else if (plansFrame.IsOpen) plansFrame.Refresh();
            else if (host != null && host.IsOpen) { hostDirty = false; host.Refresh(); }
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

            page.Stepper("Plan", () => p.Plan.Name, d =>
            {
                var list = p.File.Scenarios;
                int i = (list.IndexOf(p.Plan) + Math.Sign(d) + list.Count) % list.Count;
                p.File.Current = list[i].Id;
                p.File.Normalize();
                ChangedHere();
            }, OpenPlans, "Left/right switches between your plans for this map. Select it to add, copy, rename or delete plans.");
            page.Stepper("Compare with", () => Capital(p.OtherName), d =>
            {
                var options = new List<string> { null };
                options.AddRange(p.File.Scenarios.Where(s => s != p.Plan).Select(s => s.Id));
                int i = (options.IndexOf(p.Other?.Id) + Math.Sign(d) + options.Count) % options.Count;
                p.File.Compare = options[i];
                ChangedHere();
            }, null, "What the graph's dim line and the table's last column compare this plan with: doing nothing, or another plan.");

            page.Header(w.FirstDay <= w.LastNight ? $"Plan, day {w.FirstDay} to {w.LastNight}" : "Plan");
            var results = Ordered(p);
            if (results.Count == 0)
                page.Text("Nothing planned yet. Add the builds and upgrades you're weighing up: the graph and the table show the gold they leave you each day.");
            else
                page.Text($"<color=#{DimHex}>Left/right moves a step to another day. Select it to change or remove it.</color>");
            foreach (var r in results) ActionRow(page, p, r);
            if (w.FirstDay <= w.LastNight)
                page.Button("Add a build or upgrade", () => OpenPicker(lastDay >= w.FirstDay ? lastDay : w.FirstDay, null),
                    "Pick a building on this map (built or not) and the day to build or upgrade it.");
            else
                page.Text("This is the last night: there are no days left to plan.");

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

        private static List<ActionResult> Ordered(Projection p)
        {
            var order = EconSim.Ordered(p.Plan.Actions);
            return order.Select(a => p.Result.Actions.First(r => r.Action == a)).ToList();
        }

        private void ActionRow(ModPage page, Projection p, ActionResult r)
        {
            var a = r.Action;
            var w = p.World;
            string title = Title(r);
            var row = page.Stepper(title, () => $"Day {a.Day}", d =>
            {
                int min = Math.Min(a.Day, w.FirstDay);
                int day = Mathf.Clamp(a.Day + Math.Sign(d), Math.Max(1, min), w.LastNight);
                if (day == a.Day) return;
                a.Day = day;
                lastDay = day;
                ChangedHere();
            }, () => OpenEditor(a.Id), Tooltip(r, w));
            // Named by id, so the selection stays on this step when the page is rebuilt after a move.
            row.Button.gameObject.name = "Step " + a.Id;
            string color = r.Status switch
            {
                ActionStatus.Short => RedHex,
                ActionStatus.Blocked or ActionStatus.Past or ActionStatus.AlreadyDone => DimHex,
                _ => null,
            };
            string main = color == null ? title : $"<color=#{color}>{title}</color>";
            row.SetLabel($"{main}\n<size=76%><color=#{(r.Status == ActionStatus.Short ? RedHex : DimHex)}>{Detail(r)}</color></size>");
            var le = row.Button.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = 70f;
        }

        /// <summary>"Build House 3 · north-east" / "Upgrade Castle to level 3: Builder's Guild".</summary>
        internal static string Title(ActionResult r)
        {
            var a = r.Action;
            string name = r.Slot?.Label ?? "a building not on this map";
            string verb = a.Level <= 1 ? $"Build {name}" : $"{name} to level {a.Level}";
            var step = r.Slot != null && a.Level >= 1 && a.Level <= r.Slot.Steps.Count ? r.Slot.Steps[a.Level - 1] : null;
            if (step != null && step.IsChoice) verb += ": " + step.Branches[EconSim.BranchIndex(step, a.Choice)].Name;
            return verb;
        }

        private static string Detail(ActionResult r) => r.Status switch
        {
            ActionStatus.AlreadyDone => r.Reason == "Done" ? "done" : r.Reason,
            ActionStatus.Past => $"missed: {r.Reason}",
            ActionStatus.Blocked => $"skipped: {r.Reason}",
            _ => string.Join(" · ", new[]
            {
                r.Status == ActionStatus.Short ? $"{Coin(r.Cost)}, short by {r.Shortfall}" : Coin(r.Cost),
                r.IncomeDelta != 0 ? $"{Signed(r.IncomeDelta)} per dawn" : null,
                Payback(r),
            }.Where(x => x != null)),
        };

        private static string Payback(ActionResult r)
        {
            if (!r.Counts || r.IncomeDelta <= 0 && r.ExtraIncome <= 0 || r.Cost <= 0) return null;
            return r.PaybackDawn is int d ? $"pays for itself by dawn {d}" : $"earns back {r.ExtraIncome} of {r.Cost} by the end";
        }

        private static string Tooltip(ActionResult r, EconWorld w)
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
                    lines.Add($"Day {r.Action.Day} is over and this wasn't done.");
                    break;
                case ActionStatus.AlreadyDone:
                    lines.Add(r.Reason == "Done" ? "Done." : $"{r.Reason}, so this costs nothing.");
                    break;
            }
            if (r.Counts)
            {
                lines.Add($"Costs {r.Cost}; buildings pay {Signed(r.IncomeDelta)} per dawn after it.");
                if (r.IncomeDelta != 0 || r.ExtraIncome != 0)
                    lines.Add(r.PaybackDawn is int d ? $"It has paid for itself by dawn {d}, and brings {r.ExtraIncome} by the last dawn." : $"It brings {r.ExtraIncome} by the last dawn, against its cost of {r.Cost}.");
            }
            if (r.Branch?.Description is { Length: > 0 } desc) lines.Add(desc);
            lines.Add("Left/right: move it a day. Select: change it.");
            return string.Join("\n", lines);
        }

        private static void DayTable(ModPage page, Projection p)
        {
            var w = p.World;
            var rows = new List<string[]>();
            if (w.DawnPending)
            {
                int income = p.Result.PendingDrops + p.Result.PendingBuildings + p.Result.PendingTreasure;
                rows.Add([$"Night {w.FirstDay - 1}", Gold(w.Gold), "-", N(income), N(p.Result.PendingBuildings), Gold(w.Gold + income), ""]);
            }
            foreach (var d in p.Result.Days)
            {
                var c = p.Compare.Days.FirstOrDefault(x => x.Day == d.Day);
                string spent = d.Spent == 0 ? "-" : d.Shortfall > 0 ? $"<color=#{RedHex}>{N(d.Spent)}!</color>" : N(d.Spent);
                string vs = c == null || p.Compare == p.Result ? "" : Signed(d.AtDawn - c.AtDawn);
                rows.Add([$"Day {d.Day}", Gold(d.Start), spent, N(d.Income), N(d.Buildings), Gold(d.AtDawn), vs]);
            }
            string vsHeader = p.Compare == p.Result ? "" : "vs " + Short(p.OtherName, 9);
            AnalyticsGraph.Table(page.Custom("Days table", AnalyticsGraph.TableHeight(rows.Count)),
                ["", "Sunrise", "Spent", "Income", "Buildings", "At dawn", vsHeader], rows, [1.05f, 1f, 0.9f, 0.9f, 1.05f, 1f, 1.1f], rows.Count);
        }

        private static string About(EconWorld w) =>
            "Sunrise: gold when the day starts. Spent: what the plan builds that day (! when there isn't enough). Income: interest at dusk" +
            (w.Interest ? "" : " (with the Interest perk)") + ", the night's enemy gold, and what buildings pay at dawn (Buildings)" +
            (w.TreasureHunter ? " plus Treasure Hunter" : "") + ". At dawn: gold the next day starts with.\n" +
            "Worked out from this map's buildings, costs and your perks as they are now: mines run dry, harbours grow, Builder's Guild upgrades a house each dusk. " +
            "Assumes no building is knocked out after tonight and every coin is picked up before dusk. Not counted: shrines unlocking and Temple prayers.";

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
                    (p.Plan.Actions.Count > 0 ? $"  <color=#{DimHex}>{Signed(vsNothing)} vs doing nothing</color>" : ""),
            };
            if (p.Other != null) lines.Add($"{Signed(r.EndGold - p.Compare.EndGold)} vs {p.Other.Name}");
            lines.Add($"Now {Coin(w.Gold)}" + (w.DawnPending ? $", {Signed(r.PendingDrops + r.PendingBuildings + r.PendingTreasure)} by dawn" : "") +
                $" · buildings pay {r.NextDawnBuildings} at the next dawn, {r.LastDawnBuildings} at the last" +
                (r.LastDawnBuildings != p.Compare.LastDawnBuildings ? $" ({Signed(r.LastDawnBuildings - p.Compare.LastDawnBuildings)})" : ""));
            var shortDay = r.FirstShortDay;
            lines.Add($"Spends {r.TotalSpent}" + (shortDay != null
                ? $" · <color=#{RedHex}>short by {shortDay.Shortfall} on day {shortDay.Day}</color>" + (r.Days.Count(d => d.Shortfall > 0) > 1 ? $" <color=#{RedHex}>and {r.Days.Count(d => d.Shortfall > 0) - 1} more</color>" : "")
                : p.Plan.Actions.Count > 0 ? " · always affordable" : ""));
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
                    p.Compare.Days.Select(d => (float)d.Start).Append(p.Compare.EndGold).ToArray()));
            var shortCols = new HashSet<int>(days.Select((d, i) => (d, i)).Where(t => t.d.Shortfall > 0).Select(t => t.i));
            var actionCols = new HashSet<int>(days.Select((d, i) => (d, i)).Where(t => t.d.Actions > 0).Select(t => t.i));
            float height = Mathf.Clamp(V.AsideHeight(aside) - y - 10f, 240f, 380f);
            V.Gold(V.Holder(aside, "Graph", y, width, height), width, height, labels, series, shortCols, actionCols);
        }

        // ---- Add to plan --------------------------------------------------------------------------------------

        internal void OpenPicker(int day, string replace)
        {
            var s = Snap();
            if (s == null) return;
            pickDay = Mathf.Clamp(day, s.World.FirstDay, Math.Max(s.World.FirstDay, s.World.LastNight));
            replaceId = replace;
            pickSlot = -1;
            pickerFrame.SetTitle(replace == null ? "Add to plan" : "Change building");
            if (pickerFrame.IsOpen) pickerFrame.Refresh();
            else pickerFrame.Open();
        }

        private void BuildPicker(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var w = p.World;
            var others = p.Plan.Actions.Where(a => a.Id != replaceId).ToList();
            var state = EconSim.StateOn(w, others, pickDay);
            var planned = new HashSet<int>(p.Plan.Actions.Select(a => w.Find(a.Slot)?.Index ?? -1));
            if (pickSlot >= 0 && pickSlot < state.Slots.Count)
            {
                BuildChoices(page, p, state, state.Slots[pickSlot], planned);
                return;
            }
            page.Stepper("Day", () => $"Day {pickDay}", d =>
            {
                pickDay = Mathf.Clamp(pickDay + Math.Sign(d), w.FirstDay, w.LastNight);
                pickerFrame.Refresh();
            }, null, "The day to do it. Buildings are listed as they'd be that day, after the plan's earlier steps.");
            page.Stepper("Show", () => Filters[pickFilter], d =>
            {
                pickFilter = (pickFilter + Math.Sign(d) + Filters.Length) % Filters.Length;
                pickerFrame.Refresh();
            }, null, "Buildings you can add: the ones that can be built or upgraded that day. Income buildings: everything that earns gold. Every slot: the whole map.");

            var slots = state.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0 && (e.Active || e.Activator >= 0)).Where(e => pickFilter switch
            {
                0 => e.Active && e.Level < e.MaxLevel && EconSim.CanUpgrade(state, e),
                1 => w.Slots[e.Index].EverPays,
                _ => true,
            }).ToList();
            pickerRows = slots.Count;
            var aside = page.Aside;
            if (slots.Count == 0)
            {
                page.Text(pickFilter == 0 ? "Nothing can be built or upgraded that day. Try another day, or show every slot." : "No buildings.");
                ShowSlot(aside, p, state, null, planned);
                return;
            }
            // Income buildings first, then the rest, each by name.
            foreach (var group in slots.GroupBy(e => e.TypeName).OrderBy(g => w.Slots[g.First().Index].EverPays ? 0 : 1).ThenBy(g => g.Key))
            {
                var header = page.Header(group.Count() > 1 ? $"{group.Key} <size=70%><color=#{DimHex}>×{group.Count()}</color></size>" : group.Key);
                header.fontSize = 32f;
                foreach (var e in group)
                {
                    var slot = e;
                    string name = slot.Label.StartsWith(slot.TypeName) && slot.Label.Length > slot.TypeName.Length
                        ? slot.Label.Substring(slot.TypeName.Length).TrimStart(' ', '·').Trim() : slot.Level == 0 ? "Build" : $"Level {slot.Level}";
                    if (name.Length > 0 && char.IsDigit(name[0])) name = "No. " + name;
                    var row = page.Row(name, NextStep(state, slot), (string)null, () => Pick(p, state, slot));
                    row.Button.gameObject.name = "Slot " + slot.Index;
                    row.Button.onSelectionStateChange.AddListener(() =>
                    {
                        if (IsSelected(row.Button)) ShowSlot(aside, p, state, slot, planned);
                    });
                }
            }
            ShowSlot(aside, p, state, null, planned);
        }

        private static bool IsSelected(TFUITextButton b) =>
            b != null && b.CurrentState is ThronefallUIElement.SelectionState.Selected or ThronefallUIElement.SelectionState.FocussedAndSelected;

        /// <summary>"Lv 1 → 2 · 8 · +2 per dawn", or why it can't go further.</summary>
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
            var step = slot.Steps[slot.Level];
            if (step.IsChoice)
            {
                pickSlot = slot.Index;
                pickerFrame.Refresh();
                return;
            }
            Commit(p, slot, slot.Level + 1, null);
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
                string value = b.Locked ? $"<color=#{DimHex}>locked</color>" : income != 0 ? $"{Signed(income)} per dawn" : "";
                if (b.StartsGuild) value = (value.Length > 0 ? value + " · " : "") + "a free house upgrade each dusk";
                page.Row(b.Name, value, b.Description, () =>
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
            ShowSlot(page.Aside, p, state, slot, planned);
        }

        private void Commit(Projection p, EconSlot slot, int level, string choice)
        {
            var existing = replaceId == null ? null : p.Plan.Actions.FirstOrDefault(a => a.Id == replaceId);
            if (existing != null)
            {
                existing.Slot = slot.Id;
                existing.Level = level;
                existing.Choice = choice;
                existing.Day = pickDay;
                if (editorFrame != null) editId = existing.Id;
            }
            else p.Plan.Actions.Add(new EconAction { Day = pickDay, Slot = slot.Id, Level = level, Choice = choice });
            lastDay = pickDay;
            Save();
            hostDirty = true;
            pickerFrame.Close();
            // Back in the editor (when changing a step's building) or on the tab: both rebuild on activation.
            if (editorFrame.IsOpen) editorFrame.Refresh();
        }

        /// <summary>The map with <paramref name="slot"/> lit, and what it is and does on the picked day.</summary>
        private void ShowSlot(RectTransform aside, Projection p, EconWorld state, EconSlot slot, HashSet<int> planned)
        {
            if (aside == null) return;
            foreach (Transform child in aside.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            float width = V.AsideWidth(aside), y = 0f;
            float mapHeight = Mathf.Min(width * 0.62f, 360f);
            V.Map(V.Holder(aside, "Map", y, width, mapHeight), width, mapHeight, state, slot?.Index ?? -1, planned);
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
            if (slot.Level != now.Level) lines.Add($"Level {slot.Level} by day {state.FirstDay} with the plan's earlier steps.");
            if (slot.Level < slot.Steps.Count && slot.Level < slot.MaxLevel)
            {
                var step = slot.Steps[slot.Level];
                lines.Add($"Next: {NextStep(state, slot)}");
                if (step.IsChoice)
                    lines.Add($"<color=#{DimHex}>Choices: {string.Join(", ", step.Branches.Select(b => b.Name + (b.Locked ? " (locked)" : "")))}</color>");
                else if (!string.IsNullOrEmpty(step.Text)) lines.Add($"<color=#{DimHex}>{step.Text}</color>");
            }
            if (slot.Modifiers.Any(m => m.Kind == EconModifierKind.Mine)) lines.Add($"<color=#{DimHex}>Mines pay less each dawn.</color>");
            if (slot.Modifiers.Any(m => m.Kind == EconModifierKind.Harbour)) lines.Add($"<color=#{DimHex}>Harbours pay more each dawn, up to {slot.Modifiers.First(m => m.Kind == EconModifierKind.Harbour).Max}.</color>");
            V.Text(aside, string.Join("\n", lines), y, width, 23f);
        }

        // ---- Plan step --------------------------------------------------------------------------------------------

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
            var a = p.Plan.Actions.FirstOrDefault(x => x.Id == editId);
            if (a == null) { page.Text("This step isn't in the plan any more."); return; }
            var r = p.Result.Actions.First(x => x.Action == a);
            var slot = r.Slot ?? w.Find(a.Slot);
            page.Header(slot?.Label ?? "A building not on this map");

            page.Stepper("Day", () => $"Day {a.Day}", d =>
            {
                a.Day = Mathf.Clamp(a.Day + Math.Sign(d), Math.Max(1, Math.Min(a.Day, w.FirstDay)), w.LastNight);
                lastDay = a.Day;
                ChangedOnTop(editorFrame);
            }, null, "The day to do it.");
            if (slot != null)
            {
                page.Stepper("Step", () => a.Level <= 1 ? "Build" : $"Upgrade to level {a.Level}", d =>
                {
                    a.Level = Mathf.Clamp(a.Level + Math.Sign(d), 1, Math.Max(1, slot.MaxLevel));
                    var st = slot.Steps[a.Level - 1];
                    a.Choice = st.IsChoice ? st.Branches[EconSim.BranchIndex(st, a.Choice)].Key : null;
                    ChangedOnTop(editorFrame);
                }, null, "Which level this step takes the building to. A step needs the level before it, by then.");
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
            }

            page.Row("Cost", r.Counts ? Coin(r.Cost) : "-", "Gold this step costs on its day (Castle Up and perks included).");
            page.Row("Buildings pay", r.Counts ? $"{Signed(r.IncomeDelta)} per dawn" : "-", "How much more the building pays at each dawn after this step.");
            string payback = Payback(r);
            if (payback != null) page.Row("Payback", Capital(payback), "When the extra income has repaid the cost, counting mines running dry and harbours growing.");
            if (!r.Counts || r.Status == ActionStatus.Short)
                page.Row("Status", r.Status == ActionStatus.Short ? $"<color=#{RedHex}>short by {r.Shortfall}</color>" : $"<color=#{DimHex}>{Detail(r)}</color>", Tooltip(r, w));

            var sameDay = p.Plan.Actions.Count(x => x.Day == a.Day);
            if (sameDay > 1)
            {
                page.Button("Do it earlier that day", () => { if (Reorder(p.Plan, a, -1)) ChangedOnTop(editorFrame); },
                    "Steps on the same day are done in order; it matters when one unlocks another or gold runs short.");
                page.Button("Do it later that day", () => { if (Reorder(p.Plan, a, 1)) ChangedOnTop(editorFrame); });
            }
            page.Button("Change building", () => OpenPicker(a.Day, a.Id), "Pick another building for this step.");
            page.Button("Remove from plan", () =>
            {
                p.Plan.Actions.Remove(a);
                Save();
                hostDirty = true;
                editorFrame.Close();
            });

            var planned = new HashSet<int>(p.Plan.Actions.Select(x => w.Find(x.Slot)?.Index ?? -1));
            var state = EconSim.StateOn(w, p.Plan.Actions.Where(x => x != a).ToList(), Mathf.Max(a.Day, w.FirstDay));
            ShowSlot(page.Aside, p, state, slot == null ? null : state.Slots[slot.Index], planned);
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
            page.Header("Plans for this map");
            foreach (var s in f.Scenarios)
            {
                var scenario = s;
                var res = scenario == p.Plan ? p.Result : EconSim.Run(w, scenario.Actions, paybacks: false);
                string mark = scenario == p.Plan ? $"<color=#{ModStyle.Hex(ModStyle.Accent)}>◆</color> " : "";
                var row = page.Row(mark + scenario.Name, $"{scenario.Actions.Count} step{(scenario.Actions.Count == 1 ? "" : "s")} · ends with {Coin(res.EndGold)}",
                    "Select to show this plan.", () =>
                    {
                        f.Current = scenario.Id;
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
            page.Button("New empty plan", () =>
            {
                var s = new PlannerScenario { Name = f.FreeName("Plan") };
                f.Scenarios.Add(s);
                f.Current = s.Id;
                ChangedOnTop(plansFrame);
            });
            page.Button("Copy this plan", () =>
            {
                var s = p.Plan.CopyAs(f.FreeName(p.Plan.Name + " copy"));
                f.Scenarios.Add(s);
                f.Current = s.Id;
                ChangedOnTop(plansFrame);
            }, "A copy to try a variation on; compare the two on the Economy tab.");
            if (p.Plan.Actions.Count > 0)
            {
                page.Button(confirm == "clear" ? $"Press again to clear \"{p.Plan.Name}\"" : "Clear its steps", () =>
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
                DeletePlan(f, p.Plan);
                ChangedOnTop(plansFrame);
            });
        }

        // ---- Text helpers -----------------------------------------------------------------------------------------

        internal static string Coin(int amount) => $"{(amount < 0 ? "-" : "")}{Mathf.Abs(amount):N0}<sprite name=\"coin\">";
        private static string Gold(int v) => v < 0 ? $"<color=#{RedHex}>{v:N0}</color>" : v.ToString("N0");
        private static string N(int v) => v.ToString("N0");
        private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        private static string Short(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
