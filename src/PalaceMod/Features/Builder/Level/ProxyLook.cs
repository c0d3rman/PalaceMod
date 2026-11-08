using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The built look of an editor proxy (LayoutScene.Proxy): the slot's building, hidden, plus what each upgrade level
    changes (UpgradeBranch.replacementMesh, objectsToActivate, objectsToDisable; the first branch where there's a
    choice). Show(level) turns the proxy into its building at that level, optionally drawn in the game's own preview
    material (BuildingInteractor.previewMaterial), or back into its blueprint (level 0).

    Captured from the copy's BuildSlot before LayoutScene.Strip removes it. The fields are serialized, so a proxy
    copied with Object.Instantiate keeps them, pointing at its own children. Objects outside the copy (an upgrade that
    turns on something elsewhere on the map) are left out.
*/
namespace PalaceMod.Builder.Level
{
    internal sealed class ProxyLook : MonoBehaviour
    {
        public GameObject blueprint, built;
        public MeshFilter main;
        public Mesh mainMesh;
        /// <summary>Per upgrade (index 0 = the build): the main mesh after it, or null to keep the one before.</summary>
        public List<Mesh> meshes = [];
        /// <summary>Objects an upgrade turns on (objOn) or off, with the upgrade index that does it.</summary>
        public List<GameObject> objs = [];
        public List<int> objUpgrade = [];
        public List<bool> objOn = [];
        /// <summary>Renderers of the building with their own materials (rendMats[i] materials each, in order).</summary>
        public List<Renderer> rends = [];
        public List<int> rendMats = [];
        public List<Material> mats = [];
        public int shownLevel;
        public bool shownGhost;

        public int Levels => meshes.Count;

        public void Capture(BuildSlot slot, GameObject blueprintRoot, GameObject builtRoot)
        {
            blueprint = blueprintRoot;
            built = builtRoot;
            main = slot.MainMesh != null && slot.MainMesh.transform.IsChildOf(transform) ? slot.MainMesh : null;
            mainMesh = main != null ? main.sharedMesh : null;
            var states = new Dictionary<GameObject, bool>();
            for (int i = 0; i < slot.OwnUpgrades.Count; i++)
            {
                var branch = slot.OwnUpgrades[i]?.upgradeBranches?.FirstOrDefault();
                meshes.Add(branch?.replacementMesh);
                if (branch == null) continue;
                foreach (var go in branch.objectsToActivate ?? []) Add(go, i, true);
                foreach (var go in branch.objectsToDisable ?? []) Add(go, i, false);
            }
            foreach (var r in builtRoot.GetComponentsInChildren<Renderer>(true))
            {
                rends.Add(r);
                rendMats.Add(r.sharedMaterials.Length);
                mats.AddRange(r.sharedMaterials);
            }

            void Add(GameObject go, int upgrade, bool on)
            {
                if (go == null || !go.transform.IsChildOf(transform)) return;
                objs.Add(go);
                objUpgrade.Add(upgrade);
                objOn.Add(on);
            }
        }

        public void Show(int level, bool ghost)
        {
            level = Mathf.Clamp(level, 0, Levels);
            shownLevel = level;
            shownGhost = ghost && level > 0;
            if (blueprint != null) blueprint.SetActive(level == 0);
            if (built != null) built.SetActive(level > 0);
            if (level == 0) return;

            // Objects an upgrade turns on start hidden (BuildSlot.OnEnable); then apply upgrades 0..level-1 in order.
            for (int i = 0; i < objs.Count; i++) if (objs[i] != null && objOn[i]) objs[i].SetActive(false);
            Mesh mesh = mainMesh;
            for (int u = 0; u < level; u++)
            {
                if (meshes[u] != null) mesh = meshes[u];
                for (int i = 0; i < objs.Count; i++) if (objs[i] != null && objUpgrade[i] == u) objs[i].SetActive(objOn[i]);
            }
            if (main != null && mesh != null) main.sharedMesh = mesh;

            var preview = shownGhost ? PreviewMaterial() : null;
            int at = 0;
            for (int i = 0; i < rends.Count; i++)
            {
                int n = rendMats[i];
                if (rends[i] != null)
                {
                    if (preview != null) rends[i].sharedMaterials = Enumerable.Repeat(preview, n).ToArray();
                    else rends[i].sharedMaterials = mats.Skip(at).Take(n).ToArray();
                }
                at += n;
            }
        }

        private static Material preview;

        /// <summary>The game's building preview material (what it draws upgrade previews with).</summary>
        public static Material PreviewMaterial()
        {
            if (preview != null) return preview;
            foreach (var bi in Resources.FindObjectsOfTypeAll<BuildingInteractor>())
                if (bi != null && bi.previewMaterial != null) return preview = bi.previewMaterial;
            return null;
        }
    }
}
