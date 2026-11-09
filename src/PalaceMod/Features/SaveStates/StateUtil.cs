using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PalaceMod
{
    /// <summary>Cached reflection access to the game's private fields.</summary>
    internal static class Priv
    {
        private static readonly Dictionary<(Type, string), FieldInfo> fields = [];

        private static FieldInfo Field(Type type, string name)
        {
            if (!fields.TryGetValue((type, name), out var field))
            {
                field = AccessTools.Field(type, name) ?? throw new MissingFieldException(type.Name, name);
                fields[(type, name)] = field;
            }
            return field;
        }

        public static T Get<T>(object obj, string name) => (T)Field(obj.GetType(), name).GetValue(obj);
        public static void Set(object obj, string name, object value) => Field(obj.GetType(), name).SetValue(obj, value);
        public static T Get<T>(Type type, string name, object obj) => (T)Field(type, name).GetValue(obj);
        public static void Set(Type type, string name, object obj, object value) => Field(type, name).SetValue(obj, value);
        public static T GetStatic<T>(Type type, string name) => (T)Field(type, name).GetValue(null);
        public static void SetStatic(Type type, string name, object value) => Field(type, name).SetValue(null, value);
    }

    /// <summary>
    /// Stable names for scene objects, so a captured unit or building can be found again after the level reloads:
    /// "Root[0]/Child[2]/Unit[0]", where [k] counts earlier siblings with the same name. Build one per operation.
    /// </summary>
    internal sealed class ScenePaths
    {
        private readonly Dictionary<Transform, string> cache = [];
        private readonly Dictionary<Scene, GameObject[]> roots = [];

        public string Of(Transform t)
        {
            if (cache.TryGetValue(t, out string hit)) return hit;
            int ordinal = 0;
            if (t.parent != null)
            {
                for (int i = 0; i < t.GetSiblingIndex(); i++)
                    if (t.parent.GetChild(i).name == t.name) ordinal++;
            }
            else
            {
                if (!roots.TryGetValue(t.gameObject.scene, out var sceneRoots))
                    roots[t.gameObject.scene] = sceneRoots = t.gameObject.scene.GetRootGameObjects();
                foreach (var root in sceneRoots)
                {
                    if (root.transform == t) break;
                    if (root.name == t.name) ordinal++;
                }
            }
            var sb = new StringBuilder();
            if (t.parent != null) sb.Append(Of(t.parent)).Append('/');
            sb.Append(t.name).Append('[').Append(ordinal).Append(']');
            string path = sb.ToString();
            cache[t] = path;
            return path;
        }

        /// <summary>Path -> component for every given component (first wins on a clash).</summary>
        public Dictionary<string, T> Index<T>(IEnumerable<T> components) where T : Component
        {
            var map = new Dictionary<string, T>();
            foreach (var c in components)
            {
                if (c == null) continue;
                string path = Of(c.transform);
                if (!map.ContainsKey(path)) map[path] = c;
            }
            return map;
        }
    }

    internal static class StateUtil
    {
        public const string TutorialScene = "Neuland(Tutorial)";

        /// <summary>The running Builder session, while its host is the active scene; null otherwise.</summary>
        public static Builder.BuilderSessionInfo BuilderSession
        {
            get
            {
                try
                {
                    var s = Builder.BuilderServices.Sessions.Current;
                    return s != null && SceneManager.GetActiveScene().name == s.HostScene ? s : null;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>The Studio is editing the level (not test-playing it): no savestates there.</summary>
        public static bool StudioEditing
        {
            get
            {
                try { return BuilderManager.Launcher?.Current != null && BuilderManager.Launcher.StudioKind == Builder.StudioSessionKind.Edit; }
                catch (Exception) { return false; }
            }
        }

        private static (object pack, object level, object waveSet) pinFor;
        private static Builder.Model.LevelPin pin;

        /// <summary>The running custom level and its version (cached per session objects), or null in vanilla play.</summary>
        public static Builder.Model.LevelPin BuilderPin
        {
            get
            {
                var s = BuilderSession;
                if (s == null) return null;
                var key = ((object)s.Pack, (object)s.Level, (object)s.WaveSet);
                if (pin == null || !ReferenceEquals(pinFor.pack, key.Item1) || !ReferenceEquals(pinFor.level, key.Item2) || !ReferenceEquals(pinFor.waveSet, key.Item3))
                {
                    pin = Builder.Model.LevelPin.Of(s.Pack, s.Level, s.WaveSet);
                    pinFor = key;
                }
                return pin;
            }
        }

        /// <summary>Where the current level's states are filed: the Builder level's key in a custom session, else the scene.</summary>
        public static string LevelKey(string scene)
        {
            var s = BuilderSession;
            return s == null ? scene : Builder.Model.LevelPin.KeyOf(s.Pack?.Id, s.Level?.Id, s.WaveSet?.Id);
        }

        public static string StripClone(string name)
        {
            while (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "(Clone)".Length).TrimEnd();
            return name;
        }

        /// <summary>The object that is "one enemy": a spawned clone's root, or the tagged object itself for enemies
        /// placed in the scene (which may share a parent with others).</summary>
        public static GameObject EnemyRoot(TaggedObject tagged)
        {
            var root = tagged.transform.root.gameObject;
            return IsClone(root) ? root : tagged.gameObject;
        }

        public static bool IsClone(GameObject go) => go.name.EndsWith("(Clone)", StringComparison.Ordinal);

        public static bool Has(TaggedObject tagged, TagManager.ETag tag) => tagged != null && tagged.Tags.Contains(tag);
    }
}
