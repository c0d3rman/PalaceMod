using System.Linq;
using HarmonyLib;
using UnityEngine;

/*
    Dev only: a fake mouse that the game itself sees, for testing menus without a human (dev command "uikit mouse").
    Patched in on first use. While a position is set, Rewired's mouse position and Unity's Input.mousePosition report it,
    so the game's UIFrame raycasts and hovers through its normal path; Click makes the left button go down for one frame
    and up a few frames later (Rewired's Mouse.GetButton* and Input.GetMouseButton*); Scroll adds to
    Input.mouseScrollDelta for a number of frames, like a wheel notch or a trackpad swipe.
*/
namespace PalaceMod
{
    internal static class SimulatedPointer
    {
        private const int ClickFrames = 3;

        /// <summary>Screen point the game sees as the mouse; null = the real mouse.</summary>
        internal static Vector2? Position { get; private set; }

        private static int clickStart = -1;
        private static float scrollPerFrame;
        private static int scrollStart = -1, scrollFrames;
        private static bool patched;

        internal static void MoveTo(Vector2 point)
        {
            EnsurePatched();
            Position = point;
            ModTooltip.HoverSource.SimulatedMouse = point;
            Cursor.visible = true; // the game hides an idle cursor and then ignores the mouse
        }

        internal static void Release()
        {
            Position = null;
            ModTooltip.HoverSource.SimulatedMouse = null;
            clickStart = -1;
            scrollFrames = 0;
        }

        internal static void Click()
        {
            EnsurePatched();
            clickStart = Time.frameCount + 1;
        }

        /// <summary>Adds <paramref name="perFrame"/> to the scroll delta on each of the next <paramref name="frames"/> frames.</summary>
        internal static void Scroll(float perFrame, int frames)
        {
            EnsurePatched();
            scrollPerFrame = perFrame;
            scrollFrames = frames;
            scrollStart = Time.frameCount + 1;
        }

        private static int ClickFrame => clickStart >= 0 ? Time.frameCount - clickStart : -1;

        private static void EnsurePatched()
        {
            if (patched) return;
            patched = true;
            var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.SimulatedPointer");
            void Postfix(System.Reflection.MethodBase target, string patch)
            {
                if (target == null) Log.Core.Warn($"SimulatedPointer: nothing to patch for {patch}");
                else harmony.Patch(target, postfix: new HarmonyMethod(typeof(SimulatedPointer), patch));
            }
            Postfix(AccessTools.PropertyGetter(typeof(Rewired.Mouse), nameof(Rewired.Mouse.screenPosition)), nameof(ScreenPosition));
            // The getter is small enough that Mono inlines it into the game's callers (UIFrame), where the patch above
            // can't reach; so also write the mouse's own position field before the game's menus read it.
            positionField = AccessTools.GetDeclaredFields(typeof(Rewired.Mouse)).FirstOrDefault(f => f.FieldType == typeof(Vector2) && !f.IsStatic);
            if (positionField == null) Log.Core.Warn("SimulatedPointer: Rewired.Mouse has no position field; the game's menus won't see the simulated mouse");
            foreach (var update in new[] { AccessTools.Method(typeof(UIFrame), "Update"), AccessTools.Method(typeof(UIFrameManager), "Update") })
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(SimulatedPointer), nameof(WritePosition)));
            Postfix(AccessTools.Method(typeof(Rewired.Controller), nameof(Rewired.Controller.GetButton), [typeof(int)]), nameof(Held));
            Postfix(AccessTools.Method(typeof(Rewired.Controller), nameof(Rewired.Controller.GetButtonDown), [typeof(int)]), nameof(Down));
            Postfix(AccessTools.Method(typeof(Rewired.Controller), nameof(Rewired.Controller.GetButtonUp), [typeof(int)]), nameof(Up));
            Postfix(AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition)), nameof(MousePosition));
            Postfix(AccessTools.PropertyGetter(typeof(Input), nameof(Input.mouseScrollDelta)), nameof(ScrollDelta));
            Postfix(AccessTools.Method(typeof(Input), nameof(Input.GetMouseButton), [typeof(int)]), nameof(UnityHeld));
            Postfix(AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonDown), [typeof(int)]), nameof(UnityDown));
            Postfix(AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonUp), [typeof(int)]), nameof(UnityUp));
        }

        private static System.Reflection.FieldInfo positionField;

        private static void WritePosition()
        {
            if (Position is not { } p || positionField == null) return;
            var mouse = Plugin.Input?.controllers?.Mouse;
            if (mouse != null) positionField.SetValue(mouse, p);
        }

        private static void ScreenPosition(ref Vector2 __result)
        {
            if (Position is { } p) __result = p;
        }

        private static void MousePosition(ref Vector3 __result)
        {
            if (Position is { } p) __result = new Vector3(p.x, p.y, 0f);
        }

        private static void ScrollDelta(ref Vector2 __result)
        {
            int f = scrollStart >= 0 ? Time.frameCount - scrollStart : -1;
            if (f >= 0 && f < scrollFrames) __result.y += scrollPerFrame;
        }

        private static void Held(Rewired.Controller __instance, int index, ref bool __result)
        {
            if (index == 0 && __instance is Rewired.Mouse && ClickFrame is >= 0 and < ClickFrames) __result = true;
        }

        private static void Down(Rewired.Controller __instance, int index, ref bool __result)
        {
            if (index == 0 && __instance is Rewired.Mouse && ClickFrame == 0) __result = true;
        }

        private static void Up(Rewired.Controller __instance, int index, ref bool __result)
        {
            if (index == 0 && __instance is Rewired.Mouse && ClickFrame == ClickFrames) __result = true;
        }

        private static void UnityHeld(int button, ref bool __result)
        {
            if (button == 0 && ClickFrame is >= 0 and < ClickFrames) __result = true;
        }

        private static void UnityDown(int button, ref bool __result)
        {
            if (button == 0 && ClickFrame == 0) __result = true;
        }

        private static void UnityUp(int button, ref bool __result)
        {
            if (button == 0 && ClickFrame == ClickFrames) __result = true;
        }
    }
}
