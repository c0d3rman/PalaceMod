using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Tests
{
    /// <summary>The behaviour engine's data model (Model/Logic): expressions, schema, presets, validation, migration,
    /// and every docs/builder-engine.md example (tests/logic-examples) validating clean.</summary>
    public static class LogicTests
    {
        internal static string RepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p) || Directory.Exists(p)) return p;
            }
            throw new AssertException($"can't find {relative} above {AppContext.BaseDirectory}");
        }

        private static string Errors(IEnumerable<ValidationIssue> issues) =>
            string.Join("\n    ", issues.Where(i => i.Severity == Severity.Error).Select(i => i.ToString()));

        // ---- Shared expression vectors ------------------------------------------------------------------------

        private static HashSet<string> Set(JToken t) => t == null || t.Type == JTokenType.Null ? null : new HashSet<string>(t.Select(x => (string)x));

        private static CheckScope ScopeOf(JObject def, JObject over)
        {
            var o = (JObject)def.DeepClone();
            foreach (var p in over?.Properties() ?? []) o[p.Name] = p.Value;
            string ev = o["event"]?.Type == JTokenType.String ? (string)o["event"] : null;
            var scope = new CheckScope
            {
                Event = ev == null ? null : LogicSchema.Events[ev],
                LevelScript = (bool?)o["levelScript"] ?? false,
                HasIt = (bool?)o["it"] ?? false,
                HasSpawned = (bool?)o["spawned"] ?? false,
                LevelVars = Set(o["levelVars"]),
                SelfVars = Set(o["selfVars"]),
                AnyEntityVars = Set(o["anyVars"]),
                Names = Set(o["names"]),
                Templates = Set(o["templates"]),
                Statuses = Set(o["statuses"]),
            };
            if (o["extraRoles"] is JObject extra)
            {
                scope.ExtraRoles = new Dictionary<string, LType>();
                foreach (var p in extra.Properties()) scope.ExtraRoles[p.Name] = Enum.Parse<LType>((string)p.Value, ignoreCase: true);
            }
            return scope;
        }

        public static void SharedExpressionCases()
        {
            var root = JObject.Parse(File.ReadAllText(RepoFile("tests/logic-expr-cases.json")));
            var def = (JObject)root["defaultScope"];
            var failures = new List<string>();
            foreach (JObject c in root["cases"])
            {
                string src = (string)c["expr"];
                var issues = new List<ExprIssue>();
                var t = ExprChecker.Check(src, ScopeOf(def, c["scope"] as JObject), issues, out var node);
                if (c["error"] != null)
                {
                    var hit = issues.FirstOrDefault(i => i.Message.Contains((string)c["error"]));
                    if (issues.Count == 0) failures.Add($"{src}: expected error \"{c["error"]}\", got none (type {t})");
                    else if (hit.Message == null) failures.Add($"{src}: expected error \"{c["error"]}\", got \"{issues[0].Message}\"");
                    else if (c["col"] != null && hit.Col + 1 != (int)c["col"]) failures.Add($"{src}: error at col {hit.Col + 1}, expected {(int)c["col"]}");
                    continue;
                }
                if (issues.Count > 0) { failures.Add($"{src}: unexpected error {issues[0]}"); continue; }
                if (c["type"] != null && LogicSchema.TypeName(t) != (string)c["type"]) failures.Add($"{src}: type {LogicSchema.TypeName(t)}, expected {c["type"]}");
                if (c["ast"] != null && node.ToString() != (string)c["ast"]) failures.Add($"{src}: ast {node}, expected {c["ast"]}");
            }
            foreach (JObject c in root["interp"])
            {
                var parts = Expr.ParseInterp((string)c["text"], out string err, out _);
                if (c["error"] != null) { if (parts != null || !err.Contains((string)c["error"])) failures.Add($"interp {c["text"]}: expected error {c["error"]}, got {err}"); continue; }
                string got = string.Join("|", parts.Select(p => p is ExprNode n ? n.ToString() : (string)p));
                string want = string.Join("|", c["parts"].Select(x => (string)x));
                if (got != want) failures.Add($"interp {c["text"]}: {got}, expected {want}");
            }
            foreach (JObject c in root["filters"])
            {
                var f = Filter.Parse((string)c["filter"], out string err, out _);
                if (c["error"] != null) { if (f != null || !err.Contains((string)c["error"])) failures.Add($"filter {c["filter"]}: expected error {c["error"]}, got {err ?? f.ToString()}"); continue; }
                if (f == null) { failures.Add($"filter {c["filter"]}: {err}"); continue; }
                if (f.ToString() != (string)c["ast"]) failures.Add($"filter {c["filter"]}: {f}, expected {c["ast"]}");
            }
            Assert.True(failures.Count == 0, string.Join("\n    ", failures));
        }

        // ---- Schema ------------------------------------------------------------------------------------------

        public static void TagsMatchTheGame()
        {
            var game = new HashSet<string>(DescriptionsTests.GameTags.Where(n => n != "NONE"));
            Assert.True(game.SetEquals(LogicSchema.Tags), "LogicSchema.Tags != TagManager.ETag: " +
                string.Join(", ", game.Except(LogicSchema.Tags).Concat(LogicSchema.Tags.Except(game))));
        }

        public static void SchemaIsWellFormed()
        {
            foreach (var a in LogicSchema.Actions.Values)
            {
                Assert.True(a.Description?.Length > 0, a.Name + " has a description");
                foreach (var p in a.Params)
                {
                    Assert.True(LogicSchema.ParamTypes.Contains(p.Type), $"{a.Name}.{p.Name}: type {p.Type} is in ParamTypes");
                    Assert.True(p.Name != "do", $"{a.Name}: a param named \"do\" would collide with the action's own key");
                    Assert.True(p.Type != "enum" || p.Options != null || (a.Name == "setStat" && p.Name == "stat"), $"{a.Name}.{p.Name}: enum has options");
                }
                Assert.Equal(a.Params.Length, a.Params.Select(p => p.Name).Distinct().Count(), a.Name + " param names unique");
            }
            foreach (var e in LogicSchema.Events.Values)
                foreach (var p in e.Params) Assert.True(LogicSchema.ParamTypes.Contains(p.Type), $"event {e.Name}.{p.Name}: type {p.Type}");
            var dump = LogicSchema.Dump();
            Assert.True(dump["actions"].Count() == LogicSchema.Actions.Count, "dump lists every action");
            Assert.True(dump["tags"].Count() == LogicSchema.Tags.Count, "dump lists the tags");
            // The doc's action table names every action.
            string doc = File.ReadAllText(RepoFile("docs/builder-engine.md"));
            foreach (var name in LogicSchema.Actions.Keys) Assert.True(doc.Contains($"`{name}`"), $"docs/builder-engine.md mentions action {name}");
            foreach (var name in LogicSchema.Events.Keys) Assert.True(doc.Contains($"`{name}`"), $"docs/builder-engine.md mentions event {name}");
        }

        /// <summary>agent-kit/logic-schema.json is generated from LogicSchema (palace.py's offline checks read it).
        /// Regenerate: UPDATE_LOGIC_SCHEMA=1 dotnet run -- LogicSchemaFile.</summary>
        public static void LogicSchemaFileIsCurrent()
        {
            string path = RepoFile("agent-kit/palace.py").Replace("palace.py", "logic-schema.json");
            string want = LogicSchema.DumpWithPresets().ToString(Newtonsoft.Json.Formatting.Indented) + "\n";
            if (Environment.GetEnvironmentVariable("UPDATE_LOGIC_SCHEMA") == "1") File.WriteAllText(path, want);
            Assert.True(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == want,
                "agent-kit/logic-schema.json is out of date: UPDATE_LOGIC_SCHEMA=1 dotnet run --project tests/PalaceMod.Tests -- LogicSchemaFile");
        }

        public static void DefsKeepUnknownFields()
        {
            string json = @"{""format"":""palace-builder"",""version"":3,""id"":""a.b"",""future"":1,""assets"":{},""waveSets"":[],
                ""units"":[{""id"":""u"",""side"":""enemy"",""base"":""vanilla:E Melee"",""rules"":[{""when"":{""on"":""death"",""x"":1},""do"":[{""do"":""spawn"",""entity"":""u"",""y"":2}],""studio"":{""collapsed"":true}}],
                            ""movement"":{""mode"":""direct"",""zz"":1},""targeting"":[""foe"",{""filter"":""king"",""w"":3}]}],
                ""entities"":[{""id"":""e"",""kind"":""prop"",""q"":1,""body"":{""radius"":1,""r"":2},""area"":{""shape"":{""circle"":{""radius"":""=night"",""c"":1}}}}],
                ""statuses"":[{""id"":""s"",""mods"":{""moveSpeed"":{""mult"":0.5,""m"":1}},""s2"":1}],
                ""presets"":[{""id"":""p"",""params"":{""a"":{""type"":""number"",""pp"":1}},""movement"":{""mode"":""hold""}}],
                ""levels"":[{""id"":""l"",""host"":""Nordfels"",""logic"":{""lx"":1,""places"":[{""id"":""a"",""kind"":""logic"",""pos"":[0.5,1.5,2.5],""px"":1}],
                            ""attach"":{""castle"":{""name"":""c"",""ax"":1}},""hud"":[{""id"":""h"",""text"":""t"",""hx"":1}],""kingActions"":[{""id"":""k"",""kx"":1}],""caps"":{""entities"":10,""cx"":1}}}]}";
            var pack = PackSerializer.Read(json, null);
            var back = JObject.Parse(PackSerializer.WriteCompact(pack));
            var want = JObject.Parse(json);
            Assert.True(JToken.DeepEquals(want, back), "round trip:\n" + want + "\n---\n" + back);
        }

        // ---- Migration and lowering --------------------------------------------------------------------------

        public static void MigratesSpawnOnDeathToARule()
        {
            string v2 = @"{""format"":""palace-builder"",""version"":2,""id"":""a.b"",""units"":[
                {""id"":""boss"",""base"":""vanilla:E Ogre"",""behaviours"":[{""type"":""SpawnOnDeath"",""unit"":""minion""}]},
                {""id"":""minion"",""base"":""vanilla:E Racer""}]}";
            var issues = new List<ValidationIssue>();
            var pack = PackSerializer.Read(v2, issues);
            Assert.NotNull(pack, "reads");
            Assert.Equal(3, pack.Version, "migrated version");
            var boss = pack.FindUnit("boss");
            Assert.Equal(1, boss.Rules.Count, "one rule");
            Assert.Equal("minion", LogicLowering.LoweredSpawnOnDeath(boss, LogicLowering.IsUnitIn(pack)), "lowered spawn");
            Assert.True(boss.Extra == null || !boss.Extra.ContainsKey("behaviours"), "behaviours dropped");
            string json = PackSerializer.WriteCompact(pack);
            Assert.True(json.Contains("\"rules\":[{\"when\":\"death\",\"do\":[{\"do\":\"spawn\",\"entity\":\"minion\",\"at\":\"self\",\"elite\":\"inherit\"}]}]"), "rule JSON: " + json);
            Assert.Equal("", Errors(Validator.Validate(pack)), "migrated pack validates");
        }

        public static void TheSamplePackMigratesAndValidates()
        {
            using var s = typeof(LogicTests).Assembly.GetManifestResourceStream("builder-sample.json");
            using var r = new StreamReader(s);
            var issues = new List<ValidationIssue>();
            var pack = PackSerializer.Read(r.ReadToEnd(), issues);
            Assert.Equal("", Errors(issues), "reads");
            var shaman = pack.Units.First(u => u.Rules != null);
            Assert.Equal("goblin-runner", LogicLowering.LoweredSpawnOnDeath(shaman, LogicLowering.IsUnitIn(pack)), "the sample's spawn-on-death lowers");
            Assert.Equal("", Errors(Validator.Validate(pack)), "sample validates");
        }

        public static void LoweringIsExact()
        {
            JObject Rule(string json) => JObject.Parse(json);
            Assert.Equal("x", LogicLowering.SpawnOnDeathUnit(Rule(@"{""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""x"",""at"":""self"",""elite"":""inherit""}]}")), "exact");
            Assert.Equal("x", LogicLowering.SpawnOnDeathUnit(Rule(@"{""id"":""r"",""when"":{""on"":""death""},""do"":[{""do"":""spawn"",""entity"":""x"",""at"":""self"",""elite"":""inherit""}]}")), "object when + id");
            Assert.Null(LogicLowering.SpawnOnDeathUnit(Rule(@"{""when"":""death"",""if"":""chance(0.5)"",""do"":[{""do"":""spawn"",""entity"":""x"",""at"":""self"",""elite"":""inherit""}]}")), "a condition");
            Assert.Null(LogicLowering.SpawnOnDeathUnit(Rule(@"{""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""x"",""count"":2,""at"":""self"",""elite"":""inherit""}]}")), "two");
            Assert.Null(LogicLowering.SpawnOnDeathUnit(Rule(@"{""when"":{""on"":""death"",""of"":""foe""},""do"":[{""do"":""spawn"",""entity"":""x"",""at"":""self"",""elite"":""inherit""}]}")), "of");
            Assert.Null(LogicLowering.SpawnOnDeathUnit(Rule(@"{""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""=self.template"",""at"":""self"",""elite"":""inherit""}]}")), "expression");
            var rules = JArray.Parse(@"[{""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""me"",""at"":""self"",""elite"":""inherit""}]},
                                       {""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""prop1"",""at"":""self"",""elite"":""inherit""}]},
                                       {""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""b"",""at"":""self"",""elite"":""inherit""}]}]");
            Assert.Equal(2, LogicLowering.LoweredRuleIndex(rules, "me", r => r == "b"), "skips itself and non-units");
            var u = new UnitDef { Id = "u" };
            LogicLowering.SetSpawnOnDeath(u, "b");
            Assert.Equal("b", LogicLowering.LoweredSpawnOnDeath(u), "set");
            LogicLowering.SetSpawnOnDeath(u, null);
            Assert.Null(u.Rules, "removed");
        }

        public static void TemplateRefsFollowRules()
        {
            var u = new UnitDef { Id = "boss", Rules = [JObject.Parse(@"{""when"":""death"",""do"":[{""do"":""if"",""cond"":""true"",""then"":[{""do"":""spawn"",""entity"":""imp""}]},
                {""do"":""choose"",""options"":[{""weight"":1,""do"":[{""do"":""replace"",""with"":""ghost""}]}]},{""do"":""spawn"",""entity"":""=self.template""}]}").ToObject<RuleDef>()] };
            Assert.Equal("imp,ghost", string.Join(",", LogicRefs.TemplateRefs(u)), "refs");
            LogicRefs.Rewrite(u, r => r == "imp" ? "imp-2" : null);
            Assert.Equal("imp-2,ghost", string.Join(",", LogicRefs.TemplateRefs(u)), "rewritten");
        }

        public static void LevelEffectsFlagSharedEnemies()
        {
            RuleDef R(string j) => JObject.Parse(j).ToObject<RuleDef>();
            Assert.Equal("", string.Join(",", LevelEffects.Of([R(@"{""when"":""death"",""do"":[{""do"":""spawn"",""entity"":""x""}]}")])), "a plain ability");
            Assert.Equal("gold,hears death of enemy anywhere,level variable souls", string.Join(",", LevelEffects.Of([
                R(@"{""when"":""death"",""do"":[{""do"":""gold"",""amount"":5}]}"),
                R(@"{""when"":{""on"":""death"",""of"":""enemy""},""do"":[{""do"":""if"",""cond"":""true"",""then"":[{""do"":""addVar"",""name"":""$souls""}]}]}")])), "level effects");
        }

        // ---- Presets ------------------------------------------------------------------------------------------

        public static void PresetExpansionSubstitutesTypedValues()
        {
            var pack = new DesignPack
            {
                Id = "a.b",
                Units = [new UnitDef { Id = "slime", Base = "vanilla:E Slime", Rules = [JObject.Parse(@"{""preset"":""split-on-death"",""with"":{""count"":3}}").ToObject<RuleDef>()] }],
            };
            var issues = new List<ValidationIssue>();
            var x = Presets.Expand(pack, issues);
            Assert.Equal("", Errors(issues), "expands");
            var rule = x.Units[0].Rules[0];
            Assert.Equal("self.$gen < 3", (string)rule.If, "depth default substituted");
            var spawn = rule.Do[0];
            Assert.Equal(JTokenType.Integer, spawn.Get("count").Type, "a whole-string {count} keeps its number type");
            Assert.Equal(3, (int)spawn.Get("count"), "count");
            Assert.Equal("=self.template", spawn.GetString("entity"), "template default");
            Assert.True(x.Units[0].Vars.ContainsKey("gen"), "preset vars merged");
            Assert.Equal("units[0].rules[0] (preset split-on-death).rules[0]", (string)rule.Extra[Presets.SourceField], "_src");
            Assert.Equal("", Errors(Validator.Validate(pack)), "validates");
        }

        public static void PresetErrorsPointAtTheUse()
        {
            var pack = new DesignPack
            {
                Id = "a.b",
                Units = [new UnitDef { Id = "slime", Base = "vanilla:E Slime", Rules = [
                    JObject.Parse(@"{""preset"":""split-on-deth""}").ToObject<RuleDef>(),
                    JObject.Parse(@"{""preset"":""split-on-death"",""with"":{""count"":""two"",""cuont"":1}}").ToObject<RuleDef>()] }],
            };
            var issues = Validator.Validate(pack);
            string all = string.Join("\n", issues);
            Assert.True(all.Contains("units[0].rules[0]: preset \"split-on-deth\" not found (did you mean \"split-on-death\"?)"), all);
            Assert.True(all.Contains("units[0].rules[1].with.count: \"count\" expects a number"), all);
            Assert.True(all.Contains("units[0].rules[1].with.cuont: preset \"split-on-death\" has no param \"cuont\" (did you mean \"count\"?)"), all);
        }

        public static void BuiltInPresetsAllValidate()
        {
            Assert.Null(Presets.BuiltInError, "logic-presets.json loads");
            Assert.True(Presets.BuiltIn.Count >= 15, "built-ins: " + Presets.BuiltIn.Count);
            foreach (var preset in Presets.BuiltIn)
            {
                var with = new JObject();
                if (preset.Params?.ContainsKey("status") == true) with["status"] = "warded";
                var pack = new DesignPack
                {
                    Id = "a.b",
                    Statuses = [new StatusDef { Id = "warded", Duration = 1 }],
                    Levels = [new LevelDef { Id = "l", Host = "Nordfels", Logic = new LogicDef { Vars = new() { ["count"] = 0 } } }],
                };
                if (preset.Kind != null)
                {
                    pack.Levels[0].Logic.Places = [new PlaceDef { Id = "p", Preset = preset.Id, With = with, Pos = [0, 0, 0] },
                                                   new PlaceDef { Id = "path", Kind = "path", Points = [[0, 0], [5, 5]] }];
                }
                else pack.Units = [new UnitDef { Id = "u", Base = "vanilla:E Slime", Rules = [new RuleDef { Preset = preset.Id, With = with }] }];
                var issues = Validator.Validate(pack);
                Assert.Equal("", Errors(issues), $"built-in preset {preset.Id}");
                Assert.True(preset.Name != null && preset.Description != null && preset.Category != null, preset.Id + " has name, description, category");
                foreach (var kv in preset.Params ?? []) Assert.True(kv.Value.Description != null, $"{preset.Id}.{kv.Key} has a description");
            }
        }

        public static void PlaceRulesKeepTheirPathsBesideAPreset()
        {
            var issues = ValidateLevel(@"{ ""places"": [{ ""id"": ""t"", ""preset"": ""timer"", ""pos"": [0,0,0], ""rules"": [{ ""when"": ""dusk"", ""do"": [{ ""do"": ""nope"" }] }] }] }");
            Has(issues, "levels[0].logic.places[0].rules[0].do[0].do: unknown action \"nope\"");
        }

        public static void MalformedListsAreReportedNotThrown()
        {
            var issues = ValidateLevel(@"{ ""rules"": [{ ""when"": ""dusk"", ""do"": [{ ""do"": ""if"", ""cond"": ""true"", ""then"": [""oops""] },
                { ""do"": ""setTargeting"", ""target"": ""all('enemy')"", ""targeting"": [3] }] }] }");
            Has(issues, "levels[0].logic.rules[0].do[0].then[0]: is empty");
            Has(issues, "levels[0].logic.rules[0].do[1].targeting[0]: needs a filter");
        }

        public static void PlayPackKeepsUnitsTheLogicNames()
        {
            var fx = JObject.Parse(File.ReadAllText(Directory.GetFiles(RepoFile("tests/logic-examples"), "09-*.json")[0]));
            var pack = PackSerializer.FromJObject((JObject)fx["pack"], null);
            pack.Units.Add(new UnitDef { Id = "unrelated", Base = "vanilla:E Melee" });
            var play = PalaceMod.Builder.Studio.Play.TestPacks.Play(pack, pack.Levels[0].Id, out string err);
            Assert.Null(err, "play pack");
            Assert.Equal("bat", string.Join(",", play.Units.Select(u => u.Id)), "is:bat in the level script keeps the bat");
            Assert.Equal("", Errors(Validator.Validate(play)), "the play pack validates");
        }

        // ---- Validator messages ------------------------------------------------------------------------------

        private static List<ValidationIssue> ValidateLevel(string logicJson, string extraPack = null)
        {
            var root = JObject.Parse(@"{""format"":""palace-builder"",""version"":3,""id"":""a.b"",""levels"":[{""id"":""l"",""host"":""Nordfels""}]}");
            root["levels"][0]["logic"] = JObject.Parse(logicJson);
            if (extraPack != null) foreach (var p in JObject.Parse(extraPack).Properties()) root[p.Name] = p.Value;
            var issues = new List<ValidationIssue>();
            var pack = PackSerializer.FromJObject(root, issues);
            Assert.NotNull(pack, "parses: " + string.Join("; ", issues));
            return Validator.Validate(pack);
        }

        private static void Has(List<ValidationIssue> issues, string text)
        {
            string all = string.Join("\n    ", issues);
            Assert.True(issues.Any(i => i.ToString().Contains(text)), $"expected \"{text}\" in\n    {all}");
        }

        public static void ValidatorMessagesHavePathsAndColumns()
        {
            var issues = ValidateLevel(@"{ ""vars"": { ""souls"": 0 }, ""places"": [{ ""id"": ""a"", ""kind"": ""logic"", ""pos"": [0,0,0] }, { ""id"": ""b"", ""kind"": ""logic"", ""pos"": [0,0,0] },
                { ""id"": ""c"", ""kind"": ""logic"", ""pos"": [0,0,0] }, { ""id"": ""d"", ""kind"": ""logic"", ""pos"": [0,0,0], ""rules"": [{ ""when"": ""dusk"" },
                { ""when"": ""dusk"", ""if"": ""night > 1 and $soul > 2"", ""do"": [] }] }],
                ""rules"": [{ ""when"": ""levelStart"", ""do"": [{ ""do"": ""damage"", ""target"": ""other"", ""amount"": 1 }] },
                            { ""when"": ""death"", ""do"": [] },
                            { ""when"": ""dusk"", ""do"": [{ ""do"": ""pull"" }, { ""do"": ""spawn"", ""entity"": ""gobln"" }, { ""do"": ""setVar"", ""name"": ""sols"", ""value"": 1 }] }],
                ""attach"": { ""add:nope"": {}, ""tower"": {} } }", @"{ ""units"": [{ ""id"": ""goblin"", ""base"": ""vanilla:E Melee"" }] }");
            Has(issues, "levels[0].logic.places[3].rules[1].if: col 15: unknown variable \"$soul\" (did you mean \"$souls\"?)");
            Has(issues, "levels[0].logic.rules[0].do[0].target: col 1: \"other\" is not defined for the event \"levelStart\"");
            Has(issues, "levels[0].logic.rules[1].when: the level script hears \"death\" only from entities: give \"of\"");
            Has(issues, "levels[0].logic.rules[2].do[0].do: unknown action \"pull\"");
            Has(issues, "made with a newer PalaceMod?");
            Has(issues, "levels[0].logic.rules[2].do[1].entity: no template \"gobln\" in this pack (did you mean \"goblin\"?)");
            Has(issues, "levels[0].logic.rules[2].do[2].name: unknown variable \"$sols\" (did you mean \"souls\"?)");
            Has(issues, "levels[0].logic.attach[\"add:nope\"]: no added slot \"nope\"");
            Has(issues, "levels[0].logic.attach[\"tower\"]: attach keys are");
        }

        public static void ValidatorChecksGameRules()
        {
            var issues = ValidateLevel(@"{ ""vars"": { ""flipped"": false }, ""game"": { ""victory"": ""survive"", ""castle"": ""hidden"", ""holdNight"": ""count('#camp') > 0"",
                ""endNight"": ""$flipd"", ""combat"": ""isNight or $flipped"", ""build"": 3, ""dayLength"": -5, ""survivors"": ""=night > 2 ? 'stay' : 'remove'"", ""speed"": 2 } }");
            Has(issues, "levels[0].logic.game.victory: must be one of lastNight, rules");
            Has(issues, "levels[0].logic.game.endNight: col 1: unknown variable \"$flipd\"");
            Has(issues, "levels[0].logic.game.build: must be true, false or a condition");
            Has(issues, "levels[0].logic.game.dayLength: must be 0 or more");
            foreach (var ok in new[] { "castle", "holdNight", "combat", "survivors" })
                Assert.True(!issues.Any(i => i.ToString().Contains("logic.game." + ok + ":")), $"game.{ok} flagged: {string.Join("; ", issues)}");
        }

        public static void ValidatorChecksComponentsAndCaps()
        {
            var issues = ValidateLevel(@"{ ""caps"": { ""entities"": 900, ""units"": 5000 },
                ""kingActions"": [{ ""id"": ""a"" }, { ""id"": ""b"" }, { ""id"": ""c"" }, { ""id"": ""d"" }] }",
                @"{ ""entities"": [{ ""id"": ""tm"", ""kind"": ""projectile"", ""projectile"": { ""homing"": 1.4, ""face"": ""sideways"", ""hits"": ""foe and Towr"" } },
                                   { ""id"": ""zone"", ""kind"": ""area"" }],
                    ""statuses"": [{ ""id"": ""s"", ""mods"": { ""moveSped"": { ""mult"": 0.5 }, ""moveSpeed"": { ""mult"": 1, ""add"": 1 } }, ""flags"": [""stuned""] }] }");
            Has(issues, "warning levels[0].logic.caps.entities: 900 is above the default 300: may lag");
            Has(issues, "error levels[0].logic.caps.units: 5000 is above the hard limit 1000");
            Has(issues, "levels[0].logic.kingActions: at most 3 king actions");
            Has(issues, "entities[0].projectile.homing: 1.4 is outside 0..1");
            Has(issues, "entities[0].projectile.face: must be one of travel, fixed, spin");
            Has(issues, "entities[0].projectile.hits: col 9: unknown filter term \"Towr\" (did you mean \"Tower\"?)");
            Has(issues, "entities[1].area: an area template needs an area block");
            Has(issues, "statuses[0].mods.moveSped: unknown stat \"moveSped\" (did you mean \"moveSpeed\"?)");
            Has(issues, "statuses[0].mods.moveSpeed: needs exactly one of mult, add or value");
            Has(issues, "statuses[0].flags[0]: unknown flag \"stuned\" (did you mean \"stunned\"?)");
        }

        public static void PlaceTakesItsTemplatesKind()
        {
            var issues = ValidateLevel(@"{ ""places"": [{ ""id"": ""g"", ""entity"": ""gem"", ""pos"": [0,0,0], ""rules"": [{ ""when"": ""pickup"", ""do"": [] }] }] }",
                @"{ ""entities"": [{ ""id"": ""gem"", ""kind"": ""pickup"", ""pickup"": { ""by"": ""king"" } }] }");
            Assert.True(!issues.Any(i => i.Message.Contains("fires on")), string.Join("\n", issues));
        }

        public static void UnknownFieldsAndBodyTags()
        {
            var issues = ValidateLevel(@"{ ""bogus"": 1, ""places"": [{ ""id"": ""t"", ""kind"": ""prop"", ""pos"": [0,0,0], ""body"": { ""hitable"": true, ""hittable"": true, ""tags"": [""Biulding""] } }] }");
            Has(issues, "warning levels[0].logic.bogus: unknown field \"bogus\"");
            Has(issues, "warning levels[0].logic.places[0].body.hitable: unknown field \"hitable\" (did you mean \"hittable\"?)");
            Has(issues, "error levels[0].logic.places[0].body.tags[0]: unknown game tag \"Biulding\" (did you mean \"Building\"?)");
        }

        public static void ValidatorWarnsOnSignalLoops()
        {
            var issues = ValidateLevel(@"{ ""places"": [
                { ""id"": ""a"", ""kind"": ""logic"", ""pos"": [0,0,0], ""rules"": [{ ""when"": { ""on"": ""signal"", ""name"": ""ping"" }, ""do"": [{ ""do"": ""signal"", ""name"": ""pong"", ""to"": ""all"" }] }] },
                { ""id"": ""b"", ""kind"": ""logic"", ""pos"": [0,0,0], ""rules"": [{ ""when"": { ""on"": ""signal"", ""name"": ""pong"" }, ""do"": [{ ""do"": ""signal"", ""name"": ""ping"", ""to"": ""all"" }] }] },
                { ""id"": ""c"", ""kind"": ""logic"", ""pos"": [0,0,0], ""rules"": [{ ""when"": { ""on"": ""signal"", ""name"": ""tick"" }, ""do"": [{ ""do"": ""wait"", ""seconds"": 1 }, { ""do"": ""signal"", ""name"": ""tick"", ""to"": ""all"" }] }] }] }");
            Has(issues, "the signal \"ping\" leads back to itself with no wait");
            Assert.True(!issues.Any(i => i.Message.Contains("\"tick\" leads back")), "a wait breaks the loop");
        }

        // ---- The doc's examples --------------------------------------------------------------------------------

        public static void EveryDocExampleValidates()
        {
            var files = Directory.GetFiles(RepoFile("tests/logic-examples"), "*.json").OrderBy(f => f).ToList();
            Assert.True(files.Count >= 23, "fixtures: " + files.Count);
            var failures = new List<string>();
            foreach (var file in files)
            {
                var fx = JObject.Parse(File.ReadAllText(file));
                var issues = new List<ValidationIssue>();
                var pack = PackSerializer.FromJObject((JObject)fx["pack"], issues);
                if (pack == null) { failures.Add($"{Path.GetFileName(file)}: {string.Join("; ", issues)}"); continue; }
                issues.AddRange(Validator.Validate(pack));
                string errs = Errors(issues);
                if (errs.Length > 0) failures.Add($"{Path.GetFileName(file)}:\n    {errs}");
                // The play script's expressions are checked in the level script's scope too.
                var level = pack.Levels[0];
                var names = new HashSet<string>((level.Logic?.Places ?? []).Select(p => p.Id).Concat((level.Logic?.Attach ?? []).Values.Select(a => a.Name)).Where(n => n != null));
                var scope = new CheckScope
                {
                    Event = LogicSchema.Events["levelStart"], LevelScript = true,
                    LevelVars = new HashSet<string>(level.Logic?.Vars?.Keys ?? Enumerable.Empty<string>()), Names = names,
                };
                foreach (JObject step in fx["play"]?["steps"] ?? new JArray())
                {
                    if (step["expect"] is JValue ex)
                    {
                        var list = new List<ExprIssue>();
                        var t = ExprChecker.Check((string)ex, scope, list, out _);
                        if (list.Count > 0) failures.Add($"{Path.GetFileName(file)} play expect \"{ex}\": {list[0]}");
                        else if (!ExprChecker.Accepts(LType.Bool, t)) failures.Add($"{Path.GetFileName(file)} play expect \"{ex}\" is a {t}");
                    }
                }
            }
            Assert.True(failures.Count == 0, string.Join("\n  ", failures));
        }
    }
}
