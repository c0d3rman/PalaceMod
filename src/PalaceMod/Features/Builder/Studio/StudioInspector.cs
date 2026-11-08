using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The right panel's Inspector (the selection; with several objects, the fields they share, "—" where they differ, and
    every change applies to all as one undo step) and Level tab (name, description, base map, nights, path check).
    Other workstreams' sections (Studio.AddInspectorSection) follow the Studio's own.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioInspector
    {
        private const string Mixed = "—";
        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";
        private static string Esc(string s) => ModFields.TMPEscape(s ?? "");

        /// <summary>"House · 2·6·12 · moved" for the status line.</summary>
        public static string Summary(StudioItem i)
        {
            var parts = new List<string> { Esc(i.TypeName) };
            if (i.BaseCosts.Count > 0) parts.Add(string.Join("·", Enumerable.Range(0, i.BaseCosts.Count).Select(l => StudioEdits.Cost(i, l))));
            var label = Studio.LabelFor(i.Key);
            if (!string.IsNullOrEmpty(label.Badge)) parts.Add(label.Badge);
            if (i.Removed) parts.Add("removed");
            else if (i.Moved) parts.Add("moved");
            if (i.Locked && i.Kind == StudioItemKind.Slot) parts.Add(i.IsCastle ? "castle" : "locked");
            return string.Join(" · ", parts);
        }

        /// <summary>The hover card's body.</summary>
        public static string Card(StudioItem i)
        {
            var lines = new List<string> { Esc(i.TypeName) };
            if (i.BaseCosts.Count > 0)
                lines.Add("Costs " + string.Join(" / ", Enumerable.Range(0, i.BaseCosts.Count).Select(l => StudioEdits.Cost(i, l))));
            if (i.Kind == StudioItemKind.Prop && StudioEdits.PropDef(i) is { } d) lines.Add($"{(d.Solid ? "Solid" : "Walk-through")} · {d.Scale:0.##}×");
            if (i.Removed) lines.Add("Removed from the map");
            else if (i.Moved) lines.Add("Moved");
            if (StudioScene.GroupOf(i.Key) is string g) lines.Add("In " + Esc(StudioScene.Find(g)?.Name));
            var card = Studio.LabelFor(i.Key).Card;
            if (!string.IsNullOrEmpty(card)) lines.Add(card);
            return string.Join("\n", lines);
        }

        // ---- Inspector ------------------------------------------------------------------------------------------

        public static void Build(ModPage page)
        {
            var keys = StudioSelection.Keys.ToList();
            var items = keys.Select(StudioScene.Find).Where(i => i != null).ToList();
            if (items.Count == 0) { Overview(page); Sections(page, keys); return; }

            var byType = items.GroupBy(i => i.TypeName).OrderByDescending(g => g.Count()).ToList();
            StudioDock.SmallHeader(page, items.Count == 1 ? Esc(items[0].Name) : string.Join(" · ", byType.Take(3).Select(g => $"{g.Count()} {Esc(Plural(g.Key, g.Count()))}")) + (byType.Count > 3 ? " …" : ""));
            if (items.Count == 1) page.Row(Esc(items[0].TypeName), Dim(Summary(items[0]).Substring(Esc(items[0].TypeName).Length).TrimStart(' ', '·')));

            // Costs: when every selected slot has the same number of levels.
            var slots = items.Where(i => (i.Kind is StudioItemKind.Slot or StudioItemKind.Added) && i.BaseCosts.Count > 0).ToList();
            if (slots.Count > 0)
            {
                int levels = StudioMath.Common(slots.Select(s => s.BaseCosts.Count), out bool levelsDiffer);
                if (!levelsDiffer)
                {
                    for (int l = 0; l < levels; l++)
                    {
                        int level = l;
                        string label = l == 0 ? "Build cost" : $"Upgrade {l}";
                        page.Number(label, () => StudioMath.Common(slots.Select(s => StudioEdits.Cost(s, level)), out _),
                            v => StudioEdits.SetCost(slots, level, v), 0, 999,
                            format: v => StudioMath.Common(slots.Select(s => StudioEdits.Cost(s, level)), out bool m) is var _ && m ? Dim(Mixed)
                                : slots.All(s => StudioEdits.CostOverride(s, level) == null) ? $"{v}  {Dim("map's")}" : $"{v}",
                            tooltip: () => level == 0 ? "Gold to build it. Empty goes back to the map's." : $"Gold for upgrade {level}. Empty goes back to the map's.",
                            onClear: () => StudioEdits.SetCost(slots, level, null));
                    }
                }
                else
                {
                    var main = byType.First(g => g.Any(i => slots.Contains(i)));
                    page.Row("Costs differ", $"{Esc(Plural(main.Key, 2))} only", "Select only one kind to edit costs together.",
                        () => StudioSelection.Select(slots.Where(s => s.TypeName == main.Key).Select(s => s.Key), SelectOp.Replace));
                }
            }

            // Scenery.
            var props = items.Where(i => i.Kind == StudioItemKind.Prop).ToList();
            if (props.Count > 0)
            {
                page.Number("Size", () => StudioMath.Common(props.Select(p => StudioEdits.PropDef(p)?.Scale ?? 1f), out _),
                    v => StudioEdits.SetProps(props, "Set size", d => d.Scale = v, "size:" + string.Join(",", props.Select(p => p.Key))), 0.1f, 10f, 0.1f,
                    v => StudioMath.Common(props.Select(p => StudioEdits.PropDef(p)?.Scale ?? 1f), out bool m) is var _ && m ? Dim(Mixed) : $"{v:0.##}×",
                    () => "Size against the map's own.");
                bool solid = StudioMath.Common(props.Select(p => StudioEdits.PropDef(p)?.Solid ?? true), out bool solidMixed);
                page.Row("Solid", solidMixed ? Dim(Mixed) : solid ? "Yes" : "No", Descriptions.For(DescKind.Flag, "Solid") ?? "Solid scenery blocks units; walk-through is decoration.",
                    () => StudioEdits.SetProps(props, solidMixed || !solid ? "Make solid" : "Make walk-through", d => d.Solid = solidMixed || !solid));
            }

            // Pose of one object.
            if (items.Count == 1 && StudioEdits.Movable(items[0]))
            {
                var it = items[0];
                page.Number("Turn", () => Mathf.Round(Mathf.Repeat(it.Yaw, 360f)), v => StudioEdits.SetPoses([(it, it.Pos, v)], $"Turn {it.Name}", "turn:" + it.Key), 0f, 359f, StudioShell.TurnStep, v => $"{v:0}°");
            }
            if (items.Any(i => i.Moved && (i.Kind is StudioItemKind.Slot or StudioItemKind.Line)))
                page.Row("Reset to the map's", "", "Puts it back where the map has it.", () => { foreach (var i in items.Where(i => i.Moved)) StudioEdits.ResetPose(i); });

            // Groups.
            var groups = items.Select(i => StudioScene.GroupOf(i.Key)).Distinct().ToList();
            if (groups.Count == 1 && groups[0] is string gk && StudioScene.Find(gk) is { } group && group.Members.All(StudioSelection.Contains))
            {
                page.TextInput("Group", () => group.Name, v => { if (!string.IsNullOrWhiteSpace(v)) StudioEdits.RenameGroup(group.Id, v.Trim()); });
            }

            // Actions.
            var actions = page.Custom("Actions", 48f);
            float w = (page.Size.x - 12f) / 3f;
            void Act(int i, string label, Action run)
            {
                var t = ModTile.Create(actions, "Action " + label, new Vector2(w, 42f), null, label, 21f);
                t.Label.alignment = TMPro.TextAlignmentOptions.Center;
                t.Place(new Vector2(i * (w + 6f), -3f));
                t.Applied += run;
            }
            Act(0, "Duplicate", StudioShell.DuplicateAndGrab);
            if (groups.Any(g => g != null)) Act(1, "Ungroup", () => StudioEdits.Ungroup(StudioSelection.Keys));
            else Act(1, "Group", () => { if (StudioEdits.Group(StudioSelection.Keys) is string k) StudioSelection.Select([k], SelectOp.Replace); });
            bool restore = StudioEdits.WouldRestore(items);
            Act(2, restore ? "Restore" : "Delete", () => StudioEdits.Delete(StudioSelection.Keys.ToList()));

            Sections(page, keys);

            if (items.Count > 1)
            {
                StudioDock.SmallHeader(page, "Selected");
                foreach (var i in items)
                {
                    var item = i;
                    StudioDock.Compact(page.Row($"{Esc(i.Name)}  <size=75%>{Dim(Esc(i.TypeName))}</size>", null, (string)null, () => StudioSelection.Select([item.Key], SelectOp.Replace)), false, 0f, page.Size.x);
                }
            }
        }

        private static void Sections(ModPage page, IReadOnlyList<string> keys)
        {
            foreach (var s in Studio.InspectorSections)
            {
                bool applies;
                try { applies = s.AppliesTo(keys); }
                catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio inspector section {s.Id} check failed", e); continue; }
                if (!applies) continue;
                try { s.Build(page, keys); }
                catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio inspector section {s.Id} failed", e); }
            }
        }

        private static string Plural(string type, int n)
        {
            if (n == 1 || string.IsNullOrEmpty(type)) return type;
            if (type.EndsWith("s")) return type;
            if (type.EndsWith("y") && !type.EndsWith("ay") && !type.EndsWith("ey")) return type.Substring(0, type.Length - 1) + "ies";
            return type + "s";
        }

        private static void Overview(ModPage page)
        {
            var lv = Studio.Level;
            StudioDock.SmallHeader(page, Esc(lv?.Name ?? "Level"));
            var items = StudioScene.Items;
            int Count(StudioItemKind k) => items.Count(i => i.Kind == k && !i.Removed);
            page.Row("Build slots", $"{Count(StudioItemKind.Slot) + Count(StudioItemKind.Added)}", "On the map now (the castle included).");
            var kinds = items.Where(i => (i.Kind is StudioItemKind.Slot or StudioItemKind.Added) && !i.Removed).GroupBy(i => i.TypeName).OrderByDescending(g => g.Count());
            foreach (var g in kinds)
            {
                string type = g.Key;
                page.Row("   " + Esc(Plural(type, 2)), g.Count().ToString(), (string)null, () => StudioSelection.Select(StudioScene.Items.Where(i => i.TypeName == type && !i.Removed).Select(i => i.Key), SelectOp.Replace));
            }
            int removed = items.Count(i => i.Removed), moved = items.Count(i => i.Moved);
            if (removed > 0) page.Row("Removed", removed.ToString(), (string)null, () => StudioSelection.Select(StudioScene.Items.Where(i => i.Removed).Select(i => i.Key), SelectOp.Replace));
            if (moved > 0) page.Row("Moved", moved.ToString(), (string)null, () => StudioSelection.Select(StudioScene.Items.Where(i => i.Moved).Select(i => i.Key), SelectOp.Replace));
            page.Row("Scenery", Count(StudioItemKind.Prop).ToString());
            page.Row("Spawn lines", Count(StudioItemKind.Line).ToString());
            if (StudioShell.PathSummary is string paths) page.Row("Paths", Dim(StudioShell.PathShort), paths);
        }

        // ---- Level tab --------------------------------------------------------------------------------------------

        public static void BuildLevel(ModPage page)
        {
            var draft = Studio.Draft;
            var lv = Studio.Level;
            if (draft == null || lv == null) return;
            string id = lv.Id;
            void Set(string label, Action<LevelDef> change) => draft.Edit(label, p => { var l = p.FindLevel(id); if (l != null) change(l); }, structural: true);
            // A narrow panel: text fields put their value under the label at the full width, and every count gets a row
            // of its own instead of one line of "36 added, 4 moved, ..." beside a label.
            page.TextInput("Name", () => Studio.Level?.Name, v => Set("Rename level", l => l.Name = v), () => "Shown in the Custom runs list.").Stack();
            page.TextInput("Description", () => Studio.Level?.Description, v => Set("Set description", l => l.Description = v), () => "A sentence about the level, shown in its tooltip.", 300, multiline: true).Stack();
            var host = EditorCatalog.Host(lv.Host);
            StudioDock.SmallHeader(page, "Map");
            Compact(page, page.Row("Map", Esc(host?.DisplayName ?? lv.Host), "The map the level is built on."));
            if (lv.Base != null) Compact(page, page.Row("Base", lv.Base == LevelBases.Flat ? "Flat field" : "Cleared", "Everything but the castle is removed from the map."));
            var sets = new List<(string Value, string Display)> { (null, "The map's own") };
            sets.AddRange(draft.Pack.WaveSets.Where(w => w?.Host == lv.Host).Select(w => (w.Id, string.IsNullOrWhiteSpace(w.Name) ? w.Id : w.Name)));
            page.Choice("Nights", sets.Select(s => s.Display).ToList(), () => sets.FindIndex(s => s.Value == Studio.Level?.WaveSet),
                i => Set("Set nights", l => l.WaveSet = sets[i].Value), tooltip: () => "Which enemies come: the map's own nights, or one of this pack's wave sets for this map.");
            if (lv.WaveSet != null)
            {
                int ws = draft.Pack.WaveSets.FindIndex(w => w?.Id == lv.WaveSet);
                if (ws >= 0) Compact(page, page.Row("Edit nights", "▸", "Opens the wave set; Back returns here.", () => BuilderEditor.OpenWaveSet(ws)));
            }
            StudioDock.SmallHeader(page, "Paths");
            StudioDock.DetailRow(page, "Check paths", Esc(StudioShell.PathShort), false, null, StudioShell.CheckPaths,
                () => "Rescans pathfinding with your changes and draws each spawn line's route: red is blocked.");
            StudioDock.SmallHeader(page, "Changes");
            var counts = LevelCounts(lv);
            if (counts.Count == 0) page.Text(Dim("None yet"));
            foreach (var (what, n) in counts) Compact(page, page.Row(what, n.ToString()));
            if (LevelOverlay.HasEdits(lv))
                Compact(page, page.Row("Clear the layout", "", "Puts the map back the way it was (undoable).", () => Studio.Edit("Clear the layout", l => { l.BuildSlots = null; l.SpawnLines = null; l.Props = null; l.Groups = null; })));
        }

        private static void Compact(ModPage page, ModRow row) => StudioDock.Compact(row, false, 60f, page.Size.x);

        /// <summary>What the layout changes, as (what, how many), leaving out zeros.</summary>
        public static List<(string What, int Count)> LevelCounts(LevelDef lv)
        {
            var list = new List<(string, int)>();
            void Add(string what, int n) { if (n > 0) list.Add((what, n)); }
            Add("Slots moved", lv.BuildSlots?.Edit?.Count(e => e?.Pos != null || e?.RotY != null) ?? 0);
            Add("Slots added", lv.BuildSlots?.Add?.Count ?? 0);
            Add("Slots removed", lv.BuildSlots?.Remove?.Count ?? 0);
            Add("Slots repriced", lv.BuildSlots?.Edit?.Count(e => e?.Upgrades?.Any(u => u?.Cost != null) == true) ?? 0);
            Add("Scenery placed", lv.Props?.Count ?? 0);
            Add("Spawn lines moved", lv.SpawnLines?.Edit?.Count ?? 0);
            Add("Spawn lines removed", lv.SpawnLines?.Remove?.Count ?? 0);
            return list;
        }

        public static string LevelSummary(LevelDef lv)
        {
            var parts = new List<string>();
            int moved = lv.BuildSlots?.Edit?.Count(e => e?.Pos != null || e?.RotY != null) ?? 0;
            int priced = lv.BuildSlots?.Edit?.Count(e => e?.Upgrades?.Any(u => u?.Cost != null) == true) ?? 0;
            void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}"); }
            Add(moved, "moved");
            Add(lv.BuildSlots?.Add?.Count ?? 0, "added");
            Add(lv.BuildSlots?.Remove?.Count ?? 0, "removed");
            Add(priced, "repriced");
            Add(lv.Props?.Count ?? 0, "scenery");
            Add(lv.SpawnLines?.Edit?.Count ?? 0, "lines moved");
            Add(lv.SpawnLines?.Remove?.Count ?? 0, "lines removed");
            return parts.Count == 0 ? "none yet" : string.Join(", ", parts);
        }
    }
}
