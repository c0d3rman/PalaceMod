using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The three tabs of the Run Analytics frame. Each page is rebuilt from the recorded history and a fresh projection
    every time it's shown, so it always matches the game.
*/
namespace PalaceMod
{
    internal static class AnalyticsPages
    {
        private const float GraphHeight = 300f;

        // ---- Score ------------------------------------------------------------------------------------------------

        public static void Score(ModPage page, RunHistory history, IScoreRules rules, Scenario scenario)
        {
            var projection = RunProjection.Run(history, rules, scenario);
            var done = history.nights.Where(r => r.complete).ToList();
            int count = Mathf.Max(RunProjection.NightCount, done.Count + projection.Nights.Count);

            page.Row("Score so far", N(rules.CurrentScore), () =>
                $"Banked over {done.Count} night{(done.Count == 1 ? "" : "s")}. Each night pays {rules.BasePerNight} for surviving, " +
                $"up to {rules.TimeBonus(0f)} for finishing within {rules.TimeBonusFullUntil:0}s of the last spawn (nothing after {rules.TimeBonusGoneAfter:0}s), " +
                $"and up to {rules.ProtectionBonus(1f)} for keeping every building standing.");

            var labels = Enumerable.Range(1, count).Select(i => i.ToString()).ToArray();
            float[] Series(Func<NightRecord, int> actual, Func<ProjectedNight, int> projected)
            {
                var values = Enumerable.Repeat(float.NaN, count).ToArray();
                foreach (var r in done) if (r.night >= 1 && r.night <= count) values[r.night - 1] = actual(r);
                foreach (var p in projection.Nights) if (p.Night >= 1 && p.Night <= count) values[p.Night - 1] = projected(p);
                return values;
            }
            AnalyticsGraph.Bars(page.Custom("Score graph", GraphHeight), labels, new[]
            {
                new GraphSeries("Survived", AnalyticsGraph.Bronze, Series(r => r.scoreBase, p => p.ScoreBase)),
                new GraphSeries("Protection", AnalyticsGraph.Gold, Series(r => r.scoreProtection, p => p.ScoreProtection)),
                new GraphSeries("Time bonus", AnalyticsGraph.Bright, Series(r => r.scoreTime, p => p.ScoreTime)),
            }, ProjectedFrom(projection, count));
            page.Space(10f);

            var rows = new List<string[]>();
            foreach (var r in done)
                rows.Add(new[] { $"Night {r.night}", N(r.scoreBase), $"{N(r.scoreProtection)} ({Mathf.RoundToInt(r.protectedFraction * 100f)}%)",
                    $"{N(r.scoreTime)} ({r.afterSpawning:0}s)", N(r.ScoreTotal), N(r.scoreAfter) });
            foreach (var p in projection.Nights)
                rows.Add(new[] { $"Night {p.Night}{(p.InProgress ? " (now)" : "")}", N(p.ScoreBase), N(p.ScoreProtection), N(p.ScoreTime), N(p.ScoreTotal), N(p.ScoreAfter) });
            AnalyticsGraph.Table(page.Custom("Score table", AnalyticsGraph.TableHeight(rows.Count)),
                new[] { "", "Survived", "Protection", "Time bonus", "Night", "Score" }, rows, new[] { 1.3f, 1f, 1.3f, 1.3f, 0.9f, 1f }, done.Count);

            page.Header("If you win");
            VictoryRows(page, projection, rules);
        }

        private static void VictoryRows(ModPage page, Projection p, IScoreRules rules)
        {
            var v = p.Victory;
            page.Row("Projected final score", N(v.Total), () =>
                $"Nights score {N(v.Score)}, then the end screen adds the gold bonus, mutators and the no-retry bonus. " +
                $"Assumes you finish each night {p.AssumedAfterSpawning:0}s after the last spawn, keep {Pct(p.AssumedProtection)} of buildings standing " +
                $"and spend {Pct(p.AssumedSpendShare)} of your income (change these under Projection).");
            page.Row("Gold bonus", "+" + N(v.Gold), () => $"Every gold you hold at victory is worth {(p.FinalBank > 0 ? v.Gold / (float)p.FinalBank : 10f):0.#} points. Projected gold at the end: {N(p.FinalBank)}, including the final dawn's income.");
            if (v.Mutators != 0) page.Row("Mutators", "+" + N(v.Mutators), "Your mutators multiply the score plus gold bonus.");
            page.Row("No-retry bonus", v.NoRetry > 0 ? "+" + N(v.NoRetry) : "lost", v.NoRetry > 0
                ? "Added for a run without restarts or reloads."
                : "This run had a restart or a reload (resuming a save counts), so it won't get the no-retry bonus.");
        }

        // ---- Economy ----------------------------------------------------------------------------------------------

        public static void Economy(ModPage page, RunHistory history, IScoreRules rules, Scenario scenario)
        {
            var projection = RunProjection.Run(history, rules, scenario);
            var done = history.nights.Where(r => r.complete).ToList();
            int count = Mathf.Max(RunProjection.NightCount, done.Count + projection.Nights.Count);
            int now = PlayerInteraction.instance != null ? PlayerInteraction.instance.TrueBalance : 0;

            page.Row("Gold now", N(now), "Gold you hold, counting coins on the ground and still to be collected.");
            page.Row("Buildings pay at the next dawn", N(IncomeCalculator.Total()), "Income from the buildings you have now, minus any knocked out tonight.");

            var labels = Enumerable.Range(1, count).Select(i => i.ToString()).ToArray();
            float[] Series(Func<NightRecord, int> actual, Func<ProjectedNight, int> projected)
            {
                var values = Enumerable.Repeat(float.NaN, count).ToArray();
                foreach (var r in done) if (r.night >= 1 && r.night <= count) values[r.night - 1] = actual(r);
                foreach (var p in projection.Nights) if (p.Night >= 1 && p.Night <= count) values[p.Night - 1] = projected(p);
                return values;
            }
            AnalyticsGraph.Lines(page.Custom("Economy graph", GraphHeight), labels, new[]
            {
                new GraphSeries("Income", AnalyticsGraph.Bright, Series(r => r.Earned, p => p.CycleEarned)),
                new GraphSeries("Spent", AnalyticsGraph.Copper, Series(r => r.Spent, p => p.CycleSpent)),
                new GraphSeries("Gold at dusk", AnalyticsGraph.Gold, Series(r => r.bankAtDusk, p => p.BankAtDusk)),
            }, ProjectedFrom(projection, count));
            page.Space(10f);

            var rows = new List<string[]>();
            foreach (var r in done)
                rows.Add(new[] { $"Night {r.night}", N(r.Earned), N(r.Spent), N(r.bankAtDusk), r.buildings.ToString(), r.units.ToString(),
                    r.unitsLost.ToString(), Clock(r.daySeconds), Clock(r.nightSeconds) });
            AnalyticsGraph.Table(page.Custom("Economy table", AnalyticsGraph.TableHeight(rows.Count)),
                new[] { "", "Income", "Spent", "At dusk", "Bldgs", "Units", "Lost", "Day", "Night" }, rows,
                new[] { 1.25f, 1f, 1f, 1f, 0.8f, 0.8f, 0.7f, 0.9f, 0.9f }, rows.Count);

            int earned = done.Sum(r => r.Earned), spent = done.Sum(r => r.Spent);
            page.Row("Spent of all income", earned > 0 ? $"{Pct((float)spent / earned)}" : "-",
                $"{N(spent)} of {N(earned)} gold earned so far (night 1 includes the starting gold). Income per night counts the dawn income that day, interest and enemy drops.");
            page.Row("Units lost", done.Sum(r => r.unitsLost).ToString(), "Units knocked out or killed by the end of each night.");
        }

        // ---- Projection -------------------------------------------------------------------------------------------

        private static readonly int[] IncomeSteps = { 0, 2, 4, 6, 8, 10, 15, 20, 30 };
        private static readonly float[] FasterSteps = { 0f, 5f, 10f, 15f, 20f, 30f, 45f, 60f };
        private static readonly float?[] SpendSteps = { null, 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };

        public static void Projection(ModPage page, RunHistory history, IScoreRules rules, Scenario scenario, Action refresh)
        {
            var baseline = RunProjection.Run(history, rules, new Scenario());
            var p = RunProjection.Run(history, rules, scenario);
            int first = RunProjection.NextNight, last = RunProjection.NightCount;

            page.Text("Pick a line to change it; the projected score updates below.");
            page.Row("Extra income per dawn", scenario.ExtraIncome == 0 ? "none" : $"+{scenario.ExtraIncome}",
                "Gold per dawn from buildings you plan to add, e.g. a few more houses.", () =>
                {
                    scenario.ExtraIncome = Next(IncomeSteps, scenario.ExtraIncome);
                    refresh();
                });
            page.Row("Starting from night", Mathf.Clamp(scenario.ExtraFromNight, first, last).ToString(),
                "The first night whose dawn pays the extra income.", () =>
                {
                    int n = Mathf.Clamp(scenario.ExtraFromNight, first, last) + 1;
                    scenario.ExtraFromNight = n > last ? first : n;
                    refresh();
                });
            page.Row("Finish nights sooner by", scenario.FasterSeconds == 0f ? "no change" : $"{scenario.FasterSeconds:0}s",
                () => $"You've been finishing {p.AssumedAfterSpawning + scenario.FasterSeconds:0}s after the last spawn on average. The time bonus is full within {rules.TimeBonusFullUntil:0}s and gone after {rules.TimeBonusGoneAfter:0}s.", () =>
                {
                    scenario.FasterSeconds = Next(FasterSteps, scenario.FasterSeconds);
                    refresh();
                });
            float average = baseline.AssumedSpendShare;
            page.Row("Spend per day", scenario.SpendShare is float s ? $"{Pct(s)}" : $"as so far ({Pct(average)})",
                "How much of each day's income you spend. Gold you keep is worth points at the end.", () =>
                {
                    int i = Array.IndexOf(SpendSteps, scenario.SpendShare);
                    scenario.SpendShare = SpendSteps[(i + 1) % SpendSteps.Length];
                    refresh();
                });
            if (!scenario.IsBaseline)
            {
                page.Button("Reset scenario", () =>
                {
                    scenario.ExtraIncome = 0;
                    scenario.FasterSeconds = 0f;
                    scenario.SpendShare = null;
                    refresh();
                });
            }

            int delta = p.Victory.Total - baseline.Victory.Total;
            page.Row("Projected final score", scenario.IsBaseline ? N(p.Victory.Total) : $"{N(p.Victory.Total)}  ({(delta >= 0 ? "+" : "")}{N(delta)})",
                () => $"Nights {N(p.FinalScore)} + gold bonus {N(p.Victory.Gold)} + mutators {N(p.Victory.Mutators)} + no retry {N(p.Victory.NoRetry)}." +
                      (scenario.IsBaseline ? "" : $" Without the scenario: {N(baseline.Victory.Total)}."));
            page.Row("Gold at the end", N(p.FinalBank), $"Gold held at victory, including the final dawn's income. Without the scenario: {N(baseline.FinalBank)}.");

            // Running score: what's banked, then baseline vs scenario, ending in the victory total (with the gold bonus).
            var done = history.nights.Where(r => r.complete).ToList();
            int count = Mathf.Max(last, done.Count + p.Nights.Count);
            var labels = Enumerable.Range(1, count).Select(i => i.ToString()).Append("Win").ToArray();
            var banked = Enumerable.Repeat(float.NaN, count + 1).ToArray();
            var basePath = Enumerable.Repeat(float.NaN, count + 1).ToArray();
            var scenPath = Enumerable.Repeat(float.NaN, count + 1).ToArray();
            foreach (var r in done) if (r.night >= 1 && r.night <= count) banked[r.night - 1] = r.scoreAfter;
            foreach (var n in baseline.Nights) if (n.Night >= 1 && n.Night <= count) basePath[n.Night - 1] = n.ScoreAfter;
            foreach (var n in p.Nights) if (n.Night >= 1 && n.Night <= count) scenPath[n.Night - 1] = n.ScoreAfter;
            basePath[count] = baseline.Victory.Total;
            scenPath[count] = p.Victory.Total;
            if (done.Count > 0 && done[done.Count - 1].night is int lastDone && lastDone >= 1 && lastDone <= count)
                basePath[lastDone - 1] = scenPath[lastDone - 1] = done[done.Count - 1].scoreAfter; // join the projection to the banked line
            var series = new List<GraphSeries> { new("Score", AnalyticsGraph.Gold, banked), new("Projected", AnalyticsGraph.Bronze, basePath) };
            if (!scenario.IsBaseline) series.Add(new GraphSeries("Scenario", AnalyticsGraph.Bright, scenPath));
            AnalyticsGraph.Lines(page.Custom("Projection graph", GraphHeight), labels, series, ProjectedFrom(p, count + 1));
            page.Space(10f);

            page.Header("Nights ahead");
            var rows = p.Nights.Select(n => new[]
            {
                $"Night {n.Night}{(n.InProgress ? " (now)" : "")}", n.Enemies.ToString(), AnalyticsGraph.Short(n.EnemyHp),
                n.Difficulty > 1.001f ? $"+{(n.Difficulty - 1f) * 100f:0}%" : "-", N(n.EnemyGold), N(n.BuildingIncome), N(n.BankAtDawn), N(n.ScoreAfter),
            }).ToList();
            AnalyticsGraph.Table(page.Custom("Waves table", AnalyticsGraph.TableHeight(rows.Count)),
                new[] { "", "Enemies", "Enemy HP", "Harder", "Drops", "Dawn pay", "Gold", "Score" }, rows,
                new[] { 1.35f, 0.9f, 0.9f, 0.8f, 0.8f, 1f, 0.9f, 1f }, 0);
            page.Row("About these numbers", "", "Enemy HP adds up every enemy's health, with night difficulty and elites (not perks or mutators that change enemy health). Drops are the gold the wave's enemies carry. " +
                "Dawn pay is what your current buildings pay (mines run dry, harbours grow) plus the scenario's extra income. Treasure Hunter and Builder's Guild aren't counted.");
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private static int ProjectedFrom(Projection p, int count) => p.Nights.Count > 0 ? p.Nights[0].Night - 1 : count;

        private static int Next(int[] steps, int value)
        {
            int i = Array.IndexOf(steps, value);
            return steps[(i + 1) % steps.Length];
        }

        private static float Next(float[] steps, float value)
        {
            int i = Array.IndexOf(steps, value);
            return steps[(i + 1) % steps.Length];
        }

        private static string N(int v) => v.ToString("N0");

        private static string Pct(float share) => $"{share * 100f:0}%";

        private static string Clock(float seconds)
        {
            int s = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
