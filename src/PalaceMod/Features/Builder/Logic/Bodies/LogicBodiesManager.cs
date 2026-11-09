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
            LogicServices.Factory = EntityFactory.Instance;
            LogicServices.ExtraCandidates = (c, r, into) => VanillaProjectiles.Query(c.ToVector3(), r, into);
            LogicWorld.Polled += (w, t) => { corePolledAt = Time.time; Poll(t); };
            // Cleared when a world ends, never when one starts: Started fires after the level's places are built, and
            // clearing then dropped every placed walkable deck, surface and space before its scan.
            LogicWorld.Ending += _ => { SessionChanged(); cleared = true; };
            GroundGraphs.Rasterizing += DeckOpenings.Apply;
            // A level whose templates have walkable bodies may make decks at runtime: tiled ground graphs keep that cheap.
            LogicWorld.Started += w => { if (w.Templates.Values.Any(t => (t?["body"] as Newtonsoft.Json.Linq.JObject)?.Value<bool?>("walkable") == true)) Walkables.PrepareRuntimeDecks(); };
            BodyActions.Register();
            LogicSaveState.Register("bodies", BodySave.Capture, BodySave.Restore);
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
                    "interact" => BodyDev.Interact(args.Skip(1).ToArray()),
                    "focus" => BodyDev.Focus(),
                    "pick" => BodyChoices.Pick(int.Parse(args.ElementAtOrDefault(1) ?? "0")),
                    _ => "logic-l2 status | nav ... | act ... | build ... | signal ...",
                };
            });
        }

        private static float corePolledAt = -10f;
        private float pollTimer;
        private double pollTime;
        private object world;

        public override void Update()
        {
            NavSpike.Tick();
            var w = LogicWorld.Current;
            if (w != world)
            {
                // A world replaced without Ending (dev worlds): drop the old one's bodies state. A world that ended
                // was cleared then, and the new one's places may already be registered.
                if (world != null && !cleared) SessionChanged();
                else ResetPolling();
                world = w;
                cleared = false;
            }
            if (w == null) return;
            // Cuts (solid bodies, decks, surfaces) only reach the graphs while the updater runs on an interval.
            BodyNav.UseMoverRate();
            Walkables.Tick();
            Walkables.KingTick();
            Surfaces.Tick();
            // 10 Hz steps of game time: the core's (LogicWorld.Polled), or our own for a dev world it doesn't drive.
            if (Time.time - corePolledAt < 0.5f) return;
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

        private bool cleared;

        private void ResetPolling()
        {
            pollTimer = 0f;
            pollTime = 0;
        }

        private void SessionChanged()
        {
            Surfaces.Clear();
            Walkables.Clear();
            Spaces.Clear();
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

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.GET_LOCIDENTIFIER_CHOICENAME))]
        private static class ChoiceName
        {
            private static bool Prefix(Choice choice, ref string __result)
            {
                if (!BodyChoices.TryKey(choice, out var key)) return true;
                __result = key;
                return false;
            }
        }

        // ---- surfaces ------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(GroundGraphs), nameof(GroundGraphs.Scan))]
        private static class Rescanned
        {
            private static void Postfix() => Surfaces.Rescanned();
        }

        [HarmonyPatch(typeof(GroundGraphs), nameof(GroundGraphs.UpdateArea))]
        private static class AreaRescanned
        {
            private static void Postfix() => Surfaces.Rescanned();
        }

        [HarmonyPatch(typeof(PathfindMovementEnemy), "Start")]
        private static class EnemyStart
        {
            // Before Start copies the seeker's graphs into its masks and snaps to the navmesh: a unit spawned in a
            // space must be able to snap there.
            private static void Prefix(Component __instance) { if (LogicWorld.Current != null && Spaces.Any) Spaces.ApplySeeker(__instance.gameObject); }

            private static void Postfix(PathfindMovementEnemy __instance)
            {
                if (LogicWorld.Current == null) return;
                if (Spaces.Any) Spaces.Apply(__instance.gameObject);
                if (Surfaces.Any) Surfaces.Apply(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(PathfindMovementPlayerunit), "Start")]
        private static class PlayerUnitStart
        {
            // Before Start copies the seeker's graphs into its masks and snaps to the navmesh: a unit spawned in a
            // space must be able to snap there.
            private static void Prefix(Component __instance) { if (LogicWorld.Current != null && Spaces.Any) Spaces.ApplySeeker(__instance.gameObject); }

            private static void Postfix(PathfindMovementPlayerunit __instance)
            {
                if (LogicWorld.Current == null) return;
                if (Spaces.Any) Spaces.Apply(__instance.gameObject);
                if (Surfaces.Any) Surfaces.Apply(__instance.gameObject);
            }
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
