using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

/*
    A zip for bug reports: BepInEx/PalaceMod/DebugBundles/PalaceMod-debug-<time>.zip with
      info.txt        mod version and commit, game/Unity/BepInEx versions, OS and hardware, where in the game the
                      player is, gameplay impact, log settings, features (enabled, patched methods), other plugins
      recent.log      the last PalaceMod log lines and every warning/error, with timestamps
      LogOutput.log   this session's BepInEx log (the last 4 MB)
      PalaceMod.cfg   the mod's settings
    The home folder is replaced by "~" in everything written, so the user name doesn't end up in the bundle.
    Made from Settings > Mods > Diagnostics > "Create debug bundle" or `dev.py cmd "log bundle"`.
*/
namespace PalaceMod
{
    internal static class DebugBundle
    {
        private const long MaxLogBytes = 4 * 1024 * 1024;

        internal static string Folder => Path.Combine(Paths.BepInExRootPath, "PalaceMod", "DebugBundles");

        public static string Create()
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"PalaceMod-debug-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(zip, "info.txt", Info());
                Add(zip, "recent.log", string.Join("\n", LogManager.RecentLines.Instance.Snapshot()) + "\n");
                AddFile(zip, "LogOutput.log", Path.Combine(Paths.BepInExRootPath, "LogOutput.log"));
                AddFile(zip, "PalaceMod.cfg", Plugin.Instance.Config.ConfigFilePath);
                AddFile(zip, "PalaceMod.Bootstrap.log", Path.Combine(Paths.BepInExRootPath, "PalaceMod.Bootstrap.log"));
            }
            Log.For<LogManager>().Info($"Debug bundle written: {DisplayPath(path)}");
            return path;
        }

        /// <summary>The path with the home folder shortened to "~".</summary>
        internal static string DisplayPath(string path) => Redact(path);

        private static string Redact(string text)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home) || home.Length < 3) return text;
            return text.Replace(home, "~").Replace(home.Replace('/', '\\'), "~");
        }

        private static void Add(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(Redact(text));
        }

        private static void AddFile(ZipArchive zip, string name, string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                // The log is open for writing by BepInEx; share it.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                string prefix = "";
                if (stream.Length > MaxLogBytes)
                {
                    stream.Seek(-MaxLogBytes, SeekOrigin.End);
                    prefix = $"[... first {stream.Length - MaxLogBytes} bytes left out ...]\n";
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Add(zip, name, prefix + reader.ReadToEnd());
            }
            catch (Exception e)
            {
                Add(zip, name + ".error.txt", $"Couldn't read {path}: {e.Message}\n");
            }
        }

        private static string Info()
        {
            var s = new StringBuilder();
            void Line(string key, Func<object> value)
            {
                string text;
                try { text = value()?.ToString() ?? ""; }
                catch (Exception e) { text = $"(unavailable: {e.GetType().Name})"; }
                s.Append(key).Append(": ").Append(text).Append('\n');
            }

            s.Append("PalaceMod debug bundle\n\n");
            Line("Created", () => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)");
            Line("PalaceMod", () => ModVersion);
            Line("Dev launch", () => Environment.GetEnvironmentVariable("PALACEMOD_DEV") == "1");
            Line("Game", () => $"{Application.productName} {Application.version}");
            Line("Unity", () => Application.unityVersion);
            Line("BepInEx", () => typeof(Paths).Assembly.GetName().Version);
            Line("OS", () => SystemInfo.operatingSystem);
            Line("Architecture", () => $"process {RuntimeInformation.ProcessArchitecture}, OS {RuntimeInformation.OSArchitecture}");
            Line("CPU", () => $"{SystemInfo.processorType} ({SystemInfo.processorCount} threads)");
            Line("Memory", () => $"{SystemInfo.systemMemorySize} MB");
            Line("Device", () => SystemInfo.deviceModel);
            Line("GPU", () => $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsMemorySize} MB)");
            Line("Screen", () => $"{Screen.width}x{Screen.height} {Screen.fullScreenMode}, {Screen.currentResolution.refreshRateRatio.value:0.#} Hz");
            Line("Uptime", () => $"{Time.realtimeSinceStartup:0} s");
            Line("Where", GameContext.Describe);
            Line("Gameplay impact", () => GameplayImpact.Any ? string.Join(", ", GameplayImpact.Reasons) : "none");
            Line("Log level", () => $"{Log.Verbosity}, debug for [{string.Join(", ", Log.DebugFeatures)}]");

            s.Append("\nFeatures (section: state, patched methods)\n");
            foreach (var m in Plugin.Instance.Managers.OrderBy(m => m.FeatureSection ?? "~" + m.GetType().Name))
            {
                string state = m.FeatureSection == null ? "always on" : m.IsEnabled ? "enabled" : "disabled";
                s.Append($"  {m.FeatureSection ?? "-"} ({m.GetType().Name}): {state}, {m.PatchedMethodCount}\n");
            }

            s.Append("\nPlugins\n");
            foreach (var p in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.GUID))
                s.Append($"  {p.Metadata.GUID} {p.Metadata.Version} ({p.Metadata.Name})\n");
            return s.ToString();
        }

        /// <summary>"1.0.0+13350b87..." (version and the commit it was built from).</summary>
        internal static string ModVersion =>
            typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? MyPluginInfo.PLUGIN_VERSION;
    }
}
