using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The Wire tool (docs/builder-engine.md, "Wire tool"): drag from one object to another (or click one, then the other)
    and choose, beside the panel, what of the first's sets off what on the second: "When Lever is used → Door: open".
    It writes the two rules a wire is. The overlay draws every wire as an arc in the world, labelled "event → input" and
    coloured by signal name: all of them while the tool is active or View ▾ Wires is on, else the selection's own.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class WireTool : StudioTool
    {
        public const string ToolId = "wire";
        private readonly Keybind key;
        private string from;

        public WireTool(Keybind key) => this.key = key;

        public override string Id => ToolId;
        public override string Name => "Wire";
        public override string Hint => from == null ? "Drag from one thing to another, or click one then the other" : "Click what it sets off · Esc cancels";
        public override Sprite Icon => ModStyle.AssetSprite("studio_link.png");
        public override Keybind Key => key;
        public override int Order => 650;

        /// <summary>The source while dragging or after the first click (for the overlay's rubber band).</summary>
        public static string From;
        public static Vector3? Cursor;

        public override void Activate() { from = From = null; }
        public override void Deactivate() { from = From = null; Cursor = null; }

        public override bool Cancel()
        {
            if (from == null) return false;
            from = From = null;
            return true;
        }

        private static bool Wireable(string key) => key != null && LogicOwner.ForItem(key) != null;

        public override void Update(StudioPointer p)
        {
            Cursor = p.Ground;
            if (p.OverUI) return;
            if (p.LeftDown && from == null && Wireable(p.HoverKey)) { from = From = p.HoverKey; return; }
            if (!p.LeftUp || from == null) return;
            string target = p.HoverKey;
            if (target == from || !Wireable(target))
            {
                // A click on the source keeps it armed (click, then click the target); a drag to nowhere cancels.
                if (p.Dragging) from = From = null;
                return;
            }
            string source = from;
            from = From = null;
            Choose(LogicOwner.ForItem(source), LogicOwner.ForItem(target));
        }

        // ---- Choosing ----------------------------------------------------------------------------------------------

        /// <summary>Step 1: what of the source; step 2: what of the target.</summary>
        public static void Choose(LogicOwner source, LogicOwner target)
        {
            if (source == null || target == null) return;
            var (min, max) = LogicStudio.EditorOffsets();
            RuleEditor.OpenStandalone(StudioDock.Dock, min, max, $"Wire: {source.Title} → {target.Title}", page => SourcePage(page, source, target));
        }

        private static void SourcePage(ModPage page, LogicOwner source, LogicOwner target)
        {
            page.Text(LogicFields.Dim($"When {LogicFields.Esc(source.Title)}…"));
            // Its outputs first: rules with nothing to do yet, or only wires (a lever's "is used", a counter's "done").
            var rules = source.Rules()?.OfType<JObject>().ToList() ?? [];
            var outputs = rules.Where(r => LogicDoc.EventOf(r) != null && r["if"] == null && ((r["do"] as JArray)?.All(a => (string)a["do"] == "signal") ?? true)).ToList();
            if (outputs.Count > 0) StudioDock.SmallHeader(page, "Its outputs");
            foreach (var r in outputs)
            {
                var rule = r;
                string ev = LogicDoc.EventOf(rule);
                var prms = rule["when"] as JObject;
                StudioDock.DetailRow(page, LogicFields.Esc(RuleText.When(rule, source.Kind)), LogicFields.Esc(LogicCatalog.Event(ev)?.Desc ?? ""), false, null,
                    () => RuleEditor.OpenPicker($"Wire: {source.Title} → {target.Title}", p => TargetPage(p, source, target, ev, Params(prms))));
            }
            StudioDock.SmallHeader(page, "Events");
            string kind = source.Kind == OwnerKind.Place ? PlaceItems.KindOf(source.Id) : null;
            var events = LogicCatalog.Events.Where(e => e.CanOf && (e.Owners == null || (kind != null && e.Owners.Contains(kind))) || e.Owners?.Contains(kind) == true)
                .OrderByDescending(e => e.Common).ThenBy(e => Array.IndexOf(LogicCatalog.EventCategories, e.Category)).ToList();
            foreach (var e in events)
            {
                var ev = e;
                StudioDock.DetailRow(page, "this " + LogicFields.Esc(e.Phrase.Replace("{", "").Replace("}", "")), LogicFields.Esc(e.Desc), false, null,
                    () => RuleEditor.OpenPicker($"Wire: {source.Title} → {target.Title}", p => TargetPage(p, source, target, ev.Name, DefaultParams(ev))));
            }
        }

        private static JObject Params(JObject when)
        {
            if (when == null) return null;
            var o = (JObject)when.DeepClone();
            o.Remove("on");
            return o.Properties().Any() ? o : null;
        }

        private static JObject DefaultParams(EventUi e)
        {
            var o = new JObject();
            foreach (var p in e.Params.Where(p => p.Required && p.Default != null)) o[p.Name] = LogicDoc.Literal(p);
            return o.Properties().Any() ? o : null;
        }

        private static void TargetPage(ModPage page, LogicOwner source, LogicOwner target, string ev, JObject prms)
        {
            var fake = new JObject { ["when"] = prms == null ? ev : new JObject(prms.Properties().Select(p => new JProperty(p.Name, p.Value))) { ["on"] = ev } };
            page.Text(LogicFields.Dim($"When {LogicFields.Esc(source.Title)} {LogicFields.Esc(RuleText.When(fake, source.Kind).Replace("this ", ""))}, {LogicFields.Esc(target.Title)}…"));
            StudioDock.SmallHeader(page, "Built in");
            foreach (var (name, desc) in Wires.BuiltIns)
            {
                string input = name;
                StudioDock.DetailRow(page, input, desc, false, null, () => Write(source, target, ev, prms, input));
            }
            // The target's own listeners ("gets signal add").
            var listens = target.Rules()?.OfType<JObject>().Where(r => LogicDoc.EventOf(r) == "signal").Select(r => (string)(r["when"] as JObject)?["name"]).Where(n => n != null && !Wires.IsBuiltIn(n)).Distinct().ToList() ?? [];
            if (listens.Count > 0) StudioDock.SmallHeader(page, "Its inputs");
            foreach (var n in listens)
            {
                string input = n;
                StudioDock.DetailRow(page, input, "a signal it listens for", false, null, () => Write(source, target, ev, prms, input));
            }
            StudioDock.SmallHeader(page, "New");
            page.TextInput("New signal", () => "", v => { if (!string.IsNullOrWhiteSpace(v)) Write(source, target, ev, prms, v.Trim()); },
                () => "A name; the target gets a \"gets signal\" rule for it, ready for its actions.");
        }

        public static void Write(LogicOwner source, LogicOwner target, string ev, JObject prms, string input)
        {
            string label = $"Wire {source.Title} → {target.Title}";
            int listener = -1;
            Studio.Edit(label, lv => LogicDoc.EditLogic(lv, logic =>
            {
                string name = LogicOwner.EnsureName(logic, target);
                var s = LogicDoc.Node(logic, source.Kind, source.Id, true);
                var t = LogicDoc.Node(logic, target.Kind, target.Id, true);
                if (s == null || t == null || name == null) throw new InvalidOperationException("can't wire these");
                listener = Wires.Add(s, t, name, ev, prms, input);
            }));
            RuleEditor.Close();
            LogicStudio.Changed();
            Notify.Show($"Wired: {source.Title} {ev} → {input} {target.Title}", NotifyKind.Info, 2.5f, "logic");
            // A new signal: open the target's listener so its actions can be filled in.
            if (listener >= 0 && target.Rules()?[listener] is JObject r && ((r["do"] as JArray)?.Count ?? 0) == 0)
            {
                Studio.Select([target.StudioKey], SelectOp.Replace);
                LogicStudio.OpenRule(target, listener);
            }
        }

        /// <summary>Asks before removing a wire (both its rules).</summary>
        public static void Confirm(Wire w)
        {
            ModPopup.Open("Remove this wire?", page =>
            {
                page.Text(LogicFields.Esc($"{LogicOwner.FromId(w.FromId)?.Title ?? w.FromId}: {w.Event} → {w.Signal} @{w.ToName}"));
                page.Button("Remove", () => { ModPopup.Current?.Close(); Remove(w); });
                page.Button("Keep", () => ModPopup.Current?.Close());
            });
        }

        public static void Remove(Wire w)
        {
            Studio.Edit($"Remove wire {w.Event} → {w.Signal}", lv => LogicDoc.EditLogic(lv, logic =>
            {
                // Re-find it: indices may have moved since it was listed.
                var now = Wires.Scan(logic).FirstOrDefault(x => x.FromKind == w.FromKind && x.FromId == w.FromId && x.Signal == w.Signal && x.ToName == w.ToName && x.Event == w.Event);
                if (now != null) Wires.Remove(logic, now);
            }));
            LogicStudio.Changed();
        }
    }

    /// <summary>Draws wires as labelled arcs (edit mode).</summary>
    internal sealed class WireOverlay : IStudioOverlay
    {
        public static readonly WireOverlay Instance = new();
        public static bool ShowAll;

        private GameObject root;
        private Material material;
        private readonly List<(LineRenderer arc, LineRenderer head, TextMeshPro label)> pool = [];
        private int used;
        private const int Segments = 18;

        public void Draw()
        {
            used = 0;
            try { DrawWires(); }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Wire overlay failed", e); }
            finally
            {
                for (int i = used; i < pool.Count; i++) { pool[i].arc.enabled = false; pool[i].head.enabled = false; pool[i].label.gameObject.SetActive(false); }
            }
        }

        public static Vector3? PosOf(OwnerKind? kind, string id)
        {
            if (kind == null) return null;
            string key = kind == OwnerKind.Place ? PlaceItems.Prefix + id : id == "castle" ? Studio.Items.FirstOrDefault(i => i.IsCastle)?.Key : id;
            return Studio.Find(key)?.Pos;
        }

        private void DrawWires()
        {
            var logic = LogicOwner.LevelLogic();
            bool tool = Studio.ToolId == WireTool.ToolId;
            var selected = new HashSet<string>(Studio.Selection, StringComparer.Ordinal);
            foreach (var w in Wires.Scan(logic))
            {
                var a = PosOf(w.FromKind, w.FromId);
                var b = PosOf(w.ToKind, w.ToId);
                if (a == null || b == null) continue;
                string fromKey = w.FromKind == OwnerKind.Place ? PlaceItems.Prefix + w.FromId : w.FromId;
                string toKey = w.ToKind == OwnerKind.Place ? PlaceItems.Prefix + w.ToId : w.ToId;
                bool mine = selected.Contains(fromKey) || selected.Contains(toKey);
                if (!tool && !ShowAll && !mine) continue;
                var color = Color.HSVToRGB(Wires.Hue(w.Signal), 0.55f, 1f);
                if (!mine && (tool || ShowAll) && selected.Count > 0) color.a = 0.5f;
                Arc(a.Value, b.Value, color, mine ? 0.3f : 0.2f, w.Label + (w.Delayed ? " ⏱" : ""));
            }
            // The rubber band while dragging a new wire.
            if (tool && WireTool.From != null && Studio.Find(WireTool.From) is { } src && WireTool.Cursor is Vector3 c)
                Arc(src.Pos, c, new Color(1f, 1f, 1f, 0.8f), 0.18f, null);
        }

        private void Arc(Vector3 from, Vector3 to, Color color, float width, string text)
        {
            var (arc, head, label) = Next();
            var flat = to - from;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist < 0.5f) return;
            var dir = flat / dist;
            var a = from + dir * Mathf.Min(1.8f, dist * 0.2f);
            var b = to - dir * Mathf.Min(1.8f, dist * 0.2f);
            a.y = Studio.Ground(a) + 0.6f;
            b.y = Studio.Ground(b) + 0.6f;
            // Higher than requirement arrows, so both read where they overlap.
            var mid = (a + b) / 2f + Vector3.up * Mathf.Clamp(dist * 0.3f, 2f, 9f);
            arc.positionCount = Segments + 1;
            for (int i = 0; i <= Segments; i++)
            {
                float t = i / (float)Segments;
                arc.SetPosition(i, (1 - t) * (1 - t) * a + 2 * (1 - t) * t * mid + t * t * b);
            }
            var tangent = (b - mid).normalized;
            var side = Vector3.Cross(Vector3.up, tangent).normalized;
            float size = Mathf.Clamp(width * 5f, 0.9f, 1.8f);
            head.positionCount = 3;
            head.SetPosition(0, b - tangent * size + side * size * 0.6f);
            head.SetPosition(1, b);
            head.SetPosition(2, b - tangent * size - side * size * 0.6f);
            foreach (var lr in new[] { arc, head })
            {
                lr.startWidth = lr.endWidth = width;
                lr.startColor = lr.endColor = color;
                lr.enabled = true;
            }
            if (text == null) { label.gameObject.SetActive(false); return; }
            var top = 0.25f * a + 0.5f * mid + 0.25f * b;
            label.text = text;
            label.color = color;
            label.transform.position = top + Vector3.up * 0.8f;
            var cam = Camera.main;
            if (cam != null) label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
            label.fontSize = Mathf.Clamp(StudioCamera.Zoom * 6f, 5f, 14f);
            label.gameObject.SetActive(true);
        }

        private (LineRenderer, LineRenderer, TextMeshPro) Next()
        {
            if (root == null)
            {
                pool.Clear();
                root = new GameObject(LayoutScene.AddedPrefix + "wires");
                var scene = LayoutScene.Host(Studio.Level?.Host);
                if (scene.IsValid()) SceneManager.MoveGameObjectToScene(root, scene);
                material ??= new Material(Shader.Find("Sprites/Default"));
            }
            if (used == pool.Count) pool.Add((Line("arc"), Line("head"), Label()));
            return pool[used++];

            LineRenderer Line(string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = material;
                lr.useWorldSpace = true;
                lr.numCapVertices = 2;
                lr.numCornerVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.enabled = false;
                return lr;
            }

            TextMeshPro Label()
            {
                var go = new GameObject("label");
                go.transform.SetParent(root.transform, false);
                var t = go.AddComponent<TextMeshPro>();
                t.font = ModStyle.BoldFont;
                t.alignment = TextAlignmentOptions.Center;
                t.enableWordWrapping = false;
                t.outlineWidth = 0.25f;
                t.outlineColor = new Color32(20, 20, 20, 255);
                go.SetActive(false);
                return t;
            }
        }

        public void Hide()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            pool.Clear();
            used = 0;
        }
    }
}
