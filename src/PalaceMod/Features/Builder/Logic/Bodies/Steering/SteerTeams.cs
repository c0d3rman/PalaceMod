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
    component, attack and movement priorities, the weapon's friend-or-foe tags and splash layers, materials), so a
    unit going to the player or enemy side is rebuilt as its counterpart: the template's `counterpart` if it names
    one, else the catalog unit of the other side with the same role (flying, melee or ranged; the plainest such
    unit). The counterpart is only the brain: with keepLook the old body model rides on the new unit (AssetVisuals'
    "another unit's model"), and without a named counterpart its numbers come along too (CarryStats: max hp, move
    speed, keep distance, aggro time, and per attack its cooldown, range and damage per hit, as a damage multiplier
    on the counterpart's weapon). Going neutral keeps the unit's own brain and drops its team tags, so vanilla AI
    ignores it; steering gives it an empty targeting list (it attacks nothing until rules say so).

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
            // Whose numbers the new body gets: the live unit, or (raised from the dead) its template.
            GameObject statsFrom = counterpart != null ? null : go != null ? go : TemplatePrefab(e);
            if (counterpart != null)
            {
                string why = "the unit factory isn't ready";
                prefab = BuilderServices.Units?.Resolve(BuilderServices.Sessions?.Current?.Pack, counterpart, out why);
                if (prefab == null) return (null, $"counterpart \"{counterpart}\": {why}");
                bool enemyPrefab = prefab.GetComponentInChildren<TaggedObject>(true)?.Tags.Contains(TagManager.ETag.EnemyOwned) == true;
                if (enemyPrefab != (team == "enemy")) return (null, $"counterpart \"{counterpart}\" is a {(enemyPrefab ? "enemy" : "player")} unit, not {team}");
            }
            else
            {
                string side = team == "player" ? UnitSide.Player : UnitSide.Enemy;
                var cu = Counterpart(side, go != null ? Role(go) : RoleOfTemplate(e));
                if (cu == null) return (null, $"no {side} unit to convert into");
                prefab = cu.Prefab;
            }

            var made = Object.Instantiate(prefab, pos, r);
            made.name = made.name.Replace("(Clone)", "").Trim();
            if (!made.activeSelf) made.SetActive(true);   // Builder templates are kept inactive
            if (team == "enemy") Spawn.AdjustEnemyParametersAfterSpawn(made, false);
            if (statsFrom != null)
            {
                string carried = CarryStats(statsFrom, made);
                Log.For<LogicSteeringManager>().Debug($"convert {statsFrom.name} -> {prefab.name} brain, carried: {carried}");
            }
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

        /// <summary>The prefab a unit entity was made from (its Builder template or vanilla unit), for a dead one's stats.</summary>
        private static GameObject TemplatePrefab(LogicEntity e)
        {
            if (e?.Template == null) return null;
            try { return BuilderServices.Units?.Resolve(BuilderServices.Sessions?.Current?.Pack, e.Template, out _); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Gives <paramref name="to"/> (a counterpart just made) the numbers of <paramref name="from"/>: max hp, move speed,
        /// keep distance, aggro time, and per attack (paired in order; extra ones pair with the first) its cooldown,
        /// range (every target priority scaled so the longest is the old range) and damage per hit (the damage
        /// multiplier that makes the new weapon's base hit equal the old one's). The base values are read without
        /// Builder stat modifiers (StatWriter.BaseOf), so a status the unit carries applies once, on top. Returns a
        /// summary for the log.
        /// </summary>
        internal static string CarryStats(GameObject from, GameObject to)
        {
            var src = StatWriter.Of(from);
            var dst = StatWriter.Of(to);
            if (src == null || dst == null) return "nothing to carry";
            var notes = new List<string>();
            double maxHp = StatWriter.BaseOf(src, "maxHp");
            if (!double.IsNaN(maxHp) && dst.Hp != null && maxHp > 0) { dst.Hp.maxHp = (float)maxHp; notes.Add($"maxHp {maxHp:0}"); }
            double speed = StatWriter.BaseOf(src, "moveSpeed");
            if (!double.IsNaN(speed))
            {
                if (dst.EnemyMove != null) dst.EnemyMove.movementSpeed = (float)speed;
                if (dst.UnitMove != null) dst.UnitMove.movementSpeed = (float)speed;
                notes.Add($"speed {speed:0.##}");
            }
            double keep = StatWriter.BaseOf(src, "keepDistance");
            if (!double.IsNaN(keep))
            {
                if (dst.EnemyMove != null) dst.EnemyMove.keepDistanceOf = (float)keep;
                if (dst.UnitMove != null) dst.UnitMove.keepDistanceOf = (float)keep;
            }
            double aggro = StatWriter.BaseOf(src, "aggroTime");
            if (!double.IsNaN(aggro))
            {
                if (dst.EnemyMove != null) dst.EnemyMove.agroTimeWhenAttackedByPlayer = (float)aggro;
                if (dst.UnitMove != null) dst.UnitMove.agroTimeWhenAttackedByPlayer = (float)aggro;
            }
            if (src.Attacks.Length > 0)
                for (int i = 0; i < dst.Attacks.Length; i++)
                {
                    var a = src.Attacks[Math.Min(i, src.Attacks.Length - 1)];
                    var b = dst.Attacks[i];
                    var one = StatWriter.Of(a.gameObject);
                    one.Attacks = [a];
                    double cd = StatWriter.BaseOf(one, "attackCooldown");
                    if (!double.IsNaN(cd) && cd > 0) b.cooldownDuration = (float)cd;
                    double range = StatWriter.BaseOf(one, "attackRange");
                    float have = 0f;
                    foreach (var tp in b.targetPriorities) if (tp != null) have = Mathf.Max(have, tp.range);
                    if (!double.IsNaN(range) && range > 0 && have > 0)
                        foreach (var tp in b.targetPriorities) if (tp != null) tp.range *= (float)(range / have);
                    float hitFrom = BaseHit(a.weapon) * a.DamageMultiplyer, hitTo = BaseHit(b.weapon);
                    if (hitFrom > 0f && hitTo > 0f) b.DamageMultiplyer = hitFrom / hitTo;
                    if (i == 0) notes.Add($"attack cooldown {cd:0.##} s, range {range:0.#} m, hit {hitFrom:0.#}");
                }
            return string.Join(", ", notes);
        }

        /// <summary>A weapon's damage per hit against an ordinary foe: its direct damage (else its splash) modifiers that
        /// need no tags beyond the target's side, applied as the game does ((n + added) x mult, in order).</summary>
        internal static float BaseHit(Weapon w)
        {
            if (w == null) return 0f;
            var mods = w.directDamage != null && w.directDamage.Count > 0 ? w.directDamage : w.splashDamage;
            if (mods == null) return 0f;
            float n = 0f;
            foreach (var m in mods)
            {
                if (m == null) continue;
                if (m.requiredTags != null && m.requiredTags.Any(t => t is not (TagManager.ETag.PlayerOwned or TagManager.ETag.EnemyOwned))) continue;
                n = (n + m.damageAdded) * m.damageMultiplyer;
            }
            return n;
        }

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
            if (e.Kind is not "unit") return;   // only units change sides; the king, buildings and bodies are skipped
            if (alive && e.Team == team) return;
            Vector3? at = null; Quaternion? rot = null;
            if (!alive)
            {
                if (LogicSteeringManager.Deaths.TryGetValue(e, out var d)) { at = d.pos; rot = d.rot; }
                else if (go == null) return;   // gone, and where it died isn't known: skipped
                if (go != null)
                {
                    // the corpse is destroyed at the end of this frame: it must not take the entity with it
                    at ??= go.transform.position; rot ??= go.transform.rotation;
                    var ob = go.GetComponent<BuilderEntity>();
                    if (ob != null) ob.Entity = null;
                    LogicSteeringManager.Forget(go);
                }
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
                if (e.Spawned || made.GetComponent<BuilderEntity>() != null || e.Def != null)
                {
                    var nb = made.GetComponent<BuilderEntity>() ?? made.AddComponent<BuilderEntity>();
                    nb.Entity = e;
                }
                LogicGameWorld.Index(world, made, e);
                // a dead unit was forgotten: bring the same entity back (id, @name, its rule instances, vars, labels)
                if (!alive || world.Find(e.Id) != e) world.Revive(e, raiseSpawn: false);
            }
            e.Team = team;
            var hp = made.GetComponent<Hp>();
            if (hp != null) LogicSteeringManager.SetHpFraction(hp, frac);
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
            if (keep.Contains("hp") && e.Body != null && e.Body.MaxHp > 0 && made.Hp() is { } nh) LogicSteeringManager.SetHpFraction(nh, e.Body.Hp / e.Body.MaxHp);
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
            // `@name` references follow the shape: the new entity takes the old one's name
            if (!string.IsNullOrEmpty(e.Name)) world.Rename(made, e.Name);
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
            {
                // keep the side asked for, neutral included (as `spawn` with an explicit team does)
                EntityFactory.PendingNeutral = team == "neutral";
                try { return LogicWorld.Current.Spawn(template, pos.ToVec(), rotY, team, owner, ctx); }   // null when capped (traced)
                finally { EntityFactory.PendingNeutral = false; }
            }
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
