using System;
using System.Collections.Generic;
using UnityEngine;

/*
    The Terrain tool's icons, drawn at startup as white glyphs (the tiles tint them): a shape test per pixel,
    supersampled 3x3 for smooth edges. y runs up from the bottom of a 64 px square.
*/
namespace PalaceMod.Builder.Studio.Terrain
{
    internal static class TerrainIcons
    {
        private const int N = 64;
        private static readonly Dictionary<TerrainBrush, Sprite> brushes = [];
        private static Sprite tool;

        public static Sprite Tool() => tool ??= Draw((x, y) =>
            (Math.Abs(x - 26) < (52 - y) * 0.85f && y >= 10) || (Math.Abs(x - 44) < (38 - y) * 0.9f && y >= 10));

        public static Sprite Brush(TerrainBrush b)
        {
            if (brushes.TryGetValue(b, out var s) && s != null) return s;
            return brushes[b] = Draw(Shape(b));
        }

        private static Func<float, float, bool> Shape(TerrainBrush b) => b switch
        {
            // A mound with an arrow up.
            TerrainBrush.Raise => (x, y) => Mound(x, y, 32, 8, 26, 14) || Arrow(x, y, 32, 30, 58, up: true),
            // A mound cut level by a line.
            TerrainBrush.Flatten => (x, y) => (Mound(x, y, 32, 8, 28, 22) && y < 22) || Bar(x, y, 4, 60, 25, 29),
            // A mesa with a level mark above it.
            TerrainBrush.SetHeight => (x, y) => Mesa(x, y) || Bar(x, y, 22, 42, 46, 50) || Bar(x, y, 30, 34, 38, 54),
            // Two soft waves.
            TerrainBrush.Smooth => (x, y) => Wave(x, y, 24, 6, 0f) || Wave(x, y, 40, 6, 1.2f),
            // A wedge: a straight incline.
            TerrainBrush.Ramp => (x, y) => y >= 10 && y <= 14 + (x - 6) * 0.72f && x >= 6 && x <= 58 && y <= 52,
            // A paint drop.
            TerrainBrush.Material => (x, y) => Drop(x, y),
            // A circle with a bar through it.
            TerrainBrush.Block => (x, y) => Ring(x, y, 32, 32, 22, 6) || Slash(x, y),
            // Three wave lines.
            TerrainBrush.Water => (x, y) => Wave(x, y, 18, 3.2f, 0f) || Wave(x, y, 32, 3.2f, 0f) || Wave(x, y, 46, 3.2f, 0f),
            _ => (_, _) => false,
        };

        private static bool Mound(float x, float y, float cx, float baseY, float halfW, float height)
        {
            if (y < baseY || Math.Abs(x - cx) > halfW) return false;
            float t = (x - cx) / halfW;
            return y <= baseY + height * (1f - t * t);
        }

        private static bool Mesa(float x, float y) => y >= 8 && y <= 32 && Math.Abs(x - 32) <= 14 + (32 - y) * 0.6f;

        private static bool Bar(float x, float y, float x0, float x1, float y0, float y1) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

        private static bool Arrow(float x, float y, float cx, float y0, float y1, bool up)
        {
            float head = 12f;
            float tip = up ? y1 : y0;
            if (Math.Abs(y - tip) <= head && Math.Abs(x - cx) <= (head - Math.Abs(y - tip)) * 0.9f) return true;
            return Math.Abs(x - cx) <= 3f && y >= y0 && y <= y1 - head * 0.6f;
        }

        private static bool Wave(float x, float y, float cy, float thick, float phase)
        {
            if (x < 6 || x > 58) return false;
            float w = cy + (float)Math.Sin((x - 6) / 52f * Math.PI * 2f + phase) * 5f;
            return Math.Abs(y - w) <= thick;
        }

        private static bool Ring(float x, float y, float cx, float cy, float r, float thick)
        {
            float d = (float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            return d <= r && d >= r - thick;
        }

        private static bool Slash(float x, float y)
        {
            float d = Math.Abs((x - 32) - (y - 32)) / 1.414f;
            return d <= 3f && Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32)) <= 20;
        }

        private static bool Drop(float x, float y)
        {
            float dx = x - 32, dy = y - 26;
            if (dx * dx + dy * dy <= 16 * 16) return true;
            // The tip: a triangle up to (32, 58).
            return y >= 26 && y <= 58 && Math.Abs(dx) <= (58 - y) * 0.52f;
        }

        private static Sprite Draw(Func<float, float, bool> inside)
        {
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                            if (inside(x + (sx + 0.5f) / 3f, y + (sy + 0.5f) / 3f)) hits++;
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 9));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
