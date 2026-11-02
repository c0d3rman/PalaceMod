using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Editable fields for ModFrame pages, built on ModPage.Row so they look and navigate like every other row:

        page.Stepper("Max HP", () => hp.ToString(), delta => hp += delta, onApply: () => ...);   // left/right steps
        page.Choice("Side", ["Enemy", "Player"], () => side, i => side = i);                       // left/right cycles
        page.Toggle("Elite", () => elite, v => elite = v);
        page.TextInput("Name", () => name, v => name = v);                                         // apply to type

    Left/right on a selected field step it (holding a direction repeats, and speeds up after a moment: the delta passed
    to a stepper grows from 1 to 5 to 25); arrows appear beside the value while it's selected. A field with onApply
    runs it on click / apply instead of stepping. ModField.Refresh re-reads the value; SetMarker flags the label.

    Text entry (TextInput, or ModTextInput.Begin for any field) types into the value with the keyboard: Enter or a
    click confirms, Escape cancels, Cmd/Ctrl+V pastes. While typing, the game's menu input (and Escape) is held back so
    keys don't also navigate or close the menu, and mod keybinds are inert.
*/
namespace PalaceMod
{
    public enum ModFieldMarker { None, Warning, Error }

    /// <summary>An editable row on a ModPage.</summary>
    public sealed class ModField
    {
        public ModRow Row { get; }
        public TFUITextButton Button => Row.Button;
        public string Label { get; private set; }
        private readonly Func<string> value;
        private readonly bool arrows;
        private ModFieldMarker marker;
        internal string Override;   // shown instead of the value while typing

        internal ModField(ModRow row, string label, Func<string> value, bool arrows)
        {
            Row = row;
            Label = label;
            this.value = value;
            this.arrows = arrows;
            row.Button.onSelectionStateChange.AddListener(Refresh);
            // Values stay on one line to the right of the label, cut with an ellipsis rather than wrapping over it.
            var valueText = row.Button.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
            if (valueText != null)
            {
                valueText.enableWordWrapping = false;
                valueText.overflowMode = TextOverflowModes.Ellipsis;
                valueText.margin = new Vector4(ValueIndent, 0f, 0f, 0f);
            }
        }

        /// <summary>Space kept for the label at the left of a field's row.</summary>
        public const float ValueIndent = 340f;

        public bool Selected => Button != null && Button.CurrentState is ThronefallUIElement.SelectionState.Selected or ThronefallUIElement.SelectionState.FocussedAndSelected;

        /// <summary>Re-reads the value (and label marker) and redraws the row.</summary>
        public void Refresh()
        {
            if (Button == null) return;
            string v;
            if (Override != null) v = Override;
            else
            {
                try { v = value?.Invoke() ?? ""; }
                catch (Exception e) { v = "?"; Plugin.Logger.LogWarning($"Field \"{Label}\": {e.Message}"); }
                if (arrows && Selected) v = $"<color=#{ModStyle.Hex(ModStyle.Dim)}>‹</color>  {v}  <color=#{ModStyle.Hex(ModStyle.Dim)}>›</color>";
            }
            Row.SetValue(v);
        }

        public void SetLabel(string label)
        {
            Label = label;
            ApplyLabel();
        }

        /// <summary>Flags the label with a coloured mark (validation feedback).</summary>
        public void SetMarker(ModFieldMarker newMarker)
        {
            if (marker == newMarker) return;
            marker = newMarker;
            ApplyLabel();
        }

        private void ApplyLabel()
        {
            string mark = marker switch
            {
                ModFieldMarker.Error => $"  <size=130%><color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>◆</color></size>",
                ModFieldMarker.Warning => $"  <size=130%><color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Warning))}>◆</color></size>",
                _ => "",
            };
            Row.SetLabel(Label + mark);
        }
    }

    public static class ModFields
    {
        /// <summary>A number-like field: left/right call <paramref name="onStep"/> with ±1, growing to ±5 and ±25 while
        /// held. Apply runs <paramref name="onApply"/> (e.g. to type a value) if given.</summary>
        public static ModField Stepper(this ModPage page, string label, Func<string> value, Action<int> onStep,
            Action onApply = null, Func<string> tooltip = null)
        {
            ModField field = null;
            var row = page.Row(label, "", tooltip, onApply == null ? null : () => onApply());
            field = new ModField(row, label, value, arrows: true);
            var repeat = new StepRepeat();
            row.Button.onEmptyNavigate.AddListener(direction =>
            {
                if (ModTextInput.Active) return;
                int sign = direction switch
                {
                    ThronefallUIElement.NavigationDirection.Right => 1,
                    ThronefallUIElement.NavigationDirection.Left => -1,
                    _ => 0,
                };
                if (sign == 0) return;
                try { onStep(sign * repeat.Next()); }
                catch (Exception e) { Plugin.Logger.LogError($"Field \"{label}\": step failed: {e}"); }
                field.Refresh();
            });
            field.Refresh();
            return field;
        }

        /// <summary>Cycles through <paramref name="options"/> with left/right. Apply runs <paramref name="onApply"/> (e.g.
        /// to open a picker) or else steps forward.</summary>
        public static ModField Choice(this ModPage page, string label, IReadOnlyList<string> options, Func<int> index, Action<int> onChange,
            Action onApply = null, Func<string> tooltip = null)
        {
            void Step(int delta)
            {
                if (options.Count == 0) return;
                int i = index();
                int next = ((i < 0 ? (delta > 0 ? -1 : 0) : i) + Math.Sign(delta) + options.Count) % options.Count;
                onChange(next);
            }
            ModField field = null;
            field = page.Stepper(label, () =>
            {
                int i = index();
                return i >= 0 && i < options.Count ? options[i] : "-";
            }, Step, onApply ?? (() => { Step(1); field.Refresh(); }), tooltip);
            return field;
        }

        /// <summary>A yes/no field; apply or left/right flips it.</summary>
        public static ModField Toggle(this ModPage page, string label, Func<bool> value, Action<bool> onChange, Func<string> tooltip = null)
        {
            ModField field = null;
            field = page.Stepper(label, () => value() ? "Yes" : "No", _ => onChange(!value()), () => { onChange(!value()); field.Refresh(); }, tooltip);
            return field;
        }

        /// <summary>A text field: apply starts typing into it; Enter confirms (calls <paramref name="onCommit"/>).</summary>
        public static ModField TextInput(this ModPage page, string label, Func<string> value, Action<string> onCommit,
            Func<string> tooltip = null, int maxLength = 64, Func<string> display = null)
        {
            ModField field = null;
            var row = page.Row(label, "", tooltip, () => ModTextInput.Begin(field, value() ?? "", onCommit, maxLength));
            field = new ModField(row, label, display ?? (() => Placeholder(value())), arrows: false);
            field.Refresh();
            return field;
        }

        private static string Placeholder(string text) =>
            string.IsNullOrEmpty(text) ? $"<color=#{ModStyle.Hex(ModStyle.Dim)}>none</color>" : TMPEscape(text);

        /// <summary>Escapes TMP rich-text tags in player text.</summary>
        public static string TMPEscape(string text) => text?.Replace("<", "<​") ?? "";

        /// <summary>Parses a number typed by the player ("1.5", "1,5", " 20 ").</summary>
        public static bool TryParseNumber(string text, out float value)
        {
            text = text?.Trim().Replace(',', '.') ?? "";
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>Formats a number compactly: integers without decimals, others with up to two.</summary>
        public static string FormatNumber(float value) =>
            Mathf.Approximately(value, Mathf.Round(value)) ? Mathf.Round(value).ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        // Speeds a held direction up: the frame repeats navigation while a key is held (about 10 per second).
        private sealed class StepRepeat
        {
            private float last = -10f;
            private int streak;

            public int Next()
            {
                float now = Time.unscaledTime;
                streak = now - last < 0.25f ? streak + 1 : 0;
                last = now;
                return streak >= 24 ? 25 : streak >= 8 ? 5 : 1;
            }
        }
    }

    /// <summary>Keyboard text entry into a ModField (see ModFields).</summary>
    public static class ModTextInput
    {
        private static ModField editing;
        private static Action<string> commit;
        private static readonly StringBuilder text = new();
        private static bool replaceAll;
        private static int maxLength, startFrame, endFrame = -1;

        /// <summary>Whether the player is typing into a field right now.</summary>
        public static bool Active => editing != null;

        /// <summary>True while typing and on the frame typing ended, when game menu input is held back.</summary>
        internal static bool BlocksInput => Active || endFrame == Time.frameCount;

        /// <summary>Starts typing into <paramref name="target"/>, starting from <paramref name="initial"/> (selected, so the
        /// first key replaces it). <paramref name="onCommit"/> gets the text when the player presses Enter.</summary>
        public static void Begin(ModField target, string initial, Action<string> onCommit, int maxLength = 64)
        {
            if (target == null) return;
            if (Active) End(confirm: true);
            editing = target;
            commit = onCommit;
            ModTextInput.maxLength = Math.Max(1, maxLength);
            text.Clear().Append(initial ?? "");
            replaceAll = text.Length > 0;
            startFrame = Time.frameCount;
            Notify.Show("Type, then Enter to confirm or Esc to cancel", NotifyKind.Info, 3f, key: "modtextinput");
            Redraw();
        }

        /// <summary>Stops typing; confirm passes the text to the field's commit action.</summary>
        public static void End(bool confirm)
        {
            if (!Active) return;
            var target = editing;
            var action = commit;
            string result = text.ToString();
            editing = null;
            commit = null;
            endFrame = Time.frameCount;
            target.Override = null;
            if (confirm)
            {
                try { action?.Invoke(result); }
                catch (Exception e) { Plugin.Logger.LogError($"Field \"{target.Label}\": commit failed: {e}"); }
            }
            target.Refresh();
        }

        /// <summary>Dev-command hook: types <paramref name="s"/> into the active editing and confirms.</summary>
        internal static void Debug(string s)
        {
            if (!Active) return;
            text.Clear().Append(s);
            replaceAll = false;
            End(confirm: true);
        }

        internal static void Tick()
        {
            if (!Active) return;
            // The editing's row was destroyed (page rebuilt, menu closed): drop the edit.
            if (editing.Button == null || !editing.Button.gameObject.activeInHierarchy) { editing = null; commit = null; endFrame = Time.frameCount; return; }
            if (Time.frameCount == startFrame) return; // the key that started typing
            if (Input.GetKeyDown(KeyCode.Escape)) { End(confirm: false); return; }
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) { End(confirm: true); return; }
            bool command = Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool changed = false;
            if (command && Input.GetKeyDown(KeyCode.V))
            {
                Insert(GUIUtility.systemCopyBuffer?.Replace("\r", "").Replace("\n", " ") ?? "");
                changed = true;
            }
            else if (command && Input.GetKeyDown(KeyCode.A))
            {
                replaceAll = text.Length > 0;
                changed = true;
            }
            else
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\n' || c == '\r') { End(confirm: true); return; }
                    if (c == '\b')
                    {
                        if (replaceAll) text.Clear();
                        else if (text.Length > 0) text.Length--;
                        replaceAll = false;
                    }
                    else if (!char.IsControl(c) && !command) Insert(c.ToString());
                    changed = true;
                }
            }
            if (changed || Time.frameCount % 15 == 0) Redraw();
        }

        private static void Insert(string s)
        {
            if (replaceAll) text.Clear();
            replaceAll = false;
            text.Append(s);
            if (text.Length > maxLength) text.Length = maxLength;
        }

        private static void Redraw()
        {
            if (editing == null) return;
            bool caret = (int)(Time.unscaledTime * 2f) % 2 == 0;
            string body = ModFields.TMPEscape(text.ToString());
            string accent = ModStyle.Hex(ModStyle.Accent);
            if (replaceAll) body = $"<mark=#{accent}55>{body}</mark>";
            editing.Override = $"{body}<color=#{accent}>{(caret ? "|" : "<alpha=#00>|")}</color>";
            editing.Refresh();
        }
    }

    /// <summary>Runs ModTextInput and holds the game's menu input back while the player types.</summary>
    [ManagerOrder(-44)]
    public class ModTextInputManager : BaseManager
    {
        public override void Update() => ModTextInput.Tick();

        [HarmonyPatch(typeof(UIFrame), "Update")]
        private class UIFrame_Update_Patch
        {
            private static bool Prefix() => !ModTextInput.BlocksInput;
        }

        [HarmonyPatch(typeof(UIFrameManager), "Update")]
        private class UIFrameManager_Update_Patch
        {
            private static bool Prefix() => !ModTextInput.BlocksInput;
        }
    }
}
