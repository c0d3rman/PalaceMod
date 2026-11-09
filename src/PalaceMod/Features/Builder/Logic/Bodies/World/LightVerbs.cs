using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/*
    Light (docs/builder-engine.md, "Light"): the level's light (`lighting`), lights that follow entities (the `light`
    component and `setLight`), and how lit a point is (`daylight`, `lightAt`, `.light`, `.lit`, the `lit` filter term).

    How the game looks lit: ColorAndLightManager tints one directional light, the sun, from the level's colour scheme
    (white by day, the scheme's night blue at night, through its sunset colour over 3 s each way) and re-tints every
    shared FlatKit material's outline from it (AdjustAllOutlineColorsBasedOnLight). The FlatKit toon shaders take the
    sun's colour on their lit side, so its colour is what makes night read as night (research: research/*.png in the
    session's scratchpad): a dark blue sun turns the whole map dark by day, while units and the king keep their colours
    (their materials take little light), so they stay readable. The sun's intensity does much less, and Unity fog has
    no effect (the shaders don't sample it), so `lighting` drives the sun's colour.

    - `lighting` writes the sun's colour in LateUpdate (after the game's own transition coroutines), blending from what
      was shown to the target over `fade`; the target is the base (the game's current colour for phase auto, else the
      scheme's day, sunset or night colour) times `color` times `brightness`. The game's own writes are noticed (the
      sun's colour differs from what was last written) and become the auto base, so dusk and dawn still play under an
      override. Outlines follow. `reset`, `for` expiry and the level's end fade (or snap) back and release the sun.
    - Entity lights are Unity point or spot lights on a child of the entity's object, kept at `height` above it and
      turned to its facing (Facing.Of: a unit's model, the king's mesh) plus `aim`. The shaders take extra per-pixel
      lights, but URP lights each object with at most `maxAdditionalLightsCount` of them (1 in this game, so the ground,
      one big mesh, showed only one); while a level has entity lights it's raised to 8 and put back at the end.
    - The light level of a point (LightMath) is daylight (the sun's luminance: 1 by day, about 0.34 at night) plus each
      light's share: its intensity (max 1) out to 80% of its range, fading to 0 at the range; a cone counts only within
      half its width of its yaw. The game's own lights (the castle's night fire, a teleporter's glow) count too, with
      intensity / 3 over 40% of their range (URP's falloff leaves little visible light beyond that).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    // ------------------------------------------------------------------ timing shared by lighting, setLight, setShape

    /// <summary>A `for` param: seconds of world time, or "night"/"day" (until the next dawn/dusk).</summary>
    internal struct Until
    {
        public double? At;       // world time
        public string Phase;     // "night" or "day"
        public bool Set => At != null || Phase != null;

        public static Until From(ActionParams p, LogicWorld w)
        {
            var raw = p.Raw("for");
            if (raw == null || raw.Type == JTokenType.Null) return default;
            if (raw.Type == JTokenType.String && (string)raw is "night" or "day") return new Until { Phase = (string)raw };
            double s = p.Number("for");
            return s > 0 && w != null ? new Until { At = w.Time + s } : default;
        }

        /// <summary>Due now: past its time, or at the transition it waits for (dawn ends "night", dusk ends "day").</summary>
        public bool Due(double now, bool dawn, bool dusk) => At is double t ? now >= t : Phase == "night" ? dawn : Phase == "day" && dusk;

        public JObject ToJson(double now) => !Set ? null : new JObject { ["left"] = At is double t ? Math.Max(0, t - now) : null, ["phase"] = Phase };

        public static Until FromJson(JToken j, double now) =>
            j is JObject o ? new Until { At = (double?)o["left"] is double l ? now + l : null, Phase = (string)o["phase"] } : default;
    }

    /// <summary>Dawn and dusk as the world's ticks see them (for "night"/"day" timers).</summary>
    internal static class DayEdges
    {
        private static bool? wasNight;
        public static bool Dawn, Dusk;

        public static void Tick()
        {
            bool night = WorldCommon.IsNight;
            Dawn = wasNight == true && !night;
            Dusk = wasNight == false && night;
            wasNight = night;
        }

        public static void Reset() { wasNight = null; Dawn = Dusk = false; }
    }

    // ------------------------------------------------------------------ the level's light

    internal static class GlobalLight
    {
        private static readonly Log log = Log.For<LogicLightManager>();

        public static string Phase = "auto";
        public static float Brightness = 1f;
        public static Color Tint = Color.white;
        public static Until Expiry;

        private static bool overriding, releasing;
        private static Color from;
        private static float t = 1f, fade;
        private static Color gameColor = Color.white, lastWritten;
        private static bool haveWritten;
        private static Light sunOf;

        public static bool Active => overriding;

        private static Light Sun => ColorAndLightManager.Instance != null ? ColorAndLightManager.Instance.Sunlight : null;

        /// <summary>The sun's colour as the game would show it now (what `auto` builds on).</summary>
        public static Color GameColor => gameColor;

        public static void Set(string phase, float? brightness, Color? tint, float fadeSeconds, Until until)
        {
            if (phase != null) Phase = phase;
            if (brightness is float b) Brightness = Mathf.Clamp(b, 0f, 2f);
            if (tint is Color c) Tint = c;
            Expiry = until;
            Begin(fadeSeconds);
            releasing = false;
        }

        public static void Release(float fadeSeconds)
        {
            Expiry = default;
            if (!overriding) { Phase = "auto"; Brightness = 1f; Tint = Color.white; return; }
            Begin(fadeSeconds);
            releasing = true;
        }

        private static void Begin(float fadeSeconds)
        {
            var sun = Sun;
            Track(sun);
            from = sun != null ? sun.color : Color.white;
            fade = Mathf.Max(0f, fadeSeconds);
            t = fade <= 0f ? 1f : 0f;
            overriding = true;
            if (fade <= 0f) LateTick(0f);
        }

        /// <summary>Notices the game's own writes to the sun (dusk and dawn transitions).</summary>
        private static void Track(Light sun)
        {
            if (sun == null) return;
            if (sun != sunOf) { sunOf = sun; haveWritten = false; }
            if (!haveWritten || sun.color != lastWritten) gameColor = sun.color;
        }

        private static Color Base()
        {
            var scheme = ColorAndLightManager.Instance?.CurrentColorScheme;
            if (scheme == null) return gameColor;
            return Phase switch
            {
                "day" => scheme.dayLightColor,
                "sunset" => scheme.sunsetLightColor,
                "night" => scheme.nightLightColor,
                _ => gameColor,
            };
        }

        public static Color Target => releasing ? gameColor : Mul(Mul(Base(), Tint), Brightness);

        private static Color Mul(Color a, Color b) => new(a.r * b.r, a.g * b.g, a.b * b.b, 1f);
        private static Color Mul(Color a, float k) => new(a.r * k, a.g * k, a.b * k, 1f);

        /// <summary>After the game's Update and coroutines: write the sun.</summary>
        public static void LateTick(float dt)
        {
            var sun = Sun;
            if (sun == null) return;
            Track(sun);
            if (!overriding) return;
            t = fade <= 0f ? 1f : Mathf.Min(1f, t + dt / fade);
            var c = Color.Lerp(from, Target, Mathf.SmoothStep(0f, 1f, t));
            c.a = 1f;
            if (c != sun.color) Write(sun, c);
            lastWritten = sun.color;
            haveWritten = true;
            if (releasing && t >= 1f)
            {
                overriding = releasing = false;
                haveWritten = false;
                Phase = "auto"; Brightness = 1f; Tint = Color.white;
            }
        }

        private static Camera bgCamera;
        private static Color bgOriginal;

        private static void Write(Light sun, Color c)
        {
            sun.color = c;
            Background(c);
            var m = ColorAndLightManager.Instance;
            var scheme = m?.CurrentColorScheme;
            if (m == null || scheme == null) return;
            try { m.AdjustAllOutlineColorsBasedOnLight(scheme.globalShadowColor, c); }
            catch (Exception e) { log.Caught("Re-tinting outlines", e); }
        }

        /// <summary>The backdrop beyond the map's edge darkens with the light (the game leaves it alone: its own night
        /// blue barely changes how bright the sun is).</summary>
        private static void Background(Color c)
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam != bgCamera) { bgCamera = cam; bgOriginal = cam.backgroundColor; }
            double game = LightMath.Luminance(gameColor.r, gameColor.g, gameColor.b), now = LightMath.Luminance(c.r, c.g, c.b);
            float k = game > 1e-4 ? Mathf.Clamp((float)(now / game), 0f, 1.5f) : 1f;
            if (releasing && t >= 1f) k = 1f;
            cam.backgroundColor = new Color(bgOriginal.r * k, bgOriginal.g * k, bgOriginal.b * k, bgOriginal.a);
        }

        /// <summary>The level ended (or a new world started): give the sun back at once.</summary>
        public static void ResetAll()
        {
            var sun = Sun;
            if (overriding && sun != null && sun == sunOf) Write(sun, gameColor);
            if (bgCamera != null) bgCamera.backgroundColor = bgOriginal;
            bgCamera = null;
            overriding = releasing = false;
            haveWritten = false;
            sunOf = null;
            Phase = "auto"; Brightness = 1f; Tint = Color.white; Expiry = default;
        }

        public static JObject Capture(double now) => !overriding || releasing ? null : new JObject
        {
            ["phase"] = Phase, ["brightness"] = Brightness, ["color"] = "#" + ColorUtility.ToHtmlStringRGB(Tint), ["for"] = Expiry.ToJson(now),
        };

        public static void Restore(JToken state, double now)
        {
            if (state is not JObject o) { if (overriding) Release(0f); return; }
            ColorUtility.TryParseHtmlString((string)o["color"] ?? "#ffffff", out var tint);
            Set((string)o["phase"] ?? "auto", (float?)o["brightness"] ?? 1f, tint, 0f, Until.FromJson(o["for"], now));
        }

        public static string Describe()
        {
            var sun = Sun;
            return $"lighting: {(overriding ? (releasing ? "releasing" : "on") : "off")} phase={Phase} brightness={Brightness:0.##} tint=#{ColorUtility.ToHtmlStringRGB(Tint)} " +
                   $"sun={(sun != null ? "#" + ColorUtility.ToHtmlStringRGB(sun.color) : "none")} game=#{ColorUtility.ToHtmlStringRGB(gameColor)} daylight={LightLevels.Daylight:0.###}";
        }
    }

    // ------------------------------------------------------------------ entity lights

    /// <summary>A light that follows an entity (the `light` component, `setLight`).</summary>
    internal sealed class EntityLight : MonoBehaviour
    {
        public static readonly List<EntityLight> All = [];

        public LogicEntity Entity;
        public Transform Owner;
        public Light Unity;
        public Color Color = new(1f, 0.85f, 0.56f);
        public float Intensity = 1f, Range = 10f, Cone, Aim, Height = 3f, Flicker;
        public bool On = true;
        /// <summary>Changed by `setLight` (savestates keep these).</summary>
        public bool Overridden;

        private float seed;

        public static EntityLight Ensure(LogicEntity e, GameObject go)
        {
            var existing = Of(go);
            if (existing != null) return existing;
            // Not a child of the object: game code walks some objects' children (a barracks spawns its squad from
            // them), so the light follows from outside and goes when its owner does.
            var holder = new GameObject("PalaceBuilder Light");
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().IsValid())
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(holder, go.scene.IsValid() ? go.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            holder.transform.position = go.transform.position;
            var l = holder.AddComponent<EntityLight>();
            l.Entity = e;
            l.Owner = go.transform;
            l.Unity = holder.AddComponent<Light>();
            l.Unity.shadows = LightShadows.None;
            l.Unity.renderMode = LightRenderMode.ForcePixel;
            l.seed = UnityEngine.Random.value * 100f;
            LightLevels.RaiseObjectLightLimit();
            return l;
        }

        /// <summary>Applies a light block (the component, or setLight's params as JSON).</summary>
        public void Apply(JObject o, Func<JToken, double?> num)
        {
            if ((string)o["color"] is { } c && PalaceMod.Builder.Model.Validator.TryParseColor(c, out var rgb)) Color = new Color(rgb.r, rgb.g, rgb.b);
            else if ((string)o["color"] is { } c2 && ColorUtility.TryParseHtmlString(c2, out var col)) Color = col;
            if (num(o["intensity"]) is double i) Intensity = Mathf.Clamp((float)i, 0f, 4f);
            if (num(o["range"]) is double r) Range = Mathf.Clamp((float)r, 0.5f, 200f);
            if (o["cone"] is JToken cone) Cone = cone.Type == JTokenType.Null ? 0f : Mathf.Clamp((float)(num(cone) ?? 0), 0f, 179f);
            if (num(o["aim"]) is double a) Aim = (float)a;
            if (num(o["height"]) is double h) Height = Mathf.Clamp((float)h, 0f, 60f);
            if (num(o["flicker"]) is double f) Flicker = Mathf.Clamp01((float)f);
            if (o["on"] is JToken on && on.Type == JTokenType.Boolean) On = (bool)on;
            Sync();
        }

        public JObject ToJson() => new()
        {
            ["color"] = "#" + ColorUtility.ToHtmlStringRGB(Color), ["intensity"] = Intensity, ["range"] = Range, ["cone"] = Cone > 0 ? Cone : null,
            ["aim"] = Aim, ["height"] = Height, ["flicker"] = Flicker, ["on"] = On,
        };

        private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        private void OnDisable() => All.Remove(this);

        /// <summary>The light that follows this object, or null.</summary>
        public static EntityLight Of(GameObject go)
        {
            if (go == null) return null;
            foreach (var l in All) if (l != null && l.Owner == go.transform) return l;
            return null;
        }

        /// <summary>Lighting the ground: on, its entity alive and switched on.</summary>
        public bool Shining => On && Owner != null && Owner.gameObject.activeInHierarchy && (Entity == null || Entity.Exists && Entity.Enabled);

        /// <summary>The cone's yaw (its owner's facing plus aim).</summary>
        public float Yaw => (Owner != null ? Facing.Of(Owner.gameObject) : 0f) + Aim;

        public Vector3 Ground => Owner != null ? Owner.position : transform.position;

        private void LateUpdate()
        {
            if (Owner == null) { Destroy(gameObject); return; }
            Sync();
        }

        public void Sync()
        {
            if (Unity == null || Owner == null) return;
            float yaw = Yaw;
            transform.position = Owner.position + Vector3.up * Height;
            float slant = Mathf.Sqrt(Range * Range + Height * Height);
            if (Cone > 0f)
            {
                // Aimed down at the middle of the beam, as wide as the cone (a low light makes a long beam).
                float pitch = Mathf.Atan2(Height, Range * 0.55f) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                Unity.type = LightType.Spot;
                Unity.spotAngle = Mathf.Clamp(Cone, 1f, 179f);
                Unity.innerSpotAngle = Mathf.Clamp(Cone * 0.8f, 0f, Unity.spotAngle);
                Unity.range = slant * 1.15f;
            }
            else
            {
                transform.rotation = Quaternion.identity;
                Unity.type = LightType.Point;
                Unity.range = slant;
            }
            float flick = Flicker > 0f ? 1f - Flicker * 0.35f * Mathf.PerlinNoise(Time.time * 3f + seed, seed) : 1f;
            Unity.color = Color;
            Unity.intensity = Intensity * LightLevels.UnityPerIntensity * flick;
            Unity.enabled = Shining;
        }

        public double ContributionAt(Vec p)
        {
            if (!Shining) return 0;
            var g = Ground;
            return LightMath.Contribution(p.X - g.x, p.Z - g.z, Range, Intensity, Cone, Yaw);
        }
    }

    // ------------------------------------------------------------------ how lit a point is

    internal sealed class LightLevels : ILighting
    {
        public static readonly LightLevels Instance = new();

        /// <summary>Share of a game light's Unity range its glow visibly reaches (URP's falloff is steep).</summary>
        public const float GameLightReach = 0.4f;

        /// <summary>Unity intensity per unit of `light.intensity` (3 reads like the game's own fires).</summary>
        public const float UnityPerIntensity = 3f;

        public static double Daylight
        {
            get
            {
                var sun = ColorAndLightManager.Instance != null ? ColorAndLightManager.Instance.Sunlight : null;
                if (sun == null) return 1;
                var c = sun.color;
                return LightMath.Daylight(c.r, c.g, c.b, sun.enabled ? sun.intensity : 0);
            }
        }

        double ILighting.Daylight => Daylight;

        private static Light[] gameLights = [];
        private static float gameLightsAt = -10f;

        /// <summary>The game's own point and spot lights (not entity lights), refreshed once a second.</summary>
        private static Light[] GameLights()
        {
            if (Time.unscaledTime - gameLightsAt < 1f) return gameLights;
            gameLightsAt = Time.unscaledTime;
            gameLights = Object.FindObjectsOfType<Light>().Where(l => l.type is LightType.Point or LightType.Spot && l.GetComponent<EntityLight>() == null).ToArray();
            return gameLights;
        }

        public double LightAt(Vec p)
        {
            double day = Daylight;
            if (day >= 1) return 1;
            double sum = day;
            foreach (var l in EntityLight.All)
            {
                if (l == null) continue;
                sum += l.ContributionAt(p);
                if (sum >= 1) return 1;
            }
            foreach (var l in GameLights())
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                var at = l.transform.position;
                float h = Mathf.Max(0f, at.y - p.Y);
                float reach = l.range * GameLightReach;
                float ground = reach * reach - h * h;
                if (ground <= 0f) continue;
                var fwd = l.transform.forward;
                double yaw = Math.Atan2(fwd.x, fwd.z) * 180 / Math.PI;
                sum += LightMath.Contribution(p.X - at.x, p.Z - at.z, Mathf.Sqrt(ground), l.intensity / UnityPerIntensity, l.type == LightType.Spot ? l.spotAngle : 0, yaw);
                if (sum >= 1) return 1;
            }
            return LightMath.Clamp01(sum);
        }

        // URP's per-object light limit (this game: 1). Raised while entity lights exist; put back at the end.
        private static int? savedLimit;

        public static void RaiseObjectLightLimit()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            var prop = rp?.GetType().GetProperty("maxAdditionalLightsCount");
            if (prop == null || !prop.CanWrite) return;
            int cur = (int)prop.GetValue(rp);
            if (cur >= 8) return;
            savedLimit ??= cur;
            prop.SetValue(rp, 8);
        }

        public static void RestoreObjectLightLimit()
        {
            if (savedLimit is not int v) return;
            var rp = GraphicsSettings.currentRenderPipeline;
            rp?.GetType().GetProperty("maxAdditionalLightsCount")?.SetValue(rp, v);
            savedLimit = null;
        }
    }

    // ------------------------------------------------------------------ the actions and the component

    internal static class LightVerbs
    {
        private static readonly Log log = Log.For<LogicLightManager>();

        private sealed class Revert { public LogicEntity Entity; public JObject Before; public bool Had; public Until At; }

        private static readonly List<Revert> reverts = [];
        private static readonly HashSet<LogicEntity> built = [];
        private static float scanIn;

        public static void Register()
        {
            LogicActions.Register("lighting", Lighting);
            LogicActions.Register("setLight", SetLight, ActionFlags.PerTarget);
            WorldSession.Reset += ResetAll;
        }

        public static void ResetAll()
        {
            GlobalLight.ResetAll();
            foreach (var l in EntityLight.All.ToList()) if (l != null) Object.Destroy(l.gameObject);
            EntityLight.All.Clear();
            reverts.Clear();
            built.Clear();
            DayEdges.Reset();
            LightLevels.RestoreObjectLightLimit();
        }

        private static void Lighting(LogicContext ctx, ActionParams p)
        {
            float fade = (float)p.Number("fade", 1);
            if (p.Bool("reset")) { GlobalLight.Release(fade); return; }
            string phase = p.Has("phase") ? p.Text("phase") : null;
            if (phase != null && phase is not ("auto" or "day" or "sunset" or "night")) throw new LogicError($"lighting: phase \"{phase}\" isn't auto, day, sunset or night");
            float? brightness = p.Has("brightness") ? (float)p.Number("brightness") : null;
            Color? tint = p.Has("color") ? WorldCommon.ParseColor(p.Text("color"), "color") : null;
            GlobalLight.Set(phase, brightness, tint, fade, Until.From(p, ctx.World));
        }

        private static readonly string[] LightFields = ["color", "intensity", "range", "cone", "aim", "height", "flicker", "on"];

        private static void SetLight(LogicContext ctx, ActionParams p)
        {
            var e = p.Target;
            var go = e?.Go();
            if (go == null || !e.Exists) return;
            var existing = Find(go);
            if (p.Bool("remove"))
            {
                if (existing == null) return;
                Remember(e, existing, p, ctx.World);
                Object.Destroy(existing.gameObject);
                EntityLight.All.Remove(existing);
                return;
            }
            var o = new JObject();
            foreach (var f in LightFields)
                if (p.Has(f)) o[f] = f is "color" ? p.Text(f) : f is "on" ? p.Bool(f) : p.Number(f);
            if (p.Has("cone") && p.Number("cone") <= 0) o["cone"] = null;
            Remember(e, existing, p, ctx.World);
            var l = existing ?? EntityLight.Ensure(e, go);
            l.Apply(o, t => t == null || t.Type == JTokenType.Null ? null : (double)t);
            l.Overridden = true;
        }

        private static EntityLight Find(GameObject go)
        {
            return EntityLight.Of(go);
        }

        /// <summary>`for`: keep how the light was, to put it back.</summary>
        private static void Remember(LogicEntity e, EntityLight existing, ActionParams p, LogicWorld w)
        {
            var until = Until.From(p, w);
            if (!until.Set) return;
            if (reverts.Any(r => r.Entity == e)) { foreach (var r in reverts.Where(r => r.Entity == e)) r.At = until; return; }
            reverts.Add(new Revert { Entity = e, Had = existing != null, Before = existing?.ToJson(), At = until });
        }

        /// <summary>Every frame: lights for entities whose template carries one, timed reverts, the sun.</summary>
        public static void Tick(float dt)
        {
            var world = LogicWorld.Current;
            if (world == null) return;
            DayEdges.Tick();
            if (GlobalLight.Expiry.Set && GlobalLight.Expiry.Due(world.Time, DayEdges.Dawn, DayEdges.Dusk)) GlobalLight.Release(1f);
            for (int i = reverts.Count - 1; i >= 0; i--)
            {
                var r = reverts[i];
                if (!r.At.Due(world.Time, DayEdges.Dawn, DayEdges.Dusk)) continue;
                reverts.RemoveAt(i);
                var go = r.Entity.Go();
                if (go == null) continue;
                var l = Find(go);
                if (!r.Had) { if (l != null) { EntityLight.All.Remove(l); Object.Destroy(l.gameObject); } continue; }
                l ??= EntityLight.Ensure(r.Entity, go);
                l.Apply(r.Before, t => t == null || t.Type == JTokenType.Null ? null : (double)t);
            }
            scanIn -= dt;
            if (scanIn > 0f) return;
            scanIn = 0.25f;
            foreach (var e in world.Entities)
            {
                if (e.Def?["light"] is not JObject def || built.Contains(e)) continue;
                var go = e.Go();
                if (go == null) continue;
                built.Add(e);
                try
                {
                    var l = EntityLight.Ensure(e, go);
                    l.Apply(def, t => Num(world, e, t));
                }
                catch (Exception ex) { log.Warn($"{e.Id}: light: {ex.Message}"); }
            }
            built.RemoveWhere(e => e.Destroyed);
        }

        /// <summary>A component number: a literal, or "=expr" evaluated with self = the entity.</summary>
        private static double? Num(LogicWorld w, LogicEntity e, JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type is JTokenType.Integer or JTokenType.Float) return (double)t;
            if (t.Type == JTokenType.String && ((string)t).StartsWith("="))
                return Values.ToNumber(w.Evaluate(((string)t).Substring(1), new LogicContext { World = w, Self = e }));
            return double.TryParse((string)t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        // ---- savestates ----

        public static JToken Capture(LogicWorld w)
        {
            var lights = new JObject();
            foreach (var l in EntityLight.All)
                if (l != null && l.Overridden && l.Entity != null) lights[l.Entity.Id] = l.ToJson();
            return new JObject { ["lighting"] = GlobalLight.Capture(w.Time), ["lights"] = lights };
        }

        public static void Restore(LogicWorld w, JToken state)
        {
            WorldSession.Sync();
            GlobalLight.Restore(state?["lighting"], w.Time);
            if (state?["lights"] is not JObject lights) return;
            Dictionary<string, Transform> byPath = null;
            foreach (var kv in lights)
            {
                var e = w.Find(kv.Key);
                if (e == null && kv.Key.StartsWith("vanilla:"))
                {
                    byPath ??= SaveStates.FindByPath<Transform>();
                    if (byPath.TryGetValue(kv.Key.Substring(8), out var tr)) e = LogicGameWorld.EntityOf(w, tr.gameObject);
                }
                var go = e?.Go();
                if (go == null || kv.Value is not JObject o) continue;
                var l = EntityLight.Ensure(e, go);
                l.Apply(o, t => t == null || t.Type == JTokenType.Null ? null : (double)t);
                l.Overridden = true;
            }
        }

        public static string Status() =>
            $"{GlobalLight.Describe()}\nentity lights: {EntityLight.All.Count(l => l != null)} ({string.Join(", ", EntityLight.All.Where(l => l != null).Take(12).Select(l => $"{l.Entity?.Id ?? l.name}{(l.Shining ? "" : " off")} r={l.Range:0.#}{(l.Cone > 0 ? $" cone={l.Cone:0}@{l.Yaw:0}" : "")}"))})";
    }

    /// <summary>Runs GlobalLight after the game's coroutines (they tint the sun in their Update).</summary>
    internal sealed class LightDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            try { GlobalLight.LateTick(Time.deltaTime); }
            catch (Exception e) { Log.For<LogicLightManager>().Caught("Lighting", e); }
        }
    }
}
