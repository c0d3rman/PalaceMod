using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    A docked, non-modal UI over the running game (the Builder Studio's toolbar, palette and panels): a persistent
    UIFrame that doesn't stop time, freezes the player, and can't be escaped (its owner reads Esc itself). Panels are
    placed against the screen edges and built from ModPage pieces, ModTiles and ModScrollAreas:

        dock = new ModDock("Studio");
        var right = dock.AddPanel("Inspector", anchorMin: new(1, 0), anchorMax: new(1, 1), offsetMin: new(-364, 80), offsetMax: new(-24, -104));
        right.Fill(page => { page.Row("Cost", "12"); });   // a scrolling ModPage
        dock.Show();
        if (!dock.PointerOverPanel) ... world input ...

    ModPopups open over it (it stays visible and keeps updating). The mouse works as in any game menu; keyboard and
    controller navigation is off while the dock is on top, so movement keys stay free for the owner (camera pan).
*/
namespace PalaceMod
{
    public sealed class ModDock
    {
        /// <summary>Space kept free at every screen edge and between panels.</summary>
        public const float Gutter = 24f;

        private static readonly List<ModDock> docks = [];

        private readonly string name;
        private GameObject root;
        private UIFrame frame;
        private readonly List<ModDockPanel> panels = [];

        /// <summary>Set before the first Show: whether the player is frozen while the dock is the active menu (an editor),
        /// or not (a panel over live play, which the owner activates only while the pointer is on it: ShowPassive).</summary>
        public bool FreezePlayer = true;

        public ModDock(string name)
        {
            this.name = name;
            docks.Add(this);
        }

        public UIFrame Frame => frame;
        public RectTransform Root => root != null ? (RectTransform)root.transform : null;
        /// <summary>The dock is the menu on top (no popup or other menu over it).</summary>
        public bool IsActive => frame != null && UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame == frame;
        public bool IsShown => root != null && root.activeSelf && frame != null && OnStack();
        public IReadOnlyList<ModDockPanel> Panels => panels;

        /// <summary>The pointer is over one of the visible panels (or a popup over the dock).</summary>
        public bool PointerOverPanel => Over(ModTile.MousePosition);

        /// <summary>Whether a screen point is over one of the visible panels (or a popup is open over the dock).</summary>
        public bool Over(Vector2 screen)
        {
            if (root == null || !root.activeInHierarchy) return false;
            if (ModPopup.Current != null && ModPopup.Current.IsOpen) return true;
            foreach (var p in panels)
                if (p.Visible && p.Rect != null && RectTransformUtility.RectangleContainsScreenPoint(p.Rect, screen, null)) return true;
            return false;
        }

        /// <summary>Builds the root (if needed) and makes the dock the active menu. Safe to call every frame.</summary>
        public bool Show()
        {
            var manager = UIFrameManager.instance;
            if (manager == null || GameWidgets.Canvas == null) return false;
            if (root == null) Build();
            if (root == null) return false;
            if (manager.ActiveFrame == frame) { root.SetActive(true); return true; }
            // Under a menu opened over it (it comes back when that closes) or a popup (stays visible).
            if (OnStack()) return true;
            if (manager.ActiveFrame != null) return false; // another menu is up; the owner waits for it
            manager.ChangeActiveFrame(frame);
            return true;
        }

        /// <summary>Shows the dock without making it the active menu (over live play). Activate while the pointer is on a
        /// panel, so its tiles and rows take the mouse; Release when it leaves, so the game gets its input back.</summary>
        public bool ShowPassive()
        {
            var manager = UIFrameManager.instance;
            if (manager == null || GameWidgets.Canvas == null) return false;
            if (root == null) Build();
            if (root == null) return false;
            if (manager.ActiveFrame != null && manager.ActiveFrame != frame) { root.SetActive(false); return false; }
            root.SetActive(true);
            return true;
        }

        public void Activate()
        {
            var manager = UIFrameManager.instance;
            if (manager == null || root == null || manager.ActiveFrame != null) return;
            manager.ChangeActiveFrame(frame);
        }

        public void Release()
        {
            var manager = UIFrameManager.instance;
            if (manager == null || manager.ActiveFrame != frame) return;
            manager.CloseActiveFrame();
            if (root != null) root.SetActive(true);
        }

        /// <summary>Takes the dock off screen (closing it if it's the active menu).</summary>
        public void Hide()
        {
            if (root == null) return;
            var manager = UIFrameManager.instance;
            if (manager != null && manager.ActiveFrame == frame) manager.CloseActiveFrame();
            root.SetActive(false);
        }

        /// <summary>Destroys it (rebuilt on the next Show).</summary>
        public void Destroy()
        {
            Hide();
            if (root != null) Object.Destroy(root);
            root = null;
            frame = null;
            foreach (var p in panels) p.Detach();
        }

        /// <summary>A panel in the game's panel look, positioned like a RectTransform against the screen
        /// (anchors 0-1 across the screen; offsets in canvas pixels).</summary>
        public ModDockPanel AddPanel(string panelName, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, bool background = true)
        {
            var panel = new ModDockPanel(this, panelName, background) { AnchorMin = anchorMin, AnchorMax = anchorMax, OffsetMin = offsetMin, OffsetMax = offsetMax };
            panels.Add(panel);
            if (root != null) panel.Attach(root.transform);
            return panel;
        }

        private void Build()
        {
            var canvas = GameWidgets.Canvas;
            root = new GameObject($"PalaceMod Dock {name}", typeof(RectTransform));
            root.SetActive(false);
            root.transform.SetParent(canvas, false);
            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            frame = root.AddComponent<UIFrame>();
            frame.freezeTime = false;
            frame.freezePlayer = FreezePlayer;
            frame.canNotBeEscaped = true;
            frame.storeLastSelectedElementInFrameStack = false;
            foreach (var p in panels) p.Attach(root.transform);
            root.transform.SetAsFirstSibling(); // under the game's own menus and popups
        }

        /// <summary>Re-reads the selectable elements after panels were rebuilt.</summary>
        internal void Refetch()
        {
            if (frame != null) frame.RefetchManagedElements();
        }

        private bool OnStack()
        {
            try
            {
                foreach (var element in Traverse.Create(UIFrameManager.instance).Field("frameStack").GetValue<System.Collections.IEnumerable>())
                    if (Traverse.Create(element).Field("frame").GetValue<UIFrame>() == frame) return true;
            }
            catch (Exception e) { Log.Core.Warn($"ModDock: couldn't read the frame stack: {e.Message}"); }
            return false;
        }

        internal static bool IsDockFrame(UIFrame f) => f != null && docks.Any(d => d.frame == f);

        // Keyboard and controller navigation would move between the dock's tiles on WASD and arrows (the game's frames
        // navigate on Move Horizontal/Vertical too); the dock's owner uses those keys, so only the mouse acts on it.
        [HarmonyPatch(typeof(UIFrame), "HandleButtonNavigation")]
        private static class NoButtonNavigation_Patch
        {
            private static bool Prefix(UIFrame __instance) => !IsDockFrame(__instance);
        }
    }

    /// <summary>One docked panel. Fill builds its contents as a scrolling ModPage, or Custom gives a bare area.</summary>
    public sealed class ModDockPanel
    {
        private readonly ModDock dock;
        public string Name { get; }
        public Vector2 AnchorMin, AnchorMax, OffsetMin, OffsetMax;
        public RectTransform Rect { get; private set; }
        /// <summary>The area inside the panel's padding (for Custom content).</summary>
        public RectTransform Body { get; private set; }
        public bool Visible { get; private set; } = true;
        public ModScrollArea Scroll { get; private set; }
        public ModPage Page { get; private set; }
        private readonly bool background;
        public const float Padding = 12f;

        internal ModDockPanel(ModDock dock, string name, bool background)
        {
            this.dock = dock;
            Name = name;
            this.background = background;
        }

        internal void Attach(Transform parent)
        {
            GameObject box;
            var source = GameWidgets.PauseTooltip?.selectionTitle?.GetComponentInParent<MPUIKIT.MPImage>(true);
            if (background && source != null)
            {
                box = GameWidgets.Clone(source, parent, "Panel " + Name, keepChildren: false);
                foreach (var c in box.GetComponents<Component>())
                    if (c is LayoutGroup or ContentSizeFitter or LayoutElement) Object.DestroyImmediate(c);
                if (box.GetComponent<MPUIKIT.MPImage>() is { } image)
                {
                    image.raycastTarget = true;
                    image.color = new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.94f);
                    ModStyle.MarkPanel(image);
                }
            }
            else box = new GameObject("Panel " + Name, typeof(RectTransform));
            box.transform.SetParent(parent, false);
            box.transform.localScale = Vector3.one;
            Rect = (RectTransform)box.transform;
            var body = new GameObject("Body", typeof(RectTransform));
            body.transform.SetParent(box.transform, false);
            Body = (RectTransform)body.transform;
            Body.anchorMin = Vector2.zero;
            Body.anchorMax = Vector2.one;
            Body.offsetMin = new Vector2(Padding, Padding);
            Body.offsetMax = new Vector2(-Padding, -Padding);
            Layout();
            box.SetActive(Visible);
        }

        internal void Detach()
        {
            Rect = Body = null;
            Scroll = null;
            Page = null;
        }

        /// <summary>Applies changed anchors/offsets.</summary>
        public void Layout()
        {
            if (Rect == null) return;
            Rect.anchorMin = AnchorMin;
            Rect.anchorMax = AnchorMax;
            Rect.pivot = new Vector2(0.5f, 0.5f);
            Rect.offsetMin = OffsetMin;
            Rect.offsetMax = OffsetMax;
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            if (Rect != null) Rect.gameObject.SetActive(visible);
        }

        /// <summary>Removes the panel's contents.</summary>
        public void Clear()
        {
            if (Body == null) return;
            foreach (Transform child in Body.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            Scroll = null;
            Page = null;
        }

        /// <summary>Rebuilds the panel as a scrolling ModPage, keeping the scroll position.</summary>
        public ModPage Fill(Action<ModPage> build, float top = 0f)
        {
            if (Body == null || dock.Frame == null) return null;
            float keep = Scroll?.ScrollY ?? 0f;
            Clear();
            LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
            var size = new Vector2(Mathf.Max(10f, Body.rect.width), Mathf.Max(10f, Body.rect.height - top));
            Scroll = ModScrollArea.Create(Body, size, dock.Frame).Place(new Vector2(0f, -top));
            var list = Scroll.Content.gameObject.AddComponent<VerticalLayoutGroup>();
            list.childControlWidth = list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;
            list.spacing = 0f;
            Page = new ModPage(Scroll.Content) { Size = size, FieldIndent = Mathf.Min(ModField.ValueIndent, size.x * 0.46f) };
            try { build(Page); }
            catch (Exception e)
            {
                Log.Core.Caught($"ModDock panel \"{Name}\": building it failed", e);
                Page.Text("Couldn't be shown; see the log.");
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(Scroll.Content);
            Scroll.SetContentHeight(LayoutUtility.GetPreferredHeight(Scroll.Content));
            Scroll.ScrollTo(keep);
            dock.Refetch();
            return Page;
        }

        /// <summary>Re-reads selectables after hand-made content was added to Body.</summary>
        public void Refetch() => dock.Refetch();
    }
}
