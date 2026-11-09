using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    The debug menu's actions on game state, shared by the menu (DebugMenuManager) and its dev commands. Every action
    marks the run with GameplayImpact ("Debug menu: <action>") and throws InvalidOperationException with a short,
    player-facing reason when it can't run right now.

    Each action goes through the game's own code paths where one exists: AddCoin, SwitchToNight, Hp.Revive,
    BuildSlot.TryToBuildOrUpgradeAndPay (with the native choice popup), and for loadout changes the savestate
    reload (the game's "retry day" restart fed a capture of this moment), so perks and weapons are applied exactly as
    at level start.
*/
namespace PalaceMod
{
    internal static class DebugActions
    {
        internal static void Mark(string action) => GameplayImpact.Mark($"Debug menu: {action}");

        // ---- State ------------------------------------------------------------------------------------------------

        public static bool InLevel =>
            DayNightCycle.Instance != null && EnemySpawner.instance != null && PlayerInteraction.instance != null &&
            TagManager.instance != null && LocalGamestate.Instance != null &&
            LocalGamestate.Instance.CurrentState == LocalGamestate.State.InMatch;

        private static void RequireLevel()
        {
            if (!InLevel) throw new InvalidOperationException("Only available during a level");
        }

        public static bool IsNight => DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        /// <summary>Day after sunrise, no wave spawning: the window in which the next night can be planned.</summary>
        public static bool IsPlanningTime =>
            InLevel && !IsNight && DayNightCycle.Instance.AfterSunrise && !EnemySpawner.instance.SpawningInProgress;

        private static void RequireDay()
        {
            RequireLevel();
            if (!IsPlanningTime) throw new InvalidOperationException("Only during the day");
        }

        // ---- Gold -------------------------------------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<PlayerInteraction, int> BalanceRef =
            AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");

        public static int Gold => PlayerInteraction.instance != null ? PlayerInteraction.instance.Balance : 0;

        public static string AddGold(int amount)
        {
            RequireLevel();
            var player = PlayerInteraction.instance;
            if (amount > 0) player.AddCoin(amount);
            else if (amount < 0) BalanceRef(player) = Math.Max(0, player.Balance + amount);
            Mark(amount >= 0 ? "add gold" : "remove gold");
            return $"gold {player.Balance}";
        }

        public static string SetGold(int value) => AddGold(Math.Max(0, value) - Gold);

        // ---- Nights -----------------------------------------------------------------------------------------------

        /// <summary>The night in progress, or the next one during the day (1-based).</summary>
        public static int CurrentNight => EnemySpawner.instance.Wavenumber + (IsNight ? 1 : 2);

        public static int NightCount => EnemySpawner.instance.WaveCount;

        public static string StartNight()
        {
            RequireLevel();
            if (IsNight || !DayNightCycle.Instance.AfterSunrise) throw new InvalidOperationException("It's already night");
            DayNightCycle.Instance.SwitchToNight();
            Mark("start night");
            return $"night {CurrentNight} started";
        }

        /// <summary>Stops the wave and removes every enemy, so dawn comes right away.</summary>
        public static string EndNight()
        {
            RequireLevel();
            if (!IsNight) throw new InvalidOperationException("It's not night");
            EnemySpawner.instance.StopSpawnAfterWaveAndReset();
            int killed = KillAllEnemies();
            // Some enemies spawn more on death; keep clearing until the night is over.
            Plugin.Instance.StartCoroutine(KeepClearingUntilDawn());
            Mark("end night");
            return $"ended night, removed {killed} enemies";
        }

        private static IEnumerator KeepClearingUntilDawn()
        {
            float until = Time.unscaledTime + 5f;
            while (Time.unscaledTime < until && InLevel && IsNight)
            {
                if (EnemySpawner.instance.SpawningInProgress) EnemySpawner.instance.StopSpawnAfterWaveAndReset();
                KillAllEnemies();
                yield return null;
            }
        }

        private static readonly AccessTools.FieldRef<EnemySpawner, int> WavenumberRef =
            AccessTools.FieldRefAccess<EnemySpawner, int>("wavenumber");

        /// <summary>Makes <paramref name="night"/> (1-based) the next night. Day only.</summary>
        public static string JumpToNight(int night)
        {
            RequireDay();
            night = Mathf.Clamp(night, 1, NightCount);
            var spawner = EnemySpawner.instance;
            WavenumberRef(spawner) = night - 2;
            spawner.Refresh();
            Mark("jump to night");
            return $"next night is {night}/{NightCount}";
        }

        // ---- Upcoming wave ----------------------------------------------------------------------------------------

        private sealed class WaveSnapshot
        {
            public List<Spawn> Spawns;
            public Dictionary<Spawn, (int Count, bool Elite, int Gold)> Values;
            public float Difficulty;
            public float Scale = 1f;
        }

        private static readonly Dictionary<Wave, WaveSnapshot> snapshots = [];

        /// <summary>The next night's wave during the day; null at night or after the last night.</summary>
        public static Wave UpcomingWave
        {
            get
            {
                if (!IsPlanningTime) return null;
                int next = EnemySpawner.instance.Wavenumber + 1;
                return next >= 0 && next < EnemySpawner.instance.waves.Count ? EnemySpawner.instance.waves[next] : null;
            }
        }

        private static Wave RequireUpcomingWave()
        {
            RequireDay();
            return UpcomingWave ?? throw new InvalidOperationException("No night left to change");
        }

        /// <summary>The enemy-count factor last applied to <paramref name="wave"/> (1 if none).</summary>
        public static float EnemyScale(Wave wave) => wave != null && snapshots.TryGetValue(wave, out var snap) ? snap.Scale : 1f;

        public static bool IsModified(Wave wave) => wave != null && snapshots.ContainsKey(wave);

        private static void Snapshot(Wave wave)
        {
            if (snapshots.ContainsKey(wave)) return;
            snapshots[wave] = new WaveSnapshot
            {
                Spawns = [.. wave.spawns],
                Values = wave.spawns.Where(s => s != null).ToDictionary(s => s, s => (s.count, s.eliteEnemies, s.goldCoins)),
                Difficulty = wave.difficultyMulti,
            };
        }

        private static void AfterWaveChange()
        {
            EnemySpawner.instance.Refresh();
            Mark("modify wave");
        }

        // Spawns the menu can edit: enemies instantiated from a prefab (scene-placed bosses are activated, not counted).
        private static bool Editable(Spawn s) => s?.enemyPrefab != null && !s.enemyPrefab.scene.isLoaded;

        /// <summary>The upcoming wave's enemy types, with their total count, in wave order.</summary>
        public static List<(GameObject Prefab, string Name, Sprite Icon, int Count, bool AnyElite)> WaveGroups(Wave wave) =>
            wave.spawns.Where(Editable).GroupBy(s => s.enemyPrefab).Select(g =>
            {
                var icon = g.Key.GetComponent<ScreenMarkerIcon>();
                string name = icon != null ? icon.UnitName : g.Key.name;
                if (string.IsNullOrEmpty(name)) name = g.Key.name;
                return (g.Key, name, icon != null ? icon.sprite : null, g.Sum(s => s.count), g.Any(s => s.eliteEnemies));
            }).ToList();

        public static string SetDifficulty(float multiplier)
        {
            var wave = RequireUpcomingWave();
            Snapshot(wave);
            wave.difficultyMulti = Mathf.Clamp(Mathf.Round(multiplier * 10f) / 10f, 0.5f, 5f);
            AfterWaveChange();
            return $"difficulty x{wave.difficultyMulti:0.0}";
        }

        public static bool AllElite(Wave wave) => wave != null && wave.spawns.Where(Editable).All(s => s.eliteEnemies) && wave.spawns.Any(Editable);

        /// <summary>Makes every enemy of the upcoming wave elite, or puts the original elites back.</summary>
        public static string SetAllElite(bool elite)
        {
            var wave = RequireUpcomingWave();
            Snapshot(wave);
            var original = snapshots[wave].Values;
            foreach (var s in wave.spawns.Where(Editable))
            {
                s.eliteEnemies = elite || (original.TryGetValue(s, out var v) && v.Elite);
            }
            AfterWaveChange();
            return elite ? "all elite" : "original elites";
        }

        /// <summary>Adds or removes enemies of one type in the upcoming wave. The wave's gold reward is unchanged.</summary>
        public static string ChangeEnemyCount(GameObject prefab, int delta)
        {
            var wave = RequireUpcomingWave();
            var group = wave.spawns.Where(s => Editable(s) && s.enemyPrefab == prefab).ToList();
            if (group.Count == 0) throw new InvalidOperationException("That enemy isn't in the next wave");
            Snapshot(wave);
            for (; delta > 0; delta--) group[0].count++;
            for (; delta < 0; delta++)
            {
                var spawn = group.LastOrDefault(s => s.count > 1);
                if (spawn != null)
                {
                    spawn.count--;
                    continue;
                }
                // Down to one per spawn: drop a spawn (a spawn with count 0 would still spawn one).
                spawn = group.LastOrDefault();
                // Keep at least one enemy, which also keeps somewhere to put the night's gold.
                if (spawn == null || wave.spawns.Count(Editable) <= 1) break;
                wave.spawns.Remove(spawn);
                group.Remove(spawn);
                // Keep the night's gold reward: hand the removed spawn's coins to one that stays.
                var heir = wave.spawns.FirstOrDefault(Editable) ?? wave.spawns.FirstOrDefault(s => s != null);
                if (heir != null) heir.goldCoins += spawn.goldCoins;
            }
            AfterWaveChange();
            return $"{group.Sum(s => s.count)} x {prefab.name}";
        }

        /// <summary>Scales every editable enemy count in the upcoming wave, relative to the original wave.</summary>
        public static string ScaleEnemies(float factor)
        {
            var wave = RequireUpcomingWave();
            Snapshot(wave);
            var original = snapshots[wave].Values;
            foreach (var s in wave.spawns.Where(Editable))
            {
                int baseCount = original.TryGetValue(s, out var v) ? v.Count : s.count;
                s.count = Math.Max(1, Mathf.RoundToInt(baseCount * factor));
            }
            snapshots[wave].Scale = factor;
            AfterWaveChange();
            return $"enemies x{factor:0.##}";
        }

        public static string ResetWave()
        {
            var wave = RequireUpcomingWave();
            if (!Restore(wave)) return "wave unchanged";
            EnemySpawner.instance.Refresh();
            return "wave reset";
        }

        private static bool Restore(Wave wave)
        {
            if (!snapshots.TryGetValue(wave, out var snap)) return false;
            wave.spawns.Clear();
            wave.spawns.AddRange(snap.Spawns);
            foreach (var pair in snap.Values)
            {
                pair.Key.count = pair.Value.Count;
                pair.Key.eliteEnemies = pair.Value.Elite;
                pair.Key.goldCoins = pair.Value.Gold;
            }
            wave.difficultyMulti = snap.Difficulty;
            snapshots.Remove(wave);
            return true;
        }

        /// <summary>Puts every changed wave back (feature turned off).</summary>
        public static void RestoreAllWaves()
        {
            bool any = false;
            var waves = EnemySpawner.instance != null ? EnemySpawner.instance.waves : null;
            // Wavenumber is the night in progress, or the last one played during the day.
            int firstUnplayed = EnemySpawner.instance == null ? int.MaxValue : EnemySpawner.instance.Wavenumber + 1;
            foreach (var wave in snapshots.Keys.ToList())
            {
                // A wave that has started keeps its edits: restored spawns would miss the wave's Reset.
                int index = waves?.IndexOf(wave) ?? -1;
                if (index < 0 || index < firstUnplayed) snapshots.Remove(wave);
                else any |= Restore(wave);
            }
            if (any && IsPlanningTime) EnemySpawner.instance.Refresh();
        }

        /// <summary>Waves belong to the level scene; forget them when it unloads.</summary>
        public static void ForgetWaves() => snapshots.Clear();

        // ---- Buildings --------------------------------------------------------------------------------------------

        private const float NearbyRadius = 25f;

        /// <summary>The building the king is at: the focused one, else the nearest within reach.</summary>
        public static BuildSlot NearestBuilding()
        {
            if (!InLevel) return null;
            var player = PlayerInteraction.instance;
            if (player.FocussedInteractor is BuildingInteractor focused && focused.targetBuilding != null) return Root(focused.targetBuilding);
            Vector3 king = player.transform.position;
            var nearest = Object.FindObjectsOfType<BuildSlot>()
                .Where(s => s.buildingInteractor != null && s.buildingInteractor.isActiveAndEnabled)
                .OrderBy(s => (s.transform.position - king).sqrMagnitude)
                .FirstOrDefault();
            return nearest != null && (nearest.transform.position - king).magnitude <= NearbyRadius ? Root(nearest) : null;
        }

        private static BuildSlot Root(BuildSlot slot) => slot.ActivatorUpgradesThis && slot.ActivatorBuilding != null ? slot.ActivatorBuilding : slot;

        public static string BuildingName(BuildSlot slot)
        {
            if (slot == null) return "-";
            string name = TextTranslator.Translate(slot.LOCIDENTIFIER_NAME);
            return string.IsNullOrEmpty(name) || name.StartsWith("Building/") ? slot.buildingName : name;
        }

        public static BuildSlot FindBuilding(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Equals("nearest", StringComparison.OrdinalIgnoreCase)) return NearestBuilding();
            Vector3 king = PlayerInteraction.instance.transform.position;
            var slot = Object.FindObjectsOfType<BuildSlot>()
                .Where(s => string.Equals(s.buildingName, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(BuildingName(s), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => (s.transform.position - king).sqrMagnitude)
                .FirstOrDefault();
            return slot != null ? Root(slot) : null;
        }

        private static BuildSlot RequireUpgradeable(BuildSlot slot)
        {
            RequireDay();
            if (slot == null) throw new InvalidOperationException("No building nearby");
            if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) throw new InvalidOperationException("Finish the open choice first");
            if (!slot.CanBeUpgraded)
            {
                throw new InvalidOperationException(slot.Level >= slot.Upgrades.Count || slot.Upgrades[slot.Level].disableInThisMode
                    ? $"{BuildingName(slot)} is fully upgraded"
                    : $"{BuildingName(slot)} can't be upgraded yet (its parent building needs a higher level)");
            }
            return slot;
        }

        /// <summary>Builds or upgrades one level for free. With no branch, a choice upgrade shows the game's popup.</summary>
        public static string Upgrade(BuildSlot slot, int? branch = null)
        {
            RequireUpgradeable(slot);
            var player = PlayerInteraction.instance;
            var upgrade = slot.Upgrades[slot.Level];
            if (branch.HasValue)
            {
                if (branch < 0 || branch >= upgrade.upgradeBranches.Count)
                    throw new ArgumentException($"branch must be 0-{upgrade.upgradeBranches.Count - 1}");
                UpgradeWithBranch(slot, upgrade, branch.Value);
            }
            else
            {
                if (upgrade.upgradeBranches.Count > 1) UIFrameManager.instance?.CloseAllFrames();
                slot.TryToBuildOrUpgradeAndPay(player);
                Plugin.Instance.StartCoroutine(RefreshAfterChoice(slot));
            }
            Mark("free upgrade");
            return $"{BuildingName(slot)}: level {slot.Level}";
        }

        private static readonly AccessTools.FieldRef<BuildSlot, BuildSlot.Upgrade> UpgradeSelectedRef =
            AccessTools.FieldRefAccess<BuildSlot, BuildSlot.Upgrade>("upgradeSelected");
        private static readonly AccessTools.FieldRef<BuildSlot, PlayerInteraction> PlayerSelectedRef =
            AccessTools.FieldRefAccess<BuildSlot, PlayerInteraction>("playerInteractionSelected");

        // TryToBuildOrUpgradeAndPay with a chosen branch instead of the popup.
        private static void UpgradeWithBranch(BuildSlot slot, BuildSlot.Upgrade upgrade, int branch)
        {
            var player = PlayerInteraction.instance;
            var children = slot.BuiltSlotsThatRelyOnThisBuilding.Where(c => c != null && c.ActivatorUpgradesThis && c.ActivatorBuilding == slot).ToList();
            foreach (var child in children)
            {
                child.gameObject.SetActive(true);
                child.ExecuteBuildOrUpgrade(player, false);
            }
            UpgradeSelectedRef(slot) = upgrade;
            PlayerSelectedRef(slot) = player;
            slot.OnUpgradeChoiceComplete(upgrade.upgradeBranches[branch].choiceDetails);
            RefreshInteractors(slot);
        }

        private static void RefreshInteractors(BuildSlot slot)
        {
            if (slot == null) return;
            foreach (var child in slot.BuiltSlotsThatRelyOnThisBuilding)
            {
                if (child != null && child.buildingInteractor != null) child.buildingInteractor.UpdateInteractionState();
            }
            if (slot.buildingInteractor != null) slot.buildingInteractor.UpdateInteractionState();
        }

        private static IEnumerator RefreshAfterChoice(BuildSlot slot)
        {
            yield return null;
            while (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) yield return null;
            yield return null;
            RefreshInteractors(slot);
        }

        /// <summary>Upgrades the building to its last level for free, asking for each choice like the game does.</summary>
        public static string MaxUpgrade(BuildSlot slot)
        {
            RequireUpgradeable(slot);
            UIFrameManager.instance?.CloseAllFrames();
            Plugin.Instance.StartCoroutine(MaxUpgradeRoutine(slot));
            Mark("max building");
            return $"maxing {BuildingName(slot)}";
        }

        private static IEnumerator MaxUpgradeRoutine(BuildSlot slot)
        {
            var player = PlayerInteraction.instance;
            for (int guard = 0; guard < 12 && slot != null && slot.CanBeUpgraded && IsPlanningTime; guard++)
            {
                int level = slot.Level;
                slot.TryToBuildOrUpgradeAndPay(player);
                yield return null;
                while (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) yield return null;
                yield return null;
                if (slot.Level == level) break;  // choice cancelled
            }
            RefreshInteractors(slot);
        }

        /// <summary>Builds every blueprint (level 1, free), including ones that unlock along the way.</summary>
        public static string BuildAllBlueprints()
        {
            RequireDay();
            if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) throw new InvalidOperationException("Finish the open choice first");
            var player = PlayerInteraction.instance;
            int built = 0;
            for (int pass = 0; pass < 10; pass++)
            {
                var blueprints = Object.FindObjectsOfType<BuildSlot>()
                    .Where(s => s.State == BuildSlot.BuildingState.Blueprint && !s.ActivatorUpgradesThis && s.CanBeUpgraded && !s.NextUpgradeIsChoice &&
                                s.buildingInteractor != null && s.buildingInteractor.isActiveAndEnabled)
                    .ToList();
                if (blueprints.Count == 0) break;
                foreach (var slot in blueprints)
                {
                    slot.TryToBuildOrUpgradeAndPay(player, false);
                    built++;
                }
            }
            Mark("build all");
            return $"built {built} buildings";
        }

        // ---- Units and the king -----------------------------------------------------------------------------------

        private static List<TaggedObject> Find(TagManager.ETag tag)
        {
            var list = new List<TaggedObject>();
            TagManager.instance.FindAllTaggedObjectsWithTags(list, [tag], []);
            return list;
        }

        /// <summary>Revives and fully heals the king, every unit and every building.</summary>
        public static string HealAll()
        {
            RequireLevel();
            bool kingDown = KingHp is { KnockedOut: true };
            int healed = 0;
            foreach (var obj in Find(TagManager.ETag.PlayerOwned))
            {
                if (obj == null || obj.Hp == null || !obj.gameObject.activeInHierarchy) continue;
                if (obj.Hp.KnockedOut || obj.Hp.HpValue < obj.Hp.maxHp) healed++;
                obj.Hp.Revive(true, 1f);
                obj.RemoveTag(TagManager.ETag.AUTO_NoReviveNextMorning);
            }
            if (kingDown) KingRevived();
            foreach (var interactor in TagManager.instance.playerBuildingInteractors)
            {
                if (interactor != null) interactor.UpdateInteractionState();
            }
            Mark("heal all");
            return $"healed {healed}";
        }

        private static Hp KingHp => PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<Hp>() : null;

        public static string ReviveKing()
        {
            RequireLevel();
            var hp = KingHp ?? throw new InvalidOperationException("No king found");
            bool wasDown = hp.KnockedOut;
            hp.Revive(true, 1f);
            if (wasDown) KingRevived();
            Mark("revive king");
            return wasDown ? "king revived" : "king healed";
        }

        private static void KingRevived()
        {
            var revive = PlayerInteraction.instance.GetComponent<AutoRevive>();
            if (revive != null) revive.onReviveTrigger.Invoke();
        }

        public static int KillAllEnemies()
        {
            if (!InLevel) return 0;
            var enemies = Find(TagManager.ETag.EnemyOwned);
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.Hp == null) continue;
                enemy.Hp.invulnerable = false;
                enemy.Hp.TakeDamage(float.MaxValue);
            }
            return enemies.Count;
        }

        public static string KillEnemies()
        {
            RequireLevel();
            int killed = KillAllEnemies();
            Mark("kill enemies");
            return $"removed {killed} enemies";
        }

        /// <summary>King takes no damage.</summary>
        public static bool GodMode { get; private set; }

        /// <summary>Units and buildings take no damage.</summary>
        public static bool InvulnerableKingdom { get; private set; }

        public static string SetGodMode(bool on)
        {
            GodMode = on;
            if (on) Mark("god mode");
            return $"god mode {(on ? "on" : "off")}";
        }

        public static string SetInvulnerableKingdom(bool on)
        {
            InvulnerableKingdom = on;
            if (on) Mark("invulnerable kingdom");
            return on ? "invulnerable kingdom on: player units and buildings take no damage (rule damage and kill too) until off or the next level load"
                : "invulnerable kingdom off";
        }

        /// <summary>Turns god mode and the invulnerable kingdom off (the agent bridge does this on every level load:
        /// they're test helpers for one run). Returns what was on.</summary>
        public static string ResetCheats()
        {
            var was = new List<string>();
            if (GodMode) { GodMode = false; was.Add("god mode"); }
            if (InvulnerableKingdom) { InvulnerableKingdom = false; was.Add("invulnerable kingdom"); }
            return was.Count == 0 ? "none" : string.Join(", ", was) + " off";
        }

        internal static bool BlocksDamage(Hp hp)
        {
            if (!GodMode && !InvulnerableKingdom) return false;
            var tagged = hp.TaggedObj;
            if (tagged == null) return false;
            var tags = tagged.Tags;
            if (tags.Contains(TagManager.ETag.Player)) return GodMode;
            return InvulnerableKingdom && tags.Contains(TagManager.ETag.PlayerOwned);
        }

        // ---- Time -------------------------------------------------------------------------------------------------

        public static readonly float[] Speeds = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

        /// <summary>Game speed, shared with the game's own fast-forward toggle.</summary>
        public static float Speed => PlayerMovement.gameplayTimeScale;

        public static bool IsVanillaSpeed(float speed) => Mathf.Approximately(speed, 1f) || Mathf.Approximately(speed, 2f);

        public static string SetSpeed(float speed)
        {
            speed = Mathf.Clamp(speed, 0.1f, 8f);
            PlayerMovement.gameplayTimeScale = speed;
            // While a menu is open time is frozen; the game restores gameplayTimeScale when it closes.
            if (Time.timeScale != 0f) Time.timeScale = speed;
            // 1x and 2x are the game's own speeds.
            if (!IsVanillaSpeed(speed)) Mark("game speed");
            return $"speed x{speed:0.##}";
        }

        // ---- Loadout ----------------------------------------------------------------------------------------------

        public enum Category { Weapon, Perk, Mutator }

        public static Category? CategoryOf(Equippable e) => e switch
        {
            EquippableWeapon => Category.Weapon,
            EquippablePerk => Category.Perk,
            EquippableMutation => Category.Mutator,
            _ => null,
        };

        public static IEnumerable<Equippable> AllOf(Category category) =>
            PerkManager.instance == null ? [] : PerkManager.instance.allEquippables.Where(e => e != null && CategoryOf(e) == category);

        public static string DisplayName(Equippable e)
        {
            string name = TextTranslator.Translate(e.LOCIDENTIFIER_NAME);
            return string.IsNullOrEmpty(name) || name.StartsWith("Equippable/") ? e.displayName : name;
        }

        public static string Description(Equippable e)
        {
            string text = TextTranslator.Translate(e.LOCIDENTIFIER_DESCRIPTION);
            return string.IsNullOrEmpty(text) || text.StartsWith("Equippable/") ? e.description : text;
        }

        public static bool LoadoutAvailable => InLevel && LocalGamestate.SelectedGameMode != LocalGamestate.GameMode.EternalTrial;

        /// <summary>Whether applying a loadout keeps the current moment: a savestate of now is loaded back with the new
        /// loadout. Not in the tutorial or while the Builder Studio edits a level, where savestates don't work.</summary>
        public static bool LoadoutKeepsMoment =>
            LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.Classic && !StateUtil.StudioEditing &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != StateUtil.TutorialScene;

        /// <summary>Without LoadoutKeepsMoment: whether applying reloads today from the dawn save (true) or restarts the level.</summary>
        public static bool LoadoutKeepsProgress =>
            !MatchSaveLoadHandler.SaveLoadForbidden && MatchSaveLoadHandler.IsLoadingPermitted && EnemySpawner.instance.Wavenumber >= 0;

        private static bool remarkAfterRestart;

        /// <summary>Equips <paramref name="loadout"/> and reloads the level so every perk and the weapon are applied
        /// the way the game applies them at level start: normally from a savestate of this moment (buildings, units,
        /// gold and mod state kept); where savestates don't work, from today's dawn save, or a restart on day 1.</summary>
        public static string ApplyLoadout(ICollection<Equippable> loadout)
        {
            RequireLevel();
            if (!LoadoutAvailable) throw new InvalidOperationException("Not available in Eternal Trials");
            if (loadout.Count(e => e is EquippableWeapon) != 1) throw new InvalidOperationException("Pick exactly one weapon");
            if (LoadoutKeepsMoment)
            {
                SaveStates.RestoreWithLoadout(loadout);
                Mark("change loadout");
                return "loadout applied, reloading this moment";
            }
            bool keep = LoadoutKeepsProgress;
            PerkManager.ClearAllEquipped();
            foreach (var e in loadout) PerkManager.SetEquipped(e, true);
            if (keep)
            {
                // "Retry day" re-applies the save's loadout, so store the new one there too.
                MatchSaveLoadHandler.CurrentSave.FetchLoadout();
                MatchSaveLoadHandler.SaveRun();
                MatchSaveLoadHandler.OverwriteCurrentSave = false;
            }
            else
            {
                MatchSaveLoadHandler.OverwriteCurrentSave = true;
                // A restart starts a fresh run, which clears the run's gameplay impact; mark it again after.
                remarkAfterRestart = true;
            }
            Mark("change loadout");
            // Like the game's own Restart, the menu stays up (frozen) until the clouds cover it, and the transition
            // closes it at its midpoint; closing it first would flash the running map before the clouds come in.
            SceneTransitionManager.instance.RestartCurrentLevel();
            if (!SceneTransitionManager.instance.SceneTransitionIsRunning) UIFrameManager.instance?.CloseAllFrames();
            return keep ? "loadout applied, reloading today" : "loadout applied, restarting the level";
        }

        internal static void OnImpactReset()
        {
            if (!remarkAfterRestart) return;
            remarkAfterRestart = false;
            Mark("change loadout");
        }
    }
}
