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
    survive deaths and days. What the game doesn't do is keep them through a save: TaggedObject.OnLoad strips Group1-3
    when a run is continued (its per-unit key isn't unique within a building). This feature saves each unit's group
    under its own key and puts it back after loading.
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
                "groups [list|keys|build|set <1-3> [odd|even]|save|fresh <scene>|continue <scene>]   inspect and test control groups",
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
                // The unit's marker saw the stripped tag at Start and queued its hide; cancel that.
                foreach (var indicator in unit.GetComponentsInChildren<UnitGroupIndicator>(true)) indicator.StopAllCoroutines();
                restored++;
            }
            pending.Clear();
            if (restored == 0) return;
            ControlGroupUIHelper.instance?.RefreshGroupFlags();
            Plugin.Logger.LogInfo($"Control Groups: restored {restored} unit(s) to their groups");
            if (SettingsManager.Instance != null && SettingsManager.Instance.EnableControlGroups)
                Notify.Show("Control groups restored", NotifyKind.Info, 0, "control-groups");
        }

        // Vanilla saves each unit's tags under "<building guid>_<unit name>_tags", but a building's units share a name,
        // so that key can't tell them apart. Save the group per unit under its sibling-index path inside the building.
        private static string GroupKey(TaggedObject unit)
        {
            var path = new List<int>();
            for (var t = unit.transform; t != null && t.GetComponent<SaveLoadEntity>() == null; t = t.parent)
                path.Add(t.GetSiblingIndex());
            path.Reverse();
            return "PalaceMod_group_" + string.Join(".", path);
        }

        [HarmonyPatch(typeof(TaggedObject), nameof(TaggedObject.OnSave))]
        private static class TaggedObject_OnSave
        {
            private static void Postfix(TaggedObject __instance, string guid)
            {
                if (!__instance.Tags.Contains(TagManager.ETag.PlayerUnit)) return;
                int group = 0;
                foreach (var tag in GroupTags)
                    if (__instance.Tags.Contains(tag)) group = (int)tag;
                // Always written (0 = none) so a unit taken out of its group doesn't keep an older checkpoint's group.
                MatchSaveLoadHandler.SaveValue(guid, GroupKey(__instance), group);
            }
        }

        [HarmonyPatch(typeof(TaggedObject), nameof(TaggedObject.OnLoad))]
        private static class TaggedObject_OnLoad
        {
            private static void Postfix(TaggedObject __instance, string guid)
            {
                int saved = 0;
                if (!MatchSaveLoadHandler.TryLoadValue(guid, GroupKey(__instance), ref saved)) return;
                var group = (TagManager.ETag)saved;
                if (Array.IndexOf(GroupTags, group) >= 0) pending.Add((__instance, group));
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
                case "dump":
                    // Each unit's save identity and group, sorted, to diff before a save and after continuing.
                    return string.Join("\n", (TagManager.instance ?? throw new InvalidOperationException("not in a level")).PlayerUnits
                        .Select(u => $"{u.GetComponentInParent<SaveLoadEntity>()?.GUID}/{GroupKey(u)} {u.gameObject.name}: " +
                                     string.Join(",", GroupTags.Where(u.Tags.Contains)))
                        .OrderBy(line => line, StringComparer.Ordinal));
                case "keys":
                    return ListKeys();
                case "build":
                    return BuildUnitBuildings();
                case "set":
                    {
                        if (args.Length < 2 || !int.TryParse(args[1], out int n) || n < 1 || n > 3)
                            throw new ArgumentException("usage: groups set <1-3> [odd|even]");
                        var tagManager = TagManager.instance ?? throw new InvalidOperationException("not in a level");
                        // Every unit, or every other one (so a building's units end up in different groups).
                        int parity = args.Length > 2 ? (args[2] == "odd" ? 1 : 0) : -1;
                        var units = tagManager.PlayerUnits.Where((_, i) => parity < 0 || i % 2 == parity).ToList();
                        foreach (var unit in units) unit.AddUnitGroupTag(GroupTags[n - 1]);
                        ControlGroupUIHelper.instance?.OnGroupSet(GroupTags[n - 1]);
                        return $"group {n}: {units.Count} unit(s)\n{ListGroups()}";
                    }
                case "save":
                    {
                        var saver = UnityEngine.Object.FindObjectOfType<LocalMatchSaveLoad>()
                            ?? throw new InvalidOperationException("no LocalMatchSaveLoad in this scene");
                        var spawner = EnemySpawner.instance;
                        if (LocalGamestate.Instance?.CurrentState != LocalGamestate.State.InMatch || spawner == null
                            || spawner.Wavenumber >= spawner.waves.Count - 1)
                            throw new InvalidOperationException("the game only saves during a match, before the last wave");
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

        // Builds and fully upgrades every building on the map for free (repeating, since upgrades unlock more), so there
        // are units to group.
        private static string BuildUnitBuildings()
        {
            int built = 0;
            for (int pass = 0; pass < 6; pass++)
            {
                var slots = UnityEngine.Object.FindObjectsOfType<BuildSlot>().Where(s => s.CanBeUpgraded).ToList();
                if (slots.Count == 0) break;
                foreach (var slot in slots) slot.DEBUGUpgradeToMax();
                built += slots.Count;
            }
            if (built > 0) GameplayImpact.Mark("Dev: free unit buildings");
            return $"built {built} building(s)";
        }
    }
}
