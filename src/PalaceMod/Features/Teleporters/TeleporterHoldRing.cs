using UnityEngine;

/*
    The ground ring shown while the teleporter key is held: a faint full circle the size of a teleporter, and a bright
    arc that sweeps clockwise around it from the top as the hold progresses (placing), or unwinds (picking up). The
    arc closes on the same frame the last coin of the payment drops in.

    A plain scene object rebuilt each frame from a small dynamic mesh; TeleporterManager owns its lifetime.
*/
namespace PalaceMod
{
    internal sealed class TeleporterHoldRing
    {
        private const int Segments = 72;
        private const float Lift = 0.09f; // just above a pad, so picking up draws over it
        private const float Outer = TeleporterPad.Radius * 1.04f, Inner = TeleporterPad.Radius * 0.8f;

        public GameObject Root { get; }

        private readonly Mesh trackMesh, arcMesh;
        private readonly Material trackMaterial, arcMaterial;
        private readonly Vector3[] vertices = new Vector3[(Segments + 1) * 2];
        private readonly int[] triangles = new int[Segments * 6];

        public TeleporterHoldRing(Vector3 at)
        {
            Root = new GameObject("PalaceMod Teleporter Hold");
            Root.transform.position = at + Vector3.up * Lift;
            var shader = Shader.Find("Sprites/Default");
            Color accent = Color.Lerp(ModStyle.Accent, Color.white, 0.55f);
            trackMaterial = new Material(shader) { color = new Color(accent.r, accent.g, accent.b, 0.18f), renderQueue = 3110 };
            arcMaterial = new Material(shader) { color = accent, renderQueue = 3111 };
            trackMesh = Part("Track", trackMaterial);
            arcMesh = Part("Arc", arcMaterial);
            Build(trackMesh, 1f);
            Build(arcMesh, 0f);
        }

        /// <param name="fraction">0..1 of the circle to draw, clockwise from the top.</param>
        public void SetArc(float fraction) => Build(arcMesh, Mathf.Clamp01(fraction));

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            Object.Destroy(trackMesh);
            Object.Destroy(arcMesh);
            Object.Destroy(trackMaterial);
            Object.Destroy(arcMaterial);
        }

        private Mesh Part(string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root.transform, false);
            var mesh = new Mesh { name = "PalaceMod teleporter hold " + name };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return mesh;
        }

        // An annulus sector from angle 0 (world +z, the top of the screen) clockwise seen from above.
        private void Build(Mesh mesh, float fraction)
        {
            int count = fraction <= 0f ? 0 : Mathf.Max(1, Mathf.CeilToInt(Segments * fraction));
            for (int i = 0; i <= count; i++)
            {
                float angle = fraction * Mathf.PI * 2f * i / count;
                var dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                vertices[i * 2] = dir * Inner;
                vertices[i * 2 + 1] = dir * Outer;
            }
            for (int i = 0; i < count; i++)
            {
                int v = i * 2, t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 2; triangles[t + 4] = v + 1; triangles[t + 5] = v + 3;
            }
            mesh.Clear();
            if (count == 0) return;
            mesh.SetVertices(vertices, 0, (count + 1) * 2);
            mesh.SetTriangles(triangles, 0, count * 6, 0);
            mesh.RecalculateBounds();
        }
    }
}
