using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Per-object steering state (one per unit, vanilla tower or Builder body that has an override), keyed by its
    GameObject. Lives for the current LogicWorld only: a new world drops everything.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    /// <summary>The `movement` block, parsed. Unset fields keep the game's behaviour.</summary>
    internal sealed class MovementDef
    {
        public string Mode = "pathfind";      // pathfind | direct | hold | path
        public JToken Path;
        public string Then = "pathfind";      // pathfind | hold | destroy
        public float? Speed, KeepDistance;
        public float KiteBelow, KiteFor;
        public string FleeWhen, FleeFrom = "foe";
        public JToken FleeTo;
        public float WanderRadius;
        public JToken Home;

        public static readonly string[] Modes = ["pathfind", "direct", "hold", "path"];
        public static readonly string[] Thens = ["pathfind", "hold", "destroy"];

        public static MovementDef Parse(JObject o)
        {
            var m = new MovementDef();
            if (o == null) return m;
            m.Mode = (string)o["mode"] ?? "pathfind";
            if (Array.IndexOf(Modes, m.Mode) < 0) throw new LogicError($"movement mode \"{m.Mode}\": expected one of {string.Join(", ", Modes)}");
            m.Path = o["path"];
            m.Then = (string)o["then"] ?? "pathfind";
            if (Array.IndexOf(Thens, m.Then) < 0) throw new LogicError($"movement then \"{m.Then}\": expected one of {string.Join(", ", Thens)}");
            if (m.Mode == "path" && (m.Path == null || m.Path.Type == JTokenType.Null)) throw new LogicError("movement mode \"path\" needs a path");
            m.Speed = (float?)o["speed"];
            m.KeepDistance = (float?)o["keepDistance"];
            if (o["kite"] is JObject k) { m.KiteBelow = (float?)k["below"] ?? 0; m.KiteFor = (float?)k["for"] ?? 1; }
            if (o["flee"] is JObject f)
            {
                m.FleeWhen = (string)f["when"] ?? "true";
                m.FleeFrom = (string)f["from"] ?? "foe";
                m.FleeTo = f["to"];
                _ = SteerExpr.Expr(m.FleeWhen);
                _ = SteerExpr.Filter(m.FleeFrom);
            }
            if (o["wander"] is JObject w) m.WanderRadius = (float?)w["radius"] ?? 6;
            m.Home = o["home"];
            return m;
        }
    }

    internal sealed class MoveToOrder
    {
        public LogicEntity AtEntity;
        public Vector3 At;
        public float? Speed;
        public string Then = "resume";        // resume | hold
        public Vector3 Point => AtEntity != null && AtEntity.Exists ? AtEntity.Position.ToVector3() : At;
    }

    internal sealed class SteerState
    {
        public LogicEntity Entity;
        public GameObject Go;
        public PathfindMovementEnemy Enemy;
        public PathfindMovementPlayerunit Player;
        public PathfindMovement Movement => (PathfindMovement)Enemy ?? Player;

        public TargetingState Targeting;

        // movement: the template's block, plus setMovement overlays (merged over it)
        public JObject MovementBase;
        public readonly List<(JObject fields, double until)> MovementOverlays = [];
        private MovementDef effective;
        private bool dirty = true;
        public MovementDef Move
        {
            get
            {
                if (dirty || effective == null)
                {
                    var merged = MovementBase != null ? (JObject)MovementBase.DeepClone() : new JObject();
                    foreach (var (fields, _) in MovementOverlays) merged.Merge(fields, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                    try { effective = MovementDef.Parse(merged); }
                    catch (LogicError ex) { SteerLog.Once($"movement {Entity?.Id}", ex.Message); effective = new MovementDef(); }
                    dirty = false;
                    OnMovementChanged();
                }
                return effective;
            }
        }
        public void MovementChanged() => dirty = true;
        public bool HasMovement => MovementBase != null || MovementOverlays.Count > 0 || MoveTo != null;

        // runtime
        public List<Vector3> PathPoints;
        public int PathIndex;
        public bool PathDone;
        public Vector3? HoldPoint;
        public MoveToOrder MoveTo;
        public double KiteUntil;
        public LogicEntity KiteFrom;
        public bool Fleeing;
        public Vector3? WanderPoint;
        public double WanderUntil;
        public bool HomeApplied;
        public bool BaseWritten;              // speed/keepDistance written to the game's fields
        public float DirectRepath;
        public Vector3 DirectDest;
        public TaggedObject DirectTarget;
        public bool DirectWalkingHome;
        public bool Overriding;               // the last destination came from steering (not the game)

        private string pathKey;

        private void OnMovementChanged()
        {
            string key = effective.Mode == "path" ? effective.Path?.ToString(Newtonsoft.Json.Formatting.None) : null;
            if (key != pathKey) { pathKey = key; PathPoints = null; PathIndex = 0; PathDone = false; }
            if (effective.Mode != "hold") HoldPoint = null;
            WanderPoint = null;
            BaseWritten = false;
        }
    }
}
