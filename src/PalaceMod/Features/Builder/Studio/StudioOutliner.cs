using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Economy;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    The right panel's Outliner: every object of the level, filterable by chips (Slots, Castle, Scenery, Spawns, Groups,
    Changed, Problems) and text. Clicking selects (Shift adds, Ctrl/Cmd toggles); clicking a selected row frames it.
    Groups are collapsible rows. Ctrl/Cmd+A selects what the filter shows.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioOutliner
    {
        public static string Chip;           // null = all
        public static string Text = "";
        private static readonly HashSet<string> expanded = new(StringComparer.Ordinal);

        private static readonly (string id, string label)[] Chips =
            [("", "All"), ("slots", "Slots"), ("castle", "Castle"), ("scenery", "Scenery"), ("spawns", "Spawns"), ("groups", "Groups"), ("changed", "Changed"), ("problems", "Problems")];

        private static bool Matches(StudioItem i)
        {
            bool chip = Chip switch
            {
                "slots" => i.Kind is StudioItemKind.Slot or StudioItemKind.Added,
                "castle" => i.IsCastle,
                "scenery" => i.Kind == StudioItemKind.Prop,
                "spawns" => i.Kind == StudioItemKind.Line,
                "groups" => i.Kind == StudioItemKind.Group || StudioScene.GroupOf(i.Key) != null,
                "changed" => i.Moved || i.Removed || i.Kind is StudioItemKind.Added or StudioItemKind.Prop,
                "problems" => Problems().Contains(i.Key),
                _ => true,
            };
            if (!chip) return false;
            string t = Text?.Trim() ?? "";
            return t.Length == 0 || (i.Name ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || (i.TypeName ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Keys of items a level issue points at.</summary>
        private static HashSet<string> Problems()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var issue in StudioDock.LevelIssues()) if (StudioMenus.KeyForPath(issue.Path) is string k) set.Add(k);
            return set;
        }

        /// <summary>What Select all takes: the filtered objects (groups as their members).</summary>
        public static IEnumerable<string> FilteredKeys()
        {
            var list = StudioScene.Pickable.Where(Matches).Select(i => i.Key).ToList();
            return list;
        }

        public static void Build(ModPage page)
        {
            StudioDock.Tabs(page, Chips.ToList(), () => Chip ?? "", id => { Chip = id.Length == 0 ? null : id; StudioDock.RefreshAll(); }, 4, 17f);
            page.TextInput("Find", () => Text, v => { Text = v ?? ""; StudioDock.RefreshAll(); });
            var items = StudioScene.Items.Where(i => i.Kind != StudioItemKind.Group && StudioScene.GroupOf(i.Key) == null && Matches(i)).ToList();
            var groups = StudioScene.Items.Where(i => i.Kind == StudioItemKind.Group && (Matches(i) || i.Members.Any(m => StudioScene.Find(m) is { } mi && Matches(mi)))).ToList();
            if (items.Count == 0 && groups.Count == 0) { page.Text("Nothing matches"); return; }
            foreach (var g in groups)
            {
                var group = g;
                bool open = expanded.Contains(g.Key);
                bool on = g.Members.Count > 0 && g.Members.All(StudioSelection.Contains);
                Row(page, (open ? "▾ " : "▸ ") + Esc(g.Name), $"{g.Members.Count}", on, null, () => Click(group), () =>
                {
                    if (!expanded.Add(group.Key)) expanded.Remove(group.Key);
                    StudioDock.RefreshAll();
                });
                if (!open) continue;
                foreach (var m in g.Members.Select(StudioScene.Find).Where(m => m != null))
                {
                    var member = m;
                    Row(page, "    " + Esc(m.Name), Value(m), StudioSelection.Contains(m.Key), PlannerIcons.Kind(m.TypeName), () => ClickMember(member), null);
                }
            }
            foreach (var i in items)
            {
                var item = i;
                Row(page, Esc(i.Name), Value(i), StudioSelection.Contains(i.Key), i.Kind is StudioItemKind.Slot or StudioItemKind.Added ? PlannerIcons.Kind(i.TypeName) : null, () => ClickMember(item), null);
            }
        }

        private static string Esc(string s) => ModFields.TMPEscape(s ?? "");

        private static string Value(StudioItem i)
        {
            string costs = i.BaseCosts.Count > 0 ? string.Join("·", Enumerable.Range(0, i.BaseCosts.Count).Select(l => StudioEdits.Cost(i, l))) : "";
            string badge = Studio.LabelFor(i.Key).Badge;
            string state = i.Removed ? "removed" : i.Moved ? "moved" : "";
            var parts = new[] { costs, badge, state }.Where(s => !string.IsNullOrEmpty(s));
            return $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{Esc(string.Join(" · ", parts))}</color>";
        }

        private static void Click(StudioItem group)
        {
            bool wasOn = group.Members.All(StudioSelection.Contains);
            var op = StudioInput.Ctrl ? SelectOp.Toggle : StudioInput.Shift ? SelectOp.Add : SelectOp.Replace;
            if (wasOn && op == SelectOp.Replace) { StudioCamera.FrameItems([group]); return; }
            StudioSelection.Select([group.Key], op);
        }

        private static void ClickMember(StudioItem item)
        {
            var op = StudioInput.Ctrl ? SelectOp.Toggle : StudioInput.Shift ? SelectOp.Add : SelectOp.Replace;
            // Clicking the one selected row again frames it (the "double click").
            if (op == SelectOp.Replace && StudioSelection.Count == 1 && StudioSelection.Contains(item.Key)) { StudioCamera.FrameItems([item]); return; }
            StudioSelection.Select([item.Key], op);
        }

        private static void Row(ModPage page, string label, string value, bool on, Sprite icon, Action click, Action toggle)
        {
            var row = page.Row(label, value, (Func<string>)null, () => { click(); toggle?.Invoke(); }, icon);
            StudioDock.Compact(row, on, 90f);
        }
    }
}
