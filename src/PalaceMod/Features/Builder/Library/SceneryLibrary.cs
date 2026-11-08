using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/*
    The cross-map scenery library: every scenery mesh of the main maps, placeable on any map.

    The meshes are read from the player's own install (Application.dataPath, AssetFileReader) the first time the library
    is used, copied into new readable Meshes and kept for the game session; nothing is shipped with the mod or written
    to the game's files. Each mesh is drawn with the loaded level's own material of the same name (the five FlatKit
    materials that ColorAndLightManager recolours per level), so library props take the host map's colours. An entry
    that can't be read (another game version, a moved file, a material the level doesn't have) is hidden from the
    browser and logged once; a level that uses it gets the usual "no scenery" warning.

    Resolution of a prop base "mesh:<name>[@Map]": the host scene's own scenery first (so levels made before the
    library look the same), then the library.
*/
namespace PalaceMod.Builder.Library
{
    internal static class SceneryLibrary
    {
        private static SceneryCatalog catalog;
        private static readonly Dictionary<string, Mesh> meshes = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> failed = new(StringComparer.Ordinal);
        private static bool loaded;

        private static PalaceMod.Log Log => PalaceMod.Log.For<BuilderManager>();

        public static SceneryCatalog Catalog
        {
            get
            {
                if (catalog != null) return catalog;
                try { catalog = SceneryCatalog.FromResource(); }
                catch (Exception e)
                {
                    Log.Error("Couldn't read the scenery library catalog", e);
                    catalog = new SceneryCatalog();
                }
                return catalog;
            }
        }

        /// <summary>Entries whose mesh loaded (loads them all on first use).</summary>
        public static IEnumerable<SceneryEntry> Available
        {
            get
            {
                EnsureLoaded();
                return Catalog.Meshes.Where(m => meshes.ContainsKey(m.Name));
            }
        }

        public static IReadOnlyDictionary<string, string> Failed { get { EnsureLoaded(); return failed; } }

        public static Mesh MeshOf(SceneryEntry entry)
        {
            EnsureLoaded();
            return entry != null && meshes.TryGetValue(entry.Name, out var m) ? m : null;
        }

        /// <summary>Reads every catalog mesh from the install. About 400 KB in total; done once per game session.</summary>
        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            var watch = Stopwatch.StartNew();
            var cat = Catalog;
            foreach (var group in cat.Meshes.GroupBy(m => m.File))
            {
                string path = Path.Combine(Application.dataPath, group.Key);
                try
                {
                    using var file = new AssetFileReader(path);
                    if (file.UnityVersion != cat.Unity) throw new InvalidDataException($"{group.Key} is from Unity {file.UnityVersion}; the catalog is for {cat.Unity}");
                    foreach (var entry in group)
                    {
                        try
                        {
                            var raw = file.ReadMesh(entry.PathId);
                            if (raw.Name != entry.Name) throw new InvalidDataException($"object {entry.PathId} is \"{raw.Name}\"");
                            if (raw.VertexCount != entry.Vertices) throw new InvalidDataException($"{raw.VertexCount} vertices, the catalog says {entry.Vertices}");
                            meshes[entry.Name] = ToMesh(raw);
                        }
                        catch (Exception e) { Fail(entry, e.Message); }
                    }
                }
                catch (Exception e)
                {
                    foreach (var entry in group) Fail(entry, e.Message);
                }
            }
            Log.Info($"Scenery library: {meshes.Count} of {cat.Meshes.Count} meshes read from the game in {watch.ElapsedMilliseconds} ms");
        }

        private static void Fail(SceneryEntry entry, string why)
        {
            failed[entry.Name] = why;
            Log.Warn($"Scenery library: \"{entry.Name}\" ({entry.File}:{entry.PathId}) can't be used: {why}");
        }

        /// <summary>A readable Mesh with the raw buffers copied in as they are.</summary>
        internal static Mesh ToMesh(RawMesh raw)
        {
            var mesh = new Mesh { name = raw.Name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            try
            {
                var used = raw.Channels.Select((c, i) => (c, i)).Where(x => x.c.Dimension > 0).ToList();
                var attrs = used.Select(x => new VertexAttributeDescriptor((VertexAttribute)x.i, (VertexAttributeFormat)x.c.Format, x.c.Dimension, x.c.Stream)).ToArray();
                mesh.SetVertexBufferParams(raw.VertexCount, attrs);
                var streams = raw.Streams();
                foreach (var (c, i) in used)
                {
                    int offset = mesh.GetVertexAttributeOffset((VertexAttribute)i);
                    if (offset != c.Offset) throw new InvalidDataException($"attribute {(VertexAttribute)i} is at {offset} here, {c.Offset} in the file");
                }
                for (int s = 0; s < streams.Length; s++)
                {
                    if (streams[s].stride == 0) continue;
                    if (mesh.GetVertexBufferStride(s) != streams[s].stride) throw new InvalidDataException($"stream {s} stride {mesh.GetVertexBufferStride(s)}, {streams[s].stride} in the file");
                    mesh.SetVertexBufferData(raw.VertexData, streams[s].offset, 0, streams[s].stride * raw.VertexCount, s, MeshUpdateFlags.DontRecalculateBounds);
                }

                int indexSize = raw.Index32 ? 4 : 2;
                int indexCount = raw.IndexBuffer.Length / indexSize;
                mesh.SetIndexBufferParams(indexCount, raw.Index32 ? IndexFormat.UInt32 : IndexFormat.UInt16);
                if (raw.Index32)
                {
                    var idx = new int[indexCount];
                    Buffer.BlockCopy(raw.IndexBuffer, 0, idx, 0, indexCount * 4);
                    mesh.SetIndexBufferData(idx, 0, 0, indexCount, MeshUpdateFlags.DontRecalculateBounds);
                }
                else
                {
                    var idx = new ushort[indexCount];
                    Buffer.BlockCopy(raw.IndexBuffer, 0, idx, 0, indexCount * 2);
                    mesh.SetIndexBufferData(idx, 0, 0, indexCount, MeshUpdateFlags.DontRecalculateBounds);
                }

                mesh.subMeshCount = raw.SubMeshes.Count;
                for (int i = 0; i < raw.SubMeshes.Count; i++)
                {
                    var sm = raw.SubMeshes[i];
                    mesh.SetSubMesh(i, new SubMeshDescriptor((int)(sm.FirstByte / indexSize), (int)sm.IndexCount, (MeshTopology)sm.Topology)
                    {
                        baseVertex = (int)sm.BaseVertex,
                        firstVertex = (int)sm.FirstVertex,
                        vertexCount = (int)sm.VertexCount,
                        bounds = new Bounds(new Vector3(sm.Center[0], sm.Center[1], sm.Center[2]), 2f * new Vector3(sm.Extent[0], sm.Extent[1], sm.Extent[2])),
                    }, MeshUpdateFlags.DontRecalculateBounds);
                }
                mesh.RecalculateBounds();
                return mesh;
            }
            catch
            {
                UnityEngine.Object.Destroy(mesh);
                throw;
            }
        }

        // ---- Materials ---------------------------------------------------------------------------------------------

        private static int materialScene = -1;
        private static readonly Dictionary<string, Material> materials = new(StringComparer.Ordinal);

        /// <summary>The loaded level's material by name: ColorAndLightManager's (the ones it recolours) first, then any
        /// loaded material of that name. Null if the level has none.</summary>
        public static Material LiveMaterial(string name)
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (scene != materialScene)
            {
                materials.Clear();
                materialScene = scene;
                var light = UnityEngine.Object.FindObjectOfType<ColorAndLightManager>();
                if (light != null)
                    foreach (var f in typeof(ColorAndLightManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        if (f.FieldType == typeof(Material) && f.GetValue(light) is Material m && m != null && !materials.ContainsKey(m.name))
                            materials[m.name] = m;
            }
            if (name == null) return null;
            if (materials.TryGetValue(name, out var found) && found != null) return found;
            found = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m != null && m.name == name);
            if (found != null) materials[name] = found;
            return found;
        }

        /// <summary>A placeable base for a library entry in the loaded level, or null (why says what's missing).</summary>
        public static PropBase BaseOf(SceneryEntry entry, out string why)
        {
            why = null;
            var mesh = MeshOf(entry);
            if (mesh == null) { why = failed.TryGetValue(entry.Name, out var f) ? f : "not loaded"; return null; }
            var mats = new Material[Math.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++)
            {
                string name = i < entry.Materials.Count ? entry.Materials[i] : entry.Materials.LastOrDefault();
                // A material this level doesn't load falls back to the rock material, which every level recolours.
                mats[i] = LiveMaterial(name) ?? LiveMaterial("Mountain");
                if (mats[i] == null) { why = $"no material \"{name}\" in this level"; return null; }
            }
            var u = entry.Upright;
            var s = entry.Scale;
            return new PropBase
            {
                Id = entry.Ref,
                Name = entry.Name.Replace('_', ' ').Trim(),
                Mesh = mesh,
                Materials = mats,
                Upright = u != null && u.Length == 4 ? new Quaternion(u[0], u[1], u[2], u[3]).normalized : Quaternion.identity,
                Scale = s != null && s.Length == 3 ? new Vector3(s[0], s[1], s[2]) : Vector3.one,
                Count = 0,
            };
        }

        // ---- Resolution --------------------------------------------------------------------------------------------

        private static int hostScene = -1, hostFrame = -1;
        private static Dictionary<string, PropBase> hostBases = [];

        /// <summary>A prop base for "mesh:&lt;name&gt;[@Map]" in a loaded scene: the host's own scenery first, then the
        /// library. Null if neither has it.</summary>
        public static PropBase Resolve(Scene scene, string baseRef) => Resolve(scene, baseRef, out _);

        public static PropBase Resolve(Scene scene, string baseRef, out string why)
        {
            why = null;
            if (!SceneryCatalog.TryParseRef(baseRef, out var name, out var map)) { why = "not a \"mesh:<name>\" ref"; return null; }
            if (map == null || map == scene.name || map == SceneryCatalog.DisplayMap(scene.name))
            {
                var host = HostBases(scene);
                if (host.TryGetValue(SceneryCatalog.Prefix + name, out var b)) return b;
            }
            var entry = Catalog.Find(baseRef);
            if (entry == null) { why = "not on this map or in the library"; return null; }
            return BaseOf(entry, out why);
        }

        /// <summary>The host's scenery by id, cached for the frame (LayoutScene.Props walks every mesh in the scene).</summary>
        private static Dictionary<string, PropBase> HostBases(Scene scene)
        {
            if (!scene.IsValid()) return [];
            if (scene.handle != hostScene || Time.frameCount != hostFrame)
            {
                hostScene = scene.handle;
                hostFrame = Time.frameCount;
                hostBases = LayoutScene.Props(scene).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            }
            return hostBases;
        }
    }
}
