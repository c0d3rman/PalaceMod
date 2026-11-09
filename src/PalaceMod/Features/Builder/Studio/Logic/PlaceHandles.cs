using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    Shapes on the map, by mouse:

    - Handles on the one selected place: a path's points, an area's circle edge, rectangle corners or polygon corners.
      Drag one to move it (one undo step on release); Ctrl-click a point or corner to remove it; press a side's middle
      (the small ring) to add a point there and drag it. The Select tool asks PlaceHandles first (Studio.AddPointerHandler).
    - Drawing with the Place tool (StudioPlaceable.Draw): a circle area is a drag from its centre, a rectangle a drag
      from corner to corner, a polygon clicks its corners (click the first again to close), a path clicks its points
      (click the last again to end). Esc drops the shape in progress.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class PlaceHandles : IStudioOverlay
    {
        public static readonly PlaceHandles Instance = new();

        private static readonly Color HandleColor = new(1f, 0.95f, 0.75f);
        private static readonly Color MidColor = new(1f, 1f, 1f, 0.55f);

        private enum HKind { PathPoint, PathMid, Radius, Corner, PolyPoint, PolyMid }

        private struct Handle
        {
            public HKind Kind;
            public int Index;
            public Vector3 World;
        }

        private static readonly HashSet<string> drawn = [];
        private static string dragKey;
        private static Handle drag;
        private static Vector3 dragAt;

        public static bool Dragging => dragKey != null;

        // ---- Geometry ----------------------------------------------------------------------------------------------

        private static float Radius => 0.4f * StudioScene.PickRadius;

        private static (string Key, LogicOwner Owner, JObject Place, JObject Area)? Target()
        {
            var sel = Studio.Selection;
            if (sel.Count != 1 || !sel[0].StartsWith(PlaceItems.Prefix)) return null;
            var owner = LogicOwner.ForItem(sel[0]);
            var place = owner?.Read();
            if (place == null) return null;
            return (sel[0], owner, place, owner.Effective()?["area"] as JObject);
        }

        private static Vector3 V(JArray p) => p.Count >= 3 ? new Vector3((float)p[0], (float)p[1], (float)p[2]) : new Vector3((float)p[0], 0f, (float)p[1]);

        private static Vector3 Ground(Vector3 v) => StudioScene.Ground(v);

        private static List<Handle> Handles(JObject place, JObject area)
        {
            var list = new List<Handle>();
            if (place["points"] is JArray pts && pts.Count >= 1)
            {
                var ws = pts.OfType<JArray>().Select(V).ToList();
                for (int i = 0; i < ws.Count; i++) list.Add(new Handle { Kind = HKind.PathPoint, Index = i, World = ws[i] });
                for (int i = 0; i + 1 < ws.Count; i++) list.Add(new Handle { Kind = HKind.PathMid, Index = i + 1, World = Ground((ws[i] + ws[i + 1]) / 2f) });
                return list;
            }
            var shape = area?["shape"] as JObject;
            if (shape == null) return list;
            var origin = PlaceItems.Pos(place);
            var rot = Quaternion.Euler(0f, (float?)place["rotY"] ?? 0f, 0f);
            Vector3 W(float x, float z) => Ground(origin + rot * new Vector3(x, 0f, z));
            if (shape["circle"]?["radius"] is JToken r && r.Type is JTokenType.Integer or JTokenType.Float)
                list.Add(new Handle { Kind = HKind.Radius, World = W((float)r, 0f) });
            else if (shape["rect"]?["size"] is JArray size && size.Count >= 2)
            {
                float w = (float)size[0] / 2f, d = (float)size[1] / 2f;
                int i = 0;
                foreach (var (x, z) in new[] { (-w, -d), (w, -d), (w, d), (-w, d) }) list.Add(new Handle { Kind = HKind.Corner, Index = i++, World = W(x, z) });
            }
            else if (shape["sector"] is JObject sec)
            {
                // One handle on the cone's right edge: drag it out for length, round for width.
                float sr = (float?)sec["radius"] ?? 6f, half = ((float?)sec["angle"] ?? 60f) * Mathf.Deg2Rad / 2f;
                list.Add(new Handle { Kind = HKind.Corner, Index = 0, World = W(Mathf.Sin(half) * sr, Mathf.Cos(half) * sr) });
            }
            else if (shape["polygon"]?["points"] is JArray poly && poly.Count >= 3)
            {
                var ls = poly.OfType<JArray>().Select(p => new Vector2((float)p[0], (float)p[p.Count - 1])).ToList();
                for (int i = 0; i < ls.Count; i++) list.Add(new Handle { Kind = HKind.PolyPoint, Index = i, World = W(ls[i].x, ls[i].y) });
                for (int i = 0; i < ls.Count; i++)
                {
                    var m = (ls[i] + ls[(i + 1) % ls.Count]) / 2f;
                    list.Add(new Handle { Kind = HKind.PolyMid, Index = i + 1, World = W(m.x, m.y) });
                }
            }
            return list;
        }

        private static bool IsMid(HKind k) => k is HKind.PathMid or HKind.PolyMid;

        // ---- Overlay -----------------------------------------------------------------------------------------------

        public void Draw()
        {
            var shown = new HashSet<string>();
            var t = Studio.ToolId == "select" ? Target() : null;
            if (t is { } target)
            {
                var hs = Handles(target.Place, target.Area);
                if (Dragging && dragKey == target.Key) hs = Moved(hs, target.Place, target.Area);
                for (int i = 0; i < hs.Count; i++)
                {
                    var h = hs[i];
                    if (Dragging && IsMid(h.Kind)) continue;
                    string id = "handle " + i;
                    StudioGizmos.Circle(id, h.World, IsMid(h.Kind) ? Radius * 0.45f : Radius * 0.7f, IsMid(h.Kind) ? MidColor : HandleColor);
                    shown.Add(id);
                }
                if (Dragging && dragKey == target.Key) { var line = Preview(target.Place, target.Area); if (line != null) { StudioGizmos.Polyline("handle preview", line, closed: target.Place["points"] == null, color: HandleColor); shown.Add("handle preview"); } }
            }
            foreach (var id in drawn.Where(d => !shown.Contains(d)).ToList()) StudioGizmos.Polyline(id, null);
            drawn.Clear();
            drawn.UnionWith(shown);
        }

        public void Hide()
        {
            foreach (var id in drawn) StudioGizmos.Polyline(id, null);
            drawn.Clear();
            dragKey = null;
        }

        /// <summary>The handles with the dragged one at the pointer.</summary>
        private static List<Handle> Moved(List<Handle> hs, JObject place, JObject area)
        {
            var copy = (JObject)place.DeepClone();
            var a = area?.DeepClone() as JObject;
            Apply(copy, a, drag, dragAt);
            return Handles(copy, a);
        }

        private static List<Vector3> Preview(JObject place, JObject area)
        {
            var copy = (JObject)place.DeepClone();
            var a = area?.DeepClone() as JObject;
            Apply(copy, a, drag, dragAt);
            if (copy["points"] is JArray pts) return pts.OfType<JArray>().Select(V).ToList();
            var outline = PlaceItems.Outline(new JObject { ["area"] = a });
            if (outline == null) return null;
            var origin = PlaceItems.Pos(place);
            var rot = Quaternion.Euler(0f, (float?)place["rotY"] ?? 0f, 0f);
            return outline.Select(v => Ground(origin + rot * v)).ToList();
        }

        /// <summary>Moves a handle to a ground point in a place (and its area part).</summary>
        private static void Apply(JObject place, JObject area, Handle h, Vector3 g)
        {
            var origin = PlaceItems.Pos(place);
            var inv = Quaternion.Euler(0f, -((float?)place["rotY"] ?? 0f), 0f);
            var local = inv * (g - origin);
            static double R(float v) => Math.Round(v, 2);
            switch (h.Kind)
            {
                case HKind.PathPoint when (place["points"] is JArray pts && h.Index < pts.Count):
                    pts[h.Index] = new JArray(R(g.x), R(g.y), R(g.z));
                    break;
                case HKind.Radius when (area?["shape"]?["circle"] is JObject c):
                    c["radius"] = R(Mathf.Max(0.5f, new Vector2(local.x, local.z).magnitude));
                    break;
                case HKind.Corner when (area?["shape"]?["sector"] is JObject sec):
                    sec["radius"] = R(Mathf.Max(0.5f, new Vector2(local.x, local.z).magnitude));
                    sec["angle"] = R(Mathf.Clamp(2f * Mathf.Abs(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg), 1f, 360f));
                    break;
                case HKind.Corner when (area?["shape"]?["rect"] is JObject rect):
                    rect["size"] = new JArray(R(Mathf.Max(0.5f, 2f * Mathf.Abs(local.x))), R(Mathf.Max(0.5f, 2f * Mathf.Abs(local.z))));
                    break;
                case HKind.PolyPoint when (area?["shape"]?["polygon"]?["points"] is JArray poly && h.Index < poly.Count):
                    poly[h.Index] = new JArray(R(local.x), R(local.z));
                    break;
            }
        }

        // ---- Pointer -------------------------------------------------------------------------------------------------

        /// <summary>The Select tool's first look at the pointer: true while it's working a handle.</summary>
        public static bool HandlePointer(StudioPointer p)
        {
            if (Dragging)
            {
                if (p.Ground is Vector3 g) dragAt = Ground(p.Alt || p.Snapped == null ? g : p.Snapped.Value);
                if (p.LeftUp || !p.LeftHeld) Commit();
                return true;
            }
            if (!p.LeftDown || p.OverUI || p.Shift || p.Ground is not Vector3 at) return false;
            if (Target() is not { } t) return false;
            Handle? hit = null;
            float best = Radius;
            foreach (var h in Handles(t.Place, t.Area))
            {
                float d = new Vector2(h.World.x - at.x, h.World.z - at.z).magnitude * (IsMid(h.Kind) ? 1.4f : 1f);
                if (d < best) { best = d; hit = h; }
            }
            if (hit is not Handle handle) return false;
            if (p.Ctrl)
            {
                if (!IsMid(handle.Kind)) Remove(t.Owner, handle);
                return true;
            }
            if (IsMid(handle.Kind))
            {
                // A new point in the middle of a side, dragged from there.
                Insert(t.Owner, handle);
                handle = new Handle { Kind = handle.Kind == HKind.PathMid ? HKind.PathPoint : HKind.PolyPoint, Index = handle.Index, World = handle.World };
            }
            dragKey = t.Key;
            drag = handle;
            dragAt = handle.World;
            return true;
        }

        public static bool Cancel()
        {
            if (!Dragging) return false;
            dragKey = null;
            return true;
        }

        private static void Commit()
        {
            string key = dragKey;
            dragKey = null;
            var owner = LogicOwner.ForItem(key);
            var place = owner?.Read();
            if (place == null) return;
            var was = Handles(place, owner.Effective()?["area"] as JObject).FirstOrDefault(h => h.Kind == drag.Kind && h.Index == drag.Index);
            if (new Vector2(was.World.x - dragAt.x, was.World.z - dragAt.z).sqrMagnitude < 0.0025f) return;
            var h = drag;
            var at = dragAt;
            string label = h.Kind switch { HKind.PathPoint => "Move point", HKind.Radius => "Set radius", HKind.Corner => "Set size", _ => "Move corner" };
            if (h.Kind == HKind.PathPoint) owner.Edit(label, n => Apply(n, null, h, at));
            else PartsPanel.EditPart(owner, "area", label, a => Apply(owner.Read(), a, h, at));
        }

        private static void Remove(LogicOwner owner, Handle h)
        {
            if (h.Kind == HKind.PathPoint)
                owner.Edit("Remove point", n => { if (n["points"] is JArray pts && pts.Count > 2) pts.RemoveAt(h.Index); else Notify.Show("A path needs two points", NotifyKind.Info, 2f, "logic"); });
            else if (h.Kind == HKind.PolyPoint)
                PartsPanel.EditPart(owner, "area", "Remove corner", a => { if (a["shape"]?["polygon"]?["points"] is JArray poly && poly.Count > 3) poly.RemoveAt(h.Index); else Notify.Show("A polygon needs three corners", NotifyKind.Info, 2f, "logic"); });
        }

        private static void Insert(LogicOwner owner, Handle h)
        {
            if (h.Kind == HKind.PathMid)
                owner.Edit("Add point", n => { if (n["points"] is JArray pts) pts.Insert(h.Index, new JArray(Math.Round(h.World.x, 2), Math.Round(h.World.y, 2), Math.Round(h.World.z, 2))); });
            else
            {
                var place = owner.Read();
                var origin = PlaceItems.Pos(place);
                var local = Quaternion.Euler(0f, -((float?)place["rotY"] ?? 0f), 0f) * (h.World - origin);
                PartsPanel.EditPart(owner, "area", "Add corner", a => { if (a["shape"]?["polygon"]?["points"] is JArray poly) poly.Insert(h.Index, new JArray(Math.Round(local.x, 2), Math.Round(local.z, 2))); });
            }
        }
    }

    /// <summary>Areas and paths drawn with the Place tool.</summary>
    internal sealed class ShapeDrawer
    {
        public enum Shape { Circle, Rect, Polygon, Path }

        private readonly Shape shape;
        private readonly List<Vector3> points = [];
        private Vector3? start;

        public ShapeDrawer(Shape shape) { this.shape = shape; }

        private static readonly Color Ink = new(1f, 0.95f, 0.75f);
        private const string Gizmo = "shape draw", Cursor = "shape cursor";

        public bool Cancel()
        {
            bool was = points.Count > 0 || start != null;
            points.Clear();
            start = null;
            StudioGizmos.Polyline(Gizmo, null);
            StudioGizmos.Polyline(Cursor, null);
            return was;
        }

        private static float Close => 0.5f * StudioScene.PickRadius;

        public string Update(StudioPointer p)
        {
            var at = p.OverUI ? null : p.Alt ? p.Ground : p.Snapped ?? p.Ground;
            if (at is Vector3 cur) at = StudioScene.Ground(cur);
            switch (shape)
            {
                case Shape.Circle:
                case Shape.Rect:
                    if (p.LeftDown && at != null) start = at;
                    if (start is Vector3 a)
                    {
                        var b = at ?? a;
                        StudioGizmos.Polyline(Gizmo, Outline(a, b), closed: true, color: Ink);
                        if (p.LeftUp || !p.LeftHeld)
                        {
                            start = null;
                            StudioGizmos.Polyline(Gizmo, null);
                            return Make(a, b);
                        }
                    }
                    else StudioGizmos.Circle(Cursor, at, 0.4f, Ink);
                    return null;
                default:
                {
                    StudioGizmos.Circle(Cursor, at, 0.4f, Ink);
                    if (points.Count > 0 && at is Vector3 c)
                        StudioGizmos.Polyline(Gizmo, points.Append(c).ToList(), closed: false, color: Ink);
                    if (!p.Click || at is not Vector3 pt) return null;
                    float Dist(Vector3 x) => new Vector2(x.x - pt.x, x.z - pt.z).magnitude;
                    if (shape == Shape.Polygon && points.Count >= 3 && Dist(points[0]) < Close) return Finish();
                    if (shape == Shape.Path && points.Count >= 2 && Dist(points[points.Count - 1]) < Close) return Finish();
                    if (points.Count == 0 || Dist(points[points.Count - 1]) >= 0.3f) points.Add(pt);
                    return null;
                }
            }
        }

        private List<Vector3> Outline(Vector3 a, Vector3 b)
        {
            if (shape == Shape.Circle)
            {
                float r = Mathf.Max(0.5f, new Vector2(b.x - a.x, b.z - a.z).magnitude);
                return Enumerable.Range(0, 48).Select(i => StudioScene.Ground(a + new Vector3(Mathf.Cos(i / 48f * Mathf.PI * 2f) * r, 0f, Mathf.Sin(i / 48f * Mathf.PI * 2f) * r))).ToList();
            }
            return [StudioScene.Ground(new Vector3(a.x, 0, a.z)), StudioScene.Ground(new Vector3(b.x, 0, a.z)), StudioScene.Ground(new Vector3(b.x, 0, b.z)), StudioScene.Ground(new Vector3(a.x, 0, b.z))];
        }

        private static double R(float v) => Math.Round(v, 2);

        private static JObject AreaPlace(JObject shapeJson) => new()
        {
            ["kind"] = "area",
            ["area"] = new JObject { ["shape"] = shapeJson, ["watch"] = "unit", ["show"] = "outline" },
        };

        private string Make(Vector3 a, Vector3 b)
        {
            StudioGizmos.Polyline(Cursor, null);
            if (shape == Shape.Circle)
            {
                float r = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (r < 0.5f) r = 4f;
                return PlaceItems.Add("Area", "area", AreaPlace(new JObject { ["circle"] = new JObject { ["radius"] = R(r) } }), a, 0f);
            }
            float w = Mathf.Abs(b.x - a.x), d = Mathf.Abs(b.z - a.z);
            var centre = StudioScene.Ground((a + b) / 2f);
            if (w < 0.5f && d < 0.5f) { w = 6f; d = 4f; centre = a; }
            return PlaceItems.Add("Area", "area", AreaPlace(new JObject { ["rect"] = new JObject { ["size"] = new JArray(R(Mathf.Max(0.5f, w)), R(Mathf.Max(0.5f, d))) } }), centre, 0f);
        }

        private string Finish()
        {
            var pts = points.ToList();
            Cancel();
            if (shape == Shape.Polygon)
            {
                var c = new Vector3(pts.Average(p => p.x), 0f, pts.Average(p => p.z));
                c = StudioScene.Ground(c);
                var local = new JArray(pts.Select(p => new JArray(R(p.x - c.x), R(p.z - c.z))));
                return PlaceItems.Add("Area", "area", AreaPlace(new JObject { ["polygon"] = new JObject { ["points"] = local } }), c, 0f);
            }
            var origin = pts[0];
            var rel = new JArray(pts.Select(p => new JArray(R(p.x - origin.x), R(p.y - origin.y), R(p.z - origin.z))));
            return PlaceItems.Add("Path", "path", new JObject { ["kind"] = "path", ["points"] = rel }, origin, 0f);
        }
    }
}
