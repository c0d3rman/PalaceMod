using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PalaceMod
{
    public partial class PerformanceManager
    {
        private const float PowerPollSeconds = 5f;
        // Busy-wait budget on battery when "Save CPU on battery" is on: enough to absorb small wake-up delays only.
        private const float BatterySpinMs = 0.5f;
        // Unity clamps deltaTime at 1/3 s, and bigger steps can subtly change game logic, so caps never take a
        // running game below this (like Dynamic FPS). Menus and pause may go lower.
        private const int MinGameplayFps = 15;

        private readonly FramePacer pacer = new();
        private bool refreshRateApplied;
        private float nextPowerPoll;

        /// <summary>Why the frame rate is currently capped, for the log and the settings pane.</summary>
        public string FrameRateReason { get; private set; } = "";
        public float CurrentTargetFps => pacer.Enabled ? pacer.TargetFps : Application.targetFrameRate;

        private bool PrecisePacing => precisePacing.Value == Toggle.On || (precisePacing.Value == Toggle.Auto && IsMac);

        private bool onMainMenu;
        private bool sceneTracking;

        private bool InMenu => Time.timeScale == 0f || onMainMenu;

        // Scene.name allocates, so track the main menu through scene changes instead of checking every frame.
        private void OnSceneChanged(Scene from, Scene to) =>
            onMainMenu = to.name == (SceneTransitionManager.instance?.mainMenuScene ?? "_StartMenu");

        private void UpdateFrameRate()
        {
            if (!sceneTracking)
            {
                sceneTracking = true;
                SceneManager.activeSceneChanged += OnSceneChanged;
                OnSceneChanged(default, SceneManager.GetActiveScene());
            }
            ApplyRefreshRateOnce();
            TrackInput();
            CollectOnPause();
            UpdateLatencyCritical();
            if (IsMac && Time.unscaledTime >= nextPowerPoll)
            {
                nextPowerPoll = Time.unscaledTime + PowerPollSeconds;
                PowerState.Refresh();
            }
            ApplyRenderOverrides();

            float cap = ComputeCap(out string reason, out bool saveCpu);
            FrameRateReason = reason;
            if (PrecisePacing)
            {
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                double refresh = DisplayRefreshRate();
                float target = maxFrameRate.Value > 0 ? Math.Max(MinGameplayFps, maxFrameRate.Value) : (float)refresh;
                pacer.TargetFps = cap > 0 ? Math.Min(target, cap) : target;
                pacer.LockToDisplay = lockToDisplay.Value;
                pacer.MaxSpinMs = saveCpu ? Math.Min(BatterySpinMs, maxSpinMs.Value) : maxSpinMs.Value;
                return;
            }

            if (pacer.Enabled)
            {
                // Pacing was just turned off: hand frame limiting back to the game's own settings.
                pacer.TargetFps = 0;
                Application.targetFrameRate = -1;
            }
            int vSyncCount = vSync.Value switch
            {
                VSyncMode.On => 1,
                VSyncMode.Off => 0,
                _ => SettingsManager.Instance != null && !SettingsManager.Instance.VSync ? 0 : 1,
            };
            if (QualitySettings.vSyncCount != vSyncCount) QualitySettings.vSyncCount = vSyncCount;
            // Without the pacer, apply caps through Unity's limiter (which also works on other platforms).
            // 0 means no limit of our own; the game never sets the limiter, so "no limit" is Unity's default of -1.
            int limit = targetFrameRate.Value > 0 ? Math.Max(MinGameplayFps, targetFrameRate.Value) : -1;
            if (cap > 0 && (limit <= 0 || limit > cap)) limit = (int)cap;
            if (Application.targetFrameRate != limit) Application.targetFrameRate = limit;
        }

        // The lowest cap that applies right now, or 0 for none.
        private float ComputeCap(out string reason, out bool saveCpu)
        {
            float cap = 0;
            string why = "playing";
            void Apply(int value, string because)
            {
                if (value <= 0 || (cap > 0 && value >= cap)) return;
                cap = value;
                why = because;
            }

            bool menu = InMenu;
            if (menu) Apply(menuFrameRate.Value, "menu");
            if (!Application.isFocused) Apply(backgroundFrameRate.Value, "background");
            saveCpu = false;
            if (PowerState.OnBattery || PowerState.LowPowerMode)
            {
                Apply(batteryFrameRate.Value, PowerState.LowPowerMode ? "low power mode" : "battery");
                saveCpu = saveCpuOnBattery.Value;
            }
            if (idleFrameRate.Value > 0 && IdleSeconds >= idleAfterSeconds.Value && !AtNight) Apply(idleFrameRate.Value, "idle");
            if (coolDownWhenHot.Value)
            {
                if (PowerState.ThermalState == PowerState.Thermal.Critical) Apply(30, "thermal pressure (critical)");
                else if (PowerState.ThermalState == PowerState.Thermal.Serious) Apply(60, "thermal pressure");
            }
            if (cap > 0 && cap < MinGameplayFps && !menu) cap = MinGameplayFps;
            reason = why;
            return cap;
        }

        private static bool AtNight => DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        private float lastInputTime;
        private Vector3 lastMousePosition;

        private float IdleSeconds => Time.unscaledTime - lastInputTime;

        // Any key, mouse button, mouse movement, scroll or controller input counts as activity.
        private void TrackInput()
        {
            Vector3 mouse = Input.mousePosition;
            bool active = Input.anyKey || mouse != lastMousePosition || Input.mouseScrollDelta != Vector2.zero ||
                (Rewired.ReInput.isReady && (Rewired.ReInput.controllers.GetAnyButton() || AnyJoystickAxis()));
            lastMousePosition = mouse;
            if (active || DevToolsManager.FakeInputActive) lastInputTime = Time.unscaledTime;
        }

        private static bool AnyJoystickAxis()
        {
            var joysticks = Rewired.ReInput.controllers.Joysticks;
            for (int j = 0; j < joysticks.Count; j++) // indexed: foreach over the IList would allocate every frame
            {
                var joystick = joysticks[j];
                for (int i = 0; i < joystick.axisCount; i++)
                {
                    if (Mathf.Abs(joystick.GetAxis(i)) > 0.2f) return true;
                }
            }
            return false;
        }

        private bool wasInMenu;

        // Collect when a pause or menu starts: the collection's pause can't be felt there.
        private void CollectOnPause()
        {
            bool menu = InMenu;
            if (menu && !wasInMenu && collectWhenPaused.Value)
            {
                var watch = Stopwatch.StartNew();
                GC.Collect();
                Plugin.Logger.LogInfo($"Performance: collected garbage on pause in {watch.Elapsed.TotalMilliseconds:0.0} ms");
            }
            wasInMenu = menu;
        }

        private static double DisplayRefreshRate()
        {
            long period = DisplayLink.PeriodTicks;
            if (period > 0) return Stopwatch.Frequency / (double)period;
            double reported = Screen.currentResolution.refreshRateRatio.value;
            return reported > 1 ? reported : 60;
        }

        // Latency-critical mode keeps macOS from napping the game; release it in the background to save battery.
        private void UpdateLatencyCritical()
        {
            bool wanted = (latencyCritical.Value == Toggle.On || (latencyCritical.Value == Toggle.Auto && IsMac)) && Application.isFocused;
            if (wanted == MacActivity.Active) return;
            try
            {
                if (!wanted) MacActivity.End();
                else if (!MacActivity.Begin())
                {
                    Plugin.Logger.LogWarning("Performance: macOS refused latency-critical mode; turning it off");
                    latencyCritical.Value = Toggle.Off;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Performance: couldn't change latency-critical mode: {e.Message}");
                latencyCritical.Value = Toggle.Off;
            }
        }

        // Done on the first frame rather than in Awake so it lands after the game applies its own display settings.
        private void ApplyRefreshRateOnce()
        {
            if (refreshRateApplied) return;
            refreshRateApplied = true;
            if (raiseRefreshRate.Value <= 0 || !IsMac) return;
            TryLog("refresh rate", () => DisplayMode.RaiseRefreshRate(raiseRefreshRate.Value));
        }

        // The render scale the game itself wants, remembered while we override it so it can be put back.
        private float? gameRenderScale;

        // The game's URP asset isn't referenced at compile time, so set its properties by name.
        private void ApplyRenderOverrides()
        {
            bool battery = (PowerState.OnBattery || PowerState.LowPowerMode) && batteryRenderScale.Value > 0f;
            float scale = battery ? batteryRenderScale.Value : renderScale.Value;
            if (scale <= 0f && msaa.Value <= 0 && gameRenderScale == null) return;
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return;
            var type = pipeline.GetType();
            var scaleProperty = type.GetProperty("renderScale");
            if (scale > 0f)
            {
                gameRenderScale ??= scaleProperty?.GetValue(pipeline) as float?;
                SetIfDifferent(scaleProperty, pipeline, scale);
            }
            else if (gameRenderScale != null)
            {
                // No override any more (e.g. plugged back in): restore the game's own value.
                SetIfDifferent(scaleProperty, pipeline, gameRenderScale.Value);
                gameRenderScale = null;
            }
            if (msaa.Value > 0) SetIfDifferent(type.GetProperty("msaaSampleCount"), pipeline, msaa.Value);
        }

        private static void SetIfDifferent(System.Reflection.PropertyInfo property, object target, object value)
        {
            if (property == null || Equals(property.GetValue(target), value)) return;
            property.SetValue(target, value);
        }
    }
}
