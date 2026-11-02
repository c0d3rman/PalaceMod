using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

/*
    TreasuryUI (the HUD treasure chest and its gold counter) holds at most PHYSICAL_COIN_LIMIT (200) physical coins. The
    counter text is only refreshed while the chest is shown, and the chest is only shown when a physical coin is added or
    removed (or while paying). Above 200 gold no coins are added or removed, so gaining or spending gold leaves the
    counter frozen at an old value until something else opens the chest.

    Its Update also keeps the chest's render camera on during the day while the coin count differs from the balance,
    comparing against the unclamped balance. Above 200 gold that's never equal, so the hidden chest renders every daytime
    frame.

    This patch shows the chest (as a coin would) whenever the balance changes but no coin will move, and compares the
    coin count against the clamped balance.
*/
namespace PalaceMod
{
    public class GoldCounterFixManager : BaseManager
    {
        public override string FeatureSection => "Gold counter fix";
        public override string FeatureDescription => "Keeps the gold counter up to date above 200 gold, where it otherwise freezes, and stops the hidden chest rendering every frame.";

        private static readonly AccessTools.FieldRef<TreasuryUI, bool> forceRender = AccessTools.FieldRefAccess<TreasuryUI, bool>("forceRender");
        private static readonly AccessTools.FieldRef<TreasuryUI, bool> overrideActivation = AccessTools.FieldRefAccess<TreasuryUI, bool>("overrideActivation");
        private static readonly AccessTools.FieldRef<TreasuryUI, float> activationCounter = AccessTools.FieldRefAccess<TreasuryUI, float>("activationCounter");
        private static readonly AccessTools.FieldRef<TreasuryUI, TreasuryUI.AnimationState> currentState = AccessTools.FieldRefAccess<TreasuryUI, TreasuryUI.AnimationState>("currentState");
        private static readonly AccessTools.FieldRef<TreasuryUI, List<GameObject>> instantiatedCoins = AccessTools.FieldRefAccess<TreasuryUI, List<GameObject>>("instantiatedCoins");
        private static readonly AccessTools.FieldRef<TreasuryUI, PlayerInteraction> targetPlayer = AccessTools.FieldRefAccess<TreasuryUI, PlayerInteraction>("targetPlayer");

        private static TreasuryUI lastTreasury;
        private static int lastBalance;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("gold", "gold <amount>          set the player's gold to <amount>", args =>
            {
                if (args.Length != 1 || !int.TryParse(args[0], out int target) || target < 0) throw new ArgumentException("usage: gold <amount>");
                var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                int delta = target - player.Balance;
                if (delta > 0) player.AddCoin(delta);
                else if (delta < 0) player.SpendCoins(-delta);
                GameplayImpact.Mark("Dev: set gold");
                return $"gold {player.Balance}";
            });
            DevCommands.Register("coins", "coins <amount>         spawn <amount> physical income coins that fly to the king", args =>
            {
                if (args.Length != 1 || !int.TryParse(args[0], out int amount) || amount <= 0) throw new ArgumentException("usage: coins <amount>");
                var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                var spawner = UnityEngine.Object.FindObjectOfType<CoinSpawner>() ?? throw new InvalidOperationException("no active CoinSpawner in the scene");
                spawner.TriggerCoinSpawn(amount, player);
                GameplayImpact.Mark("Dev: spawn coins");
                return $"spawning {amount} coins from {spawner.name}";
            });
        }

        protected override void OnFeatureEnabled() => lastTreasury = null;

        [HarmonyPatch(typeof(TreasuryUI), "Update")]
        private class TreasuryUI_Update_Patch
        {
            private static bool Prefix(TreasuryUI __instance)
            {
                var player = targetPlayer(__instance);
                var coins = instantiatedCoins(__instance);
                if (player == null || coins == null || DayNightCycle.Instance == null) return true;

                int balance = player.Balance;
                int physicalTarget = Mathf.Min(balance, TreasuryUI.PHYSICAL_COIN_LIMIT);
                if (lastTreasury != __instance)
                {
                    lastTreasury = __instance;
                    lastBalance = balance;
                }
                else if (balance != lastBalance)
                {
                    lastBalance = balance;
                    // No coin will be added or removed for this change, so show the chest the way a coin would.
                    if (coins.Count == physicalTarget) activationCounter(__instance) = __instance.activationLifetime;
                }

                // Vanilla's early-out, but with the balance clamped like the rest of Update clamps it.
                bool coinsPending = DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day && coins.Count != physicalTarget;
                bool shouldBeActive = activationCounter(__instance) > 0f || overrideActivation(__instance);
                if (forceRender(__instance) || shouldBeActive || currentState(__instance) != TreasuryUI.AnimationState.Off || coinsPending)
                    return true;

                __instance.renderCamera.SetActive(false);
                return false;
            }
        }
    }
}
