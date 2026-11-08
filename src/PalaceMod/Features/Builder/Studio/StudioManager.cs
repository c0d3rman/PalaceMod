using System;
using System.Globalization;
using System.Linq;
using PalaceMod.Builder;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio;
using UnityEngine;

/*
    Runs the Builder Studio (Features/Builder/Studio): its keys, input ownership, per-frame loop and the
    `builder-studio` dev command. Part of the Builder feature: it follows Builder.Enabled.
*/
namespace PalaceMod
{
    public class StudioManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            StudioKeys.Register();
            Studio.Shell = StudioShell.Instance;
            Studio.ModeChanged += StudioShell.OnModeChanged;
            // While the dock is the menu on top, only the Studio's keys act (Backspace deletes; Undo Upgrade's doesn't).
            Keybinds.ClaimExclusive(StudioContexts.Edit, () => Studio.Mode == StudioMode.Edit && StudioDock.Active);
            DevCommands.Register("builder-studio", Usage, StudioDev.Run);
        }

        public override void Update() => StudioShell.Update();

        internal const string Usage =
            "builder-studio open <pack> <level> | new <pack> <scene> [cleared] | state | dock | select <key...> | add <key...> | clear | tool <id> | arm slot <type>|prop <name>|lib <id> " +
            "| cursor <x> <z>|off | click | drag <sx> <sy> <ex> <ey> [left|right|middle] [shift|ctrl|alt] | dragw <x1> <z1> <x2> <z2> [button] [mods] | rclick <x> <z> | press <tile|row> " +
            "| cam <x> <z> [zoom] [yaw] | delete | dup | copy | paste <x> <z> | group | ungroup | frame | snap <m> | cost <level> <gold|base> | labels | issues | check | audit (place every palette kind, compare with its ghost) | play [start] | leave | json   the level Studio";
    }
}

namespace PalaceMod.Builder.Studio
{
    internal static class StudioDev
    {
        public static string Run(string[] args)
        {
            string sub = args.FirstOrDefault()?.ToLowerInvariant();
            switch (sub)
            {
                case "open":
                case "edit":
                {
                    string pack = Arg(args, 1), level = Arg(args, 2);
                    BuilderEditor.OpenPack(pack);
                    var draft = BuilderEditor.Draft ?? throw new InvalidOperationException("no draft");
                    if (draft.Pack.FindLevel(level) == null) throw new ArgumentException($"no level {level} in {pack}");
                    if (!Studio.Open(draft, level, out string error)) throw new InvalidOperationException(error);
                    return "opening the Studio";
                }
                case "new":
                {
                    string id = Arg(args, 1), scene = Arg(args, 2);
                    var pack = new DesignPack { Id = id, Name = "Studio " + id, Author = "dev" };
                    pack.Levels.Add(new LevelDef { Id = Ids.FromName(scene + "-studio"), Name = scene + " Studio", Host = scene, Base = args.Length > 3 && args[3] == "cleared" ? LevelBases.Cleared : null });
                    BuilderServices.Packs.Save(pack);
                    BuilderServices.Packs.Refresh();
                    return $"saved {id} with level {pack.Levels[0].Id}";
                }
                case "state": return StudioShell.Describe();
                case "dock": return StudioDock.Describe();
                case "audit": Require(); return StudioAudit.Run();
                case "select":
                case "add":
                case "toggle":
                    Require();
                    StudioSelection.Select(Keys(args, 1), sub == "select" ? SelectOp.Replace : sub == "add" ? SelectOp.Add : SelectOp.Toggle);
                    return "selected " + string.Join(", ", StudioSelection.Keys);
                case "clear": StudioSelection.Clear(); return "cleared";
                case "tool":
                    Require();
                    StudioShell.SetTool(Arg(args, 1));
                    return "tool " + StudioShell.Tool?.Id;
                case "arm":
                {
                    Require();
                    string what = string.Join(" ", args.Skip(2)).Replace('_', ' ');
                    switch (Arg(args, 1))
                    {
                        case "slot":
                            if (!StudioScene.Natives.Any(n => n.CanBase && n.Type == what)) throw new ArgumentException("kinds: " + string.Join(", ", StudioScene.Natives.Where(n => n.CanBase).Select(n => n.Type).Distinct()));
                            StudioPalette.ArmSlot(what);
                            break;
                        case "prop":
                            var b = StudioScene.PropBases.FirstOrDefault(x => x.Name == what || x.Id == what || x.Id == "mesh:" + what) ?? throw new ArgumentException("props: " + string.Join(", ", StudioScene.PropBases.Select(x => x.Name)));
                            StudioShell.Arm(StudioPalette.PropKind(b));
                            break;
                        default:
                            var browser = StudioServices.AssetBrowser ?? throw new InvalidOperationException("no library");
                            var item = browser.Query(null, null).FirstOrDefault(x => x.Id == what || x.Name == what) ?? throw new ArgumentException("no library item " + what);
                            StudioPalette.ArmLibrary(browser, item);
                            break;
                    }
                    return "armed " + StudioShell.Place.Armed?.Id;
                }
                case "cursor":
                    StudioShell.DevCursor = Arg(args, 1) == "off" ? null : new Vector3(F(args, 1), 0f, F(args, 2));
                    return "cursor " + (StudioShell.DevCursor?.ToString() ?? "mouse");
                case "click":
                    Require();
                    StudioShell.DevClick = true;
                    return "clicked";
                case "drag":
                {
                    Require();
                    var a = new Vector2(F(args, 1), F(args, 2));
                    var b = new Vector2(F(args, 3), F(args, 4));
                    Gesture(a, b, args.Skip(5).ToArray());
                    return $"dragging {a} -> {b}";
                }
                case "dragw":
                {
                    Require();
                    var a = StudioShell.WorldToScreen(StudioScene.Ground(new Vector3(F(args, 1), 0f, F(args, 2)))) ?? throw new InvalidOperationException("off screen");
                    var b = StudioShell.WorldToScreen(StudioScene.Ground(new Vector3(F(args, 3), 0f, F(args, 4)))) ?? throw new InvalidOperationException("off screen");
                    Gesture(a, b, args.Skip(5).ToArray());
                    return $"dragging {a} -> {b}";
                }
                case "rclick":
                {
                    Require();
                    var a = StudioShell.WorldToScreen(StudioScene.Ground(new Vector3(F(args, 1), 0f, F(args, 2)))) ?? throw new InvalidOperationException("off screen");
                    StudioInput.Click(a, 1);
                    return $"right click at {a}";
                }
                case "press": Require(); return StudioDock.Press(string.Join(" ", args.Skip(1)));
                case "cam":
                    Require();
                    StudioCamera.Set(new Vector3(F(args, 1), StudioCamera.Pivot.y, F(args, 2)), args.Length > 3 ? F(args, 3) : null, args.Length > 4 ? F(args, 4) : null);
                    return "camera moved";
                case "delete": Require(); StudioEdits.Delete(StudioSelection.Keys.ToList()); return StudioShell.Describe();
                case "dup": Require(); StudioShell.DuplicateAndGrab(); return StudioShell.Describe();
                case "copy": Require(); return $"copied {StudioEdits.Copy(StudioSelection.Items)}";
                case "paste": Require(); StudioShell.PasteAtCursor(new Vector3(F(args, 1), 0f, F(args, 2))); return StudioShell.Describe();
                case "group": Require(); return StudioEdits.Group(StudioSelection.Keys) ?? "not grouped";
                case "ungroup": Require(); StudioEdits.Ungroup(StudioSelection.Keys); return "ungrouped";
                case "frame": Require(); StudioShell.FrameSelection(); return "framed";
                case "snap": StudioShell.SnapStep = F(args, 1); return "snap " + StudioShell.SnapStep;
                case "cost":
                {
                    Require();
                    int level = int.Parse(Arg(args, 1));
                    StudioEdits.SetCost(StudioSelection.Items.ToList(), level, Arg(args, 2) == "base" ? null : int.Parse(args[2]));
                    return string.Join("; ", StudioSelection.Items.Select(i => $"{i.Key} {string.Join("/", Enumerable.Range(0, i.BaseCosts.Count).Select(l => StudioEdits.Cost(i, l)))}"));
                }
                case "labels":
                    return $"labels mode {StudioViewOptions.Labels}";
                case "issues": return string.Join("\n", StudioDock.LevelIssues());
                case "check": Require(); StudioShell.CheckPaths(); return StudioShell.PathSummary ?? "no routes";
                case "play": Require(); StudioShell.Play(Arg(args, 1, "") != "start"); return "play requested";
                case "leave": Studio.Leave(); return "left";
                case "json":
                {
                    var lv = Studio.Level ?? throw new InvalidOperationException("not in the Studio");
                    return "\n" + Newtonsoft.Json.JsonConvert.SerializeObject(lv, Newtonsoft.Json.Formatting.Indented,
                        new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
                }
                default:
                    throw new ArgumentException("usage: " + PalaceMod.StudioManager.Usage);
            }
        }

        private static void Gesture(Vector2 a, Vector2 b, string[] rest)
        {
            int button = rest.Contains("right") ? 1 : rest.Contains("middle") ? 2 : 0;
            StudioInput.Drag(a, b, button, 12, rest.Contains("shift"), rest.Contains("ctrl"), rest.Contains("alt"));
        }

        private static System.Collections.Generic.IEnumerable<string> Keys(string[] args, int from)
        {
            foreach (var k in args.Skip(from))
            {
                if (StudioScene.Find(k) != null) yield return k;
                else if (StudioScene.Items.FirstOrDefault(i => i.Id == k || i.Name == k.Replace('_', ' ')) is { } item) yield return item.Key;
                else throw new ArgumentException($"no item \"{k}\" (keys look like slot:<name>, add:<id>, prop:<id>, line:<name>, group:<id>)");
            }
        }

        private static void Require()
        {
            if (!StudioShell.Editing) throw new InvalidOperationException("the Studio isn't editing (builder-studio open <pack> <level>)");
        }

        private static string Arg(string[] args, int i, string fallback = null) => args.Length > i ? args[i] : fallback ?? throw new ArgumentException("missing argument");
        private static float F(string[] args, int i) => float.Parse(Arg(args, i), CultureInfo.InvariantCulture);
    }
}
