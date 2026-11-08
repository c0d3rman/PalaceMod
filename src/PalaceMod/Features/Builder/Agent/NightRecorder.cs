using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static PalaceMod.Builder.Agent.AgentCommands;

/*
    Records each night while the Agent bridge runs, for `agent report`: how it ended (survived to dawn, the run won,
    or the castle fell), how long it took, the castle's lowest health, which buildings were knocked out, units lost,
    king knock-outs, and enemies seen and left. Sampled four times a second (real time); the last night's report is
    kept until the next night starts or a level is loaded through the bridge.
*/
namespace PalaceMod.Builder.Agent
{
    internal static class NightRecorder
    {
        private sealed class Night
        {
            public int Number;
            public string Scene;
            public float StartGameTime, StartReal;
            public float EndGameTime = -1f;
            public string Outcome;  // null while running: "dawn", "victory", "defeat", "left"
            public int GoldStart, GoldEnd = -1;
            public float CastleMin = float.MaxValue, CastleMax;
            public readonly HashSet<string> BuildingsLost = [];
            public readonly HashSet<int> EnemiesSeen = [];
            public int EnemiesMax, UnitsStart = -1, UnitsMin = int.MaxValue, UnitsEnd = -1, KingKnockouts, BuildingsStart;
            public bool KingDown;
            public readonly List<JObject> Events = [];
        }

        private static Night current, last;
        private static float nextSample;

        public static void Reset()
        {
            current = null;
            last = null;
        }

        public static void Tick()
        {
            if (Time.unscaledTime < nextSample) return;
            nextSample = Time.unscaledTime + 0.25f;
            bool inLevel = DebugActions.InLevel;
            var state = LocalGamestate.Instance != null ? LocalGamestate.Instance.CurrentState : LocalGamestate.State.PreMatch;

            if (current != null)
            {
                if (state == LocalGamestate.State.AfterMatchDefeat) { Sample(); Finish("defeat"); return; }
                if (state == LocalGamestate.State.AfterMatchVictory) { Sample(); Finish("victory"); return; }
                if (!inLevel || DayNightCycle.Instance == null) { Finish("left"); return; }
                if (!DebugActions.IsNight) { Sample(); Finish("dawn"); return; }
                Sample();
                return;
            }
            if (inLevel && DebugActions.IsNight) Begin();
        }

        private static void Begin()
        {
            var units = TagManager.instance.PlayerUnits.Count(u => u != null && u.Hp != null && !u.Hp.KnockedOut);
            current = new Night
            {
                Number = DebugActions.CurrentNight,
                Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                StartGameTime = Time.time,
                StartReal = Time.unscaledTime,
                GoldStart = DebugActions.Gold,
                UnitsStart = units,
                BuildingsStart = BuiltSlots().Count(),
            };
            Sample();
        }

        private static void Sample()
        {
            var n = current;
            if (n == null || TagManager.instance == null) return;
            var castle = CastleHp();
            if (castle != null)
            {
                n.CastleMin = Mathf.Min(n.CastleMin, Mathf.Max(0f, castle.HpValue));
                n.CastleMax = Mathf.Max(n.CastleMax, castle.maxHp);
            }
            foreach (var slot in BuiltSlots())
            {
                var hp = BuildingHp(slot);
                if (hp != null && hp.KnockedOut && n.BuildingsLost.Add(slot.name)) Event(n, $"{slot.buildingName} knocked out ({slot.name})");
            }
            int enemies = 0;
            foreach (var e in TagManager.instance.EnemyUnits)
            {
                if (e == null || e.Hp == null || e.Hp.KnockedOut) continue;
                enemies++;
                n.EnemiesSeen.Add(e.GetInstanceID());
            }
            n.EnemiesMax = Mathf.Max(n.EnemiesMax, enemies);
            int units = TagManager.instance.PlayerUnits.Count(u => u != null && u.Hp != null && !u.Hp.KnockedOut);
            n.UnitsMin = Mathf.Min(n.UnitsMin, units);
            n.UnitsEnd = units;
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<Hp>() : null;
            bool down = king != null && king.KnockedOut;
            if (down && !n.KingDown) { n.KingKnockouts++; Event(n, "king knocked out"); }
            n.KingDown = down;
            n.GoldEnd = DebugActions.InLevel ? DebugActions.Gold : n.GoldEnd;
        }

        private static void Event(Night n, string text) =>
            n.Events.Add(new JObject { ["t"] = R(Time.time - n.StartGameTime), ["event"] = text });

        private static void Finish(string outcome)
        {
            current.Outcome = outcome;
            current.EndGameTime = Time.time;
            Event(current, outcome == "dawn" ? "dawn" : outcome);
            Log.For<AgentBridgeManager>().Info($"Night {current.Number} on {current.Scene}: {outcome}, castle min {current.CastleMin:0}/{current.CastleMax:0}, " +
                $"{current.BuildingsLost.Count} buildings knocked out");
            last = current;
            current = null;
        }

        /// <summary>The running night, else the last finished one; done=false while it runs.</summary>
        public static JObject Report()
        {
            var n = current ?? last;
            if (n == null) return new JObject { ["night"] = null, ["done"] = false, ["note"] = "no night recorded yet" };
            float end = n.EndGameTime >= 0 ? n.EndGameTime : Time.time;
            return new JObject
            {
                ["night"] = n.Number,
                ["scene"] = n.Scene,
                ["done"] = n.Outcome != null,
                ["outcome"] = n.Outcome,
                ["seconds"] = R(end - n.StartGameTime),
                ["castle"] = n.CastleMax > 0 ? new JObject { ["minHp"] = R(n.CastleMin), ["maxHp"] = R(n.CastleMax) } : null,
                ["buildings"] = new JObject { ["builtAtDusk"] = n.BuildingsStart, ["knockedOut"] = new JArray(n.BuildingsLost.OrderBy(s => s)) },
                ["units"] = new JObject { ["atDusk"] = n.UnitsStart, ["fewestStanding"] = n.UnitsMin == int.MaxValue ? 0 : n.UnitsMin, ["standingAtEnd"] = n.UnitsEnd },
                ["king"] = new JObject { ["knockouts"] = n.KingKnockouts },
                ["enemies"] = new JObject { ["seen"] = n.EnemiesSeen.Count, ["mostAtOnce"] = n.EnemiesMax },
                ["gold"] = new JObject { ["atDusk"] = n.GoldStart, ["now"] = n.GoldEnd },
                ["events"] = new JArray(n.Events),
            };
        }

        public static JObject Summary()
        {
            var n = current ?? last;
            return n == null ? null : new JObject { ["night"] = n.Number, ["done"] = n.Outcome != null, ["outcome"] = n.Outcome };
        }
    }
}
