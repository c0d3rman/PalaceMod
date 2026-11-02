using UnityEngine.SceneManagement;

namespace PalaceMod
{
    public interface IManager
    {
        void Awake(Plugin plugin);
        void Start();
        void Update();
        void OnDestroy();
    }
}
