using System;
using System.Collections.Generic;

/*
    What Settings > Mods shows besides the config entries themselves (see Features/UI/SettingsPaneManager.cs):

        SettingsPane.DescribeSection("Diagnostics", "Logging and bug reports.");   // shown at the top of its page
        SettingsPane.AddButton("Diagnostics", "Create debug bundle", "Saves a zip for a bug report.", CreateBundle);

    A feature's section is described by its BaseManager.FeatureDescription; DescribeSection is for sections without a
    feature (infrastructure, sub-sections). Buttons are actions, not settings: they show after the section's settings,
    in the game's button style, and run onClick when clicked or applied. Call both in Awake.
*/
namespace PalaceMod
{
    public static class SettingsPane
    {
        public sealed class ActionButton
        {
            public string Section, Label, Description;
            public Action OnClick;
        }

        internal static readonly Dictionary<string, string> SectionDescriptions = [];
        internal static readonly List<ActionButton> Buttons = [];

        /// <summary>Sets the text shown at the top of a section's page.</summary>
        public static void DescribeSection(string section, string description) => SectionDescriptions[section] = description;

        /// <summary>Adds an action button at the end of a section's page (once per section and label).</summary>
        public static void AddButton(string section, string label, string description, Action onClick)
        {
            Buttons.RemoveAll(b => b.Section == section && b.Label == label);
            Buttons.Add(new ActionButton { Section = section, Label = label, Description = description, OnClick = onClick });
        }
    }
}
