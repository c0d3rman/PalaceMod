using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.SceneManagement;
using static PalaceMod.Builder.Agent.AgentCommands;

/*
    `agent capture <file> [pixels]`: the whole loaded level from straight above as a PNG, north-up in world terms
    (+x to the right, +z up the picture), with no UI. The result gives the world rectangle it covers, so palace.py can
    draw slots, labels and routes on top. The area is the slots, spawn lines and castle plus a margin.
*/
namespace PalaceMod.Builder.Agent
{
    internal static class MapCapture
    {
        public static JObject Take(string path, int pixels)
        {
            var scene = EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : SceneManager.GetActiveScene();
            var main = Camera.main ?? throw new InvalidOperationException("no camera (load a level first)");
            var points = LayoutScene.Slots(scene).Where(s => s.gameObject.activeInHierarchy || s.StartDeactivated).Select(s => s.transform.position)
                .Concat(LayoutScene.Lines(scene).Select(l => LayoutScene.LineCenter(l.transform))).ToList();
            if (points.Count == 0) throw new InvalidOperationException("nothing on the map to frame");
            float minX = points.Min(p => p.x) - 20f, maxX = points.Max(p => p.x) + 20f;
            float minZ = points.Min(p => p.z) - 20f, maxZ = points.Max(p => p.z) + 20f;
            float halfW = (maxX - minX) / 2f, halfH = (maxZ - minZ) / 2f;
            var center = new Vector3((minX + maxX) / 2f, points.Average(p => p.y), (minZ + maxZ) / 2f);
            int w = pixels, h = Mathf.Clamp(Mathf.RoundToInt(pixels * halfH / halfW), 64, 4096);

            GameObject obj = null;
            RenderTexture target = null;
            Texture2D tex = null;
            var active = RenderTexture.active;
            bool fog = RenderSettings.fog;
            try
            {
                target = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
                obj = new GameObject("PalaceMod Agent Capture Camera");
                obj.SetActive(false);
                var cam = obj.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = halfH;
                cam.aspect = halfW / halfH;
                cam.nearClipPlane = 1f;
                cam.farClipPlane = 1000f;
                cam.cullingMask = main.cullingMask & ~(1 << 5); // no UI
                cam.targetTexture = target;
                obj.transform.position = center + Vector3.up * 400f;
                obj.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                RenderSettings.fog = false;
                cam.Render();
                RenderTexture.active = target;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderTexture.active = active;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (obj != null) UnityEngine.Object.Destroy(obj);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            return new JObject
            {
                ["written"] = path,
                ["pixels"] = new JArray(w, h),
                // World rectangle: x from minX (left edge) to maxX, z from minZ (bottom edge) to maxZ.
                ["world"] = new JObject { ["minX"] = R(center.x - halfW), ["maxX"] = R(center.x + halfW), ["minZ"] = R(center.z - halfH), ["maxZ"] = R(center.z + halfH) },
            };
        }
    }
}
