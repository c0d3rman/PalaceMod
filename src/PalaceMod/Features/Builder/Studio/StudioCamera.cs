using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The Studio's camera: the game's CameraRig follows a free pivot (overrideCameraTarget) with an override zoom.
    WASD pans (arrows too while nothing is selected; never while Ctrl, Cmd or Alt is held), Q/E turn, the wheel zooms,
    middle-drag and right-drag grab the ground. A right press that ends within 6 px is a click (StudioShell handles it:
    cancel, else the context menu).
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioCamera
    {
        private static Transform pivot;
        private static Quaternion baseRot;
        public static float Yaw { get; private set; }
        public static float Zoom { get; private set; } = 1.6f;
        public const float MinZoom = 0.35f, MaxZoom = 3.5f;
        public const float ClickSlop = 6f;

        private static Vector3? grab;
        private static Vector2 rightDownAt;
        private static bool rightDown, rightPanning;

        public static Vector3 Pivot => pivot != null ? pivot.position : Vector3.zero;
        public static bool Ready => pivot != null;
        /// <summary>The right button is held and has moved past the click slop: a pan, not a click.</summary>
        public static bool RightPanning => rightPanning;

        public static void Setup(Transform parent, Vector3 start)
        {
            var rig = Object.FindObjectOfType<CameraRig>();
            pivot = new GameObject("PalaceBuilder camera").transform;
            pivot.SetParent(parent, false);
            pivot.position = start;
            baseRot = rig != null ? rig.transform.rotation : Quaternion.identity;
            Yaw = 0f;
            Zoom = 1.6f;
            Apply();
        }

        public static void Release()
        {
            var rig = Object.FindObjectOfType<CameraRig>();
            if (rig != null && pivot != null && rig.overrideCameraTarget == pivot) { rig.overrideCameraTarget = null; rig.overrideZoom = -1f; }
            pivot = null;
            grab = null;
            rightDown = rightPanning = false;
        }

        public static void Set(Vector3? pos, float? zoom, float? yaw)
        {
            if (pivot == null) return;
            if (pos is Vector3 p) pivot.position = p;
            if (zoom is float z) Zoom = Mathf.Clamp(z, MinZoom, MaxZoom);
            if (yaw is float y) Yaw = y;
            Apply();
        }

        private static void Apply()
        {
            if (pivot == null) return;
            pivot.rotation = Quaternion.Euler(0f, Yaw, 0f) * baseRot;
            var rig = Object.FindObjectOfType<CameraRig>();
            if (rig != null)
            {
                rig.overrideCameraTarget = pivot;
                rig.overrideZoom = Zoom;
            }
        }

        /// <summary>Frames ground points: centres on them and zooms to fit.</summary>
        public static void Frame(IReadOnlyList<Vector3> points)
        {
            if (pivot == null || points.Count == 0) return;
            var min = points[0]; var max = points[0];
            foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var center = (min + max) / 2f;
            float extent = Mathf.Max(max.x - min.x, max.z - min.z);
            // Zoom 1 shows about 40 m across; frame with some room.
            Set(center, Mathf.Clamp((extent + 16f) / 40f, 0.6f, MaxZoom), null);
        }

        /// <summary>Per frame. pointerFree: the pointer isn't over a panel (wheel and drags start only then).</summary>
        /// <returns>True on the frame a right click (press and release within the slop) happened.</returns>
        public static bool Update(bool pointerFree, bool keysFree, bool arrowsPan)
        {
            if (pivot == null) return false;
            var cam = Camera.main;
            float dt = Time.unscaledDeltaTime;
            bool mods = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand)
                || Input.GetKey(KeyCode.RightCommand) || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (keysFree && !mods && cam != null)
            {
                var move = Vector2.zero;
                if (Input.GetKey(KeyCode.W)) move.y += 1f;
                if (Input.GetKey(KeyCode.S)) move.y -= 1f;
                if (Input.GetKey(KeyCode.D)) move.x += 1f;
                if (Input.GetKey(KeyCode.A)) move.x -= 1f;
                if (arrowsPan)
                {
                    if (Input.GetKey(KeyCode.UpArrow)) move.y += 1f;
                    if (Input.GetKey(KeyCode.DownArrow)) move.y -= 1f;
                    if (Input.GetKey(KeyCode.RightArrow)) move.x += 1f;
                    if (Input.GetKey(KeyCode.LeftArrow)) move.x -= 1f;
                }
                if (StudioLabels.EdgePan && pointerFree)
                {
                    var m = StudioInput.Mouse;
                    if (m.x <= 6f) move.x -= 1f; else if (m.x >= Screen.width - 6f) move.x += 1f;
                    if (m.y <= 6f) move.y -= 1f; else if (m.y >= Screen.height - 6f) move.y += 1f;
                }
                if (move.sqrMagnitude > 0.01f)
                {
                    move = Vector2.ClampMagnitude(move, 1f);
                    Vector3 fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
                    Vector3 right = cam.transform.right; right.y = 0; right.Normalize();
                    float speed = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 2.5f : 1f;
                    pivot.position += (right * move.x + fwd * move.y) * (28f * Zoom * speed * dt);
                }
                if (StudioKeys.TurnLeft?.Held == true) Yaw += 90f * dt;
                if (StudioKeys.TurnRight?.Held == true) Yaw -= 90f * dt;
            }
            if (pointerFree && !StudioInput.Alt)
            {
                float lines = ScrollInput.Lines;
                if (Mathf.Abs(lines) > 0.01f) Zoom = Mathf.Clamp(Zoom * Mathf.Pow(0.88f, lines), MinZoom, MaxZoom);
            }

            // Middle-drag always pans; right-drag pans once it has moved past the slop.
            bool rightClick = false;
            if (StudioInput.Down(1) && pointerFree)
            {
                rightDown = true;
                rightPanning = false;
                rightDownAt = StudioInput.Mouse;
            }
            if (rightDown && StudioInput.Held(1) && !rightPanning && (StudioInput.Mouse - rightDownAt).magnitude > ClickSlop)
            {
                rightPanning = true;
                if (StudioShell.ScreenGround(rightDownAt, out var g)) grab = g;
            }
            if (rightDown && !StudioInput.Held(1))
            {
                rightClick = !rightPanning;
                rightDown = rightPanning = false;
                grab = null;
            }
            if (StudioInput.Down(2) && pointerFree && StudioShell.ScreenGround(StudioInput.Mouse, out var mg)) grab = mg;
            if (!StudioInput.Held(2) && !rightPanning) grab = null;
            if (grab is Vector3 at)
            {
                Apply();
                if (StudioShell.ScreenGround(StudioInput.Mouse, out var now))
                    pivot.position += new Vector3(at.x - now.x, 0f, at.z - now.z);
            }
            Apply();
            return rightClick;
        }

        public static Vector3 Forward
        {
            get
            {
                var cam = Camera.main;
                if (cam == null) return Vector3.forward;
                var f = cam.transform.forward; f.y = 0;
                return f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
            }
        }

        public static Vector3 Right
        {
            get
            {
                var cam = Camera.main;
                if (cam == null) return Vector3.right;
                var r = cam.transform.right; r.y = 0;
                return r.sqrMagnitude < 1e-4f ? Vector3.right : r.normalized;
            }
        }

        public static void FrameItems(IEnumerable<StudioItem> items)
        {
            var pts = items.SelectMany(i => i.Kind == StudioItemKind.Group ? i.Members.Select(StudioScene.Find).Where(m => m != null).Select(m => m.Pos) : [i.Pos]).ToList();
            Frame(pts);
        }
    }
}
