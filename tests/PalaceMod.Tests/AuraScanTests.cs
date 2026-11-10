using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;

namespace PalaceMod.Tests
{
    /// <summary>Aura rings (Logic/Runtime/AuraScan.cs): which rules imply a ring, its radius, kind and target.</summary>
    public static class AuraScanTests
    {
        private static List<RingSpec> Scan(string json)
        {
            var o = JObject.Parse(json);
            var when = o["when"] is JValue v ? new JObject { ["on"] = (string)v } : (JObject)o["when"];
            return AuraScan.FromRule(o, (string)when["on"], when);
        }

        public static void ShieldAuraIsABuffRingOnItsOwner()
        {
            var r = Scan("""{ "when": { "on": "every", "seconds": 1 }, "do": [{ "do": "applyStatus", "status": "warded", "target": "all('ally and unit', 6)" }] }""");
            Assert.NotNull(r, "a ring");
            Assert.Equal(1, r.Count, "one ring");
            Assert.Equal(6f, r[0].Radius, "radius");
            Assert.Equal("buff", r[0].Kind, "kind");
            Assert.Null(r[0].TargetLabel, "on the owner");
        }

        public static void LabelFilterNamesTheTarget()
        {
            var r = Scan("""{ "when": { "on": "every", "seconds": 0.5 }, "do": [{ "do": "if", "cond": "count('#lantern', 8) == 0", "then": [{ "do": "applyStatus", "status": "veiled" }] }] }""");
            Assert.NotNull(r, "a ring");
            Assert.Equal(8f, r[0].Radius, "radius");
            Assert.Equal("lantern", r[0].TargetLabel, "on the lanterns");
            Assert.Equal("neutral", r[0].Kind, "kind");
        }

        public static void NearDamageIsHarm()
        {
            var r = Scan("""{ "when": { "on": "near", "filter": "foe", "range": 4 }, "do": [{ "do": "damage", "target": "other", "amount": 5 }] }""");
            Assert.NotNull(r, "a ring");
            Assert.Equal(4f, r[0].Radius, "near's range");
            Assert.Equal("harm", r[0].Kind, "kind");
        }

        public static void OneShotsEventsAndOptOutsHaveNoRing()
        {
            Assert.Null(Scan("""{ "when": "death", "do": [{ "do": "damage", "target": "all('foe', 3)", "amount": 40 }] }"""), "event rule");
            Assert.Null(Scan("""{ "when": { "on": "near", "filter": "king", "range": 2 }, "do": [{ "do": "damage", "target": "all('foe', 3)", "amount": 40 }, { "do": "destroy", "drops": true }] }"""), "kamikaze");
            Assert.Null(Scan("""{ "when": { "on": "every", "seconds": 1 }, "ring": false, "do": [{ "do": "heal", "target": "all('ally', 5)", "amount": 2 }] }"""), "ring: false");
        }

        public static void RingOptionsOverride()
        {
            var r = Scan("""{ "when": "death", "ring": { "color": "#ff00ff", "style": "soft", "kind": "harm" }, "do": [{ "do": "damage", "target": "all('foe', 3)", "amount": 40 }] }""");
            Assert.NotNull(r, "forced ring");
            Assert.Equal("#ff00ff", r[0].Color, "colour");
            Assert.Equal("soft", r[0].Style, "style");
            var e = AuraScan.Explicit(JToken.Parse("""[{ "radius": 10, "style": "line" }]"""));
            Assert.Equal(10f, e[0].Radius, "explicit radius");
            Assert.True(AuraScan.RingsOff(JToken.Parse("false")), "rings: false");
        }
    }
}
