using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Linq;
using System;
using HarmonyLib;
using MPUIKIT;
using TMPro;

/*
    Adds a randomizer button to the loadout selection screen.
    This button will randomly select 5 perks and a weapon from the unlocked options.
*/
namespace PalaceMod
{
    public class LoadoutRandomizerManager : BaseManager
    {
        public override string FeatureSection => "Loadout randomizer";
        public override string FeatureDescription => "Adds dice buttons to the loadout screen that pick a random weapon, perks or mutators from what you've unlocked.";
        protected override void OnFeatureDisabled() => DestroyRandomizeButtons();

        private GameObject randomizeWeaponButton;
        private GameObject randomizePerksButton;
        private GameObject randomizeMutatorsButton;
        private readonly System.Random random = new System.Random();
        private Sprite diceSprite;
        private bool skipNextTrySelectEquippableForLoadout = false;
        private const double ExponentialLambda = 0.3; // Used for deciding how many mutators to randomize

        // Custom component to listen to clicks immediately, since the game registers clicks before we do
        private class ClickListener : MonoBehaviour, IPointerDownHandler
        {
            private LoadoutRandomizerManager Manager => Plugin.Instance.GetManager<LoadoutRandomizerManager>();

            public void OnPointerDown(PointerEventData eventData)
            {
                Manager.skipNextTrySelectEquippableForLoadout = true;
            }
        }

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);

            diceSprite = LoadSpriteFromAssets(
                "dice_icon.png",
                pixelsPerUnit: 100f
            );
        }

        [HarmonyPatch(typeof(LoadoutUIHelper), "OnShow")]
        private class LoadoutUIHelper_OnShow_Patch
        {
            static LoadoutRandomizerManager Instance => Plugin.Instance.GetManager<LoadoutRandomizerManager>();

            private static void Postfix(LoadoutUIHelper __instance)
            {
                Instance.DestroyRandomizeButtons();
                Instance.CreateRandomizeButtons(__instance);
            }
        }

        public override void OnDestroy()
        {
            DestroyRandomizeButtons();
        }

        private void DestroyRandomizeButtons()
        {
            if (randomizeWeaponButton != null) UnityEngine.Object.Destroy(randomizeWeaponButton);
            if (randomizePerksButton != null) UnityEngine.Object.Destroy(randomizePerksButton);
            if (randomizeMutatorsButton != null) UnityEngine.Object.Destroy(randomizeMutatorsButton);

            var loadoutUIHelper = UnityEngine.Object.FindObjectOfType<LoadoutUIHelper>();
            if (loadoutUIHelper != null)
            {
                AccessTools.Method(typeof(LoadoutUIHelper), "RecomputeNavigation").Invoke(loadoutUIHelper, null);
            }
        }

        private void CreateRandomizeButtons(LoadoutUIHelper loadoutUIHelper)
        {
            try
            {
                // Don't show buttons for fixed loadout levels
                if ((AccessTools.Field(typeof(LoadoutUIHelper), "levelInfo").GetValue(loadoutUIHelper) as LevelInfo).HasFixedLoadout) return;

                randomizeWeaponButton = CreateCategoryRandomizeButton(loadoutUIHelper, "Weapon", RandomizeWeapon);
                randomizePerksButton = CreateCategoryRandomizeButton(loadoutUIHelper, "Perks", RandomizePerks);
                randomizeMutatorsButton = CreateCategoryRandomizeButton(loadoutUIHelper, "Mutators", RandomizeMutators);

                AccessTools.Method(typeof(LoadoutUIHelper), "RecomputeNavigation").Invoke(loadoutUIHelper, null);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error creating randomize buttons: {e}");
            }
        }

        private GameObject CreateCategoryRandomizeButton(LoadoutUIHelper loadoutUIHelper, string categoryName, Action<LoadoutUIHelper> randomizeAction)
        {
            var button = CreateRandomizeButton(categoryName);
            var parentTransform = loadoutUIHelper.weaponsParent.GetComponent<RectTransform>().parent;
            var headerText = parentTransform.Find(categoryName + " Header").GetComponent<TMPro.TextMeshProUGUI>();
            PositionButtonInHeader(headerText, button);
            button.GetComponent<Button>().onClick.AddListener(() => randomizeAction(loadoutUIHelper));
            return button;
        }

        private void PositionButtonInHeader(TMPro.TextMeshProUGUI headerText, GameObject button)
        {
            var headerRect = headerText.rectTransform;
            var buttonRect = button.GetComponent<RectTransform>();
            button.transform.SetParent(headerText.transform.parent, false);
            float buttonSize = headerRect.sizeDelta.y * 1.2f;
            buttonRect.sizeDelta = new Vector2(buttonSize, buttonSize);
            buttonRect.anchorMin = new Vector2(0, 0.5f);
            buttonRect.anchorMax = new Vector2(0, 0.5f);
            buttonRect.pivot = new Vector2(0, 0.5f);
            Vector2 textSize = headerText.GetPreferredValues(headerText.text);
            float textEnd = headerRect.rect.width / 2 + textSize.x / 2;
            buttonRect.anchoredPosition = new Vector2(textEnd + 10f, headerRect.anchoredPosition.y);
        }

        private GameObject CreateRandomizeButton(string buttonName)
        {
            var button = new GameObject(buttonName);

            button.AddComponent<ClickListener>();

            var perklike = button.AddComponent<TFUIPerklike>();
            perklike.defaultAnimationCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            perklike.defaultAnimationTime = 0.2f;

            var background = new GameObject("Background");
            background.transform.SetParent(button.transform, false);
            var backgroundRect = background.AddComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            var backgroundImg = background.AddComponent<MPImageBasic>();
            backgroundImg.OutlineColor = new Color(0.2f, 0.2f, 0.2f, 0f);
            backgroundImg.color = new Color(0f, 0f, 0f, 0f);
            perklike.backgroundImg = backgroundImg;

            var iconObj = new GameObject("Icon");
            iconObj.transform.SetParent(background.transform, false); // Make icon a child of background so it scales with it for animation purposess
            var iconRect = iconObj.AddComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(4, 4);
            iconRect.offsetMax = new Vector2(-4, -4);

            var iconImage = iconObj.AddComponent<Image>();
            iconImage.sprite = diceSprite;
            iconImage.preserveAspect = true;
            iconImage.color = new Color(0.9f, 0.8f, 0.4f, 1f);
            perklike.iconImg = iconImage;

            var buttonComponent = button.AddComponent<Button>();
            buttonComponent.targetGraphic = backgroundImg;

            // Connect the ThronefallUIElement's onApply event to the button click
            var applyEvent = AccessTools.Field(typeof(TFUIPerklike), "onApply").GetValue(perklike) as UnityEngine.Events.UnityEvent;
            applyEvent.AddListener(() => {
                buttonComponent.onClick.Invoke();
                StartShakeAnimation(button.transform);
            });

            // Handles audio feedback when keyboard navigating to this button or activating it
            button.AddComponent<TFUIAudioHelper>();

            var curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            perklike.focussedStyle = new TFUIPerklike.Style(backgroundImg.OutlineColor, curve, 0.2f, 1.15f);
            perklike.selectedStyle = new TFUIPerklike.Style(backgroundImg.OutlineColor, curve, 0.2f, 1.2f);
            perklike.focussedAndSelectedStyle = new TFUIPerklike.Style(backgroundImg.OutlineColor, curve, 0.2f, 1.25f);

            // Set up the RectTransform
            var rectTransform = button.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0, 0.5f);
            rectTransform.anchorMax = new Vector2(0, 0.5f);
            rectTransform.pivot = new Vector2(0, 0.5f);
            rectTransform.sizeDelta = new Vector2(24, 24); // Slightly larger for better visibility

            return button;
        }

        // Shake animation for when the button is applied
        private void StartShakeAnimation(Transform buttonTransform)
        {
            Plugin.Instance.StartCoroutine(ShakeCoroutine(buttonTransform));
        }

        private System.Collections.IEnumerator ShakeCoroutine(Transform transform)
        {
            Vector3 originalPosition = transform.localPosition;
            float duration = 0.3f;
            float elapsed = 0f;
            float magnitude = 3f; // Shake intensity

            while (elapsed < duration)
            {
                float x = originalPosition.x + UnityEngine.Random.Range(-1f, 1f) * magnitude;
                float y = originalPosition.y + UnityEngine.Random.Range(-1f, 1f) * magnitude;

                transform.localPosition = new Vector3(x, y, originalPosition.z);

                elapsed += Time.deltaTime;
                yield return null;
            }

            transform.localPosition = originalPosition;
        }

        [HarmonyPatch(typeof(LoadoutUIHelper), "TrySelectEquippableForLoadout")]
        private class Patch_TrySelectEquippableForLoadout
        {
            static LoadoutRandomizerManager Instance => Plugin.Instance.GetManager<LoadoutRandomizerManager>();

            private static bool Prefix()
            {
                // Skip the method execution right after a randomize button was clicked to avoid flickering
                if (Instance.skipNextTrySelectEquippableForLoadout)
                {
                    Instance.skipNextTrySelectEquippableForLoadout = false;
                    return false;
                }
                return true;
            }
        }

        private void RandomizeWeapon(LoadoutUIHelper loadoutUIHelper)
        {
            try
            {
                // Set lastApplied to null to avoid weapon flickering caused by TrySelectEquippableForLoadout
                var frameField = AccessTools.Field(typeof(LoadoutUIHelper), "frame");
                var frame = frameField.GetValue(loadoutUIHelper) as UIFrame;
                var lastAppliedField = AccessTools.Field(typeof(UIFrame), "lastApplied");
                lastAppliedField.SetValue(frame, null);

                var weapons = AccessTools.Field(typeof(LoadoutUIHelper), "weapons").GetValue(loadoutUIHelper) as List<TFUIEquippable>;
                var availableWeapons = weapons.Where(w => !w.Locked && w.Data.IsUnlocked).ToList();

                var equippedWeaponsField = AccessTools.Field(typeof(LoadoutUIHelper), "equippedWeapons");

                foreach (var weapon in equippedWeaponsField.GetValue(loadoutUIHelper) as List<TFUIEquippable>)
                {
                    PerkManager.SetEquipped(weapon.Data, false);
                    weapon.UnPick();
                }

                var selectedWeapon = availableWeapons[random.Next(availableWeapons.Count)];
                selectedWeapon.Pick();
                PerkManager.SetEquipped(selectedWeapon.Data, true);
                equippedWeaponsField.SetValue(loadoutUIHelper, new List<TFUIEquippable> { selectedWeapon });

                Plugin.Logger.LogInfo($"Weapon randomized: {selectedWeapon.Data.name}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error randomizing weapon: {e}");
            }
        }

        private void RandomizePerks(LoadoutUIHelper loadoutUIHelper)
        {
            try
            {
                var levelInfo = AccessTools.Field(typeof(LoadoutUIHelper), "levelInfo").GetValue(loadoutUIHelper) as LevelInfo;
                var perks = AccessTools.Field(typeof(LoadoutUIHelper), "perks").GetValue(loadoutUIHelper) as List<TFUIEquippable>;
                var availablePerks = perks.Where(p => !p.Locked && p.Data.IsUnlocked).ToList();
                var equippedPerksField = AccessTools.Field(typeof(LoadoutUIHelper), "equippedPerks");

                foreach (var perk in equippedPerksField.GetValue(loadoutUIHelper) as List<TFUIEquippable>)
                {
                    PerkManager.SetEquipped(perk.Data, false);
                    perk.UnPick();
                }

                int perksToSelect = Math.Min(levelInfo.maxPerkCount, availablePerks.Count);
                var perkPool = new List<TFUIEquippable>(availablePerks);
                var selectedPerks = new List<TFUIEquippable>();

                for (int i = 0; i < perksToSelect; i++)
                {
                    if (perkPool.Count == 0) break;

                    int perkIndex = random.Next(perkPool.Count);
                    var selectedPerk = perkPool[perkIndex];
                    selectedPerk.Pick();
                    PerkManager.SetEquipped(selectedPerk.Data, true);
                    selectedPerks.Add(selectedPerk);
                    perkPool.RemoveAt(perkIndex);
                }

                equippedPerksField.SetValue(loadoutUIHelper, selectedPerks);
                AccessTools.Method(typeof(LoadoutUIHelper), "UpdatePerkPointsRemainingText")?.Invoke(loadoutUIHelper, null);

                Plugin.Logger.LogInfo($"Perks randomized: {string.Join(", ", selectedPerks.Select(p => p.Data.name))}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error randomizing loadout: {e}");
            }
        }

        private void RandomizeMutators(LoadoutUIHelper loadoutUIHelper)
        {
            try
            {
                // Set lastApplied to null to avoid flickering caused by TrySelectEquippableForLoadout
                var frameField = AccessTools.Field(typeof(LoadoutUIHelper), "frame");
                var frame = frameField.GetValue(loadoutUIHelper) as UIFrame;
                var lastAppliedField = AccessTools.Field(typeof(UIFrame), "lastApplied");
                lastAppliedField.SetValue(frame, null);

                var mutators = AccessTools.Field(typeof(LoadoutUIHelper), "mutators").GetValue(loadoutUIHelper) as List<TFUIEquippable>;
                var availableMutators = mutators.Where(m => !m.Locked && m.Data.IsUnlocked).ToList();

                var equippedMutatorsField = AccessTools.Field(typeof(LoadoutUIHelper), "equippedMutators");

                foreach (var mutator in equippedMutatorsField.GetValue(loadoutUIHelper) as List<TFUIEquippable>)
                {
                    PerkManager.SetEquipped(mutator.Data, false);
                    mutator.UnPick();
                }

                var selectedMutators = new List<TFUIEquippable>();
                var mutatorPool = new List<TFUIEquippable>(availableMutators);

                // Select a random number of mutators between 1 and max available
                int maxMutators = mutatorPool.Count;
                int mutatorCount = maxMutators > 1 ? SampleFromExponential(1, maxMutators) : maxMutators;

                for (int i = 0; i < mutatorCount; i++)
                {
                    int mutatorIndex = random.Next(mutatorPool.Count);
                    var selectedMutator = mutatorPool[mutatorIndex];
                    selectedMutator.Pick();
                    PerkManager.SetEquipped(selectedMutator.Data, true);
                    selectedMutators.Add(selectedMutator);
                    mutatorPool.RemoveAt(mutatorIndex);
                }

                equippedMutatorsField.SetValue(loadoutUIHelper, selectedMutators);

                AccessTools.Method(typeof(LoadoutUIHelper), "UpdateMutatorBonusText")?.Invoke(loadoutUIHelper, null);

                Plugin.Logger.LogInfo($"Mutators randomized: {string.Join(", ", selectedMutators.Select(m => m.Data.name))}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error randomizing mutators: {e.Message}\n{e.StackTrace}");
            }
        }

        [HarmonyPatch(typeof(LoadoutUIHelper), "RecomputeNavigation")]
        private class Patch_LoadoutUIHelper_RecomputeNavigation
        {
            static LoadoutRandomizerManager Instance => Plugin.Instance.GetManager<LoadoutRandomizerManager>();

            private static bool Prefix(LoadoutUIHelper __instance)
            {
                Plugin.Logger.LogInfo("Recomputing navigation");
                int constraintCount = __instance.weaponsParent.constraintCount;
                var weapons = AccessTools.Field(typeof(LoadoutUIHelper), "weapons").GetValue(__instance) as List<TFUIEquippable>;
                var perks = AccessTools.Field(typeof(LoadoutUIHelper), "perks").GetValue(__instance) as List<TFUIEquippable>;
                var mutators = AccessTools.Field(typeof(LoadoutUIHelper), "mutators").GetValue(__instance) as List<TFUIEquippable>;

                var stack = new List<List<ThronefallUIElement>>();
                if (Instance.randomizeWeaponButton != null) stack.Add(new List<ThronefallUIElement> { Instance.randomizeWeaponButton.GetComponent<TFUIPerklike>() });
                stack.Add(weapons.Cast<ThronefallUIElement>().ToList());
                if (Instance.randomizePerksButton != null) stack.Add(new List<ThronefallUIElement> { Instance.randomizePerksButton.GetComponent<TFUIPerklike>() });
                stack.Add(perks.Cast<ThronefallUIElement>().ToList());
                if (Instance.randomizeMutatorsButton != null) stack.Add(new List<ThronefallUIElement> { Instance.randomizeMutatorsButton.GetComponent<TFUIPerklike>() });
                if ((bool)AccessTools.Property(typeof(LoadoutUIHelper), "DrawMutators").GetValue(__instance)) stack.Add(mutators.Cast<ThronefallUIElement>().ToList());

                for (int i = 0; i < stack.Count; i++)
                {
                    ThronefallUIElement aboveElement = null;
                    List<ThronefallUIElement> aboveList = null;
                    if (i == 0) aboveElement = __instance.playButton;
                    else if (stack[i - 1].Count == 1) aboveElement = stack[i - 1][0];
                    else aboveList = stack[i - 1];

                    ThronefallUIElement belowElement = null;
                    List<ThronefallUIElement> belowList = null;
                    if (i == stack.Count - 1) belowElement = __instance.playButton;
                    else if (stack[i + 1].Count == 1) belowElement = stack[i + 1][0];
                    else belowList = stack[i + 1];

                    AssignNavigationTargets(stack[i], aboveElement, aboveList, belowElement, belowList, constraintCount);
                }

                __instance.playButton.topNav = stack[^1][^1];
                __instance.playButton.leftNav = stack[^1][^1];
                __instance.playButton.botNav = stack[0][0];
                __instance.playButton.rightNav = stack[0][0];
                if (!__instance.autoSelectPlay)
                {
                    __instance.GetComponent<UIFrame>().firstSelected = weapons[0];
                }
                return false;
            }

            // A clone of the original AssignNavigationTargets method, but that accepts ThronefallUIElement instead of TFUIEquippable
            private static void AssignNavigationTargets(List<ThronefallUIElement> targetElements, ThronefallUIElement aboveElement, List<ThronefallUIElement> aboveList, ThronefallUIElement belowElement, List<ThronefallUIElement> belowList, int maxColumns)
            {
                for (int i = 0; i < targetElements.Count; i++)
                {
                    ThronefallUIElement tFUIElement = targetElements[i];
                    int num = i - 1;
                    int num2 = i + 1;
                    int num3 = i + maxColumns;
                    int num4 = i - maxColumns;
                    if (num < 0)
                    {
                        if (aboveElement != null)
                        {
                            tFUIElement.leftNav = aboveElement;
                        }
                        else
                        {
                            tFUIElement.leftNav = aboveList[aboveList.Count - 1];
                        }
                    }
                    else
                    {
                        tFUIElement.leftNav = targetElements[num];
                    }

                    if (num2 >= targetElements.Count)
                    {
                        if (belowElement != null)
                        {
                            tFUIElement.rightNav = belowElement;
                        }
                        else
                        {
                            tFUIElement.rightNav = belowList[0];
                        }
                    }
                    else
                    {
                        tFUIElement.rightNav = targetElements[num2];
                    }

                    if (num3 >= targetElements.Count)
                    {
                        if ((targetElements.Count % maxColumns != 0 || targetElements.Count - i > maxColumns) && targetElements.Count - i > targetElements.Count % maxColumns)
                        {
                            tFUIElement.botNav = targetElements[targetElements.Count - 1];
                        }
                        else if (belowElement != null)
                        {
                            tFUIElement.botNav = belowElement;
                        }
                        else
                        {
                            int num5 = i % maxColumns;
                            if (belowList.Count < num5)
                            {
                                tFUIElement.botNav = belowList[belowList.Count - 1];
                            }
                            else
                            {
                                tFUIElement.botNav = belowList[num5];
                            }
                        }
                    }
                    else
                    {
                        tFUIElement.botNav = targetElements[num3];
                    }

                    if (num4 < 0)
                    {
                        if (aboveElement != null)
                        {
                            tFUIElement.topNav = aboveElement;
                            continue;
                        }

                        int num6 = i % maxColumns;
                        int num7 = aboveList.Count % maxColumns;
                        if (num6 > num7)
                        {
                            tFUIElement.topNav = aboveList[aboveList.Count - 1];
                            continue;
                        }

                        int value = aboveList.Count - num7 + num6;
                        value = Mathf.Clamp(value, 0, aboveList.Count - 1);
                        tFUIElement.topNav = aboveList[value];
                    }
                    else
                    {
                        tFUIElement.topNav = targetElements[num4];
                    }
                }
            }
        }

        // Samples a number from an exponential distribution. Used for randomly choosing mutator count
        private int SampleFromExponential(int min, int max)
        {
            if (max <= min) return min;

            // Calculate normalization constant
            double normalizationConstant = 0;
            for (int i = min; i <= max; i++)
            {
                normalizationConstant += Math.Exp(-ExponentialLambda * i);
            }

            // Generate random value
            double r = random.NextDouble() * normalizationConstant;
            double cumulativeProb = 0;

            // Find the corresponding value
            for (int i = min; i <= max; i++)
            {
                cumulativeProb += Math.Exp(-ExponentialLambda * i);
                if (r <= cumulativeProb)
                    return i;
            }

            return min; // Fallback
        }
    }
}
