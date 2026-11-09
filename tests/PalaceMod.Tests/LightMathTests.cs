using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Tests
{
    /// <summary>Light levels, cones and the sector shape (docs/builder-engine.md, "Light").</summary>
    public static class LightMathTests
    {
        private static void Near(double expected, double actual, string what, double tol = 1e-6)
        {
            if (Math.Abs(expected - actual) > tol) throw new AssertException($"{what}: expected {expected}, got {actual}");
        }

        public static void DaylightIsTheSunsLuminance()
        {
            Near(1, LightMath.Daylight(1, 1, 1, 1), "white day");
            Near(0.34, LightMath.Daylight(0.254, 0.315, 0.799, 1), "the game's night blue", 0.01);
            Near(0, LightMath.Daylight(1, 1, 1, 0), "no intensity");
            Assert.True(LightMath.Daylight(0.254, 0.315, 0.799, 1) < LightMath.LitThreshold, "night alone isn't lit");
        }

        public static void PointLightIsFullThenFades()
        {
            Near(1, LightMath.Contribution(0, 5, 10, 1, 0, 0), "well inside");
            Near(1, LightMath.Contribution(7, 0, 10, 1, 0, 0), "at 70%");
            Near(0.5, LightMath.Contribution(0, -9, 10, 1, 0, 0), "half-way through the fade");
            Near(0, LightMath.Contribution(0, 10.1, 10, 1, 0, 0), "past the range");
            Near(0.5, LightMath.Contribution(0, 1, 10, 0.5, 0, 0), "dimmer light");
        }

        public static void ConeLightsOnlyItsSector()
        {
            // A 60° cone facing east (90°).
            Near(1, LightMath.Contribution(5, 0, 10, 1, 60, 90), "straight ahead");
            Near(1, LightMath.Contribution(5, 2.5, 10, 1, 60, 90), "26.6° off, inside");
            Near(0, LightMath.Contribution(5, 3.5, 10, 1, 60, 90), "35° off, outside");
            Near(0, LightMath.Contribution(-5, 0, 10, 1, 60, 90), "behind");
            Assert.True(LightMath.InCone(-1, 10, 40, 350), "wraps round north");
        }

        public static void LevelAddsUpAndCaps()
        {
            Near(0.34 + 0.5, LightMath.Level(0.34, [0.5]), "night plus a light");
            Near(1, LightMath.Level(0.34, [0.5, 0.5]), "capped at 1");
            Assert.True(LightMath.Level(0.34, []) < LightMath.LitThreshold, "dark without lights");
        }

        public static void SectorShape()
        {
            Assert.True(LightMath.SectorContains(0, 5, 10, 60), "ahead");
            Assert.True(!LightMath.SectorContains(0, -5, 10, 60), "behind");
            Assert.True(!LightMath.SectorContains(4, 5, 10, 60), "38.7° off a 60° sector");
            Assert.True(LightMath.SectorContains(-4, -5, 10, 360), "a full circle");
            var o = LightMath.SectorOutline(10, 90);
            Assert.True(o[0] == (0, 0), "starts at the apex");
            Near(-Math.Sqrt(50), o[1].x, "left edge x", 1e-6);
            Near(Math.Sqrt(50), o[o.Count - 1].x, "right edge x", 1e-6);
            Assert.True(o.Skip(1).All(p => Math.Abs(Math.Sqrt(p.x * p.x + p.z * p.z) - 10) < 1e-6), "arc on the radius");
        }

        public static void AngleHelpers()
        {
            Near(-20, LightMath.DeltaAngle(350, 10), "across north");
            Near(180, LightMath.DeltaAngle(0, 180), "opposite");
            Near(270, LightMath.Yaw(-90), "negative yaw");
        }

        private static List<ValidationIssue> Validate(string pack)
        {
            var root = JObject.Parse(@"{""format"":""palace-builder"",""version"":3,""id"":""a.b"",""levels"":[{""id"":""l"",""host"":""Nordfels"",""logic"":{}}]}");
            foreach (var p in JObject.Parse(pack).Properties()) root[p.Name] = p.Value;
            var issues = new List<ValidationIssue>();
            var parsed = PackSerializer.FromJObject(root, issues);
            Assert.NotNull(parsed, "parses: " + string.Join("; ", issues));
            return Validator.Validate(parsed);
        }

        public static void ValidatorKnowsLightsSectorsAndTheNewActions()
        {
            var issues = Validate(@"{ ""entities"": [{ ""id"": ""beam"", ""kind"": ""area"",
                    ""area"": { ""shape"": { ""sector"": { ""radius"": 40, ""angle"": 60 } }, ""show"": ""fill"" },
                    ""light"": { ""cone"": 60, ""range"": 40, ""color"": ""#fff3a0"" },
                    ""rules"": [{ ""when"": { ""on"": ""every"", ""seconds"": 1 }, ""if"": ""daylight < 0.5 and self.lit and lightAt(self) > 0.2"", ""do"": [
                        { ""do"": ""turn"", ""toward"": ""king"", ""speed"": 90 },
                        { ""do"": ""setShape"", ""angle"": ""self.facing / 3"" },
                        { ""do"": ""setLight"", ""target"": ""king"", ""range"": 8, ""for"": ""night"" },
                        { ""do"": ""lighting"", ""brightness"": 0.15, ""phase"": ""night"", ""for"": 20 },
                        { ""do"": ""applyStatus"", ""target"": ""all('enemy and lit')"", ""status"": ""s"" }] }] }],
                ""units"": [{ ""id"": ""torchbearer"", ""base"": ""vanilla:E Melee"", ""light"": { ""range"": 6 } }],
                ""statuses"": [{ ""id"": ""s"" }] }");
            Assert.True(!issues.Any(i => i.Severity == Severity.Error), string.Join("\n", issues));
            var bad = Validate(@"{ ""entities"": [{ ""id"": ""b"", ""kind"": ""prop"", ""light"": { ""color"": ""yellow"", ""cone"": 200 } }] }");
            Assert.True(bad.Any(i => i.Path.EndsWith("light.color")), "bad colour: " + string.Join("\n", bad));
            Assert.True(bad.Any(i => i.Path.EndsWith("light.cone")), "cone over 179: " + string.Join("\n", bad));
        }
    }
}
