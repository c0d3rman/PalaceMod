using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

namespace PalaceMod
{
    public class PauseMenuScoreManager : BaseManager
    {
        public override string FeatureSection => "Pause menu score";
        public override string FeatureDescription => "Shows the current and potential score of the level in the pause menu.";
        protected override void OnFeatureDisabled() => OnDestroy();

        private List<GameObject> createdObjects = [];
        private static readonly TMP_FontAsset titleFont = Resources.Load<TMP_FontAsset>("fonts & materials/CormorantGaramond-SemiBold SDF");
        private static readonly TMP_FontAsset numberFont = Resources.Load<TMP_FontAsset>("fonts & materials/UnifrakturCook-Bold SDF");
        private static readonly Color32 GOLD_COLOR = new Color32(200, 166, 116, 255);
        private const float CHARACTER_SPACING = 45f;
        private const float ROW_MIN_HEIGHT = 30f;
        private const int DEFAULT_FONT_SIZE = 28;
        private const int BIG_VALUE_FONT_SIZE = 40;

        public struct ScoreData(int baseScore, int timeBonus, int protectionBonus, float protectionPercent)
        {
            public int BaseScore = baseScore;
            public int TimeBonus = timeBonus;
            public int ProtectionBonus = protectionBonus;
            public float ProtectionPercent = protectionPercent;
            public readonly int TotalPotentialScore => BaseScore + TimeBonus + ProtectionBonus;

            public static ScoreData Empty => new(0, 0, 0, 100f);
        }

        [HarmonyPatch(typeof(PauseUILoadoutHelper), "OnEnable")]
        private class PauseUILoadoutHelper_OnEnable_Patch
        {
            static PauseMenuScoreManager Instance => Plugin.Instance.GetManager<PauseMenuScoreManager>();

            private static void Postfix(PauseUILoadoutHelper __instance)
            {
                Instance.AddScoreDisplay(__instance);
            }
        }

        private void AddScoreDisplay(PauseUILoadoutHelper pauseUIHelper)
        {
            foreach (var obj in createdObjects)
            {
                if (obj != null) Object.Destroy(obj);
            }
            createdObjects.Clear();

            Transform pauseMenuFrame = pauseUIHelper.transform;

            Transform enemyPreview = pauseUIHelper.enemyPreview.transform;
            if (enemyPreview == null) return;

            var scoreContainer = new GameObject("ScoreDisplay", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement), typeof(Image));
            scoreContainer.transform.SetParent(pauseMenuFrame, false);
            createdObjects.Add(scoreContainer);

            var rectTransform = scoreContainer.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1, 1);
            rectTransform.anchorMax = new Vector2(1, 1);
            rectTransform.pivot = new Vector2(1, 1);
            rectTransform.anchoredPosition = new Vector2(-50, -50); // Offset from top right

            var image = scoreContainer.GetComponent<Image>();
            image.color = new Color(0, 0, 0, 0.8f);
            image.sprite = Resources.Load<Sprite>("ui/frame_round");
            image.type = Image.Type.Sliced;

            var vlg = scoreContainer.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 15;
            vlg.padding = new RectOffset(30, 30, 20, 20);
            vlg.childAlignment = TextAnchor.MiddleLeft;

            int currentScore = ScoreManager.Instance.CurrentScore;
            bool isNight = DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
            ScoreData scoreData = ScoreData.Empty;

            // Calculate potential score for current wave only during night
            if (isNight)
            {
                scoreData = CalculatePotentialScoreDetailed();
            }

            // Get active mutators and their multipliers
            List<(string name, float multiplier)> activeMutators = [];
            float mutatorMultiplier = 1.0f;
            foreach (var equippable in PerkManager.instance.CurrentlyEquipped)
            {
                if (equippable is not EquippableMutation) continue;
                float scoreMultiplier = (equippable as EquippableMutation).scoreMultiplyerOnWin;
                string mutationName = equippable.displayName;
                activeMutators.Add((mutationName, scoreMultiplier));
                mutatorMultiplier *= scoreMultiplier;
            }
            activeMutators.Sort((a, b) => b.multiplier.CompareTo(a.multiplier)); // Sort mutators by bonus (highest first)
            bool hasMutatorBonus = mutatorMultiplier > 1.0f;
            float mutatorBonusPercentage = (mutatorMultiplier - 1.0f) * 100f;

            // Set container size based on content
            rectTransform.sizeDelta = isNight ?
                (hasMutatorBonus ? new Vector2(800, 480) : new Vector2(800, 420)) :
                (hasMutatorBonus ? new Vector2(800, 220) : new Vector2(800, 160));

            // Display current score
            AddScoreRow(scoreContainer, "CURRENT SCORE", currentScore.ToString(), true);

            // Add mutator bonus for current score if applicable
            if (hasMutatorBonus && currentScore > 0)
            {
                int totalWithBonus = Mathf.CeilToInt(currentScore * mutatorMultiplier);

                // Create detailed tooltip showing each mutator's contribution
                System.Text.StringBuilder tooltipBuilder = new System.Text.StringBuilder();
                foreach (var (name, multiplier) in activeMutators)
                {
                    float individualBonus = (multiplier - 1.0f) * 100f;
                    tooltipBuilder.AppendLine($"• {name}: +{individualBonus:0}%");
                }
                AddScoreRow(scoreContainer, $"W/ MUTATORS (+{mutatorBonusPercentage:0}%)", $"{totalWithBonus}", false, tooltipBuilder.ToString());
            }

            // Add projected score details during night
            if (isNight)
            {
                AddSpacer(scoreContainer, 5);
                AddHorizontalRule(scoreContainer);
                AddSpacer(scoreContainer, 0);

                AddScoreRow(scoreContainer, "PROJECTED:", "");

                int baseScorePerNight = AccessTools.Field(typeof(ScoreManager), "baseScorePerNight").GetValue(ScoreManager.Instance) as int? ?? 100;
                string baseScoreTooltip = $"You always get {baseScorePerNight} points for surviving the night.";
                AddScoreRow(scoreContainer, "NIGHT SURVIVED", $"+{scoreData.BaseScore}", false, baseScoreTooltip);

                // Add tooltip for realm protection calculation
                int protectionScorePerNight = AccessTools.Field(typeof(ScoreManager), "protectionScorePerNight").GetValue(ScoreManager.Instance) as int? ?? 250;
                float scoreExponent = AccessTools.Field(typeof(ScoreManager), "scoreExponent").GetValue(ScoreManager.Instance) as float? ?? 2f;
                string protectionTooltip = $"Calculated as {protectionScorePerNight} × (% Buildings Protected)^{scoreExponent}.";
                AddScoreRow(scoreContainer, $"REALM {scoreData.ProtectionPercent:0}% PROTECTED", $"+{scoreData.ProtectionBonus}", false, protectionTooltip);

                // Add time bonus with seconds since final spawn
                float timeSinceFinalSpawn = Mathf.Max(0, DayNightCycle.Instance.CurrentNightLength - EnemySpawner.instance.LastSpawnPeriodDuration);
                int timeScorePerNight = AccessTools.Field(typeof(ScoreManager), "timeScorePerNight").GetValue(ScoreManager.Instance) as int? ?? 250;
                float timeBonusMinTime = AccessTools.Field(typeof(ScoreManager), "timeBonusMinTime").GetValue(ScoreManager.Instance) as float? ?? 5f;
                float timeBonusMaxTime = AccessTools.Field(typeof(ScoreManager), "timeBonusMaxTime").GetValue(ScoreManager.Instance) as float? ?? 90f;
                string timeTooltip = $"Time bonus is based on how long after the final spawn you complete the night.<br>{timeBonusMinTime}s or less gives maximum points, {timeBonusMaxTime}s or more gives no points.<br>Calculated as {timeScorePerNight} × ((({timeBonusMaxTime}s - time since final spawn) / {timeBonusMaxTime - timeBonusMinTime}s))^{scoreExponent}.";
                AddScoreRow(scoreContainer, $"TIME BONUS ({timeSinceFinalSpawn:0}s)", $"+{scoreData.TimeBonus}", false, timeTooltip);

                AddSpacer(scoreContainer, 0);

                // Show total projected score
                int projectedScore = currentScore + scoreData.TotalPotentialScore;
                AddScoreRow(scoreContainer, "", projectedScore.ToString(), true);

                // Add mutator bonus for projected score if applicable
                if (hasMutatorBonus)
                {
                    int totalWithBonus = Mathf.CeilToInt(projectedScore * mutatorMultiplier);

                    // Create detailed tooltip showing each mutator's contribution
                    System.Text.StringBuilder tooltipBuilder = new System.Text.StringBuilder();
                    foreach (var (name, multiplier) in activeMutators)
                    {
                        float individualBonus = (multiplier - 1.0f) * 100f;
                        tooltipBuilder.AppendLine($"• {name}: +{individualBonus:0}%");
                    }
                    AddScoreRow(scoreContainer, $"W/ MUTATORS (+{mutatorBonusPercentage:0}%)", $"{totalWithBonus}", false, tooltipBuilder.ToString());
                }

                AddSpacer(scoreContainer, 0);
            }
        }

        private void AddSpacer(GameObject parent, float height)
        {
            var spacerObj = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacerObj.transform.SetParent(parent.transform, false);
            createdObjects.Add(spacerObj);

            var layoutElement = spacerObj.GetComponent<LayoutElement>();
            layoutElement.minHeight = height;
        }

        private void AddHorizontalRule(GameObject parent)
        {
            var ruleObj = new GameObject("HorizontalRule", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            ruleObj.transform.SetParent(parent.transform, false);
            createdObjects.Add(ruleObj);

            var rectTransform = ruleObj.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(590, 2);

            var layoutElement = ruleObj.GetComponent<LayoutElement>();
            layoutElement.minHeight = 2;
            layoutElement.flexibleWidth = 1;

            var image = ruleObj.GetComponent<Image>();
            image.color = GOLD_COLOR;
        }

        private void AddScoreRow(GameObject parent, string label, string value, bool isBigValue = false, string tooltip = null)
        {
            // Create row container
            var rowObj = new GameObject("ScoreRow", typeof(RectTransform), typeof(LayoutElement));
            rowObj.transform.SetParent(parent.transform, false);
            createdObjects.Add(rowObj);

            var rowLayout = rowObj.GetComponent<LayoutElement>();
            rowLayout.minHeight = ROW_MIN_HEIGHT + (isBigValue ? 10 : 0);
            rowLayout.preferredHeight = ROW_MIN_HEIGHT + (isBigValue ? 10 : 0);

            // Add horizontal layout for label and value
            var hlgObj = new GameObject("HorizontalLayout", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            hlgObj.transform.SetParent(rowObj.transform, false);
            createdObjects.Add(hlgObj);

            var rectTransform = hlgObj.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var hlg = hlgObj.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childForceExpandWidth = true;

            // Add label
            if (!string.IsNullOrEmpty(label))
            {
                var labelObj = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
                labelObj.transform.SetParent(hlgObj.transform, false);
                createdObjects.Add(labelObj);

                var labelText = labelObj.GetComponent<TextMeshProUGUI>();
                labelText.text = label;
                labelText.fontSize = DEFAULT_FONT_SIZE;
                labelText.font = titleFont;
                labelText.color = GOLD_COLOR;
                labelText.alignment = TextAlignmentOptions.Left;
                labelText.characterSpacing = CHARACTER_SPACING;
                labelText.enableAutoSizing = false;

                var labelLayout = labelObj.GetComponent<LayoutElement>();
                labelLayout.flexibleWidth = 1;

                // Add tooltip if provided
                if (!string.IsNullOrEmpty(tooltip))
                {
                    // Add a simple button component to handle events
                    var button = labelObj.AddComponent<Button>();
                    var colors = button.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = Color.white;
                    colors.pressedColor = Color.white;
                    colors.selectedColor = Color.white;
                    colors.disabledColor = Color.white;
                    colors.colorMultiplier = 1f;
                    button.colors = colors;

                    // Create a static tooltip manager if it doesn't exist
                    if (tooltipManager == null)
                    {
                        CreateTooltipManager();
                    }

                    // Add event listeners
                    button.onClick.AddListener(() => {});  // Empty listener to make button work

                    var eventTrigger = labelObj.AddComponent<UnityEngine.EventSystems.EventTrigger>();

                    var pointerEnterEntry = new UnityEngine.EventSystems.EventTrigger.Entry
                    {
                        eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter
                    };
                    pointerEnterEntry.callback.AddListener((data) => {
                        ShowTooltip(tooltip, ((UnityEngine.EventSystems.PointerEventData)data).position);
                    });
                    eventTrigger.triggers.Add(pointerEnterEntry);

                    var pointerExitEntry = new UnityEngine.EventSystems.EventTrigger.Entry
                    {
                        eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit
                    };
                    pointerExitEntry.callback.AddListener((data) => {
                        HideTooltip();
                    });
                    eventTrigger.triggers.Add(pointerExitEntry);

                    var pointerMoveEntry = new UnityEngine.EventSystems.EventTrigger.Entry
                    {
                        eventID = UnityEngine.EventSystems.EventTriggerType.Move
                    };
                    pointerMoveEntry.callback.AddListener((data) => {
                        UpdateTooltipPosition(((UnityEngine.EventSystems.PointerEventData)data).position);
                    });
                    eventTrigger.triggers.Add(pointerMoveEntry);
                }
            }

            // Add value
            if (!string.IsNullOrEmpty(value))
            {
                var valueObj = new GameObject("Value", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
                valueObj.transform.SetParent(hlgObj.transform, false);
                createdObjects.Add(valueObj);

                var valueText = valueObj.GetComponent<TextMeshProUGUI>();
                valueText.text = value;
                valueText.fontSize = isBigValue ? BIG_VALUE_FONT_SIZE : DEFAULT_FONT_SIZE;
                valueText.font = numberFont;
                valueText.color = GOLD_COLOR;
                valueText.alignment = TextAlignmentOptions.Right;
                valueText.characterSpacing = CHARACTER_SPACING;
                valueText.enableAutoSizing = false;

                var valueLayout = valueObj.GetComponent<LayoutElement>();
                valueLayout.minWidth = 150;
            }
        }

        private ScoreData CalculatePotentialScoreDetailed()
        {
            try
            {
                int baseScorePerNight = AccessTools.Field(typeof(ScoreManager), "baseScorePerNight").GetValue(ScoreManager.Instance) as int? ?? 100;
                int protectionScorePerNight = AccessTools.Field(typeof(ScoreManager), "protectionScorePerNight").GetValue(ScoreManager.Instance) as int? ?? 200;
                int timeScorePerNight = AccessTools.Field(typeof(ScoreManager), "timeScorePerNight").GetValue(ScoreManager.Instance) as int? ?? 200;
                float scoreExponent = AccessTools.Field(typeof(ScoreManager), "scoreExponent").GetValue(ScoreManager.Instance) as float? ?? 2f;
                float timeBonusMinTime = AccessTools.Field(typeof(ScoreManager), "timeBonusMinTime").GetValue(ScoreManager.Instance) as float? ?? 10f;
                float timeBonusMaxTime = AccessTools.Field(typeof(ScoreManager), "timeBonusMaxTime").GetValue(ScoreManager.Instance) as float? ?? 120f;
                float scoreMultiplyerFromPerks = AccessTools.Field(typeof(ScoreManager), "scoreMultiplyerFromPerks").GetValue(ScoreManager.Instance) as float? ?? 1f;

                int baseScore = Mathf.RoundToInt(baseScorePerNight * scoreMultiplyerFromPerks);

                float currentNightLength = DayNightCycle.Instance.CurrentNightLength;
                float lastSpawnPeriodDuration = EnemySpawner.instance.LastSpawnPeriodDuration;

                float inverseLerp = Mathf.InverseLerp(
                    timeBonusMaxTime + lastSpawnPeriodDuration,
                    timeBonusMinTime + lastSpawnPeriodDuration,
                    currentNightLength);

                int timeBonus = Mathf.RoundToInt(
                    Mathf.Pow(inverseLerp, scoreExponent) *
                    timeScorePerNight *
                    scoreMultiplyerFromPerks);

                List<TaggedObject> buildings = [];
                TagManager.instance.FindAllTaggedObjectsWithTags(buildings, [TagManager.ETag.Building], null);

                int knockedOutBuildings = 0;
                foreach (TaggedObject building in buildings)
                {
                    BuildingInteractor interactor = building.transform.parent.GetComponentInChildren<BuildingInteractor>(true);
                    if (interactor != null && interactor.KnockedOutTonight)
                    {
                        knockedOutBuildings++;
                    }
                }

                float protectionPercent = 100f;
                if (buildings.Count > 0)
                {
                    protectionPercent = 100 * (1f - (float)knockedOutBuildings / buildings.Count);
                }

                float protectionRatio = 1f - Mathf.InverseLerp(0f, buildings.Count, knockedOutBuildings);
                float protectionRatioPowered = Mathf.Pow(protectionRatio, scoreExponent);

                int protectionBonus = Mathf.RoundToInt(
                    protectionScorePerNight *
                    protectionRatioPowered *
                    scoreMultiplyerFromPerks);

                return new ScoreData(baseScore, timeBonus, protectionBonus, protectionPercent);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"Error calculating potential score: {ex.Message}");
                return ScoreData.Empty;
            }
        }

        public override void OnDestroy()
        {
            foreach (var obj in createdObjects)
            {
                if (obj != null) Object.Destroy(obj);
            }
            createdObjects.Clear();

            // Clean up tooltip manager
            if (tooltipManager != null)
            {
                Object.Destroy(tooltipManager);
                tooltipManager = null;
            }

            // Clean up any active tooltip
            if (activeTooltip != null)
            {
                Object.Destroy(activeTooltip);
                activeTooltip = null;
            }
        }

        // Global tooltip manager
        private static GameObject tooltipManager;
        private static GameObject activeTooltip;

        private void CreateTooltipManager()
        {
            tooltipManager = new GameObject("TooltipManager");
            Object.DontDestroyOnLoad(tooltipManager);
            createdObjects.Add(tooltipManager);
        }

        private void ShowTooltip(string text, Vector2 position)
        {
            HideTooltip(); // Hide any existing tooltip

            // Find the main canvas
            Canvas mainCanvas = null;
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (Canvas canvas in canvases)
            {
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    mainCanvas = canvas;
                    break;
                }
            }

            if (mainCanvas == null)
            {
                Debug.LogError("Cannot create tooltip: No overlay canvas found in scene");
                return;
            }

            // Create tooltip
            activeTooltip = new GameObject("ActiveTooltip", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(Image), typeof(CanvasGroup));
            activeTooltip.transform.SetParent(mainCanvas.transform, false);
            createdObjects.Add(activeTooltip);

            // Make tooltip ignore mouse events
            var canvasGroup = activeTooltip.GetComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;

            var rectTransform = activeTooltip.GetComponent<RectTransform>();
            // Position tooltip below the cursor instead of directly at cursor position
            rectTransform.position = position + new Vector2(0, -70);

            var image = activeTooltip.GetComponent<Image>();
            image.color = new Color(0, 0, 0, 0.9f);
            image.sprite = Resources.Load<Sprite>("ui/frame_round");
            image.type = Image.Type.Sliced;

            var vlg = activeTooltip.GetComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(15, 15, 10, 10);

            var csf = activeTooltip.GetComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textObj = new GameObject("TooltipText", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            textObj.transform.SetParent(activeTooltip.transform, false);
            createdObjects.Add(textObj);

            var textComponent = textObj.GetComponent<TextMeshProUGUI>();
            textComponent.text = text;
            textComponent.fontSize = 20;
            textComponent.font = titleFont;
            textComponent.color = GOLD_COLOR;
            textComponent.alignment = TextAlignmentOptions.Left;

            var layoutElement = textObj.GetComponent<LayoutElement>();
            layoutElement.minWidth = 450;
            layoutElement.preferredWidth = 450;
            layoutElement.flexibleWidth = 0;
        }

        private void UpdateTooltipPosition(Vector2 position)
        {
            if (activeTooltip != null)
            {
                var rectTransform = activeTooltip.GetComponent<RectTransform>();
                // Position tooltip below the cursor instead of directly at cursor position
                rectTransform.position = position + new Vector2(0, -70);
            }
        }

        private void HideTooltip()
        {
            if (activeTooltip != null)
            {
                Object.Destroy(activeTooltip);
                activeTooltip = null;
            }
        }
    }
}
