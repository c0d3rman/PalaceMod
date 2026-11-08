using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    Extra buttons in the game's retry popup (pause > Restart, and Try Again on the defeat screen), below its own
    "Retry Day": Retry Night, Earlier Day. Each feature registers its button once; the features' UIFrame.Activate
    prefixes call Sync, which shows the visible ones in a taller panel (spaced like the game's two rows) or puts the
    game's own layout and navigation back when none is visible.
*/
namespace PalaceMod
{
    internal static class RetryPopup
    {
        private sealed class Entry
        {
            public string Id, Label;
            public int Order;
            public Func<bool> Visible;
            public Action Apply;
            public Func<string> Tooltip;
        }

        private sealed class Hook
        {
            public UIFrame Frame;
            public TFUITextButton Retry, Fresh;
            public readonly Dictionary<string, TFUITextButton> Buttons = [];
            public Vector2 RetryPos, FreshPos, BackgroundSize;
            public RectTransform Background;
            public ThronefallUIElement RetryTop, FreshBot;
        }

        private static readonly List<Entry> entries = [];
        // Popups we've added buttons to (the pause menu and the end screen each have their own).
        private static readonly List<Hook> hooks = [];

        /// <summary>Adds a button under "Retry Day" (lower order = higher up). Idempotent per id.</summary>
        public static void AddButton(string id, string label, int order, Func<bool> visible, Action apply, Func<string> tooltip = null)
        {
            entries.RemoveAll(e => e.Id == id);
            entries.Add(new Entry { Id = id, Label = label, Order = order, Visible = visible, Apply = apply, Tooltip = tooltip });
        }

        /// <summary>The popup the player opened it from is a retry popup.</summary>
        public static bool IsRetryPopup(UIFrame frame) => frame != null && frame.GetComponent<EndscreenRetryHelper>() != null;

        /// <summary>Shows the buttons whose Visible says so. Runs before the frame collects its elements.</summary>
        public static void Sync(UIFrame popup)
        {
            var hook = HookFor(popup);
            if (hook != null) Layout(hook);
        }

        /// <summary>Re-lays out every popup we know (a feature was turned off).</summary>
        public static void SyncAll()
        {
            hooks.RemoveAll(h => h.Frame == null);
            foreach (var hook in hooks) Layout(hook);
        }

        // The popup's two buttons are found by what they do (EndscreenRetryHelper.RestartLastDay / FreshStart).
        private static TFUITextButton FindButton(UIFrame popup, string method) =>
            popup.GetComponentsInChildren<TFUITextButton>(true).FirstOrDefault(b =>
            {
                var e = b.onApply;
                for (int i = 0; i < e.GetPersistentEventCount(); i++)
                    if (e.GetPersistentMethodName(i) == method) return true;
                return false;
            });

        private static Hook HookFor(UIFrame popup)
        {
            hooks.RemoveAll(h => h.Frame == null);
            var hook = hooks.FirstOrDefault(h => h.Frame == popup);
            if (hook == null)
            {
                var retry = FindButton(popup, nameof(EndscreenRetryHelper.RestartLastDay));
                var fresh = FindButton(popup, nameof(EndscreenRetryHelper.FreshStart));
                if (retry == null || fresh == null || retry.transform.parent != fresh.transform.parent) return null;
                hook = new Hook
                {
                    Frame = popup,
                    Retry = retry,
                    Fresh = fresh,
                    RetryPos = ((RectTransform)retry.transform).anchoredPosition,
                    FreshPos = ((RectTransform)fresh.transform).anchoredPosition,
                    Background = retry.transform.parent as RectTransform,
                    BackgroundSize = retry.transform.parent is RectTransform bg ? bg.sizeDelta : Vector2.zero,
                    RetryTop = retry.topNav,
                    FreshBot = fresh.botNav,
                };
                hooks.Add(hook);
            }
            foreach (var entry in entries)
            {
                if (hook.Buttons.TryGetValue(entry.Id, out var existing) && existing != null) continue;
                var e = entry;
                var button = GameWidgets.Button(hook.Fresh, hook.Retry.transform.parent, $"PalaceMod Retry {e.Id}", e.Label, () =>
                {
                    try { e.Apply(); }
                    catch (Exception ex) { Log.For<RetryEarlierDayManager>().Caught($"\"{e.Label}\" failed", ex); }
                });
                var emblem = ModStyle.AddEmblem(button.transform, 9f);
                var follower = emblem.gameObject.AddComponent<EmblemFollower>();
                follower.Text = button.GetComponent<TMPro.TextMeshProUGUI>();
                follower.Gap = 16f;
                var rt = (RectTransform)button.transform;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, 70f);
                if (e.Tooltip != null) ModTooltip.Attach(button, e.Label, e.Tooltip);
                button.gameObject.SetActive(false);
                hook.Buttons[e.Id] = button;
            }
            return hook;
        }

        private static bool IsVisible(Entry e)
        {
            try { return e.Visible(); }
            catch (Exception ex)
            {
                Log.For<RetryEarlierDayManager>().Caught($"Checking \"{e.Label}\" failed", ex);
                return false;
            }
        }

        // Rows top to bottom: New Game, Retry Day, then the visible extras in order.
        private static void Layout(Hook hook)
        {
            if (hook.Retry == null || hook.Fresh == null) return;
            var shown = new List<TFUITextButton>();
            foreach (var entry in entries.OrderBy(e => e.Order))
            {
                if (!hook.Buttons.TryGetValue(entry.Id, out var button) || button == null) continue;
                bool visible = IsVisible(entry);
                if (!visible && hook.Frame != null && hook.Frame.CurrentSelection == button) hook.Frame.Select(hook.Retry);
                button.gameObject.SetActive(visible);
                if (visible) shown.Add(button);
            }

            var retry = (RectTransform)hook.Retry.transform;
            var fresh = (RectTransform)hook.Fresh.transform;
            float step = hook.FreshPos.y - hook.RetryPos.y; // 80 in the game's layout (New Game above Retry Day)
            float shift = shown.Count * step / 2f;
            fresh.anchoredPosition = new Vector2(hook.FreshPos.x, hook.FreshPos.y + shift);
            retry.anchoredPosition = new Vector2(hook.RetryPos.x, hook.RetryPos.y + shift);
            if (hook.Background != null) hook.Background.sizeDelta = hook.BackgroundSize + new Vector2(0f, shown.Count * Mathf.Abs(step));

            // Navigation: New Game <-> Retry Day <-> extras, wrapping from the last back to New Game like the game's.
            hook.Retry.topNav = hook.RetryTop;
            hook.Fresh.botNav = hook.FreshBot;
            ThronefallUIElement previous = hook.Retry;
            for (int i = 0; i < shown.Count; i++)
            {
                var button = shown[i];
                ((RectTransform)button.transform).anchoredPosition = new Vector2(hook.RetryPos.x, hook.RetryPos.y + shift - (i + 1) * step);
                previous.botNav = button;
                button.topNav = previous;
                previous = button;
            }
            previous.botNav = hook.Fresh;
            hook.Fresh.topNav = previous;
        }
    }
}
