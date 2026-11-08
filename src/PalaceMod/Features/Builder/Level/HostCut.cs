using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Sculpting a map's own ground (docs/builder-ux.md, "Terrain"): the cells the terrain sculpts are drawn by the ground
    layer, so the host's ground has to make room. Every host ground mesh (the renderers on the Ground layer: the
    combined ground MeshFusion draws, which the graphs rasterize, and the Ground-layer MeshColliders the king and coins
    stand on) is swapped for a copy with the sculpted cells cut out of it: each triangle that reaches into one loses
    exactly the part inside (convex pieces, attributes interpolated), so lowering and digging work as well as raising.
    The originals come back on Restore (the ground layer's Clear).
*/
namespace PalaceMod.Builder.Level
{
    internal static class HostCut
    {
        private sealed class Target
        {
            public MeshFilter Filter;
            public MeshCollider Collider;
            public Mesh Original, Cut;
            public Transform T;
            // The original's data, read once.
            public Vector3[] V, N;
            public Color[] C;
            public List<Vector2> Uv0, Uv1;
            public int[][] Sub;
        }

        private static readonly List<Target> targets = [];
        private static bool[] lastMask;

        public static int Count => targets.Count;
        public static string Describe() => $"{targets.Count} host ground meshes ({targets.FindAll(t => t.Filter != null).Count} drawn, {targets.FindAll(t => t.Collider != null).Count} colliders)";

        /// <summary>Finds the host's ground meshes (before anything is cut).</summary>
        public static void Collect(Scene scene, Func<Transform, bool> owned)
        {
            Restore();
            foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || r.gameObject.scene != scene || r.gameObject.layer != 6 || owned(r.transform)) continue;
                var f = r.GetComponent<MeshFilter>();
                if (f?.sharedMesh == null || !f.sharedMesh.isReadable) continue;
                targets.Add(Read(new Target { Filter = f, Original = f.sharedMesh, T = f.transform }));
            }
            foreach (var c in Resources.FindObjectsOfTypeAll<MeshCollider>())
            {
                if (c == null || c.gameObject.scene != scene || c.gameObject.layer != 6 || owned(c.transform)) continue;
                if (c.sharedMesh == null || !c.sharedMesh.isReadable) continue;
                targets.Add(Read(new Target { Collider = c, Original = c.sharedMesh, T = c.transform }));
            }
        }

        private static Target Read(Target t)
        {
            var m = t.Original;
            t.V = m.vertices;
            t.N = m.normals.Length == t.V.Length ? m.normals : null;
            t.C = m.colors.Length == t.V.Length ? m.colors : null;
            t.Uv0 = []; m.GetUVs(0, t.Uv0); if (t.Uv0.Count != t.V.Length) t.Uv0 = null;
            t.Uv1 = []; m.GetUVs(1, t.Uv1); if (t.Uv1.Count != t.V.Length) t.Uv1 = null;
            t.Sub = new int[m.subMeshCount][];
            for (int s = 0; s < m.subMeshCount; s++) t.Sub[s] = m.GetTriangles(s);
            return t;
        }

        /// <summary>Cuts the grid's sculpted cells out of the host's ground (only when they changed).</summary>
        public static void Apply(TerrainGrid g)
        {
            if (targets.Count == 0 || g?.Host == null) return;
            var mask = new bool[g.W * g.H];
            bool any = false;
            for (int z = 0; z < g.H; z++)
                for (int x = 0; x < g.W; x++)
                    if (g.Sculpted(x, z)) { mask[g.CellIndex(x, z)] = true; any = true; }
            if (lastMask != null && Same(mask, lastMask)) return;
            lastMask = mask;
            var rects = any ? Rects(g, mask) : [];
            foreach (var t in targets) Cut(t, rects);
        }

        private static bool Same(bool[] a, bool[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>The sculpted cells as world rectangles (runs along rows).</summary>
        private static List<Rect> Rects(TerrainGrid g, bool[] mask)
        {
            var list = new List<Rect>();
            for (int z = 0; z < g.H; z++)
            {
                int run = -1;
                for (int x = 0; x <= g.W; x++)
                {
                    bool on = x < g.W && mask[g.CellIndex(x, z)];
                    if (on && run < 0) run = x;
                    if (!on && run >= 0)
                    {
                        list.Add(Rect.MinMaxRect(g.CornerX(run), g.CornerZ(z), g.CornerX(x), g.CornerZ(z + 1)));
                        run = -1;
                    }
                }
            }
            return list;
        }

        public static void Restore()
        {
            foreach (var t in targets)
            {
                if (t.Filter != null && t.Original != null) t.Filter.sharedMesh = t.Original;
                if (t.Collider != null && t.Original != null)
                {
                    t.Collider.sharedMesh = null;
                    t.Collider.sharedMesh = t.Original;
                }
                if (t.Cut != null) UnityEngine.Object.Destroy(t.Cut);
            }
            targets.Clear();
            lastMask = null;
        }

        // A piece of a triangle: points in world xz with their barycentric weights on the triangle.
        private struct P
        {
            public float X, Z, A, B, C;
            public static P Lerp(P p, P q, float t) => new()
            {
                X = p.X + (q.X - p.X) * t, Z = p.Z + (q.Z - p.Z) * t,
                A = p.A + (q.A - p.A) * t, B = p.B + (q.B - p.B) * t, C = p.C + (q.C - p.C) * t,
            };
        }

        private static void Cut(Target t, List<Rect> rects)
        {
            if (rects.Count == 0)
            {
                Swap(t, t.Original);
                return;
            }
            var v = new List<Vector3>(t.V);
            var n = t.N != null ? new List<Vector3>(t.N) : null;
            var c = t.C != null ? new List<Color>(t.C) : null;
            var uv0 = t.Uv0 != null ? new List<Vector2>(t.Uv0) : null;
            var uv1 = t.Uv1 != null ? new List<Vector2>(t.Uv1) : null;
            var world = new Vector3[t.V.Length];
            var m = t.T.localToWorldMatrix;
            for (int i = 0; i < world.Length; i++) world[i] = m.MultiplyPoint3x4(t.V[i]);
            var subs = new List<int>[t.Sub.Length];
            var pieces = new List<List<P>>();
            var next = new List<List<P>>();
            for (int s = 0; s < t.Sub.Length; s++)
            {
                var src = t.Sub[s];
                var dst = subs[s] = new List<int>(src.Length);
                for (int k = 0; k + 2 < src.Length; k += 3)
                {
                    int i0 = src[k], i1 = src[k + 1], i2 = src[k + 2];
                    Vector3 a = world[i0], b = world[i1], cc = world[i2];
                    float minX = Mathf.Min(a.x, b.x, cc.x), maxX = Mathf.Max(a.x, b.x, cc.x), minZ = Mathf.Min(a.z, b.z, cc.z), maxZ = Mathf.Max(a.z, b.z, cc.z);
                    pieces.Clear();
                    bool touched = false;
                    foreach (var r in rects)
                    {
                        if (r.xMin >= maxX || r.xMax <= minX || r.yMin >= maxZ || r.yMax <= minZ) continue;
                        if (!touched)
                        {
                            touched = true;
                            pieces.Add([new P { X = a.x, Z = a.z, A = 1f }, new P { X = b.x, Z = b.z, B = 1f }, new P { X = cc.x, Z = cc.z, C = 1f }]);
                        }
                        next.Clear();
                        foreach (var poly in pieces) Subtract(poly, r, next);
                        (pieces, next) = (next, pieces);
                        if (pieces.Count == 0) break;
                    }
                    if (!touched)
                    {
                        dst.Add(i0); dst.Add(i1); dst.Add(i2);
                        continue;
                    }
                    foreach (var poly in pieces)
                    {
                        int b0 = v.Count;
                        foreach (var p in poly)
                        {
                            v.Add(t.V[i0] * p.A + t.V[i1] * p.B + t.V[i2] * p.C);
                            n?.Add((t.N[i0] * p.A + t.N[i1] * p.B + t.N[i2] * p.C).normalized);
                            c?.Add(t.C[i0] * p.A + t.C[i1] * p.B + t.C[i2] * p.C);
                            uv0?.Add(t.Uv0[i0] * p.A + t.Uv0[i1] * p.B + t.Uv0[i2] * p.C);
                            uv1?.Add(t.Uv1[i0] * p.A + t.Uv1[i1] * p.B + t.Uv1[i2] * p.C);
                        }
                        for (int q = 1; q + 1 < poly.Count; q++) { dst.Add(b0); dst.Add(b0 + q); dst.Add(b0 + q + 1); }
                    }
                }
            }
            var mesh = t.Cut;
            if (mesh == null)
            {
                mesh = t.Cut = new Mesh { name = t.Original.name + " (sculpted)", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            }
            mesh.Clear();
            mesh.SetVertices(v);
            if (n != null) mesh.SetNormals(n);
            if (c != null) mesh.SetColors(c);
            if (uv0 != null) mesh.SetUVs(0, uv0);
            if (uv1 != null) mesh.SetUVs(1, uv1);
            mesh.subMeshCount = subs.Length;
            for (int s = 0; s < subs.Length; s++) mesh.SetTriangles(subs[s], s, false);
            mesh.RecalculateBounds();
            Swap(t, mesh);
        }

        private static void Swap(Target t, Mesh mesh)
        {
            if (t.Filter != null) t.Filter.sharedMesh = mesh;
            if (t.Collider != null)
            {
                t.Collider.sharedMesh = null;
                t.Collider.sharedMesh = mesh;
            }
        }

        /// <summary>A convex polygon minus a rectangle: up to four convex pieces (left, right, and below/above within
        /// the rectangle's columns).</summary>
        private static void Subtract(List<P> poly, Rect r, List<List<P>> outList)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in poly) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
            if (r.xMin >= maxX || r.xMax <= minX || r.yMin >= maxZ || r.yMax <= minZ) { outList.Add(poly); return; }
            Keep(Clip(poly, 0, r.xMin, false), outList);                       // x < xMin
            Keep(Clip(poly, 0, r.xMax, true), outList);                        // x > xMax
            var middle = Clip(Clip(poly, 0, r.xMin, true), 0, r.xMax, false); // xMin..xMax
            Keep(Clip(middle, 1, r.yMin, false), outList);                     // z < zMin
            Keep(Clip(middle, 1, r.yMax, true), outList);                      // z > zMax
        }

        private static void Keep(List<P> poly, List<List<P>> outList)
        {
            if (poly.Count < 3) return;
            // Drop slivers with no area.
            double area = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                area += (double)p.X * q.Z - (double)q.X * p.Z;
            }
            if (Math.Abs(area) > 1e-6) outList.Add(poly);
        }

        /// <summary>Sutherland-Hodgman against one axis line: keeps the side above (keepGreater) or below it.</summary>
        private static List<P> Clip(List<P> poly, int axis, float at, bool keepGreater)
        {
            var result = new List<P>(poly.Count + 2);
            if (poly.Count == 0) return result;
            float Val(P p) => (axis == 0 ? p.X : p.Z) - at;
            bool In(float d) => keepGreater ? d >= 0f : d <= 0f;
            var prev = poly[poly.Count - 1];
            float dp = Val(prev);
            foreach (var cur in poly)
            {
                float dc = Val(cur);
                bool ic = In(dc), ip = In(dp);
                if (ic != ip) result.Add(P.Lerp(prev, cur, dp / (dp - dc)));
                if (ic) result.Add(cur);
                prev = cur;
                dp = dc;
            }
            return result;
        }
    }
}
