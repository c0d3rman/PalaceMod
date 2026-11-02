using System.Collections.Generic;
using UnityEngine;

/*
    Ground markers for patrols: a ring at each point and a dashed line along the route, draped over the ground. Points
    being dropped (still pending) are white, with a line from the last one to the king; active routes are a soft gold.
    Geometry is rebuilt only when the points change; the fade (faint at night) is just the material colour.
*/
namespace PalaceMod
{
    internal sealed class PatrolMarkers
    {
        private static readonly Color RouteColor = new(1f, 0.85f, 0.45f, 0.75f);
        private static readonly Color PendingColor = new(1f, 1f, 1f, 0.9f);
        private const float RingSize = 1.6f;
        private const float LineWidth = 0.22f;
        private const float Lift = 0.1f;
        private const float SampleStep = 1.5f;

        private GameObject root;
        private Material ringMaterial, pendingRingMaterial, lineMaterial, pendingLineMaterial;
        private Mesh quad;
        private readonly List<GameObject> rings = [];
        private readonly List<LineRenderer> lines = [];
        private LineRenderer kingLine;
        private int builtHash;
        private readonly List<Vector3> samples = [];

        public void Show(List<PatrolManager.Route> routes, List<Vector3> pending, Vector3? king, bool pendingLoop, float alpha)
        {
            if (root == null) Build();
            root.SetActive(true);
            SetAlpha(ringMaterial, RouteColor, alpha);
            SetAlpha(lineMaterial, RouteColor, alpha);
            SetAlpha(pendingRingMaterial, PendingColor, alpha);
            SetAlpha(pendingLineMaterial, PendingColor, alpha);

            int hash = Hash(routes, pending);
            if (hash != builtHash)
            {
                builtHash = hash;
                Rebuild(routes, pending);
            }

            bool showKing = king.HasValue && pending.Count > 0;
            kingLine.gameObject.SetActive(showKing);
            if (showKing) Drape(kingLine, pending[pending.Count - 1], king.Value);
        }

        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        public void Destroy()
        {
            if (root != null) Object.Destroy(root);
            foreach (var material in new[] { ringMaterial, pendingRingMaterial, lineMaterial, pendingLineMaterial })
            {
                if (material == null) continue;
                Object.Destroy(material.mainTexture);
                Object.Destroy(material);
            }
            if (quad != null) Object.Destroy(quad);
            rings.Clear();
            lines.Clear();
            root = null;
            kingLine = null;
            builtHash = 0;
        }

        private static void SetAlpha(Material material, Color color, float alpha) =>
            material.color = new Color(color.r, color.g, color.b, color.a * alpha);

        private static int Hash(List<PatrolManager.Route> routes, List<Vector3> pending)
        {
            unchecked
            {
                int h = 17;
                foreach (var route in routes)
                {
                    h = h * 31 + (route.Loop ? 1 : 2);
                    foreach (var p in route.Points) h = h * 31 + p.GetHashCode();
                }
                h = h * 31 + 7;
                foreach (var p in pending) h = h * 31 + p.GetHashCode();
                return h == 0 ? 1 : h;
            }
        }

        private void Rebuild(List<PatrolManager.Route> routes, List<Vector3> pending)
        {
            int ring = 0, line = 0;
            foreach (var route in routes)
            {
                var points = route.Points;
                for (int i = 0; i < points.Count; i++)
                {
                    PlaceRing(ring++, points[i], ringMaterial);
                    if (i + 1 < points.Count) Drape(Line(line++, lineMaterial), points[i], points[i + 1]);
                }
                if (route.Loop && points.Count > 2) Drape(Line(line++, lineMaterial), points[points.Count - 1], points[0]);
            }
            for (int i = 0; i < pending.Count; i++)
            {
                PlaceRing(ring++, pending[i], pendingRingMaterial);
                if (i + 1 < pending.Count) Drape(Line(line++, pendingLineMaterial), pending[i], pending[i + 1]);
            }
            for (int i = ring; i < rings.Count; i++) rings[i].SetActive(false);
            for (int i = line; i < lines.Count; i++) lines[i].gameObject.SetActive(false);
        }

        private void PlaceRing(int index, Vector3 position, Material material)
        {
            while (rings.Count <= index) rings.Add(CreateRing());
            var go = rings[index];
            go.SetActive(true);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.transform.position = position + Vector3.up * Lift;
        }

        private LineRenderer Line(int index, Material material)
        {
            while (lines.Count <= index) lines.Add(CreateLine("Route"));
            var lr = lines[index];
            lr.gameObject.SetActive(true);
            lr.sharedMaterial = material;
            return lr;
        }

        /// <summary>Lays a line from a to b over the ground, stopping short of the rings at both ends.</summary>
        private void Drape(LineRenderer lr, Vector3 a, Vector3 b)
        {
            samples.Clear();
            Vector3 flat = new(b.x - a.x, 0f, b.z - a.z);
            float length = flat.magnitude;
            float gap = RingSize * 0.5f;
            if (length <= gap * 2f)
            {
                lr.positionCount = 0;
                return;
            }
            int steps = Mathf.Max(1, Mathf.CeilToInt((length - 2f * gap) / SampleStep));
            for (int i = 0; i <= steps; i++)
            {
                float t = (gap + (length - 2f * gap) * i / steps) / length;
                Vector3 p = Vector3.Lerp(a, b, t);
                if (i > 0 && i < steps && AstarPath.active != null)
                {
                    // Follow the ground between the points (hills, ramps).
                    var nearest = AstarPath.active.GetNearest(p).position;
                    if (new Vector2(nearest.x - p.x, nearest.z - p.z).magnitude < 1.5f) p.y = nearest.y;
                }
                samples.Add(p + Vector3.up * Lift);
            }
            lr.positionCount = samples.Count;
            lr.SetPositions(samples.ToArray());
        }

        private void Build()
        {
            root = new GameObject("PalaceMod Patrol Markers");
            Object.DontDestroyOnLoad(root);
            quad = FlatQuad();
            var shader = Shader.Find("Sprites/Default");
            
            ringMaterial = new Material(shader) { mainTexture = RingTexture(), renderQueue = 3100 };
            pendingRingMaterial = new Material(shader) { mainTexture = RingTexture(), renderQueue = 3100 };
            lineMaterial = new Material(shader) { mainTexture = DashTexture(), renderQueue = 3100 };
            pendingLineMaterial = new Material(shader) { mainTexture = DashTexture(), renderQueue = 3100 };
            kingLine = CreateLine("To king");
            kingLine.sharedMaterial = pendingLineMaterial;
        }

        private GameObject CreateRing()
        {
            var go = new GameObject("Point");
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = Vector3.one * RingSize;
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private LineRenderer CreateLine(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = LineWidth;
            lr.textureMode = LineTextureMode.Tile;
            lr.alignment = LineAlignment.TransformZ;
            // Lie flat: TransformZ alignment faces the line along the object's Z axis, so point Z up.
            go.transform.rotation = Quaternion.LookRotation(Vector3.up);
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.positionCount = 0;
            return lr;
        }

        private static Mesh FlatQuad()
        {
            var mesh = new Mesh { name = "PalaceMod patrol quad" };
            mesh.vertices = [new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f)];
            mesh.uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            mesh.triangles = [0, 2, 1, 2, 3, 1];
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private const int TextureSize = 64;

        // A thin ring with a small solid dot in the middle: reads as a waypoint, distinct from placement rings.
        private static Texture2D RingTexture() => Paint(TextureSize, TextureSize, (x, y) =>
        {
            float d = new Vector2(x - 0.5f, y - 0.5f).magnitude * 2f;
            float ring = 1f - Mathf.Clamp01((Mathf.Abs(d - 0.82f) - 0.05f) / 0.07f);
            float dot = 1f - Mathf.Clamp01((d - 0.18f) / 0.06f);
            return Mathf.Max(ring, dot);
        }, TextureWrapMode.Clamp);

        // One dash and one gap along u, soft across v; Tile mode repeats it once per world unit.
        private static Texture2D DashTexture() => Paint(64, 16, (u, v) =>
        {
            float dash = u < 0.55f ? 1f : 0f;
            float edge = 1f - Mathf.Clamp01((Mathf.Abs(v - 0.5f) * 2f - 0.6f) / 0.4f);
            return dash * edge;
        }, TextureWrapMode.Repeat);

        private static Texture2D Paint(int width, int height, System.Func<float, float, float> alpha, TextureWrapMode wrap)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = wrap };
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float a = Mathf.Clamp01(alpha((x + 0.5f) / width, (y + 0.5f) / height));
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
