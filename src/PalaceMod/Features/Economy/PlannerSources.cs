using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PalaceMod.Economy;
using V = PalaceMod.Economy.PlannerVisuals;
using TMPro;
using UnityEngine.UI;

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
            if (sourcesFrame.IsOpen) DrawSources();
            else sourcesFrame.Open();
        }

        private RectTransform sourcesChart;

        private void BuildSources(ModPage page)
        {
            var p = Project();
            if (p == null) { page.Text("The economy planner works during a level."); return; }
            var w = p.World;
            if (w.FirstDay > w.LastNight) { page.Text("No days left to build on."); return; }
            // The day changes the chart in place: rebuilding the page would destroy the row being stepped (and its
            // selection) on every press.
            Num(page, "If built on day", () => sourcesDay, v => { sourcesDay = v; DrawSources(); }, w.FirstDay, w.LastNight, null, v => $"Day {v}");
            page.Text($"<color=#{ModStyle.Hex(ModStyle.Gold)}>earned by the end</color>  <color=#{ModStyle.Hex(AnalyticsGraph.Copper)}>cost</color>  <color=#{DimHex}>net on the right; each line adds a level</color>");
            sourcesChart = page.Custom("Sources chart", 10f);
            DrawSources();
            OtherSources(page.Aside, p);
        }

        private void DrawSources()
        {
            if (sourcesChart == null) return;
            foreach (Transform child in sourcesChart.Cast<Transform>().ToList()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            var p = Project();
            if (p == null) return;
            var sources = Sources(p, sourcesDay);
            var bars = new List<V.SourceBar>();
            foreach (var src in sources)
                foreach (var e in src.Levels)
                    bars.Add(new V.SourceBar
                    {
                        Label = $"{src.Name}{(e.Level > 1 ? $" lv {e.Level}" : "")}{(e.Choice != null ? $": {e.Choice}" : "")}",
                        Cost = e.Cost, Earned = e.Earned,
                        Note = string.Join(" · ", new[]
                        {
                            e.PaybackDawn is int d ? $"back by dawn {d}" : "never pays back",
                            e.FirstDawn == e.LastDawn ? $"{e.FirstDawn}/night" : $"{e.FirstDawn}→{e.LastDawn}/night",
                            e.Blocked?.ToLowerInvariant(),
                            e == src.Levels[0] ? src.Note : null,
                        }.Where(x => !string.IsNullOrEmpty(x))),
                        First = e == src.Levels[0],
                    });
            float height = bars.Count == 0 ? 40f : V.SourceBarsHeight(bars.Count);
            var layout = sourcesChart.GetComponent<LayoutElement>();
            if (layout != null) layout.preferredHeight = height;
            if (bars.Count == 0) V.Text(sourcesChart, $"<color=#{DimHex}>No income building can be built or upgraded that day</color>", 0f, 820f, 22f);
            else V.SourceBars(sourcesChart, 820f, bars);
            if (sourcesChart.parent is RectTransform parent) LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
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
            if (shrines > 0) lines.Add($"Shrines: {shrines}, paying once deaths nearby unlock them (not planned); Temple prayers stop them for a night.");
            var outposts = w.Slots.Where(s => s.OutpostTowers > 0).ToList();
            if (outposts.Count > 0)
            {
                int value = outposts.Sum(o => w.Slots.Where(c => c.Activator == o.Index && c.ObjectName.Contains("Tower") && c.Level == 0 && c.Steps.Count > 0)
                    .OrderByDescending(c => (c.MapX - o.MapX) * (c.MapX - o.MapX) + (c.MapY - o.MapY) * (c.MapY - o.MapY)).Take(o.OutpostTowers).Sum(c => c.Steps[0].Cost));
                lines.Add($"Outposts{(w.Outpost ? "" : " (with the Outpost perk)")}: build {outposts.Sum(o => o.OutpostTowers)} towers for free, worth {value}.");
            }
            if (w.Slots.Any(s => s.Steps.Any(st => st.Branches.Any(b => b.IsWisdom))))
                lines.Add("Temple wisdom: gold for each building destroyed that night (mark them lost on that day's Lost layer).");
            V.Text(aside, string.Join("\n", lines), 0f, width, 23f);
        }
    }
}
