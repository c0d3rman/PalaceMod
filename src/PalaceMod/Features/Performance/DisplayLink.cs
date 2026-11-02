using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/*
    Reads the main display's refresh timing from CoreVideo's CVDisplayLink, converted to Stopwatch ticks, so the frame
    pacer can line frames up with refreshes instead of only matching their rate.
*/
namespace PalaceMod
{
    internal static class DisplayLink
    {
        private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";

        [StructLayout(LayoutKind.Sequential)]
        private struct Timebase { public uint Numer, Denom; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OutputCallback(IntPtr link, IntPtr now, IntPtr outputTime, ulong flagsIn, IntPtr flagsOut, IntPtr context);

        [DllImport(CoreVideo)] private static extern int CVDisplayLinkCreateWithCGDisplay(uint display, out IntPtr link);
        [DllImport(CoreVideo)] private static extern int CVDisplayLinkSetOutputCallback(IntPtr link, OutputCallback callback, IntPtr context);
        [DllImport(CoreVideo)] private static extern int CVDisplayLinkStart(IntPtr link);
        [DllImport(CoreVideo)] private static extern double CVDisplayLinkGetActualOutputVideoRefreshPeriod(IntPtr link);
        [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
        [DllImport(LibSystem)] private static extern ulong mach_absolute_time();
        [DllImport(LibSystem)] private static extern int mach_timebase_info(out Timebase timebase);

        private static IntPtr link;
        private static OutputCallback callback; // kept alive for the native side
        private static double machToStopwatch;
        private static long lastRefresh;
        private static long periodTicks;

        /// <summary>Stopwatch timestamp of the most recent display refresh, or 0 before the first one.</summary>
        public static long LastRefresh => Interlocked.Read(ref lastRefresh);

        /// <summary>Refresh period in Stopwatch ticks, or 0 if unknown.</summary>
        public static long PeriodTicks => Interlocked.Read(ref periodTicks);

        public static bool Start()
        {
            if (link != IntPtr.Zero) return true;
            mach_timebase_info(out var timebase);
            machToStopwatch = (double)timebase.Numer / timebase.Denom * Stopwatch.Frequency / 1e9;
            if (CVDisplayLinkCreateWithCGDisplay(CGMainDisplayID(), out link) != 0 || link == IntPtr.Zero) return false;
            callback = OnRefresh;
            CVDisplayLinkSetOutputCallback(link, callback, IntPtr.Zero);
            return CVDisplayLinkStart(link) == 0;
        }

        // Runs on CoreVideo's display link thread once per refresh.
        private static unsafe int OnRefresh(IntPtr link, IntPtr now, IntPtr outputTime, ulong flagsIn, IntPtr flagsOut, IntPtr context)
        {
            // CVTimeStamp: uint32 version, int32 videoTimeScale, int64 videoTime, uint64 hostTime, double rateScalar, int64 videoRefreshPeriod, ...
            ulong hostTime = *(ulong*)((byte*)now + 16);
            long stopwatchNow = Stopwatch.GetTimestamp();
            ulong machNow = mach_absolute_time();
            long refresh = stopwatchNow - (long)((machNow - hostTime) * machToStopwatch);
            Interlocked.Exchange(ref lastRefresh, refresh);

            // The nominal period from the timestamp is stable; the "actual" period is a jittery measurement.
            int scale = *(int*)((byte*)now + 4);
            long refreshPeriod = *(long*)((byte*)now + 32);
            double period = scale > 0 && refreshPeriod > 0 ? (double)refreshPeriod / scale : CVDisplayLinkGetActualOutputVideoRefreshPeriod(link);
            if (period > 0) Interlocked.Exchange(ref periodTicks, (long)(period * Stopwatch.Frequency));
            return 0;
        }
    }
}
