using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The smaller Studio hooks of the requirement tools:
    - the delete guard: removing slots that others need asks what happens to those ("link them to what it needed" /
      "from the start" / cancel);
    - context-menu rows (Needs…, Chain…, From the start);
    - auto-link for newly placed slots (a house needs the nearest house within 25 m, else castle level 1; other kinds
      castle level 1), switched in View ▾.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal static class RequirementHooks
    {
        public static bool AutoLink = true;

        public static bool GuardDelete(IReadOnlyList<string> keys, Action proceed)
        {
            var host = RequirementState.Host;
            if (host == null) return false;
            var eff = RequirementState.Effective;
            var deleting = new HashSet<string>(keys.Where(k => Studio.Find(k) is StudioItem i && !i.Removed).Select(RequirementState.Ref).Where(r => r != null), StringComparer.Ordinal);
            var needed = deleting.Where(r => LevelRequirements.Dependents(eff, r).Any(d => !deleting.Contains(d))).ToList();
            if (needed.Count == 0) return false;
            int dependents = needed.SelectMany(r => LevelRequirements.Dependents(eff, r)).Where(d => !deleting.Contains(d)).Distinct().Count();
            string what = needed.Count == 1 ? RequirementState.Name(needed[0]) : $"{needed.Count} of these";
            ModPopup.Open($"{what} unlock{(needed.Count == 1 ? "s" : "")} {dependents} slot{(dependents == 1 ? "" : "s")}", page =>
            {
                page.Button("Link them to what it needed", () => Rewire(needed, ForgetMode.CloseUp, proceed));
                page.Button("Make them available from the start", () => Rewire(needed, ForgetMode.FromStart, proceed));
                page.Button("Cancel", () => ModPopup.Current?.Close());
            }, width: 460f);
            return true;
        }

        private static void Rewire(List<string> refs, ForgetMode mode, Action proceed)
        {
            ModPopup.Current?.Close();
            var host = RequirementState.Host;
            Studio.Edit(mode == ForgetMode.CloseUp ? "Close up the chain" : "Make dependents available from the start",
                lv => { foreach (var r in refs) LevelRequirements.ForgetSlot(lv, host, r, mode); });
            RequirementState.Invalidate();
            proceed();
        }

        public static IEnumerable<(string label, Action run)> ContextItems(IReadOnlyList<string> keys)
        {
            var deps = keys.Where(RequirementState.CanNeed).ToList();
            if (deps.Count == 0) yield break;
            Vector2 at = Input.mousePosition;
            yield return ("Needs…", () => RequirementInspector.OpenNeeds(deps, at));
            yield return ("Chain…", () => ChainPopup.Open(deps));
            yield return ("From the start", () => RequirementEdits.Apply(deps, LevelRequirements.FromStart, null));
        }

        /// <summary>Inside the edit that placed a slot: gives it its auto-link requirement (unless it has one).</summary>
        public static void OnPlaced(LevelDef lv, string key)
        {
            if (!AutoLink || lv == null) return;
            string r = SlotRefs.FromKey(key);
            if (r == null || LevelRequirements.Explicit(lv, r) != null) return;
            var item = Studio.Find(key);
            string type = item?.TypeName ?? LevelRequirements.FindAdd(lv, r)?.Base;
            bool house = (type ?? "").ToLowerInvariant().Contains("house");
            var pos = item?.Pos ?? Level.LayoutScene.ToVector(LevelRequirements.FindAdd(lv, r)?.Pos);
            var houses = Studio.Items.Where(i => i.Key != key && !i.Removed && (i.Kind is StudioItemKind.Slot or StudioItemKind.Added)
                    && (i.TypeName ?? "").ToLowerInvariant().Contains("house"))
                .Select(i => new ChainNode(RequirementState.Ref(i.Key), i.Pos.x, i.Pos.z));
            LevelRequirements.Set(lv, r, ChainPreset.AutoLink(house, pos.x, pos.z, houses));
        }
    }
}
