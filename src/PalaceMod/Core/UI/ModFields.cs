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

        page.Number("Max HP", () => hp, v => hp = v, 1, 9999);                                     // numbers: ModNumber.cs
        page.Stepper("Max HP", () => hp.ToString(), delta => hp += delta, onApply: () => ...);   // left/right steps
        page.Choice("Side", ["Enemy", "Player"], () => side, i => side = i);                       // left/right cycles
        page.Toggle("Elite", () => elite, v => elite = v);
        page.TextInput("Name", () => name, v => name = v);                                         // apply to type

    Left/right on a selected field step it (holding a direction repeats, and speeds up after a moment: the delta passed
    to a stepper grows from 1 to 5 to 25); arrows appear beside the value while it's selected. A field with onApply
    runs it on click / apply instead of stepping. ModField.Refresh re-reads the value; SetMarker flags the label.

    Text entry (TextInput, or ModTextInput.Begin for any field) types into the value with the keyboard: Enter or a
    click confirms, Escape cancels. The caret moves with the arrows (Alt for words, Cmd for the line), Home/End, and
    Backspace/Delete edit at the caret; Cmd/Ctrl+V pastes, Cmd/Ctrl+A selects all, Cmd/Ctrl+C copies. A multiline
    field takes Shift+Enter for a new line. While typing, the game's menu input (and Escape) is held back so keys don't
    also navigate or close the menu, and mod keybinds are inert.

    A text field's value wraps instead of being cut off: when it doesn't fit on one line beside the label, it's shown
    as a left-aligned block beside the label and the row grows to fit it.
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
        private readonly TextMeshProUGUI valueText;
        private bool wrap, stacked;

        private readonly float indent;

        internal ModField(ModRow row, string label, Func<string> value, bool arrows, float indent = ValueIndent)
        {
            this.indent = indent;
            Row = row;
            Label = label;
            this.value = value;
            this.arrows = arrows;
            row.Button.onSelectionStateChange.AddListener(Refresh);
            // Values stay on one line to the right of the label, cut with an ellipsis rather than wrapping over it.
            valueText = row.Button.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
            if (valueText != null)
            {
                valueText.enableWordWrapping = false;
                valueText.overflowMode = TextOverflowModes.Ellipsis;
                valueText.margin = new Vector4(indent, 0f, 0f, 0f);
            }
        }

        /// <summary>Space kept for the label at the left of a field's row.</summary>
        public const float ValueIndent = 340f;

        private const float RowHeight = 46f, WrapPad = 7f, FallbackWidth = 860f;

        /// <summary>Puts the value under the label at the row's full width (narrow panels, long text such as a
        /// description), wrapping onto as many lines as it needs.</summary>
        public ModField Stack()
        {
            stacked = true;
            if (!wrap) EnableWrap(); else Fit();
            return this;
        }

        /// <summary>Lets a long value wrap onto more lines (the row grows) instead of being cut with an ellipsis.</summary>
        internal void EnableWrap()
        {
            wrap = true;
            if (valueText != null) valueText.gameObject.AddComponent<RefitOnResize>().Field = this;
            Fit();
        }

        // The row's real width is only known after layout: fit again whenever it changes.
        private sealed class RefitOnResize : MonoBehaviour
        {
            public ModField Field;
            private float width = -1f;

            private void OnRectTransformDimensionsChange()
            {
                float w = ((RectTransform)transform).rect.width;
                if (Field == null || Mathf.Approximately(w, width)) return;
                width = w;
                Field.Fit();
            }
        }

        // One line, right-aligned, when the value fits beside the label; otherwise a wrapped block and a taller row.
        private void Fit()
        {
            if (!wrap || valueText == null || Button == null) return;
            var label = Button.GetComponent<TextMeshProUGUI>();
            var element = Button.GetComponent<LayoutElement>();
            float width = valueText.rectTransform.rect.width;
            if (width <= 1f) width = FallbackWidth;
            string shown = valueText.text ?? "";
            // TMP's preferred values include the text's margins (the label's indent on the left).
            bool multi = stacked || shown.IndexOf('\n') >= 0 || valueText.GetPreferredValues(shown, 100000f, 0f).x > width;
            float height = RowHeight;
            float left = stacked ? (label != null ? label.margin.x : 0f) : indent;
            float top = stacked ? WrapPad + (label != null ? label.fontSize * 1.15f : 30f) : WrapPad;
            if (multi)
            {
                valueText.enableWordWrapping = true;
                valueText.overflowMode = TextOverflowModes.Overflow;
                valueText.alignment = TextAlignmentOptions.TopLeft;
                valueText.margin = new Vector4(left, top, 0f, WrapPad);
                // Wrapped at the value's real width (rect minus margins), margins included.
                // Before the first layout there's no width yet: estimate; RefitOnResize fits it again.
                float textHeight;
                if (valueText.rectTransform.rect.width > 1f)
                {
                    valueText.ForceMeshUpdate();
                    textHeight = valueText.preferredHeight;
                }
                else textHeight = valueText.GetPreferredValues(shown, FallbackWidth - left, 0f).y + top + WrapPad;
                height = Mathf.Max(RowHeight, textHeight + 2f);
            }
            else
            {
                valueText.enableWordWrapping = false;
                valueText.overflowMode = TextOverflowModes.Ellipsis;
                valueText.alignment = TextAlignmentOptions.MidlineRight;
                valueText.margin = new Vector4(indent, 0f, 0f, 0f);
            }
            if (label != null)
            {
                label.alignment = multi ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
                label.margin = new Vector4(label.margin.x, multi ? WrapPad : 0f, label.margin.z, 0f);
            }
            if (element != null && !Mathf.Approximately(element.preferredHeight, height))
            {
                element.preferredHeight = height;
                if (Button.transform.parent is RectTransform parent) LayoutRebuilder.MarkLayoutForRebuild(parent);
            }
        }

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
                catch (Exception e) { v = "?"; Log.Core.Warn($"Field \"{Label}\": {e.Message}"); }
                if (arrows && Selected) v = $"<color=#{ModStyle.Hex(ModStyle.Dim)}>‹</color>  {v}  <color=#{ModStyle.Hex(ModStyle.Dim)}>›</color>";
            }
            Row.SetValue(v);
            Fit();
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
            field = new ModField(row, label, value, arrows: true, page.FieldIndent);
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
                catch (Exception e) { Log.Core.Caught($"Field \"{label}\": step failed", e); }
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

        /// <summary>A text field: apply starts typing into it; Enter confirms (calls <paramref name="onCommit"/>). A long
        /// value wraps and the row grows to show all of it. <paramref name="multiline"/> lets Shift+Enter add line breaks.</summary>
        public static ModField TextInput(this ModPage page, string label, Func<string> value, Action<string> onCommit,
            Func<string> tooltip = null, int maxLength = 64, Func<string> display = null, bool multiline = false)
        {
            ModField field = null;
            var row = page.Row(label, "", tooltip, () => ModTextInput.Begin(field, value() ?? "", onCommit, maxLength, multiline));
            field = new ModField(row, label, display ?? (() => Placeholder(value())), arrows: false, page.FieldIndent);
            field.EnableWrap();
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
        // What's being typed into: a ModField, or any text a widget shows (a settings slider's number).
        private sealed class Target
        {
            public string Label;
            public Func<bool> Alive;
            public Action<string> Show; // the text to show while typing; null puts the widget's own value back
        }

        private static Target editing;
        private static Action<string> commit;
        private static readonly StringBuilder text = new();
        private static bool replaceAll, multiline;
        private static int caret, maxLength, startFrame, endFrame = -1;

        // Held-key repeat for the keys handled here (arrows, Backspace, Delete): first repeat after a pause, then fast.
        private static KeyCode heldKey = KeyCode.None;
        private static float nextRepeat;
        private const float RepeatDelay = 0.45f, RepeatRate = 0.035f;

        private static readonly KeyCode[] EditKeys =
        [
            KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Home, KeyCode.End,
            KeyCode.Backspace, KeyCode.Delete,
        ];

        /// <summary>Whether the player is typing into a field right now.</summary>
        public static bool Active => editing != null;

        /// <summary>The text typed so far (null when not typing), for live checks such as an expression's error line.</summary>
        public static string CurrentText => editing != null ? text.ToString() : null;

        /// <summary>The ModField being typed into (null when typing into something else, or not typing).</summary>
        public static ModField CurrentField { get; private set; }

        /// <summary>True while typing and on the frame typing ended, when game menu input is held back.</summary>
        internal static bool BlocksInput => Active || endFrame == Time.frameCount;

        /// <summary>Starts typing into <paramref name="target"/>, starting from <paramref name="initial"/> (selected, so the
        /// first key replaces it). <paramref name="onCommit"/> gets the text when the player presses Enter.
        /// <paramref name="allowNewlines"/> lets Shift+Enter (and paste) add line breaks. <paramref name="hint"/> replaces
        /// the "Type, then Enter..." notice.</summary>
        public static void Begin(ModField target, string initial, Action<string> onCommit, int maxLength = 64, bool allowNewlines = false, string hint = null)
        {
            if (target == null) return;
            Begin(target.Label, () => target.Button != null && target.Button.gameObject.activeInHierarchy, shown =>
            {
                target.Override = shown;
                target.Refresh();
            }, initial, onCommit, maxLength, allowNewlines, hint);
            CurrentField = target;
        }

        /// <summary>Starts typing into any text: <paramref name="show"/> gets the text to display while typing (with the
        /// caret), then null when typing ends, to put the widget's own value back. Typing is dropped once
        /// <paramref name="alive"/> turns false (the widget was hidden or destroyed).</summary>
        public static void Begin(string label, Func<bool> alive, Action<string> show, string initial, Action<string> onCommit,
            int maxLength = 64, bool allowNewlines = false, string hint = null)
        {
            if (alive == null || show == null) return;
            if (Active) End(confirm: true);
            CurrentField = null;
            editing = new Target { Label = label, Alive = alive, Show = show };
            commit = onCommit;
            multiline = allowNewlines;
            ModTextInput.maxLength = Math.Max(1, maxLength);
            text.Clear().Append(initial ?? "");
            if (text.Length > ModTextInput.maxLength) text.Length = ModTextInput.maxLength;
            caret = text.Length;
            replaceAll = text.Length > 0;
            heldKey = KeyCode.None;
            startFrame = Time.frameCount;
            Notify.Show(hint ?? (multiline ? "Type, Shift+Enter for a new line, Enter to confirm, Esc to cancel" : "Type, then Enter to confirm or Esc to cancel"),
                NotifyKind.Info, 3f, key: "modtextinput");
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
            CurrentField = null;
            heldKey = KeyCode.None;
            endFrame = Time.frameCount;
            // Back to the widget's own value before committing, so the commit's own refresh shows the new value.
            bool alive = target.Alive();
            if (alive) target.Show(null);
            if (confirm)
            {
                try { action?.Invoke(result); }
                catch (Exception e) { Log.Core.Caught($"Field \"{target.Label}\": commit failed", e); }
            }
            if (alive && target.Alive()) target.Show(null);
        }

        /// <summary>Dev-command hook: types <paramref name="s"/> into the active editing and confirms.</summary>
        internal static void Debug(string s)
        {
            if (!Active) return;
            text.Clear().Append(s);
            caret = text.Length;
            replaceAll = false;
            End(confirm: true);
        }

        /// <summary>Dev-command hook: one key or text, handled like the keyboard. Keys: left, right, up, down, home,
        /// end, backspace, delete, enter, shift+enter, esc, selectall, alt+left/right/backspace, cmd+left/right;
        /// "paste:..." pastes (\n for a line break); anything else is typed.</summary>
        internal static void DebugKey(string key)
        {
            if (!Active) throw new InvalidOperationException("no field is being edited");
            switch (key)
            {
                case "left": Key(KeyCode.LeftArrow, false, false); break;
                case "right": Key(KeyCode.RightArrow, false, false); break;
                case "up": Key(KeyCode.UpArrow, false, false); break;
                case "down": Key(KeyCode.DownArrow, false, false); break;
                case "home": Key(KeyCode.Home, false, false); break;
                case "end": Key(KeyCode.End, false, false); break;
                case "backspace": Key(KeyCode.Backspace, false, false); break;
                case "alt+backspace": Key(KeyCode.Backspace, true, false); break;
                case "delete": Key(KeyCode.Delete, false, false); break;
                case "alt+left": Key(KeyCode.LeftArrow, true, false); break;
                case "alt+right": Key(KeyCode.RightArrow, true, false); break;
                case "cmd+left": Key(KeyCode.LeftArrow, false, true); break;
                case "cmd+right": Key(KeyCode.RightArrow, false, true); break;
                case "enter": Return(false); return;
                case "shift+enter": Return(true); break;
                case "esc": End(confirm: false); return;
                case "selectall": replaceAll = text.Length > 0; break;
                default:
                    if (key.StartsWith("paste:")) Paste(key.Substring(6).Replace("\\n", "\n"));
                    else Insert(key);
                    break;
            }
            Redraw();
        }

        /// <summary>Dev-command hook: the text being typed, the caret shown as ‸ and line breaks as ⏎.</summary>
        internal static string DebugState => !Active ? "not editing"
            : (replaceAll ? "[all selected] " : "") + text.ToString().Insert(caret, "‸").Replace("\n", "⏎");

        internal static void Tick()
        {
            if (!Active) return;
            // The editing's row was destroyed (page rebuilt, menu closed): drop the edit.
            if (!editing.Alive()) { editing = null; commit = null; endFrame = Time.frameCount; return; }
            if (Time.frameCount == startFrame) return; // the key that started typing
            if (Input.GetKeyDown(KeyCode.Escape)) { End(confirm: false); return; }
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) { End(confirm: true); return; }
            bool command = Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool changed = false;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (!Return(shift || alt)) return;
                changed = true;
            }
            else if (command && Input.GetKeyDown(KeyCode.V))
            {
                Paste(GUIUtility.systemCopyBuffer ?? "");
                changed = true;
            }
            else if (command && Input.GetKeyDown(KeyCode.A))
            {
                replaceAll = text.Length > 0;
                changed = true;
            }
            else if (command && Input.GetKeyDown(KeyCode.C))
            {
                GUIUtility.systemCopyBuffer = text.ToString();
                Notify.Show("Copied", NotifyKind.Info, 1.5f, key: "modtextinput");
            }
            else
            {
                changed |= EditKeysHeld(alt, command);
                if (!command)
                {
                    foreach (char c in Input.inputString)
                    {
                        // Enter, Backspace and Delete are handled as keys above. macOS also sends the arrow, Home/End
                        // and function keys as private-use characters (U+F700-U+F8FF); they're never text.
                        if (char.IsControl(c) || (c >= '' && c <= '')) continue;
                        Insert(c.ToString());
                        changed = true;
                    }
                }
            }
            if (changed || Time.frameCount % 15 == 0) Redraw();
        }

        // Arrows, Home/End, Backspace and Delete, repeating while held.
        private static bool EditKeysHeld(bool alt, bool command)
        {
            float now = Time.unscaledTime;
            bool acted = false;
            foreach (var k in EditKeys)
            {
                if (!Input.GetKeyDown(k)) continue;
                heldKey = k;
                nextRepeat = now + RepeatDelay;
                Key(k, alt, command);
                acted = true;
            }
            if (acted || heldKey == KeyCode.None) return acted;
            if (!Input.GetKey(heldKey)) { heldKey = KeyCode.None; return false; }
            if (now < nextRepeat) return false;
            nextRepeat = now + RepeatRate;
            Key(heldKey, alt, command);
            return true;
        }

        /// <summary>Enter: a new line (multiline fields, with Shift or Alt) or confirm. False when typing ended.</summary>
        private static bool Return(bool newline)
        {
            if (newline && multiline) { Insert("\n"); return true; }
            End(confirm: true);
            return false;
        }

        private static void Key(KeyCode key, bool alt, bool command)
        {
            // With everything selected, a move collapses the selection to that end; Backspace/Delete clear it.
            if (replaceAll)
            {
                replaceAll = false;
                switch (key)
                {
                    case KeyCode.Backspace or KeyCode.Delete: text.Clear(); caret = 0; return;
                    case KeyCode.LeftArrow or KeyCode.UpArrow or KeyCode.Home: caret = 0; return;
                    default: caret = text.Length; return;
                }
            }
            switch (key)
            {
                case KeyCode.LeftArrow: caret = command ? LineStart(caret) : alt ? WordLeft(caret) : Math.Max(0, caret - 1); break;
                case KeyCode.RightArrow: caret = command ? LineEnd(caret) : alt ? WordRight(caret) : Math.Min(text.Length, caret + 1); break;
                case KeyCode.Home: caret = command ? 0 : LineStart(caret); break;
                case KeyCode.End: caret = command ? text.Length : LineEnd(caret); break;
                case KeyCode.UpArrow: caret = command ? 0 : VerticalMove(-1); break;
                case KeyCode.DownArrow: caret = command ? text.Length : VerticalMove(1); break;
                case KeyCode.Backspace:
                {
                    int from = command ? LineStart(caret) : alt ? WordLeft(caret) : Math.Max(0, caret - 1);
                    if (from == caret && caret > 0) from = caret - 1; // Cmd+Backspace at a line start joins the lines
                    text.Remove(from, caret - from);
                    caret = from;
                    break;
                }
                case KeyCode.Delete:
                {
                    int to = alt ? WordRight(caret) : Math.Min(text.Length, caret + 1);
                    text.Remove(caret, to - caret);
                    break;
                }
            }
        }

        private static int LineStart(int i)
        {
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        private static int LineEnd(int i)
        {
            while (i < text.Length && text[i] != '\n') i++;
            return i;
        }

        private static int WordLeft(int i)
        {
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
            return i;
        }

        private static int WordRight(int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        // Up/down move between typed lines, keeping the column; on the first/last line they go to its start/end.
        private static int VerticalMove(int direction)
        {
            int start = LineStart(caret), column = caret - start;
            if (direction < 0)
            {
                if (start == 0) return 0;
                int previous = LineStart(start - 1);
                return Math.Min(previous + column, start - 1);
            }
            int end = LineEnd(caret);
            if (end >= text.Length) return text.Length;
            return Math.Min(end + 1 + column, LineEnd(end + 1));
        }

        private static void Paste(string s)
        {
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
            if (!multiline) s = s.Replace('\n', ' ');
            Insert(s);
        }

        private static void Insert(string s)
        {
            if (replaceAll) { text.Clear(); caret = 0; }
            replaceAll = false;
            int room = maxLength - text.Length;
            if (room <= 0)
            {
                Notify.Show($"At most {maxLength} characters", NotifyKind.Warning, 2f, key: "modtextinput");
                return;
            }
            if (s.Length > room) s = s.Substring(0, room);
            text.Insert(caret, s);
            caret += s.Length;
        }

        private static void Redraw()
        {
            if (editing == null) return;
            bool on = (int)(Time.unscaledTime * 2f) % 2 == 0;
            string accent = ModStyle.Hex(ModStyle.Accent);
            string all = text.ToString();
            // A thin bar that takes almost no room, so moving or blinking it barely reflows the text.
            string bar = $"<color=#{accent}>{(on ? "" : "<alpha=#00>")}<space=-0.1em>|<space=-0.1em></color><alpha=#FF>";
            editing.Show(replaceAll
                ? $"<mark=#{accent}55>{ModFields.TMPEscape(all)}</mark>{bar}"
                : ModFields.TMPEscape(all.Substring(0, caret)) + bar + ModFields.TMPEscape(all.Substring(caret)));
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

        // Settings switches tabs on its own keys (NextTab/PrevTab), which would hide a field being typed into.
        [HarmonyPatch(typeof(SettingsUIHelper), "Update")]
        private class SettingsUIHelper_Update_Patch
        {
            private static bool Prefix() => !ModTextInput.BlocksInput;
        }
    }
}
