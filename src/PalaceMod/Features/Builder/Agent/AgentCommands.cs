using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The `agent` command: what agent-kit/palace.py drives through the Agent bridge (or `dev.py cmd` in a dev launch).
    Every result is JSON (one object), so a script can read it; failures throw with a one-line reason. Files (catalog,
    map dumps, pictures) are written to a path the caller gives, since results travel through small response files.

    Positions are world coordinates in metres: x east, z north (as in a pack's "pos"), y up. "Night n" is 1-based.
*/
namespace PalaceMod.Builder.Agent
{
    internal static class AgentCommands
    {
        private static readonly Log log = Log.For<AgentBridgeManager>();

        private const string Usage =
            "agent status | slots | catalog <file> | host <file> [cell] | packs | reload | validate <pack> | " +
            "play <pack> <level> | sandbox <pack> <level> [x z] | infinitegold on|off | spawn <unit> [count] [elite] [line <name>|at <x> <z>] | vanilla <scene> | menu | night [n] | endnight | report | build <slot|all> [level] [branch] [pay] | " +
            "gold <n> | god on|off | invulnerable on|off | speed <x> | kill | heal | teleport <x> <z> | paths | " +
            "capture <file> [pixels] | screenshot <file>   agent bridge: JSON state, level tests, map dumps (see agent-kit/)";

        internal static void Register() => DevCommands.Register("agent", Usage, Run);

        /// <summary>Per frame while the bridge (or a dev launch) is running: records nights.</summary>
        internal static void Tick() => NightRecorder.Tick();

        private static string Run(string[] args)
        {
            if (args.Length == 0) throw new ArgumentException("usage: " + Usage);
            string sub = args[0].ToLowerInvariant();
            switch (sub)
            {
                case "status": return Json(Status());
                case "slots": return Json(new JObject { ["slots"] = LiveSlots() });
                case "catalog":
                {
                    string path = PathArg(args, 1);
                    var catalog = BuilderServices.Catalog as Catalog ?? throw new InvalidOperationException("the Builder catalog isn't loaded");
                    File.WriteAllText(path, catalog.ToJson());
                    return Json(new JObject { ["written"] = path, ["units"] = catalog.Units.Count, ["hosts"] = catalog.Hosts.Count });
                }
                case "host":
                {
                    string path = PathArg(args, 1, out float? option);
                    float cell = option ?? 2f;
                    var dump = MapDump.Take(Mathf.Clamp(cell, 1f, 8f));
                    File.WriteAllText(path, dump.ToString(Formatting.None));
                    return Json(new JObject { ["written"] = path, ["scene"] = dump["scene"], ["slots"] = ((JArray)dump["slots"]).Count });
                }
                case "packs":
                    BuilderServices.Packs.Refresh();
                    return Json(new JObject
                    {
                        ["root"] = BuilderServices.Packs.Root,
                        ["packs"] = new JArray(BuilderServices.Packs.Packs.Select(p => new JObject
                        {
                            ["id"] = p.Id, ["name"] = p.Pack?.Name, ["source"] = p.Source.ToString(), ["folder"] = p.Folder,
                            ["errors"] = p.Issues.Count(i => i.Severity == Severity.Error), ["warnings"] = p.Issues.Count(i => i.Severity == Severity.Warning),
                            ["levels"] = new JArray((p.Pack?.Levels ?? []).Select(l => l?.Id)),
                            ["waveSets"] = new JArray((p.Pack?.WaveSets ?? []).Select(w => w?.Id)),
                        })),
                    });
                case "reload":
                    BuilderServices.Packs.Refresh();
                    return Json(new JObject { ["packs"] = BuilderServices.Packs.Packs.Count });
                case "validate":
                {
                    var entry = FindPack(Arg(args, 1));
                    return Json(new JObject
                    {
                        ["pack"] = entry.Id,
                        ["ok"] = !entry.HasErrors,
                        ["issues"] = new JArray(entry.Issues.Select(i => new JObject
                        {
                            ["severity"] = i.Severity == Severity.Error ? "error" : "warning", ["path"] = i.Path, ["message"] = i.Message,
                        })),
                    });
                }
                case "play":
                {
                    var entry = FindPack(Arg(args, 1));
                    if (entry.HasErrors) throw new InvalidOperationException($"{entry.Id} has errors; run `agent validate {entry.Id}`");
                    string level = Arg(args, 2);
                    if (entry.Pack.FindLevel(level) == null) throw new ArgumentException($"{entry.Id} has no level \"{level}\"");
                    if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = entry.Pack, LevelId = level }, out string error))
                        throw new InvalidOperationException(error);
                    NightRecorder.Reset();
                    return Json(new JObject { ["loading"] = $"{entry.Id}/{level}" });
                }
                case "sandbox":
                {
                    // Studio test play: unlimited gold, sandbox panel; the king starts at (x, z) if given.
                    var entry = FindPack(Arg(args, 1));
                    string level = Arg(args, 2);
                    if (entry.Pack.FindLevel(level) == null) throw new ArgumentException($"{entry.Id} has no level \"{level}\"");
                    Vector3? at = args.Length >= 5 ? new Vector3(F(args[3]), 0f, F(args[4])) : null;
                    if (!Studio.Play.StudioPlay.Instance.PlayLevel(new Editor.PackDraft(entry), level, at))
                        throw new InvalidOperationException("test play didn't start (see `agent status` and the log)");
                    NightRecorder.Reset();
                    return Json(new JObject { ["loading"] = $"{entry.Id}/{level}", ["sandbox"] = true });
                }
                case "infinitegold":
                    Studio.Play.SandboxActions.InfiniteGold = OnOff(args);
                    return Json(new JObject { ["infiniteGold"] = Studio.Play.SandboxActions.InfiniteGold, ["appliesIn"] = "sandbox play only" });
                case "spawn":
                {
                    // spawn <unit> [count] [elite] [line <name...> | at <x> <z>]: unit is "vanilla:E Melee" (spaces as _) or a pack unit id.
                    if (!DebugActions.InLevel) throw new InvalidOperationException("not in a level");
                    int i = 2, count = 1;
                    if (args.Length > i && int.TryParse(args[i], out int c)) { count = Mathf.Clamp(c, 1, 200); i++; }
                    bool elite = args.Length > i && args[i] == "elite";
                    if (elite) i++;
                    Vector3? at = null;
                    string line = null;
                    if (args.Length > i + 1 && args[i] == "line") line = string.Join(" ", args.Skip(i + 1));
                    else if (args.Length > i + 2 && args[i] == "at")
                        at = LayoutScene.Ground(new Vector3(F(args[i + 1]), PlayerInteraction.instance.transform.position.y, F(args[i + 2])));
                    else at = PlayerInteraction.instance.transform.position + new Vector3(10f, 0f, 10f);
                    string unit = Arg(args, 1).Replace('_', ' ');
                    var pack = BuilderServices.Sessions?.Current?.Pack;
                    return Json(new JObject { ["result"] = Studio.Play.SandboxActions.Spawn(pack, unit, count, elite, at, line) });
                }
                case "vanilla":
                {
                    string scene = string.Join(" ", args.Skip(1));
                    if (scene.Length == 0) throw new ArgumentException("usage: agent vanilla <scene>");
                    if (BuilderServices.InSession) BuilderServices.Sessions.End();
                    var transitions = SceneTransitionManager.instance ?? throw new InvalidOperationException("the game isn't ready");
                    if (transitions.SceneTransitionIsRunning) throw new InvalidOperationException("a scene is loading; try again");
                    transitions.TransitionFromNullToLevel(scene);
                    NightRecorder.Reset();
                    return Json(new JObject { ["loading"] = scene });
                }
                case "menu":
                {
                    if (BuilderServices.InSession) BuilderServices.Sessions.End();
                    var transitions = SceneTransitionManager.instance ?? throw new InvalidOperationException("the game isn't ready");
                    if (!transitions.SceneTransitionIsRunning) transitions.TransitionToMainMenu();
                    return Json(new JObject { ["loading"] = "_StartMenu" });
                }
                case "night":
                {
                    if (args.Length > 1) DebugActions.JumpToNight(int.Parse(args[1], CultureInfo.InvariantCulture));
                    string text = DebugActions.StartNight();
                    return Json(new JObject { ["started"] = text, ["night"] = DebugActions.CurrentNight, ["nights"] = DebugActions.NightCount });
                }
                case "endnight": return Json(new JObject { ["result"] = DebugActions.EndNight() });
                case "report": return Json(NightRecorder.Report());
                case "build": return Json(Build(args));
                case "gold": return Json(new JObject { ["result"] = DebugActions.SetGold(int.Parse(Arg(args, 1), CultureInfo.InvariantCulture)) });
                case "god": return Json(new JObject { ["result"] = DebugActions.SetGodMode(OnOff(args)) });
                case "invulnerable": return Json(new JObject { ["result"] = DebugActions.SetInvulnerableKingdom(OnOff(args)) });
                case "speed": return Json(new JObject { ["result"] = DebugActions.SetSpeed(F(Arg(args, 1))) });
                case "kill": return Json(new JObject { ["result"] = DebugActions.KillEnemies() });
                case "heal": return Json(new JObject { ["result"] = DebugActions.HealAll() });
                case "teleport":
                {
                    if (!DebugActions.InLevel || PlayerMovement.instance == null) throw new InvalidOperationException("not in a level");
                    var p = LayoutScene.Ground(new Vector3(F(Arg(args, 1)), PlayerMovement.instance.transform.position.y, F(Arg(args, 2))));
                    PlayerMovement.instance.TeleportTo(p);
                    return Json(new JObject { ["king"] = V(p) });
                }
                case "paths": return Json(Paths(rescan: args.Length > 1 && args[1] == "rescan"));
                case "capture":
                {
                    string path = PathArg(args, 1, out float? option);
                    int pixels = (int)(option ?? 1600);
                    return Json(MapCapture.Take(path, Mathf.Clamp(pixels, 256, 4096)));
                }
                case "screenshot":
                {
                    string path = PathArg(args, 1);
                    ScreenCapture.CaptureScreenshot(path);
                    return Json(new JObject { ["writing"] = path });
                }
                default:
                    throw new ArgumentException("usage: " + Usage);
            }
        }

        // ---------------------------------------------------------------------------------------------- state

        private static JObject Status()
        {
            var o = new JObject
            {
                ["scene"] = SceneManager.GetActiveScene().name,
                ["loading"] = SceneTransitionManager.instance != null && SceneTransitionManager.instance.SceneTransitionIsRunning,
                ["inLevel"] = DebugActions.InLevel,
            };
            var session = BuilderServices.Sessions?.Current;
            o["session"] = session == null ? null : new JObject
            {
                ["pack"] = session.Pack?.Id, ["level"] = session.Level?.Id, ["waveSet"] = session.WaveSet?.Id, ["host"] = session.HostScene,
            };
            o["sandbox"] = Studio.Play.StudioPlay.InPlay;
            if (LocalGamestate.Instance != null) o["state"] = LocalGamestate.Instance.CurrentState.ToString();
            if (!DebugActions.InLevel)
            {
                o["report"] = NightRecorder.Summary();
                return o;
            }
            bool night = DebugActions.IsNight;
            o["time"] = night ? "night" : DayNightCycle.Instance.AfterSunrise ? "day" : "dawn";
            o["night"] = DebugActions.CurrentNight;
            o["nights"] = DebugActions.NightCount;
            o["gold"] = DebugActions.Gold;
            o["speed"] = DebugActions.Speed;
            o["godMode"] = DebugActions.GodMode;
            o["invulnerable"] = DebugActions.InvulnerableKingdom;
            var castle = CastleHp();
            o["castle"] = castle == null ? null : new JObject { ["hp"] = R(castle.HpValue), ["maxHp"] = R(castle.maxHp), ["built"] = CastleCenter.instance != null && CastleCenter.instance.IsCastleBuilt };
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<Hp>() : null;
            if (king != null) o["king"] = new JObject { ["hp"] = R(king.HpValue), ["maxHp"] = R(king.maxHp), ["knockedOut"] = king.KnockedOut, ["pos"] = V(king.transform.position) };
            var buildings = BuiltSlots().ToList();
            o["buildings"] = new JObject
            {
                ["built"] = buildings.Count,
                ["knockedOut"] = buildings.Count(s => BuildingHp(s)?.KnockedOut == true),
                ["income"] = buildings.Sum(s => s.GoldIncome),
            };
            var units = TagManager.instance.PlayerUnits.Where(u => u != null && u.Hp != null).ToList();
            o["units"] = new JObject { ["alive"] = units.Count(u => !u.Hp.KnockedOut), ["total"] = units.Count };
            o["enemies"] = TagManager.instance.EnemyUnits.Count(e => e != null && e.Hp != null && !e.Hp.KnockedOut);
            o["report"] = NightRecorder.Summary();
            return o;
        }

        internal static Hp CastleHp()
        {
            if (LocalGamestate.Instance == null) return null;
            var list = Traverse.Create(LocalGamestate.Instance).Field("objectsThatTriggerLoseWhenDestroyed").GetValue<List<Hp>>();
            return list?.FirstOrDefault(h => h != null);
        }

        internal static IEnumerable<BuildSlot> BuiltSlots()
        {
            var scene = EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : SceneManager.GetActiveScene();
            return LayoutScene.Slots(scene).Where(s => s.gameObject.activeInHierarchy && s.Level > 0 && !s.ActivatorUpgradesThis);
        }

        internal static Hp BuildingHp(BuildSlot slot) => slot.BuildingParent != null ? slot.BuildingParent.GetComponent<Hp>() : null;

        private static JArray LiveSlots()
        {
            var scene = EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : SceneManager.GetActiveScene();
            var arr = new JArray();
            foreach (var s in LayoutScene.Slots(scene))
            {
                var hp = BuildingHp(s);
                var o = new JObject
                {
                    ["name"] = s.name, ["kind"] = s.buildingName, ["pos"] = V(s.transform.position),
                    ["available"] = s.gameObject.activeInHierarchy, ["level"] = s.Level, ["maxLevel"] = s.Upgrades.Count,
                    ["state"] = s.State.ToString(), ["income"] = s.GoldIncome,
                };
                if (s.ActivatorUpgradesThis) o["followsActivator"] = s.ActivatorBuilding != null ? s.ActivatorBuilding.name : null;
                if (s.gameObject.activeInHierarchy && s.Level < s.Upgrades.Count) o["nextCost"] = s.Upgrades[s.Level].cost;
                if (hp != null && s.Level > 0) { o["hp"] = R(hp.HpValue); o["maxHp"] = R(hp.maxHp); o["knockedOut"] = hp.KnockedOut; }
                arr.Add(o);
            }
            return arr;
        }

        // ---------------------------------------------------------------------------------------------- building

        /// <summary>build &lt;slot|all&gt; [level] [branch] [pay]: builds or upgrades a slot to a level (default: one
        /// level up). Choices take the branch (0-based, default 0). "pay" charges the cost like a player would and stops
        /// when gold runs out; otherwise it's free.</summary>
        private static JObject Build(string[] args)
        {
            string target = Arg(args, 1);
            bool pay = args.Contains("pay");
            var rest = args.Skip(2).Where(a => a != "pay").ToList();
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
                return new JObject { ["result"] = DebugActions.BuildAllBlueprints() };
            var slot = FindSlot(target) ?? throw new ArgumentException($"no build slot \"{target}\" (`agent slots` lists them)");
            if (!slot.gameObject.activeInHierarchy) throw new InvalidOperationException($"{slot.name} isn't available yet (its requirement isn't met)");
            int to = rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : slot.Level + 1;
            int branch = rest.Count > 1 ? int.Parse(rest[1], CultureInfo.InvariantCulture) : 0;
            to = Mathf.Clamp(to, 0, slot.Upgrades.Count);
            int spent = 0;
            string stopped = null;
            while (slot.Level < to)
            {
                var up = slot.Upgrades[slot.Level];
                if (pay)
                {
                    if (DebugActions.Gold < up.cost) { stopped = $"not enough gold for level {slot.Level + 1} ({up.cost})"; break; }
                }
                if (!slot.CanBeUpgraded) { stopped = $"can't go past level {slot.Level} (needs a higher castle level, or it's maxed)"; break; }
                int before = slot.Level;
                int b = up.upgradeBranches.Count > 1 ? Mathf.Clamp(branch, 0, up.upgradeBranches.Count - 1) : 0;
                DebugActions.Upgrade(slot, up.upgradeBranches.Count > 1 ? b : 0);
                if (slot.Level == before) { stopped = "the upgrade didn't happen"; break; }
                if (pay)
                {
                    DebugActions.AddGold(-up.cost);
                    spent += up.cost;
                }
            }
            return new JObject { ["slot"] = slot.name, ["level"] = slot.Level, ["spent"] = spent, ["gold"] = DebugActions.Gold, ["stopped"] = stopped };
        }

        /// <summary>A slot by GameObject name, or an added slot by its id in the level.</summary>
        internal static BuildSlot FindSlot(string name)
        {
            var scene = EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : SceneManager.GetActiveScene();
            var slots = LayoutScene.Slots(scene);
            if (name.Equals("castle", StringComparison.OrdinalIgnoreCase)) return slots.FirstOrDefault(LayoutScene.IsCastle);
            if (name.StartsWith("add:")) name = name.Substring(4);
            // Commands split on spaces, so a host slot name's spaces may come as underscores ("House_Variant_(3)").
            string spaced = name.Replace('_', ' ');
            return slots.FirstOrDefault(s => s.name == LayoutScene.AddedPrefix + name)
                ?? slots.FirstOrDefault(s => s.name == name)
                ?? slots.FirstOrDefault(s => s.name == spaced)
                ?? slots.FirstOrDefault(s => string.Equals(s.name, spaced, StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------------------------------------- paths

        private static JObject Paths(bool rescan)
        {
            var scene = EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : SceneManager.GetActiveScene();
            long ms = rescan ? PathCheck.Rescan() : 0;
            var routes = PathCheck.Check(scene, withPoints: true);
            return new JObject
            {
                ["rescanMs"] = ms,
                ["castle"] = PathCheck.Castle(scene) is Vector3 c ? V(c) : null,
                ["routes"] = new JArray(routes.Select(r => new JObject
                {
                    ["line"] = r.Line, ["graph"] = r.Graph, ["reachable"] = r.Reachable,
                    ["length"] = R(Length(r.Points)),
                    ["points"] = new JArray(r.Points.Select(p => new JArray(R(p.x), R(p.z)))),
                })),
            };
        }

        private static float Length(List<Vector3> pts)
        {
            float sum = 0;
            for (int i = 1; i < pts.Count; i++) sum += Vector3.Distance(pts[i - 1], pts[i]);
            return sum;
        }

        // ---------------------------------------------------------------------------------------------- helpers

        internal static PackEntry FindPack(string id)
        {
            BuilderServices.Packs.Refresh();
            var entry = BuilderServices.Packs.Find(id) ?? throw new ArgumentException($"no pack \"{id}\" (`agent packs` lists them)");
            if (entry.Pack == null) throw new InvalidOperationException($"{entry.Id} can't be read: {string.Join("; ", entry.Issues.Select(i => i.ToString()))}");
            return entry;
        }

        private static string Json(JObject o) => o.ToString(Formatting.None);

        private static string Arg(string[] args, int i) =>
            args.Length > i ? args[i] : throw new ArgumentException($"missing argument {i}; usage: {Usage}");

        /// <summary>A file path argument: the rest of the line, so paths with spaces work.</summary>
        private static string PathArg(string[] args, int i) => PathArg(args, i, out _, allowOption: false);

        /// <summary>A file path argument followed by an optional number (the last word, if it's numeric).</summary>
        private static string PathArg(string[] args, int i, out float? option, bool allowOption = true)
        {
            if (args.Length <= i) throw new ArgumentException("missing file path");
            option = null;
            int end = args.Length;
            if (allowOption && args.Length > i + 1 && float.TryParse(args[args.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float n))
            {
                option = n;
                end--;
            }
            string path = Path.GetFullPath(string.Join(" ", args.Skip(i).Take(end - i)));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }

        private static bool OnOff(string[] args) => Arg(args, 1).ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => throw new ArgumentException("on or off"),
        };

        internal static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        internal static float R(float f) => (float)Math.Round(f, 2);
        internal static JArray V(Vector3 v) => [R(v.x), R(v.y), R(v.z)];
    }
}
