using System.Collections.Generic;
using UnityEngine;

/*
    Ground markers showing where units will stand before a mouse placement is confirmed: a ring per unit and an arrow
    for the direction the formation faces. Markers are pooled and lie flat just above the ground.
*/
namespace PalaceMod
{
    internal sealed class PlacementPreview
    {
        private static readonly Color NormalColor = new(1f, 1f, 1f, 0.75f);
        private static readonly Color HoldColor = new(1f, 0.82f, 0.3f, 0.9f);
        private const float RingSize = 1.1f;
        private const float ArrowSize = 1.4f;
        private const float Lift = 0.08f;

        private readonly List<GameObject> rings = [];
        private GameObject root, arrow;
        private Material ringMaterial, arrowMaterial;
        private Mesh quad;

        public bool Visible => root != null && root.activeSelf;

        public void Show(List<FormationSlot> slots, Vector3 center, Vector3 forward, bool hold)
        {
            if (root == null) Build();
            root.SetActive(true);
            Color color = hold ? HoldColor : NormalColor;
            ringMaterial.color = color;
            arrowMaterial.color = color;

            while (rings.Count < slots.Count) rings.Add(CreateMarker("Ring", ringMaterial, RingSize));
            for (int i = 0; i < rings.Count; i++)
            {
                bool used = i < slots.Count;
                rings[i].SetActive(used);
                if (used) rings[i].transform.position = slots[i].Position + Vector3.up * Lift;
            }

            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            float front = 0f;
            foreach (var slot in slots) front = Mathf.Max(front, Vector3.Dot(slot.Position - center, forward));
            arrow.transform.SetPositionAndRotation(center + forward * (front + 1.5f) + Vector3.up * Lift, Quaternion.LookRotation(forward));
        }

        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        public void Destroy()
        {
            if (root != null) Object.Destroy(root);
            foreach (var material in new[] { ringMaterial, arrowMaterial })
            {
                if (material == null) continue;
                Object.Destroy(material.mainTexture);
                Object.Destroy(material);
            }
            if (quad != null) Object.Destroy(quad);
            rings.Clear();
            root = arrow = null;
            ringMaterial = arrowMaterial = null;
            quad = null;
        }

        private void Build()
        {
            root = new GameObject("PalaceMod Placement Preview");
            Object.DontDestroyOnLoad(root);
            quad = FlatQuad();
            var shader = Shader.Find("Sprites/Default");
            ringMaterial = new Material(shader) { mainTexture = RingTexture(), renderQueue = 3100 };
            arrowMaterial = new Material(shader) { mainTexture = ArrowTexture(), renderQueue = 3100 };
            arrow = CreateMarker("Arrow", arrowMaterial, ArrowSize);
        }

        private GameObject CreateMarker(string name, Material material, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        // A unit quad lying in the XZ plane, facing up, with +Z as "up" in texture space.
        private static Mesh FlatQuad()
        {
            var mesh = new Mesh { name = "PalaceMod flat quad" };
            mesh.vertices = [new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f)];
            mesh.uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            mesh.triangles = [0, 2, 1, 2, 3, 1];
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private const int TextureSize = 64;

        private static Texture2D RingTexture() => Paint((x, y) =>
        {
            float d = new Vector2(x - 0.5f, y - 0.5f).magnitude * 2f; // 0 at the center, 1 at the edge
            float ring = 1f - Mathf.Clamp01(Mathf.Abs(d - 0.78f) / 0.14f);
            float fill = d < 0.78f ? 0.18f : 0f;
            return Mathf.Max(ring, fill);
        });

        // A chevron pointing toward +y (texture up), which FlatQuad maps to the marker's forward.
        private static Texture2D ArrowTexture() => Paint((x, y) =>
        {
            float cx = Mathf.Abs(x - 0.5f);
            float tip = 0.9f - cx * 1.3f;           // upper edge of the chevron
            float inner = tip - 0.28f;              // lower edge
            return y <= tip && y >= inner && y > 0.15f ? 1f : 0f;
        });

        private static Texture2D Paint(System.Func<float, float, float> alpha)
        {
            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[TextureSize * TextureSize];
            for (int y = 0; y < TextureSize; y++)
                for (int x = 0; x < TextureSize; x++)
                {
                    float a = Mathf.Clamp01(alpha((x + 0.5f) / TextureSize, (y + 0.5f) / TextureSize));
                    pixels[y * TextureSize + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
