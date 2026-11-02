using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine.LowLevel;

/*
    Precise frame limiter. On macOS both Unity's targetFrameRate limiter and the VSync wait oversleep by several
    frames, so a game that renders in a few ms still stutters between 16 and 80 ms per frame.
    This runs at the very start of each frame and waits for the next deadline itself: it sleeps while more than
    SleepMarginMs remains and spins for the rest. When the display's refresh timing is known (DisplayLink), deadlines
    sit just after a refresh, so every frame is ready for the next one; otherwise frames would drift across refresh
    boundaries and motion would judder even at a steady frame rate.
*/
namespace PalaceMod
{
    internal class FramePacer
    {
        private const double SleepMarginMs = 2.0;
        // How long after a refresh to start the next frame.
        private const double PhaseOffsetMs = 0.5;
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        private long nextDeadline;
        private long lastEndRefresh = long.MinValue;

        public float TargetFps { get; set; }
        public bool Enabled => TargetFps > 0f;
        public bool LockToDisplay { get; set; } = true;
        public bool LockedToDisplay { get; private set; }

        // Diagnostics, reset by the summary.
        public double WorstSleepMs { get; private set; }
        public int Frames { get; private set; }
        public int MistimedFrames { get; private set; } // shown for more or fewer refreshes than intended

        public void ResetStats()
        {
            WorstSleepMs = 0;
            Frames = MistimedFrames = 0;
        }

        public void Install()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var phases = root.subSystemList;
            var wait = new PlayerLoopSystem { type = typeof(FramePacer), updateDelegate = Wait };
            phases[0].subSystemList = new[] { wait }.Concat(phases[0].subSystemList ?? []).ToArray();
            int last = phases.Length - 1;
            var end = new PlayerLoopSystem { type = typeof(FramePacer), updateDelegate = FrameEnd };
            phases[last].subSystemList = (phases[last].subSystemList ?? []).Concat(new[] { end }).ToArray();
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
        }

        private int RefreshesPerFrame(long period) =>
            Math.Max(1, (int)Math.Round(Stopwatch.Frequency / (double)period / TargetFps));

        private void Wait()
        {
            if (!Enabled)
            {
                nextDeadline = 0;
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long refresh = DisplayLink.LastRefresh;
            long period = DisplayLink.PeriodTicks;
            LockedToDisplay = LockToDisplay && refresh != 0 && period > 0;
            long interval = LockedToDisplay ? period * RefreshesPerFrame(period) : (long)(Stopwatch.Frequency / TargetFps);

            if (nextDeadline == 0 || now - nextDeadline > interval)
            {
                // First frame, or more than a frame behind: restart the schedule instead of rushing to catch up.
                nextDeadline = now;
            }
            else
            {
                SleepUntil(nextDeadline);
            }

            long next = nextDeadline + interval;
            if (LockedToDisplay)
            {
                // Snap to the refresh grid so small errors don't accumulate into drift.
                long offset = (long)(PhaseOffsetMs * TicksPerMs);
                long n = (long)Math.Round((next - offset - refresh) / (double)period);
                next = refresh + n * period + offset;
            }
            nextDeadline = next;
        }

        private void SleepUntil(long deadline)
        {
            long sleepUntil = deadline - (long)(SleepMarginMs * TicksPerMs);
            long now = Stopwatch.GetTimestamp();
            while (now < sleepUntil)
            {
                long before = now;
                Thread.Sleep(1);
                now = Stopwatch.GetTimestamp();
                double slept = (now - before) / TicksPerMs;
                if (slept > WorstSleepMs) WorstSleepMs = slept;
            }
            while (Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(64);
        }

        // At the end of the frame, count frames that will appear after a different number of refreshes than intended.
        private void FrameEnd()
        {
            long refresh = DisplayLink.LastRefresh;
            long period = DisplayLink.PeriodTicks;
            if (!Enabled || refresh == 0 || period <= 0) return;
            // Absolute refresh number: whole periods since the latest refresh, plus that refresh's own position on the grid.
            long index = (long)Math.Floor((Stopwatch.GetTimestamp() - refresh) / (double)period + refresh / (double)period);
            if (lastEndRefresh != long.MinValue && index - lastEndRefresh != RefreshesPerFrame(period)) MistimedFrames++;
            lastEndRefresh = index;
            Frames++;
        }
    }
}
