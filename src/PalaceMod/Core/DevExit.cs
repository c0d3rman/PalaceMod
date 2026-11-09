using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

/*
    How dev.py stops a dev launch without a "Thronefall quit unexpectedly" dialog.

    Unity's own quit (SIGTERM, Cmd+Q, Application.Quit) tears the player down while a CVDisplayLink thread is still
    firing, and that thread then calls into freed memory (EXC_BAD_ACCESS in CVDisplayLink::performIO, after the job
    workers and the Finalizer thread are gone). ReportCrash writes a crash report and macOS shows the dialog. It hit
    -batchmode instances most, but it's Unity teardown, not our code.

    So dev launches never go through that teardown. On request, this runs what a quit would persist (the
    Application.quitting handlers, PlayerPrefs.Save), flushes the logs and ends the process with _exit(0): no atexit
    handlers, no native teardown, no crash report. The game's own OnApplicationQuit (ObjectPools) only frees memory.

    - dev.py writes BepInEx/PalaceMod-dev/exit.request; a background thread notices it (so it works while the main
      thread is busy), and the main thread does the exit at its next Update. If the main thread doesn't get to it
      within MainThreadGrace s (hung, or a long load), the background thread flushes the logs and exits on its own.
    - exit.ready holds the pid once the watcher runs; dev.py only asks a process whose pid is in it, and otherwise
      (still starting, or not a dev launch) kills it outright.
    - `dev.py cmd exit` (or the agent bridge's `exit`) does the same after the response is written.
*/
namespace PalaceMod
{
    internal static class DevExit
    {
        private const double MainThreadGrace = 4.0;

        private static string requestFile;
        private static volatile bool requested;
        private static string reason = "dev.py stop";
        private static volatile bool exiting;
        private static int requestedFrame = int.MaxValue;
        private static readonly object exitLock = new();
        private static bool mac;

        /// <summary>Starts the watcher and publishes exit.ready. Dev launches only.</summary>
        internal static void Start(string devDir)
        {
            mac = Application.platform == RuntimePlatform.OSXPlayer;
            requestFile = Path.Combine(devDir, "exit.request");
            File.Delete(requestFile);
            var watcher = new Thread(Watch) { IsBackground = true, Name = "PalaceMod dev exit" };
            watcher.Start();
            string ready = Path.Combine(devDir, "exit.ready");
            File.WriteAllText(ready + ".tmp", Process.GetCurrentProcess().Id.ToString());
            File.Delete(ready);
            File.Move(ready + ".tmp", ready);
        }

        /// <summary>The `exit` command: exit on a later frame, once its response has been written.</summary>
        internal static void RequestFromCommand()
        {
            requestedFrame = Time.frameCount;
            reason = "exit command";
            requested = true;
        }

        /// <summary>Called from DevToolsManager.Update on the main thread.</summary>
        internal static void Poll()
        {
            if (requested && Time.frameCount > requestedFrame) ExitNow(reason);
        }

        private static void Watch()
        {
            while (!File.Exists(requestFile)) Thread.Sleep(100);
            requestedFrame = -1;
            requested = true;
            var clock = Stopwatch.StartNew();
            // The main thread gets the first chance; once it has started, it isn't cut short unless it hangs.
            while (clock.Elapsed.TotalSeconds < (exiting ? MainThreadGrace * 3 : MainThreadGrace)) Thread.Sleep(50);
            // A main thread stuck inside ExitNow holds the lock; don't wait on it forever.
            Monitor.TryEnter(exitLock, 2000);
            Log.Core.Warn($"Main thread didn't exit within {clock.Elapsed.TotalSeconds:0.#} s of the exit request; exiting without quit handlers");
            FlushLogs();
            Terminate();
        }

        private static void ExitNow(string why)
        {
            exiting = true;
            lock (exitLock)
            {
                Log.Core.Info($"Exiting ({why}): quit handlers, PlayerPrefs.Save, then _exit without Unity's teardown");
                RunQuittingHandlers();
                try { PlayerPrefs.Save(); }
                catch (Exception e) { Log.Core.Warn($"PlayerPrefs.Save failed: {e.Message}"); }
                Log.Core.Info("Exited cleanly");
                FlushLogs();
                Terminate();
            }
        }

        // Application.quitting's subscribers (the game's and ours, e.g. BonusSafeSave's CheckNow), each isolated.
        private static void RunQuittingHandlers()
        {
            var field = typeof(Application).GetField("quitting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field?.GetValue(null) is not Action quitting) return;
            foreach (Action handler in quitting.GetInvocationList())
            {
                try { handler(); }
                catch (Exception e) { Log.Core.Warn($"Application.quitting handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name} threw: {e.Message}"); }
            }
        }

        private static void FlushLogs()
        {
            try { Log.Repeats.Flush(all: true); } catch { }
            foreach (var listener in BepInEx.Logging.Logger.Listeners)
            {
                try { if (listener is DiskLogListener disk) disk.LogWriter?.Flush(); } catch { }
            }
            try { Console.Out.Flush(); } catch { }
            try { Console.Error.Flush(); } catch { }
        }

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_exit")]
        private static extern void PosixExit(int status);

        private static void Terminate()
        {
            if (mac)
            {
                try { PosixExit(0); } catch { }
            }
            Process.GetCurrentProcess().Kill();
        }
    }
}
