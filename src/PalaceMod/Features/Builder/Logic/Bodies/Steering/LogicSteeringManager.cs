using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using PalaceMod.Builder.Logic.Bodies.Steering;
using UnityEngine;

/*
    Steering & attacks (docs/builder-engine.md, workstream L2): `targeting`, `movement` and `attacks` components, the
    Move and Team actions, and drift on units. Infrastructure: acts only in a logic session (LogicWorld.Current);
    every patch returns to the game's own code at once outside one, or for objects without an override.

    State is per GameObject (SteerState) and per LogicWorld. Builder units get their components from their template
    JSON on BodyEvents.UnitSpawned; vanilla units and towers get overrides from actions (setTargeting, setMovement,
    taunt, moveTo...).
*/
namespace PalaceMod
{
    public partial class LogicSteeringManager : BaseManager
    {
        private static LogicWorld owner;
        private static readonly Dictionary<int, SteerState> states = [];
        private static readonly HashSet<int> suppressed = [];          // AutoAttacks replaced by Builder attacks
        internal static readonly Dictionary<LogicEntity, (Vector3 pos, Quaternion rot)> Deaths = [];
        private static float pollTimer;
        internal static readonly SteerTargeting TargetingService = new();

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LogicServices.Targeting = TargetingService;
            BodyEvents.UnitSpawned += OnUnitSpawned;
            SteerActions.Register();
            LogicSaveState.Register("steering", SteerSave.Capture, SteerSave.Restore);
            DevCommands.Register("logic-steer", "logic-steer apply <role> <json> | state <role> | units   steering (L2): apply a unit template's targeting/movement/attacks to live units, inspect", SteerDev.Run);
        }

        // ------------------------------------------------------------------ registry

        /// <summary>True when a session is running; drops the previous session's state on a new world.</summary>
        internal static bool Live()
        {
            var w = LogicWorld.Current;
            if (w == null) return false;
            if (w != owner)
            {
                owner = w;
                states.Clear();
                suppressed.Clear();
                Deaths.Clear();
                SteerLog.Reset();
            }
            return true;
        }

        internal static SteerState Get(GameObject go)
        {
            if (go == null || states.Count == 0 || !Live()) return null;
            return states.TryGetValue(go.GetInstanceID(), out var st) ? st : null;
        }

        internal static SteerState Get(Component c) => c == null ? null : Get(c.gameObject);

        /// <summary>The state for an entity's object, made on first use (null for an entity without a game object).</summary>
        internal static SteerState For(LogicEntity e, GameObject go = null)
        {
            if (!Live()) return null;
            go ??= e.Go();
            if (go == null) return null;
            int key = go.GetInstanceID();
            if (states.TryGetValue(key, out var st) && st.Go == go) return st;
            st = new SteerState
            {
                Entity = e ?? LogicGame.EntityOf(go),
                Go = go,
                Enemy = go.GetComponent<PathfindMovementEnemy>(),
                Player = go.GetComponent<PathfindMovementPlayerunit>(),
            };
            states[key] = st;
            return st;
        }

        internal static void Forget(GameObject go)
        {
            if (go != null) states.Remove(go.GetInstanceID());
        }

        internal static IEnumerable<SteerState> States => states.Values;

        internal static void Suppress(AutoAttack a) { if (a != null) suppressed.Add(a.GetInstanceID()); }

        // ------------------------------------------------------------------ templates

        /// <summary>Apply a template's `targeting`, `movement` and `attacks` to a freshly spawned unit.</summary>
        internal static void OnUnitSpawned(LogicEntity e, GameObject go)
        {
            if (e?.Def == null || go == null || !Live()) return;
            ApplyComponents(e, go, e.Def);
        }

        internal static void ApplyComponents(LogicEntity e, GameObject go, JObject def)
        {
            var targeting = def["targeting"];
            var movement = def["movement"] as JObject;
            var attacks = def["attacks"] as JArray;
            if (targeting == null && movement == null && attacks == null) return;
            var st = For(e, go);
            if (st == null) return;
            string id = e.Template ?? e.Id;
            try
            {
                if (targeting != null && targeting.Type != JTokenType.Null)
                {
                    st.Targeting ??= new TargetingState();
                    st.Targeting.Base = TargetRule.ParseList(targeting);
                }
            }
            catch (LogicError ex) { SteerLog.Once($"{id} targeting", $"{id}.targeting: {ex.Message}"); }
            if (movement != null)
            {
                st.MovementBase = movement;
                st.MovementChanged();
                _ = st.Move;
                SteerMovement.ApplyBase(st);
            }
            if (attacks != null) BuilderAttack.AddAll(e, go, attacks, id);
        }

        /// <summary>Remove an entity without a death event, effects or drops (path end `then: destroy`, replace).</summary>
        internal static void DestroySilently(LogicEntity e, GameObject go)
        {
            go ??= e?.Go();
            if (e != null && LogicServices.Factory != null && e.Spawned)
            {
                try { LogicServices.Factory.Despawn(e, effects: false, drops: false); return; }
                catch (Exception ex) { Log.For<LogicSteeringManager>().Warn($"despawn {e.Id}: {ex.Message}"); }
            }
            if (go == null) return;
            Forget(go);
            if (e != null) LogicWorld.Current?.Forget(e);
            var be = go.GetComponent<BuilderEntity>();
            if (be != null) be.Entity = null;
            UnityEngine.Object.Destroy(go);
        }

        // ------------------------------------------------------------------ per frame

        public override void Update()
        {
            if (!Live()) return;
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            if (Drifts.Any) ApplyDrifts(dt);
            pollTimer += dt;
            if (pollTimer < 0.1f) return;
            pollTimer = 0;
            double now = LogicWorld.Current.Time;
            List<int> dead = null;
            foreach (var kv in states)
            {
                var st = kv.Value;
                if (st.Go == null) { (dead ??= []).Add(kv.Key); continue; }
                if (st.HasMovement && (st.Enemy != null || st.Player != null)) SteerMovement.Poll(st, now);
            }
            if (dead != null) foreach (var k in dead) states.Remove(k);
        }

        private static readonly List<LogicEntity> drifting = [];

        /// <summary>Drifts move units (flyers included, not buildings); Bodies moves projectiles.</summary>
        private static void ApplyDrifts(float dt)
        {
            drifting.Clear();
            drifting.AddRange(Drifts.Drifting);
            foreach (var e in drifting)
            {
                if (e == null || e.Kind is not ("unit" or "king")) continue;
                var go = e.Go();
                if (go == null || !e.Alive) continue;
                var v = Drifts.Velocity(e);
                if (v.sqrMagnitude < 1e-6f) continue;
                if (StatusFlags.Has(go, "rooted")) continue;
                var step = new Vector3(v.x, 0, v.z) * dt;
                var pm = go.GetComponent<PathfindMovement>();
                if (pm is PathfindMovementEnemy en) go.transform.position = SnapNear(go.transform.position + step, en.GetNearestGroundPosition(go.transform.position + step), en);
                else if (pm is PathfindMovementPlayerunit pu) go.transform.position = SnapNear(go.transform.position + step, pu.GetNearestGroundPosition(go.transform.position + step), pu);
                else if (go.GetComponent<CharacterController>() is { } cc && cc.enabled) cc.Move(step);
                else go.transform.position += step;
            }
        }

        private static Vector3 SnapNear(Vector3 want, Vector3 nav, Component c)
        {
            bool flyer = c.GetComponent<TaggedObject>()?.Tags.Contains(TagManager.ETag.Flying) ?? false;
            return flyer ? new Vector3(nav.x, want.y, nav.z) : nav;
        }

        // ------------------------------------------------------------------ patches: attack targets

        private static readonly List<TaggedObject> picks = [];

        internal static float AttackRange(AutoAttack a)
        {
            float r = 0;
            foreach (var p in a.targetPriorities) if (p != null && p.range > r) r = p.range;
            return r;
        }

        [HarmonyPatch]
        private static class AutoAttackTarget
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(AutoAttack), nameof(AutoAttack.FindAutoAttackTarget), Type.EmptyTypes);
                foreach (var t in typeof(AutoAttack).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(AutoAttack))))
                    if (AccessTools.DeclaredMethod(t, nameof(AutoAttack.FindAutoAttackTarget), Type.EmptyTypes) is { } m) yield return m;
            }

            private static bool Prefix(AutoAttack __instance, ref TaggedObject __result)
            {
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st?.Targeting == null) return true;
                if (!TargetChooser.Choose(st, __instance.transform.position, AttackRange(__instance), 0, 1, picks)) return true;
                __result = picks.Count > 0 ? picks[0] : null;
                return false;
            }
        }

        [HarmonyPatch(typeof(AutoAttackTower), nameof(AutoAttackTower.FindAutoAttackTargets))]
        private static class TowerTargets
        {
            private static readonly AccessTools.FieldRef<AutoAttackTower, List<TaggedObject>> targets = AccessTools.FieldRefAccess<AutoAttackTower, List<TaggedObject>>("attackTargets");

            private static bool Prefix(AutoAttackTower __instance)
            {
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st?.Targeting == null) return true;
                if (!TargetChooser.Choose(st, __instance.transform.position, AttackRange(__instance), 0, Mathf.Max(1, __instance.simultaneousProjectiles), picks)) return true;
                var list = targets(__instance);
                list.Clear();
                list.AddRange(picks);
                return false;
            }
        }

        [HarmonyPatch]
        private static class MultiTargets
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(AutoAttackTwoTargets), "FindAutoAttackTargets");
                yield return AccessTools.Method(typeof(AutoAttackLowestInRangeMulti), "FindAutoAttackTargets");
            }

            private static bool Prefix(AutoAttack __instance, TaggedObject[] outAttacks)
            {
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st?.Targeting == null) return true;
                if (!TargetChooser.Choose(st, __instance.transform.position, AttackRange(__instance), 0, outAttacks.Length, picks)) return true;
                for (int i = 0; i < outAttacks.Length; i++) outAttacks[i] = i < picks.Count ? picks[i] : null;
                return false;
            }
        }

        /// <summary>A unit's vanilla attacks stay off while a Builder attack with `replace` stands in for them.</summary>
        [HarmonyPatch]
        private static class ReplacedAttacks
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(AutoAttack), nameof(AutoAttack.Update));
                foreach (var t in typeof(AutoAttack).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(AutoAttack))))
                    if (AccessTools.DeclaredMethod(t, nameof(AutoAttack.Update), Type.EmptyTypes) is { } m) yield return m;
            }

            private static bool Prefix(AutoAttack __instance) => suppressed.Count == 0 || !suppressed.Contains(__instance.GetInstanceID());
        }

        // ------------------------------------------------------------------ patches: movement

        [HarmonyPatch(typeof(PathfindMovementEnemy), "FindMoveToTarget")]
        private static class EnemyDestination
        {
            private static bool Prefix(PathfindMovementEnemy __instance, ref Vector3 __result)
            {
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st == null) return true;
                st.Overriding = false;
                if (!SteerMovement.Destination(st, false, out var dest, out var target, out bool home)) return true;
                st.Overriding = true;
                SteerMovement.E_walkingHome(__instance) = home;
                SteerMovement.E_seekObj(__instance) = target;
                SteerMovement.E_prio(__instance) = null;
                // live-track a moving target (the game does this for the king only)
                SteerMovement.E_chasing(__instance) = target != null && (target.GetComponent<PathfindMovement>() != null || target.Tags.Contains(TagManager.ETag.Player));
                __result = dest;
                return false;
            }

            private static void Postfix(PathfindMovementEnemy __instance, ref Vector3 __result)
            {
                if (states.Count == 0) return;
                var st = Get(__instance);
                if (st == null || st.Overriding || !SteerMovement.E_walkingHome(__instance)) return;
                if (SteerMovement.Wander(st, __instance.transform.position, LogicWorld.Current.Time, out var w)) __result = w;
            }
        }

        [HarmonyPatch(typeof(PathfindMovementPlayerunit), "FindMoveToTarget")]
        private static class PlayerDestination
        {
            private static bool Prefix(PathfindMovementPlayerunit __instance, ref Vector3 __result)
            {
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st == null) return true;
                st.Overriding = false;
                if (__instance.FollowingPlayer && st.MoveTo == null) return true;
                if (!SteerMovement.Destination(st, true, out var dest, out _, out bool home)) return true;
                st.Overriding = true;
                SteerMovement.P_walkingHome(__instance) = home;
                SteerMovement.P_seekObj(__instance) = null;
                SteerMovement.P_prio(__instance) = null;
                __result = dest;
                return false;
            }

            private static void Postfix(PathfindMovementPlayerunit __instance, ref Vector3 __result)
            {
                if (states.Count == 0) return;
                var st = Get(__instance);
                if (st == null || st.Overriding || __instance.FollowingPlayer || !__instance.HasReachedHomePositionAlready || !SteerMovement.P_walkingHome(__instance)) return;
                if (SteerMovement.Wander(st, __instance.transform.position, LogicWorld.Current.Time, out var w)) __result = w;
            }
        }

        [HarmonyPatch(typeof(PathfindMovementPlayerunit), nameof(PathfindMovementPlayerunit.FindTargetPosition))]
        private static class PlayerTarget
        {
            private static bool Prefix(PathfindMovementPlayerunit __instance, ref (Vector3, bool) __result)
            {
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st?.Targeting == null) return true;
                var pos = __instance.transform.position;
                // holding position: only what comes within the game's small hold radius of home
                float range = __instance.HoldPosition ? 7f + SteerMovement.Flat(pos, __instance.HomePosition) : float.PositiveInfinity;
                if (!TargetChooser.Choose(st, pos, range, 0, 1, picks)) return true;
                if (picks.Count == 0) { SteerMovement.P_seekObj(__instance) = null; __result = (Vector3.negativeInfinity, false); return false; }
                SteerMovement.P_seekObj(__instance) = picks[0];
                __result = (SteerMovement.ClosestPoint(picks[0], pos), true);
                return false;
            }
        }

        [HarmonyPatch]
        private static class MovementUpdate
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PathfindMovementEnemy), "Update");
                yield return AccessTools.Method(typeof(PathfindMovementPlayerunit), "Update");
            }

            private static bool Prefix(PathfindMovement __instance, out float __state)
            {
                __state = -1;
                if (LogicWorld.Current == null) return true;
                var go = __instance.gameObject;
                if (StatusFlags.Has(go, "rooted") || StatusFlags.Has(go, "stunned")) return false;
                if (states.Count == 0) return true;
                var st = Get(__instance);
                if (st == null) return true;
                SteerMovement.ApplyBase(st);
                var en = st.Enemy;
                var pu = st.Player;

                // a target the game live-tracks may have been destroyed: pick again instead of reading it
                if (en != null && SteerMovement.E_chasing(en) && SteerMovement.E_seekObj(en) is { } so && !so)
                {
                    SteerMovement.E_chasing(en) = false;
                    SteerMovement.E_seekObj(en) = null;
                    SteerMovement.E_repath(en) = 0;
                }
                // reached a moveTo point or path waypoint: pick the next one now, not at the game's next repath
                var pos = go.transform.position;
                if (st.MoveTo != null && SteerMovement.Flat(pos, st.MoveTo.Point) <= 1.5f) SteerMovement.Repath(st);
                else if (st.PathPoints != null && !st.PathDone && st.PathIndex < st.PathPoints.Count && SteerMovement.Flat(pos, st.PathPoints[st.PathIndex]) <= 1.5f) SteerMovement.Repath(st);

                float baseSpeed = en != null ? en.movementSpeed : pu.movementSpeed;
                float speed = st.MoveTo?.Speed ?? baseSpeed;
                if (st.Move.Mode == "direct")
                {
                    if (en != null && en.IsSlowed) speed *= SteerMovement.E_speedSlowed(en);
                    var rvo = en != null ? en.RVO : SteerMovement.P_rvo(pu);
                    float keep = en != null ? en.keepDistanceOf : pu.keepDistanceOf;
                    if (en != null && SteerMovement.E_slowedFor(en) > 0) SteerMovement.E_slowedFor(en) -= Time.deltaTime;
                    SteerMovement.DirectUpdate(st, speed, keep, rvo, Time.deltaTime);
                    return false;
                }
                if (st.MoveTo?.Speed is float s)
                {
                    __state = baseSpeed;
                    if (en != null) en.movementSpeed = s; else pu.movementSpeed = s;
                }
                return true;
            }

            private static void Postfix(PathfindMovement __instance, float __state)
            {
                if (__state < 0) return;
                if (__instance is PathfindMovementEnemy en) en.movementSpeed = __state;
                else if (__instance is PathfindMovementPlayerunit pu) pu.movementSpeed = __state;
            }
        }

        /// <summary>Where units die (a dead target can still be converted after its object is gone).</summary>
        [HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
        private static class RecordDeaths
        {
            private static void Prefix(Hp __instance, out bool __state) => __state = LogicWorld.Current != null && __instance.HpValue > 0;

            private static void Postfix(Hp __instance, bool __state)
            {
                if (!__state || __instance.HpValue > 0 || __instance.GetComponent<PathfindMovement>() == null) return;
                var e = LogicGame.EntityOf(__instance.gameObject);
                if (e == null) return;
                if (Deaths.Count > 512) Deaths.Clear();
                Deaths[e] = (__instance.transform.position, __instance.transform.rotation);
            }
        }
    }
}
