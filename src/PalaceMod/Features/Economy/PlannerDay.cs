using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PalaceMod.Economy;
using Object = UnityEngine.Object;
using G = PalaceMod.AnalyticsGraph;
using V = PalaceMod.Economy.PlannerVisuals;

/*
    One day of the plan (a day's header on the board, or "+ lost"): a picture of the level from above with a marker on
    every build slot, the kinds of building that day can take, the day's steps, and the gold curve.

    It edits the plan directly (every change can be undone): clicking a marker takes that slot one step further that day
    (castle 1 -> 2 -> 3, or build a house and upgrade it), and when it can't go further, clicking takes its steps on
    that day out again; right-click takes one step off. The day's steps list does the same with − / +, and a kind's tile
    adds the nearest one of that kind. Coming back to a day shows its steps on the map, lit, to change or drop.

    Two layers on the same map: Build (that day's builds and upgrades) and Lost that night (buildings expected to be
    destroyed; with a Temple wisdom prayer that day, what they drop). The shoulders (or ‹ ›) change the day.
*/
namespace PalaceMod
{
    internal enum PlannerDayLayer { Build, Lost }

    public partial class EconomyPlannerManager
    {
        private ModFrame dayFrame;
        private PlannerDayView dayView;
        internal int ViewDay;
        internal PlannerDayLayer ViewLayer;
        /// <summary>The plan's result when the day view opened: its graph's "Your plan".</summary>
        internal EconResult ViewEntry;

        internal void OpenDay(int day, PlannerDayLayer layer = PlannerDayLayer.Build)
        {
            var p = Project();
            if (p == null) return;
            // A popup over the board (quick add, a chip) goes first: one layer at a time.
            if (ModPopup.Current != null && ModPopup.Current.IsOpen) ModPopup.Current.Close();
            ViewDay = day;
            ViewLayer = layer;
            if (dayFrame.IsOpen) { dayView?.Render(); return; }
            ViewEntry = p.Result;
            dayFrame.Open();
        }

        private void BuildDayPage(ModPage page)
        {
            dayView = null;
            if (Project() == null)
            {
                page.Text("The economy planner works during a level.");
                return;
            }
            var area = page.Custom("Day", page.Size.y);
            dayView = new PlannerDayView(this, page.Frame ?? dayFrame, area, page.Size);
            page.Wired += (above, below) => dayView?.Connect(below);
            dayView.Render();
        }

        // ---- A day's state, and the edits the day view and quick add make --------------------------------------------

        internal enum Mark { Open, Poor, Locked, Max }

        /// <summary>A day of the plan: the world before and after its steps, and its gold.</summary>
        internal sealed class DayState
        {
            public Projection P;
            public int Day;
            /// <summary>After the plan's steps that day.</summary>
            public EconWorld After;
            /// <summary>Gold at sunrise, and what's left after the day's steps.</summary>
            public int Budget, Left;
            /// <summary>The plan's steps that day, in the order they're done.</summary>
            public List<EconAction> Steps;
            public DayResult Result;

            public List<EconAction> StepsOn(EconSlot e) => Steps.Where(a => a.Slot == e.Id).OrderBy(a => a.Level).ToList();
        }

        internal DayState DayOf(Projection p, int day)
        {
            var w = p.World;
            day = Mathf.Clamp(day, w.FirstDay, Math.Max(w.FirstDay, w.LastNight));
            var d = new DayState { P = p, Day = day, Result = p.Result.Days.FirstOrDefault(x => x.Day == day) };
            d.Steps = EconSim.Ordered(p.Actions).Where(a => a.IsStep && a.Day == day).ToList();
            d.After = EconSim.StateOn(w, p.Actions, day);
            d.Budget = d.Result?.Start ?? w.Gold;
            d.Left = d.Result?.AfterSpend ?? w.Gold;
            return d;
        }

        /// <summary>What a slot can do that day after the plan's steps: take a step (and whether the gold's there), or why not.</summary>
        internal static Mark StatusOf(DayState d, EconSlot e, out string why, out int cost)
        {
            var s = d.After.Slots[e.Index];
            cost = 0;
            why = null;
            if (s.Level >= s.MaxLevel)
            {
                why = s.Level == 0 ? "Not in this mode" : $"Level {s.Level}, its highest";
                return Mark.Max;
            }
            if (EconSim.Blocker(d.After, s) is { } blocked)
            {
                why = blocked;
                return Mark.Locked;
            }
            cost = s.Steps[s.Level].Cost;
            if (cost > d.Left)
            {
                why = $"Costs {Coin(cost)}, {Coin(Math.Max(0, d.Left))} left";
                return Mark.Poor;
            }
            return Mark.Open;
        }

        /// <summary>Takes the slot one step further that day. False (with a notice) if it can't.</summary>
        internal bool AddStep(DayState d, EconSlot e)
        {
            var mark = StatusOf(d, e, out string why, out _);
            if (mark != Mark.Open)
            {
                Notify.Show($"{e.Label}: {Plain(why)}", NotifyKind.Info, 2.5f, key: "econ-day");
                return false;
            }
            var s = d.After.Slots[e.Index];
            var step = s.Steps[s.Level];
            string choice = step.IsChoice ? step.Branches[EconSim.BranchIndex(step, null)].Key : null;
            var a = new EconAction { Day = d.Day, Slot = e.Id, Level = s.Level + 1, Choice = choice };
            Edit(() => d.P.Plan.Actions.Add(a), a.Id);
            return true;
        }

        /// <summary>Takes the slot's last step that day off (all of them with <paramref name="all"/>).</summary>
        internal bool RemoveStep(DayState d, EconSlot e, bool all = false)
        {
            var mine = d.StepsOn(e).Where(a => !d.P.IsFixed(a)).ToList();
            if (mine.Count == 0)
            {
                if (d.StepsOn(e).Any(d.P.IsFixed)) Notify.Show(FixedNote, NotifyKind.Info, 3f, key: "econ-move");
                return false;
            }
            var gone = all ? mine : [mine[mine.Count - 1]];
            Edit(() => { foreach (var a in gone) Holder(d.P, a).Actions.Remove(a); });
            return true;
        }

        /// <summary>A click on a slot: one step further, or (when it can't go further) its steps that day off again.</summary>
        internal void Cycle(DayState d, EconSlot e)
        {
            var mark = StatusOf(d, e, out string why, out _);
            if (mark == Mark.Open) AddStep(d, e);
            else if (mark != Mark.Poor && d.StepsOn(e).Any(a => !d.P.IsFixed(a))) RemoveStep(d, e, all: true);
            else Notify.Show($"{e.Label}: {Plain(why)}", NotifyKind.Info, 2.5f, key: "econ-day");
        }

        /// <summary>A kind of step on a day: a building type and the level it goes to.</summary>
        internal sealed class Kind
        {
            public string Key, Type;
            public int Level;
            public List<EconSlot> Open = [], Poor = [];
            public int Cost, Planned;
            public EconSlot Sample;
            public string Name => Level <= 1 ? PlannerIcons.Short(Type) : $"{PlannerIcons.Short(Type)} → {PlannerIcons.Roman(Level)}";
        }

        /// <summary>Every kind of step the day can still take (or could, with more gold), and the kinds it has.</summary>
        internal static List<Kind> KindsOf(DayState d)
        {
            var kinds = new Dictionary<string, Kind>();
            Kind Get(string type, int level, EconSlot sample)
            {
                string key = $"{type}|{level}";
                if (!kinds.TryGetValue(key, out var k)) kinds[key] = k = new Kind { Key = key, Type = type, Level = level, Sample = sample };
                return k;
            }
            foreach (var e in d.After.Slots.Where(x => !x.IsDependent && x.Steps.Count > 0))
            {
                var mark = StatusOf(d, e, out _, out int cost);
                if (mark is not (Mark.Open or Mark.Poor)) continue;
                var k = Get(e.TypeName, e.Level + 1, e);
                (mark == Mark.Open ? k.Open : k.Poor).Add(e);
                k.Cost = k.Cost == 0 ? cost : Math.Min(k.Cost, cost);
            }
            foreach (var a in d.Steps.Where(a => !d.P.IsFixed(a)))
            {
                var e = d.After.Find(a.Slot);
                if (e == null || e.IsDependent) continue;
                Get(e.TypeName, a.Level, e).Planned++;
            }
            var w = d.P.World;
            return kinds.Values.OrderBy(k => w.Slots[k.Sample.Index].EverPays || w.Slots.Any(x => x.Activator == k.Sample.Index && x.EverPays) ? 0 : 1)
                .ThenBy(k => k.Type).ThenBy(k => k.Level).ToList();
        }

        /// <summary>One more of a kind that day: the nearest free one (a picked mill's fields first, around it).</summary>
        internal bool AddKind(DayState d, Kind k)
        {
            if (k.Open.Count == 0)
            {
                string why = k.Poor.Count > 0 ? $"Costs {Coin(k.Cost)}, {Coin(Math.Max(0, d.Left))} left" : "None left that day";
                Notify.Show($"{k.Name}: {Plain(why)}", NotifyKind.Info, 2.5f, key: "econ-day");
                return false;
            }
            var owners = d.Steps.Select(a => d.After.Find(a.Slot)?.Index ?? -1).ToHashSet();
            var next = k.Open.OrderBy(e => owners.Contains(e.Activator) ? 0 : 1).ThenBy(e =>
            {
                var o = e.Activator >= 0 && owners.Contains(e.Activator) ? d.After.Slots[e.Activator] : null;
                return o != null ? Sq(e.MapX - o.MapX) + Sq(e.MapY - o.MapY) : Sq(e.MapX) + Sq(e.MapY);
            }).First();
            return AddStep(d, next);
        }

        /// <summary>One fewer of a kind that day: its last step of that kind.</summary>
        internal bool RemoveKind(DayState d, Kind k)
        {
            var last = d.Steps.LastOrDefault(a => !d.P.IsFixed(a) && a.Level == k.Level && d.After.Find(a.Slot)?.TypeName == k.Type);
            if (last == null) return false;
            Edit(() => Holder(d.P, last).Actions.Remove(last));
            return true;
        }

        private static float Sq(float v) => v * v;

        /// <summary>Marks a building lost on a night, or not.</summary>
        internal void ToggleLost(Projection p, int night, EconSlot e)
        {
            var existing = p.Actions.Where(a => a.Kind == EconActionKind.KnockOut && a.Day == night && a.Slot == e.Id).ToList();
            if (existing.Count > 0) Edit(() => { foreach (var a in existing) Holder(p, a).Actions.Remove(a); });
            else
            {
                var a = new EconAction { Kind = EconActionKind.KnockOut, Day = night, Slot = e.Id };
                Edit(() => p.Plan.Actions.Add(a), a.Id);
            }
        }

        internal static string Plain(string rich) => System.Text.RegularExpressions.Regex.Replace(rich ?? "", "<[^>]*>", "");
    }

    internal sealed class PlannerDayView
    {
        private const float TopH = 44f, Gap = 12f, Pad = 24f, InfoH = 78f, KindH = 50f, RowH = 38f, ChoiceTile = 40f, LegendH = 26f;

        private readonly EconomyPlannerManager m;
        private readonly ModFrame frame;
        private readonly RectTransform area;
        private readonly Vector2 size;
        private ThronefallUIElement below;
        private RectTransform root, mapHolder, panel, strip;
        private ModMap map;
        private EconomyPlannerManager.Projection P;
        private EconomyPlannerManager.DayState D;
        private TextMeshProUGUI info;
        internal readonly Dictionary<string, ModTile> Tiles = [];
        private readonly List<ModTile> top = [];
        private readonly List<List<ThronefallUIElement>> panelRows = [];
        private ModPage wisdomPage;
        private string focus, confirm;
        private float kindsScroll, listScroll;
        private ModScrollArea kindsArea, listArea;
        private float W => size.x - Pad;

        public PlannerDayView(EconomyPlannerManager m, ModFrame frame, RectTransform area, Vector2 size)
        {
            this.m = m;
            this.frame = frame;
            this.area = area;
            this.size = size;
        }

        private int Day => m.ViewDay;
        private PlannerDayLayer Layer => m.ViewLayer;

        public void Connect(ThronefallUIElement below)
        {
            this.below = below;
            Wire();
        }

        // ---- Build --------------------------------------------------------------------------------------------------

        public void Render()
        {
            if (area == null) return;
            var keep = frame?.Frame?.CurrentSelection as ModTile;
            if (keep != null && Tiles.ContainsValue(keep)) focus ??= keep.Key;
            if (kindsArea != null) kindsScroll = kindsArea.ScrollY;
            if (listArea != null) listScroll = listArea.ScrollY;
            Tiles.Clear();
            top.Clear();
            panelRows.Clear();
            wisdomPage = null;
            P = m.Project();
            if (P == null) return;
            var w = P.World;
            var (first, last) = EconomyPlannerManager.DayRange(w, Layer == PlannerDayLayer.Build ? EconActionKind.Step : EconActionKind.KnockOut);
            m.ViewDay = Mathf.Clamp(Day, first, Math.Max(first, last));
            D = m.DayOf(P, Math.Max(Day, w.FirstDay));
            frame?.SetTitle($"Day {Day}");

            if (root == null)
            {
                var obj = new GameObject("Day", typeof(RectTransform));
                obj.transform.SetParent(area, false);
                root = (RectTransform)obj.transform;
                root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
                root.sizeDelta = size;
                obj.AddComponent<Driver>().View = this;
                var backdrop = V.Panel(root, "Backdrop", size.x + 40f, size.y + 24f, 0.9f);
                backdrop.anchoredPosition = new Vector2(-20f, 12f);
            }
            foreach (var old in new[] { panel, strip }) if (old != null) Object.DestroyImmediate(old.gameObject);
            foreach (Transform child in root.Cast<Transform>().Where(t => t.name == "Top").ToList()) Object.DestroyImmediate(child.gameObject);

            float mapW = Mathf.Round(W * 0.6f), panelX = mapW + 24f;
            float stripH = size.y >= 620f ? 150f : 120f;
            float mapH = size.y - TopH - Gap - LegendH - stripH - Gap;
            Top();
            Map(new Vector2(mapW, mapH));
            Legend(mapW, TopH + Gap + mapH);
            Strip(mapW, TopH + Gap + mapH + LegendH, stripH);
            Panel(panelX, W - panelX);
            Wire();
            frame?.Frame?.RefetchManagedElements();
            Restore();
        }

        private RectTransform Box(Transform parent, string name, float x, float y, float w, float h)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        private ModTile Tile(Transform parent, string key, float x, float y, Vector2 tileSize, Sprite icon, string text, float font = 19f)
        {
            var t = ModTile.Create(parent, key, tileSize, icon, text, font).Place(new Vector2(x, -y));
            t.Key = key;
            Tiles[key] = t;
            return t;
        }

        private static TextMeshProUGUI Words(Transform parent, string text, float x, float y, float w, float h, float fontSize, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.TopLeft, TMP_FontAsset font = null, bool wrap = false)
        {
            var tmp = GameWidgets.Text(parent, "Text", text, font ?? ModStyle.BodyFont, fontSize, color, align);
            tmp.enableWordWrapping = wrap;
            tmp.overflowMode = wrap ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;
            tmp.characterSpacing = 0f;
            tmp.raycastTarget = false;
            var rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return tmp;
        }

        private static string Dim => EconomyPlannerManager.DimHex;

        private void Top()
        {
            var bar = Box(root, "Top", 0f, 0f, W, TopH);
            float x = 0f;
            var (firstDay, lastDay) = EconomyPlannerManager.DayRange(P.World, Layer == PlannerDayLayer.Build ? EconActionKind.Step : EconActionKind.KnockOut);
            var prev = Tile(bar, "day:prev", x, 0f, new Vector2(104f, TopH - 4f), null, Day > firstDay ? $"‹ Day {Day - 1}" : "‹", 18f);
            prev.Label.alignment = TextAlignmentOptions.Center;
            prev.SetDim(Day <= firstDay, 0.4f);
            prev.Applied += () => ChangeDay(-1);
            top.Add(prev);
            x += 110f;
            var next = Tile(bar, "day:next", x, 0f, new Vector2(104f, TopH - 4f), null, Day < lastDay ? $"Day {Day + 1} ›" : "›", 18f);
            next.Label.alignment = TextAlignmentOptions.Center;
            next.SetDim(Day >= lastDay, 0.4f);
            next.Applied += () => ChangeDay(1);
            top.Add(next);
            x += 126f;
            void LayerTile(string key, string text, PlannerDayLayer layer, float width)
            {
                var t = Tile(bar, key, x, 0f, new Vector2(width, TopH - 4f), null, text, 19f);
                t.Label.alignment = TextAlignmentOptions.Center;
                t.SetOn(Layer == layer);
                t.Applied += () =>
                {
                    if (m.ViewLayer == layer) return;
                    m.ViewLayer = layer;
                    focus = key;
                    Render();
                };
                t.Shown += _ => Say(null);
                top.Add(t);
                x += width + 8f;
            }
            LayerTile("layer:build", "Build", PlannerDayLayer.Build, 120f);
            int lost = P.Actions.Count(a => a.Kind == EconActionKind.KnockOut && a.Day == Day);
            LayerTile("layer:lost", lost > 0 ? $"Lost that night · {lost}" : "Lost that night", PlannerDayLayer.Lost, 220f);
            x += 16f;
            // The day's gold: left after its steps, of what it has at sunrise.
            string left = D.Left < 0 ? $"<color=#{EconomyPlannerManager.RedHex}>{D.Left}</color>" : D.Left.ToString("N0");
            var budget = Words(bar, $"{left}<color=#{Dim}>/</color>{EconomyPlannerManager.Coin(D.Budget)}", x, 0f, 200f, TopH - 4f, 26f, ModStyle.Bright,
                TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);
            budget.raycastTarget = true;
            ModTooltip.AttachHover(budget.gameObject, null, () => $"Left after day {Day}'s steps / gold at sunrise");

            // Branch and Clear, at the right.
            float rx = W;
            ModTile Right(string key, string text, float width, Action act)
            {
                rx -= width;
                var t = Tile(bar, key, rx, 0f, new Vector2(width, TopH - 4f), null, text, 18f);
                t.Label.alignment = TextAlignmentOptions.Center;
                t.Applied += act;
                top.Add(t);
                rx -= 8f;
                return t;
            }
            var own = P.Plan.Actions.Where(a => a.Day == Day && !P.IsFixed(a)).ToList();
            if (own.Count > 0)
            {
                var clear = Right("clear", confirm == "clear" ? "Press again" : $"Clear day · {own.Count}", 170f, () =>
                {
                    focus = "clear";
                    if (confirm != "clear") { confirm = "clear"; Render(); return; }
                    confirm = null;
                    m.Edit(() => P.Plan.Actions.RemoveAll(own.Contains));
                });
                if (confirm == "clear") clear.SetWarning(true);
                clear.Shown += _ => Say($"Takes day {Day}'s {own.Count} steps and losses out of {P.PlanName}.");
            }
            if (Day > w0FirstDay)
            {
                var branch = Right("branch", "Branch here", 150f, () =>
                {
                    focus = "branch";
                    m.Edit(() => m.Branch(P.File, P.Plan, Day));
                    Notify.Show($"Branched from day {Day}", NotifyKind.Info, 2.5f, key: "econ-branch");
                });
                branch.Shown += _ => Say($"A new plan that shares {P.PlanName}'s steps before day {Day}, compared with it.");
            }
            // The top bar's tiles left to right.
            top.Sort((a, b) => a.Rect.anchoredPosition.x.CompareTo(b.Rect.anchoredPosition.x));
        }

        private int w0FirstDay => P.World.FirstDay;

        internal void ChangeDay(int delta)
        {
            var (first, last) = EconomyPlannerManager.DayRange(P.World, Layer == PlannerDayLayer.Build ? EconActionKind.Step : EconActionKind.KnockOut);
            int day = Mathf.Clamp(Day + delta, first, Math.Max(first, last));
            if (day == Day) return;
            m.ViewDay = day;
            confirm = null;
            focus = focus != null && focus.StartsWith("marker:") ? focus : null;
            Render();
        }

        // ---- Map ----------------------------------------------------------------------------------------------------

        private List<EconSlot> Shown() => D.After.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0).ToList();

        private void Map(Vector2 mapSize)
        {
            var snap = m.Snap();
            Vector3 Pos(int i) => snap.Objects.TryGetValue(i, out var o) && o != null ? o.transform.position : Vector3.zero;
            if (map == null || map.Root == null)
            {
                mapHolder = Box(root, "Map Holder", 0f, TopH + Gap, mapSize.x, mapSize.y);
                var all = D.After.Slots.Where(e => e.Steps.Count > 0 && snap.Objects.ContainsKey(e.Index)).ToList();
                map = ModMap.Create(mapHolder, mapSize, all.Select(e => Pos(e.Index)));
            }
            map.ClearMarkers();
            foreach (Transform child in map.Overlay.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            if (Layer == PlannerDayLayer.Lost) { LostMarkers(snap, Pos); return; }

            var slots = Shown().Where(e => snap.Objects.ContainsKey(e.Index))
                .Select(e => (e, mark: EconomyPlannerManager.StatusOf(D, e, out string why, out int cost), why, cost, steps: D.StepsOn(e)))
                // Locked ones under the rest, nearest the castle on top.
                .OrderBy(t => t.steps.Count > 0 ? 3 : t.mark == EconomyPlannerManager.Mark.Open ? 2 : t.mark == EconomyPlannerManager.Mark.Poor ? 1 : 0)
                .ThenByDescending(t => t.e.MapX * t.e.MapX + t.e.MapY * t.e.MapY).ToList();
            foreach (var (e, mark, why, cost, steps) in slots)
            {
                var slot = e;
                bool locked = mark is EconomyPlannerManager.Mark.Locked or EconomyPlannerManager.Mark.Max;
                bool today = steps.Count > 0;
                var marker = map.AddMarker("marker:" + e.Index, Pos(e.Index), locked && !today ? 24f : 30f, PlannerIcons.Kind(e.TypeName));
                Tiles[marker.Key] = marker;
                marker.Badge.fontSize = 13f;
                if (today)
                {
                    // Lit, with the level it reaches that day.
                    marker.SetOn(true);
                    marker.SetBadge(steps.Max(a => a.Level) <= 1 ? "new" : "→" + PlannerIcons.Roman(steps.Max(a => a.Level)), ModStyle.Bright);
                }
                else if (mark == EconomyPlannerManager.Mark.Poor)
                {
                    // Unlocked, but the gold isn't there: full colour, its cost in red.
                    marker.SetBadge(cost.ToString(), V.Red);
                }
                else if (e.Level > 0) marker.SetBadge(PlannerIcons.Roman(e.Level), ModStyle.Gold);
                if (locked && !today)
                {
                    // Not unlocked (or out of reach, or as high as it goes): dark, with a padlock.
                    marker.SetFill(new Color(0.08f, 0.085f, 0.09f, 1f));
                    marker.Icon.color = G.Fade(ModStyle.Dim, 0.9f);
                    if (mark == EconomyPlannerManager.Mark.Locked) marker.SetCornerIcon(PlannerIcons.Lock, ModStyle.Dim);
                    marker.SetDim(true, 0.75f);
                }
                marker.Applied += () => { focus = "marker:" + slot.Index; m.Cycle(D, slot); };
                marker.RightClicked += () => { focus = "marker:" + slot.Index; m.RemoveStep(D, slot); };
                marker.Shown += _ => ShowSlot(slot);
            }
            if (slots.All(t => t.mark != EconomyPlannerManager.Mark.Open && t.steps.Count == 0))
                Note(slots.Any(t => t.mark == EconomyPlannerManager.Mark.Poor) ? $"Not enough gold for anything on day {Day}" : $"Nothing can be built or upgraded on day {Day}");
        }

        private void LostMarkers(EconSnapshot snap, Func<int, Vector3> pos)
        {
            var lost = new HashSet<string>(P.Actions.Where(a => a.Kind == EconActionKind.KnockOut && a.Day == Day).Select(a => a.Slot));
            var standing = Shown().Where(e => e.Level >= 1 && snap.Objects.ContainsKey(e.Index)).ToList();
            foreach (var e in standing.OrderByDescending(e => e.MapX * e.MapX + e.MapY * e.MapY))
            {
                var slot = e;
                bool isLost = lost.Contains(e.Id);
                var marker = map.AddMarker("marker:" + e.Index, pos(e.Index), 30f, PlannerIcons.Kind(e.TypeName));
                Tiles[marker.Key] = marker;
                marker.Badge.fontSize = 13f;
                if (isLost)
                {
                    marker.SetFill(Color.Lerp(ModStyle.Panel, V.Red, 0.6f));
                    marker.SetCornerIcon(PlannerIcons.Lost, ModStyle.Bright);
                }
                else if (e.Pays && e.Income > 0) marker.SetBadge(e.Income.ToString(), ModStyle.Gold);
                if (e.Indestructible)
                {
                    marker.SetDim(true, 0.5f);
                    marker.SetCornerIcon(PlannerIcons.Lock, ModStyle.Dim);
                }
                marker.Applied += () =>
                {
                    focus = "marker:" + slot.Index;
                    if (slot.Indestructible) { Notify.Show($"{slot.Label} can't be destroyed", NotifyKind.Info, 2f, key: "econ-day"); return; }
                    m.ToggleLost(P, Day, P.World.Slots[slot.Index]);
                };
                marker.RightClicked += () => { if (lost.Contains(slot.Id)) { focus = "marker:" + slot.Index; m.ToggleLost(P, Day, P.World.Slots[slot.Index]); } };
                marker.Shown += _ => ShowSlot(slot);
            }
            if (standing.Count == 0) Note($"Nothing is standing on night {Day}");
        }

        // A line over the map, on a dark band so it reads over the level.
        private void Note(string text)
        {
            var band = V.Panel(map.Overlay, "Note", map.Size.x, 44f, 0.92f);
            band.anchoredPosition = new Vector2(0f, 0f);
            Words(band, text, 16f, 0f, map.Size.x - 32f, 44f, 21f, ModStyle.Bright, TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);
        }

        // What the marker styles mean, under the map.
        private void Legend(float width, float y)
        {
            var row = Box(root, "Top", 0f, y, width, LegendH);
            float x = 4f;
            void Item(Sprite icon, Color iconColor, string swatch, Color swatchColor, string text)
            {
                if (icon != null)
                {
                    var img = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
                    img.transform.SetParent(row, false);
                    img.sprite = icon;
                    img.color = iconColor;
                    img.preserveAspect = true;
                    img.raycastTarget = false;
                    var r = img.rectTransform;
                    r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
                    r.anchoredPosition = new Vector2(x, -4f);
                    r.sizeDelta = new Vector2(16f, 16f);
                    x += 20f;
                }
                if (swatch != null)
                {
                    var t = Words(row, swatch, x, 0f, 40f, LegendH, 16f, swatchColor, TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);
                    x += t.GetPreferredValues(swatch).x + 5f;
                }
                var label = Words(row, text, x, 0f, 300f, LegendH, 16f, ModStyle.Dim, TextAlignmentOptions.MidlineLeft);
                x += label.GetPreferredValues(text).x + 22f;
            }
            if (Layer == PlannerDayLayer.Build)
            {
                Item(null, default, "→II", ModStyle.Bright, "planned that day");
                Item(null, default, "8", V.Red, "not enough gold");
                Item(PlannerIcons.Lock, ModStyle.Dim, null, default, "not unlocked yet");
                Words(row, "click: +1 level · right-click: −1", x, 0f, Mathf.Max(0f, width - x), LegendH, 16f, G.Fade(ModStyle.Dim, 0.8f), TextAlignmentOptions.MidlineRight);
            }
            else
            {
                Item(PlannerIcons.Lost, ModStyle.Bright, null, default, "lost that night");
                Item(null, default, "3", ModStyle.Gold, "pays at dawn");
                Item(PlannerIcons.Lock, ModStyle.Dim, null, default, "can't be destroyed");
            }
        }

        private void ShowSlot(EconSlot e)
        {
            var now = P.World.Slots[e.Index];
            string level = e.Level == 0 ? "not built" : $"level {e.Level}";
            string what;
            if (Layer == PlannerDayLayer.Lost)
            {
                bool lost = P.Actions.Any(a => a.Kind == EconActionKind.KnockOut && a.Day == Day && a.Slot == e.Id);
                what = e.Indestructible ? "Can't be destroyed" : lost ? $"Lost on night {Day}" + (e.NeverDenied ? ": still pays (its perk)" : e.Pays && e.Income > 0 ? $": -{e.Income} at dawn" : "")
                    : e.Pays && e.Income > 0 ? $"Pays {e.Income} at dawn" : "Pays nothing";
            }
            else
            {
                var steps = D.StepsOn(e);
                var mark = EconomyPlannerManager.StatusOf(D, e, out string why, out _);
                string planned = steps.Count == 0 ? "" : $"Day {Day}: " + string.Join(", ", steps.Select(a => a.Level <= 1 ? "build" : $"→{PlannerIcons.Roman(a.Level)}")) + "\n";
                what = planned + (mark == EconomyPlannerManager.Mark.Open ? "Next: " + EconomyPlannerManager.Plain(EconomyPlannerManager.NextStep(D.After, e))
                    : $"<color=#{(mark == EconomyPlannerManager.Mark.Poor ? EconomyPlannerManager.RedHex : Dim)}>{why}</color>");
                var fields = EconomyPlannerManager.Fields(D.After, e);
                if (fields.Count > 0) what += $" <color=#{Dim}>· {fields[0].TypeName}s {fields.Count(f => f.Level > 0)}/{fields.Count}</color>";
            }
            Say($"<b>{e.Label}</b>  <color=#{Dim}>{level}{(e.Level != now.Level ? $" by day {Day}" : "")}</color>\n{what}");
        }

        private void Say(string text)
        {
            if (info != null) info.text = text ?? "";
        }

        // ---- Preview ------------------------------------------------------------------------------------------------

        private void Strip(float width, float y, float height)
        {
            var days = P.Result.Days;
            strip = Box(root, "Preview", 0f, y, width, height);
            if (days.Count == 0) return;
            const float gutter = 58f;
            var back = G.Rect(strip, "Back", new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.6f));
            back.anchorMin = back.anchorMax = back.pivot = new Vector2(0f, 1f);
            back.anchoredPosition = new Vector2(gutter, 0f);
            back.sizeDelta = new Vector2(width - gutter, height);
            int n = days.Count + 1;
            float colW = (width - gutter) / n;
            var xs = Enumerable.Range(0, n).Select(i => gutter + colW * (i + 0.5f)).ToArray();
            float[] Values(EconResult r) => days.Select(d => m.GoldOf(r.Days.FirstOrDefault(x => x.Day == d.Day))).Append(r.EndGold).ToArray();
            var series = new List<GraphSeries>();
            foreach (var c in P.Compares) series.Add(new GraphSeries(EconomyPlannerManager.Capital(EconomyPlannerManager.Short(c.Name, 20)), c.Color, Values(c.Result)));
            var entry = m.ViewEntry;
            bool changed = entry != null && entry != P.Result && !Values(entry).SequenceEqual(Values(P.Result));
            if (changed)
            {
                series.Add(new GraphSeries("Your plan", G.Fade(G.Dim, 0.95f), Values(entry)));
                series.Add(new GraphSeries("With these", ModStyle.Bright, Values(P.Result)));
            }
            else series.Add(new GraphSeries("Your plan", ModStyle.Bright, Values(P.Result)));
            var range = V.Range(series);
            var plot = Box(strip, "Plot", 0f, 0f, width, height);
            plot.anchorMin = plot.anchorMax = plot.pivot = Vector2.zero;
            plot.anchoredPosition = new Vector2(0f, 0f);
            var shortCols = new HashSet<int>(days.Select((d, i) => (d, i)).Where(t => t.d.Shortfall > 0).Select(t => t.i));
            int lit = days.FindIndex(d => d.Day == Day);
            V.Strip(plot, width, height, xs, colW, series, range, shortCols, lit);
            var axis = Box(strip, "Axis", 0f, 0f, gutter, height);
            axis.anchorMin = axis.anchorMax = axis.pivot = Vector2.zero;
            axis.anchoredPosition = Vector2.zero;
            V.StripAxis(axis, gutter, height, range);
            var legend = Box(strip, "Legend", gutter + 10f, 4f, width - gutter - 10f, 24f);
            V.Legend(legend, series.AsEnumerable().Reverse().ToList());
            int every = colW < 34f ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                if (i % every != 0 && i != n - 1) continue;
                string label = i == n - 1 ? "End" : days[i].Day.ToString();
                var t = G.Label(plot, label, 15f, i == lit ? ModStyle.Bright : G.Dim, TextAlignmentOptions.Midline);
                t.rectTransform.anchoredPosition = new Vector2(xs[i] - 24f, -2f);
                t.rectTransform.sizeDelta = new Vector2(48f, 18f);
            }
        }

        // ---- Right panel --------------------------------------------------------------------------------------------

        private void Panel(float x, float width)
        {
            panel = Box(root, "Panel", x, TopH + Gap, width, size.y - TopH - Gap);
            var infoBack = V.Panel(panel, "Info", width, InfoH, 0.85f);
            infoBack.anchoredPosition = Vector2.zero;
            info = Words(panel, "", 12f, 6f, width - 24f, InfoH - 10f, 19f, ModStyle.Gold, wrap: true);
            float y = InfoH + 10f;
            if (Layer == PlannerDayLayer.Lost) { LostPanel(width, y); return; }

            // Kinds: one more of each, nearest the castle.
            var kinds = EconomyPlannerManager.KindsOf(D);
            int perRow = Mathf.Max(1, Mathf.FloorToInt((width + 8f) / (190f + 8f)));
            float kindW = (width - 12f - (perRow - 1) * 8f) / perRow;
            int kindRows = Mathf.CeilToInt(kinds.Count / (float)perRow);
            float listRoom = size.y - TopH - Gap - y;
            float kindsH = Mathf.Min(kindRows * (KindH + 8f), Mathf.Max(KindH + 8f, listRoom * 0.45f));
            kindsArea = ModScrollArea.Create(panel, new Vector2(width - 12f, kindsH), frame?.Frame).Place(new Vector2(0f, -y));
            var line = new List<ThronefallUIElement>();
            for (int i = 0; i < kinds.Count; i++)
            {
                var k = kinds[i];
                int free = k.Open.Count;
                string sub = free > 0 ? $"×{free} · {EconomyPlannerManager.Coin(k.Cost)}" : k.Poor.Count > 0 ? $"<color=#{EconomyPlannerManager.RedHex}>{EconomyPlannerManager.Coin(k.Cost)}</color>" : k.Planned > 0 ? "" : "none left";
                var t = Tile(kindsArea.Content, "kind:" + k.Key, i % perRow * (kindW + 8f), i / perRow * (KindH + 8f), new Vector2(kindW, KindH),
                    PlannerIcons.Kind(k.Type), $"{k.Name}\n<size=78%><color=#{Dim}>{sub}</color></size>", 19f);
                t.Label.enableWordWrapping = true;
                t.Label.lineSpacing = -16f;
                if (k.Planned > 0) { t.SetOn(true); t.SetBadge($"+{k.Planned}"); }
                if (free == 0) t.SetDim(true, 0.55f);
                var kind = k;
                t.Applied += () => { focus = t.Key; m.AddKind(D, kind); };
                t.RightClicked += () => { focus = t.Key; m.RemoveKind(D, kind); };
                t.Shown += _ => Say($"<b>{k.Name}</b>  <color=#{Dim}>{free} free{(k.Poor.Count > 0 ? $" · {k.Poor.Count} too dear" : "")}</color>\n" +
                    EconomyPlannerManager.Plain(EconomyPlannerManager.NextStep(D.After, k.Sample)));
                line.Add(t);
                if (line.Count == perRow || i == kinds.Count - 1) { panelRows.Add(line); line = []; }
            }
            kindsArea.SetContentHeight(kindRows * (KindH + 8f));
            kindsArea.ScrollTo(kindsScroll);
            y += kindsH + 10f;

            // The day's steps, one row per building.
            Words(panel, $"Day {Day}", 0f, y, width, 26f, 21f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);
            y += 30f;
            listArea = ModScrollArea.Create(panel, new Vector2(width - 12f, Mathf.Max(RowH, size.y - TopH - Gap - y)), frame?.Frame).Place(new Vector2(0f, -y));
            float ly = 0f;
            var bySlot = D.Steps.GroupBy(a => a.Slot).ToList();
            if (bySlot.Count == 0) Words(listArea.Content, $"<color=#{Dim}>Nothing yet</color>", 4f, 4f, width - 20f, RowH, 19f, ModStyle.Gold);
            foreach (var group in bySlot)
            {
                var e = D.After.Find(group.Key);
                if (e == null) continue;
                ly = StepRow(listArea.Content, e, group.OrderBy(a => a.Level).ToList(), width - 12f, ly);
            }
            listArea.SetContentHeight(ly);
            listArea.ScrollTo(listScroll);
        }

        // One building's steps that day: icon, name, the level it goes to and the cost, − / +; then its choices and fields.
        private float StepRow(Transform parent, EconSlot e, List<EconAction> steps, float width, float y)
        {
            bool fixedOnly = steps.All(P.IsFixed);
            int to = steps.Max(a => a.Level), from = steps.Min(a => a.Level) - 1;
            int cost = steps.Sum(a => P.Result.Actions.FirstOrDefault(r => r.Action == a)?.Cost ?? 0);
            bool skipped = steps.Any(a => P.Result.Actions.FirstOrDefault(r => r.Action == a) is { Counts: false });
            string levels = from == 0 ? (to == 1 ? "build" : $"build → {PlannerIcons.Roman(to)}") : $"{PlannerIcons.Roman(from)} → {PlannerIcons.Roman(to)}";
            var row = Tile(parent, "row:" + e.Index, 0f, y, new Vector2(width - 2f * (RowH + 6f), RowH), PlannerIcons.Kind(e.TypeName),
                $"{e.Label} <size=85%><color=#{Dim}>· {levels} · {EconomyPlannerManager.Coin(cost)}</color></size>", 18f);
            if (skipped) row.SetWarning(true);
            var slot = e;
            row.Shown += _ => ShowSlot(slot);
            row.Applied += () => { focus = "row:" + slot.Index; m.Cycle(D, slot); };
            var items = new List<ThronefallUIElement> { row };
            if (!fixedOnly)
            {
                var minus = Tile(parent, "minus:" + e.Index, width - 2f * RowH - 6f, y, new Vector2(RowH, RowH), null, "−", 24f);
                minus.Label.alignment = TextAlignmentOptions.Center;
                minus.Applied += () => { focus = "minus:" + slot.Index; m.RemoveStep(D, slot); };
                minus.Shown += _ => ShowSlot(slot);
                var plus = Tile(parent, "plus:" + e.Index, width - RowH, y, new Vector2(RowH, RowH), null, "+", 24f);
                plus.Label.alignment = TextAlignmentOptions.Center;
                plus.SetDim(EconomyPlannerManager.StatusOf(D, e, out _, out _) != EconomyPlannerManager.Mark.Open, 0.45f);
                plus.Applied += () => { focus = "plus:" + slot.Index; m.AddStep(D, slot); };
                plus.Shown += _ => ShowSlot(slot);
                items.Add(minus);
                items.Add(plus);
            }
            panelRows.Add(items);
            y += RowH + 4f;

            // Choice upgrades: the game's choice tiles, one row per step with a choice.
            foreach (var a in steps.Where(a => a.Level >= 1 && a.Level <= e.Steps.Count && e.Steps[a.Level - 1].IsChoice && !P.IsFixed(a)))
            {
                var step = e.Steps[a.Level - 1];
                var tiles = new List<ThronefallUIElement>();
                var act = a;
                Words(parent, PlannerIcons.Roman(a.Level), 4f, y, 26f, ChoiceTile, 17f, ModStyle.Dim, TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);
                for (int b = 0; b < step.Branches.Count; b++)
                {
                    var br = step.Branches[b];
                    var icon = PlannerIcons.Choice(m.Snap(), e, a.Level, br.Key);
                    var c = Tile(parent, $"choice:{e.Index}:{a.Level}:{b}", 30f + b * (ChoiceTile + 6f), y, new Vector2(ChoiceTile, ChoiceTile), icon, icon == null ? br.Name.Substring(0, 1) : null);
                    c.SetOn(EconSim.BranchIndex(step, a.Choice) == b);
                    c.SetDim(br.Locked, 0.35f);
                    string key = br.Key;
                    int income = b == 0 && e.Modifiers.Any(x => x.Kind == EconModifierKind.Harbour) && a.Level == 2 ? e.HarbourField : br.IncomeChange;
                    c.Applied += () =>
                    {
                        if (br.Locked) { Notify.Show($"{br.Name} is locked in this run", NotifyKind.Warning, 2f, key: "econ-day"); return; }
                        focus = c.Key;
                        m.Edit(() => act.Choice = key, act.Id);
                    };
                    c.Shown += _ => Say($"<b>{br.Name}</b>{(br.Locked ? " (locked)" : "")}{(income != 0 ? $" · {EconomyPlannerManager.Signed(income)} per dawn" : "")}\n<size=88%><color=#{Dim}>{br.Description}</color></size>");
                    tiles.Add(c);
                }
                panelRows.Add(tiles);
                y += ChoiceTile + 6f;
            }

            // A mill's fields, right here.
            var fields = EconomyPlannerManager.Fields(D.After, e);
            if (fields.Count > 0 && !fixedOnly)
            {
                var ids = new HashSet<string>(fields.Select(f => f.Id));
                int planned = D.Steps.Count(a => ids.Contains(a.Slot));
                int free = fields.Count(f => EconomyPlannerManager.StatusOf(D, f, out _, out _) == EconomyPlannerManager.Mark.Open);
                string kind = fields[0].TypeName;
                var fieldKind = EconomyPlannerManager.KindsOf(D).FirstOrDefault(k => k.Type == kind && k.Level == 1);
                var minus = Tile(parent, "field-:" + e.Index, 30f, y, new Vector2(ChoiceTile, ChoiceTile), null, "−", 24f);
                minus.Label.alignment = TextAlignmentOptions.Center;
                minus.SetDim(planned == 0, 0.45f);
                minus.Applied += () =>
                {
                    focus = minus.Key;
                    var last = D.Steps.LastOrDefault(a => ids.Contains(a.Slot));
                    if (last != null) m.Edit(() => m.Holder(P, last).Actions.Remove(last));
                };
                var plus = Tile(parent, "field+:" + e.Index, 30f + ChoiceTile + 6f, y, new Vector2(ChoiceTile, ChoiceTile), PlannerIcons.Kind(kind), null);
                plus.SetBadge("+", ModStyle.Bright);
                plus.SetDim(free == 0, 0.45f);
                plus.Applied += () => { focus = plus.Key; if (fieldKind != null) m.AddKind(D, fieldKind); else Notify.Show($"No more {kind}s that day", NotifyKind.Info, 2f, key: "econ-day"); };
                Words(parent, $"{kind}s <b>{planned}</b> <color=#{Dim}>· {free} more</color>", 30f + 2f * (ChoiceTile + 6f) + 4f, y, width - 120f, ChoiceTile, 18f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
                minus.Shown += _ => ShowSlot(slot);
                plus.Shown += _ => ShowSlot(slot);
                panelRows.Add([minus, plus]);
                y += ChoiceTile + 6f;
            }
            return y + 4f;
        }

        private void LostPanel(float width, float y)
        {
            var lost = P.Actions.Where(a => a.Kind == EconActionKind.KnockOut && a.Day == Day).ToList();
            var standing = Shown().Where(e => e.Level >= 1 && !e.Indestructible).ToList();
            var all = Tile(panel, "lost:all", 0f, y, new Vector2(Mathf.Min(260f, width - 12f), KindH - 6f), PlannerIcons.Lost,
                lost.Count >= standing.Count && standing.Count > 0 ? "None lost" : $"Every building · {standing.Count}", 19f);
            all.Applied += () =>
            {
                focus = "lost:all";
                var ids = new HashSet<string>(lost.Select(a => a.Slot));
                if (standing.All(e => ids.Contains(e.Id))) m.Edit(() => { foreach (var a in lost) m.Holder(P, a).Actions.Remove(a); });
                else m.Edit(() => P.Plan.Actions.AddRange(standing.Where(e => !ids.Contains(e.Id)).Select(e => new EconAction { Kind = EconActionKind.KnockOut, Day = Day, Slot = e.Id })));
            };
            all.Shown += _ => Say("Every building standing that night, or none.");
            panelRows.Add([all]);
            y += KindH + 4f;

            // Temple wisdom bought that day: what the buildings lost that night drop.
            var wisdom = P.Result.Actions.FirstOrDefault(r => r.Action.IsStep && r.Action.Day == Day && r.Counts && r.Branch?.IsWisdom == true);
            if (wisdom != null)
            {
                var holder = new GameObject("Wisdom", typeof(RectTransform), typeof(VerticalLayoutGroup));
                holder.transform.SetParent(panel, false);
                var rect = (RectTransform)holder.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(0f, -y);
                rect.sizeDelta = new Vector2(width - 12f, 46f);
                var layout = holder.GetComponent<VerticalLayoutGroup>();
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                wisdomPage = new ModPage(holder.transform) { Size = new Vector2(width - 12f, 46f) };
                var a = wisdom.Action;
                int each = wisdom.Branch.WisdomGold;
                int count = lost.Count(x => P.World.Find(x.Slot)?.IsBuilding == true);
                wisdomPage.Number("Wisdom", () => a.Amount ?? wisdom.Wisdom, v => m.Edit(() => a.Amount = v, a.Id), 0, 9999,
                    format: v => a.Amount == null ? $"{EconomyPlannerManager.Coin(v)}  <color=#{Dim}>{count} × {each}</color>" : $"{EconomyPlannerManager.Coin(v)}  <color=#{Dim}>typed</color>",
                    onClear: () => m.Edit(() => a.Amount = null, a.Id));
                y += 52f;
            }

            Words(panel, $"Lost on night {Day}", 0f, y, width, 26f, 21f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);
            y += 30f;
            listArea = ModScrollArea.Create(panel, new Vector2(width - 12f, Mathf.Max(RowH, size.y - TopH - Gap - y)), frame?.Frame).Place(new Vector2(0f, -y));
            float ly = 0f;
            if (lost.Count == 0) Words(listArea.Content, $"<color=#{Dim}>Nothing yet</color>", 4f, 4f, width - 20f, RowH, 19f, ModStyle.Gold);
            foreach (var a in lost)
            {
                var r = P.Result.Actions.FirstOrDefault(x => x.Action == a);
                var e = P.World.Find(a.Slot);
                if (e == null) continue;
                string effect = r == null ? "" : !r.Counts ? r.Reason?.ToLowerInvariant() ?? "" : e.NeverDenied ? "still pays" : r.IncomeDelta < 0 ? $"{r.IncomeDelta} at dawn" : "pays nothing anyway";
                var row = Tile(listArea.Content, "lostrow:" + e.Index, 0f, ly, new Vector2(width - 12f - RowH - 6f, RowH), PlannerIcons.Kind(e.TypeName),
                    $"{e.Label} <size=85%><color=#{Dim}>· {effect}</color></size>", 18f);
                if (r != null && !r.Counts) row.SetDim(true, 0.5f);
                var x = Tile(listArea.Content, "unlose:" + e.Index, width - 12f - RowH, ly, new Vector2(RowH, RowH), null, "×", 22f);
                x.Label.alignment = TextAlignmentOptions.Center;
                var slot = e;
                x.Applied += () => { focus = "unlose:" + slot.Index; m.ToggleLost(P, Day, slot); };
                row.Applied += () => { focus = "lostrow:" + slot.Index; m.ToggleLost(P, Day, slot); };
                row.Shown += _ => ShowSlot(D.After.Slots[slot.Index]);
                panelRows.Add([row, x]);
                ly += RowH + 4f;
            }
            listArea.SetContentHeight(ly);
            listArea.ScrollTo(listScroll);
        }

        // ---- Navigation ---------------------------------------------------------------------------------------------

        private void Wire()
        {
            if (root == null) return;
            var markers = map?.Markers ?? [];
            var wisdomRows = wisdomPage?.NavigationRows() ?? [];
            var rows = new List<IReadOnlyList<ThronefallUIElement>>();
            rows.AddRange(panelRows.Take(Layer == PlannerDayLayer.Lost ? 1 : panelRows.Count));
            foreach (var w in wisdomRows) rows.Add(w);
            if (Layer == PlannerDayLayer.Lost) rows.AddRange(panelRows.Skip(1));
            var nearestMarker = markers.OrderByDescending(mk => mk.Rect.anchoredPosition.x).FirstOrDefault();
            ModNav.Grid(rows, top.LastOrDefault(), below, left: nearestMarker);
            var panelEntry = rows.FirstOrDefault()?.FirstOrDefault();
            map?.Wire([null, panelEntry, top.FirstOrDefault(), below]);
            ModNav.Grid([top], null, markers.Count > 0 ? markers.OrderBy(mk => -mk.Rect.anchoredPosition.y).First() : panelEntry ?? below);
            if (below != null) below.topNav = markers.FirstOrDefault() ?? panelEntry ?? top.FirstOrDefault();
        }

        private void Restore()
        {
            var f = frame?.Frame;
            if (f == null) return;
            ThronefallUIElement target = null;
            if (focus != null && Tiles.TryGetValue(focus, out var t)) target = t;
            // A − / + that went away (no steps left): the marker it was for.
            if (target == null && focus != null && focus.IndexOf(':') > 0 && Tiles.TryGetValue("marker:" + focus.Substring(focus.IndexOf(':') + 1).Split(':')[0], out var mk)) target = mk;
            target ??= map?.Markers.LastOrDefault() ?? (ThronefallUIElement)top.FirstOrDefault();
            focus = null;
            if (target != null) f.Select(target);
        }

        // ---- Dev ----------------------------------------------------------------------------------------------------

        internal string Describe()
        {
            if (P == null || D == null) return "no day";
            var marks = Shown().Where(e => Tiles.ContainsKey("marker:" + e.Index)).Select(e => (e, mark: EconomyPlannerManager.StatusOf(D, e, out _, out _))).ToList();
            return $"day {Day} {Layer}: {D.Left}/{D.Budget} gold, {D.Steps.Count} steps " +
                $"[{string.Join(", ", D.Steps.Select(a => $"{D.After.Find(a.Slot)?.Label}→{a.Level}{(a.Choice != null ? ":" + a.Choice : "")}"))}], " +
                $"lost [{string.Join(", ", P.Actions.Where(a => a.Kind == EconActionKind.KnockOut && a.Day == Day).Select(a => P.World.Find(a.Slot)?.Label))}], " +
                $"markers {map?.Markers.Count ?? 0}: {marks.Count(t => t.mark == EconomyPlannerManager.Mark.Open)} open, {marks.Count(t => t.mark == EconomyPlannerManager.Mark.Poor)} poor, " +
                $"{marks.Count(t => t.mark == EconomyPlannerManager.Mark.Locked)} locked, {marks.Count(t => t.mark == EconomyPlannerManager.Mark.Max)} max; " +
                $"kinds [{string.Join(", ", Tiles.Keys.Where(k => k.StartsWith("kind:")).Select(k => k.Substring(5)))}]; selected {(frame?.Frame?.CurrentSelection as ModTile)?.Key ?? "-"}";
        }

        internal EconomyPlannerManager.DayState State => D;

        internal void DebugClick(int slot, bool right)
        {
            var e = D.After.Slots.ElementAtOrDefault(slot) ?? throw new ArgumentException($"no slot #{slot}");
            if (Layer == PlannerDayLayer.Lost) { m.ToggleLost(P, Day, P.World.Slots[slot]); return; }
            if (right) m.RemoveStep(D, e);
            else m.Cycle(D, e);
        }

        internal void DebugKind(string kind, int n, bool remove)
        {
            for (int i = 0; i < n; i++)
            {
                var k = EconomyPlannerManager.KindsOf(D).FirstOrDefault(x => x.Key.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
                    ?? EconomyPlannerManager.KindsOf(D).FirstOrDefault(x => x.Key.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? throw new ArgumentException($"no kind \"{kind}\"; kinds: {string.Join(", ", EconomyPlannerManager.KindsOf(D).Select(x => x.Key))}");
                if (remove) m.RemoveKind(D, k);
                else m.AddKind(D, k);
            }
        }

        internal bool DebugSelect(string key)
        {
            if (!Tiles.TryGetValue(key, out var t)) return false;
            frame?.Frame?.Select(t);
            return true;
        }

        internal string DebugMap(string path)
        {
            var shot = map?.Shot ?? throw new InvalidOperationException("no map");
            if (shot.Texture == null) return "no picture";
            LevelCapture.SavePng(shot, path);
            return "saved " + path;
        }

        private void Tick()
        {
            var f = frame?.Frame;
            if (f == null || UIFrameManager.instance?.ActiveFrame != f || ModTextInput.BlocksInput) return;
            try
            {
                if (EconomyPlannerManager.ShoulderDown(false) || m.EarlierKey.Down) ChangeDay(-1);
                else if (EconomyPlannerManager.ShoulderDown(true) || m.LaterKey.Down) ChangeDay(1);
                else if (m.KeyUndo) m.Undo();
                else if (m.KeyRemove && f.CurrentSelection is ModTile t && t.Key != null && t.Key.StartsWith("marker:") && int.TryParse(t.Key.Substring(7), out int i))
                {
                    focus = t.Key;
                    DebugClick(i, right: true);
                }
            }
            catch (Exception e) { Log.For<EconomyPlannerManager>().Caught("Planner day key failed", e); }
        }

        private class Driver : MonoBehaviour
        {
            public PlannerDayView View;
            private void Update() => View?.Tick();
        }
    }
}
