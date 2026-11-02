using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    The layout editor's status panel at the bottom of the screen: what's selected and the keys. Built like the
    notification panel (UI kit): the game's in-world tooltip panel, restyled and marked as PalaceMod's.
*/
namespace PalaceMod.Builder.Level
{
    internal sealed class LayoutHud
    {
        private const float Width = 920f, Pad = 18f, FontSize = 21f;
        private GameObject canvasObj, panel;
        private CanvasScaler scaler;
        private TextMeshProUGUI label;
        private string shown;

        public void Show(string text)
        {
            if (panel == null && !Build()) return;
            SyncScale();
            panel.SetActive(true);
            if (text == shown) return;
            shown = text;
            label.text = text;
            var size = label.GetPreferredValues(text, Width - 2 * Pad, 0f);
            label.rectTransform.sizeDelta = new Vector2(Width - 2 * Pad, Mathf.Ceil(size.y));
            ((RectTransform)panel.transform).sizeDelta = new Vector2(Width, Mathf.Ceil(size.y) + 2 * Pad - 4f);
        }

        public void Hide()
        {
            if (panel != null) panel.SetActive(false);
        }

        public void Destroy()
        {
            if (canvasObj != null) Object.Destroy(canvasObj);
            canvasObj = panel = null;
            shown = null;
        }

        private void SyncScale()
        {
            var game = GameWidgets.Canvas != null ? GameWidgets.Canvas.GetComponent<Canvas>() : null;
            scaler.scaleFactor = game != null ? game.scaleFactor : Mathf.Max(1f, Screen.height / 1080f);
        }

        private bool Build()
        {
            var tooltip = GameWidgets.HudTooltip;
            if (tooltip == null) return false;
            canvasObj = new GameObject("PalaceBuilder Layout HUD");
            Object.DontDestroyOnLoad(canvasObj);
            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31000;
            scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            try
            {
                panel = GameWidgets.Clone(tooltip, canvasObj.transform, "Layout Panel");
                label = panel.GetComponentInChildren<TextMeshProUGUI>(true);
                foreach (Transform child in panel.transform)
                    if (child != label.transform) child.gameObject.SetActive(false);
                foreach (var c in label.GetComponents<Component>())
                    if (c is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(c);
                panel.transform.localScale = Vector3.one;
                var rt = (RectTransform)panel.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 24f);
                var image = panel.GetComponent<MPUIKIT.MPImage>();
                if (image != null)
                {
                    image.color = new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.93f);
                    image.raycastTarget = false;
                    ModStyle.MarkPanel(image);
                }
                var lr = label.rectTransform;
                lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 0.5f);
                lr.pivot = new Vector2(0.5f, 0.5f);
                lr.anchoredPosition = Vector2.zero;
                label.font = ModStyle.BodyFont ?? label.font;
                label.fontSize = FontSize;
                label.color = ModStyle.Gold;
                label.alignment = TextAlignmentOptions.Center;
                label.enableWordWrapping = true;
                label.richText = true;
                label.raycastTarget = false;
                var emblem = ModStyle.AddEmblem(panel.transform, 11f);
                emblem.anchorMin = emblem.anchorMax = new Vector2(0f, 1f);
                emblem.anchoredPosition = new Vector2(14f, -14f);
                panel.AddComponent<CanvasGroup>().blocksRaycasts = false;
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError($"Builder: layout editor panel couldn't be built: {e}");
                Destroy();
                return false;
            }
        }
    }
}
