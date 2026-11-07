using System.Collections.Generic;
using System.Linq;

/*
    Slots a map keeps out of the player's reach until some other slot is built (a bridge over water), which the game
    doesn't encode as an unlock (BuildSlot.ActivatorBuilding): the slot is active, but the king can't walk to it. Level
    data, by scene and the slots' SaveLoadEntity GUIDs (EconSlot.Id); each slot needs ANY one of its sets of slots
    built, ALL of that set. The planner treats an unmet one like a lock (EconSim's Blocker: "Out of reach until ...").
*/
namespace PalaceMod.Economy
{
    internal static class PlannerReach
    {
        // scene -> (slot, sets of slots any one of which, all built, reaches it)
        private static readonly Dictionary<string, (string Slot, string[][] AnyOf)[]> Levels = new()
        {
        };

        /// <summary>Fills EconSlot.Reach for the level's slots (slots missing from the map are left out).</summary>
        public static void Apply(EconWorld w)
        {
            if (w?.Level == null || !Levels.TryGetValue(w.Level, out var rules)) return;
            foreach (var (slot, anyOf) in rules)
            {
                var target = w.Find(slot);
                if (target == null) continue;
                var sets = anyOf.Select(set => set.Select(id => w.Find(id)?.Index ?? -1).ToArray()).Where(set => set.All(i => i >= 0)).ToList();
                if (sets.Count > 0) target.Reach = sets;
            }
        }

        /// <summary>The rules for a level, for the dev check: (slot, sets).</summary>
        public static IEnumerable<(string Slot, string[][] AnyOf)> For(string level) =>
            level != null && Levels.TryGetValue(level, out var rules) ? rules : [];
    }
}
