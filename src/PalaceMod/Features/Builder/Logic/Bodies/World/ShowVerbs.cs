using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Assets;
using PalaceMod.Builder.Model;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Show actions (docs/builder-engine.md, "Actions", Show): effect, sound, message, floatText, setVisual, shake, marker.
    (hud is LevelHud.cs.)

    - effect: a prefab from GameFx (the game's own visual effects), instantiated like the game does (prefab rotation),
      scaled, and destroyed after 10 s at the latest.
    - sound: a GameFx sound or a pack's "asset:" clip, through ThronefallAudioManager's one-shot pool on the SFX mixer
      (positional when `at` is given).
    - floatText: a label in the game's bold serif that rises from a world point and fades, drawn on the mod's overlay
      canvas so it keeps the game's UI scale.
    - setVisual: tint and glow paint clones of the model's materials (FlatKit: _BaseColor and _ColorDim, as unit tints
      do), scale multiplies the original scale, visible switches renderers and GPU instancing, mesh swaps the model
      (AssetVisuals). With `for`, everything goes back to how it was before the first timed change.
    - shake: the game's camera punch (CameraController.shakeCurve on the orthographic size), relative, so zoom is kept.
    - marker: the game's enemy-spawn screen marker (EnemySpawnManager.screenMarkerPrefabEnemySpawns) on a point or
      following an entity, with an icon.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class ShowVerbs
    {
        public static void Register()
        {
            LogicActions.Register("effect", Effect);
            LogicActions.Register("sound", Sound);
            LogicActions.Register("message", Message);
            LogicActions.Register("floatText", FloatText);
            LogicActions.Register("setVisual", SetVisual, ActionFlags.PerTarget);
            LogicActions.Register("shake", Shake);
            LogicActions.Register("marker", Marker);
            WorldSession.Reset += ResetAll;
        }

        private static void ResetAll()
        {
            foreach (var f in floaters) if (f.Label != null) Object.Destroy(f.Label.gameObject);
            floaters.Clear();
            foreach (var v in visuals.Values) Revert(v);
            visuals.Clear();
            foreach (var m in markers.Values) if (m.Marker != null) Object.Destroy(m.Marker.gameObject);
            markers.Clear();
            foreach (var m in paints.Values) if (m != null) Object.Destroy(m);
            paints.Clear();
        }

        // ------------------------------------------------------------------ effect and sound

        private static void Effect(LogicContext ctx, ActionParams p)
        {
            string id = p.Text("fx") ?? throw new LogicError("effect: needs \"fx\"");
            var fx = GameFx.FindEffect(id) ?? throw new LogicError($"effect: no game effect \"{id}\"{WorldCommon.Near(GameFx.Effects.Keys, id)} (`builder dump` lists them)");
            var at = WorldCommon.At(ctx, p) ?? throw new LogicError("effect: needs \"at\"");
            float scale = (float)p.Number("scale", 1);
            var go = Object.Instantiate(fx.Prefab, at, fx.Prefab.transform.rotation);
            if (!Mathf.Approximately(scale, 1f))
            {
                go.transform.localScale *= scale;
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
            }
            Object.Destroy(go, 10f);
        }

        private static void Sound(LogicContext ctx, ActionParams p)
        {
            string id = p.Text("sound") ?? throw new LogicError("sound: needs \"sound\"");
            var audio = ThronefallAudioManager.Instance;
            if (audio == null) return;
            AudioClip clip;
            if (id.StartsWith("asset:"))
            {
                var entry = WorldCommon.PackEntry ?? throw new LogicError("sound: \"asset:\" sounds need a Builder session");
                clip = BuilderServices.Assets.GetAudio(entry, id);
                if (clip == null) { WorldCommon.log.Debug($"sound {id}: not loaded (yet)"); return; }
            }
            else clip = GameFx.FindSound(id) ?? throw new LogicError($"sound: no game sound \"{id}\"{WorldCommon.Near(GameFx.Sounds.Keys, id)} (`builder dump` lists them)");
            Vector3? at = p.Has("at") ? WorldCommon.At(ctx, p) : null;
            audio.PlaySoundAsOneShot(clip, 1f, 1f, audio.mgSFX, 128, at != null, at ?? default);
        }

        private static void Message(LogicContext ctx, ActionParams p)
        {
            string text = p.Text("text") ?? throw new LogicError("message: needs \"text\"");
            var kind = (p.Text("kind", "info") ?? "info").ToLowerInvariant() switch
            {
                "success" => NotifyKind.Success,
                "warning" => NotifyKind.Warning,
                "error" => NotifyKind.Error,
                _ => NotifyKind.Info,
            };
            Notify.Show(text, kind, (float)p.Number("seconds", Notify.DefaultSeconds));
        }

        // ------------------------------------------------------------------ floatText

        private sealed class Floater
        {
            public TextMeshProUGUI Label;
            public Vector3 World;
            public float Born;
            public Color Color;
        }

        private static readonly List<Floater> floaters = [];
        private const float FloatSeconds = 1.4f, FloatRise = 2.5f;

        private static void FloatText(LogicContext ctx, ActionParams p)
        {
            string text = p.Text("text") ?? throw new LogicError("floatText: needs \"text\"");
            var at = WorldCommon.At(ctx, p) ?? throw new LogicError("floatText: needs \"at\"");
            var color = p.Has("color") ? WorldCommon.ParseColor(p.Text("color"), "color") : ModStyle.Bright;
            var canvas = WorldOverlay.Canvas;
            if (canvas == null) return;
            if (floaters.Count >= 60) { Object.Destroy(floaters[0].Label.gameObject); floaters.RemoveAt(0); }
            var label = GameWidgets.Text(canvas, "PalaceMod floatText", text, ModStyle.BoldFont, 30f, color, TextAlignmentOptions.Center);
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.outlineWidth = 0.22f;
            label.outlineColor = new Color32(26, 28, 29, 255);
            var rt = label.rectTransform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(Mathf.Ceil(label.GetPreferredValues(text).x) + 8f, 40f);
            floaters.Add(new Floater { Label = label, World = at + Vector3.up * 2f, Born = Time.time, Color = color });
            TickFloaters();
        }

        private static void TickFloaters()
        {
            if (floaters.Count == 0) return;
            var cam = Camera.main;
            float scale = WorldOverlay.ScaleFactor;
            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                var f = floaters[i];
                float t = (Time.time - f.Born) / FloatSeconds;
                if (f.Label == null || t >= 1f || cam == null)
                {
                    if (f.Label != null) Object.Destroy(f.Label.gameObject);
                    floaters.RemoveAt(i);
                    continue;
                }
                var screen = cam.WorldToScreenPoint(f.World + Vector3.up * FloatRise * (1f - (1f - t) * (1f - t)));
                f.Label.enabled = screen.z > 0;
                f.Label.rectTransform.anchoredPosition = new Vector2(screen.x, screen.y) / scale;
                var c = f.Color;
                c.a = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
                f.Label.color = c;
            }
        }

        // ------------------------------------------------------------------ setVisual

        private sealed class VisualState
        {
            public GameObject Go;
            public Vector3 Scale;
            public readonly Dictionary<Renderer, Material[]> Materials = [];
            public readonly Dictionary<Renderer, bool> Enabled = [];
            public readonly Dictionary<GPUInstanced, bool> Instanced = [];
            public readonly Dictionary<MeshFilter, Mesh> Meshes = [];
            public HashSet<Transform> Children;
            public float Until;              // game time; +inf = no timed change pending
            public bool Timed;
        }

        private static readonly Dictionary<GameObject, VisualState> visuals = [];
        private static readonly Dictionary<(Material, Color, Color), Material> paints = [];

        private static VisualState Capture(GameObject go)
        {
            var s = new VisualState { Go = go, Scale = go.transform.localScale, Children = new HashSet<Transform>(go.GetComponentsInChildren<Transform>(true)) };
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                s.Materials[r] = r.sharedMaterials;
                s.Enabled[r] = r.enabled;
            }
            foreach (var g in go.GetComponentsInChildren<GPUInstanced>(true)) s.Instanced[g] = g.enabled;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) s.Meshes[mf] = mf.sharedMesh;
            return s;
        }

        private static void Revert(VisualState s)
        {
            if (s.Go == null) return;
            s.Go.transform.localScale = s.Scale;
            foreach (var t in s.Go.GetComponentsInChildren<Transform>(true))
                if (!s.Children.Contains(t) && t != null) Object.Destroy(t.gameObject);
            foreach (var kv in s.Meshes) if (kv.Key != null) kv.Key.sharedMesh = kv.Value;
            foreach (var kv in s.Materials) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            foreach (var kv in s.Instanced)
            {
                if (kv.Key == null) continue;
                kv.Key.enabled = kv.Value;
                var mr = kv.Key.GetComponent<MeshRenderer>();
                var mf = kv.Key.GetComponent<MeshFilter>();
                if (mr != null && mf != null && kv.Value) kv.Key.OnMeshOrMaterialUpdate(mf.sharedMesh, mr.sharedMaterial);
            }
            foreach (var kv in s.Enabled) if (kv.Key != null && !(kv.Key.GetComponent<GPUInstanced>() is { enabled: true })) kv.Key.enabled = kv.Value;
        }

        private static void SetVisual(LogicContext ctx, ActionParams p)
        {
            var e = p.Target ?? ctx.Self;
            var go = e?.Go();
            if (go == null) throw new LogicError($"setVisual: {e?.Id ?? "the target"} has no model");
            if (!visuals.TryGetValue(go, out var state)) visuals[go] = state = Capture(go);
            double seconds = p.Has("for") ? p.Number("for") : 0;
            if (seconds > 0) { state.Timed = true; state.Until = Mathf.Max(state.Until, Time.time + (float)seconds); }

            if (p.Has("mesh"))
            {
                string mesh = p.Text("mesh");
                var notes = new List<string>();
                AssetVisuals.Apply(go, WorldCommon.PackEntry, new VisualsDef { Mesh = mesh }, notes, []);
                foreach (var g in go.GetComponentsInChildren<GPUInstanced>(true))
                {
                    var mf = g.GetComponent<MeshFilter>();
                    var mr = g.GetComponent<MeshRenderer>();
                    if (mf != null && mr != null && g.enabled) g.OnMeshOrMaterialUpdate(mf.sharedMesh, mr.sharedMaterial);
                }
                if (notes.Count > 0) throw new LogicError($"setVisual mesh: {string.Join("; ", notes)}");
            }
            if (p.Has("scale")) go.transform.localScale = state.Scale * (float)p.Number("scale", 1);
            if (p.Has("tint") || p.Has("glow"))
            {
                var tint = p.Has("tint") && p.Text("tint") is { Length: > 0 } t ? WorldCommon.ParseColor(t, "tint") : Color.white;
                var glow = Color.clear;
                if (p.Has("glow"))
                {
                    var g = p.Get("glow");
                    if (g.Kind == ValueKind.Bool) glow = g.Num != 0 ? new Color(1f, 0.85f, 0.45f, 0.5f) : Color.clear;
                    else if (g.Kind == ValueKind.Number) glow = new Color(1f, 0.85f, 0.45f, Mathf.Clamp01((float)g.Num));
                    else if (p.Text("glow") is { Length: > 0 } gt) { glow = WorldCommon.ParseColor(gt, "glow"); glow.a = 0.5f; }
                }
                foreach (var kv in state.Materials)
                {
                    if (kv.Key == null) continue;
                    kv.Key.sharedMaterials = kv.Value.Select(m => Paint(m, tint, glow)).ToArray();
                }
            }
            if (p.Has("visible"))
            {
                bool on = p.Bool("visible", true);
                foreach (var g in state.Instanced.Keys) if (g != null) g.enabled = on && state.Instanced[g];
                foreach (var r in state.Enabled.Keys)
                {
                    if (r == null) continue;
                    var gi = r.GetComponent<GPUInstanced>();
                    r.enabled = on && state.Enabled[r] && !(gi != null && gi.enabled);
                }
            }
        }

        /// <summary>A clone of a material painted like a unit tint (lit and shaded side), plus a glow toward a colour.</summary>
        private static Material Paint(Material m, Color tint, Color glow)
        {
            if (m == null || (tint == Color.white && glow.a <= 0f)) return m;
            if (paints.TryGetValue((m, tint, glow), out var clone) && clone != null) return clone;
            clone = new Material(m) { name = m.name };
            string baseProp = clone.HasProperty("_BaseColor") ? "_BaseColor" : clone.HasProperty("_Color") ? "_Color" : null;
            if (baseProp != null)
            {
                var c = clone.GetColor(baseProp) * tint;
                c = Color.Lerp(c, new Color(glow.r, glow.g, glow.b, c.a), glow.a);
                clone.SetColor(baseProp, c);
                if (clone.HasProperty("_ColorDim"))
                {
                    var dim = clone.GetColor("_ColorDim");
                    dim = Color.Lerp(dim, dim * tint, 0.5f);
                    clone.SetColor("_ColorDim", Color.Lerp(dim, new Color(glow.r, glow.g, glow.b, dim.a), glow.a));
                }
            }
            return paints[(m, tint, glow)] = clone;
        }

        private static void TickVisuals()
        {
            if (visuals.Count == 0) return;
            List<GameObject> drop = null;
            foreach (var kv in visuals)
            {
                if (kv.Key == null) { (drop ??= []).Add(kv.Key); continue; }
                if (kv.Value.Timed && Time.time >= kv.Value.Until) { Revert(kv.Value); (drop ??= []).Add(kv.Key); }
            }
            if (drop != null) foreach (var go in drop) visuals.Remove(go);
        }

        // ------------------------------------------------------------------ shake

        private static Coroutine shaking;
        private static float shakeOffset;

        private static void Shake(LogicContext ctx, ActionParams p)
        {
            var cc = CameraController.instance;
            if (cc == null || Camera.main == null || cc.shakeCurve == null) return;
            if (SettingsManager.Instance != null && SettingsManager.Instance.DisableScreenShake) return;
            float strength = (float)p.Number("strength", 1);
            float seconds = Mathf.Clamp((float)p.Number("seconds", cc.shakeDuration), 0.05f, 10f);
            if (shaking != null) Plugin.Instance.StopCoroutine(shaking);
            shaking = Plugin.Instance.StartCoroutine(ShakeRoutine(Camera.main, cc.shakeCurve, cc.shakeScaleFactor * strength, seconds));
        }

        // Like CameraController.ExecuteShake, but relative to the size each frame, so zooming meanwhile is kept.
        private static IEnumerator ShakeRoutine(Camera cam, AnimationCurve curve, float scale, float duration)
        {
            float timer = 0f;
            while (timer <= duration && cam != null)
            {
                timer += Time.deltaTime;
                float offset = curve.Evaluate(timer / duration) * scale;
                cam.orthographicSize += offset - shakeOffset;
                shakeOffset = offset;
                yield return null;
            }
            if (cam != null) cam.orthographicSize -= shakeOffset;
            shakeOffset = 0f;
            shaking = null;
        }

        // ------------------------------------------------------------------ marker

        private sealed class MarkerRec
        {
            public ScreenMarker Marker;
            public LogicEntity On;
            public float Until;
        }

        private static readonly Dictionary<string, MarkerRec> markers = [];

        private static void Marker(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("marker");
            LogicEntity on = null;
            Vector3 pos;
            if (p.Has("on"))
            {
                var v = p.Get("on");
                on = v.Kind == ValueKind.Entity ? v.AsEntity : v.Kind == ValueKind.Set && v.AsSet.Count > 0 ? v.AsSet[0] : null;
                pos = (p.Position("on") ?? throw new LogicError("marker: \"on\" is empty")).ToVector3();
            }
            else
            {
                on = ctx.Self?.Kind == "level" ? null : ctx.Self;
                pos = on?.Position.ToVector3() ?? throw new LogicError("marker: needs \"on\"");
            }
            string key = on?.Id ?? $"{pos.x:0.#},{pos.z:0.#}";
            double seconds = p.Has("for") ? p.Number("for") : -1;
            if (markers.TryGetValue(key, out var old))
            {
                if (old.Marker != null) Object.Destroy(old.Marker.gameObject);
                markers.Remove(key);
            }
            if (p.Has("for") && seconds <= 0) return;   // for: 0 removes it
            var prefab = EnemySpawnManager.instance != null ? EnemySpawnManager.instance.screenMarkerPrefabEnemySpawns : null;
            if (prefab == null) throw new LogicError("marker: the game's screen marker isn't loaded");
            var marker = Object.Instantiate(prefab, pos, Quaternion.identity).GetComponent<ScreenMarker>();
            marker.SetSprite(Icon(p, on));
            marker.SetNumber(0);
            marker.SetElite(false);
            markers[key] = new MarkerRec { Marker = marker, On = on, Until = seconds > 0 ? Time.time + (float)seconds : float.PositiveInfinity };
        }

        private static Sprite Icon(ActionParams p, LogicEntity on)
        {
            string icon = p.Has("icon") ? p.Text("icon") : null;
            if (icon != null && icon.StartsWith("asset:"))
            {
                var entry = WorldCommon.PackEntry;
                var sprite = entry != null ? BuilderServices.Assets.GetSprite(entry, icon) : null;
                if (sprite != null) return sprite;
                throw new LogicError($"marker: icon {icon} isn't available");
            }
            if (icon != null)
            {
                string unit = icon.StartsWith("unit:") ? icon.Substring(5) : icon;
                var prefab = BuilderServices.Units.Resolve(WorldCommon.Session?.Pack, unit, out _) ?? BuilderServices.Units.Resolve(WorldCommon.Session?.Pack, "vanilla:" + unit, out _);
                var s = prefab != null ? prefab.GetComponent<ScreenMarkerIcon>()?.sprite : null;
                return s != null ? s : throw new LogicError($"marker: no icon \"{icon}\" (an \"asset:\" sprite or a unit id)");
            }
            var own = on?.Go() != null ? on.Go().GetComponentInParent<ScreenMarkerIcon>() : null;
            return own != null && own.sprite != null ? own.sprite : ModStyle.EmblemSprite;
        }

        private static void TickMarkers()
        {
            if (markers.Count == 0) return;
            List<string> drop = null;
            foreach (var kv in markers)
            {
                var m = kv.Value;
                bool gone = m.Marker == null || Time.time >= m.Until || (m.On != null && !m.On.Exists);
                if (gone)
                {
                    if (m.Marker != null) Object.Destroy(m.Marker.gameObject);
                    (drop ??= []).Add(kv.Key);
                    continue;
                }
                if (m.On != null) m.Marker.transform.position = m.On.Position.ToVector3();
            }
            if (drop != null) foreach (var k in drop) markers.Remove(k);
        }

        public static void Tick()
        {
            TickFloaters();
            TickVisuals();
            TickMarkers();
        }

        public static string Status() =>
            $"floatTexts={floaters.Count} visuals={visuals.Count} (timed {visuals.Values.Count(v => v.Timed)}) markers={markers.Count} paints={paints.Count}";
    }

    /// <summary>The mod's overlay canvas for in-world labels and the level HUD: under the game's menus, at its UI scale.</summary>
    internal static class WorldOverlay
    {
        private static GameObject root;
        private static CanvasScaler scaler;

        public static Transform Canvas
        {
            get
            {
                if (root == null)
                {
                    root = new GameObject("PalaceMod Logic Overlay");
                    Object.DontDestroyOnLoad(root);
                    var canvas = root.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 900;   // under the game's UI (menus at 1000)
                    scaler = root.AddComponent<CanvasScaler>();
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                }
                Sync();
                return root.transform;
            }
        }

        public static float ScaleFactor => scaler != null ? scaler.scaleFactor : 1f;

        public static void Sync()
        {
            if (scaler == null) return;
            var game = GameWidgets.Canvas != null ? GameWidgets.Canvas.GetComponent<Canvas>() : null;
            scaler.scaleFactor = game != null ? game.scaleFactor : Mathf.Max(1f, Screen.height / 1080f);
        }

        public static void Destroy()
        {
            if (root != null) Object.Destroy(root);
        }
    }
}
