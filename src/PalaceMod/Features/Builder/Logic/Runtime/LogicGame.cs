using UnityEngine;

/*
    Unity side of the logic API: conversions between LogicEntity and game objects. L2 uses these to reach the
    GameObject, Hp and TaggedObject behind an entity, and to register the entities it makes.
*/
namespace PalaceMod.Builder.Logic
{
    public static class LogicGame
    {
        public static Vec ToVec(this Vector3 v) => new(v.x, v.y, v.z);
        public static Vector3 ToVector3(this Vec v) => new(v.X, v.Y, v.Z);

        public static GameObject Go(this LogicEntity e) => e?.Game as GameObject;
        public static Hp Hp(this LogicEntity e) => e.Go() is { } go && go ? go.GetComponent<Hp>() : null;
        public static TaggedObject Tagged(this LogicEntity e) => e.Go() is { } go && go ? go.GetComponent<TaggedObject>() : null;
        public static BuilderEntity Builder(this LogicEntity e) => e.Go() is { } go && go ? go.GetComponent<BuilderEntity>() : null;

        /// <summary>
        /// The entity for a game object: its BuilderEntity's, or a lazily made one for a vanilla object (unit,
        /// building, king, coin), registered in the current world. Null outside a logic session, or for null.
        /// </summary>
        public static LogicEntity EntityOf(GameObject go) => go == null ? null : LogicGameWorld.EntityOf(LogicWorld.Current, go);
        public static LogicEntity EntityOf(Component c) => c == null ? null : EntityOf(c.gameObject);

        /// <summary>
        /// The BuilderEntity a game object belongs to, or null. Anything under a Builder entity's object is part of
        /// it (a tower's AutoAttack under an attached slot, a prop's collider), except a unit living in its hierarchy:
        /// a barracks' squad spawns under its slot, and with the slot attached each soldier is still its own
        /// (vanilla) unit. A unit here is a TaggedObject below the entity's object, not tagged Building, that isn't
        /// the entity's own body.
        /// </summary>
        public static BuilderEntity OwningBuilder(GameObject go)
        {
            if (go == null) return null;
            var be = go.GetComponentInParent<BuilderEntity>(true);
            return be != null && BelongsTo(go, be.transform, be.Entity) ? be : null;
        }

        /// <summary>Whether <paramref name="go"/>, at or under <paramref name="root"/> (the object of
        /// <paramref name="e"/>), is part of that entity rather than a unit living in its hierarchy (see OwningBuilder).</summary>
        public static bool BelongsTo(GameObject go, Transform root, LogicEntity e)
        {
            var tagged = go.GetComponentInParent<TaggedObject>(true);
            if (tagged == null || tagged.transform == root || !tagged.transform.IsChildOf(root)) return true;
            if (e?.Body is GameBody gb && gb.Tagged == tagged) return true;
            return tagged.Tags.Contains(TagManager.ETag.Building);
        }

        public static BuilderEntity OwningBuilder(Component c) => c == null ? null : OwningBuilder(c.gameObject);

        /// <summary>
        /// Register an entity whose game object the caller made (L2's factory; L1 for places and attaches): adds a
        /// BuilderEntity to <paramref name="go"/>, gives the entity a body over it, then <see cref="LogicWorld.Add"/>
        /// (id, name, rules, the `spawn` event). No-op outside a logic session.
        /// </summary>
        public static void Register(LogicEntity e, GameObject go)
        {
            var world = LogicWorld.Current;
            if (world == null) return;
            if (go != null)
            {
                e.Game = go;
                e.Body ??= new GameBody(go);
                var be = go.GetComponent<BuilderEntity>() ?? go.AddComponent<BuilderEntity>();
                be.Entity = e;
                LogicGameWorld.Index(world, go, e);
            }
            world.Add(e);
        }
    }
}
