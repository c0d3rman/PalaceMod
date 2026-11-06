using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Mod buttons in the in-match pause menu, below the game's "Settings":

        PauseMenu.AddButton("compendium", "Compendium", () => compendiumFrame.Open(), order: 10);

    Calling AddButton again with the same id updates that button; it never makes a second one. Buttons are sorted
    by `order`, then id. `visible` (optional) is checked each time the menu opens. Each button is a clone of the
    game's own Settings entry, with PalaceMod's diamond emblem beside the label.

    The buttons are (re)built and spliced into the menu's navigation every time the pause menu opens, from the
    registry here, so they survive the UI being rebuilt and can't end up duplicated or half-made: a button that
    fails to build is dropped for that session and the menu is left as the game made it. The game's menu column is
    tightened so it still ends above the "Night" row; past MaxInline buttons, the rest move into a "More" frame.

    The level-select map has its own pause bar (Continue / Settings / Main Menu, side by side). A button passed
    onMap: true is shown there too, after Settings, whenever its `visible` says so; the others never are, so a
    button written for levels doesn't turn up on the map.
*/
namespace PalaceMod
{
    public static class PauseMenu
    {
        public sealed class Button
        {
            public string Id { get; internal set; }
            public string Label { get; internal set; }
            public int Order { get; internal set; }
            internal Action OnClick;
            internal Func<bool> Visible;
            internal TFUITextButton Widget;
            internal bool OnMap;
            internal TFUITextButton MapWidget;
        }

        internal static readonly Dictionary<string, Button> Buttons = [];

        /// <summary>Adds (or updates) a pause-menu button. Safe to call from Awake, before the game's UI exists.</summary>
        /// <param name="id">Stable, unique id, e.g. "compendium".</param>
        /// <param name="label">Text shown; keep it to one or two words.</param>
        /// <param name="onClick">Runs when the player picks it (the pause menu stays open underneath).</param>
        /// <param name="order">Lower comes first among mod buttons.</param>
        /// <param name="visible">Checked each time the menu opens; null means always shown. Use it to hide the
        /// button while your feature is disabled.</param>
        /// <param name="onMap">Also offer it in the level-select map's pause bar (subject to <paramref name="visible"/>).</param>
        public static Button AddButton(string id, string label, Action onClick, int order = 0, Func<bool> visible = null, bool onMap = false)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("pause button needs an id");
            if (!Buttons.TryGetValue(id, out var button)) Buttons[id] = button = new Button { Id = id };
            button.Label = label;
            button.OnClick = onClick;
            button.Order = order;
            button.Visible = visible;
            button.OnMap = onMap;
            if (button.Widget != null) button.Widget.SetOriginalString(label);
            if (button.MapWidget != null) button.MapWidget.SetOriginalString(label);
            Feature<PauseMenuManager>.Instance?.SyncIfOpen();
            return button;
        }

        public static void RemoveButton(string id)
        {
            if (!Buttons.TryGetValue(id, out var button)) return;
            Buttons.Remove(id);
            if (button.Widget != null)
            {
                Feature<PauseMenuManager>.Instance?.Hide(button.Widget);
                Object.Destroy(button.Widget.gameObject);
            }
            if (button.MapWidget != null) Object.Destroy(button.MapWidget.gameObject);
            button.Widget = button.MapWidget = null;
            Feature<PauseMenuManager>.Instance?.SyncIfOpen();
        }

        /// <summary>Re-checks visibility and labels now if the pause menu is open (it's also done on every open).</summary>
        public static void Refresh() => Feature<PauseMenuManager>.Instance?.SyncIfOpen();
    }

    [ManagerOrder(-45)]
    public class PauseMenuManager : BaseManager
    {
        internal const int MaxInline = 3;
        private const string MoreId = "\0more";

        private UIFrame hookedFrame;
        private Transform menu;                 // the column holding Continue / Settings / Restart / Back
        private ThronefallUIElement nativeNext; // what came after Settings before we spliced in
        private TFUITextButton moreWidget;
        private ModFrame moreFrame;
        private readonly HashSet<string> failed = [];

        // The column's layout as the game made it, restored when no mod button is showing.
        private bool layoutSaved;
        private Vector2 savedPivot, savedPosition;
        private float savedSpacing, savedHeight;

        private UIFrame hookedMapFrame;

        public override void Update()
        {
            var mapFrame = UIFrameManager.instance == null ? null : Traverse.Create(UIFrameManager.instance).Field("overworldPauseMenuFrame").GetValue<UIFrame>();
            if (mapFrame != hookedMapFrame)
            {
                hookedMapFrame = mapFrame;
                foreach (var b in PauseMenu.Buttons.Values) b.MapWidget = null;
                if (mapFrame != null) mapFrame.onActivate.AddListener(SyncMap);
            }
            var frame = GameWidgets.PauseFrame;
            if (frame == hookedFrame) return;
            hookedFrame = frame;
            menu = null;
            nativeNext = null;
            moreWidget = null;
            layoutSaved = false;
            foreach (var b in PauseMenu.Buttons.Values) b.Widget = null; // they were children of the old frame
            if (frame == null) return;
            // onActivate runs before the frame collects its elements, so buttons made here are navigable at once.
            frame.onActivate.AddListener(Sync);
        }

        internal void SyncIfOpen()
        {
            if (hookedFrame == null) return;
            if (UIFrameManager.instance?.ActiveFrame == hookedFrame)
            {
                Sync();
                hookedFrame.RefetchManagedElements();
            }
            else if (moreFrame != null && moreFrame.IsOpen)
            {
                // The overflow list is built from the registry; rebuild it so removed or hidden buttons go away.
                Sync();
                moreFrame.Refresh();
            }
        }

        // Hides one of our buttons; if it's selected, the selection moves to Settings first so keyboard and
        // controller navigation don't get stuck on an inactive or destroyed element.
        internal void Hide(TFUITextButton widget)
        {
            if (widget == null) return;
            if (hookedFrame != null && hookedFrame.CurrentSelection == widget && GameWidgets.PauseHelper?.settingsElement is { } settings)
            {
                hookedFrame.Select(settings);
            }
            widget.gameObject.SetActive(false);
        }

        private void Sync()
        {
            try
            {
                SyncButtons();
            }
            catch (Exception e)
            {
                Log.For<PauseMenuManager>().Error($"Couldn't update the mod buttons", e);
            }
        }

        private void SyncButtons()
        {
            var helper = GameWidgets.PauseHelper;
            var settings = helper?.settingsElement as TFUITextButton;
            if (settings == null) return;
            if (menu == null)
            {
                menu = settings.transform.parent;
                nativeNext = settings.botNav;
            }

            // Which buttons show, in order.
            var shown = new List<PauseMenu.Button>();
            foreach (var b in PauseMenu.Buttons.Values.OrderBy(b => b.Order).ThenBy(b => b.Id, StringComparer.Ordinal))
            {
                if (failed.Contains(b.Id)) continue;
                bool visible;
                try { visible = b.Visible?.Invoke() ?? true; }
                catch (Exception e)
                {
                    Log.For<PauseMenuManager>().Caught($"Visibility check for \"{b.Id}\" failed", e);
                    visible = false;
                }
                if (visible) shown.Add(b);
                else Hide(b.Widget);
            }
            var inline = shown.Count > MaxInline ? shown.Take(MaxInline - 1).ToList() : shown;
            var overflow = shown.Skip(inline.Count).ToList();
            foreach (var b in overflow) Hide(b.Widget);

            // Build what's missing (each button on its own, so one failure doesn't take the others down).
            var widgets = new List<TFUITextButton>();
            foreach (var b in inline)
            {
                if (b.Widget == null)
                {
                    try { b.Widget = Build(settings, $"PalaceMod Pause {b.Id}", b.Label, () => b.OnClick?.Invoke()); }
                    catch (Exception e)
                    {
                        failed.Add(b.Id);
                        Log.For<PauseMenuManager>().Error($"Couldn't add \"{b.Id}\"", e);
                        continue;
                    }
                }
                widgets.Add(b.Widget);
            }
            if (overflow.Count > 0)
            {
                if (moreWidget == null) moreWidget = Build(settings, "PalaceMod Pause More", "More", OpenMore);
                widgets.Add(moreWidget);
                moreItems = overflow;
            }
            else
            {
                Hide(moreWidget);
                moreItems = [];
            }

            SaveLayout(helper);

            // Order them right after Settings and show them.
            int index = settings.transform.GetSiblingIndex();
            foreach (var w in widgets)
            {
                w.transform.SetSiblingIndex(++index);
                w.gameObject.SetActive(true);
            }

            // Navigation: Settings -> ours... -> whatever followed Settings. Hidden ones are skipped by the game.
            ThronefallUIElement previous = settings;
            foreach (var w in widgets)
            {
                previous.botNav = w;
                w.topNav = previous;
                w.leftNav = settings.leftNav;
                w.rightNav = settings.rightNav;
                previous = w;
            }
            previous.botNav = nativeNext;
            if (nativeNext != null) nativeNext.topNav = previous;

            Layout(helper, widgets.Count);
        }

        private List<PauseMenu.Button> moreItems = [];

        // The map's pause bar: the game's buttons side by side under one layout group; ours go after Settings (the
        // second entry) with left/right navigation threaded through, wrapping like the game's.
        private void SyncMap()
        {
            try
            {
                var frame = hookedMapFrame;
                var native = frame.GetComponentsInChildren<TFUITextButton>(false).Where(b => !b.name.StartsWith("PalaceMod ")).ToList();
                if (native.Count < 2) return;
                var settings = native[1];
                var bar = settings.transform.parent;
                var shown = new List<TFUITextButton>();
                foreach (var b in PauseMenu.Buttons.Values.Where(b => b.OnMap).OrderBy(b => b.Order).ThenBy(b => b.Id, StringComparer.Ordinal))
                {
                    bool visible;
                    try { visible = b.Visible?.Invoke() ?? true; }
                    catch (Exception e)
                    {
                        Log.For<PauseMenuManager>().Caught($"Visibility check for \"{b.Id}\" failed", e);
                        visible = false;
                    }
                    if (!visible)
                    {
                        if (b.MapWidget != null) b.MapWidget.gameObject.SetActive(false);
                        continue;
                    }
                    if (b.MapWidget == null)
                    {
                        var button = b;
                        b.MapWidget = Build(settings, bar, $"PalaceMod Pause {b.Id}", b.Label, () => button.OnClick?.Invoke());
                    }
                    b.MapWidget.gameObject.SetActive(true);
                    shown.Add(b.MapWidget);
                }
                int index = settings.transform.GetSiblingIndex();
                foreach (var w in shown) w.transform.SetSiblingIndex(++index);
                var row = bar.Cast<Transform>().Where(t => t.gameObject.activeSelf).Select(t => t.GetComponent<TFUITextButton>()).Where(b => b != null).ToList();
                for (int i = 0; i < row.Count; i++)
                {
                    row[i].leftNav = row[(i + row.Count - 1) % row.Count];
                    row[i].rightNav = row[(i + 1) % row.Count];
                }
            }
            catch (Exception e)
            {
                Log.For<PauseMenuManager>().Error("Couldn't update the map's pause bar", e);
            }
        }

        private void OpenMore()
        {
            moreFrame ??= new ModFrame("Mods").SetContent(page =>
            {
                foreach (var b in moreItems) page.Button(b.Label, () => b.OnClick?.Invoke());
            });
            moreFrame.Open();
        }

        private TFUITextButton Build(TFUITextButton settings, string name, string label, Action onClick) =>
            Build(settings, menu, name, label, onClick);

        private TFUITextButton Build(TFUITextButton settings, Transform parent, string name, string label, Action onClick)
        {
            var button = GameWidgets.Button(settings, parent, name, label, onClick);
            var text = button.GetComponent<TextMeshProUGUI>();
            var emblem = ModStyle.AddEmblem(button.transform, 9f);
            var follower = emblem.gameObject.AddComponent<EmblemFollower>();
            follower.Text = text;
            follower.Gap = 16f;
            return button;
        }

        // Keeps the column's top where the game puts it and tightens the rows so the column still ends above the
        // Night row. With no mod buttons showing, the game's own layout is restored exactly.
        private void SaveLayout(PauseUILoadoutHelper helper)
        {
            if (layoutSaved || menu is not RectTransform rect || menu.GetComponent<VerticalLayoutGroup>() is not { } group) return;
            // Measured while only the game's own rows are showing.
            foreach (var b in PauseMenu.Buttons.Values) if (b.Widget != null) b.Widget.gameObject.SetActive(false);
            if (moreWidget != null) moreWidget.gameObject.SetActive(false);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            savedPivot = rect.pivot;
            savedPosition = rect.anchoredPosition;
            savedSpacing = group.spacing;
            savedHeight = rect.rect.height;
            layoutSaved = true;
        }

        private void Layout(PauseUILoadoutHelper helper, int modCount)
        {
            var group = menu.GetComponent<VerticalLayoutGroup>();
            var rect = menu as RectTransform;
            if (group == null || rect == null) return;
            if (modCount == 0)
            {
                rect.pivot = savedPivot;
                rect.anchoredPosition = savedPosition;
                group.spacing = savedSpacing;
                return;
            }
            // Where the game's own (centred) column would start with the rows it shows right now; Restart, for
            // example, is hidden before the first night.
            float rowHeight = ((RectTransform)helper.settingsElement.transform).rect.height;
            var children = menu.Cast<Transform>().Where(t => t.gameObject.activeSelf).ToList();
            int rows = children.Count;
            int nativeRows = children.Count(t => !t.name.StartsWith("PalaceMod "));
            float center = savedPosition.y + savedHeight * (0.5f - savedPivot.y);
            float nativeHeight = nativeRows * rowHeight + Mathf.Max(0, nativeRows - 1) * savedSpacing;
            float top = center + nativeHeight / 2f;
            float available = nativeHeight;
            if (helper.nightButton != null && rect.parent is RectTransform parent)
            {
                var corners = new Vector3[4];
                ((RectTransform)helper.nightButton.transform).GetWorldCorners(corners);
                float nightTop = parent.InverseTransformPoint(corners[1]).y;
                // Anchored positions are relative to the anchor; convert the column's top to the same space.
                float anchorY = Mathf.Lerp(parent.rect.yMin, parent.rect.yMax, rect.anchorMin.y);
                available = top + anchorY - nightTop - 10f;
            }
            float pitch = Mathf.Min(rowHeight + savedSpacing, available / rows);
            rect.pivot = new Vector2(savedPivot.x, 1f);
            rect.anchoredPosition = new Vector2(savedPosition.x, top);
            group.spacing = pitch - rowHeight;
        }

        // Coming back from a menu opened on top, the game restores the old selection, which may be one of our buttons
        // that has since been hidden or removed; move it to Settings so the highlight isn't lost.
        [HarmonyPatch(typeof(UIFrame), nameof(UIFrame.Activate))]
        private class UIFrame_Activate_Patch
        {
            private static void Postfix(UIFrame __instance)
            {
                var manager = Feature<PauseMenuManager>.Instance;
                if (manager == null || __instance != manager.hookedFrame) return;
                var selected = __instance.CurrentSelection;
                if (selected != null && selected.gameObject.activeInHierarchy) return;
                if (GameWidgets.PauseHelper?.settingsElement is { } settings && settings.gameObject.activeInHierarchy) __instance.Select(settings);
            }
        }

        // The game points the menu's right arrow at the loadout grid each time it refreshes; ours follow Settings.
        // The game also guards loadoutGrid[0] with playerGrid.Count, so with units but an empty loadout (a dev or
        // Builder level started without a weapon) it throws inside UIFrame.Activate: time is frozen, but the pause frame
        // never becomes active, so Escape can't close it and the game is soft-locked. The prefix runs the same method
        // with the right guard in that one case.
        [HarmonyPatch(typeof(PauseUILoadoutHelper), "AssignNavigationTargets")]
        private class AssignNavigationTargets_Patch
        {
            private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, List<ThronefallUIElement>> LoadoutGrid =
                AccessTools.FieldRefAccess<PauseUILoadoutHelper, List<ThronefallUIElement>>("loadoutGrid");
            private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, List<ThronefallUIElement>> EnemyGrid =
                AccessTools.FieldRefAccess<PauseUILoadoutHelper, List<ThronefallUIElement>>("enemyGrid");
            private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, List<ThronefallUIElement>> PlayerGrid =
                AccessTools.FieldRefAccess<PauseUILoadoutHelper, List<ThronefallUIElement>>("playerGrid");
            private static readonly System.Reflection.MethodInfo CalculateGridNavigation =
                AccessTools.Method(typeof(PauseUILoadoutHelper), "CalculateGridNavigation");

            private static bool Prefix(PauseUILoadoutHelper __instance)
            {
                var loadout = LoadoutGrid(__instance);
                var players = PlayerGrid(__instance);
                if (CalculateGridNavigation == null || loadout == null || players == null || loadout.Count > 0 || players.Count == 0) return true;
                var enemies = EnemyGrid(__instance) ?? [];
                var h = __instance;
                CalculateGridNavigation.Invoke(h, [enemies, players[0], h.botmostButton, h.rightMostElement, true, false, false]);
                var top = enemies.Count > 0 ? enemies[0] : h.botmostButton;
                CalculateGridNavigation.Invoke(h, [players, h.topmostButton, top, h.rightMostElement, false, true, false]);
                return false;
            }

            private static void Postfix(PauseUILoadoutHelper __instance)
            {
                foreach (var b in PauseMenu.Buttons.Values)
                {
                    if (b.Widget == null) continue;
                    b.Widget.rightNav = __instance.settingsElement.rightNav;
                    b.Widget.leftNav = __instance.settingsElement.leftNav;
                }
                var more = Feature<PauseMenuManager>.Instance?.moreWidget;
                if (more != null) more.rightNav = __instance.settingsElement.rightNav;
            }
        }
    }
}
