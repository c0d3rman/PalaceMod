using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    One shared tooltip in the game's tooltip look (the pause menu's selection tooltip: rounded charcoal panel,
    Unifraktur title, Cormorant body), with PalaceMod's verdigris edge. It is placed beside whatever it describes,
    on the game's UI canvas so it follows the game's UI scale.

        ModTooltip.Attach(button, "Title", () => "Body, built when shown");   // menu element: on select or hover
        ModTooltip.AttachHover(someRect.gameObject, "Title", () => "Body");    // any graphic: on mouse hover
        ModTooltip.Show("Title", "Body", nearRect); ModTooltip.Hide();       // manual
*/
namespace PalaceMod
{
    public static class ModTooltip
    {
        private const float Offset = 48f;

        private static RectTransform panel;
        private static TextMeshProUGUI title, body;
        private static VerticalLayoutGroup textSection;
        private static int textTop;

        // The game's tooltip (measured with "uidump Tooltip Background"): a 520-wide panel whose text section is inset 20 on
        // every side, followed by the icon/stats section and 32 below it. Its title's line height leaves about 11 above
        // the capitals, so text sits about 31 from the top edge and the stats box 32 from the bottom. We keep only the
        // text section, so these restore the same visible margins: 32 from the last line's glyphs to the bottom edge
        // (a body line already ends with about 6 of leading), and with no title, the body's first line as far from the
        // top as the title's would be (a body line starts with about 6 of leading).
        private const int BottomPadding = 6, NoTitleExtraTop = 6;
        private static object owner;
        private static RectTransform anchor; // what the tooltip sits beside; followed while it moves (e.g. scrolling)
        private static bool failed;

        /// <summary>Shows the tooltip beside <paramref name="near"/> (right of it if there's room, else left).</summary>
        /// <param name="owner">Who shows it; Hide(owner) only hides it if nobody else has shown it since.</param>
        public static void Show(string titleText, string bodyText, RectTransform near, object owner = null)
        {
            if (!Ensure()) return;
            ModTooltip.owner = owner;
            title.text = titleText ?? "";
            title.gameObject.SetActive(!string.IsNullOrEmpty(titleText));
            body.text = bodyText ?? "";
            body.gameObject.SetActive(!string.IsNullOrEmpty(bodyText));
            if (textSection != null) textSection.padding.top = textTop + (title.gameObject.activeSelf ? 0 : NoTitleExtraTop);
            panel.SetAsLastSibling();
            panel.gameObject.SetActive(true);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            anchor = near;
            Place(near);
        }

        public static void Hide(object owner = null)
        {
            if (panel == null || (owner != null && owner != ModTooltip.owner)) return;
            panel.gameObject.SetActive(false);
            ModTooltip.owner = null;
        }

        public static bool Visible => panel != null && panel.gameObject.activeSelf;

        /// <summary>Dev: the shown tooltip's text and what it's beside, or null.</summary>
        internal static string Describe() => Visible ? $"\"{title.text}\" / \"{body.text}\" beside {(anchor != null ? anchor.name : "nothing")}" : null;

        /// <summary>Shows a tooltip while a menu element is selected (controller/keyboard) or hovered (mouse).</summary>
        public static void Attach(ThronefallUIElement element, string titleText, Func<string> bodyText)
        {
            var source = element.gameObject.GetComponent<SelectionSource>() ?? element.gameObject.AddComponent<SelectionSource>();
            source.Init(element, () => (titleText, bodyText?.Invoke()));
        }

        /// <summary>Shows a tooltip while the mouse is over <paramref name="target"/>'s rect (no menu element needed).
        /// Nothing shows while both title and body are empty.</summary>
        public static void AttachHover(GameObject target, string titleText, Func<string> bodyText)
        {
            var source = target.GetComponent<HoverSource>() ?? target.AddComponent<HoverSource>();
            source.Content = () => (titleText, bodyText?.Invoke());
        }

        private static void Place(RectTransform near)
        {
            var canvas = (RectTransform)panel.parent;
            var size = panel.rect.size;
            var bounds = canvas.rect;
            Vector2 pos;
            if (near != null)
            {
                var corners = new Vector3[4];
                near.GetWorldCorners(corners);
                Vector2 min = canvas.InverseTransformPoint(corners[0]), max = canvas.InverseTransformPoint(corners[2]);
                pos = new Vector2(max.x + Offset, max.y);
                if (pos.x + size.x > bounds.xMax - 16f) pos.x = min.x - Offset - size.x;
            }
            else pos = new Vector2(-size.x / 2f, size.y / 2f);
            pos.x = Mathf.Clamp(pos.x, bounds.xMin + 16f, bounds.xMax - 16f - size.x);
            pos.y = Mathf.Clamp(pos.y, bounds.yMin + 16f + size.y, bounds.yMax - 16f);
            panel.localPosition = new Vector3(pos.x + size.x * panel.pivot.x, pos.y - size.y * (1f - panel.pivot.y), 0f);
        }

        private static bool Ensure()
        {
            if (panel != null && panel.parent == GameWidgets.Canvas) return true;
            if (panel != null) Object.Destroy(panel.gameObject); // the UI canvas was rebuilt
            panel = null;
            if (failed || GameWidgets.Canvas == null) return false;
            var helper = GameWidgets.PauseTooltip;
            if (helper?.selectionTitle == null || helper.selectionDescription == null) return false;
            GameObject obj = null;
            try
            {
                var background = helper.selectionTitle.GetComponentInParent<MPUIKIT.MPImage>(true)
                    ?? throw new InvalidOperationException("tooltip title has no panel");
                obj = GameWidgets.Clone(background, GameWidgets.Canvas, "PalaceMod Tooltip");
                var cloneTitle = GameWidgets.Corresponding(background.transform, helper.selectionTitle.transform, obj.transform).GetComponent<TextMeshProUGUI>();
                var cloneBody = GameWidgets.Corresponding(background.transform, helper.selectionDescription.transform, obj.transform).GetComponent<TextMeshProUGUI>();
                // Keep just the section holding title and description; drop the icon and unit-stats section.
                var section = cloneTitle.transform.parent;
                foreach (Transform child in obj.transform.Cast<Transform>().ToList())
                {
                    if (child != section) Object.DestroyImmediate(child.gameObject);
                }
                foreach (Transform child in section.Cast<Transform>().ToList())
                {
                    if (child != cloneTitle.transform && child != cloneBody.transform) Object.DestroyImmediate(child.gameObject);
                }
                // The section is tinted for debugging in the game's scene (translucent red); keep only its layout.
                if (section.GetComponent<Graphic>() is { } tint) tint.color = Color.clear;
                // The panel's bottom padding is meant for below the dropped icon/stats section; under the text section
                // (which has its own bottom margin) it would leave a gap twice the size of the top one.
                if (obj.GetComponent<VerticalLayoutGroup>() is { } outer) outer.padding.bottom = BottomPadding;
                textSection = section.GetComponent<VerticalLayoutGroup>();
                textTop = textSection != null ? textSection.padding.top : 0;
                foreach (var graphic in obj.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                ModStyle.MarkPanel(obj.GetComponent<MPUIKIT.MPImage>());
                var rect = (RectTransform)obj.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 1f);
                obj.AddComponent<Follower>();
                obj.SetActive(false);
                panel = rect;
                title = cloneTitle;
                body = cloneBody;
                return true;
            }
            catch (Exception e)
            {
                if (obj != null) Object.Destroy(obj);
                failed = true;
                Log.Core.Error($"Couldn't build the tooltip from the game's", e);
                return false;
            }
        }

        private class Follower : MonoBehaviour
        {
            private void LateUpdate()
            {
                if (anchor == null) return;
                if (!anchor.gameObject.activeInHierarchy) Hide();
                else Place(anchor);
            }
        }

        private class SelectionSource : MonoBehaviour
        {
            private ThronefallUIElement element;
            private Func<(string, string)> content;

            public void Init(ThronefallUIElement element, Func<(string, string)> content)
            {
                this.content = content;
                if (this.element != null) return;
                this.element = element;
                element.onSelectionStateChange.AddListener(Refresh);
            }

            private void Refresh()
            {
                if (element.CurrentState == ThronefallUIElement.SelectionState.Default) Hide(this);
                else
                {
                    var (t, b) = content();
                    Show(t, b, (RectTransform)element.transform, this);
                }
            }

            private void OnDisable() => Hide(this);
        }

        /// <summary>
        /// Mouse-hover tooltips. Neighbouring targets' rects often overlap (a text's rect is taller than its line), so the
        /// mouse can be inside two at once, and targets update in no particular order. So targets don't show on enter and
        /// hide on exit; each only reports whether the mouse is inside it, and the tooltip goes to the hovered target whose
        /// centre is nearest the mouse (one with nothing to say is skipped). That's re-decided whenever the hovered set
        /// changes or the mouse moves, so moving between adjacent rows in either direction shows the row under the mouse.
        /// </summary>
        internal class HoverSource : MonoBehaviour
        {
            public Func<(string, string)> Content;
            private bool over;

            private static readonly List<HoverSource> hovered = [];
            private static Vector2 lastMouse;

            /// <summary>Dev: a screen point used instead of the real mouse (dev command "uikit mouse").</summary>
            internal static Vector2? SimulatedMouse;

            private static Vector2 Mouse => SimulatedMouse ?? (Vector2)Input.mousePosition;

            private void LateUpdate()
            {
                bool now = (SimulatedMouse != null || Cursor.visible)
                    && RectTransformUtility.RectangleContainsScreenPoint((RectTransform)transform, Mouse, null);
                if (now != over)
                {
                    SetOver(now);
                    return;
                }
                // Inside an overlap the nearest target changes as the mouse moves. Only re-decide when it has, so a
                // tooltip someone else hid (e.g. while the game's own tooltip is up) stays hidden until the mouse moves.
                if (over && hovered.Count > 1 && hovered[0] == this && Mouse != lastMouse) Arbitrate();
            }

            private void SetOver(bool now)
            {
                over = now;
                hovered.Remove(this);
                if (over) hovered.Add(this);
                Arbitrate();
            }

            private static void Arbitrate()
            {
                hovered.RemoveAll(h => h == null);
                var mouse = lastMouse = Mouse;
                foreach (var source in hovered.OrderBy(h => (Center(h) - mouse).sqrMagnitude))
                {
                    if (ReferenceEquals(owner, source) && Visible) return;
                    var (t, b) = source.Content();
                    // Nothing to say right now (the content can change): try the next one.
                    if (string.IsNullOrEmpty(t) && string.IsNullOrEmpty(b)) continue;
                    Show(t, b, (RectTransform)source.transform, source);
                    return;
                }
                if (owner is HoverSource) Hide();
            }

            private static Vector2 Center(HoverSource h)
            {
                var corners = new Vector3[4];
                ((RectTransform)h.transform).GetWorldCorners(corners); // screen pixels on the game's overlay canvas
                return (corners[0] + corners[2]) / 2f;
            }

            private void OnDisable()
            {
                if (over) SetOver(false);
            }
        }
    }
}
