using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

/*
    Smooths out frame-time spikes and reports where the remaining ones come from.
    - Pre-grows the Mono (Boehm) heap at startup so the game doesn't stall on heap expansions mid-level.
    - Optionally widens the incremental GC time slice so collections finish before allocation forces a full stop-the-world pass.
    - Logs frame spikes with whether a GC ran during that frame and which engine subsystems took the time, plus a
      periodic summary, so spikes can be attributed.
    - Removes per-frame garbage in a few game hot paths, with results identical to the originals.
    - Logs (and optionally overrides) the A* pathfinding thread settings for each level.
    - On macOS, paces frames precisely and opts out of App Nap/timer coalescing; both VSync and Unity's frame limiter
      otherwise oversleep by several frames there.
*/
namespace PalaceMod
{
    public class PerformanceManager : BaseManager
    {
        private const string Section = "Performance";

        private ConfigEntry<int> heapReserveMb;
        private ConfigEntry<float> gcTimeSliceMs;
        private ConfigEntry<bool> spikeLogging;
        private ConfigEntry<float> spikeThresholdMs;
        private ConfigEntry<float> summaryIntervalSeconds;
        private ConfigEntry<PathfindingThreads> pathfindingThreads;
        private ConfigEntry<VSyncMode> vSync;
        private ConfigEntry<int> targetFrameRate;
        private DateTime configWriteTime;
        private float nextConfigCheck;

        private ConfigEntry<Toggle> precisePacing;
        private ConfigEntry<int> pacedFrameRate;
        private ConfigEntry<bool> lockToDisplay;
        private readonly FramePacer pacer = new();
        private ConfigEntry<float> renderScale;
        private ConfigEntry<int> msaa;

        public enum VSyncMode { GameDefault, On, Off }
        public enum Toggle { Auto, On, Off }

        public enum PathfindingThreads { GameDefault, None, One, Two, Four, AutomaticLowLoad, AutomaticHighLoad }

        private readonly FrameStats stats = new();
        private readonly PlayerLoopTimer loopTimer = new();
        private int lastGcCount;
        private float smoothedFrameMs = 16.7f;
        private float nextSummary;
        private int spikesLoggedThisWindow;
        private const int MaxSpikeLinesPerWindow = 20;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var config = plugin.Config;
            heapReserveMb = config.Bind(Section, "Minimum heap size (MB)", 512,
                "Grow the managed heap to at least this size at startup so it doesn't have to expand (and stall) during play. 0 disables.");
            gcTimeSliceMs = config.Bind(Section, "Incremental GC time slice (ms)", 0f,
                "Per-frame budget for the incremental garbage collector. 0 keeps the game's default (3 ms).");
            spikeLogging = config.Bind(Section, "Log frame spikes", true,
                "Write frame-time spikes and a periodic frame-time summary to the BepInEx log.");
            spikeThresholdMs = config.Bind(Section, "Spike threshold (ms)", 25f,
                "A frame counts as a spike when it takes longer than this and more than twice the recent average.");
            summaryIntervalSeconds = config.Bind(Section, "Summary interval (s)", 60f,
                "How often to log the frame-time summary.");

            pathfindingThreads = config.Bind(Section, "Pathfinding threads", PathfindingThreads.GameDefault,
                "Threads A* uses to compute paths, applied when a level loads. None computes paths on the main thread.");

            vSync = config.Bind(Section, "VSync", VSyncMode.GameDefault,
                "Override the game's VSync setting. Off plus a target frame rate often paces better on Mac displays.");
            targetFrameRate = config.Bind(Section, "Target frame rate", 0,
                "Frame rate cap used when VSync is off. 0 keeps the game's setting, -1 is uncapped.");
            precisePacing = config.Bind(Section, "Precise frame pacing", Toggle.Auto,
                "Replace VSync and Unity's frame limiter with a precise one. Both oversleep by several frames on macOS, " +
                "which makes the game stutter even though it renders in about a millisecond. Auto turns it on on macOS. " +
                "Overrides the VSync and Target frame rate settings.");
            pacedFrameRate = config.Bind(Section, "Paced frame rate", 0,
                "Frame rate for precise frame pacing. 0 matches the display's refresh rate.");
            lockToDisplay = config.Bind(Section, "Lock pacing to display refresh", true,
                "Start each paced frame just after a display refresh, so frames don't drift across refreshes (judder).");
            renderScale = config.Bind(Section, "Render scale override", 0f,
                "Resolution multiplier for 3D rendering (UI stays sharp). 0 keeps the game's setting.");
            msaa = config.Bind(Section, "MSAA override", 0,
                "Anti-aliasing samples: 1 (off), 2, 4 or 8. 0 keeps the game's setting.");
            var latencyCritical = config.Bind(Section, "Latency-critical mode", Toggle.Auto,
                "Ask macOS to treat the game as latency-critical, which disables App Nap and timer coalescing so waits " +
                "don't overshoot. Auto turns it on on macOS. Takes effect at launch.");
            configWriteTime = System.IO.File.GetLastWriteTimeUtc(config.ConfigFilePath);
            if (latencyCritical.Value == Toggle.On || (latencyCritical.Value == Toggle.Auto && Application.platform == RuntimePlatform.OSXPlayer))
            {
                try
                {
                    Plugin.Logger.LogInfo($"Performance: latency-critical mode {(MacActivity.Begin() ? "on" : "unavailable")}");
                    Plugin.Logger.LogInfo($"Performance: display link {(DisplayLink.Start() ? "running" : "unavailable")}");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"Performance: couldn't enable latency-critical mode: {e.Message}");
                }
            }

            AstarPath.OnAwakeSettings += ConfigurePathfinding;
            ReserveHeap(heapReserveMb.Value);
            ApplyGcTimeSlice();
            gcTimeSliceMs.SettingChanged += (_, _) => ApplyGcTimeSlice();

            lastGcCount = GC.CollectionCount(0);
            pacer.Install();
            if (spikeLogging.Value) loopTimer.Install(); // after the pacer, so its wait shows up as its own entry
            Plugin.Logger.LogInfo($"Performance: GC mode {GarbageCollector.GCMode}, incremental {GarbageCollector.isIncremental}, " +
                $"time slice {GarbageCollector.incrementalTimeSliceNanoseconds / 1e6:0.#} ms, heap {Profiler.GetMonoHeapSizeLong() / 1048576} MB, " +
                $"vSync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}, arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        }

        public override void OnDestroy()
        {
            AstarPath.OnAwakeSettings -= ConfigurePathfinding;
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
            long after = Profiler.GetMonoHeapSizeLong();
            Plugin.Logger.LogInfo($"Performance: grew managed heap {before / 1048576} MB -> {after / 1048576} MB");
        }

        private void ApplyGcTimeSlice()
        {
            if (gcTimeSliceMs.Value <= 0f || !GarbageCollector.isIncremental) return;
            GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(gcTimeSliceMs.Value * 1e6f);
        }

        public override void Update()
        {
            ReloadConfigIfChanged();
            ApplyFramePacing();
            if (!spikeLogging.Value) return;

            float frameMs = Time.unscaledDeltaTime * 1000f;
            int gcCount = GC.CollectionCount(0);
            bool gcRan = gcCount != lastGcCount;
            lastGcCount = gcCount;

            bool spike = frameMs > spikeThresholdMs.Value && frameMs > smoothedFrameMs * 2f;
            stats.Add(frameMs, spike, gcRan);
            // Don't let spikes drag the baseline up.
            if (!spike) smoothedFrameMs = Mathf.Lerp(smoothedFrameMs, frameMs, 0.05f);

            if (spike && spikesLoggedThisWindow < MaxSpikeLinesPerWindow)
            {
                spikesLoggedThisWindow++;
                Plugin.Logger.LogInfo($"Performance: spike {frameMs:0.0} ms (baseline {smoothedFrameMs:0.0} ms){(gcRan ? " during GC" : "")} " +
                    $"in {SceneManager.GetActiveScene().name}, {DescribePhase()}, mono used {Profiler.GetMonoUsedSizeLong() / 1048576} MB; " +
                    $"slowest: {loopTimer.DescribeLastFrame()}");
            }

            if (Time.unscaledTime >= nextSummary)
            {
                if (stats.Frames > 0)
                {
                    Plugin.Logger.LogInfo($"Performance: {stats.Summarize()}, vSync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}, " +
                        $"paced {(pacer.Enabled ? $"{pacer.TargetFps:0.#} fps{(pacer.LockedToDisplay ? " locked to display" : "")}, worst sleep {pacer.WorstSleepMs:0.0} ms, " +
                            $"mistimed {pacer.MistimedFrames}/{pacer.Frames} frames" : "off")}, " +
                        $"refresh period {DisplayLink.PeriodTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms, " +
                        $"display {Screen.currentResolution.refreshRateRatio.value:0.#} Hz, focused {Application.isFocused}, {DescribeRendering()}, mono used {Profiler.GetMonoUsedSizeLong() / 1048576}/{Profiler.GetMonoHeapSizeLong() / 1048576} MB");
                }
                stats.Reset();
                pacer.ResetStats();
                spikesLoggedThisWindow = 0;
                nextSummary = Time.unscaledTime + Mathf.Max(5f, summaryIntervalSeconds.Value);
            }
        }

        // Lets settings be tuned while the game runs by editing the config file.
        private void ReloadConfigIfChanged()
        {
            if (Time.unscaledTime < nextConfigCheck) return;
            nextConfigCheck = Time.unscaledTime + 2f;
            var path = Plugin.Config.ConfigFilePath;
            var written = System.IO.File.GetLastWriteTimeUtc(path);
            if (written == configWriteTime) return;
            configWriteTime = written;
            Plugin.Config.Reload();
            Plugin.Logger.LogInfo($"Performance: reloaded config (VSync {vSync.Value}, target frame rate {targetFrameRate.Value}, precise pacing {precisePacing.Value} {pacedFrameRate.Value})");
            stats.Reset();
        }

        // The game applies its own VSync setting when settings load, so re-assert ours whenever it differs.
        // The game's URP asset isn't referenced at compile time, so set its properties by name.
        private void ApplyRenderOverrides()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return;
            var type = pipeline.GetType();
            if (renderScale.Value > 0f) SetIfDifferent(type.GetProperty("renderScale"), pipeline, renderScale.Value);
            if (msaa.Value > 0) SetIfDifferent(type.GetProperty("msaaSampleCount"), pipeline, msaa.Value);
        }

        private static void SetIfDifferent(System.Reflection.PropertyInfo property, object target, object value)
        {
            if (property == null || Equals(property.GetValue(target), value)) return;
            property.SetValue(target, value);
        }

        private string DescribeRendering()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return "no render pipeline";
            var type = pipeline.GetType();
            return $"render scale {type.GetProperty("renderScale")?.GetValue(pipeline)}, MSAA {type.GetProperty("msaaSampleCount")?.GetValue(pipeline)}, " +
                $"{Screen.width}x{Screen.height}";
        }

        private void ApplyFramePacing()
        {
            ApplyRenderOverrides();
            bool precise = precisePacing.Value == Toggle.On ||
                (precisePacing.Value == Toggle.Auto && Application.platform == RuntimePlatform.OSXPlayer);
            if (precise)
            {
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                long period = DisplayLink.PeriodTicks;
                double refresh = period > 0 ? System.Diagnostics.Stopwatch.Frequency / (double)period : Screen.currentResolution.refreshRateRatio.value;
                pacer.LockToDisplay = lockToDisplay.Value;
                pacer.TargetFps = pacedFrameRate.Value > 0 ? pacedFrameRate.Value : (float)(refresh > 1 ? refresh : 60);
                return;
            }
            pacer.TargetFps = 0;
            if (vSync.Value != VSyncMode.GameDefault)
            {
                int count = vSync.Value == VSyncMode.On ? 1 : 0;
                if (QualitySettings.vSyncCount != count) QualitySettings.vSyncCount = count;
            }
            if (targetFrameRate.Value != 0 && Application.targetFrameRate != targetFrameRate.Value)
            {
                Application.targetFrameRate = targetFrameRate.Value;
            }
        }

        private static string DescribePhase()
        {
            var cycle = DayNightCycle.Instance;
            return cycle == null ? "no level" : cycle.CurrentTimestate.ToString().ToLowerInvariant();
        }

        // TagManager.PlayerUnits/EnemyUnits/Players wrap their list in a new ReadOnlyCollection on every access.
        // A ReadOnlyCollection is a live view, so handing out one per list is equivalent.
        private static class ReadOnlyViews
        {
            private static readonly ConditionalWeakTable<List<TaggedObject>, ReadOnlyCollection<TaggedObject>> Views = new();
            private static readonly ConditionalWeakTable<List<TaggedObject>, ReadOnlyCollection<TaggedObject>>.CreateValueCallback Create = list => list.AsReadOnly();

            public static IReadOnlyList<TaggedObject> For(List<TaggedObject> list) => list == null ? null : Views.GetValue(list, Create);
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.PlayerUnits), MethodType.Getter)]
        private class TagManager_PlayerUnits_Patch
        {
            private static bool Prefix(List<TaggedObject> ___bufferedPlayerUnits, ref IReadOnlyList<TaggedObject> __result)
            {
                __result = ReadOnlyViews.For(___bufferedPlayerUnits);
                return false;
            }
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.EnemyUnits), MethodType.Getter)]
        private class TagManager_EnemyUnits_Patch
        {
            private static bool Prefix(List<TaggedObject> ___bufferedEnemyUnits, ref IReadOnlyList<TaggedObject> __result)
            {
                __result = ReadOnlyViews.For(___bufferedEnemyUnits);
                return false;
            }
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.Players), MethodType.Getter)]
        private class TagManager_Players_Patch
        {
            private static bool Prefix(List<TaggedObject> ___bufferedPlayers, ref IReadOnlyList<TaggedObject> __result)
            {
                __result = ReadOnlyViews.For(___bufferedPlayers);
                return false;
            }
        }

        // GPUInstancingManager.Update allocates a new Plane[6] every frame; fill one reused array instead.
        [HarmonyPatch(typeof(GPUInstancingManager), "Update")]
        private class GPUInstancingManager_Update_Patch
        {
            private static readonly Plane[] Planes = new Plane[6];

            public static Plane[] CalculateFrustumPlanes(Camera camera)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, Planes);
                return Planes;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools.Method(typeof(GeometryUtility), nameof(GeometryUtility.CalculateFrustumPlanes), [typeof(Camera)]);
                var replacement = AccessTools.Method(typeof(GPUInstancingManager_Update_Patch), nameof(CalculateFrustumPlanes));
                bool replaced = false;
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        instruction.operand = replacement;
                        replaced = true;
                    }
                    yield return instruction;
                }
                if (!replaced) Plugin.Logger.LogWarning("Performance: GPUInstancingManager.Update no longer calls CalculateFrustumPlanes; patch skipped");
            }
        }

        // PowerTowerPerk.Update calls GetComponentsInChildren twice per frame at night. Look the components up again
        // whenever the game refreshes its closest tower (every 0.2 s) instead. Towers can't be rebuilt at night.
        [HarmonyPatch(typeof(PowerTowerPerk), "Update")]
        private class PowerTowerPerk_Update_Patch
        {
            private class TowerParts
            {
                public TaggedObject Tower;
                public AutoAttack[] Attacks;
                public HotOilTower[] HotOil;
            }

            private static readonly ConditionalWeakTable<PowerTowerPerk, TowerParts> Cache = new();

            private static bool Prefix(PowerTowerPerk __instance, TagManager ___tagManager, float ___attackSpeedBonus, ref float ___nextRefreshIn,
                ref TaggedObject ___closestTower, List<TagManager.ETag> ___mustHaveTags, List<TagManager.ETag> ___mayNotHaveTags)
            {
                if (DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day) return false;
                var parts = Cache.GetOrCreateValue(__instance);
                ___nextRefreshIn -= Time.deltaTime;
                if (___nextRefreshIn <= 0f)
                {
                    ___closestTower = ___tagManager.FindClosestTaggedObjectWithTags(__instance.transform.position, ___mustHaveTags, ___mayNotHaveTags);
                    ___nextRefreshIn = 0.2f;
                    parts.Tower = null; // re-read the components below
                }
                if (___closestTower == null) return false;
                if (!ReferenceEquals(parts.Tower, ___closestTower))
                {
                    parts.Tower = ___closestTower;
                    parts.Attacks = ___closestTower.GetComponentsInChildren<AutoAttack>();
                    parts.HotOil = ___closestTower.GetComponentsInChildren<HotOilTower>();
                }
                float reduction = ___attackSpeedBonus * Time.deltaTime;
                foreach (var attack in parts.Attacks) attack.ReduceCooldownBy(reduction);
                foreach (var oil in parts.HotOil) oil.ReduceCooldownBy(reduction);
                return false;
            }
        }

        private class FrameStats
        {
            private readonly List<float> frameTimes = new(4096);
            private int spikes;
            private int gcSpikes;
            private int gcFrames;

            public int Frames => frameTimes.Count;

            public void Add(float frameMs, bool spike, bool gcRan)
            {
                frameTimes.Add(frameMs);
                if (gcRan) gcFrames++;
                if (spike) spikes++;
                if (spike && gcRan) gcSpikes++;
            }

            public string Summarize()
            {
                float total = 0f;
                foreach (float t in frameTimes) total += t;
                frameTimes.Sort();
                float Percentile(float p) => frameTimes[Mathf.Clamp((int)(p * (frameTimes.Count - 1)), 0, frameTimes.Count - 1)];
                return $"{frameTimes.Count} frames, avg {1000f * frameTimes.Count / total:0} fps, median {Percentile(0.5f):0.0} ms, " +
                    $"p99 {Percentile(0.99f):0.0} ms, worst {frameTimes[frameTimes.Count - 1]:0.0} ms, " +
                    $"{spikes} spikes ({gcSpikes} with GC), {gcFrames} GCs";
            }

            public void Reset()
            {
                frameTimes.Clear();
                spikes = gcSpikes = gcFrames = 0;
            }
        }
    }
}
