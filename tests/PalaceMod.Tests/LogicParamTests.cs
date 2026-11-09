using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Tests
{
    /// <summary>Action params read the same way by `check` and the game (ParamForms), colours, the `building` slot as
    /// an expression, selector opt-ins (dead, disabled, unbuilt), `outer` and forEach's `as`.</summary>
    public static class LogicParamTests
    {
        private sealed class Names : IValidationContext, ILogicNamesContext
        {
            public IReadOnlyCollection<string> Effects { get; } = ["smoke", "explosion", "Enemy Death FX"];
            public IReadOnlyCollection<string> Sounds { get; } = ["coinSpend"];
            public bool HasVanillaUnit(string id, string side) => true;
            public bool HasVanillaWeapon(string id) => true;
            public bool HasHost(string scene) => true;
            public IReadOnlyCollection<string> SpawnLines(string scene) => null;
            public bool HasPackUnit(string packId, string unitId) => false;
        }

        private static List<ValidationIssue> Validate(string rulesJson, IValidationContext game = null)
        {
            var root = JObject.Parse(@"{""format"":""palace-builder"",""version"":3,""id"":""a.b"",""levels"":[{""id"":""l"",""host"":""Nordfels""}]}");
            root["levels"][0]["logic"] = JObject.Parse(@"{ ""vars"": { ""n"": 0, ""seen"": [] }, ""rules"": [{ ""when"": ""dusk"", ""do"": " + rulesJson + " }] }");
            var issues = new List<ValidationIssue>();
            var pack = PackSerializer.FromJObject(root, issues);
            Assert.NotNull(pack, "parses: " + string.Join("; ", issues));
            return Validator.Validate(pack, game);
        }

        private static string Errors(List<ValidationIssue> issues) => string.Join("\n    ", issues.Where(i => i.Severity == Severity.Error));

        public static void ClassifyIsTheRuntimeRule()
        {
            ParamForm F(string type, string name, JToken v, out string src) => ParamForms.Classify(type, name, v, out src);
            Assert.Equal(ParamForm.Literal, F("color", "glow", "#40c0ff", out var s), "a colour is a literal");
            Assert.Equal("#40c0ff", s, "as written");
            Assert.Equal(ParamForm.Expression, F("color", "tint", "=$c", out s), "=expr on a colour");
            Assert.Equal("$c", s, "without the =");
            Assert.Equal(ParamForm.Expression, F("slot", "slot", "it", out _), "a slot is an expression");
            Assert.Equal(ParamForm.Literal, F("slot", "slot", "add:h1", out _), "a Studio key is a literal");
            Assert.Equal(ParamForm.Expression, F("bool", "visible", "=true", out s), "bool strips =");
            Assert.Equal("true", s, "bool source");
            Assert.Equal(ParamForm.Text, F("text", "text", "Hi {night}", out _), "text interpolates");
            Assert.Equal(ParamForm.Text, F("signal", "name", "open", out _), "a signal name is text");
            Assert.Equal(ParamForm.Literal, F("var", "name", "=x", out _), "a var name is never an expression");
            Assert.Equal(ParamForm.Literal, F("entities", "to", "all", out _), "signal to: all");
            Assert.Equal(ParamForm.Literal, F("for", "for", "night", out _), "for: night");
            Assert.Equal(ParamForm.Json, F("number", "amount", 3, out _), "a JSON number");
            Assert.Equal(ParamForm.Structured, F("actions", "then", new JArray(), out _), "a list");
        }

        public static void Colours()
        {
            foreach (var ok in new[] { "#40c0ff", "40c0ff", "#4cf", "#40c0ff80", "red", "Orange", " grey " })
                Assert.True(ParamForms.TryParseColor(ok, out _, out _, out _, out _), ok);
            foreach (var bad in new[] { "", "#40c0f", "nope", "#ggg", null })
                Assert.True(!ParamForms.TryParseColor(bad, out _, out _, out _, out _), bad ?? "null");
            ParamForms.TryParseColor("#ff8000", out float r, out float g, out float b, out float a);
            Assert.Equal((1f, 128 / 255f, 0f, 1f), (r, g, b, a), "rgb");
        }

        /// <summary>What `check` accepts compiles in the game, and what it rejects fails there too.</summary>
        public static void CheckAndRuntimeAgree()
        {
            var good = new[]
            {
                """{ "do": "setVisual", "glow": "#40c0ff", "tint": "red" }""",
                """{ "do": "floatText", "text": "hi {night}", "color": "#4cf", "at": "vec(0, 0)" }""",
                """{ "do": "setVisual", "tint": "='#40c0ff'", "glow": true }""",
                """{ "do": "building", "slot": "=nearest('unbuilt')", "op": "build" }""",
                """{ "do": "building", "target": "all('unbuilt and type:\"Defense Tower\"')", "op": "build" }""",
            };
            foreach (var json in good)
            {
                Assert.Equal("", Errors(Validate("[" + json + "]")), "check: " + json);
                var a = ActionCompiler.Compile(JObject.Parse(json), "t");
                a.Params.CompileAll();   // throws on a param the game couldn't read
            }
            var bad = new[] { ("""{ "do": "setVisual", "glow": "#40c0fz" }""", "must be a colour") };
            foreach (var (json, msg) in bad)
                Assert.True(Errors(Validate("[" + json + "]")).Contains(msg), $"check rejects {json}: {Errors(Validate("[" + json + "]"))}");
        }

        public static void EffectAndSoundNamesNeedTheGame()
        {
            string rule = """[{ "do": "effect", "fx": "spark" }, { "do": "sound", "sound": "coinSpnd" }, { "do": "effect", "fx": "SMOKE" }]""";
            Assert.Equal("", Errors(Validate(rule)), "offline (no game names): not checked");
            var e = Errors(Validate(rule, new Names()));
            Assert.True(e.Contains("no game effect \"spark\""), e);
            Assert.True(e.Contains("no game sound \"coinSpnd\"") && e.Contains("did you mean \"coinSpend\""), e);
            Assert.True(!e.Contains("SMOKE"), "names match case-insensitively");
        }

        public static void ForEachAsAndOuter()
        {
            Assert.Equal("", Errors(Validate("""[{ "do": "forEach", "in": "all('foe')", "as": "m", "actions": [{ "do": "listAdd", "target": "level", "name": "$seen", "value": "m" }, { "do": "listAdd", "target": "level", "name": "$seen", "value": "outer" }] }]""")), "as + outer");
            Assert.True(Errors(Validate("""[{ "do": "forEach", "in": "all('foe')", "as": "night", "actions": [] }]""")).Contains("already a name"), "as can't shadow a global");
            Assert.True(Errors(Validate("""[{ "do": "listAdd", "target": "level", "name": "$seen", "value": "outer" }]""")).Contains("\"outer\""), "outer needs an it scope");
        }

        // ---- runtime ---------------------------------------------------------------------------------------------

        private static LogicWorld NewWorld(string logicJson)
        {
            var w = new LogicWorld();
            LogicWorld.Current = w;
            LogicServices.Factory = new FakeFactory();
            LogicServices.Game = new FakeGame();
            LogicServices.Spatial = null;
            LogicServices.Stats = null;
            LogicServices.Areas = null;
            LogicServices.UnbuiltSlots = null;
            w.Load(JObject.Parse(logicJson), [], null, null, "levels[0]");
            return w;
        }

        private static LogicEntity Add(LogicWorld w, string id, string kind, string team, Vec pos, string label = null, FakeBody body = null)
        {
            var e = w.Describe(null, JObject.Parse($$"""{ "id": "{{id}}" }"""), id, team, null);
            e.Kind = kind;
            if (label != null) e.Labels.Add(label);
            e.Body = body ?? new FakeBody { Position = pos };
            w.Add(e);
            return e;
        }

        public static void SelectorsOptInToDisabledDeadAndUnbuilt()
        {
            var w = NewWorld("{}");
            var self = Add(w, "self", "logic", "player", new Vec(0, 0, 0));
            var lamp = Add(w, "lamp", "prop", "neutral", new Vec(2, 0, 0), "lamp");
            Add(w, "lamp2", "prop", "neutral", new Vec(3, 0, 0), "lamp");
            lamp.Enabled = false;
            Add(w, "dead", "unit", "enemy", new Vec(4, 0, 0), "foe1", new FakeBody { Position = new Vec(4, 0, 0), Hp = 0 });
            Add(w, "empty", "building", "player", new Vec(5, 0, 0), null, new FakeBody { Position = new Vec(5, 0, 0), Hp = 0, BuildingType = "House", Level = 0 });
            Add(w, "house", "building", "player", new Vec(6, 0, 0), null, new FakeBody { Position = new Vec(6, 0, 0), BuildingType = "House", Level = 1 });
            var ctx = new LogicContext { World = w, Self = self };
            double N(string s) => ExprCompiler.Compile(s, "t")(ctx).Num;
            Assert.Equal(1.0, N("count('#lamp')"), "a switched-off entity is skipped");
            Assert.Equal(2.0, N("count('#lamp and include:disabled')"), "include:disabled widens");
            Assert.Equal(1.0, N("count('#lamp and disabled')"), "disabled finds only those");
            Assert.Equal(0.0, N("count('#foe1')"), "the dead are skipped");
            Assert.Equal(1.0, N("count('dead')"), "dead names the dead (not the switched-off or empty slots)");
            Assert.Equal(1.0, N("count('building')"), "an empty slot is skipped");
            Assert.Equal(2.0, N("count('type:House and include:unbuilt')"), "include:unbuilt widens");
            Assert.Equal("empty", ExprCompiler.Compile("nearest('unbuilt')", "t")(ctx).AsEntity?.Id, "unbuilt finds the empty slot");
            Assert.Equal(1.0, N("count('include:self and self')"), "include:self");
            Assert.True(ExprCompiler.Compile("nearest('#nosuch').dist(self)", "t")(ctx).IsNone, "none.dist(x) is none");
            Assert.True(ExprCompiler.Compile("dist(nearest('#nosuch'), self)", "t")(ctx).IsNone, "dist(none, x) is none");
        }

        public static void ForEachBindsAsAndOuter()
        {
            var w = NewWorld("""{ "vars": { "seen": [] } }""");
            var self = Add(w, "self", "logic", "player", new Vec(0, 0, 0));
            Add(w, "a", "unit", "enemy", new Vec(1, 0, 0), "t");
            Add(w, "b", "unit", "enemy", new Vec(2, 0, 0), "t");
            var list = ActionCompiler.CompileList(JArray.Parse("""
                [{ "do": "forEach", "in": "all('#t')", "as": "m", "actions": [
                    { "do": "listAdd", "target": "level", "name": "$seen", "value": "outer" },
                    { "do": "listAdd", "target": "level", "name": "$seen", "value": "m" },
                    { "do": "listAdd", "target": "level", "name": "$seen", "value": "it" } ] }]
                """), "t");
            w.RunList(list, new LogicContext { World = w, Self = self });
            w.Drain();
            var seen = w.GetVar(w.Level, "seen").AsList.Select(v => v.AsEntity?.Id ?? "none").ToList();
            // Inside listAdd (targeted at the level) `it` is the level, `outer` the forEach member, `m` the member.
            Assert.Equal("a a level b b level", string.Join(" ", seen), "outer and as see the member");
        }
    }
}
