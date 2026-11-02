using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace PalaceMod.Experimental
{
    public static class SquadManager
    {
        private static readonly Dictionary<KeyCode, List<TaggedObject>> savedSquads = new Dictionary<KeyCode, List<TaggedObject>>()
        {
            { KeyCode.Alpha5, new List<TaggedObject>() },
            { KeyCode.Alpha6, new List<TaggedObject>() },
            { KeyCode.Alpha7, new List<TaggedObject>() },
            { KeyCode.Alpha8, new List<TaggedObject>() },
            { KeyCode.Alpha9, new List<TaggedObject>() },
            { KeyCode.Alpha0, new List<TaggedObject>() }
        };

        [HarmonyPatch(typeof(PlayerMovement))]
        [HarmonyPatch("Update")]
        public static class PlayerMovement_Update_Patch
        {
            private static void Prefix(PlayerMovement __instance)
            {
                try
                {
                    foreach (var key in savedSquads.Keys)
                    {
                        if (Input.GetKeyDown(key))
                        {
                            HandleSquadKey(key, __instance);
                            break;
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogError($"Error in squad management: {e}");
                }
            }

            private static void HandleSquadKey(KeyCode key, PlayerMovement __instance)
            {
                var playerManager = GameObject.FindObjectOfType<PlayerManager>();
                if (playerManager == null || playerManager.RegisteredPlayers == null || playerManager.RegisteredPlayers.Length == 0)
                    return;

                var player = playerManager.RegisteredPlayers[0];
                var commandComponent = player.GetComponent<CommandUnits>();
                if (commandComponent == null)
                    return;

                var type = commandComponent.GetType();
                var playerUnitsCommandingField = type.GetField("playerUnitsCommanding", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var resultsField = type.GetField("results", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

                if (playerUnitsCommandingField == null || resultsField == null)
                    return;

                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                {
                    // Save or unset squad
                    var selectedUnits = playerUnitsCommandingField.GetValue(commandComponent) as List<PathfindMovementPlayerunit>;
                    var squad = savedSquads[key];
                    squad.Clear();

                    if (selectedUnits != null && selectedUnits.Count > 0)
                    {
                        foreach (var unit in selectedUnits)
                        {
                            if (unit != null && unit.gameObject != null)
                            {
                                var taggedObject = unit.GetComponent<TaggedObject>();
                                if (taggedObject != null)
                                {
                                    squad.Add(taggedObject);
                                }
                            }
                        }

                        if (squad.Count > 0)
                        {
                            Plugin.Instance.ShowMessage($"Set squad {key.ToString().Last()}");
                        }
                    }
                    else
                    {
                        Plugin.Instance.ShowMessage($"Unset squad {key.ToString().Last()}");
                    }
                }
                else
                {
                    // Clear current selection
                    var results = resultsField.GetValue(commandComponent) as List<TaggedObject>;
                    if (results != null)
                    {
                        results.Clear();

                        // If we have a saved squad, select it
                        var squad = savedSquads[key];
                        if (squad.Count > 0)
                        {
                            // Filter out any destroyed units
                            squad.RemoveAll(unit => unit == null || unit.gameObject == null);

                            if (squad.Count > 0)
                            {
                                results.AddRange(squad);
                            }
                        }

                        // Always call TryToSelectUnits to update the selection
                        var selectMethod = type.GetMethod("TryToSelectUnits",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);

                        if (selectMethod != null)
                        {
                            selectMethod.Invoke(commandComponent, new object[] { results });
                        }
                    }
                }
            }
        }
    }
}
