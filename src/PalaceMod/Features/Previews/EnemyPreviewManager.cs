using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using I2.Loc;

namespace PalaceMod
{
    public class EnemyPreviewManager : BaseManager
    {
        private List<GameObject> createdObjects = new List<GameObject>();

        [HarmonyPatch(typeof(NightPreviewElement))]
        [HarmonyPatch("SetData")]
        private class NightPreviewElement_SetData_Patch
        {
            private static bool Prefix()
            {
                return false; // Skip original method
            }
        }

        [HarmonyPatch(typeof(PauseUILoadoutHelper))]
        [HarmonyPatch("RefreshOnlyEnemyPreview")]
        private class RefreshOnlyEnemyPreview_Patch
        {
            static EnemyPreviewManager Instance => Plugin.Instance.GetManager<EnemyPreviewManager>();
            static readonly TMP_FontAsset font = Resources.Load<TMP_FontAsset>("fonts & materials/cormorantgaramond-light sdf");

            private struct EnemyData
            {
                public WaveEnemyInfo Info;
                public int SpawnedCount;
            }

            private static void Postfix(PauseUILoadoutHelper __instance)
            {
                CreateTitleContainerIfNeeded(__instance);

                var currentPreviewWavenumber = (int)AccessTools.Field(__instance.GetType(), "currentPreviewWavenumber").GetValue(__instance);

                var enemyDataByLocation = GatherEnemyWaveData(currentPreviewWavenumber);

                ModifyEnemyPreviewUI(__instance, enemyDataByLocation, currentPreviewWavenumber);
            }

            private static void CreateTitleContainerIfNeeded(PauseUILoadoutHelper __instance)
            {
                var titleContainer = __instance.transform.Find("Title ");
                var layoutGroup = titleContainer?.GetComponent<VerticalLayoutGroup>();

                if (layoutGroup == null)
                {
                    if (titleContainer == null)
                    {
                        Plugin.Logger.LogError("Title container not found");
                        return;
                    }

                    // Convert the title container into a vertical layout group so we can resize items and they will be properly positioned
                    layoutGroup = titleContainer.gameObject.AddComponent<VerticalLayoutGroup>();
                    layoutGroup.name = "Title ";
                    layoutGroup.spacing = 48;

                    // Get our first menu item's position for reference
                    var menuItems = titleContainer.Find("Menu Items");
                    var rect = menuItems.GetComponent<RectTransform>();
                    float topPadding = -rect?.localPosition.y * (1 - rect?.pivot.y) ?? 0;
                    layoutGroup.padding = new RectOffset(0, 0, Mathf.RoundToInt(topPadding), 0);

                    // Exclude tooltip from layout
                    var tooltip = titleContainer.Find("Tooltip Background");
                    if (tooltip != null)
                    {
                        var tooltipLayout = tooltip.GetComponent<LayoutElement>();
                        if (tooltipLayout == null)
                        {
                            tooltipLayout = tooltip.gameObject.AddComponent<LayoutElement>();
                            tooltipLayout.name = "ModTooltipLayout";
                        }
                        tooltipLayout.ignoreLayout = true;
                    }
                }
            }

            private static Dictionary<string, List<EnemyData>> GatherEnemyWaveData(int waveNumber)
            {
                if (EnemySpawner.instance == null || waveNumber < 0 || waveNumber >= EnemySpawner.instance.waves.Count)
                {
                    return [];
                }

                var wave = EnemySpawner.instance.waves[waveNumber];

                // Dictionary to track the first spawn we find for each enemy type
                // This will be used to get sprite and description data
                var enemyDataByLocationRaw = new Dictionary<string, Dictionary<(string name, bool elite), (Spawn firstSpawn, int planned, int spawned)>>();

                foreach (var spawn in wave.spawns)
                {
                    var spawnPos = spawn.spawnLine.name;
                    var component = spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>();
                    if (component == null) continue;

                    if (!enemyDataByLocationRaw.ContainsKey(spawnPos))
                    {
                        enemyDataByLocationRaw[spawnPos] = [];
                    }

                    var enemyKey = (component.UnitName, spawn.eliteEnemies);

                    if (!enemyDataByLocationRaw[spawnPos].ContainsKey(enemyKey))
                    {
                        // Store the first spawn we find for this enemy type for description and sprite
                        enemyDataByLocationRaw[spawnPos][enemyKey] = (spawn, 0, 0);
                    }
                    var current = enemyDataByLocationRaw[spawnPos][enemyKey];
                    enemyDataByLocationRaw[spawnPos][enemyKey] = (
                        current.firstSpawn,
                        current.planned + spawn.count,
                        current.spawned + spawn.SpawnedUnits
                    );
                }

                // Now create the WaveEnemyInfo objects from the aggregated data
                var enemyDataByLocation = new Dictionary<string, List<EnemyData>>();
                foreach (var locationEntry in enemyDataByLocationRaw)
                {
                    string location = locationEntry.Key;
                    enemyDataByLocation[location] = [];

                    foreach (var enemyEntry in locationEntry.Value)
                    {
                        var data = enemyEntry.Value;
                        var spawn = data.firstSpawn;
                        var component = spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>();

                        var waveEnemyInfo = new WaveEnemyInfo
                        {
                            enemyIcon = component.sprite,
                            enemyCount = data.planned,
                            enemyName = component.UnitName,
                            enemyDescription = component.UnitDescription,
                            eliteEnemy = spawn.eliteEnemies
                        };

                        // During the night, add info about units spawned so far to the tooltip
                        if (data.planned > 0 &&
                            DayNightCycle.Instance != null &&
                            DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night)
                        {
                            waveEnemyInfo.enemyDescription += "\n\n";
                            waveEnemyInfo.enemyDescription += $"<b>{data.spawned}</b> spawned of <b>{data.planned}</b> total";
                        }

                        enemyDataByLocation[location].Add(new EnemyData
                        {
                            Info = waveEnemyInfo,
                            SpawnedCount = data.spawned
                        });
                    }
                }

                return enemyDataByLocation;
            }

            private static void ModifyEnemyPreviewUI(PauseUILoadoutHelper __instance, Dictionary<string, List<EnemyData>> enemyDataByLocation, int currentPreviewWavenumber)
            {
                var enemyGrid = AccessTools.Field(__instance.GetType(), "enemyGrid").GetValue(__instance) as List<ThronefallUIElement>;
                var addTFUIEnemeyMethod = AccessTools.Method(__instance.GetType(), "AddTFUIEnemey");
                var addIncomePreviewMethod = AccessTools.Method(__instance.GetType(), "AddIncomePreview");

                if (EnemySpawner.instance == null) return;

                // Clear existing
                enemyGrid.Clear();
                foreach (Transform child in __instance.enemyPreview.transform)
                {
                    Object.Destroy(child.gameObject);
                }

                // Add a row for each spawn location
                foreach (var location in enemyDataByLocation)
                {
                    var groupContainer = new GameObject("SpawnGroup", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
                    groupContainer.transform.SetParent(__instance.enemyPreview.transform, false);
                    Instance.createdObjects.Add(groupContainer);

                    var hlg = groupContainer.GetComponent<HorizontalLayoutGroup>();
                    hlg.spacing = 10;

                    // Add a label for the location
                    var textObj = new GameObject("SpawnLocation", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
                    textObj.transform.SetParent(groupContainer.transform, false);
                    Instance.createdObjects.Add(textObj);

                    var text = textObj.GetComponent<TextMeshProUGUI>();
                    text.text = $"{location.Key}:";
                    text.fontSize = 20;
                    text.font = font;
                    text.color = new Color32(200, 166, 116, 255);
                    text.alignment = TextAlignmentOptions.Left;
                    text.enableWordWrapping = false;

                    // Make sure the text object actually has a width so it doesn't overlap
                    var textLayoutElement = textObj.GetComponent<LayoutElement>();
                    textLayoutElement.minWidth = text.preferredWidth;

                    // Add a grid for the enemies at that location
                    var enemyGridObj = new GameObject("EnemyGrid", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
                    enemyGridObj.transform.SetParent(groupContainer.transform, false);
                    Instance.createdObjects.Add(enemyGridObj);

                    var grid = enemyGridObj.GetComponent<GridLayoutGroup>();
                    grid.spacing = new Vector2(5, 5);
                    grid.cellSize = new Vector2(50, 50);
                    grid.constraint = GridLayoutGroup.Constraint.FixedRowCount;
                    grid.constraintCount = 1;

                    // Add enemies to the grid
                    foreach (var enemyData in location.Value)
                    {
                        // Add the enemy UI element
                        addTFUIEnemeyMethod.Invoke(__instance, [grid, enemyData.Info]);

                        // Update the count text if we're in night mode
                        if (grid != null && grid.transform.childCount > 0 &&
                            DayNightCycle.Instance != null &&
                            DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night)
                        {
                            var lastChild = grid.transform.GetChild(grid.transform.childCount - 1);
                            if (lastChild != null)
                            {
                                // Update enemy count text
                                var textComponent = lastChild.GetComponentInChildren<TextMeshProUGUI>(true);
                                if (textComponent != null && enemyData.Info.enemyCount > 0)
                                {
                                    textComponent.text = $"{enemyData.SpawnedCount}|{enemyData.Info.enemyCount}";
                                }
                            }
                        }
                    }
                }

                // Add a final row for the income and difficulty icons
                var bottomGrid = new GameObject("BottomGrid", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
                bottomGrid.transform.SetParent(__instance.enemyPreview.transform, false);
                Instance.createdObjects.Add(bottomGrid);

                var bottomGridLayout = bottomGrid.GetComponent<GridLayoutGroup>();
                bottomGridLayout.spacing = new Vector2(5, 5);
                bottomGridLayout.cellSize = new Vector2(50, 50);
                bottomGridLayout.constraint = GridLayoutGroup.Constraint.FixedRowCount;
                bottomGridLayout.constraintCount = 1;

                // Add the income and difficulty icons to the bottom grid
                addIncomePreviewMethod.Invoke(__instance, new object[] { bottomGridLayout });

                var waveInfo = EnemySpawner.GetWaveInfoByNumber(currentPreviewWavenumber);
                if (waveInfo.difficultyMulti != 1f)
                {
                    WaveEnemyInfo waveEnemyInfo = new WaveEnemyInfo();
                    waveEnemyInfo.enemyIcon = EnemySpawner.instance.difficultySprite;
                    waveEnemyInfo.enemyCount = Mathf.RoundToInt((waveInfo.difficultyMulti - 1f) / 0.1f);
                    waveEnemyInfo.enemyName = LocalizationManager.GetTranslation("Menu/DifficultyBoost", true, 0, true, false, null, null, true);
                    waveEnemyInfo.enemyDescription = "+ " + Mathf.Round((waveInfo.difficultyMulti - 1f) * 100f).ToString() + "% " + LocalizationManager.GetTranslation("Menu/AdditionalHealth", true, 0, true, false, null, null, true);
                    WaveEnemyInfo waveEnemyInfo2 = waveEnemyInfo;
                    waveEnemyInfo2.enemyDescription = string.Concat(new string[]
                    {
                        waveEnemyInfo2.enemyDescription,
                        "\n+ ",
                        Mathf.Round((waveInfo.difficultyMulti - 1f) * 50f).ToString(),
                        "% ",
                        LocalizationManager.GetTranslation("Menu/AdditionalDamage", true, 0, true, false, null, null, true)
                    });
                    addTFUIEnemeyMethod.Invoke(__instance, new object[] { bottomGridLayout, waveEnemyInfo });
                }

                // Set a max of 3 rows before wrapping
                var enemyPreviewLayout = __instance.enemyPreview.GetComponent<GridLayoutGroup>();
                enemyPreviewLayout.constraint = GridLayoutGroup.Constraint.FixedRowCount;
                enemyPreviewLayout.constraintCount = 3;
                enemyPreviewLayout.spacing = new Vector2(200, 10);

                // Set the enemy preview to anchor to the left
                var rectTransform = __instance.enemyPreview.GetComponent<RectTransform>();
                rectTransform.anchorMin = new Vector2(0, 0.5f);
                rectTransform.anchorMax = new Vector2(0, 0.5f);
                rectTransform.pivot = new Vector2(0, 0.5f);
                rectTransform.anchoredPosition = new Vector2(10, 0); // Add some padding from the left edge
            }
        }
    }
}
