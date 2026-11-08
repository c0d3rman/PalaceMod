using System;
using System.IO;
using System.IO.Compression;
using PalaceMod.Builder.Model;

/*
    The terrain's working form (TerrainGrid) and its compact pack form (TerrainDef). Pure C#, unit-tested.

    Heights are int16 centimetres relative to the grid's y, delta-coded along the row-major sample order and deflated,
    so flat or gently sloped ground costs almost nothing. Materials are a byte per cell, masks a bit per cell, both
    deflated. A field at its default (all zero) is left out.
*/
namespace PalaceMod.Builder.Level
{
    /// <summary>Cell materials, in the order of TerrainDef.materials.</summary>
    internal enum GroundMaterial : byte { Ground = 0, High = 1, Low = 2, Earth = 3, Sand = 4, Road = 5, Rock = 6 }

    internal sealed class TerrainGrid
    {
        public const float MinHeight = -8f, MaxHeight = 30f;   // world y; below -10 the game respawns the king
        public const int MaxCells = 400;                        // per side

        public readonly int W, H;          // cells
        public readonly float Cell;
        public readonly float OriginX, OriginZ;
        public float Y;                    // world height of 0
        public readonly float[] Heights;   // (W+1)*(H+1) corners, relative to Y
        public readonly byte[] Materials;  // W*H
        public readonly bool[] Blocked;    // W*H
        public readonly bool[] Water;      // W*H
        public float WaterLevel = -1f;     // relative to Y

        public TerrainGrid(int w, int h, float cell, float originX, float originZ, float y)
        {
            if (w < 1 || h < 1 || w > MaxCells || h > MaxCells) throw new ArgumentOutOfRangeException(nameof(w), $"terrain size {w}x{h} (1-{MaxCells})");
            if (!(cell >= 0.5f && cell <= 16f)) throw new ArgumentOutOfRangeException(nameof(cell), $"cell {cell} (0.5-16)");
            W = w; H = h; Cell = cell; OriginX = originX; OriginZ = originZ; Y = y;
            Heights = new float[(w + 1) * (h + 1)];
            Materials = new byte[w * h];
            Blocked = new bool[w * h];
            Water = new bool[w * h];
        }

        public int Corner(int x, int z) => z * (W + 1) + x;
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

        /// <summary>Clamps a relative height so its world height stays in [MinHeight, MaxHeight], to the cm.</summary>
        public float ClampHeight(float rel) => (float)Math.Round(Clamp(rel, MinHeight - Y, MaxHeight - Y) * 100f) / 100f;

        public bool AnyBlocked => Array.IndexOf(Blocked, true) >= 0;
        public bool AnyWater => Array.IndexOf(Water, true) >= 0;

        public TerrainGrid Clone()
        {
            var g = new TerrainGrid(W, H, Cell, OriginX, OriginZ, Y) { WaterLevel = WaterLevel };
            Array.Copy(Heights, g.Heights, Heights.Length);
            Array.Copy(Materials, g.Materials, Materials.Length);
            Array.Copy(Blocked, g.Blocked, Blocked.Length);
            Array.Copy(Water, g.Water, Water.Length);
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
            if (!string.IsNullOrEmpty(def.Materials))
            {
                var bytes = Inflate(def.Materials, g.Materials.Length, "materials");
                for (int i = 0; i < bytes.Length; i++) g.Materials[i] = bytes[i] <= (byte)GroundMaterial.Rock ? bytes[i] : (byte)0;
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
            };
            bool any = false;
            var hb = new byte[g.Heights.Length * 2];
            short prev = 0;
            for (int i = 0; i < g.Heights.Length; i++)
            {
                short v = (short)Math.Round(TerrainGrid.Clamp(g.Heights[i], -327f, 327f) * 100f);
                if (v != 0) any = true;
                short d = (short)(v - prev);
                hb[2 * i] = (byte)d;
                hb[2 * i + 1] = (byte)(d >> 8);
                prev = v;
            }
            if (any) def.Heights = Deflate(hb);
            if (Array.Exists(g.Materials, m => m != 0)) def.Materials = Deflate(g.Materials);
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
