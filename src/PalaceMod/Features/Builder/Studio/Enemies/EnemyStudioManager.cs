using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using PalaceMod.Builder;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio;
using PalaceMod.Builder.Studio.Enemies;
using UnityEngine;

/*
    Runs the Enemy Studio (Features/Builder/Studio/Enemies): installs it as StudioServices.Enemies, its keys (context
    BuilderEnemyStudio, exclusive while it's up), the per-frame loop, the arena's day-time attack patch and the
    `builder-enemy` dev command. Part of the Builder feature.
*/
namespace PalaceMod
{
    public class EnemyStudioManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            EnemyKeys.Register();
            StudioServices.Enemies = new EnemyStudioService();
            StudioShell.Suspended = () => EnemyStudio.Running;
            Keybinds.ClaimExclusive(EnemyKeys.Context, () => EnemyStudio.Active && EnemyDock.Active);
            // Right-click on empty ground in the Level Studio: open the Enemy Studio on this level's enemies.
            Studio.AddContextItems(keys => keys.Count > 0 ? [] : [("Enemy Studio…", () => OpenFromLevel(null))]);
            DevCommands.Register("builder-enemy", EnemyDev.Usage, EnemyDev.Run);
        }

        internal static void OpenFromLevel(string unitId)
        {
            var draft = Studio.Draft;
            if (draft == null) return;
            string id = unitId ?? draft.Pack.Units.FirstOrDefault(u => u?.Side == UnitSide.Enemy)?.Id;
            if (!EnemyStudio.Open(draft, id, null, out string error)) Notify.Show("Enemy Studio: " + error, NotifyKind.Warning, 4f, "builder-enemy");
        }

        public override void Update()
        {
            try { EnemyStudio.Update(); }
            catch (Exception e)
            {
                Log.Error("Enemy Studio failed; closing it", e);
                try { EnemyStudio.Close(returnToLevel: false); } catch (Exception e2) { Log.Caught("Enemy Studio close failed", e2); }
            }
        }

        /// <summary>While the Enemy Studio's arena exists, every position-based target search (TargetPriority's and
        /// TagManager's Find* methods) remembers where it searches from, so the filter below can keep the arena's
        /// fight and the level apart (ArenaZone).</summary>
        [HarmonyPatch]
        private static class ArenaSearch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var t in new[] { typeof(TargetPriority), typeof(TagManager) })
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        var ps = m.GetParameters();
                        if (m.Name.StartsWith("Find") && ps.Length > 0 && ps[0].ParameterType == typeof(Vector3)) yield return m;
                    }
            }

            private static void Prefix(Vector3 __0, out Vector3? __state)
            {
                __state = ArenaZone.SearchFrom;
                if (ArenaZone.Active) ArenaZone.SearchFrom = __0;
            }

            private static Exception Finalizer(Exception __exception, Vector3? __state)
            {
                ArenaZone.SearchFrom = ArenaZone.Active ? __state : null;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.FindAllTaggedObjectsWithTags))]
        private static class ArenaSearchFilter
        {
            private static void Postfix(List<TaggedObject> _listToPolulate)
            {
                if (ArenaZone.SearchFrom is Vector3 from) ArenaZone.Filter(_listToPolulate, from);
            }
        }

        /// <summary>Arena units' attacks run in the day (AutoAttack.Update and every override).</summary>
        [HarmonyPatch]
        private static class ArenaAttackPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var t in typeof(AutoAttack).Assembly.GetTypes())
                {
                    if (!typeof(AutoAttack).IsAssignableFrom(t)) continue;
                    var m = t.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null && !m.IsAbstract) yield return m;
                }
            }

            private static void Prefix(Component __instance, out bool __state) => __state = EnemyArena.Running && EnemyArena.PretendNight(__instance);

            private static Exception Finalizer(Exception __exception, bool __state)
            {
                if (__state) EnemyArena.BackToDay();
                return __exception;
            }
        }
    }
}

namespace PalaceMod.Builder.Studio.Enemies
{
    internal static class EnemyKeys
    {
        public const string Context = "BuilderEnemyStudio";
        public static Keybind Back, Fight, Turn, Walk, Frame, ResetView, Save, SaveCtrl, Undo, UndoCtrl, Redo, RedoCtrl, RedoCtrlY;

        private static Keybind K(string id, string name, KeyboardShortcut key, string what) =>
            Keybinds.Register("Builder", "enemy-" + id, "Enemy Studio: " + name, key, what, KeybindFlags.AllowInMenus, Context);

        private static KeyboardShortcut Cmd(KeyCode k, bool shift = false) => shift ? new KeyboardShortcut(k, KeyCode.LeftCommand, KeyCode.LeftShift) : new KeyboardShortcut(k, KeyCode.LeftCommand);
        private static KeyboardShortcut Ctrl(KeyCode k, bool shift = false) => shift ? new KeyboardShortcut(k, KeyCode.LeftControl, KeyCode.LeftShift) : new KeyboardShortcut(k, KeyCode.LeftControl);

        public static void Register()
        {
            Back = K("back", "back", new KeyboardShortcut(KeyCode.Escape), "Back to the level (or out of the arena).");
            Fight = K("fight", "fight", new KeyboardShortcut(KeyCode.F5), "Starts or stops the arena fight.");
            Turn = K("turn", "turntable", new KeyboardShortcut(KeyCode.Space), "Spins the model, or holds it.");
            Walk = K("walk", "walk preview", new KeyboardShortcut(KeyCode.W), "Shows the walk a model without its own animation gets.");
            Frame = K("frame", "frame", new KeyboardShortcut(KeyCode.F), "Frames the model.");
            ResetView = K("reset-view", "reset view", new KeyboardShortcut(KeyCode.Home), "Back to the starting view of the model.");
            Save = K("save", "save", Cmd(KeyCode.S), "Saves (and takes the library portrait).");
            SaveCtrl = K("save-ctrl", "save (Ctrl)", Ctrl(KeyCode.S), "Also saves.");
            Undo = K("undo", "undo", Cmd(KeyCode.Z), "Undoes the last change.");
            UndoCtrl = K("undo-ctrl", "undo (Ctrl)", Ctrl(KeyCode.Z), "Also undoes.");
            Redo = K("redo", "redo", Cmd(KeyCode.Z, true), "Redoes.");
            RedoCtrl = K("redo-ctrl", "redo (Ctrl+Shift)", Ctrl(KeyCode.Z, true), "Also redoes.");
            RedoCtrlY = K("redo-ctrl-y", "redo (Ctrl+Y)", Ctrl(KeyCode.Y), "Also redoes.");
        }

        public static bool Down(params Keybind[] keys) => keys.Any(k => k?.Down == true);
    }

    /// <summary>The stage's mouse and keys: left-drag orbits, wheel zooms, right/middle-drag pans.</summary>
    internal static class EnemyControls
    {
        private static Vector2 last;
        private static int dragButton = -1;

        public static void Update()
        {
            bool overUI = EnemyDock.PointerOverPanel || (ModPopup.Current != null && ModPopup.Current.IsOpen) || Picker.IsOpen;
            var mouse = StudioInput.Mouse;
            if (dragButton < 0 && !overUI)
            {
                for (int b = 0; b < 3; b++) if (StudioInput.Down(b)) { dragButton = b; last = mouse; break; }
                float lines = ScrollInput.Lines;
                if (Mathf.Abs(lines) > 0.01f) EnemyStage.Distance *= Mathf.Pow(0.88f, lines);
            }
            if (dragButton >= 0)
            {
                if (!StudioInput.Held(dragButton)) dragButton = -1;
                else
                {
                    var delta = mouse - last;
                    last = mouse;
                    if (dragButton == 0)
                    {
                        EnemyStage.Yaw += delta.x * 0.3f;
                        EnemyStage.Pitch -= delta.y * 0.2f;
                    }
                    else
                    {
                        // Pan the focus in the view plane, scaled to the distance.
                        var cam = Camera.main;
                        if (cam != null)
                        {
                            float k = EnemyStage.Distance * 0.0016f;
                            EnemyStage.Focus -= (cam.transform.right * delta.x + cam.transform.up * delta.y) * k;
                        }
                    }
                }
            }
            if (ModTextInput.Active || !EnemyDock.Active) return;
            if (EnemyKeys.Down(EnemyKeys.Back) && !(ModPopup.Current != null && ModPopup.Current.IsOpen)) { EnemyStudio.Back(); return; }
            if (EnemyKeys.Down(EnemyKeys.Redo, EnemyKeys.RedoCtrl, EnemyKeys.RedoCtrlY)) { EnemyStudio.Redo(); return; }
            if (EnemyKeys.Down(EnemyKeys.Undo, EnemyKeys.UndoCtrl)) { EnemyStudio.Undo(); return; }
            if (EnemyKeys.Down(EnemyKeys.Save, EnemyKeys.SaveCtrl)) { EnemyStudio.Save(); return; }
            if (StudioInput.Ctrl) return;
            if (EnemyKeys.Down(EnemyKeys.Fight)) { EnemyDock.Fight(); return; }
            if (EnemyKeys.Down(EnemyKeys.Turn)) { EnemyStage.Turning = !EnemyStage.Turning; EnemyDock.RefreshAll(); return; }
            if (EnemyKeys.Down(EnemyKeys.Walk)) { EnemyStudio.SetPreviewWalk(!EnemyStudio.PreviewWalk); EnemyDock.RefreshAll(); return; }
            if (EnemyKeys.Down(EnemyKeys.Frame)) { EnemyStage.FrameModel(); return; }
            if (EnemyKeys.Down(EnemyKeys.ResetView)) EnemyStage.ResetView();
        }
    }

    internal static class EnemyDev
    {
        internal const string Usage =
            "builder-enemy open [unit] | library [unit] | level [unit] | new [base] | state | dock | press <tile|row> | set <field> <value> | color <part> <#hex|-> | parts | picker | look <ref|base> | fit <scale|-> " +
            "| offset <x> <y> <z> | rot <x> <y> <z> | material own|unit | motion auto|bob|none | walk on|off | fight [n] | stop | readout | portrait | orbit <yaw> <pitch> <dist> | spin <deg> " +
            "| tab <palette tab> <panel tab> | select <unit> | undo | redo | save | back   the Enemy Studio";

        private static string Arg(string[] a, int i) => i < a.Length ? a[i] : throw new ArgumentException("missing argument " + i);
        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static void Require()
        {
            if (!EnemyStudio.Active) throw new InvalidOperationException("the Enemy Studio isn't open");
        }

        public static string Run(string[] args)
        {
            string sub = args.FirstOrDefault()?.ToLowerInvariant();
            string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;
            switch (sub)
            {
                case "open":
                case "level":
                {
                    if (!Studio.Running || Studio.Draft == null) throw new InvalidOperationException("no Level Studio is open (builder-enemy library opens it on its own)");
                    if (!EnemyStudio.Open(Studio.Draft, rest, id => Log.For<BuilderManager>().Info($"Enemy Studio returned {id}"), out string e)) throw new InvalidOperationException(e);
                    return "opened in the Level Studio";
                }
                case "library":
                {
                    var draft = DesignLibrary.OpenEnemies() ?? throw new InvalidOperationException("the library isn't available");
                    if (rest == null) rest = draft.Pack.Units.FirstOrDefault(u => u?.Side == UnitSide.Enemy)?.Id;
                    if (!EnemyStudio.Open(draft, rest, id => Log.For<BuilderManager>().Info($"Enemy Studio returned {id}"), out string e)) throw new InvalidOperationException(e);
                    return EnemyStudio.Active ? "opened" : "opening the arena";
                }
                case "new": Require(); EnemyStudio.New(rest); return "new " + EnemyStudio.UnitId;
                case "state": return EnemyStudio.Describe();
                case "dock": return EnemyDock.Describe();
                case "press": Require(); return EnemyDock.Press(rest);
                case "select": Require(); EnemyStudio.Select(rest); return "editing " + EnemyStudio.UnitId;
                case "tab":
                    EnemyDock.PaletteTab = Arg(args, 1);
                    if (args.Length > 2) EnemyDock.PanelTab = args[2];
                    if (args.Length > 3) EnemyDock.LookSource = args[3];
                    EnemyDock.RefreshAll();
                    return $"palette {EnemyDock.PaletteTab}, panel {EnemyDock.PanelTab}";
                case "set":
                {
                    Require();
                    string field = Arg(args, 1);
                    string value = args.Length > 2 ? string.Join(" ", args.Skip(2)) : null;
                    EnemyStudio.Edit("Set " + field, u =>
                    {
                        u.Stats ??= new StatsDef();
                        switch (field)
                        {
                            case "name": u.Name = value; break;
                            case "base": u.Base = ContentRef.Vanilla(value); break;
                            case "maxHp": u.Stats.MaxHp = value == null ? null : F(value); break;
                            case "moveSpeed": u.Stats.MoveSpeed = value == null ? null : F(value); break;
                            case "attackCooldown": u.Stats.AttackCooldown = value == null ? null : F(value); break;
                            case "attackRange": u.Stats.AttackRange = value == null ? null : F(value); break;
                            case "scale": u.Visuals ??= new VisualsDef(); u.Visuals.Scale = value == null ? null : F(value); break;
                            case "tint": u.Visuals ??= new VisualsDef(); u.Visuals.Tint = value; break;
                            case "damageMult": u.Weapon ??= new WeaponDef(); u.Weapon.DamageMult = value == null ? null : F(value); break;
                            case "tag": u.Tags ??= new TagEditDef(); if (value != null && !u.Tags.Add.Contains(value)) u.Tags.Add.Add(value); break;
                            default: throw new ArgumentException("fields: name base maxHp moveSpeed attackCooldown attackRange scale tint damageMult tag");
                        }
                        if (u.Stats.MaxHp == null && u.Stats.MoveSpeed == null && u.Stats.AttackCooldown == null && u.Stats.AttackRange == null && u.Stats.KeepDistance == null && u.Stats.AggroTime == null) u.Stats = null;
                    }, structural: true);
                    return EnemyStudio.Describe();
                }
                case "color":
                {
                    Require();
                    string part = Arg(args, 1), hex = args.Length > 2 ? args[2] : "-";
                    EnemyStudio.EditVisuals("Colour " + part, v =>
                    {
                        v.Colors ??= new Dictionary<string, string>();
                        if (hex == "-") v.Colors.Remove(part); else v.Colors[part] = hex;
                        if (v.Colors.Count == 0) v.Colors = null;
                    }, structural: true);
                    return "colors: " + string.Join(", ", (EnemyStudio.Unit?.Visuals?.Colors ?? []).Select(kv => kv.Key + "=" + kv.Value));
                }
                case "parts":
                {
                    Require();
                    var model = EnemyStage.Model;
                    if (model == null) return "no model";
                    return string.Join("; ", model.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
                        .Select(r => r.name + ": " + string.Join(",", r.sharedMaterials.Where(m => m != null).Select(m =>
                            UnitFactory.PartName(m) + (m.HasProperty("_BaseColor") ? " #" + ColorUtility.ToHtmlStringRGB(m.GetColor("_BaseColor")) : "") +
                            (m.HasProperty("_ColorDim") ? "/#" + ColorUtility.ToHtmlStringRGB(m.GetColor("_ColorDim")) : "")))));
                }
                case "picker":
                    Require();
                    ModColorPicker.Open("Test colour", Color.red, c => Log.For<BuilderManager>().Info($"picked {c}"));
                    return "picker open";
                case "look":
                    Require();
                    EnemyStudio.EditVisuals("Set model", v => v.Mesh = rest == "base" ? null : rest, structural: true);
                    return "model " + (EnemyStudio.Unit?.Visuals?.Mesh ?? "base");
                case "fit":
                    Require();
                    EnemyStudio.EditVisuals("Set model scale", v => v.MeshScale = rest == "-" ? null : F(rest));
                    return "ok";
                case "offset":
                    Require();
                    EnemyStudio.EditVisuals("Move the model", v => v.MeshOffset = [F(Arg(args, 1)), F(Arg(args, 2)), F(Arg(args, 3))]);
                    return "ok";
                case "rot":
                    Require();
                    EnemyStudio.EditVisuals("Turn the model", v => { v.MeshRotX = F(Arg(args, 1)); v.MeshRotY = F(Arg(args, 2)); v.MeshRotZ = F(Arg(args, 3)); });
                    return "ok";
                case "material":
                    Require();
                    EnemyStudio.EditVisuals("Set model materials", v => v.MeshLook = rest);
                    return "ok";
                case "motion":
                    Require();
                    EnemyStudio.EditVisuals("Set the walk", v => v.Motion = rest);
                    return "ok";
                case "walk": Require(); EnemyStudio.SetPreviewWalk(rest == "on"); return "walk " + EnemyStudio.PreviewWalk;
                case "fight":
                    Require();
                    if (rest != null) EnemyArena.Count = int.Parse(rest, CultureInfo.InvariantCulture);
                    return EnemyArena.Start(EnemyStudio.Draft.Pack, EnemyStudio.UnitId);
                case "stop": EnemyArena.Clear(); return "stopped";
                case "readout": return EnemyArena.Describe();
                case "portrait":
                {
                    Require();
                    return "portrait: " + (EnemyStudio.SavePortrait() ?? "failed");
                }
                case "orbit":
                    Require();
                    EnemyStage.Yaw = F(Arg(args, 1));
                    EnemyStage.Pitch = F(Arg(args, 2));
                    EnemyStage.Distance = F(Arg(args, 3));
                    return "ok";
                case "spin":
                    Require();
                    EnemyStage.Turning = false;
                    EnemyStage.Spin = F(Arg(args, 1));
                    return "ok";
                case "undo": EnemyStudio.Undo(); return "undo";
                case "redo": EnemyStudio.Redo(); return "redo";
                case "save": Require(); EnemyStudio.Save(); return EnemyStudio.Draft?.Dirty == false ? "saved" : "not saved";
                case "back": EnemyStudio.Back(); return "back; level studio: " + StudioShell.Describe();
                default: throw new ArgumentException(Usage);
            }
        }
    }
}
