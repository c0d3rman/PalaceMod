using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

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
            DevCommands.Register("fast", "fast [on|off] [fps]   fast simulation: fixed 1/fps steps (default 60), uncapped frames, no drawing", Command);
            if (Headless)
            {
                // Nothing to look at: never draw a frame except for an explicit capture.
                OnDemandRendering.renderFrameInterval = SkipInterval;
                Log.Info("Headless launch: frames aren't drawn");
            }
            if (Environment.GetEnvironmentVariable("PALACEMOD_FAST") == "1") Set(true, Fps);
        }

        private static string Command(string[] args)
        {
            if (args.Length == 0) return Describe();
            string mode = args[0].ToLowerInvariant();
            if (mode is not ("on" or "off")) throw new ArgumentException("usage: fast [on|off] [fps]");
            float fps = args.Length > 1 ? float.Parse(args[1], CultureInfo.InvariantCulture) : 60f;
            return Set(mode == "on", fps);
        }

        /// <summary>Turns fast simulation on (with a fixed step of 1/fps game seconds at 1x) or off.</summary>
        public static string Set(bool on, float fps = 60f)
        {
            if (fps < 10f || fps > 1000f) throw new ArgumentException("fps must be between 10 and 1000");
            On = on;
            Fps = fps;
            Time.captureDeltaTime = on ? 1f / fps : 0f;
            if (on)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                framesAtStart = Time.frameCount;
                wallAtStart = wall.Elapsed.TotalSeconds;
            }
            else if (!Headless) OnDemandRendering.renderFrameInterval = 1;
            Log.For<FastSimManager>().Info($"Fast simulation {(on ? $"on, step 1/{fps:0.#} s" : "off")}");
            return Describe();
        }

        /// <summary>One line: on/off, the step, and how fast it has been running (sim frames per wall second).</summary>
        public static string Describe()
        {
            if (!On) return $"fast off{(Headless ? " (headless)" : "")}";
            double seconds = wall.Elapsed.TotalSeconds - wallAtStart;
            int frames = Time.frameCount - framesAtStart;
            string rate = seconds > 0.5 ? $", {frames / seconds:0} frames/s = {frames / seconds / Fps:0.0}x real time at 1x speed" : "";
            return $"fast on, step 1/{Fps:0.#} s{rate}{(Headless ? " (headless)" : "")}";
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
    }
}
