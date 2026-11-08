using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The "empty map" bases (LevelDef.base, docs/builder-ux.md "Starting a level, and the empty map"). Run by the overlay
    on the freshly loaded host, after Awake and before Start, in play and in the Studio alike:

    - every build slot but the castle (and the add-ons it upgrades) is removed: LevelOverlay does that, after cloning,
      so the removed slots still serve as bases for added ones;
    - scenery is hidden here: every renderer of the host is turned off unless it only draws with the map's ground,
      water and road materials (ColorAndLightManager's), or belongs to the castle, a build slot, the enemy spawner, a
      unit, the camera or UI. Colliders on hidden objects go too, so the king walks through where trees stood. The
      map's invisible walls stay, so its boundary holds. MeshFusion combining is turned off before it runs (it would
      otherwise merge hidden sources back into visible chunks).
*/
namespace PalaceMod.Builder.Level
{
    internal static class EmptyBase
    {
        private static readonly string[] GroundFields =
        [
            "groundMaterial", "groundHigh", "groundLow", "earthMaterial", "sandMaterial", "groundPatches", "rockMaterial",
            "waterMaterial", "waterfallMaterial", "oceanMaterial", "roadMaterial",
        ];

        /// <summary>The map's ground, water and road materials (by reference and by name).</summary>
        public static HashSet<string> GroundMaterialNames(Scene scene)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var clm = ColorAndLightManager.Instance;
            if (clm == null || clm.gameObject.scene != scene)
                clm = Resources.FindObjectsOfTypeAll<ColorAndLightManager>().FirstOrDefault(c => c != null && c.gameObject.scene == scene) ?? clm;
            if (clm == null) return names;
            var t = Traverse.Create(clm);
            foreach (var f in GroundFields)
            {
                var m = t.Field(f).GetValue<Material>();
                if (m != null) names.Add(Clean(m.name));
            }
            return names;
        }

        private static string Clean(string name) => name.Replace(" (Instance)", "").Trim();

        /// <summary>Hides the host's scenery. Returns how many renderers were turned off.</summary>
        public static int HideScenery(Scene scene)
        {
            var ground = GroundMaterialNames(scene);
            if (ground.Count == 0)
            {
                Log.For<BuilderManager>().Warn($"{scene.name}: no ground materials found; the scenery stays");
                return 0;
            }
            var keepRoots = new HashSet<Transform>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var c in root.GetComponentsInChildren<BuildSlot>(true)) keepRoots.Add(c.transform);
                foreach (var c in root.GetComponentsInChildren<EnemySpawner>(true)) keepRoots.Add(c.transform);
                foreach (var c in root.GetComponentsInChildren<Hp>(true)) keepRoots.Add(c.transform);
                foreach (var c in root.GetComponentsInChildren<Camera>(true)) keepRoots.Add(c.transform);
                foreach (var c in root.GetComponentsInChildren<Canvas>(true)) keepRoots.Add(c.transform);
                foreach (var c in root.GetComponentsInChildren<PlayerMovement>(true)) keepRoots.Add(c.transform);
                foreach (var c in root.GetComponentsInChildren<NavmeshBakeHelper>(true))
                {
                    var walls = Traverse.Create(c).Field("invisibleWalls").GetValue<GameObject>();
                    if (walls != null) keepRoots.Add(walls.transform);
                }
            }

            int hidden = 0;
            var hiddenObjects = new HashSet<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var source in root.GetComponentsInChildren<NGS.MeshFusionPro.MeshFusionSource>(true)) source.enabled = false;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || r.gameObject.layer == 5 || r.name.StartsWith(LayoutScene.AddedPrefix, StringComparison.Ordinal)) continue;
                    if (Kept(r.transform, keepRoots)) continue;
                    var mats = r.sharedMaterials;
                    if (mats.Length > 0 && mats.All(m => m != null && ground.Contains(Clean(m.name)))) continue;
                    r.enabled = false;
                    hiddenObjects.Add(r.gameObject);
                    hidden++;
                }
            }
            foreach (var go in hiddenObjects)
                foreach (var col in go.GetComponents<Collider>())
                    col.enabled = false;
            return hidden;
        }

        private static bool Kept(Transform t, HashSet<Transform> keep)
        {
            for (var p = t; p != null; p = p.parent)
                if (keep.Contains(p)) return true;
            return false;
        }

        /// <summary>Whether a host slot survives an empty base: the castle, slots inside it and add-ons it upgrades.</summary>
        public static bool Keeps(BuildSlot slot)
        {
            if (LayoutScene.IsCastle(slot)) return true;
            for (var p = slot.transform.parent; p != null; p = p.parent)
            {
                var owner = p.GetComponent<BuildSlot>();
                if (owner != null && LayoutScene.IsCastle(owner)) return true;
            }
            var a = slot;
            for (int guard = 0; a != null && a.ActivatorUpgradesThis && guard < 16; guard++)
            {
                a = a.ActivatorBuilding;
                if (a != null && LayoutScene.IsCastle(a)) return true;
            }
            return false;
        }
    }
}
