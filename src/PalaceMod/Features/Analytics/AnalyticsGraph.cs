using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    Small line and stacked-bar graphs drawn with plain UI Images, in the game's palette, for a ModFrame page
    (ModPage.Custom). Lines are rotated thin Images, the way the game's own end-screen GraphDrawer draws them.

        var area = page.Custom("Score graph", 300f);
        AnalyticsGraph.Bars(area, labels, new[] { baseSeries, timeSeries, protectionSeries }, projectedFrom: 5);

    Columns from `projectedFrom` on are projections: they sit on a faintly lit band, drawn dimmer and thinner.
*/
namespace PalaceMod
{
    public sealed class GraphSeries
    {
        public string Label;
        public Color Color;
        public float[] Values;

        public GraphSeries(string label, Color color, float[] values)
        {
            Label = label;
            Color = color;
            Values = values;
        }
    }

    public static class AnalyticsGraph
    {
        public const float Width = 820f;
        private const float PadLeft = 74f, PadRight = 18f, PadTop = 46f, PadBottom = 34f;
        private const float ProjectedAlpha = 0.45f;

        // Graph palette: the game's golds, plus a muted copper for spending.
        public static readonly Color Bright = ModStyle.Bright;
        public static readonly Color Gold = ModStyle.Gold;
        public static readonly Color Bronze = new(0.62f, 0.45f, 0.24f);
        public static readonly Color Copper = new(0.80f, 0.45f, 0.32f);
        public static readonly Color Dim = ModStyle.Dim;

        /// <summary>Lines, one per series; points are joined in order and NaN values break the line.</summary>
        public static void Lines(RectTransform area, string[] xLabels, IList<GraphSeries> series, int projectedFrom)
        {
            float max = 0f;
            foreach (var s in series)
                foreach (var v in s.Values)
                    if (!float.IsNaN(v)) max = Mathf.Max(max, v);
            var plot = Frame(area, xLabels, series, projectedFrom, max, out float top, out float slot, out float height);
            foreach (var s in series)
            {
                Vector2? prev = null;
                for (int i = 0; i < s.Values.Length; i++)
                {
                    float v = s.Values[i];
                    if (float.IsNaN(v)) { prev = null; continue; }
                    var point = new Vector2((i + 0.5f) * slot, height * v / top);
                    bool projected = i >= projectedFrom;
                    var color = projected ? Fade(s.Color, ProjectedAlpha) : s.Color;
                    if (prev is Vector2 a) Segment(plot, a, point, projected ? 2f : 3f, color);
                    Dot(plot, point, projected ? 6f : 8f, color);
                    prev = point;
                }
            }
        }

        /// <summary>Stacked bars: each column stacks the series' values bottom to top in the order given.</summary>
        public static void Bars(RectTransform area, string[] xLabels, IList<GraphSeries> stacks, int projectedFrom)
        {
            float max = 0f;
            for (int i = 0; i < xLabels.Length; i++)
            {
                float sum = 0f;
                foreach (var s in stacks) if (i < s.Values.Length && !float.IsNaN(s.Values[i])) sum += s.Values[i];
                max = Mathf.Max(max, sum);
            }
            var plot = Frame(area, xLabels, stacks, projectedFrom, max, out float top, out float slot, out float height);
            float barWidth = Mathf.Min(42f, slot * 0.62f);
            for (int i = 0; i < xLabels.Length; i++)
            {
                float y = 0f;
                bool projected = i >= projectedFrom;
                foreach (var s in stacks)
                {
                    if (i >= s.Values.Length || float.IsNaN(s.Values[i]) || s.Values[i] <= 0f) continue;
                    float h = height * s.Values[i] / top;
                    var bar = Rect(plot, "Bar", projected ? Fade(s.Color, ProjectedAlpha) : s.Color);
                    bar.anchoredPosition = new Vector2((i + 0.5f) * slot - barWidth / 2f, y);
                    bar.sizeDelta = new Vector2(barWidth, Mathf.Max(1f, h - 1f)); // a 1px gap between stacked parts
                    y += h;
                }
            }
        }

        public const float TableRowHeight = 30f;

        /// <summary>Height a Table with this many body rows needs.</summary>
        public static float TableHeight(int rows) => (rows + 1) * TableRowHeight + 8f;

        /// <summary>A compact table: right-aligned numbers, first column left-aligned. Rows from `projectedFrom` on are dimmed.
        /// Columns share the graph width in proportion to `weights`.</summary>
        public static void Table(RectTransform area, string[] headers, IList<string[]> rows, float[] weights, int projectedFrom)
        {
            float total = 0f;
            foreach (var w in weights) total += w;
            var panel = Rect(area, "Table", new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.4f));
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = Vector2.zero;
            panel.sizeDelta = new Vector2(Width, TableHeight(rows.Count));
            const float pad = 14f;
            float usable = Width - 2f * pad;

            void Row(string[] cells, float y, Color color, TMP_FontAsset font)
            {
                float x = pad;
                for (int c = 0; c < cells.Length && c < weights.Length; c++)
                {
                    float w = usable * weights[c] / total;
                    var label = Label(panel, cells[c] ?? "", 22f, color, c == 0 ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight, font);
                    var rect = label.rectTransform;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                    rect.anchoredPosition = new Vector2(x, y);
                    rect.sizeDelta = new Vector2(w - 6f, TableRowHeight);
                    x += w;
                }
            }

            Row(headers, -4f, Dim, ModStyle.BoldFont);
            var rule = Rect(panel, "Rule", new Color(Dim.r, Dim.g, Dim.b, 0.6f));
            rule.anchorMin = rule.anchorMax = rule.pivot = new Vector2(0f, 1f);
            rule.anchoredPosition = new Vector2(pad, -4f - TableRowHeight);
            rule.sizeDelta = new Vector2(usable, 1f);
            for (int i = 0; i < rows.Count; i++)
                Row(rows[i], -4f - (i + 1) * TableRowHeight - 2f, i >= projectedFrom ? Dim : Gold, i >= projectedFrom ? ModStyle.ButtonFont : ModStyle.BodyFont);
        }

        // ---- Frame: panel, legend, grid, axis labels, projection band -------------------------------------------

        private static RectTransform Frame(RectTransform area, string[] xLabels, IList<GraphSeries> series, int projectedFrom,
            float max, out float top, out float slot, out float height)
        {
            float areaHeight = area.GetComponent<LayoutElement>()?.preferredHeight ?? 300f;
            var panel = Rect(area, "Graph", new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.55f));
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = Vector2.zero;
            panel.sizeDelta = new Vector2(Width, areaHeight);

            var plot = Rect(panel, "Plot", Color.clear);
            plot.anchorMin = plot.anchorMax = plot.pivot = Vector2.zero;
            plot.anchoredPosition = new Vector2(PadLeft, PadBottom);
            float width = Width - PadLeft - PadRight;
            height = areaHeight - PadTop - PadBottom;
            plot.sizeDelta = new Vector2(width, height);
            int columns = Mathf.Max(1, xLabels.Length);
            slot = width / columns;

            // Legend, top left.
            float x = PadLeft;
            foreach (var s in series)
            {
                var swatch = Rect(panel, "Swatch", s.Color);
                swatch.anchorMin = swatch.anchorMax = swatch.pivot = new Vector2(0f, 1f);
                swatch.anchoredPosition = new Vector2(x, -17f);
                swatch.sizeDelta = new Vector2(14f, 14f);
                var label = Label(panel, s.Label, 22f, Gold, TextAlignmentOptions.MidlineLeft);
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = new Vector2(0f, 1f);
                label.rectTransform.anchoredPosition = new Vector2(x + 22f, -9f);
                label.rectTransform.sizeDelta = new Vector2(200f, 30f);
                x += 22f + label.GetPreferredValues(s.Label).x + 30f;
            }

            // Projection band.
            if (projectedFrom < columns)
            {
                var band = Rect(plot, "Projected", new Color(Gold.r, Gold.g, Gold.b, 0.035f));
                band.anchoredPosition = new Vector2(Mathf.Max(0, projectedFrom) * slot, 0f);
                band.sizeDelta = new Vector2((columns - Mathf.Max(0, projectedFrom)) * slot, height);
                var tag = Label(panel, "projected", 20f, Dim, TextAlignmentOptions.MidlineRight, ModStyle.ButtonFont);
                tag.rectTransform.anchorMin = tag.rectTransform.anchorMax = tag.rectTransform.pivot = new Vector2(1f, 1f);
                tag.rectTransform.anchoredPosition = new Vector2(-PadRight - 4f, -10f);
                tag.rectTransform.sizeDelta = new Vector2(200f, 28f);
            }

            // Horizontal grid with round steps.
            float step = NiceStep(Mathf.Max(1f, max) / 4f);
            top = Mathf.Max(step, Mathf.Ceil(Mathf.Max(1f, max) / step) * step);
            for (float v = 0f; v <= top + step * 0.01f; v += step)
            {
                float y = height * v / top;
                var line = Rect(plot, "Grid", new Color(Dim.r, Dim.g, Dim.b, v == 0f ? 0.8f : 0.3f));
                line.anchoredPosition = new Vector2(0f, y - 0.5f);
                line.sizeDelta = new Vector2(width, v == 0f ? 2f : 1f);
                var label = Label(plot, Short(v), 20f, Dim, TextAlignmentOptions.MidlineRight);
                label.rectTransform.anchoredPosition = new Vector2(-70f, y - 14f);
                label.rectTransform.sizeDelta = new Vector2(62f, 28f);
            }

            // Night numbers under the columns (thinned out when crowded).
            int every = columns > 24 ? 3 : columns > 14 ? 2 : 1;
            for (int i = 0; i < xLabels.Length; i++)
            {
                if (i % every != 0 && i != xLabels.Length - 1) continue;
                var label = Label(plot, xLabels[i], 20f, i >= projectedFrom ? Dim : Gold, TextAlignmentOptions.Midline);
                label.rectTransform.anchoredPosition = new Vector2((i + 0.5f) * slot - 30f, -32f);
                label.rectTransform.sizeDelta = new Vector2(60f, 28f);
            }
            return plot;
        }

        // ---- Primitives -------------------------------------------------------------------------------------------

        private static RectTransform Rect(Transform parent, string name, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            var image = obj.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            return rect;
        }

        private static void Segment(RectTransform plot, Vector2 a, Vector2 b, float thickness, Color color)
        {
            var rect = Rect(plot, "Line", color);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = (a + b) / 2f;
            rect.sizeDelta = new Vector2((b - a).magnitude, thickness);
            rect.localRotation = Quaternion.Euler(0f, 0f, Vector2.SignedAngle(Vector2.right, b - a));
        }

        private static void Dot(RectTransform plot, Vector2 at, float size, Color color)
        {
            var rect = Rect(plot, "Point", color);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = at;
            rect.sizeDelta = new Vector2(size, size);
        }

        private static TextMeshProUGUI Label(Transform parent, string text, float size, Color color, TextAlignmentOptions alignment,
            TMP_FontAsset font = null)
        {
            var tmp = GameWidgets.Text(parent, "Label", text, font ?? ModStyle.BoldFont, size, color, alignment);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            return tmp;
        }

        private static Color Fade(Color c, float alpha) => new(c.r, c.g, c.b, c.a * alpha);

        private static float NiceStep(float raw)
        {
            float magnitude = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float f = raw / magnitude;
            return (f <= 1f ? 1f : f <= 2f ? 2f : f <= 2.5f ? 2.5f : f <= 5f ? 5f : 10f) * magnitude;
        }

        public static string Short(float v) =>
            v >= 10000f ? $"{v / 1000f:0}k" : v >= 1000f ? $"{v / 1000f:0.#}k" : $"{v:0}";
    }
}
