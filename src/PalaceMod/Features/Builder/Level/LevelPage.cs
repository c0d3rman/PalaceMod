using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;

/*
    The pack editor's Levels tab and level screen (Builder stage 2): a level is a map with your layout changes and,
    optionally, one of the pack's wave sets. The layout itself is edited on the map (LayoutEditor).
*/
namespace PalaceMod.Builder.Editor
{
    internal sealed class LevelPage : EditorPage
    {
        public int Index { get; set; }

        private LevelDef Level => Index >= 0 && Index < (Pack?.Levels?.Count ?? 0) ? Pack.Levels[Index] : null;
        private string P => $"levels[{Index}]";

        public LevelPage() : base("Level")
        {
            Frame.SetContent(Page(Build));
        }

        // ---- The Levels tab of the pack screen ---------------------------------------------------------------------

        public static void BuildList(PackPage owner, ModPage page)
        {
            var draft = BuilderEditor.Draft;
            if (draft == null) return;
            owner.ShowStatus(page);
            page.Header("Levels");
            var levels = draft.Pack.Levels ?? [];
            if (levels.Count == 0) page.Text("A level is a map with your own layout: move, add or remove build slots, change their costs, add scenery and move where enemies spawn. Pick a map to start.");
            for (int i = 0; i < levels.Count; i++)
            {
                int index = i;
                var lv = levels[i];
                if (lv == null) continue;
                var host = EditorCatalog.Host(lv.Host);
                owner.ListRow(page, string.IsNullOrWhiteSpace(lv.Name) ? lv.Id ?? "(unnamed)" : lv.Name,
                    () => $"{host?.DisplayName ?? lv.Host ?? "no map"} · {Summary(lv)}", "Open to edit its layout and settings.", $"levels[{i}]", () => BuilderEditor.OpenLevel(index));
            }
            page.Button("New level on a map", NewLevel, "Starts a level from a map as it is; then edit its layout on the map.");
        }

        private static void NewLevel()
        {
            var draft = BuilderEditor.Draft;
            Picker.One("Choose a Map", WavePage.HostItems(), null, scene =>
            {
                var host = EditorCatalog.Host(scene);
                if (host == null || draft == null) return;
                int index = draft.Pack.Levels?.Count ?? 0;
                draft.Edit("Add level", p =>
                {
                    p.Levels ??= [];
                    string name = $"{host.DisplayName} Layout";
                    p.Levels.Add(new LevelDef
                    {
                        Id = EditorCatalog.UniqueId(Ids.FromName(name), p.Levels.Select(l => l?.Id)),
                        Name = name,
                        Host = host.Scene,
                    });
                }, structural: true);
                BuilderEditor.OpenLevel(index);
            });
        }

        public static string Summary(LevelDef lv)
        {
            var parts = new List<string>();
            int moved = lv.BuildSlots?.Edit?.Count(e => e?.Pos != null || e?.RotY != null) ?? 0;
            int priced = lv.BuildSlots?.Edit?.Count(e => e?.Upgrades?.Any(u => u?.Cost != null) == true) ?? 0;
            void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}"); }
            Add(moved, "moved");
            Add(lv.BuildSlots?.Add?.Count ?? 0, "added");
            Add(lv.BuildSlots?.Remove?.Count ?? 0, "removed");
            Add(priced, "repriced");
            Add(lv.Props?.Count ?? 0, (lv.Props?.Count ?? 0) == 1 ? "prop" : "props");
            Add(lv.SpawnLines?.Edit?.Count ?? 0, "spawn lines moved");
            return parts.Count == 0 ? "no layout changes yet" : string.Join(", ", parts);
        }

        // ---- One level ---------------------------------------------------------------------------------------------

        private void Build(ModPage page)
        {
            var lv = Level;
            if (lv == null) { page.Text("This level no longer exists."); return; }
            Frame.SetTitle(string.IsNullOrWhiteSpace(lv.Name) ? "Level" : lv.Name);
            page.Header("Level");
            Text(page, "Name", P + ".name", () => Level?.Name, (p, v) => p.Levels[Index].Name = v, "Shown in the Custom runs list.");
            Text(page, "Description", P + ".description", () => Level?.Description, (p, v) => p.Levels[Index].Description = v, "A sentence about the level, shown in its tooltip.", 300);
            var host = EditorCatalog.Host(lv.Host);
            Marked(page, "Map", () => host?.DisplayName ?? lv.Host ?? "?", "The map the level is built on. Make a new level to use another map.", P + ".host", null);
            var sets = new List<(string Value, string Display)> { (null, "The map's own nights") };
            sets.AddRange(Pack.WaveSets.Where(w => w?.Host == lv.Host).Select(w => (w.Id, string.IsNullOrWhiteSpace(w.Name) ? w.Id : w.Name)));
            Choice(page, "Nights", P + ".waveSet", sets, () => Level?.WaveSet, (p, v) => p.Levels[Index].WaveSet = v,
                "Which enemies come: the map's own nights, or one of this pack's wave sets for this map.");

            page.Header("Layout");
            Marked(page, "Changes", () => Summary(Level ?? lv), "Your changes to the map. Edit them on the map.", P + ".buildSlots", null);
            page.Row("Edit the layout on the map", "", "Opens the map in the layout editor: move, add and remove build slots, change their costs, add scenery and move spawn lines.", EditLayout);
            if (LevelOverlay.HasEdits(lv)) Danger(page, "Clear the layout", "Puts the map back the way it was (you can undo this).", () =>
                Draft.Edit("Clear the layout", p => { var l = p.Levels[Index]; l.BuildSlots = null; l.SpawnLines = null; l.Props = null; }, structural: true));

            page.Header("Play");
            var test = page.Row("Test play", Draft.ErrorCount > 0 ? Dim("fix the pack's problems first") : "",
                "Saves the pack and plays this level (a custom run: no saves, scores or achievements).", Play);
            Danger(page, "Delete level", "Removes this level from the pack (you can undo this).", () =>
            {
                Draft.Edit("Delete level", p => p.Levels.RemoveAt(Index), structural: true);
                Frame.Close();
            });
        }

        private void EditLayout()
        {
            var lv = Level;
            if (lv == null) return;
            if (!LayoutEditor.Launch(Draft, lv.Id, out string error))
            {
                Notify.Show("Can't open the layout editor: " + error, NotifyKind.Error, 6f, key: "builder-editor");
                return;
            }
            BuilderEditor.CloseAll();
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "_StartMenu") UIFrameManager.instance?.CloseAllFrames();
        }

        private void Play()
        {
            var lv = Level;
            if (lv == null) return;
            if (!Draft.ReadOnly && Draft.Dirty && Draft.ErrorCount == 0 && !Draft.Save(out string saveError))
                Notify.Show(saveError, NotifyKind.Warning, key: "builder-editor");
            if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = Draft.Pack, LevelId = lv.Id }, out string error))
            {
                Notify.Show("Can't test: " + error, NotifyKind.Error, 6f, key: "builder-editor");
                return;
            }
            BuilderEditor.CloseAll();
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "_StartMenu") UIFrameManager.instance?.CloseAllFrames();
        }
    }
}
