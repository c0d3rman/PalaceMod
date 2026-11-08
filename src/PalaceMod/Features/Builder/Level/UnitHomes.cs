using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The homes of a training building's units on a Builder level (docs/builder-ux.md, "Unit positions").

    A barracks' units are children of its slot (UnitRespawnerForBuildings.units), placed by the map's author around the
    building. PathfindMovementPlayerunit.Start makes wherever a unit stands its home (homePosition and
    homePositionOriginal); it returns there every morning and respawns next to it. A slot the Builder moved, turned or
    copied somewhere else carries those offsets along, so its units could stand in a river or on a cliff: they appeared
    there, the build preview drew them there, and they only snapped to the navmesh once they walked.

    - ApplyOffsets (LevelOverlay, before Start): a level's own unit positions for a slot (buildSlots units).
    - Settle (BuilderLevelManager, right after the level-start rescan, so on the graphs the level plays on): every unit
      home off its own graph (Seeker.graphMask) is moved to the nearest free walkable spot (UnitSpread), the unit's
      transform too, so its first Start, the build preview and later respawns all use the new home. Units that already
      started (a building loaded built) get homePosition and homePositionOriginal set as well.
*/
namespace PalaceMod.Builder.Level
{
    /// <summary>One unit of a training slot as the Studio shows it: where it stands relative to the slot.</summary>
    internal sealed class UnitSpot
    {
        /// <summary>Metres from the slot's centre in the slot's frame (x right, z forward); y is the height difference.</summary>
        public float X, Y, Z;
        public GraphMask Mask;
        public string Name;
    }

    internal static class UnitHomes
    {
        private static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, Vector3> HomeRef = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, Vector3>("homePosition");
        private static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, Vector3> OriginalRef = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, Vector3>("homePositionOriginal");
        private static readonly AccessTools.FieldRef<PathfindMovementPlayerunit, Seeker> SeekerRef = AccessTools.FieldRefAccess<PathfindMovementPlayerunit, Seeker>("seeker");

        /// <summary>What the last Settle did (dev).</summary>
        public static string LastSettle = "";

        /// <summary>The units a slot trains, in the game's order (every respawner of every level, each once).</summary>
        public static List<Hp> Units(BuildSlot slot)
        {
            var list = new List<Hp>();
            if (slot == null) return list;
            foreach (var r in slot.GetComponentsInChildren<UnitRespawnerForBuildings>(true))
                foreach (var u in r.units ?? [])
                    if (u != null && !list.Contains(u)) list.Add(u);
            return list;
        }

        public static GraphMask MaskOf(Component unit) =>
            unit != null && unit.GetComponent<Seeker>() is Seeker s ? s.graphMask : GraphMask.FromGraphName("Player Units S");

        /// <summary>A slot's units relative to it (taken from the map's own slot, so the map's positions).</summary>
        public static List<UnitSpot> Spots(BuildSlot slot)
        {
            var t = slot.transform;
            float yaw = t.eulerAngles.y;
            return Units(slot).Select(u =>
            {
                var p = u.transform.position;
                var (x, z) = UnitSpread.ToSlot(t.position.x, t.position.z, yaw, p.x, p.z);
                return new UnitSpot { X = x, Z = z, Y = p.y - t.position.y, Mask = MaskOf(u), Name = UnitName(u) };
            }).ToList();
        }

        private static string UnitName(Hp u)
        {
            string n = u.name;
            int cut = n.IndexOfAny(['(', '[']);
            return (cut > 0 ? n.Substring(0, cut) : n).Trim();
        }

        /// <summary>Puts a slot's units at a level's own offsets (before Start). Returns how many moved.</summary>
        public static int ApplyOffsets(BuildSlot slot, List<float[]> offsets)
        {
            if (slot == null || offsets == null || offsets.Count == 0) return 0;
            var units = Units(slot);
            var t = slot.transform;
            float yaw = t.eulerAngles.y;
            int n = 0;
            for (int i = 0; i < units.Count && i < offsets.Count; i++)
            {
                var o = offsets[i];
                if (o == null || o.Length < 2 || float.IsNaN(o[0]) || float.IsNaN(o[1])) continue;
                var (x, z) = UnitSpread.ToWorld(t.position.x, t.position.z, yaw, Mathf.Clamp(o[0], -40f, 40f), Mathf.Clamp(o[1], -40f, 40f));
                var ut = units[i].transform;
                ut.position = new Vector3(x, ut.position.y, z);
                n++;
            }
            return n;
        }

        /// <summary>The nearest walkable point on a graph, or null without pathfinding.</summary>
        public static Vector3? Nearest(Vector3 p, GraphMask mask)
        {
            var astar = AstarPath.active;
            if (astar == null) return null;
            var nn = NearestNodeConstraint.Walkable;
            nn.graphMask = mask;
            var hit = astar.GetNearest(p, nn);
            return hit.node != null ? hit.position : null;
        }

        /// <summary>Moves every unit home that is off its walkable ground onto it. Call after the graphs are final.
        /// Returns how many units moved.</summary>
        public static int Settle(Scene scene)
        {
            if (AstarPath.active == null || !scene.IsValid()) return 0;
            int moved = 0, total = 0;
            var where = new List<string>();
            foreach (var slot in LayoutScene.Slots(scene))
            {
                var units = Units(slot);
                if (units.Count == 0) continue;
                total += units.Count;
                var movers = units.Select(u => u.GetComponent<PathfindMovementPlayerunit>()).ToList();
                var homes = units.Select((u, i) => Started(movers[i]) ? OriginalRef(movers[i]) : u.transform.position).ToList();
                var masks = units.Select(MaskOf).ToList();
                (float, float)? Near(int i, float x, float z) => Nearest(new Vector3(x, homes[i].y, z), masks[i]) is Vector3 v ? (v.x, v.z) : null;
                var placed = UnitSpread.Place(homes.Select(h => (h.x, h.z)).ToList(), Near, out bool[] changed);
                int here = 0;
                for (int i = 0; i < units.Count; i++)
                {
                    if (!changed[i]) continue;
                    var target = new Vector3(placed[i].x, homes[i].y, placed[i].z);
                    if (Nearest(target, masks[i]) is Vector3 g) target = new Vector3(placed[i].x, g.y, placed[i].z);
                    SetHome(units[i], movers[i], homes[i], target);
                    here++;
                }
                if (here > 0) { moved += here; where.Add($"{slot.name} {here}/{units.Count}"); }
            }
            LastSettle = $"{moved} of {total} unit homes moved onto walkable ground{(where.Count > 0 ? ": " + string.Join(", ", where) : "")}";
            return moved;
        }

        private static bool Started(PathfindMovementPlayerunit m) => m != null && SeekerRef(m) != null;

        private static void SetHome(Hp unit, PathfindMovementPlayerunit mover, Vector3 oldHome, Vector3 home)
        {
            var t = unit.transform;
            if (!Started(mover))
            {
                t.position = home;   // Start makes it the home; the build preview draws it here
                return;
            }
            // A unit that is standing at (or near) its old home goes along; one out fighting or following keeps going.
            Vector3 flat = t.position - oldHome;
            flat.y = 0f;
            if (flat.magnitude < 2f) t.position = home;
            if ((HomeRef(mover) - oldHome).sqrMagnitude < 0.01f) HomeRef(mover) = home;
            OriginalRef(mover) = home;
        }
    }
}
