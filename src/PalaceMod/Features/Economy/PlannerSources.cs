using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PalaceMod.Economy;
using V = PalaceMod.Economy.PlannerVisuals;
using TMPro;

/*
    Income sources: every kind of income building on the map, what it costs, what it pays by the end and when it pays
    for itself if built on a day (on top of the current plan). Opened from the planner's toolbar.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private ModFrame sourcesFrame;
        private int sourcesDay;

        /// <summary>A whole-number field (the UI kit's: stepping that speeds up, Shift x10, typing).</summary>
        private static ModField Num(ModPage page, string label, Func<int> get, Action<int> set, int min, int max, Func<string> tooltip, Func<int, string> show = null) =>
            page.Number(label, get, set, min, Math.Max(min, max), format: show, tooltip: tooltip);

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
                        Note = (e.PaybackDawn is int d ? $"back by dawn {d}" : "never pays back") + (e.FirstDawn == e.LastDawn ? $" · {e.FirstDawn}/night" : $" · {e.FirstDawn}→{e.LastDawn}/night") + (e.Blocked != null ? " · " + e.Blocked.ToLowerInvariant() : ""),
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
            V.AsideBackdrop(aside);
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
    }
}
