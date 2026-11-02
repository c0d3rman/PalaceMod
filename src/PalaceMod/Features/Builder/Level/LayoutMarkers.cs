using System.Collections.Generic;
using UnityEngine;

/*
    Ground markers for the layout editor: a flat ring per editable object (coloured by what it is and whether it's
    selected), a direction tick for its facing, and polylines for spawn lines and checked enemy routes. Everything
    lives under one root in the host scene, so it goes away with the map.
*/
namespace PalaceMod.Builder.Level
{
    internal sealed class LayoutMarkers
    {
        public static readonly Color NativeColor = new(0.784f, 0.651f, 0.455f, 0.95f);   // ModStyle.Gold
        public static readonly Color AddedColor = new(0.42f, 0.78f, 0.69f, 0.95f);       // ModStyle.Accent
        public static readonly Color PropColor = new(0.62f, 0.72f, 0.55f, 0.85f);
        public static readonly Color LineColor = new(0.86f, 0.36f, 0.30f, 0.95f);
        public static readonly Color RemovedColor = new(0.86f, 0.30f, 0.26f, 0.55f);
        public static readonly Color LockedColor = new(0.55f, 0.50f, 0.42f, 0.8f);
        public static readonly Color SelectedColor = new(1f, 0.95f, 0.82f, 1f);
        public static readonly Color RouteOk = new(0.42f, 0.78f, 0.69f, 0.9f);
        public static readonly Color RouteBlocked = new(0.95f, 0.35f, 0.3f, 0.95f);

        private const float Lift = 0.12f;
        private readonly Transform root;
        private readonly Mesh quad;
        private readonly Shader shader;
        private readonly Texture2D ringTexture, tickTexture, crossTexture;
        private readonly Dictionary<Color, Material> ringMaterials = [], tickMaterials = [], crossMaterials = [], lineMaterials = [];
        private readonly Dictionary<string, Marker> markers = [];
        private readonly List<LineRenderer> routes = [];

        private sealed class Marker
        {
            public GameObject Ring, Tick, Cross;
            public LineRenderer Line;
        }

        public LayoutMarkers(Transform parent)
        {
            root = new GameObject("PalaceBuilder markers").transform;
            root.SetParent(parent, false);
            quad = FlatQuad();
            shader = Shader.Find("Sprites/Default");
            ringTexture = Paint((x, y) =>
            {
                float d = new Vector2(x - 0.5f, y - 0.5f).magnitude * 2f;
                float ring = 1f - Mathf.Clamp01((Mathf.Abs(d - 0.86f) - 0.05f) / 0.06f);
                return Mathf.Max(ring, d < 0.86f ? 0.16f : 0f);
            });
            tickTexture = Paint((x, y) =>
            {
                float cx = Mathf.Abs(x - 0.5f);
                float tip = 0.95f - cx * 1.6f;
                return y <= tip && y >= tip - 0.3f && y > 0.35f ? 1f : 0f;
            });
            crossTexture = Paint((x, y) =>
            {
                float a = Mathf.Abs((x - 0.5f) - (y - 0.5f)), b = Mathf.Abs((x - 0.5f) + (y - 0.5f));
                float r = new Vector2(x - 0.5f, y - 0.5f).magnitude;
                return (a < 0.06f || b < 0.06f) && r < 0.4f ? 1f : 0f;
            });
        }

        /// <summary>Shows or updates the marker of one object.</summary>
        public void Set(string key, Vector3 pos, float yaw, float size, Color color, bool removed, List<Vector3> polyline = null)
        {
            if (!markers.TryGetValue(key, out var m))
            {
                m = new Marker
                {
                    Ring = Quad("ring " + key),
                    Tick = Quad("tick " + key),
                    Cross = Quad("cross " + key),
                };
                markers[key] = m;
            }
            m.Ring.GetComponent<MeshRenderer>().sharedMaterial = Material(ringMaterials, ringTexture, color);
            m.Tick.GetComponent<MeshRenderer>().sharedMaterial = Material(tickMaterials, tickTexture, color);
            m.Cross.GetComponent<MeshRenderer>().sharedMaterial = Material(crossMaterials, crossTexture, color);
            var p = pos + Vector3.up * Lift;
            m.Ring.transform.SetPositionAndRotation(p, Quaternion.identity);
            m.Ring.transform.localScale = Vector3.one * size;
            m.Tick.transform.SetPositionAndRotation(p + Vector3.up * 0.01f, Quaternion.Euler(0f, yaw, 0f));
            m.Tick.transform.localScale = Vector3.one * size;
            m.Tick.SetActive(!removed);
            m.Cross.transform.SetPositionAndRotation(p + Vector3.up * 0.02f, Quaternion.identity);
            m.Cross.transform.localScale = Vector3.one * size;
            m.Cross.SetActive(removed);
            if (polyline != null && polyline.Count > 1)
            {
                m.Line ??= NewLine("line " + key);
                m.Line.sharedMaterial = Material(lineMaterials, null, color);
                m.Line.positionCount = polyline.Count;
                for (int i = 0; i < polyline.Count; i++) m.Line.SetPosition(i, polyline[i] + Vector3.up * (Lift + 0.3f));
                m.Line.widthMultiplier = 0.6f;
                m.Line.gameObject.SetActive(true);
            }
            else if (m.Line != null) m.Line.gameObject.SetActive(false);
        }

        public void Remove(string key)
        {
            if (!markers.TryGetValue(key, out var m)) return;
            Object.Destroy(m.Ring);
            Object.Destroy(m.Tick);
            Object.Destroy(m.Cross);
            if (m.Line != null) Object.Destroy(m.Line.gameObject);
            markers.Remove(key);
        }

        public IEnumerable<string> Keys => new List<string>(markers.Keys);

        /// <summary>Draws checked enemy routes (cleared by passing an empty list).</summary>
        public void ShowRoutes(List<PathCheck.Route> list)
        {
            foreach (var r in routes) Object.Destroy(r.gameObject);
            routes.Clear();
            foreach (var route in list)
            {
                if (route.Points.Count < 2) continue;
                var lr = NewLine("route " + route.Line);
                lr.sharedMaterial = Material(lineMaterials, null, route.Reachable ? RouteOk : RouteBlocked);
                lr.positionCount = route.Points.Count;
                for (int i = 0; i < route.Points.Count; i++) lr.SetPosition(i, route.Points[i] + Vector3.up * 0.5f);
                lr.widthMultiplier = 0.35f;
                routes.Add(lr);
            }
        }

        public void Destroy()
        {
            if (root != null) Object.Destroy(root.gameObject);
            foreach (var dict in new[] { ringMaterials, tickMaterials, crossMaterials, lineMaterials })
            {
                foreach (var m in dict.Values) Object.Destroy(m);
                dict.Clear();
            }
            Object.Destroy(ringTexture);
            Object.Destroy(tickTexture);
            Object.Destroy(crossTexture);
            Object.Destroy(quad);
        }

        private Material Material(Dictionary<Color, Material> cache, Texture2D texture, Color color)
        {
            if (cache.TryGetValue(color, out var m)) return m;
            m = new Material(shader) { color = color, renderQueue = 3100 };
            if (texture != null) m.mainTexture = texture;
            return cache[color] = m;
        }

        private GameObject Quad(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        private LineRenderer NewLine(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.numCornerVertices = 2;
            lr.numCapVertices = 2;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private static Mesh FlatQuad()
        {
            var mesh = new Mesh { name = "PalaceBuilder flat quad" };
            mesh.vertices = [new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f)];
            mesh.uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            mesh.triangles = [0, 2, 1, 2, 3, 1];
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Texture2D Paint(System.Func<float, float, float> alpha)
        {
            const int size = 96;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha((x + 0.5f) / size, (y + 0.5f) / size)) * 255));
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
