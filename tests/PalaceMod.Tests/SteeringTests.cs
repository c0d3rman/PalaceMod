using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies.Steering;

namespace PalaceMod.Tests
{
    /// <summary>Targeting lists and their runtime layers (Steering's setTargeting).</summary>
    public static class SteeringTests
    {
        private static string Filters(TargetingState s, double now) =>
            string.Join(" > ", s.Effective(now, out bool tail).Select(r => r.Filter)) + (tail ? " > game" : "");

        private static void Eq(string expected, string actual, string what)
        {
            if (expected != actual) throw new Exception($"{what}: expected \"{expected}\", got \"{actual}\"");
        }

        public static void ParsesStringsAndRules()
        {
            var l = TargetRule.ParseList(JToken.Parse("[\"foe and Tower\", {\"filter\": \"foe\", \"range\": 25, \"prefer\": \"best\", \"score\": \"-it.hp\"}]"));
            Eq("foe and Tower|nearest", l[0].Filter + "|" + l[0].Prefer, "bare filter");
            Eq("foe|best|25", $"{l[1].Filter}|{l[1].Prefer}|{l[1].Range}", "rule");
            Eq("foe", TargetRule.ParseList(JToken.Parse("\"foe\""))[0].Filter, "a lone filter");
        }

        public static void AScoreMeansPreferBest()
        {
            var r = TargetRule.ParseList(JToken.Parse("[{\"filter\": \"player and building\", \"score\": \"it.income\"}]"))[0];
            Eq("best", r.Prefer, "a score without prefer ranks by the score");
            Eq("{\"filter\":\"player and building\",\"score\":\"it.income\"}", r.ToJson().ToString(Newtonsoft.Json.Formatting.None), "round trip keeps it implied");
        }

        public static void RejectsBadRules()
        {
            foreach (var bad in new[] { "{\"filter\": \"foe\", \"prefer\": \"closest\"}", "{\"filter\": \"foe\", \"prefer\": \"best\"}", "\"foe and (\"",
                "{\"filter\": \"foe\", \"prefer\": \"nearest\", \"score\": \"it.hp\"}" })
            {
                bool threw = false;
                try { TargetRule.ParseList(JToken.Parse(bad)); } catch (LogicError) { threw = true; }
                if (!threw) throw new Exception($"{bad} should be a rule error");
            }
        }

        public static void PrependOnTheGamesPrioritiesFallsBack()
        {
            var s = new TargetingState();
            s.Apply(JToken.Parse("{\"prepend\": [\"#decoy\"]}"), 0, 0);
            Eq("#decoy > game", Filters(s, 0), "prepend over the game's own");
        }

        public static void TimedLayersRevert()
        {
            var s = new TargetingState { Base = TargetRule.ParseList(JToken.Parse("[\"foe and Building\", \"foe\"]")) };
            s.Apply(JToken.Parse("{\"prepend\": [\"#decoy\"]}"), 8, 10);
            s.Apply(JToken.Parse("{\"remove\": [\"foe and Building\"]}"), 0, 10);
            Eq("#decoy > foe", Filters(s, 12), "prepend + remove");
            Eq("foe", Filters(s, 18.5), "prepend expired");
            s.Apply(JToken.Parse("[\"king\"]"), 2, 20);
            Eq("king", Filters(s, 21), "timed replace");
            Eq("foe", Filters(s, 22.1), "replace expired");
        }

        public static void PermanentReplaceBecomesTheBase()
        {
            var s = new TargetingState { Base = TargetRule.ParseList(JToken.Parse("[\"foe\"]")) };
            s.Apply(JToken.Parse("{\"remove\": [\"foe\"]}"), 0, 0);
            s.Apply(JToken.Parse("[\"Tower\"]"), 0, 0);
            Eq("Tower", Filters(s, 1), "new base, earlier permanent layers dropped");
            s.Apply(JToken.Parse("{\"reset\": true}"), 0, 0);
            Eq(" > game", Filters(s, 1), "reset");
        }

        public static void CaptureRestoreRoundTrip()
        {
            var s = new TargetingState { Base = TargetRule.ParseList(JToken.Parse("[{\"filter\": \"foe\", \"prefer\": \"weakest\", \"range\": 12}]")) };
            s.Apply(JToken.Parse("{\"prepend\": [\"king\"]}"), 5, 100);
            var saved = s.Capture(101);
            var r = new TargetingState();
            r.Restore(saved, 0);
            Eq("king > foe", Filters(r, 3.9), "restored, layer still on");
            Eq("foe", Filters(r, 4.1), "restored layer keeps its remaining time");
            Eq("weakest|12", $"{r.Base[0].Prefer}|{r.Base[0].Range}", "rule fields");
        }
    }
}
