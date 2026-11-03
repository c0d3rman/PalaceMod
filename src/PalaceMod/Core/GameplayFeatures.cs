using System;
using System.Linq;
using BepInEx.Configuration;

/*
    The master switch for everything that can make a run unranked (Settings > Mods > General > "Gameplay-changing
    features"). Off keeps every run ranked: features that override BaseManager.AffectsRanking behave as disabled
    (Update not called, patches removed, keys inert, menus hidden) without touching their own Enabled settings, so
    turning the switch back on restores exactly what the player had. Sub-features inside an otherwise harmless feature
    check GameplayFeatures.Allowed themselves, call RegisterPart, and tag their setting (if any) with
    SettingTags.AffectsRanking, so the settings pane can show that the switch controls them.

        public override bool AffectsRanking => true;                         // a whole feature
        if (!mousePlacement.Value || !GameplayFeatures.Allowed) return;      // one part of a feature
*/
namespace PalaceMod
{
    public static class GameplayFeatures
    {
        public const string Section = "General";
        public const string Key = "Gameplay-changing features";

        private static ConfigEntry<bool> entry;

        /// <summary>Whether features that can make a run unranked may run. Cheap: read it in hot paths.</summary>
        public static bool Allowed { get; private set; } = true;

        /// <summary>Raised after Allowed changes (from the settings pane, a config reload or code).</summary>
        public static event Action Changed;

        /// <summary>The switch's setting; bound on first use, before any manager initialises.</summary>
        public static ConfigEntry<bool> Entry
        {
            get
            {
                if (entry != null) return entry;
                entry = Plugin.Instance.Config.Bind(Section, Key, true, new ConfigDescription(
                    "Off turns off every feature that can make a run unranked, so every run stays ranked. Each " +
                    "feature keeps its own setting for when you turn this back on."));
                Allowed = entry.Value;
                entry.SettingChanged += (_, _) =>
                {
                    if (Allowed == entry.Value) return;
                    Allowed = entry.Value;
                    Log.Core.Info($"Gameplay-changing features {(Allowed ? "on" : "off")}");
                    Changed?.Invoke();
                };
                return entry;
            }
        }

        /// <summary>Names of the features the switch controls, for descriptions.</summary>
        internal static string FeatureList() =>
            string.Join(", ", Plugin.Instance.Managers.Where(m => m.AffectsRanking && m.FeatureSection != null)
                .Select(m => m.FeatureSection).Concat(SubFeatures).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        private static readonly System.Collections.Generic.List<string> SubFeatures = [];

        /// <summary>Lists a part of a feature that checks Allowed itself (e.g. "Formations: mouse placement"), so the
        /// switch's description names it. Call in Awake.</summary>
        public static void RegisterPart(string name)
        {
            if (!SubFeatures.Contains(name)) SubFeatures.Add(name);
        }
    }
}
