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
    The Add screen, opened from a column of the board ("+ build", "+ lost", a day's popup): a picture of the level from
    above with a marker on every slot that can take a step that day, and beside it a tile per kind of building ("House
    ×6": apply picks the next one nearest the castle), the picks (a choice upgrade shows the game's choice tiles to pick
    from), and the Add button with what the picks do to the end gold. Under the map, the gold curve with and without
    them. Modes: build or upgrade on a day; buildings lost on a night; gold found on a night.

    Picks live only here until Add; Back drops them. The shoulders (or the ‹ › tiles) change the day, keeping the picks
    that still can be done then.
*/
namespace PalaceMod
{
    internal enum PlannerAddMode { Build, Lose, Gold }

    public partial class EconomyPlannerManager
    {
        private ModFrame addFrame;
        private PlannerAddView addView;
        internal int AddDay;
        internal PlannerAddMode AddMode;
        internal readonly List<PlannerAddView.Pick> AddPicks = [];
        internal int AddGold = 10;

        internal void OpenAdd(int day, PlannerAddMode mode)
        {
            if (Snap() == null) return;
            AddDay = day;
            AddMode = mode;
            AddPicks.Clear();
            if (addFrame.IsOpen) addView?.Render();
            else addFrame.Open();
        }

        private void BuildAdd(ModPage page)
        {
            addView = null;
            if (Project() == null)
            {
                page.Text("The economy planner works during a level.");
                return;
            }
            var area = page.Custom("Add", page.Size.y);
            addView = new PlannerAddView(this, page.Frame ?? addFrame, area, page.Size);
            page.Wired += (above, below) => addView?.Connect(below);
            addView.Render();
        }

        /// <summary>Adds the picks to the plan and goes back to the board.</summary>
        internal void CommitAdd(Projection p, List<EconAction> actions)
        {
            if (actions.Count == 0) return;
            Edit(() => p.Plan.Actions.AddRange(actions), actions[0].Id);
            AddPicks.Clear();
            addFrame.Close();
        }
    }

    internal sealed class PlannerAddView
    {
        internal sealed class Pick
        {
            public int Slot;
            public int Level;
            public string Choice;
            public EconActionKind Kind;
        }

        private const float TopH = 42f, Gap = 12f, KindW = 214f, KindH = 50f, PickH = 34f, ChoiceTile = 44f;

        private readonly EconomyPlannerManager m;
        private readonly ModFrame frame;
        private readonly RectTransform area;
        private readonly Vector2 size;
        private ThronefallUIElement below;
        private RectTransform root;
        private EconomyPlannerManager.Projection P;
        private EconWorld state;
        private ModMap map;
        private TextMeshProUGUI hover;
        internal readonly Dictionary<string, ModTile> Tiles = [];
        private readonly List<ModTile> top = [], kinds = [], picksList = [], commit = [];
        private readonly List<List<ModTile>> choiceRows = [];
        private string focus;
        private ModPage goldPage;

        public PlannerAddView(EconomyPlannerManager m, ModFrame frame, RectTransform area, Vector2 size)
        {
            this.m = m;
            this.frame = frame;
            this.area = area;
            this.size = size;
        }

        private List<Pick> Picks => m.AddPicks;
        private int Day => m.AddDay;
        private PlannerAddMode Mode => m.AddMode;

        public void Connect(ThronefallUIElement below)
        {
            this.below = below;
            Wire();
        }

        public void OnActivated() => Render();

        // ---- Candidates ---------------------------------------------------------------------------------------------

        /// <summary>What a slot's next step is that day, for a pick ("build", "to level 2").</summary>
        private static string StepWord(EconSlot e) => e.Level == 0 ? "build" : $"to level {e.Level + 1}";

        /// <summary>Slots that can be picked that day: buildable or upgradable (Build), standing (Lose).</summary>
        private List<EconSlot> Candidates()
        {
            if (Mode == PlannerAddMode.Build)
                return state.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0 && e.Active && e.Level < e.MaxLevel && e.Level < e.Steps.Count
                    && !e.Busy && EconSim.CanUpgrade(state, e)).ToList();
            if (Mode == PlannerAddMode.Lose)
            {
                var lost = new HashSet<string>(P.Actions.Where(a => a.Kind == EconActionKind.KnockOut && a.Day == Day).Select(a => a.Slot));
                return state.Slots.Where(e => !e.IsDependent && e.Steps.Count > 0 && e.Level >= 1 && !e.Indestructible && !lost.Contains(e.Id)).ToList();
            }
            return [];
        }

        private string KindOf(EconSlot e) => Mode == PlannerAddMode.Build && e.Level > 0 ? $"{e.TypeName}|{e.Level + 1}" : e.TypeName;

        private static float Distance(EconSlot e) => e.MapX * e.MapX + e.MapY * e.MapY;

        private List<EconAction> Actions() => Picks.Select(k => new EconAction
        {
            Kind = k.Kind, Day = Day, Slot = P.World.Slots[k.Slot].Id, Level = k.Level, Choice = k.Choice,
        }).ToList();

        private void Toggle(EconSlot e)
        {
            var existing = Picks.FirstOrDefault(k => k.Slot == e.Index);
            if (existing != null) Picks.Remove(existing);
            else AddPick(e);
            focus = "marker:" + e.Index;
            Render();
        }

        private void AddPick(EconSlot e)
        {
            if (Mode == PlannerAddMode.Lose) Picks.Add(new Pick { Slot = e.Index, Kind = EconActionKind.KnockOut });
            else
            {
                var step = e.Steps[e.Level];
                string choice = null;
                if (step.IsChoice)
                {
                    int first = step.Branches.FindIndex(b => !b.Locked);
                    choice = step.Branches[first < 0 ? 0 : first].Key;
                }
                Picks.Add(new Pick { Slot = e.Index, Level = e.Level + 1, Choice = choice, Kind = EconActionKind.Step });
            }
        }

        private void PickKind(string kind, int delta)
        {
            if (delta > 0)
            {
                var next = Candidates().Where(e => KindOf(e) == kind && Picks.All(k => k.Slot != e.Index)).OrderBy(Distance).FirstOrDefault();
                if (next == null)
                {
                    Notify.Show("No more of those that day.", NotifyKind.Info, 2f, key: "econ-add");
                    return;
                }
                AddPick(next);
            }
            else
            {
                var last = Picks.LastOrDefault(k => KindOf(state.Slots[k.Slot]) == kind);
                if (last == null) return;
                Picks.Remove(last);
            }
            focus = "kind:" + kind;
            Render();
        }

        // ---- Build --------------------------------------------------------------------------------------------------

        public void Render()
        {
            if (area == null) return;
            var keep = frame?.Frame?.CurrentSelection as ModTile;
            if (keep != null && Tiles.ContainsValue(keep)) focus ??= keep.Key;
            if (root != null) Object.DestroyImmediate(root.gameObject);
            Tiles.Clear();
            top.Clear();
            kinds.Clear();
            picksList.Clear();
            commit.Clear();
            choiceRows.Clear();
            goldPage = null;
            map = null;
            var obj = new GameObject("Add", typeof(RectTransform));
            obj.transform.SetParent(area, false);
            root = (RectTransform)obj.transform;
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = size;
            obj.AddComponent<Driver>().View = this;
            var backdrop = V.Panel(root, "Backdrop", size.x + 40f, size.y + 24f, 0.9f);
            backdrop.anchoredPosition = new Vector2(-20f, 12f);
            P = m.Project();
            if (P == null) return;
            var w = P.World;
            var (first, last) = EconomyPlannerManager.DayRange(w, Mode == PlannerAddMode.Build ? EconActionKind.Step : EconActionKind.KnockOut);
            m.AddDay = Mathf.Clamp(Day, first, Math.Max(first, last));
            frame?.SetTitle(Mode switch
            {
                PlannerAddMode.Build => $"Build on day {Day}",
                PlannerAddMode.Lose => $"Lost on night {Day}",
                _ => $"Gold found on night {Day}",
            });
            var steps = Mode == PlannerAddMode.Build ? P.Actions : P.Actions.Where(a => a.IsStep).ToList();
            state = EconSim.StateOn(w, steps, Math.Max(Day, w.FirstDay));
            // Picks that can't be done on this day any more go.
            var cands = new HashSet<int>(Candidates().Select(e => e.Index));
            Picks.RemoveAll(k => !cands.Contains(k.Slot) || (Mode == PlannerAddMode.Lose) != (k.Kind == EconActionKind.KnockOut));

            Top();
            float mapW = Mathf.Round(size.x * 0.6f), panelX = mapW + 24f, panelW = size.x - panelX;
            float stripH = size.y >= 620f ? 160f : 130f;
            float mapH = size.y - TopH - Gap - stripH - Gap;
            Map(new Vector2(mapW, mapH));
            Strip(mapW, TopH + Gap + mapH + Gap, stripH);
            Panel(panelX, panelW);
            Wire();
            frame?.Frame?.RefetchManagedElements();
            Restore();
        }

        private static readonly string Dim = EconomyPlannerManager.DimHex;

        private ModTile Tile(Transform parent, string key, float x, float y, Vector2 tileSize, Sprite icon, string text, float font = 19f)
        {
            var t = ModTile.Create(parent, key, tileSize, icon, text, font).Place(new Vector2(x, -y));
            t.Key = key;
            Tiles[key] = t;
            return t;
        }

        private void Top()
        {
            float x = 0f;
            void Mode(string key, string text, PlannerAddMode mode, float width)
            {
                var t = Tile(root, key, x, 0f, new Vector2(width, TopH - 4f), null, text, 19f);
                t.SetOn(m.AddMode == mode);
                t.Applied += () =>
                {
                    if (m.AddMode == mode) return;
                    m.AddMode = mode;
                    Picks.Clear();
                    focus = key;
                    Render();
                };
                t.Shown += _ => hover?.SetText(mode switch
                {
                    PlannerAddMode.Build => "Build or upgrade: pick buildings on the map, or a kind on the right.",
                    PlannerAddMode.Lose => "Buildings you expect to lose that night: they pay nothing at the next dawn (and drop Temple wisdom's gold).",
                    _ => "Gold you expect that night beyond the enemies' drops.",
                });
                top.Add(t);
                x += width + 10f;
            }
            Mode("mode:build", "Build or upgrade", PlannerAddMode.Build, 210f);
            Mode("mode:lose", "Lost that night", PlannerAddMode.Lose, 190f);
            Mode("mode:gold", "Gold found", PlannerAddMode.Gold, 150f);
            x += 20f;
            var prev = Tile(root, "day:prev", x, 0f, new Vector2(46f, TopH - 4f), null, "‹", 26f);
            prev.Label.alignment = TextAlignmentOptions.Center;
            prev.Applied += () => ChangeDay(-1);
            top.Add(prev);
            x += 52f;
            Words(root, $"{(m.AddMode == PlannerAddMode.Build ? "Day" : "Night")} {Day}", x, 0f, 110f, TopH - 4f, 24f, ModStyle.Bright, TextAlignmentOptions.Center, ModStyle.BoldFont);
            x += 116f;
            var next = Tile(root, "day:next", x, 0f, new Vector2(46f, TopH - 4f), null, "›", 26f);
            next.Label.alignment = TextAlignmentOptions.Center;
            next.Applied += () => ChangeDay(1);
            top.Add(next);
            foreach (var t in new[] { prev, next })
                t.Shown += _ => hover?.SetText(ModNumber.UsingController ? "Another day (LB/RB too); your picks stay if they can still be done." : "Another day (the shoulder buttons too); your picks stay if they can still be done.");
        }

        internal void ChangeDay(int delta)
        {
            var (first, last) = EconomyPlannerManager.DayRange(P.World, Mode == PlannerAddMode.Build ? EconActionKind.Step : EconActionKind.KnockOut);
            int day = Mathf.Clamp(Day + delta, first, Math.Max(first, last));
            if (day == Day) return;
            m.AddDay = day;
            Render();
        }

        private static TextMeshProUGUI Words(Transform parent, string text, float x, float y, float w, float h, float fontSize, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.TopLeft, TMP_FontAsset font = null, bool wrap = false)
        {
            var tmp = GameWidgets.Text(parent, "Text", text, font ?? ModStyle.BodyFont, fontSize, color, align);
            tmp.enableWordWrapping = wrap;
            tmp.overflowMode = wrap ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;
            tmp.characterSpacing = 0f;
            var rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return tmp;
        }

        // ---- Map ----------------------------------------------------------------------------------------------------

        private void Map(Vector2 mapSize)
        {
            var snap = m.Snap();
            var holder = new GameObject("Map Holder", typeof(RectTransform));
            holder.transform.SetParent(root, false);
            var rect = (RectTransform)holder.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -(TopH + Gap));
            rect.sizeDelta = mapSize;
            Vector3 Pos(int i) => snap.Objects.TryGetValue(i, out var o) && o != null ? o.transform.position : Vector3.zero;
            var all = state.Slots.Where(e => e.Steps.Count > 0 && snap.Objects.ContainsKey(e.Index)).ToList();
            map = ModMap.Create(holder.transform, mapSize, all.Select(e => Pos(e.Index)));
            var cands = Mode == PlannerAddMode.Gold ? [] : Candidates();
            var candSet = new HashSet<int>(cands.Select(e => e.Index));
            foreach (var e in all)
            {
                if (candSet.Contains(e.Index) || e.IsDependent) continue;
                bool built = e.Level >= 1;
                map.AddDot(Pos(e.Index), built ? 9f : 6f, built ? G.Fade(ModStyle.Gold, 0.9f) : G.Fade(ModStyle.Dim, e.Active ? 0.9f : 0.45f));
            }
            var castle = state.Slots.FirstOrDefault(e => e.IsCastle && !e.IsDependent);
            foreach (var e in cands.OrderBy(Distance))
            {
                var slot = e;
                var marker = map.AddMarker("marker:" + e.Index, Pos(e.Index), 30f, PlannerIcons.Kind(e.TypeName));
                Tiles[marker.Key] = marker;
                var pick = Picks.FirstOrDefault(k => k.Slot == e.Index);
                marker.SetOn(pick != null);
                if (Mode == PlannerAddMode.Build && e.Level > 0) marker.SetBadge(PlannerIcons.Roman(e.Level), ModStyle.Gold);
                if (Mode == PlannerAddMode.Lose) marker.SetFill(pick != null ? Color.Lerp(ModStyle.Panel, V.Red, 0.6f) : null);
                marker.Badge.fontSize = 13f;
                marker.Applied += () => Toggle(slot);
                marker.Shown += _ => ShowSlot(slot);
            }
            if (cands.Count == 0 && Mode != PlannerAddMode.Gold)
                Words(map.Root, Mode == PlannerAddMode.Build ? "Nothing can be built or upgraded that day: try a later day." : "Nothing is standing that night.",
                    20f, 16f, mapSize.x - 40f, 30f, 22f, ModStyle.Gold, font: ModStyle.ButtonFont);
            // A compass of sorts: which way is up.
            Words(map.Root, "north is up the screen, as you play", 12f, mapSize.y - 28f, mapSize.x - 24f, 24f, 17f, G.Fade(ModStyle.Gold, 0.7f), font: ModStyle.ButtonFont);
        }

        private void ShowSlot(EconSlot e)
        {
            if (hover == null) return;
            var now = P.World.Slots[e.Index];
            string what = Mode == PlannerAddMode.Lose
                ? (e.Pays && e.Income > 0 ? $"pays {e.Income} that dawn" : "pays nothing")
                : EconomyPlannerManager.NextStep(state, e);
            string level = e.Level == 0 ? "not built" : $"level {e.Level}{(e.Level != now.Level ? $" by day {Day}" : "")}";
            hover.text = $"<b>{e.Label}</b>  <color=#{Dim}>{level}</color>\n{what}";
        }

        // ---- Preview ------------------------------------------------------------------------------------------------

        private EconResult preview;

        private void Strip(float width, float y, float height)
        {
            var w = P.World;
            var acts = P.Actions.Concat(Actions()).ToList();
            preview = Picks.Count == 0 ? P.Result : EconSim.Run(P.World, acts, paybacks: false);
            var days = P.Result.Days;
            if (days.Count == 0) return;
            const float gutter = 58f;
            var view = new GameObject("Preview", typeof(RectTransform));
            view.transform.SetParent(root, false);
            var rect = (RectTransform)view.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(width, height);
            var back = G.Rect(view.transform, "Back", new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.6f));
            back.anchorMin = back.anchorMax = back.pivot = new Vector2(0f, 1f);
            back.anchoredPosition = new Vector2(gutter, 0f);
            back.sizeDelta = new Vector2(width - gutter, height);
            int n = days.Count + 1;
            float colW = (width - gutter) / n;
            var xs = Enumerable.Range(0, n).Select(i => gutter + colW * (i + 0.5f)).ToArray();
            float[] Values(EconResult r) => days.Select(d => (float)(r.Days.FirstOrDefault(x => x.Day == d.Day)?.Start ?? float.NaN)).Append(r.EndGold).ToArray();
            var series = new List<GraphSeries> { new("Your plan", G.Fade(G.Dim, 0.95f), Values(P.Result)) };
            if (Picks.Count > 0) series.Add(new GraphSeries("With these", ModStyle.Bright, Values(preview)));
            var range = V.Range(series);
            var plot = new GameObject("Plot", typeof(RectTransform));
            plot.transform.SetParent(view.transform, false);
            var plotRect = (RectTransform)plot.transform;
            plotRect.anchorMin = plotRect.anchorMax = plotRect.pivot = Vector2.zero;
            plotRect.sizeDelta = new Vector2(width, height);
            var shortCols = new HashSet<int>(days.Select((d, i) => (d, i)).Where(t => preview.Days.FirstOrDefault(x => x.Day == t.d.Day)?.Shortfall > 0).Select(t => t.i));
            int lit = days.FindIndex(d => d.Day == Day);
            V.Strip(plotRect, width, height, xs, colW, series, range, shortCols, lit);
            var axis = new GameObject("Axis", typeof(RectTransform));
            axis.transform.SetParent(view.transform, false);
            var axisRect = (RectTransform)axis.transform;
            axisRect.anchorMin = axisRect.anchorMax = axisRect.pivot = Vector2.zero;
            axisRect.sizeDelta = new Vector2(gutter, height);
            V.StripAxis(axisRect, gutter, height, range);
            var legend = new GameObject("Legend", typeof(RectTransform));
            legend.transform.SetParent(view.transform, false);
            var legendRect = (RectTransform)legend.transform;
            legendRect.anchorMin = legendRect.anchorMax = legendRect.pivot = new Vector2(0f, 1f);
            legendRect.anchoredPosition = new Vector2(gutter + 10f, -4f);
            float lx = V.Legend(legendRect, series.AsEnumerable().Reverse().ToList());
            // Day labels along the bottom, every other one when crowded.
            int every = colW < 34f ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                if (i % every != 0 && i != n - 1) continue;
                string label = i == n - 1 ? "End" : days[i].Day.ToString();
                var t = G.Label(plotRect, label, 15f, i == lit ? ModStyle.Bright : G.Dim, TextAlignmentOptions.Midline);
                t.rectTransform.anchoredPosition = new Vector2(xs[i] - 24f, -2f);
                t.rectTransform.sizeDelta = new Vector2(48f, 18f);
            }
        }

        // ---- Right panel --------------------------------------------------------------------------------------------

        private void Panel(float x, float width)
        {
            float y = TopH + Gap;
            var w = P.World;
            hover = Words(root, "", x, y, width, 54f, 20f, ModStyle.Gold, wrap: true);
            y += 60f;
            if (Mode == PlannerAddMode.Gold)
            {
                var holder = new GameObject("Gold", typeof(RectTransform), typeof(VerticalLayoutGroup));
                holder.transform.SetParent(root, false);
                var rect = (RectTransform)holder.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, -y);
                rect.sizeDelta = new Vector2(width, 200f);
                var layout = holder.GetComponent<VerticalLayoutGroup>();
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                goldPage = new ModPage(holder.transform) { Size = new Vector2(width, 200f) };
                goldPage.Number($"Gold on night {Day}", () => m.AddGold, v => m.AddGold = v, 1, 99999,
                    tooltip: () => "How much gold you expect that night beyond the enemies' drops: Temple wisdom, a chest you'll open...");
                var goldAdd = goldPage.Button($"Add {m.AddGold} gold on night {Day}", () =>
                    m.CommitAdd(P, [new EconAction { Kind = EconActionKind.Gold, Day = Day, Amount = m.AddGold }]));
                goldAdd.gameObject.name = "Commit";
                hover.text = $"Gold found on night {Day}: <color=#{Dim}>an estimate the planner can't work out itself.</color>";
                return;
            }

            // Kinds.
            var cands = Candidates();
            var groups = cands.GroupBy(KindOf).OrderBy(g => w.Slots[g.First().Index].EverPays ? 0 : 1).ThenBy(g => g.First().TypeName).ThenBy(g => g.Key).ToList();
            Words(root, Mode == PlannerAddMode.Build ? "By kind <size=80%><color=#" + Dim + ">(each press picks the next nearest the castle)</color></size>" : "By kind",
                x, y, width, 24f, 20f, ModStyle.Gold, font: ModStyle.BoldFont);
            y += 28f;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((width + 10f) / (KindW + 10f)));
            float kindW = (width - (perRow - 1) * 10f) / perRow;
            int maxRows = Mathf.Max(1, Mathf.FloorToInt((size.y - y - 330f) / (KindH + 8f)));
            int shown = Math.Min(groups.Count, perRow * maxRows);
            for (int i = 0; i < shown; i++)
            {
                var g = groups[i];
                var e = g.First();
                string kind = g.Key;
                int picked = Picks.Count(k => KindOf(state.Slots[k.Slot]) == kind);
                string name = Mode == PlannerAddMode.Build && e.Level > 0 ? $"{PlannerIcons.Short(e.TypeName)} → {PlannerIcons.Roman(e.Level + 1)}" : PlannerIcons.Short(e.TypeName);
                string sub = Mode == PlannerAddMode.Build ? Plain(EconomyPlannerManager.NextStep(state, g.OrderBy(Distance).First())) : $"{g.Count()} standing";
                var t = Tile(root, "kind:" + kind, x + i % perRow * (kindW + 10f), y + i / perRow * (KindH + 8f), new Vector2(kindW, KindH),
                    PlannerIcons.Kind(e.TypeName), $"{name} <size=80%><color=#{Dim}>×{g.Count()}</color></size>\n<size=72%><color=#{Dim}>{sub}</color></size>", 19f);
                t.Label.enableWordWrapping = true;
                t.Label.lineSpacing = -14f;
                t.SetOn(picked > 0);
                if (picked > 0) t.SetBadge($"+{picked}");
                t.Applied += () => PickKind(kind, 1);
                t.RightClicked += () => PickKind(kind, -1);
                t.Shown += _ => hover?.SetText($"<b>{e.TypeName}</b>: {g.Count()} {(Mode == PlannerAddMode.Build ? "can take this step" : "standing")}. " +
                    (ModNumber.UsingController ? "A picks one more." : "Enter or click picks one more, right-click drops one."));
                kinds.Add(t);
            }
            if (Mode == PlannerAddMode.Lose && cands.Count > 1)
            {
                int i = shown;
                var all = Tile(root, "kind:*", x + i % perRow * (kindW + 10f), y + i / perRow * (KindH + 8f), new Vector2(kindW, KindH), null,
                    $"Every building <size=80%><color=#{Dim}>×{cands.Count}</color></size>", 19f);
                all.Applied += () =>
                {
                    bool everything = cands.All(e => Picks.Any(k => k.Slot == e.Index));
                    Picks.Clear();
                    if (!everything) Picks.AddRange(cands.Select(e => new Pick { Slot = e.Index, Kind = EconActionKind.KnockOut }));
                    focus = "kind:*";
                    Render();
                };
                all.Shown += _ => hover?.SetText("Every building standing that night (for Temple wisdom: each one lost drops its gold).");
                kinds.Add(all);
                shown++;
            }
            if (groups.Count > shown) Words(root, $"<color=#{Dim}>and {groups.Count - shown} more kinds: pick them on the map</color>", x, y + Mathf.CeilToInt(shown / (float)perRow) * (KindH + 8f), width, 22f, 18f, ModStyle.Gold);
            y += Mathf.CeilToInt(shown / (float)perRow) * (KindH + 8f) + (groups.Count > shown ? 26f : 6f);

            // Picks.
            float bottom = size.y - 120f;
            Words(root, Picks.Count == 0 ? $"Picked <color=#{Dim}>· nothing yet</color>" : $"Picked · {Picks.Count}", x, y, width, 24f, 20f, ModStyle.Gold, font: ModStyle.BoldFont);
            y += 28f;
            var rows = PickRows();
            foreach (var row in rows)
            {
                float need = PickH + (row.Choice ? ChoiceTile + 6f : 0f) + 4f;
                if (y + need > bottom)
                {
                    Words(root, $"<color=#{Dim}>and {rows.Count - rows.IndexOf(row)} more</color>", x, y, width, 22f, 18f, ModStyle.Gold);
                    break;
                }
                var t = Tile(root, row.Key, x, y, new Vector2(width, PickH), PlannerIcons.Kind(row.Slot.TypeName), row.Text, 18f);
                if (Mode == PlannerAddMode.Lose) t.SetFill(Color.Lerp(ModStyle.Panel, V.Red, 0.25f));
                var members = row.Members;
                t.Applied += () =>
                {
                    Picks.Remove(members[members.Count - 1]);
                    focus = row.Key;
                    Render();
                };
                t.Shown += _ => hover?.SetText($"{row.Slot.Label}: " + (ModNumber.UsingController ? "A drops it." : "Enter or click drops it."));
                picksList.Add(t);
                y += PickH + 4f;
                if (row.Choice)
                {
                    var pick = members[0];
                    var slot = state.Slots[pick.Slot];
                    var step = slot.Steps[pick.Level - 1];
                    var tiles = new List<ModTile>();
                    for (int b = 0; b < step.Branches.Count; b++)
                    {
                        var br = step.Branches[b];
                        var icon = PlannerIcons.Choice(m.Snap(), slot, pick.Level, br.Key);
                        var c = Tile(root, $"choice:{pick.Slot}:{b}", x + 30f + b * (ChoiceTile + 8f), y, new Vector2(ChoiceTile, ChoiceTile), icon, icon == null ? br.Name.Substring(0, 1) : null);
                        c.SetOn(pick.Choice == br.Key);
                        c.SetDim(br.Locked, 0.35f);
                        string key = br.Key;
                        int income = EconSim.StepIncome(slot, b);
                        c.Applied += () =>
                        {
                            if (br.Locked) { Notify.Show($"{br.Name} is locked in this run.", NotifyKind.Warning, 2f, key: "econ-add"); return; }
                            pick.Choice = key;
                            focus = c.Key;
                            Render();
                        };
                        c.Shown += _ => hover?.SetText($"<b>{br.Name}</b>{(br.Locked ? " (locked)" : "")}{(income != 0 ? $" · {EconomyPlannerManager.Signed(income)} per dawn" : "")}\n<size=85%><color=#{Dim}>{br.Description}</color></size>");
                        tiles.Add(c);
                    }
                    choiceRows.Add(tiles);
                    y += ChoiceTile + 6f;
                }
            }

            // Summary and Add.
            var summary = Summary();
            Words(root, summary, x, size.y - 112f, width, 56f, 20f, ModStyle.Gold, wrap: true);
            var add = Tile(root, "commit", x, size.y - 50f, new Vector2(width * 0.62f, 44f), null,
                Picks.Count == 0 ? "Pick something first" : Mode == PlannerAddMode.Build ? $"Add {Picks.Count} to day {Day}" : $"Mark {Picks.Count} lost on night {Day}", 21f);
            add.Label.alignment = TextAlignmentOptions.Center;
            add.SetFill(Picks.Count > 0 ? Color.Lerp(ModStyle.Panel, ModStyle.Accent, 0.32f) : null);
            add.SetDim(Picks.Count == 0, 0.5f);
            add.Applied += () =>
            {
                if (Picks.Count == 0) return;
                m.CommitAdd(P, Actions());
            };
            add.Shown += _ => hover?.SetText(summary);
            commit.Add(add);
            var clear = Tile(root, "clear", x + width * 0.62f + 10f, size.y - 50f, new Vector2(width * 0.38f - 10f, 44f), null, "Clear picks", 19f);
            clear.Label.alignment = TextAlignmentOptions.Center;
            clear.SetDim(Picks.Count == 0, 0.5f);
            clear.Applied += () => { Picks.Clear(); focus = "clear"; Render(); };
            commit.Add(clear);
        }

        private static string Plain(string rich) => System.Text.RegularExpressions.Regex.Replace(rich ?? "", "<color[^>]*>|</color>", "");

        private sealed class PickRow
        {
            public string Key, Text;
            public EconSlot Slot;
            public List<Pick> Members = [];
            public bool Choice;
        }

        /// <summary>Picks as rows: plain builds (or losses) of one kind together, choice upgrades one by one.</summary>
        private List<PickRow> PickRows()
        {
            var rows = new List<PickRow>();
            foreach (var k in Picks)
            {
                var e = state.Slots[k.Slot];
                bool choice = k.Kind == EconActionKind.Step && e.Steps[k.Level - 1].IsChoice;
                string key = choice ? $"pick:{k.Slot}" : $"pick:{KindOf(e)}";
                var row = rows.FirstOrDefault(r => r.Key == key);
                if (row == null)
                {
                    row = new PickRow { Key = key, Slot = e, Choice = choice };
                    rows.Add(row);
                }
                row.Members.Add(k);
            }
            foreach (var row in rows)
            {
                var e = row.Slot;
                int n = row.Members.Count;
                string name = n > 1 ? $"{e.TypeName} ×{n}" : e.Label;
                if (Mode == PlannerAddMode.Lose)
                {
                    int pay = row.Members.Sum(k => state.Slots[k.Slot].Pays ? state.Slots[k.Slot].Income : 0);
                    row.Text = $"{name} <size=85%><color=#{Dim}>· lost{(pay > 0 ? $", -{pay} at dawn" : "")}</color></size>";
                }
                else
                {
                    int cost = row.Members.Sum(k => state.Slots[k.Slot].Steps[k.Level - 1].Cost);
                    row.Text = $"{name} <size=85%><color=#{Dim}>· {StepWord(e)} · {EconomyPlannerManager.Coin(cost)}</color></size>";
                }
            }
            return rows;
        }

        private string Summary()
        {
            if (Picks.Count == 0)
                return Mode == PlannerAddMode.Build
                    ? $"<color=#{Dim}>Pick buildings on the map or by kind: the curve shows what they'd change.</color>"
                    : $"<color=#{Dim}>Pick the buildings you expect to lose that night.</color>";
            var r = preview ?? P.Result;
            int delta = r.EndGold - P.Result.EndGold;
            var lines = new List<string>();
            if (Mode == PlannerAddMode.Build) lines.Add($"Costs {EconomyPlannerManager.Coin(Picks.Sum(k => state.Slots[k.Slot].Steps[k.Level - 1].Cost))}");
            lines.Add($"end gold {P.Result.EndGold} → <b>{r.EndGold}</b> ({EconomyPlannerManager.Signed(delta)})");
            var shortDay = r.FirstShortDay;
            if (shortDay != null) lines.Add($"<color=#{EconomyPlannerManager.RedHex}>short by {shortDay.Shortfall} on day {shortDay.Day}</color>");
            return string.Join(" · ", lines);
        }

        // ---- Navigation ---------------------------------------------------------------------------------------------

        private void Wire()
        {
            if (root == null) return;
            ModNav.Grid([top], null, map?.Markers.FirstOrDefault() ?? kinds.FirstOrDefault() ?? (ThronefallUIElement)commit.FirstOrDefault());
            // Right panel: kinds (rows), picks, their choice tiles, then Add/Clear.
            var panelRows = new List<IReadOnlyList<ThronefallUIElement>>();
            if (kinds.Count > 0)
            {
                float firstY = kinds[0].Rect.anchoredPosition.y;
                foreach (var line in kinds.GroupBy(k => k.Rect.anchoredPosition.y).OrderByDescending(g => g.Key)) panelRows.Add(line.Cast<ThronefallUIElement>().ToList());
            }
            int choiceIndex = 0;
            foreach (var p in picksList)
            {
                panelRows.Add([p]);
                var tile = choiceRows.ElementAtOrDefault(choiceIndex);
                if (tile != null && tile.Count > 0 && tile[0].Rect.anchoredPosition.y < p.Rect.anchoredPosition.y && tile[0].Rect.anchoredPosition.y > p.Rect.anchoredPosition.y - PickH - 12f)
                {
                    panelRows.Add(tile.Cast<ThronefallUIElement>().ToList());
                    choiceIndex++;
                }
            }
            if (commit.Count > 0) panelRows.Add(commit.Cast<ThronefallUIElement>().ToList());
            var markers = map?.Markers ?? [];
            var nearestMarker = markers.OrderByDescending(mk => mk.Rect.anchoredPosition.x).FirstOrDefault();
            ModNav.Grid(panelRows, top.LastOrDefault(), below, left: nearestMarker);
            var panelEntry = panelRows.FirstOrDefault()?.FirstOrDefault();
            map?.Wire([null, panelEntry, top.FirstOrDefault(), below]);
            if (goldPage != null)
            {
                var rows = goldPage.NavigationRows();
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i][0].topNav = i > 0 ? rows[i - 1][0] : top.FirstOrDefault();
                    rows[i][0].botNav = i < rows.Count - 1 ? rows[i + 1][0] : below;
                }
                foreach (var t in top) t.botNav = rows.FirstOrDefault()?.FirstOrDefault();
            }
            else foreach (var t in top) t.botNav = markers.Count > 0 ? markers.OrderBy(mk => -mk.Rect.anchoredPosition.y).First() : panelEntry ?? below;
            if (below != null) below.topNav = commit.FirstOrDefault() ?? (ThronefallUIElement)top.FirstOrDefault();
        }

        private void Restore()
        {
            var f = frame?.Frame;
            if (f == null) return;
            ThronefallUIElement target = null;
            if (focus != null && Tiles.TryGetValue(focus, out var t)) target = t;
            if (target == null && focus != null && focus.StartsWith("Gold")) target = goldPage?.NavigationRows().FirstOrDefault()?.FirstOrDefault();
            target ??= goldPage?.NavigationRows().FirstOrDefault()?.FirstOrDefault()
                ?? (ThronefallUIElement)map?.Markers.FirstOrDefault(mk => mk.Key == focus) ?? (Mode == PlannerAddMode.Build ? (ThronefallUIElement)kinds.FirstOrDefault() ?? map?.Markers.FirstOrDefault() : map?.Markers.FirstOrDefault()) ?? top.FirstOrDefault();
            focus = null;
            if (target != null) f.Select(target);
        }

        // ---- Dev ----------------------------------------------------------------------------------------------------

        internal string Describe() =>
            $"{Mode} {(Mode == PlannerAddMode.Build ? "day" : "night")} {Day}: {map?.Markers.Count ?? 0} markers, {kinds.Count} kinds, {Picks.Count} picked " +
            $"[{string.Join(", ", Picks.Select(k => $"{state.Slots[k.Slot].Label}{(k.Choice != null ? ":" + k.Choice : "")}"))}]; {Plain(Summary())}" +
            $"\n  markers: {string.Join(", ", (map?.Markers ?? []).Take(12).Select(mk => $"{mk.Key}={state.Slots[int.Parse(mk.Key.Substring(7))].Label}"))}";

        internal void DebugPickSlot(int index)
        {
            var e = Candidates().FirstOrDefault(x => x.Index == index) ?? throw new ArgumentException($"slot #{index} can't be picked {Day}");
            Toggle(e);
        }

        internal void DebugPickKind(string kind, int n)
        {
            var match = Tiles.Keys.FirstOrDefault(k => k.StartsWith("kind:") && k.Substring(5).StartsWith(kind, StringComparison.OrdinalIgnoreCase))
                ?? Tiles.Keys.FirstOrDefault(k => k.StartsWith("kind:") && k.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException($"no kind \"{kind}\"; kinds: {string.Join(", ", Tiles.Keys.Where(k => k.StartsWith("kind:")))}");
            for (int i = 0; i < n; i++) PickKind(match.Substring(5), 1);
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
            var raw = map.Root.GetComponentInChildren<RawImage>(true);
            return $"saved {path}: {shot.Texture.width}x{shot.Texture.height} created {shot.Texture.IsCreated()}, raw {(raw == null ? "none" : $"{raw.texture?.name} {raw.color} active {raw.isActiveAndEnabled} rect {raw.rectTransform.rect}")}, " +
                $"center {shot.Center} half {shot.HalfWidth:0}x{shot.HalfHeight:0}";
        }

        internal void DebugCommit()
        {
            if (Picks.Count == 0) throw new InvalidOperationException("nothing picked");
            m.CommitAdd(P, Actions());
        }

        private void Tick()
        {
            var f = frame?.Frame;
            if (f == null || UIFrameManager.instance?.ActiveFrame != f || ModTextInput.BlocksInput) return;
            try
            {
                if (EconomyPlannerManager.ShoulderDown(false) || m.EarlierKey.Down) ChangeDay(-1);
                else if (EconomyPlannerManager.ShoulderDown(true) || m.LaterKey.Down) ChangeDay(1);
            }
            catch (Exception e) { Log.For<EconomyPlannerManager>().Caught("Planner add key failed", e); }
        }

        private class Driver : MonoBehaviour
        {
            public PlannerAddView View;
            private void Update() => View?.Tick();
        }
    }
}
