using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;

/*
    The pack editor's Levels tab: a level is a map with your layout changes and, optionally, one of the pack's wave
    sets. A level opens straight into the Studio on its map (Features/Builder/Studio), where everything about it is edited.
*/
namespace PalaceMod.Builder.Editor
{
    internal static class LevelPage
    {
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
                    () => $"{host?.DisplayName ?? lv.Host ?? "no map"} · {Summary(lv)}", "Opens it in the Studio, on its map.", $"levels[{i}]", () => BuilderEditor.OpenLevel(index));
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
    }
}
