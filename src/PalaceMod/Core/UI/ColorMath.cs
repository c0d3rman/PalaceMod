using System;
using System.Globalization;

/*
    Colour maths without Unity types (tests/PalaceMod.Tests covers it): hex parsing, HSV, distance, and the shade a
    FlatKit toon material pairs with a lit colour. ColorTools (ModColorPicker.cs) wraps it for UnityEngine.Color.
*/
namespace PalaceMod
{
    public static class ColorMath
    {
        /// <summary>"#8fd14f", "8fd14f" or "#8fd" (short form), as 0-1 RGB.</summary>
        public static bool TryParseHex(string text, out (float r, float g, float b) color)
        {
            color = (0f, 0f, 0f);
            string s = text?.Trim() ?? "";
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 3) s = new string([s[0], s[0], s[1], s[1], s[2], s[2]]);
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            color = (((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
            return true;
        }

        public static string Hex((float r, float g, float b) c) =>
            "#" + Byte(c.r).ToString("x2") + Byte(c.g).ToString("x2") + Byte(c.b).ToString("x2");

        private static int Byte(float v) => (int)Math.Round(Clamp01(v) * 255);

        public static float Distance((float r, float g, float b) a, (float r, float g, float b) b) =>
            (float)Math.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        /// <summary>RGB to hue (0-1), saturation, value, as Unity's Color.RGBToHSV.</summary>
        public static (float h, float s, float v) ToHsv((float r, float g, float b) c)
        {
            float max = Math.Max(c.r, Math.Max(c.g, c.b)), min = Math.Min(c.r, Math.Min(c.g, c.b)), d = max - min;
            float h = 0f;
            if (d > 1e-6f)
            {
                if (max == c.r) h = (c.g - c.b) / d / 6f;
                else if (max == c.g) h = ((c.b - c.r) / d + 2f) / 6f;
                else h = ((c.r - c.g) / d + 4f) / 6f;
                if (h < 0f) h += 1f;
            }
            return (h, max <= 0f ? 0f : d / max, max);
        }

        public static (float r, float g, float b) FromHsv(float h, float s, float v)
        {
            h = ((h % 1f) + 1f) % 1f * 6f;
            int i = (int)Math.Floor(h);
            float f = h - i, p = v * (1f - s), q = v * (1f - s * f), t = v * (1f - s * (1f - f));
            return (i % 6) switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
        }

        /// <summary>
        /// The shade (FlatKit's _ColorDim) for a new lit colour (_BaseColor), keeping the source material's light-to-
        /// shade relation: the same hue turn, brightness ratio and saturation change, each in proportion to how colourful
        /// the target is, so a grey stays grey.
        /// </summary>
        public static (float r, float g, float b) ShadeFor((float r, float g, float b) target, (float r, float g, float b) sourceLight, (float r, float g, float b) sourceShade)
        {
            var (hl, sl, vl) = ToHsv(sourceLight);
            var (hm, sm, vm) = ToHsv(sourceShade);
            var (h, s, v) = ToHsv(target);
            float dh = (float)(Math.IEEERemainder((hm - hl) * 360.0, 360.0) / 360.0);
            float colourful = Clamp01(s * 8f);
            float shadeS = Clamp01(s + (sm - sl) * colourful);
            float ratio = vl > 0.01f ? Math.Max(0.2f, Math.Min(1f, vm / vl)) : 0.7f;
            // The hue turn of a pale source (the enemy's lit pink) is mostly noise: weigh it by how colourful that is too.
            float turn = dh * Math.Min(colourful, Clamp01(sl * 3f));
            return FromHsv(h + turn, shadeS, v * ratio);
        }
    }
}
