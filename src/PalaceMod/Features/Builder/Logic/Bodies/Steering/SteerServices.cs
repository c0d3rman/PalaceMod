using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    /// <summary>LogicServices.Targeting: setTargeting from anywhere (actions, Stats, presets).</summary>
    internal sealed class SteerTargeting : ITargeting
    {
        /// <summary>
        /// <paramref name="targeting"/>: a list (or one rule/filter) replaces; an object takes `targeting`, `prepend`,
        /// `remove` and `reset`. <paramref name="seconds"/> &gt; 0 reverts it after that long.
        /// </summary>
        public void Set(LogicEntity e, JToken targeting, double seconds)
        {
            if (e == null) return;
            var go = e.Go();
            if (go == null) return;   // a body-less entity has nothing to aim: skipped
            var st = LogicSteeringManager.For(e, go) ?? throw new LogicError("setTargeting: no logic session");
            st.Targeting ??= new TargetingState();
            st.Targeting.Apply(targeting, seconds, LogicWorld.Current.Time);
            SteerMovement.Repath(st);
        }
    }

    /// <summary>Savestates: runtime targeting/movement overrides per entity id (template components come back with the unit).</summary>
    internal static class SteerSave
    {
        public static JToken Capture(LogicWorld world)
        {
            var arr = new JArray();
            double now = world.Time;
            foreach (var st in LogicSteeringManager.States)
            {
                if (st.Entity == null || st.Go == null) continue;
                var o = new JObject { ["id"] = st.Entity.Id };
                if (st.Targeting != null && (st.Targeting.Layers.Count > 0 || st.Entity.Def?["targeting"] == null && st.Targeting.Base != null))
                    o["targeting"] = st.Targeting.Capture(now);
                var overlays = st.MovementOverlays.Where(x => x.until > now).Select(x => new JObject { ["fields"] = x.fields, ["left"] = double.IsPositiveInfinity(x.until) ? -1 : x.until - now }).ToList();
                if (overlays.Count > 0) o["movement"] = new JArray(overlays);
                if (st.PathIndex > 0) o["pathIndex"] = st.PathIndex;
                if (o.Count > 1) arr.Add(o);
            }
            return arr.Count > 0 ? arr : null;
        }

        public static void Restore(LogicWorld world, JToken state)
        {
            if (state is not JArray arr) return;
            double now = world.Time;
            foreach (var o in arr.OfType<JObject>())
            {
                var e = world.Find((string)o["id"]);
                var go = e?.Go();
                if (go == null) continue;
                var st = LogicSteeringManager.For(e, go);
                if (o["targeting"] is JObject t) { st.Targeting ??= new TargetingState(); st.Targeting.Restore(t, now); }
                foreach (var m in o["movement"] as JArray ?? [])
                {
                    double left = (double)m["left"];
                    st.MovementOverlays.Add(((JObject)m["fields"], left < 0 ? double.PositiveInfinity : now + left));
                }
                st.MovementChanged();
                if (o["pathIndex"] != null) { _ = st.Move; st.PathIndex = (int)o["pathIndex"]; }
            }
        }
    }

    /// <summary>`logic-steer`: apply template components to live units (until the factory raises UnitSpawned), inspect.</summary>
    internal static class SteerDev
    {
        public static string Run(string[] args)
        {
            string sub = args.FirstOrDefault() ?? "";
            LogicWorld.Current ??= new LogicWorld();
            switch (sub)
            {
                case "apply":
                    {
                        if (args.Length < 3) return "apply <role> <json: {targeting, movement, attacks}>";
                        var ctx = new LogicContext { World = LogicWorld.Current, Self = LogicWorld.Current.Level };
                        var targets = DevParams.Resolve(args[1], ctx) ?? throw new System.ArgumentException($"no entity for {args[1]}");
                        var def = JObject.Parse(string.Join(" ", args.Skip(2)));
                        int n = 0;
                        foreach (var e in targets.Take(256))
                        {
                            if (e.Go() == null) continue;
                            // as BodyEvents.UnitSpawned would for a Builder unit with this template
                            LogicSteeringManager.ApplyComponents(e, e.Go(), def);
                            n++;
                        }
                        return $"applied to {n} unit(s): {string.Join(", ", targets.Take(6).Select(t => t.Id))}";
                    }
                case "state":
                    {
                        if (args.Length < 2) return "state <role>";
                        var ctx = new LogicContext { World = LogicWorld.Current, Self = LogicWorld.Current.Level };
                        var e = DevParams.Resolve(args[1], ctx)?.FirstOrDefault() ?? throw new System.ArgumentException($"no entity for {args[1]}");
                        var st = LogicSteeringManager.Get(e.Go());
                        var go = e.Go();
                        string pos = go != null ? go.transform.position.ToString("F1") : "-";
                        if (st == null) return $"{e.Id} team={e.Team} hp={e.Body?.Hp:0.#}/{e.Body?.MaxHp:0.#} at {pos}: no steering";
                        var m = st.Move;
                        string tgt = st.Targeting == null ? "game" : string.Join(" > ", st.Targeting.Effective(LogicWorld.Current.Time, out bool tail).Select(r => r.Filter)) + (tail ? " > game" : "");
                        string seek = st.Enemy != null ? SteerMovement.E_seekObj(st.Enemy)?.name : st.Player != null ? SteerMovement.P_seekObj(st.Player)?.name : null;
                        var attacks = go.GetComponents<BuilderAttack>();
                        // what the game's own attack would pick now (through the patched finders)
                        string picks = "-";
                        if (go.GetComponentInChildren<AutoAttackTower>() is { } tower)
                        {
                            tower.FindAutoAttackTargets();
                            picks = string.Join(",", Traverse.Create(tower).Field<System.Collections.Generic.List<TaggedObject>>("attackTargets").Value.Select(t => t != null ? t.name : "null"));
                        }
                        else if (go.GetComponentInChildren<AutoAttack>() is { } aa) picks = aa.FindAutoAttackTarget()?.name ?? "none";
                        return $"{e.Id} attackPick={picks} team={e.Team} hp={e.Body?.Hp:0.#}/{e.Body?.MaxHp:0.#} at {pos} mode={m.Mode} path={st.PathIndex}/{st.PathPoints?.Count ?? 0}{(st.PathDone ? " done" : "")} moveTo={(st.MoveTo != null ? st.MoveTo.Point.ToString("F1") : "-")} " +
                               $"kite={(st.KiteUntil > LogicWorld.Current.Time)} flee={st.Fleeing} targeting=[{tgt}] taunt={st.Targeting?.Taunt?.Id ?? "-"} seek={seek ?? "-"} attacks={attacks.Length} leader={st.FollowLeader?.Id ?? "-"} slot={(st.FollowDest is Vector3 fd ? fd.ToString("F1") : "-")} overriding={st.Overriding} heading={Heading.Yaw(e):0} vel={(st.Enemy != null && st.Enemy.RVO != null ? st.Enemy.RVO.velocity.ToString("F1") : "-")}";
                    }
                case "units":
                    {
                        var lines = LogicSteeringManager.States.Where(s => s.Go != null).Take(40)
                            .Select(s => $"{s.Entity?.Id} {s.Go.transform.position:F1} mode={s.Move.Mode}");
                        return string.Join("\n", lines.DefaultIfEmpty("no steered units"));
                    }
                case "counterparts":
                    {
                        return string.Join("\n", new[] { "player", "enemy" }.SelectMany(side => new[] { "melee", "ranged", "flying" }.Select(role => $"{side}/{role}: {SteerTeams.Counterpart(side, role)?.Id ?? "-"}")));
                    }
                default:
                    return "logic-steer apply <role> <json> | state <role> | units | counterparts";
            }
        }
    }
}
