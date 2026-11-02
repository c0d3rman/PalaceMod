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
            StartPauseOverview();
        }

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
            DevCommands.Register("upgv.units", "upgv.units             print the pause menu's unit list (name, count, stars, hover text)", _ =>
            {
                if (PlayerInteraction.instance == null) throw new InvalidOperationException("not in a level");
                // PlayerUnitsInfo lives on the pause frame and only registers once that has been opened.
                var source = PlayerUnitsInfo.instance != null ? PlayerUnitsInfo.instance : Resources.FindObjectsOfTypeAll<PlayerUnitsInfo>().FirstOrDefault()
                             ?? throw new InvalidOperationException("no PlayerUnitsInfo");
                var info = source.GetUnitsInfo();
                return string.Join("\n", info.Units.Select(u =>
                    $"{u.unitName} x{u.unitCount} stars={(infoBadges.TryGetValue(u, out var b) ? b.Stars : 0)} hp={u.maxHP}: {u.unitDescription.Replace("\n", " / ")}"));
            });
            DevCommands.Register("upgv.showtypes", "upgv.showtypes [off]   show the command-mode unit icons as if commanding (test helper)", args =>
            {
                if (CommandUnits.instance == null) throw new InvalidOperationException("not in a level");
                forceUnitTypes = !(args.Length > 0 && args[0] == "off");
                return forceUnitTypes ? "showing" : "hidden";
            });
            DevCommands.Register("upgv.peek", "upgv.peek <dy>         shift the pause menu up by dy pixels to screenshot its bottom rows (0 resets)", args =>
            {
                var frame = GameWidgets.PauseFrame ?? throw new InvalidOperationException("no pause menu");
                float dy = args.Length > 0 ? float.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                var p = frame.transform.localPosition;
                frame.transform.localPosition = new Vector3(p.x, dy, p.z);
                return $"pause menu at y={dy}";
            });
            DevCommands.Register("upgv.goto", "upgv.goto <slot>       teleport the king next to a build slot", args =>
            {
                var slot = FindSlot(args.Length > 0 ? string.Join(" ", args) : throw new ArgumentException("slot name needed"));
                var king = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
                var collider = slot.Interactor != null ? slot.Interactor.GetComponent<Collider>() : null;
                var target = collider != null ? collider.bounds.center : slot.transform.position;
                // Of eight spots around the building, take one where it's the nearest interactor.
                Vector3 pos = target + Vector3.back * 2.5f;
                for (int i = 0; i < 8; i++)
                {
                    var dir = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.back;
                    var probe = target + dir * ((collider != null ? collider.bounds.extents.magnitude : 1.5f) + 0.8f);
                    var nearest = Physics.OverlapSphere(probe, king.interactionRadius, king.interactorLayer)
                        .Where(h => h.GetComponent<BuildingInteractor>() != null)
                        .OrderBy(h => Vector3.Distance(probe, h.ClosestPoint(probe))).FirstOrDefault();
                    if (nearest != null && nearest.GetComponent<BuildingInteractor>().targetBuilding == slot) { pos = probe; break; }
                }
                var mover = king.GetComponent<CharacterController>();
                if (mover != null) mover.enabled = false;
                king.transform.position = new Vector3(pos.x, king.transform.position.y, pos.z);
                if (mover != null) mover.enabled = true;
                return $"king at {king.transform.position}";
            });
        }

        // Dev only: show the command-mode unit icons without holding the command key.
        private static bool forceUnitTypes;

        [HarmonyLib.HarmonyPatch(typeof(CommandUnits), nameof(CommandUnits.ShouldShowUnitTypes), HarmonyLib.MethodType.Getter)]
        private static class ForceUnitTypesPatch
        {
            private static void Postfix(ref bool __result) { if (forceUnitTypes) __result = true; }
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
