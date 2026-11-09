using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PalaceMod.Builder.Assets;
using PalaceMod.Builder.Library;
using UnityEngine;
using UnityEngine.SceneManagement;
using NVec = System.Numerics.Vector3;
using Object = UnityEngine.Object;

/*
    The Enemy Studio's stage: a stone turntable with the enemy standing on it, and the camera that orbits it.

    Where: on a round stone platform of its own, well outside the level (Margin past the east edge of every pathfinding
    graph and every prop, above whatever ground lies there), so no tree, rock or hill of the level can stand between
    the camera and the model however it orbits, zooms or pans. The arena fight happens on the same spot (ArenaZone).
    As a last guard (a level whose props reach further than its box says, a huge model zoomed far out), any of the
    level's renderers that comes between the camera and the focus, or into the model's room on the turntable, is
    switched off while it does and back on afterwards and on Leave.

    The model is a copy of the unit's template (the object the game spawns) with every script removed except the
    procedural walk, so it's inert: no AI, no tags, no hit points, nothing registered with the game. Animators stay
    (the base's idle pose plays).

    The camera: the game's CameraRig is paused and the rig itself is moved so the main camera (a child of the rig,
    orthographic) looks at the stage in perspective; every camera of the rig switches to perspective with it, so stacked
    cameras stay aligned. Leave puts back the projection, the rig's transform and the rig.
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    internal static class EnemyStage
    {
        public static Vector3 Point { get; private set; }
        public static bool Ready => root != null;
        public static GameObject Model => model;
        public static Bounds ModelBounds { get; private set; }

        // Orbit.
        public static float Yaw = 200f, Pitch = 16f, Distance = 7f;
        public static Vector3 Focus;
        public const float MinPitch = 3f, MaxPitch = 75f, MinDistance = 1.5f, Fov = 30f;
        /// <summary>The zoom-out limit: 60 m, or more for a model that needs it (StageMath.MaxDistance).</summary>
        public static float MaxDistance { get; private set; } = 60f;
        public static bool Turning = true;
        private static float spin;

        private static GameObject root, floor, disc, holder, model;
        private const float DiscHeight = 0.12f;
        /// <summary>The platform's radius (room for the Entity Studio's 60 m shots and the arena).</summary>
        public const float FloorRadius = 70f;
        /// <summary>How far past the level's box the stage stands.</summary>
        public const float Margin = ArenaZone.Reach + 70f;
        /// <summary>How far above the ground under it the platform's top is (clear of sea and ground planes).</summary>
        public const float Lift = 3f;

        // ---- Lifecycle ----------------------------------------------------------------------------------------------

        /// <summary>Builds the stage outside the level of the scene (at about the height of a ground point of it)
        /// and takes the camera.</summary>
        public static void Enter(Scene scene, Vector3 ground)
        {
            Leave();
            Point = Place(Snap(ground).y);
            root = new GameObject("PalaceBuilder Enemy stage");
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = Point;
            var stone = SceneryLibrary.LiveMaterial("Mountain");
            // The platform: a stone plinth whose top is the stage's ground (Ground layer, so clicks and units find it).
            floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "Platform";
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            floor.AddComponent<BoxCollider>();
            floor.transform.SetParent(root.transform, false);
            floor.transform.localScale = new Vector3(FloorRadius * 2f, 3f, FloorRadius * 2f);
            floor.transform.localPosition = Vector3.down * 3f;
            int groundLayer = LayerMask.NameToLayer("Ground");
            if (groundLayer >= 0) floor.layer = groundLayer;
            if (stone != null) floor.GetComponent<MeshRenderer>().sharedMaterial = stone;
            disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Turntable";
            Object.Destroy(disc.GetComponent<Collider>());
            disc.transform.SetParent(root.transform, false);
            if (stone != null) disc.GetComponent<MeshRenderer>().sharedMaterial = stone;
            holder = new GameObject("Holder");
            holder.SetActive(false);
            holder.transform.SetParent(root.transform, false);
            spin = 0f;
            HideKing();
            CollectOccluders();
            TakeCamera();
        }

        /// <summary>The stage's centre for this scene: Margin east of the level's box (its graphs and its props, not
        /// the sea or ground planes laid far around it), Lift above whatever is under the platform there, or above
        /// <paramref name="height"/> when nothing is.</summary>
        private static Vector3 Place(float height)
        {
            Bounds? level = null;
            void Add(Bounds b)
            {
                if (level == null) level = b; else { var x = level.Value; x.Encapsulate(b); level = x; }
            }
            try
            {
                foreach (var g in AstarPath.active?.data.graphs ?? [])
                {
                    if (g == null || g.name == ArenaZone.GraphName) continue;
                    Bounds b;
                    try { b = g.bounds; } catch { continue; }
                    if (b.size.sqrMagnitude >= 1f) Add(b);
                }
            }
            catch (Exception e) { Log.For<BuilderManager>().Debug($"Enemy Studio: graph bounds: {e.Message}"); }
            // Props: anything half as wide again as the map's graphs is a backdrop (sea, ground plane), not a prop.
            var graphs = level;
            float limit = graphs is Bounds gb ? Mathf.Max(gb.size.x, gb.size.z) * 1.5f : 600f;
            foreach (var r in LevelRenderers())
            {
                var b = r.bounds;
                if (StageMath.IsBackdrop(N(b.size), limit)) continue;
                Add(b);
            }
            var box = level ?? new Bounds(Vector3.zero, Vector3.one * 200f);
            var p = U(StageMath.AwayPoint(N(box.min), N(box.max), Margin, height));
            // Level with the playable middle (a stray far-off prop shouldn't drag the stage along the edge).
            if (graphs is Bounds middle) p.z = middle.center.z;
            // Whatever stands under the platform out there (a ground plane, the hills of a terrain mesh): stay above it.
            float top = float.NegativeInfinity;
            for (int i = 0; i <= 8; i++)
            {
                var at = i == 0 ? p : p + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * FloorRadius;
                foreach (var hit in Physics.RaycastAll(new Vector3(at.x, box.max.y + 500f, at.z), Vector3.down, box.size.y + 1500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    if (!Ours(hit.collider.transform)) top = Mathf.Max(top, hit.point.y);
            }
            p.y = (float.IsNegativeInfinity(top) ? height : Mathf.Max(top, height - 20f)) + Lift;
            Log.For<BuilderManager>().Info($"Enemy Studio stage at {p} (level {box.min}..{box.max})");
            return p;
        }

        /// <summary>The level's visible renderers: not effects, and not the Enemy Studio's own stage, arena and fighters
        /// (a stage being replaced is still there until the end of the frame).</summary>
        private static IEnumerable<Renderer> LevelRenderers()
        {
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!Ours(r.transform)) yield return r;
            }
        }

        private static bool Ours(Transform t)
        {
            string top = t.root.name;
            return top.StartsWith("PalaceBuilder Enemy", StringComparison.Ordinal) || top.StartsWith("PalaceBuilder arena", StringComparison.Ordinal);
        }

        // ---- Keeping the view clear ---------------------------------------------------------------------------------

        // The level's renderers near enough to get in the way (none, unless the level reaches the stage), and those
        // switched off because they're in the way now.
        private static readonly List<Renderer> occluders = [];
        private static readonly HashSet<Renderer> hiddenOccluders = [];

        private static void CollectOccluders()
        {
            ShowOccluders();
            occluders.Clear();
            float reach = FloorRadius + 400f;
            foreach (var r in LevelRenderers())
            {
                if (root != null && r.transform.IsChildOf(root.transform)) continue;
                var b = r.bounds;
                if (StageMath.IsBackdrop(N(b.size), 600f)) continue;
                var c = b.ClosestPoint(Point);
                if (new Vector2(c.x - Point.x, c.z - Point.z).magnitude < reach) occluders.Add(r);
            }
            Log.For<BuilderManager>().Debug($"Enemy Studio: {occluders.Count} of the level's renderers near the stage");
        }

        /// <summary>Switches off the level's renderers between the camera and the focus or in the model's room, and back
        /// on those no longer in the way.</summary>
        private static void ClearView(Vector3 camPos)
        {
            if (occluders.Count == 0 || disc == null) return;
            var room = model != null ? ModelBounds : new Bounds(Point + Vector3.up, Vector3.one * 2f);
            room.Encapsulate(new Bounds(Point, new Vector3(disc.transform.localScale.x, 0.5f, disc.transform.localScale.z)));
            room.Expand(1f);
            foreach (var r in occluders)
            {
                if (r == null) continue;
                var b = r.bounds;
                bool inWay = StageMath.SegmentHitsBox(N(camPos), N(Focus), N(b.min), N(b.max), 0.3f) || StageMath.BoxesOverlap(N(b.min), N(b.max), N(room.min), N(room.max));
                if (inWay && r.enabled) { r.enabled = false; hiddenOccluders.Add(r); }
                else if (!inWay && hiddenOccluders.Remove(r)) r.enabled = true;
            }
        }

        private static void ShowOccluders()
        {
            foreach (var r in hiddenOccluders) if (r != null) r.enabled = true;
            hiddenOccluders.Clear();
        }

        private static NVec N(Vector3 v) => new(v.x, v.y, v.z);
        private static Vector3 U(NVec v) => new(v.X, v.Y, v.Z);

        /// <summary>How many of the level's renderers are switched off to keep the view clear.</summary>
        public static int HiddenCount => hiddenOccluders.Count;

        // The stage stands where the Level Studio's camera was, often right where the king waits: hide him (his
        // renderers only; he stays where he is) so he doesn't stand in front of the model.
        private static readonly List<Renderer> hiddenKing = [];

        private static void HideKing()
        {
            ShowKing();
            var king = TagManager.instance != null && TagManager.instance.Players.Count > 0 ? TagManager.instance.Players[0] : null;
            if (king == null) return;
            foreach (var r in king.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; hiddenKing.Add(r); }
        }

        private static void ShowKing()
        {
            foreach (var r in hiddenKing) if (r != null) r.enabled = true;
            hiddenKing.Clear();
        }

        /// <summary>Shows or hides the turntable and its model (hidden while the arena fights).</summary>
        public static void SetVisible(bool on)
        {
            if (root != null) root.SetActive(on);
        }

        public static void Leave()
        {
            ShowKing();
            ShowOccluders();
            occluders.Clear();
            ReleaseCamera();
            if (root != null) Object.Destroy(root);
            root = floor = disc = holder = model = null;
            MaxDistance = 60f;
        }

        /// <summary>A walkable point near a ground point (the navmesh's nearest node), else the point itself.</summary>
        public static Vector3 Snap(Vector3 p)
        {
            try
            {
                var astar = AstarPath.active;
                if (astar != null)
                {
                    var n = astar.GetNearest(p, Pathfinding.NNConstraint.Default);
                    if (n.node != null && (n.position - p).sqrMagnitude < 40f * 40f) return n.position;
                }
            }
            catch (Exception e) { Log.For<BuilderManager>().Debug($"Enemy Studio: navmesh snap failed: {e.Message}"); }
            return p;
        }

        // ---- The model ----------------------------------------------------------------------------------------------

        /// <summary>Shows an inert copy of a unit template (or prefab) on the turntable; null clears it.</summary>
        public static void Show(GameObject template, float previewWalk)
        {
            if (model != null) Object.Destroy(model);
            model = null;
            if (root == null || template == null) return;
            var copy = Object.Instantiate(template, holder.transform, false);
            copy.name = "Model";
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            Strip(copy);
            foreach (var motion in copy.GetComponentsInChildren<BuilderVisualMotion>(true)) motion.previewSpeed = previewWalk;
            copy.transform.SetParent(root.transform, false);
            copy.transform.localPosition = Vector3.up * DiscHeight;
            copy.transform.localRotation = Quaternion.Euler(0f, spin, 0f);
            model = copy;
            ModelBounds = BoundsOf(copy);
            float radius = Mathf.Clamp(Mathf.Max(ModelBounds.extents.x, ModelBounds.extents.z) * 1.35f + 0.4f, 0.8f, 8f);
            disc.transform.localScale = new Vector3(radius * 2f, DiscHeight * 0.5f, radius * 2f);
            disc.transform.localPosition = Vector3.up * (DiscHeight * 0.5f);
        }

        public static void SetPreviewWalk(float speed)
        {
            if (model == null) return;
            foreach (var motion in model.GetComponentsInChildren<BuilderVisualMotion>(true)) motion.previewSpeed = speed;
        }

        private static Bounds BoundsOf(GameObject go)
        {
            Bounds? b = null;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (r.bounds.size.sqrMagnitude < 1e-6f) continue;
                if (b == null) b = r.bounds; else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
            }
            return b ?? new Bounds(go.transform.position + Vector3.up, Vector3.one * 2f);
        }

        /// <summary>Removes every script (and collider, pathfinding and audio) from an inactive copy so it can be
        /// switched on without any game logic waking up. Dependents go first (RequireComponent).</summary>
        internal static void Strip(GameObject go)
        {
            var keep = new HashSet<Type> { typeof(BuilderVisualMotion) };
            for (int pass = 0; pass < 6; pass++)
            {
                var doomed = go.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && !keep.Contains(c.GetType()) && (c is MonoBehaviour || c is Collider || c is Rigidbody || c is AudioSource || c is Joint))
                    .ToList();
                if (doomed.Count == 0) return;
                var required = new HashSet<(GameObject, Type)>();
                foreach (var c in doomed)
                    foreach (RequireComponent req in c.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                        foreach (var t in new[] { req.m_Type0, req.m_Type1, req.m_Type2 })
                            if (t != null) required.Add((c.gameObject, t));
                bool any = false;
                foreach (var c in doomed)
                {
                    if (required.Any(r => r.Item1 == c.gameObject && r.Item2.IsInstanceOfType(c))) continue;
                    Object.DestroyImmediate(c);
                    any = true;
                }
                if (!any) break;
            }
            // Anything left that something still requires (shouldn't happen): at least switch it off.
            foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true)) if (b != null && !keep.Contains(b.GetType())) b.enabled = false;
        }

        // ---- Per frame ----------------------------------------------------------------------------------------------

        public static void Update(float dt)
        {
            if (root == null) return;
            if (Turning && model != null)
            {
                spin = (spin + dt * 24f) % 360f;
                model.transform.localRotation = Quaternion.Euler(0f, spin, 0f);
            }
            ApplyCamera();
        }

        /// <summary>The angle the model faces (its turntable angle).</summary>
        public static float Spin
        {
            get => spin;
            set
            {
                spin = value;
                if (model != null) model.transform.localRotation = Quaternion.Euler(0f, spin, 0f);
            }
        }

        /// <summary>Frames the model: the focus on its middle, far enough to see all of it.</summary>
        public static void FrameModel()
        {
            if (model == null) { Focus = Point + Vector3.up; Distance = 7f; return; }
            var b = BoundsOf(model);
            ModelBounds = b;
            Focus = b.center;
            float framed = StageMath.FrameDistance(Mathf.Max(0.5f, b.extents.magnitude), Fov);
            MaxDistance = StageMath.MaxDistance(framed, 60f);
            Distance = Mathf.Clamp(framed, MinDistance, MaxDistance);
        }

        public static void ResetView()
        {
            Yaw = 200f;
            Pitch = 16f;
            FrameModel();
        }

        /// <summary>Frames a set of points (the arena during a fight).</summary>
        public static void FrameArea(Vector3 center, float radius)
        {
            Focus = center + Vector3.up;
            Distance = Mathf.Clamp(radius / Mathf.Sin(Fov * 0.5f * Mathf.Deg2Rad), MinDistance, MaxDistance);
            Pitch = Mathf.Max(Pitch, 38f);
        }

        // ---- Camera -------------------------------------------------------------------------------------------------

        private static CameraRig rig;
        private static bool rigWasEnabled;
        private static Vector3 rigPos;
        private static Quaternion rigRot;
        private static readonly List<(Camera cam, bool ortho, float fov, float near, float far)> cams = [];
        private static readonly AccessTools.FieldRef<CameraRig, List<Camera>> RigCameras = AccessTools.FieldRefAccess<CameraRig, List<Camera>>("cameras");

        private static void TakeCamera()
        {
            ReleaseCamera();
            rig = Object.FindObjectOfType<CameraRig>();
            if (rig == null) return;
            rigWasEnabled = rig.enabled;
            rigPos = rig.transform.position;
            rigRot = rig.transform.rotation;
            rig.enabled = false;
            var list = new List<Camera>();
            try { list.AddRange(RigCameras(rig) ?? []); } catch (Exception e) { Log.For<BuilderManager>().Debug($"Enemy Studio: rig cameras: {e.Message}"); }
            if (Camera.main != null && !list.Contains(Camera.main)) list.Add(Camera.main);
            foreach (var c in list.Where(c => c != null))
            {
                cams.Add((c, c.orthographic, c.fieldOfView, c.nearClipPlane, c.farClipPlane));
                c.orthographic = false;
                c.fieldOfView = Fov;
                c.nearClipPlane = 0.1f;
                c.farClipPlane = Mathf.Max(c.farClipPlane, 400f);
            }
            FrameModel();
            ApplyCamera();
        }

        private static void ReleaseCamera()
        {
            foreach (var (c, ortho, fov, near, far) in cams)
            {
                if (c == null) continue;
                c.orthographic = ortho;
                c.fieldOfView = fov;
                c.nearClipPlane = near;
                c.farClipPlane = far;
            }
            cams.Clear();
            if (rig != null)
            {
                rig.transform.SetPositionAndRotation(rigPos, rigRot);
                rig.enabled = rigWasEnabled;
            }
            rig = null;
        }

        /// <summary>Moves the rig so its main camera sits on the orbit, looking at the focus.</summary>
        private static void ApplyCamera()
        {
            var main = cams.Count > 0 ? cams[0].cam : null;
            if (rig == null || main == null) return;
            Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
            Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
            // Panning stays over the platform (and up to the model's height), so the model can always be brought back.
            float top = model != null ? Mathf.Max(ModelBounds.max.y - Point.y, 2f) : 2f;
            Focus = U(StageMath.ClampFocus(N(Focus), N(Point), FloorRadius, 1f, top + 2f));
            var rot = Quaternion.Euler(Pitch, Yaw, 0f);
            var camPos = Focus - rot * Vector3.forward * Distance;
            ClearView(camPos);
            // The main camera's pose relative to the rig stays; place the rig so the camera ends up at camPos/rot.
            var local = rig.transform.worldToLocalMatrix * main.transform.localToWorldMatrix;
            var localRot = Quaternion.LookRotation(local.GetColumn(2), local.GetColumn(1));
            var rigRotation = rot * Quaternion.Inverse(localRot);
            rig.transform.rotation = rigRotation;
            rig.transform.position = camPos - rigRotation * (Vector3)local.GetColumn(3);
            // Far enough to see all of a huge model from as far as it can zoom out.
            float far = Distance + (model != null ? ModelBounds.extents.magnitude * 2f : 0f) + 100f;
            foreach (var (c, _, _, _, oldFar) in cams)
                if (c != null) c.farClipPlane = Mathf.Max(oldFar, 400f, far);
        }

        /// <summary>The ground point under a screen point (a plane at the stage's height).</summary>
        public static bool ScreenGround(Vector2 screen, out Vector3 point)
        {
            point = default;
            var cam = Camera.main;
            if (cam == null || root == null) return false;
            var ray = cam.ScreenPointToRay(screen);
            if (Physics.Raycast(ray, out var hit, 500f, GroundMask, QueryTriggerInteraction.Ignore)) { point = hit.point; return true; }
            if (!new Plane(Vector3.up, Point).Raycast(ray, out float d)) return false;
            point = ray.GetPoint(d);
            return true;
        }

        private static int GroundMask
        {
            get
            {
                int ground = LayerMask.NameToLayer("Ground");
                return ground >= 0 ? 1 << ground : Physics.DefaultRaycastLayers;
            }
        }

        // ---- Portrait -----------------------------------------------------------------------------------------------

        /// <summary>A square picture of the model (transparent background, front-left, 20° down), or null. The caller
        /// owns the texture.</summary>
        public static Texture2D Portrait(int pixels = 256)
        {
            var main = Camera.main;
            if (main == null || model == null) return null;
            GameObject camObj = null;
            RenderTexture target = null;
            var active = RenderTexture.active;
            bool fog = RenderSettings.fog;
            float oldSpin = spin;
            var oldPos = model.transform.position;
            try
            {
                // Alone, far below the map (as the library's thumbnails), so nothing else is in the picture.
                Spin = 0f;
                model.transform.position = new Vector3(0f, -5000f, 0f);
                var b = BoundsOf(model);
                camObj = new GameObject("PalaceBuilder portrait camera");
                camObj.SetActive(false);
                var cam = camObj.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                cam.orthographic = false;
                cam.fieldOfView = 24f;
                cam.aspect = 1f;
                float radius = Mathf.Max(0.3f, b.extents.magnitude);
                float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.92f;
                var dir = Quaternion.Euler(20f, 200f, 0f) * Vector3.forward;
                cam.transform.position = b.center - dir * distance;
                cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                cam.nearClipPlane = Mathf.Max(0.05f, distance - radius * 2f);
                cam.farClipPlane = distance + radius * 2f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = main.cullingMask & ~(1 << 5); // no UI
                target = RenderTexture.GetTemporary(pixels, pixels, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = target;
                RenderSettings.fog = false;
                cam.Render();
                RenderTexture.active = target;
                var tex = new Texture2D(pixels, pixels, TextureFormat.RGBA32, false) { name = "PalaceBuilder enemy portrait" };
                tex.ReadPixels(new Rect(0, 0, pixels, pixels), 0, 0);
                tex.Apply(false, false);
                return tex;
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Enemy Studio: couldn't take the portrait: {e.Message}");
                return null;
            }
            finally
            {
                if (model != null) model.transform.position = oldPos;
                RenderSettings.fog = fog;
                RenderTexture.active = active;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (camObj != null) Object.Destroy(camObj);
                Spin = oldSpin;
            }
        }
    }
}
