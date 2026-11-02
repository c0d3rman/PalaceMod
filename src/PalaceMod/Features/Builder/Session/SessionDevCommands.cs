using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine.SceneManagement;

// `dev.py cmd "builder-session <sub>"`: drives the Custom Runs flow in a dev launch.
namespace PalaceMod.Builder
{
    internal static class SessionDevCommands
    {
        internal static void Register(SessionLauncher launcher)
        {
            DevCommands.Register("builder-session",
                "builder-session prelevel <map>|menu|arm <pack> <waveSet>|disarm|play|finish win|lose|scores|state   custom runs: menus, results", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: builder-session prelevel|menu|arm|disarm|play|finish|scores|state");
                switch (args[0].ToLowerInvariant())
                {
                    case "prelevel":
                    {
                        // The map's pre-level menu, as if the king walked onto it.
                        if (args.Length < 2) throw new ArgumentException("usage: builder-session prelevel <map scene>");
                        if (SceneTransitionManager.instance?.IsInLevelSelect() != true) throw new InvalidOperationException("not in level select (`levelselect`)");
                        var info = LevelProgressManager.instance.GetLevelInfoFromSceneName(string.Join(" ", args.Skip(1))) ?? throw new ArgumentException($"no map \"{string.Join(" ", args.Skip(1))}\"");
                        launcher.Disarm();
                        LevelInteractor.lastActiveLevelInfo = info;
                        if (UIFrameManager.instance.ActiveFrame != null) UIFrameManager.instance.CloseAllFrames();
                        UIFrameManager.TryOpenLevelSelect();
                        return $"opened {info.sceneName}";
                    }
                    case "menu":
                        SessionMenu.Open();
                        return SessionMenu.IsOpen ? "open" : "didn't open";
                    case "arm":
                    {
                        if (args.Length < 3) throw new ArgumentException("usage: builder-session arm <pack> <waveSet>");
                        var entry = BuilderServices.Packs.Find(args[1]) ?? throw new ArgumentException($"no pack \"{args[1]}\"");
                        if (!launcher.Arm(new SessionRequest { Pack = entry.Pack, WaveSetId = args[2] }, out string error)) throw new InvalidOperationException(error);
                        return $"armed {launcher.Armed.Name} on {launcher.Armed.Host}";
                    }
                    case "disarm":
                        launcher.Disarm();
                        return "disarmed";
                    case "play":
                    {
                        // The pre-level menu's Play button.
                        var ui = UIFrameManager.instance;
                        var frame = Traverse.Create(ui).Field("levelSelectFrame").GetValue<UIFrame>();
                        if (ui.ActiveFrame != frame) throw new InvalidOperationException("the pre-level menu isn't open");
                        frame.GetComponentInChildren<LevelSelectUIFrameHelper>(true).TransitionToSelectedLevel();
                        return "play";
                    }
                    case "finish":
                    {
                        var gs = LocalGamestate.Instance ?? throw new InvalidOperationException("not in a level");
                        bool win = args.Length < 2 || args[1].ToLowerInvariant() != "lose";
                        // A forced result isn't a real run: mark it so its score record lists the assist.
                        GameplayImpact.Mark("Dev: forced match end");
                        gs.SetState(win ? LocalGamestate.State.AfterMatchVictory : LocalGamestate.State.AfterMatchDefeat);
                        return win ? "victory" : "defeat";
                    }
                    case "scores":
                        return "\n" + JsonConvert.SerializeObject(ScoreStore.All, Formatting.Indented);
                    case "state":
                    {
                        var c = launcher.Current;
                        var a = launcher.Armed;
                        var lp = LevelProgressManager.instance;
                        string host = c?.HostScene ?? a?.Host ?? LevelInteractor.lastActiveLevelInfo?.sceneName;
                        var ld = host != null ? lp?.GetLevelDataForScene(host) : null;
                        return $"session={(c == null ? "none" : $"{ScoreStore.Key(c)} on {c.HostScene}")}; armed={(a == null ? "none" : $"{a.Name} on {a.Host}")}; " +
                            $"scene={SceneManager.GetActiveScene().name}; frame={UIFrameManager.instance?.ActiveFrame?.name}; " +
                            $"levelData[{host}] best={ld?.highscoreBest} beatenBest={ld?.beatenBest}; " +
                            $"perk level={PerkManager.instance?.level} xp={PerkManager.instance?.xp}; overwriteSave={MatchSaveLoadHandler.OverwriteCurrentSave}; " +
                            $"impact={string.Join(", ", GameplayImpact.Reasons)}; equipped={string.Join(",", PerkManager.instance?.CurrentlyEquipped.Select(e => e?.name) ?? [])}";
                    }
                    default:
                        throw new ArgumentException("unknown builder-session subcommand");
                }
            });
        }
    }
}
