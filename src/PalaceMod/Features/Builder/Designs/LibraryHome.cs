using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Start;
using UnityEngine;
using UnityEngine.UI;

/*
    The Builder's home (docs/builder-ux.md, "Library"): My Levels and My Enemies, each a grid of tiles with the saved
    preview, name and a line of facts. Click opens (a level in the Studio, an enemy in the Enemy Studio); right-click
    for Rename, Duplicate, Delete, Export. The toolbar: New, Import, Export, search and sort. Levels and enemies of
    installed packs (mods, built-in samples, pack folders) are listed below as read-only tiles that copy into the
    library.
*/
namespace PalaceMod.Builder
{
    internal static class LibraryHome
    {
        private static ModFrame frame;
        private static string search = "";
        private static int sort;
        private static readonly string[] SortNames = ["Recent", "Name", "Map"];
        private static readonly string[] EnemySortNames = ["Recent", "Name", "Base"];
        private const float TileW = 268f, TileH = 214f, Gap = 16f, BarH = 52f;

        public static bool IsActive => frame?.IsOpen == true;

        public static void Open()
        {
            if (frame == null)
            {
                frame = new ModFrame("Builder")
                    .AddTab("My Levels", page => Build(page, DesignKind.Level), wide: true)
                    .AddTab("My Enemies", page => Build(page, DesignKind.Enemy), wide: true);
                DesignLibrary.Changed += () => { if (frame.IsOpen && ModPopup.Current == null) frame.Refresh(); };
            }
            frame.Open();
        }

        public static void ShowTab(DesignKind kind)
        {
            Open();
            frame.ShowTab(kind == DesignKind.Level ? "My Levels" : "My Enemies");
        }

        public static void Refresh() { if (frame?.IsOpen == true) frame.Refresh(); }

        // ---------------------------------------------------------------- page

        private static void Build(ModPage page, DesignKind kind)
        {
            if (EditorCatalog.Catalog == null || !DesignLibrary.Available)
            {
                page.Text("The Builder is still loading. Try again in a moment.");
                return;
            }
            float width = page.Size.x;
            bool levels = kind == DesignKind.Level;

            // Toolbar: New · Import · Export · search · sort.
            var bar = page.Custom("toolbar", BarH + Gap);
            var tools = new List<ThronefallUIElement>();
            float x = 0f;
            ModTile Tool(string label, float w, Action apply, string tip)
            {
                var t = ModTile.Create(bar, "tool " + label, new Vector2(w, BarH), null, label, 20f);
                t.Label.alignment = TMPro.TextAlignmentOptions.Center;
                t.Place(new Vector2(x, 0f));
                x += w + Gap * 0.75f;
                t.Applied += apply;
                if (tip != null) ModTooltip.Attach(t, label, () => tip);
                tools.Add(t);
                return t;
            }
            Tool(levels ? "New level" : "New enemy", 170f, () => New(kind), levels ? "Pick a map to start from" : "Pick a game enemy to start from");
            Tool("Import", 120f, OpenImport, "Add levels and enemies from a share code or a .tfpack file");
            Tool("Export", 120f, () => OpenExport(kind), "Pick levels to share as a .tfpack or a share code; the enemies they use come along");
            float sortW = 190f;
            float searchW = Mathf.Max(200f, width - x - sortW - Gap * 0.75f);
            ModTile searchTile = null;
            searchTile = Tool(SearchLabel(), searchW, () => BeginSearch(searchTile), "Filter by name, map or base enemy");
            searchTile.Label.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            var names = levels ? SortNames : EnemySortNames;
            Tool("Sort: " + names[sort], sortW, () => { sort = (sort + 1) % names.Length; frame.Refresh(); }, "Recent · Name · " + names[2]);
            page.AddElementRow(tools);

            var items = Filter(DesignLibrary.Items(kind), kind);
            if (items.Count == 0)
                page.Text(search.Length > 0 ? $"Nothing matches \"{search}\"."
                    : levels ? "No levels yet. New level starts one on any map." : "No enemies yet. New enemy starts one as a copy of a game enemy; any level can use it.");
            Grid(page, width, items.Count, (parent, i, at, size) => ItemTile(parent, items[i], at, size));

            // Installed packs: read-only, copy into the library to change.
            var installed = Installed(kind).Where(i => Matches(i.name, i.detail)).ToList();
            if (installed.Count > 0)
            {
                page.Header(levels ? "From installed packs" : "Enemies in installed packs");
                Grid(page, width, installed.Count, (parent, i, at, size) => InstalledTile(parent, installed[i], kind, at, size));
            }
        }

        private delegate ModTile TileMaker(RectTransform parent, int index, Vector2 at, Vector2 size);

        private static void Grid(ModPage page, float width, int count, TileMaker make)
        {
            int cols = Mathf.Max(1, Mathf.FloorToInt((width + Gap) / (TileW + Gap)));
            float tileW = (width - (cols - 1) * Gap) / cols;
            for (int r = 0; r * cols < count; r++)
            {
                var area = page.Custom("row " + r, TileH + Gap);
                var row = new List<ThronefallUIElement>();
                for (int c = 0; c < cols && r * cols + c < count; c++)
                    row.Add(make(area, r * cols + c, new Vector2(c * (tileW + Gap), 0f), new Vector2(tileW, TileH)));
                page.AddElementRow(row);
            }
        }

        private static string SearchLabel() => search.Length == 0 ? $"<color=#{ModStyle.Hex(ModStyle.Dim)}>Search…</color>" : "Search: " + ModFields.TMPEscape(search);

        private static void BeginSearch(ModTile tile)
        {
            ModTextInput.Begin("Search", () => tile != null && tile.gameObject.activeInHierarchy,
                shown => tile.SetText(shown == null ? SearchLabel() : "Search: " + shown), search,
                v => { search = v?.Trim() ?? ""; frame.Refresh(); }, hint: "Type to filter, Enter to apply, Esc to cancel");
        }

        private static bool Matches(params string[] fields) =>
            search.Length == 0 || fields.Any(f => f != null && f.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        private static List<LibraryItem> Filter(IReadOnlyList<LibraryItem> items, DesignKind kind)
        {
            var list = items.Where(i => Matches(i.Name, i.Id, i.Host, MapName(i.Host), BaseName(i.Base), i.Description)).ToList();
            return sort switch
            {
                1 => list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => kind == DesignKind.Level
                    ? list.OrderBy(i => MapName(i.Host), StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    : list.OrderBy(i => BaseName(i.Base), StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => list.OrderByDescending(i => i.Modified).ToList(),
            };
        }

        private static string MapName(string scene) => scene == null ? null : EditorCatalog.Host(scene)?.DisplayName ?? scene;

        private static string BaseName(string baseRef)
        {
            if (baseRef == null) return null;
            var r = ContentRef.Parse(baseRef);
            return EditorCatalog.Catalog?.FindUnit(r.Id)?.DisplayName ?? r.Id;
        }

        // ---------------------------------------------------------------- tiles

        private static ModTile ItemTile(RectTransform parent, LibraryItem item, Vector2 at, Vector2 size)
        {
            bool level = item.Kind == DesignKind.Level;
            string facts = level
                ? $"{MapName(item.Host)} · {(item.Nights == 0 ? "map's nights" : item.Nights == 1 ? "1 night" : item.Nights + " nights")}"
                : $"{BaseName(item.Base)} · {(item.Nights == 0 ? "unused" : item.Nights == 1 ? "in 1 level" : $"in {item.Nights} levels")}";
            var tile = Card(parent, "item " + item.Id, at, size, item.Name, facts, DesignLibrary.Preview(item.Kind, item.Id) ?? Fallback(item.Kind, item.Host, item.Base));
            tile.Key = item.Id;
            if (DesignLibrary.HasUnsaved(item.Kind, item.Id)) tile.SetBadge("unsaved", ModStyle.Accent);
            else if (!level && item.Side == UnitSide.Player) tile.SetBadge("ally");
            tile.Applied += () => OpenItem(item);
            tile.RightClicked += () => Actions(item, tile);
            ModTooltip.Attach(tile, item.Name, () => Tip(item));
            return tile;
        }

        private static string Tip(LibraryItem item)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Description)) lines.Add(item.Description);
            if (item.Kind == DesignKind.Enemy)
            {
                var used = DesignLibrary.UsedBy(item.Id).Select(id => DesignLibrary.Find(DesignKind.Level, id)?.Name ?? id).ToList();
                lines.Add(used.Count == 0 ? "No level uses it yet." : "Used in " + string.Join(", ", used) + ".");
            }
            if (item.Origin != null) lines.Add($"From the pack \"{item.Origin}\".");
            lines.Add($"Changed {item.Modified.ToLocalTime():d MMM yyyy, HH:mm}. Id: {item.Id}.");
            lines.Add("Click to open · right-click for more");
            return string.Join("\n", lines);
        }

        private static Sprite Fallback(DesignKind kind, string host, string baseRef) => kind == DesignKind.Level
            ? Studio.Start.Thumbnails.Get(host)
            : EditorCatalog.Catalog?.FindUnit(ContentRef.Parse(baseRef).Id)?.Icon;

        /// <summary>A tile with a picture filling its top and a name plus a line of facts at the bottom.</summary>
        private static ModTile Card(RectTransform parent, string name, Vector2 at, Vector2 size, string title, string facts, Sprite picture)
        {
            string text = $"{ModFields.TMPEscape(title)}\n<size=78%><color=#{ModStyle.Hex(ModStyle.Dim)}>{ModFields.TMPEscape(facts)}</color></size>";
            var tile = ModTile.Create(parent, name, size, null, text, 19f);
            tile.Place(at);
            var label = tile.Label.rectTransform;
            label.anchorMin = new Vector2(0f, 0f);
            label.anchorMax = new Vector2(1f, 0f);
            label.pivot = new Vector2(0.5f, 0f);
            label.anchoredPosition = new Vector2(0f, 6f);
            label.sizeDelta = new Vector2(-20f, 48f);
            tile.Label.alignment = TMPro.TextAlignmentOptions.BottomLeft;
            var img = new GameObject("Preview", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
            img.transform.SetParent(tile.Icon.transform.parent, false);
            img.transform.SetSiblingIndex(tile.Icon.transform.GetSiblingIndex());
            img.raycastTarget = false;
            img.preserveAspect = true;
            var rt = img.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(10f, 60f);
            rt.offsetMax = new Vector2(-10f, -10f);
            if (picture != null) img.sprite = picture;
            else
            {
                // No snapshot yet: a quiet panel instead of an empty hole.
                img.color = new Color(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.18f);
                img.preserveAspect = false;
            }
            return tile;
        }

        // ---------------------------------------------------------------- actions

        private static void OpenItem(LibraryItem item)
        {
            if (item.Kind == DesignKind.Level) OpenLevel(item.Id);
            else OpenEnemy(item.Id);
        }

        internal static void OpenLevel(string id)
        {
            var draft = DesignLibrary.OpenLevel(id);
            if (draft == null) { Notify.Show($"The level \"{id}\" can't be read", NotifyKind.Error, 5f, "builder-library"); return; }
            // On success the home stays up under the clouds; the scene transition closes it.
            if (!Studio.Studio.Open(draft, id, out string error))
                Notify.Show("Can't open the Studio: " + error, NotifyKind.Error, 6f, "builder-library");
        }

        internal static void OpenEnemy(string id)
        {
            var draft = DesignLibrary.OpenEnemies();
            if (draft == null) return;
            EnemyEditing.Edit(draft, id, _ => Refresh());
        }

        private static void New(DesignKind kind)
        {
            if (kind == DesignKind.Level)
            {
                var draft = DesignLibrary.NewLevelWorkspace();
                if (draft != null) StartPopup.Open(draft);
                return;
            }
            var enemies = DesignLibrary.OpenEnemies();
            if (enemies == null) return;
            EnemyEditing.PickAndNew(enemies, id =>
            {
                // A new enemy is a library item at once (its tile shows), not only when the editor saves.
                if (enemies.Save(out string message)) Refresh();
                else Notify.Show(message, NotifyKind.Error, 5f, "builder-library");
            });
        }

        private static void Actions(LibraryItem item, ModTile tile)
        {
            bool level = item.Kind == DesignKind.Level;
            ModPopup.Open(item.Name, page =>
            {
                page.Row(level ? "Open in the Studio" : "Open in the Enemy Studio", "", (string)null, () => { ModPopup.Current?.Close(); OpenItem(item); });
                page.TextInput("Name", () => item.Name, v =>
                {
                    if (string.IsNullOrWhiteSpace(v) || v.Trim() == item.Name) return;
                    if (DesignLibrary.Rename(item.Kind, item.Id, v.Trim())) item.Name = v.Trim();
                    ModPopup.Current?.Close();
                    Refresh();
                });
                page.Row("Duplicate", "", "A copy with its own id; the original is untouched.", () =>
                {
                    ModPopup.Current?.Close();
                    string copy = DesignLibrary.Duplicate(item.Kind, item.Id);
                    if (copy != null) Notify.Show($"Made \"{DesignLibrary.Find(item.Kind, copy)?.Name ?? copy}\"", NotifyKind.Success, 3f, "builder-library");
                    Refresh();
                });
                if (level) page.Row("Export…", "", "Share this level (and the enemies it uses).", () => { ModPopup.Current?.Close(); OpenExport(DesignKind.Level, item.Id); });
                else
                {
                    var used = DesignLibrary.UsedBy(item.Id);
                    page.Row("Used in", used.Count == 0 ? "no level" : used.Count == 1 ? "1 level" : $"{used.Count} levels",
                        used.Count == 0 ? null : string.Join("\n", used.Select(id => DesignLibrary.Find(DesignKind.Level, id)?.Name ?? id)));
                }
                page.Row("Delete…", "", "Moves it to the library's trash folder.", () => { ModPopup.Current?.Close(); ConfirmDelete(item); });
            }, near: tile.Rect);
        }

        private static void ConfirmDelete(LibraryItem item)
        {
            bool level = item.Kind == DesignKind.Level;
            var used = level ? [] : DesignLibrary.UsedBy(item.Id);
            ModPopup.Open($"Delete \"{item.Name}\"?", page =>
            {
                if (used.Count > 0)
                    page.Text($"Used in {string.Join(", ", used.Select(id => DesignLibrary.Find(DesignKind.Level, id)?.Name ?? id))}. Those nights will need another enemy before they can be played.");
                page.Text("It goes to the library's trash folder, so a file can still be recovered from there.");
                page.Button("Delete", () =>
                {
                    ModPopup.Current?.Close();
                    if (DesignLibrary.Delete(item.Kind, item.Id)) Notify.Show($"Deleted \"{item.Name}\"", NotifyKind.Info, 3f, "builder-library");
                    Refresh();
                });
                page.Button("Keep it", () => ModPopup.Current?.Close());
            }, width: 620f);
        }

        // ---------------------------------------------------------------- installed packs

        private sealed class InstalledItem
        {
            public PackEntry Entry;
            public string Id, name, detail, Host, Base;
        }

        private static List<InstalledItem> Installed(DesignKind kind)
        {
            var list = new List<InstalledItem>();
            foreach (var entry in BuilderServices.Packs.Packs)
            {
                if (entry.Pack == null || entry.Id == DesignLibrary.PackId) continue;
                string packName = string.IsNullOrWhiteSpace(entry.Pack.Name) ? entry.Id : entry.Pack.Name;
                if (kind == DesignKind.Level)
                    foreach (var lv in entry.Pack.Levels ?? [])
                        list.Add(new InstalledItem { Entry = entry, Id = lv.Id, name = string.IsNullOrWhiteSpace(lv.Name) ? lv.Id : lv.Name, detail = packName, Host = lv.Host });
                else
                    foreach (var u in entry.Pack.Units ?? [])
                        list.Add(new InstalledItem { Entry = entry, Id = u.Id, name = string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name, detail = packName, Base = u.Base });
            }
            return list;
        }

        private static ModTile InstalledTile(RectTransform parent, InstalledItem item, DesignKind kind, Vector2 at, Vector2 size)
        {
            var tile = Card(parent, "installed " + item.Entry.Id + "/" + item.Id, at, size, item.name, item.detail, Fallback(kind, item.Host, item.Base));
            tile.SetBadge(item.Entry.Source == PackSource.BuiltIn ? "built in" : item.Entry.Source == PackSource.Plugin ? "mod" : "pack");
            tile.SetDim(true, 0.75f);
            tile.Applied += () => CopyIn(item, kind);
            ModTooltip.Attach(tile, item.name, () => $"In the pack \"{item.detail}\" ({item.Entry.Id}). Click to copy it into {(kind == DesignKind.Level ? "My Levels" : "My Enemies")} and edit the copy.");
            return tile;
        }

        private static void CopyIn(InstalledItem item, DesignKind kind)
        {
            try
            {
                DesignPack subset;
                if (kind == DesignKind.Level)
                    subset = Studio.Play.TestPacks.Play(item.Entry.Pack, item.Id, out string error) ?? throw new InvalidOperationException(error);
                else
                {
                    subset = PackSerializer.Clone(item.Entry.Pack);
                    var keep = LibraryStore.UnitClosure([item.Id], subset.FindUnit);
                    subset.Units = subset.Units.Where(u => keep.Contains(u.Id)).ToList();
                    subset.Levels = [];
                    subset.WaveSets = [];
                }
                var result = DesignLibrary.Import(subset, item.Entry.Folder);
                Notify.Show($"Copied {result.Summary()} into your library", NotifyKind.Success, 4f, "builder-library");
                Refresh();
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is System.IO.IOException)
            {
                Notify.Show("Couldn't copy it: " + e.Message, NotifyKind.Error, 5f, "builder-library");
            }
        }

        // ---------------------------------------------------------------- import / export

        private static void OpenImport()
        {
            ModPopup.Open("Import", page =>
            {
                page.Row("Paste a share code", "", "Reads a code (TFB1:…) from the clipboard. Its levels and enemies are added to your library; nothing of yours is replaced.", () =>
                {
                    ModPopup.Current?.Close();
                    var entry = Sharing.SharingUI.ImportFromClipboard();
                    if (entry != null) Refresh();
                });
                var files = PalaceMod.BuilderSharingManager.PendingImports();
                if (files.Count > 0)
                {
                    page.Header("Files in the import folder");
                    foreach (var f in files)
                    {
                        string file = f;
                        page.Row(System.IO.Path.GetFileName(file), "import", (string)null, () =>
                        {
                            ModPopup.Current?.Close();
                            if (PalaceMod.BuilderSharingManager.ImportAndFile(file) != null) Refresh();
                        });
                    }
                }
                page.Row("Open the import folder", "", "Drop .tfpack files (or .txt files holding a code) here; they're imported within a few seconds when automatic import is on.",
                    () => { string folder = PalaceMod.BuilderSharingManager.ImportFolder; if (folder != null) { System.IO.Directory.CreateDirectory(folder); Application.OpenURL("file://" + folder); } });
            }, width: 640f);
        }

        private static readonly HashSet<string> exportLevels = new(StringComparer.Ordinal);
        private static readonly HashSet<string> exportEnemies = new(StringComparer.Ordinal);
        private static string exportName;

        internal static void OpenExport(DesignKind kind, string preselect = null)
        {
            exportLevels.RemoveWhere(id => !DesignLibrary.Store.HasLevel(id));
            exportEnemies.RemoveWhere(id => !DesignLibrary.Store.HasUnit(id));
            if (preselect != null)
            {
                exportLevels.Clear();
                exportEnemies.Clear();
                (kind == DesignKind.Level ? exportLevels : exportEnemies).Add(preselect);
                exportName = DesignLibrary.Find(kind, preselect)?.Name;
            }
            ModPopup.Open("Export", BuildExport, width: 700f);
        }

        private static void BuildExport(ModPage page)
        {
            var levels = DesignLibrary.Items(DesignKind.Level).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var enemies = DesignLibrary.Items(DesignKind.Enemy).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var pulled = new HashSet<string>(exportLevels.SelectMany(id => enemies.Where(e => DesignLibrary.UsedBy(e.Id).Contains(id)).Select(e => e.Id)));
            page.TextInput("Pack name", () => exportName ?? DefaultExportName(levels), v => exportName = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
            page.Header("Levels");
            if (levels.Count == 0) page.Text("No levels yet.");
            foreach (var lv in levels)
            {
                string id = lv.Id;
                page.Toggle(lv.Name, () => exportLevels.Contains(id), on => { if (on) exportLevels.Add(id); else exportLevels.Remove(id); ModPopup.Current?.Refresh(); },
                    () => $"{MapName(lv.Host)} · {lv.Nights} nights");
            }
            page.Header("Enemies");
            foreach (var e in enemies)
            {
                string id = e.Id;
                if (pulled.Contains(id)) { page.Row(e.Name, "with its levels", "A chosen level uses it, so it's included."); continue; }
                page.Toggle(e.Name, () => exportEnemies.Contains(id), on => { if (on) exportEnemies.Add(id); else exportEnemies.Remove(id); ModPopup.Current?.Refresh(); });
            }
            bool any = exportLevels.Count > 0 || exportEnemies.Count > 0;
            page.Button("Save as a .tfpack file", () =>
            {
                if (!any) { Notify.Show("Pick something to export", NotifyKind.Info, 3f, "builder-library"); return; }
                try
                {
                    string path = DesignLibrary.ExportFile(exportLevels, exportEnemies, exportName ?? DefaultExportName(levels));
                    Notify.Show($"Saved {System.IO.Path.GetFileName(path)} in the exports folder", NotifyKind.Success, 5f, "builder-library");
                    ModPopup.Current?.Close();
                    Application.OpenURL("file://" + System.IO.Path.GetDirectoryName(path));
                }
                catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is System.IO.IOException)
                {
                    Notify.Show("Couldn't export: " + e.Message, NotifyKind.Error, 6f, "builder-library");
                }
            }, "Writes one file with the levels, every enemy they use and their asset files. Opens the exports folder.");
            page.Button("Copy a share code", () =>
            {
                if (!any) { Notify.Show("Pick something to export", NotifyKind.Info, 3f, "builder-library"); return; }
                try
                {
                    string code = DesignLibrary.ShareCode(exportLevels, exportEnemies, exportName ?? DefaultExportName(levels));
                    GUIUtility.systemCopyBuffer = code;
                    Notify.Show($"Share code copied ({code.Length} characters)", NotifyKind.Success, 4f, "builder-library");
                    ModPopup.Current?.Close();
                }
                catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is NotSupportedException || e is System.IO.IOException)
                {
                    Notify.Show("Couldn't make a code: " + e.Message, NotifyKind.Warning, 6f, "builder-library");
                }
            }, "Text to paste anywhere. Files (imported meshes, sounds) only travel in a .tfpack.");
        }

        private static string DefaultExportName(List<LibraryItem> levels)
        {
            var chosen = levels.Where(l => exportLevels.Contains(l.Id)).ToList();
            return chosen.Count == 1 ? chosen[0].Name : chosen.Count > 1 ? $"{chosen[0].Name} and {chosen.Count - 1} more" : "My enemies";
        }
    }
}
