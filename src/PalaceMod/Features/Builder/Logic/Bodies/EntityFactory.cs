using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Pathfinding;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Templates → game objects (docs/builder-engine.md, "Templates", "Components"). The core describes an entity
    (id, template, kind, team, owner, e.Def = the template JSON with the place's inline fields merged), checks caps,
    then calls Build; Build makes the object and registers it (LogicGame.Register), which compiles its rules and queues
    `spawn`.

    - Units (a pack `units[]` id, "vanilla:<id>"): the game's prefab or the Builder template, instantiated like the
      game's spawn-on-death (enemies go through Spawn.AdjustEnemyParametersAfterSpawn). A team other than the
      template's side goes through Teams.Convert (Steering).
    - Everything else: a root "PalaceBuilder <id>" with the visual on a child and the body/components on the root:
      body (footprint, solid NavmeshCut + collider, hittable Hp/TaggedObject, flying), projectile, mover, area,
      pickup, interact, lifetime.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class EntityFactory : IEntityFactory
    {
        public static readonly EntityFactory Instance = new();

        /// <summary>Set by `spawn` around a spawn: the unit is made elite (the game's elite multipliers and look).</summary>
        public static bool PendingElite;

        public const string Prefix = LayoutScene.AddedPrefix;

        // ---- templates ---------------------------------------------------------------------------------------------

        /// <summary>The session pack's templates as JSON, by id (units[] and entities[]).</summary>
        private JObject packJson;
        private DesignPack packJsonOf;
        private readonly Dictionary<string, JObject> units = new(StringComparer.Ordinal), entities = new(StringComparer.Ordinal);

        private void Refresh()
        {
            var pack = BuilderServices.Sessions.Current?.Pack;
            if (pack == packJsonOf && packJson != null) return;
            packJsonOf = pack;
            units.Clear(); entities.Clear();
            packJson = pack != null ? JObject.Parse(PackSerializer.WriteCompact(pack)) : new JObject();
            foreach (var u in packJson["units"] as JArray ?? []) if (u is JObject o && o["id"] != null) units[(string)o["id"]] = o;
            foreach (var u in packJson["entities"] as JArray ?? []) if (u is JObject o && o["id"] != null) entities[(string)o["id"]] = o;
        }

        public static PackEntry PackEntry => BuilderServices.Sessions.Current?.Pack is { } p ? BuilderServices.Packs.Find(p.Id) : null;

        public JObject UnitDef(string id)
        {
            if (id == null) return null;
            if (LogicWorld.Current?.Templates.TryGetValue(id, out var t) == true && (string)t["kind"] is null or "unit") return t;
            Refresh();
            return units.TryGetValue(id, out var o) ? o : null;
        }

        public JObject EntityDef(string id)
        {
            if (id == null) return null;
            if (LogicWorld.Current?.Templates.TryGetValue(id, out var t) == true && (string)t["kind"] is { } k && k != "unit") return t;
            Refresh();
            return entities.TryGetValue(id, out var o) ? o : null;
        }

        /// <summary>Set by `spawn` with an explicit "neutral" team (the core's default team is also "neutral").</summary>
        public static bool PendingNeutral;

        public bool IsUnit(string template) =>
            template != null && (template.StartsWith("vanilla:") || UnitDef(template) != null || (template.Contains("/") && ContentRef.Parse(template).Kind == RefKind.PackUnit));

        public bool HasTemplate(string template)
        {
            if (string.IsNullOrEmpty(template)) return false;
            if (EntityDef(template) != null || UnitDef(template) != null) return true;
            if (template.StartsWith("vanilla:")) return BuilderServices.Catalog.FindUnit(template.Substring(8)) != null;
            return false;
        }

        public string CapClass(string template)
        {
            if (IsUnit(template)) return "unit";
            var def = EntityDef(template);
            return def?["kind"]?.ToString() == "projectile" || def?["projectile"] is JObject ? "projectile" : "entity";
        }

        // ---- build -------------------------------------------------------------------------------------------------

        public bool Build(LogicEntity e, Vec pos, float rotY, out string error)
        {
            error = null;
            try
            {
                if (IsUnit(e.Template)) return BuildUnit(e, pos.ToVector3(), rotY, out error);
                return BuildBody(e, pos.ToVector3(), rotY, out error);
            }
            catch (LogicError ex) { error = ex.Message; return false; }
            catch (Exception ex)
            {
                Log.For<LogicBodiesManager>().Error($"Couldn't build {e.Id} ({e.Template})", ex);
                error = ex.Message;
                return false;
            }
        }

        private bool BuildUnit(LogicEntity e, Vector3 pos, float rotY, out string error)
        {
            var pack = BuilderServices.Sessions.Current?.Pack;
            var prefab = BuilderServices.Units.Resolve(pack, e.Template, out error);
            if (prefab == null) return false;
            pos = BodyNav.UnitGround(pos);
            var go = Object.Instantiate(prefab, pos, Quaternion.Euler(0f, rotY, 0f));
            go.name = go.name.Replace("(Clone)", "").Trim();
            bool enemySide = go.GetComponentInChildren<TaggedObject>()?.Tags.Contains(TagManager.ETag.EnemyOwned) == true;
            string side = enemySide ? "enemy" : "player";
            if (enemySide) Spawn.AdjustEnemyParametersAfterSpawn(go, PendingElite);
            e.Kind = "unit";
            e.Def ??= UnitDef(e.Template);
            // The core defaults a template without a team to "neutral": a unit then keeps its own side.
            string team = e.Team ?? side;
            if (team == "neutral" && e.Def?["team"] == null && !PendingNeutral) team = side;
            if (team != side)
            {
                var (converted, why) = Teams.Convert(e, go, team);
                if (converted == null) { Object.Destroy(go); error = why; return false; }
                if (converted != go) Object.Destroy(go);
                go = converted;
            }
            e.Team = team;
            LogicGame.Register(e, go);
            BodyEvents.RaiseUnitSpawned(e, go);
            BodyEvents.RaiseEntityBuilt(e, go);
            return true;
        }

        private bool BuildBody(LogicEntity e, Vector3 pos, float rotY, out string error)
        {
            error = null;
            var def = e.Def ?? EntityDef(e.Template) ?? new JObject();
            e.Def = def;
            e.Kind ??= (string)def["kind"] ?? (def["projectile"] is JObject ? "projectile" : def["area"] is JObject ? "area" : def["pickup"] is JObject ? "pickup" : "prop");
            e.Team ??= (string)def["team"] is { } t && t != "owner" ? t : e.Owner?.Team ?? "neutral";

            var go = new GameObject(Prefix + e.Kind + " " + (e.Id ?? e.Template));
            go.SetActive(false);
            if (SceneManager.GetActiveScene().IsValid()) SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, rotY, 0f));
            float scale = def.Value<float?>("scale") ?? 1f;
            go.transform.localScale = Vector3.one * scale;

            string note = BodyVisual.Build(go, def["visual"] as JObject ?? DefaultInteractVisual(def), PackEntry);
            if (note != null) Log.For<LogicBodiesManager>().Warn($"{e.Template ?? e.Id}: {note}");

            var body = def["body"] as JObject;
            var state = go.AddComponent<BuilderBody>();
            state.Entity = e;
            state.Setup(body, e.Team, def.Value<bool?>("countsAsEnemy") ?? false);

            if (def["projectile"] is JObject proj) go.AddComponent<BuilderProjectile>().Configure(e, proj);
            if (def["mover"] is JObject mover) go.AddComponent<BuilderMover>().Configure(e, mover);
            if (def["area"] is JObject area) go.AddComponent<BuilderArea>().Configure(e, area);
            if (def["pickup"] is JObject pickup) go.AddComponent<BuilderPickup>().Configure(e, pickup);
            if (def["interact"] is JObject interact) BodyInteract.Add(go, e, interact);
            if (def["lifetime"] is JToken life && life.Type is JTokenType.Integer or JTokenType.Float)
                go.AddComponent<BuilderLifetime>().Seconds = (float)life;

            if (state.Walkable) Walkables.Add(state, go.GetComponent<BuilderMover>());
            e.Ext = state;
            go.SetActive(e.Enabled);
            LogicGame.Register(e, go);
            BodyEvents.RaiseEntityBuilt(e, go);
            return true;
        }

        /// <summary>
        /// The look of something the king has to find and use (an `interact`) that has no `visual` of its own, such as an
        /// interactable logic object: a plain post (the lever preset's), so it's never an invisible spot on the ground.
        /// An area drawn with `show` is already visible and keeps no post.
        /// </summary>
        private static JObject DefaultInteractVisual(JObject def)
        {
            if (def["interact"] is not JObject) return null;
            if (def["area"] is JObject area && (string)area["show"] is "outline" or "fill") return null;
            return new JObject { ["mesh"] = "mesh:Graveyard Wall Post" };
        }

        /// <summary>An attach (slot, prop, line, castle) got its entity: add the components its attach JSON carries.</summary>
        public void Attach(LogicEntity e)
        {
            var go = e.Go();
            if (go == null || e.Def == null) return;
            if (e.Def["area"] is JObject area) go.AddComponent<BuilderArea>().Configure(e, area);
            if (e.Def["interact"] is JObject interact) BodyInteract.Add(go, e, interact);
            if (e.Def["mover"] is JObject mover) go.AddComponent<BuilderMover>().Configure(e, mover);
            if (e.Def["pickup"] is JObject pickup) go.AddComponent<BuilderPickup>().Configure(e, pickup);
            BodyEvents.RaiseEntityBuilt(e, go);
        }

        // ---- despawn -----------------------------------------------------------------------------------------------

        public void Despawn(LogicEntity e, bool effects, bool drops)
        {
            if (VanillaProjectiles.Destroy(e)) { LogicWorld.Current?.Forget(e); return; }
            var go = e.Go();
            if (go == null) { LogicWorld.Current?.Forget(e); return; }
            Drifts.Forget(e);
            var hp = go.GetComponentInChildren<Hp>();
            if (hp != null && e.Kind == "unit")
            {
                if (!drops) hp.coinCount = 0;
                else if (hp.coin != null)
                    for (int i = 0; i < hp.coinCount; i++)
                        Object.Instantiate(hp.coin, go.transform.position + Vector3.up * hp.coinSpawnOffset + Quaternion.Euler(0f, 45f * i, 0f) * Vector3.forward * (i > 0 ? 2f : 0f), Quaternion.identity);
                if (effects && hp.fxToSpawnOnDeath != null)
                    Object.Instantiate(hp.fxToSpawnOnDeath, go.transform.position + hp.deathFxSpawnOffset, hp.fxToSpawnOnDeath.transform.rotation);
            }
            if (go.GetComponent<BuilderBody>() is { } b) b.Despawning = true;
            // The player's units are knocked out instead of dying (and revive at dawn). A unit the logic spawned is
            // removed outright; one the game made (a barracks troop) is knocked out for good: no revive at dawn.
            // Buildings, the castle and the king are only knocked out.
            if (hp != null && hp.getsKnockedOutInsteadOfDying)
            {
                bool unit = e.Kind == "unit";
                if (unit && e.Spawned) { Object.Destroy(go); LogicWorld.Current?.Forget(e); return; }
                if (hp.HpValue > 0f || !hp.KnockedOut)
                {
                    bool inv = hp.invulnerable;
                    hp.invulnerable = false;
                    hp.TakeDamage(hp.HpValue + 1f);
                    if (hp != null) hp.invulnerable = inv;
                }
                if (unit && hp.TaggedObj != null && !hp.TaggedObj.Tags.Contains(TagManager.ETag.AUTO_NoReviveNextMorning))
                    hp.TaggedObj.AddTag(TagManager.ETag.AUTO_NoReviveNextMorning);
                return;
            }
            Object.Destroy(go);
            LogicWorld.Current?.Forget(e);
        }
    }

    /// <summary>The root of every non-unit Builder body: footprint, solid cut and collider, hittability.</summary>
    internal sealed class BuilderBody : MonoBehaviour
    {
        public LogicEntity Entity;
        public float Radius = 0.5f, Height = 1f;
        public Vector2? Box;
        public bool Solid, Walkable, Hittable, Flying;
        public bool Despawning;
        public string[] GameTags = [];
        public NavmeshCut Cut;
        private Collider blocker;

        public void Setup(JObject body, string team, bool countsAsEnemy)
        {
            if (body != null)
            {
                Radius = body.Value<float?>("radius") ?? Radius;
                Height = body.Value<float?>("height") ?? Height;
                if (body["box"] is JArray box && box.Count == 2) Box = new Vector2((float)box[0], (float)box[1]);
                Solid = body.Value<bool?>("solid") ?? false;
                Walkable = body.Value<bool?>("walkable") ?? false;
                Hittable = body.Value<bool?>("hittable") ?? false;
                Flying = body.Value<bool?>("flying") ?? false;
                if (body["tags"] is JArray extra) GameTags = extra.Select(x => (string)x).Where(x => x != null).ToArray();
            }
            if (Solid) MakeSolid();
            if (Hittable) MakeHittable(body?.Value<float?>("hp") ?? 100f, team, countsAsEnemy);
        }

        private void MakeSolid()
        {
            // The ground graphs only: fliers cross solid bodies.
            Cut = gameObject.AddComponent<NavmeshCut>();
            Cut.graphMask = BodyNav.GroundMask();
            Cut.height = Mathf.Max(Height, 1f);
            Cut.center = new Vector3(0f, Cut.height / 2f - 0.5f, 0f);
            Cut.useRotationAndScale = true;
            Cut.updateDistance = 0.25f;
            Cut.updateRotationDistance = 5f;
            if (Box is Vector2 b) { Cut.type = NavmeshCut.MeshType.Rectangle; Cut.rectangleSize = b; }
            else { Cut.type = NavmeshCut.MeshType.Circle; Cut.circleRadius = Radius; Cut.circleResolution = 8; }
            // The king and coins collide with it too.
            var holder = new GameObject(LayoutScene.BlockerName) { layer = LayoutScene.ObstacleLayer };
            holder.transform.SetParent(transform, false);
            if (Box is Vector2 bb)
            {
                var c = holder.AddComponent<BoxCollider>();
                c.size = new Vector3(bb.x, Height, bb.y);
                c.center = new Vector3(0f, Height / 2f, 0f);
                blocker = c;
            }
            else
            {
                var c = holder.AddComponent<CapsuleCollider>();
                c.radius = Radius;
                c.height = Mathf.Max(Height, Radius * 2f);
                c.center = new Vector3(0f, c.height / 2f, 0f);
                blocker = c;
            }
        }

        private void MakeHittable(float hp, string team, bool countsAsEnemy)
        {
            var tagged = gameObject.AddComponent<TaggedObject>();
            var tags = tagged.Tags;
            tags.Clear();
            if (team == "player") tags.Add(TagManager.ETag.PlayerOwned);
            else if (team == "enemy")
            {
                // The game's towers, units and king attack EnemyOwned things; only countsAsEnemy ones hold the night
                // open (LogicLinksManager.EnemyCount leaves the others out of the game's enemy count).
                tags.Add(TagManager.ETag.EnemyOwned);
                if (!countsAsEnemy) EnemyBodies.Uncounted.Add(tagged);
            }
            if (Flying) tags.Add(TagManager.ETag.Flying);
            // body.tags: extra game tags, so the game's AI treats it as one of its own kinds ("Building", "Tower",
            // "PlayerUnit"): vanilla enemies only pick targets by tags.
            foreach (var t in GameTags) if (LogicTags.TryParse(t, out var tag) && !tags.Contains(tag)) tags.Add(tag);
            var h = gameObject.AddComponent<Hp>();
            h.maxHp = hp;
            h.hitFeedbackHeight = Height / 2f;
            h.getsKnockedOutInsteadOfDying = false;
            // A big body is measured from its outline, like the game's large buildings.
            if (Radius > 1.5f || Box != null)
            {
                var col = gameObject.AddComponent<CapsuleCollider>();
                col.isTrigger = true;
                col.radius = Box is Vector2 b ? Mathf.Max(b.x, b.y) / 2f : Radius;
                col.height = Height;
                col.center = new Vector3(0f, Height / 2f, 0f);
                tagged.colliderForBigOjectsToMeasureDistance = col;
            }
        }

        /// <summary>enable/disable: the cut, the collider and hittability follow the object's active state.</summary>
        private void OnDestroy() { if (Walkable) Walkables.Remove(this); }

        private void OnEnable() { if (Cut != null) { Cut.enabled = true; Cut.ForceUpdate(); } if (blocker != null) blocker.enabled = true; if (Walkable) Walkables.SetEnabled(this); }
        private void OnDisable() { if (Cut != null) Cut.enabled = false; if (blocker != null) blocker.enabled = false; if (Walkable) Walkables.SetEnabled(this); }
    }

    /// <summary>`lifetime`: the `expire` event, then the body is destroyed.</summary>
    internal sealed class BuilderLifetime : MonoBehaviour
    {
        public float Seconds;
        private float age;
        private bool expired;

        private void Update()
        {
            if (expired) return;
            age += Time.deltaTime * BodyTime.Scale(GetComponent<BuilderEntity>()?.Entity);
            if (age < Seconds) return;
            expired = true;
            var e = GetComponent<BuilderEntity>()?.Entity;
            BodyEnd.Expire(e, gameObject);
        }
    }

    internal static class BodyEnd
    {
        /// <summary>Raise `expire`, then destroy after the event has been dispatched (its rules still see the body).</summary>
        public static void Expire(LogicEntity e, GameObject go)
        {
            if (e != null) BodyDev.Raise("expire", e, null, 0, go.transform.position.ToVec());
            Retire(go);
        }

        /// <summary>Stop drawing and acting, and destroy once this frame's events have run (rules may read its position).</summary>
        public static void Retire(GameObject go)
        {
            if (go == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            if (go.GetComponent<NavmeshCut>() is { } cut) cut.enabled = false;
            Object.Destroy(go, 0.25f);
        }
    }

    /// <summary>Game-time scale for a body (`timeScale` stat; 1 when stats aren't there).</summary>
    internal static class BodyTime
    {
        public static float Scale(LogicEntity e)
        {
            if (e == null || LogicServices.Stats == null) return 1f;
            double s = LogicServices.Stats.Get(e, "timeScale");
            return double.IsNaN(s) ? 1f : Mathf.Max(0f, (float)s);
        }
    }
}
