using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

/*
    Patrols: units walk a route of points the player drops, fight whatever comes near, then go back to the route.

    Controls follow the game's own king-centric commanding. While units follow the king, the patrol key drops a point
    at the king (an optional, unbound key drops one at the cursor). Placing the units as usual (Space, or right-click from unit placement)
    starts the patrol: the drop spot becomes the last point. Back and forth (StarCraft's convention) or loop.
    Dropping a point, or placing the units, back near the first point closes the route into a loop whatever the Route
    setting says (the near-duplicate point is merged into the first).
    Picking the units up again, or making them hold position, ends their patrol.

    How units are driven: the game's unit AI (PathfindMovementPlayerunit) walks to HomePosition and, once it has
    reached home, attacks any enemy within its target range of where it stands, then walks home again. A patrol just
    moves each unit's HomePosition from point to point once the unit has arrived and isn't fighting. Engaging and
    resuming are the game's own behaviour. The only patch is a postfix on the placement method, to know which units
    were placed where, and one on the dawn formation reset.

    Knocked-out units are revived in place by their building, so their patrol carries on after a respawn.
*/
namespace PalaceMod
{
    public enum PatrolRouteMode
    {
        BackAndForth,
        Loop,
    }

    public enum PatrolMarkerMode
    {
        Always,
        FaintAtNight,
        WhileCommanding,
        Hidden,
    }

    public class PatrolManager : BaseManager
    {
        public override string FeatureSection => "Patrols";
        public override string FeatureDescription =>
            "Units walk a route you set and fight what they meet on the way. While leading units, drop patrol points, then place the units to start.";

        private ConfigEntry<PatrolRouteMode> routeMode;
        private ConfigEntry<PatrolMarkerMode> markerMode;
        private ConfigEntry<bool> hints;

        private Keybind pointKey, cursorPointKey;

        /// <summary>A route and the units walking it.</summary>
        internal sealed class Route
        {
            public readonly List<Vector3> Points = [];
            public bool Loop;
            public int Units;
        }

        private sealed class Patroller
        {
            public Route Route;
            public Vector3 Offset;
            public int Index;
            public int Direction = 1;
            public float LegTime;
            /// <summary>Where the unit was placed. It stays there for the first moments, so a hold-position
            /// command (holding the Command key, Ctrl-placing) still holds it at the spot the player chose.</summary>
            public Vector3 PlacedHome;
            public bool Started;
        }

        private readonly Dictionary<PathfindMovementPlayerunit, Patroller> patrollers = [];
        private readonly List<Route> routes = [];
        private readonly List<Vector3> pending = [];
        /// <summary>The pending points were closed into a loop by dropping a point back at the first one.</summary>
        private bool pendingLoop;
        private readonly PatrolMarkers markers = new();

        private const float ArriveDistance = 2.5f;
        private const float MergeDistance = 3f;
        private const float MaxOffset = 3.5f;
        /// <summary>A point (or the drop spot) this close to the first point closes the route into a loop.</summary>
        private const float CloseLoopDistance = 4.5f;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var config = plugin.Config;
            routeMode = config.Bind(FeatureSection, "Route", PatrolRouteMode.BackAndForth, new ConfigDescription(
                "How units walk an open patrol route with three or more points: back and forth along it, or around it in a loop. Ending a route back at its first point always makes a loop."));
            markerMode = config.Bind(FeatureSection, "Route markers", PatrolMarkerMode.FaintAtNight, new ConfigDescription(
                "Shows patrol routes on the ground: always, faint at night, only while you lead units, or never."));
            hints = config.Bind(FeatureSection, "Hints", true, new ConfigDescription(
                "Shows a short reminder of the patrol keys the first time you lead units each session."));

            pointKey = Keybinds.Register(FeatureSection, "point", "Add patrol point", new KeyboardShortcut(KeyCode.P),
                "While leading units, drops a patrol point where the king stands; place the units to start the patrol. Hold to clear the points, or (not leading) to stop every patrol.");
            // Unbound by default: the game already uses middle-click (Command Units) and right-click (unit placement).
            cursorPointKey = Keybinds.Register(FeatureSection, "cursor-point", "Add patrol point at cursor", KeyboardShortcut.Empty,
                "While leading units, drops a patrol point under the mouse cursor.");

            RegisterDevCommands();
            SaveStates.RegisterState("patrols", CaptureState, RestoreState);
        }

        // ---- Savestates -------------------------------------------------------------------------------------------

        internal sealed class SavedPatrols
        {
            public List<SavedRoute> routes = [];
            public List<V3> pending = [];
        }

        internal sealed class SavedRoute
        {
            public List<V3> points = [];
            public bool loop;
            public List<SavedPatroller> units = [];
        }

        internal sealed class SavedPatroller
        {
            public string unit;
            public V3 offset, placedHome;
            public int index, direction = 1;
            public float legTime;
            public bool started;
        }

        private SavedPatrols CaptureState()
        {
            if (routes.Count == 0 && pending.Count == 0) return null;
            var saved = new SavedPatrols { pending = pending.Select(p => (V3)p).ToList() };
            foreach (var route in routes)
            {
                var r = new SavedRoute { points = route.Points.Select(p => (V3)p).ToList(), loop = route.Loop };
                foreach (var (unit, p) in patrollers)
                {
                    if (unit == null || p.Route != route) continue;
                    r.units.Add(new SavedPatroller
                    {
                        unit = SaveStates.PathOf(unit), offset = p.Offset, placedHome = p.PlacedHome,
                        index = p.Index, direction = p.Direction, legTime = p.LegTime, started = p.Started,
                    });
                }
                if (r.units.Count > 0) saved.routes.Add(r);
            }
            return saved;
        }

        // The reload dropped every patrol (the units are new objects); hand the captured ones to the same units.
        private void RestoreState(SavedPatrols saved)
        {
            if (!IsEnabled) return;
            Reset();
            pending.AddRange(saved.pending.Select(p => (Vector3)p));
            var units = SaveStates.FindByPath<PathfindMovementPlayerunit>();
            int restored = 0;
            foreach (var r in saved.routes)
            {
                var route = new Route { Loop = r.loop };
                route.Points.AddRange(r.points.Select(p => (Vector3)p));
                if (route.Points.Count < 2) continue;
                foreach (var s in r.units)
                {
                    if (s.unit == null || !units.TryGetValue(s.unit, out var unit) || patrollers.ContainsKey(unit)) continue;
                    patrollers[unit] = new Patroller
                    {
                        Route = route, Offset = s.offset, PlacedHome = s.placedHome,
                        Index = Mathf.Clamp(s.index, 0, route.Points.Count - 1), Direction = s.direction >= 0 ? 1 : -1,
                        LegTime = s.legTime, Started = s.started,
                    };
                    route.Units++;
                    if (s.started) unit.HasReachedHomePositionAlready = true;
                }
                if (route.Units > 0)
                {
                    routes.Add(route);
                    restored += route.Units;
                }
            }
            if (restored > 0) GameplayImpact.Mark("Unit patrols");
            Log.Debug($"Restored {routes.Count} patrol routes ({restored} units), {pending.Count} pending points from a savestate");
        }

        protected override void OnFeatureDisabled()
        {
            StopAll(keepPosition: true);
            ClearPending();
            markers.Hide();
        }

        public override void OnDestroy() => markers.Destroy();

        // ---- Input ------------------------------------------------------------------------------------------------

        private bool wasLeading, hintShown;
        private float pointHeldFor;
        private bool pointHoldDone;

        public override void Update()
        {
            var cu = CommandUnits.instance;
            if (cu == null || TagManager.instance == null)
            {
                // Left the level: every unit is gone.
                if (patrollers.Count > 0 || pending.Count > 0) Reset();
                wasLeading = false;
                markers.Hide();
                return;
            }

            bool leading = cu.commanding && cu.PlayerUnitsCommanding.Count > 0;
            if (leading && !wasLeading) OnStartLeading();
            if (!leading && wasLeading && pending.Count > 0)
            {
                // Stopped leading without placing anyone (all knocked out, king down): the points have no units.
                ClearPending();
            }
            wasLeading = leading;

            if (leading)
            {
                if (pointKey.Down) AddPoint(cu.transform.position, cu);
                if (cursorPointKey.Down && TryCursorWorld(cu, out var cursor)) AddPoint(cursor, cu);
            }
            if (HeldLong(cu))
            {
                if (leading && pending.Count > 0)
                {
                    ClearPending();
                    Notify.Show("Patrol points removed", key: "patrol");
                }
                else if (!leading && patrollers.Count > 0)
                {
                    int count = patrollers.Count;
                    StopAll(keepPosition: true);
                    Notify.Show(count == 1 ? "1 unit stopped patrolling" : $"{count} units stopped patrolling", key: "patrol");
                }
            }

            Drive();
            UpdateMarkers(leading);
        }

        /// <summary>True once when the patrol key has been held as long as the game's hold-to-hold-position press.</summary>
        private bool HeldLong(CommandUnits cu)
        {
            if (!pointKey.Held)
            {
                pointHeldFor = 0f;
                pointHoldDone = false;
                return false;
            }
            pointHeldFor += Time.deltaTime;
            if (pointHoldDone || pointHeldFor < cu.holdToHoldPositionTime) return false;
            pointHoldDone = true;
            return true;
        }

        private void OnStartLeading()
        {
            if (!hints.Value || hintShown || !Bound(pointKey)) return;
            hintShown = true;
            string cursor = Bound(cursorPointKey) ? $" ({KeyLabel(cursorPointKey)}: at cursor)" : "";
            Notify.Show($"{KeyLabel(pointKey)}: patrol point{cursor}, then place units to patrol. Hold {KeyLabel(pointKey)} to clear", seconds: 7f, key: "patrol-hint");
        }

        private void ClearPending()
        {
            pending.Clear();
            pendingLoop = false;
        }

        /// <summary>Whether a point here would close the pending route back onto its first point, making a loop of
        /// at least three points.</summary>
        private bool ClosesLoop(Vector3 position) =>
            pending.Count >= 3 && Flat(position - pending[0]).magnitude < CloseLoopDistance;

        private void AddPoint(Vector3 position, CommandUnits cu)
        {
            var units = cu.PlayerUnitsCommanding;
            if (units.Count > 0) position = units[0].GetNearestGroundPosition(position);
            if (!pendingLoop && ClosesLoop(position))
            {
                // Back at the start: merge into the first point and close the route.
                pendingLoop = true;
                if (cu.dropWaypointFx != null) cu.dropWaypointFx.Emit(cu.drowWaypointParticleCount / 3);
                Notify.Show($"Patrol: loop of {pending.Count} points. Place your units to start.", key: "patrol");
                Log.Info($"Patrol route closed into a {pending.Count}-point loop");
                return;
            }
            if (pendingLoop && Flat(position - pending[0]).magnitude < CloseLoopDistance)
            {
                Notify.Show("Patrol: already a loop. Place your units to start.", key: "patrol");
                return;
            }
            if (pending.Count > 0 && Flat(position - pending[pending.Count - 1]).magnitude < MergeDistance)
            {
                Notify.Show("Patrol point is too close to the last one", NotifyKind.Warning, key: "patrol");
                return;
            }
            pending.Add(position);
            if (cu.dropWaypointFx != null) cu.dropWaypointFx.Emit(cu.drowWaypointParticleCount / 3);
            Notify.Show(pending.Count == 1
                ? "Patrol point set. Add more, or place your units to patrol from there to here."
                : $"Patrol: {pending.Count} points. Place your units to start.", key: "patrol");
        }

        // ---- Starting a patrol ------------------------------------------------------------------------------------

        /// <summary>Called after the game placed units: turns pending points plus the drop spot into their route.</summary>
        private void OnPlaced(List<PathfindMovementPlayerunit> placed, bool? loop = null)
        {
            if (pending.Count == 0 || placed.Count == 0) return;
            var units = placed.Where(u => u != null).ToList();
            if (units.Count == 0) return;
            if (units.All(u => u.HoldPosition))
            {
                // A hold-position placement is a different command: the units stay put.
                ClearPending();
                Notify.Show("Holding position: patrol points dropped", key: "patrol");
                return;
            }
            units.RemoveAll(u => u.HoldPosition);

            Vector3 center = Vector3.zero;
            foreach (var unit in units) center += unit.HomePosition;
            center /= units.Count;

            // Placed back at the first point: that closes the loop too (the drop spot merges into the first point).
            bool closed = pendingLoop || ClosesLoop(center);
            var route = new Route { Loop = closed || (loop ?? routeMode.Value == PatrolRouteMode.Loop) };
            route.Points.AddRange(pending);
            ClearPending();
            // On a closed loop the drop spot isn't a point; otherwise it becomes the last one.
            if (!closed && Flat(center - route.Points[route.Points.Count - 1]).magnitude >= MergeDistance) route.Points.Add(center);
            if (route.Points.Count < 2)
            {
                Notify.Show("A patrol needs two points: drop one, then place units somewhere else", NotifyKind.Warning, key: "patrol");
                return;
            }
            // Two points have one path between them; "loop" only means something with three or more.
            if (route.Points.Count == 2) route.Loop = false;
            routes.Add(route);

            // Units start where they were dropped (the last point) and head back along the route. On a closed loop
            // they join it at the point nearest the drop spot and carry on around.
            int start = route.Points.Count - 1;
            if (closed)
            {
                int nearest = 0;
                for (int i = 1; i < route.Points.Count; i++)
                    if (Flat(route.Points[i] - center).sqrMagnitude < Flat(route.Points[nearest] - center).sqrMagnitude) nearest = i;
                start = (nearest - 1 + route.Points.Count) % route.Points.Count;
            }
            foreach (var unit in units)
            {
                Vector3 offset = Flat(unit.HomePosition - center);
                if (offset.magnitude > MaxOffset) offset = offset.normalized * MaxOffset;
                Remove(unit, keepPosition: false);
                var patroller = new Patroller { Route = route, Offset = offset, Index = start, PlacedHome = unit.HomePosition };
                patrollers[unit] = patroller;
                route.Units++;
                unit.HasReachedHomePositionAlready = true;
            }
            GameplayImpact.Mark("Unit patrols");
            string shape = route.Loop ? $"a loop of {route.Points.Count} points" : $"{route.Points.Count} points";
            Notify.Show($"{units.Count} {(units.Count == 1 ? "unit" : "units")} patrolling {shape}", NotifyKind.Success, key: "patrol");
            Log.Info($"{units.Count} units on a {route.Points.Count}-point {(route.Loop ? "loop" : "back-and-forth")} route");
        }

        /// <summary>Starts a patrol for the given units along the given points (for other features, e.g. squads).</summary>
        internal void Assign(IEnumerable<PathfindMovementPlayerunit> units, IList<Vector3> points, bool? loop = null)
        {
            if (points == null || points.Count < 2) throw new ArgumentException("a patrol needs at least two points");
            ClearPending();
            pending.AddRange(points.Take(points.Count - 1));
            var list = units.ToList();
            // Pretend the units were dropped at the last point, keeping their spread.
            Vector3 center = Vector3.zero;
            foreach (var unit in list) center += unit.transform.position;
            if (list.Count > 0) center /= list.Count;
            foreach (var unit in list) unit.HomePosition = points[points.Count - 1] + Flat(unit.transform.position - center);
            OnPlaced(list, loop);
        }

        // ---- Driving units ----------------------------------------------------------------------------------------

        private static readonly List<PathfindMovementPlayerunit> stale = [];
        private bool holdNoticeShown;

        private void Drive()
        {
            stale.Clear();
            foreach (var (unit, p) in patrollers)
            {
                if (unit == null) { stale.Add(unit); continue; }
                // A new command (picked up again, or told to hold position) ends the patrol.
                if (unit.FollowingPlayer || unit.HoldPosition)
                {
                    if (unit.HoldPosition && !holdNoticeShown)
                    {
                        holdNoticeShown = true;
                        Notify.Show("Holding position ends a patrol", key: "patrol");
                    }
                    stale.Add(unit);
                    continue;
                }
                // Knocked out: the building revives it in place, and it walks back to its patrol point.
                if (!unit.isActiveAndEnabled || unit.hp == null || unit.hp.KnockedOut) continue;

                if (!p.Started)
                {
                    // Wait out the game's hold-to-hold-position window at the placed spot before setting off.
                    p.LegTime += Time.deltaTime;
                    unit.HomePosition = p.PlacedHome;
                    float window = CommandUnits.instance != null ? CommandUnits.instance.holdToHoldPositionTime : 1f;
                    if (p.LegTime > window + 0.25f)
                    {
                        p.Started = true;
                        Advance(unit, p);
                    }
                    continue;
                }

                Vector3 target = Target(unit, p);
                // Keep re-asserting the point: something else (the dawn reset, other mods) may have moved home.
                unit.HomePosition = target;
                bool fighting = unit.SeekToTaggedObj != null;
                if (fighting) continue;

                p.LegTime += Time.deltaTime;
                float distance = Flat(unit.transform.position - target).magnitude;
                float leg = Flat(target - Previous(unit, p)).magnitude;
                // On short legs the arrival circles of both ends must not overlap, or the unit would flip every frame.
                float arrive = Mathf.Min(ArriveDistance, leg * 0.35f);
                // Give up on a point the unit can't reach (blocked, crowded) after about twice the walk.
                float patience = 6f + 2f * leg / Mathf.Max(0.5f, unit.movementSpeed);
                if (distance < arrive || p.LegTime > patience) Advance(unit, p);
            }
            foreach (var unit in stale) Remove(unit, keepPosition: false);
        }

        private static Vector3 Target(PathfindMovementPlayerunit unit, Patroller p) =>
            unit.GetNearestGroundPosition(p.Route.Points[p.Index] + p.Offset);

        private Vector3 Previous(PathfindMovementPlayerunit unit, Patroller p)
        {
            int count = p.Route.Points.Count;
            int prev = p.Route.Loop ? (p.Index - 1 + count) % count : Mathf.Clamp(p.Index - p.Direction, 0, count - 1);
            return p.Route.Points[prev] + p.Offset;
        }

        private static void Advance(PathfindMovementPlayerunit unit, Patroller p)
        {
            int count = p.Route.Points.Count;
            if (p.Route.Loop) p.Index = (p.Index + 1) % count;
            else
            {
                if (p.Index + p.Direction >= count || p.Index + p.Direction < 0) p.Direction = -p.Direction;
                p.Index += p.Direction;
            }
            p.LegTime = 0f;
            unit.HomePosition = Target(unit, p);
            // Arrived somewhere: from now on the game lets the unit fight on the way instead of walking home first.
            unit.HasReachedHomePositionAlready = true;
            // Ask for a path now instead of standing at the old point until the next periodic repath.
            if (unit.isActiveAndEnabled) unit.OriginalPathRequest();
        }

        private void Remove(PathfindMovementPlayerunit unit, bool keepPosition)
        {
            if (!patrollers.TryGetValue(unit, out var p)) return;
            patrollers.Remove(unit);
            if (keepPosition && unit != null && !unit.FollowingPlayer)
            {
                // Stay where it is now, like a unit placed there.
                unit.HomePosition = unit.GetNearestGroundPosition(unit.transform.position);
            }
            if (--p.Route.Units <= 0) routes.Remove(p.Route);
        }

        private void StopAll(bool keepPosition)
        {
            foreach (var unit in patrollers.Keys.ToList()) Remove(unit, keepPosition);
            routes.Clear();
        }

        private void Reset()
        {
            patrollers.Clear();
            routes.Clear();
            ClearPending();
        }

        internal bool IsPatrolling(PathfindMovementPlayerunit unit) => unit != null && patrollers.ContainsKey(unit);

        // ---- Markers ----------------------------------------------------------------------------------------------

        private void UpdateMarkers(bool leading)
        {
            var mode = markerMode.Value;
            bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Day;
            float alpha = mode switch
            {
                PatrolMarkerMode.Hidden => 0f,
                PatrolMarkerMode.WhileCommanding => leading ? 1f : 0f,
                PatrolMarkerMode.FaintAtNight => leading || !night ? 1f : 0.3f,
                _ => 1f,
            };
            if (alpha <= 0f || (routes.Count == 0 && pending.Count == 0))
            {
                markers.Hide();
                return;
            }
            Vector3? next = null;
            var cu = CommandUnits.instance;
            if (leading && pending.Count > 0) next = cu.transform.position;
            markers.Show(routes, pending, pendingLoop, next, alpha);
        }

        // ---- Patches ----------------------------------------------------------------------------------------------

        // Runs after unit placement's own postfix (which picks the final spots), so homes are final here.
        [HarmonyPatch(typeof(CommandUnits), nameof(CommandUnits.PlaceCommandedUnitsAndCalculateTargetPositions))]
        private static class PlacePatch
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(List<PathfindMovementPlayerunit> ___toBePlaced)
            {
                var manager = Feature<PatrolManager>.Instance;
                if (manager == null || !manager.IsEnabled) return;
                manager.OnPlaced(___toBePlaced);
            }
        }

        // The game's "reset unit formation every morning" option sends units home at dawn; let it end patrols too.
        [HarmonyPatch(typeof(PathfindMovementPlayerunit), nameof(PathfindMovementPlayerunit.OnDawn_BeforeSunrise))]
        private static class DawnPatch
        {
            private static void Postfix(PathfindMovementPlayerunit __instance)
            {
                var manager = Feature<PatrolManager>.Instance;
                if (manager == null || SettingsManager.Instance == null || !SettingsManager.Instance.ResetUnitFormationEveryMorning) return;
                manager.Remove(__instance, keepPosition: false);
            }
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        private Vector2? devCursor;

        private bool TryCursorWorld(CommandUnits cu, out Vector3 point)
        {
            point = default;
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 screen = devCursor.HasValue ? new Vector3(devCursor.Value.x * Screen.width, devCursor.Value.y * Screen.height) : Input.mousePosition;
            var ray = cam.ScreenPointToRay(screen);
            float height = cu.transform.position.y;
            var units = cu.PlayerUnitsCommanding;
            for (int i = 0; i < 2; i++)
            {
                if (!new Plane(Vector3.up, new Vector3(0f, height, 0f)).Raycast(ray, out float distance)) return false;
                point = ray.GetPoint(distance);
                if (units.Count > 0) point = units[0].GetNearestGroundPosition(point);
                height = point.y;
            }
            return true;
        }

        private static bool Bound(Keybind keybind) => keybind.Shortcut.MainKey != KeyCode.None;

        private static string KeyLabel(Keybind keybind)
        {
            static string Name(KeyCode key) => key switch
            {
                KeyCode.Mouse0 => "Left-click",
                KeyCode.Mouse1 => "Right-click",
                KeyCode.Mouse2 => "Middle-click",
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                _ => key.ToString(),
            };
            var shortcut = keybind.Shortcut;
            return string.Join("+", shortcut.Modifiers.Select(Name).Append(Name(shortcut.MainKey)));
        }

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("patrol-point", "patrol-point [dx dz]   drop a patrol point at the king (+ offset), like the patrol key", args =>
            {
                var cu = RequireLevel();
                if (!(cu.commanding && cu.PlayerUnitsCommanding.Count > 0)) throw new InvalidOperationException("no units following the king");
                Vector3 p = cu.transform.position;
                if (args.Length >= 2) p += new Vector3(float.Parse(args[0]), 0f, float.Parse(args[1]));
                AddPoint(p, cu);
                return $"{pending.Count} pending points";
            });
            DevCommands.Register("patrol-cursor", "patrol-cursor <x> <y>  drop a patrol point at a screen spot (0-1 fractions), like the cursor key", args =>
            {
                var cu = RequireLevel();
                if (args.Length < 2) throw new ArgumentException("usage: patrol-cursor <x> <y>");
                devCursor = new Vector2(float.Parse(args[0]), float.Parse(args[1]));
                try
                {
                    if (!TryCursorWorld(cu, out var p)) throw new InvalidOperationException("cursor not over ground");
                    AddPoint(p, cu);
                }
                finally
                {
                    devCursor = null;
                }
                return $"{pending.Count} pending points";
            });
            DevCommands.Register("patrol-clear", "patrol-clear           stop every patrol and drop pending points", _ =>
            {
                RequireLevel();
                int n = patrollers.Count;
                StopAll(keepPosition: true);
                ClearPending();
                return $"stopped {n} patrolling units";
            });
            DevCommands.Register("patrol-ko", "patrol-ko              knock out one patrolling unit (to test respawn)", _ =>
            {
                RequireLevel();
                var unit = patrollers.Keys.FirstOrDefault(u => u != null && u.hp != null && !u.hp.KnockedOut)
                    ?? throw new InvalidOperationException("no patrolling unit standing");
                unit.hp.TakeDamage(unit.hp.HpValue + 1000f);
                GameplayImpact.Mark("Dev: knocked out a unit");
                return $"knocked out {unit.name}";
            });
            DevCommands.Register("patrol-status", "patrol-status          routes, pending points and each patrolling unit's leg", _ => Status(RequireLevel()));
        }

        private static CommandUnits RequireLevel() =>
            CommandUnits.instance != null && TagManager.instance != null ? CommandUnits.instance : throw new InvalidOperationException("not in a level");

        private string Status(CommandUnits cu)
        {
            Vector3 king = cu.transform.position;
            string P(Vector3 v) => $"({v.x - king.x:F1}, {v.z - king.z:F1})";
            var lines = new List<string> { $"{routes.Count} routes, {patrollers.Count} patrolling, {pending.Count} pending{(pendingLoop ? " (closed loop)" : "")}: {string.Join(" ", pending.Select(P))}  (relative to king)" };
            for (int r = 0; r < routes.Count; r++)
                lines.Add($"  route {r}: {(routes[r].Loop ? "loop" : "back and forth")}, {routes[r].Units} units: {string.Join(" -> ", routes[r].Points.Select(P))}");
            foreach (var (unit, p) in patrollers)
            {
                if (unit == null) continue;
                string state = unit.hp != null && unit.hp.KnockedOut ? "knocked out" : unit.SeekToTaggedObj != null ? $"fighting {unit.SeekToTaggedObj.name}" : "walking";
                lines.Add($"  {unit.name}: route {routes.IndexOf(p.Route)} point {p.Index}, at {P(unit.transform.position)}, {Flat(unit.transform.position - unit.HomePosition).magnitude:F1}m from target, {state}");
            }
            return string.Join("\n", lines);
        }
    }
}
