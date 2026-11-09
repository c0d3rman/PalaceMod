using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    A colour picker in a ModPopup: a saturation/brightness square and a hue bar (click or drag), the old and new colour
    side by side, a hex field, the game's palette (the level's colour scheme: enemy, ally, player, building, ground,
    tree, rock colours) and the colours the loaded map uses (its materials' base colours, grouped and sorted by hue).

        ModColorPicker.Open("Body colour", current, c => Edit(c), near: row.Button.transform as RectTransform);

    changed gets every colour as it changes (drags included; coalesce undo steps on the caller's side), and null for
    "none" when allowNone is set. The picker's colour maths (ColorMath) is plain C# and unit-tested.
*/
namespace PalaceMod
{
    public static class ModColorPicker
    {
        public sealed class Swatch
        {
            public string Name;
            public Color Color;
            public Swatch(string name, Color color) { Name = name; Color = color; }
        }

        private const float Square = 230f, Bar = 34f, Gap = 14f, SwatchSize = 34f, SwatchGap = 6f;

        /// <summary>Opens the picker. Returns the popup (null without a UI).</summary>
        public static ModPopup Open(string title, Color? current, Action<Color?> changed, RectTransform near = null, bool allowNone = true,
            IEnumerable<Swatch> extra = null, string noneLabel = "None")
        {
            var state = new State { Original = current, Changed = changed };
            Color.RGBToHSV(current ?? new Color(0.8f, 0.3f, 0.3f), out state.H, out state.S, out state.V);
            var extraList = extra?.ToList() ?? [];
            return ModPopup.Open(title, page => Build(page, state, allowNone, extraList, noneLabel), near, width: 600f);
        }

        private sealed class State
        {
            public float H, S, V;
            public Color? Original;
            public Action<Color?> Changed;
            public Texture2D SquareTex, BarTex;
            public RawImage SquareImage;
            public RectTransform SquareMark, BarMark;
            public Image NewSwatch;
            public ModField Hex;
            public Color Current => Color.HSVToRGB(H, S, V);
        }

        private static void Build(ModPage page, State st, bool allowNone, List<Swatch> extra, string noneLabel)
        {
            var area = page.Custom("Picker", Square);
            // Saturation (x) and brightness (y) for the hue.
            var sq = ModTile.Create(area, "Square", new Vector2(Square, Square));
            sq.Place(Vector2.zero);
            sq.Draggable = true;
            st.SquareTex = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "PalaceMod colour square" };
            st.SquareImage = Raw(sq.Rect, st.SquareTex);
            st.SquareMark = Marker(sq.Rect, 14f);
            void PickSquare(Vector2 screen)
            {
                var p = Local(sq.Rect, screen);
                st.S = p.x;
                st.V = p.y;
                Changed(st, true);
            }
            sq.Applied += () => PickSquare(ModTile.MousePosition);
            sq.Dragging += PickSquare;
            sq.Dropped += PickSquare;
            page.AddElement(sq);

            // Hue, top to bottom.
            var bar = ModTile.Create(area, "Hue", new Vector2(Bar, Square));
            bar.Place(new Vector2(Square + Gap, 0f));
            bar.Draggable = true;
            st.BarTex = new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "PalaceMod hue bar" };
            for (int y = 0; y < 64; y++) st.BarTex.SetPixel(0, y, Color.HSVToRGB(y / 63f, 1f, 1f));
            st.BarTex.Apply(false);
            Raw(bar.Rect, st.BarTex);
            st.BarMark = Marker(bar.Rect, 10f, horizontal: true);
            void PickHue(Vector2 screen)
            {
                st.H = Mathf.Clamp(Local(bar.Rect, screen).y, 0f, 0.999f);
                Changed(st, true);
            }
            bar.Applied += () => PickHue(ModTile.MousePosition);
            bar.Dragging += PickHue;
            bar.Dropped += PickHue;
            page.AddElement(bar);

            // Old over new.
            float sx = Square + Gap + Bar + Gap * 1.5f;
            Label(area, "Was", new Vector2(sx, 0f));
            var was = Plain(area, new Vector2(sx, -26f), new Vector2(110f, 60f), st.Original ?? Color.clear);
            if (st.Original == null) Label(was.rectTransform, Dim(noneLabel.ToLowerInvariant()), new Vector2(8f, -14f));
            Label(area, "Now", new Vector2(sx, -100f));
            st.NewSwatch = Plain(area, new Vector2(sx, -126f), new Vector2(110f, 60f), st.Current);

            st.Hex = page.TextInput("Hex", () => "#" + ColorUtility.ToHtmlStringRGB(st.Current).ToLowerInvariant(), text =>
            {
                if (!ColorMath.TryParseHex(text, out var c)) { Notify.Show($"\"{ModFields.TMPEscape(text)}\" isn't a colour like #8fd14f", NotifyKind.Warning, 3f, "modcolor"); return; }
                Color.RGBToHSV(c, out st.H, out st.S, out st.V);
                Changed(st, true);
            }, () => "Type a colour as #rrggbb (or rrggbb), then Enter.", 7);

            var palette = GamePalette().Concat(extra).ToList();
            if (palette.Count > 0) Swatches(page, "Palette", palette, st);
            var map = MapColours();
            if (map.Count > 0) Swatches(page, "From this map", map, st);
            if (allowNone) page.Button(noneLabel, () => { st.Changed?.Invoke(null); ModPopup.Current?.Close(); }, "Clears the colour.");
            Changed(st, false);
        }

        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";

        private static void Changed(State st, bool notify)
        {
            // The square: this hue at every saturation and brightness.
            if (st.SquareTex != null)
            {
                int n = st.SquareTex.width;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        st.SquareTex.SetPixel(x, y, Color.HSVToRGB(st.H, x / (n - 1f), y / (n - 1f)));
                st.SquareTex.Apply(false);
            }
            Place(st.SquareMark, st.S, st.V);
            Place(st.BarMark, 0.5f, st.H);
            if (st.SquareMark != null) st.SquareMark.GetComponent<Image>().color = st.V > 0.55f && st.S < 0.6f ? Color.black : Color.white;
            if (st.NewSwatch != null) st.NewSwatch.color = st.Current;
            st.Hex?.Refresh();
            if (!notify) return;
            try { st.Changed?.Invoke(st.Current); }
            catch (Exception e) { Log.Core.Caught("Colour picker: change failed", e); }
        }

        private static void Swatches(ModPage page, string title, List<Swatch> swatches, State st)
        {
            page.Text(Dim(title));
            float width = Mathf.Max(200f, page.Size.x > 1f ? Mathf.Min(page.Size.x, 548f) : 548f);
            int perRow = Mathf.Max(1, (int)((width + SwatchGap) / (SwatchSize + SwatchGap)));
            int rows = (swatches.Count + perRow - 1) / perRow;
            var area = page.Custom(title, rows * (SwatchSize + SwatchGap));
            for (int i = 0; i < swatches.Count; i++)
            {
                var s = swatches[i];
                var tile = ModTile.Create(area, $"{title} {i}", new Vector2(SwatchSize, SwatchSize));
                tile.Place(new Vector2(i % perRow * (SwatchSize + SwatchGap), -(i / perRow) * (SwatchSize + SwatchGap)));
                tile.SetFill(s.Color);
                tile.Key = "#" + ColorUtility.ToHtmlStringRGB(s.Color).ToLowerInvariant();
                ModTooltip.Attach(tile, s.Name, () => tile.Key);
                tile.Applied += () =>
                {
                    Color.RGBToHSV(s.Color, out st.H, out st.S, out st.V);
                    Changed(st, true);
                };
                page.AddElement(tile);
            }
        }

        // ---- Drawing helpers ------------------------------------------------------------------------------------------

        private static RawImage Raw(RectTransform parent, Texture tex)
        {
            var go = new GameObject("Gradient", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(4f, 4f);
            r.offsetMax = new Vector2(-4f, -4f);
            var img = go.GetComponent<RawImage>();
            img.texture = tex;
            img.raycastTarget = false;
            return img;
        }

        private static Image Plain(RectTransform parent, Vector2 topLeft, Vector2 size, Color color)
        {
            var go = new GameObject("Swatch", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = topLeft;
            r.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static void Label(RectTransform parent, string text, Vector2 topLeft)
        {
            var t = GameWidgets.Text(parent, "Label", text, ModStyle.BodyFont, 22f, ModStyle.Gold, TextAlignmentOptions.TopLeft);
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            var r = t.rectTransform;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = topLeft;
            r.sizeDelta = new Vector2(140f, 26f);
        }

        private static RectTransform Marker(RectTransform parent, float size, bool horizontal = false)
        {
            var go = new GameObject("Mark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = horizontal ? new Vector2(parent.sizeDelta.x + 6f, 5f) : new Vector2(size, size);
            var img = go.GetComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = false;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
            outline.effectDistance = new Vector2(1.5f, 1.5f);
            return r;
        }

        private static void Place(RectTransform mark, float x, float y)
        {
            if (mark == null) return;
            mark.anchorMin = mark.anchorMax = new Vector2(Mathf.Lerp(0.02f, 0.98f, x), Mathf.Lerp(0.02f, 0.98f, y));
            mark.anchoredPosition = Vector2.zero;
        }

        /// <summary>A screen point as 0..1 inside a rect (clamped).</summary>
        private static Vector2 Local(RectTransform rect, Vector2 screen)
        {
            var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screen, cam, out var local);
            var r = rect.rect;
            return new Vector2(Mathf.Clamp01((local.x - r.xMin - 4f) / Mathf.Max(1f, r.width - 8f)), Mathf.Clamp01((local.y - r.yMin - 4f) / Mathf.Max(1f, r.height - 8f)));
        }

        // ---- Colours of the game ----------------------------------------------------------------------------------

        /// <summary>The level's colour scheme (light and mid of each kind), or nothing outside a level.</summary>
        public static List<Swatch> GamePalette()
        {
            var list = new List<Swatch>();
            var manager = Object.FindObjectOfType<ColorAndLightManager>();
            var scheme = manager != null ? manager.CurrentColorScheme : null;
            if (scheme == null) return list;
            void Add(string name, Color c) { if (c.a > 0f && list.All(s => !Same(s.Color, c))) list.Add(new Swatch(name, c)); }
            Add("Enemy", scheme.enemyLightColor);
            Add("Enemy shade", scheme.enemyMidColor);
            Add("Ally", scheme.allyLightColor);
            Add("Ally shade", scheme.allyMidColor);
            Add("Ranged ally", scheme.allyRangedLightColor);
            Add("Ranged ally shade", scheme.allyRangedMidColor);
            Add("King", scheme.playerLightColor);
            Add("King shade", scheme.playerMidColor);
            Add("Cape", scheme.playerCapeLightColor);
            Add("Crown", scheme.playerCrownLightColor);
            Add("Horse", scheme.horseLightColor);
            Add("Building", scheme.buildingLightColor);
            Add("Building shade", scheme.buildingMidColor);
            Add("Ground", scheme.groundColor);
            Add("Earth", scheme.earthLightColor);
            Add("Sand", scheme.sandColor);
            Add("Water", scheme.waterLightColor);
            Add("Road", scheme.roadColor);
            Add("Tree", scheme.treeLightColor);
            Add("Rock", scheme.rockLightColor);
            Add("Coin", scheme.coinLightColor);
            Add("Shadow", scheme.globalShadowColor);
            return list;
        }

        private static int mapScene = -1;
        private static List<Swatch> mapColours = [];

        /// <summary>The base colours of the loaded map's materials (each colour once, sorted by hue), cached per scene.</summary>
        public static List<Swatch> MapColours()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return [];
            if (scene.handle == mapScene) return mapColours;
            mapScene = scene.handle;
            var found = new List<(string name, Color color)>();
            var seen = new HashSet<Material>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || r.gameObject.scene != scene || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    foreach (var prop in new[] { "_BaseColor", "_Color" })
                    {
                        if (!m.HasProperty(prop)) continue;
                        found.Add((m.name, m.GetColor(prop)));
                        break;
                    }
                }
            }
            mapColours = ColorMath.Distinct(found.Select(f => (f.name, f.color)), 0.06f, 40).Select(f => new Swatch(f.name, f.color)).ToList();
            return mapColours;
        }

        private static bool Same(Color a, Color b) => ColorMath.Distance(a, b) < 0.02f;
    }

    /// <summary>Colour helpers without UI (tests/PalaceMod.Tests covers them).</summary>
    public static class ColorMath
    {
        /// <summary>"#8fd14f", "8fd14f" or "#8fd" (short form).</summary>
        public static bool TryParseHex(string text, out Color color)
        {
            color = Color.black;
            string s = text?.Trim() ?? "";
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 3) s = new string([s[0], s[0], s[1], s[1], s[2], s[2]]);
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            color = new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
            return true;
        }

        public static float Distance(Color a, Color b) => Mathf.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));

        /// <summary>Drops colours closer than <paramref name="minDistance"/> to one already kept (opaque only), at most
        /// <paramref name="max"/>, sorted greys first, then by hue.</summary>
        public static List<(string name, Color color)> Distinct(IEnumerable<(string name, Color color)> colours, float minDistance, int max)
        {
            var kept = new List<(string name, Color color)>();
            foreach (var (name, c) in colours)
            {
                if (c.a < 0.5f) continue;
                var opaque = new Color(c.r, c.g, c.b, 1f);
                if (kept.Any(k => Distance(k.color, opaque) < minDistance)) continue;
                kept.Add((name, opaque));
                if (kept.Count >= max) break;
            }
            return kept.OrderBy(k => { Color.RGBToHSV(k.color, out float h, out float s, out float v); return s < 0.12f ? -1f + v * 0.5f : h + (1f - v) * 0.001f; }).ToList();
        }

        /// <summary>
        /// The shade (FlatKit's _ColorDim) for a new lit colour, keeping the source material's light-to-shade relation:
        /// the same hue turn, brightness ratio and saturation change. A grey stays grey.
        /// </summary>
        public static Color ShadeFor(Color target, Color sourceLight, Color sourceShade)
        {
            Color.RGBToHSV(sourceLight, out float hl, out float sl, out float vl);
            Color.RGBToHSV(sourceShade, out float hm, out float sm, out float vm);
            Color.RGBToHSV(target, out float h, out float s, out float v);
            float dh = Mathf.DeltaAngle(hl * 360f, hm * 360f) / 360f;
            // How much more saturated the shade is, applied in proportion to the target's own colourfulness.
            float ds = sm - sl;
            float shadeS = Mathf.Clamp01(s + ds * Mathf.Clamp01(s * 3f));
            float ratio = vl > 0.01f ? Mathf.Clamp(vm / vl, 0.2f, 1f) : 0.7f;
            float hue = (h + dh * Mathf.Clamp01(s * 3f) + 1f) % 1f;
            var c = Color.HSVToRGB(hue, shadeS, v * ratio);
            c.a = target.a;
            return c;
        }
    }
}
