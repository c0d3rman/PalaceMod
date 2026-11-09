using System;
using System.Collections.Generic;

/*
    The pure maths behind lighting and shapes (docs/builder-engine.md, "Light"): how lit a ground point is, and the
    `sector` (cone) shape. No UnityEngine, so the unit tests cover it.

    Angles are yaw degrees, 0 = north (+Z), clockwise (90 = east), as `.facing` and `dir()` use them.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class LightMath
    {
        /// <summary>Share of a light's range that is fully lit; beyond it the light fades to 0 at the range.</summary>
        public const double FullShare = 0.8;

        /// <summary>A light level reads as "lit" from this up (`.lit`, the `lit` filter term).</summary>
        public const double LitThreshold = 0.5;

        /// <summary>Rec. 709 luminance of an RGB colour (0..1 per channel).</summary>
        public static double Luminance(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

        /// <summary>The level's own light from the sun's colour and intensity: 1 for the game's white daylight, about 0.34
        /// for its night blue, 0 for black.</summary>
        public static double Daylight(double r, double g, double b, double intensity) =>
            Clamp01(Luminance(r, g, b) * Math.Max(0, intensity));

        /// <summary>
        /// How much one light lights a ground point dx, dz metres away from it (flat): its intensity (capped at 1) out to
        /// FullShare of its range, fading to 0 at the range. A cone (coneDeg &gt; 0) lights only the sector within half
        /// the cone of its yaw.
        /// </summary>
        public static double Contribution(double dx, double dz, double range, double intensity, double coneDeg, double yawDeg)
        {
            if (range <= 0 || intensity <= 0) return 0;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d > range) return 0;
            if (coneDeg > 0 && coneDeg < 360 && d > 0.25 && !InCone(dx, dz, coneDeg, yawDeg)) return 0;
            double fall = d <= range * FullShare ? 1 : 1 - (d - range * FullShare) / (range * (1 - FullShare));
            return Math.Min(1, intensity) * Clamp01(fall);
        }

        /// <summary>The light at a point: the level's daylight plus every light's contribution, capped at 1.</summary>
        public static double Level(double daylight, IEnumerable<double> contributions)
        {
            double sum = Clamp01(daylight);
            foreach (var c in contributions) sum += Math.Max(0, c);
            return Clamp01(sum);
        }

        /// <summary>Whether a direction (dx, dz) lies within half the cone of a yaw.</summary>
        public static bool InCone(double dx, double dz, double coneDeg, double yawDeg)
        {
            double a = Math.Atan2(dx, dz) * 180 / Math.PI;
            return Math.Abs(DeltaAngle(a, yawDeg)) <= coneDeg / 2 + 1e-6;
        }

        /// <summary>Signed smallest difference a - b in degrees, in (-180, 180].</summary>
        public static double DeltaAngle(double a, double b)
        {
            double d = (a - b) % 360;
            if (d > 180) d -= 360;
            if (d <= -180) d += 360;
            return d;
        }

        /// <summary>Yaw normalised to [0, 360).</summary>
        public static double Yaw(double deg)
        {
            double y = deg % 360;
            return y < 0 ? y + 360 : y;
        }

        /// <summary>
        /// A sector (a cone on the ground) in its local frame (x right, z forward): radius metres, angle degrees wide,
        /// centred on +Z. Inside: within the radius and within half the angle of +Z (an angle of 360 or more is a circle).
        /// </summary>
        public static bool SectorContains(double x, double z, double radius, double angleDeg)
        {
            if (x * x + z * z > radius * radius) return false;
            if (angleDeg >= 360) return true;
            if (x * x + z * z < 1e-8) return true;
            return InCone(x, z, angleDeg, 0);
        }

        /// <summary>A sector's outline as (x, z) points: the apex, then the arc from left to right (closing back to the
        /// apex). A full circle has no apex.</summary>
        public static List<(double x, double z)> SectorOutline(double radius, double angleDeg, int segments = 0)
        {
            var pts = new List<(double, double)>();
            angleDeg = Math.Max(1, Math.Min(360, angleDeg));
            if (segments <= 0) segments = Math.Max(4, (int)Math.Ceiling(angleDeg / 7.5));
            bool full = angleDeg >= 360;
            if (!full) pts.Add((0, 0));
            int n = full ? segments : segments + 1;
            for (int i = 0; i < n; i++)
            {
                double a = (-angleDeg / 2 + angleDeg * i / segments) * Math.PI / 180;
                pts.Add((Math.Sin(a) * radius, Math.Cos(a) * radius));
            }
            return pts;
        }

        public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
