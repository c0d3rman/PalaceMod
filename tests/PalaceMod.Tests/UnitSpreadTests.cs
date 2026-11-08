using System;
using System.Linq;
using PalaceMod.Builder.Level;

namespace PalaceMod.Tests
{
    public static class UnitSpreadTests
    {
        // Land is x <= 0 (a river beyond): the nearest walkable point clamps x.
        private static (float x, float z)? Shore(int unit, float x, float z) => (Math.Min(x, 0f), z);

        private static float Dist((float x, float z) a, (float x, float z) b) => (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

        public static void UnitsOnGroundStay()
        {
            var homes = new[] { (-3f, 0f), (-3f, 2f), (-5f, 1f) };
            var placed = UnitSpread.Place(homes, Shore, out bool[] moved);
            Assert.True(moved.All(m => !m), "nothing moved");
            Assert.True(placed.SequenceEqual(homes), "same homes");
        }

        public static void UnitsInWaterGoAshoreAndInland()
        {
            var placed = UnitSpread.Place([(4f, 0f)], Shore, out bool[] moved);
            Assert.True(moved[0], "moved");
            Assert.True(Math.Abs(placed[0].x + UnitSpread.Inset) < 0.01f, $"pushed {UnitSpread.Inset} m in: {placed[0]}");
            Assert.True(Math.Abs(placed[0].z) < 0.01f, "straight in");
        }

        public static void FormationStaysSpread()
        {
            // Units in a line straight out into the water would all land on one shore point.
            var homes = new[] { (2f, 0f), (4f, 0f), (6f, 0f), (8f, 0f), (-2f, 0f) };
            var placed = UnitSpread.Place(homes, Shore, out bool[] moved);
            Assert.True(!moved[4], "the one on land stays");
            for (int i = 0; i < placed.Length; i++)
            {
                Assert.True(placed[i].x <= 0.001f, $"unit {i} on land: {placed[i]}");
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.True(Dist(placed[i], placed[j]) >= UnitSpread.Spacing - 0.01f, $"units {i} and {j} apart: {placed[i]} {placed[j]}");
            }
        }

        public static void UpgradeChoicesShareASpot()
        {
            // The same spot in two upgrade choices: one new spot for both, not pushed apart.
            var placed = UnitSpread.Place([(3f, 0f), (5f, 1f), (3f, 0f), (5f, 1f)], Shore, out bool[] moved);
            Assert.True(moved.All(m => m), "all moved");
            Assert.True(placed[0] == placed[2] && placed[1] == placed[3], "twins together");
            Assert.True(Dist(placed[0], placed[1]) >= UnitSpread.Spacing - 0.01f, "different spots apart");
        }

        public static void NoGroundKeepsHome()
        {
            var placed = UnitSpread.Place([(1f, 1f)], (u, x, z) => null, out bool[] moved);
            Assert.True(!moved[0] && placed[0] == (1f, 1f), "no graph: unchanged");
        }

        public static void PerUnitGraphs()
        {
            // Unit 1 flies: everything is ground for it.
            (float, float)? Near(int u, float x, float z) => u == 1 ? (x, z) : Shore(u, x, z);
            UnitSpread.Place([(3f, 0f), (3f, 0f)], Near, out bool[] moved);
            Assert.True(moved[0] && !moved[1], "only the walker moves");
        }

        // A river z in (-3, 3) with a ford at x in (20, 26) (ground everywhere else). The building stands at (0, -8).
        private static bool RiverGround(float x, float z) => z <= -3f || z >= 3f || (x >= 20f && x <= 26f);

        private static (float x, float z)? RiverNearest(int unit, float x, float z)
        {
            if (RiverGround(x, z)) return (x, z);
            // Nearest of: the near bank, the far bank, the ford.
            var options = new[] { (x, -3f), (x, 3f), (Math.Min(Math.Max(x, 20f), 26f), z) };
            return options.OrderBy(o => Dist(o, (x, z))).First();
        }

        public static void UnitsNeverCrossTheRiver()
        {
            // A spot just short of the far bank: its nearest ground is over the river, 1 m away; the building's own bank
            // is 5 m back. The far bank is reachable only round by the ford (40 m+), so the unit comes back.
            var home = (0f, 2f);
            var reach = UnitSpread.Flood(0f, -8f, RiverGround);
            Assert.True(reach.Reachable(0f, -4f), "the building's own bank is reachable");
            Assert.True(!reach.Reachable(0f, 4f), "the far bank isn't, though the ford connects it");
            var placed = UnitSpread.Place([home], RiverNearest, out bool[] moved, reach: _ => reach);
            Assert.True(moved[0], "moved");
            Assert.True(placed[0].z <= -3f + 0.01f, $"on the building's bank: {placed[0]}");
            // Without the flood it would have gone over (the old behaviour).
            var over = UnitSpread.Place([home], RiverNearest, out _);
            Assert.True(over[0].z >= 3f, $"nearest ground alone crosses: {over[0]}");
            // A formation across the river stays spread out on the near bank.
            var many = UnitSpread.Place([(0f, 2f), (2f, 2f), (4f, 2.5f), (-2f, 1f)], RiverNearest, out _, reach: _ => reach);
            for (int i = 0; i < many.Length; i++)
            {
                Assert.True(many[i].z <= -3f + 0.01f, $"unit {i} on the near bank: {many[i]}");
                for (int j = i + 1; j < many.Length; j++) Assert.True(Dist(many[i], many[j]) >= UnitSpread.Spacing - 0.01f, $"{i},{j} apart");
            }
        }

        public static void UnitsByTheFordStillTakeTheNearestGround()
        {
            // Next to the building's own ford, the ford is the building's ground: no detour rule kicks in.
            var reach = UnitSpread.Flood(23f, -8f, RiverGround);
            var placed = UnitSpread.Place([(19f, 0f)], RiverNearest, out bool[] moved, reach: _ => reach);
            Assert.True(moved[0] && placed[0].x >= 20f - 0.01f && Math.Abs(placed[0].z) < 3f, $"onto the ford: {placed[0]}");
        }

        public static void SlotFrameRoundTrips()
        {
            foreach (float yaw in new[] { 0f, 37f, 90f, 180f, 271f })
            {
                var w = UnitSpread.ToWorld(10f, -5f, yaw, 2f, 3f);
                var back = UnitSpread.ToSlot(10f, -5f, yaw, w.x, w.z);
                Assert.True(Math.Abs(back.x - 2f) < 1e-4f && Math.Abs(back.z - 3f) < 1e-4f, $"yaw {yaw}: {back}");
            }
            // Unity's yaw 90 turns forward (+z) to +x.
            var f = UnitSpread.ToWorld(0f, 0f, 90f, 0f, 1f);
            Assert.True(Math.Abs(f.x - 1f) < 1e-4f && Math.Abs(f.z) < 1e-4f, $"forward at 90: {f}");
        }
    }
}
