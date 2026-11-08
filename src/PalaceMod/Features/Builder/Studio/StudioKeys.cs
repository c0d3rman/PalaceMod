using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

/*
    The Studio's keys (Builder section, context BuilderStudio, rebindable in Settings > Controls). They work while the
    Studio's dock is the menu on top; meanwhile every other mod key is inert and game actions on these inputs are
    blocked (Keybinds.ClaimExclusive), so Backspace deletes instead of reaching Undo Upgrade.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioKeys
    {
        public static Keybind Delete, DeleteFwd, Undo, UndoCtrl, Redo, RedoCtrlY, RedoCtrl, Duplicate, DuplicateCtrl, Copy, CopyCtrl,
            Paste, PasteCtrl, SelectAll, SelectAllCtrl, Group, GroupCtrl, Ungroup, UngroupCtrl, Frame, FrameAll, Hide, ShowAll, XRay,
            Cancel, Play, PlayStart, Save, SaveCtrl, TurnLeft, TurnRight, Rotate, RotateBack, Palette,
            ToolSelect, ToolLasso, ToolPaint, ToolMove, ToolRotate, ToolErase;
        public static readonly Keybind[] Hotbar = new Keybind[9];

        private const string S = "Builder";

        private static Keybind K(string id, string name, KeyboardShortcut key, string what) =>
            Keybinds.Register(S, "studio-" + id, "Studio: " + name, key, what, KeybindFlags.AllowInMenus, StudioContexts.Edit);

        private static KeyboardShortcut Cmd(KeyCode k, bool shift = false) => shift ? new KeyboardShortcut(k, KeyCode.LeftCommand, KeyCode.LeftShift) : new KeyboardShortcut(k, KeyCode.LeftCommand);
        private static KeyboardShortcut Ctrl(KeyCode k, bool shift = false) => shift ? new KeyboardShortcut(k, KeyCode.LeftControl, KeyCode.LeftShift) : new KeyboardShortcut(k, KeyCode.LeftControl);

        public static void Register()
        {
            Delete = K("delete", "delete", new KeyboardShortcut(KeyCode.Backspace), "Deletes the selection (a map's slot is removed, or restored if it was).");
            DeleteFwd = K("delete-fwd", "delete (Del)", new KeyboardShortcut(KeyCode.Delete), "Also deletes the selection.");
            Undo = K("undo", "undo", Cmd(KeyCode.Z), "Undoes the last change.");
            UndoCtrl = K("undo-ctrl", "undo (Ctrl)", Ctrl(KeyCode.Z), "Also undoes.");
            Redo = K("redo", "redo", Cmd(KeyCode.Z, true), "Redoes the last undone change.");
            RedoCtrlY = K("redo-ctrl-y", "redo (Ctrl+Y)", Ctrl(KeyCode.Y), "Also redoes.");
            RedoCtrl = K("redo-ctrl", "redo (Ctrl+Shift)", Ctrl(KeyCode.Z, true), "Also redoes.");
            Duplicate = K("duplicate", "duplicate", Cmd(KeyCode.D), "Copies the selection in place and starts moving the copies.");
            DuplicateCtrl = K("duplicate-ctrl", "duplicate (Ctrl)", Ctrl(KeyCode.D), "Also duplicates.");
            Copy = K("copy", "copy", Cmd(KeyCode.C), "Copies the selection.");
            CopyCtrl = K("copy-ctrl", "copy (Ctrl)", Ctrl(KeyCode.C), "Also copies.");
            Paste = K("paste", "paste", Cmd(KeyCode.V), "Pastes at the cursor, keeping the copies' layout.");
            PasteCtrl = K("paste-ctrl", "paste (Ctrl)", Ctrl(KeyCode.V), "Also pastes.");
            SelectAll = K("select-all", "select all", Cmd(KeyCode.A), "Selects every object (of the outliner's filter, if one is set).");
            SelectAllCtrl = K("select-all-ctrl", "select all (Ctrl)", Ctrl(KeyCode.A), "Also selects all.");
            Group = K("group", "group", Cmd(KeyCode.G), "Groups the selection.");
            GroupCtrl = K("group-ctrl", "group (Ctrl)", Ctrl(KeyCode.G), "Also groups.");
            Ungroup = K("ungroup", "ungroup", Cmd(KeyCode.G, true), "Ungroups the selection's groups.");
            UngroupCtrl = K("ungroup-ctrl", "ungroup (Ctrl)", Ctrl(KeyCode.G, true), "Also ungroups.");
            Frame = K("frame", "frame selection", new KeyboardShortcut(KeyCode.F), "Moves the camera to the selection.");
            FrameAll = K("frame-all", "frame all", new KeyboardShortcut(KeyCode.Home), "Moves the camera to show the whole level.");
            Hide = K("hide", "hide", new KeyboardShortcut(KeyCode.H), "Hides the selection in the Studio (not in play).");
            ShowAll = K("show-all", "show all", new KeyboardShortcut(KeyCode.H, KeyCode.LeftAlt), "Shows everything hidden.");
            XRay = K("xray", "x-ray", new KeyboardShortcut(KeyCode.X), "Draws markers over scenery.");
            Cancel = K("cancel", "cancel / menu", new KeyboardShortcut(KeyCode.Escape), "Cancels what's in progress, else clears the selection, else opens the Studio menu.");
            Play = K("play", "play from cursor", new KeyboardShortcut(KeyCode.F5), "Plays the level with the king at the cursor.");
            PlayStart = K("play-start", "play from start", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftShift), "Plays the level from its start.");
            Save = K("save", "save", Cmd(KeyCode.S), "Saves the pack.");
            SaveCtrl = K("save-ctrl", "save (Ctrl)", Ctrl(KeyCode.S), "Also saves.");
            TurnLeft = K("view-left", "turn view left", new KeyboardShortcut(KeyCode.Q), "Turns the camera.");
            TurnRight = K("view-right", "turn view right", new KeyboardShortcut(KeyCode.E), "Turns the camera the other way.");
            Rotate = K("turn", "turn", new KeyboardShortcut(KeyCode.Period), "Turns the selection (or what's being placed) by the turn step.");
            RotateBack = K("turn-back", "turn back", new KeyboardShortcut(KeyCode.Comma), "Turns it the other way.");
            Palette = K("palette", "palette", new KeyboardShortcut(KeyCode.Tab), "Shows or hides the palette.");
            ToolSelect = K("tool-select", "Select tool", new KeyboardShortcut(KeyCode.V), "Click to select, drag on the ground to draw a box.");
            ToolLasso = K("tool-lasso", "Lasso tool", new KeyboardShortcut(KeyCode.L), "Drag a free shape to select what's inside.");
            ToolPaint = K("tool-paint", "Paint select tool", new KeyboardShortcut(KeyCode.B), "Drag a brush to select what it passes over.");
            ToolMove = K("tool-move", "Move tool", new KeyboardShortcut(KeyCode.G), "The selection follows the cursor until you click.");
            ToolRotate = K("tool-rotate", "Rotate tool", new KeyboardShortcut(KeyCode.R), "Drag sideways to turn the selection.");
            ToolErase = K("tool-erase", "Erase tool", new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift), "Click or drag over objects to delete them.");
            for (int i = 0; i < 9; i++)
                Hotbar[i] = K($"recent-{i + 1}", $"recent kind {i + 1}", new KeyboardShortcut(KeyCode.Alpha1 + i), $"Arms the Place tool with the {Ordinal(i + 1)} most recent kind placed.");
        }

        private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

        public static bool Down(params Keybind[] keys) => keys.Any(k => k?.Down == true);

        /// <summary>A key's label for tooltips and badges ("Cmd+Z", "Del").</summary>
        public static string Label(Keybind k)
        {
            if (k == null) return "";
            var sc = k.Shortcut;
            static string Name(KeyCode c) => c switch
            {
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftCommand or KeyCode.RightCommand => "Cmd",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                KeyCode.Delete => "Del",
                KeyCode.Backspace => "Bksp",
                KeyCode.Escape => "Esc",
                KeyCode.Comma => ",",
                KeyCode.Period => ".",
                KeyCode.LeftBracket => "[",
                KeyCode.RightBracket => "]",
                >= KeyCode.Alpha0 and <= KeyCode.Alpha9 => ((int)(c - KeyCode.Alpha0)).ToString(),
                _ => c.ToString(),
            };
            if (sc.MainKey == KeyCode.None) return "";
            return string.Join("+", sc.Modifiers.Select(Name).Concat([Name(sc.MainKey)]));
        }
    }
}
