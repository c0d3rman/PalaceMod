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
      MeshCollider (the king walks on it, coins and lobbed shots hit it), drawn with the level's own ground materials
      (ColorAndLightManager recolours them, so it follows the scheme and daylight); faces too steep to walk are rock,
      cells beside water are sand;
    - blocked and water cells: invisible readable columns on the Obstacle layer that reach above the graphs' bounds
      (so they cut the navmesh and leave no walkable top), with a MeshCollider that stops the king; fliers ignore them;
    - water: a plane at the water level with the river material.
    A flat base also hides the host's ground, cliffs, rivers, roads, invisible walls and flier surface, raises the
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
        private const float SteepSlope = 32f;  // the graphs' maxSlope is 30: steeper reads as a cliff
        private const int GroundLayerIndex = 6, ObstacleLayerIndex = 7, FlyerLayerIndex = 9, WaterLayerIndex = 4;

        private sealed class Chunk
        {
            public int X0, Z0, X1, Z1;   // cells, exclusive end
            public Mesh Ground, Blocks, Water;
            public MeshFilter GroundFilter, BlocksFilter, WaterFilter;
            public MeshCollider GroundCollider, BlocksCollider;
        }

        private static GameObject root;
        private static Chunk[,] chunks;
        private static Mesh airMesh;
        private static MeshRenderer air;
        private static float blockBottom, blockTop;
        private static Material[] materials;
        private static Material water;
        private static readonly List<(Behaviour b, Renderer r, Collider c, GameObject go)> hidden = [];

        /// <summary>The terrain being shown, or null.</summary>
        public static TerrainGrid Grid { get; private set; }
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
            if (Flat) SnapToGround(scene);
            Log.For<BuilderManager>().Info($"terrain on {scene.name}: {grid.W}x{grid.H} cells of {grid.Cell} m, {(Flat ? "flat base" : "host ground")}, {grid.Blocked.Count(b => b)} blocked, {grid.Water.Count(b => b)} water");
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
            var gb = GraphBounds();
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
            GroundGraphs.Extra.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            Grid = null;
            chunks = null;
            air = null;
            flat = false;
        }

        /// <summary>The terrain's surface height under a point, when it has a ground of its own there.</summary>
        public static bool TryHeight(Vector3 p, out float y)
        {
            y = p.y;
            if (!Flat || !Grid.Contains(p.x, p.z)) return false;
            y = Grid.HeightAt(p.x, p.z);
            return true;
        }

        // ---- Flat base: the host's ground goes --------------------------------------------------------------------

        private static void HideHost(Scene scene)
        {
            var ground = new HashSet<Material>(GroundMaterials.All().Where(kv => kv.Key != "oceanMaterial" && kv.Value != null).Select(kv => kv.Value));
            // The dark shapes drawn along rivers and roads go with them.
            if (GroundMaterials.Get("shadowShapeMaterial") is Material shadow) ground.Add(shadow);
            var keepLayers = new HashSet<int> { 0, GroundLayerIndex, ObstacleLayerIndex, WaterLayerIndex, 21 };
            foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || r.gameObject.scene != scene || !keepLayers.Contains(r.gameObject.layer)) continue;
                if (r.sharedMaterials.Length == 0 || !r.sharedMaterials.All(m => m != null && ground.Contains(m))) continue;
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

        private static void Hide(Collider c)
        {
            if (c == null || !c.enabled) return;
            c.enabled = false;
            hidden.Add((null, null, c, null));
        }

        /// <summary>Whether something belongs to a slot, unit, spawner or the mod, not to the map's ground.</summary>
        private static bool Owned(Transform t) =>
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
            float lo = TerrainGrid.MinHeight - 2f, hi = TerrainGrid.MaxHeight + AirHeight + 3f;
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
            // Indexed by GroundMaterial.
            materials = [M("groundMaterial"), M("groundHigh"), M("groundLow"), M("earthMaterial"), M("sandMaterial"), M("roadMaterial"), M("rockMaterial")];
            water = M("waterMaterial");
        }

        private static Chunk MakeChunk(int x0, int z0, int x1, int z1)
        {
            var c = new Chunk { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1 };
            if (flat)
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

                var w = Part($"water {x0 / ChunkCells},{z0 / ChunkCells}", WaterLayerIndex);
                c.Water = NewMesh(w.name);
                c.WaterFilter = w.AddComponent<MeshFilter>();
                c.WaterFilter.sharedMesh = c.Water;
                var wr = w.AddComponent<MeshRenderer>();
                wr.sharedMaterial = water;
                wr.shadowCastingMode = ShadowCastingMode.Off;
            }
            // Named like the layout props' blockers, so the path check sees a spawn walled in by them.
            var b = Part("blocker", ObstacleLayerIndex);
            c.Blocks = NewMesh(b.name);
            c.BlocksFilter = b.AddComponent<MeshFilter>();
            c.BlocksFilter.sharedMesh = c.Blocks;
            b.AddComponent<MeshRenderer>().forceRenderingOff = true;   // enabled so the scanner takes it, never drawn
            c.BlocksCollider = b.AddComponent<MeshCollider>();
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
            if (c.Ground != null) BuildGround(g, c);
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
            float wy = g.Y + g.WaterLevel;
            float steep = Mathf.Tan(SteepSlope * Mathf.Deg2Rad) * g.Cell;
            for (int z = c.Z0; z < c.Z1; z++)
                for (int x = c.X0; x < c.X1; x++)
                {
                    float h00 = g.Heights[g.Corner(x, z)], h10 = g.Heights[g.Corner(x + 1, z)];
                    float h01 = g.Heights[g.Corner(x, z + 1)], h11 = g.Heights[g.Corner(x + 1, z + 1)];
                    int b = verts.Count;
                    AddCorner(g, x, z, h00, verts, normals, uvs);
                    AddCorner(g, x + 1, z, h10, verts, normals, uvs);
                    AddCorner(g, x, z + 1, h01, verts, normals, uvs);
                    AddCorner(g, x + 1, z + 1, h11, verts, normals, uvs);
                    float rise = Mathf.Max(Mathf.Abs(h00 - h10), Mathf.Abs(h01 - h11), Mathf.Abs(h00 - h01), Mathf.Abs(h10 - h11));
                    int ci = g.CellIndex(x, z);
                    var m = (GroundMaterial)g.Materials[ci];
                    if (rise > steep) m = GroundMaterial.Rock;
                    else if (m == GroundMaterial.Ground && !g.Water[ci] && NextToWater(g, x, z)) m = GroundMaterial.Sand;
                    var t = tris[(int)m];
                    // Split along the diagonal that keeps the surface convex-looking.
                    if (Mathf.Abs(h00 - h11) <= Mathf.Abs(h10 - h01)) t.AddRange([b, b + 2, b + 3, b, b + 3, b + 1]);
                    else t.AddRange([b, b + 2, b + 1, b + 1, b + 2, b + 3]);
                    if (g.Water[ci])
                    {
                        int wb = wverts.Count;
                        float x0 = g.CornerX(x), z0 = g.CornerZ(z), x1 = x0 + g.Cell, z1 = z0 + g.Cell;
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
            c.GroundCollider.sharedMesh = mesh;

            c.Water.Clear();
            c.Water.SetVertices(wverts);
            c.Water.SetUVs(0, wuvs);
            c.Water.SetTriangles(wtris, 0);
            c.Water.RecalculateNormals();
            c.Water.RecalculateBounds();
        }

        private static void AddCorner(TerrainGrid g, int x, int z, float h, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs)
        {
            float wx = g.CornerX(x), wz = g.CornerZ(z);
            verts.Add(new Vector3(wx, g.Y + h, wz));
            float l = g.Heights[g.Corner(Math.Max(0, x - 1), z)], r = g.Heights[g.Corner(Math.Min(g.W, x + 1), z)];
            float d = g.Heights[g.Corner(x, Math.Max(0, z - 1))], u = g.Heights[g.Corner(x, Math.Min(g.H, z + 1))];
            normals.Add(new Vector3(l - r, 2f * g.Cell, d - u).normalized);
            uvs.Add(new Vector2(wx, wz) * 0.05f);
        }

        private static bool NextToWater(TerrainGrid g, int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx >= 0 && nz >= 0 && nx < g.W && nz < g.H && g.Water[g.CellIndex(nx, nz)]) return true;
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
        }

        private static bool IsBlocked(TerrainGrid g, int x, int z)
        {
            int i = g.CellIndex(x, z);
            return g.Blocked[i] || (flat && g.Water[i]);
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
            float y0 = TerrainGrid.MinHeight - 4f, y1 = TerrainGrid.MaxHeight + 10f, t = 2f;
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
