using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/*
    A map of the level for a mod menu: a picture of it from straight above (LevelCapture), turned like the player's
    camera, with selectable markers (ModTiles) on world positions and plain dots for things that can't be picked.

        var map = ModMap.Create(parent, new Vector2(900, 520), worldPoints);       // frames the points, takes the picture
        var marker = map.AddMarker("Slot 12", slot.transform.position, 28f, icon);  // a ModTile centred on the spot
        map.AddDot(other.transform.position, 6f, ModStyle.Dim);
        map.Wire(edges);                                                            // arrows move between markers

    The picture is released when the map is destroyed. Without a main camera the map is a plain panel (markers still
    work).
*/
namespace PalaceMod
{
    public sealed class ModMap
    {
        public RectTransform Root { get; private set; }
        public LevelShot Shot { get; private set; }
        public Vector2 Size { get; private set; }
        public readonly List<ModTile> Markers = [];
        private RectTransform layer;

        /// <summary>A map of <paramref name="size"/> at the top left of <paramref name="parent"/>, framing
        /// <paramref name="points"/> (world positions) with a margin.</summary>
        public static ModMap Create(Transform parent, Vector2 size, IEnumerable<Vector3> points, float margin = 6f, float dim = 0.72f)
        {
            var map = new ModMap { Size = size };
            var obj = new GameObject("Map", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
            obj.transform.SetParent(parent, false);
            map.Root = (RectTransform)obj.transform;
            map.Root.anchorMin = map.Root.anchorMax = map.Root.pivot = new Vector2(0f, 1f);
            map.Root.sizeDelta = size;
            var back = obj.GetComponent<Image>();
            back.color = new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.95f);
            back.raycastTarget = false;

            LevelCapture.Orientation(out var right, out var up);
            var list = points.ToList();
            Vector3 center = Vector3.zero;
            float halfW = 30f, halfH = 30f;
            if (list.Count > 0)
            {
                float minX = list.Min(p => Vector3.Dot(p, right)), maxX = list.Max(p => Vector3.Dot(p, right));
                float minY = list.Min(p => Vector3.Dot(p, up)), maxY = list.Max(p => Vector3.Dot(p, up));
                halfW = (maxX - minX) / 2f + margin;
                halfH = (maxY - minY) / 2f + margin;
                float height = list.Average(p => p.y);
                center = right * ((minX + maxX) / 2f) + up * ((minY + maxY) / 2f) + Vector3.up * height;
            }
            // Match the panel's shape: widen whichever side is short.
            float aspect = size.x / Mathf.Max(1f, size.y);
            if (halfW / halfH < aspect) halfW = halfH * aspect;
            else halfH = halfW / aspect;
            // The frame is fixed now (markers need it); the picture is taken on the next Update: rendered while
            // a menu is being built, a camera's output comes out black.
            map.Shot = new LevelShot { Center = center, Right = right, Up = up, HalfWidth = halfW, HalfHeight = halfH };
            var pic = new GameObject("Picture", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            pic.transform.SetParent(map.Root, false);
            var raw = pic.GetComponent<RawImage>();
            raw.color = new Color(dim, dim, dim, 1f);
            raw.raycastTarget = false;
            raw.enabled = false;
            Stretch((RectTransform)pic.transform);
            var painter = obj.AddComponent<Painter>();
            painter.Map = map;
            painter.Picture = raw;
            painter.Pixels = Mathf.RoundToInt(Mathf.Min(2048f, size.x * 2f));
            var layerObj = new GameObject("Markers", typeof(RectTransform));
            layerObj.transform.SetParent(map.Root, false);
            map.layer = (RectTransform)layerObj.transform;
            Stretch(map.layer);
            return map;
        }

        /// <summary>Where a world position is on the map, from its top left (x right, y down negative).</summary>
        public Vector2 Position(Vector3 world)
        {
            var uv = Shot.ToUV(world);
            return new Vector2(uv.x * Size.x, (uv.y - 1f) * Size.y);
        }

        public bool Contains(Vector3 world)
        {
            var uv = Shot.ToUV(world);
            return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        }

        /// <summary>A selectable marker centred on <paramref name="world"/>.</summary>
        public ModTile AddMarker(string key, Vector3 world, float size, Sprite icon = null, string text = null)
        {
            var tile = ModTile.Create(layer, $"Marker {key}", new Vector2(size, size), icon, text, size * 0.55f);
            tile.Key = key;
            tile.Place(Position(world) + new Vector2(-size / 2f, size / 2f));
            tile.gameObject.AddComponent<NearestHit>().Map = this;
            Markers.Add(tile);
            return tile;
        }

        /// <summary>A plain dot (not selectable).</summary>
        public Image AddDot(Vector3 world, float size, Color color, bool diamond = true)
        {
            var obj = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(layer, false);
            var image = obj.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Position(world);
            float side = diamond ? size / Mathf.Sqrt(2f) : size;
            rect.sizeDelta = new Vector2(side, side);
            if (diamond) rect.localRotation = Quaternion.Euler(0f, 0f, 45f);
            obj.transform.SetAsFirstSibling(); // under the markers
            return image;
        }

        /// <summary>Arrows move to the nearest marker that way; past the last, to <paramref name="edges"/> (left,
        /// right, up, down).</summary>
        public void Wire(ThronefallUIElement[] edges = null) =>
            ModNav.Spatial(Markers, m => m.Rect.anchoredPosition, edges);

        /// <summary>The marker whose centre is nearest a screen point, among those under it (null: none).</summary>
        public ModTile MarkerAt(Vector2 screen, Camera cam = null)
        {
            ModTile best = null;
            float bestDistance = float.MaxValue;
            foreach (var m in Markers)
            {
                if (m == null || !m.isActiveAndEnabled || !RectTransformUtility.RectangleContainsScreenPoint(m.Rect, screen, cam)) continue;
                var corners = new Vector3[4];
                m.Rect.GetWorldCorners(corners);
                Vector2 center = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[2]) / 2f);
                float d = (center - screen).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = m; }
            }
            return best;
        }

        // Markers of slots close together overlap; the game's menus take the topmost one under the pointer, which isn't
        // the one the player aimed at. Each marker only takes the pointer where it's the nearest.
        private class NearestHit : MonoBehaviour, ICanvasRaycastFilter
        {
            public ModMap Map;

            public bool IsRaycastLocationValid(Vector2 screen, Camera cam) => Map == null || Map.MarkerAt(screen, cam)?.gameObject == gameObject;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // Takes the picture once, and releases it with the map.
        private class Painter : MonoBehaviour
        {
            public ModMap Map;
            public RawImage Picture;
            public int Pixels;
            private LevelShot taken;

            private void Update()
            {
                if (taken != null || Map == null) { enabled = false; return; }
                var s = Map.Shot;
                taken = LevelCapture.Take(s.Center, s.HalfWidth, s.HalfHeight, Pixels);
                enabled = false;
                if (taken == null || Picture == null) return;
                s.Texture = taken.Texture;
                Picture.texture = taken.Texture;
                Picture.enabled = true;
            }

            private void OnDestroy() => taken?.Release();
        }
    }
}
