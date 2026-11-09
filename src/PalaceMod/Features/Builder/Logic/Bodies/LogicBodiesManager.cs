using System.Linq;
using HarmonyLib;
using Pathfinding.Graphs.Navmesh;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;

/*
    The behaviour engine's bodies (docs/builder-engine.md, workstream L2): the entity factory, projectiles (Builder and
    the game's own), movers, areas, pickups, interactables, walkable surfaces and per-unit walkability. The other L2
    parts (Stats, Steering, World verbs) have their own managers; the seams are in Logic/Bodies/L2Shared.cs.
    Infrastructure: no settings of its own; it acts only while a logic world runs (Builder sessions).
*/
namespace PalaceMod
{
    public class LogicBodiesManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            Launcher.Launch = BodyLaunch.Launch;
            Projectiles.Redirect = BodyLaunch.Redirect;
            LogicServices.Areas = AreaPoller.Instance;
            BodyActions.Register();
            DevCommands.Register("logic-l2", "logic-l2 status | nav ... | act <action> [json] [self=<role>] [other=<role>] | build <template> <x> <z> [rotY] | signal <entity> <input> [value]   bodies (L2): spikes, actions with literal params, entities", args =>
            {
                string sub = args.FirstOrDefault() ?? "";
                return sub switch
                {
                    "nav" => NavSpike.Run(args.Skip(1).ToArray()),
                    "act" => DevAct.Run(args.Skip(1).ToArray()),
                    "status" => BodyDev.Status(),
                    "build" => BodyDev.Build(args.Skip(1).ToArray()),
                    "signal" => BodyDev.Signal(args.Skip(1).ToArray()),
                    "events" => BodyDev.Events(),
                    "vp" => BodyDev.Vanilla(args.Skip(1).ToArray()),
                    _ => "logic-l2 status | nav ... | act ... | build ... | signal ...",
                };
            });
        }

        private float pollTimer;
        private double pollTime;
        private object world;

        public override void Update()
        {
            NavSpike.Tick();
            var w = LogicWorld.Current;
            if (w != world)
            {
                world = w;
                SessionChanged();
            }
            if (w == null) return;
            Walkables.Tick();
            Surfaces.Tick();
            // 10 Hz steps of game time (until the core drives Polled).
            pollTimer += Time.deltaTime;
            while (pollTimer >= 0.1f)
            {
                pollTimer -= 0.1f;
                pollTime += 0.1;
                Poll(pollTime);
            }
        }

        internal static void Poll(double time)
        {
            AreaPoller.Step(time);
            foreach (var p in BuilderPickup.All.ToList()) if (p != null) p.Poll();
            BodyDev.FieldStep();
        }

        private void SessionChanged()
        {
            Surfaces.Clear();
            Walkables.Clear();
            VanillaProjectiles.Clear();
            Drifts.Clear();
            BodyFilter.ClearCache();
            pollTimer = 0f;
            pollTime = 0;
        }

        // ---- the game's projectiles ----------------------------------------------------------------------------

        [HarmonyPatch(typeof(AimbotProjectile), nameof(AimbotProjectile.Fire))]
        private static class ProjectileFired
        {
            private static void Postfix(AimbotProjectile __instance) { if (LogicWorld.Current != null) VanillaProjectiles.Fired(__instance); }
        }

        [HarmonyPatch(typeof(AimbotProjectile), "Update")]
        private static class ProjectileUpdate
        {
            private static void Prefix(AimbotProjectile __instance) { if (LogicWorld.Current != null) VanillaProjectiles.BeforeUpdate(__instance); }
            private static void Postfix(AimbotProjectile __instance) { if (LogicWorld.Current != null) VanillaProjectiles.AfterUpdate(__instance); }
        }

        [HarmonyPatch(typeof(AimbotProjectile), "DestroyProjectile")]
        private static class ProjectileGone
        {
            private static void Prefix(AimbotProjectile __instance) => VanillaProjectiles.Released(__instance);
        }

        // ---- surfaces ------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(GroundGraphs), nameof(GroundGraphs.Scan))]
        private static class Rescanned
        {
            private static void Postfix() => Surfaces.Rescanned();
        }

        [HarmonyPatch(typeof(PathfindMovementEnemy), "Start")]
        private static class EnemyStart
        {
            private static void Postfix(PathfindMovementEnemy __instance) { if (LogicWorld.Current != null && Surfaces.Any) Surfaces.Apply(__instance.gameObject); }
        }

        [HarmonyPatch(typeof(PathfindMovementPlayerunit), "Start")]
        private static class PlayerUnitStart
        {
            private static void Postfix(PathfindMovementPlayerunit __instance) { if (LogicWorld.Current != null && Surfaces.Any) Surfaces.Apply(__instance.gameObject); }
        }

        // A seeker limited to some tags may find its target behind tags it can't cross: walk as near as it can instead
        // of failing (a failed path keeps the unit on its stale one, through the forbidden area).
        [HarmonyPatch(typeof(Pathfinding.Seeker), "StartPathInternal")]
        private static class PartialWhenTagged
        {
            private static void Prefix(Pathfinding.Seeker __instance, Pathfinding.Path p)
            {
                if (__instance.traversableTags != -1 && p is Pathfinding.ABPath ab) ab.calculatePartial = true;
            }
        }

        [HarmonyPatch(typeof(NavmeshUpdates.NavmeshUpdateSettings), nameof(NavmeshUpdates.NavmeshUpdateSettings.ReloadDirtyTilesImmediately))]
        private static class TimeCutReload
        {
            private static void Prefix() { NavSpike.CutTime.Start(); NavSpike.CutReloads++; }
            private static void Postfix() => NavSpike.CutTime.Stop();
        }
    }
}
