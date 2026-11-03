using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    One scroll speed for every scrolling list, the game's and the mod's.

    Unity reports Input.mouseScrollDelta in "lines": about 1 per mouse-wheel notch, but a trackpad swipe on a Mac sends
    a stream of deltas every frame (plus momentum) that adds up to many lines. The game's ScrollArea (Settings >
    Controls, the level select's lists) moves its whole scroll range per line, so a swipe flung it from end to end.

    ScrollInput.Lines turns the frame's raw delta into lines to scroll. A wheel notch (a whole-number delta on a frame
    after one without scrolling) scrolls NotchLines. Otherwise (a trackpad, or a wheel spun fast) up to one line per
    frame counts fully, as in a slow drag, and anything above that per frame is damped, so a flick or its momentum
    doesn't fling the list. The result is scaled by Settings > Mods > General > "Scroll speed". ScrollInput.Pixels is
    that times PixelsPerLine. ModScroll (the mod's lists) scrolls by Pixels, and the patch below makes the game's
    ScrollArea scroll the same distance in pixels instead of a share of its range.
    Raw deltas are logged at Debug ("log debug ScrollInput") for tuning.
*/
namespace PalaceMod
{
    public static class ScrollInput
    {
        /// <summary>Distance one line scrolls (about one row of a menu).</summary>
        public const float PixelsPerLine = 60f;

        // A wheel notch scrolls this many lines; per frame of continuous scrolling, delta past FullLines counts at Damping.
        private const float NotchLines = 2f, FullLines = 1f, Damping = 0.3f;

        private static int frame = -1, lastScrollFrame = -10;
        private static float lines;

        internal static ConfigEntry<float> Speed;

        /// <summary>Lines to scroll this frame, positive = towards the top (like Input.mouseScrollDelta.y).</summary>
        public static float Lines
        {
            get
            {
                if (frame == Time.frameCount) return lines;
                frame = Time.frameCount;
                float raw = Input.mouseScrollDelta.y;
                float size = Mathf.Abs(raw);
                bool notch = size >= 1f && Mathf.Abs(size - Mathf.Round(size)) < 0.001f && lastScrollFrame < frame - 1;
                float amount = notch ? size * NotchLines : Mathf.Min(size, FullLines) + Mathf.Max(0f, size - FullLines) * Damping;
                lines = Mathf.Sign(raw) * amount * (Speed?.Value ?? 1f);
                if (raw != 0f) lastScrollFrame = frame;
                if (raw != 0f) Log.For<ScrollInputManager>().Debug($"Scroll delta {raw:0.###} -> {lines:0.###} lines");
                return lines;
            }
        }

        /// <summary>Pixels (in the canvas's reference units) to scroll this frame, positive = towards the top.</summary>
        public static float Pixels => Lines * PixelsPerLine;

        // The game's ScrollArea, given this frame's scroll in pixels as a share of its range (what it subtracts from
        // its 0..1 scroll value). Same range as ScrollArea.Update: the overflow plus an end buffer of 20% of the mask.
        internal static Vector2 ForScrollArea(ScrollArea area)
        {
            if (area == null || area.mask == null || area.content == null) return Input.mouseScrollDelta;
            float range = area.content.rect.height - area.mask.rect.height;
            if (range <= 0f) return Vector2.zero;
            range += area.mask.rect.height * 0.2f;
            return new Vector2(0f, Pixels / range);
        }
    }

    public class ScrollInputManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            ScrollInput.Speed = plugin.Config.Bind(GameplayFeatures.Section, "Scroll speed", 1f, new ConfigDescription(
                "How far the mouse wheel and trackpad scroll lists, in the game's menus (like Settings > Controls) and the mod's.",
                new AcceptableValueRange<float>(0.25f, 3f), new SettingTags { Step = 0.25f, Unit = "x" }));
            DevCommands.Register("scrollareas", "scrollareas   the game's active scroll areas: scroll position (0..1) and range in pixels", _ =>
                string.Join("\n", Object.FindObjectsOfType<ScrollArea>().Select(a =>
                    $"{a.name}: value {a.scrollValue:0.###}, content {a.content.rect.height:0}, mask {a.mask.rect.height:0}")));
        }

        // ScrollArea.Update does `desiredScrollValue -= Input.mouseScrollDelta.y`; read ScrollInput.ForScrollArea(this)
        // there instead.
        [HarmonyPatch(typeof(ScrollArea), "Update")]
        private static class ScrollArea_Update_Patch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var getter = AccessTools.PropertyGetter(typeof(Input), nameof(Input.mouseScrollDelta));
                var replacement = AccessTools.Method(typeof(ScrollInput), nameof(ScrollInput.ForScrollArea));
                int replaced = 0;
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(getter))
                    {
                        // Keep any labels on the first instruction of the replacement.
                        yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                        yield return new CodeInstruction(OpCodes.Call, replacement);
                        replaced++;
                        continue;
                    }
                    yield return instruction;
                }
                if (replaced != 1) Log.For<ScrollInputManager>().Warn($"ScrollArea.Update: expected one scroll-delta read, replaced {replaced}");
            }
        }
    }
}
