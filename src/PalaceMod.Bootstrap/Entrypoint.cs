using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/*
    Doorstop entrypoint that lets BepInEx run natively on Apple Silicon instead of under Rosetta.

    BepInEx 5's MonoMod writes detours by mprotect-ing JIT code RWX and flushes the instruction cache by
    executing a managed byte array. Apple Silicon forbids both: Mono's JIT code lives in MAP_JIT memory, which
    a thread may only write after pthread_jit_write_protect_np(0). This installs a MonoMod native platform that
    does that, then hands off to BepInEx's own entrypoint. On any other platform it only hands off.
*/
namespace Doorstop
{
    public static class Entrypoint
    {
        private static string coreDir;

        public static void Start()
        {
            string self = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH") ?? typeof(Entrypoint).Assembly.Location;
            coreDir = Path.GetDirectoryName(Path.GetFullPath(self));
            string preloader = Path.Combine(coreDir, "BepInEx.Preloader.dll");
            string status;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    // Resolve MonoMod exactly the way BepInEx does (LoadFile from BepInEx/core) so both share one copy.
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveFromCore;
                    InstallAppleSiliconPlatform();
                    status = "installed the Apple Silicon detour platform";
                }
                else
                {
                    status = $"left MonoMod alone ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture})";
                }
            }
            catch (Exception e)
            {
                status = $"failed to install the Apple Silicon detour platform: {e}";
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromCore;
            }

            File.WriteAllText(Path.Combine(coreDir, "..", "PalaceMod.Bootstrap.log"), $"{DateTime.Now:O} {status}\n");

            // BepInEx locates itself from this variable, so point it at the real preloader.
            Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", preloader);
            Assembly.LoadFile(preloader).GetType("Doorstop.Entrypoint", true)
                .GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
        }

        private static Assembly ResolveFromCore(object sender, ResolveEventArgs args)
        {
            string path = Path.Combine(coreDir, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFile(path) : null;
        }

        // Kept out of Start so MonoMod types resolve only after the resolver is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InstallAppleSiliconPlatform() => AppleSiliconDetourPlatform.Install();
    }
}
