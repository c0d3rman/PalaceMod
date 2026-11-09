using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Seams between L2's parts (docs/builder-engine.md, L2), so each can be built on its own:
    - Bodies (LogicBodiesManager): entity factory, projectiles (Builder and vanilla), movers, areas, pickups,
      interactables, walkable surfaces and surface tags; installs Launcher.
    - Stats (LogicStatsManager): stat modifiers, statuses and flags, the damage/heal hooks; installs StatusFlags.
    - Steering (LogicSteeringManager): targeting and movement overrides, attacks, drift on units, convert/replace.
    - World verbs (LogicWorldVerbsManager): gold, coins, buildings, waves, end, and the Show actions.
    Each part writes the game's own fields; the others read the game's fields (e.g. Stats writes
    PathfindMovementEnemy.movementSpeed, Steering's own movement modes read it).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    /// <summary>Lifecycle events every L2 part can hook.</summary>
    internal static class BodyEvents
    {
        /// <summary>A unit (Builder template or vanilla prefab clone) got its LogicEntity, with its template JSON in
        /// <c>e.Def</c> when it's a Builder unit (targeting, movement, attacks, statuses, surfaces...). Raised before
        /// the `spawn` event is dispatched.</summary>
        public static event Action<LogicEntity, GameObject> UnitSpawned;
        public static void RaiseUnitSpawned(LogicEntity e, GameObject go)
        {
            try { UnitSpawned?.Invoke(e, go); }
            catch (Exception ex) { Log.For<LogicBodiesManager>().Error($"UnitSpawned handler failed for {e?.Id}", ex); }
        }

        /// <summary>Any entity the factory built (projectile, prop, area, pickup, logic, unit), after its components.</summary>
        public static event Action<LogicEntity, GameObject> EntityBuilt;
        public static void RaiseEntityBuilt(LogicEntity e, GameObject go)
        {
            try { EntityBuilt?.Invoke(e, go); }
            catch (Exception ex) { Log.For<LogicBodiesManager>().Error($"EntityBuilt handler failed for {e?.Id}", ex); }
        }
    }

    /// <summary>Status flags as other parts see them (Stats installs <see cref="Has"/>).</summary>
    internal static class StatusFlags
    {
        /// <summary>Whether the object (a unit, building or Builder body) currently has the flag ("stunned",
        /// "rooted", "disarmed", "untargetable", "invulnerable", "silenced", "noIncome").</summary>
        public static Func<GameObject, string, bool> Has = (_, _) => false;
    }

    /// <summary>
    /// Continuous velocities on entities (`drift` action and status mod): wind, currents, pulls. Keyed per entity;
    /// different keys add up, the same key replaces. Bodies applies them to projectiles, Steering to units.
    /// </summary>
    internal static class Drifts
    {
        internal sealed class Drift
        {
            public string Key;
            /// <summary>Velocity in m/s, re-evaluated by the owner each poll (toward/from a moving thing).</summary>
            public Func<Vector3> Velocity;
            public double Until;              // world time; double.PositiveInfinity = until removed
        }

        private static readonly Dictionary<LogicEntity, List<Drift>> drifts = [];

        public static void Set(LogicEntity e, string key, Func<Vector3> velocity, double seconds)
        {
            if (e == null) return;
            if (!drifts.TryGetValue(e, out var list)) drifts[e] = list = [];
            list.RemoveAll(d => d.Key == key);
            double now = LogicWorld.Current?.Time ?? 0;
            list.Add(new Drift { Key = key ?? Guid.NewGuid().ToString("N"), Velocity = velocity, Until = seconds > 0 ? now + seconds : double.PositiveInfinity });
        }

        public static void Remove(LogicEntity e, string key)
        {
            if (e != null && drifts.TryGetValue(e, out var list)) list.RemoveAll(d => d.Key == key);
        }

        /// <summary>The summed drift velocity now (expired ones dropped).</summary>
        public static Vector3 Velocity(LogicEntity e)
        {
            if (e == null || !drifts.TryGetValue(e, out var list) || list.Count == 0) return Vector3.zero;
            double now = LogicWorld.Current?.Time ?? 0;
            var v = Vector3.zero;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Until < now) { list.RemoveAt(i); continue; }
                try { v += list[i].Velocity(); } catch { list.RemoveAt(i); }
            }
            return v;
        }

        /// <summary>Drop every drift of an entity (it's gone).</summary>
        public static void Forget(LogicEntity e) { if (e != null) drifts.Remove(e); }

        public static bool Any => drifts.Count > 0;
        public static IEnumerable<LogicEntity> Drifting => drifts.Keys;
        public static void Clear() => drifts.Clear();
    }

    /// <summary>Team changes of live units (Steering installs it; `convert` uses the same code).</summary>
    internal static class Teams
    {
        /// <summary>Make a live unit fight for another side ("player" | "enemy" | "neutral"). Returns the object that
        /// now carries the entity (a replacement when the side's brain differs), or null with an error. The factory
        /// calls it when `spawn` asks for a team other than the template's side.</summary>
        public static Func<LogicEntity, GameObject, string, (GameObject go, string error)> Convert =
            (_, go, _) => (null, "team changes aren't available");
    }

    /// <summary>Projectiles in flight, Builder and vanilla (Bodies installs it).</summary>
    internal static class Projectiles
    {
        /// <summary>Send a projectile entity at a new target entity or point (taunt on a projectile, deflection).
        /// Returns false when the entity isn't a projectile in flight.</summary>
        public static Func<LogicEntity, LogicEntity, Vector3?, bool> Redirect = (_, _, _) => false;
    }

    /// <summary>What `launch` and attacks with `launch` ask Bodies for.</summary>
    internal sealed class LaunchRequest
    {
        public string Template;               // an entities[] template id with a projectile component (or any template)
        public LogicEntity Owner;             // launcher (team "owner", hit credit)
        public Vector3 From;
        public LogicEntity Target;            // homing target, or null
        public Vector3? Toward;               // a point (when Target is null)
        public JObject Overrides;             // projectile fields to override ("speed", "homing", "pierce", ...)
        public string Team;                   // null = the template's (or the owner's for "owner")
    }

    internal static class Launcher
    {
        /// <summary>Installed by Bodies. Returns the launched entity, or null with an error.</summary>
        public static Func<LaunchRequest, (LogicEntity e, string error)> Launch = _ => (null, "projectiles aren't available");
    }
}
