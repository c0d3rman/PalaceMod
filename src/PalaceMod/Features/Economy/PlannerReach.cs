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
        // Found by flood-filling the walkable ground from the castle with each bridge built and not (a dev-only check
        // in a game instance); every other level has nothing out of reach this way.
        private static readonly Dictionary<string, (string Slot, string[][] AnyOf)[]> Levels = new()
        {
            // Uferwind: the south bank. Bridge E (21,-72) or bridge W (-9,-114) crosses to it; the middle bridge (10,-103)
            // joins the two halves, so each half is reached by its own bridge or by the other one plus the middle one.
            ["Uferwind"] =
            [
                ("f2cd6fed-5b4c-48ab-b116-02c61a6b638b", [[BridgeE], [BridgeW]]),                      // Bridge, middle (10,-103)
                ("bcb61238-7db5-448a-ab40-b75fd6971f29", [[BridgeE], [BridgeW, BridgeMiddle]]),        // Archery Range (32,-101)
                ("631a03bb-0047-4bcf-9be4-e9d069f952ad", [[BridgeE], [BridgeW, BridgeMiddle]]),        // Defense Tower (18,-104)
                ("175b3761-8914-4391-ba8e-d005615cef8b", [[BridgeE], [BridgeW, BridgeMiddle]]),        // Defense Tower (38,-113)
                ("34f44f5c-51aa-4775-9642-5a6209dd3e8e", [[BridgeE], [BridgeW, BridgeMiddle]]),        // Defense Tower (49,-85)
                ("1116ba59-3c89-4e3a-a9da-59c15400207e", [[BridgeE], [BridgeW, BridgeMiddle]]),        // Blacksmith (36,-87)
                ("35fa293b-8ce3-42a1-9c5d-dc5c800e51dd", [[BridgeW], [BridgeE, BridgeMiddle]]),        // Harbour (-29,-142)
                ("897372d0-9cd6-445d-9999-534b702d1061", [[BridgeW], [BridgeE, BridgeMiddle]]),        // Archery Range (1,-141)
                ("274b4485-bd2e-4660-a847-76bc0a29b0a4", [[BridgeW], [BridgeE, BridgeMiddle]]),        // Defense Tower (-39,-141)
                ("036c8ce0-3d75-4a9a-97e8-9321bd2ebafb", [[BridgeW], [BridgeE, BridgeMiddle]]),        // Defense Tower (17,-148)
                ("c3c6b7c1-63a4-4764-bc9a-dec87e3a7548", [[BridgeW], [BridgeE, BridgeMiddle]]),        // Defense Tower (-2,-163)
                ("73cb2925-0e1a-424f-899b-187aae0d1642", [[BridgeW], [BridgeE, BridgeMiddle]]),        // Defense Tower (9,-117)
            ],
        };

        private const string BridgeE = "7cf210e6-3e9b-435d-972c-52d41e43c53f", BridgeW = "366bdb95-dab5-4de6-9024-45af73830f55",
            BridgeMiddle = "f2cd6fed-5b4c-48ab-b116-02c61a6b638b";

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
