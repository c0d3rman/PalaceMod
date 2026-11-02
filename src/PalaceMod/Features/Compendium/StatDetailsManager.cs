using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

/*
    Enriches the pause menu's native stats panel (2.13+: hover an enemy of the coming night or one of your units).
    The game shows prefab values for enemies and leaves out most multipliers for units; this shows "base → actual"
    in each row and lists what changed the numbers (night difficulty, elite, perks, Blacksmith upgrades) and damage
    against other targets under the description. Values come from CompendiumStats.

    Respects the game's own "unit stats" setting (SettingsManager.EnableUnitStats): when that's off, nothing changes.
*/
namespace PalaceMod
{
    public class StatDetailsManager : BaseManager
    {
        public override string FeatureSection => "Stat details";
        public override string FeatureDescription => "The pause menu's enemy and unit stats show base → actual values, including night difficulty, elite bonuses, perks and upgrades.";

        // The scene-enemy baseline patch must run even while the feature is off; the tooltip patch checks On itself.
        protected override bool UnpatchWhenDisabled => false;

        internal const string Arrow = " <sprite name=\"arrow_right\"> ";

        private static readonly AccessTools.FieldRef<PerkSelectionTooltipHelper, TFUIEnemy> CurrentEnemy = AccessTools.FieldRefAccess<PerkSelectionTooltipHelper, TFUIEnemy>("currentEnemyElement");
        private static readonly AccessTools.FieldRef<PerkSelectionTooltipHelper, TFUIPlayerUnit> CurrentUnit = AccessTools.FieldRefAccess<PerkSelectionTooltipHelper, TFUIPlayerUnit>("currentPlayerUnitElement");
        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, int> PreviewWave = AccessTools.FieldRefAccess<PauseUILoadoutHelper, int>("currentPreviewWavenumber");

        private static readonly string[] RowFields = ["maxHPValueText", "speedValueText", "rangeValueText", "attackDamageValueText", "attackCooldownValueText", "projectileSpeedValueText"];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("stats", "stats [wave]           print computed enemy (base → actual) and live unit stats for a wave (default: next)", args =>
                DescribeAll(args.Length > 0 ? int.Parse(args[0]) - 1 : CompendiumStats.NextWaveIndex()));
            DevCommands.Register("statsverify", "statsverify            compare live enemies' HP, damage multiplier, speed and cooldown with the computed values", _ => VerifyLive());
            DevCommands.Register("hitlog", "hitlog [on|off]        log every hit (attacker, target, damage) to the BepInEx log", args =>
            {
                bool on = args.Length == 0 || args[0] != "off";
                // Patched only on request: Hp.TakeDamage is hot.
                var target = AccessTools.Method(typeof(Hp), nameof(Hp.TakeDamage));
                if (on && hitLogHarmony == null)
                {
                    hitLogHarmony = new Harmony(MyPluginInfo.PLUGIN_GUID + ".hitlog");
                    hitLogHarmony.Patch(target, prefix: new HarmonyMethod(typeof(StatDetailsManager), nameof(LogHit)));
                }
                else if (!on && hitLogHarmony != null)
                {
                    hitLogHarmony.UnpatchSelf();
                    hitLogHarmony = null;
                }
                return on ? "hit log on" : "hit log off";
            });
            DevCommands.Register("equip", "equip [clear | <name part> [off]]   list, equip or unequip perks/weapons/mutators (takes effect on the next level load)", args =>
            {
                var perks = PerkManager.instance ?? throw new System.InvalidOperationException("no PerkManager");
                if (args.Length > 0 && args[0] == "clear")
                {
                    PerkManager.ClearAllEquipped();
                    GameplayImpact.Mark("Dev: changed the loadout");
                }
                else if (args.Length > 0)
                {
                    bool off = args[^1] == "off";
                    var query = string.Join(" ", args.Take(args.Length - (off ? 1 : 0)));
                    var match = perks.allEquippables.FirstOrDefault(e => CompendiumStats.EquippableName(e).IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0 || e.name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? throw new System.ArgumentException($"no equippable matching \"{query}\"");
                    PerkManager.SetEquipped(match, !off);
                    GameplayImpact.Mark("Dev: changed the loadout");
                }
                return "equipped: " + string.Join(", ", perks.CurrentlyEquipped.Select(e => $"{CompendiumStats.EquippableName(e)} ({e.name})"));
            });
            DevCommands.Register("wave", "wave <n>               make night n the next night (by day; dev only)", args =>
            {
                var spawner = EnemySpawner.instance ?? throw new System.InvalidOperationException("not in a level");
                if (args.Length == 0) throw new System.ArgumentException("usage: wave <n>");
                int n = int.Parse(args[0]);
                if (n < 1 || n > spawner.WaveCount) throw new System.ArgumentException($"night must be 1..{spawner.WaveCount}");
                AccessTools.Field(typeof(EnemySpawner), "wavenumber").SetValue(spawner, n - 2);
                GameplayImpact.Mark("Dev: skipped to a night");
                return $"next night: {n}";
            });
            DevCommands.Register("tooltip", "tooltip                print the pause menu's open tooltip (title, description, stat rows)", _ => DescribeTooltip());
        }

        protected override void OnFeatureDisabled() => RefreshOpenTooltips();
        protected override void OnFeatureEnabled() => RefreshOpenTooltips();

        private static void RefreshOpenTooltips()
        {
            foreach (var helper in Object.FindObjectsOfType<PerkSelectionTooltipHelper>()) helper.UpdateTooltip();
        }

        // ---------------------------------------------------------------- the panel

        [HarmonyPatch(typeof(PerkSelectionTooltipHelper), nameof(PerkSelectionTooltipHelper.UpdateTooltip))]
        private static class UpdateTooltip_Patch
        {
            private static void Postfix(PerkSelectionTooltipHelper __instance)
            {
                if (!Feature<StatDetailsManager>.On || SettingsManager.Instance == null || !SettingsManager.Instance.EnableUnitStats) return;
                CombatantStats stats = null;
                var enemy = CurrentEnemy(__instance);
                var unit = CurrentUnit(__instance);
                var pause = __instance.GetComponentInParent<PauseUILoadoutHelper>();
                int wave = pause != null ? PreviewWave(pause) : CompendiumStats.NextWaveIndex();
                if (enemy != null && enemy.Data != null)
                {
                    var prefab = CompendiumStats.FindWavePrefab(wave, enemy.Data.enemyName, enemy.Data.enemyIcon, enemy.Data.eliteEnemy);
                    if (prefab != null) stats = CompendiumStats.Enemy(prefab, enemy.Data.eliteEnemy, wave);
                }
                else if (unit != null && unit.Data != null)
                {
                    var group = CompendiumStats.LiveUnitGroups().FirstOrDefault(g =>
                    {
                        var marker = g[0].GetComponent<ScreenMarkerIcon>();
                        return marker.sprite == unit.Data.unitIcon && marker.UnitName == unit.Data.unitName;
                    });
                    if (group != null) stats = CompendiumStats.Unit(group[0], wave);
                }
                if (stats == null) return;

                if (__instance.UnitStatsDisplay != null && __instance.UnitStatsDisplay.gameObject.activeSelf)
                    WriteRows(__instance.UnitStatsDisplay, stats);
                var extra = DetailLines(stats, enemy != null);
                if (extra.Length > 0)
                {
                    __instance.selectionDescription.text += (string.IsNullOrEmpty(__instance.selectionDescription.text) ? "" : "\n\n") + extra;
                    if (__instance.sizer) __instance.sizer.Trigger();
                }
            }
        }

        // Some levels spawn enemies that live in the scene (bosses, Eldermoles): Spawn.Update activates and modifies that
        // very object, so remember its authored stats before the first night.
        [HarmonyPatch(typeof(EnemySpawner), "Start")]
        private static class EnemySpawner_Start_Patch
        {
            private static void Postfix(EnemySpawner __instance) => CompendiumStats.CaptureSceneBaselines(__instance);
        }

        private static void WriteRows(UnitStatsDisplayUI display, CombatantStats stats)
        {
            var b = stats.Base;
            var a = stats.Actual;
            float[] baseValues = [b.Hp, b.Speed, b.Range, b.Damage, b.Cooldown, b.ProjectileSpeed];
            float[] actualValues = [a.Hp, a.Speed, a.Range, a.Damage, a.Cooldown, a.ProjectileSpeed];
            for (int i = 0; i < RowFields.Length; i++)
            {
                var text = Traverse.Create(display).Field(RowFields[i]).GetValue<TextMeshProUGUI>();
                if (text == null) continue;
                bool show = actualValues[i] != 0f || baseValues[i] != 0f;
                text.transform.parent.gameObject.SetActive(show);
                if (show) text.text = ValueText(baseValues[i], actualValues[i]);
            }
        }

        internal static string ValueText(float baseValue, float actual) =>
            CompendiumStats.SameShown(baseValue, actual)
                ? CompendiumStats.Format(actual)
                : $"<alpha=#99>{CompendiumStats.Format(baseValue)}<alpha=#FF>{Arrow}{CompendiumStats.Format(actual)}";

        /// <summary>What changed the numbers, and damage against other targets where it differs.</summary>
        internal static string DetailLines(CombatantStats stats, bool isEnemy)
        {
            var sb = new StringBuilder();
            foreach (var m in stats.Modifiers)
            {
                var what = m.Describe();
                if (what.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(m.Label).Append(": ").Append(what);
            }
            if (stats.SplashActual > 0f)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Splash damage: ").Append(ValueText(stats.SplashBase, stats.SplashActual));
            }
            // Other targets: only those that take a different amount than the main row shows, at most four.
            int shown = 0;
            var mainActual = stats.Actual.Damage;
            var seen = new HashSet<string>();
            foreach (var vs in stats.DamageByTarget)
            {
                if (isEnemy && vs.Target == "You") continue;
                if (CompendiumStats.SameShown(vs.Actual, mainActual) || !seen.Add(vs.Target)) continue;
                if (shown == 4) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("vs ").Append(vs.Target).Append(": ").Append(ValueText(vs.Base, vs.Actual));
                shown++;
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- dev commands

        private static Harmony hitLogHarmony;

        private static void LogHit(Hp __instance, float _amount, TaggedObject _damageComingFrom)
        {
            if (_amount <= 0f || __instance.HpValue <= 0f) return;
            Plugin.Logger.LogInfo($"[hit] {DescribeHitParty(_damageComingFrom?.gameObject)} -> {DescribeHitParty(__instance.gameObject)}: {_amount:0.###} (hp {__instance.HpValue:0.#}/{__instance.maxHp:0.#})");
        }

        private static string DescribeHitParty(GameObject go)
        {
            if (go == null) return "?";
            var marker = go.GetComponentInParent<ScreenMarkerIcon>();
            var hp = go.GetComponentInParent<Hp>();
            string elite = hp != null && hp.Elite ? "elite " : "";
            return elite + (marker != null ? marker.UnitName : go.name);
        }

        private static string DescribeAll(int waveIndex)
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null) throw new System.InvalidOperationException("not in a level");
            var sb = new StringBuilder();
            var smith = BlacksmithUpgrades.instance;
            sb.AppendLine($"wave {waveIndex + 1}/{spawner.WaveCount}, difficulty x{(waveIndex >= 0 && waveIndex < spawner.waves.Count ? spawner.waves[waveIndex].difficultyMulti : 1f)}; blacksmith melee {smith?.meleeDamage} ranged {smith?.rangedDamage} meleeRes {smith?.meleeResistance} rangedRes {smith?.rangedResistance}");
            sb.AppendLine("equipped: " + string.Join(", ", PerkManager.instance.CurrentlyEquipped.Select(CompendiumStats.EquippableName)));
            if (waveIndex >= 0 && waveIndex < spawner.waves.Count)
            {
                var seen = new HashSet<(GameObject, bool)>();
                foreach (var spawn in spawner.waves[waveIndex].spawns)
                {
                    if (spawn.enemyPrefab == null || !seen.Add((spawn.enemyPrefab, spawn.eliteEnemies))) continue;
                    var s = CompendiumStats.Enemy(spawn.enemyPrefab, spawn.eliteEnemies, waveIndex);
                    sb.AppendLine(Line("enemy", s));
                    var w = (spawn.enemyPrefab.GetComponent<AutoAttack>() ?? spawn.enemyPrefab.GetComponentInChildren<AutoAttack>(true))?.weapon;
                    if (w != null) sb.AppendLine($"    weapon {w.name} smith={w.blacksmithEffect} direct=[{Mods(w.directDamage)}] splash=[{Mods(w.splashDamage)}]");
                }
            }
            foreach (var group in CompendiumStats.LiveUnitGroups())
            {
                var s = CompendiumStats.Unit(group[0], waveIndex);
                sb.AppendLine(Line($"unit x{group.Count}", s));
                var aa = group[0].GetComponent<AutoAttack>();
                if (aa?.weapon != null) sb.AppendLine($"    weapon {aa.weapon.name} smith={aa.weapon.blacksmithEffect} dmgMulti={aa.DamageMultiplyer} direct=[{Mods(aa.weapon.directDamage)}]");
            }
            return sb.ToString().TrimEnd();
        }

        private static string Mods(List<DamageModifyer> mods) =>
            mods == null ? "" : string.Join("; ", mods.Select(m => $"{string.Join("+", m.requiredTags)}:+{m.damageAdded}x{m.damageMultiplyer}"));

        private static string Line(string kind, CombatantStats s)
        {
            string P(float b, float a) => CompendiumStats.SameShown(b, a) ? CompendiumStats.Format(a) : $"{CompendiumStats.Format(b)}->{CompendiumStats.Format(a)}";
            var line = $"{kind} {(s.Elite ? "elite " : "")}{s.Name} [{string.Join(",", s.Tags)}]: hp {P(s.Base.Hp, s.Actual.Hp)} spd {P(s.Base.Speed, s.Actual.Speed)} rng {P(s.Base.Range, s.Actual.Range)} dmg {P(s.Base.Damage, s.Actual.Damage)} cd {P(s.Base.Cooldown, s.Actual.Cooldown)} proj {P(s.Base.ProjectileSpeed, s.Actual.ProjectileSpeed)}";
            if (s.Modifiers.Count > 0) line += "\n    mods: " + string.Join("; ", s.Modifiers.Select(m => $"{m.Label} {m.Describe()}"));
            if (s.DamageByTarget.Count > 0) line += "\n    vs: " + string.Join("; ", s.DamageByTarget.Select(d => $"{d.Target} {P(d.Base, d.Actual)}"));
            return line;
        }

        /// <summary>Checks the computed actual values against live enemies of the current night.</summary>
        private static string VerifyLive()
        {
            var spawner = EnemySpawner.instance;
            var tm = TagManager.instance;
            if (spawner == null || tm == null) throw new System.InvalidOperationException("not in a level");
            int wave = spawner.Wavenumber;
            var sb = new StringBuilder();
            int ok = 0, bad = 0;
            var checkedKinds = new HashSet<(string, bool)>();
            foreach (var enemy in tm.EnemyUnits)
            {
                if (enemy == null) continue;
                var marker = enemy.GetComponent<ScreenMarkerIcon>();
                var hp = enemy.GetComponent<Hp>() ?? enemy.GetComponentInChildren<Hp>();
                if (marker == null || hp == null) continue;
                if (!checkedKinds.Add((marker.UnitName, hp.Elite))) continue;
                var prefab = CompendiumStats.FindWavePrefab(wave, marker.UnitName, null, hp.Elite);
                if (prefab == null) { sb.AppendLine($"? {marker.UnitName}: not in wave {wave + 1}"); continue; }
                var s = CompendiumStats.Enemy(prefab, hp.Elite, wave);
                var aa = enemy.GetComponent<AutoAttack>() ?? enemy.GetComponentInChildren<AutoAttack>();
                var move = enemy.GetComponent<PathfindMovementEnemy>() ?? enemy.GetComponentInChildren<PathfindMovementEnemy>();
                var prefabAa = prefab.GetComponent<AutoAttack>() ?? prefab.GetComponentInChildren<AutoAttack>(true);
                float expectedMulti = 1f;
                if (prefabAa != null && s.Base.Damage > 0f) expectedMulti = prefabAa.DamageMultiplyer * (s.Actual.Damage / s.Base.Damage) / CompendiumStats.BlacksmithMulti(prefabAa.weapon, out _);
                var diffs = new List<string>();
                void Check(string what, float expected, float live) { if (Mathf.Abs(expected - live) > 0.01f * Mathf.Max(1f, Mathf.Abs(live))) diffs.Add($"{what} computed {expected:0.###} live {live:0.###}"); }
                Check("maxHp", s.Actual.Hp, hp.maxHp);
                if (aa != null && s.Base.Damage > 0f) Check("damageMulti", expectedMulti, aa.DamageMultiplyer);
                if (aa != null) Check("cooldown", s.Actual.Cooldown, aa.cooldownDuration);
                if (move != null) Check("speed", s.Actual.Speed, move.movementSpeed);
                string name = (hp.Elite ? "elite " : "") + marker.UnitName;
                if (diffs.Count == 0) { ok++; sb.AppendLine($"ok {name}: hp {hp.maxHp:0.#}, dmg vs you {s.Actual.Damage:0.##}"); }
                else { bad++; sb.AppendLine($"MISMATCH {name}: " + string.Join("; ", diffs)); }
            }
            return $"wave {wave + 1}: {ok} ok, {bad} mismatched\n" + sb.ToString().TrimEnd();
        }

        private static string DescribeTooltip()
        {
            var helper = Object.FindObjectsOfType<PerkSelectionTooltipHelper>().FirstOrDefault(h => h.isActiveAndEnabled && h.selectionTitle != null && h.selectionTitle.gameObject.activeInHierarchy);
            if (helper == null) throw new System.InvalidOperationException("no tooltip open");
            var sb = new StringBuilder();
            sb.AppendLine("title: " + helper.selectionTitle.text);
            sb.AppendLine("description: " + helper.selectionDescription.text.Replace("\n", " | "));
            if (helper.UnitStatsDisplay != null && helper.UnitStatsDisplay.gameObject.activeInHierarchy)
                foreach (var field in RowFields)
                {
                    var text = Traverse.Create(helper.UnitStatsDisplay).Field(field).GetValue<TextMeshProUGUI>();
                    if (text != null && text.gameObject.activeInHierarchy) sb.AppendLine($"{field}: {text.text}");
                }
            return sb.ToString().TrimEnd();
        }
    }
}
