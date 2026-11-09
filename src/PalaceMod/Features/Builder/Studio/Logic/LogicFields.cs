using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

/*
    Typed fields for rule cards: numbers that also take a formula, expressions checked as they're typed, and rows that
    open a picker (events, actions, entities, filters, templates, variables, signals, statuses) in the rule editor.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class LogicFields
    {
        public static readonly Color ErrorColor = new(0.88f, 0.38f, 0.31f);
        public static readonly Color WarnColor = new(0.9f, 0.72f, 0.3f);
        public static readonly Color WhenColor = new(0.55f, 0.78f, 0.95f);
        public static readonly Color IfColor = new(0.95f, 0.78f, 0.45f);
        public static readonly Color ThenColor = new(0.55f, 0.88f, 0.6f);

        public static string Esc(string s) => ModFields.TMPEscape(s ?? "");
        public static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";
        public static string Tint(string s, Color c) => $"<color=#{ModStyle.Hex(c)}>{s}</color>";

        /// <summary>The error lines under a value ("col 9: unknown variable …").</summary>
        public static string IssueText(IReadOnlyList<ExprIssue> issues)
        {
            if (issues == null || issues.Count == 0) return "";
            return "\n<size=78%>" + string.Join("\n", issues.Take(2).Select(i => Tint("⚠ " + Esc(i.ToString()), i.Warning ? WarnColor : ErrorColor))) + "</size>";
        }

        // ---- Raw values ---------------------------------------------------------------------------------------------

        public static string Raw(JToken t) => t == null || t.Type == JTokenType.Null ? "" : t.Type is JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);

        /// <summary>A typed entry as JSON for a param: numbers stay numbers, "yes"/"no" for bools, else the text (a formula
        /// for non-text kinds).</summary>
        public static JToken Parse(string typed, ParamUi p)
        {
            typed = typed?.Trim() ?? "";
            if (typed.Length == 0) return null;
            if (p.Kind == ParamKind.Json || ((typed.StartsWith("{") || typed.StartsWith("[")) && p.Kind is ParamKind.Labels or ParamKind.Json))
            {
                try { return JToken.Parse(typed); } catch { return new JValue(typed); }
            }
            if (p.Kind == ParamKind.Labels) return new JArray(typed.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            if (p.Kind == ParamKind.Number && double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d == Math.Floor(d) && Math.Abs(d) < 1e9 ? new JValue((long)d) : new JValue(d);
            if (p.Kind == ParamKind.Bool && typed is "yes" or "true" or "no" or "false") return new JValue(typed is "yes" or "true");
            return new JValue(typed);
        }

        /// <summary>Checks a typed value for its kind (an empty list when fine).</summary>
        public static List<ExprIssue> Check(string typed, ParamUi p, ExprScope scope)
        {
            if (string.IsNullOrWhiteSpace(typed)) return p.Required ? [new ExprIssue { Col = 1, Message = "needed" }] : [];
            switch (p.Kind)
            {
                case ParamKind.Filter: return ExprLint.CheckFilter(typed, scope);
                case ParamKind.Number:
                    if (double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return [];
                    return ExprLint.Check(typed, scope);
                case ParamKind.Condition:
                case ParamKind.Expr:
                case ParamKind.Entity:
                case ParamKind.Target:
                case ParamKind.Position:
                    if (typed == "all" && p.Name == "to") return [];
                    return ExprLint.Check(typed, scope);
                case ParamKind.Template:
                case ParamKind.Text:
                    return typed.StartsWith("=") ? ExprLint.Check(typed.Substring(1), scope) : [];
                case ParamKind.Json:
                    try { JToken.Parse(typed); return []; } catch (Exception e) { return [new ExprIssue { Col = 1, Message = e.Message.Split('.')[0] }]; }
                default: return [];
            }
        }

        // ---- Fields -------------------------------------------------------------------------------------------------

        /// <summary>A param's field: the right editor for its kind. get/set read and write the param's JSON.</summary>
        public static void Param(ModPage page, string label, ParamUi p, Func<JToken> get, Action<JToken, string> set, ExprScope scope, Action openPicker = null)
        {
            string tip = p.Desc ?? DefaultTip(p);
            switch (p.Kind)
            {
                case ParamKind.Bool:
                    page.Toggle(label, () => get()?.Type == JTokenType.Boolean ? (bool)get() : p.Default == "true", v => set(new JValue(v), $"Set {p.Label}"), () => tip);
                    return;
                case ParamKind.Choice:
                {
                    var options = p.Options.ToList();
                    var shown = options.Select(o => LogicCatalog.Humanize(o)).ToList();
                    if (!p.Required) { options.Insert(0, null); shown.Insert(0, p.Default != null ? $"Default ({LogicCatalog.Humanize(p.Default)})" : "Default"); }
                    page.Choice(label, shown, () => options.IndexOf((string)get()) is int i && i >= 0 ? i : 0,
                        i => set(options[i] == null ? null : new JValue(options[i]), $"Set {p.Label}"), tooltip: () => tip);
                    return;
                }
                case ParamKind.Number:
                    NumberField(page, label, p, get, set, scope, tip);
                    return;
            }
            if (openPicker != null)
            {
                // A value chosen from a picker (apply), with the formula typed in by Shift-click or from the picker.
                var field = page.Stepper(label, () => Show(get(), p, scope), _ => { }, openPicker, () => tip + "\nClick to choose.");
                return;
            }
            ExprField(page, label, p, get, set, scope, tip);
        }

        private static string DefaultTip(ParamUi p) => p.Kind switch
        {
            ParamKind.Condition => "A yes/no formula, e.g. self.hpPct < 0.5 and isNight.",
            ParamKind.Expr => "A formula: numbers, roles (self, other), $variables, functions.",
            ParamKind.Text => "Text. {…} puts a formula's value in, e.g. Souls {$souls}.",
            ParamKind.Filter => "What it matches, e.g. foe and Tower and not #lit.",
            ParamKind.Json => "JSON, as in the level file.",
            ParamKind.Labels => "Labels, separated by commas.",
            _ => "",
        };

        /// <summary>A value as the field shows it, with its problems under it.</summary>
        public static string Show(JToken v, ParamUi p, ExprScope scope)
        {
            string raw = Raw(v);
            string shown = v == null ? Dim(p.Default != null ? RuleText.Value(null, p, scope?.Event) : p.Required ? "choose…" : "—") : Esc(RuleText.Value(v, p, scope?.Event));
            return shown + IssueText(v == null ? (p.Required && p.Default == null ? Check("", p, scope) : []) : Check(raw, p, scope));
        }

        /// <summary>A number: left/right step it; click types a number or a formula.</summary>
        public static ModField NumberField(ModPage page, string label, ParamUi p, Func<JToken> get, Action<JToken, string> set, ExprScope scope, string tip)
        {
            ModField field = null;
            double step = p.Name is "pct" or "hpPct" or "homing" or "arc" or "mult" or "chance" ? 0.05 : p.Name is "seconds" or "for" or "cooldown" or "every" or "interval" or "delay" or "wait" ? 0.5 : 1;
            field = page.Stepper(label, () => Show(get(), p, scope), delta =>
            {
                var v = get();
                double cur = v?.Type is JTokenType.Integer or JTokenType.Float ? (double)v : double.TryParse(p.Default ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                if (v != null && v.Type == JTokenType.String) { Notify.Show("A formula: click to edit it", NotifyKind.Info, 2f, "logic-field"); return; }
                double next = Math.Round(cur + delta * step, 4);
                set(next == Math.Floor(next) ? new JValue((long)next) : new JValue(next), $"Set {p.Label}");
            }, () => ModTextInput.Begin(field, Raw(get()), typed => Commit(typed, p, set, scope), 512, hint: "A number or a formula (self.hp * 2, $souls + 1); Enter to confirm"),
                () => tip + "\nArrows step it; click to type a number or a formula.");
            field.EnableWrap();
            Live(field, p, scope);
            return field;
        }

        /// <summary>A formula or text field, checked as it's typed.</summary>
        public static ModField ExprField(ModPage page, string label, ParamUi p, Func<JToken> get, Action<JToken, string> set, ExprScope scope, string tip)
        {
            var field = page.TextInput(label, () => Raw(get()), typed => Commit(typed, p, set, scope), () => tip, 512, () => Show(get(), p, scope));
            Live(field, p, scope);
            return field;
        }

        private static void Commit(string typed, ParamUi p, Action<JToken, string> set, ExprScope scope)
        {
            var issues = Check(typed, p, scope).Where(i => !i.Warning).ToList();
            if (issues.Count > 0) Notify.Show($"{p.Label}: {issues[0]}", NotifyKind.Warning, 4f, "logic-field");
            // Kept even with problems (the field shows them), so a half-written formula isn't lost.
            set(Parse(typed, p), $"Set {p.Label}");
        }

        /// <summary>Shows the problems of what's being typed under the field, live.</summary>
        public static void Live(ModField field, ParamUi p, ExprScope scope)
        {
            var c = field.Button.gameObject.AddComponent<LiveCheck>();
            c.Field = field;
            c.Check = text => Check(text, p, scope);
        }

        private sealed class LiveCheck : MonoBehaviour
        {
            public ModField Field;
            public Func<string, List<ExprIssue>> Check;
            private string lastText, lastSuffix = "";
            private TextMeshProUGUI value;

            private void LateUpdate()
            {
                if (Field == null || ModTextInput.CurrentField != Field) { lastText = null; return; }
                value ??= Field.Button.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
                if (value == null) return;
                string typed = ModTextInput.CurrentText ?? "";
                if (typed != lastText)
                {
                    lastText = typed;
                    List<ExprIssue> issues;
                    try { issues = Check(typed); } catch { issues = []; }
                    lastSuffix = issues.Count == 0 ? (typed.Length > 0 ? "\n<size=78%>" + Tint("✓", new Color(0.55f, 0.88f, 0.6f)) + "</size>" : "") : IssueText(issues);
                }
                string shown = (Field.Override ?? "") + lastSuffix;
                if (value.text != shown) value.text = shown;
            }
        }

        // ---- Small rows --------------------------------------------------------------------------------------------

        /// <summary>A heading row in a section colour ("When", "If", "Then").</summary>
        public static void Section(ModPage page, string text, Color color)
        {
            var h = page.Header(Tint(text, color));
            h.fontSize = 30f;
        }

        /// <summary>A compact clickable row.</summary>
        public static ModRow Action(ModPage page, string label, Action click, string tip = null, string value = null)
        {
            var row = page.Row(label, value, tip, click);
            StudioDock.Compact(row, false, 40f, page.Size.x);
            return row;
        }

        /// <summary>A strip of small buttons on one line.</summary>
        public static void Buttons(ModPage page, params (string label, Action run, string tip)[] buttons)
        {
            var list = buttons.Where(b => b.run != null).ToList();
            if (list.Count == 0) return;
            var area = page.Custom("Buttons", 46f);
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(page.Content);
            float w = (page.Size.x - (list.Count - 1) * 6f) / list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var (label, run, tip) = list[i];
                var t = ModTile.Create(area, "Button " + label, new Vector2(w, 40f), null, label, 19f);
                t.Label.alignment = TextAlignmentOptions.Center;
                t.Place(new Vector2(i * (w + 6f), -3f));
                t.Applied += run;
                if (tip != null) ModTooltip.Attach(t, label, () => tip);
            }
        }
    }
}
