using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The Studio's own tools: Select (click, Shift add, Ctrl/Cmd toggle, a drag from empty ground draws a box, a drag from
    a selected object moves the selection), Lasso, Paint select, Move (grab), Rotate, Place (a palette kind) and Erase.
    W3's Link and W4's Terrain tools register through Studio.AddTool.
*/
namespace PalaceMod.Builder.Studio
{
    internal abstract class BuiltinTool : StudioTool
    {
        private readonly string id, name, hint, icon;
        private readonly Func<Keybind> key;
        private readonly int order;
        private Sprite sprite;

        protected BuiltinTool(string id, string name, string hint, string icon, Func<Keybind> key, int order)
        {
            this.id = id; this.name = name; this.hint = hint; this.icon = icon; this.key = key; this.order = order;
        }

        public override string Id => id;
        public override string Name => name;
        public override string Hint => hint;
        public override Sprite Icon => sprite ??= ModStyle.AssetSprite($"studio_{icon}.png");
        public override Keybind Key => key();
        public override int Order => order;
    }

    /// <summary>Box and free-shape selection over the items' screen points.</summary>
    internal static class AreaSelect
    {
        public static void Apply(Func<Vector2, bool> inside, Vector3? pressGround, SelectOp op)
        {
            var hits = new List<StudioItem>();
            foreach (var item in StudioScene.Pickable)
            {
                if (StudioShell.WorldToScreen(item.Pos) is Vector2 s && inside(s)) hits.Add(item);
            }
            var origin = pressGround ?? Vector3.zero;
            var keys = hits.OrderBy(i => (i.Pos - origin).sqrMagnitude).Select(i => i.Key).ToList();
            if (op == SelectOp.Replace) StudioSelection.Select(keys, SelectOp.Replace);
            else StudioSelection.Select(keys, op == SelectOp.Toggle ? SelectOp.Toggle : SelectOp.Add);
        }

        public static bool InPolygon(Vector2 p, IReadOnlyList<Vector2> poly) => StudioMath.InPolygon(p, poly);
    }

    internal sealed class SelectTool : BuiltinTool
    {
        public SelectTool() : base("select", "Select", "Click select · Shift add · Ctrl toggle · drag box · drag selection to move", "select", () => StudioKeys.ToolSelect, 0) { }

        private enum Drag { None, Pending, Box, Move }
        private Drag drag;
        private string pressKey;
        private Dictionary<string, (Vector3 pos, float yaw)> start;
        private Vector3 lastDelta;

        public override void Deactivate() => Cancel();

        public override bool Cancel()
        {
            bool busy = Units.StudioUnits.CancelDrag() | drag is Drag.Box or Drag.Move;
            if (drag == Drag.Move) StudioScene.Reconcile(Studio.Level);
            drag = Drag.None;
            StudioDock.SetMarquee(null);
            return busy;
        }

        public override void Update(StudioPointer p)
        {
            // A press on a selected training slot's unit disc drags that unit (Studio/Units).
            if (drag == Drag.None && Units.StudioUnits.HandlePointer(p)) return;
            if (p.LeftDown && !p.OverUI)
            {
                pressKey = p.HoverKey;
                drag = Drag.Pending;
                if (pressKey != null && !StudioSelection.Contains(pressKey)) StudioSelection.ClickSelect(pressKey, p);
                else if (pressKey != null && p.Ctrl) StudioSelection.ClickSelect(pressKey, p);
            }
            if (drag == Drag.Pending && p.Dragging)
            {
                if (pressKey != null && StudioSelection.Contains(pressKey) && !p.Ctrl)
                {
                    drag = Drag.Move;
                    start = StudioSelection.Items.Where(StudioEdits.Movable).ToDictionary(i => i.Key, i => (i.Pos, i.Yaw));
                }
                else drag = Drag.Box;
            }
            if (drag == Drag.Box)
            {
                var r = Rect.MinMaxRect(Mathf.Min(p.DownScreen.x, p.Screen.x), Mathf.Min(p.DownScreen.y, p.Screen.y), Mathf.Max(p.DownScreen.x, p.Screen.x), Mathf.Max(p.DownScreen.y, p.Screen.y));
                StudioDock.SetMarquee(r);
                if (p.LeftUp)
                {
                    AreaSelect.Apply(r.Contains, p.DownGround, StudioSelection.OpFor(p));
                    StudioDock.SetMarquee(null);
                    drag = Drag.None;
                }
            }
            else if (drag == Drag.Move)
            {
                if (p.Ground is Vector3 g && p.DownGround is Vector3 d0)
                {
                    lastDelta = StudioShell.SnapDelta(g - d0, p.Alt);
                    foreach (var kv in start)
                        if (StudioScene.Find(kv.Key) is { } item) StudioScene.Preview(item, StudioScene.Ground(kv.Value.pos + lastDelta), kv.Value.yaw);
                }
                if (p.LeftUp)
                {
                    drag = Drag.None;
                    var items = start.Keys.Select(StudioScene.Find).Where(i => i != null).ToList();
                    if (lastDelta.sqrMagnitude > 1e-6f && items.Count > 0) StudioEdits.SetPoses(items.Select(i => (i, StudioScene.Ground(start[i.Key].pos + lastDelta), start[i.Key].yaw)).ToList(), $"Move {StudioEdits.Count(items.Count, items[0].Name)}");
                    else StudioScene.Reconcile(Studio.Level);
                    lastDelta = Vector3.zero;
                }
            }
            else if (drag == Drag.Pending && p.LeftUp)
            {
                drag = Drag.None;
                // A click on an already selected object without modifiers narrows the selection to it.
                if (pressKey != null && StudioSelection.Contains(pressKey) && !p.Shift && !p.Ctrl) StudioSelection.ClickSelect(pressKey, p);
                else if (pressKey == null) StudioSelection.ClickSelect(null, p);
            }
        }
    }

    internal sealed class LassoTool : BuiltinTool
    {
        public LassoTool() : base("lasso", "Lasso", "Drag a shape · Shift add · Ctrl toggle", "lasso", () => StudioKeys.ToolLasso, 10) { }

        private readonly List<Vector2> points = [];
        private readonly List<Vector3> ground = [];
        private bool drawing;

        public override void Deactivate() => Cancel();

        public override bool Cancel()
        {
            bool was = drawing;
            drawing = false;
            points.Clear();
            ground.Clear();
            StudioGizmos.Polyline("lasso", null);
            return was;
        }

        public override void Update(StudioPointer p)
        {
            if (p.LeftDown && !p.OverUI) { drawing = true; points.Clear(); ground.Clear(); }
            if (!drawing) return;
            if (points.Count == 0 || (points[points.Count - 1] - p.Screen).sqrMagnitude > 16f)
            {
                points.Add(p.Screen);
                if (p.Ground is Vector3 g) ground.Add(g);
                StudioGizmos.Polyline("lasso", ground, closed: true, color: ModStyle.Bright);
            }
            if (p.LeftUp)
            {
                if (points.Count >= 3) AreaSelect.Apply(s => AreaSelect.InPolygon(s, points), p.DownGround, StudioSelection.OpFor(p));
                else if (!p.Dragging) StudioSelection.ClickSelect(p.HoverKey, p);
                Cancel();
            }
        }
    }

    internal sealed class PaintTool : BuiltinTool
    {
        public PaintTool() : base("paint", "Paint select", "Drag to add · Alt+drag removes · Alt+wheel size", "paint", () => StudioKeys.ToolPaint, 20) { }

        public static float Radius = 4f;
        private bool painting;

        public override void Deactivate() { painting = false; StudioGizmos.Circle("paint", null, 0f); }

        public override bool Cancel()
        {
            bool was = painting;
            painting = false;
            return was;
        }

        public override void Update(StudioPointer p)
        {
            if (p.Alt && !p.OverUI)
            {
                float lines = ScrollInput.Lines;
                if (Mathf.Abs(lines) > 0.01f) Radius = Mathf.Clamp(Radius * Mathf.Pow(1.15f, lines), 1f, 40f);
            }
            StudioGizmos.Circle("paint", p.OverUI ? null : p.Ground, Radius, p.Alt ? LayoutColors.Remove : ModStyle.Bright);
            if (p.LeftDown && !p.OverUI)
            {
                painting = true;
                if (!p.Shift && !p.Ctrl && !p.Alt) StudioSelection.Clear();
            }
            if (painting && p.Ground is Vector3 g)
            {
                var keys = StudioScene.Pickable.Where(i => new Vector2(i.Pos.x - g.x, i.Pos.z - g.z).magnitude <= Radius).OrderBy(i => (i.Pos - g).sqrMagnitude).Select(i => i.Key).ToList();
                if (keys.Count > 0) StudioSelection.Select(keys, p.Alt ? SelectOp.Remove : SelectOp.Add);
            }
            if (p.LeftUp) painting = false;
        }
    }

    internal static class LayoutColors
    {
        public static readonly Color Remove = new(0.95f, 0.4f, 0.35f, 1f);
    }

    /// <summary>Grab: the selection follows the cursor; click commits, Esc or a right click cancels.</summary>
    internal sealed class MoveTool : BuiltinTool
    {
        public MoveTool() : base("move", "Move", "Click to drop · X/Z lock axis · arrows nudge · Esc cancels", "move", () => StudioKeys.ToolMove, 30) { }

        private Dictionary<string, (Vector3 pos, float yaw)> start;
        private Vector3? anchor;
        private Vector3 delta;
        private int axis; // 0 free, 1 x, 2 z
        /// <summary>The tool to go back to after a drop or cancel.</summary>
        public string ReturnTo = "select";

        public bool Grabbing => start != null;

        public override void Activate()
        {
            start = StudioSelection.Items.Where(StudioEdits.Movable).ToDictionary(i => i.Key, i => (i.Pos, i.Yaw));
            if (StudioSelection.Items.Any(i => i.IsCastle)) Notify.Show("Castle stays put", NotifyKind.Info, 2f, "builder-studio");
            if (start.Count == 0) { start = null; return; }
            anchor = StudioShell.CursorGroundNow;
            delta = Vector3.zero;
            axis = 0;
        }

        public override void Deactivate()
        {
            if (start != null) StudioScene.Reconcile(Studio.Level);
            start = null;
        }

        public override bool Cancel()
        {
            if (start == null) return false;
            start = null;
            StudioScene.Reconcile(Studio.Level);
            StudioShell.SetToolLater(ReturnTo);
            return true;
        }

        public override void Update(StudioPointer p)
        {
            if (start == null)
            {
                // Nothing grabbed: a click selects, and pressing G again with a selection grabs it.
                if (p.Click && !p.OverUI) { StudioSelection.ClickSelect(p.HoverKey, p); Activate(); }
                return;
            }
            if (Input.GetKeyDown(KeyCode.X)) axis = axis == 1 ? 0 : 1;
            if (Input.GetKeyDown(KeyCode.Z) && !p.Ctrl) axis = axis == 2 ? 0 : 2;
            if (anchor == null && p.Ground is Vector3 a) anchor = a;
            if (p.Ground is Vector3 g && anchor is Vector3 a0)
            {
                var d = g - a0;
                if (axis == 1) d = new Vector3(d.x, 0f, 0f);
                else if (axis == 2) d = new Vector3(0f, 0f, d.z);
                delta = StudioShell.SnapDelta(d, p.Alt);
            }
            foreach (var kv in start)
                if (StudioScene.Find(kv.Key) is { } item) StudioScene.Preview(item, StudioScene.Ground(kv.Value.pos + delta), kv.Value.yaw);
            if (p.Click && !p.OverUI)
            {
                var items = start.Keys.Select(StudioScene.Find).Where(i => i != null).ToList();
                var s = start;
                start = null;
                if (items.Count > 0 && delta.sqrMagnitude > 1e-6f) StudioEdits.SetPoses(items.Select(i => (i, StudioScene.Ground(s[i.Key].pos + delta), s[i.Key].yaw)).ToList(), $"Move {StudioEdits.Count(items.Count, items[0].Name)}");
                else StudioScene.Reconcile(Studio.Level);
                StudioShell.SetToolLater(ReturnTo);
            }
        }

        public string AxisLabel => axis == 1 ? "X" : axis == 2 ? "Z" : null;
    }

    internal sealed class RotateTool : BuiltinTool
    {
        public RotateTool() : base("rotate", "Rotate", "Drag sideways to turn · X tilt / Z roll scenery · , . step · Alt free", "rotate", () => StudioKeys.ToolRotate, 40) { }

        private Dictionary<string, (Vector3 pos, float yaw, float tiltX, float tiltZ)> start;
        private Vector3 center;
        private float angle;
        /// <summary>0 turn (about the up axis), 1 tilt (props' own x), 2 roll (props' own z). X and Z toggle it.</summary>
        public int Axis { get; private set; }

        public string AxisLabel => Axis == 1 ? "Tilt" : Axis == 2 ? "Roll" : null;

        public override bool Cancel()
        {
            if (start == null)
            {
                if (Axis == 0) return false;
                Axis = 0;   // Esc first leaves tilt/roll, then (with nothing to cancel) the tool
                return true;
            }
            start = null;
            StudioScene.Reconcile(Studio.Level);
            return true;
        }

        public override void Deactivate()
        {
            Cancel();
            Axis = 0;
        }

        /// <summary>The , and . keys while tilting or rolling: steps the selected props' tilt.</summary>
        public void Step(float degrees)
        {
            var props = StudioSelection.Items.Where(i => StudioEdits.Movable(i) && i.Kind == StudioItemKind.Prop).ToList();
            if (props.Count == 0) { Notify.Show("Only scenery tilts", NotifyKind.Info, 2f, "builder-studio"); return; }
            StudioEdits.SetTilts(props.Select(i => (i, i.TiltX + (Axis == 1 ? degrees : 0f), i.TiltZ + (Axis == 2 ? degrees : 0f))).ToList(),
                $"{AxisLabel} {StudioEdits.Count(props.Count, props[0].Name)}", (Axis == 1 ? "tiltx:" : "tiltz:") + string.Join(",", props.Select(i => i.Key)));
        }

        public override void Update(StudioPointer p)
        {
            if (start == null)
            {
                if (Input.GetKeyDown(KeyCode.X)) Axis = Axis == 1 ? 0 : 1;
                if (Input.GetKeyDown(KeyCode.Z) && !p.Ctrl) Axis = Axis == 2 ? 0 : 2;
            }
            if (p.LeftDown && !p.OverUI)
            {
                if (p.HoverKey != null && !StudioSelection.Contains(p.HoverKey)) StudioSelection.ClickSelect(p.HoverKey, p);
                var items = StudioSelection.Items.Where(StudioEdits.Movable).Where(i => Axis == 0 || i.Kind == StudioItemKind.Prop).ToList();
                if (Axis != 0 && items.Count == 0 && StudioSelection.Count > 0) Notify.Show("Only scenery tilts", NotifyKind.Info, 2f, "builder-studio");
                if (items.Count > 0)
                {
                    start = items.ToDictionary(i => i.Key, i => (i.Pos, i.Yaw, i.TiltX, i.TiltZ));
                    center = items.Aggregate(Vector3.zero, (a, i) => a + i.Pos) / items.Count;
                    angle = 0f;
                }
            }
            if (start == null) return;
            float raw = (p.Screen.x - p.DownScreen.x) * 0.5f;
            angle = p.Alt ? raw : Mathf.Round(raw / StudioShell.TurnStep) * StudioShell.TurnStep;
            var rot = Quaternion.Euler(0f, angle, 0f);
            int count = start.Count;
            Vector3 c = center;
            Vector3 PosOf(Vector3 pos) => Axis != 0 || count == 1 ? pos : StudioScene.Ground(c + rot * (pos - c));
            foreach (var kv in start)
            {
                if (StudioScene.Find(kv.Key) is not { } item) continue;
                var s = kv.Value;
                if (Axis == 0) StudioScene.Preview(item, PosOf(s.pos), s.yaw + angle);
                else StudioScene.Preview(item, s.pos, s.yaw, s.tiltX + (Axis == 1 ? angle : 0f), s.tiltZ + (Axis == 2 ? angle : 0f));
            }
            if (p.LeftUp)
            {
                var st = start;
                start = null;
                var items = st.Keys.Select(StudioScene.Find).Where(i => i != null).ToList();
                if (Mathf.Abs(angle) <= 0.01f || items.Count == 0) StudioScene.Reconcile(Studio.Level);
                else if (Axis == 0)
                    StudioEdits.SetPoses(items.Select(i => (i, PosOf(st[i.Key].pos), st[i.Key].yaw + angle)).ToList(), $"Turn {StudioEdits.Count(items.Count, items[0].Name)}");
                else
                    StudioEdits.SetTilts(items.Select(i => (i, st[i.Key].tiltX + (Axis == 1 ? angle : 0f), st[i.Key].tiltZ + (Axis == 2 ? angle : 0f))).ToList(),
                        $"{AxisLabel} {StudioEdits.Count(items.Count, items[0].Name)}");
            }
        }
    }

    internal sealed class PlaceTool : BuiltinTool
    {
        public PlaceTool() : base("place", "Place", "Click to place · Shift keeps placing · , . turn · Alt free", "place", () => null, 50) { }

        public StudioPlaceable Armed { get; private set; }
        public float GhostYaw;
        private GameObject ghost;
        private Quaternion ghostRot = Quaternion.identity;

        public void Arm(StudioPlaceable placeable)
        {
            Disarm();
            Armed = placeable;
            GhostYaw = 0f;
            if (placeable?.MakeGhost != null && StudioScene.Root != null)
            {
                try { ghost = placeable.MakeGhost(StudioScene.Root); }
                catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio: ghost for {placeable.Id} failed", e); }
                if (ghost != null) { ghost.SetActive(false); ghost.name = "PalaceBuilder ghost"; ghostRot = ghost.transform.rotation; }
            }
        }

        private void Disarm()
        {
            if (ghost != null) UnityEngine.Object.Destroy(ghost);
            ghost = null;
            Armed = null;
        }

        public override void Deactivate() => Disarm();

        public override bool Cancel()
        {
            if (Armed == null) return false;
            Disarm();
            StudioShell.SetToolLater("select");
            return true;
        }

        public void Turn(float degrees) => GhostYaw += degrees;

        public override void Update(StudioPointer p)
        {
            if (Armed == null) return;
            bool show = p.Snapped != null && !p.OverUI;
            if (ghost != null)
            {
                ghost.SetActive(show);
                if (show)
                {
                    // Ghosts are made in their kind's own pose; turning adds yaw on top.
                    ghost.transform.SetPositionAndRotation(p.Snapped.Value, Quaternion.Euler(0f, GhostYaw, 0f) * ghostRot);
                }
            }
            if (p.Click && show)
            {
                string key = null;
                try { key = Armed.Place?.Invoke(p.Snapped.Value, GhostYaw + GhostBaseYaw); }
                catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio: placing {Armed.Id} failed", e); }
                StudioShell.Remember(Armed);
                if (key != null) StudioSelection.Select([key], SelectOp.Replace);
                if (!p.Shift) { Disarm(); StudioShell.SetToolLater("select"); }
            }
        }

        /// <summary>The armed kind's own yaw (a host slot's), added to the turn when placing.</summary>
        public float GhostBaseYaw;
    }

    internal sealed class EraseTool : BuiltinTool
    {
        public EraseTool() : base("erase", "Erase", "Click or drag over objects to delete them", "erase", () => StudioKeys.ToolErase, 900) { }

        private readonly List<string> marked = [];
        private bool erasing;

        public override bool Cancel()
        {
            bool was = erasing;
            erasing = false;
            marked.Clear();
            StudioScene.RefreshMarkers();
            return was;
        }

        public override void Deactivate() => Cancel();

        public override void Update(StudioPointer p)
        {
            if (p.LeftDown && !p.OverUI) { erasing = true; marked.Clear(); }
            if (!erasing) return;
            if (p.HoverKey != null && !marked.Contains(p.HoverKey))
            {
                marked.Add(p.HoverKey);
                StudioSelection.Select([p.HoverKey], SelectOp.Add);
            }
            if (p.LeftUp)
            {
                erasing = false;
                if (marked.Count > 0) StudioEdits.Delete(marked.ToList());
                marked.Clear();
            }
        }
    }

    /// <summary>World-space helper drawings (lasso path, brush ring) under the Studio's root.</summary>
    internal static class StudioGizmos
    {
        private static readonly Dictionary<string, LineRenderer> lines = [];
        private static Material material;

        public static void Polyline(string id, IReadOnlyList<Vector3> points, bool closed = false, Color? color = null)
        {
            var lr = Get(id);
            if (lr == null) return;
            if (points == null || points.Count < 2) { lr.gameObject.SetActive(false); return; }
            lr.gameObject.SetActive(true);
            lr.loop = closed;
            lr.startColor = lr.endColor = color ?? Color.white;
            lr.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) lr.SetPosition(i, points[i] + Vector3.up * 0.35f);
        }

        public static void Circle(string id, Vector3? center, float radius, Color? color = null)
        {
            if (center is not Vector3 c) { Polyline(id, null); return; }
            var pts = new List<Vector3>(48);
            for (int i = 0; i < 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                var p = c + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                pts.Add(StudioScene.Ground(p));
            }
            Polyline(id, pts, closed: true, color: color);
        }

        private static LineRenderer Get(string id)
        {
            if (StudioScene.Root == null) return null;
            if (lines.TryGetValue(id, out var lr) && lr != null) return lr;
            var go = new GameObject("gizmo " + id);
            go.transform.SetParent(StudioScene.Root, false);
            lr = go.AddComponent<LineRenderer>();
            material ??= new Material(Shader.Find("Sprites/Default")) { renderQueue = 3200 };
            lr.sharedMaterial = material;
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.25f;
            lr.numCapVertices = 2;
            lr.numCornerVertices = 2;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lines[id] = lr;
            return lr;
        }

        public static void Clear()
        {
            foreach (var lr in lines.Values) if (lr != null) UnityEngine.Object.Destroy(lr.gameObject);
            lines.Clear();
        }
    }
}
