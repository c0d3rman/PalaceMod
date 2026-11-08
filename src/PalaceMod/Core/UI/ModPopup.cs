using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    A small menu over the current screen, which stays visible underneath (e.g. edit one thing on a board and watch the
    board change). Built from ModPage pieces, so Row, Button, Stepper, Number, Toggle and TextInput all work in it:

        ModPopup.Open("House ×3 · day 4", page =>
        {
            page.Stepper("Day", () => $"Day {day}", d => Move(d));
            page.Number("How many", () => n, SetCount, 1, 9);
            page.Button("Remove", () => { Remove(); ModPopup.Current?.Close(); });
        }, near: chip.Rect);

    It's a UIFrame of its own on the game's UIFrameManager: Escape or back closes it (as does a click outside it), and
    the screen below gets its selection back. Up/down moves through the rows in order. Refresh() rebuilds it in place,
    keeping the selected row.
*/
namespace PalaceMod
{
    public sealed class ModPopup
    {
        /// <summary>The popup on screen, if any.</summary>
        public static ModPopup Current { get; private set; }

        public bool IsOpen => frame != null && UIFrameManager.instance?.ActiveFrame == frame;
        public event Action Closed;

        private readonly string title;
        private readonly Action<ModPage> build;
        private readonly float baseWidth;
        private float width;
        private GameObject root;
        private UIFrame frame;
        private RectTransform panel, content;
        private TextMeshProUGUI titleText;
        private ModPage page;
        private RectTransform near;
        private bool hadNear;
        private Vector2? at, placed;

        private ModPopup(string title, Action<ModPage> build, float width)
        {
            this.title = title;
            this.build = build;
            this.width = baseWidth = width;
        }

        /// <summary>Opens a popup beside <paramref name="near"/> (or at screen point <paramref name="screenPoint"/>, or
        /// centred). Returns null if there's no game UI to put it on.</summary>
        public static ModPopup Open(string title, Action<ModPage> build, RectTransform near = null, Vector2? screenPoint = null, float width = 560f)
        {
            var manager = UIFrameManager.instance;
            if (manager == null || GameWidgets.Canvas == null) return null;
            if (Current != null && Current.IsOpen) Current.Close();
            else Current?.DropIfOrphaned();
            var popup = new ModPopup(title, build, width) { near = near, hadNear = near != null, at = screenPoint };
            try { popup.Build(); }
            catch (Exception e)
            {
                popup.Destroy();
                Log.Core.Caught($"ModPopup \"{title}\": couldn't build it", e);
                return null;
            }
            Current = popup;
            popup.Fill(keepSelection: false);
            popup.frame.firstSelected = popup.page.Elements.FirstOrDefault();
            manager.ChangeActiveFrameKeepOldVisible(popup.frame);
            return popup;
        }

        /// <summary>Renames it while open.</summary>
        public void SetTitle(string text)
        {
            if (titleText != null) titleText.text = text;
        }

        public void Close()
        {
            if (IsOpen) UIFrameManager.instance.CloseActiveFrame();
        }

        /// <summary>Rebuilds the rows, keeping the selection on the same row (by name) if it's still there.</summary>
        public void Refresh()
        {
            if (root == null) return;
            Fill(keepSelection: true);
        }

        private void Build()
        {
            var canvas = GameWidgets.Canvas;
            root = new GameObject($"PalaceMod Popup {title}", typeof(RectTransform));
            root.SetActive(false);
            root.transform.SetParent(canvas, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
            frame = root.AddComponent<UIFrame>();
            frame.freezeTime = true;
            frame.freezePlayer = true;
            frame.storeLastSelectedElementInFrameStack = true;
            frame.onDeactivate.AddListener(OnDeactivate);

            var source = GameWidgets.PauseTooltip?.selectionTitle?.GetComponentInParent<MPUIKIT.MPImage>(true);
            var box = source != null ? GameWidgets.Clone(source, root.transform, "Panel", keepChildren: false) : new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            foreach (var c in box.GetComponents<Component>())
                if (c is LayoutGroup or ContentSizeFitter or LayoutElement) Object.DestroyImmediate(c);
            box.transform.SetParent(root.transform, false);
            if (box.GetComponent<MPUIKIT.MPImage>() is { } image)
            {
                image.raycastTarget = true;
                image.color = new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.97f);
                ModStyle.MarkPanel(image);
            }
            box.SetActive(true);
            panel = (RectTransform)box.transform;
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0f, 1f);
            panel.sizeDelta = new Vector2(width, 100f);
            var layout = box.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(26, 26, 16, 18);
            layout.spacing = 2f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            box.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            titleText = GameWidgets.Text(box.transform, "Title", title, ModStyle.TitleFont, 30f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
            titleText.enableWordWrapping = true;
            titleText.margin = new Vector4(0f, 0f, 0f, 4f);

            var list = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup));
            list.transform.SetParent(box.transform, false);
            content = (RectTransform)list.transform;
            var v = list.GetComponent<VerticalLayoutGroup>();
            v.spacing = 0f;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            root.AddComponent<OutsideClick>().Popup = this;
            root.transform.SetAsLastSibling();
        }

        private void Fill(bool keepSelection)
        {
            var current = frame.CurrentSelection;
            string selectedName = keepSelection && current != null && page != null && page.Elements.Contains(current) ? current.name : null;
            int selectedIndex = page != null && current != null ? page.Elements.IndexOf(current) : -1;
            // Rows never clip: the popup widens to its longest row (up to most of the screen), and a row still too long
            // wraps onto more lines.
            width = baseWidth;
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Transform child in content.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
                panel.sizeDelta = new Vector2(width, panel.sizeDelta.y);
                page = new ModPage(content) { Size = new Vector2(width - 52f, 600f) };
                try { build(page); }
                catch (Exception e)
                {
                    Log.Core.Caught($"ModPopup \"{title}\": building it failed", e);
                    page.Text("This couldn't be shown; the error is in the BepInEx log.");
                }
                float need = page.Elements.OfType<TFUITextButton>().Select(RowWidth).DefaultIfEmpty(0f).Max() + 52f;
                float max = Mathf.Max(baseWidth, ((RectTransform)root.transform).rect.width - 96f);
                if (pass > 0 || need <= width + 0.5f) break;
                width = Mathf.Min(need, max);
            }
            panel.sizeDelta = new Vector2(width, panel.sizeDelta.y);
            // Rows are the game's menu entries: a little smaller here.
            foreach (var e in page.Elements.OfType<TFUITextButton>())
            {
                var le = e.GetComponent<LayoutElement>();
                if (le != null) le.preferredHeight = 40f;
                WrapIfLong(e, width - 52f);
            }
            var rows = page.NavigationRows();
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < rows[r].Count; c++)
                {
                    rows[r][c].topNav = r > 0 ? rows[r - 1][Math.Min(c, rows[r - 1].Count - 1)] : null;
                    rows[r][c].botNav = r < rows.Count - 1 ? rows[r + 1][Math.Min(c, rows[r + 1].Count - 1)] : null;
                }
            }
            page.RaiseWired(null, null);
            frame.RefetchManagedElements();
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            Position();
            if (keepSelection && page.Elements.Count > 0 && selectedIndex >= 0)
                frame.Select(page.Elements.FirstOrDefault(e => e.name == selectedName) ?? page.Elements[Math.Min(selectedIndex, page.Elements.Count - 1)]);
        }

        /// <summary>The label's preferred size in the largest of the row's styles: a selected row's text is bigger (and
        /// bold), and it must fit then too.</summary>
        internal static Vector2 LabelWidth(TFUITextButton row, TextMeshProUGUI text, float width)
        {
            var size = text.GetPreferredValues(text.text, width, 0f);
            float keepSize = text.fontSize;
            var keepFont = text.font;
            try
            {
                foreach (var style in new[] { row.selectedStyle, row.focussedStyle, row.focussedAndSelectedStyle })
                {
                    if (style == null || style.fontSize <= 0f) continue;
                    text.fontSize = style.fontSize;
                    if (style.font != null) text.font = style.font;
                    var s = text.GetPreferredValues(text.text, width, 0f);
                    size = Vector2.Max(size, s);
                    text.font = keepFont;
                }
            }
            finally
            {
                text.fontSize = keepSize;
                text.font = keepFont;
            }
            return size;
        }

        /// <summary>The width a row's label (with its icon margin) and value need on one line.</summary>
        internal static float RowWidth(TFUITextButton row)
        {
            var text = row.GetComponent<TextMeshProUGUI>();
            if (text == null || string.IsNullOrEmpty(text.text)) return 0f;
            float w = LabelWidth(row, text, 100000f).x + text.margin.x + Mathf.Max(0f, text.margin.z);
            var value = row.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
            if (value != null && !string.IsNullOrEmpty(value.text)) w += value.GetPreferredValues(value.text, 100000f, 100f).x + 24f;
            return w + 8f;
        }

        /// <summary>A row too long for its room wraps onto more lines and grows to fit them.</summary>
        internal static void WrapIfLong(TFUITextButton row, float room)
        {
            if (RowWidth(row) <= room + 0.5f) return;
            var text = row.GetComponent<TextMeshProUGUI>();
            if (text == null) return;
            // Fields lay out their own value (indented beside the label); leave them be.
            var value = row.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
            if (value != null && value.margin.x > 0f) return;
            // The wrapped label keeps clear of the value on the right.
            if (value != null && !string.IsNullOrEmpty(value.text))
                text.margin = new Vector4(text.margin.x, text.margin.y, Mathf.Max(text.margin.z, value.GetPreferredValues(value.text, 100000f, 100f).x + 16f), text.margin.w);
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            float h = LabelWidth(row, text, room - text.margin.x - Mathf.Max(0f, text.margin.z)).y + 12f;
            var le = row.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = Mathf.Max(le.preferredHeight, h);
        }

        // Beside the anchor (right if there's room, else left), kept on screen.
        private void Position()
        {
            var canvasRect = (RectTransform)root.transform;
            var bounds = canvasRect.rect;
            var size = panel.rect.size;
            Vector2 topLeft;
            // What it was opened beside was rebuilt (the screen below redraws as the popup edits it): stay put.
            if (hadNear && near == null && placed is Vector2 kept) topLeft = kept;
            else if (near != null)
            {
                var corners = new Vector3[4];
                near.GetWorldCorners(corners);
                Vector2 min = canvasRect.InverseTransformPoint(corners[0]), max = canvasRect.InverseTransformPoint(corners[2]);
                topLeft = new Vector2(max.x + 18f, max.y + 10f);
                if (topLeft.x + size.x > bounds.xMax - 16f) topLeft.x = min.x - 18f - size.x;
            }
            else if (at is Vector2 p)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, p, null, out var local);
                topLeft = local;
            }
            else topLeft = new Vector2(-size.x / 2f, size.y / 2f);
            topLeft.x = Mathf.Clamp(topLeft.x, bounds.xMin + 16f, bounds.xMax - 16f - size.x);
            topLeft.y = Mathf.Clamp(topLeft.y, bounds.yMin + 16f + size.y, bounds.yMax - 16f);
            panel.anchoredPosition = topLeft;
            placed = topLeft;
        }

        private bool Stacked()
        {
            try
            {
                foreach (var element in HarmonyLib.Traverse.Create(UIFrameManager.instance).Field("frameStack").GetValue<System.Collections.IEnumerable>())
                    if (HarmonyLib.Traverse.Create(element).Field("frame").GetValue<UIFrame>() == frame) return true;
            }
            catch (Exception e) { Log.Core.Warn($"ModPopup: couldn't read the frame stack: {e.Message}"); }
            return false;
        }

        private void OnDeactivate()
        {
            // A menu opened on top keeps it on the frame stack, to come back to.
            if (UIFrameManager.instance != null && Stacked()) return;
            // Closed (back, a click outside, or Close): it's never shown again.
            if (Current == this) Current = null;
            try { Closed?.Invoke(); }
            catch (Exception e) { Log.Core.Caught($"ModPopup \"{title}\": Closed handler failed", e); }
            Destroy();
        }

        /// <summary>Destroys it if it's neither on screen nor on the frame stack to come back to (left behind when
        /// the menus under it were closed some other way).</summary>
        private void DropIfOrphaned()
        {
            if (root == null || IsOpen || (UIFrameManager.instance != null && Stacked())) return;
            if (Current == this) Current = null;
            Destroy();
        }

        private void Destroy()
        {
            if (root != null) Object.Destroy(root);
            root = null;
        }

        /// <summary>Selects a row by its label (dev commands).</summary>
        internal bool SelectRow(string label)
        {
            var e = page?.Elements.FirstOrDefault(x => x.name == $"Row {label}" || x.name.EndsWith(label, StringComparison.OrdinalIgnoreCase));
            if (e == null) return false;
            frame.Select(e);
            return true;
        }

        /// <summary>The rows' labels (dev commands).</summary>
        internal IEnumerable<string> RowNames => page?.Elements.Select(e => e.name) ?? [];

        /// <summary>Applies the selected row (dev commands).</summary>
        internal void ApplySelected() => frame.Apply();

        internal UIFrame Frame => frame;

        // Closes on a click outside the panel. It closes when the button comes back up, not when it goes down: closed on
        // the press, the screen below would become the active menu that same frame and take the press as a click of its
        // own, on whatever is under the pointer (another chip, a tab, Back).
        private class OutsideClick : MonoBehaviour
        {
            public ModPopup Popup;
            private bool pressedOutside;

            private void Update()
            {
                if (Popup == null) return;
                if (!Popup.IsOpen)
                {
                    pressedOutside = false;
                    // Visible but neither on top nor waiting on the stack: nothing can close it any more.
                    Popup.DropIfOrphaned();
                    return;
                }
                var mouse = Plugin.Input?.controllers?.Mouse;
                bool down, up;
                try
                {
                    down = mouse != null ? mouse.GetButtonDown(0) || mouse.GetButtonDown(1) : Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
                    up = mouse != null ? mouse.GetButtonUp(0) || mouse.GetButtonUp(1) : Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1);
                }
                catch { return; }
                if (down) pressedOutside = !RectTransformUtility.RectangleContainsScreenPoint(Popup.panel, ModTile.MousePosition, null);
                if (up && pressedOutside)
                {
                    pressedOutside = false;
                    Popup.Close();
                }
            }
        }
    }
}
