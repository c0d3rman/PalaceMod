using System;
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
                Plugin.Logger.LogError($"ModTooltip: couldn't build the tooltip from the game's: {e}");
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

        private class HoverSource : MonoBehaviour
        {
            public Func<(string, string)> Content;
            private bool over;

            private void LateUpdate()
            {
                var rect = (RectTransform)transform;
                bool now = Cursor.visible && RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null);
                if (now == over) return;
                over = now;
                if (over)
                {
                    var (t, b) = Content();
                    // Nothing to say right now (the content can change): stay hidden.
                    if (string.IsNullOrEmpty(t) && string.IsNullOrEmpty(b)) over = false;
                    else Show(t, b, rect, this);
                }
                else Hide(this);
            }

            private void OnDisable()
            {
                over = false;
                Hide(this);
            }
        }
    }
}
