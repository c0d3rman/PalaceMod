using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using PalaceMod.Builder;
using PalaceMod.Builder.Studio;
using PalaceMod.Builder.Studio.Enemies;
using PalaceMod.Builder.Studio.Entities;
using PalaceMod.Builder.Studio.Logic;
using UnityEngine;

/*
    Installs the Entity Studio (StudioServices.Entities, the Level Studio's "Edit thing" hook) and its dev command,
    `builder-entity`, which drives it the way the mouse does: `press` clicks a real row or tile by its text, `type`
    types into the field a press started editing.
*/
namespace PalaceMod
{
    public class EntityStudioManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            StudioServices.Entities = new EntityStudioService();
            LogicStudio.OpenEntityStudio = EntityStudio.OpenFromLevel;
            DevCommands.Register("builder-entity", EntityDev.Usage, EntityDev.Run);
        }
    }
}

namespace PalaceMod.Builder.Studio.Entities
{
    internal static class EntityDev
    {
        internal const string Usage =
            "builder-entity open [id|new <kind>] | library [id|new <kind>] | new [kind] | select <id> | enemy <unit> | state | json | tab <palette> [panel] [look source] | press <text> | type <text> " +
            "| rows | look <ref|none> | launch | stop | test | moving on|off | distance <m> | undo | redo | save | back   the Entity Studio (things: projectiles, props, pickups, areas, logic objects)";

        private static void Require()
        {
            if (!EntityStudio.Active || !EnemyStudio.Active) throw new InvalidOperationException("the Entity Studio isn't open (builder-entity open|library)");
        }

        private static void RequireStudio()
        {
            if (!EnemyStudio.Active) throw new InvalidOperationException("the Enemy or Entity Studio isn't open (builder-entity open|library)");
        }

        private static (string id, string kind) Target(string[] args)
        {
            if (args.Length < 2) return (null, null);
            if (args[1] == "new") return (null, args.Length > 2 ? args[2] : null);
            return (args[1], null);
        }

        public static string Run(string[] args)
        {
            string sub = args.FirstOrDefault()?.ToLowerInvariant();
            string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;
            switch (sub)
            {
                case "open":
                {
                    if (!Studio.Running || Studio.Draft == null) throw new InvalidOperationException("no Level Studio is open (builder-entity library opens it on its own)");
                    var (id, kind) = Target(args);
                    if (!EntityStudio.Open(Studio.Draft, id, kind, back => Log.For<BuilderManager>().Info($"Entity Studio returned {back}"), out string e)) throw new InvalidOperationException(e);
                    return "opened in the Level Studio: " + EntityStudio.Describe();
                }
                case "library":
                {
                    var draft = DesignLibrary.OpenEnemies() ?? throw new InvalidOperationException("the library isn't available");
                    var (id, kind) = Target(args);
                    if (!EntityStudio.Open(draft, id, kind, back => Log.For<BuilderManager>().Info($"Entity Studio returned {back}"), out string e)) throw new InvalidOperationException(e);
                    return EnemyStudio.Active ? "opened" : "opening the arena";
                }
                case "new":
                    Require();
                    if (rest == null) { EntityStudio.Select(null); return "kind picker"; }
                    EntityStudio.New(rest);
                    return "new " + EntityStudio.Id;
                case "select": Require(); EntityStudio.Select(rest); return EntityStudio.Describe();
                case "enemy":
                    if (!EnemyStudio.Active) throw new InvalidOperationException("the Enemy Studio isn't open");
                    if (rest != null) EnemyStudio.Select(rest);
                    else if (!EntityStudio.Back()) throw new InvalidOperationException("no enemy to go back to");
                    return EnemyStudio.Describe();
                case "state": return EnemyStudio.Describe();
                case "json":
                    Require();
                    return EntityStudio.Entity == null ? "(no thing)" : JsonConvert.SerializeObject(EntityStudio.Entity, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                case "tab":
                    EnemyDock.PaletteTab = args[1];
                    if (args.Length > 2) EnemyDock.PanelTab = args[2];
                    if (args.Length > 3) EnemyDock.LookSource = args[3];
                    EnemyDock.RefreshAll();
                    return $"palette {EnemyDock.PaletteTab}, panel {EnemyDock.PanelTab}";
                case "press": RequireStudio(); return Press(rest ?? throw new ArgumentException("press <text>"));
                case "type":
                    if (!ModTextInput.Active) throw new InvalidOperationException("nothing is being typed into (press a field first)");
                    ModTextInput.DebugKey("selectall");
                    ModTextInput.DebugKey(rest ?? "");
                    ModTextInput.DebugKey("enter");
                    return "typed " + rest;
                case "rows": RequireStudio(); return Rows();
                case "look":
                    Require();
                    EntityStudio.EditVisual("Set model", v => v.Mesh = rest == "none" ? null : rest, structural: true);
                    return "model " + (EntityStudio.Entity?.Visual?.Mesh ?? "none");
                case "launch":
                case "try":
                    Require();
                    return EntityTest.Start();
                case "stop": EntityTest.Clear(); return "stopped";
                case "test": return EntityTest.Describe();
                case "moving": EntityTest.Moving = rest != "off"; EnemyDock.RefreshAll(); return "target moves " + EntityTest.Moving;
                case "distance": EntityTest.Distance = float.Parse(rest, System.Globalization.CultureInfo.InvariantCulture); EnemyDock.RefreshAll(); return "distance " + EntityTest.Distance;
                case "undo": EnemyStudio.Undo(); return "undo";
                case "redo": EnemyStudio.Redo(); return "redo";
                case "save": Require(); EnemyStudio.Save(); return EnemyStudio.Draft?.Dirty == false ? "saved" : "not saved";
                case "back": EnemyStudio.Back(); return EnemyStudio.Running ? EnemyStudio.Describe() : "closed; level studio: " + StudioShell.Describe();
                default: throw new ArgumentException(Usage);
            }
        }

        // ---- Mouse-like presses -------------------------------------------------------------------------------------

        private static string Plain(string s) => Regex.Replace(s ?? "", "<[^>]+>", "").Replace("​", "").Trim();

        /// <summary>An open popup first, then the rule editor's panel, then the dock.</summary>
        private static IEnumerable<Transform> Roots()
        {
            var popup = ModPopup.Current != null && ModPopup.Current.IsOpen
                ? UnityEngine.Object.FindObjectsOfType<RectTransform>().FirstOrDefault(r => r.name.StartsWith("ModPopup") && r.gameObject.activeInHierarchy) : null;
            return new Transform[] { popup, RuleEditor.PanelRect, EnemyDock.Dock?.Root }.Where(r => r != null);
        }

        private static IEnumerable<(string text, Action press, string what)> Targets()
        {
            foreach (var root in Roots())
            {
                foreach (var b in root.GetComponentsInChildren<TFUITextButton>())
                {
                    if (!b.gameObject.activeInHierarchy) continue;
                    var t = b.GetComponent<TMPro.TextMeshProUGUI>();
                    string text = Plain(t != null ? t.text : b.name);
                    var button = b;
                    yield return (text, () => button.onApply?.Invoke(), "row");
                }
                foreach (var tile in root.GetComponentsInChildren<ModTile>())
                {
                    if (!tile.gameObject.activeInHierarchy) continue;
                    string label = tile.Label != null ? Plain(tile.Label.text) : "";
                    var t = tile;
                    yield return (label.Length > 0 ? label : tile.name, () => t.Press(), "tile");
                    if (tile.Key != null) yield return (tile.Key, () => t.Press(), "tile");
                    yield return (tile.name, () => t.Press(), "tile");
                }
            }
        }

        /// <summary>Clicks the first row or tile whose text is (else starts with, else contains) the given text.</summary>
        public static string Press(string text)
        {
            var all = Targets().ToList();
            foreach (var match in new Func<string, bool>[]
            {
                s => s.Equals(text, StringComparison.OrdinalIgnoreCase),
                s => s.StartsWith(text, StringComparison.OrdinalIgnoreCase),
                s => s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0,
            })
            {
                var hit = all.FirstOrDefault(t => match(t.text));
                if (hit.press == null) continue;
                hit.press();
                return $"pressed {hit.what} {hit.text.Replace("\n", " / ")}";
            }
            throw new ArgumentException($"nothing showing \"{text}\"");
        }

        private static string Rows() => string.Join("\n", Targets().Select(t => $"{t.what}: {t.text.Replace("\n", " / ")}").Distinct().Take(150));
    }
}
