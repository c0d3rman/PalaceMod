using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    /// <summary>spawnLines.remove: the format round-trips, and the validator refuses waves that still spawn from a
    /// removed line, unknown names and added lines.</summary>
    public static class SpawnLineTests
    {
        private static DesignPack Pack(string remove, string waveLine = "Spawn Line A", bool waveSet = true)
        {
            var issues = new List<ValidationIssue>();
            var pack = PackSerializer.Read($@"{{ ""format"": ""palace-builder"", ""version"": 2, ""id"": ""t.remove"", ""name"": ""R"",
                ""waveSets"": [ {{ ""id"": ""w"", ""host"": ""Nordfels"", ""nights"": [ {{ ""spawns"": [ {{ ""unit"": ""vanilla:E Melee"", ""count"": 1, ""line"": ""{waveLine}"" }} ] }} ] }} ],
                ""levels"": [ {{ ""id"": ""l"", ""host"": ""Nordfels"", {(waveSet ? @"""waveSet"": ""w""," : "")}
                    ""spawnLines"": {{ ""add"": [ {{ ""id"": ""north"", ""pos"": [0,0,50] }} ], ""remove"": [{remove}] }} }} ] }}", issues);
            Assert.NotNull(pack, "reads: " + string.Join("; ", issues));
            return pack;
        }

        private static List<ValidationIssue> Errors(DesignPack pack, IValidationContext game = null) =>
            Validator.Validate(pack, game ?? new Game()).Where(i => i.Severity == Severity.Error).ToList();

        public static void RemoveRoundTrips()
        {
            var pack = Pack(@"""Spawn Line B""", waveLine: "north");
            Assert.Equal("Spawn Line B", pack.FindLevel("l").SpawnLines.Remove.Single(), "read");
            var again = PackSerializer.Read(PackSerializer.Write(pack), new List<ValidationIssue>());
            Assert.Equal("Spawn Line B", again.FindLevel("l").SpawnLines.Remove.Single(), "written and read back");
        }

        public static void RemovingAnUnusedHostLineIsFine()
        {
            var errors = Errors(Pack(@"""Spawn Line B""", waveLine: "north"));
            Assert.True(errors.Count == 0, "no errors: " + string.Join("; ", errors));
        }

        public static void WaveFromRemovedLineIsAnError()
        {
            var errors = Errors(Pack(@"""Spawn Line A"""));
            Assert.True(errors.Any(e => e.Path == "waveSets[0].nights[0].spawns[0].line" && e.Message.Contains("removed")),
                "the spawn is refused: " + string.Join("; ", errors));
        }

        public static void RemoveNeedsAHostLine()
        {
            var errors = Errors(Pack(@"""Nope"", ""north""", waveLine: "Spawn Line B"));
            Assert.True(errors.Any(e => e.Path == "levels[0].spawnLines.remove[0]" && e.Message.Contains("no spawn line")), "unknown name: " + string.Join("; ", errors));
            Assert.True(errors.Any(e => e.Path == "levels[0].spawnLines.remove[1]" && e.Message.Contains("added")), "added line: " + string.Join("; ", errors));
        }

        public static void VanillaNightsFromRemovedLineAreAnError()
        {
            var errors = Errors(Pack(@"""Spawn Line A""", waveSet: false));
            Assert.True(errors.Any(e => e.Path == "levels[0].spawnLines.remove" && e.Message.Contains("map's own nights")), "vanilla waves: " + string.Join("; ", errors));
            Assert.True(Errors(Pack(@"""Spawn Line B""", waveSet: false)).Count == 0, "a line the vanilla nights don't use can go");
        }

        private sealed class Game : IValidationContext, IHostWavesContext
        {
            public bool HasVanillaUnit(string id, string side) => true;
            public bool HasVanillaWeapon(string id) => true;
            public bool HasHost(string scene) => true;
            public IReadOnlyCollection<string> SpawnLines(string scene) => ["Spawn Line A", "Spawn Line B"];
            public bool HasPackUnit(string packId, string unitId) => false;
            public IReadOnlyCollection<string> VanillaWaveLines(string scene) => ["Spawn Line A"];
        }
    }
}
