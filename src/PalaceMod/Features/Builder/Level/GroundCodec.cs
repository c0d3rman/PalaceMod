using System;
using System.IO;
using System.IO.Compression;
using PalaceMod.Builder.Model;

/*
    The terrain's working form (TerrainGrid) and its compact pack form (TerrainDef). Pure C#, unit-tested.

    Heights are int16 centimetres relative to the grid's y, delta-coded along the row-major sample order and deflated,
    so flat or gently sloped ground costs almost nothing. Materials are a byte per paint sample (PaintRes x PaintRes per
    cell, so painted edges follow curves, not 2 m squares; the ground layer draws them as smooth outlines), masks a bit
    per cell, all deflated. A field at its default (all zero) is left out.

    Format versions (TerrainDef.format): 1 (absent) had one material byte per cell; 2 adds paintRes. Decode reads both
    (a format-1 level's cells are spread over their samples); Encode always writes the current one.
*/
namespace PalaceMod.Builder.Level
{
    /// <summary>Cell materials, in the order of TerrainDef.materials.</summary>
    internal enum GroundMaterial : byte { Ground = 0, High = 1, Low = 2, Earth = 3, Sand = 4, Road = 5, Rock = 6 }

    internal sealed class TerrainGrid
    {
        /// <summary>World y floor: below -10 the game respawns the king.</summary>
        public const float MinHeight = -8f;
        /// <summary>The ceiling: at least MaxHeight world, and always MaxAbove over the grid's y (the castle's height),
        /// so a high host (Sturmklamm, castle y 31) still has room to build up.</summary>
        public const float MaxHeight = 30f, MaxAbove = 30f;
        public const int MaxCells = 400;                        // per side
        /// <summary>Paint samples per cell edge.</summary>
        public const int PaintRes = 2;
        public const int Format = 2;

        public readonly int W, H;          // cells
        public readonly float Cell;
        public readonly float OriginX, OriginZ;
        public float Y;                    // world height of 0
        public readonly float[] Heights;   // (W+1)*(H+1) corners, relative to Y
        public readonly byte[] Materials;  // PaintW*PaintH paint samples (PaintRes per cell edge)
        public readonly bool[] Blocked;    // W*H
        public readonly bool[] Water;      // W*H
        public float WaterLevel = -1f;     // relative to Y
        /// <summary>Runtime only (the `terrain` action; never in the pack): water cells frozen into walkable ice.</summary>
        public readonly bool[] Frozen;     // W*H
        /// <summary>Runtime only, on a map's own ground: cells a `terrain` action touched, drawn by the terrain even where
        /// their height still matches the host's (paint, water and blocks show there).</summary>
        public readonly bool[] Forced;     // W*H

        public TerrainGrid(int w, int h, float cell, float originX, float originZ, float y)
        {
            if (w < 1 || h < 1 || w > MaxCells || h > MaxCells) throw new ArgumentOutOfRangeException(nameof(w), $"terrain size {w}x{h} (1-{MaxCells})");
            if (!(cell >= 0.5f && cell <= 16f)) throw new ArgumentOutOfRangeException(nameof(cell), $"cell {cell} (0.5-16)");
            W = w; H = h; Cell = cell; OriginX = originX; OriginZ = originZ; Y = y;
            Heights = new float[(w + 1) * (h + 1)];
            Materials = new byte[w * PaintRes * h * PaintRes];
            Blocked = new bool[w * h];
            Water = new bool[w * h];
            Frozen = new bool[w * h];
            Forced = new bool[w * h];
        }

        /// <summary>A water cell that isn't frozen.</summary>
        public bool OpenWater(int i) => Water[i] && !Frozen[i];

        public int Corner(int x, int z) => z * (W + 1) + x;
        public int PaintW => W * PaintRes;
        public int PaintH => H * PaintRes;
        public int PaintIndex(int px, int pz) => pz * PaintW + px;
        public float PaintStep => Cell / PaintRes;
        /// <summary>World ceiling for this grid (see MaxAbove).</summary>
        public float MaxWorld => Math.Max(MaxHeight, Y + MaxAbove);
        public int CellIndex(int x, int z) => z * W + x;
        public float CornerX(int x) => OriginX + x * Cell;
        public float CornerZ(int z) => OriginZ + z * Cell;
        public float SizeX => W * Cell;
        public float SizeZ => H * Cell;

        public bool Contains(float x, float z) => x >= OriginX && z >= OriginZ && x < OriginX + SizeX && z < OriginZ + SizeZ;

        /// <summary>The cell under a world point, or false off the grid.</summary>
        public bool CellAt(float x, float z, out int cx, out int cz)
        {
            cx = (int)Math.Floor((x - OriginX) / Cell);
            cz = (int)Math.Floor((z - OriginZ) / Cell);
            return cx >= 0 && cz >= 0 && cx < W && cz < H;
        }

        /// <summary>World height of the surface at a point (bilinear over the cell's corners, clamped to the grid).</summary>
        public float HeightAt(float x, float z)
        {
            float fx = Clamp((x - OriginX) / Cell, 0f, W), fz = Clamp((z - OriginZ) / Cell, 0f, H);
            int ix = Math.Min((int)fx, W - 1), iz = Math.Min((int)fz, H - 1);
            float tx = fx - ix, tz = fz - iz;
            float a = Heights[Corner(ix, iz)], b = Heights[Corner(ix + 1, iz)], c = Heights[Corner(ix, iz + 1)], d = Heights[Corner(ix + 1, iz + 1)];
            return Y + Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
        }

        /// <summary>Clamps a relative height so its world height stays in [MinHeight, MaxWorld], to the cm.</summary>
        public float ClampHeight(float rel) => (float)Math.Round(Clamp(rel, MinHeight - Y, MaxWorld - Y) * 100f) / 100f;

        /// <summary>The painted material at a world point (the paint sample under it), Ground off the grid.</summary>
        public GroundMaterial MaterialAt(float x, float z)
        {
            int px = (int)Math.Floor((x - OriginX) / PaintStep), pz = (int)Math.Floor((z - OriginZ) / PaintStep);
            return px >= 0 && pz >= 0 && px < PaintW && pz < PaintH ? (GroundMaterial)Materials[PaintIndex(px, pz)] : GroundMaterial.Ground;
        }

        /// <summary>Whether a cell's diagonal runs 00-11 (else 10-01): the split the ground mesh uses.</summary>
        public bool SplitsMain(int x, int z) =>
            Math.Abs(Heights[Corner(x, z)] - Heights[Corner(x + 1, z + 1)]) <= Math.Abs(Heights[Corner(x + 1, z)] - Heights[Corner(x, z + 1)]);

        /// <summary>World height of the drawn surface (the cell's two triangles, as the mesh and its collider have them).</summary>
        public float SurfaceAt(float x, float z)
        {
            float fx = Clamp((x - OriginX) / Cell, 0f, W), fz = Clamp((z - OriginZ) / Cell, 0f, H);
            int ix = Math.Min((int)fx, W - 1), iz = Math.Min((int)fz, H - 1);
            float tx = fx - ix, tz = fz - iz;
            float a = Heights[Corner(ix, iz)], b = Heights[Corner(ix + 1, iz)], c = Heights[Corner(ix, iz + 1)], d = Heights[Corner(ix + 1, iz + 1)];
            float h;
            if (SplitsMain(ix, iz))
                h = tx >= tz ? a + (b - a) * tx + (d - b) * tz : a + (c - a) * tz + (d - c) * tx;
            else
                h = tx + tz <= 1f ? a + (b - a) * tx + (c - a) * tz : d + (c - d) * (1f - tx) + (b - d) * (1f - tz);
            return Y + h;
        }

        /// <summary>Steepest of a cell's two triangles (the mesh's split), in degrees; each triangle's own in first/second
        /// (first: the one along the cell's x = 0 edge).</summary>
        public float CellSlope(int x, int z, out float first, out float second)
        {
            float a = Heights[Corner(x, z)], b = Heights[Corner(x + 1, z)], c = Heights[Corner(x, z + 1)], d = Heights[Corner(x + 1, z + 1)];
            if (SplitsMain(x, z))
            {
                first = Slope(d - c, c - a);    // 00, 01, 11
                second = Slope(b - a, d - b);   // 00, 11, 10
            }
            else
            {
                first = Slope(b - a, c - a);    // 00, 01, 10
                second = Slope(d - c, d - b);   // 10, 01, 11
            }
            return Math.Max(first, second);
        }

        private float Slope(float rx, float rz) => (float)(Math.Atan(Math.Sqrt(rx * rx + rz * rz) / Cell) * 180.0 / Math.PI);

        /// <summary>Where a ray first meets the drawn surface (marching the heightfield), or false if it misses the grid.</summary>
        public bool Raycast(float ox, float oy, float oz, float dx, float dy, float dz, out float hx, out float hy, out float hz)
        {
            hx = hy = hz = 0f;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6f) return false;
            dx /= len; dy /= len; dz /= len;
            // Clip the ray to the grid's box (heights between the floor and the ceiling).
            float t0 = 0f, t1 = 5000f;
            if (!Slab(ox, dx, OriginX, OriginX + SizeX, ref t0, ref t1) || !Slab(oz, dz, OriginZ, OriginZ + SizeZ, ref t0, ref t1) ||
                !Slab(oy, dy, MinHeight - 1f, MaxWorld + 1f, ref t0, ref t1)) return false;
            float step = Cell / 4f, prevT = t0;
            if (oy + dy * t0 - SurfaceAt(ox + dx * t0, oz + dz * t0) < 0f) return false;   // starts under the ground
            for (float t = t0 + step; ; t += step)
            {
                float tt = Math.Min(t, t1);
                if (oy + dy * tt - SurfaceAt(ox + dx * tt, oz + dz * tt) <= 0f)
                {
                    float lo = prevT, hi = tt;
                    for (int i = 0; i < 24; i++)
                    {
                        float mid = (lo + hi) / 2f;
                        if (oy + dy * mid - SurfaceAt(ox + dx * mid, oz + dz * mid) > 0f) lo = mid; else hi = mid;
                    }
                    hx = ox + dx * hi; hz = oz + dz * hi; hy = SurfaceAt(hx, hz);
                    return true;
                }
                prevT = tt;
                if (tt >= t1) return false;
            }
        }

        private static bool Slab(float o, float d, float min, float max, ref float t0, ref float t1)
        {
            if (Math.Abs(d) < 1e-7f) return o >= min && o <= max;
            float a = (min - o) / d, b = (max - o) / d;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
            return t0 <= t1;
        }

        public bool AnyBlocked => Array.IndexOf(Blocked, true) >= 0;
        public bool AnyWater => Array.IndexOf(Water, true) >= 0;

        /// <summary>On a map's own ground: the host's ground height at each corner (relative to Y, NaN where it has none),
        /// sampled when the terrain is shown. Heights stay absolute in memory; the pack stores them as offsets from this, and
        /// only cells that differ from it (Sculpted) replace the host's ground. Null on a flat field.</summary>
        public float[] Host;

        /// <summary>Whether a cell is the terrain's own ground: always on a flat field; on a map's own ground, where a corner
        /// was moved off the host's height (and the host has ground under all four).</summary>
        public bool Sculpted(int x, int z)
        {
            if (Host == null) return true;
            bool moved = Forced[CellIndex(x, z)];
            for (int k = 0; k < 4; k++)
            {
                int i = Corner(x + (k & 1), z + (k >> 1));
                if (float.IsNaN(Host[i])) return false;
                if (Math.Abs(Heights[i] - Host[i]) > 0.005f) moved = true;
            }
            return moved;
        }

        public TerrainGrid Clone()
        {
            var g = new TerrainGrid(W, H, Cell, OriginX, OriginZ, Y) { WaterLevel = WaterLevel };
            if (Host != null) g.Host = (float[])Host.Clone();
            Array.Copy(Heights, g.Heights, Heights.Length);
            Array.Copy(Materials, g.Materials, Materials.Length);
            Array.Copy(Blocked, g.Blocked, Blocked.Length);
            Array.Copy(Water, g.Water, Water.Length);
            Array.Copy(Frozen, g.Frozen, Frozen.Length);
            Array.Copy(Forced, g.Forced, Forced.Length);
            return g;
        }

        internal static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        internal static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    internal static class GroundCodec
    {
        /// <summary>Reads a level's terrain. Throws FormatException on a malformed one.</summary>
        public static TerrainGrid Decode(TerrainDef def)
        {
            if (def == null) return null;
            if (def.Size == null || def.Size.Length != 2) throw new FormatException("terrain.size must be [columns, rows]");
            if (def.Origin == null || def.Origin.Length != 2) throw new FormatException("terrain.origin must be [x, z]");
            var g = new TerrainGrid(def.Size[0], def.Size[1], def.Cell, def.Origin[0], def.Origin[1], def.Y);
            if (!string.IsNullOrEmpty(def.Heights))
            {
                var bytes = Inflate(def.Heights, g.Heights.Length * 2, "heights");
                int prev = 0;
                for (int i = 0; i < g.Heights.Length; i++)
                {
                    prev = (short)(prev + (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8)));
                    g.Heights[i] = prev / 100f;
                }
            }
            int format = def.Format ?? 1;
            if (format < 1 || format > TerrainGrid.Format)
                throw new FormatException($"terrain format {format} is newer than this PalaceMod reads ({TerrainGrid.Format}): update PalaceMod");
            if (!string.IsNullOrEmpty(def.Materials))
            {
                int res = format == 1 ? 1 : def.PaintRes ?? TerrainGrid.PaintRes;
                if (res < 1 || res > 8) throw new FormatException($"terrain.paintRes {res} (1-8)");
                var bytes = Inflate(def.Materials, g.W * res * g.H * res, "materials");
                // Spread (or sample) the stored resolution over the grid's paint samples: a format-1 cell fills its samples.
                int R = TerrainGrid.PaintRes, rowW = g.W * res;
                for (int pz = 0; pz < g.PaintH; pz++)
                    for (int px = 0; px < g.PaintW; px++)
                    {
                        byte v = bytes[(pz * res / R) * rowW + px * res / R];
                        g.Materials[g.PaintIndex(px, pz)] = v <= (byte)GroundMaterial.Rock ? v : (byte)0;
                    }
            }
            if (!string.IsNullOrEmpty(def.Blocked)) ReadBits(def.Blocked, g.Blocked, "blocked");
            if (def.Water != null)
            {
                g.WaterLevel = def.Water.Level;
                if (!string.IsNullOrEmpty(def.Water.Cells)) ReadBits(def.Water.Cells, g.Water, "water");
            }
            return g;
        }

        /// <summary>The pack form of a grid; default fields are left out.</summary>
        public static TerrainDef Encode(TerrainGrid g)
        {
            var def = new TerrainDef
            {
                Origin = [Round(g.OriginX), Round(g.OriginZ)],
                Cell = g.Cell,
                Size = [g.W, g.H],
                Y = Round(g.Y),
                Format = TerrainGrid.Format,
            };
            bool any = false;
            var hb = new byte[g.Heights.Length * 2];
            short prev = 0;
            for (int i = 0; i < g.Heights.Length; i++)
            {
                // On a map's own ground the pack holds offsets from the host's ground (0 where untouched).
                float rel = g.Host == null ? g.Heights[i] : float.IsNaN(g.Host[i]) ? 0f : g.Heights[i] - g.Host[i];
                short v = (short)Math.Round(TerrainGrid.Clamp(rel, -327f, 327f) * 100f);
                if (v != 0) any = true;
                short d = (short)(v - prev);
                hb[2 * i] = (byte)d;
                hb[2 * i + 1] = (byte)(d >> 8);
                prev = v;
            }
            if (any) def.Heights = Deflate(hb);
            if (Array.Exists(g.Materials, m => m != 0))
            {
                def.Materials = Deflate(g.Materials);
                def.PaintRes = TerrainGrid.PaintRes;
            }
            if (g.AnyBlocked) def.Blocked = Deflate(PackBits(g.Blocked));
            if (g.AnyWater) def.Water = new WaterDef { Level = Round(g.WaterLevel), Cells = Deflate(PackBits(g.Water)) };
            return def;
        }

        private static float Round(float f) => (float)Math.Round(f * 100f) / 100f;

        private static byte[] PackBits(bool[] bits)
        {
            var b = new byte[(bits.Length + 7) / 8];
            for (int i = 0; i < bits.Length; i++) if (bits[i]) b[i >> 3] |= (byte)(1 << (i & 7));
            return b;
        }

        private static void ReadBits(string data, bool[] into, string what)
        {
            var b = Inflate(data, (into.Length + 7) / 8, what);
            for (int i = 0; i < into.Length; i++) into[i] = (b[i >> 3] & (1 << (i & 7))) != 0;
        }

        private static string Deflate(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(raw, 0, raw.Length);
            return Convert.ToBase64String(ms.ToArray());
        }

        private static byte[] Inflate(string data, int expected, string what)
        {
            try
            {
                using var input = new MemoryStream(Convert.FromBase64String(data));
                using var d = new DeflateStream(input, CompressionMode.Decompress);
                var buf = new byte[expected];
                int read = 0, n;
                while (read < expected && (n = d.Read(buf, read, expected - read)) > 0) read += n;
                if (read != expected || d.ReadByte() != -1) throw new FormatException($"terrain.{what} has the wrong length for the grid");
                return buf;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException)
            {
                throw new FormatException($"terrain.{what} is corrupt", e);
            }
            catch (FormatException e) when (!e.Message.StartsWith("terrain."))
            {
                throw new FormatException($"terrain.{what} isn't base64", e);
            }
        }
    }
}
