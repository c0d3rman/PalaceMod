using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    The Studio's docked UI (a ModDock): top bar, tool column, palette, right panel (Inspector, Outliner, Level, and
    panels other workstreams add) and the status line, with 24 px gutters at the screen edges and between panels.
    Panels are rebuilt lazily (once per frame at most, and never while a text field is being typed into).
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioDock
    {
        private const float G = ModDock.Gutter, TopH = 64f, StatusH = 40f, ToolW = 76f, PaletteW = 300f, RightW = 340f, TabH = 40f;

        private static ModDock dock;
        private static ModDockPanel top, tools, palette, right, status;
        private static readonly Dictionary<string, ModTile> toolTiles = [];
        private static TextMeshProUGUI statusLeft, statusMid, statusRight, title;
        private static ModTile undoTile, redoTile, snapTile, viewTile, problemsTile, playTile, menuTile;
        private static RectTransform marquee, anchor;
        private static bool dirtyInspector, dirtyLevel, dirtyTools, dirtyPalette, dirtyTop;

        public static string PaletteTab = "slots", PanelTab = "inspector";
        public static bool PaletteCollapsed;
        public static float PaletteScroll => palette?.Scroll?.ScrollY ?? 0f;
        public static float PanelScroll => right?.Scroll?.ScrollY ?? 0f;
        public static bool Active => dock?.IsActive == true;
        public static bool PointerOverPanel => dock?.Over(StudioInput.Mouse) == true;
        public static ModDock Dock => dock;

        // ---- Build ------------------------------------------------------------------------------------------------

        private static void Ensure()
        {
            if (dock != null) return;
            dock = new ModDock("Studio");
            float below = G + TopH + G, above = G + StatusH + G;
            top = dock.AddPanel("Top", new Vector2(0, 1), new Vector2(1, 1), new Vector2(G, -G - TopH), new Vector2(-G, -G));
            status = dock.AddPanel("Status", new Vector2(0, 0), new Vector2(1, 0), new Vector2(G, G), new Vector2(-G, G + StatusH));
            tools = dock.AddPanel("Tools", new Vector2(0, 0), new Vector2(0, 1), new Vector2(G, above), new Vector2(G + ToolW, -below));
            palette = dock.AddPanel("Palette", new Vector2(0, 0), new Vector2(0, 1), new Vector2(G + ToolW + G, above), new Vector2(G + ToolW + G + PaletteW, -below));
            right = dock.AddPanel("Right", new Vector2(1, 0), new Vector2(1, 1), new Vector2(-G - RightW, above), new Vector2(-G, -below));
        }

        /// <summary>Shows the dock (each frame in edit mode).</summary>
        public static void Show()
        {
            Ensure();
            bool fresh = dock.Root == null;
            ShowHud(false);
            if (!dock.Show()) return;
            if (fresh || toolTiles.Count == 0) BuildAll();
        }

        public static void Build()
        {
            Ensure();
            if (dock.Show()) BuildAll();
        }

        private static void BuildAll()
        {
            if (dock.Root == null) return;
            BuildOverlay();
            BuildTop();
            BuildStatus();
            BuildTools();
            dirtyPalette = dirtyInspector = dirtyLevel = true;
        }

        public static void Hide()
        {
            ModTooltip.Hide();
            dock?.Hide();
            ShowHud(true);
        }

        // The game's HUD (gold, the night's enemy markers) would sit under the panels; it's faded out while editing.
        private static CanvasGroup hud;

        public static void ShowHud(bool show)
        {
            var container = UIFrameManager.instance?.InGameUIContainer;
            if (container == null) return;
            if (hud == null || hud.gameObject != container.gameObject)
            {
                if (show) return;
                hud = container.GetComponent<CanvasGroup>() ?? container.gameObject.AddComponent<CanvasGroup>();
            }
            hud.alpha = show ? 1f : 0f;
            hud.blocksRaycasts = show;
        }

        public static void Destroy()
        {
            ShowHud(true);
            dock?.Destroy();
            toolTiles.Clear();
            marquee = anchor = null;
            statusLeft = statusMid = statusRight = title = null;
        }

        // ---- Change notifications (rebuilt in Tick) -------------------------------------------------------------

        public static void OnSelectionChanged() { dirtyInspector = true; dirtyTop = true; }
        public static void OnLevelChanged() { dirtyInspector = dirtyLevel = dirtyTop = dirtyPalette = true; }
        public static void OnToolChanged() { dirtyTools = true; if (StudioShell.Tool?.HasPanel == true || lastToolPanel) dirtyPalette = true; }
        public static void OnRecentChanged() => dirtyPalette = true;
        public static void RefreshAll() { dirtyInspector = dirtyLevel = dirtyTools = dirtyPalette = dirtyTop = dirtyPlay = true; }

        // ---- Play mode: the panels other workstreams add for play (W2's sandbox) --------------------------------

        private static ModDock playDock;
        private static ModDockPanel playPanel;
        private static bool dirtyPlay;
        public static string PlayTab;

        public static bool PointerOverPlayPanel => playDock?.Over(StudioInput.Mouse) == true;

        /// <summary>Per frame in play mode: shows the play panels without taking the game's input, except while the
        /// pointer is on them (then their rows and tiles take the mouse).</summary>
        public static void PlayTick()
        {
            var panels = Studio.Panels.Where(p => p.Mode == StudioMode.Play && SafeVisible(p)).ToList();
            if (panels.Count == 0 || SceneTransitionManager.instance == null || SceneTransitionManager.instance.SceneTransitionIsRunning) { HidePlay(); return; }
            if (playDock == null)
            {
                playDock = new ModDock("Studio play") { FreezePlayer = false };
                playPanel = playDock.AddPanel("Play", new Vector2(1, 0), new Vector2(1, 1), new Vector2(-G - RightW, G + 240f), new Vector2(-G, -G));
                dirtyPlay = true;
            }
            bool fresh = playDock.Root == null;
            if (!playDock.ShowPassive()) return;
            if (fresh) dirtyPlay = true;
            bool over = playPanel.Rect != null && RectTransformUtility.RectangleContainsScreenPoint(playPanel.Rect, StudioInput.Mouse, null);
            bool held = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            if (over && !playDock.IsActive && !held) playDock.Activate();
            else if (!over && playDock.IsActive && !held && !ModTextInput.Active && (ModPopup.Current == null || !ModPopup.Current.IsOpen)) playDock.Release();
            if (dirtyPlay && !ModTextInput.Active && !held)
            {
                dirtyPlay = false;
                if (panels.All(p => p.Id != PlayTab)) PlayTab = panels[0].Id;
                playPanel.Fill(page =>
                {
                    if (panels.Count > 1) Tabs(page, panels.Select(p => (p.Id, p.Id)).ToList(), () => PlayTab, id => { PlayTab = id; dirtyPlay = true; }, 3);
                    var panel = panels.First(p => p.Id == PlayTab);
                    try { panel.Build(page); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio panel {panel.Id} failed", e); }
                });
            }
        }

        public static void HidePlay()
        {
            if (playDock?.Root == null) return;
            playDock.Release();
            playDock.Hide();
        }

        private static bool lastToolPanel;

        public static void Tick(StudioPointer p)
        {
            if (dock?.Root == null || !dock.Root.gameObject.activeInHierarchy) return;
            UpdateStatus(p);
            if (ModTextInput.Active) return;
            if (dirtyTop) { dirtyTop = false; UpdateTop(); }
            if (dirtyTools) { dirtyTools = false; UpdateTools(); }
            if (dirtyPalette) { dirtyPalette = false; BuildPalette(); }
            if (dirtyInspector || dirtyLevel)
            {
                bool rebuild = PanelTab switch { "inspector" => dirtyInspector || dirtyLevel, "outliner" => dirtyLevel || dirtyInspector, _ => dirtyLevel };
                dirtyInspector = dirtyLevel = false;
                if (rebuild) BuildRight();
            }
        }

        // ---- Overlay (marquee, tooltip anchor) ------------------------------------------------------------------

        private static void BuildOverlay()
        {
            if (marquee != null) return;
            var m = new GameObject("Marquee", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            m.transform.SetParent(dock.Root, false);
            var img = m.GetComponent<Image>();
            img.color = new Color(ModStyle.Bright.r, ModStyle.Bright.g, ModStyle.Bright.b, 0.12f);
            img.raycastTarget = false;
            var outline = m.AddComponent<Outline>();
            outline.effectColor = new Color(ModStyle.Bright.r, ModStyle.Bright.g, ModStyle.Bright.b, 0.9f);
            outline.effectDistance = new Vector2(1.5f, 1.5f);
            marquee = (RectTransform)m.transform;
            marquee.anchorMin = marquee.anchorMax = marquee.pivot = Vector2.zero;
            m.SetActive(false);
            var a = new GameObject("Pointer anchor", typeof(RectTransform));
            a.transform.SetParent(dock.Root, false);
            anchor = (RectTransform)a.transform;
            anchor.anchorMin = anchor.anchorMax = anchor.pivot = Vector2.zero;
            anchor.sizeDelta = new Vector2(24f, 24f);
        }

        private static Vector2 Local(Vector2 screen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(dock.Root, screen, null, out var local);
            return local - dock.Root.rect.min;
        }

        public static void SetMarquee(Rect? screenRect)
        {
            if (marquee == null) return;
            if (screenRect is not Rect r) { marquee.gameObject.SetActive(false); return; }
            var a = Local(r.min);
            var b = Local(r.max);
            marquee.anchoredPosition = a;
            marquee.sizeDelta = b - a;
            marquee.gameObject.SetActive(true);
        }

        /// <summary>The screen point just under the ≡ tile (where the Studio menu opens).</summary>
        public static Vector2? BelowMenu()
        {
            if (menuTile == null) return null;
            var c = new Vector3[4];
            menuTile.Rect.GetWorldCorners(c);
            return new Vector2(c[0].x, c[0].y - 12f);
        }

        /// <summary>A small rect at a screen point, for a tooltip beside the pointer.</summary>
        public static RectTransform PointAnchor(Vector2 screen)
        {
            if (anchor == null) return null;
            anchor.anchoredPosition = Local(screen) - new Vector2(12f, 12f);
            return anchor;
        }

        // ---- Top bar --------------------------------------------------------------------------------------------

        private static ModTile Button(Transform parent, string name, Vector2 size, Sprite icon, string text, Action onClick, string tip, Func<string> tipBody = null)
        {
            var tile = ModTile.Create(parent, name, size, icon, text, 22f);
            tile.Applied += onClick;
            if (tip != null) ModTooltip.Attach(tile, tip, tipBody ?? (() => ""));
            return tile;
        }

        private static Sprite Icon(string name) => ModStyle.AssetSprite($"studio_{name}.png");

        private static void BuildTop()
        {
            top.Clear();
            var body = top.Body;
            float h = body.rect.height > 1f ? body.rect.height : TopH - 2 * ModDockPanel.Padding;
            float x = 0f;
            menuTile = Button(body, "Menu", new Vector2(h, h), Icon("menu"), null, () => StudioMenus.OpenStudioMenu(), "Studio menu");
            menuTile.Place(new Vector2(x, 0f));
            x += h + 14f;
            title = GameWidgets.Text(body, "Title", "", ModStyle.BoldFont, 26f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
            title.enableWordWrapping = false;
            title.overflowMode = TextOverflowModes.Ellipsis;
            var tr = title.rectTransform;
            tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0f, 1f);
            tr.anchoredPosition = new Vector2(x, 0f);
            tr.sizeDelta = new Vector2(620f, h);
            x += 630f;
            undoTile = Button(body, "Undo", new Vector2(h, h), Icon("undo"), null, BuilderEditor.Undo, "Undo", () => Studio.Draft?.History.CanUndo == true ? Studio.Draft.History.UndoLabel : "Nothing to undo");
            undoTile.Place(new Vector2(x, 0f));
            x += h + 6f;
            redoTile = Button(body, "Redo", new Vector2(h, h), Icon("redo"), null, BuilderEditor.Redo, "Redo", () => Studio.Draft?.History.CanRedo == true ? Studio.Draft.History.RedoLabel : "Nothing to redo");
            redoTile.Place(new Vector2(x, 0f));
            x += h + 18f;
            snapTile = Button(body, "Snap", new Vector2(150f, h), null, "", () => StudioMenus.OpenSnap(snapTile.Rect), "Snap", () => "Grid step and turn step. Hold Alt to place freely.");
            snapTile.Place(new Vector2(x, 0f));
            x += 156f;
            viewTile = Button(body, "View", new Vector2(110f, h), null, "View", () => StudioMenus.OpenView(viewTile.Rect), "View", () => "Labels, link arrows, x-ray and routes.");
            viewTile.Place(new Vector2(x, 0f));
            x += 116f;
            problemsTile = Button(body, "Problems", new Vector2(90f, h), Icon("warning"), "", () => StudioMenus.OpenProblems(problemsTile.Rect), "Problems", () => "What's wrong with this level.");
            problemsTile.Place(new Vector2(x, 0f));
            playTile = Button(body, "Play", new Vector2(190f, h), Icon("play"), "Play", () => StudioShell.Play(true), "Play", () => $"{StudioKeys.Label(StudioKeys.Play)} from the cursor · {StudioKeys.Label(StudioKeys.PlayStart)} from the start");
            var pr = playTile.Rect;
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(1f, 1f);
            pr.anchoredPosition = Vector2.zero;
            playTile.SetOn(true);
            dock.Refetch();
            UpdateTop();
        }

        private static void UpdateTop()
        {
            if (title == null) return;
            var d = Studio.Draft;
            var lv = Studio.Level;
            string dot = d?.Dirty == true ? $"  <color=#{ModStyle.Hex(ModStyle.Accent)}>●</color> <size=70%>unsaved</size>" : "";
            title.text = $"{ModFields.TMPEscape(lv?.Name ?? Studio.LevelId ?? "")} {(d?.IsLibrary == true ? "" : $"<color=#{ModStyle.Hex(ModStyle.Dim)}>· {ModFields.TMPEscape(d?.Pack.Name ?? "")}</color>")}{dot}";
            undoTile?.SetDim(d?.History.CanUndo != true);
            redoTile?.SetDim(d?.History.CanRedo != true);
            snapTile?.SetText(StudioShell.SnapStep <= 0f ? "Snap off" : $"Snap {StudioShell.SnapStep:0.#} m");
            int problems = LevelIssues().Count;
            if (problemsTile != null)
            {
                problemsTile.gameObject.SetActive(problems > 0);
                problemsTile.SetText(problems.ToString());
                problemsTile.SetWarning(LevelIssues().Any(i => i.Severity == Model.Severity.Error));
            }
        }

        /// <summary>The draft's issues about this level, plus the path check's result.</summary>
        public static List<Model.ValidationIssue> LevelIssues()
        {
            var d = Studio.Draft;
            if (d == null) return [];
            int index = d.Pack.Levels.FindIndex(l => l?.Id == Studio.LevelId);
            string prefix = $"levels[{index}]";
            var list = d.Issues.Where(i => i.Path != null && (i.Path == prefix || i.Path.StartsWith(prefix + "."))).ToList();
            var blocked = StudioScene.Routes.Where(r => !r.Reachable).Select(r => r.Line).ToList();
            if (blocked.Count > 0) list.Add(new Model.ValidationIssue(Model.Severity.Warning, prefix, "Blocked: " + string.Join(", ", blocked)));
            if (StudioServices.Terrain?.PathsStale == true) list.Add(new Model.ValidationIssue(Model.Severity.Warning, prefix, "Paths out of date"));
            list.AddRange(Units.StudioUnits.Issues(prefix));
            return list;
        }

        // ---- Status line ----------------------------------------------------------------------------------------

        private static void BuildStatus()
        {
            status.Clear();
            // The line is shorter than a panel's padding allows: a text taller than its box shows nothing at all.
            status.Body.offsetMin = new Vector2(16f, 2f);
            status.Body.offsetMax = new Vector2(-16f, -2f);
            var body = status.Body;
            TextMeshProUGUI Make(string name, TextAlignmentOptions align, float minX, float maxX)
            {
                var t = GameWidgets.Text(body, name, "", ModStyle.BodyFont, 22f, ModStyle.Gold, align);
                t.enableWordWrapping = false;
                t.overflowMode = TextOverflowModes.Ellipsis;
                t.characterSpacing = 0f;
                var r = t.rectTransform;
                r.anchorMin = new Vector2(minX, 0f);
                r.anchorMax = new Vector2(maxX, 1f);
                r.offsetMin = r.offsetMax = Vector2.zero;
                return t;
            }
            statusLeft = Make("Hover", TextAlignmentOptions.MidlineLeft, 0f, 0.42f);
            statusMid = Make("Selection", TextAlignmentOptions.Midline, 0.42f, 0.58f);
            statusRight = Make("Hint", TextAlignmentOptions.MidlineRight, 0.58f, 1f);
        }

        private static void UpdateStatus(StudioPointer p)
        {
            if (statusLeft == null) return;
            var hover = StudioScene.Find(p.HoverKey);
            string at = p.Ground is Vector3 g ? $"x {g.x:0} z {g.z:0}" : "";
            statusLeft.text = hover != null ? $"{ModFields.TMPEscape(hover.Name)} · {StudioInspector.Summary(hover)}   <color=#{ModStyle.Hex(ModStyle.Dim)}>{at}</color>" : $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{at}</color>";
            int n = StudioSelection.Count;
            statusMid.text = n == 0 ? "" : $"{n} selected";
            string hint = StudioShell.PickPrompt ?? StudioShell.Tool?.Hint ?? "";
            if (StudioShell.Tool == StudioShell.Move && StudioShell.Move.AxisLabel is string ax) hint = $"Locked to {ax} · " + hint;
            if (StudioShell.Tool == StudioShell.Rotate && StudioShell.Rotate.AxisLabel is string rx) hint = $"{rx} scenery: drag sideways · , . step · Alt free · {(StudioShell.Rotate.Axis == 1 ? "X" : "Z")} or Esc: back to turn";
            if (StudioShell.Tool == StudioShell.Place && StudioShell.Place.Armed is { } armed) hint = $"{ModFields.TMPEscape(armed.Name)}: " + hint;
            statusRight.text = hint;
        }

        // ---- Tool column ----------------------------------------------------------------------------------------

        private static void BuildTools()
        {
            tools.Clear();
            toolTiles.Clear();
            float size = ToolW - 2 * ModDockPanel.Padding;
            float y = 0f;
            foreach (var t in StudioShell.AllTools)
            {
                var tool = t;
                var tile = ModTile.Create(tools.Body, "Tool " + tool.Id, new Vector2(size, size), tool.Icon, tool.Icon == null ? tool.Name.Substring(0, 1) : null, 22f);
                tile.Key = tool.Id;
                tile.Place(new Vector2(0f, -y));
                tile.Applied += () =>
                {
                    if (tool == StudioShell.Place && StudioShell.Place.Armed == null) { PaletteCollapsed = false; dirtyPalette = true; palette.SetVisible(true); return; }
                    StudioShell.SetTool(tool.Id);
                };
                string key = StudioKeys.Label(tool.Key);
                tile.SetBadge(key.Length > 1 ? $"<size=65%>{key.Replace("Shift+", "⇧")}</size>" : key);
                ModTooltip.Attach(tile, tool.Name, () => tool.Hint);
                toolTiles[tool.Id] = tile;
                y += size + 8f;
            }
            dock.Refetch();
            UpdateTools();
        }

        private static void UpdateTools()
        {
            if (toolTiles.Count != StudioShell.AllTools.Count()) { BuildTools(); return; }
            foreach (var kv in toolTiles) kv.Value.SetOn(StudioShell.Tool?.Id == kv.Key);
        }

        // ---- Palette --------------------------------------------------------------------------------------------

        public static void TogglePalette()
        {
            PaletteCollapsed = !PaletteCollapsed;
            palette?.SetVisible(!PaletteCollapsed);
            if (!PaletteCollapsed) dirtyPalette = true;
        }

        private static readonly (string id, string label)[] PaletteTabs = [("slots", "Slots"), ("scenery", "Scenery"), ("spawns", "Spawns"), ("library", "Library")];
        public static string SceneryFilter = "", LibraryFilter = "", LibraryCategory;

        private static void BuildPalette()
        {
            if (palette?.Body == null) return;
            var tool = StudioShell.Tool;
            lastToolPanel = tool?.HasPanel == true;
            palette.SetVisible(!PaletteCollapsed || lastToolPanel);
            if (lastToolPanel)
            {
                palette.Fill(page =>
                {
                    page.Header(tool.Name);
                    try { tool.BuildPanel(page); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio tool {tool.Id} panel failed", e); }
                });
                return;
            }
            if (PaletteCollapsed) return;
            palette.Fill(page =>
            {
                var tabs = PaletteTabs.Select(t => (t.id, t.id == "library" ? StudioServices.AssetBrowser?.Title ?? t.label : t.label)).ToList();
                tabs.AddRange(Studio.PaletteTabs.Select(t => (t.Id, t.Label)));
                Tabs(page, tabs, () => PaletteTab, id => { PaletteTab = id; dirtyPalette = true; }, tabs.Count > 4 ? 3 : 2);
                var extra = Studio.PaletteTabs.FirstOrDefault(t => t.Id == PaletteTab);
                switch (PaletteTab)
                {
                    case "slots": StudioPalette.Slots(page); break;
                    case "scenery": StudioPalette.Scenery(page); break;
                    case "spawns": StudioPalette.Spawns(page); break;
                    case var _ when extra.Build != null:
                        try { extra.Build(page); }
                        catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio palette tab {extra.Id} failed", e); }
                        break;
                    default: StudioPalette.Library(page); break;
                }
            });
        }

        /// <summary>A row of tab tiles at the top of a page.</summary>
        public static void Tabs(ModPage page, IReadOnlyList<(string id, string label)> tabs, Func<string> current, Action<string> pick, int perRow, float textSize = 21f)
        {
            int rows = (tabs.Count + perRow - 1) / perRow;
            var area = page.Custom("Tabs", rows * (TabH + 6f) + 6f);
            LayoutRebuilder.ForceRebuildLayoutImmediate(page.Content);
            float width = page.Size.x;
            float w = (width - (perRow - 1) * 6f) / perRow;
            for (int i = 0; i < tabs.Count; i++)
            {
                var (id, label) = tabs[i];
                var tile = ModTile.Create(area, "Tab " + id, new Vector2(w, TabH), null, label, textSize);
                tile.Label.alignment = TextAlignmentOptions.Center;
                tile.Place(new Vector2((i % perRow) * (w + 6f), -(i / perRow) * (TabH + 6f)));
                tile.SetOn(current() == id);
                tile.Applied += () => pick(id);
            }
        }

        // ---- Right panel ----------------------------------------------------------------------------------------

        private static List<(string id, string label)> RightTabs()
        {
            var list = new List<(string, string)> { ("inspector", "Inspector"), ("outliner", "Outliner"), ("level", "Level") };
            foreach (var p in Studio.Panels.Where(p => p.Mode == StudioMode.Edit && SafeVisible(p))) list.Add(("panel:" + p.Id, p.Id));
            return list;
        }

        private static bool SafeVisible(Studio.Panel p)
        {
            try { return p.Visible(); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio panel {p.Id} visible check failed", e); return false; }
        }

        private static void BuildRight()
        {
            if (right?.Body == null) return;
            var tabs = RightTabs();
            if (tabs.All(t => t.id != PanelTab)) PanelTab = "inspector";
            right.Fill(page =>
            {
                Tabs(page, tabs, () => PanelTab, id => { PanelTab = id; dirtyLevel = true; }, 3);
                switch (PanelTab)
                {
                    case "inspector": StudioInspector.Build(page); break;
                    case "outliner": StudioOutliner.Build(page); break;
                    case "level": StudioInspector.BuildLevel(page); break;
                    default:
                        var panel = Studio.Panels.FirstOrDefault(p => "panel:" + p.Id == PanelTab);
                        try { panel?.Build(page); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio panel {panel?.Id} failed", e); }
                        break;
                }
            });
        }

        public static void ShowPanel(string tab)
        {
            PanelTab = tab;
            dirtyLevel = true;
        }

        public static void RestoreTabs(StudioViewState v)
        {
            if (v.PaletteTab != null) PaletteTab = v.PaletteTab;
            if (v.PanelTab != null) PanelTab = v.PanelTab;
            PaletteCollapsed = v.PaletteCollapsed;
            RefreshAll();
        }

        /// <summary>A list row in a side panel: shorter, with room kept for its value on the right; lit when on. A label
        /// too long for its line wraps onto a second one (never cut short), so pass the page's width.</summary>
        public static void Compact(ModRow row, bool on = false, float valueRoom = 60f, float width = 0f)
        {
            var b = row.Button;
            var le = b.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = 40f;
            var text = b.GetComponent<TextMeshProUGUI>();
            if (text != null)
            {
                text.fontSize = Mathf.Min(text.fontSize, 23f);
                // The game's menu rows grow a lot when hovered; in a list that's a jump (and room kept for nothing).
                foreach (var style in new[] { b.selectedStyle, b.focussedStyle, b.focussedAndSelectedStyle })
                    if (style != null && style.fontSize > text.fontSize * 1.1f) style.fontSize = text.fontSize * 1.1f;
                text.enableWordWrapping = true;
                text.overflowMode = TextOverflowModes.Overflow;
                var value = b.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
                if (value != null)
                {
                    value.fontSize = 19f;
                    if (!string.IsNullOrEmpty(value.text)) valueRoom = Mathf.Max(valueRoom, value.GetPreferredValues(value.text).x + 10f);
                }
                text.margin = new Vector4(text.margin.x, 0f, valueRoom, 0f);
                if (width <= 0f && b.transform.parent is RectTransform parent && parent.rect.width > 1f) width = parent.rect.width;
                if (width > 0f && le != null)
                {
                    float room = Mathf.Max(40f, width - text.margin.x - valueRoom);
                    le.preferredHeight = Mathf.Max(40f, ModPopup.LabelWidth(b, text, room).y + 10f);
                }
                if (on) text.color = ModStyle.Bright;
            }
            if (on) ModStyle.AddEmblem(b.transform, 10f).anchoredPosition = new Vector2(-4f, 0f);
        }

        /// <summary>A list row in a side panel with its details on a second, smaller line under the name (so neither is
        /// cut short); lit when on.</summary>
        public static ModRow DetailRow(ModPage page, string name, string detail, bool on, Sprite icon, Action click, Func<string> tooltip = null)
        {
            string label = string.IsNullOrEmpty(detail) ? name : $"{name}\n<size=75%><color=#{ModStyle.Hex(ModStyle.Dim)}>{detail}</color></size>";
            var row = page.Row(label, null, tooltip, click, icon);
            Compact(row, on, 8f, page.Size.x);
            if (row.Button.GetComponent<TextMeshProUGUI>() is { } t) t.lineSpacing = -12f;
            return row;
        }

        /// <summary>A heading that fits a side panel.</summary>
        public static void SmallHeader(ModPage page, string text)
        {
            var h = page.Header(text);
            h.fontSize = 30f;
            h.enableWordWrapping = true;
        }

        // ---- Dev ------------------------------------------------------------------------------------------------

        /// <summary>Presses a tile or row by name in the dock's panels (dev).</summary>
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

        /// <summary>The panels' screen rects (dev: to check the gutters).</summary>
        public static string Describe()
        {
            if (dock?.Root == null) return "no dock";
            var root = dock.Root.rect;
            return $"canvas {root.width:0}x{root.height:0} scale {dock.Root.lossyScale.x:0.###}; " + string.Join("; ", dock.Panels.Where(p => p.Rect != null && p.Visible).Select(p =>
            {
                var c = new Vector3[4];
                p.Rect.GetWorldCorners(c);
                return $"{p.Name} [{c[0].x:0},{c[0].y:0} .. {c[2].x:0},{c[2].y:0}]";
            })) + $"; screen {Screen.width}x{Screen.height}";
        }
    }
}
