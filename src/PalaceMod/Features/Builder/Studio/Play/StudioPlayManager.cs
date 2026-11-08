using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using PalaceMod.Builder;
using PalaceMod.Builder.Studio;
using PalaceMod.Builder.Studio.Play;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Studio test play (W2, docs/builder-ux.md "Play mode"): installs StudioPlay as StudioServices.Play, registers the
    sandbox panel and its keys (context BuilderPlay), drives the swap state machine per frame, and holds the patches
    that make play feel like the Studio's and not a normal level:
    - unlimited gold (spending gives the coin back; SandboxActions keeps the balance topped up);
    - no in-level tips (formations, zoom, sprint, "hold to preview"...) in any Studio load, edit or play;
    - leaving play by the game's own exits (pause menu Back to Map, end screen Continue) comes back to the Studio, and
      those rows say so; the pause menu gets "Back to the Studio" as its first mod row.
    Part of the Builder feature (its keys are in the Builder section); every patch is a no-op outside a Studio session.
*/
namespace PalaceMod
{
    public class StudioPlayManager : BaseManager
    {
        internal const string BackLabel = "Back to the Studio";

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            StudioServices.Play = StudioPlay.Instance;
            PlayKeys.Register();
            Keybinds.ClaimContext(StudioContexts.Play, () => StudioPlay.InPlay);
            SandboxPanel.Register();
            PauseMenu.AddButton("studio-back", BackLabel, StudioPlay.Instance.ReturnToEdit, order: -100,
                visible: () => StudioPlay.InPlay);
            Studio.ModeChanged += mode => { if (mode == StudioMode.Off) StudioPlay.Instance.Reset(); };
            SceneManager.sceneLoaded += (scene, _) =>
            {
                try { StudioPlay.Instance.OnHostLoaded(scene); }
                catch (Exception e) { Log.For<BuilderManager>().Error("Studio play: scene load handling failed", e); }
            };
            RegisterDevCommand();
        }

        public override void Start()
        {
            // The session can end without the shell noticing (Builder switched off, a dev `builder end`).
            if (BuilderManager.Launcher != null) BuilderManager.Launcher.Ended += _ => { if (Studio.Mode == StudioMode.Off) StudioPlay.Instance.Reset(); };
        }

        public override void Update()
        {
            var play = StudioPlay.Instance;
            play.Update();
            if (!StudioPlay.InPlay || play.Swapping) return;
            if (!Feature<BuilderManager>.On) return;

            if (PlayKeys.Back.Down) { play.ReturnToEdit(); return; }
            if (PlayKeys.Restart.Down) { play.Restart(); return; }
            if (PlayKeys.Teleport.Down) SandboxPanel.Toggle(SandboxPanel.Armed.Teleport);
            if (PlayKeys.StartNight.Down) SandboxPanel.Run(DebugActions.IsNight ? DebugActions.EndNight : DebugActions.StartNight);
            if (PlayKeys.KillAll.Down) SandboxPanel.Run(DebugActions.KillEnemies);
            if (PlayKeys.Slower.Down) SandboxPanel.StepSpeed(-1);
            if (PlayKeys.Faster.Down) SandboxPanel.StepSpeed(1);

            if (SandboxPanel.Arm != SandboxPanel.Armed.None && UIFrameManager.instance != null && !PausedByMenu())
            {
                if (Input.GetMouseButtonDown(1)) SandboxPanel.Disarm();
                else if (Input.GetMouseButtonDown(0) && !StudioPlay.PointerOverUI() && SandboxActions.CursorGround(out var ground)) SandboxPanel.Clicked(ground);
            }
        }

        /// <summary>A game menu (pause, settings) is up: clicks belong to it.</summary>
        private static bool PausedByMenu() => Time.timeScale == 0f;

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(PlayerInteraction), nameof(PlayerInteraction.SpendCoins))]
        private static class InfiniteGold_Patch
        {
            private static void Postfix(PlayerInteraction __instance, int amount) => SandboxActions.Refund(__instance, amount);
        }

        // The game's in-level tips (AfterTutorialTipManager) are for learning the game, not for building a level. Only
        // its Update is skipped: OnDusk still saves through DayNightCycle.
        [HarmonyPatch(typeof(AfterTutorialTipManager), "Update")]
        private static class NoTips_Patch
        {
            private static AfterTutorialTipManager cleared;

            private static bool Prefix(AfterTutorialTipManager __instance)
            {
                if (!StudioPlay.InStudioSession && !Studio.Running) return true;
                if (cleared != __instance)
                {
                    cleared = __instance;
                    TipManager.instance?.UpdateTipRaw("", "");
                }
                return false;
            }
        }

        // Back to Map (pause menu, resign popup) and the end screen's Continue go back to the Studio from play.
        [HarmonyPatch]
        private static class BackToStudio_Patch
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromLevelToLevelSelect));
                yield return AccessTools.Method(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromEndScreenToLevelSelect));
            }

            [HarmonyPriority(Priority.First)]
            private static bool Prefix(SceneTransitionManager __instance)
            {
                if (!StudioPlay.InPlay || !Feature<BuilderManager>.On || __instance.SceneTransitionIsRunning) return true;
                StudioPlay.Instance.ReturnToEdit();
                return false;
            }
        }

        // The end screen's Continue says where it goes.
        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), nameof(EndOfMatchScoreUIHelper.OnActivate))]
        private static class EndScreenLabel_Patch
        {
            private static readonly Dictionary<TFUITextButton, string> originals = [];

            private static void Postfix(EndOfMatchScoreUIHelper __instance)
            {
                bool studio = StudioPlay.InPlay;
                foreach (var button in __instance.GetComponentsInChildren<TFUITextButton>(true))
                {
                    if (!Calls(button, nameof(EndOfMatchScoreUIHelper.OnContinue))) continue;
                    if (!originals.ContainsKey(button)) originals[button] = Traverse.Create(button).Field("originalString").GetValue<string>();
                    string want = studio ? BackLabel : originals[button];
                    if (want != null) button.SetOriginalString(want);
                }
            }

            private static bool Calls(ThronefallUIElement element, string method)
            {
                var e = element.onApply;
                for (int i = 0; i < e.GetPersistentEventCount(); i++)
                    if (e.GetPersistentMethodName(i) == method) return true;
                return false;
            }
        }

        // ------------------------------------------------------------------ dev command

        private static void RegisterDevCommand()
        {
            DevCommands.Register("builder-play",
                "builder-play start <pack> <level> [x z] | enter [castle] | back | restart | state | gold on|off|<n>|spend <n> | spawn <unit> [count] [elite] [line <name>|at <x> <z>] | " +
                "teleport <x> <z> [followers] | cursor <x> <z>|off | arm teleport|spawn|none | click | panel | endscreen | tips | music   Studio test play", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: builder-play start|enter|back|restart|state|gold|spawn|teleport|cursor|arm|click|panel|endscreen|music");
                var play = StudioPlay.Instance;
                switch (args[0].ToLowerInvariant())
                {
                    case "start":
                    {
                        if (args.Length < 3) throw new ArgumentException("usage: builder-play start <pack> <level> [x z]");
                        var entry = BuilderServices.Packs.Find(args[1]) ?? throw new ArgumentException($"no pack \"{args[1]}\"");
                        if (entry.Pack == null) throw new InvalidOperationException($"{entry.Id} can't be read");
                        Vector3? at = args.Length >= 5 ? new Vector3(F(args[3]), 0f, F(args[4])) : null;
                        if (!play.PlayLevel(new Builder.Editor.PackDraft(entry), args[2], at)) throw new InvalidOperationException("didn't start (see the notice)");
                        return "starting";
                    }
                    case "enter":
                    {
                        if (!play.CanSwap(out string why)) throw new InvalidOperationException(why);
                        play.EnterPlay(fromCursor: !(args.Length > 1 && args[1] == "castle"));
                        return play.Swapping ? $"entering play; spot={play.Spot?.ToString() ?? "castle"}" : "didn't start (see the notice)";
                    }
                    case "back":
                        if (!StudioPlay.InPlay) throw new InvalidOperationException("not in Studio play");
                        play.ReturnToEdit();
                        return play.Swapping ? "returning" : "didn't return";
                    case "restart":
                        play.Restart();
                        return play.Swapping ? "restarting" : "didn't restart";
                    case "state":
                        return play.Describe() + $"; armed={SandboxPanel.Arm} unit={SandboxPanel.UnitRef} enemies={(TagManager.instance == null ? 0 : TagManager.instance.CountAllTaggedObjectsWithTag(TagManager.ETag.EnemyOwned))}" +
                            $"; king={PlayerInteraction.instance?.transform.position}; tipsShown={LevelProgressManager.instance?.tipsShown?.Count}; tip=\"{TipText()}\"";
                    case "gold":
                    {
                        string v = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                        if (v == "spend" && args.Length > 2)
                        {
                            // The game's own payment call, as filling a build slot's coin slots makes it.
                            var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                            int before = player.Balance;
                            player.SpendCoins(int.Parse(args[2]));
                            return $"balance {before} -> {player.Balance}";
                        }
                        if (v == "on" || v == "off") SandboxActions.InfiniteGold = v == "on";
                        else if (int.TryParse(v, out int n)) { SandboxActions.InfiniteGold = false; DebugActions.SetGold(n); }
                        else throw new ArgumentException("usage: builder-play gold on|off|<n>");
                        SandboxPanel.Refresh();
                        return $"unlimited={SandboxActions.InfiniteGold} balance={PlayerInteraction.instance?.Balance}";
                    }
                    case "spawn":
                    {
                        if (args.Length < 2) throw new ArgumentException("usage: builder-play spawn <unit> [count] [elite] [line <name>|at <x> <z>]");
                        int i = 2, count = 1;
                        if (args.Length > i && int.TryParse(args[i], out int c)) { count = c; i++; }
                        bool elite = args.Length > i && args[i] == "elite";
                        if (elite) i++;
                        Vector3? at = null;
                        string line = null;
                        if (args.Length > i + 1 && args[i] == "line") line = string.Join(" ", args.Skip(i + 1));
                        else if (args.Length > i + 2 && args[i] == "at") at = Builder.Level.LayoutScene.Ground(new Vector3(F(args[i + 1]), PlayerInteraction.instance?.transform.position.y ?? 0f, F(args[i + 2])));
                        else at = PlayerInteraction.instance != null ? PlayerInteraction.instance.transform.position + new Vector3(8f, 0f, 8f) : null;
                        string unit = args[1].Replace('_', ' ');
                        return SandboxActions.Spawn(play.DraftPack, unit, count, elite, at, line);
                    }
                    case "teleport":
                    {
                        if (args.Length < 3) throw new ArgumentException("usage: builder-play teleport <x> <z> [followers]");
                        var p = Builder.Level.LayoutScene.Ground(new Vector3(F(args[1]), PlayerInteraction.instance?.transform.position.y ?? 0f, F(args[2])));
                        return SandboxActions.Teleport(p, args.Length > 3);
                    }
                    case "cursor":
                        if (args.Length > 1 && args[1] == "off") { SandboxActions.DevCursor = null; return "cursor off"; }
                        if (args.Length < 3) throw new ArgumentException("usage: builder-play cursor <x> <z>|off");
                        SandboxActions.DevCursor = new Vector3(F(args[1]), PlayerInteraction.instance?.transform.position.y ?? 0f, F(args[2]));
                        return $"cursor {SandboxActions.DevCursor}";
                    case "arm":
                        SandboxPanel.Toggle(args.Length > 1 && args[1] == "spawn" ? SandboxPanel.Armed.Spawn : args.Length > 1 && args[1] == "teleport" ? SandboxPanel.Armed.Teleport : SandboxPanel.Armed.None);
                        return $"armed {SandboxPanel.Arm}";
                    case "click":
                    {
                        if (!SandboxActions.CursorGround(out var g)) throw new InvalidOperationException("no ground under the cursor");
                        var was = SandboxPanel.Arm;
                        SandboxPanel.Clicked(g);
                        return $"clicked {g} ({was})";
                    }
                    case "panel":
                    {
                        var panel = Studio.Panels.FirstOrDefault(p => p.Id == SandboxPanel.Id);
                        return panel == null ? "not registered" : $"registered for {panel.Mode}, visible={panel.Visible()}";
                    }
                    case "endscreen":
                    {
                        var helper = UnityEngine.Object.FindObjectOfType<EndOfMatchScoreUIHelper>(true);
                        if (helper == null) return "no end screen";
                        return string.Join(", ", helper.GetComponentsInChildren<TFUITextButton>(true).Select(b => $"{b.name}=\"{b.GetComponentInChildren<TMPro.TMP_Text>(true)?.text}\"{(b.gameObject.activeInHierarchy ? "" : " (hidden)")}"));
                    }
                    case "tips":
                    {
                        // Forget the tips this save has seen (in memory only), so the next level load would show them.
                        var shown = LevelProgressManager.instance?.tipsShown ?? throw new InvalidOperationException("no progress manager");
                        int n = shown.Count;
                        shown.Clear();
                        return $"forgot {n} seen tips; reload a level to test";
                    }
                    case "music":
                    {
                        var m = MusicManager.instance;
                        if (m == null) return "no music manager";
                        var t = Traverse.Create(m);
                        return $"current={t.Field("currentMusic").GetValue<AudioClip>()?.name ?? "none"} requested={t.Field("requestedMusic").GetValue<AudioClip>()?.name ?? "none"}";
                    }
                    default:
                        throw new ArgumentException("unknown builder-play subcommand");
                }
            });
        }

        private static string TipText()
        {
            var tm = TipManager.instance;
            if (tm == null) return "";
            return string.Join(" | ", tm.GetComponentsInChildren<TMPro.TMP_Text>(false).Select(t => t.text).Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
}

namespace PalaceMod.Builder.Studio.Play
{
    /// <summary>Play-mode keys (Builder section, context BuilderPlay: only while a Studio play load runs).</summary>
    internal static class PlayKeys
    {
        public static Keybind Back, Restart, Teleport, StartNight, KillAll, Slower, Faster;

        public static void Register()
        {
            const string s = "Builder";
            const string c = StudioContexts.Play;
            const KeybindFlags f = KeybindFlags.AllowInMenus;
            Back = Keybinds.Register(s, "play-back", "Studio play: back to the Studio", new KeyboardShortcut(KeyCode.F5), "In Studio test play: back to the editor, as it was.", f, c);
            Restart = Keybinds.Register(s, "play-restart", "Studio play: restart", new KeyboardShortcut(KeyCode.R), "In Studio test play: starts the test again from the same spot.", flags: KeybindFlags.None, context: c);
            Teleport = Keybinds.Register(s, "play-teleport", "Studio play: teleport", new KeyboardShortcut(KeyCode.T), "In Studio test play: the next click on the map moves the king there.", flags: KeybindFlags.None, context: c);
            StartNight = Keybinds.Register(s, "play-night", "Studio play: start or end night", new KeyboardShortcut(KeyCode.N), "In Studio test play: starts the next night now, or ends the one in progress.", flags: KeybindFlags.None, context: c);
            KillAll = Keybinds.Register(s, "play-kill", "Studio play: kill all enemies", new KeyboardShortcut(KeyCode.K), "In Studio test play: removes every enemy.", flags: KeybindFlags.None, context: c);
            Slower = Keybinds.Register(s, "play-slower", "Studio play: slower", new KeyboardShortcut(KeyCode.LeftBracket), "In Studio test play: game speed down a step.", flags: KeybindFlags.None, context: c);
            Faster = Keybinds.Register(s, "play-faster", "Studio play: faster", new KeyboardShortcut(KeyCode.RightBracket), "In Studio test play: game speed up a step.", flags: KeybindFlags.None, context: c);
        }

        public static string Label(Keybind k) => Level.Keys.Label(k);
    }
}
