using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Rewired;
using Rewired.Data;
using UnityEngine;

/*
    Turns every registered Keybind into a real Rewired action, so the game's own Controls > Keyboard screen shows it
    (see KeybindsManager.ControlsScreen.cs) and remaps it with its own listener, for keyboard, mouse and controller.

    - Injection: a prefix on InputManager_Base.Awake adds a "PalaceMod" action category and one button action per
      keybind to Rewired's UserData before Rewired builds its runtime data. Keybinds register in feature Awake, which
      runs before the game's first scene, so they're all known by then. One registered later falls back to Unity
      Input and gets no row.
    - Persistence lives in the BepInEx config, never in the game's ControlsConfig_v2.json: Keybind.Entry holds the
      primary binding (a keyboard key with modifiers, or a mouse button if there's no key), and a hidden
      "<key> (more)" entry holds any further mouse and controller bindings. Before every save of the controls file
      the mod's bindings are copied into the config, and they're dropped from the serialized maps, so the file stays
      exactly vanilla (removing the mod, or a game update that adds actions, can't misassign anything). After the game
      loads the file, or resets controls, the bindings are re-applied from the config. A mouse button with modifiers
      can't be a Rewired binding, so such a keybind keeps reading Unity Input.
    - Polling reads the action from Rewired (GetButtonDown etc.), which ignores unrelated held keys.
*/
namespace PalaceMod
{
    internal sealed partial class KeybindsManager : BaseManager
    {
        internal const string CategoryName = "PalaceMod";
        private const string ActionPrefix = "PalaceMod: ";

        private static int categoryId = -1;
        private static readonly Dictionary<int, Keybind> byAction = [];
        private static readonly Dictionary<Keybind, ConfigEntry<string>> moreEntries = [];
        private static bool injected, writingConfig, resetting, applied;

        /// <summary>The keybind behind a Rewired action id, if it's one of ours.</summary>
        internal static bool TryGetKeybind(int actionId, out Keybind keybind) => byAction.TryGetValue(actionId, out keybind);

        internal static bool HasActions => categoryId >= 0 && byAction.Count > 0;

        private static bool Ready => applied && ReInput.isReady && Plugin.Input != null;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            Keybinds.Backend = new RewiredBackend();
            Keybinds.Registered += keybind =>
            {
                if (injected)
                    Log.For<KeybindsManager>().Warn($"Keybind {keybind.Section}: {keybind.Id} was registered after input started, so it " +
                        "has no row in Controls and reads Unity Input. Register keybinds in Awake.");
            };
            RegisterCommands();
        }

        public override void Update()
        {
            // The game loads its controls file a frame after its settings wake up, and our postfix applies then; this
            // covers the case where that never runs.
            if (!applied && categoryId >= 0 && ReInput.isReady && Plugin.Input != null) ApplyAll();
            if (!Ready) return;
            PatchGlyphs();
            UpdateClaims();
        }

        // ---- Exclusive contexts -----------------------------------------------------------------------------------

        // Other actions' element maps switched off while a claimed context's keys use the same input.
        private static readonly List<ActionElementMap> blocked = [];
        private static readonly List<int> claimingActions = [], lastClaimingActions = [];
        private static int mapsVersion, blockedVersion = -1;

        private static void UpdateClaims()
        {
            var contexts = Keybinds.ClaimedContexts();
            claimingActions.Clear();
            if (contexts.Count > 0)
            {
                foreach (var pair in byAction)
                {
                    var k = pair.Value;
                    if (k.Context != null && contexts.Contains(k.Context) && Keybinds.FeatureEnabled(k.Section)) claimingActions.Add(pair.Key);
                }
            }
            if (blockedVersion == mapsVersion && claimingActions.SequenceEqual(lastClaimingActions)) return;
            ReleaseBlocked();
            lastClaimingActions.Clear();
            lastClaimingActions.AddRange(claimingActions);
            blockedVersion = mapsVersion;
            if (claimingActions.Count == 0) return;

            var maps = Plugin.Input.controllers.maps.GetAllMaps().ToList();
            foreach (var map in maps)
            {
                foreach (var element in map.AllMaps)
                {
                    if (!claimingActions.Contains(element.actionId)) continue;
                    foreach (var otherMap in maps)
                    {
                        if (otherMap.controllerType != map.controllerType || otherMap.controllerId != map.controllerId) continue;
                        foreach (var other in otherMap.AllMaps)
                        {
                            if (!other.enabled || claimingActions.Contains(other.actionId)) continue;
                            if (other.elementType != element.elementType || other.elementIdentifierId != element.elementIdentifierId) continue;
                            if (map.controllerType == ControllerType.Keyboard && other.modifierKeyFlags != element.modifierKeyFlags) continue;
                            if (element.axisRange != AxisRange.Full && other.axisRange != AxisRange.Full && element.axisRange != other.axisRange) continue;
                            other.enabled = false;
                            blocked.Add(other);
                        }
                    }
                }
            }
            if (blocked.Count > 0)
                Log.For<KeybindsManager>().Debug($"Blocked {string.Join(", ", blocked.Select(b => ReInput.mapping.GetAction(b.actionId)?.name).Distinct())} for claimed keys");
        }

        private static void ReleaseBlocked()
        {
            foreach (var element in blocked) element.enabled = true;
            blocked.Clear();
            lastClaimingActions.Clear();
            blockedVersion = -1;
        }

        // ---- Injection --------------------------------------------------------------------------------------------

        private static readonly PropertyInfo actionName = AccessTools.Property(typeof(InputAction), nameof(InputAction.name));
        private static readonly PropertyInfo actionDescriptiveName = AccessTools.Property(typeof(InputAction), nameof(InputAction.descriptiveName));
        private static readonly PropertyInfo actionUserAssignable = AccessTools.Property(typeof(InputAction), nameof(InputAction.userAssignable));
        private static readonly PropertyInfo categoryName = AccessTools.Property(typeof(InputCategory), nameof(InputCategory.name));
        private static readonly PropertyInfo categoryDescriptiveName = AccessTools.Property(typeof(InputCategory), nameof(InputCategory.descriptiveName));
        private static readonly PropertyInfo categoryUserAssignable = AccessTools.Property(typeof(InputCategory), nameof(InputCategory.userAssignable));

        private static void Inject(UserData data)
        {
            if (data == null || Keybinds.All.Count == 0 || data.GetActionCategoryId(CategoryName) >= 0) return;
            data.AddActionCategory();
            var category = data.GetActionCategory(data.GetActionCategoryCount() - 1);
            categoryName.SetValue(category, CategoryName);
            categoryDescriptiveName.SetValue(category, "Mods");
            categoryUserAssignable.SetValue(category, true);
            categoryId = category.id;

            // Grouped by feature, features in the order they first registered a key.
            var sections = Keybinds.All.Select(k => k.Section).Distinct().ToList();
            foreach (var keybind in Keybinds.All.OrderBy(k => sections.IndexOf(k.Section)))
            {
                data.AddAction(categoryId);
                var action = data.GetAction(data.GetActionIds().Length - 1);
                actionName.SetValue(action, ActionPrefix + Keybinds.ConfigKey(keybind));
                actionDescriptiveName.SetValue(action, keybind.DisplayName);
                actionUserAssignable.SetValue(action, true);
                keybind.ActionId = action.id;
                byAction[action.id] = keybind;

                moreEntries[keybind] = Plugin.Instance.Config.Bind(Keybinds.ConfigSection, Keybinds.ConfigKey(keybind) + " (more)", "",
                    new ConfigDescription($"{keybind.DisplayName}: extra mouse and controller bindings, set in Controls.",
                        null, new SettingTags { Hidden = true }));
                var bound = keybind;
                keybind.Entry.SettingChanged += (_, _) => OnConfigChanged(bound);
                moreEntries[keybind].SettingChanged += (_, _) => OnConfigChanged(bound);
            }
            foreach (var manager in Plugin.Instance.Managers)
            {
                if (manager.EnabledEntry != null && sections.Contains(manager.FeatureSection))
                {
                    string section = manager.FeatureSection;
                    manager.EnabledEntry.SettingChanged += (_, _) => OnFeatureToggled(section);
                }
            }
            Log.For<KeybindsManager>().Debug($"Added {byAction.Count} Rewired actions in category {CategoryName} ({categoryId})");
        }

        private static void OnFeatureToggled(string section)
        {
            if (!Ready) return;
            foreach (var keybind in byAction.Values)
            {
                if (keybind.Section == section) Apply(keybind);
            }
            RebuildControlsScreen();
        }

        private static void OnConfigChanged(Keybind keybind)
        {
            if (writingConfig || !Ready) return;
            Apply(keybind);
            RefreshControlsScreen();
        }

        // ---- Config <-> Rewired maps ------------------------------------------------------------------------------

        // A binding in the "(more)" entry: type:hardwareGuid:elementType:elementId:axisRange:pole:invert:keyCode:modifiers.
        private readonly struct Binding
        {
            public readonly ControllerType Controller;
            public readonly Guid Hardware;
            public readonly ControllerElementType ElementType;
            public readonly int ElementId;
            public readonly AxisRange Range;
            public readonly Pole Pole;
            public readonly bool Invert;
            public readonly KeyCode Key;
            public readonly ModifierKeyFlags Modifiers;

            public Binding(ControllerType controller, Guid hardware, ActionElementMap map)
            {
                Controller = controller;
                Hardware = hardware;
                ElementType = map.elementType;
                ElementId = map.elementIdentifierId;
                Range = map.axisRange;
                Pole = map.axisContribution;
                Invert = map.invert;
                Key = map.keyCode;
                Modifiers = map.modifierKeyFlags;
            }

            private Binding(string[] f)
            {
                Controller = (ControllerType)int.Parse(f[0], CultureInfo.InvariantCulture);
                Hardware = string.IsNullOrEmpty(f[1]) ? Guid.Empty : new Guid(f[1]);
                ElementType = (ControllerElementType)int.Parse(f[2], CultureInfo.InvariantCulture);
                ElementId = int.Parse(f[3], CultureInfo.InvariantCulture);
                Range = (AxisRange)int.Parse(f[4], CultureInfo.InvariantCulture);
                Pole = (Pole)int.Parse(f[5], CultureInfo.InvariantCulture);
                Invert = f[6] == "1";
                Key = (KeyCode)int.Parse(f[7], CultureInfo.InvariantCulture);
                Modifiers = (ModifierKeyFlags)int.Parse(f[8], CultureInfo.InvariantCulture);
            }

            public override string ToString() => string.Join(":", (int)Controller, Hardware == Guid.Empty ? "" : Hardware.ToString(),
                (int)ElementType, ElementId, (int)Range, (int)Pole, Invert ? "1" : "0", (int)Key, (int)Modifiers);

            public static List<Binding> ParseAll(string text)
            {
                var result = new List<Binding>();
                foreach (var part in (text ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = part.Split(':');
                    if (fields.Length != 9) continue;
                    try { result.Add(new Binding(fields)); }
                    catch (Exception e) when (e is FormatException || e is OverflowException) { }
                }
                return result;
            }

            public void CreateIn(ControllerMap map, int actionId)
            {
                if (Controller == ControllerType.Keyboard) map.CreateElementMap(actionId, Pole.Positive, Key, Modifiers);
                else map.CreateElementMap(actionId, Pole, ElementId, ElementType, Range, Invert);
            }
        }

        // Rewired's mouse element ids: 3 = left button, 4 = right, 5 = middle, 6-9 = buttons 4-7.
        private const int FirstMouseButtonId = 3;
        private static bool IsMouseKey(KeyCode key) => key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;

        // Rewired mouse maps have no modifiers and keyboard maps allow only Ctrl/Alt/Shift/Cmd, so other shortcuts (a
        // mouse button with modifiers, F7+Space) stay on Unity Input; their extra controller bindings still use Rewired.
        private static bool UnityOnly(Keybind keybind)
        {
            var shortcut = keybind.Shortcut;
            if (!shortcut.Modifiers.Any()) return false;
            return IsMouseKey(shortcut.MainKey) || shortcut.Modifiers.Any(m => ToFlags([m]) == ModifierKeyFlags.None);
        }

        private static ControllerMap KeyboardMap => Plugin.Input?.controllers.maps.GetMap(ControllerType.Keyboard, 0, 0, 0);
        private static ControllerMap MouseMap => Plugin.Input?.controllers.maps.GetMap(ControllerType.Mouse, 0, 0, 0);

        private static IEnumerable<ControllerMap> EditableMaps()
        {
            var player = Plugin.Input;
            if (player == null) yield break;
            foreach (var map in player.controllers.maps.GetAllMaps())
            {
                if (map.categoryId == 0 && map.layoutId == 0) yield return map;
            }
        }

        private static Guid HardwareOf(ControllerMap map) =>
            map.controllerType == ControllerType.Joystick && map.controller is Joystick joystick ? joystick.hardwareTypeGuid : Guid.Empty;

        /// <summary>Replaces the keybind's element maps with what the config says.</summary>
        private static void Apply(Keybind keybind)
        {
            int id = keybind.ActionId;
            if (id < 0 || Plugin.Input == null) return;
            mapsVersion++;
            foreach (var map in Plugin.Input.controllers.maps.GetAllMaps()) map.DeleteElementMapsWithAction(id);
            // A disabled feature's keys are kept in the config but not mapped, so they can't shadow or clash with anything.
            if (!Keybinds.FeatureEnabled(keybind.Section)) return;

            var primary = keybind.Shortcut;
            if (UnityOnly(keybind)) { }
            else if (IsMouseKey(primary.MainKey))
            {
                MouseMap?.CreateElementMap(id, Pole.Positive, FirstMouseButtonId + (primary.MainKey - KeyCode.Mouse0),
                    ControllerElementType.Button, AxisRange.Positive, false);
            }
            else if (primary.MainKey != KeyCode.None)
            {
                KeyboardMap?.CreateElementMap(id, Pole.Positive, primary.MainKey, ToFlags(primary.Modifiers));
            }

            var more = Binding.ParseAll(moreEntries.TryGetValue(keybind, out var entry) ? entry.Value : "");
            if (more.Count == 0) return;
            foreach (var map in EditableMaps())
            {
                var hardware = HardwareOf(map);
                foreach (var binding in more)
                {
                    if (binding.Controller == map.controllerType && binding.Hardware == hardware) binding.CreateIn(map, id);
                }
            }
        }

        private static void ApplyAll()
        {
            foreach (var keybind in byAction.Values) Apply(keybind);
            applied = true;
        }

        /// <summary>Copies the keybind's current element maps into its config entries.</summary>
        private static void Sync(Keybind keybind)
        {
            if (!Keybinds.FeatureEnabled(keybind.Section)) return; // not mapped; the config is the binding
            int id = keybind.ActionId;
            ActionElementMap keyboard = null, mouse = null;
            var rest = new List<(ControllerMap map, ActionElementMap element)>();
            foreach (var map in EditableMaps())
            {
                foreach (var element in map.GetElementMapsWithAction(id))
                {
                    if (keyboard == null && map.controllerType == ControllerType.Keyboard) keyboard = element;
                    else if (mouse == null && map.controllerType == ControllerType.Mouse && element.elementType == ControllerElementType.Button
                        && element.elementIdentifierId >= FirstMouseButtonId && element.elementIdentifierId <= FirstMouseButtonId + 6)
                        mouse = element;
                    else rest.Add((map, element));
                }
            }

            // A shortcut only Unity Input can read isn't in the maps, so it stays primary unless a key replaced it.
            KeyboardShortcut primary;
            bool mouseIsExtra = keyboard != null || UnityOnly(keybind);
            if (keyboard != null) primary = new KeyboardShortcut(keyboard.keyCode, FromFlags(keyboard.modifierKeyFlags));
            else if (UnityOnly(keybind)) primary = keybind.Shortcut;
            else if (mouse != null) primary = new KeyboardShortcut(KeyCode.Mouse0 + (mouse.elementIdentifierId - FirstMouseButtonId));
            else primary = KeyboardShortcut.Empty;
            if (mouseIsExtra && mouse != null) rest.Insert(0, (MouseMap, mouse));

            // Bindings for controllers that aren't plugged in stay as they were.
            var connected = new HashSet<(ControllerType, Guid)>(EditableMaps().Select(m => (m.controllerType, HardwareOf(m))));
            var kept = Binding.ParseAll(moreEntries[keybind].Value).Where(b => !connected.Contains((b.Controller, b.Hardware)));
            string more = string.Join(";", kept.Concat(rest.Select(r => new Binding(r.map.controllerType, HardwareOf(r.map), r.element)))
                .Select(b => b.ToString()).Distinct());

            writingConfig = true;
            try
            {
                if (!primary.Equals(keybind.Shortcut)) keybind.Shortcut = primary;
                if (moreEntries[keybind].Value != more) moreEntries[keybind].Value = more;
            }
            finally
            {
                writingConfig = false;
            }
        }

        private static ModifierKeyFlags ToFlags(IEnumerable<KeyCode> modifiers)
        {
            var flags = ModifierKeyFlags.None;
            foreach (var key in modifiers)
            {
                flags |= key switch
                {
                    KeyCode.LeftControl => ModifierKeyFlags.LeftControl,
                    KeyCode.RightControl => ModifierKeyFlags.RightControl,
                    KeyCode.LeftAlt => ModifierKeyFlags.LeftAlt,
                    KeyCode.RightAlt => ModifierKeyFlags.RightAlt,
                    KeyCode.LeftShift => ModifierKeyFlags.LeftShift,
                    KeyCode.RightShift => ModifierKeyFlags.RightShift,
                    KeyCode.LeftCommand => ModifierKeyFlags.LeftCommand,
                    KeyCode.RightCommand => ModifierKeyFlags.RightCommand,
                    _ => ModifierKeyFlags.None,
                };
            }
            return flags;
        }

        private static KeyCode[] FromFlags(ModifierKeyFlags flags)
        {
            var keys = new List<KeyCode>();
            if ((flags & (ModifierKeyFlags.LeftControl | ModifierKeyFlags.RightControl)) != 0) keys.Add(KeyCode.LeftControl);
            if ((flags & (ModifierKeyFlags.LeftAlt | ModifierKeyFlags.RightAlt)) != 0) keys.Add(KeyCode.LeftAlt);
            if ((flags & (ModifierKeyFlags.LeftShift | ModifierKeyFlags.RightShift)) != 0) keys.Add(KeyCode.LeftShift);
            if ((flags & (ModifierKeyFlags.LeftCommand | ModifierKeyFlags.RightCommand)) != 0) keys.Add(KeyCode.LeftCommand);
            return keys.ToArray();
        }

        // ---- Backend ----------------------------------------------------------------------------------------------

        private sealed class RewiredBackend : IKeybindBackend
        {
            public bool Down(Keybind k) => Read(k, Keybinds.UnityInput.Down, id => Plugin.Input.GetButtonDown(id));
            public bool Held(Keybind k) => Read(k, Keybinds.UnityInput.Held, id => Plugin.Input.GetButton(id));
            public bool Up(Keybind k) => Read(k, Keybinds.UnityInput.Up, id => Plugin.Input.GetButtonUp(id));

            private static bool Read(Keybind k, Func<Keybind, bool> unity, Func<int, bool> rewired)
            {
                if (k.ActionId < 0 || !Ready) return unity(k);
                if (UnityOnly(k)) return unity(k) || rewired(k.ActionId);
                return rewired(k.ActionId);
            }
        }

        // ---- Patches ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InputManager_Base), "Awake")]
        private static class InputManager_Awake_Patch
        {
            private static void Prefix(InputManager_Base __instance)
            {
                if (ReInput.isReady || injected) return; // a duplicate manager in a later scene; Rewired destroys it
                injected = true;
                try
                {
                    Inject(__instance.userData);
                }
                catch (Exception e)
                {
                    Log.For<KeybindsManager>().Error($"Couldn't add Rewired actions, falling back to Unity Input", e);
                    foreach (var keybind in byAction.Values) keybind.ActionId = -1;
                    byAction.Clear();
                    categoryId = -1;
                }
            }
        }

        [HarmonyPatch(typeof(ControlConfigSaveLoad), nameof(ControlConfigSaveLoad.LoadControlConfigFromJson))]
        private static class Load_Patch
        {
            private static void Postfix()
            {
                if (categoryId < 0 || !ReInput.isReady) return;
                ApplyAll();
                RefreshControlsScreen();
            }
        }

        // Keep the controls file vanilla: copy our bindings into the config, then drop them from the serialized maps
        // (a copy; the live maps keep their element maps, which the open Controls rows hold on to).
        [HarmonyPatch(typeof(ControlConfigSaveLoad), nameof(ControlConfigSaveLoad.SaveControlConfigToJson))]
        private static class Save_Patch
        {
            private static void Prefix()
            {
                ReleaseBlocked(); // never save a blocked binding as switched off; it's re-blocked next frame
                if (!Ready || resetting) return;
                foreach (var keybind in byAction.Values) Sync(keybind);
            }

            // The listener refreshes its rows before saving, so the per-row "Default" buttons follow the synced config here.
            private static void Postfix()
            {
                if (Ready) UpdateResetButtons();
            }
        }

        [HarmonyPatch(typeof(ControlConfig.ControllerData), MethodType.Constructor, [typeof(ControllerType), typeof(int), typeof(string)])]
        private static class ControllerData_Patch
        {
            private static void Postfix(ControlConfig.ControllerData __instance)
            {
                if (byAction.Count == 0) return;
                __instance.knownActionIds.RemoveAll(byAction.ContainsKey);
                try
                {
                    var copy = ControllerMap.CreateFromJson(__instance.controllerType, __instance.jsonMap);
                    if (copy == null || !copy.AllMaps.Any(e => byAction.ContainsKey(e.actionId))) return;
                    foreach (var id in byAction.Keys) copy.DeleteElementMapsWithAction(id);
                    __instance.jsonMap = copy.ToJsonString();
                }
                catch (Exception e)
                {
                    Log.For<KeybindsManager>().Error($"Couldn't strip mod bindings from the controls file", e);
                }
            }
        }

        [HarmonyPatch(typeof(KeyRebinder), nameof(KeyRebinder.TriggerControlsReset))]
        private static class Reset_Patch
        {
            private static void Prefix() => resetting = true;

            private static void Finalizer()
            {
                resetting = false;
                if (!Ready) return;
                writingConfig = true;
                try
                {
                    foreach (var keybind in byAction.Values)
                    {
                        keybind.Shortcut = keybind.DefaultShortcut;
                        moreEntries[keybind].Value = "";
                    }
                }
                finally
                {
                    writingConfig = false;
                }
                ApplyAll();
                RefreshControlsScreen();
            }
        }
    }
}
