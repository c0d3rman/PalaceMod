using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Player-placed teleporters.

    During the day, press the place key to put a teleporter down where the king stands (for gold, or free); press it
    again on a teleporter to pick it up for a refund. Standing on a linked teleporter for a moment sends the king to the
    next one, and the units following him come along.

    This reuses the game's own mine-shaft mechanic (MineShaft): standing in the entrance for a short channel
    (0.75 s natively) moves the king with PlayerMovement.TeleportTo to the next shaft in a fixed cycle, then both ends
    rest briefly. Teleporters cycle in the order they were placed, so with two they form a pair. Following units are
    moved the way CommandUnits does it in daytime: set the position on the navmesh and clear the path.

    Placed units with a destination stay on the game's pathfinding; routing them through teleporters isn't robust, so
    only units following the king teleport. Enemies ignore teleporters, and can't use or destroy them.

    Positions are stored in the match save (the game's own key/value store), so they survive retry-day and continue.
*/
namespace PalaceMod
{
    public class TeleporterManager : BaseManager
    {
        public override string FeatureSection => "Teleporters";
        public override string FeatureDescription =>
            "Place teleporters during the day and stand on one to jump to the next. Units following you come along. Placing one makes the run unranked.";

        // Save bookkeeping must run while disabled (see SaveRun_Patch); nothing else is patched.
        protected override bool UnpatchWhenDisabled => false;

        private const string SaveGuid = "PalaceMod";
        private const string SaveKey = "teleporters";
        private const float MinSpacing = 6f;
        private const float StepOffRadius = TeleporterPad.Radius + 0.4f;

        private ConfigEntry<int> cost, maxCount;
        private ConfigEntry<float> channelTime, cooldown;
        private ConfigEntry<bool> bringFollowers, useAtNight, hintShown;
        private Keybind placeKey;

        private readonly List<TeleporterPad> pads = [];
        private Scene padScene;
        private float channel, cooldownLeft;
        private TeleporterPad channelPad, arrivedPad;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var config = plugin.Config;
            cost = config.Bind(FeatureSection, "Cost", 5, new ConfigDescription(
                "Gold per teleporter, refunded when you pick it up.",
                new AcceptableValueRange<int>(0, 20), new SettingTags { ZeroLabel = "Free", Unit = " gold" }));
            maxCount = config.Bind(FeatureSection, "Teleporters", 2, new ConfigDescription(
                "How many you can place. Each one sends you to the next in the order placed, so two make a pair.",
                new AcceptableValueRange<int>(2, 6)));
            channelTime = config.Bind(FeatureSection, "Channel time", 0.75f, new ConfigDescription(
                "How long to stand on a teleporter before it fires. The game's mine shafts use 0.75 s.",
                new AcceptableValueRange<float>(0.25f, 3f), new SettingTags { Step = 0.25f, Unit = " s" }));
            cooldown = config.Bind(FeatureSection, "Cooldown", 3f, new ConfigDescription(
                "Rest time for the whole network after a jump.",
                new AcceptableValueRange<float>(0f, 30f), new SettingTags { Step = 1f, Unit = " s", ZeroLabel = "None" }));
            bringFollowers = config.Bind(FeatureSection, "Bring followers", true, new ConfigDescription(
                "Units following the king teleport with him. Placed units always walk."));
            useAtNight = config.Bind(FeatureSection, "Usable at night", true, new ConfigDescription(
                "Whether teleporters work during the night's battle, not only during the day."));
            hintShown = config.Bind(FeatureSection, "Hint shown", false, new ConfigDescription(
                "Whether the one-time placement hint was shown.", null, new SettingTags { Hidden = true }));

            placeKey = Keybinds.Register(FeatureSection, "place", "Place teleporter", new KeyboardShortcut(KeyCode.T),
                "During the day, places a teleporter where the king stands, or picks up the one he stands on.");

            RegisterDevCommands();
            SaveStates.RegisterState("teleporters", CaptureState, RestoreState);
        }

        // ---- Savestates -------------------------------------------------------------------------------------------

        internal sealed class SavedNetwork
        {
            public string network;
            public float cooldown;
        }

        private SavedNetwork CaptureState() => PlayerMovement.instance == null ? null : new SavedNetwork
        {
            network = CurrentNetwork(),
            cooldown = cooldownLeft,
        };

        // Replaces the network the reload brought back (the dawn one from the match save) with the captured one.
        private void RestoreState(SavedNetwork saved)
        {
            var king = PlayerMovement.instance;
            if (king == null) return;
            Clear();
            pendingLoad = null;
            padScene = king.gameObject.scene;
            hintPending = false;
            var list = Parse(saved.network);
            foreach (var (at, g) in list) Add(at, g);
            cooldownLeft = saved.cooldown;
            armAfterAppear = true;
            if (list.Count > 0) GameplayImpact.Mark("Teleporters");
            Log.For<TeleporterManager>().Debug($"Restored {list.Count} from a savestate");
        }

        protected override void OnFeatureDisabled()
        {
            foreach (var pad in pads) pad.SetVisible(false);
            channel = 0f;
            channelPad = null;
        }

        protected override void OnFeatureEnabled()
        {
            foreach (var pad in pads) pad.SetVisible(true);
            armAfterAppear = true;
        }

        // When pads appear under the king (level start, re-enabling), he has to step off before one fires.
        private bool armAfterAppear;

        // ---- Frame ------------------------------------------------------------------------------------------------

        public override void Update()
        {
            var king = PlayerMovement.instance;
            var day = DayNightCycle.Instance;
            if (king == null || day == null || PlayerInteraction.instance == null)
            {
                Clear();
                return;
            }
            if (padScene != king.gameObject.scene)
            {
                Clear();
                padScene = king.gameObject.scene;
                hintPending = true;
                SpawnLoaded();
                armAfterAppear = true;
            }
            if (armAfterAppear)
            {
                armAfterAppear = false;
                arrivedPad = PadAt(king.transform.position, StepOffRadius);
            }

            bool night = day.CurrentTimestate == DayNightCycle.Timestate.Night;
            bool inMatch = LocalGamestate.Instance != null && LocalGamestate.Instance.CurrentState == LocalGamestate.State.InMatch;
            if (hintPending && inMatch && !night) ShowHint();

            if (placeKey.Down && inMatch) OnPlaceKey(king, night);

            cooldownLeft = Mathf.Max(0f, cooldownLeft - Time.deltaTime);
            bool usable = inMatch && pads.Count >= 2 && (!night || useAtNight.Value) && KingAlive(king);
            UpdateChannel(king, usable);

            foreach (var pad in pads)
            {
                bool ready = usable && cooldownLeft <= 0f && pad != arrivedPad;
                pad.Tick(pads.Count >= 2, ready, pad == channelPad ? channel : 0f, night);
            }
        }

        private void UpdateChannel(PlayerMovement king, bool usable)
        {
            Vector3 at = king.transform.position;
            if (arrivedPad != null && Flat(at - arrivedPad.Position) > StepOffRadius) arrivedPad = null;

            var on = usable && cooldownLeft <= 0f ? PadAt(at, TeleporterPad.Radius * 0.75f) : null;
            if (on == null || on == arrivedPad)
            {
                channel = 0f;
                channelPad = null;
                return;
            }
            if (on != channelPad)
            {
                channelPad = on;
                channel = 0f;
            }
            channel += Time.deltaTime / Mathf.Max(0.05f, channelTime.Value);
            if (channel >= 1f) Jump(king, on);
        }

        private void Jump(PlayerMovement king, TeleporterPad from)
        {
            var to = pads[(pads.IndexOf(from) + 1) % pads.Count];
            from.Burst();
            king.TeleportTo(to.Position + Vector3.up * 0.05f);
            to.Burst();

            int moved = bringFollowers.Value ? MoveFollowers(from, to) : 0;
            channel = 0f;
            channelPad = null;
            arrivedPad = to;
            cooldownLeft = cooldown.Value;
            GameplayImpact.Mark("Teleporters");
            Log.For<TeleporterManager>().Debug($"Jumped {pads.IndexOf(from) + 1} -> {pads.IndexOf(to) + 1}" + (moved > 0 ? $" with {moved} units" : ""));
        }

        // Followers keep their spot relative to the king, snapped to their navmesh, the way the game drops them in daytime.
        private static int MoveFollowers(TeleporterPad from, TeleporterPad to)
        {
            var cu = CommandUnits.instance;
            // Not cu.commanding: that is only set once the command key is released, while units join as it's held.
            if (cu == null) return 0;
            int moved = 0;
            foreach (var unit in cu.PlayerUnitsCommanding)
            {
                if (unit == null || !unit.FollowingPlayer || unit.hp == null || !unit.hp.Alive) continue;
                Vector3 relative = unit.transform.position - from.Position;
                relative.y = 0f;
                if (relative.magnitude > 8f) relative = relative.normalized * 8f;
                unit.transform.position = unit.GetNearestGroundPosition(to.Position + relative);
                unit.ClearCurrentPath();
                moved++;
            }
            return moved;
        }

        // ---- Placing ----------------------------------------------------------------------------------------------

        private void OnPlaceKey(PlayerMovement king, bool night)
        {
            if (!KingAlive(king)) return;
            Vector3 at = king.transform.position;
            var on = PadAt(at, TeleporterPad.Radius);
            if (night)
            {
                Notify.Show("Teleporters can only be placed or picked up during the day.", NotifyKind.Warning, key: "teleporter");
                return;
            }
            if (on != null)
            {
                Remove(on, refund: true);
                return;
            }
            if (pads.Count >= maxCount.Value)
            {
                Notify.Show($"All {maxCount.Value} teleporters are placed. Stand on one and press {KeyLabel()} to pick it up.", NotifyKind.Warning, key: "teleporter");
                return;
            }
            if (pads.Any(p => Flat(p.Position - at) < MinSpacing))
            {
                Notify.Show("Too close to another teleporter.", NotifyKind.Warning, key: "teleporter");
                return;
            }
            var player = PlayerInteraction.instance;
            if (player.Balance < cost.Value)
            {
                Notify.Show($"A teleporter costs {cost.Value} gold.", NotifyKind.Warning, key: "teleporter");
                return;
            }
            if (cost.Value > 0) player.SpendCoins(cost.Value);
            var pad = Add(at, paidFor: cost.Value);
            arrivedPad = pad; // don't fire under the king's feet straight away
            GameplayImpact.Mark("Teleporters");
            string costText = cost.Value > 0 ? $" for {cost.Value} gold" : "";
            Notify.Show(pads.Count == 1
                ? $"Teleporter placed{costText}. Place another to link them."
                : $"Teleporter {pads.Count} placed{costText}. Stand on one to travel.", NotifyKind.Success, key: "teleporter");
        }

        private readonly Dictionary<TeleporterPad, int> paid = [];

        private TeleporterPad Add(Vector3 at, int paidFor)
        {
            var sparkle = CommandUnits.instance != null ? CommandUnits.instance.dropWaypointFx : null;
            var pad = new TeleporterPad(at, sparkle);
            SceneManager.MoveGameObjectToScene(pad.Root, padScene);
            pad.SetVisible(IsEnabled);
            pad.Burst();
            pads.Add(pad);
            paid[pad] = paidFor;
            Log.For<TeleporterManager>().Info($"Placed #{pads.Count} at {at}");
            return pad;
        }

        private void Remove(TeleporterPad pad, bool refund)
        {
            int amount = paid.TryGetValue(pad, out var p) ? p : 0;
            pads.Remove(pad);
            paid.Remove(pad);
            if (channelPad == pad) channelPad = null;
            if (arrivedPad == pad) arrivedPad = null;
            pad.Destroy();
            if (refund && amount > 0) Refund(amount);
            if (refund) Notify.Show(amount > 0 ? $"Teleporter picked up, {amount} gold refunded." : "Teleporter picked up.", key: "teleporter");
        }

        // AddCoin would also count the gold as newly earned in the end-of-run stats; a refund isn't income.
        private static readonly AccessTools.FieldRef<PlayerInteraction, int> balanceRef = AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");

        private static void Refund(int amount)
        {
            var player = PlayerInteraction.instance;
            if (player == null) return;
            balanceRef(player) += amount;
            player.onBalanceGain.Invoke(amount);
        }

        private void Clear()
        {
            foreach (var pad in pads) pad.Destroy();
            pads.Clear();
            paid.Clear();
            channelPad = arrivedPad = null;
            channel = cooldownLeft = 0f;
            padScene = default;
        }

        private bool hintPending;

        private void ShowHint()
        {
            hintPending = false;
            if (hintShown.Value || placeKey.Shortcut.MainKey == KeyCode.None) return;
            hintShown.Value = true;
            Notify.Show($"New: press {KeyLabel()} during the day to place a teleporter, then stand on it to travel.", seconds: 7f, key: "teleporter");
        }

        // ---- Save and load ----------------------------------------------------------------------------------------

        private string Serialize() => Serialize(pads.Select(p => (p.Position, paid.TryGetValue(p, out var g) ? g : 0)));

        private static string Serialize(IEnumerable<(Vector3 at, int gold)> entries) => string.Join(";", entries.Select(e =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##},{3}", e.at.x, e.at.y, e.at.z, e.gold)));

        // The current level's network, even while the feature is off and its pads were never spawned.
        private string CurrentNetwork()
        {
            if (padScene == PlayerMovement.instance.gameObject.scene) return Serialize();
            return pendingLoad != null ? Serialize(pendingLoad) : "";
        }

        private static List<(Vector3, int)> Parse(string text)
        {
            var result = new List<(Vector3, int)>();
            if (string.IsNullOrEmpty(text)) return result;
            foreach (var part in text.Split(';'))
            {
                var f = part.Split(',');
                if (f.Length < 3) continue;
                if (!float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
                int g = f.Length > 3 && int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gi) ? gi : 0;
                result.Add((new Vector3(x, y, z), g));
            }
            return result;
        }

        private static List<(Vector3, int)> pendingLoad; // from the match save, waiting for the level to start

        private void SpawnLoaded()
        {
            if (pendingLoad == null) return;
            var list = pendingLoad;
            pendingLoad = null;
            foreach (var (at, g) in list) Add(at, g);
            if (list.Count > 0)
            {
                GameplayImpact.Mark("Teleporters");
                Log.For<TeleporterManager>().Info($"Restored {list.Count} from the match save");
            }
        }

        // Only the dawn checkpoint (LocalMatchSaveLoad.Save) records the network, so it stays in step with the saved
        // gold; other writes of the file (the score after a defeat) keep the dawn value. These patches stay applied while
        // the feature is off, so saving with it disabled doesn't drop a network that was paid for.
        private static bool checkpoint;

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "Save")]
        private class Checkpoint_Patch
        {
            private static void Prefix() => checkpoint = true;
            private static void Finalizer() => checkpoint = false;
        }

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private class SaveRun_Patch
        {
            private static void Prefix()
            {
                var manager = Feature<TeleporterManager>.Instance;
                if (!checkpoint || manager == null || MatchSaveLoadHandler.CurrentSave == null || PlayerMovement.instance == null) return;
                MatchSaveLoadHandler.SaveValue(SaveGuid, SaveKey, manager.CurrentNetwork());
            }
        }

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "TryLoad")]
        private class TryLoad_Patch
        {
            private static void Postfix()
            {
                pendingLoad = null;
                if (!MatchSaveLoadHandler.IsLoadingPermitted) return;
                string text = null;
                if (MatchSaveLoadHandler.TryLoadValue(SaveGuid, SaveKey, ref text)) pendingLoad = Parse(text);
            }
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private TeleporterPad PadAt(Vector3 at, float radius)
        {
            TeleporterPad best = null;
            float bestDistance = radius;
            foreach (var pad in pads)
            {
                float d = Flat(pad.Position - at);
                if (d <= bestDistance && Mathf.Abs(pad.Position.y - at.y) < 2f)
                {
                    best = pad;
                    bestDistance = d;
                }
            }
            return best;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private static bool KingAlive(PlayerMovement king)
        {
            var hp = king.GetComponent<Hp>();
            return hp == null || !hp.KnockedOut;
        }

        private string KeyLabel() => placeKey.Shortcut.MainKey == KeyCode.None ? "the teleporter key" : placeKey.Shortcut.ToString();

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("tp", "tp place|pickup|list|goto <n>|clear|save   drive teleporters (place/pickup act at the king)", args =>
            {
                var king = PlayerMovement.instance ?? throw new InvalidOperationException("not in a level");
                if (!IsEnabled) throw new InvalidOperationException("Teleporters is disabled");
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
                switch (sub)
                {
                    case "place":
                    case "pickup":
                        OnPlaceKey(king, DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night);
                        return List(king);
                    case "goto":
                    {
                        int i = args.Length > 1 ? int.Parse(args[1]) - 1 : 0;
                        if (i < 0 || i >= pads.Count) throw new ArgumentException("no such teleporter");
                        king.TeleportTo(pads[i].Position + Vector3.up * 0.05f);
                        return List(king);
                    }
                    case "clear":
                        foreach (var pad in pads.ToList()) Remove(pad, refund: true);
                        return List(king);
                    case "save":
                        return Serialize();
                    case "hint":
                        hintShown.Value = false;
                        hintPending = true;
                        return "";
                    default:
                        return List(king);
                }
            });
        }

        private string List(PlayerMovement king) =>
            $"king {king.transform.position} gold {PlayerInteraction.instance?.Balance} cooldown {cooldownLeft:0.0}s; " +
            (pads.Count == 0 ? "no teleporters" : string.Join("; ", pads.Select((p, i) => $"#{i + 1} {p.Position} ({Flat(p.Position - king.transform.position):0.0} m)")));
    }
}
