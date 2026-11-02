using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

/*
    Runs the mod-wide logging (Core/Log.cs): the Diagnostics settings, the ring buffer of recent lines for the debug
    bundle, flushing of collapsed repeats, and attribution of Unity-logged exceptions whose stack runs through
    PalaceMod code ("[Savestates] Unhandled exception ... | scene ..., night 3" plus the stack, once per distinct
    exception). Dev command: `log` (see its usage line).
*/
namespace PalaceMod
{
    [ManagerOrder(-100)]
    public class LogManager : BaseManager
    {
        internal const string Section = "Diagnostics";

        private ConfigEntry<LogVerbosity> level;
        private ConfigEntry<string> debugFeatures;
        private ConfigEntry<bool> createBundle;
        private bool bundleRequested;
        private float nextFlush;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            level = plugin.Config.Bind(Section, "Log level", LogVerbosity.Info,
                "How much PalaceMod writes to BepInEx/LogOutput.log. Info: what features do. Debug: much more detail, " +
                "for chasing a bug.");
            debugFeatures = plugin.Config.Bind(Section, "Debug logging for", "", new ConfigDescription(
                "Comma-separated feature names (the [Name] prefix of their log lines) that log at Debug level whatever " +
                "the log level, or * for all.", null, new SettingTags { Hidden = true }));
            createBundle = plugin.Config.Bind(Section, "Create debug bundle", false,
                "Turn on to save a zip for a bug report (recent logs, settings, versions, system info) to " +
                "BepInEx/PalaceMod/DebugBundles. Turns itself back off.");
            createBundle.Value = false; // a leftover "true" from a crash shouldn't make a bundle at every launch

            ApplySettings();
            level.SettingChanged += (_, _) => ApplySettings();
            debugFeatures.SettingChanged += (_, _) => ApplySettings();
            createBundle.SettingChanged += (_, _) => bundleRequested |= createBundle.Value;

            Application.logMessageReceived += OnUnityLog;
            RegisterCommands();
        }

        private void ApplySettings()
        {
            Log.Verbosity = level.Value;
            Log.DebugFeatures = (debugFeatures.Value ?? "").Split(',');
        }

        public override void Update()
        {
            if (bundleRequested)
            {
                bundleRequested = false;
                createBundle.Value = false;
                CreateBundleAndNotify();
            }
            if (Time.unscaledTime >= nextFlush)
            {
                nextFlush = Time.unscaledTime + 5f;
                Log.Repeats.Flush();
            }
        }

        public override void OnDestroy() => Application.logMessageReceived -= OnUnityLog;

        internal static string CreateBundleAndNotify()
        {
            try
            {
                string path = DebugBundle.Create();
                string shown = DebugBundle.DisplayPath(path);
                try { GUIUtility.systemCopyBuffer = path; } catch { }
                Notify.Show($"Debug bundle saved to {shown} (path copied)", NotifyKind.Success, 6f, key: "palacemod-debug-bundle");
                return path;
            }
            catch (Exception e)
            {
                Log.For<LogManager>().Error("Couldn't create the debug bundle", e);
                Notify.Show("Couldn't create the debug bundle; see BepInEx/LogOutput.log", NotifyKind.Error, 5f, key: "palacemod-debug-bundle");
                return null;
            }
        }

        // ---- Unity log ---------------------------------------------------------------------------------------------

        private bool inUnityLog;

        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (inUnityLog || type is not (LogType.Exception or LogType.Error or LogType.Assert)) return;
            inUnityLog = true;
            try
            {
                var log = Log.AttributeStack(stackTrace);
                if (log == null) return;
                string what = type == LogType.Exception ? "Unhandled exception" : "Unity error";
                log.Write(Log.Level.Error, $"{what} in PalaceMod code: {condition}", null, true, stackTrace.TrimEnd());
            }
            finally
            {
                inUnityLog = false;
            }
        }

        // ---- Ring buffer -----------------------------------------------------------------------------------------

        /// <summary>The last lines PalaceMod logged (all levels it wrote) plus every warning and error from any
        /// source, with timestamps. Registered as a BepInEx log listener by Plugin before anything else runs.</summary>
        internal sealed class RecentLines : ILogListener
        {
            public static readonly RecentLines Instance = new();
            private const int Capacity = 2000;
            private readonly string[] lines = new string[Capacity];
            private int next, count;
            private object lastData;
            private int lastRepeats;

            public void LogEvent(object sender, LogEventArgs args)
            {
                bool mine = args.Source?.SourceName == MyPluginInfo.PLUGIN_NAME;
                if (!mine && (args.Level & (BepInEx.Logging.LogLevel.Fatal | BepInEx.Logging.LogLevel.Error | BepInEx.Logging.LogLevel.Warning)) == 0) return;
                string data = args.Data?.ToString() ?? "";
                string line = $"{DateTime.Now:HH:mm:ss.fff} [{args.Level,-7}:{args.Source?.SourceName}] {data}";
                lock (lines)
                {
                    // A message repeated back to back (a per-frame Unity exception) takes one line with a count.
                    if (count > 0 && data == (string)lastData)
                    {
                        lastRepeats++;
                        lines[(next - 1 + Capacity) % Capacity] = $"{line} [x{lastRepeats + 1}]";
                        return;
                    }
                    lastData = data;
                    lastRepeats = 0;
                    lines[next] = line;
                    next = (next + 1) % Capacity;
                    if (count < Capacity) count++;
                }
            }

            public List<string> Snapshot()
            {
                lock (lines)
                {
                    var list = new List<string>(count);
                    for (int i = 0; i < count; i++) list.Add(lines[(next - count + i + Capacity) % Capacity]);
                    return list;
                }
            }

            public void Dispose() { }
        }

        // ---- Dev command -------------------------------------------------------------------------------------------

        private static bool throwInPatch;
        private static int throwFrames;

        private void RegisterCommands()
        {
            DevCommands.Register("log",
                "log [level <errors|warnings|info|debug> | debug <feature|*> [on|off] | names | bundle | throw [frames] | test]   logging settings and checks",
                args =>
                {
                    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    switch (sub)
                    {
                        case "":
                            return $"level {level.Value}, debug for [{string.Join(", ", Log.DebugFeatures)}]";
                        case "level":
                            if (args.Length < 2 || !Enum.TryParse(args[1], true, out LogVerbosity v)) throw new ArgumentException("log level <errors|warnings|info|debug>");
                            level.Value = v;
                            return $"level {v}";
                        case "debug":
                        {
                            if (args.Length < 2) throw new ArgumentException("log debug <feature|*> [on|off]");
                            bool on = args.Length < 3 || !args[2].Equals("off", StringComparison.OrdinalIgnoreCase);
                            var set = Log.DebugFeatures.Where(n => !n.Equals(args[1], StringComparison.OrdinalIgnoreCase)).ToList();
                            if (on) set.Add(args[1]);
                            debugFeatures.Value = string.Join(",", set);
                            return $"debug for [{string.Join(", ", Log.DebugFeatures)}]";
                        }
                        case "names":
                            return string.Join(", ", Log.KnownNames);
                        case "bundle":
                            return CreateBundleAndNotify() ?? throw new InvalidOperationException("bundle failed; see the log");
                        case "throw":
                            // Throws from a Harmony postfix on UIFrameManager.Update for N frames (dev launches only).
                            throwFrames = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 1;
                            throwInPatch = true;
                            return $"throwing in a patch for {throwFrames} frame(s)";
                        case "test":
                        {
                            var log = Log.For("LogTest");
                            log.Debug($"debug line, shown only with debug on for LogTest ({DateTime.Now:HH:mm:ss})");
                            log.Info($"info line ({DateTime.Now:HH:mm:ss})");
                            log.Warn("warning line");
                            for (int i = 0; i < 5; i++) log.Info("repeated line");
                            return "wrote debug, info, warning and 5 identical info lines (4 should collapse)";
                        }
                        default:
                            throw new ArgumentException($"unknown subcommand \"{sub}\"");
                    }
                });
        }

        [HarmonyPatch(typeof(UIFrameManager), "Update")]
        private class ThrowTest_Patch
        {
            private static bool Prepare() => Environment.GetEnvironmentVariable("PALACEMOD_DEV") == "1";

            private static void Postfix()
            {
                if (!throwInPatch) return;
                if (--throwFrames <= 0) throwInPatch = false;
                throw new InvalidOperationException("Test exception from LogManager's UIFrameManager.Update postfix (dev command `log throw`)");
            }
        }
    }
}
