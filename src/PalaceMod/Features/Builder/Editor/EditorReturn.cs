using HarmonyLib;
using UnityEngine.SceneManagement;

/*
    Coming back to the editor. A test play or the layout editor started from the Builder editor remembers where the
    player was: the screen they opened the Builder from (the title, or a pause menu on the map select or in a level)
    and the editor page they launched from. When that custom run leaves its map for the map select (the layout
    editor's Leave, the pause menu's Back to Map, the end screen's Continue), the one cloud transition goes to the
    title instead when that's where the Builder was opened, and the editor is reopened on the same page under the
    clouds, so the player lands back where they were with no second trip through the menus. From a pause menu (a
    level's own map is gone by then) it lands on the map select, with the pause menu and the editor open on it.
*/
namespace PalaceMod.Builder.Editor
{
    internal static class EditorReturn
    {
        private sealed class Place
        {
            public bool Title;
            public string PackId, Path;
            public string Scene;   // the scene the transition is heading to (set once it's under way)
        }

        private static Place pending, reopening;

        /// <summary>Called right after the editor launched a custom run: remembers the screen to come back to.</summary>
        internal static void Remember()
        {
            if (!BuilderEditor.CurrentPlace(out string packId, out string path)) return;
            // Launched from a custom run's own pause menu: the run started somewhere else; keep where that was.
            bool title = pending != null && BuilderServices.InSession
                ? pending.Title
                : SceneManager.GetActiveScene().name == (SceneTransitionManager.instance?.mainMenuScene ?? "_StartMenu");
            pending = new Place { Title = title, PackId = packId, Path = path };
            Log.For<BuilderManager>().Debug($"Return point: {(title ? "title" : "map select")}, {packId} {path}");
        }

        /// <summary>Every scene transition passes here first (TransitionToScene prefix), and may be redirected.</summary>
        internal static void OnTransition(ref string scene)
        {
            var p = pending;
            if (p == null) return;
            var session = BuilderServices.Sessions.Current;
            // Starting (or restarting) the run's map: the run goes on.
            if (session != null && scene == session.HostScene) return;
            pending = null;
            var stm = SceneTransitionManager.instance;
            if (session == null || stm == null || !Feature<BuilderManager>.On) return;
            if (scene != stm.levelSelectScene && scene != stm.mainMenuScene) return;
            if (p.Title) scene = stm.mainMenuScene;
            p.Scene = scene;
            reopening = p;
            // Nothing on the Builder's screens asks for music, so the run's (a night's) would play on until the title.
            Builder.Studio.Play.StudioPlay.StopMusic();
            Log.For<BuilderManager>().Info($"Back to the Builder: {scene}, {p.PackId} {p.Path}");
        }

        /// <summary>Per frame: once the transition has swapped the scene in (and closed the old menus), opens the
        /// editor under the clouds that are still clearing.</summary>
        internal static void Update()
        {
            var p = reopening;
            if (p == null) return;
            var manager = UIFrameManager.instance;
            if (manager == null || SceneManager.GetActiveScene().name != p.Scene) return;
            reopening = null;
            if (!Feature<BuilderManager>.On) return;
            // The map select has no menu up after a transition; the pause menu goes under the editor, as when the
            // Builder was first opened from it, so backing out of the editor lands there.
            if (!p.Title && manager.ActiveFrame == null) AccessTools.Method(typeof(UIFrameManager), "OpenMenu").Invoke(manager, null);
            BuilderEditor.Reopen(p.PackId, p.Path);
        }
    }
}
