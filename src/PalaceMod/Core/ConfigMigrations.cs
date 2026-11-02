using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

/*
    Renamed settings keep their saved values. A section or key name is part of the BepInEx config key, so renaming
    one would otherwise reset it to its default. Add the old and new name here; Plugin applies the list at startup,
    before any setting is bound, by moving the saved value to the new name (only if nothing is saved under it yet).

    BepInEx keeps values from the file that no code has bound yet in ConfigFile.OrphanedEntries, and Bind reads its
    value from there, so moving the orphaned value is the whole migration. Renaming a section also renames its keybinds
    ("<section>: <id>" keys in the Keybinds section).
*/
namespace PalaceMod
{
    internal static class ConfigMigrations
    {
        private static readonly (string from, string to)[] Sections =
        [
            ("Control Groups", "Control groups"),
            ("Debug Menu", "Debug menu"),
        ];

        private static readonly (string section, string from, string to)[] Keys =
        [
            ("Frame rate", "Raise refresh rate (Hz)", "Raise refresh rate"),
            ("Battery", "Idle after (s)", "Idle after"),
            ("Memory", "Minimum heap size (MB)", "Minimum heap size"),
            ("Memory", "Incremental GC time slice (ms)", "Incremental GC time slice"),
            ("Diagnostics", "Spike threshold (ms)", "Spike threshold"),
            ("Diagnostics", "Summary interval (s)", "Summary interval"),
            ("Advanced", "Max busy-wait (ms)", "Max busy-wait"),
        ];

        internal static void Apply(ConfigFile config)
        {
            if (AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) is not Dictionary<ConfigDefinition, string> orphaned)
            {
                Plugin.Logger.LogWarning("Config migrations: BepInEx has no OrphanedEntries; renamed settings start from their defaults");
                return;
            }
            var moves = new List<(ConfigDefinition from, ConfigDefinition to)>();
            foreach (var definition in orphaned.Keys)
            {
                var target = Renamed(definition);
                if (target != null) moves.Add((definition, target));
            }
            int moved = 0;
            foreach (var (from, to) in moves)
            {
                if (!orphaned.ContainsKey(to) && !config.ContainsKey(to))
                {
                    orphaned[to] = orphaned[from];
                    moved++;
                }
                orphaned.Remove(from);
            }
            if (moves.Count == 0) return;
            config.Save();
            Plugin.Logger.LogInfo($"Config migrations: renamed {moved} setting(s): " +
                string.Join(", ", moves.Select(m => $"{m.from.Section}.{m.from.Key} -> {m.to.Section}.{m.to.Key}")));
        }

        private static ConfigDefinition Renamed(ConfigDefinition definition)
        {
            foreach (var (section, from, to) in Keys)
            {
                if (definition.Section == section && definition.Key == from) return new ConfigDefinition(section, to);
            }
            foreach (var (from, to) in Sections)
            {
                if (definition.Section == from) return new ConfigDefinition(to, definition.Key);
                if (definition.Section == Keybinds.ConfigSection && definition.Key.StartsWith(from + ": ", StringComparison.Ordinal))
                    return new ConfigDefinition(definition.Section, to + definition.Key.Substring(from.Length));
            }
            return null;
        }
    }
}
