using System.Linq;
using BepInEx.Configuration;
using PalaceMod.Builder.Editor;

/*
    The layout editor's menus (UI kit frames, opened over the map): the main menu (what to place, the level's
    actions, help) and the details of the selected object (upgrade costs, size, reset, remove).
*/
namespace PalaceMod.Builder.Level
{
    internal static class LayoutMenus
    {
        private static ModFrame main, selected;
        internal static ConfigEntry<bool> HintShown;

        public static void OpenMain()
        {
            main ??= new ModFrame("Layout Editor")
                .AddTab("Add", BuildAdd)
                .AddTab("Level", BuildLevel)
                .AddTab("Help", BuildHelp);
            main.SetTitle("Layout: " + (LayoutEditor.Level?.Name ?? LayoutEditor.LevelId));
            if (main.IsOpen) main.Refresh(); else main.Open();
        }

        public static void OpenSelected()
        {
            if (LayoutEditor.Selected == null) return;
            selected ??= new ModFrame("Selected").SetContent(BuildSelected);
            selected.SetTitle(LayoutEditor.Selected.Type);
            if (selected.IsOpen) selected.Refresh(); else selected.Open();
        }

        public static void Refresh()
        {
            if (main?.IsOpen == true) main.Refresh();
            if (selected?.IsOpen == true)
            {
                if (LayoutEditor.Selected == null) selected.Close();
            }
        }

        public static void CloseAll()
        {
            var manager = UIFrameManager.instance;
            if (manager == null) return;
            for (int guard = 0; guard < 4 && (main?.IsOpen == true || selected?.IsOpen == true); guard++) manager.CloseActiveFrame();
        }

        public static bool IsOpen => main?.IsOpen == true || selected?.IsOpen == true;

        public static void FirstUseHint()
        {
            if (HintShown != null && HintShown.Value) return;
            if (HintShown != null) HintShown.Value = true;
            Notify.Show($"Layout editor: {Keys.Label(Keys.Menu)} opens the menu to add things; click a marker to select, drag to move", NotifyKind.Info, 8f, "builder-layout-hint");
        }

        // ---- Main menu -------------------------------------------------------------------------------------------

        private static void BuildAdd(ModPage page)
        {
            if (!LayoutEditor.Active) { page.Text("The layout editor isn't running."); return; }
            page.Header("Build slots");
            page.Text("A new slot copies the nearest slot of its kind: same upgrades and costs, and it unlocks together with it.");
            var center = UnityEngine.Camera.main != null && LayoutEditor.CursorGround(out var g) ? g : UnityEngine.Vector3.zero;
            foreach (var group in LayoutEditor.Natives.Where(n => n.CanBase).GroupBy(n => n.Type).OrderBy(g2 => g2.Key))
            {
                var sample = group.First();
                string type = group.Key;
                page.Row(type, $"costs {string.Join("/", sample.Costs)}   ·   {group.Count()} on this map",
                    $"Place a new {type.ToLowerInvariant()} slot. Click on the map to put it down; Shift-click to place several.",
                    () =>
                    {
                        string name = LayoutEditor.NearestBase(type, center) ?? sample.Name;
                        main.Close();
                        LayoutEditor.StartTool("slot:" + name);
                    });
            }
            page.Header("Scenery");
            if (LayoutEditor.PropBases.Count == 0) page.Text("This map has no scenery to copy.");
            foreach (var b in LayoutEditor.PropBases)
            {
                string id = b.Id;
                page.Row(b.Name, $"{b.Count} on this map", "Place a copy of this map's scenery. Solid props block paths (checked when the level starts).",
                    () => { main.Close(); LayoutEditor.StartTool(id); });
            }
        }

        private static void BuildLevel(ModPage page)
        {
            var draft = LayoutEditor.Draft;
            if (draft == null) { page.Text("The layout editor isn't running."); return; }
            var lv = LayoutEditor.Level;
            int moved = LayoutEditor.Items.Values.Count(i => i.Moved), removed = LayoutEditor.Items.Values.Count(i => i.Removed);
            int added = lv?.BuildSlots?.Add?.Count ?? 0, props = lv?.Props?.Count ?? 0;
            page.Header("This layout");
            page.Row("Changes", $"{moved} moved · {added} added · {removed} removed · {props} props", "Everything you changed on the map.");
            page.Row(draft.ReadOnly ? "Save a copy" : "Save", draft.Dirty ? Accent("unsaved changes") : Dim("saved"), "Writes the pack to its folder.", () => LayoutEditor.Save());
            page.Row("Undo", draft.History.CanUndo ? ModFields.TMPEscape(draft.History.UndoLabel) : Dim("nothing"), "Cmd/Ctrl+Z also undoes on the map.", BuilderEditor.Undo);
            page.Row("Redo", draft.History.CanRedo ? ModFields.TMPEscape(draft.History.RedoLabel) : Dim("nothing"), "Cmd/Ctrl+Shift+Z also redoes.", BuilderEditor.Redo);
            page.Row("Check enemy paths", Dim(LayoutEditor.Routes.Count == 0 ? "not checked" : $"{LayoutEditor.Routes.Count(r => r.Reachable)}/{LayoutEditor.Routes.Count} lines reach the castle"),
                "Updates pathfinding with your changes (about a second) and draws each spawn line's route to the castle: red means blocked.",
                () => { main.Close(); LayoutEditor.CheckPaths(); });
            page.Row("Snap to grid", LayoutEditor.Snap ? "on" : "off", $"Positions snap to whole metres and turns to 15°. {Keys.Label(Keys.Snap)} toggles it on the map.",
                () => { LayoutEditor.Snap = !LayoutEditor.Snap; main.Refresh(); });
            page.Header("Done");
            page.Row("Play this layout", draft.ErrorCount > 0 ? Error($"{draft.ErrorCount} problem(s) in the pack") : "",
                "Saves the pack and starts the level with your layout (a custom run: no saves, scores or achievements).", LayoutEditor.Play);
            page.Row("Open the pack editor", "", "The pack's units, wave sets and level settings. Close it to come back here.", () => BuilderEditor.OpenPack(draft.SourceId ?? draft.Pack.Id));
            page.Row("Leave the layout editor", "", "Returns to where you opened the Builder from. Unsaved changes stay in the pack editor until the game quits.", LayoutEditor.Leave);
        }

        private static void BuildHelp(ModPage page)
        {
            page.Header("On the map");
            page.Text($"Click a ring to select it and drag to move it. {Keys.Label(Keys.Rotate)} turns it ({Keys.Label(Keys.RotateBack)} the other way), {Keys.Label(Keys.Delete)} removes it, {Keys.Label(Keys.Properties)} shows its costs. Right-click deselects.");
            page.Text($"Move the view with WASD or by dragging with the middle mouse button, turn it with {Keys.Label(Keys.TurnLeft)}/{Keys.Label(Keys.TurnRight)}, zoom with the wheel.");
            page.Header("Markers");
            page.Text("Gold: the map's build slots (the big ring is the castle, which stays put). Green: slots you added. Pale green: scenery. Red lines: where enemies spawn. A cross marks a removed slot; select it and remove again to bring it back.");
            page.Header("Good to know");
            page.Text("Slots unlock like the slot they copy. Removing a slot also hides the slots it unlocks. Pathfinding updates once when the level starts; use Check enemy paths to see routes now.");
        }

        // ---- Selected object ---------------------------------------------------------------------------------------

        private static void BuildSelected(ModPage page)
        {
            var item = LayoutEditor.Selected;
            if (item == null) { page.Text("Nothing is selected."); return; }
            page.Header(ModFields.TMPEscape(item.Label));
            switch (item.Kind)
            {
                case LayoutEditor.ItemKind.Slot:
                case LayoutEditor.ItemKind.Added:
                    if (item.BaseCosts.Count == 0) page.Text("Its gate builds and upgrades it, so it has no costs of its own.");
                    for (int i = 0; i < item.BaseCosts.Count; i++)
                    {
                        int level = i;
                        string label = i == 0 ? "Build cost" : $"Upgrade {i} cost";
                        page.Stepper(label, () =>
                        {
                            int? o = LayoutEditor.CostOverride(item, level);
                            return o is int c ? $"{c}" : Dim($"{item.BaseCosts[level]}  (map's)");
                        }, delta =>
                        {
                            int current = LayoutEditor.CostOverride(item, level) ?? item.BaseCosts[level];
                            LayoutEditor.SetCost(item, level, UnityEngine.Mathf.Clamp(current + delta, 0, 999));
                        }, () => LayoutEditor.SetCost(item, level, null), "Gold to " + (level == 0 ? "build it" : $"buy upgrade {level}") + ". Left/right to change, apply to go back to the map's value.");
                    }
                    if (item.Kind == LayoutEditor.ItemKind.Slot && !item.Locked)
                    {
                        if (item.Moved) page.Row("Put it back", "", "Returns it to where the map has it.", () => { LayoutEditor.ResetPose(item); selected.Refresh(); });
                        page.Row(item.Removed ? "Restore" : "Remove", "", item.Removed ? "Brings the slot back." : "Takes the slot (and the slots it unlocks) off the map.", () => { LayoutEditor.RemoveOrRestore(item); selected.Refresh(); });
                    }
                    if (item.Kind == LayoutEditor.ItemKind.Added)
                        page.Row("Delete", "", "Removes the slot you added.", () => { LayoutEditor.RemoveOrRestore(item); selected.Close(); });
                    break;
                case LayoutEditor.ItemKind.Prop:
                    var def = LayoutEditor.PropDef(item);
                    if (def == null) break;
                    page.Stepper("Size", () => $"{(LayoutEditor.PropDef(item)?.Scale ?? 1f):0.##}×", delta => LayoutEditor.SetProp(item, "Set size", d => d.Scale = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.Round((d.Scale + 0.1f * delta) * 10f) / 10f, 0.3f, 4f), "size:" + item.Key),
                        null, "How big it is compared with the map's own.");
                    page.Row("Solid", def.Solid ? "yes" : "no", "Solid props block units, who walk around them. Walk-through props are decoration only.",
                        () => { LayoutEditor.SetProp(item, def.Solid ? "Make walk-through" : "Make solid", d => d.Solid = !d.Solid); selected.Refresh(); });
                    page.Row("Delete", "", "Removes this prop.", () => { LayoutEditor.RemoveOrRestore(item); selected.Close(); });
                    break;
                case LayoutEditor.ItemKind.Line:
                    page.Text("Enemies of the night's groups on this line appear along it. Drag it to move where they come from.");
                    if (item.Moved) page.Row("Put it back", "", "Returns the line to where the map has it.", () => { LayoutEditor.ResetPose(item); selected.Refresh(); });
                    break;
            }
        }

        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";
        private static string Accent(string s) => $"<color=#{ModStyle.Hex(ModStyle.Accent)}>{s}</color>";
        private static string Error(string s) => $"<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>{s}</color>";
    }
}
