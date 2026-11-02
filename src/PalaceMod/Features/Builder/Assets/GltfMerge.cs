using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    Merges what glTFast loaded (SceneRecorder's primitives and the decoded textures) into one mesh and one texture
    for a unit body. See GltfLoader for why.

    1. Collect: every triangle submesh of every placed primitive, baked into model space, grouped by glTF material.
    2. Paint: each group is textured (its base colour texture times its base colour), vertex-coloured (no texture,
       COLOR_0 present: one flat colour per triangle) or flat (its base colour).
    3. Texture: a single textured material keeps its own texture (so tiling UVs still repeat). Anything else gets an
       atlas: texture tiles, padded with their edge pixels, plus 8x8 swatches for flat colours; tile UVs are wrapped
       into the tile (per triangle, after unwelding, where a material's UVs tile).
*/
namespace PalaceMod.Builder.Assets
{
    /// <summary>The one thing the merge needs from glTFast's import: decoded textures by glTF texture index.</summary>
    internal sealed class GltfTextures(Func<int, Texture2D> get)
    {
        public Texture2D Get(int index) => index < 0 ? null : get(index);
    }

    internal static class GltfMerge
    {
        public const int MaxTile = 1024;
        public const int MaxAtlas = 2048;
        private const int Swatch = 8;        // flat-colour tile size; everything sits on an 8-pixel grid
        private const int Pad = 8;           // around texture tiles (edge pixels repeated), against filtering bleed
        private const int AtlasMips = 4;     // so an 8-pixel swatch never mixes with its neighbours

        private enum Paint { Flat, VertexColour, Textured }

        /// <summary>One material's share of the model: triangles in model space, painted one way.</summary>
        private sealed class Group
        {
            public Paint Paint;
            public Color32 Flat;                    // sRGB, for Flat
            public int Texture = -1;                // glTF texture index, for Textured
            public Color Factor = Color.white;      // linear base colour; multiplies the texture or vertex colours
            public bool Tiles;                      // UVs leave the unit square: unweld and wrap per triangle
            public readonly List<Vector3> Positions = [];
            public readonly List<Vector3> Normals = [];
            public readonly List<Vector2> Uv = [];         // the material's UV set, texture-transformed
            public readonly List<Vector2> Uv0 = [];        // the model's first UV set, untouched
            public readonly List<Color32> Colours = [];    // vertex colours (sRGB)
            public readonly List<int> Triangles = [];
            public int[] TriangleSwatches;                 // VertexColour: atlas swatch per triangle
            public int Tile = -1, FlatSwatch = -1;
        }

        public static void Build(GltfModel model, GltfTextures textures, SceneRecorder scene, string name, GltfResult result)
        {
            var groups = Collect(model, scene);
            if (groups.Sum(g => g.Triangles.Count) == 0) throw new AssetException("glTF scene has no triangles to show");
            foreach (var g in groups)
                if (g.Paint == Paint.Textured && textures.Get(g.Texture) == null)
                {
                    result.Warnings.Add($"texture {g.Texture} couldn't be loaded; using its material's colour");
                    g.Paint = Paint.Flat;
                    g.Flat = (Color32)g.Factor.gamma;
                }

            var keys = groups.Where(g => g.Paint == Paint.Textured).Select(g => (g.Texture, g.Factor)).Distinct().ToList();
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var uv0 = new List<Vector2>();
            var triangles = new List<int>();
            Texture2D texture;
            string paint;

            if (keys.Count == 1 && groups.All(g => g.Paint == Paint.Textured))
            {
                // One texture for the whole model: use it directly; tiling UVs keep working (the texture repeats).
                texture = Resample(textures.Get(keys[0].Texture), MaxAtlas, keys[0].Factor, out int w, out int h);
                foreach (var g in groups) Append(g, positions, normals, uvs, uv0, triangles, (v, _) => g.Uv[v]);
                paint = $"its texture at {w}x{h}";
            }
            else
            {
                // Unwelding tiling and vertex-coloured groups adds vertices; past the limit, simplify them instead.
                long added = groups.Where(Unwelds).Sum(g => (long)g.Triangles.Count - g.Positions.Count);
                bool unweld = groups.Sum(g => (long)g.Positions.Count) + Math.Max(0, added) <= AssetLimits.MaxMeshVertices;
                if (!unweld) result.Warnings.Add("too many vertices to keep tiling textures and vertex colours exact; simplified them");

                var atlas = new Atlas();
                var tileOf = keys.ToDictionary(k => k, k => atlas.AddTile(textures.Get(k.Texture), k.Factor));
                foreach (var g in groups)
                {
                    if (g.Paint == Paint.Textured) g.Tile = tileOf[(g.Texture, g.Factor)];
                    else if (g.Paint == Paint.Flat) g.FlatSwatch = atlas.AddSwatch(g.Flat);
                    else if (!unweld) g.FlatSwatch = atlas.AddSwatch(Multiply(AverageAll(g.Colours), g.Factor));
                    else
                    {
                        g.TriangleSwatches = new int[g.Triangles.Count / 3];
                        for (int t = 0; t < g.TriangleSwatches.Length; t++)
                        {
                            var c = Average(g.Colours[g.Triangles[3 * t]], g.Colours[g.Triangles[3 * t + 1]], g.Colours[g.Triangles[3 * t + 2]]);
                            g.TriangleSwatches[t] = atlas.AddSwatch(Multiply(c, g.Factor));
                        }
                    }
                }
                atlas.Layout();
                foreach (var g in groups)
                {
                    if (g.TriangleSwatches != null)
                        AppendUnwelded(g, positions, normals, uvs, uv0, triangles, (t, _) => atlas.SwatchUv(g.TriangleSwatches[t]));
                    else if (g.FlatSwatch >= 0)
                    {
                        var uv = atlas.SwatchUv(g.FlatSwatch);
                        Append(g, positions, normals, uvs, uv0, triangles, (_, _) => uv);
                    }
                    else if (g.Tiles && unweld)
                        AppendUnwelded(g, positions, normals, uvs, uv0, triangles, (t, corner) =>
                        {
                            Vector2 a = g.Uv[g.Triangles[3 * t]], b = g.Uv[g.Triangles[3 * t + 1]], c = g.Uv[g.Triangles[3 * t + 2]];
                            var shift = new Vector2(Mathf.Floor(Mathf.Min(a.x, b.x, c.x) + 1e-4f), Mathf.Floor(Mathf.Min(a.y, b.y, c.y) + 1e-4f));
                            return atlas.TileUv(g.Tile, g.Uv[g.Triangles[3 * t + corner]] - shift);
                        });
                    else
                    {
                        var shift = Shift(g.Uv);
                        Append(g, positions, normals, uvs, uv0, triangles, (v, _) => atlas.TileUv(g.Tile, g.Uv[v] - shift));
                    }
                }
                texture = atlas.Bake(out int w, out int h);
                paint = $"a {w}x{h} atlas of {keys.Count} texture{(keys.Count == 1 ? "" : "s")} and {atlas.SwatchCount} colour{(atlas.SwatchCount == 1 ? "" : "s")}";
            }
            texture.name = name;

            if (positions.Count > AssetLimits.MaxMeshVertices || triangles.Count / 3 > AssetLimits.MaxMeshTriangles)
            {
                Object.Destroy(texture);
                throw new AssetException($"model has {positions.Count:N0} vertices and {triangles.Count / 3:N0} triangles; the limits are {AssetLimits.MaxMeshVertices:N0} and {AssetLimits.MaxMeshTriangles:N0}");
            }
            Mesh mesh;
            try
            {
                mesh = MeshBuilder.Build(name, positions, uvs, normals, Enumerable.Repeat(Color.white, positions.Count).ToList(), triangles);
            }
            catch
            {
                Object.Destroy(texture);
                throw;
            }
            mesh.SetUVs(1, uv0);
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            result.Mesh = mesh;
            result.Texture = texture;
            result.Summary = $"{positions.Count:N0} vertices, {triangles.Count / 3:N0} triangles, {groups.Count} material{(groups.Count == 1 ? "" : "s")}, painted with {paint}";
        }

        private static bool Unwelds(Group g) => g.Paint == Paint.VertexColour || (g.Paint == Paint.Textured && g.Tiles);

        // ------------------------------------------------------------------ collecting primitives

        /// <summary>Vertex data of one glTFast mesh, read once.</summary>
        private sealed class MeshData
        {
            public readonly Vector3[] Positions, Normals;
            public readonly Color32[] Colours;
            public readonly BoneWeight[] Weights;
            public readonly Matrix4x4[] BindPoses;
            private readonly List<Vector2>[] uvs = new List<Vector2>[8];
            private readonly Mesh mesh;

            public MeshData(Mesh mesh)
            {
                this.mesh = mesh;
                if (mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
                Positions = mesh.vertices;
                Normals = mesh.normals;
                var colours = mesh.colors32;
                Colours = colours.Length == Positions.Length && colours.Any(c => c.r != 255 || c.g != 255 || c.b != 255) ? colours : null;
                Weights = mesh.boneWeights;
                BindPoses = mesh.bindposes;
            }

            public List<Vector2> Uv(int channel)
            {
                if (channel < 0 || channel > 7) return null;
                if (uvs[channel] == null)
                {
                    var list = new List<Vector2>();
                    mesh.GetUVs(channel, list);
                    uvs[channel] = list.Count == Positions.Length ? list : [];
                }
                return uvs[channel].Count == 0 ? null : uvs[channel];
            }

            public int[] Indices(int sub) => mesh.GetIndices(sub);
        }

        /// <summary>Where a primitive goes in model space: a matrix per placement, or per-bone matrices for a
        /// skinned primitive in its rest pose (joint world matrix x bind pose).</summary>
        private readonly struct Placement(Matrix4x4 matrix, Matrix4x4[] bones)
        {
            public readonly Matrix4x4 Matrix = matrix;
            public readonly Matrix4x4[] Bones = bones;
        }

        private static IEnumerable<Placement> Placements(SceneRecorder.Primitive prim, SceneRecorder scene, MeshData data)
        {
            if (prim.Joints != null && prim.Joints.Length > 0 && data.Weights.Length == data.Positions.Length && data.BindPoses.Length >= prim.Joints.Length)
            {
                var bones = new Matrix4x4[prim.Joints.Length];
                for (int b = 0; b < bones.Length; b++) bones[b] = scene.World(prim.Joints[b]) * data.BindPoses[b];
                yield return new Placement(Matrix4x4.identity, bones);
                yield break;
            }
            var node = scene.World(prim.Node);
            if (prim.Instances == null) yield return new Placement(node, null);
            else foreach (var i in prim.Instances) yield return new Placement(node * i, null);
        }

        private static List<Group> Collect(GltfModel model, SceneRecorder scene)
        {
            var groups = new Dictionary<int, Group>();   // by glTF material (-1: the default material)
            var meshData = new Dictionary<Mesh, MeshData>();
            foreach (var prim in scene.Primitives)
            {
                var mesh = prim.Mesh.mesh;
                if (mesh == null) continue;
                if (!meshData.TryGetValue(mesh, out var data)) meshData[mesh] = data = new MeshData(mesh);
                foreach (var placement in Placements(prim, scene, data))
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                        var mats = prim.Mesh.materialIndices;
                        int material = mats != null && sub < mats.Length ? mats[sub] : -1;
                        var info = material >= 0 && material < model.Materials.Count ? model.Materials[material] : null;
                        if (!groups.TryGetValue(material, out var g)) groups[material] = g = NewGroup(info, data);
                        AddSubmesh(g, data, sub, placement, info);
                    }
            }
            return groups.Values.ToList();
        }

        private static Group NewGroup(GltfMaterialInfo info, MeshData data)
        {
            var factor = info == null ? Color.white : new Color(info.BaseColor[0], info.BaseColor[1], info.BaseColor[2], 1f);
            var g = new Group { Factor = factor };
            if (info != null && info.Texture >= 0 && data.Uv(info.TexCoord) != null) { g.Paint = Paint.Textured; g.Texture = info.Texture; }
            else if (data.Colours != null) g.Paint = Paint.VertexColour;
            else { g.Paint = Paint.Flat; g.Flat = (Color32)factor.gamma; }
            return g;
        }

        private static void AddSubmesh(Group g, MeshData data, int sub, Placement placement, GltfMaterialInfo info)
        {
            var indices = data.Indices(sub);
            int n = data.Positions.Length, start = g.Positions.Count;
            var matrix = placement.Matrix;
            var normalMatrix = matrix.inverse.transpose;
            var uvSet = g.Paint == Paint.Textured ? data.Uv(info.TexCoord) : null;
            var uv0 = data.Uv(0);
            var map = new Dictionary<int, int>();   // only the vertices this submesh uses, renumbered
            var tris = new List<int>(indices.Length);
            for (int i = 0; i + 2 < indices.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int v = indices[i + k];
                    if (v < 0 || v >= n) throw new AssetException("glTF is malformed: an index points past its vertices");
                    if (!map.TryGetValue(v, out int to))
                    {
                        to = map[v] = g.Positions.Count;
                        var p = data.Positions[v];
                        var normal = data.Normals.Length == n ? data.Normals[v] : Vector3.up;
                        if (placement.Bones != null)
                        {
                            var m = SkinMatrix(data.Weights[v], placement.Bones);
                            g.Positions.Add(m.MultiplyPoint3x4(p));
                            g.Normals.Add(m.MultiplyVector(normal).normalized);
                        }
                        else
                        {
                            g.Positions.Add(matrix.MultiplyPoint3x4(p));
                            g.Normals.Add(normalMatrix.MultiplyVector(normal).normalized);
                        }
                        g.Uv.Add(uvSet != null ? Transform(uvSet[v], info) : Vector2.zero);
                        g.Uv0.Add(uv0 != null ? uv0[v] : Vector2.zero);
                        g.Colours.Add(data.Colours != null ? data.Colours[v] : new Color32(255, 255, 255, 255));
                    }
                    tris.Add(to);
                }
            // A mirroring transform turns the faces inside out; flip them back.
            if (placement.Bones == null && matrix.determinant < 0)
                for (int i = 0; i < tris.Count; i += 3) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            g.Triangles.AddRange(tris);
            if (g.Paint == Paint.Textured && !g.Tiles)
            {
                var added = g.Uv.Skip(start).ToList();
                var s = Shift(added);
                g.Tiles = added.Any(uv => uv.x - s.x < -1e-3f || uv.x - s.x > 1.001f || uv.y - s.y < -1e-3f || uv.y - s.y > 1.001f);
            }
        }

        private static Matrix4x4 SkinMatrix(BoneWeight w, Matrix4x4[] bones)
        {
            var m = new Matrix4x4();
            float total = 0;
            void Add(int bone, float weight)
            {
                if (weight <= 0 || bone < 0 || bone >= bones.Length) return;
                for (int i = 0; i < 16; i++) m[i] += bones[bone][i] * weight;
                total += weight;
            }
            Add(w.boneIndex0, w.weight0);
            Add(w.boneIndex1, w.weight1);
            Add(w.boneIndex2, w.weight2);
            Add(w.boneIndex3, w.weight3);
            if (total <= 0) return Matrix4x4.identity;
            for (int i = 0; i < 16; i++) m[i] /= total;
            return m;
        }

        /// <summary>KHR_texture_transform, applied in glTF's UV space (V down; glTFast flipped it for Unity).</summary>
        private static Vector2 Transform(Vector2 uv, GltfMaterialInfo info)
        {
            if (info.Offset == null && info.Scale == null && info.Rotation == 0) return uv;
            float x = uv.x * (info.Scale?[0] ?? 1), y = (1 - uv.y) * (info.Scale?[1] ?? 1);
            float cos = Mathf.Cos(info.Rotation), sin = Mathf.Sin(info.Rotation);
            float rx = cos * x + sin * y, ry = -sin * x + cos * y;
            return new Vector2(rx + (info.Offset?[0] ?? 0), 1 - (ry + (info.Offset?[1] ?? 0)));
        }

        /// <summary>The whole-number offset that moves a set of UVs closest to the unit square.</summary>
        private static Vector2 Shift(IEnumerable<Vector2> uvs)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            foreach (var uv in uvs) { minX = Mathf.Min(minX, uv.x); minY = Mathf.Min(minY, uv.y); }
            return minX == float.MaxValue ? Vector2.zero : new Vector2(Mathf.Floor(minX + 1e-3f), Mathf.Floor(minY + 1e-3f));
        }

        // ------------------------------------------------------------------ output

        /// <summary>Adds a group's vertices as they are; <paramref name="uv"/> gives the final UV of a vertex.</summary>
        private static void Append(Group g, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<Vector2> uv0, List<int> triangles, Func<int, int, Vector2> uv)
        {
            int start = positions.Count;
            positions.AddRange(g.Positions);
            normals.AddRange(g.Normals);
            for (int v = 0; v < g.Positions.Count; v++) uvs.Add(uv(v, 0));
            uv0.AddRange(g.Uv0);
            foreach (var t in g.Triangles) triangles.Add(start + t);
        }

        /// <summary>Gives every triangle its own three vertices; <paramref name="uv"/>(triangle, corner) gives their UVs.</summary>
        private static void AppendUnwelded(Group g, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<Vector2> uv0, List<int> triangles, Func<int, int, Vector2> uv)
        {
            for (int t = 0; 3 * t + 2 < g.Triangles.Count; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = g.Triangles[3 * t + k];
                    triangles.Add(positions.Count);
                    positions.Add(g.Positions[v]);
                    normals.Add(g.Normals[v]);
                    uvs.Add(uv(t, k));
                    uv0.Add(g.Uv0[v]);
                }
        }

        // ------------------------------------------------------------------ colour

        private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i => Mathf.GammaToLinearSpace(i / 255f)).ToArray();

        private static byte Encode(float linear) => (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(linear)) * 255f), 0, 255);

        private static Color32 Multiply(Color32 srgb, Color factor) =>
            new(Encode(ToLinear[srgb.r] * factor.r), Encode(ToLinear[srgb.g] * factor.g), Encode(ToLinear[srgb.b] * factor.b), 255);

        private static Color32 Average(Color32 a, Color32 b, Color32 c) => new(
            Encode((ToLinear[a.r] + ToLinear[b.r] + ToLinear[c.r]) / 3), Encode((ToLinear[a.g] + ToLinear[b.g] + ToLinear[c.g]) / 3),
            Encode((ToLinear[a.b] + ToLinear[b.b] + ToLinear[c.b]) / 3), 255);

        private static Color32 AverageAll(List<Color32> colours)
        {
            if (colours.Count == 0) return new Color32(255, 255, 255, 255);
            double r = 0, gr = 0, b = 0;
            foreach (var c in colours) { r += ToLinear[c.r]; gr += ToLinear[c.g]; b += ToLinear[c.b]; }
            return new Color32(Encode((float)(r / colours.Count)), Encode((float)(gr / colours.Count)), Encode((float)(b / colours.Count)), 255);
        }

        private static void Tint(Color32[] pixels, Color factor)
        {
            if (factor.r >= 0.999f && factor.g >= 0.999f && factor.b >= 0.999f) return;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Multiply(pixels[i], factor);
        }

        /// <summary>A texture's sRGB pixels at w x h, read through the GPU (works for any format or readability).</summary>
        private static Color32[] ReadPixels(Texture2D source, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var pixels = copy.GetPixels32();
                Object.Destroy(copy);
                return pixels;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static (int w, int h) FitSize(Texture2D t, int max)
        {
            float s = Mathf.Min(1f, max / (float)Mathf.Max(t.width, t.height));
            return (Mathf.Max(1, Mathf.RoundToInt(t.width * s)), Mathf.Max(1, Mathf.RoundToInt(t.height * s)));
        }

        private static Texture2D Resample(Texture2D source, int max, Color factor, out int w, out int h)
        {
            (w, h) = FitSize(source, max);
            var pixels = ReadPixels(source, w, h);
            Tint(pixels, factor);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 2 };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        // ------------------------------------------------------------------ atlas

        /// <summary>Texture tiles (shelf-packed, padded) and a grid of flat-colour swatches in one texture. Add
        /// tiles and swatches, Layout, then ask for UVs, then Bake.</summary>
        private sealed class Atlas
        {
            private sealed class Tile
            {
                public Texture2D Source;
                public Color Factor;
                public int W, H, X, Y;   // content size, and position inside the padding
            }

            private readonly List<Tile> tiles = [];
            private readonly Dictionary<Color32, int> swatchIndex = [];
            private readonly List<Color32> swatches = [];
            private int swatchX, swatchY, columns = 1, width, height;

            public int SwatchCount => swatches.Count;

            public int AddTile(Texture2D source, Color factor)
            {
                tiles.Add(new Tile { Source = source, Factor = factor });
                return tiles.Count - 1;
            }

            public int AddSwatch(Color32 c)
            {
                c.a = 255;
                if (!swatchIndex.TryGetValue(c, out int i)) { swatchIndex[c] = i = swatches.Count; swatches.Add(c); }
                return i;
            }

            public void Layout()
            {
                columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(swatches.Count)));
                int rows = (swatches.Count + columns - 1) / columns;
                // Swatches beyond what fits are unlikely (2048/8 squared = 65k colours); shrink tiles until all fits.
                for (int max = MaxTile; ; max /= 2)
                {
                    foreach (var t in tiles) (t.W, t.H) = FitSize(t.Source, max);
                    if (Pack(columns * Swatch, rows * Swatch) || max <= 16) break;
                }
            }

            private bool Pack(int blockW, int blockH)
            {
                var boxes = tiles.Select((t, i) => (i, w: Align(t.W + 2 * Pad), h: Align(t.H + 2 * Pad))).ToList();
                if (blockH > 0) boxes.Add((-1, blockW, blockH));
                boxes.Sort((a, b) => b.h.CompareTo(a.h));
                int x = 0, y = 0, shelf = 0, used = 0;
                foreach (var (i, w, h) in boxes)
                {
                    if (w > MaxAtlas) return false;
                    if (x + w > MaxAtlas) { y += shelf; x = 0; shelf = 0; }
                    if (i < 0) { swatchX = x; swatchY = y; }
                    else { tiles[i].X = x + Pad; tiles[i].Y = y + Pad; }
                    x += w;
                    used = Math.Max(used, x);
                    shelf = Math.Max(shelf, h);
                }
                width = Math.Max(Swatch, used);
                height = Math.Max(Swatch, y + shelf);
                return height <= MaxAtlas;
            }

            private static int Align(int v) => (v + Swatch - 1) / Swatch * Swatch;

            private (int x, int y) SwatchAt(int i) => (swatchX + i % columns * Swatch, swatchY + i / columns * Swatch);

            public Vector2 SwatchUv(int i)
            {
                var (x, y) = SwatchAt(i);
                return new Vector2((x + Swatch / 2f) / width, (y + Swatch / 2f) / height);
            }

            /// <summary>A UV in the unit square mapped into a tile, half a texel in from its edges.</summary>
            public Vector2 TileUv(int tile, Vector2 uv)
            {
                var t = tiles[tile];
                float u = Mathf.Lerp(t.X + 0.5f, t.X + t.W - 0.5f, Mathf.Clamp01(uv.x));
                float v = Mathf.Lerp(t.Y + 0.5f, t.Y + t.H - 0.5f, Mathf.Clamp01(uv.y));
                return new Vector2(u / width, v / height);
            }

            public Texture2D Bake(out int w, out int h)
            {
                w = width;
                h = height;
                var pixels = new Color32[w * h];
                for (int s = 0; s < swatches.Count; s++)
                {
                    var (sx, sy) = SwatchAt(s);
                    for (int yy = 0; yy < Swatch; yy++)
                        for (int xx = 0; xx < Swatch; xx++) pixels[(sy + yy) * w + sx + xx] = swatches[s];
                }
                foreach (var t in tiles)
                {
                    var src = ReadPixels(t.Source, t.W, t.H);
                    Tint(src, t.Factor);
                    for (int yy = -Pad; yy < t.H + Pad; yy++)
                    {
                        int ty = t.Y + yy, sy = Mathf.Clamp(yy, 0, t.H - 1);
                        if (ty < 0 || ty >= h) continue;
                        for (int xx = -Pad; xx < t.W + Pad; xx++)
                        {
                            int tx = t.X + xx;
                            if (tx >= 0 && tx < w) pixels[ty * w + tx] = src[sy * t.W + Mathf.Clamp(xx, 0, t.W - 1)];
                        }
                    }
                }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, AtlasMips, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 2 };
                tex.SetPixels32(pixels);
                tex.Apply(true, true);
                return tex;
            }
        }
    }
}
