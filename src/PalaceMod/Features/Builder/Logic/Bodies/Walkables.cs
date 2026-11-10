using System.Collections.Generic;
using System.Linq;
using Pathfinding;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    Walkable surfaces (`body.walkable`; docs/builder-engine.md, "Walkable surfaces"). Ground graphs can't gain
    triangles locally and a rescan costs 0.6-1.8 s, so:
    - each walkable body gets a deck: an invisible readable box on the Ground layer (the graphs rasterize it; its
      collider carries the king and coins), top at body.height, footprint = body.box or a square of 2 x radius;
    - a walkable mover also leaves a ghost deck at every pose (renderer only), so one scan sees every place it can be;
    - one GroundGraphs.Scan runs after the walkable bodies of a level start are built (coalesced);
    - a deck that's switched off, or a pose ghost the mover isn't at, is hidden from the graphs by a non-dual
      NavmeshCut limited to a 1.2 m band around the deck's top (cut updates, ~10 ms, not rescans).
    A walkable mover snaps between poses and carries the units standing on it.
    A riding deck (a walkable body linked as a moving part: a howdah on an elephant, the back of a walking colossus)
    is kept off the graphs: they can't move with it, and under a deck its parent's own ground would have no clearance,
    so the parent would stand in a hole and never walk. Its collider still carries the king, and DeckCarrier carries
    the units on it (they ride, not walk).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class Walkables
    {
        internal sealed class Deck
        {
            public BuilderBody Body;
            public GameObject Proxy;          // the deck that moves with the body
            public NavmeshCut Hide;           // hides the deck's triangles while switched off
            public readonly Dictionary<string, (GameObject ghost, NavmeshCut hide)> Ghosts = [];
            public Vector3 Size;
            public Vector2 Center;            // the deck's middle in the body's frame (x, z): off zero when clipped to its visual
            public string Pose;
            public bool InSpace;              // off the map: its own space graphs, no walls to open
            public bool Riding;               // a moving linked part: off the graphs (the parent walks under it)
            public readonly List<DeckOpenings.Top> Tops = [];   // what the host graphs rasterize: the proxy, or the pose ghosts
        }

        private static readonly List<Deck> decks = [];
        private static bool scanPending;
        private static bool settled;                 // the level's first frame has passed: later decks update their area
        private static readonly List<Bounds> pendingAreas = [];

        /// <summary>Tile size (m) of the ground graphs in a level that can make walkable bodies at runtime.</summary>
        public const float TileMetres = 25.6f;

        /// <summary>Margin (m) round a deck its area update rebuilds: ramps (up to 5.4 m) and the large units' radius (4 m).</summary>
        private const float AreaMargin = 6f;
        private static Mesh box;

        public static int Count => decks.Count;

        public static void Clear() { decks.Clear(); scanPending = false; settled = false; pendingAreas.Clear(); DeckOpenings.Clear(); }

        /// <summary>
        /// The level can make walkable bodies (its templates have one): tile the ground graphs at the level-start scan, so a
        /// deck that appears or goes later rebuilds only the tiles round it (tens of ms) instead of every graph (0.6-1.8 s).
        /// </summary>
        public static void PrepareRuntimeDecks()
        {
            var astar = AstarPath.active;
            if (astar == null || GroundGraphs.Tiled) return;
            var g = astar.data.graphs.OfType<RecastGraph>().FirstOrDefault(x => x.name != BodyNav.AirGraph);
            if (g == null) return;
            GroundGraphs.UseTiles(Mathf.Clamp(Mathf.RoundToInt(TileMetres / g.cellSize), 32, 512));
            RequestScan();
        }

        /// <summary>The deck meshes on the host's graphs (DeckOpenings opens the walls under them).</summary>
        public static IEnumerable<DeckOpenings.Top> NavTops() => decks.Where(d => !d.InSpace && d.Body != null).SelectMany(d => d.Tops).Where(t => t.Mesh != null);

        public static Mesh BoxMesh() => Box();

        /// <summary>The decks that carry riders (DeckCarrier): every walkable body's own deck; offGraph: a riding deck,
        /// which the graphs don't have, so units on it ride instead of walking.</summary>
        public static IEnumerable<(Transform deck, bool offGraph)> LiveDecks()
        {
            foreach (var d in decks) if (d.Body != null && d.Proxy != null && d.Body.isActiveAndEnabled) yield return (d.Proxy.transform, d.Riding);
        }

        /// <summary>A point stands on (or just above) a riding deck's top: a unit put there rides it.</summary>
        public static bool OnRidingDeck(Vector3 p)
        {
            foreach (var d in decks)
            {
                if (!d.Riding || d.Proxy == null || d.Body == null || !d.Body.isActiveAndEnabled) continue;
                var l = d.Proxy.transform.InverseTransformPoint(p);
                if (Mathf.Abs(l.x) > 0.5f || Mathf.Abs(l.z) > 0.5f) continue;
                float dy = p.y - d.Proxy.transform.TransformPoint(new Vector3(l.x, 0.5f, l.z)).y;
                if (dy > -0.6f && dy < 1.5f) return true;
            }
            return false;
        }

        /// <summary>A walkable body linked as a moving part leaves the graphs (and comes back when it's unlinked).</summary>
        private static void UpdateRiding(Deck d)
        {
            if (d.Body == null || d.Proxy == null || d.Ghosts.Count > 0) return;
            var e = d.Body.Entity;
            bool ride = e?.Parent != null && e.Link is { } l && l.Moves;
            if (ride == d.Riding) return;
            var area = AreaAround(d.Proxy);
            d.Riding = ride;
            if (d.Proxy.GetComponent<MeshRenderer>() is { } r) r.enabled = !ride;
            if (ride)
            {
                foreach (var t in d.Tops) if (t.Edges != null) Object.Destroy(t.Edges);
                d.Tops.Clear();
            }
            else if (d.Tops.Count == 0) d.Tops.Add(new DeckOpenings.Top { Mesh = d.Proxy, Live = true });
            Log.For<LogicBodiesManager>().Info($"walkable {d.Body.name}: {(ride ? "a moving part now; off the graphs (riders ride it)" : "no longer a moving part; back on the graphs")}");
            if (d.InSpace || AstarPath.active == null) return;
            if (settled) pendingAreas.Add(area);
            else RequestScan();
        }

        private static Bounds AreaAround(GameObject proxy)
        {
            var t = new DeckOpenings.Top { Mesh = proxy };
            t.Update();
            var b = new Bounds(t.Corners[0], Vector3.zero);
            foreach (var c in t.Corners) b.Encapsulate(c);
            b.Expand(new Vector3(2f * AreaMargin, 0f, 2f * AreaMargin));
            b.size = new Vector3(b.size.x, 60f, b.size.z);
            return b;
        }

        public static void Add(BuilderBody body, BuilderMover mover)
        {
            var size = body.Box is Vector2 b ? new Vector3(b.x, 0.3f, b.y) : new Vector3(body.Radius * 2f, 0.3f, body.Radius * 2f);
            var center = ClipToVisual(body, ref size);
            var d = new Deck { Body = body, Size = size, Center = center };
            if (mover == null || mover.Poses.Count == 0) LiftOutOfGround(body, size, center);
            d.Proxy = MakeDeck(body.transform, size, center, body.Height, collider: true);
            d.Hide = MakeHide(d.Proxy, size);
            d.Hide.enabled = !body.isActiveAndEnabled;
            if (mover != null)
                foreach (var kv in mover.Poses)
                {
                    var (rotY, pos, offset) = kv.Value;
                    var at = pos is Vector3 p ? new Vector3(p.x, float.IsNaN(p.y) ? body.transform.position.y : p.y, p.z) : offset is Vector3 o ? body.transform.position + o : body.transform.position;
                    var root = new GameObject($"PalaceBuilder pose {kv.Key} of {body.name}");
                    root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, rotY ?? body.transform.eulerAngles.y, 0f));
                    root.transform.localScale = body.transform.lossyScale;
                    var ghost = MakeDeck(root.transform, size, center, body.Height, collider: false);
                    var hide = MakeHide(ghost, size);
                    d.Ghosts[kv.Key] = (root, hide);
                    d.Tops.Add(new DeckOpenings.Top { Mesh = ghost });
                    // The pose it was placed in (else its first) is where it starts.
                    if (d.Pose == null || ((root.transform.position - body.transform.position).sqrMagnitude < 0.01f && Quaternion.Angle(root.transform.rotation, body.transform.rotation) < 1f))
                        d.Pose = kv.Key;
                }
            if (d.Ghosts.Count > 0)
            {
                // Only the ghosts are on the graphs; the moving deck just carries the king.
                Object.Destroy(d.Proxy.GetComponent<MeshRenderer>());
                var start = d.Ghosts[d.Pose].ghost.transform;
                body.transform.SetPositionAndRotation(start.position, start.rotation);
            }
            else d.Tops.Add(new DeckOpenings.Top { Mesh = d.Proxy, Live = true });
            decks.Add(d);
            DeckCarrier.Ensure();
            // Off the map: its own space graphs (fast); on the map: the host graphs' one coalesced scan.
            var where = d.Ghosts.Count > 0 ? d.Ghosts.Values.First().ghost.transform.position : body.transform.position;
            if (Spaces.Outside(where))
            {
                d.InSpace = true;
                foreach (var g in d.Ghosts.Values) Spaces.Add(BodyVisual.Bounds(g.ghost));
                if (d.Ghosts.Count == 0) Spaces.Add(new Bounds(d.Proxy.transform.TransformPoint(Vector3.up * 0.5f), new Vector3(size.x, 1f, size.z) * body.transform.lossyScale.x));
            }
            else if (settled && AstarPath.active != null) pendingAreas.Add(AreaOf(d));
            else RequestScan();
            Refresh(d);
        }

        /// <summary>
        /// A deck whose top is under the ground somewhere in its footprint (a bridge placed at the river bed's height, or a
        /// raft on the water whose ends lie on the banks) is buried there: the bank's ground covers it and the graphs
        /// see a wall where it meets the bank. Such a deck is raised so its top is on the highest ground under it, which
        /// is how a bridge rests on its banks. Decks wholly over water or lower ground keep their height.
        /// </summary>
        private static void LiftOutOfGround(BuilderBody body, Vector3 size, Vector2 center)
        {
            var t = body.transform;
            float top = t.position.y + body.Height * t.lossyScale.y, highest = float.MinValue;
            var hits = new RaycastHit[8];
            float hx = size.x / 2f, hz = size.z / 2f;
            int nx = Mathf.Max(1, Mathf.CeilToInt(size.x)), nz = Mathf.Max(1, Mathf.CeilToInt(size.z));
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= nz; j++)
                {
                    var p = t.TransformPoint(new Vector3(center.x - hx + size.x * i / nx, 0f, center.y - hz + size.z * j / nz));
                    int n = Physics.RaycastNonAlloc(new Vector3(p.x, top + 40f, p.z), Vector3.down, hits, 120f, 1 << 6, QueryTriggerInteraction.Ignore);
                    for (int k = 0; k < n; k++)
                    {
                        if (hits[k].collider.transform.IsChildOf(t) || hits[k].collider.name.Contains("walkable deck")) continue;
                        if (hits[k].point.y > highest) highest = hits[k].point.y;
                    }
                }
            if (highest == float.MinValue || highest <= top + 0.05f) return;
            float lift = highest - top;
            t.position += Vector3.up * lift;
            if (body.Entity != null) body.Entity.StaticPos = t.position.ToVec();
            Log.For<LogicBodiesManager>().Info($"walkable {body.name}: its deck was {lift:0.00} m under the ground it spans; raised to rest on it (top {highest:0.00} m)");
        }

        /// <summary>What an area update for this deck rebuilds: its deck(s) and the margin round them.</summary>
        private static Bounds AreaOf(Deck d)
        {
            Bounds? b = null;
            foreach (var t in d.Tops)
            {
                if (!t.Update()) continue;
                foreach (var c in t.Corners) { if (b is Bounds bb) { bb.Encapsulate(c); b = bb; } else b = new Bounds(c, Vector3.zero); }
            }
            var area = b ?? new Bounds(d.Body != null ? d.Body.transform.position : Vector3.zero, Vector3.one);
            area.Expand(new Vector3(2f * AreaMargin, 0f, 2f * AreaMargin));
            area.size = new Vector3(area.size.x, 60f, area.size.z);
            return area;
        }

        /// <summary>
        /// A deck is no wider or longer than the mesh that shows it: a body box larger than the visual (a 10 m box under
        /// a 6 m bridge) made walkable ground over the water either side of the bridge, where units then walked. The
        /// footprint is clipped to the visual's bounds in the body's frame; returns the clipped deck's middle (x, z).
        /// Only for an explicit `visual.mesh` (an interactable's default marker isn't the deck's shape).
        /// </summary>
        private static Vector2 ClipToVisual(BuilderBody body, ref Vector3 size)
        {
            if (body.Entity?.Def?["visual"] is not Newtonsoft.Json.Linq.JObject v || string.IsNullOrEmpty((string)v["mesh"])) return Vector2.zero;
            var vis = body.transform.Find(BodyVisual.ChildName);
            if (vis == null) return Vector2.zero;
            var t = body.transform;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var mf in vis.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = t.InverseTransformPoint(mf.transform.TransformPoint(c));
                    minX = Mathf.Min(minX, l.x); maxX = Mathf.Max(maxX, l.x);
                    minZ = Mathf.Min(minZ, l.z); maxZ = Mathf.Max(maxZ, l.z);
                }
            }
            if (minX > maxX) return Vector2.zero;
            float x0 = Mathf.Max(-size.x / 2f, minX), x1 = Mathf.Min(size.x / 2f, maxX);
            float z0 = Mathf.Max(-size.z / 2f, minZ), z1 = Mathf.Min(size.z / 2f, maxZ);
            const float Slack = 0.25f, MinSide = 1f;
            if (x1 - x0 < MinSide || z1 - z0 < MinSide)
            {
                Log.For<LogicBodiesManager>().Warn($"walkable {body.name}: its visual ({maxX - minX:0.0} x {maxZ - minZ:0.0} m) hardly overlaps its body box ({size.x:0.0} x {size.z:0.0} m); the deck keeps the box");
                return Vector2.zero;
            }
            if (size.x - (x1 - x0) < Slack && size.z - (z1 - z0) < Slack) return Vector2.zero;
            Log.For<LogicBodiesManager>().Info($"walkable {body.name}: body box {size.x:0.0} x {size.z:0.0} m is larger than its visual ({maxX - minX:0.0} x {maxZ - minZ:0.0} m); deck clipped to {x1 - x0:0.0} x {z1 - z0:0.0} m");
            size = new Vector3(x1 - x0, size.y, z1 - z0);
            return new Vector2((x0 + x1) / 2f, (z0 + z1) / 2f);
        }

        private static GameObject MakeDeck(Transform parent, Vector3 size, Vector2 center, float top, bool collider)
        {
            var go = new GameObject(LayoutScene.AddedPrefix + "walkable deck") { layer = 6 };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(center.x, top - size.y / 2f, center.y);
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = Box();
            go.AddComponent<MeshRenderer>().forceRenderingOff = true;   // enabled for the graphs, never drawn
            if (collider) go.AddComponent<BoxCollider>();
            return go;
        }

        private static NavmeshCut MakeHide(GameObject deck, Vector3 size)
        {
            // On its own object at the deck's top (a 1.2 m band, so ground below a raised deck keeps its navmesh), not a
            // child of the body: switching the body off deactivates its children, and the cut must stay on then.
            var holder = new GameObject("PalaceBuilder deck hide");
            var top = deck.transform.TransformPoint(Vector3.up * 0.5f);
            holder.transform.SetPositionAndRotation(top, Quaternion.Euler(0f, deck.transform.eulerAngles.y, 0f));
            var cut = holder.AddComponent<NavmeshCut>();
            cut.type = NavmeshCut.MeshType.Rectangle;
            cut.rectangleSize = new Vector2(size.x, size.z);
            cut.useRotationAndScale = true;
            cut.height = 1.2f;
            cut.radiusExpansionMode = NavmeshCut.RadiusExpansionMode.DontExpand;
            cut.graphMask = BodyNav.GroundMask();
            cut.enabled = false;
            return cut;
        }

        public static void RequestScan() => scanPending = true;

        /// <summary>Once a frame: the coalesced scan, after the level's walkable bodies exist.</summary>
        public static void Tick()
        {
            long spaceMs = Spaces.Tick();
            if (spaceMs >= 0)
            {
                Log.For<LogicBodiesManager>().Info($"spaces: {Spaces.Describe()}, scan {spaceMs} ms");
                foreach (var d in decks) Refresh(d);
            }
            foreach (var d in decks) UpdateRiding(d);
            if (AstarPath.active == null) return;
            bool first = !settled;
            settled = true;
            if (!scanPending && pendingAreas.Count == 0) return;
            if (scanPending || first)
            {
                scanPending = false;
                pendingAreas.Clear();
                long ms = GroundGraphs.Scan();
                Log.For<LogicBodiesManager>().Info($"walkable surfaces: {decks.Count} decks, {decks.Sum(d => d.Ghosts.Count)} pose ghosts, scan {ms} ms ({GroundGraphs.LastTimings}); {DeckOpenings.LastSummary}");
            }
            else
            {
                var areas = pendingAreas.ToArray();
                pendingAreas.Clear();
                long ms = GroundGraphs.UpdateArea(areas);
                Log.For<LogicBodiesManager>().Info($"walkable surfaces: {decks.Count} decks, {GroundGraphs.LastTimings}{(GroundGraphs.Tiled ? "" : " (one-tile graphs: rebuilt whole)")}; {DeckOpenings.LastSummary}");
            }
            foreach (var d in decks) Refresh(d);
            var cut = DeckOpenings.Disconnected();
            if (cut.Count > 0) Log.For<LogicBodiesManager>().Warn($"walkable decks off the castle's ground (small enemies' graph): {string.Join("; ", cut)}");
        }

        /// <summary>Once a frame (after Tick): the king passes walls the decks opened while he's on one.</summary>
        public static void KingTick() => DeckOpenings.Tick();

        /// <summary>Switch a deck's cuts to match its body's state and pose.</summary>
        public static void Refresh(Deck d)
        {
            if (d.Body == null) return;
            bool on = d.Body.isActiveAndEnabled;
            var mask = BodyNav.GroundMask();
            d.Hide.graphMask = mask;
            foreach (var g in d.Ghosts.Values) g.hide.graphMask = mask;
            if (d.Riding) { Switch(d.Hide, false); return; }
            if (d.Ghosts.Count == 0) { Switch(d.Hide, !on); return; }
            // A posed mover: the live deck is never cut (it's off the graph while moving, units wait); the ghost of
            // the current pose shows, every other ghost is hidden.
            Switch(d.Hide, false);
            foreach (var kv in d.Ghosts) Switch(kv.Value.hide, !(on && kv.Key == d.Pose));
        }

        /// <summary>A cut switched on only reaches the graphs once it's marked dirty (A* doesn't for a new clipper).</summary>
        private static void Switch(NavmeshCut cut, bool on)
        {
            if (cut == null || cut.enabled == on) return;
            cut.enabled = on;
            if (on) cut.ForceUpdate();
            else AstarPath.active?.navmeshUpdates.ForceUpdate();
        }

        /// <summary>The body is gone: its cuts and pose ghosts go too.</summary>
        public static void Remove(BuilderBody body)
        {
            foreach (var d in decks.Where(d => d.Body == body).ToList())
            {
                // Despawned mid-level: rebuild its area (the deck's triangles would stay walkable otherwise).
                if (settled && body.Despawning && !d.InSpace && AstarPath.active != null) pendingAreas.Add(AreaOf(d));
                if (d.Hide != null) Object.Destroy(d.Hide.gameObject);
                foreach (var g in d.Ghosts.Values) if (g.ghost != null) Object.Destroy(g.ghost);
                decks.Remove(d);
            }
        }

        public static void SetEnabled(BuilderBody body)
        {
            foreach (var d in decks.Where(d => d.Body == body)) Refresh(d);
        }

        /// <summary>A walkable mover is about to snap to a pose: carry what stands on it, then show that pose.</summary>
        public static void Snap(BuilderMover m, string pose, Vector3 pos, Quaternion rot)
        {
            var d = decks.FirstOrDefault(x => x.Body != null && x.Body.gameObject == m.gameObject);
            var before = m.transform.localToWorldMatrix;
            var riders = d != null ? Riders(d) : [];
            m.transform.SetPositionAndRotation(pos, rot);
            var after = m.transform.localToWorldMatrix;
            foreach (var r in riders)
            {
                var local = before.inverse.MultiplyPoint3x4(r.position);
                r.position = after.MultiplyPoint3x4(local);
                r.GetComponent<PathfindMovement>()?.ClearCurrentPath();
            }
            if (d != null) { d.Pose = pose; Refresh(d); }
        }

        public static void PoseReached(BuilderMover m) { }

        private static List<Transform> Riders(Deck d)
        {
            var list = new List<Transform>();
            var deck = d.Proxy.transform;
            foreach (var m in Object.FindObjectsOfType<PathfindMovement>()) Add(m.transform);
            if (PlayerInteraction.instance != null) Add(PlayerInteraction.instance.transform);
            return list;

            void Add(Transform t)
            {
                var local = deck.InverseTransformPoint(t.position);
                if (Mathf.Abs(local.x) <= 0.5f && Mathf.Abs(local.z) <= 0.5f && local.y > -2f && local.y < 8f) list.Add(t);
            }
        }

        private static Mesh Box()
        {
            if (box != null) return box;
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
            int[] t = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];
            box = new Mesh { name = "PalaceBuilder deck", vertices = v, triangles = t };
            box.RecalculateBounds();
            return box;
        }

        public static string Describe() => $"{decks.Count} walkable decks ({string.Join(", ", decks.Select(d => $"{d.Body?.name}{(d.Pose != null ? " @" + d.Pose : "")}{(d.Riding ? " riding" : "")}{(d.Hide.enabled ? " hidden" : "")}"))})";
    }
}
