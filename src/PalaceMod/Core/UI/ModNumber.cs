using System;
using Rewired;
using UnityEngine;

/*
    A number field for ModFrame pages:

        page.Number("Player level", () => level, v => SetLevel(v), 1, 9999);                        // int
        page.Number("Difficulty", () => wave.difficulty, v => SetDifficulty(v), 0.5f, 5f, 0.1f, v => $"x{v:0.0}");
        page.Number("Cost", () => cost ?? baseCost, v => cost = v, 0, 999, onClear: () => cost = null);

    - Left/right (keyboard, controller, or the arrows beside the value with the mouse) step it. Holding a direction
      speeds up (1, 5, 25, 125 steps per repeat; at most a twentieth of the range). Shift steps x10, and so do the
      controller's tab buttons (shoulders).
    - Apply (Enter, a click) types a value: Enter confirms, Esc cancels. The value is clamped to [min, max] (with a
      notice), rounded for an int field, and junk is refused. A unit after the number is fine if the field shows it
      ("60 fps", "x1.5", "50%"); "10k" is 10,000. An empty entry calls onClear when given (e.g. back to a default).
    - Home/End jump to min/max.
    set is only called with a changed, clamped value; the row re-reads get afterwards (set may also rebuild the page).
    Settings > Mods sliders use the same stepping and typing (SettingsPaneManager.BuildSlider).
*/
namespace PalaceMod
{
    public static class ModNumber
    {
        /// <summary>A number field (float). <paramref name="format"/> shows a value (default: up to two decimals).</summary>
        public static ModField Number(this ModPage page, string label, Func<float> get, Action<float> set, float min, float max,
            float step = 1f, Func<float, string> format = null, Func<string> tooltip = null, Action onClear = null) =>
            Build(page, label, get, set, min, max, step, format, tooltip, onClear, integer: false);

        /// <summary>A whole-number field.</summary>
        public static ModField Number(this ModPage page, string label, Func<int> get, Action<int> set, int min, int max,
            int step = 1, Func<int, string> format = null, Func<string> tooltip = null, Action onClear = null) =>
            Build(page, label, () => get(), v => set((int)Math.Round(v)), min, max, Math.Max(1, step),
                format == null ? null : v => format((int)Math.Round(v)), tooltip, onClear, integer: true);

        /// <summary>Shift held (or forced by the dev command): steps go x10.</summary>
        public static bool ShiftHeld => DebugShift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        internal static bool DebugShift;

        /// <summary>The player is using a controller, which can't type: apply on a number field just explains.</summary>
        internal static bool UsingController
        {
            get
            {
                try { return Plugin.Input?.controllers.GetLastActiveController()?.type == ControllerType.Joystick; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Reads typed text as a number for a field showing values with <paramref name="display"/>. False (with a
        /// message) for junk; true with the value clamped (and a message saying so) otherwise.
        /// </summary>
        public static bool TryRead(string text, float min, float max, bool integer, Func<float, string> display, out float value, out string message)
        {
            value = 0f;
            message = null;
            if (!NumberMath.TryParse(text, out double parsed, out string unit) || !NumberMath.UnitMatches(unit, SafeDisplay(display, (float)parsed)))
            {
                message = $"\"{ModFields.TMPEscape(text?.Trim())}\" isn't a number";
                return false;
            }
            double clamped = NumberMath.Clamp(parsed, min, max, integer);
            if (parsed < min || parsed > max)
                message = $"Kept to {SafeDisplay(display, (float)clamped)} (from {Grouped(min)} to {Grouped(max)})";
            value = (float)clamped;
            return true;
        }

        private static string SafeDisplay(Func<float, string> display, float v)
        {
            try { return display?.Invoke(v) ?? ModFields.FormatNumber(v); }
            catch { return ModFields.FormatNumber(v); }
        }

        // 1000000 -> "1,000,000" (what the player may type back, too).
        private static string Grouped(float v) => v.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The typing notice for a field from <paramref name="min"/> to <paramref name="max"/>.</summary>
        internal static string TypingHint(float min, float max) =>
            $"Type a number from {Grouped(min)} to {Grouped(max)}, then Enter (Esc cancels)";

        internal static string ControllerHint(bool shoulders) =>
            "Left/right to change it (hold to speed up)" + (shoulders ? "; the shoulder buttons step x10" : "");

        private static ModField Build(ModPage page, string label, Func<float> get, Action<float> set, float min, float max, float step,
            Func<float, string> format, Func<string> tooltip, Action onClear, bool integer)
        {
            if (max < min) (min, max) = (max, min);
            if (step <= 0f) step = integer ? 1f : 0.1f;
            ModField field = null;
            float Read()
            {
                try { return get(); }
                catch (Exception e)
                {
                    Log.Core.Warn($"Number \"{label}\": {e.Message}");
                    return min;
                }
            }
            string Show(float v) => SafeDisplay(format, v);
            void Apply(float v)
            {
                v = (float)NumberMath.Clamp(v, min, max, integer);
                if (v != Read())
                {
                    try { set(v); }
                    catch (Exception e) { Log.Core.Caught($"Number \"{label}\": set failed", e); }
                }
                field?.Refresh();
            }
            void Move(int steps) => Apply((float)NumberMath.Step(Read(), step, steps, min, max, integer));
            // Left/right and the arrows: accelerate while held, x10 with Shift.
            void Step(int sign)
            {
                if (ModTextInput.Active) return;
                int speed = NumberMath.Acceleration(NumberRepeat.Next(label, sign), min, max, step);
                Move(sign * speed * (ShiftHeld ? 10 : 1));
            }
            void Type()
            {
                if (UsingController)
                {
                    Notify.Show(ControllerHint(shoulders: true), NotifyKind.Info, 3f, key: "modnumber");
                    return;
                }
                ModTextInput.Begin(field, ModFields.FormatNumber(Read()), text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        if (onClear == null) return;
                        try { onClear(); }
                        catch (Exception e) { Log.Core.Caught($"Number \"{label}\": clear failed", e); }
                        field.Refresh();
                        return;
                    }
                    bool ok = TryRead(text, min, max, integer, Show, out float value, out string message);
                    if (message != null) Notify.Show(message, NotifyKind.Warning, 3f, key: "modnumber");
                    if (ok) Apply(value);
                }, 24, hint: TypingHint(min, max));
            }

            var row = page.Row(label, "", tooltip, Type);
            field = new ModField(row, label, () => Show(Read()), arrows: false, page.FieldIndent);
            row.Button.onEmptyNavigate.AddListener(direction =>
            {
                if (direction == ThronefallUIElement.NavigationDirection.Right) Step(1);
                else if (direction == ThronefallUIElement.NavigationDirection.Left) Step(-1);
            });
            row.AddArrows(() => Step(-1), () => Step(1));
            var keys = row.Button.gameObject.AddComponent<NumberKeys>();
            keys.Selected = () => field.Selected;
            keys.Jump = toMax => Apply(toMax ? max : min);
            keys.Big = sign => Move(sign * 10);
            field.Refresh();
            return field;
        }
    }

    /// <summary>Counts how long a direction has been held on one field: the game's menus repeat a held direction about
    /// 10 times a second, so presses closer together than 0.25 s are one hold.</summary>
    internal static class NumberRepeat
    {
        private static string lastKey;
        private static float last = -10f;
        private static int streak;

        /// <summary>Repeats so far of this hold (0 on a fresh press). Keyed by field, so a page rebuilt between
        /// repeats keeps its speed.</summary>
        public static int Next(string field, int sign)
        {
            string key = field + (sign > 0 ? "+" : "-");
            float now = Time.unscaledTime;
            streak = key == lastKey && now - last < 0.25f ? streak + 1 : 0;
            lastKey = key;
            last = now;
            return streak;
        }
    }

    /// <summary>Keys a selected number field handles itself: Home/End jump to min/max; the controller's tab buttons
    /// (shoulders) step x10 (only where the screen doesn't use them for tabs: <see cref="Big"/> null there).</summary>
    internal sealed class NumberKeys : MonoBehaviour
    {
        public Func<bool> Selected;
        public Action<bool> Jump;   // true: to max
        public Action<int> Big;     // +1 / -1

        private UIFrame frame;

        private bool Active()
        {
            if (Selected == null || ModTextInput.BlocksInput) return false;
            if (frame == null) frame = GetComponentInParent<UIFrame>(true);
            return frame != null && UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame == frame && Selected();
        }

        private void Update()
        {
            if (!Active()) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.Home)) Jump?.Invoke(false);
                else if (Input.GetKeyDown(KeyCode.End)) Jump?.Invoke(true);
                else if (Big != null && Plugin.Input != null)
                {
                    if (Plugin.Input.GetButtonRepeating("NextTab")) Big(1);
                    else if (Plugin.Input.GetButtonRepeating("PrevTab")) Big(-1);
                }
            }
            catch (Exception e) { Log.Core.Caught("Number field key failed", e); }
        }

        /// <summary>Dev-command hook: min, max, next (shoulder right) or prev.</summary>
        internal void DebugKey(string key)
        {
            switch (key)
            {
                case "min": Jump?.Invoke(false); break;
                case "max": Jump?.Invoke(true); break;
                case "next": (Big ?? throw new InvalidOperationException("this field has no shoulder steps"))(1); break;
                case "prev": (Big ?? throw new InvalidOperationException("this field has no shoulder steps"))(-1); break;
                default: throw new ArgumentException("expected min, max, next or prev");
            }
        }
    }
}
