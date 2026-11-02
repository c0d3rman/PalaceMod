using UnityEngine;
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

/*
    Base class for everything Plugin runs. Plugin finds every non-abstract BaseManager subclass outside the
    PalaceMod.Experimental namespace, orders them by [ManagerOrder] (then by name), and applies the Harmony patches
    nested inside each one. Adding a feature never requires touching Plugin.cs.

    A feature overrides FeatureSection and FeatureDescription. BaseManager then binds "<FeatureSection>.Enabled",
    shown as the first row of that section in Settings > Mods, and while the feature is disabled:
    - its Update isn't called,
    - its nested Harmony patches are removed (unless UnpatchWhenDisabled is false), and
    - OnFeatureDisabled / OnFeatureEnabled run whenever the setting changes (from the pane or a live config reload).
    Patches that stay applied can early-out with `if (!Feature<MyManager>.On) return;`.
    Managers that leave FeatureSection null are infrastructure and are always on.
*/
namespace PalaceMod
{
    public abstract class BaseManager : IManager
    {
        protected Plugin Plugin { get; private set; }

        /// <summary>Settings section that holds this feature's Enabled switch and settings; null = always on.</summary>
        public virtual string FeatureSection => null;

        /// <summary>One or two sentences shown beside the Enabled row in Settings > Mods.</summary>
        public virtual string FeatureDescription => null;

        /// <summary>Whether the feature starts enabled for players who haven't changed the setting.</summary>
        protected virtual bool EnabledByDefault => true;

        /// <summary>Remove this feature's Harmony patches while it's disabled. Turn off to keep patches applied
        /// and gate them with Feature&lt;T&gt;.On instead.</summary>
        protected virtual bool UnpatchWhenDisabled => true;

        /// <summary>The feature's Enabled setting; null for infrastructure managers.</summary>
        public ConfigEntry<bool> EnabledEntry { get; private set; }

        public bool IsEnabled => EnabledEntry == null || EnabledEntry.Value;

        /// <summary>Runs when the Enabled setting changes to true at runtime (not at startup; check IsEnabled in Awake).</summary>
        protected virtual void OnFeatureEnabled() { }

        /// <summary>Runs when the Enabled setting changes to false at runtime. Undo visible changes here (created UI,
        /// game state you overrode). Patches are already removed when this runs.</summary>
        protected virtual void OnFeatureDisabled() { }

        private Harmony harmony;
        private bool patched;
        private bool lastEnabled;

        /// <summary>Called by Plugin before Awake: binds Enabled and applies patches if the feature is on.</summary>
        internal void Initialize(Plugin plugin)
        {
            Plugin = plugin;
            harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.{GetType().Name}");
            if (FeatureSection != null)
            {
                EnabledEntry = plugin.Config.Bind(FeatureSection, "Enabled", EnabledByDefault, new ConfigDescription(
                    FeatureDescription ?? $"Turns {FeatureSection} on or off."));
                EnabledEntry.SettingChanged += (_, _) => ApplyEnabled();
            }
            lastEnabled = IsEnabled;
            if (IsEnabled || !UnpatchWhenDisabled) Patch();
        }

        private void ApplyEnabled()
        {
            bool enabled = IsEnabled;
            if (enabled == lastEnabled) return;
            lastEnabled = enabled;
            try
            {
                if (UnpatchWhenDisabled)
                {
                    if (enabled) Patch();
                    else Unpatch();
                }
                if (enabled) OnFeatureEnabled();
                else OnFeatureDisabled();
                Plugin.Logger.LogInfo($"{FeatureSection}: {(enabled ? "enabled" : "disabled")}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"{GetType().Name}: switching {(enabled ? "on" : "off")} failed: {e}");
            }
        }

        // The manager's own patches and those of every type nested in it, at any depth.
        private void Patch()
        {
            if (patched) return;
            patched = true;
            foreach (var type in AccessTools.GetTypesFromAssembly(GetType().Assembly))
            {
                for (var t = type; t != null; t = t.DeclaringType)
                {
                    if (t != GetType()) continue;
                    harmony.CreateClassProcessor(type).Patch();
                    break;
                }
            }
        }

        private void Unpatch()
        {
            if (!patched) return;
            patched = false;
            harmony.UnpatchSelf();
        }

        internal void UnpatchAll() => Unpatch();

        /// <summary>Number of game methods this manager's patches currently modify.</summary>
        internal int PatchedMethodCount => harmony == null ? 0 : harmony.GetPatchedMethods().Count();

        public virtual void Awake(Plugin plugin)
        {
            Plugin = plugin;
        }

        public virtual void Start() { }

        public virtual void Update() { }

        public virtual void OnDestroy() { }

        /// <summary>
        /// Loads a sprite from an embedded resource in the Assets folder.
        /// </summary>
        /// <param name="resourceName">The name of the resource file (e.g., "dice_icon.png")</param>
        /// <param name="pixelsPerUnit">Pixels per unit for the sprite. Default is 100.</param>
        /// <param name="pivot">Pivot point for the sprite. Default is center (0.5, 0.5).</param>
        /// <returns>A Sprite created from the embedded resource</returns>
        /// <exception cref="FileNotFoundException">Thrown when the resource cannot be found</exception>
        /// <exception cref="InvalidOperationException">Thrown when the resource cannot be loaded as an image</exception>
        protected Sprite LoadSpriteFromAssets(string resourceName, float pixelsPerUnit = 100f, Vector2? pivot = null)
        {
            if (string.IsNullOrEmpty(resourceName))
                throw new ArgumentNullException(nameof(resourceName), "Resource name cannot be null or empty");

            var assembly = Assembly.GetExecutingAssembly();
            string fullResourceName = $"{MyPluginInfo.PLUGIN_GUID}.Assets.{resourceName}";
            if (!assembly.GetManifestResourceNames().Contains(fullResourceName))
            {
                string availableResources = string.Join(", ", assembly.GetManifestResourceNames());
                throw new FileNotFoundException(
                    $"Resource '{resourceName}' not found. Full resource name '{fullResourceName}' not found in assembly. " +
                    $"Available resources: {availableResources}",
                    resourceName);
            }

            using (Stream stream = assembly.GetManifestResourceStream(fullResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"Failed to open resource stream for '{fullResourceName}'");

                byte[] fileData;
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    fileData = ms.ToArray();
                }

                if (fileData.Length == 0)
                    throw new InvalidOperationException($"Resource '{fullResourceName}' is empty");

                Texture2D texture = new Texture2D(2, 2);
                if (!texture.LoadImage(fileData))
                    throw new InvalidOperationException($"Failed to load image data from resource '{fullResourceName}'");

                return Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    pivot ?? new Vector2(0.5f, 0.5f),
                    pixelsPerUnit
                );
            }
        }
    }
}
