using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using I2.Loc;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    The keybind rows in Settings > Controls > Keyboard. The game's KeyRebinder makes one ControlMapButton per
    user-assignable action in its actionCategoriesToShow; adding our category there gives every keybind a native row
    (keyboard, mouse and controller columns, the native listening popup, the native "restore defaults" button). This
    file adds the "Mods" heading (plus one subheading per feature when several have keys), the labels, a conflict note,
    modifier display, and lets the listener accept modifier combos for mod rows.
*/
namespace PalaceMod
{
    internal sealed partial class KeybindsManager
    {
        private const string HeaderName = "PalaceMod Keybinds Header";
        private const string DividerName = "PalaceMod Keybinds Divider";
        private const string ResetName = "PalaceMod Reset Key";
        private const string ConflictColor = "#E0604A";

        private static readonly AccessTools.FieldRef<ControlMapButton, InputAction> rowAction =
            AccessTools.FieldRefAccess<ControlMapButton, InputAction>("actionToMap");
        private static readonly AccessTools.FieldRef<ControlMapButton, ActionElementMap> rowKeyMap =
            AccessTools.FieldRefAccess<ControlMapButton, ActionElementMap>("keyMap");
        private static readonly AccessTools.FieldRef<ControlMapButton, ControlMapButton.AxisMode> rowAxisMode =
            AccessTools.FieldRefAccess<ControlMapButton, ControlMapButton.AxisMode>("axisMode");
        private static readonly AccessTools.FieldRef<KeyRebinder, InputMapper> keyboardMapper =
            AccessTools.FieldRefAccess<KeyRebinder, InputMapper>("keyboardMapper");

        private static bool IsModRow(ControlMapButton row) => row != null && rowAction(row) != null && byAction.ContainsKey(rowAction(row).id);

        /// <summary>Re-reads bindings into every visible Controls row (after a config change or reset).</summary>
        private static void RefreshControlsScreen()
        {
            foreach (var rebinder in UnityEngine.Object.FindObjectsOfType<KeyRebinder>())
            {
                if (rebinder.isActiveAndEnabled) rebinder.UpdateButtonLabels();
            }
        }

        /// <summary>Rebuilds every visible Controls list (after a feature is switched on or off).</summary>
        private static void RebuildControlsScreen()
        {
            foreach (var rebinder in UnityEngine.Object.FindObjectsOfType<KeyRebinder>())
            {
                if (!rebinder.isActiveAndEnabled) continue;
                // The rebuild destroys the selected row; select its replacement, or the tab if it's gone.
                var frame = rebinder.GetComponentInParent<UIFrame>();
                var selected = frame?.CurrentSelection?.GetComponentInParent<ControlMapButton>();
                var key = selected != null && rowAction(selected) != null ? (rowAction(selected).id, rowAxisMode(selected)) : ((int, ControlMapButton.AxisMode)?)null;
                bool wasInList = frame?.CurrentSelection != null && frame.CurrentSelection.transform.IsChildOf(rebinder.buttonParent);
                rebinder.RegenerateButtons();
                if (frame == null || !wasInList) continue;
                var replacement = key == null ? null : rebinder.buttons.FirstOrDefault(b => rowAction(b)?.id == key.Value.Item1 && rowAxisMode(b) == key.Value.Item2);
                frame.Select(replacement != null ? replacement.target : rebinder.tabButton);
            }
        }

        // ---- Rows and headings ------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(KeyRebinder), nameof(KeyRebinder.RegenerateButtons))]
        private static class RegenerateButtons_Patch
        {
            // The old rows are only scheduled for destruction, so they're still children during the postfix.
            private static void Prefix(KeyRebinder __instance, out HashSet<Transform> __state)
            {
                __state = new HashSet<Transform>();
                foreach (Transform child in __instance.buttonParent) __state.Add(child);
                if (HasActions && !__instance.actionCategoriesToShow.Contains(CategoryName))
                    __instance.actionCategoriesToShow.Add(CategoryName);
            }

            private static void Postfix(KeyRebinder __instance, HashSet<Transform> __state)
            {
                HideDisabledRows(__instance);
                var rows = __instance.buttons.Where(IsModRow).ToList();
                if (rows.Count == 0) return;

                                AddDivider(__instance, rows[0]);
                AddHeader(rows[0], "Mods", major: true);
                string section = null;
                foreach (var row in rows)
                {
                    var keybind = byAction[rowAction(row).id];
                    if (PerFeatureHeadings && keybind.Section != section) AddHeader(row, keybind.Section, major: false);
                    section = keybind.Section;
                    AddResetButton(row, keybind);
                }

                // ScrollArea maps a selection's ScrollElementID onto the child count, so keep ids equal to positions
                // among the children that will remain.
                int position = 0;
                foreach (Transform child in __instance.buttonParent)
                {
                    if (__state.Contains(child) || !child.gameObject.activeSelf) continue;
                    var id = child.GetComponent<ScrollElementID>();
                    if (id != null) id.id = position;
                    // A selected row's "Default" button scrolls like its row (no id would scroll to the top).
                    var reset = child.Find(ResetName);
                    if (reset != null) (reset.GetComponent<ScrollElementID>() ?? reset.gameObject.AddComponent<ScrollElementID>()).id = position;
                    position++;
                }
                __instance.GetComponentInParent<UIFrame>()?.RefetchManagedElements();
            }
        }

        // Rows of switched-off features are hidden; relink the controller navigation the way RegenerateButtons does.
        private static void HideDisabledRows(KeyRebinder rebinder)
        {
            var buttons = rebinder.buttons;
            var hidden = buttons.Where(b => IsModRow(b) && !Keybinds.FeatureEnabled(byAction[rowAction(b).id].Section)).ToList();
            if (hidden.Count == 0 || hidden.Count == buttons.Count) return;
            var above = buttons[0].target.topNav;
            foreach (var row in hidden)
            {
                buttons.Remove(row);
                row.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(row.gameObject);
            }
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].target.topNav = i == 0 ? above : buttons[i - 1].target;
                buttons[i].target.botNav = i == buttons.Count - 1 ? rebinder.tabButton : buttons[i + 1].target;
            }
            rebinder.tabButton.topNav = buttons[buttons.Count - 1].target;
            rebinder.GetComponentInParent<UIFrame>()?.RefetchManagedElements();
        }

        // Whether the mod rows are grouped under one subheading per feature (only when more than one feature has keys).
        private static bool PerFeatureHeadings =>
            byAction.Values.Where(k => Keybinds.FeatureEnabled(k.Section)).Select(k => k.Section).Distinct().Skip(1).Any();

        // The rule and device icons the game puts above its own rows, repeated above the mod rows.
        private static void AddDivider(KeyRebinder rebinder, ControlMapButton firstRow)
        {
            if (rebinder.controlDevicesIconsPrefab == null) return;
            var divider = UnityEngine.Object.Instantiate(rebinder.controlDevicesIconsPrefab, rebinder.buttonParent);
            divider.name = DividerName;
            divider.transform.SetSiblingIndex(firstRow.transform.GetSiblingIndex());
            foreach (var id in divider.GetComponentsInChildren<ScrollElementID>(true)) UnityEngine.Object.DestroyImmediate(id);
        }

        // A heading made from a copy of the row, so it has the native indent and layout, minus everything interactive.
        // Headings use the game's section-label style (Cormorant Bold, capitals, wide tracking, like "NIGHT:"), which
        // reads as a heading against the light italic row labels; "Mods" is larger, bright and carries the mod's gem.
        private static void AddHeader(ControlMapButton row, string text, bool major)
        {
            var header = UnityEngine.Object.Instantiate(row.gameObject, row.transform.parent);
            header.name = HeaderName;
            header.transform.SetSiblingIndex(row.transform.GetSiblingIndex());
            var copy = header.GetComponent<ControlMapButton>();
            var label = copy.target.GetComponent<TextMeshProUGUI>();
            foreach (var display in new[] { copy.displayKeyboard, copy.displayMouse, copy.displayController })
            {
                if (display != null) UnityEngine.Object.DestroyImmediate(display.gameObject);
            }
            if (copy.selectionIndicator != null) UnityEngine.Object.DestroyImmediate(copy.selectionIndicator);
            var reset = header.transform.Find(ResetName);
            if (reset != null) UnityEngine.Object.DestroyImmediate(reset.gameObject);
            UnityEngine.Object.DestroyImmediate(copy.GetComponent<ScrollElementID>());
            UnityEngine.Object.DestroyImmediate(copy);
            // Dependents first: TFUIAudioHelper requires the button.
            foreach (var helper in header.GetComponentsInChildren<TFUIAudioHelper>(true)) UnityEngine.Object.DestroyImmediate(helper);
            foreach (var element in header.GetComponentsInChildren<ThronefallUIElement>(true)) UnityEngine.Object.DestroyImmediate(element);
            foreach (var localize in header.GetComponentsInChildren<Localize>(true)) UnityEngine.Object.DestroyImmediate(localize);

            label.text = text.ToUpperInvariant();
            if (ModStyle.BoldFont != null) label.font = ModStyle.BoldFont;
            label.fontStyle = FontStyles.Normal;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.alignment = TextAlignmentOptions.BottomLeft;
            label.raycastTarget = false;
            var rect = (RectTransform)header.transform;
            float rowHeight = Mathf.Max(rect.sizeDelta.y, 40f);
            var layout = header.GetComponent<LayoutElement>() ?? header.AddComponent<LayoutElement>();
            if (major)
            {
                label.fontSize = 30f;
                label.characterSpacing = 30f;
                label.color = ModStyle.Bright;
                layout.minHeight = layout.preferredHeight = rowHeight * 1.25f;
                var emblem = ModStyle.AddEmblem(header.transform, 16f);
                var follower = emblem.gameObject.AddComponent<EmblemFollower>();
                follower.Text = label;
            }
            else
            {
                label.fontSize = 21f;
                label.characterSpacing = 25f;
                label.color = ModStyle.Gold;
                layout.minHeight = layout.preferredHeight = rowHeight * 1.5f;
            }
        }

        // ---- Per-row restore default ------------------------------------------------------------------------------

        // Each mod row gets a small "Default" button just left of its key columns, shown only while the binding
        // differs from the default. Click it, or press right on the row and apply.
        private static void AddResetButton(ControlMapButton row, Keybind keybind)
        {
            var button = GameWidgets.Button(row.target, row.transform, ResetName, "Default", () => ResetKeybind(keybind, row));
            foreach (Transform child in button.transform.Cast<Transform>().ToList()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            foreach (var c in button.GetComponents<Component>())
            {
                if (c is ContentSizeFitter || c is LayoutElement || c is Localize) UnityEngine.Object.DestroyImmediate(c);
            }
            var rect = (RectTransform)button.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(ResetRightEdge, 0f);
            rect.sizeDelta = new Vector2(160f, 40f);
            var text = button.GetComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.Right;
            text.fontSize = 22f;
            text.color = ModStyle.Dim;
            text.enableWordWrapping = false;
            button.leftNav = row.target;
            button.topNav = row.target.topNav;
            button.botNav = row.target.botNav;
            UpdateResetButton(row);
        }

        // The selection indicator dots sit at ~818 from the row's left edge; the keyboard column starts at 844.
        private const float ResetRightEdge = 790f;

        private static bool IsCustomized(Keybind keybind) =>
            !keybind.Shortcut.Equals(keybind.DefaultShortcut) || (moreEntries.TryGetValue(keybind, out var more) && more.Value != "");

        private static void UpdateResetButton(ControlMapButton row)
        {
            var action = rowAction(row);
            if (action == null || !byAction.TryGetValue(action.id, out var keybind)) return;
            var button = row.transform.Find(ResetName)?.GetComponent<TFUITextButton>();
            if (button == null) return;
            bool show = IsCustomized(keybind);
            row.target.rightNav = show ? button : null;
            if (button.gameObject.activeSelf == show) return;
            var frame = row.GetComponentInParent<UIFrame>();
            if (!show && frame != null && frame.CurrentSelection == button) frame.Select(row.target);
            button.gameObject.SetActive(show);
        }

        private static void UpdateResetButtons()
        {
            foreach (var rebinder in UnityEngine.Object.FindObjectsOfType<KeyRebinder>())
            {
                if (!rebinder.isActiveAndEnabled) continue;
                foreach (var row in rebinder.buttons) UpdateResetButton(row);
            }
        }

        /// <summary>Puts one keybind back to its default (keyboard and any extra mouse or controller bindings).</summary>
        private static void ResetKeybind(Keybind keybind, ControlMapButton row = null)
        {
            if (!Ready) return;
            writingConfig = true;
            try
            {
                keybind.Shortcut = keybind.DefaultShortcut;
                if (moreEntries.TryGetValue(keybind, out var more)) more.Value = "";
            }
            finally
            {
                writingConfig = false;
            }
            Apply(keybind);
            RefreshControlsScreen();
            UpdateResetButtons();
            if (row != null) row.GetComponentInParent<UIFrame>()?.Select(row.target);
            Log.For<KeybindsManager>().Info($"Restored the default binding of {Keybinds.ConfigKey(keybind)}: {keybind.Shortcut}");
        }

        [HarmonyPatch(typeof(ControlMapButton), nameof(ControlMapButton.SetData))]
        private static class SetData_Patch
        {
            private static void Postfix(ControlMapButton __instance) => UpdateRow(__instance, decorateKeys: false);
        }

        // Refresh rewrites the key glyphs, so that's where modifiers are added (SetData calls Refresh first).
        [HarmonyPatch(typeof(ControlMapButton), nameof(ControlMapButton.Refresh))]
        private static class Refresh_Patch
        {
            private static void Postfix(ControlMapButton __instance) => UpdateRow(__instance, decorateKeys: true);
        }

        // The I2 term the game uses for a row's label (axis rows have Positive/Negative variants).
        private static string NativeLabel(ControlMapButton row)
        {
            var action = rowAction(row);
            string term = rowAxisMode(row) switch
            {
                ControlMapButton.AxisMode.Positive => action.name + " Positive",
                ControlMapButton.AxisMode.Negative => action.name + " Negative",
                _ => action.name,
            };
            return LocalizationManager.GetTermTranslation("Controls/" + term);
        }

        private static void UpdateRow(ControlMapButton row, bool decorateKeys)
        {
            var action = rowAction(row);
            if (action == null || !HasActions) return;
            bool mine = byAction.TryGetValue(action.id, out var keybind);
            var conflicts = Conflicts(action.id, rowAxisMode(row) switch
            {
                ControlMapButton.AxisMode.Positive => Pole.Positive,
                ControlMapButton.AxisMode.Negative => Pole.Negative,
                _ => (Pole?)null,
            });
            // Native rows only get a note when they clash with a mod key; their label otherwise stays the game's.
            if (!mine && conflicts.Count == 0)
            {
                if (row.target.GetComponent<TextMeshProUGUI>()?.text.Contains(ConflictColor) == true)
                    row.target.SetOriginalString(NativeLabel(row));
                return;
            }
            if (mine) UpdateResetButton(row);
            string label = mine ? RowLabel(keybind) : NativeLabel(row);
            if (conflicts.Count > 0) label += $" <color={ConflictColor}><size=70%>(also {string.Join(", ", conflicts)})</size></color>";
            row.target.SetOriginalString(label);

            // The native glyph is looked up by the element name, which for a key with modifiers is "Ctrl + K" and has
            // no sprite; draw each part's glyph instead.
            var keyMap = rowKeyMap(row);
            if (decorateKeys && mine && keyMap != null && keyMap.modifierKeyFlags != ModifierKeyFlags.None)
                row.displayKeyboard.text = ComboGlyphs(keyMap);
        }

        private static readonly Func<string, string> putIntoVoffset =
            AccessTools.MethodDelegate<Func<string, string>>(AccessTools.Method(typeof(InputActionGlyph), "PutIntoVoffset"));

        // Under its feature's subheading, a key named "Builder: undo" is just "Undo".
        private static string RowLabel(Keybind keybind)
        {
            string name = keybind.DisplayName;
            string prefix = keybind.Section + ": ";
            if (!PerFeatureHeadings || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length) return name;
            name = name.Substring(prefix.Length);
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private static string ComboGlyphs(ActionElementMap keyMap)
        {
            var parts = new List<string>();
            var flags = keyMap.modifierKeyFlags;
            if ((flags & (ModifierKeyFlags.LeftControl | ModifierKeyFlags.RightControl)) != 0) parts.Add(ElementGlyph("Left Control"));
            if ((flags & (ModifierKeyFlags.LeftAlt | ModifierKeyFlags.RightAlt)) != 0) parts.Add(ElementGlyph("Left Alt"));
            if ((flags & (ModifierKeyFlags.LeftShift | ModifierKeyFlags.RightShift)) != 0) parts.Add(ElementGlyph("Left Shift"));
            if ((flags & (ModifierKeyFlags.LeftCommand | ModifierKeyFlags.RightCommand)) != 0) parts.Add(ElementGlyph("Left Command"));
            string key = ReInput.controllers.Keyboard.GetElementIdentifierById(keyMap.elementIdentifierId)?.name ?? keyMap.keyCode.ToString();
            parts.Add(ElementGlyph(key));
            return string.Join("<color=#C8A674> + </color>", parts);
        }

        /// <summary>Names of the visible actions sharing a binding with this one, where at least one side is a mod key.
        /// Compares across every map of the same controller (the game keeps some keys, like WASD, in other map
        /// categories). Only actions that can act at the same time count (see Clashes).</summary>
        internal static List<string> Conflicts(int actionId, Pole? pole = null)
        {
            var names = new List<string>();
            if (Plugin.Input == null) return names;
            var allMaps = Plugin.Input.controllers.maps.GetAllMaps().ToList();
            int uiCategory = ReInput.mapping.GetActionCategoryId("UI");
            foreach (var map in allMaps)
            {
                foreach (var element in map.GetElementMapsWithAction(actionId))
                {
                    if (pole != null && element.axisContribution != pole) continue;
                    foreach (var otherMap in allMaps)
                    {
                        if (otherMap.controllerType != map.controllerType || otherMap.controllerId != map.controllerId) continue;
                        foreach (var other in otherMap.AllMaps)
                        {
                            if (other.actionId == actionId || !other.enabled) continue;
                            if (other.elementType != element.elementType || other.elementIdentifierId != element.elementIdentifierId) continue;
                            if (map.controllerType == ControllerType.Keyboard && other.modifierKeyFlags != element.modifierKeyFlags) continue;
                            // Opposite halves of one axis (wheel up / wheel down, stick left / right) are separate inputs.
                            if (element.axisRange != AxisRange.Full && other.axisRange != AxisRange.Full && element.axisRange != other.axisRange) continue;
                            if (!Clashes(actionId, other.actionId, uiCategory)) continue;
                            string name = ActionLabel(other);
                            if (name != null && !names.Contains(name)) names.Add(name);
                        }
                    }
                }
            }
            return names;
        }

        // Menu actions the game only reads on particular screens (the Settings and wardrobe tabs).
        private static readonly HashSet<string> ScreenSpecificMenuActions = ["NextTab", "PrevTab"];

        /// <summary>Whether two actions sharing a binding can both act at the same moment. A mod key with a context acts
        /// only there: on a menu screen (AllowInMenus) that excludes gameplay keys and other screens' menu keys.</summary>
        private static bool Clashes(int a, int b, int uiCategory)
        {
            bool aMine = byAction.TryGetValue(a, out var ka), bMine = byAction.TryGetValue(b, out var kb);
            if (!aMine && !bMine) return false;
            if (aMine && bMine)
            {
                if (ka.Context != null && kb.Context != null) return ka.Context == kb.Context;
                var scoped = ka.Context != null ? ka : kb.Context != null ? kb : null;
                var other = scoped == ka ? kb : ka;
                return scoped == null || !InMenus(scoped) || InMenus(other);
            }
            var mod = aMine ? ka : kb;
            var native = ReInput.mapping.GetAction(aMine ? b : a);
            if (native == null) return false;
            bool menuAction = native.categoryId == uiCategory;
            if (mod.Context == null) return !menuAction || InMenus(mod);
            if (!InMenus(mod)) return !menuAction;
            return menuAction && !ScreenSpecificMenuActions.Contains(native.name);
        }

        private static bool InMenus(Keybind keybind) => (keybind.Flags & KeybindFlags.AllowInMenus) != 0;

        private static string ActionLabel(ActionElementMap element)
        {
            if (byAction.TryGetValue(element.actionId, out var keybind)) return keybind.DisplayName;
            var action = ReInput.mapping.GetAction(element.actionId);
            if (action == null || !action.userAssignable) return null;
            string term = action.name;
            if (action.type == InputActionType.Axis) term += element.axisContribution == Pole.Positive ? " Positive" : " Negative";
            string translated = LocalizationManager.GetTermTranslation("Controls/" + term);
            return string.IsNullOrEmpty(translated) ? action.descriptiveName : translated;
        }

        // ---- Listening --------------------------------------------------------------------------------------------

        // The game's listener rejects keys with modifiers; allow them for mod rows (KeyboardShortcut supports them).
        [HarmonyPatch(typeof(KeyRebinder), nameof(KeyRebinder.TriggerRebind))]
        private static class TriggerRebind_Patch
        {
            private static void Prefix(KeyRebinder __instance, InputAction actionToMap)
            {
                var mapper = keyboardMapper(__instance);
                if (mapper != null) mapper.options.allowKeyboardKeysWithModifiers = actionToMap != null && byAction.ContainsKey(actionToMap.id);
            }
        }

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private static Keybind Find(string key) =>
            Keybinds.All.FirstOrDefault(k => string.Equals(Keybinds.ConfigKey(k).Replace(" ", ""), key.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no keybind '{key}'; known: {string.Join(", ", Keybinds.All.Select(k => Keybinds.ConfigKey(k).Replace(" ", "")))}");

        private static KeyRebinder ActiveRebinder() =>
            UnityEngine.Object.FindObjectsOfType<KeyRebinder>().FirstOrDefault(r => r.isActiveAndEnabled)
            ?? throw new InvalidOperationException("Controls > Keyboard isn't open (try `keybinds open`)");

        private static void RegisterCommands()
        {
            DevCommands.Register("keybinds", "keybinds [list|open|select <Section:id>|bind <Section:id> <Key> [Mod...]|default <Section:id>|reset|claims|camera|glyphs [sheet [dump]|elements]]   " +
                "mod keybinds in Controls; bind/reset go through the game's own save path", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
                switch (sub)
                {
                    case "list":
                        return "\n" + string.Join("\n", Keybinds.All.Select(Describe));
                    case "open":
                        return OpenControls();
                    case "select":
                    {
                        if (args.Length < 2) throw new ArgumentException("usage: keybinds select <Section:id>");
                        var keybind = Find(args[1]);
                        var row = ActiveRebinder().buttons.FirstOrDefault(b => rowAction(b)?.id == keybind.ActionId)
                            ?? throw new InvalidOperationException("no row for that keybind");
                        row.GetComponentInParent<UIFrame>().Select(row.target);
                        return $"selected {row.target.GetComponent<TextMeshProUGUI>().text}";
                    }
                    case "bind":
                    {
                        // What the native listener does when a key is pressed: replace the row's keyboard element, then
                        // the game saves its controls (our save hook copies the binding into the config).
                        if (args.Length < 3) throw new ArgumentException("usage: keybinds bind <Section:id> <KeyCode> [modifier KeyCodes]");
                        var keybind = Find(args[1]);
                        var key = (KeyCode)Enum.Parse(typeof(KeyCode), args[2], true);
                        var modifiers = args.Skip(3).Select(a => (KeyCode)Enum.Parse(typeof(KeyCode), a, true));
                        var map = IsMouseKey(key) ? MouseMap : KeyboardMap;
                        foreach (var old in map.GetElementMapsWithAction(keybind.ActionId)) map.DeleteElementMap(old.id);
                        if (IsMouseKey(key))
                            map.CreateElementMap(keybind.ActionId, Pole.Positive, FirstMouseButtonId + (key - KeyCode.Mouse0), ControllerElementType.Button, AxisRange.Positive, false);
                        else map.CreateElementMap(keybind.ActionId, Pole.Positive, key, ToFlags(modifiers));
                        ControlConfigSaveLoad.SaveControlConfigToJson();
                        RefreshControlsScreen();
                        return Describe(keybind);
                    }
                    case "key":
                    {
                        // Every action bound to a keyboard key, in any map: keybinds key <KeyCode>
                        if (args.Length < 2) throw new ArgumentException("usage: keybinds key <KeyCode>");
                        var key = (KeyCode)Enum.Parse(typeof(KeyCode), args[1], true);
                        return "\n" + string.Join("\n", Plugin.Input.controllers.maps.GetAllMaps(ControllerType.Keyboard)
                            .SelectMany(m => m.AllMaps.Where(e => e.keyCode == key).Select(e =>
                                $"map cat {m.categoryId} layout {m.layoutId} enabled {m.enabled}: {ReInput.mapping.GetAction(e.actionId)?.name} " +
                                $"({e.actionId}, {e.elementType}, id {e.elementIdentifierId}, {e.axisContribution}, range {e.axisRange}, mods {e.modifierKeyFlags}, enabled {e.enabled})")));
                    }
                    case "default":
                    {
                        // What the row's "Default" button does: keybinds default <Section:id>
                        if (args.Length < 2) throw new ArgumentException("usage: keybinds default <Section:id>");
                        var keybind = Find(args[1]);
                        ResetKeybind(keybind);
                        return Describe(keybind);
                    }
                    case "claims":
                    {
                        var contexts = Keybinds.ClaimedContexts();
                        return $"claimed=[{string.Join(", ", contexts)}] blocked=[{string.Join(", ", blocked.Select(b => $"{ReInput.mapping.GetAction(b.actionId)?.name}:{b.elementIdentifierName}"))}]";
                    }
                    case "press":
                    {
                        // Fake a physical key press through Unity's Input (which Rewired reads): keybinds press <KeyCode>
                        // An optional hold time keeps it down that long (seconds, unscaled): keybinds press <KeyCode> [seconds]
                        if (args.Length < 2) throw new ArgumentException("usage: keybinds press <KeyCode> [seconds]");
                        float seconds = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                        FakeKey.Press((KeyCode)Enum.Parse(typeof(KeyCode), args[1], true), seconds);
                        return seconds > 0f ? $"holding {args[1]} for {seconds:0.##} s" : $"pressing {args[1]} for {FakeKey.Frames} frames";
                    }
                    case "camera":
                    {
                        // The camera's zoom, to check a claimed key didn't also zoom: keybinds camera
                        var rig = UnityEngine.Object.FindObjectOfType<CameraRig>() ?? throw new InvalidOperationException("no camera rig");
                        int level = Traverse.Create(rig).Field("zoomLevel").GetValue<int>();
                        float target = Traverse.Create(rig).Field("targetOrthSize").GetValue<float>();
                        return $"zoomLevel={level} targetOrthSize={target} orthographicSize={Camera.main?.orthographicSize}";
                    }
                    case "glyphs":
                    {
                        // Sprite names in the game's glyph assets: keybinds glyphs [keyboard_glyphs|mouse_glyphs|...]
                        string asset = args.Length > 1 ? args[1] : "keyboard_glyphs";
                        if (asset == "elements")
                            return string.Join(" | ", ReInput.controllers.Keyboard.ElementIdentifiers.Select(e => e.name)) + "\nMOUSE: " +
                                string.Join(" | ", ReInput.controllers.Mouse.ElementIdentifiers.Select(e => $"{e.id}={e.name}"));
                        var sprites = Resources.Load<TMP_SpriteAsset>("Sprite Assets/" + asset) ?? throw new ArgumentException($"no sprite asset {asset}");
                        if (args.Length > 2 && args[2] == "dump")
                        {
                            var source = (Texture2D)sprites.spriteSheet;
                            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                            Graphics.Blit(source, rt);
                            var prev = RenderTexture.active;
                            RenderTexture.active = rt;
                            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(rt);
                            string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, asset + ".png");
                            System.IO.File.WriteAllBytes(path, copy.EncodeToPNG());
                            return path + "  " + string.Join("; ", sprites.spriteGlyphTable.Select((g, i) => $"{sprites.spriteCharacterTable.FirstOrDefault(c => c.glyphIndex == g.index)?.name}: {g.glyphRect.x},{g.glyphRect.y} {g.glyphRect.width}x{g.glyphRect.height} bx{g.metrics.horizontalBearingX} by{g.metrics.horizontalBearingY} adv{g.metrics.horizontalAdvance} s{g.scale}"));
                        }
                        return $"{sprites.spriteCharacterTable.Count} sprites: " + string.Join(", ", sprites.spriteCharacterTable.Select(c => c.name));
                    }
                    case "reset":
                        ActiveRebinder().TriggerControlsReset();
                        return "\n" + string.Join("\n", Keybinds.All.Select(Describe));
                    default:
                        throw new ArgumentException("usage: keybinds [list|open|select|bind|reset]");
                }
            });
        }

        // Dev only: makes Input.GetKey/GetKeyDown/GetKeyUp report one key as pressed (down on the first frame, held, then
        // up), patched in on first use. Rewired polls the keyboard through these, so game actions see it too.
        private static class FakeKey
        {
            internal const int Frames = 4;
            private static KeyCode key = KeyCode.None;
            private static int start = -1;
            private static bool patched;

            private static float until;
            private static int release = -1;

            internal static void Press(KeyCode pressed, float seconds = 0f)
            {
                if (!patched)
                {
                    var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.FakeKey");
                    foreach (var name in new[] { nameof(Input.GetKey), nameof(Input.GetKeyDown), nameof(Input.GetKeyUp) })
                        harmony.Patch(AccessTools.Method(typeof(Input), name, [typeof(KeyCode)]),
                            postfix: new HarmonyMethod(typeof(FakeKey), name == nameof(Input.GetKey) ? nameof(Held) : name == nameof(Input.GetKeyDown) ? nameof(Down) : nameof(Up)));
                    // Rewired only polls the keys while anyKey is true.
                    harmony.Patch(AccessTools.PropertyGetter(typeof(Input), nameof(Input.anyKey)), postfix: new HarmonyMethod(typeof(FakeKey), nameof(Any)));
                    patched = true;
                }
                key = pressed;
                start = Time.frameCount + 1;
                until = Time.unscaledTime + seconds;
                release = -1;
            }

            // Down for at least Frames frames and until the hold time has passed; released on the frame after.
            private static void Tick()
            {
                if (start >= 0 && release < 0 && Time.frameCount - start >= Frames && Time.unscaledTime >= until) release = Time.frameCount;
            }

            private static int Frame(KeyCode k) => k == key && start >= 0 ? Time.frameCount - start : -1;
            private static void Held(KeyCode key, ref bool __result) { Tick(); if (Frame(key) >= 0 && (release < 0 || Time.frameCount < release)) __result = true; }
            private static void Down(KeyCode key, ref bool __result) { if (Frame(key) == 0) __result = true; }
            private static void Up(KeyCode key, ref bool __result) { Tick(); if (Frame(key) >= 0 && Time.frameCount == release) __result = true; }
            private static void Any(ref bool __result) { Tick(); if (Frame(key) >= 0 && (release < 0 || Time.frameCount <= release)) __result = true; }
        }

        private static string Describe(Keybind k)
        {
            string elements = "";
            if (k.ActionId >= 0 && Plugin.Input != null)
            {
                elements = string.Join(", ", EditableMaps().SelectMany(m => m.GetElementMapsWithAction(k.ActionId)
                    .Select(e => $"{m.controllerType}:{e.elementIdentifierName}")));
            }
            var conflicts = k.ActionId >= 0 ? Conflicts(k.ActionId) : [];
            string more = moreEntries.TryGetValue(k, out var entry) && entry.Value != "" ? $" more=[{entry.Value}]" : "";
            // A switched-off feature keeps its action, but its keys aren't mapped (and its row is hidden) until it's on.
            string off = Keybinds.FeatureEnabled(k.Section) ? "" : " (feature off: mapped when enabled)";
            string context = k.Context != null ? $"  context={k.Context}" : "";
            return $"{Keybinds.ConfigKey(k)}  action={k.ActionId}{context}  config={k.Shortcut}{more}  rewired=[{elements}]{off}" +
                (conflicts.Count > 0 ? $"  conflicts=[{string.Join(", ", conflicts)}]" : "");
        }

        // Settings > Controls tab, wherever the settings frame is.
        private static string OpenControls()
        {
            var rebinder = Resources.FindObjectsOfTypeAll<KeyRebinder>().FirstOrDefault(r => r.gameObject.scene.IsValid())
                ?? throw new InvalidOperationException("no Controls screen in this scene");
            var frame = rebinder.GetComponentInParent<UIFrame>(true) ?? throw new InvalidOperationException("Controls has no frame");
            UIFrameManager.instance.ChangeActiveFrame(frame);
            var tab = rebinder.tabButton;
            if (tab != null)
            {
                frame.Select(tab);
                frame.Apply();
            }
            return $"opened {frame.name}, tab {tab?.name}";
        }
    }
}
