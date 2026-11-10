using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    Map thumbnails for the start popup. A map that isn't loaded can't be pictured, and LevelInfo has no preview
    picture, so each map's thumbnail is captured from above around its castle the first time it's played without a custom
    level (LevelCapture) and cached as
    BepInEx/PalaceBuilder/cache/thumbs/<scene>.png. Maps without one show their name only.
*/
namespace PalaceMod.Builder.Studio.Start
{
    internal static class Thumbnails
    {
        private static readonly Dictionary<string, Sprite> loaded = new(StringComparer.Ordinal);

        public static string Folder => Path.Combine(Paths.BepInExRootPath, "PalaceBuilder", "cache", "thumbs");
        private static string FileFor(string scene) => Path.Combine(Folder, scene + ".png");

        /// <summary>The cached thumbnail of a map, or null.</summary>
        public static Sprite Get(string scene)
        {
            if (scene == null) return null;
            if (loaded.TryGetValue(scene, out var s) && s != null) return s;
            string file = FileFor(scene);
            if (!File.Exists(file)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!tex.LoadImage(File.ReadAllBytes(file))) return null;
                tex.wrapMode = TextureWrapMode.Clamp;
                return loaded[scene] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Couldn't read the thumbnail {file}: {e.Message}");
                return null;
            }
        }

        /// <summary>A CPU-readable copy of a (GPU-only) texture.</summary>
        private static Texture2D Readable(Texture2D source)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public static bool Has(string scene) => scene != null && File.Exists(FileFor(scene));

        /// <summary>Pictures the loaded map around its castle and caches it (once per map unless forced).</summary>
        public static bool Capture(string scene, bool force = false)
        {
            if (scene == null || (!force && Has(scene))) return false;
            var s = LayoutScene.Host(scene);
            if (!s.IsValid() || !s.isLoaded) return false;
            var castle = LayoutScene.Castle(s);
            if (castle == null) return false;
            LevelShot shot = null;
            try
            {
                shot = LevelCapture.Take(castle.transform.position, 70f, 46f, 384);
                if (shot?.Texture == null) return false;
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(FileFor(scene), Readable(shot.Texture).EncodeToPNG());
                loaded.Remove(scene);
                Log.For<BuilderManager>().Info($"thumbnail of {scene} cached");
                return true;
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Couldn't capture a thumbnail of {scene}: {e.Message}");
                return false;
            }
            finally
            {
                shot?.Release();
            }
        }
    }
}
