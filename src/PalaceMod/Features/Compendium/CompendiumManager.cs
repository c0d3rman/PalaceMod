using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/*
    The Compendium: a menu opened from the pause menu with this level's waves, the enemies in them, your living units,
    the level's buildings and their upgrade trees, and every perk, weapon and mutator. Everything is read from the
    running game (CompendiumStats for the numbers), so it follows game updates and never ships game data.
*/
namespace PalaceMod
{
    public class CompendiumManager : BaseManager
    {
        public override string FeatureSection => "Compendium";
        public override string FeatureDescription => "Adds a Compendium to the pause menu: this level's waves, enemies, your units, buildings and upgrades, and all perks, with actual values.";

        private ModFrame frame;
        private bool showEarlierNights;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            frame = new ModFrame("Compendium")
                .AddTab("Waves", BuildWaves)
                .AddTab("Enemies", BuildEnemies)
                .AddTab("Units", BuildUnits)
                .AddTab("Buildings", BuildBuildings)
                .AddTab("Perks", BuildPerks);
            PauseMenu.AddButton("compendium", "Compendium", Open, order: 10, visible: () => IsEnabled);
            DevCommands.Register("compendium", "compendium [close]     open (or close) the Compendium", args =>
            {
                if (args.Length > 0 && args[0] == "close") frame.Close();
                else Open();
                return frame.IsOpen ? "open" : "closed";
            });
        }

        private void Open()
        {
            showEarlierNights = false;
            frame.Open();
        }

        protected override void OnFeatureDisabled()
        {
            if (frame.IsOpen) frame.Close();
            PauseMenu.Refresh();
        }

        protected override void OnFeatureEnabled() => PauseMenu.Refresh();

        private static string Coin(int amount) => $"{amount}<sprite name=\"coin\">";

        // ---------------------------------------------------------------- shared text

        /// <summary>A stats block for a tooltip: one line per stat (base → actual where they differ), then what changed them.</summary>
        internal static string StatBlock(CombatantStats s, bool isEnemy)
        {
            var sb = new StringBuilder();
            void Line(string label, float b, float a)
            {
                if (a == 0f && b == 0f) return;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(label).Append(": ").Append(StatDetailsManager.ValueText(b, a));
            }
            Line("Health", s.Base.Hp, s.Actual.Hp);
            Line("Damage", s.Base.Damage, s.Actual.Damage);
            Line("Attack cooldown", s.Base.Cooldown, s.Actual.Cooldown);
            Line("Range", s.Base.Range, s.Actual.Range);
            Line("Speed", s.Base.Speed, s.Actual.Speed);
            Line("Projectile speed", s.Base.ProjectileSpeed, s.Actual.ProjectileSpeed);
            var details = StatDetailsManager.DetailLines(s, isEnemy);
            if (details.Length > 0) sb.Append("\n\n").Append(details);
            return sb.ToString();
        }

        private static void NotInLevel(ModPage page) => page.Text("Open the Compendium from the pause menu during a level to see this.");

        private static string EliteName(string name, bool elite) => elite ? CompendiumStats.Translate("Menu/Elite Prefix") + " " + name : name;

        // ---------------------------------------------------------------- tabs

        private void BuildWaves(ModPage page)
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null || spawner.waves.Count == 0) { NotInLevel(page); return; }
            int next = CompendiumStats.NextWaveIndex();
            int first = 0;
            if (next > 0 && !showEarlierNights)
            {
                first = Mathf.Min(next, spawner.waves.Count - 1);
                page.Button($"Show earlier nights ({first})", () =>
                {
                    showEarlierNights = true;
                    frame.Refresh();
                });
            }
            for (int w = first; w < spawner.waves.Count; w++)
            {
                var wave = spawner.waves[w];
                string state = w < next ? " (done)" : w == next ? (DayNightCycle.Instance?.CurrentTimestate == DayNightCycle.Timestate.Night ? " (now)" : " (next)") : "";
                page.Header($"{CompendiumStats.Translate("Menu/Night")} {w + 1}/{spawner.waves.Count}{state}");
                if (wave.difficultyMulti != 1f)
                    page.Text($"{CompendiumStats.Translate("Menu/DifficultyBoost")}: {CompendiumStats.Percent(wave.difficultyMulti)} health, {CompendiumStats.Percent(Mathf.Lerp(1f, wave.difficultyMulti, 0.5f))} damage");
                // Group like the pause menu (same enemy and elite), keeping the spawn locations.
                var groups = new List<(GameObject Prefab, bool Elite, int Count, List<string> Lines)>();
                int gold = 0;
                foreach (var spawn in wave.spawns)
                {
                    gold += spawn.goldCoins;
                    if (spawn.enemyPrefab == null || spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>() == null) continue;
                    int i = groups.FindIndex(g => g.Prefab == spawn.enemyPrefab && g.Elite == spawn.eliteEnemies);
                    string line = spawn.spawnLine != null ? spawn.spawnLine.name : null;
                    if (i < 0) groups.Add((spawn.enemyPrefab, spawn.eliteEnemies, spawn.count, line != null ? [line] : []));
                    else
                    {
                        var g = groups[i];
                        if (line != null && !g.Lines.Contains(line)) g.Lines.Add(line);
                        groups[i] = (g.Prefab, g.Elite, g.Count + spawn.count, g.Lines);
                    }
                }
                int waveIndex = w;
                foreach (var g in groups)
                {
                    var stats = CompendiumStats.Enemy(g.Prefab, g.Elite, waveIndex);
                    var lines = g.Lines;
                    page.Row($"{g.Count}× {EliteName(stats.Name, g.Elite)}", $"{CompendiumStats.Format(stats.Actual.Hp)}<sprite name=\"heart\">", () =>
                    {
                        var body = StatBlock(CompendiumStats.Enemy(g.Prefab, g.Elite, waveIndex), true);
                        if (lines.Count > 0) body += "\n\nFrom: " + string.Join(", ", lines);
                        return body;
                    }, icon: stats.Icon);
                }
                if (gold > 0) page.Row("Gold dropped", Coin(gold), "Gold this night's enemies drop when killed.");
            }
        }

        private void BuildEnemies(ModPage page)
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null || spawner.waves.Count == 0) { NotInLevel(page); return; }
            int next = Mathf.Clamp(CompendiumStats.NextWaveIndex(), 0, spawner.waves.Count - 1);
            // Every enemy type in this level, in order of first appearance, with the nights it comes.
            var order = new List<GameObject>();
            var nights = new Dictionary<GameObject, List<string>>();
            var elites = new HashSet<GameObject>();
            for (int w = 0; w < spawner.waves.Count; w++)
                foreach (var spawn in spawner.waves[w].spawns)
                {
                    var prefab = spawn.enemyPrefab;
                    if (prefab == null || prefab.GetComponent<ScreenMarkerIcon>() == null) continue;
                    if (!nights.TryGetValue(prefab, out var list)) { list = []; nights[prefab] = list; order.Add(prefab); }
                    string n = (w + 1) + (spawn.eliteEnemies ? "*" : "");
                    if (!list.Contains(n)) list.Add(n);
                    if (spawn.eliteEnemies) elites.Add(prefab);
                }
            page.Text($"Actual values are for night {next + 1}. * = elite.");
            foreach (var prefab in order)
            {
                var p = prefab;
                var stats = CompendiumStats.Enemy(p, false, next);
                page.Row(stats.Name, $"{CompendiumStats.Format(stats.Base.Hp)}<sprite name=\"heart\">", () =>
                {
                    var body = new StringBuilder(StatBlock(CompendiumStats.Enemy(p, false, next), true));
                    if (elites.Contains(p))
                    {
                        var elite = CompendiumStats.Enemy(p, true, next);
                        body.Append($"\n\n{CompendiumStats.Translate("Menu/Elite Prefix")}: {CompendiumStats.Format(elite.Actual.Hp)} health, {CompendiumStats.Format(elite.Actual.Damage)} damage");
                    }
                    var description = p.GetComponent<ScreenMarkerIcon>().UnitDescription;
                    if (!string.IsNullOrEmpty(description)) body.Insert(0, description + "\n\n");
                    body.Append("\n\nNights: ").Append(string.Join(", ", nights[p]));
                    return body.ToString();
                }, icon: stats.Icon);
            }
        }

        private void BuildUnits(ModPage page)
        {
            if (TagManager.instance == null) { NotInLevel(page); return; }
            var groups = CompendiumStats.LiveUnitGroups();
            if (groups.Count == 0) { page.Text("You have no units right now. Build a barracks or another unit building to recruit some."); return; }
            page.Text("Your living units. Damage is against a plain enemy; the tooltip lists this night's enemies that take a different amount.");
            int wave = CompendiumStats.NextWaveIndex();
            foreach (var group in groups)
            {
                var unit = group[0];
                var stats = CompendiumStats.Unit(unit, wave);
                page.Row($"{stats.Name} ×{group.Count}", $"{CompendiumStats.Format(stats.Actual.Hp)}<sprite name=\"heart\">", () =>
                {
                    if (unit == null) return "This unit is gone.";
                    var marker = unit.GetComponent<ScreenMarkerIcon>();
                    var description = marker != null ? marker.UnitDescription : "";
                    var block = StatBlock(CompendiumStats.Unit(unit, wave), false);
                    return string.IsNullOrEmpty(description) ? block : description + "\n\n" + block;
                }, icon: stats.Icon);
            }
        }

        private void BuildBuildings(ModPage page)
        {
            if (EnemySpawner.instance == null) { NotInLevel(page); return; }
            // Building slots with their own upgrade tree, grouped by building and tree (e.g. all houses together).
            var groups = new List<(string Key, List<BuildSlot> Slots)>();
            // Include inactive slots: buildings that unlock later are hidden until their prerequisite is upgraded.
            foreach (var slot in Resources.FindObjectsOfTypeAll<BuildSlot>().Where(b => b.gameObject.scene.isLoaded))
            {
                if (slot.ActivatorUpgradesThis || slot.OwnUpgrades == null || slot.OwnUpgrades.Count == 0) continue;
                var key = slot.buildingName + "|" + string.Join(",", slot.OwnUpgrades.Select(u => u.cost));
                int i = groups.FindIndex(g => g.Key == key);
                if (i < 0) groups.Add((key, [slot]));
                else groups[i].Slots.Add(slot);
            }
            if (groups.Count == 0) { page.Text("This level has no buildings."); return; }
            foreach (var (_, slots) in groups.OrderBy(g => g.Slots[0].OwnUpgrades[0].cost).ThenBy(g => g.Slots[0].buildingName))
            {
                var slot = slots[0];
                int built = slots.Count(s => s.Level > 0);
                page.Header(CompendiumStats.Translate(slot.LOCIDENTIFIER_NAME) + (slots.Count > 1 ? $" ×{slots.Count}" : ""));
                if (built > 0) page.Text(slots.Count > 1 ? $"Built: {built}. Levels: {string.Join(", ", slots.Select(s => s.Level))}" : $"Level {slot.Level}");
                var upgrades = slot.OwnUpgrades;
                for (int level = 0; level < upgrades.Count; level++)
                {
                    var upgrade = upgrades[level];
                    if (upgrade.disableInThisMode) continue;
                    int lvl = level;
                    var label = level == 0 ? CompendiumStats.Translate("Tooltip/Build") : $"{CompendiumStats.Translate("Menu/Level")} {level + 1}";
                    page.Row(label, Coin(upgrade.cost), () => UpgradeText(slot, lvl));
                }
            }
        }

        private static string UpgradeText(BuildSlot slot, int level)
        {
            var upgrade = slot.OwnUpgrades[level];
            var sb = new StringBuilder(CompendiumStats.Translate(slot.GET_LOCIDENTIFIER_UPGRADE(level)));
            var branches = upgrade.upgradeBranches.Where(b => b.choiceDetails == null || !b.choiceDetails.disabledInThisMode).ToList();
            foreach (var branch in branches)
            {
                sb.Append("\n\n");
                if (branches.Count > 1 && branch.choiceDetails != null)
                {
                    sb.Append("<b>").Append(CompendiumStats.Translate(slot.GET_LOCIDENTIFIER_CHOICENAME(branch.choiceDetails))).Append("</b>");
                    var description = CompendiumStats.Translate(slot.GET_LOCIDENTIFIER_CHOICEDESCRIPTION(branch.choiceDetails));
                    if (!string.IsNullOrEmpty(description)) sb.Append('\n').Append(description);
                    if (branch.choiceDetails.requiresUnlocked != null && !branch.choiceDetails.requiresUnlocked.IsUnlocked) sb.Append("\n(").Append(CompendiumStats.Translate("Menu/Locked")).Append(')');
                    sb.Append('\n');
                }
                var changes = new List<string>();
                if (branch.hpChange != 0) changes.Add($"{(branch.hpChange > 0 ? "+" : "")}{branch.hpChange}<sprite name=\"heart\">");
                if (branch.goldIncomeChange != 0) changes.Add($"{(branch.goldIncomeChange > 0 ? "+" : "")}{branch.goldIncomeChange}<sprite name=\"coin\"> per day");
                if (changes.Count > 0) sb.Append(string.Join("   ", changes));
            }
            return sb.ToString().TrimEnd();
        }

        private void BuildPerks(ModPage page)
        {
            var perks = PerkManager.instance;
            if (perks == null) { page.Text("Perks aren't loaded yet."); return; }
            void Section(string title, IEnumerable<Equippable> items)
            {
                var list = items.ToList();
                if (list.Count == 0) return;
                page.Header(title);
                foreach (var e in list)
                {
                    var item = e;
                    bool unlocked = item.IsUnlocked;
                    string value = PerkManager.IsEquipped(item) ? "Equipped" : unlocked ? "" : CompendiumStats.Translate("Menu/Locked");
                    string name = unlocked ? CompendiumStats.EquippableName(item) : "???";
                    page.Row(name, value, () =>
                    {
                        if (!item.IsUnlocked) return item.GetLockedTooltip();
                        var body = CompendiumStats.Translate(item.LOCIDENTIFIER_DESCRIPTION);
                        if (item is EquippableMutation mutation) body += $"\n\nScore on a win: {CompendiumStats.Percent(mutation.scoreMultiplyerOnWin)}";
                        return body;
                    }, icon: unlocked ? item.icon : null);
                }
            }
            var all = perks.allEquippables.Where(e => e != null).ToList();
            Section("Weapons", all.OfType<EquippableWeapon>());
            Section("Perks", all.OfType<EquippablePerk>());
            Section("Mutators", all.OfType<EquippableMutation>());
            Section("Building unlocks", all.OfType<EquippableBuildingUpgrade>());
        }
    }
}
