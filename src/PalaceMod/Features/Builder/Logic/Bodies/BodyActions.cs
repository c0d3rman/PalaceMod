using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    The Make actions (docs/builder-engine.md, "Actions"): `spawn` and `launch`, plus the bodies' built-in inputs
    (movers: start/stop/reverse/goto/toggle and their poses; areas: show/hide; interactables: lock/unlock).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyActions
    {
        private static readonly string[] ProjectileFields = ["speed", "accel", "homing", "arc", "gravity", "spin", "face", "hits", "hitRadius", "pierce", "onGround", "range"];

        public static void Register()
        {
            LogicActions.Register("spawn", Spawn);
            LogicActions.Register("launch", Launch);
            LogicActions.Register("destroy", Destroy, ActionFlags.PerTarget);
            foreach (var name in BuilderMover.BuiltInInputs.Concat(["show", "hide", "lock", "unlock"])) RegisterInput(name);
            LogicActions.RegisterInput("enable", (e, ev) => { SetEnabled(e); return false; });
            LogicActions.RegisterInput("disable", (e, ev) => { SetEnabled(e); return false; });
            LogicActions.InputsOf = InputsOf;
        }

        private static readonly HashSet<string> registered = [];

        /// <summary>Registers an input name (pose names are registered as movers with them are built).</summary>
        public static void RegisterInput(string name)
        {
            if (!registered.Add(name)) return;
            LogicActions.RegisterInput(name, (e, ev) => Input(e, name, ev?.Value ?? 0));
        }

        /// <summary>enable/disable reached a body: its object follows (cut, collider, hittability, area, mover, look).</summary>
        private static void SetEnabled(LogicEntity e)
        {
            var go = e?.Go();
            if (go == null || go.GetComponent<BuilderBody>() == null) return;
            if (go.activeSelf != e.Enabled) go.SetActive(e.Enabled);
        }

        // spawn: entity, count, at, spread, team, elite, facing, for (SpawnLives), then
        private static void Spawn(LogicContext ctx, ActionParams p)
        {
            string template = p.Template("entity") ?? throw new LogicError("spawn: no entity");
            int count = Mathf.Clamp((int)p.Number("count", 1), 0, 100);
            var at = (p.Position("at") ?? ctx.Self?.Position ?? Vec.Zero).ToVector3();
            float spread = (float)p.Number("spread", count > 1 ? 1f : 0f);
            string team = p.Text("team");
            if (team == "owner") team = ctx.Self?.Team;
            float facing = (float)p.Number("facing", ctx.Self?.Body?.Facing ?? 0f);
            var then = p.Actions("then");
            var lasts = Until.From(p, ctx.World);
            for (int i = 0; i < count; i++)
            {
                var pos = at;
                if (spread > 0f)
                {
                    // Even ring for a group, a deterministic jitter from the logic random source.
                    double a = (i + (ctx.World?.Random.NextDouble() ?? 0.5)) * Mathf.PI * 2f / Mathf.Max(1, count);
                    pos += new Vector3(Mathf.Sin((float)a), 0f, Mathf.Cos((float)a)) * spread;
                }
                EntityFactory.PendingElite = p.Has("elite") && (p.Text("elite") == "inherit" ? ctx.Self?.Hp()?.Elite == true : p.Bool("elite"));
                EntityFactory.PendingNeutral = team == "neutral";
                LogicEntity e;
                try { e = BodyLaunch.SpawnVia(template, pos, facing, team, ctx.Self, ctx); }
                finally { EntityFactory.PendingElite = false; EntityFactory.PendingNeutral = false; }
                if (e == null) continue;
                SpawnLives.Mark(e, lasts);
                if (then != null && then.Count > 0) ctx.With(spawned: e).Run(then);
            }
        }

        // destroy: effects (true), drops (false), silent (no `death` event)
        private static void Destroy(LogicContext ctx, ActionParams p)
        {
            var e = p.Target;
            if (e == null || e.Destroyed || e.Kind == "level") return;
            bool effects = p.Bool("effects", true), drops = p.Bool("drops", false), silent = p.Bool("silent", false);
            // Its own death rules still run: the event is queued before the object goes (they see its last position).
            if (!silent) ctx.Raise("death", e, ctx.Self, 0, e.Position);
            var pos = e.Position;
            e.StaticPos = pos;
            LogicServices.Factory?.Despawn(e, effects, drops);
        }

        // launch: entity, from, toward, projectile overrides, onImpact, then
        private static void Launch(LogicContext ctx, ActionParams p)
        {
            string template = p.Template("entity") ?? throw new LogicError("launch: no entity");
            var self = ctx.Self;
            Vector3 from;
            var raw = p.Raw("from");
            var basePos = (self?.Position ?? Vec.Zero).ToVector3();
            if (raw is JArray a && a.Count == 3) from = basePos + (self?.Go() is { } sg ? sg.transform.rotation * new Vector3((float)a[0], (float)a[1], (float)a[2]) : new Vector3((float)a[0], (float)a[1], (float)a[2]));
            else from = (p.Position("from") ?? self?.Position ?? Vec.Zero).ToVector3() + (raw == null ? Vector3.up * 1f : Vector3.zero);
            LogicEntity target = null;
            Vector3? toward = null;
            var tv = p.Get("toward");
            if (tv.Kind == ValueKind.Entity) target = tv.AsEntity;
            else if (tv.Kind == ValueKind.Set && tv.AsSet.Count > 0) target = tv.AsSet[0];
            else if (tv.Kind == ValueKind.Position) toward = tv.Pos.ToVector3();
            else if (ctx.Other != null) target = ctx.Other;
            else throw new LogicError("launch: no target (toward)");
            var overrides = new JObject();
            foreach (var f in ProjectileFields)
                if (p.Has(f)) overrides[f] = p.Raw(f) is JValue v && v.Type == JTokenType.String && ((string)v).StartsWith("=") ? JToken.FromObject(p.Get(f).Num) : p.Raw(f);
            var (e, error) = BodyLaunch.Launch(new LaunchRequest
            {
                Template = template, Owner = self, From = from, Target = target, Toward = toward, Overrides = overrides,
                Team = p.Text("team") is { } t && t != "owner" ? t : self?.Team,
            });
            if (e == null) { if (error != "capped") throw new LogicError("launch: " + error); return; }
            var onImpact = p.Actions("onImpact");
            if (onImpact != null && onImpact.Count > 0 && e.Go()?.GetComponent<BuilderProjectile>() is { } proj)
            {
                var keep = ctx.With(spawned: e);
                proj.OnImpact = (hit, at) => BodyDefer.Run(onImpact, keep.With(other: hit));
            }
            var then = p.Actions("then");
            if (then != null && then.Count > 0) ctx.With(spawned: e).Run(then);
        }

        // ---- inputs ----------------------------------------------------------------------------------------------

        /// <summary>A built-in input reaching a body. True when the body handles it.</summary>
        public static bool Input(LogicEntity e, string input, double value)
        {
            var go = e?.Go();
            if (go == null) return false;
            if (go.GetComponent<BuilderMover>() is { } m && m.Input(input, value)) return true;
            if (go.GetComponent<BuilderArea>() is { } a && input is "show" or "hide")
            {
                a.SetShow(input == "show" ? (a.Show == "none" ? "outline" : a.Show) : "none");
                return true;
            }
            if (input is "lock" or "unlock" && go.GetComponentInChildren<BuilderInteractor>() is { } i) { i.Locked = input == "lock"; return true; }
            return false;
        }

        public static IEnumerable<string> InputsOf(LogicEntity e)
        {
            var go = e?.Go();
            if (go == null) yield break;
            if (go.GetComponent<BuilderMover>() is { } m) foreach (var s in m.Inputs) yield return s;
            if (go.GetComponent<BuilderArea>() != null) { yield return "show"; yield return "hide"; }
            if (go.GetComponentInChildren<BuilderInteractor>() != null) { yield return "lock"; yield return "unlock"; }
        }
    }

    /// <summary>Running an action list from a body callback (outside the core's drain).</summary>
    internal static class BodyDefer
    {
        /// <summary>Installed with the core's world.Defer once it's there; until then runs in place.</summary>
        public static System.Action<ActionList, LogicContext> Defer = (list, ctx) => (ctx.World ?? LogicWorld.Current)?.Defer(list, ctx);

        public static void Run(ActionList list, LogicContext ctx)
        {
            try { Defer(list, ctx); }
            catch (System.Exception e) { Log.For<LogicBodiesManager>().Warn($"onImpact failed: {e.Message}"); }
        }
    }
}
