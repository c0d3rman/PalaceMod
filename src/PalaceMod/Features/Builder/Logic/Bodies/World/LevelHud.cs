using System;
using System.Collections.Generic;
using System.Linq;
using MPUIKIT;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    The level HUD (docs/builder-engine.md: `logic.hud` and the `hud` action): a column of lines at the top right of
    the screen, where the game's HUD has nothing (the interaction tooltip and control groups are top-left, Notify's
    messages top-centre, gold bottom-right, the night counter bottom-centre).

    Each line is a clone of the game's in-world tooltip panel (rounded charcoal, Cormorant Garamond in the game's gold)
    with a thin verdigris edge (the mod's mark), an optional icon, and text whose {expr} parts are evaluated again
    every 0.25 s, so `"Souls {$souls}/10"` follows the variable. `visible` is an expression (or a bool) re-checked as
    often. Lines start from `logic.hud`; the `hud` action changes a line (only the fields it gives) or adds one.
    Hidden while a menu is open.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class LevelHud
    {
        internal sealed class Line
        {
            public string Id, Text, Icon;
            public JToken Visible;           // null/true/false or an expression string
            public RectTransform Panel;
            public TextMeshProUGUI Label;
            public Image IconImage;
            public string Shown;             // last drawn text
            public bool Warned;
        }

        private static readonly List<Line> lines = [];
        private static float nextRefresh;
        private static GameObject template;
        private static bool defaultsLoaded, lastShow;

        private const float Gutter = 24f, Gap = 8f, PadX = 18f, PadY = 8f, IconSize = 26f, FontSize = 24f, MaxWidth = 420f;

        public static void Register()
        {
            LogicActions.Register("hud", Hud);
            WorldSession.Reset += () => { Clear(); defaultsLoaded = false; };
        }

        // ------------------------------------------------------------------ data

        /// <summary>Loads `logic.hud` (an array of {id, text, icon, visible}) as the starting lines.</summary>
        public static void LoadDefaults(JToken hud)
        {
            Clear();
            defaultsLoaded = true;
            foreach (var o in (hud as JArray)?.OfType<JObject>() ?? [])
            {
                string id = (string)o["id"];
                if (string.IsNullOrEmpty(id) || lines.Any(l => l.Id == id)) continue;
                lines.Add(new Line { Id = id, Text = (string)o["text"] ?? "", Icon = (string)o["icon"], Visible = o["visible"]?.DeepClone() });
            }
            nextRefresh = 0f;
        }

        /// <summary>The level's `logic.hud`, as the world loaded it.</summary>
        private static JToken SessionHud() => LogicWorld.Current?.LevelLogic?["hud"];

        private static void Hud(LogicContext ctx, ActionParams p)
        {
            EnsureDefaults();
            string id = p.Text("id") ?? throw new LogicError("hud: needs \"id\"");
            var line = lines.FirstOrDefault(l => l.Id == id);
            if (line == null) lines.Add(line = new Line { Id = id, Text = "" });
            // Text keeps its {expr} parts: they're evaluated on every refresh, not once now.
            if (p.Has("text")) line.Text = p.Raw("text")?.Type == JTokenType.String ? (string)p.Raw("text") : p.Text("text");
            if (p.Has("icon")) line.Icon = p.Text("icon");
            if (p.Has("visible")) line.Visible = p.Raw("visible")?.DeepClone();
            line.Shown = null;
            nextRefresh = 0f;
        }

        private static void EnsureDefaults()
        {
            if (defaultsLoaded) return;
            LoadDefaults(SessionHud());
        }

        private static void Clear()
        {
            foreach (var l in lines) if (l.Panel != null) Object.Destroy(l.Panel.gameObject);
            lines.Clear();
        }

        private static bool Visible(LogicWorld world, Line l)
        {
            if (l.Visible == null || l.Visible.Type == JTokenType.Null) return true;
            if (l.Visible.Type == JTokenType.Boolean) return (bool)l.Visible;
            if (l.Visible.Type == JTokenType.Integer || l.Visible.Type == JTokenType.Float) return (double)l.Visible != 0;
            var v = world.Evaluate((string)l.Visible);
            return v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num != 0 : !v.IsNone;
        }

        // ------------------------------------------------------------------ drawing

        public static void Tick()
        {
            var world = LogicWorld.Current;
            if (world == null) return;
            if (!defaultsLoaded && WorldCommon.InMatch) EnsureDefaults();
            if (lines.Count == 0) return;
            bool show = WorldCommon.InMatch && (UIFrameManager.instance == null || UIFrameManager.instance.ActiveFrame == null);
            if (Time.unscaledTime >= nextRefresh || show != lastShow)
            {
                nextRefresh = Time.unscaledTime + 0.25f;
                lastShow = show;
                if (show && !EnsureTemplate()) return;
                Refresh(world, show);
            }
            Place();
        }

        private static void Refresh(LogicWorld world, bool show)
        {
            foreach (var l in lines)
            {
                bool visible = false;
                string text = l.Shown;
                if (show)
                {
                    try
                    {
                        visible = Visible(world, l);
                        if (visible) text = world.Interpolate(l.Text);
                    }
                    catch (Exception e) when (e is LogicError or FormatException or InvalidCastException)
                    {
                        if (!l.Warned) WorldCommon.log.Warn($"hud \"{l.Id}\": {e.Message}");
                        l.Warned = true;
                        visible = true;
                        text = l.Text;
                    }
                }
                if (!visible || string.IsNullOrEmpty(text) && string.IsNullOrEmpty(l.Icon))
                {
                    if (l.Panel != null) l.Panel.gameObject.SetActive(false);
                    l.Shown = show ? null : l.Shown;
                    continue;
                }
                if (l.Panel == null) Build(l);
                l.Panel.gameObject.SetActive(true);
                if (text != l.Shown || l.IconImage.sprite == null != string.IsNullOrEmpty(l.Icon)) Layout(l, text);
            }
        }

        // Every frame: the column starts under the game's night-score panel while it's up (it shares the top right).
        private static void Place()
        {
            float y = Gutter;
            var score = NightscoreUI.instance != null ? NightscoreUI.instance.nightScorePanel : null;
            if (score != null && score.activeInHierarchy && score.transform is RectTransform rt)
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);   // screen space: the game's HUD canvas is an overlay
                float bottom = Mathf.Min(corners[0].y, corners[3].y);
                y = Mathf.Max(y, (Screen.height - bottom) / WorldOverlay.ScaleFactor + Gap);
            }
            foreach (var l in lines)
            {
                if (l.Panel == null || !l.Panel.gameObject.activeSelf) continue;
                l.Panel.anchoredPosition = new Vector2(-Gutter, -y);
                y += l.Panel.sizeDelta.y + Gap;
            }
        }

        private static bool EnsureTemplate()
        {
            if (template != null) return true;
            var tooltip = GameWidgets.HudTooltip;
            if (tooltip == null) return false;
            var canvas = WorldOverlay.Canvas;
            var obj = GameWidgets.Clone(tooltip, canvas, "PalaceMod HUD line template");
            var text = obj.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text == null) { Object.Destroy(obj); return false; }
            foreach (Transform child in obj.transform) if (child != text.transform) child.gameObject.SetActive(false);
            foreach (var c in text.GetComponents<Component>()) if (c is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(c);
            foreach (var c in obj.GetComponents<Component>()) if (c is MonoBehaviour mb && c is not (MPImage or Graphic) && mb.GetType().Assembly.GetName().Name == "Assembly-CSharp") Object.DestroyImmediate(c);
            obj.transform.localScale = Vector3.one;
            var panel = (RectTransform)obj.transform;
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 1f);
            panel.pivot = new Vector2(1f, 1f);
            var image = obj.GetComponent<MPImage>();
            if (image != null)
            {
                image.color = new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.92f);
                image.raycastTarget = false;
                ModStyle.MarkPanel(image);
            }
            var label = text.rectTransform;
            label.anchorMin = label.anchorMax = new Vector2(0f, 1f);
            label.pivot = new Vector2(0f, 1f);
            text.font = ModStyle.BodyFont ?? text.font;
            text.fontSize = FontSize;
            text.color = ModStyle.Gold;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.richText = true;
            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(obj.transform, false);
            var icon = (RectTransform)iconObj.transform;
            icon.anchorMin = icon.anchorMax = new Vector2(0f, 0.5f);
            icon.pivot = new Vector2(0f, 0.5f);
            icon.sizeDelta = new Vector2(IconSize, IconSize);
            var img = iconObj.GetComponent<Image>();
            img.preserveAspect = true;
            img.raycastTarget = false;
            obj.AddComponent<CanvasGroup>().blocksRaycasts = false;
            obj.SetActive(false);
            template = obj;
            return true;
        }

        private static void Build(Line l)
        {
            var obj = Object.Instantiate(template, template.transform.parent, false);
            obj.name = "PalaceMod HUD " + l.Id;
            l.Panel = (RectTransform)obj.transform;
            l.Label = obj.GetComponentInChildren<TextMeshProUGUI>(true);
            l.IconImage = obj.transform.Find("Icon").GetComponent<Image>();
        }

        private static void Layout(Line l, string text)
        {
            l.Shown = text;
            var sprite = IconSprite(l.Icon);
            l.IconImage.sprite = sprite;
            l.IconImage.enabled = sprite != null;
            float left = PadX + (sprite != null ? IconSize + 8f : 0f);
            l.Label.text = text ?? "";
            var size = l.Label.GetPreferredValues(l.Label.text, MaxWidth, 0f);
            float width = Mathf.Min(Mathf.Ceil(size.x) + 2f, MaxWidth);
            var label = l.Label.rectTransform;
            label.anchoredPosition = new Vector2(left, -PadY);
            label.sizeDelta = new Vector2(width, 10000f);
            l.Label.ForceMeshUpdate(true, true);
            float height = Mathf.Max(l.Label.preferredHeight, l.Label.textInfo.characterCount > 0 ? -l.Label.textBounds.min.y : 0f);
            height = Mathf.Max(Mathf.Ceil(height), sprite != null ? IconSize : 0f);
            label.sizeDelta = new Vector2(width, height);
            l.Panel.sizeDelta = new Vector2(left + width + PadX, height + 2f * PadY);
            ((RectTransform)l.IconImage.transform).anchoredPosition = new Vector2(PadX, 0f);
        }

        private static readonly Dictionary<string, Sprite> sprites = [];

        private static Sprite IconSprite(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            if (sprites.TryGetValue(icon, out var cached) && cached != null) return cached;
            Sprite s = null;
            if (icon.StartsWith("asset:"))
            {
                var entry = WorldCommon.PackEntry;
                s = entry != null ? BuilderServices.Assets.GetSprite(entry, icon) : null;
            }
            else
            {
                string unit = icon.StartsWith("unit:") ? icon.Substring(5) : icon;
                var prefab = BuilderServices.Units.Resolve(WorldCommon.Session?.Pack, unit, out _) ?? BuilderServices.Units.Resolve(WorldCommon.Session?.Pack, "vanilla:" + unit, out _);
                s = prefab != null ? prefab.GetComponent<ScreenMarkerIcon>()?.sprite : null;
            }
            if (s != null) sprites[icon] = s;
            return s;
        }

        // ------------------------------------------------------------------ savestates and dev

        internal static JToken Capture() => new JArray(lines.Select(l => new JObject { ["id"] = l.Id, ["text"] = l.Text, ["icon"] = l.Icon, ["visible"] = l.Visible?.DeepClone() }));

        internal static void Restore(JToken state)
        {
            if (state is JArray a) LoadDefaults(a);
        }

        public static string Status()
        {
            var world = LogicWorld.Current;
            return lines.Count == 0 ? "no HUD lines" : string.Join("\n", lines.Select(l =>
                $"{l.Id}: \"{l.Text}\" visible={l.Visible?.ToString(Newtonsoft.Json.Formatting.None) ?? "true"} shown={(l.Panel != null && l.Panel.gameObject.activeSelf ? $"\"{l.Shown}\"" : "no")}"));
        }

        public static void Destroy()
        {
            Clear();
            if (template != null) Object.Destroy(template);
        }
    }
}
