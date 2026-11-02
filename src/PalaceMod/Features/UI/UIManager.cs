using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Text;

/*
    Draws Notify messages: a stack of lines in the top-left corner, each fading out after its duration.
    Placeholder visuals; callers go through Notify.Show and never touch this directly.
*/
namespace PalaceMod
{
    [ManagerOrder(-50)]
    public class UIManager : BaseManager
    {
        private const int MaxLines = 6;
        private const float FadeSeconds = 1f;

        private GameObject canvasObj;
        private Text feedbackText;

        private class Message
        {
            public string Text, Key;
            public NotifyKind Kind;
            public float Until;
        }

        private readonly List<Message> messages = [];
        private readonly StringBuilder builder = new();

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            CreateCanvas();
        }

        internal void ShowNotification(string text, NotifyKind kind, float seconds, string key)
        {
            if (key != null) messages.RemoveAll(m => m.Key == key);
            messages.Add(new Message { Text = text, Key = key, Kind = kind, Until = Time.unscaledTime + seconds });
            if (messages.Count > MaxLines) messages.RemoveRange(0, messages.Count - MaxLines);
            Redraw();
        }

        public override void Update()
        {
            if (messages.Count == 0) return;
            float now = Time.unscaledTime;
            messages.RemoveAll(m => now >= m.Until + FadeSeconds);
            Redraw();
        }

        private void Redraw()
        {
            if (feedbackText == null) return;
            float now = Time.unscaledTime;
            builder.Clear();
            foreach (var m in messages)
            {
                float alpha = Mathf.Clamp01(1f - (now - m.Until) / FadeSeconds);
                Color color = m.Kind switch
                {
                    NotifyKind.Success => new Color(0.5f, 1f, 0.5f),
                    NotifyKind.Warning => new Color(1f, 0.7f, 0.2f),
                    NotifyKind.Error => new Color(1f, 0.4f, 0.4f),
                    _ => new Color(1f, 1f, 0f),
                };
                color.a = alpha;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append("<color=#").Append(ColorUtility.ToHtmlStringRGBA(color)).Append('>').Append(m.Text).Append("</color>");
            }
            feedbackText.text = builder.ToString();
        }

        private void CreateCanvas()
        {
            canvasObj = new GameObject("ModCanvas");
            Object.DontDestroyOnLoad(canvasObj);

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999; // render on top

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var feedbackObj = new GameObject("FeedbackText");
            feedbackObj.transform.SetParent(canvasObj.transform, false);
            feedbackText = feedbackObj.AddComponent<Text>();
            feedbackText.text = "";
            feedbackText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            feedbackText.fontSize = 32;
            feedbackText.supportRichText = true;
            feedbackText.raycastTarget = false;
            feedbackText.alignment = TextAnchor.UpperLeft;
            feedbackText.horizontalOverflow = HorizontalWrapMode.Overflow;
            feedbackText.verticalOverflow = VerticalWrapMode.Overflow;

            var rect = feedbackText.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(10, -10);
            rect.sizeDelta = new Vector2(900, 50);
        }

        public override void OnDestroy()
        {
            if (canvasObj != null) Object.Destroy(canvasObj);
        }
    }
}
