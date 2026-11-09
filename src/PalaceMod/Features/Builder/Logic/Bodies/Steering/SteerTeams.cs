using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Assets;
using PalaceMod.Builder.Model;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    Changing sides (docs/builder-engine.md, "Converting a team"). A unit's side is more than a tag (movement
    component, attack priorities, materials), so a unit going to the player or enemy side is rebuilt as its
    counterpart: the template's `counterpart` if it names one, else the catalog unit of the other side with the same
    role (flying, melee or ranged; the plainest such unit). With keepLook the old body model rides on the new unit
    (AssetVisuals' "another unit's model"). Going neutral keeps the unit's own brain and drops its team tags, so
    vanilla AI ignores it; steering gives it an empty targeting list (it attacks nothing until rules say so).

    The LogicEntity survives a conversion (same id, name, vars, labels, rules): only its game object changes.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal static class SteerTeams
    {
        // the plainest unit of each side and role, tried in order before any other catalog match
        private static readonly Dictionary<string, string[]> Preferred = new()
        {
            ["player/melee"] = ["P Melee"],
            ["player/ranged"] = ["P Archer"],
            ["player/flying"] = ["P Firewing"],
            ["enemy/melee"] = ["E Melee"],
            ["enemy/ranged"] = ["E Archer"],
            ["enemy/flying"] = ["E Flyer"],
        };

        public static string Role(GameObject go)
        {
            var t = go != null ? go.GetComponent<TaggedObject>() : null;
            if (t == null) return "melee";
            if (t.Tags.Contains(TagManager.ETag.Flying)) return "flying";
            if (t.Tags.Contains(TagManager.ETag.RangedFighter)) return "ranged";
            return "melee";
        }

        /// <summary>The catalog unit of <paramref name="side"/> with the same role.</summary>
        public static CatalogUnit Counterpart(string side, string role)
        {
            var units = BuilderServices.Catalog?.Units?.Where(u => u.Side == side && u.Prefab != null).ToList();
            if (units == null || units.Count == 0) return null;
            bool Fits(CatalogUnit u) => role switch
            {
                "flying" => u.Flying,
                "ranged" => !u.Flying && u.Tags.Contains("RangedFighter"),
                _ => !u.Flying && u.Tags.Contains("MeeleFighter"),
            };
            if (Preferred.TryGetValue(side + "/" + role, out var names))
                foreach (var n in names)
                    if (units.FirstOrDefault(u => u.Id == n && Fits(u)) is { } hit) return hit;
            // the plainest: player troops trained by a building, then the lowest difficulty / hp
            return units.Where(Fits)
                .OrderBy(u => side == UnitSide.Player && u.Buildings.Count == 0 ? 1 : 0)
                .ThenBy(u => u.Large ? 1 : 0)
                .ThenBy(u => u.Difficulty > 0 ? u.Difficulty : 999)
                .ThenBy(u => u.MaxHp)
                .ThenBy(u => u.Id, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? units.OrderBy(u => u.MaxHp).First();
        }

        /// <summary>The vanilla model id a unit's look comes from ("E Melee"), for keepLook.</summary>
        public static string LookOf(LogicEntity e, GameObject go)
        {
            var mesh = (string)e?.Def?["visuals"]?["mesh"];
            if (mesh != null) return mesh;
            string baseRef = (string)e?.Def?["base"] ?? e?.Template;
            if (baseRef != null && baseRef.StartsWith("vanilla:")) return baseRef;
            if (go != null) return "vanilla:" + go.name.Replace("(Clone)", "").Trim();
            return null;
        }

        /// <summary>
        /// Make the object for <paramref name="e"/> on <paramref name="team"/>: a new counterpart object (the old one
        /// isn't touched), or <paramref name="go"/> itself for neutral. Error text when it can't.
        /// Also the body of Teams.Convert (the factory's hook for spawning a template on the other side).
        /// </summary>
        public static (GameObject go, string error) Convert(LogicEntity e, GameObject go, string team, bool keepLook = true, Vector3? at = null, Quaternion? rot = null)
        {
            if (team is not ("player" or "enemy" or "neutral")) return (null, $"team \"{team}\": expected player, enemy or neutral");
            if (team == "neutral")
            {
                if (go == null) return (null, "a dead unit can't be raised as neutral (no body to keep)");
                var t = go.GetComponent<TaggedObject>();
                if (t != null) { t.RemoveTag(TagManager.ETag.PlayerOwned); t.RemoveTag(TagManager.ETag.EnemyOwned); t.RemoveTag(TagManager.ETag.PlayerUnit); }
                return (go, null);
            }

            Vector3 pos = at ?? (go != null ? go.transform.position : e.Position.ToVector3());
            Quaternion r = rot ?? (go != null ? go.transform.rotation : Quaternion.identity);
            string look = keepLook ? LookOf(e, go) : null;

            GameObject prefab = null;
            string counterpart = (string)e?.Def?["counterpart"];
            if (counterpart != null)
            {
                var cr = ContentRef.Parse(counterpart);
                prefab = cr.Kind == RefKind.Vanilla ? BuilderServices.Catalog?.FindUnit(cr.Id)?.Prefab : null;
                if (prefab == null) return (null, $"counterpart \"{counterpart}\": only vanilla units can be counterparts until the factory is on main");
            }
            else
            {
                string side = team == "player" ? UnitSide.Player : UnitSide.Enemy;
                var cu = Counterpart(side, go != null ? Role(go) : RoleOfTemplate(e));
                if (cu == null) return (null, $"no {side} unit to convert into");
                prefab = cu.Prefab;
            }

            var made = Object.Instantiate(prefab, pos, r);
            if (team == "enemy") Spawn.AdjustEnemyParametersAfterSpawn(made, false);
            if (look != null && look != "vanilla:" + prefab.name)
            {
                var notes = new List<string>();
                try { AssetVisuals.Apply(made, null, new VisualsDef { Mesh = look }, notes, owned); }
                catch (Exception ex) { notes.Add(ex.Message); }
                if (notes.Count > 0) SteerLog.Once("look " + look, $"convert keepLook {look}: {string.Join("; ", notes)}");
            }
            return (made, null);
        }

        private static readonly List<Object> owned = [];

        private static string RoleOfTemplate(LogicEntity e)
        {
            string id = e?.Template != null && e.Template.StartsWith("vanilla:") ? e.Template.Substring(8) : (string)e?.Def?["base"];
            if (id != null && id.StartsWith("vanilla:")) id = id.Substring(8);
            var u = id != null ? BuilderServices.Catalog?.FindUnit(id) : null;
            return u == null ? "melee" : u.Flying ? "flying" : u.Tags.Contains("RangedFighter") ? "ranged" : "melee";
        }

        /// <summary>
        /// The `convert` action on one entity. A live unit keeps its LogicEntity and gets the new object; a dead one
        /// is raised where it died. hpPct: the new hp fraction (default: the old one, or full for the dead).
        /// </summary>
        public static void ConvertEntity(LogicEntity e, string team, bool keepLook, double? hpPct)
        {
            var world = LogicWorld.Current;
            var go = e.Go();
            bool alive = go != null && e.Alive;
            if (e.Kind is "building" or "castle" or "king") throw new LogicError($"convert: {e.Id} is a {e.Kind}; only units change sides");
            if (alive && e.Team == team) return;
            Vector3? at = null; Quaternion? rot = null;
            if (!alive)
            {
                if (LogicSteeringManager.Deaths.TryGetValue(e, out var d)) { at = d.pos; rot = d.rot; }
                else if (go == null) throw new LogicError($"convert: {e.Id} is gone and where it died isn't known");
                go = null;
            }
            float frac = (float)(hpPct ?? (alive && e.Body != null && e.Body.MaxHp > 0 ? e.Body.Hp / e.Body.MaxHp : 1));
            var (made, error) = Convert(e, alive ? go : null, team, keepLook, at, rot);
            if (made == null) throw new LogicError("convert: " + error);
            var oldSteer = alive ? LogicSteeringManager.Get(go) : null;

            if (alive && made != go)
            {
                // swap the entity onto the new object, then drop the old one without a death
                var be = go.GetComponent<BuilderEntity>();
                if (be != null) be.Entity = null;
                LogicSteeringManager.Forget(go);
                Object.Destroy(go);
            }
            if (made != e.Go() || !alive)
            {
                e.Game = made;
                e.Body = new GameBody(made);
                e.Destroyed = false;
                if (e.Spawned || made.GetComponent<BuilderEntity>() != null || e.Def != null)
                {
                    var nb = made.GetComponent<BuilderEntity>() ?? made.AddComponent<BuilderEntity>();
                    nb.Entity = e;
                }
                LogicGameWorld.Index(world, made, e);
                if (world.Find(e.Id) == null) world.Add(e, vanilla: true);   // re-index only: its rules and vars are still on it
            }
            e.Team = team;
            var hp = made.GetComponent<Hp>();
            if (hp != null) { hp.HpPercentage = Mathf.Clamp(frac, 0.01f, 1f); }
            LogicSteeringManager.Deaths.Remove(e);

            // steering follows the entity: the template's components, then any runtime override it had
            if (e.Def != null) LogicSteeringManager.ApplyComponents(e, made, e.Def);
            if (oldSteer != null && (oldSteer.Targeting != null || oldSteer.MovementOverlays.Count > 0))
            {
                var ns = LogicSteeringManager.For(e, made);
                ns.Targeting ??= oldSteer.Targeting;
                ns.MovementOverlays.AddRange(oldSteer.MovementOverlays);
                ns.MovementChanged();
            }
            if (team == "neutral")
            {
                var ns = LogicSteeringManager.For(e, made);
                ns.Targeting ??= new TargetingState { Base = [] };
            }
        }

        /// <summary>The `replace` action: swap the entity for another template in place.</summary>
        public static LogicEntity Replace(LogicContext ctx, LogicEntity e, string with, HashSet<string> keep)
        {
            var world = LogicWorld.Current;
            var go = e.Go();
            if (go == null || !e.Exists) throw new LogicError($"replace: {e.Id} is gone");
            var pos = go.transform.position;
            float rotY = go.transform.eulerAngles.y;
            var made = SteerSpawn.Spawn(ctx, with, pos, rotY, e.Team, e.Owner);
            if (made == null) return null;
            if (keep.Contains("hp") && e.Body != null && e.Body.MaxHp > 0 && made.Hp() is { } nh) nh.HpPercentage = Mathf.Clamp(e.Body.Hp / e.Body.MaxHp, 0.01f, 1f);
            if (keep.Contains("vars")) foreach (var kv in e.Vars) made.Vars[kv.Key] = kv.Value;
            if (keep.Contains("labels")) foreach (var l in e.Labels) made.Labels.Add(l);
            if (keep.Contains("statuses") && LogicServices.Stats != null)
            {
                try { LogicServices.Stats.Restore(made, LogicServices.Stats.Capture(e)); }
                catch (Exception ex) { SteerLog.Once("replace statuses", $"replace: statuses not carried over: {ex.Message}"); }
            }
            // the old one's runtime steering overrides carry over too
            var old = LogicSteeringManager.Get(go);
            if (old?.Targeting != null && made.Go() is { } mg) { var ns = LogicSteeringManager.For(made, mg); ns.Targeting ??= old.Targeting; }
            LogicSteeringManager.DestroySilently(e, go);
            return made;
        }
    }

    /// <summary>Spawning a template for convert/replace: the session's factory when installed, else vanilla prefabs.</summary>
    internal static class SteerSpawn
    {
        public static LogicEntity Spawn(LogicContext ctx, string template, Vector3 pos, float rotY, string team, LogicEntity owner)
        {
            if (template != null && template.StartsWith("@@")) template = template.Substring(2);
            if (LogicServices.Factory != null && LogicServices.Factory.HasTemplate(template))
                return LogicWorld.Current.Spawn(template, pos.ToVec(), rotY, team, owner, ctx);   // null when capped (traced)
            var r = ContentRef.Parse(template ?? "");
            var cu = r.Kind == RefKind.Vanilla ? BuilderServices.Catalog?.FindUnit(r.Id) : null;
            if (cu?.Prefab == null) throw new LogicError($"no template \"{template}\" (Builder templates need the entity factory)");
            var go = Object.Instantiate(cu.Prefab, pos, Quaternion.Euler(0, rotY, 0));
            if (cu.Side == UnitSide.Enemy) global::Spawn.AdjustEnemyParametersAfterSpawn(go, false);
            var made = LogicGame.EntityOf(go);
            made.Owner = owner;
            if (team != null && team != made.Team)
            {
                SteerTeams.ConvertEntity(made, team, keepLook: true, null);
            }
            return made;
        }
    }
}
