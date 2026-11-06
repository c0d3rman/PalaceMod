using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

namespace PalaceMod.Builder.Editor
{
    /// <summary>
    /// One editor screen (a ModFrame) bound to the open draft. Fields made through the helpers here carry the JSON
    /// path of the value they edit, so validation issues mark the field (a coloured diamond and the message in its
    /// tooltip) and the Problems list can jump to it. Value edits update the open page in place; structural edits
    /// rebuild it (keeping the selection), and pages that aren't showing rebuild when they're shown again.
    /// </summary>
    internal abstract class EditorPage
    {
        public ModFrame Frame { get; }
        protected PackDraft Draft => BuilderEditor.Draft;
        protected DesignPack Pack => Draft?.Pack;

        private readonly List<(ModField Field, string Path)> bound = [];
        private bool stale;

        protected EditorPage(string title)
        {
            Frame = new ModFrame(title);
            Frame.Activated += () =>
            {
                if (!stale) return;
                stale = false;
                Rebuild();
            };
        }

        public bool IsOpen => Frame.IsOpen;

        /// <summary>Wraps a page builder so every build starts with a clean field list.</summary>
        protected Action<ModPage> Page(Action<ModPage> build, bool needsDraft = true) => page =>
        {
            bound.Clear();
            if (needsDraft && Draft == null) { page.Text("No pack is open."); return; }
            build(page);
            UpdateFields();
        };

        public void OnDraftChanged(bool structural)
        {
            if (!Frame.IsOpen) { stale = true; return; }
            if (structural) Rebuild();
            else UpdateFields();
        }

        /// <summary>Rebuilds the open page, keeping the selection (pages with custom selectables override this).</summary>
        public virtual void Rebuild() => Frame.Refresh();

        public void MarkStale() => stale = true;

        protected void UpdateFields()
        {
            foreach (var (field, path) in bound)
            {
                if (field.Button == null) continue;
                field.Refresh();
                if (path != null) field.SetMarker(MarkerFor(path));
            }
            OnFieldsUpdated();
        }

        /// <summary>Redraw custom (non-field) widgets after a value edit.</summary>
        protected virtual void OnFieldsUpdated() { }

        /// <summary>Selects the field whose path best matches an issue path; false if none is on this page.</summary>
        public bool SelectPath(string path)
        {
            var best = bound.Where(b => b.Path != null && b.Field.Button != null && Covers(b.Path, path))
                .OrderByDescending(b => b.Path.Length).FirstOrDefault();
            if (best.Field == null || Frame.Frame == null) return false;
            Frame.Frame.Select(best.Field.Button);
            return true;
        }

        // ---- Validation feedback --------------------------------------------------------------------------------

        protected static bool Covers(string fieldPath, string issuePath) =>
            issuePath == fieldPath || issuePath.StartsWith(fieldPath + ".") || issuePath.StartsWith(fieldPath + "[");

        protected IEnumerable<ValidationIssue> IssuesAt(string path) =>
            path == null || Draft == null ? [] : Draft.Issues.Where(i => Covers(path, i.Path));

        protected ModFieldMarker MarkerFor(string path)
        {
            var issues = IssuesAt(path).ToList();
            return issues.Any(i => i.Severity == Severity.Error) ? ModFieldMarker.Error : issues.Count > 0 ? ModFieldMarker.Warning : ModFieldMarker.None;
        }

        /// <summary>Tooltip text: the help line, then any problems at the path.</summary>
        protected Func<string> Tip(string help, string path) => () =>
        {
            var lines = IssuesAt(path).Select(i =>
                $"<color=#{ModStyle.Hex(ModStyle.KindColor(i.Severity == Severity.Error ? NotifyKind.Error : NotifyKind.Warning))}>{Capital(i.Message)}</color>").ToList();
            if (!string.IsNullOrEmpty(help)) lines.Insert(0, help);
            return string.Join("\n", lines);
        };

        protected static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        protected T Bind<T>(T field, string path) where T : class
        {
            if (field is ModField f) bound.Add((f, path));
            return field;
        }

        /// <summary>Marks a plain row (not a field) with its path's issues.</summary>
        protected ModField Marked(ModPage page, string label, Func<string> value, string help, string path, Action onClick, Sprite icon = null)
        {
            var row = page.Row(label, "", Tip(help, path), onClick, icon);
            var field = new ModField(row, label, value, arrows: false);
            field.Refresh();
            return Bind(field, path);
        }

        // ---- Field helpers ----------------------------------------------------------------------------------------

        protected ModField Number(ModPage page, NumberSpec spec, string path, Func<float?> get, Action<DesignPack, float?> set,
            Func<float?> baseValue = null, bool nullable = true, string label = null)
        {
            label ??= spec.Label;
            string Display(float shown)
            {
                if (get() is float) return spec.Format(shown);
                return baseValue?.Invoke() is float ? Dim(spec.Format(shown) + "  (base)") : Dim("base");
            }
            // The kit's number field: typing, clamping, held-key speed-up, Shift x10, Home/End (Core/UI/ModNumber.cs).
            var field = page.Number(label, () => get() ?? baseValue?.Invoke() ?? spec.Min,
                v => Draft.Edit($"Set {label.ToLowerInvariant()}", p => set(p, spec.Clamp(v)), coalesce: path),
                spec.Min, spec.Max, spec.Step, Display,
                Tip(spec.Help + (nullable ? "\nLeft/right to change, apply to type a value (empty resets to the base unit's)." : "\nLeft/right to change, apply to type a value."), path),
                nullable ? () => Draft.Edit($"Reset {label.ToLowerInvariant()}", p => set(p, null)) : null);
            return Bind(field, path);
        }

        protected ModField Text(ModPage page, string label, string path, Func<string> get, Action<DesignPack, string> set, string help, int maxLength = 64,
            bool isId = false, bool multiline = false)
        {
            if (multiline) help += "\nApply to edit. Shift+Enter starts a new line.";
            var field = page.TextInput(label, get, text =>
            {
                text = text.Trim();
                if (text == (get() ?? "")) return;
                if (isId && !Ids.IsValid(text))
                {
                    string suggestion = Ids.FromName(text);
                    Notify.Show($"Ids use lowercase a-z, 0-9, '.', '_' and '-'" + (Ids.IsValid(suggestion) && suggestion != get() ? $"; using \"{suggestion}\"" : ""), NotifyKind.Warning, 4f, key: "builder-edit");
                    if (!Ids.IsValid(suggestion) || suggestion == get()) return;
                    text = suggestion;
                }
                Draft.Edit($"Set {label.ToLowerInvariant()}", p => set(p, text.Length == 0 ? null : text));
            }, Tip(help, path), maxLength, multiline: multiline);
            return Bind(field, path);
        }

        protected ModField Choice<TValue>(ModPage page, string label, string path, IReadOnlyList<(TValue Value, string Display)> options,
            Func<TValue> get, Action<DesignPack, TValue> set, string help, bool structural = false, Action picker = null, Func<string> display = null)
        {
            var names = options.Select(o => o.Display).ToList();
            int Index() => options.ToList().FindIndex(o => Equals(o.Value, get()));
            ModField field;
            if (display != null)
            {
                field = page.Stepper(label, display, delta => Step(delta), picker ?? (() => Step(1)), Tip(help, path));
            }
            else
            {
                field = page.Choice(label, names, Index, i => Draft.Edit($"Set {label.ToLowerInvariant()}", p => set(p, options[i].Value), structural, coalesce: path), picker, Tip(help, path));
            }
            return Bind(field, path);

            void Step(int delta)
            {
                if (options.Count == 0) return;
                int i = Index();
                int next = ((i < 0 ? (delta > 0 ? -1 : 0) : i) + Math.Sign(delta) + options.Count) % options.Count;
                Draft.Edit($"Set {label.ToLowerInvariant()}", p => set(p, options[next].Value), structural, coalesce: path);
            }
        }

        protected ModField Toggle(ModPage page, string label, string path, Func<bool> get, Action<DesignPack, bool> set, string help) =>
            Bind(page.Toggle(label, get, v => Draft.Edit($"{(v ? "Turn on" : "Turn off")} {label.ToLowerInvariant()}", p => set(p, v)), Tip(help, path)), path);

        /// <summary>The loadout Test play starts with (Test play skips the loadout screen): last used on the map, or one
        /// weapon alone. Not saved in the pack.</summary>
        protected void TestLoadoutRow(ModPage page, Func<string> host)
        {
            const string last = "";
            List<string> Options() => [last, .. TestLoadout.Weapons.Select(w => w.name)];
            string Current() => TestLoadout.Choice ?? last;
            string Display()
            {
                if (TestLoadout.IsFixed(host())) return Dim("fixed by the map");
                var weapon = TestLoadout.Weapons.FirstOrDefault(w => w.name == TestLoadout.Choice);
                if (weapon != null) return $"{TestLoadout.Name(weapon)} only";
                return $"{(TestLoadout.HasLastUsed(host()) ? "Last used" : "Default")}: {TestLoadout.Describe(TestLoadout.For(host()))}";
            }
            void Set(string choice) => TestLoadout.Choice = string.IsNullOrEmpty(choice) ? null : choice;
            ModField field = null;
            field = page.Stepper("Test loadout", Display, delta =>
            {
                if (TestLoadout.IsFixed(host())) return;
                var options = Options();
                int i = Math.Max(0, options.IndexOf(Current()));
                Set(options[((i + Math.Sign(delta)) % options.Count + options.Count) % options.Count]);
            }, () =>
            {
                if (TestLoadout.IsFixed(host())) return;
                var items = new List<Picker.Item>
                {
                    new() { Id = last, Label = "Last used on this map", Value = TestLoadout.Describe(LastUsedPreview()),
                        Tooltip = "The loadout you last started this map with from its menu.\n" + TestLoadout.List(LastUsedPreview()) },
                };
                items.AddRange(TestLoadout.Weapons.Select(w => new Picker.Item { Id = w.name, Label = $"{TestLoadout.Name(w)} only", Icon = w.icon, Tooltip = "Just this weapon, no perks." }));
                Picker.One("Test Loadout", items, Current(), id => { Set(id); field?.Refresh(); });
            }, () => "What the king brings to Test play, which skips the loadout screen: the loadout you last started this map with, or one weapon alone.\n"
                + TestLoadout.List(TestLoadout.For(host())));

            List<Equippable> LastUsedPreview()
            {
                string saved = TestLoadout.Choice;
                TestLoadout.Choice = null;
                try { return TestLoadout.For(host()); }
                finally { TestLoadout.Choice = saved; }
            }
        }

        /// <summary>A destructive action: the first press arms it ("Press again to ..."), a second within 3 s runs it.</summary>
        protected void Danger(ModPage page, string label, string help, Action action)
        {
            float armedAt = -10f;
            ModRow row = null;
            row = page.Row(label, null, help, () =>
            {
                if (Time.unscaledTime - armedAt > 3f)
                {
                    armedAt = Time.unscaledTime;
                    row.SetLabel($"{label}: press again to confirm");
                    return;
                }
                armedAt = -10f;
                row.SetLabel(label);
                action();
            });
        }

        protected static string Dim(string text) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{text}</color>";
        protected static string Accent(string text) => $"<color=#{ModStyle.Hex(ModStyle.Accent)}>{text}</color>";
        protected static string Escape(string text) => ModFields.TMPEscape(text);
    }

    /// <summary>A full-screen list to pick one item (or toggle several) from, e.g. a base unit or a spawn line.</summary>
    internal static class Picker
    {
        public sealed class Item
        {
            public string Id, Label, Value, Tooltip;
            public Sprite Icon;
            /// <summary>A section heading instead of a pickable row.</summary>
            public bool Header;
        }

        private static ModFrame frame;
        private static List<Item> items = [];
        private static string current;
        private static Action<string> onPick;
        private static Func<string, bool> isOn;
        private static string hint;

        /// <summary>Pick one; <paramref name="pick"/> runs with the chosen id and the picker closes.</summary>
        public static void One(string title, IEnumerable<Item> choices, string selected, Action<string> pick, string help = null)
        {
            items = choices.ToList();
            current = selected;
            onPick = id => { frame.Close(); pick(id); };
            isOn = null;
            hint = help;
            Show(title);
        }

        /// <summary>Toggle several; <paramref name="toggle"/> runs per click and the list stays open.</summary>
        public static void Many(string title, IEnumerable<Item> choices, Func<string, bool> on, Action<string> toggle, string help = null)
        {
            items = choices.ToList();
            current = null;
            isOn = on;
            onPick = id => { toggle(id); frame.Refresh(); };
            hint = help;
            Show(title);
        }

        public static bool IsOpen => frame?.IsOpen == true;

        public static void Close() => frame?.Close();

        private static void Show(string title)
        {
            if (frame == null)
            {
                frame = new ModFrame(title).SetContent(Build);
            }
            frame.SetTitle(title);
            if (frame.IsOpen) frame.Refresh();
            else frame.Open();
            // Start on the current choice rather than the top of a long list.
            if (current != null && rows.TryGetValue(current, out var row) && row != null) frame.Frame?.Select(row);
        }

        private static readonly Dictionary<string, TFUITextButton> rows = [];

        private static void Build(ModPage page)
        {
            rows.Clear();
            if (!string.IsNullOrEmpty(hint)) page.Text(hint);
            foreach (var item in items)
            {
                if (item.Header) { page.Header(item.Label); continue; }
                string id = item.Id;
                bool selected = isOn != null ? isOn(id) : id == current;
                string value = item.Value ?? "";
                if (selected) value = (value.Length > 0 ? value + "   " : "") + $"<color=#{ModStyle.Hex(ModStyle.Accent)}>◆</color>";
                var row = page.Row(item.Label, value, item.Tooltip, () => onPick(id), item.Icon);
                rows[id] = row.Button;
            }
            if (items.Count == 0) page.Text("Nothing to choose from.");
        }
    }
}
