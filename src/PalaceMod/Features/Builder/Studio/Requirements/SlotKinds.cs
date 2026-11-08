using System.Collections.Generic;
using System.Linq;
using System.Text;
using PalaceMod.Builder.Model;
using PalaceMod.Economy;
using UnityEngine;

/*
    "What's what" (docs/builder-ux.md): each Studio item's kind colour, icon, requirement badge and hover card, as a
    Studio label provider. The kind comes from the building's name (the Economy planner's icon words).
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal enum SlotKind { Castle, Economy, Military, Wall, Special, Spawn, Scenery }

    internal static class SlotKinds
    {
        // First match wins; longer names before the words they contain.
        private static readonly (string Word, SlotKind Kind)[] Words =
        [
            ("castle", SlotKind.Castle),
            ("house", SlotKind.Economy), ("mill", SlotKind.Economy), ("field", SlotKind.Economy), ("mine", SlotKind.Economy),
            ("harb", SlotKind.Economy), ("treasur", SlotKind.Economy), ("farm", SlotKind.Economy), ("fish", SlotKind.Economy),
            ("gate", SlotKind.Wall), ("wall", SlotKind.Wall), ("barricade", SlotKind.Wall), ("bridge", SlotKind.Wall),
            ("tower", SlotKind.Military), ("outpost", SlotKind.Military), ("archer", SlotKind.Military), ("barrack", SlotKind.Military),
            ("hero", SlotKind.Military), ("knight", SlotKind.Military), ("stable", SlotKind.Military),
        ];

        public static SlotKind Classify(string typeName)
        {
            string lower = (typeName ?? "").ToLowerInvariant();
            foreach (var (word, kind) in Words)
                if (lower.Contains(word)) return kind;
            return SlotKind.Special;
        }

        public static Color ColorOf(SlotKind kind) => kind switch
        {
            SlotKind.Castle => new Color(0.98f, 0.80f, 0.30f),     // gold
            SlotKind.Economy => new Color(0.98f, 0.90f, 0.50f),    // warm yellow
            SlotKind.Military => new Color(0.92f, 0.36f, 0.32f),   // red
            SlotKind.Wall => new Color(0.70f, 0.70f, 0.72f),       // grey
            SlotKind.Spawn => new Color(0.75f, 0.10f, 0.22f),      // crimson
            SlotKind.Scenery => new Color(0.66f, 0.86f, 0.62f),    // pale green
            _ => new Color(0.70f, 0.52f, 0.92f),                   // violet
        };

        public static string Label(SlotKind kind) => kind switch
        {
            SlotKind.Castle => "Castle",
            SlotKind.Economy => "Income",
            SlotKind.Military => "Military",
            SlotKind.Wall => "Walls and gates",
            SlotKind.Spawn => "Spawn line",
            SlotKind.Scenery => "Scenery",
            _ => "Special",
        };

        public static SlotKind KindOf(StudioItem item) => item.Kind switch
        {
            StudioItemKind.Line => SlotKind.Spawn,
            StudioItemKind.Prop => SlotKind.Scenery,
            _ => item.IsCastle ? SlotKind.Castle : Classify(item.TypeName),
        };

        /// <summary>The Studio label provider: colour, icon, cost chain, requirement badge and hover card.</summary>
        public static StudioLabel Provide(string key)
        {
            var item = Studio.Find(key);
            if (item == null || item.Kind == StudioItemKind.Group) return null;
            var kind = KindOf(item);
            var label = new StudioLabel { Color = ColorOf(kind) };
            if (item.Kind is not (StudioItemKind.Slot or StudioItemKind.Added)) return label;
            label.Icon = PlannerIcons.Kind(item.TypeName);
            var costs = Costs(key, item);
            if (costs.Count > 0) label.Costs = string.Join("·", costs) + "◉";
            label.Badge = Badge(key);
            label.Card = Card(key, item, kind, costs);
            return label;
        }

        /// <summary>Costs per level with the level's overrides.</summary>
        public static List<int> Costs(string key, StudioItem item)
        {
            var level = Studio.Level;
            var costs = item.BaseCosts.ToList();
            string r = RequirementState.Ref(key);
            var edits = SlotRefs.IsAdded(r) ? LevelRequirements.FindAdd(level, r)?.Upgrades : LevelRequirements.FindEdit(level, r)?.Upgrades;
            for (int i = 0; i < costs.Count && i < (edits?.Count ?? 0); i++)
                if (edits[i]?.Cost is int c) costs[i] = c;
            return costs;
        }

        /// <summary>"♜2" for castle level 2, "⤳ House 6" for a slot, "★" for from the start; null for the castle.</summary>
        public static string Badge(string key)
        {
            string r = RequirementState.Ref(key);
            if (r == null || r == RequirementState.Castle) return null;
            if (!RequirementState.Effective.TryGetValue(r, out var req)) return null;
            if (req?.Slot == null) return "★";
            if (req.Slot == RequirementState.Castle) return $"♜{req.Level + 1}";
            return $"⤳ {RequirementState.Name(req.Slot)}{(req.Level > 0 ? $" {req.Level + 1}" : "")}";
        }

        private static string Card(string key, StudioItem item, SlotKind kind, List<int> costs)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Label(kind)}{(item.Kind == StudioItemKind.Added ? " · added" : "")}{(item.Removed ? " · removed" : "")}");
            if (costs.Count > 0) sb.AppendLine("Cost " + string.Join(" / ", costs.Select(c => c + "◉")));
            var income = RequirementState.Income(key);
            if (income.Any(i => i != 0))
            {
                int total = 0;
                sb.AppendLine("Income " + string.Join(" / ", income.Select(i => (total += i) + "◉")));
            }
            string needs = RequirementState.DescribeFor(key);
            if (needs != null && !item.IsCastle) sb.AppendLine("Needs " + needs);
            string r = RequirementState.Ref(key);
            var eff = RequirementState.Effective;
            var unlocks = LevelRequirements.Dependents(eff, r).Select(RequirementState.Name).ToList();
            if (unlocks.Count > 0) sb.AppendLine("Unlocks " + string.Join(", ", unlocks));
            if (LevelRequirements.Cycles(eff).Any(l => l.Contains(r))) sb.AppendLine("Problem: in a requirement loop");
            var own = LevelRequirements.Explicit(Studio.Level, r);
            if (own?.Slot != null && eff.TryGetValue(r, out var now) && now == null) sb.AppendLine($"Problem: needs {RequirementState.Name(own.Slot)}, which isn't on the map");
            return sb.ToString().TrimEnd();
        }
    }
}
