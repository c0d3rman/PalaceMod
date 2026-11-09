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
