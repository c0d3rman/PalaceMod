using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Studio's game-independent logic (unit-tested in tests/PalaceMod.Tests/StudioTests.cs): mixed values for
    multi-edit, point-in-shape for the lasso, snapping, and keeping a level's JSON as small as its changes.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioMath
    {
        /// <summary>The value every element shares, or mixed = true when they differ (or there are none).</summary>
        public static T Common<T>(IEnumerable<T> values, out bool mixed)
        {
            mixed = false;
            bool any = false;
            T first = default;
            foreach (var v in values)
            {
                if (!any) { first = v; any = true; continue; }
                if (!EqualityComparer<T>.Default.Equals(first, v)) { mixed = true; return first; }
            }
            if (!any) mixed = true;
            return first;
        }

        /// <summary>Even-odd test of a point against a closed polygon.</summary>
        public static bool InPolygon(Vector2 p, IReadOnlyList<Vector2> poly) => InPolygon(p.x, p.y, poly.Count, i => poly[i].x, i => poly[i].y);

        public static bool InPolygon(float px, float py, int count, Func<int, float> xs, Func<int, float> ys)
        {
            bool inside = false;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                float yi = ys(i), yj = ys(j), xi = xs(i), xj = xs(j);
                if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>A ground offset rounded to the snap step (0 = free).</summary>
        public static Vector3 SnapDelta(Vector3 d, float step) =>
            step <= 0f ? new Vector3(d.x, 0f, d.z) : new Vector3(Mathf.Round(d.x / step) * step, 0f, Mathf.Round(d.z / step) * step);

        /// <summary>Drops empty edit entries, lists and groups, so the JSON stays as small as the change. A group loses
        /// members that are gone and is dropped below 2 members.</summary>
        public static void Prune(LevelDef lv)
        {
            static bool EmptyUpgrade(UpgradeEdit u) => u == null || (u.Cost == null && (u.Extra?.Count ?? 0) == 0);
            if (lv.BuildSlots != null)
            {
                lv.BuildSlots.Edit?.RemoveAll(e => e == null || (e.Pos == null && e.RotY == null && e.Requires == null && e.UpgradeCap == null
                    && (e.Upgrades == null || e.Upgrades.All(EmptyUpgrade)) && (e.Extra?.Count ?? 0) == 0));
                foreach (var e in lv.BuildSlots.Edit ?? []) if (e.Upgrades != null && e.Upgrades.All(EmptyUpgrade)) e.Upgrades = null;
                foreach (var a in lv.BuildSlots.Add ?? []) if (a?.Upgrades != null && a.Upgrades.All(EmptyUpgrade)) a.Upgrades = null;
                if (lv.BuildSlots.Edit?.Count == 0) lv.BuildSlots.Edit = null;
                if (lv.BuildSlots.Add?.Count == 0) lv.BuildSlots.Add = null;
                if (lv.BuildSlots.Remove?.Count == 0) lv.BuildSlots.Remove = null;
                if (lv.BuildSlots.Edit == null && lv.BuildSlots.Add == null && lv.BuildSlots.Remove == null && (lv.BuildSlots.Extra?.Count ?? 0) == 0) lv.BuildSlots = null;
            }
            if (lv.SpawnLines != null)
            {
                if (lv.SpawnLines.Edit?.Count == 0) lv.SpawnLines.Edit = null;
                if (lv.SpawnLines.Add?.Count == 0) lv.SpawnLines.Add = null;
                if (lv.SpawnLines.Remove?.Count == 0) lv.SpawnLines.Remove = null;
                if (lv.SpawnLines.Edit == null && lv.SpawnLines.Add == null && lv.SpawnLines.Remove == null && (lv.SpawnLines.Extra?.Count ?? 0) == 0) lv.SpawnLines = null;
            }
            if (lv.Props?.Count == 0) lv.Props = null;
            if (lv.Groups != null)
            {
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var a in lv.BuildSlots?.Add ?? []) if (a?.Id != null) present.Add("add:" + a.Id);
                foreach (var p in lv.Props ?? []) if (p?.Id != null) present.Add("prop:" + p.Id);
                foreach (var l in lv.SpawnLines?.Add ?? []) if (l?.Id != null) present.Add("line:" + l.Id);
                foreach (var g in lv.Groups)
                {
                    if (g?.Members == null) continue;
                    g.Members.RemoveAll(m => m == null || ((m.StartsWith("add:") || m.StartsWith("prop:")) && !present.Contains(m)));
                    var distinct = g.Members.Distinct().ToList();
                    g.Members.Clear();
                    g.Members.AddRange(distinct);
                }
                lv.Groups.RemoveAll(g => g?.Id == null || (g.Members?.Count ?? 0) < 2);
                if (lv.Groups.Count == 0) lv.Groups = null;
            }
        }

        /// <summary>Makes a group of members (each leaves any group it was in). Returns the new group's id.</summary>
        public static string Group(LevelDef lv, IReadOnlyList<string> members, string id)
        {
            lv.Groups ??= [];
            foreach (var g in lv.Groups) g.Members?.RemoveAll(m => members.Contains(m));
            lv.Groups.Add(new GroupDef { Id = id, Name = $"Group {lv.Groups.Count + 1}", Members = members.Distinct().ToList() });
            Prune(lv);
            return id;
        }
    }
}
