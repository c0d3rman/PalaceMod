using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Run analytics: records every night of a run (score parts, gold earned and spent, buildings, units, losses, time)
    and shows it from the pause menu ("Run Analytics") as graphs and tables, with a projection of the remaining
    nights and a few what-if knobs.

    Recording
      - dusk: polled in Update (day -> night) to start the night's record with the day's totals and a snapshot.
      - dawn: ScoreManager.OnNightScoreAdd, raised inside the game's own end-of-night scoring, completes it.
      - spending: PlayerInteraction.onBalanceSpend.
    Persistence: the history rides in the game's match save under RunHistory.SaveKey (written in a SaveRun prefix,
    read back after LocalMatchSaveLoad.TryLoad), so save/quit/resume, "retry day" and fresh starts all match the run.
    The game ignores keys it doesn't know, so the save still loads without the mod.

    The game's end screen already graphs score and net worth per day after a match; this adds the in-run view, the
    per-night breakdown, spending, and projections.
*/
namespace PalaceMod
{
    public class AnalyticsManager : BaseManager
    {
        public override string FeatureSection => "Run analytics";
        public override string FeatureDescription =>
            "Records score and gold for every night of a run. Pause and pick \"Run Analytics\" for graphs, the rest of the run projected, and what-if scenarios.";

        private static readonly IScoreRules Rules = GameScoreRules.Instance;

        private RunHistory history;
        private ScoreManager trackedScore;
        private PlayerInteraction trackedPlayer;
        private bool wasNight;
        private readonly Scenario scenario = new();
        private ModFrame frame;

        // Handed over from the load patch (which runs in the scene's Awake, before Update sees the new level).
        private static RunHistory loaded;
        private static string loadedScene;

        public RunHistory History => history;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            PauseMenu.AddButton("analytics", "Run Analytics", Open, order: 20, visible: () => IsEnabled && Rules.Available);
            DevCommands.Register("analytics", "analytics [open|kill|reload|dump|scenario <income> <fromNight> <faster> <spend%|avg>]   run analytics: summary, open the frame, end the night, scenario", Command);
        }

        protected override void OnFeatureDisabled()
        {
            frame?.Close();
            Detach();
        }

        public override void Update()
        {
            if (!Rules.Available)
            {
                if (trackedScore != null) Detach();
                return;
            }
            if (ScoreManager.Instance != trackedScore) Attach();
            if (trackedPlayer == null && PlayerInteraction.instance != null)
            {
                trackedPlayer = PlayerInteraction.instance;
                trackedPlayer.onBalanceSpend.AddListener(OnSpend);
            }
            if (LocalGamestate.Instance == null || LocalGamestate.Instance.CurrentState != LocalGamestate.State.InMatch) return;

            bool night = RunProjection.IsNight;
            if (night && !wasNight) OnDusk();
            wasNight = night;
            if (!night && UIFrameManager.instance?.ActiveFrame == null) history.daySeconds += Time.deltaTime;
        }

        // ---- Recording --------------------------------------------------------------------------------------------

        private void Attach()
        {
            Detach();
            var sm = ScoreManager.Instance;
            string scene = SceneManager.GetActiveScene().name;
            if (history != null && historyOwner == sm)
            {
                // Re-enabled in the same level: keep going.
            }
            else if (loaded != null && loadedScene == scene)
            {
                history = loaded;
                if (history.Current != null) history.nights.RemoveAt(history.nights.Count - 1);
                Plugin.Logger.LogInfo($"Analytics: resumed {history.nights.Count} recorded nights from the save");
            }
            else
            {
                // A fresh run: before the first night, the starting gold counts as day 1's income.
                bool fresh = EnemySpawner.instance.Wavenumber < 0 && !RunProjection.IsNight;
                history = new RunHistory
                {
                    level = scene,
                    dawnBank = fresh || PlayerInteraction.instance == null ? 0 : PlayerInteraction.instance.TrueBalance,
                };
            }
            loaded = null;
            loadedScene = null;
            historyOwner = sm;
            trackedScore = sm;
            sm.OnNightScoreAdd.AddListener(OnNightScored);
            // Opened mid-night with nothing recorded for it: start its record now.
            wasNight = RunProjection.IsNight && history.Current != null;
        }

        private ScoreManager historyOwner;

        private void Detach()
        {
            if (trackedScore != null) trackedScore.OnNightScoreAdd.RemoveListener(OnNightScored);
            if (trackedPlayer != null) trackedPlayer.onBalanceSpend.RemoveListener(OnSpend);
            trackedScore = null;
            trackedPlayer = null;
        }

        private void OnSpend(int amount)
        {
            if (history == null) return;
            var current = history.Current;
            if (RunProjection.IsNight && current != null) current.nightSpent += amount;
            else history.spentSinceDawn += amount;
        }

        private void OnDusk()
        {
            int bank = PlayerInteraction.instance != null ? PlayerInteraction.instance.TrueBalance : 0;
            RunProjection.CountBuildings(out int buildings, out _);
            var record = new NightRecord
            {
                night = RunProjection.NextNight,
                daySeconds = history.daySeconds,
                daySpent = history.spentSinceDawn,
                bankAtDusk = bank,
                dayEarned = bank - history.dawnBank + history.spentSinceDawn,
                buildings = buildings,
                units = TagManager.instance != null ? TagManager.instance.PlayerUnits.Count : 0,
            };
            history.nights.RemoveAll(r => r.night >= record.night);
            history.nights.Add(record);
            Plugin.Logger.LogInfo($"Analytics: night {record.night} began; bank {bank}, spent {record.daySpent}, earned {record.dayEarned} during the day");
        }

        private void OnNightScored(int baseScore, int timeBonus, float protectedFraction, int protectionBonus)
        {
            try
            {
                var record = history.Current;
                if (record == null)
                {
                    OnDusk(); // never saw dusk (feature switched on mid-night); the day's numbers are approximate
                    record = history.Current;
                }
                var bank = PlayerInteraction.instance != null ? PlayerInteraction.instance.TrueBalance : 0;
                float nightLength = DayNightCycle.Instance.CurrentNightLength;
                record.nightSeconds = nightLength;
                record.afterSpawning = Mathf.Max(0f, nightLength - EnemySpawner.instance.LastSpawnPeriodDuration);
                record.nightEarned = bank - record.bankAtDusk + record.nightSpent;
                RunProjection.CountBuildings(out int buildings, out int down);
                record.buildings = buildings;
                record.buildingsKnockedOut = down;
                int units = 0, knocked = 0;
                if (TagManager.instance != null)
                {
                    foreach (var unit in TagManager.instance.PlayerUnits)
                    {
                        if (unit == null) continue;
                        units++;
                        if (unit.Hp != null && unit.Hp.KnockedOut) knocked++;
                    }
                }
                record.unitsLost = knocked + Mathf.Max(0, record.units - units);
                record.scoreBase = baseScore;
                record.scoreTime = timeBonus;
                record.scoreProtection = protectionBonus;
                record.protectedFraction = protectedFraction;
                record.scoreAfter = Rules.CurrentScore;
                // The game has already switched to day here, so apply tonight's knock-outs ourselves.
                int income = 0;
                foreach (var b in IncomeCalculator.All())
                {
                    var interactor = b.Slot.Interactor != null ? b.Slot.Interactor : b.Slot.buildingInteractor;
                    if (interactor == null || !interactor.KnockedOutTonight) income += b.Income;
                }
                record.buildingIncome = income;
                record.complete = true;

                history.dawnBank = bank;
                history.spentSinceDawn = 0;
                history.daySeconds = 0f;
                Plugin.Logger.LogInfo($"Analytics: night {record.night} scored {record.ScoreTotal} ({baseScore}+{timeBonus}+{protectionBonus}), " +
                    $"{record.afterSpawning:0}s after spawning, earned {record.nightEarned}, lost {record.unitsLost} units, {down}/{buildings} buildings down, dawn income {income}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Analytics: recording the night failed: {e}");
            }
        }

        // ---- Persistence (inside the game's match save) -----------------------------------------------------------

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private static class SaveRunPatch
        {
            private static void Prefix()
            {
                var self = Feature<AnalyticsManager>.Instance;
                var save = MatchSaveLoadHandler.CurrentSave;
                if (self?.history == null || save == null || self.trackedScore == null) return;
                if (self.history.level != SceneManager.GetActiveScene().name) return;
                save.AddValue(RunHistory.SaveKey, self.history.ToJson());
            }
        }

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "TryLoad")]
        private static class TryLoadPatch
        {
            private static void Prefix(out bool __state) => __state = MatchSaveLoadHandler.IsLoadingPermitted;

            private static void Postfix(bool __state)
            {
                loaded = null;
                loadedScene = null;
                if (!__state || MatchSaveLoadHandler.CurrentSave == null) return;
                string json = null;
                if (!MatchSaveLoadHandler.CurrentSave.TryLoadValue(RunHistory.SaveKey, ref json)) return;
                loaded = RunHistory.FromJson(json);
                loadedScene = SceneManager.GetActiveScene().name;
            }
        }

        // ---- Frame ------------------------------------------------------------------------------------------------

        private void Open()
        {
            if (!Rules.Available || history == null) return;
            if (scenario.ExtraFromNight < RunProjection.NextNight) scenario.ExtraFromNight = RunProjection.NextNight;
            frame ??= new ModFrame("Run Analytics")
                .AddTab("Score", page => AnalyticsPages.Score(page, history, Rules, scenario))
                .AddTab("Economy", page => AnalyticsPages.Economy(page, history, Rules, scenario))
                .AddTab("Projection", page => AnalyticsPages.Projection(page, history, Rules, scenario, () => frame.Refresh()));
            frame.Open();
        }

        // ---- Dev command ------------------------------------------------------------------------------------------

        private string Command(string[] args)
        {
            if (!Rules.Available || history == null) throw new InvalidOperationException("not in a level");
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case "open":
                    Open();
                    return frame != null && frame.IsOpen ? "opened" : throw new InvalidOperationException("didn't open; see the log");
                case "kill":
                    GameplayImpact.Mark("Dev: killed all enemies");
                    Hp.KillAllEnemyUnits();
                    return $"enemies left {EnemySpawner.instance.NumberOfEnemiesOnTheMap}, spawning {EnemySpawner.instance.SpawningInProgress}";
                case "reload":
                    // Like quitting and continuing from level select: read the match save from disk and reload the level.
                    MatchSaveLoadHandler.TryLoadRun(SceneManager.GetActiveScene().name);
                    SceneTransitionManager.instance.RestartCurrentLevel();
                    return "reloading from the save";
                case "dump":
                    return "\n" + history.ToJson();
                case "scenario":
                    if (args.Length < 5) throw new ArgumentException("usage: analytics scenario <income> <fromNight> <faster> <spend%|avg>");
                    scenario.ExtraIncome = int.Parse(args[1]);
                    scenario.ExtraFromNight = int.Parse(args[2]);
                    scenario.FasterSeconds = float.Parse(args[3]);
                    scenario.SpendShare = args[4] == "avg" ? null : float.Parse(args[4]) / 100f;
                    frame?.Refresh();
                    return Summary();
                default:
                    return Summary();
            }
        }

        private string Summary()
        {
            var lines = new List<string> { $"level {history.level}, night {RunProjection.NextNight}/{RunProjection.NightCount}, score {Rules.CurrentScore}" };
            foreach (var r in history.nights)
                lines.Add($"  N{r.night}{(r.complete ? "" : "*")}: score +{r.ScoreTotal} ({r.scoreBase}/{r.scoreTime}/{r.scoreProtection}) -> {r.scoreAfter}; " +
                    $"earned {r.Earned} spent {r.Spent} bank@dusk {r.bankAtDusk}; bldg {r.buildings} units {r.units} lost {r.unitsLost}; " +
                    $"day {r.daySeconds:0}s night {r.nightSeconds:0}s (+{r.afterSpawning:0}s); dawn income {r.buildingIncome}");
            var baseline = RunProjection.Run(history, Rules, new Scenario());
            var mine = RunProjection.Run(history, Rules, scenario);
            lines.Add($"  projected final: {baseline.Victory.Total} (score {baseline.FinalScore}, bank {baseline.FinalBank}, gold bonus {baseline.Victory.Gold}, " +
                $"mutators {baseline.Victory.Mutators}, no retry {baseline.Victory.NoRetry}); assumes {baseline.AssumedAfterSpawning:0}s, " +
                $"{baseline.AssumedProtection:P0} protected, spend {baseline.AssumedSpendShare:P0}");
            if (!scenario.IsBaseline) lines.Add($"  scenario: {mine.Victory.Total} ({mine.Victory.Total - baseline.Victory.Total:+#;-#;0})");
            return string.Join("\n", lines);
        }
    }
}
