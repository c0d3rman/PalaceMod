using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Library;
using PalaceMod.Builder.Model;
using TMPro;
using UnityEngine;

/*
    The Enemy Studio's panels (a ModDock of its own, 24 px gutters): the top bar, the palette (Enemies, Base, Look), the
    inspector (Stats, Attack, Tags, Look, Abilities: the pack editor's unit fields, rendered through UnitPage, plus the
    model's fit), the arena controls and the readout. Panels rebuild lazily: structural edits rebuild them, value
    edits refresh the fields in place.
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    internal static class EnemyDock
    {
        private const float G = ModDock.Gutter, TopH = 64f, PaletteW = 300f, RightW = 460f, ArenaW = 400f, BottomH = 250f, TabH = 40f;

        private static ModDock dock;
        private static ModDockPanel top, palette, right, arena, readout;
        private static TextMeshProUGUI title;
        private static ModTile backTile, undoTile, redoTile, turnTile, walkTile, problemsTile, saveTile, fightTile;
        private static bool dirtyTop, dirtyPalette, dirtyRight, dirtyArena, dirtyReadout;
        private static float readoutAt;
        private static int arenaVersion, testVersion;

        public static string PaletteTab = "enemies", PanelTab = "stats", LookSource = "scenery", LookFilter = "";
        public static bool PointerOverPanel => dock?.Over(StudioInput.Mouse) == true;
        public static bool Active => dock?.IsActive == true;
        public static ModDock Dock => dock;

        // ---- Lifecycle ------------------------------------------------------------------------------------------------

        private static void Ensure()
        {
            if (dock != null) return;
            dock = new ModDock("Enemy Studio");
            float below = G + TopH + G;
            top = dock.AddPanel("Top", new Vector2(0, 1), new Vector2(1, 1), new Vector2(G, -G - TopH), new Vector2(-G, -G));
            palette = dock.AddPanel("Palette", new Vector2(0, 0), new Vector2(0, 1), new Vector2(G, G), new Vector2(G + PaletteW, -below));
            right = dock.AddPanel("Inspector", new Vector2(1, 0), new Vector2(1, 1), new Vector2(-G - RightW, G), new Vector2(-G, -below));
            float left = G + PaletteW + G;
            arena = dock.AddPanel("Arena", new Vector2(0, 0), new Vector2(0, 0), new Vector2(left, G), new Vector2(left + ArenaW, G + BottomH));
            readout = dock.AddPanel("Readout", new Vector2(0, 0), new Vector2(1, 0), new Vector2(left + ArenaW + G, G), new Vector2(-G - RightW - G, G + BottomH));
            ModTooltip.Zone = TooltipZone;
        }

        /// <summary>Tooltips never cover the stage: the inspector's and the readout's go over the palette, the
        /// palette's and the arena's over the inspector.</summary>
        private static Rect? TooltipZone(RectTransform near)
        {
            if (dock?.Root == null || near == null) return null;
            ModDockPanel Owner() => new[] { palette, right, arena, readout }.FirstOrDefault(p => p?.Rect != null && near.IsChildOf(p.Rect));
            var owner = Owner();
            var target = owner == right || owner == readout ? palette : owner == palette || owner == arena ? right : null;
            if (target?.Rect == null) return null;
            var c = new Vector3[4];
            target.Rect.GetWorldCorners(c);
            return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
        }

        public static void Build()
        {
            Ensure();
            if (dock.Show()) BuildAll();
        }

        private static void BuildAll()
        {
            if (dock.Root == null) return;
            BuildTop();
            dirtyPalette = dirtyRight = dirtyArena = dirtyReadout = true;
        }

        public static void Destroy()
        {
            ModTooltip.Hide();
            if (ModTooltip.Zone == (Func<RectTransform, Rect?>)TooltipZone) ModTooltip.Zone = null;
            dock?.Destroy();
            dock = null;
            title = null;
            StudioDock.ShowHud(true);
        }

        public static void RefreshAll() => dirtyTop = dirtyPalette = dirtyRight = dirtyArena = dirtyReadout = true;

        public static void OnChanged(bool structural)
        {
            dirtyTop = dirtyReadout = true;
            if (structural) dirtyPalette = dirtyRight = true;
            else
            {
                try { EnemyStudio.Page.RefreshFields(); } catch (Exception e) { Log.For<BuilderManager>().Caught("Enemy Studio: field refresh failed", e); }
            }
        }

        public static void OnModelRebuilt() => dirtyReadout = true;

        /// <summary>Per frame while the Enemy Studio is up.</summary>
        public static void Tick()
        {
            Ensure();
            StudioDock.ShowHud(false);
            bool fresh = dock.Root == null;
            if (!dock.Show()) return;
            if (fresh || title == null) BuildAll();
            if (ModTextInput.Active) return;
            if ((EnemyArena.Running && Time.unscaledTime >= readoutAt) || EnemyArena.Version != arenaVersion) { arenaVersion = EnemyArena.Version; dirtyReadout = dirtyTop = true; }
            if ((Entities.EntityTest.Running && Time.unscaledTime >= readoutAt) || Entities.EntityTest.Version != testVersion) { testVersion = Entities.EntityTest.Version; dirtyReadout = dirtyTop = true; }
            if (dirtyTop) { dirtyTop = false; UpdateTop(); }
            if (dirtyPalette) { dirtyPalette = false; BuildPalette(); }
            if (dirtyRight) { dirtyRight = false; BuildRight(); }
            if (dirtyArena) { dirtyArena = false; BuildArena(); }
            if (dirtyReadout) { dirtyReadout = false; readoutAt = Time.unscaledTime + 0.25f; BuildReadout(); }
        }

        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";
        private static string Gold(string s) => $"<color=#{ModStyle.Hex(ModStyle.Gold)}>{s}</color>";
        private static string Esc(string s) => ModFields.TMPEscape(s ?? "");
        private static Sprite Icon(string name) => ModStyle.AssetSprite($"studio_{name}.png");

        // ---- Top bar --------------------------------------------------------------------------------------------------

        private static ModTile Button(Transform parent, string name, Vector2 size, Sprite icon, string text, Action onClick, string tip, Func<string> body = null)
        {
            var tile = ModTile.Create(parent, name, size, icon, text, 22f);
            tile.Applied += onClick;
            if (tip != null) ModTooltip.Attach(tile, tip, body ?? (() => ""));
            return tile;
        }

        private static void BuildTop()
        {
            top.Clear();
            var body = top.Body;
            float h = body.rect.height > 1f ? body.rect.height : TopH - 2 * ModDockPanel.Padding;
            float x = 0f;
            backTile = Button(body, "Back", new Vector2(150f, h), null, BackLabel(), EnemyStudio.Back, "Back", () =>
                Entities.EntityStudio.ReturnUnit != null ? "Back to the enemy whose attack you came from (Esc)."
                : EnemyStudio.FromLevel ? "Returns to the Level Studio as you left it (Esc)." : "Leaves the arena (Esc). Unsaved changes stay until you leave the Builder.");
            backTile.Place(new Vector2(x, 0f));
            x += backTile.Rect.sizeDelta.x + 14f;
            title = GameWidgets.Text(body, "Title", "", ModStyle.BoldFont, 26f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
            title.enableWordWrapping = false;
            title.overflowMode = TextOverflowModes.Ellipsis;
            var tr = title.rectTransform;
            tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0f, 1f);
            tr.anchoredPosition = new Vector2(x, 0f);
            tr.sizeDelta = new Vector2(560f, h);
            x += 570f;
            undoTile = Button(body, "Undo", new Vector2(h, h), Icon("undo"), null, EnemyStudio.Undo, "Undo", () => EnemyStudio.Draft?.History.CanUndo == true ? EnemyStudio.Draft.History.UndoLabel : "Nothing to undo");
            undoTile.Place(new Vector2(x, 0f));
            x += h + 6f;
            redoTile = Button(body, "Redo", new Vector2(h, h), Icon("redo"), null, EnemyStudio.Redo, "Redo", () => EnemyStudio.Draft?.History.CanRedo == true ? EnemyStudio.Draft.History.RedoLabel : "Nothing to redo");
            redoTile.Place(new Vector2(x, 0f));
            x += h + 18f;
            turnTile = Button(body, "Turn", new Vector2(110f, h), null, "Turn", () => { EnemyStage.Turning = !EnemyStage.Turning; dirtyTop = true; }, "Turntable",
                () => "Spins the model (Space). Off holds the angle; drag on the stage to orbit.");
            turnTile.Place(new Vector2(x, 0f));
            x += 116f;
            walkTile = Button(body, "Walk", new Vector2(110f, h), null, "Walk", () => { EnemyStudio.SetPreviewWalk(!EnemyStudio.PreviewWalk); dirtyTop = true; }, "Walk",
                () => "Shows the walk a model without its own animation gets (W).");
            walkTile.Place(new Vector2(x, 0f));
            x += 116f;
            problemsTile = Button(body, "Problems", new Vector2(90f, h), Icon("warning"), "", null, "Problems", ProblemText);
            problemsTile.Place(new Vector2(x, 0f));
            fightTile = Button(body, "Fight", new Vector2(170f, h), Icon("play"), "Fight", Fight, "Fight",
                () => $"Spawns {EnemyArena.Count} of this enemy against the defenders on the stage (F5). Again: stops it.");
            var fr = fightTile.Rect;
            fr.anchorMin = fr.anchorMax = fr.pivot = new Vector2(1f, 1f);
            fr.anchoredPosition = Vector2.zero;
            fightTile.SetOn(true);
            saveTile = Button(body, "Save", new Vector2(120f, h), null, "Save", EnemyStudio.Save, "Save", () => "Saves and takes the library portrait (Cmd/Ctrl+S).");
            var sr = saveTile.Rect;
            sr.anchorMin = sr.anchorMax = sr.pivot = new Vector2(1f, 1f);
            sr.anchoredPosition = new Vector2(-176f, 0f);
            dock.Refetch();
            UpdateTop();
        }

        private static string BackLabel() => Entities.EntityStudio.ReturnUnit != null ? "← Enemy" : EnemyStudio.FromLevel ? "← Level" : "← Leave";

        public static void Fight()
        {
            if (Entities.EntityStudio.Active) { Entities.EntityPanels.Test(); dirtyReadout = dirtyTop = true; return; }
            if (EnemyArena.Running) { EnemyArena.Clear(); dirtyReadout = dirtyTop = true; return; }
            try
            {
                string what = EnemyArena.Start(EnemyStudio.Draft.Pack, EnemyStudio.UnitId);
                Log.For<BuilderManager>().Info("Enemy Studio: " + what);
            }
            catch (Exception e) { Notify.Show("Can't fight: " + e.Message, NotifyKind.Warning, 4f, "builder-enemy"); }
            dirtyReadout = dirtyTop = true;
        }

        private static List<ValidationIssue> Issues()
        {
            var d = EnemyStudio.Draft;
            int at = Entities.EntityStudio.Active ? Entities.EntityStudio.Index : EnemyStudio.UnitIndex;
            if (d == null || at < 0) return [];
            string prefix = Entities.EntityStudio.Active ? $"entities[{at}]" : $"units[{at}]";
            return d.Issues.Where(i => i.Path != null && (i.Path == prefix || i.Path.StartsWith(prefix + "."))).ToList();
        }

        private static string ProblemText() =>
            string.Join("\n", Issues().Select(i => $"{i.Path.Substring(i.Path.IndexOf('.') + 1)}: {i.Message}").DefaultIfEmpty("No problems"));

        private static void UpdateTop()
        {
            if (title == null) return;
            var d = EnemyStudio.Draft;
            var u = EnemyStudio.Unit;
            string dot = d?.Dirty == true ? $"  <color=#{ModStyle.Hex(ModStyle.Accent)}>●</color> <size=70%>unsaved</size>" : "";
            if (Entities.EntityStudio.Active)
            {
                var e = Entities.EntityStudio.Entity;
                title.text = e == null ? $"New thing {Dim("· " + Esc(d?.Pack.Name ?? d?.Pack.Id ?? ""))}{dot}"
                    : $"{Esc(e.Name ?? e.Id)} {Dim("· " + Entities.ThingKinds.Label(Entities.EntityStudio.Kind) + " · " + Esc(d?.Pack.Name ?? d?.Pack.Id ?? ""))}{dot}";
            }
            else title.text = $"{Esc(u?.Name ?? u?.Id ?? "")} {Dim("· " + Esc(d?.Pack.Name ?? d?.Pack.Id ?? ""))}{dot}";
            backTile?.SetText(BackLabel());
            undoTile?.SetDim(d?.History.CanUndo != true);
            redoTile?.SetDim(d?.History.CanRedo != true);
            turnTile?.SetOn(EnemyStage.Turning);
            walkTile?.SetOn(EnemyStudio.PreviewWalk);
            if (Entities.EntityStudio.Active)
            {
                fightTile?.gameObject.SetActive(Entities.EntityStudio.Entity != null);
                fightTile?.SetText(Entities.EntityTest.Running ? "Stop" : Entities.EntityStudio.IsProjectile ? "Launch" : "Try");
                walkTile?.gameObject.SetActive(false);
            }
            else
            {
                fightTile?.gameObject.SetActive(true);
                walkTile?.gameObject.SetActive(true);
                fightTile?.SetText(EnemyArena.Running ? "Stop" : "Fight");
            }
            var issues = Issues();
            if (problemsTile != null)
            {
                problemsTile.gameObject.SetActive(issues.Count > 0);
                problemsTile.SetText(issues.Count.ToString());
                problemsTile.SetWarning(issues.Any(i => i.Severity == Severity.Error));
            }
        }

        // ---- Shared bits ----------------------------------------------------------------------------------------------

        private static void Tabs(ModPage page, IReadOnlyList<(string id, string label)> tabs, Func<string> current, Action<string> pick, int perRow) =>
            StudioDock.Tabs(page, tabs, current, pick, perRow, 20f);

        /// <summary>A grid of tiles filling the page's width.</summary>
        internal static void Grid<T>(ModPage page, string name, IReadOnlyList<T> items, float size, Func<T, (string key, Sprite icon, string text, string badge, bool on, string tip, string body)> look,
            Action<T> click)
        {
            const float gap = 8f;
            float width = page.Size.x;
            int perRow = Mathf.Max(1, (int)((width + gap) / (size + gap)));
            int rows = (items.Count + perRow - 1) / perRow;
            var area = page.Custom(name, Mathf.Max(1, rows) * (size + gap));
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var l = look(it);
                var t = ModTile.Create(area, name + " " + l.key, new Vector2(size, size), l.icon, l.icon == null ? l.text : null, 15f);
                t.Key = l.key;
                t.Place(new Vector2((i % perRow) * (size + gap), -(i / perRow) * (size + gap)));
                if (l.badge != null) t.SetBadge(l.badge);
                t.SetOn(l.on);
                if (l.icon == null) t.Label.alignment = TextAlignmentOptions.Center;
                ModTooltip.Attach(t, l.tip, () => l.body ?? "");
                t.Applied += () => click(it);
            }
        }

        internal static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : name.Length <= 12 ? name : name.Substring(0, 11) + "…";

        // ---- Palette --------------------------------------------------------------------------------------------------

        private static (string id, string label)[] PaletteTabs => Entities.EntityStudio.Active
            ? [("enemies", "Enemies"), ("things", "Things"), ("look", "Look")]
            : [("enemies", "Enemies"), ("things", "Things"), ("base", "Base"), ("look", "Look")];

        private static void BuildPalette()
        {
            if (palette?.Body == null) return;
            palette.Fill(page =>
            {
                var tabs = PaletteTabs;
                if (tabs.All(t => t.id != PaletteTab)) PaletteTab = tabs[0].id;
                Tabs(page, tabs, () => PaletteTab, id => { PaletteTab = id; dirtyPalette = true; }, tabs.Length);
                try
                {
                    switch (PaletteTab)
                    {
                        case "base": BasePalette(page); break;
                        case "look": LookPalette(page); break;
                        case "things": Entities.EntityPanels.Palette(page); break;
                        default: EnemiesPalette(page); break;
                    }
                }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Enemy Studio palette failed", e); }
            });
        }

        private static Sprite IconOf(UnitDef u)
        {
            var d = EnemyStudio.Draft;
            return (d != null ? EnemyStudio.PortraitSprite(d.Pack.Id, u.Id) : null) ?? BuilderServices.Catalog?.FindUnit(ContentRef.Parse(u.Base).Id)?.Icon;
        }

        private static void EnemiesPalette(ModPage page)
        {
            var list = EnemyStudio.Enemies.ToList();
            var cur = Entities.EntityStudio.Active ? null : EnemyStudio.Unit;
            Grid(page, "Enemy", list.Cast<object>().Append("+").ToList(), 82f, o =>
            {
                if (o is UnitDef u)
                    return (u.Id, IconOf(u), Short(u.Name ?? u.Id), null, u == cur, u.Name ?? u.Id, $"Base: {BuilderServices.Catalog?.FindUnit(ContentRef.Parse(u.Base).Id)?.DisplayName ?? u.Base}\nId: {u.Id}");
                return ("new", null, "+ New", null, false, "New enemy", "Makes a new enemy from the current one's base unit.");
            }, o =>
            {
                if (o is UnitDef u) EnemyStudio.Select(u.Id);
                else EnemyStudio.New(ContentRef.Parse(EnemyStudio.Unit?.Base).Id);
            });
            if (cur == null) return;
            page.Button("Duplicate", EnemyStudio.Duplicate, "Makes a copy of this enemy to vary.");
            DeleteRow(page);
        }

        private static float deleteArmed = -10f;

        private static void DeleteRow(ModPage page)
        {
            ModRow row = null;
            row = page.Row("Delete", null, "Deletes this enemy. Wave sets that use it show a problem until you change them.", () =>
            {
                if (Time.unscaledTime - deleteArmed > 3f) { deleteArmed = Time.unscaledTime; row.SetLabel("Delete: press again"); return; }
                deleteArmed = -10f;
                int i = EnemyStudio.UnitIndex;
                EnemyStudio.Draft.Edit("Delete enemy", p => p.Units.RemoveAt(i), structural: true);
            });
        }

        private static void BasePalette(ModPage page)
        {
            var units = BuilderServices.Catalog?.Units.Where(u => u.Side == UnitSide.Enemy).ToList() ?? [];
            string current = ContentRef.Parse(EnemyStudio.Unit?.Base).Id;
            foreach (var group in units.GroupBy(c => c.Flying ? "Flying" : c.Large ? "Large" : "Ground").OrderBy(g => g.Key == "Ground" ? 0 : g.Key == "Large" ? 1 : 2))
            {
                StudioDock.SmallHeader(page, group.Key);
                Grid(page, "Base", group.ToList(), 72f, c => (c.Id, c.Icon, Short(c.DisplayName ?? c.Id), null, c.Id == current, c.DisplayName ?? c.Id,
                    (string.IsNullOrWhiteSpace(c.Description) ? "" : c.Description + "\n") + Editor.EditorCatalog.Stats(c) + $"\nId: {c.Id}"),
                    c => EnemyStudio.Edit("Change base unit", u => u.Base = ContentRef.Vanilla(c.Id), structural: true));
            }
        }

        private static readonly (string id, string label)[] LookSources = [("scenery", "Scenery"), ("units", "Units"), ("imported", "Import")];

        private sealed class LookItem
        {
            public string Ref, Name, Badge, Body;
            public Sprite Icon;
        }

        private static void LookPalette(ModPage page)
        {
            Tabs(page, LookSources, () => LookSource, id => { LookSource = id; dirtyPalette = true; }, 3);
            page.TextInput("Filter", () => LookFilter, v => { LookFilter = v ?? ""; dirtyPalette = true; });
            string f = LookFilter?.Trim() ?? "";
            bool Match(params string[] s) => f.Length == 0 || s.Any(x => x != null && x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            bool thing = Entities.EntityStudio.Active;
            if (thing && Entities.EntityStudio.Entity == null) { page.Text(Dim("Pick the kind of the new thing first.")); return; }
            var items = new List<LookItem> { thing ? new() { Ref = null, Name = "None", Body = "No model: an invisible thing (its outlines still show on the stage)." } : new() { Ref = null, Name = "Base", Body = "The base unit's own model." } };
            switch (LookSource)
            {
                case "units":
                    foreach (var c in BuilderServices.Catalog?.Units ?? [])
                        if (c.Prefab != null && Match(c.DisplayName, c.Id))
                            items.Add(new LookItem { Ref = ModelRef.Unit(c.Id), Name = c.DisplayName ?? c.Id, Icon = c.Icon, Badge = c.Side == UnitSide.Player ? "ally" : null, Body = $"{c.Id}: its model, with its own colours." });
                    break;
                case "imported":
                    foreach (var kv in (EnemyStudio.Draft?.Pack.Assets ?? []).OrderBy(k => k.Key))
                    {
                        string path = kv.Value?.Path ?? kv.Value?.Url ?? "";
                        if (!Validator.MeshExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || !Match(kv.Key)) continue;
                        items.Add(new LookItem { Ref = ContentRef.Asset(kv.Key), Name = kv.Key, Body = path });
                    }
                    if (items.Count == 1) page.Text(Dim("No OBJ or glTF files in this pack's assets. Add them with the pack's Assets (or the agent kit), then pick them here."));
                    break;
                default:
                    foreach (var e in SceneryLibrary.Available)
                        if (Match(e.Name, e.Category, SceneryCatalog.DisplayMap(e.Home)))
                            items.Add(new LookItem { Ref = e.Ref, Name = e.Name.Replace('_', ' '), Icon = LibraryThumbs.Get(e), Badge = SceneryCatalog.DisplayMap(e.Home)?.Substring(0, Math.Min(3, SceneryCatalog.DisplayMap(e.Home)?.Length ?? 0)), Body = $"{e.Category} · {SceneryCatalog.DisplayMap(e.Home)}" });
                    break;
            }
            string current = thing ? Entities.EntityStudio.Entity?.Visual?.Mesh : EnemyStudio.Unit?.Visuals?.Mesh;
            void Pick(VisualsDef v, string mesh)
            {
                v.Mesh = mesh;
                if (mesh == null) { v.MeshScale = v.MeshRotX = v.MeshRotY = v.MeshRotZ = null; v.MeshOffset = null; v.MeshLook = null; }
            }
            Grid(page, "Look", items, 82f, it => (it.Ref ?? "base", it.Icon, Short(it.Name), it.Badge, it.Ref == current, it.Name, it.Body), it =>
            {
                if (thing) Entities.EntityStudio.EditVisual(it.Ref == null ? "No model" : $"Model: {it.Name}", v => Pick(v, it.Ref), structural: true);
                else EnemyStudio.EditVisuals(it.Ref == null ? "Use the base model" : $"Model: {it.Name}", v => Pick(v, it.Ref), structural: true);
            });
            LibraryThumbs.Rendered -= OnThumbs;
            LibraryThumbs.Rendered += OnThumbs;
        }

        private static void OnThumbs()
        {
            if (PaletteTab == "look" && LookSource == "scenery") dirtyPalette = true;
        }

        // ---- Inspector ------------------------------------------------------------------------------------------------

        private static readonly (string id, string label)[] RightTabs = [("stats", "Stats"), ("attack", "Attack"), ("tags", "Tags"), ("look", "Look"), ("abilities", "Abilities")];

        private static readonly List<(string Id, string Label, string Replaces, Action<ModPage> Build)> extraTabs = [];

        /// <summary>An inspector tab from another workstream (the behaviour editor's Behaviour, Targeting, Movement);
        /// <paramref name="replaces"/> names a built-in tab it takes the place of (null: added at the end).</summary>
        public static void AddTab(string id, string label, string replaces, Action<ModPage> build)
        {
            extraTabs.RemoveAll(t => t.Id == id);
            extraTabs.Add((id, label, replaces, build));
        }

        /// <summary>The inspector's ModDockPanel rect offsets (for panels placed beside it).</summary>
        public static (Vector2 Min, Vector2 Max) BesideInspector(float width) =>
            (new Vector2(-G - RightW - G - width, G + BottomH + G), new Vector2(-G - RightW - G, -(G + TopH + G)));

        public static void RefreshInspector() => dirtyRight = true;

        private static List<(string id, string label)> Tabs()
        {
            var list = new List<(string, string)>();
            foreach (var (id, label) in RightTabs)
            {
                var swap = extraTabs.FirstOrDefault(t => t.Replaces == id);
                list.Add(swap.Id != null ? (swap.Id, swap.Label) : (id, label));
            }
            list.AddRange(extraTabs.Where(t => t.Replaces == null).Select(t => (t.Id, t.Label)));
            return list;
        }

        private static void BuildRight()
        {
            if (right?.Body == null) return;
            right.Fill(page =>
            {
                if (Entities.EntityStudio.Active)
                {
                    page.FieldIndent = 200f;
                    try { Entities.EntityPanels.Inspector(page, (list, current, pick) => Tabs(page, list, current, pick, list.Count)); }
                    catch (Exception e) { Log.For<BuilderManager>().Caught("Entity Studio inspector failed", e); }
                    return;
                }
                var tabs = Tabs();
                if (tabs.All(t => t.id != PanelTab)) PanelTab = "stats";
                Tabs(page, tabs, () => PanelTab, id => { PanelTab = id; dirtyRight = true; }, 3);
                var unitPage = EnemyStudio.Page;
                unitPage.Index = EnemyStudio.UnitIndex;
                page.FieldIndent = 235f;
                var extra = extraTabs.FirstOrDefault(t => t.Id == PanelTab);
                try
                {
                    if (extra.Build != null) { extra.Build(page); return; }
                    switch (PanelTab)
                    {
                        case "attack": EnemyPanels.Attack(page); break;
                        case "tags": EnemyPanels.Tags(page); break;
                        case "look": EnemyPanels.Colours(page); EnemyPanels.Size(page); ModelFields(page); break;
                        case "abilities": unitPage.BuildSection(page, "behaviours"); break;
                        default: unitPage.BuildSection(page, "identity", "stats"); break;
                    }
                }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Enemy Studio inspector failed", e); }
            });
        }

        private static string Schema(string tag) => Editor.Schema.Words(tag);

        /// <summary>The model's fit: scale, offset, turn, materials, motion (only with a model set).</summary>
        private static void ModelFields(ModPage page)
        {
            var v = EnemyStudio.Unit?.Visuals;
            StudioDock.SmallHeader(page, "Model");
            string mesh = v?.Mesh;
            page.Row("Source", mesh == null ? Dim("base model") : Esc(mesh), "The body: the base model, a scenery mesh, another unit's model or an imported file. Pick one in the palette's Look tab.",
                () => { PaletteTab = "look"; dirtyPalette = true; });
            if (mesh == null) { page.Text(Dim("Pick a model in the palette's Look tab to scale, move and turn it.")); return; }
            page.Number("Scale", () => EnemyStudio.Unit?.Visuals?.MeshScale ?? 0f, x => EnemyStudio.EditVisuals("Set model scale", vis => vis.MeshScale = Mathf.Max(0.001f, x), coalesce: "meshScale"),
                0f, 100f, 0.05f, x => EnemyStudio.Unit?.Visuals?.MeshScale == null ? Dim("fit") : x.ToString("0.###") + "×",
                () => (Descriptions.For(DescKind.Stat, "meshScale") ?? "The model's own scale.") + "\nEmpty: fit it to the base unit's height.",
                () => EnemyStudio.EditVisuals("Fit the model", vis => vis.MeshScale = null));
            string[] axes = ["X", "Y", "Z"];
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                page.Number("Move " + axes[k], () => EnemyStudio.Unit?.Visuals?.MeshOffset is { Length: 3 } o ? o[k] : 0f,
                    x => EnemyStudio.EditVisuals("Move the model", vis =>
                    {
                        var o = vis.MeshOffset is { Length: 3 } cur ? (float[])cur.Clone() : new float[3];
                        o[k] = Mathf.Clamp(x, -50f, 50f);
                        vis.MeshOffset = o.All(c => Mathf.Abs(c) < 1e-4f) ? null : o;
                    }, coalesce: "meshOffset" + k), -50f, 50f, 0.05f, x => x.ToString("0.##") + " m",
                    () => k switch { 0 => "Right (+) or left, in metres.", 1 => "Up (+) or down, in metres.", _ => "Forward (+) or back, in metres." });
            }
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                Func<VisualsDef, float?> get = k switch { 0 => vis => vis?.MeshRotX, 1 => vis => vis?.MeshRotY, _ => vis => vis?.MeshRotZ };
                page.Number("Turn " + axes[k], () => get(EnemyStudio.Unit?.Visuals) ?? 0f,
                    x => EnemyStudio.EditVisuals("Turn the model", vis =>
                    {
                        float? val = Mathf.Abs(x) < 1e-3f ? null : Mathf.Clamp(x, -360f, 360f);
                        if (k == 0) vis.MeshRotX = val; else if (k == 1) vis.MeshRotY = val; else vis.MeshRotZ = val;
                    }, coalesce: "meshRot" + k), -360f, 360f, 5f, x => x.ToString("0.#") + "°",
                    () => k switch { 0 => "Tilts it forward or back.", 1 => "Turns it to face forward (the way it walks).", _ => "Tilts it sideways." });
            }
            var looks = new[] { (ModelRef.LookOwn, "Own"), (ModelRef.LookUnit, "Enemy") };
            page.Choice("Materials", looks.Select(l => l.Item2).ToList(), () => Array.FindIndex(looks, l => l.Item1 == ModelRef.Look(EnemyStudio.Unit?.Visuals)),
                i => EnemyStudio.EditVisuals("Set model materials", vis => vis.MeshLook = looks[i].Item1, coalesce: "meshLook"),
                tooltip: () => "Its own colours: as the model is drawn on the map. Enemy material: the enemy colour, which takes the tint and the elite look.");
            var motions = new[] { (ModelRef.MotionAuto, "Auto"), (ModelRef.MotionBob, "Hop"), (ModelRef.MotionNone, "None") };
            page.Choice("Walk", motions.Select(m => m.Item2).ToList(), () => Math.Max(0, Array.FindIndex(motions, m => m.Item1 == (EnemyStudio.Unit?.Visuals?.Motion ?? ModelRef.MotionAuto))),
                i => EnemyStudio.EditVisuals("Set the walk", vis => vis.Motion = i == 0 ? null : motions[i].Item1, coalesce: "motion"),
                tooltip: () => "How a model without its own animation moves: it hops and waddles while it walks (Auto does this for every model that replaces the body). Turn on Walk in the top bar to see it.");
        }

        // ---- Arena ----------------------------------------------------------------------------------------------------

        private static void BuildArena()
        {
            if (arena?.Body == null) return;
            arena.Fill(page =>
            {
                page.FieldIndent = 170f;
                if (Entities.EntityStudio.Active) { Entities.EntityPanels.Arena(page); return; }
                StudioDock.SmallHeader(page, "Arena");
                page.Number("Enemies", () => EnemyArena.Count, x => { EnemyArena.Count = x; dirtyReadout = true; }, 1, 50, 1, tooltip: () => "How many copies fight.");
                page.Toggle("Elite", () => EnemyArena.Elite, x => { EnemyArena.Elite = x; dirtyReadout = true; }, () => Descriptions.For(DescKind.Flag, "Elite") ?? "Elite enemies: ×4 hit points, ×3 damage, the elite look.");
                page.Number("Difficulty", () => EnemyArena.Difficulty, x => { EnemyArena.Difficulty = x; dirtyReadout = true; }, 0.1f, 20f, 0.05f, x => x.ToString("0.##") + "×",
                    () => "A night's difficulty: multiplies hit points, and damage halfway (as the game does).");
                foreach (var d in EnemyArena.Defenders.ToList())
                {
                    var def = d;
                    page.Number(def.Name, () => def.Count, x => { def.Count = x; if (x == 0) { EnemyArena.Defenders.Remove(def); dirtyArena = true; } dirtyReadout = true; }, 0, 20, 1,
                        tooltip: () => "Defenders of this kind (0 removes them).");
                }
                page.Button("Add defenders", () => Editor.Picker.One("Defenders",
                    (BuilderServices.Catalog?.Units ?? []).Where(u => u.Side == UnitSide.Player && u.Prefab != null && EnemyArena.Defenders.All(x => x.Id != u.Id))
                        .Select(u => new Editor.Picker.Item { Id = u.Id, Label = u.DisplayName ?? u.Id, Icon = u.Icon, Value = $"HP {Editor.EditorCatalog.Num(u.MaxHp)}" }),
                    null, id =>
                    {
                        var u = BuilderServices.Catalog.FindUnit(id);
                        EnemyArena.Defenders.Add(new EnemyArena.Defender { Id = id, Name = u?.DisplayName ?? id, Count = 3 });
                        dirtyArena = dirtyReadout = true;
                    }), "Troops that hold the stage against the enemies.");
            });
        }

        // ---- Readout --------------------------------------------------------------------------------------------------

        private sealed class Tower
        {
            public string Name;
            public EffectiveUnit Attacker;
        }

        private static int towerScene = -1;
        private static List<Tower> towers = [];

        /// <summary>The map's tower kinds (every AutoAttackTower in the scene, built or not), weakest first.</summary>
        private static List<Tower> Towers()
        {
            var scene = StudioScene.Scene;
            if (!scene.IsValid()) return [];
            if (scene.handle == towerScene) return towers;
            towerScene = scene.handle;
            var seen = new HashSet<string>();
            var list = new List<Tower>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<AutoAttackTower>(true))
            {
                if (t == null || t.weapon == null || t.gameObject.scene != scene) continue;
                if (EnemyStage.Model != null && t.transform.IsChildOf(EnemyStage.Model.transform)) continue;
                string building = t.GetComponentInParent<BuildSlot>(true)?.buildingName;
                string name = string.IsNullOrWhiteSpace(building) ? t.gameObject.name : building;
                var e = new EffectiveUnit
                {
                    Name = name,
                    AttackCooldown = t.cooldownDuration,
                    DamageMult = t.DamageMultiplyer,
                    Shots = Math.Max(1, t.simultaneousProjectiles),
                    Damage = t.weapon.directDamage.Select(m => new DamageModDef { Tags = m.requiredTags.Select(x => x.ToString()).ToList(), Add = m.damageAdded, Mult = m.damageMultiplyer }).ToList(),
                };
                string key = $"{name}|{t.weapon.name}|{e.AttackCooldown:0.###}|{e.DamageMult:0.###}";
                if (!seen.Add(key)) continue;
                list.Add(new Tower { Name = name, Attacker = e });
            }
            // Number the levels of one building by strength.
            var probe = new HashSet<string>(StringComparer.Ordinal);
            towers = list.GroupBy(t => t.Name).SelectMany(g =>
            {
                var ordered = g.OrderBy(t => EnemyMath.PerSecond(t.Attacker, probe)).ToList();
                if (ordered.Count > 1) for (int i = 0; i < ordered.Count; i++) ordered[i].Name = $"{g.Key} {i + 1}";
                return ordered;
            }).ToList();
            return towers;
        }

        private static string Num(float v) => v >= 100 ? v.ToString("0") : v >= 10 ? v.ToString("0.#") : v.ToString("0.##");

        private static void BuildReadout()
        {
            if (readout?.Body == null) return;
            readout.Fill(page =>
            {
                if (Entities.EntityStudio.Active) { Entities.EntityPanels.Readout(page); return; }
                var u = EnemyStudio.Unit;
                if (u == null) return;
                var e = EnemyStudio.Effective();
                float hpMult = EnemyArena.Difficulty * (EnemyArena.Elite ? 4f : 1f), dmgMult = Mathf.Lerp(1f, EnemyArena.Difficulty, 0.5f) * (EnemyArena.Elite ? 3f : 1f);
                float hp = e.MaxHp * hpMult;
                var me = new EffectiveUnit { AttackCooldown = e.AttackCooldown, DamageMult = e.DamageMult * dmgMult, Damage = e.Damage };
                if (EnemyArena.Running || EnemyArena.HasResult)
                {
                    StudioDock.SmallHeader(page, EnemyArena.Running ? $"Fighting · {EnemyArena.Elapsed:0.0} s" : EnemyArena.Result);
                    page.Text($"Enemies {Gold(EnemyArena.EnemiesAlive + "/" + EnemyArena.EnemiesTotal)}   Defenders {Gold(EnemyArena.DefendersStanding + "/" + EnemyArena.DefendersTotal)}\n" +
                        $"Damage to defenders {Gold(Num(EnemyArena.DamageToDefenders))}   to enemies {Gold(Num(EnemyArena.DamageToEnemies))}\n" +
                        $"First kill {(EnemyArena.FirstKill is float f ? f.ToString("0.0") + " s" : "-")}   Last {(EnemyArena.LastKill is float l ? l.ToString("0.0") + " s" : "-")}" +
                        (EnemyArena.Elapsed > 0.5f ? $"   Enemy DPS {Num(EnemyArena.DamageToDefenders / EnemyArena.Elapsed)}" : ""));
                }
                StudioDock.SmallHeader(page, $"{Esc(u.Name ?? u.Id)}");
                string flags = string.Join(" · ", new[] { e.Flying ? "flying" : null, e.Large ? "large" : null, EnemyArena.Elite ? "elite" : null }.Where(s => s != null));
                page.Text($"HP {Gold(Num(hp))}{(hpMult != 1f ? Dim($" ({Num(e.MaxHp)} ×{hpMult:0.##})") : "")}   Speed {Gold(Num(e.MoveSpeed))}   Range {Gold(Num(e.AttackRange))}   " +
                    $"Every {Gold(Num(e.AttackCooldown) + " s")}{(flags.Length > 0 ? "   " + Dim(flags) : "")}" + (EnemyStudio.ModelError != null ? $"\n<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>{Esc(EnemyStudio.ModelError)}</color>" : ""));
                StudioDock.SmallHeader(page, "It deals");
                var lines = new List<string>();
                foreach (var t in EnemyMath.Targets)
                    lines.Add($"{t.Name}: {Gold(Num(EnemyMath.Damage(me.Damage, t.Tags, me.DamageMult)))} a hit · {Num(EnemyMath.PerSecond(me, t.Tags))}/s");
                foreach (var d in EnemyArena.Defenders)
                {
                    var c = BuilderServices.Catalog?.FindUnit(d.Id);
                    if (c == null) continue;
                    var tags = new HashSet<string>(c.Tags);
                    var ttk = EnemyMath.TimeToKill(c.MaxHp, EnemyMath.Damage(me.Damage, tags, me.DamageMult), me.AttackCooldown);
                    lines.Add($"{Esc(d.Name)}: {Gold(Num(EnemyMath.Damage(me.Damage, tags, me.DamageMult)))} a hit · kills one in {EnemyMath.Describe(ttk)}");
                }
                page.Text(string.Join("\n", lines));
                StudioDock.SmallHeader(page, "Killed by");
                var killers = new List<string>();
                foreach (var t in Towers())
                {
                    float perHit = EnemyMath.Damage(t.Attacker.Damage, e.Tags, t.Attacker.DamageMult);
                    killers.Add($"{Esc(t.Name)}: {EnemyMath.Describe(EnemyMath.TimeToKill(hp, perHit, t.Attacker.AttackCooldown))}{(t.Attacker.Shots > 1 ? Dim($" ({t.Attacker.Shots} shots)") : "")}");
                }
                foreach (var d in EnemyArena.Defenders)
                {
                    var c = BuilderServices.Catalog?.FindUnit(d.Id);
                    var w = c?.Weapon != null ? BuilderServices.Catalog.FindWeapon(c.Weapon) : null;
                    if (c == null || w == null) continue;
                    var ttk = EnemyMath.TimeToKill(hp, EnemyMath.Damage(w.Damage, e.Tags), c.AttackCooldown);
                    killers.Add($"One {Esc(d.Name)}: {EnemyMath.Describe(ttk)}");
                }
                page.Text(killers.Count == 0 ? Dim("No towers on this map") : string.Join("\n", killers));
            });
        }

        // ---- Dev ------------------------------------------------------------------------------------------------------

        public static string Press(string name)
        {
            if (dock?.Root == null) throw new InvalidOperationException("the dock isn't up");
            foreach (var tile in dock.Root.GetComponentsInChildren<ModTile>())
                if (string.Equals(tile.name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(tile.Key, name, StringComparison.OrdinalIgnoreCase) || tile.name.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase))
                { tile.Press(); return "pressed " + tile.name; }
            foreach (var b in dock.Root.GetComponentsInChildren<TFUITextButton>())
                if (b.name.Equals("Row " + name, StringComparison.OrdinalIgnoreCase)) { b.onApply?.Invoke(); return "pressed " + b.name; }
            throw new ArgumentException($"no tile or row \"{name}\"");
        }

        public static string Describe()
        {
            if (dock?.Root == null) return "no dock";
            return string.Join("; ", dock.Panels.Where(p => p.Rect != null && p.Visible).Select(p =>
            {
                var c = new Vector3[4];
                p.Rect.GetWorldCorners(c);
                return $"{p.Name} [{c[0].x:0},{c[0].y:0} .. {c[2].x:0},{c[2].y:0}]";
            })) + $"; screen {Screen.width}x{Screen.height}";
        }
    }
}
