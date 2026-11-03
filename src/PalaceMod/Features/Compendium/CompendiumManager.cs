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
                // A subheading per spawn location (named like the pause menu's spawn panel); under it, enemies grouped
                // like the pause menu (same enemy and elite).
                var places = new List<(string Name, List<(GameObject Prefab, bool Elite, int Count)> Enemies)>();
                int gold = 0;
                foreach (var spawn in wave.spawns)
                {
                    gold += spawn.goldCoins;
                    if (spawn.enemyPrefab == null || spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>() == null) continue;
                    string name = spawn.spawnLine != null ? SpawnLocations.Name(spawn.spawnLine) : "Elsewhere";
                    int p = places.FindIndex(x => x.Name == name);
                    if (p < 0) { places.Add((name, [])); p = places.Count - 1; }
                    var enemies = places[p].Enemies;
                    int i = enemies.FindIndex(g => g.Prefab == spawn.enemyPrefab && g.Elite == spawn.eliteEnemies);
                    if (i < 0) enemies.Add((spawn.enemyPrefab, spawn.eliteEnemies, spawn.count));
                    else enemies[i] = (enemies[i].Prefab, enemies[i].Elite, enemies[i].Count + spawn.count);
                }
                int waveIndex = w;
                foreach (var (place, enemies) in places)
                {
                    Subheading(page, place);
                    foreach (var g in enemies)
                    {
                        var stats = CompendiumStats.Enemy(g.Prefab, g.Elite, waveIndex);
                        page.Row($"{g.Count}× {EliteName(stats.Name, g.Elite)}", $"{CompendiumStats.Format(stats.Actual.Hp)}<sprite name=\"heart\">",
                            () => StatBlock(CompendiumStats.Enemy(g.Prefab, g.Elite, waveIndex), true), icon: stats.Icon);
                    }
                }
                if (gold > 0) page.Row("Gold dropped", Coin(gold), "Gold this night's enemies drop when killed.");
            }
        }

        // A spawn location under a night's header: smaller than the header, in the game's bold body font.
        private static void Subheading(ModPage page, string text)
        {
            var tmp = GameWidgets.Text(page.Content, "Subheading", text, ModStyle.BoldFont, 28f, ModStyle.Gold, TMPro.TextAlignmentOptions.BottomLeft);
            tmp.margin = new Vector4(0f, 10f, 0f, 0f);
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
                    var branches = Branches(upgrade);
                    bool choice = branches.Count > 1;
                    page.Row(label, Coin(upgrade.cost), () => LevelText(slot, lvl, withChanges: !choice));
                    // A choice gets one row per option (its icon, its own short tooltip), so no tooltip lists them all.
                    if (!choice) continue;
                    foreach (var branch in branches)
                    {
                        var details = branch.choiceDetails;
                        bool locked = details.requiresUnlocked != null && !details.requiresUnlocked.IsUnlocked;
                        string name = CompendiumStats.Translate(slot.GET_LOCIDENTIFIER_CHOICENAME(details));
                        page.Row(locked ? $"{name} ({CompendiumStats.Translate("Menu/Locked")})" : name, Changes(branch), () =>
                        {
                            var description = CompendiumStats.Translate(slot.GET_LOCIDENTIFIER_CHOICEDESCRIPTION(details));
                            return locked ? $"{description}\n\n{details.requiresUnlocked.GetLockedTooltip()}".Trim() : description;
                        }, icon: details.icon);
                    }
                }
            }
        }

        private static List<BuildSlot.UpgradeBranch> Branches(BuildSlot.Upgrade upgrade) =>
            upgrade.upgradeBranches.Where(b => b.choiceDetails == null || !b.choiceDetails.disabledInThisMode).ToList();

        // The game's text for a level, plus its health and income changes when it has no choice.
        private static string LevelText(BuildSlot slot, int level, bool withChanges)
        {
            var text = CompendiumStats.Translate(slot.GET_LOCIDENTIFIER_UPGRADE(level));
            if (!withChanges) return text;
            var changes = string.Join("   ", Branches(slot.OwnUpgrades[level]).Select(Changes).Where(c => c.Length > 0));
            return changes.Length > 0 ? (text + "\n\n" + changes).Trim() : text;
        }

        private static string Changes(BuildSlot.UpgradeBranch branch)
        {
            var changes = new List<string>();
            if (branch.hpChange != 0) changes.Add($"{(branch.hpChange > 0 ? "+" : "")}{branch.hpChange}<sprite name=\"heart\">");
            if (branch.goldIncomeChange != 0) changes.Add($"{(branch.goldIncomeChange > 0 ? "+" : "")}{branch.goldIncomeChange}<sprite name=\"coin\">/day");
            return string.Join("  ", changes);
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
                // Locked items stay hidden like in the game's loadout screen: one row says how many, and how to unlock them.
                var locked = list.Where(e => !e.IsUnlocked && !PerkManager.IsEquipped(e)).ToList();
                foreach (var e in list.Except(locked))
                {
                    var item = e;
                    page.Row(CompendiumStats.EquippableName(item), PerkManager.IsEquipped(item) ? "Equipped" : "", () =>
                    {
                        var body = CompendiumStats.Translate(item.LOCIDENTIFIER_DESCRIPTION);
                        if (item is EquippableMutation mutation) body += $"\n\nScore on a win: {CompendiumStats.Percent(mutation.scoreMultiplyerOnWin)}";
                        return body;
                    }, icon: item.icon);
                }
                if (locked.Count > 0)
                    page.Row(CompendiumStats.Translate("Menu/Locked"), locked.Count.ToString(), () =>
                        string.Join("\n", locked.Select(e => e.GetLockedTooltip()).Distinct()));
            }
            var all = perks.allEquippables.Where(e => e != null).ToList();
            Section("Weapons", all.OfType<EquippableWeapon>());
            Section("Perks", all.OfType<EquippablePerk>());
            Section("Mutators", all.OfType<EquippableMutation>());
            Section("Building unlocks", all.OfType<EquippableBuildingUpgrade>());
        }
    }
}
