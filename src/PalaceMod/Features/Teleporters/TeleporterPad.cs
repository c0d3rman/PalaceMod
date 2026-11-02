using UnityEngine;

/*
    One placed teleporter: a flat rune circle on the ground in the mod's signature colour, a slowly turning outer ring,
    a fill that grows while the king channels, a soft light for the night, and the game's own "units placed" sparkle
    (CommandUnits.dropWaypointFx) cloned as an idle shimmer and a burst on each jump.

    The pad is a plain scene object, so it goes away with the level. TeleporterManager owns the logic.
*/
namespace PalaceMod
{
    internal sealed class TeleporterPad
    {
        public const float Radius = 1.8f;
        private const float Lift = 0.06f;

        // The mod's signature colour, so the pad reads as a mod object.
        private static Color Accent => ModStyle.Accent;
        private static readonly Color Idle = new(0.55f, 0.6f, 0.58f);

        private static Mesh quad;
        private static Texture2D baseTexture, ringTexture, fillTexture;

        public GameObject Root { get; }
        public Vector3 Position => Root.transform.position;

        private readonly Transform spinner, fill;
        private readonly Material baseMaterial, ringMaterial, fillMaterial;
        private readonly ParticleSystem shimmer, burst;
        private readonly Light glow;
        private readonly System.Collections.Generic.List<Material> particleMaterials = [];

        public TeleporterPad(Vector3 position, ParticleSystem nativeSparkle)
        {
            Root = new GameObject("PalaceMod Teleporter");
            Root.transform.position = position;

            var shader = Shader.Find("Sprites/Default");
            baseMaterial = new Material(shader) { mainTexture = baseTexture ??= BaseTexture(), renderQueue = 3100 };
            ringMaterial = new Material(shader) { mainTexture = ringTexture ??= RingTexture(), renderQueue = 3101 };
            fillMaterial = new Material(shader) { mainTexture = fillTexture ??= FillTexture(), renderQueue = 3102 };

            Marker("Base", baseMaterial, Radius * 2f, Lift);
            spinner = Marker("Ring", ringMaterial, Radius * 2.35f, Lift + 0.005f).transform;
            fill = Marker("Fill", fillMaterial, 0f, Lift + 0.01f).transform;

            var lightObj = new GameObject("Glow");
            lightObj.transform.SetParent(Root.transform, false);
            lightObj.transform.localPosition = Vector3.up * 1.2f;
            glow = lightObj.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = Accent;
            glow.range = 4f;
            glow.shadows = LightShadows.None;

            if (nativeSparkle != null)
            {
                shimmer = CloneSparkle(nativeSparkle, "Shimmer", loop: true);
                burst = CloneSparkle(nativeSparkle, "Burst", loop: false);
            }
        }

        /// <param name="linked">There is somewhere to go.</param>
        /// <param name="ready">Linked and off cooldown.</param>
        /// <param name="channel">0..1 progress of the king standing on it.</param>
        /// <param name="night">Brighter light at night.</param>
        public void Tick(bool linked, bool ready, float channel, bool night)
        {
            // A touch lighter than the UI accent: the game's colour grading darkens everything on the ground.
            Color color = Color.Lerp(linked ? Accent : Idle, Color.white, 0.2f);
            float alpha = ready ? 1f : 0.45f;
            baseMaterial.color = new Color(color.r, color.g, color.b, 0.95f * alpha);
            ringMaterial.color = new Color(color.r, color.g, color.b, alpha);
            fillMaterial.color = new Color(Accent.r, Accent.g, Accent.b, 0.55f);

            float speed = ready ? 25f + 300f * channel : 8f;
            spinner.Rotate(0f, speed * Time.deltaTime, 0f, Space.World);
            fill.localScale = Vector3.one * (Radius * 2f * Mathf.SmoothStep(0f, 1f, channel));
            fill.gameObject.SetActive(channel > 0f);

            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.time * 2.5f);
            glow.intensity = (night ? 1.6f : 0.3f) * (ready ? pulse : 0.4f) * (1f + 1.5f * channel);

            if (shimmer != null)
            {
                var emission = shimmer.emission;
                emission.rateOverTime = ready ? 10f + 60f * channel : 0f;
            }
        }

        /// <summary>The native sparkle burst, for leaving or arriving.</summary>
        public void Burst()
        {
            if (burst != null) burst.Emit(60);
        }

        public void SetVisible(bool visible) => Root.SetActive(visible);

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            Object.Destroy(baseMaterial);
            Object.Destroy(ringMaterial);
            Object.Destroy(fillMaterial);
            foreach (var material in particleMaterials) Object.Destroy(material);
        }

        private GameObject Marker(string name, Material material, float size, float lift)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root.transform, false);
            go.transform.localPosition = Vector3.up * lift;
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = quad ??= FlatQuad();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        // A copy of the game's waypoint sparkle, recoloured and reshaped to rise from the pad's rim.
        private ParticleSystem CloneSparkle(ParticleSystem native, string name, bool loop)
        {
            var copy = Object.Instantiate(native.gameObject, Root.transform);
            copy.name = name;
            copy.transform.localPosition = Vector3.up * 0.1f;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = Vector3.one;
            foreach (var child in copy.GetComponentsInChildren<ParticleSystem>(true))
                if (child.gameObject != copy) Object.Destroy(child.gameObject);
            var ps = copy.GetComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = loop;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(Accent, Color.Lerp(Accent, Color.white, 0.5f));
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, loop ? 1.6f : 4f);
            main.gravityModifier = loop ? -0.15f : 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = Radius * (loop ? 0.85f : 0.6f);
            shape.radiusThickness = loop ? 0.15f : 1f;
            shape.rotation = new Vector3(-90f, 0f, 0f); // emit upward from a flat circle
            shape.arc = 360f;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 0f;
            emission.burstCount = 0;

            // The native sparkle is white; tint its material too, in case its shader ignores vertex colour.
            var renderer = copy.GetComponent<ParticleSystemRenderer>();
            if (renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_Color"))
            {
                renderer.material.color = Accent;
                particleMaterials.Add(renderer.sharedMaterial);
            }

            if (loop) ps.Play();
            return ps;
        }

        // ---- Textures ---------------------------------------------------------------------------------------------

        private static Mesh FlatQuad()
        {
            var mesh = new Mesh { name = "PalaceMod teleporter quad" };
            mesh.vertices = [new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f)];
            mesh.uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            mesh.triangles = [0, 2, 1, 2, 3, 1];
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private const int Size = 256;

        private static float Band(float d, float center, float halfWidth, float soft = 0.012f) =>
            1f - Mathf.Clamp01((Mathf.Abs(d - center) - halfWidth) / soft);

        // Solid outer ring, a faint inner disc, and a four-point star in the middle (like a compass rose).
        private static Texture2D BaseTexture() => Paint((d, angle) =>
        {
            float outer = Band(d, 0.92f, 0.05f);
            float inner = Band(d, 0.62f, 0.02f) * 0.85f;
            float disc = d < 0.92f ? 0.22f : 0f;
            float star = 0f;
            for (int i = 0; i < 4; i++)
            {
                float a = Mathf.DeltaAngle(angle, i * 90f) / 90f; // -0.5..0.5 within a quadrant
                float reach = 0.56f * (1f - Mathf.Abs(a) * 4.5f);
                if (reach > 0f && d < reach) star = Mathf.Max(star, 0.8f);
            }
            return Mathf.Max(outer, inner, disc, star);
        });

        // A dashed ring with small diamonds, rotated over the base.
        private static Texture2D RingTexture() => Paint((d, angle) =>
        {
            float dash = Mathf.Repeat(angle, 22.5f) < 14f ? 1f : 0f;
            float ring = Band(d, 0.86f, 0.022f) * dash;
            float a = Mathf.Repeat(angle + 11.25f, 45f) - 22.5f; // diamonds between dashes, every 45°
            float diamond = Mathf.Abs(a) / 6f + Mathf.Abs(d - 0.86f) / 0.05f < 1f ? 1f : 0f;
            return Mathf.Max(ring, diamond);
        });

        // A soft disc that grows while channelling.
        private static Texture2D FillTexture() => Paint((d, _) => d < 1f ? Mathf.Lerp(0.9f, 0.2f, d) * Band(d, 0.5f, 0.5f, 0.04f) : 0f);

        // alpha(distance from centre 0..1+, angle in degrees)
        private static Texture2D Paint(System.Func<float, float, float> alpha)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float u = (x + 0.5f) / Size * 2f - 1f, v = (y + 0.5f) / Size * 2f - 1f;
                    float d = Mathf.Sqrt(u * u + v * v);
                    float angle = Mathf.Atan2(v, u) * Mathf.Rad2Deg;
                    float a = d > 1f ? 0f : Mathf.Clamp01(alpha(d, angle));
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
