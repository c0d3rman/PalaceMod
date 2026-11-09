using UnityEngine;

namespace PalaceMod.Builder.Logic
{
    /// <summary>
    /// The MonoBehaviour on every Builder entity's game object (placed, attached, spawned). It ties the object to its
    /// <see cref="LogicEntity"/>; all state lives there. Added by <see cref="LogicGame.Register"/>.
    ///
    /// On a unit template (UnitFactory) it carries <see cref="TemplateId"/>, which Instantiate copies, so a clone the
    /// game spawns itself (a wave, the game's SpawnOnDeath) registers as an entity of that template in Start. Vanilla
    /// objects get a LogicEntity without this component (LogicGame.EntityOf).
    /// </summary>
    public sealed class BuilderEntity : MonoBehaviour
    {
        /// <summary>The Builder template this object is (or was cloned from); serialized so clones keep it.</summary>
        public string TemplateId;

        [System.NonSerialized] public LogicEntity Entity;

        private void Start()
        {
            if (Entity != null || string.IsNullOrEmpty(TemplateId)) return;
            var world = LogicWorld.Current;
            if (world == null) return;
            var tagged = GetComponentInChildren<TaggedObject>(true);
            string team = tagged != null && tagged.Tags.Contains(TagManager.ETag.PlayerOwned) ? "player"
                : tagged != null && tagged.Tags.Contains(TagManager.ETag.EnemyOwned) ? "enemy" : null;
            var e = world.Describe(TemplateId, null, null, team, null);
            e.Kind = "unit";
            // Made by the game (a wave), not by a rule: not counted against the spawn caps, and savestates restore
            // it as the game's own enemy, not as a respawned Builder entity.
            e.Spawned = false;
            LogicGame.Register(e, gameObject);
            // L2 adds the template's components (targeting, movement, attacks, statuses); `spawn` is only queued yet.
            Bodies.BodyEvents.RaiseUnitSpawned(e, gameObject);
        }

        private void OnDestroy()
        {
            if (Entity != null && !Entity.Destroyed) LogicWorld.Current?.Forget(Entity);
        }
    }
}
