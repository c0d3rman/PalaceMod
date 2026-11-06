using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using G = PalaceMod.AnalyticsGraph;

/*
    The planner's drawings, from plain UI Images in the game's palette (AnalyticsGraph's primitives):
      - Gold: gold per day as lines, with room below zero (a plan can overspend), red marks under days that do, and a
        small diamond above days with planned steps.
      - Map: every build slot from above, oriented like the screen, the castle in the middle; the slot being looked at
        is the large verdigris diamond, slots in the plan are bronze.
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

        /// <summary>Lines over <paramref name="labels"/> columns; NaN breaks a line. Columns flagged in <paramref name="shortCols"/>
        /// get a red mark, those in <paramref name="actionCols"/> a diamond.</summary>
        public static void Gold(RectTransform parent, float width, float height, string[] labels, IList<GraphSeries> series,
            ISet<int> shortCols, ISet<int> actionCols)
        {
            var panel = Panel(parent, "Gold graph", width, height);
            float min = 0f, max = 1f;
            foreach (var s in series)
                foreach (var v in s.Values)
                    if (!float.IsNaN(v)) { min = Mathf.Min(min, v); max = Mathf.Max(max, v); }
            float step = G.NiceStep(Mathf.Max(1f, max - min) / 4f);
            float bottom = Mathf.Floor(min / step) * step, top = Mathf.Max(bottom + step, Mathf.Ceil(max / step) * step);

            var plot = G.Rect(panel, "Plot", Color.clear);
            plot.anchorMin = plot.anchorMax = plot.pivot = Vector2.zero;
            plot.anchoredPosition = new Vector2(PadLeft, PadBottom);
            float w = width - PadLeft - PadRight, h = height - PadTop - PadBottom;
            plot.sizeDelta = new Vector2(w, h);
            int columns = Mathf.Max(1, labels.Length);
            float slot = w / columns;
            float Y(float v) => h * (v - bottom) / (top - bottom);

            // Legend.
            float x = 14f;
            foreach (var s in series)
            {
                var swatch = G.Rect(panel, "Swatch", s.Color);
                swatch.anchorMin = swatch.anchorMax = swatch.pivot = new Vector2(0f, 1f);
                swatch.anchoredPosition = new Vector2(x, -15f);
                swatch.sizeDelta = new Vector2(12f, 12f);
                var label = G.Label(panel, s.Label, 20f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = new Vector2(0f, 1f);
                label.rectTransform.anchoredPosition = new Vector2(x + 18f, -7f);
                label.rectTransform.sizeDelta = new Vector2(220f, 26f);
                x += 18f + label.GetPreferredValues(s.Label).x + 22f;
            }

            // Below zero: a faint red band.
            if (bottom < 0f)
            {
                var band = G.Rect(plot, "Below zero", G.Fade(Red, 0.12f));
                band.anchoredPosition = Vector2.zero;
                band.sizeDelta = new Vector2(w, Y(0f));
            }
            for (float v = bottom; v <= top + step * 0.01f; v += step)
            {
                float y = Y(v);
                bool zero = Mathf.Abs(v) < step * 0.01f;
                var line = G.Rect(plot, "Grid", new Color(G.Dim.r, G.Dim.g, G.Dim.b, zero ? 0.8f : 0.28f));
                line.anchoredPosition = new Vector2(0f, y - 0.5f);
                line.sizeDelta = new Vector2(w, zero ? 2f : 1f);
                var label = G.Label(plot, (v < 0f ? "-" : "") + G.Short(Mathf.Abs(v)), 18f, v < 0f ? Red : G.Dim, TextAlignmentOptions.MidlineRight);
                label.rectTransform.anchoredPosition = new Vector2(-58f, y - 13f);
                label.rectTransform.sizeDelta = new Vector2(52f, 26f);
            }
            int every = columns > 24 ? 3 : columns > 14 ? 2 : 1;
            for (int i = 0; i < labels.Length; i++)
            {
                float cx = (i + 0.5f) * slot;
                if (i % every == 0 || i == labels.Length - 1)
                {
                    bool bad = shortCols != null && shortCols.Contains(i);
                    var label = G.Label(plot, labels[i], 18f, bad ? Red : ModStyle.Gold, TextAlignmentOptions.Midline);
                    label.rectTransform.anchoredPosition = new Vector2(cx - 30f, -28f);
                    label.rectTransform.sizeDelta = new Vector2(60f, 26f);
                }
                if (actionCols != null && actionCols.Contains(i)) Diamond(plot, new Vector2(cx, h + 8f), 9f, ModStyle.Accent);
            }
            foreach (var s in series)
            {
                Vector2? prev = null;
                for (int i = 0; i < s.Values.Length; i++)
                {
                    float v = s.Values[i];
                    if (float.IsNaN(v)) { prev = null; continue; }
                    var point = new Vector2((i + 0.5f) * slot, Y(v));
                    var color = v < 0f ? Red : s.Color;
                    if (prev is Vector2 a) G.Segment(plot, a, point, 2.5f, s.Color);
                    G.Dot(plot, point, 7f, color);
                    prev = point;
                }
            }
        }

        // ---- Map ----------------------------------------------------------------------------------------------

        /// <summary>The map of every slot, <paramref name="selected"/> highlighted (and its type lit), slots in
        /// <paramref name="planned"/> marked.</summary>
        public static void Map(RectTransform parent, float width, float height, EconWorld world, int selected, ISet<int> planned)
        {
            var panel = Panel(parent, "Map", width, height, 0.6f);
            var slots = world.Slots.Where(s => s.Steps.Count > 0 || s.IsCastle).ToList();
            if (slots.Count == 0) return;
            float minX = slots.Min(s => s.MapX), maxX = slots.Max(s => s.MapX);
            float minY = slots.Min(s => s.MapY), maxY = slots.Max(s => s.MapY);
            const float pad = 22f;
            float scale = Mathf.Min((width - 2f * pad) / Mathf.Max(1f, maxX - minX), (height - 2f * pad) / Mathf.Max(1f, maxY - minY));
            float ox = (width - (maxX - minX) * scale) / 2f, oy = (height - (maxY - minY) * scale) / 2f;
            Vector2 P(EconSlot s) => new(ox + (s.MapX - minX) * scale, oy + (s.MapY - minY) * scale);
            var area = G.Rect(panel, "Area", Color.clear);
            area.anchorMin = area.anchorMax = area.pivot = Vector2.zero;
            area.sizeDelta = new Vector2(width, height);
            area.anchoredPosition = Vector2.zero; // from the panel's bottom left, like the graph's plot

            var sel = selected >= 0 && selected < world.Slots.Count ? world.Slots[selected] : null;
            // Draw the small things first so highlights end up on top.
            foreach (var s in slots.OrderBy(s => s == sel ? 3 : planned != null && planned.Contains(s.Index) ? 2 : sel != null && s.TypeName == sel.TypeName ? 1 : 0))
            {
                var at = P(s);
                if (s.IsCastle) { Diamond(area, at, 22f, ModStyle.Gold); continue; }
                bool built = s.Level >= 1;
                if (s == sel)
                {
                    Diamond(area, at, 30f, G.Fade(ModStyle.Accent, 0.35f));
                    Diamond(area, at, 16f, ModStyle.Accent);
                }
                else if (planned != null && planned.Contains(s.Index)) Diamond(area, at, 12f, G.Bronze);
                else if (sel != null && s.TypeName == sel.TypeName && !s.IsDependent) Square(area, at, 9f, G.Fade(ModStyle.Bright, built ? 1f : 0.6f));
                else if (s.IsDependent) Square(area, at, 4f, G.Fade(G.Dim, built ? 0.9f : 0.45f));
                else Square(area, at, built ? 8f : 6f, built ? G.Fade(ModStyle.Gold, 0.85f) : G.Fade(G.Dim, s.Active ? 0.9f : 0.4f));
            }
        }

        public static void MapLegend(Transform parent, float width)
        {
            var text = GameWidgets.Text(parent, "Map legend",
                $"<color=#{ModStyle.Hex(ModStyle.Accent)}>◆</color> this slot   <color=#{ModStyle.Hex(ModStyle.Bright)}>■</color> same building   " +
                $"<color=#{ModStyle.Hex(G.Bronze)}>◆</color> in the plan   <color=#{ModStyle.Hex(ModStyle.Gold)}>■</color> built   " +
                $"<color=#{ModStyle.Hex(G.Dim)}>■</color> not built", ModStyle.BodyFont, 19f, ModStyle.Gold, TextAlignmentOptions.TopLeft);
            text.enableWordWrapping = true;
            text.rectTransform.sizeDelta = new Vector2(width, 30f);
        }

        private static void Square(RectTransform parent, Vector2 at, float size, Color color)
        {
            var r = G.Rect(parent, "Slot", color);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = at;
            r.sizeDelta = new Vector2(size, size);
        }

        private static void Diamond(RectTransform parent, Vector2 at, float size, Color color)
        {
            var r = G.Rect(parent, "Mark", color);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = at;
            float side = size / Mathf.Sqrt(2f);
            r.sizeDelta = new Vector2(side, side);
            r.localRotation = Quaternion.Euler(0f, 0f, 45f);
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
