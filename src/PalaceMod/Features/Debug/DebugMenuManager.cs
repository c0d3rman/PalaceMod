using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Debug menu: a cheat menu for practice and theorycrafting. It's just a menu (no Enabled switch): pause > "Debug" or
    its key (` by default); the "Gameplay-changing features" master switch hides it.
    - In a level: Run (gold, nights, game speed), Wave (edit the next night), Build (free upgrades), Combat (god mode,
      healing), Loadout (swap weapon, perks and mutators).
    - On the level-select map: Progress (player level, XP, unlock everything, reset), Stages (locked/open/beaten),
      Unlocks (weapons, perks, mutators). These change the player's real progression (ProgressionActions), so the
      page suggests a scratch profile first.

    Steppers change their value with left/right and act on apply. The actions themselves live in DebugActions and
    are also dev commands (dbg.*), which double as test hooks. Every action marks the run via GameplayImpact.
*/
namespace PalaceMod
{
    public class DebugMenuManager : BaseManager
    {
        public override string FeatureSection => "Debug menu";
        public override bool AffectsRanking => true; // off while "Gameplay-changing features" is off
        public override string FeatureDescription =>
            "Cheat menu in the pause menu: in a level, gold, nights, waves, free buildings, god mode and loadout swaps (the run becomes unranked); on the map, your level, stages and unlocks.";
        protected override bool HasEnabledSwitch => false; // just a menu
        // The achievement guard must hold even while the master switch hides the menu; the damage patch checks cheats itself.
        protected override bool UnpatchWhenDisabled => false;

        private Keybind openKey;
        private ModFrame frame, mapFrame;
        private bool hintShown, mapHintShown;
        private string confirming; // the map's bulk action waiting for Yes/No, or null

        // Menu state.
        private static readonly int[] GoldAmounts = [1, 5, 10, 25, 50, 100, 250, 500, 1000];
        private int goldIndex = 5;
        private int jumpTarget = -1;
        private static readonly float[] EnemyScales = [0.5f, 1f, 1.5f, 2f, 3f];
        private HashSet<Equippable> pendingLoadout;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            openKey = Keybinds.Register(FeatureSection, "open", "Debug menu", new KeyboardShortcut(KeyCode.BackQuote),
                "Opens or closes the debug menu in a level or on the map.", KeybindFlags.AllowInMenus);
            PauseMenu.AddButton("debug-menu", "Debug", Open, order: 90, visible: () => IsEnabled && (DebugActions.InLevel || ProgressionActions.OnMap));
            AchievementGuard.Stages = plugin.Config.Bind(FeatureSection, "Achievement guard stages", "", new ConfigDescription(
                "Stages the debug menu marked beaten; they don't grant their Steam achievement until won for real.", null, new SettingTags { Hidden = true }));
            AchievementGuard.Level = plugin.Config.Bind(FeatureSection, "Achievement guard level", false, new ConfigDescription(
                "The debug menu raised the player level; the map doesn't grant the max-level achievement.", null, new SettingTags { Hidden = true }));
            GameplayImpact.Reset += OnImpactReset;
            SceneManager.sceneLoaded += (_, _) =>
            {
                DebugActions.ForgetWaves();
                pendingLoadout = null;
                jumpTarget = -1;
            };
            RegisterDevCommands();
        }

        public override void Update()
        {
            if (!openKey.Down) return;
            if (frame != null && frame.IsOpen) frame.Close();
            else if (mapFrame != null && mapFrame.IsOpen) mapFrame.Close();
            else if (UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame == null && (DebugActions.InLevel || ProgressionActions.OnMap)) Open();
        }

        protected override void OnFeatureDisabled()
        {
            if (frame != null && frame.IsOpen) frame.Close();
            if (mapFrame != null && mapFrame.IsOpen) mapFrame.Close();
            DebugActions.SetGodMode(false);
            DebugActions.SetInvulnerableKingdom(false);
            if (!DebugActions.IsVanillaSpeed(DebugActions.Speed))
            {
                PlayerMovement.gameplayTimeScale = 1f;
                if (Time.timeScale != 0f) Time.timeScale = 1f;
            }
            DebugActions.RestoreAllWaves();
            pendingLoadout = null;
        }

        // A fresh run clears the gameplay impact; cheats that are still switched on mark it again.
        private static void OnImpactReset()
        {
            DebugActions.OnImpactReset();
            if (!Feature<DebugMenuManager>.On) return;
            if (DebugActions.GodMode) DebugActions.Mark("god mode");
            if (DebugActions.InvulnerableKingdom) DebugActions.Mark("invulnerable kingdom");
            if (!DebugActions.IsVanillaSpeed(DebugActions.Speed)) DebugActions.Mark("game speed");
        }

        // ---- Menu -------------------------------------------------------------------------------------------------

        private void Open()
        {
            if (!IsEnabled) return;
            if (ProgressionActions.OnMap && ProgressionActions.Ready)
            {
                OpenMap();
                return;
            }
            if (!DebugActions.InLevel) return;
            frame ??= new ModFrame("Debug")
                .AddTab("Run", BuildRunTab)
                .AddTab("Wave", BuildWaveTab)
                .AddTab("Build", BuildBuildTab)
                .AddTab("Combat", BuildCombatTab)
                .AddTab("Loadout", BuildLoadoutTab);
            pendingLoadout = null;
            jumpTarget = -1;
            frame.Open();
            if (!hintShown && frame.IsOpen)
            {
                hintShown = true;
                Notify.Show("Debug menu: left/right change values. Using it makes the run unranked.", NotifyKind.Info, 5f, "debug-menu-hint");
            }
        }

        // Runs an action from the menu: reports the result or the reason it couldn't run, then refreshes the page.
        private void Run(Func<string> action, bool quiet = false)
        {
            try
            {
                string result = action();
                if (!quiet && !string.IsNullOrEmpty(result)) Notify.Show(Capitalize(result), NotifyKind.Success, 2.5f, "debug-menu");
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                Notify.Show(e.Message, NotifyKind.Warning, 3f, "debug-menu");
            }
            if (frame != null && frame.IsOpen) frame.Refresh();
            if (mapFrame != null && mapFrame.IsOpen) mapFrame.Refresh();
        }

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // A row whose value changes with left/right or the arrows beside it (step) and that acts on apply.
        private ModRow Stepper(ModPage page, string label, Func<string> value, Action<int> step, Action apply, string tooltip, Sprite icon = null) =>
            page.Stepper(label, value, step, apply, tooltip, icon);

        private static string OnOff(bool on) => on ? "On" : "Off";

        private void BuildRunTab(ModPage page)
        {
            if (!DebugActions.InLevel)
            {
                page.Text("Only available during a level.");
                return;
            }
            page.Header($"Gold  {DebugActions.Gold}");
            Stepper(page, "Add gold", () => $"+{GoldAmounts[goldIndex]}", d => goldIndex = Mathf.Clamp(goldIndex + d, 0, GoldAmounts.Length - 1),
                () => Run(() => DebugActions.AddGold(GoldAmounts[goldIndex])), "Gives you gold. Left/right change the amount.");
            Stepper(page, "Remove gold", () => $"-{GoldAmounts[goldIndex]}", d => goldIndex = Mathf.Clamp(goldIndex + d, 0, GoldAmounts.Length - 1),
                () => Run(() => DebugActions.AddGold(-GoldAmounts[goldIndex])), "Takes gold away. Left/right change the amount.");

            int night = DebugActions.CurrentNight, count = DebugActions.NightCount;
            page.Header(DebugActions.IsNight ? $"Night {night} of {count}" : $"Day {night} of {count}");
            if (DebugActions.IsNight)
            {
                page.Button("End night now", () => Run(DebugActions.EndNight), "Stops the wave and removes every enemy; dawn follows.");
            }
            else
            {
                page.Button("Start night now", () => Run(DebugActions.StartNight), "Starts the next night right away.");
                if (DebugActions.IsPlanningTime)
                {
                    if (jumpTarget < 1) jumpTarget = night;
                    Stepper(page, "Jump to night", () => $"{jumpTarget} of {count}", d => jumpTarget = Mathf.Clamp(jumpTarget + d, 1, count),
                        () => Run(() => DebugActions.JumpToNight(jumpTarget)),
                        "Makes the chosen night the next one. Buildings and gold stay as they are.");
                }
            }

            page.Header("Speed");
            Stepper(page, "Game speed", () => $"x{DebugActions.Speed:0.##}", d =>
            {
                var speeds = DebugActions.Speeds;
                int nearest = Array.IndexOf(speeds, speeds.OrderBy(s => Mathf.Abs(s - DebugActions.Speed)).First());
                Run(() => DebugActions.SetSpeed(speeds[Mathf.Clamp(nearest + d, 0, speeds.Length - 1)]), quiet: true);
            },() => Run(() => DebugActions.SetSpeed(1f)), "Left/right change the game speed; apply resets it to normal. Takes effect when the menu closes.");
        }

        private void BuildWaveTab(ModPage page)
        {
            var wave = DebugActions.UpcomingWave;
            if (wave == null)
            {
                page.Text(DebugActions.InLevel && !DebugActions.IsNight && DebugActions.CurrentNight > DebugActions.NightCount
                    ? "There are no nights left."
                    : "The next night's wave can be changed during the day.");
                return;
            }
            bool modified = DebugActions.IsModified(wave);
            page.Header($"Night {DebugActions.CurrentNight}{(modified ? "  (changed)" : "")}");
            Stepper(page, "Difficulty", () => $"x{wave.difficultyMulti:0.0}",
                d => Run(() => DebugActions.SetDifficulty(wave.difficultyMulti + d * 0.1f), quiet: true), null,
                "Night difficulty: scales enemy health, and enemy damage by half as much.");
            page.Row("All elite", OnOff(DebugActions.AllElite(wave)), "Makes every enemy of the night elite (much tougher).",
                () => Run(() => DebugActions.SetAllElite(!DebugActions.AllElite(wave)), quiet: true));
            Stepper(page, "Enemy count", () => $"x{DebugActions.EnemyScale(wave):0.##}", d =>
            {
                float scale = DebugActions.EnemyScale(wave);
                int nearest = Array.IndexOf(EnemyScales, EnemyScales.OrderBy(s => Mathf.Abs(s - scale)).First());
                Run(() => DebugActions.ScaleEnemies(EnemyScales[Mathf.Clamp(nearest + d, 0, EnemyScales.Length - 1)]), quiet: true);
            }, null, "Scales the number of every enemy type, relative to the original wave. The gold reward stays the same.");

            page.Header("Enemies");
            var groups = DebugActions.WaveGroups(wave);
            if (groups.Count == 0) page.Text("No enemies.");
            foreach (var group in groups)
            {
                var prefab = group.Prefab;
                Stepper(page, group.AnyElite ? $"{group.Name} (elite)" : group.Name,
                    () => DebugActions.WaveGroups(wave).FirstOrDefault(g => g.Prefab == prefab).Count.ToString(),
                    d => Run(() => DebugActions.ChangeEnemyCount(prefab, d), quiet: true), null,
                    "Left/right add or remove one of this enemy.", group.Icon);
            }
            if (modified)
            {
                page.Space(10f);
                page.Button("Reset wave", () => Run(DebugActions.ResetWave), "Puts the night back the way the level made it.");
            }
        }

        private void BuildBuildTab(ModPage page)
        {
            if (!DebugActions.IsPlanningTime)
            {
                page.Text("Buildings can be changed during the day.");
                return;
            }
            var slot = DebugActions.NearestBuilding();
            if (slot == null)
            {
                page.Header("Nearby");
                page.Text("Walk up to a building or blueprint to upgrade it.");
            }
            else
            {
                page.Header(DebugActions.BuildingName(slot));
                page.Row("Level", $"{slot.Level} of {slot.Upgrades.Count}", "The building next to the king.");
                page.Button(slot.Level == 0 ? "Build (free)" : "Upgrade (free)", () => Run(() => DebugActions.Upgrade(slot)),
                    "Builds or upgrades it one level for free. Choice upgrades ask which branch.");
                page.Button("Max out (free)", () => Run(() => DebugActions.MaxUpgrade(slot)),
                    "Upgrades it to the last level for free, asking for each choice.");
            }
            page.Header("Everywhere");
            page.Button("Build all blueprints", () => Run(DebugActions.BuildAllBlueprints),
                "Builds every blueprint on the map at level 1 for free, including ones that unlock along the way.");
        }

        private void BuildCombatTab(ModPage page)
        {
            if (!DebugActions.InLevel)
            {
                page.Text("Only available during a level.");
                return;
            }
            page.Header("Protection");
            page.Row("God mode", OnOff(DebugActions.GodMode), "The king takes no damage.",
                () => Run(() => DebugActions.SetGodMode(!DebugActions.GodMode), quiet: true));
            page.Row("Invulnerable kingdom", OnOff(DebugActions.InvulnerableKingdom), "Your units and buildings take no damage.",
                () => Run(() => DebugActions.SetInvulnerableKingdom(!DebugActions.InvulnerableKingdom), quiet: true));
            page.Header("Actions");
            page.Button("Heal everything", () => Run(DebugActions.HealAll), "Revives and fully heals the king, your units and your buildings.");
            page.Button("Revive king", () => Run(DebugActions.ReviveKing), "Brings the king back right away at full health.");
            page.Button("Kill all enemies", () => Run(DebugActions.KillEnemies), "Kills every enemy on the map. They drop their gold.");
        }

        private void BuildLoadoutTab(ModPage page)
        {
            if (!DebugActions.LoadoutAvailable)
            {
                page.Text(DebugActions.InLevel ? "Loadout changes aren't available in Eternal Trials." : "Only available during a level.");
                return;
            }
            pendingLoadout ??= [.. PerkManager.instance.CurrentlyEquipped.Where(e => DebugActions.CategoryOf(e) != null)];
            var current = new HashSet<Equippable>(PerkManager.instance.CurrentlyEquipped.Where(e => DebugActions.CategoryOf(e) != null));
            int changes = pendingLoadout.Count(e => !current.Contains(e)) + current.Count(e => !pendingLoadout.Contains(e));
            string how = DebugActions.LoadoutKeepsMoment
                ? "Equips the loadout below and reloads this exact moment with it, so perks apply like at level start. " +
                  "Buildings, units, gold and positions stay; health keeps its share of any new maximum. Enemies already out keep their stats; units following you are left where they stand."
                : DebugActions.LoadoutKeepsProgress
                    ? "Equips the loadout below and reloads today from dawn (savestates don't work here), so everything since dawn is lost."
                    : "Equips the loadout below and restarts the level (savestates don't work here and there's no dawn save yet).";

            page.Header("Apply");
            page.Row("Apply loadout", changes == 0 ? "no changes" : $"{changes} change{(changes == 1 ? "" : "s")}",
                how,
                () => Run(() => changes == 0 ? throw new InvalidOperationException("Nothing changed") : DebugActions.ApplyLoadout(pendingLoadout)));
            // Always shown, so toggling items never shifts the rows below it.
            page.Button("Undo changes", () => Run(() =>
            {
                if (changes == 0) throw new InvalidOperationException("Nothing changed");
                pendingLoadout = null;
                return "";
            }), "Goes back to the loadout you're playing with.");

            foreach (var category in new[] { DebugActions.Category.Weapon, DebugActions.Category.Perk, DebugActions.Category.Mutator })
            {
                int equipped = pendingLoadout.Count(e => DebugActions.CategoryOf(e) == category);
                page.Header(category switch
                {
                    DebugActions.Category.Weapon => "Weapon",
                    DebugActions.Category.Perk => $"Perks  {equipped}",
                    _ => $"Mutators  {equipped}",
                });
                foreach (var e in DebugActions.AllOf(category))
                {
                    var item = e;
                    bool on = pendingLoadout.Contains(item);
                    page.Row(DebugActions.DisplayName(item), on ? (current.Contains(item) ? "Equipped" : "Adding") : (current.Contains(item) ? "Removing" : ""),
                        () => DebugActions.Description(item), () => Run(() =>
                        {
                            if (category == DebugActions.Category.Weapon)
                            {
                                pendingLoadout.RemoveWhere(x => x is EquippableWeapon);
                                pendingLoadout.Add(item);
                            }
                            else if (!pendingLoadout.Remove(item)) pendingLoadout.Add(item);
                            return "";
                        }), item.icon);
                }
            }
        }

        // ---- Map: progression ------------------------------------------------------------------------------------

        private const string ProfileNote = "These change your real progression and are saved at once. To keep yours, switch to a scratch profile first (title screen > Profiles).";

        private void OpenMap()
        {
            mapFrame ??= new ModFrame("Debug")
                .AddTab("Progress", BuildProgressTab)
                .AddTab("Stages", BuildStagesTab)
                .AddTab("Unlocks", BuildUnlocksTab);
            confirming = null;
            mapFrame.Open();
            if (!mapHintShown && mapFrame.IsOpen)
            {
                mapHintShown = true;
                Notify.Show("The map shows changes after you re-enter it (Progress > Reload map).", NotifyKind.Info, 5f, "debug-menu-hint");
            }
        }

        private static string LevelText()
        {
            int level = ProgressionActions.Level, max = ProgressionActions.MaxRewardLevel;
            return level >= max ? $"{level}  (all rewards)" : $"{level} of {max}";
        }

        private void BuildProgressTab(ModPage page)
        {
            page.Text(ProfileNote);
            page.Header("Level");
            Stepper(page, "Player level", LevelText, d => Run(() => ProgressionActions.SetLevel(ProgressionActions.Level + d), quiet: true), null,
                "Left/right change your level by one (XP goes to 0). Each level up to the last reward unlocks the next weapon, perk or mutator.");
            Stepper(page, "XP", () => $"{ProgressionActions.Xp} / {ProgressionActions.XpNeeded}",
                d => Run(() => ProgressionActions.SetXp(ProgressionActions.Xp + d * Mathf.Max(1, ProgressionActions.XpNeeded / 10)), quiet: true), null,
                "XP towards the next level. Left/right change it by a tenth of a level.");

            page.Header("Everything");
            if (confirming == null)
            {
                page.Button("Unlock everything", () => Confirm("unlock"),
                    "Every stage beaten and your level raised to the last reward, so every weapon, perk and mutator is unlocked. Asks first.");
                page.Button("Reset progression", () => Confirm("reset"),
                    "Back to level 1 with no stage beaten. Scores and runs in progress are kept; Profiles > Reset progress is the full wipe. Asks first.");
            }
            else
            {
                string question = confirming == "unlock" ? "Unlock every stage, weapon, perk and mutator?" : "Reset to level 1 with no stage beaten?";
                page.Text(question);
                page.Button(confirming == "unlock" ? "Yes, unlock everything" : "Yes, reset progression", () =>
                {
                    string action = confirming;
                    confirming = null;
                    Run(action == "unlock" ? ProgressionActions.UnlockEverything : ProgressionActions.ResetProgression);
                }, question);
                page.Button("No", () => Confirm(null), question);
            }

            page.Header("Map");
            page.Button("Reload map", () =>
            {
                mapFrame?.Close();
                Run(ProgressionActions.ReloadMap, quiet: true);
            }, "Re-enters the map so it shows what you changed here.");
        }

        private void Confirm(string action)
        {
            confirming = action;
            mapFrame?.Refresh();
        }

        private static string StageText(ProgressionActions.StageState state) => state switch
        {
            ProgressionActions.StageState.Beaten => "Beaten",
            ProgressionActions.StageState.Open => "Open",
            _ => "Locked",
        };

        private void BuildStagesTab(ModPage page)
        {
            page.Text("Left/right: locked, open (playable) or beaten. Beating a stage opens the next ones and the items it unlocks.");
            foreach (var s in ProgressionActions.Stages)
            {
                var stage = s;
                Stepper(page, ProgressionActions.StageName(stage), () => StageText(ProgressionActions.StateOf(stage)), d =>
                {
                    int next = Mathf.Clamp((int)ProgressionActions.StateOf(stage) + d, 0, 2);
                    if (next != (int)ProgressionActions.StateOf(stage))
                        Run(() => ProgressionActions.SetStage(stage, (ProgressionActions.StageState)next), quiet: true);
                }, null, stage.unlockRequirement == null
                    ? "Always open. Left/right mark it beaten or not."
                    : $"Opens when {ProgressionActions.StageName(stage.unlockRequirement)} is beaten. Locking it un-beats that stage too.");
            }
        }

        private void BuildUnlocksTab(ModPage page)
        {
            page.Text("Apply toggles an item. Items unlock by level or by beating a stage, so toggling one changes that.");
            foreach (var category in new[] { DebugActions.Category.Weapon, DebugActions.Category.Perk, DebugActions.Category.Mutator })
            {
                var items = DebugActions.AllOf(category).ToList();
                string title = category switch { DebugActions.Category.Weapon => "Weapons", DebugActions.Category.Perk => "Perks", _ => "Mutators" };
                page.Header($"{title}  {items.Count(e => e.IsUnlocked)}/{items.Count}");
                foreach (var e in items)
                {
                    var item = e;
                    bool always = ProgressionActions.AlwaysUnlocked(item);
                    int reward = ProgressionActions.RewardLevel(item);
                    string value = always ? "Always" : item.IsUnlocked ? "Unlocked" : reward > 0 ? $"Locked (level {reward + 1})" : "Locked";
                    page.Row(DebugActions.DisplayName(item), value, () => ProgressionActions.HowUnlocked(item),
                        always ? null : () => Run(() => ProgressionActions.SetUnlocked(item, !item.IsUnlocked), quiet: true), item.icon);
                }
            }
        }

        // ---- Achievements ---------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.LevelBeaten))]
        private class AchievementManager_LevelBeaten_Patch
        {
            private static bool Prefix(string _scene) => AchievementGuard.AllowLevelBeaten(_scene);
        }

        [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.UnlockAchievement))]
        private class AchievementManager_UnlockAchievement_Patch
        {
            private static bool Prefix(AchievementManager.Achievements _achievement) =>
                _achievement != AchievementManager.Achievements.MAXLEVEL_REACHED || AchievementGuard.AllowMaxLevel();
        }

        // ---- Damage block -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
        private class Hp_TakeDamage_Patch
        {
            private static bool Prefix(Hp __instance, ref bool __result)
            {
                if (!DebugActions.BlocksDamage(__instance)) return true;
                __result = false;
                return false;
            }
        }

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private static float ParseFloat(string s) => float.Parse(s.TrimStart('x', 'X'), CultureInfo.InvariantCulture);

        private static bool ParseOnOff(string[] args, bool current) => args.Length == 0 ? !current : args[0].ToLowerInvariant() switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => throw new ArgumentException("expected on or off"),
        };

        private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static Equippable FindEquippable(string name)
        {
            string key = Squash(name);
            return PerkManager.instance.allEquippables.FirstOrDefault(e => e != null && DebugActions.CategoryOf(e) != null &&
                       (Squash(e.name) == key || Squash(e.displayName) == key || Squash(DebugActions.DisplayName(e)) == key))
                   ?? throw new ArgumentException($"no weapon, perk or mutator \"{name}\"");
        }

        private static string KingHpText()
        {
            var hp = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<Hp>() : null;
            return hp == null ? "-" : $"{hp.HpValue:0}/{hp.maxHp:0}";
        }

        private string DescribeWave()
        {
            var wave = DebugActions.UpcomingWave ?? throw new InvalidOperationException("no upcoming wave (day only)");
            return $"night {DebugActions.CurrentNight}: difficulty x{wave.difficultyMulti:0.0}, all elite {DebugActions.AllElite(wave)}, " +
                   $"modified {DebugActions.IsModified(wave)}; " +
                   string.Join(", ", DebugActions.WaveGroups(wave).Select(g => $"{g.Count} {g.Prefab.name}{(g.AnyElite ? " (elite)" : "")}"));
        }

        private void RegisterDevCommands()
        {
            DevCommands.Register("dbg.menu", "dbg.menu [close]       open (or close) the debug menu", args =>
            {
                if (args.FirstOrDefault() == "close") { frame?.Close(); mapFrame?.Close(); }
                else Open();
                return frame != null && frame.IsOpen ? "open (level)" : mapFrame != null && mapFrame.IsOpen ? "open (map)"
                    : IsEnabled ? "closed" : "closed (gameplay-changing features are off)";
            });
            // Dev launches load levels without the level select, which is what points the match save at the level;
            // without it nothing is saved and "reload today" can't be tested.
            DevCommands.Register("dbg.play", "dbg.play <level> [continue]   load a level like the level select does (fresh run unless continue)", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: dbg.play <level> [continue]");
                MatchSaveLoadHandler.TryLoadRun(args[0]);
                MatchSaveLoadHandler.OverwriteCurrentSave = !(args.Length > 1 && args[1] == "continue");
                if (MatchSaveLoadHandler.OverwriteCurrentSave && !PerkManager.instance.CurrentlyEquipped.Any(e => e is EquippableWeapon))
                {
                    var weapon = DebugActions.AllOf(DebugActions.Category.Weapon).FirstOrDefault();
                    if (weapon != null) PerkManager.SetEquipped(weapon, true);
                }
                SceneTransitionManager.instance.TransitionFromNullToLevel(args[0]);
                return MatchSaveLoadHandler.OverwriteCurrentSave ? "fresh run" : "continuing";
            });
            DevCommands.Register("dbg.prog",
                "dbg.prog [level <n>|xp <n>|unlock|lock <item>|stage <name> locked|open|beaten|all|reset|reload|guard]   show or change progression (real save!)", args =>
            {
                if (args.Length == 0) return ProgressionActions.Describe();
                string rest = string.Join(" ", args.Skip(1));
                switch (args[0])
                {
                    case "level": return ProgressionActions.SetLevel(int.Parse(args[1], CultureInfo.InvariantCulture));
                    case "xp": return ProgressionActions.SetXp(int.Parse(args[1], CultureInfo.InvariantCulture));
                    case "unlock": return ProgressionActions.SetUnlocked(FindEquippable(rest), true);
                    case "lock": return ProgressionActions.SetUnlocked(FindEquippable(rest), false);
                    case "stage":
                    {
                        if (args.Length < 3 || !Enum.TryParse(args[args.Length - 1], true, out ProgressionActions.StageState state))
                            throw new ArgumentException("usage: dbg.prog stage <name> locked|open|beaten");
                        return ProgressionActions.SetStage(ProgressionActions.FindStage(string.Join(" ", args.Skip(1).Take(args.Length - 2))), state);
                    }
                    case "all": return ProgressionActions.UnlockEverything();
                    case "reset": return ProgressionActions.ResetProgression();
                    case "reload": return ProgressionActions.ReloadMap();
                    case "guard": return AchievementGuard.Describe();
                    default: throw new ArgumentException("unknown progression command");
                }
            });
            DevCommands.Register("dbg.state", "dbg.state              gold, night, speed and cheats", _ =>
            {
                if (!DebugActions.InLevel) throw new InvalidOperationException("not in a level");
                return $"gold {DebugActions.Gold}, {(DebugActions.IsNight ? "night" : "day")} {DebugActions.CurrentNight}/{DebugActions.NightCount}, " +
                       $"speed x{DebugActions.Speed:0.##}, god {DebugActions.GodMode}, invulnerable {DebugActions.InvulnerableKingdom}, " +
                       $"king hp {KingHpText()}, nearest {DebugActions.BuildingName(DebugActions.NearestBuilding())}, loadout " +
                       string.Join(", ", PerkManager.instance.CurrentlyEquipped.Select(e => e.name));
            });
            DevCommands.Register("dbg.hurt", "dbg.hurt <n>           deal n damage to the king (tests god mode)", args =>
            {
                var hp = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<Hp>() : null;
                if (hp == null) throw new InvalidOperationException("not in a level");
                hp.TakeDamage(float.Parse(args.FirstOrDefault() ?? "10", CultureInfo.InvariantCulture));
                return $"king hp {KingHpText()}";
            });
            DevCommands.Register("dbg.gold", "dbg.gold <n|+n|-n>     set gold, or add/remove with a sign", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: dbg.gold <n|+n|-n>");
                int n = int.Parse(args[0], CultureInfo.InvariantCulture);
                return args[0].StartsWith("+") || args[0].StartsWith("-") ? DebugActions.AddGold(n) : DebugActions.SetGold(n);
            });
            DevCommands.Register("dbg.night", "dbg.night <start|end|n>   start or end the night, or make night n the next one", args =>
            {
                string arg = args.FirstOrDefault() ?? throw new ArgumentException("usage: dbg.night <start|end|n>");
                return arg switch
                {
                    "start" => DebugActions.StartNight(),
                    "end" => DebugActions.EndNight(),
                    _ => DebugActions.JumpToNight(int.Parse(arg, CultureInfo.InvariantCulture)),
                };
            });
            DevCommands.Register("dbg.wave", "dbg.wave [difficulty <x>|elite on|off|scale <x>|add|remove <enemy> [n]|reset]   show or edit the next wave", args =>
            {
                if (args.Length == 0) return DescribeWave();
                switch (args[0])
                {
                    case "difficulty": DebugActions.SetDifficulty(ParseFloat(args[1])); break;
                    case "elite": DebugActions.SetAllElite(ParseOnOff(args.Skip(1).ToArray(), DebugActions.AllElite(DebugActions.UpcomingWave))); break;
                    case "scale": DebugActions.ScaleEnemies(ParseFloat(args[1])); break;
                    case "reset": DebugActions.ResetWave(); break;
                    case "add":
                    case "remove":
                    {
                        if (args.Length < 2) throw new ArgumentException("usage: dbg.wave add|remove <enemy> [n]");
                        var wave = DebugActions.UpcomingWave ?? throw new InvalidOperationException("no upcoming wave (day only)");
                        string key = Squash(args[1]);
                        var group = DebugActions.WaveGroups(wave).FirstOrDefault(g => Squash(g.Prefab.name).Contains(key) || Squash(g.Name).Contains(key));
                        if (group.Prefab == null) throw new ArgumentException($"no \"{args[1]}\" in the next wave");
                        int n = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1;
                        DebugActions.ChangeEnemyCount(group.Prefab, args[0] == "add" ? n : -n);
                        break;
                    }
                    default: throw new ArgumentException("unknown wave edit");
                }
                return DescribeWave();
            });
            DevCommands.Register("dbg.upgrade", "dbg.upgrade [nearest|building] [branch]   free build/upgrade one level (branch index, else the choice popup)", args =>
            {
                if (!DebugActions.InLevel) throw new InvalidOperationException("not in a level");
                int? branch = args.Length > 1 && int.TryParse(args[args.Length - 1], out int b) ? b : null;
                string name = string.Join(" ", branch.HasValue ? args.Take(args.Length - 1) : args);
                var slot = DebugActions.FindBuilding(name) ?? throw new ArgumentException("no such building");
                return DebugActions.Upgrade(slot, branch);
            });
            DevCommands.Register("dbg.max", "dbg.max [nearest|building]   free upgrade to the last level", args =>
            {
                if (!DebugActions.InLevel) throw new InvalidOperationException("not in a level");
                return DebugActions.MaxUpgrade(DebugActions.FindBuilding(string.Join(" ", args)) ?? throw new ArgumentException("no such building"));
            });
            DevCommands.Register("dbg.buildall", "dbg.buildall           build every blueprint at level 1", _ => DebugActions.BuildAllBlueprints());
            DevCommands.Register("dbg.heal", "dbg.heal               revive and heal the king, units and buildings", _ => DebugActions.HealAll());
            DevCommands.Register("dbg.revive", "dbg.revive             revive the king", _ => DebugActions.ReviveKing());
            DevCommands.Register("dbg.kill", "dbg.kill               kill every enemy", _ => DebugActions.KillEnemies());
            DevCommands.Register("dbg.god", "dbg.god [on|off]       king takes no damage", args => DebugActions.SetGodMode(ParseOnOff(args, DebugActions.GodMode)));
            DevCommands.Register("dbg.invuln", "dbg.invuln [on|off]    units and buildings take no damage",
                args => DebugActions.SetInvulnerableKingdom(ParseOnOff(args, DebugActions.InvulnerableKingdom)));
            DevCommands.Register("dbg.speed", "dbg.speed <x>          game speed", args => DebugActions.SetSpeed(ParseFloat(args.FirstOrDefault() ?? "1")));
            DevCommands.Register("dbg.loadout", "dbg.loadout [+name|-name ...]   list, or change and apply the loadout (a weapon replaces the current one)", args =>
            {
                if (!DebugActions.InLevel) throw new InvalidOperationException("not in a level");
                if (args.Length == 0)
                {
                    return $"equipped: {string.Join(", ", PerkManager.instance.CurrentlyEquipped.Select(e => e.name))}; all: " +
                           string.Join(", ", PerkManager.instance.allEquippables.Where(e => e != null && DebugActions.CategoryOf(e) != null)
                               .Select(e => $"{e.name} ({DebugActions.CategoryOf(e)})"));
                }
                var loadout = new HashSet<Equippable>(PerkManager.instance.CurrentlyEquipped.Where(e => DebugActions.CategoryOf(e) != null));
                foreach (var arg in args)
                {
                    bool remove = arg.StartsWith("-");
                    var e = FindEquippable(arg.TrimStart('+', '-'));
                    if (remove) loadout.Remove(e);
                    else
                    {
                        if (e is EquippableWeapon) loadout.RemoveWhere(x => x is EquippableWeapon);
                        loadout.Add(e);
                    }
                }
                return DebugActions.ApplyLoadout(loadout);
            });
        }
    }
}
