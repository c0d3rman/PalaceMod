using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

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
        private ConfigEntry<bool> groupFields;
        private const string FieldName = "Building/Field";
        private Keybind holdKey;

        private Canvas canvas;
        private TMP_FontAsset font;
        private Material fontMaterial;
        private bool styleResolved;
        private readonly List<Label> pool = [];
        private readonly Dictionary<BuildSlot, Entry> entries = [];
        private readonly List<Entry> entryList = [];
        private int lastBuildFrame = -1;

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
            groupFields = plugin.Config.Bind(FeatureSection, "Group fields", true,
                "Add each field's gold to its mill's label instead of labelling every field.");
            // Unbound by default: the game's own preview button (Alt) already shows labels via "With build previews".
            holdKey = Keybinds.Register(FeatureSection, "show", "Show income", KeyboardShortcut.Empty,
                "Hold to show each building's income without build previews.");
            DevCommands.Register("buildall", "buildall [passes]   build every available slot for free (random choices), for testing", args =>
            {
                var player = PlayerInteraction.instance;
                if (player == null) throw new System.InvalidOperationException("not in a level");
                int passes = args.Length > 0 ? int.Parse(args[0]) : 1;
                int built = 0;
                for (int p = 0; p < passes; p++)
                {
                    foreach (var slot in Object.FindObjectsOfType<BuildSlot>())
                    {
                        if (slot.Level != 0 || slot.ActivatorUpgradesThis || !slot.CanBeUpgraded || !slot.gameObject.activeInHierarchy) continue;
                        slot.TryToBuildOrUpgradeAndPay(player, false);
                        built++;
                    }
                }
                if (built > 0) GameplayImpact.Mark("Dev: free buildings");
                return $"built {built}";
            });
            DevCommands.Register("zoom", "zoom <step>   set the camera's zoom step like pressing Z", args =>
            {
                var rig = Object.FindObjectOfType<CameraRig>() ?? throw new System.InvalidOperationException("no CameraRig");
                var levels = (float[])HarmonyLib.AccessTools.Field(typeof(CameraRig), "zoomLevels").GetValue(rig);
                int level = Mathf.Clamp(int.Parse(args[0]), 0, levels.Length - 1);
                HarmonyLib.AccessTools.Field(typeof(CameraRig), "zoomLevel").SetValue(rig, level);
                HarmonyLib.AccessTools.Field(typeof(CameraRig), "targetOrthSize").SetValue(rig, levels[level]);
                return $"zoom {level} = {levels[level]}";
            });
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
                // Normalise to a 1080p reference before clamping, then scale to the real resolution once.
                float resolution = Screen.height / 1080f;
                // 1.0 at the game's default zoom (~15 px per unit at 1080p); clamped so labels stay readable when
                // zoomed out and don't balloon when zoomed in.
                float scale = Mathf.Clamp(ppu / resolution / 15f, 0.75f, 1.4f) * sizeMul * resolution;

                var label = GetLabel(used++);
                if (label.Amount != e.Amount)
                {
                    label.Amount = e.Amount;
                    label.Text.text = $"<sprite name=coin>{e.Amount}";
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

        // The building a slot's income is shown on: slots that upgrade with their activator belong to it, and so do
        // fields (activated by their mill) when grouping is on.
        private BuildSlot Root(BuildSlot slot)
        {
            int guard = 0;
            while (slot.ActivatorBuilding != null && guard++ < 16
                && (slot.ActivatorUpgradesThis || (groupFields.Value && slot.LOCIDENTIFIER_NAME == FieldName)))
                slot = slot.ActivatorBuilding;
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

        // Borrow the numeral font of the game's own cost display; the coin is the HUD's TMP "coin" sprite.
        private void ResolveStyle()
        {
            foreach (var bi in TagManager.instance.playerBuildingInteractors)
            {
                var amount = bi != null && bi.costDisplay != null ? bi.costDisplay.amountDisplay : null;
                if (amount == null) continue;
                font = amount.font;
                fontMaterial = amount.fontSharedMaterial;
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
            root.sizeDelta = new Vector2(120f, 40f);
            var group = go.GetComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = font;
            if (fontMaterial != null) text.fontSharedMaterial = fontMaterial;
            text.fontSize = 32f;
            // The colour of the game's own cost numbers.
            text.color = new Color(1f, 0.908f, 0.769f);
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.outlineWidth = 0.3f;
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
            sb.AppendLine($"shown={ShouldShow()} previews={BuildingInteractor.displayAllBuildPreviews} font={font?.name}");
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
    }
}
