using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Requirements;

namespace PalaceMod.Tests
{
    public static class RequirementsTests
    {
        private static string V1Pack(string adds, string extra = "") => $@"{{
  ""format"": ""palace-builder"", ""version"": 1, ""id"": ""t.req"", ""name"": ""Req"",
  ""levels"": [ {{ ""id"": ""l1"", ""name"": ""L"", ""host"": ""Nordfels"", {extra}
    ""buildSlots"": {{ ""add"": [ {adds} ] }} }} ]
}}";

        private static DesignPack Read(string json, List<ValidationIssue> issues = null)
        {
            issues ??= [];
            var pack = PackSerializer.Read(json, issues);
            Assert.NotNull(pack, "pack read: " + string.Join("; ", issues));
            return pack;
        }

        public static void MigratesActivatorToRequires()
        {
            var pack = Read(V1Pack(@"
                { ""id"": ""a"", ""base"": ""scene:House 1"", ""pos"": [1,0,1], ""activator"": ""House 3"", ""activatorLevel"": 1 },
                { ""id"": ""b"", ""base"": ""scene:House 1"", ""pos"": [2,0,1], ""activator"": ""a"" },
                { ""id"": ""c"", ""base"": ""scene:House 1"", ""pos"": [3,0,1] },
                { ""id"": ""d"", ""base"": ""scene:House 1"", ""pos"": [4,0,1], ""activatorLevel"": 2 }"));
            Assert.Equal(DesignPack.CurrentVersion, pack.Version, "version");
            var adds = pack.Levels[0].BuildSlots.Add;
            Assert.Equal("House 3", adds[0].Requires.Slot, "host activator");
            Assert.Equal(1, adds[0].Requires.Level, "host activator level");
            Assert.Equal("add:a", adds[1].Requires.Slot, "added activator becomes add:<id>");
            Assert.Equal(0, adds[1].Requires.Level, "default level");
            Assert.Null(adds[2].Requires, "no activator keeps the base's");
            Assert.Null(adds[3].Requires, "a level without activator is dropped");
            string written = PackSerializer.Write(pack);
            Assert.True(!written.Contains("activator"), "old fields aren't written");
            Assert.True(written.Contains("\"requires\""), "requires written");
            Assert.Equal(0, Validator.Validate(pack).Count(i => i.Severity == Severity.Error), "migrated pack validates");
        }

        public static void MigrationKeepsUnrelatedFieldsAndIsIdempotent()
        {
            var pack = Read(V1Pack(@"{ ""id"": ""a"", ""base"": ""scene:House 1"", ""pos"": [1,0,1], ""activator"": ""a2"", ""custom"": 5 },
                                      { ""id"": ""a2"", ""base"": ""scene:House 1"", ""pos"": [1,0,5] }"));
            Assert.Equal(5, (int)pack.Levels[0].BuildSlots.Add[0].Extra["custom"], "unknown field kept");
            var again = Read(PackSerializer.Write(pack));
            Assert.Equal(PackSerializer.WriteCompact(pack), PackSerializer.WriteCompact(again), "v2 reads back unchanged");
        }

        public static void SampleStillReads()
        {
            var pack = ShareCodecTests.Sample();
            Assert.Equal(DesignPack.CurrentVersion, pack.Version, "sample upgraded");
        }

        public static void ValidatorRefusesLoops()
        {
            var pack = Read(V1Pack(@"
                { ""id"": ""a"", ""base"": ""scene:House 1"", ""pos"": [1,0,1], ""requires"": { ""slot"": ""add:b"" } },
                { ""id"": ""b"", ""base"": ""scene:House 1"", ""pos"": [2,0,1], ""requires"": { ""slot"": ""add:a"" } }"));
            var errors = Validator.Validate(pack).Where(i => i.Severity == Severity.Error).ToList();
            Assert.True(errors.Any(e => e.Message.Contains("loop")), "loop reported: " + string.Join("; ", errors));
        }

        public static void ValidatorChecksRequirementShape()
        {
            var pack = Read(V1Pack(@"
                { ""id"": ""a"", ""base"": ""scene:House 1"", ""pos"": [1,0,1], ""requires"": { ""slot"": ""add:nope"" } },
                { ""id"": ""b"", ""base"": ""scene:House 1"", ""pos"": [2,0,1], ""requires"": { ""slot"": ""castle"", ""level"": 11 } },
                { ""id"": ""c"", ""base"": ""scene:House 1"", ""pos"": [3,0,1], ""requires"": { ""slot"": ""add:c"" } },
                { ""id"": ""d"", ""base"": ""scene:House 1"", ""pos"": [4,0,1], ""upgradeCap"": 50 }", @"""base"": ""bogus"","));
            var errors = Validator.Validate(pack).Where(i => i.Severity == Severity.Error).Select(i => i.Path).ToList();
            foreach (var p in new[] { "add[0].requires.slot", "add[1].requires.level", "add[2].requires.slot", "add[3].upgradeCap", "levels[0].base" })
                Assert.True(errors.Any(e => e.EndsWith(p)), $"error at {p}: {string.Join(", ", errors)}");
        }

        public static void ValidatorAcceptsNewSpawnLinesInWaves()
        {
            var pack = Read(@"{ ""format"": ""palace-builder"", ""version"": 2, ""id"": ""t.lines"", ""name"": ""L"",
                ""waveSets"": [ { ""id"": ""w"", ""host"": ""Nordfels"", ""nights"": [ { ""spawns"": [ { ""unit"": ""vanilla:E Melee"", ""count"": 1, ""line"": ""north"" } ] } ] } ],
                ""levels"": [ { ""id"": ""l"", ""host"": ""Nordfels"", ""waveSet"": ""w"", ""spawnLines"": { ""add"": [ { ""id"": ""north"", ""pos"": [0,0,50] } ] } } ] }");
            var issues = Validator.Validate(pack, new FakeGame());
            Assert.True(!issues.Any(i => i.Path.Contains("spawns")), "added line is a known line: " + string.Join("; ", issues));
            Assert.True(!issues.Any(i => i.Path.Contains("spawnLines.add")), "no add warning: " + string.Join("; ", issues));
        }

        private sealed class FakeGame : IValidationContext
        {
            public bool HasVanillaUnit(string id, string side) => true;
            public bool HasVanillaWeapon(string id) => true;
            public bool HasHost(string scene) => true;
            public IReadOnlyCollection<string> SpawnLines(string scene) => ["Spawn Line A"];
            public bool HasPackUnit(string packId, string unitId) => false;
        }

        // ---- The requirement graph -------------------------------------------------------------------------------

        private static HostSlots Host() => new()
        {
            Castle = "Castle",
            Native = new()
            {
                ["Castle"] = null,
                ["House 1"] = new SlotRequirement { Slot = "Castle", Level = 0 },
                ["House 2"] = new SlotRequirement { Slot = "House 1", Level = 0 },
                ["Tower"] = new SlotRequirement { Slot = "Castle", Level = 1 },
            },
            Levels = new() { ["Castle"] = 4, ["House 1"] = 3, ["House 2"] = 3, ["Tower"] = 3 },
        };

        private static LevelDef Level() => new() { Id = "l", Host = "Nordfels", BuildSlots = new BuildSlotOverlay { Add = [] } };

        public static void EffectiveUsesHostThenLevel()
        {
            var l = Level();
            l.BuildSlots.Add.Add(new BuildSlotAdd { Id = "x", Base = "scene:House 2", Pos = [0, 0, 0] });
            l.BuildSlots.Add.Add(new BuildSlotAdd { Id = "y", Base = "scene:House 2", Pos = [0, 0, 0], Requires = new SlotRequirement { Slot = "add:x" } });
            LevelRequirements.Set(l, "Tower", LevelRequirements.CastleLevel(3));
            var eff = LevelRequirements.Effective(l, Host());
            Assert.Equal("House 1", eff["add:x"].Slot, "added slot takes its base's");
            Assert.Equal("add:x", eff["add:y"].Slot, "explicit added");
            Assert.Equal("Castle", eff["Tower"].Slot, "castle resolved to its name");
            Assert.Equal(2, eff["Tower"].Level, "castle level 3 = level 2");
            Assert.Equal(1, LevelRequirements.CastleLevelVia(eff, "Castle", "add:y"), "y waits for castle level 1 through x");
            Assert.Equal(3, LevelRequirements.CastleLevelVia(eff, "Castle", "Tower"), "tower castle level");
        }

        public static void EmptyBaseRemovesHostSlotsAndDefaultsToCastle()
        {
            var l = Level();
            l.Base = LevelBases.Cleared;
            l.BuildSlots.Add.Add(new BuildSlotAdd { Id = "x", Base = "scene:House 2", Pos = [0, 0, 0] });
            var eff = LevelRequirements.Effective(l, Host());
            Assert.True(!eff.ContainsKey("House 1"), "host slots cleared");
            Assert.True(eff.ContainsKey("Castle"), "castle kept");
            Assert.Equal("Castle", eff["add:x"].Slot, "added slot needs the castle");
            Assert.Equal(0, eff["add:x"].Level, "castle level 1");
        }

        public static void SetAndPruneHostEdits()
        {
            var l = Level();
            LevelRequirements.Set(l, "House 2", LevelRequirements.FromStart);
            Assert.Equal(1, l.BuildSlots.Edit.Count, "edit created");
            Assert.Null(l.BuildSlots.Edit[0].Requires.Slot, "from the start");
            LevelRequirements.Set(l, "House 2", null);
            Assert.Equal(0, l.BuildSlots.Edit.Count, "empty edit pruned");
            LevelRequirements.SetCap(l, "House 2", 0);
            Assert.Equal(0, l.BuildSlots.Edit[0].UpgradeCap, "cap set");
        }

        public static void CyclesAndWouldCycle()
        {
            var l = Level();
            LevelRequirements.Set(l, "House 1", new SlotRequirement { Slot = "House 2" });
            var eff = LevelRequirements.Effective(l, Host());
            var loops = LevelRequirements.Cycles(eff);
            Assert.Equal(1, loops.Count, "one loop through the host's own House 2 > House 1");
            Assert.True(loops[0].Contains("House 1") && loops[0].Contains("House 2"), "loop members");
            var clean = LevelRequirements.Effective(Level(), Host());
            Assert.True(LevelRequirements.WouldCycle(clean, "House 1", "House 2"), "House 1 needing House 2 would loop");
            Assert.True(!LevelRequirements.WouldCycle(clean, "House 2", "Tower"), "House 2 needing Tower is fine");
        }

        public static void ForgetClosesTheChain()
        {
            var l = Level();
            l.BuildSlots.Add.Add(new BuildSlotAdd { Id = "x", Base = "scene:House 2", Pos = [0, 0, 0], Requires = LevelRequirements.CastleLevel(2) });
            l.BuildSlots.Add.Add(new BuildSlotAdd { Id = "y", Base = "scene:House 2", Pos = [0, 0, 0], Requires = new SlotRequirement { Slot = "add:x" } });
            LevelRequirements.Set(l, "Tower", new SlotRequirement { Slot = "add:x", Level = 1 });
            int n = LevelRequirements.ForgetSlot(l, Host(), "add:x", ForgetMode.CloseUp);
            Assert.Equal(2, n, "two dependents");
            Assert.Equal("castle", l.BuildSlots.Add[1].Requires.Slot, "y now needs the castle");
            Assert.Equal(1, l.BuildSlots.Add[1].Requires.Level, "at x's level");
            Assert.Equal("castle", LevelRequirements.Explicit(l, "Tower").Slot, "tower too");
            LevelRequirements.ForgetSlot(l, Host(), "House 1", ForgetMode.FromStart);
            Assert.Null(LevelRequirements.Explicit(l, "House 2").Slot, "House 2 from the start");
        }

        public static void SlotRefKeys()
        {
            Assert.Equal("House 1", SlotRefs.FromKey("slot:House 1"), "slot key");
            Assert.Equal("add:x", SlotRefs.FromKey("add:x"), "add key");
            Assert.Null(SlotRefs.FromKey("prop:p"), "prop isn't a slot");
            Assert.Equal("slot:Castle", SlotRefs.ToKey("castle", "Castle"), "castle key");
        }

        // ---- Chain presets ---------------------------------------------------------------------------------------

        private static List<ChainNode> Row() => [new("a", 30, 0), new("b", 10, 0), new("c", 20, 0), new("d", 10, 8)];

        private static Dictionary<string, SlotRequirement> Chain(ChainMode mode, IReadOnlyList<ChainNode> nodes = null) =>
            ChainPreset.Build(nodes ?? Row(), 0, 0, new ChainOptions { Mode = mode, CastleLevel = 2, LinkLevel = 0 }).ToDictionary(x => x.slot, x => x.requires);

        public static void ChainInSelectionOrder()
        {
            var r = Chain(ChainMode.SelectionOrder);
            Assert.Equal("castle", r["a"].Slot, "first needs the castle");
            Assert.Equal(1, r["a"].Level, "castle level 2");
            Assert.Equal("a", r["b"].Slot, "b needs a");
            Assert.Equal("b", r["c"].Slot, "c needs b");
            Assert.Equal("c", r["d"].Slot, "d needs c");
        }

        public static void ChainNearestNeighbour()
        {
            var r = Chain(ChainMode.NearestNeighbour);
            Assert.Equal("castle", r["b"].Slot, "b is nearest the castle");
            Assert.Equal("b", r["d"].Slot, "d nearest to b (8)");
            Assert.Equal("d", r["c"].Slot, "then c (12.8 from d)");
            Assert.Equal("c", r["a"].Slot, "then a");
        }

        public static void ChainBranchingIsAMinimumSpanningTree()
        {
            var r = Chain(ChainMode.Branching);
            Assert.Equal("castle", r["b"].Slot, "b from the castle");
            Assert.Equal("b", r["c"].Slot, "c from b");
            Assert.Equal("b", r["d"].Slot, "d from b");
            Assert.Equal("c", r["a"].Slot, "a from c");
            // Two clusters on opposite sides both hang off the castle.
            var two = Chain(ChainMode.Branching, [new("e", 10, 0), new("w", -10, 0), new("e2", 20, 0)]);
            Assert.Equal("castle", two["e"].Slot, "east from castle");
            Assert.Equal("castle", two["w"].Slot, "west from castle");
            Assert.Equal("e", two["e2"].Slot, "e2 from e");
        }

        public static void ChainsNeverLoop()
        {
            foreach (ChainMode mode in new[] { ChainMode.Branching, ChainMode.NearestNeighbour, ChainMode.SelectionOrder })
            {
                var r = Chain(mode);
                var eff = r.ToDictionary(kv => kv.Key, kv => kv.Value.Slot == "castle" ? null : kv.Value);
                Assert.Equal(0, LevelRequirements.Cycles(eff).Count, $"{mode} has no loops");
                Assert.True(r.Values.Any(v => v.Slot == "castle"), $"{mode} reaches the castle");
            }
        }

        public static void AutoLinkPicksNearestHouse()
        {
            var houses = new[] { new ChainNode("h1", 10, 0), new ChainNode("h2", 40, 0) };
            Assert.Equal("h2", ChainPreset.AutoLink(true, 35, 0, houses).Slot, "nearest house");
            Assert.Equal("castle", ChainPreset.AutoLink(true, 100, 100, houses).Slot, "none in range: castle");
            Assert.Equal("castle", ChainPreset.AutoLink(false, 35, 0, houses).Slot, "not a house: castle");
        }
    }
}
