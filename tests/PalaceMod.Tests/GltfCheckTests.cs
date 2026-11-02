using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Assets;

namespace PalaceMod.Tests
{
    /// <summary>
    /// GltfCheck must accept sound models and turn every malformed one into an AssetException (never another
    /// exception, never a pass that would hand glTFast out-of-range data). Set PALACEMOD_GLTF_CORPUS to a folder with
    /// real/ (must load) and malformed/ + unsupported/ (must be refused) to also run a file corpus.
    /// </summary>
    public static class GltfCheckTests
    {
        // A single triangle: 3 float VEC3 positions (36 bytes) then 3 ushort indices (6 bytes, padded to 8).
        private static byte[] TriangleBin()
        {
            var b = new List<byte>();
            foreach (var f in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) b.AddRange(BitConverter.GetBytes(f));
            foreach (var i in new ushort[] { 0, 1, 2 }) b.AddRange(BitConverter.GetBytes(i));
            b.AddRange(new byte[2]);
            return b.ToArray();
        }

        private static JObject TriangleJson(int binLength) => JObject.Parse($$"""
            {
              "asset": { "version": "2.0" }, "scene": 0, "scenes": [{ "nodes": [0] }],
              "nodes": [{ "mesh": 0 }],
              "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1, "material": 0 }] }],
              "materials": [{ "pbrMetallicRoughness": { "baseColorFactor": [1, 0.5, 0.25, 1] } }],
              "buffers": [{ "byteLength": {{binLength}} }],
              "bufferViews": [{ "buffer": 0, "byteOffset": 0, "byteLength": 36 }, { "buffer": 0, "byteOffset": 36, "byteLength": 6 }],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0,0,0], "max": [1,1,0] },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
              ]
            }
            """);

        private static byte[] Glb(JObject json, byte[] bin)
        {
            var js = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
            var jsPadded = js.Concat(Enumerable.Repeat((byte)' ', (4 - js.Length % 4) % 4)).ToArray();
            var binPadded = bin.Concat(new byte[(4 - bin.Length % 4) % 4]).ToArray();
            var o = new List<byte>();
            o.AddRange(Encoding.ASCII.GetBytes("glTF"));
            o.AddRange(BitConverter.GetBytes(2u));
            o.AddRange(BitConverter.GetBytes((uint)(12 + 8 + jsPadded.Length + 8 + binPadded.Length)));
            o.AddRange(BitConverter.GetBytes((uint)jsPadded.Length));
            o.AddRange(BitConverter.GetBytes(0x4E4F534Au));
            o.AddRange(jsPadded);
            o.AddRange(BitConverter.GetBytes((uint)binPadded.Length));
            o.AddRange(BitConverter.GetBytes(0x004E4942u));
            o.AddRange(binPadded);
            return o.ToArray();
        }

        private static byte[] Triangle(Action<JObject> edit = null, byte[] bin = null)
        {
            bin ??= TriangleBin();
            var json = TriangleJson(bin.Length);
            edit?.Invoke(json);
            return Glb(json, bin);
        }

        private static void Refused(byte[] file, string expect, string path = null, string folder = null)
        {
            try
            {
                GltfCheck.Read(file, path, folder);
            }
            catch (AssetException e)
            {
                if (expect != null && !e.Message.Contains(expect)) throw new AssetException($"refused, but with \"{e.Message}\" (expected \"{expect}\")");
                return;
            }
            throw new AssertException($"accepted a file that should be refused ({expect})");
        }

        public static void AcceptsTriangle()
        {
            var model = GltfCheck.Read(Triangle(), null, null);
            Assert.True(model.Binary, "binary");
            Assert.Equal(3L, model.Vertices, "vertices");
            Assert.Equal(1L, model.Triangles, "triangles");
            Assert.Equal(1, model.Materials.Count, "materials");
            Assert.Equal(0.5f, model.Materials[0].BaseColor[1], "base colour");
        }

        public static void AcceptsNonIndexed()
        {
            var model = GltfCheck.Read(Triangle(j => ((JObject)j["meshes"][0]["primitives"][0]).Remove("indices")), null, null);
            Assert.Equal(1L, model.Triangles, "triangles");
        }

        public static void CountsInstancesOfReusedMeshes()
        {
            var model = GltfCheck.Read(Triangle(j =>
            {
                j["nodes"] = JArray.Parse("""[{ "children": [1, 2, 3] }, { "mesh": 0 }, { "mesh": 0, "translation": [2,0,0] }, { "mesh": 0, "scale": [-1,1,1] }]""");
            }), null, null);
            Assert.Equal(3L, model.Triangles, "triangles");
        }

        public static void RefusesBrokenContainers()
        {
            Refused([], "empty");
            var glb = Triangle();
            Refused(glb.Take(16).ToArray(), "truncated");
            Refused(glb.Take(glb.Length - 20).ToArray(), "truncated");
            var v1 = (byte[])glb.Clone();
            v1[4] = 1;
            Refused(v1, "version 1");
            Refused(Encoding.UTF8.GetBytes("{ not json"), "invalid");
            Refused(Encoding.UTF8.GetBytes("[1, 2, 3]"), "isn't an object");
            Refused(Encoding.UTF8.GetBytes(new string('[', 10000) + new string(']', 10000)), "invalid");
            Refused(Encoding.UTF8.GetBytes("""{ "asset": { "version": "1.0" } }"""), "version");
            Refused(Triangle(j => j.Remove("asset")), "asset");
            var junk = new byte[400_000];
            new Random(1).NextBytes(junk);
            Refused(junk, null);
        }

        public static void RefusesOutOfRangeData()
        {
            Refused(Triangle(j => j["accessors"][0]["count"] = 4), "runs past");
            Refused(Triangle(j => j["accessors"][0]["count"] = int.MaxValue), "too many");
            Refused(Triangle(j => j["accessors"][0]["byteOffset"] = 4), "runs past");
            Refused(Triangle(j => j["bufferViews"][1]["byteLength"] = 100), "runs past");
            Refused(Triangle(j => j["buffers"][0]["byteLength"] = 1000), "declares");
            Refused(Triangle(j => j["accessors"][0]["componentType"] = 5124), "componentType");
            Refused(Triangle(j => j["accessors"][0]["componentType"] = 9999), "componentType");
            Refused(Triangle(j => j["accessors"][0]["type"] = "VEC2"), "wrong type");
            Refused(Triangle(j => j["accessors"][1]["componentType"] = 5122), "unsigned integer scalars");
            Refused(Triangle(j => j["bufferViews"][0]["byteStride"] = 3), "byteStride");
            var bin = TriangleBin();
            BitConverter.GetBytes((ushort)7).CopyTo(bin, 38);
            Refused(Triangle(bin: bin), "index past");
        }

        public static void RefusesBadAttributesAndSparse()
        {
            Refused(Triangle(j =>
            {
                j["accessors"] = JArray.Parse(
                    "[{ 'bufferView': 0, 'componentType': 5126, 'count': 3, 'type': 'VEC3' }," +
                    " { 'bufferView': 1, 'componentType': 5123, 'count': 3, 'type': 'SCALAR' }," +
                    " { 'bufferView': 0, 'componentType': 5126, 'count': 2, 'type': 'VEC3' }]");
                j["meshes"][0]["primitives"][0]["attributes"]["NORMAL"] = 2;
            }), "elements but the other");
            // Sparse indices are the ushort index data (0, 1, 2) against an accessor of count 2.
            Refused(Triangle(j =>
            {
                ((JArray)j["accessors"]).Add(JObject.Parse(
                    "{ 'componentType': 5126, 'count': 2, 'type': 'VEC3', 'sparse': { 'count': 2," +
                    " 'indices': { 'bufferView': 1, 'byteOffset': 2, 'componentType': 5123 }, 'values': { 'bufferView': 0 } } }"));
            }), "sparse");
            Refused(Triangle(j => ((JObject)j["meshes"][0]["primitives"][0]["attributes"]).Remove("POSITION")), "POSITION");
            Refused(Triangle(j => j["meshes"][0]["primitives"] = new JArray()), "no primitives");
        }

        public static void RefusesBadNodes()
        {
            Refused(Triangle(j => j["nodes"] = JArray.Parse("""[{ "children": [1] }, { "children": [0], "mesh": 0 }]""")), "loop");
            Refused(Triangle(j =>
            {
                j["nodes"] = JArray.Parse("""[{ "children": [1] }, { "children": [2] }, { "children": [1], "mesh": 0 }]""");
            }), null);
            Refused(Triangle(j => j["nodes"][0]["mesh"] = 5), "missing");
            Refused(Triangle(j => j["scenes"][0]["nodes"] = JArray.Parse("[3]")), "missing node");
            Refused(Triangle(j => j["scene"] = 2), "missing scene");
            Refused(Triangle(j => j["nodes"][0]["translation"] = JArray.Parse("[1, 2]")), "3 numbers");
            Refused(Triangle(j => j.Remove("scenes")), "no scene");
            var deep = new JArray();
            for (int i = 0; i < 300; i++) deep.Add(JObject.Parse(i < 299 ? $$"""{ "children": [{{i + 1}}] }""" : """{ "mesh": 0 }"""));
            Refused(Triangle(j => j["nodes"] = deep), "deeper");
        }

        public static void RefusesUnsupportedAndExternal()
        {
            Refused(Triangle(j => j["extensionsRequired"] = JArray.Parse("""["KHR_draco_mesh_compression"]""")), "Draco");
            Refused(Triangle(j => j["extensionsRequired"] = JArray.Parse("""["KHR_texture_basisu"]""")), "KTX2");
            Refused(Triangle(j => j["buffers"][0]["uri"] = "https://example.com/a.bin"), "only files next to");
            Refused(Triangle(j => j["buffers"][0]["uri"] = "/etc/passwd"), "only files next to");
            Refused(Triangle(j => j["buffers"][0]["uri"] = "a.bin"), "no folder");
            Refused(Triangle(j => j["buffers"][0]["uri"] = "data:application/octet-stream,abc"), "base64");
        }

        public static void ResolvesAdjacentFilesInsideThePackOnly()
        {
            using var dir = new TempDir();
            string pack = Path.Combine(dir.Path, "pack"), models = Path.Combine(pack, "assets", "models");
            Directory.CreateDirectory(models);
            var bin = TriangleBin();
            File.WriteAllBytes(Path.Combine(models, "tri angle.bin"), bin);
            File.WriteAllBytes(Path.Combine(dir.Path, "outside.bin"), bin);
            var json = TriangleJson(bin.Length);
            json["buffers"][0]["uri"] = "tri%20angle.bin";
            string gltf = Path.Combine(models, "tri.gltf");
            var model = GltfCheck.Read(Encoding.UTF8.GetBytes(json.ToString()), gltf, pack);
            Assert.True(!model.Binary, "json");
            Assert.Equal(1, model.Files.Count, "files");
            Assert.True(model.Files.ContainsKey(Path.GetFullPath(Path.Combine(models, "tri angle.bin"))), "file path");

            json["buffers"][0]["uri"] = "../../../outside.bin";
            Refused(Encoding.UTF8.GetBytes(json.ToString()), "outside the pack", gltf, pack);
            json["buffers"][0]["uri"] = "missing.bin";
            Refused(Encoding.UTF8.GetBytes(json.ToString()), "isn't in the pack", gltf, pack);

            // Embedded as a data URI works without a folder.
            json["buffers"][0]["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bin);
            Assert.Equal(1L, GltfCheck.Read(Encoding.UTF8.GetBytes(json.ToString()), null, null).Triangles, "embedded");
        }

        public static void ChecksImages()
        {
            var png = new byte[33];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
            void Size(int w, int h) { BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(png, 16); BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(png, 20); }
            JObject WithImage(byte[] image) => JObject.FromObject(new { uri = "data:image/png;base64," + Convert.ToBase64String(image) });
            Size(64, 32);
            var model = GltfCheck.Read(Triangle(j =>
            {
                j["images"] = new JArray(WithImage(png));
                j["textures"] = JArray.Parse("""[{ "source": 0 }]""");
                j["materials"][0]["pbrMetallicRoughness"]["baseColorTexture"] = JObject.Parse("""{ "index": 0 }""");
            }), null, null);
            Assert.Equal(0, model.Materials[0].Texture, "texture");
            Size(20000, 20000);
            Refused(Triangle(j => j["images"] = new JArray(WithImage(png))), "limit");
            Refused(Triangle(j => j["images"] = new JArray(WithImage(Encoding.ASCII.GetBytes("GIF89a.....")))), "isn't a PNG or JPEG");
            Refused(Triangle(j => j["materials"][0]["pbrMetallicRoughness"]["baseColorTexture"] = JObject.Parse("""{ "index": 3 }""")), "missing");
        }

        public static void JpegSize()
        {
            // SOI, APP0 (length 4), SOF0 with height 300, width 200.
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x01, 0x2C, 0x00, 0xC8, 3, 0, 0, 0];
            var (w, h) = GltfCheck.ImageSize(jpeg, 0, jpeg.Length, "image");
            Assert.Equal(200, w, "width");
            Assert.Equal(300, h, "height");
        }

        /// <summary>Optional file corpus (see the class comment).</summary>
        public static void Corpus()
        {
            string root = Environment.GetEnvironmentVariable("PALACEMOD_GLTF_CORPUS");
            if (string.IsNullOrEmpty(root)) return;
            var failures = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
            {
                string rel = Path.GetRelativePath(root, file);
                string top = rel.Split(Path.DirectorySeparatorChar)[0];
                string fileName = Path.GetFileName(file);
                bool expectOk = top == "real" || fileName.StartsWith("control_") || rel.Contains("_base");
                string outcome;
                try
                {
                    var m = GltfCheck.Read(File.ReadAllBytes(file), file, Path.GetDirectoryName(file));
                    outcome = $"ok ({m.Vertices:N0} vertices, {m.Triangles:N0} triangles, {m.Materials.Count} materials, {m.ImageCount} images)";
                    if (!expectOk) failures.Add($"{rel}: accepted");
                }
                catch (AssetException e)
                {
                    outcome = "refused: " + e.Message;
                    if (expectOk && !e.Message.Contains("vertices") && !e.Message.Contains("triangles")) failures.Add($"{rel}: {e.Message}");
                }
                catch (Exception e)
                {
                    outcome = "CRASH: " + e;
                    failures.Add($"{rel}: {e.GetType().Name}: {e.Message}");
                }
                Console.WriteLine($"        {rel}: {outcome}");
            }
            if (failures.Count > 0) throw new AssertException(string.Join("\n", failures));
        }
    }
}
