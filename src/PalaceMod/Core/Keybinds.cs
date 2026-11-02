using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;

/*
    Mod keybinds. Register once (in Awake) and poll the handle, typically in Update:

        cycle = Keybinds.Register(FeatureSection, "cycle", "Cycle formation", new KeyboardShortcut(KeyCode.F7),
            "Switches to the next unit formation.");
        if (cycle.Down) ...

    Bindings are saved as KeyboardShortcut entries in the hidden "Keybinds" config section (key "<section>: <id>"), so
    they don't appear in Settings > Mods; they belong in the game's Controls > Keyboard screen under a "Mods" heading.
    A keybind is inert while its feature is disabled, while a menu is open (unless AllowInMenus), and while a text field
    has keyboard focus.

    How keys are read is behind IKeybindBackend so the polling can move to Rewired actions later without touching
    callers; see the keybind notes in CLAUDE.md.
*/
namespace PalaceMod
{
    [Flags]
    public enum KeybindFlags
    {
        None = 0,
        /// <summary>Also fire while a game menu (pause, settings, any UIFrame) is open.</summary>
        AllowInMenus = 1,
    }

    /// <summary>Reads the physical state of a keybind. Swap with Keybinds.Backend.</summary>
    public interface IKeybindBackend
    {
        bool Down(Keybind keybind);
        bool Held(Keybind keybind);
        bool Up(Keybind keybind);
    }

    /// <summary>A registered keybind. Poll Down/Held/Up from Update or a patch.</summary>
    public sealed class Keybind
    {
        /// <summary>Settings section of the owning feature.</summary>
        public string Section { get; }
        /// <summary>Stable identifier within the section; part of the config key, so don't rename it.</summary>
        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public KeybindFlags Flags { get; }
        public KeyboardShortcut DefaultShortcut => (KeyboardShortcut)Entry.DefaultValue;
        /// <summary>The saved binding. The keybind UI writes this; it's persisted to the config file.</summary>
        public ConfigEntry<KeyboardShortcut> Entry { get; }

        public KeyboardShortcut Shortcut
        {
            get => Entry.Value;
            set => Entry.Value = value;
        }

        /// <summary>Pressed this frame (with its modifiers held).</summary>
        public bool Down => Active && Keybinds.Backend.Down(this);
        /// <summary>Held down.</summary>
        public bool Held => Active && Keybinds.Backend.Held(this);
        /// <summary>Released this frame.</summary>
        public bool Up => Active && Keybinds.Backend.Up(this);

        internal Keybind(string section, string id, string displayName, string description, KeybindFlags flags, ConfigEntry<KeyboardShortcut> entry)
        {
            Section = section;
            Id = id;
            DisplayName = displayName;
            Description = description;
            Flags = flags;
            Entry = entry;
        }

        private bool Active => Keybinds.FeatureEnabled(Section) && Keybinds.InputAllowed((Flags & KeybindFlags.AllowInMenus) != 0);
    }

    public static class Keybinds
    {
        public const string ConfigSection = "Keybinds";

        private static readonly List<Keybind> all = [];
        private static readonly Dictionary<string, Keybind> byKey = [];

        /// <summary>Every registered keybind, in registration order (for the keybind UI).</summary>
        public static IReadOnlyList<Keybind> All => all;

        /// <summary>Raised when a keybind is registered (for a UI that is already built).</summary>
        public static event Action<Keybind> Registered;

        /// <summary>How keys are read; Unity's Input by default.</summary>
        public static IKeybindBackend Backend { get; set; } = new UnityInputBackend();

        /// <summary>Registers a keybind; registering the same section and id again returns the existing handle.</summary>
        /// <param name="section">The owning feature's FeatureSection, so the keybind follows its Enabled switch.</param>
        /// <param name="id">Stable identifier, unique within the section.</param>
        /// <param name="displayName">Label shown in the controls screen.</param>
        /// <param name="defaultKey">Default binding; KeyboardShortcut.Empty for unbound.</param>
        /// <param name="description">What the key does, shown as its tooltip.</param>
        public static Keybind Register(string section, string id, string displayName, KeyboardShortcut defaultKey,
            string description, KeybindFlags flags = KeybindFlags.None)
        {
            if (string.IsNullOrEmpty(section)) throw new ArgumentException("section is required", nameof(section));
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is required", nameof(id));
            string key = $"{section}: {id}";
            if (byKey.TryGetValue(key, out var existing)) return existing;
            var entry = Plugin.Instance.Config.Bind(ConfigSection, key, defaultKey, new ConfigDescription(
                $"{displayName}: {description}", null, new SettingTags { Hidden = true }));
            var keybind = new Keybind(section, id, displayName, description, flags, entry);
            all.Add(keybind);
            byKey[key] = keybind;
            Registered?.Invoke(keybind);
            return keybind;
        }

        // ---- Gating -----------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, BaseManager> features = [];

        internal static void RegisterFeature(BaseManager manager)
        {
            if (manager.FeatureSection != null) features[manager.FeatureSection] = manager;
        }

        internal static bool FeatureEnabled(string section) => !features.TryGetValue(section, out var manager) || manager.IsEnabled;

        private static int checkedFrame = -1;
        private static bool menuOpen, typing;

        internal static bool InputAllowed(bool allowInMenus)
        {
            if (checkedFrame != Time.frameCount)
            {
                checkedFrame = Time.frameCount;
                menuOpen = UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame != null;
                typing = TextFieldFocused();
            }
            return !typing && (allowInMenus || !menuOpen);
        }

        // The game has no text fields of its own; this covers mod UIs (uGUI, TextMeshPro and IMGUI fields).
        private static bool TextFieldFocused()
        {
            if (GUIUtility.keyboardControl != 0 || ModTextInput.Active) return true;
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            var tmp = selected.GetComponent<TMPro.TMP_InputField>();
            if (tmp != null && tmp.isFocused) return true;
            var legacy = selected.GetComponent<UnityEngine.UI.InputField>();
            return legacy != null && legacy.isFocused;
        }

        private sealed class UnityInputBackend : IKeybindBackend
        {
            public bool Down(Keybind keybind) => keybind.Shortcut.IsDown();
            public bool Held(Keybind keybind) => keybind.Shortcut.IsPressed();
            public bool Up(Keybind keybind) => keybind.Shortcut.IsUp();
        }
    }
}
