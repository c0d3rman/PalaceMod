using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

/*
    Upgrade info: shows what buildings and units have been upgraded with, and what's still to come.

    - Near a building: a compact panel beside the game's tooltip listing the choices taken, the next upgrade's
      options (greyed with their cost when you can't afford them yet) and optionally the later levels' options.
    - Pause screen: an overview of every building's upgrades.
    - Units: upgraded units get stars on their icons (pause-menu unit list, the command-mode icons over their heads),
      the pause-menu list keeps differently upgraded units of one type apart, and its hover text names the upgrades.

    Read-only with respect to upgrade state.
*/
namespace PalaceMod
{
    public partial class UpgradesViewManager : BaseManager
    {
        public override string FeatureSection => "Upgrade info";
        public override string FeatureDescription => "Shows which upgrades buildings and units have, and which are still available.";

        internal ConfigEntry<bool> NearBuilding;
        internal ConfigEntry<bool> ShowFuture;
        internal ConfigEntry<bool> PauseOverview;
        internal ConfigEntry<bool> MarkUnits;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var cfg = plugin.Config;
            NearBuilding = cfg.Bind(FeatureSection, "Near buildings", true, new ConfigDescription(
                "Standing next to a building shows the upgrades it has and the options for its next one, even when you can't afford it yet."));
            ShowFuture = cfg.Bind(FeatureSection, "Show later levels", true, new ConfigDescription(
                "The building panel also lists the options of the levels after the next one."));
            PauseOverview = cfg.Bind(FeatureSection, "Pause overview", true, new ConfigDescription(
                "Adds an Upgrades button to the pause menu that lists every building's upgrades."));
            MarkUnits = cfg.Bind(FeatureSection, "Mark upgraded units", true, new ConfigDescription(
                "Stars on unit icons show how many times their building was upgraded; the pause menu lists differently upgraded units separately and names their upgrades."));

            NearBuilding.SettingChanged += (_, _) => { if (!NearBuilding.Value) HidePanel(); };
            MarkUnits.SettingChanged += (_, _) => { if (!MarkUnits.Value) ClearUnitMarks(); };
            PauseOverview.SettingChanged += (_, _) => RefreshPauseButton();

            RegisterDevCommands();
        }

        public override void Start() => StartPauseOverview();

        public override void Update()
        {
            UpdatePanel();
            UpdateUnitMarks();
        }

        protected override void OnFeatureDisabled()
        {
            HidePanel();
            ClearUnitMarks();
            RefreshPauseButton();
        }

        protected override void OnFeatureEnabled() => RefreshPauseButton();

        public override void OnDestroy()
        {
            DestroyPanel();
            ClearUnitMarks();
        }

        // ---------- Test hooks ----------

        private void RegisterDevCommands()
        {
            DevCommands.Register("upgv.gold", "upgv.gold <n>          add n coins to the king's purse (test helper)", args =>
            {
                var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                int n = args.Length > 0 ? int.Parse(args[0]) : 100;
                GameplayImpact.Mark("Dev: gold");
                if (n >= 0) player.AddCoin(n); else player.SpendCoins(Math.Min(-n, player.Balance));
                return $"balance {player.Balance}";
            });
            DevCommands.Register("upgv.upgrade", "upgv.upgrade <slot|nearest> [branch]  free build/upgrade of one level (test helper)", args =>
            {
                var slot = FindSlot(args.Length > 0 ? args[0] : "nearest");
                int branch = args.Length > 1 ? int.Parse(args[1]) : 0;
                slot = UpgradeInfo.Owner(slot);
                if (!slot.CanBeUpgraded) throw new InvalidOperationException($"{slot.name} can't be upgraded now (level {slot.Level})");
                var next = slot.OwnUpgrades[slot.Level];
                var choice = next.upgradeBranches[Mathf.Clamp(branch, 0, next.upgradeBranches.Count - 1)].choiceDetails;
                GameplayImpact.Mark("Dev: free upgrade");
                // Same as picking `choice` in the game's popup: OnUpgradeChoiceComplete for the selected upgrade step.
                if (next.upgradeBranches.Count == 1)
                {
                    slot.TryToBuildOrUpgradeAndPay(null, _presentChoice: false);
                }
                else
                {
                    HarmonyLib.Traverse.Create(slot).Field("upgradeSelected").SetValue(next);
                    slot.OnUpgradeChoiceComplete(choice);
                    if (slot.buildingInteractor != null) slot.buildingInteractor.UpdateInteractionState();
                }
                return $"{slot.name} ({UpgradeInfo.BuildingName(slot)}) now level {slot.Level}, applied [{string.Join(",", slot.AppliedUpgrades)}]";
            });
            DevCommands.Register("upgv.slots", "upgv.slots             list build slots with level, distance and upgrade info", _ =>
            {
                var king = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                var sb = new StringBuilder();
                foreach (var s in UnityEngine.Object.FindObjectsOfType<BuildSlot>().Where(s => !s.ActivatorUpgradesThis)
                             .OrderBy(s => Vector3.Distance(s.transform.position, king.transform.position)))
                {
                    var taken = UpgradeInfo.TakenChoices(s).Select(t => UpgradeInfo.ChoiceName(s, t.choice));
                    sb.AppendLine($"{s.name} [{UpgradeInfo.BuildingName(s)}] lvl {s.Level}/{s.OwnUpgrades.Count} " +
                                  $"d={Vector3.Distance(s.transform.position, king.transform.position):F0} canUp={s.CanBeUpgraded} " +
                                  $"cost={s.NextUpgradeOrBuildCost} taken=[{string.Join(", ", taken)}]");
                }
                return sb.ToString();
            });
            DevCommands.Register("upgv.goto", "upgv.goto <slot>       teleport the king next to a build slot", args =>
            {
                var slot = FindSlot(args.Length > 0 ? args[0] : throw new ArgumentException("slot name needed"));
                var king = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                var target = slot.Interactor != null ? slot.Interactor.transform.position : slot.transform.position;
                var dir = (king.transform.position - target); dir.y = 0;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.back;
                var pos = target + dir.normalized * 2.5f;
                var mover = king.GetComponent<CharacterController>();
                if (mover != null) mover.enabled = false;
                king.transform.position = new Vector3(pos.x, king.transform.position.y, pos.z);
                if (mover != null) mover.enabled = true;
                return $"king at {king.transform.position}";
            });
        }

        private static BuildSlot FindSlot(string name)
        {
            var king = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
            var slots = UnityEngine.Object.FindObjectsOfType<BuildSlot>();
            if (name.Equals("nearest", StringComparison.OrdinalIgnoreCase))
                return slots.Where(s => s.CanBeUpgraded)
                    .OrderBy(s => Vector3.Distance(s.transform.position, king.transform.position)).FirstOrDefault()
                    ?? throw new ArgumentException("no upgradable slot");
            return slots.Where(s => s.name.Equals(name, StringComparison.OrdinalIgnoreCase) || s.buildingName.Equals(name, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(s => Vector3.Distance(s.transform.position, king.transform.position)).FirstOrDefault()
                   ?? throw new ArgumentException($"no slot named {name}");
        }
    }
}
