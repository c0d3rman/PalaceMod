using System.Linq;
using HarmonyLib;
using Pathfinding.Graphs.Navmesh;
using PalaceMod.Builder.Logic.Bodies;

/*
    The behaviour engine's world verbs (docs/builder-engine.md, workstream L2): bodies (projectiles, movers, areas,
    pickups, interactables, attacks), statuses and stats, steering, and the patches on the game's own projectiles and
    pathfinding they need. Infrastructure: no settings of its own; it acts only in Builder sessions.
*/
namespace PalaceMod
{
    public class LogicBodiesManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("logic-l2", "logic-l2 nav ...   world-verb spikes and checks (L2)", args =>
            {
                string sub = args.FirstOrDefault() ?? "";
                return sub switch
                {
                    "nav" => NavSpike.Run(args.Skip(1).ToArray()),
                    _ => "logic-l2 nav ...",
                };
            });
        }

        public override void Update()
        {
            NavSpike.Tick();
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
