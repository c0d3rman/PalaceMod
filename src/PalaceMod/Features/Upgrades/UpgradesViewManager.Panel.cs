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
        private bool showDetails; // the details key is held: every option's description is written out
        private static bool forceDetails; // dev: as if the details key were held
        // What each hover target says, for the dev command that shows it without a mouse.
        private static readonly List<(RectTransform Target, string Title, Func<string> Body)> hoverTargets = [];

        private void RegisterPanelDevCommands()
        {
            DevCommands.Register("upgv.details", "upgv.details [on|off]  show the building panel as if the details key were held", args =>
            {
                forceDetails = !(args.Length > 0 && args[0] == "off");
                return forceDetails ? "details on" : "details off";
            });
            DevCommands.Register("upgv.hover", "upgv.hover [n|off]     show option n's hover tooltip in the building panel (no arg: list them)", args =>
            {
                hoverTargets.RemoveAll(t => t.Target == null);
                if (args.Length == 0)
                    return $"cursor visible={Cursor.visible}\n" + string.Join("\n", hoverTargets.Select((t, i) => $"{i + 1}: {t.Title}"));
                if (args[0] == "off") { ModTooltip.Hide(); return "hidden"; }
                int n = int.Parse(args[0]) - 1;
                if (n < 0 || n >= hoverTargets.Count) throw new ArgumentException($"there are {hoverTargets.Count} options");
                var (target, title, body) = hoverTargets[n];
                ModTooltip.Show(title, body(), target);
                return title;
            });
        }

        private void UpdatePanel()
        {
            var slot = NearBuilding.Value ? NearbySlot() : null;
            if (slot == null)
            {
                HidePanel();
                return;
            }
            if (!EnsurePanel()) return;
            showDetails = DetailsKey.Held || forceDetails;
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
            if (Time.timeScale == 0f || (GameWidgets.PauseFrame != null && GameWidgets.PauseFrame.gameObject.activeInHierarchy)) return null;
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
            // The deficit is shown ("need 6 more"), so it's part of the key; once affordable the balance no longer matters.
            int cost = slot.NextUpgradeOrBuildCost;
            int deficit = UpgradeInfo.Next(slot) == null ? 0 : Mathf.Max(0, cost - Purse(slot));
            return $"{slot.GetInstanceID()}|{slot.Level}|{string.Join(",", slot.AppliedUpgrades)}|{cost}|{deficit}|{slot.CanBeUpgraded}|{ShowFuture.Value}|{showDetails}";
        }

        private static readonly AccessTools.FieldRef<CostDisplay, int> FilledCoins =
            AccessTools.FieldRefAccess<CostDisplay, int>("currentlyFilledCoins");

        /// <summary>Coins the king has toward the slot's next step, counting ones already paid into it while holding
        /// Interact (the game takes them from the balance one at a time).</summary>
        private static int Purse(BuildSlot slot)
        {
            var king = PlayerInteraction.instance;
            if (king == null) return 0;
            int paid = 0;
            var interactor = slot.buildingInteractor;
            if (interactor != null && interactor.costDisplay != null)
                paid = FilledCoins(interactor.costDisplay);
            return king.Balance + paid;
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
                obj.AddComponent<LayoutElement>().ignoreLayout = true; // in case the HUD parent lays out its children
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
                Log.For<UpgradesViewManager>().Error($"Couldn't build the building panel", e);
                return false;
            }
        }

        private void Rebuild(BuildSlot slot)
        {
            hoverTargets.Clear();
            foreach (Transform child in content.Cast<Transform>().ToList())
            {
                if (child.name == "PalaceMod Emblem") continue;
                Object.Destroy(child.gameObject);
                child.SetParent(null, false);
            }

            int maxLevel = UpgradeInfo.MaxLevel(slot);
            string title = UpgradeInfo.BuildingName(slot);
            string level = slot.Level < 1 ? "Not built" : $"Level {slot.Level}/{maxLevel}";
            AddText(content, $"{title}  <color=#{ModStyle.Hex(ModStyle.Dim)}><size=80%>{level}</size></color>", ModStyle.BoldFont, 27f, ModStyle.Gold);

            var taken = UpgradeInfo.TakenChoices(slot);
            if (taken.Count > 0)
            {
                foreach (var (step, choice) in taken)
                {
                    var row = AddRow(content, 10f);
                    AddTile(row, choice, TileSize * 0.7f, dim: false, taken: true);
                    AddText(row, $"<color=#{ModStyle.Hex(ModStyle.Dim)}>Lv {step.Level}</color>  {UpgradeInfo.ChoiceName(slot, choice)}",
                        ModStyle.BodyFont, 24f, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
                    AttachDescription(row.gameObject, slot, choice, taken: true);
                    if (showDetails) AddDescription(content, slot, choice, 380f, taken: true);
                }
            }

            var next = UpgradeInfo.Next(slot);
            if (next is { } n)
            {
                int cost = slot.NextUpgradeOrBuildCost;
                int balance = Purse(slot);
                bool affordable = balance >= cost;
                string label = slot.Level < 1 ? "Build" : "Next";
                string costText = affordable
                    ? $"{cost}<sprite name=\"coin\">"
                    : $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{cost}<sprite name=\"coin\">  (need {cost - balance} more)</color>";
                AddSectionLabel(content, $"{label} · {costText}");
                // A single-branch step is already described by the game's tooltip right beside the panel.
                if (n.IsChoice) AddOptions(slot, n, TileSize, dim: !affordable, withNames: true);
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
                                AttachDescription(AddTile(row, branch.choiceDetails, SmallTileSize, dim: true).gameObject, slot, branch.choiceDetails);
                        }
                        else
                        {
                            AddText(row, Shorten(UpgradeInfo.StepText(slot, step)), ModStyle.BodyFont, 22f, ModStyle.Dim, TextAlignmentOptions.MidlineLeft);
                        }
                    }
                }
            }

            if (!showDetails && HasChoices(slot))
            {
                var key = DetailsKey.Shortcut.MainKey;
                string hint = key == KeyCode.None ? "Hover an option to see what it does" : $"Hover an option or hold {KeyName(key)} for details";
                var tmp = AddText(content, hint, ModStyle.ButtonFont, 18f, ModStyle.Dim);
                tmp.margin = new Vector4(0f, 6f, 0f, 0f);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
        }

        // Whether the panel shows any upgrade choice (taken, next or later), i.e. anything with a description.
        private bool HasChoices(BuildSlot slot)
        {
            if (UpgradeInfo.TakenChoices(slot).Count > 0) return true;
            if (slot.Level < slot.OwnUpgrades.Count && !slot.OwnUpgrades[slot.Level].disableInThisMode && slot.OwnUpgrades[slot.Level].upgradeBranches.Count > 1) return true;
            return ShowFuture.Value && UpgradeInfo.Future(slot).Any(s => s.IsChoice);
        }

        private static string KeyName(KeyCode key) => key switch
        {
            KeyCode.LeftShift or KeyCode.RightShift => "Shift",
            KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
            KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
            >= KeyCode.Alpha0 and <= KeyCode.Alpha9 => ((int)(key - KeyCode.Alpha0)).ToString(),
            _ => key.ToString(),
        };

        /// <summary>What an upgrade choice does, in the game's own words (or how to unlock it).</summary>
        internal static string DescriptionText(BuildSlot slot, Choice choice, bool taken = false)
        {
            if (choice == null) return "";
            if (!taken && UpgradeInfo.IsLocked(choice))
                return choice.disabledInThisMode ? "Not available in this mode." : choice.requiresUnlocked != null ? choice.requiresUnlocked.GetLockedTooltip() : "Locked.";
            return UpgradeInfo.ChoiceDescription(slot, choice);
        }

        private static string DescriptionTitle(BuildSlot slot, Choice choice, bool taken = false) =>
            !taken && UpgradeInfo.IsLocked(choice) ? "Locked" : UpgradeInfo.ChoiceName(slot, choice);

        // Mouse hover over an option shows its name and description in the mod tooltip.
        private static void AttachDescription(GameObject target, BuildSlot slot, Choice choice, bool taken = false)
        {
            if (choice == null) return;
            string title = DescriptionTitle(slot, choice, taken);
            Func<string> body = () => slot == null ? "" : DescriptionText(slot, choice, taken);
            ModTooltip.AttachHover(target, title, body);
            hoverTargets.Add(((RectTransform)target.transform, title, body));
        }

        private static void AddDescription(Transform parent, BuildSlot slot, Choice choice, float width, bool taken = false)
        {
            string text = DescriptionText(slot, choice, taken);
            if (string.IsNullOrEmpty(text)) return;
            var tmp = AddText(parent, text, ModStyle.BodyFont, 19f, ModStyle.Gold, TextAlignmentOptions.TopLeft, wrapWidth: width);
            tmp.characterSpacing = 0f;
        }

        private void AddOptions(BuildSlot slot, UpgradeInfo.Step step, float size, bool dim, bool withNames)
        {
            if (!step.IsChoice)
            {
                string text = UpgradeInfo.StepText(slot, step);
                if (!string.IsNullOrEmpty(text))
                    AddText(content, Shorten(text), ModStyle.BodyFont, 23f, dim ? ModStyle.Dim : ModStyle.Gold);
                return;
            }
            var row = AddRow(content, 10f);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft; // descriptions differ in length
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
                float width = showDetails ? 190f : 120f;
                if (withNames)
                {
                    string name = locked ? "Locked" : UpgradeInfo.ChoiceName(slot, choice);
                    AddText(column.transform, name, ModStyle.BodyFont, 19f, dim || locked ? ModStyle.Dim : ModStyle.Gold,
                        TextAlignmentOptions.Top, width: width, wrapWidth: width);
                }
                if (showDetails)
                {
                    var tmp = AddText(column.transform, DescriptionText(slot, choice), ModStyle.BodyFont, 17f, ModStyle.Gold,
                        TextAlignmentOptions.Top, width: width, wrapWidth: width);
                    tmp.characterSpacing = 0f;
                    tmp.color = new Color(ModStyle.Gold.r, ModStyle.Gold.g, ModStyle.Gold.b, dim || locked ? 0.6f : 0.9f);
                }
                AttachDescription(column, slot, choice);
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

        private static GameObject tileStaging;

        /// <summary>
        /// A visual-only copy of the popup's tile. GameWidgets.Clone can't strip it: the tile's TFUIAudioHelper
        /// requires the TFUIUpgradeChoice it would remove, so drop the helper first, then the game's other scripts.
        /// </summary>
        private static GameObject CloneTile(TFUIUpgradeChoice prefab, Transform parent)
        {
            if (tileStaging == null)
            {
                tileStaging = new GameObject("PalaceMod Tile Staging");
                tileStaging.SetActive(false);
                Object.DontDestroyOnLoad(tileStaging);
            }
            var obj = Object.Instantiate(prefab.gameObject, tileStaging.transform, false);
            obj.name = "Choice";
            foreach (var helper in obj.GetComponentsInChildren<TFUIAudioHelper>(true)) Object.DestroyImmediate(helper);
            var gameAssembly = typeof(TFUIUpgradeChoice).Assembly;
            foreach (var behaviour in obj.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().Assembly == gameAssembly) Object.DestroyImmediate(behaviour);
            }
            obj.transform.SetParent(parent, false);
            return obj;
        }

        /// <summary>A copy of the choice popup's tile showing <paramref name="choice"/>, scaled to <paramref name="size"/>.</summary>
        internal static RectTransform AddTile(Transform parent, Choice choice, float size, bool dim, bool taken = false)
        {
            var holder = new GameObject("Tile", typeof(RectTransform), typeof(LayoutElement), typeof(CanvasGroup));
            holder.transform.SetParent(parent, false);
            var le = holder.GetComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = le.minWidth = le.minHeight = size;
            holder.GetComponent<CanvasGroup>().alpha = dim ? 0.4f : 1f;
            holder.GetComponent<CanvasGroup>().blocksRaycasts = false;
            var holderRect = (RectTransform)holder.transform;
            holderRect.sizeDelta = new Vector2(size, size);

            bool locked = !taken && UpgradeInfo.IsLocked(choice); // a choice already taken shows its icon either way
            var prefab = TilePrefab;
            if (prefab != null)
            {
                var tile = CloneTile(prefab, holder.transform);
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
            // Right of the game's tooltip with tops aligned; where the tooltip would be when it's hidden (a building
            // with nothing left to buy has none).
            pos = tooltipShown ? new Vector2(max.x + PanelGap, max.y - size.y) : new Vector2(min.x, max.y - size.y);
            pos.x = Mathf.Clamp(pos.x, bounds.xMin + 16f, bounds.xMax - 16f - size.x);
            pos.y = Mathf.Clamp(pos.y, bounds.yMin + 16f, bounds.yMax - 16f - size.y);
            panel.localPosition = new Vector3(pos.x, pos.y, 0f);
        }
    }
}
