using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using I2.Loc;
using UnityEngine;

/*
    Pause menu income tooltip (TFUIIncomeDisplay, the coin icon after the enemy preview).

    The game shows one "Income From Buildings" total, the sum of every built slot's GoldIncome, for whichever night is
    being previewed. This feature:
      - lists that income per building type under the total ("3x House -> 15 (3x5)"), with the game's localized names;
      - makes the total match what the dawn will actually pay: buildings knocked out tonight pay nothing, and when
        previewing a later night, mines have run down and harbours have built boats (IncomeCalculator);
      - updates the coin icon's number to match.
    Rows come from the same data the game uses, and are inserted after the game's own buildings line, found by
    rebuilding that exact string, so no localized text is parsed.
*/
namespace PalaceMod
{
    public class IncomeDisplayManager : BaseManager
    {
        public override string FeatureSection => "Income display";
        public override string FeatureDescription =>
            "Itemizes building income in the pause menu's gold tooltip and corrects it for knocked-out buildings, mines running down and harbours growing.";

        private static readonly AccessTools.FieldRef<TFUIIncomeDisplay, int> fromBuildings = AccessTools.FieldRefAccess<TFUIIncomeDisplay, int>("iFromBuildings");
        private static readonly AccessTools.FieldRef<TFUIIncomeDisplay, int> fromInterest = AccessTools.FieldRefAccess<TFUIIncomeDisplay, int>("iFromInterest");
        private static readonly AccessTools.FieldRef<TFUIIncomeDisplay, int> fromGuild = AccessTools.FieldRefAccess<TFUIIncomeDisplay, int>("iFromBuildersGuild");
        private static readonly AccessTools.FieldRef<TFUIIncomeDisplay, int> fromTreasure = AccessTools.FieldRefAccess<TFUIIncomeDisplay, int>("iFromTreasureHunter");
        private static readonly AccessTools.FieldRef<TFUIIncomeDisplay, int> fromEnemies = AccessTools.FieldRefAccess<TFUIIncomeDisplay, int>("iFromEnemies");
        private static readonly AccessTools.FieldRef<TFUIIncomeDisplay, int> currentIncome = AccessTools.FieldRefAccess<TFUIIncomeDisplay, int>("currentIncome");

        /// <summary>Building income for each live display, captured when the game fills it in.</summary>
        private static readonly ConditionalWeakTable<TFUIIncomeDisplay, List<BuildingIncome>> breakdowns = new();

        private const string Arrow = " <sprite name=arrow_right> <sprite name=coin><style=Body Numerals>";

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("income", "income [build [passes]|knockout [n]|pause|select|preview <+n|-n>]   show the income breakdown; build every slot; knock out n buildings; open the pause menu; show the income tooltip; cycle the previewed night", RunCommand);
        }

        protected override void OnFeatureEnabled() => RefreshOpenDisplays();
        protected override void OnFeatureDisabled() => RefreshOpenDisplays();

        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, int> previewWave = AccessTools.FieldRefAccess<PauseUILoadoutHelper, int>("currentPreviewWavenumber");

        /// <summary>Re-fills any income display on screen (with or without the patches, whichever is current) and
        /// redraws open tooltips, so toggling the feature with the pause menu open takes effect at once.</summary>
        private static void RefreshOpenDisplays()
        {
            var helper = UnityEngine.Object.FindObjectOfType<PauseUILoadoutHelper>();
            if (helper == null) return;
            foreach (var display in UnityEngine.Object.FindObjectsOfType<TFUIIncomeDisplay>())
            {
                breakdowns.Remove(display);
                display.UpdateData(previewWave(helper));
            }
            foreach (var tooltip in UnityEngine.Object.FindObjectsOfType<PerkSelectionTooltipHelper>()) tooltip.UpdateTooltip();
        }

        /// <summary>Dawns between the coming one and the one after the previewed night (the pause menu can look ahead).</summary>
        private static int DawnsAhead(int previewWave)
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null) return 0;
            int trueWave = IncomeCalculator.IsNight ? spawner.Wavenumber : spawner.Wavenumber + 1;
            return Math.Max(0, previewWave - trueWave);
        }

        [HarmonyPatch(typeof(TFUIIncomeDisplay), nameof(TFUIIncomeDisplay.UpdateData))]
        private static class UpdateDataPatch
        {
            private static void Postfix(TFUIIncomeDisplay __instance, int wavenumber)
            {
                var buildings = IncomeCalculator.All(DawnsAhead(wavenumber));
                breakdowns.Remove(__instance);
                breakdowns.Add(__instance, buildings);

                fromBuildings(__instance) = buildings.Sum(b => b.Income);
                int total = fromBuildings(__instance) + fromGuild(__instance) + fromTreasure(__instance) + fromEnemies(__instance) + fromInterest(__instance);
                currentIncome(__instance) = total;
                if (__instance.displayNumber != null) __instance.displayNumber.text = total.ToString();
            }
        }

        [HarmonyPatch(typeof(TFUIIncomeDisplay), nameof(TFUIIncomeDisplay.GetDescription))]
        private static class GetDescriptionPatch
        {
            private static void Postfix(TFUIIncomeDisplay __instance, ref string __result)
            {
                if (!breakdowns.TryGetValue(__instance, out var buildings) || buildings.Count == 0) return;
                // The game's own line, built exactly as TFUIIncomeDisplay.GetDescription builds it.
                string nativeLine = LocalizationManager.GetTranslation("Menu/Income From Buildings") + Arrow + fromBuildings(__instance) + "</style>\n";
                int at = __result.IndexOf(nativeLine, StringComparison.Ordinal);
                if (at < 0) return;
                at += nativeLine.Length;
                __result = __result.Substring(0, at) + Breakdown(buildings) + __result.Substring(at);
            }
        }

        private readonly struct Row
        {
            public readonly string Name;
            public readonly int Each;
            public readonly int Count;
            public readonly bool KnockedOut;
            public int Total => Each * Count;

            public Row(string name, int each, int count, bool knockedOut)
            {
                Name = name;
                Each = each;
                Count = count;
                KnockedOut = knockedOut;
            }
        }

        private static List<Row> Rows(List<BuildingIncome> buildings) => buildings
            .Where(b => b.IncomeIfStanding > 0)
            .GroupBy(b => (name: DisplayName(b.Slot), each: b.IncomeIfStanding, b.KnockedOut))
            .Select(g => new Row(g.Key.name, g.Key.each, g.Count(), g.Key.KnockedOut))
            .OrderBy(r => r.KnockedOut)
            .ThenByDescending(r => r.Total)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

        private static string Breakdown(List<BuildingIncome> buildings)
        {
            var text = new StringBuilder();
            foreach (var row in Rows(buildings))
            {
                // Indented, slightly smaller rows under the game's total. Knocked-out buildings are struck through:
                // they show what they'd normally pay, and pay nothing this dawn.
                text.Append("<size=85%><indent=1em>");
                if (row.KnockedOut) text.Append("<alpha=#80><s>");
                if (row.Count > 1) text.Append("<style=Body Numerals>").Append(row.Count).Append("</style>× ");
                text.Append(row.Name).Append(Arrow).Append(row.Total).Append("</style>");
                if (row.Count > 1) text.Append(" <alpha=#80>(").Append(row.Count).Append('×').Append(row.Each).Append(")");
                if (row.KnockedOut) text.Append("</s>");
                text.Append("</indent></size><alpha=#FF>\n");
            }
            return text.ToString();
        }

        private static string DisplayName(BuildSlot slot)
        {
            string name = LocalizationManager.GetTranslation(slot.LOCIDENTIFIER_NAME);
            return string.IsNullOrEmpty(name) ? slot.buildingName : name;
        }

        private static string RunCommand(string[] args)
        {
            if (TagManager.instance == null) throw new InvalidOperationException("not in a level");
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "build":
                {
                    GameplayImpact.Mark("Dev: built every slot for free");
                    int passes = args.Length > 1 ? int.Parse(args[1]) : 1, built = 0;
                    for (int pass = 0; pass < passes; pass++)
                        foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
                        {
                            if (!slot.CanBeUpgraded || slot.ActivatorUpgradesThis) continue;
                            slot.TryToBuildOrUpgradeAndPay(null, _presentChoice: false);
                            built++;
                        }
                    return $"{built} builds/upgrades";
                }
                case "knockout":
                {
                    // Flags buildings as knocked out tonight (as if destroyed), without touching their HP.
                    int count = args.Length > 1 ? int.Parse(args[1]) : 3, done = 0;
                    foreach (var b in IncomeCalculator.All().Where(b => b.Income > 0).Take(count))
                    {
                        b.Slot.Interactor.OnKnockOut();
                        done++;
                    }
                    GameplayImpact.Mark("Dev: knocked out buildings");
                    return $"knocked out {done}";
                }
                case "pause":
                    if (UIFrameManager.instance.ActiveFrame == null)
                    {
                        try { UIFrameManager.instance.InMatchPause(); }
                        catch (ArgumentOutOfRangeException) { } // see SelectIncomeDisplay
                    }
                    return "paused; `income select` shows the tooltip";
                case "select":
                    return SelectIncomeDisplay();
                case "preview":
                {
                    var helper = UnityEngine.Object.FindObjectOfType<PauseUILoadoutHelper>() ?? throw new InvalidOperationException("pause menu not open");
                    try { helper.CycleNightPreview(args.Length > 1 ? int.Parse(args[1]) : 1); }
                    catch (ArgumentOutOfRangeException) { } // see SelectIncomeDisplay
                    return SelectIncomeDisplay();
                }
                case "":
                {
                    var lines = Rows(IncomeCalculator.All()).Select(r =>
                        $"{(r.KnockedOut ? "[KO] " : "")}{r.Count}x {r.Name} = {r.Total} ({r.Count}x{r.Each})");
                    int nativeSum = IncomeCalculator.All().Sum(b => b.Slot.GoldIncome);
                    return $"next dawn {IncomeCalculator.Total()} (+1 dawn {IncomeCalculator.Total(1)}, +3 {IncomeCalculator.Total(3)}); " +
                        $"sum of GoldIncome {nativeSum}; night {IncomeCalculator.IsNight}\n" + string.Join("\n", lines);
                }
                default:
                    throw new ArgumentException("usage: income [build [passes]|pause|preview <n>]");
            }
        }

        private static string SelectIncomeDisplay()
        {
            // The pause frame itself, not ActiveFrame: a level loaded straight from `dev.py cmd level` has no loadout,
            // and the game's PauseUILoadoutHelper.AssignNavigationTargets then throws while the frame opens.
            var frame = UnityEngine.Object.FindObjectOfType<PauseUILoadoutHelper>()?.frame ?? throw new InvalidOperationException("pause menu not open");
            var display = UnityEngine.Object.FindObjectOfType<TFUIIncomeDisplay>() ?? throw new InvalidOperationException("income display not found");
            frame.Select(display);
            foreach (var tooltip in UnityEngine.Object.FindObjectsOfType<PerkSelectionTooltipHelper>()) tooltip.OnSelection();
            return display.GetTitle() + ": " + display.CurrentIncome + "\n" + display.GetDescription();
        }
    }
}
