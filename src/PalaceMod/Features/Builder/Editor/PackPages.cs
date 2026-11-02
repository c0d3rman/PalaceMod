using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Builder.Editor
{
    /// <summary>The editor's first screen: every installed pack, plus New and Import.</summary>
    internal sealed class PackListPage : EditorPage
    {
        public PackListPage() : base("Builder")
        {
            Frame.SetContent(Page(Build, needsDraft: false));
            BuilderServices.Packs.Changed += () => OnDraftChanged(true);
        }

        private void Build(ModPage page)
        {
            if (EditorCatalog.Catalog == null || BuilderServices.Packs.Root == null)
            {
                page.Text("The Builder is still loading. Try again in a moment.");
                return;
            }
            var packs = BuilderServices.Packs.Packs;
            var mine = packs.Where(p => p.Source == PackSource.User).OrderBy(p => p.Pack?.Name ?? p.Id).ToList();
            var installed = packs.Where(p => p.Source != PackSource.User).OrderBy(p => p.Pack?.Name ?? p.Id).ToList();

            page.Header("Your designs");
            if (mine.Count == 0) page.Text("Make enemies and nights of your own: start a pack, add units and wave sets, then test-play them on any map.");
            foreach (var entry in mine) PackRow(page, entry);
            page.Button("New pack", BuilderEditor.NewPack, "Start an empty pack of your own.");
            page.Button("Import share code", BuilderEditor.ImportShareCode, "Adds the pack whose share code is on the clipboard (it never replaces one you have).");

            if (installed.Count > 0)
            {
                page.Header("Installed");
                foreach (var entry in installed) PackRow(page, entry);
            }

            page.Header("Files");
            page.Button("Open packs folder", BuilderEditor.OpenFolder, "Packs are folders of JSON in BepInEx/PalaceBuilder/packs. Edits made in a text editor show up here.");
        }

        private void PackRow(ModPage page, PackEntry entry)
        {
            var draft = BuilderEditor.ExistingDraft(entry.Id);
            var pack = draft?.Pack ?? entry.Pack;
            string id = entry.Id;
            if (pack == null)
            {
                page.Row(entry.Id, Dim("can't be read"), string.Join("\n", entry.Issues.Select(i => i.Message)));
                return;
            }
            int errors = draft?.ErrorCount ?? entry.Issues.Count(i => i.Severity == Severity.Error);
            var parts = new List<string>();
            parts.Add(Count(pack.Units.Count, "unit"));
            parts.Add(Count(pack.WaveSets.Count, "wave set"));
            string value = string.Join(" · ", parts);
            if (draft?.Dirty == true) value = Accent("unsaved") + "   " + value;
            if (entry.ReadOnly) value += "   " + Dim(entry.Source == PackSource.BuiltIn ? "built in" : "mod");
            string tip = (string.IsNullOrWhiteSpace(pack.Description) ? "" : pack.Description + "\n") +
                (pack.Author != null ? $"By {pack.Author}. " : "") +
                (errors > 0 ? $"<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>{Count(errors, "problem")} to fix before it can be played.</color>" : "Ready to play.");
            var field = Marked(page, string.IsNullOrWhiteSpace(pack.Name) ? id : pack.Name, () => value, tip, null, () => BuilderEditor.OpenPack(id));
            field.SetMarker(errors > 0 ? ModFieldMarker.Error : ModFieldMarker.None);
        }

        internal static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
    }

    /// <summary>One pack: its units, wave sets, details and actions, and its problems.</summary>
    internal sealed class PackPage : EditorPage
    {
        public PackPage() : base("Edit Pack")
        {
            Frame.AddTab("Units", Page(BuildUnits))
                .AddTab("Wave Sets", Page(BuildWaveSets))
                .AddTab("Pack", Page(BuildPack))
                .AddTab("Problems", Page(BuildProblems));
        }

        private void BuildUnits(ModPage page)
        {
            Status(page);
            var units = Pack.Units;
            foreach (var side in new[] { UnitSide.Enemy, UnitSide.Player })
            {
                var ofSide = Enumerable.Range(0, units.Count).Where(i => (units[i]?.Side ?? UnitSide.Enemy) == side).ToList();
                if (ofSide.Count == 0 && side == UnitSide.Player) continue;
                page.Header(side == UnitSide.Enemy ? "Enemies" : "Player units");
                foreach (int i in ofSide)
                {
                    int index = i;
                    var u = units[i];
                    var b = EditorCatalog.BaseOf(Pack, u.Base);
                    Marked(page, string.IsNullOrWhiteSpace(u.Name) ? u.Id ?? "(unnamed)" : u.Name,
                        () => Dim("based on ") + (b?.DisplayName ?? ContentRef.Parse(u.Base).Id ?? "?"),
                        string.IsNullOrWhiteSpace(u.Description) ? EditorCatalog.Stats(b) : u.Description,
                        $"units[{i}]", () => BuilderEditor.OpenUnit(index), EditorCatalog.UnitIcon(Draft, u.Id));
                }
                if (side == UnitSide.Enemy && ofSide.Count == 0) page.Text("No custom enemies yet. Each one starts as a copy of a vanilla enemy that you then change.");
            }
            page.Button("New enemy", () => NewUnit(), "Pick a vanilla enemy to start from.");
        }

        private void NewUnit()
        {
            Picker.One("Start From", UnitPage.BaseItems(Pack, UnitSide.Enemy), null, baseId =>
            {
                var b = EditorCatalog.Catalog?.FindUnit(baseId);
                int index = Pack.Units.Count;
                Draft.Edit("Add enemy", p =>
                {
                    string name = $"New {b?.DisplayName ?? "Enemy"}";
                    p.Units.Add(new UnitDef
                    {
                        Id = EditorCatalog.UniqueId(Ids.FromName(name), p.Units.Select(u => u.Id)),
                        Side = UnitSide.Enemy,
                        Base = ContentRef.Vanilla(baseId),
                        Name = name,
                    });
                }, structural: true);
                BuilderEditor.OpenUnit(index);
            }, "Your enemy starts with this one's model, stats and weapon. You can change all of it.");
        }

        private void BuildWaveSets(ModPage page)
        {
            Status(page);
            page.Header("Wave sets");
            if (Pack.WaveSets.Count == 0) page.Text("A wave set is a run: the nights of enemies on one map. Start from a map's own waves to tweak them, or from nothing.");
            for (int i = 0; i < Pack.WaveSets.Count; i++)
            {
                int index = i;
                var ws = Pack.WaveSets[i];
                var host = EditorCatalog.Host(ws.Host);
                Marked(page, string.IsNullOrWhiteSpace(ws.Name) ? ws.Id ?? "(unnamed)" : ws.Name,
                    () => $"{host?.DisplayName ?? ws.Host ?? "no map"} · {PackListPage.Count(ws.Nights?.Count ?? 0, "night")}",
                    "Open to edit its nights.", $"waveSets[{i}]", () => BuilderEditor.OpenWaveSet(index));
            }
            page.Button("New wave set from a map's waves", () => NewWaveSet(copyVanilla: true), "Copies a map's vanilla nights to edit.");
            page.Button("New empty wave set", () => NewWaveSet(copyVanilla: false), "One night with one group, on a map you pick.");
        }

        private void NewWaveSet(bool copyVanilla)
        {
            Picker.One("Choose a Map", WavePage.HostItems(), null, scene =>
            {
                var host = EditorCatalog.Host(scene);
                if (host == null) return;
                int index = Pack.WaveSets.Count;
                Draft.Edit("Add wave set", p =>
                {
                    WaveSetDef ws = copyVanilla ? EditorCatalog.Copy(host.VanillaWaves) : null;
                    ws ??= new WaveSetDef
                    {
                        Nights = [new NightDef { Spawns = [WavePage.DefaultSpawn(p, host)] }],
                    };
                    string name = copyVanilla ? $"{host.DisplayName} Remix" : $"{host.DisplayName} Nights";
                    ws.Name = name;
                    ws.Host = host.Scene;
                    ws.Id = EditorCatalog.UniqueId(Ids.FromName(name), p.WaveSets.Select(w => w.Id));
                    p.WaveSets.Add(ws);
                }, structural: true);
                BuilderEditor.OpenWaveSet(index);
            });
        }

        private void BuildPack(ModPage page)
        {
            Status(page);
            page.Header("Details");
            Text(page, "Name", "name", () => Pack.Name, (p, v) => p.Name = v, "Shown in pack lists and to players you share it with.");
            Text(page, "Id", "id", () => Pack.Id, (p, v) => p.Id = v ?? p.Id,
                "Unique pack id and folder name: lowercase letters, digits, '.', '_' or '-'. Changing it moves the pack's folder when you save.");
            Text(page, "Author", "author", () => Pack.Author, (p, v) => p.Author = v, "Your name, shown with the pack.");
            Text(page, "Description", "description", () => Pack.Description, (p, v) => p.Description = v, "A sentence or two about the pack.", 300);

            page.Header("Actions");
            var save = page.Row(Draft.ReadOnly ? "Save a copy" : "Save", Draft.Dirty ? Accent("unsaved changes") : Dim(Draft.ReadOnly ? "read-only pack" : "saved"),
                Draft.ReadOnly ? "This pack came with PalaceMod or another mod, so changes are saved as a new pack of your own." : "Writes the pack to its folder.",
                BuilderEditor.Save);
            page.Row("Undo", Draft.History.CanUndo ? Escape(Draft.History.UndoLabel) : Dim("nothing"), "Cmd/Ctrl+Z also undoes while the Builder is open.", BuilderEditor.Undo);
            page.Row("Redo", Draft.History.CanRedo ? Escape(Draft.History.RedoLabel) : Dim("nothing"), "Cmd/Ctrl+Shift+Z also redoes.", BuilderEditor.Redo);
            if (Pack.WaveSets.Count > 0)
            {
                var sets = Pack.WaveSets.Select(w => (w.Id, string.IsNullOrWhiteSpace(w.Name) ? w.Id : w.Name)).ToList();
                testSet = sets.Any(s => s.Id == testSet) ? testSet : sets[0].Id;
                var test = page.Stepper("Test play", () => sets.First(s => s.Id == testSet).Item2,
                    delta =>
                    {
                        int i = sets.FindIndex(s => s.Id == testSet);
                        testSet = sets[(i + System.Math.Sign(delta) + sets.Count) % sets.Count].Id;
                    },
                    () => BuilderEditor.TestWaveSet(testSet),
                    () => "Plays the wave set (left/right picks which). Scores and saves aren't recorded." + (Draft.ErrorCount > 0 ? "\nFix the pack's problems first." : ""));
                test.SetMarker(Draft.ErrorCount > 0 ? ModFieldMarker.Error : ModFieldMarker.None);
            }
            page.Button("Copy share code", BuilderEditor.CopyShareCode, "Copies a code for this pack to the clipboard, to paste in a message. Images and other files aren't included.");
            if (Draft.SavedJson != null && Draft.Dirty) Danger(page, "Revert to saved", "Throws away the changes since the last save (you can undo this).", Draft.Revert);
            if (!Draft.ReadOnly && Draft.SourceId != null)
            {
                Danger(page, "Delete pack", "Moves the pack to BepInEx/PalaceBuilder/trash.", () =>
                {
                    string id = Draft.SourceId;
                    if (BuilderServices.Packs.Delete(id))
                    {
                        Notify.Show($"Moved {id} to the trash folder", NotifyKind.Success, key: "builder-editor");
                        Frame.Close();
                    }
                });
            }
        }

        private string testSet;

        private void BuildProblems(ModPage page)
        {
            var issues = Draft.Issues;
            if (issues.Count == 0)
            {
                page.Text("No problems. The pack is ready to play and share.");
                return;
            }
            foreach (var group in issues.GroupBy(i => i.Severity).OrderByDescending(g => g.Key))
            {
                page.Header(group.Key == Severity.Error ? "Must fix" : "Worth a look");
                foreach (var issue in group)
                {
                    string path = issue.Path;
                    var row = page.Row(Capital(issue.Message), Dim(Where(issue.Path)), $"{Where(issue.Path)}: {issue.Message}\nApply to go there.", () => BuilderEditor.GoTo(path));
                    row.SetLabel(Capital(Escape(issue.Message)));
                }
            }
        }

        /// <summary>A short "where" for an issue path: "Iron Brute, hit points".</summary>
        private string Where(string path)
        {
            if (string.IsNullOrEmpty(path)) return "pack";
            var parts = path.Split('.');
            var m = System.Text.RegularExpressions.Regex.Match(parts[0], @"^(units|waveSets|levels)\[(\d+)\]$");
            if (!m.Success) return parts[0];
            int i = int.Parse(m.Groups[2].Value);
            string owner = m.Groups[1].Value switch
            {
                "units" => i < Pack.Units.Count ? Pack.Units[i].Name ?? Pack.Units[i].Id : "unit",
                "waveSets" => i < Pack.WaveSets.Count ? Pack.WaveSets[i].Name ?? Pack.WaveSets[i].Id : "wave set",
                _ => "level",
            };
            var night = System.Text.RegularExpressions.Regex.Match(path, @"nights\[(\d+)\]");
            if (night.Success) owner += $", night {int.Parse(night.Groups[1].Value) + 1}";
            return owner;
        }

        /// <summary>A one-line status at the top of each tab: problems and unsaved state.</summary>
        private void Status(ModPage page)
        {
            int errors = Draft.ErrorCount;
            int warnings = Draft.Issues.Count - errors;
            string text = errors > 0 ? $"<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>{PackListPage.Count(errors, "problem")} to fix</color>"
                : warnings > 0 ? $"<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Warning))}>{PackListPage.Count(warnings, "warning")}</color>" : "Ready to play";
            if (Draft.Dirty) text += "   ·   " + Accent(Draft.ReadOnly ? "unsaved (save a copy on the Pack tab)" : "unsaved");
            if (Draft.ReadOnly && !Draft.Dirty) text += "   ·   " + Dim("read-only: changes save as a copy");
            page.Text(text);
        }
    }
}
