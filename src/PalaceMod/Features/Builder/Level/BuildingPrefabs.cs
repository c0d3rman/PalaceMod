using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The game's standard buildings (Model.BuildingRefs): every player building kind is a Resources prefab holding the
    whole build slot (BuildSlot with its upgrade tree, blueprint and built looks, interactor, troops, upgrade effects),
    and each map's slots of that kind are instances of it. A level adds a kind its host lacks by instantiating the
    prefab exactly like a cloned host slot ("building:mill" on Frostsee): LevelOverlay for play, the Studio's palette
    for the editor. The prefab carries no map-specific wiring (activator, upgrade cap), so the overlay gives it its
    requirement (castle level 1 by default).
*/
namespace PalaceMod.Builder.Level
{
    internal static class BuildingPrefabs
    {
        private static readonly Dictionary<string, BuildSlot> cache = new(StringComparer.Ordinal);

        /// <summary>The prefab's BuildSlot for a kind id (not instantiated; never activate or edit it), or null.</summary>
        public static BuildSlot Load(string id)
        {
            if (id == null) return null;
            if (cache.TryGetValue(id, out var s) && s != null) return s;
            string path = BuildingRefs.PathOf(id);
            if (path == null) return null;
            var go = Resources.Load<GameObject>(path);
            s = go != null ? go.GetComponent<BuildSlot>() : null;
            if (s == null) Log.For<BuilderManager>().Warn($"No standard building \"{path}\" in the game's resources");
            cache[id] = s;
            return s;
        }

        /// <summary>The prefab's BuildSlot for a "building:&lt;kind&gt;" base, or null.</summary>
        public static BuildSlot ForBase(string baseRef)
        {
            var r = ContentRef.Parse(baseRef ?? "");
            return r.Kind == RefKind.Building ? Load(r.Id) : null;
        }

        /// <summary>Every standard building that loads, with its kind id.</summary>
        public static IEnumerable<(string Id, BuildSlot Slot)> All() =>
            BuildingRefs.Kinds.Select(k => (k.Id, Load(k.Id))).Where(k => k.Item2 != null);

        /// <summary>Dev (`builder-level buildings`): each standard building's tree, and how the loaded map's own slots of
        /// that kind differ from it (costs, income, choices, HP, cap, children).</summary>
        public static string Describe(UnityEngine.SceneManagement.Scene scene)
        {
            var host = scene.IsValid() && scene.isLoaded ? LayoutScene.Slots(scene) : [];
            var sb = new System.Text.StringBuilder();
            foreach (var (id, s) in All())
            {
                sb.AppendLine($"{id}: \"{s.buildingName}\" ({LayoutScene.TypeName(s)}) {Tree(s)} cap={s.RequiredRootLevelDifference} startDeactivated={s.StartDeactivated} children={Count(s)}");
                var same = host.Where(h => h.buildingName == s.buildingName && LayoutScene.CanBeBase(h)).ToList();
                if (same.Count == 0) { sb.AppendLine("    map: none"); continue; }
                foreach (var g in same.GroupBy(h => $"{Tree(h)} cap={h.RequiredRootLevelDifference} children={Count(h)}"))
                    sb.AppendLine($"    map x{g.Count()}: {(g.Key == $"{Tree(s)} cap={s.RequiredRootLevelDifference} children={Count(s)}" ? "same" : g.Key)} (e.g. {g.First().name})");
            }
            return sb.ToString();
        }

        /// <summary>Dev: child objects only in the standard building ("-") or only in the map's first slot of its kind ("+").</summary>
        public static string Diff(UnityEngine.SceneManagement.Scene scene, string id)
        {
            var s = Load(id) ?? throw new ArgumentException("no standard building " + id);
            var h = LayoutScene.Slots(scene).FirstOrDefault(x => x.buildingName == s.buildingName && LayoutScene.CanBeBase(x)) ?? throw new ArgumentException("the map has none");
            List<string> Paths(BuildSlot b) => b.GetComponentsInChildren<Transform>(true).Select(t => PathOf(t, b.transform) + " [" + string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().Name)) + "]").ToList();
            var a = Paths(s);
            var b = Paths(h);
            var sb = new System.Text.StringBuilder($"{id} vs {h.name}\n");
            foreach (var p in a.Except(b)) sb.AppendLine("- " + p);
            foreach (var p in b.Except(a)) sb.AppendLine("+ " + p);
            return sb.ToString();
        }

        private static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Tree(BuildSlot s) => string.Join(" | ", s.OwnUpgrades.Select(u =>
            $"{u.cost}{(u.disableInThisMode ? "(off)" : "")}:" + string.Join("/", (u.upgradeBranches ?? []).Select(b =>
                $"{(b.choiceDetails != null ? b.choiceDetails.name : "?")}+{b.goldIncomeChange}g+{b.hpChange}hp"))));

        private static int Count(BuildSlot s) => s.GetComponentsInChildren<Transform>(true).Length;
    }
}
