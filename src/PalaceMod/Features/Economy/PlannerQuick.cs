using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using PalaceMod.Economy;

/*
    Quick add, from a day's "+" on the board: a popup over the board with a tile per kind of building that day can take
    (icon, how many are free, the cost; "House → II" for upgrades), and the day's gold as "left/at sunrise". Each press
    adds the nearest free one to the plan straight away (right-click takes the last one off again), and the board
    underneath and the popup update, so several can be added without closing it. "Day N" opens the full day view.
*/
namespace PalaceMod
{
    public partial class EconomyPlannerManager
    {
        private int quickDay;
        private float quickScroll;
        private ModScrollArea quickArea;

        internal void QuickAdd(int day, RectTransform near)
        {
            quickDay = day;
            quickScroll = 0f;
            quickArea = null;
            ModPopup.Open($"Day {day}", BuildQuick, near, width: 620f);
        }

        private void BuildQuick(ModPage page)
        {
            var p = Project();
            if (p == null) return;
            var d = DayOf(p, quickDay);
            if (quickArea != null) quickScroll = quickArea.ScrollY;
            string left = d.Left < 0 ? $"<color=#{RedHex}>{d.Left}</color>" : d.Left.ToString("N0");
            ModPopup.Current?.SetTitle($"Day {d.Day}");
            var budget = page.Text($"{left}<color=#{DimHex}>/</color>{Coin(d.Budget)}");
            budget.font = ModStyle.BoldFont;
            budget.fontSize = 26f;
            budget.color = ModStyle.Bright;

            var kinds = KindsOf(d);
            const float tileH = 56f, gap = 8f;
            float width = page.Size.x;
            int perRow = 3;
            float tileW = (width - 14f - (perRow - 1) * gap) / perRow;
            int rows = Mathf.CeilToInt(kinds.Count / (float)perRow);
            float height = Mathf.Clamp(rows * (tileH + gap), tileH + gap, 4.5f * (tileH + gap));
            var holder = page.Custom("Kinds", kinds.Count == 0 ? 40f : height);
            if (kinds.Count == 0)
            {
                var none = GameWidgets.Text(holder, "None", $"<color=#{DimHex}>Nothing more can be built that day</color>", ModStyle.BodyFont, 20f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
                none.rectTransform.anchorMin = Vector2.zero;
                none.rectTransform.anchorMax = Vector2.one;
                none.rectTransform.offsetMin = none.rectTransform.offsetMax = Vector2.zero;
            }
            else
            {
                quickArea = ModScrollArea.Create(holder, new Vector2(width - 10f, height), ModPopup.Current?.Frame, barGap: 4f);
                var line = new List<ThronefallUIElement>();
                for (int i = 0; i < kinds.Count; i++)
                {
                    var k = kinds[i];
                    int free = k.Open.Count;
                    string sub = free > 0 ? $"×{free} · {Coin(k.Cost)}" : k.Poor.Count > 0 ? $"<color=#{RedHex}>{Coin(k.Cost)}</color>" : k.Planned > 0 ? "" : "none left";
                    var t = ModTile.Create(quickArea.Content, "quick:" + k.Key, new Vector2(tileW, tileH), PlannerIcons.Kind(k.Type),
                        $"{k.Name}\n<size=78%><color=#{DimHex}>{sub}</color></size>", 19f).Place(new Vector2(i % perRow * (tileW + gap), -(i / perRow) * (tileH + gap)));
                    t.Key = "quick:" + k.Key;
                    t.Label.enableWordWrapping = true;
                    t.Label.lineSpacing = -16f;
                    if (k.Planned > 0) { t.SetOn(true); t.SetBadge($"+{k.Planned}"); }
                    if (free == 0) t.SetDim(true, 0.5f);
                    var kind = k;
                    t.Applied += () => AddKind(DayOf(Project(), quickDay), FindKind(kind.Key) ?? kind);
                    t.RightClicked += () =>
                    {
                        var now = FindKind(kind.Key);
                        if (now != null) RemoveKind(DayOf(Project(), quickDay), now);
                    };
                    line.Add(t);
                    if (line.Count == perRow || i == kinds.Count - 1)
                    {
                        page.AddElementRow(line);
                        line = [];
                    }
                }
                quickArea.SetContentHeight(rows * (tileH + gap));
                quickArea.ScrollTo(quickScroll);
            }
            var open = page.Button($"Day {d.Day}: map, losses, choices", () => OpenDay(d.Day));
            open.gameObject.name = "Open day";
        }

        private Kind FindKind(string key)
        {
            var p = Project();
            return p == null ? null : KindsOf(DayOf(p, quickDay)).FirstOrDefault(k => k.Key == key);
        }
    }
}
