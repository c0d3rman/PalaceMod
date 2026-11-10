using System;
using System.Collections.Generic;
using System.Linq;
using Pathfinding;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/*
    A level's terrain in the loaded host scene (docs/builder-ux.md, "Terrain"). One root object in the host scene (so it
    goes with the scene) holds, per 32x32-cell chunk:
    - on a "flat" base, the ground: a readable mesh on the Ground layer (the recast graphs rasterize it) with a
      MeshCollider (the king walks on it, coins and lobbed shots hit it), drawn with copies of the level's own ground
      materials, re-tinted whenever ColorAndLightManager recolours the originals (so it follows the scheme) and moved
      apart where the scheme gives several paints one colour (GroundPalette); faces too steep to walk are rock, cells
      beside water are sand;
    - blocked and water cells: invisible readable columns on the Obstacle layer that reach above the graphs' bounds
      (so they cut the navmesh and leave no walkable top), with a MeshCollider that stops the king; fliers ignore them;
    - water: a plane at the water level with the river material.
    A flat base also hides the host's ground, cliffs, rivers, lakes and ponds (all its water but a sea reaching past the
    field, which stays so the field reads as an island), roads, invisible walls and flier surface, raises the
    graphs' height range to the terrain's, and brings its own: walls round the field (king) and a flier surface 5 m
    above the ground (layer Flyer Surface, only switched on while GroundGraphs.Scan runs, as the game's is).

    Play: LevelOverlay calls Apply after Awake and before Start; the level-start rescan (GroundGraphs.Scan) then builds
    the graphs and the minimap. Edit: the Studio calls Show/Rebuild and marks the graphs stale until its next check.
*/
namespace PalaceMod.Builder.Level
{
    internal static class GroundLayer
    {
        public const int ChunkCells = 32;
        public const float AirHeight = 5f;     // fliers spawn 5 m up (Spawn.cs) and fly on a surface that high
        private const string RootName = LayoutScene.AddedPrefix + "ground";
        private const int GroundLayerIndex = 6, ObstacleLayerIndex = 7, FlyerLayerIndex = 9, WaterLayerIndex = 4;

        private sealed class Chunk
        {
            public int X0, Z0, X1, Z1;   // cells, exclusive end
            public Mesh Ground, Blocks, Water, Marks, Paint, Walls, Ice;
            public MeshFilter GroundFilter, BlocksFilter, WaterFilter;
            public MeshRenderer MarksRenderer;
            public MeshCollider GroundCollider, BlocksCollider, WallsCollider;
        }

        private static GameObject root;
        private static Chunk[,] chunks;
        private static Mesh airMesh;
        private static MeshRenderer air;
        private static float blockBottom, blockTop;
        /// <summary>The ground graphs' max slope: steeper triangles are rock faces and walls for the king.</summary>
        private static float walkSlope = 30f;
        public static float WalkSlopeDegrees => walkSlope;
        private static Material[] materials, sources;
        private static ColorAndLightManager listening;
        private static Material water;
        private static readonly List<(Behaviour b, Renderer r, Collider c, GameObject go)> hidden = [];

        /// <summary>The terrain being shown, or null.</summary>
        public static TerrainGrid Grid { get; private set; }

        /// <summary>KeyOf the level data the shown ground was built from (the Studio rebuilds when the level's differs).</summary>
        public static string SourceKey;

        /// <summary>A comparable digest of a level's base and terrain.</summary>
        public static string KeyOf(string levelBase, TerrainDef t) => t == null ? $"{levelBase}|-" :
            $"{levelBase}|{t.Format}|{t.PaintRes}|{string.Join(",", t.Origin ?? [])}|{t.Cell}|{string.Join(",", t.Size ?? [])}|{t.Y}|{t.Heights}|{t.Materials}|{t.Blocked}|{t.Water?.Level}|{t.Water?.Cells}";
        public static bool Active => root != null && Grid != null;
        /// <summary>Whether the host's ground is replaced (a flat base).</summary>
        public static bool Flat => Active && flat;
        private static bool flat;
        /// <summary>The flier surface is ours: GroundGraphs.Scan leaves the host's off.</summary>
        public static bool ReplacesAir => Flat;
        /// <summary>The host's invisible walls are hidden (a flat base): GroundGraphs.Scan leaves them off.</summary>
        public static bool ReplacesWalls => Flat;

        // ---- Entry points -------------------------------------------------------------------------------------

        /// <summary>Whether a level has a ground layer at all.</summary>
        public static bool Wants(LevelDef level) => level != null && (level.Terrain != null || IsFlat(level));

        public static bool IsFlat(LevelDef level) => level?.Base == LevelBases.Flat;

        /// <summary>Play: builds the level's terrain in the freshly loaded host (after Awake, before Start).</summary>
        public static void Apply(Scene scene, DesignPack pack, LevelDef level, List<ValidationIssue> issues)
        {
            if (!Wants(level)) return;
            TerrainGrid grid;
            try
            {
                grid = Decode(level) ?? NewFlat(scene);
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                issues.Add(new ValidationIssue(Severity.Error, $"levels[{level.Id}].terrain", "terrain: " + e.Message));
                return;
            }
            if (grid == null) return;
            Show(scene, grid, IsFlat(level));
            SourceKey = KeyOf(level.Base, level.Terrain);
            SnapToGround(scene);
            Log.For<BuilderManager>().Info($"terrain on {scene.name}: {grid.W}x{grid.H} cells of {grid.Cell} m, {(Flat ? "flat base" : $"host ground, {SculptedCount(grid)} cells sculpted, {HostCut.Describe()}")}, {grid.Blocked.Count(b => b)} blocked, {grid.Water.Count(b => b)} water");
        }

        /// <summary>Reads the level's terrain (null without one). Throws FormatException on a malformed one.</summary>
        public static TerrainGrid Decode(LevelDef level) => level?.Terrain == null ? null : GroundCodec.Decode(level.Terrain);

        /// <summary>A flat grid over the host's playable area (its graphs' bounds) at the castle's height.</summary>
        public static TerrainGrid NewFlat(Scene scene, float cell = 2f)
        {
            var bounds = GraphBounds();
            float y = PathCheck.Castle(scene)?.y ?? 0f;
            if (bounds.size.x < 1f) bounds = new Bounds(PathCheck.Castle(scene) ?? Vector3.zero, new Vector3(160f, 20f, 160f));
            float ox = Mathf.Floor(bounds.min.x / cell) * cell, oz = Mathf.Floor(bounds.min.z / cell) * cell;
            int w = Mathf.Clamp(Mathf.CeilToInt((bounds.max.x - ox) / cell), 1, TerrainGrid.MaxCells);
            int h = Mathf.Clamp(Mathf.CeilToInt((bounds.max.z - oz) / cell), 1, TerrainGrid.MaxCells);
            return new TerrainGrid(w, h, cell, ox, oz, y);
        }

        /// <summary>Shows a grid in the host scene, replacing what was shown.</summary>
        public static void Show(Scene scene, TerrainGrid grid, bool flatBase)
        {
            Clear();
            Grid = grid;
            flat = flatBase;
            root = new GameObject(RootName);
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(root, scene);
            LoadMaterials();
            if (flatBase)
            {
                HideHost(scene);
                WidenGraphs(grid);
            }
            else
            {
                // A map's own ground: the pack holds offsets from it; sample it, then make the heights absolute.
                SampleHost(grid);
                HostCut.Collect(scene, Owned);
                if (SculptedCount(grid) > 0) WidenHeights(grid);
            }
            var gb = GraphBounds();
            walkSlope = WalkSlope();
            blockBottom = Mathf.Min(gb.min.y, TerrainGrid.MinHeight) - 2f;
            blockTop = gb.max.y + 2f;
            int cw = (grid.W + ChunkCells - 1) / ChunkCells, ch = (grid.H + ChunkCells - 1) / ChunkCells;
            chunks = new Chunk[cw, ch];
            for (int x = 0; x < cw; x++)
                for (int z = 0; z < ch; z++)
                    chunks[x, z] = MakeChunk(x * ChunkCells, z * ChunkCells, Math.Min(grid.W, (x + 1) * ChunkCells), Math.Min(grid.H, (z + 1) * ChunkCells));
            if (flatBase)
            {
                MakeWalls(grid);
                MakeAir();
            }
            Rebuild(new Dirty { X0 = 0, Z0 = 0, X1 = grid.W - 1, Z1 = grid.H - 1 });
        }

        /// <summary>Re-meshes the chunks a brush touched (the graphs aren't rescanned; GroundGraphs.Scan does that).</summary>
        public static void Rebuild(Dirty d)
        {
            if (!Active || d.Empty) return;
            // Neighbouring cells change too: smooth normals and the sand shore read across a chunk edge.
            int x0 = Math.Max(0, d.X0 - 1) / ChunkCells, z0 = Math.Max(0, d.Z0 - 1) / ChunkCells;
            int x1 = Math.Min(Grid.W - 1, d.X1 + 1) / ChunkCells, z1 = Math.Min(Grid.H - 1, d.Z1 + 1) / ChunkCells;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    BuildChunk(chunks[x, z]);
            if (air != null) BuildAir();
            if (!flat) HostCut.Apply(Grid);
        }

        private static int SculptedCount(TerrainGrid g)
        {
            if (g.Host == null) return g.W * g.H;
            int n = 0;
            for (int z = 0; z < g.H; z++)
                for (int x = 0; x < g.W; x++)
                    if (g.Sculpted(x, z)) n++;
            return n;
        }

        /// <summary>The host's ground height at every corner (its Ground-layer colliders, raycast from above), and the
        /// grid's heights from offsets to absolute.</summary>
        private static void SampleHost(TerrainGrid g)
        {
            var host = new float[g.Heights.Length];
            var hits = new RaycastHit[16];
            float top = g.MaxWorld + 60f;
            for (int z = 0; z <= g.H; z++)
                for (int x = 0; x <= g.W; x++)
                {
                    float best = float.NaN;
                    int n = Physics.RaycastNonAlloc(new Vector3(g.CornerX(x), top, g.CornerZ(z)), Vector3.down, hits, top + 40f, 1 << GroundLayerIndex, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                        if (!Owned(hits[i].collider.transform) && (float.IsNaN(best) || hits[i].point.y > best)) best = hits[i].point.y;
                    host[g.Corner(x, z)] = float.IsNaN(best) ? float.NaN : best - g.Y;
                }
            g.Host = host;
            for (int i = 0; i < host.Length; i++)
                if (!float.IsNaN(host[i])) g.Heights[i] = g.ClampHeight(host[i] + g.Heights[i]);
        }

        /// <summary>A map's own ground: the graphs' height range has to hold the sculpted ground.</summary>
        private static void WidenHeights(TerrainGrid g)
        {
            if (AstarPath.active == null) return;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int z = 0; z < g.H; z++)
                for (int x = 0; x < g.W; x++)
                {
                    if (!g.Sculpted(x, z)) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        float h = g.Y + g.Heights[g.Corner(x + (k & 1), z + (k >> 1))];
                        lo = Mathf.Min(lo, h);
                        hi = Mathf.Max(hi, h);
                    }
                }
            foreach (var r in AstarPath.active.data.graphs.OfType<RecastGraph>())
            {
                if (r.name == "Enemy Units Air") continue;
                var c = r.forcedBoundsCenter;
                var size = r.forcedBoundsSize;
                float y0 = Mathf.Min(c.y - size.y / 2f, lo - 2f), y1 = Mathf.Max(c.y + size.y / 2f, hi + 3f);
                r.forcedBoundsCenter = new Vector3(c.x, (y0 + y1) / 2f, c.z);
                r.forcedBoundsSize = new Vector3(size.x, y1 - y0, size.z);
            }
        }

        /// <summary>Removes the terrain and gives the host back what it hid.</summary>
        public static void Clear()
        {
            foreach (var (b, r, c, go) in hidden)
            {
                if (b != null) b.enabled = true;
                if (r != null) r.enabled = true;
                if (c != null) c.enabled = true;
                if (go != null) go.SetActive(true);
            }
            hidden.Clear();
            HostCut.Restore();
            DropPaints();
            GroundGraphs.Extra.Clear();
            if (root != null)
            {
                root.SetActive(false);   // its colliders go now (Destroy waits for the frame's end; a re-Show samples the host)
                UnityEngine.Object.Destroy(root);
            }
            root = null;
            Grid = null;
            chunks = null;
            air = null;
            flat = false;
            SourceKey = null;
        }

        /// <summary>The terrain's surface height under a point, when it has a ground of its own there.</summary>
        public static bool TryHeight(Vector3 p, out float y)
        {
            y = p.y;
            if (!Active || !Grid.Contains(p.x, p.z)) return false;
            if (!flat && !(Grid.CellAt(p.x, p.z, out int cx, out int cz) && Grid.Sculpted(cx, cz))) return false;
            y = Grid.SurfaceAt(p.x, p.z);
            return true;
        }

        /// <summary>The water surface's height at a point over the terrain's open (unfrozen) water, when it is.</summary>
        public static bool TryWater(Vector3 p, out float y)
        {
            y = 0f;
            if (!Active || !Grid.CellAt(p.x, p.z, out int cx, out int cz)) return false;
            int i = Grid.CellIndex(cx, cz);
            if (!Grid.Water[i]) return false;
            y = Grid.Y + Grid.WaterLevel + (Grid.Frozen[i] ? IceLift : 0f);
            return true;
        }

        /// <summary>
        /// The point is over one of the terrain's blocked or open-water cells. Their columns reach above the graphs, so a
        /// physics ray starting inside one misses it, and recast sees only their sides (a column has no top or bottom
        /// inside the graphs' bounds): ask the grid instead.
        /// </summary>
        public static bool ClosedCell(Vector3 p) => Active && Grid.CellAt(p.x, p.z, out int cx, out int cz) && IsBlocked(Grid, cx, cz);

        /// <summary>Where a ray from the camera meets the terrain's own ground (exact over cliffs, unlike a ground-plane
        /// guess), when it does.</summary>
        public static bool TryRaycast(Ray ray, out Vector3 point)
        {
            point = default;
            if (!Active) return false;
            if (!flat)
            {
                // A map's own ground (cut where sculpted) and the sculpted cells are all on the Ground layer.
                if (!Physics.Raycast(ray, out var hit, 3000f, 1 << GroundLayerIndex, QueryTriggerInteraction.Ignore)) return false;
                point = hit.point;
                return true;
            }
            if (!Grid.Raycast(ray.origin.x, ray.origin.y, ray.origin.z, ray.direction.x, ray.direction.y, ray.direction.z, out float x, out float y, out float z)) return false;
            point = new Vector3(x, y, z);
            return true;
        }

        /// <summary>The ground graphs' max slope (the strictest), 30 if there are none.</summary>
        private static float WalkSlope()
        {
            var graphs = AstarPath.active?.data.graphs.OfType<RecastGraph>().Where(r => r.name != "Enemy Units Air").ToList();
            return graphs == null || graphs.Count == 0 ? 30f : graphs.Min(r => r.maxSlope);
        }

        // ---- Flat base: the host's ground goes --------------------------------------------------------------------

        private static void HideHost(Scene scene)
        {
            var ground = new HashSet<Material>(GroundMaterials.All().Where(kv => kv.Key != "oceanMaterial" && kv.Value != null).Select(kv => kv.Value));
            // The dark shapes drawn along rivers and roads go with them, and so do waterfalls.
            if (GroundMaterials.Get("shadowShapeMaterial") is Material shadow) ground.Add(shadow);
            if (GroundMaterials.Get("waterfallMaterial") is Material fall) ground.Add(fall);
            // The ocean material also draws lakes and ponds (Moorweg's): those go, a sea round the field stays.
            var ocean = GroundMaterials.Get("oceanMaterial");
            var keepLayers = new HashSet<int> { 0, GroundLayerIndex, ObstacleLayerIndex, WaterLayerIndex, 21 };
            foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || r.gameObject.scene != scene || !keepLayers.Contains(r.gameObject.layer)) continue;
                if (r.sharedMaterials.Length == 0 || !r.sharedMaterials.All(m => m != null && (ground.Contains(m) || (m == ocean && OnField(r.bounds))))) continue;
                if (Owned(r.transform)) continue;
                if (r.enabled)
                {
                    r.enabled = false;
                    hidden.Add((null, r, null, null));
                }
                // Once the level runs, MeshFusion turns its sources and combined chunks back on: keep it off them.
                foreach (var mb in r.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.enabled && (mb.GetType().Namespace ?? "").StartsWith("NGS.MeshFusionPro", StringComparison.Ordinal))
                    {
                        mb.enabled = false;
                        hidden.Add((mb, null, null, null));
                    }
                foreach (var c in r.GetComponents<Collider>()) Hide(c);
            }
            foreach (var c in Resources.FindObjectsOfTypeAll<Collider>())
                if (c != null && c.gameObject.scene == scene && c.enabled && c.gameObject.layer == GroundLayerIndex && !Owned(c.transform)) Hide(c);
            // The map's own invisible walls along its rivers and cliffs (PathMesher strips such as Nordfels'
            // "InvisiWall (3)", outside NavmeshBakeHelper.invisibleWalls): the ground they fenced is gone, and they'd
            // stop the king in the middle of the new field (the Far Bank's "invisible warrior").
            foreach (var pm in Resources.FindObjectsOfTypeAll<PathMesher>())
                if (pm != null && pm.gameObject.scene == scene && !Owned(pm.transform))
                    foreach (var c in pm.GetComponentsInChildren<Collider>(true))
                        if (!c.isTrigger) Hide(c);
            foreach (var helper in UnityEngine.Object.FindObjectsOfType<NavmeshBakeHelper>())
            {
                var walls = HarmonyLib.Traverse.Create(helper).Field("invisibleWalls").GetValue<GameObject>();
                if (walls != null && walls.activeSelf)
                {
                    walls.SetActive(false);
                    hidden.Add((null, null, null, walls));
                }
            }
        }

        private static bool OnField(Bounds b) => Grid != null &&
            GroundPalette.IsLake(b.min.x, b.min.z, b.max.x, b.max.z, Grid.OriginX, Grid.OriginZ, Grid.OriginX + Grid.SizeX, Grid.OriginZ + Grid.SizeZ);

        private static void Hide(Collider c)
        {
            if (c == null || !c.enabled) return;
            c.enabled = false;
            hidden.Add((null, null, c, null));
        }

        /// <summary>Whether something belongs to a slot, unit, spawner or the mod, not to the map's ground.</summary>
        internal static bool Owned(Transform t) =>
            t.GetComponentInParent<BuildSlot>(true) != null || t.GetComponentInParent<Hp>(true) != null ||
            t.GetComponentInParent<EnemySpawner>(true) != null || t.GetComponentInParent<PlayerMovement>(true) != null ||
            t.root.name.StartsWith(LayoutScene.AddedPrefix);

        /// <summary>Puts slots and the king on the new ground (they stood on the host's).</summary>
        public static void SnapToGround(Scene scene)
        {
            foreach (var slot in LayoutScene.Slots(scene))
            {
                if (slot.transform.parent != null && slot.transform.parent.GetComponentInParent<BuildSlot>(true) != null) continue;
                var p = slot.transform.position;
                if (TryHeight(p, out float y)) slot.transform.position = new Vector3(p.x, y, p.z);
            }
            var king = PlayerMovement.instance;
            if (king != null && king.gameObject.scene == scene && TryHeight(king.transform.position, out float ky))
                king.transform.position = new Vector3(king.transform.position.x, ky + 0.05f, king.transform.position.z);
        }

        /// <summary>The graphs' height range has to hold the terrain; fliers may climb any slope on their surface.</summary>
        private static void WidenGraphs(TerrainGrid g)
        {
            if (AstarPath.active == null) return;
            float lo = TerrainGrid.MinHeight - 2f, hi = g.MaxWorld + AirHeight + 3f;
            foreach (var r in AstarPath.active.data.graphs.OfType<RecastGraph>())
            {
                var c = r.forcedBoundsCenter;
                var s = r.forcedBoundsSize;
                float x0 = Mathf.Min(c.x - s.x / 2f, g.OriginX), x1 = Mathf.Max(c.x + s.x / 2f, g.OriginX + g.SizeX);
                float z0 = Mathf.Min(c.z - s.z / 2f, g.OriginZ), z1 = Mathf.Max(c.z + s.z / 2f, g.OriginZ + g.SizeZ);
                r.forcedBoundsCenter = new Vector3((x0 + x1) / 2f, (lo + hi) / 2f, (z0 + z1) / 2f);
                r.forcedBoundsSize = new Vector3(x1 - x0, hi - lo, z1 - z0);
                if (r.name == "Enemy Units Air")
                {
                    r.maxSlope = 89f;
                    r.walkableClimb = Mathf.Max(r.walkableClimb, 4f);
                }
            }
        }

        private static Bounds GraphBounds()
        {
            var g = AstarPath.active?.data.graphs.OfType<RecastGraph>().FirstOrDefault();
            return g == null ? new Bounds(Vector3.zero, new Vector3(0f, 30f, 0f)) : new Bounds(g.forcedBoundsCenter, g.forcedBoundsSize);
        }

        // ---- Meshes -------------------------------------------------------------------------------------------

        private static void LoadMaterials()
        {
            var all = GroundMaterials.All();
            Material M(string f) => all.TryGetValue(f, out var m) && m != null ? m : all.TryGetValue("groundMaterial", out var g) ? g : null;
            DropPaints();
            // Indexed by GroundMaterial. Copies: their colours are moved apart from the scheme's (GroundPalette).
            sources = [M("groundMaterial"), M("groundHigh"), M("groundLow"), M("earthMaterial"), M("sandMaterial"), M("roadMaterial"), M("rockMaterial")];
            materials = sources.Select((m, i) => m == null ? null : new Material(m) { name = $"{LayoutScene.AddedPrefix}paint {(GroundMaterial)i}" }).ToArray();
            water = M("waterMaterial");
            Recolour();
            // The scheme is applied in ColorAndLightManager.Start (after this) and again on every scheme switch.
            listening = ColorAndLightManager.Instance ?? UnityEngine.Object.FindObjectOfType<ColorAndLightManager>();
            listening?.OnColorChange.AddListener(Recolour);
        }

        // What ColorAndLightManager sets on a ground material (SetMaterialColors, and the outline's tint by daylight).
        private static readonly string[] SchemeColours = ["_BaseColor", "_ColorDim", "_ColorDimExtra", "_UnityShadowColor", "_OutlineColor"];

        /// <summary>Copies the scheme's current colours onto the paints, then moves the paints' own colours apart.</summary>
        private static void Recolour()
        {
            if (materials == null || sources == null) return;
            var host = new Rgb[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null || materials[i] == null) continue;
                foreach (var p in SchemeColours)
                    if (src.HasProperty(p)) materials[i].SetColor(p, src.GetColor(p));
                var c = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.gray;
                host[i] = new Rgb(c.r, c.g, c.b);
            }
            var paint = GroundPalette.Resolve(host);
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != null && materials[i].HasProperty("_BaseColor"))
                    materials[i].SetColor("_BaseColor", new Color(paint[i].R, paint[i].G, paint[i].B, 1f));
        }

        /// <summary>A paint's colour as the field draws it (the brush strip's swatches), also with no field shown.</summary>
        public static Color PaintColour(GroundMaterial m)
        {
            if (materials != null && materials[(int)m] is Material mat && mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            var all = GroundMaterials.All();
            string[] fields = ["groundMaterial", "groundHigh", "groundLow", "earthMaterial", "sandMaterial", "roadMaterial", "rockMaterial"];
            var host = fields.Select(f =>
            {
                var src = all.TryGetValue(f, out var x) && x != null ? x : all.TryGetValue("groundMaterial", out var g) ? g : null;
                var c = src != null && src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.gray;
                return new Rgb(c.r, c.g, c.b);
            }).ToArray();
            var p = GroundPalette.Resolve(host)[(int)m];
            return new Color(p.R, p.G, p.B, 1f);
        }

        /// <summary>Each paint's colour now and the scheme's colour it came from (dev tools).</summary>
        public static string PaintColours() => materials == null ? "no paints" : string.Join("\n", materials.Select((m, i) =>
            $"{(GroundMaterial)i}: {Describe(m)} from {sources[i]?.name} {Describe(sources[i])}"));

        private static string Describe(Material m) => m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-";

        private static void DropPaints()
        {
            listening?.OnColorChange.RemoveListener(Recolour);
            listening = null;
            if (materials != null)
                foreach (var m in materials)
                    if (m != null) UnityEngine.Object.Destroy(m);
            materials = null;
            sources = null;
            if (iceMaterial != null) UnityEngine.Object.Destroy(iceMaterial);
            iceMaterial = null;
        }

        private static Chunk MakeChunk(int x0, int z0, int x1, int z1)
        {
            var c = new Chunk { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1 };
            {
                var go = Part($"chunk {x0 / ChunkCells},{z0 / ChunkCells}", GroundLayerIndex);
                c.Ground = NewMesh(go.name);
                c.GroundFilter = go.AddComponent<MeshFilter>();
                c.GroundFilter.sharedMesh = c.Ground;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = materials;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
                c.GroundCollider = go.AddComponent<MeshCollider>();

                // Paint outlines: drawn only (layer Default: the graphs don't rasterize it, nothing collides with it).
                var p = Part($"paint {x0 / ChunkCells},{z0 / ChunkCells}", 0);
                c.Paint = NewMesh(p.name);
                p.AddComponent<MeshFilter>().sharedMesh = c.Paint;
                var pr = p.AddComponent<MeshRenderer>();
                pr.sharedMaterials = materials;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = true;

                // Walls over faces too steep to walk: the king's collider, and walls for the ground graphs too (an
                // invisible renderer, so they're rasterized). The graphs leave steep ground out by itself, but their
                // erosion (agent radius) only stops at a span with a missing neighbour, and a steep face's voxels are
                // neighbours wherever it drops less than the graph's climb per 0.2 m voxel: a 6 m raised ramp
                // carried the large graph (radius 2-4 m) right to its edges. A column over the face is a wall.
                // The fliers' graph is scanned without them (GroundGraphs.Scan), like blocked cells.
                var k = Part(LayoutScene.CliffWallsPrefix + $"{x0 / ChunkCells},{z0 / ChunkCells}", ObstacleLayerIndex);
                c.Walls = NewMesh(k.name);
                c.WallsCollider = k.AddComponent<MeshCollider>();
                k.AddComponent<MeshFilter>().sharedMesh = c.Walls;
                k.AddComponent<MeshRenderer>().forceRenderingOff = true;   // enabled so the scanner takes it, never drawn

                var w = Part($"water {x0 / ChunkCells},{z0 / ChunkCells}", WaterLayerIndex);
                c.Water = NewMesh(w.name);
                c.WaterFilter = w.AddComponent<MeshFilter>();
                c.WaterFilter.sharedMesh = c.Water;
                var wr = w.AddComponent<MeshRenderer>();
                wr.sharedMaterial = water;
                wr.shadowCastingMode = ShadowCastingMode.Off;

                // Frozen water (the `terrain` action's freeze) is drawn as a pale sheet just over the ground, which the freeze
                // has filled up to the water level there (TerrainVerbs.FillIce): the ground carries the king and the graphs.
                var ice = Part($"ice {x0 / ChunkCells},{z0 / ChunkCells}", 0);
                c.Ice = NewMesh(ice.name);
                ice.AddComponent<MeshFilter>().sharedMesh = c.Ice;
                var ir = ice.AddComponent<MeshRenderer>();
                ir.sharedMaterial = IceMaterial();
                ir.shadowCastingMode = ShadowCastingMode.Off;
                ir.receiveShadows = true;
            }
            // Named like the layout props' blockers, so the path check sees a spawn walled in by them.
            var b = Part("blocker", ObstacleLayerIndex);
            c.Blocks = NewMesh(b.name);
            c.BlocksFilter = b.AddComponent<MeshFilter>();
            c.BlocksFilter.sharedMesh = c.Blocks;
            b.AddComponent<MeshRenderer>().forceRenderingOff = true;   // enabled so the scanner takes it, never drawn
            c.BlocksCollider = b.AddComponent<MeshCollider>();

            // Editor only: blocked cells drawn as a red wash on the ground.
            var m = Part("marks", 0);
            c.Marks = NewMesh(m.name);
            m.AddComponent<MeshFilter>().sharedMesh = c.Marks;
            c.MarksRenderer = m.AddComponent<MeshRenderer>();
            c.MarksRenderer.sharedMaterial = MarksMaterial();
            c.MarksRenderer.shadowCastingMode = ShadowCastingMode.Off;
            c.MarksRenderer.receiveShadows = false;
            c.MarksRenderer.enabled = showMarks;
            return c;
        }

        private static GameObject Part(string name, int layer)
        {
            var go = new GameObject(LayoutScene.AddedPrefix + name) { layer = layer };
            go.transform.SetParent(root.transform, false);
            return go;
        }

        private static Mesh NewMesh(string name) => new() { name = name, indexFormat = IndexFormat.UInt32 };

        private static void BuildChunk(Chunk c)
        {
            var g = Grid;
            BuildGround(g, c);
            BuildBlocks(g, c);
        }

        private static void BuildGround(TerrainGrid g, Chunk c)
        {
            int n = (c.X1 - c.X0) * (c.Z1 - c.Z0);
            var verts = new List<Vector3>(n * 4);
            var normals = new List<Vector3>(n * 4);
            var uvs = new List<Vector2>(n * 4);
            var tris = new List<int>[materials.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = [];
            var wverts = new List<Vector3>();
            var wtris = new List<int>();
            var wuvs = new List<Vector2>();
            var kv = new List<Vector3>();
            var kt = new List<int>();
            var iverts = new List<Vector3>();
            var itris = new List<int>();
            float wy = g.Y + g.WaterLevel;
            bool anyWater = g.AnyWater;
            var steepCells = new bool[n];
            for (int z = c.Z0; z < c.Z1; z++)
                for (int x = c.X0; x < c.X1; x++)
                {
                    // On a map's own ground only the sculpted cells are ours (HostCut leaves a hole for them).
                    if (!g.Sculpted(x, z)) continue;
                    float h00 = g.Heights[g.Corner(x, z)], h10 = g.Heights[g.Corner(x + 1, z)];
                    float h01 = g.Heights[g.Corner(x, z + 1)], h11 = g.Heights[g.Corner(x + 1, z + 1)];
                    if (g.Host != null) Skirts(g, x, z, verts, normals, uvs, tris[(int)GroundMaterial.Ground]);
                    int b = verts.Count;
                    AddCorner(g, x, z, h00, verts, normals, uvs);
                    AddCorner(g, x + 1, z, h10, verts, normals, uvs);
                    AddCorner(g, x, z + 1, h01, verts, normals, uvs);
                    AddCorner(g, x + 1, z + 1, h11, verts, normals, uvs);
                    // Each triangle is plain ground or, too steep to walk (the graphs' max slope), a rock face that the
                    // king can't walk either (a wall): what he can walk down he can walk up, as for every enemy.
                    g.CellSlope(x, z, out float s1, out float s2);
                    bool steep1 = s1 > walkSlope, steep2 = s2 > walkSlope;
                    steepCells[(z - c.Z0) * (c.X1 - c.X0) + x - c.X0] = steep1 || steep2;
                    var t1 = tris[(int)(steep1 ? GroundMaterial.Rock : GroundMaterial.Ground)];
                    var t2 = tris[(int)(steep2 ? GroundMaterial.Rock : GroundMaterial.Ground)];
                    float x0 = g.CornerX(x), z0 = g.CornerZ(z), x1 = x0 + g.Cell, z1 = z0 + g.Cell;
                    if (g.SplitsMain(x, z))
                    {
                        t1.AddRange([b, b + 2, b + 3]);
                        t2.AddRange([b, b + 3, b + 1]);
                        if (steep1 && steep2) AddBox(kv, kt, new Vector3(x0, blockBottom, z0), new Vector3(x1, blockTop, z1));
                        else if (steep1) AddPrism(kv, kt, x0, z0, x0, z1, x1, z1);
                        else if (steep2) AddPrism(kv, kt, x0, z0, x1, z1, x1, z0);
                    }
                    else
                    {
                        t1.AddRange([b, b + 2, b + 1]);
                        t2.AddRange([b + 1, b + 2, b + 3]);
                        if (steep1 && steep2) AddBox(kv, kt, new Vector3(x0, blockBottom, z0), new Vector3(x1, blockTop, z1));
                        else if (steep1) AddPrism(kv, kt, x0, z0, x0, z1, x1, z0);
                        else if (steep2) AddPrism(kv, kt, x1, z0, x0, z1, x1, z1);
                    }
                    // Water: its cells, and the cells round them that rise out of it (the bank hides the plane's edge,
                    // so the shore is where the slope meets the water, not a cell edge).
                    int ci = g.CellIndex(x, z);
                    if (g.Water[ci] && g.Frozen[ci])
                    {
                        int ib = iverts.Count;
                        iverts.AddRange([new Vector3(x0, wy + IceLift, z0), new Vector3(x1, wy + IceLift, z0), new Vector3(x0, wy + IceLift, z1), new Vector3(x1, wy + IceLift, z1)]);
                        itris.AddRange([ib, ib + 2, ib + 3, ib, ib + 3, ib + 1]);
                    }
                    else if (anyWater && (g.Water[ci] || (Mathf.Max(h00, h10, h01, h11) > g.WaterLevel && Mathf.Min(h00, h10, h01, h11) < g.WaterLevel && NextToWater(g, x, z))))
                    {
                        int wb = wverts.Count;
                        wverts.AddRange([new Vector3(x0, wy, z0), new Vector3(x1, wy, z0), new Vector3(x0, wy, z1), new Vector3(x1, wy, z1)]);
                        wuvs.AddRange([new Vector2(x0, z0) * 0.05f, new Vector2(x1, z0) * 0.05f, new Vector2(x0, z1) * 0.05f, new Vector2(x1, z1) * 0.05f]);
                        wtris.AddRange([wb, wb + 2, wb + 3, wb, wb + 3, wb + 1]);
                    }
                }
            var mesh = c.Ground;
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i, false);
            mesh.RecalculateBounds();
            c.GroundCollider.sharedMesh = null;
            if (verts.Count > 0) c.GroundCollider.sharedMesh = mesh;
            c.GroundCollider.enabled = verts.Count > 0;

            c.Water.Clear();
            c.Water.SetVertices(wverts);
            c.Water.SetUVs(0, wuvs);
            c.Water.SetTriangles(wtris, 0);
            c.Water.RecalculateNormals();
            c.Water.RecalculateBounds();

            c.Ice.Clear();
            c.Ice.SetVertices(iverts);
            c.Ice.SetTriangles(itris, 0);
            c.Ice.RecalculateNormals();
            c.Ice.RecalculateBounds();

            c.Walls.Clear();
            c.Walls.SetVertices(kv);
            c.Walls.SetTriangles(kt, 0);
            c.Walls.RecalculateBounds();
            c.WallsCollider.sharedMesh = null;
            if (kv.Count > 0) c.WallsCollider.sharedMesh = c.Walls;
            c.WallsCollider.enabled = kv.Count > 0;

            BuildPaint(g, c, steepCells);
        }

        /// <summary>Painted materials as smooth outlines a few cm over the ground (PaintOutline), one submesh each,
        /// later materials on top. Cliffs keep their rock.</summary>
        private static void BuildPaint(TerrainGrid g, Chunk c, bool[] steepCells)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>[materials.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = [];
            var present = PaintOutline.Present(g, c.X0, c.Z0, c.X1 - 1, c.Z1 - 1);
            var xz = new List<float>();
            int cw = c.X1 - c.X0;
            for (int m = 1; m < present.Length; m++)
            {
                if (!present[m] || materials[m] == null) continue;
                xz.Clear();
                PaintOutline.Build(g, c.X0, c.Z0, c.X1, c.Z1, (byte)m, (x, z) => steepCells[(z - c.Z0) * cw + x - c.X0], xz);
                float lift = PaintLift + PaintStepUp * m;
                var t = tris[m];
                for (int k = 0; k + 1 < xz.Count; k += 2)
                {
                    float x = xz[k], z = xz[k + 1];
                    t.Add(verts.Count);
                    verts.Add(new Vector3(x, PaintY(g, x, z) + lift, z));
                    normals.Add(NormalAt(g, x, z));
                    uvs.Add(new Vector2(x, z) * 0.05f);
                }
            }
            var mesh = c.Paint;
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i, false);
            mesh.RecalculateBounds();
        }

        private const float PaintLift = 0.03f, PaintStepUp = 0.012f;

        /// <summary>Where paint lies: the terrain's surface, or on a map's own ground outside the sculpted cells, the
        /// host's (raycast: the grid's samples of it are only a 2 m approximation).</summary>
        private static float PaintY(TerrainGrid g, float x, float z)
        {
            if (g.Host == null || (g.CellAt(x, z, out int cx, out int cz) && g.Sculpted(cx, cz))) return g.SurfaceAt(x, z);
            return Physics.Raycast(new Vector3(x, g.MaxWorld + 60f, z), Vector3.down, out var hit, g.MaxWorld + 100f, 1 << GroundLayerIndex, QueryTriggerInteraction.Ignore)
                ? hit.point.y : g.SurfaceAt(x, z);
        }

        /// <summary>A sculpted cell's edges that meet the host's ground get a short skirt down (both faces), so the
        /// seam where the cut host ground and the patch differ by a few cm shows no gap.</summary>
        private static void Skirts(TerrainGrid g, int x, int z, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris)
        {
            const float depth = 1f;
            for (int e = 0; e < 4; e++)
            {
                int nx = x + (e == 0 ? -1 : e == 1 ? 1 : 0), nz = z + (e == 2 ? -1 : e == 3 ? 1 : 0);
                if (nx >= 0 && nz >= 0 && nx < g.W && nz < g.H && g.Sculpted(nx, nz)) continue;
                // The edge's two corners.
                int ax = e == 1 ? x + 1 : x, az = e == 3 ? z + 1 : z;
                int bx = e == 0 ? x : x + 1, bz = e == 2 ? z : z + 1;
                var a = new Vector3(g.CornerX(ax), g.Y + g.Heights[g.Corner(ax, az)], g.CornerZ(az));
                var b = new Vector3(g.CornerX(bx), g.Y + g.Heights[g.Corner(bx, bz)], g.CornerZ(bz));
                var n = new Vector3(nx - x, 0f, nz - z);
                int i = verts.Count;
                verts.AddRange([a, b, a + Vector3.down * depth, b + Vector3.down * depth]);
                normals.AddRange([n, n, n, n]);
                uvs.AddRange([new Vector2(a.x, a.z) * 0.05f, new Vector2(b.x, b.z) * 0.05f, new Vector2(a.x, a.z) * 0.05f, new Vector2(b.x, b.z) * 0.05f]);
                tris.AddRange([i, i + 1, i + 3, i, i + 3, i + 2, i, i + 3, i + 1, i, i + 2, i + 3]);
            }
        }

        private static Vector3 CornerNormal(TerrainGrid g, int x, int z)
        {
            float l = g.Heights[g.Corner(Math.Max(0, x - 1), z)], r = g.Heights[g.Corner(Math.Min(g.W, x + 1), z)];
            float d = g.Heights[g.Corner(x, Math.Max(0, z - 1))], u = g.Heights[g.Corner(x, Math.Min(g.H, z + 1))];
            return new Vector3(l - r, 2f * g.Cell, d - u).normalized;
        }

        /// <summary>The ground's shading normal at a point (its corners' normals, blended).</summary>
        private static Vector3 NormalAt(TerrainGrid g, float x, float z)
        {
            float fx = Mathf.Clamp((x - g.OriginX) / g.Cell, 0f, g.W), fz = Mathf.Clamp((z - g.OriginZ) / g.Cell, 0f, g.H);
            int ix = Math.Min((int)fx, g.W - 1), iz = Math.Min((int)fz, g.H - 1);
            float tx = fx - ix, tz = fz - iz;
            var a = Vector3.Lerp(CornerNormal(g, ix, iz), CornerNormal(g, ix + 1, iz), tx);
            var b = Vector3.Lerp(CornerNormal(g, ix, iz + 1), CornerNormal(g, ix + 1, iz + 1), tx);
            return Vector3.Lerp(a, b, tz).normalized;
        }

        private static void AddCorner(TerrainGrid g, int x, int z, float h, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs)
        {
            float wx = g.CornerX(x), wz = g.CornerZ(z);
            verts.Add(new Vector3(wx, g.Y + h, wz));
            normals.Add(CornerNormal(g, x, z));
            uvs.Add(new Vector2(wx, wz) * 0.05f);
        }

        /// <summary>A wall the king can't pass over one steep triangle, from below the ground to above the graphs.</summary>
        private static void AddPrism(List<Vector3> v, List<int> t, float ax, float az, float bx, float bz, float cx, float cz)
        {
            int b0 = v.Count;
            v.AddRange([new Vector3(ax, blockBottom, az), new Vector3(bx, blockBottom, bz), new Vector3(cx, blockBottom, cz),
                        new Vector3(ax, blockTop, az), new Vector3(bx, blockTop, bz), new Vector3(cx, blockTop, cz)]);
            // Caps and three sides, both windings (whichever way the triangle runs, the capsule meets a front face).
            int s = t.Count;
            t.AddRange([b0, b0 + 2, b0 + 1, b0 + 3, b0 + 4, b0 + 5]);
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                t.AddRange([b0 + i, b0 + j, b0 + 3 + j, b0 + i, b0 + 3 + j, b0 + 3 + i]);
            }
            for (int k = s, e = t.Count; k < e; k += 3) t.AddRange([t[k], t[k + 2], t[k + 1]]);
        }

        private static bool NextToWater(TerrainGrid g, int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx >= 0 && nz >= 0 && nx < g.W && nz < g.H && g.OpenWater(g.CellIndex(nx, nz))) return true;
                }
            return false;
        }

        /// <summary>One column per blocked or water cell (merged along rows), from below the ground to above the graphs.</summary>
        private static void BuildBlocks(TerrainGrid g, Chunk c)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int z = c.Z0; z < c.Z1; z++)
            {
                int run = -1;
                for (int x = c.X0; x <= c.X1; x++)
                {
                    bool on = x < c.X1 && IsBlocked(g, x, z);
                    if (on && run < 0) run = x;
                    if (!on && run >= 0)
                    {
                        AddBox(verts, tris, new Vector3(g.CornerX(run), blockBottom, g.CornerZ(z)), new Vector3(g.CornerX(x), blockTop, g.CornerZ(z + 1)));
                        run = -1;
                    }
                }
            }
            c.Blocks.Clear();
            c.Blocks.SetVertices(verts);
            c.Blocks.SetTriangles(tris, 0);
            c.Blocks.RecalculateBounds();
            c.BlocksCollider.sharedMesh = null;
            if (verts.Count > 0) c.BlocksCollider.sharedMesh = c.Blocks;
            c.BlocksCollider.enabled = verts.Count > 0;
            if (showMarks) BuildMarks(g, c);
        }

        private static bool showMarks;

        /// <summary>Whether blocked cells are drawn (the Studio's edit mode).</summary>
        /// <summary>Dev: switches the king's cliff walls off (to see what he does without them) or back on.</summary>
        public static int SetCliffWalls(bool on)
        {
            int n = 0;
            if (chunks == null) return 0;
            foreach (var c in chunks)
                if (c.WallsCollider != null && c.WallsCollider.sharedMesh != null) { c.WallsCollider.enabled = on; n++; }
            return n;
        }

        public static bool ShowMarks
        {
            get => showMarks;
            set
            {
                if (showMarks == value) return;
                showMarks = value;
                if (chunks == null) return;
                foreach (var c in chunks)
                {
                    if (c.MarksRenderer != null) c.MarksRenderer.enabled = value;
                    if (value) BuildMarks(Grid, c);
                }
            }
        }

        private static Material marksMaterial, iceMaterial;
        private const float IceLift = 0.2f;   // over the filled ground (water level + 0.05) and every paint outline on it (up to ~0.1 m)
        /// <summary>The ice's colour (pale blue-white), shaded by the level's ground material.</summary>
        public static readonly Color IceColour = new(0.80f, 0.91f, 0.97f, 1f);

        private static Material IceMaterial()
        {
            if (iceMaterial != null) return iceMaterial;
            var src = materials?.ElementAtOrDefault((int)GroundMaterial.Sand) ?? materials?.FirstOrDefault(m => m != null);
            iceMaterial = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
            iceMaterial.name = LayoutScene.AddedPrefix + "ice";
            // The toon shader shades with the scheme's dim colours too: tint each by the ice colour, keeping the source's
            // ratio to its base colour (so the ice is shaded like the ground under the same light).
            var baseC = src != null && src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.white;
            foreach (var prop in SchemeColours)
            {
                if (!iceMaterial.HasProperty(prop) || prop == "_OutlineColor") continue;
                var c = iceMaterial.GetColor(prop);
                Color ratio = new(baseC.r > 0.01f ? c.r / baseC.r : 1f, baseC.g > 0.01f ? c.g / baseC.g : 1f, baseC.b > 0.01f ? c.b / baseC.b : 1f, 1f);
                iceMaterial.SetColor(prop, prop == "_BaseColor" ? IceColour : new Color(IceColour.r * ratio.r, IceColour.g * ratio.g, IceColour.b * ratio.b, c.a));
            }
            if (!iceMaterial.HasProperty("_BaseColor")) iceMaterial.color = IceColour;
            if (iceMaterial.HasProperty("_BaseMap")) iceMaterial.SetTexture("_BaseMap", null);
            return iceMaterial;
        }

        private static Material MarksMaterial()
        {
            if (marksMaterial != null) return marksMaterial;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            marksMaterial = new Material(shader) { name = LayoutScene.AddedPrefix + "terrain marks", color = new Color(0.95f, 0.25f, 0.2f, 0.45f), renderQueue = 3000 };
            return marksMaterial;
        }

        /// <summary>A quad a little above the ground on each blocked cell (the host's ground is found by raycast).</summary>
        private static void BuildMarks(TerrainGrid g, Chunk c)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            const float lift = 0.12f;
            for (int z = c.Z0; z < c.Z1; z++)
                for (int x = c.X0; x < c.X1; x++)
                {
                    if (!g.Blocked[g.CellIndex(x, z)]) continue;
                    int b = verts.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        float wx = g.CornerX(x + (k & 1)), wz = g.CornerZ(z + (k >> 1));
                        verts.Add(new Vector3(wx, SurfaceY(g, wx, wz) + lift, wz));
                    }
                    tris.AddRange([b, b + 2, b + 3, b, b + 3, b + 1]);
                }
            c.Marks.Clear();
            c.Marks.SetVertices(verts);
            c.Marks.SetTriangles(tris, 0);
            c.Marks.RecalculateBounds();
        }

        private static float SurfaceY(TerrainGrid g, float x, float z)
        {
            if (flat) return g.SurfaceAt(x, z);
            return Physics.Raycast(new Vector3(x, 80f, z), Vector3.down, out var hit, 200f, 1 << GroundLayerIndex, QueryTriggerInteraction.Ignore)
                ? hit.point.y : LayoutScene.Ground(new Vector3(x, 0f, z)).y;
        }

        private static bool IsBlocked(TerrainGrid g, int x, int z)
        {
            int i = g.CellIndex(x, z);
            return g.Blocked[i] || g.OpenWater(i);
        }

        private static void AddBox(List<Vector3> v, List<int> t, Vector3 min, Vector3 max)
        {
            int b = v.Count;
            for (int i = 0; i < 8; i++) v.Add(new Vector3((i & 1) == 0 ? min.x : max.x, ((i >> 1) & 1) == 0 ? min.y : max.y, ((i >> 2) & 1) == 0 ? min.z : max.z));
            int[] faces =
            [
                0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,
                0, 1, 4, 1, 5, 4,   2, 6, 3, 3, 6, 7,
                0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,
            ];
            foreach (int f in faces) t.Add(b + f);
        }

        /// <summary>Walls round the field: the king can't walk off it (the graphs end at its edge anyway).</summary>
        private static void MakeWalls(TerrainGrid g)
        {
            var go = Part("walls", ObstacleLayerIndex);
            float y0 = TerrainGrid.MinHeight - 4f, y1 = g.MaxWorld + 10f, t = 2f;
            float x0 = g.OriginX, z0 = g.OriginZ, x1 = g.OriginX + g.SizeX, z1 = g.OriginZ + g.SizeZ;
            var mesh = NewMesh(go.name);
            var v = new List<Vector3>();
            var tr = new List<int>();
            AddBox(v, tr, new Vector3(x0 - t, y0, z0 - t), new Vector3(x1 + t, y1, z0));
            AddBox(v, tr, new Vector3(x0 - t, y0, z1), new Vector3(x1 + t, y1, z1 + t));
            AddBox(v, tr, new Vector3(x0 - t, y0, z0), new Vector3(x0, y1, z1));
            AddBox(v, tr, new Vector3(x1, y0, z0), new Vector3(x1 + t, y1, z1));
            mesh.SetVertices(v);
            mesh.SetTriangles(tr, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void MakeAir()
        {
            var go = Part("flyer surface", FlyerLayerIndex);
            airMesh = NewMesh(go.name);
            go.AddComponent<MeshFilter>().sharedMesh = airMesh;
            air = go.AddComponent<MeshRenderer>();
            air.forceRenderingOff = true;
            air.sharedMaterial = materials[0];
            go.SetActive(false);   // like the game's: only on while the graphs are scanned
            GroundGraphs.Extra.Add(air);
        }

        /// <summary>The flier surface: AirHeight above the highest ground around each corner.</summary>
        private static void BuildAir()
        {
            var g = Grid;
            var v = new List<Vector3>((g.W + 1) * (g.H + 1));
            for (int z = 0; z <= g.H; z++)
                for (int x = 0; x <= g.W; x++)
                {
                    float h = float.MinValue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = Mathf.Clamp(x + dx, 0, g.W), nz = Mathf.Clamp(z + dz, 0, g.H);
                            h = Mathf.Max(h, g.Heights[g.Corner(nx, nz)]);
                        }
                    v.Add(new Vector3(g.CornerX(x), g.Y + Mathf.Max(h, g.WaterLevel) + AirHeight, g.CornerZ(z)));
                }
            var t = new List<int>(g.W * g.H * 6);
            for (int z = 0; z < g.H; z++)
                for (int x = 0; x < g.W; x++)
                {
                    int a = g.Corner(x, z), b = g.Corner(x + 1, z), c = g.Corner(x, z + 1), d = g.Corner(x + 1, z + 1);
                    t.AddRange([a, c, d, a, d, b]);
                }
            airMesh.Clear();
            airMesh.SetVertices(v);
            airMesh.SetTriangles(t, 0);
            airMesh.RecalculateBounds();
        }
    }
}
