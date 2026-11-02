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

                bool perFeature = rows.Select(r => byAction[rowAction(r).id].Section).Distinct().Count() > 1;
                AddHeader(rows[0], "Mods", major: true);
                string section = null;
                foreach (var row in rows)
                {
                    var keybind = byAction[rowAction(row).id];
                    if (perFeature && keybind.Section != section) AddHeader(row, keybind.Section, major: false);
                    section = keybind.Section;
                }

                // ScrollArea maps a selection's ScrollElementID onto the child count, so keep ids equal to positions
                // among the children that will remain.
                int position = 0;
                foreach (Transform child in __instance.buttonParent)
                {
                    if (__state.Contains(child) || !child.gameObject.activeSelf) continue;
                    var id = child.GetComponent<ScrollElementID>();
                    if (id != null) id.id = position;
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

        // A heading made from a copy of the row, so it has the native font, size and indent, minus everything interactive.
        private static void AddHeader(ControlMapButton row, string text, bool major)
        {
            var header = UnityEngine.Object.Instantiate(row.gameObject, row.transform.parent);
            header.name = HeaderName;
            header.transform.SetSiblingIndex(row.transform.GetSiblingIndex());
            var copy = header.GetComponent<ControlMapButton>();
            var label = copy.target.GetComponent<TextMeshProUGUI>();
            foreach (var display in new[] { copy.displayKeyboard, copy.displayMouse, copy.displayController })
            {
                if (display != null) display.text = "";
            }
            if (copy.selectionIndicator != null) UnityEngine.Object.DestroyImmediate(copy.selectionIndicator);
            UnityEngine.Object.DestroyImmediate(copy.GetComponent<ScrollElementID>());
            UnityEngine.Object.DestroyImmediate(copy);
            // Dependents first: TFUIAudioHelper requires the button.
            foreach (var helper in header.GetComponentsInChildren<TFUIAudioHelper>(true)) UnityEngine.Object.DestroyImmediate(helper);
            foreach (var element in header.GetComponentsInChildren<ThronefallUIElement>(true)) UnityEngine.Object.DestroyImmediate(element);

            label.text = text;
            if (major)
            {
                // Like the settings tabs: spaced small caps in the bright text colour, with room above it.
                label.fontStyle = FontStyles.SmallCaps;
                label.characterSpacing = 12f;
                label.color = new Color(1f, 0.906f, 0.769f); // the game's FFE7C4
                label.alignment = TextAlignmentOptions.BottomLeft;
                var rect = (RectTransform)header.transform;
                float height = Mathf.Max(rect.sizeDelta.y, LayoutUtility.GetPreferredHeight(rect));
                var layout = header.GetComponent<LayoutElement>() ?? header.AddComponent<LayoutElement>();
                layout.minHeight = layout.preferredHeight = height * 2f;
            }
            else
            {
                label.color = new Color(0.784f, 0.651f, 0.455f); // C8A674
                label.fontSize *= 0.85f;
            }
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
            string label = mine ? keybind.DisplayName : NativeLabel(row);
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

        private static string KeyGlyph(string name)
        {
            var sprites = Resources.Load<TMP_SpriteAsset>("Sprite Assets/keyboard_glyphs");
            if (sprites == null || sprites.GetSpriteIndexFromName(name) < 0) return $"<color=#C8A674>{name}</color>";
            return putIntoVoffset($"<sprite=\"keyboard_glyphs\" name=\"{name}\" color=#C8A674>");
        }

        private static string ComboGlyphs(ActionElementMap keyMap)
        {
            var parts = new List<string>();
            var flags = keyMap.modifierKeyFlags;
            if ((flags & (ModifierKeyFlags.LeftControl | ModifierKeyFlags.RightControl)) != 0) parts.Add(KeyGlyph("Left Control"));
            if ((flags & (ModifierKeyFlags.LeftAlt | ModifierKeyFlags.RightAlt)) != 0) parts.Add(KeyGlyph("Left Alt"));
            if ((flags & (ModifierKeyFlags.LeftShift | ModifierKeyFlags.RightShift)) != 0) parts.Add(KeyGlyph("Left Shift"));
            if ((flags & (ModifierKeyFlags.LeftCommand | ModifierKeyFlags.RightCommand)) != 0) parts.Add(KeyGlyph("Left Command"));
            string key = ReInput.controllers.Keyboard.GetElementIdentifierById(keyMap.elementIdentifierId)?.name ?? keyMap.keyCode.ToString();
            parts.Add(KeyGlyph(key));
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
            DevCommands.Register("keybinds", "keybinds [list|open|select <Section:id>|bind <Section:id> <Key> [Mod...]|reset]   " +
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
                    case "reset":
                        ActiveRebinder().TriggerControlsReset();
                        return "\n" + string.Join("\n", Keybinds.All.Select(Describe));
                    default:
                        throw new ArgumentException("usage: keybinds [list|open|select|bind|reset]");
                }
            });
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
