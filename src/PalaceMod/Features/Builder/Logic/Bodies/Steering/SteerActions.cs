using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    The Move and Team actions (docs/builder-engine.md, "Actions"): teleport, push, drift, moveTo, setMovement,
    setTargeting, taunt, command, convert, replace. All are PerTarget (the core fans `target` out, default self).
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal static class SteerActions
    {
        public static void Register()
        {
            LogicActions.Register("teleport", Teleport, ActionFlags.PerTarget);
            LogicActions.Register("push", Push, ActionFlags.PerTarget);
            LogicActions.Register("drift", Drift, ActionFlags.PerTarget);
            LogicActions.Register("moveTo", MoveTo, ActionFlags.PerTarget);
            LogicActions.Register("setMovement", SetMovement, ActionFlags.PerTarget);
            LogicActions.Register("setTargeting", SetTargeting, ActionFlags.PerTarget);
            LogicActions.Register("taunt", Taunt, ActionFlags.PerTarget);
            LogicActions.Register("command", Command, ActionFlags.PerTarget);
            LogicActions.Register("convert", Convert, ActionFlags.PerTarget);
            LogicActions.Register("replace", Replace, ActionFlags.PerTarget);
        }

        private static LogicEntity Target(ActionParams p, string action)
        {
            var t = p.Target;
            if (t == null) throw new LogicError($"{action}: no target");
            return t;
        }

        private static Vector3 Need(ActionParams p, string name, string action) =>
            p.Position(name)?.ToVector3() ?? throw new LogicError($"{action}: `{name}` is missing or isn't a position");

        // ------------------------------------------------------------------ teleport

        private static void Teleport(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "teleport");
            var at = Need(p, "at", "teleport");
            var go = e.Go();
            if (go == null || !e.Exists) return;
            if (e.Kind is "building" or "castle") return;   // buildings don't move (skipped, as every verb skips members it can't act on)
            if (go.GetComponent<PlayerMovement>() is { } king) { king.TeleportTo(at); return; }
            var pm = go.GetComponent<PathfindMovement>();
            // Onto a riding deck (off the graphs): stand on it as given, and ride it (DeckCarrier).
            if (Walkables.OnRidingDeck(at)) { go.transform.position = at; pm?.ClearCurrentPath(); return; }
            if (pm is PathfindMovementEnemy en) at = Flyer(go) ? new Vector3(en.GetNearestGroundPosition(at).x, at.y, en.GetNearestGroundPosition(at).z) : en.GetNearestGroundPosition(at);
            else if (pm is PathfindMovementPlayerunit pu) at = Flyer(go) ? at : pu.GetNearestGroundPosition(at);
            go.transform.position = at;
            if (pm != null)
            {
                pm.ClearCurrentPath();
                if (LogicSteeringManager.Get(go) is { } st) { st.HoldPoint = null; st.WanderPoint = null; SteerMovement.Repath(st); }
                else if (pm is PathfindMovementEnemy en2) SteerMovement.E_repath(en2) = 0;
                else if (pm is PathfindMovementPlayerunit pu2) SteerMovement.P_repath(pu2) = 0;
            }
        }

        private static bool Flyer(GameObject go) => go.GetComponent<TaggedObject>()?.Tags.Contains(TagManager.ETag.Flying) ?? false;

        // ------------------------------------------------------------------ push (the game's knockback)

        private static readonly AnimationCurve Flat1 = AnimationCurve.Constant(0, 1, 1);
        private static readonly AccessTools.FieldRef<KnockbackBehavior, float> arcHeight = AccessTools.FieldRefAccess<KnockbackBehavior, float>("arcHeight");

        private static void Push(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "push");
            var go = e.Go();
            if (go == null || !e.Alive) return;
            if (e.Kind is "building" or "castle") return;   // buildings don't move
            var hp = go.GetComponent<Hp>();
            var pm = go.GetComponent<PathfindMovement>();
            if (hp == null || pm == null || pm.RVO == null) return;   // the king and bodies without the game's movement
            bool pull = !p.Has("from") && p.Has("toward");
            var src = p.Position(pull ? "toward" : "from")?.ToVector3() ?? (ctx.Self?.Position.ToVector3() ?? go.transform.position);
            var dir = go.transform.position - src;
            if (pull) dir = -dir;
            dir.y = 0;
            float dist = dir.magnitude;
            dir = dist > 0.01f ? dir / dist : go.transform.forward;
            float force = (float)p.Number("force", 3);
            if (pull) force = Mathf.Min(force, Mathf.Max(0, dist - 0.5f));   // a pull stops at the puller
            float stun = (float)p.Number("stun", 0);
            var kb = go.GetComponent<KnockbackBehavior>() ?? go.AddComponent<KnockbackBehavior>();
            if (!kb.enabled) return;
            if (p.Has("up")) arcHeight(kb) = (float)p.Number("up", 1.5);
            kb.ApplyKnockback(dir, 0, 1, Mathf.Max(force, 0.001f), hp, pm, pm.RVO, hp.AutoAttacks, Flat1, stun, stun > 0 ? Flat1 : null);
        }

        // ------------------------------------------------------------------ drift

        private static void Drift(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "drift");
            string key = p.Text("key", "drift");
            double seconds = p.Number("for", 0);
            if (p.Has("speed") && p.Number("speed") == 0 && !p.Has("dir")) { Drifts.Remove(e, key); return; }
            float speed = (float)p.Number("speed", 3);
            Func<Vector3> v;
            if (p.Has("dir"))
            {
                var d = p.Position("dir")?.ToVector3() ?? throw new LogicError("drift: `dir` must be a vector");
                d.y = 0;
                var vel = p.Has("speed") ? d.normalized * speed : d;
                v = () => vel;
            }
            else
            {
                bool away = p.Has("from");
                var g = p.Get(away ? "from" : "toward");
                LogicEntity anchor = g.Kind == ValueKind.Entity ? g.AsEntity : g.Kind == ValueKind.Set && g.AsSet.Count > 0 ? g.AsSet[0] : null;
                Vector3? point = g.Kind == ValueKind.Position ? g.Pos.ToVector3() : null;
                if (anchor == null && point == null) throw new LogicError("drift: give `dir`, `toward` or `from`");
                v = () =>
                {
                    if (anchor != null && !anchor.Exists) return Vector3.zero;
                    var to = anchor != null ? anchor.Position.ToVector3() : point.Value;
                    var d = to - e.Position.ToVector3();
                    d.y = 0;
                    if (away) d = -d;
                    return !away && d.sqrMagnitude < 0.09f ? Vector3.zero : d.normalized * speed;
                };
            }
            Drifts.Set(e, key, v, seconds);
        }

        // ------------------------------------------------------------------ moveTo / setMovement / setTargeting

        private static SteerState Unit(LogicEntity e, string action)
        {
            var go = e.Go();
            if (go == null || go.GetComponent<PathfindMovement>() == null) return null;   // not a walking unit: skipped
            return LogicSteeringManager.For(e, go);
        }

        private static void MoveTo(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "moveTo");
            if (!e.Alive) return;
            var st = Unit(e, "moveTo");
            if (st == null) return;
            var g = p.Get("at");
            var order = new MoveToOrder
            {
                AtEntity = g.Kind == ValueKind.Entity ? g.AsEntity : null,
                At = p.Position("at")?.ToVector3() ?? throw new LogicError("moveTo: `at` is missing or isn't a position"),
                Speed = p.Has("speed") ? (float)p.Number("speed") : null,
                Then = p.Text("then", "resume"),
            };
            if (order.Then is not ("resume" or "hold")) throw new LogicError($"moveTo then \"{order.Then}\": expected resume or hold");
            // a hold left by an earlier moveTo ends with a new order
            if (st.MovementOverlays.RemoveAll(o => o.fields.Count == 1 && (string)o.fields["mode"] == "hold" && double.IsPositiveInfinity(o.until)) > 0) st.MovementChanged();
            st.MoveTo = order;
            SteerMovement.Repath(st);
        }

        /// <summary>setMovement: the given fields (or a whole `movement` block) merged over the current movement;
        /// `for` seconds makes it revert; `reset: true` drops every earlier setMovement.</summary>
        private static void SetMovement(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "setMovement");
            var st = Unit(e, "setMovement");
            if (st == null) return;
            var fields = new JObject();
            if (p.Raw("movement") is JObject block) fields.Merge(block);
            foreach (var name in new[] { "mode", "path", "then", "speed", "keepDistance", "kite", "flee", "wander", "home" })
            {
                if (!p.Has(name)) continue;
                var raw = p.Raw(name);
                // numbers may be expressions; the rest are written as JSON
                fields[name] = name is "speed" or "keepDistance" && raw.Type == JTokenType.String ? p.Number(name) : raw.DeepClone();
            }
            if (p.Bool("reset", false)) st.MovementOverlays.Clear();
            MovementDef.Parse(fields);   // check before applying
            double seconds = p.Number("for", 0);
            if (fields.Count > 0) st.MovementOverlays.Add((fields, seconds > 0 ? ctx.World.Time + seconds : double.PositiveInfinity));
            st.MovementChanged();
            st.BaseWritten = false;
            SteerMovement.Repath(st);
        }

        private static void SetTargeting(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "setTargeting");
            var o = new JObject();
            foreach (var name in new[] { "targeting", "prepend", "remove" }) if (p.Has(name)) o[name] = p.Raw(name).DeepClone();
            if (p.Bool("reset", false)) o["reset"] = true;
            if (o.Count == 0) throw new LogicError("setTargeting: give `targeting`, `prepend`, `remove` or `reset`");
            LogicSteeringManager.TargetingService.Set(e, o, p.Number("for", 0));
        }

        // ------------------------------------------------------------------ taunt

        private static void Taunt(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "taunt");
            var toward = p.Entity("toward") ?? throw new LogicError("taunt: `toward` must be an entity");
            double seconds = p.Number("for", 5);
            if (e.Kind == "projectile") { Projectiles.Redirect(e, toward, null); return; }   // a decoy pulls arrows
            var go = e.Go();
            if (go == null || !e.Alive) return;
            var st = LogicSteeringManager.For(e, go);
            st.Targeting ??= new TargetingState();
            st.Targeting.Taunt = toward;
            st.Targeting.TauntUntil = ctx.World.Time + seconds;
            // the game's own agro too (enemies chasing the king keep its live tracking)
            if (go.GetComponent<PathfindMovementEnemy>() is { } en && toward.Go()?.GetComponent<TaggedObject>() is { } tt && tt.Tags.Contains(TagManager.ETag.Player))
            {
                float keep = en.agroTimeWhenAttackedByPlayer;
                en.agroTimeWhenAttackedByPlayer = (float)seconds;
                en.GetAgroFromObject(tt);
                en.agroTimeWhenAttackedByPlayer = keep;
            }
            SteerMovement.Repath(st);
        }

        // ------------------------------------------------------------------ command (the king's own orders)

        private static void Command(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "command");
            var go = e.Go();
            if (go == null || !e.Alive) return;
            var pu = go.GetComponent<PathfindMovementPlayerunit>();
            if (pu == null) return;   // the king, enemies, buildings: skipped
            var cu = CommandUnits.instance;
            string order = p.Text("order", "follow");
            if (order is not ("follow" or "hold" or "home")) throw new LogicError($"command order \"{order}\": expected follow, hold or home");
            bool commanded = cu != null && cu.PlayerUnitsCommanding.Contains(pu);
            switch (order)
            {
                case "follow":
                    if (cu == null || commanded) return;
                    cu.OnUnitAdd(go.GetComponent<TaggedObject>(), false);
                    break;
                case "hold":
                    Release(cu, pu, commanded);
                    pu.HomePosition = p.Position("at")?.ToVector3() is Vector3 at ? pu.GetNearestGroundPosition(at) : go.transform.position;
                    pu.HasReachedHomePositionAlready = false;
                    pu.HoldPosition = true;
                    break;
                case "home":
                    Release(cu, pu, commanded);
                    pu.HoldPosition = false;
                    pu.HomePosition = pu.HomePositionOriginal;
                    pu.HasReachedHomePositionAlready = false;
                    break;
                default: throw new LogicError($"command order \"{order}\": expected follow, hold or home");
            }
            pu.ClearCurrentPath();
            SteerMovement.P_repath(pu) = 0;
        }

        private static void Release(CommandUnits cu, PathfindMovementPlayerunit pu, bool commanded)
        {
            if (cu == null || !commanded) return;
            cu.PlayerUnitsCommanding.Remove(pu);
            cu.OnUnitRemove(pu);
            foreach (var a in pu.GetComponents<AutoAttack>()) a.enabled = true;   // OnUnitAdd switched them off
        }

        // ------------------------------------------------------------------ convert / replace

        private static void Convert(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "convert");
            string team = TeamParam(ctx, p);
            SteerTeams.ConvertEntity(e, team, p.Bool("keepLook", true), p.Has("hpPct") ? p.Number("hpPct") : null);
        }

        /// <summary>`team`: player | enemy | neutral, or an expression giving one (`self.team`).</summary>
        private static string TeamParam(LogicContext ctx, ActionParams p)
        {
            var raw = p.Raw("team");
            if (raw == null) throw new LogicError("convert: `team` is missing");
            string s = raw.Type == JTokenType.String ? (string)raw : null;
            if (s is "player" or "enemy" or "neutral") return s;
            Value v;
            try { v = p.Get("team"); } catch (LogicError) { v = Value.None; }
            if (v.Kind is ValueKind.Text && v.Str is "player" or "enemy" or "neutral") return v.Str;
            if (s != null)
            {
                v = SteerExpr.Expr(s)(ctx.With(it: p.Target));
                if (v.Kind == ValueKind.Text && v.Str is "player" or "enemy" or "neutral") return v.Str;
                if (v.Kind == ValueKind.Entity) return v.AsEntity.Team;
            }
            throw new LogicError($"convert: team {raw} isn't player, enemy or neutral");
        }

        private static void Replace(LogicContext ctx, ActionParams p)
        {
            var e = Target(p, "replace");
            string with = p.Template("with") ?? throw new LogicError("replace: `with` is missing");
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var raw = p.Raw("keep");
            if (raw is JArray a) foreach (var k in a) keep.Add((string)k);
            else if (raw?.Type == JTokenType.String) keep.Add((string)raw);
            foreach (var k in keep)
                if (k is not ("hp" or "vars" or "statuses" or "labels")) throw new LogicError($"replace keep \"{k}\": expected hp, vars, statuses or labels");
            var made = SteerTeams.Replace(ctx, e, with, keep);
            if (made != null && p.Actions("then") is { } then) ctx.With(spawned: made).Run(then);
        }
    }
}
