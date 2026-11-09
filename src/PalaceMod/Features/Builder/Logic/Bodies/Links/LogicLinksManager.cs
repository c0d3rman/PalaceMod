using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    Entity links on the game side (docs/builder-engine.md, "Links"; the relation itself is Logic/Runtime/Links.cs):
    - every frame (LateUpdate, after the game's movement) each part that follows its parent is placed at its offset
      in the parent's frame (Heading.Yaw) and, for `follow: rigid`, turned with it; a part whose parent died follows
      its onParentDeath. `Links.Moved` fires after, for anything that carries riders on moving bodies.
    - a linked unit that follows its parent doesn't walk (its movement Update is skipped); it still attacks.
    - damage shares (damageToParent / damageToGroup / damageKept) inside the game's own damage call, so the parent's
      and the group's hits are credited to the real attacker.
    - a template's `parts` are spawned and linked when the entity appears.
    - `interact` on units (templates, places, spawned and wave units): the same interactor Builder bodies get.
    - enemy-team hittable Builder bodies are real enemies to the game's towers, units and king (EnemyOwned), but
      only `countsAsEnemy` ones hold the night open.
    Infrastructure: acts only while a logic world runs.
*/
namespace PalaceMod
{
    public class LogicLinksManager : BaseManager
    {
        private static readonly HashSet<LogicEntity> partsDone = [];
        private static readonly List<LogicEntity> partsQueue = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LinkServices.Kill = KillPart;
            LinkServices.Released = Released;
            LinkServices.Relative = Relative;
            BodyEvents.EntityBuilt += Built;
            BodyEvents.UnitSpawned += Built;
            BodyEvents.UnitSpawned += UnitInteract;
            LogicWorld.Ending += _ => { partsDone.Clear(); partsQueue.Clear(); Links.Carried.Clear(); EnemyBodies.Uncounted.Clear(); };
            var driver = new GameObject("PalaceMod LinkDriver");
            Object.DontDestroyOnLoad(driver);
            driver.AddComponent<LinkDriver>();
            DevCommands.Register("logic-links", "logic-links [filter]   linked parts: parent, offset, follow, distance from their slot", args => Links.Status(args.FirstOrDefault()));
        }

        // ---- parts and unit interact ---------------------------------------------------------------------------

        private static void Built(LogicEntity e, GameObject go)
        {
            if (e?.Def?["parts"] is not JArray { Count: > 0 } || !partsDone.Add(e)) return;
            partsQueue.Add(e);
        }

        private static void UnitInteract(LogicEntity e, GameObject go)
        {
            if (go == null || e?.Def?["interact"] is not JObject interact) return;
            if (go.GetComponentInChildren<BuilderInteractor>(true) != null) return;
            BodyInteract.Add(go, e, interact);
        }

        /// <summary>Spawn and link a template's parts (after its own `spawn` was queued, so its vars exist).</summary>
        internal static void BuildParts()
        {
            if (partsQueue.Count == 0) return;
            var w = LogicWorld.Current;
            var list = partsQueue.ToArray();
            partsQueue.Clear();
            if (w == null) return;
            foreach (var parent in list)
            {
                if (!parent.Exists || parent.Def?["parts"] is not JArray parts) continue;
                int depth = 0;
                for (var p = parent; p?.Link != null; p = p.Parent) depth++;
                if (depth >= 4) { w.TraceAdd(parent.Id, null, null, "parts", TraceOutcome.Capped, "parts nest deeper than 4; skipped", 0, null); continue; }
                for (int i = 0; i < parts.Count && i < 32; i++)
                {
                    if (parts[i] is not JObject def || (string)def["entity"] is not { } template) continue;
                    try
                    {
                        var spec = LinkSpec.FromJson(def);
                        spec.FromParts = true;
                        var rot = Heading.Rotation(parent);
                        var at = parent.Position.ToVector3() + rot * spec.Offset.ToVector3();
                        string team = (string)def["team"] is { } t && t != "owner" ? t : parent.Team;
                        var part = w.Spawn(template, at.ToVec(), Heading.Yaw(parent) + spec.RotY, team, parent);
                        if (part == null) continue;
                        if ((string)def["name"] is { Length: > 0 } label) part.Labels.Add(label);
                        w.LinkTo(part, parent, spec);
                        Links.Place(part);
                    }
                    catch (LogicError ex) { w.TraceAdd(parent.Id, null, $"{parent.Template}.parts[{i}]", "parts", TraceOutcome.Error, ex.Message, 0, null); }
                }
            }
        }

        // ---- services ------------------------------------------------------------------------------------------

        private static void KillPart(LogicEntity e, LogicEntity killer)
        {
            var go = e?.Go();
            if (go == null) { if (e != null && !e.Destroyed) LogicWorld.Current?.Forget(e); return; }
            var hp = go.GetComponent<Hp>() ?? go.GetComponentInChildren<Hp>();
            if (hp != null)
            {
                if (hp.HpValue <= 0) return;
                bool inv = hp.invulnerable;
                hp.invulnerable = false;
                partKilling = true;
                try { hp.TakeDamage(hp.HpValue + hp.maxHp + 1f, killer?.Tagged(), false, true); }
                finally { partKilling = false; if (hp != null) hp.invulnerable = inv; }
                return;
            }
            LogicServices.Factory?.Despawn(e, true, false);
        }

        private static void Released(LogicEntity e, string how)
        {
            Links.Carried.Remove(e);
            var go = e?.Go();
            // A part dropped while its level unloads (the parent's object went first) has nowhere to land.
            if (go == null || how != LinkSpec.Drop || !go.activeInHierarchy || AstarPath.active == null) return;
            var p = go.transform.position;
            var pm = go.GetComponent<PathfindMovement>();
            try
            {
                go.transform.position = pm is PathfindMovementEnemy en ? en.GetNearestGroundPosition(p)
                    : pm is PathfindMovementPlayerunit pu ? pu.GetNearestGroundPosition(p)
                    : BodyNav.UnitGround(p);
            }
            catch (NullReferenceException)
            {
                // A unit whose movement hasn't started has no nearest-node constraint yet: the plain graph query.
                go.transform.position = BodyNav.UnitGround(p);
            }
            if (go.GetComponent<BuilderBody>() != null) go.transform.rotation = Quaternion.Euler(0f, go.transform.eulerAngles.y, 0f);
        }

        private static (Vec, float) Relative(LogicEntity child, LogicEntity parent)
        {
            var rot = Heading.Rotation(parent);
            var d = child.Position.ToVector3() - parent.Position.ToVector3();
            return ((Quaternion.Inverse(rot) * d).ToVec(), Mathf.DeltaAngle(Heading.Yaw(parent), Heading.Yaw(child)));
        }

        // ---- patches -------------------------------------------------------------------------------------------

        /// <summary>A unit carried by its parent doesn't walk (it's placed every frame); it still attacks.</summary>
        [HarmonyPatch]
        private static class CarriedUnits
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PathfindMovementEnemy), "Update");
                yield return AccessTools.Method(typeof(PathfindMovementPlayerunit), "Update");
            }

            private static bool Prefix(Component __instance) => Links.Carried.Count == 0 || !Links.CarriedGo.Contains(__instance.gameObject.GetInstanceID());
        }

        private sealed class ShareState
        {
            public LogicEntity Parent;
            public double ToParent, Each;
            public List<LogicEntity> Others;
            public TaggedObject From;
            public bool ByPlayer;
        }

        private static int sharing, forwarding;
        private static bool partKilling;

        /// <summary>damageToParent / damageToGroup / damageKept, after the damage modifiers (Stats' prefix runs first).</summary>
        [HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
        private static class ShareDamage
        {
            [HarmonyPriority(Priority.VeryHigh)]
            private static bool Prefix(Hp __instance, ref float _amount, TaggedObject _damageComingFrom, bool causedByPlayer, ref bool __result, bool __runOriginal, out ShareState __state)
            {
                __state = null;
                if (!__runOriginal) return false;
                var w = LogicWorld.Current;
                if (w == null || w.Linked.Count == 0 || sharing > 0 || partKilling || LogicStatsManager.killing || _amount <= 0 || __instance.HpValue <= 0 || __instance.invulnerable) return true;
                var e = LogicGame.EntityOf(__instance.gameObject);
                if (e == null || (e.Link == null ? e.Children.Count == 0 : !e.Link.SharesDamage)) return true;
                var (kept, toParent, each, others) = LogicWorld.ShareDamage(e, _amount);
                if (toParent <= 0 && others == null && Math.Abs(kept - _amount) < 1e-6) return true;
                __state = new ShareState { Parent = e.Parent, ToParent = toParent, Each = each, Others = others, From = _damageComingFrom, ByPlayer = causedByPlayer };
                if (kept <= 0) { __result = false; return false; }
                _amount = (float)kept;
                return true;
            }

            private static void Postfix(ShareState __state)
            {
                if (__state == null) return;
                if (__state.Others != null && __state.Each > 0)
                {
                    sharing++;
                    LogicStatsManager.rawHits++;
                    try
                    {
                        foreach (var o in __state.Others)
                            if (o.Alive && !o.Flags.Contains("invulnerable") && o.Hp() is { } hp && hp.HpValue > 0) hp.TakeDamage((float)__state.Each, __state.From, __state.ByPlayer, true);
                    }
                    finally { sharing--; LogicStatsManager.rawHits--; }
                }
                if (__state.ToParent > 0 && __state.Parent != null && __state.Parent.Alive && forwarding < 8 && __state.Parent.Hp() is { } php && php.HpValue > 0)
                {
                    forwarding++;
                    LogicStatsManager.rawHits++;
                    try { if (!__state.Parent.Flags.Contains("invulnerable")) php.TakeDamage((float)__state.ToParent, __state.From, __state.ByPlayer, true); }
                    finally { forwarding--; LogicStatsManager.rawHits--; }
                }
            }
        }

        /// <summary>Enemy-team hittable bodies carry EnemyOwned (so towers, units and the king fight them); the
        /// night-end count leaves out the ones that don't count as enemies.</summary>
        [HarmonyPatch]
        private static class EnemyCount
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(TagManager), nameof(TagManager.CountAllTaggedObjectsWithTag), [typeof(TagManager.ETag)]);
                yield return AccessTools.Method(typeof(TagManager), nameof(TagManager.CountAllTaggedObjectsWithTag), [typeof(TagManager.ETag), typeof(TagManager.ETag), typeof(TagManager.ETag)]);
                yield return AccessTools.Method(typeof(TagManager), nameof(TagManager.CountObjectsWithTag));
            }

            private static void Postfix(object[] __args, ref int __result)
            {
                if (EnemyBodies.Uncounted.Count == 0 || __args.Length == 0 || __args[0] is not TagManager.ETag tag || tag != TagManager.ETag.EnemyOwned) return;
                __result = Math.Max(0, __result - EnemyBodies.Live());
            }
        }
    }
}

namespace PalaceMod.Builder.Logic.Bodies
{
    /// <summary>Enemy-team hittable Builder bodies that don't hold the night open (BuilderBody tags them EnemyOwned).</summary>
    internal static class EnemyBodies
    {
        public static readonly HashSet<TaggedObject> Uncounted = [];

        public static int Live()
        {
            Uncounted.RemoveWhere(t => t == null);
            int n = 0;
            foreach (var t in Uncounted) if (t.isActiveAndEnabled && t.Tags.Contains(TagManager.ETag.EnemyOwned)) n++;
            return n;
        }
    }

    /// <summary>Placing parts on their parents each frame.</summary>
    public static class Links
    {
        /// <summary>Fired every frame after every following part has been placed (riders on moving bodies go after).</summary>
        public static event Action Moved;

        internal static readonly HashSet<LogicEntity> Carried = [];
        internal static readonly HashSet<int> CarriedGo = [];

        /// <summary>Where a part belongs now: its parent's position plus its offset turned by the parent's heading.</summary>
        public static Vector3 Slot(LogicEntity part, out float yaw)
        {
            var s = part.Link;
            float py = Heading.Yaw(part.Parent);
            yaw = py + s.RotY;
            return part.Parent.Position.ToVector3() + Quaternion.Euler(0f, py, 0f) * s.Offset.ToVector3();
        }

        internal static void Place(LogicEntity part)
        {
            var go = part.Go();
            if (go == null || part.Link == null || !part.Link.Moves || part.Parent == null) return;
            var at = Slot(part, out float yaw);
            go.transform.position = at;
            if (part.Link.Follow == LinkSpec.Rigid && part.Kind != "unit") go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        internal static void Tick()
        {
            var w = LogicWorld.Current;
            if (w == null) { Carried.Clear(); CarriedGo.Clear(); return; }
            LogicLinksManager_BuildParts();
            Carried.Clear();
            CarriedGo.Clear();
            if (w.Linked.Count > 0)
            {
                foreach (var part in w.Linked.ToArray())
                {
                    var parent = part.Parent;
                    if (parent == null) continue;
                    if (!part.Exists) continue;
                    if (!parent.Alive) { w.ParentDied(parent); continue; }
                    if (part.Link == null || !part.Link.Moves) continue;
                    Place(part);
                    if (part.Kind == "unit" && part.Go() is { } go) { Carried.Add(part); CarriedGo.Add(go.GetInstanceID()); }
                }
            }
            try { Moved?.Invoke(); }
            catch (Exception ex) { Log.For<LogicLinksManager>().Error("Links.Moved handler failed", ex); }
        }

        private static void LogicLinksManager_BuildParts() => LogicLinksManager.BuildParts();

        internal static string Status(string filter)
        {
            var w = LogicWorld.Current;
            if (w == null) return "no logic world";
            var parts = w.Linked.Where(e => filter == null || LogicExpr.Matches(filter, w.Level, e)).ToList();
            var lines = parts.Take(40).Select(e =>
            {
                string slot = "";
                if (e.Link?.Moves == true && e.Parent != null) slot = $" off-slot {Vector3.Distance(Slot(e, out _), e.Position.ToVector3()):0.##} m";
                string gap = e.Parent != null ? $" {Vec.FlatDistance(e.Position, e.Parent.Position):0.##} m from parent at {e.Parent.Position}" : "";
                return $"{e.Id} -> {e.Parent?.Id} offset {e.Link?.Offset} follow {e.Link?.Follow} death {e.Link?.OnParentDeath} dmg parent {e.Link?.ToParent} group {e.Link?.ToGroup} kept {e.Link?.Kept} at {e.Position}{gap}{slot}";
            });
            return $"{parts.Count} linked parts\n" + string.Join("\n", lines);
        }
    }

    [DefaultExecutionOrder(-50)]
    internal sealed class LinkDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            try { Links.Tick(); }
            catch (Exception ex) { Log.For<LogicLinksManager>().Error("Links tick failed", ex); }
        }
    }
}
