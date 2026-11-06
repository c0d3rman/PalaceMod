using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Adds a "Mods" tab to the game's own Settings screen, generated from PalaceMod's BepInEx config entries:
    bools become checkboxes, enums and value lists become the game's arrow selectors, numeric ranges become sliders,
    and SettingsPane.AddButton actions become buttons in the style of the game's "Reset ..." entries.

    Layout: a scrolling list of pages on the left, grouped by area, and the selected page on the right: its name with
    the mod's gem, its description, a note when something holds it off, its rows (scrolling if they don't fit), and
    under a rule the description of the selected or hovered row.

    Pages: a page shows one or more config sections (Layout below). A small feature shares a page with related ones
    instead of getting its own: there, each feature's Enabled row is labelled with the feature's name and its other
    settings are indented under it. Pages are only a display grouping; every setting keeps its config section and key,
    so merging pages never moves a saved value. A page whose features are all off (or held off by the "Gameplay-changing
    features" master switch) is dimmed in the list, and so are the rows of a feature that's off, except its Enabled.

    Resetting: a row whose value differs from its default shows a small "Default" button on its right (click it, or
    move right onto it from a checkbox row); every page ends with "Reset to defaults" (its settings and keys); General
    has "Reset all PalaceMod settings", which asks first. Keys reset through KeybindsManager.

    Navigation: moving through the list with the keyboard or a controller shows each page as it's selected; right
    (or clicking a row) goes into its settings, and Escape, the controller's back button or the "Back" link return to
    the list (Escape on the list closes Settings as usual). Up and down stop at the first and last row. With the mouse,
    hovering a page highlights it and clicking shows it; the wheel scrolls whichever column the pointer is over.

    Everything is cloned from the game's existing widgets, found through SettingsUIHelper's fields and by component
    type rather than by GameObject path, and the game's localisation and settings-binding components are stripped
    from the clones. If anything goes wrong the tab is simply not added; the settings stay editable in the config
    file and BepInEx's ConfigurationManager (F1).
*/
namespace PalaceMod
{
    [ManagerOrder(-40)]
    public class SettingsPaneManager : BaseManager
    {
        private const string TabLabel = "Mods";
        private const string OtherGroup = "Other";

        // Player-facing areas and their pages, in order; each page lists the config sections it shows. Sections not
        // listed get a page of their own under "Other".
        private static readonly (string Group, (string Page, string[] Sections)[] Pages)[] Layout =
        [
            ("General", [("General", [GameplayFeatures.Section])]),
            ("Units", [
                ("Formations", ["Formations"]),
                ("Patrols & control groups", ["Patrols", "Control groups"]),
                ("Teleporters", ["Teleporters"]),
                ("Unit ranges", ["Unit ranges"]),
                ("Minigun", ["Minigun"]),
            ]),
            ("Buildings & gold", [
                ("Build previews & income", ["Build previews", "Income labels", "Income display"]),
                ("Upgrades", ["Upgrade info", "Undo upgrade", "Level 4 buildings"]),
            ]),
            ("Pause menu & info", [
                ("Pause menu", ["Pause menu score", "Enemy previews", "Run analytics"]),
                ("Stat details", ["Stat details"]),
            ]),
            ("Runs & saves", [
                ("Practice", ["Savestates", "Retry earlier day"]),
                ("Scores", ["Unranked runs", "Bonus-safe saves"]),
                ("Loadout randomizer", ["Loadout randomizer"]),
            ]),
            ("Builder", [("Builder", ["Builder"])]),
            (OtherGroup, []),
            ("Mod", [
                ("About & logs", ["Version badge", "Diagnostics"]),
                ("Fixes", ["Fixes", "Gold counter fix"]),
            ]),
            ("Performance", [
                ("Performance", ["Performance", "Frame rate"]),
                ("Battery", ["Battery"]),
                ("Optimizations & memory", ["Optimizations", "Memory"]),
                ("Advanced", ["Advanced"]),
            ]),
        ];

        // Sections without a feature of their own that the named feature switches on and off.
        private static readonly Dictionary<string, string> Governors = new()
        {
            ["Frame rate"] = "Performance",
            ["Battery"] = "Performance",
            ["Optimizations"] = "Performance",
            ["Memory"] = "Performance",
            ["Advanced"] = "Performance",
        };

        // Descriptions of pages that show several sections.
        private static readonly Dictionary<string, string> PageDescriptions = new()
        {
            ["Patrols & control groups"] = "Send units on patrol routes, and keep the game's control groups when you continue a run.",
            ["Build previews & income"] = "What the build previews show, and gold income above buildings and in the pause menu.",
            ["Upgrades"] = "See, undo and extend building and unit upgrades.",
            ["Pause menu"] = "Extra panels in the pause menu: the run's score and the night's enemies, and recording each night for Run Analytics.",
            ["Practice"] = "Tools for practising a level: automatic savestates and their keys, and going back to an earlier day. Pause > Savestates and Pause > Debug are always there. They make the run unranked.",
            ["Scores"] = "How mod features affect high scores, and keeping the no-restart bonus.",
            ["About & logs"] = "PalaceMod's version badge, its log, and a bundle of logs and settings to attach to bug reports.",
            ["Fixes"] = "Fixes for small bugs in the base game. Turn one off to restore vanilla behavior.",
            ["Performance"] = "Smoother frame pacing, battery saving and the optimizations on the next pages. Off hands frame limiting back to the game.",
            ["Optimizations & memory"] = "Small changes to the game's code that save work every frame without changing how it plays, and garbage-collector tuning that moves memory clean-up away from battles.",
        };

        // Descriptions for sections that have no feature and don't call SettingsPane.DescribeSection.
        private static readonly Dictionary<string, string> FallbackDescriptions = new()
        {
            [GameplayFeatures.Section] = "Switches for the whole mod.",
            ["Frame rate"] = "How the mod paces frames and caps the frame rate while playing and in menus.",
            ["Battery"] = "Lower frame rates and resolution on battery, in the background, when idle or when the Mac runs hot.",
            ["Optimizations"] = "Small changes to the game's code that save work every frame. They don't change how the game plays.",
            ["Memory"] = "Garbage-collector tuning that moves memory clean-up away from battles.",
            ["Advanced"] = "Low-level overrides for frame pacing and rendering. The defaults suit most Macs.",
        };

        private const string HeldOffNote = "Off for now: \"Gameplay-changing features\" is turned off in General. Its own settings are kept.";
        private const string ResetPageLabel = "Reset to defaults";
        private const string ResetAllLabel = "Reset all PalaceMod settings";

        private SettingsUIHelper helper;
        private bool injected;
        private SettingsUIHelper.SettingsTab tab;
        private GameObject toggleTemplate, selectorTemplate, sliderTemplate, buttonTemplate;
        private TextMeshProUGUI textTemplate;

        // One config section on a page.
        private sealed class Part
        {
            public string Section;
            public BaseManager Manager;          // the section's own feature, if any
            public BaseManager Owner;            // what switches it on and off: Manager, else its governor
            public List<ConfigEntryBase> Entries; // shown entries, Enabled first
            public List<SettingsPane.ActionButton> Buttons;
            public bool Off => Owner != null && !Owner.IsEnabled;
            public bool HeldOff => Owner != null && Owner.HeldOffByMasterSwitch;
            public bool HasSwitch => Manager?.EnabledEntry != null; // a menu-only feature has none
        }

        private sealed class Page
        {
            public string Name, Group, Description;
            public List<Part> Parts;
            public TFUITextButton Item;
            public bool Multi => Parts.Count > 1;
            // Dimmed in the list when everything on it is switched off.
            public bool Off => Parts.All(p => p.Off);
            public IEnumerable<ConfigEntryBase> Entries => Parts.SelectMany(p => p.Entries);
            public bool HasKeys => Keybinds.All.Any(k => Parts.Any(p => p.Section == k.Section));
        }

        private sealed class Row
        {
            public ThronefallUIElement Element;
            public Func<string> Description;
            public bool LeftToList; // checkbox and button rows; selectors and sliders use left/right for their value
            public ConfigEntryBase Entry;
            public Part Part;
            public bool IsEnabled; // the feature's own Enabled row: never dimmed
            public SettingTags Tags;
            public TFUITextButton Reset;
        }

        private List<Page> pages = [];
        private int current;
        private readonly List<Row> rows = [];
        private bool confirmingResetAll;

        // Built once per settings screen.
        private RectTransform root, listViewport, listContent, rowsViewport, rowsContent;
        private ModScroll listScroll, rowsScroll;
        private TextMeshProUGUI titleText, descriptionText, noteText, rowDescriptionText;
        private TFUITextButton backButton;

        public TFUITextButton TabButton => tab?.parentElement;
        public UIFrame Frame => helper?.targetFrame ?? FindHelper()?.targetFrame;

        private static SettingsUIHelper FindHelper() =>
            Resources.FindObjectsOfTypeAll<SettingsUIHelper>().FirstOrDefault(h => h.gameObject.scene.IsValid());

        [HarmonyPatch(typeof(SettingsUIHelper), "Awake")]
        private class SettingsUIHelper_Awake_Patch
        {
            private static void Postfix(SettingsUIHelper __instance) => Feature<SettingsPaneManager>.Instance?.TryInject(__instance);
        }

        // Escape (the game's "Pause Menu" action) and the controller's back button ("Cancel") inside a page's settings
        // go back to the page list instead of closing Settings.
        [HarmonyPatch(typeof(UIFrameManager), "Cancel")]
        private class UIFrameManager_Cancel_Patch
        {
            private static bool Prefix() => Feature<SettingsPaneManager>.Instance?.BackToList() != true;
        }

        [HarmonyPatch(typeof(UIFrameManager), "OpenMenu")]
        private class UIFrameManager_OpenMenu_Patch
        {
            private static bool Prefix() => Feature<SettingsPaneManager>.Instance?.BackToList() != true;
        }

        private const int MaxFallbackSearches = 30;
        private int fallbackSearches;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            GameplayFeatures.Changed += OnStateChanged;
            plugin.Config.SettingChanged += OnSettingChanged;
            RegisterCommands();
        }

        public override void Update()
        {
            if (burstFrames > 0) Burst();
            if (refreshPending)
            {
                refreshPending = false;
                RefreshStates();
            }
            // If the settings screen is ever rebuilt (its scene reloaded), add the tab to the new one.
            if (injected && helper == null)
            {
                injected = false;
                tab = null;
            }
            UpdateBackButton();
            // Fallback in case the settings screen woke up before our patch was applied; gives up after a minute or so.
            if (injected || fallbackSearches >= MaxFallbackSearches || Time.frameCount % 120 != 0) return;
            fallbackSearches++;
            var existing = FindHelper();
            if (existing != null && Traverse.Create(existing).Field("tabs").GetValue<List<SettingsUIHelper.SettingsTab>>()?.Count > 0)
            {
                TryInject(existing);
            }
        }

        private void TryInject(SettingsUIHelper settings)
        {
            if (injected) return;
            injected = true; // one attempt per settings screen; a failure shouldn't be retried every frame
            helper = settings;
            var created = new List<GameObject>();
            try
            {
                Inject(created);
                Log.Debug($"Added \"{TabLabel}\" tab with {pages.Count} pages");
            }
            catch (Exception e)
            {
                // Nothing has been hooked into the game's lists or navigation yet, so removing our clones undoes it.
                foreach (var obj in created) if (obj != null) Object.Destroy(obj);
                tab = null;
                root = null;
                Log.Error("Couldn't add the settings tab, use the config file or F1 instead", e);
            }
        }

        // Builds everything first and only then hooks it into the game's tab lists and navigation, so a failure
        // part-way leaves the settings screen untouched.
        private void Inject(List<GameObject> created)
        {
            FindTemplates();

            // Tab button, cloned from the Video tab; hidden until everything else is built.
            var sourceTab = helper.videoTab.parentElement;
            var tabObject = Object.Instantiate(sourceTab.gameObject, sourceTab.transform.parent);
            created.Add(tabObject);
            tabObject.SetActive(false);
            tabObject.name = "PalaceMod Tab";
            tabObject.transform.SetAsLastSibling();
            StripLocalization(tabObject);
            var tabButton = tabObject.GetComponent<TFUITextButton>();
            tabButton.SetOriginalString(TabLabel);

            // The page: a full-screen container beside the game's tab pages, shown and hidden by SettingsUIHelper.
            var page = new GameObject("PalaceMod Settings", typeof(RectTransform));
            created.Add(page);
            page.SetActive(false);
            page.transform.SetParent(helper.videoTab.contentParent.transform.parent, false);
            root = (RectTransform)page.transform;
            Stretch(root);
            BuildLayout();

            pages = CollectPages();
            if (pages.Count == 0) throw new InvalidOperationException("no settings to show");
            // Our own navigation; the game's per-tab computation assumes one column.
            tab = new SettingsUIHelper.SettingsTab
            {
                parentElement = tabButton,
                contentParent = page,
                childContainer = rowsContent.GetComponent<VerticalLayoutGroup>(),
                disableNavigationProcessing = true,
            };
            BuildList();
            ShowPage(Mathf.Clamp(current, 0, pages.Count - 1));

            // Hook into the game only once everything exists.
            SpliceIntoTabRow(sourceTab, tabButton);
            Traverse.Create(helper).Field("allTabs").GetValue<Dictionary<ThronefallUIElement, SettingsUIHelper.SettingsTab>>().Add(tabButton, tab);
            Traverse.Create(helper).Field("tabs").GetValue<List<SettingsUIHelper.SettingsTab>>().Add(tab);
            helper.targetFrame.onNewSelection.AddListener(OnSelectionChanged);
            helper.targetFrame.onNewFocus.AddListener(UpdateRowDescription);
            tabObject.SetActive(true);
            helper.targetFrame.RefetchManagedElements();
        }

        // ---- Templates ----------------------------------------------------------------------------------------

        private void FindTemplates()
        {
            var rowsOf = new[] { helper.videoTab, helper.audioTab, helper.gameplayTab, helper.accessibilityTab }
                .Where(t => t?.childContainer != null)
                .SelectMany(t => t.childContainer.transform.Cast<Transform>())
                .Select(t => t.gameObject)
                .ToList();
            toggleTemplate = rowsOf.FirstOrDefault(r => r.GetComponentInChildren<Checkbox>(true) != null);
            selectorTemplate = rowsOf.FirstOrDefault(r => r.GetComponentInChildren<EnumSelector>(true) is { delayedApply: false });
            sliderTemplate = rowsOf.FirstOrDefault(r => r.GetComponentInChildren<TFUISlider>(true) != null && r.GetComponentInChildren<TFUISlider>(true).GetType() == typeof(TFUISlider));
            // The game's "Reset Video Settings" style entries: a text button with no control in it.
            buttonTemplate = rowsOf.FirstOrDefault(r => r.GetComponent<TFUITextButton>() != null && r.GetComponentInChildren<Checkbox>(true) == null
                && r.GetComponentInChildren<EnumSelector>(true) == null && r.GetComponentInChildren<TFUISlider>(true) == null);
            if (toggleTemplate == null || selectorTemplate == null || sliderTemplate == null || buttonTemplate == null)
            {
                throw new InvalidOperationException($"missing row templates (toggle {toggleTemplate != null}, selector {selectorTemplate != null}, " +
                    $"slider {sliderTemplate != null}, button {buttonTemplate != null})");
            }
            // Plain text: the game's own per-row hint (the text beside the selected render-scale slider), else a label.
            var slider = sliderTemplate.GetComponentInChildren<TFUISlider>(true);
            textTemplate = (slider.tooltip != null ? slider.tooltip.GetComponentInChildren<TextMeshProUGUI>(true) : null)
                ?? toggleTemplate.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        private static void StripLocalization(GameObject root)
        {
            foreach (var localize in root.GetComponentsInChildren<I2.Loc.Localize>(true)) Object.DestroyImmediate(localize);
        }

        // The game's Settings* components read and write SettingsManager; a cloned row must not keep them.
        private static void StripBinders(GameObject root)
        {
            var gameAssembly = typeof(SettingsUIHelper).Assembly;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetType();
                if (type.Assembly == gameAssembly && type.Name.StartsWith("Settings") && type != typeof(SettingsUIHelper))
                {
                    Object.DestroyImmediate(behaviour);
                }
            }
        }

        // Tab buttons link left/right to each other (set up in the scene); put ours at the end of that chain.
        private static void SpliceIntoTabRow(ThronefallUIElement anyTab, ThronefallUIElement newTab)
        {
            var seen = new HashSet<ThronefallUIElement> { anyTab };
            var last = anyTab;
            while (last.rightNav != null && seen.Add(last.rightNav)) last = last.rightNav;
            // If the chain loops, the last tab points back to the first; keep the loop going through the new tab.
            var head = last.rightNav;
            last.rightNav = newTab;
            newTab.leftNav = last;
            newTab.rightNav = head;
            if (head != null) head.leftNav = newTab;
        }

        // ---- Layout -------------------------------------------------------------------------------------------

        // Positions in the settings screen's 1920x1200 reference space, from its top-left corner. The tabs sit
        // around y 330; the game's own rows start near y 470.
        private const float ListLeft = 140f, ListWidth = 400f, Top = 420f, Bottom = 60f;
        private const float RightLeft = 640f, RightWidth = 1160f, RowWidth = 640f;
        private const float RowDescriptionTop = 1040f;
        private const float Indent = 34f;
        // The "Default" button's right edge, from a row's left edge: past the widest control (the sliders' value).
        private const float ResetRight = 1060f;

        private void BuildLayout()
        {
            // Section list.
            (listViewport, listContent) = ScrollArea("Sections", ListLeft - 30f, ListWidth + 30f, Top, Bottom, 30f);
            var listLayout = listContent.GetComponent<VerticalLayoutGroup>();
            listLayout.childControlWidth = true;
            listLayout.childForceExpandWidth = true;
            listLayout.spacing = 2f;
            listScroll = listViewport.gameObject.AddComponent<ModScroll>();
            listScroll.Init(helper.targetFrame, listViewport, listContent);
            listScroll.WheelNeedsHover = true;

            // The list's scroll bar doubles as the divider between the columns.

            // Page header.
            titleText = Text(root, "Section Title", ModStyle.TitleFont, 46f, ModStyle.Gold);
            Place(titleText.rectTransform, RightLeft + 30f, Top - 12f, RightWidth - 30f, 64f);
            var emblem = ModStyle.AddEmblem(titleText.transform, 16f);
            var follower = emblem.gameObject.AddComponent<EmblemFollower>();
            follower.Text = titleText;
            follower.Gap = 22f;
            descriptionText = Text(root, "Section Description", ModStyle.BodyFont, 27f, new Color(ModStyle.Gold.r, ModStyle.Gold.g, ModStyle.Gold.b, 0.9f));
            descriptionText.enableWordWrapping = true;
            noteText = Text(root, "Section Note", ModStyle.ButtonFont, 25f, WarningColor);
            noteText.enableWordWrapping = true;

            // "Back" to the list, at the right end of the title line while the selection is in a page's settings.
            // Mouse only (Escape and the controller's back button do the same), so it stays out of the up/down chain.
            backButton = SmallButton(root, "PalaceMod Back", "Back", () => BackToList(force: true));
            var backRect = (RectTransform)backButton.transform;
            Place(backRect, RightLeft + RightWidth - 200f, Top - 2f, 160f, 44f);
            backButton.GetComponent<TextMeshProUGUI>().alignment = TextAlignmentOptions.Right;
            backButton.cannotBeSelected = true;
            backButton.gameObject.SetActive(false);

            // Rows; the top is set per page, under its description.
            (rowsViewport, rowsContent) = ScrollArea("Rows", RightLeft, RightWidth, Top + 120f, 1200f - RowDescriptionTop + 14f, 20f);
            var rowsLayout = rowsContent.GetComponent<VerticalLayoutGroup>();
            rowsLayout.childControlWidth = false;
            rowsLayout.childForceExpandWidth = false;
            rowsLayout.spacing = 10f;
            rowsScroll = rowsViewport.gameObject.AddComponent<ModScroll>();
            rowsScroll.Init(helper.targetFrame, rowsViewport, rowsContent);
            rowsScroll.WheelNeedsHover = true;

            // The selected row's description, under a rule like the one under the mod frames' titles.
            var rule = Image("Description Rule", root, new Color(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.6f));
            Place(rule, RightLeft + 20f, RowDescriptionTop, RightWidth - 60f, 1.5f);
            rowDescriptionText = Text(root, "Row Description", ModStyle.ButtonFont, 27f, ModStyle.Gold);
            rowDescriptionText.enableWordWrapping = true;
            Place(rowDescriptionText.rectTransform, RightLeft + 20f, RowDescriptionTop + 14f, RightWidth - 60f, 100f);
        }

        // The game's warning red (its render-scale hint), softened to sit beside the gold text.
        private static readonly Color WarningColor = new(0.86f, 0.42f, 0.36f);

        private (RectTransform viewport, RectTransform content) ScrollArea(string name, float left, float width, float top, float bottom, float padLeft)
        {
            var view = new GameObject($"{name} Viewport", typeof(RectTransform), typeof(RectMask2D));
            view.transform.SetParent(root, false);
            var viewport = (RectTransform)view.transform;
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(0f, 1f);
            viewport.pivot = new Vector2(0f, 1f);
            viewport.offsetMin = new Vector2(left, bottom);
            viewport.offsetMax = new Vector2(left + width, -top);
            var list = new GameObject($"{name} Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            list.transform.SetParent(view.transform, false);
            var content = (RectTransform)list.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(padLeft, 0f);
            content.offsetMax = Vector2.zero;
            var layout = list.GetComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.padding = new RectOffset(0, 0, 6, 30);
            list.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return (viewport, content);
        }

        private static RectTransform Image(string name, Transform parent, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            var image = obj.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return (RectTransform)obj.transform;
        }

        private static void Spacer(Transform parent, float height)
        {
            var obj = new GameObject("Space", typeof(RectTransform), typeof(LayoutElement));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<LayoutElement>().preferredHeight = height;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // A plain text, cloned from the game's settings text (AddComponent<TextMeshProUGUI> on a fresh object fails).
        private TextMeshProUGUI Text(Transform parent, string name, TMP_FontAsset font, float size, Color color)
        {
            var obj = Object.Instantiate(textTemplate.gameObject, parent, false);
            obj.name = $"PalaceMod {name}";
            foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            foreach (var component in obj.GetComponents<Component>())
            {
                if (component is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(component);
            }
            var text = obj.GetComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.text = "";
            obj.SetActive(true);
            return text;
        }

        // A small dim text button (the per-row "Default" and "Back"), cloned from the game's reset entry.
        private TFUITextButton SmallButton(Transform parent, string name, string label, Action onClick)
        {
            var source = buttonTemplate.GetComponent<TFUITextButton>();
            var button = GameWidgets.Button(source, parent, name, label, onClick);
            var obj = button.gameObject; // GameWidgets.Clone already dropped the template's own reset action
            foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            foreach (var c in obj.GetComponents<Component>())
            {
                if (c is ContentSizeFitter or LayoutElement) Object.DestroyImmediate(c);
            }
            var text = obj.GetComponent<TextMeshProUGUI>();
            text.fontSize = 22f;
            text.color = ModStyle.Dim;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            foreach (var style in new[] { button.focussedStyle, button.selectedStyle, button.focussedAndSelectedStyle })
            {
                if (style != null) style.fontSize = 24f;
            }
            Traverse.Create(button).Field("defaultStyleInitialized").SetValue(false);
            button.SetOriginalString(label);
            obj.SetActive(true);
            return button;
        }

        // ---- Pages --------------------------------------------------------------------------------------------

        private List<Page> CollectPages()
        {
            var names = Plugin.Config.Keys.Select(d => d.Section)
                .Concat(SettingsPane.Buttons.Select(b => b.Section))
                .Distinct()
                .Where(s => Entries(s).Any() || SettingsPane.Buttons.Any(b => b.Section == s))
                .ToList();
            var listed = new HashSet<string>(Layout.SelectMany(g => g.Pages).SelectMany(p => p.Sections));
            var result = new List<Page>();
            foreach (var (group, groupPages) in Layout)
            {
                var layout = group == OtherGroup
                    ? names.Where(n => !listed.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => (n, new[] { n })).ToArray()
                    : groupPages;
                foreach (var (name, sections) in layout)
                {
                    var parts = sections.Where(names.Contains).Select(MakePart).ToList();
                    if (parts.Count == 0) continue;
                    string description = parts.Count > 1 && PageDescriptions.TryGetValue(name, out var pd) ? pd : SectionDescription(parts[0].Section, parts[0].Manager);
                    if (name == GameplayFeatures.Section)
                        description += $" \"{GameplayFeatures.Key}\" covers everything that can make a run unranked: {GameplayFeatures.FeatureList()}.";
                    result.Add(new Page { Name = name, Group = group, Description = description, Parts = parts });
                }
            }
            return result;
        }

        private Part MakePart(string section)
        {
            var manager = Plugin.Managers.FirstOrDefault(m => m.FeatureSection == section);
            var owner = manager ?? (Governors.TryGetValue(section, out var governor) ? Plugin.Managers.FirstOrDefault(m => m.FeatureSection == governor) : null);
            return new Part
            {
                Section = section,
                Manager = manager,
                Owner = owner,
                Entries = Entries(section).ToList(),
                Buttons = SettingsPane.Buttons.Where(b => b.Section == section).ToList(),
            };
        }

        private static string SectionDescription(string section, BaseManager manager) =>
            manager?.FeatureDescription
            ?? (SettingsPane.SectionDescriptions.TryGetValue(section, out var d) ? d : null)
            ?? (FallbackDescriptions.TryGetValue(section, out var f) ? f : "");

        private IEnumerable<ConfigEntryBase> Entries(string section) =>
            Plugin.Config.Keys.Where(d => d.Section == section).Select(d => Plugin.Config[d])
                .Where(e => Tags(e)?.Hidden != true && Supported(e))
                .OrderBy(e => e.Definition.Key == "Enabled" ? 0 : 1); // stable, so the rest keep their order

        private static SettingTags Tags(ConfigEntryBase entry) => entry.Description.Tags?.OfType<SettingTags>().FirstOrDefault();

        private static bool Supported(ConfigEntryBase entry) =>
            entry.SettingType == typeof(bool) || entry.SettingType.IsEnum ||
            entry.Description.AcceptableValues is AcceptableValueList<int> or AcceptableValueRange<int> or AcceptableValueRange<float>;

        private static bool IsEnabledEntry(Part part, ConfigEntryBase entry) => part.Manager != null && entry == part.Manager.EnabledEntry;

        // ---- Page list ----------------------------------------------------------------------------------------

        private const float ListScale = 0.85f;

        private void BuildList()
        {
            foreach (Transform child in listContent.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            string group = null;
            foreach (var page in pages)
            {
                if (page.Group != group)
                {
                    group = page.Group;
                    // A group that's just its one namesake page ("General", "Builder") needs no heading.
                    if (pages.Count(s => s.Group == group) == 1 && page.Name == group)
                    {
                        if (page != pages[0]) Spacer(listContent, 22f);
                        page.Item = BuildListItem(page);
                        continue;
                    }
                    var header = Text(listContent, $"Group {group}", ModStyle.BoldFont, 21f, ModStyle.Dim);
                    header.text = group.ToUpperInvariant();
                    header.characterSpacing = 12f;
                    header.alignment = TextAlignmentOptions.BottomLeft;
                    header.gameObject.AddComponent<LayoutElement>().preferredHeight = page == pages[0] ? 30f : 52f;
                }
                page.Item = BuildListItem(page);
            }
            LinkList();
        }

        private TFUITextButton BuildListItem(Page page)
        {
            var obj = Object.Instantiate(toggleTemplate, listContent);
            obj.name = $"PalaceMod Section {page.Name}";
            foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            StripLocalization(obj);
            StripBinders(obj);
            obj.SetActive(true);
            var button = obj.GetComponent<TFUITextButton>();
            button.onApply = new UnityEngine.Events.UnityEvent(); // the template's checkbox toggle
            // Hovering only highlights; clicking (or keyboard/controller selection) shows the page, so moving the
            // mouse across the list toward the settings doesn't switch pages on the way.
            button.autoSelectOnFocus = false;
            // The template row leaves the mouse to its checkbox (destroyed above), so the row itself ignored clicks.
            button.ignoreMouse = false;
            var text = obj.GetComponent<TextMeshProUGUI>();
            text.fontSize *= ListScale;
            text.enableWordWrapping = false;
            // Not Ellipsis: the selection animation overshoots the item's height for a frame, and TMP then drops the
            // whole line, so the label blinked out each time an item was selected.
            text.overflowMode = TextOverflowModes.Overflow;
            text.color = page.Off ? ModStyle.Dim : ModStyle.Gold; // read as the resting style by SetOriginalString
            foreach (var style in new[] { button.focussedStyle, button.selectedStyle, button.focussedAndSelectedStyle })
            {
                if (style != null) style.fontSize *= ListScale;
            }
            // The shown page keeps a bright look while the selection is in its settings, like the current tab.
            var selected = button.selectedStyle;
            button.overrideStyle = new TFUITextButton.Style(ModStyle.Bright, text.fontSize + 2f, selected?.font ?? text.font,
                selected?.animationCurve ?? AnimationCurve.EaseInOut(0f, 0f, 1f, 1f), selected?.animationDuration ?? 0.3f);
            button.SetOriginalString(page.Name);
            obj.AddComponent<LayoutElement>().preferredHeight = 42f;
            // The mod's gem marks the shown page.
            var gem = ModStyle.AddEmblem(obj.transform, 11f);
            gem.gameObject.AddComponent<EmblemFollower>().Text = text;
            gem.gameObject.SetActive(false);
            return button;
        }

        private void LinkList()
        {
            var items = pages.Select(s => s.Item).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                items[i].topNav = i == 0 ? tab.parentElement : items[i - 1];
                items[i].botNav = i == items.Count - 1 ? null : items[i + 1];
            }
        }

        private void MarkCurrent()
        {
            var selection = helper.targetFrame.CurrentSelection;
            for (int i = 0; i < pages.Count; i++)
            {
                var item = pages[i].Item;
                if (item == null) continue;
                bool isCurrent = i == current;
                item.transform.Find("PalaceMod Emblem")?.gameObject.SetActive(isCurrent);
                bool wanted = isCurrent && selection != item;
                if (item.applyOverrideStyle == wanted) continue;
                item.applyOverrideStyle = wanted;
                // Restyle now unless the frame is about to (it clears the previous selection after this event).
                if (selection != item && item.CurrentState == ThronefallUIElement.SelectionState.Default) Restyle(item);
            }
        }

        // Re-runs a resting item's styling (to or from the override style); Clear() is a no-op when already resting.
        private static readonly Action<TFUITextButton> Restyle =
            AccessTools.MethodDelegate<Action<TFUITextButton>>(AccessTools.Method(typeof(TFUITextButton), "OnClear"));

        private static readonly AccessTools.FieldRef<TFUITextButton, TFUITextButton.Style> RestStyle =
            AccessTools.FieldRefAccess<TFUITextButton, TFUITextButton.Style>("defaultStyle");

        // Changes a text button's resting colour (dimmed or not), and shows it now if the button is resting.
        private static void SetRestColor(TFUITextButton button, Color color)
        {
            var style = RestStyle(button);
            if (style == null || style.color == color) return;
            style.color = color;
            if (button.CurrentState == ThronefallUIElement.SelectionState.Default && !button.applyOverrideStyle)
                button.HardStateSet(ThronefallUIElement.SelectionState.Default);
        }

        // ---- Page ---------------------------------------------------------------------------------------------

        private void ShowPage(int index)
        {
            current = Mathf.Clamp(index, 0, pages.Count - 1);
            var page = pages[current];
            confirmingResetAll = false;

            // Don't leave the selection on a row that's about to be destroyed (e.g. a page switched by a command).
            var selected = helper.targetFrame.CurrentSelection;
            if (selected != null && selected.transform.IsChildOf(rowsContent)) helper.targetFrame.Select(page.Item);

            titleText.text = page.Name;
            LayoutHeader(page);
            BuildRows(page);
            MarkCurrent();
            UpdateRowDescription();
        }

        // The description and note under the title; the rows start below them.
        private void LayoutHeader(Page page)
        {
            float y = Top + 58f;
            descriptionText.text = page.Description;
            Place(descriptionText.rectTransform, RightLeft + 30f, y, RightWidth - 60f, 10f);
            float height = string.IsNullOrEmpty(page.Description) ? 0f : descriptionText.GetPreferredValues(page.Description, RightWidth - 60f, 0f).y;
            descriptionText.rectTransform.sizeDelta = new Vector2(RightWidth - 60f, height);
            y += height + 8f;
            string note = Note(page);
            noteText.gameObject.SetActive(note != null);
            if (note != null)
            {
                noteText.text = note;
                float noteHeight = noteText.GetPreferredValues(note, RightWidth - 60f, 0f).y;
                Place(noteText.rectTransform, RightLeft + 30f, y, RightWidth - 60f, noteHeight);
                y += noteHeight + 8f;
            }
            rowsViewport.offsetMax = new Vector2(rowsViewport.offsetMax.x, -(y + 12f));
        }

        // Why the page's settings are off although their own switch is on, if they are.
        private static string Note(Page page)
        {
            if (page.Parts.Any(p => p.HeldOff)) return page.Multi ? "Gameplay-changing features are turned off in General, so the ones here are off for now. Their own settings are kept." : HeldOffNote;
            var governor = page.Parts.Select(p => p.Owner).FirstOrDefault(o => o != null && page.Parts.All(p => p.Manager != o));
            if (governor != null && !governor.IsEnabled) return $"Off for now: {governor.FeatureSection} is turned off.";
            return null;
        }

        private void BuildRows(Page page)
        {
            foreach (Transform child in rowsContent.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            rows.Clear();
            Part previous = null;
            foreach (var part in page.Parts)
            {
                // On a shared page a feature is introduced by its Enabled row (labelled with its name) and its settings
                // are indented under it; a section without a switch (no feature, or a menu-only feature) gets a small
                // heading, unless it follows the feature that switches it (Frame rate under Performance).
                bool indent = page.Multi && (part.HasSwitch || (part.Manager == null && previous?.Manager == part.Owner));
                if (page.Multi && !part.HasSwitch && (part.Owner == null || part.Owner == part.Manager || previous?.Manager != part.Owner))
                {
                    SubHeader(part.Section);
                    indent = false;
                }
                foreach (var entry in part.Entries)
                {
                    bool isEnabled = IsEnabledEntry(part, entry);
                    string label = !isEnabled ? entry.Definition.Key : part.Manager.EnabledLabel ?? (page.Multi ? part.Section : entry.Definition.Key);
                    var row = entry.SettingType == typeof(bool) ? BuildToggle(entry, label)
                        : entry.Description.AcceptableValues is AcceptableValueRange<int> or AcceptableValueRange<float> ? BuildSlider(entry, label)
                        : BuildSelector(entry, label);
                    if (indent && !isEnabled) row.GetComponent<TextMeshProUGUI>().margin = new Vector4(Indent, 0f, 0f, 0f);
                    var info = new Row
                    {
                        Element = row.GetComponent<ThronefallUIElement>(),
                        LeftToList = entry.SettingType == typeof(bool),
                        Entry = entry,
                        Part = part,
                        IsEnabled = isEnabled,
                        Tags = Tags(entry),
                    };
                    info.Description = () => RowDescription(page, info);
                    info.Reset = ResetButton(row, info);
                    rows.Add(info);
                }
                foreach (var button in part.Buttons) rows.Add(BuildButton(button.Label, () => button.Description, button.OnClick, part));
                previous = part;
            }
            AddResetRows(page);

            // Navigation: list -> first row, rows in order; up and down stop at the ends.
            var item = page.Item;
            var elements = rows.Select(r => r.Element).ToList();
            foreach (var s in pages) if (s.Item != null) s.Item.rightNav = elements.FirstOrDefault();
            tab.parentElement.botNav = item;
            tab.parentElement.topNav = null;
            for (int i = 0; i < rows.Count; i++)
            {
                var e = rows[i].Element;
                e.topNav = i == 0 ? null : elements[i - 1];
                e.botNav = i == rows.Count - 1 ? null : elements[i + 1];
                e.leftNav = rows[i].LeftToList ? item : null;
                e.rightNav = null;
                if (rows[i].Reset != null)
                {
                    rows[i].Reset.leftNav = e;
                    rows[i].Reset.topNav = e.topNav;
                    rows[i].Reset.botNav = e.botNav;
                }
            }
            helper.targetFrame.RefetchManagedElements();
            ApplyRowStates();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rowsContent);
            rowsScroll.ResetTo(0f);
        }

        private void SubHeader(string label)
        {
            var header = Text(rowsContent, $"Heading {label}", ModStyle.BoldFont, 20f, ModStyle.Dim);
            header.text = label.ToUpperInvariant();
            header.characterSpacing = 12f;
            header.alignment = TextAlignmentOptions.BottomLeft;
            header.rectTransform.sizeDelta = new Vector2(RowWidth, 40f);
            header.gameObject.AddComponent<LayoutElement>().preferredHeight = rows.Count > 0 ? 50f : 34f;
        }

        // "Reset to defaults" on every page with something to reset; General also resets the whole mod, after a Yes.
        private void AddResetRows(Page page)
        {
            // General's own two settings are covered by resetting everything.
            if (page.Name != GameplayFeatures.Section && (page.Entries.Any() || page.HasKeys))
            {
                rows.Add(BuildButton(ResetPageLabel,
                    () => page.HasKeys ? "Puts every setting and key on this page back to its default." : "Puts every setting on this page back to its default.",
                    () => ResetPage(page), null));
            }
            if (page.Name != GameplayFeatures.Section) return;
            if (!confirmingResetAll)
            {
                rows.Add(BuildButton(ResetAllLabel, () => "Puts every PalaceMod setting and key back to its default, on every page. Asks first.",
                    () => SetConfirming(true), null));
                return;
            }
            const string question = "Reset every PalaceMod setting and key to its default?";
            rows.Add(BuildButton("Yes, reset everything", () => question, () => { ResetAll(); SetConfirming(false); }, null, WarningColor));
            rows.Add(BuildButton("No", () => question, () => SetConfirming(false), null));
        }

        private void SetConfirming(bool confirming)
        {
            confirmingResetAll = confirming;
            var page = pages[current];
            // Rebuilding destroys the selected button; park the selection on the page item meanwhile.
            helper.targetFrame.Select(page.Item);
            BuildRows(page);
            // Land on "No" when asking, back on the reset button after.
            string target = confirming ? "No" : ResetAllLabel;
            var row = rows.FirstOrDefault(r => r.Element.name == $"PalaceMod Button {target}");
            if (row != null) helper.targetFrame.Select(row.Element);
            UpdateRowDescription();
        }

        private string RowDescription(Page page, Row row)
        {
            var entry = row.Entry;
            string text = entry.Description.Description;
            var manager = row.Part.Manager;
            if (row.IsEnabled)
            {
                // A page of its own shows the feature's description at the top already.
                text = page.Multi ? manager.FeatureDescription ?? $"Turns {row.Part.Section} on or off."
                    : $"Turns {row.Part.Section} on or off. Off, the game behaves as it does without it.";
                if (manager.AffectsRanking && !page.Multi) text += " Using it can make a run unranked.";
                if (manager.HeldOffByMasterSwitch && page.Multi) text += " Off for now: \"Gameplay-changing features\" is turned off in General.";
            }
            else if (entry.Definition.Section == GameplayFeatures.Section && entry.Definition.Key == GameplayFeatures.Key)
            {
                text = "Off keeps every run ranked: the features listed above are turned off. Each keeps its own setting, " +
                    "so turning this back on restores them as they were.";
            }
            if (row.Tags?.AffectsRanking == true && !GameplayFeatures.Allowed && !row.Part.HeldOff)
                text += " Off for now: \"Gameplay-changing features\" is turned off in General.";
            if (entry.Description.AcceptableValues is AcceptableValueRange<int> or AcceptableValueRange<float> && !ModNumber.UsingController)
                text += $" <color=#{ModStyle.Hex(ModStyle.Dim)}>Enter or click to type a value; Shift steps x10.</color>";
            return text;
        }

        private GameObject CreateRow(GameObject template, string label)
        {
            var row = Object.Instantiate(template, rowsContent);
            row.name = $"PalaceMod {label}";
            StripLocalization(row);
            StripBinders(row);
            var text = row.GetComponent<TextMeshProUGUI>();
            if (text != null) text.color = ModStyle.Gold;
            var rect = (RectTransform)row.transform;
            rect.sizeDelta = new Vector2(RowWidth, 50f);
            row.AddComponent<LayoutElement>().preferredHeight = 50f;
            row.SetActive(true);
            var button = row.GetComponent<TFUITextButton>() ?? row.GetComponentInChildren<TFUITextButton>(true);
            button?.SetOriginalString(label);
            return row;
        }

        private GameObject BuildToggle(ConfigEntryBase entry, string label)
        {
            var row = CreateRow(toggleTemplate, label);
            var checkbox = row.GetComponentInChildren<Checkbox>(true);
            checkbox.onToggle.AddListener(() => entry.BoxedValue = checkbox.state);
            Bind(row, entry, () => checkbox.SetState((bool)entry.BoxedValue));
            return row;
        }

        private GameObject BuildSelector(ConfigEntryBase entry, string label)
        {
            var row = CreateRow(selectorTemplate, label);
            var selector = row.GetComponentInChildren<EnumSelector>(true);
            var tags = Tags(entry);
            object[] values = entry.SettingType.IsEnum
                ? Enum.GetValues(entry.SettingType).Cast<object>().ToArray()
                : ((AcceptableValueList<int>)entry.Description.AcceptableValues).AcceptableValues.Cast<object>().ToArray();
            selector.delayedApply = false;
            selector.options = values.Select(v => v is Enum ? Words(v.ToString()) : FormatNumber(Convert.ToSingle(v), tags, 0)).ToList();
            selector.onChange.AddListener(() => entry.BoxedValue = values[selector.Index]);
            Bind(row, entry, () => selector.SetIndex(Math.Max(0, Array.IndexOf(values, entry.BoxedValue))));
            return row;
        }

        private GameObject BuildSlider(ConfigEntryBase entry, string label)
        {
            var row = CreateRow(sliderTemplate, label);
            var slider = row.GetComponentInChildren<TFUISlider>(true);
            var tags = Tags(entry);
            bool isInt = entry.SettingType == typeof(int);
            float min, max;
            if (entry.Description.AcceptableValues is AcceptableValueRange<int> ints) (min, max) = (ints.MinValue, ints.MaxValue);
            else
            {
                var floats = (AcceptableValueRange<float>)entry.Description.AcceptableValues;
                (min, max) = (floats.MinValue, floats.MaxValue);
            }
            float step = tags?.Step > 0 ? tags.Step : isInt ? Mathf.Max(1, Mathf.Round((max - min) / 48f)) : (max - min) / 20f;
            slider.minValue = min;
            slider.maxValue = max;
            slider.increments = step;
            slider.displayFPPrecision = isInt ? 0 : step < 0.1f ? 2 : 1;
            slider.customLabelRemapping = false;
            slider.suffix = "";
            // The cloned row carries the game's render-scale hint; the shared description replaces it.
            if (slider.tooltip != null)
            {
                Object.DestroyImmediate(slider.tooltip);
                slider.tooltip = null;
            }
            if (tags != null) SliderLabels.AddOrUpdate(slider, tags);
            slider.onChange.AddListener(() =>
            {
                // The game's slider fires onChange on up/down navigation too, without changing the value; don't
                // re-save (and re-snap) a value the player didn't touch.
                if (Mathf.Approximately(slider.value, Convert.ToSingle(entry.BoxedValue))) return;
                // Keyboard steps add to the current value and clicks snap to multiples of the step from zero, so put
                // the value back on the min + k * step grid.
                float snapped = Mathf.Clamp(min + Mathf.Round((slider.value - min) / step) * step, min, max);
                if (snapped != slider.value) slider.SetValue(snapped);
                entry.BoxedValue = isInt ? Mathf.RoundToInt(snapped) : (object)(float)Math.Round(snapped, 3);
            });
            Bind(row, entry, () => slider.SetValue(Convert.ToSingle(entry.BoxedValue)));

            // Big ranges: holding left/right speeds up and Shift steps x10 (TFUISlider_Navigate_Patch); Enter or a
            // click on the row types a value (any value in range, not just the step grid); Home/End go to the ends.
            ModSliders.AddOrUpdate(slider, new SliderInfo { Key = $"setting:{entry.Definition}", Min = min, Max = max, Step = step, Integer = isInt });
            void Set(float v) => entry.BoxedValue = isInt ? Mathf.RoundToInt(v) : (object)(float)Math.Round(v, 3);
            string Show(float v) => FormatNumber(v, tags, slider.displayFPPrecision);
            var target = slider.target;
            if (target != null)
            {
                target.onApply.AddListener(() =>
                {
                    if (ModNumber.UsingController)
                    {
                        Notify.Show(ModNumber.ControllerHint(shoulders: false), NotifyKind.Info, 3f, key: "modnumber");
                        return;
                    }
                    ModTextInput.Begin(label, () => slider != null && slider.gameObject.activeInHierarchy && slider.display != null,
                        shown =>
                        {
                            if (shown != null) slider.display.text = shown;
                            else slider.SetValue(Convert.ToSingle(entry.BoxedValue));
                        },
                        ModFields.FormatNumber(Convert.ToSingle(entry.BoxedValue)), text =>
                        {
                            if (string.IsNullOrWhiteSpace(text)) return;
                            // The name shown for 0 ("Off", "Display") types as 0.
                            if (!string.IsNullOrEmpty(tags?.ZeroLabel) && string.Equals(text.Trim(), tags.ZeroLabel, StringComparison.OrdinalIgnoreCase) && min <= 0f && max >= 0f)
                            {
                                Set(0f);
                                return;
                            }
                            bool ok = ModNumber.TryRead(text, min, max, isInt, Show, out float value, out string message);
                            if (message != null) Notify.Show(message, NotifyKind.Warning, 3f, key: "modnumber");
                            if (ok) Set(value);
                        }, 24, hint: ModNumber.TypingHint(min, max));
                });
                var keys = target.gameObject.AddComponent<NumberKeys>();
                keys.Selected = () => target.CurrentState is ThronefallUIElement.SelectionState.Selected or ThronefallUIElement.SelectionState.FocussedAndSelected;
                keys.Jump = toMax => Set(toMax ? max : min);
            }
            return row;
        }

        private sealed class SliderInfo
        {
            public string Key;
            public float Min, Max, Step;
            public bool Integer;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TFUISlider, SliderInfo> ModSliders = new();

        // A held direction on a mod slider moves further each repeat, and Shift moves x10 (as ModNumber fields do).
        [HarmonyPatch(typeof(TFUISlider), nameof(TFUISlider.Navigate))]
        private class TFUISlider_Navigate_Patch
        {
            private static void Prefix(TFUISlider __instance, ThronefallUIElement.NavigationDirection direction, out float __state)
            {
                __state = __instance.increments;
                if (!ModSliders.TryGetValue(__instance, out var info)) return;
                int sign = direction == __instance.increase ? 1 : direction == __instance.decrease ? -1 : 0;
                if (sign == 0) return;
                int speed = NumberMath.Acceleration(NumberRepeat.Next(info.Key, sign), info.Min, info.Max, info.Step);
                // Navigate adds or subtracts increments: aim it at the grid point that many steps away (a typed 144
                // with step 5 goes to 145, not 150).
                float target = (float)NumberMath.Step(__instance.value, info.Step, sign * speed * (ModNumber.ShiftHeld ? 10 : 1), info.Min, info.Max, info.Integer);
                __instance.increments = Mathf.Abs(target - __instance.value);
            }

            private static void Postfix(TFUISlider __instance, float __state) => __instance.increments = __state;
        }

        private Row BuildButton(string label, Func<string> description, Action onClick, Part part, Color? color = null)
        {
            var row = Object.Instantiate(buttonTemplate, rowsContent);
            row.name = $"PalaceMod Button {label}";
            StripLocalization(row);
            StripBinders(row);
            var button = row.GetComponent<TFUITextButton>();
            button.onApply = new UnityEngine.Events.UnityEvent(); // the template's reset action
            var text = row.GetComponent<TextMeshProUGUI>();
            // The game draws its reset entries dim; an action the player came here for reads as available.
            text.color = color ?? ModStyle.Gold;
            var rect = (RectTransform)row.transform;
            rect.sizeDelta = new Vector2(RowWidth, 50f);
            // A little space above, like the game's buttons under its settings.
            row.AddComponent<LayoutElement>().preferredHeight = 76f;
            text.alignment = TextAlignmentOptions.BottomLeft;
            row.SetActive(true);
            button.SetOriginalString(label);
            button.onApply.AddListener(() =>
            {
                try { onClick?.Invoke(); }
                catch (Exception e) { Log.Caught($"Settings button \"{label}\" failed", e); }
            });
            return new Row { Element = button, Description = description, LeftToList = true, Part = part };
        }

        // ---- Row states: dimming and per-row Default ------------------------------------------------------------

        private TFUITextButton ResetButton(GameObject row, Row info)
        {
            var entry = info.Entry;
            var button = SmallButton(row.transform, "PalaceMod Default", "Default", () =>
            {
                // Keep the selection on the row (the button hides once the value is back to its default).
                var frame = helper.targetFrame;
                if (frame.CurrentSelection == info.Reset) frame.Select(info.Element);
                entry.BoxedValue = entry.DefaultValue;
            });
            var rect = (RectTransform)button.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(ResetRight, 0f);
            rect.sizeDelta = new Vector2(140f, 40f);
            button.GetComponent<TextMeshProUGUI>().alignment = TextAlignmentOptions.Right;
            button.gameObject.SetActive(false);
            return button;
        }

        private static bool IsDefault(ConfigEntryBase entry)
        {
            object value = entry.BoxedValue, fallback = entry.DefaultValue;
            if (value is float f && fallback is float d) return Mathf.Approximately(f, d);
            return Equals(value, fallback);
        }

        // Dims what's switched off and shows each row's "Default" button while its value differs from the default.
        private void ApplyRowStates()
        {
            var frame = helper.targetFrame;
            foreach (var row in rows)
            {
                if (row.Element == null || row.Entry == null) continue; // action buttons keep their own colour
                bool dim = !row.IsEnabled
                    && (row.Part.Off || (row.Tags?.AffectsRanking == true && !GameplayFeatures.Allowed));
                if (row.Element is TFUITextButton label) SetRestColor(label, dim ? ModStyle.Dim : ModStyle.Gold);
                // The control beside the label (checkbox, selector, slider) fades with it.
                foreach (Transform child in row.Element.transform)
                {
                    if (child.name == "PalaceMod Default") continue;
                    var group = child.GetComponent<CanvasGroup>() ?? child.gameObject.AddComponent<CanvasGroup>();
                    group.alpha = dim ? 0.45f : 1f;
                }
                if (row.Reset == null) continue;
                bool show = !IsDefault(row.Entry);
                if (row.Entry.SettingType == typeof(bool)) row.Element.rightNav = show ? row.Reset : null;
                if (row.Reset.gameObject.activeSelf == show) continue;
                if (!show && frame.CurrentSelection == row.Reset) frame.Select(row.Element);
                row.Reset.gameObject.SetActive(show);
            }
            foreach (var page in pages)
            {
                if (page.Item != null) SetRestColor(page.Item, page.Off ? ModStyle.Dim : ModStyle.Gold);
            }
        }

        // A setting or the master switch changed (here, in the config file, or from a reset): restyle next frame,
        // after the clicked checkbox has finished its own handler.
        private bool refreshPending;

        private void OnStateChanged() => refreshPending = true;

        private void OnSettingChanged(object sender, SettingChangedEventArgs e) => refreshPending = true;

        private void RefreshStates()
        {
            if (root == null || helper == null || pages.Count == 0) return;
            try
            {
                LayoutHeader(pages[current]);
                ApplyRowStates();
                UpdateRowDescription();
            }
            catch (Exception e)
            {
                Log.Error("Couldn't update the settings page after a change", e);
            }
        }

        // ---- Resetting ----------------------------------------------------------------------------------------

        private void ResetPage(Page page)
        {
            int changed = ResetEntries(page.Entries);
            var sections = new HashSet<string>(page.Parts.Select(p => p.Section));
            int keys = KeybindsManager.ResetKeybinds(k => sections.Contains(k.Section));
            Log.Info($"Reset \"{page.Name}\" to defaults: {changed} setting(s), {keys} key(s)");
            Notify.Show(changed + keys > 0 ? $"{page.Name}: back to defaults" : $"{page.Name} is already at its defaults", NotifyKind.Success, 3f, "settings-reset");
        }

        private void ResetAll()
        {
            int changed = ResetEntries(pages.SelectMany(p => p.Entries));
            int keys = KeybindsManager.ResetKeybinds(_ => true);
            Log.Info($"Reset all PalaceMod settings: {changed} setting(s), {keys} key(s)");
            Notify.Show("All PalaceMod settings are back to their defaults", NotifyKind.Success, 4f, "settings-reset");
        }

        // The master switch goes last: switching it on brings features back with their final values.
        private static int ResetEntries(IEnumerable<ConfigEntryBase> entries)
        {
            int changed = 0;
            foreach (var entry in entries.OrderBy(e => e == GameplayFeatures.Entry ? 1 : 0))
            {
                if (IsDefault(entry)) continue;
                entry.BoxedValue = entry.DefaultValue;
                changed++;
            }
            return changed;
        }

        // Sliders whose zero has a name ("Off", "Display"); the game's slider would just print the number.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TFUISlider, SettingTags> SliderLabels = new();

        [HarmonyPatch(typeof(TFUISlider), "UpdateDisplay")]
        private class TFUISlider_UpdateDisplay_Patch
        {
            private static void Postfix(TFUISlider __instance)
            {
                if (__instance.display == null || !SliderLabels.TryGetValue(__instance, out var tags)) return;
                __instance.display.text = FormatNumber(__instance.value, tags, __instance.displayFPPrecision);
            }
        }

        // Shows the entry's current value whenever the row becomes visible or the setting changes elsewhere.
        private static void Bind(GameObject row, ConfigEntryBase entry, Action refresh)
        {
            refresh();
            // AddComponent runs OnEnable immediately on a visible row, before fields could be set, so Init subscribes.
            row.AddComponent<RowBinding>().Init(entry, refresh);
        }

        private class RowBinding : MonoBehaviour
        {
            public Action Refresh;
            public ConfigEntryBase Entry;
            private bool subscribed;

            public void Init(ConfigEntryBase entry, Action refresh)
            {
                Entry = entry;
                Refresh = refresh;
                if (isActiveAndEnabled) Subscribe();
            }

            private void OnEnable()
            {
                Refresh?.Invoke();
                Subscribe();
            }

            private void OnDisable()
            {
                if (subscribed) Entry.ConfigFile.SettingChanged -= OnSettingChanged;
                subscribed = false;
            }

            private void OnDestroy() => OnDisable();

            private void Subscribe()
            {
                if (subscribed || Entry == null) return;
                Entry.ConfigFile.SettingChanged += OnSettingChanged;
                subscribed = true;
            }

            private void OnSettingChanged(object sender, SettingChangedEventArgs e)
            {
                if (e.ChangedSetting == Entry) Refresh?.Invoke();
            }
        }

        // ---- Selection ----------------------------------------------------------------------------------------

        private void OnSelectionChanged()
        {
            if (root == null) return;
            var selected = helper.targetFrame.CurrentSelection;
            int index = pages.FindIndex(s => s.Item == selected);
            if (index >= 0 && index != current) ShowPage(index);
            else MarkCurrent();
            UpdateRowDescription();
        }

        private bool InPage(ThronefallUIElement element) => element != null && rowsContent != null && element.transform.IsChildOf(rowsContent);

        /// <summary>From a page's settings back to its entry in the list; false when the selection isn't in a page
        /// (Escape then closes Settings as usual).</summary>
        private bool BackToList(bool force = false)
        {
            if (root == null || helper == null || !root.gameObject.activeInHierarchy) return false;
            var frame = helper.targetFrame;
            if (UIFrameManager.instance?.ActiveFrame != frame) return false;
            if (!force && !InPage(frame.CurrentSelection)) return false;
            if (confirmingResetAll) SetConfirming(false);
            frame.Select(pages[current].Item);
            return true;
        }

        private void UpdateBackButton()
        {
            if (backButton == null) return;
            bool show = root.gameObject.activeInHierarchy && InPage(helper.targetFrame.CurrentSelection);
            if (backButton.gameObject.activeSelf != show) backButton.gameObject.SetActive(show);
        }

        // The description of the hovered row, else of the selected one.
        private void UpdateRowDescription()
        {
            if (rowDescriptionText == null) return;
            var frame = helper.targetFrame;
            var row = FindRow(frame.CurrentFocus) ?? FindRow(frame.CurrentSelection);
            if (row != null)
            {
                bool onReset = row.Reset != null && (frame.CurrentFocus == row.Reset || frame.CurrentSelection == row.Reset);
                rowDescriptionText.text = onReset ? $"Back to the default: {DefaultText(row.Entry)}." : row.Description?.Invoke() ?? "";
                rowDescriptionText.color = new Color(ModStyle.Gold.r, ModStyle.Gold.g, ModStyle.Gold.b, 0.85f);
                return;
            }
            // On the page list: how to get into the settings, quietly.
            bool onList = pages.Any(s => s.Item != null && s.Item == frame.CurrentSelection);
            rowDescriptionText.text = onList && rows.Count > 0 ? "Move right, or click a setting, to change it. Esc comes back here." : "";
            rowDescriptionText.color = ModStyle.Dim;
        }

        private static string DefaultText(ConfigEntryBase entry) => entry.DefaultValue switch
        {
            bool b => b ? "on" : "off",
            Enum e => Words(e.ToString()),
            float f => FormatNumber(f, Tags(entry), 2),
            int i => FormatNumber(i, Tags(entry), 0),
            var v => v?.ToString() ?? "",
        };

        private Row FindRow(ThronefallUIElement element)
        {
            if (element == null) return null;
            foreach (var row in rows)
            {
                if (row.Element != null && element.transform.IsChildOf(row.Element.transform)) return row;
            }
            return null;
        }

        // ---- Formatting ---------------------------------------------------------------------------------------

        private static string FormatNumber(float value, SettingTags tags, int decimals)
        {
            if (value == 0 && !string.IsNullOrEmpty(tags?.ZeroLabel)) return tags.ZeroLabel;
            return value.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals)) + (tags?.Unit ?? "");
        }

        // "GameDefault" -> "Game default"
        private static string Words(string identifier)
        {
            var text = new StringBuilder();
            for (int i = 0; i < identifier.Length; i++)
            {
                char c = identifier[i];
                if (i > 0 && char.IsUpper(c))
                {
                    text.Append(' ');
                    c = char.ToLowerInvariant(c);
                }
                text.Append(c);
            }
            return text.ToString();
        }

        // ---- Dev command --------------------------------------------------------------------------------------

        private int burstFrames, burstIndex;
        private string burstDir;

        // Dev: captures consecutive frames while moving the selection down one step (checks for flicker).
        private void Burst()
        {
            if (burstIndex == 1 && helper?.targetFrame?.CurrentSelection?.botNav is { } next) helper.targetFrame.Select(next);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(burstDir, $"frame_{burstIndex:00}.png"));
            burstIndex++;
            burstFrames--;
        }

        private void RegisterCommands()
        {
            DevCommands.Register("modsettings",
                "modsettings [list | <page> [row <label>] | state | probe | frames <n> <dir>]   open Settings > Mods on a page (and select a row); " +
                "state: pages and rows with dim/default flags; probe: what the mouse is over; frames: capture n frames while stepping down",
                args =>
                {
                    if (args.Length == 0 || args[0] == "list")
                        return string.Join(", ", pages.Select((s, i) => $"{i}:{s.Name}{(s.Off ? " (off)" : "")}"));
                    var frame = Frame ?? throw new InvalidOperationException("settings screen not found");
                    switch (args[0])
                    {
                        case "state":
                            return $"page {pages[current].Name}; selection {frame.CurrentSelection?.name}; back {(backButton != null && backButton.gameObject.activeSelf ? "shown" : "hidden")}\n" +
                                string.Join("\n", rows.Select(r =>
                                {
                                    var label = r.Element as TFUITextButton;
                                    string dim = label != null && RestStyle(label)?.color == ModStyle.Dim ? " dim" : "";
                                    string reset = r.Reset != null && r.Reset.gameObject.activeSelf ? " [Default]" : "";
                                    return $"  {r.Element.name}{dim}{reset}" + (r.Entry != null ? $" = {r.Entry.BoxedValue}" : "");
                                })) + "\nlist: " + string.Join(", ", pages.Where(p => p.Off).Select(p => p.Name)) + " dimmed";
                        case "probe":
                            var raycaster = frame.GetComponentInParent<GraphicRaycaster>();
                            var data = new UnityEngine.EventSystems.PointerEventData(null) { position = Plugin.Input.controllers.Mouse.screenPosition };
                            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                            raycaster.Raycast(data, hits);
                            return $"mouse {data.position} selection={frame.CurrentSelection?.name} focus={frame.CurrentFocus?.name} cursor={Cursor.visible}\n" +
                                string.Join("\n", hits.Select(h => $"  {h.gameObject.name} -> {h.gameObject.GetComponentInParent<ThronefallUIElement>()?.name}"));
                        case "frames":
                            burstFrames = int.Parse(args[1]);
                            burstDir = args[2];
                            burstIndex = 0;
                            return $"capturing {burstFrames} frames to {burstDir}";
                    }
                    if (UIFrameManager.instance.ActiveFrame != frame) UIFrameManager.instance.ChangeActiveFrame(frame);
                    if (tab == null) throw new InvalidOperationException("Mods tab not built");
                    int rowAt = Array.FindIndex(args, a => a == "row");
                    string name = string.Join(" ", rowAt < 0 ? args : args.Take(rowAt));
                    int index = int.TryParse(name, out int n) ? n : pages.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) index = pages.FindIndex(p => p.Parts.Any(part => part.Section.Equals(name, StringComparison.OrdinalIgnoreCase)));
                    if (index < 0 || index >= pages.Count) throw new ArgumentException($"no page \"{name}\"");
                    frame.Select(tab.parentElement);
                    frame.Select(pages[index].Item);
                    if (rowAt >= 0)
                    {
                        string label = string.Join(" ", args.Skip(rowAt + 1));
                        var row = rows.FirstOrDefault(r => r.Element.name.EndsWith(label, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"no row \"{label}\"");
                        frame.Select(row.Element);
                    }
                    return $"{pages[index].Name}: {rows.Count} rows; description: {rowDescriptionText?.text}";
                });
        }
    }
}
