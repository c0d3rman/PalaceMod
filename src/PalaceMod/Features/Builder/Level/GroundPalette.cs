using System;
using System.Collections.Generic;

/*
    The flat field's paint colours (GroundLayer). Each paint starts from the host scheme's own colour for it (what
    ColorAndLightManager puts on groundMaterial, groundHigh, groundLow, earthMaterial, sandMaterial, roadMaterial and
    rockMaterial), so the field looks like the host's ground. But most schemes give several of them one colour
    (ground = high = low everywhere; road = earth on Moorweg, Nordfels and Frostsee), and a painted stripe would then
    vanish. Resolve keeps a host colour where it already stands apart and otherwise steps it lighter or darker (same
    hue) until it does: ground stays the host's grass, high and low become lighter and darker grass, road a clear path,
    earth a bank (darker, or drawn towards the sand). Pure maths, unit-tested.
*/
namespace PalaceMod.Builder.Level
{
    internal readonly struct Rgb(float r, float g, float b)
    {
        public readonly float R = r, G = g, B = b;
        public override string ToString() => $"({R:0.###}, {G:0.###}, {B:0.###})";
    }

    internal static class GroundPalette
    {
        /// <summary>Two paints closer than this read as one (redmean distance on 0..1 channels, 0..~3).</summary>
        public const float MinDistance = 0.18f;
        /// <summary>Grass shades (ground, high, low) only need to be told apart, not to contrast.</summary>
        public const float ShadeDistance = 0.12f;
        /// <summary>A road has to read as a path on the grass around it.</summary>
        public const float RoadDistance = 0.26f;

        private const int Count = 7;   // GroundMaterial values
        // Resolved in this order: earlier ones keep their host colour, later ones move away from them.
        private static readonly GroundMaterial[] Order =
            [GroundMaterial.Ground, GroundMaterial.Rock, GroundMaterial.Sand, GroundMaterial.High, GroundMaterial.Low, GroundMaterial.Road, GroundMaterial.Earth];

        /// <summary>Colours indexed by GroundMaterial, each distinct from the others; host indexed the same way.</summary>
        public static Rgb[] Resolve(Rgb[] host)
        {
            if (host == null || host.Length < Count) throw new ArgumentException($"need {Count} colours");
            var result = new Rgb[Count];
            var done = new bool[Count];
            var ground = host[(int)GroundMaterial.Ground];
            var sand = host[(int)GroundMaterial.Sand];
            foreach (var m in Order)
            {
                var c = host[(int)m];
                bool lighterFirst = m switch
                {
                    GroundMaterial.Low or GroundMaterial.Earth => false,
                    // A road goes the way it already leans from the grass (darker when it doesn't).
                    GroundMaterial.Road => Value(c) > Value(ground) + 0.02f,
                    _ => true,
                };
                result[(int)m] = m == GroundMaterial.Ground ? c : Pick(Candidates(c, lighterFirst, m == GroundMaterial.Earth ? sand : null), c, x => Fits(m, x, result, done));
                done[(int)m] = true;
            }
            return result;
        }

        /// <summary>The minimum distance between two paints.</summary>
        public static float Needed(GroundMaterial a, GroundMaterial b)
        {
            if ((a == GroundMaterial.Road && b == GroundMaterial.Ground) || (a == GroundMaterial.Ground && b == GroundMaterial.Road)) return RoadDistance;
            return IsShade(a) && IsShade(b) ? ShadeDistance : MinDistance;
        }

        private static bool IsShade(GroundMaterial m) => m is GroundMaterial.Ground or GroundMaterial.High or GroundMaterial.Low;

        private static bool Fits(GroundMaterial m, Rgb c, Rgb[] chosen, bool[] done)
        {
            for (int i = 0; i < Count; i++)
                if (done[i] && Distance(c, chosen[i]) < Needed(m, (GroundMaterial)i)) return false;
            return true;
        }

        private static Rgb Pick(IEnumerable<Rgb> candidates, Rgb fallback, Func<Rgb, bool> fits)
        {
            Rgb? last = null;
            foreach (var c in candidates)
            {
                if (fits(c)) return c;
                last = c;
            }
            // Nothing fits (a scheme of near-identical colours): the furthest step tried.
            return last ?? fallback;
        }

        /// <summary>The host colour, then steps in the preferred direction, the other direction, and further out.
        /// Earth also tries being drawn towards the sand (a warmer bank) after a few darker steps.</summary>
        private static IEnumerable<Rgb> Candidates(Rgb c, bool lighterFirst, Rgb? towards)
        {
            yield return c;
            ToHsv(c, out float h, out float s, out float v);
            if (towards is Rgb t)
            {
                for (int k = 1; k <= 3; k++)
                    if (Step(h, s, v, k, lighterFirst) is Rgb x) yield return x;
                foreach (float f in new[] { 0.25f, 0.4f, 0.55f })
                    yield return new Rgb(c.R + (t.R - c.R) * f, c.G + (t.G - c.G) * f, c.B + (t.B - c.B) * f);
            }
            foreach (var (from, to) in new[] { (1, 5), (6, 10) })
                foreach (bool lighter in new[] { lighterFirst, !lighterFirst })
                    for (int k = from; k <= to; k++)
                        if (Step(h, s, v, k, lighter) is Rgb x) yield return x;
        }

        private static Rgb? Step(float h, float s, float v, int k, bool lighter)
        {
            const float dv = 0.07f;
            if (lighter)
            {
                if (v >= 0.999f && s <= 0.001f) return null;
                float nv = v + dv * k;
                // Past white, lighten by washing the colour out instead.
                float ns = nv > 1f ? Math.Max(0f, s - (nv - 1f)) : s;
                return FromHsv(h, ns, Math.Min(1f, nv));
            }
            if (v <= 0.03f) return null;
            return FromHsv(h, s, Math.Max(0.02f, v - dv * k));
        }

        /// <summary>Whether a host water plane x0..x1, z0..z1 is a lake or pond rather than a sea round the flat field
        /// fx0..fx1, fz0..fz1: a sea reaches past both of the field's opposite edges (Frostsee's), a lake doesn't. A flat
        /// base hides lakes, also those beside the field: with the host's ground gone they'd float in the void.</summary>
        public static bool IsLake(float x0, float z0, float x1, float z1, float fx0, float fz0, float fx1, float fz1) =>
            !(x0 <= fx0 && x1 >= fx1) && !(z0 <= fz0 && z1 >= fz1);

        /// <summary>"Redmean" colour distance on 0..1 channels: cheap and close enough to how far apart colours look.</summary>
        public static float Distance(Rgb a, Rgb b)
        {
            float rm = (a.R + b.R) / 2f, dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return (float)Math.Sqrt((2f + rm) * dr * dr + 4f * dg * dg + (3f - rm) * db * db);
        }

        private static float Value(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B));

        private static void ToHsv(Rgb c, out float h, out float s, out float v)
        {
            float max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), d = max - min;
            v = max;
            s = max <= 0f ? 0f : d / max;
            if (d <= 0f) { h = 0f; return; }
            if (max == c.R) h = (c.G - c.B) / d % 6f;
            else if (max == c.G) h = (c.B - c.R) / d + 2f;
            else h = (c.R - c.G) / d + 4f;
            h /= 6f;
            if (h < 0f) h += 1f;
        }

        private static Rgb FromHsv(float h, float s, float v)
        {
            float c = v * s, x = c * (1f - Math.Abs(h * 6f % 2f - 1f)), m = v - c;
            float r, g, b;
            switch ((int)(h * 6f) % 6)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }
            return new Rgb(r + m, g + m, b + m);
        }
    }
}
