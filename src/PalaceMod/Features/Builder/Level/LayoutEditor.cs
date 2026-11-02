using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The layout editor: an in-level mode for a pack's level (Builder stage 2).

    It runs inside a custom session on the host map (so nothing touches the player's saves), launched with a trimmed
    copy of the pack that holds just the level. The overlay isn't applied in that session; instead every build slot is
    hidden and shown as an inert copy (LayoutScene.Proxy: no game script runs for it), and the scene is a view of the
    level's data: after every change (including undo/redo) Reconcile() moves, adds and removes the copies, props and
    ground markers to match the LevelDef. All changes go through PackDraft.Edit, so undo, validation and saving are the
    editor's own. The player is frozen, the camera follows a free pivot (CameraRig.overrideCameraTarget), and nothing
    rescans pathfinding until the player asks ("Check enemy paths") or plays the level (once, at level start).
*/
namespace PalaceMod.Builder.Level
{
    internal static class LayoutEditor
    {
        public enum ItemKind { Slot, Added, Prop, Line }

        public sealed class Item
        {
            public ItemKind Kind;
            public string Key, Id, Label, Type;
            public GameObject Proxy;
            public Vector3 Pos;
            public float Yaw;
            public bool Removed, Locked, Moved;
            public NativeSlot Native;      // Slot, and the base of Added
            public NativeLine Line;
            public PropBase Prop;
            public List<int> BaseCosts = [];
        }

        public sealed class NativeSlot
        {
            public string Name, Type;
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Castle, CanBase, CanMove;
            public List<int> Costs;
            public GameObject Look;        // the hidden proxy template (blueprint look)
        }

        public sealed class NativeLine
        {
            public string Name;
            public Vector3 Center;
            public List<Vector3> Points;
            public bool Flying, Small, Big;
        }

        // ---- State ----------------------------------------------------------------------------------------------

        private enum State { Off, Waiting, Editing }

        private static State state;
        private static PackDraft draft;
        private static string levelId, packId, host;
        private static BuilderSessionInfo session;
        private static int startFrame;
        private static bool claimed;
        private static Scene scene;
        private static Transform root, looks;
        private static LayoutMarkers markers;
        private static readonly LayoutHud hud = new();
        private static readonly Dictionary<string, NativeSlot> natives = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, NativeLine> lines = new(StringComparer.Ordinal);
        private static List<PropBase> propBases = [];
        private static readonly Dictionary<string, Item> items = [];
        private static List<PathCheck.Route> routes = [];

        public static bool Active => state == State.Editing;
        public static bool Running => state != State.Off;
        public static PackDraft Draft => draft;
        public static string LevelId => levelId;
        public static LevelDef Level => draft?.Pack.FindLevel(levelId);
        public static IReadOnlyDictionary<string, Item> Items => items;
        public static IReadOnlyCollection<NativeSlot> Natives => natives.Values;
        public static IReadOnlyList<PropBase> PropBases => propBases;
        public static Scene Scene => scene;
        public static Item Selected => selectedKey != null && items.TryGetValue(selectedKey, out var i) ? i : null;
        public static bool Snap { get; set; } = true;
        public static IReadOnlyList<PathCheck.Route> Routes => routes;

        private static string selectedKey, hoverKey;

        // ---- Launch ---------------------------------------------------------------------------------------------

        /// <summary>Opens a level of the draft for layout editing: loads its map in an edit session.</summary>
        public static bool Launch(PackDraft d, string level, out string error)
        {
            error = null;
            var lv = d?.Pack.FindLevel(level);
            if (lv == null) { error = "no such level"; return false; }
            // The session validates the pack it's given; the edit session needs only the level, so problems elsewhere
            // in the pack (or the level's wave set) don't stop the layout from being edited.
            var copy = PackSerializer.Clone(d.Pack);
            var only = copy.FindLevel(level);
            only.WaveSet = null;
            copy.Units.Clear();
            copy.WaveSets.Clear();
            copy.Levels = [only];
            copy.Requires = null;
            copy.Assets?.Clear();
            Stop();
            draft = d;
            levelId = level;
            packId = d.Pack.Id;
            host = lv.Host;
            state = State.Waiting;
            claimed = false;
            if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = copy, LevelId = level }, out error))
            {
                Stop();
                return false;
            }
            session = BuilderServices.Sessions.Current;
            Log.For<BuilderManager>().Info($"editing the layout of {packId}/{level} on {host}");
            return true;
        }

        /// <summary>The overlay hook: true when this host load belongs to the edit session (then nothing is applied;
        /// the editor shows the edits itself). Restarting the map inside the session re-enters the editor.</summary>
        public static bool ClaimSession(DesignPack pack, LevelDef level)
        {
            if (state == State.Off || session == null || BuilderServices.Sessions.Current != session) return false;
            if (pack.Id != packId || level.Id != levelId) return false;
            ClearScene();
            state = State.Waiting;
            claimed = true;
            startFrame = Time.frameCount;
            return true;
        }

        /// <summary>Leaves the editor (the map stays loaded until the session ends).</summary>
        public static void Stop()
        {
            ClearScene();
            hud.Hide();
            state = State.Off;
            session = null;
            draft = null;
        }

        private static void ClearScene()
        {
            if (draft != null) draft.Changed -= OnDraftChanged;
            var rig = UnityEngine.Object.FindObjectOfType<CameraRig>();
            if (rig != null && camPivot != null && rig.overrideCameraTarget == camPivot) { rig.overrideCameraTarget = null; rig.overrideZoom = -1f; }
            markers?.Destroy();
            markers = null;
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            if (ghost != null) UnityEngine.Object.Destroy(ghost);
            root = looks = null;
            camPivot = null;
            ghost = null;
            tool = null;
            dragKey = null;
            items.Clear();
            natives.Clear();
            lines.Clear();
            routes = [];
            selectedKey = hoverKey = null;
            LayoutMenus.CloseAll();
        }

        // ---- Per frame ------------------------------------------------------------------------------------------

        public static void Update()
        {
            if (state == State.Off) return;
            var current = BuilderServices.Sessions.Current;
            if (session != null && current != session) { Stop(); return; }
            if (state == State.Waiting)
            {
                if (session == null) session = current;
                var s = LayoutScene.Host(host);
                // Wait for the map's Start (build slots deactivate themselves there) and the camera rig.
                if (!claimed || !s.IsValid() || !s.isLoaded || Time.frameCount < startFrame + 3 || UnityEngine.Object.FindObjectOfType<CameraRig>() == null) return;
                if (session != null && current == session) Enter(s);
                return;
            }
            if (!Feature<BuilderManager>.On) { BuilderServices.Sessions.End(); Stop(); return; }
            if (!scene.isLoaded) { Stop(); return; }

            bool menu = UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame != null;
            if (menu)
            {
                hud.Hide();
                if (ghost != null) ghost.SetActive(false);
                return;
            }
            LocalGamestate.Instance?.SetPlayerFreezeState(true);
            UpdateCamera();
            UpdatePointer();
            UpdateKeys();
            hud.Show(HudText());
        }

        private static void Enter(Scene s)
        {
            scene = s;
            var lv = Level;
            if (lv == null) { Notify.Show("The level was deleted", NotifyKind.Warning); Stop(); return; }
            root = new GameObject("PalaceBuilder layout editor").transform;
            SceneManager.MoveGameObjectToScene(root.gameObject, scene);
            looks = new GameObject("looks").transform;
            looks.SetParent(root, false);
            looks.gameObject.SetActive(false);
            markers = new LayoutMarkers(root);

            // Hide the map's build slots; keep a blueprint-look copy of each to show instead.
            foreach (var slot in LayoutScene.Slots(scene))
            {
                if (natives.ContainsKey(slot.name)) continue; // the overlay also only knows the first of a name
                var info = new NativeSlot
                {
                    Name = slot.name, Type = LayoutScene.TypeName(slot), Pos = slot.transform.position, Rot = slot.transform.rotation,
                    Castle = LayoutScene.IsCastle(slot), CanBase = LayoutScene.CanBeBase(slot), CanMove = LayoutScene.CanMove(slot),
                    Costs = slot.OwnUpgrades.Select(u => u.cost).ToList(),
                };
                if (slot.ActivatorUpgradesThis) info.Costs = [];
                // The castle stays as it is (it can't move, and the game's tips read it every frame).
                if (!info.Castle)
                {
                    slot.gameObject.SetActive(false);
                    info.Look = LayoutScene.Proxy(slot.gameObject, looks, "look " + slot.name);
                }
                natives[slot.name] = info;
            }
            foreach (var line in LayoutScene.Lines(scene).Where(l => l.canSpawnSmallGround || l.canSpawnBigGround || l.canSpawnFlying))
            {
                var t = line.transform;
                var pts = new List<Vector3>();
                for (int i = 0; i < t.childCount; i++) pts.Add(t.GetChild(i).position);
                if (pts.Count == 0) pts.Add(t.position);
                lines[line.name] = new NativeLine { Name = line.name, Center = LayoutScene.LineCenter(t), Points = pts, Flying = line.canSpawnFlying, Small = line.canSpawnSmallGround, Big = line.canSpawnBigGround };
            }
            propBases = LayoutScene.Props(scene);
            SetupCamera();
            draft.Changed += OnDraftChanged;
            BuilderEditor.BindDraft(draft);
            state = State.Editing;
            Reconcile();
            Log.For<BuilderManager>().Info($"layout editor on {scene.name}: {natives.Count} build slots, {lines.Count} spawn lines, {propBases.Count} prop kinds");
            LayoutMenus.FirstUseHint();
        }

        private static void OnDraftChanged(bool structural)
        {
            if (state != State.Editing) return;
            if (Level == null) { Notify.Show("The level was deleted; leaving the layout editor", NotifyKind.Warning); BuilderServices.Sessions.End(); Stop(); return; }
            Reconcile();
            LayoutMenus.Refresh();
        }

        // ---- The scene as a view of the level ---------------------------------------------------------------------

        /// <summary>Makes the copies, props and markers match the level's data.</summary>
        public static void Reconcile()
        {
            var lv = Level;
            if (lv == null || root == null) return;
            var seen = new HashSet<string>();
            var removed = new HashSet<string>(lv.BuildSlots?.Remove ?? [], StringComparer.Ordinal);

            foreach (var n in natives.Values)
            {
                var edit = lv.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == n.Name);
                var item = Get("slot:" + n.Name, ItemKind.Slot, n.Name);
                item.Native = n;
                item.Label = n.Name;
                item.Type = n.Type + (n.Castle ? " (castle)" : "");
                item.Locked = !n.CanMove;
                item.Pos = edit?.Pos != null && n.CanMove ? LayoutScene.ToVector(edit.Pos) : n.Pos;
                item.Yaw = edit?.RotY is float y && n.CanMove ? y : n.Rot.eulerAngles.y;
                item.Moved = n.CanMove && (edit?.Pos != null || edit?.RotY != null);
                item.Removed = removed.Contains(n.Name);
                item.BaseCosts = n.Costs;
                if (n.Look != null)
                {
                    EnsureProxy(item, () => UnityEngine.Object.Instantiate(n.Look, root));
                    Pose(item, n.Rot);
                }
                seen.Add(item.Key);
            }
            foreach (var add in lv.BuildSlots?.Add ?? [])
            {
                if (add?.Id == null) continue;
                var baseName = ContentRef.Parse(add.Base ?? "").Id;
                if (baseName == null || !natives.TryGetValue(baseName, out var n)) continue;
                var item = Get("add:" + add.Id, ItemKind.Added, add.Id);
                if (item.Native != n && item.Proxy != null) { UnityEngine.Object.Destroy(item.Proxy); item.Proxy = null; }
                item.Native = n;
                item.Label = "New " + n.Type;
                item.Type = n.Type + " (added)";
                item.Pos = LayoutScene.ToVector(add.Pos);
                item.Yaw = add.RotY;
                item.BaseCosts = n.Costs;
                EnsureProxy(item, () => UnityEngine.Object.Instantiate(n.Look, root));
                Pose(item, n.Rot);
                seen.Add(item.Key);
            }
            foreach (var prop in lv.Props ?? [])
            {
                if (prop?.Id == null) continue;
                var b = propBases.FirstOrDefault(p => p.Id == prop.Base);
                if (b == null) continue;
                var item = Get("prop:" + prop.Id, ItemKind.Prop, prop.Id);
                if (item.Prop != b && item.Proxy != null) { UnityEngine.Object.Destroy(item.Proxy); item.Proxy = null; }
                item.Prop = b;
                item.Label = b.Name;
                item.Type = "Prop" + (prop.Solid ? "" : " (walk-through)");
                item.Pos = LayoutScene.ToVector(prop.Pos);
                item.Yaw = prop.RotY;
                EnsureProxy(item, () => LayoutScene.MakeProp(scene, b, "edit " + prop.Id, item.Pos, item.Yaw, 1f, false));
                item.Proxy.transform.SetParent(root, true);
                item.Proxy.transform.SetPositionAndRotation(item.Pos, Quaternion.Euler(0f, item.Yaw, 0f) * b.Upright);
                item.Proxy.transform.localScale = b.Scale * Mathf.Clamp(prop.Scale, 0.1f, 10f);
                seen.Add(item.Key);
            }
            foreach (var l in lines.Values)
            {
                var edit = lv.SpawnLines?.Edit?.FirstOrDefault(e => e?.Ref == l.Name);
                var item = Get("line:" + l.Name, ItemKind.Line, l.Name);
                item.Line = l;
                item.Label = l.Name;
                item.Type = "Spawn line" + (l.Flying && !l.Small && !l.Big ? " (flying only)" : "");
                item.Pos = edit?.Pos != null ? LayoutScene.ToVector(edit.Pos) : l.Center;
                item.Yaw = edit?.RotY ?? 0f;
                item.Moved = edit != null;
                seen.Add(item.Key);
            }
            foreach (var key in items.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                if (items[key].Proxy != null) UnityEngine.Object.Destroy(items[key].Proxy);
                markers.Remove(key);
                items.Remove(key);
            }
            if (selectedKey != null && !items.ContainsKey(selectedKey)) selectedKey = null;
            RefreshMarkers();
        }

        private static Item Get(string key, ItemKind kind, string id)
        {
            if (!items.TryGetValue(key, out var item)) items[key] = item = new Item { Key = key, Kind = kind, Id = id };
            return item;
        }

        private static void EnsureProxy(Item item, Func<GameObject> make)
        {
            if (item.Proxy != null) return;
            item.Proxy = make();
            item.Proxy.name = item.Key;
            item.Proxy.SetActive(true);
        }

        private static void Pose(Item item, Quaternion baseRot)
        {
            item.Proxy.SetActive(!item.Removed);
            item.Proxy.transform.SetPositionAndRotation(item.Pos, LayoutScene.WithYaw(baseRot, item.Yaw));
        }

        /// <summary>Where a spawn line's points are with its edit (or a preview pose) applied.</summary>
        public static List<Vector3> LinePoints(NativeLine l, Vector3 center, float yaw)
        {
            var rot = Quaternion.Euler(0f, yaw, 0f);
            return l.Points.Select(p => center + rot * (p - l.Center)).ToList();
        }

        private static void RefreshMarkers()
        {
            if (markers == null) return;
            foreach (var item in items.Values) Mark(item, item.Pos, item.Yaw);
        }

        private static void Mark(Item item, Vector3 pos, float yaw)
        {
            bool selected = item.Key == selectedKey, hover = item.Key == hoverKey;
            Color color = item.Removed ? LayoutMarkers.RemovedColor
                : item.Locked && item.Kind == ItemKind.Slot ? LayoutMarkers.LockedColor
                : item.Kind switch
                {
                    ItemKind.Added => LayoutMarkers.AddedColor,
                    ItemKind.Prop => LayoutMarkers.PropColor,
                    ItemKind.Line => LayoutMarkers.LineColor,
                    _ => item.Moved ? Color.Lerp(LayoutMarkers.NativeColor, LayoutMarkers.AddedColor, 0.5f) : LayoutMarkers.NativeColor,
                };
            if (selected) color = LayoutMarkers.SelectedColor;
            else if (hover) color = Color.Lerp(color, LayoutMarkers.SelectedColor, 0.45f);
            float size = item.Kind switch { ItemKind.Line => 5f, ItemKind.Prop => 3f, _ => item.Native?.Castle == true ? 9f : 4.2f };
            if (selected) size *= 1.15f;
            List<Vector3> poly = item.Kind == ItemKind.Line ? LinePoints(item.Line, pos, yaw) : null;
            markers.Set(item.Key, pos, yaw, size, color, item.Removed, poly);
        }

        // ---- Camera ---------------------------------------------------------------------------------------------

        private static Transform camPivot;
        private static Quaternion camBase;
        private static float camYaw, camZoom = 1.4f;
        private static Vector3? panGrab;

        private static void SetupCamera()
        {
            var rig = UnityEngine.Object.FindObjectOfType<CameraRig>();
            camPivot = new GameObject("PalaceBuilder camera").transform;
            camPivot.SetParent(root, false);
            var castle = natives.Values.FirstOrDefault(n => n.Castle);
            camPivot.position = castle != null ? castle.Pos : rig.transform.position;
            camBase = rig.transform.rotation;
            camYaw = 0f;
            camZoom = 1.6f;
            camPivot.rotation = camBase;
            rig.overrideCameraTarget = camPivot;
            rig.overrideZoom = camZoom;
        }

        /// <summary>Moves the editor camera (dev command and menus).</summary>
        public static void SetCamera(Vector3? pos, float? zoom, float? yaw)
        {
            if (camPivot == null) return;
            if (pos is Vector3 p) camPivot.position = p;
            if (zoom is float z) camZoom = Mathf.Clamp(z, 0.35f, 3.5f);
            if (yaw is float y) camYaw = y;
            ApplyCamera();
        }

        private static void ApplyCamera()
        {
            var rig = UnityEngine.Object.FindObjectOfType<CameraRig>();
            camPivot.rotation = Quaternion.Euler(0f, camYaw, 0f) * camBase;
            if (rig != null)
            {
                rig.overrideCameraTarget = camPivot;
                rig.overrideZoom = camZoom;
            }
        }

        private static void UpdateCamera()
        {
            if (camPivot == null) return;
            var cam = Camera.main;
            float dt = Time.unscaledDeltaTime;
            var input = Plugin.Input;
            Vector2 move = input != null ? new Vector2(input.GetAxis("Move Horizontal"), input.GetAxis("Move Vertical")) : Vector2.zero;
            if (move.sqrMagnitude > 0.01f && cam != null)
            {
                Vector3 fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
                Vector3 right = cam.transform.right; right.y = 0; right.Normalize();
                camPivot.position += (right * move.x + fwd * move.y) * (28f * camZoom * dt);
            }
            if (Keys.TurnLeft?.Held == true) camYaw += 90f * dt;
            if (Keys.TurnRight?.Held == true) camYaw -= 90f * dt;
            float scroll = Input.mouseScrollDelta.y; // the mouse wheel is pointer input, not a key
            if (Mathf.Abs(scroll) > 0.01f) camZoom = Mathf.Clamp(camZoom * (1f - 0.12f * scroll), 0.35f, 3.5f);
            // Middle-drag grabs the ground and pans.
            if (Input.GetMouseButtonDown(2) && CursorGround(out var g)) panGrab = g;
            if (!Input.GetMouseButton(2)) panGrab = null;
            if (panGrab is Vector3 grab && CursorGround(out var now)) camPivot.position += new Vector3(grab.x - now.x, 0f, grab.z - now.z);
            ApplyCamera();
        }

        // ---- Pointer --------------------------------------------------------------------------------------------

        /// <summary>A placement tool: "slot:&lt;native name&gt;" (a copy of that slot's kind) or a prop base id.</summary>
        private static string tool;
        private static GameObject ghost;
        private static float ghostYaw;
        private static string dragKey;
        private static Vector3 dragOffset, dragPos, downMouse;
        private static bool dragging;

        /// <summary>Dev-command stand-in for the mouse: a world point on the ground.</summary>
        public static Vector3? DevCursor;

        public static bool CursorGround(out Vector3 point)
        {
            point = default;
            if (DevCursor is Vector3 dev) { point = LayoutScene.Ground(dev); return true; }
            var cam = Camera.main;
            if (cam == null) return false;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            float height = camPivot != null ? camPivot.position.y : 0f;
            for (int i = 0; i < 2; i++)
            {
                if (!new Plane(Vector3.up, new Vector3(0f, height, 0f)).Raycast(ray, out float d)) return false;
                point = LayoutScene.Ground(ray.GetPoint(d));
                height = point.y;
            }
            return true;
        }

        private static Vector3 Snapped(Vector3 p) => Snap ? new Vector3(Mathf.Round(p.x), p.y, Mathf.Round(p.z)) : p;

        private static float PickRadius => 2.2f * Mathf.Max(0.8f, camZoom);

        private static Item ItemAt(Vector3 p)
        {
            Item best = null;
            float bestD = float.MaxValue;
            foreach (var item in items.Values)
            {
                float r = item.Kind == ItemKind.Line ? PickRadius * 1.6f : item.Native?.Castle == true ? PickRadius * 2f : PickRadius;
                float d = new Vector2(item.Pos.x - p.x, item.Pos.z - p.z).magnitude;
                if (d < r && d < bestD) { best = item; bestD = d; }
            }
            return best;
        }

        /// <summary>Dev command: a left click at DevCursor on the next frame.</summary>
        public static bool DevClick;

        private static void UpdatePointer()
        {
            bool click = Input.GetMouseButtonDown(0) || DevClick;
            DevClick = false;
            bool haveGround = CursorGround(out var ground);
            string prevHover = hoverKey;
            hoverKey = tool == null && haveGround && !dragging ? ItemAt(ground)?.Key : hoverKey;
            if (tool == null && !dragging && !haveGround) hoverKey = null;

            if (tool != null)
            {
                UpdateGhost(haveGround, ground);
                if (Input.GetMouseButtonDown(1)) { CancelTool(); return; }
                if (click && haveGround) PlaceTool(Snapped(ground), ghostYaw, keep: Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
                return;
            }

            if (Input.GetMouseButtonDown(1)) { Select(null); return; }
            if (click)
            {
                var hit = haveGround ? ItemAt(ground) : null;
                Select(hit?.Key);
                if (hit != null && !hit.Removed && !(hit.Locked && hit.Kind == ItemKind.Slot))
                {
                    dragKey = hit.Key;
                    dragOffset = hit.Pos - ground;
                    downMouse = Input.mousePosition;
                    dragging = false;
                }
            }
            if (dragKey != null && items.TryGetValue(dragKey, out var drag))
            {
                if (Input.GetMouseButton(0))
                {
                    if (!dragging && (Input.mousePosition - downMouse).magnitude > 6f) dragging = true;
                    if (dragging && haveGround)
                    {
                        dragPos = Snapped(LayoutScene.Ground(ground + dragOffset));
                        Preview(drag, dragPos, drag.Yaw);
                    }
                }
                else
                {
                    if (dragging) Move(drag, dragPos, drag.Yaw);
                    dragKey = null;
                    dragging = false;
                }
            }
            if (prevHover != hoverKey) RefreshMarkers();
        }

        /// <summary>Shows an object at a pose without committing it (while dragging).</summary>
        private static void Preview(Item item, Vector3 pos, float yaw)
        {
            if (item.Proxy != null)
            {
                if (item.Kind == ItemKind.Prop) item.Proxy.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f) * item.Prop.Upright);
                else item.Proxy.transform.SetPositionAndRotation(pos, LayoutScene.WithYaw(item.Native.Rot, yaw));
            }
            Mark(item, pos, yaw);
        }

        public static void StartTool(string id)
        {
            CancelTool();
            tool = id;
            Select(null);
            if (id.StartsWith("slot:") && natives.TryGetValue(id.Substring(5), out var n))
            {
                ghost = UnityEngine.Object.Instantiate(n.Look, root);
                ghostYaw = n.Rot.eulerAngles.y;
            }
            else if (propBases.FirstOrDefault(p => p.Id == id) is PropBase b)
            {
                ghost = LayoutScene.MakeProp(scene, b, "ghost", Vector3.zero, 0f, StartScale(b), false);
                ghost.transform.SetParent(root, true);
                ghostYaw = 0f;
            }
            if (ghost != null) ghost.SetActive(false);
        }

        public static string Tool => tool;

        public static void CancelTool()
        {
            tool = null;
            if (ghost != null) UnityEngine.Object.Destroy(ghost);
            ghost = null;
        }

        private static void UpdateGhost(bool haveGround, Vector3 ground)
        {
            if (ghost == null) return;
            ghost.SetActive(haveGround);
            if (!haveGround) return;
            var p = Snapped(ground);
            if (tool.StartsWith("slot:") && natives.TryGetValue(tool.Substring(5), out var n))
                ghost.transform.SetPositionAndRotation(p, LayoutScene.WithYaw(n.Rot, ghostYaw));
            else if (propBases.FirstOrDefault(b => b.Id == tool) is PropBase b)
                ghost.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, ghostYaw, 0f) * b.Upright);
        }

        public static void Select(string key)
        {
            if (selectedKey == key) return;
            selectedKey = key;
            RefreshMarkers();
        }

        // ---- Keys -----------------------------------------------------------------------------------------------

        private static void UpdateKeys()
        {
            if (Keys.Menu?.Down == true) { CancelTool(); LayoutMenus.OpenMain(); return; }
            if (Keys.Snap?.Down == true)
            {
                Snap = !Snap;
                Notify.Show(Snap ? "Snapping to the 1 m grid" : "Free placement (no snapping)", NotifyKind.Info, 2f, "builder-layout");
            }
            float turn = Keys.RotateBack?.Down == true ? -15f : Keys.Rotate?.Down == true ? 15f : 0f;
            if (turn != 0f)
            {
                if (tool != null) ghostYaw += turn;
                else if (Selected is Item s) Rotate(s, turn);
            }
            if (Selected is Item sel)
            {
                if (Keys.Delete?.Down == true) RemoveOrRestore(sel);
                if (Keys.Properties?.Down == true) LayoutMenus.OpenSelected();
            }
        }

        // ---- Edits (all through the draft) ------------------------------------------------------------------------

        private static void EditLevel(string label, Action<LevelDef> change, string coalesce = null)
        {
            if (draft == null) return;
            string id = levelId;
            draft.Edit(label, p =>
            {
                var lv = p.FindLevel(id) ?? throw new InvalidOperationException("level missing");
                change(lv);
                Prune(lv);
            }, structural: true, coalesce: coalesce);
        }

        private static BuildSlotEdit SlotEdit(LevelDef lv, string name)
        {
            lv.BuildSlots ??= new BuildSlotOverlay();
            lv.BuildSlots.Edit ??= [];
            var e = lv.BuildSlots.Edit.FirstOrDefault(x => x?.Ref == name);
            if (e == null) lv.BuildSlots.Edit.Add(e = new BuildSlotEdit { Ref = name });
            return e;
        }

        /// <summary>Drops empty edit entries and lists, so the JSON stays as small as the change.</summary>
        private static void Prune(LevelDef lv)
        {
            if (lv.BuildSlots != null)
            {
                lv.BuildSlots.Edit?.RemoveAll(e => e == null || (e.Pos == null && e.RotY == null && (e.Upgrades == null || e.Upgrades.All(u => u?.Cost == null)) && (e.Extra?.Count ?? 0) == 0));
                foreach (var e in lv.BuildSlots.Edit ?? []) if (e.Upgrades != null && e.Upgrades.All(u => u?.Cost == null)) e.Upgrades = null;
                foreach (var a in lv.BuildSlots.Add ?? []) if (a?.Upgrades != null && a.Upgrades.All(u => u?.Cost == null)) a.Upgrades = null;
                if (lv.BuildSlots.Edit?.Count == 0) lv.BuildSlots.Edit = null;
                if (lv.BuildSlots.Add?.Count == 0) lv.BuildSlots.Add = null;
                if (lv.BuildSlots.Remove?.Count == 0) lv.BuildSlots.Remove = null;
                if (lv.BuildSlots.Edit == null && lv.BuildSlots.Add == null && lv.BuildSlots.Remove == null && (lv.BuildSlots.Extra?.Count ?? 0) == 0) lv.BuildSlots = null;
            }
            if (lv.SpawnLines != null)
            {
                if (lv.SpawnLines.Edit?.Count == 0) lv.SpawnLines.Edit = null;
                if (lv.SpawnLines.Add?.Count == 0) lv.SpawnLines.Add = null;
                if (lv.SpawnLines.Edit == null && lv.SpawnLines.Add == null && (lv.SpawnLines.Extra?.Count ?? 0) == 0) lv.SpawnLines = null;
            }
            if (lv.Props?.Count == 0) lv.Props = null;
        }

        public static void Move(Item item, Vector3 pos, float yaw, string label = null)
        {
            yaw = Mathf.Repeat(yaw, 360f);
            float[] p = LayoutScene.FromVector(pos);
            float ry = LayoutScene.Round(yaw);
            switch (item.Kind)
            {
                case ItemKind.Slot:
                    if (!item.Native.CanMove) { Notify.Show("The castle stays where the map puts it", NotifyKind.Info, 3f, "builder-layout"); return; }
                    EditLevel(label ?? $"Move {item.Type}", lv =>
                    {
                        var e = SlotEdit(lv, item.Id);
                        bool home = (pos - item.Native.Pos).sqrMagnitude < 0.0004f;
                        bool sameYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, item.Native.Rot.eulerAngles.y)) < 0.05f;
                        e.Pos = home ? null : p;
                        e.RotY = sameYaw ? null : ry;
                    }, label != null ? "rotate:" + item.Key : null);
                    break;
                case ItemKind.Added:
                    EditLevel(label ?? $"Move {item.Label.ToLowerInvariant()}", lv =>
                    {
                        var a = lv.BuildSlots?.Add?.FirstOrDefault(x => x?.Id == item.Id);
                        if (a != null) { a.Pos = p; a.RotY = ry; }
                    }, label != null ? "rotate:" + item.Key : null);
                    break;
                case ItemKind.Prop:
                    EditLevel(label ?? $"Move {item.Label.ToLowerInvariant()}", lv =>
                    {
                        var d = lv.Props?.FirstOrDefault(x => x?.Id == item.Id);
                        if (d != null) { d.Pos = p; d.RotY = ry; }
                    }, label != null ? "rotate:" + item.Key : null);
                    break;
                case ItemKind.Line:
                    EditLevel(label ?? $"Move spawn line {item.Id}", lv =>
                    {
                        lv.SpawnLines ??= new SpawnLineOverlay();
                        lv.SpawnLines.Edit ??= [];
                        var e = lv.SpawnLines.Edit.FirstOrDefault(x => x?.Ref == item.Id);
                        if (e == null) lv.SpawnLines.Edit.Add(e = new SpawnLineEdit { Ref = item.Id });
                        e.Pos = p;
                        e.RotY = Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) < 0.05f ? null : LayoutScene.Round(Mathf.DeltaAngle(0f, yaw));
                        if ((pos - item.Line.Center).sqrMagnitude < 0.0004f && e.RotY == null) lv.SpawnLines.Edit.Remove(e);
                    }, label != null ? "rotate:" + item.Key : null);
                    break;
            }
        }

        public static void Rotate(Item item, float degrees)
        {
            if (item.Removed || (item.Locked && item.Kind == ItemKind.Slot)) return;
            Move(item, item.Pos, item.Yaw + degrees, $"Rotate {item.Label}");
        }

        public static void ResetPose(Item item)
        {
            if (item.Kind == ItemKind.Slot) Move(item, item.Native.Pos, item.Native.Rot.eulerAngles.y, $"Reset {item.Label}");
            else if (item.Kind == ItemKind.Line) Move(item, item.Line.Center, 0f, $"Reset {item.Label}");
        }

        public static void RemoveOrRestore(Item item)
        {
            switch (item.Kind)
            {
                case ItemKind.Slot:
                    if (item.Native.Castle) { Notify.Show("The castle can't be removed", NotifyKind.Info, 3f, "builder-layout"); return; }
                    bool restore = item.Removed;
                    EditLevel(restore ? $"Restore {item.Label}" : $"Remove {item.Label}", lv =>
                    {
                        lv.BuildSlots ??= new BuildSlotOverlay();
                        lv.BuildSlots.Remove ??= [];
                        if (restore) lv.BuildSlots.Remove.Remove(item.Id);
                        else if (!lv.BuildSlots.Remove.Contains(item.Id)) lv.BuildSlots.Remove.Add(item.Id);
                    });
                    break;
                case ItemKind.Added:
                    EditLevel($"Delete {item.Label.ToLowerInvariant()}", lv =>
                    {
                        lv.BuildSlots?.Add?.RemoveAll(a => a?.Id == item.Id);
                        foreach (var a in lv.BuildSlots?.Add ?? []) if (a.Activator == item.Id) a.Activator = null;
                    });
                    break;
                case ItemKind.Prop:
                    EditLevel($"Delete {item.Label.ToLowerInvariant()}", lv => lv.Props?.RemoveAll(x => x?.Id == item.Id));
                    break;
                case ItemKind.Line:
                    ResetPose(item);
                    break;
            }
        }

        public static string AddSlot(string baseName, Vector3 pos, float yaw)
        {
            if (!natives.TryGetValue(baseName, out var n) || !n.CanBase) return null;
            string id = null;
            EditLevel($"Add {n.Type}", lv =>
            {
                lv.BuildSlots ??= new BuildSlotOverlay();
                lv.BuildSlots.Add ??= [];
                id = UniqueId("slot", lv);
                lv.BuildSlots.Add.Add(new BuildSlotAdd { Id = id, Base = "scene:" + baseName, Pos = LayoutScene.FromVector(pos), RotY = LayoutScene.Round(Mathf.Repeat(yaw, 360f)) });
            });
            return id == null ? null : "add:" + id;
        }

        public static string AddProp(string baseId, Vector3 pos, float yaw)
        {
            var b = propBases.FirstOrDefault(p => p.Id == baseId);
            if (b == null) return null;
            string id = null;
            EditLevel($"Add {b.Name.ToLowerInvariant()}", lv =>
            {
                lv.Props ??= [];
                id = UniqueId("prop", lv);
                lv.Props.Add(new PropDef { Id = id, Base = baseId, Pos = LayoutScene.FromVector(pos), RotY = LayoutScene.Round(Mathf.Repeat(yaw, 360f)), Scale = StartScale(b) });
            });
            return id == null ? null : "prop:" + id;
        }

        /// <summary>Some scenery is huge (cliff rocks): new props start at most about 6 m across.</summary>
        private static float StartScale(PropBase b)
        {
            var size = Vector3.Scale(b.Mesh.bounds.size, b.Scale);
            float fit = Mathf.Min(1f, 6f / Mathf.Max(0.01f, Mathf.Max(size.x, size.y, size.z)));
            return Mathf.Clamp(Mathf.Round(fit * 10f) / 10f, 0.3f, 1f);
        }

        private static string UniqueId(string prefix, LevelDef lv)
        {
            var taken = new HashSet<string>((lv.BuildSlots?.Add ?? []).Select(a => a?.Id).Concat((lv.Props ?? []).Select(p => p?.Id)).Where(x => x != null));
            for (int i = 1; ; i++) if (!taken.Contains($"{prefix}-{i}")) return $"{prefix}-{i}";
        }

        private static void PlaceTool(Vector3 pos, float yaw, bool keep)
        {
            string key = tool.StartsWith("slot:") ? AddSlot(tool.Substring(5), pos, yaw) : AddProp(tool, pos, yaw);
            if (!keep) CancelTool();
            if (key != null) Select(key);
        }

        /// <summary>Places a copy of a native slot's kind: the copy is based on the slot of that kind nearest to the
        /// spot, so it unlocks along with its neighbours.</summary>
        public static string NearestBase(string type, Vector3 near) =>
            natives.Values.Where(n => n.CanBase && n.Type == type).OrderBy(n => (n.Pos - near).sqrMagnitude).FirstOrDefault()?.Name;

        /// <summary>Upgrade costs: a level's override, or null for the base value.</summary>
        public static int? CostOverride(Item item, int level)
        {
            var lv = Level;
            var list = item.Kind == ItemKind.Slot ? lv?.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == item.Id)?.Upgrades
                : lv?.BuildSlots?.Add?.FirstOrDefault(a => a?.Id == item.Id)?.Upgrades;
            return list != null && level < list.Count ? list[level]?.Cost : null;
        }

        public static void SetCost(Item item, int level, int? cost)
        {
            string label = level == 0 ? "Set build cost" : $"Set upgrade {level} cost";
            EditLevel(label, lv =>
            {
                List<UpgradeEdit> list;
                if (item.Kind == ItemKind.Slot) list = SlotEdit(lv, item.Id).Upgrades ??= [];
                else
                {
                    var a = lv.BuildSlots?.Add?.FirstOrDefault(x => x?.Id == item.Id);
                    if (a == null) return;
                    list = a.Upgrades ??= [];
                }
                while (list.Count <= level) list.Add(null);
                if (cost != null && item.BaseCosts.Count > level && cost == item.BaseCosts[level]) cost = null;
                list[level] = cost == null ? null : new UpgradeEdit { Cost = cost };
                while (list.Count > 0 && list[list.Count - 1] == null) list.RemoveAt(list.Count - 1);
            }, coalesce: $"cost:{item.Key}:{level}");
        }

        public static PropDef PropDef(Item item) => Level?.Props?.FirstOrDefault(p => p?.Id == item.Id);

        public static void SetProp(Item item, string label, Action<PropDef> change, string coalesce = null) =>
            EditLevel(label, lv => { var d = lv.Props?.FirstOrDefault(p => p?.Id == item.Id); if (d != null) change(d); }, coalesce);

        public static void ClearAll() => EditLevel("Clear the layout", lv => { lv.BuildSlots = null; lv.SpawnLines = null; lv.Props = null; });

        // ---- Paths ----------------------------------------------------------------------------------------------

        /// <summary>Rescans with the edits in place (props as obstacles) and draws each spawn line's route.</summary>
        public static string CheckPaths()
        {
            if (!Active) return "not editing";
            // Show the scene as it will play: solid props block, removed slots are gone (slots don't block until built).
            var solid = new List<GameObject>();
            foreach (var item in items.Values.Where(i => i.Kind == ItemKind.Prop && i.Proxy != null))
                if (PropDef(item)?.Solid != false) { item.Proxy.layer = LayoutScene.ObstacleLayer; solid.Add(item.Proxy); }
            long ms = PathCheck.Rescan();
            foreach (var go in solid) go.layer = 0;
            var lineItems = items.Values.Where(i => i.Kind == ItemKind.Line).ToDictionary(i => i.Id);
            routes = PathCheck.Check(scene, l => lineItems.TryGetValue(l.name, out var it) ? it.Pos : LayoutScene.LineCenter(l.transform), withPoints: true);
            markers?.ShowRoutes(routes);
            int blocked = routes.Count(r => !r.Reachable);
            string text = routes.Count == 0 ? "No ground spawn lines to check"
                : blocked == 0 ? $"Enemies can reach the castle from all {routes.Count} ground spawn lines"
                : $"Blocked: enemies from {string.Join(", ", routes.Where(r => !r.Reachable).Select(r => r.Line))} can't reach the castle";
            Notify.Show(text, blocked == 0 ? NotifyKind.Success : NotifyKind.Warning, 6f, "builder-paths");
            Log.For<BuilderManager>().Info($"layout path check (rescan {ms} ms): {PathCheck.Describe(routes)}");
            return $"rescan {ms} ms; {PathCheck.Describe(routes)}";
        }

        // ---- Leaving --------------------------------------------------------------------------------------------

        /// <summary>Saves (when the pack can be) and plays the level with its layout.</summary>
        public static void Play()
        {
            var d = draft;
            string id = levelId;
            if (d == null) return;
            if (!d.ReadOnly && d.Dirty && d.ErrorCount == 0 && !d.Save(out string saveError)) Notify.Show(saveError, NotifyKind.Warning, key: "builder-layout");
            LayoutMenus.CloseAll();
            Stop();
            if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = d.Pack, LevelId = id }, out string error))
            {
                Notify.Show("Can't play: " + error, NotifyKind.Error, 6f, "builder-layout");
                return;
            }
            UIFrameManager.instance?.CloseAllFrames();
        }

        public static void Save()
        {
            if (draft == null) return;
            bool ok = draft.Save(out string message);
            Notify.Show(message, ok ? NotifyKind.Success : NotifyKind.Error, key: "builder-layout");
            LayoutMenus.Refresh();
        }

        /// <summary>Leaves the editor and the map (unsaved changes stay in the draft).</summary>
        public static void Leave()
        {
            LayoutMenus.CloseAll();
            Stop();
            BuilderServices.Sessions.End();
            UIFrameManager.instance?.CloseAllFrames();
        }

        // ---- HUD ------------------------------------------------------------------------------------------------

        private static string HudText()
        {
            string title = $"<font=\"{ModStyle.BoldFont?.name}\">Layout: {Esc(Level?.Name ?? levelId)}</font>";
            string dirty = draft?.Dirty == true ? $"   <color=#{ModStyle.Hex(ModStyle.Accent)}>unsaved</color>" : "";
            string line2;
            if (tool != null)
            {
                string what = tool.StartsWith("slot:") && natives.TryGetValue(tool.Substring(5), out var n) ? n.Type : propBases.FirstOrDefault(p => p.Id == tool)?.Name ?? tool;
                line2 = $"Placing: {Esc(what)}.   Click to place (Shift keeps placing)  ·  {Keys.Label(Keys.Rotate)} turn  ·  right-click cancels";
            }
            else if (Selected is Item s)
            {
                string state = s.Removed ? "removed" : s.Locked && s.Kind == ItemKind.Slot ? "can't move" : s.Moved ? "moved" : "";
                string costs = s.BaseCosts.Count > 0 && s.Kind != ItemKind.Prop ? "   costs " + string.Join("/", s.BaseCosts.Select((c, i) => CostOverride(s, i) ?? c)) : "";
                line2 = $"<color=#{ModStyle.Hex(ModStyle.Bright)}>{Esc(s.Type)}</color>  {Dim(s.Label)}{(state.Length > 0 ? "  · " + state : "")}{costs}\n" +
                    $"Drag to move  ·  {Keys.Label(Keys.Rotate)} turn  ·  {Keys.Label(Keys.Delete)} {(s.Kind == ItemKind.Slot ? (s.Removed ? "restore" : "remove") : s.Kind == ItemKind.Line ? "reset" : "delete")}  ·  {Keys.Label(Keys.Properties)} details";
            }
            else line2 = "Click a marker to select it, drag to move it.";
            string keys = $"{Dim($"{Keys.Label(Keys.Menu)} add & menu  ·  WASD or middle-drag pan  ·  {Keys.Label(Keys.TurnLeft)} {Keys.Label(Keys.TurnRight)} turn  ·  wheel zoom  ·  {Keys.Label(Keys.Snap)} snap {(Snap ? "on" : "off")}  ·  Cmd+Z undo")}";
            return $"{title}{dirty}\n{line2}\n{keys}";
        }

        private static string Dim(string s) => $"<color=#{ModStyle.Hex(Color.Lerp(ModStyle.Dim, ModStyle.Gold, 0.55f))}>{Esc(s)}</color>";
        private static string Esc(string s) => ModFields.TMPEscape(s ?? "");

        // ---- Dev ------------------------------------------------------------------------------------------------

        public static string Describe()
        {
            if (state == State.Off) return "not editing";
            if (state == State.Waiting) return $"waiting for {host}";
            int counts(ItemKind k) => items.Values.Count(i => i.Kind == k);
            return $"editing {packId}/{levelId} on {scene.name}: {counts(ItemKind.Slot)} slots ({items.Values.Count(i => i.Removed)} removed, {items.Values.Count(i => i.Moved)} moved), " +
                $"{counts(ItemKind.Added)} added, {counts(ItemKind.Prop)} props, {counts(ItemKind.Line)} lines; selected={selectedKey ?? "-"} tool={tool ?? "-"} snap={Snap} " +
                $"cam={camPivot?.position} zoom={camZoom:0.##} yaw={camYaw:0}; dirty={draft?.Dirty}";
        }
    }
}
