using System.Collections.Generic;
using UnityEngine;

/*
    Stand-ins for the effect aliases (GameFx.Aliases) whose game prefab isn't loaded on the current map. The game's
    effect prefabs are only in memory while something that spawns them is (an enemy's death puff, a weapon's impact),
    so "summon" (the player revive particles) or "fire_small" (the blood wand's glow) exist on some maps and not on
    others. An alias must play everywhere, so each one has a small particle effect built here from a spec: a burst
    (or, for fire_small, a looping flame) in the alias's colours on a soft round sprite. Templates are made once and
    kept (inactive holder, DontDestroyOnLoad); an instance plays on awake and, unless it loops, destroys itself.
*/
namespace PalaceMod.Builder
{
    internal static class FallbackFx
    {
        internal sealed class Spec
        {
            public Color A, B;
            public int Burst;            // particles in one burst (0 = looping)
            public float Rate;           // per second while looping
            public float Life, Speed, Size, Gravity, Radius;
            public bool Up;              // a cone pointing up (else a sphere)
        }

        private static Color C(float r, float g, float b) => new(r, g, b, 1f);

        public static readonly Dictionary<string, Spec> Specs = new()
        {
            ["smoke"] = new Spec { A = C(0.55f, 0.55f, 0.55f), B = C(0.82f, 0.82f, 0.8f), Burst = 14, Life = 0.9f, Speed = 1.5f, Size = 0.9f, Gravity = -0.05f, Radius = 0.4f },
            ["dust_small"] = new Spec { A = C(0.75f, 0.68f, 0.55f), B = C(0.9f, 0.85f, 0.75f), Burst = 10, Life = 0.6f, Speed = 2f, Size = 0.5f, Radius = 0.3f },
            ["dust_large"] = new Spec { A = C(0.75f, 0.68f, 0.55f), B = C(0.9f, 0.85f, 0.75f), Burst = 30, Life = 1f, Speed = 3.5f, Size = 1.2f, Radius = 1f },
            ["explosion"] = new Spec { A = C(1f, 0.4f, 0.08f), B = C(1f, 0.85f, 0.3f), Burst = 50, Life = 0.7f, Speed = 6f, Size = 1.4f, Radius = 0.5f, Gravity = 0.3f },
            ["burst"] = new Spec { A = C(1f, 0.9f, 0.6f), B = C(1f, 1f, 1f), Burst = 24, Life = 0.5f, Speed = 5f, Size = 0.6f, Radius = 0.3f },
            ["summon"] = new Spec { A = C(0.55f, 0.35f, 1f), B = C(0.75f, 0.9f, 1f), Burst = 40, Life = 1.2f, Speed = 2.5f, Size = 0.35f, Gravity = -0.3f, Radius = 1f, Up = true },
            ["spikes"] = new Spec { A = C(0.75f, 0.75f, 0.8f), B = C(1f, 1f, 1f), Burst = 16, Life = 0.35f, Speed = 7f, Size = 0.25f, Radius = 0.6f, Up = true },
            ["slash"] = new Spec { A = C(0.9f, 0.9f, 0.95f), B = C(1f, 1f, 1f), Burst = 12, Life = 0.25f, Speed = 8f, Size = 0.3f, Radius = 0.3f },
            ["hit"] = new Spec { A = C(1f, 1f, 1f), B = C(1f, 0.8f, 0.6f), Burst = 8, Life = 0.3f, Speed = 4f, Size = 0.35f, Radius = 0.2f },
            ["fire_small"] = new Spec { A = C(1f, 0.35f, 0.05f), B = C(1f, 0.85f, 0.2f), Rate = 25f, Life = 0.6f, Speed = 1.4f, Size = 0.45f, Gravity = -0.2f, Radius = 0.25f, Up = true },
            ["ward"] = new Spec { A = C(0.5f, 0.8f, 1f), B = C(1f, 1f, 1f), Burst = 20, Life = 0.5f, Speed = 3f, Size = 0.4f, Radius = 0.4f },
            ["coins"] = new Spec { A = C(1f, 0.8f, 0.2f), B = C(1f, 0.95f, 0.5f), Burst = 14, Life = 0.6f, Speed = 3f, Size = 0.3f, Radius = 0.3f, Up = true },
            ["blood"] = new Spec { A = C(0.6f, 0.02f, 0.02f), B = C(0.85f, 0.1f, 0.1f), Burst = 18, Life = 0.5f, Speed = 4f, Size = 0.3f, Gravity = 1f, Radius = 0.3f },
            ["impact"] = new Spec { A = C(1f, 0.9f, 0.7f), B = C(1f, 0.6f, 0.2f), Burst = 20, Life = 0.4f, Speed = 5f, Size = 0.45f, Radius = 0.3f },
            ["goo"] = new Spec { A = C(0.3f, 0.8f, 0.2f), B = C(0.6f, 1f, 0.3f), Burst = 18, Life = 0.6f, Speed = 3.5f, Size = 0.4f, Gravity = 0.8f, Radius = 0.3f },
            ["ally_death"] = new Spec { A = C(0.6f, 0.7f, 1f), B = C(1f, 1f, 1f), Burst = 16, Life = 0.9f, Speed = 1.5f, Size = 0.8f, Radius = 0.4f },
        };

        private static GameObject holder;
        private static Material material;
        private static readonly Dictionary<string, GameObject> templates = [];

        /// <summary>The stand-in template for an alias (made on first use), or null when it has no spec.</summary>
        public static GameObject Template(string alias)
        {
            if (!Specs.TryGetValue(alias, out var spec)) return null;
            if (templates.TryGetValue(alias, out var t) && t != null) return t;
            if (holder == null)
            {
                holder = new GameObject("PalaceMod Fallback FX");
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
            }
            t = Build(alias, spec, holder.transform);
            templates[alias] = t;
            return t;
        }

        private static GameObject Build(string alias, Spec s, Transform parent)
        {
            // Under the inactive holder before the particle system is added, so it isn't playing while it's set up.
            var go = new GameObject("PalaceMod FX " + alias);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            bool loop = s.Burst <= 0;
            var main = ps.main;
            main.duration = loop ? 1f : 0.2f;
            main.loop = loop;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(s.Life * 0.6f, s.Life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s.Speed * 0.5f, s.Speed);
            main.startSize = new ParticleSystem.MinMaxCurve(s.Size * 0.6f, s.Size);
            main.startColor = new ParticleSystem.MinMaxGradient(s.A, s.B);
            main.gravityModifier = s.Gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 200;
            main.stopAction = loop ? ParticleSystemStopAction.None : ParticleSystemStopAction.Destroy;

            var em = ps.emission;
            em.rateOverTime = loop ? s.Rate : 0f;
            if (!loop) em.SetBursts([new ParticleSystem.Burst(0f, (short)s.Burst)]);

            var shape = ps.shape;
            shape.enabled = true;
            if (s.Up)
            {
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 25f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
            }
            else shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.01f, s.Radius);

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.1f));
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys([new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f)],
                         [new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.6f), new GradientAlphaKey(0f, 1f)]);
            col.color = new ParticleSystem.MinMaxGradient(fade);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = Material;
            return go;
        }

        /// <summary>A soft round dot on the sprite shader (vertex colours tint it), shared by every stand-in.</summary>
        private static Material Material
        {
            get
            {
                if (material != null) return material;
                const int n = 32;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "PalaceMod FX dot", wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                        px[y * n + x] = new Color32(255, 255, 255, (byte)(255 * a * a * (3f - 2f * a)));
                    }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                material = new Material(Shader.Find("Sprites/Default")) { name = "PalaceMod FX", mainTexture = tex };
                return material;
            }
        }
    }
}
