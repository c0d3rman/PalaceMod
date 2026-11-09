using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    public static class ColorMathTests
    {
        private static bool Near(float a, float b, float eps = 0.01f) => Math.Abs(a - b) < eps;

        public static void ParsesHex()
        {
            Assert.True(ColorMath.TryParseHex("#8fd14f", out var c) && Near(c.r, 0x8f / 255f) && Near(c.g, 0xd1 / 255f) && Near(c.b, 0x4f / 255f), "#rrggbb");
            Assert.True(ColorMath.TryParseHex("8FD14F", out _), "no #, capitals");
            Assert.True(ColorMath.TryParseHex("#f80", out var s) && Near(s.r, 1f) && Near(s.g, 0x88 / 255f) && Near(s.b, 0f), "short form");
            Assert.True(!ColorMath.TryParseHex("#12345", out _) && !ColorMath.TryParseHex("green", out _) && !ColorMath.TryParseHex(null, out _), "junk refused");
            Assert.Equal("#8fd14f", ColorMath.Hex(c), "back to hex");
        }

        public static void HsvRoundTrips()
        {
            foreach (var hex in new[] { "#ff0000", "#00ff00", "#0000ff", "#8fd14f", "#2a2a33", "#ffffff", "#000000", "#c0573a" })
            {
                ColorMath.TryParseHex(hex, out var c);
                var (h, s, v) = ColorMath.ToHsv(c);
                Assert.Equal(hex, ColorMath.Hex(ColorMath.FromHsv(h, s, v)), "round trip " + hex);
            }
            var red = ColorMath.ToHsv((1f, 0f, 0f));
            Assert.True(Near(red.h, 0f) && Near(red.s, 1f) && Near(red.v, 1f), "red is hue 0");
            var blue = ColorMath.ToHsv((0f, 0f, 1f));
            Assert.True(Near(blue.h, 2f / 3f), "blue is hue 2/3");
        }

        // The game's enemy material: a pale pink lit side and a deep red shade (docs/builder-design.md).
        private static readonly (float, float, float) EnemyLight = (1f, 0.90f, 0.86f), EnemyShade = (0.69f, 0f, 0.16f);

        public static void ShadeFollowsTheColour()
        {
            // A saturated blue gets a darker, still blue shade (not the scheme's red).
            var shade = ColorMath.ShadeFor((0.2f, 0.4f, 1f), EnemyLight, EnemyShade);
            var (h, s, v) = ColorMath.ToHsv(shade);
            Assert.True(h > 0.55f && h < 0.75f, $"blue shade hue {h}");
            Assert.True(v < 1f && v > 0.4f, $"darker: {v}");
            Assert.True(s > 0.7f, $"saturated: {s}");
            // A grey stays grey.
            var grey = ColorMath.ToHsv(ColorMath.ShadeFor((0.6f, 0.6f, 0.6f), EnemyLight, EnemyShade));
            Assert.True(grey.s < 0.01f && grey.v < 0.6f, $"grey shade: s {grey.s} v {grey.v}");
            // The game's own pairing comes back (roughly) for its own lit colour.
            var own = ColorMath.ShadeFor(EnemyLight, EnemyLight, EnemyShade);
            Assert.True(ColorMath.Distance(own, EnemyShade) < 0.35f, $"own pairing close to the scheme's: {ColorMath.Hex(own)}");
        }

        public static void ValidatesColors()
        {
            var pack = new DesignPack { Id = "p", Name = "P" };
            pack.Units.Add(new UnitDef
            {
                Id = "blue", Base = "vanilla:E Racer", Name = "Blue",
                Visuals = new VisualsDef { Colors = new Dictionary<string, string> { ["body"] = "#3366ff", ["Metal"] = "#aaaaaa" } },
            });
            List<ValidationIssue> issues;
            try { issues = Validator.Validate(pack); }
            catch (TypeInitializationException) { return; } // the validator's tag table needs the game's assembly
            Assert.True(!issues.Any(i => i.Path.StartsWith("units[0].visuals")), "valid colours: " + string.Join("; ", issues.Select(i => i.Path + " " + i.Message)));
            pack.Units[0].Visuals.Colors["body"] = "blue";
            Assert.True(Validator.Validate(pack).Any(i => i.Path == "units[0].visuals.colors.body" && i.Severity == Severity.Error), "a bad colour is an error");
            var json = PackSerializer.WriteCompact(pack);
            Assert.True(json.Contains("\"colors\":{\"body\":\"blue\",\"Metal\":\"#aaaaaa\"}"), "serialized as visuals.colors: " + json);
        }
    }
}
