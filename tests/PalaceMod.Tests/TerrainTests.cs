using System;
using System.Linq;
using Newtonsoft.Json;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    public static class TerrainTests
    {
        private static TerrainGrid Field(int w = 100, int h = 70) => new(w, h, 2f, -100f, -70f, 1.5f);

        private static void Check(bool ok, string what)
        {
            if (!ok) throw new Exception(what);
        }

        public static void FlatFieldEncodesToNothing()
        {
            var def = GroundCodec.Encode(Field());
            Check(def.Heights == null && def.Materials == null && def.Blocked == null && def.Water == null, "a flat field has no arrays");
            var g = GroundCodec.Decode(def);
            Check(g.W == 100 && g.H == 70 && g.Y == 1.5f && g.OriginX == -100f && g.Heights.All(h => h == 0f), "decodes to the same flat field");
        }

        public static void RoundTripsEverything()
        {
            var g = Field();
            TerrainBrushes.Raise(g, 0f, 0f, 20f, 1f, 6f);
            TerrainBrushes.Flatten(g, 30f, -30f, 10f, 0f, 4f);
            TerrainBrushes.Paint(g, -40f, 20f, 6f, CellLayer.Material, (byte)GroundMaterial.Road);
            TerrainBrushes.Paint(g, 40f, 20f, 5f, CellLayer.Blocked, 1);
            g.WaterLevel = -0.5f;
            TerrainBrushes.Water(g, -60f, -40f, 8f, true);
            var json = JsonConvert.SerializeObject(GroundCodec.Encode(g));
            var back = GroundCodec.Decode(JsonConvert.DeserializeObject<TerrainDef>(json));
            for (int i = 0; i < g.Heights.Length; i++) Check(Math.Abs(g.Heights[i] - back.Heights[i]) < 0.006f, $"height {i}: {g.Heights[i]} vs {back.Heights[i]}");
            Check(g.Materials.SequenceEqual(back.Materials), "materials");
            Check(g.Blocked.SequenceEqual(back.Blocked), "blocked");
            Check(g.Water.SequenceEqual(back.Water), "water");
            Check(back.WaterLevel == -0.5f, "water level");
            Check(json.Length < 3000, $"a sculpted 200x140 m field stays compact ({json.Length} chars)");
        }

        public static void RejectsBadData()
        {
            var def = GroundCodec.Encode(Field(10, 10));
            def.Blocked = "not base64!";
            Throws(() => GroundCodec.Decode(def), "bad base64");
            def = GroundCodec.Encode(Field(10, 10));
            def.Heights = GroundCodec.Encode(Field(20, 20).Also(f => f.Heights[3] = 1f)).Heights;
            Throws(() => GroundCodec.Decode(def), "wrong length");
            def.Heights = null;
            def.Size = [0, 10];
            Throws(() => GroundCodec.Decode(def), "zero size");
            def.Size = [10000, 10];
            Throws(() => GroundCodec.Decode(def), "too large");
        }

        public static void HeightsAreClampedToTheWorldRange()
        {
            var g = Field();
            TerrainBrushes.Raise(g, 0f, 0f, 10f, 0f, 100f);
            Check(Math.Abs(g.Heights.Max() + g.Y - TerrainGrid.MaxHeight) < 0.01f, $"max {g.Heights.Max() + g.Y}");
            TerrainBrushes.Raise(g, 0f, 0f, 10f, 0f, -500f);
            Check(Math.Abs(g.Heights.Min() + g.Y - TerrainGrid.MinHeight) < 0.01f, $"min {g.Heights.Min() + g.Y}");
        }

        public static void HardFlattenMakesACliffEdge()
        {
            var g = Field();
            var d = TerrainBrushes.Flatten(g, 0f, 0f, 10f, 0f, 4f);
            Check(!d.Empty, "dirty");
            Check(g.HeightAt(0f, 0f) - g.Y == 4f, "top is at 4");
            Check(g.HeightAt(14f, 0f) - g.Y == 0f, "outside stays flat");
            Check(g.HeightAt(7f, 0f) - g.Y == 4f, "edge of the top");
        }

        public static void SoftRaiseFallsOff()
        {
            var g = Field();
            TerrainBrushes.Raise(g, 0f, 0f, 10f, 1f, 5f);
            float mid = g.HeightAt(0f, 0f) - g.Y, half = g.HeightAt(5f, 0f) - g.Y, edge = g.HeightAt(10f, 0f) - g.Y;
            Check(Math.Abs(mid - 5f) < 0.01f && half > 0.5f && half < 4.5f && edge < 0.01f, $"falloff {mid} {half} {edge}");
        }

        public static void RampInterpolates()
        {
            var g = Field();
            TerrainBrushes.Ramp(g, 0f, 0f, 4f, 0f, 20f, 0f, 6f);
            Check(Math.Abs(g.HeightAt(0f, 10f) - g.Y - 2f) < 0.05f, $"middle {g.HeightAt(0f, 10f) - g.Y}");
            Check(g.HeightAt(10f, 10f) - g.Y == 0f, "beside the ramp");
        }

        public static void SmoothEvensOutASpike()
        {
            var g = Field();
            g.Heights[g.Corner(50, 35)] = 10f;
            TerrainBrushes.Smooth(g, g.CornerX(50), g.CornerZ(35), 4f, 0f, 1f);
            Check(g.Heights[g.Corner(50, 35)] < 3f, $"spike {g.Heights[g.Corner(50, 35)]}");
        }

        public static void WaterDigsBelowTheSurface()
        {
            var g = Field();
            g.WaterLevel = -0.5f;
            TerrainBrushes.Water(g, 0f, 0f, 6f, true);
            Check(g.CellAt(0f, 0f, out int cx, out int cz) && g.Water[g.CellIndex(cx, cz)], "water cell");
            Check(g.HeightAt(1f, 1f) - g.Y <= -0.5f - TerrainBrushes.WaterDepth + 0.01f, "dug");
            TerrainBrushes.Water(g, 0f, 0f, 20f, false);
            Check(!g.AnyWater, "dried");
        }

        public static void TinyBrushPaintsTheCellUnderIt()
        {
            var g = Field();
            TerrainBrushes.Paint(g, 0.3f, 0.3f, 0.1f, CellLayer.Blocked, 1);
            Check(g.Blocked.Count(b => b) == 1, $"{g.Blocked.Count(b => b)} cells");
        }

        private static void Throws(Action a, string what)
        {
            try { a(); }
            catch (FormatException) { return; }
            catch (ArgumentException) { return; }
            throw new Exception("didn't throw: " + what);
        }

        private static T Also<T>(this T t, Action<T> f)
        {
            f(t);
            return t;
        }
    }
}
