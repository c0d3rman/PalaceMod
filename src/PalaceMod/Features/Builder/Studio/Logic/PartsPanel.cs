using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Library;
using PalaceMod.Builder.Model;

/*
    "Parts": the components a thing is made of (docs/builder-engine.md, "Components"), added, edited and removed by
    mouse. One table drives it: each part is a JSON block (visual, body, projectile, mover, area, pickup, interact) with
    its fields as typed params, plus the list-shaped bits (mover poses, interact choices, starting statuses) and the
    template's own settings (kind, team, lifetime). Shown in the Behaviour tab for placed things and in the Entity
    Studio for templates.

    A place made from a template shows the template's parts; editing one copies it onto the place first (the engine
    replaces a component as a whole, so the copy keeps the rest of it), and the row says so.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class PartsPanel
    {
        private static string Esc(string s) => LogicFields.Esc(s);
        private static string Dim(string s) => LogicFields.Dim(s);

        internal sealed class FieldSpec
        {
            /// <summary>Path inside the part ("speed", "shape.circle.radius", "spin.1").</summary>
            public string Path;
            public ParamUi P;
            /// <summary>Shown only when this holds for the part's JSON (null: always).</summary>
            public Func<JObject, bool> When;
            public Action<ModPage, LogicOwner, PartSpec> Custom;
        }

        internal sealed class PartSpec
        {
            public string Key, Label, Desc;
            /// <summary>The block a new part starts as.</summary>
            public Func<JObject> Fresh;
            public List<FieldSpec> Fields = [];
        }

        private static ParamUi N(string name, string label, string desc, string def = null) => new() { Name = name, Label = label, Kind = ParamKind.Number, Desc = desc, Default = def };
        private static ParamUi B(string name, string label, string desc, bool def = false) => new() { Name = name, Label = label, Kind = ParamKind.Bool, Desc = desc, Default = def ? "true" : "false" };
        private static ParamUi T(string name, string label, string desc, ParamKind kind = ParamKind.Text, string def = null) => new() { Name = name, Label = label, Kind = kind, Desc = desc, Default = def };
        private static ParamUi C(string name, string label, string desc, string def, params string[] options) => new() { Name = name, Label = label, Kind = ParamKind.Choice, Desc = desc, Default = def, Options = options };

        private static FieldSpec F(string path, ParamUi p, Func<JObject, bool> when = null) => new() { Path = path, P = p, When = when };

        public static readonly List<PartSpec> Parts =
        [
            new()
            {
                Key = "visual", Label = "Look", Desc = "What it looks like: any scenery mesh, any unit's model or an imported file.",
                Fresh = () => new JObject { ["mesh"] = "mesh:Graveyard Wall Post" },
                Fields =
                [
                    new() { Path = "mesh", Custom = MeshRow },
                    F("meshScale", N("meshScale", "Scale", "The model's own scale (1 = as the map has it).", "1")),
                    F("meshRotY", N("meshRotY", "Turn (°)", "Turns the model around the vertical axis.", "0")),
                    F("meshOffset.1", N("y", "Raise (m)", "Moves the model up or down.", "0")),
                    F("tint", T("tint", "Tint", "Multiplies its colour, #rrggbb.")),
                ],
            },
            new()
            {
                Key = "body", Label = "Body", Desc = "Its footprint: blocks walkers (solid), can be walked on (walkable), can be hit (hittable).",
                Fresh = () => new JObject { ["radius"] = 1, ["height"] = 2 },
                Fields =
                [
                    new() { Path = "box", Custom = BodyShape },
                    F("radius", N("radius", "Radius (m)", "The circle footprint's radius.", "1"), b => b["box"] == null),
                    F("box.0", N("w", "Width (m)", "The box footprint's width (turns with it).", "4"), b => b["box"] != null),
                    F("box.1", N("d", "Depth (m)", "The box footprint's depth.", "1"), b => b["box"] != null),
                    F("height", N("height", "Height (m)", "How tall it is.", "2")),
                    F("solid", B("solid", "Solid", "Ground units can't walk through it (flyers can).")),
                    F("walkable", B("walkable", "Walkable top", "Ground units walk on its top (a bridge, a raft).")),
                    F("hittable", B("hittable", "Hittable", "Has hit points; can be targeted and damaged.")),
                    F("hp", N("hp", "Hit points", "Its health when hittable.", "100"), b => b["hittable"]?.Type == JTokenType.Boolean && (bool)b["hittable"]),
                    F("flying", B("flying", "Counts as flying", "Targeting treats it as air."), b => b["hittable"]?.Type == JTokenType.Boolean && (bool)b["hittable"]),
                    F("tags", T("tags", "Game tags", "Tags vanilla enemies target by (e.g. Building), separated by commas.", ParamKind.Labels), b => b["hittable"]?.Type == JTokenType.Boolean && (bool)b["hittable"]),
                ],
            },
            new()
            {
                Key = "projectile", Label = "Flight", Desc = "It flies: launched by an attack or a launch action, it strikes what it hits (\"impact\").",
                Fresh = () => new JObject { ["speed"] = 12, ["hits"] = "foe" },
                Fields =
                [
                    F("speed", N("speed", "Speed (m/s)", "How fast it flies.", "12")),
                    F("accel", N("accel", "Acceleration", "m/s² added each second.", "0")),
                    F("homing", N("homing", "Homing", "0 flies at where the target was; 1 always hits; between: how hard it turns.", "0")),
                    F("arc", N("arc", "Arc", "Height of the arc as a share of the distance.", "0")),
                    F("gravity", N("gravity", "Gravity", "m/s² for a ballistic shot to a point (replaces the arc).", "0")),
                    F("spin.0", N("x", "Spin X (°/s)", "Tumbles end over end (visual only).", "0")),
                    F("spin.1", N("y", "Spin Y (°/s)", "Turns like a top (visual only).", "0")),
                    F("spin.2", N("z", "Spin Z (°/s)", "Rolls sideways (visual only).", "0")),
                    F("face", C("face", "Faces", "Which way the model points in flight.", "travel", "travel", "fixed", "spin")),
                    F("hits", T("hits", "Hits", "What it can strike (a filter).", ParamKind.Filter, "foe")),
                    F("hitRadius", N("hitRadius", "Hit radius (m)", "How close counts as a hit.", "1")),
                    F("pierce", N("pierce", "Pierces", "Extra targets it passes through (-1: all).", "0")),
                    F("onGround", C("onGround", "On the ground", "What it does when it lands.", "impact", "impact", "stick", "bounce", "pass")),
                    F("range", N("range", "Range (m)", "Distance flown before it runs out (\"expire\").", "40")),
                ],
            },
            new()
            {
                Key = "mover", Label = "Mover", Desc = "It moves: along a path, spinning, swinging, or between named poses (signal a pose's name).",
                Fresh = () => new JObject { ["speed"] = 3 },
                Fields =
                [
                    new() { Path = "path", Custom = MoverPath },
                    F("mode", C("mode", "Mode", "How it travels its path.", "loop", "loop", "pingpong", "once"), m => m["path"] != null),
                    F("speed", N("speed", "Speed (m/s)", "Along its path.", "3")),
                    F("wait", N("wait", "Wait (s)", "Pause at each point.", "0"), m => m["path"] != null),
                    F("spin", N("spin", "Spin (°/s)", "Turns around its centre (a rotating blade wall).", "0")),
                    F("swing.angle", N("angle", "Swing (°)", "Swings back and forth this far (a door, a pendulum).", "0")),
                    F("swing.speed", N("speed", "Swing speed (°/s)", "How fast it swings.", "45"), m => m["swing"] != null),
                    F("running", B("running", "Starts moving", "Off: waits for \"start\".", true)),
                    F("turnSpeed", N("turnSpeed", "Pose turn (°/s)", "How fast it turns into a pose.", "90"), m => m["pose"] != null || m["poses"] != null),
                    new() { Path = "pose", Custom = Poses },
                ],
            },
            new()
            {
                Key = "area", Label = "Area", Desc = "A zone that notices what's inside it: enter, exit, inside, empty, occupied.",
                Fresh = () => new JObject { ["shape"] = new JObject { ["circle"] = new JObject { ["radius"] = 4 } }, ["watch"] = "unit" },
                Fields =
                [
                    new() { Path = "shape", Custom = AreaShape },
                    F("shape.circle.radius", N("radius", "Radius (m)", "The circle's radius.", "4"), a => a["shape"]?["circle"] != null),
                    F("shape.rect.size.0", N("w", "Width (m)", "The rectangle's width.", "4"), a => a["shape"]?["rect"] != null),
                    F("shape.rect.size.1", N("d", "Depth (m)", "The rectangle's depth.", "4"), a => a["shape"]?["rect"] != null),
                    F("watch", T("watch", "Watches", "What it notices (a filter).", ParamKind.Filter, "unit")),
                    F("show", C("show", "Shown", "How players see it in the level.", "outline", "outline", "fill", "none")),
                    F("color", T("color", "Colour", "#rrggbb.")),
                    F("tick", N("tick", "Inside every (s)", "Seconds between \"inside\" events (empty: none).", null)),
                    F("surface.name", T("name", "Surface", "Makes its ground a named surface (mud, road, ladder): who may walk it, at what cost.")),
                    F("surface.allow", T("allow", "   Who may walk it", "A filter (empty: everyone).", ParamKind.Filter), a => a["surface"] != null),
                    F("surface.cost", N("cost", "   Path cost", "Above 1: avoided; below 1: preferred.", "1"), a => a["surface"] != null),
                ],
            },
            new()
            {
                Key = "pickup", Label = "Pickup", Desc = "Collected when someone touches it (\"pickup\"), then gone unless kept.",
                Fresh = () => new JObject { ["by"] = "king", ["radius"] = 1.5 },
                Fields =
                [
                    F("by", T("by", "Collected by", "Who picks it up (a filter: king, player and unit).", ParamKind.Filter, "king")),
                    F("radius", N("radius", "Radius (m)", "How close counts.", "1.5")),
                    F("magnet", N("magnet", "Magnet (m)", "Flies to the collector from this far.", "0")),
                    F("consume", B("consume", "Disappears", "Off: it stays; a rule decides.", true)),
                ],
            },
            new()
            {
                Key = "interact", Label = "Interact", Desc = "The king can use it with the interact key, paying its cost: \"is used by the king\".",
                Fresh = () => new JObject { ["prompt"] = "Use", ["when"] = "day" },
                Fields =
                [
                    F("prompt", T("prompt", "Prompt", "What the king sees. {cost} shows the price.")),
                    F("cost", N("cost", "Cost (gold)", "Paid by holding the key, like building.", "0")),
                    F("when", C("when", "Usable", "When the king can use it.", "always", "day", "night", "always")),
                    F("radius", N("radius", "Reach (m)", "How close the king must be.", "3")),
                    F("repeat", B("repeat", "Again and again", "Off: usable once.", true)),
                    new() { Path = "choices", Custom = Choices },
                ],
            },
        ];

        public static PartSpec Part(string key) => Parts.FirstOrDefault(p => p.Key == key);

        // ---- Reading and writing --------------------------------------------------------------------------------------

        /// <summary>A part's JSON as the thing has it (a place's own, else its template's), or null.</summary>
        public static JObject Get(LogicOwner owner, string key) => owner.Effective()?[key] as JObject;

        /// <summary>Whether a place's part comes only from its template.</summary>
        private static bool Inherited(LogicOwner owner, string key) =>
            owner.Kind == OwnerKind.Place && owner.Read()?[key] == null && LogicDoc.TemplateOf(LogicOwner.DraftPack, owner.Read())?[key] != null;

        /// <summary>Edits a part (one undo step). A place gets its template's part copied first.</summary>
        public static void EditPart(LogicOwner owner, string key, string label, Action<JObject> change, string coalesce = null)
        {
            JToken inherited = owner.Kind == OwnerKind.Place && owner.Read()?[key] == null ? LogicDoc.TemplateOf(LogicOwner.DraftPack, owner.Read())?[key]?.DeepClone() : null;
            owner.Edit(label, n =>
            {
                if (n[key] is not JObject part) n[key] = part = inherited as JObject ?? new JObject();
                change(part);
                Tidy(part);
                if (!part.Properties().Any()) n.Remove(key);
            }, coalesce);
        }

        /// <summary>Drops empty objects left behind by removed fields.</summary>
        private static void Tidy(JObject o)
        {
            foreach (var p in o.Properties().ToList())
            {
                if (p.Value is JObject c) { Tidy(c); if (!c.Properties().Any()) p.Remove(); }
                else if (p.Value.Type == JTokenType.Null) p.Remove();
            }
        }

        public static JToken GetPath(JObject part, string path)
        {
            JToken cur = part;
            foreach (var seg in path.Split('.'))
            {
                if (cur == null) return null;
                if (int.TryParse(seg, out int i)) cur = cur is JArray a && i < a.Count ? a[i] : null;
                else if (cur is JObject o) cur = o[seg];
                else if (cur.Type == JTokenType.String && seg == "name") return cur;   // "surface": "mud"
                else return null;
            }
            return cur;
        }

        /// <summary>Sets a path inside a part, making the objects and arrays on the way (null removes the leaf).</summary>
        public static void SetPath(JObject part, string path, JToken value)
        {
            var segs = path.Split('.');
            JToken cur = part;
            for (int k = 0; k < segs.Length - 1; k++)
            {
                string seg = segs[k];
                bool nextIndex = int.TryParse(segs[k + 1], out _);
                if (cur is JObject o)
                {
                    // A shorthand string ("surface": "mud") becomes its object form.
                    if (o[seg] is JValue sv && sv.Type == JTokenType.String && !nextIndex) o[seg] = new JObject { ["name"] = (string)sv };
                    if (o[seg] is not JContainer) { if (value == null) return; o[seg] = nextIndex ? new JArray() : new JObject(); }
                    cur = o[seg];
                }
                else if (cur is JArray a && int.TryParse(seg, out int i))
                {
                    while (a.Count <= i) a.Add(0);
                    if (a[i] is not JContainer) a[i] = nextIndex ? new JArray() : new JObject();
                    cur = a[i];
                }
            }
            string last = segs[segs.Length - 1];
            if (cur is JArray arr && int.TryParse(last, out int idx))
            {
                // Vectors (spin, offsets, sizes): the other axes stay, a cleared axis becomes 0.
                int len = path.StartsWith("box") || path.Contains("size") ? 2 : 3;
                while (arr.Count < Math.Max(len, idx + 1)) arr.Add(0);
                arr[idx] = value ?? 0;
                if (arr.All(t => t.Type is JTokenType.Integer or JTokenType.Float && (double)t == 0) && !path.StartsWith("box") && !path.Contains("size"))
                    (arr.Parent as JProperty)?.Remove();
            }
            else if (cur is JObject obj)
            {
                if (value == null) obj.Remove(last); else obj[last] = value;
            }
        }

        // ---- The panel ------------------------------------------------------------------------------------------------

        /// <summary>The parts section for a place or a template.</summary>
        public static void Build(ModPage page, LogicOwner owner)
        {
            var node = owner.Effective() ?? new JObject();
            string kind = owner.Kind == OwnerKind.Place ? PlaceItems.KindOf(owner.Read()) : (string)node["kind"];
            if (kind is "path" or "point") return;
            if (owner.Kind == OwnerKind.Entity) Template(page, owner, node);
            foreach (var spec in Parts)
                if (node[spec.Key] is JObject) PartBlock(page, owner, spec);
            Statuses(page, owner, node);
            var missing = Parts.Where(s => node[s.Key] is not JObject).ToList();
            StudioDock.SmallHeader(page, "Add a part");
            var buttons = missing.Select(s => ($"+ {s.Label}", (Action)(() => AddPart(owner, s)), s.Desc)).ToList();
            if (node["statuses"] == null) buttons.Add(("+ Status", () => PickStatus(owner), "Starts with a status (a buff or debuff)."));
            if (owner.Kind == OwnerKind.Place && node["lifetime"] == null) buttons.Add(("+ Lifetime", () => owner.Edit("Set lifetime", n => n["lifetime"] = 10), "Disappears after a while (\"runs out\")."));
            for (int i = 0; i < buttons.Count; i += 3) LogicFields.Buttons(page, buttons.Skip(i).Take(3).ToArray());
            if (owner.Kind == OwnerKind.Place && node["lifetime"] != null)
                LogicFields.NumberField(page, "Lifetime (s)", N("lifetime", "Lifetime", "Seconds until it runs out; empty: forever."), () => owner.Read()?["lifetime"],
                    (v, l) => owner.Edit(l, n => { if (v == null) n.Remove("lifetime"); else n["lifetime"] = v; }, "lifetime"), owner.Scope(null), "Seconds until it runs out (\"expire\"), then it's gone. Empty: forever.");
        }

        public static void AddPart(LogicOwner owner, PartSpec spec) => owner.Edit($"Add {spec.Label.ToLowerInvariant()}", n => n[spec.Key] = spec.Fresh());

        public static void RemovePart(LogicOwner owner, PartSpec spec) => owner.Edit($"Remove {spec.Label.ToLowerInvariant()}", n => n.Remove(spec.Key));

        private static void PartBlock(ModPage page, LogicOwner owner, PartSpec spec)
        {
            bool inherited = Inherited(owner, spec.Key);
            StudioDock.SmallHeader(page, spec.Label + (inherited ? Dim("  from " + Esc((string)owner.Read()?["entity"])) : ""));
            var part = Get(owner, spec.Key) ?? new JObject();
            var scope = owner.Scope(null);
            foreach (var f in spec.Fields)
            {
                if (f.When != null && !f.When(part)) continue;
                if (f.Custom != null) { f.Custom(page, owner, spec); continue; }
                var field = f;
                Action open = field.P.Kind == ParamKind.Filter
                    ? () => LogicStudio.OpenSide(field.P.Label + "…", p => RuleEditor.FilterPicker(p, (string)GetPath(Get(owner, spec.Key), field.Path), v => EditPart(owner, spec.Key, $"Set {field.P.Label}", x => SetPath(x, field.Path, v)), false))
                    : null;
                LogicFields.Param(page, field.P.Label, field.P, () => GetPath(Get(owner, spec.Key), field.Path),
                    (v, l) => EditPart(owner, spec.Key, l, x => SetPath(x, field.Path, v), spec.Key + "." + field.Path), scope, open);
            }
            if (!inherited) LogicFields.Action(page, Dim("Remove " + spec.Label.ToLowerInvariant()), () => RemovePart(owner, spec), spec.Desc, "×");
        }

        // ---- Template settings -----------------------------------------------------------------------------------------

        public static readonly string[] Kinds = ["prop", "projectile", "pickup", "area", "logic"];

        private static void Template(ModPage page, LogicOwner owner, JObject node)
        {
            StudioDock.SmallHeader(page, "Thing");
            page.TextInput("Name", () => (string)owner.Read()?["name"] ?? "", v => owner.Edit("Rename", n => { if (string.IsNullOrWhiteSpace(v)) n.Remove("name"); else n["name"] = v.Trim(); }),
                () => "Its name in pickers and the palette (its id stays " + owner.Id + ").", 60);
            page.Choice("Kind", Kinds.Select(LogicCatalog.Humanize).ToList(), () => Math.Max(0, Array.IndexOf(Kinds, (string)owner.Read()?["kind"] ?? "prop")),
                i => owner.Edit("Set kind", n => n["kind"] = Kinds[i]), tooltip: () => "What sort of thing it is. Any kind can have any part; the kind picks the palette and the defaults.");
            string[] teams = ["neutral", "player", "enemy", "owner"];
            page.Choice("Side", ["Neutral", "Player", "Enemy", "Its launcher's"], () => Math.Max(0, Array.IndexOf(teams, (string)owner.Read()?["team"] ?? "neutral")),
                i => owner.Edit("Set side", n => { if (i == 0) n.Remove("team"); else n["team"] = teams[i]; }), tooltip: () => "Whose side it's on (foe and ally in filters).");
            if ((string)node["team"] == "enemy" && node["body"]?["hittable"]?.Type == JTokenType.Boolean && (bool)node["body"]["hittable"])
                page.Toggle("Keeps the night open", () => owner.Read()?["countsAsEnemy"]?.Type == JTokenType.Boolean && (bool)owner.Read()["countsAsEnemy"],
                    v => owner.Edit(v ? "Counts as an enemy" : "Doesn't count", n => { if (v) n["countsAsEnemy"] = true; else n.Remove("countsAsEnemy"); }),
                    () => "While it stands, the night isn't over.");
            var scope = owner.Scope(null);
            LogicFields.NumberField(page, "Lifetime (s)", N("lifetime", "Lifetime", "Seconds until it runs out; empty: forever."), () => owner.Read()?["lifetime"],
                (v, l) => owner.Edit(l, n => { if (v == null) n.Remove("lifetime"); else n["lifetime"] = v; }, "lifetime"), scope, "Seconds until it runs out (\"expire\"), then it's gone. Empty: forever.");
            LogicFields.NumberField(page, "Most alive", N("maxAlive", "Most alive", "Cap on live copies.", "100"), () => owner.Read()?["maxAlive"],
                (v, l) => owner.Edit(l, n => { if (v == null) n.Remove("maxAlive"); else n["maxAlive"] = v; }, "maxAlive"), scope, "At most this many alive at once (more are skipped). Default 100.");
        }

        // ---- Custom rows ------------------------------------------------------------------------------------------------

        private static void MeshRow(ModPage page, LogicOwner owner, PartSpec spec)
        {
            string mesh = (string)Get(owner, "visual")?["mesh"];
            page.Row("Model", mesh == null ? Dim("none") : Esc(mesh), "Click to pick a model.", () => LogicStudio.OpenSide("Model…", p => MeshPicker(p, owner)));
        }

        private static string meshFilter = "";

        public static void MeshPicker(ModPage page, LogicOwner owner)
        {
            page.TextInput("Search", () => meshFilter, v => { meshFilter = v ?? ""; RuleEditor.MarkDirty(); }, () => "Part of a name or category.");
            string f = meshFilter?.Trim() ?? "";
            bool Match(params string[] s) => f.Length == 0 || s.Any(x => x != null && x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            void Pick(string label, string detail, string mesh) => StudioDock.DetailRow(page, Esc(label), Esc(detail), false, null, () =>
            {
                EditPart(owner, "visual", $"Model: {label}", v => v["mesh"] = mesh);
                RuleEditor.ClosePicker();
            });
            StudioDock.SmallHeader(page, "Scenery");
            int n = 0;
            foreach (var e in SceneryLibrary.Available)
            {
                if (!Match(e.Name, e.Category) || n++ > 300) continue;
                Pick(e.Name.Replace('_', ' '), $"{e.Category} · {SceneryCatalog.DisplayMap(e.Home)}", e.Ref);
            }
            StudioDock.SmallHeader(page, "Unit models");
            foreach (var c in BuilderServices.Catalog?.Units ?? [])
                if (c.Prefab != null && Match(c.DisplayName, c.Id)) Pick(c.DisplayName ?? c.Id, c.Id, ModelRef.Unit(c.Id));
        }

        private static void BodyShape(ModPage page, LogicOwner owner, PartSpec spec)
        {
            bool box = Get(owner, "body")?["box"] != null;
            page.Choice("Footprint", ["Circle", "Box"], () => box ? 1 : 0, i => EditPart(owner, "body", "Set footprint", b =>
            {
                if (i == 1) { b["box"] = new JArray(4, 1); b.Remove("radius"); }
                else { b.Remove("box"); b["radius"] = 1; }
            }), tooltip: () => "A circle, or a box that turns with it.");
        }

        private static void AreaShape(ModPage page, LogicOwner owner, PartSpec spec)
        {
            var shape = Get(owner, "area")?["shape"] as JObject;
            int cur = shape?["rect"] != null ? 1 : shape?["polygon"] != null ? 2 : 0;
            page.Choice("Shape", ["Circle", "Rectangle", "Polygon"], () => cur, i => EditPart(owner, "area", "Set shape", a => a["shape"] = i switch
            {
                1 => new JObject { ["rect"] = new JObject { ["size"] = new JArray(6, 4) } },
                2 => new JObject { ["polygon"] = new JObject { ["points"] = new JArray(new JArray(-3, -3), new JArray(3, -3), new JArray(3, 3), new JArray(-3, 3)) } },
                _ => new JObject { ["circle"] = new JObject { ["radius"] = 4 } },
            }), tooltip: () => "Drag its handles on the map to resize it.");
            if (cur == 2 && shape?["polygon"]?["points"] is JArray pts)
            {
                page.Text(Dim($"{pts.Count} corners: drag them on the map; Ctrl-click one to remove it, click a side's middle to add one."));
            }
        }

        private static void MoverPath(ModPage page, LogicOwner owner, PartSpec spec)
        {
            var path = Get(owner, "mover")?["path"];
            string shown = path == null ? Dim("stays put") : path.Type == JTokenType.String ? Esc((string)path) : Dim($"{(path as JArray)?.Count ?? 0} points");
            page.Row("Path", shown, "A placed path it travels along (@name). Click to choose.", () => LogicStudio.OpenSide("Path…", p =>
            {
                StudioDock.DetailRow(p, "Stays put", Dim("no path"), false, null, () => { EditPart(owner, "mover", "No path", m => m.Remove("path")); RuleEditor.ClosePicker(); });
                foreach (var place in LogicDoc.Places(LogicOwner.LevelLogic())?.OfType<JObject>() ?? [])
                {
                    if (PlaceItems.KindOf(place) != "path") continue;
                    string name = LogicDoc.RefName(OwnerKind.Place, (string)place["id"], place);
                    StudioDock.DetailRow(p, "@" + Esc(name), Dim($"{(place["points"] as JArray)?.Count ?? 0} points"), false, null, () =>
                    {
                        EditPart(owner, "mover", "Set path", m => m["path"] = "@" + name);
                        RuleEditor.ClosePicker();
                    });
                }
                if (Studio.Level == null) p.Text(Dim("Paths are placed in a level (Logic palette: Path)."));
            }));
        }

        private static void Poses(ModPage page, LogicOwner owner, PartSpec spec)
        {
            var mover = Get(owner, "mover");
            string key = mover?["poses"] != null && mover["pose"] == null ? "poses" : "pose";
            var poses = mover?[key] as JObject;
            StudioDock.SmallHeader(page, "Poses");
            if (poses == null || !poses.Properties().Any()) page.Text(Dim("None. A pose is a turn or an offset it moves to when it gets a signal of that name."));
            var scope = owner.Scope(null);
            foreach (var prop in poses?.Properties().ToList() ?? [])
            {
                string name = prop.Name;
                LogicFields.Action(page, Esc(name), () => EditPart(owner, "mover", $"Remove pose {name}", m => (m[key] as JObject)?.Remove(name)), $"Signal \"{name}\" moves it here. Click to remove it.", "×");
                LogicFields.NumberField(page, "   Turn (°)", N("rotY", "Turn", "Its turn in this pose."), () => (Get(owner, "mover")?[key] as JObject)?[name]?["rotY"],
                    (v, l) => EditPart(owner, "mover", l, m => SetPath(m, $"{key}.{name}.rotY", v), $"pose.{name}.rotY"), scope, "Its turn in this pose, degrees.");
                string[] axes = ["x", "y", "z"];
                for (int i = 0; i < 3; i++)
                {
                    int ax = i;
                    LogicFields.NumberField(page, $"   Offset {axes[i]} (m)", N(axes[i], "Offset", "Moved from its placed position."), () => (Get(owner, "mover")?[key] as JObject)?[name]?["offset"]?[ax],
                        (v, l) => EditPart(owner, "mover", l, m => SetPath(m, $"{key}.{name}.offset.{ax}", v), $"pose.{name}.offset{ax}"), scope, "Moved from where it's placed, metres (y is up).");
                }
            }
            page.TextInput("+ pose", () => "", v =>
            {
                v = v?.Trim();
                if (string.IsNullOrEmpty(v)) return;
                EditPart(owner, "mover", $"Add pose {v}", m =>
                {
                    if (m[key] is not JObject ps) m[key] = ps = new JObject();
                    ps[v] ??= new JObject { ["rotY"] = 0 };
                });
            }, () => "A pose's name (open, closed, up…): the signal that moves it there.", 40, () => Dim("name…"));
        }

        private static void Choices(ModPage page, LogicOwner owner, PartSpec spec)
        {
            var list = Get(owner, "interact")?["choices"] as JArray;
            StudioDock.SmallHeader(page, "Choices");
            if (list == null || list.Count == 0) page.Text(Dim("None: one prompt. With choices the game's choice popup opens (up to 4)."));
            var scope = owner.Scope(null);
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                int idx = i;
                JToken Ch(string f) => (Get(owner, "interact")?["choices"] as JArray)?[idx]?[f];
                LogicFields.ExprField(page, $"{i + 1}. Label", T("label", "Label", "What the choice says."), () => Ch("label"),
                    (v, l) => EditPart(owner, "interact", l, x => SetPath(x, $"choices.{idx}.label", v)), scope, "What the choice says.");
                LogicFields.ExprField(page, "   Id", T("id", "Id", "Rules see it as event.choice."), () => Ch("id"),
                    (v, l) => EditPart(owner, "interact", l, x => SetPath(x, $"choices.{idx}.id", v)), scope, "The name rules check: event.choice == 'id'.");
                LogicFields.NumberField(page, "   Cost", N("cost", "Cost", "Gold for this choice."), () => Ch("cost"),
                    (v, l) => EditPart(owner, "interact", l, x => SetPath(x, $"choices.{idx}.cost", v)), scope, "Gold paid for this choice (replaces the cost above).");
                LogicFields.Action(page, Dim("   Remove choice"), () => EditPart(owner, "interact", "Remove choice", x => (x["choices"] as JArray)?.RemoveAt(idx)));
            }
            if ((list?.Count ?? 0) < 4)
                LogicFields.Action(page, "+ choice", () => EditPart(owner, "interact", "Add choice", x =>
                {
                    if (x["choices"] is not JArray arr) x["choices"] = arr = new JArray();
                    arr.Add(new JObject { ["id"] = "choice" + (arr.Count + 1), ["label"] = "Choice " + (arr.Count + 1) });
                }), "Up to 4; the picked one's id is event.choice.");
        }

        // ---- Statuses ---------------------------------------------------------------------------------------------------

        private static void Statuses(ModPage page, LogicOwner owner, JObject node)
        {
            if (node["statuses"] is not JArray list || list.Count == 0) return;
            StudioDock.SmallHeader(page, "Starts with");
            foreach (var s in list.Select(t => (string)t).Where(t => t != null).ToList())
            {
                string id = s;
                LogicFields.Action(page, Esc(StatusPanel.NameOf(id)), () => owner.Edit($"Remove status {id}", n =>
                {
                    if (n["statuses"] is not JArray a) n["statuses"] = a = (JArray)node["statuses"].DeepClone();
                    foreach (var t in a.Where(t => (string)t == id).ToList()) t.Remove();
                }), "A status it has from the start. Click × to remove it.", "×");
            }
            LogicFields.Buttons(page, ("+ status", () => PickStatus(owner), "Another starting status."));
        }

        public static void PickStatus(LogicOwner owner) => LogicStudio.OpenSide("Status…", p => StatusPanel.Picker(p, id =>
        {
            var start = owner.Effective()?["statuses"]?.DeepClone() as JArray;
            owner.Edit($"Start with {id}", n =>
            {
                if (n["statuses"] is not JArray a) n["statuses"] = a = start ?? new JArray();
                if (!a.Any(t => (string)t == id)) a.Add(id);
            });
        }));
    }
}
