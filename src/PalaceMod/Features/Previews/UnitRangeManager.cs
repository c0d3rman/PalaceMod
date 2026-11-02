using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Draws attack-range rings for your units with the game's own RangeIndicatorHandler (the rings towers show).

    The game (as of 2.13) shows ranges only for buildings: the focused tower's range and upgrade preview, shrines,
    and optionally every tower's range while interacting. Nothing shows a unit's range, so this fills that gap:
    - Commanded units: one ring per distinct range, centred on the king, since that's where they'll be placed.
      A group of archers is one ring rather than a stack of overlapping ones.
    - Units in the king's command radius (the ones a command press would pick up), and every unit while the hold
      key is held: rings around each unit where it stands.

    A unit's attack ranges are each AutoAttack's longest target-priority range, ignoring priorities that only target
    flying enemies (longbows have a longer anti-air priority) unless the attack has nothing else, deduplicated. Its engage range (how far it walks to fight once placed) is
    an optional lighter ring, shown when it's beyond the attack range (around its post, capped at 7, while it holds
    position). Ranges are read once when a unit starts
    showing and reused until it stops.

    RangeIndicatorHandler keys rings by (type, sender GameObject). The game only ever uses buildings as senders, so
    unit rings can't collide with its own: per-unit rings use the unit as the key for the first ring and pooled empty
    GameObjects for the rest; group rings use a fixed set of empty GameObjects. Attack rings use the Built style
    (a built tower's solid ring), engage rings the Preview style. Each frame the wanted rings are refreshed and any
    ring that wasn't wanted is faded out, which covers release, death, despawn and setting changes without patches.
*/
namespace PalaceMod
{
    public class UnitRangeManager : BaseManager
    {
        public override string FeatureSection => "Unit ranges";
        public override string FeatureDescription => "Shows attack-range rings for your units, like the ones towers show.";

        private const int MaxAttackRanges = 3;
        private const int MaxGroupRings = 4;
        private const float SameRangeTolerance = 0.25f;
        private const float HoldEngageRange = 7f; // PathfindMovementPlayerunit.FindTargetPosition's limit while holding
        private const RangeIndicatorHandler.IndicatorType AttackStyle = RangeIndicatorHandler.IndicatorType.Built;
        private const RangeIndicatorHandler.IndicatorType EngageStyle = RangeIndicatorHandler.IndicatorType.Preview;

        private ConfigEntry<bool> showCommanded;
        private ConfigEntry<bool> showNearby;
        private ConfigEntry<bool> showEngageRange;
        private Keybind showAllKey;

        private readonly struct RingId : IEquatable<RingId>
        {
            public readonly GameObject key;
            public readonly RangeIndicatorHandler.IndicatorType type;
            public RingId(GameObject key, RangeIndicatorHandler.IndicatorType type) { this.key = key; this.type = type; }
            // Reference equality: a key whose unit was destroyed must still match so its ring can be hidden.
            public bool Equals(RingId other) => ReferenceEquals(key, other.key) && type == other.type;
            public override bool Equals(object obj) => obj is RingId other && Equals(other);
            public override int GetHashCode() => RuntimeHelpers.GetHashCode(key) * 31 + (int)type;
        }

        private sealed class UnitInfo
        {
            public readonly float[] attack = new float[MaxAttackRanges];
            public readonly GameObject[] keys = new GameObject[MaxAttackRanges];
            public int count;
            public float engage; // 0 when not shown
            public int lastFrame;
        }

        private readonly Dictionary<PathfindMovementPlayerunit, UnitInfo> units = new();
        private readonly Stack<UnitInfo> infoPool = new();
        private readonly Stack<GameObject> keyPool = new();
        private readonly List<PathfindMovementPlayerunit> staleUnits = new();
        private readonly GameObject[] groupAttackKeys = new GameObject[MaxGroupRings];
        private readonly GameObject[] groupEngageKeys = new GameObject[MaxGroupRings];
        private readonly List<float> groupAttack = new();
        private readonly List<float> groupEngage = new();
        private HashSet<RingId> shownRings = new();
        private HashSet<RingId> wantedRings = new();
        private int frame;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            showCommanded = plugin.Config.Bind(FeatureSection, "Commanded units", true,
                "While commanding, show your units' ranges around you, where they'd be placed.");
            showNearby = plugin.Config.Bind(FeatureSection, "Units in command radius", false,
                "Show the ranges of your units close enough to be picked up by a command.");
            showEngageRange = plugin.Config.Bind(FeatureSection, "Engage range", false,
                "Also show a lighter ring for how far units walk to fight once placed, when that's beyond their attack range.");
            showAllKey = Keybinds.Register(FeatureSection, "show-all", "Show all unit ranges", KeyboardShortcut.Empty,
                "Hold to show the ranges of all your units.");
            showEngageRange.SettingChanged += (_, _) => { HideAll(); ForgetUnits(); };

            for (int i = 0; i < MaxGroupRings; i++)
            {
                groupAttackKeys[i] = NewKey();
                groupEngageKeys[i] = NewKey();
            }

            SceneManager.sceneLoaded += (_, _) => { shownRings.Clear(); ForgetUnits(); };

            DevCommands.Register("unitrange", "unitrange slots|build <name>|command [name]|release|dump   set up and inspect unit ranges", DevCommand);
        }

        protected override void OnFeatureDisabled()
        {
            HideAll();
            ForgetUnits();
        }

        public override void Update()
        {
            var command = CommandUnits.instance;
            var handler = RangeIndicatorHandler.instance;
            var tags = TagManager.instance;
            if (command == null || handler == null || tags == null)
            {
                shownRings.Clear();
                if (units.Count > 0) ForgetUnits();
                return;
            }
            frame++;

            if (showCommanded.Value && command.PlayerUnitsCommanding.Count > 0) WantGroup(command, handler);

            bool all = showAllKey.Held;
            if (all || (showNearby.Value && command.PlayerUnitsCommanding.Count == 0))
            {
                Vector3 king = command.transform.position;
                foreach (var tagged in tags.PlayerUnits)
                {
                    if (tagged == null || tagged.Hp == null || tagged.Hp.PathfindMovement is not PathfindMovementPlayerunit unit) continue;
                    if (showCommanded.Value && command.PlayerUnitsCommanding.Contains(unit)) continue; // drawn as the group
                    if (all || InCommandRadius(tags, tagged, king, command.attractRange)) WantUnit(unit, handler);
                }
            }

            foreach (var ring in shownRings)
            {
                if (!wantedRings.Contains(ring)) handler.HideIndicator(ring.key, ring.type);
            }
            shownRings.Clear();
            (shownRings, wantedRings) = (wantedRings, shownRings);

            DropUnseenUnits();
        }

        // Commanded units: their distinct ranges as rings around the king.
        private void WantGroup(CommandUnits command, RangeIndicatorHandler handler)
        {
            groupAttack.Clear();
            groupEngage.Clear();
            foreach (var unit in command.PlayerUnitsCommanding)
            {
                var info = Info(unit);
                if (info == null) continue;
                for (int i = 0; i < info.count; i++) AddDistinct(groupAttack, info.attack[i]);
                if (info.engage > 0f) AddDistinct(groupEngage, info.engage);
            }
            Vector3 king = command.transform.position;
            for (int i = 0; i < groupAttack.Count && i < MaxGroupRings; i++) Show(handler, king, groupAttack[i], groupAttackKeys[i], AttackStyle);
            for (int i = 0; i < groupEngage.Count && i < MaxGroupRings; i++) Show(handler, king, groupEngage[i], groupEngageKeys[i], EngageStyle);
        }

        private void WantUnit(PathfindMovementPlayerunit unit, RangeIndicatorHandler handler)
        {
            var info = Info(unit);
            if (info == null) return;
            Vector3 position = unit.transform.position;
            for (int i = 0; i < info.count; i++)
            {
                info.keys[i] ??= i == 0 ? unit.gameObject : TakeKey();
                Show(handler, position, info.attack[i], info.keys[i], AttackStyle);
            }
            if (info.engage > 0f)
            {
                // A unit holding position only engages enemies within HoldEngageRange of its post.
                if (unit.HoldPosition)
                {
                    float held = Mathf.Min(info.engage, HoldEngageRange);
                    if (held > (info.count > 0 ? info.attack[0] : 0f) + SameRangeTolerance) Show(handler, unit.HomePosition, held, unit.gameObject, EngageStyle);
                }
                else Show(handler, position, info.engage, unit.gameObject, EngageStyle);
            }
        }

        private void Show(RangeIndicatorHandler handler, Vector3 position, float range, GameObject key, RangeIndicatorHandler.IndicatorType type)
        {
            if (!wantedRings.Add(new RingId(key, type))) return;
            handler.ShowIndicator(position, range, key, type);
        }

        // Same test CommandUnits uses to pick units up: 3D distance, or horizontal distance for flying units.
        private static bool InCommandRadius(TagManager tags, TaggedObject tagged, Vector3 king, float radius)
        {
            if (tags.MeasureDistanceToTaggedObject(tagged, king) <= radius) return true;
            if (!tagged.Tags.Contains(TagManager.ETag.Flying)) return false;
            Vector3 d = tagged.transform.position - king;
            return new Vector2(d.x, d.z).magnitude <= radius;
        }

        // The unit's ranges, read the first frame it's shown; null for dead or disabled units.
        private UnitInfo Info(PathfindMovementPlayerunit unit)
        {
            if (unit == null || !unit.isActiveAndEnabled || (unit.hp != null && !unit.hp.Alive)) return null;
            if (!units.TryGetValue(unit, out var info))
            {
                info = infoPool.Count > 0 ? infoPool.Pop() : new UnitInfo();
                ReadRanges(unit, info);
                units.Add(unit, info);
            }
            info.lastFrame = frame;
            return info;
        }

        private readonly List<float> scratchRanges = new();

        private void ReadRanges(PathfindMovementPlayerunit unit, UnitInfo info)
        {
            scratchRanges.Clear();
            var attacks = unit.autoAttacks;
            if (attacks == null || attacks.Length == 0) attacks = unit.GetComponents<AutoAttack>();
            foreach (var attack in attacks)
            {
                if (attack == null) continue;
                // Longbows reach flying enemies from further (50) than everything else (40): show the general
                // reach, and fall back to flying-only priorities only for attacks that have nothing else.
                float reach = 0f, flyingOnlyReach = 0f;
                foreach (var priority in attack.targetPriorities)
                {
                    if (priority.mustHaveTags.Contains(TagManager.ETag.Flying)) flyingOnlyReach = Mathf.Max(flyingOnlyReach, priority.range);
                    else reach = Mathf.Max(reach, priority.range);
                }
                if (reach <= 0f) reach = flyingOnlyReach;
                if (reach > 0f) AddDistinct(scratchRanges, reach);
            }
            info.count = Math.Min(scratchRanges.Count, MaxAttackRanges);
            for (int i = 0; i < info.count; i++) info.attack[i] = scratchRanges[i];

            info.engage = 0f;
            if (showEngageRange.Value)
            {
                float engage = 0f;
                foreach (var priority in unit.targetPriorities) engage = Mathf.Max(engage, priority.range);
                if (engage > (info.count > 0 ? info.attack[0] : 0f) + SameRangeTolerance) info.engage = engage;
            }
        }

        // Keeps the list sorted longest first, without near-duplicates.
        private static void AddDistinct(List<float> ranges, float range)
        {
            int at = 0;
            for (; at < ranges.Count; at++)
            {
                if (Mathf.Abs(ranges[at] - range) <= SameRangeTolerance) return;
                if (ranges[at] < range) break;
            }
            ranges.Insert(at, range);
        }

        private void DropUnseenUnits()
        {
            staleUnits.Clear();
            foreach (var pair in units)
            {
                if (pair.Value.lastFrame != frame) staleUnits.Add(pair.Key);
            }
            foreach (var unit in staleUnits)
            {
                Recycle(units[unit]);
                units.Remove(unit);
            }
        }

        private void ForgetUnits()
        {
            foreach (var info in units.Values) Recycle(info);
            units.Clear();
        }

        private void Recycle(UnitInfo info)
        {
            for (int i = 1; i < info.keys.Length; i++)
            {
                if (info.keys[i] != null) keyPool.Push(info.keys[i]);
            }
            Array.Clear(info.keys, 0, info.keys.Length);
            info.count = 0;
            infoPool.Push(info);
        }

        private void HideAll()
        {
            var handler = RangeIndicatorHandler.instance;
            if (handler != null)
            {
                foreach (var ring in shownRings) handler.HideIndicator(ring.key, ring.type);
            }
            shownRings.Clear();
        }

        private GameObject TakeKey() => keyPool.Count > 0 ? keyPool.Pop() : NewKey();

        private static GameObject NewKey()
        {
            var key = new GameObject("PalaceMod unit range key") { hideFlags = HideFlags.HideAndDontSave };
            key.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(key);
            return key;
        }

        private string DevCommand(string[] args)
        {
            var command = CommandUnits.instance;
            if (command == null || TagManager.instance == null) throw new InvalidOperationException("not in a level");
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "dump";
            string filter = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null;
            var sb = new StringBuilder();
            switch (sub)
            {
                case "slots":
                    foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>(true))
                    {
                        if (slot.GetComponentInChildren<UnitRespawnerForBuildings>(true) == null) continue;
                        sb.AppendLine($"{slot.name} level={slot.Level} active={slot.gameObject.activeInHierarchy}");
                    }
                    return sb.ToString();
                case "build":
                    if (filter == null) throw new ArgumentException("build <name>");
                    foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>(true))
                    {
                        if (slot.Level > 0 || slot.upgrades.Count == 0 || slot.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (slot.GetComponentInChildren<UnitRespawnerForBuildings>(true) == null) continue;
                        slot.ActivateIgrnoringActivatorLevel();
                        slot.ForceManualUpgradeWithoutFeedbackEffects(slot.upgrades[0].upgradeBranches[0]);
                        GameplayImpact.Mark("Built a troop building for free (dev command)");
                        sb.AppendLine($"built {slot.name}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "nothing matched";
                case "command":
                    foreach (var tagged in TagManager.instance.PlayerUnits)
                    {
                        if (tagged == null || (filter != null && tagged.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        command.OnUnitAdd(tagged, false);
                    }
                    command.commanding = command.PlayerUnitsCommanding.Count > 0;
                    return $"commanding {command.PlayerUnitsCommanding.Count}";
                case "release":
                    command.ForceCommandingEnd();
                    command.commanding = false;
                    return $"commanding {command.PlayerUnitsCommanding.Count}";
                case "dump":
                    foreach (var tagged in TagManager.instance.PlayerUnits)
                    {
                        if (tagged == null || tagged.Hp == null || tagged.Hp.PathfindMovement is not PathfindMovementPlayerunit unit) continue;
                        sb.Append(unit.name).Append(": attacks");
                        foreach (var attack in unit.autoAttacks ?? Array.Empty<AutoAttack>())
                        {
                            sb.Append(" [");
                            foreach (var p in attack.targetPriorities)
                                sb.Append(p.range.ToString("0.#")).Append(" +").Append(string.Join("+", p.mustHaveTags)).Append(" -").Append(string.Join("-", p.mayNotHaveTags)).Append("; ");
                            sb.Append(']');
                        }
                        sb.Append(" engage");
                        foreach (var p in unit.targetPriorities) sb.Append(' ').Append(p.range.ToString("0.#"));
                        if (units.TryGetValue(unit, out var info)) sb.Append($" showing {info.count}{(info.engage > 0f ? "+engage" : "")}");
                        sb.AppendLine();
                    }
                    sb.Append($"rings shown: {shownRings.Count}");
                    return sb.ToString();
                default:
                    throw new ArgumentException("unitrange slots|build <name>|command [name]|release|dump");
            }
        }
    }
}
