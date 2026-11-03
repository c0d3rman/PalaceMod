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
    has keyboard focus. A key that only acts on one screen or in one mode can name it as its context (e.g.
    KeybindContext.LoadoutScreen); the Controls screen then only reports conflicts with keys that can act there too.
    A feature that knows when its context is on screen can claim it (Keybinds.ClaimContext): while claimed, the
    context's keys own their inputs, and every other action on the same input (the game's Zoom on Z, a mod key) is
    blocked, so one press doesn't also act in the background.

    How keys are read is behind IKeybindBackend. KeybindsManager makes each keybind a Rewired action (so it gets a
    native row in Controls > Keyboard and can be bound to keyboard, mouse or controller) and reads it from Rewired;
    keybinds registered after Rewired starts fall back to Unity's Input.
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

    /// <summary>Common keybind contexts: where a key acts, so conflicts are only shown between keys active at once.
    /// Any other string works too; keys with the same context can clash with each other.</summary>
    public static class KeybindContext
    {
        /// <summary>The loadout (perks and mutators) screen before a level.</summary>
        public const string LoadoutScreen = "Loadout screen";
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
        /// <summary>Where the key acts (see KeybindContext), or null for wherever its flags allow.</summary>
        public string Context { get; }
        public KeyboardShortcut DefaultShortcut => (KeyboardShortcut)Entry.DefaultValue;
        /// <summary>The saved binding. The keybind UI writes this; it's persisted to the config file.</summary>
        public ConfigEntry<KeyboardShortcut> Entry { get; }

        /// <summary>The Rewired action id backing this keybind, or -1 if it has none (see KeybindsManager).</summary>
        internal int ActionId { get; set; } = -1;

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

        internal Keybind(string section, string id, string displayName, string description, KeybindFlags flags, string context,
            ConfigEntry<KeyboardShortcut> entry)
        {
            Context = string.IsNullOrEmpty(context) ? null : context;
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

        /// <summary>Reads the configured KeyboardShortcut with Unity's Input; the fallback for other backends.</summary>
        internal static readonly IKeybindBackend UnityInput = new UnityInputBackend();

        /// <summary>How keys are read; Unity's Input until KeybindsManager installs the Rewired backend.</summary>
        public static IKeybindBackend Backend { get; set; } = UnityInput;

        /// <summary>Registers a keybind; registering the same section and id again returns the existing handle.</summary>
        /// <param name="section">The owning feature's FeatureSection, so the keybind follows its Enabled switch.</param>
        /// <param name="id">Stable identifier, unique within the section.</param>
        /// <param name="displayName">Label shown in the controls screen.</param>
        /// <param name="defaultKey">Default binding; KeyboardShortcut.Empty for unbound.</param>
        /// <param name="description">What the key does, shown as its tooltip.</param>
        /// <param name="context">The one screen or mode the key acts in (see KeybindContext); null for anywhere its
        /// flags allow. Only affects which conflicts the Controls screen reports.</param>
        public static Keybind Register(string section, string id, string displayName, KeyboardShortcut defaultKey,
            string description, KeybindFlags flags = KeybindFlags.None, string context = null)
        {
            if (string.IsNullOrEmpty(section)) throw new ArgumentException("section is required", nameof(section));
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is required", nameof(id));
            string key = ConfigKey(section, id);
            if (byKey.TryGetValue(key, out var existing)) return existing;
            var entry = Plugin.Instance.Config.Bind(ConfigSection, key, defaultKey, new ConfigDescription(
                $"{displayName}: {description}", null, new SettingTags { Hidden = true }));
            var keybind = new Keybind(section, id, displayName, description, flags, context, entry);
            all.Add(keybind);
            byKey[key] = keybind;
            Registered?.Invoke(keybind);
            return keybind;
        }

        // ---- Exclusive contexts ------------------------------------------------------------------------------------

        private static readonly Dictionary<string, Func<bool>> claims = [];

        /// <summary>Makes <paramref name="context"/>'s keys exclusive while <paramref name="whileActive"/> returns true:
        /// any other game or mod action bound to the same input is blocked meanwhile (e.g. the camera's Zoom while the
        /// loadout screen uses Z). Call once, typically in Awake; it's checked every frame, so keep it cheap.</summary>
        public static void ClaimContext(string context, Func<bool> whileActive)
        {
            if (string.IsNullOrEmpty(context)) throw new ArgumentException("context is required", nameof(context));
            claims[context] = whileActive ?? throw new ArgumentNullException(nameof(whileActive));
        }

        private static readonly HashSet<string> claimed = [];

        /// <summary>The contexts claimed right now (a reused set; don't keep it).</summary>
        internal static HashSet<string> ClaimedContexts()
        {
            claimed.Clear();
            foreach (var claim in claims)
            {
                bool on;
                try { on = claim.Value(); }
                catch (Exception e)
                {
                    Log.For<KeybindsManager>().Caught($"Claim check for keybind context '{claim.Key}' failed", e);
                    on = false;
                }
                if (on) claimed.Add(claim.Key);
            }
            return claimed;
        }

        internal static string ConfigKey(string section, string id) => $"{section}: {id}";
        internal static string ConfigKey(Keybind keybind) => ConfigKey(keybind.Section, keybind.Id);

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

        // KeyboardShortcut.IsDown etc. return false while any other key is held (walking, sprinting), so check only
        // the shortcut's own keys.
        private sealed class UnityInputBackend : IKeybindBackend
        {
            public bool Down(Keybind keybind) => Check(keybind.Shortcut, Input.GetKeyDown);
            public bool Held(Keybind keybind) => Check(keybind.Shortcut, Input.GetKey);
            public bool Up(Keybind keybind) => Check(keybind.Shortcut, Input.GetKeyUp);

            private static bool Check(KeyboardShortcut shortcut, Func<KeyCode, bool> main)
            {
                if (shortcut.MainKey == KeyCode.None || !main(shortcut.MainKey)) return false;
                foreach (var modifier in shortcut.Modifiers)
                {
                    if (!Input.GetKey(modifier)) return false;
                }
                return true;
            }
        }
    }
}
