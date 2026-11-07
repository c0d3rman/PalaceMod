using System;
using UnityEngine;

/*
    A picture of the level from straight above, for maps in mod menus (ModMap): an orthographic camera with the main
    camera's settings renders the area once into a RenderTexture, turned so that "up" in the picture is the way the
    player's camera looks (what the player calls north). Fog is switched off for the shot. Works while the game is
    paused. The texture belongs to the caller: Release() it when the menu goes away.

        var shot = LevelCapture.Take(center, halfWidth: 60f, halfHeight: 40f, pixelWidth: 1400);
        Vector2 uv = shot.ToUV(slot.transform.position);   // 0..1 across the picture, (0,0) bottom left
*/
namespace PalaceMod
{
    public sealed class LevelShot
    {
        public RenderTexture Texture;
        public Vector3 Center, Right, Up;
        public float HalfWidth, HalfHeight;

        /// <summary>Where a world position falls in the picture: 0..1 from the bottom left.</summary>
        public Vector2 ToUV(Vector3 world)
        {
            var d = world - Center;
            return new Vector2(0.5f + Vector3.Dot(d, Right) / (2f * HalfWidth), 0.5f + Vector3.Dot(d, Up) / (2f * HalfHeight));
        }

        public void Release()
        {
            if (Texture == null) return;
            Texture.Release();
            UnityEngine.Object.Destroy(Texture);
            Texture = null;
        }
    }

    public static class LevelCapture
    {
        /// <summary>The player's view directions flattened onto the ground: right and "north" (away from the camera).</summary>
        public static void Orientation(out Vector3 right, out Vector3 up)
        {
            right = Vector3.right;
            up = Vector3.forward;
            var cam = Camera.main;
            if (cam == null) return;
            var f = new Vector3(cam.transform.forward.x, 0f, cam.transform.forward.z);
            var r = new Vector3(cam.transform.right.x, 0f, cam.transform.right.z);
            if (f.sqrMagnitude < 1e-4f || r.sqrMagnitude < 1e-4f) return;
            up = f.normalized;
            right = r.normalized;
        }

        /// <summary>Renders the level around <paramref name="center"/> (world units each side) from above. Null if
        /// there's no main camera to copy, or rendering failed (logged).</summary>
        public static LevelShot Take(Vector3 center, float halfWidth, float halfHeight, int pixelWidth)
        {
            var main = Camera.main;
            if (main == null || halfWidth <= 0f || halfHeight <= 0f) return null;
            Orientation(out var right, out var up);
            int w = Mathf.Clamp(pixelWidth, 64, 4096), h = Mathf.Clamp(Mathf.RoundToInt(pixelWidth * halfHeight / halfWidth), 64, 4096);
            var shot = new LevelShot { Center = center, Right = right, Up = up, HalfWidth = halfWidth, HalfHeight = halfHeight };
            GameObject obj = null;
            bool fog = RenderSettings.fog;
            try
            {
                shot.Texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "PalaceMod LevelCapture", antiAliasing = 1 };
                shot.Texture.Create();
                obj = new GameObject("PalaceMod LevelCapture Camera");
                obj.SetActive(false);
                var cam = obj.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = halfHeight;
                cam.aspect = halfWidth / halfHeight;
                cam.nearClipPlane = 1f;
                cam.farClipPlane = 1000f;
                cam.cullingMask = main.cullingMask & ~(1 << 5); // no UI layer
                cam.targetTexture = shot.Texture;
                obj.transform.position = center + Vector3.up * 400f;
                obj.transform.rotation = Quaternion.LookRotation(Vector3.down, up);
                RenderSettings.fog = false;
                cam.Render();
                return shot;
            }
            catch (Exception e)
            {
                Log.Core.Warn($"Couldn't take a picture of the level: {e.Message}");
                shot.Release();
                return null;
            }
            finally
            {
                RenderSettings.fog = fog;
                if (obj != null) UnityEngine.Object.Destroy(obj);
            }
        }

        /// <summary>Saves a shot as PNG (for dev commands).</summary>
        internal static void SavePng(LevelShot shot, string path)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = shot.Texture;
                var tex = new Texture2D(shot.Texture.width, shot.Texture.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
            finally { RenderTexture.active = previous; }
        }
    }
}
