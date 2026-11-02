using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Pathfinding;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

/*
    Performance and battery features that don't change gameplay. Split across partial files:
    - PerformanceManager.cs: settings and lifecycle.
    - PerformanceManager.FrameRate.cs: precise frame pacing, refresh rate, and lowering the frame rate in menus, in
      the background, on battery and when the Mac is hot.
    - PerformanceManager.Diagnostics.cs: spike logging and frame-time summaries.
    - PerformanceManager.HotPaths.cs: Harmony patches removing per-frame work and garbage in game code, with results
      identical to the originals.
*/
namespace PalaceMod
{
    public partial class PerformanceManager : BaseManager
    {
        public enum Toggle { Auto, On, Off }
        public enum VSyncMode { GameDefault, On, Off }
        public enum PathfindingThreads { GameDefault, None, One, Two, Four, AutomaticLowLoad, AutomaticHighLoad }

        private const string FrameRateSection = "Frame rate";
        private const string BatterySection = "Battery";
        private const string MemorySection = "Memory";
        private const string DiagnosticsSection = "Diagnostics";
        private const string AdvancedSection = "Advanced";

        // Frame rate
        private ConfigEntry<Toggle> precisePacing;
        private ConfigEntry<int> maxFrameRate;
        private ConfigEntry<int> raiseRefreshRate;
        private ConfigEntry<int> menuFrameRate;
        private ConfigEntry<bool> lockToDisplay;
        // Battery
        private ConfigEntry<int> backgroundFrameRate;
        private ConfigEntry<int> batteryFrameRate;
        private ConfigEntry<bool> saveCpuOnBattery;
        private ConfigEntry<bool> coolDownWhenHot;
        private ConfigEntry<int> idleFrameRate;
        private ConfigEntry<int> idleAfterSeconds;
        // Memory
        private ConfigEntry<int> heapReserveMb;
        private ConfigEntry<float> gcTimeSliceMs;
        private ConfigEntry<bool> collectWhenPaused;
        // Diagnostics
        private ConfigEntry<bool> spikeLogging;
        private ConfigEntry<float> spikeThresholdMs;
        private ConfigEntry<float> spikeRatio;
        private ConfigEntry<float> summaryIntervalSeconds;
        // Advanced
        private ConfigEntry<Toggle> latencyCritical;
        private ConfigEntry<float> maxSpinMs;
        private ConfigEntry<VSyncMode> vSync;
        private ConfigEntry<int> targetFrameRate;
        private ConfigEntry<float> renderScale;
        private ConfigEntry<int> msaa;
        private ConfigEntry<PathfindingThreads> pathfindingThreads;

        private DateTime configWriteTime;
        private float nextConfigCheck;

        private static bool IsMac => Application.platform == RuntimePlatform.OSXPlayer;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            BindSettings(plugin.Config);
            configWriteTime = System.IO.File.GetLastWriteTimeUtc(plugin.Config.ConfigFilePath);

            if (IsMac)
            {
                TryLog("display link", () => DisplayLink.Start() ? "running" : "unavailable");
                PowerState.Refresh();
                Plugin.Logger.LogInfo($"Performance: on battery {PowerState.OnBattery}, low power mode {PowerState.LowPowerMode}, thermal {PowerState.ThermalState}");
            }
            AstarPath.OnAwakeSettings += ConfigurePathfinding;
            ReserveHeap(heapReserveMb.Value);
            ApplyGcTimeSlice();
            gcTimeSliceMs.SettingChanged += (_, _) => ApplyGcTimeSlice();

            pacer.Install();
            if (spikeLogging.Value) loopTimer.Install(); // after the pacer, so its wait shows up as its own entry
            lastGcCount = GC.CollectionCount(0);
            Plugin.Logger.LogInfo($"Performance: GC incremental {GarbageCollector.isIncremental}, heap {Profiler.GetMonoHeapSizeLong() / 1048576} MB, " +
                $"run in background {Application.runInBackground}, arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        }

        private void BindSettings(ConfigFile config)
        {
            precisePacing = config.Bind(FrameRateSection, "Precise frame pacing", Toggle.Auto,
                "Pace frames with the mod's own precise timer instead of VSync and Unity's frame limiter, which both " +
                "oversleep by several frames on macOS and make the game stutter. Auto: on for macOS.");
            maxFrameRate = config.Bind(FrameRateSection, "Max frame rate", 0, new ConfigDescription(
                "Frame rate cap while playing. 0 matches the display's refresh rate.", new AcceptableValueRange<int>(0, 240)));
            raiseRefreshRate = config.Bind(FrameRateSection, "Raise refresh rate (Hz)", 120, new ConfigDescription(
                "macOS: while the game runs, switch the display to the highest refresh rate up to this, e.g. 120 on a " +
                "ProMotion screen set to 60 Hz. macOS restores your setting when the game quits. 0 leaves it alone. " +
                "Takes effect at launch.", new AcceptableValueList<int>(0, 90, 100, 120, 144)));
            menuFrameRate = config.Bind(FrameRateSection, "Menu frame rate", 60, new ConfigDescription(
                "Frame rate cap on the main menu and while paused. 0 for no extra cap.", new AcceptableValueRange<int>(0, 240)));
            lockToDisplay = config.Bind(FrameRateSection, "Lock to display refresh", true,
                "Start each frame just after a display refresh so frames don't drift across refreshes, which looks like judder.");

            backgroundFrameRate = config.Bind(BatterySection, "Background frame rate", 15, new ConfigDescription(
                "Frame rate cap while the game window isn't focused (if the game keeps running in the background). " +
                "0 for no extra cap.", new AcceptableValueRange<int>(0, 240)));
            batteryFrameRate = config.Bind(BatterySection, "Battery frame rate", 60, new ConfigDescription(
                "Frame rate cap while on battery power or in Low Power Mode. 0 for no extra cap.", new AcceptableValueRange<int>(0, 240)));
            saveCpuOnBattery = config.Bind(BatterySection, "Save CPU on battery", true,
                "On battery, sleep instead of busy-waiting for the exact frame time. Uses much less CPU; frame timing gets slightly less precise.");
            coolDownWhenHot = config.Bind(BatterySection, "Cool down when hot", true,
                "Cap the frame rate at 60 when macOS reports the Mac is under thermal pressure, and at 30 when critical.");

            idleFrameRate = config.Bind(BatterySection, "Idle frame rate", 30, new ConfigDescription(
                "Frame rate cap when there's been no input for a while, outside of night battles. 0 for no extra cap.",
                new AcceptableValueRange<int>(0, 240)));
            idleAfterSeconds = config.Bind(BatterySection, "Idle after (s)", 60, new ConfigDescription(
                "Seconds without keyboard, mouse or controller input before the idle frame rate applies.", new AcceptableValueRange<int>(5, 600)));

            heapReserveMb = config.Bind(MemorySection, "Minimum heap size (MB)", 512, new ConfigDescription(
                "Grow the managed heap to at least this size at startup so it doesn't have to expand (and stall) during play. " +
                "0 disables. Takes effect at launch.", new AcceptableValueRange<int>(0, 2048)));
            gcTimeSliceMs = config.Bind(MemorySection, "Incremental GC time slice (ms)", 0f, new ConfigDescription(
                "Per-frame budget for the incremental garbage collector. 0 keeps the game's default (3 ms).", new AcceptableValueRange<float>(0f, 10f)));

            collectWhenPaused = config.Bind(MemorySection, "Collect garbage when paused", true,
                "Run a garbage collection when the game is paused or a menu opens, where the pause can't be felt, so " +
                "fewer collections happen mid-battle.");

            spikeLogging = config.Bind(DiagnosticsSection, "Log frame spikes", true,
                "Write frame-time spikes and a periodic frame-time summary to the BepInEx log. Takes effect at launch.");
            spikeThresholdMs = config.Bind(DiagnosticsSection, "Spike threshold (ms)", 25f, new ConfigDescription(
                "A frame counts as a spike when it takes longer than this...", new AcceptableValueRange<float>(5f, 200f)));
            spikeRatio = config.Bind(DiagnosticsSection, "Spike ratio", 2f, new ConfigDescription(
                "...and longer than this multiple of the recent average frame time.", new AcceptableValueRange<float>(1f, 10f)));
            summaryIntervalSeconds = config.Bind(DiagnosticsSection, "Summary interval (s)", 60f, new ConfigDescription(
                "How often to log the frame-time summary.", new AcceptableValueRange<float>(5f, 600f)));

            latencyCritical = config.Bind(AdvancedSection, "Latency-critical mode", Toggle.Auto,
                "Ask macOS to treat the game as latency-critical while it's focused, which disables App Nap and timer " +
                "coalescing so waits don't overshoot. Auto: on for macOS.");
            maxSpinMs = config.Bind(AdvancedSection, "Max busy-wait (ms)", 4f, new ConfigDescription(
                "Longest the frame pacer busy-waits before each frame for exact timing. It adapts to how precisely this Mac " +
                "wakes from sleep and only spins as long as needed.", new AcceptableValueRange<float>(0f, 8f)));
            vSync = config.Bind(AdvancedSection, "VSync override", VSyncMode.GameDefault,
                "Override the game's VSync setting. Ignored while precise frame pacing is on.");
            targetFrameRate = config.Bind(AdvancedSection, "Unity frame limiter", 0, new ConfigDescription(
                "Unity's own frame cap when VSync is off. 0 keeps the game's setting, -1 is uncapped. Ignored while precise " +
                "frame pacing is on.", new AcceptableValueRange<int>(-1, 240)));
            renderScale = config.Bind(AdvancedSection, "Render scale override", 0f, new ConfigDescription(
                "Resolution multiplier for 3D rendering (UI stays sharp). 0 keeps the game's setting.", new AcceptableValueRange<float>(0f, 2f)));
            msaa = config.Bind(AdvancedSection, "MSAA override", 0, new ConfigDescription(
                "Anti-aliasing samples. 0 keeps the game's setting.", new AcceptableValueList<int>(0, 1, 2, 4, 8)));
            pathfindingThreads = config.Bind(AdvancedSection, "Pathfinding threads", PathfindingThreads.GameDefault,
                "Threads A* uses to compute paths, applied when a level loads.");

            BindOptimizations(config);
        }

        private static void TryLog(string what, Func<string> action)
        {
            try
            {
                Plugin.Logger.LogInfo($"Performance: {what} {action()}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Performance: {what} failed: {e.Message}");
            }
        }

        public override void OnDestroy()
        {
            AstarPath.OnAwakeSettings -= ConfigurePathfinding;
            if (MacActivity.Active) MacActivity.End();
        }

        public override void Update()
        {
            ReloadConfigIfChanged();
            UpdateFrameRate();
            UpdateDiagnostics();
        }

        // Lets settings be tuned while the game runs by editing the config file.
        private void ReloadConfigIfChanged()
        {
            if (Time.unscaledTime < nextConfigCheck) return;
            nextConfigCheck = Time.unscaledTime + 2f;
            var written = System.IO.File.GetLastWriteTimeUtc(Plugin.Config.ConfigFilePath);
            if (written == configWriteTime) return;
            configWriteTime = written;
            Plugin.Config.Reload();
            Plugin.Logger.LogInfo("Performance: reloaded config");
            stats.Reset();
        }

        // Runs inside AstarPath.OnEnable, just before its path threads start.
        private void ConfigurePathfinding()
        {
            var astar = AstarPath.active;
            if (astar == null) return;
            var original = astar.threadCount;
            if (pathfindingThreads.Value != PathfindingThreads.GameDefault)
            {
                astar.threadCount = (ThreadCount)Enum.Parse(typeof(ThreadCount), pathfindingThreads.Value.ToString());
            }
            Plugin.Logger.LogInfo($"Performance: A* threads {original}{(astar.threadCount != original ? $" -> {astar.threadCount}" : "")}, " +
                $"max frame time {astar.maxFrameTime} ms, navmesh cut interval {astar.navmeshUpdates.updateInterval} s, graphs {astar.graphs?.Length ?? 0}");
        }

        private static void ReserveHeap(int megabytes)
        {
            long before = Profiler.GetMonoHeapSizeLong();
            int missing = megabytes - (int)(before / 1048576);
            if (missing <= 0) return;
            // Hold every chunk at once so the collector has to expand the heap instead of reusing freed space.
            var chunks = new List<byte[]>(missing);
            for (int i = 0; i < missing; i++) chunks.Add(new byte[1024 * 1024]);
            chunks.Clear();
            Plugin.Logger.LogInfo($"Performance: grew managed heap {before / 1048576} MB -> {Profiler.GetMonoHeapSizeLong() / 1048576} MB");
        }

        private void ApplyGcTimeSlice()
        {
            if (gcTimeSliceMs.Value <= 0f || !GarbageCollector.isIncremental) return;
            GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(gcTimeSliceMs.Value * 1e6f);
        }
    }
}
