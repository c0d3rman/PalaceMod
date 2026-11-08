using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Agent bridge: lets a coding agent on this computer (Claude Code and the like) build and test Builder levels with
    the game running, from a normal install. Off by default.

    - Command channel: the same one-line commands as `dev.py cmd` (DevToolsManager.Execute), read from
      BepInEx/PalaceMod/Agent/bridge/command.request ("@<id>" then command lines) and answered in
      command-<id>.response ("ok"/"error", then the text). `agent ...` (AgentCommands) is the agent-facing set, with
      JSON results. Only programs that can write to the game folder can use it; nothing listens on the network.
    - bridge/status.json is rewritten every 2 s (pid, mod version, scene, time), so a client can tell the game is up.
    - The agent kit (the level-building guide, the Claude Code skill, palace.py and examples) is embedded in the DLL
      (agent-kit/ in the repo) and written to BepInEx/PalaceMod/Agent when the bridge is switched on, at startup
      while it's on, and from the "Write agent kit" button.
    - While on (and "Run in background" is on), the game keeps running when another window has focus, as dev
      launches do, so tests don't pause while the agent's terminal is in front.
    - Commands that change a vanilla run (anything but read-only `agent` queries, outside a Builder session) mark the
      run's gameplay impact.
*/
namespace PalaceMod.Builder.Agent
{
    public class AgentBridgeManager : BaseManager
    {
        public const string Section = "Agent bridge";
        public override string FeatureSection => Section;
        public override string FeatureDescription =>
            "Lets a coding agent on this computer (such as Claude Code) build, test and look at Builder levels while the game runs: " +
            "it reads commands from a file in the game folder, nothing listens on the network. Also writes the agent kit " +
            "(guide, skill and the palace.py tool) to BepInEx/PalaceMod/Agent.";
        protected override bool EnabledByDefault => false;

        public static string KitDir => Path.Combine(Paths.BepInExRootPath, "PalaceMod", "Agent");
        public static string BridgeDir => Path.Combine(KitDir, "bridge");
        private static string CommandRequest => Path.Combine(BridgeDir, "command.request");
        private static string StatusFile => Path.Combine(BridgeDir, "status.json");
        private const string KitResourcePrefix = "PalaceMod.AgentKit.";

        private ConfigEntry<bool> runInBackground;
        private bool? backgroundBefore;
        private float nextPoll, nextStatus;
        private bool started;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            runInBackground = plugin.Config.Bind(Section, "Run in background", true, new ConfigDescription(
                "Keep the game running while another window (the agent's terminal) has focus, so its tests don't pause."));
            runInBackground.SettingChanged += (_, _) => ApplyBackground();
            SettingsPane.AddButton(Section, "Write agent kit",
                "Writes the guide, the Claude Code skill and palace.py to BepInEx/PalaceMod/Agent again (also done whenever the bridge is switched on).",
                () =>
                {
                    try
                    {
                        int n = WriteKit();
                        Notify.Show($"Agent kit: {n} files in BepInEx/PalaceMod/Agent", key: "agent-kit");
                    }
                    catch (Exception e)
                    {
                        Log.Error("Couldn't write the agent kit", e);
                        Notify.Show("Couldn't write the agent kit (see the log)", NotifyKind.Warning, key: "agent-kit");
                    }
                });
            AgentCommands.Register();
        }

        public override void Start()
        {
            if (IsEnabled) TurnOn();
        }

        protected override void OnFeatureEnabled() => TurnOn();

        protected override void OnFeatureDisabled()
        {
            started = false;
            ApplyBackground();
            try { File.Delete(StatusFile); } catch (Exception) { }
            Log.Info("Agent bridge off");
        }

        private void TurnOn()
        {
            if (started) return;
            try
            {
                Directory.CreateDirectory(BridgeDir);
                File.Delete(CommandRequest);
                foreach (string stale in Directory.GetFiles(BridgeDir, "command-*.response*")) File.Delete(stale);
                int files = WriteKit();
                started = true;
                ApplyBackground();
                WriteStatus();
                Log.Info($"Agent bridge on: {BridgeDir} ({files} kit files written)");
            }
            catch (Exception e)
            {
                Log.Error("Couldn't start the agent bridge", e);
            }
        }

        private void ApplyBackground()
        {
            bool want = started && runInBackground.Value;
            if (want)
            {
                backgroundBefore ??= Application.runInBackground;
                Application.runInBackground = true;
            }
            else if (backgroundBefore is bool before)
            {
                // A dev launch keeps running in the background for its own reasons.
                if (Environment.GetEnvironmentVariable("PALACEMOD_DEV") != "1") Application.runInBackground = before;
                backgroundBefore = null;
            }
        }

        public override void Update()
        {
            if (!started) return;
            AgentCommands.Tick();
            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 2f;
                WriteStatus();
            }
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.1f;
            if (!File.Exists(CommandRequest)) return;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(CommandRequest);
                File.Delete(CommandRequest);
            }
            catch (IOException)
            {
                return; // still being written; next poll
            }
            string id = null;
            foreach (string line in lines.Select(c => c.Trim()).Where(c => c.Length > 0))
            {
                if (line.StartsWith("@"))
                {
                    id = line.Substring(1);
                    continue;
                }
                MarkImpact(line);
                string text = DevToolsManager.Execute(line, out bool ok);
                if (id != null) DevToolsManager.WriteResponse(BridgeDir, id, ok, text);
            }
        }

        // Read-only queries never change a run.
        private static readonly string[] ReadOnly =
            ["help", "scenes", "agent status", "agent catalog", "agent host", "agent packs", "agent validate", "agent reload",
             "agent paths", "agent capture", "agent report", "agent screenshot", "agent scenery", "agent wait"];

        private static void MarkImpact(string line)
        {
            if (BuilderServices.InSession || !DebugActions.InLevel) return;
            string lower = line.ToLowerInvariant();
            if (ReadOnly.Any(r => lower == r || lower.StartsWith(r + " "))) return;
            GameplayImpact.Mark("Agent bridge command");
        }

        private void WriteStatus()
        {
            try
            {
                var status = new JObject
                {
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["modVersion"] = DebugBundle.ModVersion,
                    ["gameVersion"] = Application.version,
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["bepinex"] = Paths.BepInExRootPath,
                    ["packs"] = BuilderServices.Packs?.Root,
                };
                File.WriteAllText(StatusFile + ".tmp", status.ToString());
                File.Delete(StatusFile);
                File.Move(StatusFile + ".tmp", StatusFile);
            }
            catch (Exception e)
            {
                Log.Debug($"status.json: {e.Message}");
            }
        }

        /// <summary>Writes the embedded agent kit to <see cref="KitDir"/> (overwriting the kit's own files, never
        /// anything else there). Returns the number of files written.</summary>
        internal static int WriteKit()
        {
            var asm = Assembly.GetExecutingAssembly();
            int n = 0;
            foreach (string name in asm.GetManifestResourceNames().Where(r => r.StartsWith(KitResourcePrefix)))
            {
                string relative = name.Substring(KitResourcePrefix.Length).Replace('\\', '/');
                if (relative.Contains("..")) continue;
                string path = Path.Combine(KitDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var stream = asm.GetManifestResourceStream(name);
                using var file = File.Create(path);
                stream.CopyTo(file);
                n++;
            }
            // The cross-map scenery library's catalog, for `palace.py ref scenery` and prop checks.
            using (var scenery = asm.GetManifestResourceStream("PalaceMod.Assets.scenery.json"))
            {
                if (scenery != null)
                {
                    string path = Path.Combine(KitDir, "reference", "scenery.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using var file = File.Create(path);
                    scenery.CopyTo(file);
                    n++;
                }
            }
            return n;
        }
    }
}
