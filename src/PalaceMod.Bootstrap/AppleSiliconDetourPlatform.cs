using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.Platforms;

namespace Doorstop
{
    /// <summary>
    /// MonoMod native platform for macOS on arm64. Every write to code happens inside Apply/Copy with the
    /// thread's JIT write protection lifted, and nothing in between can trigger the Mono JIT (which would
    /// re-enable protection when it finishes compiling).
    /// </summary>
    internal sealed unsafe class AppleSiliconDetourPlatform : IDetourNativePlatform
    {
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const uint AArch64DetourSize = 16;

        [DllImport(LibSystem)] private static extern void pthread_jit_write_protect_np(int enabled);
        [DllImport(LibSystem)] private static extern void sys_icache_invalidate(IntPtr start, UIntPtr length);

        private readonly IDetourNativePlatform inner;

        private AppleSiliconDetourPlatform(IDetourNativePlatform inner) => this.inner = inner;

        public static void Install()
        {
            // Creating DetourNativeARMPlatform directly skips the libc wrapper whose mprotect calls fail here.
            var platform = new AppleSiliconDetourPlatform(new DetourNativeARMPlatform());
            platform.WarmUp();
            DetourHelper.Native = platform;
        }

        // Compile everything Apply/Copy call before the first real detour, so no JIT runs while protection is lifted.
        private void WarmUp()
        {
            foreach (string name in new[] { nameof(Apply), nameof(Copy), nameof(FlushICache), nameof(Create) })
            {
                RuntimeHelpers.PrepareMethod(typeof(AppleSiliconDetourPlatform).GetMethod(name).MethodHandle);
            }
            pthread_jit_write_protect_np(1);
            long* scratch = stackalloc long[2];
            Copy((IntPtr)scratch, (IntPtr)scratch, 0);
        }

        public NativeDetourData Create(IntPtr from, IntPtr to, byte? type) => inner.Create(from, to, type);

        public void Free(NativeDetourData detour) => inner.Free(detour);

        // Protection is lifted only inside Apply/Copy.
        public void MakeWritable(IntPtr src, uint size) { }

        public void MakeExecutable(IntPtr src, uint size) { }

        public void MakeReadWriteExecutable(IntPtr src, uint size) { }

        public void FlushICache(IntPtr src, uint size) => sys_icache_invalidate(src, (UIntPtr)size);

        public void Apply(NativeDetourData detour)
        {
            if (detour.Size != AArch64DetourSize) throw new NotSupportedException($"Unexpected detour size {detour.Size}");
            byte* code = (byte*)detour.Method;
            pthread_jit_write_protect_np(0);
            // ldr x15, #8 ; br x15 ; .quad target  (same sequence as MonoMod's DetourNativeARMPlatform)
            *(uint*)code = 0x5800004F;
            *(uint*)(code + 4) = 0xD61F01E0;
            *(ulong*)(code + 8) = (ulong)(long)detour.Target;
            pthread_jit_write_protect_np(1);
            sys_icache_invalidate(detour.Method, (UIntPtr)AArch64DetourSize);
        }

        public void Copy(IntPtr src, IntPtr dst, byte type)
        {
            long* from = (long*)src;
            long* to = (long*)dst;
            pthread_jit_write_protect_np(0);
            to[0] = from[0];
            to[1] = from[1];
            pthread_jit_write_protect_np(1);
            sys_icache_invalidate(dst, (UIntPtr)AArch64DetourSize);
        }

        // Backups of overwritten code are plain data, so ordinary memory is fine.
        public IntPtr MemAlloc(uint size) => inner.MemAlloc(size);

        public void MemFree(IntPtr ptr) => inner.MemFree(ptr);
    }
}
