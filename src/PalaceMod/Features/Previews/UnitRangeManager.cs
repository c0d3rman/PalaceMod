using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Draws attack-range rings around your units, using the game's own RangeIndicatorHandler (the rings towers show).

    The game (as of 2.13) only shows ranges for buildings: the focused tower's range, upgrade previews, shrines, and
    optionally every tower's range while interacting. Nothing shows a unit's range, so this fills that gap:
    - units you're commanding (default on),
    - your units within the king's command radius, i.e. the ones a command press would pick up (default off),
    - all your units while a hold key is held (unbound by default).

    A unit's rings: one per distinct attack reach (each AutoAttack's longest target-priority range; the shorter
    priorities on the same attack are just target filters inside that reach), plus optionally its engage range (how
    far it walks to fight once placed) when that's longer. Rings are computed once when a unit starts showing and
    reused until it stops.

    RangeIndicatorHandler keys rings by (type, sender GameObject). Units are never senders for the game's own rings,
    so the first ring uses the unit itself as the key; extra rings use pooled empty GameObjects as keys. Attack rings
    use the Built style (the solid ring a built tower shows) and the engage ring uses the Preview style. Every frame
    the rings that should be visible are refreshed and any unit that dropped out (released, dead, despawned) has its
    rings faded out, so no patches are needed.
*/
namespace PalaceMod
{
    public class UnitRangeManager : BaseManager
    {
        public override string FeatureSection => "Unit ranges";
        public override string FeatureDescription => "Shows attack-range rings around your units, like the ones towers show.";

        private const int MaxAttackRings = 3;
        private const float SameRangeTolerance = 0.25f;

        private ConfigEntry<bool> showCommanded;
        private ConfigEntry<bool> showNearby;
        private ConfigEntry<bool> showEngageRange;
        private Keybind showAllKey;

        private struct Ring
        {
            public float range;
            public GameObject key;
            public RangeIndicatorHandler.IndicatorType type;
        }

        private sealed class UnitRings
        {
            public readonly Ring[] rings = new Ring[MaxAttackRings + 1];
            public int count;
        }

        // Units whose rings are showing, and the scratch set of units that should show this frame.
        private Dictionary<PathfindMovementPlayerunit, UnitRings> shown = new();
        private Dictionary<PathfindMovementPlayerunit, UnitRings> wanted = new();
        private readonly Stack<UnitRings> ringPool = new();
        private readonly Stack<GameObject> keyPool = new();
        private readonly List<float> scratchRanges = new();

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            showCommanded = plugin.Config.Bind(FeatureSection, "Commanded units", true,
                "Show the ranges of the units following you.");
            showNearby = plugin.Config.Bind(FeatureSection, "Units in command radius", false,
                "Show the ranges of your units close enough to be picked up by a command.");
            showEngageRange = plugin.Config.Bind(FeatureSection, "Engage range", false,
                "Also show a lighter ring for how far a unit will walk to fight once placed, when that's beyond its attack range.");
            showAllKey = Keybinds.Register(FeatureSection, "show-all", "Show all unit ranges", KeyboardShortcut.Empty,
                "Hold to show the ranges of all your units.");
            showEngageRange.SettingChanged += (_, _) => HideAll();

            SceneManager.sceneLoaded += (_, _) => ForgetAll();

            DevCommands.Register("unitrange", "unitrange slots|build <name>|command [name]|release|dump   set up and inspect unit ranges", DevCommand);
        }

        protected override void OnFeatureDisabled() => HideAll();

        public override void Update()
        {
            var command = CommandUnits.instance;
            var handler = RangeIndicatorHandler.instance;
            if (command == null || handler == null)
            {
                if (shown.Count > 0) ForgetAll();
                return;
            }

            if (TagManager.instance == null) return;

            if (showAllKey.Held)
            {
                foreach (var tagged in TagManager.instance.PlayerUnits) Want(tagged);
            }
            else
            {
                if (showCommanded.Value)
                {
                    foreach (var unit in command.PlayerUnitsCommanding) Want(unit);
                }
                if (showNearby.Value && !command.commanding)
                {
                    Vector3 king = command.transform.position;
                    foreach (var tagged in TagManager.instance.PlayerUnits)
                    {
                        if (tagged != null && TagManager.instance.MeasureDistanceToTaggedObject(tagged, king) <= command.attractRange) Want(tagged);
                    }
                }
            }

            foreach (var pair in shown)
            {
                if (!wanted.ContainsKey(pair.Key)) Release(pair.Value, handler);
            }
            shown.Clear();
            (shown, wanted) = (wanted, shown);

            foreach (var pair in shown)
            {
                Vector3 position = pair.Key.transform.position;
                var unitRings = pair.Value;
                for (int i = 0; i < unitRings.count; i++)
                {
                    ref var ring = ref unitRings.rings[i];
                    handler.ShowIndicator(position, ring.range, ring.key, ring.type);
                }
            }
        }

        private void Want(TaggedObject tagged)
        {
            if (tagged == null || tagged.Hp == null) return;
            if (tagged.Hp.PathfindMovement is PathfindMovementPlayerunit unit) Want(unit);
        }

        private void Want(PathfindMovementPlayerunit unit)
        {
            if (unit == null || !unit.isActiveAndEnabled || wanted.ContainsKey(unit)) return;
            if (unit.hp != null && !unit.hp.Alive) return;
            if (!shown.TryGetValue(unit, out var rings)) rings = BuildRings(unit);
            wanted.Add(unit, rings);
        }

        private UnitRings BuildRings(PathfindMovementPlayerunit unit)
        {
            var result = ringPool.Count > 0 ? ringPool.Pop() : new UnitRings();
            result.count = 0;

            // Each AutoAttack reaches as far as its longest target priority.
            scratchRanges.Clear();
            var attacks = unit.autoAttacks;
            if (attacks == null || attacks.Length == 0) attacks = unit.GetComponents<AutoAttack>();
            foreach (var attack in attacks)
            {
                if (attack == null) continue;
                float reach = 0f;
                foreach (var priority in attack.targetPriorities) reach = Mathf.Max(reach, priority.range);
                if (reach > 0f) AddDistinct(scratchRanges, reach);
            }
            scratchRanges.Sort((a, b) => b.CompareTo(a));
            float longestAttack = scratchRanges.Count > 0 ? scratchRanges[0] : 0f;

            for (int i = 0; i < scratchRanges.Count && i < MaxAttackRings; i++)
            {
                result.rings[result.count++] = new Ring
                {
                    range = scratchRanges[i],
                    key = i == 0 ? unit.gameObject : TakeKey(),
                    type = RangeIndicatorHandler.IndicatorType.Built,
                };
            }

            if (showEngageRange.Value)
            {
                float engage = 0f;
                foreach (var priority in unit.targetPriorities) engage = Mathf.Max(engage, priority.range);
                if (engage > longestAttack + SameRangeTolerance)
                {
                    result.rings[result.count++] = new Ring
                    {
                        range = engage,
                        key = unit.gameObject,
                        type = RangeIndicatorHandler.IndicatorType.Preview,
                    };
                }
            }
            return result;
        }

        private static void AddDistinct(List<float> ranges, float range)
        {
            foreach (float existing in ranges)
            {
                if (Mathf.Abs(existing - range) <= SameRangeTolerance) return;
            }
            ranges.Add(range);
        }

        private GameObject TakeKey()
        {
            if (keyPool.Count > 0) return keyPool.Pop();
            var key = new GameObject("PalaceMod unit range key") { hideFlags = HideFlags.HideAndDontSave };
            key.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(key);
            return key;
        }

        // Fades the unit's rings out (if the handler is still alive) and returns its pooled objects.
        private void Release(UnitRings unitRings, RangeIndicatorHandler handler)
        {
            for (int i = 0; i < unitRings.count; i++)
            {
                ref var ring = ref unitRings.rings[i];
                if (handler != null) handler.HideIndicator(ring.key, ring.type);
                if (ring.key != null && ring.key.name == "PalaceMod unit range key") keyPool.Push(ring.key);
                ring.key = null;
            }
            unitRings.count = 0;
            ringPool.Push(unitRings);
        }

        private void HideAll()
        {
            var handler = RangeIndicatorHandler.instance;
            foreach (var pair in shown) Release(pair.Value, handler);
            shown.Clear();
        }

        // Level changed: the handler and its rings went with the old scene.
        private void ForgetAll()
        {
            foreach (var pair in shown) Release(pair.Value, null);
            shown.Clear();
        }

        private string DevCommand(string[] args)
        {
            var command = CommandUnits.instance;
            if (command == null) throw new InvalidOperationException("not in a level");
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "dump";
            string filter = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null;
            var sb = new StringBuilder();
            switch (sub)
            {
                case "slots":
                    foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
                    {
                        if (slot.GetComponentInChildren<UnitRespawnerForBuildings>(true) == null) continue;
                        sb.AppendLine($"{slot.name} level={slot.Level} state={slot.State}");
                    }
                    return sb.ToString();
                case "build":
                    if (filter == null) throw new ArgumentException("build <name>");
                    foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
                    {
                        if (slot.Level > 0 || slot.upgrades.Count == 0 || slot.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (slot.GetComponentInChildren<UnitRespawnerForBuildings>(true) == null) continue;
                        slot.ForceManualUpgradeWithoutFeedbackEffects(slot.upgrades[0].upgradeBranches[0]);
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
                    return $"commanding {command.PlayerUnitsCommanding.Count}";
                case "dump":
                    foreach (var tagged in TagManager.instance.PlayerUnits)
                    {
                        if (tagged?.Hp?.PathfindMovement is not PathfindMovementPlayerunit unit) continue;
                        sb.Append(unit.name).Append(": attacks");
                        foreach (var attack in unit.autoAttacks ?? Array.Empty<AutoAttack>())
                        {
                            sb.Append(" [");
                            foreach (var p in attack.targetPriorities) sb.Append(p.range.ToString("0.#")).Append(' ');
                            sb.Append(']');
                        }
                        sb.Append(" engage");
                        foreach (var p in unit.targetPriorities) sb.Append(' ').Append(p.range.ToString("0.#"));
                        sb.Append(shown.TryGetValue(unit, out var r) ? $" showing {r.count}" : "").AppendLine();
                    }
                    return sb.Length > 0 ? sb.ToString() : "no player units";
                default:
                    throw new ArgumentException("unitrange slots|build <name>|command [name]|release|dump");
            }
        }
    }
}
