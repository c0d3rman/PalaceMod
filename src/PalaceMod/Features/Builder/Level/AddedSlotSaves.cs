using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The game's match save for slots a level adds. LocalMatchSaveLoad lists every SaveLoadEntity and loads the match in
    its Awake, before the overlay clones the added slots (sceneLoaded). Without this the added slots would be neither
    saved (the dawn save that retry-day uses, savestates) nor loaded: a retry or a savestate load would bring them back
    unbuilt and locked. So each added slot's entities get stable GUIDs, join the loader's list, and, when the loader
    has just loaded a save, are loaded the same way (before-pass, load, after-pass) now that they exist.
*/
namespace PalaceMod.Builder.Level
{
    internal static class AddedSlotSaves
    {
        /// <summary>Call once the clones are in the scene (active, before their Start). Returns how many were loaded.</summary>
        public static int Join(Scene scene, string packId, string levelId, IEnumerable<(string id, GameObject go)> added)
        {
            var loader = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<LocalMatchSaveLoad>(true)).FirstOrDefault();
            if (loader == null) return 0;
            var entities = new List<SaveLoadEntity>();
            foreach (var (id, go) in added)
            {
                if (go == null) continue;
                foreach (var sle in go.GetComponentsInChildren<SaveLoadEntity>(true))
                {
                    // The root got its GUID from the overlay; children of a copied slot would share their source's.
                    if (sle.gameObject != go) LayoutScene.SetGuid(sle.gameObject, LayoutScene.Guid(packId, levelId, id + "/" + RelativePath(go.transform, sle.transform)));
                    entities.Add(sle);
                }
            }
            if (entities.Count == 0) return 0;
            var list = Priv.Get<SaveLoadEntity[]>(loader, "allSaveLoadEntities");
            if (list == null) return 0;   // automatic save/load is off for this level
            Priv.Set(loader, "allSaveLoadEntities", list.Concat(entities).Distinct().ToArray());

            // LocalMatchSaveLoad.TryLoad's own condition, unchanged since its Awake this frame.
            if (loader.disableAutomaticSaveLoading || !MatchSaveLoadHandler.IsLoadingPermitted || MatchSaveLoadHandler.CurrentSave == null) return 0;
            foreach (var e in entities) e.ExecuteBeforeMainLoadPass();
            foreach (var e in entities) e.ExecuteLoad();
            foreach (var e in entities) e.ExecuteAfterMainLoadPass();
            return entities.Count;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Add(t.name + "#" + t.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
