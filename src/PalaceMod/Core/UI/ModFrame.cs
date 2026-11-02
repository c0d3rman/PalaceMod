using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    A full-screen menu for a feature, built from the game's own widgets and run by the game's UIFrameManager, so
    Escape / the controller's back button return to whatever was open before, the game stays paused underneath, and
    keyboard, controller and mouse navigation work like in the game's menus.

        var frame = new ModFrame("Compendium")
            .AddTab("Enemies", page =>
            {
                page.Header("Night 3");
                page.Row("Hit points", "120", tooltip: "Base 100, +20% night difficulty");
                page.Text("Free text, wrapped to the page width.");
                page.Button("Reset", () => ...);
            })
            .AddTab("Units", page => ...);
        frame.Open();       // e.g. from PauseMenu.AddButton(..., () => frame.Open())
        frame.Refresh();    // rebuild the current tab after the data changed

    Tabs are built when shown, so a page always shows current data. The frame is built on first Open and rebuilt if
    the game's UI is; if building fails, nothing is left behind and Open logs the error and does nothing.
    PalaceMod's mark: a verdigris diamond beside the title and a thin verdigris rule under it.
*/
namespace PalaceMod
{
    public sealed class ModFrame
    {
        private const float Left = 200f, ContentWidth = 860f;

        public string Title { get; private set; }
        private TextMeshProUGUI titleText;

        /// <summary>Renames the frame, also while it's open.</summary>
        public void SetTitle(string title)
        {
            Title = title;
            if (titleText != null) titleText.text = title;
        }

        /// <summary>Runs each time the frame is shown and its selection set: on Open, and when the player comes back
        /// from a menu opened on top of it. A good place to Refresh.</summary>
        public event Action Activated;

        internal void RaiseActivated()
        {
            try { Activated?.Invoke(); }
            catch (Exception e) { Plugin.Logger.LogError($"ModFrame \"{Title}\": Activated handler failed: {e}"); }
        }

        internal static readonly Dictionary<UIFrame, ModFrame> Frames = [];
        private readonly List<(string Label, Action<ModPage> Build)> tabs = [];
        private Action<ModPage> content;

        private GameObject root;
        private UIFrame frame;
        private readonly List<TFUITextButton> tabButtons = [];
        private RectTransform tabMarker, viewport, contentRoot;
        private TFUITextButton back;
        private ModScroll scroll;
        private int currentTab;
        private ModPage page;

        public ModFrame(string title) => Title = title;

        /// <summary>Adds a tab; <paramref name="build"/> fills its page each time it's shown.</summary>
        public ModFrame AddTab(string label, Action<ModPage> build)
        {
            if (root != null) throw new InvalidOperationException("add tabs before the frame is first opened");
            tabs.Add((label, build));
            return this;
        }

        /// <summary>A single page without tabs.</summary>
        public ModFrame SetContent(Action<ModPage> build)
        {
            content = build;
            return this;
        }

        public bool IsOpen => frame != null && UIFrameManager.instance?.ActiveFrame == frame;

        /// <summary>The game's UIFrame behind this menu (null until first opened).</summary>
        public UIFrame Frame => frame;

        /// <summary>Opens the menu on top of whatever is open (or over gameplay, pausing it).</summary>
        public void Open()
        {
            var manager = UIFrameManager.instance;
            if (manager == null) return;
            if (IsOpen) return;
            if (!EnsureBuilt()) return;
            ShowTab(currentTab, select: false);
            frame.firstSelected = tabButtons.Count > 0 ? tabButtons[currentTab] : (ThronefallUIElement)page.Elements.FirstOrDefault() ?? back;
            manager.ChangeActiveFrame(frame);
        }

        /// <summary>Closes the menu, returning to whatever was open before it.</summary>
        public void Close()
        {
            if (IsOpen) UIFrameManager.instance.CloseActiveFrame();
        }

        /// <summary>Rebuilds the current tab's page, keeping the selection where it was if it still exists.</summary>
        public void Refresh()
        {
            if (root == null) return;
            ShowTab(currentTab, select: true, keepScroll: true);
        }

        // ---- Build ----------------------------------------------------------------------------------------------

        private bool EnsureBuilt()
        {
            if (root != null && root.transform.parent == GameWidgets.Canvas) return true;
            if (root != null) Object.Destroy(root);
            root = null;
            tabButtons.Clear();
            var canvas = GameWidgets.Canvas;
            if (canvas == null) return false;
            GameObject obj = null;
            try
            {
                obj = new GameObject($"PalaceMod Frame {Title}", typeof(RectTransform));
                obj.SetActive(false);
                obj.transform.SetParent(canvas, false);
                Stretch((RectTransform)obj.transform);
                // Added while inactive: the frame registers itself with UIFrameManager when first activated.
                frame = obj.AddComponent<UIFrame>();
                Frames[frame] = this;
                frame.freezeTime = true;
                frame.freezePlayer = true;
                frame.storeLastSelectedElementInFrameStack = true;
                Build(obj.transform);
                // The frame sits above the game's frames it can be opened from.
                obj.transform.SetAsLastSibling();
                root = obj;
                return true;
            }
            catch (Exception e)
            {
                if (obj != null) Object.DestroyImmediate(obj);
                frame = null;
                tabButtons.Clear();
                Plugin.Logger.LogError($"ModFrame \"{Title}\": couldn't build the menu: {e}");
                Notify.Show($"Couldn't open {Title}; see the log", NotifyKind.Error, key: "modframe-error");
                return false;
            }
        }

        private void Build(Transform root)
        {
            var backdrop = GameWidgets.Clone(GameWidgets.Backdrop, root, "Backdrop", keepChildren: false);
            Stretch((RectTransform)backdrop.transform);
            backdrop.SetActive(true);

            // Title, like the game's "Settings" / "Paused".
            var title = GameWidgets.Clone(GameWidgets.Title, root, "Title", keepChildren: false).GetComponent<TextMeshProUGUI>();
            foreach (var c in title.GetComponents<Component>())
            {
                if (c is not (RectTransform or CanvasRenderer or TextMeshProUGUI or ContentSizeFitter)) Object.DestroyImmediate(c);
            }
            title.text = Title;
            titleText = title;
            title.alignment = TextAlignmentOptions.TopLeft;
            title.raycastTarget = false;
            var titleRect = title.rectTransform;
            TopLeft(titleRect, Left, -110f);
            titleRect.sizeDelta = new Vector2(1200f, 110f);
            title.gameObject.SetActive(true);
            var emblem = ModStyle.AddEmblem(title.transform, 15f);
            var follower = emblem.gameObject.AddComponent<EmblemFollower>();
            follower.Text = title;
            follower.Gap = 26f;
            var rule = ModStyle.AddRule(root, 420f);
            TopLeft(rule, Left, -222f);

            // Tabs, like Settings' Video / Audio / ... row.
            float top = -250f;
            if (tabs.Count > 1)
            {
                var row = new GameObject("Tabs", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
                row.transform.SetParent(root, false);
                var rowRect = (RectTransform)row.transform;
                TopLeft(rowRect, Left, top);
                rowRect.sizeDelta = new Vector2(ContentWidth, 44f);
                var layout = row.GetComponent<HorizontalLayoutGroup>();
                layout.spacing = 44f;
                layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childControlWidth = layout.childControlHeight = false;
                layout.childForceExpandWidth = layout.childForceExpandHeight = false;
                row.GetComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                for (int i = 0; i < tabs.Count; i++)
                {
                    int index = i;
                    var tab = GameWidgets.Button(GameWidgets.TabButton, row.transform, $"Tab {tabs[i].Label}", tabs[i].Label, () => ShowTab(index, select: false));
                    ((RectTransform)tab.transform).sizeDelta = new Vector2(((RectTransform)tab.transform).sizeDelta.x, 44f);
                    tab.gameObject.SetActive(true);
                    tabButtons.Add(tab);
                }
                for (int i = 0; i < tabButtons.Count; i++)
                {
                    tabButtons[i].leftNav = tabButtons[(i + tabButtons.Count - 1) % tabButtons.Count];
                    tabButtons[i].rightNav = tabButtons[(i + 1) % tabButtons.Count];
                }
                // The current tab is underlined in the game's gold.
                var marker = new GameObject("Tab Marker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                marker.transform.SetParent(row.transform, false);
                marker.GetComponent<LayoutElement>().ignoreLayout = true;
                marker.GetComponent<Image>().color = ModStyle.Gold;
                marker.GetComponent<Image>().raycastTarget = false;
                tabMarker = (RectTransform)marker.transform;
                tabMarker.anchorMin = tabMarker.anchorMax = new Vector2(0f, 0.5f);
                tabMarker.pivot = new Vector2(0.5f, 1f);
                top -= 70f;
                // Layout only runs on active objects, so place the marker again once the frame is showing.
                frame.onActivate.AddListener(UpdateTabMarker);
                frame.onNewSelection.AddListener(() =>
                {
                    int selected = tabButtons.IndexOf(frame.CurrentSelection as TFUITextButton);
                    if (selected >= 0 && selected != currentTab) ShowTab(selected, select: false);
                });
            }

            // Scrolling page area.
            var view = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            view.transform.SetParent(root, false);
            viewport = (RectTransform)view.transform;
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(0f, 1f);
            viewport.pivot = new Vector2(0f, 1f);
            viewport.offsetMin = new Vector2(Left - 30f, 150f);  // a little room on the left for selection growth
            viewport.offsetMax = new Vector2(Left + ContentWidth, top - 10f);
            var list = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            list.transform.SetParent(view.transform, false);
            contentRoot = (RectTransform)list.transform;
            contentRoot.anchorMin = new Vector2(0f, 1f);
            contentRoot.anchorMax = new Vector2(1f, 1f);
            contentRoot.pivot = new Vector2(0.5f, 1f);
            contentRoot.offsetMin = new Vector2(30f, 0f);
            contentRoot.offsetMax = new Vector2(0f, 0f);
            var vertical = list.GetComponent<VerticalLayoutGroup>();
            vertical.spacing = 4f;
            vertical.childControlWidth = vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childForceExpandHeight = false;
            vertical.padding = new RectOffset(0, 0, 4, 24);
            list.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll = view.AddComponent<ModScroll>();
            scroll.Init(frame, viewport, contentRoot);

            // Back, bottom left, like the game's menus.
            back = GameWidgets.Button(GameWidgets.MenuButton, root, "Back", "Back", Close);
            back.GetComponent<TextMeshProUGUI>().alignment = TextAlignmentOptions.MidlineLeft;
            var backRect = (RectTransform)back.transform;
            backRect.anchorMin = backRect.anchorMax = new Vector2(0f, 0f);
            backRect.pivot = new Vector2(0f, 0.5f);
            backRect.anchoredPosition = new Vector2(Left, 90f);
            back.gameObject.SetActive(true);
        }

        private void ShowTab(int index, bool select, bool keepScroll = false)
        {
            if (frame == null) return;
            currentTab = Mathf.Clamp(index, 0, Math.Max(0, tabs.Count - 1));
            int selectedIndex = page?.Elements.IndexOf(frame.CurrentSelection) ?? -1;
            // Don't leave the selection on an element that's about to be destroyed.
            if (select && selectedIndex >= 0) frame.Select(tabButtons.Count > 0 ? tabButtons[currentTab] : back);

            foreach (Transform child in contentRoot.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            page = new ModPage(contentRoot);
            var build = tabs.Count > 0 ? tabs[currentTab].Build : content;
            try
            {
                build?.Invoke(page);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"ModFrame \"{Title}\": building the page failed: {e}");
                page.Text("This page couldn't be shown; the error is in the BepInEx log.");
            }

            // Navigation: tabs <-> first element, elements in order, last element <-> Back.
            var elements = page.Elements;
            ThronefallUIElement up = tabButtons.Count > 0 ? tabButtons[currentTab] : null;
            foreach (var tab in tabButtons) tab.botNav = elements.FirstOrDefault() ?? back;
            for (int i = 0; i < elements.Count; i++)
            {
                elements[i].topNav = i == 0 ? up : elements[i - 1];
                elements[i].botNav = i == elements.Count - 1 ? back : elements[i + 1];
            }
            back.topNav = elements.LastOrDefault() ?? up;
            frame.RefetchManagedElements();
            if (!keepScroll) scroll.ResetTo(0f);
            else
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(contentRoot);
                scroll.Clamp();
            }
            if (select && selectedIndex >= 0 && elements.Count > 0) frame.Select(elements[Math.Min(selectedIndex, elements.Count - 1)]);
            UpdateTabMarker();
        }

        private void UpdateTabMarker()
        {
            if (tabMarker == null || tabButtons.Count == 0) return;
            var tab = (RectTransform)tabButtons[currentTab].transform;
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)tab.parent);
            var text = tab.GetComponent<TextMeshProUGUI>();
            float width = Mathf.Max(24f, text.GetPreferredValues(text.text).x);
            tabMarker.sizeDelta = new Vector2(width, 2f);
            tabMarker.anchoredPosition = new Vector2(tab.anchoredPosition.x + (0.5f - tab.pivot.x) * tab.rect.width, -18f);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static void TopLeft(RectTransform rect, float x, float y)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
        }
    }

    /// <summary>Hosts the ModFrame hooks into the game's frame code.</summary>
    [ManagerOrder(-44)]
    public class ModFrameManager : BaseManager
    {
        // After Activate the frame has its selection (fresh or restored from the frame stack).
        [HarmonyLib.HarmonyPatch(typeof(UIFrame), nameof(UIFrame.Activate))]
        private class UIFrame_Activate_Patch
        {
            private static void Postfix(UIFrame __instance)
            {
                if (__instance != null && ModFrame.Frames.TryGetValue(__instance, out var frame)) frame.RaiseActivated();
            }
        }
    }

    /// <summary>Builds the contents of one ModFrame page, top to bottom.</summary>
    public sealed class ModPage
    {
        private readonly Transform content;

        /// <summary>The page's selectable elements, in navigation order.</summary>
        internal List<ThronefallUIElement> Elements { get; } = [];

        /// <summary>The page's content area (a vertical layout), for parenting custom widgets.</summary>
        public RectTransform Content => (RectTransform)content;

        /// <summary>Adds a selectable you made yourself to the page's up/down navigation, after what's there.
        /// Its topNav/botNav are overwritten; set leftNav/rightNav yourself.</summary>
        public void AddElement(ThronefallUIElement element) => Elements.Add(element);

        internal ModPage(Transform content) => this.content = content;

        /// <summary>A section heading in the game's title font.</summary>
        public TextMeshProUGUI Header(string text)
        {
            var tmp = GameWidgets.Text(content, "Header", text, ModStyle.TitleFont, 40f, ModStyle.Gold, TextAlignmentOptions.BottomLeft);
            tmp.margin = new Vector4(0f, Elements.Count > 0 || content.childCount > 1 ? 18f : 0f, 0f, 4f);
            return tmp;
        }

        /// <summary>A wrapped paragraph of body text (not selectable).</summary>
        public TextMeshProUGUI Text(string text)
        {
            var tmp = GameWidgets.Text(content, "Text", text, ModStyle.BodyFont, 25f, new Color(ModStyle.Gold.r, ModStyle.Gold.g, ModStyle.Gold.b, 0.85f));
            tmp.enableWordWrapping = true;
            tmp.margin = new Vector4(0f, 2f, 0f, 6f);
            return tmp;
        }

        /// <summary>A selectable row: optional icon, label on the left, optional value on the right, optional tooltip
        /// and action.</summary>
        public ModRow Row(string label, string value = null, string tooltip = null, Action onClick = null, Sprite icon = null) =>
            Row(label, value, tooltip == null ? null : () => tooltip, onClick, icon);

        /// <summary>A row whose tooltip text is built when it's shown.</summary>
        public ModRow Row(string label, string value, Func<string> tooltip, Action onClick = null, Sprite icon = null)
        {
            var button = GameWidgets.Button(GameWidgets.MenuButton, content, $"Row {label}", label, onClick);
            var text = button.GetComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.MidlineLeft;
            button.gameObject.AddComponent<LayoutElement>().preferredHeight = 46f;
            if (icon != null)
            {
                const float size = 36f;
                var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                iconObj.transform.SetParent(button.transform, false);
                var image = iconObj.GetComponent<Image>();
                image.sprite = icon;
                image.preserveAspect = true;
                image.raycastTarget = false;
                var iconRect = (RectTransform)iconObj.transform;
                iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
                iconRect.pivot = new Vector2(0f, 0.5f);
                iconRect.sizeDelta = new Vector2(size, size);
                text.margin = new Vector4(size + 12f, 0f, 0f, 0f);
            }
            TextMeshProUGUI valueText = null;
            if (value != null)
            {
                valueText = GameWidgets.Text(button.transform, "Value", value, ModStyle.BoldFont, 27f, ModStyle.Gold, TextAlignmentOptions.MidlineRight);
                var rect = valueText.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
            }
            if (tooltip != null) ModTooltip.Attach(button, label, tooltip);
            button.gameObject.SetActive(true);
            Elements.Add(button);
            return new ModRow(button, valueText);
        }

        /// <summary>A selectable action, styled like the game's menu entries.</summary>
        public TFUITextButton Button(string label, Action onClick, string tooltip = null) =>
            Row(label, null, tooltip, onClick).Button;

        /// <summary>An empty full-width area of the given height for custom drawing (graphs, grids). Not selectable;
        /// add your own ThronefallUIElements under it only if you also wire their navigation.</summary>
        public RectTransform Custom(string name, float height)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
            obj.transform.SetParent(content, false);
            obj.GetComponent<LayoutElement>().preferredHeight = height;
            return (RectTransform)obj.transform;
        }

        /// <summary>Vertical space.</summary>
        public void Space(float height = 20f)
        {
            var obj = new GameObject("Space", typeof(RectTransform), typeof(LayoutElement));
            obj.transform.SetParent(content, false);
            obj.GetComponent<LayoutElement>().preferredHeight = height;
        }
    }

    /// <summary>A row on a ModPage; change its text in place without rebuilding the page.</summary>
    public sealed class ModRow
    {
        public TFUITextButton Button { get; }
        private readonly TextMeshProUGUI value;

        internal ModRow(TFUITextButton button, TextMeshProUGUI value)
        {
            Button = button;
            this.value = value;
        }

        public void SetLabel(string label) => Button.SetOriginalString(label);

        public void SetValue(string text)
        {
            if (value != null) value.text = text;
        }
    }

    /// <summary>Scrolls a ModFrame page: keeps the selection in view and follows the mouse wheel, on unscaled time.</summary>
    internal class ModScroll : MonoBehaviour
    {
        private UIFrame frame;
        private RectTransform viewport, content;
        private float target, velocity;

        public void Init(UIFrame frame, RectTransform viewport, RectTransform content)
        {
            this.frame = frame;
            this.viewport = viewport;
            this.content = content;
            frame.onNewSelection.AddListener(KeepSelectionVisible);
            // A thin scroll bar right of the page, in the game's slider colours; shown only when the page overflows.
            track = Bar("Scroll Bar", viewport.parent, new Color(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.6f), 2f);
            track.anchorMin = track.anchorMax = new Vector2(0f, 1f);
            track.pivot = new Vector2(0.5f, 1f);
            thumb = Bar("Thumb", track, ModStyle.Gold, 4f);
            thumb.anchorMin = thumb.anchorMax = new Vector2(0.5f, 1f);
            thumb.pivot = new Vector2(0.5f, 1f);
        }

        private RectTransform track, thumb;

        private static RectTransform Bar(string name, Transform parent, Color color, float width)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            var image = obj.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = (RectTransform)obj.transform;
            rect.sizeDelta = new Vector2(width, 0f);
            return rect;
        }

        public void ResetTo(float y)
        {
            target = y;
            velocity = 0f;
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
        }

        public void Clamp() => target = Mathf.Clamp(target, 0f, MaxScroll);

        private float MaxScroll => Mathf.Max(0f, content.rect.height - viewport.rect.height);

        private void KeepSelectionVisible()
        {
            var selected = frame.CurrentSelection;
            if (selected == null || !selected.transform.IsChildOf(content)) return;
            var rect = (RectTransform)selected.transform;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            // Positions below the content's top edge (positive down), unaffected by the current scroll.
            float topEdge = -content.InverseTransformPoint(corners[1]).y + content.rect.yMax;
            float bottomEdge = -content.InverseTransformPoint(corners[0]).y + content.rect.yMax;
            float margin = 40f, height = viewport.rect.height;
            if (topEdge - margin < target) target = topEdge - margin;
            else if (bottomEdge + margin > target + height) target = bottomEdge + margin - height;
            target = Mathf.Clamp(target, 0f, MaxScroll);
        }

        private void Update()
        {
            if (UIFrameManager.instance?.ActiveFrame == frame && Input.mouseScrollDelta.y != 0f)
            {
                target = Mathf.Clamp(target - Input.mouseScrollDelta.y * 60f, 0f, MaxScroll);
            }
            float y = Mathf.SmoothDamp(content.anchoredPosition.y, target, ref velocity, 0.12f, float.PositiveInfinity, Time.unscaledDeltaTime);
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);

            float max = MaxScroll, height = viewport.rect.height;
            track.gameObject.SetActive(max > 1f);
            if (max <= 1f) return;
            // Track sits just right of the viewport, over its full height.
            track.anchoredPosition = new Vector2(viewport.offsetMax.x + 16f, viewport.offsetMax.y);
            track.sizeDelta = new Vector2(2f, height);
            float thumbHeight = Mathf.Max(30f, height * height / content.rect.height);
            thumb.sizeDelta = new Vector2(4f, thumbHeight);
            thumb.anchoredPosition = new Vector2(0f, -(height - thumbHeight) * Mathf.Clamp01(y / max));
        }
    }
}
