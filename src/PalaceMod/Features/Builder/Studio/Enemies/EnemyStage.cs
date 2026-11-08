using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PalaceMod.Builder.Assets;
using PalaceMod.Builder.Library;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    The Enemy Studio's stage: a stone turntable on the map with the enemy standing on it, and the camera that orbits it.

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
        public const float MinPitch = 3f, MaxPitch = 75f, MinDistance = 1.5f, MaxDistance = 60f, Fov = 30f;
        public static bool Turning = true;
        private static float spin;

        private static GameObject root, disc, holder, model;
        private const float DiscHeight = 0.12f;

        // ---- Lifecycle ----------------------------------------------------------------------------------------------

        /// <summary>Builds the stage at a ground point in the scene and takes the camera.</summary>
        public static void Enter(Scene scene, Vector3 ground)
        {
            Leave();
            Point = Snap(ground);
            root = new GameObject("PalaceBuilder Enemy stage");
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = Point;
            disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Turntable";
            Object.Destroy(disc.GetComponent<Collider>());
            disc.transform.SetParent(root.transform, false);
            var stone = SceneryLibrary.LiveMaterial("Mountain");
            if (stone != null) disc.GetComponent<MeshRenderer>().sharedMaterial = stone;
            holder = new GameObject("Holder");
            holder.SetActive(false);
            holder.transform.SetParent(root.transform, false);
            spin = 0f;
            TakeCamera();
        }

        public static void Leave()
        {
            ReleaseCamera();
            if (root != null) Object.Destroy(root);
            root = disc = holder = model = null;
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
            float radius = Mathf.Max(0.5f, b.extents.magnitude);
            Distance = Mathf.Clamp(radius / Mathf.Sin(Fov * 0.5f * Mathf.Deg2Rad) * 1.05f, MinDistance, MaxDistance);
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
            var rot = Quaternion.Euler(Pitch, Yaw, 0f);
            var camPos = Focus - rot * Vector3.forward * Distance;
            // The main camera's pose relative to the rig stays; place the rig so the camera ends up at camPos/rot.
            var local = rig.transform.worldToLocalMatrix * main.transform.localToWorldMatrix;
            var localRot = Quaternion.LookRotation(local.GetColumn(2), local.GetColumn(1));
            var rigRotation = rot * Quaternion.Inverse(localRot);
            rig.transform.rotation = rigRotation;
            rig.transform.position = camPos - rigRotation * (Vector3)local.GetColumn(3);
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
