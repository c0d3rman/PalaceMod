using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The Studio's scene as a view of the level (moved from the old layout editor). Every build slot of the host is
    hidden and shown as an inert copy (LayoutScene.Proxy: no game script runs for it); after every change, undo and redo
    Reconcile() moves, adds and removes the copies, props and ground markers to match the LevelDef. The castle stays as
    it is (it can't move, and the game's tips read it every frame).
*/
namespace PalaceMod.Builder.Studio
{
    internal sealed class NativeSlot
    {
        public string Name, Type;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool Castle, CanBase, CanMove, AddOn;
        /// <summary>Not a host slot: the game's standard building of a kind the host lacks (Name is its
        /// "building:&lt;kind&gt;" base). Only a palette base, never an object on the map.</summary>
        public bool Standard;
        public List<int> Costs;
        public GameObject Look;        // the hidden proxy template (blueprint look)
    }

    internal sealed class NativeLine
    {
        public string Name;
        public Vector3 Center;
        public List<Vector3> Points;
        public bool Flying, Small, Big;
    }

    internal static class StudioScene
    {
        public static Scene Scene { get; private set; }
        /// <summary>The host's slots as the requirements model sees them (castle, native prerequisites, levels).</summary>
        public static HostSlots HostSlots { get; private set; } = new();
        public static Transform Root { get; private set; }
        private static Transform looks;
        private static LayoutMarkers markers;
        private static readonly Dictionary<string, NativeSlot> natives = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, NativeLine> lines = new(StringComparer.Ordinal);
        private static List<PropBase> propBases = [];
        private static readonly Dictionary<string, StudioItem> items = new(StringComparer.Ordinal);
        private static readonly List<StudioItem> ordered = [];
        private static readonly Dictionary<string, NativeSlot> nativeOf = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, NativeLine> lineOf = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, PropBase> propOf = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> groupOf = new(StringComparer.Ordinal);
        private static readonly HashSet<string> hidden = new(StringComparer.Ordinal);
        private static List<PathCheck.Route> routes = [];

        public static bool Ready => Root != null;
        public static IReadOnlyCollection<NativeSlot> Natives => natives.Values;
        public static IReadOnlyCollection<NativeLine> Lines => lines.Values;
        public static IReadOnlyList<PropBase> PropBases => propBases;
        public static IReadOnlyList<StudioItem> Items => ordered;
        public static IReadOnlyList<PathCheck.Route> Routes => routes;
        public static NativeSlot NativeFor(StudioItem i) => i != null && nativeOf.TryGetValue(i.Key, out var n) ? n : null;
        public static NativeLine LineFor(StudioItem i) => i != null && lineOf.TryGetValue(i.Key, out var l) ? l : null;
        public static PropBase PropFor(StudioItem i) => i != null && propOf.TryGetValue(i.Key, out var p) ? p : null;
        public static NativeSlot Native(string name) => name != null && natives.TryGetValue(name, out var n) ? n : null;
        public static StudioItem Find(string key) => key != null && items.TryGetValue(key, out var i) ? i : null;
        /// <summary>The group an item belongs to ("group:&lt;id&gt;"), or null.</summary>
        public static string GroupOf(string key) => key != null && groupOf.TryGetValue(key, out var g) ? g : null;
        public static bool IsHidden(string key) => hidden.Contains(key);

        // ---- Enter / clear --------------------------------------------------------------------------------------

        /// <summary>How long Enter's parts took (ms), for the log.</summary>
        public static string LastTimings = "";

        public static void Enter(Scene s)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Scene = s;
            Root = new GameObject("PalaceBuilder studio").transform;
            SceneManager.MoveGameObjectToScene(Root.gameObject, s);
            // The slots' inert copies are most of the time it takes to open the editor, and test play reloads the same
            // map every time: keep them across reloads of one host (they hold only meshes, materials and transforms).
            if (lookCache == null || lookHost != s.name)
            {
                DropLookCache();
                lookCache = new GameObject("PalaceBuilder studio looks").transform;
                lookCache.gameObject.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(lookCache.gameObject);
                lookHost = s.name;
            }
            looks = lookCache;
            markers = new LayoutMarkers(Root);

            var all = LayoutScene.Slots(s);
            HostSlots = LayoutScene.HostSlots(all);
            long found = clock.ElapsedMilliseconds;
            foreach (var slot in all)
            {
                if (natives.ContainsKey(slot.name)) continue; // the overlay also only knows the first of a name
                var info = new NativeSlot
                {
                    Name = slot.name, Type = LayoutScene.TypeName(slot), Pos = slot.transform.position, Rot = slot.transform.rotation,
                    Castle = LayoutScene.IsCastle(slot), CanBase = LayoutScene.CanBeBase(slot), CanMove = LayoutScene.CanMove(slot),
                    AddOn = slot.ActivatorUpgradesThis,
                    Costs = slot.OwnUpgrades.Select(u => u.cost).ToList(),
                };
                if (slot.ActivatorUpgradesThis) info.Costs = [];
                if (!info.Castle)
                {
                    slot.gameObject.SetActive(false);
                    var cached = looks.Find("look " + slot.name);
                    info.Look = cached != null ? cached.gameObject : LayoutScene.Proxy(slot.gameObject, looks, "look " + slot.name);
                }
                natives[slot.name] = info;
            }
            // Kinds the host doesn't have: the game's standard building of that kind (BuildingPrefabs).
            var hostKinds = new HashSet<string>(natives.Values.Where(n => n.CanBase).Select(n => n.Type), StringComparer.Ordinal);
            foreach (var (id, prefab) in BuildingPrefabs.All())
            {
                string type = LayoutScene.TypeName(prefab);
                if (!hostKinds.Add(type)) continue;
                string name = BuildingRefs.Ref(id);
                var cached = looks.Find("look " + name);
                natives[name] = new NativeSlot
                {
                    Name = name, Type = type, Pos = Vector3.zero, Rot = prefab.transform.rotation, CanBase = true, CanMove = true, Standard = true,
                    Costs = prefab.OwnUpgrades.Select(u => u.cost).ToList(),
                    Look = cached != null ? cached.gameObject : LayoutScene.Proxy(prefab.gameObject, looks, "look " + name),
                };
            }
            long proxies = clock.ElapsedMilliseconds;
            foreach (var line in LayoutScene.Lines(s).Where(l => l.canSpawnSmallGround || l.canSpawnBigGround || l.canSpawnFlying))
            {
                var t = line.transform;
                var pts = new List<Vector3>();
                for (int i = 0; i < t.childCount; i++) pts.Add(t.GetChild(i).position);
                if (pts.Count == 0) pts.Add(t.position);
                lines[line.name] = new NativeLine { Name = line.name, Center = LayoutScene.LineCenter(t), Points = pts, Flying = line.canSpawnFlying, Small = line.canSpawnSmallGround, Big = line.canSpawnBigGround };
            }
            long lineMs = clock.ElapsedMilliseconds;
            propBases = LayoutScene.Props(s);
            LastTimings = $"slots {found}, proxies {proxies - found}, lines {lineMs - proxies}, scenery {clock.ElapsedMilliseconds - lineMs}";
        }

        private static Transform lookCache;
        private static string lookHost;

        /// <summary>Forgets the kept slot copies (leaving the Studio).</summary>
        public static void DropLookCache()
        {
            if (lookCache != null) UnityEngine.Object.Destroy(lookCache.gameObject);
            lookCache = null;
            lookHost = null;
        }

        public static void Clear()
        {
            markers?.Destroy();
            markers = null;
            if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
            Root = looks = null;
            items.Clear();
            ordered.Clear();
            nativeOf.Clear();
            lineOf.Clear();
            propOf.Clear();
            groupOf.Clear();
            natives.Clear();
            lines.Clear();
            hidden.Clear();
            HostSlots = new();
            propBases = [];
            routes = [];
        }

        public static NativeSlot Castle => natives.Values.FirstOrDefault(n => n.Castle);

        // ---- Reconcile ------------------------------------------------------------------------------------------

        public static void Reconcile(LevelDef lv)
        {
            if (lv == null || Root == null) return;
            StudioServices.Colors?.ApplyLevel(lv.Colors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool empty = LevelBases.IsEmpty(lv);
            ordered.Clear();

            foreach (var n in natives.Values.OrderBy(n => n.Castle ? 0 : 1).ThenBy(n => n.Name, StringComparer.Ordinal))
            {
                // An empty base removes every slot but the castle; they stay palette bases, not objects on the map.
                if ((empty && !n.Castle) || n.Standard) continue;
                var edit = lv.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == n.Name);
                var item = Get("slot:" + n.Name, StudioItemKind.Slot, n.Name);
                nativeOf[item.Key] = n;
                item.Name = n.Name;
                item.TypeName = n.Type;
                item.IsCastle = n.Castle;
                item.HostSlot = n.Name;
                item.Locked = !n.CanMove || n.AddOn;
                item.Pos = edit?.Pos != null && n.CanMove ? LayoutScene.ToVector(edit.Pos) : n.Pos;
                item.Yaw = edit?.RotY is float y && n.CanMove ? y : n.Rot.eulerAngles.y;
                item.Moved = n.CanMove && (edit?.Pos != null || edit?.RotY != null);
                item.Removed = LevelRequirements.IsRemoved(lv, HostSlots, n.Name);
                item.BaseCosts = n.Costs;
                if (n.Look != null)
                {
                    EnsureProxy(item, () => UnityEngine.Object.Instantiate(n.Look, Root));
                    Pose(item, n.Rot);
                }
                seen.Add(item.Key);
            }
            int addIndex = 0;
            foreach (var add in lv.BuildSlots?.Add ?? [])
            {
                if (add?.Id == null) continue;
                addIndex++;
                var baseName = BaseName(add.Base);
                if (baseName == null || !natives.TryGetValue(baseName, out var n) || n.Look == null) continue;
                var item = Get("add:" + add.Id, StudioItemKind.Added, add.Id);
                if (nativeOf.TryGetValue(item.Key, out var was) && was != n && item.Proxy != null) { UnityEngine.Object.Destroy(item.Proxy); item.Proxy = null; }
                nativeOf[item.Key] = n;
                item.Name = $"{n.Type} {add.Id.Replace("slot-", "+")}";
                item.TypeName = n.Type;
                item.HostSlot = n.Name;
                item.Pos = LayoutScene.ToVector(add.Pos);
                item.Yaw = add.RotY;
                item.BaseCosts = n.Costs;
                EnsureProxy(item, () => UnityEngine.Object.Instantiate(n.Look, Root));
                Pose(item, n.Rot);
                seen.Add(item.Key);
            }
            foreach (var prop in lv.Props ?? [])
            {
                if (prop?.Id == null) continue;
                var b = ResolveProp(prop.Base);
                if (b == null) continue;
                var item = Get("prop:" + prop.Id, StudioItemKind.Prop, prop.Id);
                if (propOf.TryGetValue(item.Key, out var was) && was != b && item.Proxy != null) { UnityEngine.Object.Destroy(item.Proxy); item.Proxy = null; }
                propOf[item.Key] = b;
                item.Name = b.Name;
                item.TypeName = prop.Solid ? "Scenery" : "Scenery (walk-through)";
                item.Pos = LayoutScene.ToVector(prop.Pos);
                item.Yaw = prop.RotY;
                EnsureProxy(item, () => LayoutScene.MakeProp(Scene, b, "edit " + prop.Id, item.Pos, item.Yaw, 1f, false));
                StudioServices.Colors?.Paint(item.Proxy, b, prop.Colors);
                item.Proxy.transform.SetParent(Root, true);
                item.Proxy.transform.SetPositionAndRotation(item.Pos, Quaternion.Euler(0f, item.Yaw, 0f) * b.Upright);
                item.Proxy.transform.localScale = b.Scale * Mathf.Clamp(prop.Scale, 0.1f, 10f);
                item.Proxy.SetActive(!hidden.Contains(item.Key));
                seen.Add(item.Key);
            }
            foreach (var l in lines.Values.OrderBy(l => l.Name, StringComparer.Ordinal))
            {
                var edit = lv.SpawnLines?.Edit?.FirstOrDefault(e => e?.Ref == l.Name);
                var item = Get("line:" + l.Name, StudioItemKind.Line, l.Name);
                lineOf[item.Key] = l;
                item.Name = l.Name;
                item.TypeName = "Spawn line" + (l.Flying && !l.Small && !l.Big ? " (flying)" : "");
                item.Pos = edit?.Pos != null ? LayoutScene.ToVector(edit.Pos) : l.Center;
                item.Yaw = edit?.RotY ?? 0f;
                item.Removed = lv.SpawnLines?.Remove?.Contains(l.Name) == true;
                item.Moved = edit != null && !item.Removed;
                seen.Add(item.Key);
            }
            groupOf.Clear();
            foreach (var g in lv.Groups ?? [])
            {
                if (g?.Id == null) continue;
                var members = (g.Members ?? []).Where(m => m != null && items.ContainsKey(m) && seen.Contains(m)).Distinct().ToList();
                var item = Get("group:" + g.Id, StudioItemKind.Group, g.Id);
                item.Name = string.IsNullOrWhiteSpace(g.Name) ? "Group " + g.Id : g.Name;
                item.TypeName = "Group";
                item.Members = members;
                item.Pos = members.Count > 0 ? members.Select(m => items[m].Pos).Aggregate(Vector3.zero, (a, b) => a + b) / members.Count : Vector3.zero;
                foreach (var m in members) groupOf[m] = item.Key;
                seen.Add(item.Key);
            }
            foreach (var key in items.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                if (items[key].Proxy != null) UnityEngine.Object.Destroy(items[key].Proxy);
                markers?.Remove(key);
                items.Remove(key);
                nativeOf.Remove(key);
                lineOf.Remove(key);
                propOf.Remove(key);
                hidden.Remove(key);
            }
            ordered.AddRange(items.Values.OrderBy(i => (int)i.Kind).ThenBy(i => i.IsCastle ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
            RefreshMarkers();
        }

        /// <summary>A prop base for a PropDef base string: the library's resolver first (cross-map scenery), then this
        /// host's scenery.</summary>
        public static PropBase ResolveProp(string baseRef)
        {
            if (baseRef == null) return null;
            PropBase b = null;
            try { b = StudioServices.PropResolver?.Invoke(Scene, baseRef); }
            catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio prop resolver failed on {baseRef}", e); }
            return b ?? propBases.FirstOrDefault(p => p.Id == baseRef);
        }

        /// <summary>The native an added slot's base names: a host slot name for "scene:X", the whole ref for a
        /// standard building ("building:mill").</summary>
        public static string BaseName(string baseRef) =>
            BuildingRefs.IsBuilding(baseRef) ? baseRef : ContentRef.Parse(baseRef ?? "").Id;

        /// <summary>The level's base string for a native's name (inverse of BaseName).</summary>
        public static string BaseRef(string nativeName) =>
            BuildingRefs.IsBuilding(nativeName) ? nativeName : "scene:" + nativeName;

        private static StudioItem Get(string key, StudioItemKind kind, string id)
        {
            if (!items.TryGetValue(key, out var item)) items[key] = item = new StudioItem { Key = key, Kind = kind, Id = id };
            return item;
        }

        private static void EnsureProxy(StudioItem item, Func<GameObject> make)
        {
            if (item.Proxy != null) return;
            item.Proxy = make();
            item.Proxy.name = item.Key;
            item.Proxy.SetActive(true);
        }

        private static void Pose(StudioItem item, Quaternion baseRot)
        {
            item.Proxy.SetActive(!item.Removed && !hidden.Contains(item.Key));
            item.Proxy.transform.SetPositionAndRotation(item.Pos, LayoutScene.WithYaw(baseRot, item.Yaw));
        }

        /// <summary>Where a spawn line's points are with its edit (or a preview pose) applied.</summary>
        public static List<Vector3> LinePoints(NativeLine l, Vector3 center, float yaw)
        {
            var rot = Quaternion.Euler(0f, yaw, 0f);
            return l.Points.Select(p => center + rot * (p - l.Center)).ToList();
        }

        // ---- Hide (editor view only) ----------------------------------------------------------------------------

        public static void Hide(IEnumerable<string> keys)
        {
            foreach (var k in keys) if (items.TryGetValue(k, out var i) && i.Kind != StudioItemKind.Group) hidden.Add(k);
            ApplyHidden();
        }

        public static void ShowAll()
        {
            hidden.Clear();
            ApplyHidden();
        }

        private static void ApplyHidden()
        {
            foreach (var i in items.Values)
            {
                if (i.Proxy != null) i.Proxy.SetActive(!i.Removed && !hidden.Contains(i.Key));
            }
            RefreshMarkers();
        }

        // ---- Markers --------------------------------------------------------------------------------------------

        public static void RefreshMarkers()
        {
            if (markers == null) return;
            foreach (var item in items.Values)
            {
                if (item.Kind == StudioItemKind.Group) continue;
                Mark(item, item.Pos, item.Yaw);
            }
        }

        /// <summary>The colour by kind when no label provider gives one.</summary>
        public static Color KindColor(StudioItem item)
        {
            if (item.Kind == StudioItemKind.Prop) return LayoutMarkers.PropColor;
            if (item.Kind == StudioItemKind.Line) return LayoutMarkers.LineColor;
            if (item.IsCastle) return LayoutMarkers.CastleColor;
            return LayoutMarkers.KindColor(item.TypeName);
        }

        public static void Mark(StudioItem item, Vector3 pos, float yaw)
        {
            if (markers == null) return;
            if (hidden.Contains(item.Key)) { markers.Remove(item.Key); return; }
            bool selected = StudioSelection.Contains(item.Key), hover = StudioSelection.Hover == item.Key;
            Color color = item.Removed ? LayoutMarkers.RemovedColor
                : Studio.LabelFor(item.Key).Color ?? KindColor(item);
            if (item.Locked && item.Kind == StudioItemKind.Slot && !item.IsCastle) color = Color.Lerp(color, LayoutMarkers.LockedColor, 0.5f);
            if (selected) color = LayoutMarkers.SelectedColor;
            else if (hover) color = Color.Lerp(color, LayoutMarkers.SelectedColor, 0.5f);
            float size = item.Kind switch { StudioItemKind.Line => 5f, StudioItemKind.Prop => 3f, _ => item.IsCastle ? 9f : 4.2f };
            if (selected) size *= 1.15f;
            List<Vector3> poly = item.Kind == StudioItemKind.Line ? LinePoints(lineOf[item.Key], pos, yaw) : null;
            markers.Set(item.Key, pos, yaw, size, color, item.Removed, poly, selected, StudioViewOptions.XRay);
        }

        /// <summary>Shows an object at a pose without committing it (while dragging).</summary>
        public static void Preview(StudioItem item, Vector3 pos, float yaw)
        {
            if (item.Proxy != null)
            {
                if (item.Kind == StudioItemKind.Prop && propOf.TryGetValue(item.Key, out var b)) item.Proxy.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f) * b.Upright);
                else if (nativeOf.TryGetValue(item.Key, out var n)) item.Proxy.transform.SetPositionAndRotation(pos, LayoutScene.WithYaw(n.Rot, yaw));
            }
            Mark(item, pos, yaw);
        }

        public static void ShowRoutes(List<PathCheck.Route> list)
        {
            routes = list ?? [];
            markers?.ShowRoutes(routes);
        }

        public static void SetMarkersVisible(bool visible)
        {
            if (Root != null) markers?.SetVisible(visible);
        }

        // ---- Picking --------------------------------------------------------------------------------------------

        public static float PickRadius => 2.2f * Mathf.Max(0.8f, StudioCamera.Zoom);

        public static float RadiusOf(StudioItem item) =>
            item.Kind == StudioItemKind.Line ? PickRadius * 1.6f : item.IsCastle ? PickRadius * 2f : item.Kind == StudioItemKind.Prop ? PickRadius * 0.85f : PickRadius;

        /// <summary>The item at a ground point: the smallest pick radius that contains it first, then the nearest.</summary>
        public static StudioItem ItemAt(Vector3 p)
        {
            StudioItem best = null;
            float bestR = float.MaxValue, bestD = float.MaxValue;
            foreach (var item in ordered)
            {
                if (item.Kind == StudioItemKind.Group || hidden.Contains(item.Key)) continue;
                float r = RadiusOf(item);
                float d = new Vector2(item.Pos.x - p.x, item.Pos.z - p.z).magnitude;
                if (d >= r) continue;
                if (r < bestR - 0.01f || (Mathf.Abs(r - bestR) <= 0.01f && d < bestD)) { best = item; bestR = r; bestD = d; }
            }
            return best;
        }

        /// <summary>Items that can be picked (not groups, not hidden).</summary>
        public static IEnumerable<StudioItem> Pickable => ordered.Where(i => i.Kind != StudioItemKind.Group && !hidden.Contains(i.Key));

        /// <summary>The ground height at a point: terrain while sculpting, else the host's navmesh.</summary>
        public static float GroundY(Vector3 p)
        {
            try
            {
                if (StudioServices.Terrain?.Height(p) is float h) return h;
            }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Studio terrain height failed", e); }
            return LayoutScene.Ground(p).y;
        }

        public static Vector3 Ground(Vector3 p) => new(p.x, GroundY(p), p.z);
    }
}
