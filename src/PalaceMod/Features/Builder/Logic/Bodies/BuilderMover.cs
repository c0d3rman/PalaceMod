using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `mover` (docs/builder-engine.md, "Components"): moving platforms, rotating walls, patrols, doors and gates.
    - path: a placed path ("@route": its `points`) or inline [[x,z] | [x,y,z], ...] in world space; mode loop |
      pingpong | once; speed (m/s); wait (s at each point). `arrive` at each point (value = index), `end` at a once
      or pingpong end.
    - spin (deg/s around Y), swing {angle, speed} (swings ±angle around its start heading, like a door left open).
    - poses {name: {rotY, pos: [x,y,z] | offset: [x,y,z]}}: the signal (input) with a pose's name moves there at
      `speed` / `turnSpeed` (default 90 deg/s); a walkable mover snaps.
    - Inputs: start, stop, reverse, goto (value = point index), toggle, and one per pose.
    A solid mover's NavmeshCut follows it (BuilderBody; at most 4 updates a second) and units it would trap are
    pushed out. Time is game time scaled by the body's `timeScale`.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class BuilderMover : MonoBehaviour
    {
        public LogicEntity Entity;
        public List<Vector3> Points = [];
        public string Mode = "loop";
        public float Speed = 3f, Wait, Spin, SwingAngle, SwingSpeed, TurnSpeed = 90f;
        public bool Running = true;
        public readonly Dictionary<string, (float? rotY, Vector3? pos, Vector3? offset)> Poses = [];

        private int index, dirStep = 1;
        private float waitLeft, swingPhase, baseYaw;
        private Vector3 home;
        private (float? rotY, Vector3? pos)? poseTarget;
        private bool ended, snapping;
        private float pushTimer;
        private BuilderBody body;

        public static readonly HashSet<BuilderMover> All = [];

        public void Configure(LogicEntity e, JObject m)
        {
            Entity = e;
            Mode = (string)m["mode"] ?? Mode;
            Speed = m.Value<float?>("speed") ?? Speed;
            Wait = m.Value<float?>("wait") ?? Wait;
            Spin = m.Value<float?>("spin") ?? Spin;
            TurnSpeed = m.Value<float?>("turnSpeed") ?? TurnSpeed;
            if (m["swing"] is JObject sw) { SwingAngle = sw.Value<float?>("angle") ?? 90f; SwingSpeed = sw.Value<float?>("speed") ?? 45f; }
            Running = m.Value<bool?>("running") ?? true;
            PathToken = m["path"];
            var poseObj = m["pose"] as JObject ?? m["poses"] as JObject;
            if (poseObj != null)
                foreach (var kv in poseObj)
                    if (kv.Value is JObject p)
                    {
                        Poses[kv.Key] = (p.Value<float?>("rotY"), V(p["pos"]), V(p["offset"]));
                        BodyActions.RegisterInput(kv.Key);
                    }
        }

        public JToken PathToken;

        private static Vector3? V(JToken t) => t is JArray a && a.Count >= 2
            ? (a.Count == 2 ? new Vector3((float)a[0], float.NaN, (float)a[1]) : new Vector3((float)a[0], (float)a[1], (float)a[2]))
            : null;

        private void Start()
        {
            home = transform.position;
            baseYaw = transform.eulerAngles.y;
            body = GetComponent<BuilderBody>();
            Points = ResolvePath(PathToken);
            if (body != null && body.Solid) BodyNav.UseMoverRate();
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        /// <summary>Points of a placed path ("@name" → its `points`) or an inline list.</summary>
        public static List<Vector3> ResolvePath(JToken path)
        {
            var list = new List<Vector3>();
            JToken pts = path;
            if (path?.Type == JTokenType.String)
            {
                string name = ((string)path).TrimStart('@');
                var e = LogicWorld.Current?.FindNamed(name);
                pts = e?.Def?["points"] ?? e?.Def?["path"];
            }
            if (pts is not JArray arr) return list;
            foreach (var t in arr)
            {
                var v = V(t);
                if (v == null) continue;
                var p = v.Value;
                if (float.IsNaN(p.y)) p.y = BodyGround.Below(new Vector3(p.x, 0f, p.z), out float g) ? g : 0f;
                list.Add(p);
            }
            return list;
        }

        private void Update()
        {
            if (Entity == null) return;
            float dt = Time.deltaTime * BodyTime.Scale(Entity);
            if (dt <= 0f) return;
            bool moved = false;

            if (poseTarget is { } pose)
            {
                moved = true;
                bool posDone = true, rotDone = true;
                if (pose.pos is Vector3 target)
                {
                    transform.position = snapping ? target : Vector3.MoveTowards(transform.position, target, Speed * dt);
                    posDone = (transform.position - target).sqrMagnitude < 1e-4f;
                }
                if (pose.rotY is float yaw)
                {
                    var r = Quaternion.Euler(0f, yaw, 0f);
                    transform.rotation = snapping ? r : Quaternion.RotateTowards(transform.rotation, r, TurnSpeed * dt);
                    rotDone = Quaternion.Angle(transform.rotation, r) < 0.1f;
                }
                if (posDone && rotDone)
                {
                    poseTarget = null;
                    BodyDev.Raise("arrive", Entity, null, -1, transform.position.ToVec());
                    Surfaces.PoseReached(this);
                }
            }
            else if (Running)
            {
                if (Spin != 0f) { transform.Rotate(0f, Spin * dt, 0f); moved = true; }
                if (SwingAngle > 0f && SwingSpeed > 0f)
                {
                    swingPhase += SwingSpeed * dt;
                    float yaw = baseYaw + Mathf.PingPong(swingPhase, 2f * SwingAngle) - SwingAngle;
                    transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    moved = true;
                }
                if (Points.Count > 0 && !ended) moved |= FollowPath(dt);
            }

            if (moved && body != null && body.Solid)
            {
                pushTimer -= Time.deltaTime;
                if (pushTimer <= 0f) { pushTimer = 0.5f; BodyNav.PushOut(BodyVisual.Bounds(gameObject)); }
            }
        }

        private bool FollowPath(float dt)
        {
            if (waitLeft > 0f) { waitLeft -= dt; return false; }
            var target = Points[Mathf.Clamp(index, 0, Points.Count - 1)];
            transform.position = Vector3.MoveTowards(transform.position, target, Speed * dt);
            if ((transform.position - target).sqrMagnitude > 1e-4f) return true;
            BodyDev.Raise("arrive", Entity, null, index, target.ToVec());
            waitLeft = Wait;
            int next = index + dirStep;
            if (next >= Points.Count || next < 0)
            {
                switch (Mode)
                {
                    case "loop": next = dirStep > 0 ? 0 : Points.Count - 1; break;
                    case "pingpong":
                        BodyDev.Raise("end", Entity, null, index, target.ToVec());
                        dirStep = -dirStep;
                        next = index + dirStep;
                        break;
                    default:
                        ended = true;
                        BodyDev.Raise("end", Entity, null, index, target.ToVec());
                        return true;
                }
            }
            index = Mathf.Clamp(next, 0, Points.Count - 1);
            return true;
        }

        // ---- inputs --------------------------------------------------------------------------------------------

        public static readonly string[] BuiltInInputs = ["start", "stop", "reverse", "goto", "toggle"];

        public IEnumerable<string> Inputs => BuiltInInputs.Concat(Poses.Keys);

        public bool Input(string name, double value)
        {
            switch (name)
            {
                case "start": Running = true; ended = false; return true;
                case "stop": Running = false; return true;
                case "toggle": Running = !Running; return true;
                case "reverse": dirStep = -dirStep; index = Mathf.Clamp(index + dirStep, 0, Mathf.Max(0, Points.Count - 1)); ended = false; return true;
                case "goto":
                    if (Points.Count == 0) return true;
                    index = Mathf.Clamp((int)value, 0, Points.Count - 1);
                    ended = false; Running = true;
                    return true;
            }
            if (!Poses.TryGetValue(name, out var p)) return false;
            Vector3? pos = p.pos is Vector3 abs ? new Vector3(abs.x, float.IsNaN(abs.y) ? home.y : abs.y, abs.z) : p.offset is Vector3 off ? home + off : null;
            if (body != null && body.Walkable)
            {
                // Walkable movers snap (the graphs show each pose's pre-scanned deck) and carry their riders.
                Walkables.Snap(this, name, pos ?? transform.position, p.rotY is float y ? Quaternion.Euler(0f, y, 0f) : transform.rotation);
                BodyDev.Raise("arrive", Entity, null, -1, transform.position.ToVec());
                return true;
            }
            poseTarget = (p.rotY, pos);
            snapping = false;
            return true;
        }
    }
}
