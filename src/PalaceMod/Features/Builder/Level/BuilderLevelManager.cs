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
    rescan after a custom level starts, and registers the `builder-level` dev command. The level Studio (where levels
    are edited) is StudioManager. Part of the Builder feature: it follows Builder.Enabled.
*/
namespace PalaceMod
{
    public class BuilderLevelManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            BuilderServices.Overlay = new LevelOverlay();
            RegisterDevCommand();
        }

        public override void Update()
        {
            if (LevelOverlay.ScanAfterFrame >= 0 && Time.frameCount >= LevelOverlay.ScanAfterFrame) ScanAfterLevelStart();
        }

        // Once per custom level start, after Start has run (slots placed, removed ones gone): rebuild the graphs with
        // the new props, check every ground spawn line still reaches the castle, and put unit homes on walkable ground.
        private static void ScanAfterLevelStart()
        {
            LevelOverlay.ScanAfterFrame = -1;
            var scene = LayoutScene.Host(LevelOverlay.ScanHost);
            if (!scene.IsValid() || !scene.isLoaded || AstarPath.active == null) return;
            try
            {
                long ms = PathCheck.Rescan();
                var routes = PathCheck.Check(scene);
                // On the graphs the level plays on: barracks units standing in water or on a cliff get walkable homes.
                UnitHomes.Settle(scene);
                Log.For<BuilderManager>().Info($"level start rescan took {ms} ms; paths: {PathCheck.Describe(routes)}; {UnitHomes.LastSettle}");
                var blocked = routes.Where(r => !r.Reachable).Select(r => r.Line).ToList();
                if (blocked.Count > 0)
                    Notify.Show($"This layout blocks enemies from {string.Join(", ", blocked)}: they can't reach the castle", NotifyKind.Warning, 8f, "builder-paths");
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Error("Rescan after the layout failed", e);
            }
        }

        // `builder-level` is kept for its graph and spike tools; its editing subcommands are aliases of `builder-studio`.
        private void RegisterDevCommand()
        {
            DevCommands.Register("builder-level",
                "builder-level paths | graphs | spike ... | <any builder-studio subcommand>   level tools (editing: see builder-studio)", args =>
            {
                string sub = args.FirstOrDefault()?.ToLowerInvariant();
                switch (sub)
                {
                    case "paths":
                    {
                        var scene = LayoutScene.Host(BuilderServices.Sessions.Current?.HostScene);
                        return PathCheck.Describe(PathCheck.Check(scene));
                    }
                    case "graphs":
                        return "\n" + PathCheck.DescribeGraphs() + "\n" + string.Join("\n", PathCheck.Check(Builder.Studio.StudioShell.Editing ? Builder.Studio.StudioScene.Scene : LayoutScene.Host(BuilderServices.Sessions.Current?.HostScene)).Select(r => $"{r.Line} on {r.Graph}: {r.Detail}"));
                    case "spike":
                        return LevelSpike.Run(args.Length > 1 ? args[1] : "");
                    default:
                        return Builder.Studio.StudioDev.Run(args);
                }
            });
        }
    }
}
