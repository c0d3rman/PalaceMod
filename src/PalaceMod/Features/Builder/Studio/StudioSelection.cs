using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The Studio's selection: an ordered list of item keys (click order; marquee and lasso append in distance order from
    the press point), and the hovered item. Group keys never sit in the selection: selecting a group selects its
    members (Studio.Selection keeps the order of the clicks that added them).
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioSelection
    {
        private static readonly List<string> keys = [];
        private static readonly HashSet<string> set = new(StringComparer.Ordinal);

        public static IReadOnlyList<string> Keys => keys;
        public static int Count => keys.Count;
        public static string Hover { get; private set; }
        public static bool Contains(string key) => key != null && set.Contains(key);

        public static IEnumerable<StudioItem> Items => keys.Select(StudioScene.Find).Where(i => i != null);

        /// <summary>Expands group keys to their members.</summary>
        public static IEnumerable<string> Expand(IEnumerable<string> input)
        {
            foreach (var k in input)
            {
                if (k == null) continue;
                var item = StudioScene.Find(k);
                if (item == null) continue;
                if (item.Kind == StudioItemKind.Group)
                {
                    foreach (var m in item.Members) yield return m;
                }
                else yield return k;
            }
        }

        public static void Select(IEnumerable<string> input, SelectOp op)
        {
            var list = Expand(input).Distinct().ToList();
            bool changed = false;
            switch (op)
            {
                case SelectOp.Replace:
                    if (!list.SequenceEqual(keys)) { keys.Clear(); set.Clear(); foreach (var k in list) { keys.Add(k); set.Add(k); } changed = true; }
                    break;
                case SelectOp.Add:
                    foreach (var k in list) if (set.Add(k)) { keys.Add(k); changed = true; }
                    break;
                case SelectOp.Remove:
                    foreach (var k in list) if (set.Remove(k)) { keys.Remove(k); changed = true; }
                    break;
                case SelectOp.Toggle:
                    // A group toggles as a whole: off if all its members are on.
                    bool allOn = list.Count > 0 && list.All(set.Contains);
                    foreach (var k in list)
                    {
                        if (allOn) { set.Remove(k); keys.Remove(k); }
                        else if (set.Add(k)) keys.Add(k);
                    }
                    changed = list.Count > 0;
                    break;
            }
            if (changed) Changed();
        }

        public static void Clear()
        {
            if (keys.Count == 0) return;
            keys.Clear();
            set.Clear();
            Changed();
        }

        /// <summary>Drops keys whose item is gone (after a reconcile).</summary>
        public static void Prune()
        {
            int before = keys.Count;
            keys.RemoveAll(k => StudioScene.Find(k) == null);
            set.RemoveWhere(k => StudioScene.Find(k) == null);
            if (Hover != null && StudioScene.Find(Hover) == null) Hover = null;
            if (keys.Count != before) Changed(markers: false);
        }

        public static void SetHover(string key)
        {
            if (Hover == key) return;
            string old = Hover;
            Hover = key;
            if (old != null && StudioScene.Find(old) is { } a && a.Kind != StudioItemKind.Group) StudioScene.Mark(a, a.Pos, a.Yaw);
            if (key != null && StudioScene.Find(key) is { } b && b.Kind != StudioItemKind.Group) StudioScene.Mark(b, b.Pos, b.Yaw);
        }

        /// <summary>The op the modifiers ask for: Shift adds, Ctrl/Cmd toggles, Alt+drag (paint) removes.</summary>
        public static SelectOp OpFor(StudioPointer p) => p.Ctrl ? SelectOp.Toggle : p.Shift ? SelectOp.Add : SelectOp.Replace;

        /// <summary>Selects what a click picked: the item's group unless Alt is held.</summary>
        public static void ClickSelect(string key, StudioPointer p)
        {
            if (key == null)
            {
                if (!p.Shift && !p.Ctrl) Clear();
                return;
            }
            string target = !p.Alt && StudioScene.GroupOf(key) is string g ? g : key;
            Select([target], OpFor(p));
        }

        private static void Changed(bool markers = true)
        {
            if (markers) StudioScene.RefreshMarkers();
            Studio.RaiseSelectionChanged();
            StudioShell.OnSelectionChanged();
        }

        /// <summary>The selection's centre on the ground.</summary>
        public static Vector3 Center()
        {
            var items = Items.ToList();
            if (items.Count == 0) return Vector3.zero;
            return items.Aggregate(Vector3.zero, (a, i) => a + i.Pos) / items.Count;
        }
    }
}
