using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;

/*
    The Behaviour tab: what the selected thing does. Its name (@name for other rules), labels and variables, then its
    rules as one-line cards ("When this dies → Spawn Slime × 2"); a card opens the rule editor beside the panel. With
    nothing selected it shows the level's own logic: variables, the level script, king actions, HUD lines and limits.
    The Enemy Studio's Behaviour tab is the same, for the unit (plus the "affects the level" badge).
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class BehaviourPanel
    {
        private static string Esc(string s) => LogicFields.Esc(s);
        private static string Dim(string s) => LogicFields.Dim(s);

        /// <summary>The tab for an owner (null: the level).</summary>
        public static void Build(ModPage page, LogicOwner owner)
        {
            page.FieldIndent = Math.Min(page.FieldIndent, 150f);
            // A template or status opened from here shows in place of the selection, with the way back on top.
            var focus = LogicStudio.Focus;
            if (focus != null && !focus.Equals(owner))
            {
                if (focus.Read() == null) LogicStudio.FocusOn(null);
                else
                {
                    LogicFields.Action(page, "← " + Esc(owner == null || owner.Kind == OwnerKind.Level ? "This level" : owner.Title), () => LogicStudio.FocusOn(null), "Back to what's selected.");
                    owner = focus;
                }
            }
            if (owner == null || owner.Kind == OwnerKind.Level) { Level(page); return; }
            var node = owner.Read() ?? new JObject();
            StudioDock.SmallHeader(page, Esc(owner.Kind == OwnerKind.Status ? "Status: " + owner.Title : owner.Kind == OwnerKind.Entity ? "Thing: " + owner.Title : owner.Title));
            if (owner.Kind == OwnerKind.Status) { StatusPanel.Build(page, owner); return; }
            if (owner.IsTemplate) Badge(page, node);
            if (owner.Kind is OwnerKind.Attach or OwnerKind.Place) NameField(page, owner, node);
            LabelsField(page, owner);
            if (owner.Kind == OwnerKind.Place) PlaceFields(page, owner, node);
            if (owner.Kind is OwnerKind.Place or OwnerKind.Entity) PartsPanel.Build(page, owner);
            Variables(page, owner, "Variables", "Numbers, yes/no, text or positions this thing keeps (self.$name).");
            Rules(page, owner, "Rules");
            if (!owner.IsTemplate) WireList(page, owner);
        }

        // ---- Badge -------------------------------------------------------------------------------------------------

        /// <summary>"This enemy affects the level": its rules give gold, change nights, end the level…</summary>
        private static void Badge(ModPage page, JObject node)
        {
            var rules = (node["rules"] as JArray)?.ToObject<List<PalaceMod.Builder.Model.Logic.RuleDef>>() ?? [];
            var effects = PalaceMod.Builder.Model.Logic.LevelEffects.Of(rules);
            if (effects.Count == 0) return;
            var row = page.Row(LogicFields.Tint("! Affects the level", LogicFields.WarnColor), null,
                () => "Its rules reach beyond itself: " + string.Join(", ", effects) + ".\nShared with others, it does this in their levels too.");
            StudioDock.Compact(row, false, 10f, page.Size.x);
        }

        // ---- Identity ----------------------------------------------------------------------------------------------

        private static void NameField(ModPage page, LogicOwner owner, JObject node)
        {
            string current = LogicDoc.RefName(owner.Kind, owner.Id, node);
            page.TextInput("Name", () => LogicDoc.RefName(owner.Kind, owner.Id, owner.Read()) ?? "", v =>
            {
                v = v?.Trim();
                string clean = string.IsNullOrEmpty(v) ? null : LogicDoc.NameFor(v, new HashSet<string>());
                owner.Edit("Rename", n => { if (clean == null) n.Remove("name"); else n["name"] = clean; });
            }, () => "Other rules reach it as @name." + (current != null ? $" Now: @{current}." : ""), 40,
                () => current == null ? Dim("none") : "@" + Esc(current));
        }

        private static void LabelsField(ModPage page, LogicOwner owner)
        {
            page.TextInput("Labels", () => string.Join(", ", (owner.Read()?["labels"] as JArray)?.Select(t => (string)t) ?? []), v =>
            {
                var list = (v ?? "").Split(',').Select(s => s.Trim().TrimStart('#')).Where(s => s.Length > 0).Distinct().ToList();
                owner.Edit("Set labels", n => { if (list.Count == 0) n.Remove("labels"); else n["labels"] = new JArray(list); });
            }, () => "Words filters can test with #label (e.g. #shrine), separated by commas.", 200,
                () => { var l = (owner.Read()?["labels"] as JArray)?.Select(t => "#" + (string)t).ToList() ?? []; return l.Count == 0 ? Dim("none") : Esc(string.Join(" ", l)); });
        }

        // ---- Place components --------------------------------------------------------------------------------------

        private static void PlaceFields(ModPage page, LogicOwner owner, JObject node)
        {
            string kind = PlaceItems.KindOf(node);
            page.Toggle("Starts on", () => !(owner.Read()?["active"]?.Type == JTokenType.Boolean && !(bool)owner.Read()["active"]),
                v => owner.Edit(v ? "Start on" : "Start off", n => { if (v) n.Remove("active"); else n["active"] = false; }),
                () => "Off: its rules, body and look wait until something sends it \"enable\".");
            if (kind == "path" && node["points"] is JArray pts)
            {
                StudioDock.SmallHeader(page, "Points");
                for (int i = 0; i < pts.Count; i++)
                {
                    var pt = pts[i] as JArray;
                    StudioDock.Compact(page.Row($"Point {i + 1}", pt == null ? "" : $"x {(float)pt[0]:0.#}  z {(float)pt[pt.Count - 1]:0.#}"), false, 60f, page.Size.x);
                }
                page.Text(Dim("Drag the points on the map; Ctrl-click one to remove it, click a segment's middle to add one."));
                LogicFields.Buttons(page, ("+ point", () => AddPathPoint(owner), "Adds a point after the last, 4 m on"),
                    ("− point", pts.Count > 2 ? () => owner.Edit("Remove point", n => ((JArray)n["points"]).Last.Remove()) : null, null));
            }
        }

        private static void AddPathPoint(LogicOwner owner) => owner.Edit("Add point", n =>
        {
            var pts = (JArray)n["points"];
            var last = (JArray)pts.Last;
            var prev = pts.Count > 1 ? (JArray)pts[pts.Count - 2] : null;
            float dx = prev != null ? (float)last[0] - (float)prev[0] : 4f, dz = prev != null ? (float)last[last.Count - 1] - (float)prev[prev.Count - 1] : 0f;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.1f) { dx = 4f; dz = 0f; len = 4f; }
            var next = (JArray)last.DeepClone();
            next[0] = Round((float)last[0] + dx / len * 4f);
            next[next.Count - 1] = Round((float)last[last.Count - 1] + dz / len * 4f);
            pts.Add(next);
        });

        private static double Round(float v) => Math.Round(v, 2);

        // ---- Variables ---------------------------------------------------------------------------------------------

        /// <summary>A var's start value as typed ("0", "true", "'summer'", "vec(1, 2)", "[]") → JSON.</summary>
        public static JToken ParseStart(string typed)
        {
            typed = typed?.Trim() ?? "";
            if (double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d == Math.Floor(d) ? new JValue((long)d) : new JValue(d);
            if (typed is "true" or "false") return new JValue(typed == "true");
            if (typed == "[]") return new JArray();
            if (typed.StartsWith("{")) { try { return JToken.Parse(typed); } catch { } }
            if (typed.Length >= 2 && typed[0] != '\'' && !typed.StartsWith("vec(")) typed = "'" + typed.Trim('"') + "'";
            return new JValue(typed);
        }

        public static void Variables(ModPage page, LogicOwner owner, string title, string tip)
        {
            StudioDock.SmallHeader(page, title);
            var vars = owner.Read()?["vars"] as JObject;
            if (vars == null || !vars.Properties().Any()) page.Text(Dim("None."));
            foreach (var p in vars?.Properties().ToList() ?? [])
            {
                string name = p.Name;
                string prefix = owner.Kind == OwnerKind.Level ? "$" : "";
                page.TextInput(prefix + name, () => LogicFields.Raw((owner.Read()?["vars"] as JObject)?[name]), v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) owner.Edit($"Remove variable {name}", n => (n["vars"] as JObject)?.Remove(name));
                    else owner.Edit($"Set {name}", n => ((JObject)n["vars"])[name] = ParseStart(v));
                }, () => tip + "\nThe start value: 0, true, 'text', vec(x, z), [] for a list. Empty it to remove it.", 120);
            }
            page.TextInput("+ variable", () => "", v =>
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                string name = v.Trim().TrimStart('$');
                string start = "0";
                int eq = name.IndexOf('=');
                if (eq > 0) { start = name.Substring(eq + 1); name = name.Substring(0, eq).Trim(); }
                if (!Ids(name)) { Notify.Show("Letters, digits and _ only", NotifyKind.Info, 2f, "logic"); return; }
                owner.Edit($"Add variable {name}", n => { if (n["vars"] is not JObject o) n["vars"] = o = new JObject(); o[name] = ParseStart(start); });
            }, () => "A name, or name = start value (souls = 0).", 60, () => Dim("add…"));
        }

        private static bool Ids(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_') && !char.IsDigit(s[0]);

        // ---- Rules -------------------------------------------------------------------------------------------------

        public static void Rules(ModPage page, LogicOwner owner, string title)
        {
            StudioDock.SmallHeader(page, title);
            var rules = owner.Rules();
            if (rules == null || rules.Count == 0) page.Text(Dim(owner.Kind == OwnerKind.Level ? "None yet." : "None yet."));
            for (int i = 0; i < (rules?.Count ?? 0); i++)
            {
                int index = i;
                var rule = rules[i] as JObject;
                bool on = RuleEditor.IsOpen && owner.Equals(RuleEditor.Owner) && RuleEditor.Rule == i;
                bool off = rule?["enabled"]?.Type == JTokenType.Boolean && !(bool)rule["enabled"];
                string summary = RuleText.Summary(rule, owner.Kind);
                string detail = string.Join(" · ", new[] { (string)rule?["note"], RuleText.Options(rule), LiveCount(owner, i) }.Where(s => !string.IsNullOrEmpty(s)));
                StudioDock.DetailRow(page, (off ? Dim(Esc(summary)) : Esc(summary)), Esc(detail), on, null, () => LogicStudio.OpenRule(owner, index),
                    () => summary + "\nClick to edit.");
            }
            LogicFields.Buttons(page,
                ("+ rule", () => LogicStudio.NewRule(owner), "When something happens, if…, then do…"),
                ("+ preset", () => LogicStudio.PickPreset(owner), "A saved rule set"),
                ("Paste", () => PasteRule(owner), "A rule copied as JSON"));
        }

        private static string LiveCount(LogicOwner owner, int index) => LogicPlay.FiredCount(owner, index) is int n && n > 0 ? $"fired {n}×" : null;

        private static void PasteRule(LogicOwner owner)
        {
            string text = UnityEngine.GUIUtility.systemCopyBuffer;
            JToken t;
            try { t = JToken.Parse(text ?? ""); }
            catch { Notify.Show("The clipboard has no rule (copy one with Copy)", NotifyKind.Info, 2.5f, "logic"); return; }
            var list = t is JArray a ? a.OfType<JObject>().ToList() : t is JObject o ? [o] : [];
            list = list.Where(r => r["when"] != null || r["preset"] != null).ToList();
            if (list.Count == 0) { Notify.Show("The clipboard has no rule", NotifyKind.Info, 2f, "logic"); return; }
            owner.Edit(list.Count == 1 ? "Paste rule" : $"Paste {list.Count} rules", n => { var rules = LogicDoc.EnsureRules(n); foreach (var r in list) rules.Add(r.DeepClone()); });
        }

        // ---- Wires -------------------------------------------------------------------------------------------------

        private static void WireList(ModPage page, LogicOwner owner)
        {
            var logic = LogicOwner.LevelLogic();
            var all = Wires.Scan(logic);
            var mine = all.Where(w => (w.FromKind == owner.Kind && w.FromId == owner.Id) || (w.ToKind == owner.Kind && w.ToId == owner.Id)).ToList();
            if (mine.Count == 0) return;
            StudioDock.SmallHeader(page, "Wires");
            foreach (var w in mine)
            {
                var wire = w;
                bool outgoing = w.FromKind == owner.Kind && w.FromId == owner.Id;
                string other = outgoing ? RuleText.PlaceName(w.ToName) : LogicOwner.FromId(w.FromId)?.Title ?? w.FromId;
                string text = outgoing ? $"{w.Event} → {w.Signal} {other}" : $"{other} {w.Event} → {w.Signal}";
                LogicFields.Action(page, Esc(text), () => WireTool.Confirm(wire), "Click to remove it (both rules).", "×");
            }
        }

        // ---- The level ---------------------------------------------------------------------------------------------

        private static void Level(ModPage page)
        {
            var level = LogicOwner.Level();
            StudioDock.SmallHeader(page, "This level");
            Variables(page, level, "Level variables", "Level-wide values every rule can read ($name) and HUD lines can show.");
            Rules(page, level, "Level script");
            GameRules(page);
            KingActions(page);
            Hud(page);
            Templates(page);
            Caps(page);
        }

        // ---- Game rules (logic.game) -------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> GameRuleLabels = new()
        {
            ["victory"] = "Win by", ["defeat"] = "Lose by", ["castle"] = "Castle", ["holdNight"] = "Night lasts while",
            ["endNight"] = "Night ends when", ["survivors"] = "Enemies left then", ["combat"] = "Fighting when",
            ["build"] = "Building when", ["dayLength"] = "Day length (s)", ["spawnedDead"] = "Dead spawned troops",
        };

        /// <summary>The game's own rules: win and lose, the castle, night flow, when fighting and building happen.</summary>
        private static void GameRules(ModPage page)
        {
            StudioDock.SmallHeader(page, "Game rules");
            var set = LogicOwner.LevelLogic()?["game"] as JObject;
            if (set == null || !set.HasValues) page.Text(Dim("Vanilla: survive every night; the castle falling loses."));
            var scope = LogicOwner.Level().Scope(null);
            foreach (var spec in Model.Logic.LogicSchema.GameRules)
            {
                string name = spec.Name;
                string label = GameRuleLabels.TryGetValue(name, out var l) ? l : LogicCatalog.Humanize(name);
                var ui = new ParamUi
                {
                    Name = name, Label = label, Desc = spec.Description, Default = spec.Default?.ToString(Newtonsoft.Json.Formatting.None).Trim('"'),
                    Kind = spec.Type switch { "enum" => ParamKind.Choice, "number" => ParamKind.Number, _ => ParamKind.Condition },
                    Options = spec.Options ?? [],
                };
                LogicFields.Param(page, label, ui, () => (LogicOwner.LevelLogic()?["game"] as JObject)?[name],
                    (v, what) => LogicOwner.Level().Edit(what ?? $"Set {label}", n =>
                    {
                        if (n["game"] is not JObject g) n["game"] = g = new JObject();
                        if (v == null || v.Type == JTokenType.Null || (v.Type == JTokenType.String && (string)v == "")) g.Remove(name); else g[name] = v;
                        if (!g.HasValues) n.Remove("game");
                    }, "game:" + name), scope);
            }
        }

        /// <summary>The pack's things (non-unit templates) and statuses: each opens here (or in the Entity Studio).</summary>
        private static void Templates(ModPage page)
        {
            var pack = LogicOwner.DraftPack;
            if (pack == null) return;
            StudioDock.SmallHeader(page, "Statuses");
            if (pack.Statuses == null || pack.Statuses.Count == 0) page.Text(Dim("None. Buffs and debuffs: apply them with rules."));
            foreach (var s in pack.Statuses ?? [])
            {
                if (s?.Id == null) continue;
                string id = s.Id;
                StudioDock.DetailRow(page, Esc(s.Name ?? id), Esc(StatusPanel.Describe(LogicDoc.StatusNode(pack, id))), false, null, () => LogicStudio.FocusOn(LogicOwner.Status(id)), () => "Click to edit it.");
            }
            page.TextInput("+ status", () => "", v =>
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                var id = StatusPanel.New(v.Trim());
                if (id != null) LogicStudio.FocusOn(LogicOwner.Status(id));
            }, () => "A timed modifier with its own rules. Its name (Burning, Warded…) also makes the id rules use (burning).", 40, () => Dim("name…"));
            StudioDock.SmallHeader(page, "Things");
            if (pack.Entities == null || pack.Entities.Count == 0) page.Text(Dim("None. Projectiles, props and pickups to place, spawn or launch."));
            foreach (var e in pack.Entities ?? [])
            {
                if (e?.Id == null) continue;
                string id = e.Id;
                StudioDock.DetailRow(page, Esc(e.Name ?? id), Esc($"{e.Kind ?? "prop"} · {id}"), false, null, () => LogicStudio.EditThing(id), () => "Click to edit it.");
            }
            LogicFields.Buttons(page, ("+ thing", () => LogicStudio.NewThing(null), "A projectile, prop, pickup or logic object template."));
        }

        private static void KingActions(ModPage page)
        {
            StudioDock.SmallHeader(page, "King actions");
            var list = LogicOwner.LevelLogic()?["kingActions"] as JArray;
            if (list == null || list.Count == 0) page.Text(Dim("None (up to 3 keys)."));
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                int index = i;
                var a = list[i] as JObject;
                string id = (string)a?["id"] ?? "?";
                page.TextInput($"Key {i + 1}", () => (string)((LogicOwner.LevelLogic()?["kingActions"] as JArray)?[index] as JObject)?["name"] ?? "", v =>
                    EditKing(index, "Rename king action", k => k["name"] = v ?? ""), () => $"Its name on the HUD. Rules hear it as \"the king uses {id}\".", 40);
                string[] whens = ["always", "day", "night"];
                page.Choice("   Usable", ["Always", "By day", "At night"], () => Math.Max(0, Array.IndexOf(whens, (string)a?["when"] ?? "always")),
                    w => EditKing(index, "Set usable", k => { if (w == 0) k.Remove("when"); else k["when"] = whens[w]; }));
                page.Number("   Cooldown (s)", () => (float?)(((LogicOwner.LevelLogic()?["kingActions"] as JArray)?[index] as JObject)?["cooldown"]) ?? 0f,
                    v => EditKing(index, "Set cooldown", k => { if (v <= 0) k.Remove("cooldown"); else k["cooldown"] = Round(v); }), 0f, 600f, 1f);
                LogicFields.Buttons(page,
                    ("+ rule for it", () => LogicStudio.NewRule(LogicOwner.Level(), new JObject { ["on"] = "kingAction", ["action"] = id }), "A level rule that runs when it's pressed"),
                    ("Remove", () => LogicOwner.Level().Edit("Remove king action", n => (n["kingActions"] as JArray)?.RemoveAt(index)), null));
            }
            if ((list?.Count ?? 0) < 3)
                LogicFields.Action(page, "+ king action", () => LogicOwner.Level().Edit("Add king action", n =>
                {
                    if (n["kingActions"] is not JArray arr) n["kingActions"] = arr = new JArray();
                    var taken = new HashSet<string>(arr.OfType<JObject>().Select(o => (string)o["id"]).Where(x => x != null));
                    string id = LogicDoc.NameFor("action", taken);
                    arr.Add(new JObject { ["id"] = id, ["name"] = "Action " + (arr.Count + 1), ["when"] = "day" });
                }), "A key the player presses in this level (rebindable in Controls).");
        }

        private static void EditKing(int index, string label, Action<JObject> change) => LogicOwner.Level().Edit(label, n =>
        {
            if ((n["kingActions"] as JArray)?[index] is JObject k) change(k);
        }, "king:" + index + label);

        private static void Hud(ModPage page)
        {
            StudioDock.SmallHeader(page, "HUD lines");
            var list = LogicOwner.LevelLogic()?["hud"] as JArray;
            if (list == null || list.Count == 0) page.Text(Dim("None."));
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                int index = i;
                var h = list[i] as JObject;
                var scope = LogicOwner.Level().Scope(null);
                var tp = new ParamUi { Name = "text", Label = "Text", Kind = ParamKind.Text, Desc = "{…} shows a formula's value: Souls {$souls}/10." };
                LogicFields.Param(page, (string)h?["id"] ?? $"Line {i + 1}", tp, () => ((LogicOwner.LevelLogic()?["hud"] as JArray)?[index] as JObject)?["text"],
                    (v, l) => EditHud(index, "Set HUD text", x => x["text"] = v ?? ""), scope);
                var vp = new ParamUi { Name = "visible", Label = "Shown if", Kind = ParamKind.Condition, Desc = "Empty: always." };
                LogicFields.Param(page, "   Shown if", vp, () => ((LogicOwner.LevelLogic()?["hud"] as JArray)?[index] as JObject)?["visible"],
                    (v, l) => EditHud(index, "Set HUD condition", x => { if (v == null) x.Remove("visible"); else x["visible"] = v; }), scope);
                LogicFields.Action(page, "   Remove", () => LogicOwner.Level().Edit("Remove HUD line", n => (n["hud"] as JArray)?.RemoveAt(index)));
            }
            LogicFields.Buttons(page,
                ("+ HUD line", () => LogicOwner.Level().Edit("Add HUD line", n => AddHud(n, null)), null),
                ("+ counter", () => LogicOwner.Level().Edit("Add counter", n =>
                {
                    if (n["vars"] is not JObject vars) n["vars"] = vars = new JObject();
                    string name = LogicDoc.NameFor("count", new HashSet<string>(vars.Properties().Select(p => p.Name)));
                    vars[name] = 0;
                    AddHud(n, $"{LogicCatalog.Humanize(name)}: {{${name}}}");
                }), "A level variable and a HUD line showing it"));
        }

        private static void AddHud(JObject n, string text)
        {
            if (n["hud"] is not JArray arr) n["hud"] = arr = new JArray();
            var taken = new HashSet<string>(arr.OfType<JObject>().Select(o => (string)o["id"]).Where(x => x != null));
            arr.Add(new JObject { ["id"] = LogicDoc.NameFor("line", taken), ["text"] = text ?? "Text" });
        }

        private static void EditHud(int index, string label, Action<JObject> change) => LogicOwner.Level().Edit(label, n =>
        {
            if ((n["hud"] as JArray)?[index] is JObject h) change(h);
        });

        // ---- Limits ------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, (string Label, string Tip)> CapText = new()
        {
            ["entities"] = ("Things alive", "Spawned things at once (units, props, pickups)."),
            ["units"] = ("Units alive", "Spawned units at once."),
            ["projectiles"] = ("Shots in flight", "Builder projectiles at once."),
            ["perTemplate"] = ("Copies of one kind", "Live copies of any one template."),
            ["firingsPerFrame"] = ("Rule firings per frame", "More wait for the next frame."),
            ["actionsPerFrame"] = ("Actions per frame", "More wait for the next frame."),
        };

        private static int Default(string key)
        {
            var d = new LogicCaps();
            return key switch
            {
                "entities" => d.Entities, "units" => d.Units, "projectiles" => d.Projectiles, "perTemplate" => d.PerTemplate,
                "firingsPerFrame" => d.FiringsPerFrame, "actionsPerFrame" => d.ActionsPerFrame, _ => 0,
            };
        }

        private static int Max(string key) => key switch
        {
            "entities" => LogicCaps.Max.Entities, "units" => LogicCaps.Max.Units, "projectiles" => LogicCaps.Max.Projectiles, "perTemplate" => LogicCaps.Max.PerTemplate,
            "firingsPerFrame" => LogicCaps.Max.FiringsPerFrame, "actionsPerFrame" => LogicCaps.Max.ActionsPerFrame, _ => 0,
        };

        /// <summary>The level's raised limits ("may lag"), as (key, value, default).</summary>
        public static List<(string Key, int Value, int Default)> Raised(JObject logic) =>
            LogicCaps.Keys.Select(k => (k, (int?)(logic?["caps"]?[k]) ?? Default(k), Default(k))).Where(c => c.Item2 > c.Item3).ToList();

        private static void Caps(ModPage page)
        {
            StudioDock.SmallHeader(page, "Limits");
            var raised = Raised(LogicOwner.LevelLogic());
            if (raised.Count > 0)
            {
                var row = page.Row(LogicFields.Tint("! Raised limits may lag", LogicFields.WarnColor), null,
                    () => "Above the defaults, busy moments can drop the frame rate on slower machines. Test with many things alive.");
                StudioDock.Compact(row, false, 10f, page.Size.x);
            }
            else page.Text(Dim("Defaults keep runaway rules from freezing the game."));
            foreach (var key in LogicCaps.Keys)
            {
                string k = key;
                var (label, tip) = CapText.TryGetValue(k, out var t) ? t : (LogicCatalog.Humanize(k), "");
                int def = Default(k), max = Max(k);
                var field = page.Number(label, () => (int?)(LogicOwner.LevelLogic()?["caps"]?[k]) ?? def, v =>
                    LogicOwner.Level().Edit($"Set limit {label}", n =>
                    {
                        if (n["caps"] is not JObject caps) n["caps"] = caps = new JObject();
                        if (v == def) caps.Remove(k); else caps[k] = v;
                    }, "cap:" + k), Math.Min(def, 1), max, Math.Max(1, def / 10),
                    tooltip: () => $"{tip}\nDefault {def}; above it may lag (up to {max}).", onClear: () => LogicOwner.Level().Edit($"Reset limit {label}", n => (n["caps"] as JObject)?.Remove(k)));
                if (((int?)(LogicOwner.LevelLogic()?["caps"]?[k]) ?? def) > def) field.SetMarker(ModFieldMarker.Warning);
            }
        }
    }
}
