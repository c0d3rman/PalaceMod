using UnityEngine;
using UnityEngine.UI;
using System.Collections;

namespace PalaceMod
{
    public class UIManager : BaseManager
    {
        private GameObject canvasObj;
        private GameObject feedbackObj;
        private Text feedbackText;
        private MonoBehaviour coroutineRunner;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            CreateModIndicator();
            coroutineRunner = plugin;
        }

        public void ShowMessage(string message)
        {
            if (feedbackText != null && coroutineRunner != null)
            {
                feedbackText.text = message;
                coroutineRunner.StartCoroutine(FadeMessage());
            }
        }

        private void CreateModIndicator()
        {
            // Create canvas as root object
            canvasObj = new GameObject("ModCanvas");
            GameObject.DontDestroyOnLoad(canvasObj);

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999; // Ensure it renders on top

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasObj.AddComponent<GraphicRaycaster>();

            // Create feedback text object
            feedbackObj = new GameObject("FeedbackText");
            feedbackObj.transform.SetParent(canvasObj.transform, false);

            // Add Text component for feedback
            feedbackText = feedbackObj.AddComponent<Text>();
            feedbackText.text = "";
            feedbackText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            feedbackText.fontSize = 32;
            feedbackText.color = new Color(1, 1, 0, 1);
            feedbackText.alignment = TextAnchor.UpperLeft;

            // Position feedback
            var feedbackTransform = feedbackText.GetComponent<RectTransform>();
            feedbackTransform.anchorMin = new Vector2(0, 1);
            feedbackTransform.anchorMax = new Vector2(0, 1);
            feedbackTransform.pivot = new Vector2(0, 1);
            feedbackTransform.anchoredPosition = new Vector2(10, -10);
            feedbackTransform.sizeDelta = new Vector2(500, 50);
        }

        private IEnumerator FadeMessage()
        {
            if (feedbackText != null)
            {
                feedbackText.color = new Color(1, 1, 0, 1);
                yield return new WaitForSeconds(2f);

                float elapsed = 0;
                while (elapsed < 1f && feedbackText != null)
                {
                    elapsed += Time.deltaTime;
                    feedbackText.color = new Color(1, 1, 0, 1 - elapsed);
                    yield return null;
                }

                if (feedbackText != null)
                    feedbackText.text = "";
            }
        }

        public override void OnDestroy()
        {
            if (canvasObj != null)
            {
                GameObject.Destroy(canvasObj);
            }
        }
    }
}
