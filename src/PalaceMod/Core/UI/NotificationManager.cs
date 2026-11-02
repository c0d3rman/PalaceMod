using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Draws Notify messages: small panels stacked at the top centre of the screen, where the game's HUD has nothing
    (the interaction tooltip and control groups are top-left, gold and the chest bottom-right, the night counter
    bottom-centre).

    Each panel is a clone of the game's own in-world tooltip panel (rounded charcoal, Cormorant Garamond in the game's
    gold), marked as PalaceMod's with a thin verdigris edge and a diamond emblem; the emblem's colour hints at the
    NotifyKind. Messages fade and slide in and out on unscaled time, so they work in the pause menu (timescale 0).
    Callers go through Notify.Show and never touch this directly.

    Messages sent before the game's UI exists (e.g. at plugin load) wait until it does.
*/
namespace PalaceMod
{
    [ManagerOrder(-50)]
    public class NotificationManager : BaseManager
    {
        private const int MaxVisible = 5;
        private const float FadeIn = 0.18f, FadeOut = 0.45f;
        private const float TopMargin = 28f, Gap = 8f, MaxTextWidth = 560f;
        private const float PadLeft = 34f, PadRight = 20f, PadY = 8f;
        private const float FontSize = 24f;

        private class Message
        {
            public string Text, Key;
            public NotifyKind Kind;
            public float Shown, Until;
            public RectTransform Panel;
            public CanvasGroup Group;
            public TextMeshProUGUI Label;
            public Image Emblem;
            public float Y;            // current animated y (from the top, positive down)
            public bool Placed;
        }

        private readonly List<Message> messages = [];
        private readonly List<Message> pending = [];
        private GameObject canvasObj;
        private Canvas canvas;
        private CanvasScaler scaler;
        private GameObject template;
        private float nextTemplateCheck;

        internal void ShowNotification(string text, NotifyKind kind, float seconds, string key)
        {
            float now = Time.unscaledTime;
            if (key != null)
            {
                var existing = messages.Find(m => m.Key == key) ?? pending.Find(m => m.Key == key);
                if (existing != null)
                {
                    // Replace in place: same slot, new text, timer restarted.
                    existing.Text = text;
                    existing.Kind = kind;
                    existing.Until = now + seconds;
                    if (existing.Panel != null) Apply(existing);
                    return;
                }
            }
            var message = new Message { Text = text, Key = key, Kind = kind, Shown = now, Until = now + seconds };
            if (template == null) pending.Add(message);
            else Spawn(message);
        }

        public override void Update()
        {
            if (template == null)
            {
                if (Time.unscaledTime < nextTemplateCheck) return;
                nextTemplateCheck = Time.unscaledTime + 0.5f;
                if (!TryCreateTemplate()) return;
                foreach (var m in pending)
                {
                    // Start the clock now: the player hasn't seen it yet.
                    float duration = m.Until - m.Shown;
                    m.Shown = Time.unscaledTime;
                    m.Until = m.Shown + duration;
                    Spawn(m);
                }
                pending.Clear();
            }
            if (messages.Count == 0) return;

            SyncScale();
            float now = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;
            float y = TopMargin;
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m.Panel == null) continue;
                float appear = Mathf.Clamp01((now - m.Shown) / FadeIn);
                float vanish = Mathf.Clamp01((now - m.Until) / FadeOut);
                float alpha = Smooth(appear) * (1f - Smooth(vanish));
                m.Group.alpha = alpha;
                // Newest on top; a message slides into its slot, and the ones below move down to make room.
                float target = y;
                if (!m.Placed)
                {
                    m.Y = target - 10f;
                    m.Placed = true;
                }
                m.Y = Mathf.Lerp(m.Y, target, 1f - Mathf.Exp(-18f * dt));
                m.Panel.anchoredPosition = new Vector2(0f, -m.Y - (1f - Smooth(appear)) * 6f);
                y += (m.Panel.sizeDelta.y + Gap) * (1f - Smooth(vanish));
            }
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m.Panel == null || now >= m.Until + FadeOut)
                {
                    if (m.Panel != null) Object.Destroy(m.Panel.gameObject);
                    messages.RemoveAt(i);
                }
            }
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private void Spawn(Message m)
        {
            var obj = Object.Instantiate(template, canvasObj.transform, false);
            obj.name = "PalaceMod Notification";
            m.Panel = (RectTransform)obj.transform;
            m.Group = obj.GetComponent<CanvasGroup>();
            m.Label = obj.GetComponentInChildren<TextMeshProUGUI>(true);
            m.Emblem = obj.transform.Find("PalaceMod Emblem").GetComponent<Image>();
            m.Group.alpha = 0f;
            obj.SetActive(true);
            Apply(m);
            messages.Insert(0, m);
            // Too many: the oldest still showing starts fading now.
            int visible = 0;
            foreach (var other in messages)
            {
                if (Time.unscaledTime < other.Until && ++visible > MaxVisible) other.Until = Time.unscaledTime;
            }
        }

        private static void Apply(Message m)
        {
            m.Label.text = m.Text;
            ModStyle.TintEmblem(m.Emblem, ModStyle.KindColor(m.Kind));
            m.Label.color = m.Kind == NotifyKind.Error ? Color.Lerp(ModStyle.Gold, ModStyle.KindColor(NotifyKind.Error), 0.35f) : ModStyle.Gold;
            var size = m.Label.GetPreferredValues(m.Text, MaxTextWidth, 0f);
            float width = Mathf.Min(Mathf.Ceil(size.x) + 2f, MaxTextWidth);
            size = m.Label.GetPreferredValues(m.Text, width, 0f);
            var label = m.Label.rectTransform;
            label.sizeDelta = new Vector2(width, Mathf.Ceil(size.y));
            label.anchoredPosition = new Vector2(PadLeft, -PadY);
            m.Panel.sizeDelta = new Vector2(PadLeft + width + PadRight, Mathf.Ceil(size.y) + 2f * PadY);
            var emblem = (RectTransform)m.Emblem.transform;
            emblem.anchoredPosition = new Vector2(PadLeft * 0.5f + 2f, -m.Panel.sizeDelta.y * 0.5f);
            if (m.Shown < Time.unscaledTime - FadeIn) m.Shown = Time.unscaledTime - FadeIn; // replaced: stay fully visible
        }

        // Our canvas sits above the game's UI and uses the same scale (which includes the game's UI-scale setting).
        private void SyncScale()
        {
            var game = GameWidgets.Canvas != null ? GameWidgets.Canvas.GetComponent<Canvas>() : null;
            scaler.scaleFactor = game != null ? game.scaleFactor : Mathf.Max(1f, Screen.height / 1080f);
        }

        private void EnsureCanvas()
        {
            if (canvasObj != null) return;
            canvasObj = new GameObject("PalaceMod Notifications");
            Object.DontDestroyOnLoad(canvasObj);
            canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000; // above the game's UI (1000), its transitions and our own frames
            scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            SyncScale();
        }

        // The panel template: the game's in-world tooltip panel and text, restyled and marked as ours.
        private bool TryCreateTemplate()
        {
            var tooltip = GameWidgets.HudTooltip;
            if (tooltip == null) return false;
            EnsureCanvas();
            GameObject obj = null;
            try
            {
                obj = GameWidgets.Clone(tooltip, canvasObj.transform, "PalaceMod Notification Template");
                var text = obj.GetComponentInChildren<TextMeshProUGUI>(true) ?? throw new System.InvalidOperationException("tooltip has no text");
                foreach (Transform child in obj.transform)
                {
                    if (child != text.transform) child.gameObject.SetActive(false);
                }
                foreach (var c in text.GetComponents<Component>())
                {
                    if (c is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(c);
                }
                obj.transform.localScale = Vector3.one; // the game's Tooltip starts at scale 0 and animates open
                var panel = (RectTransform)obj.transform;
                panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 1f);
                panel.pivot = new Vector2(0.5f, 1f);
                var image = obj.GetComponent<MPUIKIT.MPImage>();
                if (image != null)
                {
                    image.color = new Color(ModStyle.Panel.r, ModStyle.Panel.g, ModStyle.Panel.b, 0.97f);
                    image.raycastTarget = false;
                    ModStyle.MarkPanel(image);
                }
                var label = text.rectTransform;
                label.anchorMin = label.anchorMax = new Vector2(0f, 1f);
                label.pivot = new Vector2(0f, 1f);
                text.font = ModStyle.BodyFont ?? text.font;
                text.fontSize = FontSize;
                text.alignment = TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = true;
                text.overflowMode = TextOverflowModes.Overflow;
                text.raycastTarget = false;
                text.richText = true;
                var emblem = ModStyle.AddEmblem(obj.transform, 11f);
                emblem.anchorMin = emblem.anchorMax = new Vector2(0f, 1f);
                obj.AddComponent<CanvasGroup>().blocksRaycasts = false;
                obj.SetActive(false);
                template = obj;
                Log.For<NotificationManager>().Debug("Using the game's tooltip panel");
                return true;
            }
            catch (System.Exception e)
            {
                if (obj != null) Object.Destroy(obj);
                Log.For<NotificationManager>().Error($"Couldn't build the panel from the game's tooltip", e);
                nextTemplateCheck = float.PositiveInfinity; // don't retry every half second
                return false;
            }
        }

        public override void OnDestroy()
        {
            if (canvasObj != null) Object.Destroy(canvasObj);
        }
    }
}
