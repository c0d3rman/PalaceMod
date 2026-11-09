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
            GroundGraphs.RunPendingScan();
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

        // A gold mine a layout adds (a copy of a host slot or the standard building) has a building Hp that's never been
        // set up when the mine is built: on the map's own slots the level load leaves it set up, on a copy its
        // TaggedObject only appears in Hp.Start, the frame after the build at the earliest. MineShaft.Update reads
        // buildingHP.TaggedObj every frame once built, so a build that runs before it in a frame (the agent bridge's
        // `build`, the sandbox's) threw a NullReferenceException. Skip that frame.
        [HarmonyLib.HarmonyPatch(typeof(MineShaft), "Update")]
        private static class MineShaft_Update
        {
            private static bool Prefix(MineShaft __instance)
            {
                if (__instance.MyBuildSlot == null || __instance.MyBuildSlot.Level <= 0) return true;
                var hp = __instance.buildingInteractor != null ? __instance.buildingInteractor.buildingHP : null;
                if (hp != null && hp.TaggedObj != null) return true;
                Log.For<BuilderManager>().Debug($"Skipped a frame of {__instance.name}'s mine shaft: its building {(hp == null ? "has no Hp" : "isn't set up yet")}");
                return false;
            }
        }

        // `builder-level` is kept for its graph and spike tools; its editing subcommands are aliases of `builder-studio`.
        private void RegisterDevCommand()
        {
            DevCommands.Register("builder-level",
                "builder-level paths | graphs | spike ... | buildings [kind] | mines | <any builder-studio subcommand>   level tools (editing: see builder-studio)", args =>
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
                    case "mines":
                    {
                        // Each mine shaft's building Hp: whether it's been set up (see MineShaft_Update).
                        static string Where(Component c) => c == null ? "null" : $"{c.transform.root.name}/{c.name} (active {c.gameObject.activeInHierarchy})";
                        return string.Join("\n", UnityEngine.Object.FindObjectsOfType<MineShaft>(true).Select(m =>
                        {
                            var hp = m.buildingInteractor != null ? m.buildingInteractor.buildingHP : null;
                            return $"{Where(m)}: level {(m.MyBuildSlot != null ? m.MyBuildSlot.Level : -1)}, hp {Where(hp)} set up={hp != null && hp.TaggedObj != null}";
                        }));
                    }
                    case "buildings":
                        return "\n" + (args.Length > 1 ? BuildingPrefabs.Diff(EnemySpawner.instance.gameObject.scene, args[1]) : BuildingPrefabs.Describe(EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : default));
                    default:
                        return Builder.Studio.StudioDev.Run(args);
                }
            });
        }
    }
}
