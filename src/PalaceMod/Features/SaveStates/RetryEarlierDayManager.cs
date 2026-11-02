using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    "Earlier Day" in the game's retry popup (pause > Restart, and Try Again on the defeat screen), under its own
    "Retry Day": a list of this run's earlier dawns, newest first, each loaded with SaveStates.Restore. It only reads
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
        public override string FeatureDescription =>
            "Adds \"Earlier Day\" to the game's retry popup (pause > Restart, and Try Again after a defeat): go back to the dawn of any earlier day this run. Uses the savestates' automatic dawn states, so Savestates must be on.";

        private const string ButtonName = "PalaceMod Retry Earlier Day";

        private ModFrame frame;

        // Retry popups we've added the button to (the pause menu and the end screen each have their own).
        private readonly List<PopupHook> hooks = [];

        private sealed class PopupHook
        {
            public UIFrame Frame;
            public TFUITextButton Retry, Fresh, Button;
            public Vector2 RetryPos, FreshPos, BackgroundSize;
            public RectTransform Background;
        }

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            frame = new ModFrame("Retry Earlier Day").SetContent(BuildList);
            RegisterDevCommands();
        }

        protected override void OnFeatureDisabled()
        {
            frame.Close();
            foreach (var hook in hooks) Show(hook, false);
        }

        // ---- Which dawns ------------------------------------------------------------------------------------------

        /// <summary>The night the player is on: the night being fought, or the next one during the day.</summary>
        private static int CurrentNight()
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
                page.Text(Feature<SaveStatesManager>.On
                    ? "No earlier dawns saved for this run yet."
                    : "Turn on Savestates (Settings > Mods) to retry earlier days.");
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

        // The popup's two buttons are found by what they do (EndscreenRetryHelper.RestartLastDay / FreshStart).
        private static TFUITextButton FindButton(UIFrame popup, string method) =>
            popup.GetComponentsInChildren<TFUITextButton>(true).FirstOrDefault(b =>
            {
                var e = b.onApply;
                for (int i = 0; i < e.GetPersistentEventCount(); i++)
                    if (e.GetPersistentMethodName(i) == method) return true;
                return false;
            });

        private PopupHook Hook(UIFrame popup)
        {
            hooks.RemoveAll(h => h.Frame == null);
            var hook = hooks.FirstOrDefault(h => h.Frame == popup);
            if (hook != null) return hook;
            var retry = FindButton(popup, nameof(EndscreenRetryHelper.RestartLastDay));
            var fresh = FindButton(popup, nameof(EndscreenRetryHelper.FreshStart));
            if (retry == null || fresh == null || retry.transform.parent != fresh.transform.parent) return null;
            var parent = retry.transform.parent;
            var button = GameWidgets.Button(fresh, parent, ButtonName, "Earlier Day", () => frame.Open());
            var emblem = ModStyle.AddEmblem(button.transform, 9f);
            var follower = emblem.gameObject.AddComponent<EmblemFollower>();
            follower.Text = button.GetComponent<TMPro.TextMeshProUGUI>();
            follower.Gap = 16f;
            var rt = (RectTransform)button.transform;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 70f);
            hook = new PopupHook
            {
                Frame = popup,
                Retry = retry,
                Fresh = fresh,
                Button = button,
                RetryPos = ((RectTransform)retry.transform).anchoredPosition,
                FreshPos = ((RectTransform)fresh.transform).anchoredPosition,
                Background = parent as RectTransform,
                BackgroundSize = parent is RectTransform bg ? bg.sizeDelta : Vector2.zero,
            };
            hooks.Add(hook);
            return hook;
        }

        // Three rows (New Game, Retry Day, Earlier Day) spaced like the game's two, in a taller panel; or the game's
        // own layout and navigation back exactly.
        private static void Show(PopupHook hook, bool show)
        {
            if (hook.Button == null || hook.Retry == null || hook.Fresh == null) return;
            var retry = (RectTransform)hook.Retry.transform;
            var fresh = (RectTransform)hook.Fresh.transform;
            if (show)
            {
                float step = hook.FreshPos.y - hook.RetryPos.y; // 80 in the game's layout
                fresh.anchoredPosition = new Vector2(hook.FreshPos.x, hook.FreshPos.y + step / 2f);
                retry.anchoredPosition = new Vector2(hook.RetryPos.x, hook.RetryPos.y + step / 2f);
                ((RectTransform)hook.Button.transform).anchoredPosition = new Vector2(hook.RetryPos.x, hook.RetryPos.y - step / 2f);
                if (hook.Background != null) hook.Background.sizeDelta = hook.BackgroundSize + new Vector2(0f, step);
                hook.Retry.botNav = hook.Button;
                hook.Button.topNav = hook.Retry;
                hook.Button.botNav = hook.Fresh;
                hook.Fresh.topNav = hook.Button;
                hook.Button.gameObject.SetActive(true);
            }
            else
            {
                if (hook.Frame != null && hook.Frame.CurrentSelection == hook.Button) hook.Frame.Select(hook.Retry);
                hook.Button.gameObject.SetActive(false);
                fresh.anchoredPosition = hook.FreshPos;
                retry.anchoredPosition = hook.RetryPos;
                if (hook.Background != null) hook.Background.sizeDelta = hook.BackgroundSize;
                hook.Retry.botNav = hook.Fresh;
                hook.Fresh.topNav = hook.Retry;
            }
        }

        private void Sync(UIFrame popup)
        {
            var hook = Hook(popup);
            if (hook == null) return;
            bool show = IsEnabled && LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.Classic && EarlierDawns().Count > 0;
            Show(hook, show);
        }

        // Runs before the frame collects its elements, so the button is navigable at once.
        [HarmonyPatch(typeof(UIFrame), nameof(UIFrame.Activate))]
        private class Activate_Patch
        {
            private static void Prefix(UIFrame __instance)
            {
                if (__instance == null || __instance.GetComponent<EndscreenRetryHelper>() == null) return;
                var manager = Feature<RetryEarlierDayManager>.Instance;
                if (manager == null) return;
                try { manager.Sync(__instance); }
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
