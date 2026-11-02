using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    Floats a small coin + amount label over every building that earns gold, while build previews are shown and/or
    while a hold key is down. Amounts come from IncomeCalculator (what each building pays at the coming dawn). Slots
    that upgrade together with their activator (a mill's fields) are summed into the activator's label. Labels only
    show by day, so knock-outs (a night-only state) don't apply; Denied is kept for completeness.

    Labels live on one screen-space canvas, are pooled, and are sized from the camera's pixels per world unit at
    each building, so they follow zoom within readable bounds.
*/
namespace PalaceMod
{
    public class IncomeLabelManager : BaseManager
    {
        public override string FeatureSection => "Income labels";
        public override string FeatureDescription => "Shows how much gold each building pays at dawn, above the building.";

        private ConfigEntry<bool> withPreviews;
        private ConfigEntry<float> sizeSetting;
        private Keybind holdKey;

        private Canvas canvas;
        private TMP_FontAsset font;
        private Material fontMaterial;
        private Sprite coinSprite;
        private bool styleResolved;
        private readonly List<Label> pool = [];
        private readonly Dictionary<BuildSlot, Entry> entries = [];
        private readonly List<Entry> entryList = [];
        private int lastBuildFrame = -1;
        private bool hintShown;

        private sealed class Label
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public TextMeshProUGUI Text;
            public int Amount = int.MinValue;
        }

        private sealed class Entry
        {
            public BuildSlot Slot;
            public BuildingInteractor Interactor;
            public int Amount;
            public bool Denied;
            public bool Focussed;
        }

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            withPreviews = plugin.Config.Bind(FeatureSection, "With build previews", true,
                "Show income labels whenever build previews are shown (holding or toggling the preview button).");
            sizeSetting = plugin.Config.Bind(FeatureSection, "Label size", 1f, new ConfigDescription(
                "How big the labels are.", new AcceptableValueRange<float>(0.5f, 2f), new SettingTags { Step = 0.1f, Unit = "x" }));
            holdKey = Keybinds.Register(FeatureSection, "show", "Show income", new KeyboardShortcut(KeyCode.LeftAlt),
                "Hold to show each building's income.");
            DevCommands.Register("incomeprobe", "incomeprobe   dump building interactors", _ => Probe());
            DevCommands.Register("incomelabels", "incomelabels   list the income labels currently computed", _ => Describe());
        }

        protected override void OnFeatureDisabled() => HideAll();

        public override void OnDestroy()
        {
            if (canvas != null) Object.Destroy(canvas.gameObject);
        }

        private bool ShouldShow()
        {
            if (TagManager.instance == null || DayNightCycle.Instance == null) return false;
            if (DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Day) return false;
            if (LocalGamestate.Instance == null || LocalGamestate.Instance.CurrentState != LocalGamestate.State.InMatch) return false;
            if (UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame != null) return false;
            if (holdKey.Held) return true;
            return withPreviews.Value && BuildingInteractor.displayAllBuildPreviews;
        }

        public override void Update()
        {
            if (!ShouldShow())
            {
                HideAll();
                return;
            }
            var cam = Camera.main;
            if (cam == null) { HideAll(); return; }
            if (!EnsureCanvas()) return;

            if (!hintShown && holdKey.Held)
            {
                hintShown = true;
            }

            Collect();
            int used = 0;
            float sizeMul = sizeSetting.Value;
            foreach (var e in entryList)
            {
                if (e.Amount <= 0 || e.Focussed) continue;
                Vector3 anchor = Anchor(e);
                Vector3 screen = cam.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;
                // Pixels per world unit at this depth, so labels shrink and grow with the camera zoom.
                float ppu = (cam.WorldToScreenPoint(anchor + cam.transform.up) - screen).magnitude;
                float scale = Mathf.Clamp(ppu / 40f, 0.55f, 1.6f) * sizeMul * Screen.height / 1080f;

                var label = GetLabel(used++);
                if (label.Amount != e.Amount)
                {
                    label.Amount = e.Amount;
                    label.Text.text = coinSprite == null ? $"<sprite name=coin>{e.Amount}" : e.Amount.ToString();
                }
                label.Root.position = new Vector3(Mathf.Round(screen.x), Mathf.Round(screen.y), 0f);
                label.Root.localScale = new Vector3(scale, scale, 1f);
                label.Group.alpha = e.Denied ? 0.45f : 1f;
                if (!label.Root.gameObject.activeSelf) label.Root.gameObject.SetActive(true);
            }
            for (int i = used; i < pool.Count; i++)
            {
                if (pool[i].Root.gameObject.activeSelf) pool[i].Root.gameObject.SetActive(false);
            }
        }

        // Where a label sits: just above the building's harvest cue, which the game already places over the roof.
        private static Vector3 Anchor(Entry e)
        {
            if (e.Interactor.harvestCue != null) return e.Interactor.harvestCue.transform.position;
            return e.Interactor.transform.position + Vector3.up * 3f;
        }

        private static BuildSlot Root(BuildSlot slot)
        {
            int guard = 0;
            while (slot.ActivatorUpgradesThis && slot.ActivatorBuilding != null && guard++ < 16) slot = slot.ActivatorBuilding;
            return slot;
        }

        private void Collect()
        {
            if (lastBuildFrame == Time.frameCount) return;
            lastBuildFrame = Time.frameCount;
            foreach (var e in entryList)
            {
                e.Amount = 0;
                e.Denied = false;
                e.Focussed = false;
            }
            foreach (var income in IncomeCalculator.All())
            {
                if (income.IncomeIfStanding <= 0) continue;
                var slot = income.Slot;
                var root = Root(slot);
                if (!entries.TryGetValue(root, out var e))
                {
                    e = new Entry { Slot = root };
                    entries[root] = e;
                    entryList.Add(e);
                }
                var bi = slot.buildingInteractor;
                if (e.Interactor == null || root == slot) e.Interactor = root.buildingInteractor != null ? root.buildingInteractor : bi;
                e.Amount += income.IncomeIfStanding;
                if (income.KnockedOut) e.Denied = true;
                if (bi != null && bi.Focussed) e.Focussed = true;
            }
            // Drop buildings from a previous level.
            for (int i = entryList.Count - 1; i >= 0; i--)
            {
                if (entryList[i].Slot == null || entryList[i].Interactor == null)
                {
                    entries.Remove(entryList[i].Slot);
                    entryList.RemoveAt(i);
                }
            }
        }

        private void HideAll()
        {
            foreach (var l in pool)
            {
                if (l.Root != null && l.Root.gameObject.activeSelf) l.Root.gameObject.SetActive(false);
            }
        }

        private bool EnsureCanvas()
        {
            if (!styleResolved) ResolveStyle();
            if (font == null) return false;
            if (canvas != null) return true;
            var go = new GameObject("PalaceMod Income Labels");
            Object.DontDestroyOnLoad(go);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Under the game's HUD and menus.
            canvas.sortingOrder = -50;
            pool.Clear();
            return true;
        }

        // Borrow the look of the game's own cost display: its numeral font and its coin.
        private void ResolveStyle()
        {
            foreach (var bi in TagManager.instance.playerBuildingInteractors)
            {
                if (bi == null || bi.costDisplay == null) continue;
                var amount = bi.costDisplay.amountDisplay;
                if (amount != null)
                {
                    font = amount.font;
                    fontMaterial = amount.fontSharedMaterial;
                }
                var coin = bi.costDisplay.coinPrefab;
                if (coin != null)
                {
                    var img = coin.GetComponentInChildren<Image>(true);
                    if (img != null) coinSprite = img.sprite;
                }
                break;
            }
            if (font == null) font = TMP_Settings.defaultFontAsset;
            styleResolved = font != null;
        }

        private Label GetLabel(int index)
        {
            while (pool.Count <= index) pool.Add(CreateLabel());
            return pool[index];
        }

        private Label CreateLabel()
        {
            var go = new GameObject("Income label", typeof(RectTransform), typeof(CanvasGroup));
            var root = (RectTransform)go.transform;
            root.SetParent(canvas.transform, false);
            root.sizeDelta = new Vector2(80f, 32f);
            var group = go.GetComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 3f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            if (coinSprite != null)
            {
                var icon = new GameObject("Coin", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                icon.transform.SetParent(root, false);
                var img = icon.GetComponent<Image>();
                img.sprite = coinSprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                var le = icon.GetComponent<LayoutElement>();
                le.preferredWidth = 24f;
                le.preferredHeight = 24f;
            }

            var textGo = new GameObject("Amount", typeof(RectTransform));
            textGo.transform.SetParent(root, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.font = font;
            if (fontMaterial != null) text.fontSharedMaterial = fontMaterial;
            text.fontSize = 28f;
            text.color = new Color(1f, 0.92f, 0.6f);
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            text.outlineWidth = 0.25f;
            text.outlineColor = new Color32(30, 20, 10, 255);

            go.SetActive(false);
            return new Label { Root = root, Group = group, Text = text };
        }

        private string Describe()
        {
            if (TagManager.instance == null) return "not in a level";
            lastBuildFrame = -1;
            Collect();
            var sb = new StringBuilder();
            sb.AppendLine($"shown={ShouldShow()} previews={BuildingInteractor.displayAllBuildPreviews} font={font?.name} coin={coinSprite?.name}");
            int total = 0;
            foreach (var e in entryList)
            {
                if (e.Amount <= 0) continue;
                total += e.Amount;
                sb.AppendLine($"  {e.Slot.name} ({e.Slot.LOCIDENTIFIER_NAME} lvl {e.Slot.Level}): {e.Amount}{(e.Denied ? " denied" : "")}");
            }
            sb.Append($"total={total}");
            return sb.ToString();
        }

        private static string PathOf(Transform t) => t == null ? "null" : (t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name);

        private static string Probe()
        {
            var sb = new StringBuilder();
            var cam = Camera.main;
            sb.AppendLine($"cam {cam?.name} ortho={cam?.orthographic} fov={cam?.fieldOfView} size={cam?.orthographicSize} pos={cam?.transform.position} rot={cam?.transform.eulerAngles}");
            sb.AppendLine($"tmp default sprite asset {TMP_Settings.defaultSpriteAsset?.name}");
            int n = 0;
            foreach (var bi in TagManager.instance.playerBuildingInteractors)
            {
                var s = bi.targetBuilding;
                sb.AppendLine($"{PathOf(bi.transform)} slot={s.name} loc={s.LOCIDENTIFIER_NAME} lvl={s.Level} inc={s.GoldIncome} state={s.State} harvestable={bi.canBeHarvested} act={(s.ActivatorBuilding ? s.ActivatorBuilding.name : "-")} actUp={s.ActivatorUpgradesThis} mods={bi.IncomeModifiers.Count} pos={bi.transform.position} harvestCue={bi.harvestCue?.transform.position} cost={bi.costDisplay?.transform.position}");
                if (n++ == 0 && bi.costDisplay != null)
                {
                    var cd = bi.costDisplay;
                    var c = cd.GetComponentInParent<Canvas>();
                    sb.AppendLine($"  costDisplay {PathOf(cd.transform)} scale={cd.transform.lossyScale} rot={cd.transform.eulerAngles} canvas={c?.name} mode={c?.renderMode} sort={c?.sortingOrder} layer={cd.gameObject.layer}");
                    var t = cd.amountDisplay;
                    if (t != null) sb.AppendLine($"  amount font={t.font?.name} mat={t.fontSharedMaterial?.name} size={t.fontSize} color={t.color} sprite={t.spriteAsset?.name} text='{t.text}'");
                    foreach (var img in cd.coinPrefab.GetComponentsInChildren<Image>(true))
                        sb.AppendLine($"  coin img {img.name} sprite={img.sprite?.name} color={img.color} size={img.rectTransform.sizeDelta}");
                    foreach (var comp in bi.harvestCue.GetComponentsInChildren<Component>(true))
                        sb.AppendLine($"  harvestCue comp {comp.name}:{comp.GetType().Name}");
                }
            }
            foreach (var cv in Object.FindObjectsOfType<Canvas>())
                if (cv.isRootCanvas) sb.AppendLine($"canvas {cv.name} mode={cv.renderMode} sort={cv.sortingOrder}");
            return sb.ToString();
        }
    }
}
