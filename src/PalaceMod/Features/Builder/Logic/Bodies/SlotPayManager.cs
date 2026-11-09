using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;

/*
    Item costs on a build slot's own build prompt (docs/builder-engine.md, "attach"): an attached slot's `pay`
    ([{ "var": "$wood", "amount": 2, "label": "wood", "level": 1 }]) is paid in level variables together with the
    slot's gold, for the level being bought (an item's `level`: 1 = the build, 2 = the first upgrade...; none = every
    level). The game's tooltip gets a "Costs 2 wood + 3 stone" line. When the king is short of any item the press is
    refused before a coin is filled (a notice, `interactRefused` on the slot with event.name = what's short), so nothing
    is paid; otherwise the items are taken when the build or upgrade happens (after the upgrade-choice popup, so
    cancelling it costs nothing). Only the king's own purchase pays: logic `building` actions, `palace.py build`
    (free) and savestate replays don't.
    Infrastructure: acts only while a logic world runs.
*/
namespace PalaceMod
{
    public class SlotPayManager : BaseManager
    {
        private static readonly AccessTools.FieldRef<BuildingInteractor, BuildingInteractor.InteractionState> State =
            AccessTools.FieldRefAccess<BuildingInteractor, BuildingInteractor.InteractionState>("currentState");
        private static readonly AccessTools.FieldRef<BuildingInteractor, bool> Started =
            AccessTools.FieldRefAccess<BuildingInteractor, bool>("interactionStarted");
        private static readonly AccessTools.FieldRef<BuildingInteractor, bool> Complete =
            AccessTools.FieldRefAccess<BuildingInteractor, bool>("interactionComplete");
        private static readonly AccessTools.FieldRef<BuildingInteractor, bool> Waiting =
            AccessTools.FieldRefAccess<BuildingInteractor, bool>("isWaitingForChoice");

        /// <summary>Purchases on their way through the upgrade (choice) step: the slot that levels up, its level before,
        /// and the items to take once it has.</summary>
        private static readonly ConditionalWeakTable<BuildSlot, Pending> pending = new();

        private sealed class Pending
        {
            public int Level;
            public List<BuilderInteractor.PayItem> Pays;
        }

        /// <summary>The item costs of <paramref name="slot"/>'s next build or upgrade (empty without a logic world or
        /// an attached `pay`).</summary>
        internal static List<BuilderInteractor.PayItem> Next(BuildSlot slot)
        {
            if (LogicWorld.Current == null || slot == null || !slot.CanBeUpgraded) return [];
            var e = slot.GetComponent<BuilderEntity>()?.Entity;
            if (e?.Def?["pay"] == null) return [];
            return BuilderInteractor.ReadPays(e.Def["pay"], e, slot.Level + 1);
        }

        private static bool Refuse(BuildingInteractor bi, List<string> shortOf, PlayerInteraction player)
        {
            if (shortOf.Count == 0) return false;
            Notify.Show($"Not enough: {string.Join(", ", shortOf)} needed", NotifyKind.Warning, 3f, "builder-interact");
            var slot = bi.targetBuilding;
            BodyDev.Raise("interactRefused", slot.GetComponent<BuilderEntity>()?.Entity ?? LogicGame.EntityOf(slot.gameObject),
                LogicGame.EntityOf(player.gameObject), slot.NextUpgradeOrBuildCost, slot.transform.position.ToVec(), string.Join(", ", shortOf));
            if (bi.costDisplay != null) bi.costDisplay.Deny();
            return true;
        }

        [HarmonyPatch(typeof(BuildingInteractor))]
        private static class Interactor
        {
            /// <summary>Short of an item: refuse the press before any coin is filled.</summary>
            [HarmonyPatch(nameof(BuildingInteractor.InteractionBegin)), HarmonyPrefix]
            private static bool Begin(BuildingInteractor __instance, PlayerInteraction player)
            {
                if (LogicWorld.Current == null || Waiting(__instance) || State(__instance) != BuildingInteractor.InteractionState.Upgrade) return true;
                var pays = Next(__instance.targetBuilding);
                return pays.Count == 0 || !Refuse(__instance, BuilderInteractor.Short(pays), player);
            }

            /// <summary>The coins are in: check the items again (they may have changed while held), then note what to
            /// take once the build or upgrade happens.</summary>
            [HarmonyPatch(nameof(BuildingInteractor.InteractionHold)), HarmonyPrefix]
            private static bool Hold(BuildingInteractor __instance, PlayerInteraction player)
            {
                if (LogicWorld.Current == null || State(__instance) != BuildingInteractor.InteractionState.Upgrade || Complete(__instance)
                    || !Started(__instance) || Waiting(__instance) || __instance.costDisplay == null || !__instance.costDisplay.CompletelyFilled) return true;
                var slot = __instance.targetBuilding;
                var pays = Next(slot);
                if (pays.Count == 0) return true;
                if (Refuse(__instance, BuilderInteractor.Short(pays), player))
                {
                    __instance.costDisplay.CancelFill(player);
                    Started(__instance) = false;
                    return false;
                }
                var target = slot.ActivatorUpgradesThis && slot.ActivatorBuilding != null ? slot.ActivatorBuilding : slot;
                pending.Remove(target);
                pending.Add(target, new Pending { Level = target.Level, Pays = pays });
                return true;
            }
        }

        /// <summary>The build or upgrade happened (or was cancelled: no choice): take the items noted for it.</summary>
        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnUpgradeChoiceComplete))]
        private static class Upgraded
        {
            private static void Postfix(BuildSlot __instance, Choice _choiceMade)
            {
                if (!pending.TryGetValue(__instance, out var p)) return;
                pending.Remove(__instance);
                if (_choiceMade != null && __instance.Level > p.Level) BuilderInteractor.Charge(p.Pays);
            }
        }

        /// <summary>The game's build tooltip: a line with the item costs.</summary>
        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.ReturnTooltip))]
        private static class Tooltip
        {
            private static void Postfix(BuildSlot __instance, ref string __result)
            {
                if (string.IsNullOrEmpty(__result)) return;
                var pays = Next(__instance);
                if (pays.Count == 0) return;
                int gold = __instance.NextUpgradeOrBuildCost;
                string text = BuilderInteractor.CostText(gold, pays, goldWord: true);
                __result += $"\n<style=\"Tooltip Default\">Costs {text}";
            }
        }
    }
}
