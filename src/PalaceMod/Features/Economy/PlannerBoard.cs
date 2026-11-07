using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PalaceMod.Economy;
using Object = UnityEngine.Object;
using G = PalaceMod.AnalyticsGraph;
using V = PalaceMod.Economy.PlannerVisuals;

/*
    The planner's board (Run Analytics > Economy, a wide tab): a toolbar (plan, compare, perks, income sources, undo,
    and the end gold), then one column per day left to the level's last night. A day's builds and upgrades are chips
    above the night line, the buildings lost and gold found that night below it; identical steps on a day are one chip
    ("House ×5"). Under the columns, on the same x, the gold at each sunrise (and after building, and what it's
    compared with). An info line at the bottom says what's selected and what the inputs do.

    Inputs on a chip: apply opens its popup (PlannerPopups.cs); the move keys (the controller's shoulders) move it a
    day; the remove key or a right click removes it; dragging drops it on another day. A day's header opens the day's
    popup; "+ build" / "+ lost" open the Add screen (PlannerAdd.cs). Every change can be undone.

    The board re-renders itself in place after each change (not the whole frame), keeping the selection on the same
    thing by key, and its scroll position across tab switches.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private ModFrame host;
        private PlannerBoard board;
        internal string BoardFocus;
        internal float BoardScrollX, BoardScrollY;
        private readonly List<string> undo = [];
        private Keybind keyEarlier, keyLater, keyRemove, keyUndo;
        internal const string KeyContext = "Economy planner";

        private void BuildFrames()
        {
            sourcesFrame = new ModFrame("Income sources").SetContent(BuildSources);
            addFrame = new ModFrame("Add to the plan").SetContent(BuildAdd, wide: true);
            addFrame.Activated += () => addView?.OnActivated();
            perksFrame = new ModFrame("Perks").SetContent(BuildPerks, wide: true);
        }

        private void RegisterKeys()
        {
            keyEarlier = Keybinds.Register(FeatureSection, "earlier", "Planner: move a day earlier", new KeyboardShortcut(KeyCode.Comma),
                "On the economy planner's board: moves the selected item a day earlier (the controller's left shoulder does too).", KeybindFlags.AllowInMenus, KeyContext);
            keyLater = Keybinds.Register(FeatureSection, "later", "Planner: move a day later", new KeyboardShortcut(KeyCode.Period),
                "On the economy planner's board: moves the selected item a day later (the controller's right shoulder does too).", KeybindFlags.AllowInMenus, KeyContext);
            keyRemove = Keybinds.Register(FeatureSection, "remove", "Planner: remove", new KeyboardShortcut(KeyCode.Backspace),
                "On the economy planner's board: removes the selected item (a right click does too).", KeybindFlags.AllowInMenus, KeyContext);
            keyUndo = Keybinds.Register(FeatureSection, "undo", "Planner: undo", new KeyboardShortcut(KeyCode.Z),
                "In the economy planner: undoes the last change.", KeybindFlags.AllowInMenus, KeyContext);
            Keybinds.ClaimContext(KeyContext, () => (host != null && host.IsOpen && board != null) || (addFrame != null && addFrame.IsOpen) || (perksFrame != null && perksFrame.IsOpen));
        }

        internal Keybind EarlierKey => keyEarlier;
        internal Keybind LaterKey => keyLater;
        internal Keybind RemoveKey => keyRemove;
        internal Keybind UndoKey => keyUndo;
        internal bool KeyEarlier => keyEarlier.Down || ShoulderDown(false);
        internal bool KeyLater => keyLater.Down || ShoulderDown(true);
        internal bool KeyRemove => keyRemove.Down;
        internal bool KeyUndo => keyUndo.Down;

        internal static bool ShoulderDown(bool next)
        {
            try { return Plugin.Input != null && Plugin.Input.GetButtonDown(next ? "NextTab" : "PrevTab"); }
            catch { return false; }
        }

        /// <summary>Run Analytics hands over its frame: the board re-renders when the player comes back to it.</summary>
        internal void AttachHost(ModFrame frame)
        {
            if (host == frame) return;
            host = frame;
            frame.Activated += () => board?.OnActivated();
        }

        /// <summary>The Economy tab.</summary>
        internal void BuildPage(ModPage page)
        {
            board = null;
            if (Project() == null)
            {
                page.Text("The economy planner works during a level.");
                return;
            }
            var area = page.Custom("Planner", page.Size.y);
            board = new PlannerBoard(this, page.Frame ?? host, area, page.Size);
            page.Wired += (above, below) => board?.Connect(above, below);
            board.Render();
        }

        // ---- Changes and undo -------------------------------------------------------------------------------------

        /// <summary>Makes a change to the plans: remembered for undo, saved, and every open view redrawn.
        /// <paramref name="focus"/> is what the board selects afterwards (a chip's first action id).</summary>
        internal void Edit(Action change, string focus = null)
        {
            if (file == null) return;
            Remember();
            change();
            file.Normalize();
            Save();
            if (focus != null) BoardFocus = "chip:" + focus;
            RefreshOpen();
        }

        private void Remember()
        {
            undo.Add(JsonConvert.SerializeObject(file));
            if (undo.Count > 60) undo.RemoveAt(0);
        }

        internal bool CanUndo => undo.Count > 0;

        internal void Undo()
        {
            if (undo.Count == 0 || file == null)
            {
                Notify.Show("Nothing to undo.", NotifyKind.Info, 2f, key: "econ-undo");
                return;
            }
            string level = file.Level;
            var restored = JsonConvert.DeserializeObject<PlannerFile>(undo[undo.Count - 1]);
            undo.RemoveAt(undo.Count - 1);
            if (restored == null) return;
            restored.Level = level;
            restored.Normalize();
            file = restored;
            Save();
            // Popups hold the old plan's items.
            ModPopup.Current?.Close();
            Notify.Show("Undone.", NotifyKind.Info, 1.5f, key: "econ-undo");
            RefreshOpen();
        }

        /// <summary>Redraws whatever planner view is on screen.</summary>
        internal void RefreshOpen()
        {
            board?.Render();
            if (addFrame != null && addFrame.IsOpen) addView?.Render();
            if (perksFrame != null && perksFrame.IsOpen) perksView?.Render();
            if (sourcesFrame != null && sourcesFrame.IsOpen) sourcesFrame.Refresh();
            ModPopup.Current?.Refresh();
        }

        /// <summary>The plan that owns an action (a branch's inherited steps belong to its parent).</summary>
        internal PlannerScenario Holder(Projection p, EconAction a) => p.File.OwnerOf(a) ?? p.Plan;

        /// <summary>The first and last day (or night) an action of this kind can be on.</summary>
        internal static (int First, int Last) DayRange(EconWorld w, EconActionKind kind) =>
            (kind == EconActionKind.Step || !w.DawnPending ? w.FirstDay : w.FirstDay - 1, w.LastNight);

        internal const string FixedNote = "Every run starts by building the castle on day 1: it's part of every plan, and of doing nothing.";

        /// <summary>Gold short on <paramref name="day"/>: what its steps cost beyond the gold there was when each was paid.</summary>
        internal static int ShortOn(EconResult r, int day) =>
            r.Actions.Where(x => x.Status == ActionStatus.Short && x.Action.IsStep && x.Action.Day == day).Sum(x => x.Shortfall);

        /// <summary>Gold left on <paramref name="day"/> after its steps (what more building that day can spend).</summary>
        internal static int LeftOn(EconResult r, int day) => r.Days.FirstOrDefault(d => d.Day == day)?.AfterSpend ?? 0;

        /// <summary>Whether the plan with <paramref name="remove"/> taken out and <paramref name="add"/> put in can pay
        /// for everything on <paramref name="day"/> (no worse than it already is). <paramref name="why"/> says what's missing.</summary>
        internal static bool Affordable(Projection p, IEnumerable<EconAction> remove, IEnumerable<EconAction> add, int day, out string why)
        {
            why = null;
            if (day < p.World.FirstDay) return true;
            var gone = new HashSet<EconAction>(remove ?? []);
            var acts = p.Actions.Where(a => !gone.Contains(a)).Concat(add ?? []).ToList();
            var after = EconSim.Run(p.World, acts, paybacks: false);
            int before = ShortOn(p.Result, day), now = ShortOn(after, day);
            if (now <= before) return true;
            int cost = after.Actions.Where(x => x.Action.IsStep && x.Action.Day == day && x.Counts && (add?.Contains(x.Action) ?? false)).Sum(x => x.Cost);
            int left = LeftOn(p.Result, day) + p.Result.Actions.Where(x => gone.Contains(x.Action) && x.Action.Day == day && x.Counts).Sum(x => x.Cost);
            why = $"Not enough gold on day {day}: that costs {Coin(cost)}, {Coin(Math.Max(0, left))} is left that day.";
            return false;
        }

        /// <summary>Moves a chip's actions by <paramref name="delta"/> days (or to <paramref name="toDay"/>).</summary>
        internal bool MoveGroup(Projection p, List<EconAction> members, int? toDay, int delta = 0)
        {
            if (members.Count == 0) return false;
            if (members.Any(p.IsFixed))
            {
                Notify.Show(FixedNote, NotifyKind.Info, 3f, key: "econ-move");
                return false;
            }
            var (first, last) = DayRange(p.World, members[0].Kind);
            if (first > last) return false;
            int from = members[0].Day;
            int day = Mathf.Clamp(toDay ?? from + delta, first, last);
            if (day == from)
            {
                if (toDay != null && toDay < first)
                    Notify.Show(members[0].IsStep ? $"Builds can only go on day {first} or later." : $"That can only go on night {first} or later.", NotifyKind.Info, 2.5f, key: "econ-move");
                return false;
            }
            if (members[0].IsStep)
            {
                var moved = members.Select(a => { var c = a.Copy(); c.Day = day; return c; }).ToList();
                if (!Affordable(p, members, moved, day, out string why))
                {
                    Notify.Show(why, NotifyKind.Warning, 3f, key: "econ-move");
                    return false;
                }
            }
            Edit(() => { foreach (var a in members) a.Day = day; }, members[0].Id);
            return true;
        }

        internal void RemoveGroup(Projection p, List<EconAction> members)
        {
            if (members.Count == 0) return;
            if (members.Any(p.IsFixed))
            {
                Notify.Show(FixedNote, NotifyKind.Info, 3f, key: "econ-move");
                return;
            }
            Edit(() => { foreach (var a in members) Holder(p, a).Actions.Remove(a); });
        }
    }

    /// <summary>One chip: identical actions on the same day (or one action).</summary>
    internal sealed class PlanGroup
    {
        public string Key => "chip:" + Members[0].Id;
        public readonly List<EconAction> Members = [];
        public ActionResult R;
        public PlannerScenario Owner;
        public bool Inherited;
        public EconAction First => Members[0];
        public int Count => Members.Count;

        /// <summary>The plan's actions as chips, in plan order.</summary>
        public static List<PlanGroup> Build(EconomyPlannerManager.Projection p)
        {
            var groups = new List<PlanGroup>();
            var byKey = new Dictionary<string, PlanGroup>();
            foreach (var a in EconSim.Ordered(p.Actions))
            {
                var r = p.Result.Actions.FirstOrDefault(x => x.Action == a);
                if (r == null) continue;
                var owner = p.File.OwnerOf(a) ?? p.Plan;
                string key = a.Kind == EconActionKind.Gold || r.Slot == null
                    ? a.Id
                    : $"{owner.Id}|{a.Kind}|{a.Day}|{r.Slot.TypeName}|{a.Level}|{a.Choice}|{(a.Amount?.ToString() ?? "")}|{r.Counts}|{r.Status == ActionStatus.Short}";
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new PlanGroup { R = r, Owner = owner, Inherited = p.InheritedFrom(a) };
                    byKey[key] = g;
                    groups.Add(g);
                }
                g.Members.Add(a);
            }
            return groups;
        }
    }

    internal sealed class PlannerBoard
    {
        private const float Gutter = 70f, ToolbarH = 42f, HeaderH = 54f, RowH = 38f, ChipH = 34f, AddH = 28f, Divider = 18f,
            InfoH = 56f, EndW = 92f, MinCol = 122f, MaxCol = 200f, Gap = 10f;

        private enum Kind { Done, Tonight, Day }

        private sealed class Column
        {
            public Kind Kind;
            public int Day;
            public DayResult Result, Compare;
            public readonly List<PlanGroup> DayGroups = [], NightGroups = [];
            public ModTile Header, AddDay, AddNight;
            public readonly List<ModTile> Elements = [];
            public bool HasDay => Kind != Kind.Tonight;
            public bool HasNight => Kind != Kind.Done;
        }

        private readonly EconomyPlannerManager m;
        private readonly ModFrame host;
        private readonly RectTransform area;
        private readonly Vector2 size;
        private ThronefallUIElement above, below;

        internal EconomyPlannerManager.Projection P { get; private set; }
        private readonly List<Column> cols = [];
        private readonly List<ModTile> toolbar = [];
        internal readonly Dictionary<string, ModTile> Tiles = [];
        private readonly Dictionary<ModTile, PlanGroup> groupOf = [];
        private readonly Dictionary<ModTile, Column> columnOf = [];
        private RectTransform root, headerLayer, bodyLayer, graphLayer, graphPlot, bodyMask;
        private TextMeshProUGUI info, hints;
        private float colW, viewW, bodyViewH, bodyH, contentW, boardTop, graphTop, graphH;
        private float scrollX, scrollY;
        private int litColumn = -1;
        private ModTile dragging;

        public PlannerBoard(EconomyPlannerManager m, ModFrame host, RectTransform area, Vector2 size)
        {
            this.m = m;
            this.host = host;
            this.area = area;
            this.size = size;
            scrollX = m.BoardScrollX;
            scrollY = m.BoardScrollY;
        }

        private string Focus
        {
            get => m.BoardFocus;
            set => m.BoardFocus = value;
        }

        /// <summary>Wired by the frame: what's above the toolbar (the tab) and below the board (Back).</summary>
        public void Connect(ThronefallUIElement above, ThronefallUIElement below)
        {
            this.above = above;
            this.below = below;
            Wire();
        }

        public void OnActivated()
        {
            if (area == null) return;
            // The frame put back whatever it had stored (maybe gone since): the board's own focus wins.
            Render(keepSelection: false);
        }

        // ---- Build ------------------------------------------------------------------------------------------------

        public void Render(bool keepSelection = true)
        {
            if (area == null) return; // the page went away
            var keep = keepSelection ? host?.Frame?.CurrentSelection as ModTile : null;
            if (keep != null && Tiles.ContainsValue(keep) && keep.Key != null && Focus == null) Focus = keep.Key;
            if (root != null) Object.DestroyImmediate(root.gameObject);
            Tiles.Clear();
            groupOf.Clear();
            columnOf.Clear();
            toolbar.Clear();
            cols.Clear();
            P = m.Project();
            var rootObj = new GameObject("Board", typeof(RectTransform));
            rootObj.transform.SetParent(area, false);
            root = (RectTransform)rootObj.transform;
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = size;
            rootObj.AddComponent<Driver>().Board = this;
            // A dark panel behind it all, so the level doesn't show through the board.
            var backdrop = V.Panel(root, "Backdrop", size.x + 40f, size.y + 24f, 0.9f);
            backdrop.anchoredPosition = new Vector2(-20f, 12f);
            if (P == null)
            {
                V.Text(root, "The economy planner works during a level.", 0f, size.x, 25f);
                return;
            }
            Columns();
            Layout();
            Toolbar();
            Board();
            Graph();
            Info();
            Wire();
            host?.Frame?.RefetchManagedElements();
            Restore();
            Apply(true);
        }

        private void Columns()
        {
            var w = P.World;
            var groups = PlanGroup.Build(P);
            bool anyDone = groups.Any(g => g.First.IsStep ? g.First.Day < w.FirstDay : g.First.Day < (w.DawnPending ? w.FirstDay - 1 : w.FirstDay));
            if (anyDone) cols.Add(new Column { Kind = Kind.Done, Day = 0 });
            if (w.DawnPending) cols.Add(new Column { Kind = Kind.Tonight, Day = w.FirstDay - 1 });
            for (int d = w.FirstDay; d <= w.LastNight; d++)
                cols.Add(new Column
                {
                    Kind = Kind.Day, Day = d, Result = P.Result.Days.FirstOrDefault(x => x.Day == d),
                    Compare = P.Compare == P.Result ? null : P.Compare.Days.FirstOrDefault(x => x.Day == d),
                });
            foreach (var g in groups)
            {
                var a = g.First;
                Column col = a.IsStep
                    ? cols.FirstOrDefault(c => c.Kind == Kind.Day && c.Day == a.Day)
                    : cols.FirstOrDefault(c => c.Kind != Kind.Done && c.Day == a.Day);
                col ??= cols.First(); // the Done column
                (a.IsStep ? col.DayGroups : col.NightGroups).Add(g);
            }
        }

        private void Layout()
        {
            int n = cols.Count;
            viewW = size.x - Gutter;
            colW = n == 0 ? MinCol : Mathf.Clamp((viewW - EndW) / n, MinCol, MaxCol);
            contentW = n * colW + EndW;
            boardTop = ToolbarH + Gap;
            int dayRows = cols.Max(c => c.HasDay ? c.DayGroups.Count + 1 : 0);
            int nightRows = cols.Max(c => c.HasNight ? c.NightGroups.Count + 1 : 0);
            // Room for one more chip a day and a night than the busiest, so the board doesn't jump on every add.
            dayRowsCount = Mathf.Max(2, dayRows + 1);
            bodyH = dayRowsCount * RowH + Divider + Mathf.Max(2, nightRows + 1) * RowH + 6f;
            // The board takes what it needs (scrolling past a limit); the graph gets the rest.
            float room = size.y - InfoH - boardTop - HeaderH - Gap;
            float minGraph = size.y >= 640f ? 120f : 100f;
            // Without its spare day row if that's what it takes to show every chip.
            if (bodyH > room - minGraph && dayRowsCount > Mathf.Max(2, dayRows))
            {
                dayRowsCount = Mathf.Max(2, dayRows);
                bodyH = dayRowsCount * RowH + Divider + Mathf.Max(2, nightRows + 1) * RowH + 6f;
            }
            bodyViewH = Mathf.Clamp(bodyH, 4f * RowH + Divider, room - minGraph);
            graphH = Mathf.Min(room - bodyViewH, 300f);
            graphTop = boardTop + HeaderH + bodyViewH + Gap;
            scrollX = Mathf.Clamp(scrollX, 0f, Mathf.Max(0f, contentW - viewW));
            scrollY = Mathf.Clamp(scrollY, 0f, Mathf.Max(0f, bodyH - bodyViewH));
        }

        private int dayRowsCount;
        private float NightTop => dayRowsCount * RowH + Divider;

        private RectTransform Box(Transform parent, string name, float x, float y, float w, float h, bool mask = false)
        {
            var obj = mask ? new GameObject(name, typeof(RectTransform), typeof(RectMask2D)) : new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        private static RectTransform Fill(Transform parent, string name, float x, float y, float w, float h, Color color)
        {
            var rect = G.Rect(parent, name, color);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        private static TextMeshProUGUI Words(Transform parent, string text, float x, float y, float w, float h, float fontSize, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, TMP_FontAsset font = null)
        {
            var tmp = GameWidgets.Text(parent, "Text", text, font ?? ModStyle.BodyFont, fontSize, color, align);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.characterSpacing = 0f;
            var rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return tmp;
        }

        private ModTile Tile(Transform parent, string key, float x, float y, float w, float h, Sprite icon, string text, float fontSize = 19f)
        {
            var tile = ModTile.Create(parent, key, new Vector2(w, h), icon, text, fontSize).Place(new Vector2(x, -y));
            tile.Key = key;
            Tiles[key] = tile;
            tile.Shown += Shown;
            return tile;
        }

        // ---- Toolbar ----------------------------------------------------------------------------------------------

        private void Toolbar()
        {
            var p = P;
            var w = p.World;
            float x = 0f;
            string dim = EconomyPlannerManager.DimHex;
            ModTile Add(string key, float width, string text, Action onApply)
            {
                var t = Tile(root, key, x, 0f, width, ToolbarH - 4f, null, text, 20f);
                t.Applied += onApply;
                toolbar.Add(t);
                x += width + 10f;
                return t;
            }
            Add("tool:plan", 270f, $"<color=#{dim}>Plan</color>  {p.PlanName}", () => m.PlanPopup(this, Tiles["tool:plan"]));
            Add("tool:compare", 230f, $"<color=#{dim}>vs</color>  {EconomyPlannerManager.Capital(p.OtherName)}", () => m.ComparePopup(this, Tiles["tool:compare"]));
            int changed = p.File.EffectivePerks(p.Plan).Count(kv => w.Perks.Any(k => k.Key == kv.Key && k.Equipped != kv.Value));
            var perks = Add("tool:perks", 170f, changed == 0 ? "Perks" : $"Perks <color=#{dim}>·</color> {changed}", m.OpenPerks);
            perks.SetMarker(changed > 0);
            Add("tool:sources", 180f, "Income sources", m.OpenSources);
            var undo = Add("tool:undo", 90f, "Undo", m.Undo);
            undo.SetDim(!m.CanUndo, 0.5f);

            var r = p.Result;
            var shortDay = EconomyPlannerManager.FirstShort(r);
            if (shortDay is int sd)
            {
                // A change to an earlier day left a later one without the gold for its builds: say so, and offer the fix.
                var fix = Add("tool:short", 220f, $"Short on day {sd} · fix", () => m.ShortPopup(this, sd, Tiles["tool:short"]));
                fix.SetWarning(true);
                fix.SetFill(Color.Lerp(ModStyle.Panel, V.Red, 0.35f));
                fix.Label.color = ModStyle.Bright;
            }
            var parts = new List<string> { $"<size=120%><b>{EconomyPlannerManager.Coin(r.EndGold)}</b></size> at the end" };
            if (p.Compare != r) parts.Add($"{EconomyPlannerManager.Signed(r.EndGold - p.Compare.EndGold)} vs {EconomyPlannerManager.Short(p.OtherName, 18)}");
            else if (p.Baseline != r) parts.Add($"{EconomyPlannerManager.Signed(r.EndGold - p.Baseline.EndGold)} vs doing nothing");
            var summary = Words(root, string.Join($"  <color=#{dim}>·</color>  ", parts), x + 4f, 0f, size.x - x - 4f, ToolbarH - 4f, 21f, ModStyle.Gold,
                TextAlignmentOptions.MidlineRight, ModStyle.BoldFont);
            summary.enableAutoSizing = true;
            summary.fontSizeMin = 15f;
            summary.fontSizeMax = 21f;
        }

        // ---- Columns ----------------------------------------------------------------------------------------------

        private float ColX(int i) => i * colW;

        private void Board()
        {
            var w = P.World;
            var headerMask = Box(root, "Header View", Gutter, boardTop, viewW, HeaderH, mask: true);
            headerLayer = Box(headerMask, "Headers", 0f, 0f, contentW, HeaderH);
            bodyMask = Box(root, "Body View", Gutter, boardTop + HeaderH, viewW, bodyViewH, mask: true);
            bodyLayer = Box(bodyMask, "Body", 0f, 0f, contentW, Mathf.Max(bodyH, bodyViewH));
            float fullH = Mathf.Max(bodyH, bodyViewH);

            // Day and night backgrounds, and the gutter's labels.
            Fill(bodyLayer, "Days", 0f, 0f, contentW, NightTop - Divider / 2f, new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.55f));
            Fill(bodyLayer, "Nights", 0f, NightTop - Divider / 2f, contentW, fullH - NightTop + Divider / 2f, new Color(0.06f, 0.065f, 0.08f, 0.82f));
            Fill(bodyLayer, "Night line", 0f, NightTop - Divider / 2f, contentW, 1.5f, G.Fade(ModStyle.Dim, 0.8f));
            var gutterMask = Box(root, "Gutter", 0f, boardTop + HeaderH, Gutter - 8f, bodyViewH, mask: true);
            gutterLayer = Box(gutterMask, "Labels", 0f, 0f, Gutter - 8f, fullH);
            Words(gutterLayer, "Day", 0f, 4f, Gutter - 12f, 26f, 20f, ModStyle.Dim, TextAlignmentOptions.MidlineRight, ModStyle.ButtonFont);
            Words(gutterLayer, "Night", 0f, NightTop + 4f, Gutter - 12f, 26f, 20f, ModStyle.Dim, TextAlignmentOptions.MidlineRight, ModStyle.ButtonFont);

            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                float x = ColX(i);
                if (i > 0) Fill(bodyLayer, "Rule", x, 0f, 1f, fullH, G.Fade(ModStyle.Dim, 0.35f));
                Header(c, i, x);
                if (c.HasDay)
                {
                    for (int k = 0; k < c.DayGroups.Count; k++) Chip(c, c.DayGroups[k], x, k * RowH + 3f);
                    if (c.Kind == Kind.Day)
                    {
                        c.AddDay = Tile(bodyLayer, $"add:{c.Day}", x + 5f, c.DayGroups.Count * RowH + 6f, colW - 10f, AddH, null, "+ build", 17f);
                        c.AddDay.SetFill(new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.3f));
                        c.AddDay.Label.color = ModStyle.Dim;
                        c.AddDay.Label.alignment = TextAlignmentOptions.Center;
                        int day = c.Day;
                        c.AddDay.Applied += () => m.OpenAdd(day, PlannerAddMode.Build);
                        c.Elements.Add(c.AddDay);
                        columnOf[c.AddDay] = c;
                    }
                }
                if (c.HasNight)
                {
                    for (int k = 0; k < c.NightGroups.Count; k++) Chip(c, c.NightGroups[k], x, NightTop + k * RowH + 3f);
                    c.AddNight = Tile(bodyLayer, $"lost:{c.Day}", x + 5f, NightTop + c.NightGroups.Count * RowH + 6f, colW - 10f, AddH, null, "+ lost", 17f);
                    c.AddNight.SetFill(new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.3f));
                    c.AddNight.Label.color = ModStyle.Dim;
                    c.AddNight.Label.alignment = TextAlignmentOptions.Center;
                    int day = c.Day;
                    c.AddNight.Applied += () => m.OpenAdd(day, PlannerAddMode.Lose);
                    c.Elements.Add(c.AddNight);
                    columnOf[c.AddNight] = c;
                }
            }
            // End of the level: the answer, not selectable.
            float ex = ColX(cols.Count);
            Fill(headerLayer, "End", ex + 4f, 3f, EndW - 8f, HeaderH - 6f, new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.7f));
            var end = Words(headerLayer, $"End\n<size=85%>{EconomyPlannerManager.Coin(P.Result.EndGold)}</size>", ex + 4f, 3f, EndW - 8f, HeaderH - 6f, 19f, ModStyle.Bright,
                TextAlignmentOptions.Center, ModStyle.BoldFont);
            end.enableWordWrapping = true;
            end.overflowMode = TextOverflowModes.Overflow;
            end.lineSpacing = -18f;

            // A branch starts at its fork day: a verdigris line there.
            if (P.Plan.Parent != null && P.Plan.ForkDay != int.MaxValue)
            {
                int fork = cols.FindIndex(c => c.Kind == Kind.Day && c.Day == P.Plan.ForkDay);
                if (fork > 0)
                {
                    var accent = G.Fade(ModStyle.Accent, 0.85f);
                    Fill(bodyLayer, "Fork", ColX(fork) - 1.5f, 0f, 3f, fullH, accent);
                    Fill(headerLayer, "Fork", ColX(fork) - 1.5f, 0f, 3f, HeaderH, accent);
                }
            }
            Arrows();
        }

        private RectTransform gutterLayer;
        private TextMeshProUGUI leftMore, rightMore;

        private void Arrows()
        {
            leftMore = Words(root, "", 0f, boardTop, Gutter - 8f, HeaderH, 18f, ModStyle.Gold, TextAlignmentOptions.MidlineRight, ModStyle.BoldFont);
            rightMore = Words(root, "", size.x - 60f, boardTop - 22f, 60f, 22f, 17f, ModStyle.Gold, TextAlignmentOptions.MidlineRight, ModStyle.BoldFont);
        }

        private void Header(Column c, int i, float x)
        {
            string text;
            string badge = null;
            Color? badgeColor = null;
            var w = P.World;
            switch (c.Kind)
            {
                case Kind.Done:
                    text = "Earlier\n<size=78%>done</size>";
                    break;
                case Kind.Tonight:
                    text = $"Tonight\n<size=78%>now {EconomyPlannerManager.Coin(w.Gold)}</size>";
                    break;
                default:
                    var d = c.Result;
                    // What the day has to spend, and what's left after its builds: the budget being split up.
                    string gold = "";
                    if (d != null)
                    {
                        int left = d.AfterSpend;
                        string leftText = left < 0 ? $"<color=#{EconomyPlannerManager.RedHex}>{left} short</color>" : d.Spent > 0 ? $"<color=#{ModStyle.Hex(ModStyle.Bright)}>{left} left</color>" : "to spend";
                        gold = $"{EconomyPlannerManager.Coin(d.Start)} <color=#{EconomyPlannerManager.DimHex}>·</color> {leftText}";
                    }
                    text = $"Day {c.Day}\n<size=74%>{gold}</size>";
                    if (d != null && c.Compare != null && d.Start != c.Compare.Start)
                    {
                        badge = EconomyPlannerManager.Signed(d.Start - c.Compare.Start);
                        badgeColor = d.Start > c.Compare.Start ? ModStyle.Accent : V.Red;
                    }
                    break;
            }
            var tile = Tile(headerLayer, $"day:{(c.Kind == Kind.Done ? "done" : c.Day.ToString())}", x + 4f, 3f, colW - 8f, HeaderH - 6f, null, text, 19f);
            tile.Label.alignment = TextAlignmentOptions.Center;
            tile.Label.enableWordWrapping = true;
            tile.Label.lineSpacing = -18f;
            tile.SetBadge(badge, badgeColor);
            tile.Badge.fontSize = 15f;
            if (c.Kind == Kind.Day && c.Result != null && c.Result.Shortfall > 0) tile.SetWarning(true);
            if (c.Kind == Kind.Tonight) tile.SetFill(new Color(0.07f, 0.075f, 0.09f, 1f));
            var col = c;
            tile.Applied += () => m.DayPopup(this, col.Kind == Kind.Done ? int.MinValue : col.Day, col.Kind == Kind.Tonight, tile);
            c.Header = tile;
            columnOf[tile] = c;
        }

        private void Chip(Column c, PlanGroup g, float x, float y)
        {
            var r = g.R;
            var a = g.First;
            var slot = r.Slot;
            Sprite icon = null;
            string text;
            switch (a.Kind)
            {
                case EconActionKind.Gold:
                    text = $"+{a.Amount ?? 0} <sprite name=\"coin\">";
                    break;
                case EconActionKind.KnockOut:
                    icon = PlannerIcons.Kind(slot?.TypeName);
                    text = PlannerIcons.Short(slot?.TypeName);
                    break;
                default:
                    icon = PlannerIcons.Step(m.Snap(), slot, a.Level, a.Choice);
                    text = PlannerIcons.Short(slot?.TypeName) + (a.Level > 1 ? " " + PlannerIcons.Roman(a.Level) : "");
                    break;
            }
            bool fixedStep = P.IsFixed(a);
            if (g.Inherited) text = "› " + text;
            var tile = Tile(bodyLayer, g.Key, x + 5f, y, colW - 10f, ChipH, icon, text, 18f);
            if (fixedStep)
            {
                // The castle's build: part of every plan, so it can't be moved or removed.
                tile.SetFill(Color.Lerp(ModStyle.Panel, ModStyle.Gold, 0.12f));
                tile.SetBadge("fixed", ModStyle.Dim);
                tile.Badge.fontSize = 13f;
                tile.Applied += () => Notify.Show(EconomyPlannerManager.FixedNote, NotifyKind.Info, 3f, key: "econ-move");
                groupOf[tile] = g;
                columnOf[tile] = c;
                c.Elements.Add(tile);
                return;
            }
            if (g.Count > 1) tile.SetBadge($"×{g.Count}");
            if (a.Kind == EconActionKind.KnockOut) tile.SetFill(Color.Lerp(ModStyle.Panel, V.Red, 0.28f));
            else if (a.Kind == EconActionKind.Gold) tile.SetFill(Color.Lerp(ModStyle.Panel, ModStyle.Gold, 0.18f));
            if (r.Status == ActionStatus.Short) tile.SetWarning(true);
            if (!r.Counts) tile.SetDim(true, 0.4f);
            else if (g.Inherited) tile.SetDim(true, 0.68f);
            tile.Draggable = true;
            var group = g;
            tile.Applied += () => m.ChipPopup(this, group, tile);
            tile.RightClicked += () => m.RemoveGroup(P, group.Members);
            tile.Dragging += at => Drag(tile, at);
            tile.Dropped += at => Drop(group, at);
            tile.DragEnded += () => dragging = null;
            groupOf[tile] = g;
            columnOf[tile] = c;
            c.Elements.Add(tile);
        }

        // ---- Drag and drop ----------------------------------------------------------------------------------------

        private Vector2 Local(RectTransform rect, Vector2 screen)
        {
            var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screen, cam, out var local);
            return local;
        }

        /// <summary>The column under a screen point (by x), or -1.</summary>
        private int ColumnAt(Vector2 screen)
        {
            var local = Local(bodyLayer, screen);
            // bodyLayer's pivot is its top left.
            int i = Mathf.FloorToInt(local.x / colW);
            return i >= 0 && i < cols.Count ? i : -1;
        }

        private void Drag(ModTile tile, Vector2 screen)
        {
            dragging = tile;
            var local = Local(bodyLayer, screen);
            tile.transform.SetAsLastSibling();
            tile.Place(new Vector2(local.x - tile.Rect.sizeDelta.x / 2f, local.y + tile.Rect.sizeDelta.y / 2f));
            int i = ColumnAt(screen);
            if (i != litColumn) { litColumn = i; Graph(); }
        }

        private void Drop(PlanGroup g, Vector2 screen)
        {
            int i = ColumnAt(screen);
            dragging = null;
            if (i < 0 || cols[i].Kind == Kind.Done || (g.First.IsStep && cols[i].Kind == Kind.Tonight) || !m.MoveGroup(P, g.Members, cols[i].Day))
                Render(); // back where it was
        }

        /// <summary>Drops a chip on a day as a mouse drag would (dev command).</summary>
        internal bool DebugDrop(string key, int day)
        {
            if (key == "selected") key = Selected?.Key ?? "";
            if (!Tiles.TryGetValue(key, out var tile) || !groupOf.TryGetValue(tile, out var g)) throw new ArgumentException($"no chip {key}");
            int i = cols.FindIndex(c => c.Kind != Kind.Done && c.Day == day && (!g.First.IsStep || c.Kind == Kind.Day));
            if (i < 0) throw new ArgumentException($"no column for day {day}");
            var center = new Vector2(ColX(i) + colW / 2f, -10f);
            var screen = RectTransformUtility.WorldToScreenPoint(null, bodyLayer.TransformPoint(center));
            Drag(tile, screen);
            Drop(g, screen);
            return true;
        }

        // ---- Graph ------------------------------------------------------------------------------------------------

        private void Graph()
        {
            if (P == null) return;
            if (graphLayer != null) Object.DestroyImmediate(graphLayer.parent.gameObject);
            var view = Box(root, "Graph", 0f, graphTop, size.x, graphH);
            var mask = Box(view, "Plot View", Gutter, 0f, viewW, graphH, mask: true);
            graphLayer = Box(mask, "Plot", -scrollX, 0f, contentW, graphH);
            var plot = new GameObject("Area", typeof(RectTransform));
            plot.transform.SetParent(graphLayer, false);
            graphPlot = (RectTransform)plot.transform;
            graphPlot.anchorMin = graphPlot.anchorMax = graphPlot.pivot = Vector2.zero;
            graphPlot.sizeDelta = new Vector2(contentW, graphH);
            var bg = G.Rect(view, "Back", new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.55f));
            bg.anchorMin = bg.anchorMax = bg.pivot = new Vector2(0f, 1f);
            bg.anchoredPosition = new Vector2(Gutter, 0f);
            bg.sizeDelta = new Vector2(viewW, graphH);
            bg.SetAsFirstSibling();

            var p = P;
            var r = p.Result;
            float Gold(Column c, Func<DayResult, float> f) => c.Kind == Kind.Day && c.Result != null ? f(c.Result) : float.NaN;
            float[] Values(Func<Column, float> f, float end) => cols.Select(f).Append(end).ToArray();
            var series = new List<GraphSeries>();
            if (p.Compare != r)
                series.Add(new GraphSeries(EconomyPlannerManager.Capital(EconomyPlannerManager.Short(p.OtherName, 22)), G.Fade(G.Dim, 0.95f),
                    Values(c => c.Kind == Kind.Day ? (float)(c.Compare?.Start ?? float.NaN) : c.Kind == Kind.Tonight ? p.World.Gold : float.NaN, p.Compare.EndGold)));
            series.Add(new GraphSeries("After building", G.Bronze, Values(c => Gold(c, d => d.AfterSpend), float.NaN)));
            series.Add(new GraphSeries("At sunrise", ModStyle.Bright, Values(c => c.Kind == Kind.Tonight ? p.World.Gold : Gold(c, d => d.Start), r.EndGold)));
            var range = V.Range(series);
            var xs = cols.Select((c, i) => ColX(i) + colW / 2f).Append(ColX(cols.Count) + EndW / 2f).ToArray();
            var shortCols = new HashSet<int>(cols.Select((c, i) => (c, i)).Where(t => t.c.Result != null && t.c.Result.Shortfall > 0).Select(t => t.i));
            V.Strip(graphPlot, contentW, graphH, xs, colW, series, range, shortCols, litColumn);
            var axis = new GameObject("Axis", typeof(RectTransform));
            axis.transform.SetParent(view, false);
            var axisRect = (RectTransform)axis.transform;
            axisRect.anchorMin = axisRect.anchorMax = axisRect.pivot = Vector2.zero;
            axisRect.sizeDelta = new Vector2(Gutter, graphH);
            V.StripAxis(axisRect, Gutter, graphH, range);
            var legend = Box(view, "Legend", Gutter + 10f, 4f, viewW, 24f);
            V.Legend(legend, series.AsEnumerable().Reverse().ToList());
        }

        // ---- Info line --------------------------------------------------------------------------------------------

        private void Info()
        {
            info = Words(root, "", 0f, size.y - InfoH + 6f, size.x, 28f, 23f, ModStyle.Gold);
            hints = Words(root, "", 0f, size.y - InfoH + 32f, size.x, 24f, 19f, ModStyle.Dim, font: ModStyle.ButtonFont);
        }

        private string Hints(string what) => ModNumber.UsingController ? what switch
        {
            "chip" => "A: edit · LB/RB: move a day · B: back",
            "day" => "A: add, branch or clear this day",
            "add" => "A: open the map to pick",
            _ => "A: open",
        } : what switch
        {
            "chip" => $"Enter: edit · {KeyName(m.EarlierKey)}/{KeyName(m.LaterKey)}: move a day · {KeyName(m.RemoveKey)}: remove · {KeyName(m.UndoKey)}: undo · drag to another day · right-click: remove",
            "day" => $"Enter: add, branch or clear this day · {KeyName(m.UndoKey)}: undo",
            "add" => "Enter: open the map to pick buildings",
            _ => $"Enter: open · {KeyName(m.UndoKey)}: undo",
        };

        private static string KeyName(Keybind k) => k == null || k.Shortcut.MainKey == KeyCode.None ? "-" : k.Shortcut.ToString();

        private void Shown(ModTile tile)
        {
            if (tile == null || info == null) return;
            if (tile.Key != null && dragging == null) Focus = tile.Key;
            if (columnOf.TryGetValue(tile, out var col))
            {
                int i = cols.IndexOf(col);
                // Scroll to what the keys or the controller select, never to what the mouse hovers: the board would
                // slide under the pointer, and the click would land on whatever slid there.
                if (!HoveredByMouse(tile)) Reveal(i, tile);
                if (i != litColumn && dragging == null) { litColumn = i; Graph(); }
            }
            string text, hint;
            var w = P.World;
            if (groupOf.TryGetValue(tile, out var g))
            {
                var r = g.R;
                string title = EconomyPlannerManager.Title(r) + (g.Count > 1 ? $" <color=#{EconomyPlannerManager.DimHex}>and {g.Count - 1} more {r.Slot?.TypeName}</color>" : "");
                string detail = EconomyPlannerManager.Detail(r);
                if (g.Count > 1 && r.Counts && r.Action.IsStep) detail = $"{EconomyPlannerManager.Coin(g.Members.Sum(a => P.Result.Actions.FirstOrDefault(x => x.Action == a)?.Cost ?? 0))} for all {g.Count}";
                if (g.Inherited) detail += $" · from {P.File.FullName(g.Owner)}";
                if (P.IsFixed(g.First)) detail = $"{EconomyPlannerManager.Coin(r.Cost)} · part of every plan";
                text = $"{title}  <color=#{(r.Status == ActionStatus.Short ? EconomyPlannerManager.RedHex : EconomyPlannerManager.DimHex)}>{detail}</color>";
                hint = P.IsFixed(g.First) ? EconomyPlannerManager.FixedNote : Hints("chip");
            }
            else if (tile.Key.StartsWith("day:"))
            {
                text = col == null ? "" : DayText(col);
                hint = Hints("day");
            }
            else if (tile.Key.StartsWith("add:") || tile.Key.StartsWith("lost:"))
            {
                bool lost = tile.Key.StartsWith("lost:");
                text = lost ? $"Mark buildings you expect to lose on night {col?.Day}: they pay nothing at the next dawn." : $"Build or upgrade on day {col?.Day}: pick buildings on the map.";
                hint = Hints("add");
            }
            else
            {
                text = tile.Key switch
                {
                    "tool:plan" => $"Showing {P.PlanName}. Switch plans, make one, rename, copy or delete it.",
                    "tool:compare" => $"The dim line and the days' +/- compare this plan with {P.OtherName}.",
                    "tool:perks" => "Try this plan with other perks: the gold perks as the loadout screen shows them.",
                    "tool:sources" => "What each kind of income building costs, pays and when it pays for itself.",
                    "tool:undo" => m.CanUndo ? "Undo the last change to your plans." : "Nothing to undo yet.",
                    "tool:short" => "A change left a later day without the gold for its builds. Enter: move them to a day that can pay, or undo.",
                    _ => "",
                };
                hint = Hints("tool");
            }
            info.text = text;
            hints.text = hint;
        }

        private bool HoveredByMouse(ModTile tile)
        {
            var frame = host?.Frame;
            return frame != null && !frame.MouseSleeping && tile.PointerOver();
        }

        private string DayText(Column c)
        {
            var w = P.World;
            if (c.Kind == Kind.Done) return "Steps from days that are over: done, or missed. Enter to clear them out.";
            if (c.Kind == Kind.Tonight)
            {
                var r = P.Result;
                return $"Tonight (night {c.Day}): {EconomyPlannerManager.Coin(w.Gold)} now, {EconomyPlannerManager.Signed(r.PendingDrops + r.PendingBuildings + r.PendingTreasure + r.PendingExtra)} by dawn " +
                    $"<color=#{EconomyPlannerManager.DimHex}>(enemies {r.PendingDrops}, buildings {r.PendingBuildings}{(r.PendingTreasure > 0 ? $", treasure {r.PendingTreasure}" : "")})</color>";
            }
            var d = c.Result;
            if (d == null) return $"Day {c.Day}";
            var parts = new List<string>();
            if (d.Drops > 0) parts.Add($"enemies {d.Drops}");
            if (d.Buildings + d.Harvest > 0) parts.Add($"buildings {d.Buildings + d.Harvest}");
            if (d.Interest > 0) parts.Add($"interest {d.Interest}");
            if (d.Extra > 0) parts.Add($"other {d.Extra}");
            if (d.Treasure > 0) parts.Add($"treasure {d.Treasure}");
            string spent = d.Spent == 0 ? "" : d.Shortfall > 0
                ? $", spends {d.Spent} <color=#{EconomyPlannerManager.RedHex}>({d.Shortfall} short)</color>"
                : $", spends {d.Spent}";
            string vs = c.Compare != null ? $" <color=#{EconomyPlannerManager.DimHex}>({EconomyPlannerManager.Signed(d.AtDawn - c.Compare.AtDawn)} vs {P.OtherName})</color>" : "";
            return $"Day {c.Day}: {EconomyPlannerManager.Coin(d.Start)} at sunrise{spent}; night {c.Day} brings {d.Income} <color=#{EconomyPlannerManager.DimHex}>({string.Join(", ", parts)})</color>, {EconomyPlannerManager.Coin(d.AtDawn)} at dawn{vs}";
        }

        // ---- Navigation -------------------------------------------------------------------------------------------

        private void Wire()
        {
            if (cols.Count == 0 && toolbar.Count == 0) return;
            var columns = cols.Select(c => (IReadOnlyList<ThronefallUIElement>)new ThronefallUIElement[] { c.Header }.Concat(c.Elements.OrderBy(e => -e.Rect.anchoredPosition.y)).ToList()).ToList();
            ModNav.Columns(columns, null, below);
            ModNav.Grid([toolbar], above, cols.Count > 0 ? cols[0].Header : below);
            // Up from a header goes to the toolbar button above it; down from the toolbar to the column below it.
            foreach (var c in cols)
            {
                var center = c.Header.Rect.anchoredPosition.x + colW / 2f + Gutter - scrollX;
                c.Header.topNav = toolbar.OrderBy(t => Mathf.Abs(t.Rect.anchoredPosition.x + t.Rect.sizeDelta.x / 2f - center)).First();
            }
            foreach (var t in toolbar)
            {
                float center = t.Rect.anchoredPosition.x + t.Rect.sizeDelta.x / 2f - Gutter + scrollX;
                int i = Mathf.Clamp(Mathf.FloorToInt(center / colW), 0, cols.Count - 1);
                if (cols.Count > 0) t.botNav = cols[i].Header;
            }
            if (above != null && toolbar.Count > 0) above.botNav = toolbar[0];
            if (below != null && cols.Count > 0) below.topNav = cols[Mathf.Clamp(Mathf.FloorToInt(scrollX / colW), 0, cols.Count - 1)].Elements.LastOrDefault() ?? cols[0].Header;
        }

        private void Restore()
        {
            var frame = host?.Frame;
            if (frame == null) return;
            ModTile target = null;
            if (Focus != null) Tiles.TryGetValue(Focus, out target);
            if (target == null)
            {
                var current = frame.CurrentSelection;
                // The selection is elsewhere (tabs, Back): leave it.
                if (current != null && !(current is ModTile)) return;
                target = cols.FirstOrDefault(c => c.Kind == Kind.Day)?.Header ?? toolbar.FirstOrDefault();
            }
            if (target != null && frame.CurrentSelection != target) frame.Select(target);
            else if (target != null) Shown(target);
        }

        /// <summary>Selects a board element by key (dev commands).</summary>
        internal bool Select(string key)
        {
            if (!Tiles.TryGetValue(key, out var tile)) return false;
            host?.Frame?.Select(tile);
            return true;
        }

        internal ModTile Selected => host?.Frame?.CurrentSelection as ModTile is { } t && Tiles.ContainsValue(t) ? t : null;

        internal PlanGroup GroupOf(ModTile tile) => tile != null && groupOf.TryGetValue(tile, out var g) ? g : null;

        internal string Describe()
        {
            int visible = Mathf.Max(1, Mathf.FloorToInt(viewW / colW));
            return $"{cols.Count} columns ({colW:0} px, {visible} fit, scroll {scrollX:0}/{Mathf.Max(0f, contentW - viewW):0}), " +
                $"{groupOf.Count} chips for {P?.Actions.Count} actions, body {bodyH:0}/{bodyViewH:0} px (scroll {scrollY:0}), graph {graphH:0} px, " +
                $"selected {Selected?.Key ?? "-"}\n  info: {info?.text}\n  " +
                string.Join("\n  ", cols.Select(c => $"{(c.Kind == Kind.Day ? $"day {c.Day}" : c.Kind.ToString())}: " +
                    string.Join(", ", c.DayGroups.Select(g => $"{g.Key}={EconomyPlannerManager.Title(g.R)}{(g.Count > 1 ? $" x{g.Count}" : "")}")) +
                    (c.NightGroups.Count > 0 ? " | night: " + string.Join(", ", c.NightGroups.Select(g => $"{g.Key}={EconomyPlannerManager.Title(g.R)}{(g.Count > 1 ? $" x{g.Count}" : "")}")) : "")));
        }

        // ---- Scrolling and keys -----------------------------------------------------------------------------------

        private void Reveal(int i, ModTile tile)
        {
            if (i < 0) return;
            float left = ColX(i), right = left + colW;
            if (left < scrollX) scrollX = left;
            else if (right > scrollX + viewW - (i == cols.Count - 1 ? EndW : 0f)) scrollX = Mathf.Min(right - viewW + (i == cols.Count - 1 ? EndW : 0f), contentW - viewW);
            if (tile != null && tile.transform.parent == bodyLayer)
            {
                float top = -tile.Rect.anchoredPosition.y, bottom = top + tile.Rect.sizeDelta.y;
                if (top - 6f < scrollY) scrollY = Mathf.Max(0f, top - 6f);
                else if (bottom + 6f > scrollY + bodyViewH) scrollY = bottom + 6f - bodyViewH;
            }
            scrollX = Mathf.Clamp(scrollX, 0f, Mathf.Max(0f, contentW - viewW));
            scrollY = Mathf.Clamp(scrollY, 0f, Mathf.Max(0f, bodyH - bodyViewH));
        }

        private float shownX = -1f, shownY = -1f;

        private void Apply(bool instant)
        {
            if (headerLayer == null) return;
            float x = instant || shownX < 0f ? scrollX : Mathf.Lerp(shownX, scrollX, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f));
            float y = instant || shownY < 0f ? scrollY : Mathf.Lerp(shownY, scrollY, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f));
            if (Mathf.Abs(x - scrollX) < 0.5f) x = scrollX;
            if (Mathf.Abs(y - scrollY) < 0.5f) y = scrollY;
            shownX = x;
            shownY = y;
            headerLayer.anchoredPosition = new Vector2(-x, 0f);
            bodyLayer.anchoredPosition = new Vector2(-x, y);
            if (graphLayer != null) graphLayer.anchoredPosition = new Vector2(-x, 0f);
            if (gutterLayer != null) gutterLayer.anchoredPosition = new Vector2(0f, y);
            m.BoardScrollX = scrollX;
            m.BoardScrollY = scrollY;
            int hiddenLeft = Mathf.FloorToInt((x + 1f) / colW);
            int hiddenRight = Mathf.Max(0, cols.Count - Mathf.FloorToInt((x + viewW - EndW + 1f) / colW));
            if (leftMore != null) leftMore.text = hiddenLeft > 0 ? $"‹ {hiddenLeft}" : "";
            if (rightMore != null) rightMore.text = hiddenRight > 0 ? $"{hiddenRight} more ›" : "";
        }

        private void Tick()
        {
            if (P == null || headerLayer == null) return;
            Apply(false);
            var frame = host?.Frame;
            if (frame == null || UIFrameManager.instance == null || UIFrameManager.instance.ActiveFrame != frame || ModTextInput.BlocksInput) return;
            try
            {
                float wheel = ScrollInput.Pixels;
                if (wheel != 0f && bodyMask != null && Over(bodyMask))
                {
                    // Sideways when days are hidden (Shift: up and down), else up and down.
                    if (contentW > viewW && !(ModNumber.ShiftHeld && bodyH > bodyViewH)) scrollX = Mathf.Clamp(scrollX - wheel, 0f, contentW - viewW);
                    else scrollY = Mathf.Clamp(scrollY - wheel, 0f, Mathf.Max(0f, bodyH - bodyViewH));
                }
                if (m.KeyUndo) { m.Undo(); return; }
                var g = GroupOf(Selected);
                if (g == null) return;
                if (m.KeyEarlier) m.MoveGroup(P, g.Members, null, -1);
                else if (m.KeyLater) m.MoveGroup(P, g.Members, null, 1);
                else if (m.KeyRemove) m.RemoveGroup(P, g.Members);
            }
            catch (Exception e) { Log.For<EconomyPlannerManager>().Caught("Planner board key failed", e); }
        }

        private static bool Over(RectTransform rect) => RectTransformUtility.RectangleContainsScreenPoint(rect, ModTile.MousePosition, null);

        private class Driver : MonoBehaviour
        {
            public PlannerBoard Board;
            private void Update() => Board?.Tick();
        }
    }
}
