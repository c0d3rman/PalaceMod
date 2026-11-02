using UnityEngine;

/*
    Draws the minigun's loadout icon in code: a white silhouette on transparency, laid diagonally like the native
    weapon icons (the game tints icon images itself). Shapes are signed-distance capsules and boxes, so edges are
    anti-aliased at any size.
*/
namespace PalaceMod
{
    internal static class MinigunIcon
    {
        public static Sprite Create(int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "PalaceMod Minigun Icon", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float aa = 1.5f / size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Unit coordinates, then rotate 45° so the gun points to the top right.
                    var p = new Vector2((x + 0.5f) / size - 0.5f, (y + 0.5f) / size - 0.5f);
                    var q = Rotate(p, -45f);
                    float d = Distance(q);
                    float a = Mathf.Clamp01(0.5f - d / aa);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // The gun lies along +x in q-space, roughly from x=-0.42 (stock) to x=+0.45 (muzzle).
        private static float Distance(Vector2 q)
        {
            const float barrelR = 0.026f;
            float d = float.MaxValue;
            // Three visible barrels, spaced around the spindle.
            foreach (float off in new[] { -0.062f, 0f, 0.062f })
                d = Mathf.Min(d, Capsule(q, new Vector2(-0.05f, off), new Vector2(0.43f, off), barrelR));
            // Clamping bands near the muzzle and mid-barrel.
            d = Mathf.Min(d, Box(q, new Vector2(0.36f, 0f), new Vector2(0.025f, 0.11f), 0.015f));
            d = Mathf.Min(d, Box(q, new Vector2(0.17f, 0f), new Vector2(0.02f, 0.10f), 0.012f));
            // Housing that holds the barrels.
            d = Mathf.Min(d, Box(q, new Vector2(-0.12f, 0f), new Vector2(0.11f, 0.125f), 0.035f));
            // Rear body and stock.
            d = Mathf.Min(d, Box(q, new Vector2(-0.31f, 0.0f), new Vector2(0.09f, 0.085f), 0.04f));
            // Grip below the body, and the carrying handle above the housing.
            d = Mathf.Min(d, Capsule(q, new Vector2(-0.27f, -0.08f), new Vector2(-0.33f, -0.21f), 0.038f));
            float handle = Mathf.Abs(Box(q, new Vector2(-0.12f, 0.17f), new Vector2(0.075f, 0.045f), 0.035f)) - 0.016f;
            d = Mathf.Min(d, Mathf.Max(handle, 0.135f - q.y));
            // Ammo belt hanging from the housing.
            for (int i = 0; i < 4; i++)
                d = Mathf.Min(d, Box(q, new Vector2(-0.09f - i * 0.035f, -0.17f - i * 0.045f), new Vector2(0.018f, 0.03f), 0.008f));
            return d;
        }

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
        }

        private static float Capsule(Vector2 p, Vector2 a, Vector2 b, float r)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude - r;
        }

        private static float Box(Vector2 p, Vector2 c, Vector2 half, float round)
        {
            Vector2 d = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half + new Vector2(round, round);
            return new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude + Mathf.Min(Mathf.Max(d.x, d.y), 0f) - round;
        }
    }
}
