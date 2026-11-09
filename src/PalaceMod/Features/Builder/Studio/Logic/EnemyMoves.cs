using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Studio.Enemies;

/*
    The Enemy Studio's Moves tab (docs/builder-engine.md, "targeting", "movement", "attacks"): who the enemy goes for (an
    ordered list: the first that matches wins), how it moves (path-finding, straight, holding, keeping its distance,
    kiting, fleeing), and extra attacks (a cooldown, a range, and what it launches). Empty sections keep the base's own.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class EnemyMoves
    {
        private static LogicOwner Owner()
        {
            var u = EnemyStudio.Unit;
            return u == null ? null : LogicOwner.Unit(u.Id, string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name);
        }

        private static JObject Node() => Owner()?.Read() ?? new JObject();

        public static void Build(ModPage page)
        {
            var owner = Owner();
            if (owner == null) return;
            page.FieldIndent = Math.Min(page.FieldIndent, 190f);
            Targeting(page, owner);
            Movement(page, owner);
            Attacks(page, owner);
            StudioDock.SmallHeader(page, "The king and attached things");
            PartsPanel.UnitExtras(page, owner, OpenTemplatePicker);
        }

        // ---- Targeting ----------------------------------------------------------------------------------------------

        private static readonly string[] Prefers = ["nearest", "farthest", "weakest", "strongest", "random", "best"];

        private static void Targeting(ModPage page, LogicOwner owner)
        {
            StudioDock.SmallHeader(page, "Goes for");
            var list = Node()["targeting"] as JArray;
            if (list == null || list.Count == 0) page.Text(LogicFields.Dim("The base's own targets."));
            var scope = owner.Scope(null);
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                int index = i;
                JToken Entry() => (Node()["targeting"] as JArray)?[index];
                void Set(string field, JToken v, string label) => owner.Edit(label, n =>
                {
                    var arr = (JArray)n["targeting"];
                    if (arr[index] is not JObject o) arr[index] = o = new JObject { ["filter"] = (string)arr[index] };
                    if (v == null) o.Remove(field); else o[field] = v;
                    if (o.Properties().All(p => p.Name == "filter")) arr[index] = o["filter"]?.DeepClone();
                }, $"tgt:{index}:{field}");
                var fp = new ParamUi { Name = "filter", Label = "Filter", Kind = ParamKind.Filter, Required = true, Desc = "What it goes for: foe and Tower, king, foe and Building…" };
                LogicFields.Param(page, $"{i + 1}. Who", fp, () => Entry() is JObject o ? o["filter"] : Entry(), (v, l) => Set("filter", v, l), scope);
                var rp = new ParamUi { Name = "range", Label = "Range", Kind = ParamKind.Number, Desc = "Only within this many metres (empty: any)." };
                LogicFields.Param(page, "   Within (m)", rp, () => (Entry() as JObject)?["range"], (v, l) => Set("range", v, l), scope);
                page.Choice("   Prefers", Prefers.Select(LogicCatalog.Humanize).ToList(), () => Math.Max(0, Array.IndexOf(Prefers, (string)(Entry() as JObject)?["prefer"] ?? "nearest")),
                    p => Set("prefer", p == 0 ? null : new JValue(Prefers[p]), "Set preference"), tooltip: () => "Which match it picks. Best: the highest score formula.");
                if ((string)(Entry() as JObject)?["prefer"] == "best")
                {
                    var sp = new ParamUi { Name = "score", Label = "Score", Kind = ParamKind.Expr, Desc = "Higher wins; `it` is the candidate (-it.hp: the weakest)." };
                    LogicFields.Param(page, "   Score", sp, () => (Entry() as JObject)?["score"], (v, l) => Set("score", v, l), new ExprScope { HasIt = true });
                }
                LogicFields.Buttons(page,
                    ("Up", index > 0 ? () => owner.Edit("Reorder targets", n => Swap((JArray)n["targeting"], index, index - 1)) : null, "Checked earlier"),
                    ("Remove", () => owner.Edit("Remove target", n => ((JArray)n["targeting"]).RemoveAt(index)), null));
            }
            LogicFields.Action(page, "+ target", () => owner.Edit("Add target", n =>
            {
                if (n["targeting"] is not JArray arr) n["targeting"] = arr = new JArray();
                arr.Add(arr.Count == 0 ? "foe and Building" : "foe");
            }), "The first rule that finds something wins; later ones are fallbacks.");
        }

        private static void Swap(JArray a, int i, int j)
        {
            var t = a[i];
            a[i] = a[j].DeepClone();
            a[j] = t.DeepClone();
        }

        // ---- Movement -----------------------------------------------------------------------------------------------

        private static readonly string[] Modes = [null, "pathfind", "direct", "hold", "path", "follow"];
        private static readonly string[] Thens = ["pathfind", "hold", "destroy"];

        private static void Movement(ModPage page, LogicOwner owner)
        {
            StudioDock.SmallHeader(page, "Moves");
            void Set(string path, JToken v, string label) => owner.Edit(label, n =>
            {
                if (n["movement"] is not JObject m) n["movement"] = m = new JObject();
                var parts = path.Split('.');
                JObject at = m;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (at[parts[i]] is not JObject next) at[parts[i]] = next = new JObject();
                    at = next;
                }
                if (v == null) at.Remove(parts[^1]); else at[parts[^1]] = v;
                foreach (var p in m.Properties().ToList()) if (LogicDoc.IsEmpty(p.Value)) p.Remove();
            }, "move:" + path);
            JToken Get(string path) => Node()["movement"]?.SelectToken(path);
            string mode = (string)Get("mode");
            page.Choice("How", ["The base's own", "Finds a path", "Straight line", "Holds still", "Follows a path", "Walks with a leader"], () => Math.Max(0, Array.IndexOf(Modes, mode)),
                i => Set("mode", Modes[i] == null ? null : new JValue(Modes[i]), "Set movement"),
                tooltip: () => "Straight line suits flyers and ghosts; a path follows a placed path (@name); a leader: walks in formation behind or beside another (a chain, an escort).");
            if (mode == "follow")
            {
                var lp = new ParamUi { Name = "leader", Label = "Leader", Kind = ParamKind.Entity, Required = true, Desc = "Who it walks with: self.parent (what it's attached to), self.$leader, nearest('#captain')." };
                LogicFields.Param(page, "   Leader", lp, () => Get("leader"), (v, l) => Set("leader", v, l), owner.Scope(null));
                Number(page, "   Behind by (m)", "spacing", "Trails this far behind its leader (default 2).", Get, Set, owner.Scope(null));
                void SetOffset(int ax, JToken v, string l) => owner.Edit(l, n =>
                {
                    if (n["movement"] is not JObject m) n["movement"] = m = new JObject();
                    var arr = m["offset"] as JArray ?? new JArray(0, 0);
                    while (arr.Count < 2) arr.Add(0);
                    arr[ax] = v ?? 0;
                    if (arr.All(t => t.Type is JTokenType.Integer or JTokenType.Float && (double)t == 0)) m.Remove("offset"); else m["offset"] = arr;
                }, "move:offset" + ax);
                Number(page, "   Slot right (m)", "offset[0]", "A fixed slot instead of a trail: metres to the leader's right…", Get, (_, v, l) => SetOffset(0, v, l), owner.Scope(null));
                Number(page, "   Slot ahead (m)", "offset[1]", "…and ahead of it (negative: behind).", Get, (_, v, l) => SetOffset(1, v, l), owner.Scope(null));
                Number(page, "   Catches up ×", "catchUp", "Speed multiplier while it's left behind (default 1.5).", Get, Set, owner.Scope(null));
                page.Choice("   Without a leader", ["Goes its own way", "Holds still", "Disappears"], () => Math.Max(0, Array.IndexOf(Thens, (string)Get("then") ?? "pathfind")),
                    i => Set("then", i == 0 ? null : new JValue(Thens[i]), "Set leaderless"), tooltip: () => "When its leader is gone or dead: it leads itself, stays, or is removed.");
            }
            if (mode == "path")
            {
                var pp = new ParamUi { Name = "path", Label = "Path", Kind = ParamKind.Entity, Desc = "A placed path: @name." };
                LogicFields.Param(page, "   Path", pp, () => Get("path"), (v, l) => Set("path", v, l), owner.Scope(null));
            }
            var scope = owner.Scope(null);
            Number(page, "Speed (m/s)", "speed", "Walking speed (empty: the base's).", Get, Set, scope);
            Number(page, "Keeps away (m)", "keepDistance", "Stands this far from its target (ranged units).", Get, Set, scope);
            Number(page, "Backs off under (m)", "kite.below", "If a foe gets this close, it steps away…", Get, Set, scope);
            Number(page, "   for (s)", "kite.for", "…for this long, then re-engages.", Get, Set, scope);
            Number(page, "Wanders (m)", "wander.radius", "Drifts around home when it has nothing to do.", Get, Set, scope);
            var fl = new ParamUi { Name = "when", Label = "Flees when", Kind = ParamKind.Condition, Desc = "A formula: self.hpPct < 0.25. It retreats while it holds." };
            LogicFields.Param(page, "Flees when", fl, () => Get("flee.when"), (v, l) => Set("flee.when", v, l), scope);
        }

        private static void Number(ModPage page, string label, string path, string desc, Func<string, JToken> get, Action<string, JToken, string> set, ExprScope scope)
        {
            var p = new ParamUi { Name = path.Split('.').Last(), Label = label, Kind = ParamKind.Number, Desc = desc };
            LogicFields.Param(page, label, p, () => get(path), (v, l) => set(path, v, l), scope);
        }

        // ---- Attacks ------------------------------------------------------------------------------------------------

        private static readonly string[] Whens = ["night", "always", "day"];

        private static void Attacks(ModPage page, LogicOwner owner)
        {
            StudioDock.SmallHeader(page, "Extra attacks");
            var list = Node()["attacks"] as JArray;
            if (list == null || list.Count == 0) page.Text(LogicFields.Dim("Only the base's attack."));
            var scope = owner.Scope(null);
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                int index = i;
                JObject A() => (Node()["attacks"] as JArray)?[index] as JObject;
                void Set(string field, JToken v, string label) => owner.Edit(label, n =>
                {
                    if ((n["attacks"] as JArray)?[index] is not JObject a) return;
                    if (v == null) a.Remove(field); else a[field] = v;
                }, $"atk:{index}:{field}");
                page.Text(LogicFields.Tint($"Attack {i + 1}", LogicFields.ThenColor));
                Number(page, "   Every (s)", "cooldown", "Seconds between attacks.", f => A()?[f], (f, v, l) => Set(f, v, l), scope);
                Number(page, "   Range (m)", "range", "How far it reaches.", f => A()?[f], (f, v, l) => Set(f, v, l), scope);
                page.Choice("   When", ["At night", "Always", "By day"], () => Math.Max(0, Array.IndexOf(Whens, (string)A()?["when"] ?? "night")),
                    w => Set("when", w == 0 ? null : new JValue(Whens[w]), "Set when"));
                var tp = new ParamUi { Name = "targeting", Label = "At", Kind = ParamKind.Filter, Desc = "What it attacks (a filter)." };
                LogicFields.Param(page, "   At", tp, () => (A()?["targeting"] as JArray)?.FirstOrDefault(), (v, l) => Set("targeting", v == null ? null : new JArray(v), l), scope);
                var lp = new ParamUi { Name = "entity", Label = "Launches", Kind = ParamKind.Template, Desc = "A projectile (or anything) it throws." };
                LogicFields.Param(page, "   Launches", lp, () => A()?["launch"]?["entity"], (v, l) => Set("launch", v == null ? null : new JObject { ["entity"] = v }, l), scope,
                    () => OpenTemplatePicker(t => Set("launch", t == null ? null : new JObject { ["entity"] = t }, "Set launch")));
                ThingButtons(page, owner, index, (string)A()?["launch"]?["entity"]);
                page.Toggle("   Replaces the base's", () => A()?["replace"]?.Type == JTokenType.Boolean && (bool)A()["replace"],
                    v => Set("replace", v ? new JValue(true) : null, "Set replace"), () => "On: the base's own attack goes.");
                LogicFields.Action(page, "   Remove", () => owner.Edit("Remove attack", n => ((JArray)n["attacks"]).RemoveAt(index)));
            }
            LogicFields.Action(page, "+ attack", () => owner.Edit("Add attack", n =>
            {
                if (n["attacks"] is not JArray arr) n["attacks"] = arr = new JArray();
                arr.Add(new JObject { ["cooldown"] = 3, ["range"] = 12, ["targeting"] = new JArray("foe") });
            }), "Throws something every few seconds (a projectile from the pack's things).");
        }

        /// <summary>Under "Launches": pick one of the draft's things, edit the one it launches, or design a new
        /// projectile, in the Entity Studio (Back returns here).</summary>
        private static void ThingButtons(ModPage page, LogicOwner owner, int index, string launched)
        {
            var draft = EnemyStudio.Draft;
            if (draft == null) return;
            string unit = EnemyStudio.UnitId;
            bool isThing = launched != null && draft.Pack.FindEntity(launched) != null;
            void SetLaunch(string id) => owner.Edit("Launch " + id, n =>
            {
                if ((n["attacks"] as JArray)?[index] is not JObject a) return;
                if (a["launch"] is not JObject l) a["launch"] = l = new JObject();
                l["entity"] = id;
            });
            LogicFields.Buttons(page,
                ("Pick a thing", () => LogicStudio.OpenSide("Launches…", p =>
                {
                    var things = Entities.ThingKinds.Launchable(draft.Pack);
                    if (things.Count == 0) p.Text(LogicFields.Dim("No things yet: New projectile designs one."));
                    foreach (var t in things)
                    {
                        string id = t.Id;
                        StudioDock.DetailRow(p, LogicFields.Esc(t.Name ?? t.Id), LogicFields.Esc(Entities.ThingKinds.Label(Entities.ThingKinds.Of(t)) + " · " + t.Id), id == launched, null,
                            () => { RuleEditor.Close(); SetLaunch(id); });
                    }
                }), "One of this pack's things (projectiles first)."),
                ("Edit…", isThing ? () => { RuleEditor.Close(); Entities.EntityStudio.Enter(launched, unit); } : null, "Opens it in the Entity Studio; Back returns here."),
                ("New projectile…", () =>
                {
                    RuleEditor.Close();
                    string id = Entities.EntityStudio.Create(draft, "projectile");
                    if (id == null) return;
                    SetLaunch(id);
                    Entities.EntityStudio.Enter(id, unit);
                }, "Designs a new projectile in the Entity Studio and launches it from this attack."));
        }

        private static void OpenTemplatePicker(Action<JToken> set)
        {
            var (min, max) = EnemyDock.BesideInspector(RuleEditor.Width);
            RuleEditor.OpenStandalone(EnemyDock.Dock, min, max, "Launches…", page => Pickers.Template(page, v => { set(v); }));
        }
    }
}
