using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    The rule editor: a wide panel beside the right panel (Level Studio) or the inspector (Enemy Studio) that shows one
    rule as a card, When / If / Then, top to bottom:

        When   this dies                                   ← the event (click: the event picker)
               Whose   this ▸ something else: a foe unit within 12 m
        If     self.$gen < 3                               ← formulas, checked as you type
        Then   1  Spawn Slime × 2 at this                  ← click to open its fields; nested lists indent
               2  Effect smoke at this
               + action
        Options  once · cooldown · starts off · id · note

    Pickers open in the same panel (search, categories, one-line descriptions), so long lists scroll and never clip;
    Back or Esc returns to the card. Every change is one undo step on the draft.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class RuleEditor
    {
        public const float Width = 620f;

        private static readonly Dictionary<ModDock, ModDockPanel> panels = [];
        private static ModDock dock;
        private static ModDockPanel panel;
        private static bool dirty;

        public static LogicOwner Owner { get; private set; }
        public static int Rule { get; private set; } = -1;
        public static bool IsOpen => Owner != null && Rule >= 0 && panel?.Visible == true;
        /// <summary>The panel is up (a rule, or a picker on its own such as the Wire tool's).</summary>
        public static bool IsShown => panel?.Visible == true && (IsOpen || picker != null);
        public static bool Standalone => Owner == null && picker != null;
        public static ModDock Dock => dock;
        /// <summary>The panel's rect while shown (dev: press its rows first).</summary>
        public static RectTransform PanelRect => panel?.Visible == true ? panel.Rect : null;

        // Picker mode: a title and a builder replacing the card until Back.
        private static string pickerTitle;
        private static Action<ModPage> picker;
        public static string Search = "", Category;

        /// <summary>Expanded actions, by path key ("0", "0/then/1").</summary>
        private static readonly HashSet<string> expanded = [];

        /// <summary>Opens a rule of an owner beside a dock's panels. offsetMin/Max place the panel (anchored to the right
        /// edge, full height).</summary>
        public static void Open(LogicOwner owner, int rule, ModDock onDock, Vector2 offsetMin, Vector2 offsetMax)
        {
            if (onDock == null || owner == null) return;
            if (!panels.TryGetValue(onDock, out var p))
            {
                p = onDock.AddPanel("Rule", new Vector2(1, 0), new Vector2(1, 1), offsetMin, offsetMax);
                panels[onDock] = p;
            }
            p.OffsetMin = offsetMin;
            p.OffsetMax = offsetMax;
            p.Layout();
            if (panel != null && panel != p) panel.SetVisible(false);
            bool same = Owner != null && Owner.Equals(owner) && Rule == rule;
            dock = onDock;
            panel = p;
            Owner = owner;
            Rule = rule;
            if (!same) { expanded.Clear(); picker = null; pickerTitle = null; }
            panel.SetVisible(true);
            dirty = true;
        }

        /// <summary>A picker in the panel with no rule behind it (the Wire tool's event and input lists).</summary>
        public static void OpenStandalone(ModDock onDock, Vector2 offsetMin, Vector2 offsetMax, string title, Action<ModPage> build)
        {
            Close();
            Open(LogicOwner.Level(), -1, onDock, offsetMin, offsetMax);
            Owner = null;
            OpenPicker(title, build);
        }

        public static void Close()
        {
            Owner = null;
            Rule = -1;
            picker = null;
            expanded.Clear();
            panel?.SetVisible(false);
        }

        /// <summary>Esc: closes the picker, else the editor. True if it took the key.</summary>
        public static bool Cancel()
        {
            if (!IsShown) return false;
            if (ModTextInput.Active) return false;
            if (picker != null) { ClosePicker(); return true; }
            Close();
            return true;
        }

        public static void MarkDirty() => dirty = true;

        /// <summary>Per frame while its dock is up: rebuilds after changes (never while typing).</summary>
        public static void Tick()
        {
            if (panel == null || !panel.Visible || (Owner == null && picker == null)) return;
            if (panel.Rect == null) { panel.SetVisible(false); return; }
            if (!dirty || ModTextInput.Active) return;
            if (Input.GetMouseButton(0)) return; // not under a held click
            dirty = false;
            if (Owner != null)
            {
                var rules = Owner.Rules();
                if (rules == null || Rule < 0 || Rule >= rules.Count) { Close(); return; }
            }
            panel.Fill(Build);
        }

        public static void OpenPicker(string title, Action<ModPage> build)
        {
            pickerTitle = title;
            picker = build;
            Search = "";
            Category = null;
            dirty = true;
        }

        public static void ClosePicker()
        {
            picker = null;
            pickerTitle = null;
            dirty = true;
            if (Owner == null) Close();
        }

        /// <summary>Opens the event picker for the open rule (a new rule starts here).</summary>
        public static void PickEvent() => OpenPicker("When…", EventPicker);

        private static JObject CurrentRule() => Owner?.Rules()?[Rule] as JObject;

        // ---- Build --------------------------------------------------------------------------------------------------

        private static void Build(ModPage page)
        {
            page.FieldIndent = 190f;
            if (picker != null)
            {
                StudioDock.SmallHeader(page, LogicFields.Esc(pickerTitle));
                LogicFields.Action(page, Owner == null ? "Cancel" : "Back to the rule", ClosePicker, "Esc");
                try { picker(page); }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Rule editor picker failed", e); }
                return;
            }
            var rule = CurrentRule();
            if (rule == null) return;
            int count = Owner.Rules()?.Count ?? 0;
            StudioDock.SmallHeader(page, $"{LogicFields.Esc(Owner.Title)} · rule {Rule + 1} of {count}");
            LogicFields.Buttons(page,
                ("Close", Close, "Esc"),
                ("Previous", Rule > 0 ? () => Open(Owner, Rule - 1, dock, panel.OffsetMin, panel.OffsetMax) : null, null),
                ("Next", Rule < count - 1 ? () => Open(Owner, Rule + 1, dock, panel.OffsetMin, panel.OffsetMax) : null, null));
            if (rule["preset"] != null) { PresetCard(page, rule); return; }
            var ev = LogicCatalog.Event(LogicDoc.EventOf(rule));
            WhenSection(page, rule, ev);
            IfSection(page, rule, ev);
            ThenSection(page, rule, ev);
            OptionsSection(page, rule);
        }

        // ---- When ---------------------------------------------------------------------------------------------------

        private static void WhenSection(ModPage page, JObject rule, EventUi ev)
        {
            LogicFields.Section(page, "When", LogicFields.WhenColor);
            string name = LogicDoc.EventOf(rule);
            StudioDock.DetailRow(page, LogicFields.Esc(RuleText.When(rule, Owner.Kind)), ev == null ? (name == null ? "choose an event" : $"unknown event \"{name}\"") : LogicFields.Esc(ev.Desc),
                false, null, () => OpenPicker("When…", EventPicker), () => "Click to choose another event.");
            if (ev == null) return;
            var when = rule["when"] as JObject;
            if (ev.CanOf)
            {
                bool of = when?["of"] != null;
                bool mustOf = Owner.Kind == OwnerKind.Level;
                if (!mustOf)
                    page.Choice("Whose", ["This", "Something else"], () => of ? 1 : 0, i => Owner.EditRule(Rule, "Set whose event", r =>
                    {
                        var w = LogicDoc.When(r);
                        if (i == 1) w["of"] = w["of"] ?? "foe and unit"; else { w.Remove("of"); w.Remove("range"); }
                    }), tooltip: () => "This: only its own events. Something else: any matching thing's (a necromancer hears foes die).");
                if (of || mustOf)
                {
                    var scope = Owner.Scope(ev);
                    var fp = new ParamUi { Name = "of", Label = "Of", Kind = ParamKind.Filter, Required = mustOf, Desc = "Whose event: a filter (foe and unit, #shrine, player and Tower)." };
                    LogicFields.Param(page, "Of what", fp, () => (rule["when"] as JObject)?["of"], (v, l) => SetWhen("of", v, l), scope, () => OpenPicker("Of what…", p => FilterPicker(p, (string)(CurrentRule()?["when"] as JObject)?["of"], f => SetWhen("of", f == null ? null : new JValue(f), "Set whose event"), false)));
                    var rp = new ParamUi { Name = "range", Label = "Range", Kind = ParamKind.Number, Desc = "Only within this many metres (empty: anywhere)." };
                    LogicFields.Param(page, "Within (m)", rp, () => (rule["when"] as JObject)?["range"], (v, l) => SetWhen("range", v, l), scope);
                }
            }
            var evScope = Owner.Scope(ev);
            foreach (var p in ev.Params)
            {
                var param = p;
                LogicFields.Param(page, p.Label, p, () => (CurrentRule()?["when"] as JObject)?[param.Name], (v, l) => SetWhen(param.Name, v, l), evScope, PickerFor(param, evScope, v => SetWhen(param.Name, v, $"Set {param.Label}")));
            }
        }

        private static void SetWhen(string field, JToken v, string label) => Owner.EditRule(Rule, label, r =>
        {
            var w = LogicDoc.When(r);
            if (v == null) w.Remove(field); else w[field] = v;
        }, "when:" + field);

        // ---- If -----------------------------------------------------------------------------------------------------

        private static void IfSection(ModPage page, JObject rule, EventUi ev)
        {
            LogicFields.Section(page, "If", LogicFields.IfColor);
            var conds = LogicDoc.Conditions(rule);
            var scope = Owner.Scope(ev);
            if (conds.Count == 0) page.Text(LogicFields.Dim("Always."));
            for (int i = 0; i < conds.Count; i++)
            {
                int index = i;
                var p = new ParamUi { Name = "if", Label = "If", Kind = ParamKind.Condition, Desc = "A yes/no formula. Empty it to remove it." };
                LogicFields.Param(page, i == 0 ? "If" : "and", p, () => { var c = LogicDoc.Conditions(CurrentRule()); return index < c.Count ? new JValue(c[index]) : null; },
                    (v, l) => Owner.EditRule(Rule, v == null ? "Remove condition" : "Set condition", r =>
                    {
                        var list = LogicDoc.Conditions(r);
                        if (index >= list.Count) return;
                        if (v == null) list.RemoveAt(index); else list[index] = (string)v;
                        LogicDoc.SetConditions(r, list);
                    }), scope);
            }
            LogicFields.Action(page, "+ condition", () => OpenPicker("Add a condition", p => ConditionPicker(p, ev)), "A test that must hold for the rule to act.");
        }

        // ---- Then ---------------------------------------------------------------------------------------------------

        private static void ThenSection(ModPage page, JObject rule, EventUi ev)
        {
            LogicFields.Section(page, "Then", LogicFields.ThenColor);
            ActionList(page, rule, [], ev, 0, spawned: false);
        }

        private static string PathKey(IReadOnlyList<(int Index, string Field)> path, int i) =>
            string.Join("/", path.Select(s => $"{s.Index}/{s.Field}")) + (path.Count > 0 ? "/" : "") + i;

        private static void ActionList(ModPage page, JObject rule, List<(int Index, string Field)> path, EventUi ev, int depth, bool spawned)
        {
            var list = LogicDoc.ActionList(rule, path, false);
            string indent = new(' ', depth * 4);
            int n = list?.Count ?? 0;
            if (n == 0) page.Text(LogicFields.Dim(indent + (depth == 0 ? "Nothing yet." : "Nothing.")));
            for (int i = 0; i < n; i++)
            {
                if (list[i] is not JObject a) continue;
                int index = i;
                string key = PathKey(path, i);
                bool open = expanded.Contains(key);
                var ui = LogicCatalog.Action((string)a["do"]);
                string label = $"{indent}{i + 1}  {LogicFields.Esc(RuleText.Action(a, ev))}";
                StudioDock.DetailRow(page, label, open ? null : LogicFields.Esc(ui?.Desc ?? ""), open, null, () => { if (!expanded.Remove(key)) expanded.Add(key); dirty = true; },
                    () => ui == null ? "Unknown action (made with a newer PalaceMod?)" : ui.Desc + "\nClick to " + (open ? "close." : "edit."));
                if (!open) continue;
                ActionFields(page, rule, path, index, a, ui, ev, depth, spawned);
            }
            var at = path.ToList();
            LogicFields.Action(page, indent + "+ action", () => OpenPicker("Add an action", p => ActionPicker(p, at, ev)), "Something the rule does.");
        }

        private static void ActionFields(ModPage page, JObject rule, List<(int Index, string Field)> path, int index, JObject a, ActionUi ui, EventUi ev, int depth, bool spawned)
        {
            string indent = new(' ', depth * 4 + 4);
            if (ui == null)
            {
                var jp = new ParamUi { Name = "json", Label = "JSON", Kind = ParamKind.Json };
                LogicFields.Param(page, indent + "JSON", jp, () => a, (v, l) => EditAction(path, index, "Edit action", x => { if (v is JObject o) { x.RemoveAll(); foreach (var pp in o.Properties()) x[pp.Name] = pp.Value; } }), null);
            }
            else
            {
                var scope = Owner.Scope(ev, spawned);
                foreach (var p in ui.Params.Where(p => p.Kind != ParamKind.Actions && !(ui.Name == "choose" && p.Name == "options")))
                {
                    var param = p;
                    LogicFields.Param(page, indent + p.Label, p, () => CurrentAction(path, index)?[param.Name],
                        (v, l) => EditAction(path, index, l, x => { if (v == null) x.Remove(param.Name); else x[param.Name] = v; }, $"act:{PathKey(path, index)}:{param.Name}"),
                        scope, PickerFor(param, scope, v => EditAction(path, index, $"Set {param.Label}", x => { if (v == null) x.Remove(param.Name); else x[param.Name] = v; })));
                }
                bool spawns = ui.Name is "spawn" or "launch";
                foreach (var nested in ui.Nested)
                {
                    page.Text(LogicFields.Dim($"{indent}{LogicCatalog.Humanize(nested)}{(spawns && nested == "then" ? " (spawned: the new one)" : nested == "onImpact" ? " (other: what it hit)" : "")}"));
                    var child = path.ToList();
                    child.Add((index, nested));
                    ActionList(page, rule, child, ev, depth + 1, spawned || (spawns && nested == "then"));
                }
                if (ui.Name == "choose") ChooseOptions(page, rule, path, index, a, ev, depth, spawned);
            }
            var list = LogicDoc.ActionList(rule, path, false);
            LogicFields.Buttons(page,
                ("Up", index > 0 ? () => MoveAction(path, index, -1) : null, "Run it earlier"),
                ("Down", index < (list?.Count ?? 0) - 1 ? () => MoveAction(path, index, 1) : null, "Run it later"),
                ("Copy", () => EditList(path, "Duplicate action", l => l.Insert(index + 1, l[index].DeepClone())), "Duplicate it"),
                ("Delete", () => { EditList(path, "Delete action", l => l.RemoveAt(index)); expanded.Clear(); }, null));
        }

        private static void ChooseOptions(ModPage page, JObject rule, List<(int Index, string Field)> path, int index, JObject a, EventUi ev, int depth, bool spawned)
        {
            string indent = new(' ', depth * 4 + 4);
            var opts = a["options"] as JArray;
            for (int o = 0; o < (opts?.Count ?? 0); o++)
            {
                int oi = o;
                var wp = new ParamUi { Name = "weight", Label = "Weight", Kind = ParamKind.Number, Default = "1", Desc = "How likely, against the other options' weights." };
                LogicFields.Param(page, $"{indent}Option {o + 1} weight", wp, () => ((CurrentAction(path, index)?["options"] as JArray)?[oi] as JObject)?["weight"],
                    (v, l) => EditAction(path, index, "Set weight", x => { if ((x["options"] as JArray)?[oi] is JObject opt) { if (v == null) opt.Remove("weight"); else opt["weight"] = v; } }), null);
                var child = path.ToList();
                child.Add((index, $"options/{oi}"));
                ActionList(page, rule, child, ev, depth + 1, spawned);
            }
            LogicFields.Action(page, indent + "+ option", () => EditAction(path, index, "Add option", x =>
            {
                if (x["options"] is not JArray arr) x["options"] = arr = new JArray();
                arr.Add(new JObject { ["weight"] = 1, ["do"] = new JArray() });
            }));
        }

        private static JObject CurrentAction(List<(int Index, string Field)> path, int index)
        {
            var list = LogicDoc.ActionList(CurrentRule(), path, false);
            return list != null && index < list.Count ? list[index] as JObject : null;
        }

        private static void EditList(List<(int Index, string Field)> path, string label, Action<JArray> change) =>
            Owner.EditRule(Rule, label, r => change(LogicDoc.ActionList(r, path, true) ?? throw new InvalidOperationException("that list is gone")));

        private static void EditAction(List<(int Index, string Field)> path, int index, string label, Action<JObject> change, string coalesce = null) =>
            Owner.EditRule(Rule, label, r =>
            {
                var list = LogicDoc.ActionList(r, path, true);
                if (list == null || index >= list.Count || list[index] is not JObject a) throw new InvalidOperationException("that action is gone");
                change(a);
            }, coalesce);

        private static void MoveAction(List<(int Index, string Field)> path, int index, int delta)
        {
            EditList(path, "Reorder actions", l =>
            {
                var t = l[index];
                t.Remove();
                l.Insert(index + delta, t);
            });
            string from = PathKey(path, index), to = PathKey(path, index + delta);
            if (expanded.Remove(from)) expanded.Add(to);
        }

        // ---- Options ------------------------------------------------------------------------------------------------

        private static void OptionsSection(ModPage page, JObject rule)
        {
            StudioDock.SmallHeader(page, "Options");
            page.Toggle("Only once", () => CurrentRule()?["once"]?.Type == JTokenType.Boolean && (bool)CurrentRule()["once"],
                v => Owner.EditRule(Rule, "Set once", r => { if (v) r["once"] = true; else r.Remove("once"); }), () => "Fires at most once per owner each run.");
            var cd = new ParamUi { Name = "cooldown", Label = "Cooldown", Kind = ParamKind.Number, Desc = "Seconds before it can fire again." };
            LogicFields.Param(page, "Cooldown (s)", cd, () => CurrentRule()?["cooldown"], (v, l) => Owner.EditRule(Rule, l, r => { if (v == null || (v.Type is JTokenType.Integer or JTokenType.Float && (double)v <= 0)) r.Remove("cooldown"); else r["cooldown"] = v; }, "cooldown"), null);
            page.Toggle("Starts on", () => !(CurrentRule()?["enabled"]?.Type == JTokenType.Boolean && !(bool)CurrentRule()["enabled"]),
                v => Owner.EditRule(Rule, v ? "Start on" : "Start off", r => { if (v) r.Remove("enabled"); else r["enabled"] = false; }), () => "Off: it waits until a \"Turn a rule on\" action names it.");
            var idp = new ParamUi { Name = "id", Label = "Id", Kind = ParamKind.Text, Desc = "A name other rules use to turn this one on or off." };
            LogicFields.Param(page, "Id", idp, () => CurrentRule()?["id"], (v, l) => Owner.EditRule(Rule, "Set rule id", r => { if (v == null) r.Remove("id"); else r["id"] = v; }), null);
            var np = new ParamUi { Name = "note", Label = "Note", Kind = ParamKind.Text, Desc = "Shown on the card and in hovers." };
            LogicFields.Param(page, "Note", np, () => CurrentRule()?["note"], (v, l) => Owner.EditRule(Rule, "Set note", r => { if (v == null) r.Remove("note"); else r["note"] = v; }), null);
            LogicFields.Buttons(page,
                ("Duplicate", () => { Owner.Edit("Duplicate rule", n => { var rules = LogicDoc.EnsureRules(n); rules.Insert(Rule + 1, rules[Rule].DeepClone()); }); Open(Owner, Rule + 1, dock, panel.OffsetMin, panel.OffsetMax); }, null),
                ("Copy", () => { GUIUtility.systemCopyBuffer = CurrentRule()?.ToString(Newtonsoft.Json.Formatting.Indented) ?? ""; Notify.Show("Rule copied as JSON", NotifyKind.Info, 2f, "logic"); }, "The same JSON agents write"),
                ("Save as preset", () => PresetUi.SaveRule(Owner, Rule), "Reuse it on other things"),
                ("Delete", () => { int r = Rule; Owner.Edit("Delete rule", n => LogicDoc.EnsureRules(n).RemoveAt(r)); Close(); }, null));
        }

        // ---- Presets ------------------------------------------------------------------------------------------------

        private static void PresetCard(ModPage page, JObject rule)
        {
            string id = (string)rule["preset"];
            var preset = PresetUi.Find(id);
            LogicFields.Section(page, "Preset", LogicFields.ThenColor);
            StudioDock.DetailRow(page, LogicFields.Esc(preset?.Name ?? id), LogicFields.Esc(preset?.Describe(rule["with"] as JObject) ?? $"preset \"{id}\" not found"), false, null, null);
            foreach (var p in preset?.Params ?? [])
            {
                var param = p;
                LogicFields.Param(page, p.Label, p, () => (CurrentRule()?["with"] as JObject)?[param.Name],
                    (v, l) => Owner.EditRule(Rule, l, r =>
                    {
                        if (r["with"] is not JObject w) r["with"] = w = new JObject();
                        if (v == null) w.Remove(param.Name); else w[param.Name] = v;
                    }, "with:" + param.Name), Owner.Scope(null));
            }
            LogicFields.Buttons(page,
                ("Unpack", preset == null ? null : () => PresetUi.Unpack(Owner, Rule), "Turn it into plain rules you can edit"),
                ("Delete", () => { int r = Rule; Owner.Edit("Delete rule", n => LogicDoc.EnsureRules(n).RemoveAt(r)); Close(); }, null));
        }

        // ---- Pickers ------------------------------------------------------------------------------------------------

        /// <summary>The picker for a param kind (null: the field is typed in).</summary>
        private static Action PickerFor(ParamUi p, ExprScope scope, Action<JToken> set) => p.Kind switch
        {
            ParamKind.Entity or ParamKind.Target or ParamKind.Position => () => OpenPicker($"{p.Label}…", page => Pickers.Value(page, p, scope, set)),
            ParamKind.Template => () => OpenPicker($"{p.Label}…", page => Pickers.Template(page, set)),
            ParamKind.Var => () => OpenPicker("Variable…", page => Pickers.Variable(page, Owner, set)),
            ParamKind.Signal => () => OpenPicker("Signal…", page => Pickers.Signal(page, set)),
            ParamKind.Status => () => OpenPicker("Status…", page => Pickers.Status(page, set)),
            ParamKind.Stat => () => OpenPicker("Stat…", page => Pickers.Stat(page, set)),
            ParamKind.Filter => () => OpenPicker($"{p.Label}…", page => FilterPicker(page, null, f => set(f == null ? null : new JValue(f)), false)),
            _ => null,
        };

        private static void EventPicker(ModPage page)
        {
            var events = LogicCatalog.Events.Where(e => e.Owners == null || Owner.Kind == OwnerKind.Place && PlaceItems.KindOf(Owner.Id) is string k && e.Owners.Contains(k)).ToList();
            Pickers.Catalog(page, events, e => e.Category, e => e.Common, e => (e.Owners == null && e.CanOf && Owner.Kind != OwnerKind.Level ? "this " : "") + e.Phrase.Replace("{", "").Replace("}", ""), e => e.Desc, e => e.Name,
                LogicCatalog.EventCategories, e => Owner.EditRule(Rule, "Set event", r =>
                {
                    var fresh = LogicDoc.NewRule(e.Name);
                    var keepOf = (r["when"] as JObject)?["of"];
                    r["when"] = fresh["when"];
                    if (keepOf != null && e.CanOf) LogicDoc.When(r)["of"] = keepOf;
                    if (Owner.Kind == OwnerKind.Level && e.CanOf && keepOf == null) LogicDoc.When(r)["of"] = "enemy";
                }), close: true);
        }

        private static void ActionPicker(ModPage page, List<(int Index, string Field)> path, EventUi ev)
        {
            Pickers.Catalog(page, LogicCatalog.Actions, a => a.Category, a => a.Common, a => a.Label, a => a.Desc, a => a.Name, LogicCatalog.ActionCategories, a =>
            {
                int added = -1;
                EditList(path, $"Add {a.Label}", l => { l.Add(LogicDoc.NewAction(a.Name)); added = l.Count - 1; });
                if (added >= 0) expanded.Add(PathKey(path, added));
            }, close: true);
        }

        private static void ConditionPicker(ModPage page, EventUi ev)
        {
            var scope = Owner.Scope(ev);
            var common = new List<(string label, string expr, string tip)>
            {
                ("A chance", "chance(0.3)", "Passes 30 % of the time."),
                ("At night", "isNight", null),
                ("By day", "isDay", null),
                ("From night 3 on", "night >= 3", null),
                ("Below half health", "self.hpPct < 0.5", null),
                ("Foes nearby", "count('foe', 10) >= 3", "Three or more foes within 10 m."),
                ("Gold at least", "gold >= 10", null),
            };
            if (ev?.Other != null) common.Add(($"The {ev.Other} is a unit", "other.is('unit')", null));
            foreach (var v in scope.LevelVars) common.Add(($"${v} at least", $"${v} >= 1", "A level variable."));
            foreach (var v in scope.SelfVars.Except(scope.LevelVars)) common.Add(($"Its {v} below", $"self.${v} < 3", "This thing's variable."));
            foreach (var (label, expr, tip) in common)
                StudioDock.DetailRow(page, LogicFields.Esc(label), LogicFields.Esc(expr), false, null, () => { AddCondition(expr); ClosePicker(); }, () => (tip ?? "") + "\nAdds it; edit the numbers after.");
            StudioDock.SmallHeader(page, "Your own");
            var p = new ParamUi { Name = "if", Label = "Formula", Kind = ParamKind.Condition, Desc = "Any yes/no formula." };
            JToken typed = null;
            LogicFields.Param(page, "Formula", p, () => typed, (v, l) => { if (v != null) { AddCondition((string)v); ClosePicker(); } }, scope);
        }

        private static void AddCondition(string expr) => Owner.EditRule(Rule, "Add condition", r =>
        {
            var list = LogicDoc.Conditions(r);
            list.Add(expr);
            LogicDoc.SetConditions(r, list);
        });

        // A filter, built from parts or typed.
        private sealed class FilterDraft
        {
            public string Team = "foe", Kind = "unit", Extra = "";
        }

        private static FilterDraft filterDraft;

        public static void FilterPicker(ModPage page, string current, Action<string> set, bool selector)
        {
            filterDraft ??= new FilterDraft();
            var d = filterDraft;
            string[] teams = ["any", "foe", "ally", "player", "enemy", "neutral"];
            string[] kinds = ["anything", "unit", "building", "king", "castle", "projectile", "area", "pickup", "coin"];
            page.Choice("Team", teams.Select(LogicCatalog.Humanize).ToList(), () => Array.IndexOf(teams, d.Team ?? "any"), i => { d.Team = teams[i]; dirty = true; },
                tooltip: () => "foe/ally are relative to the rule's owner; player/enemy/neutral are fixed sides.");
            page.Choice("Kind", kinds.Select(LogicCatalog.Humanize).ToList(), () => Math.Max(0, Array.IndexOf(kinds, d.Kind ?? "anything")), i => { d.Kind = kinds[i]; dirty = true; });
            page.TextInput("And also", () => d.Extra, v => { d.Extra = v ?? ""; dirty = true; },
                () => "More terms: Tower, Flying, melee, #label, is:goblin (a template), in:@zone, has:burning, not Wall.", 200);
            string built = Build(d);
            var issues = ExprLint.CheckFilter(built);
            StudioDock.DetailRow(page, "Use: " + LogicFields.Esc(built), LogicFields.Esc(RuleText.Filter(built)) + LogicFields.IssueText(issues), true, null, () => { set(built); ClosePicker(); });
            if (!string.IsNullOrEmpty(current))
            {
                StudioDock.SmallHeader(page, "Or type it");
                var fp = new ParamUi { Name = "filter", Label = "Filter", Kind = ParamKind.Filter };
                LogicFields.Param(page, "Filter", fp, () => new JValue(current), (v, l) => { set((string)v); ClosePicker(); }, null);
            }
            else
            {
                StudioDock.SmallHeader(page, "Or type it");
                var fp = new ParamUi { Name = "filter", Label = "Filter", Kind = ParamKind.Filter };
                JToken none = null;
                LogicFields.Param(page, "Filter", fp, () => none, (v, l) => { set((string)v); ClosePicker(); }, null);
            }
        }

        private static string Build(FilterDraft d)
        {
            var parts = new List<string>();
            if (d.Team != null && d.Team != "any") parts.Add(d.Team);
            if (d.Kind != null && d.Kind != "anything") parts.Add(d.Kind);
            var extra = (d.Extra ?? "").Trim();
            if (extra.Length > 0) parts.Add(extra);
            return parts.Count == 0 ? "unit" : string.Join(" and ", parts);
        }
    }
}
