using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using G = PalaceMod.AnalyticsGraph;

/*
    The planner's drawings, from plain UI Images in the game's palette (AnalyticsGraph's primitives):
      - Strip: gold per day as lines over the board's columns, red marks
        under days that are short of gold, and the selected column lit.
      - SourceBars: the income-sources chart.
*/
namespace PalaceMod.Economy
{
    internal static class PlannerVisuals
    {
        public static readonly Color Red = new(0.86f, 0.36f, 0.30f);
        private const float PadLeft = 62f, PadRight = 14f, PadTop = 40f, PadBottom = 30f;

        public static RectTransform Panel(Transform parent, string name, float width, float height, float alpha = 0.55f)
        {
            var panel = G.Rect(parent, name, new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, alpha));
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = Vector2.zero;
            panel.sizeDelta = new Vector2(width, height);
            return panel;
        }

        /// <summary>The value range and grid step for a gold strip over <paramref name="series"/> (below zero only as far as a
        /// flagged shortfall goes).</summary>
        public static (float Bottom, float Top, float Step) Range(IEnumerable<GraphSeries> series)
        {
            float min = 0f, max = 1f;
            foreach (var s in series)
                foreach (var v in s.Values)
                    if (!float.IsNaN(v)) { min = Mathf.Min(min, v); max = Mathf.Max(max, v); }
            // A plan can't spend gold it doesn't have (the board refuses it), so a dip below zero is only ever a small
            // flagged shortfall: the strip shows it without giving a whole band to it.
            float step = G.NiceStep(Mathf.Max(1f, max - min) / 3f);
            float bottom = Mathf.Floor(min / step) * step, top = Mathf.Max(bottom + step, Mathf.Ceil(max / step) * step);
            return (bottom, top, step);
        }

        /// <summary>The y axis of a gold strip: grid values right-aligned in <paramref name="gutter"/> (bottom left origin).</summary>
        public static void StripAxis(RectTransform gutter, float width, float height, (float Bottom, float Top, float Step) range)
        {
            float Y(float v) => StripPad + (height - 2f * StripPad) * (v - range.Bottom) / (range.Top - range.Bottom);
            for (float v = range.Bottom; v <= range.Top + range.Step * 0.01f; v += range.Step)
            {
                var label = G.Label(gutter, (v < 0f ? "-" : "") + G.Short(Mathf.Abs(v)), 17f, v < 0f ? Red : G.Dim, TextAlignmentOptions.MidlineRight);
                label.rectTransform.anchoredPosition = new Vector2(0f, Y(v) - 12f);
                label.rectTransform.sizeDelta = new Vector2(width - 8f, 24f);
            }
        }

        private const float StripPad = 12f;

        /// <summary>
        /// Gold lines over columns whose centres are <paramref name="xs"/> (in <paramref name="plot"/>, bottom left
        /// origin), with grid lines across <paramref name="width"/>, a red tick under columns in
        /// <paramref name="shortCols"/>, and a lit band behind column <paramref name="lit"/>. NaN breaks a line.
        /// </summary>
        public static void Strip(RectTransform plot, float width, float height, float[] xs, float colWidth, IList<GraphSeries> series,
            (float Bottom, float Top, float Step) range, ISet<int> shortCols, int lit)
        {
            float Y(float v) => StripPad + (height - 2f * StripPad) * (v - range.Bottom) / (range.Top - range.Bottom);
            if (lit >= 0 && lit < xs.Length)
            {
                var band = G.Rect(plot, "Lit", G.Fade(ModStyle.Gold, 0.08f));
                band.anchoredPosition = new Vector2(xs[lit] - colWidth / 2f, 0f);
                band.sizeDelta = new Vector2(colWidth, height);
            }
            for (float v = range.Bottom; v <= range.Top + range.Step * 0.01f; v += range.Step)
            {
                bool zero = Mathf.Abs(v) < range.Step * 0.01f;
                var line = G.Rect(plot, "Grid", new Color(G.Dim.r, G.Dim.g, G.Dim.b, zero ? 0.8f : 0.25f));
                line.anchoredPosition = new Vector2(0f, Y(v) - 0.5f);
                line.sizeDelta = new Vector2(width, zero ? 2f : 1f);
            }
            if (shortCols != null)
                foreach (int i in shortCols)
                {
                    if (i < 0 || i >= xs.Length) continue;
                    var tick = G.Rect(plot, "Short", Red);
                    tick.anchoredPosition = new Vector2(xs[i] - colWidth * 0.3f, 0f);
                    tick.sizeDelta = new Vector2(colWidth * 0.6f, 4f);
                }
            foreach (var s in series)
            {
                Vector2? prev = null;
                for (int i = 0; i < s.Values.Length && i < xs.Length; i++)
                {
                    float v = s.Values[i];
                    if (float.IsNaN(v)) { prev = null; continue; }
                    var point = new Vector2(xs[i], Y(v));
                    if (prev is Vector2 a) G.Segment(plot, a, point, 2.5f, s.Color);
                    G.Dot(plot, point, 7f, v < 0f ? Red : s.Color);
                    prev = point;
                }
            }
        }

        /// <summary>A legend row (swatches and names) at the top left of <paramref name="parent"/>; returns its width.</summary>
        public static float Legend(RectTransform parent, IList<GraphSeries> series, float x = 0f, float y = 0f)
        {
            foreach (var s in series)
            {
                var swatch = G.Rect(parent, "Swatch", s.Color);
                swatch.anchorMin = swatch.anchorMax = swatch.pivot = new Vector2(0f, 1f);
                swatch.anchoredPosition = new Vector2(x, y - 7f);
                swatch.sizeDelta = new Vector2(11f, 11f);
                var label = G.Label(parent, s.Label, 18f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = new Vector2(0f, 1f);
                label.rectTransform.anchoredPosition = new Vector2(x + 16f, y);
                label.rectTransform.sizeDelta = new Vector2(240f, 24f);
                x += 16f + label.GetPreferredValues(s.Label).x + 20f;
            }
            return x;
        }
        // ---- Income sources ---------------------------------------------------------------------------------

        internal sealed class SourceBar
        {
            public string Label, Note;
            public int Cost, Earned;
            /// <summary>First level of its building (a thin rule above it separates buildings).</summary>
            public bool First;
        }

        private const float BarRow = 46f, BarLabel = 340f, BarNet = 70f;

        public static float SourceBarsHeight(int rows) => rows * BarRow + 10f;

        /// <summary>One line per building and level: a gold bar for what it pays by the end over a copper bar for what it
        /// costs, on one scale, with what's left over and a note (payback, pay per night) under the bars.</summary>
        public static void SourceBars(RectTransform parent, float width, IList<SourceBar> rows)
        {
            var panel = Panel(parent, "Sources", width, SourceBarsHeight(rows.Count), 0.85f);
            float max = Mathf.Max(1f, rows.Max(r => Mathf.Max(r.Cost, r.Earned)));
            float barArea = width - BarLabel - BarNet - 24f;
            var copper = AnalyticsGraph.Copper;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                float top = -6f - i * BarRow;
                if (r.First && i > 0)
                {
                    var rule = G.Rect(panel, "Rule", G.Fade(G.Dim, 0.5f));
                    rule.anchorMin = rule.anchorMax = rule.pivot = new Vector2(0f, 1f);
                    rule.anchoredPosition = new Vector2(10f, top + 2f);
                    rule.sizeDelta = new Vector2(width - 20f, 1f);
                }
                var label = G.Label(panel, r.Label, 20f, r.First ? ModStyle.Gold : G.Fade(ModStyle.Gold, 0.8f), TextAlignmentOptions.MidlineLeft, r.First ? ModStyle.BoldFont : ModStyle.BodyFont);
                label.overflowMode = TextOverflowModes.Ellipsis;
                Place(label.rectTransform, 12f, top, BarLabel - 24f, 26f);
                float x0 = BarLabel;
                var earned = G.Rect(panel, "Earned", ModStyle.Gold);
                Place(earned, x0, top - 4f, Mathf.Max(2f, barArea * r.Earned / max), 10f);
                var cost = G.Rect(panel, "Cost", copper);
                Place(cost, x0, top - 16f, Mathf.Max(2f, barArea * r.Cost / max), 6f);
                int net = r.Earned - r.Cost;
                var value = G.Label(panel, (net > 0 ? "+" : "") + net.ToString("N0"), 21f, net >= 0 ? ModStyle.Bright : Red, TextAlignmentOptions.MidlineRight, ModStyle.BoldFont);
                Place(value.rectTransform, width - BarNet - 12f, top, BarNet, 26f);
                var note = G.Label(panel, r.Note, 17f, G.Dim, TextAlignmentOptions.MidlineLeft, ModStyle.ButtonFont);
                note.overflowMode = TextOverflowModes.Ellipsis;
                Place(note.rectTransform, x0, top - 22f, width - x0 - 12f, 20f);
            }
        }

        private static void Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(w, h);
        }

        /// <summary>A dark panel behind everything in the aside, so the level behind the menu doesn't show through.</summary>
        public static void AsideBackdrop(RectTransform aside)
        {
            var rect = Panel(aside, "Backdrop", AsideWidth(aside) + 24f, AsideHeight(aside) + 24f, 0.94f);
            rect.anchoredPosition = new Vector2(-12f, 12f);
            rect.SetAsFirstSibling();
        }

        /// <summary>A text block in the aside, wrapped to <paramref name="width"/>; returns its height.</summary>
        public static float Text(Transform parent, string text, float y, float width, float size = 24f, TMP_FontAsset font = null, Color? color = null)
        {
            var tmp = GameWidgets.Text(parent, "Text", text, font ?? ModStyle.BodyFont, size, color ?? ModStyle.Gold, TextAlignmentOptions.TopLeft);
            tmp.enableWordWrapping = true;
            tmp.characterSpacing = 0f;
            tmp.raycastTarget = false;
            var rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            float h = tmp.GetPreferredValues(text, width, 0f).y + 2f;
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(width, h);
            return h;
        }

        /// <summary>A holder at the top left of the aside, <paramref name="y"/> down.</summary>
        public static RectTransform Holder(Transform parent, string name, float y, float width, float height)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        public static float AsideWidth(RectTransform aside) => aside != null && aside.rect.width > 100f ? Mathf.Min(aside.rect.width, 760f) : 660f;
        public static float AsideHeight(RectTransform aside) => aside != null && aside.rect.height > 100f ? aside.rect.height : 600f;
    }
}
