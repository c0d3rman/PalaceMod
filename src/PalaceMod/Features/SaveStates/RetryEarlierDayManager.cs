using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

/*
    "Earlier Day" in the game's retry popup (pause > Restart, and Try Again on the defeat screen), under its own
    "Retry Day" (and Retry Night; see RetryPopup): a list of this run's earlier dawns, newest first, each loaded with SaveStates.Restore. It only reads
    the automatic dawn states the savestates feature takes; it never captures anything itself.

    Which dawns belong to this run: every run starts with a day-1 dawn state, so the run's states are those taken
    since the newest day-1 dawn. Only days before the current one are offered (the game's Retry Day covers the
    current one), and for each day the newest dawn wins, so after going back and replaying, the list follows the
    replayed timeline rather than the abandoned one.
*/
namespace PalaceMod
{
    public class RetryEarlierDayManager : BaseManager
    {
        public override string FeatureSection => "Retry earlier day";
        public override bool AffectsRanking => true; // off while "Gameplay-changing features" is off
        public override string FeatureDescription =>
            "Adds \"Earlier Day\" to the game's retry popup (pause > Restart, and Try Again after a defeat): go back to the dawn of any earlier day this run. Uses the automatic dawn states (Savestates > \"Automatic states\" must include dawn).";

        private ModFrame frame;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            frame = new ModFrame("Retry Earlier Day").SetContent(BuildList);
            RetryPopup.AddButton("earlierday", "Earlier Day", 20, Visible, () => frame.Open());
            RegisterDevCommands();
        }

        protected override void OnFeatureDisabled()
        {
            frame.Close();
            RetryPopup.SyncAll();
        }

        // ---- Which dawns ------------------------------------------------------------------------------------------

        /// <summary>The night the player is on: the night being fought, or the next one during the day.</summary>
        internal static int CurrentNight()
        {
            var spawner = EnemySpawner.instance;
            var cycle = DayNightCycle.Instance;
            if (spawner == null || cycle == null) return 0;
            return cycle.CurrentTimestate == DayNightCycle.Timestate.Night ? spawner.Wavenumber + 1 : spawner.Wavenumber + 2;
        }

        /// <summary>This run's dawns before the current day, latest day first.</summary>
        internal static List<SaveStateInfo> EarlierDawns()
        {
            if (!Feature<SaveStatesManager>.On || SaveStates.CurrentLevel == null || SaveStates.IsRestoring) return [];
            var dawns = SaveStates.List().Where(s => s.Kind == SaveStateKind.AutoDawn).ToList();
            var runStart = dawns.Where(s => s.NightNumber == 1).Select(s => s.CreatedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            int current = CurrentNight();
            return dawns
                .Where(s => s.CreatedUtc >= runStart && s.NightNumber < current)
                .GroupBy(s => s.NightNumber)
                .Select(g => g.OrderByDescending(s => s.CreatedUtc).First())
                .OrderByDescending(s => s.NightNumber)
                .ToList();
        }

        // ---- The list -------------------------------------------------------------------------------------------

        private void BuildList(ModPage page)
        {
            var dawns = EarlierDawns();
            if (dawns.Count == 0)
            {
                var savestates = Feature<SaveStatesManager>.Instance;
                page.Text(savestates != null && savestates.AutoMode is AutoStateMode.Off or AutoStateMode.Dusk
                    ? "Set Savestates > \"Automatic states\" (Settings > Mods > Practice) to include dawn to retry earlier days."
                    : "No earlier dawns saved for this run yet.");
                return;
            }
            bool defeated = LocalGamestate.Instance != null && LocalGamestate.Instance.CurrentState == LocalGamestate.State.AfterMatchDefeat;
            page.Text(defeated
                ? "Go back to the dawn of an earlier day. Retrying marks this run as modded."
                : "Go back to the dawn of an earlier day. Pause > Savestates keeps the current moment as an undo state. Retrying marks this run as modded.");
            page.Space(10f);
            foreach (var dawn in dawns)
            {
                var state = dawn;
                page.Row($"Day {state.NightNumber}", $"{state.Gold} gold", () => Details(state), () => Load(state));
            }
        }

        private static string Details(SaveStateInfo s) =>
            $"Dawn before night {s.NightNumber} of {s.WaveCount}\n{s.Gold} gold, score {s.Score}\n{s.UnitsAlive} units\nSaved {s.CreatedUtc.ToLocalTime():MMM d, HH:mm}";

        private static void Load(SaveStateInfo state)
        {
            if (!SaveStates.CanRestore(state, out string reason))
            {
                Notify.Show($"Can't retry day {state.NightNumber}: {reason}", NotifyKind.Warning, key: "retryearlierday");
                return;
            }
            try
            {
                Log.For<RetryEarlierDayManager>().Info($"Loading the dawn of day {state.NightNumber} ({state.Id})");
                SaveStates.Restore(state);
            }
            catch (InvalidOperationException e) { Notify.Show($"Can't retry: {e.Message}", NotifyKind.Warning, key: "retryearlierday"); }
        }

        // ---- The retry popup ------------------------------------------------------------------------------------

        private bool Visible() => IsEnabled && LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.Classic && EarlierDawns().Count > 0;

        // Runs before the frame collects its elements, so the button is navigable at once.
        [HarmonyPatch(typeof(UIFrame), nameof(UIFrame.Activate))]
        private class Activate_Patch
        {
            private static void Prefix(UIFrame __instance)
            {
                if (!RetryPopup.IsRetryPopup(__instance)) return;
                try { RetryPopup.Sync(__instance); }
                catch (Exception e) { Log.For<RetryEarlierDayManager>().Error($"Couldn't update the retry popup", e); }
            }
        }

        // ---- Dev commands ---------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("retryday", "retryday [list | open | load <day>]   this run's earlier dawns; open the list; retry from a day's dawn", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
                switch (sub)
                {
                    case "list":
                    {
                        var dawns = EarlierDawns();
                        return $"current night {CurrentNight()}; {dawns.Count} earlier dawn(s)" +
                            string.Concat(dawns.Select(s => $"\n  day {s.NightNumber}  {s.Gold}g  score {s.Score}  {s.Id}"));
                    }
                    case "open":
                        frame.Open();
                        return "";
                    case "load":
                    {
                        int day = int.Parse(args[1]);
                        var state = EarlierDawns().FirstOrDefault(s => s.NightNumber == day) ?? throw new InvalidOperationException($"no earlier dawn for day {day}");
                        Load(state);
                        return $"loading {state}";
                    }
                    default:
                        throw new ArgumentException("unknown subcommand");
                }
            });
        }
    }
}
