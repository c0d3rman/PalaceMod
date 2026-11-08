using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;

namespace PalaceMod.Tests
{
    public static class GroundPaletteTests
    {
        // Host scheme colours measured in game (ground, high, low, earth, sand, road, rock).
        private static Rgb[] Scheme(Rgb ground, Rgb earth, Rgb sand, Rgb road, Rgb rock) => [ground, ground, ground, earth, sand, road, rock];

        private static readonly Dictionary<string, Rgb[]> Hosts = new()
        {
            ["Moorweg"] = Scheme(new(0.058f, 0.267f, 0.349f), new(0.070f, 0.204f, 0.292f), new(0.632f, 0.597f, 0.522f), new(0.070f, 0.204f, 0.292f), new(0.714f, 0.898f, 0.880f)),
            ["Nordfels"] = Scheme(new(0.121f, 0.396f, 0.391f), new(0.670f, 0.482f, 0.376f), new(0.991f, 0.776f, 0.519f), new(0.670f, 0.482f, 0.376f), new(0.858f, 0.858f, 0.725f)),
            ["Wildbach"] = Scheme(new(0.709f, 0.243f, 0.361f), new(0.219f, 0.263f, 0.340f), new(0.898f, 0.861f, 0.471f), new(0.299f, 0.208f, 0.322f), new(0.802f, 0.792f, 0.700f)),
            ["Totend"] = Scheme(new(0.16f, 0.16f, 0.16f), new(0.302f, 0.135f, 0.154f), new(0.651f, 0.562f, 0.372f), new(0.193f, 0.205f, 0.217f), new(0.717f, 0.708f, 0.558f)),
            ["Freifort"] = Scheme(new(0.962f, 0.655f, 0.168f), new(0.708f, 0.386f, 0.210f), new(1f, 0.893f, 0.590f), new(0.774f, 0.461f, 0.244f), new(0.962f, 0.897f, 0.722f)),
            ["Frostsee"] = Scheme(new(0.824f, 0.912f, 0.934f), new(0.623f, 0.509f, 0.490f), new(0.769f, 0.914f, 1f), new(0.623f, 0.509f, 0.490f), new(0.587f, 0.759f, 0.858f)),
        };

        public static void EveryPaintIsDistinctOnEveryHost()
        {
            foreach (var (host, scheme) in Hosts)
            {
                var p = GroundPalette.Resolve(scheme);
                for (int a = 0; a < 7; a++)
                    for (int b = a + 1; b < 7; b++)
                    {
                        var ma = (GroundMaterial)a;
                        var mb = (GroundMaterial)b;
                        float d = GroundPalette.Distance(p[a], p[b]);
                        Assert.True(d >= GroundPalette.Needed(ma, mb) - 1e-4f, $"{host}: {ma} {p[a]} and {mb} {p[b]} are only {d:0.###} apart");
                    }
            }
        }

        public static void GrassKeepsTheHostColour()
        {
            foreach (var (host, scheme) in Hosts)
            {
                var p = GroundPalette.Resolve(scheme);
                Assert.True(GroundPalette.Distance(p[0], scheme[0]) < 1e-5f, $"{host}: ground stays the host's grass");
            }
        }

        public static void DistinctHostColoursAreKept()
        {
            // Nordfels' sand, road and rock already stand apart: they're the host's own.
            var s = Hosts["Nordfels"];
            var p = GroundPalette.Resolve(s);
            foreach (var m in new[] { GroundMaterial.Sand, GroundMaterial.Road, GroundMaterial.Rock })
                Assert.True(GroundPalette.Distance(p[(int)m], s[(int)m]) < 1e-5f, $"{m} keeps the host colour");
        }

        public static void HighIsLighterAndLowDarkerOnAGreenHost()
        {
            var p = GroundPalette.Resolve(Hosts["Nordfels"]);
            static float L(Rgb c) => 0.3f * c.R + 0.59f * c.G + 0.11f * c.B;
            Assert.True(L(p[(int)GroundMaterial.High]) > L(p[0]) && L(p[(int)GroundMaterial.Low]) < L(p[0]), "high lighter, low darker");
        }

        public static void AllOneColourStillResolves()
        {
            var grey = new Rgb(0.5f, 0.5f, 0.5f);
            var p = GroundPalette.Resolve(Enumerable.Repeat(grey, 7).ToArray());
            Assert.True(p.Distinct().Count() >= 5, "a one-colour scheme still gives several paints");
            Assert.True(p.All(c => c.R >= 0f && c.R <= 1f && c.G >= 0f && c.G <= 1f && c.B >= 0f && c.B <= 1f), "colours stay in range");
        }

        public static void LakesGoSeasStay()
        {
            // Field x -188..22, z -102..102 (Moorweg).
            bool Lake(float x0, float z0, float x1, float z1) => GroundPalette.IsLake(x0, z0, x1, z1, -188f, -102f, 22f, 102f);
            Assert.True(Lake(-149f, 63f, -124f, 91f), "a pond on the field");
            Assert.True(Lake(-33f, -137f, 6f, -98f), "a lake across the field's edge");
            Assert.True(Lake(-212f, -17f, -198f, 0f), "a lake beside the field");
            Assert.True(!Lake(-500f, -500f, 500f, 500f), "a sea all round");
            Assert.True(!Lake(-300f, -50f, 100f, 50f), "a sea across the field");
        }
    }
}
