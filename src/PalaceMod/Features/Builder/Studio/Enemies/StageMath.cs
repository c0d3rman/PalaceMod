using System;
using NVec = System.Numerics.Vector3;

/*
    The Enemy Studio stage's geometry, without the scene (unit-tested in tests/PalaceMod.Tests/EnemyStudioTests.cs, so
    System.Numerics vectors, not Unity's): where the stage goes outside a level, what counts as the level's backdrop,
    how far the camera stands to frame a model, where the orbit's focus may go, and whether a prop stands between the
    camera and the model.
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    internal static class StageMath
    {
        /// <summary>The stage's centre: <paramref name="margin"/> past the east edge of the level's box, level with
        /// its middle north-south.</summary>
        public static NVec AwayPoint(NVec levelMin, NVec levelMax, float margin, float height) =>
            new(levelMax.X + margin, height, (levelMin.Z + levelMax.Z) * 0.5f);

        /// <summary>A renderer the level's box ignores: wider than <paramref name="limit"/> on the ground (sea and
        /// ground planes laid far around a map), or with no size at all.</summary>
        public static bool IsBackdrop(NVec size, float limit) =>
            Math.Max(size.X, size.Z) > limit || size.LengthSquared() < 1e-6f;

        /// <summary>How far a camera with vertical field of view <paramref name="fovDeg"/> stands so a sphere of
        /// <paramref name="radius"/> fits the picture's height.</summary>
        public static float FrameDistance(float radius, float fovDeg) =>
            (float)(Math.Max(0.3f, radius) / Math.Sin(fovDeg * 0.5 * Math.PI / 180.0) * 1.05);

        /// <summary>The zoom-out limit for a model framed at <paramref name="framed"/>: at least
        /// <paramref name="floor"/>, and always room to back off well past the framing.</summary>
        public static float MaxDistance(float framed, float floor) => Math.Max(floor, framed * 2.5f);

        /// <summary>Keeps the orbit's focus over the stage: within <paramref name="reach"/> of its centre on the
        /// ground, and between <paramref name="below"/> under and <paramref name="above"/> over its floor.</summary>
        public static NVec ClampFocus(NVec focus, NVec stage, float reach, float below, float above)
        {
            float dx = focus.X - stage.X, dz = focus.Z - stage.Z;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len > reach) { dx *= reach / len; dz *= reach / len; }
            float y = Math.Min(Math.Max(focus.Y, stage.Y - below), stage.Y + above);
            return new NVec(stage.X + dx, y, stage.Z + dz);
        }

        /// <summary>Whether the segment a-b passes through the box (min, max) grown by <paramref name="pad"/> (slab
        /// test).</summary>
        public static bool SegmentHitsBox(NVec a, NVec b, NVec min, NVec max, float pad = 0f)
        {
            float t0 = 0f, t1 = 1f;
            float[] from = [a.X, a.Y, a.Z], d = [b.X - a.X, b.Y - a.Y, b.Z - a.Z], lo = [min.X, min.Y, min.Z], hi = [max.X, max.Y, max.Z];
            for (int i = 0; i < 3; i++)
            {
                float l = lo[i] - pad, h = hi[i] + pad;
                if (Math.Abs(d[i]) < 1e-6f)
                {
                    if (from[i] < l || from[i] > h) return false;
                    continue;
                }
                float u = (l - from[i]) / d[i], v = (h - from[i]) / d[i];
                if (u > v) (u, v) = (v, u);
                t0 = Math.Max(t0, u);
                t1 = Math.Min(t1, v);
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>Whether two boxes overlap.</summary>
        public static bool BoxesOverlap(NVec aMin, NVec aMax, NVec bMin, NVec bMax) =>
            aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }
}
