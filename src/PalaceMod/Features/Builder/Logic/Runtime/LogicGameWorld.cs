using System;
using System.Collections.Generic;
using UnityEngine;

/*
    Game-object side of the registry: the body over a GameObject, and lazy LogicEntities for vanilla objects (units,
    buildings, the king, the castle, coins) the first time an event or a selector touches them.
*/
namespace PalaceMod.Builder.Logic
{
    /// <summary>IEntityBody over a game object (its Hp and TaggedObject, looked up once).</summary>
    public sealed class GameBody : IEntityBody
    {
        public readonly GameObject Go;
        private readonly Hp hp;
        private readonly TaggedObject tagged;
        private BuildSlot slot;
        private bool slotLooked;

        public GameBody(GameObject go)
        {
            Go = go;
            hp = go.GetComponentInChildren<Hp>(true);
            tagged = hp != null ? hp.GetComponent<TaggedObject>() : go.GetComponentInChildren<TaggedObject>(true);
        }

        public Hp Hp => hp;
        public TaggedObject Tagged => tagged;
        public bool Exists => Go != null;
        public bool Alive => Go != null && Go.activeInHierarchy && (hp == null || !hp.KnockedOut);
        public Vec Position => Go != null ? (tagged != null ? tagged.transform.position : Go.transform.position).ToVec() : Vec.Zero;
        public float Facing => Go != null ? Go.transform.eulerAngles.y : 0f;
        float IEntityBody.Hp => hp != null ? hp.HpValue : 0f;
        public float MaxHp => hp != null ? hp.maxHp : 0f;

        public bool HasTag(string etag) => tagged != null && LogicTags.TryParse(etag, out var t) && tagged.Tags.Contains(t);

        private BuildSlot Slot
        {
            get
            {
                if (!slotLooked) { slotLooked = true; slot = Go != null ? Go.GetComponentInParent<BuildSlot>() : null; }
                return slot;
            }
        }

        public string BuildingType => Slot != null ? Slot.buildingName : null;
        public int Level => Slot != null ? Slot.Level : 0;
    }

    internal static class LogicTags
    {
        private static readonly Dictionary<string, TagManager.ETag?> cache = new(StringComparer.Ordinal);

        public static bool TryParse(string name, out TagManager.ETag tag)
        {
            if (!cache.TryGetValue(name, out var t))
            {
                t = Enum.TryParse(name, false, out TagManager.ETag parsed) && Enum.IsDefined(typeof(TagManager.ETag), parsed) ? parsed : null;
                cache[name] = t;
            }
            tag = t ?? TagManager.ETag.NONE;
            return t.HasValue;
        }
    }

    internal static class LogicGameWorld
    {
        private sealed class Index_ { public readonly Dictionary<int, LogicEntity> ByGame = []; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LogicWorld, Index_> indices = new();

        public static void Index(LogicWorld world, GameObject go, LogicEntity e) => indices.GetOrCreateValue(world).ByGame[go.GetInstanceID()] = e;

        public static LogicEntity EntityOf(LogicWorld world, GameObject go)
        {
            if (world == null || go == null) return null;
            var idx = indices.GetOrCreateValue(world).ByGame;
            int key = go.GetInstanceID();
            if (idx.TryGetValue(key, out var e) && !e.Destroyed) return e;

            var be = go.GetComponentInParent<BuilderEntity>();
            if (be?.Entity != null) { idx[key] = be.Entity; return be.Entity; }

            // A vanilla object: wrap the object that carries its TaggedObject, so a child collider or Hp maps to the same entity.
            var tagged = go.GetComponentInParent<TaggedObject>() ?? go.GetComponentInChildren<TaggedObject>();
            var root = tagged != null ? tagged.gameObject : go;
            int rootKey = root.GetInstanceID();
            if (rootKey != key && idx.TryGetValue(rootKey, out e) && !e.Destroyed) { idx[key] = e; return e; }

            e = new LogicEntity
            {
                Id = "vanilla:" + (SaveStates.PathOf(root.transform) ?? root.name),
                Template = "vanilla:" + root.name.Replace("(Clone)", "").Trim(),
                Game = root,
            };
            var body = new GameBody(root);
            e.Body = body;
            e.Team = body.HasTag("PlayerOwned") ? "player" : body.HasTag("EnemyOwned") ? "enemy" : "neutral";
            e.Kind = body.HasTag("Player") ? "king"
                : body.HasTag("CastleCenter") ? "castle"
                : root.GetComponent<Coin>() != null ? "coin"
                : body.HasTag("Building") ? "building"
                : "unit";
            idx[rootKey] = e;
            idx[key] = e;
            world.Add(e, vanilla: true);
            return e;
        }
    }
}
