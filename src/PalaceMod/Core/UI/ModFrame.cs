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
        private RectTransform ruleRect;

        /// <summary>Renames the frame, also while it's open.</summary>
        public void SetTitle(string title)
        {
            Title = title;
            if (titleText != null)
            {
                titleText.text = title;
                ruleRect.sizeDelta = new Vector2(Mathf.Clamp(titleText.GetPreferredValues(title).x, 240f, 900f), ruleRect.sizeDelta.y);
            }
        }

        /// <summary>Runs each time the frame is shown and its selection set: on Open, and when the player comes back
        /// from a menu opened on top of it. A good place to Refresh.</summary>
        public event Action Activated;

        internal void RaiseActivated()
        {
            try { Activated?.Invoke(); }
            catch (Exception e) { Log.For<ModFrameManager>().Caught($"ModFrame \"{Title}\": Activated handler failed", e); }
        }

        internal static readonly Dictionary<UIFrame, ModFrame> Frames = [];
        private readonly List<(string Label, Action<ModPage> Build, bool Wide)> tabs = [];
        private Action<ModPage> content;
        private bool contentWide;
        private float viewTop;

        private GameObject root;
        private UIFrame frame;
        private readonly List<TFUITextButton> tabButtons = [];
        private RectTransform tabMarker, viewport, contentRoot, aside;
        private TFUITextButton back;
        private ModScroll scroll;
        private int currentTab;
        private ModPage page;

        public ModFrame(string title) => Title = title;

        /// <summary>Adds a tab; <paramref name="build"/> fills its page each time it's shown. A <paramref name="wide"/>
        /// page gets the frame's full width and no aside (for boards, maps and grids that lay themselves out; see
        /// ModPage.Size).</summary>
        public ModFrame AddTab(string label, Action<ModPage> build, bool wide = false)
        {
            if (root != null) throw new InvalidOperationException("add tabs before the frame is first opened");
            tabs.Add((label, build, wide));
            return this;
        }

        /// <summary>A single page without tabs (<paramref name="wide"/>: see AddTab).</summary>
        public ModFrame SetContent(Action<ModPage> build, bool wide = false)
        {
            content = build;
            contentWide = wide;
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
            // Already open under something (a menu opened from it): go back down to it rather than open a second
            // copy on top, which Escape would later come back to.
            if (frame != null && OnStack(frame))
            {
                for (int guard = 0; guard < 16 && manager.ActiveFrame != frame && OnStack(frame); guard++) manager.CloseActiveFrame();
                if (IsOpen) return;
            }
            if (!EnsureBuilt()) return;
            ShowTab(currentTab, select: false);
            frame.firstSelected = tabButtons.Count > 0 ? tabButtons[currentTab] : (ThronefallUIElement)page.Elements.FirstOrDefault() ?? back;
            manager.ChangeActiveFrame(frame);
        }

        /// <summary>Whether <paramref name="f"/> is on UIFrameManager's stack (open, under the active menu).</summary>
        internal static bool OnStack(UIFrame f)
        {
            try
            {
                foreach (var element in HarmonyLib.Traverse.Create(UIFrameManager.instance).Field("frameStack").GetValue<System.Collections.IEnumerable>())
                    if (HarmonyLib.Traverse.Create(element).Field("frame").GetValue<UIFrame>() == f) return true;
            }
            catch (Exception e) { Log.Core.Warn($"ModFrame: couldn't read the frame stack: {e.Message}"); }
            return false;
        }

        /// <summary>Closes the menu, returning to whatever was open before it.</summary>
        public void Close()
        {
            if (IsOpen) UIFrameManager.instance.CloseActiveFrame();
        }

        /// <summary>Switches to the tab labelled <paramref name="label"/>: right away if the menu is open, else the
        /// next Open shows it.</summary>
        public void SelectTab(string label)
        {
            int index = tabs.FindIndex(t => t.Label == label);
            if (index < 0) return;
            if (!IsOpen)
            {
                currentTab = index;
                return;
            }
            ShowTab(index, select: false);
            if (index < tabButtons.Count) frame.Select(tabButtons[index]);
        }

        /// <summary>Rebuilds the current tab's page, keeping the selection where it was if it still exists.</summary>
        public void Refresh()
        {
            if (root == null) return;
            ShowTab(currentTab, select: true, keepScroll: true);
        }

        /// <summary>Switches to the tab with this label (selecting its tab button while open), or shows it first on
        /// the next Open.</summary>
        public void ShowTab(string label)
        {
            int index = tabs.FindIndex(t => t.Label == label);
            if (index < 0) throw new ArgumentException($"no tab \"{label}\"");
            if (IsOpen && index < tabButtons.Count) frame.Select(tabButtons[index]); // onNewSelection shows the tab
            else currentTab = index;
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
                Log.For<ModFrameManager>().Caught($"ModFrame \"{Title}\": couldn't build the menu", e);
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
            var rule = ModStyle.AddRule(root, Mathf.Clamp(title.GetPreferredValues(Title).x, 240f, 900f));
            ruleRect = rule;
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
            viewTop = top - 10f;
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

            // Fixed area right of the page (past the scroll bar), for a page's detail view; empty unless a page uses it.
            var side = new GameObject("Aside", typeof(RectTransform));
            side.transform.SetParent(root, false);
            aside = (RectTransform)side.transform;
            aside.anchorMin = new Vector2(0f, 0f);
            aside.anchorMax = new Vector2(1f, 1f);
            aside.pivot = new Vector2(0f, 1f);
            aside.offsetMin = new Vector2(Left + ContentWidth + 70f, 150f);
            aside.offsetMax = new Vector2(-80f, top - 10f);

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
            string selectedName = selectedIndex >= 0 ? frame.CurrentSelection.name : null;
            // Don't leave the selection on an element that's about to be destroyed.
            if (select && selectedIndex >= 0) frame.Select(tabButtons.Count > 0 ? tabButtons[currentTab] : back);

            foreach (Transform child in contentRoot.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            foreach (Transform child in aside.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            bool wide = tabs.Count > 0 ? tabs[currentTab].Wide : contentWide;
            // Wide: the viewport runs to the right edge and the aside is hidden.
            viewport.anchorMax = new Vector2(wide ? 1f : 0f, 1f);
            viewport.offsetMax = new Vector2(wide ? -80f : Left + ContentWidth, viewTop);
            aside.gameObject.SetActive(!wide);
            page = new ModPage(contentRoot, wide ? null : aside)
            {
                // Content padding (VerticalLayoutGroup: 4 top, 24 bottom) and its 30 px left inset come off.
                Size = new Vector2(viewport.rect.width - 30f, viewport.rect.height - 29f),
                Frame = this,
            };
            var build = tabs.Count > 0 ? tabs[currentTab].Build : content;
            try
            {
                build?.Invoke(page);
            }
            catch (Exception e)
            {
                Log.For<ModFrameManager>().Caught($"ModFrame \"{Title}\": building the page failed", e);
                page.Text("This page couldn't be shown; the error is in the BepInEx log.");
            }

            // Navigation: tabs <-> first element, elements in order, last element <-> Back. Elements added as a row
            // (AddElementRow) are one step: up/down goes to the same column of the row above/below.
            var elements = page.Elements;
            var rows = page.NavigationRows();
            ThronefallUIElement up = tabButtons.Count > 0 ? tabButtons[currentTab] : null;
            foreach (var tab in tabButtons) tab.botNav = elements.FirstOrDefault() ?? back;
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < rows[r].Count; c++)
                {
                    rows[r][c].topNav = r == 0 ? up : rows[r - 1][Math.Min(c, rows[r - 1].Count - 1)];
                    rows[r][c].botNav = r == rows.Count - 1 ? back : rows[r + 1][Math.Min(c, rows[r + 1].Count - 1)];
                }
            }
            back.topNav = rows.Count > 0 ? rows[rows.Count - 1][0] : up;
            page.RaiseWired(up, back);
            frame.RefetchManagedElements();
            // Lay the page out now, so scrolling to the restored selection measures its real position.
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRoot);
            if (!keepScroll) scroll.ResetTo(0f);
            else scroll.Clamp();
            if (select && selectedIndex >= 0 && elements.Count > 0)
            {
                // The same row (by label) if it's still there, else the same position.
                frame.Select(elements.FirstOrDefault(e => e.name == selectedName) ?? elements[Math.Min(selectedIndex, elements.Count - 1)]);
            }
            UpdateTabMarker();
        }

        private void UpdateTabMarker()
        {
            if (tabMarker == null || tabButtons.Count == 0) return;
            var follower = tabMarker.GetComponent<TabMarker>() ?? tabMarker.gameObject.AddComponent<TabMarker>();
            follower.Tab = (RectTransform)tabButtons[currentTab].transform;
        }

        // Keeps the underline under the current tab; tabs resize while their selection animation plays, so this
        // follows every frame rather than measuring once.
        private class TabMarker : MonoBehaviour
        {
            public RectTransform Tab;

            private void LateUpdate()
            {
                if (Tab == null) return;
                var rect = (RectTransform)transform;
                var row = (RectTransform)Tab.parent;
                var center = row.InverseTransformPoint(Tab.TransformPoint(Tab.rect.center));
                rect.sizeDelta = new Vector2(Mathf.Max(24f, Tab.rect.width * 0.9f), 2f);
                rect.localPosition = new Vector3(center.x, center.y - 18f, 0f);
            }
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

        /// <summary>The room the page has without scrolling (width, height), e.g. for a wide page's Custom area.</summary>
        public Vector2 Size { get; internal set; } = new(860f, 600f);

        /// <summary>Space a field (Number, Stepper, TextInput...) keeps for its label before the value; narrow pages (a
        /// docked panel) use less.</summary>
        public float FieldIndent { get; set; } = ModField.ValueIndent;

        /// <summary>The frame showing the page (null in a ModPopup).</summary>
        public ModFrame Frame { get; internal set; }

        /// <summary>Runs after the frame has wired the page's up/down navigation, with what's above the first row (the
        /// current tab, or null) and below the last (Back). For elements wired by hand (ModNav), to connect them.</summary>
        public event Action<ThronefallUIElement, ThronefallUIElement> Wired;

        internal void RaiseWired(ThronefallUIElement above, ThronefallUIElement below)
        {
            try { Wired?.Invoke(above, below); }
            catch (Exception e) { Log.For<ModFrameManager>().Caught("ModPage: Wired handler failed", e); }
        }

        /// <summary>Adds a selectable you made yourself to the page's up/down navigation, after what's there.
        /// Its topNav/botNav are overwritten; set leftNav/rightNav yourself.</summary>
        public void AddElement(ThronefallUIElement element) => Elements.Add(element);

        /// <summary>Adds selectables that sit side by side (one line of a grid) as one step of the up/down navigation:
        /// left/right moves along the row, up/down to the nearest column of the row above or below. Call once per
        /// line, top to bottom.</summary>
        public void AddElementRow(IReadOnlyList<ThronefallUIElement> row)
        {
            if (row == null || row.Count == 0) return;
            var line = row.ToList();
            for (int i = 0; i < line.Count; i++)
            {
                line[i].leftNav = i > 0 ? line[i - 1] : null;
                line[i].rightNav = i < line.Count - 1 ? line[i + 1] : null;
                rowOf[line[i]] = line;
                Elements.Add(line[i]);
            }
        }

        private readonly Dictionary<ThronefallUIElement, List<ThronefallUIElement>> rowOf = [];

        /// <summary>The page's elements as navigation rows, top to bottom (a lone element is a row of one).</summary>
        internal List<List<ThronefallUIElement>> NavigationRows()
        {
            var rows = new List<List<ThronefallUIElement>>();
            foreach (var e in Elements)
            {
                if (!rowOf.TryGetValue(e, out var line)) rows.Add([e]);
                else if (line[0] == e) rows.Add(line);
            }
            return rows;
        }

        /// <summary>A fixed area to the right of the page that doesn't scroll, e.g. for details of the selected
        /// element. It's emptied with the page; lay out its children yourself.</summary>
        public RectTransform Aside { get; }

        internal ModPage(Transform content, RectTransform aside = null)
        {
            this.content = content;
            Aside = aside;
        }

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
            tmp.characterSpacing = 0f; // the tooltip text it's cloned from is letter-spaced; paragraphs read better without
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
            var row = new ModRow(button, valueText);
            row.SetIcon(icon);
            return row;
        }

        /// <summary>A selectable action, styled like the game's menu entries.</summary>
        public TFUITextButton Button(string label, Action onClick, string tooltip = null) =>
            Row(label, null, tooltip, onClick).Button;

        /// <summary>
        /// A row whose value is changed with left/right (keyboard, controller) or by clicking the game's arrows
        /// either side of the value (mouse). <paramref name="step"/> gets -1 or +1; the value is re-read after each
        /// step. <paramref name="onClick"/> (optional) runs on apply, e.g. to act on the chosen value.
        /// </summary>
        public ModRow Stepper(string label, Func<string> value, Action<int> step, Action onClick = null, string tooltip = null, Sprite icon = null)
        {
            var row = Row(label, value(), tooltip, onClick, icon);
            void Step(int delta)
            {
                try { step(delta); }
                catch (Exception e) { Log.For<ModFrameManager>().Caught($"Stepper \"{label}\" failed", e); }
                // A step may rebuild the page (Refresh), destroying this row.
                if (row.Button != null) row.SetValue(value());
            }
            row.Button.onEmptyNavigate.AddListener(direction =>
            {
                if (direction == ThronefallUIElement.NavigationDirection.Left) Step(-1);
                else if (direction == ThronefallUIElement.NavigationDirection.Right) Step(1);
            });
            row.AddArrows(() => Step(-1), () => Step(1));
            return row;
        }

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

        // Clickable arrows either side of the value, cloned from the pause menu's Night selector.
        internal void AddArrows(Action decrease, Action increase)
        {
            var (left, right) = GameWidgets.Arrows;
            if (left == null || right == null || value == null) return; // keyboard and controller still work
            const float arrowSpace = 34f;
            value.rectTransform.offsetMax = new Vector2(-arrowSpace, 0f);
            var increaseButton = Arrow(right, increase, "Increase");
            var increaseRect = (RectTransform)increaseButton.transform;
            increaseRect.anchorMin = increaseRect.anchorMax = new Vector2(1f, 0.5f);
            increaseRect.anchoredPosition = new Vector2(-arrowSpace / 2f, 0f);
            var decreaseButton = Arrow(left, decrease, "Decrease");
            var follower = decreaseButton.gameObject.AddComponent<ArrowFollower>();
            follower.Value = value;
        }

        private TFUIEnumSelectorButton Arrow(TFUIEnumSelectorButton source, Action onClick, string name)
        {
            var arrow = GameWidgets.Clone(source, Button.transform, name).GetComponent<TFUIEnumSelectorButton>();
            // Clicking an arrow acts on it without moving the selection off the row.
            arrow.cannotBeSelected = true;
            // The game flips one arrow by rotating it 180 degrees around an edge pivot; pivot on the centre instead,
            // so positions below are the arrow's centre whichever way it points.
            var rect = (RectTransform)arrow.transform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            arrow.onApply.AddListener(() => onClick());
            arrow.gameObject.SetActive(true);
            return arrow;
        }

        // Keeps the decrease arrow just left of the value's visible text, whatever its length.
        private class ArrowFollower : MonoBehaviour
        {
            public TextMeshProUGUI Value;

            private void LateUpdate()
            {
                if (Value == null) return;
                var bounds = Value.textBounds;
                if (bounds.size.x <= 0f) return;
                var rect = (RectTransform)transform;
                var parent = (RectTransform)rect.parent;
                var left = parent.InverseTransformPoint(Value.rectTransform.TransformPoint(new Vector3(bounds.min.x, bounds.center.y, 0f)));
                rect.localPosition = new Vector3(left.x - 18f, left.y, 0f);
            }
        }

        internal ModRow(TFUITextButton button, TextMeshProUGUI value)
        {
            Button = button;
            this.value = value;
        }

        public void SetLabel(string label) => Button.SetOriginalString(label);

        /// <summary>Shows an icon left of the label (null: none). For rows made by helpers that take no icon.</summary>
        public void SetIcon(Sprite icon)
        {
            const float size = 36f;
            var existing = Button.transform.Find("Row Icon");
            var text = Button.GetComponent<TextMeshProUGUI>();
            if (icon == null)
            {
                if (existing == null) return;
                Object.Destroy(existing.gameObject);
                text.margin = new Vector4(0f, text.margin.y, text.margin.z, text.margin.w);
                return;
            }
            var iconObj = existing != null ? existing.gameObject : new GameObject("Row Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            iconObj.transform.SetParent(Button.transform, false);
            var image = iconObj.GetComponent<Image>();
            image.sprite = icon;
            image.color = ModStyle.Gold; // the game's icons are white and tinted gold in its menus
            image.preserveAspect = true;
            image.raycastTarget = false;
            var iconRect = (RectTransform)iconObj.transform;
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(size, size);
            text.margin = new Vector4(size + 12f, text.margin.y, text.margin.z, text.margin.w);
        }

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

        /// <summary>Only follow the mouse wheel while the pointer is over the viewport (for several scroll areas on
        /// one screen).</summary>
        public bool WheelNeedsHover;

        /// <summary>Gap between the viewport's right edge and the scroll bar.</summary>
        public float BarGap = 16f;

        public void Init(UIFrame frame, RectTransform viewport, RectTransform content)
        {
            this.frame = frame;
            this.viewport = viewport;
            this.content = content;
            frame.onNewSelection.AddListener(KeepSelectionVisible);
            listening = true;
            // A thin scroll bar right of the page, in the game's slider colours; shown only when the page overflows.
            track = Bar("Scroll Bar", viewport.parent, new Color(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.6f), 2f);
            track.anchorMin = track.anchorMax = new Vector2(0f, 1f);
            track.pivot = new Vector2(0.5f, 1f);
            thumb = Bar("Thumb", track, ModStyle.Gold, 4f);
            thumb.anchorMin = thumb.anchorMax = new Vector2(0.5f, 1f);
            thumb.pivot = new Vector2(0.5f, 1f);
        }

        private RectTransform track, thumb;
        private bool listening;

        // A scroll area inside a page or popup goes away with it; its frame lives on.
        private void OnDestroy()
        {
            if (listening && frame != null) frame.onNewSelection.RemoveListener(KeepSelectionVisible);
            if (track != null) Destroy(track.gameObject);
        }

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

        private bool PointerOver()
        {
            var canvas = viewport.GetComponentInParent<Canvas>()?.rootCanvas;
            var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(viewport, Input.mousePosition, camera);
        }

        public void ResetTo(float y)
        {
            target = y;
            velocity = 0f;
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
        }

        public void Clamp() => target = Mathf.Clamp(target, 0f, MaxScroll);

        private float MaxScroll => Mathf.Max(0f, content.rect.height - viewport.rect.height);

        private ThronefallUIElement last;
        private bool stepping;
        private ThronefallUIElement pendingReselect;

        private void KeepSelectionVisible()
        {
            if (stepping) return;
            var previous = last;
            if (content == null || viewport == null) return;
            var selected = frame.CurrentSelection;
            last = selected;
            if (selected == null || !selected.transform.IsChildOf(content)) return;
            // Hovering selects what's under the pointer: scrolling it into view then would move another element under
            // the still pointer, which hovering selects and scrolls to in turn, so a click near the edge lands a row
            // or two further on. Only keyboard and controller selection (the frame's mouse is asleep) scrolls.
            if (!frame.MouseSleeping && PointerOver()) return;
            var rect = (RectTransform)selected.transform;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            // Positions below the content's top edge (positive down), unaffected by the current scroll.
            float topEdge = -content.InverseTransformPoint(corners[1]).y + content.rect.yMax;
            float bottomEdge = -content.InverseTransformPoint(corners[0]).y + content.rect.yMax;
            float margin = 40f, height = viewport.rect.height, page = height - 2f * margin;
            float wanted = target;
            if (topEdge - margin < target) wanted = topEdge - margin;
            else if (bottomEdge + margin > target + height) wanted = bottomEdge + margin - height;
            wanted = Mathf.Clamp(wanted, 0f, MaxScroll);
            // Moving between two rows that have tall non-selectable content (text, Custom blocks) between them:
            // scroll one page towards the new row and keep the old selection, so keyboard and controller players
            // see everything in between. The next press continues until the row is in view.
            if (Mathf.Abs(wanted - target) > page && previous != null && previous.transform.IsChildOf(content) && previous.gameObject.activeInHierarchy)
            {
                target = Mathf.Clamp(target + Mathf.Sign(wanted - target) * page, 0f, MaxScroll);
                // Can't re-select from inside the selection event (the game finishes styling the new row after it).
                pendingReselect = previous;
                last = previous;
                return;
            }
            target = wanted;
        }

        private void Update()
        {
            if (pendingReselect != null)
            {
                var element = pendingReselect;
                pendingReselect = null;
                if (element != null && element.gameObject.activeInHierarchy && UIFrameManager.instance?.ActiveFrame == frame)
                {
                    stepping = true;
                    try { frame.Select(element); }
                    finally { stepping = false; }
                }
            }
            float wheel = ScrollInput.Pixels; // wheel notches and trackpad swipes, evened out (Core/UI/ScrollInput.cs)
            if (UIFrameManager.instance?.ActiveFrame == frame && wheel != 0f && (!WheelNeedsHover || PointerOver()))
            {
                target = Mathf.Clamp(target - wheel, 0f, MaxScroll);
            }
            float y = Mathf.SmoothDamp(content.anchoredPosition.y, target, ref velocity, 0.12f, float.PositiveInfinity, Time.unscaledDeltaTime);
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);

            float max = MaxScroll, height = viewport.rect.height;
            track.gameObject.SetActive(max > 1f);
            if (max <= 1f) return;
            // Track sits just right of the viewport, over its full height.
            track.anchoredPosition = new Vector2(viewport.offsetMax.x + BarGap, viewport.offsetMax.y);
            track.sizeDelta = new Vector2(2f, height);
            float thumbHeight = Mathf.Max(30f, height * height / content.rect.height);
            thumb.sizeDelta = new Vector2(4f, thumbHeight);
            thumb.anchoredPosition = new Vector2(0f, -(height - thumbHeight) * Mathf.Clamp01(y / max));
        }
    }
}
