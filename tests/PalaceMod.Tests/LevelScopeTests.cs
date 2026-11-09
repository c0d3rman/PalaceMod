using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Tests
{
    /// <summary>Shared templates validated in the context of the levels that use them (Model/Logic/LevelScope.cs).</summary>
    public static class LevelScopeTests
    {
        private static RuleDef Rule(string json) => JObject.Parse(json).ToObject<RuleDef>();

        // The blind test's case: level "snail" has a path @snail-route and $next; the unit siege-snail follows that path
        // and the entity slime-pool names the snail (is:siege-snail); level "plain" uses neither. Level "pillars" uses the
        // entity pillar, which reads $next, but doesn't declare it. "orphan" is used by no level.
        private static DesignPack Library() => new()
        {
            Id = "library",
            Name = "My library",
            Units =
            [
                JObject.Parse(@"{""id"":""siege-snail"",""side"":""enemy"",""base"":""vanilla:E Melee"",""name"":""Snail"",""movement"":{""path"":""@snail-route""}}").ToObject<UnitDef>(),
                new UnitDef { Id = "grunt", Side = UnitSide.Enemy, Base = "vanilla:E Melee", Name = "Grunt" },
            ],
            Entities =
            [
                new EntityDef { Id = "slime-pool", Kind = "prop", Rules = [Rule(@"{""when"":{""on"":""every"",""seconds"":1},""do"":[{""do"":""applyStatus"",""status"":""mired"",""target"":""all('enemy and not is:siege-snail', 3)""}]}")] },
                new EntityDef { Id = "pillar", Kind = "prop", Rules = [Rule(@"{""when"":""spawn"",""if"":""$next == 1"",""do"":[]}")] },
                new EntityDef { Id = "orphan", Kind = "prop", Rules = [Rule(@"{""when"":""spawn"",""if"":""$nowhere == 1 and @nobody.alive"",""do"":[]}")] },
            ],
            Statuses = [new StatusDef { Id = "mired", Duration = 4 }, new StatusDef { Id = "unused-status", Duration = 99999 }],
            Levels =
            [
                new LevelDef
                {
                    Id = "snail", Name = "Snail", Host = "Nordfels",
                    Logic = JObject.Parse(@"{""vars"":{""next"":1},""places"":[{""id"":""snail-route"",""kind"":""path"",""points"":[[0,0],[10,0]]}],
                        ""rules"":[{""when"":""dusk"",""do"":[{""do"":""spawn"",""entity"":""siege-snail""},{""do"":""spawn"",""entity"":""slime-pool""}]}]}").ToObject<LogicDef>(),
                },
                new LevelDef
                {
                    Id = "plain", Name = "Plain", Host = "Nordfels",
                    Logic = JObject.Parse(@"{""rules"":[{""when"":""dusk"",""do"":[{""do"":""spawn"",""entity"":""grunt""}]}]}").ToObject<LogicDef>(),
                },
                new LevelDef
                {
                    Id = "pillars", Name = "Pillars", Host = "Nordfels",
                    Logic = JObject.Parse(@"{""places"":[{""id"":""p1"",""entity"":""pillar"",""pos"":[0,0,0]}]}").ToObject<LogicDef>(),
                },
            ],
        };

        private static string Errors(System.Collections.Generic.IEnumerable<ValidationIssue> issues) =>
            string.Join("; ", issues.Where(i => i.Severity == Severity.Error));

        public static void UsesFollowsLevelsNightsAndItemsTransitively()
        {
            var lib = Library();
            var used = LevelScope.Uses(lib, lib.FindLevel("snail"));
            Assert.Equal("siege-snail", string.Join(",", used["units"]), "units");
            Assert.Equal("slime-pool", string.Join(",", used["entities"]), "entities");
            Assert.Equal("mired", string.Join(",", used["statuses"]), "status through the entity");
            used = LevelScope.Uses(lib, lib.FindLevel("plain"));
            Assert.Equal("grunt", string.Join(",", used["units"]), "plain's units");
            Assert.Equal(0, used["entities"].Count, "plain uses no entity");
        }

        public static void OneLevelsTemplateNeverBlocksAnother()
        {
            var lib = Library();
            var plain = LevelScope.For(lib, "plain");
            Assert.Equal("", Errors(Validator.Validate(plain)), "plain validates clean");
            var snail = LevelScope.For(lib, "snail");
            Assert.Equal("", Errors(Validator.Validate(snail)), "the snail level has its route and var");
        }

        public static void TemplateIsCheckedInTheUsingLevelsContext()
        {
            var lib = Library();
            var scoped = LevelScope.For(lib, "pillars");
            var errors = Validator.Validate(scoped).Where(i => i.Severity == Severity.Error).Select(i => LevelScope.Attribute(scoped, i)).ToList();
            var next = errors.FirstOrDefault(i => i.Message.Contains("$next"));
            Assert.NotNull(next, "pillar's $next is an error in a level without it: " + Errors(errors));
            Assert.Equal("entities/pillar.json", next.File, "names the library file");
            Assert.True(next.Path.StartsWith("entities[id=pillar]."), "path names the item by id: " + next.Path);
        }

        public static void LibraryIssuesAttributeLevelsAndSkipUnusedLevelRefs()
        {
            var lib = Library();
            var issues = LevelScope.LibraryIssues(lib, p => Validator.Validate(p));
            var next = issues.FirstOrDefault(i => i.Message.Contains("$next"));
            Assert.NotNull(next, "pillar's $next reported");
            Assert.Equal("pillars", next.Level, "in the level that uses it");
            Assert.True(!issues.Any(i => i.Message.Contains("snail-route")), "the snail's route is fine where it's used: " + Errors(issues));
            Assert.True(!issues.Any(i => i.Message.Contains("$nowhere") || i.Message.Contains("@nobody")), "an unused item's level refs aren't checked");
        }

        public static void AttributeMapsLevelAndNightsPaths()
        {
            var lib = Library();
            lib.FindLevel("plain").WaveSet = "plain-nights";
            lib.WaveSets.Add(new WaveSetDef { Id = "plain-nights", Host = "Nordfels" });
            var scoped = LevelScope.For(lib, "plain");
            var a = LevelScope.Attribute(scoped, new ValidationIssue(Severity.Error, "levels[0].logic.rules[0]", "x"));
            Assert.Equal("level.logic.rules[0]", a.Path, "level path");
            Assert.Equal("levels/plain.json", a.File, "level file");
            var b = LevelScope.Attribute(scoped, new ValidationIssue(Severity.Error, "waveSets[0].nights[0]", "x"));
            Assert.Equal("nights.nights[0]", b.Path, "nights path");
            Assert.Equal("levels/plain.json", b.File, "nights live in the level's file");
        }
    }
}
