using HarmonyLib;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using I2.Loc;
using UnityEngine;
using TMPro;
using System.Linq;
using System;

namespace PalaceMod
{
    public class IncomeDisplayManager : BaseManager
    {
        private struct BuildingIdentifierParams
        {
            public string BaseName;
            public BuildSlot BuildSlot;
            public List<string> Choices;
            public int GoldIncome;
            public int? FieldCount;
            public int? BoatCount;

            public BuildingIdentifierParams(string baseName, BuildSlot buildSlot, List<string> choices, int goldIncome)
            {
                BaseName = baseName;
                BuildSlot = buildSlot;
                Choices = choices;
                GoldIncome = goldIncome;
                FieldCount = null;
                BoatCount = null;
            }
        }

        [HarmonyPatch(typeof(TFUIIncomeDisplay), "GetDescription")]
        private class TFUIIncomeDisplay_GetDescription_Patch
        {
            static IncomeDisplayManager Instance => Plugin.Instance.GetManager<IncomeDisplayManager>();

            private static string GetTranslatedName(BuildSlot buildSlot) =>
                LocalizationManager.GetTranslation(buildSlot.LOCIDENTIFIER_NAME, true, 0, true, false, null, null, true);

            private static List<string> GetBuildingChoices(BuildSlot buildSlot)
            {
                var choices = new List<string>();
                for (int i = 0; i < buildSlot.AppliedUpgrades.Count; i++)
                {
                    var upgrade = buildSlot.OwnUpgrades[i];
                    if (upgrade.upgradeBranches.Count > 1)
                    {
                        var choice = upgrade.upgradeBranches[buildSlot.AppliedUpgrades[i]].choiceDetails;
                        if (choice != null)
                        {
                            string choiceName = LocalizationManager.GetTranslation(buildSlot.GET_LOCIDENTIFIER_CHOICENAME(choice), true, 0, true, false, null, null, true);
                            if (!string.IsNullOrEmpty(choiceName))
                            {
                                choices.Add(choiceName);
                            }
                        }
                    }
                }
                return choices;
            }

            private static string GetBuildingFullIdentifier(BuildingIdentifierParams p)
            {
                string buildingName = p.BaseName;
                if (p.BuildSlot.Level > 1)
                {
                    buildingName += new string('+', p.BuildSlot.Level - 1);
                }

                var parts = new List<string>();
                if (p.Choices.Count > 0)
                {
                    parts.AddRange(p.Choices);
                }
                if (p.BaseName == "Mill" && p.FieldCount.HasValue && p.FieldCount.Value > 0)
                {
                    parts.Add($"& {p.FieldCount.Value} Field{(p.FieldCount.Value != 1 ? "s" : "")}");
                }
                if (parts.Count > 0)
                {
                    buildingName += $" ({string.Join(", ", parts)})";
                }

                if (p.BaseName == "Gold Mine")
                {
                    buildingName += $" ({p.GoldIncome}g)";
                }
                else if (p.BaseName == "Harbor" && p.BoatCount.HasValue)
                {
                    buildingName += $" ({p.BoatCount.Value} boat{(p.BoatCount.Value != 1 ? "s" : "")})";
                }

                return buildingName;
            }

            private static void Postfix(TFUIIncomeDisplay __instance, ref string __result)
            {
                var buildingsHeader = LocalizationManager.GetTranslation("Menu/Income From Buildings", true, 0, true, false, null, null, true);
                var pattern = $"({buildingsHeader}.*?<sprite name=arrow_right> <sprite name=coin><style=Body Numerals>\\d+</style>)(\\n|$)";
                var match = Regex.Match(__result, pattern);
                if (!match.Success) return;

                // Get wave info for gold mine calculations
                var pauseUI = GameObject.FindObjectOfType<PauseUILoadoutHelper>();
                if (pauseUI == null)
                {
                    Plugin.Logger.LogError("PauseUILoadoutHelper not found");
                    return;
                }
                var trueWave = (int)AccessTools.Field(typeof(PauseUILoadoutHelper), "currentTrueWaveNumver").GetValue(pauseUI);
                var previewWave = (int)AccessTools.Field(typeof(PauseUILoadoutHelper), "currentPreviewWavenumber").GetValue(pauseUI);
                var waveGap = previewWave - trueWave;

                // Get max mine income and harbor boats based on equipped perks
                var currentlyEquipped = (List<Equippable>)AccessTools.Field(typeof(PerkManager), "currentlyEquipped").GetValue(PerkManager.instance);
                int maxMineIncome = currentlyEquipped.Any(e => e.LOCIDENTIFIER_NAME == "Equippable/Indestructible Mines") ? 7 : 6;
                int maxHarborBoats = currentlyEquipped.Any(e => e.LOCIDENTIFIER_NAME == "Equippable/Big Harbours") ? 7 : 5;

                // Collect all built buildings and count fields per mill
                var buildings = new List<BuildSlot>();
                var fields = new HashSet<BuildSlot>();
                var millFieldCounts = new Dictionary<BuildSlot, int>();

                foreach (var tag in new[] { TagManager.ETag.Building, TagManager.ETag.Bridge })
                {
                    var taggedObjects = new List<TaggedObject>();
                    TagManager.instance.FindAllTaggedObjectsWithTag(taggedObjects, tag);
                    foreach (var obj in taggedObjects)
                    {
                        var buildSlot = obj.GetComponentInParent<BuildSlot>();
                        if (buildSlot?.State == BuildSlot.BuildingState.Built)
                        {
                            buildings.Add(buildSlot);

                            // Count fields per mill while we're here
                            var buildingName = GetTranslatedName(buildSlot);
                            if (buildingName.Contains("Field"))
                            {
                                var mill = buildSlot.ActivatorBuilding;
                                if (mill != null)
                                {
                                    fields.Add(buildSlot);
                                    millFieldCounts[mill] = millFieldCounts.GetValueOrDefault(mill) + 1;
                                }
                            }
                        }
                    }
                }

                // Group buildings by their full identifier
                var buildingGroups = new Dictionary<string, List<(string name, BuildSlot slot, int income)>>();
                foreach (var buildSlot in buildings)
                {
                    var goldIncome = buildSlot.GoldIncome;
                    string buildingName = GetTranslatedName(buildSlot);
                    var choices = GetBuildingChoices(buildSlot);
                    string fullIdentifier;

                    if (buildingName.Contains("Field") && buildSlot.ActivatorBuilding != null) continue;

                    var identifierParams = new BuildingIdentifierParams(buildingName, buildSlot, choices, goldIncome);

                    if (buildingName == "Gold Mine")
                    {
                        goldIncome -= waveGap;
                        if (goldIncome <= 0 || goldIncome > maxMineIncome) continue;
                        identifierParams.GoldIncome = goldIncome;
                        fullIdentifier = GetBuildingFullIdentifier(identifierParams);
                    }
                    else if (buildingName == "Harbor")
                    {
                        var goldPerBoat = buildSlot.Level > 1 ? 2 : 1;
                        var baseBoatCount = Math.Min(maxHarborBoats, goldIncome / goldPerBoat);
                        var activeBoatCount = Math.Min(maxHarborBoats, baseBoatCount + waveGap);
                        goldIncome = activeBoatCount * goldPerBoat;

                        identifierParams.GoldIncome = goldIncome;
                        identifierParams.BoatCount = activeBoatCount;
                        fullIdentifier = GetBuildingFullIdentifier(identifierParams);
                    }
                    else
                    {
                        if (buildingName == "Mill")
                        {
                            identifierParams.FieldCount = millFieldCounts.GetValueOrDefault(buildSlot);
                        }
                        fullIdentifier = GetBuildingFullIdentifier(identifierParams);
                    }

                    if (goldIncome == 0) continue;

                    if (!buildingGroups.ContainsKey(fullIdentifier))
                    {
                        buildingGroups[fullIdentifier] = [];
                    }
                    buildingGroups[fullIdentifier].Add((buildingName, buildSlot, goldIncome));
                }

                // Generate the output
                var sortedGroups = buildingGroups
                    .Select(kvp => (
                        identifier: kvp.Key,
                        baseName: kvp.Value[0].name,
                        income: kvp.Value[0].income,
                        group: kvp.Value
                    ))
                    .OrderBy(x => x.baseName)
                    .ThenByDescending(x => x.income)
                    .ToList();

                var replacement = match.Groups[1].Value + "\n";
                foreach (var (identifier, baseName, income, group) in sortedGroups)
                {
                    if (group.Count > 1)
                    {
                        var totalIncome = group.Sum(b => b.income);
                        replacement += $"    {group.Count}x {identifier} <sprite name=arrow_right> <sprite name=coin><style=Body Numerals>{totalIncome} ({group.Count}x{income})</style>\n";
                    }
                    else
                    {
                        replacement += $"    {identifier} <sprite name=arrow_right> <sprite name=coin><style=Body Numerals>{income}</style>\n";
                    }
                }

                __result = Regex.Replace(__result, pattern, replacement);
            }
        }
    }
}
