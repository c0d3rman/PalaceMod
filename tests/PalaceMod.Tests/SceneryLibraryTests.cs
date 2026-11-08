using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PalaceMod.Builder.Library;

namespace PalaceMod.Tests
{
    /// <summary>
    /// The scenery library's file reader and catalog. A synthetic format-22 file covers the layout; if the game is
    /// installed (PALACEMOD_GAME_DATA, else the default Steam path on a Mac) every catalog mesh is also read from it.
    /// </summary>
    public static class SceneryLibraryTests
    {
        public static void RefsParse()
        {
            Assert.True(SceneryCatalog.TryParseRef("mesh:Rock A", out var n, out var m) && n == "Rock A" && m == null, "plain ref");
            Assert.True(SceneryCatalog.TryParseRef("mesh:Rock A@Moorweg", out n, out m) && n == "Rock A" && m == "Moorweg", "ref with a map");
            Assert.True(SceneryCatalog.TryParseRef("mesh:Defense Tower lvl 2 ", out n, out _) && n == "Defense Tower lvl 2 ", "trailing space kept");
            Assert.True(!SceneryCatalog.TryParseRef("mesh:", out _, out _), "empty name");
            Assert.True(!SceneryCatalog.TryParseRef("mesh:@Moorweg", out _, out _), "only a map");
            Assert.True(!SceneryCatalog.TryParseRef("asset:x", out _, out _), "other kind");
            Assert.Equal("Neuland", SceneryCatalog.DisplayMap("Neuland(Tutorial)"));
        }

        public static void EmbeddedCatalogIsSane()
        {
            var c = Catalog();
            Assert.True(c.Unity == "2022.3.62f2", "unity version");
            Assert.True(c.Meshes.Count >= 30, $"only {c.Meshes.Count} meshes");
            Assert.True(c.Meshes.Select(x => x.Name).Distinct().Count() == c.Meshes.Count, "names are unique");
            foreach (var e in c.Meshes)
            {
                Assert.True(c.Maps.Contains(e.Home) && e.Maps.ContainsKey(e.Home), $"{e.Name}: home");
                Assert.True(e.Upright?.Length == 4 && e.Scale?.Length == 3 && e.Materials.Count > 0 && e.Vertices > 0, $"{e.Name}: fields");
                Assert.True(c.Find(e.Ref) == e && c.Find(e.Ref + "@" + SceneryCatalog.DisplayMap(e.Home)) == e, $"{e.Name}: find");
            }
            Assert.True(c.Find("mesh:Rock A@Nowhere") == null, "a map that doesn't use it");
        }

        public static void ReadsSyntheticMesh()
        {
            var bytes = SyntheticFile(out long pathId);
            using var r = new AssetFileReader(new MemoryStream(bytes));
            Assert.Equal("2022.3.62f2", r.UnityVersion);
            Assert.Equal(22, r.Format);
            var m = r.ReadMesh(pathId);
            Assert.Equal("Tri", m.Name);
            Assert.Equal(3, m.VertexCount);
            Assert.Equal(1, m.SubMeshes.Count);
            Assert.Equal(3u, m.SubMeshes[0].IndexCount);
            Assert.True(!m.Index32 && m.IndexBuffer.Length == 6, "16-bit indices");
            var s = m.Streams();
            Assert.Equal(1, s.Length);
            Assert.Equal(12, s[0].stride);
            Assert.Equal(36, m.VertexData.Length);
            Assert.True(Throws(() => r.ReadMesh(99)), "unknown object");
        }

        public static void RefusesOtherFormats()
        {
            var bytes = SyntheticFile(out _);
            bytes[8 + 3] = 21;   // format 21 (big-endian u32 at offset 8)
            Assert.True(Throws(() => new AssetFileReader(new MemoryStream(bytes))), "format 21 refused");
        }

        public static void ReadsInstalledGame()
        {
            string data = Environment.GetEnvironmentVariable("PALACEMOD_GAME_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library/Application Support/Steam/steamapps/common/Thronefall/Thronefall.app/Contents/Resources/Data");
            if (!Directory.Exists(data)) { Console.WriteLine("        (game not installed; skipped)"); return; }
            var c = Catalog();
            int read = 0;
            foreach (var g in c.Meshes.GroupBy(e => e.File))
            {
                using var r = new AssetFileReader(Path.Combine(data, g.Key));
                Assert.Equal(c.Unity, r.UnityVersion);
                foreach (var e in g)
                {
                    var m = r.ReadMesh(e.PathId);
                    Assert.Equal(e.Name, m.Name);
                    Assert.Equal(e.Vertices, m.VertexCount);
                    Assert.True(m.SubMeshes.Count > 0 && m.IndexBuffer.Length > 0, $"{e.Name}: geometry");
                    Assert.True(m.Channels.Count > 0 && m.Channels[0].Dimension == 3 && m.Channels[0].Format == 0, $"{e.Name}: float3 positions");
                    read++;
                }
            }
            Console.WriteLine($"        read {read} meshes from {data}");
        }

        private static SceneryCatalog Catalog()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src/PalaceMod/Assets/scenery.json"))) dir = Path.GetDirectoryName(dir);
            Assert.True(dir != null, "scenery.json not found above the test binary");
            return SceneryCatalog.Parse(File.ReadAllText(Path.Combine(dir, "src/PalaceMod/Assets/scenery.json")));
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (InvalidDataException) { return true; }
        }

        /// <summary>A minimal serialized file (format 22, little-endian metadata) holding one triangle Mesh.</summary>
        private static byte[] SyntheticFile(out long pathId)
        {
            pathId = 7;
            var obj = new Writer();
            obj.Str("Tri");
            obj.I32(1);   // one submesh
            obj.U32(0); obj.U32(3); obj.I32(0); obj.U32(0); obj.U32(0); obj.U32(3);
            for (int i = 0; i < 6; i++) obj.F32(0.5f);
            obj.I32(0); obj.I32(0); obj.I32(0); obj.I32(0);   // blend shapes: vertices, shapes, channels, fullWeights
            obj.I32(0); obj.I32(0); obj.U32(0); obj.I32(0); obj.I32(0);   // bind poses, bone hashes, root, AABBs, weights
            obj.B(0); obj.B(1); obj.B(1); obj.B(1); obj.Align();
            obj.I32(0);   // 16-bit
            obj.Bytes([0, 0, 1, 0, 2, 0]); obj.Align();
            obj.U32(3);
            obj.I32(1); obj.B(0); obj.B(0); obj.B(0); obj.B(3);   // position: stream 0, offset 0, float32, 3
            var verts = new List<byte>();
            foreach (var f in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) verts.AddRange(BitConverter.GetBytes(f));
            obj.Bytes(verts.ToArray()); obj.Align();
            var objBytes = obj.ToArray();

            var meta = new Writer();
            meta.CStr("2022.3.62f2");
            meta.I32(2);          // platform
            meta.B(0);            // no type trees
            meta.I32(1);          // one type
            meta.I32(43); meta.B(0); meta.I16(-1); meta.Raw(new byte[16]);
            meta.I32(1);          // one object
            // Object entries align to 4 relative to the file start; the header is 48 bytes, so align meta itself.
            meta.Align();
            meta.I64(pathId); meta.I64(0); meta.U32((uint)objBytes.Length); meta.I32(0);
            var metaBytes = meta.ToArray();

            long dataOffset = (48 + metaBytes.Length + 15) / 16 * 16;
            var file = new Writer();
            file.BE(0); file.BE(0); file.BE(22); file.BE(0);
            file.B(0); file.Raw(new byte[3]);
            file.BE((uint)metaBytes.Length);
            file.BE64(dataOffset + objBytes.Length); file.BE64(dataOffset); file.BE64(0);
            file.Raw(metaBytes);
            while (file.Length < dataOffset) file.B(0);
            file.Raw(objBytes);
            return file.ToArray();
        }

        private sealed class Writer
        {
            private readonly MemoryStream s = new();
            public long Length => s.Length;
            public void Raw(byte[] b) => s.Write(b, 0, b.Length);
            public void B(byte b) => s.WriteByte(b);
            public void I16(short v) => Raw(BitConverter.GetBytes(v));
            public void I32(int v) => Raw(BitConverter.GetBytes(v));
            public void U32(uint v) => Raw(BitConverter.GetBytes(v));
            public void I64(long v) => Raw(BitConverter.GetBytes(v));
            public void F32(float v) => Raw(BitConverter.GetBytes(v));
            public void BE(uint v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); Raw(b); }
            public void BE64(long v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); Raw(b); }
            public void Align() { while (s.Length % 4 != 0) s.WriteByte(0); }
            public void Bytes(byte[] b) { I32(b.Length); Raw(b); }
            public void Str(string v) { Bytes(Encoding.UTF8.GetBytes(v)); Align(); }
            public void CStr(string v) { Raw(Encoding.UTF8.GetBytes(v)); B(0); }
            public byte[] ToArray() => s.ToArray();
        }
    }
}
