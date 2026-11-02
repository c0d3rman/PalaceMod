using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Development hooks used by scripts/dev.py. Inactive unless the game was started with PALACEMOD_DEV=1.
    - Keeps the game running and rendering while its window is unfocused.
    - Captures a screenshot when dev.py drops a request file containing the output path.
    - Runs commands from a request file so an agent can drive the game without a human:
        scenes                 list the scenes in the build
        menu | levelselect     go to the main menu or level select
        level <scene>          load a level directly
        run <h> <v> <seconds>  hold the movement stick (each axis -1..1) for a while
        circle <seconds>       run in a circle, for testing camera motion
        sprint <on|off>        hold sprint while running
        stop                   release all fake input
        night                  start the next night (wave) now
        timescale <x>          set Time.timeScale
        resolutions            list the current and available display modes
        setres <w> <h> <hz>    switch display mode (keeps the fullscreen mode)
    Each command logs "Dev command done: <command>" or "Dev command failed: <command>: <reason>".
*/
namespace PalaceMod
{
    public class DevToolsManager : BaseManager
    {
        private static readonly string DevDir = Path.Combine(Paths.BepInExRootPath, "PalaceMod-dev");
        private static readonly string ScreenshotRequest = Path.Combine(DevDir, "screenshot.request");
        private static readonly string CommandRequest = Path.Combine(DevDir, "command.request");

        private bool enabled;
        private float nextPoll;

        // Fake input, read by the Rewired patch below.
        private static bool inputActive;
        public static bool FakeInputActive => inputActive;
        private static float moveHorizontal, moveVertical;
        private static bool sprint;
        private float inputUntil;
        private bool circling;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            enabled = Environment.GetEnvironmentVariable("PALACEMOD_DEV") == "1";
            if (!enabled) return;

            Application.runInBackground = true;
            Directory.CreateDirectory(DevDir);
            File.Delete(ScreenshotRequest);
            File.Delete(CommandRequest);
            Plugin.Logger.LogInfo("Dev tools enabled");
        }

        public override void Update()
        {
            if (!enabled) return;
            UpdateFakeInput();

            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.25f;

            if (File.Exists(ScreenshotRequest))
            {
                string output = File.ReadAllText(ScreenshotRequest).Trim();
                File.Delete(ScreenshotRequest);
                ScreenCapture.CaptureScreenshot(output);
                Plugin.Logger.LogInfo($"Screenshot requested: {output}");
            }

            if (File.Exists(CommandRequest))
            {
                string[] commands = File.ReadAllLines(CommandRequest);
                File.Delete(CommandRequest);
                foreach (string command in commands.Select(c => c.Trim()).Where(c => c.Length > 0))
                {
                    try
                    {
                        string result = Run(command.Split([' '], StringSplitOptions.RemoveEmptyEntries));
                        Plugin.Logger.LogInfo($"Dev command done: {command}{(string.IsNullOrEmpty(result) ? "" : $": {result}")}");
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger.LogInfo($"Dev command failed: {command}: {e.Message}");
                    }
                }
            }
        }

        private string Run(string[] args)
        {
            var transitions = SceneTransitionManager.instance;
            switch (args[0].ToLowerInvariant())
            {
                case "scenes":
                    return string.Join(", ", Enumerable.Range(0, SceneManager.sceneCountInBuildSettings)
                        .Select(i => Path.GetFileNameWithoutExtension(UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i))));
                case "menu":
                    Require(transitions).TransitionToMainMenu();
                    return "";
                case "levelselect":
                    Require(transitions).TransitionToLevelSelect();
                    return "";
                case "level":
                    if (args.Length < 2) throw new ArgumentException("usage: level <scene>");
                    Require(transitions).TransitionFromNullToLevel(args[1]);
                    return "";
                case "run":
                    if (args.Length < 4) throw new ArgumentException("usage: run <h> <v> <seconds>");
                    circling = false;
                    StartInput(float.Parse(args[1]), float.Parse(args[2]), float.Parse(args[3]));
                    return "";
                case "circle":
                    circling = true;
                    StartInput(1f, 0f, args.Length > 1 ? float.Parse(args[1]) : 5f);
                    return "";
                case "sprint":
                    sprint = args.Length < 2 || args[1] != "off";
                    return "";
                case "stop":
                    inputActive = circling = sprint = false;
                    return "";
                case "night":
                    if (DayNightCycle.Instance == null) throw new InvalidOperationException("not in a level");
                    DayNightCycle.Instance.SwitchToNight();
                    return "";
                case "timescale":
                    if (args.Length < 2) throw new ArgumentException("usage: timescale <x>");
                    Time.timeScale = float.Parse(args[1]);
                    return "";
                case "resolutions":
                    return $"current {Screen.currentResolution.width}x{Screen.currentResolution.height}@{Screen.currentResolution.refreshRateRatio.value:0.##} " +
                        $"({Screen.fullScreenMode}); available " +
                        string.Join(", ", Screen.resolutions.Select(r => $"{r.width}x{r.height}@{r.refreshRateRatio.value:0.##}"));
                case "setres":
                    if (args.Length < 4) throw new ArgumentException("usage: setres <width> <height> <hz>");
                    Screen.SetResolution(int.Parse(args[1]), int.Parse(args[2]), Screen.fullScreenMode,
                        new RefreshRate { numerator = uint.Parse(args[3]), denominator = 1 });
                    return "";
                default:
                    throw new ArgumentException("unknown command");
            }
        }

        private static SceneTransitionManager Require(SceneTransitionManager transitions) =>
            transitions ?? throw new InvalidOperationException("SceneTransitionManager isn't loaded yet");

        private void StartInput(float horizontal, float vertical, float seconds)
        {
            moveHorizontal = Mathf.Clamp(horizontal, -1f, 1f);
            moveVertical = Mathf.Clamp(vertical, -1f, 1f);
            inputUntil = Time.unscaledTime + seconds;
            inputActive = true;
        }

        private void UpdateFakeInput()
        {
            if (!inputActive) return;
            if (Time.unscaledTime >= inputUntil)
            {
                inputActive = circling = false;
                Plugin.Logger.LogInfo("Dev input finished");
                return;
            }
            if (circling)
            {
                float angle = Time.unscaledTime * 1.5f;
                moveHorizontal = Mathf.Cos(angle);
                moveVertical = Mathf.Sin(angle);
            }
        }

        [HarmonyPatch(typeof(Rewired.Player), nameof(Rewired.Player.GetAxis), [typeof(string)])]
        private class Player_GetAxis_Patch
        {
            private static void Postfix(string actionName, ref float __result)
            {
                if (!inputActive) return;
                if (actionName == "Move Horizontal") __result = moveHorizontal;
                else if (actionName == "Move Vertical") __result = moveVertical;
            }
        }

        [HarmonyPatch(typeof(Rewired.Player), nameof(Rewired.Player.GetButton), [typeof(string)])]
        private class Player_GetButton_Patch
        {
            private static void Postfix(string actionName, ref bool __result)
            {
                if (inputActive && sprint && actionName == "Sprint") __result = true;
            }
        }
    }
}
