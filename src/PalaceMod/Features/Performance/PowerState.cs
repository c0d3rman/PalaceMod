using System;
using System.Runtime.InteropServices;
using System.Text;

/*
    macOS power and thermal state: whether the Mac is running on battery, whether Low Power Mode is on, and
    the system thermal state. Used to lower the frame rate when that saves battery or heat.
*/
namespace PalaceMod
{
    internal static class PowerState
    {
        public enum Thermal { Nominal = 0, Fair = 1, Serious = 2, Critical = 3 }

        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string LibObjC = "/usr/lib/libobjc.A.dylib";
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        [DllImport(IOKit)] private static extern IntPtr IOPSCopyPowerSourcesInfo();
        [DllImport(IOKit)] private static extern IntPtr IOPSGetProvidingPowerSourceType(IntPtr snapshot);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);
        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)] // CoreFoundation's Boolean is one byte
        private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, IntPtr size, uint encoding);
        [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr receiver, IntPtr selector);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr argument);
        private static bool hasLowPower, hasThermal;

        private static IntPtr processInfo;
        private static IntPtr lowPowerSelector, thermalSelector;
        private static readonly byte[] Buffer = new byte[64];

        public static bool OnBattery { get; private set; }
        public static bool LowPowerMode { get; private set; }
        public static Thermal ThermalState { get; private set; }
        public static bool Available { get; private set; }

        public static void Refresh()
        {
            try
            {
                if (processInfo == IntPtr.Zero)
                {
                    processInfo = SendPtr(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
                    lowPowerSelector = sel_registerName("isLowPowerModeEnabled");
                    thermalSelector = sel_registerName("thermalState");
                    // Sending an unknown selector raises a native exception managed code can't catch, and these
                    // properties are newer than the oldest macOS the game supports (Low Power Mode: macOS 12).
                    var respondsTo = sel_registerName("respondsToSelector:");
                    hasLowPower = SendBool(processInfo, respondsTo, lowPowerSelector) != 0;
                    hasThermal = SendBool(processInfo, respondsTo, thermalSelector) != 0;
                }
                OnBattery = ReadPowerSource() == "Battery Power";
                LowPowerMode = hasLowPower && SendBool(processInfo, lowPowerSelector) != 0;
                ThermalState = hasThermal ? (Thermal)SendLong(processInfo, thermalSelector) : Thermal.Nominal;
                Available = true;
            }
            catch (Exception)
            {
                Available = false;
            }
        }

        private static string ReadPowerSource()
        {
            IntPtr snapshot = IOPSCopyPowerSourcesInfo();
            if (snapshot == IntPtr.Zero) return null;
            try
            {
                IntPtr type = IOPSGetProvidingPowerSourceType(snapshot); // not owned; lives as long as the snapshot
                if (type == IntPtr.Zero || !CFStringGetCString(type, Buffer, (IntPtr)Buffer.Length, kCFStringEncodingUTF8)) return null;
                int length = Array.IndexOf(Buffer, (byte)0);
                return Encoding.UTF8.GetString(Buffer, 0, length < 0 ? Buffer.Length : length);
            }
            finally
            {
                CFRelease(snapshot);
            }
        }
    }
}
