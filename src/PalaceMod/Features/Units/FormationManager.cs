using UnityEngine;
using System;
using System.Collections.Generic;
using HarmonyLib;
using Rewired;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;

namespace PalaceMod
{
    public class FormationManager : BaseManager
    {
        public override string FeatureSection => "Formations";
        public override string FeatureDescription => "Lets you cycle unit formations (line, circle, dot, semicircle) and place commanded units one at a time.";

        private Keybind cycleFormationKey;
        private Keybind placeOneUnitKey;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            cycleFormationKey = Keybinds.Register(FeatureSection, "cycle", "Cycle formation", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F7),
                "Switches the formation your commanded units are placed in.");
            placeOneUnitKey = Keybinds.Register(FeatureSection, "place-one", "Place one unit", new BepInEx.Configuration.KeyboardShortcut(KeyCode.O),
                "Places only the first of your commanded units; hold to make it hold position.");
        }

        protected override void OnFeatureDisabled() => placeOnlyOneUnit = false;

        public enum FormationType
        {
            Normal,
            Line,
            Circle,
            Dot,
            Semicircle
        }

        private FormationType currentFormation = FormationType.Normal;

        // For press-and-hold tracking
        private float timeSincePlaceOne = 0f;
        private bool switchedToHold = false;

        // Flag to indicate if we should place only one unit on next placement (used for PlaceCommandedUnitsAndCalculateTargetPositions)
        public static bool placeOnlyOneUnit = false;

        public override void Update()
        {
            if (CommandUnits.instance == null) return; // not in a level

            if (cycleFormationKey.Down)
            {
                CycleFormation();
            }

            // Place one unit key pressed
            if (placeOneUnitKey.Down)
            {
                placeOnlyOneUnit = true;
                CommandUnits.instance.PlaceCommandedUnitsAndCalculateTargetPositions(false);
            }

            // Place one unit key released
            if (placeOneUnitKey.Up)
            {
                // Clear the buffer so press-and-holding O again doesn't cause the last unit to hold position
                AccessTools.Field(typeof(CommandUnits), "playerUnitsCommandingBuffer").SetValue(CommandUnits.instance, new List<PathfindMovementPlayerunit>());
            }

            // Place one unit key held
            if (placeOneUnitKey.Held)
            {
                timeSincePlaceOne += Time.deltaTime;
                if (timeSincePlaceOne > CommandUnits.instance.holdToHoldPositionTime && !switchedToHold)
                {
                    switchedToHold = true;
                    CommandUnits.instance.MakeUnitsInBufferHoldPosition();
                }
            }
            else
            {
                timeSincePlaceOne = 0f;
                switchedToHold = false;
            }
        }

        private void CycleFormation()
        {
            currentFormation = (FormationType)(((int)currentFormation + 1) % Enum.GetValues(typeof(FormationType)).Length);
            string formationName = Enum.GetName(typeof(FormationType), currentFormation);
            Notify.Show($"Formation: {formationName}", key: "formation");
        }

        public void SetUnitPosition(PathfindMovementPlayerunit unit, Vector3 position)
        {
            unit.HomePosition = position;
            if (DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day)
            {
                unit.transform.position = position;
            }
        }

        // Helper function for the transpiler to call so we don't have to write IL code for it
        public static void ModifyToBePlaced()
        {
            if (!placeOnlyOneUnit) return;

            // Keep only the first unit
            var toBePlacedField = AccessTools.Field(typeof(CommandUnits), "toBePlaced");
            var toBePlaced = (List<PathfindMovementPlayerunit>)toBePlacedField.GetValue(CommandUnits.instance);
            if (toBePlaced.Count > 0)
            {
                toBePlaced = toBePlaced.Take(1).ToList();
                toBePlacedField.SetValue(CommandUnits.instance, toBePlaced);
            }

            // Reset this flag for internals of CommandUnits
            AccessTools.Field(typeof(CommandUnits), "commandUnitsButtonLast").SetValue(CommandUnits.instance, false);
        }

        [HarmonyPatch(typeof(CommandUnits), "PlaceCommandedUnitsAndCalculateTargetPositions")]
        private class CommandUnits_PlaceCommandedUnits_Patch
        {
            static FormationManager Instance => Plugin.Instance.GetManager<FormationManager>();

            // Transpiler that inserts a call to ModifyToBePlaced() after the foreach loop immediately after toBePlaced.Clear()
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var toBePlacedClearFound = false;
                var codeInserted = false;

                foreach (var instruction in instructions)
                {
                    // First continue until toBePlaced.Clear()
                    if (!toBePlacedClearFound)
                    {
                        if (instruction.Calls(AccessTools.Method(typeof(List<PathfindMovementPlayerunit>), "Clear")))
                        {
                            toBePlacedClearFound = true;
                        }
                        yield return instruction;
                        continue;
                    }

                    // Then find the end of the foreach loop and insert our code
                    if (!codeInserted && instruction.opcode == OpCodes.Endfinally)
                    {
                        yield return instruction;

                        // Call FormationManager.ModifyToBePlaced()
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FormationManager), "ModifyToBePlaced"));
                        codeInserted = true;
                        continue;
                    }

                    yield return instruction;
                }
            }

            // Postfix mostly handles placing units in formations
            private static void Postfix(CommandUnits __instance, List<PathfindMovementPlayerunit> ___toBePlaced, float ___unitDistanceFromEachOther)
            {
                // If we just placed one unit, don't do formations
                if (placeOnlyOneUnit)
                {
                    placeOnlyOneUnit = false;

                    // Fix a glitch - if we placed the last unit, we should stop commanding
                    var playerUnitsCommanding = (List<PathfindMovementPlayerunit>)AccessTools.Field(typeof(CommandUnits), "playerUnitsCommanding").GetValue(__instance);
                    if (playerUnitsCommanding.Count == 0)
                    {
                        AccessTools.Field(typeof(CommandUnits), "commanding").SetValue(__instance, false);
                    }
                    return;
                }

                // If there are no units to place, don't do formations
                if (___toBePlaced == null || ___toBePlaced.Count == 0) return;

                // Handle formations
                var player = PlayerManager.Instance.RegisteredPlayers[0];
                float unitSpacing = ___unitDistanceFromEachOther;
                Vector3 playerForward = player.meshParent.forward;

                switch (Instance.currentFormation)
                {
                    case FormationType.Normal:
                        return;
                    case FormationType.Line:
                        PlaceInLine(___toBePlaced, player, playerForward, unitSpacing);
                        break;
                    case FormationType.Circle:
                        PlaceInCircle(___toBePlaced, player, unitSpacing);
                        break;
                    case FormationType.Dot:
                        PlaceInDot(___toBePlaced, player);
                        break;
                    case FormationType.Semicircle:
                        PlaceInSemicircle(___toBePlaced, player, playerForward, unitSpacing);
                        break;
                }
            }

            private static void PlaceInLine(List<PathfindMovementPlayerunit> units, PlayerMovement player, Vector3 playerForward, float spacing)
            {
                float totalWidth = (units.Count - 1) * spacing;
                Vector3 lineStart = player.transform.position - (playerForward * totalWidth / 2f);

                for (int i = 0; i < units.Count; i++)
                {
                    Vector3 targetPos = lineStart + (playerForward * i * spacing);
                    Instance.SetUnitPosition(units[i], targetPos);
                }
            }

            private static void PlaceInCircle(List<PathfindMovementPlayerunit> units, PlayerMovement player, float spacing)
            {
                float radius = spacing * 2f; // Make circle radius proportional to unit spacing
                float angleStep = 360f / units.Count;

                for (int i = 0; i < units.Count; i++)
                {
                    float angle = i * angleStep * Mathf.Deg2Rad;
                    Vector3 offset = new Vector3(Mathf.Sin(angle) * radius, 0, Mathf.Cos(angle) * radius);
                    Vector3 targetPos = player.transform.position + offset;
                    Instance.SetUnitPosition(units[i], targetPos);
                }
            }

            private static void PlaceInDot(List<PathfindMovementPlayerunit> units, PlayerMovement player)
            {
                foreach (var unit in units)
                {
                    Instance.SetUnitPosition(unit, player.transform.position);
                }
            }

            private static void PlaceInSemicircle(List<PathfindMovementPlayerunit> units, PlayerMovement player, Vector3 playerForward, float spacing)
            {
                float radius = spacing * 2f;
                float angleStep = 180f / (units.Count - 1); // 180 degrees for semicircle

                for (int i = 0; i < units.Count; i++)
                {
                    float angle = (-90f + (i * angleStep)) * Mathf.Deg2Rad; // Start from -90 degrees to center the semicircle
                    Vector3 right = Vector3.Cross(playerForward, Vector3.up);
                    Vector3 offset = playerForward * Mathf.Cos(angle) + right * Mathf.Sin(angle);
                    Vector3 targetPos = player.transform.position + (offset * radius);
                    Instance.SetUnitPosition(units[i], targetPos);
                }
            }
        }
    }
}
