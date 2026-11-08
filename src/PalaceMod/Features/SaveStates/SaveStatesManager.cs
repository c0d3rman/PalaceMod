using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

/*
    Savestates for practice and theorycrafting: quick save/load keys, named slots with thumbnails in a menu
    (pause > Savestates), automatic states at every dawn and dusk, an undo state before every load, and optionally
    loading the last state instead of losing. The capture/restore logic is in StateCapture/StateRestorer and the
    public API in SaveStates.

    The player's real match save is never written by this feature. While "Protect real save" is on, the game's own
    match-save writes are skipped for the rest of a run once a state has been loaded in it, so practising can't
    replace the run you'd continue from the level select.
*/
namespace PalaceMod
{
    public enum AutoStateMode { Off, Dawn, Dusk, Both }

    public class SaveStatesManager : BaseManager
    {
        public override string FeatureSection => "Savestates";
        public override bool AffectsRanking => true; // off while "Gameplay-changing features" is off
        public override string FeatureDescription =>
            "Save and load the whole game mid-level, for practice. Pause > Savestates lists them. Loading a state makes the run unranked.";
        // The menu and keys are there to use or not; the passive part (automatic states) has its own setting.
        protected override bool HasEnabledSwitch => false;

        // The real-save guard must outlive the master switch: turning gameplay-changing features off after a load
        // mustn't let the practice run be saved. The other patches check IsEnabled themselves.
        protected override bool UnpatchWhenDisabled => false;

        private ConfigEntry<AutoStateMode> autoMode;
        private ConfigEntry<int> autoHistory;
        private ConfigEntry<bool> confirmLoad;
        private ConfigEntry<bool> loadOnDefeat;
        private ConfigEntry<bool> protectSave;
        private ConfigEntry<bool> hintShown;

        internal Keybind QuickSaveKey { get; private set; }
        internal Keybind QuickLoadKey { get; private set; }
        private Keybind menuKey;

        internal AutoStateMode AutoMode => autoMode.Value;
        internal int AutoHistory => autoHistory.Value;
        internal bool ConfirmLoad => confirmLoad.Value;
        /// <summary>An AutoDusk state will be taken when the next night starts.</summary>
        internal bool CapturesDusk => IsEnabled && autoMode.Value is AutoStateMode.Dusk or AutoStateMode.Both;

        private SaveStateBrowser browser;
        private string lastLevel;
        private DayNightCycle lastCycle;
        private bool practiceRun, savesSkippedLogged;
        private float lastDefeatLoad = -100f;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var config = plugin.Config;
            autoMode = config.Bind(FeatureSection, "Automatic states", AutoStateMode.Both, new ConfigDescription(
                "When to save a state automatically: shortly after sunrise, right before the night starts, or both. Off saves only when you ask. Retry earlier day needs the dawn states."));
            autoHistory = config.Bind(FeatureSection, "Automatic history", 40, new ConfigDescription(
                "How many automatic states (dawn and pre-night together) to keep per level; older ones are deleted. About 0.1 MB each.", new AcceptableValueRange<int>(1, 80)));
            confirmLoad = config.Bind(FeatureSection, "Confirm loads", true, new ConfigDescription(
                "Ask before loading a state from the menu. The quick load key never asks; every load keeps an undo state."));
            loadOnDefeat = config.Bind(FeatureSection, "Load on defeat", false, new ConfigDescription(
                "When the castle falls, load the last state instead of ending the run."));
            protectSave = config.Bind(FeatureSection, "Protect real save", true, new ConfigDescription(
                "After loading a state, the game stops saving this run's progress, so the run you continue from the level select stays as it was."));
            hintShown = config.Bind(FeatureSection, "Hint shown", false, new ConfigDescription(
                "Whether the first-use hint was shown.", null, new SettingTags { Hidden = true }));

            QuickSaveKey = Keybinds.Register(FeatureSection, "quicksave", "Quick save", new KeyboardShortcut(KeyCode.F5),
                "Saves the current moment to the quick-save slot.");
            QuickLoadKey = Keybinds.Register(FeatureSection, "quickload", "Quick load", new KeyboardShortcut(KeyCode.F9),
                "Loads the quick-save slot (an undo state is kept).");
            menuKey = Keybinds.Register(FeatureSection, "menu", "Savestates menu", new KeyboardShortcut(KeyCode.F6),
                "Opens the savestates menu.");

            browser = new SaveStateBrowser();
            PauseMenu.AddButton("savestates", "Savestates", () => browser.Open(), order: 20,
                visible: () => IsEnabled && SaveStates.CurrentLevel != null && SaveStates.CurrentLevel != StateUtil.TutorialScene);

            GameplayImpact.Reset += () =>
            {
                if (practiceRun) Log.Info("New run; the game saves normally again");
                practiceRun = savesSkippedLogged = false;
            };

            // Loading a state never makes a run look cleaner than the one it came from.
            SaveStates.RegisterState<List<string>>("gameplay-impact",
                () => GameplayImpact.Reasons.Count > 0 ? [.. GameplayImpact.Reasons] : null,
                reasons => { foreach (string reason in reasons) GameplayImpact.Mark(reason); });

            RegisterDevCommands();
        }

        protected override void OnFeatureDisabled()
        {
            browser.Close();
        }

        public override void Update()
        {
            string level = SaveStates.CurrentLevel;
            if (level != lastLevel && !StateRestorer.Busy)
            {
                lastLevel = level;
                if (SaveStates.Last != null && SaveStates.Last.Level != level) SaveStates.Last = null;
            }
            // Day 1 has no sunrise, so its dawn state is taken when a level starts fresh (not when a state loads).
            var cycle = DayNightCycle.Instance;
            if (cycle != lastCycle)
            {
                lastCycle = cycle;
                if (cycle != null && !StateRestorer.Busy) Plugin.Instance.StartCoroutine(FirstDawnAfterDelay(cycle));
            }
            if (level == null) return;
            if (QuickSaveKey.Down) QuickSave();
            else if (QuickLoadKey.Down) QuickLoad();
            else if (menuKey.Down) browser.Open();
        }

        internal void MarkPracticeRun() => practiceRun = true;

        private void QuickSave()
        {
            try
            {
                SaveStates.Capture(null, SaveStateKind.Quick);
                if (!hintShown.Value)
                {
                    hintShown.Value = true;
                    Notify.Show($"Quick saved. {QuickLoadKey.Shortcut} loads it; Pause > Savestates has more.", NotifyKind.Success, 6f, "savestates");
                }
                else Notify.Show("Quick saved", NotifyKind.Success, key: "savestates");
            }
            catch (InvalidOperationException e) { Notify.Show($"Can't save: {e.Message}", NotifyKind.Warning, key: "savestates"); }
            catch (Exception e)
            {
                Log.Error($"Quick save failed", e);
                Notify.Show("Quick save failed", NotifyKind.Error, key: "savestates");
            }
        }

        private void QuickLoad()
        {
            var state = SaveStates.Latest(kind: SaveStateKind.Quick);
            if (state == null)
            {
                Notify.Show($"No quick save yet; {QuickSaveKey.Shortcut} makes one", NotifyKind.Warning, key: "savestates");
                return;
            }
            try { SaveStates.Restore(state); }
            catch (InvalidOperationException e) { Notify.Show($"Can't load: {e.Message}", NotifyKind.Warning, key: "savestates"); }
        }

        // ---- Automatic states ---------------------------------------------------------------------------------------

        private static void TryAuto(SaveStateKind kind)
        {
            var manager = Feature<SaveStatesManager>.Instance;
            if (manager == null || !manager.IsEnabled || StateRestorer.Busy) return;
            var mode = manager.AutoMode;
            bool wanted = mode == AutoStateMode.Both || (kind == SaveStateKind.AutoDawn ? mode == AutoStateMode.Dawn : mode == AutoStateMode.Dusk);
            if (!wanted) return;
            if (!SaveStates.CanCapture(out string reason))
            {
                Log.For<SaveStatesManager>().Debug($"No {kind} state ({reason})");
                return;
            }
            try { SaveStates.Capture(null, kind); }
            catch (Exception e) { Log.For<SaveStatesManager>().Error($"Automatic {kind} state failed", e); }
        }

        private static IEnumerator DawnAfterDelay()
        {
            // The game's own dawn save happens a few frames after sunrise; coins flying to the king count as banked.
            yield return new WaitForSeconds(1f);
            TryAuto(SaveStateKind.AutoDawn);
        }

        private static IEnumerator FirstDawnAfterDelay(DayNightCycle cycle)
        {
            // Wait for the level to finish loading (the scene transition, then the game's match loader).
            float deadline = Time.realtimeSinceStartup + 60f;
            while (DayNightCycle.Instance == cycle && Time.realtimeSinceStartup < deadline &&
                   (SceneTransitionManager.instance == null || SceneTransitionManager.instance.SceneTransitionIsRunning ||
                    LocalMatchSaveLoad.CurrentlyLoadingMatch || LocalGamestate.Instance == null ||
                    LocalGamestate.Instance.CurrentState != LocalGamestate.State.InMatch))
                yield return null;
            yield return new WaitForSeconds(1f);
            if (DayNightCycle.Instance != cycle || StateRestorer.Busy || EnemySpawner.instance == null || EnemySpawner.instance.Wavenumber != -1 ||
                cycle.CurrentTimestate != DayNightCycle.Timestate.Day) yield break;
            TryAuto(SaveStateKind.AutoDawn);
        }

        [HarmonyPatch(typeof(DayNightCycle), nameof(DayNightCycle.SwitchToNight))]
        private class Dusk_Patch
        {
            private static void Prefix(DayNightCycle __instance)
            {
                if (__instance.CurrentTimestate == DayNightCycle.Timestate.Day) TryAuto(SaveStateKind.AutoDusk);
            }
        }

        [HarmonyPatch(typeof(DayNightCycle), "DawnCallAfterSunrise")]
        private class Dawn_Patch
        {
            private static void Postfix()
            {
                if (Feature<SaveStatesManager>.On && !StateRestorer.Busy) Plugin.Instance.StartCoroutine(DawnAfterDelay());
            }
        }

        // ---- Real save protection -------------------------------------------------------------------------------

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private class SaveRun_Patch
        {
            private static bool Prefix()
            {
                var manager = Feature<SaveStatesManager>.Instance;
                if (manager == null || !manager.practiceRun || !manager.protectSave.Value) return true;
                // SaveRun is also where the game stores the loadout into the in-memory save that retry-day applies.
                MatchSaveLoadHandler.CurrentSave?.FetchLoadout();
                if (!manager.savesSkippedLogged)
                {
                    manager.savesSkippedLogged = true;
                    Log.For<SaveStatesManager>().Info("A state was loaded this run, so the game's match save isn't written (Protect real save)");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(InterestPerk), nameof(InterestPerk.OnDusk))]
        private class Interest_Patch
        {
            private static bool Prefix() => !StateRestorer.ReplayingDusk;
        }

        [HarmonyPatch(typeof(UpgradeBuildersGuild), nameof(UpgradeBuildersGuild.OnDuskEarly))]
        private class BuildersGuild_Patch
        {
            private static bool Prefix() => !StateRestorer.ReplayingDusk;
        }

        // ---- Load on defeat -------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(LocalGamestate), "OnVitalObjectKill")]
        private class Defeat_Patch
        {
            private static bool Prefix()
            {
                var manager = Feature<SaveStatesManager>.Instance;
                if (manager == null || !manager.IsEnabled || !manager.loadOnDefeat.Value || LocalGamestate.Instance?.CurrentState != LocalGamestate.State.InMatch) return true;
                // A state that loses again right away would loop; let the defeat through then.
                if (Time.realtimeSinceStartup - manager.lastDefeatLoad < 10f) return true;
                var last = SaveStates.Last;
                var state = last != null && last.Level == SaveStates.CurrentLevel && System.IO.File.Exists(last.JsonPath) ? last
                    : SaveStates.List().FirstOrDefault(s => s.Kind != SaveStateKind.BeforeLoad);
                if (state == null || !SaveStates.CanRestore(state, out _)) return true;
                manager.lastDefeatLoad = Time.realtimeSinceStartup;
                Notify.Show($"Defeated; loading \"{state.Name}\"", NotifyKind.Warning, key: "savestates");
                StateRestorer.Begin(state, backup: false);
                return false;
            }
        }

        // ---- Dev commands ---------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("savestate", "savestate save [name] | quick | load <id|name|quick|last> | list | dump | delete <id|name> | menu | gold <n> | build [n] | defeat   savestates (gold/build/defeat: test setup)", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
                string rest = string.Join(" ", args.Skip(1));
                switch (sub)
                {
                    case "save":
                        return "saved " + SaveStates.Capture(rest.Length > 0 ? rest : null);
                    case "quick":
                        return "saved " + SaveStates.Capture(null, SaveStateKind.Quick);
                    case "load":
                    {
                        var state = Resolve(rest);
                        SaveStates.Restore(state);
                        return $"loading {state}";
                    }
                    case "list":
                        return "\n" + string.Join("\n", SaveStates.List().Select(s => $"{s.Id}  {s}  {SaveStateBrowser.Ago(s.CreatedUtc)}"));
                    case "dump":
                        return StateRestorer.Summary() + $" restoring={SaveStates.IsRestoring} practiceRun={practiceRun}";
                    case "delete":
                    {
                        var state = Resolve(rest);
                        SaveStates.Delete(state);
                        return $"deleted {state}";
                    }
                    case "menu":
                        browser.Open();
                        return "";
                    case "defeat":
                    {
                        // Test setup: destroy the castle (to try "Load on defeat").
                        var castle = TagManager.instance.FindAllTaggedObjectsWithTagDirect_UseWithCare(TagManager.ETag.CastleCenter).FirstOrDefault();
                        var hp = castle != null ? castle.GetComponent<Hp>() : null;
                        if (hp == null) throw new InvalidOperationException("no castle");
                        hp.invulnerable = false;
                        hp.TakeDamage(hp.HpValue + 1f);
                        return $"castle hp {hp.HpValue}, state {LocalGamestate.Instance.CurrentState}";
                    }
                    case "gold":
                        // Test setup: add gold.
                        PlayerInteraction.instance.AddCoin(int.Parse(rest));
                        return $"gold {PlayerInteraction.instance.Balance}";
                    case "build":
                    {
                        // Test setup: build or upgrade up to n affordable buildings, paying their cost.
                        int n = rest.Length > 0 ? int.Parse(rest) : 5, built = 0;
                        var player = PlayerInteraction.instance;
                        foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
                        {
                            if (built >= n) break;
                            if (!slot.CanBeUpgraded || slot.ActivatorUpgradesThis) continue;
                            int cost = slot.upgrades[slot.Level].cost;
                            if (player.Balance < cost) continue;
                            int before = slot.Level;
                            player.SpendCoins(cost);
                            slot.TryToBuildOrUpgradeAndPay(player, _presentChoice: false);
                            if (slot.Level != before) built++;
                            else player.AddCoin(cost);
                        }
                        return $"built {built}, gold {player.Balance}";
                    }
                    default:
                        throw new ArgumentException("unknown subcommand");
                }
            });
        }

        private static SaveStateInfo Resolve(string key)
        {
            if (string.IsNullOrEmpty(key) || key == "last") return SaveStates.Last ?? SaveStates.Latest() ?? throw new InvalidOperationException("no states");
            if (key == "quick") return SaveStates.Latest(kind: SaveStateKind.Quick) ?? throw new InvalidOperationException("no quick save");
            return SaveStates.Find(key) ?? throw new InvalidOperationException($"no state \"{key}\"");
        }
    }
}
