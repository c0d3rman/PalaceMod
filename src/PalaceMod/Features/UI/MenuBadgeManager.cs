using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/*
    A small "PalaceMod <version>" line with the mod's mark on the title screen, just above the game's own version text,
    in the same font, size and colour. Tells players at a glance that the mod is installed, and which build.
*/
namespace PalaceMod
{
    public class MenuBadgeManager : BaseManager
    {
        public override string FeatureSection => "Title screen badge";
        public override string FeatureDescription => "Shows PalaceMod's mark and version above the game's version number on the title screen.";

        private const string BadgeName = "PalaceMod Badge";
        private TextMeshProUGUI badge;
        private bool looked;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            SceneManager.sceneLoaded += (_, _) => looked = false;
            DevCommands.Register("badge", "badge   report the title-screen badge and the version text it sits on", _ =>
                badge != null ? $"badge \"{badge.text}\" at {((RectTransform)badge.transform).anchoredPosition} under {badge.transform.parent.name}"
                              : "no badge (not on the title screen, or the feature is off)");
        }

        public override void Update()
        {
            if (looked || badge != null) return;
            looked = true; // one search per scene; the version text is created with the title screen
            var version = Object.FindObjectOfType<VersionNumberDisplay>();
            if (version == null || version.display == null) return;
            badge = Create(version.display);
        }

        protected override void OnFeatureDisabled()
        {
            if (badge != null) Object.Destroy(badge.gameObject);
            badge = null;
            looked = false;
        }

        protected override void OnFeatureEnabled() => looked = false;

        private static TextMeshProUGUI Create(TextMeshProUGUI version)
        {
            var obj = GameWidgets.Clone(version, version.transform.parent, BadgeName, keepChildren: false);
            var text = obj.GetComponent<TextMeshProUGUI>();
            text.text = $"PalaceMod {MyPluginInfo.PLUGIN_VERSION}";
            text.raycastTarget = false;
            // One line above the game's text, wherever its anchors put it.
            var rect = (RectTransform)obj.transform;
            float line = Mathf.Max(version.fontSize * 1.25f, version.preferredHeight);
            rect.anchoredPosition += new Vector2(0f, version.alignment.ToString().StartsWith("Top") ? -line : line);

            var sprite = ModStyle.MarkSprite;
            if (sprite != null)
            {
                var markObj = new GameObject("PalaceMod Mark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                markObj.transform.SetParent(obj.transform, false);
                var image = markObj.GetComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
                float size = Mathf.Round(version.fontSize * 1.15f);
                ((RectTransform)markObj.transform).sizeDelta = new Vector2(size, size);
                markObj.AddComponent<LayoutElement>().ignoreLayout = true;
                var follower = markObj.AddComponent<EmblemFollower>();
                follower.Text = text;
                follower.Gap = size * 0.5f + version.fontSize * 0.3f;
            }
            obj.SetActive(true);
            Plugin.Logger.LogInfo($"Title badge: added above \"{version.text}\"");
            return text;
        }
    }
}
