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
            DevCommands.Register("analytics", "analytics [open|kill|build <n>|reload [disk|fresh]|dump|scenario <income> <fromNight> <faster> <spend%|avg>]   run analytics: summary, open the frame, end the night, scenario", Command);
            // The match save only holds the history as of the last dawn; a savestate keeps it as of the moment.
            SaveStates.RegisterState("analytics",
                () => IsEnabled && history != null && trackedScore != null && history.level == trackedScore.gameObject.scene.name ? new SavedHistory { json = history.ToJson() } : null,
                RestoreState);
        }

        internal sealed class SavedHistory { public string json; }

        private void RestoreState(SavedHistory saved)
        {
            if (!IsEnabled || ScoreManager.Instance == null) return;
            var restored = RunHistory.FromJson(saved.json);
            if (restored == null || restored.level != ScoreManager.Instance.gameObject.scene.name) return;
            if (ScoreManager.Instance != trackedScore) Attach();
            history = restored;
            // A state taken at night already has that night's record; don't start another one.
            wasNight = RunProjection.IsNight;
            Log.Debug($"Restored {history.nights.Count} recorded nights from a savestate");
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
            if (DayNightCycle.Instance.AfterSunrise) history.PendingDawnIncome = 0;
        }

        // ---- Recording --------------------------------------------------------------------------------------------

        private void Attach()
        {
            Detach();
            var sm = ScoreManager.Instance;
            string scene = sm.gameObject.scene.name;
            if (history != null && historyOwner == sm)
            {
                // Re-enabled in the same level: nothing was recorded while off, so drop the unfinished night and start
                // the day's tallies from now (a night in progress gets a fresh record at the next Update).
                if (history.Current != null) history.nights.RemoveAt(history.nights.Count - 1);
                history.dawnBank = PlayerInteraction.instance != null ? PlayerInteraction.instance.TrueBalance : 0;
                history.spentSinceDawn = 0;
                history.daySeconds = 0f;
            }
            else if (loaded != null && loadedScene == scene)
            {
                history = loaded;
                if (history.Current != null) history.nights.RemoveAt(history.nights.Count - 1);
                // The save may be newer than the history in it (nights played with the feature off): keep what
                // matches the loaded world and start the day's tallies from now.
                int played = EnemySpawner.instance.Wavenumber + 1;
                if (history.nights.RemoveAll(r => r.night > played) > 0 || history.nights.Count == 0 || history.nights[history.nights.Count - 1].night != played)
                {
                    history.dawnBank = PlayerInteraction.instance != null ? PlayerInteraction.instance.TrueBalance : 0;
                    history.spentSinceDawn = 0;
                    history.daySeconds = 0f;
                }
                Log.Info($"Resumed {history.nights.Count} recorded nights from the save");
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
            wasNight = false;
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
            Log.Debug($"Night {record.night} began; bank {bank}, spent {record.daySpent}, earned {record.dayEarned} during the day");
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
                history.PendingDawnIncome = income;
                Log.Info($"Night {record.night} scored {record.ScoreTotal} ({baseScore}+{timeBonus}+{protectionBonus}), " +
                    $"{record.afterSpawning:0}s after spawning, earned {record.nightEarned}, lost {record.unitsLost} units, {down}/{buildings} buildings down, dawn income {income}");
            }
            catch (Exception e)
            {
                Log.Error($"Recording the night failed", e);
            }
        }

        // ---- Persistence (inside the game's match save) -----------------------------------------------------------

        // Only the dawn checkpoint (LocalMatchSaveLoad.Save) writes the history: other flushes of the save file, like
        // the one after a defeat, keep the world from the last dawn, so the history must stay at that dawn too.
        private static bool checkpointing;

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "Save")]
        private static class CheckpointPatch
        {
            private static void Prefix() => checkpointing = true;
            private static void Finalizer() => checkpointing = false;
        }

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private static class SaveRunPatch
        {
            private static void Prefix()
            {
                if (!checkpointing) return;
                var self = Feature<AnalyticsManager>.Instance;
                var save = MatchSaveLoadHandler.CurrentSave;
                if (self?.history == null || save == null || self.trackedScore == null) return;
                if (self.history.level != self.trackedScore.gameObject.scene.name) return;
                save.AddValue(RunHistory.SaveKey, self.history.ToJson());
            }
        }

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "TryLoad")]
        private static class TryLoadPatch
        {
            private static void Prefix(out bool __state) => __state = MatchSaveLoadHandler.IsLoadingPermitted;

            private static void Postfix(LocalMatchSaveLoad __instance, bool __state)
            {
                loaded = null;
                loadedScene = null;
                if (!__state || MatchSaveLoadHandler.CurrentSave == null) return;
                string json = null;
                if (!MatchSaveLoadHandler.CurrentSave.TryLoadValue(RunHistory.SaveKey, ref json)) return;
                loaded = RunHistory.FromJson(json);
                loadedScene = __instance.gameObject.scene.name;
            }
        }

        // ---- Frame ------------------------------------------------------------------------------------------------

        private void Open()
        {
            if (!Rules.Available || history == null) return;
            scenario.ExtraFromNight = Mathf.Clamp(scenario.ExtraFromNight, RunProjection.NextNight, Mathf.Max(RunProjection.NextNight, RunProjection.NightCount));
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
                case "build":
                {
                    // Buy the cheapest unbuilt slots you can afford, paying like the player does (to exercise spending).
                    int want = args.Length > 1 ? int.Parse(args[1]) : 1, built = 0;
                    var player = PlayerInteraction.instance;
                    var slots = UnityEngine.Object.FindObjectsOfType<BuildSlot>()
                        .Where(s => s.Level == 0 && !s.ActivatorUpgradesThis && s.CanBeUpgraded && s.gameObject.activeInHierarchy)
                        .OrderBy(s => s.NextUpgradeOrBuildCost).ToList();
                    foreach (var slot in slots)
                    {
                        if (built >= want || player.Balance < slot.NextUpgradeOrBuildCost) break;
                        player.SpendCoins(slot.NextUpgradeOrBuildCost);
                        slot.TryToBuildOrUpgradeAndPay(player, false);
                        built++;
                        GameplayImpact.Mark("Dev: remote build");
                    }
                    return $"built {built}, balance {player.Balance}";
                }
                case "reload":
                {
                    // reload: like "retry day" (the in-memory match save). reload disk: like quitting and continuing
                    // from level select (re-read the save file). reload fresh: a fresh start.
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (mode == "disk") MatchSaveLoadHandler.TryLoadRun(ScoreManager.Instance.gameObject.scene.name);
                    MatchSaveLoadHandler.OverwriteCurrentSave = mode == "fresh";
                    SceneTransitionManager.instance.RestartCurrentLevel();
                    return $"reloading ({(mode == "" ? "retry day" : mode)})";
                }
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
