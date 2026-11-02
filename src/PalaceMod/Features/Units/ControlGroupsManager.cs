using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Rewired;
using UnityEngine;

/*
    The game has native control groups (Settings > Gameplay > Control Groups): three groups, set by holding a group key
    while commanding units and recalled by tapping it, with a flag on the HUD and a marker over each unit. Group
    membership is a tag on the unit's TaggedObject, and units revive at dawn as the same objects, so groups already
    survive deaths and days. What the game doesn't do is keep them through a save: the units' tags are saved, but
    TaggedObject.OnLoad strips Group1-3 when a run is continued. This feature puts them back.
*/
namespace PalaceMod
{
    public class ControlGroupsManager : BaseManager
    {
        public override string FeatureSection => "Control Groups";
        public override string FeatureDescription =>
            "Keeps the game's control groups (Settings > Gameplay > Control Groups) assigned when you continue a saved run.";

        private static readonly TagManager.ETag[] GroupTags = [TagManager.ETag.Group1, TagManager.ETag.Group2, TagManager.ETag.Group3];

        // Filled by the OnLoad postfix while LocalMatchSaveLoad loads, applied once loading has finished.
        private static readonly List<(TaggedObject unit, TagManager.ETag group)> pending = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("groups",
                "groups [list|keys|build|set <1-3>|save|fresh <scene>|continue <scene>]   inspect and test control groups",
                RunDevCommand);
        }

        protected override void OnFeatureDisabled() => pending.Clear();

        public override void Update()
        {
            if (pending.Count == 0 || LocalMatchSaveLoad.CurrentlyLoadingMatch || TagManager.instance == null) return;
            int restored = 0;
            foreach (var (unit, group) in pending)
            {
                if (unit == null || unit.Tags.Contains(group)) continue;
                unit.AddUnitGroupTag(group);
                restored++;
            }
            pending.Clear();
            if (restored == 0) return;
            ControlGroupUIHelper.instance?.RefreshGroupFlags();
            Plugin.Logger.LogInfo($"Control Groups: restored {restored} unit(s) to their groups");
            if (SettingsManager.Instance != null && SettingsManager.Instance.EnableControlGroups)
                Notify.Show("Control groups restored", NotifyKind.Info, 0, "control-groups");
        }

        [HarmonyPatch(typeof(TaggedObject), nameof(TaggedObject.OnLoad))]
        private static class TaggedObject_OnLoad
        {
            private static void Postfix(TaggedObject __instance, string guid)
            {
                TagManager.ETag[] saved = [];
                MatchSaveLoadHandler.TryLoadValue(guid, __instance.gameObject.name + "_tags", ref saved);
                if (saved == null) return;
                foreach (var tag in GroupTags)
                {
                    if (Array.IndexOf(saved, tag) < 0) continue;
                    pending.Add((__instance, tag));
                    return; // a unit is in at most one group
                }
            }
        }

        // ---- Dev commands ---------------------------------------------------------------------------------------

        private static string RunDevCommand(string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "list":
                    return ListGroups();
                case "keys":
                    return ListKeys();
                case "build":
                    return BuildUnitBuildings();
                case "set":
                    {
                        if (args.Length < 2 || !int.TryParse(args[1], out int n) || n < 1 || n > 3)
                            throw new ArgumentException("usage: groups set <1-3>");
                        var commander = CommandUnits.instance ?? throw new InvalidOperationException("not in a level");
                        var units = commander.PlayerUnitsCommanding;
                        if (units.Count == 0)
                        {
                            // Command every living unit, as "Select All Army" would.
                            foreach (var unit in TagManager.instance.PlayerUnits.ToList()) commander.OnUnitAdd(unit, false);
                            commander.commanding = units.Count > 0;
                        }
                        commander.AddUnitsToGroup(GroupTags[n - 1]);
                        ControlGroupUIHelper.instance?.OnGroupSet(GroupTags[n - 1]);
                        return $"group {n}: {units.Count} unit(s)\n{ListGroups()}";
                    }
                case "save":
                    {
                        var saver = UnityEngine.Object.FindObjectOfType<LocalMatchSaveLoad>()
                            ?? throw new InvalidOperationException("no LocalMatchSaveLoad in this scene");
                        AccessTools.Method(typeof(LocalMatchSaveLoad), "Save").Invoke(saver, null);
                        return "saved";
                    }
                case "fresh":
                case "continue":
                    {
                        if (args.Length < 2) throw new ArgumentException($"usage: groups {sub} <scene>");
                        MatchSaveLoadHandler.TryLoadRun(args[1]);
                        MatchSaveLoadHandler.OverwriteCurrentSave = sub == "fresh";
                        if (SettingsManager.Instance != null && !SettingsManager.Instance.EnableControlGroups)
                            SettingsManager.Instance.SetEnableControlGroups(true);
                        (SceneTransitionManager.instance ?? throw new InvalidOperationException("no SceneTransitionManager"))
                            .TransitionFromNullToLevel(args[1]);
                        return sub == "fresh" ? "starting a new run" : $"continuing (save found: {MatchSaveLoadHandler.CurrentSave != null})";
                    }
                default:
                    throw new ArgumentException($"unknown subcommand \"{sub}\"");
            }
        }

        private static string ListGroups()
        {
            var tags = TagManager.instance ?? throw new InvalidOperationException("not in a level");
            var units = tags.PlayerUnits;
            var lines = new List<string> { $"{units.Count} player unit(s); control groups setting: {SettingsManager.Instance?.EnableControlGroups}" };
            foreach (var group in GroupTags)
            {
                var members = units.Where(u => u != null && u.Tags.Contains(group)).Select(u => u.gameObject.name).ToList();
                lines.Add($"{group}: {members.Count} [{string.Join(", ", members)}]");
            }
            return string.Join("\n", lines);
        }

        private static string ListKeys()
        {
            var lines = new List<string>();
            var player = ReInput.players.GetPlayer(0);
            foreach (var action in ReInput.mapping.Actions)
            {
                var keys = player.controllers.maps.ButtonMapsWithAction(ControllerType.Keyboard, action.id, false)
                    .Select(m => m.elementIdentifierName);
                lines.Add($"{action.name}: {string.Join(" / ", keys)}");
            }
            return string.Join("\n", lines);
        }

        // Builds every unit-producing building on the map, free, so there are units to group.
        private static string BuildUnitBuildings()
        {
            int built = 0;
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
            {
                if (slot.GetComponentInChildren<UnitRespawnerForBuildings>(true) == null) continue;
                if (!slot.CanBeUpgraded || slot.Level > 0) continue;
                slot.TryToBuildOrUpgradeAndPay(null, false);
                built++;
            }
            return $"built {built} building(s)";
        }
    }
}
