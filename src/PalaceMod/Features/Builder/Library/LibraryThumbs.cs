using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Tile pictures for the scenery library: each mesh rendered on its own by an offscreen camera (like LevelCapture),
    three-quarter view, transparent background, with the loaded level's materials and sun, so the picture has the
    colours the prop will have on this map. Rendered on demand a few per frame, cached as PNGs under
    BepInEx/PalaceBuilder/cache/thumbs/<map>/ and in memory for the game session.
*/
namespace PalaceMod.Builder.Library
{
    internal static class LibraryThumbs
    {
        public const int Pixels = 128;
        private const int PerFrame = 4;

        private static readonly Dictionary<string, Sprite> sprites = new(StringComparer.Ordinal);
        private static readonly List<(string key, SceneryEntry entry)> queue = [];
        private static readonly HashSet<string> queued = new(StringComparer.Ordinal);

        /// <summary>Raised once the queue has drained (so the browser re-queries once, not per picture).</summary>
        public static event Action Rendered;

        public static string Folder => Path.Combine(Paths.BepInExRootPath, "PalaceBuilder", "cache", "thumbs");

        private static string Host => SceneManager.GetActiveScene().name;

        private static string Key(string host, SceneryEntry e) => host + "/" + e.Name;

        public static string FileOf(string host, SceneryEntry e)
        {
            var bad = Path.GetInvalidFileNameChars();
            string Safe(string s) => new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
            return Path.Combine(Folder, Safe(host), Safe(e.Name) + ".png");
        }

        /// <summary>The picture for this map, or null (it's queued and Rendered fires when it's there).</summary>
        public static Sprite Get(SceneryEntry entry)
        {
            string host = Host, key = Key(host, entry);
            if (sprites.TryGetValue(key, out var s) && s != null) return s;
            string file = FileOf(host, entry);
            if (File.Exists(file))
            {
                try
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "PalaceMod thumb " + entry.Name, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    if (tex.LoadImage(File.ReadAllBytes(file), true)) return sprites[key] = ToSprite(tex);
                    UnityEngine.Object.Destroy(tex);
                }
                catch (Exception e) { Log.For<BuilderManager>().Warn($"Scenery library: couldn't read {file}: {e.Message}"); }
            }
            if (queued.Add(key)) queue.Add((key, entry));
            return null;
        }

        /// <summary>Renders a few queued pictures; called every frame.</summary>
        public static void Pump()
        {
            if (queue.Count == 0) return;
            string host = Host;
            int done = 0;
            while (queue.Count > 0 && done < PerFrame)
            {
                var (key, entry) = queue[0];
                queue.RemoveAt(0);
                queued.Remove(key);
                if (!key.StartsWith(host + "/", StringComparison.Ordinal)) continue;   // queued on another map
                done++;
                var b = SceneryLibrary.BaseOf(entry, out _);
                if (b == null) continue;
                var tex = Render(b, Pixels);
                if (tex == null) continue;
                try
                {
                    string file = FileOf(host, entry);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, tex.EncodeToPNG());
                }
                catch (Exception e) { Log.For<BuilderManager>().Warn($"Scenery library: couldn't cache the picture of {entry.Name}: {e.Message}"); }
                tex.Apply(false, true);
                sprites[key] = ToSprite(tex);
            }
            if (queue.Count == 0)
            {
                try { Rendered?.Invoke(); }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Scenery library: Rendered handler failed", e); }
            }
        }

        /// <summary>Forgets the in-memory pictures (the files stay); dev command.</summary>
        public static void Clear(bool files)
        {
            sprites.Clear();
            if (files && Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        private static Sprite ToSprite(Texture2D tex)
        {
            var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s;
        }

        private static readonly Vector3 Away = new(0f, -5000f, 0f);

        /// <summary>A flat direction toward the level's sun, turned 40° so lit and shaded sides both show (the
        /// pictures are taken from the lit side; levels like Moorweg are too dark from the other one).</summary>
        private static Vector3 SunSide()
        {
            var sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional && (sun == null || l.intensity > sun.intensity)) sun = l;
            if (sun == null) return new Vector3(-1f, 0f, -1f).normalized;
            var toSun = -sun.transform.forward;
            var flat = new Vector3(toSun.x, 0f, toSun.z);
            if (flat.sqrMagnitude < 1e-4f) return new Vector3(-1f, 0f, -1f).normalized;
            return Quaternion.Euler(0f, 40f, 0f) * flat.normalized;
        }

        /// <summary>A readable RGBA picture of a prop base, or null (logged). The caller owns the texture.</summary>
        public static Texture2D Render(PropBase b, int pixels)
        {
            var main = Camera.main;
            if (main == null) return null;
            GameObject obj = null, camObj = null;
            RenderTexture target = null;
            var active = RenderTexture.active;
            bool fog = RenderSettings.fog;
            try
            {
                obj = new GameObject("PalaceMod thumb subject");
                obj.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = b.Materials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                obj.transform.SetPositionAndRotation(Away, Quaternion.Euler(0f, 30f, 0f) * b.Upright);
                obj.transform.localScale = b.Scale;
                var bounds = renderer.bounds;
                float radius = Mathf.Max(0.1f, bounds.extents.magnitude);

                camObj = new GameObject("PalaceMod thumb camera");
                camObj.SetActive(false);
                var cam = camObj.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                cam.orthographic = false;
                cam.fieldOfView = 25f;
                cam.aspect = 1f;
                // The bounding sphere is loose for long thin meshes; 0.85 frames most of them tightly without clipping.
                float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.85f;
                var dir = (SunSide() + Vector3.up * 0.9f).normalized;
                cam.transform.position = bounds.center + dir * distance;
                cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
                cam.nearClipPlane = Mathf.Max(0.05f, distance - radius * 1.5f);
                cam.farClipPlane = distance + radius * 1.5f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << obj.layer;
                target = RenderTexture.GetTemporary(pixels, pixels, 24, RenderTextureFormat.ARGB32);
                target.antiAliasing = 1;
                cam.targetTexture = target;
                RenderSettings.fog = false;
                cam.Render();
                RenderTexture.active = target;
                var tex = new Texture2D(pixels, pixels, TextureFormat.RGBA32, false) { name = "PalaceMod thumb " + b.Name, hideFlags = HideFlags.DontUnloadUnusedAsset };
                tex.ReadPixels(new Rect(0, 0, pixels, pixels), 0, 0);
                tex.Apply(false, false);
                return tex;
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Scenery library: couldn't draw {b.Name}: {e.Message}");
                return null;
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderTexture.active = active;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (camObj != null) UnityEngine.Object.Destroy(camObj);
                if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
            }
        }
    }
}
