using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Pathfinding;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace PalaceMod
{
    public class WikiEntry
    {
        public string Name { get; set; }  // Display name
        public string InternalName { get; set; }  // Internal/prefab name
        public string Type { get; set; }  // "PlayerUnit", "Building", "Enemy", etc.
        public List<string> Tags { get; set; }
        public Dictionary<string, Dictionary<string, object>> Categories { get; set; }

        public WikiEntry(string type)
        {
            Type = type;
            Categories = new()
            {
                ["General"] = [],
                ["Stats"] = [],
                ["Units"] = [],
                ["Display"] = [],
                ["Health"] = [],
                ["Combat"] = [],
                ["Movement"] = [],
                ["Production"] = [],
                ["Collision"] = [],
                ["Perks"] = [],
                ["Spawning"] = []
            };
        }
    }

    public class WikiDataManager : BaseManager
    {
        public override string FeatureSection => "Wiki";
        public override string FeatureDescription => "Adds an unfinished in-game wiki of units, buildings and enemies to the pause menu.";
        protected override void OnFeatureDisabled() => OnDestroy();

        public Dictionary<string, WikiEntry> wikiEntries = [];
        private readonly HashSet<string> nonInterestingTags =
        [
            "AUTO_Alive", "AUTO_KnockedOutAndHealOnDawn", "AUTO_Commanded", "AUTO_NoReviveNextMorning",
            "PlayerOwned", "EnemyOwned", "PlayerShrine",
            "BlockableEnemyProjectile", "UnreachableForBigEnemiesDueToLevelDesign", "UselessWallThatDoesNotBlockPath", "NONE"
        ];

        [HarmonyPatch(typeof(UIFrameManager), "OpenMenu")]
        private class UIFrameManager_OpenMenu_Patch
        {
            static void Postfix(UIFrameManager __instance)
            {
                try
                {
                    var activeFrame = AccessTools.Field(typeof(UIFrameManager), "activeFrame").GetValue(__instance) as UIFrame;
                    var inMatchPauseMenuFrame = AccessTools.Field(typeof(UIFrameManager), "inMatchPauseMenuFrame").GetValue(__instance) as UIFrame;
                    if (activeFrame != inMatchPauseMenuFrame) return;

                    var menuItems = activeFrame.transform.Find("Title ").Find("Menu Items");
                    if (menuItems.Find("Wiki") != null) return;

                    var settingsButton = menuItems.Find("Settings");
                    var wikiButton = new GameObject("Wiki", typeof(RectTransform), typeof(TFUITextButton), typeof(TextMeshProUGUI));
                    wikiButton.transform.SetParent(menuItems, false);
                    wikiButton.transform.SetSiblingIndex(settingsButton.GetSiblingIndex() + 1);

                    // Copy the visual setup from Settings button
                    var settingsRT = settingsButton.GetComponent<RectTransform>();
                    var wikiRT = wikiButton.GetComponent<RectTransform>();
                    wikiRT.anchorMin = settingsRT.anchorMin;
                    wikiRT.anchorMax = settingsRT.anchorMax;
                    wikiRT.sizeDelta = settingsRT.sizeDelta;
                    wikiRT.anchoredPosition = new Vector2(settingsRT.anchoredPosition.x, settingsRT.anchoredPosition.y - settingsRT.sizeDelta.y - 10);

                    // Set up text
                    var textComponent = wikiButton.GetComponent<TextMeshProUGUI>();
                    var settingsText = settingsButton.GetComponentInChildren<TextMeshProUGUI>();
                    textComponent.font = settingsText.font;
                    textComponent.fontSize = settingsText.fontSize;
                    textComponent.color = settingsText.color;
                    textComponent.alignment = settingsText.alignment;
                    textComponent.text = "Wiki";

                    // Set up button
                    var wikiTFButton = wikiButton.GetComponent<TFUITextButton>();
                    var settingsTFButton = settingsButton.GetComponent<TFUITextButton>();

                    // Save text styles from settings button for later use
                    var savedFont = settingsText.font;
                    var savedFontSize = settingsText.fontSize;
                    var savedColor = settingsText.color;
                    var savedAlignment = settingsText.alignment;

                    // Save button styles
                    var savedAnimCurve = settingsTFButton.defaultAnimationCurve;
                    var savedAnimTime = settingsTFButton.defaultAnimationTime;
                    var savedFocusStyle = settingsTFButton.focussedStyle;
                    var savedSelectedStyle = settingsTFButton.selectedStyle;
                    var savedFocusSelectedStyle = settingsTFButton.focussedAndSelectedStyle;

                    // Set up navigation
                    var nextButton = settingsTFButton.botNav;  // Get the button that was after Settings
                    wikiTFButton.topNav = settingsTFButton;  // Wiki points up to Settings
                    wikiTFButton.botNav = nextButton;        // Wiki points down to next button
                    settingsTFButton.botNav = wikiTFButton;  // Settings points down to Wiki
                    nextButton.topNav = wikiTFButton;    // Next button points up to Wiki

                    // Add click handler
                    wikiTFButton.onApply.AddListener(() => {
                        Plugin.Logger.LogInfo("Wiki button clicked!");

                        // Ensure we have wiki data
                        // var wikiManager = Plugin.Instance.GetManager<WikiDataManager>();
                        // if (wikiManager != null && wikiManager.wikiEntries.Count == 0)
                        // {
                        //     wikiManager.LoadWikiData();
                        // }

                        // Create wiki frame if it doesn't exist yet
                        var wikiFrame = GameObject.Find("Wiki Frame")?.GetComponent<UIFrame>();
                        Plugin.Logger.LogInfo("Trying to find existing Wiki Frame");
                        if (wikiFrame == null)
                        {
                            Plugin.Logger.LogInfo("Wiki Frame not found, creating new one");
                            // Create a new frame GameObject
                            var wikiFrameObj = new GameObject("Wiki Frame", typeof(RectTransform), typeof(UIFrame), typeof(CanvasRenderer));
                            wikiFrame = wikiFrameObj.GetComponent<UIFrame>();

                            // Set up the frame's transform
                            wikiFrameObj.transform.SetParent(activeFrame.transform.parent, false);
                            var wikiFrameRT = wikiFrameObj.GetComponent<RectTransform>();
                            wikiFrameRT.anchorMin = Vector2.zero;
                            wikiFrameRT.anchorMax = Vector2.one;
                            wikiFrameRT.sizeDelta = Vector2.zero;
                            wikiFrameRT.anchoredPosition = Vector2.zero;

                            // Clone the background from the original pause menu
                            var originalBackground = inMatchPauseMenuFrame.transform.Find("BG");
                            Plugin.Logger.LogInfo($"Original BG found: {originalBackground != null}");

                            // Clone the original background
                            var bgObject = UnityEngine.Object.Instantiate(originalBackground.gameObject, wikiFrameObj.transform);
                            bgObject.name = "BG";
                            bgObject.transform.SetAsFirstSibling(); // Make sure it's behind other elements

                            // Reset transform properties
                            var bgRT = bgObject.GetComponent<RectTransform>();
                            bgRT.anchorMin = Vector2.zero;
                            bgRT.anchorMax = Vector2.one;
                            bgRT.sizeDelta = Vector2.zero;
                            bgRT.anchoredPosition = Vector2.zero;

                            Plugin.Logger.LogInfo("Successfully cloned background from original pause menu");

                            // Create back button - use the same approach as the original game buttons
                            var backButton = new GameObject("Back");
                            backButton.transform.SetParent(wikiFrameObj.transform, false);

                            // Add components in the correct order
                            var backRT = backButton.AddComponent<RectTransform>();
                            backRT.anchorMin = new Vector2(0, 1);
                            backRT.anchorMax = new Vector2(0, 1);
                            backRT.sizeDelta = new Vector2(200, 50);
                            backRT.anchoredPosition = new Vector2(100, -50);

                            // Add text component first (like in the original buttons)
                            var backText = backButton.AddComponent<TextMeshProUGUI>();
                            backText.font = savedFont;
                            backText.fontSize = savedFontSize;
                            backText.color = savedColor;
                            backText.alignment = savedAlignment;
                            backText.text = "Back";

                            // Add button component after text
                            var backTFButton = backButton.AddComponent<TFUITextButton>();
                            backTFButton.defaultAnimationCurve = savedAnimCurve;
                            backTFButton.defaultAnimationTime = savedAnimTime;
                            backTFButton.focussedStyle = savedFocusStyle;
                            backTFButton.selectedStyle = savedSelectedStyle;
                            backTFButton.focussedAndSelectedStyle = savedFocusSelectedStyle;

                            // Add audio helper like original buttons have
                            backButton.AddComponent<TFUIAudioHelper>();

                            // Add back button click handler
                            backTFButton.onApply.AddListener(() => {
                                Plugin.Logger.LogInfo("Back button clicked");
                                __instance.CloseActiveFrame();
                            });

                            // Register the frame with the frame manager
                            __instance.RegisterFrame(wikiFrame);

                            // Add the back button to the frame's managed elements
                            var managedElementsField = AccessTools.Field(typeof(UIFrame), "managedElements");
                            if (managedElementsField != null)
                            {
                                var managedElements = new List<ThronefallUIElement> { backTFButton };
                                managedElementsField.SetValue(wikiFrame, managedElements);
                            }

                            // Set up other required fields for the frame
                            var frameManagerField = AccessTools.Field(typeof(UIFrame), "frameManager");
                            var inputField = AccessTools.Field(typeof(UIFrame), "input");
                            var graphicRaycasterField = AccessTools.Field(typeof(UIFrame), "graphicRaycaster");

                            if (frameManagerField != null && inputField != null)
                            {
                                frameManagerField.SetValue(wikiFrame, __instance);
                                inputField.SetValue(wikiFrame, AccessTools.Field(typeof(UIFrameManager), "input").GetValue(__instance));

                                if (graphicRaycasterField != null)
                                {
                                        graphicRaycasterField.SetValue(wikiFrame, activeFrame.transform.parent.GetComponent<GraphicRaycaster>());
                                }
                            }

                            // Set the current selected element
                            var currentSelectedField = AccessTools.Field(typeof(UIFrame), "currentSelected");
                            if (currentSelectedField != null)
                            {
                                currentSelectedField.SetValue(wikiFrame, backTFButton);
                            }
                        }

                        // Use the frame manager to change to the wiki frame
                        __instance.ChangeActiveFrame(wikiFrame);

                        // Find and log Berserk entry as an example
                        // var berserkEntry = wikiManager?.wikiEntries.Values.FirstOrDefault(e => e.Name.Contains("Berserk"));
                        // if (berserkEntry != null)
                        // {
                        //     wikiManager.LogWikiEntry(berserkEntry);
                        // }
                        // else
                        // {
                        //     Plugin.Logger.LogInfo("Berserk unit entry not found in wiki data");
                        // }
                    });
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"Error creating Wiki button: {e}");
                }
            }
        }

        private void LoadWikiData()
        {
            wikiEntries.Clear();
            var allObjects = Resources.LoadAll<GameObject>("");
            foreach (var obj in allObjects)
            {
                SearchGameObjectRecursively(obj);
            }
        }

        private void SearchGameObjectRecursively(GameObject obj)
        {
            if (obj == null) return;

            // Check if this is a tagged object
            var taggedObject = obj.GetComponent<TaggedObject>();
            if (taggedObject != null && taggedObject.Tags != null)
            {
                if (taggedObject.Tags.Contains(TagManager.ETag.PlayerUnit))
                {
                    ExtractData(obj, "PlayerUnit");
                }
                else if (taggedObject.Tags.Contains(TagManager.ETag.EnemyOwned))
                {
                    ExtractData(obj, "Enemy");
                }
                else if (taggedObject.Tags.Contains(TagManager.ETag.Building))
                {
                    ExtractData(obj, "Building");
                }
            }

            // Search children
            foreach (Transform child in obj.transform)
            {
                SearchGameObjectRecursively(child.gameObject);
            }
        }

        private string FormatTargetPriority(TargetPriority priority)
        {
            var description = new List<string>();

            if (priority.mustHaveTags?.Any() == true)
            {
                var interestingMustHave = priority.mustHaveTags.Select(t => t.ToString()).Where(t => !nonInterestingTags.Contains(t));
                if (interestingMustHave.Any())
                {
                    description.Add($"Must have: {string.Join(", ", interestingMustHave)}");
                }
            }
            if (priority.mayNotHaveTags?.Any() == true)
            {
                var interestingMayNot = priority.mayNotHaveTags.Select(t => t.ToString()).Where(t => !nonInterestingTags.Contains(t));
                if (interestingMayNot.Any())
                {
                    description.Add($"Must not have: {string.Join(", ", interestingMayNot)}");
                }
            }
            description.Add($"Range: {priority.range}");

            return string.Join(" | ", description);
        }

        private void ExtractData(GameObject obj, string type)
        {
            // For buildings, gather all relevant components and data
            if (type == "Building")
            {
                // Get mesh info for display name
                var meshFilter = obj.GetComponentInChildren<MeshFilter>();
                var meshName = meshFilter?.sharedMesh?.name ?? "";
                // Clean up the mesh name to get a readable building name
                var displayName = meshName.Replace(" Instance", "").Replace(" Final", "");

                // For buildings, check components
                var hasHp = obj.GetComponent<Hp>();
                var hasAttack = obj.GetComponent<AutoAttack>();
                var hasRespawner = obj.GetComponent<UnitRespawnerForBuildings>();
                var hasBuildSlot = obj.GetComponent<BuildSlot>();
                var navmeshCut = obj.GetComponent<NavmeshCut>();
                var buildingInteractor = hasBuildSlot?.GetComponent<BuildingInteractor>();

                var buildingEntry = new WikiEntry(type);
                buildingEntry.Name = displayName;
                buildingEntry.InternalName = buildingInteractor?.name ?? hasBuildSlot?.name ?? meshName;

                // General info
                var general = buildingEntry.Categories["General"];
                general["Name"] = displayName;

                // Try to determine building category from tags or components
                var buildingTags = obj.GetComponent<TaggedObject>();
                if (buildingTags != null && buildingTags.Tags != null)
                {
                    if (buildingTags.Tags.Contains(TagManager.ETag.TrainingFacility))
                        general["Building Category"] = "Defensive";
                }

                // Stats
                var stats = buildingEntry.Categories["Stats"];
                if (hasHp != null)
                {
                    stats["Base Health"] = hasHp.maxHp;
                }

                // Get costs and upgrade info from interactor
                if (buildingInteractor != null)
                {
                    var interactorType = buildingInteractor.GetType();
                    var costField = interactorType.GetField("cost");
                    if (costField != null)
                    {
                        var cost = costField.GetValue(buildingInteractor);
                        if (cost != null)
                            stats["Building Cost"] = cost + " Gold";
                    }

                    var upgradeCostsField = interactorType.GetField("upgradeCosts");
                    if (upgradeCostsField != null)
                    {
                        var upgradeCosts = upgradeCostsField.GetValue(buildingInteractor) as List<int>;
                        if (upgradeCosts != null)
                        {
                            for (int i = 0; i < upgradeCosts.Count; i++)
                            {
                                stats[$"Upgrade Cost Level {i + 2}"] = upgradeCosts[i] + " Gold";
                            }
                        }
                    }

                    // Try to get max level for health and unit counts
                    var maxLevelField = interactorType.GetField("maxLevel");
                    if (maxLevelField != null)
                    {
                        var maxLevel = (int)maxLevelField.GetValue(buildingInteractor);
                        if (maxLevel > 0)
                        {
                            List<float> healthPerLevel = [];
                            List<int> unitsPerLevel = [];

                            // Base values
                            if (hasHp != null)
                                healthPerLevel.Add(hasHp.maxHp);
                            if (hasRespawner != null)
                                unitsPerLevel.Add(4); // Base unit count

                            // Estimate higher levels (this is a guess, adjust multipliers as needed)
                            for (int i = 1; i < maxLevel; i++)
                            {
                                healthPerLevel.Add(hasHp != null ? hasHp.maxHp * (1 + i * 0.33f) : 0);
                                unitsPerLevel.Add(4 * (i + 1)); // Double units each level
                            }

                            if (healthPerLevel.Any())
                                stats["Health Points"] = string.Join(" | ", healthPerLevel);
                            if (unitsPerLevel.Any())
                                stats["Units spawned"] = string.Join(" | ", unitsPerLevel);
                        }
                    }
                }
            }

            var entry = new WikiEntry(type);
            entry.Name = obj.name;
            entry.InternalName = obj.name;

            // Tags
            var taggedObj = obj.GetComponent<TaggedObject>();
            if (taggedObj != null && taggedObj.Tags != null)
            {
                entry.Tags = taggedObj.Tags.Select(t => t.ToString()).ToList();
            }

            // HP
            var hp = obj.GetComponent<Hp>();
            if (hp != null)
            {
                entry.Categories["Health"]["Base HP"] = hp.maxHp;
                entry.Categories["Health"]["Hit Feedback Height"] = hp.hitFeedbackHeight;
            }

            // Attack
            var attack = obj.GetComponent<AutoAttack>();
            if (attack != null)
            {
                var combat = entry.Categories["Combat"];
                combat["Attack Cooldown"] = attack.cooldownDuration;
                combat["Cooldown After Spawn"] = attack.cooldownAfterSpawn;
                combat["Cooldown Randomization"] = attack.cooldownRandomization;
                combat["Recheck Target Interval"] = attack.recheckTargetInterval;
                combat["Attack Height"] = attack.spawnAttackHeight;

                if (attack.targetPriorities != null && attack.targetPriorities.Count > 0)
                {
                    var priorities = new List<string>();
                    for (int i = 0; i < attack.targetPriorities.Count; i++)
                    {
                        priorities.Add($"Priority {i + 1}: {FormatTargetPriority(attack.targetPriorities[i])}");
                    }
                    combat["Target Priorities"] = priorities;
                }
            }

            // Movement (for units and enemies)
            if (type != "Building")
            {
                var movement = obj.GetComponent<PathfindMovementPlayerunit>();
                if (movement != null)
                {
                    var movementDict = entry.Categories["Movement"];
                    movementDict["Base Speed"] = movement.movementSpeed;
                    movementDict["Keep Distance"] = movement.keepDistanceOf;
                    movementDict["Max Distance From Home"] = movement.maximumDistanceFromHome;
                    movementDict["Recalculate Path Interval"] = movement.recalculatePathInterval;

                    // Unit-specific movement additions
                    if (type == "PlayerUnit")
                    {
                        movementDict["Daytime Speed Boost"] = movement.speedBoostDuringDaytime;
                        movementDict["Agro Time When Attacked"] = movement.agroTimeWhenAttackedByPlayer;
                    }
                }
            }

            // Type-specific data
            if (type == "PlayerUnit")
            {
                // Display Type
                var display = obj.GetComponent<UnitTypeDisplay>();
                if (display != null)
                {
                    entry.Categories["Display"]["Is Ranged"] = display.ranged;
                }

                // Perks
                var perkHp = obj.GetComponent<PerkHpModifyer>();
                if (perkHp != null)
                {
                    entry.Categories["Perks"][$"HP Multiplier ({perkHp.requiredPerk?.name ?? "Unknown"})"] = perkHp.hpMultiplyer;
                }

                var perkSpeed = obj.GetComponent<PerkSpeedModifyer>();
                if (perkSpeed != null)
                {
                    entry.Categories["Perks"][$"Speed Multiplier ({perkSpeed.requiredPerk?.name ?? "Unknown"})"] = perkSpeed.speedMultiplyer;
                }

                var perkDamage = obj.GetComponent<PerkDamageAutoAttackModifyer>();
                if (perkDamage != null)
                {
                    entry.Categories["Perks"]["Damage Multiplier"] = perkDamage.damageMultiplyer;
                }

                // Parent info (if it's a respawner)
                if (obj.transform.parent != null)
                {
                    var respawner = obj.transform.parent.GetComponent<UnitRespawnerForBuildings>();
                    if (respawner != null)
                    {
                        entry.Categories["Spawning"] = [];
                        entry.Categories["Spawning"]["Respawn Times"] = respawner.timeToRespawnAUnitDependingOnLevel;
                        if (respawner.myBuildSlot != null)
                        {
                            entry.Categories["Spawning"]["Building"] = respawner.myBuildSlot.name;
                        }
                    }
                }
            }
            else if (type == "Building")
            {
                // Production
                var respawner = obj.GetComponent<UnitRespawnerForBuildings>();
                if (respawner != null)
                {
                    entry.Categories["Production"]["Respawn Times"] = respawner.timeToRespawnAUnitDependingOnLevel;
                    if (respawner.myBuildSlot != null)
                    {
                        entry.Categories["Production"]["Building Type"] = respawner.myBuildSlot.name;
                    }
                }
            }

            wikiEntries[entry.Name] = entry;
        }

        private void LogWikiEntry(WikiEntry entry)
        {
            Plugin.Logger.LogInfo($"\n=== {entry.Type}: {entry.Name} ===");
            Plugin.Logger.LogInfo($"Internal Name: {entry.InternalName}");

            if (entry.Tags?.Any() == true)
            {
                var interestingTags = entry.Tags.Where(t => !nonInterestingTags.Contains(t));
                Plugin.Logger.LogInfo($"Tags: {string.Join(", ", interestingTags)}");
            }

            foreach (var category in entry.Categories.Where(c => c.Value.Any()))
            {
                Plugin.Logger.LogInfo($"\n{category.Key}:");
                foreach (var prop in category.Value)
                {
                    if (prop.Value is List<float> respawnTimes)
                    {
                        Plugin.Logger.LogInfo($"- {prop.Key}:");
                        for (int i = 0; i < respawnTimes.Count; i++)
                        {
                            Plugin.Logger.LogInfo($"  Level {i + 1}: {respawnTimes[i]} seconds");
                        }
                    }
                    else if (prop.Value is List<string> stringList)
                    {
                        Plugin.Logger.LogInfo($"- {prop.Key}:");
                        foreach (var item in stringList)
                        {
                            Plugin.Logger.LogInfo($"  {item}");
                        }
                    }
                    else if (prop.Value is Dictionary<string, object> details)
                    {
                        Plugin.Logger.LogInfo($"- {prop.Key}:");
                        foreach (var detail in details)
                        {
                            Plugin.Logger.LogInfo($"  {detail.Key}: {detail.Value}");
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"- {prop.Key}: {prop.Value}");
                    }
                }
            }
        }

        public override void OnDestroy()
        {
            // Find and clean up all Wiki buttons
            // Include inactive objects: the pause menu holding the button is usually closed when this runs.
            var allButtons = Resources.FindObjectsOfTypeAll<ThronefallUIElement>().Where(b => b.gameObject.scene.IsValid());
            foreach (var button in allButtons)
            {
                if (button == null || button.name != "Wiki") continue;

                // Fix navigation - link the buttons above and below this one back up
                var topButton = button.topNav;
                var bottomButton = button.botNav;
                if (topButton != null) topButton.botNav = bottomButton;
                if (bottomButton != null) bottomButton.topNav = topButton;

                // Destroy the Wiki button
                UnityEngine.Object.Destroy(button.gameObject);
            }

            // Find and destroy any Wiki frames
            var wikiFrame = Resources.FindObjectsOfTypeAll<UIFrame>().FirstOrDefault(f => f.name == "Wiki Frame" && f.gameObject.scene.IsValid())?.gameObject;
            if (wikiFrame != null)
            {
                // If the frame is active, close it first
                var frameManager = UnityEngine.Object.FindObjectOfType<UIFrameManager>();
                if (frameManager != null)
                {
                    var activeFrame = AccessTools.Field(typeof(UIFrameManager), "activeFrame").GetValue(frameManager) as UIFrame;

                    if (activeFrame?.gameObject == wikiFrame)
                    {
                        Plugin.Logger.LogInfo("Closing Wiki Frame");
                        frameManager.CloseActiveFrame();
                    }
                }

                // Destroy the frame
                UnityEngine.Object.Destroy(wikiFrame);
            }
        }

        // Helper method to log the hierarchy of a GameObject and its children
        private static void LogGameObjectHierarchy(GameObject obj, int depth)
        {
            if (obj == null) return;

            // Create indentation based on depth
            string indent = new string(' ', depth * 2);

            // Log this object and its components
            Plugin.Logger.LogInfo($"{indent}GameObject: {obj.name} (Active: {obj.activeSelf})");

            // Log all components
            var components = obj.GetComponents<Component>();
            Plugin.Logger.LogInfo($"{indent}  Components: {components.Length}");
            foreach (var component in components)
            {
                if (component == null) continue;
                Plugin.Logger.LogInfo($"{indent}  - {component.GetType().FullName}");

                // Special handling for Image components to log their properties
                // if (component is Image img)
                // {
                //     Plugin.Logger.LogInfo($"{indent}    Color: {img.color}");
                //     Plugin.Logger.LogInfo($"{indent}    Sprite: {img.sprite?.name ?? "null"}");
                //     Plugin.Logger.LogInfo($"{indent}    Type: {img.type}");
                //     Plugin.Logger.LogInfo($"{indent}    Material: {img.material?.name ?? "null"}");
                //     Plugin.Logger.LogInfo($"{indent}    RaycastTarget: {img.raycastTarget}");
                // }

                // // Special handling for RectTransform to log its properties
                // if (component is RectTransform rt)
                // {
                //     Plugin.Logger.LogInfo($"{indent}    AnchorMin: {rt.anchorMin}");
                //     Plugin.Logger.LogInfo($"{indent}    AnchorMax: {rt.anchorMax}");
                //     Plugin.Logger.LogInfo($"{indent}    SizeDelta: {rt.sizeDelta}");
                //     Plugin.Logger.LogInfo($"{indent}    AnchoredPosition: {rt.anchoredPosition}");
                //     Plugin.Logger.LogInfo($"{indent}    Pivot: {rt.pivot}");
                // }

                // // Special handling for CanvasRenderer
                // if (component is CanvasRenderer cr)
                // {
                //     Plugin.Logger.LogInfo($"{indent}    Color: {cr.GetColor()}");
                //     Plugin.Logger.LogInfo($"{indent}    Alpha: {cr.GetAlpha()}");
                // }
            }

            // Log transform info
            Plugin.Logger.LogInfo($"{indent}  Child count: {obj.transform.childCount}");

            // Recursively log all children
            for (int i = 0; i < obj.transform.childCount; i++)
            {
                var child = obj.transform.GetChild(i);
                Plugin.Logger.LogInfo($"{indent}  Child {i}:");
                LogGameObjectHierarchy(child.gameObject, depth + 1);
            }
        }
    }
}
