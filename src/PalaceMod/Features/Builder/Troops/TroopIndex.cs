using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

/*
    Where player troops live. Troops aren't standalone prefabs: every building that trains them (Archery Range,
    Barracks, Hero's Quarter) carries all of its possible troops as inactive children, one set per upgrade choice,
    and UpgradeBranch.objectsToActivate switches a set on. Level scenes hold instances of those building prefabs, so
    a troop is identified by its GameObject name without Unity's " (n)" suffix ("P Archer", "P Knight", ...).

    The building prefabs are in Resources. They take about 2 s to load synchronously, so they're loaded in the background
    (Prefetch) before the catalog first needs them.
*/
namespace PalaceMod.Builder
{
    public static class TroopIndex
    {
        /// <summary>Resources paths of the building prefabs that train troops (they have UnitRespawnerForBuildings).</summary>
        internal static readonly string[] TroopBuildingPaths = ["archer hut", "swordsmen hut", "heros quarter variant"];

        /// <summary>Names of the troops a building starts with, which no upgrade choice names and whose markers have no
        /// usable name ("P Melee" translates to " Name", "P Archer" to its building). The game calls them this in its texts.</summary>
        private static readonly Dictionary<string, string> StartingTroopNames = new() { ["P Melee"] = "Swordsmen", ["P Archer"] = "Archers" };

        private static readonly Regex CopySuffix = new(@"\s*\(\d+\)$");

        /// <summary>Catalog id of a troop: its GameObject name without a trailing " (n)".</summary>
        public static string TroopId(GameObject unit) => CopySuffix.Replace(unit.name, "");

        /// <summary>The game's name of the building a troop belongs to (BuildSlot.buildingName), or null.</summary>
        public static string BuildingOf(GameObject unit) => unit.GetComponentInParent<BuildSlot>(true)?.buildingName;

        /// <summary>Every troop GameObject (a player unit that moves and has hit points) under root, inactive included.</summary>
        public static IEnumerable<GameObject> TroopsUnder(GameObject root) =>
            root.GetComponentsInChildren<PathfindMovementPlayerunit>(true).Where(m => m.GetComponent<Hp>() != null).Select(m => m.gameObject);

        private static List<ResourceRequest> requests;

        /// <summary>Starts loading the troop buildings in the background (once); true when they're all loaded. A
        /// synchronous load takes about 2 s, so BuilderManager waits for this before it first validates packs.</summary>
        public static bool Prefetch()
        {
            requests ??= TroopBuildingPaths.Select(p => Resources.LoadAsync<GameObject>(p)).ToList();
            return requests.All(r => r.isDone);
        }

        /// <summary>One prototype per troop id from the troop buildings' prefabs, with the buildings that train it and the
        /// player-facing name: the upgrade choice that switches it on, or the starting-troop name (else null).
        /// Finishes the background loads synchronously if they're still running.</summary>
        public static List<(GameObject unit, List<string> buildings, string name)> Prototypes()
        {
            Prefetch();
            var byId = new Dictionary<string, (GameObject unit, List<string> buildings)>();
            var choices = new Dictionary<string, string>();
            for (int i = 0; i < TroopBuildingPaths.Length; i++)
            {
                if (requests[i].asset is not GameObject prefab)
                {
                    Log.For<BuilderManager>().Warn($"Troop building \"{TroopBuildingPaths[i]}\" isn't in Resources any more; its troops are missing from the catalog");
                    continue;
                }
                foreach (var unit in TroopsUnder(prefab))
                {
                    string id = TroopId(unit);
                    string building = BuildingOf(unit);
                    if (!byId.TryGetValue(id, out var entry)) byId[id] = entry = (unit, []);
                    if (!string.IsNullOrEmpty(building) && !entry.buildings.Contains(building)) entry.buildings.Add(building);
                }
                foreach (var slot in prefab.GetComponentsInChildren<BuildSlot>(true))
                    foreach (var upgrade in slot.upgrades ?? [])
                        foreach (var branch in upgrade?.upgradeBranches ?? [])
                        {
                            if (branch?.choiceDetails == null) continue;
                            foreach (var obj in branch.objectsToActivate ?? [])
                            {
                                if (obj == null) continue;
                                foreach (var unit in TroopsUnder(obj))
                                {
                                    string id = TroopId(unit);
                                    if (!choices.ContainsKey(id)) choices[id] = ChoiceName(slot, branch.choiceDetails);
                                }
                            }
                        }
            }
            return byId.Select(kv => (kv.Value.unit, kv.Value.buildings, choices.TryGetValue(kv.Key, out var c) ? c : StartingTroopNames.TryGetValue(kv.Key, out var n) ? n : null)).ToList();
        }

        private static string ChoiceName(BuildSlot slot, Choice choice)
        {
            string key = slot.GET_LOCIDENTIFIER_CHOICENAME(choice);
            string name = null;
            try { name = TextTranslator.Translate(key); }
            catch (System.Exception) { }
            name = name?.Trim();
            return string.IsNullOrEmpty(name) || name == key ? choice.name?.Trim() : name;
        }
    }
}
