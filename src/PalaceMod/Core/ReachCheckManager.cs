using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Dev-only walk test: which build slots the king can physically walk up to, and which other slots (bridges) must be
    built first. Walks the king's own CharacterController over a grid from where he stands (flood fill: step, then
    fall), then checks each slot's interactor collider against PlayerInteraction's radius. Then, for every slot the
    king can reach, builds it (and up to two more that helped), floods again from the changed area and puts
    everything back. This is how the economy planner's reach rules (Features/Economy/PlannerReach.cs) were found.

    A walk that leaves the map's bounds (baseline walk + slots, plus 60 m) is counted as ESCAPED, split by how:
      - pushed: the walk passed through a spot where the king's capsule overlaps solid geometry, i.e. the new
        building appeared on top of him and the controller shoved him out (a real king can only get there by standing
        on the slot while it's built);
      - walked: every step started from free space: a player could walk out the same way.
    The first escapes of each kind are logged with the spot they left from.

    `reach run <out file> [cell] [baseline]` analyses (baseline: only the walk with nothing built and the slots it
    doesn't reach) the loaded level in the background (the report is written as it goes,
    plus <out>.nodes.csv); `reach status` reports progress. `reach walk x y z tx tz` steers the king's controller
    from a point toward a target and says where it ended; `reach colliders x y z r` lists what the king collides with
    there; `reach inspect <slot>` dumps a slot's colliders and upgrade objects; `reach capture <png> <half width>`
    saves a top-down picture with the slots as CSV.
*/
namespace PalaceMod
{
    public class ReachCheckManager : BaseManager
    {
        private static IEnumerator<float> job;
        private static float waitUntil;
        private static string status = "idle";

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            if (Environment.GetEnvironmentVariable("PALACEMOD_DEV") != "1") return;
            DevCommands.Register("reach", "reach run <file> [cell] [baseline] | status | inspect <slot> | walk x y z tx tz | colliders x y z r | capture <png> <hw>   king walk test: slot reachability, bridges, escapes", args =>
            {
                string sub = args.Length > 0 ? args[0] : "status";
                switch (sub)
                {
                    case "run":
                        if (job != null) return "already running: " + status;
                        var a = new Analysis(args[1], args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1f, args.Contains("baseline"));
                        job = a.Run();
                        status = "started";
                        return "started";
                    case "status": return (job != null ? "running: " : "done: ") + status;
                    case "replay":
                    {
                        // The real thing for a "pushed" escape: the king (his own movement on) stands at x y z, the slot is
                        // built on top of him, and after a few seconds we look where he is. Everything is put back.
                        if (job != null) return "already running: " + status;
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        string id = args[1];
                        var slot = Object.FindObjectsOfType<BuildSlot>(true).FirstOrDefault(b => Id(b) == id) ?? throw new ArgumentException("no slot " + id);
                        var at = new Vector3(float.Parse(args[2], ci), float.Parse(args[3], ci), float.Parse(args[4], ci));
                        job = Replay(slot, at);
                        status = "replaying";
                        return "started";
                    }
                    case "inspect": return Inspect(string.Join(" ", args.Skip(1)));
                    case "colliders":
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        var king = PlayerMovement.instance.gameObject;
                        var c0 = new Vector3(float.Parse(args[1], ci), float.Parse(args[2], ci), float.Parse(args[3], ci));
                        var sb = new StringBuilder();
                        foreach (var col in Physics.OverlapSphere(c0, float.Parse(args[4], ci), ~0, QueryTriggerInteraction.Ignore))
                        {
                            if (Physics.GetIgnoreLayerCollision(king.layer, col.gameObject.layer)) continue;
                            var t = col.transform; var path = new List<string>();
                            for (int i = 0; t != null && i < 6; t = t.parent, i++) path.Add(t.name);
                            sb.AppendLine($"{string.Join(" < ", path)} | {col.GetType().Name} layer={LayerMask.LayerToName(col.gameObject.layer)} b={col.bounds.center}/{col.bounds.size} comps=[{string.Join(",", col.GetComponents<Component>().Select(x => x.GetType().Name))}]");
                        }
                        return sb.ToString();
                    }
                    case "walk":
                    {
                        // move the king with its own controller in small steps toward a target, report where it ends
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        var pm = PlayerMovement.instance; var cc = pm.GetComponent<CharacterController>();
                        var from = new Vector3(float.Parse(args[1], ci), float.Parse(args[2], ci), float.Parse(args[3], ci));
                        var to = new Vector3(float.Parse(args[4], ci), 0, float.Parse(args[5], ci));
                        pm.enabled = false;
                        pm.transform.position = from; Physics.SyncTransforms(); cc.Move(Vector3.down * 5);
                        for (int i = 0; i < 400; i++)
                        {
                            var d = to - pm.transform.position; d.y = 0;
                            if (d.magnitude < 0.5f) break;
                            cc.Move(d.normalized * 0.25f); cc.Move(Vector3.down * 2f);
                        }
                        var end = pm.transform.position;
                        pm.enabled = true;
                        return $"ended at {end}";
                    }
                    case "capture":
                    {
                        float hw = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
                        var c = CastleCenter.instance != null ? CastleCenter.instance.transform.position : PlayerMovement.instance.transform.position;
                        var shot = LevelCapture.Take(c, hw, hw, 1600) ?? throw new InvalidOperationException("no picture");
                        LevelCapture.SavePng(shot, args[1]);
                        shot.Release();
                        var sb = new StringBuilder();
                        foreach (var s in Object.FindObjectsOfType<BuildSlot>(true))
                            sb.AppendLine($"{Id(s)},{s.buildingName},{s.transform.position.x:0.0},{s.transform.position.z:0.0}");
                        File.WriteAllText(args[1] + ".slots.csv", sb.ToString());
                        return $"{c.x},{c.z},{shot.Right.x},{shot.Right.z},{shot.Up.x},{shot.Up.z},{hw}";
                    }
                    default: throw new ArgumentException("reach run|status|inspect");
                }
            });
        }

        public override void Update()
        {
            if (job == null || Time.realtimeSinceStartup < waitUntil) return;
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < 250)
                {
                    if (!job.MoveNext()) { job = null; break; }
                    if (job.Current > 0) { waitUntil = Time.realtimeSinceStartup + job.Current; break; }
                }
            }
            catch (Exception e)
            {
                status = "FAILED: " + e;
                job = null;
                Log.Error("reach failed", e);
            }
        }

        private static IEnumerator<float> Replay(BuildSlot slot, Vector3 at)
        {
            var a = new Analysis(null, 1f, false);
            var pm = PlayerMovement.instance;
            a.Init(pm);
            var snap = a.TakeSnapPublic();
            pm.TeleportTo(at);
            yield return 0.5f;
            var before = pm.transform.position;
            Analysis.BuildPublic(slot);
            yield return 2f;
            var sb = new StringBuilder($"{slot.buildingName} {Id(slot)}: king at {V(before)}, built on him, he stays at {V(pm.transform.position)}. Walking out (2 s each, real input):");
            int ground = 1 << LayerMask.NameToLayer("Ground");
            foreach (var (h, v) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
            {
                pm.TeleportTo(before);
                yield return 0.3f;
                DevToolsManager.Execute($"run {h} {v} 2", out _);
                yield return 2.5f;
                var end = pm.transform.position;
                string under = Physics.Raycast(end + Vector3.up * 200f, Vector3.down, out var hit, 400f, ground, QueryTriggerInteraction.Ignore) ? $"{hit.point.y:0.0}" : "none";
                sb.Append($" [{h},{v}] -> {V(end)} ({Vector3.Distance(new Vector3(before.x, 0, before.z), new Vector3(end.x, 0, end.z)):0.0} m, top ground {under})");
            }
            status = "finished: " + sb;
            a.RestorePublic(snap);
            pm.TeleportTo(before);
        }

        private static string V(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";

        private static string Inspect(string name)
        {
            var sb = new StringBuilder();
            foreach (var s in Object.FindObjectsOfType<BuildSlot>(true).Where(s => s.buildingName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || (s.GetComponent<SaveLoadEntity>()?.GUID ?? "") == name))
            {
                sb.AppendLine($"== {s.buildingName} {s.GetComponent<SaveLoadEntity>()?.GUID} at {s.transform.position} active={s.gameObject.activeInHierarchy} level={s.Level}");
                foreach (var c in s.GetComponentsInChildren<Collider>(true))
                    sb.AppendLine($"  collider {Path(c.transform, s.transform)} {c.GetType().Name} layer={LayerMask.LayerToName(c.gameObject.layer)} trig={c.isTrigger} en={c.enabled} act={c.gameObject.activeInHierarchy} b={c.bounds.center}/{c.bounds.size}");
                int ui = 0;
                foreach (var u in s.OwnUpgrades)
                {
                    foreach (var b in u.upgradeBranches)
                        sb.AppendLine($"  upg{ui} on=[{string.Join(",", b.objectsToActivate.Where(o => o).Select(o => o.name))}] off=[{string.Join(",", b.objectsToDisable.Where(o => o).Select(o => o.name))}]");
                    ui++;
                }
                for (int i = 0; i < s.OnUpgrade.GetPersistentEventCount(); i++)
                    sb.AppendLine($"  OnUpgrade -> {s.OnUpgrade.GetPersistentTarget(i)}.{s.OnUpgrade.GetPersistentMethodName(i)}");
            }
            return sb.ToString();
        }

        private static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------------------------------------------------

        private sealed class Reach
        {
            public readonly Dictionary<long, Vector3> Nodes = new();
            /// <summary>Nodes reached only through a spot where the king overlapped solid geometry.</summary>
            public readonly HashSet<long> Pushed = new();
            public Reach Clone()
            {
                var r = new Reach();
                foreach (var kv in Nodes) r.Nodes[kv.Key] = kv.Value;
                r.Pushed.UnionWith(Pushed);
                return r;
            }
        }

        private sealed class Ctx
        {
            public List<BuildSlot> Built = new();
            public Reach R;
            public HashSet<BuildSlot> Interactable;
            public Ctx Parent;
            public int Gain;
            public string Key => string.Join("+", Built.Select(Id).OrderBy(x => x));
        }

        private static string Id(BuildSlot s) => s.GetComponent<SaveLoadEntity>()?.GUID ?? s.name;

        private sealed class Analysis
        {
            private readonly string outFile;
            private readonly float cell;
            private readonly bool baselineOnly;
            private CharacterController cc;
            private Transform king;
            private float radius, yFall;
            private List<BuildSlot> slots;
            private readonly StringBuilder log = new();
            private static readonly Vector3[] Dirs = Enumerable.Range(0, 8).Select(i => Quaternion.Euler(0, i * 45f, 0) * Vector3.forward).ToArray();
            private int probes;

            public Analysis(string outFile, float cell, bool baselineOnly) { this.outFile = outFile; this.cell = cell; this.baselineOnly = baselineOnly; }

            // For replay: the snapshot/build/restore machinery without a walk.
            public void Init(PlayerMovement pm)
            {
                king = pm.transform;
                cc = king.GetComponent<CharacterController>();
                slots = Object.FindObjectsOfType<BuildSlot>(true).ToList();
            }
            public object TakeSnapPublic() => TakeSnap();
            public void RestorePublic(object snap) => Restore((Snap)snap);
            public static void BuildPublic(BuildSlot s) => Build(s);

            private long Key(Vector3 p) =>
                ((long)(Mathf.RoundToInt(p.x / cell) + 32768) << 40) | ((long)(Mathf.RoundToInt(p.z / cell) + 32768) << 20) | (long)(Mathf.RoundToInt(p.y / 3f) + 512);

            private Vector3 Probe(Vector3 from, Vector3 delta)
            {
                probes++;
                king.position = from;
                Physics.SyncTransforms();
                // Small sub-steps, like real frames (~0.25-0.4 m each): one long Move can't climb ramps.
                for (int i = 0; i < 4; i++)
                {
                    cc.Move(delta * 0.25f);
                    cc.Move(Vector3.down * (i == 3 ? 40f : 1f));
                }
                return king.position;
            }

            private int solidMask;
            private string lastOverlap;

            // The king's capsule at p overlaps something he collides with: he's inside geometry, not standing free.
            private bool Overlaps(Vector3 p)
            {
                var c = p + cc.center;
                float half = Mathf.Max(0, cc.height * 0.5f - cc.radius);
                // The king's own colliders (his controller, hitboxes) travel with him: never count those.
                foreach (var hit in Physics.OverlapCapsule(c + Vector3.up * half, c - Vector3.up * half, cc.radius * 0.9f, solidMask, QueryTriggerInteraction.Ignore))
                    if (!hit.transform.IsChildOf(king)) { lastOverlap = hit.name; return true; }
                return false;
            }

            private IEnumerable<float> Flood(Reach r, IEnumerable<Vector3> frontier)
            {
                var q = new Queue<Vector3>(frontier);
                int n = 0;
                while (q.Count > 0)
                {
                    var p = q.Dequeue();
                    bool inside = !r.Pushed.Contains(Key(p)) && Overlaps(p);
                    if (inside && pushOrigins.Count < 3) pushOrigins.Add(V(p));
                    bool pushed = inside || r.Pushed.Contains(Key(p));
                    foreach (var d in Dirs)
                    {
                        var np = Probe(p, d * cell);
                        if (np.y < yFall) continue;
                        var moved = np - p; moved.y = 0;
                        if (moved.magnitude < cell * 0.3f) continue;
                        if (bound.HasValue && !bound.Value.Contains(new Vector3(np.x, 0, np.z)))
                        {
                            if (pushed) escapedPushed++; else escapedWalked++;
                            var list = pushed ? pushedExamples : walkedExamples;
                            if (list.Count < 3) list.Add($"{V(p)}->{V(np)}");
                            continue;
                        }
                        long k = Key(np);
                        if (r.Nodes.ContainsKey(k)) continue;
                        r.Nodes[k] = np;
                        if (pushed) r.Pushed.Add(k);
                        q.Enqueue(np);
                    }
                    if (++n % 200 == 0) { status = $"{statusPrefix} nodes={r.Nodes.Count} queue={q.Count}"; yield return 0; }
                    if (r.Nodes.Count > 600000) throw new InvalidOperationException("too many nodes");
                }
            }

            private string statusPrefix = "";
            private Bounds? bound;
            private int escapedPushed, escapedWalked;
            private readonly List<string> pushedExamples = new(), walkedExamples = new(), pushOrigins = new();

            private static string V(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";

            private static IEnumerable<Collider> InteractColliders(BuildSlot s)
            {
                var bi = s.buildingInteractor != null ? s.buildingInteractor : s.Interactor;
                if (bi == null) return Enumerable.Empty<Collider>();
                return bi.GetComponents<Collider>();
            }

            private static float Dist(Collider c, Vector3 p)
            {
                if (c is BoxCollider b)
                {
                    var t = b.transform;
                    var lp = t.InverseTransformPoint(p) - b.center;
                    var h = b.size * 0.5f;
                    lp = new Vector3(Mathf.Clamp(lp.x, -h.x, h.x), Mathf.Clamp(lp.y, -h.y, h.y), Mathf.Clamp(lp.z, -h.z, h.z));
                    return Vector3.Distance(p, t.TransformPoint(lp + b.center));
                }
                if (c is SphereCollider sc)
                {
                    var t = sc.transform;
                    float rr = sc.radius * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z));
                    return Mathf.Max(0, Vector3.Distance(p, t.TransformPoint(sc.center)) - rr);
                }
                if (c.enabled && c.gameObject.activeInHierarchy) return Vector3.Distance(p, c.ClosestPoint(p));
                return Vector3.Distance(p, c.transform.position);
            }

            private HashSet<BuildSlot> Interactable(Reach r)
            {
                var buckets = new Dictionary<(int, int), List<Vector3>>();
                foreach (var p in r.Nodes.Values)
                {
                    var bk = (Mathf.FloorToInt(p.x / 8f), Mathf.FloorToInt(p.z / 8f));
                    if (!buckets.TryGetValue(bk, out var l)) buckets[bk] = l = new List<Vector3>();
                    l.Add(p);
                }
                var res = new HashSet<BuildSlot>();
                foreach (var s in slots)
                {
                    foreach (var c in InteractColliders(s))
                    {
                        var cp = c.transform.position;
                        int bx = Mathf.FloorToInt(cp.x / 8f), bz = Mathf.FloorToInt(cp.z / 8f);
                        bool hit = false;
                        for (int dx = -4; dx <= 4 && !hit; dx++)
                            for (int dz = -4; dz <= 4 && !hit; dz++)
                                if (buckets.TryGetValue((bx + dx, bz + dz), out var l))
                                    foreach (var p in l)
                                        if (Dist(c, p) <= radius) { hit = true; break; }
                        if (hit) { res.Add(s); break; }
                    }
                }
                return res;
            }

            // ---- build / restore ----

            private sealed class Snap
            {
                public List<(GameObject, bool)> Active = new();
                public List<(Collider, bool)> Enabled = new();
                public HashSet<int> Ids = new();
                public List<(Transform, Vector3, Quaternion, Vector3)> Xf = new();
                public Dictionary<BuildSlot, (int, List<int>, int)> SlotState = new();
                public Dictionary<Collider, Bounds> SolidBounds = new();
            }

            private static IEnumerable<Transform> AllTransforms()
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (!sc.isLoaded) continue;
                    foreach (var root in sc.GetRootGameObjects())
                        foreach (var t in root.GetComponentsInChildren<Transform>(true)) yield return t;
                }
            }

            private Snap TakeSnap()
            {
                var s = new Snap();
                foreach (var t in AllTransforms())
                {
                    s.Active.Add((t.gameObject, t.gameObject.activeSelf));
                    s.Ids.Add(t.gameObject.GetInstanceID());
                    s.Xf.Add((t, t.localPosition, t.localRotation, t.localScale));
                    foreach (var c in t.GetComponents<Collider>())
                    {
                        s.Enabled.Add((c, c.enabled));
                        if (Solid(c)) s.SolidBounds[c] = c.bounds;
                    }
                }
                foreach (var b in slots)
                {
                    var tr = Traverse.Create(b);
                    s.SlotState[b] = (tr.Field("level").GetValue<int>(), new List<int>(b.AppliedUpgrades), b.GoldIncome);
                }
                return s;
            }

            private bool Solid(Collider c) =>
                c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger && !Physics.GetIgnoreLayerCollision(king.gameObject.layer, c.gameObject.layer);

            private void Restore(Snap s)
            {
                foreach (var t in AllTransforms().ToList())
                    if (t != null && !s.Ids.Contains(t.gameObject.GetInstanceID()) && (t.parent == null || s.Ids.Contains(t.parent.gameObject.GetInstanceID())))
                        t.gameObject.SetActive(false); // spawned by the build (units, effects)
                foreach (var (c, en) in s.Enabled) if (c != null && c.enabled != en) c.enabled = en;
                foreach (var (t, p, r, sc) in s.Xf) if (t != null) { t.localPosition = p; t.localRotation = r; t.localScale = sc; }
                foreach (var (go, a) in s.Active) if (go != null && go.activeSelf != a) go.SetActive(a);
                foreach (var kv in s.SlotState)
                {
                    var tr = Traverse.Create(kv.Key);
                    tr.Field("level").SetValue(kv.Value.Item1);
                    kv.Key.AppliedUpgrades.Clear(); kv.Key.AppliedUpgrades.AddRange(kv.Value.Item2);
                    kv.Key.GoldIncome = kv.Value.Item3;
                }
                Physics.SyncTransforms();
            }

            private static void Build(BuildSlot s)
            {
                if (!s.gameObject.activeSelf) s.gameObject.SetActive(true);
                var dependents = s.BuiltSlotsThatRelyOnThisBuilding.Where(d => d != null && d.ActivatorUpgradesThis && d.ActivatorBuilding == s).ToList();
                foreach (var d in dependents) { d.gameObject.SetActive(true); Upgrade(d); }
                Upgrade(s);
            }

            private static void Upgrade(BuildSlot s)
            {
                var tr = Traverse.Create(s);
                int lvl = tr.Field("level").GetValue<int>();
                var up = s.OwnUpgrades[lvl];
                tr.Field("upgradeSelected").SetValue(up);
                s.OnUpgradeChoiceComplete(up.upgradeBranches[0].choiceDetails);
            }

            // Changed solid geometry since the snapshot, as bounds.
            private List<Bounds> Changed(Snap s)
            {
                var res = new List<Bounds>();
                var now = new HashSet<Collider>();
                foreach (var t in AllTransforms())
                    foreach (var c in t.GetComponents<Collider>())
                        if (Solid(c)) { now.Add(c); if (!s.SolidBounds.ContainsKey(c)) res.Add(c.bounds); }
                foreach (var kv in s.SolidBounds) if (kv.Key == null || !now.Contains(kv.Key)) res.Add(kv.Value);
                return res;
            }

            // ---- driver ----

            public IEnumerator<float> Run()
            {
                var pm = PlayerMovement.instance;
                king = pm.transform;
                cc = king.GetComponent<CharacterController>();
                radius = PlayerInteraction.instance != null ? PlayerInteraction.instance.interactionRadius : 3f;
                yFall = pm.yFallThroughMapDetection;
                var kingStart = king.position;
                pm.enabled = false;
                slots = Object.FindObjectsOfType<BuildSlot>(true).ToList();
                for (int layer = 0; layer < 32; layer++)
                    if (!Physics.GetIgnoreLayerCollision(king.gameObject.layer, layer)) solidMask |= 1 << layer;
                solidMask &= ~(1 << king.gameObject.layer);
                log.AppendLine($"level={SceneManager.GetActiveScene().name} cell={cell} radius={radius} cc r={cc.radius} h={cc.height} step={cc.stepOffset} slope={cc.slopeLimit} yFall={yFall} start={kingStart}");
                var sw = Stopwatch.StartNew();
                try
                {
                    // settle on the ground
                    king.position = kingStart; Physics.SyncTransforms(); cc.Move(Vector3.down * 5f);
                    var root = new Ctx { R = new Reach() };
                    root.R.Nodes[Key(king.position)] = king.position;
                    statusPrefix = "baseline";
                    foreach (var w in Flood(root.R, root.R.Nodes.Values.ToList())) yield return w;
                    root.Interactable = Interactable(root.R);
                    var bb = new Bounds(new Vector3(king.position.x, 0, king.position.z), Vector3.zero);
                    foreach (var p in root.R.Nodes.Values) bb.Encapsulate(new Vector3(p.x, 0, p.z));
                    foreach (var s in slots) bb.Encapsulate(new Vector3(s.transform.position.x, 0, s.transform.position.z));
                    bb.Expand(new Vector3(60, 1000, 60));
                    bound = bb;
                    log.AppendLine($"bound={bb.min.x:0},{bb.min.z:0}..{bb.max.x:0},{bb.max.z:0}");
                    // Sanity check for the pushed/walked split: standing nodes should almost never overlap anything.
                    int overlapping = root.R.Nodes.Values.Count(Overlaps);
                    log.AppendLine($"baseline nodes={root.R.Nodes.Count} overlapping={overlapping} (e.g. {lastOverlap}) pushed={root.R.Pushed.Count} escaped={escapedPushed + escapedWalked} probes={probes} t={sw.Elapsed.TotalSeconds:0.0}s");
                    escapedPushed = escapedWalked = 0;
                    pushedExamples.Clear(); walkedExamples.Clear(); pushOrigins.Clear();

                    foreach (var s in slots.OrderBy(s => s.transform.position.x))
                    {
                        var se = s.GetComponent<SaveLoadEntity>();
                        log.AppendLine($"SLOT {Id(s)} | {s.buildingName} | pos=({s.transform.position.x:0},{s.transform.position.z:0}) | level={Traverse.Create(s).Field("level").GetValue<int>()} active={s.gameObject.activeInHierarchy} startDeact={s.StartDeactivated} activator={(s.ActivatorBuilding ? Id(s.ActivatorBuilding) + " " + s.ActivatorBuilding.buildingName + " lvl" + s.ActivatorLevel : "-")} upgThis={s.ActivatorUpgradesThis} | baselineReach={root.Interactable.Contains(s)} interactors={InteractColliders(s).Count()}");
                    }

                    var unreached = slots.Where(x => !root.Interactable.Contains(x)).ToList();
                    log.AppendLine($"UNREACHED {unreached.Count}: {string.Join(", ", unreached.Select(x => $"{x.buildingName} {Id(x)} ({x.transform.position.x:0},{x.transform.position.z:0})"))}");
                    if (baselineOnly)
                    {
                        log.AppendLine($"done (baseline only) probes={probes} t={sw.Elapsed.TotalSeconds:0.0}s");
                        status = "finished " + outFile;
                        yield break;
                    }

                    var all = new List<Ctx> { root };
                    var tested = new HashSet<string>();
                    var helpful = new List<BuildSlot>();
                    var queue = new Queue<Ctx>(); queue.Enqueue(root);
                    while (queue.Count > 0)
                    {
                        var ctx = queue.Dequeue();
                        if (ctx.Built.Count >= 3) continue;
                        IEnumerable<BuildSlot> cands = ctx.Parent == null
                            ? ctx.Interactable
                            : ctx.Interactable.Where(s => !ctx.Parent.Interactable.Contains(s)).Concat(ctx.Built.Count == 1 ? helpful : Enumerable.Empty<BuildSlot>());
                        foreach (var c in cands.Distinct().ToList())
                        {
                            if (ctx.Built.Contains(c) || c.ActivatorUpgradesThis) continue;
                            if (Traverse.Create(c).Field("level").GetValue<int>() != 0) continue;
                            if (c.OwnUpgrades.Count == 0) continue;
                            if (!ctx.Interactable.Contains(c)) continue;
                            var next = new Ctx { Built = ctx.Built.Concat(new[] { c }).ToList(), Parent = ctx };
                            if (!tested.Add(next.Key)) continue;
                            statusPrefix = $"test {next.Built.Count} {c.buildingName}";
                            var snap = TakeSnap();
                            foreach (var b in next.Built) Build(b);
                            yield return 1.5f;
                            Physics.SyncTransforms();
                            var changed = Changed(snap);
                            next.R = ctx.R.Clone();
                            int before = next.R.Nodes.Count;
                            var frontier = next.R.Nodes.Values.Where(p => changed.Any(b => { var bb = b; bb.Expand(2 * cell + 2 * cc.radius + 2f); return bb.Contains(p); })).ToList();
                            foreach (var w in Flood(next.R, frontier)) yield return w;
                            next.Gain = next.R.Nodes.Count - before;
                            int escP = escapedPushed, escW = escapedWalked;
                            string escText = escP + escW == 0 ? "" :
                                $" ESCAPED={escP + escW} (walked {escW}{(walkedExamples.Count > 0 ? " e.g. " + string.Join(" ", walkedExamples) : "")}; " +
                                $"pushed {escP}{(pushedExamples.Count > 0 ? " e.g. " + string.Join(" ", pushedExamples) : "")}" +
                                $"{(escP > 0 && pushOrigins.Count > 0 ? ", inside the building at " + string.Join(" ", pushOrigins) : "")})";
                            int esc = escP + escW;
                            escapedPushed = escapedWalked = 0;
                            pushedExamples.Clear(); walkedExamples.Clear(); pushOrigins.Clear();
                            Restore(snap);
                            yield return 0.3f;
                            next.Interactable = Interactable(next.R);
                            var newly = next.Interactable.Where(s => !ctx.Interactable.Contains(s)).ToList();
                            log.AppendLine($"TEST [{string.Join(" + ", next.Built.Select(b => b.buildingName + "@" + Mathf.RoundToInt(b.transform.position.x) + "," + Mathf.RoundToInt(b.transform.position.z)))}] changedColliders={changed.Count} frontier={frontier.Count} gain={next.Gain}{escText} newlyInteractable=[{string.Join(", ", newly.Select(s => s.buildingName + " " + Id(s)))}]");
                            File.WriteAllText(outFile, log.ToString());
                            if (esc == 0 && (next.Gain >= 60 || newly.Count > 0))
                            {
                                if (ctx.Parent == null) helpful.Add(c);
                                all.Add(next);
                                queue.Enqueue(next);
                            }
                        }
                    }

                    // Requirements: minimal build sets per slot not reachable at baseline.
                    log.AppendLine("=== REQUIREMENTS ===");
                    foreach (var s in slots)
                    {
                        if (root.Interactable.Contains(s)) continue;
                        var sets = all.Where(c => c.Interactable.Contains(s)).Select(c => new HashSet<BuildSlot>(c.Built)).ToList();
                        var minimal = sets.Where(a => !sets.Any(b => b.Count < a.Count && b.IsSubsetOf(a))).Select(a => string.Join(" ", a.Select(Id).OrderBy(x => x))).Distinct().ToList();
                        log.AppendLine($"REQ {Id(s)} | {s.buildingName} | ({s.transform.position.x:0},{s.transform.position.z:0}) | {(minimal.Count == 0 ? "NEVER (within search)" : string.Join("  OR  ", minimal.Select(m => "{" + m + "}")))}");
                    }
                    foreach (var c in all.Skip(1))
                        log.AppendLine($"CTX {c.Key} names=[{string.Join(", ", c.Built.Select(b => b.buildingName))}] nodes={c.R.Nodes.Count} gain={c.Gain}");
                    log.AppendLine($"done probes={probes} t={sw.Elapsed.TotalSeconds:0.0}s");

                    // node dump for plotting
                    using (var w = new StreamWriter(outFile + ".nodes.csv"))
                        foreach (var p in root.R.Nodes.Values) w.WriteLine($"{p.x:0.0},{p.y:0.0},{p.z:0.0}");
                }
                finally
                {
                    king.position = kingStart; Physics.SyncTransforms();
                    pm.enabled = true;
                    File.WriteAllText(outFile, log.ToString());
                }
                status = "finished " + outFile;
            }
        }
    }
}
