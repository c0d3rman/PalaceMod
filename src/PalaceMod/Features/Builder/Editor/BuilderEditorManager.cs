using System;
using System.Linq;
using BepInEx.Configuration;
using PalaceMod.Builder;
using PalaceMod.Builder.Editor;
using UnityEngine;

/*
    Runs the Builder editor (Features/Builder/Editor): its menu entries, undo/redo keys, first-use hint and the
    `builder-ui` dev command. Part of the Builder feature: it follows Builder.Enabled (BuilderManager's switch).
*/
namespace PalaceMod
{
    public class BuilderEditorManager : BaseManager
    {
        private Keybind undo, redo, redoAlt, undoCtrl, redoCtrl;
        private static ConfigEntry<bool> hintShown;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            undo = Keybinds.Register("Builder", "editor-undo", "Builder: undo", new KeyboardShortcut(KeyCode.Z, KeyCode.LeftCommand),
                "Undoes the last change in the Builder editor.", KeybindFlags.AllowInMenus);
            redo = Keybinds.Register("Builder", "editor-redo", "Builder: redo", new KeyboardShortcut(KeyCode.Z, KeyCode.LeftCommand, KeyCode.LeftShift),
                "Redoes the last undone change in the Builder editor.", KeybindFlags.AllowInMenus);
            redoAlt = Keybinds.Register("Builder", "editor-redo-ctrl", "Builder: redo (Ctrl)", new KeyboardShortcut(KeyCode.Y, KeyCode.LeftControl),
                "Also redoes in the Builder editor.", KeybindFlags.AllowInMenus);
            undoCtrl = Keybinds.Register("Builder", "editor-undo-ctrl", "Builder: undo (Ctrl)", new KeyboardShortcut(KeyCode.Z, KeyCode.LeftControl),
                "Also undoes in the Builder editor.", KeybindFlags.AllowInMenus);
            redoCtrl = Keybinds.Register("Builder", "editor-redo-ctrl-shift", "Builder: redo (Ctrl+Shift)", new KeyboardShortcut(KeyCode.Z, KeyCode.LeftControl, KeyCode.LeftShift),
                "Also redoes in the Builder editor.", KeybindFlags.AllowInMenus);
            hintShown = plugin.Config.Bind("Builder", "Editor hint shown", false,
                new ConfigDescription("Whether the Builder editor's first-use hint has been shown.", null, new SettingTags { Hidden = true }));
            PauseMenu.AddButton("builder", "Builder", BuilderEditor.Open, order: 40, visible: () => Feature<BuilderManager>.On);
            TitleMenu.AddButton("builder", "Builder", BuilderEditor.Open, () => Feature<BuilderManager>.On);
            HookSessionMenu();
            RegisterDevCommand();
        }

        public override void Update()
        {
            if (!BuilderEditor.EditorFocused) return;
            if (!Feature<BuilderManager>.On) { BuilderEditor.Shutdown(); return; }
            if (ModTextInput.Active) return;
            if (redo.Down || redoAlt.Down || redoCtrl.Down) BuilderEditor.Redo();
            else if (undo.Down || undoCtrl.Down) BuilderEditor.Undo();
        }

        internal static void FirstUseHint()
        {
            if (hintShown == null || hintShown.Value) return;
            hintShown.Value = true;
            Notify.Show("Builder: left/right changes a value, apply edits it, Cmd+Z undoes", NotifyKind.Info, 6f, key: "builder-hint");
        }

        // WP3's custom-runs menu ("Create your own", per-pack "Edit") links here through its hooks.
        private static void HookSessionMenu()
        {
            SessionMenu.EditorAvailable = () => BuilderEditor.IsAvailable;
            SessionMenu.OpenEditor = BuilderEditor.Open;
            SessionMenu.OpenPackInEditor = BuilderEditor.OpenPack;
        }

        private void RegisterDevCommand()
        {
            DevCommands.Register("builder-ui",
                "builder-ui open | pack <id> | unit <i> | wave <i> [night [group]] | goto <path> | undo | redo | save | issues | test <waveSet> | type <text>   drive the Builder editor", args =>
            {
                string sub = args.FirstOrDefault()?.ToLowerInvariant();
                switch (sub)
                {
                    case "open":
                        BuilderEditor.Open();
                        return BuilderEditor.EditorFocused ? "opened" : throw new InvalidOperationException("didn't open; see the log");
                    case "pack":
                        BuilderEditor.OpenPack(Arg(args, 1));
                        return BuilderEditor.Draft != null ? $"editing {BuilderEditor.Draft.Pack.Id}" : throw new InvalidOperationException("didn't open");
                    case "unit":
                        RequireDraft();
                        BuilderEditor.OpenUnit(int.Parse(Arg(args, 1)));
                        return "unit page";
                    case "wave":
                        RequireDraft();
                        BuilderEditor.GoTo($"waveSets[{Arg(args, 1)}]" + (args.Length > 2 ? $".nights[{args[2]}]" : "") + (args.Length > 3 ? $".spawns[{args[3]}]" : ""));
                        return "wave page";
                    case "goto":
                        RequireDraft();
                        BuilderEditor.GoTo(Arg(args, 1));
                        return "ok";
                    case "undo":
                        BuilderEditor.Undo();
                        return BuilderEditor.Draft?.History.UndoLabel ?? "-";
                    case "redo":
                        BuilderEditor.Redo();
                        return BuilderEditor.Draft?.History.RedoLabel ?? "-";
                    case "save":
                        RequireDraft();
                        BuilderEditor.Save();
                        return BuilderEditor.Draft.Dirty ? "still unsaved" : "saved";
                    case "issues":
                        RequireDraft();
                        return BuilderEditor.Draft.Issues.Count == 0 ? "no issues" : "\n" + string.Join("\n", BuilderEditor.Draft.Issues);
                    case "test":
                        RequireDraft();
                        BuilderEditor.TestWaveSet(Arg(args, 1));
                        return BuilderServices.InSession ? "launched" : "not launched (see notifications/log)";
                    case "testunit":
                        RequireDraft();
                        BuilderEditor.TestUnit(int.Parse(Arg(args, 1)));
                        return BuilderServices.InSession ? "launched" : "not launched";
                    case "type":
                        // Simulates typing into the field being edited: "type 120" then Enter.
                        if (!ModTextInput.Active) throw new InvalidOperationException("no field is being edited (apply a text or number field first)");
                        ModTextInput.Debug(string.Join(" ", args.Skip(1)));
                        return "typed";
                    case "json":
                        RequireDraft();
                        return "\n" + Builder.Model.PackSerializer.Write(BuilderEditor.Draft.Pack);
                    default:
                        throw new ArgumentException("usage: builder-ui open|pack <id>|unit <i>|wave <i> [night [group]]|goto <path>|undo|redo|save|issues|test <waveSet>|testunit <i>|type <text>|json");
                }
            });

            static string Arg(string[] args, int i) => args.Length > i ? args[i] : throw new ArgumentException("missing argument");
            static void RequireDraft()
            {
                if (BuilderEditor.Draft == null) throw new InvalidOperationException("no pack open (builder-ui pack <id>)");
            }
        }
    }
}
