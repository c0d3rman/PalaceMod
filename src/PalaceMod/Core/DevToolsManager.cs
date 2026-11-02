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
    - Runs commands from a request file so an agent can drive the game without a human. `dev.py cmd help` lists them:
      the built-ins in BuiltIns below plus everything features register with DevCommands.Register.
    A request is "@<id>" followed by command lines. Each result goes to command-<id>.response ("ok" or "error" on the
    first line, then the text), which dev.py prints, and to the log as "Dev command done/failed: <command>".
*/
namespace PalaceMod
{
    [ManagerOrder(-100)]
    public class DevToolsManager : BaseManager
    {
        private static readonly string[] BuiltIns =
        [
            "help                   list commands",
            "scenes                 list the scenes in the build",
            "menu | levelselect     go to the main menu or level select",
            "level <scene>          load a level directly",
            "run <h> <v> <seconds>  hold the movement stick (each axis -1..1) for a while",
            "circle <seconds>       run in a circle, for testing camera motion",
            "sprint <on|off>        hold sprint while running",
            "stop                   release all fake input",
            "settings               open the Settings screen on the Mods tab",
            "ui [up|down|left|right|apply|close]   navigate the open menu like a controller; prints the selection",
            "mute [off]             mute or unmute the game (dev launches start muted unless PALACEMOD_SOUND=1)",
            "night                  start the next night (wave) now",
            "pause                  open the in-match pause menu",
            "timescale <x>          set Time.timeScale",
            "resolutions            list the current and available display modes",
            "setres <w> <h> <hz>    switch display mode (keeps the fullscreen mode)",
        ];

        internal static bool IsBuiltIn(string name) =>
            BuiltIns.Any(usage => usage.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            name.Equals("levelselect", StringComparison.OrdinalIgnoreCase);

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
            if (Environment.GetEnvironmentVariable("PALACEMOD_SOUND") != "1") AudioListener.volume = 0f;
            Directory.CreateDirectory(DevDir);
            File.Delete(ScreenshotRequest);
            File.Delete(CommandRequest);
            foreach (string stale in Directory.GetFiles(DevDir, "command-*.response*")) File.Delete(stale);
            RegisterCommands();
            Log.Info("Dev tools enabled");
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
                Log.Debug($"Screenshot requested: {output}");
            }

            if (File.Exists(CommandRequest))
            {
                string[] lines = File.ReadAllLines(CommandRequest);
                File.Delete(CommandRequest);
                string id = null;
                foreach (string line in lines.Select(c => c.Trim()).Where(c => c.Length > 0))
                {
                    if (line.StartsWith("@"))
                    {
                        id = line.Substring(1);
                        continue;
                    }
                    bool ok;
                    string text;
                    try
                    {
                        text = Run(line.Split([' '], StringSplitOptions.RemoveEmptyEntries)) ?? "";
                        ok = true;
                        Log.Info($"Dev command done: {line}{(text.Length == 0 ? "" : $": {text}")}");
                    }
                    catch (Exception e)
                    {
                        text = e is ArgumentException or InvalidOperationException or FormatException ? e.Message : e.ToString();
                        ok = false;
                        Log.Warn($"Dev command failed: {line}: {text}");
                    }
                    if (id != null) WriteResponse(id, ok, text);
                }
            }
        }

        private static void WriteResponse(string id, bool ok, string text)
        {
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            string path = Path.Combine(DevDir, $"command-{id}.response");
            File.WriteAllText(path + ".tmp", $"{(ok ? "ok" : "error")}\n{text}");
            File.Delete(path);
            File.Move(path + ".tmp", path);
        }

        // Commands about the foundation APIs, registered through DevCommands like a feature's would be.
        private void RegisterCommands()
        {
            DevCommands.Register("features", "features               list managers: enabled or not, and how many game methods they patch", _ =>
                "\n" + string.Join("\n", Plugin.Managers.Select(m =>
                    $"{m.GetType().Name}: {(m.FeatureSection == null ? "always on" : m.IsEnabled ? "enabled" : "disabled")}, {m.PatchedMethodCount} patched methods")));
            DevCommands.Register("impact", "impact [mark <reason>] show (or record) this run's gameplay-impact reasons", args =>
            {
                if (args.Length >= 2 && args[0] == "mark") GameplayImpact.Mark(string.Join(" ", args.Skip(1)));
                else if (args.Length > 0) throw new ArgumentException("usage: impact [mark <reason>]");
                return GameplayImpact.Any ? string.Join(", ", GameplayImpact.Reasons) : "none";
            });
            DevCommands.Register("notify", "notify <text>          show a notification", args =>
            {
                Notify.Show(string.Join(" ", args), key: "dev-notify");
                return "";
            });
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
                    Require(transitions).TransitionFromNullToLevel(string.Join(" ", args.Skip(1)));
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
                case "settings":
                {
                    var pane = Plugin.GetManager<SettingsPaneManager>();
                    if (pane?.Frame == null) throw new InvalidOperationException("settings screen not found");
                    UIFrameManager.instance.ChangeActiveFrame(pane.Frame);
                    // The tab is injected when the screen first wakes up, i.e. by the line above.
                    if (pane.TabButton != null) pane.Frame.Select(pane.TabButton);
                    return DescribeSelection();
                }
                case "ui":
                {
                    var frame = UIFrameManager.instance?.ActiveFrame ?? throw new InvalidOperationException("no menu open");
                    string action = args.Length > 1 ? args[1] : "";
                    if (action == "apply") frame.Apply();
                    else if (action == "close") UIFrameManager.instance.CloseActiveFrame();
                    else if (action != "")
                    {
                        var direction = (ThronefallUIElement.NavigationDirection)Enum.Parse(typeof(ThronefallUIElement.NavigationDirection), action, true);
                        var next = frame.CurrentSelection?.TryNavigate(direction);
                        if (next != null) frame.Select(next);
                    }
                    return DescribeSelection();
                }
                case "mute":
                    AudioListener.volume = args.Length > 1 && args[1] == "off" ? 1f : 0f;
                    return "";
                case "night":
                    if (DayNightCycle.Instance == null) throw new InvalidOperationException("not in a level");
                    DayNightCycle.Instance.SwitchToNight();
                    return "";
                case "pause":
                    if (DayNightCycle.Instance == null || UIFrameManager.instance == null) throw new InvalidOperationException("not in a level");
                    UIFrameManager.instance.InMatchPause();
                    return DescribeSelection();
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
                case "help":
                    return "\n" + string.Join("\n", BuiltIns.Concat(DevCommands.All.Select(c => c.Usage)));
                default:
                    if (DevCommands.TryGet(args[0], out var command)) return command.Handler(args.Skip(1).ToArray());
                    throw new ArgumentException("unknown command; `dev.py cmd help` lists them");
            }
        }

        private static string DescribeSelection()
        {
            var frame = UIFrameManager.instance?.ActiveFrame;
            var selected = frame?.CurrentSelection;
            if (frame == null) return "no menu open";
            if (selected == null) return $"{frame.name} > nothing selected";
            static string Name(ThronefallUIElement e) => e == null ? "-" : $"{e.name}{(e.gameObject.activeInHierarchy ? "" : " (inactive)")}";
            return $"{frame.name} > {selected.name} [{selected.GetType().Name}, up {Name(selected.topNav)}, down {Name(selected.botNav)}, " +
                $"left {Name(selected.leftNav)}, right {Name(selected.rightNav)}]";
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
                Log.Debug("Dev input finished");
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
