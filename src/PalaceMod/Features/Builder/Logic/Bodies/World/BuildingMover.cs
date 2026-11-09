using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic.Bodies.Steering;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    `teleport` on a building or the castle (docs/builder-engine.md, "Moving buildings"): the whole build slot moves —
    its building, colliders, navmesh cuts, interactor, coin spawner, its child slots (the castle's add-ons) — and the
    world follows: the map's merged meshes give the slot's parts back (MeshFusion), the ground graphs are updated where
    it was and where it is now, units the new footprint would trap are pushed out, enemies repath (they target the
    castle's TaggedObject, which moved with it), and the castle's static centre (CastleCenter.CastleCenterPosition:
    where late spawns gather) follows. The king revives where he fell, so nothing about him needs to move.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BuildingMover
    {
        private static readonly Log log = Log.For<LogicWorldVerbsManager>();

        /// <summary>Moves the build slot <paramref name="go"/> belongs to (or <paramref name="go"/> itself) so its
        /// origin stands on the ground at <paramref name="at"/> (x, z; y from the ground). Returns false when there
        /// is nothing to move.</summary>
        public static bool Move(GameObject go, Vector3 at)
        {
            if (go == null) return false;
            var slot = go.GetComponentInParent<BuildSlot>();
            // A slot an activator upgrades (a wall segment, a castle add-on) moves with its owner, never alone.
            while (slot != null && slot.ActivatorUpgradesThis && slot.transform.parent?.GetComponentInParent<BuildSlot>() is { } owner) slot = owner;
            var root = slot != null ? slot.transform : go.transform;
            var ground = Level.LayoutScene.Ground(new Vector3(at.x, root.position.y, at.z));
            var delta = ground - root.position;
            if (delta.sqrMagnitude < 1e-6f) return true;

            var before = Footprint(root.gameObject);
            Uncombine(root.gameObject, moving: true);
            root.position += delta;
            if (slot != null) moved.Add(slot);
            Physics.SyncTransforms();
            var after = Footprint(root.gameObject);

            var center = root.GetComponentInChildren<CastleCenter>(true);
            bool castle = center != null;
            if (castle) CastleCenter.CastleCenterPosition = center.transform.position;
            GameRules.Moved(root.gameObject, delta);

            bool cuts = root.GetComponentsInChildren<Pathfinding.NavmeshCut>(true).Length > 0 || root.GetComponentsInChildren<Collider>(true).Any(c => c.enabled && !c.isTrigger);
            if (cuts && !GameRules.IsHidden(root.gameObject))
            {
                Level.GroundGraphs.UpdateArea(before, after);
                BodyNav.PushOut(after);
            }
            foreach (var en in Object.FindObjectsOfType<PathfindMovementEnemy>())
                if (en != null) SteerMovement.E_repath(en) = 0;
            log.Info($"moved {root.name} by ({delta.x:0.#}, {delta.z:0.#}) to ({root.position.x:0.#}, {root.position.y:0.#}, {root.position.z:0.#})" +
                     (castle ? " (the castle)" : ""));
            return true;
        }

        // ------------------------------------------------------------------ savestates (the level reload puts slots back)

        private static readonly HashSet<BuildSlot> moved = [];

        public static JToken Capture()
        {
            moved.RemoveWhere(s => s == null);
            if (moved.Count == 0) return null;
            var a = new JArray();
            foreach (var s in moved)
            {
                var p = s.transform.position;
                a.Add(new JObject { ["path"] = SaveStates.PathOf(s), ["x"] = p.x, ["z"] = p.z });
            }
            return a;
        }

        public static void Restore(JToken state)
        {
            moved.Clear();
            if (state is not JArray a || a.Count == 0) return;
            var slots = SaveStates.FindByPath<BuildSlot>();
            foreach (var o in a.OfType<JObject>())
                if ((string)o["path"] is { } path && slots.TryGetValue(path, out var s))
                    Move(s.gameObject, new Vector3((float?)o["x"] ?? 0, 0, (float?)o["z"] ?? 0));
        }

        private static Bounds Footprint(GameObject root)
        {
            var b = new Bounds(root.transform.position, Vector3.one * 2f);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r is MeshRenderer or SkinnedMeshRenderer) b.Encapsulate(r.bounds);
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (c.enabled && !c.isTrigger) b.Encapsulate(c.bounds);
            b.Expand(new Vector3(1f, 0f, 1f));
            return b;
        }

        /// <summary>The map's static meshes are drawn merged (MeshFusion): give the slot's parts their own renderers
        /// back, so they move with it.</summary>
        internal static void Uncombine(GameObject root, bool moving = false)
        {
            foreach (var src in root.GetComponentsInChildren<NGS.MeshFusionPro.MeshFusionSource>(true))
                if (src.CombineStatus is NGS.MeshFusionPro.SourceCombineStatus.Combined or NGS.MeshFusionPro.SourceCombineStatus.CombinedPartially)
                    src.UndoCombine();
            if (!moving) return;
            int batched = root.GetComponentsInChildren<Renderer>(true).Count(r => r.isPartOfStaticBatch);
            if (batched > 0) log.Warn($"{root.name}: {batched} renderers are statically batched and stay drawn where they were");
        }
    }
}
