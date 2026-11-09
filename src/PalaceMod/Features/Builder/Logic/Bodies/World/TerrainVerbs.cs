using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The `terrain` action (docs/builder-engine.md, "Runtime terrain"): the level's ground changes during play. It edits the
    terrain grid the level was built with (GroundLayer: a flat field's, or on a map's own ground a grid laid over it at the
    level's start), re-meshes only the chunks it touched and rebuilds the ground graphs only round them
    (GroundGraphs.UpdateArea on tiled graphs: tens of ms). Ops:
    - raise / lower (amount m), set (height: world y), smooth: the ground's corners, with soft edges (softness);
    - paint (material), block / unblock: per paint sample or per cell;
    - water / dry: cells become water (dug a metre under the water level, as the Studio's brush does) or dry land;
    - freeze / thaw: water cells become walkable ice at the water level, or water again;
    - waterLevel (level: world y): the water's surface moves; low ground joined to the water floods (or every cell under
      the level, flood "all"), and ground the water leaves dries.
    Where: a circle (radius), a rectangle (width x length, turned by angle), an area or body (in: its own shape), or for
    waterLevel/freeze/thaw/dry with no shape the whole field. `over` (s) animates heights and the water level; the graphs
    follow at the end (and once a second while water spreads).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class TerrainVerbs
    {
        private static readonly string[] Ops = ["raise", "lower", "set", "smooth", "paint", "block", "unblock", "water", "dry", "freeze", "thaw", "waterLevel"];
        private static readonly string[] Paints = ["grass", "highGrass", "lowGrass", "earth", "sand", "road", "rock"];

        /// <summary>Whether any terrain change ran this level (savestates keep the grid then).</summary>
        public static bool Changed;
        private static readonly List<Anim> anims = [];
        private static readonly List<Bounds> pendingNav = [];
        private static float lastNav;

        public static void Register()
        {
            LogicActions.Register("terrain", Terrain);
            WorldSession.Reset += () => { anims.Clear(); pendingNav.Clear(); Changed = false; };
        }

        // ------------------------------------------------------------------ shapes

        private sealed class Shape
        {
            public Vector3 Center;
            public float Radius = -1f, Width, Length, Angle, Softness;
            public BuilderArea Area;
            public bool Everywhere;
            public Bounds Bounds;

            /// <summary>0..1: how much a point is inside (1 in the hard core, falling to 0 over the soft edge).</summary>
            public float Weight(float x, float z)
            {
                if (Everywhere) return 1f;
                if (Area != null) return Area.Contains(new Vector3(x, 0f, z)) ? 1f : 0f;
                float dx = x - Center.x, dz = z - Center.z;
                if (Radius > 0f) return TerrainBrushes.Weight(Mathf.Sqrt(dx * dx + dz * dz), Radius, Softness);
                float a = -Angle * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
                float lx = dx * c - dz * s, lz = dx * s + dz * c;
                float hx = Width / 2f, hz = Length / 2f;
                if (Mathf.Abs(lx) > hx || Mathf.Abs(lz) > hz) return 0f;
                float edge = Mathf.Min(hx - Mathf.Abs(lx), hz - Mathf.Abs(lz));
                float soft = Softness * Mathf.Min(hx, hz);
                return soft <= 0f || edge >= soft ? 1f : Mathf.SmoothStep(0f, 1f, edge / soft);
            }
        }

        private static Shape ReadShape(LogicContext ctx, ActionParams p, string op, bool wholeByDefault)
        {
            var s = new Shape { Softness = (float)Mathf.Clamp01((float)p.Number("softness", op is "raise" or "lower" or "set" or "smooth" ? 0.3 : 0)) };
            if (p.Has("in"))
            {
                var e = p.Entity("in") ?? throw new LogicError("terrain: \"in\" is empty");
                var go = e.Go();
                var area = go != null ? go.GetComponent<BuilderArea>() : null;
                var body = go != null ? go.GetComponent<BuilderBody>() : null;
                if (area != null)
                {
                    s.Area = area;
                    float r = area.Shape.Reach * area.transform.lossyScale.x;
                    s.Bounds = new Bounds(area.transform.position, new Vector3(2 * r, 1f, 2 * r));
                    return s;
                }
                if (body == null) throw new LogicError($"terrain: \"in\" needs an area or a body ({e.Id} has neither)");
                s.Center = body.transform.position;
                s.Angle = body.transform.eulerAngles.y;
                if (body.Box is Vector2 b) { s.Width = b.x * body.transform.lossyScale.x; s.Length = b.y * body.transform.lossyScale.z; }
                else s.Radius = body.Radius * body.transform.lossyScale.x;
            }
            else
            {
                bool shaped = p.Has("radius") || p.Has("width") || p.Has("length");
                if (!shaped)
                {
                    if (!wholeByDefault) throw new LogicError($"terrain {op}: give \"radius\", \"width\" and \"length\", or \"in\"");
                    s.Everywhere = true;
                    return s;
                }
                s.Center = WorldCommon.At(ctx, p) ?? throw new LogicError("terrain: needs \"at\" (or a self with a position)");
                if (p.Has("radius")) s.Radius = Mathf.Max(0.1f, (float)p.Number("radius"));
                else
                {
                    s.Width = Mathf.Max(0.1f, (float)p.Number("width", p.Number("length")));
                    s.Length = Mathf.Max(0.1f, (float)p.Number("length", s.Width));
                    s.Angle = (float)p.Number("angle");
                }
            }
            float reach = s.Radius > 0 ? s.Radius : Mathf.Sqrt(s.Width * s.Width + s.Length * s.Length) / 2f;
            s.Bounds = new Bounds(s.Center, new Vector3(2 * reach, 1f, 2 * reach));
            return s;
        }

        // ------------------------------------------------------------------ the action

        private static void Terrain(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("terrain");
            string op = p.Text("op") ?? throw new LogicError("terrain: needs \"op\"");
            if (!Ops.Contains(op)) throw new LogicError($"terrain: unknown op \"{op}\"{WorldCommon.Near(Ops, op)}");
            var g = EnsureGrid();
            var shape = ReadShape(ctx, p, op, op is "waterLevel" or "freeze" or "thaw" or "dry");
            if (shape.Everywhere) shape.Bounds = new Bounds(new Vector3(g.OriginX + g.SizeX / 2f, 0f, g.OriginZ + g.SizeZ / 2f), new Vector3(g.SizeX, 1f, g.SizeZ));
            float over = Mathf.Max(0f, (float)p.Number("over"));
            Changed = true;
            switch (op)
            {
                case "raise":
                case "lower":
                {
                    float amount = (float)p.Number("amount", 1) * (op == "lower" ? -1f : 1f);
                    Heights(g, shape, (h, w) => h + amount * w, over);
                    break;
                }
                case "set":
                {
                    if (!p.Has("height")) throw new LogicError("terrain set: needs \"height\" (world y)");
                    float target = (float)p.Number("height") - g.Y;
                    Heights(g, shape, (h, w) => Mathf.Lerp(h, target, w), over);
                    break;
                }
                case "smooth":
                {
                    var src = (float[])g.Heights.Clone();
                    Heights(g, shape, (h, w) => h, over, (cx, cz, h, w) =>
                    {
                        float sum = 0f; int n = 0;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx, nz = cz + dz;
                                if (nx < 0 || nz < 0 || nx > g.W || nz > g.H) continue;
                                sum += src[g.Corner(nx, nz)]; n++;
                            }
                        return Mathf.Lerp(h, sum / n, 0.5f * w);
                    });
                    break;
                }
                case "paint":
                {
                    string m = p.Text("material") ?? throw new LogicError("terrain paint: needs \"material\"");
                    int idx = Array.FindIndex(Paints, x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) throw new LogicError($"terrain paint: \"{m}\" isn't a paint ({string.Join(", ", Paints)})");
                    Paint(g, shape, (byte)idx);
                    break;
                }
                case "block": Cells(g, shape, i => g.Blocked[i] = true); break;
                case "unblock": Cells(g, shape, i => g.Blocked[i] = false); break;
                case "water":
                {
                    float floor = g.ClampHeight(g.WaterLevel - TerrainBrushes.WaterDepth);
                    var d = Cells(g, shape, i => { g.Water[i] = true; g.Frozen[i] = false; }, apply: false);
                    if (d.Empty) break;
                    for (int z = d.Z0; z <= d.Z1; z++)
                        for (int x = d.X0; x <= d.X1; x++)
                        {
                            if (!g.Water[g.CellIndex(x, z)]) continue;
                            for (int k = 0; k < 4; k++)
                            {
                                int c = g.Corner(x + (k & 1), z + (k >> 1));
                                if (g.Heights[c] > floor) g.Heights[c] = floor;
                            }
                        }
                    d.Add(Math.Max(0, d.X0 - 1), Math.Max(0, d.Z0 - 1));
                    d.Add(Math.Min(g.W - 1, d.X1 + 1), Math.Min(g.H - 1, d.Z1 + 1));
                    Commit(g, d);
                    break;
                }
                case "dry": Cells(g, shape, i => { g.Water[i] = false; g.Frozen[i] = false; }, only: i => g.Water[i]); break;
                case "freeze":
                case "thaw":
                {
                    bool on = op == "freeze";
                    var d = Cells(g, shape, i => g.Frozen[i] = on, only: i => g.Water[i] && g.Frozen[i] != on, apply: false);
                    if (d.Empty) break;
                    FillIce(g);
                    d.Add(Math.Max(0, d.X0 - 1), Math.Max(0, d.Z0 - 1));
                    d.Add(Math.Min(g.W - 1, d.X1 + 1), Math.Min(g.H - 1, d.Z1 + 1));
                    Commit(g, d);
                    break;
                }
                case "waterLevel":
                {
                    if (!p.Has("level")) throw new LogicError("terrain waterLevel: needs \"level\" (world y)");
                    float to = Mathf.Clamp((float)p.Number("level") - g.Y, TerrainGrid.MinHeight - g.Y, g.MaxWorld - g.Y);
                    string flood = p.Text("flood", "connected");
                    if (flood is not ("connected" or "all" or "none")) throw new LogicError($"terrain waterLevel: flood is connected, all or none, not \"{flood}\"");
                    if (over > 0f) anims.Add(new Anim { Shape = shape, Water = true, From = g.WaterLevel, To = to, Over = over, Flood = flood });
                    else SetLevel(g, shape, to, flood, nav: true);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ grid edits

        /// <summary>The grid to edit: the level's, or (on a map's own ground with none) one laid over the map now.</summary>
        private static TerrainGrid EnsureGrid()
        {
            if (GroundLayer.Active) return GroundLayer.Grid;
            var scene = SceneManager.GetActiveScene();
            var grid = GroundLayer.NewFlat(scene);
            GroundLayer.Show(scene, grid, false);
            Log.For<LogicWorldVerbsManager>().Info($"terrain: laid a {grid.W}x{grid.H} grid over {scene.name}'s ground for runtime changes");
            return GroundLayer.Grid;
        }

        /// <summary>Called at level start when the level's rules use `terrain`: lay the grid and tile the graphs now, so
        /// the first change doesn't stall.</summary>
        public static void Prepare()
        {
            EnsureGrid();
            Walkables.PrepareRuntimeDecks();
        }

        private static void Heights(TerrainGrid g, Shape s, Func<float, float, float> f, float over, Func<int, int, float, float, float> at = null)
        {
            Range(g, s.Bounds, cells: false, out int x0, out int z0, out int x1, out int z1);
            var change = new List<(int i, float from, float to)>();
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    float w = s.Weight(g.CornerX(cx), g.CornerZ(cz));
                    if (w <= 0f) continue;
                    int i = g.Corner(cx, cz);
                    float h = g.Heights[i];
                    float to = g.ClampHeight(at != null ? at(cx, cz, h, w) : f(h, w));
                    if (to != h) change.Add((i, h, to));
                    if (g.Host != null) MarkForced(g, cx, cz);
                }
            if (change.Count == 0) return;
            if (over > 0f) { anims.Add(new Anim { Corners = change, Over = over }); return; }
            foreach (var (i, _, to) in change) g.Heights[i] = to;
            Commit(g, DirtyOf(g, change.Select(c => c.i)));
        }

        /// <summary>On a map's own ground: the cells round a corner are drawn by the terrain from now on.</summary>
        private static void MarkForced(TerrainGrid g, int cx, int cz)
        {
            for (int dz = -1; dz <= 0; dz++)
                for (int dx = -1; dx <= 0; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x >= 0 && z >= 0 && x < g.W && z < g.H) g.Forced[g.CellIndex(x, z)] = true;
                }
        }

        private static Dirty DirtyOf(TerrainGrid g, IEnumerable<int> corners)
        {
            var d = Dirty.None;
            foreach (int i in corners)
            {
                int cx = i % (g.W + 1), cz = i / (g.W + 1);
                d.Add(Math.Max(0, cx - 1), Math.Max(0, cz - 1));
                d.Add(Math.Min(g.W - 1, cx), Math.Min(g.H - 1, cz));
            }
            return d;
        }

        private static Dirty Cells(TerrainGrid g, Shape s, Action<int> set, Func<int, bool> only = null, bool apply = true)
        {
            Range(g, s.Bounds, cells: true, out int x0, out int z0, out int x1, out int z1);
            var d = Dirty.None;
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (s.Weight(g.CornerX(cx) + g.Cell / 2f, g.CornerZ(cz) + g.Cell / 2f) < 0.5f) continue;
                    int i = g.CellIndex(cx, cz);
                    if (only != null && !only(i)) continue;
                    set(i);
                    if (g.Host != null) g.Forced[i] = true;
                    d.Add(cx, cz);
                }
            if (apply && !d.Empty) Commit(g, d);
            return d;
        }

        private static void Paint(TerrainGrid g, Shape s, byte value)
        {
            var d = Dirty.None;
            float step = g.PaintStep;
            int px0 = Mathf.Max(0, Mathf.FloorToInt((s.Bounds.min.x - g.OriginX) / step)), px1 = Mathf.Min(g.PaintW - 1, Mathf.CeilToInt((s.Bounds.max.x - g.OriginX) / step));
            int pz0 = Mathf.Max(0, Mathf.FloorToInt((s.Bounds.min.z - g.OriginZ) / step)), pz1 = Mathf.Min(g.PaintH - 1, Mathf.CeilToInt((s.Bounds.max.z - g.OriginZ) / step));
            if (s.Everywhere) { px0 = 0; pz0 = 0; px1 = g.PaintW - 1; pz1 = g.PaintH - 1; }
            for (int pz = pz0; pz <= pz1; pz++)
                for (int px = px0; px <= px1; px++)
                {
                    if (s.Weight(g.OriginX + (px + 0.5f) * step, g.OriginZ + (pz + 0.5f) * step) < 0.5f) continue;
                    int i = g.PaintIndex(px, pz);
                    int cx = px / TerrainGrid.PaintRes, cz = pz / TerrainGrid.PaintRes;
                    if (g.Host != null) g.Forced[g.CellIndex(cx, cz)] = true;
                    if (g.Materials[i] == value) continue;
                    g.Materials[i] = value;
                    d.Add(cx, cz);
                }
            if (!d.Empty) Commit(g, d, nav: false);   // paint never changes where units walk
        }

        private static void Range(TerrainGrid g, Bounds b, bool cells, out int x0, out int z0, out int x1, out int z1)
        {
            int maxX = cells ? g.W - 1 : g.W, maxZ = cells ? g.H - 1 : g.H;
            x0 = Math.Max(0, Mathf.FloorToInt((b.min.x - g.OriginX) / g.Cell) - 1);
            z0 = Math.Max(0, Mathf.FloorToInt((b.min.z - g.OriginZ) / g.Cell) - 1);
            x1 = Math.Min(maxX, Mathf.CeilToInt((b.max.x - g.OriginX) / g.Cell) + 1);
            z1 = Math.Min(maxZ, Mathf.CeilToInt((b.max.z - g.OriginZ) / g.Cell) + 1);
        }

        /// <summary>
        /// Ice is ground at the water level: every corner of a frozen cell is filled up to the ice's top (its bed height is
        /// kept in IceBed), which also turns the dug bank beside it into a gentle slope instead of a cliff, so units and
        /// the king walk from the bank onto the ice. Corners no frozen cell touches any more get their bed back.
        /// </summary>
        public static void FillIce(TerrainGrid g)
        {
            float top = g.ClampHeight(g.WaterLevel + 0.05f);
            for (int cz = 0; cz <= g.H; cz++)
                for (int cx = 0; cx <= g.W; cx++)
                {
                    bool touch = false;
                    for (int k = 0; k < 4 && !touch; k++)
                    {
                        int x = cx - (k & 1), z = cz - (k >> 1);
                        if (x >= 0 && z >= 0 && x < g.W && z < g.H && g.Frozen[g.CellIndex(x, z)]) touch = true;
                    }
                    int i = g.Corner(cx, cz);
                    if (touch && !g.IceBed.ContainsKey(i))
                    {
                        g.IceBed[i] = g.Heights[i];
                        if (g.Heights[i] < top) g.Heights[i] = top;
                    }
                    else if (!touch && g.IceBed.TryGetValue(i, out float bed))
                    {
                        g.Heights[i] = bed;
                        g.IceBed.Remove(i);
                    }
                }
        }

        /// <summary>The water's surface moves to `to` (relative): cells under it that the flood reaches become water,
        /// and water cells whose ground is now above it dry. Returns the changed cells.</summary>
        private static Dirty SetLevel(TerrainGrid g, Shape s, float to, string flood, bool nav)
        {
            g.WaterLevel = to;
            var d = Dirty.None;
            float CellLow(int x, int z) => Mathf.Min(Mathf.Min(g.Heights[g.Corner(x, z)], g.Heights[g.Corner(x + 1, z)]), Mathf.Min(g.Heights[g.Corner(x, z + 1)], g.Heights[g.Corner(x + 1, z + 1)]));
            float CellMid(int x, int z) => (g.Heights[g.Corner(x, z)] + g.Heights[g.Corner(x + 1, z)] + g.Heights[g.Corner(x, z + 1)] + g.Heights[g.Corner(x + 1, z + 1)]) / 4f;
            bool In(int x, int z) => s.Everywhere || s.Weight(g.CornerX(x) + g.Cell / 2f, g.CornerZ(z) + g.Cell / 2f) >= 0.5f;
            // Dry what the water left (its ground's middle above the surface).
            for (int z = 0; z < g.H; z++)
                for (int x = 0; x < g.W; x++)
                {
                    int i = g.CellIndex(x, z);
                    if (g.Water[i] && In(x, z) && CellMid(x, z) >= to) { g.Water[i] = false; g.Frozen[i] = false; d.Add(x, z); }
                }
            if (flood != "none")
            {
                var queue = new Queue<int>();
                bool any = false;
                for (int i = 0; i < g.Water.Length; i++) if (g.Water[i]) { queue.Enqueue(i); any = true; }
                bool all = flood == "all" || !any;
                for (int z = 0; z < g.H; z++)
                    for (int x = 0; x < g.W; x++)
                    {
                        int i = g.CellIndex(x, z);
                        if (g.Water[i] || !In(x, z) || CellMid(x, z) >= to || CellLow(x, z) >= to) continue;
                        if (all) { g.Water[i] = true; if (g.Host != null) g.Forced[i] = true; d.Add(x, z); }
                    }
                if (!all)
                    while (queue.Count > 0)
                    {
                        int i = queue.Dequeue();
                        int x = i % g.W, z = i / g.W;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                            if (nx < 0 || nz < 0 || nx >= g.W || nz >= g.H) continue;
                            int j = g.CellIndex(nx, nz);
                            if (g.Water[j] || !In(nx, nz) || CellMid(nx, nz) >= to) continue;
                            g.Water[j] = true;
                            if (g.Host != null) g.Forced[j] = true;
                            d.Add(nx, nz);
                            queue.Enqueue(j);
                        }
                    }
            }
            // The plane is drawn at the level everywhere: every chunk with water re-meshes.
            var all2 = new Dirty { X0 = 0, Z0 = 0, X1 = g.W - 1, Z1 = g.H - 1 };
            GroundLayer.Rebuild(all2);
            if (!d.Empty) QueueNav(g, d, now: nav);
            return d;
        }

        /// <summary>Re-mesh what changed and (unless only paint changed) rebuild the graphs there.</summary>
        private static void Commit(TerrainGrid g, Dirty d, bool nav = true)
        {
            GroundLayer.Rebuild(d);
            LiftKing(g, d);
            if (nav) QueueNav(g, d, now: true);
        }

        private static Bounds WorldBounds(TerrainGrid g, Dirty d)
        {
            var min = new Vector3(g.CornerX(d.X0), -50f, g.CornerZ(d.Z0));
            var max = new Vector3(g.CornerX(d.X1 + 1), 80f, g.CornerZ(d.Z1 + 1));
            var b = new Bounds((min + max) / 2f, max - min);
            b.Expand(new Vector3(12f, 0f, 12f));   // ramps, cliff walls and the large units' radius round it
            return b;
        }

        private static void QueueNav(TerrainGrid g, Dirty d, bool now)
        {
            pendingNav.Add(WorldBounds(g, d));
            if (now) lastNav = -10f;
        }

        /// <summary>The king stands on the ground's collider: ground that rose under him lifts him onto it.</summary>
        private static void LiftKing(TerrainGrid g, Dirty d)
        {
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.transform : null;
            if (king == null) return;
            var p = king.position;
            if (!WorldBounds(g, d).Contains(new Vector3(p.x, 0f, p.z))) return;
            if (GroundLayer.TryHeight(p, out float y) && y > p.y - 0.05f)
            {
                var cc = king.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                king.position = new Vector3(p.x, y + 0.05f, p.z);
                if (cc != null) cc.enabled = true;
            }
        }

        // ------------------------------------------------------------------ animation and graph updates

        private sealed class Anim
        {
            public List<(int i, float from, float to)> Corners;
            public bool Water;
            public float From, To, Over, T;
            public string Flood;
            public Shape Shape;
            public float LastStep;
        }

        /// <summary>Once a frame (LogicWorldVerbsManager): advance animations, then flush graph updates (at most 4 a second
        /// while something is still moving).</summary>
        public static void Tick(float dt)
        {
            if (!GroundLayer.Active) { anims.Clear(); pendingNav.Clear(); return; }
            var g = GroundLayer.Grid;
            for (int k = anims.Count - 1; k >= 0; k--)
            {
                var a = anims[k];
                a.T = Mathf.Min(a.Over, a.T + dt);
                bool done = a.T >= a.Over;
                // Re-mesh about 10 times a second (and at the end).
                if (!done && a.T - a.LastStep < 0.1f) continue;
                a.LastStep = a.T;
                float f = a.Over > 0f ? a.T / a.Over : 1f;
                if (a.Water)
                {
                    var d = SetLevel(g, a.Shape, Mathf.Lerp(a.From, a.To, f), a.Flood, nav: done);
                    if (!d.Empty && !done) QueueNav(g, d, now: false);
                }
                else
                {
                    foreach (var (i, from, to) in a.Corners) g.Heights[i] = done ? to : Mathf.Lerp(from, to, f);
                    var d = DirtyOf(g, a.Corners.Select(c => c.i));
                    GroundLayer.Rebuild(d);
                    LiftKing(g, d);
                    if (done) QueueNav(g, d, now: true);
                }
                if (done) anims.RemoveAt(k);
            }
            if (pendingNav.Count == 0 || Time.time - lastNav < 1f) return;
            lastNav = Time.time;
            var areas = Merge(pendingNav);
            pendingNav.Clear();
            long ms = GroundGraphs.UpdateArea(areas);
            int pushed = areas.Sum(BodyNav.PushOut);
            Log.For<LogicWorldVerbsManager>().Info($"terrain: graphs rebuilt round {areas.Length} area(s) in {ms} ms{(GroundGraphs.Tiled ? "" : " (one-tile graphs: rebuilt whole)")}; {pushed} unit(s) moved to walkable ground");
        }

        private static Bounds[] Merge(List<Bounds> list)
        {
            var outList = new List<Bounds>();
            foreach (var b in list)
            {
                int hit = outList.FindIndex(o => o.Intersects(b));
                if (hit < 0) outList.Add(b);
                else { var m = outList[hit]; m.Encapsulate(b); outList[hit] = m; }
            }
            return outList.ToArray();
        }

        // ------------------------------------------------------------------ savestates

        public static JToken Capture()
        {
            if (!Changed || !GroundLayer.Active) return null;
            var g = GroundLayer.Grid;
            // Animations end where they were going.
            foreach (var a in anims)
            {
                if (a.Water) g.WaterLevel = a.To;
                else foreach (var (i, _, to) in a.Corners) g.Heights[i] = to;
            }
            var def = GroundCodec.Encode(HostRelative(g));
            return new JObject
            {
                ["terrain"] = JToken.FromObject(def),
                ["frozen"] = Bits(g.Frozen),
                ["forced"] = Bits(g.Forced),
                ["iceBed"] = new JObject(g.IceBed.Select(kv => new JProperty(kv.Key.ToString(), kv.Value))),
                ["flat"] = GroundLayer.Flat,
            };
        }

        /// <summary>The pack form keeps a map's own ground as offsets from the host's (Encode expects that).</summary>
        private static TerrainGrid HostRelative(TerrainGrid g)
        {
            if (g.Host == null) return g;
            var c = g.Clone();
            for (int i = 0; i < c.Heights.Length; i++) if (!float.IsNaN(c.Host[i])) c.Heights[i] -= c.Host[i];
            return c;
        }

        public static void Restore(JToken state)
        {
            if (state is not JObject o || o["terrain"] == null) return;
            var def = o["terrain"].ToObject<Model.TerrainDef>();
            var grid = GroundCodec.Decode(def);
            ReadBits((string)o["frozen"], grid.Frozen);
            ReadBits((string)o["forced"], grid.Forced);
            if (o["iceBed"] is JObject bed) foreach (var p in bed.Properties()) grid.IceBed[int.Parse(p.Name)] = (float)p.Value;
            GroundLayer.Show(SceneManager.GetActiveScene(), grid, (bool?)o["flat"] ?? false);
            Changed = true;
            anims.Clear();
            pendingNav.Clear();
            GroundGraphs.Scan();
        }

        private static string Bits(bool[] b)
        {
            var bytes = new byte[(b.Length + 7) / 8];
            for (int i = 0; i < b.Length; i++) if (b[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            return Convert.ToBase64String(bytes);
        }

        private static void ReadBits(string s, bool[] into)
        {
            if (string.IsNullOrEmpty(s)) return;
            var bytes = Convert.FromBase64String(s);
            for (int i = 0; i < into.Length && (i >> 3) < bytes.Length; i++) into[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
        }

        public static string Status() => GroundLayer.Active
            ? $"terrain {GroundLayer.Grid.W}x{GroundLayer.Grid.H}, water level {GroundLayer.Grid.Y + GroundLayer.Grid.WaterLevel:0.00}, water {GroundLayer.Grid.Water.Count(b => b)}, ice {GroundLayer.Grid.Frozen.Count(b => b)}, anims {anims.Count}, changed {Changed}"
            : "no terrain";
    }
}
