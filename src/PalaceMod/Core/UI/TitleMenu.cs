using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Mod buttons in the title screen's menu, just above its last entry (Quit):

        TitleMenu.AddButton("builder", "Builder", () => editor.Open(), visible: () => Feature<BuilderManager>.On);

    Like PauseMenu: calling AddButton again with the same id updates that button, buttons are sorted by order then id,
    and they're rebuilt and spliced into the menu's navigation each time the title menu opens, cloned from the game's
    own entry with PalaceMod's emblem. A button that fails to build is dropped and the menu is left as the game made it.
*/
namespace PalaceMod
{
    public static class TitleMenu
    {
        internal sealed class Entry
        {
            public string Id, Label;
            public int Order;
            public Action OnClick;
            public Func<bool> Visible;
            public TFUITextButton Widget;
        }

        internal static readonly Dictionary<string, Entry> Buttons = [];

        /// <summary>Adds (or updates) a title-menu button. Safe to call from Awake.</summary>
        public static void AddButton(string id, string label, Action onClick, Func<bool> visible = null, int order = 0)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("title button needs an id");
            if (!Buttons.TryGetValue(id, out var entry)) Buttons[id] = entry = new Entry { Id = id };
            entry.Label = label;
            entry.OnClick = onClick;
            entry.Visible = visible;
            entry.Order = order;
            if (entry.Widget != null) entry.Widget.SetOriginalString(label);
        }

        public static void RemoveButton(string id)
        {
            if (!Buttons.TryGetValue(id, out var entry)) return;
            Buttons.Remove(id);
            if (entry.Widget != null) Object.Destroy(entry.Widget.gameObject);
        }
    }

    [ManagerOrder(-45)]
    public class TitleMenuManager : BaseManager
    {
        private UIFrame hooked;
        private TFUITextButton anchor, last;
        private readonly HashSet<string> failed = [];

        private static UIFrame TitleFrame =>
            UIFrameManager.instance == null ? null : Traverse.Create(UIFrameManager.instance).Field("titleFrame").GetValue<UIFrame>();

        public override void Update()
        {
            var frame = TitleFrame;
            if (frame == hooked) return;
            hooked = frame;
            anchor = last = null;
            foreach (var b in TitleMenu.Buttons.Values) b.Widget = null;
            if (frame == null) return;
            frame.onActivate.AddListener(Sync);
            if (UIFrameManager.instance.ActiveFrame == frame)
            {
                Sync();
                frame.RefetchManagedElements();
            }
        }

        private void Sync()
        {
            try { SyncButtons(); }
            catch (Exception e) { Plugin.Logger.LogError($"Title menu: couldn't update the mod buttons: {e}"); }
        }

        private void SyncButtons()
        {
            if (anchor == null && !FindColumn()) return;
            var shown = new List<TFUITextButton>();
            foreach (var b in TitleMenu.Buttons.Values.OrderBy(b => b.Order).ThenBy(b => b.Id, StringComparer.Ordinal))
            {
                bool visible;
                try { visible = !failed.Contains(b.Id) && (b.Visible?.Invoke() ?? true); }
                catch (Exception) { visible = false; }
                if (!visible)
                {
                    if (b.Widget != null) b.Widget.gameObject.SetActive(false);
                    continue;
                }
                if (b.Widget == null)
                {
                    try { b.Widget = Build(b); }
                    catch (Exception e)
                    {
                        failed.Add(b.Id);
                        Plugin.Logger.LogError($"Title menu: couldn't add \"{b.Id}\": {e}");
                        continue;
                    }
                }
                shown.Add(b.Widget);
            }

            // Place them just before the last entry, in order.
            int index = last.transform.GetSiblingIndex();
            foreach (var w in shown)
            {
                w.transform.SetSiblingIndex(index++);
                w.gameObject.SetActive(true);
            }
            last.transform.SetSiblingIndex(index);

            // Navigation: anchor -> ours... -> last.
            ThronefallUIElement previous = anchor;
            foreach (var w in shown)
            {
                previous.botNav = w;
                w.topNav = previous;
                w.leftNav = anchor.leftNav;
                w.rightNav = anchor.rightNav;
                previous = w;
            }
            previous.botNav = last;
            last.topNav = previous;
        }

        // The menu column: the parent holding the most of the frame's text buttons. Our buttons go between its
        // second-to-last and last entries (the last is Quit).
        private bool FindColumn()
        {
            var buttons = hooked.GetComponentsInChildren<TFUITextButton>(true)
                .Where(b => !b.name.StartsWith("PalaceMod ") && b.gameObject.activeSelf).ToList();
            var column = buttons.GroupBy(b => b.transform.parent).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (column == null || column.Count() < 2) return false;
            var ordered = column.OrderBy(b => b.transform.GetSiblingIndex()).ToList();
            last = ordered[ordered.Count - 1];
            anchor = ordered[ordered.Count - 2];
            return true;
        }

        private TFUITextButton Build(TitleMenu.Entry entry)
        {
            var button = GameWidgets.Button(anchor, anchor.transform.parent, $"PalaceMod Title {entry.Id}", entry.Label, () => entry.OnClick?.Invoke());
            var emblem = ModStyle.AddEmblem(button.transform, 9f);
            var follower = emblem.gameObject.AddComponent<EmblemFollower>();
            follower.Text = button.GetComponent<TextMeshProUGUI>();
            follower.Gap = 16f;
            return button;
        }
    }
}
