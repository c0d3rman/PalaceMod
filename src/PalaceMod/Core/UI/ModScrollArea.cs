using UnityEngine;
using UnityEngine.UI;

/*
    A scrolling box for hand-laid-out content (a list or grid inside a wide page or a popup), instead of cutting a list
    short with "and 3 more":

        var area = ModScrollArea.Create(parent, new Vector2(400f, 300f), frame);   // frame: the UIFrame it's in
        area.Place(new Vector2(x, -y));
        ... add children to area.Content at (x, -y) from its top left ...
        area.SetContentHeight(totalHeight);

    The wheel scrolls it while the pointer is over it, the selection is kept in view (keyboard and controller), and a
    thin scroll bar shows beside it when the content is taller than the box.
*/
namespace PalaceMod
{
    public sealed class ModScrollArea
    {
        public RectTransform Viewport { get; private set; }
        public RectTransform Content { get; private set; }
        private ModScroll scroll;

        public static ModScrollArea Create(Transform parent, Vector2 size, UIFrame frame, float barGap = 6f)
        {
            var view = new GameObject("Scroll Area", typeof(RectTransform), typeof(RectMask2D));
            view.transform.SetParent(parent, false);
            var viewport = (RectTransform)view.transform;
            viewport.anchorMin = viewport.anchorMax = viewport.pivot = new Vector2(0f, 1f);
            viewport.sizeDelta = size;
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(view.transform, false);
            var rect = (RectTransform)content.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(size.x, size.y);
            var area = new ModScrollArea { Viewport = viewport, Content = rect };
            if (frame != null)
            {
                area.scroll = view.AddComponent<ModScroll>();
                area.scroll.WheelNeedsHover = true;
                area.scroll.BarGap = barGap;
                area.scroll.Init(frame, viewport, rect);
            }
            return area;
        }

        public ModScrollArea Place(Vector2 topLeft)
        {
            Viewport.anchoredPosition = topLeft;
            return this;
        }

        /// <summary>How tall the content is (it scrolls when that's more than the box).</summary>
        public void SetContentHeight(float height)
        {
            Content.sizeDelta = new Vector2(Viewport.sizeDelta.x, Mathf.Max(height, 1f));
            scroll?.Clamp();
        }

        /// <summary>Scrolls to <paramref name="y"/> pixels down at once (e.g. to keep a position across a rebuild).</summary>
        public void ScrollTo(float y) => scroll?.ResetTo(Mathf.Clamp(y, 0f, Mathf.Max(0f, Content.sizeDelta.y - Viewport.sizeDelta.y)));

        /// <summary>How far down it's scrolled.</summary>
        public float ScrollY => Content.anchoredPosition.y;
    }
}
