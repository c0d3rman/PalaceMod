using System.Collections.Generic;
using System.Linq;
using Rewired;
using UnityEngine;

/*
    Mouse gate: a click on the mod's UI must never also act in the game. The game's UIFrames read the mouse at the
    controller level (Mouse.GetButtonDown), but gameplay reads Rewired actions ("Interact" swings the king's sceptre,
    raises the night flag or attacks; "Command Units", "Lock Target", "Zoom" ...), and a few of them (PlayerScept,
    the sceptre animation) ignore the player-frozen state. So while the pointer is on a ModDock panel, or a mod menu
    (ModFrame, ModPopup, a frozen dock such as the Studio's) is the active frame, every mouse binding of a game
    action is switched off. It's switched back on only once no mouse button is held (re-enabling a binding under a
    held button would read as a fresh press) and is never saved switched off (Save_Patch releases it).
*/
namespace PalaceMod
{
    internal sealed partial class KeybindsManager
    {
        private static readonly List<ActionElementMap> mouseBlocked = [];

        /// <summary>Whether game actions bound to the mouse are switched off this frame (dev, tests).</summary>
        internal static bool MouseGated => mouseBlocked.Count > 0;

        internal static bool ModUIWantsMouse()
        {
            if (ModDock.AnyPointerOver()) return true;
            var active = UIFrameManager.instance?.ActiveFrame;
            return active != null && active.gameObject.name.StartsWith("PalaceMod ", System.StringComparison.Ordinal);
        }

        private static void UpdateMouseGate()
        {
            bool want = ModUIWantsMouse();
            if (want)
            {
                if (mouseBlocked.Count == 0) BlockMouse();
                return;
            }
            if (mouseBlocked.Count == 0) return;
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)) return;
            ReleaseMouse();
        }

        private static void BlockMouse()
        {
            var mouse = Plugin.Input?.controllers?.Mouse;
            if (mouse == null) return;
            foreach (var map in Plugin.Input.controllers.maps.GetMaps(ControllerType.Mouse, mouse.id) ?? Enumerable.Empty<ControllerMap>())
            {
                foreach (var element in map.AllMaps)
                {
                    if (!element.enabled || !IsGameAction(element.actionId)) continue;
                    element.enabled = false;
                    mouseBlocked.Add(element);
                }
            }
            if (mouseBlocked.Count > 0)
                Log.For<KeybindsManager>().Debug($"Mouse gate on: {string.Join(", ", mouseBlocked.Select(b => ReInput.mapping.GetAction(b.actionId)?.name).Distinct())}");
        }

        /// <summary>A gameplay action: not one of ours, and not the menus' own (which the UIFrames need).</summary>
        private static bool IsGameAction(int actionId)
        {
            if (byAction.ContainsKey(actionId)) return false;
            var action = ReInput.mapping.GetAction(actionId);
            if (action == null) return false;
            string name = action.name ?? "";
            if (name.StartsWith("Menu", System.StringComparison.Ordinal) || name.StartsWith("UI", System.StringComparison.Ordinal)) return false;
            var category = ReInput.mapping.GetActionCategory(action.categoryId);
            string cat = category?.name ?? "";
            return cat != CategoryName && cat.IndexOf("UI", System.StringComparison.OrdinalIgnoreCase) < 0 && cat.IndexOf("Menu", System.StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void ReleaseMouse()
        {
            foreach (var element in mouseBlocked) element.enabled = true;
            mouseBlocked.Clear();
            blockedVersion = -1;   // let the exclusive-context claims look at these bindings again
            Log.For<KeybindsManager>().Debug("Mouse gate off");
        }
    }
}
