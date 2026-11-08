using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The chain preset popup (Inspector "Chain…", context menu): mode (branching from the castle, nearest neighbour, in
    selection order), the castle level the first links need and the level each link waits for. The arrows preview the
    result live (RequirementArrows.Preview); Apply is one undo step.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal static class ChainPopup
    {
        private static readonly ChainOptions options = new();
        private static ModPopup popup;
        private static List<string> keys = [];

        public static bool IsOpen => popup != null && ModPopup.Current == popup && popup.IsOpen;

        public static void Open(IReadOnlyList<string> selection)
        {
            keys = selection.Where(RequirementState.CanNeed).ToList();
            if (keys.Count == 0) { Notify.Show("Select slots to chain", NotifyKind.Info, 3f, "builder-link"); return; }
            if (keys.All(k => SlotKinds.Classify(Studio.Find(k)?.TypeName) == SlotKind.Economy && (Studio.Find(k)?.TypeName ?? "").ToLowerInvariant().Contains("house")))
                options.Mode = ChainMode.Branching;
            popup = ModPopup.Open($"Chain {keys.Count} slots", Build, width: 640f);
            UpdatePreview();
        }

        private static void Build(ModPage page)
        {
            page.FieldIndent = 220f;
            var modes = new[] { ChainMode.Branching, ChainMode.NearestNeighbour, ChainMode.SelectionOrder };
            page.Choice("Order", modes.Select(ChainPreset.Label).ToList(), () => System.Array.IndexOf(modes, options.Mode),
                i => { options.Mode = modes[i]; UpdatePreview(); },
                tooltip: () => options.Mode switch
                {
                    ChainMode.SelectionOrder => "The first selected needs the castle; each next one needs the one before",
                    ChainMode.NearestNeighbour => "One path: the slot nearest the castle first, then always the nearest one left",
                    _ => "A tree from the castle: each slot needs its nearest neighbour towards the castle",
                });
            int castleLevels = RequirementState.Levels(SlotRefs.Castle);
            page.Number("First link", () => options.CastleLevel, (int v) => { options.CastleLevel = v; UpdatePreview(); }, 1, castleLevels,
                format: v => $"Castle level {v}", tooltip: () => "The castle level the slots next to it need");
            page.Number("Each link", () => options.LinkLevel, (int v) => { options.LinkLevel = v; UpdatePreview(); }, 0, 3,
                format: v => v == 0 ? "Built" : $"Level {v + 1}", tooltip: () => "How far a slot must be upgraded before the next appears");
            page.Button("Apply", Apply);
        }

        public static List<(string slot, SlotRequirement requires)> Result()
        {
            var castle = RequirementState.Pos(SlotRefs.Castle) ?? Vector3.zero;
            var nodes = keys.Select(k => (k, item: Studio.Find(k))).Where(x => x.item != null)
                .Select(x => new ChainNode(RequirementState.Ref(x.k), x.item.Pos.x, x.item.Pos.z)).ToList();
            return ChainPreset.Build(nodes, castle.x, castle.z, options);
        }

        private static void UpdatePreview() => RequirementArrows.Preview = Result();

        public static void Apply()
        {
            var result = Result();
            popup?.Close();
            popup = null;
            RequirementArrows.Preview = null;
            if (result.Count == 0) return;
            Studio.Edit($"Chain {result.Count} slots ({ChainPreset.Label(options.Mode).ToLowerInvariant()})",
                lv => { foreach (var (slot, req) in result) LevelRequirements.Set(lv, slot, req); });
            RequirementState.Invalidate();
        }

        /// <summary>Drops the preview once the popup has closed (called every frame by the overlay).</summary>
        public static void Tick()
        {
            if (RequirementArrows.Preview != null && !IsOpen) { RequirementArrows.Preview = null; popup = null; }
        }

        /// <summary>For the dev command: the chain a mode would make for the selection.</summary>
        public static List<(string slot, SlotRequirement requires)> Plan(IReadOnlyList<string> selection, ChainMode mode, int castleLevel, int linkLevel)
        {
            keys = selection.Where(RequirementState.CanNeed).ToList();
            options.Mode = mode;
            options.CastleLevel = castleLevel;
            options.LinkLevel = linkLevel;
            return Result();
        }
    }
}
