using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/*
    Decoders for pack assets. Every loader checks the file's signature and its limits before handing bytes to Unity,
    and reports problems by throwing AssetException with a player-facing message (AssetProvider turns that into a
    null result and a logged warning). Nothing here may crash the game on a malformed file: no unchecked sizes, no
    unbounded allocations, no recursion.
*/
namespace PalaceMod.Builder.Assets
{
    internal sealed class AssetException(string message) : Exception(message);

    internal enum AssetKind { Unknown, Png, Wav, Ogg, Obj, Glb, Gltf }

    internal static class AssetLimits
    {
        public const long MaxImageBytes = 8 << 20;
        public const int MaxImageSide = 2048;
        public const long MaxWavBytes = 24 << 20;
        public const long MaxOggBytes = 8 << 20;
        public const float MaxAudioSeconds = 60f;
        public const long MaxMeshBytes = 16 << 20;
        public const int MaxMeshVertices = 100_000;
        public const int MaxMeshTriangles = 150_000;
        public const long MaxDownloadBytes = 24 << 20;
        /// <summary>A glTF model: the .glb, or the .gltf plus every file it references.</summary>
        public const long MaxModelBytes = 24 << 20;

        public static long MaxBytes(AssetKind kind) => kind switch
        {
            AssetKind.Png => MaxImageBytes,
            AssetKind.Wav => MaxWavBytes,
            AssetKind.Ogg => MaxOggBytes,
            AssetKind.Obj => MaxMeshBytes,
            AssetKind.Glb or AssetKind.Gltf => MaxModelBytes,
            _ => MaxDownloadBytes,
        };

        public static string Mb(long bytes) => (bytes / (1024f * 1024f)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    internal static class AssetSniffer
    {
        /// <summary>The file type from its first bytes (extensions lie; downloaded files have none).</summary>
        public static AssetKind Detect(byte[] head, int length)
        {
            if (length >= 8 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G' && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
                return AssetKind.Png;
            if (length >= 12 && Ascii(head, 0, "RIFF") && Ascii(head, 8, "WAVE")) return AssetKind.Wav;
            if (length >= 4 && Ascii(head, 0, "OggS")) return AssetKind.Ogg;
            if (length >= 4 && Ascii(head, 0, "glTF")) return AssetKind.Glb;
            if (LooksLikeGltfJson(head, length)) return AssetKind.Gltf;
            if (LooksLikeObj(head, length)) return AssetKind.Obj;
            return AssetKind.Unknown;
        }

        public static AssetKind DetectFile(string path)
        {
            using var f = File.OpenRead(path);
            // Binary formats are known by their first bytes; OBJ may open with long comment headers, so read enough
            // text to get past them.
            var head = new byte[64 * 1024];
            int n = 0, read;
            while (n < head.Length && (read = f.Read(head, n, head.Length - n)) > 0) n += read;
            return Detect(head, n);
        }

        public static string Name(AssetKind kind) => kind switch
        {
            AssetKind.Png => "PNG image",
            AssetKind.Wav => "WAV sound",
            AssetKind.Ogg => "OGG sound",
            AssetKind.Obj => "OBJ mesh",
            AssetKind.Glb => "GLB model",
            AssetKind.Gltf => "glTF model",
            _ => "unrecognised file",
        };

        private static bool Ascii(byte[] b, int at, string s)
        {
            for (int i = 0; i < s.Length; i++) if (b[at + i] != s[i]) return false;
            return true;
        }

        // A .gltf is a JSON object with glTF's top-level members ("asset" is required and usually first; a file that
        // opens with a long embedded buffer or image shows the "buffers" or "images" key before it). GltfCheck decides.
        private static bool LooksLikeGltfJson(byte[] head, int length)
        {
            int i = length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
            while (i < length && (head[i] == ' ' || head[i] == '\t' || head[i] == '\r' || head[i] == '\n')) i++;
            if (i >= length || head[i] != '{') return false;
            string text = Encoding.UTF8.GetString(head, i, length - i);
            return text.Contains("\"asset\"") || text.Contains("\"accessors\"") || text.Contains("\"buffers\"") || text.Contains("\"images\"") || text.Contains("\"meshes\"");
        }

        // OBJ is plain text: printable ASCII/UTF-8 whose lines start with known keywords or comments.
        private static bool LooksLikeObj(byte[] head, int length)
        {
            if (length == 0) return false;
            for (int i = 0; i < length; i++)
            {
                byte c = head[i];
                if (c == 0) return false;
                if (c < 0x09 || (c > 0x0D && c < 0x20)) return false;
            }
            string text = Encoding.UTF8.GetString(head, 0, length);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string kw = line.Split(' ', '\t')[0];
                return kw is "v" or "vt" or "vn" or "f" or "o" or "g" or "s" or "mtllib" or "usemtl" or "l" or "p";
            }
            // Only comments so far (a long header): plain text, so let the OBJ parser decide.
            return true;
        }
    }

    // ------------------------------------------------------------------ PNG

    internal static class PngLoader
    {
        /// <summary>Reads width and height from the IHDR chunk without decoding.</summary>
        public static (int w, int h) ReadSize(byte[] bytes)
        {
            if (bytes.Length < 24 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
                throw new AssetException("not a valid PNG (no header)");
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return (w, h);
        }

        public static Texture2D Load(byte[] bytes, string name, bool forSprite)
        {
            var (w, h) = ReadSize(bytes);
            if (w <= 0 || h <= 0) throw new AssetException("PNG has no pixels");
            if (w > AssetLimits.MaxImageSide || h > AssetLimits.MaxImageSide)
                throw new AssetException($"PNG is {w}x{h}; the limit is {AssetLimits.MaxImageSide}x{AssetLimits.MaxImageSide}");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, !forSprite) { name = name };
            if (!tex.LoadImage(bytes, false) || tex.width != w || tex.height != h)
            {
                UnityEngine.Object.Destroy(tex);
                throw new AssetException("PNG couldn't be decoded");
            }
            tex.wrapMode = forSprite ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = forSprite ? 0 : 2;
            tex.Apply(!forSprite, true); // upload and free the CPU copy
            return tex;
        }
    }

    // ------------------------------------------------------------------ WAV

    internal static class WavLoader
    {
        public static AudioClip Load(byte[] b, string name)
        {
            if (b.Length < 12) throw new AssetException("WAV is truncated");
            int pos = 12;
            int format = 0, channels = 0, rate = 0, bits = 0;
            int dataOffset = -1, dataLength = 0;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                long size = BitConverter.ToUInt32(b, pos + 4);
                int body = pos + 8;
                if (size > b.Length - body) size = b.Length - body; // tolerate a truncated last chunk
                if (id == "fmt ")
                {
                    if (size < 16) throw new AssetException("WAV format chunk is too short");
                    format = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, body + 24); // WAVE_FORMAT_EXTENSIBLE sub-format
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = (int)size;
                }
                pos = body + (int)size + (int)(size & 1);
            }
            if (channels == 0) throw new AssetException("WAV has no format chunk");
            if (dataOffset < 0) throw new AssetException("WAV has no sound data");
            if (channels < 1 || channels > 8) throw new AssetException($"WAV has {channels} channels (1 to 8 supported)");
            if (rate < 8000 || rate > 192000) throw new AssetException($"WAV sample rate {rate} isn't supported");
            bool pcm = format == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
            bool flt = format == 3 && bits == 32;
            if (!pcm && !flt) throw new AssetException($"WAV encoding (format {format}, {bits}-bit) isn't supported; use 16-bit PCM");

            int bytesPerSample = bits / 8;
            int frames = dataLength / (bytesPerSample * channels);
            if (frames == 0) throw new AssetException("WAV is empty");
            if (frames / (float)rate > AssetLimits.MaxAudioSeconds) throw new AssetException($"WAV is longer than {AssetLimits.MaxAudioSeconds:0} s");

            var samples = new float[frames * channels];
            int p = dataOffset;
            for (int i = 0; i < samples.Length; i++, p += bytesPerSample)
            {
                samples[i] = (flt, bits) switch
                {
                    (true, _) => BitConverter.ToSingle(b, p),
                    (_, 8) => (b[p] - 128) / 128f,
                    (_, 16) => BitConverter.ToInt16(b, p) / 32768f,
                    (_, 24) => ((b[p] << 8 | b[p + 1] << 16 | b[p + 2] << 24) >> 8) / 8388608f,
                    _ => BitConverter.ToInt32(b, p) / 2147483648f,
                };
                if (float.IsNaN(samples[i])) samples[i] = 0;
            }
            var clip = AudioClip.Create(name, frames, channels, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }

    // ------------------------------------------------------------------ OGG

    internal static class OggLoader
    {
        /// <summary>Checks that an Ogg file carries Vorbis audio before Unity's decoder sees it (Unity only plays Vorbis).</summary>
        public static void Check(byte[] head, int length)
        {
            if (length < 64) throw new AssetException("OGG is truncated");
            // First page: 27-byte header + segment table, then the identification packet "\x01vorbis".
            int segments = head[26];
            int packet = 27 + segments;
            if (packet + 7 > length || head[packet] != 1 || Encoding.ASCII.GetString(head, packet + 1, 6) != "vorbis")
                throw new AssetException("OGG isn't Vorbis audio (Opus and FLAC-in-Ogg aren't supported)");
            int channels = head[packet + 11];
            int rate = BitConverter.ToInt32(head, packet + 12);
            if (channels < 1 || channels > 8 || rate < 8000 || rate > 192000) throw new AssetException("OGG has an unsupported channel count or sample rate");
            long samples = CheckPages(head, length);
            // The decoder expands everything into memory, so the length limit must hold before decoding: a long,
            // quiet file is tiny on disk but huge as PCM.
            if (samples / (double)rate > AssetLimits.MaxAudioSeconds) throw new AssetException($"sound is longer than {AssetLimits.MaxAudioSeconds:0} s");
        }

        /// <summary>Walks every Ogg page and verifies its CRC, so a truncated or damaged file is refused here rather
        /// than by Unity's decoder (which logs an FMOD error). Returns the stream's length in samples (the largest
        /// granule position).</summary>
        private static long CheckPages(byte[] b, int length)
        {
            int pos = 0, pages = 0;
            long samples = 0;
            uint serial = length >= 18 ? BitConverter.ToUInt32(b, 14) : 0;
            while (pos < length)
            {
                if (pos + 27 > length || b[pos] != 'O' || b[pos + 1] != 'g' || b[pos + 2] != 'g' || b[pos + 3] != 'S' || b[pos + 4] != 0)
                    throw new AssetException("OGG is damaged or truncated");
                int segments = b[pos + 26];
                if (pos + 27 + segments > length) throw new AssetException("OGG is truncated");
                int body = 0;
                for (int i = 0; i < segments; i++) body += b[pos + 27 + i];
                int size = 27 + segments + body;
                if (pos + size > length) throw new AssetException("OGG is truncated");
                uint stored = BitConverter.ToUInt32(b, pos + 22);
                uint crc = 0;
                for (int i = 0; i < size; i++)
                {
                    byte x = i >= 22 && i < 26 ? (byte)0 : b[pos + i];
                    crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ x];
                }
                if (crc != stored) throw new AssetException("OGG is damaged (checksum mismatch)");
                if (BitConverter.ToUInt32(b, pos + 14) != serial) throw new AssetException("OGG has several streams; export a single sound");
                long granule = BitConverter.ToInt64(b, pos + 6);
                if (granule > samples) samples = granule; // -1 (no packet ends here) is skipped
                pos += size;
                pages++;
            }
            if (pages < 3) throw new AssetException("OGG is truncated");
            return samples;
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                t[i] = r;
            }
            return t;
        }
    }

    // ------------------------------------------------------------------ OBJ

    /// <summary>
    /// Wavefront OBJ: positions, UVs, normals and faces (any polygon, fan-triangulated; negative indices allowed).
    /// Everything ends up in one submesh, since units render with one material. Materials (mtllib) are ignored;
    /// colour comes from the unit's own material, its tint and the optional texture. OBJ is right-handed, Unity
    /// left-handed: X is mirrored and the winding flipped, as Unity's own importer does.
    /// </summary>
    internal static class ObjLoader
    {
        public static Mesh Load(byte[] bytes, string name)
        {
            var positions = new List<Vector3>();
            var colors = new List<Color>();
            var outColor = new List<Color>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var outPos = new List<Vector3>();
            var outUv = new List<Vector2>();
            var outNormal = new List<Vector3>();
            var triangles = new List<int>();
            var vertexMap = new Dictionary<(int, int, int), int>();
            bool anyUv = false, anyNormal = false;
            var face = new List<int>(8);

            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
            string line;
            int lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length < 2) continue;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "v":
                        if (parts.Length < 4) throw Bad(lineNo, "a vertex needs x y z");
                        positions.Add(new Vector3(-F(parts[1], lineNo), F(parts[2], lineNo), F(parts[3], lineNo)));
                        // Common extension (Blender, MeshLab, MagicaVoxel): "v x y z r g b" with 0-1 colours.
                        if (parts.Length >= 7)
                        {
                            while (colors.Count < positions.Count - 1) colors.Add(Color.white);
                            colors.Add(new Color(Mathf.Clamp01(F(parts[4], lineNo)), Mathf.Clamp01(F(parts[5], lineNo)), Mathf.Clamp01(F(parts[6], lineNo))));
                        }
                        break;
                    case "vt":
                        if (parts.Length < 3) throw Bad(lineNo, "a texture coordinate needs u v");
                        uvs.Add(new Vector2(F(parts[1], lineNo), F(parts[2], lineNo)));
                        break;
                    case "vn":
                        if (parts.Length < 4) throw Bad(lineNo, "a normal needs x y z");
                        normals.Add(new Vector3(-F(parts[1], lineNo), F(parts[2], lineNo), F(parts[3], lineNo)));
                        break;
                    case "f":
                        if (parts.Length < 4) throw Bad(lineNo, "a face needs at least 3 vertices");
                        face.Clear();
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var idx = parts[i].Split('/');
                            int vi = Index(idx[0], positions.Count, lineNo);
                            int ti = idx.Length > 1 && idx[1].Length > 0 ? Index(idx[1], uvs.Count, lineNo) : -1;
                            int ni = idx.Length > 2 && idx[2].Length > 0 ? Index(idx[2], normals.Count, lineNo) : -1;
                            var key = (vi, ti, ni);
                            if (!vertexMap.TryGetValue(key, out int v))
                            {
                                if (outPos.Count >= AssetLimits.MaxMeshVertices)
                                    throw new AssetException($"mesh has more than {AssetLimits.MaxMeshVertices} vertices");
                                v = outPos.Count;
                                vertexMap[key] = v;
                                outPos.Add(positions[vi]);
                                outColor.Add(vi < colors.Count ? colors[vi] : Color.white);
                                outUv.Add(ti >= 0 ? uvs[ti] : Vector2.zero);
                                outNormal.Add(ni >= 0 ? normals[ni] : Vector3.zero);
                                anyUv |= ti >= 0;
                                anyNormal |= ni >= 0;
                            }
                            face.Add(v);
                        }
                        Triangulate(face, outPos, triangles);
                        if (triangles.Count / 3 > AssetLimits.MaxMeshTriangles)
                            throw new AssetException($"mesh has more than {AssetLimits.MaxMeshTriangles} triangles");
                        break;
                }
            }
            if (triangles.Count == 0) throw new AssetException("OBJ has no faces");
            return MeshBuilder.Build(name, outPos, anyUv ? outUv : null, anyNormal ? outNormal : null, outColor, triangles);
        }

        /// <summary>
        /// Splits a polygon into triangles, emitted with flipped winding (the handedness change). Triangles and convex
        /// polygons are fanned; concave ones are ear-clipped in their own plane, so a U-shaped face keeps its notch.
        /// A self-intersecting polygon that can't be clipped falls back to a fan for what remains.
        /// </summary>
        private static void Triangulate(List<int> face, List<Vector3> pos, List<int> triangles)
        {
            void Emit(int a, int b, int c) { triangles.Add(a); triangles.Add(c); triangles.Add(b); }
            int n = face.Count;
            if (n == 3) { Emit(face[0], face[1], face[2]); return; }

            // Newell normal, then a 2D basis in the polygon's plane in which the polygon winds counter-clockwise.
            var normal = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = pos[face[i]], b = pos[face[(i + 1) % n]];
                normal += new Vector3((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y));
            }
            if (normal.sqrMagnitude < 1e-20f) { for (int i = 1; i + 1 < n; i++) Emit(face[0], face[i], face[i + 1]); return; }
            normal.Normalize();
            var u = Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            var w = Vector3.Cross(normal, u);
            var p = new Vector2[n];
            for (int i = 0; i < n; i++) p[i] = new Vector2(Vector3.Dot(pos[face[i]], w), Vector3.Dot(pos[face[i]], u));
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            float area = 0;
            for (int i = 0; i < n; i++) area += Cross(Vector2.zero, p[i], p[(i + 1) % n]);
            int sign = area >= 0 ? 1 : -1;

            bool convex = true;
            for (int i = 0; i < n && convex; i++) convex = sign * Cross(p[i], p[(i + 1) % n], p[(i + 2) % n]) >= -1e-9f;
            if (convex) { for (int i = 1; i + 1 < n; i++) Emit(face[0], face[i], face[i + 1]); return; }

            var left = new List<int>(n);
            for (int i = 0; i < n; i++) left.Add(i);
            int guard = n * n;
            while (left.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int k = 0; k < left.Count; k++)
                {
                    int ia = left[(k + left.Count - 1) % left.Count], ib = left[k], ic = left[(k + 1) % left.Count];
                    if (sign * Cross(p[ia], p[ib], p[ic]) <= 1e-12f) continue; // reflex or flat corner
                    bool inside = false;
                    foreach (int j in left)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (sign * Cross(p[ia], p[ib], p[j]) >= 0 && sign * Cross(p[ib], p[ic], p[j]) >= 0 && sign * Cross(p[ic], p[ia], p[j]) >= 0) { inside = true; break; }
                    }
                    if (inside) continue;
                    Emit(face[ia], face[ib], face[ic]);
                    left.RemoveAt(k);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            for (int i = 1; i + 1 < left.Count; i++) Emit(face[left[0]], face[left[i]], face[left[i + 1]]);
        }

        private static AssetException Bad(int line, string message) => new($"OBJ line {line}: {message}");

        private static float F(string s, int line)
        {
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 1e6f)
                throw Bad(line, $"\"{s}\" isn't a number");
            return f;
        }

        private static int Index(string s, int count, int line)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i == 0) throw Bad(line, $"bad index \"{s}\"");
            int resolved = i > 0 ? i - 1 : count + i;
            if (resolved < 0 || resolved >= count) throw Bad(line, $"index {i} points past the {count} entries defined so far");
            return resolved;
        }
    }

    internal static class MeshBuilder
    {
        /// <param name="colors">Vertex colours (white when the file has none). The game's `Enemy` material doesn't use
        /// them today; they're kept so a vertex-colour material could.</param>
        public static Mesh Build(string name, List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals, List<Color> colors, List<int> triangles)
        {
            var mesh = new Mesh { name = name };
            if (positions.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            FixOrientation(positions, normals, triangles);
            mesh.SetVertices(positions);
            mesh.SetColors(colors);
            if (uvs != null) mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, true);
            bool goodNormals = normals != null;
            if (goodNormals) foreach (var n in normals) if (n.sqrMagnitude < 1e-8f) { goodNormals = false; break; }
            if (goodNormals) mesh.SetNormals(normals);
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var size = mesh.bounds.size;
            if (size.x <= 1e-6f && size.y <= 1e-6f && size.z <= 1e-6f) throw new AssetException("mesh has no size (all vertices in one place)");
            return mesh;
        }

        /// <summary>
        /// Exporters disagree on face winding. An inside-out mesh shows its back faces with inward normals, which the
        /// game's cel shader renders black, so: if the mesh is closed and encloses negative volume, flip every
        /// triangle; then if the file's normals mostly oppose the faces, flip them too. Open meshes (planes, cards)
        /// keep their winding: signed volume means nothing for them.
        /// </summary>
        /// <summary>Whether every edge (by vertex position, so UV and normal seams don't count as holes) is shared by
        /// an even number of triangles.</summary>
        private static bool IsClosed(List<Vector3> p, List<int> t)
        {
            var ids = new Dictionary<Vector3, int>();
            int Id(int v) { if (!ids.TryGetValue(p[v], out int id)) ids[p[v]] = id = ids.Count; return id; }
            var edges = new Dictionary<(int, int), int>();
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                int a = Id(t[i]), b = Id(t[i + 1]), c = Id(t[i + 2]);
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    if (x == y) continue;
                    var key = x < y ? (x, y) : (y, x);
                    edges[key] = edges.TryGetValue(key, out int count) ? count + 1 : 1;
                }
            }
            foreach (int count in edges.Values) if ((count & 1) != 0) return false;
            return edges.Count > 0;
        }

        private static void FixOrientation(List<Vector3> p, List<Vector3> normals, List<int> t)
        {
            if (!IsClosed(p, t)) return;
            double volume = 0, agree = 0;
            for (int i = 0; i + 2 < t.Count; i += 3)
                volume += Vector3.Dot(p[t[i]], Vector3.Cross(p[t[i + 1]], p[t[i + 2]]));
            bool flip = volume < 0;
            if (flip)
                for (int i = 0; i + 2 < t.Count; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            if (normals == null) return;
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                // Unity's front faces wind clockwise, so the outward face normal is (b - a) x (c - a).
                var face = Vector3.Cross(p[t[i + 1]] - p[t[i]], p[t[i + 2]] - p[t[i]]);
                agree += Vector3.Dot(face, normals[t[i]] + normals[t[i + 1]] + normals[t[i + 2]]);
            }
            if (agree < 0) for (int i = 0; i < normals.Count; i++) normals[i] = -normals[i];
        }
    }
}
