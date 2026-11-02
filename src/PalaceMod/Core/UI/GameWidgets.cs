using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

/*
    Finds the game's own UI widgets to clone, and clones them cleanly.

    Templates are found through the fields of the game's UI scripts (UIFrameManager, PauseUILoadoutHelper,
    PerkSelectionTooltipHelper, TooltipManager) and by component type, never by GameObject path, so a renamed or
    re-parented object in a game update doesn't break them. Clones are stripped of the game's localisation (which
    would overwrite our text) and of every game script except the visual ones, and their click handlers are cleared.
*/
namespace PalaceMod
{
    public static class GameWidgets
    {
        /// <summary>The UI canvas everything native lives on (holds UIFrameManager); null before the UI scene loads.</summary>
        public static Transform Canvas => UIFrameManager.instance != null ? UIFrameManager.instance.transform : null;

        /// <summary>The in-match pause frame.</summary>
        public static UIFrame PauseFrame =>
            UIFrameManager.instance == null ? null : Traverse.Create(UIFrameManager.instance).Field("inMatchPauseMenuFrame").GetValue<UIFrame>();

        /// <summary>The pause frame's helper: its fields point at Continue, Settings, Restart, Back and the Night row.</summary>
        public static PauseUILoadoutHelper PauseHelper => PauseFrame != null ? PauseFrame.GetComponent<PauseUILoadoutHelper>() : null;

        /// <summary>A menu entry (the pause menu's Settings button): TFUITextButton on a TMP, CormorantGaramond italic.</summary>
        public static TFUITextButton MenuButton => PauseHelper?.settingsElement as TFUITextButton;

        /// <summary>A screen title (the pause menu's "Paused"): Unifraktur, 90 pt. Has the menu as children.</summary>
        public static TextMeshProUGUI Title
        {
            get
            {
                var menu = MenuButton;
                return menu == null ? null : menu.transform.parent?.parent?.GetComponent<TextMeshProUGUI>();
            }
        }

        /// <summary>The pause menu's full-screen backdrop: the first full-stretch MPImage directly under the frame.</summary>
        public static MPUIKIT.MPImage Backdrop
        {
            get
            {
                var frame = PauseFrame;
                if (frame == null) return null;
                foreach (Transform child in frame.transform)
                {
                    if (child.GetComponent<MPUIKIT.MPImage>() is { } image && child is RectTransform r && r.anchorMin == Vector2.zero && r.anchorMax == Vector2.one)
                    {
                        return image;
                    }
                }
                return null;
            }
        }

        /// <summary>The pause menu's selection tooltip (title, description, rounded charcoal panel).</summary>
        public static PerkSelectionTooltipHelper PauseTooltip => PauseFrame != null ? PauseFrame.GetComponent<PerkSelectionTooltipHelper>() : null;

        /// <summary>The in-world interaction tooltip ("Build Tower: 5 gold"): a small rounded panel with one text.</summary>
        public static Tooltip HudTooltip => TooltipManager.instance != null ? TooltipManager.instance.targetTooltip : null;

        /// <summary>A Settings tab button (Video), for tab rows.</summary>
        public static TFUITextButton TabButton
        {
            get
            {
                var helper = Resources.FindObjectsOfTypeAll<SettingsUIHelper>().FirstOrDefault(h => h.gameObject.scene.IsValid());
                return helper?.videoTab?.parentElement as TFUITextButton;
            }
        }

        // ---- Cloning --------------------------------------------------------------------------------------------

        // Game scripts a clone may keep: they only draw or animate it. Everything else (settings binders, helpers
        // that reach into game state, localisation) is removed.
        private static readonly HashSet<Type> KeepTypes =
        [
            typeof(TFUITextButton), typeof(TFUIAudioHelper), typeof(TFUIEnumSelectorButton),
        ];

        /// <summary>The pause menu's Night row arrows (decrease, increase): clickable MPImage arrows that light up
        /// on hover. Null if the game's layout changed.</summary>
        public static (TFUIEnumSelectorButton decrease, TFUIEnumSelectorButton increase) Arrows
        {
            get
            {
                var arrows = PauseHelper?.nightButton?.GetComponentsInChildren<TFUIEnumSelectorButton>(true);
                if (arrows == null || arrows.Length < 2) return (null, null);
                // Order by x: the left one decreases.
                var sorted = arrows.OrderBy(a => a.transform.position.x).ToArray();
                return (sorted[0], sorted[sorted.Length - 1]);
            }
        }

        /// <summary>
        /// Instantiates <paramref name="source"/> under <paramref name="parent"/>, inactive, and strips it: no
        /// localisation, no game logic scripts, no click handlers. Activate it yourself once it's set up.
        /// </summary>
        public static GameObject Clone(Component source, Transform parent, string name, bool keepChildren = true)
        {
            if (source == null) throw new InvalidOperationException($"no game widget to clone for {name}");
            // Instantiate under an inactive staging object so no Awake/OnEnable runs on the copy before it's
            // stripped, without touching the source.
            if (staging == null)
            {
                staging = new GameObject("PalaceMod Staging");
                staging.SetActive(false);
                Object.DontDestroyOnLoad(staging);
            }
            var obj = Object.Instantiate(source.gameObject, staging.transform, false);
            obj.SetActive(false);
            obj.transform.SetParent(parent, false);
            obj.name = name;
            if (!keepChildren)
            {
                foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            }
            Strip(obj);
            return obj;
        }

        /// <summary>The object at the same place under <paramref name="clone"/> as <paramref name="target"/> is under
        /// <paramref name="source"/> (by sibling index, so it survives renames).</summary>
        public static Transform Corresponding(Transform source, Transform target, Transform clone)
        {
            var path = new Stack<int>();
            for (var t = target; t != source; t = t.parent)
            {
                if (t == null) throw new InvalidOperationException($"{target.name} isn't under {source.name}");
                path.Push(t.GetSiblingIndex());
            }
            var result = clone;
            while (path.Count > 0) result = result.GetChild(path.Pop());
            return result;
        }

        private static GameObject staging;

        private static void Strip(GameObject root)
        {
            var gameAssembly = typeof(UIFrame).Assembly;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                bool game = type.Assembly == gameAssembly || type.Namespace?.StartsWith("I2.Loc") == true || type.Namespace?.StartsWith("MoreMountains") == true;
                if (game && !KeepTypes.Contains(type)) Object.DestroyImmediate(behaviour);
            }
            foreach (var element in root.GetComponentsInChildren<ThronefallUIElement>(true))
            {
                // Persistent (scene-wired) handlers would run the original button's action.
                element.onApply = new UnityEvent();
                element.leftNav = element.rightNav = element.topNav = element.botNav = null;
            }
        }

        /// <summary>
        /// A clone of a game text button with a new label and click action, inactive. The clone starts from the
        /// source's resting look even if the source is highlighted right now.
        /// </summary>
        public static TFUITextButton Button(TFUITextButton source, Transform parent, string name, string label, Action onClick)
        {
            if (source == null) throw new InvalidOperationException($"no button to clone for {name}");
            var obj = Clone(source, parent, name);
            var button = obj.GetComponent<TFUITextButton>();
            var text = obj.GetComponent<TextMeshProUGUI>();
            // TFUITextButton records its resting style from the text the first time it's used; the copied text has
            // whatever the source is animated to right now (e.g. its "selected" colour), so put the resting style back.
            var rest = Traverse.Create(source).Field("defaultStyle").GetValue<TFUITextButton.Style>();
            if (rest != null && Traverse.Create(source).Field("defaultStyleInitialized").GetValue<bool>())
            {
                text.color = rest.color;
                text.fontSize = rest.fontSize;
                if (rest.font != null) text.font = rest.font;
            }
            Traverse.Create(button).Field("defaultStyleInitialized").SetValue(false);
            button.SetOriginalString(label);
            if (onClick != null)
            {
                button.onApply.AddListener(() =>
                {
                    try { onClick(); }
                    catch (Exception e) { Log.Core.Caught($"{name}: click handler failed", e); }
                });
            }
            return button;
        }

        /// <summary>A plain text clone (from the pause tooltip's description), restyled.</summary>
        public static TextMeshProUGUI Text(Transform parent, string name, string text, TMP_FontAsset font, float size, Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
        {
            var source = PauseTooltip?.selectionDescription ?? throw new InvalidOperationException("no text template");
            var obj = Clone(source, parent, name, keepChildren: false);
            foreach (var c in obj.GetComponents<Component>())
            {
                if (c is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(c);
            }
            var tmp = obj.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.raycastTarget = false;
            tmp.text = text;
            obj.SetActive(true);
            return tmp;
        }
    }
}
