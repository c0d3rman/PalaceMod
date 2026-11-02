using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    The game's UI palette and fonts, plus PalaceMod's visual signature.

    Everything PalaceMod draws uses the game's own look (charcoal panels, gold-tan Cormorant/Unifraktur text) with one
    consistent mark that says "this is from the mod": a small verdigris gem (the emblem from PalaceMod's logo), and on
    panels a thin verdigris edge. The game's UI has no cool colours at all, so the mark reads as foreign at a glance
    without any text. Use ModStyle.AddEmblem / ModStyle.MarkPanel rather than inventing another accent.

    branding/ is the source of truth for the emblem and the accent colour: branding/build.py renders the textures in
    Assets/ (palacemod_emblem*.png, palacemod_mark.png), and branding/README.md lists the palette.
*/
namespace PalaceMod
{
    public static class ModStyle
    {
        // The game's palette (read from its UI at runtime; see `dev.py cmd "uidump pause 4"`).
        public static readonly Color Gold = new(0.784f, 0.651f, 0.455f);        // #C8A674, all native text
        public static readonly Color Dim = new(0.490f, 0.431f, 0.341f);         // #7D6E57, disabled text, arrows, slider tracks
        public static readonly Color Panel = new(0.102f, 0.110f, 0.114f);       // #1A1C1D, every native panel and backdrop
        public static readonly Color Bright = new(1f, 0.906f, 0.769f);          // #FFE7C4, highlighted numbers

        /// <summary>PalaceMod's signature colour (verdigris, the emblem's main facet). Only for the emblem and panel edges.</summary>
        public static readonly Color Accent = new(0.42f, 0.78f, 0.69f);         // #6BC7B0, see branding/README.md

        /// <summary>Emblem tint per notification kind; Info uses the signature colour.</summary>
        public static Color KindColor(NotifyKind kind) => kind switch
        {
            NotifyKind.Success => new Color(0.56f, 0.80f, 0.45f),
            NotifyKind.Warning => new Color(0.90f, 0.66f, 0.28f),
            NotifyKind.Error => new Color(0.86f, 0.38f, 0.32f),
            _ => Accent,
        };

        // ---- Fonts ----------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, TMP_FontAsset> fonts = [];

        /// <summary>A game font by asset name, e.g. "CormorantGaramond-Light SDF"; null if the game doesn't have it.</summary>
        public static TMP_FontAsset Font(string name)
        {
            if (fonts.TryGetValue(name, out var font) && font != null) return font;
            font = Resources.Load<TMP_FontAsset>("fonts & materials/" + name)
                ?? Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == name);
            if (font != null) fonts[name] = font;
            return font;
        }

        /// <summary>Blackletter used for the game's titles ("Paused", "Settings").</summary>
        public static TMP_FontAsset TitleFont => Font("UnifrakturCook-Bold SDF");
        /// <summary>Body text (tooltips, descriptions).</summary>
        public static TMP_FontAsset BodyFont => Font("CormorantGaramond-Light SDF");
        /// <summary>Menu entries and buttons.</summary>
        public static TMP_FontAsset ButtonFont => Font("CormorantGaramond-LightItalic SDF");
        /// <summary>Values and section labels.</summary>
        public static TMP_FontAsset BoldFont => Font("CormorantGaramond-Bold SDF");

        // ---- Signature ------------------------------------------------------------------------------------------

        /// <summary>Adds the mod emblem (the logo's gem, about <paramref name="size"/> tall) as a child of <paramref name="parent"/>.</summary>
        public static RectTransform AddEmblem(Transform parent, float size = 10f, Color? color = null)
        {
            var obj = new GameObject("PalaceMod Emblem", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            var image = obj.GetComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;
            var rect = (RectTransform)obj.transform;
            if (EmblemSprite != null)
                rect.sizeDelta = new Vector2(size, size) * 1.2f; // the gem is narrower than a square diamond; this matches its weight
            else
            {
                // No texture: a plain diamond, i.e. a rotated square whose diagonal is `size`.
                rect.sizeDelta = new Vector2(size, size) / Mathf.Sqrt(2f);
                rect.localRotation = Quaternion.Euler(0f, 0f, 45f);
            }
            TintEmblem(image, color);
            obj.AddComponent<LayoutElement>().ignoreLayout = true;
            return rect;
        }

        /// <summary>
        /// Colours an emblem made by <see cref="AddEmblem"/>. The signature colour shows the gem as the logo draws it;
        /// any other colour tints a grey copy, so the facets keep their shading.
        /// </summary>
        public static void TintEmblem(Image emblem, Color? color = null)
        {
            if (emblem == null) return;
            var c = color ?? Accent;
            bool signature = c == Accent;
            var sprite = signature ? EmblemSprite : EmblemTintSprite;
            if (sprite == null || emblem.transform.localRotation != Quaternion.identity) // the plain-diamond fallback
            {
                emblem.color = c;
                return;
            }
            emblem.sprite = sprite;
            emblem.color = signature ? Color.white : c;
        }

        /// <summary>The emblem as the logo draws it (null if the texture didn't load).</summary>
        public static Sprite EmblemSprite => emblemSprite ??= AssetSprite("palacemod_emblem.png");
        /// <summary>The emblem in greys, for tinting.</summary>
        public static Sprite EmblemTintSprite => emblemTintSprite ??= AssetSprite("palacemod_emblem_tint.png");
        /// <summary>The full mark (the gate and gem), cut for 16-32 px.</summary>
        public static Sprite MarkSprite => markSprite ??= AssetSprite("palacemod_mark.png");
        private static Sprite emblemSprite, emblemTintSprite, markSprite;
        private static readonly HashSet<string> failedAssets = [];

        /// <summary>A sprite from an image embedded from src/PalaceMod/Assets, or null (logged once) if it can't load.</summary>
        internal static Sprite AssetSprite(string file)
        {
            if (failedAssets.Contains(file)) return null;
            try
            {
                using var stream = typeof(ModStyle).Assembly.GetManifestResourceStream("PalaceMod.Assets." + file)
                    ?? throw new System.IO.FileNotFoundException("not embedded", file);
                using var buffer = new System.IO.MemoryStream();
                stream.CopyTo(buffer);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = file };
                if (!texture.LoadImage(buffer.ToArray())) throw new System.InvalidOperationException("not an image");
                texture.filterMode = FilterMode.Trilinear;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.hideFlags = HideFlags.HideAndDontSave;
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.hideFlags = HideFlags.HideAndDontSave;
                return sprite;
            }
            catch (System.Exception e)
            {
                failedAssets.Add(file);
                Log.Core.Warn($"Couldn't load {file}: {e.Message}");
                return null;
            }
        }

        /// <summary>Gives a cloned game panel (an MPImage) the mod's edge: a thin verdigris outline.</summary>
        public static void MarkPanel(MPUIKIT.MPImage panel, float width = 1.5f)
        {
            if (panel == null) return;
            panel.OutlineWidth = width;
            panel.OutlineColor = new Color(Accent.r, Accent.g, Accent.b, 0.65f);
        }

        /// <summary>A thin horizontal rule in the accent colour, e.g. under a mod frame's title.</summary>
        public static RectTransform AddRule(Transform parent, float width, float thickness = 2f)
        {
            var obj = new GameObject("PalaceMod Rule", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            var image = obj.GetComponent<Image>();
            image.color = new Color(Accent.r, Accent.g, Accent.b, 0.6f);
            image.raycastTarget = false;
            var rect = (RectTransform)obj.transform;
            rect.sizeDelta = new Vector2(width, thickness);
            return rect;
        }

        internal static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
    }

    /// <summary>Keeps an emblem just left of a TMP text's visible glyphs, wherever alignment and size put them.</summary>
    internal class EmblemFollower : MonoBehaviour
    {
        public TextMeshProUGUI Text;
        public float Gap = 14f;
        private RectTransform rect;

        private void LateUpdate()
        {
            if (Text == null) return;
            rect ??= (RectTransform)transform;
            var bounds = Text.textBounds;
            if (bounds.size.x <= 0f) return;
            // textBounds is in the text's local space; the emblem is the text's child.
            rect.localPosition = new Vector3(bounds.min.x - Gap, bounds.center.y, 0f);
        }
    }
}
