using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

/*
    IAssetProvider (WP5): turns "asset:<key>" refs into Unity objects.

    - Local assets: pack folder + def.Path (must stay inside the folder).
    - Remote assets: def.Url + def.Sha256, downloaded once into PalaceBuilder/cache/<sha256> and verified before use.
      Downloads are asynchronous; the Get* methods return null until the file is cached (Prefetch starts them).
    - OGG decoding goes through UnityWebRequestMultimedia and is asynchronous too; WAV, PNG and OBJ decode inline.

    Loaded objects are cached by (file path, size, mtime, use), so editing a file and changing the pack picks up the
    new version. Every failure is caught: the caller gets null (base visuals stay) and the reason is logged once and
    available through GetState for the editor.
*/
namespace PalaceMod.Builder.Assets
{
    public sealed class AssetProvider : IAssetProvider
    {
        private sealed class Loaded
        {
            public Object Value;          // Sprite, Texture2D, AudioClip or Mesh
            public string Error;
            public bool Pending;          // async decode in flight
        }

        private sealed class Download
        {
            public UnityWebRequest Request;
            public string Sha256, Url, Label;
        }

        private sealed class AudioDecode
        {
            public UnityWebRequest Request;
            public string CacheKey, Label;
        }

        /// <summary>Whether remote (url) assets may be downloaded. Bound to a setting by BuilderAssetsManager.</summary>
        public Func<bool> DownloadsAllowed = () => true;
        /// <summary>Raised on the main thread when a background download or decode finishes (successfully or not).</summary>
        public event Action AssetsArrived;

        private readonly Func<string> root;
        private readonly Dictionary<string, Loaded> cache = [];
        private readonly Dictionary<string, Download> downloads = [];          // by sha256
        private readonly Dictionary<string, string> failedDownloads = [];      // sha256 -> reason
        private readonly Dictionary<string, (float until, string reason)> retryAfter = [];          // transient failures
        private readonly List<AudioDecode> decodes = [];
        private readonly HashSet<string> warned = [];

        public AssetProvider(Func<string> root) { this.root = root; }

        private string CacheDir => root() == null ? null : Path.Combine(root(), "cache");

        // ------------------------------------------------------------------ IAssetProvider

        public Sprite GetSprite(PackEntry pack, string assetRef) => Get<Sprite>(pack, assetRef, AssetUse.Sprite);
        public Texture2D GetTexture(PackEntry pack, string assetRef) => Get<Texture2D>(pack, assetRef, AssetUse.Texture);
        public AudioClip GetAudio(PackEntry pack, string assetRef) => Get<AudioClip>(pack, assetRef, AssetUse.Audio);

        public (Mesh mesh, Material[] materials)? GetMesh(PackEntry pack, string assetRef)
        {
            var mesh = Get<Mesh>(pack, assetRef, AssetUse.Mesh);
            return mesh == null ? null : (mesh, Array.Empty<Material>());
        }

        public AssetState GetState(PackEntry pack, string assetRef, string use, out string message)
        {
            message = null;
            try
            {
                var file = Locate(pack, assetRef, out message, out bool downloading);
                if (downloading) { message = "downloading"; return AssetState.Loading; }
                if (file == null) return message != null && message.StartsWith("download failed") ? AssetState.Failed : AssetState.Missing;
                var kind = Sniff(file, out message);
                if (kind == AssetKind.Unknown) return AssetState.Failed;
                if (use == null) { message = AssetSniffer.Name(kind); return AssetState.Ready; }
                var loaded = Load(file, kind, use, Label(pack, assetRef));
                if (loaded.Pending) { message = "decoding"; return AssetState.Loading; }
                message = loaded.Error;
                return loaded.Value != null ? AssetState.Ready : AssetState.Failed;
            }
            catch (Exception e)
            {
                message = e.Message;
                return AssetState.Failed;
            }
        }

        public void Prefetch(PackEntry pack)
        {
            if (pack?.Pack?.Assets == null) return;
            foreach (var kv in pack.Pack.Assets)
            {
                try
                {
                    var file = Locate(pack, ContentRef.Asset(kv.Key), out _, out _);
                    // Start background decodes (OGG) now, so sounds are ready when the first unit spawns.
                    if (file != null && Sniff(file, out _) == AssetKind.Ogg) Load(file, AssetKind.Ogg, AssetUse.Audio, Label(pack, ContentRef.Asset(kv.Key)));
                }
                catch (Exception e)
                {
                    WarnOnce($"prefetch {pack.Id}/{kv.Key}", $"{pack.Id}/{kv.Key}: {e.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ resolution

        private T Get<T>(PackEntry pack, string assetRef, string use) where T : Object
        {
            if (pack == null || assetRef == null) return null;
            try
            {
                var file = Locate(pack, assetRef, out string why, out bool downloading);
                if (file == null)
                {
                    if (!downloading && why != null) WarnOnce("locate " + Label(pack, assetRef) + why, $"{Label(pack, assetRef)}: {why}");
                    return null;
                }
                var kind = Sniff(file, out why);
                if (kind == AssetKind.Unknown) { WarnOnce("kind " + file, $"{Label(pack, assetRef)}: {why}"); return null; }
                var loaded = Load(file, kind, use, Label(pack, assetRef));
                if (loaded.Error != null) WarnOnce(CacheKey(file, use) + loaded.Error, $"{Label(pack, assetRef)}: {loaded.Error}");
                return loaded.Value as T;
            }
            catch (Exception e)
            {
                WarnOnce("get " + Label(pack, assetRef) + e.Message, $"{Label(pack, assetRef)}: {e.Message}");
                return null;
            }
        }

        /// <summary>The file on disk for an asset ref, or null with the reason (downloading = a download is in flight).</summary>
        private string Locate(PackEntry pack, string assetRef, out string why, out bool downloading)
        {
            why = null;
            downloading = false;
            var r = ContentRef.Parse(assetRef);
            if (r.Kind != RefKind.Asset) { why = $"\"{assetRef}\" isn't an asset reference"; return null; }
            if (pack.Pack?.Assets == null || !pack.Pack.Assets.TryGetValue(r.Id, out var def) || def == null)
            {
                why = $"no asset \"{r.Id}\" in the pack";
                return null;
            }
            if (!string.IsNullOrEmpty(def.Path))
            {
                if (pack.Folder == null) why = "built-in packs can't have asset files";
                else
                {
                    string folder = Path.GetFullPath(pack.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string full = Path.GetFullPath(Path.Combine(folder, def.Path));
                    if (!full.StartsWith(folder, StringComparison.Ordinal)) why = $"{def.Path} is outside the pack folder";
                    else if (File.Exists(full)) return full;
                    else why = $"file {def.Path} not found";
                }
                if (string.IsNullOrEmpty(def.Url)) return null;
            }
            if (!string.IsNullOrEmpty(def.Url))
            {
                if (!Validator.IsSha256(def.Sha256)) { why = "a url asset needs its sha256"; return null; }
                var cached = CachePath(def.Sha256);
                if (cached != null && File.Exists(cached)) return cached;
                if (failedDownloads.TryGetValue(def.Sha256, out string failure)) { why = "download failed: " + failure; return null; }
                downloading = StartDownload(def.Url, def.Sha256, Label(pack, assetRef), out why);
                return null;
            }
            return null;
        }

        private string CachePath(string sha) => CacheDir == null ? null : Path.Combine(CacheDir, sha);

        private static AssetKind Sniff(string file, out string why)
        {
            why = null;
            var kind = AssetSniffer.DetectFile(file);
            if (kind == AssetKind.Unknown) why = "not a PNG, WAV, OGG or OBJ file";
            return kind;
        }

        private static string Label(PackEntry pack, string assetRef) => $"{pack.Id}/{ContentRef.Parse(assetRef).Id}";

        private static string CacheKey(string file, string use)
        {
            var info = new FileInfo(file);
            return $"{use}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{file}";
        }

        // ------------------------------------------------------------------ decoding

        private Loaded Load(string file, AssetKind kind, string use, string label)
        {
            string key = CacheKey(file, use);
            if (cache.TryGetValue(key, out var hit) && (hit.Pending || hit.Error != null || hit.Value != null)) return hit;
            var loaded = new Loaded();
            cache[key] = loaded;
            try
            {
                string expected = use switch
                {
                    AssetUse.Sprite or AssetUse.Texture => kind == AssetKind.Png ? null : "an image (PNG)",
                    AssetUse.Audio => kind is AssetKind.Wav or AssetKind.Ogg ? null : "a sound (WAV or OGG)",
                    AssetUse.Mesh => kind == AssetKind.Obj ? null : kind == AssetKind.Glb ? "an OBJ mesh (glTF isn't supported yet)" : "a mesh (OBJ)",
                    _ => null,
                };
                if (expected != null) throw new AssetException($"is a {AssetSniffer.Name(kind)}, not {expected}");
                long length = new FileInfo(file).Length;
                long max = AssetLimits.MaxBytes(kind);
                if (length > max) throw new AssetException($"{AssetSniffer.Name(kind)} is {AssetLimits.Mb(length)}; the limit is {AssetLimits.Mb(max)}");

                string name = "PalaceBuilder " + label;
                switch (kind)
                {
                    case AssetKind.Png:
                    {
                        var tex = PngLoader.Load(File.ReadAllBytes(file), name, use == AssetUse.Sprite);
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        if (use == AssetUse.Sprite)
                        {
                            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                            sprite.name = name;
                            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            loaded.Value = sprite;
                        }
                        else loaded.Value = tex;
                        break;
                    }
                    case AssetKind.Wav:
                    {
                        var clip = WavLoader.Load(File.ReadAllBytes(file), name);
                        clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        loaded.Value = clip;
                        break;
                    }
                    case AssetKind.Ogg:
                    {
                        var bytes = File.ReadAllBytes(file);
                        OggLoader.Check(bytes, bytes.Length);
                        var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.OGGVORBIS);
                        ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
                        request.SendWebRequest();
                        loaded.Pending = true;
                        decodes.Add(new AudioDecode { Request = request, CacheKey = key, Label = label });
                        break;
                    }
                    case AssetKind.Obj:
                    {
                        var mesh = ObjLoader.Load(File.ReadAllBytes(file), name);
                        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        loaded.Value = mesh;
                        break;
                    }
                }
                if (loaded.Value != null) Plugin.Logger.LogInfo($"Builder assets: loaded {label} ({AssetSniffer.Name(kind)}, {Describe(loaded.Value)})");
            }
            catch (AssetException e)
            {
                loaded.Error = e.Message;
            }
            catch (Exception e)
            {
                loaded.Error = "couldn't be read: " + e.Message;
            }
            return loaded;
        }

        private static string Describe(Object o) => o switch
        {
            Sprite s => $"{s.texture.width}x{s.texture.height}",
            Texture2D t => $"{t.width}x{t.height}",
            AudioClip c => $"{c.length:0.##} s, {c.channels} ch, {c.frequency} Hz",
            Mesh m => $"{m.vertexCount} vertices, {m.triangles.Length / 3} triangles, size {m.bounds.size}",
            _ => o.GetType().Name,
        };

        // ------------------------------------------------------------------ downloads

        private bool StartDownload(string url, string sha, string label, out string why)
        {
            why = null;
            if (downloads.ContainsKey(sha)) return true;
            if (!DownloadsAllowed()) { why = "downloading pack assets is turned off (Builder settings)"; return false; }
            if (CacheDir == null) { why = "no pack folder"; return false; }
            if (retryAfter.TryGetValue(sha, out var wait) && Time.unscaledTime < wait.until) { why = $"download failed: {wait.reason} (retrying in a minute)"; return false; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                failedDownloads[sha] = "only http and https urls are allowed";
                why = "download failed: " + failedDownloads[sha];
                return false;
            }
            var request = UnityWebRequest.Get(uri);
            request.timeout = 30;
            request.redirectLimit = 4;
            request.SendWebRequest();
            downloads[sha] = new Download { Request = request, Sha256 = sha, Url = url, Label = label };
            Plugin.Logger.LogInfo($"Builder assets: downloading {label} from {url}");
            return true;
        }

        /// <summary>Advances downloads and background decodes. Called every frame by BuilderAssetsManager.</summary>
        public void Update()
        {
            if (downloads.Count == 0 && decodes.Count == 0) return;
            bool arrived = false;
            foreach (var d in downloads.Values.ToList())
            {
                var req = d.Request;
                if (!req.isDone)
                {
                    if (req.downloadedBytes > (ulong)AssetLimits.MaxDownloadBytes) req.Abort();
                    continue;
                }
                downloads.Remove(d.Sha256);
                arrived = true;
                try
                {
                    FinishDownload(d);
                }
                catch (Exception e)
                {
                    Fail(d, e.Message);
                }
                finally
                {
                    req.Dispose();
                }
            }
            for (int i = decodes.Count - 1; i >= 0; i--)
            {
                var d = decodes[i];
                if (!d.Request.isDone) continue;
                decodes.RemoveAt(i);
                arrived = true;
                if (!cache.TryGetValue(d.CacheKey, out var loaded)) loaded = cache[d.CacheKey] = new Loaded();
                loaded.Pending = false;
                try
                {
                    if (d.Request.result != UnityWebRequest.Result.Success) loaded.Error = "OGG couldn't be decoded: " + d.Request.error;
                    else
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(d.Request);
                        if (clip == null || clip.loadState == AudioDataLoadState.Failed || clip.samples == 0) loaded.Error = "OGG couldn't be decoded";
                        else if (clip.length > AssetLimits.MaxAudioSeconds) { loaded.Error = $"sound is longer than {AssetLimits.MaxAudioSeconds:0} s"; Object.Destroy(clip); }
                        else
                        {
                            clip.name = "PalaceBuilder " + d.Label;
                            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            loaded.Value = clip;
                            Plugin.Logger.LogInfo($"Builder assets: loaded {d.Label} (OGG sound, {Describe(clip)})");
                        }
                    }
                }
                catch (Exception e)
                {
                    loaded.Error = "OGG couldn't be decoded: " + e.Message;
                }
                finally
                {
                    d.Request.Dispose();
                }
                if (loaded.Error != null) WarnOnce(d.CacheKey + loaded.Error, $"{d.Label}: {loaded.Error}");
            }
            if (arrived) AssetsArrived?.Invoke();
        }

        private void FinishDownload(Download d)
        {
            var req = d.Request;
            if (req.result != UnityWebRequest.Result.Success)
            {
                bool tooBig = req.downloadedBytes > (ulong)AssetLimits.MaxDownloadBytes;
                Fail(d, tooBig ? $"file is larger than {AssetLimits.Mb(AssetLimits.MaxDownloadBytes)}" : req.error, retry: !tooBig);
                return;
            }
            var data = req.downloadHandler.data;
            if (data == null || data.Length == 0) { Fail(d, "empty response", retry: true); return; }
            if (data.Length > AssetLimits.MaxDownloadBytes) { Fail(d, $"file is larger than {AssetLimits.Mb(AssetLimits.MaxDownloadBytes)}"); return; }
            string actual;
            using (var sha = SHA256.Create()) actual = string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
            if (actual != d.Sha256) { Fail(d, "the file's sha256 doesn't match the pack (it changed or is the wrong file)"); return; }
            Directory.CreateDirectory(CacheDir);
            string target = CachePath(d.Sha256), temp = target + ".part";
            File.WriteAllBytes(temp, data);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            Plugin.Logger.LogInfo($"Builder assets: cached {d.Label} ({AssetLimits.Mb(data.Length)}) as cache/{d.Sha256}");
        }

        private void Fail(Download d, string reason, bool retry = false)
        {
            if (retry) retryAfter[d.Sha256] = (Time.unscaledTime + 60f, reason);
            else failedDownloads[d.Sha256] = reason;
            WarnOnce("download " + d.Sha256 + reason, $"{d.Label}: download from {d.Url} failed: {reason}");
        }

        // ------------------------------------------------------------------ housekeeping

        /// <summary>Drops cached objects whose files changed or vanished (a fixed file is then decoded again).
        /// Only call when no template or live unit can still reference them (outside sessions,
        /// after IUnitFactory.Clear).</summary>
        public void Sweep()
        {
            foreach (var key in cache.Keys.ToList())
            {
                var loaded = cache[key];
                if (loaded.Pending) continue;
                string file = key.Substring(key.IndexOf('|', key.IndexOf('|', key.IndexOf('|') + 1) + 1) + 1);
                bool stale = !File.Exists(file) || CacheKey(file, key.Substring(0, key.IndexOf('|'))) != key;
                if (!stale) continue; // errors stay cached until the file changes (no re-decoding a bad file on every rescan)
                cache.Remove(key);
                Destroy(loaded.Value);
            }
        }

        /// <summary>Destroys every loaded object and cancels background work.</summary>
        public void Clear()
        {
            foreach (var loaded in cache.Values) Destroy(loaded.Value);
            cache.Clear();
            foreach (var d in downloads.Values) d.Request.Dispose();
            downloads.Clear();
            foreach (var d in decodes) d.Request.Dispose();
            decodes.Clear();
            failedDownloads.Clear();
            warned.Clear();
        }

        private static void Destroy(Object o)
        {
            if (o == null) return;
            if (o is Sprite s && s.texture != null) Object.Destroy(s.texture);
            Object.Destroy(o);
        }

        internal IEnumerable<string> Status()
        {
            foreach (var kv in cache)
                yield return $"{kv.Key.Split('|')[0]} {Path.GetFileName(kv.Key.Substring(kv.Key.LastIndexOf('|') + 1))}: " +
                    (kv.Value.Pending ? "decoding" : kv.Value.Error ?? (kv.Value.Value == null ? "null" : Describe(kv.Value.Value)));
            foreach (var d in downloads.Values) yield return $"download {d.Label}: {d.Request.downloadedBytes} bytes";
            foreach (var kv in failedDownloads) yield return $"download {kv.Key}: failed: {kv.Value}";
        }

        private void WarnOnce(string key, string message)
        {
            if (warned.Add(key)) Plugin.Logger.LogWarning("Builder assets: " + message);
        }
    }
}
