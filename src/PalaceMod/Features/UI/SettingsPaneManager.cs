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

    Layout: a scrolling list of sections on the left, grouped by area (General first, Performance last), and the
    selected section on the right: its name with the mod's gem, its description (the feature's FeatureDescription or
    SettingsPane.DescribeSection), a note when the "Gameplay-changing features" master switch holds it off, its rows
    (scrolling if they don't fit), and under a rule the description of the selected or hovered row.

    Navigation: moving through the list with the keyboard or a controller shows each section as it's selected; right
    (or clicking a row) goes into its settings, left from a checkbox or button and up from the first row go back to the
    list. With the mouse, hovering a section highlights it and clicking shows it; the wheel scrolls whichever column the
    pointer is over.

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

        // Player-facing areas for the section list, in order. Sections not listed go under "Other".
        private static readonly (string Group, string[] Sections)[] Groups =
        [
            ("General", [GameplayFeatures.Section]),
            ("Units", ["Formations", "Patrols", "Control groups", "Unit ranges", "Teleporters", "Minigun"]),
            ("Buildings & gold", ["Build previews", "Income labels", "Income display", "Gold counter fix", "Upgrade info", "Undo upgrade", "Level 4 buildings"]),
            ("Information", ["Pause menu score", "Enemy previews", "Compendium", "Stat details", "Run analytics"]),
            ("Runs & saves", ["Savestates", "Retry earlier day", "Bonus-safe saves", "Unranked runs", "Loadout randomizer", "Profiles", "Debug menu"]),
            ("Builder", ["Builder"]),
            (OtherGroup, []),
            ("Mod", ["Fixes", "Version badge", "Diagnostics"]),
            ("Performance", ["Performance", "Frame rate", "Battery", "Optimizations", "Memory", "Advanced"]),
        ];

        // Groups listed in a fixed order rather than alphabetically.
        private static readonly HashSet<string> FixedOrderGroups = ["Performance"];

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

        private SettingsUIHelper helper;
        private bool injected;
        private SettingsUIHelper.SettingsTab tab;
        private GameObject toggleTemplate, selectorTemplate, sliderTemplate, buttonTemplate;
        private TextMeshProUGUI textTemplate;

        private sealed class Section
        {
            public string Name, Group, Description;
            public BaseManager Manager;
            public List<ConfigEntryBase> Entries;
            public List<SettingsPane.ActionButton> Buttons;
            public TFUITextButton Item;
            public bool HeldOff => Manager != null && Manager.HeldOffByMasterSwitch;
        }

        private sealed class Row
        {
            public ThronefallUIElement Element;
            public Func<string> Description;
            public bool LeftToList; // checkbox and button rows; selectors and sliders use left/right for their value
        }

        private List<Section> sections = [];
        private int current;
        private readonly List<Row> rows = [];

        // Built once per settings screen.
        private RectTransform root, listViewport, listContent, rowsViewport, rowsContent;
        private ModScroll listScroll, rowsScroll;
        private TextMeshProUGUI titleText, descriptionText, noteText, rowDescriptionText;

        public TFUITextButton TabButton => tab?.parentElement;
        public UIFrame Frame => helper?.targetFrame ?? FindHelper()?.targetFrame;

        private static SettingsUIHelper FindHelper() =>
            Resources.FindObjectsOfTypeAll<SettingsUIHelper>().FirstOrDefault(h => h.gameObject.scene.IsValid());

        [HarmonyPatch(typeof(SettingsUIHelper), "Awake")]
        private class SettingsUIHelper_Awake_Patch
        {
            private static void Postfix(SettingsUIHelper __instance) => Feature<SettingsPaneManager>.Instance?.TryInject(__instance);
        }

        private const int MaxFallbackSearches = 30;
        private int fallbackSearches;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            GameplayFeatures.Changed += OnMasterSwitchChanged;
            RegisterCommands();
        }

        public override void Update()
        {
            if (redrawPending)
            {
                redrawPending = false;
                Redraw();
            }
            // If the settings screen is ever rebuilt (its scene reloaded), add the tab to the new one.
            if (injected && helper == null)
            {
                injected = false;
                tab = null;
            }
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
                Log.Debug($"Added \"{TabLabel}\" tab with {sections.Count} sections");
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

            sections = CollectSections();
            if (sections.Count == 0) throw new InvalidOperationException("no settings to show");
            // Our own navigation; the game's per-tab computation assumes one column.
            tab = new SettingsUIHelper.SettingsTab
            {
                parentElement = tabButton,
                contentParent = page,
                childContainer = rowsContent.GetComponent<VerticalLayoutGroup>(),
                disableNavigationProcessing = true,
            };
            BuildList();
            ShowSection(Mathf.Clamp(current, 0, sections.Count - 1));

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

            // Section header.
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

            // Rows; the top is set per section, under its description.
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

        // ---- Sections -----------------------------------------------------------------------------------------

        private List<Section> CollectSections()
        {
            var names = Plugin.Config.Keys.Select(d => d.Section)
                .Concat(SettingsPane.Buttons.Select(b => b.Section))
                .Distinct()
                .Where(s => Entries(s).Any() || SettingsPane.Buttons.Any(b => b.Section == s))
                .ToList();
            string GroupOf(string section) => Groups.FirstOrDefault(g => g.Sections.Contains(section)).Group ?? OtherGroup;
            var result = new List<Section>();
            foreach (var (group, members) in Groups)
            {
                var inGroup = names.Where(n => GroupOf(n) == group);
                inGroup = FixedOrderGroups.Contains(group)
                    ? inGroup.OrderBy(n => Array.IndexOf(members, n))
                    : inGroup.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                foreach (var name in inGroup)
                {
                    var manager = Plugin.Managers.FirstOrDefault(m => m.FeatureSection == name);
                    string description = manager?.FeatureDescription
                        ?? (SettingsPane.SectionDescriptions.TryGetValue(name, out var d) ? d : null)
                        ?? (FallbackDescriptions.TryGetValue(name, out var f) ? f : "");
                    result.Add(new Section
                    {
                        Name = name,
                        Group = group,
                        Description = description,
                        Manager = manager,
                        Entries = Entries(name).ToList(),
                        Buttons = SettingsPane.Buttons.Where(b => b.Section == name).ToList(),
                    });
                }
            }
            return result;
        }

        private IEnumerable<ConfigEntryBase> Entries(string section) =>
            Plugin.Config.Keys.Where(d => d.Section == section).Select(d => Plugin.Config[d])
                .Where(e => Tags(e)?.Hidden != true && Supported(e))
                .OrderBy(e => e.Definition.Key == "Enabled" ? 0 : 1); // stable, so the rest keep their order

        private static SettingTags Tags(ConfigEntryBase entry) => entry.Description.Tags?.OfType<SettingTags>().FirstOrDefault();

        private static bool Supported(ConfigEntryBase entry) =>
            entry.SettingType == typeof(bool) || entry.SettingType.IsEnum ||
            entry.Description.AcceptableValues is AcceptableValueList<int> or AcceptableValueRange<int> or AcceptableValueRange<float>;

        // ---- Section list -------------------------------------------------------------------------------------

        private const float ListScale = 0.85f;

        private void BuildList()
        {
            foreach (Transform child in listContent.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            string group = null;
            foreach (var section in sections)
            {
                if (section.Group != group)
                {
                    group = section.Group;
                    // A group that's just its one namesake section ("General", "Builder") needs no heading.
                    if (sections.Count(s => s.Group == group) == 1 && section.Name == group)
                    {
                        if (section != sections[0]) Spacer(listContent, 22f);
                        section.Item = BuildListItem(section);
                        continue;
                    }
                    var header = Text(listContent, $"Group {group}", ModStyle.BoldFont, 21f, ModStyle.Dim);
                    header.text = group.ToUpperInvariant();
                    header.characterSpacing = 12f;
                    header.alignment = TextAlignmentOptions.BottomLeft;
                    header.gameObject.AddComponent<LayoutElement>().preferredHeight = section == sections[0] ? 30f : 52f;
                }
                section.Item = BuildListItem(section);
            }
            LinkList();
        }

        private TFUITextButton BuildListItem(Section section)
        {
            var obj = Object.Instantiate(toggleTemplate, listContent);
            obj.name = $"PalaceMod Section {section.Name}";
            foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            StripLocalization(obj);
            StripBinders(obj);
            obj.SetActive(true);
            var button = obj.GetComponent<TFUITextButton>();
            button.onApply = new UnityEngine.Events.UnityEvent(); // the template's checkbox toggle
            // Hovering only highlights; clicking (or keyboard/controller selection) shows the section, so moving the
            // mouse across the list toward the settings doesn't switch sections on the way.
            button.autoSelectOnFocus = false;
            var text = obj.GetComponent<TextMeshProUGUI>();
            text.fontSize *= ListScale;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            if (section.HeldOff) text.color = ModStyle.Dim;
            foreach (var style in new[] { button.focussedStyle, button.selectedStyle, button.focussedAndSelectedStyle })
            {
                if (style != null) style.fontSize *= ListScale;
            }
            // The shown section keeps a bright look while the selection is in its settings, like the current tab.
            var selected = button.selectedStyle;
            button.overrideStyle = new TFUITextButton.Style(ModStyle.Bright, text.fontSize + 2f, selected?.font ?? text.font,
                selected?.animationCurve ?? AnimationCurve.EaseInOut(0f, 0f, 1f, 1f), selected?.animationDuration ?? 0.3f);
            button.SetOriginalString(section.Name);
            obj.AddComponent<LayoutElement>().preferredHeight = 42f;
            // The mod's gem marks the shown section.
            var gem = ModStyle.AddEmblem(obj.transform, 11f);
            gem.gameObject.AddComponent<EmblemFollower>().Text = text;
            gem.gameObject.SetActive(false);
            return button;
        }

        private void LinkList()
        {
            var items = sections.Select(s => s.Item).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                items[i].topNav = i == 0 ? tab.parentElement : items[i - 1];
                items[i].botNav = i == items.Count - 1 ? tab.parentElement : items[i + 1];
            }
        }

        private void MarkCurrent()
        {
            var selection = helper.targetFrame.CurrentSelection;
            for (int i = 0; i < sections.Count; i++)
            {
                var item = sections[i].Item;
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

        // ---- Section page -------------------------------------------------------------------------------------

        private void ShowSection(int index)
        {
            current = Mathf.Clamp(index, 0, sections.Count - 1);
            var section = sections[current];

            // Don't leave the selection on a row that's about to be destroyed (e.g. a section switched by a command).
            var selected = helper.targetFrame.CurrentSelection;
            if (selected != null && selected.transform.IsChildOf(rowsContent)) helper.targetFrame.Select(section.Item);

            titleText.text = section.Name;
            float y = Top + 58f;
            descriptionText.text = section.Description;
            Place(descriptionText.rectTransform, RightLeft + 30f, y, RightWidth - 60f, 10f);
            float height = string.IsNullOrEmpty(section.Description) ? 0f : descriptionText.GetPreferredValues(section.Description, RightWidth - 60f, 0f).y;
            descriptionText.rectTransform.sizeDelta = new Vector2(RightWidth - 60f, height);
            y += height + 8f;
            bool heldOff = section.HeldOff;
            noteText.gameObject.SetActive(heldOff);
            if (heldOff)
            {
                noteText.text = HeldOffNote;
                float noteHeight = noteText.GetPreferredValues(HeldOffNote, RightWidth - 60f, 0f).y;
                Place(noteText.rectTransform, RightLeft + 30f, y, RightWidth - 60f, noteHeight);
                y += noteHeight + 8f;
            }
            rowsViewport.offsetMax = new Vector2(rowsViewport.offsetMax.x, -(y + 12f));

            BuildRows(section);
            MarkCurrent();
            UpdateRowDescription();
        }

        private void BuildRows(Section section)
        {
            foreach (Transform child in rowsContent.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            rows.Clear();
            foreach (var entry in section.Entries)
            {
                var tags = Tags(entry);
                bool rowHeldOff = section.HeldOff || (tags?.AffectsRanking == true && !GameplayFeatures.Allowed);
                string label = entry.Definition.Key;
                var row = entry.SettingType == typeof(bool) ? BuildToggle(entry, label, rowHeldOff)
                    : entry.Description.AcceptableValues is AcceptableValueRange<int> or AcceptableValueRange<float> ? BuildSlider(entry, label, rowHeldOff)
                    : BuildSelector(entry, label, rowHeldOff);
                var info = new Row
                {
                    Element = row.GetComponent<ThronefallUIElement>(),
                    LeftToList = entry.SettingType == typeof(bool),
                };
                info.Description = () => RowDescription(section, entry, tags);
                rows.Add(info);
            }
            foreach (var button in section.Buttons) rows.Add(BuildButton(button));

            // Navigation: list <-> first row, rows in order, the last back up to the tabs like the game's pages.
            var item = section.Item;
            var elements = rows.Select(r => r.Element).ToList();
            foreach (var s in sections) if (s.Item != null) s.Item.rightNav = elements.FirstOrDefault();
            tab.parentElement.botNav = item;
            tab.parentElement.topNav = elements.LastOrDefault() ?? item;
            for (int i = 0; i < rows.Count; i++)
            {
                var e = rows[i].Element;
                e.topNav = i == 0 ? item : elements[i - 1];
                e.botNav = i == rows.Count - 1 ? tab.parentElement : elements[i + 1];
                e.leftNav = rows[i].LeftToList ? item : null;
                e.rightNav = null;
            }
            helper.targetFrame.RefetchManagedElements();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rowsContent);
            rowsScroll.ResetTo(0f);
        }

        private string RowDescription(Section section, ConfigEntryBase entry, SettingTags tags)
        {
            string text = entry.Description.Description;
            if (entry.Definition.Key == "Enabled" && section.Manager != null)
            {
                // The section's description is already at the top of the page.
                text = $"Turns {section.Name} on or off. Off, the game behaves as it does without it.";
                if (section.Manager.AffectsRanking) text += " Using it can make a run unranked.";
            }
            else if (entry.Definition.Section == GameplayFeatures.Section && entry.Definition.Key == GameplayFeatures.Key)
            {
                text = $"On: every PalaceMod feature is available as you set it up. Off: the features that can make a run " +
                    $"unranked are turned off, so every run stays ranked ({GameplayFeatures.FeatureList()}). Each keeps its own " +
                    "setting for when you turn this back on.";
            }
            if (tags?.AffectsRanking == true && !GameplayFeatures.Allowed && !section.HeldOff)
                text += " Off for now: \"Gameplay-changing features\" is turned off in General.";
            return text;
        }

        private GameObject CreateRow(GameObject template, string label, bool dim)
        {
            var row = Object.Instantiate(template, rowsContent);
            row.name = $"PalaceMod {label}";
            StripLocalization(row);
            StripBinders(row);
            var text = row.GetComponent<TextMeshProUGUI>();
            // The row's resting style is read from its text the first time it's styled, so dim it before that.
            if (dim && text != null) text.color = ModStyle.Dim;
            var rect = (RectTransform)row.transform;
            rect.sizeDelta = new Vector2(RowWidth, 50f);
            row.AddComponent<LayoutElement>().preferredHeight = 50f;
            row.SetActive(true);
            var button = row.GetComponent<TFUITextButton>() ?? row.GetComponentInChildren<TFUITextButton>(true);
            button?.SetOriginalString(label);
            return row;
        }

        private GameObject BuildToggle(ConfigEntryBase entry, string label, bool dim)
        {
            var row = CreateRow(toggleTemplate, label, dim);
            var checkbox = row.GetComponentInChildren<Checkbox>(true);
            checkbox.onToggle.AddListener(() => entry.BoxedValue = checkbox.state);
            Bind(row, entry, () => checkbox.SetState((bool)entry.BoxedValue));
            return row;
        }

        private GameObject BuildSelector(ConfigEntryBase entry, string label, bool dim)
        {
            var row = CreateRow(selectorTemplate, label, dim);
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

        private GameObject BuildSlider(ConfigEntryBase entry, string label, bool dim)
        {
            var row = CreateRow(sliderTemplate, label, dim);
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
            return row;
        }

        private Row BuildButton(SettingsPane.ActionButton action)
        {
            var row = Object.Instantiate(buttonTemplate, rowsContent);
            row.name = $"PalaceMod Button {action.Label}";
            StripLocalization(row);
            StripBinders(row);
            var button = row.GetComponent<TFUITextButton>();
            button.onApply = new UnityEngine.Events.UnityEvent(); // the template's reset action
            var text = row.GetComponent<TextMeshProUGUI>();
            // The game draws its reset entries dim; an action the player came here for reads as available.
            text.color = ModStyle.Gold;
            var rect = (RectTransform)row.transform;
            rect.sizeDelta = new Vector2(RowWidth, 50f);
            // A little space above, like the game's buttons under its settings.
            row.AddComponent<LayoutElement>().preferredHeight = 76f;
            text.alignment = TextAlignmentOptions.BottomLeft;
            row.SetActive(true);
            button.SetOriginalString(action.Label);
            button.onApply.AddListener(() =>
            {
                try { action.OnClick?.Invoke(); }
                catch (Exception e) { Log.Caught($"Settings button \"{action.Label}\" failed", e); }
            });
            return new Row { Element = button, Description = () => action.Description, LeftToList = true };
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
            int index = sections.FindIndex(s => s.Item == selected);
            if (index >= 0 && index != current) ShowSection(index);
            else MarkCurrent();
            UpdateRowDescription();
        }

        // The description of the hovered row, else of the selected one.
        private void UpdateRowDescription()
        {
            if (rowDescriptionText == null) return;
            var frame = helper.targetFrame;
            var row = FindRow(frame.CurrentFocus) ?? FindRow(frame.CurrentSelection);
            if (row != null)
            {
                rowDescriptionText.text = row.Description?.Invoke() ?? "";
                rowDescriptionText.color = new Color(ModStyle.Gold.r, ModStyle.Gold.g, ModStyle.Gold.b, 0.85f);
                return;
            }
            // On the section list: how to get into the settings, quietly.
            bool onList = sections.Any(s => s.Item != null && s.Item == frame.CurrentSelection);
            rowDescriptionText.text = onList && rows.Count > 0 ? "Move right, or click a setting, to change it." : "";
            rowDescriptionText.color = ModStyle.Dim;
        }

        private Row FindRow(ThronefallUIElement element)
        {
            if (element == null) return null;
            foreach (var row in rows)
            {
                if (row.Element != null && element.transform.IsChildOf(row.Element.transform)) return row;
            }
            return null;
        }

        // The master switch changes what's held off: redraw the list and the shown section in place. Deferred to the
        // next Update: the switch's own checkbox is still running its click handler when this fires.
        private bool redrawPending;

        private void OnMasterSwitchChanged() => redrawPending = true;

        private void Redraw()
        {
            if (root == null || helper == null) return;
            try
            {
                var selected = helper.targetFrame.CurrentSelection;
                int selectedRow = rows.FindIndex(r => r.Element == selected);
                bool onItem = sections.Any(s => s.Item == selected);
                if (onItem || selectedRow >= 0) helper.targetFrame.Select(tab.parentElement);
                BuildList();
                ShowSection(current);
                if (onItem) helper.targetFrame.Select(sections[current].Item);
                else if (selectedRow >= 0 && selectedRow < rows.Count) helper.targetFrame.Select(rows[selectedRow].Element);
            }
            catch (Exception e)
            {
                Log.Error("Couldn't redraw the settings after the master switch changed", e);
            }
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

        private void RegisterCommands()
        {
            DevCommands.Register("modsettings",
                "modsettings [list | <section> [row <label>]]   open Settings > Mods on a section (and select a row)",
                args =>
                {
                    if (args.Length == 0 || args[0] == "list")
                        return string.Join(", ", sections.Select((s, i) => $"{i}:{s.Name}{(s.HeldOff ? " (held off)" : "")}"));
                    var frame = Frame ?? throw new InvalidOperationException("settings screen not found");
                    if (UIFrameManager.instance.ActiveFrame != frame) UIFrameManager.instance.ChangeActiveFrame(frame);
                    if (tab == null) throw new InvalidOperationException("Mods tab not built");
                    int rowAt = Array.FindIndex(args, a => a == "row");
                    string name = string.Join(" ", rowAt < 0 ? args : args.Take(rowAt));
                    int index = int.TryParse(name, out int n) ? n : sections.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 || index >= sections.Count) throw new ArgumentException($"no section \"{name}\"");
                    frame.Select(tab.parentElement);
                    frame.Select(sections[index].Item);
                    if (rowAt >= 0)
                    {
                        string label = string.Join(" ", args.Skip(rowAt + 1));
                        var row = rows.FirstOrDefault(r => r.Element.name.EndsWith(label, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"no row \"{label}\"");
                        frame.Select(row.Element);
                    }
                    return $"{sections[index].Name}: {rows.Count} rows; description: {rowDescriptionText?.text}";
                });
        }
    }
}
