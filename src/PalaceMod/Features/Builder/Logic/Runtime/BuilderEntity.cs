using UnityEngine;

namespace PalaceMod.Builder.Logic
{
    /// <summary>
    /// The MonoBehaviour on every Builder entity's game object (placed, attached, spawned). It ties the object to its
    /// <see cref="LogicEntity"/>; all state lives there. Added by <see cref="LogicGame.Register"/>; vanilla objects
    /// get a LogicEntity without this component (see LogicGame.EntityOf).
    /// </summary>
    public sealed class BuilderEntity : MonoBehaviour
    {
        public LogicEntity Entity;

        private void OnDestroy()
        {
            if (Entity != null && !Entity.Destroyed) LogicWorld.Current?.Forget(Entity);
        }
    }
}
