using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using PalaceMod.Economy;
using Object = UnityEngine.Object;
using G = PalaceMod.AnalyticsGraph;
using V = PalaceMod.Economy.PlannerVisuals;

/*
    The perks screen: every perk as the loadout screen shows it, as a grid of the game's icons. Perks that move gold
    are bright and toggle (lit = on in this plan), each with what toggling it would do to the end gold; a gem marks one
    that differs from the run's loadout. The rest are faint: they don't change the numbers. Beside the grid, the
    selected perk in the game's words and what it does to gold; under it, this loadout's gold curve against the run's.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private ModFrame perksFrame;
        private PlannerPerksView perksView;

        internal void OpenPerks()
        {
            if (Snap() == null) return;
            if (!perksFrame.IsOpen) perksFrame.Open();
        }

        private void BuildPerks(ModPage page)
        {
            perksView = null;
            if (Project() == null)
            {
                page.Text("The economy planner works during a level.");
                return;
            }
            var area = page.Custom("Perks", page.Size.y);
            perksView = new PlannerPerksView(this, page.Frame ?? perksFrame, area, page.Size);
            page.Wired += (above, below) => perksView?.Connect(below);
            perksView.Render();
        }

        /// <summary>Sets a perk on or off in the plan shown (dropping the change when it's the run's own state).</summary>
        internal void SetPerk(Projection p, EconPerk perk, bool on) => Edit(() =>
        {
            if (on == perk.Equipped && !p.File.Ancestors(p.Plan).Any(a => a.Perks.ContainsKey(perk.Key))) p.Plan.Perks.Remove(perk.Key);
            else p.Plan.Perks[perk.Key] = on;
        });
    }

    internal sealed class PlannerPerksView
    {
        private const float TileGap = 10f, Gap = 14f;

        private readonly EconomyPlannerManager m;
        private readonly ModFrame frame;
        private readonly RectTransform area;
        private readonly Vector2 size;
        private ThronefallUIElement below;
        private RectTransform root;
        private EconomyPlannerManager.Projection P;
        private TextMeshProUGUI detail;
        internal readonly Dictionary<string, ModTile> Tiles = [];
        private readonly List<List<ModTile>> rows = [];
        private readonly List<ModTile> buttons = [];
        private string focus;

        public PlannerPerksView(EconomyPlannerManager m, ModFrame frame, RectTransform area, Vector2 size)
        {
            this.m = m;
            this.frame = frame;
            this.area = area;
            this.size = size;
        }

        public void Connect(ThronefallUIElement below)
        {
            this.below = below;
            Wire();
        }

        private static string Dim => EconomyPlannerManager.DimHex;

        public void Render()
        {
            if (area == null) return;
            if (frame?.Frame?.CurrentSelection is ModTile keep && Tiles.ContainsValue(keep)) focus = keep.Key;
            if (root != null) Object.DestroyImmediate(root.gameObject);
            Tiles.Clear();
            rows.Clear();
            buttons.Clear();
            var obj = new GameObject("Perks", typeof(RectTransform));
            obj.transform.SetParent(area, false);
            root = (RectTransform)obj.transform;
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = size;
            var backdrop = V.Panel(root, "Backdrop", size.x + 40f, size.y + 24f, 0.9f);
            backdrop.anchoredPosition = new Vector2(-20f, 12f);
            P = m.Project();
            if (P == null) return;
            var snapWorld = P.Snap.World;
            var econ = snapWorld.Perks.ToDictionary(k => k.Key);
            var effective = P.File.EffectivePerks(P.Plan);
            bool On(EconPerk k) => effective.TryGetValue(k.Key, out bool v) ? v : k.Equipped;
            var runLoadout = EconSim.Run(EconPerks.Apply(snapWorld, new Dictionary<string, bool>()), P.Actions, paybacks: false);
            frame?.SetTitle("Perks");

            int delta = P.Result.EndGold - runLoadout.EndGold;
            Words(root, $"<size=125%><b>{EconomyPlannerManager.Coin(P.Result.EndGold)}</b></size> at the end with {P.PlanName}'s perks" +
                (delta != 0 ? $"  <color=#{Dim}>·</color>  {EconomyPlannerManager.Signed(delta)} vs your run's loadout ({runLoadout.EndGold})" : $"  <color=#{Dim}>· the same as your run's loadout</color>"),
                0f, 0f, size.x, 34f, 22f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft, ModStyle.BoldFont);

            float gridW = Mathf.Round(size.x * 0.6f);
            var perks = PerkManager.instance != null
                ? PerkManager.instance.allEquippables.Where(e => e is EquippablePerk).ToList()
                : [];
            // Perks the snapshot knows but the game list doesn't (shouldn't happen): still shown, without an icon.
            var keys = new HashSet<string>(perks.Select(e => e.name));
            float graphH = size.y >= 620f ? 170f : 140f;
            float y = 44f;
            int count = perks.Count + snapWorld.Perks.Count(k => !keys.Contains(k.Key));
            // The biggest tiles (up to the loadout screen's size) that fit above the graph.
            float Tile = 76f;
            int perRow;
            while (true)
            {
                perRow = Mathf.Max(4, Mathf.FloorToInt((gridW + TileGap) / (Tile + TileGap)));
                int lines = Mathf.CeilToInt(count / (float)perRow);
                if (Tile <= 46f || y + lines * (Tile + TileGap) <= size.y - graphH - Gap) break;
                Tile -= 2f;
            }
            var line = new List<ModTile>();
            int i = 0;
            foreach (var e in perks.Select(x => (x.name, (Equippable)x)).Concat(snapWorld.Perks.Where(k => !keys.Contains(k.Key)).Select(k => (k.Key, (Equippable)null))))
            {
                var (key, eq) = e;
                econ.TryGetValue(key, out var perk);
                float x = i % perRow * (Tile + TileGap);
                float ty = y + i / perRow * (Tile + TileGap);
                string name = eq == null ? perk?.Name : eq.icon == null ? EconomySnapshot.Translate(eq.LOCIDENTIFIER_NAME, eq.displayName) : null;
                var t = ModTile.Create(root, "perk:" + key, new Vector2(Tile, Tile), eq?.icon, name, 13f).Place(new Vector2(x, -ty));
                if (name != null)
                {
                    // No icon: its name, small and wrapped.
                    t.Label.enableWordWrapping = true;
                    t.Label.alignment = TextAlignmentOptions.Center;
                    t.Label.overflowMode = TextOverflowModes.Truncate;
                    t.Label.lineSpacing = -20f;
                }
                t.Key = "perk:" + key;
                Tiles[t.Key] = t;
                if (perk != null)
                {
                    bool on = On(perk);
                    t.SetOn(on);
                    t.SetMarker(on != perk.Equipped);
                    var flipped = new Dictionary<string, bool>(effective) { [perk.Key] = !on };
                    int change = EconSim.Run(EconPerks.Apply(snapWorld, flipped), P.Actions, paybacks: false).EndGold - P.Result.EndGold;
                    if (change != 0) t.SetBadge(EconomyPlannerManager.Signed(change), change > 0 ? ModStyle.Accent : V.Red);
                    t.Badge.fontSize = 15f;
                    var k = perk;
                    t.Applied += () => { focus = t.Key; m.SetPerk(m.Project(), k, !On(k)); };
                    t.Shown += _ => Show(eq, k, on, change);
                }
                else
                {
                    t.SetDim(true, 0.3f);
                    bool equipped = PerkManager.instance != null && eq != null && PerkManager.instance.CurrentlyEquipped.Contains(eq);
                    t.SetOn(equipped);
                    t.Applied += () => Notify.Show("That perk doesn't change gold, so the planner leaves it as it is.", NotifyKind.Info, 2.5f, key: "econ-perk");
                    t.Shown += _ => Show(eq, null, equipped, 0);
                }
                line.Add(t);
                if (line.Count == perRow) { rows.Add(line); line = []; }
                i++;
            }
            if (line.Count > 0) rows.Add(line);
            float gridBottom = y + rows.Count * (Tile + TileGap);

            // Detail beside the grid.
            float dx = gridW + 30f;
            detail = Words(root, "", dx, y, size.x - dx, size.y - y - graphH - 80f, 21f, ModStyle.Gold, TextAlignmentOptions.TopLeft, wrap: true);

            // Buttons under the detail.
            float by = size.y - graphH - Gap - 48f;
            float bx = dx;
            void Button(string key, string text, float width, Action action, string about)
            {
                var b = ModTile.Create(root, key, new Vector2(width, 42f), null, text, 19f).Place(new Vector2(bx, -by));
                b.Key = key;
                b.Label.alignment = TextAlignmentOptions.Center;
                Tiles[key] = b;
                b.Applied += action;
                b.Shown += _ => detail.text = about;
                buttons.Add(b);
                bx += width + 10f;
            }
            if (P.Plan.Perks.Count > 0)
                Button("reset", "Back to your loadout", 240f, () => { focus = "reset"; m.Edit(() => P.Plan.Perks.Clear()); }, "Drop this plan's perk changes.");
            Button("branch", "Try in a branch", 210f, () =>
            {
                focus = "branch";
                m.Edit(() => m.Branch(P.File, P.Plan, int.MaxValue, "perks"));
                Notify.Show("A branch with the same steps: change its perks and compare the two on the board.", NotifyKind.Info, 3f, key: "econ-branch");
            }, "A branch with this plan's steps, compared with it on the board, to try another loadout.");

            Graph(runLoadout, size.y - graphH, graphH);
            Wire();
            frame?.Frame?.RefetchManagedElements();
            ModTile target = focus != null && Tiles.TryGetValue(focus, out var f) ? f : rows.SelectMany(r => r).FirstOrDefault(t => t.Badge.text.Length > 0 || Tiles.ContainsKey(t.Key));
            target = focus != null && Tiles.TryGetValue(focus, out var g) ? g : rows.SelectMany(r => r).FirstOrDefault(t => econ.ContainsKey(t.Key.Substring(5))) ?? target;
            if (target != null) frame?.Frame?.Select(target);
        }

        private void Show(Equippable eq, EconPerk perk, bool on, int change)
        {
            if (detail == null) return;
            string name = eq != null ? EconomySnapshot.Translate(eq.LOCIDENTIFIER_NAME, eq.displayName) : perk?.Name ?? "?";
            string description = eq != null ? EconomySnapshot.Translate(eq.LOCIDENTIFIER_DESCRIPTION, eq.description) : "";
            var lines = new List<string> { $"<font=\"{ModStyle.TitleFont?.name}\"><size=150%>{name}</size></font>" };
            if (!string.IsNullOrEmpty(description)) lines.Add($"<color=#{Dim}>{description}</color>");
            if (perk == null)
            {
                lines.Add("Doesn't change gold: the planner leaves it as it is." + (on ? " Equipped in your run." : ""));
            }
            else
            {
                lines.Add(EconomyPlannerManager.PerkText(perk, P.World));
                lines.Add((on ? "On in this plan" : "Off in this plan") + (on != perk.Equipped ? $", <color=#{ModStyle.Hex(ModStyle.Accent)}>{(perk.Equipped ? "on" : "off")} in your run</color>." : ", as in your run."));
                lines.Add(change == 0 ? $"<color=#{Dim}>Turning it {(on ? "off" : "on")} wouldn't change the end gold.</color>"
                    : $"Turning it {(on ? "off" : "on")}: {EconomyPlannerManager.Signed(change)} at the end ({P.Result.EndGold + change}).");
                lines.Add($"<color=#{Dim}>{(ModNumber.UsingController ? "A" : "Enter or click")} turns it {(on ? "off" : "on")}.</color>");
            }
            detail.text = string.Join("\n", lines);
        }

        private void Graph(EconResult run, float y, float height)
        {
            var days = P.Result.Days;
            if (days.Count == 0) return;
            const float gutter = 58f;
            var view = new GameObject("Graph", typeof(RectTransform));
            view.transform.SetParent(root, false);
            var rect = (RectTransform)view.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(size.x, height);
            var back = G.Rect(view.transform, "Back", new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.6f));
            back.anchorMin = back.anchorMax = back.pivot = new Vector2(0f, 1f);
            back.anchoredPosition = new Vector2(gutter, 0f);
            back.sizeDelta = new Vector2(size.x - gutter, height);
            int n = days.Count + 1;
            float colW = (size.x - gutter) / n;
            var xs = Enumerable.Range(0, n).Select(i => gutter + colW * (i + 0.5f)).ToArray();
            float[] Values(EconResult r) => days.Select(d => (float)(r.Days.FirstOrDefault(x => x.Day == d.Day)?.Start ?? float.NaN)).Append(r.EndGold).ToArray();
            var series = new List<GraphSeries>
            {
                new("Your run's loadout", G.Fade(G.Dim, 0.95f), Values(run)),
                new("These perks", ModStyle.Bright, Values(P.Result)),
            };
            var range = V.Range(series);
            var plot = new GameObject("Plot", typeof(RectTransform));
            plot.transform.SetParent(view.transform, false);
            var plotRect = (RectTransform)plot.transform;
            plotRect.anchorMin = plotRect.anchorMax = plotRect.pivot = Vector2.zero;
            plotRect.sizeDelta = new Vector2(size.x, height);
            V.Strip(plotRect, size.x, height, xs, colW, series, range, null, -1);
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
            V.Legend(legendRect, series.AsEnumerable().Reverse().ToList());
            for (int i = 0; i < n; i++)
            {
                var t = G.Label(plotRect, i == n - 1 ? "End" : days[i].Day.ToString(), 15f, G.Dim, TextAlignmentOptions.Midline);
                t.rectTransform.anchoredPosition = new Vector2(xs[i] - 24f, -2f);
                t.rectTransform.sizeDelta = new Vector2(48f, 18f);
            }
        }

        private static TextMeshProUGUI Words(Transform parent, string text, float x, float y, float w, float h, float fontSize, Color color,
            TextAlignmentOptions align, TMP_FontAsset font = null, bool wrap = false)
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

        private void Wire()
        {
            if (rows.Count == 0) return;
            var lastRow = rows[rows.Count - 1];
            ModNav.Grid(rows.Cast<IReadOnlyList<ThronefallUIElement>>().ToList(), null, buttons.FirstOrDefault() ?? below, right: buttons.FirstOrDefault());
            ModNav.Grid([buttons], lastRow.FirstOrDefault(), below, left: lastRow.LastOrDefault());
            if (below != null) below.topNav = buttons.FirstOrDefault() ?? (ThronefallUIElement)lastRow.FirstOrDefault();
        }

        internal string Describe() =>
            string.Join(", ", Tiles.Values.Where(t => t.Key.StartsWith("perk:") && t.Badge != null && t.Background != null)
                .Select(t => $"{t.Key.Substring(5)}{(t.Badge.text.Length > 0 ? $"({t.Badge.text})" : "")}")) + "\n  " + (detail?.text ?? "");

        internal bool Select(string key)
        {
            if (!Tiles.TryGetValue(key, out var t)) return false;
            frame?.Frame?.Select(t);
            return true;
        }
    }
}
