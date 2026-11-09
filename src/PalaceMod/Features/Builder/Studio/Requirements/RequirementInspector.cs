using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Inspector's requirement section (docs/builder-ux.md, "Editing"): for the selected slots, what they need (one
    prerequisite each: from the start, a castle level, or a slot at a level), the upgrade cap against the castle's
    level, what a single slot unlocks, the built-look preview level and the chain preset. Mixed values show "—"; every
    change applies to all selected slots as one undo step.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal static class RequirementInspector
    {
        public const string Mixed = "—";

        public static bool AppliesTo(IReadOnlyList<string> keys) => keys.Any(k => RequirementState.Ref(k) != null && Studio.Find(k) != null);

        public static void Build(ModPage page, IReadOnlyList<string> keys)
        {
            var deps = keys.Where(RequirementState.CanNeed).ToList();
            var slots = keys.Where(k => RequirementState.Ref(k) != null).ToList();
            page.Header("Requirements");

            if (deps.Count > 0)
            {
                page.Row("Needs", NeedsText(deps), tooltip: "What makes them appear: from the start, a castle level, or another slot",
                    onClick: () => OpenNeeds(deps, null));
                // The prerequisite's level, when they all need the same slot (or the castle).
                var reqs = deps.Select(k => RequirementState.Effective.TryGetValue(RequirementState.Ref(k), out var r) ? r : null).ToList();
                if (reqs.All(r => r?.Slot != null && r.Slot == reqs[0].Slot))
                {
                    string target = reqs[0].Slot;
                    bool castle = target == RequirementState.Castle;
                    int levels = RequirementState.Levels(target);
                    if (levels > 1)
                    {
                        int Current() => reqs.All(r => r.Level == reqs[0].Level) ? reqs[0].Level : -1;
                        page.Stepper(castle ? "Castle level" : "After", () => Current() < 0 ? Mixed : castle ? $"{Current() + 1}" : Current() == 0 ? "Built" : $"Level {Current() + 1}",
                            step => RequirementEdits.Apply(deps, new SlotRequirement { Slot = castle ? SlotRefs.Castle : target, Level = Mathf.Clamp(Math.Max(0, Current()) + step, 0, levels - 1) }, null),
                            tooltip: castle ? "The castle level they appear at" : "How far the prerequisite must be upgraded");
                    }
                }

                var caps = deps.Select(RequirementState.Cap).Distinct().ToList();
                bool capped = caps.All(c => c != -100 && c > -100);
                page.Toggle("Upgrades ≤ castle level", () => capped, on => RequirementEdits.ApplyCap(deps, on ? 0 : -100),
                    () => (caps.Count > 1 ? "Mixed: some are capped. " : "") + Descriptions.For(DescKind.Flag, "upgradeCap"));
                if (capped)
                    page.Number("Allowance", () => caps.Count == 1 ? caps[0] : 0, (int v) => RequirementEdits.ApplyCap(deps, v), 0, 5,
                        format: (int v) => caps.Count > 1 ? Mixed : v == 0 ? "None" : $"+{v}", tooltip: () => Descriptions.For(DescKind.Flag, "Allowance"));
                page.Button(deps.Count > 1 ? "Chain…" : "Chain from here…", () => ChainPopup.Open(deps.Count > 1 ? deps : SameKind(deps[0])),
                    tooltip: "Link the selection in a chain from the castle");
            }

            if (slots.Count == 1)
            {
                string r = RequirementState.Ref(slots[0]);
                var unlocks = LevelRequirements.Dependents(RequirementState.Effective, r);
                page.Row("Unlocks", unlocks.Count == 0 ? "Nothing" : unlocks.Count == 1 ? "1 slot" : $"{unlocks.Count} slots",
                    tooltip: unlocks.Count == 0 ? null : string.Join(", ", unlocks.Select(RequirementState.Name)) + "\nClick to select them", onClick: unlocks.Count == 0 ? null : () => Studio.Select(unlocks.Select(RequirementState.Key)));
                var item = Studio.Find(slots[0]);
                int levels = Level.LayoutScene.BuiltLevels(item?.Proxy);
                if (levels > 0)
                    page.Stepper("Preview", () => BuiltPreview.LevelOf(slots[0]) == 0 ? "Blueprint" : $"Level {BuiltPreview.LevelOf(slots[0])}",
                        step => BuiltPreview.SetLevel(slots[0], Mathf.Clamp(BuiltPreview.LevelOf(slots[0]) + step, 0, levels)),
                        tooltip: "Show the building as it looks at a level");
            }
        }

        private static string NeedsText(List<string> deps)
        {
            var texts = deps.Select(RequirementState.DescribeFor).Distinct().ToList();
            return texts.Count == 1 ? texts[0] ?? "" : Mixed;
        }

        /// <summary>All slots of the same kind as this one (a chain "from here" over the map's houses, say).</summary>
        private static List<string> SameKind(string key)
        {
            var type = Studio.Find(key)?.TypeName;
            return Studio.Items.Where(i => i.TypeName == type && !i.Removed && RequirementState.CanNeed(i.Key)).Select(i => i.Key).ToList();
        }

        /// <summary>The "Needs" chooser: from the start, castle level 1-N, or a slot picked on the map.</summary>
        public static void OpenNeeds(IReadOnlyList<string> deps, Vector2? at)
        {
            var list = deps.Where(RequirementState.CanNeed).ToList();
            if (list.Count == 0) return;
            int castleLevels = RequirementState.Levels(SlotRefs.Castle);
            ModPopup.Open("Needs", page =>
            {
                page.Button("From the start", () => { ModPopup.Current?.Close(); RequirementEdits.Apply(list, LevelRequirements.FromStart, null); });
                for (int i = 1; i <= castleLevels; i++)
                {
                    int n = i;
                    page.Button($"Castle level {n}", () => { ModPopup.Current?.Close(); RequirementEdits.Apply(list, LevelRequirements.CastleLevel(n), null); });
                }
                page.Button("A slot on the map…", () =>
                {
                    ModPopup.Current?.Close();
                    Studio.PickOnMap("Click what they need", k => RequirementState.CanBeNeeded(k) && !list.Contains(k), picked =>
                    {
                        string target = RequirementState.Ref(picked);
                        bool castle = target == RequirementState.Castle;
                        int levels = RequirementState.Levels(target);
                        void Done(int level) => RequirementEdits.Apply(list, new SlotRequirement { Slot = castle ? SlotRefs.Castle : target, Level = level }, null);
                        if (levels <= 1 && !castle) Done(0);
                        else RequirementEdits.PickLevel(castle, target, levels, Input.mousePosition, Done);
                    });
                });
            }, screenPoint: at, width: 340f);
        }
    }
}
