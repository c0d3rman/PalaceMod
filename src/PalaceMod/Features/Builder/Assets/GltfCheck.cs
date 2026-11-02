using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
    Reads and validates a glTF 2.0 model (.glb, or .gltf with embedded or adjacent buffers and images) before glTFast
    sees it. glTFast is built for trusted content: its jobs read accessors through raw pointers, so a bad offset or
    count can read past a buffer, it recurses over the node tree, and a few of its checks write straight to the Unity
    log. Everything that could reach those paths is checked here first, so a malformed file becomes one player-facing
    AssetException and glTFast only ever loads files that are structurally sound.

    Checked: the GLB container, JSON (depth-capped), asset version, required extensions, every buffer (embedded BIN,
    base64 data URI, or a file that must resolve inside the pack folder), buffer view and accessor bounds (including
    sparse indices and their values), attribute types and counts per primitive, index values, node references,
    cycles and depth, skins, scenes, images (PNG or JPEG, size read from the header) and texture references. It also
    adds up the vertices and triangles the scene will instance, against the same limits as OBJ meshes.

    Pure C# (Newtonsoft only, no Unity), so tests/PalaceMod.Tests runs it on malformed files.
*/
namespace PalaceMod.Builder.Assets
{
    /// <summary>A validated glTF model, ready for GltfLoader.</summary>
    internal sealed class GltfModel
    {
        public bool Binary;
        /// <summary>The .glb file as given (Binary), for glTFast's LoadGltfBinary.</summary>
        public byte[] Glb;
        /// <summary>The JSON text (.gltf, or the GLB's JSON chunk).</summary>
        public string Json;
        /// <summary>The model file's own path (glTFast resolves relative URIs against it).</summary>
        public string Path;
        /// <summary>External files the model references, by full path. glTFast may load only these.</summary>
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public int Scene;
        public readonly List<GltfMaterialInfo> Materials = [];
        /// <summary>Vertices and triangles the scene instances (before merging).</summary>
        public long Vertices, Triangles;
        public int MeshCount, NodeCount, ImageCount;
        /// <summary>Things that are read but not shown (animations, cameras, ...), for the log.</summary>
        public readonly List<string> Notes = [];
    }

    /// <summary>What the Builder uses of a glTF material: its base colour and base colour texture.</summary>
    internal sealed class GltfMaterialInfo
    {
        public string Name;
        /// <summary>Linear RGBA (glTF's baseColorFactor).</summary>
        public float[] BaseColor = [1, 1, 1, 1];
        /// <summary>glTF texture index of the base colour texture, or -1.</summary>
        public int Texture = -1;
        public int TexCoord;
        /// <summary>KHR_texture_transform of the base colour texture (glTF UV space), or null.</summary>
        public float[] Offset, Scale;
        public float Rotation;
    }

    internal static class GltfCheck
    {
        public const int MaxJsonDepth = 64;
        public const int MaxNodeDepth = 256;
        public const int MaxTextureSide = 4096;
        public const long MaxTexturePixels = 3L * 4096 * 4096;
        public const int MaxElements = 1 << 24;   // accessor counts beyond this are nonsense for a unit

        private static readonly HashSet<string> Unsupported = new()
        {
            "KHR_draco_mesh_compression", "EXT_meshopt_compression", "KHR_meshopt_compression", "KHR_texture_basisu", "EXT_texture_webp", "EXT_texture_avif",
        };

        /// <summary>Validates a model. <paramref name="packFolder"/> bounds where adjacent files may come from (null:
        /// none allowed, e.g. a downloaded file). Throws AssetException with a player-facing reason.</summary>
        public static GltfModel Read(byte[] file, string path, string packFolder)
        {
            if (file == null || file.Length == 0) throw new AssetException("glTF file is empty");
            var model = new GltfModel { Path = path };
            ArraySegment<byte>? bin = null;
            if (file.Length >= 4 && file[0] == 'g' && file[1] == 'l' && file[2] == 'T' && file[3] == 'F')
            {
                model.Binary = true;
                model.Glb = file;
                model.Json = ReadGlb(file, out bin);
            }
            else model.Json = DecodeText(file);

            var root = ParseJson(model.Json);
            var ctx = new Ctx(root, model, bin, path == null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), packFolder);
            ctx.Check(file.Length);
            return model;
        }

        // ------------------------------------------------------------------ container

        private static string ReadGlb(byte[] b, out ArraySegment<byte>? bin)
        {
            bin = null;
            if (b.Length < 20) throw new AssetException("GLB is truncated");
            uint version = BitConverter.ToUInt32(b, 4), length = BitConverter.ToUInt32(b, 8);
            if (version != 2) throw new AssetException($"GLB version {version} isn't supported (glTF 2.0 only)");
            if (length > b.Length) throw new AssetException("GLB is truncated");
            if (length < 20) throw new AssetException("GLB header is damaged");
            string json = null;
            long pos = 12;
            for (int chunk = 0; pos + 8 <= length; chunk++)
            {
                uint size = BitConverter.ToUInt32(b, (int)pos), type = BitConverter.ToUInt32(b, (int)pos + 4);
                long body = pos + 8;
                if (size > length - body) throw new AssetException("GLB is truncated (a chunk runs past the end)");
                if (chunk == 0)
                {
                    if (type != 0x4E4F534A) throw new AssetException("GLB doesn't start with a JSON chunk");
                    json = DecodeText(new ArraySegment<byte>(b, (int)body, (int)size).ToArray());
                }
                else if (chunk == 1 && type == 0x004E4942) bin = new ArraySegment<byte>(b, (int)body, (int)size);
                pos = body + size;
                pos += (4 - (pos & 3)) & 3;
            }
            return json ?? throw new AssetException("GLB has no JSON chunk");
        }

        private static string DecodeText(byte[] bytes)
        {
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
                return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
            }
            catch (ArgumentException)
            {
                throw new AssetException("glTF JSON isn't valid UTF-8 text");
            }
        }

        private static JObject ParseJson(string json)
        {
            try
            {
                using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = MaxJsonDepth, DateParseHandling = DateParseHandling.None };
                var token = JToken.ReadFrom(reader);
                if (reader.Read() && reader.TokenType != JsonToken.Comment) throw new AssetException("glTF JSON has extra text after the end");
                return token as JObject ?? throw new AssetException("glTF JSON isn't an object");
            }
            catch (JsonException e)
            {
                throw new AssetException("glTF JSON is invalid: " + Short(e.Message));
            }
        }

        private static string Short(string s) => s.Length > 120 ? s.Substring(0, 120) + "..." : s;

        // ------------------------------------------------------------------ validation

        private sealed class Ctx(JObject root, GltfModel model, ArraySegment<byte>? bin, string baseDir, string packFolder)
        {
            private readonly List<ArraySegment<byte>> buffers = [];
            private JArray accessors, bufferViews, meshes, nodes, images, textures;
            private long externalBytes;

            public void Check(long fileBytes)
            {
                externalBytes = fileBytes;
                var asset = root["asset"] as JObject ?? throw Bad("has no \"asset\" (not a glTF 2.0 file)");
                string version = asset["version"]?.Type == JTokenType.String ? (string)asset["version"] : null;
                if (version == null || !version.StartsWith("2")) throw new AssetException($"glTF version {version ?? "(missing)"} isn't supported (glTF 2.0 only)");

                foreach (var ext in Strings(root["extensionsRequired"], "extensionsRequired"))
                    if (Unsupported.Contains(ext)) throw new AssetException(UnsupportedMessage(ext));
                foreach (var ext in Strings(root["extensionsUsed"], "extensionsUsed"))
                    if (Unsupported.Contains(ext)) model.Notes.Add($"ignores {ext}");

                accessors = Array("accessors");
                bufferViews = Array("bufferViews");
                meshes = Array("meshes");
                nodes = Array("nodes");
                images = Array("images");
                textures = Array("textures");
                var samplers = Array("samplers");

                CheckBuffers();
                for (int i = 0; i < bufferViews.Count; i++) CheckBufferView(i);
                for (int i = 0; i < accessors.Count; i++) CheckAccessor(i);
                for (int i = 0; i < meshes.Count; i++) CheckMesh(i);
                CheckImages();
                for (int i = 0; i < textures.Count; i++)
                {
                    var t = Obj(textures[i], $"textures[{i}]");
                    OptIndex(t, "source", images.Count, $"textures[{i}]");
                    OptIndex(t, "sampler", samplers.Count, $"textures[{i}]");
                }
                ReadMaterials();
                var skins = Array("skins");
                for (int i = 0; i < skins.Count; i++) CheckSkin(i);
                CheckNodes(skins.Count);
                CountScene();
                if (Array("animations").Count > 0) model.Notes.Add("animations aren't played (the unit bobs with its base's walk)");
                model.MeshCount = meshes.Count;
                model.NodeCount = nodes.Count;
                model.ImageCount = images.Count;
            }

            private static string UnsupportedMessage(string ext) => ext switch
            {
                "KHR_draco_mesh_compression" => "uses Draco mesh compression, which isn't supported; export without it",
                "EXT_meshopt_compression" or "KHR_meshopt_compression" => "uses meshopt compression, which isn't supported; export without it",
                "KHR_texture_basisu" => "uses KTX2/Basis textures, which aren't supported; use PNG or JPEG",
                _ => $"uses {ext} textures, which aren't supported; use PNG or JPEG",
            };

            // -------- buffers

            private void CheckBuffers()
            {
                var list = Array("buffers");
                for (int i = 0; i < list.Count; i++)
                {
                    var b = Obj(list[i], $"buffers[{i}]");
                    long length = Int(b, "byteLength", $"buffers[{i}]", min: 1);
                    string uri = Str(b, "uri", $"buffers[{i}]");
                    ArraySegment<byte> data;
                    if (uri == null)
                    {
                        if (i != 0 || bin == null) throw Bad($"buffers[{i}] has no data (no uri and no GLB binary chunk)");
                        data = bin.Value;
                    }
                    else data = new ArraySegment<byte>(Load(uri, $"buffers[{i}]"));
                    if (data.Count < length) throw Bad($"buffers[{i}] is {data.Count} bytes but declares {length}");
                    buffers.Add(new ArraySegment<byte>(data.Array, data.Offset, (int)length));
                }
            }

            /// <summary>Bytes behind a URI: a base64 data URI, or a file next to the model inside the pack folder.</summary>
            private byte[] Load(string uri, string where)
            {
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = uri.IndexOf(',');
                    if (comma < 0 || !uri.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                        throw Bad($"{where} has a data URI that isn't base64");
                    try { return Convert.FromBase64String(uri.Substring(comma + 1)); }
                    catch (FormatException) { throw Bad($"{where} has a damaged base64 data URI"); }
                }
                string rel = Uri.UnescapeDataString(uri);
                // No schemes (http:, file:, C:), no rooted paths: only paths relative to the model.
                if (rel.Length == 0 || rel.IndexOf(':') >= 0 || rel[0] == '/' || rel[0] == '\\' || rel.IndexOf('\0') >= 0)
                    throw new AssetException($"{where} links to \"{Short(uri)}\"; only files next to the model are allowed (or use a .glb)");
                if (baseDir == null || packFolder == null)
                    throw new AssetException($"{where} needs the file \"{Short(rel)}\", but this model has no folder (embed it, or use a .glb)");
                string folder = System.IO.Path.GetFullPath(packFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                string full;
                try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel)); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw Bad($"{where} has an invalid file name"); }
                if (!full.StartsWith(folder, StringComparison.Ordinal)) throw new AssetException($"{where} points outside the pack folder (\"{Short(rel)}\")");
                if (model.Files.TryGetValue(full, out var cached)) return cached;
                if (!File.Exists(full)) throw new AssetException($"{where} needs \"{Short(rel)}\", which isn't in the pack");
                long size = new FileInfo(full).Length;
                externalBytes += size;
                if (externalBytes > AssetLimits.MaxModelBytes) throw new AssetException($"the model's files add up to more than {AssetLimits.Mb(AssetLimits.MaxModelBytes)}");
                var bytes = File.ReadAllBytes(full);
                model.Files[full] = bytes;
                return bytes;
            }

            private void CheckBufferView(int i)
            {
                string w = $"bufferViews[{i}]";
                var v = Obj(bufferViews[i], w);
                int buffer = Index(v, "buffer", buffers.Count, w);
                long offset = Int(v, "byteOffset", w, def: 0), length = Int(v, "byteLength", w, min: 1);
                if (offset + length > buffers[buffer].Count) throw Bad($"{w} runs past the end of buffers[{buffer}]");
                if (v["byteStride"] != null)
                {
                    long stride = Int(v, "byteStride", w);
                    if (stride < 4 || stride > 252 || stride % 4 != 0) throw Bad($"{w} has an invalid byteStride {stride}");
                }
            }

            private static int ComponentSize(long type) => type switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                5125 or 5126 => 4,
                _ => 0,
            };

            private static int Components(string type) => type switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => 0,
            };

            private static int ElementSize(string type, int componentSize) => (type, componentSize) switch
            {
                ("MAT2", 1) => 8,
                ("MAT3", 1) => 12,
                ("MAT3", 2) => 24,
                _ => Components(type) * componentSize,
            };

            private void CheckAccessor(int i)
            {
                string w = $"accessors[{i}]";
                var a = Obj(accessors[i], w);
                long ct = Int(a, "componentType", w);
                int cs = ComponentSize(ct);
                if (cs == 0) throw Bad($"{w} has an invalid componentType {ct}");
                string type = Str(a, "type", w) ?? throw Bad($"{w} has no type");
                if (Components(type) == 0) throw Bad($"{w} has an invalid type \"{Short(type)}\"");
                long count = Int(a, "count", w, min: 1);
                if (count > MaxElements) throw new AssetException($"{w} has {count} elements; that's far too many for a unit model");
                if (a["bufferView"] != null)
                {
                    int view = Index(a, "bufferView", bufferViews.Count, w);
                    CheckRange(view, Int(a, "byteOffset", w, def: 0), count, ElementSize(type, cs), w, vertexStride: true);
                }
                if (a["sparse"] is JToken sparseToken)
                {
                    var sparse = Obj(sparseToken, w + ".sparse");
                    long n = Int(sparse, "count", w + ".sparse", min: 1);
                    if (n > count) throw Bad($"{w}.sparse has more entries than the accessor");
                    var idx = Obj(sparse["indices"], w + ".sparse.indices");
                    var val = Obj(sparse["values"], w + ".sparse.values");
                    long ict = Int(idx, "componentType", w + ".sparse.indices");
                    if (ict is not (5121 or 5123 or 5125)) throw Bad($"{w}.sparse.indices has an invalid componentType {ict}");
                    int iview = Index(idx, "bufferView", bufferViews.Count, w + ".sparse.indices");
                    long ioffset = Int(idx, "byteOffset", w + ".sparse.indices", def: 0);
                    CheckRange(iview, ioffset, n, ComponentSize(ict), w + ".sparse.indices", vertexStride: false);
                    int vview = Index(val, "bufferView", bufferViews.Count, w + ".sparse.values");
                    CheckRange(vview, Int(val, "byteOffset", w + ".sparse.values", def: 0), n, ElementSize(type, cs), w + ".sparse.values", vertexStride: false);
                    var (data, start) = ViewData(iview, ioffset);
                    int ics = ComponentSize(ict);
                    for (long k = 0; k < n; k++)
                        if (ReadUInt(data, start + k * ics, ics) >= count) throw Bad($"{w}.sparse has an index past the accessor's {count} elements");
                }
            }

            private void CheckRange(int view, long offset, long count, int elementSize, string w, bool vertexStride)
            {
                var v = (JObject)bufferViews[view];
                long length = Int(v, "byteLength", w);
                long stride = vertexStride && v["byteStride"] != null ? Int(v, "byteStride", w) : elementSize;
                if (stride < elementSize) throw Bad($"{w} doesn't fit its bufferView's byteStride");
                if (offset < 0 || offset + (count - 1) * stride + elementSize > length) throw Bad($"{w} runs past the end of bufferViews[{view}]");
            }

            private (ArraySegment<byte> data, long start) ViewData(int view, long accessorOffset)
            {
                var v = (JObject)bufferViews[view];
                return (buffers[(int)Int(v, "buffer", "")], Int(v, "byteOffset", "", def: 0) + accessorOffset);
            }

            private static long ReadUInt(ArraySegment<byte> d, long at, int size)
            {
                int p = d.Offset + (int)at;
                return size switch
                {
                    1 => d.Array[p],
                    2 => BitConverter.ToUInt16(d.Array, p),
                    _ => BitConverter.ToUInt32(d.Array, p),
                };
            }

            // -------- meshes

            private static readonly Dictionary<string, (string[] types, long[] components)> AttributeRules = new()
            {
                ["POSITION"] = (["VEC3"], [5126, 5120, 5121, 5122, 5123]),
                ["NORMAL"] = (["VEC3"], [5126, 5120, 5122]),
                ["TANGENT"] = (["VEC4"], [5126, 5120, 5122]),
                ["TEXCOORD"] = (["VEC2"], [5126, 5120, 5121, 5122, 5123]),
                ["COLOR"] = (["VEC3", "VEC4"], [5126, 5121, 5123]),
                ["JOINTS"] = (["VEC4"], [5121, 5123]),
                ["WEIGHTS"] = (["VEC4"], [5126, 5121, 5123]),
            };

            private readonly Dictionary<int, (long vertices, long triangles)> meshCounts = [];

            private void CheckMesh(int i)
            {
                string w = $"meshes[{i}]";
                var m = Obj(meshes[i], w);
                var prims = m["primitives"] as JArray;
                if (prims == null || prims.Count == 0) throw Bad($"{w} has no primitives");
                long vertices = 0, triangles = 0;
                for (int p = 0; p < prims.Count; p++)
                {
                    string pw = $"{w}.primitives[{p}]";
                    var prim = Obj(prims[p], pw);
                    if (prim["extensions"] is JObject pext && pext.Property("KHR_draco_mesh_compression") != null)
                        throw new AssetException(UnsupportedMessage("KHR_draco_mesh_compression"));
                    var attrs = Obj(prim["attributes"], pw + ".attributes");
                    if (attrs["POSITION"] == null) throw Bad($"{pw} has no POSITION attribute");
                    long count = CheckAttributes(attrs, pw + ".attributes", -1);
                    if (prim["targets"] is JToken targets)
                    {
                        if (targets is not JArray ta) throw Bad($"{pw}.targets isn't a list");
                        for (int t = 0; t < ta.Count; t++) CheckAttributes(Obj(ta[t], $"{pw}.targets[{t}]"), $"{pw}.targets[{t}]", count);
                    }
                    long mode = Int(prim, "mode", pw, def: 4);
                    if (mode < 0 || mode > 6) throw Bad($"{pw} has an invalid mode {mode}");
                    OptIndex(prim, "material", Array("materials").Count, pw);
                    long elements = count;
                    if (prim["indices"] != null)
                    {
                        int ia = Index(prim, "indices", accessors.Count, pw);
                        var acc = (JObject)accessors[ia];
                        long ct = Int(acc, "componentType", pw);
                        if ((string)acc["type"] != "SCALAR" || ct is not (5121 or 5123 or 5125)) throw Bad($"{pw}.indices must be unsigned integer scalars");
                        elements = Int(acc, "count", pw);
                        CheckIndexValues(acc, count, pw);
                    }
                    vertices += count;
                    triangles += mode switch { 4 => elements / 3, 5 or 6 => Math.Max(0, elements - 2), _ => 0 };
                    if (mode is not (4 or 5 or 6)) model.Notes.Add($"{pw}: points and lines aren't shown");
                }
                meshCounts[i] = (vertices, triangles);
            }

            /// <summary>Checks a primitive's (or morph target's) attributes; returns their shared element count.</summary>
            private long CheckAttributes(JObject attrs, string w, long expected)
            {
                long count = expected;
                foreach (var prop in attrs.Properties())
                {
                    int a = Index(attrs, prop.Name, accessors.Count, w);
                    var acc = (JObject)accessors[a];
                    string semantic = prop.Name;
                    int us = semantic.IndexOf('_');
                    string family = us > 0 && semantic[0] != '_' && char.IsDigit(semantic.LastOrDefault()) ? semantic.Substring(0, us) : semantic;
                    if (AttributeRules.TryGetValue(family, out var rule))
                    {
                        string type = (string)acc["type"];
                        long ct = Int(acc, "componentType", w);
                        if (!rule.types.Contains(type) || !rule.components.Contains(ct))
                            throw Bad($"{w}.{semantic} has the wrong type ({type}, componentType {ct})");
                    }
                    long n = Int(acc, "count", w);
                    if (count < 0) count = n;
                    else if (n != count) throw Bad($"{w}.{semantic} has {n} elements but the other attributes have {count}");
                }
                if (count < 0) throw Bad($"{w} has no attributes");
                return count;
            }

            private void CheckIndexValues(JObject acc, long vertexCount, string w)
            {
                if (acc["bufferView"] == null) return;   // all zero, plus sparse entries (their indices are checked; values below)
                long ct = Int(acc, "componentType", w);
                int size = ComponentSize(ct);
                int view = (int)Int(acc, "bufferView", w);
                var v = (JObject)bufferViews[view];
                long stride = v["byteStride"] != null ? Int(v, "byteStride", w) : size;
                var (data, start) = ViewData(view, Int(acc, "byteOffset", w, def: 0));
                long n = Int(acc, "count", w);
                for (long k = 0; k < n; k++)
                    if (ReadUInt(data, start + k * stride, size) >= vertexCount) throw Bad($"{w} has an index past its {vertexCount} vertices");
                if (acc["sparse"] is JObject sparse)
                {
                    var val = (JObject)sparse["values"];
                    int vview = (int)Int(val, "bufferView", w);
                    var (vdata, vstart) = ViewData(vview, Int(val, "byteOffset", w, def: 0));
                    long sn = Int(sparse, "count", w);
                    for (long k = 0; k < sn; k++)
                        if (ReadUInt(vdata, vstart + k * size, size) >= vertexCount) throw Bad($"{w} has an index past its {vertexCount} vertices");
                }
            }

            // -------- images, textures, materials

            private void CheckImages()
            {
                long pixels = 0;
                for (int i = 0; i < images.Count; i++)
                {
                    string w = $"images[{i}]";
                    var img = Obj(images[i], w);
                    byte[] bytes;
                    int offset = 0, length;
                    string uri = Str(img, "uri", w);
                    if (uri != null) { bytes = Load(uri, w); length = bytes.Length; }
                    else if (img["bufferView"] != null)
                    {
                        int view = Index(img, "bufferView", bufferViews.Count, w);
                        var v = (JObject)bufferViews[view];
                        var (data, start) = ViewData(view, 0);
                        bytes = data.Array;
                        offset = data.Offset + (int)start;
                        length = (int)Int(v, "byteLength", w);
                    }
                    else throw Bad($"{w} has no data");
                    var (iw, ih) = ImageSize(bytes, offset, length, w);
                    if (iw <= 0 || ih <= 0) throw Bad($"{w} has no pixels");
                    if (iw > MaxTextureSide || ih > MaxTextureSide)
                        throw new AssetException($"{w} is {iw}x{ih}; the limit is {MaxTextureSide}x{MaxTextureSide} (resize the textures)");
                    pixels += (long)iw * ih;
                    if (pixels > MaxTexturePixels) throw new AssetException("the model's textures are too big in total (resize them)");
                }
            }

            /// <summary>Width and height from a PNG or JPEG header, without decoding.</summary>
            internal static (int w, int h) ImageSize(byte[] b, int at, int length, string w)
            {
                if (length >= 24 && b[at] == 0x89 && b[at + 1] == 'P' && b[at + 2] == 'N' && b[at + 3] == 'G')
                {
                    if (b[at + 12] != 'I' || b[at + 13] != 'H' || b[at + 14] != 'D' || b[at + 15] != 'R') throw new AssetException($"{w} is a damaged PNG");
                    return (BigEndian(b, at + 16), BigEndian(b, at + 20));
                }
                if (length >= 4 && b[at] == 0xFF && b[at + 1] == 0xD8)
                {
                    int p = at + 2, end = at + length;
                    while (p + 4 <= end)
                    {
                        if (b[p] != 0xFF) throw new AssetException($"{w} is a damaged JPEG");
                        byte marker = b[p + 1];
                        if (marker == 0xFF) { p++; continue; }
                        if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
                        int segment = (b[p + 2] << 8) | b[p + 3];
                        if (segment < 2) throw new AssetException($"{w} is a damaged JPEG");
                        bool sof = marker >= 0xC0 && marker <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                        if (sof)
                        {
                            if (p + 9 > end) break;
                            return ((b[p + 7] << 8) | b[p + 8], (b[p + 5] << 8) | b[p + 6]);
                        }
                        if (marker == 0xDA) break;   // start of scan before a frame header
                        p += 2 + segment;
                    }
                    throw new AssetException($"{w} is a damaged JPEG (no size found)");
                }
                if (length >= 12 && Encoding.ASCII.GetString(b, at, 4) == "RIFF" && Encoding.ASCII.GetString(b, at + 8, 4) == "WEBP")
                    throw new AssetException($"{w} is a WebP image; use PNG or JPEG");
                if (length >= 12 && b[at] == 0xAB && b[at + 1] == 'K' && b[at + 2] == 'T' && b[at + 3] == 'X')
                    throw new AssetException($"{w} is a KTX image; use PNG or JPEG");
                throw new AssetException($"{w} isn't a PNG or JPEG image");
            }

            private static int BigEndian(byte[] b, int p) => (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];

            private void ReadMaterials()
            {
                var list = Array("materials");
                for (int i = 0; i < list.Count; i++)
                {
                    string w = $"materials[{i}]";
                    var m = Obj(list[i], w);
                    var info = new GltfMaterialInfo { Name = m["name"]?.Type == JTokenType.String ? (string)m["name"] : null };
                    foreach (var key in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
                        if (m[key] != null) Index(Obj(m[key], $"{w}.{key}"), "index", textures.Count, $"{w}.{key}");
                    if (m["pbrMetallicRoughness"] is JToken pbrToken)
                    {
                        var pbr = Obj(pbrToken, w + ".pbrMetallicRoughness");
                        if (pbr["baseColorFactor"] is JToken f)
                        {
                            var factor = Numbers(f, w + ".baseColorFactor");
                            if (factor.Length != 4) throw Bad($"{w}.baseColorFactor needs 4 numbers");
                            info.BaseColor = factor.Select(x => Math.Max(0f, Math.Min(1f, x))).ToArray();
                        }
                        if (pbr["metallicRoughnessTexture"] != null) Index(Obj(pbr["metallicRoughnessTexture"], w), "index", textures.Count, w + ".metallicRoughnessTexture");
                        if (pbr["baseColorTexture"] is JToken t)
                        {
                            var tex = Obj(t, w + ".baseColorTexture");
                            info.Texture = Index(tex, "index", textures.Count, w + ".baseColorTexture");
                            info.TexCoord = (int)Int(tex, "texCoord", w + ".baseColorTexture", def: 0);
                            if (info.TexCoord < 0 || info.TexCoord > 7) throw Bad($"{w}.baseColorTexture.texCoord is out of range");
                            if (tex["extensions"]?["KHR_texture_transform"] is JObject tt)
                            {
                                info.Offset = tt["offset"] != null ? Numbers(tt["offset"], w) : null;
                                info.Scale = tt["scale"] != null ? Numbers(tt["scale"], w) : null;
                                info.Rotation = tt["rotation"] != null ? (float)Num(tt["rotation"], w) : 0f;
                                if (tt["texCoord"] != null) info.TexCoord = (int)Math.Max(0, Math.Min(7, Num(tt["texCoord"], w)));
                                if ((info.Offset != null && info.Offset.Length != 2) || (info.Scale != null && info.Scale.Length != 2))
                                    throw Bad($"{w} has an invalid KHR_texture_transform");
                            }
                            var texture = (JObject)textures[info.Texture];
                            if (texture["source"] == null)
                                throw new AssetException(texture["extensions"] is JObject te && te.Property("KHR_texture_basisu") != null
                                    ? UnsupportedMessage("KHR_texture_basisu") : UnsupportedMessage("EXT_texture_webp"));
                        }
                    }
                    model.Materials.Add(info);
                }
            }

            // -------- nodes and scenes

            private void CheckSkin(int i)
            {
                string w = $"skins[{i}]";
                var s = Obj(root["skins"][i], w);
                if (s["joints"] is not JArray joints || joints.Count == 0) throw Bad($"{w} has no joints");
                for (int j = 0; j < joints.Count; j++)
                {
                    long n = Num(joints[j], w);
                    if (n < 0 || n >= nodes.Count) throw Bad($"{w}.joints[{j}] points to a missing node");
                }
                OptIndex(s, "skeleton", nodes.Count, w);
                if (s["inverseBindMatrices"] != null)
                {
                    int a = Index(s, "inverseBindMatrices", accessors.Count, w);
                    var acc = (JObject)accessors[a];
                    if ((string)acc["type"] != "MAT4" || Int(acc, "componentType", w) != 5126 || Int(acc, "count", w) < joints.Count)
                        throw Bad($"{w}.inverseBindMatrices must be one float MAT4 per joint");
                }
            }

            private int[] parent;

            private void CheckNodes(int skinCount)
            {
                parent = Enumerable.Repeat(-1, nodes.Count).ToArray();
                int cameras = Array("cameras").Count;
                int lights = (root["extensions"]?["KHR_lights_punctual"]?["lights"] as JArray)?.Count ?? 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    string w = $"nodes[{i}]";
                    var n = Obj(nodes[i], w);
                    OptIndex(n, "mesh", meshes.Count, w);
                    OptIndex(n, "skin", skinCount, w);
                    OptIndex(n, "camera", cameras, w);
                    if (n["extensions"]?["KHR_lights_punctual"] is JObject l) OptIndex(l, "light", lights, w + ".KHR_lights_punctual");
                    foreach (var key in new[] { "translation", "rotation", "scale", "matrix" })
                    {
                        if (n[key] == null) continue;
                        var v = Numbers(n[key], $"{w}.{key}");
                        int expected = key switch { "translation" or "scale" => 3, "rotation" => 4, _ => 16 };
                        if (v.Length != expected) throw Bad($"{w}.{key} needs {expected} numbers");
                        if (v.Any(x => float.IsNaN(x) || float.IsInfinity(x))) throw Bad($"{w}.{key} isn't a finite number");
                    }
                    if (n["weights"] != null) Numbers(n["weights"], w + ".weights");
                    if (n["children"] is JToken c)
                    {
                        if (c is not JArray children) throw Bad($"{w}.children isn't a list");
                        foreach (var child in children)
                        {
                            long k = Num(child, w + ".children");
                            if (k < 0 || k >= nodes.Count) throw Bad($"{w}.children points to a missing node");
                            if (k == i) throw Bad($"{w} is its own child");
                            if (parent[k] >= 0) throw Bad($"nodes[{k}] has two parents");
                            parent[k] = i;
                        }
                    }
                    if (n["extensions"]?["EXT_mesh_gpu_instancing"]?["attributes"] is JObject inst) CheckAttributes(inst, w + ".EXT_mesh_gpu_instancing", -1);
                }
                // A node whose parent chain loops never reaches a root.
                for (int i = 0; i < nodes.Count; i++)
                {
                    int depth = 0;
                    for (int p = parent[i]; p >= 0; p = parent[p])
                        if (++depth > MaxNodeDepth) throw Bad(depth > nodes.Count ? "has a loop in its node tree" : $"node tree is deeper than {MaxNodeDepth} levels");
                }
            }

            private void CountScene()
            {
                var scenes = Array("scenes");
                if (scenes.Count == 0) throw new AssetException("glTF has no scene to show");
                model.Scene = (int)Int(root, "scene", "scene", def: 0);
                if (model.Scene < 0 || model.Scene >= scenes.Count) throw Bad("\"scene\" points to a missing scene");
                for (int s = 0; s < scenes.Count; s++)
                {
                    var scene = Obj(scenes[s], $"scenes[{s}]");
                    if (scene["nodes"] == null) continue;
                    if (scene["nodes"] is not JArray roots) throw Bad($"scenes[{s}].nodes isn't a list");
                    foreach (var r in roots)
                    {
                        long n = Num(r, $"scenes[{s}].nodes");
                        if (n < 0 || n >= nodes.Count) throw Bad($"scenes[{s}] points to a missing node");
                        if (parent[n] >= 0) throw Bad($"scenes[{s}] lists nodes[{n}], which is another node's child");
                    }
                }
                var main = (JObject)scenes[model.Scene];
                var stack = new Stack<int>();
                if (main["nodes"] is JArray mainRoots) foreach (var r in mainRoots) stack.Push((int)Num(r, "scene"));
                var children = new List<int>[nodes.Count];
                for (int i = 0; i < nodes.Count; i++) if (parent[i] >= 0) (children[parent[i]] ??= []).Add(i);
                bool meshes = false;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    var n = (JObject)nodes[i];
                    if (n["mesh"] != null)
                    {
                        long instances = 1;
                        if (n["extensions"]?["EXT_mesh_gpu_instancing"]?["attributes"] is JObject inst && inst.Properties().FirstOrDefault() is JProperty first)
                            instances = Int((JObject)accessors[(int)Num(first.Value, "")], "count", "");
                        var (v, t) = meshCounts[(int)Num(n["mesh"], "")];
                        model.Vertices += v * instances;
                        model.Triangles += t * instances;
                        meshes = true;
                        if (n["skin"] != null && !model.Notes.Contains("skinned meshes are shown in their rest pose")) model.Notes.Add("skinned meshes are shown in their rest pose");
                        if (model.Vertices > AssetLimits.MaxMeshVertices)
                            throw new AssetException($"model has more than {AssetLimits.MaxMeshVertices:N0} vertices; simplify it");
                        if (model.Triangles > AssetLimits.MaxMeshTriangles)
                            throw new AssetException($"model has more than {AssetLimits.MaxMeshTriangles:N0} triangles; simplify it");
                    }
                    if (children[i] != null) foreach (var c in children[i]) stack.Push(c);
                }
                if (!meshes) throw new AssetException("glTF scene has no meshes");
                if (model.Triangles == 0) throw new AssetException("glTF scene has no triangles (only points or lines)");
            }

            // -------- JSON helpers

            private JArray Array(string key)
            {
                var t = root[key];
                if (t == null || t.Type == JTokenType.Null) return [];
                return t as JArray ?? throw Bad($"\"{key}\" isn't a list");
            }

            private static JObject Obj(JToken t, string w) => t as JObject ?? throw Bad($"{w} isn't an object");

            private static string Str(JObject o, string key, string w)
            {
                var t = o[key];
                if (t == null || t.Type == JTokenType.Null) return null;
                return t.Type == JTokenType.String ? (string)t : throw Bad($"{w}.{key} isn't text");
            }

            private static IEnumerable<string> Strings(JToken t, string w)
            {
                if (t == null || t.Type == JTokenType.Null) return [];
                if (t is not JArray a) throw Bad($"\"{w}\" isn't a list");
                return a.Select(x => x.Type == JTokenType.String ? (string)x : throw Bad($"\"{w}\" has a non-text entry"));
            }

            private static long Int(JObject o, string key, string w, long? def = null, long min = 0)
            {
                var t = o[key];
                if (t == null || t.Type == JTokenType.Null)
                    return def ?? throw Bad($"{w} has no {key}");
                long v = Num(t, $"{w}.{key}");
                if (v < min) throw Bad($"{w}.{key} is out of range ({v})");
                return v;
            }

            private static long Num(JToken t, string w)
            {
                if (t.Type == JTokenType.Integer)
                {
                    try { return (long)t; }
                    catch (OverflowException) { throw Bad($"{w} is out of range"); }
                }
                if (t.Type == JTokenType.Float)
                {
                    double d = (double)t;
                    if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
                }
                throw Bad($"{w} isn't a whole number");
            }

            private static float[] Numbers(JToken t, string w)
            {
                if (t is not JArray a) throw Bad($"{w} isn't a list of numbers");
                return a.Select(x => x.Type is JTokenType.Integer or JTokenType.Float ? (float)(double)x : throw Bad($"{w} isn't a list of numbers")).ToArray();
            }

            private static int Index(JObject o, string key, int count, string w)
            {
                long v = Int(o, key, w);
                if (v >= count) throw Bad($"{w}.{key} points to a missing entry ({v})");
                return (int)v;
            }

            private static void OptIndex(JObject o, string key, int count, string w)
            {
                if (o[key] != null && o[key].Type != JTokenType.Null) Index(o, key, count, w);
            }

            private static AssetException Bad(string what) => new("glTF is malformed: " + what);
        }
    }
}
