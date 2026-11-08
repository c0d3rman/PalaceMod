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
            Check(Math.Abs(g.Heights.Max() + g.Y - g.MaxWorld) < 0.01f, $"max {g.Heights.Max() + g.Y}");
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

        public static void HighHostsCanBuildAboveTheCastle()
        {
            var g = new TerrainGrid(20, 20, 2f, 0f, 0f, 31f);   // Sturmklamm's castle height
            TerrainBrushes.Raise(g, 20f, 20f, 6f, 0f, 100f);
            Check(Math.Abs(g.Heights.Max() - TerrainGrid.MaxAbove) < 0.01f, $"30 m over the castle: {g.Heights.Max()}");
            TerrainBrushes.Raise(g, 20f, 20f, 6f, 0f, -500f);
            Check(Math.Abs(g.Heights.Min() + g.Y - TerrainGrid.MinHeight) < 0.01f, $"floor stays at world -8: {g.Heights.Min() + g.Y}");
        }

        public static void FormatOneMaterialsAreSpreadOverTheirSamples()
        {
            var def = GroundCodec.Encode(Field(4, 3));
            var cells = new byte[12];
            cells[1 * 4 + 2] = (byte)GroundMaterial.Road;   // cell (2, 1)
            using (var ms = new System.IO.MemoryStream())
            {
                using (var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) d.Write(cells, 0, cells.Length);
                def.Materials = Convert.ToBase64String(ms.ToArray());
            }
            def.Format = null;
            def.PaintRes = null;
            var g = GroundCodec.Decode(def);
            int painted = g.Materials.Count(m => m == (byte)GroundMaterial.Road);
            Check(painted == TerrainGrid.PaintRes * TerrainGrid.PaintRes, $"one cell's samples: {painted}");
            Check(g.MaterialAt(g.CornerX(2) + 0.2f, g.CornerZ(1) + 1.8f) == GroundMaterial.Road, "inside the cell");
            Check(g.MaterialAt(g.CornerX(3) + 0.2f, g.CornerZ(1) + 1f) == GroundMaterial.Ground, "next cell");
            var again = GroundCodec.Encode(g);
            Check(again.Format == TerrainGrid.Format && again.PaintRes == TerrainGrid.PaintRes, "re-encoded in the current format");
            Check(GroundCodec.Decode(again).Materials.SequenceEqual(g.Materials), "round trip");
            def.Format = 99;
            Throws(() => GroundCodec.Decode(def), "a newer format");
        }

        public static void PaintFollowsTheBrushFinerThanCells()
        {
            var g = Field(20, 20);
            TerrainBrushes.Paint(g, -80f, -50f, 3f, CellLayer.Material, (byte)GroundMaterial.Road);
            int n = g.Materials.Count(m => m == (byte)GroundMaterial.Road);
            float area = n * g.PaintStep * g.PaintStep;
            Check(Math.Abs(area - Math.PI * 9) < 6f, $"painted area {area} vs {Math.PI * 9}");
        }

        public static void PaintOutlinesAreSmoothAndCoverThePaint()
        {
            var g = Field(16, 16);
            TerrainBrushes.Paint(g, -84f, -54f, 6f, CellLayer.Material, (byte)GroundMaterial.Road);
            var xz = new System.Collections.Generic.List<float>();
            int tris = PaintOutline.Build(g, 0, 0, 16, 16, (byte)GroundMaterial.Road, null, xz);
            Check(tris > 0 && xz.Count == tris * 6, "triangles");
            double area = 0;
            var angles = new System.Collections.Generic.HashSet<int>();
            for (int k = 0; k < xz.Count; k += 6)
            {
                double ax = xz[k], az = xz[k + 1], bx = xz[k + 2], bz = xz[k + 3], cx = xz[k + 4], cz = xz[k + 5];
                double cross = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
                Check(cross <= 1e-4, "clockwise from above (Unity's front face)");
                area += -cross / 2;
            }
            Check(Math.Abs(area - Math.PI * 36) < 15, $"outline area {area:0.0} vs circle {Math.PI * 36:0.0}");
            // Edge directions: a staircase only has 0/90 degrees; an outline has many.
            for (int k = 0; k < xz.Count; k += 6)
                for (int e = 0; e < 3; e++)
                {
                    double dx = xz[k + (e + 1) % 3 * 2] - xz[k + e * 2], dz = xz[k + (e + 1) % 3 * 2 + 1] - xz[k + e * 2 + 1];
                    if (dx * dx + dz * dz > 0.01) angles.Add((int)Math.Round(Math.Atan2(dz, dx) * 180 / Math.PI / 5) % 36);
                }
            Check(angles.Count > 12, $"edge directions {angles.Count}");
            Check(PaintOutline.Build(g, 0, 0, 16, 16, (byte)GroundMaterial.Sand, null, xz) == 0, "no sand painted, no sand drawn");
        }

        public static void RampIsWalkableAcrossItsWidth()
        {
            var g = Field(40, 40);
            TerrainBrushes.Flatten(g, -60f, -40f, 12f, 0f, 4f);   // a plateau, hard edges
            // Diagonal ramp off the plateau.
            TerrainBrushes.Ramp(g, -55f, -35f, 4f, -40f, -20f, 0f, 6f);
            float slope = TerrainBrushes.RampSlope(-55f, -35f, 4f, -40f, -20f, 0f);
            Check(slope < 30f, $"incline {slope}");
            // Across the middle, 6 m of the ramp's width is no steeper than its incline (plus the grid's error).
            float sx = -0.7071f, sz = 0.7071f;   // across the ramp
            for (float s = -3f; s <= 3f; s += 0.5f)
            {
                float x = -47.5f + sx * s, z = -27.5f + sz * s;
                Check(g.CellAt(x, z, out int cx, out int cz), "on the grid");
                float steepest = g.CellSlope(cx, cz, out _, out _);
                Check(steepest < 30f, $"walkable at {s} m from the middle: {steepest:0.0} deg");
            }
        }

        public static void RaycastFindsTheCliffTop()
        {
            var g = Field(40, 40);
            TerrainBrushes.Flatten(g, -60f, -40f, 10f, 0f, 6f);
            // Looking down at 50 degrees at a point on the plateau (from 50 m away).
            float dy = -Mathf.Sin(50f), dz = Mathf.Cos(50f);
            float ox = -60f, oy = g.Y + 6f - dy * 50f, oz = -36f - dz * 50f;
            Check(g.Raycast(ox, oy, oz, 0f, dy, dz, out float hx, out float hy, out float hz), "hits");
            Check(Math.Abs(hy - g.Y - 6f) < 0.01f, $"on the top: {hy - g.Y}");
            Check(Math.Abs(g.SurfaceAt(hx, hz) - hy) < 0.001f, "on the surface");
            Check(Math.Abs(hz + 36f) < 0.05f, $"where it was aimed: {hz}");
        }

        private static class Mathf
        {
            public static float Sin(float deg) => (float)Math.Sin(deg * Math.PI / 180);
            public static float Cos(float deg) => (float)Math.Cos(deg * Math.PI / 180);
        }

        public static void SurfaceMatchesTheMeshTriangles()
        {
            var g = Field(4, 4);
            g.Heights[g.Corner(1, 1)] = 2f;   // one raised corner: the cells around it fold along their diagonals
            for (int cz = 0; cz < 4; cz++)
                for (int cx = 0; cx < 4; cx++)
                    for (int k = 0; k < 4; k++)
                    {
                        int x = cx + (k & 1), z = cz + (k >> 1);
                        Check(Math.Abs(g.SurfaceAt(g.CornerX(x), g.CornerZ(z)) - g.Y - g.Heights[g.Corner(x, z)]) < 1e-4f, "corners");
                    }
            // The middle of the cell's diagonal is the average of the diagonal's ends.
            bool main = g.SplitsMain(0, 0);
            float mid = g.SurfaceAt(g.CornerX(0) + 1f, g.CornerZ(0) + 1f) - g.Y;
            float expect = main ? (g.Heights[g.Corner(0, 0)] + g.Heights[g.Corner(1, 1)]) / 2f : (g.Heights[g.Corner(1, 0)] + g.Heights[g.Corner(0, 1)]) / 2f;
            Check(Math.Abs(mid - expect) < 1e-4f, $"diagonal {mid} vs {expect}");
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
