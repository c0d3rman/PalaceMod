using System;
using System.IO;
using HarmonyLib;

/*
    "Retry Night" in the game's retry popup (pause > Restart, and Try Again on the defeat screen): back to the moment
    the current night was called, still daytime, with everything bought, upgraded and chosen that day kept and its
    gold spent. Dusk effects (interest, temple blessings, the Builders' Guild...) haven't happened yet at that moment,
    so they apply once when the player calls the night again.

    The state is the savestates feature's automatic dusk state when those are on; otherwise this feature takes its
    own single "night-start" slot at the same moment (SwitchToNight, before the night begins). Only a state taken in
    the current level session counts (or restored by this feature), so New Game, the game's Retry Day or loading
    another state never offers a stale one.

    Like the game's Retry Day it's a retry of the same run: the no-retry bonus is lost (MatchSave.hadRestarts, which
    the game's loader sets in ScoreManager.OnLoad), and the game keeps saving the run afterwards.
*/
namespace PalaceMod
{
    public class RetryNightManager : BaseManager
    {
        public override string FeatureSection => "Retry night";
        public override bool AffectsRanking => true; // off while "Gameplay-changing features" is off
        public override string FeatureDescription =>
            "Adds \"Retry Night\" to the game's retry popup (pause > Restart, and Try Again after a defeat): back to right before the current night started, with that day's purchases and upgrades kept. Counts as a retry, like Retry Day.";

        // The hadRestarts guard must outlive the switch: turning the feature off after a retry mustn't give the bonus back.
        protected override bool UnpatchWhenDisabled => false;

        private const string ImpactReason = "Retry night";

        // The state for the night being fought, and the level session it belongs to (DayNightCycle is per load).
        private static SaveStateInfo nightStart;
        private static DayNightCycle nightStartCycle;
        // The level session a retry was loaded into: its fresh saves keep hadRestarts.
        private static DayNightCycle retriedCycle;
        private static bool loading;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            RetryPopup.AddButton("retrynight", "Retry Night", 10,
                () => IsEnabled && LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.Classic && Available(out _) != null,
                Load, Tooltip);

            SaveStates.Captured += state =>
            {
                if (state.Kind is not (SaveStateKind.AutoDusk or SaveStateKind.NightStart)) return;
                nightStart = state;
                nightStartCycle = DayNightCycle.Instance;
            };
            SaveStates.Restoring += state =>
            {
                if (nightStart != null && state.JsonPath == nightStart.JsonPath) return;
                nightStart = null;
                loading = false;
            };
            SaveStates.Restored += state =>
            {
                if (!loading) return;
                loading = false;
                nightStart = state;
                nightStartCycle = retriedCycle = DayNightCycle.Instance;
                MarkRestarted();
                Log.Info($"Retried night {state.NightNumber}; {StateRestorer.Summary()}");
            };
            RegisterDevCommands();
        }

        protected override void OnFeatureDisabled() => RetryPopup.SyncAll();

        // ---- The state ------------------------------------------------------------------------------------------

        /// <summary>The state to retry the current night from, or null (reason says why).</summary>
        internal static SaveStateInfo Available(out string reason)
        {
            reason = null;
            if (nightStart == null || nightStartCycle == null || nightStartCycle != DayNightCycle.Instance) reason = "No pre-night state this session";
            else if (nightStart.Level != SaveStates.CurrentLevel) reason = "That state is from another level";
            else if (nightStart.NightNumber != RetryEarlierDayManager.CurrentNight()) reason = $"The state is for night {nightStart.NightNumber}";
            else if (!File.Exists(nightStart.JsonPath)) reason = "The state was deleted";
            else if (!SaveStates.CanRestore(nightStart, out reason)) { }
            return reason == null ? nightStart : null;
        }

        private static string Tooltip()
        {
            var state = Available(out string reason);
            return state == null ? reason : $"Back to right before night {state.NightNumber}, with {state.Gold} gold and that day's purchases kept. Counts as a retry.";
        }

        private static void Load()
        {
            var state = Available(out string reason);
            if (state == null)
            {
                Notify.Show($"Can't retry the night: {reason}", NotifyKind.Warning, key: "retrynight");
                return;
            }
            try
            {
                Log.For<RetryNightManager>().Info($"Retrying night {state.NightNumber} from {state.Id}");
                loading = true;
                SaveStates.RestoreAsRetry(state, ImpactReason);
            }
            catch (InvalidOperationException e)
            {
                loading = false;
                Notify.Show($"Can't retry the night: {e.Message}", NotifyKind.Warning, key: "retrynight");
            }
        }

        // Mirrors the game's loader (ScoreManager.OnLoad): a retry in a level with 2+ waves loses the no-retry bonus.
        private static void MarkRestarted()
        {
            var save = MatchSaveLoadHandler.CurrentSave;
            if (save != null && (EnemySpawner.instance == null || EnemySpawner.instance.WaveCount >= 2)) save.hadRestarts = true;
        }

        // ---- Capture --------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(DayNightCycle), nameof(DayNightCycle.SwitchToNight))]
        private class NightStart_Patch
        {
            private static void Prefix(DayNightCycle __instance)
            {
                if (__instance.CurrentTimestate != DayNightCycle.Timestate.Day || !Feature<RetryNightManager>.On || StateRestorer.Busy) return;
                var savestates = Feature<SaveStatesManager>.Instance;
                if (savestates == null || savestates.CapturesDusk) return; // its AutoDusk state is the one (see Captured)
                if (!SaveStates.CanCapture(out string reason))
                {
                    Log.For<RetryNightManager>().Debug($"No night-start state ({reason})");
                    return;
                }
                try { SaveStates.Capture(null, SaveStateKind.NightStart); }
                catch (Exception e) { Log.For<RetryNightManager>().Error("Night-start state failed", e); }
            }
        }

        // A retry on the first day restores a run the game still treats as fresh, so its next dawn starts a new
        // MatchSave; carry hadRestarts into it, as a retry from a later day would have.
        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.InitializeCurrentSave))]
        private class InitializeSave_Patch
        {
            private static void Postfix()
            {
                if (retriedCycle != null && retriedCycle == DayNightCycle.Instance) MarkRestarted();
            }
        }

        // Each popup feature syncs the popup itself, so either one works with the other turned off.
        [HarmonyPatch(typeof(UIFrame), nameof(UIFrame.Activate))]
        private class Activate_Patch
        {
            private static void Prefix(UIFrame __instance)
            {
                if (!Feature<RetryNightManager>.On || !RetryPopup.IsRetryPopup(__instance)) return;
                try { RetryPopup.Sync(__instance); }
                catch (Exception e) { Log.For<RetryNightManager>().Error("Couldn't update the retry popup", e); }
            }
        }

        // ---- Dev commands ---------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("retrynight", "retrynight [status | load]   the state Retry Night would load (and why not); load it", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
                switch (sub)
                {
                    case "status":
                    {
                        var state = Available(out string reason);
                        var save = MatchSaveLoadHandler.CurrentSave;
                        return $"current night {RetryEarlierDayManager.CurrentNight()}; " +
                            (state != null ? $"available: {state} {state.Id}" : $"unavailable: {reason}" + (nightStart != null ? $" (have {nightStart} {nightStart.Id})" : "")) +
                            $"; hadRestarts={save?.hadRestarts.ToString() ?? "no save"} overwrite={MatchSaveLoadHandler.OverwriteCurrentSave}" +
                            $" retried={(retriedCycle != null && retriedCycle == DayNightCycle.Instance)}" +
                            $" noRetryBonus={(ScoreManager.Instance != null ? ScoreManager.Instance.DefeatNoRetryBonus : 0)}" +
                            $" mult(dmg={PlayerUpgradeManager.instance?.PlayerDamageMultiplyer:0.###} regen={PlayerUpgradeManager.instance?.PlayerHealthRegenerationMultiplyer:0.###} speed={PlayerMovement.instance?.speed:0.###})" +
                            $"\n{StateRestorer.Summary()}";
                    }
                    case "load":
                    {
                        var state = Available(out string reason) ?? throw new InvalidOperationException(reason);
                        Load();
                        return $"loading {state}";
                    }
                    default:
                        throw new ArgumentException("unknown subcommand");
                }
            });
        }
    }
}
