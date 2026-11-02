using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace PalaceMod
{
    public partial class PerformanceManager
    {
        private const int MaxSpikeLinesPerWindow = 20;

        private readonly FrameStats stats = new();
        private readonly PlayerLoopTimer loopTimer = new();
        private int lastGcCount;
        private float smoothedFrameMs = 16.7f;
        private float nextSummary;
        private int spikesLoggedThisWindow;

        private void UpdateDiagnostics()
        {
            if (!spikeLogging.Value) return;

            float frameMs = Time.unscaledDeltaTime * 1000f;
            int gcCount = GC.CollectionCount(0);
            bool gcRan = gcCount != lastGcCount;
            lastGcCount = gcCount;

            // Frames slowed down on purpose (menus, background, battery) aren't spikes.
            float expectedMs = pacer.Enabled ? 1000f / pacer.TargetFps : smoothedFrameMs;
            bool spike = frameMs > spikeThresholdMs.Value && frameMs > Math.Max(smoothedFrameMs, expectedMs) * spikeRatio.Value;
            stats.Add(frameMs, spike, gcRan, pacer.Enabled ? (float)Math.Max(0, frameMs - pacer.LastWaitMs) : frameMs);
            // Don't let spikes drag the baseline up.
            if (!spike) smoothedFrameMs = Mathf.Lerp(smoothedFrameMs, frameMs, 0.05f);

            if (spike && spikesLoggedThisWindow < MaxSpikeLinesPerWindow)
            {
                spikesLoggedThisWindow++;
                Log.For<PerformanceManager>().Debug($"Spike {frameMs:0.0} ms (baseline {smoothedFrameMs:0.0} ms){(gcRan ? " during GC" : "")} " +
                    $"in {SceneManager.GetActiveScene().name}, {DescribePhase()}, mono used {Profiler.GetMonoUsedSizeLong() / 1048576} MB; " +
                    $"slowest: {loopTimer.DescribeLastFrame()}");
            }

            if (Time.unscaledTime < nextSummary) return;
            if (stats.Frames > 0)
            {
                Log.For<PerformanceManager>().Debug($"{stats.Summarize()}, " +
                    $"paced {(pacer.Enabled ? $"{pacer.TargetFps:0.#} fps ({FrameRateReason}){(pacer.LockedToDisplay ? " locked to display" : "")}, " +
                        $"spin margin {pacer.MarginMs:0.0} ms, latest start {pacer.WorstSleepMs:0.0} ms late, mistimed {pacer.MistimedFrames}/{pacer.Frames}" : $"off, vSync {QualitySettings.vSyncCount}, limiter {Application.targetFrameRate}")}, " +
                    $"display {DisplayRefreshRate():0.#} Hz, focused {Application.isFocused}, battery {PowerState.OnBattery}, " +
                    $"low power {PowerState.LowPowerMode}, thermal {PowerState.ThermalState}, {DescribeRendering()}, " +
                    $"mono used {Profiler.GetMonoUsedSizeLong() / 1048576}/{Profiler.GetMonoHeapSizeLong() / 1048576} MB");
            }
            stats.Reset();
            pacer.ResetStats();
            spikesLoggedThisWindow = 0;
            nextSummary = Time.unscaledTime + Mathf.Max(5f, summaryIntervalSeconds.Value);
        }

        private static string DescribePhase()
        {
            var cycle = DayNightCycle.Instance;
            return cycle == null ? "no level" : cycle.CurrentTimestate.ToString().ToLowerInvariant();
        }

        private static string DescribeRendering()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return "no render pipeline";
            var type = pipeline.GetType();
            return $"render scale {type.GetProperty("renderScale")?.GetValue(pipeline)}, MSAA {type.GetProperty("msaaSampleCount")?.GetValue(pipeline)}, " +
                $"{Screen.width}x{Screen.height}";
        }

        private class FrameStats
        {
            private readonly List<float> frameTimes = new(4096);
            private readonly List<float> workTimes = new(4096);
            private int spikes;
            private int gcSpikes;
            private int gcFrames;

            public int Frames => frameTimes.Count;

            public void Add(float frameMs, bool spike, bool gcRan, float workMs)
            {
                frameTimes.Add(frameMs);
                workTimes.Add(workMs);
                if (gcRan) gcFrames++;
                if (spike) spikes++;
                if (spike && gcRan) gcSpikes++;
            }

            public string Summarize()
            {
                float total = 0f;
                foreach (float t in frameTimes) total += t;
                frameTimes.Sort();
                float work = 0f;
                foreach (float t in workTimes) work += t;
                workTimes.Sort();
                float Percentile(List<float> list, float p) => list[Mathf.Clamp((int)(p * (list.Count - 1)), 0, list.Count - 1)];
                return $"{frameTimes.Count} frames, avg {1000f * frameTimes.Count / total:0} fps, median {Percentile(frameTimes, 0.5f):0.0} ms, " +
                    $"p99 {Percentile(frameTimes, 0.99f):0.0} ms, worst {frameTimes[frameTimes.Count - 1]:0.0} ms, " +
                    $"work avg {work / workTimes.Count:0.00} ms p95 {Percentile(workTimes, 0.95f):0.00} ms, " +
                    $"{spikes} spikes ({gcSpikes} with GC), {gcFrames} GCs";
            }

            public void Reset()
            {
                frameTimes.Clear();
                workTimes.Clear();
                spikes = gcSpikes = gcFrames = 0;
            }
        }
    }
}
