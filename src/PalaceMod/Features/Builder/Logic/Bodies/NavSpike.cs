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
                    var sample = Object.FindObjectsOfType<PathfindMovementEnemy>().Take(6).Select(m => $"({m.transform.position.x:0},{m.transform.position.z:0})->{(m.SeekToTargetObj != null ? m.SeekToTargetObj.name : "home")} mask {m.GetComponent<Seeker>().traversableTags}");
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
                case "kingwalk":
                    // kingwalk <x> <z> [seconds]: the king walks there over the next frames (see Tick); `kingwalk` alone reports
                    if (a.Length < 3) return KingWalk;
                    kingTarget = new Vector3(F(a[1]), 0f, F(a[2]));
                    kingUntil = Time.time + (a.Length > 3 ? F(a[3]) : 10f);
                    KingWalk = "walking";
                    return "walking";
                case "bench":
                {
                    // bench <n> <x> <z> <w> <d>: n full scans and n area updates over the box, interleaved; min and median ms
                    int n = int.Parse(a[1]);
                    var box = new Bounds(new Vector3(F(a[2]), 0f, F(a[3])), new Vector3(F(a[4]), 60f, F(a[5])));
                    var full = new List<long>(); var area = new List<long>();
                    for (int i = 0; i < n; i++) { full.Add(GroundGraphs.Scan()); area.Add(GroundGraphs.UpdateArea(box)); }
                    full.Sort(); area.Sort();
                    return $"full scan min {full[0]} median {full[n / 2]} ms; area update min {area[0]} median {area[n / 2]} ms; tiled {GroundGraphs.Tiled}; last: {GroundGraphs.LastTimings}";
                }
                case "deckbench":
                {
                    // deckbench <n> <template> <x> <z>: spawn a walkable template, let its area update run, destroy it; n times.
                    // Reports each update's time (from the log line Walkables writes) - run it, then read `logic-l2 nav timings`.
                    benchLastTimings = -1; benchLeft = int.Parse(a[1]) * 2; benchTemplate = a[2]; benchAt = new Vec(F(a[3]), 0f, F(a[4])); benchTimes.Clear();
                    return "running";
                }
                case "deckbenchresult":
                {
                    if (benchLeft > 0) return $"running ({benchLeft} steps left)";
                    var t = benchTimes.ToList(); t.Sort();
                    return t.Count == 0 ? "none" : $"{t.Count} area updates: min {t[0]} median {t[t.Count / 2]} max {t[^1]} ms: {string.Join(", ", benchTimes)}";
                }
                case "timings":
                    return GroundGraphs.LastTimings;
                case "tiles":
                {
                    // tiles <voxels|0>: tile the ground graphs (0 = one tile) and rescan
                    GroundGraphs.UseTiles(int.Parse(a[1]));
                    long ms = GroundGraphs.Scan();
                    return $"{GroundGraphs.LastTimings}; {Graphs()}";
                }
                case "update":
                {
                    // update <x> <z> <w> <d>: GroundGraphs.UpdateArea over a box (ground graphs only)
                    var c = new Vector3(F(a[1]), 0f, F(a[2]));
                    long ms = GroundGraphs.UpdateArea(new Bounds(c, new Vector3(F(a[3]), 30f, F(a[4]))));
                    return $"{GroundGraphs.LastTimings}; {Graphs()}";
                }
                case "graphs":
                    return string.Join("\n", astar.data.graphs.OfType<RecastGraph>().Select(r =>
                        $"{r.name}: radius {r.characterRadius}, climb {r.walkableClimb}, height {r.walkableHeight}, slope {r.maxSlope}, cell {r.cellSize}, " +
                        $"minRegion {r.minRegionSize}, meshes {r.collectionSettings.rasterizeMeshes}, colliders {r.collectionSettings.rasterizeColliders}, terrain {r.collectionSettings.rasterizeTerrain}, " +
                        $"mask {r.collectionSettings.layerMask.value:X}, tiles {r.tileXCount}x{r.tileZCount} ({r.editorTileSize}), bounds {r.forcedBoundsCenter} {r.forcedBoundsSize}"));
                case "wallmesh":
                {
                    // wallmesh <file>: the invisible walls' triangles in world space, as navdump's JSON (4th = wall index)
                    var json = new StringBuilder("[");
                    int wi = 0, n = 0;
                    foreach (var helper in Object.FindObjectsOfType<NavmeshBakeHelper>())
                    {
                        var walls = Traverse.Create(helper).Field("invisibleWalls").GetValue<GameObject>();
                        if (walls == null) continue;
                        foreach (var mf in walls.GetComponentsInChildren<MeshFilter>(true))
                        {
                            var mesh = mf.sharedMesh;
                            if (mesh == null || !mesh.isReadable) { wi++; continue; }
                            var v = mesh.vertices; var t = mesh.triangles; var m = mf.transform.localToWorldMatrix;
                            for (int i = 0; i < t.Length; i += 3)
                            {
                                if (n++ > 0) json.Append(',');
                                json.Append('[');
                                for (int k = 0; k < 3; k++) { var p = m.MultiplyPoint3x4(v[t[i + k]]); json.Append(System.FormattableString.Invariant($"[{p.x:0.00},{p.y:0.00},{p.z:0.00}],")); }
                                json.Append(wi).Append(']');
                            }
                            wi++;
                        }
                    }
                    json.Append(']');
                    System.IO.File.WriteAllText(a[1], json.ToString());
                    return $"{n} wall triangles -> {a[1]}";
                }
                case "walls":
                {
                    // walls [x z r]: the bake's invisible boundary walls (rasterized by GroundGraphs.Scan), near a point
                    var near = a.Length > 3 ? new Vector3(F(a[1]), 0, F(a[2])) : (Vector3?)null;
                    float r = a.Length > 3 ? F(a[3]) : 0f;
                    foreach (var helper in Object.FindObjectsOfType<NavmeshBakeHelper>())
                    {
                        var walls = Traverse.Create(helper).Field("invisibleWalls").GetValue<GameObject>();
                        if (walls == null) continue;
                        foreach (var mr in walls.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            var b = mr.bounds;
                            if (b.size == Vector3.zero) b = new Bounds(mr.transform.position, mr.transform.lossyScale);
                            if (near is Vector3 c && Mathf.Sqrt(b.SqrDistance(new Vector3(c.x, b.center.y, c.z))) > r) continue;
                            var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                            sb.Append($"{mr.name} layer {mr.gameObject.layer} on {mr.enabled && mr.gameObject.activeInHierarchy} bounds {b.min:0.0}..{b.max:0.0} mesh {mesh?.name} readable {mesh?.isReadable} verts {(mesh != null && mesh.isReadable ? mesh.vertexCount : -1)} tris {(mesh != null && mesh.isReadable ? mesh.triangles.Length / 3 : -1)}\n");
                        }
                    }
                    if (near is Vector3 at)
                    {
                        sb.Append("-- other renderers on the ground graphs' layers near it:\n");
                        int mask = astar.data.graphs.OfType<RecastGraph>().First().collectionSettings.layerMask.value;
                        foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(true))
                        {
                            if ((mask & (1 << mr.gameObject.layer)) == 0) continue;
                            var b = mr.bounds;
                            if (Mathf.Sqrt(b.SqrDistance(new Vector3(at.x, b.center.y, at.z))) > r) continue;
                            sb.Append($"{mr.transform.root.name}/{mr.name} layer {mr.gameObject.layer} on {mr.enabled && mr.gameObject.activeInHierarchy} bounds {b.min:0.0}..{b.max:0.0}\n");
                        }
                    }
                    return sb.Length > 0 ? sb.ToString() : "none";
                }
                case "path":
                {
                    // path <x1> <z1> <x2> <z2> [graph part]: an A* path on one ground graph (default Units_L), as the
                    // game's units would ask for it; reports whether it reaches the end and its highest/lowest points.
                    var from = new Vector3(F(a[1]), 0, F(a[2]));
                    var to = new Vector3(F(a[3]), 0, F(a[4]));
                    string part = (a.Length > 5 ? a[5] : "Units_L").Replace('_', ' ');
                    var g = astar.data.graphs.FirstOrDefault(x => x != null && x.name.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    if (g == null) return "no such graph";
                    var nn = NearestNodeConstraint.Walkable;
                    nn.graphMask = GraphMask.FromGraph(g);
                    from.y = to.y = 1f;
                    var hs = astar.GetNearest(from, nn); var he = astar.GetNearest(to, nn);
                    if (hs.node == null || he.node == null) return $"no walkable node near {(hs.node == null ? "the start" : "the end")}";
                    var p = ABPath.Construct(hs.position, he.position);
                    p.traversalConstraint.graphMask = GraphMask.FromGraph(g);
                    AstarPath.StartPath(p);
                    p.BlockUntilCalculated();
                    if (p.error) return $"path error: {p.errorLog}";
                    var v = p.vectorPath;
                    var end = v.Count > 0 ? v[^1] : Vector3.zero;
                    float miss = Vector2.Distance(new Vector2(end.x, end.z), new Vector2(he.position.x, he.position.z));
                    float len = 0f; for (int i = 1; i < v.Count; i++) len += Vector3.Distance(v[i - 1], v[i]);
                    return $"{g.name}: start node area {hs.node?.Area} at {hs.position}, end area {he.node?.Area} at {he.position}; " +
                           $"path {v.Count} points, {len:0.0} m, {(p.path.Count > 0 && p.path[^1] == he.node ? "REACHES the end node" : $"stops {miss:0.0} m short")}; " +
                           $"y {v.Select(q => q.y).DefaultIfEmpty().Min():0.00}..{v.Select(q => q.y).DefaultIfEmpty().Max():0.00}; " +
                           string.Join(" ", v.Where((_, i) => i % System.Math.Max(1, v.Count / 12) == 0 || i == v.Count - 1).Select(q => $"({q.x:0.0},{q.y:0.0},{q.z:0.0})"));
                }
                default:
                    return "nav scan [n] | path x1 z1 x2 z2 [graph] | cutting on|off | cut <id> x z w d [dual] | move <id> dx dz [rotY] | glide <id> dx dz speed [spin] | stop | interval s | clear | stuck | tag x z w d tag | walk mask";
            }
        }

        private static float started;

        private static int benchLeft;
        private static string benchTemplate;
        private static Vec benchAt;
        private static LogicEntity benchDeck;
        private static int benchLastTimings = -1;
        public static readonly List<long> benchTimes = [];

        /// <summary>deckbench: one step per call (every few frames): spawn, or record the update and destroy.</summary>
        private static void BenchStep()
        {
            if (benchLeft <= 0 || LogicWorld.Current == null || Time.frameCount % 15 != 0) return;
            var w = LogicWorld.Current;
            if (benchDeck == null)
            {
                if (benchLastTimings >= 0 && GroundGraphs.AreaUpdates != benchLastTimings) RecordBench();   // the despawn's update
                benchLastTimings = GroundGraphs.AreaUpdates;
                benchDeck = w.Spawn(benchTemplate, benchAt, 0f, null, null, null);
            }
            else
            {
                if (GroundGraphs.AreaUpdates == benchLastTimings) return;   // its update hasn't run yet
                RecordBench();
                benchLastTimings = GroundGraphs.AreaUpdates;
                LogicServices.Factory.Despawn(benchDeck, false, false);
                benchDeck = null;
            }
            benchLeft--;
            if (benchLeft == 0 && benchDeck == null) { }
        }

        private static void RecordBench() => benchTimes.Add(GroundGraphs.LastAreaMs);

        private static Vector3? kingTarget;
        private static float kingUntil;
        public static string KingWalk = "";

        public static void Tick()
        {
            BenchStep();
            if (kingTarget is Vector3 kt && PlayerMovement.instance != null)
            {
                // The king walked by his own CharacterController, one step per frame (fake input doesn't reach a
                // headless game), so per-frame collision changes (DeckOpenings) take part.
                var cc = PlayerMovement.instance.GetComponent<CharacterController>();
                var d = kt - cc.transform.position; d.y = 0f;
                if (d.magnitude < 0.4f || Time.time > kingUntil)
                {
                    KingWalk = $"king walk ended at {cc.transform.position} ({(d.magnitude < 0.4f ? "arrived" : $"{d.magnitude:0.0} m short")})";
                    kingTarget = null;
                    PlayerMovement.instance.enabled = true;
                }
                else
                {
                    PlayerMovement.instance.enabled = false;
                    cc.Move(d.normalized * Mathf.Min(d.magnitude, 6f * Time.deltaTime) + Vector3.down * 4f * Time.deltaTime);
                }
            }
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
