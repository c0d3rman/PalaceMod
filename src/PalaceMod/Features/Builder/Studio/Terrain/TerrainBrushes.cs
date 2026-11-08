using System;
using PalaceMod.Builder.Level;

/*
    Terrain brush maths on a TerrainGrid (pure C#, unit-tested). Every brush takes a world point and a radius and
    returns the cells it touched (Dirty), so the ground layer re-meshes only those chunks.

    Height brushes work on corners with a falloff: weight 1 inside (1 - softness) of the radius, easing to 0 at the
    edge. Softness 0 is a hard edge (plateaus with cliff sides); 1 is a smooth hill. Cell brushes (material, blocked,
    water) take every cell whose centre is inside the radius.
*/
namespace PalaceMod.Builder.Level
{
    internal struct Dirty
    {
        public int X0, Z0, X1, Z1;   // inclusive cell range; empty when X1 < X0
        public static Dirty None => new() { X0 = 0, Z0 = 0, X1 = -1, Z1 = -1 };
        public bool Empty => X1 < X0 || Z1 < Z0;

        public void Add(int x, int z)
        {
            if (Empty) { X0 = X1 = x; Z0 = Z1 = z; return; }
            X0 = Math.Min(X0, x); Z0 = Math.Min(Z0, z); X1 = Math.Max(X1, x); Z1 = Math.Max(Z1, z);
        }

        public void Add(Dirty o)
        {
            if (o.Empty) return;
            Add(o.X0, o.Z0);
            Add(o.X1, o.Z1);
        }
    }

    internal enum CellLayer { Material, Blocked, Water }

    internal static class TerrainBrushes
    {
        /// <summary>Brush weight at a distance: 1 in the hard core, smoothstep down to 0 at the radius.</summary>
        public static float Weight(float dist, float radius, float softness)
        {
            if (dist >= radius) return 0f;
            float core = radius * (1f - TerrainGrid.Clamp(softness, 0f, 1f));
            if (dist <= core) return 1f;
            float t = (dist - core) / (radius - core);
            return 1f - t * t * (3f - 2f * t);
        }

        /// <summary>Adds amount metres (negative lowers) under the brush, weighted.</summary>
        public static Dirty Raise(TerrainGrid g, float x, float z, float radius, float softness, float amount) =>
            Corners(g, x, z, radius, softness, (h, w) => h + amount * w);

        /// <summary>Moves heights toward a relative target by strength (0-1) per application; 1 sets it outright.</summary>
        public static Dirty Flatten(TerrainGrid g, float x, float z, float radius, float softness, float target, float strength = 1f) =>
            Corners(g, x, z, radius, softness, (h, w) => TerrainGrid.Lerp(h, target, TerrainGrid.Clamp(strength, 0f, 1f) * w));

        /// <summary>Averages each corner with its neighbours, weighted.</summary>
        public static Dirty Smooth(TerrainGrid g, float x, float z, float radius, float softness, float strength = 0.5f)
        {
            var src = (float[])g.Heights.Clone();
            return CornersAt(g, x, z, radius, softness, (cx, cz, h, w) =>
            {
                float sum = 0f;
                int n = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx > g.W || nz > g.H) continue;
                        sum += src[g.Corner(nx, nz)];
                        n++;
                    }
                return TerrainGrid.Lerp(h, sum / n, TerrainGrid.Clamp(strength, 0f, 1f) * w);
            });
        }

        /// <summary>A ramp: heights along the segment a→b go linearly from ha to hb (relative), over its width.</summary>
        public static Dirty Ramp(TerrainGrid g, float ax, float az, float ha, float bx, float bz, float hb, float width)
        {
            var dirty = Dirty.None;
            float dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
            if (len2 < 0.01f) return dirty;
            float half = width / 2f;
            for (int cz = 0; cz <= g.H; cz++)
                for (int cx = 0; cx <= g.W; cx++)
                {
                    float px = g.CornerX(cx) - ax, pz = g.CornerZ(cz) - az;
                    float t = (px * dx + pz * dz) / len2;
                    if (t < 0f || t > 1f) continue;
                    float ox = px - t * dx, oz = pz - t * dz;
                    if (ox * ox + oz * oz > half * half) continue;
                    g.Heights[g.Corner(cx, cz)] = g.ClampHeight(TerrainGrid.Lerp(ha, hb, t));
                    TouchCorner(g, cx, cz, ref dirty);
                }
            return dirty;
        }

        /// <summary>Sets a cell layer for every cell whose centre is under the brush.</summary>
        public static Dirty Paint(TerrainGrid g, float x, float z, float radius, CellLayer layer, byte value)
        {
            var dirty = Dirty.None;
            Range(g, x, z, radius, out int x0, out int z0, out int x1, out int z1, cells: true);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    float px = g.CornerX(cx) + g.Cell / 2f - x, pz = g.CornerZ(cz) + g.Cell / 2f - z;
                    // A brush smaller than a cell still paints the cell it's on.
                    if (px * px + pz * pz > radius * radius && !(Math.Abs(px) <= g.Cell / 2f && Math.Abs(pz) <= g.Cell / 2f)) continue;
                    int i = g.CellIndex(cx, cz);
                    switch (layer)
                    {
                        case CellLayer.Material: g.Materials[i] = value; break;
                        case CellLayer.Blocked: g.Blocked[i] = value != 0; break;
                        case CellLayer.Water: g.Water[i] = value != 0; break;
                    }
                    dirty.Add(cx, cz);
                }
            return dirty;
        }

        /// <summary>Water cells under the brush (or dry land with on false). The ground under new water is dug to at
        /// least WaterDepth below the water level, so the banks slope down to it.</summary>
        public static Dirty Water(TerrainGrid g, float x, float z, float radius, bool on)
        {
            var dirty = Paint(g, x, z, radius, CellLayer.Water, (byte)(on ? 1 : 0));
            if (!on || dirty.Empty) return dirty;
            float floor = g.ClampHeight(g.WaterLevel - WaterDepth);
            for (int cz = dirty.Z0; cz <= dirty.Z1; cz++)
                for (int cx = dirty.X0; cx <= dirty.X1; cx++)
                {
                    if (!g.Water[g.CellIndex(cx, cz)]) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        int i = g.Corner(cx + (k & 1), cz + (k >> 1));
                        if (g.Heights[i] > floor) g.Heights[i] = floor;
                    }
                }
            dirty.Add(Math.Max(0, dirty.X0 - 1), Math.Max(0, dirty.Z0 - 1));
            dirty.Add(Math.Min(g.W - 1, dirty.X1 + 1), Math.Min(g.H - 1, dirty.Z1 + 1));
            return dirty;
        }

        public const float WaterDepth = 1f;

        private static Dirty Corners(TerrainGrid g, float x, float z, float radius, float softness, Func<float, float, float> f) =>
            CornersAt(g, x, z, radius, softness, (_, _, h, w) => f(h, w));

        private static Dirty CornersAt(TerrainGrid g, float x, float z, float radius, float softness, Func<int, int, float, float, float> f)
        {
            var dirty = Dirty.None;
            Range(g, x, z, radius, out int x0, out int z0, out int x1, out int z1, cells: false);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    float px = g.CornerX(cx) - x, pz = g.CornerZ(cz) - z;
                    float w = Weight((float)Math.Sqrt(px * px + pz * pz), radius, softness);
                    if (w <= 0f) continue;
                    int i = g.Corner(cx, cz);
                    float h = g.ClampHeight(f(cx, cz, g.Heights[i], w));
                    if (h == g.Heights[i]) continue;
                    g.Heights[i] = h;
                    TouchCorner(g, cx, cz, ref dirty);
                }
            return dirty;
        }

        /// <summary>A corner belongs to up to four cells.</summary>
        private static void TouchCorner(TerrainGrid g, int cx, int cz, ref Dirty dirty)
        {
            dirty.Add(Math.Max(0, cx - 1), Math.Max(0, cz - 1));
            dirty.Add(Math.Min(g.W - 1, cx), Math.Min(g.H - 1, cz));
        }

        private static void Range(TerrainGrid g, float x, float z, float radius, out int x0, out int z0, out int x1, out int z1, bool cells)
        {
            int maxX = cells ? g.W - 1 : g.W, maxZ = cells ? g.H - 1 : g.H;
            x0 = Math.Max(0, (int)Math.Floor((x - radius - g.OriginX) / g.Cell) - 1);
            z0 = Math.Max(0, (int)Math.Floor((z - radius - g.OriginZ) / g.Cell) - 1);
            x1 = Math.Min(maxX, (int)Math.Ceiling((x + radius - g.OriginX) / g.Cell) + 1);
            z1 = Math.Min(maxZ, (int)Math.Ceiling((z + radius - g.OriginZ) / g.Cell) + 1);
        }
    }
}
