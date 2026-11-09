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
        // Uferwind: the south bank. Bridge E (21,-72) or bridge W (-9,-114) crosses to it; the middle bridge (10,-103)
        // joins the two halves, so each half is reached by its own bridge or by the other one plus the middle one.
        // Declared before Levels: static fields initialise in order.
        private static readonly (string Slot, string[][] AnyOf)[] Uferwind =
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
            ];

        // scene -> (slot, sets of slots any one of which, all built, reaches it)
        // Found with the king's walk test (`dev.py cmd "reach run <file>"`, Core/ReachCheckManager.cs): a flood fill of
        // the walkable ground from the castle with each bridge built and not, on every scene in the build, Mastery and
        // challenge variants included. Only Uferwind and its three variants have slots out of reach; every other scene
        // reaches every slot with nothing built.
        private static readonly Dictionary<string, (string Slot, string[][] AnyOf)[]> Levels = new()
        {
            ["Uferwind"] = Uferwind,
            // Same ground and slot GUIDs as Uferwind (checked: the same 12 slots need the same bridges).
            ["Uferwind Defense Puzzle"] = Uferwind,
            // Same ground and bridges; of the south bank's slots only the middle bridge is left, needing either bridge.
            ["Uferwind Lightning Staff Challenge"] = Uferwind,
            // Its own GUIDs: bridges W 3405e7c6 (-9,-114), middle 35a6f8a7 (10,-103), E 4b1cf36a (21,-72).
            ["Uferwind Hero Commander"] =
            [
                ("35a6f8a7-5127-46fd-8375-779da496c5d2", [[HcE], [HcW]]),                     // Bridge, middle (10,-103)
                ("00d1ae87-a033-49f2-9a60-667d9983c0a8", [[HcW], [HcE, HcMiddle]]),           // Hero's Quarter (1,-139)
                ("91019012-d3b9-4cb5-b4a6-b3196a01944f", [[HcE], [HcW, HcMiddle]]),           // Hero's Quarter (29,-97)
                ("8491fa56-d7a7-4e9d-a394-928803310bfc", [[HcE], [HcW, HcMiddle]]),           // Blacksmith (36,-87)
            ],
        };

        private const string HcE = "4b1cf36a-9cd1-42ad-a24e-d3e67e412bd8", HcW = "3405e7c6-0154-4e9f-8e5f-c3455249f798",
            HcMiddle = "35a6f8a7-5127-46fd-8375-779da496c5d2";


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
