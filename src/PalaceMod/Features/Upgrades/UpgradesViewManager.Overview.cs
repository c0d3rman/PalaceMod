using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Pause-screen overview: an "Upgrades" button in the pause menu opens a frame with a grid of building cards. A card
    shows the building's name, its level as pips and the upgrade choices it took as the game's own choice tiles;
    identical buildings (same level and choices, e.g. four plain houses) share one card. Selecting a card (keyboard,
    controller or mouse hover) fills a fixed detail area beside the grid with the building's path so far, in the
    game's words, and what its next upgrade offers. The detail area shrinks to fit, so nothing runs off the screen.
*/
namespace PalaceMod
{
    public partial class UpgradesViewManager
    {
        private const string PauseButtonId = "upgrades-overview";
        private const int CardColumns = 3;
        private const float CardWidth = 272f, CardHeight = 150f, CardGap = 14f, CardTile = 46f;

        private ModFrame overview;
        private RectTransform overviewAside;
        private readonly List<OverviewCard> cards = [];
        private OverviewCard shownCard;

        private sealed class BuildingGroup
        {
            public string Name;
            public List<BuildSlot> Slots = [];
            public BuildSlot First => Slots[0];
        }

        private void StartPauseOverview()
        {
            overview = new ModFrame("Upgrades").SetContent(BuildOverview);
            DevCommands.Register("upgv.overview", "upgv.overview [card]   open the pause-screen upgrades overview (and select card n, 1-based)", args =>
            {
                if (!overview.IsOpen) overview.Open();
                if (!overview.IsOpen) return "not open";
                if (args.Length > 0)
                {
                    int n = int.Parse(args[0]) - 1;
                    if (n < 0 || n >= cards.Count) throw new ArgumentException($"there are {cards.Count} cards");
                    overview.Frame.Select(cards[n]);
                }
                return $"open, {cards.Count} cards" + (shownCard != null ? $", showing {shownCard.Group.Name}" : "");
            });
            // A menu, not part of the feature's switch: always there in a level.
            PauseMenu.AddButton(PauseButtonId, "Upgrades", overview.Open, order: 20, visible: () => PlayerInteraction.instance != null);
        }

        // ---------- Grid ----------

        private void BuildOverview(ModPage page)
        {
            cards.Clear();
            shownCard = null;
            overviewAside = page.Aside;
            var groups = Groups();
            if (groups.Count == 0)
            {
                page.Text("Nothing built yet.");
                return;
            }
            int rows = Mathf.CeilToInt(groups.Count / (float)CardColumns);
            var grid = page.Custom("Cards", rows * CardHeight + (rows - 1) * CardGap + 12f);
            for (int i = 0; i < groups.Count; i++)
            {
                var card = OverviewCard.Create(grid, groups[i], ShowDetail);
                var rect = (RectTransform)card.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(CardWidth, CardHeight);
                rect.anchoredPosition = new Vector2(i % CardColumns * (CardWidth + CardGap) + CardWidth / 2f + 6f,
                    -(i / CardColumns * (CardHeight + CardGap) + CardHeight / 2f + 6f));
                cards.Add(card);
            }
            for (int r = 0; r < rows; r++)
                page.AddElementRow(cards.Skip(r * CardColumns).Take(CardColumns).Cast<ThronefallUIElement>().ToList());
            ShowDetail(cards[0]);
        }

        /// <summary>Built buildings, identical ones (same name, level and choices) together, cheapest building first.</summary>
        private static List<BuildingGroup> Groups()
        {
            var groups = new List<BuildingGroup>();
            var keys = new Dictionary<string, BuildingGroup>();
            foreach (var slot in UpgradeInfo.AllBuilt().OrderBy(s => s.OwnUpgrades[0].cost))
            {
                string name = UpgradeInfo.BuildingName(slot);
                string key = $"{name}|{slot.Level}|{UpgradeInfo.MaxLevel(slot)}|{string.Join(",", UpgradeInfo.Taken(slot).Select(t => t.BranchIndex))}";
                if (!keys.TryGetValue(key, out var group))
                {
                    group = new BuildingGroup { Name = name };
                    keys[key] = group;
                    groups.Add(group);
                }
                group.Slots.Add(slot);
            }
            return groups;
        }

        private static string Title(BuildingGroup group) => group.Slots.Count > 1 ? $"{group.Name} ×{group.Slots.Count}" : group.Name;

        private static string Coin(int amount) => $"{amount}<sprite name=\"coin\">";

        /// <summary>One line on a card or in the details: what's next for the building.</summary>
        private static string NextStatus(BuildSlot slot)
        {
            if (UpgradeInfo.Next(slot) != null) return $"Next {Coin(slot.NextUpgradeOrBuildCost)}";
            return UpgradeInfo.HasRemaining(slot) ? "Needs castle" : "Max";
        }

        /// <summary>Level pips: filled diamonds for levels reached, hollow-looking dim ones for the rest.</summary>
        private static RectTransform AddPips(Transform parent, int level, int max, float size)
        {
            var row = AddRow(parent, size * 0.75f);
            for (int i = 0; i < max; i++)
            {
                var holder = new GameObject("Pip", typeof(RectTransform), typeof(LayoutElement));
                holder.transform.SetParent(row, false);
                var le = holder.GetComponent<LayoutElement>();
                le.preferredWidth = le.preferredHeight = le.minWidth = le.minHeight = size * 1.2f;
                var pip = new GameObject("Diamond", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                pip.transform.SetParent(holder.transform, false);
                var image = pip.GetComponent<Image>();
                image.raycastTarget = false;
                image.color = i < level ? ModStyle.Gold : new Color(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.55f);
                var rect = (RectTransform)pip.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(size * 0.8f, size * 0.8f);
                rect.localRotation = Quaternion.Euler(0f, 0f, 45f);
            }
            return row;
        }

        // ---------- Detail area ----------

        private void ShowDetail(OverviewCard card)
        {
            if (card == null || card == shownCard || overviewAside == null) return;
            shownCard = card;
            foreach (Transform child in overviewAside.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            var group = card.Group;
            var slot = group.First;
            if (slot == null) return;

            var source = GameWidgets.PauseTooltip?.selectionTitle?.GetComponentInParent<MPUIKIT.MPImage>(true);
            var box = source != null ? GameWidgets.Clone(source, overviewAside, "Details", keepChildren: false) : new GameObject("Details", typeof(RectTransform));
            foreach (var c in box.GetComponents<Component>())
                if (c is LayoutGroup or ContentSizeFitter or LayoutElement) Object.DestroyImmediate(c);
            box.AddComponent<VerticalLayoutGroup>().padding = new RectOffset(28, 28, 20, 24);
            box.AddComponent<ContentSizeFitter>();
            if (box.GetComponent<MPUIKIT.MPImage>() is { } panelImage)
            {
                panelImage.raycastTarget = false;
                ModStyle.MarkPanel(panelImage);
            }
            box.SetActive(true);
            box.transform.SetParent(overviewAside, false);
            var rect = (RectTransform)box.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            float width = Mathf.Min(overviewAside.rect.width, 700f);
            rect.sizeDelta = new Vector2(width, 0f);
            var layout = box.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            box.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            AddText(box.transform, Title(group), ModStyle.TitleFont, 40f, ModStyle.Gold, wrapWidth: width - 56f);
            var levelRow = AddRow(box.transform, 14f);
            AddPips(levelRow, slot.Level, UpgradeInfo.MaxLevel(slot), 16f);
            AddText(levelRow, $"Level {slot.Level} of {UpgradeInfo.MaxLevel(slot)}", ModStyle.BodyFont, 24f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);

            float textWidth = width - 56f - 72f;
            var taken = UpgradeInfo.Taken(slot).Where(s => s.Level > 1 || s.IsChoice).ToList();
            if (taken.Count > 0)
            {
                DetailSection(box.transform, "Upgrades taken");
                foreach (var step in taken)
                {
                    if (step.IsChoice && step.TakenChoice != null)
                        DetailChoice(box.transform, slot, step.TakenChoice, $"Lv {step.Level}", dim: false, textWidth, taken: true);
                    else
                    {
                        string text = UpgradeInfo.StepText(slot, step);
                        if (!string.IsNullOrEmpty(text))
                            AddText(box.transform, $"<color=#{ModStyle.Hex(ModStyle.Dim)}>Lv {step.Level}</color>  {Shorten(text)}", ModStyle.BodyFont, 22f, ModStyle.Gold, wrapWidth: width - 56f);
                    }
                }
            }

            var next = UpgradeInfo.Next(slot);
            if (next is { } n)
            {
                DetailSection(box.transform, $"Next · Lv {n.Level} · {Coin(slot.NextUpgradeOrBuildCost)}");
                if (n.IsChoice)
                    foreach (var branch in n.Upgrade.upgradeBranches)
                        DetailChoice(box.transform, slot, branch.choiceDetails, null, dim: UpgradeInfo.IsLocked(branch.choiceDetails), textWidth);
                else AddText(box.transform, Shorten(UpgradeInfo.StepText(slot, n)), ModStyle.BodyFont, 22f, ModStyle.Gold, wrapWidth: width - 56f);
            }
            else DetailSection(box.transform, UpgradeInfo.HasRemaining(slot) ? "Next upgrade needs a higher castle level" : "Fully upgraded");

            // Shrink to fit the area rather than run off the screen.
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            float room = overviewAside.rect.height;
            float scale = rect.rect.height > room && room > 0f ? room / rect.rect.height : 1f;
            rect.localScale = new Vector3(scale, scale, 1f);
        }

        private void DetailSection(Transform parent, string text)
        {
            var tmp = AddText(parent, text, ModStyle.BoldFont, 25f, ModStyle.Gold);
            tmp.margin = new Vector4(0f, 12f, 0f, 0f);
        }

        // A choice tile, its name (with an optional prefix like "Lv 2") and the game's description beside it.
        private static void DetailChoice(Transform parent, BuildSlot slot, Choice choice, string prefix, bool dim, float textWidth, bool taken = false)
        {
            var row = AddRow(parent, 14f);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
            AddTile(row, choice, 54f, dim, taken);
            var column = new GameObject("Text", typeof(RectTransform), typeof(VerticalLayoutGroup));
            column.transform.SetParent(row, false);
            var v = column.GetComponent<VerticalLayoutGroup>();
            v.spacing = 0f;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = v.childForceExpandHeight = false;
            string name = DescriptionTitle(slot, choice, taken);
            if (prefix != null) name = $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{prefix}</color>  {name}";
            AddText(column.transform, name, ModStyle.BoldFont, 23f, dim ? ModStyle.Dim : ModStyle.Gold, wrapWidth: textWidth);
            string description = DescriptionText(slot, choice, taken);
            if (!string.IsNullOrEmpty(description))
            {
                var tmp = AddText(column.transform, description, ModStyle.BodyFont, 20f, dim ? ModStyle.Dim : ModStyle.Gold, wrapWidth: textWidth);
                tmp.characterSpacing = 0f;
            }
        }

        // ---------- Card ----------

        /// <summary>A building card in the overview grid: a selectable panel in the game's tooltip look.</summary>
        private class OverviewCard : ThronefallUIElement
        {
            public BuildingGroup Group;
            private Action<OverviewCard> onShow;
            private MPUIKIT.MPImage background;
            private Color fillDefault, fillFocus;
            private float scaleFrom = 1f, scaleTo = 1f, clock = 1f;
            private Color outlineFrom, outlineTo;
            private const float AnimTime = 0.15f;
            private static readonly Color OutlineDefault = new(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.6f);

            public static OverviewCard Create(Transform parent, BuildingGroup group, Action<OverviewCard> onShow)
            {
                var go = new GameObject($"Card {Title(group)}", typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var card = go.AddComponent<OverviewCard>();
                card.Group = group;
                card.onShow = onShow;
                card.autoSelectOnFocus = true; // hovering a card shows its details, like moving the selection there

                var source = GameWidgets.PauseTooltip?.selectionTitle?.GetComponentInParent<MPUIKIT.MPImage>(true);
                var bgObj = source != null
                    ? GameWidgets.Clone(source, go.transform, "Background", keepChildren: false)
                    : new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(MPUIKIT.MPImage));
                bgObj.transform.SetParent(go.transform, false);
                foreach (var c in bgObj.GetComponents<Component>())
                    if (c is LayoutGroup or ContentSizeFitter or LayoutElement) DestroyImmediate(c);
                var bgRect = (RectTransform)bgObj.transform;
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.pivot = new Vector2(0.5f, 0.5f);
                bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;
                bgRect.localScale = Vector3.one;
                card.background = bgObj.GetComponent<MPUIKIT.MPImage>();
                card.background.raycastTarget = true; // the frame finds the card under the mouse through this
                card.fillDefault = Color.Lerp(ModStyle.Panel, Color.white, 0.04f);
                card.fillFocus = Color.Lerp(ModStyle.Panel, Color.white, 0.09f);
                card.background.color = card.fillDefault;
                card.background.OutlineWidth = 1.5f;
                card.background.OutlineColor = OutlineDefault;
                bgObj.SetActive(true);

                var slot = group.First;
                var content = bgObj.transform;
                var title = AddText(content, Title(group), ModStyle.BoldFont, 25f, ModStyle.Gold);
                title.overflowMode = TextOverflowModes.Ellipsis;
                Place(title.rectTransform, new Vector2(16f, -10f), new Vector2(CardWidth - 32f, 32f));
                title.GetComponent<LayoutElement>().ignoreLayout = true;

                var pips = AddPips(content, slot.Level, UpgradeInfo.MaxLevel(slot), 13f);
                pips.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                Place(pips, new Vector2(16f, -46f), new Vector2(0f, 18f));

                var status = AddText(content, NextStatus(slot), ModStyle.BodyFont, 20f, ModStyle.Dim, TextAlignmentOptions.MidlineRight);
                status.GetComponent<LayoutElement>().ignoreLayout = true;
                var statusRect = status.rectTransform;
                statusRect.anchorMin = statusRect.anchorMax = statusRect.pivot = new Vector2(1f, 1f);
                statusRect.anchoredPosition = new Vector2(-14f, -44f);
                statusRect.sizeDelta = new Vector2(150f, 22f);

                var taken = UpgradeInfo.TakenChoices(slot);
                if (taken.Count > 0)
                {
                    var tiles = AddRow(content, 8f);
                    tiles.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                    tiles.anchorMin = tiles.anchorMax = tiles.pivot = new Vector2(0f, 0f);
                    tiles.anchoredPosition = new Vector2(16f, 14f);
                    tiles.sizeDelta = new Vector2(0f, CardTile);
                    foreach (var (_, choice) in taken) AddTile(tiles, choice, CardTile, dim: false, taken: true);
                }
                else
                {
                    var none = AddText(content, "No upgrade choices", ModStyle.ButtonFont, 20f, ModStyle.Dim);
                    none.GetComponent<LayoutElement>().ignoreLayout = true;
                    var r = none.rectTransform;
                    r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 0f);
                    r.anchoredPosition = new Vector2(16f, 22f);
                    r.sizeDelta = new Vector2(CardWidth - 32f, 26f);
                }
                go.AddComponent<TFUIAudioHelper>(); // native select sounds
                return card;
            }

            private static void Place(RectTransform rect, Vector2 topLeft, Vector2 size)
            {
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = topLeft;
                rect.sizeDelta = size;
            }

            private void Animate(SelectionState state)
            {
                bool on = state != SelectionState.Default;
                scaleFrom = background != null ? background.transform.localScale.x : 1f;
                scaleTo = on ? 1.04f : 1f;
                outlineFrom = background != null ? background.OutlineColor : OutlineDefault;
                outlineTo = on ? ModStyle.Gold : OutlineDefault;
                clock = 0f;
                if (background != null) background.color = on ? fillFocus : fillDefault;
                if (on) onShow?.Invoke(this);
            }

            private void Update()
            {
                if (clock >= 1f || background == null) return;
                clock = Mathf.Min(1f, clock + Time.unscaledDeltaTime / AnimTime);
                float t = Mathf.SmoothStep(0f, 1f, clock);
                background.transform.localScale = Vector3.one * Mathf.Lerp(scaleFrom, scaleTo, t);
                background.OutlineColor = Color.Lerp(outlineFrom, outlineTo, t);
                background.OutlineWidth = Mathf.Lerp(1.5f, 2.5f, outlineTo == ModStyle.Gold ? t : 1f - t);
            }

            protected override void OnApply() { }
            protected override void OnClear() => Animate(SelectionState.Default);
            protected override void OnFocus() => Animate(SelectionState.Focussed);
            protected override void OnSelect() => Animate(SelectionState.Selected);
            protected override void OnFocusAndSelect() => Animate(SelectionState.FocussedAndSelected);

            protected override void OnHardStateSet(SelectionState selectionState)
            {
                Animate(selectionState);
                clock = 1f;
                if (background == null) return;
                bool on = selectionState != SelectionState.Default;
                background.transform.localScale = Vector3.one * (on ? 1.04f : 1f);
                background.OutlineColor = on ? ModStyle.Gold : OutlineDefault;
                background.OutlineWidth = on ? 2.5f : 1.5f;
            }
        }
    }
}
