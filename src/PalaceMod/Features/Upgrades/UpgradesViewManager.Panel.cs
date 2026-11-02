using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    The panel shown while the king stands next to a building (daytime, no menu open): the building's level, the
    choices taken so far, the next upgrade's options with their cost (greyed while you can't afford it) and, if
    enabled, the options of the levels after that. Built from the game's own pieces: the pause tooltip's panel and
    fonts, and the upgrade-choice popup's tile for each option, so it reads like the game's choice screen.
*/
namespace PalaceMod
{
    public partial class UpgradesViewManager
    {
        private const float TileSize = 58f, SmallTileSize = 40f, PanelGap = 18f;

        private RectTransform panel;
        private RectTransform content;
        private string panelKey;
        private bool panelFailed;

        private void UpdatePanel()
        {
            var slot = NearBuilding.Value ? NearbySlot() : null;
            if (slot == null)
            {
                HidePanel();
                return;
            }
            if (!EnsurePanel()) return;
            string key = PanelKey(slot);
            if (key != panelKey)
            {
                panelKey = key;
                Rebuild(slot);
            }
            if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
            Place();
        }

        private void HidePanel()
        {
            panelKey = null;
            if (panel != null && panel.gameObject.activeSelf) panel.gameObject.SetActive(false);
        }

        private void DestroyPanel()
        {
            if (panel != null) Object.Destroy(panel.gameObject);
            panel = null;
            content = null;
            panelKey = null;
        }

        /// <summary>The building the king is standing at, if the panel should show for it now.</summary>
        private static BuildSlot NearbySlot()
        {
            var king = PlayerInteraction.instance;
            if (king == null || UIFrameManager.instance == null || UIFrameManager.instance.ActiveFrame != null) return null;
            if (DayNightCycle.Instance == null || DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night) return null;
            if (BuildingInteractor.IsChoosingUpgrade) return null;
            if (TooltipManager.instance != null && TooltipManager.instance.hideAllTooltips) return null;
            if (SettingsManager.Instance != null && SettingsManager.Instance.DisableTooltips) return null;

            BuildingInteractor best = king.FocussedInteractor as BuildingInteractor;
            if (best == null)
            {
                // Fully upgraded buildings can't be focused, so look for them the way the game looks for interactors.
                var hits = Physics.OverlapSphere(king.transform.position, king.interactionRadius, king.interactorLayer);
                float bestDistance = float.PositiveInfinity;
                foreach (var hit in hits)
                {
                    var interactor = hit.GetComponent<BuildingInteractor>();
                    if (interactor == null || interactor.targetBuilding == null || interactor.targetBuilding.Level < 1) continue;
                    float d = Vector3.Distance(king.transform.position, hit.ClosestPoint(king.transform.position));
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = interactor;
                    }
                }
            }
            if (best == null || best.targetBuilding == null) return null;
            var slot = UpgradeInfo.Owner(best.targetBuilding);
            if (slot == null || slot.OwnUpgrades.Count == 0) return null;
            // Nothing to say about a one-level building that's built and has no choices.
            if (slot.Level >= 1 && UpgradeInfo.TakenChoices(slot).Count == 0 && !UpgradeInfo.HasRemaining(slot)) return null;
            return slot;
        }

        private string PanelKey(BuildSlot slot)
        {
            var next = UpgradeInfo.Next(slot);
            bool affordable = next == null || PlayerInteraction.instance.Balance >= slot.NextUpgradeOrBuildCost;
            return $"{slot.GetInstanceID()}|{slot.Level}|{string.Join(",", slot.AppliedUpgrades)}|{slot.NextUpgradeOrBuildCost}|{affordable}|{slot.CanBeUpgraded}|{ShowFuture.Value}";
        }

        // ---------- Construction ----------

        private bool EnsurePanel()
        {
            var hud = GameWidgets.HudTooltip;
            if (hud == null) return false;
            var parent = hud.transform.parent;
            if (panel != null && panel.parent == parent) return true;
            if (panel != null) Object.Destroy(panel.gameObject);
            panel = null;
            if (panelFailed) return false;
            var helper = GameWidgets.PauseTooltip;
            if (helper?.selectionTitle == null) return false;
            try
            {
                var background = helper.selectionTitle.GetComponentInParent<MPUIKIT.MPImage>(true)
                    ?? throw new InvalidOperationException("pause tooltip has no panel");
                var obj = GameWidgets.Clone(background, parent, "PalaceMod Upgrade Panel", keepChildren: false);
                foreach (var c in obj.GetComponents<Component>())
                {
                    if (c is LayoutGroup or ContentSizeFitter or LayoutElement) Object.DestroyImmediate(c);
                }
                var image = obj.GetComponent<MPUIKIT.MPImage>();
                image.raycastTarget = false;
                ModStyle.MarkPanel(image);
                var rect = (RectTransform)obj.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 0f);
                rect.localScale = Vector3.one;
                var layout = obj.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(22, 22, 16, 18);
                layout.spacing = 6f;
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = layout.childForceExpandHeight = false;
                var fitter = obj.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var emblem = ModStyle.AddEmblem(obj.transform, 9f);
                emblem.anchorMin = emblem.anchorMax = new Vector2(1f, 1f);
                emblem.anchoredPosition = new Vector2(-12f, -12f);
                panel = rect;
                content = rect;
                obj.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                panelFailed = true;
                Plugin.Logger.LogError($"Upgrade info: couldn't build the building panel: {e}");
                return false;
            }
        }

        private void Rebuild(BuildSlot slot)
        {
            foreach (Transform child in content.Cast<Transform>().ToList())
            {
                if (child.GetComponent<LayoutElement>()?.ignoreLayout == true) continue; // the emblem
                Object.Destroy(child.gameObject);
                child.SetParent(null, false);
            }

            int maxLevel = slot.OwnUpgrades.Count(u => !u.disableInThisMode);
            string title = UpgradeInfo.BuildingName(slot);
            string level = slot.Level < 1 ? "Not built" : $"Level {slot.Level}/{maxLevel}";
            AddText(content, $"{title}  <color=#{ModStyle.Hex(ModStyle.Dim)}><size=80%>{level}</size></color>", ModStyle.BoldFont, 27f, ModStyle.Gold);

            var taken = UpgradeInfo.TakenChoices(slot);
            if (taken.Count > 0)
            {
                foreach (var (step, choice) in taken)
                {
                    var row = AddRow(content, 10f);
                    AddTile(row, choice, TileSize * 0.7f, dim: false);
                    AddText(row, $"<color=#{ModStyle.Hex(ModStyle.Dim)}>Lv {step.Level}</color>  {UpgradeInfo.ChoiceName(slot, choice)}",
                        ModStyle.BodyFont, 24f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
                }
            }

            var next = UpgradeInfo.Next(slot);
            if (next is { } n)
            {
                int cost = slot.NextUpgradeOrBuildCost;
                int balance = PlayerInteraction.instance != null ? PlayerInteraction.instance.Balance : 0;
                bool affordable = balance >= cost;
                string label = slot.Level < 1 ? "Build" : "Next";
                string costText = affordable
                    ? $"{cost}<sprite name=\"coin\">"
                    : $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{cost}<sprite name=\"coin\">  (need {cost - balance} more)</color>";
                AddSectionLabel(content, $"{label} · {costText}");
                AddOptions(slot, n, TileSize, dim: !affordable, withNames: true);
            }
            else if (UpgradeInfo.HasRemaining(slot))
            {
                var blocked = new UpgradeInfo.Step { Level = slot.Level + 1, Upgrade = slot.OwnUpgrades[slot.Level], BranchIndex = -1 };
                AddSectionLabel(content, $"Next · <color=#{ModStyle.Hex(ModStyle.Dim)}>needs a higher castle level</color>");
                AddOptions(slot, blocked, TileSize, dim: true, withNames: true);
            }

            if (ShowFuture.Value)
            {
                var later = UpgradeInfo.Future(slot);
                if (later.Count > 0)
                {
                    AddSectionLabel(content, "Later");
                    foreach (var step in later)
                    {
                        var row = AddRow(content, 6f);
                        AddText(row, $"Lv {step.Level}", ModStyle.BodyFont, 22f, ModStyle.Dim, TextAlignmentOptions.MidlineLeft, width: 52f);
                        if (step.IsChoice)
                        {
                            foreach (var branch in step.Upgrade.upgradeBranches)
                                AddTile(row, branch.choiceDetails, SmallTileSize, dim: true);
                        }
                        else
                        {
                            AddText(row, Shorten(UpgradeInfo.StepText(slot, step)), ModStyle.BodyFont, 22f, ModStyle.Dim, TextAlignmentOptions.MidlineLeft);
                        }
                    }
                }
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
        }

        private void AddOptions(BuildSlot slot, UpgradeInfo.Step step, float size, bool dim, bool withNames)
        {
            if (!step.IsChoice)
            {
                string text = UpgradeInfo.StepText(slot, step);
                if (!string.IsNullOrEmpty(text))
                    AddText(content, Shorten(text), ModStyle.BodyFont, 23f, dim ? ModStyle.Dim : ModStyle.Gold, width: 0f, wrapWidth: 360f);
                return;
            }
            var row = AddRow(content, 10f);
            foreach (var branch in step.Upgrade.upgradeBranches)
            {
                var choice = branch.choiceDetails;
                var column = new GameObject("Option", typeof(RectTransform));
                column.transform.SetParent(row, false);
                var v = column.AddComponent<VerticalLayoutGroup>();
                v.spacing = 2f;
                v.childAlignment = TextAnchor.UpperCenter;
                v.childControlWidth = v.childControlHeight = true;
                v.childForceExpandWidth = v.childForceExpandHeight = false;
                bool locked = UpgradeInfo.IsLocked(choice);
                AddTile(column.transform, choice, size, dim);
                if (withNames)
                {
                    string name = locked ? "Locked" : UpgradeInfo.ChoiceName(slot, choice);
                    AddText(column.transform, name, ModStyle.BodyFont, 19f, dim || locked ? ModStyle.Dim : ModStyle.Gold,
                        TextAlignmentOptions.Top, width: 96f, wrapWidth: 96f);
                }
            }
        }

        private static string Shorten(string text)
        {
            // The game's step texts can be two lines; the panel only needs the first.
            text = text.Replace("\r", "");
            int nl = text.IndexOf('\n');
            return nl > 0 ? text.Substring(0, nl) : text;
        }

        private static RectTransform AddRow(Transform parent, float spacing)
        {
            var obj = new GameObject("Row", typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var h = obj.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            return (RectTransform)obj.transform;
        }

        private void AddSectionLabel(Transform parent, string text)
        {
            var tmp = AddText(parent, text, ModStyle.BoldFont, 22f, ModStyle.Gold);
            tmp.margin = new Vector4(0f, 6f, 0f, 0f);
        }

        private static TextMeshProUGUI AddText(Transform parent, string text, TMP_FontAsset font, float size, Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft, float width = 0f, float wrapWidth = 0f)
        {
            var tmp = GameWidgets.Text(parent, "Text", text, font, size, color, alignment);
            tmp.enableWordWrapping = wrapWidth > 0f;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var le = tmp.gameObject.AddComponent<LayoutElement>();
            if (width > 0f) le.preferredWidth = width;
            else if (wrapWidth > 0f) le.preferredWidth = Mathf.Min(wrapWidth, tmp.GetPreferredValues(text, 10000f, 100f).x + 2f);
            return tmp;
        }

        // ---------- Choice tiles ----------

        private static TFUIUpgradeChoice tilePrefab;

        private static TFUIUpgradeChoice TilePrefab
        {
            get
            {
                if (tilePrefab == null)
                    tilePrefab = Resources.FindObjectsOfTypeAll<ChoiceUIFrameHelper>().Select(h => h.upgradeChoiceButtonPrefab).FirstOrDefault(p => p != null);
                return tilePrefab;
            }
        }

        /// <summary>A copy of the choice popup's tile showing <paramref name="choice"/>, scaled to <paramref name="size"/>.</summary>
        internal static RectTransform AddTile(Transform parent, Choice choice, float size, bool dim)
        {
            var holder = new GameObject("Tile", typeof(RectTransform), typeof(LayoutElement), typeof(CanvasGroup));
            holder.transform.SetParent(parent, false);
            var le = holder.GetComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = le.minWidth = le.minHeight = size;
            holder.GetComponent<CanvasGroup>().alpha = dim ? 0.5f : 1f;
            holder.GetComponent<CanvasGroup>().blocksRaycasts = false;
            var holderRect = (RectTransform)holder.transform;
            holderRect.sizeDelta = new Vector2(size, size);

            bool locked = UpgradeInfo.IsLocked(choice);
            var prefab = TilePrefab;
            if (prefab != null)
            {
                var tile = GameWidgets.Clone(prefab, holder.transform, "Choice");
                var icon = GameWidgets.Corresponding(prefab.transform, Traverse.Create(prefab).Field("iconImg").GetValue<Image>().transform, tile.transform).GetComponent<Image>();
                var bgSource = Traverse.Create(prefab).Field("backgroundImg").GetValue<MPUIKIT.MPImageBasic>();
                var bg = bgSource != null ? GameWidgets.Corresponding(prefab.transform, bgSource.transform, tile.transform).GetComponent<MPUIKIT.MPImageBasic>() : null;
                if (locked)
                {
                    icon.sprite = Traverse.Create(prefab).Field("lockIcon").GetValue<Sprite>();
                    var lockedOutline = Traverse.Create(prefab).Field("lockedOutlineColor").GetValue<Color>();
                    icon.color = lockedOutline;
                    if (bg != null)
                    {
                        bg.color = Traverse.Create(prefab).Field("lockedBgColor").GetValue<Color>();
                        bg.OutlineColor = lockedOutline;
                    }
                }
                else icon.sprite = UpgradeInfo.Icon(choice);
                foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                var rect = (RectTransform)tile.transform;
                float native = Mathf.Max(rect.rect.width, rect.rect.height, 1f);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.localScale = Vector3.one * (size / native);
                tile.SetActive(true);
            }
            else
            {
                // No popup prefab loaded (shouldn't happen in a level): a bare icon.
                var img = holder.AddComponent<Image>();
                img.sprite = UpgradeInfo.Icon(choice);
                img.preserveAspect = true;
                img.raycastTarget = false;
            }
            return holderRect;
        }

        // ---------- Placement ----------

        private void Place()
        {
            var hud = GameWidgets.HudTooltip;
            if (hud == null) return;
            var canvas = (RectTransform)panel.parent;
            var tooltipRect = (RectTransform)hud.transform;
            var size = panel.rect.size;
            var bounds = canvas.rect;
            var corners = new Vector3[4];
            tooltipRect.GetWorldCorners(corners);
            Vector2 min = canvas.InverseTransformPoint(corners[0]), max = canvas.InverseTransformPoint(corners[2]);
            bool tooltipShown = tooltipRect.localScale.x > 0.5f && !string.IsNullOrEmpty(hud.currentText);
            Vector2 pos;
            if (tooltipShown) pos = new Vector2(max.x + PanelGap, min.y);   // right of the game's tooltip, bottoms aligned
            else pos = new Vector2(min.x, min.y);                             // where the game's tooltip would be
            pos.x = Mathf.Clamp(pos.x, bounds.xMin + 16f, bounds.xMax - 16f - size.x);
            pos.y = Mathf.Clamp(pos.y, bounds.yMin + 16f, bounds.yMax - 16f - size.y);
            panel.localPosition = new Vector3(pos.x, pos.y, 0f);
        }
    }
}
