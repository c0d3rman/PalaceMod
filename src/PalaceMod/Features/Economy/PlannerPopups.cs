using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PalaceMod.Economy;

/*
    The board's popups (ModPopup: small menus over the board, which stays visible and updates as you change things):
      chip     day, how many, level, choice, wisdom gold, gold amount, order within the day, branch, remove
      day      build / lose / gold found on that day (the Add screen), branch from it, clear it
      plan     switch plans, new, copy, rename, clear, delete
      compare  what the dim line compares with
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private string confirm;

        private static string DayWord(EconAction a) => a.IsStep ? "Day" : "Night";

        internal void ChipPopup(PlannerBoard b, PlanGroup g, ModTile near)
        {
            var members = g.Members.ToList();
            confirm = null;
            ModPopup.Open(Title(g.R), page => BuildChip(page, members), near.Rect);
        }

        private void BuildChip(ModPage page, List<EconAction> members)
        {
            var p = Project();
            if (p == null) return;
            members.RemoveAll(a => !p.Actions.Contains(a));
            if (members.Count == 0)
            {
                page.Text("This isn't in the plan any more.");
                return;
            }
            var w = p.World;
            var a = members[0];
            var r = p.Result.Actions.First(x => x.Action == a);
            var slot = r.Slot ?? w.Find(a.Slot);
            var holder = Holder(p, a);
            ModPopup.Current?.SetTitle(members.Count > 1 ? $"{Title(r)} ×{members.Count}" : Title(r));
            page.Text($"<color=#{(r.Status == ActionStatus.Short ? RedHex : DimHex)}>{Detail(r)}</color>");
            if (holder != p.Plan) page.Text($"<color=#{DimHex}>Part of {p.File.FullName(holder)}, which this branches from: changes here change it too.</color>");

            page.Stepper(DayWord(a), () => $"{DayWord(a)} {a.Day}", d => MoveGroup(Project(), members, null, Math.Sign(d)),
                tooltip: a.IsStep ? "The day to do it. Left/right moves it." : "The night it happens. Left/right moves it.");

            // How many: more of the same kind on that day, nearest the castle first.
            if (slot != null && (a.IsStep && a.Level == 1 || a.Kind == EconActionKind.KnockOut))
            {
                int free = Candidates(p, a, members).Count;
                if (free > 0 || members.Count > 1)
                    page.Number("How many", () => members.Count, v => SetCount(members, v), 1, members.Count + free,
                        tooltip: () => a.IsStep ? $"How many {slot.TypeName} to build that day ({free} more can be)." : $"How many {slot.TypeName} are lost that night.");
            }
            if (a.IsStep && slot != null && members.Count == 1)
            {
                page.Number("Level", () => a.Level, v => Edit(() =>
                {
                    a.Level = v;
                    var st = slot.Steps[Mathf.Clamp(v, 1, slot.Steps.Count) - 1];
                    a.Choice = st.IsChoice ? st.Branches[EconSim.BranchIndex(st, a.Choice)].Key : null;
                }, a.Id), 1, Math.Max(1, slot.MaxLevel), format: v => v <= 1 ? "Build" : $"Level {v}",
                    tooltip: () => "The level this step takes the building to (1 builds it). It needs the level before it by then.");
            }
            var step = a.IsStep && slot != null && a.Level >= 1 && a.Level <= slot.Steps.Count ? slot.Steps[a.Level - 1] : null;
            if (step != null && step.IsChoice)
            {
                page.Stepper("Choice", () =>
                {
                    var br = step.Branches[EconSim.BranchIndex(step, a.Choice)];
                    return br.Locked ? $"{br.Name} (locked)" : br.Name;
                }, d => Edit(() =>
                {
                    int i = (EconSim.BranchIndex(step, a.Choice) + Math.Sign(d) + step.Branches.Count) % step.Branches.Count;
                    foreach (var x in members) x.Choice = step.Branches[i].Key;
                }, a.Id), tooltip: step.Branches[EconSim.BranchIndex(step, a.Choice)].Description);
            }
            var branch = step != null && step.Branches.Count > 0 ? step.Branches[EconSim.BranchIndex(step, a.Choice)] : null;
            if (branch != null && branch.IsWisdom)
            {
                page.Number("Wisdom gold", () => a.Amount ?? r.Wisdom, v => Edit(() => a.Amount = v, a.Id), 0, 99999,
                    format: v => a.Amount == null ? $"{v} (from {(branch.WisdomGold > 0 ? v / branch.WisdomGold : 0)} lost)" : $"{v}",
                    tooltip: () => $"Gold from buildings destroyed that night: {branch.WisdomGold} each. Worked out from the buildings the plan marks lost that night; type your own estimate instead (empty: back to working it out).",
                    onClear: () => Edit(() => a.Amount = null, a.Id));
            }
            if (a.Kind == EconActionKind.Gold)
                page.Number("Gold", () => a.Amount ?? 0, v => Edit(() => a.Amount = v, a.Id), 0, 99999, tooltip: () => "How much gold you expect that night beyond the enemies' drops.");

            if (a.IsStep && holder.Actions.Count(x => x.Day == a.Day && x.IsStep && !members.Contains(x)) > 0)
            {
                page.Button("Earlier that day", () => Edit(() => Reorder(holder, a, -1), a.Id), "Steps on a day are done in order: it matters when one unlocks another or gold runs short.");
                page.Button("Later that day", () => Edit(() => Reorder(holder, members[members.Count - 1], 1), a.Id));
            }
            if (w.FirstDay <= w.LastNight)
            {
                int day = Mathf.Clamp(a.Day, w.FirstDay, w.LastNight);
                page.Button($"Branch from day {day}", () =>
                {
                    ModPopup.Current?.Close();
                    Edit(() => Branch(p.File, p.Plan, day));
                    Notify.Show($"Branched: from day {day} this plan goes its own way, compared with the one before.", NotifyKind.Info, 3f, key: "econ-branch");
                }, "A branch shares this plan's items before that day and has its own after; the graph compares the two.");
            }
            page.Button(members.Count > 1 ? $"Remove all {members.Count}" : "Remove", () =>
            {
                ModPopup.Current?.Close();
                RemoveGroup(Project(), members);
            });
        }

        /// <summary>More slots of the chip's kind that could join it that day: unbuilt and buildable (builds), or
        /// standing and not lost yet (losses); nearest the castle first.</summary>
        private List<EconSlot> Candidates(Projection p, EconAction a, List<EconAction> members)
        {
            var w = p.World;
            var type = w.Find(a.Slot)?.TypeName;
            if (type == null) return [];
            var taken = new HashSet<string>(members.Select(x => x.Slot));
            if (a.IsStep)
            {
                var others = p.Actions.Where(x => !members.Contains(x)).ToList();
                var state = EconSim.StateOn(w, others.Where(x => x.Day < a.Day || x.Day == a.Day && !x.IsStep).ToList(), a.Day);
                var planned = new HashSet<string>(p.Actions.Where(x => x.IsStep).Select(x => x.Slot));
                return state.Slots.Where(e => !e.IsDependent && e.TypeName == type && e.Level == 0 && e.Active && e.Steps.Count > 0
                        && EconSim.CanUpgrade(state, e) && !taken.Contains(e.Id) && !planned.Contains(e.Id))
                    .OrderBy(e => e.MapX * e.MapX + e.MapY * e.MapY).ToList();
            }
            var night = EconSim.StateOn(w, p.Actions.Where(x => x.IsStep).ToList(), Math.Max(a.Day, w.FirstDay));
            var lost = new HashSet<string>(p.Actions.Where(x => x.Kind == EconActionKind.KnockOut && x.Day == a.Day).Select(x => x.Slot));
            return night.Slots.Where(e => !e.IsDependent && e.TypeName == type && e.Level >= 1 && !lost.Contains(e.Id) && !taken.Contains(e.Id))
                .OrderBy(e => e.MapX * e.MapX + e.MapY * e.MapY).ToList();
        }

        private void SetCount(List<EconAction> members, int count)
        {
            var p = Project();
            var a = members[0];
            var holder = Holder(p, a);
            if (count < members.Count)
            {
                Edit(() =>
                {
                    while (members.Count > count)
                    {
                        var last = members[members.Count - 1];
                        Holder(p, last).Actions.Remove(last);
                        members.RemoveAt(members.Count - 1);
                    }
                }, a.Id);
                return;
            }
            var more = Candidates(p, a, members).Take(count - members.Count).ToList();
            if (more.Count == 0) return;
            Edit(() =>
            {
                int at = holder.Actions.IndexOf(members[members.Count - 1]) + 1;
                foreach (var e in more)
                {
                    var x = new EconAction { Kind = a.Kind, Day = a.Day, Slot = e.Id, Level = a.Level, Choice = a.Choice };
                    holder.Actions.Insert(Mathf.Clamp(at++, 0, holder.Actions.Count), x);
                    members.Add(x);
                }
            }, a.Id);
        }

        // ---- Day ----------------------------------------------------------------------------------------------------

        internal void DayPopup(PlannerBoard b, int day, bool tonight, ModTile near)
        {
            confirm = null;
            string title = day == int.MinValue ? "Earlier days" : tonight ? $"Tonight (night {day})" : $"Day {day}";
            ModPopup.Open(title, page => BuildDay(page, day, tonight), near.Rect);
        }

        private void BuildDay(ModPage page, int day, bool tonight)
        {
            var p = Project();
            if (p == null) return;
            var w = p.World;
            if (day == int.MinValue)
            {
                var old = p.Plan.Actions.Where(a => a.IsStep ? a.Day < w.FirstDay : a.Day < (w.DawnPending ? w.FirstDay - 1 : w.FirstDay)).ToList();
                page.Text($"<color=#{DimHex}>Steps from days that are over: they're done, or were missed. They don't cost anything any more.</color>");
                if (old.Count > 0)
                    page.Button($"Remove them ({old.Count})", () =>
                    {
                        ModPopup.Current?.Close();
                        Edit(() => p.Plan.Actions.RemoveAll(old.Contains));
                    });
                return;
            }
            void Open(PlannerAddMode mode)
            {
                ModPopup.Current?.Close();
                OpenAdd(day, mode);
            }
            if (!tonight) page.Button($"Build or upgrade on day {day}…", () => Open(PlannerAddMode.Build), "Pick buildings on the map.");
            page.Button($"Buildings lost on night {day}…", () => Open(PlannerAddMode.Lose), "Mark buildings you expect to lose that night: they pay nothing at the next dawn.");
            page.Button($"Gold found on night {day}…", () => Open(PlannerAddMode.Gold), "Gold you expect beyond the enemies' drops (Temple wisdom, a chest...).");
            if (!tonight && day > w.FirstDay)
                page.Button($"Branch from day {day}", () =>
                {
                    ModPopup.Current?.Close();
                    Edit(() => Branch(p.File, p.Plan, day));
                    Notify.Show($"Branched: from day {day} this plan goes its own way, compared with the one before.", NotifyKind.Info, 3f, key: "econ-branch");
                }, "A branch shares this plan's items before that day and has its own after; the graph compares the two.");
            var own = p.Plan.Actions.Where(a => a.Day == day && (!tonight || !a.IsStep)).ToList();
            if (own.Count > 0)
                page.Button(confirm == "clear-day" ? $"Press again to clear day {day}" : $"Clear day {day} ({own.Count})", () =>
                {
                    if (confirm != "clear-day") { confirm = "clear-day"; ModPopup.Current?.Refresh(); return; }
                    confirm = null;
                    ModPopup.Current?.Close();
                    Edit(() => p.Plan.Actions.RemoveAll(own.Contains));
                });
        }

        // ---- Plans --------------------------------------------------------------------------------------------------

        internal void PlanPopup(PlannerBoard b, ModTile near)
        {
            confirm = null;
            ModPopup.Open("Plans", BuildPlans, near.Rect, width: 640f);
        }

        /// <summary>Plans in tree order (each root, then its branches), with what each ends with.</summary>
        private List<(PlannerScenario Plan, EconResult Result)> Tree(Projection p)
        {
            var f = p.File;
            var order = new List<PlannerScenario>();
            void Add(PlannerScenario s)
            {
                if (order.Contains(s)) return;
                order.Add(s);
                foreach (var c in f.Scenarios.Where(c => c.Parent == s.Id)) Add(c);
            }
            foreach (var s in f.Scenarios.Where(s => s.Parent == null)) Add(s);
            foreach (var s in f.Scenarios) Add(s);
            return order.Select(s => (s, s == p.Plan ? p.Result : EconSim.Run(EconPerks.Apply(p.Snap.World, f.EffectivePerks(s)), f.Effective(s), paybacks: false))).ToList();
        }

        private void BuildPlans(ModPage page)
        {
            var p = Project();
            if (p == null) return;
            var f = p.File;
            foreach (var (s, r) in Tree(p))
            {
                var plan = s;
                int depth = f.Ancestors(s).Count;
                string name = new string(' ', depth * 3) + (depth > 0 ? "› " : "") + s.Name;
                if (s == p.Plan) name = $"<color=#{ModStyle.Hex(ModStyle.Accent)}>{name}</color>";
                string fork = s.Parent == null ? "" : s.ForkDay == int.MaxValue ? " · same steps" : $" · from day {s.ForkDay}";
                var row = page.Row(name, $"{Coin(r.EndGold)}{fork}", s == p.Plan ? "The plan shown." : "Show this plan.", () =>
                {
                    ModPopup.Current?.Close();
                    Edit(() =>
                    {
                        f.Current = plan.Id;
                        if (plan.Parent != null && f.Compare == null) f.Compare = plan.Parent;
                    });
                });
                row.Button.gameObject.name = "Plan " + s.Id;
            }
            page.TextInput("Name", () => p.Plan.Name, text =>
            {
                text = text?.Trim();
                if (string.IsNullOrEmpty(text)) return;
                Edit(() => p.Plan.Name = text);
            }, () => "Select and type to rename the plan shown.", maxLength: 40);
            page.Button("New plan", () =>
            {
                ModPopup.Current?.Close();
                Edit(() =>
                {
                    var s = NewPlan(f, p.Snap.World, "Plan");
                    f.Scenarios.Add(s);
                    f.Current = s.Id;
                });
            }, "A plan of its own, starting with the castle's build.");
            page.Button("Copy this plan", () =>
            {
                ModPopup.Current?.Close();
                Edit(() =>
                {
                    var s = p.Plan.CopyAs(f.FreeName(p.Plan.Name + " copy"));
                    f.Scenarios.Add(s);
                    f.Current = s.Id;
                });
            }, "A copy that goes its own way. To share the early days, branch from a day instead (a day's header).");
            if (p.Plan.Actions.Count > 0)
                page.Button(confirm == "clear" ? $"Press again to clear \"{p.Plan.Name}\"" : "Clear its items", () =>
                {
                    if (confirm != "clear") { confirm = "clear"; ModPopup.Current?.Refresh(); return; }
                    confirm = null;
                    Edit(() => p.Plan.Actions.Clear());
                });
            if (f.Scenarios.Count > 1)
                page.Button(confirm == "delete" ? $"Press again to delete \"{p.Plan.Name}\"" : "Delete this plan", () =>
                {
                    if (confirm != "delete") { confirm = "delete"; ModPopup.Current?.Refresh(); return; }
                    confirm = null;
                    ModPopup.Current?.Close();
                    Edit(() => f.Delete(p.Plan));
                }, "Its branches keep the items they had from it.");
        }

        internal void ComparePopup(PlannerBoard b, ModTile near)
        {
            ModPopup.Open("Compare with", page =>
            {
                var p = Project();
                if (p == null) return;
                var f = p.File;
                page.Row(p.Other == null ? $"<color=#{ModStyle.Hex(ModStyle.Accent)}>Doing nothing</color>" : "Doing nothing", Coin(p.Baseline.EndGold),
                    "No more builds: what you'd end with if you stopped now.", () => { ModPopup.Current?.Close(); Edit(() => f.Compare = null); });
                foreach (var (s, r) in Tree(p))
                {
                    if (s == p.Plan) continue;
                    var plan = s;
                    string name = f.FullName(s);
                    page.Row(s == p.Other ? $"<color=#{ModStyle.Hex(ModStyle.Accent)}>{name}</color>" : name, Coin(r.EndGold), (string)null,
                        () => { ModPopup.Current?.Close(); Edit(() => f.Compare = plan.Id); });
                }
            }, near.Rect, width: 620f);
        }
    }
}
