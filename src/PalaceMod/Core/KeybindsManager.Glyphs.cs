using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

/*
    Key glyphs. The game's keyboard_glyphs sheet only covers Alt, Ctrl, Shift, Space, Tab and the arrows (and its mouse
    sheet only the first three buttons); anything else shows its Rewired name as text ("Left Command", "Back Quote").
    branding/key_glyphs.py draws the missing keys in the same keycap style; this loads them as a TMP sprite asset and
    swaps them in wherever the game draws a binding (InputActionGlyph), so Cmd, F5 or a mouse side button look native.
*/
namespace PalaceMod
{
    internal sealed partial class KeybindsManager
    {
        private const string ModSheet = "palacemod_key_glyphs";

        // Right-hand modifiers use the left-hand key's art.
        private static readonly Dictionary<string, string> GlyphAliases = new()
        {
            ["Right Shift"] = "Left Shift",
            ["Right Control"] = "Left Control",
            ["Right Alt"] = "Left Alt",
            ["Right Command"] = "Left Command",
            ["Right Windows"] = "Left Windows",
        };

        private static TMP_SpriteAsset modGlyphs;
        private static Dictionary<string, string> modGlyphNames; // Rewired element name -> sprite name
        private static bool modGlyphsFailed;

        /// <summary>The rich-text glyph for a keyboard or mouse element (by its Rewired name), in the game's style: the
        /// game's sprite, else the mod's, else the name as text.</summary>
        internal static string ElementGlyph(string elementName, string color = "C8A674")
        {
            if (string.IsNullOrEmpty(elementName)) return "";
            string name = GlyphAliases.TryGetValue(elementName, out var alias) ? alias : elementName;
            var native = Resources.Load<TMP_SpriteAsset>("Sprite Assets/keyboard_glyphs");
            if (native != null && native.GetSpriteIndexFromName(name) >= 0) return SpriteTag("keyboard_glyphs", name, color);
            string mouse = name switch
            {
                "Left Mouse Button" => "click_l",
                "Right Mouse Button" => "click_r",
                "Mouse Button 3" => "click_m",
                _ => null,
            };
            if (mouse != null) return SpriteTag("mouse_glyphs", mouse, color);
            if (ModGlyphs() != null && modGlyphNames.TryGetValue(name, out var sprite)) return SpriteTag(ModSheet, sprite, color);
            return $"<color=#{color}>{elementName}</color>";
        }

        private static string SpriteTag(string sheet, string name, string color) =>
            putIntoVoffset($"<sprite=\"{sheet}\" name=\"{name}\" color=#{color}>");

        // Built on first use: the embedded sheet, with the native sheet's material and metrics so the caps match it in size.
        private static TMP_SpriteAsset ModGlyphs()
        {
            if (modGlyphs != null || modGlyphsFailed) return modGlyphs;
            try
            {
                var native = Resources.Load<TMP_SpriteAsset>("Sprite Assets/keyboard_glyphs")
                    ?? throw new InvalidOperationException("the game's keyboard_glyphs sheet is missing");
                // Shown at about its own size, so no mipmaps: blending in the half-size level blurs the small labels.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = ModSheet, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
                if (!texture.LoadImage(Resource(ModSheet + ".png"))) throw new InvalidOperationException($"{ModSheet}.png isn't an image");
                string index = System.Text.Encoding.UTF8.GetString(Resource(ModSheet + ".txt"));

                var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
                asset.name = ModSheet;
                asset.hideFlags = HideFlags.HideAndDontSave;
                // A blank version makes TMP "upgrade" the asset from its legacy list, wiping the tables.
                AccessTools.Field(typeof(TMP_SpriteAsset), "m_Version").SetValue(asset, native.version);
                AccessTools.Property(typeof(TMP_SpriteAsset), nameof(TMP_SpriteAsset.faceInfo)).SetValue(asset, native.faceInfo);
                asset.spriteSheet = texture;
                var material = new Material(native.material) { name = ModSheet + " Material", hideFlags = HideFlags.HideAndDontSave };
                material.SetTexture(ShaderUtilities.ID_MainTex, texture);
                asset.material = material;
                asset.materialHashCode = TMP_TextUtilities.GetSimpleHashCode(material.name);

                var names = new Dictionary<string, string>();
                var characters = (List<TMP_SpriteCharacter>)AccessTools.Field(typeof(TMP_SpriteAsset), "m_SpriteCharacterTable").GetValue(asset);
                uint glyphIndex = 0;
                foreach (var line in index.Split('\n'))
                {
                    var f = line.TrimEnd('\r').Split('\t');
                    if (f.Length != 6) continue;
                    int x = int.Parse(f[2]), y = int.Parse(f[3]), w = int.Parse(f[4]), h = int.Parse(f[5]);
                    var glyph = new TMP_SpriteGlyph(glyphIndex, new GlyphMetrics(w, h, 0, h, w), new GlyphRect(x, y, w, h), 1f, 0);
                    asset.spriteGlyphTable.Add(glyph);
                    characters.Add(new TMP_SpriteCharacter(0xFFFE, asset, glyph) { name = f[1] });
                    names[f[0]] = f[1];
                    glyphIndex++;
                }
                asset.UpdateLookupTables();
                MaterialReferenceManager.AddSpriteAsset(TMP_TextUtilities.GetSimpleHashCode(ModSheet), asset);
                modGlyphNames = names;
                modGlyphs = asset;
                Log.For<KeybindsManager>().Debug($"Loaded {names.Count} key glyphs");
            }
            catch (Exception e)
            {
                modGlyphsFailed = true;
                Log.For<KeybindsManager>().Warn($"Couldn't load the mod's key glyphs, keys without a game glyph show as text: {e.Message}");
            }
            return modGlyphs;
        }

        private static byte[] Resource(string file)
        {
            using var stream = typeof(KeybindsManager).Assembly.GetManifestResourceStream("PalaceMod.Assets." + file)
                ?? throw new FileNotFoundException("not embedded", file);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        // Keyboard and mouse bindings with no game glyph get the mod's, everywhere the game draws one (Controls rows,
        // hints). Controller glyphs are left alone. Patched by hand once Rewired is up: patching compiles the method,
        // which runs InputActionGlyph's static constructor, and that needs Rewired's players.
        private static bool glyphPatchApplied;

        private static void PatchGlyphs()
        {
            if (glyphPatchApplied) return;
            glyphPatchApplied = true;
            try
            {
                new Harmony($"{MyPluginInfo.PLUGIN_GUID}.KeyGlyphs").Patch(
                    AccessTools.Method(typeof(InputActionGlyph), nameof(InputActionGlyph.GetGlyphFromElementMap)),
                    postfix: new HarmonyMethod(typeof(GlyphFromElementMap_Patch), nameof(GlyphFromElementMap_Patch.Postfix)));
            }
            catch (Exception e)
            {
                Log.For<KeybindsManager>().Error("Couldn't patch the game's key glyphs; keys without one show as text", e);
            }
        }

        private static class GlyphFromElementMap_Patch
        {
            internal static void Postfix(ActionElementMap targetMap, bool useHighlightColor, ref string __result)
            {
                if (targetMap?.controllerMap == null || __result == null || !__result.StartsWith("<color=")) return;
                var type = targetMap.controllerMap.controllerType;
                if (type != ControllerType.Keyboard && type != ControllerType.Mouse) return;
                string glyph = ElementGlyph(targetMap.elementIdentifierName, useHighlightColor ? "FFE7C4" : "C8A674");
                if (!glyph.StartsWith("<color=")) __result = glyph;
            }
        }
    }
}
