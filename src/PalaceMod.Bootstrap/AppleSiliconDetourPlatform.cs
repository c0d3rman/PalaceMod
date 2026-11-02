using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.Platforms;

namespace Doorstop
{
    /// <summary>
    /// MonoMod native platform for macOS on arm64. Every write to code goes through palace_jit_write in
    /// libpalacejit.dylib, which lifts the thread's JIT write protection, copies and re-protects in native code.
    /// (While protection is lifted, JIT memory isn't executable for the thread, so managed code can't do this.)
    /// </summary>
    internal sealed unsafe class AppleSiliconDetourPlatform : IDetourNativePlatform
    {
        private const uint AArch64DetourSize = 16;
        private const int RTLD_NOW = 2;

        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlerror();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void JitWrite(IntPtr dst, IntPtr src, UIntPtr size);

        private readonly IDetourNativePlatform inner;
        private readonly JitWrite jitWrite;

        private AppleSiliconDetourPlatform(IDetourNativePlatform inner, JitWrite jitWrite)
        {
            this.inner = inner;
            this.jitWrite = jitWrite;
        }

        public static void Install(string coreDir)
        {
            string library = Path.Combine(coreDir, "libpalacejit.dylib");
            IntPtr handle = dlopen(library, RTLD_NOW);
            if (handle == IntPtr.Zero) throw new DllNotFoundException($"{library}: {Marshal.PtrToStringAnsi(dlerror())}");
            IntPtr symbol = dlsym(handle, "palace_jit_write");
            if (symbol == IntPtr.Zero) throw new EntryPointNotFoundException("palace_jit_write");
            var jitWrite = (JitWrite)Marshal.GetDelegateForFunctionPointer(symbol, typeof(JitWrite));

            // Creating DetourNativeARMPlatform directly skips the libc wrapper whose mprotect calls fail here.
            var platform = new AppleSiliconDetourPlatform(new DetourNativeARMPlatform(), jitWrite);
            // Exercise the write path once on ordinary memory so its marshalling stub is compiled up front.
            long* scratch = stackalloc long[2];
            platform.Copy((IntPtr)scratch, (IntPtr)scratch, 0);
            DetourHelper.Native = platform;
        }

        public NativeDetourData Create(IntPtr from, IntPtr to, byte? type) => inner.Create(from, to, type);

        public void Free(NativeDetourData detour) => inner.Free(detour);

        // Protection is handled inside palace_jit_write.
        public void MakeWritable(IntPtr src, uint size) { }

        public void MakeExecutable(IntPtr src, uint size) { }

        public void MakeReadWriteExecutable(IntPtr src, uint size) { }

        public void FlushICache(IntPtr src, uint size) { } // palace_jit_write already invalidated it

        public void Apply(NativeDetourData detour)
        {
            if (detour.Size != AArch64DetourSize) throw new NotSupportedException($"Unexpected detour size {detour.Size}");
            // ldr x15, #8 ; br x15 ; .quad target  (same sequence as MonoMod's DetourNativeARMPlatform)
            byte* code = stackalloc byte[(int)AArch64DetourSize];
            *(uint*)code = 0x5800004F;
            *(uint*)(code + 4) = 0xD61F01E0;
            *(ulong*)(code + 8) = (ulong)(long)detour.Target;
            jitWrite(detour.Method, (IntPtr)code, (UIntPtr)AArch64DetourSize);
        }

        public void Copy(IntPtr src, IntPtr dst, byte type)
        {
            // Copy through a buffer in case the ranges overlap.
            byte* buffer = stackalloc byte[(int)AArch64DetourSize];
            Buffer.MemoryCopy((void*)src, buffer, AArch64DetourSize, AArch64DetourSize);
            jitWrite(dst, (IntPtr)buffer, (UIntPtr)AArch64DetourSize);
        }

        // Backups of overwritten code are plain data, so ordinary memory is fine.
        public IntPtr MemAlloc(uint size) => inner.MemAlloc(size);

        public void MemFree(IntPtr ptr) => inner.MemFree(ptr);
    }
}
