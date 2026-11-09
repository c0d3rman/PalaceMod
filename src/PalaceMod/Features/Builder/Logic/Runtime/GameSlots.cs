using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Build slots for the expression language: empty slots as selector candidates (filters that say `unbuilt` or
    `include:unbuilt`; the game's tag lists only hold built buildings), and slot properties the core can't read
    (`.cost`). Installed into LogicServices when a world starts.
*/
namespace PalaceMod.Builder.Logic
{
    internal static class GameSlots
    {
        private static readonly List<BuildSlot> slots = [];
        private static int scannedFrame = -1;
        private static Scene scannedScene;

        public static void Install()
        {
            LogicServices.UnbuiltSlots = Unbuilt;
            LogicServices.GameProperty = Property;
        }

        /// <summary>Every build slot in the loaded level (rescanned at most once a frame).</summary>
        private static List<BuildSlot> All()
        {
            var scene = SceneManager.GetActiveScene();
            if (scannedFrame == Time.frameCount && scannedScene == scene) return slots;
            scannedFrame = Time.frameCount;
            scannedScene = scene;
            slots.Clear();
            foreach (var s in Object.FindObjectsOfType<BuildSlot>(true))
                if (s != null && s.gameObject.scene == scene) slots.Add(s);
            return slots;
        }

        private static void Unbuilt(Vec center, float radius, List<LogicEntity> into)
        {
            var world = LogicWorld.Current;
            if (world == null) return;
            float r2 = radius * radius;
            foreach (var s in All())
            {
                if (s == null || s.Level > 0) continue;
                // The slot's building object (inactive while empty): the same entity the built building will be.
                var go = s.BuildingParent != null ? s.BuildingParent : s.gameObject;
                var p = go.transform.position;
                if (radius > 0 && (p.x - center.X) * (p.x - center.X) + (p.z - center.Z) * (p.z - center.Z) > r2) continue;
                var e = LogicGameWorld.EntityOf(world, go);
                if (e != null && !into.Contains(e)) into.Add(e);
            }
        }

        private static Value? Property(LogicEntity e, string name)
        {
            switch (name)
            {
                case "cost":
                {
                    var slot = SlotOf(e);
                    // The price of the next build or upgrade; none when there is no next step (maxed, or capped).
                    return slot != null && slot.CanBeUpgraded ? Value.Number(slot.NextUpgradeOrBuildCost) : Value.None;
                }
            }
            return null;
        }

        /// <summary>The build slot behind an entity (a slot, or a building under one), or null.</summary>
        internal static BuildSlot SlotOf(LogicEntity e)
        {
            var go = e?.Go();
            if (go == null) return null;
            return go.GetComponentInParent<BuildSlot>(true) ?? go.GetComponentInChildren<BuildSlot>(true);
        }
    }
}
