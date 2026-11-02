using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using I2.Loc;
using Rewired;
using TMPro;
using UnityEngine;

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

        // ---- Rows and headings ------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(KeyRebinder), nameof(KeyRebinder.RegenerateButtons))]
        private static class RegenerateButtons_Patch
        {
            private static void Prefix(KeyRebinder __instance)
            {
                if (HasActions && !__instance.actionCategoriesToShow.Contains(CategoryName))
                    __instance.actionCategoriesToShow.Add(CategoryName);
            }

            private static void Postfix(KeyRebinder __instance)
            {
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

                // ScrollArea maps a selection's ScrollElementID onto the child count, so keep ids equal to positions.
                var parent = __instance.buttonParent;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var id = parent.GetChild(i).GetComponent<ScrollElementID>();
                    if (id != null) id.id = i;
                }
                __instance.GetComponentInParent<UIFrame>()?.RefetchManagedElements();
            }
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
            UnityEngine.Object.DestroyImmediate(copy.target);
            UnityEngine.Object.DestroyImmediate(copy);
            foreach (var element in header.GetComponentsInChildren<ThronefallUIElement>(true)) UnityEngine.Object.DestroyImmediate(element);

            label.text = major ? text.ToUpperInvariant() : text;
            label.fontStyle = major ? FontStyles.Bold : FontStyles.Italic;
            label.color = major ? new Color(1f, 0.906f, 0.769f) : new Color(0.784f, 0.651f, 0.455f); // the game's FFE7C4 / C8A674
            if (major) label.fontSize *= 1.1f;
        }

        [HarmonyPatch(typeof(ControlMapButton), nameof(ControlMapButton.SetData))]
        private static class SetData_Patch
        {
            private static void Postfix(ControlMapButton __instance) => UpdateRow(__instance);
        }

        [HarmonyPatch(typeof(ControlMapButton), nameof(ControlMapButton.Refresh))]
        private static class Refresh_Patch
        {
            private static void Postfix(ControlMapButton __instance) => UpdateRow(__instance);
        }

        private static void UpdateRow(ControlMapButton row)
        {
            var action = rowAction(row);
            if (action == null || !HasActions) return;
            bool mine = byAction.TryGetValue(action.id, out var keybind);
            var conflicts = Conflicts(action.id);
            // Native rows only get a note when they clash with a mod key; their label otherwise stays the game's.
            if (!mine && conflicts.Count == 0)
            {
                if (row.target.GetComponent<TextMeshProUGUI>()?.text.Contains(ConflictColor) == true)
                    row.target.SetOriginalString(LocalizationManager.GetTermTranslation("Controls/" + action.name));
                return;
            }
            string label = mine ? keybind.DisplayName : LocalizationManager.GetTermTranslation("Controls/" + action.name);
            if (conflicts.Count > 0) label += $" <color={ConflictColor}><size=70%>(also {string.Join(", ", conflicts)})</size></color>";
            row.target.SetOriginalString(label);

            // The native glyph shows only the key; show held modifiers in front of it.
            var keyMap = rowKeyMap(row);
            if (mine && keyMap != null && keyMap.modifierKeyFlags != ModifierKeyFlags.None)
                row.displayKeyboard.text = $"<color=#C8A674>{ModifierText(keyMap.modifierKeyFlags)}</color>{row.displayKeyboard.text}";
        }

        private static string ModifierText(ModifierKeyFlags flags)
        {
            string text = "";
            if ((flags & (ModifierKeyFlags.LeftControl | ModifierKeyFlags.RightControl)) != 0) text += "Ctrl+";
            if ((flags & (ModifierKeyFlags.LeftAlt | ModifierKeyFlags.RightAlt)) != 0) text += "Alt+";
            if ((flags & (ModifierKeyFlags.LeftShift | ModifierKeyFlags.RightShift)) != 0) text += "Shift+";
            if ((flags & (ModifierKeyFlags.LeftCommand | ModifierKeyFlags.RightCommand)) != 0) text += "Cmd+";
            return text;
        }

        /// <summary>Names of the visible actions sharing a binding with this one, where at least one side is a mod key.</summary>
        internal static List<string> Conflicts(int actionId)
        {
            var names = new List<string>();
            if (Plugin.Input == null) return names;
            bool mine = byAction.ContainsKey(actionId);
            foreach (var map in Plugin.Input.controllers.maps.GetAllMaps())
            {
                if (!map.enabled && map.categoryId != 0) continue;
                foreach (var element in map.GetElementMapsWithAction(actionId))
                {
                    foreach (var other in map.AllMaps)
                    {
                        if (other.actionId == actionId || !other.enabled || (!mine && !byAction.ContainsKey(other.actionId))) continue;
                        if (other.elementType != element.elementType || other.elementIdentifierId != element.elementIdentifierId) continue;
                        if (map.controllerType == ControllerType.Keyboard && other.modifierKeyFlags != element.modifierKeyFlags) continue;
                        string name = ActionLabel(other);
                        if (name != null && !names.Contains(name)) names.Add(name);
                    }
                }
            }
            return names;
        }

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
                    .Select(e => $"{m.controllerType}:{(e.modifierKeyFlags != ModifierKeyFlags.None ? ModifierText(e.modifierKeyFlags) : "")}{e.elementIdentifierName}")));
            }
            var conflicts = k.ActionId >= 0 ? Conflicts(k.ActionId) : [];
            string more = moreEntries.TryGetValue(k, out var entry) && entry.Value != "" ? $" more=[{entry.Value}]" : "";
            return $"{Keybinds.ConfigKey(k)}  action={k.ActionId}  config={k.Shortcut}{more}  rewired=[{elements}]" +
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
