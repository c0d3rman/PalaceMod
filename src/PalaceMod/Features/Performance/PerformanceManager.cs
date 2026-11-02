using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

/*
    Smooths out frame-time spikes and reports where the remaining ones come from.
    - Pre-grows the Mono (Boehm) heap at startup so the game doesn't stall on heap expansions mid-level.
    - Optionally widens the incremental GC time slice so collections finish before allocation forces a full stop-the-world pass.
    - Logs frame spikes with whether a GC ran during that frame, plus a periodic summary, so spikes can be attributed.
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

        private readonly FrameStats stats = new();
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

            ReserveHeap(heapReserveMb.Value);
            ApplyGcTimeSlice();
            gcTimeSliceMs.SettingChanged += (_, _) => ApplyGcTimeSlice();

            lastGcCount = GC.CollectionCount(0);
            Plugin.Logger.LogInfo($"Performance: GC mode {GarbageCollector.GCMode}, incremental {GarbageCollector.isIncremental}, " +
                $"time slice {GarbageCollector.incrementalTimeSliceNanoseconds / 1e6:0.#} ms, heap {Profiler.GetMonoHeapSizeLong() / 1048576} MB, " +
                $"vSync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}, arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
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
                    $"in {SceneManager.GetActiveScene().name}, {DescribePhase()}, mono used {Profiler.GetMonoUsedSizeLong() / 1048576} MB");
            }

            if (Time.unscaledTime >= nextSummary)
            {
                if (stats.Frames > 0)
                {
                    Plugin.Logger.LogInfo($"Performance: {stats.Summarize()}, mono used {Profiler.GetMonoUsedSizeLong() / 1048576}/{Profiler.GetMonoHeapSizeLong() / 1048576} MB");
                }
                stats.Reset();
                spikesLoggedThisWindow = 0;
                nextSummary = Time.unscaledTime + Mathf.Max(5f, summaryIntervalSeconds.Value);
            }
        }

        private static string DescribePhase()
        {
            var cycle = DayNightCycle.Instance;
            return cycle == null ? "no level" : cycle.CurrentTimestate.ToString().ToLowerInvariant();
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
