using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using Thronefall.GameServices;
using UnityEngine;
using UnityEngine.UI;

/*
    Keeps runs that used gameplay-changing mod features (GameplayImpact) off the player's records. While the current
    run has any impact ("unranked"):

    - Blocked: the level's best score and its day-by-day best graphs (LevelData.SaveScoreAndStatsToBestIfBest), the
      "beaten with this loadout" records that loadout quests (crowns) check, the "new personal best" banner, Steam
      leaderboard uploads (classic levels and Eternal Trials), the Eternal Trials best score, and Steam achievements
      unlocked from inside a level.
    - Blocked by default, optional: counting the level as beaten. Beating a level unlocks the next level and weapon
      rewards, completes the "beat the level" quest, and later grants that level's Steam achievement, all derived from
      one saved flag, so an unranked win doesn't count unless "Count unranked wins" is on.
    - Kept: XP and the meta level. The game grants XP for defeats too, so it's account progress rather than a record,
      and taking it away would make casual use of mod features punishing.

    The record of an unranked run survives quitting and resuming (see GameplayImpact). Achievements that the game
    re-checks on the map (crowns, levels beaten) are only held back while in a level, so legitimate ones still unlock.
*/
namespace PalaceMod
{
    public class HighscoreGuardManager : BaseManager
    {
        public override string FeatureSection => "Unranked runs";
        public override string FeatureDescription =>
            "Runs that use gameplay-changing mod features (cheats, undo, save states...) don't record high scores, " +
            "leaderboard entries, quests or achievements. To keep every run ranked, turn off General > Gameplay-changing features.";
        protected override bool UnpatchWhenDisabled => false;

        private static ConfigEntry<bool> countWins;
        private static ConfigEntry<bool> showBadge;

        // Level data whose current-run score belongs to an unranked run, until the game commits (and resets) it.
        private static readonly HashSet<LevelData> unrankedLevelData = [];
        private static readonly FieldInfo personalRecordField =
            AccessTools.Field(typeof(SceneTransitionManager), "totalScoreFromLastMatchIsNewPersonalRecord");

        private static bool Blocking => Feature<HighscoreGuardManager>.On && GameplayImpact.Any;
        private static LevelData ActiveLevelData => LevelProgressManager.instance?.GetLevelDataForActiveScene();
        private static bool InLevel => ActiveLevelData != null;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            countWins = plugin.Config.Bind(FeatureSection, "Count unranked wins", false, new ConfigDescription(
                "Still mark the level beaten after an unranked win: unlocks the next level and rewards, and counts for the " +
                "\"beat the level\" quest and achievement."));
            showBadge = plugin.Config.Bind(FeatureSection, "Show badge", true, new ConfigDescription(
                "Show an \"Unranked\" badge in the pause menu and on the end screen; hover it for the reasons."));
            showBadge.SettingChanged += (_, _) => RefreshBadges();

            GameplayImpact.Marked += OnMarked;
            GameplayImpact.Reset += RefreshBadges;

            DevCommands.Register("unranked", "unranked [new|continue <scene> | save | score <n> | win | lose | hover | unhover]   unranked state and level bests; drive a run like the level select does", args =>
            {
                string action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                switch (action)
                {
                    case "":
                        break;
                    case "new":
                    case "continue":
                        if (args.Length < 2) throw new System.ArgumentException("usage: unranked new|continue <scene>");
                        if (SceneTransitionManager.instance == null || SceneTransitionManager.instance.SceneTransitionIsRunning)
                            throw new System.InvalidOperationException("the game isn't ready for a scene change");
                        MatchSaveLoadHandler.TryLoadRun(args[1]);
                        MatchSaveLoadHandler.OverwriteCurrentSave = action == "new";
                        LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
                        SceneTransitionManager.instance.TransitionFromNullToLevel(args[1]);
                        return "";
                    case "save":
                        AccessTools.Method(typeof(LocalMatchSaveLoad), "Save").Invoke(Object.FindObjectOfType<LocalMatchSaveLoad>() ??
                            throw new System.InvalidOperationException("not in a level"), null);
                        return "saved: " + Describe();
                    case "score":
                        if (ScoreManager.Instance == null || ActiveLevelData == null || args.Length < 2)
                            throw new System.ArgumentException("usage: unranked score <n> (in a level)");
                        // A made-up score must never reach real records or Steam.
                        GameplayImpact.Mark("Dev command: set score");
                        AccessTools.Field(typeof(ScoreManager), "currentScore").SetValue(ScoreManager.Instance, int.Parse(args[1]));
                        ActiveLevelData.highscore = int.Parse(args[1]);
                        break;
                    case "hover":
                        var badge = Feature<HighscoreGuardManager>.Instance?.badges.FirstOrDefault(b => b != null && b.isActiveAndEnabled)
                            ?? throw new System.InvalidOperationException("no badge showing");
                        badge.ShowTooltip();
                        return "tooltip shown";
                    case "unhover":
                        foreach (var b in Feature<HighscoreGuardManager>.Instance?.badges ?? []) if (b != null) b.HideTooltip();
                        return "";
                    case "win":
                    case "lose":
                        if (LocalGamestate.Instance == null) throw new System.InvalidOperationException("not in a level");
                        // A forced win isn't earned; a forced defeat records only the score actually played.
                        if (action == "win") GameplayImpact.Mark("Dev command: forced win");
                        LocalGamestate.Instance.SetState(action == "win" ? LocalGamestate.State.AfterMatchVictory : LocalGamestate.State.AfterMatchDefeat);
                        break;
                    default:
                        throw new System.ArgumentException("unknown subcommand");
                }
                return Describe();
            });
        }

        private static string Describe()
        {
            string reasons = GameplayImpact.Any ? string.Join(", ", GameplayImpact.Reasons) : "none";
            string text = $"blocking={Blocking} reasons={reasons}";
            var data = ActiveLevelData ?? SceneTransitionManager.instance?.LevelDataFromLastMatch;
            if (data != null) text += $" | level: highscore={data.highscore} best={data.highscoreBest} beatenBest={data.beatenBest} beatenWith={data.levelHasBeenBeatenWith.Count}";
            if (LevelProgressManager.instance != null) text += $" | ET best={LevelProgressManager.instance.EternalTrialsHighscore}";
            return text;
        }

        private void OnMarked(string reason)
        {
            var data = ActiveLevelData;
            if (data != null) unrankedLevelData.Add(data);
            if (!IsEnabled) return;
            // The level Studio's sessions are never ranked play: no notice there (the badge still shows).
            if (GameplayImpact.Reasons.Count == 1 && !Builder.Studio.Play.StudioPlay.InStudioSession)
                Notify.Show($"Unranked run ({reason}): high scores and achievements won't be saved.", NotifyKind.Warning, 6f, "unranked");
            RefreshBadges();
        }

        protected override void OnFeatureDisabled() => RefreshBadges();
        protected override void OnFeatureEnabled() => RefreshBadges();

        public override void OnDestroy()
        {
            GameplayImpact.Marked -= OnMarked;
            GameplayImpact.Reset -= RefreshBadges;
            foreach (var badge in badges) if (badge != null) Object.Destroy(badge.gameObject);
            badges.Clear();
        }

        // ---------------- Blocking ----------------

        // Commits the run's score to the level's bests: at the end screen, and when the level is next entered (for a
        // run that was left unfinished).
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.SaveScoreAndStatsToBestIfBest))]
        private class SaveBest_Patch
        {
            private static bool Prefix(LevelData __instance, bool _endOfMatch)
            {
                bool unranked = unrankedLevelData.Remove(__instance);
                if (!unranked || !Feature<HighscoreGuardManager>.On || IsBuilderRecord(__instance)) return true;
                if (__instance.highscore > __instance.highscoreBest || (_endOfMatch && __instance.beaten))
                    Log.For<HighscoreGuardManager>().Info($"Not recording score {__instance.highscore} (best {__instance.highscoreBest}), beaten={__instance.beaten}");
                if (countWins.Value && __instance.beaten) __instance.beatenBest = true;
                // The rest of what the original does: reset the current-run fields.
                __instance.beaten = false;
                __instance.highscore = 0;
                __instance.dayToDayScore = [0];
                __instance.dayToDayNetworth = [];
                return false;
            }
        }

        // Entering a level whose saved run is unranked: its score from here on is unranked too. (Runs after the game
        // has committed whatever was left over from the last visit.)
        [HarmonyPatch(typeof(LocalGamestate), "Start")]
        private class LevelStart_Patch
        {
            private static void Postfix()
            {
                if (!GameplayImpact.Any) return;
                var data = ActiveLevelData;
                if (data != null) unrankedLevelData.Add(data);
            }
        }

        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromGameplayToEndScreen))]
        private class EndScreen_Patch
        {
            private static void Postfix(SceneTransitionManager __instance)
            {
                if (!GameplayImpact.Any) return;
                if (__instance.LevelDataFromLastMatch != null) unrankedLevelData.Add(__instance.LevelDataFromLastMatch);
                if (Feature<HighscoreGuardManager>.On && !IsBuilderRecord(__instance.LevelDataFromLastMatch))
                    personalRecordField?.SetValue(__instance, false);
            }
        }

        // A Builder custom session keeps its own isolated score record, apart from the vanilla level's; its bests are
        // its own business. Leaderboards, achievements and vanilla records stay blocked.
        private static bool IsBuilderRecord(LevelData data) =>
            data != null && Builder.BuilderServices.Sessions?.Current?.Scores == data;

        // The "you unlocked ..." popup for a first win; with the win not counted, nothing is unlocked.
        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), "ShowCampaignLevelBeatenRewards")]
        private class BeatenRewards_Patch
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                if (!Blocking || countWins.Value) return true;
                __result = Enumerable.Empty<object>().GetEnumerator();
                return false;
            }
        }

        [HarmonyPatch]
        private class Leaderboard_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.AllTypes()
                    .Where(t => typeof(IGameServices).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .Select(t => AccessTools.Method(t, nameof(IGameServices.UploadHighscore), [typeof(int), typeof(string)]))
                    .Where(m => m != null && m.DeclaringType != null && !m.DeclaringType.IsInterface)
                    .Cast<MethodBase>()
                    .Distinct();

            private static bool Prefix(int score, string leaderboardTableName)
            {
                if (!Blocking)
                {
                    Log.For<HighscoreGuardManager>().Info($"Ranked run: uploading {score} to leaderboard \"{leaderboardTableName}\"");
                    return true;
                }
                Log.For<HighscoreGuardManager>().Info($"Not uploading {score} to leaderboard \"{leaderboardTableName}\"");
                return false;
            }
        }

        // The Eternal Trials defeat screen decides whether to celebrate a new best by comparing the run's score with the
        // stored best, then stores the higher one. Between the screen starting and that store, an unranked run reads
        // the best as at least its own score, so there's no banner; the store itself is then refused.
        private static bool eternalTrialsDefeatPending;

        [HarmonyPatch(typeof(EternalTrialsDefeatUI), nameof(EternalTrialsDefeatUI.PlayAnimation))]
        private class EternalTrialsDefeat_Patch
        {
            private static void Prefix() => eternalTrialsDefeatPending = Blocking;
        }

        [HarmonyPatch(typeof(LevelProgressManager), nameof(LevelProgressManager.EternalTrialsHighscore), MethodType.Getter)]
        private class EternalTrialsBestGet_Patch
        {
            private static void Postfix(ref int __result)
            {
                if (eternalTrialsDefeatPending && Blocking) __result = Mathf.Max(__result, EternalTrialsRunManager.CurrentRun.OverallScore);
            }
        }

        [HarmonyPatch(typeof(LevelProgressManager), nameof(LevelProgressManager.EternalTrialsHighscore), MethodType.Setter)]
        private class EternalTrialsBest_Patch
        {
            private static void Prefix(LevelProgressManager __instance, ref int value)
            {
                eternalTrialsDefeatPending = false;
                if (!Blocking || !InLevel || value <= __instance.EternalTrialsHighscore) return;
                Log.For<HighscoreGuardManager>().Info($"Not recording Eternal Trials score {value}");
                value = __instance.EternalTrialsHighscore;
            }
        }

        [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.UnlockAchievement))]
        private class Achievement_Patch
        {
            private static bool Prefix(AchievementManager.Achievements _achievement)
            {
                if (!Blocking || !InLevel) return true;
                Log.For<HighscoreGuardManager>().Info($"Holding back achievement {_achievement}");
                return false;
            }
        }

        // ---------------- Badge ----------------

        private readonly List<Badge> badges = [];

        [HarmonyPatch(typeof(PauseUILoadoutHelper), "OnEnable")]
        private class Pause_Patch
        {
            private static void Postfix(PauseUILoadoutHelper __instance) =>
                Feature<HighscoreGuardManager>.Instance?.AttachBadge(__instance.transform);
        }

        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), nameof(EndOfMatchScoreUIHelper.OnActivate))]
        private class EndOfMatch_Patch
        {
            private static void Postfix(EndOfMatchScoreUIHelper __instance) =>
                Feature<HighscoreGuardManager>.Instance?.AttachBadge(__instance.transform);
        }

        private void AttachBadge(Transform parent)
        {
            badges.RemoveAll(b => b == null);
            var badge = badges.FirstOrDefault(b => b.transform.parent == parent);
            if (badge == null)
            {
                try
                {
                    badge = Badge.Create(parent);
                }
                catch (System.Exception e)
                {
                    Log.For<HighscoreGuardManager>().Warn($"Couldn't build the badge: {e.Message}");
                    return;
                }
                badges.Add(badge);
            }
            badge.Refresh();
        }

        private void RefreshBadges()
        {
            badges.RemoveAll(b => b == null);
            foreach (var badge in badges) badge.Refresh();
        }

        private static string TooltipBody() =>
            "Scores, quests and achievements from this run won't be recorded.\nMod features used:\n" +
            string.Join("\n", GameplayImpact.Reasons.Select(r => "• " + r));

        // A small panel in the corner ("◆ UNRANKED") cut from the game's tooltip panel, with the mod's edge and emblem;
        // hovering it shows the reasons in the shared mod tooltip.
        private class Badge : MonoBehaviour
        {
            public static Badge Create(Transform parent)
            {
                var source = GameWidgets.PauseTooltip?.selectionTitle?.GetComponentInParent<MPUIKIT.MPImage>(true)
                    ?? throw new System.InvalidOperationException("no tooltip panel to clone");
                var obj = GameWidgets.Clone(source, parent, "PalaceMod Unranked Badge", keepChildren: false);
                foreach (var c in obj.GetComponents<Component>())
                {
                    if (c is not (RectTransform or CanvasRenderer or MPUIKIT.MPImage)) DestroyImmediate(c);
                }
                ModStyle.MarkPanel(obj.GetComponent<MPUIKIT.MPImage>());
                obj.GetComponent<MPUIKIT.MPImage>().raycastTarget = false;

                var rect = (RectTransform)obj.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(48f, -40f);

                var layout = obj.AddComponent<HorizontalLayoutGroup>();
                layout.padding = new RectOffset(38, 20, 8, 8);
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = layout.childForceExpandHeight = false;
                var fitter = obj.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var label = GameWidgets.Text(obj.transform, "Label", "UNRANKED", ModStyle.BoldFont, 22f, ModStyle.Gold,
                    TextAlignmentOptions.MidlineLeft);
                label.characterSpacing = 30f;
                label.enableWordWrapping = false;

                var emblem = ModStyle.AddEmblem(obj.transform, 12f, ModStyle.KindColor(NotifyKind.Warning));
                emblem.anchorMin = emblem.anchorMax = new Vector2(0f, 0.5f);
                emblem.anchoredPosition = new Vector2(20f, 0f);

                ModTooltip.AttachHover(obj, "Unranked run", TooltipBody);
                return obj.AddComponent<Badge>();
            }

            public void Refresh() => gameObject.SetActive(Blocking && showBadge.Value);

            public void ShowTooltip() => ModTooltip.Show("Unranked run", TooltipBody(), (RectTransform)transform, this);

            public void HideTooltip() => ModTooltip.Hide(this);
        }
    }
}
