using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

/*
    The rule editor's pickers, drawn into its panel: a search box, category tabs (Common first) and one row per entry
    with its one-line description. Entity pickers offer the roles this event has, the level's named things, "pick on
    the map", spatial finds (nearest, all within…) built from a filter, and a typed formula.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class Pickers
    {
        private static string Esc(string s) => LogicFields.Esc(s);

        /// <summary>A searchable, categorised list. Searching looks at every category.</summary>
        public static void Catalog<T>(ModPage page, IReadOnlyList<T> items, Func<T, string> category, Func<T, bool> common, Func<T, string> label, Func<T, string> desc, Func<T, string> id,
            IReadOnlyList<string> categories, Action<T> pick, bool close)
        {
            page.TextInput("Search", () => RuleEditor.Search, v => { RuleEditor.Search = v ?? ""; RuleEditor.MarkDirty(); }, () => "Type part of a name or description.");
            string q = RuleEditor.Search?.Trim() ?? "";
            var cats = new List<string> { "Common" };
            cats.AddRange(categories.Where(c => items.Any(i => category(i) == c)));
            string current = RuleEditor.Category ?? "Common";
            if (q.Length == 0)
                StudioDock.Tabs(page, cats.Select(c => (c, c)).ToList(), () => current, c => { RuleEditor.Category = c; RuleEditor.MarkDirty(); }, 4, 19f);
            IEnumerable<T> shown = q.Length > 0
                ? items.Where(i => Contains(label(i), q) || Contains(desc(i), q) || Contains(id(i), q))
                : current == "Common" ? items.Where(common) : items.Where(i => category(i) == current);
            int n = 0;
            foreach (var item in shown)
            {
                var it = item;
                n++;
                StudioDock.DetailRow(page, Esc(LogicCatalog.Humanize(label(item))), Esc(desc(item)), false, null, () =>
                {
                    pick(it);
                    if (close) RuleEditor.ClosePicker();
                }, () => $"{id(it)}\n{desc(it)}");
            }
            if (n == 0) page.Text(LogicFields.Dim("Nothing matches."));
        }

        private static bool Contains(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Pick(ModPage page, string label, string detail, Action run, string tip = null) =>
            StudioDock.DetailRow(page, Esc(label), Esc(detail), false, null, () => { run(); RuleEditor.ClosePicker(); }, tip == null ? null : () => tip);

        // ---- Entities and positions ---------------------------------------------------------------------------------

        public static void Value(ModPage page, ParamUi p, ExprScope scope, Action<JToken> set)
        {
            var ev = scope?.Event;
            StudioDock.SmallHeader(page, "Who");
            Pick(page, "This", "the rule's owner (self)", () => set(p.Name == "target" ? null : new JValue("self")));
            if (ev?.Other != null) Pick(page, "The " + ev.Other.Replace("the ", ""), "other: who the event names", () => set(new JValue("other")));
            if (ev != null && ev.CanOf) Pick(page, "The one it happened to", "subject: with \"something else\", the one that died, was hit…", () => set(new JValue("subject")));
            if (scope?.HasSpawned == true) Pick(page, "The spawned", "spawned: what spawn or launch just made", () => set(new JValue("spawned")));
            Pick(page, "The king", "king", () => set(new JValue("king")));
            Pick(page, "The castle", "castle", () => set(new JValue("castle")));
            if (p.Name == "to") Pick(page, "Everyone", "every listener and the level script", () => set(new JValue("all")));
            if (p.Kind == ParamKind.Target && p.Name == "target") Pick(page, "The level", "for level variables", () => set(new JValue("level")));
            if (p.Kind == ParamKind.Position)
            {
                if (ev?.HasPos == true) Pick(page, "Where it happened", "event.pos", () => set(new JValue("event.pos")));
                Pick(page, "A random spot near this", "pointNear(self, 5)", () => set(new JValue("pointNear(self, 5)")));
            }

            var names = scope?.Names?.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            StudioDock.SmallHeader(page, "Something on the map");
            Pick(page, "Pick on the map…", "click a slot, prop, line or placed object", () => PickOnMap(set));
            foreach (var n in names) Pick(page, RuleText.PlaceName(n), "@" + n, () => set(new JValue("@" + n)));

            StudioDock.SmallHeader(page, "Find");
            foreach (var (fn, label, detail) in new[]
            {
                ("nearest", "Nearest…", "the closest match"), ("all", "Every… within", "a set: acts on each"),
                ("random", "A random…", "one match at random"), ("weakest", "Weakest…", "lowest health"),
                ("strongest", "Strongest…", "highest health"), ("farthest", "Farthest…", "within range"),
            })
            {
                if (fn == "all" && p.Kind == ParamKind.Entity) continue;
                string f = fn;
                StudioDock.DetailRow(page, label, detail, false, null, () => RuleEditor.OpenPicker(label, pg => Selector(pg, f, set)));
            }

            StudioDock.SmallHeader(page, "Formula");
            JToken typed = null;
            LogicFields.Param(page, "Formula", new ParamUi { Name = p.Name, Label = p.Label, Kind = ParamKind.Expr, Desc = "Any formula: nearest('foe and Tower', 20), @gate1, other.owner…" },
                () => typed, (v, l) => { if (v != null) { set(v); RuleEditor.ClosePicker(); } }, scope);
        }

        private static float selectorRange = 12f;

        private static void Selector(ModPage page, string fn, Action<JToken> set)
        {
            page.Number("Within (m)", () => selectorRange, v => selectorRange = v, 0f, 200f, 1f, tooltip: () => "0: anywhere on the map.");
            RuleEditor.FilterPicker(page, null, f =>
            {
                string expr = selectorRange > 0 ? $"{fn}('{f}', {RuleText.Num(selectorRange)})" : $"{fn}('{f}')";
                set(new JValue(expr));
            }, true);
        }

        /// <summary>Asks for a click on the map; names the clicked thing if it has no name, then sets "@name".</summary>
        public static void PickOnMap(Action<JToken> set)
        {
            Studio.PickOnMap("Click the thing to use", key => LogicOwner.ForItem(key) != null, key =>
            {
                var target = LogicOwner.ForItem(key);
                if (target == null) return;
                string name = null;
                Studio.Edit("Name " + target.Title, lv => LogicDoc.EditLogic(lv, logic => name = LogicOwner.EnsureName(logic, target)));
                if (name != null) set(new JValue("@" + name));
                RuleEditor.MarkDirty();
            });
        }

        // ---- Templates ----------------------------------------------------------------------------------------------

        public static void Template(ModPage page, Action<JToken> set)
        {
            var pack = Studio.Draft?.Pack ?? Enemies.EnemyStudio.Draft?.Pack;
            Pick(page, "The same as this", "=self.template", () => set(new JValue("=self.template")));
            if (pack != null)
            {
                StudioDock.SmallHeader(page, "This pack's units");
                foreach (var u in pack.Units.Where(u => u?.Id != null))
                {
                    var unit = u;
                    Pick(page, string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name, $"{u.Id} · {u.Side}", () => set(new JValue(unit.Id)));
                }
                var entities = LogicDoc.Get(pack, "entities") as JArray;
                if (entities?.Count > 0)
                {
                    StudioDock.SmallHeader(page, "Things (projectiles, props, pickups)");
                    foreach (var e in entities.OfType<JObject>())
                    {
                        string id = (string)e["id"];
                        if (id == null) continue;
                        Pick(page, (string)e["name"] ?? id, $"{id} · {(string)e["kind"]}", () => set(new JValue(id)));
                    }
                }
            }
            var catalog = BuilderServices.Catalog;
            if (catalog != null)
            {
                StudioDock.SmallHeader(page, "The game's enemies");
                foreach (var u in catalog.Units.Where(u => u.Side == UnitSide.Enemy))
                {
                    var unit = u;
                    Pick(page, u.DisplayName, ContentRef.Vanilla(u.Id), () => set(new JValue(ContentRef.Vanilla(unit.Id))));
                }
            }
        }

        // ---- Names --------------------------------------------------------------------------------------------------

        public static void Variable(ModPage page, LogicOwner owner, Action<JToken> set)
        {
            var scope = owner.Scope(null);
            if (scope.LevelVars.Count > 0) StudioDock.SmallHeader(page, "Level");
            foreach (var v in scope.LevelVars.OrderBy(x => x)) Pick(page, "$" + v, "a level variable", () => set(new JValue(v)));
            var own = scope.SelfVars.Except(scope.LevelVars).OrderBy(x => x).ToList();
            if (own.Count > 0) StudioDock.SmallHeader(page, owner.Title ?? "This");
            foreach (var v in own) Pick(page, v, "this thing's variable", () => set(new JValue(v)));
            StudioDock.SmallHeader(page, "New");
            page.TextInput("New on this", () => "", v =>
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                string name = v.Trim().TrimStart('$');
                owner.Edit($"Add variable {name}", n => { if (n["vars"] is not JObject vars) n["vars"] = vars = new JObject(); vars[name] ??= 0; });
                set(new JValue(name));
                RuleEditor.ClosePicker();
            }, () => "Declares a variable on this thing (start value 0) and uses it.");
            if (owner.Kind != OwnerKind.Unit)
                page.TextInput("New on the level", () => "", v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) return;
                    string name = v.Trim().TrimStart('$');
                    LogicOwner.Level().Edit($"Add level variable {name}", n => { if (n["vars"] is not JObject vars) n["vars"] = vars = new JObject(); vars[name] ??= 0; });
                    set(new JValue("$" + name));
                    RuleEditor.ClosePicker();
                }, () => "Declares a level variable (start value 0) and uses it.");
        }

        /// <summary>Signal names used anywhere in the level (sent or listened for).</summary>
        public static List<string> SignalNames()
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (_, _, node) in LogicDoc.Owners(LogicOwner.LevelLogic()))
                foreach (var rule in LogicDoc.Rules(node)?.OfType<JObject>() ?? [])
                {
                    if (LogicDoc.EventOf(rule) == "signal" && (string)(rule["when"] as JObject)?["name"] is string ln) set.Add(ln);
                    foreach (var a in LogicDoc.AllActions(rule["do"] as JArray))
                        if ((string)a["do"] == "signal" && (string)a["name"] is string sn) set.Add(sn);
                }
            return set.ToList();
        }

        public static void Signal(ModPage page, Action<JToken> set)
        {
            StudioDock.SmallHeader(page, "Built-in inputs");
            foreach (var (n, d) in Wires.BuiltIns) Pick(page, n, d, () => set(new JValue(n)));
            var used = SignalNames().Where(n => Wires.BuiltIns.All(b => b.Name != n)).ToList();
            if (used.Count > 0) StudioDock.SmallHeader(page, "In this level");
            foreach (var n in used) Pick(page, n, "a signal name", () => set(new JValue(n)));
            StudioDock.SmallHeader(page, "New");
            page.TextInput("New signal", () => "", v => { if (!string.IsNullOrWhiteSpace(v)) { set(new JValue(v.Trim())); RuleEditor.ClosePicker(); } }, () => "Any name; a wire or a \"gets signal\" rule listens for it.");
        }

        public static void Status(ModPage page, Action<JToken> set) => StatusPanel.Picker(page, id => set(new JValue(id)));

        public static void Stat(ModPage page, Action<JToken> set)
        {
            foreach (var s in LogicCatalog.Stats)
            {
                string stat = s;
                Pick(page, LogicCatalog.Humanize(s), LogicCatalog.StatDescriptions.TryGetValue(s, out var d) ? d : s, () => set(new JValue(stat)));
            }
        }
    }
}
