using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PalaceMod.Economy;
using V = PalaceMod.Economy.PlannerVisuals;

/*
    The planner's words: what a plan item is and does, in one line for chips and the info line, and longer for
    tooltips; perk effects; number formats.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        internal static readonly string DimHex = ModStyle.Hex(ModStyle.Dim);
        internal static readonly string RedHex = ModStyle.Hex(V.Red);

        /// <summary>"Build House 3 · north-east" / "Castle to level 3: Builder's Guild" / "Lose Mill" / "Gold found: 12".</summary>
        internal static string Title(ActionResult r)
        {
            var a = r.Action;
            string name = r.Slot?.Label ?? "a building not on this map";
            switch (a.Kind)
            {
                case EconActionKind.KnockOut: return $"Lose {name}";
                case EconActionKind.Gold: return $"Gold found: {a.Amount ?? 0}";
            }
            string verb = a.Level <= 1 ? $"Build {name}" : $"{name} to level {a.Level}";
            var step = r.Slot != null && a.Level >= 1 && a.Level <= r.Slot.Steps.Count ? r.Slot.Steps[a.Level - 1] : null;
            if (step != null && step.IsChoice) verb += ": " + step.Branches[EconSim.BranchIndex(step, a.Choice)].Name;
            return verb;
        }

        internal static string Detail(ActionResult r)
        {
            switch (r.Status)
            {
                case ActionStatus.AlreadyDone: return r.Reason == "Done" ? "done" : r.Reason;
                case ActionStatus.Past: return $"missed: {r.Reason}";
                case ActionStatus.Blocked: return $"skipped: {r.Reason}";
            }
            switch (r.Action.Kind)
            {
                case EconActionKind.KnockOut: return r.IncomeDelta < 0 ? $"pays nothing that dawn ({Signed(r.IncomeDelta)})" : "pays nothing that dawn";
                case EconActionKind.Gold: return "your estimate";
            }
            return string.Join(" · ", new[]
            {
                r.Status == ActionStatus.Short ? $"{Coin(r.Cost)}, short by {r.Shortfall}" : Coin(r.Cost),
                r.IncomeDelta != 0 ? $"{Signed(r.IncomeDelta)} per dawn" : null,
                r.Branch?.IsWisdom == true ? $"wisdom {Signed(r.Wisdom)} that night{(r.Action.Amount == null ? $" ({r.Branch.WisdomGold} per building lost)" : "")}" : null,
                r.Branch?.Prayers == true ? "shrines pay nothing that dawn" : null,
                Payback(r),
            }.Where(x => x != null));
        }

        internal static string Payback(ActionResult r)
        {
            if (!r.Counts || !r.Action.IsStep || r.IncomeDelta <= 0 && r.ExtraIncome <= 0 || r.Cost <= 0) return null;
            return r.PaybackDawn is int d ? $"pays for itself by dawn {d}" : $"earns back {r.ExtraIncome} of {r.Cost} by the end";
        }

        /// <summary>"build · 8 · +2 per dawn", or why it can't go further.</summary>
        internal static string NextStep(EconWorld state, EconSlot e)
        {
            if (e.Level >= e.MaxLevel) return $"<color=#{DimHex}>level {e.Level}, highest</color>";
            if (EconSim.Blocker(state, e) is { } why) return $"<color=#{DimHex}>{why}</color>";
            var step = e.Steps[e.Level];
            var incomes = step.Branches.Select((b, i) => EconSim.StepIncome(e, i)).Distinct().OrderBy(v => v).ToList();
            string income = incomes.All(v => v == 0) ? "" : " · " + string.Join("/", incomes.Select(Signed)) + " per dawn";
            string what = e.Level == 0 ? "build" : $"to level {e.Level + 1}";
            return $"{what} · {Coin(step.Cost)}{income}";
        }

        internal static string PerkText(EconPerk p, EconWorld w) => p.Kind switch
        {
            EconPerkKind.Interest => $"1 gold at dusk per {p.Each} gold you hold, up to {p.Max}.",
            EconPerkKind.TreasureHunter => $"{p.T1}, {p.T2} and {p.T3} gold at the dawns before the last three nights.",
            EconPerkKind.RoyalMint => $"{p.Amount} more gold at the start.",
            EconPerkKind.Loan => $"{p.Amount} gold at the start, paid back with {p.Amount2} of the first nights' enemy gold.",
            EconPerkKind.RatGod => $"Enemies drop {p.Factor * 100f:0}% of their gold.",
            EconPerkKind.Outpost => "Building an outpost builds some of its towers for free.",
            EconPerkKind.DestructionGod => "Destroyed buildings stay down a second night (and pay nothing at either dawn) unless upgraded the day between.",
            _ => "Changes some buildings' costs or income on this map.",
        };

        internal static string Coin(int amount) => $"{(amount < 0 ? "-" : "")}{Mathf.Abs(amount):N0}<sprite name=\"coin\">";
        internal static string Gold(int v) => v < 0 ? $"<color=#{RedHex}>{v:N0}</color>" : v.ToString("N0");
        internal static string N(int v) => v.ToString("N0");
        internal static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        internal static string Short(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
