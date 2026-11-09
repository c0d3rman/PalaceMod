using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Logic;

namespace PalaceMod.Tests
{
    /// <summary>The Studio behaviour editor's pure parts: where rules live in the JSON, rules as sentences, the live
    /// expression check and wires.</summary>
    public static class LogicEditorTests
    {
        public static void CatalogCoversTheDocsEventsAndActions()
        {
            string[] events = ["spawn", "death", "damaged", "healed", "hpBelow", "attack", "hit", "kill", "every", "after", "near", "enter", "exit", "inside", "empty", "occupied",
                "impact", "expire", "arrive", "end", "pickup", "interact", "signal", "varChanged", "statusStart", "statusEnd", "statusTick", "targetChanged", "built", "upgraded",
                "repaired", "coinDrop", "coinPickup", "lineSpawn", "levelStart", "dusk", "nightStart", "dawn", "day", "waveCleared", "goldChanged", "kingAction", "levelEnd"];
            foreach (var e in events) Assert.True(LogicCatalog.Event(e) != null, "event " + e);
            string[] actions = ["spawn", "launch", "spawnGroup", "replace", "damage", "heal", "kill", "revive", "setHp", "setStat", "applyStatus", "removeStatus", "convert",
                "teleport", "push", "drift", "moveTo", "setMovement", "setTargeting", "taunt", "command", "signal", "setVar", "addVar", "listAdd", "listRemove", "listClear",
                "addLabel", "removeLabel", "enableRule", "disableRule", "enable", "disable", "toggle", "wait", "if", "repeat", "forEach", "choose", "stop", "gold", "dropCoins",
                "building", "waves", "endLevel", "effect", "sound", "message", "floatText", "setVisual", "hud", "shake", "marker", "destroy"];
            foreach (var a in actions) Assert.True(LogicCatalog.Action(a) != null, "action " + a);
            Assert.True(LogicCatalog.Action("damage").Params[0].Name == "target", "targeted actions start with target");
        }

        public static void LevelLogicRoundTripsThroughExtra()
        {
            var lv = new LevelDef { Id = "l" };
            LogicDoc.EditLogic(lv, logic =>
            {
                var node = LogicDoc.Node(logic, OwnerKind.Attach, "slot:Tower 3", true);
                LogicDoc.EnsureRules(node).Add(LogicDoc.NewRule("death"));
            });
            var back = LogicDoc.Logic(lv);
            Assert.Equal("death", LogicDoc.EventOf((JObject)back["attach"]["slot:Tower 3"]["rules"][0]), "rule stored under attach");
            // Emptying it prunes the attach entry and the logic block.
            LogicDoc.EditLogic(lv, logic => ((JArray)logic["attach"]["slot:Tower 3"]["rules"]).Clear());
            Assert.True(lv.Extra == null || !lv.Extra.ContainsKey("logic"), "empty logic is dropped");
        }

        public static void NewRuleUsesShorthandOrRequiredParams()
        {
            Assert.Equal("death", (string)LogicDoc.NewRule("death")["when"], "shorthand");
            var every = LogicDoc.NewRule("every");
            Assert.Equal(1L, (long)every["when"]["seconds"], "every gets seconds");
            var spawn = LogicDoc.NewAction("spawn");
            Assert.True(spawn["entity"] == null, "a template has no default");
            Assert.Equal(10L, (long)LogicDoc.NewAction("heal")["amount"], "heal amount default");
        }

        public static void ActionListPaths()
        {
            var rule = JObject.Parse("{ 'when': 'death', 'do': [ { 'do': 'if', 'cond': 'x', 'then': [ { 'do': 'wait' } ] }, { 'do': 'choose', 'options': [ { 'do': [ { 'do': 'stop' } ] } ] } ] }");
            Assert.Equal(2, LogicDoc.ActionList(rule, [], false).Count, "top");
            Assert.Equal("wait", (string)LogicDoc.ActionList(rule, [(0, "then")], false)[0]["do"], "nested then");
            Assert.Equal("stop", (string)LogicDoc.ActionList(rule, [(1, "options/0")], false)[0]["do"], "choose option");
            Assert.True(LogicDoc.ActionList(rule, [(0, "else")], false) == null, "missing list without create");
            Assert.True(LogicDoc.ActionList(rule, [(0, "else")], true) != null, "created");
        }

        public static void RuleSentences()
        {
            var split = JObject.Parse("{ 'when': 'death', 'if': 'self.$gen < 3', 'do': [ { 'do': 'spawn', 'entity': '=self.template', 'count': 2, 'at': 'self' } ] }");
            Assert.Equal("When this dies if self.$gen < 3 → Spawn ƒ self.template × 2 at this", RuleText.Summary(split), "split rule");
            var necro = JObject.Parse("{ 'when': { 'on': 'death', 'of': 'foe and unit', 'range': 12 }, 'do': [ { 'do': 'spawn', 'entity': 'skeleton', 'at': 'subject' } ] }");
            Assert.Equal("When a foe unit within 12 m dies → Spawn skeleton × 1 at the one it happened to", RuleText.Summary(necro), "of rule");
            var stat = JObject.Parse("{ 'do': 'setStat', 'target': \"all('player and Tower')\", 'stat': 'attackCooldown', 'mult': 0.5, 'for': 5 }");
            Assert.Equal("Attack cooldown of every player Tower × 0.5 for 5 s", RuleText.Action(stat), "setStat");
            var timer = JObject.Parse("{ 'when': { 'on': 'every', 'seconds': 8, 'during': 'night' }, 'do': [] }");
            Assert.Equal("When every 8 s at night → (nothing yet)", RuleText.Summary(timer), "every");
            Assert.Equal("Damage nearest foe Tower within 20 m by 5", RuleText.Action(JObject.Parse("{ 'do': 'damage', 'target': \"nearest('foe and Tower', 20)\", 'amount': 5 }")), "selector words");
            Assert.True(RuleText.Action(JObject.Parse("{ 'do': 'spawn' }")).EndsWith("(!)"), "missing required param flagged");
        }

        public static void ExpressionChecks()
        {
            var scope = new ExprScope { Event = LogicCatalog.Event("levelStart"), IsLevelScript = true, LevelVars = new HashSet<string> { "souls" } };
            Assert.Equal(0, ExprLint.Check("$souls >= 10 and isNight", scope).Count, "fine");
            var typo = ExprLint.Check("$soul >= 10", scope);
            Assert.True(typo.Count == 1 && typo[0].Message.Contains("did you mean \"$souls\"") && typo[0].Col == 1, "var typo: " + string.Join(";", typo));
            var other = ExprLint.Check("other.hp > 3", scope);
            Assert.True(other.Count == 1 && other[0].Message.Contains("not defined for the event \"levelStart\""), "role: " + string.Join(";", other));
            Assert.True(ExprLint.Check("count('foe', 10", scope)[0].Message.Contains("isn't closed"), "paren");
            Assert.True(ExprLint.Check("night = 3", scope)[0].Message.Contains("=="), "assignment");
            Assert.True(ExprLint.Check("nearst('foe')", scope)[0].Message.Contains("did you mean \"nearest\""), "function typo");
            Assert.Equal(0, ExprLint.Check("nearest('foe and Tower', 20).hpPct < 0.5", scope).Count, "properties pass");
            Assert.Equal(0, ExprLint.CheckFilter("foe and Building and not #lit").Count, "filter ok");
            Assert.True(ExprLint.CheckFilter("foe and and unit").Count == 1, "filter double and");
            Assert.True(ExprLint.CheckFilter("foe untis")[0].Message.Contains("and"), "filter missing join");
            Assert.True(ExprLint.CheckFilter("foe and unti")[0].Message.Contains("did you mean \"unit\""), "filter word typo");
        }

        public static void WiresAreRules()
        {
            var logic = new JObject();
            var lever = new JObject { ["id"] = "lever", ["kind"] = "logic", ["rules"] = JArray.Parse("[ { 'when': 'interact', 'do': [] } ]") };
            var bridge = new JObject { ["id"] = "bridge", ["entity"] = "drawbridge" };
            logic["places"] = new JArray(lever, bridge);
            Assert.Equal(-1, Wires.Add(lever, bridge, "bridge", "interact", null, "open"), "built-in input: no listener");
            Assert.Equal(1, ((JArray)lever["rules"]).Count, "joined the lever's empty output rule");
            Assert.Equal("{\"do\":\"signal\",\"name\":\"open\",\"to\":\"@bridge\"}", lever["rules"][0]["do"][0].ToString(Newtonsoft.Json.Formatting.None), "the doc's form");
            int listener = Wires.Add(lever, bridge, "bridge", "interact", null, "raise");
            Assert.Equal(0, listener, "listener added on the target");
            Assert.Equal("raise", (string)bridge["rules"][0]["when"]["name"], "listener name");
            var wires = Wires.Scan(logic);
            Assert.Equal(2, wires.Count, "two wires");
            Assert.True(wires.All(w => w.ToKind == OwnerKind.Place && w.ToId == "bridge" && w.Event == "interact"), "resolved");
            Wires.Remove(logic, wires.First(w => w.Signal == "raise"));
            Assert.Equal(1, Wires.Scan(logic).Count, "one left");
            Assert.True(bridge["rules"] == null || ((JArray)bridge["rules"]).Count == 0, "empty listener removed");
            Wires.Remove(logic, Wires.Scan(logic)[0]);
            Assert.Equal(1, ((JArray)lever["rules"]).Count, "a logic object keeps its output rule");
        }

        public static void LevelEffectsBadge()
        {
            var rules = JArray.Parse("[ { 'when': 'death', 'do': [ { 'do': 'spawn', 'entity': 'x' }, { 'do': 'if', 'cond': 'true', 'then': [ { 'do': 'gold', 'amount': 5 } ] } ] }, { 'when': 'hit', 'do': [ { 'do': 'damage', 'amount': 3 } ] } ]");
            var effects = LogicDoc.LevelEffects(rules);
            Assert.True(effects.Contains("Give gold") && effects.Contains("Spawn"), "nested gold and spawn: " + string.Join(",", effects));
            Assert.Equal(0, LogicDoc.LevelEffects(JArray.Parse("[ { 'when': 'hit', 'do': [ { 'do': 'heal', 'amount': 3 } ] } ]")).Count, "local only");
        }

        public static void UnitFieldsLiveInExtra()
        {
            var u = new UnitDef { Id = "slime" };
            LogicDoc.EditUnit(u, n => { n["rules"] = new JArray(LogicDoc.NewRule("death")); n["vars"] = new JObject { ["gen"] = 0 }; });
            Assert.True(u.Extra.ContainsKey("rules") && u.Extra.ContainsKey("vars"), "written");
            Assert.Equal(2, LogicDoc.UnitNode(u).Properties().Count(), "read back");
            LogicDoc.EditUnit(u, n => n.Remove("vars"));
            Assert.True(!u.Extra.ContainsKey("vars"), "removed");
        }

        public static void NamesAreUnique()
        {
            var taken = new HashSet<string> { "tower3" };
            Assert.Equal("tower32", LogicDoc.NameFor("Tower 3", taken), "unique");
            Assert.Equal("northGate", LogicDoc.NameFor("North gate", taken), "camel");
            Assert.Equal("n12", LogicDoc.NameFor("12", taken), "leading digit");
        }
    }
}
