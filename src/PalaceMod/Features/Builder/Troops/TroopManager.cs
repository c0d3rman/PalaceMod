using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Custom player troops (WaveSetDef.troops). Troops aren't spawned: they sit inactive inside their building in the
    level scene until an upgrade switches them on (see TroopIndex). So when a custom session's host map loads (after
    Awake, before any Start), every troop whose type a design replaces gets the design's overrides written in place
    through IUnitFactory.ApplyTo. The game then initialises them as usual, so perks, blacksmith upgrades, respawning,
    commands and the pause menu's unit list all work on the custom values. Vanilla runs are never touched.

    Infrastructure for the Builder feature (no settings of its own); BuilderManager.Enabled governs launches.
*/
namespace PalaceMod
{
    [ManagerOrder(-20)]
    public sealed class TroopManager : BaseManager
    {
        /// <summary>Troops given overrides on the current map, by "&lt;troop id&gt; @ &lt;building&gt; -&gt; &lt;unit ref&gt;".</summary>
        private static readonly Dictionary<string, int> applied = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            SceneManager.sceneLoaded += OnSceneLoaded;
            DevCommands.Register("builder-troops",
                "builder-troops list|scene|live|build <building>   custom player troops: catalog troops, overrides on this map, live troop stats, build a building", Command);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == (SceneTransitionManager.instance?.uiScene ?? "_UI")) return;
            applied.Clear();
            var session = BuilderServices.Sessions.Current;
            if (session == null || scene.name != session.HostScene || (session.WaveSet?.Troops?.Count ?? 0) == 0) return;
            try
            {
                Apply(scene, session.Pack, session.WaveSet.Troops);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Builder: applying custom troops failed: {e}");
            }
        }

        private static void Apply(Scene scene, DesignPack pack, List<TroopDef> troops)
        {
            // Resolve each replacement to the catalog id of the troop it stands in for (the unit's base).
            var rules = new List<(string troopId, string building, string unitRef)>();
            foreach (var troop in troops)
            {
                var r = ContentRef.Parse(troop.Unit);
                var owner = r.Kind == RefKind.PackUnit && r.Pack != pack.Id ? BuilderServices.Packs.Find(r.Pack)?.Pack : pack;
                var def = owner?.FindUnit(r.Id);
                var baseUnit = def == null ? null : BuilderServices.Catalog.FindUnit(ContentRef.Parse(def.Base).Id);
                if (baseUnit == null || baseUnit.Side != UnitSide.Player)
                {
                    Plugin.Logger.LogWarning($"Builder: troop {troop.Unit} skipped: it isn't a custom player unit with a known base");
                    continue;
                }
                rules.Add((baseUnit.Id, troop.Building, troop.Unit));
            }
            if (rules.Count == 0) return;

            int count = 0;
            var errors = new HashSet<string>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var unit in TroopIndex.TroopsUnder(root))
                {
                    string id = TroopIndex.TroopId(unit);
                    string building = TroopIndex.BuildingOf(unit);
                    // A building-specific replacement beats a general one.
                    var rule = rules.FirstOrDefault(x => x.troopId == id && x.building != null && string.Equals(x.building, building, StringComparison.OrdinalIgnoreCase));
                    if (rule.unitRef == null) rule = rules.FirstOrDefault(x => x.troopId == id && x.building == null);
                    if (rule.unitRef == null) continue;
                    bool ok = BuilderServices.Units.ApplyTo(unit, pack, rule.unitRef, out string error);
                    if (error != null) errors.Add($"{rule.unitRef}: {error}");
                    if (!ok) continue;
                    count++;
                    string key = $"{id} @ {building ?? "-"} -> {rule.unitRef}";
                    applied[key] = applied.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            foreach (var e in errors) Plugin.Logger.LogWarning($"Builder: troop override: {e}");
            Plugin.Logger.LogInfo($"Builder: custom troops on {scene.name}: {count} troops overridden ({string.Join("; ", applied.Select(kv => $"{kv.Value}x {kv.Key}"))})");
        }

        // ------------------------------------------------------------------ dev commands

        private static string Command(string[] args)
        {
            switch (args.Length == 0 ? "" : args[0].ToLowerInvariant())
            {
                case "list":
                    return "\n" + string.Join("\n", BuilderServices.Catalog.Units.Where(u => u.Side == UnitSide.Player).Select(u =>
                        $"{u.Id} ({u.DisplayName}): hp={u.MaxHp} speed={u.MoveSpeed} cooldown={u.AttackCooldown} range={u.AttackRange} weapon={u.Weapon} " +
                        $"buildings=[{string.Join(", ", u.Buildings)}] tags=[{string.Join(",", u.Tags)}]"));
                case "scene":
                {
                    var troops = SceneTroops(includeInactive: true).ToList();
                    if (troops.Count == 0) return "no troops in the loaded scenes";
                    return "\n" + string.Join("\n", troops.GroupBy(t => $"{TroopIndex.TroopId(t)} @ {TroopIndex.BuildingOf(t)}").OrderBy(g => g.Key)
                        .Select(g => $"{g.Count()}x {g.Key} (active {g.Count(t => t.activeInHierarchy)})")) +
                        "\noverridden: " + (applied.Count == 0 ? "none" : string.Join("; ", applied.Select(kv => $"{kv.Value}x {kv.Key}")));
                }
                case "live":
                {
                    var troops = SceneTroops(includeInactive: false).ToList();
                    if (troops.Count == 0) return "no active troops (build a troop building first)";
                    return "\n" + string.Join("\n", troops.GroupBy(t => t.GetComponent<ScreenMarkerIcon>()?.UnitName + "|" + TroopIndex.TroopId(t))
                        .Select(g => $"{g.Count()}x {TroopIndex.TroopId(g.First())}: {Describe(g.First())}"));
                }
                case "build":
                {
                    if (args.Length < 2) throw new ArgumentException("usage: builder-troops build <building>[:<choice>], e.g. \"Archery Range:Archers\"");
                    var parts = string.Join(" ", args.Skip(1)).Trim('"').Split(':');
                    string name = parts[0].Trim();
                    var slot = Object.FindObjectsOfType<BuildSlot>().Where(s => string.Equals(s.buildingName, name, StringComparison.OrdinalIgnoreCase) && s.CanBeUpgraded)
                        .OrderBy(s => s.Level).FirstOrDefault() ?? throw new InvalidOperationException($"no active, upgradable \"{name}\" here");
                    var branches = slot.upgrades[slot.Level].upgradeBranches;
                    var branch = parts.Length < 2 ? branches[0]
                        : branches.FirstOrDefault(b => b.choiceDetails?.name?.IndexOf(parts[1].Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                          ?? throw new ArgumentException($"no choice \"{parts[1]}\" (has: {string.Join(", ", branches.Select(b => b.choiceDetails?.name))})");
                    slot.ForceManualUpgradeWithoutFeedbackEffects(branch);
                    return $"{slot.name}: {name} now level {slot.Level} ({branch.choiceDetails?.name}; choices were {string.Join(", ", branches.Select(b => b.choiceDetails?.name))})";
                }
                default:
                    throw new ArgumentException("usage: builder-troops list|scene|live|build <building>");
            }
        }

        private static IEnumerable<GameObject> SceneTroops(bool includeInactive)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in TroopIndex.TroopsUnder(root))
                        if (includeInactive || t.activeInHierarchy) yield return t;
            }
        }

        private static string Describe(GameObject unit)
        {
            var hp = unit.GetComponent<Hp>();
            var icon = unit.GetComponent<ScreenMarkerIcon>();
            var move = unit.GetComponent<PathfindMovementPlayerunit>();
            var attack = unit.GetComponent<AutoAttack>();
            var tagged = unit.GetComponent<TaggedObject>();
            string dmg = attack?.weapon == null ? "-" : string.Join(",", attack.weapon.directDamage.Select(d =>
                $"{(d.requiredTags.Count == 0 ? "any" : string.Join("+", d.requiredTags))}:+{d.damageAdded}x{d.damageMultiplyer}"));
            return $"name=\"{icon?.UnitName}\" hp={hp?.HpValue:0.#}/{hp?.maxHp:0.#} speed={move?.movementSpeed} cooldown={attack?.cooldownDuration} " +
                $"range={(attack == null || attack.targetPriorities.Count == 0 ? 0 : attack.targetPriorities.Max(t => t.range))} dmgMult={attack?.DamageMultiplyer} " +
                $"weapon={attack?.weapon?.name}#{attack?.weapon?.GetInstanceID()} damage=[{dmg}] scale={unit.transform.localScale.x:0.##} " +
                $"tags=[{(tagged == null ? "" : string.Join(",", tagged.Tags))}] materials=[{Materials(unit)}]";
        }

        private static string Materials(GameObject unit) => string.Join(", ", unit.GetComponentsInChildren<Renderer>()
            .SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct()
            .Select(m => m.HasProperty("_BaseColor") ? $"{m.name} {ColorUtility.ToHtmlStringRGB(m.GetColor("_BaseColor"))}" : m.name));
    }
}
