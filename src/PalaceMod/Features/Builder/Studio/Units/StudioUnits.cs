using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using Pathfinding;
using UnityEngine;

/*
    Unit positions in the Studio (docs/builder-ux.md, "Unit positions"): a training slot (barracks, archery range,
    hero's quarter...) shows a small disc per unit where it will stand, while it is selected or hovered; a unit on
    unwalkable ground (water, blocked or steep terrain cells, or off its pathfinding graph) is a red disc with a cross,
    shown at all times, and listed in Problems. Drag a disc (Select tool) to move that unit; the Inspector's "Move units
    onto ground" puts every stray one on the nearest free ground (UnitSpread, as play does), "Reset unit positions" goes
    back to the map's. Positions are stored per slot as buildSlots edit/add "units" (slot-relative [x, z]).
*/
namespace PalaceMod.Builder.Studio.Units
{
    internal sealed class StudioUnits : IStudioOverlay
    {
        public static readonly StudioUnits Instance = new();

        internal sealed class Shown
        {
            public StudioItem Item;
            public int Index;
            public Vector3 World;
            public bool Ok;
            public UnitSpot Spot;
        }

        private static readonly Color OkColor = new(0.42f, 0.78f, 0.69f, 0.95f);
        private static readonly Color BadColor = new(0.95f, 0.30f, 0.26f, 0.98f);
        // Disc size: grows with the zoom like the slot rings, so it reads at every zoom (2 m at the default zoom).
        private static float Size => Mathf.Max(1.5f, 0.55f * StudioScene.PickRadius);

        // ---- What a slot's units are --------------------------------------------------------------------------------

        public static bool Trains(StudioItem i) =>
            i != null && i.Kind is StudioItemKind.Slot or StudioItemKind.Added && !i.Removed && (StudioScene.NativeFor(i)?.Units.Count ?? 0) > 0;

        private static List<float[]> Custom(LevelDef lv, StudioItem i) => i.Kind == StudioItemKind.Slot
            ? lv?.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == i.Id)?.Units
            : lv?.BuildSlots?.Add?.FirstOrDefault(a => a?.Id == i.Id)?.Units;

        public static bool HasCustom(StudioItem i) => Custom(Studio.Level, i) != null;

        /// <summary>Each unit's offset from the slot: the level's own where it has one, else the map's.</summary>
        public static List<(float x, float z)> Offsets(StudioItem i)
        {
            var spots = StudioScene.NativeFor(i)?.Units ?? [];
            var custom = Custom(Studio.Level, i);
            var list = new List<(float x, float z)>();
            for (int k = 0; k < spots.Count; k++)
            {
                var o = custom != null && k < custom.Count ? custom[k] : null;
                list.Add(o != null && o.Length >= 2 ? (o[0], o[1]) : (spots[k].X, spots[k].Z));
            }
            return list;
        }

        /// <summary>The slot's pose as shown (its proxy follows drags before they're committed).</summary>
        public static (Vector3 pos, float yaw) Pose(StudioItem i) =>
            i.Proxy != null && i.Proxy.activeInHierarchy ? (i.Proxy.transform.position, i.Proxy.transform.eulerAngles.y) : (i.Pos, i.Yaw);

        public static List<Shown> Spots(StudioItem i)
        {
            var result = new List<Shown>();
            if (!Trains(i)) return result;
            var spots = StudioScene.NativeFor(i).Units;
            var offsets = Offsets(i);
            var (pos, yaw) = Pose(i);
            for (int k = 0; k < spots.Count; k++)
            {
                var (x, z) = UnitSpread.ToWorld(pos.x, pos.z, yaw, offsets[k].x, offsets[k].z);
                var w = new Vector3(x, pos.y + spots[k].Y, z);
                w.y = StudioScene.GroundY(w);
                result.Add(new Shown { Item = i, Index = k, World = w, Spot = spots[k], Ok = OnGround(w, spots[k].Mask) });
            }
            return result;
        }

        // ---- Walkable ground in the Studio ----------------------------------------------------------------------------
        // Terrain (a flat field, or painted cells on a map) is read from its grid, which is never stale; elsewhere the
        // unit's own pathfinding graph says.

        private const float Margin = 0.5f;     // a unit's radius: the cells around it must be walkable too
        private const float MaxSlope = 0.55f;  // rise per metre the ground graphs still walk (30°)

        private static TerrainGrid GridFor(float x, float z)
        {
            var g = GroundLayer.Active ? GroundLayer.Grid : null;
            return g != null && (GroundLayer.Flat || g.Contains(x, z)) ? g : null;
        }

        private static bool CellOk(TerrainGrid g, float x, float z)
        {
            if (!g.CellAt(x, z, out int cx, out int cz)) return false;
            int i = g.CellIndex(cx, cz);
            return !g.Water[i] && !g.Blocked[i];
        }

        private static bool GridOk(TerrainGrid g, float x, float z)
        {
            if (!CellOk(g, x, z) || !CellOk(g, x + Margin, z) || !CellOk(g, x - Margin, z) || !CellOk(g, x, z + Margin) || !CellOk(g, x, z - Margin)) return false;
            float sx = Math.Abs(g.HeightAt(x + Margin, z) - g.HeightAt(x - Margin, z)) / (2f * Margin);
            float sz = Math.Abs(g.HeightAt(x, z + Margin) - g.HeightAt(x, z - Margin)) / (2f * Margin);
            return sx <= MaxSlope && sz <= MaxSlope;
        }

        public static bool OnGround(Vector3 p, GraphMask mask)
        {
            var g = GridFor(p.x, p.z);
            if (g != null) return GridOk(g, p.x, p.z);
            if (UnitHomes.Nearest(p, mask) is not Vector3 n) return true;   // no pathfinding: nothing to say
            return new Vector2(n.x - p.x, n.z - p.z).magnitude <= UnitSpread.OnGround;
        }

        /// <summary>The nearest walkable point (UnitSpread.Nearest for the Studio).</summary>
        public static (float x, float z)? NearestGround(Vector3 p, GraphMask mask)
        {
            var g = GridFor(p.x, p.z);
            if (g == null) return UnitHomes.Nearest(p, mask) is Vector3 v ? (v.x, v.z) : null;
            if (GridOk(g, p.x, p.z)) return (p.x, p.z);
            g.CellAt(p.x, p.z, out int px, out int pz);
            (float x, float z)? best = null;
            float bestD = float.MaxValue;
            int maxRing = Math.Max(2, (int)(40f / g.Cell)), foundAt = -1;
            for (int r = 1; r <= maxRing && (foundAt < 0 || r <= foundAt + 1); r++)
            {
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;
                        int cx = px + dx, cz = pz + dz;
                        if (cx < 0 || cz < 0 || cx >= g.W || cz >= g.H) continue;
                        float x0 = g.CornerX(cx), z0 = g.CornerZ(cz);
                        // The point of the cell nearest p, kept a radius inside it.
                        float lo = Math.Min(Margin, g.Cell / 2f);
                        float x = Mathf.Clamp(p.x, x0 + lo, x0 + g.Cell - lo), z = Mathf.Clamp(p.z, z0 + lo, z0 + g.Cell - lo);
                        if (!GridOk(g, x, z)) { x = x0 + g.Cell / 2f; z = z0 + g.Cell / 2f; if (!GridOk(g, x, z)) continue; }
                        float d = (x - p.x) * (x - p.x) + (z - p.z) * (z - p.z);
                        if (d < bestD) { bestD = d; best = (x, z); if (foundAt < 0) foundAt = r; }
                    }
            }
            return best;
        }

        // ---- Edits ------------------------------------------------------------------------------------------------

        private static List<float[]> Stored(StudioItem i, IReadOnlyList<(float x, float z)> offsets)
        {
            var spots = StudioScene.NativeFor(i).Units;
            bool same = offsets.Count == spots.Count && offsets.Select((o, k) => Math.Abs(o.x - spots[k].X) < 0.01f && Math.Abs(o.z - spots[k].Z) < 0.01f).All(b => b);
            return same ? null : UnitSpread.Store(offsets);
        }

        private static void Write(LevelDef lv, StudioItem i, List<float[]> units)
        {
            if (i.Kind == StudioItemKind.Added)
            {
                var a = lv.BuildSlots?.Add?.FirstOrDefault(x => x?.Id == i.Id);
                if (a != null) a.Units = units;
                return;
            }
            var e = lv.BuildSlots?.Edit?.FirstOrDefault(x => x?.Ref == i.Id);
            if (e == null && units == null) return;
            if (e == null)
            {
                lv.BuildSlots ??= new BuildSlotOverlay();
                lv.BuildSlots.Edit ??= [];
                lv.BuildSlots.Edit.Add(e = new BuildSlotEdit { Ref = i.Id });
            }
            e.Units = units;
        }

        private static void Apply(List<(StudioItem item, List<float[]> units)> changes, string label)
        {
            if (changes.Count == 0) return;
            var list = changes.Select(c => (key: c.item.Key, c.units)).ToList();
            Studio.Edit(label, lv =>
            {
                foreach (var (key, units) in list)
                    if (StudioScene.Find(key) is { } item) Write(lv, item, units?.Select(u => (float[])u.Clone()).ToList());
            });
        }

        public static void SetOffset(StudioItem i, int index, Vector3 world, string label = null)
        {
            if (!Trains(i)) return;
            var offs = Offsets(i);
            if (index < 0 || index >= offs.Count) throw new ArgumentOutOfRangeException(nameof(index), $"{i.Name} has {offs.Count} units");
            var (pos, yaw) = Pose(i);
            offs[index] = UnitSpread.ToSlot(pos.x, pos.z, yaw, world.x, world.z);
            Apply([(i, Stored(i, offs))], label ?? $"Move a unit of {i.Name}");
        }

        /// <summary>Puts every unit standing off walkable ground on the nearest free ground. Returns how many moved.</summary>
        public static int MoveOntoGround(IEnumerable<StudioItem> items)
        {
            var changes = new List<(StudioItem, List<float[]>)>();
            int moved = 0;
            foreach (var i in items.Where(Trains).ToList())
            {
                var spots = Spots(i);
                if (spots.All(s => s.Ok)) continue;
                (float, float)? Near(int k, float x, float z)
                {
                    var p = new Vector3(x, spots[k].World.y, z);
                    return OnGround(p, spots[k].Spot.Mask) ? (x, z) : NearestGround(p, spots[k].Spot.Mask);
                }
                var placed = UnitSpread.Place(spots.Select(s => (s.World.x, s.World.z)).ToList(), Near, out bool[] changed);
                var (pos, yaw) = Pose(i);
                var offs = Offsets(i);
                for (int k = 0; k < spots.Count; k++)
                    if (changed[k]) { offs[k] = UnitSpread.ToSlot(pos.x, pos.z, yaw, placed[k].x, placed[k].z); moved++; }
                changes.Add((i, Stored(i, offs)));
            }
            Apply(changes, moved == 1 ? "Move a unit onto ground" : $"Move {moved} units onto ground");
            return moved;
        }

        public static void Reset(IEnumerable<StudioItem> items)
        {
            var list = items.Where(i => Trains(i) && HasCustom(i)).ToList();
            Apply(list.Select(i => (i, (List<float[]>)null)).ToList(), "Reset unit positions");
        }

        // ---- Problems ---------------------------------------------------------------------------------------------

        private static List<ValidationIssue> cached = [];
        private static float cachedAt = -10f;
        private static string cachedPrefix;

        /// <summary>One warning per slot with units off walkable ground (StudioDock.LevelIssues).</summary>
        public static List<ValidationIssue> Issues(string prefix)
        {
            if (prefix == cachedPrefix && Time.unscaledTime - cachedAt < 0.5f) return cached;
            cachedPrefix = prefix;
            cachedAt = Time.unscaledTime;
            cached = [];
            var lv = Studio.Level;
            if (lv == null || !StudioScene.Ready) return cached;
            foreach (var i in StudioScene.Items.Where(Trains))
            {
                var spots = Spots(i);
                int off = spots.Count(s => !s.Ok);
                if (off == 0) continue;
                string path = prefix;
                if (i.Kind == StudioItemKind.Added && lv.BuildSlots?.Add?.FindIndex(a => a?.Id == i.Id) is int ai && ai >= 0) path = $"{prefix}.buildSlots.add[{ai}].units";
                else if (i.Kind == StudioItemKind.Slot && lv.BuildSlots?.Edit?.FindIndex(e => e?.Ref == i.Id) is int ei && ei >= 0) path = $"{prefix}.buildSlots.edit[{ei}].units";
                cached.Add(new ValidationIssue(Severity.Warning, path,
                    $"{i.Name}: {(off == spots.Count ? (off == 1 ? "its unit stands" : $"all {off} units stand") : $"{off} of {spots.Count} units stand")} off walkable ground (play moves them to the nearest ground)"));
            }
            return cached;
        }

        public static void Invalidate() => cachedAt = -10f;

        // ---- Dragging a unit (Select tool) -------------------------------------------------------------------------

        private static StudioItem dragItem;
        private static int dragIndex;
        private static Vector3 dragAt;

        /// <summary>Called first by the Select tool: true while it owns the pointer (a press on a selected slot's unit).</summary>
        public static bool HandlePointer(StudioPointer p)
        {
            if (dragItem != null)
            {
                if (p.Ground is Vector3 g) dragAt = new Vector3(g.x, StudioScene.GroundY(g), g.z);
                if (p.LeftUp || !p.LeftHeld)
                {
                    var item = dragItem;
                    dragItem = null;
                    var was = Spots(item).ElementAtOrDefault(dragIndex);
                    if (was != null && new Vector2(dragAt.x - was.World.x, dragAt.z - was.World.z).sqrMagnitude > 0.0025f)
                        SetOffset(item, dragIndex, dragAt);
                }
                return true;
            }
            if (!p.LeftDown || p.OverUI || p.Shift || p.Ctrl || p.Ground is not Vector3 at) return false;
            float radius = 0.45f * StudioScene.PickRadius, best = radius;
            Shown hit = null;
            foreach (var item in StudioSelection.Items.Where(Trains))
                foreach (var s in Spots(item))
                {
                    float d = new Vector2(s.World.x - at.x, s.World.z - at.z).magnitude;
                    if (d < best) { best = d; hit = s; }
                }
            if (hit == null) return false;
            dragItem = hit.Item;
            dragIndex = hit.Index;
            dragAt = hit.World;
            return true;
        }

        public static bool CancelDrag()
        {
            bool was = dragItem != null;
            dragItem = null;
            return was;
        }

        public static bool Dragging => dragItem != null;

        // ---- Drawing ----------------------------------------------------------------------------------------------

        private readonly List<GameObject> pool = [];
        private Transform poolRoot;
        private Mesh quad;
        private Material okMat, badMat;
        private Texture2D dot, crossDot;

        public void Draw()
        {
            if (StudioScene.Root == null) return;
            EnsureResources();
            int used = 0;
            var selected = new HashSet<string>(StudioSelection.Keys);
            string hover = StudioSelection.Hover;
            foreach (var item in StudioScene.Items)
            {
                if (!Trains(item) || StudioScene.IsHidden(item.Key)) continue;
                bool all = selected.Contains(item.Key) || item.Key == hover || item == dragItem;
                foreach (var s in Spots(item))
                {
                    var at = s.World;
                    bool ok = s.Ok;
                    if (item == dragItem && s.Index == dragIndex) { at = dragAt; ok = OnGround(dragAt, s.Spot.Mask); }
                    if (!all && ok) continue;
                    var go = Marker(used++);
                    go.transform.position = at + Vector3.up * 0.15f;
                    go.transform.localScale = Vector3.one * (Size * (item == dragItem && s.Index == dragIndex ? 1.25f : 1f));
                    go.GetComponent<MeshRenderer>().sharedMaterial = ok ? okMat : badMat;
                    go.SetActive(true);
                }
            }
            for (int i = used; i < pool.Count; i++) if (pool[i] != null) pool[i].SetActive(false);
        }

        public void Hide()
        {
            CancelDrag();
            foreach (var go in pool) if (go != null) go.SetActive(false);
        }

        private GameObject Marker(int i)
        {
            while (pool.Count <= i)
            {
                var go = new GameObject("unit spot");
                go.transform.SetParent(poolRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                pool.Add(go);
            }
            return pool[i];
        }

        private void EnsureResources()
        {
            if (poolRoot == null)
            {
                pool.Clear();
                poolRoot = new GameObject("PalaceBuilder unit spots").transform;
                poolRoot.SetParent(StudioScene.Root, false);
            }
            if (quad != null) return;
            quad = new Mesh { name = "PalaceBuilder unit spot" };
            quad.vertices = [new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f)];
            quad.uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            quad.triangles = [0, 2, 1, 2, 3, 1];
            quad.RecalculateBounds();
            dot = Paint(false);
            crossDot = Paint(true);
            okMat = Mat(dot, OkColor);
            badMat = Mat(crossDot, BadColor);
        }

        private static Material Mat(Texture2D tex, Color color)
        {
            var shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
            var m = new Material(shader) { color = color, mainTexture = tex, renderQueue = 3150 };
            // Drawn over buildings and scenery, like the X-ray rings.
            m.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            return m;
        }

        // A disc with a dark rim (and a cross cut out for a unit off the ground).
        private static Texture2D Paint(bool cross)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size - 0.5f, v = (y + 0.5f) / size - 0.5f, d = Mathf.Sqrt(u * u + v * v) * 2f;
                    float a = d < 0.92f ? 1f : 0f;
                    float shade = d > 0.72f ? 0.35f : 1f;
                    if (cross && d < 0.62f && (Mathf.Abs(u - v) < 0.07f || Mathf.Abs(u + v) < 0.07f)) shade = 0.15f;
                    px[y * size + x] = new Color32((byte)(255 * shade), (byte)(255 * shade), (byte)(255 * shade), (byte)(255 * a));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
