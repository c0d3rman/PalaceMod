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
    bools become checkboxes, enums and value lists become the game's arrow selectors, numeric ranges become sliders.
    A section selector at the top switches between config sections (the game's tabs don't scroll), and a line under
    the rows describes the selected setting. The sections in SectionOrder come first, the rest alphabetically; a
    feature's Enabled switch is its section's first row, and a section with more than RowsPerPage rows is split into
    pages ("Name (1/2)").

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
        private static readonly string[] SectionOrder = ["Frame rate", "Battery", "Optimizations", "Memory", "Diagnostics", "Advanced"];

        private SettingsUIHelper helper;
        private bool injected;
        private SettingsUIHelper.SettingsTab tab;
        private GameObject toggleTemplate, selectorTemplate, sliderTemplate;
        private GameObject sectionRow;
        private TextMeshProUGUI description;
        private readonly Dictionary<GameObject, ConfigEntryBase> rowEntries = [];
        private List<string> sections = [];
        private List<List<ConfigEntryBase>> pages = [];
        private const int RowsPerPage = 7; // what fits under the section row without scrolling
        private int sectionIndex;

        public TFUITextButton TabButton => tab?.parentElement;
        public UIFrame Frame => helper?.targetFrame ?? FindHelper()?.targetFrame;

        private static SettingsUIHelper FindHelper() =>
            Resources.FindObjectsOfTypeAll<SettingsUIHelper>().FirstOrDefault(h => h.gameObject.scene.IsValid());

        [HarmonyPatch(typeof(SettingsUIHelper), "Awake")]
        private class SettingsUIHelper_Awake_Patch
        {
            private static void Postfix(SettingsUIHelper __instance) => Plugin.Instance.GetManager<SettingsPaneManager>()?.TryInject(__instance);
        }

        private const int MaxFallbackSearches = 30;
        private int fallbackSearches;

        public override void Update()
        {
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
                Plugin.Logger.LogInfo($"Settings pane: added \"{TabLabel}\" tab with {sections.Count} sections");
            }
            catch (Exception e)
            {
                // Nothing has been hooked into the game's lists or navigation yet, so removing our clones undoes it.
                foreach (var obj in created) if (obj != null) Object.Destroy(obj);
                tab = null;
                description = null;
                Plugin.Logger.LogError($"Settings pane: couldn't add the settings tab, use the config file or F1 instead: {e}");
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

            // Content panel, cloned from the Video tab's and emptied.
            var sourceContent = helper.videoTab.contentParent;
            var content = Object.Instantiate(sourceContent, sourceContent.transform.parent);
            created.Add(content);
            content.name = "PalaceMod Settings";
            content.SetActive(false);
            var container = Corresponding(sourceContent.transform, helper.videoTab.childContainer.transform, content.transform).GetComponent<VerticalLayoutGroup>();
            foreach (Transform child in container.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            tab = new SettingsUIHelper.SettingsTab { parentElement = tabButton, contentParent = content, childContainer = container };

            CreateDescription(content.transform, container.transform);
            created.Add(description.gameObject);
            sections = CollectSections();
            if (sections.Count == 0) throw new InvalidOperationException("no settings to show");
            BuildSectionRow();
            BuildRows();

            // Hook into the game only once everything exists.
            SpliceIntoTabRow(sourceTab, tabButton);
            Traverse.Create(helper).Field("allTabs").GetValue<Dictionary<ThronefallUIElement, SettingsUIHelper.SettingsTab>>().Add(tabButton, tab);
            Traverse.Create(helper).Field("tabs").GetValue<List<SettingsUIHelper.SettingsTab>>().Add(tab);
            helper.targetFrame.onNewSelection.AddListener(OnSelectionChanged);
            tabObject.SetActive(true);
            helper.targetFrame.RefetchManagedElements();
        }

        // ---- Templates ----------------------------------------------------------------------------------------

        private void FindTemplates()
        {
            var rows = new[] { helper.videoTab, helper.audioTab, helper.gameplayTab, helper.accessibilityTab }
                .Where(t => t?.childContainer != null)
                .SelectMany(t => t.childContainer.transform.Cast<Transform>())
                .Select(t => t.gameObject)
                .ToList();
            toggleTemplate = rows.FirstOrDefault(r => r.GetComponentInChildren<Checkbox>(true) != null);
            selectorTemplate = rows.FirstOrDefault(r => r.GetComponentInChildren<EnumSelector>(true) is { delayedApply: false });
            sliderTemplate = rows.FirstOrDefault(r => r.GetComponentInChildren<TFUISlider>(true) != null && r.GetComponentInChildren<TFUISlider>(true).GetType() == typeof(TFUISlider));
            if (toggleTemplate == null || selectorTemplate == null || sliderTemplate == null)
            {
                throw new InvalidOperationException($"missing row templates (toggle {toggleTemplate != null}, selector {selectorTemplate != null}, slider {sliderTemplate != null})");
            }
        }

        // The object at the same position in `clone` as `target` is under `source`.
        private static Transform Corresponding(Transform source, Transform target, Transform clone)
        {
            var path = new Stack<int>();
            for (var t = target; t != source; t = t.parent)
            {
                if (t == null) throw new InvalidOperationException("child container isn't under its content panel");
                path.Push(t.GetSiblingIndex());
            }
            var result = clone;
            while (path.Count > 0) result = result.GetChild(path.Pop());
            return result;
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

        // Where the game shows a slider row's hint, relative to that row; the description is placed the same way
        // beside whichever row is selected.
        private Vector3 hintOffset;

        private void CreateDescription(Transform content, Transform container)
        {
            // The game's own per-row hint (the text shown beside the selected render-scale slider) is the template, so
            // descriptions look and sit like the game's. (AddComponent<TextMeshProUGUI> on a fresh object fails here.)
            var slider = sliderTemplate.GetComponentInChildren<TFUISlider>(true);
            var hint = slider.tooltip != null ? slider.tooltip.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            var template = hint ?? toggleTemplate.GetComponentInChildren<TextMeshProUGUI>(true);
            // Parent above the content panel: the rows' layout group can sit on the panel itself, where the
            // description would be laid out (and rebuilt away) as if it were a row.
            var obj = Object.Instantiate(template.gameObject, content.parent, false);
            obj.name = "PalaceMod Description";
            foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            foreach (var component in obj.GetComponents<Component>())
            {
                if (component is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(component);
            }
            obj.AddComponent<LayoutElement>().ignoreLayout = true;
            obj.SetActive(false);
            description = obj.GetComponent<TextMeshProUGUI>();
            description.raycastTarget = false;
            // The game's hint uses its warning colour; descriptions use the labels' colour instead.
            var label = toggleTemplate.GetComponentInChildren<TextMeshProUGUI>(true);
            description.color = new Color(label.color.r, label.color.g, label.color.b, 0.8f);
            var rect = (RectTransform)obj.transform;
            var source = (RectTransform)template.transform;
            rect.pivot = source.pivot;
            rect.sizeDelta = source.rect.size;
            hintOffset = hint != null ? sliderTemplate.transform.InverseTransformPoint(source.position) : new Vector3(source.rect.width, 0f, 0f);
            Plugin.Logger.LogInfo($"Settings pane: description template {(hint != null ? $"slider hint \"{hint.text}\"" : "row label")}, offset {hintOffset}, size {rect.sizeDelta}");
        }

        // ---- Rows ---------------------------------------------------------------------------------------------

        // Section labels for the selector; fills `pages` with the rows of each.
        private List<string> CollectSections()
        {
            var present = Plugin.Config.Keys.Select(d => d.Section).Distinct().Where(s => Entries(s).Any()).ToList();
            var ordered = SectionOrder.Where(present.Contains).Concat(present.Except(SectionOrder).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            var labels = new List<string>();
            pages = [];
            foreach (var section in ordered)
            {
                var entries = Entries(section).ToList();
                int count = (entries.Count + RowsPerPage - 1) / RowsPerPage;
                for (int i = 0; i < count; i++)
                {
                    labels.Add(count == 1 ? section : $"{section} ({i + 1}/{count})");
                    pages.Add(entries.Skip(i * RowsPerPage).Take(RowsPerPage).ToList());
                }
            }
            return labels;
        }

        private IEnumerable<ConfigEntryBase> Entries(string section) =>
            Plugin.Config.Keys.Where(d => d.Section == section).Select(d => Plugin.Config[d])
                .Where(e => Tags(e)?.Hidden != true && Supported(e))
                .OrderBy(e => e.Definition.Key == "Enabled" ? 0 : 1); // stable, so the rest keep their order

        private static SettingTags Tags(ConfigEntryBase entry) => entry.Description.Tags?.OfType<SettingTags>().FirstOrDefault();

        private static bool Supported(ConfigEntryBase entry) =>
            entry.SettingType == typeof(bool) || entry.SettingType.IsEnum ||
            entry.Description.AcceptableValues is AcceptableValueList<int> or AcceptableValueRange<int> or AcceptableValueRange<float>;

        private void BuildSectionRow()
        {
            sectionRow = CreateRow(selectorTemplate, "Section");
            var selector = sectionRow.GetComponentInChildren<EnumSelector>(true);
            selector.delayedApply = false;
            selector.options = [.. sections];
            selector.SetIndex(sectionIndex);
            selector.onChange.AddListener(() =>
            {
                // The game's selector "applies" on every navigation, up/down included; only rebuild on a real change,
                // or the rows would be replaced while the player is moving into them.
                if (selector.Index == sectionIndex) return;
                sectionIndex = selector.Index;
                BuildRows();
                OnSelectionChanged();
            });
        }

        private void BuildRows()
        {
            foreach (Transform child in tab.childContainer.transform.Cast<Transform>().ToList())
            {
                if (child.gameObject != sectionRow) Object.DestroyImmediate(child.gameObject);
            }
            rowEntries.Clear();
            foreach (var entry in pages[sectionIndex])
            {
                var row = entry.SettingType == typeof(bool) ? BuildToggle(entry)
                    : entry.Description.AcceptableValues is AcceptableValueRange<int> or AcceptableValueRange<float> ? BuildSlider(entry)
                    : BuildSelector(entry);
                rowEntries[row] = entry;
            }
            tab.ComputeNavigationForSettingsTab();
            helper.targetFrame.RefetchManagedElements();
        }

        private GameObject CreateRow(GameObject template, string label)
        {
            var row = Object.Instantiate(template, tab.childContainer.transform);
            row.name = $"PalaceMod {label}";
            row.SetActive(true);
            StripLocalization(row);
            StripBinders(row);
            var text = row.GetComponent<TFUITextButton>() ?? row.GetComponentInChildren<TFUITextButton>(true);
            text?.SetOriginalString(label);
            return row;
        }

        private GameObject BuildToggle(ConfigEntryBase entry)
        {
            var row = CreateRow(toggleTemplate, entry.Definition.Key);
            var checkbox = row.GetComponentInChildren<Checkbox>(true);
            checkbox.onToggle.AddListener(() => entry.BoxedValue = checkbox.state);
            Bind(row, entry, () => checkbox.SetState((bool)entry.BoxedValue));
            return row;
        }

        private GameObject BuildSelector(ConfigEntryBase entry)
        {
            var row = CreateRow(selectorTemplate, entry.Definition.Key);
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

        private GameObject BuildSlider(ConfigEntryBase entry)
        {
            var row = CreateRow(sliderTemplate, entry.Definition.Key);
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

        // ---- Description --------------------------------------------------------------------------------------

        private void OnSelectionChanged()
        {
            if (description == null) return;
            var selected = helper.targetFrame.CurrentSelection;
            for (var t = selected != null ? selected.transform : null; t != null && t != tab.childContainer.transform; t = t.parent)
            {
                string text = rowEntries.TryGetValue(t.gameObject, out var entry) ? entry.Description.Description
                    : t.gameObject == sectionRow ? "PalaceMod settings, grouped by section. Changes apply right away and are saved to the config file."
                    : null;
                if (text == null) continue;
                description.text = text;
                description.transform.position = t.TransformPoint(hintOffset);
                description.gameObject.SetActive(true);
                return;
            }
            description.gameObject.SetActive(false);
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
    }
}
