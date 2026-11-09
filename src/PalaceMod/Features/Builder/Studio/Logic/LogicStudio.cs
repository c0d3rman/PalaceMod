using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Studio.Enemies;
using TMPro;
using UnityEngine;

/*
    The Studio's behaviour editor (docs/builder-engine.md, workstream L3), wired into the Level and Enemy Studios through
    their extension points: the Behaviour tab, the rule editor beside it, the Logic palette, placed logic objects as map
    items, the Wire tool and its overlay, the play-mode Logic panel with rule pips, and the Enemy Studio's Behaviour,
    Targeting and Movement tabs. Dev command: `builder-logic` (see Awake).
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class LogicStudioManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LogicSchemaBridge.Merge();
            var wireKey = Keybinds.Register("Builder", "studio-wire", "Studio: wire", new KeyboardShortcut(KeyCode.J),
                "Wire tool: drag from one thing to another to make one set off the other.", context: StudioContexts.Edit, flags: KeybindFlags.AllowInMenus);
            Studio.AddTool(new WireTool(wireKey));
            Studio.AddOverlay(WireOverlay.Instance);
            Studio.AddItemSource(PlaceItems.Instance);
            Studio.AddPanel(LogicStudio.Tab, StudioMode.Edit, () => true, page => BehaviourPanel.Build(page, LogicStudio.SelectedOwner()));
            Studio.AddInspectorSection("behaviour", 150, keys => keys.Count == 1 && LogicOwner.ForItem(keys[0]) != null, LogicStudio.InspectorSummary);
            Studio.AddPaletteTab("logic", "Logic", LogicStudio.Palette);
            Studio.AddViewToggle("wires", "Wires", () => WireOverlay.ShowAll, v => WireOverlay.ShowAll = v);
            Studio.AddViewToggle("pips", "Rule pips (play)", () => LogicPlay.Pips, v => LogicPlay.Pips = v);
            Studio.AddCancelHandler(RuleEditor.Cancel);
            Studio.AddLabelProvider(LogicStudio.Label);
            Studio.AddContextItems(LogicStudio.ContextItems);
            Studio.ModeChanged += mode =>
            {
                if (mode != StudioMode.Edit) RuleEditor.Close();
                if (mode == StudioMode.Play) LogicPlay.Reset();
                else LogicPlay.HidePips();
            };
            Studio.SelectionChanged += LogicStudio.OnSelection;
            Studio.ItemsChanged += () => RuleEditor.MarkDirty();
            LogicEvents.Traced += LogicPlay.OnTraced;
            EnemyDock.AddTab("behaviour", "Behaviour", "abilities", LogicStudio.EnemyBehaviour);
            EnemyDock.AddTab("moves", "Moves", null, EnemyMoves.Build);
            RuleText.TemplateName = LogicStudio.TemplateName;
            DevCommands.Register("builder-logic", "builder-logic state | owner <key|level> | rules | open <i> | new <event> | act <action> | press <text> | set <path> <json> | place <object> [x z] | wire <from> <event> <to> <input> | unwire <from> <signal> | json | check <expr> | tab | close   drive the Studio's behaviour editor", LogicDev.Run);
        }

        // After every Awake, so the sandbox panel stays the first play tab.
        public override void Start() =>
            Studio.AddPanel("Logic", StudioMode.Play, () => LogicWorld.Current != null || LogicDoc.Logic(Studio.Level) != null, LogicPlay.Build);

        public override void Update()
        {
            try
            {
                if (Studio.Mode == StudioMode.Edit || EnemyStudio.Active) RuleEditor.Tick();
                LogicPlay.Tick();
            }
            catch (Exception e) { Log.Caught("Logic editor tick failed", e); }
        }
    }

    internal static class LogicStudio
    {
        public const string Tab = "Behaviour";
        private const float G = ModDock.Gutter, TopH = 64f, StatusH = 40f, RightW = 340f;

        /// <summary>The rule editor's place in the Level Studio: left of the right panel, same height.</summary>
        public static (Vector2 Min, Vector2 Max) EditorOffsets() =>
            (new Vector2(-G - RightW - G - RuleEditor.Width, G + StatusH + G), new Vector2(-G - RightW - G, -(G + TopH + G)));

        /// <summary>After any logic edit: the panels and the open card follow.</summary>
        public static void Changed()
        {
            RuleEditor.MarkDirty();
            if (EnemyStudio.Active) EnemyDock.RefreshInspector();
            else Studio.RefreshPanels();
        }

        /// <summary>The Behaviour tab's owner: the one selected thing, or the level.</summary>
        public static LogicOwner SelectedOwner()
        {
            var keys = Studio.Selection;
            return keys.Count == 1 ? LogicOwner.ForItem(keys[0]) ?? LogicOwner.Level() : LogicOwner.Level();
        }

        public static void OnSelection()
        {
            // The open card follows the selection: another thing selected closes it.
            if (RuleEditor.IsOpen && !RuleEditor.Owner.IsUnit && RuleEditor.Owner.Kind != OwnerKind.Level && !Studio.Selection.Contains(RuleEditor.Owner.StudioKey))
                RuleEditor.Close();
        }

        public static void OpenRule(LogicOwner owner, int index)
        {
            if (owner == null) return;
            if (owner.IsUnit)
            {
                var (min, max) = EnemyDock.BesideInspector(RuleEditor.Width);
                RuleEditor.Open(owner, index, EnemyDock.Dock, min, max);
                EnemyDock.RefreshInspector();
                return;
            }
            var (a, b) = EditorOffsets();
            RuleEditor.Open(owner, index, StudioDock.Dock, a, b);
            if (StudioDock.PanelTab != "panel:" + Tab) StudioDock.ShowPanel("panel:" + Tab);
            Studio.RefreshPanels();
        }

        /// <summary>Adds a rule and opens it on the event picker (or with a given "when").</summary>
        public static void NewRule(LogicOwner owner, JToken when = null)
        {
            int index = -1;
            owner.Edit("Add rule", n =>
            {
                var rules = LogicDoc.EnsureRules(n);
                var rule = new JObject();
                if (when != null) rule["when"] = when.DeepClone();
                rule["do"] = new JArray();
                rules.Add(rule);
                index = rules.Count - 1;
            });
            if (index < 0) return;
            OpenRule(owner, index);
            if (when == null) RuleEditor.PickEvent();
        }

        public static void PickPreset(LogicOwner owner)
        {
            var list = PresetUi.ForRules().ToList();
            void Build(ModPage page)
            {
                if (list.Count == 0) { page.Text(LogicFields.Dim("No presets yet. \"Save as preset\" on a rule makes one; the Logic palette has ready-made objects.")); return; }
                foreach (var p in list)
                {
                    var preset = p;
                    StudioDock.DetailRow(page, LogicFields.Esc(p.Name), LogicFields.Esc((p.BuiltIn ? "built in · " : "this pack · ") + (p.Describe(null) ?? "")), false, null, () =>
                    {
                        PresetUi.Use(owner, preset);
                        RuleEditor.Close();
                        var count = owner.Rules()?.Count ?? 0;
                        if (count > 0) OpenRule(owner, count - 1);
                    });
                }
            }
            if (owner.IsUnit)
            {
                var (min, max) = EnemyDock.BesideInspector(RuleEditor.Width);
                RuleEditor.OpenStandalone(EnemyDock.Dock, min, max, "Add a preset", Build);
            }
            else
            {
                var (a, b) = EditorOffsets();
                RuleEditor.OpenStandalone(StudioDock.Dock, a, b, "Add a preset", Build);
            }
        }

        // ---- Inspector, labels, context menu ------------------------------------------------------------------------

        public static void InspectorSummary(ModPage page, IReadOnlyList<string> keys)
        {
            var owner = LogicOwner.ForItem(keys[0]);
            if (owner == null) return;
            var node = owner.Read();
            int rules = LogicDoc.Rules(node)?.Count ?? 0;
            string name = LogicDoc.RefName(owner.Kind, owner.Id, node);
            StudioDock.SmallHeader(page, "Behaviour");
            string detail = string.Join(" · ", new[] { name == null ? null : "@" + name, rules == 0 ? "no rules" : rules == 1 ? "1 rule" : $"{rules} rules" }.Where(s => s != null));
            StudioDock.DetailRow(page, rules == 0 ? "Add behaviour" : LogicFields.Esc(RuleText.Summary(LogicDoc.Rules(node)?[0] as JObject, owner.Kind)), LogicFields.Esc(detail), false, null,
                () => { StudioDock.ShowPanel("panel:" + Tab); if (rules == 0) NewRule(owner); },
                () => "Opens the Behaviour tab: what it does, when.");
        }

        public static StudioLabel Label(string key)
        {
            if (key == null || !Studio.Running) return null;
            var owner = key.StartsWith(PlaceItems.Prefix) ? LogicOwner.ForItem(key) : null;
            if (owner == null)
            {
                // Attached rules on the level's own things.
                var item = Studio.Find(key);
                if (item == null || item.Kind == StudioItemKind.Group) return null;
                var node = LogicDoc.Attach(LogicOwner.LevelLogic(), LogicDoc.AttachKey(key, item.IsCastle), false);
                int n = LogicDoc.Rules(node)?.Count ?? 0;
                if (n == 0 && node?["name"] == null) return null;
                return new StudioLabel { Card = (node?["name"] != null ? $"@{(string)node["name"]} · " : "") + (n == 1 ? "1 rule" : $"{n} rules") };
            }
            var place = owner.Read();
            int count = LogicDoc.Rules(place)?.Count ?? 0;
            return new StudioLabel { Name = Studio.Find(key)?.Name, Card = count == 0 ? null : count == 1 ? "1 rule" : $"{count} rules" };
        }

        public static IEnumerable<(string label, Action run)> ContextItems(IReadOnlyList<string> keys)
        {
            if (keys.Count != 1 || LogicOwner.ForItem(keys[0]) is not { } owner) yield break;
            yield return ("Behaviour…", () => StudioDock.ShowPanel("panel:" + Tab));
            yield return ("Add a rule…", () => NewRule(owner));
            yield return ("Wire from here…", () => { Studio.SetTool(WireTool.ToolId); WireTool.From = keys[0]; });
        }

        public static string TemplateName(string id)
        {
            if (id == null) return "";
            if (id.StartsWith("vanilla:")) return BuilderServices.Catalog?.FindUnit(id.Substring(8))?.DisplayName ?? id.Substring(8);
            var pack = Studio.Draft?.Pack ?? EnemyStudio.Draft?.Pack;
            var u = pack?.Units.FirstOrDefault(x => x?.Id == id);
            if (u != null) return string.IsNullOrWhiteSpace(u.Name) ? id : u.Name;
            var e = (LogicDoc.Get(pack, "entities") as JArray)?.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == id);
            return (string)e?["name"] ?? id;
        }

        // ---- Logic palette -------------------------------------------------------------------------------------------

        /// <summary>A Logic palette entry: a shape (area, point, path) or a built-in logic object (a "Logic" preset).</summary>
        internal sealed class PaletteObject
        {
            public string Id, Name, Desc, IdPrefix;
            public JObject Place;
            public string Kind => (string)Place["kind"];
        }

        private static readonly PaletteObject[] Shapes =
        [
            new() { Id = "area", Name = "Area", IdPrefix = "area", Desc = "A zone that notices what's inside: enter, exit, inside, empty.",
                Place = JObject.Parse("{ 'kind': 'area', 'area': { 'shape': { 'circle': { 'radius': 4 } }, 'watch': 'unit', 'show': 'outline' } }") },
            new() { Id = "point", Name = "Point", IdPrefix = "point", Desc = "A named spot to teleport, spawn or walk to (@name).", Place = JObject.Parse("{ 'kind': 'point' }") },
            new() { Id = "path", Name = "Path", IdPrefix = "path", Desc = "A route for movers and walkers (@name). Move it whole; add points in its Behaviour tab.",
                Place = JObject.Parse("{ 'kind': 'path', 'points': [[0, 0, 0], [6, 0, 0], [6, 0, 6]] }") },
        ];

        public static List<PaletteObject> PaletteObjects() => Shapes.Concat(PresetUi.LogicObjects().Select(d => new PaletteObject
        {
            Id = d.Id, Name = d.Name ?? d.Id, Desc = d.Description ?? "", IdPrefix = d.Id, Place = PresetUi.AsPlace(d),
        })).ToList();

        public static StudioPlaceable Placeable(PaletteObject obj) => new()
        {
            Id = "logic:" + obj.Id,
            Name = obj.Name,
            MakeGhost = parent =>
            {
                var g = GameObject.CreatePrimitive(obj.Kind == "area" ? PrimitiveType.Cylinder : PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent, false);
                g.transform.localScale = obj.Kind == "area" ? new Vector3(8f, 0.05f, 8f) : Vector3.one * 1.2f;
                var r = g.GetComponent<Renderer>();
                if (r != null) { r.material = new Material(Shader.Find("Sprites/Default")) { color = PlaceItems.ColorOf(obj.Kind) * new Color(1, 1, 1, 0.6f) }; }
                return g;
            },
            Place = (pos, yaw) => PlaceItems.Add(obj.Name, obj.IdPrefix, obj.Place, pos, yaw),
        };

        public static void Palette(ModPage page)
        {
            string armed = StudioShell.Tool == StudioShell.Place ? StudioShell.Place.Armed?.Id : null;
            const int perRow = 2;
            var all = PaletteObjects();
            int rows = (all.Count + perRow - 1) / perRow;
            const float h = 64f, gap = 6f;
            var area = page.Custom("Logic objects", rows * (h + gap));
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(page.Content);
            float w = (page.Size.x - (perRow - 1) * gap) / perRow;
            for (int i = 0; i < all.Count; i++)
            {
                var obj = all[i];
                var tile = ModTile.Create(area, "Logic " + obj.Name, new Vector2(w, h), null, obj.Name, 20f);
                tile.Key = "logic:" + obj.Id;
                tile.Label.alignment = TextAlignmentOptions.Center;
                tile.Place(new Vector2((i % perRow) * (w + gap), -(i / perRow) * (h + gap)));
                tile.SetOn(armed == "logic:" + obj.Id);
                tile.SetMarker(true, PlaceItems.ColorOf(obj.Kind));
                tile.Applied += () => Studio.Arm(Placeable(obj));
                ModTooltip.Attach(tile, obj.Name, () => obj.Desc + "\nClick, then click the map.");
            }
            page.Text(LogicFields.Dim("Then select it and open Behaviour, or wire it (J)."));
        }

        // ---- Enemy Studio --------------------------------------------------------------------------------------------

        public static void EnemyBehaviour(ModPage page)
        {
            var unit = EnemyStudio.Unit;
            if (unit == null) return;
            var owner = LogicOwner.Unit(unit.Id, string.IsNullOrWhiteSpace(unit.Name) ? unit.Id : unit.Name);
            BehaviourPanel.Build(page, owner);
        }
    }
}
