using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/*
    Fast simulation, for automated tests (agent-kit `palace.py test --fast`, `agent fast on`, `dev.py cmd fast on`).
    Every frame advances the game by the same fixed step it would see at 60 fps (Time.captureDeltaTime = 1/60, times
    the game speed as usual), and frames run back to back as fast as the CPU allows: no VSync, no frame pacer, no
    background or idle caps. So the simulation is the same as a smooth 60 fps run at that speed, only faster than real
    time, instead of a few large steps per second (a raised timeScale on a capped frame rate: an unfocused game is
    capped at 15 fps, so 3x speed there means 0.2 s steps).
    While it's on, frames are drawn only about 10 times a wall-clock second (OnDemandRendering), and never in a
    headless (-batchmode) launch, where nobody can see them. Screenshots then come from the main camera rendered into a
    texture (no UI overlay), which also works headless.
    A frame that isn't drawn never reaches its end-of-frame stage, so a WaitForEndOfFrame coroutine waits for the next
    drawn frame (up to SkipInterval frames headless). The game uses one: PlayerMovement.TeleportTo turns the king's
    CharacterController off and back on "at the end of the frame", so after any teleport the king couldn't move for
    seconds (fake input such as `run` then did nothing). TeleportTo_Patch turns it back on the next frame instead.
    Fast mode is per level load: every scene load turns it off again, unless it was turned on with `keep` (`fast on
    keep`, palace.py test, PALACEMOD_FAST=1), which lasts until `fast off`. Otherwise one test that turned it on left
    it on for every later test on the instance, whose real-time waits then ran 10-30x too fast.
*/
namespace PalaceMod
{
    [ManagerOrder(-95)]
    public class FastSimManager : BaseManager
    {
        // Rendered frames per wall-clock second while fast (windowed only), so a watcher still sees it move.
        private const double PreviewIntervalMs = 100;
        private const int SkipInterval = 1000;

        /// <summary>Whether fast simulation is on.</summary>
        public static bool On { get; private set; }
        /// <summary>Simulation frames per game second at 1x (the fixed step is 1/Fps, times the game speed).</summary>
        public static float Fps { get; private set; } = 60f;
        /// <summary>On across scene loads (`fast on keep`); otherwise the next scene load turns fast mode off.</summary>
        public static bool Keep { get; private set; }
        /// <summary>Started with -batchmode (dev.py --headless): there is no window, nothing is ever drawn.</summary>
        public static bool Headless => Application.isBatchMode;

        private static readonly Stopwatch wall = new();
        private static long lastRender;
        private static int framesAtStart;
        private static double wallAtStart;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            wall.Start();
            DevCommands.Register("fast", "fast [on|off] [fps] [keep]   fast simulation: fixed 1/fps steps (default 60), uncapped frames, no drawing; off again at the next scene load unless keep", Command);
            SceneManager.sceneLoaded += (_, _) =>
            {
                if (!On || Keep) return; // any mode: the game (and SceneLoadOrderManager) load levels additively
                Set(false);
                Log.Info("Fast simulation turned off by a scene load (`fast on keep` keeps it across loads)");
            };
            if (Headless)
            {
                // Nothing to look at: never draw a frame except for an explicit capture.
                OnDemandRendering.renderFrameInterval = SkipInterval;
                Log.Info("Headless launch: frames aren't drawn");
            }
            if (Environment.GetEnvironmentVariable("PALACEMOD_FAST") == "1") Set(true, Fps, keep: true);
        }

        private static string Command(string[] args)
        {
            if (args.Length == 0) return Describe();
            Apply(args, "fast");
            return Describe();
        }

        /// <summary>Parses and applies `on|off [fps] [keep]` (the dev command's and the bridge's `agent fast`).</summary>
        public static void Apply(string[] args, string usage)
        {
            string mode = args[0].ToLowerInvariant();
            if (mode is not ("on" or "off")) throw new ArgumentException($"usage: {usage} [on|off] [fps] [keep]");
            float fps = 60f;
            bool keep = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("keep", StringComparison.OrdinalIgnoreCase)) keep = true;
                else if (float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) fps = f;
                else throw new ArgumentException($"usage: {usage} [on|off] [fps] [keep]; not {args[i]}");
            }
            Set(mode == "on", fps, keep);
        }

        /// <summary>Turns fast simulation on (with a fixed step of 1/fps game seconds at 1x) or off. Unless
        /// <paramref name="keep"/>, the next scene load turns it off again.</summary>
        public static string Set(bool on, float fps = 60f, bool keep = false)
        {
            if (fps < 10f || fps > 1000f) throw new ArgumentException("fps must be between 10 and 1000");
            On = on;
            Fps = fps;
            Keep = on && keep;
            Time.captureDeltaTime = on ? 1f / fps : 0f;
            if (on)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                framesAtStart = Time.frameCount;
                wallAtStart = wall.Elapsed.TotalSeconds;
            }
            else if (!Headless) OnDemandRendering.renderFrameInterval = 1;
            Log.For<FastSimManager>().Info($"Fast simulation {(on ? $"on, step 1/{fps:0.#} s{(Keep ? ", kept across loads" : ", until the next scene load")}" : "off")}");
            return Describe();
        }

        /// <summary>One line: on/off, the step, and how fast it has been running (sim frames per wall second).</summary>
        public static string Describe()
        {
            if (!On) return $"fast off{(Headless ? " (headless)" : "")}";
            double seconds = wall.Elapsed.TotalSeconds - wallAtStart;
            int frames = Time.frameCount - framesAtStart;
            string rate = seconds > 0.5 ? $", {frames / seconds:0} frames/s = {frames / seconds / Fps:0.0}x real time at 1x speed" : "";
            return $"fast on{(Keep ? " (kept across loads)" : " (until the next scene load)")}, step 1/{Fps:0.#} s{rate}{(Headless ? " (headless)" : "")}";
        }

        /// <summary>Frames per wall-clock second since fast mode was turned on (0 when off).</summary>
        public static double FramesPerSecond
        {
            get
            {
                double seconds = wall.Elapsed.TotalSeconds - wallAtStart;
                return On && seconds > 0 ? (Time.frameCount - framesAtStart) / seconds : 0;
            }
        }

        public override void Update()
        {
            if (!On || Headless || shooting) return;
            // Draw a frame now and then so the window isn't frozen; the decision applies from the next frame.
            long now = wall.ElapsedMilliseconds;
            if (now - lastRender >= PreviewIntervalMs)
            {
                lastRender = now;
                OnDemandRendering.renderFrameInterval = 1;
            }
            else OnDemandRendering.renderFrameInterval = SkipInterval;
        }

        /// <summary>Whether a screenshot has to come from a camera render instead of the screen: headless, or fast
        /// mode, where the current frame usually isn't drawn.</summary>
        public static bool ShotFromCamera => Headless || On;

        // Frames drawn before a camera shot. On frames that aren't drawn the game's units (skinned, animated meshes)
        // aren't prepared for rendering, so a camera render on its own shows the level without them.
        private const int ShotDrawFrames = 3;
        private static bool shooting;

        /// <summary>Writes a screenshot to <paramref name="path"/>: the screen (with UI) normally; headless or in fast
        /// mode, the main camera rendered into a texture (no UI overlay) after a few drawn frames, so the file appears
        /// a moment later (callers wait for it). Returns how it's taken.</summary>
        public static string Screenshot(string path)
        {
            if (!ShotFromCamera)
            {
                ScreenCapture.CaptureScreenshot(path);
                return "screen";
            }
            if (Camera.main == null) throw new InvalidOperationException("no main camera to take a screenshot with");
            Plugin.Instance.StartCoroutine(CameraShot(path));
            return "camera";
        }

        private static IEnumerator CameraShot(string path)
        {
            shooting = true;
            OnDemandRendering.renderFrameInterval = 1;
            for (int i = 0; i < ShotDrawFrames; i++) yield return null;
            yield return new WaitForEndOfFrame();
            try
            {
                WriteCameraShot(path);
            }
            catch (Exception e)
            {
                Log.For<FastSimManager>().Warn($"Screenshot failed: {e.Message}");
            }
            finally
            {
                shooting = false;
                if (Headless) OnDemandRendering.renderFrameInterval = SkipInterval;
                else if (!On) OnDemandRendering.renderFrameInterval = 1;
            }
        }

        private static void WriteCameraShot(string path)
        {
            var cam = Camera.main;
            if (cam == null) throw new InvalidOperationException("no main camera");
            int width = Headless ? 1280 : Mathf.Max(320, Screen.width);
            int height = Headless ? 720 : Mathf.Max(180, Screen.height);
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, texture.EncodeToPNG());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        /// <summary>Whether frames are being skipped (headless, or fast mode between preview frames).</summary>
        private static bool SkippingFrames => Headless || On;

        private static IEnumerator EnableNextFrame(CharacterController controller)
        {
            yield return null;
            if (controller != null && !controller.enabled) controller.enabled = true;
        }

        [HarmonyPatch(typeof(PlayerMovement))]
        private static class TeleportTo_Patch
        {
            [HarmonyPatch(nameof(PlayerMovement.TeleportTo)), HarmonyPostfix]
            private static void TeleportTo(PlayerMovement __instance) => Resume(__instance);

            [HarmonyPatch(nameof(PlayerMovement.SetTransformTo)), HarmonyPostfix]
            private static void SetTransformTo(PlayerMovement __instance) => Resume(__instance);

            private static void Resume(PlayerMovement king)
            {
                if (!SkippingFrames || king == null || !king.isActiveAndEnabled) return;
                var controller = king.GetComponent<CharacterController>();
                if (controller != null) king.StartCoroutine(EnableNextFrame(controller));
            }
        }
    }
}
