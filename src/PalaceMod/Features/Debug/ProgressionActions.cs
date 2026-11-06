using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The debug menu's progression cheats, used on the level-select map: player level and XP, which stages are beaten
    (which is what opens the next ones), and the weapons, perks and mutators those gate. Everything changes the game's
    own progression singletons and is written with the game's own SaveLoadManager.SaveGame, so it's real progression:
    it survives quitting and is what the loadout screen and the map read.

    How the game gates things: an item with a meta-level unlock is unlocked while PerkManager.level is above its
    reward level (rewards come one per level, in order), so unlocking one sets the level just past it and locking one
    sets the level to it, which also locks every later reward. An item with a campaign unlock opens when its stage is
    beaten. A stage is open when the stage it requires is beaten. The map reads all of this when it loads, so changes
    show on it after re-entering (ReloadMap).
*/
namespace PalaceMod
{
    internal static class ProgressionActions
    {
        /// <summary>On a level-select map (the overworld), outside any level.</summary>
        public static bool OnMap =>
            MapLoaded && SceneTransitionManager.instance != null &&
            SceneTransitionManager.instance.CurrentSceneState != SceneTransitionManager.SceneState.MainMenu &&
            !SceneTransitionManager.instance.SceneTransitionIsRunning && !DebugActions.InLevel;

        // A map scene is one with stage markers (_LevelSelect and the DLC maps; the game's static
        // LevelSelectManager.instance isn't reliable). Only checked when a menu opens or a key is pressed.
        internal static bool MapLoaded => UnityEngine.Object.FindObjectOfType<LevelInteractor>() != null;

        public static bool Ready => PerkManager.instance != null && LevelProgressManager.instance != null && SaveLoadManager.instance != null;

        private static void Require()
        {
            if (!Ready) throw new InvalidOperationException("the game's progression isn't loaded");
        }

        private static PerkManager Perks => PerkManager.instance;

        private static string Done(string what)
        {
            KeepLoadoutValid();
            SaveLoadManager.instance.SaveGame();
            Log.For<DebugMenuManager>().Info($"Progression: {what}");
            return what;
        }

        // ---- Level and XP ---------------------------------------------------------------------------------------

        public static int Level => Perks.level;
        public static int Xp => Perks.xp;
        public static int XpNeeded => Perks.NextMetaLevel.requiredXp;
        public static int RewardLevels => Perks.MetaLevels.Count;

        /// <summary>The level at which every meta-level reward is unlocked.</summary>
        public static int MaxRewardLevel => RewardLevels + 1;

        public static string SetLevel(int level)
        {
            Require();
            level = Mathf.Clamp(level, 1, PerkManager.MaxLevel);
            if (level > Perks.level) AchievementGuard.CheatedLevel();
            Perks.level = level;
            Perks.xp = 0;
            return Done($"level {level}");
        }

        public static string SetXp(int xp)
        {
            Require();
            Perks.xp = Mathf.Clamp(xp, 0, Mathf.Max(0, XpNeeded - 1));
            return Done($"{Perks.xp} / {XpNeeded} XP");
        }

        // ---- Stages ---------------------------------------------------------------------------------------------

        public enum StageState { Locked, Open, Beaten }

        public static IEnumerable<LevelInfo> Stages =>
            (LevelProgressManager.instance?.levelInfos ?? []).Where(l => l != null && LevelProgressManager.SceneExists(l.sceneName));

        public static StageState StateOf(LevelInfo stage) =>
            stage.Beaten ? StageState.Beaten : stage.unlockRequirement == null || stage.unlockRequirement.Beaten ? StageState.Open : StageState.Locked;

        public static string StageName(LevelInfo stage)
        {
            string name = stage.LocalizedDisplayName;
            if (string.IsNullOrEmpty(name) || name.StartsWith("Map Name/")) name = stage.displayName;
            // Challenges share their map's name; the game tells them apart by subtitle.
            string subtitle = stage.displaySubtitle;
            if (!string.IsNullOrEmpty(subtitle) && !subtitle.StartsWith("Map/")) return $"{name}: {subtitle}";
            return stage.sceneName != stage.displayName && stage.sceneName.StartsWith(stage.displayName) ? stage.sceneName : name;
        }

        public static LevelInfo FindStage(string name)
        {
            string key = Squash(name);
            return Stages.FirstOrDefault(s => Squash(s.sceneName) == key || Squash(s.displayName) == key || Squash(StageName(s)) == key)
                   ?? throw new ArgumentException($"no stage \"{name}\"");
        }

        private static void SetBeaten(LevelInfo stage, bool beaten)
        {
            var data = stage.LevelData;
            if (data == null) return;
            data.beatenBest = beaten;
            if (beaten) AchievementGuard.Cheated(stage.sceneName);
        }

        // Beats the chain of stages that opens this one.
        private static void OpenPath(LevelInfo stage)
        {
            var seen = new HashSet<LevelInfo>();
            for (var r = stage.unlockRequirement; r != null && seen.Add(r); r = r.unlockRequirement)
                if (!r.Beaten) SetBeaten(r, true);
        }

        public static string SetStage(LevelInfo stage, StageState state)
        {
            Require();
            switch (state)
            {
                case StageState.Beaten:
                    OpenPath(stage);
                    SetBeaten(stage, true);
                    break;
                case StageState.Open:
                    OpenPath(stage);
                    SetBeaten(stage, false);
                    break;
                default:
                    if (stage.unlockRequirement == null) throw new InvalidOperationException($"{StageName(stage)} is always open");
                    SetBeaten(stage, false);
                    SetBeaten(stage.unlockRequirement, false);
                    break;
            }
            return Done($"{StageName(stage)}: {StateOf(stage).ToString().ToLowerInvariant()}");
        }

        // ---- Unlocks --------------------------------------------------------------------------------------------

        /// <summary>The meta level whose reward this is, or 0 if it isn't one.</summary>
        public static int RewardLevel(Equippable e) =>
            e.unlockRequirement == Equippable.UnlockRequirement.MetaLevel && Perks.metaLevelByPerk.TryGetValue(e, out int level) ? level : 0;

        /// <summary>The stage that unlocks this item, or null.</summary>
        public static LevelInfo UnlockStage(Equippable e) =>
            e.unlockRequirement == Equippable.UnlockRequirement.CampaignProgress && e.requiredBeatenLevel != Equippable.LevelName.DefaultUnlocked
                ? LevelProgressManager.instance.GetLevelInfoFromSceneName(e.requiredBeatenLevel.ToString())
                : null;

        public static bool AlwaysUnlocked(Equippable e) => RewardLevel(e) == 0 && UnlockStage(e) == null;

        public static string HowUnlocked(Equippable e)
        {
            int reward = RewardLevel(e);
            if (reward > 0)
                return $"Reward for reaching level {reward + 1}. Unlocking sets your level to {reward + 1}; locking sets it to {reward}, which also locks every later reward.";
            var stage = UnlockStage(e);
            if (stage != null) return $"Unlocked by beating {StageName(stage)}. Toggling it marks {StageName(stage)} beaten or not.";
            return "Always unlocked.";
        }

        public static string SetUnlocked(Equippable e, bool unlocked)
        {
            Require();
            int reward = RewardLevel(e);
            var stage = UnlockStage(e);
            if (reward > 0)
            {
                if (unlocked && Perks.level <= reward) { Perks.level = reward + 1; Perks.xp = 0; AchievementGuard.CheatedLevel(); }
                else if (!unlocked && Perks.level > reward) { Perks.level = reward; Perks.xp = 0; }
            }
            else if (stage != null)
            {
                if (unlocked) { OpenPath(stage); SetBeaten(stage, true); }
                else SetBeaten(stage, false);
            }
            else throw new InvalidOperationException($"{DebugActions.DisplayName(e)} is always unlocked");
            return Done($"{DebugActions.DisplayName(e)} {(e.IsUnlocked ? "unlocked" : "locked")} (level {Perks.level})");
        }

        // ---- Shortcuts ------------------------------------------------------------------------------------------

        public static string UnlockEverything()
        {
            Require();
            if (Perks.level < MaxRewardLevel) { Perks.level = MaxRewardLevel; Perks.xp = 0; AchievementGuard.CheatedLevel(); }
            foreach (var stage in Stages) SetBeaten(stage, true);
            return Done($"everything unlocked (level {Perks.level}, {Stages.Count()} stages beaten)");
        }

        /// <summary>Level 1, no XP, no stage beaten. Scores, in-progress runs and Eternal Trials are kept; Profiles'
        /// "Reset progress" on the title screen is the full wipe.</summary>
        public static string ResetProgression()
        {
            Require();
            Perks.level = 1;
            Perks.xp = 0;
            foreach (var stage in Stages) SetBeaten(stage, false);
            AchievementGuard.Clear();
            return Done("progression reset (level 1, no stages beaten)");
        }

        /// <summary>Re-enters the current map, so it shows the changes (it reads progression when it loads).</summary>
        public static string ReloadMap()
        {
            if (!OnMap) throw new InvalidOperationException("not on the map");
            SceneTransitionManager.instance.TransitionToLevelSelect();
            return "reloading the map";
        }

        // Nothing locked stays equipped, and there's always an unlocked weapon.
        private static void KeepLoadoutValid()
        {
            foreach (var e in Perks.CurrentlyEquipped.Where(e => e != null && !e.IsUnlocked).ToList()) PerkManager.SetEquipped(e, false);
            if (!Perks.CurrentlyEquipped.Any(e => e is EquippableWeapon))
            {
                var weapon = DebugActions.AllOf(DebugActions.Category.Weapon).FirstOrDefault(w => w.IsUnlocked);
                if (weapon != null) PerkManager.SetEquipped(weapon, true);
            }
        }

        public static string Describe()
        {
            Require();
            var items = new[] { DebugActions.Category.Weapon, DebugActions.Category.Perk, DebugActions.Category.Mutator }.SelectMany(DebugActions.AllOf).ToList();
            return $"on map {OnMap} (map {MapLoaded}, scene state {SceneTransitionManager.instance?.CurrentSceneState}, in level {DebugActions.InLevel}); " +
                   $"level {Level} (rewards to {MaxRewardLevel}), xp {Xp}/{XpNeeded}, unlocked {items.Count(i => i.IsUnlocked)}/{items.Count}; stages: " +
                   string.Join(", ", Stages.Select(s => $"{s.sceneName}={StateOf(s)}")) + "; locked: " +
                   string.Join(", ", items.Where(i => !i.IsUnlocked).Select(i => i.name)) + "; equipped: " +
                   string.Join(", ", Perks.CurrentlyEquipped.Select(e => e.name));
        }

        internal static string Squash(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    /*
        Progression set through the debug menu mustn't earn Steam achievements. The game grants "beat <stage>" for
        every beaten stage each time it saves or shows the map, and "max level" whenever the map finds everything
        unlocked, so: stages the menu marked beaten don't grant theirs until the stage is won for real, and once the
        menu has raised the level, "max level" is only granted from the end of a real match. Reset progression clears
        both. The list is kept in the mod's config (hidden), not in the game's save.
    */
    internal static class AchievementGuard
    {
        internal static BepInEx.Configuration.ConfigEntry<string> Stages;
        internal static BepInEx.Configuration.ConfigEntry<bool> Level;

        private static HashSet<string> Set() =>
            new((Stages?.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

        internal static void Cheated(string scene)
        {
            if (Stages == null) return;
            var set = Set();
            if (set.Add(scene)) Stages.Value = string.Join(",", set.OrderBy(s => s, StringComparer.Ordinal));
        }

        internal static void CheatedLevel()
        {
            if (Level != null) Level.Value = true;
        }

        internal static void Clear()
        {
            if (Stages != null) Stages.Value = "";
            if (Level != null) Level.Value = false;
        }

        private static bool WonForReal(string scene) =>
            LocalGamestate.Instance != null && LocalGamestate.Instance.CurrentState == LocalGamestate.State.AfterMatchVictory &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene;

        /// <summary>Whether the game may grant "beat <paramref name="scene"/>" now.</summary>
        internal static bool AllowLevelBeaten(string scene)
        {
            var set = Set();
            if (!set.Contains(scene)) return true;
            if (!WonForReal(scene)) return false;
            set.Remove(scene);
            Stages.Value = string.Join(",", set.OrderBy(s => s, StringComparer.Ordinal));
            return true;
        }

        internal static bool AllowMaxLevel() => Level == null || !Level.Value || !ProgressionActions.MapLoaded; // not from the map

        internal static string Describe() => $"guarded stages [{Stages?.Value}], level raised {Level?.Value}";
    }
}
