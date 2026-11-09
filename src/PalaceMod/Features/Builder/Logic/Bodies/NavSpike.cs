using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using Pathfinding;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    L2 spikes (docs/builder-engine.md, "L2"): moving NavmeshCuts on the host's ground graphs, the cost of
    GroundGraphs.Scan, and node tags for per-unit walkability. Dev only (`logic-l2 nav ...`).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class NavSpike
    {
        private static readonly Dictionary<string, GameObject> cuts = [];
        public static readonly Stopwatch CutTime = new();
        public static int CutReloads;
        private static readonly Dictionary<string, (Vector3 dir, float speed, float spin)> moving = [];

        public static string Run(string[] a)
        {
            var astar = AstarPath.active;
            if (astar == null) return "no AstarPath";
            string sub = a.Length > 0 ? a[0] : "";
            var sb = new StringBuilder();
            switch (sub)
            {
                case "scan":
                {
                    int n = a.Length > 1 ? int.Parse(a[1]) : 3;
                    var times = new List<long>();
                    for (int i = 0; i < n; i++) times.Add(GroundGraphs.Scan());
                    return $"GroundGraphs.Scan x{n}: {string.Join(", ", times)} ms; graphs: {Graphs()}";
                }
                case "cutting":
                {
                    bool on = a.Length < 2 || a[1] != "off";
                    var sw = Stopwatch.StartNew();
                    foreach (var g in astar.data.graphs.OfType<RecastGraph>())
                        if (g.name != "Enemy Units Air") g.enableNavmeshCutting = on;
                    astar.navmeshUpdates.ForceUpdate();
                    astar.FlushWorkItems();
                    return $"cutting {on}: {sw.ElapsedMilliseconds} ms; interval {astar.navmeshUpdates.updateInterval}";
                }
                case "cut":
                {
                    // cut <id> <x> <z> <w> <d> [dual]
                    string id = a[1];
                    var pos = new Vector3(F(a[2]), 0, F(a[3]));
                    pos.y = astar.GetNearest(pos).position.y;
                    var go = new GameObject("L2 spike cut " + id);
                    go.transform.position = pos;
                    var cut = go.AddComponent<NavmeshCut>();
                    cut.type = NavmeshCut.MeshType.Box;
                    cut.rectangleSize = new Vector2(F(a[4]), F(a[5]));
                    cut.height = 6f;
                    cut.useRotationAndScale = true;
                    cut.isDual = a.Length > 6 && a[6] == "dual";
                    if (cuts.TryGetValue(id, out var old) && old != null) Object.Destroy(old);
                    cuts[id] = go;
                    var sw = Stopwatch.StartNew();
                    astar.navmeshUpdates.ForceUpdate();
                    astar.FlushWorkItems();
                    return $"cut {id} at {pos}: {sw.ElapsedMilliseconds} ms";
                }
                case "move":
                {
                    // move <id> <dx> <dz> [rotY]
                    var go = cuts[a[1]];
                    go.transform.position += new Vector3(F(a[2]), 0, F(a[3]));
                    if (a.Length > 4) go.transform.rotation = Quaternion.Euler(0, F(a[4]), 0);
                    var sw = Stopwatch.StartNew();
                    astar.navmeshUpdates.ForceUpdate();
                    astar.FlushWorkItems();
                    return $"moved {a[1]} to {go.transform.position}: {sw.ElapsedMilliseconds} ms";
                }
                case "glide":
                {
                    // glide <id> <dx> <dz> <speed m/s> [spin deg/s]: moves continuously (Tick), the navmesh updater follows
                    moving[a[1]] = (new Vector3(F(a[2]), 0, F(a[3])).normalized, F(a[4]), a.Length > 5 ? F(a[5]) : 0f);
                    CutTime.Reset(); CutReloads = 0; started = Time.time;
                    return "gliding";
                }
                case "stop":
                    moving.Clear();
                    return $"cut reloads {CutReloads} in {Time.time - started:0.0} s, {CutTime.ElapsedMilliseconds} ms total, {(CutReloads > 0 ? CutTime.ElapsedMilliseconds / (float)CutReloads : 0):0.0} ms each";
                case "interval":
                    astar.navmeshUpdates.updateInterval = F(a[1]);
                    return $"interval {astar.navmeshUpdates.updateInterval}";
                case "clear":
                    foreach (var go in cuts.Values) if (go != null) Object.Destroy(go);
                    cuts.Clear(); moving.Clear();
                    return "cleared";
                case "stuck":
                {
                    // units whose position is far from walkable ground on their own graphs
                    int total = 0, off = 0;
                    foreach (var m in Object.FindObjectsOfType<PathfindMovementEnemy>())
                    {
                        total++;
                        var nn = NearestNodeConstraint.Walkable;
                        nn.graphMask = m.GetComponent<Seeker>().graphMask;
                        var hit = astar.GetNearest(m.transform.position, nn);
                        float d = hit.node == null ? 999 : Vector2.Distance(new Vector2(hit.position.x, hit.position.z), new Vector2(m.transform.position.x, m.transform.position.z));
                        if (d > 0.3f) { off++; sb.Append($"{m.name}@{m.transform.position} {d:0.0}m; "); }
                    }
                    return $"{off}/{total} enemies off the navmesh. {sb}";
                }
                case "tag":
                {
                    // tag <x> <z> <w> <d> <tag>: GUO setTag on the nodes inside a box (no physics)
                    var c = new Vector3(F(a[1]), 0, F(a[2]));
                    float w = F(a[3]) / 2, d = F(a[4]) / 2;
                    var pts = new[] { c + new Vector3(-w, 0, -d), c + new Vector3(w, 0, -d), c + new Vector3(w, 0, d), c + new Vector3(-w, 0, d) };
                    var guo = new GraphUpdateObject(new Bounds(c, new Vector3(w * 2, 200, d * 2)))
                    {
                        updatePhysics = false, modifyTag = true, setTag = (uint)int.Parse(a[5]),
                        shape = new GraphUpdateShape(pts, true, Matrix4x4.identity, 0f),
                    };
                    var sw = Stopwatch.StartNew();
                    astar.UpdateGraphs(guo);
                    astar.FlushGraphUpdates();
                    int tagged = 0;
                    foreach (var g in astar.data.graphs) g?.GetNodes(n => { if (n.Tag == uint.Parse(a[5])) tagged++; });
                    return $"tagged in {sw.ElapsedMilliseconds} ms; nodes with tag {a[5]}: {tagged}";
                }
                case "count":
                {
                    // count x z w d: enemies inside a box, and the tags under them
                    var c = new Vector3(F(a[1]), 0, F(a[2]));
                    float w = F(a[3]) / 2, d = F(a[4]) / 2;
                    int inside = 0, total = 0;
                    var tags = new Dictionary<uint, int>();
                    foreach (var m in Object.FindObjectsOfType<PathfindMovementEnemy>())
                    {
                        total++;
                        var p = m.transform.position;
                        if (Mathf.Abs(p.x - c.x) <= w && Mathf.Abs(p.z - c.z) <= d) inside++;
                        var node = astar.GetNearest(p, NearestNodeConstraint.None).node;
                        if (node != null) tags[node.Tag] = tags.TryGetValue(node.Tag, out int k) ? k + 1 : 1;
                    }
                    var sample = Object.FindObjectsOfType<PathfindMovementEnemy>().Take(6).Select(m => $"({m.transform.position.x:0},{m.transform.position.z:0})->{(m.SeekToTargetObj != null ? m.SeekToTargetObj.name : "home")}");
                    return $"{inside}/{total} inside; tags under enemies: {string.Join(", ", tags.Select(t => $"{t.Key}:{t.Value}"))}; e.g. {string.Join(" ", sample)}";
                }
                case "walk":
                {
                    // walk <mask>: every enemy's seeker and snap constraint may only use these tags (-1 = all)
                    int mask = int.Parse(a[1]);
                    int n = 0;
                    foreach (var m in Object.FindObjectsOfType<PathfindMovementEnemy>())
                    {
                        m.GetComponent<Seeker>().traversableTags = mask;
                        var nn = Traverse.Create(m).Field("nearestConstraint").GetValue<NNConstraint>();
                        nn.constrainTags = true; nn.tags = mask;
                        m.ClearCurrentPath();
                        m.OriginalPathRequest();
                        n++;
                    }
                    return $"{n} enemies limited to tags {mask}";
                }
                default:
                    return "nav scan [n] | cutting on|off | cut <id> x z w d [dual] | move <id> dx dz [rotY] | glide <id> dx dz speed [spin] | stop | interval s | clear | stuck | tag x z w d tag | walk mask";
            }
        }

        private static float started;

        public static void Tick()
        {
            if (moving.Count == 0) return;
            foreach (var kv in moving)
            {
                if (!cuts.TryGetValue(kv.Key, out var go) || go == null) continue;
                go.transform.position += kv.Value.dir * kv.Value.speed * Time.deltaTime;
                if (kv.Value.spin != 0) go.transform.Rotate(0, kv.Value.spin * Time.deltaTime, 0);
            }
        }

        private static string Graphs()
        {
            var sb = new StringBuilder();
            foreach (var g in AstarPath.active.data.graphs)
            {
                if (g is not RecastGraph r) continue;
                int nodes = 0;
                r.GetNodes(_ => nodes++);
                sb.Append($"{r.name}: {nodes} tris, tiles {r.tileXCount}x{r.tileZCount}, cutting {r.enableNavmeshCutting}; ");
            }
            return sb.ToString();
        }

        private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
}
