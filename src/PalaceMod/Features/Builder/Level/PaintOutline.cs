using System;
using System.Collections.Generic;

/*
    Smooth outlines for painted ground (GroundLayer). The paint is stored per sample (TerrainGrid.PaintRes per cell
    edge); drawn as squares it reads as a staircase. Instead, each material's samples become a soft coverage field
    (a 3x3 binomial blur of "is this material"), sampled bilinearly on a lattice twice as fine as the samples, and
    marching squares fill where the field is over Iso. The result is a set of flat triangles (x, z) per material whose
    edges follow curves; GroundLayer lifts them onto the surface a few cm apart per material, the later material on
    top. Iso sits a little under one half so two paints that meet overlap instead of leaving a seam of grass.

    Derived paint: water cells and plain ground next to water read as sand (the shore), as the cell-based drawing did.
    Pure C#, unit-tested.
*/
namespace PalaceMod.Builder.Level
{
    internal static class PaintOutline
    {
        public const float Iso = 0.42f;
        /// <summary>Outline lattice steps per paint sample.</summary>
        public const int Sub = 2;

        /// <summary>The material a paint sample is drawn with (shore and river bed sand included).</summary>
        public static byte Effective(TerrainGrid g, int px, int pz, bool anyWater)
        {
            byte m = g.Materials[g.PaintIndex(px, pz)];
            if (m != (byte)GroundMaterial.Ground || !anyWater) return m;
            int cx = px / TerrainGrid.PaintRes, cz = pz / TerrainGrid.PaintRes;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, nz = cz + dz;
                    if (nx >= 0 && nz >= 0 && nx < g.W && nz < g.H && g.Water[g.CellIndex(nx, nz)]) return (byte)GroundMaterial.Sand;
                }
            return m;
        }

        /// <summary>Which materials (other than plain ground) appear in a cell range, with a margin for the blur.</summary>
        public static bool[] Present(TerrainGrid g, int cx0, int cz0, int cx1, int cz1)
        {
            bool water = g.AnyWater;
            var present = new bool[7];
            int R = TerrainGrid.PaintRes;
            int px0 = Math.Max(0, cx0 * R - 2), pz0 = Math.Max(0, cz0 * R - 2);
            int px1 = Math.Min(g.PaintW - 1, cx1 * R + 1), pz1 = Math.Min(g.PaintH - 1, cz1 * R + 1);
            for (int pz = pz0; pz <= pz1; pz++)
                for (int px = px0; px <= px1; px++)
                    present[Effective(g, px, pz, water)] = true;
            present[(int)GroundMaterial.Ground] = false;
            return present;
        }

        /// <summary>Fills the outline of one material over cells [cx0, cx1) x [cz0, cz1). Writes flat (x, z) triangle
        /// corners, clockwise seen from above (Unity's front face). Cells for which skip is true are left out (cliffs
        /// keep their rock). Returns the triangle count.</summary>
        public static int Build(TerrainGrid g, int cx0, int cz0, int cx1, int cz1, byte material, Func<int, int, bool> skip, List<float> xz)
        {
            int R = TerrainGrid.PaintRes, per = R * Sub;   // lattice steps per cell
            float step = g.PaintStep / Sub;
            // Blurred coverage on the samples the range reads (plus 2 for the blur and the bilinear reach).
            int px0 = Math.Max(0, cx0 * R - 2), pz0 = Math.Max(0, cz0 * R - 2);
            int px1 = Math.Min(g.PaintW - 1, cx1 * R + 1), pz1 = Math.Min(g.PaintH - 1, cz1 * R + 1);
            int bw = px1 - px0 + 1, bh = pz1 - pz0 + 1;
            bool anyWater = g.AnyWater;
            var ind = new float[bw * bh];
            for (int pz = pz0; pz <= pz1; pz++)
                for (int px = px0; px <= px1; px++)
                    ind[(pz - pz0) * bw + px - px0] = Effective(g, px, pz, anyWater) == material ? 1f : 0f;
            var blur = new float[bw * bh];
            for (int z = 0; z < bh; z++)
                for (int x = 0; x < bw; x++)
                {
                    float sum = 0f, wsum = 0f;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            // Clamped into the read box: that only bites at the grid's own edge (the box has a margin).
                            int gx = Math.Max(0, Math.Min(bw - 1, x + dx)), gz = Math.Max(0, Math.Min(bh - 1, z + dz));
                            float w = (dx == 0 ? 2f : 1f) * (dz == 0 ? 2f : 1f);
                            sum += w * ind[gz * bw + gx];
                            wsum += w;
                        }
                    blur[z * bw + x] = sum / wsum;
                }
            // The field on the lattice over the range: vertex (i, j) at origin + (i, j) * step.
            int li0 = cx0 * per, lj0 = cz0 * per, lw = (cx1 - cx0) * per + 1, lh = (cz1 - cz0) * per + 1;
            var f = new float[lw * lh];
            for (int j = 0; j < lh; j++)
                for (int i = 0; i < lw; i++)
                {
                    // Sample centres sit at (k + 0.5) * PaintStep: u is the position in sample units.
                    float u = (li0 + i) / (float)Sub - 0.5f, v = (lj0 + j) / (float)Sub - 0.5f;
                    u = Math.Max(0f, Math.Min(g.PaintW - 1, u));
                    v = Math.Max(0f, Math.Min(g.PaintH - 1, v));
                    int iu = Math.Min((int)u, g.PaintW - 2), iv = Math.Min((int)v, g.PaintH - 2);   // PaintW, PaintH >= 2
                    float tu = u - iu, tv = v - iv;
                    float B(int sx, int sz) => blur[(Math.Max(pz0, Math.Min(pz1, sz)) - pz0) * bw + Math.Max(px0, Math.Min(px1, sx)) - px0];
                    float a = B(iu, iv), b = B(iu + 1, iv), c = B(iu, iv + 1), d = B(iu + 1, iv + 1);
                    f[j * lw + i] = (a + (b - a) * tu) * (1f - tv) + (c + (d - c) * tu) * tv;
                }
            int tris = 0;
            var poly = new List<(float x, float z)>(8);
            for (int cz = cz0; cz < cz1; cz++)
                for (int cx = cx0; cx < cx1; cx++)
                {
                    if (skip != null && skip(cx, cz)) continue;
                    int bi = (cx - cx0) * per, bj = (cz - cz0) * per;
                    bool full = true, empty = true;
                    for (int j = 0; j <= per && (full || empty); j++)
                        for (int i = 0; i <= per; i++)
                        {
                            bool on = f[(bj + j) * lw + bi + i] >= Iso;
                            full &= on;
                            empty &= !on;
                        }
                    if (empty) continue;
                    if (full)
                    {
                        // The whole cell: its two triangles exactly as the ground mesh splits it.
                        float x0 = g.CornerX(cx), z0 = g.CornerZ(cz), x1 = x0 + g.Cell, z1 = z0 + g.Cell;
                        if (g.SplitsMain(cx, cz)) Tris(xz, x0, z0, x0, z1, x1, z1, x0, z0, x1, z1, x1, z0);
                        else Tris(xz, x0, z0, x0, z1, x1, z0, x1, z0, x0, z1, x1, z1);
                        tris += 2;
                        continue;
                    }
                    for (int j = 0; j < per; j++)
                        for (int i = 0; i < per; i++)
                        {
                            int li = bi + i, lj = bj + j;
                            float v00 = f[lj * lw + li], v10 = f[lj * lw + li + 1], v01 = f[(lj + 1) * lw + li], v11 = f[(lj + 1) * lw + li + 1];
                            float x0 = g.OriginX + (li0 + li) * step, z0 = g.OriginZ + (lj0 + lj) * step;
                            tris += Square(xz, poly, x0, z0, step, v00, v01, v11, v10);
                        }
                }
            return tris;
        }

        private static void Tris(List<float> xz, params float[] p) => xz.AddRange(p);

        /// <summary>Marching squares on one lattice square, corners clockwise from above: 00, 01, 11, 10.</summary>
        private static int Square(List<float> xz, List<(float x, float z)> poly, float x0, float z0, float s, float a, float b, float c, float d)
        {
            bool ia = a >= Iso, ib = b >= Iso, ic = c >= Iso, id = d >= Iso;
            if (!ia && !ib && !ic && !id) return 0;
            float x1 = x0 + s, z1 = z0 + s;
            if (ia && ib && ic && id)
            {
                Tris(xz, x0, z0, x0, z1, x1, z1, x0, z0, x1, z1, x1, z0);
                return 2;
            }
            poly.Clear();
            // Walk the corners clockwise, adding each inside corner and each crossing on the edges.
            Edge(poly, ia, ib, x0, z0, x0, z1, a, b);
            Edge(poly, ib, ic, x0, z1, x1, z1, b, c);
            Edge(poly, ic, id, x1, z1, x1, z0, c, d);
            Edge(poly, id, ia, x1, z0, x0, z0, d, a);
            int n = 0;
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                xz.Add(poly[0].x); xz.Add(poly[0].z);
                xz.Add(poly[k].x); xz.Add(poly[k].z);
                xz.Add(poly[k + 1].x); xz.Add(poly[k + 1].z);
                n++;
            }
            return n;
        }

        private static void Edge(List<(float x, float z)> poly, bool inA, bool inB, float ax, float az, float bx, float bz, float va, float vb)
        {
            if (inA) poly.Add((ax, az));
            if (inA == inB) return;
            float t = (Iso - va) / (vb - va);
            poly.Add((ax + (bx - ax) * t, az + (bz - az) * t));
        }
    }
}
