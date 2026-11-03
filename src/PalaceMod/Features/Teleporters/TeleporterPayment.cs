using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Paying for a teleporter the way a build slot is paid for: a copy of a building's CostDisplay (the row of coin slots
    over the spot) appears, and while the key is held the slots fill one by one, each one taking a coin from the king
    with PlayerInteraction.SpendCoins (the coin-spend effect on the king) and playing the slot's own fill feedback and
    sound. Releasing early is CostDisplay.CancelFill: the paid coins fly back to the king as the game's Coin pickups.

    The game fills its slots at its own pace (CostDisplay.FillUp); here each slot fills over an equal share of the hold
    instead, so the last coin lands as the hold completes. Coins flying back would count as newly earned gold
    (Coin -> PlayerInteraction.AddCoin raises networth), so the networth they will add is taken off up front.

    If no building in the level has a cost display to copy, the coins are taken without the display.
*/
namespace PalaceMod
{
    internal sealed class TeleporterPayment
    {
        private static readonly AccessTools.FieldRef<CostDisplay, List<Coinslot>> activeSlots =
            AccessTools.FieldRefAccess<CostDisplay, List<Coinslot>>("currentlyActiveCoinslots");
        private static readonly AccessTools.FieldRef<CostDisplay, int> filledCoins = AccessTools.FieldRefAccess<CostDisplay, int>("currentlyFilledCoins");
        private static readonly AccessTools.FieldRef<PlayerInteraction, int> networth = AccessTools.FieldRefAccess<PlayerInteraction, int>("networth");
        private static readonly AccessTools.FieldRef<PlayerInteraction, int> balance = AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");

        private readonly int cost;
        private readonly CostDisplay display;
        private int paid;
        private bool denied, done;

        public int Paid => paid;

        public TeleporterPayment(Vector3 at, int cost, float duration, Scene scene)
        {
            this.cost = cost;
            if (cost <= 0) return;
            var template = Template();
            if (template == null) return;

            var source = template.costDisplay;
            var copy = Object.Instantiate(source.gameObject);
            copy.name = "PalaceMod Teleporter CostDisplay";
            SceneManager.MoveGameObjectToScene(copy, scene);
            // Same height over the spot as over the building it was copied from.
            copy.transform.SetPositionAndRotation(at + (source.transform.position - template.transform.position), source.transform.rotation);
            display = copy.GetComponent<CostDisplay>();
            // The copy brings the source's slot objects along, outside its own (fresh) pool.
            foreach (var slot in copy.GetComponentsInChildren<Coinslot>(true)) Object.DestroyImmediate(slot.gameObject);
            // Start computes the row layout; run it now so the first layout is final (no re-layout next frame).
            AccessTools.Method(typeof(CostDisplay), "Start").Invoke(display, null);
            display.UpdateDisplay(cost, dirty: false);
            ThronefallAudioManager.SetCoinDisplayFillTime(duration);
            ThronefallAudioManager.Oneshot(ThronefallAudioManager.AudioOneShot.CoinslotInteractionStart);
            Log.For<TeleporterManager>().Debug($"Cost display from {template.name} at offset {source.transform.position - template.transform.position}");
        }

        /// <summary>The game's Coin pickup, to send gold back to the king; null if the level has no cost display.</summary>
        public static Coin CoinPrefab => Template()?.costDisplay.coinPrefab;

        /// <summary>Fills the slots up to <paramref name="progress"/> (0..1) of the hold. False while the king can't pay.</summary>
        public bool Advance(float progress, PlayerInteraction player)
        {
            if (done || cost <= 0) return true;
            var slots = display != null ? activeSlots(display) : null;
            float filled = Mathf.Clamp01(progress) * cost;
            while (paid < cost)
            {
                float want = Mathf.Clamp01(filled - paid);
                if (want <= 0f) break;
                if (player.Balance < 1)
                {
                    if (!denied && display != null) display.Deny();
                    denied = true;
                    return false;
                }
                bool full;
                if (slots != null && paid < slots.Count)
                {
                    var slot = slots[paid];
                    full = slot.AddFill(Mathf.Max(0f, want - slot.CurrentFill01) + (want >= 1f ? 0.001f : 0f), paid == cost - 1);
                }
                else full = want >= 1f;
                if (!full) break;
                player.SpendCoins(1);
                paid++;
            }
            if (display != null) filledCoins(display) = paid;
            return true;
        }

        /// <summary>Released early: the paid coins fly back.</summary>
        public void Cancel(PlayerInteraction player)
        {
            if (done) return;
            done = true;
            if (display == null)
            {
                if (paid > 0 && player != null) Refund(player, paid);
                return;
            }
            if (player != null)
            {
                networth(player) -= paid; // the returning coins add it back
                display.CancelFill(player);
            }
            ThronefallAudioManager.Instance?.MakeSureCoinFillSoundIsNotPlayingAnymore();
            Dismiss();
        }

        public void Complete()
        {
            if (done) return;
            done = true;
            if (display == null) return;
            display.OnCompletion();
            ThronefallAudioManager.Instance?.MakeSureCoinFillSoundIsNotPlayingAnymore();
            Dismiss();
        }

        /// <summary>Gone with its level (or the feature switched off): nothing to animate, just give back what was paid.</summary>
        public void Abort(PlayerInteraction player)
        {
            if (done) return;
            done = true;
            if (paid > 0 && player != null) Refund(player, paid);
            ThronefallAudioManager.Instance?.MakeSureCoinFillSoundIsNotPlayingAnymore();
            if (display != null) Object.Destroy(display.gameObject);
        }

        /// <summary>Sends <paramref name="amount"/> coins from <paramref name="from"/> to the king, as a refund (not income).</summary>
        public static void FlyBack(Vector3 from, int amount, PlayerInteraction player)
        {
            if (amount <= 0 || player == null) return;
            var prefab = CoinPrefab;
            if (prefab == null)
            {
                Refund(player, amount);
                return;
            }
            networth(player) -= amount;
            for (int i = 0; i < amount; i++)
            {
                float angle = (i + 0.5f) / amount * Mathf.PI * 2f;
                var spot = from + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * (TeleporterPad.Radius * 0.6f) + Vector3.up * 0.5f;
                Object.Instantiate(prefab, spot, Quaternion.identity).SetTarget(player);
            }
        }

        // A refund without the coin animation: balance only, so it isn't counted as earned gold.
        private static void Refund(PlayerInteraction player, int amount)
        {
            balance(player) += amount;
            player.onBalanceGain.Invoke(amount);
        }

        private void Dismiss()
        {
            display.Hide();
            Object.Destroy(display.gameObject, 2f); // after its hide animation
        }

        private static BuildingInteractor Template()
        {
            var tags = TagManager.instance;
            if (tags == null) return null;
            foreach (var interactor in tags.playerBuildingInteractors)
                if (interactor != null && interactor.costDisplay != null && interactor.costDisplay.coinslotPrefab != null) return interactor;
            return null;
        }
    }
}
