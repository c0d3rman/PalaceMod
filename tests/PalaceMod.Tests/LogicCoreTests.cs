using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;

namespace PalaceMod.Tests
{
    /// <summary>A fake world for the logic core: bodies are plain objects, the factory makes body-ful entities.</summary>
    internal sealed class FakeBody : IEntityBody
    {
        public bool Exists { get; set; } = true;
        public bool Alive => Exists && Hp > 0;
        public Vec Position { get; set; }
        public float Facing { get; set; }
        public float Hp { get; set; } = 100;
        public float MaxHp { get; set; } = 100;
        public HashSet<string> Tags = [];
        public bool HasTag(string etag) => Tags.Contains(etag);
        public string BuildingType { get; set; }
        public int Level { get; set; }
    }

    internal sealed class FakeGame : IGameState
    {
        public int Night { get; set; } = 1;
        public bool IsNight { get; set; }
        public int Nights { get; set; } = 5;
        public double NightTime { get; set; }
        public double NightProgress { get; set; }
        public int Gold { get; set; }
        public int Income { get; set; }
        public int EnemiesAlive { get; set; }
        public double Difficulty { get; set; } = 1;
        public LogicEntity King { get; set; }
        public LogicEntity Castle { get; set; }
        public int WaveCount(int night, string filter) => 0;
        public IReadOnlyList<string> WaveUnits(int night) => [];
        public string StrongestUnit(int night) => null;
        public Vec SpawnLine(string name) => Vec.Zero;
        public Vec PointNear(Vec p, float r, LogicRandom rng) => p + new Vec((float)rng.Range(-r, r), 0, 0);
    }

    internal sealed class FakeFactory : IEntityFactory
    {
        public int Built;
        public bool Build(LogicEntity e, Vec pos, float rotY, out string error)
        {
            error = null;
            Built++;
            e.Body = new FakeBody { Position = pos, Facing = rotY };
            LogicWorld.Current.Add(e);
            return true;
        }
        public void Despawn(LogicEntity e, bool effects, bool drops) { ((FakeBody)e.Body).Exists = false; LogicWorld.Current.Forget(e); }
        public bool HasTemplate(string template) => LogicWorld.Current.Templates.ContainsKey(template);
        public string CapClass(string template) => "unit";
        public void Attach(LogicEntity e) { }
    }

    public static class LogicCoreTests
    {
        private static LogicWorld NewWorld(string logicJson = "{}", string templatesJson = "{}")
        {
            var w = new LogicWorld();
            LogicWorld.Current = w;
            LogicServices.Factory = new FakeFactory();
            LogicServices.Game = new FakeGame();
            LogicServices.Spatial = null;
            LogicServices.Stats = null;
            LogicServices.Areas = null;
            var templates = JObject.Parse(templatesJson).Properties().Select(p => (p.Name, (JObject)p.Value));
            w.Load(JObject.Parse(logicJson), templates, null, null, "levels[0]");
            return w;
        }

        private static LogicEntity Place(LogicWorld w, string json, Vec pos, string template = null)
        {
            var place = JObject.Parse(json);
            var e = w.Describe(template ?? (string)place["entity"], place, (string)place["id"], (string)place["team"], null);
            e.Body = new FakeBody { Position = pos };
            w.Add(e);
            return e;
        }

        private static void Run(LogicWorld w, double seconds, double dt = 1.0 / 60)
        {
            int frames = (int)Math.Round(seconds / dt);
            for (int i = 0; i < frames; i++) w.Tick(dt);
        }

        private static double Num(LogicWorld w, string var) => w.GetVar(w.Level, var).Num;

        public static void EveryTimerFiresOnFixedSteps()
        {
            var w = NewWorld("""{ "vars": { "n": 0 }, "rules": [ { "when": { "on": "every", "seconds": 1 }, "do": [ { "do": "addVar", "name": "$n", "value": 1 } ] } ] }""");
            w.BeginLevel();
            Run(w, 3.5);
            Assert.Equal(3.0, Num(w, "n"), "every 1 s over 3.5 s");
            // Frame rate doesn't change the count.
            var w2 = NewWorld("""{ "vars": { "n": 0 }, "rules": [ { "when": { "on": "every", "seconds": 1 }, "do": [ { "do": "addVar", "name": "$n", "value": 1 } ] } ] }""");
            Run(w2, 3.5, 0.2);
            Assert.Equal(3.0, Num(w2, "n"), "every 1 s at 5 fps");
        }

        public static void AfterFiresOnceAndWaitResumes()
        {
            var w = NewWorld("""{ "vars": { "a": 0, "b": 0 }, "rules": [ { "when": { "on": "after", "seconds": 0.5 }, "do": [ { "do": "setVar", "name": "$a", "value": 1 }, { "do": "wait", "seconds": 1 }, { "do": "setVar", "name": "$b", "value": 1 } ] } ] }""");
            Run(w, 1.0);
            Assert.Equal(1.0, Num(w, "a"), "after fired");
            Assert.Equal(0.0, Num(w, "b"), "still waiting");
            Run(w, 1.0);
            Assert.Equal(1.0, Num(w, "b"), "wait resumed");
            Assert.Equal(0, w.PendingTasks, "no task left");
        }

        public static void DispatchIsInRaiseOrder()
        {
            var w = NewWorld("""
                { "vars": { "log": [] }, "rules": [ { "when": "levelStart", "do": [ { "do": "signal", "name": "a", "to": "all" }, { "do": "signal", "name": "b", "to": "all" } ] },
                { "when": { "on": "signal", "name": "a" }, "do": [ { "do": "listAdd", "name": "$log", "value": "'a'" }, { "do": "signal", "name": "c", "to": "all" } ] },
                { "when": { "on": "signal", "name": "b" }, "do": [ { "do": "listAdd", "name": "$log", "value": "'b'" } ] },
                { "when": { "on": "signal", "name": "c" }, "do": [ { "do": "listAdd", "name": "$log", "value": "'c'" } ] } ] }
                """);
            w.BeginLevel();
            w.Tick(0.016);
            var log = w.GetVar(w.Level, "log").AsList.Select(v => v.Str);
            Assert.Equal("a,b,c", string.Join(",", log), "events run in the order they were raised; chained ones go to the back");
        }

        public static void ConditionsOnceAndCooldown()
        {
            var w = NewWorld("""
                { "vars": { "n": 0, "m": 0, "k": 0 }, "rules": [
                { "when": { "on": "every", "seconds": 0.5 }, "if": "$n < 2", "do": [ { "do": "addVar", "name": "$n" } ] },
                { "when": { "on": "every", "seconds": 0.5 }, "once": true, "do": [ { "do": "addVar", "name": "$m" } ] },
                { "when": { "on": "every", "seconds": 0.5 }, "cooldown": 2, "do": [ { "do": "addVar", "name": "$k" } ] } ] }
                """);
            Run(w, 5.2);
            Assert.Equal(2.0, Num(w, "n"), "if");
            Assert.Equal(1.0, Num(w, "m"), "once");
            Assert.Equal(3.0, Num(w, "k"), "cooldown 2 s over 5 s of 0.5 s ticks: t=0.5, 2.5, 4.5");
        }

        public static void ExpressionsAndSelectors()
        {
            var w = NewWorld("""{ "vars": { "r": 0, "t": "'x'", "p": "vec(1, 2)" } }""");
            var a = Place(w, """{ "id": "a", "team": "player", "labels": ["lit"] }""", new Vec(0, 0, 0));
            Place(w, """{ "id": "b", "team": "enemy" }""", new Vec(5, 0, 0));
            Place(w, """{ "id": "c", "team": "enemy", "labels": ["boss"] }""", new Vec(10, 0, 0));
            var ctx = new LogicContext { World = w, Self = a };
            Value E(string s) => ExprCompiler.Compile(s, "test")(ctx);
            Assert.Equal("b", E("nearest('foe')").AsEntity?.Id, "nearest foe");
            Assert.Equal("c", E("farthest('foe')").AsEntity?.Id, "farthest foe");
            Assert.Equal(1.0, E("count('foe', 6)").Num, "count in range");
            Assert.Equal(2.0, E("count('foe')").Num, "count anywhere");
            Assert.Equal(0.0, E("count('ally')").Num, "selectors skip self");
            Assert.Equal(1.0, E("count('ally or self')").Num, "unless the filter says self");
            Assert.Equal("c", E("nearest('#boss')").AsEntity?.Id, "label");
            Assert.Equal(true, Values.ToBool(E("self.is('#lit and player')")), "is()");
            Assert.Equal(7.0, E("1 + 2 * 3").Num, "precedence");
            Assert.Equal(0.0, E("5 / 0").Num, "division by zero is 0");
            Assert.Equal(2.0, E("$p.z").Num, "position var");
            Assert.Equal("x", E("$t").Str, "text var");
            Assert.Equal(5.0, E("nearest('foe').dist(self)").Num, "dist");
            Assert.Equal(true, Values.ToBool(E("count('foe') > 1 and night == 1")), "globals");
            Assert.Equal(90.0, Math.Round(E("angle(self, @b)").Num), "angle east");
            Assert.Equal("b", E("best('foe', 20, -it.x)").AsEntity?.Id, "best by score");
            Assert.Equal("Souls 0/10", ExprCompiler.CompileText("Souls {$r}/10", "t")(ctx), "interpolation");
            Assert.Equal(true, Values.ToBool(E("nearest('#nosuch') == none")), "none literal");
            Assert.Equal(true, Values.ToBool(E("other == none")), "an absent role is none");
            LogicServices.IsElite = e => e.Labels.Contains("boss");
            Assert.Equal(1.0, E("count('elite')").Num, "elite filter term");
            Assert.Equal(true, Values.ToBool(E("@c.elite")), ".elite");
            LogicServices.IsElite = null;
        }

        public static void OfListenerWithRange()
        {
            // Example 4's shape: a necromancer hears foes dying within 12 m.
            var w = NewWorld("{}", """{ "skeleton": { "kind": "unit" } }""");
            var necro = Place(w, """{ "id": "necro", "team": "enemy", "vars": { "raised": 0 }, "rules": [ { "when": { "on": "death", "of": "foe and unit", "range": 12 }, "do": [ { "do": "addVar", "name": "raised" }, { "do": "spawn", "entity": "skeleton", "at": "subject" } ] } ] }""", new Vec(0, 0, 0));
            var near = Place(w, """{ "id": "near", "team": "player", "kind": "unit" }""", new Vec(5, 0, 0));
            var far = Place(w, """{ "id": "far", "team": "player", "kind": "unit" }""", new Vec(50, 0, 0));
            var ally = Place(w, """{ "id": "ally", "team": "enemy", "kind": "unit" }""", new Vec(3, 0, 0));
            LogicActions.Register("spawn", (ctx, p) => ctx.World.Spawn(p.Template("entity"), p.Position("at") ?? Vec.Zero, 0, ctx.Self.Team, ctx.Self, ctx));
            w.Raise("death", near, null, 0, null, null);
            w.Raise("death", far, null, 0, null, null);
            w.Raise("death", ally, null, 0, null, null);
            w.Tick(0.016);
            Assert.Equal(1.0, w.GetVar(necro, "raised").Num, "only the foe in range");
            Assert.Equal(1, w.Entities.Count(e => e.Template == "skeleton"), "one skeleton");
            Assert.Equal(5f, w.Entities.First(e => e.Template == "skeleton").Position.X, "at the subject");
        }

        public static void SignalLoopHitsChainDepthAndStops()
        {
            var w = NewWorld("""{ "vars": { "n": 0 } }""");
            Place(w, """{ "id": "ping", "rules": [ { "when": { "on": "signal", "name": "go" }, "do": [ { "do": "addVar", "name": "$n" }, { "do": "signal", "name": "go", "to": "@pong" } ] } ] }""", Vec.Zero);
            Place(w, """{ "id": "pong", "rules": [ { "when": { "on": "signal", "name": "go" }, "do": [ { "do": "addVar", "name": "$n" }, { "do": "signal", "name": "go", "to": "@ping" } ] } ] }""", Vec.Zero);
            w.Signal("go", [w.Find("ping")], 0, null);
            Run(w, 2);
            Assert.True(Num(w, "n") <= w.Caps.ChainDepth + 1, $"loop stopped by chain depth (n={Num(w, "n")})");
            Assert.True(w.Budget().Dropped > 0, "the drop is counted");
            Assert.True(w.Trace.Any(t => t.Outcome == TraceOutcome.Dropped), "and traced");
        }

        public static void WideSignalLoopIsThrottledPerFrame()
        {
            // A loop that fans out: every firing sends to everyone, so it grows until the per-frame budget holds it.
            var w = NewWorld("""{ "vars": { "n": 0 } }""");
            for (int i = 0; i < 20; i++)
                Place(w, $$"""{ "id": "e{{i}}", "rules": [ { "when": { "on": "signal", "name": "go" }, "do": [ { "do": "addVar", "name": "$n" }, { "do": "signal", "name": "go", "to": "all" } ] } ] }""", Vec.Zero);
            w.Signal("go", null, 0, null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < 30; f++)
            {
                w.Tick(1.0 / 60);
                Assert.True(w.Budget().FiringsThisFrame <= w.Caps.FiringsPerFrame, "firings per frame capped");
            }
            Assert.True(w.Budget().Throttled > 0, "throttled");
            Assert.True(sw.ElapsedMilliseconds < 5000, $"bounded time ({sw.ElapsedMilliseconds} ms)");
        }

        public static void FanOutBombIsBoundedByTheQueue()
        {
            // Each firing raises 50 more: chain depth alone would allow 50^12 events.
            var w = NewWorld("""{ "vars": { "n": 0 } }""");
            // 20 owners, so the 30-per-second-per-owner cap doesn't hold it alone.
            for (int i = 0; i < 20; i++)
                Place(w, $$"""{ "id": "bomb{{i}}", "rules": [ { "when": { "on": "signal", "name": "flood" }, "do": [ { "do": "addVar", "name": "$n" }, { "do": "repeat", "times": 50, "actions": [ { "do": "signal", "name": "flood", "to": "all" } ] } ] } ] }""", Vec.Zero);
            w.Signal("flood", null, 0, null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var drops = new List<TraceEntry>();
            w.TraceListener = t => { if (t.Outcome == TraceOutcome.Dropped) drops.Add(t); };
            int maxQueue = 0;
            for (int f = 0; f < 120; f++)
            {
                w.Tick(1.0 / 60);
                maxQueue = Math.Max(maxQueue, w.Budget().QueuedEvents);
                Assert.True(w.Budget().QueuedEvents <= w.Caps.Queue + w.Caps.FiringsPerFrame, $"queue bounded ({w.Budget().QueuedEvents})");
            }
            Assert.True(drops.Count > 0, $"drops traced (max queue {maxQueue})");
            // The queue cap itself: fill it directly.
            for (int i = 0; i < w.Caps.Queue + 10; i++) w.Raise("ping", w.Level, null, 0, null, null);
            Assert.True(w.Budget().QueuedEvents <= w.Caps.Queue, "raise past the queue cap is dropped");
            Assert.True(drops.Any(t => t.Message.Contains("waiting")), "and traced");
            Assert.True(sw.ElapsedMilliseconds < 10000, $"bounded time ({sw.ElapsedMilliseconds} ms)");
        }

        public static void SpawnBombIsCapped()
        {
            var w = NewWorld("""{ "rules": [ { "when": "levelStart", "do": [ { "do": "repeat", "times": 100, "actions": [ { "do": "forEach", "in": "all('logic or self')", "actions": [] }, { "do": "spawnMany" } ] } ] } ] }""",
                """{ "gnat": { "kind": "unit" } }""");
            LogicActions.Register("spawnMany", (ctx, p) => { for (int i = 0; i < 10; i++) ctx.World.Spawn("gnat", Vec.Zero, 0, "enemy", null, ctx); });
            w.BeginLevel();
            Run(w, 1);
            var b = w.Budget();
            Assert.Equal(100, b.LiveUnits, "1000 spawns stop at the template's maxAlive (100)");
            Assert.Equal(900, b.CappedSpawns, "the rest are counted");
            Assert.True(w.Trace.Any(t => t.Outcome == TraceOutcome.Capped), "and traced");

            var w2 = NewWorld("""{ "caps": { "perTemplate": 1000, "units": 5000 } }""", """{ "gnat": { "kind": "unit", "maxAlive": 1000 } }""");
            Assert.Equal(1000, w2.Caps.Units, "level caps are clamped to the max");
            for (int i = 0; i < 1000; i++) w2.Spawn("gnat", Vec.Zero, 0, "enemy", null);
            Assert.Equal(300, w2.Budget().LiveUnits, "the engine-wide entity cap (300) still applies");
        }

        public static void ActionBudgetCarriesOver()
        {
            var w = NewWorld("""{ "vars": { "n": 0 }, "caps": { "actionsPerFrame": 50 }, "rules": [ { "when": "levelStart", "do": [ { "do": "repeat", "times": 100, "actions": [ { "do": "addVar", "name": "$n" } ] } ] } ] }""");
            w.BeginLevel();
            w.Tick(0.016);
            Assert.True(Num(w, "n") < 100, $"cut off by the action budget (n={Num(w, "n")})");
            Run(w, 0.2);
            Assert.Equal(100.0, Num(w, "n"), "the rest runs on later frames");
        }

        public static void HpBelowIsEdgeTriggered()
        {
            var w = NewWorld("""{ "vars": { "n": 0 } }""");
            var e = Place(w, """{ "id": "u", "rules": [ { "when": { "on": "hpBelow", "pct": 0.5 }, "do": [ { "do": "addVar", "name": "$n" } ] } ] }""", Vec.Zero);
            var body = (FakeBody)e.Body;
            void Hit(float hp, string ev = "damaged") { body.Hp = hp; w.Raise(ev, e, null, 1, null, null); w.Tick(0.016); }
            Hit(60); Hit(40); Hit(30);
            Assert.Equal(1.0, Num(w, "n"), "fires once on the crossing");
            Hit(80, "healed"); Hit(20);
            Assert.Equal(2.0, Num(w, "n"), "re-armed by healing above");
        }

        public static void PresetsExpandWithParams()
        {
            var w = new LogicWorld();
            LogicWorld.Current = w;
            LogicServices.Factory = new FakeFactory();
            LogicServices.Game = new FakeGame();
            var presets = new[] { JObject.Parse("""{ "id": "counter", "params": { "step": { "type": "number", "default": 1 } }, "vars": { "c": 0 }, "rules": [ { "when": { "on": "signal", "name": "inc" }, "do": [ { "do": "addVar", "name": "c", "value": "{step}" } ] } ] }""") };
            w.Load(new JObject(), [("thing", JObject.Parse("""{ "kind": "logic", "rules": [ { "preset": "counter", "with": { "step": 5 } } ] }"""))], null, presets, "levels[0]");
            var e = w.Describe("thing", null, "t1", null, null);
            w.Add(e);
            w.Signal("inc", [e], 0, null);
            w.Signal("inc", [e], 0, null);
            w.Tick(0.016);
            Assert.Equal(10.0, w.GetVar(e, "c").Num, "preset var + param");
        }

        public static void BuiltInInputsAndRuleToggles()
        {
            var w = NewWorld("""{ "vars": { "n": 0 } }""");
            var e = Place(w, """{ "id": "t", "rules": [ { "id": "tick", "when": { "on": "every", "seconds": 0.5 }, "do": [ { "do": "addVar", "name": "$n" } ] } ] }""", Vec.Zero);
            Run(w, 1.1);
            Assert.Equal(2.0, Num(w, "n"), "running");
            w.Signal("disable", [e], 0, null);
            Run(w, 1.0);
            Assert.Equal(2.0, Num(w, "n"), "disabled by the built-in input");
            w.Signal("enable", [e], 0, null);
            w.SetRuleEnabled(e, "tick", false);
            Run(w, 1.0);
            Assert.Equal(2.0, Num(w, "n"), "rule disabled by id");
        }

        public static void SnapshotRoundTrip()
        {
            const string logic = """{ "vars": { "n": 0 }, "rules": [ { "when": { "on": "every", "seconds": 1 }, "do": [ { "do": "addVar", "name": "$n" } ] }, { "when": { "on": "after", "seconds": 0.5 }, "do": [ { "do": "spawn2" } ] } ] }""";
            LogicActions.Register("spawn2", (ctx, p) => { var s = ctx.World.Spawn("gnat", new Vec(3, 0, 4), 0, "enemy", null, ctx); s.Vars["gen"] = Value.Number(2); s.Labels.Add("x"); });
            var w = NewWorld(logic, """{ "gnat": { "kind": "unit" } }""");
            var t = Place(w, """{ "id": "t", "vars": { "hits": 0 }, "rules": [ { "id": "r", "once": true, "when": { "on": "signal", "name": "s" }, "do": [ { "do": "addVar", "name": "hits" } ] } ] }""", Vec.Zero);
            w.BeginLevel();
            w.Signal("s", [t], 0, null);
            Run(w, 2.6);
            double rnd = w.Random.NextDouble();
            var snap = w.Capture();
            double next = w.Random.NextDouble();

            var w2 = NewWorld(logic, """{ "gnat": { "kind": "unit" } }""");
            var t2 = Place(w2, """{ "id": "t", "vars": { "hits": 0 }, "rules": [ { "id": "r", "once": true, "when": { "on": "signal", "name": "s" }, "do": [ { "do": "addVar", "name": "hits" } ] } ] }""", Vec.Zero);
            string summary = w2.Restore(JObject.Parse(snap.ToString()));
            Assert.Equal(2.0, Num(w2, "n"), "level var");
            Assert.Equal(1.0, w2.GetVar(t2, "hits").Num, "entity var");
            var g = w2.Entities.Single(e => e.Template == "gnat");
            Assert.Equal(2.0, w2.GetVar(g, "gen").Num, "spawned entity var: " + summary);
            Assert.True(g.Labels.Contains("x"), "spawned entity label");
            Assert.Equal(3f, g.Position.X, "spawned entity position");
            Assert.Equal(next, w2.Random.NextDouble(), "random source continues where it was");
            w2.Signal("s", [t2], 0, null);
            Run(w2, 1.0);
            Assert.Equal(1.0, w2.GetVar(t2, "hits").Num, "`once` state kept");
            Assert.Equal(3.0, Num(w2, "n"), "timer phase kept (3rd tick at t=3)");
            Assert.Equal(1, w2.Entities.Count(e => e.Template == "gnat"), "the `after` rule doesn't fire again");
            _ = rnd;
        }

        public static void EveryL1SchemaActionHasAHandler()
        {
            _ = new LogicWorld();   // registers the core's actions
            var missing = PalaceMod.Builder.Model.Logic.LogicSchema.Actions.Values
                .Where(a => a.Owner == "L1" && !ActionCompiler.Control.Contains(a.Name) && !LogicActions.IsRegistered(a.Name))
                .Select(a => a.Name).ToList();
            Assert.True(missing.Count == 0, "L1 actions without a handler: " + string.Join(", ", missing));
            var unknown = ActionCompiler.Control.Where(c => !PalaceMod.Builder.Model.Logic.LogicSchema.Actions.ContainsKey(c)).ToList();
            Assert.True(unknown.Count == 0, "control actions missing from the schema: " + string.Join(", ", unknown));
        }

        public static void CompileErrorsAreTracedNotThrown()
        {
            var w = NewWorld("""{ "rules": [ { "when": "levelStart", "if": "1 +", "do": [] }, { "when": "levelStart", "do": [ { "do": "nosuch" } ] } ] }""");
            w.BeginLevel();
            w.Tick(0.016);
            Assert.True(w.Trace.Count(t => t.Outcome == TraceOutcome.Error) >= 2, "both errors traced: " + string.Join(" | ", w.Trace.Select(t => t.Message)));
        }
    }
}
