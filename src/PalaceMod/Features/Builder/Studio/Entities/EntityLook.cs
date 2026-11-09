using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic.Bodies;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;
using UnityEngine;

/*
    The Entity Studio's turntable model: the thing's look built by the runtime's own BodyVisual (what a level spawns),
    plus line gizmos for what isn't visible: the body's footprint (a circle or a box, at the floor and at its height),
    an area's shape, a pickup's reach and magnet, and a projectile's hit radius. The gizmos are LineRenderers, which
    survive EnemyStage's script strip and turn with the turntable (a box footprint turns with its entity too).
*/
namespace PalaceMod.Builder.Studio.Entities
{
    internal static class EntityLook
    {
        public static readonly Color BodyColor = new(0.36f, 0.78f, 0.66f), SolidColor = new(0.95f, 0.62f, 0.3f), AreaColor = new(0.45f, 0.7f, 0.98f),
            PickupColor = new(0.96f, 0.82f, 0.35f), HitColor = new(0.92f, 0.38f, 0.32f);

        private static Material material;
        private static Material Mat => material != null ? material : material = new Material(Shader.Find("Sprites/Default")) { name = "PalaceBuilder entity gizmo" };

        private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

        /// <summary>An object holding the thing's look and gizmos (the caller destroys it). note: why part of
        /// the look couldn't be made, or null.</summary>
        public static GameObject Build(EntityDef e, PackDraft draft, out string note)
        {
            // Active (EnemyStage.Show keeps the copy's active state), far below the map until it's copied and destroyed.
            var root = new GameObject("PalaceBuilder entity preview");
            root.transform.position = new Vector3(0f, -5000f, 0f);
            note = null;
            if (e.Visual != null)
            {
                var visual = JObject.Parse(JsonConvert.SerializeObject(e.Visual, Json));
                var pack = draft != null ? BuilderServices.Packs?.Find(draft.Pack.Id) : null;
                note = BodyVisual.Build(root, visual, pack);
            }
            Gizmos(root, e);
            return root;
        }

        private static float F(LNum n, float fallback) => n?.Value is double d ? (float)d : fallback;

        public static void Gizmos(GameObject root, EntityDef e)
        {
            var g = new GameObject("Gizmos");
            g.transform.SetParent(root.transform, false);
            var b = e.Body;
            if (b != null)
            {
                float h = Mathf.Max(0.05f, F(b.Height, 1f));
                var c = b.Solid == true ? SolidColor : BodyColor;
                if (b.Box is { Length: 2 } box)
                {
                    float x = box[0] / 2f, z = box[1] / 2f;
                    var corners = new[] { new Vector3(-x, 0, -z), new Vector3(x, 0, -z), new Vector3(x, 0, z), new Vector3(-x, 0, z) };
                    Line(g, "Footprint", c, Offset(corners, 0.03f), true);
                    Line(g, "Top", c, Offset(corners, h), true, 0.025f);
                    for (int i = 0; i < 4; i++) Line(g, "Edge", c, [corners[i] + Vector3.up * 0.03f, corners[i] + Vector3.up * h], false, 0.02f);
                }
                else
                {
                    float r = Mathf.Max(0.05f, F(b.Radius, 0.5f));
                    Line(g, "Footprint", c, Circle(r, 0.03f), true);
                    Line(g, "Top", c, Circle(r, h), true, 0.025f);
                    for (int i = 0; i < 4; i++)
                    {
                        var d = Quaternion.Euler(0, 90f * i, 0) * Vector3.forward * r;
                        Line(g, "Edge", c, [d + Vector3.up * 0.03f, d + Vector3.up * h], false, 0.02f);
                    }
                }
            }
            if (e.Area?.Shape is { } s)
            {
                var c = e.Area.Color != null && ColorUtility.TryParseHtmlString(e.Area.Color, out var ac) ? ac : AreaColor;
                if (s.Circle != null) Line(g, "Area", c, Circle(Mathf.Max(0.1f, F(s.Circle.Radius, 4f)), 0.05f), true, 0.06f);
                else if (s.Rect?.Size is { Length: 2 } size)
                {
                    float x = size[0] / 2f, z = size[1] / 2f;
                    Line(g, "Area", c, Offset([new(-x, 0, -z), new(x, 0, -z), new(x, 0, z), new(-x, 0, z)], 0.05f), true, 0.06f);
                }
                else if (s.Polygon?.Points is { Count: >= 3 } pts)
                {
                    var list = new List<Vector3>();
                    foreach (var p in pts) if (p is { Length: >= 2 }) list.Add(new Vector3(p[0], 0.05f, p[p.Length - 1]));
                    Line(g, "Area", c, list.ToArray(), true, 0.06f);
                }
            }
            if (e.Pickup != null)
            {
                Line(g, "Pickup", PickupColor, Circle(Mathf.Max(0.1f, F(e.Pickup.Radius, 1.5f)), 0.04f), true, 0.05f);
                if (F(e.Pickup.Magnet, 0f) > 0f) Line(g, "Magnet", new Color(PickupColor.r, PickupColor.g, PickupColor.b, 0.45f), Circle(F(e.Pickup.Magnet, 0f), 0.04f), true, 0.025f);
            }
            if (e.Projectile != null || ThingKinds.Of(e) == "projectile")
            {
                // The hit radius as a ring around the look's middle (the flight's hit test is a capsule along its path).
                float r = Mathf.Max(0.05f, F(e.Projectile?.HitRadius, 1f));
                var ring = new GameObject("HitRadius");
                ring.transform.SetParent(g.transform, false);
                ring.transform.localPosition = Vector3.up * 0.6f;
                Line(ring, "Hit", HitColor, Circle(r, 0f), true, 0.03f);
                Line(ring, "Hit", HitColor, Rotate(Circle(r, 0f), Quaternion.Euler(90f, 0f, 0f)), true, 0.02f);
            }
        }

        private static Vector3[] Offset(Vector3[] pts, float y)
        {
            var r = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++) r[i] = pts[i] + Vector3.up * y;
            return r;
        }

        private static Vector3[] Rotate(Vector3[] pts, Quaternion q)
        {
            for (int i = 0; i < pts.Length; i++) pts[i] = q * pts[i];
            return pts;
        }

        private static Vector3[] Circle(float r, float y, int n = 48)
        {
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                pts[i] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            }
            return pts;
        }

        public static LineRenderer Line(GameObject parent, string name, Color c, Vector3[] pts, bool loop, float width = 0.04f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = loop;
            lr.positionCount = pts.Length;
            lr.SetPositions(pts);
            lr.widthMultiplier = width;
            lr.sharedMaterial = Mat;
            lr.startColor = lr.endColor = c;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.numCornerVertices = 2;
            return lr;
        }
    }
}
