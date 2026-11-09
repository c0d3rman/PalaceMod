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

    Never across the water: with a Reach (a flood over the walkable ground from the slot, Flood), a new home must be
    reachable from the building by a walk not much longer than the straight line (1.5x + 4 m), so ground on the far
    bank of a river (reachable only round by a ford, or not at all) doesn't count. A unit whose nearest ground is over
    there walks back towards its building and takes the first such ground on the way.
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
        /// walkable spot (with reach: one the unit's building reaches without crossing water, see Flood). moved[i] tells
        /// which changed.</summary>
        public static (float x, float z)[] Place(IReadOnlyList<(float x, float z)> homes, Nearest nearest, out bool[] moved, float spacing = Spacing,
            Func<int, Reach> reach = null)
        {
            int n = homes.Count;
            var result = new (float x, float z)[n];
            moved = new bool[n];
            var taken = new List<(float x, float z)>();
            var off = new List<int>();
            for (int i = 0; i < n; i++)
            {
                result[i] = homes[i];
                var (hx, hz) = homes[i];
                bool on = IsOnGround(nearest, i, hx, hz) || nearest(i, hx, hz) == null;
                // Standing in the water within reach of the far bank counts as off: that bank isn't the building's.
                if (on && reach != null && nearest(i, hx, hz) is (float nx, float nz) && Dist(nx, nz, hx, hz) > 0.05f && !reach(i).Reachable(nx, nz)) on = false;
                if (on) taken.Add(homes[i]);
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
                var r = reach?.Invoke(i);
                var (nx, nz) = nearest(i, hx, hz).Value;
                // From the far side of the water: walk back towards the building to the first ground it reaches.
                if (r != null && !r.Reachable(nx, nz) && r.TowardsBuilding(i, hx, hz, nearest) is (float bx, float bz)) (nx, nz) = (bx, bz);
                // Push in along the way from the home to the edge; keep the edge point if that leaves the ground.
                float dx = nx - hx, dz = nz - hz, d = (float)Math.Sqrt(dx * dx + dz * dz);
                var spot = (x: nx, z: nz);
                if (d > 1e-4f)
                {
                    float ix = nx + dx / d * Inset, iz = nz + dz / d * Inset;
                    if (IsOnGround(nearest, i, ix, iz) && nearest(i, ix, iz) is (float sx, float sz) && (r == null || r.Reachable(sx, sz))) spot = (sx, sz);
                }
                if (!Free(spot.x, spot.z, taken, spacing)) spot = Search(i, spot, homes[i], nearest, taken, spacing, r) ?? spot;
                result[i] = spot;
                moved[i] = true;
                taken.Add(spot);
            }
            return result;
        }

        // The free walkable spot nearest the home, ring by ring around the first choice.
        private static (float x, float z)? Search(int unit, (float x, float z) c, (float x, float z) home, Nearest nearest, List<(float x, float z)> taken, float spacing, Reach reach)
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
                    if (!Free(sx, sz, taken, spacing) || (reach != null && !reach.Reachable(sx, sz))) continue;
                    float dh = Dist(sx, sz, home.x, home.z);
                    if (dh < bestD) { bestD = dh; best = (sx, sz); }
                }
                if (best != null) return best;
            }
            return null;
        }

        // ---- What a building reaches without crossing water ------------------------------------------------------------

        /// <summary>Walking distances over the walkable ground from a building: a flood on a 1 m lattice round it.</summary>
        public sealed class Reach
        {
            public const float Step = 1f, Radius = 24f, Detour = 1.5f, Slack = 4f;
            private readonly float ax, az;
            private readonly int n;           // lattice points per side (2 * half + 1)
            private readonly int half;
            private readonly float[] dist;    // walking metres from the start; +inf where not reached or off the ground
            public readonly (float x, float z)? Start;

            internal Reach(float ax, float az, Func<float, float, bool> onGround)
            {
                this.ax = ax; this.az = az;
                half = (int)(Radius / Step);
                n = 2 * half + 1;
                dist = new float[n * n];
                for (int i = 0; i < dist.Length; i++) dist[i] = float.PositiveInfinity;
                var ok = new sbyte[n * n];   // 0 unknown, 1 ground, -1 not
                bool Ok(int ix, int iz)
                {
                    int k = iz * n + ix;
                    if (ok[k] == 0) ok[k] = onGround(X(ix), Z(iz)) ? (sbyte)1 : (sbyte)-1;
                    return ok[k] > 0;
                }
                // Start at the ground point nearest the building (its centre is often under its own navmesh cut).
                int sx = -1, sz = -1;
                for (int ring = 0; ring <= 10 && sx < 0; ring++)
                {
                    float best = float.MaxValue;
                    for (int dz = -ring; dz <= ring; dz++)
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring || !Ok(half + dx, half + dz)) continue;
                            float d = dx * dx + dz * dz;
                            if (d < best) { best = d; sx = half + dx; sz = half + dz; }
                        }
                }
                if (sx < 0) return;
                Start = (X(sx), Z(sz));
                // Dijkstra over 8 neighbours (a binary heap would be quicker; the lattice is small).
                var open = new SortedSet<(float d, int k)>();
                dist[sz * n + sx] = (float)Math.Sqrt((X(sx) - ax) * (X(sx) - ax) + (Z(sz) - az) * (Z(sz) - az));
                open.Add((dist[sz * n + sx], sz * n + sx));
                while (open.Count > 0)
                {
                    var (d, k) = open.Min;
                    open.Remove(open.Min);
                    if (d > dist[k]) continue;
                    int kx = k % n, kz = k / n;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int x2 = kx + dx, z2 = kz + dz;
                            if (x2 < 0 || z2 < 0 || x2 >= n || z2 >= n || !Ok(x2, z2)) continue;
                            // A diagonal step needs both of its sides on the ground (no squeezing between two cells of water).
                            if (dx != 0 && dz != 0 && (!Ok(kx + dx, kz) || !Ok(kx, kz + dz))) continue;
                            float nd = d + (dx != 0 && dz != 0 ? 1.4142f : 1f) * Step;
                            int k2 = z2 * n + x2;
                            if (nd >= dist[k2]) continue;
                            dist[k2] = nd;
                            open.Add((nd, k2));
                        }
                }
            }

            private float X(int ix) => ax + (ix - half) * Step;
            private float Z(int iz) => az + (iz - half) * Step;

            /// <summary>Walking metres from the building to (x, z), +inf when the flood didn't get there.</summary>
            public float WalkTo(float x, float z)
            {
                int ix = (int)Math.Round((x - ax) / Step) + half, iz = (int)Math.Round((z - az) / Step) + half;
                if (ix < 0 || iz < 0 || ix >= n || iz >= n) return float.PositiveInfinity;
                // The lattice point itself, or a reached neighbour (a spot right at the edge rounds onto the water).
                float best = float.PositiveInfinity;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x2 = ix + dx, z2 = iz + dz;
                        if (x2 < 0 || z2 < 0 || x2 >= n || z2 >= n) continue;
                        float d = dist[z2 * n + x2];
                        if (float.IsPositiveInfinity(d)) continue;
                        float gx = X(x2) - x, gz = Z(z2) - z;
                        best = Math.Min(best, d + (float)Math.Sqrt(gx * gx + gz * gz));
                    }
                return best;
            }

            /// <summary>Whether the building reaches (x, z) by a walk not much longer than the straight line.</summary>
            public bool Reachable(float x, float z)
            {
                if (Start == null) return true;   // no ground round the building: nothing to compare with
                float straight = Dist(ax, az, x, z);
                return WalkTo(x, z) <= straight * Detour + Slack;
            }

            /// <summary>From a home, the first reachable ground on the straight way back to the building (else the ground
            /// next to the building).</summary>
            public (float x, float z)? TowardsBuilding(int unit, float hx, float hz, Nearest nearest)
            {
                if (Start == null) return null;
                float len = Dist(hx, hz, ax, az);
                for (float t = 0f; t <= len; t += Step * 0.5f)
                {
                    float px = hx + (ax - hx) * t / Math.Max(len, 1e-3f), pz = hz + (az - hz) * t / Math.Max(len, 1e-3f);
                    if (IsOnGround(nearest, unit, px, pz) && nearest(unit, px, pz) is (float gx, float gz) && Reachable(gx, gz)) return (gx, gz);
                }
                return Start;
            }
        }

        /// <summary>The walkable ground a building at (x, z) reaches (onGround: whether a point is on its units' ground).</summary>
        public static Reach Flood(float x, float z, Func<float, float, bool> onGround) => new(x, z, onGround);

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
