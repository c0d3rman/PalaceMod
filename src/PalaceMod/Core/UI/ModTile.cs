using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    A selectable tile in the game's panel look (the rounded, outlined squares of the loadout screen and the upgrade
    popup): an optional icon, an optional label beside it, and a small badge in the corner. For icon grids (perks,
    building kinds), chips on a board, toolbar buttons and map markers.

        var tile = ModTile.Create(parent, "Perk Interest", new Vector2(72, 72), icon: perk.icon);
        tile.Applied += () => Toggle(perk);          // Enter, A, or a click
        tile.Shown += t => ShowDetails(perk);         // selected by keyboard/controller, or hovered
        tile.SetOn(true); tile.SetBadge("+34"); tile.SetMarker(true);
        page.AddElement(tile) / ModNav.Grid(...)      // put it in the navigation

    Mouse: hovering selects it (like the game's loadout tiles). With Draggable set, pressing and moving drags it:
    Dragging gets the pointer's screen position while held, Dropped the release point; a press without moving is a
    click (Applied). RightClicked fires on a right click over it. Place it yourself (Place) or let a layout group do it.
*/
namespace PalaceMod
{
    public class ModTile : ThronefallUIElement
    {
        /// <summary>The caller's id for it (e.g. to find it again after a rebuild); also its GameObject name's tail.</summary>
        public string Key;

        public event Action Applied;
        /// <summary>Selected or hovered.</summary>
        public event Action<ModTile> Shown;
        public event Action RightClicked;
        public event Action<Vector2> Dragging;
        public event Action<Vector2> Dropped;
        /// <summary>A drag that didn't drop anywhere useful is cancelled by the caller; this fires when it ends either way.</summary>
        public event Action DragEnded;

        public bool Draggable;
        public override bool dragable => Draggable;

        public RectTransform Rect => (RectTransform)transform;
        public MPUIKIT.MPImage Background { get; private set; }
        public Image Icon { get; private set; }
        public TextMeshProUGUI Label { get; private set; }
        public TextMeshProUGUI Badge { get; private set; }

        private RectTransform body;
        private CanvasGroup group;
        private RectTransform marker;
        private bool on, warn;
        private Color? fillOverride;
        private float scaleFrom = 1f, scaleTo = 1f, clock = 1f;
        private Color outlineFrom, outlineTo;
        private bool mousePress, dragging;
        private Vector2 pressAt;
        private const float AnimTime = 0.12f, DragThreshold = 10f;

        public static readonly Color Fill = Color.Lerp(ModStyle.Panel, Color.white, 0.05f);
        public static readonly Color FillOn = Color.Lerp(ModStyle.Panel, ModStyle.Gold, 0.34f);
        public static readonly Color Edge = new(ModStyle.Dim.r, ModStyle.Dim.g, ModStyle.Dim.b, 0.7f);
        public static readonly Color Warning = new(0.86f, 0.36f, 0.30f);

        /// <summary>A tile of <paramref name="size"/> (anchored top left in <paramref name="parent"/>; move it with Place).</summary>
        public static ModTile Create(Transform parent, string name, Vector2 size, Sprite icon = null, string text = null, float textSize = 20f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tile = go.AddComponent<ModTile>();
            tile.autoSelectOnFocus = true;
            var rect = tile.Rect;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;

            // A body that scales on focus, holding everything visible.
            var bodyObj = new GameObject("Body", typeof(RectTransform), typeof(CanvasGroup));
            bodyObj.transform.SetParent(go.transform, false);
            tile.body = (RectTransform)bodyObj.transform;
            Stretch(tile.body);
            tile.group = bodyObj.GetComponent<CanvasGroup>();

            var source = GameWidgets.PauseTooltip?.selectionTitle?.GetComponentInParent<MPUIKIT.MPImage>(true);
            var bg = source != null
                ? GameWidgets.Clone(source, tile.body, "Background", keepChildren: false)
                : new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(MPUIKIT.MPImage));
            bg.transform.SetParent(tile.body, false);
            foreach (var c in bg.GetComponents<Component>())
                if (c is LayoutGroup or ContentSizeFitter or LayoutElement) DestroyImmediate(c);
            Stretch((RectTransform)bg.transform);
            bg.transform.localScale = Vector3.one;
            tile.Background = bg.GetComponent<MPUIKIT.MPImage>();
            tile.Background.raycastTarget = true; // the frame finds the tile under the mouse through this
            var shape = tile.Background.Rectangle;
            float radius = Mathf.Min(12f, Mathf.Min(size.x, size.y) * 0.22f);
            shape.CornerRadius = new Vector4(radius, radius, radius, radius);
            tile.Background.Rectangle = shape;
            tile.Background.OutlineWidth = 1.5f;
            tile.Background.OutlineColor = Edge;
            tile.Background.color = Fill;
            bg.SetActive(true);

            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            iconObj.transform.SetParent(tile.body, false);
            tile.Icon = iconObj.GetComponent<Image>();
            tile.Icon.preserveAspect = true;
            tile.Icon.raycastTarget = false;
            tile.Icon.color = ModStyle.Gold;

            tile.Label = GameWidgets.Text(tile.body, "Label", "", ModStyle.BoldFont, textSize, ModStyle.Gold, TextAlignmentOptions.MidlineLeft);
            tile.Label.raycastTarget = false;
            tile.Label.enableWordWrapping = false;
            tile.Label.overflowMode = TextOverflowModes.Ellipsis;
            // A label too long for its tile (narrow tabs and chips) shrinks before it is cut.
            tile.Label.enableAutoSizing = true;
            tile.Label.fontSizeMax = textSize;
            tile.Label.fontSizeMin = Mathf.Max(12f, textSize * 0.6f);
            tile.Label.characterSpacing = 0f;

            tile.Badge = GameWidgets.Text(tile.body, "Badge", "", ModStyle.BoldFont, Mathf.Max(14f, textSize * 0.8f), ModStyle.Bright, TextAlignmentOptions.TopRight);
            tile.Badge.raycastTarget = false;
            tile.Badge.enableWordWrapping = false;
            tile.Badge.characterSpacing = 0f;
            var badgeRect = tile.Badge.rectTransform;
            badgeRect.anchorMin = badgeRect.anchorMax = badgeRect.pivot = new Vector2(1f, 1f);
            badgeRect.anchoredPosition = new Vector2(-5f, -2f);
            badgeRect.sizeDelta = new Vector2(size.x, 22f);

            tile.SetContent(icon, text);
            go.AddComponent<TFUIAudioHelper>(); // the game's select sounds
            tile.HardStateSet(SelectionState.Default);
            return tile;
        }

        /// <summary>Puts the tile's top left at <paramref name="topLeft"/> in its parent (y down is negative).</summary>
        public ModTile Place(Vector2 topLeft)
        {
            Rect.anchoredPosition = topLeft;
            return this;
        }

        /// <summary>Icon and label: an icon alone is centred; with a label it sits at the left.</summary>
        public void SetContent(Sprite icon, string text)
        {
            var size = Rect.sizeDelta;
            Icon.sprite = icon;
            Icon.enabled = icon != null;
            bool hasText = !string.IsNullOrEmpty(text);
            Label.text = text ?? "";
            Label.gameObject.SetActive(hasText);
            var iconRect = Icon.rectTransform;
            if (!hasText)
            {
                iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0.5f, 0.5f);
                float s = Mathf.Min(size.x, size.y) * 0.62f;
                iconRect.sizeDelta = new Vector2(s, s);
                iconRect.anchoredPosition = Vector2.zero;
                return;
            }
            float iconSize = icon != null ? Mathf.Min(size.y - 8f, 34f) : 0f;
            iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(iconSize, iconSize);
            iconRect.anchoredPosition = new Vector2(6f, 0f);
            var labelRect = Label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.offsetMin = new Vector2(icon != null ? iconSize + 11f : 10f, 0f);
            labelRect.offsetMax = new Vector2(-6f, 0f);
        }

        public void SetText(string text) => SetContent(Icon.sprite, text);

        public void SetBadge(string text, Color? color = null)
        {
            Badge.text = text ?? "";
            Badge.color = color ?? ModStyle.Bright;
        }

        /// <summary>Lit (picked, equipped, on).</summary>
        public void SetOn(bool value)
        {
            on = value;
            ApplyFill();
        }

        /// <summary>Overrides the fill colour (null: the default, or lit).</summary>
        public void SetFill(Color? color)
        {
            fillOverride = color;
            ApplyFill();
        }

        /// <summary>Faded: not available, or not counting.</summary>
        public void SetDim(bool value, float alpha = 0.42f) => group.alpha = value ? alpha : 1f;

        /// <summary>A red edge (e.g. not enough gold).</summary>
        public void SetWarning(bool value)
        {
            warn = value;
            Restyle(true);
        }

        /// <summary>PalaceMod's small verdigris gem in the top left corner (e.g. "differs from your run").</summary>
        public void SetMarker(bool value, Color? color = null)
        {
            if (value && marker == null)
            {
                marker = ModStyle.AddEmblem(body, Mathf.Clamp(Rect.sizeDelta.y * 0.22f, 9f, 15f), color);
                marker.anchorMin = marker.anchorMax = new Vector2(0f, 1f);
                marker.anchoredPosition = new Vector2(7f, -7f);
            }
            if (marker != null)
            {
                marker.gameObject.SetActive(value);
                if (value && color != null) ModStyle.TintEmblem(marker.GetComponent<Image>(), color);
            }
        }

        /// <summary>A small icon on the bottom right corner (e.g. a padlock: can't be picked yet); null removes it.</summary>
        public void SetCornerIcon(Sprite sprite, Color? color = null)
        {
            if (sprite == null)
            {
                if (corner != null) corner.gameObject.SetActive(false);
                return;
            }
            if (corner == null)
            {
                var obj = new GameObject("Corner", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                obj.transform.SetParent(body, false);
                corner = obj.GetComponent<Image>();
                corner.raycastTarget = false;
                corner.preserveAspect = true;
                var rect = corner.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
                float side = Mathf.Clamp(Mathf.Min(Rect.sizeDelta.x, Rect.sizeDelta.y) * 0.5f, 10f, 18f);
                rect.sizeDelta = new Vector2(side, side);
                rect.anchoredPosition = new Vector2(side * 0.25f, -side * 0.25f);
            }
            corner.sprite = sprite;
            corner.color = color ?? ModStyle.Bright;
            corner.gameObject.SetActive(true);
        }

        private Image corner;

        private void ApplyFill()
        {
            if (Background == null) return;
            Background.color = fillOverride ?? (on ? FillOn : Fill);
            Icon.color = on ? ModStyle.Bright : ModStyle.Gold;
        }

        private Color RestOutline => warn ? Warning : on ? ModStyle.Gold : Edge;

        private void Restyle(bool instant)
        {
            bool lit = currentState != SelectionState.Default;
            scaleFrom = body != null ? body.localScale.x : 1f;
            scaleTo = lit ? 1.05f : 1f;
            outlineFrom = Background != null ? Background.OutlineColor : Edge;
            outlineTo = lit ? (warn ? Color.Lerp(Warning, ModStyle.Bright, 0.35f) : ModStyle.Bright) : RestOutline;
            clock = instant ? 1f : 0f;
            if (instant && body != null && Background != null)
            {
                body.localScale = Vector3.one * scaleTo;
                Background.OutlineColor = outlineTo;
                Background.OutlineWidth = lit ? 2.5f : 1.5f;
            }
            if (lit)
            {
                try { Shown?.Invoke(this); }
                catch (Exception e) { Log.Core.Caught($"ModTile {name}: Shown handler failed", e); }
            }
        }

        private void Update()
        {
            if (clock < 1f && body != null && Background != null)
            {
                clock = Mathf.Min(1f, clock + Time.unscaledDeltaTime / AnimTime);
                float t = Mathf.SmoothStep(0f, 1f, clock);
                body.localScale = Vector3.one * Mathf.Lerp(scaleFrom, scaleTo, t);
                Background.OutlineColor = Color.Lerp(outlineFrom, outlineTo, t);
                Background.OutlineWidth = Mathf.Lerp(1.5f, 2.5f, scaleTo > 1f ? t : 1f - t);
            }
            if (RightClicked != null && MouseButtonDown(1) && PointerOver()) Fire(RightClicked, "right click");
        }

        internal static Vector2 MousePosition => Plugin.Input?.controllers?.Mouse != null ? Plugin.Input.controllers.Mouse.screenPosition : (Vector2)Input.mousePosition;

        private static bool MouseButtonDown(int button)
        {
            try
            {
                var mouse = Plugin.Input?.controllers?.Mouse;
                return mouse != null ? mouse.GetButtonDown(button) : Input.GetMouseButtonDown(button);
            }
            catch { return false; }
        }

        /// <summary>The pointer is over the tile (and its frame is the one on top).</summary>
        public bool PointerOver()
        {
            var frame = GetComponentInParent<UIFrame>();
            if (frame == null || UIFrameManager.instance == null || UIFrameManager.instance.ActiveFrame != frame) return false;
            var canvas = GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(Rect, MousePosition, cam);
        }

        private void Fire(Action action, string what)
        {
            try { action?.Invoke(); }
            catch (Exception e) { Log.Core.Caught($"ModTile {name}: {what} handler failed", e); }
        }

        /// <summary>Runs the tile's action as if applied by keyboard or controller (for dev commands).</summary>
        public void Press() => Fire(Applied, "apply");

        protected override void OnApply()
        {
            // A mouse press on a draggable tile is a click or the start of a drag; the release decides.
            mousePress = Draggable && MouseButtonDown(0);
            if (mousePress)
            {
                pressAt = MousePosition;
                dragging = false;
                return;
            }
            Fire(Applied, "apply");
        }

        public override void OnDragStart() { }

        public override void OnDrag(Vector2 mousePosition)
        {
            if (!mousePress) return;
            if (!dragging && (mousePosition - pressAt).magnitude < DragThreshold) return;
            dragging = true;
            var at = mousePosition;
            Fire(() => Dragging?.Invoke(at), "drag");
        }

        public override void OnDragEnd()
        {
            if (!mousePress) return;
            mousePress = false;
            var at = MousePosition;
            if (dragging) Fire(() => Dropped?.Invoke(at), "drop");
            else Fire(Applied, "apply");
            dragging = false;
            Fire(DragEnded, "drag end");
        }

        protected override void OnClear() => Restyle(false);
        protected override void OnFocus() => Restyle(false);
        protected override void OnSelect() => Restyle(false);
        protected override void OnFocusAndSelect() => Restyle(false);
        protected override void OnHardStateSet(SelectionState selectionState) => Restyle(true);

        public bool IsSelected => currentState is SelectionState.Selected or SelectionState.FocussedAndSelected;

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
