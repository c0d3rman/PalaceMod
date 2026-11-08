using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Link tool (K, docs/builder-ux.md "Editing"): pick the slots that need something (the selection when the tool
    starts, or clicks), then click their prerequisite: a slot or the castle. The castle asks for a level; a slot with
    more than one level asks "after: built / level 2 / ...". The new link replaces each slot's prerequisite (one per
    slot). Ctrl/Cmd-click adds or drops a dependent; Shift-click on the prerequisite keeps the tool for the next link,
    else it goes back to Select.
    Alt-click makes a slot available from the start. A link that would close a loop is refused.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal sealed class LinkTool : StudioTool
    {
        public const string ToolId = "link";
        private readonly Keybind key;
        private readonly List<string> pending = [];

        public LinkTool(Keybind key) => this.key = key;

        public override string Id => ToolId;
        public override string Name => "Link";
        public override int Order => 600;
        public override Keybind Key => key;
        public override Sprite Icon => ModStyle.MarkSprite;
        public override string Hint => pending.Count == 0
            ? "Click what needs something · Ctrl add · Alt-click from the start"
            : $"Click what {(pending.Count == 1 ? RequirementState.Name(RequirementState.Ref(pending[0])) : $"{pending.Count} slots")} need · Ctrl add · Shift keep · Esc cancel";

        public IReadOnlyList<string> Pending => pending;

        public override void Activate()
        {
            pending.Clear();
            pending.AddRange(Studio.Selection.Where(RequirementState.CanNeed));
        }

        public override void Deactivate() => pending.Clear();

        public override bool Cancel()
        {
            if (pending.Count == 0) return false;
            pending.Clear();
            Studio.Select([], SelectOp.Replace);
            return true;
        }

        public override void Update(StudioPointer p)
        {
            if (p.OverUI || !p.Click) return;
            Click(p.HoverKey, p.Ctrl, p.Shift, p.Alt, p.Screen);
        }

        /// <summary>A click on an item (also from the dev command).</summary>
        public void Click(string key, bool ctrl, bool shift, bool alt, Vector2 screen)
        {
            if (key == null || RequirementState.Ref(key) == null)
            {
                if (!ctrl) Cancel();
                return;
            }
            if (alt)
            {
                if (!RequirementState.CanNeed(key)) return;
                RequirementEdits.Apply([key], LevelRequirements.FromStart, $"{RequirementState.Name(RequirementState.Ref(key))} from the start");
                return;
            }
            if (pending.Count == 0 || ctrl || pending.Contains(key))
            {
                if (!RequirementState.CanNeed(key))
                {
                    if (pending.Count == 0) Notify.Show(key == RequirementState.CastleKey ? "The castle doesn't need anything" : "This one can't have a requirement", NotifyKind.Info, 3f, "builder-link");
                    else if (ctrl) Notify.Show("Click without Ctrl to link to it", NotifyKind.Info, 3f, "builder-link");
                    return;
                }
                if (pending.Contains(key)) pending.Remove(key);
                else pending.Add(key);
                Studio.Select(pending, SelectOp.Replace);
                return;
            }
            if (!RequirementState.CanBeNeeded(key)) return;
            var deps = pending.ToList();
            string target = RequirementState.Ref(key);
            bool castle = target == RequirementState.Castle;
            int levels = RequirementState.Levels(target);
            void Done(int level)
            {
                var req = new SlotRequirement { Slot = castle ? SlotRefs.Castle : target, Level = level };
                if (RequirementEdits.Apply(deps, req, null) == 0) return;
                pending.Clear();
                if (!shift) Studio.SetTool("select");
            }
            if (levels <= 1 && !castle) { Done(0); return; }
            RequirementEdits.PickLevel(castle, target, levels, screen, Done);
        }
    }

    /// <summary>Applying and choosing requirements (shared by the Link tool, the inspector and the context menu).</summary>
    internal static class RequirementEdits
    {
        /// <summary>Gives every slot the requirement as one undo step, skipping (with a notice) any that would close a
        /// loop. Returns how many changed.</summary>
        public static int Apply(IReadOnlyList<string> keys, SlotRequirement req, string label)
        {
            var eff = RequirementState.Effective;
            string target = req?.Slot == SlotRefs.Castle ? RequirementState.Castle : req?.Slot;
            var ok = new List<string>();
            var refused = new List<string>();
            foreach (var key in keys)
            {
                if (!RequirementState.CanNeed(key)) continue;
                string r = RequirementState.Ref(key);
                if (target == r || LevelRequirements.WouldCycle(eff, r, target)) refused.Add(r);
                else ok.Add(r);
            }
            if (refused.Count > 0)
                Notify.Show($"{string.Join(", ", refused.Select(RequirementState.Name))} can't need {RequirementState.Name(req.Slot)}: that would make a loop", NotifyKind.Warning, 4f, "builder-link");
            if (ok.Count == 0) return 0;
            label ??= ok.Count == 1
                ? $"{RequirementState.Name(ok[0])} needs {RequirementState.Describe(req)}"
                : $"{ok.Count} slots need {RequirementState.Describe(req)}";
            Studio.Edit(label, lv => { foreach (var r in ok) LevelRequirements.Set(lv, r, req); });
            RequirementState.Invalidate();
            return ok.Count;
        }

        /// <summary>Sets the upgrade cap on slots as one undo step.</summary>
        public static void ApplyCap(IReadOnlyList<string> keys, int? cap)
        {
            var refs = keys.Where(RequirementState.CanNeed).Select(RequirementState.Ref).ToList();
            if (refs.Count == 0) return;
            string what = cap == null ? "map's cap" : cap == -100 ? "no cap" : cap == 0 ? "upgrades up to the castle's level" : $"upgrades up to castle level +{cap}";
            Studio.Edit($"{(refs.Count == 1 ? RequirementState.Name(refs[0]) : $"{refs.Count} slots")}: {what}", lv => { foreach (var r in refs) LevelRequirements.SetCap(lv, r, cap); });
        }

        /// <summary>The castle's level (1-N) or a slot's "after: built / level 2 / ..." as a small popup.</summary>
        public static void PickLevel(bool castle, string target, int levels, Vector2 screen, Action<int> picked)
        {
            ModPopup.Open(castle ? "Castle level" : $"After {RequirementState.Name(target)}", page =>
            {
                for (int i = 0; i < levels; i++)
                {
                    int level = i;
                    string label = castle ? $"Castle level {i + 1}" : i == 0 ? "Built" : $"Level {i + 1}";
                    page.Button(label, () => { ModPopup.Current?.Close(); picked(level); });
                }
            }, screenPoint: screen, width: 320f);
        }
    }
}
