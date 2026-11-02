using System;
using System.Runtime.InteropServices;

/*
    Tells macOS the game is latency-critical user activity, which turns off App Nap and timer coalescing for the
    process. Without it, sleeps and waits in the game (including Unity's VSync wait) can overshoot by tens of ms.
*/
namespace PalaceMod
{
    internal static class MacActivity
    {
        private const string LibObjC = "/usr/lib/libobjc.A.dylib";
        // NSActivityUserInitiated | NSActivityLatencyCritical
        private const ulong Options = 0x00FFFFFFUL | 0xFF00000000UL;

        [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, string utf8);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);

        // Held for the life of the process; the activity ends when this token is released.
        private static IntPtr token;

        public static bool Begin()
        {
            if (token != IntPtr.Zero) return true;
            IntPtr processInfo = Send(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            IntPtr reason = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "Smooth frame pacing (PalaceMod)");
            IntPtr activity = Send(processInfo, sel_registerName("beginActivityWithOptions:reason:"), Options, reason);
            if (activity == IntPtr.Zero) return false;
            token = Send(activity, sel_registerName("retain"));
            return true;
        }
    }
}
