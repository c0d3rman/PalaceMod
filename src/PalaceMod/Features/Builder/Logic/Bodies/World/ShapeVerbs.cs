using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Pathfinding;
using PalaceMod.Builder.Level;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    Facing and shape at runtime (docs/builder-engine.md, "Light", "Turning and reshaping"): `.facing`, `turn` and
    `setShape`.

    - Facing.Of: where a thing looks. A game unit's root never turns; its model does (RotateForwardToRVOVelocity's
      transformToRotate, turned toward its velocity and its attacks), and the king's mesh parent does
      (PlayerMovement.meshParent). Builder bodies turn their root. `.facing` reads this, so it follows a walking unit.
    - `turn`: bodies turn their root, so an area's shape, its drawn outline/fill, its surface cut, a solid footprint's
      NavmeshCut (useRotationAndScale) and the body's collider turn with it. A unit turns its model and the facing its
      rotator steers toward (it turns back toward where it walks once it moves); the king turns his mesh. Buildings
      don't turn (skipped). With `speed`, it turns at that rate (degrees/s) from the next frames on.
    - `setShape`: an area gets a new AreaShape (outline and fill redrawn, membership from the next 10 Hz poll, its
      surface's cut rebuilt and re-tagged); a body gets a new footprint (circle radius or box) on its NavmeshCut and
      collider, and units the bigger cut would trap are pushed out. `for` puts the old shape back.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class Facing
    {
        private static readonly Dictionary<int, Transform> models = [];
        private static readonly AccessTools.FieldRef<RotateForwardToRVOVelocity, Vector3> rvoForward = AccessTools.FieldRefAccess<RotateForwardToRVOVelocity, Vector3>("desiredForward");
        private static readonly AccessTools.FieldRef<RotateForwardToRVOVelocityRVO, Vector3> rvoForward2 = AccessTools.FieldRefAccess<RotateForwardToRVOVelocityRVO, Vector3>("desiredForward");
        private static readonly AccessTools.FieldRef<PlayerMovement, Quaternion> kingDesired = AccessTools.FieldRefAccess<PlayerMovement, Quaternion>("desiredMeshRotation");

        /// <summary>The transform that shows where it faces: a unit's model, the king's mesh, else the object itself.</summary>
        public static Transform Model(GameObject go)
        {
            if (go == null) return null;
            int id = go.GetInstanceID();
            if (models.TryGetValue(id, out var t) && t != null) return t;
            t = go.transform;
            if (go.GetComponent<PlayerMovement>() is { } king && king.meshParent != null) t = king.meshParent;
            else if (go.GetComponent<BuilderBody>() == null)
            {
                if (go.GetComponentInChildren<RotateForwardToRVOVelocity>() is { } r && r.transformToRotate != null) t = r.transformToRotate;
                else if (go.GetComponentInChildren<RotateForwardToRVOVelocityRVO>() is { } r2 && r2.transformToRotate != null) t = r2.transformToRotate;
            }
            if (models.Count > 4096) models.Clear();
            models[id] = t;
            return t;
        }

        /// <summary>Yaw in degrees, 0 = north (+Z), clockwise.</summary>
        public static float Of(GameObject go)
        {
            var t = Model(go);
            if (t == null) return 0f;
            var f = t.forward;
            if (new Vector2(f.x, f.z).sqrMagnitude < 1e-6f) return t.eulerAngles.y;
            return (float)LightMath.Yaw(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
        }

        /// <summary>Points it at a yaw (see the file comment for what turns).</summary>
        public static void Set(GameObject go, float yaw)
        {
            if (go == null) return;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            if (go.GetComponent<PlayerMovement>() is { } king && king.meshParent != null)
            {
                king.meshParent.rotation = rot;
                kingDesired(king) = rot;
                return;
            }
            if (go.GetComponent<BuilderBody>() != null || go.GetComponent<BuilderArea>() != null) { go.transform.rotation = rot; return; }
            var model = Model(go);
            if (model == go.transform) { go.transform.rotation = rot; return; }
            var fwd = rot * Vector3.forward;
            model.forward = fwd;
            foreach (var r in go.GetComponentsInChildren<RotateForwardToRVOVelocity>()) { rvoForward(r) = fwd; if (r.transformToRotateArach != null) r.transformToRotateArach.forward = fwd; }
            foreach (var r in go.GetComponentsInChildren<RotateForwardToRVOVelocityRVO>()) rvoForward2(r) = fwd;
        }

        public static void Clear() => models.Clear();
    }

    internal static class ShapeVerbs
    {
        private static readonly Log log = Log.For<LogicLightManager>();

        private sealed class Turn { public LogicEntity Entity; public float Target, Speed; }
        private sealed class ShapeRevert { public LogicEntity Entity; public JObject Area, Body; public Until At; }

        private static readonly List<Turn> turns = [];
        private static readonly List<ShapeRevert> reverts = [];
        /// <summary>Entities whose shape a rule changed (savestates keep the current shape).</summary>
        private static readonly HashSet<LogicEntity> reshaped = [];

        public static void Register()
        {
            LogicActions.Register("turn", TurnAction, ActionFlags.PerTarget);
            LogicActions.Register("setShape", SetShape, ActionFlags.PerTarget);
            WorldSession.Reset += () => { turns.Clear(); reverts.Clear(); reshaped.Clear(); Facing.Clear(); };
        }

        // ------------------------------------------------------------------ turn

        private static void TurnAction(LogicContext ctx, ActionParams p)
        {
            var e = p.Target;
            var go = e?.Go();
            if (go == null || !e.Exists) return;
            if (e.Kind is "building" or "castle" || e.Kind == "line") return;   // buildings and spawn lines don't turn
            float cur = Facing.Of(go);
            float target;
            if (p.Has("facing")) target = (float)p.Number("facing");
            else if (p.Has("toward"))
            {
                var at = p.Position("toward");
                if (at == null) return;
                var d = at.Value.ToVector3() - go.transform.position;
                if (new Vector2(d.x, d.z).sqrMagnitude < 1e-4f) return;
                target = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            }
            else if (p.Has("by")) target = cur + (float)p.Number("by");
            else throw new LogicError("turn: needs facing, by or toward");
            target = (float)LightMath.Yaw(target);
            turns.RemoveAll(t => t.Entity == e);
            float speed = (float)p.Number("speed", 0);
            if (speed > 0f && Mathf.Abs((float)LightMath.DeltaAngle(target, cur)) > 0.01f) turns.Add(new Turn { Entity = e, Target = target, Speed = speed });
            else Facing.Set(go, target);
        }

        public static void Tick(float dt)
        {
            for (int i = turns.Count - 1; i >= 0; i--)
            {
                var t = turns[i];
                var go = t.Entity.Go();
                if (go == null || t.Entity.Destroyed) { turns.RemoveAt(i); continue; }
                float cur = Facing.Of(go);
                float delta = (float)LightMath.DeltaAngle(t.Target, cur);
                float step = t.Speed * dt * BodyTime.Scale(t.Entity);
                if (Mathf.Abs(delta) <= step) { Facing.Set(go, t.Target); turns.RemoveAt(i); }
                else Facing.Set(go, cur + Mathf.Sign(delta) * step);
            }
            var world = LogicWorld.Current;
            if (world == null) return;
            for (int i = reverts.Count - 1; i >= 0; i--)
            {
                var r = reverts[i];
                if (!r.At.Due(world.Time, DayEdges.Dawn, DayEdges.Dusk)) continue;
                reverts.RemoveAt(i);
                var go = r.Entity.Go();
                if (go == null) continue;
                try
                {
                    if (r.Area != null && go.GetComponent<BuilderArea>() is { } a) ApplyArea(r.Entity, a, AreaShape.Parse(r.Area));
                    if (r.Body != null && go.GetComponent<BuilderBody>() is { } b) ApplyBody(b, r.Body);
                }
                catch (Exception ex) { log.Warn($"{r.Entity.Id}: putting its shape back: {ex.Message}"); }
            }
        }

        // ------------------------------------------------------------------ setShape

        private static void SetShape(LogicContext ctx, ActionParams p)
        {
            var e = p.Target;
            var go = e?.Go();
            if (go == null || !e.Exists) return;
            var area = go.GetComponent<BuilderArea>();
            var body = go.GetComponent<BuilderBody>();
            string part = p.Has("part") ? p.Text("part") : area != null ? "area" : "body";
            bool doArea = part is "area" or "both", doBody = part is "body" or "both";
            if (doArea && area == null && part == "area") throw new LogicError($"setShape: {e.Id} has no area");
            if (doBody && body == null && part == "body") throw new LogicError($"setShape: {e.Id} has no body");
            var until = Until.From(p, ctx.World);
            JObject oldArea = doArea && area != null ? ShapeJson(area.Shape) : null, oldBody = doBody && body != null ? BodyJson(body) : null;
            if (doArea && area != null)
            {
                var shape = NewShape(area.Shape, p);
                ApplyArea(e, area, shape);
            }
            if (doBody && body != null)
            {
                var cur = BodyJson(body);
                string kind = p.Has("shape") ? p.Text("shape") : cur["box"] != null ? "rect" : "circle";
                var o = new JObject();
                if (kind == "rect")
                {
                    var box = cur["box"] as JArray;
                    float w = p.Has("width") ? (float)p.Number("width") : box != null ? (float)box[0] : body.Radius * 2f;
                    float d = p.Has("depth") ? (float)p.Number("depth") : box != null ? (float)box[1] : body.Radius * 2f;
                    o["box"] = new JArray(Mathf.Max(0.1f, w), Mathf.Max(0.1f, d));
                }
                else if (kind == "circle") o["radius"] = Mathf.Max(0.1f, p.Has("radius") ? (float)p.Number("radius") : body.Radius);
                else throw new LogicError($"setShape: a body's footprint is a circle or a rect, not a {kind}");
                ApplyBody(body, o);
            }
            reshaped.Add(e);
            if (until.Set)
            {
                reverts.RemoveAll(r => r.Entity == e);
                reverts.Add(new ShapeRevert { Entity = e, Area = oldArea, Body = oldBody, At = until });
            }
        }

        private static AreaShape NewShape(AreaShape cur, ActionParams p)
        {
            string kind = p.Has("shape") ? p.Text("shape")
                : cur.SectorAngle > 0 ? "sector" : cur.Radius > 0 ? "circle" : cur.Points != null ? "polygon" : "rect";
            float CurRadius() => cur.SectorAngle > 0 ? cur.SectorRadius : cur.Radius > 0 ? cur.Radius : cur.Reach;
            switch (kind)
            {
                case "circle":
                    return new AreaShape { Radius = Mathf.Max(0.1f, p.Has("radius") ? (float)p.Number("radius") : CurRadius()) };
                case "sector":
                    return AreaShape.Sector(Mathf.Max(0.1f, p.Has("radius") ? (float)p.Number("radius") : CurRadius()),
                        Mathf.Clamp(p.Has("angle") ? (float)p.Number("angle") : cur.SectorAngle > 0 ? cur.SectorAngle : 60f, 1f, 360f));
                case "rect":
                {
                    var size = cur.Radius <= 0 && cur.Points == null ? cur.Size : Vector2.one * CurRadius() * 2f;
                    return new AreaShape { Size = new Vector2(Mathf.Max(0.1f, p.Has("width") ? (float)p.Number("width") : size.x), Mathf.Max(0.1f, p.Has("depth") ? (float)p.Number("depth") : size.y)) };
                }
                case "polygon":
                {
                    var pts = p.Raw("points") as JArray;
                    if (pts == null)
                    {
                        if (cur.Points != null && cur.SectorAngle <= 0) return cur;
                        throw new LogicError("setShape: a polygon needs points [[x, z], ...]");
                    }
                    if (pts.Count < 3) throw new LogicError("setShape: a polygon needs at least 3 points");
                    return new AreaShape { Points = pts.Select(t => new Vector2((float)t[0], (float)t[t.Count() > 2 ? 2 : 1])).ToArray() };
                }
            }
            throw new LogicError($"setShape: shape \"{kind}\" isn't circle, rect, polygon or sector");
        }

        private static void ApplyArea(LogicEntity e, BuilderArea area, AreaShape shape)
        {
            if (shape == null) return;
            area.Shape = shape;
            area.SetShow(area.Show);   // redraws the outline or fill
            var surface = e.Def?["area"]?["surface"];
            if (surface != null && surface.Type != JTokenType.Null)
            {
                Surfaces.Remove(area);
                Surfaces.Add(area, surface);
            }
        }

        private static void ApplyBody(BuilderBody b, JObject o)
        {
            if (o["box"] is JArray box && box.Count == 2) b.Box = new Vector2((float)box[0], (float)box[1]);
            else if (o["radius"] is JToken r) { b.Box = null; b.Radius = (float)r; }
            if (b.Cut != null)
            {
                if (b.Box is Vector2 bb) { b.Cut.type = NavmeshCut.MeshType.Rectangle; b.Cut.rectangleSize = bb; }
                else { b.Cut.type = NavmeshCut.MeshType.Circle; b.Cut.circleRadius = b.Radius; }
                b.Cut.ForceUpdate();
            }
            var holder = b.transform.Find(LayoutScene.BlockerName);
            if (holder != null)
            {
                foreach (var c in holder.GetComponents<Collider>()) Object.Destroy(c);
                if (b.Box is Vector2 bx)
                {
                    var c = holder.gameObject.AddComponent<BoxCollider>();
                    c.size = new Vector3(bx.x, b.Height, bx.y);
                    c.center = new Vector3(0f, b.Height / 2f, 0f);
                }
                else
                {
                    var c = holder.gameObject.AddComponent<CapsuleCollider>();
                    c.radius = b.Radius;
                    c.height = Mathf.Max(b.Height, b.Radius * 2f);
                    c.center = new Vector3(0f, c.height / 2f, 0f);
                }
            }
            if (b.Solid)
            {
                float reach = b.Box is Vector2 v ? v.magnitude / 2f : b.Radius;
                BodyNav.PushOut(new Bounds(b.transform.position, new Vector3(reach * 2f, b.Height, reach * 2f)));
            }
        }

        // ------------------------------------------------------------------ shapes as JSON (reverts, savestates)

        public static JObject ShapeJson(AreaShape s)
        {
            if (s.SectorAngle > 0) return new JObject { ["sector"] = new JObject { ["radius"] = s.SectorRadius, ["angle"] = s.SectorAngle } };
            if (s.Radius > 0) return new JObject { ["circle"] = new JObject { ["radius"] = s.Radius } };
            if (s.Points != null) return new JObject { ["polygon"] = new JObject { ["points"] = new JArray(s.Points.Select(q => new JArray(q.x, q.y))) } };
            return new JObject { ["rect"] = new JObject { ["size"] = new JArray(s.Size.x, s.Size.y) } };
        }

        private static JObject BodyJson(BuilderBody b) =>
            b.Box is Vector2 v ? new JObject { ["box"] = new JArray(v.x, v.y) } : new JObject { ["radius"] = b.Radius };

        public static JToken Capture(LogicWorld w)
        {
            var o = new JObject();
            foreach (var e in reshaped)
            {
                var go = e.Go();
                if (go == null || e.Destroyed) continue;
                var j = new JObject();
                if (go.GetComponent<BuilderArea>() is { } a) j["area"] = ShapeJson(a.Shape);
                if (go.GetComponent<BuilderBody>() is { } b) j["body"] = BodyJson(b);
                o[e.Id] = j;
            }
            return o;
        }

        public static void Restore(LogicWorld w, JToken state)
        {
            WorldSession.Sync();
            if (state is not JObject all) return;
            foreach (var kv in all)
            {
                var e = w.Find(kv.Key);
                var go = e?.Go();
                if (go == null || kv.Value is not JObject j) continue;
                if (j["area"] is JObject sa && go.GetComponent<BuilderArea>() is { } a) ApplyArea(e, a, AreaShape.Parse(sa));
                if (j["body"] is JObject sb && go.GetComponent<BuilderBody>() is { } b) ApplyBody(b, sb);
                reshaped.Add(e);
            }
        }

        public static string Status() => $"turning: {turns.Count}, reshaped: {reshaped.Count}, shape reverts: {reverts.Count}";
    }
}
