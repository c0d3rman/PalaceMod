using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Positions and paths written in component JSON (`movement.path`, `movement.home`, `flee.to`):
    - a position: [x, z] (ground height found on the navmesh) or [x, y, z], or an expression yielding a position or
      an entity (`@exit`, `castle`, `pointNear(self, 5)`);
    - a path: inline [[x, z] | [x, y, z], ...], or "@name" of a placed path, whose `points` are world positions.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal static class SteerPaths
    {
        public static Vector3? Position(JToken t, LogicEntity self)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t is JArray a && a.Count is 2 or 3 && a[0].Type is JTokenType.Integer or JTokenType.Float) return Point(a);
            if (t.Type != JTokenType.String) return null;
            string s = (string)t;
            if (s.StartsWith("@") && LogicWorld.Current?.FindNamed(s.Substring(1)) is { } named) return named.Position.ToVector3();
            if (s == "king" && PlayerInteraction.instance != null) return PlayerInteraction.instance.transform.position;
            if (s == "castle" && CastleCenter.instance != null) return CastleCenter.instance.transform.position;
            if (s == "self" && self != null) return self.Position.ToVector3();
            Value v;
            try { v = SteerExpr.Expr(s)(new LogicContext { World = LogicWorld.Current, Self = self, Subject = self }); }
            catch (LogicError ex) { SteerLog.Once("pos " + s, ex.Message); return null; }
            return v.Kind switch
            {
                ValueKind.Position => v.Pos.ToVector3(),
                ValueKind.Entity => v.AsEntity.Position.ToVector3(),
                ValueKind.Set when v.AsSet.Count > 0 => v.AsSet[0].Position.ToVector3(),
                _ => null,
            };
        }

        private static Vector3 Point(JArray a)
        {
            if (a.Count == 3) return new Vector3((float)a[0], (float)a[1], (float)a[2]);
            var p = new Vector3((float)a[0], 0, (float)a[1]);
            if (Physics.Raycast(p + Vector3.up * 200, Vector3.down, out var hit, 400, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
            return p;
        }

        public static List<Vector3> Points(JToken t, LogicEntity self)
        {
            var list = new List<Vector3>();
            JToken pts = t;
            if (t?.Type == JTokenType.String)
            {
                string s = (string)t;
                var e = s.StartsWith("@") ? LogicWorld.Current?.FindNamed(s.Substring(1)) : LogicWorld.Current?.Find(s);
                pts = e?.Def?["points"] ?? e?.Def?["path"] ?? e?.Def?["shape"]?["points"];
                if (pts == null)
                {
                    string note = self?.Template != null ? $" (movement of template \"{self.Template}\": a shared template that names a level's @path only works in levels that have it)" : "";
                    SteerLog.Once("path " + s, $"path {s}: no placed path with that name on this level (or it has no points){note}");
                    LogicWorld.Current?.ReportError(self, "movement.path", $"path {s}: no placed path with that name on this level{note}");
                    return list;
                }
            }
            if (pts is JArray arr)
                foreach (var p in arr)
                    if (p is JArray pa && pa.Count is 2 or 3) list.Add(Point(pa));
            return list;
        }
    }

    /// <summary>Rule-author errors from steering, logged once each (they'd otherwise repeat every frame).</summary>
    internal static class SteerLog
    {
        private static readonly HashSet<string> seen = [];

        public static void Once(string key, string message)
        {
            if (!seen.Add(key + "|" + message)) return;
            Log.For<LogicSteeringManager>().Warn(message);
        }

        public static void Reset() => seen.Clear();
    }
}
