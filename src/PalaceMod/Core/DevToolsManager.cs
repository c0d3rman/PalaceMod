using System;
using System.IO;
using BepInEx;
using UnityEngine;

/*
    Development hooks used by scripts/dev.py. Inactive unless the game was started with PALACEMOD_DEV=1.
    - Keeps the game running and rendering while its window is unfocused.
    - Captures a screenshot when dev.py drops a request file containing the output path.
*/
namespace PalaceMod
{
    public class DevToolsManager : BaseManager
    {
        private static readonly string RequestFile = Path.Combine(Paths.BepInExRootPath, "PalaceMod-dev", "screenshot.request");
        private bool enabled;
        private float nextPoll;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            enabled = Environment.GetEnvironmentVariable("PALACEMOD_DEV") == "1";
            if (!enabled) return;

            Application.runInBackground = true;
            Directory.CreateDirectory(Path.GetDirectoryName(RequestFile));
            File.Delete(RequestFile);
            Plugin.Logger.LogInfo("Dev tools enabled");
        }

        public override void Update()
        {
            if (!enabled || Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.5f;

            if (!File.Exists(RequestFile)) return;

            string output = File.ReadAllText(RequestFile).Trim();
            File.Delete(RequestFile);
            ScreenCapture.CaptureScreenshot(output);
            Plugin.Logger.LogInfo($"Screenshot requested: {output}");
        }
    }
}
