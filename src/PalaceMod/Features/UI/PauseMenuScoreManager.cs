using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using I2.Loc;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Pause menu score: the score so far, and a breakdown in the game's own night-score panel (cloned from NightscoreUI):
    by day the last night's subscores, at night what tonight would score if it ended now. On the last night it also
    projects the end screen's total. On the end-of-match screen after a victory it shows the final night's breakdown,
    which the game never shows (NightscoreUI.ShowNightScore skips the last wave because the end screen takes over).

    Layout: everything lives in one container, "PalaceMod Score", under the pause frame, anchored to the frame's
    top-right corner (above the loadout). The enemy preview (bottom-left area) is another feature's.
    Numbers come from ScoreCalculator, which mirrors the game's formulas.
    Tooltips use a minimal local helper (ScoreTooltip); migrate to the shared ui-kit tooltip once it lands on main.
*/
namespace PalaceMod
{
    public class PauseMenuScoreManager : BaseManager
    {
        public override string FeatureSection => "Pause menu score";
        public override string FeatureDescription => "Pause menu shows the score so far, last night's breakdown, and at night what the night would score if it ended now. After a victory, the end screen shows the final night's breakdown.";

        private BepInEx.Configuration.ConfigEntry<bool> showOnEndScreen;

        private GameObject pausePanel;
        private NightPanel pauseNight;
        private TextMeshProUGUI pauseHeader, pauseNightLabel, pauseFooter;
        private GameObject endPanel;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            showOnEndScreen = plugin.Config.Bind(FeatureSection, "Final night on end screen", true,
                "After a victory, show the final night's score breakdown on the end screen (the game skips it).");
            showOnEndScreen.SettingChanged += (_, _) => { if (!showOnEndScreen.Value) DestroyEndPanel(); };
            RegisterDevCommands();
        }

        private CanvasGroup pauseGroup;
        private PerkSelectionTooltipHelper nativeTooltip;
        private static readonly Vector3[] cornersA = new Vector3[4], cornersB = new Vector3[4];

        public override void Update()
        {
            ScoreCalculator.EnsureRecording();
            if (pausePanel != null && pausePanel.activeInHierarchy) AvoidNativeTooltip();
        }

        /// <summary>The pause menu's own selection tooltip (enemies, units, loadout) can open over the top-right corner; step aside while it's there.</summary>
        private void AvoidNativeTooltip()
        {
            bool overlaps = false;
            var bg = nativeTooltip != null ? nativeTooltip.background : null;
            if (bg != null && bg.isActiveAndEnabled && (nativeTooltip.tooltipParent == null || nativeTooltip.tooltipParent.activeInHierarchy))
            {
                bg.rectTransform.GetWorldCorners(cornersA);
                ((RectTransform)pausePanel.transform).GetWorldCorners(cornersB);
                overlaps = cornersA[0].x < cornersB[2].x && cornersB[0].x < cornersA[2].x && cornersA[0].y < cornersB[2].y && cornersB[0].y < cornersA[2].y;
            }
            float target = overlaps ? 0f : 1f;
            pauseGroup.alpha = Mathf.MoveTowards(pauseGroup.alpha, target, Time.unscaledDeltaTime * 8f);
            if (overlaps) ScoreTooltip.Hide();
        }

        protected override void OnFeatureDisabled()
        {
            DestroyPausePanel();
            DestroyEndPanel();
        }

        public override void OnDestroy() => OnFeatureDisabled();

        private void DestroyPausePanel()
        {
            if (pausePanel != null) Object.Destroy(pausePanel);
            pausePanel = null;
            pauseNight = null;
            ScoreTooltip.Hide();
        }

        private void DestroyEndPanel()
        {
            if (endPanel != null) Object.Destroy(endPanel);
            endPanel = null;
        }

        // ---- Pause menu -----------------------------------------------------------------------------------------

        // Refresh is wired to the frame's activation in the prefab; OnEnable covers it in case it isn't.
        [HarmonyPatch(typeof(PauseUILoadoutHelper), "OnEnable")]
        private static class PauseEnablePatch
        {
            private static void Postfix(PauseUILoadoutHelper __instance) => PauseRefreshPatch.Postfix(__instance);
        }

        [HarmonyPatch(typeof(PauseUILoadoutHelper), nameof(PauseUILoadoutHelper.Refresh))]
        private static class PauseRefreshPatch
        {
            internal static void Postfix(PauseUILoadoutHelper __instance)
            {
                if (!Feature<PauseMenuScoreManager>.On) return;
                Feature<PauseMenuScoreManager>.Instance.RefreshPause(__instance);
            }
        }

        private void RefreshPause(PauseUILoadoutHelper helper)
        {
            ScoreCalculator.EnsureRecording();
            if (!ScoreCalculator.Available || NightscoreUI.instance == null)
            {
                if (pausePanel != null) pausePanel.SetActive(false);
                return;
            }
            if (pausePanel == null || pausePanel.transform.parent != helper.transform) BuildPause(helper);
            pausePanel.SetActive(true);

            int score = ScoreCalculator.CurrentScore;
            pauseHeader.text = $"{Term("Menu/Score", "Score")}: <style=Body Numerals>{score}</style>";

            var footer = new StringBuilder();
            var footerTip = new StringBuilder();
            if (ScoreCalculator.IsNight)
            {
                var t = ScoreCalculator.ProjectTonight();
                pauseNightLabel.text = "If the night ended now";
                pauseNight.Root.SetActive(true);
                pauseNight.Show(t, score + t.Total, ExplainBase(), ExplainProtection(t), ExplainTime(t), timeSuffix: TimeSuffix(t));
                if (ScoreCalculator.IsFinalNight)
                {
                    var v = ScoreCalculator.Victory(score + t.Total);
                    footer.Append($"Victory total: <style=Body Numerals>{v.Total}</style>");
                    footerTip.Append(ExplainVictory(v));
                }
            }
            else if (ScoreCalculator.LastNight is NightScore last)
            {
                pauseNightLabel.text = $"{Term("Menu/Night", "Night")} {last.Night}";
                pauseNight.Root.SetActive(true);
                pauseNight.Show(last, score, ExplainBase(), ExplainProtection(last), ExplainTime(last), timeSuffix: TimeSuffix(last));
            }
            else
            {
                pauseNightLabel.text = "";
                pauseNight.Root.SetActive(false);
            }

            if (footer.Length == 0)
            {
                var mutators = ScoreCalculator.Mutators();
                float m = ScoreCalculator.MutatorMultiplier();
                if (mutators.Count > 0 && !ScoreCalculator.IsEternalTrial)
                {
                    footer.Append($"Mutators: <style=Body Numerals>+{Mathf.RoundToInt((m - 1f) * 100f)}%</style> on victory");
                    footerTip.Append("Added to your score and gold bonus when you win:");
                    foreach (var (name, mult) in mutators)
                        footerTip.Append($"\n{name}: +{Mathf.RoundToInt((mult - 1f) * 100f)}%");
                }
            }
            pauseFooter.text = footer.ToString();
            pauseFooter.gameObject.SetActive(footer.Length > 0);
            ScoreTooltip.Attach(pauseFooter, footerTip.ToString());
        }

        private void BuildPause(PauseUILoadoutHelper helper)
        {
            DestroyPausePanel();
            pausePanel = new GameObject("PalaceMod Score", typeof(RectTransform));
            var rect = (RectTransform)pausePanel.transform;
            rect.SetParent(helper.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-45f, -30f); // the dawn score panel's corner
            var layout = pausePanel.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperRight;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            layout.spacing = 6f;
            var fitter = pausePanel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            pauseGroup = pausePanel.AddComponent<CanvasGroup>();
            nativeTooltip = helper.GetComponent<PerkSelectionTooltipHelper>();

            pauseHeader = CloneLabel(helper.nightPreviewText, pausePanel.transform, "Score", 1f);
            pauseNightLabel = CloneLabel(helper.nightPreviewText, pausePanel.transform, "Night label", 0.75f);
            pauseNight = NightPanel.Clone(pausePanel.transform, "Night breakdown");
            pauseFooter = CloneLabel(helper.nightPreviewText, pausePanel.transform, "Footer", 0.75f);
        }

        /// <summary>A text styled like the pause menu's "Night: 3/12" line.</summary>
        private static TextMeshProUGUI CloneLabel(TextMeshProUGUI source, Transform parent, string name, float scale)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            var text = obj.GetComponent<TextMeshProUGUI>();
            text.font = source.font;
            text.fontSharedMaterial = source.fontSharedMaterial;
            text.fontSize = source.fontSize * scale;
            text.fontStyle = source.fontStyle;
            text.color = source.color;
            text.characterSpacing = source.characterSpacing;
            text.styleSheet = source.styleSheet;
            text.alignment = TextAlignmentOptions.Right;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            return text;
        }

        // ---- End-of-match screen --------------------------------------------------------------------------------

        [HarmonyPatch(typeof(EndOfMatchScoreUIHelper), nameof(EndOfMatchScoreUIHelper.OnActivate))]
        private static class EndScreenPatch
        {
            private static void Postfix(EndOfMatchScoreUIHelper __instance)
            {
                if (!Feature<PauseMenuScoreManager>.On) return;
                Feature<PauseMenuScoreManager>.Instance.ShowFinalNight(__instance);
            }
        }

        private void ShowFinalNight(EndOfMatchScoreUIHelper helper)
        {
            DestroyEndPanel();
            if (!showOnEndScreen.Value || NightscoreUI.instance == null) return;
            if (LocalGamestate.Instance == null || LocalGamestate.Instance.CurrentState != LocalGamestate.State.AfterMatchVictory) return;
            // Eternal Trials victories use their own screen (mainFrameParent stays hidden), which already summarises the run.
            if (ScoreCalculator.IsEternalTrial) return;
            if (ScoreCalculator.LastNight is not NightScore last || last.Night < ScoreCalculator.NightCount) return;

            // A child of the main frame, so it fades in with the score list.
            endPanel = new GameObject("PalaceMod Final Night", typeof(RectTransform));
            var rect = (RectTransform)endPanel.transform;
            rect.SetParent(helper.mainFrameParent.transform, false);
            // Left of the 600-wide centre column, level with the score list.
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-340f, -330f);
            var layout = endPanel.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperRight;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            layout.spacing = 6f;
            var endFitter = endPanel.AddComponent<ContentSizeFitter>();
            endFitter.horizontalFit = endFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var label = CloneLabel(helper.baseScoreName, endPanel.transform, "Label", 1f);
            label.text = $"{Term("Menu/Night", "Night")} {last.Night}";
            var panel = NightPanel.Clone(endPanel.transform, "Night breakdown");
            panel.Show(last, ScoreCalculator.CurrentScore, ExplainBase(), ExplainProtection(last), ExplainTime(last), TimeSuffix(last));
        }

        // ---- Text -----------------------------------------------------------------------------------------------

        private static string Term(string term, string fallback)
        {
            string s = LocalizationManager.GetTranslation(term);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static string TimeSuffix(NightScore n) => n.SecondsAfterSpawning < 0f ? "" : $" ({Mathf.FloorToInt(n.SecondsAfterSpawning)}s)";

        private static string ExplainBase() => $"Every night survived scores {ScoreManager.Instance.baseScorePerNight}.";

        private static string ExplainProtection(NightScore n)
        {
            var sm = ScoreManager.Instance;
            string counts = n.Buildings >= 0 ? $"{n.BuildingsKnockedOut} of {n.Buildings} buildings knocked out.\n" : "";
            return $"{counts}Up to {sm.protectionScorePerNight}, scaled by the share of buildings left standing, squared.";
        }

        private static string ExplainTime(NightScore n)
        {
            float full = ScoreCalculator.TimeBonusFullUntil, gone = ScoreCalculator.TimeBonusGoneAfter;
            string when = n.SecondsAfterSpawning < 0f ? ""
                : EnemySpawner.instance != null && EnemySpawner.instance.SpawningInProgress && n.Night == ScoreCalculator.NightNumber && ScoreCalculator.IsNight
                    ? "Enemies are still arriving.\n"
                    : $"Night ended {Mathf.FloorToInt(n.SecondsAfterSpawning)}s after the last enemy arrived.\n";
            return $"{when}Full {ScoreManager.Instance.timeScorePerNight} if you clear the night within {full:0}s of the last enemy arriving, falling to 0 at {gone:0}s.";
        }

        private static string ExplainVictory(MatchEndScore v)
        {
            var sb = new StringBuilder("If you win now:");
            sb.Append($"\nScore: {v.Score}");
            sb.Append($"\nGold bonus: +{v.Gold}");
            if (v.Mutators > 0) sb.Append($"\nMutator bonus: +{v.Mutators}");
            if (v.NoRetry > 0) sb.Append($"\nNo retries bonus: +{v.NoRetry}");
            return sb.ToString();
        }

        // ---- The cloned native night-score panel ----------------------------------------------------------------

        /// <summary>A copy of the game's dawn score panel (NightscoreUI), static and fully expanded.</summary>
        private sealed class NightPanel
        {
            public GameObject Root;
            private TextMeshProUGUI baseScore, timeBonus, protectionPercentage, protectionScore, overallScore, timeLabel, baseLabel;
            private string timeLabelText;

            public static NightPanel Clone(Transform parent, string name)
            {
                var ui = NightscoreUI.instance;
                var source = ui.nightScorePanel.transform;
                var root = Object.Instantiate(ui.nightScorePanel, parent, false);
                root.name = name;
                foreach (var behaviour in root.GetComponentsInChildren<NightscoreUI>(true)) Object.Destroy(behaviour);
                foreach (var audio in root.GetComponentsInChildren<AudioSource>(true)) Object.Destroy(audio);

                T Find<T>(Component c) where T : Component
                {
                    if (c == null) return null;
                    string path = RelativePath(source, c.transform);
                    var t = path == null ? null : (path.Length == 0 ? root.transform : root.transform.Find(path));
                    return t != null ? t.GetComponent<T>() : null;
                }

                var p = new NightPanel
                {
                    Root = root,
                    baseScore = Find<TextMeshProUGUI>(ui.baseScore),
                    timeBonus = Find<TextMeshProUGUI>(ui.timeBonus),
                    protectionPercentage = Find<TextMeshProUGUI>(ui.protectionPercentage),
                    protectionScore = Find<TextMeshProUGUI>(ui.protectionScore),
                    overallScore = Find<TextMeshProUGUI>(ui.overallScore),
                    timeLabel = Find<TextMeshProUGUI>(ui.timeText),
                    baseLabel = Find<TextMeshProUGUI>(ui.nightSurviveText),
                };

                // Undo the pop-up animation's start state: every row scaled to zero and the content collapsed.
                foreach (var rt in new[] { ui.nightSurviveText, ui.nightSurviveNumber, ui.timeText, ui.timeNumber, ui.protectionText,
                             ui.protectionNumber, ui.overallScoreBG, ui.overallScoreNumber })
                {
                    var c = Find<RectTransform>(rt);
                    if (c != null) c.localScale = Vector3.one;
                }
                // The panel stretches over the HUD with its content pinned 45 px into the top-right corner; pin it to our
                // corner instead and size the root to the content plus the total bubble hanging below it.
                var content = Find<RectTransform>(ui.content);
                float width = 560f;
                if (content != null)
                {
                    content.sizeDelta = new Vector2(content.sizeDelta.x, ExpandedContentHeight);
                    content.anchoredPosition = Vector2.zero;
                    width = content.sizeDelta.x;
                }
                var bg = Find<MPUIKIT.MPImage>(ui.backgroundImage);
                if (bg != null) bg.enabled = true;

                // Labels: localise once, then let us append to them.
                if (p.timeLabel != null)
                {
                    var loc = p.timeLabel.GetComponent<Localize>();
                    if (loc != null) { loc.OnLocalize(true); Object.Destroy(loc); }
                    p.timeLabelText = p.timeLabel.text;
                }

                var element = root.GetComponent<LayoutElement>() ?? root.AddComponent<LayoutElement>();
                var bubble = Find<RectTransform>(ui.overallScoreBG);
                float below = bubble != null ? -bubble.anchoredPosition.y + bubble.sizeDelta.y : 70f;
                element.preferredWidth = width;
                element.preferredHeight = ExpandedContentHeight + below;
                if (p.timeLabel != null) p.timeLabel.name = "Time Text"; // last: Find() looks children up by their original names
                root.SetActive(true);
                return p;
            }

            /// <summary>NightscoreUI grows the content by 55 + 30 + 30 while popping the rows in.</summary>
            private const float ExpandedContentHeight = 115f;

            public void Show(NightScore n, int overall, string baseTip, string protectionTip, string timeTip, string timeSuffix)
            {
                if (baseScore != null) baseScore.text = "+" + n.Base;
                if (timeBonus != null) timeBonus.text = "+" + n.Time;
                if (protectionScore != null) protectionScore.text = "+" + n.Protection;
                if (protectionPercentage != null)
                    protectionPercentage.text = $"{Term("Menu/Realm", "Realm")} {n.ProtectionPercent}% {Term("Menu/Protected", "Protected")}";
                if (overallScore != null) overallScore.text = overall.ToString();
                if (timeLabel != null) timeLabel.text = timeLabelText + timeSuffix;
                ScoreTooltip.Attach(baseLabel, baseTip);
                ScoreTooltip.Attach(protectionPercentage, protectionTip);
                ScoreTooltip.Attach(timeLabel, timeTip);
            }

            private static string RelativePath(Transform root, Transform t)
            {
                var parts = new List<string>();
                for (var cur = t; cur != null; cur = cur.parent)
                {
                    if (cur == root) { parts.Reverse(); return string.Join("/", parts); }
                    parts.Add(cur.name);
                }
                return null;
            }
        }

        // ---- Minimal hover tooltip (migrate to the shared ui-kit tooltip when it lands) --------------------------

        private sealed class ScoreTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private string text;
            private static GameObject shown;
            private static TextMeshProUGUI shownText;

            public static void Attach(TextMeshProUGUI target, string text)
            {
                if (target == null) return;
                var tip = target.GetComponent<ScoreTooltip>() ?? target.gameObject.AddComponent<ScoreTooltip>();
                tip.text = text;
                target.raycastTarget = !string.IsNullOrEmpty(text);
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (string.IsNullOrEmpty(text)) return;
                var label = GetComponent<TextMeshProUGUI>();
                var canvas = GetComponentInParent<Canvas>()?.rootCanvas;
                if (canvas == null) return;
                if (shown == null || shown.transform.parent != canvas.transform) Create(canvas.transform, label);
                shownText.text = text;
                shown.SetActive(true);
                shown.transform.SetAsLastSibling();
                // Just below the hovered label, right-aligned with it.
                var rect = (RectTransform)shown.transform;
                var corners = new Vector3[4];
                ((RectTransform)transform).GetWorldCorners(corners);
                rect.position = corners[0];
                rect.pivot = new Vector2(0f, 1f);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                var canvasRect = (RectTransform)canvas.transform;
                // Keep it on screen: flip to the label's right edge if it would overflow.
                var tipCorners = new Vector3[4];
                rect.GetWorldCorners(tipCorners);
                var canvasCorners = new Vector3[4];
                canvasRect.GetWorldCorners(canvasCorners);
                if (tipCorners[2].x > canvasCorners[2].x)
                {
                    rect.pivot = new Vector2(1f, 1f);
                    rect.position = corners[3];
                }
            }

            public void OnPointerExit(PointerEventData eventData) => Hide();

            private void OnDisable() => Hide();

            public static void Hide()
            {
                if (shown != null) shown.SetActive(false);
            }

            private static void Create(Transform canvas, TextMeshProUGUI style)
            {
                if (shown != null) Object.Destroy(shown);
                // The dawn score panel's rounded charcoal background, without its children.
                var source = NightscoreUI.instance != null ? NightscoreUI.instance.backgroundImage : null;
                if (source != null)
                {
                    shown = Object.Instantiate(source.gameObject, canvas, false);
                    foreach (Transform child in shown.transform) Object.Destroy(child.gameObject);
                    source = shown.GetComponent<MPUIKIT.MPImage>();
                    source.raycastTarget = false;
                    source.enabled = true;
                }
                else
                {
                    shown = new GameObject("bg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    shown.transform.SetParent(canvas, false);
                    var image = shown.GetComponent<Image>();
                    image.color = new Color(0.102f, 0.110f, 0.114f); // the game's panel charcoal
                    image.raycastTarget = false;
                }
                shown.name = "PalaceMod Score Tooltip";
                var shownRect = (RectTransform)shown.transform;
                shownRect.anchorMin = shownRect.anchorMax = new Vector2(0.5f, 0.5f);
                var layout = shown.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(22, 22, 14, 14);
                layout.childControlWidth = layout.childControlHeight = true;
                var fitter = shown.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var textObj = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(LayoutElement));
                textObj.transform.SetParent(shown.transform, false);
                shownText = textObj.GetComponent<TextMeshProUGUI>();
                shownText.font = TMP_Settings.defaultFontAsset;
                var body = Resources.Load<TMP_FontAsset>("fonts & materials/CormorantGaramond-Light SDF");
                body ??= Array.Find(Resources.FindObjectsOfTypeAll<TMP_FontAsset>(), f => f.name == "CormorantGaramond-Light SDF");
                if (body != null) shownText.font = body;
                shownText.fontSize = 24f;
                shownText.color = style != null ? style.color : new Color(0.784f, 0.651f, 0.455f);
                shownText.raycastTarget = false;
                shownText.enableWordWrapping = true;
                textObj.GetComponent<LayoutElement>().preferredWidth = 420f;
                shown.SetActive(false);
            }
        }

        // ---- Dev commands ---------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("score", "score [wave <n>|mutator|killall|dump <pause|nightscore|end>|shownight|hover <score|time|protection|base|footer>]   score numbers and test helpers", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                switch (sub)
                {
                    case "":
                        return Describe();
                    case "wave":
                    {
                        if (!ScoreCalculator.Available) throw new InvalidOperationException("not in a level");
                        if (ScoreCalculator.IsNight) throw new InvalidOperationException("only by day");
                        int n = int.Parse(args[1]);
                        int count = ScoreCalculator.NightCount;
                        if (n < 1 || n > count) throw new ArgumentException($"night must be 1..{count}");
                        AccessTools.Field(typeof(EnemySpawner), "wavenumber").SetValue(EnemySpawner.instance, n - 2);
                        GameplayImpact.Mark("Dev: skipped nights");
                        return $"next night is {n}/{count}";
                    }
                    case "mutator":
                    {
                        // Equips one more mutator (score multiplier only; its gameplay effect isn't applied mid-match).
                        var equipped = PerkManager.instance.CurrentlyEquipped;
                        var m = Array.Find(Resources.FindObjectsOfTypeAll<EquippableMutation>(), x => !equipped.Contains(x))
                            ?? throw new InvalidOperationException("no unequipped mutator loaded");
                        equipped.Add(m);
                        GameplayImpact.Mark("Dev: added mutator");
                        return $"equipped {m.displayName} (x{m.scoreMultiplyerOnWin})";
                    }
                    case "killall":
                        Hp.KillAllEnemyUnits();
                        GameplayImpact.Mark("Dev: killed all enemies");
                        return "killed all enemies";
                    case "dump":
                        return Dump(args.Length > 1 ? args[1] : "pause");
                    case "shownight":
                    {
                        var last = ScoreCalculator.LastNight ?? throw new InvalidOperationException("no night scored yet");
                        NightscoreUI.instance.ShowNightScore(last.Base, last.Time, last.ProtectionFraction, last.Protection);
                        return "shown";
                    }
                    case "hover":
                    {
                        if (pausePanel == null) throw new InvalidOperationException("pause menu panel not built");
                        string which = args.Length > 1 ? args[1].ToLowerInvariant() : "time";
                        TextMeshProUGUI target = null;
                        foreach (var tip in pausePanel.GetComponentsInChildren<ScoreTooltip>(true))
                            if (tip.name.ToLowerInvariant().Contains(which)) target = tip.GetComponent<TextMeshProUGUI>();
                        if (target == null) throw new ArgumentException("no tooltip on " + which);
                        target.GetComponent<ScoreTooltip>().OnPointerEnter(null);
                        return "hovering " + target.name;
                    }
                    default:
                        throw new ArgumentException("unknown subcommand " + sub);
                }
            });
        }

        private static string Describe()
        {
            if (!ScoreCalculator.Available) return "not in a level";
            var sb = new StringBuilder();
            sb.Append($"score {ScoreCalculator.CurrentScore}, night {ScoreCalculator.NightNumber}/{ScoreCalculator.NightCount} {(ScoreCalculator.IsNight ? "(night)" : "(day)")}");
            if (ScoreCalculator.IsNight)
            {
                var t = ScoreCalculator.ProjectTonight();
                sb.Append($"\ntonight: base {t.Base} time {t.Time} ({t.SecondsAfterSpawning:0.0}s after spawning) protection {t.Protection} ({t.ProtectionPercent}%, {t.BuildingsKnockedOut}/{t.Buildings} down) = {t.Total}");
                if (ScoreCalculator.IsFinalNight)
                {
                    var v = ScoreCalculator.Victory(ScoreCalculator.CurrentScore + t.Total);
                    sb.Append($"\nvictory now: score {v.Score} gold {v.Gold} mutators {v.Mutators} noretry {v.NoRetry} = {v.Total}");
                }
            }
            foreach (var n in ScoreCalculator.Nights)
                sb.Append($"\nnight {n.Night}: base {n.Base} time {n.Time} ({n.SecondsAfterSpawning:0.0}s) protection {n.Protection} ({n.ProtectionPercent}%) = {n.Total}");
            sb.Append($"\nmutators x{ScoreCalculator.MutatorMultiplier():0.###}, no-retry x{ScoreCalculator.NoRetryMultiplier():0.###}");
            if (!ScoreCalculator.IsNight)
            {
                var v = ScoreCalculator.Victory(ScoreCalculator.CurrentScore);
                sb.Append($"\nvictory with current score and gold: score {v.Score} gold {v.Gold} mutators {v.Mutators} noretry {v.NoRetry} = {v.Total}");
                if (SceneTransitionManager.instance != null) sb.Append($"; game's last end screen total {SceneTransitionManager.instance.TotalScoreFromLastMatch}");
            }
            return sb.ToString();
        }

        private static string Dump(string what)
        {
            var fm = UIFrameManager.instance;
            Transform root = what switch
            {
                "pause" => ((UIFrame)AccessTools.Field(typeof(UIFrameManager), "inMatchPauseMenuFrame").GetValue(fm))?.transform,
                "end" => ((UIFrame)AccessTools.Field(typeof(UIFrameManager), "endOfMatchFrame").GetValue(fm))?.transform,
                "nightscore" => NightscoreUI.instance != null ? NightscoreUI.instance.transform : null,
                _ => throw new ArgumentException("pause, end or nightscore"),
            };
            if (root == null) throw new InvalidOperationException("not found");
            var sb = new StringBuilder();
            DumpRec(root, 0, sb);
            Plugin.Logger.LogInfo("UI dump " + what + ":\n" + sb);
            return sb.ToString();
        }

        private static void DumpRec(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 9) return;
            var rt = t as RectTransform;
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [off]");
            if (rt != null) sb.Append($" pos{rt.anchoredPosition} size{rt.sizeDelta} anchor{rt.anchorMin}-{rt.anchorMax} pivot{rt.pivot} scale{rt.localScale.x:0.##}");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c is Transform) continue;
                sb.Append(' ').Append(c.GetType().Name);
                if (c is TextMeshProUGUI tmp) sb.Append($"(\"{Trim(tmp.text)}\" {tmp.font?.name} {tmp.fontSize} {tmp.color} sp{tmp.characterSpacing} {(tmp.fontSharedMaterial != null ? tmp.fontSharedMaterial.name : "")})");
                if (c is Localize loc) sb.Append($"(term {loc.Term})");
                if (c is Image img) sb.Append($"({img.sprite?.name} {img.color})");
                if (c is MPUIKIT.MPImage mp) sb.Append($"({mp.color})");
            }
            sb.Append('\n');
            foreach (Transform child in t) DumpRec(child, depth + 1, sb);
        }

        private static string Trim(string s) => s == null ? "" : (s.Length > 40 ? s.Substring(0, 40) : s).Replace("\n", "\\n");
    }
}
