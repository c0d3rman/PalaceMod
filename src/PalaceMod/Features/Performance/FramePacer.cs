using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine.LowLevel;

/*
    Precise frame limiter. On macOS (at least under Rosetta) both Unity's targetFrameRate limiter and the VSync wait
    oversleep by several frames, so a game that renders in ~1 ms still stutters between 16 and 80 ms per frame.
    This runs at the very start of each frame and waits for the next deadline itself: it sleeps while more than
    SleepMarginMs remains and spins for the rest.
*/
namespace PalaceMod
{
    internal class FramePacer
    {
        private const double SleepMarginMs = 2.0;
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        private long nextDeadline;

        public float TargetFps { get; set; }
        public bool Enabled => TargetFps > 0f;

        // Accuracy of Thread.Sleep(1), for the log: worst observed duration.
        public double WorstSleepMs { get; private set; }

        public void Install()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var phases = root.subSystemList;
            var first = phases[0].subSystemList ?? [];
            var pacer = new PlayerLoopSystem { type = typeof(FramePacer), updateDelegate = Wait };
            phases[0].subSystemList = new[] { pacer }.Concat(first).ToArray();
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
        }

        private void Wait()
        {
            if (!Enabled)
            {
                nextDeadline = 0;
                return;
            }
            long period = (long)(Stopwatch.Frequency / TargetFps);
            long now = Stopwatch.GetTimestamp();
            // First frame, or we fell more than a frame behind: restart the schedule instead of rushing to catch up.
            if (nextDeadline == 0 || now - nextDeadline > period)
            {
                nextDeadline = now + period;
                return;
            }
            long sleepUntil = nextDeadline - (long)(SleepMarginMs * TicksPerMs);
            while (now < sleepUntil)
            {
                long before = now;
                Thread.Sleep(1);
                now = Stopwatch.GetTimestamp();
                double slept = (now - before) / TicksPerMs;
                if (slept > WorstSleepMs) WorstSleepMs = slept;
            }
            while (Stopwatch.GetTimestamp() < nextDeadline) Thread.SpinWait(64);
            nextDeadline += period;
        }
    }
}
