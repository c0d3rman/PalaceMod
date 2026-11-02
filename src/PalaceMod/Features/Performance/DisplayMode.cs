using System;
using System.Runtime.InteropServices;

/*
    Switches the main display to a higher refresh rate at the same resolution, for this process only
    (kCGConfigureForAppOnly): macOS restores the user's mode when the game exits, even if it crashes.
*/
namespace PalaceMod
{
    internal static class DisplayMode
    {
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ConfigureForAppOnly = 0;

        [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
        [DllImport(CoreGraphics)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
        [DllImport(CoreGraphics)] private static extern IntPtr CGDisplayCopyAllDisplayModes(uint display, IntPtr options);
        [DllImport(CoreGraphics)] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern UIntPtr CGDisplayModeGetWidth(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern UIntPtr CGDisplayModeGetHeight(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern UIntPtr CGDisplayModeGetPixelWidth(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern UIntPtr CGDisplayModeGetPixelHeight(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern int CGBeginDisplayConfiguration(out IntPtr config);
        [DllImport(CoreGraphics)] private static extern int CGConfigureDisplayWithDisplayMode(IntPtr config, uint display, IntPtr mode, IntPtr options);
        [DllImport(CoreGraphics)] private static extern int CGCompleteDisplayConfiguration(IntPtr config, int option);
        [DllImport(CoreGraphics)] private static extern int CGCancelDisplayConfiguration(IntPtr config);
        [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetCount(IntPtr array);
        [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, IntPtr index);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, IntPtr count, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        // {kCGDisplayShowDuplicateLowResolutionModes: true}, without which HiDPI ("looks like") modes aren't listed.
        private static IntPtr ModeListOptions()
        {
            IntPtr cg = dlopen(CoreGraphics, 1), cf = dlopen(CoreFoundation, 1);
            IntPtr key = Marshal.ReadIntPtr(dlsym(cg, "kCGDisplayShowDuplicateLowResolutionModes"));
            IntPtr value = Marshal.ReadIntPtr(dlsym(cf, "kCFBooleanTrue"));
            return CFDictionaryCreate(IntPtr.Zero, [key], [value], (IntPtr)1,
                dlsym(cf, "kCFTypeDictionaryKeyCallBacks"), dlsym(cf, "kCFTypeDictionaryValueCallBacks"));
        }

        /// <summary>Switch to the highest refresh rate up to maxHz at the current resolution. Returns a description.</summary>
        public static string RaiseRefreshRate(double maxHz)
        {
            uint display = CGMainDisplayID();
            IntPtr current = CGDisplayCopyDisplayMode(display);
            if (current == IntPtr.Zero) return "no display mode";
            IntPtr modes = IntPtr.Zero;
            try
            {
                double currentHz = CGDisplayModeGetRefreshRate(current);
                IntPtr options = ModeListOptions();
                modes = CGDisplayCopyAllDisplayModes(display, options);
                if (options != IntPtr.Zero) CFRelease(options);
                if (modes == IntPtr.Zero) return "couldn't list display modes";
                IntPtr best = IntPtr.Zero;
                double bestHz = currentHz;
                long count = (long)CFArrayGetCount(modes);
                for (long i = 0; i < count; i++)
                {
                    IntPtr mode = CFArrayGetValueAtIndex(modes, (IntPtr)i);
                    double hz = CGDisplayModeGetRefreshRate(mode);
                    if (hz <= bestHz + 0.5 || hz > maxHz + 0.5 || !SameSize(mode, current)) continue;
                    best = mode;
                    bestHz = hz;
                }
                if (best == IntPtr.Zero) return $"already at the best rate ({currentHz:0.#} Hz)";
                if (CGBeginDisplayConfiguration(out var config) != 0) return "couldn't begin display configuration";
                if (CGConfigureDisplayWithDisplayMode(config, display, best, IntPtr.Zero) != 0 ||
                    CGCompleteDisplayConfiguration(config, ConfigureForAppOnly) != 0)
                {
                    CGCancelDisplayConfiguration(config);
                    return "macOS refused the display mode";
                }
                return $"{currentHz:0.#} Hz -> {bestHz:0.#} Hz while the game runs";
            }
            finally
            {
                if (modes != IntPtr.Zero) CFRelease(modes);
                CFRelease(current);
            }
        }

        private static bool SameSize(IntPtr a, IntPtr b) =>
            CGDisplayModeGetWidth(a) == CGDisplayModeGetWidth(b) && CGDisplayModeGetHeight(a) == CGDisplayModeGetHeight(b) &&
            CGDisplayModeGetPixelWidth(a) == CGDisplayModeGetPixelWidth(b) && CGDisplayModeGetPixelHeight(a) == CGDisplayModeGetPixelHeight(b);
    }
}
