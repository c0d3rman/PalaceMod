using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using PalaceMod.Builder;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    Builder stage 2 (docs/builder-design.md, "Level overlays"): installs the level overlay, runs the one pathfinding
    rescan after a custom level starts, drives the layout editor, and registers its keys and the `builder-level` dev
    command. Part of the Builder feature: it follows Builder.Enabled.
*/
namespace PalaceMod
{
    public class BuilderLevelManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            BuilderServices.Overlay = new LevelOverlay();
            Keys.Register();
            LayoutMenus.HintShown = plugin.Config.Bind("Builder", "Layout hint shown", false,
                new ConfigDescription("Whether the layout editor's first-use hint has been shown.", null, new SettingTags { Hidden = true }));
            RegisterDevCommand();
        }

        public override void Update()
        {
            LayoutEditor.Update();
            if (LevelOverlay.ScanAfterFrame >= 0 && Time.frameCount >= LevelOverlay.ScanAfterFrame) ScanAfterLevelStart();
        }

        // Once per custom level start, after Start has run (slots placed, removed ones gone): rebuild the graphs with
        // the new props and check every ground spawn line still reaches the castle.
        private static void ScanAfterLevelStart()
        {
            LevelOverlay.ScanAfterFrame = -1;
            var scene = LayoutScene.Host(LevelOverlay.ScanHost);
            if (!scene.IsValid() || !scene.isLoaded || AstarPath.active == null) return;
            try
            {
                long ms = PathCheck.Rescan();
                var routes = PathCheck.Check(scene);
                Log.For<BuilderManager>().Info($"level start rescan took {ms} ms; paths: {PathCheck.Describe(routes)}");
                var blocked = routes.Where(r => !r.Reachable).Select(r => r.Line).ToList();
                if (blocked.Count > 0)
                    Notify.Show($"This layout blocks enemies from {string.Join(", ", blocked)}: they can't reach the castle", NotifyKind.Warning, 8f, "builder-paths");
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Error("Rescan after the layout failed", e);
            }
        }

        private void RegisterDevCommand()
        {
            DevCommands.Register("builder-level",
                "builder-level edit <pack> <level> | state | add slot <type|slot> <x> <z> [yaw] | add prop <n|mesh> <x> <z> | select <key> | move <key> <x> <z> | rotate <key> <deg> | remove <key> | cost <key> <level> <gold|base> | cursor <x> <z>|off | tool <id>|none | click | cam <x> <z> [zoom] [yaw] | menu | details | check | play | leave | json | spike ...   level layout editor", args =>
            {
                string sub = args.FirstOrDefault()?.ToLowerInvariant();
                switch (sub)
                {
                    case "edit":
                    {
                        string pack = Arg(args, 1), level = Arg(args, 2);
                        BuilderEditor.OpenPack(pack);
                        var draft = BuilderEditor.Draft ?? throw new InvalidOperationException("no draft");
                        if (draft.Pack.FindLevel(level) == null) throw new ArgumentException($"no level {level} in {pack}");
                        BuilderEditor.CloseAll();
                        if (!LayoutEditor.Launch(draft, level, out string error)) throw new InvalidOperationException(error);
                        return "launching the layout editor";
                    }
                    case "new":
                    {
                        // A user pack with one empty level on a map (test setup).
                        string id = Arg(args, 1), scene = Arg(args, 2);
                        var pack = new Builder.Model.DesignPack { Id = id, Name = "Layout " + id, Author = "dev" };
                        pack.Levels.Add(new Builder.Model.LevelDef { Id = Builder.Model.Ids.FromName(scene + "-layout"), Name = scene + " Layout", Host = scene });
                        BuilderServices.Packs.Save(pack);
                        BuilderServices.Packs.Refresh();
                        return $"saved {id} with level {pack.Levels[0].Id}";
                    }
                    case "state":
                        return LayoutEditor.Describe();
                    case "add":
                    {
                        RequireActive();
                        var p = new Vector3(F(args, 3), 0f, F(args, 4));
                        p = LayoutScene.Ground(p);
                        float yaw = args.Length > 5 ? F(args, 5) : 0f;
                        if (Arg(args, 1) == "slot")
                        {
                            string what = Arg(args, 2).Replace('_', ' ');
                            string baseName = LayoutEditor.Natives.Any(n => n.Name == what) ? what : LayoutEditor.NearestBase(what, p)
                                ?? throw new ArgumentException($"no slot or slot kind \"{what}\" (kinds: {string.Join(", ", LayoutEditor.Natives.Where(n => n.CanBase).Select(n => n.Type).Distinct())})");
                            return LayoutEditor.AddSlot(baseName, p, yaw) ?? throw new InvalidOperationException("not added");
                        }
                        string prop = Arg(args, 2).Replace('_', ' ');
                        var b = int.TryParse(prop, out int i) && i < LayoutEditor.PropBases.Count ? LayoutEditor.PropBases[i]
                            : LayoutEditor.PropBases.FirstOrDefault(x => x.Id == prop || x.Id == "mesh:" + prop || x.Name == prop)
                            ?? throw new ArgumentException($"no prop \"{prop}\" (props: {string.Join(", ", LayoutEditor.PropBases.Select(x => x.Name))})");
                        return LayoutEditor.AddProp(b.Id, p, yaw) ?? throw new InvalidOperationException("not added");
                    }
                    case "select":
                        RequireActive();
                        LayoutEditor.Select(Item(args, 0).Key);
                        return "selected " + LayoutEditor.Selected?.Key;
                    case "move":
                    {
                        var item = Item(args, 2);
                        LayoutEditor.Move(item, LayoutScene.Ground(new Vector3(F(args, args.Length - 2), item.Pos.y, F(args, args.Length - 1))), item.Yaw);
                        return $"{item.Key} at {item.Pos}";
                    }
                    case "rotate":
                    {
                        var item = Item(args, 1);
                        LayoutEditor.Rotate(item, F(args, args.Length - 1));
                        return $"{item.Key} yaw {item.Yaw}";
                    }
                    case "remove":
                    {
                        var item = Item(args, 0);
                        LayoutEditor.RemoveOrRestore(item);
                        return $"{item.Key} removed={item.Removed}";
                    }
                    case "cost":
                    {
                        var item = Item(args, 2);
                        int level = int.Parse(args[args.Length - 2]);
                        LayoutEditor.SetCost(item, level, args[args.Length - 1] == "base" ? null : int.Parse(args[args.Length - 1]));
                        return $"{item.Key} costs {string.Join("/", item.BaseCosts.Select((c, n) => LayoutEditor.CostOverride(item, n) ?? c))}";
                    }
                    case "cursor":
                        LayoutEditor.DevCursor = Arg(args, 1) == "off" ? null : new Vector3(F(args, 1), 0f, F(args, 2));
                        return "cursor " + (LayoutEditor.DevCursor?.ToString() ?? "mouse");
                    case "click":
                        RequireActive();
                        LayoutEditor.DevClick = true;
                        return "clicked";
                    case "tool":
                        RequireActive();
                        if (Arg(args, 1) == "none") LayoutEditor.CancelTool();
                        else LayoutEditor.StartTool(string.Join(" ", args.Skip(1)));
                        return "tool " + (LayoutEditor.Tool ?? "-");
                    case "cam":
                        RequireActive();
                        LayoutEditor.SetCamera(new Vector3(F(args, 1), 0f, F(args, 2)), args.Length > 3 ? F(args, 3) : null, args.Length > 4 ? F(args, 4) : null);
                        return "camera moved";
                    case "menu":
                        RequireActive();
                        LayoutMenus.OpenMain();
                        return "menu open";
                    case "details":
                        RequireActive();
                        LayoutMenus.OpenSelected();
                        return "details open";
                    case "check":
                        RequireActive();
                        return LayoutEditor.CheckPaths();
                    case "play":
                        RequireActive();
                        LayoutEditor.Play();
                        return BuilderServices.InSession ? "playing" : "not launched";
                    case "leave":
                        LayoutEditor.Leave();
                        return "left";
                    case "json":
                    {
                        var lv = LayoutEditor.Level ?? throw new InvalidOperationException("not editing");
                        return "\n" + Newtonsoft.Json.JsonConvert.SerializeObject(lv, Newtonsoft.Json.Formatting.Indented,
                            new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
                    }
                    case "paths":
                    {
                        var scene = LayoutScene.Host(BuilderServices.Sessions.Current?.HostScene);
                        return PathCheck.Describe(PathCheck.Check(scene));
                    }
                    case "spike":
                        return LevelSpike.Run(args.Length > 1 ? args[1] : "");
                    default:
                        throw new ArgumentException("see `help` for builder-level usage");
                }
            });

            static string Arg(string[] args, int i) => args.Length > i ? args[i] : throw new ArgumentException("missing argument");
            static float F(string[] args, int i) => float.Parse(Arg(args, i), System.Globalization.CultureInfo.InvariantCulture);
            static void RequireActive()
            {
                if (!LayoutEditor.Active) throw new InvalidOperationException("the layout editor isn't running (builder-level edit <pack> <level>)");
            }
            // The key is everything between the subcommand and the trailing numbers (names have spaces).
            static LayoutEditor.Item Item(string[] args, int trailing)
            {
                RequireActive();
                string key = string.Join(" ", args.Skip(1).Take(args.Length - 1 - trailing));
                if (key.Length == 0) throw new ArgumentException("missing item key");
                return LayoutEditor.Items.TryGetValue(key, out var item) ? item
                    : LayoutEditor.Items.Values.FirstOrDefault(x => x.Id == key) ?? throw new ArgumentException($"no item \"{key}\" (keys look like slot:<name>, add:<id>, prop:<id>, line:<name>)");
            }
        }
    }
}

namespace PalaceMod.Builder.Level
{
    /// <summary>The layout editor's keys (Builder section, so they follow Builder.Enabled).</summary>
    internal static class Keys
    {
        public static Keybind Menu, Rotate, RotateBack, Delete, Properties, Snap, TurnLeft, TurnRight;

        public static void Register()
        {
            const string s = "Builder";
            Menu = Keybinds.Register(s, "layout-menu", "Layout: menu", new KeyboardShortcut(KeyCode.B), "In the layout editor: opens the menu to add slots and scenery, save, check paths and play.");
            Rotate = Keybinds.Register(s, "layout-rotate", "Layout: turn", new KeyboardShortcut(KeyCode.Period), "In the layout editor: turns the selected object (or the one being placed) by 15°.");
            RotateBack = Keybinds.Register(s, "layout-rotate-back", "Layout: turn back", new KeyboardShortcut(KeyCode.Comma), "In the layout editor: turns it the other way.");
            Delete = Keybinds.Register(s, "layout-delete", "Layout: remove", new KeyboardShortcut(KeyCode.Delete), "In the layout editor: removes (or restores) the selected object.");
            Properties = Keybinds.Register(s, "layout-details", "Layout: details", new KeyboardShortcut(KeyCode.I), "In the layout editor: shows the selected object's costs and options.");
            Snap = Keybinds.Register(s, "layout-snap", "Layout: snap", new KeyboardShortcut(KeyCode.G), "In the layout editor: turns snapping to the 1 m grid on or off.");
            TurnLeft = Keybinds.Register(s, "layout-view-left", "Layout: turn view left", new KeyboardShortcut(KeyCode.K), "In the layout editor: turns the camera.");
            TurnRight = Keybinds.Register(s, "layout-view-right", "Layout: turn view right", new KeyboardShortcut(KeyCode.L), "In the layout editor: turns the camera the other way.");
        }

        public static string Label(Keybind k)
        {
            if (k == null) return "?";
            var sc = k.Shortcut;
            string Name(KeyCode c) => c switch
            {
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftCommand or KeyCode.RightCommand => "Cmd",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                KeyCode.Delete => "Del",
                KeyCode.Comma => ",",
                KeyCode.LeftBracket => "[",
                KeyCode.RightBracket => "]",
                KeyCode.Period => ".",
                _ => c.ToString(),
            };
            if (sc.MainKey == KeyCode.None) return "(unbound)";
            return string.Join("+", sc.Modifiers.Select(Name).Concat([Name(sc.MainKey)]));
        }
    }
}
