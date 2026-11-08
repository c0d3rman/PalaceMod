using System;
using System.Collections.Generic;

/*
    Where a training building's units stand (their "home"), kept on walkable ground. Game-independent (unit-tested in
    tests/PalaceMod.Tests/UnitSpreadTests.cs): the caller supplies "the nearest walkable point to (x, z)" for each unit
    (its own pathfinding graph in play, the terrain grid or graph in the Studio).

    A unit already on the ground keeps its spot. One off it goes to the nearest walkable point, pushed a little further
    in (the nearest point is the very edge, where the unit's radius hangs over the water), and if that spot is taken
    by a unit placed before it, to the nearest free walkable spot in rings around it. So a formation that sat on a
    river bank stays a spread-out formation on the bank instead of a stack on one point.
*/
namespace PalaceMod.Builder.Level
{
    internal static class UnitSpread
    {
        /// <summary>A home within this many metres of the walkable area counts as on it (the navmesh is a 0.2 m
        /// voxel approximation, and a building's own cut can graze its units).</summary>
        public const float OnGround = 0.6f;
        /// <summary>Units closer than this share a spot.</summary>
        public const float Spacing = 1.3f;
        /// <summary>How far past the walkable edge a moved unit is pushed in.</summary>
        public const float Inset = 0.8f;
        private const int Rings = 8;
        /// <summary>Homes this close (m) are one spot: the same place in different upgrade choices.</summary>
        public const float SameSpot = 0.05f;

        /// <summary>The nearest walkable point to (x, z) for unit i, or null when there is no ground at all.</summary>
        public delegate (float x, float z)? Nearest(int unit, float x, float z);

        /// <summary>Whether (x, z) is on unit i's walkable ground.</summary>
        public static bool IsOnGround(Nearest nearest, int unit, float x, float z) =>
            nearest(unit, x, z) is (float nx, float nz) && Dist(nx, nz, x, z) <= OnGround;

        /// <summary>New homes for units at these points: unchanged where they're on the ground, else the nearest free
        /// walkable spot. moved[i] tells which changed.</summary>
        public static (float x, float z)[] Place(IReadOnlyList<(float x, float z)> homes, Nearest nearest, out bool[] moved, float spacing = Spacing)
        {
            int n = homes.Count;
            var result = new (float x, float z)[n];
            moved = new bool[n];
            var taken = new List<(float x, float z)>();
            var off = new List<int>();
            for (int i = 0; i < n; i++)
            {
                result[i] = homes[i];
                if (IsOnGround(nearest, i, homes[i].x, homes[i].z) || nearest(i, homes[i].x, homes[i].z) == null) taken.Add(homes[i]);
                else off.Add(i);
            }
            foreach (int i in off)
            {
                var (hx, hz) = homes[i];
                // Units on one spot (the same place in different upgrade choices: only one of them ever stands there)
                // go to the same new spot.
                int twin = -1;
                foreach (int j in off)
                {
                    if (j == i) break;
                    if (Dist(homes[j].x, homes[j].z, hx, hz) < SameSpot) { twin = j; break; }
                }
                if (twin >= 0) { result[i] = result[twin]; moved[i] = true; continue; }
                var (nx, nz) = nearest(i, hx, hz).Value;
                // Push in along the way from the home to the edge; keep the edge point if that leaves the ground.
                float dx = nx - hx, dz = nz - hz, d = (float)Math.Sqrt(dx * dx + dz * dz);
                var spot = (x: nx, z: nz);
                if (d > 1e-4f)
                {
                    float ix = nx + dx / d * Inset, iz = nz + dz / d * Inset;
                    if (IsOnGround(nearest, i, ix, iz) && nearest(i, ix, iz) is (float sx, float sz)) spot = (sx, sz);
                }
                if (!Free(spot.x, spot.z, taken, spacing)) spot = Search(i, spot, homes[i], nearest, taken, spacing) ?? spot;
                result[i] = spot;
                moved[i] = true;
                taken.Add(spot);
            }
            return result;
        }

        // The free walkable spot nearest the home, ring by ring around the first choice.
        private static (float x, float z)? Search(int unit, (float x, float z) c, (float x, float z) home, Nearest nearest, List<(float x, float z)> taken, float spacing)
        {
            for (int ring = 1; ring <= Rings; ring++)
            {
                float r = ring * spacing * 0.75f;
                int count = 6 * ring;
                (float x, float z)? best = null;
                float bestD = float.MaxValue;
                for (int k = 0; k < count; k++)
                {
                    double a = 2 * Math.PI * k / count;
                    float px = c.x + r * (float)Math.Cos(a), pz = c.z + r * (float)Math.Sin(a);
                    if (!IsOnGround(nearest, unit, px, pz) || nearest(unit, px, pz) is not (float sx, float sz)) continue;
                    if (!Free(sx, sz, taken, spacing)) continue;
                    float dh = Dist(sx, sz, home.x, home.z);
                    if (dh < bestD) { bestD = dh; best = (sx, sz); }
                }
                if (best != null) return best;
            }
            return null;
        }

        private static bool Free(float x, float z, List<(float x, float z)> taken, float spacing)
        {
            foreach (var t in taken) if (Dist(t.x, t.z, x, z) < spacing) return false;
            return true;
        }

        private static float Dist(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        // ---- Slot-relative offsets (LevelDef buildSlots units) ---------------------------------------------------

        /// <summary>A point in the slot's frame (x right, z forward, metres from its centre) turned by the slot's yaw.</summary>
        public static (float x, float z) ToWorld(float slotX, float slotZ, float yawDegrees, float ox, float oz)
        {
            double a = yawDegrees * Math.PI / 180.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            // Unity's yaw turns +z towards +x (clockwise seen from above).
            return (slotX + ox * c + oz * s, slotZ - ox * s + oz * c);
        }

        /// <summary>The inverse of ToWorld.</summary>
        public static (float x, float z) ToSlot(float slotX, float slotZ, float yawDegrees, float wx, float wz)
        {
            double a = yawDegrees * Math.PI / 180.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            float dx = wx - slotX, dz = wz - slotZ;
            return (dx * c - dz * s, dx * s + dz * c);
        }

        /// <summary>Offsets as stored in a level (rounded to cm).</summary>
        public static List<float[]> Store(IEnumerable<(float x, float z)> offsets)
        {
            var list = new List<float[]>();
            foreach (var (x, z) in offsets) list.Add([(float)Math.Round(x, 2), (float)Math.Round(z, 2)]);
            return list;
        }
    }
}
