using System;
using System.Collections.Generic;
using System.Linq;
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
        public override string FeatureSection => "Performance";
        public override string FeatureDescription =>
            "Smoother frame pacing, battery saving and the optimizations in the sections below. Off hands frame limiting back to the game.";

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
        private ConfigEntry<float> batteryRenderScale;
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

        private static bool IsMac => Application.platform == RuntimePlatform.OSXPlayer;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            BindSettings(plugin.Config);
            RemoveOldSettings(plugin.Config);
            plugin.Config.ConfigReloaded += (_, _) => stats.Reset();

            if (IsMac)
            {
                TryLog("display link", () => DisplayLink.Start() ? "running" : "unavailable");
                PowerState.Refresh();
                Log.For<PerformanceManager>().Debug($"On battery {PowerState.OnBattery}, low power mode {PowerState.LowPowerMode}, thermal {PowerState.ThermalState}");
            }
            AstarPath.OnAwakeSettings += ConfigurePathfinding;
            if (IsEnabled) ReserveHeap(heapReserveMb.Value);
            gameGcTimeSlice = GarbageCollector.incrementalTimeSliceNanoseconds;
            if (IsEnabled) ApplyGcTimeSlice();
            gcTimeSliceMs.SettingChanged += (_, _) => ApplyGcTimeSlice();

            pacer.Install();
            if (IsEnabled && spikeLogging.Value) loopTimer.Install(); // after the pacer, so its wait shows up as its own entry
            lastGcCount = GC.CollectionCount(0);
            Log.For<PerformanceManager>().Debug($"GC incremental {GarbageCollector.isIncremental}, heap {Profiler.GetMonoHeapSizeLong() / 1048576} MB, " +
                $"run in background {Application.runInBackground}, arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        }

        private void BindSettings(ConfigFile config)
        {
            precisePacing = config.Bind(FrameRateSection, "Precise frame pacing", Toggle.Auto,
                "Pace frames with the mod's own precise timer instead of VSync and Unity's frame limiter, which both " +
                "oversleep by several frames on macOS and make the game stutter. Auto: on for macOS.");
            maxFrameRate = config.Bind(FrameRateSection, "Max frame rate", 0, new ConfigDescription(
                "Frame rate cap while playing (at least 15). 0 matches the display's refresh rate.", new AcceptableValueRange<int>(0, 240), new SettingTags { ZeroLabel = "Display", Unit = " fps", Step = 5 }));
            raiseRefreshRate = config.Bind(FrameRateSection, "Raise refresh rate", 120, new ConfigDescription(
                "macOS: while the game runs, switch the display to the highest refresh rate up to this, e.g. 120 on a " +
                "ProMotion screen set to 60 Hz. macOS restores your setting when the game quits. 0 leaves it alone. " +
                "Takes effect at launch.", new AcceptableValueList<int>(0, 90, 100, 120, 144), new SettingTags { ZeroLabel = "Off", Unit = " Hz" }));
            menuFrameRate = config.Bind(FrameRateSection, "Menu frame rate", 60, new ConfigDescription(
                "Frame rate cap on the main menu and while paused. 0 for no extra cap.", new AcceptableValueRange<int>(0, 240), new SettingTags { ZeroLabel = "Off", Unit = " fps", Step = 5 }));
            lockToDisplay = config.Bind(FrameRateSection, "Lock to display refresh", true,
                "Start each frame just after a display refresh so frames don't drift across refreshes, which looks like judder.");

            backgroundFrameRate = config.Bind(BatterySection, "Background frame rate", 15, new ConfigDescription(
                "Frame rate cap while the game window isn't focused (if the game keeps running in the background). " +
                "0 for no extra cap.", new AcceptableValueRange<int>(0, 240), new SettingTags { ZeroLabel = "Off", Unit = " fps", Step = 5 }));
            batteryFrameRate = config.Bind(BatterySection, "Battery frame rate", 60, new ConfigDescription(
                "Frame rate cap while on battery power or in Low Power Mode. 0 for no extra cap.", new AcceptableValueRange<int>(0, 240), new SettingTags { ZeroLabel = "Off", Unit = " fps", Step = 5 }));
            saveCpuOnBattery = config.Bind(BatterySection, "Save CPU on battery", true,
                "On battery, sleep instead of busy-waiting for the exact frame time. Uses much less CPU; frame timing gets slightly less precise.");
            coolDownWhenHot = config.Bind(BatterySection, "Cool down when hot", true,
                "Cap the frame rate at 60 when macOS reports the Mac is under thermal pressure, and at 30 when critical.");

            batteryRenderScale = config.Bind(BatterySection, "Battery render scale", 0f, new ConfigDescription(
                "Render the 3D scene at this fraction of full resolution on battery or in Low Power Mode (UI stays sharp). " +
                "Saves a lot of GPU power at some sharpness. Off keeps the game's setting.", new AcceptableValueRange<float>(0f, 1f),
                new SettingTags { ZeroLabel = "Off", Unit = "x", Step = 0.05f }));
            idleFrameRate = config.Bind(BatterySection, "Idle frame rate", 30, new ConfigDescription(
                "Frame rate cap when there's been no input for a while, outside of night battles. 0 for no extra cap.",
                new AcceptableValueRange<int>(0, 240), new SettingTags { ZeroLabel = "Off", Unit = " fps", Step = 5 }));
            idleAfterSeconds = config.Bind(BatterySection, "Idle after", 60, new ConfigDescription(
                "Seconds without keyboard, mouse or controller input before the idle frame rate applies.", new AcceptableValueRange<int>(5, 600), new SettingTags { Unit = " s", Step = 5 }));

            heapReserveMb = config.Bind(MemorySection, "Minimum heap size", 512, new ConfigDescription(
                "Grow the managed heap to at least this size at startup so it doesn't have to expand (and stall) during play. " +
                "0 disables. Takes effect at launch.", new AcceptableValueRange<int>(0, 2048), new SettingTags { ZeroLabel = "Off", Unit = " MB", Step = 64 }));
            gcTimeSliceMs = config.Bind(MemorySection, "Incremental GC time slice", 0f, new ConfigDescription(
                "Per-frame budget for the incremental garbage collector. 0 keeps the game's default (3 ms).", new AcceptableValueRange<float>(0f, 10f), new SettingTags { ZeroLabel = "Default", Unit = " ms", Step = 0.5f }));

            collectWhenPaused = config.Bind(MemorySection, "Collect garbage when paused", true,
                "Run a garbage collection when the game is paused or a menu opens, where the pause can't be felt, so " +
                "fewer collections happen mid-battle.");

            spikeLogging = config.Bind(DiagnosticsSection, "Log frame spikes", true,
                "Write frame-time spikes and a periodic frame-time summary to the BepInEx log. They're debug lines, so they " +
                "only appear when \"Log level\" is Debug (or Performance debug logging is on). Takes effect at launch.");
            spikeThresholdMs = config.Bind(DiagnosticsSection, "Spike threshold", 25f, new ConfigDescription(
                "A frame counts as a spike when it takes longer than this...", new AcceptableValueRange<float>(5f, 200f), new SettingTags { Unit = " ms", Step = 5 }));
            spikeRatio = config.Bind(DiagnosticsSection, "Spike ratio", 2f, new ConfigDescription(
                "...and longer than this multiple of the recent average frame time.", new AcceptableValueRange<float>(1f, 10f), new SettingTags { Unit = "x", Step = 0.25f }));
            summaryIntervalSeconds = config.Bind(DiagnosticsSection, "Summary interval", 60f, new ConfigDescription(
                "How often to log the frame-time summary.", new AcceptableValueRange<float>(5f, 600f), new SettingTags { Unit = " s", Step = 5 }));

            latencyCritical = config.Bind(AdvancedSection, "Latency-critical mode", Toggle.Auto,
                "Ask macOS to treat the game as latency-critical while it's focused, which disables App Nap and timer " +
                "coalescing so waits don't overshoot. Auto: on for macOS.");
            maxSpinMs = config.Bind(AdvancedSection, "Max busy-wait", 4f, new ConfigDescription(
                "Longest the frame pacer busy-waits before each frame for exact timing. It adapts to how precisely this Mac " +
                "wakes from sleep and only spins as long as needed.", new AcceptableValueRange<float>(0f, 8f), new SettingTags { ZeroLabel = "Off", Unit = " ms", Step = 0.5f }));
            vSync = config.Bind(AdvancedSection, "VSync override", VSyncMode.GameDefault,
                "Override the game's VSync setting. Ignored while precise frame pacing is on.");
            targetFrameRate = config.Bind(AdvancedSection, "Unity frame limiter", 0, new ConfigDescription(
                "Frame cap used when precise frame pacing is off (at least 15); 0 for none. Applied with the mod's pacer, since " +
                "Unity's own limiter oversleeps on macOS and does nothing while VSync is on.",
                new AcceptableValueRange<int>(0, 240), new SettingTags { ZeroLabel = "Off", Unit = " fps", Step = 5 }));
            renderScale = config.Bind(AdvancedSection, "Render scale override", 0f, new ConfigDescription(
                "Resolution multiplier for 3D rendering (UI stays sharp). 0 keeps the game's setting.", new AcceptableValueRange<float>(0f, 2f), new SettingTags { ZeroLabel = "Game default", Unit = "x", Step = 0.05f }));
            msaa = config.Bind(AdvancedSection, "MSAA override", 0, new ConfigDescription(
                "Anti-aliasing samples. 0 keeps the game's setting.", new AcceptableValueList<int>(0, 1, 2, 4, 8), new SettingTags { ZeroLabel = "Game default", Unit = "x" }));
            pathfindingThreads = config.Bind(AdvancedSection, "Pathfinding threads", PathfindingThreads.GameDefault,
                "Threads A* uses to compute paths, applied when a level loads.");

            BindOptimizations(config);
        }

        // Earlier versions kept everything in one "Performance" section; BepInEx would otherwise keep those orphaned
        // entries in the file forever.
        private static void RemoveOldSettings(ConfigFile config)
        {
            var orphans = HarmonyLib.Traverse.Create(config).Property("OrphanedEntries").GetValue<Dictionary<ConfigDefinition, string>>();
            if (orphans == null) return;
            var old = orphans.Keys.Where(d => d.Section == "Performance").ToList();
            if (old.Count == 0) return;
            foreach (var definition in old) orphans.Remove(definition);
            config.Save();
            Log.For<PerformanceManager>().Info($"Removed {old.Count} settings left over from an older version");
        }

        private static void TryLog(string what, Func<string> action)
        {
            try
            {
                string result = action(); // always run it; only the log line depends on the level
                Log.For<PerformanceManager>().Debug($"{what} {result}");
            }
            catch (Exception e)
            {
                Log.For<PerformanceManager>().Warn($"{what} failed: {e.Message}");
            }
        }

        public override void OnDestroy()
        {
            AstarPath.OnAwakeSettings -= ConfigurePathfinding;
            if (MacActivity.Active) MacActivity.End();
        }

        public override void Update()
        {
            UpdateFrameRate();
            UpdateDiagnostics();
        }

        // Hand frame limiting, VSync and render scale back to the game. A raised refresh rate stays until quit.
        protected override void OnFeatureDisabled()
        {
            pacer.TargetFps = 0;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = SettingsManager.Instance != null && !SettingsManager.Instance.VSync ? 0 : 1;
            RestoreRenderScale();
            RestoreMsaa();
            if (GarbageCollector.isIncremental) GarbageCollector.incrementalTimeSliceNanoseconds = gameGcTimeSlice;
            if (MacActivity.Active) MacActivity.End();
            stats.Reset();
        }

        protected override void OnFeatureEnabled() => ApplyGcTimeSlice();

        // Runs inside AstarPath.OnEnable, just before its path threads start.
        private void ConfigurePathfinding()
        {
            var astar = AstarPath.active;
            if (astar == null || !IsEnabled) return;
            var original = astar.threadCount;
            if (pathfindingThreads.Value != PathfindingThreads.GameDefault)
            {
                astar.threadCount = (ThreadCount)Enum.Parse(typeof(ThreadCount), pathfindingThreads.Value.ToString());
            }
            Log.For<PerformanceManager>().Debug($"A* threads {original}{(astar.threadCount != original ? $" -> {astar.threadCount}" : "")}, " +
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
            Log.For<PerformanceManager>().Debug($"Grew managed heap {before / 1048576} MB -> {Profiler.GetMonoHeapSizeLong() / 1048576} MB");
        }

        private ulong gameGcTimeSlice;

        private void ApplyGcTimeSlice()
        {
            if (!IsEnabled || !GarbageCollector.isIncremental) return;
            GarbageCollector.incrementalTimeSliceNanoseconds = gcTimeSliceMs.Value > 0f ? (ulong)(gcTimeSliceMs.Value * 1e6f) : gameGcTimeSlice;
        }
    }
}
