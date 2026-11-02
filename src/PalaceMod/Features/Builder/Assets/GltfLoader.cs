using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Loading;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    glTF/GLB import through glTFast (PalaceMod.glTFast.dll), for files GltfCheck has validated.

    glTFast decodes the buffers, meshes and images; PalaceMod supplies everything around it so nothing of glTFast's
    reaches the game: a download provider that serves only the model's validated files, placeholder materials (its
    own shaders aren't in the build), a logger into PalaceMod's log, and an instantiator that records nodes and
    primitives instead of creating GameObjects. The result is merged into one static mesh with one texture, because a
    unit body is one GPU-instanced renderer with one material (GPUInstancingManager draws submesh 0 only):

    - Every primitive is baked into model space: node transforms, mirrored nodes (winding flipped), GPU instancing,
      and skinned meshes in their rest pose (joint matrices x bind poses).
    - Colour goes into the texture. One textured material: that texture (times its base colour). Otherwise an
      atlas: textures as tiles (UVs remapped; tiling UVs are unwelded per triangle and wrapped into the tile) and
      flat colours as small swatches (base colours, or per-triangle averages of vertex colours).
    - The model's own first UV set is kept in UV2, so a unit's explicit "texture" can still be painted with it.

    The import is asynchronous (glTFast awaits jobs and yields when over a frame budget); Start calls back exactly
    once on the main thread. Everything glTFast created is destroyed (GltfImport.Dispose) once the merge is done.
*/
namespace PalaceMod.Builder.Assets
{
    internal sealed class GltfResult
    {
        public Mesh Mesh;
        public Texture2D Texture;
        public string Error;
        public string Summary;
        public readonly List<string> Warnings = [];
    }

    internal static class GltfLoader
    {
        /// <summary>Starts an import; <paramref name="done"/> runs once, on the main thread. Never throws if the
        /// glTFast DLL is present; callers catch the load failure when it isn't (see AssetProvider).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Start(GltfModel model, string name, Action<GltfResult> done) => Run(model, name, done);

        /// <summary>Touches a glTFast type, so a missing PalaceMod.glTFast.dll shows up as an exception here.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Probe() => _ = typeof(ImportSettings).Name;

        private static GameObject agentHost;

        private static IDeferAgent Agent()
        {
            if (agentHost == null)
            {
                agentHost = new GameObject("PalaceBuilder glTF") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(agentHost);
                agentHost.AddComponent<TimeBudgetPerFrameDeferAgent>().SetFrameBudget(0.3f);
            }
            return agentHost.GetComponent<TimeBudgetPerFrameDeferAgent>();
        }

        private static async void Run(GltfModel model, string name, Action<GltfResult> done)
        {
            var result = new GltfResult();
            var log = new GltfLog(result.Warnings);
            GLTFast.Newtonsoft.GltfImport import = null;
            var watch = Stopwatch.StartNew();
            try
            {
                import = new GLTFast.Newtonsoft.GltfImport(new ModelFiles(model), Agent(), new PlaceholderMaterials(), log);
                var settings = new ImportSettings
                {
                    AnimationMethod = AnimationMethod.None,
                    GenerateMipMaps = false,
                    TexturesReadable = true,
                    NodeNameMethod = NameImportMethod.Original,
                };
                var uri = model.Path != null ? new Uri(Path.GetFullPath(model.Path)) : null;
                bool ok = model.Binary
                    ? await import.LoadGltfBinary(model.Glb, uri, settings)
                    : await import.LoadGltfJson(model.Json, uri, settings);
                if (!ok) throw new AssetException(log.FirstError ?? "glTF couldn't be loaded");
                var scene = new SceneRecorder();
                if (!await import.InstantiateSceneAsync(scene, model.Scene)) throw new AssetException(log.FirstError ?? "glTF scene couldn't be built");
                GltfMerge.Build(model, new GltfTextures(import.GetTexture), scene, name, result);
                result.Summary += $", {watch.ElapsedMilliseconds} ms";
            }
            catch (AssetException e)
            {
                result.Error = e.Message;
            }
            catch (Exception e)
            {
                result.Error = "glTF couldn't be read: " + e.Message;
                Log.For<BuilderManager>().Warn($"Assets: {name}: glTF import failed", e);
            }
            finally
            {
                try { import?.Dispose(); }
                catch (Exception e) { Log.For<BuilderManager>().Warn($"Assets: {name}: glTF cleanup failed", e); }
            }
            if (result.Error != null)
            {
                if (result.Mesh != null) Object.Destroy(result.Mesh);
                if (result.Texture != null) Object.Destroy(result.Texture);
                result.Mesh = null;
                result.Texture = null;
            }
            try { done(result); }
            catch (Exception e) { Log.For<BuilderManager>().Caught($"Assets: {name}: glTF callback failed", e); }
        }

        /// <summary>glTFast's messages go to PalaceMod's log as warnings (never Unity errors); the first error
        /// becomes the player-facing reason when the import fails.</summary>
        private sealed class GltfLog(List<string> sink) : ICodeLogger
        {
            public string FirstError;

            public void Error(LogCode code, params string[] messages) => Add(LogType.Error, code, messages);
            public void Warning(LogCode code, params string[] messages) => Add(LogType.Warning, code, messages);
            public void Info(LogCode code, params string[] messages) { }
            public void Log(LogType logType, LogCode code, params string[] messages) => Add(logType, code, messages);
            public void Error(string message) => Add(LogType.Error, message);
            public void Warning(string message) => Add(LogType.Warning, message);
            public void Info(string message) { }

            private void Add(LogType type, LogCode code, string[] messages)
            {
                // Performance hints about how glTFast was built or how the file stores buffers: not the player's concern.
                if (type is LogType.Log || code is LogCode.UnityWebRequestTextureNotEnabled or LogCode.EmbedSlow) return;
                string text;
                try { text = LogMessages.GetFullMessage(code, messages); }
                catch { text = code + (messages == null ? "" : ": " + string.Join(", ", messages)); }
                Add(type, text);
            }

            private void Add(LogType type, string text)
            {
                if (type is LogType.Log) return;
                if (type is LogType.Error or LogType.Exception or LogType.Assert) FirstError ??= "glTF: " + text;
                if (sink.Count < 20) sink.Add(text);
            }
        }

        /// <summary>Serves the external files GltfCheck validated (by full path), and nothing else.</summary>
        private sealed class ModelFiles(GltfModel model) : IDownloadProvider
        {
            public Task<IDownload> Request(Uri url)
            {
                string path = null;
                try { path = Path.GetFullPath(url.IsAbsoluteUri ? url.LocalPath : url.OriginalString); }
                catch (Exception) { }
                return Task.FromResult<IDownload>(path != null && model.Files.TryGetValue(path, out var bytes)
                    ? new Download(bytes, null)
                    : new Download(null, $"\"{url}\" isn't one of the model's files"));
            }

            public Task<ITextureDownload> RequestTexture(Uri url, bool nonReadable) =>
                Task.FromResult<ITextureDownload>(new Download(null, "texture downloads aren't used"));
        }

        private sealed class Download(byte[] data, string error) : ITextureDownload
        {
            public bool Success => data != null;
            public string Error => error;
            public byte[] Data => data;
            public string Text => data == null ? null : System.Text.Encoding.UTF8.GetString(data);
            public bool? IsBinary => data == null ? null : data.Length >= 4 && data[0] == 'g' && data[1] == 'l' && data[2] == 'T' && data[3] == 'F';
            public Texture2D Texture => null;
            public void Dispose() { }
        }

        /// <summary>glTFast's material shaders aren't in the game, and the unit keeps its own material anyway:
        /// glTF materials are read from the JSON (GltfCheck) and baked into the texture.</summary>
        private sealed class PlaceholderMaterials : IMaterialGenerator
        {
            private static Shader shader;

            private static Material Make(string name)
            {
                shader ??= Shader.Find("Hidden/InternalErrorShader");
                return shader == null ? null : new Material(shader) { name = name, hideFlags = HideFlags.DontSave };
            }

            public Material GetDefaultMaterial(bool pointsSupport = false) => Make("PalaceBuilder glTF default");
            public Material GenerateMaterial(GLTFast.Schema.MaterialBase gltfMaterial, IGltfReadable gltf, bool pointsSupport = false) => Make("PalaceBuilder glTF " + gltfMaterial?.name);
            public void SetLogger(ICodeLogger logger) { }
        }
    }

    /// <summary>Records the scene glTFast instantiates: node transforms and primitives, no GameObjects.</summary>
    internal sealed class SceneRecorder : IInstantiator
    {
        public sealed class Node
        {
            public uint? Parent;
            public Matrix4x4 Local;
        }

        public sealed class Primitive
        {
            public uint Node;
            public MeshResult Mesh;
            public uint[] Joints;
            public Matrix4x4[] Instances;   // null: one instance at the node
        }

        public readonly Dictionary<uint, Node> Nodes = [];
        public readonly List<Primitive> Primitives = [];

        public void BeginScene(string name, uint[] rootNodeIndices) { }

        public void CreateNode(uint nodeIndex, uint? parentIndex, Vector3 position, Quaternion rotation, Vector3 scale) =>
            Nodes[nodeIndex] = new Node { Parent = parentIndex, Local = Matrix4x4.TRS(position, rotation, scale) };

        public void SetNodeName(uint nodeIndex, string name) { }

        // A node a file lists both as a scene root and as a child is instantiated twice; keep one copy (its
        // transform is the last CreateNode's).
        private readonly HashSet<(uint, int, int)> added = [];

        public void AddPrimitive(uint nodeIndex, string meshName, MeshResult meshResult, uint[] joints = null, uint? rootJoint = null,
            float[] morphTargetWeights = null, int primitiveNumeration = 0)
        {
            if (added.Add((nodeIndex, meshResult.meshIndex, primitiveNumeration)))
                Primitives.Add(new Primitive { Node = nodeIndex, Mesh = meshResult, Joints = joints });
        }

        public void AddPrimitiveInstanced(uint nodeIndex, string meshName, MeshResult meshResult, uint instanceCount,
            Unity.Collections.NativeArray<Vector3>? positions, Unity.Collections.NativeArray<Quaternion>? rotations,
            Unity.Collections.NativeArray<Vector3>? scales, int primitiveNumeration = 0)
        {
            if (!added.Add((nodeIndex, meshResult.meshIndex, primitiveNumeration))) return;
            var instances = new Matrix4x4[instanceCount];
            for (int i = 0; i < instanceCount; i++)
                instances[i] = Matrix4x4.TRS(
                    positions.HasValue && i < positions.Value.Length ? positions.Value[i] : Vector3.zero,
                    rotations.HasValue && i < rotations.Value.Length ? rotations.Value[i] : Quaternion.identity,
                    scales.HasValue && i < scales.Value.Length ? scales.Value[i] : Vector3.one);
            Primitives.Add(new Primitive { Node = nodeIndex, Mesh = meshResult, Instances = instances });
        }

        public void AddCamera(uint nodeIndex, uint cameraIndex) { }
        public void AddLightPunctual(uint nodeIndex, uint lightIndex) { }
        public void EndScene(uint[] rootNodeIndices) { }

        private readonly Dictionary<uint, Matrix4x4> world = [];

        /// <summary>Node to model space (the scene root).</summary>
        public Matrix4x4 World(uint node)
        {
            if (world.TryGetValue(node, out var m)) return m;
            // Iterative: GltfCheck caps the depth, but don't recurse anyway.
            var chain = new List<uint>();
            for (uint? n = node; n.HasValue && !world.ContainsKey(n.Value); n = Nodes.TryGetValue(n.Value, out var info) ? info.Parent : null)
            {
                chain.Add(n.Value);
                if (chain.Count > GltfCheck.MaxNodeDepth + 1) break;
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                uint n = chain[i];
                var info = Nodes.TryGetValue(n, out var x) ? x : null;
                var local = info?.Local ?? Matrix4x4.identity;
                world[n] = info?.Parent is uint p && world.TryGetValue(p, out var pw) ? pw * local : local;
            }
            return world.TryGetValue(node, out m) ? m : Matrix4x4.identity;
        }
    }
}
