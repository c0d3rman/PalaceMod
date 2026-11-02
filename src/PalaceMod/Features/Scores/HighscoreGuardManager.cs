using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using Thronefall.GameServices;
using UnityEngine;
using UnityEngine.EventSystems;
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
        public override string FeatureSection => "Unranked Runs";
        public override string FeatureDescription =>
            "Runs that use gameplay-changing mod features (cheats, undo, save states...) don't record high scores, " +
            "leaderboard entries, quests or achievements.";
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

            DevCommands.Register("unranked", "unranked [new|continue <scene> | save | score <n> | win | lose]   unranked state and level bests; drive a run like the level select does", args =>
            {
                string action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                switch (action)
                {
                    case "":
                        break;
                    case "new":
                    case "continue":
                        if (args.Length < 2) throw new System.ArgumentException("usage: unranked new|continue <scene>");
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
                        if (ScoreManager.Instance == null || args.Length < 2) throw new System.ArgumentException("usage: unranked score <n> (in a level)");
                        AccessTools.Field(typeof(ScoreManager), "currentScore").SetValue(ScoreManager.Instance, int.Parse(args[1]));
                        ActiveLevelData.highscore = int.Parse(args[1]);
                        break;
                    case "win":
                    case "lose":
                        if (LocalGamestate.Instance == null) throw new System.InvalidOperationException("not in a level");
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
            if (GameplayImpact.Reasons.Count == 1)
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
                if (!unranked || !Feature<HighscoreGuardManager>.On) return true;
                if (__instance.highscore > __instance.highscoreBest || (_endOfMatch && __instance.beaten))
                    Plugin.Logger.LogInfo($"Unranked run: not recording score {__instance.highscore} (best {__instance.highscoreBest}), beaten={__instance.beaten}");
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
                if (Feature<HighscoreGuardManager>.On) personalRecordField?.SetValue(__instance, false);
            }
        }

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
                    Plugin.Logger.LogInfo($"Ranked run: uploading {score} to leaderboard \"{leaderboardTableName}\"");
                    return true;
                }
                Plugin.Logger.LogInfo($"Unranked run: not uploading {score} to leaderboard \"{leaderboardTableName}\"");
                return false;
            }
        }

        [HarmonyPatch(typeof(LevelProgressManager), nameof(LevelProgressManager.EternalTrialsHighscore), MethodType.Setter)]
        private class EternalTrialsBest_Patch
        {
            private static void Prefix(LevelProgressManager __instance, ref int value)
            {
                if (!Blocking || !InLevel || value <= __instance.EternalTrialsHighscore) return;
                Plugin.Logger.LogInfo($"Unranked run: not recording Eternal Trials score {value}");
                value = __instance.EternalTrialsHighscore;
            }
        }

        [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.UnlockAchievement))]
        private class Achievement_Patch
        {
            private static bool Prefix(AchievementManager.Achievements _achievement)
            {
                if (!Blocking || !InLevel) return true;
                Plugin.Logger.LogInfo($"Unranked run: holding back achievement {_achievement}");
                return false;
            }
        }

        // ---------------- Badge ----------------

        private static readonly Color32 Gold = new(200, 166, 116, 255);
        private static readonly Color32 Amber = new(232, 150, 70, 255);
        private static TMP_FontAsset titleFont;
        private readonly List<Badge> badges = [];

        [HarmonyPatch(typeof(PauseUILoadoutHelper), "OnEnable")]
        private class Pause_Patch
        {
            private static void Postfix(PauseUILoadoutHelper __instance) =>
                Feature<HighscoreGuardManager>.Instance?.AttachBadge(__instance.transform, new Vector2(50, -50));
        }

        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), nameof(EndOfMatchScoreUIHelper.OnActivate))]
        private class EndOfMatch_Patch
        {
            private static void Postfix(EndOfMatchScoreUIHelper __instance) =>
                Feature<HighscoreGuardManager>.Instance?.AttachBadge(__instance.transform, new Vector2(50, -50));
        }

        private void AttachBadge(Transform parent, Vector2 offset)
        {
            badges.RemoveAll(b => b == null);
            var badge = badges.FirstOrDefault(b => b.transform.parent == parent);
            if (badge == null)
            {
                badge = Badge.Create(parent, offset);
                badges.Add(badge);
            }
            badge.Refresh();
        }

        private void RefreshBadges()
        {
            badges.RemoveAll(b => b == null);
            foreach (var badge in badges) badge.Refresh();
        }

        private class Badge : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private GameObject tooltip;
            private TextMeshProUGUI tooltipText;

            public static Badge Create(Transform parent, Vector2 offset)
            {
                titleFont ??= Resources.Load<TMP_FontAsset>("fonts & materials/CormorantGaramond-SemiBold SDF");

                var go = new GameObject("PalaceMod Unranked Badge", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
                go.transform.SetParent(parent, false);
                var rect = (RectTransform)go.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = offset;

                var image = go.GetComponent<Image>();
                image.sprite = Resources.Load<Sprite>("ui/frame_round");
                image.type = Image.Type.Sliced;
                image.color = new Color(0, 0, 0, 0.75f);

                var layout = go.GetComponent<HorizontalLayoutGroup>();
                layout.padding = new RectOffset(18, 18, 6, 6);
                layout.childControlWidth = layout.childControlHeight = true;
                var fitter = go.GetComponent<ContentSizeFitter>();
                fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var label = MakeText(go.transform, "UNRANKED", 24, Amber);
                label.characterSpacing = 30;
                label.raycastTarget = false;

                var badge = go.AddComponent<Badge>();
                badge.BuildTooltip();
                return badge;
            }

            private static TextMeshProUGUI MakeText(Transform parent, string text, float size, Color color)
            {
                var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(parent, false);
                var tmp = go.GetComponent<TextMeshProUGUI>();
                if (titleFont != null) tmp.font = titleFont;
                tmp.text = text;
                tmp.fontSize = size;
                tmp.color = color;
                tmp.enableWordWrapping = false;
                return tmp;
            }

            private void BuildTooltip()
            {
                tooltip = new GameObject("Tooltip", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
                tooltip.transform.SetParent(transform, false);
                tooltip.GetComponent<LayoutElement>().ignoreLayout = true;
                var rect = (RectTransform)tooltip.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = new Vector2(0, -52);

                var image = tooltip.GetComponent<Image>();
                image.sprite = Resources.Load<Sprite>("ui/frame_round");
                image.type = Image.Type.Sliced;
                image.color = new Color(0, 0, 0, 0.9f);
                image.raycastTarget = false;

                var layout = tooltip.GetComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(18, 18, 12, 12);
                layout.childControlWidth = layout.childControlHeight = true;
                var fitter = tooltip.GetComponent<ContentSizeFitter>();
                fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                tooltipText = MakeText(tooltip.transform, "", 22, Gold);
                tooltipText.raycastTarget = false;
                tooltip.SetActive(false);
            }

            public void Refresh()
            {
                bool show = Blocking && showBadge.Value;
                gameObject.SetActive(show);
                if (!show) return;
                string reasons = string.Join("\n", GameplayImpact.Reasons.Select(r => "• " + r));
                tooltipText.text = "Scores, quests and achievements from this run\nwon't be recorded. Mod features used:\n" + reasons;
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                Refresh();
                tooltip.SetActive(true);
            }

            public void OnPointerExit(PointerEventData eventData) => tooltip.SetActive(false);

            private void OnDisable()
            {
                if (tooltip != null) tooltip.SetActive(false);
            }
        }
    }
}
