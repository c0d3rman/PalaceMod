using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Economy;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    The palette's tabs: Slots (every slot kind on the host), Scenery (the host's scenery meshes), Spawns (the host's
    spawn lines) and the Library (StudioServices.AssetBrowser). Clicking a kind arms the Place tool.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioPalette
    {
        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";

        public static string CostChain(IReadOnlyList<int> costs) => costs.Count == 0 ? "" : string.Join("·", costs);

        // ---- Slots ----------------------------------------------------------------------------------------------

        public static StudioPlaceable SlotKind(string type)
        {
            var sample = StudioScene.Natives.FirstOrDefault(n => n.CanBase && n.Type == type && n.Look != null);
            if (sample == null) return null;
            return new StudioPlaceable
            {
                Id = "slot:" + type,
                Name = type,
                Icon = PlannerIcons.Kind(type),
                MakeGhost = parent =>
                {
                    var near = StudioScene.Native(StudioEdits.NearestBase(type, StudioCamera.Pivot)) ?? sample;
                    var g = UnityEngine.Object.Instantiate(near.Look, parent);
                    g.transform.rotation = sample.Rot;
                    return g;
                },
                Place = (pos, yaw) => StudioEdits.AddSlot(StudioEdits.NearestBase(type, pos) ?? sample.Name, pos, yaw),
            };
        }

        public static void ArmSlot(string type)
        {
            var p = SlotKind(type);
            if (p == null) return;
            float baseYaw = StudioScene.Natives.First(n => n.CanBase && n.Type == type).Rot.eulerAngles.y;
            StudioShell.RememberYaw(p.Id, baseYaw);
            StudioShell.Arm(p, baseYaw);
        }

        public static void Slots(ModPage page)
        {
            var kinds = StudioScene.Natives.Where(n => n.CanBase && n.Look != null).GroupBy(n => n.Type).OrderBy(g => g.Key).ToList();
            if (kinds.Count == 0) { page.Text("No slot kinds on this map"); return; }
            string armed = StudioShell.Tool == StudioShell.Place ? StudioShell.Place.Armed?.Id : null;
            foreach (var g in kinds)
            {
                string type = g.Key;
                int onMap = StudioScene.Items.Count(i => (i.Kind is StudioItemKind.Slot or StudioItemKind.Added) && i.TypeName == type && !i.Removed);
                var row = page.Row($"{type}  <size=75%>{Dim(CostChain(g.First().Costs))}</size>", $"×{onMap}", () => $"Place a {type.ToLowerInvariant()}: copies the nearest {type.ToLowerInvariant()} on the map (its costs and upgrades).",
                    () => ArmSlot(type), PlannerIcons.Kind(type));
                Shrink(row, armed == "slot:" + type);
            }
        }

        // ---- Scenery --------------------------------------------------------------------------------------------

        public static StudioPlaceable PropKind(PropBase b) => new()
        {
            Id = b.Id,
            Name = b.Name,
            MakeGhost = parent =>
            {
                var g = LayoutScene.MakeProp(StudioScene.Scene, b, "ghost", Vector3.zero, 0f, StudioEdits.StartScale(b), false);
                g.transform.SetParent(parent, true);
                return g;
            },
            Place = (pos, yaw) => StudioEdits.AddProp(b.Id, b.Name, pos, yaw, StudioEdits.StartScale(b)),
        };

        public static void Scenery(ModPage page)
        {
            page.TextInput("Filter", () => StudioDock.SceneryFilter, v => { StudioDock.SceneryFilter = v ?? ""; StudioDock.OnRecentChanged(); });
            string f = StudioDock.SceneryFilter?.Trim() ?? "";
            var list = StudioScene.PropBases.Where(b => f.Length == 0 || b.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (list.Count == 0) page.Text(f.Length == 0 ? "No scenery on this map" : "No match");
            string armed = StudioShell.Tool == StudioShell.Place ? StudioShell.Place.Armed?.Id : null;
            foreach (var b in list)
            {
                var kind = b;
                var row = page.Row(b.Name, Dim($"×{b.Count}"), () => "Place a copy of this map's scenery. Solid by default: units walk around it.", () => StudioShell.Arm(PropKind(kind)));
                Shrink(row, armed == b.Id);
            }
        }

        // ---- Spawns ---------------------------------------------------------------------------------------------

        public static void Spawns(ModPage page)
        {
            var lines = StudioScene.Items.Where(i => i.Kind == StudioItemKind.Line).ToList();
            if (lines.Count == 0) page.Text("No spawn lines on this map");
            foreach (var l in lines)
            {
                var item = l;
                var row = page.Row(l.Name, Dim(l.Moved ? "moved" : l.TypeName.Replace("Spawn line", "").Trim()), () => "Select it on the map (drag it to move where enemies come from).",
                    () => { StudioSelection.Select([item.Key], SelectOp.Replace); StudioCamera.FrameItems([item]); });
                Shrink(row, StudioSelection.Contains(l.Key), 110f);
            }
        }

        // ---- Library --------------------------------------------------------------------------------------------

        private static IAssetBrowser hooked;

        public static void Library(ModPage page)
        {
            var browser = StudioServices.AssetBrowser;
            if (browser == null) { page.Text("No library"); return; }
            if (hooked != browser)
            {
                if (hooked != null) hooked.Changed -= OnLibraryChanged;
                hooked = browser;
                browser.Changed += OnLibraryChanged;
            }
            var cats = new List<(string id, string label)> { ("", "All") };
            try { cats.AddRange((browser.Categories ?? []).Select(c => (c.Id, c.Name))); }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Library categories failed", e); }
            if (cats.Count > 1)
                StudioDock.Tabs(page, cats, () => StudioDock.LibraryCategory ?? "", id => { StudioDock.LibraryCategory = id.Length == 0 ? null : id; StudioDock.OnRecentChanged(); }, 3);
            page.TextInput("Filter", () => StudioDock.LibraryFilter, v => { StudioDock.LibraryFilter = v ?? ""; StudioDock.OnRecentChanged(); });
            IReadOnlyList<AssetBrowserItem> items;
            try { items = browser.Query(StudioDock.LibraryCategory, StudioDock.LibraryFilter) ?? []; }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Library query failed", e); items = []; }
            if (items.Count == 0) { page.Text("Nothing here"); return; }
            const float tile = 84f, gap = 8f;
            float width = page.Size.x;
            int perRow = Mathf.Max(1, (int)((width + gap) / (tile + gap)));
            int rows = (items.Count + perRow - 1) / perRow;
            var area = page.Custom("Library tiles", rows * (tile + gap));
            string armed = StudioShell.Tool == StudioShell.Place ? StudioShell.Place.Armed?.Id : null;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var t = ModTile.Create(area, "Lib " + it.Id, new Vector2(tile, tile), it.Icon, it.Icon == null ? Short(it.Name) : null, 16f);
                t.Key = it.Id;
                t.Place(new Vector2((i % perRow) * (tile + gap), -(i / perRow) * (tile + gap)));
                t.SetBadge(it.Ready ? it.Badge : "…");
                t.SetDim(!it.Ready);
                t.SetOn(armed == "lib:" + it.Id);
                if (it.Icon == null) t.Label.alignment = TextAlignmentOptions.Center;
                ModTooltip.Attach(t, it.Name, () => it.Description ?? "");
                t.Applied += () => ArmLibrary(browser, it);
            }
        }

        private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : name.Length <= 10 ? name : name.Substring(0, 9) + "…";

        private static void OnLibraryChanged()
        {
            if (StudioDock.PaletteTab == "library") StudioDock.OnRecentChanged();
        }

        public static void ArmLibrary(IAssetBrowser browser, AssetBrowserItem it)
        {
            if (!it.Ready) { Notify.Show("Still loading", NotifyKind.Info, 2f, "builder-studio"); return; }
            StudioShell.Arm(LibraryKind(browser, it));
        }

        public static StudioPlaceable LibraryKind(IAssetBrowser browser, AssetBrowserItem it) =>
            new()
            {
                Id = "lib:" + it.Id,
                Name = it.Name,
                Icon = it.Icon,
                MakeGhost = parent => browser.MakeGhost(it, parent),
                Place = (pos, yaw) =>
                {
                    string baseRef = browser.BaseRef(it);
                    if (baseRef == null) return null;
                    float scale = 1f;
                    try { scale = browser.StartScale(it); } catch (Exception e) { Log.For<BuilderManager>().Caught("Library StartScale failed", e); }
                    return StudioEdits.AddProp(baseRef, it.Name, pos, yaw, scale);
                },
            };

        private static void Shrink(ModRow row, bool on, float room = 60f) => StudioDock.Compact(row, on, room);
    }
}
