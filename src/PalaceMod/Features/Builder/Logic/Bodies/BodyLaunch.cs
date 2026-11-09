using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Launching a projectile entity: makes it (through the core's spawn path, so caps apply), applies the launch's
    projectile overrides and flies it from a point at a target entity or a point. Used by the `launch` action and by
    Builder attacks with `launch` (Steering).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyLaunch
    {
        /// <summary>Spawns an entity through the core (caps, ids, rules). Swapped to the core's spawn once it lands.</summary>
        public static System.Func<string, Vector3, float, string, LogicEntity, LogicContext, LogicEntity> SpawnVia = CoreSpawn;

        private static LogicEntity CoreSpawn(string template, Vector3 pos, float rotY, string team, LogicEntity owner, LogicContext ctx)
        {
            if (template != null && template.StartsWith("{")) return DirectSpawn(template, pos, rotY, team, owner, ctx);   // dev
            var world = LogicWorld.Current ?? throw new LogicError("no logic session");
            return world.Spawn(template, pos.ToVec(), rotY, team == "owner" ? owner?.Team : team, owner, ctx);
        }

        private static LogicEntity DirectSpawn(string template, Vector3 pos, float rotY, string team, LogicEntity owner, LogicContext ctx)
        {
            var world = LogicWorld.Current;
            if (world == null) return null;
            var f = EntityFactory.Instance;
            // Dev: an inline template as JSON.
            var def = template.StartsWith("{") ? JObject.Parse(template) : f.EntityDef(template);
            var e = new LogicEntity
            {
                Template = template.StartsWith("{") ? (string)def["id"] : template,
                Owner = owner,
                Team = team == "owner" ? owner?.Team : team,
                Def = def != null ? (JObject)def.DeepClone() : null,
            };
            return f.Build(e, pos.ToVec(), rotY, out string error) ? e : throw new LogicError(error);
        }

        public static (LogicEntity e, string error) Launch(LaunchRequest r)
        {
            var aim = r.Target != null ? r.Target.Position.ToVector3() : r.Toward ?? r.From + Vector3.forward;
            var flat = aim - r.From; flat.y = 0f;
            float yaw = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat).eulerAngles.y : 0f;
            LogicEntity e;
            try { e = SpawnVia(r.Template, r.From, yaw, r.Team, r.Owner, null); }
            catch (LogicError ex) { return (null, ex.Message); }
            if (e == null) return (null, "capped");
            var go = e.Go();
            var p = go != null ? go.GetComponent<BuilderProjectile>() : null;
            if (p == null)
            {
                // A template without a projectile component flies with the defaults (a tree, a boulder...).
                if (go == null) return (e, "not a body");
                p = go.AddComponent<BuilderProjectile>();
                p.Configure(e, null);
            }
            p.Apply(r.Overrides);
            p.Launch(r.From, r.Target, r.Target == null ? r.Toward : null);
            return (e, null);
        }

        /// <summary>Redirect a projectile entity (Builder or vanilla).</summary>
        public static bool Redirect(LogicEntity e, LogicEntity target, Vector3? point)
        {
            if (e?.Go()?.GetComponent<BuilderProjectile>() is { } p) { p.Redirect(target, point); return true; }
            return VanillaProjectiles.Redirect(e, target, point);
        }
    }
}
