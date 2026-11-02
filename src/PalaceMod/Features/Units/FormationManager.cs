using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

/*
    Unit placement: formations, dropping one unit at a time, and sending units to the mouse cursor.

    The game places units in CommandUnits.PlaceCommandedUnitsAndCalculateTargetPositions: every commanded unit gets a
    home position in a loose cluster around the king. This feature patches that method:
    - Prefix: for a "place N units" request, hides the other commanded units from the game's list for the duration of
      the call (and re-hides their auto-attacks afterwards), and applies the Spacing setting.
    - Postfix: overrides the home positions with the chosen formation, at the king or at a mouse-picked spot, and
      optionally makes the placed units hold position.
    Everything else (sounds, effects, the hold-position buffer, day-time teleport) stays the game's own. While a cursor
    placement is aimed, PlayerMovement's "Move Towards Mouse" read (the same right mouse button) is ignored.

    Placements the mod starts (mouse, "place one", dev commands) go through Place(request); a plain Space-bar drop uses
    the default request built from the settings.
*/
namespace PalaceMod
{
    public enum PlacementFacing
    {
        WalkingDirection,
        Cursor,
        NearestEnemies,
    }

    public class FormationManager : BaseManager
    {
        public override string FeatureSection => "Formations";
        public override string FeatureDescription =>
            "Formations and finer control over where your units go: change formation, drop one unit at a time, and right-click to send units to the cursor.";

        private ConfigEntry<FormationType> formation;
        private ConfigEntry<float> spacing;
        private ConfigEntry<PlacementFacing> facing;
        private ConfigEntry<bool> mousePlacement, preview, hints;

        private Keybind cycleKey, placeOneKey, mouseKey, holdKey;

        /// <summary>One placement: which units, where, in what shape.</summary>
        internal struct PlacementRequest
        {
            public FormationType Formation;
            /// <summary>False: around the king, like the game.</summary>
            public bool HasCenter;
            public Vector3 Center;
            /// <summary>Zero: from the Facing setting.</summary>
            public Vector3 Forward;
            /// <summary>Front-row width from a mouse drag; 0 = automatic.</summary>
            public float Width;
            /// <summary>Place at most this many of the commanded units; 0 = all.</summary>
            public int MaxUnits;
            public bool Hold;
        }

        private static PlacementRequest? pending;
        private static readonly List<FormationSlot> slots = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var config = plugin.Config;
            formation = config.Bind(FeatureSection, "Formation", FormationType.Normal, new ConfigDescription(
                "How units line up when you place them. Normal is the game's own cluster; in the others melee units take the front."));
            spacing = config.Bind(FeatureSection, "Spacing", 2f, new ConfigDescription(
                "Distance between placed units. The game's default is 2.",
                new AcceptableValueRange<float>(1f, 5f), new SettingTags { Step = 0.5f }));
            facing = config.Bind(FeatureSection, "Facing", PlacementFacing.WalkingDirection, new ConfigDescription(
                "Which way Line and Arc formations face: where the king walks, toward the mouse cursor, or toward the nearest enemies (or the next wave's spawn)."));
            mousePlacement = config.Bind(FeatureSection, "Mouse placement", true, new ConfigDescription(
                "While units follow you, right-click to send them to the cursor, or right-drag to draw a line for them. Hold the click, or Shift, to make them hold position. Using it makes the run unranked."));
            preview = config.Bind(FeatureSection, "Placement preview", true, new ConfigDescription(
                "Shows markers on the ground where units will stand while you aim a mouse placement."));
            hints = config.Bind(FeatureSection, "Hints", true, new ConfigDescription(
                "Shows a short reminder of the placement keys the first time you lead units each session."));
            preview.SettingChanged += (_, _) => { if (!preview.Value) previewMarkers.Hide(); };
            mousePlacement.SettingChanged += (_, _) => { if (!mousePlacement.Value) CancelDrag(); };

            cycleKey = Keybinds.Register(FeatureSection, "cycle", "Change formation", new KeyboardShortcut(KeyCode.F7),
                "Switches to the next unit formation.");
            placeOneKey = Keybinds.Register(FeatureSection, "place-one", "Place one unit", new KeyboardShortcut(KeyCode.O),
                "Drops one of the units following you; hold to make it hold position.");
            mouseKey = Keybinds.Register(FeatureSection, "mouse-place", "Place at cursor", new KeyboardShortcut(KeyCode.Mouse1),
                "Sends the units following you to the cursor; drag to draw a line for them.");
            holdKey = Keybinds.Register(FeatureSection, "mouse-hold", "Hold position (with cursor placement)", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold while placing at the cursor to make the units hold position.");

            RegisterDevCommands();
        }

        protected override void OnFeatureDisabled()
        {
            CancelDrag();
            placeOneActive = false;
            devPress = devHeld = devRelease = devWalk = false;
        }

        public override void OnDestroy() => previewMarkers.Destroy();

        // ---- Input ------------------------------------------------------------------------------------------------

        private bool wasLeading, hintShown;
        private bool placeOneActive, placeOneHeld;
        private float placeOneTime;

        public override void Update()
        {
            var cu = CommandUnits.instance;
            if (cu == null)
            {
                CancelDrag();
                wasLeading = false;
                return;
            }
            bool leading = cu.commanding && cu.PlayerUnitsCommanding.Count > 0;
            if (leading && !wasLeading) OnStartLeading();
            wasLeading = leading;

            if (cycleKey.Down) CycleFormation();
            UpdatePlaceOne(cu, leading);
            UpdateMouse(cu, leading);
        }

        private void OnStartLeading()
        {
            if (!hints.Value || hintShown) return;
            hintShown = true;
            var parts = new List<string>();
            if (Bound(cycleKey)) parts.Add($"{KeyLabel(cycleKey)}: formation ({Words(formation.Value)})");
            if (Bound(placeOneKey)) parts.Add($"{KeyLabel(placeOneKey)}: drop one unit");
            if (mousePlacement.Value && Bound(mouseKey)) parts.Add($"{KeyLabel(mouseKey)}: send to cursor (drag for a line)");
            if (parts.Count > 0) Notify.Show(string.Join("   ", parts), seconds: 7f, key: "placement-hint");
        }

        private void CycleFormation()
        {
            var values = (FormationType[])Enum.GetValues(typeof(FormationType));
            formation.Value = values[(Array.IndexOf(values, formation.Value) + 1) % values.Length];
            Notify.Show($"Formation: {Words(formation.Value)}", key: "formation");
        }

        private void UpdatePlaceOne(CommandUnits cu, bool leading)
        {
            if (placeOneKey.Down && leading)
            {
                Place(new PlacementRequest { Formation = FormationType.Normal, MaxUnits = 1 });
                placeOneActive = true;
                placeOneHeld = false;
                placeOneTime = 0f;
            }
            if (!placeOneActive) return;
            if (!placeOneKey.Held)
            {
                placeOneActive = false;
                return;
            }
            // Same long press as the game's own Command key: hold to make the dropped unit hold position.
            placeOneTime += Time.deltaTime;
            if (!placeOneHeld && placeOneTime > cu.holdToHoldPositionTime)
            {
                placeOneHeld = true;
                cu.MakeUnitsInBufferHoldPosition();
            }
        }

        // ---- Mouse placement --------------------------------------------------------------------------------------

        private const float DragThreshold = 1.5f;

        private readonly PlacementPreview previewMarkers = new();
        private bool dragging, dragMoved;
        private Vector3 dragStart;
        private float dragStartTime;

        // Dev-command stand-ins for the mouse, so placement can be driven without a real cursor.
        private Vector2? devCursor;
        private bool devPress, devHeld, devRelease;
        private bool devWalk; // stands in for holding the game's "Move Towards Mouse"

        private void UpdateMouse(CommandUnits cu, bool leading)
        {
            bool pressed = mouseKey.Down || devPress;
            bool held = mouseKey.Held || devHeld;
            bool released = mouseKey.Up || devRelease;
            devPress = devRelease = false;

            if (!mousePlacement.Value || !leading)
            {
                CancelDrag();
                return;
            }
            if (pressed && !dragging && TryCursorWorld(out dragStart))
            {
                dragging = true;
                dragMoved = false;
                dragStartTime = Time.time;
            }
            if (!dragging) return;
            if (!held && !released)
            {
                // Released while input was blocked (a menu opened): drop the placement.
                CancelDrag();
                return;
            }

            if (!TryCursorWorld(out var current)) current = dragStart;
            var request = MouseRequest(cu, current);
            if (released)
            {
                CancelDrag();
                // The user's call: sending units to the cursor goes beyond vanilla controls, so the run is unranked.
                GameplayImpact.Mark("Mouse unit placement");
                Place(request);
                return;
            }
            if (preview.Value) ShowPreview(cu, request);
        }

        private PlacementRequest MouseRequest(CommandUnits cu, Vector3 current)
        {
            Vector3 chord = current - dragStart;
            chord.y = 0f;
            if (chord.magnitude >= DragThreshold) dragMoved = true;

            bool hold = holdKey.Held || (!dragMoved && Time.time - dragStartTime >= cu.holdToHoldPositionTime);
            if (!dragMoved)
            {
                return new PlacementRequest
                {
                    Formation = formation.Value,
                    HasCenter = true,
                    Center = dragStart,
                    Forward = Facing(cu, dragStart, awayFromKing: true),
                    Hold = hold,
                };
            }

            // A drag draws the front of the formation: its middle, width and (perpendicular) facing.
            Vector3 middle = (dragStart + current) / 2f;
            Vector3 forward = Vector3.Cross(chord.normalized, Vector3.up);
            if (Vector3.Dot(forward, Facing(cu, middle, awayFromKing: true)) < 0f) forward = -forward;
            var shape = formation.Value is FormationType.Arc or FormationType.Circle ? formation.Value : FormationType.Line;
            return new PlacementRequest
            {
                Formation = shape,
                HasCenter = true,
                Center = middle,
                Forward = forward,
                Width = chord.magnitude,
                Hold = hold,
            };
        }

        private void ShowPreview(CommandUnits cu, PlacementRequest request)
        {
            if (!Assign(cu, cu.PlayerUnitsCommanding, request, assigned)) return;
            slots.Clear();
            foreach (var (_, position) in assigned) slots.Add(new FormationSlot { Position = position });
            previewMarkers.Show(slots, request.Center, request.Forward, request.Hold);
        }

        private void CancelDrag()
        {
            dragging = false;
            previewMarkers.Hide();
        }

        /// <summary>The point on the ground under the mouse cursor.</summary>
        private bool TryCursorWorld(out Vector3 point)
        {
            point = default;
            var cam = Camera.main;
            var cu = CommandUnits.instance;
            if (cam == null || cu == null) return false;
            Vector3 screen = devCursor.HasValue ? new Vector3(devCursor.Value.x * Screen.width, devCursor.Value.y * Screen.height) : Input.mousePosition;
            var ray = cam.ScreenPointToRay(screen);
            var units = cu.PlayerUnitsCommanding;
            float height = cu.transform.position.y;
            // Intersect a ground plane, snap to the navmesh, and repeat once at the snapped height for sloped ground.
            for (int i = 0; i < 2; i++)
            {
                if (!new Plane(Vector3.up, new Vector3(0f, height, 0f)).Raycast(ray, out float distance)) return false;
                point = ray.GetPoint(distance);
                if (units.Count > 0) point = units[0].GetNearestGroundPosition(point);
                height = point.y;
            }
            return true;
        }

        // ---- Facing -----------------------------------------------------------------------------------------------

        private Vector3 Facing(CommandUnits cu, Vector3 center, bool awayFromKing)
        {
            Vector3 king = cu.transform.position;
            switch (facing.Value)
            {
                case PlacementFacing.NearestEnemies when TryNearestThreat(center, out var threat):
                    return Flat(threat - center);
                case PlacementFacing.Cursor when !awayFromKing && TryCursorWorld(out var cursor) && Flat(cursor - center).magnitude > 1f:
                    return Flat(cursor - center);
            }
            if (awayFromKing && Flat(center - king).magnitude > 1f) return Flat(center - king);
            // CommandUnits sits on the king.
            var player = cu.GetComponent<PlayerMovement>();
            return player != null && player.meshParent != null ? Flat(player.meshParent.forward) : Vector3.forward;
        }

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        /// <summary>The closest living enemy, or else the closest spawn point of the current or next wave.</summary>
        private static bool TryNearestThreat(Vector3 from, out Vector3 threat)
        {
            threat = default;
            float best = float.MaxValue;
            if (TagManager.instance != null)
            {
                foreach (var enemy in TagManager.instance.EnemyUnits)
                {
                    if (enemy == null || enemy.Hp == null || !enemy.Hp.Alive) continue;
                    float d = (enemy.transform.position - from).sqrMagnitude;
                    if (d < best) (best, threat) = (d, enemy.transform.position);
                }
            }
            if (best < float.MaxValue) return true;

            var spawner = EnemySpawner.instance;
            if (spawner == null) return false;
            // By day the counter still points at last night's wave; the coming one is next.
            bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Day;
            int w = night ? spawner.Wavenumber : spawner.Wavenumber + 1;
            if (w >= 0 && w < spawner.waves.Count)
            {
                foreach (var spawn in spawner.waves[w].spawns)
                {
                    var line = spawn.spawnLine;
                    if (line == null) continue;
                    // Same spawn point the game marks on screen: the middle of the spawn line.
                    Vector3 p = line.childCount > 0 ? (line.GetChild(0).position + line.GetChild(line.childCount - 1).position) / 2f : line.position;
                    float d = (p - from).sqrMagnitude;
                    if (d < best) (best, threat) = (d, p);
                }
            }
            return best < float.MaxValue;
        }

        // ---- Placement --------------------------------------------------------------------------------------------

        /// <summary>Places the commanded units as described; no-op if none are following the king.</summary>
        internal void Place(PlacementRequest request)
        {
            var cu = CommandUnits.instance;
            if (cu == null || !cu.commanding || cu.PlayerUnitsCommanding.Count == 0) return;
            pending = request;
            try
            {
                cu.PlaceCommandedUnitsAndCalculateTargetPositions(false);
            }
            finally
            {
                pending = null;
            }
            // The game only stops commanding when the Command key is released; a placement the mod made that emptied
            // the group would otherwise swallow the next Command press.
            if (cu.PlayerUnitsCommanding.Count == 0) cu.commanding = false;
        }

        private PlacementRequest DefaultRequest(CommandUnits cu) => new() { Formation = formation.Value };

        private static readonly List<PathfindMovementPlayerunit> ordered = [];
        private static readonly List<int> rowSlots = [];
        private static readonly List<PathfindMovementPlayerunit> rowUnits = [];

        private static readonly List<(PathfindMovementPlayerunit unit, Vector3 position)> assigned = [];

        private void Arrange(CommandUnits cu, List<PathfindMovementPlayerunit> units, PlacementRequest request)
        {
            // Normal at the king is the game's own placement.
            if (request.Formation == FormationType.Normal && !request.HasCenter) return;
            if (!Assign(cu, units, request, assigned)) return;
            bool day = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day;
            foreach (var (unit, position) in assigned)
            {
                unit.HomePosition = position;
                // Matches the game: during the day placed units appear at their spot instead of walking there.
                if (day)
                {
                    unit.transform.position = position;
                    unit.ClearCurrentPath();
                }
            }
        }

        /// <summary>Works out where each unit stands (snapped to that unit's navmesh); shared by placement and preview.</summary>
        private bool Assign(CommandUnits cu, List<PathfindMovementPlayerunit> units, PlacementRequest request, List<(PathfindMovementPlayerunit, Vector3)> result)
        {
            result.Clear();
            if (units.Count == 0) return false;

            Vector3 center = request.HasCenter ? request.Center : cu.transform.position;
            Vector3 forward = request.Forward.sqrMagnitude > 1e-6f ? request.Forward : Facing(cu, center, awayFromKing: false);
            forward = Flat(forward).normalized;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            FormationLayout.Compute(request.Formation, units.Count, center, forward, spacing.Value, request.Width, slots);

            // Melee units first, so they get the front rows (the outer ring of a circle).
            ordered.Clear();
            ordered.AddRange(units);
            var melee = ordered.Select(IsMeleeOnly).ToList();
            var indices = Enumerable.Range(0, ordered.Count).OrderBy(i => melee[i] ? 0 : 1).ThenBy(i => i).ToList();
            var byPriority = indices.Select(i => ordered[i]).ToList();

            // Within a row, match units to slots left-to-right (or by angle) so their paths don't cross.
            bool ring = request.Formation == FormationType.Circle;
            float Key(Vector3 p)
            {
                Vector3 d = p - center;
                float x = Vector3.Dot(d, right);
                if (!ring) return x;
                float a = Mathf.Atan2(x, Vector3.Dot(d, forward));
                return a < 0f ? a + 2f * Mathf.PI : a;
            }

            int next = 0;
            for (int start = 0; start < slots.Count;)
            {
                int row = slots[start].Row, end = start;
                while (end < slots.Count && slots[end].Row == row) end++;
                rowSlots.Clear();
                for (int i = start; i < end; i++) rowSlots.Add(i);
                rowSlots.Sort((a, b) => slots[a].Order.CompareTo(slots[b].Order));
                rowUnits.Clear();
                for (int i = start; i < end; i++) rowUnits.Add(byPriority[next++]);
                rowUnits.Sort((a, b) => Key(a.transform.position).CompareTo(Key(b.transform.position)));
                for (int i = 0; i < rowUnits.Count; i++)
                    result.Add((rowUnits[i], rowUnits[i].GetNearestGroundPosition(slots[rowSlots[i]].Position)));
                start = end;
            }
            return true;
        }

        private static bool IsMeleeOnly(PathfindMovementPlayerunit unit)
        {
            var tags = unit.GetComponent<TaggedObject>()?.Tags;
            return tags != null && tags.Contains(TagManager.ETag.MeeleFighter) && !tags.Contains(TagManager.ETag.RangedFighter);
        }

        /// <summary>A mouse placement is being aimed, or its key is down and about to start one (the game may read its
        /// input before this manager's Update does).</summary>
        private bool AimingAtCursor
        {
            get
            {
                if (dragging) return true;
                var cu = CommandUnits.instance;
                return mousePlacement.Value && mouseKey.Held && cu != null && cu.commanding && cu.PlayerUnitsCommanding.Count > 0;
            }
        }

        // The game's "Move Towards Mouse" (right mouse button by default) would walk the king toward the cursor while
        // a placement is aimed with the same button; it's ignored during that window only.
        [HarmonyPatch(typeof(PlayerMovement), "Update")]
        private static class MoveTowardsMousePatch
        {
            private const string Action = "Move Towards Mouse";

            private static bool GetButton(Rewired.Player player, string action)
            {
                if (action != Action) return player.GetButton(action);
                var manager = Feature<FormationManager>.Instance;
                if (manager != null && manager.AimingAtCursor) return false;
                return player.GetButton(action) || (manager != null && manager.devWalk);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools.Method(typeof(Rewired.Player), nameof(Rewired.Player.GetButton), [typeof(string)]);
                var replacement = AccessTools.Method(typeof(MoveTowardsMousePatch), nameof(GetButton));
                var list = instructions.ToList();
                bool replaced = false;
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[i].Calls(original) && list[i - 1].opcode == System.Reflection.Emit.OpCodes.Ldstr && (string)list[i - 1].operand == Action)
                    {
                        list[i].opcode = System.Reflection.Emit.OpCodes.Call;
                        list[i].operand = replacement;
                        replaced = true;
                    }
                }
                if (!replaced) Plugin.Logger.LogWarning("Formations: PlayerMovement.Update has no \"Move Towards Mouse\" read; right-click placement may also walk the king");
                return list;
            }
        }

        [HarmonyPatch(typeof(CommandUnits), nameof(CommandUnits.PlaceCommandedUnitsAndCalculateTargetPositions))]
        private static class PlacePatch
        {
            internal struct State
            {
                public bool Active;
                public float VanillaSpacing;
                public List<PathfindMovementPlayerunit> Held;
                public List<AutoAttack> HeldAttacks;
            }

            private static void Prefix(CommandUnits __instance, List<PathfindMovementPlayerunit> ___playerUnitsCommanding,
                List<AutoAttack> ___autoAttacksToEnable, out State __state)
            {
                __state = default;
                var manager = Feature<FormationManager>.Instance;
                if (manager == null || !__instance.commanding || ___playerUnitsCommanding.Count == 0) return;
                __state.Active = true;
                __state.VanillaSpacing = __instance.unitDistanceFromEachOther;
                __instance.unitDistanceFromEachOther = manager.spacing.Value;

                var request = pending ?? manager.DefaultRequest(__instance);
                if (request.MaxUnits <= 0 || ___playerUnitsCommanding.Count <= request.MaxUnits) return;

                // Hide the units that stay with the king, so the game places only the first MaxUnits.
                __state.Held = ___playerUnitsCommanding.GetRange(request.MaxUnits, ___playerUnitsCommanding.Count - request.MaxUnits);
                ___playerUnitsCommanding.RemoveRange(request.MaxUnits, __state.Held.Count);
                // The game re-enables every pending auto-attack on placement; remember the held units' so they stay off.
                __state.HeldAttacks = [];
                foreach (var unit in __state.Held)
                    foreach (var attack in unit.GetComponents<AutoAttack>())
                        if (___autoAttacksToEnable.Contains(attack)) __state.HeldAttacks.Add(attack);
            }

            private static void Postfix(CommandUnits __instance, List<PathfindMovementPlayerunit> ___playerUnitsCommanding,
                List<PathfindMovementPlayerunit> ___toBePlaced, List<AutoAttack> ___autoAttacksToEnable, State __state)
            {
                if (!__state.Active) return;
                __instance.unitDistanceFromEachOther = __state.VanillaSpacing;
                var manager = Feature<FormationManager>.Instance;
                var request = pending ?? manager.DefaultRequest(__instance);

                if (__state.Held != null)
                {
                    ___playerUnitsCommanding.AddRange(__state.Held);
                    foreach (var attack in __state.HeldAttacks)
                    {
                        if (attack == null || ___autoAttacksToEnable.Contains(attack)) continue;
                        attack.enabled = false;
                        ___autoAttacksToEnable.Add(attack);
                    }
                }

                manager.Arrange(__instance, ___toBePlaced, request);
                if (request.Hold) __instance.MakeUnitsInBufferHoldPosition();
            }

            // Exceptions in the prefix must not leave units hidden from the game's list.
            private static Exception Finalizer(Exception __exception, CommandUnits __instance, List<PathfindMovementPlayerunit> ___playerUnitsCommanding, State __state)
            {
                if (__exception != null && __state.Active)
                {
                    __instance.unitDistanceFromEachOther = __state.VanillaSpacing;
                    if (__state.Held != null)
                        foreach (var unit in __state.Held)
                            if (!___playerUnitsCommanding.Contains(unit)) ___playerUnitsCommanding.Add(unit);
                }
                return __exception;
            }
        }

        // ---- Labels -----------------------------------------------------------------------------------------------

        private static bool Bound(Keybind keybind) => keybind.Shortcut.MainKey != KeyCode.None;

        private static string KeyLabel(Keybind keybind)
        {
            var shortcut = keybind.Shortcut;
            string Name(KeyCode key) => key switch
            {
                KeyCode.Mouse0 => "Left-click",
                KeyCode.Mouse1 => "Right-click",
                KeyCode.Mouse2 => "Middle-click",
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                _ => key.ToString(),
            };
            return string.Join("+", shortcut.Modifiers.Select(Name).Append(Name(shortcut.MainKey)));
        }

        private static string Words(Enum value) => value.ToString() switch
        {
            "WalkingDirection" => "Walking direction",
            "NearestEnemies" => "Nearest enemies",
            var s => s,
        };

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("place-army", "place-army [n]         build unit buildings until there are n player units (default 10)", args =>
            {
                int want = args.Length > 0 ? int.Parse(args[0]) : 10;
                RequireLevel();
                int built = 0;
                foreach (var respawner in UnityEngine.Object.FindObjectsOfType<UnitRespawnerForBuildings>(true))
                {
                    if (TagManager.instance.PlayerUnits.Count >= want) break;
                    var slot = respawner.myBuildSlot;
                    if (slot == null || slot.Level > 0 || !slot.CanBeUpgraded) continue;
                    slot.ActivateIgrnoringActivatorLevel();
                    slot.TryToBuildOrUpgradeAndPay(null, _presentChoice: false);
                    built++;
                }
                GameplayImpact.Mark("Dev: spawned units");
                return $"built {built} buildings; {TagManager.instance.PlayerUnits.Count} player units";
            });
            DevCommands.Register("place-select", "place-select           make every player unit follow the king", _ =>
            {
                var cu = RequireLevel();
                foreach (var unit in TagManager.instance.PlayerUnits.ToList()) cu.OnUnitAdd(unit, _smartCommand: false);
                cu.commanding = cu.PlayerUnitsCommanding.Count > 0;
                return $"{cu.PlayerUnitsCommanding.Count} units following";
            });
            DevCommands.Register("place-at", "place-at <dx> <dz> [hold]   place the following units at the king + (dx, dz), like a right-click", args =>
            {
                var cu = RequireLevel();
                if (args.Length < 2) throw new ArgumentException("usage: place-at <dx> <dz> [hold]");
                Vector3 center = cu.transform.position + new Vector3(float.Parse(args[0]), 0f, float.Parse(args[1]));
                Place(new PlacementRequest
                {
                    Formation = formation.Value, HasCenter = true, Center = center,
                    Forward = Facing(cu, center, awayFromKing: true), Hold = args.Contains("hold"),
                });
                return Status(cu);
            });
            DevCommands.Register("place-key", "place-key [one] [hold]   drop the following units at the king like the Command key (one: just one)", args =>
            {
                var cu = RequireLevel();
                if (args.Contains("one")) Place(new PlacementRequest { Formation = FormationType.Normal, MaxUnits = 1, Hold = args.Contains("hold") });
                else
                {
                    var request = DefaultRequest(cu);
                    request.Hold = args.Contains("hold");
                    Place(request);
                }
                return Status(cu);
            });
            DevCommands.Register("place-mouse", "place-mouse press|move <x> <y> | release | walk on|off | off   drive cursor placement (x, y: 0-1 screen fractions); walk holds the game's Move Towards Mouse", args =>
            {
                RequireLevel();
                string action = args.Length > 0 ? args[0] : "";
                if ((action == "press" || action == "move") && args.Length >= 3) devCursor = new Vector2(float.Parse(args[1]), float.Parse(args[2]));
                switch (action)
                {
                    case "press": devPress = devHeld = true; break;
                    case "move": break;
                    case "release": devHeld = false; devRelease = true; break;
                    case "walk": devWalk = args.Length < 2 || args[1] != "off"; break;
                    case "off": devCursor = null; devHeld = devWalk = false; break;
                    default: throw new ArgumentException("usage: place-mouse press|move <x> <y> | release | walk on|off | off");
                }
                string king = $"king at {CommandUnits.instance.transform.position}, aiming {AimingAtCursor}";
                return TryCursorWorld(out var p) ? $"cursor at ground {p}; {king}" : $"cursor not over ground; {king}";
            });
            DevCommands.Register("place-status", "place-status           following units and every placed unit's home and hold state", _ => Status(RequireLevel()));
        }

        private static CommandUnits RequireLevel() =>
            CommandUnits.instance != null && TagManager.instance != null ? CommandUnits.instance : throw new InvalidOperationException("not in a level");

        private string Status(CommandUnits cu)
        {
            Vector3 king = cu.transform.position;
            var lines = TagManager.instance.PlayerUnits
                .Select(t => t.GetComponent<PathfindMovementPlayerunit>())
                .Where(u => u != null && u.hp != null && u.hp.Alive)
                .Select(u => $"  {u.name}: home {Flat(u.HomePosition - king)} from king{(u.FollowingPlayer ? ", following" : "")}{(u.GetComponents<AutoAttack>().Any(a => !a.enabled) ? ", attacks off" : "")}{(u.HoldPosition ? ", holding" : "")}");
            return $"formation {formation.Value}, {cu.PlayerUnitsCommanding.Count} following, commanding={cu.commanding}\n" + string.Join("\n", lines);
        }
    }
}
