using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    Other maps' colour schemes, for a level ("colors" on the level: the whole map recoloured, like the game's Eternal
    Trials schemes) or one prop ("colors" on a prop: it keeps the colours of the map it comes from).

    Every map recolours the same shared FlatKit materials in ColorAndLightManager.ApplyColorScheme from its Colorscheme
    asset, so a library prop drawn with the host's material takes the host's colours. A map's Colorscheme is read from
    the player's install (catalog "colorschemes", AssetFileReader.ReadColorscheme) into a new Colorscheme, cached for the
    game session. A prop with its own colours gets clones of its materials coloured from that scheme the same way
    ApplyColorScheme does it; their outlines follow the host's sunlight through the day like the originals. A level's
    scheme goes through the game's own EasySwitchEntireColorSchemeTo (or is in place before ColorAndLightManager.Start).
*/
namespace PalaceMod.Builder.Library
{
    internal static class MapColors
    {
        private static PalaceMod.Log Log => PalaceMod.Log.For<BuilderManager>();

        private static readonly Dictionary<string, Colorscheme> schemes = new(StringComparer.Ordinal);
        private static readonly HashSet<string> failed = new(StringComparer.Ordinal);

        /// <summary>A main map's colour scheme (scene or display name), read from the install once; null if it can't
        /// be read (logged once).</summary>
        public static Colorscheme Scheme(string map)
        {
            var cat = SceneryLibrary.Catalog;
            string key = cat.SchemeMap(map);
            if (key == null || failed.Contains(key)) return null;
            if (schemes.TryGetValue(key, out var s) && s != null) return s;
            var r = cat.Colorschemes[key];
            try
            {
                using var file = new AssetFileReader(Path.Combine(SceneryLibrary.DataFolder, r.File));
                if (file.UnityVersion != cat.Unity) throw new InvalidDataException($"{r.File} is from Unity {file.UnityVersion}; the catalog is for {cat.Unity}");
                var raw = file.ReadColorscheme(r.PathId, r.Name);
                s = ScriptableObject.CreateInstance<Colorscheme>();
                s.name = "PalaceMod " + r.Name;
                s.hideFlags = HideFlags.DontUnloadUnusedAsset;
                foreach (var kv in raw.Colors)
                {
                    var f = typeof(Colorscheme).GetField(kv.Key, BindingFlags.Instance | BindingFlags.Public);
                    if (f == null || f.FieldType != typeof(Color) || f.IsInitOnly) continue;
                    f.SetValue(s, new Color(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
                }
                schemes[key] = s;
                return s;
            }
            catch (Exception e)
            {
                failed.Add(key);
                Log.Warn($"Can't read {key}'s colour scheme ({r.File}:{r.PathId}): {e.Message}");
                return null;
            }
        }

        // ---- One prop ----------------------------------------------------------------------------------------------

        /// <summary>The ColorAndLightManager materials a prop can use and the scheme colours ApplyColorScheme gives
        /// each (light, mid); shadow is always globalShadowColor.</summary>
        private static readonly (string Field, Func<Colorscheme, Color> Light, Func<Colorscheme, Color> Mid)[] Recoloured =
        [
            ("buildingMaterial", s => s.buildingLightColor, s => s.buildingMidColor),
            ("buildingMaterialShadow", s => s.buildingLightColor, s => s.buildingMidColor),
            ("groundMaterial", s => s.groundColor, s => s.globalShadowColor),
            ("groundPatches", s => s.groundColorLow, s => s.globalShadowColor),
            ("groundHigh", s => s.groundColorHigh, s => s.globalShadowColor),
            ("groundLow", s => s.groundColorLow, s => s.globalShadowColor),
            ("earthMaterial", s => s.earthLightColor, s => s.earthMidColor),
            ("sandMaterial", s => s.sandColor, s => s.globalShadowColor),
            ("treeMaterial", s => s.treeLightColor, s => s.treeMidColor),
            ("rockMaterial", s => s.rockLightColor, s => s.rockMidColor),
            ("roadMaterial", s => s.roadColor, s => s.globalShadowColor),
            ("coinMaterial", s => s.coinLightColor, s => s.coinMidColor),
            ("waterMaterial", s => s.waterLightColor, s => s.waterSecondaryColor),
            ("oceanMaterial", s => s.waterLightColor, s => s.waterSecondaryColor),
        ];

        private static ColorAndLightManager owner;
        private static readonly Dictionary<Material, int> slotOf = [];
        private static readonly Dictionary<(Material, string), (Material Clone, Colorscheme Scheme)> clones = [];

        /// <summary>A prop's materials in a map's colours: each material ColorAndLightManager recolours is replaced by
        /// a clone coloured from that scheme; the rest (and everything, if the scheme can't be read) stay as they are.</summary>
        public static Material[] Recolor(Material[] materials, string map)
        {
            if (materials == null || map == null) return materials;
            var scheme = Scheme(map);
            if (scheme == null || !Bind()) return materials;
            string key = SceneryLibrary.Catalog.SchemeMap(map);
            var result = (Material[])materials.Clone();
            for (int i = 0; i < result.Length; i++)
            {
                var m = result[i];
                if (m == null || !slotOf.TryGetValue(m, out int slot)) continue;
                if (!clones.TryGetValue((m, key), out var c) || c.Clone == null)
                {
                    var r = Recoloured[slot];
                    var clone = new Material(m) { name = $"{m.name} ({SceneryCatalog.DisplayMap(key)})" };
                    clone.SetColor("_BaseColor", r.Light(scheme));
                    clone.SetColor("_ColorDim", r.Mid(scheme));
                    clone.SetColor("_ColorDimExtra", scheme.globalShadowColor);
                    clone.SetColor("_UnityShadowColor", scheme.globalShadowColor);
                    c = (clone, scheme);
                    clones[(m, key)] = c;
                    Outline(clone, scheme);
                }
                result[i] = c.Clone;
            }
            return result;
        }

        /// <summary>Draws a prop (made with LayoutScene.MakeProp from b) in a map's colours, or the level's (null).</summary>
        public static void Paint(GameObject prop, PropBase b, string map)
        {
            var r = prop != null ? prop.GetComponent<MeshRenderer>() : null;
            if (r == null || b == null) return;
            r.sharedMaterials = map == null ? b.Materials : Recolor(b.Materials, map);
        }

        /// <summary>The map a prop base's colours come from: a library entry's "@Map" or home map, else (the host's own
        /// scenery) the host.</summary>
        public static string DonorOf(UnityEngine.SceneManagement.Scene scene, string baseRef)
        {
            var cat = SceneryLibrary.Catalog;
            // The host's own mesh wins over the library's (SceneryLibrary.Resolve), so its colours are the host's.
            if (SceneryLibrary.IsHostMesh(scene, baseRef)) return cat.SchemeMap(scene.name);
            return cat.DonorOf(baseRef) ?? cat.SchemeMap(scene.name);
        }

        /// <summary>Tracks the loaded level's ColorAndLightManager: which material is which, and its colour changes
        /// (the clones' outlines follow its light). False outside a level.</summary>
        private static bool Bind()
        {
            var m = ColorAndLightManager.Instance;
            if (m == null) return false;
            if (m == owner) return true;
            foreach (var c in clones.Values) if (c.Clone != null) UnityEngine.Object.Destroy(c.Clone);
            clones.Clear();
            slotOf.Clear();
            owner = m;
            for (int i = 0; i < Recoloured.Length; i++)
                if (AccessTools.Field(typeof(ColorAndLightManager), Recoloured[i].Field)?.GetValue(m) is Material mat && mat != null && !slotOf.ContainsKey(mat))
                    slotOf[mat] = i;
            m.OnColorChange.AddListener(OnColorChange);
            return true;
        }

        private static void OnColorChange()
        {
            foreach (var c in clones.Values) if (c.Clone != null) Outline(c.Clone, c.Scheme);
        }

        /// <summary>The outline as AdjustAllOutlineColorsBasedOnLight sets it: the scheme's shadow, tinted by the
        /// host's current sunlight as much as the material takes light.</summary>
        private static void Outline(Material clone, Colorscheme scheme)
        {
            var light = owner != null && owner.Sunlight != null ? owner.Sunlight.color : Color.white;
            float k = clone.HasProperty("_LightContribution") ? clone.GetFloat("_LightContribution") : 0f;
            clone.SetColor("_OutlineColor", Color.Lerp(scheme.globalShadowColor, scheme.globalShadowColor * light, k));
        }

        // ---- The whole level ---------------------------------------------------------------------------------------

        private static ColorAndLightManager levelOwner;
        private static Colorscheme hostScheme;
        private static string levelMap;

        /// <summary>Gives the loaded level a map's colour scheme (null: back to the host's own). Safe to call every
        /// edit; it only switches when the map changes. Before ColorAndLightManager.Start it just sets the scheme Start
        /// will apply.</summary>
        public static void ApplyLevel(string map)
        {
            var m = ColorAndLightManager.Instance;
            if (m == null) return;
            var field = Traverse.Create(m).Field("colorscheme");
            if (m != levelOwner)
            {
                levelOwner = m;
                hostScheme = field.GetValue<Colorscheme>();
                levelMap = null;
            }
            string key = map == null ? null : SceneryLibrary.Catalog.SchemeMap(map);
            if (key == levelMap) return;
            var target = key == null ? hostScheme : Scheme(key);
            if (target == null || hostScheme == null) return;
            if (target != hostScheme) target.particlesToAttachToPlayer = hostScheme.particlesToAttachToPlayer;   // keep the host's weather
            levelMap = key;
            bool started = Traverse.Create(m).Field("initialColorscheme").GetValue<Colorscheme>() != null;
            if (!started) { field.SetValue(target); return; }
            try { m.EasySwitchEntireColorSchemeTo(target); }
            catch (Exception e) { Log.Caught($"Switching to {key ?? "the host's"} colours", e); }
        }
    }
}
