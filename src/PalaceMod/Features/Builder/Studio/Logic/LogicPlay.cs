using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PalaceMod.Builder.Logic;
using TMPro;
using UnityEngine;

/*
    Play mode's Logic panel (docs/builder-engine.md, "Play mode"): live level variables (editable), the trace (the last
    100 rule firings: when, whose rule, which event, what came of it, in words), throttles and errors in red, the
    budget, and "Send signal". In the world, a pip flashes over a thing whenever one of its rules fires.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class LogicPlay
    {
        private const int Keep = 100;
        private static readonly List<TraceEntry> trace = [];
        private static readonly Dictionary<string, int> fired = new(StringComparer.Ordinal);
        public static bool Pips = true;
        private static string signalName = "", signalTo = "";
        private static float nextRefresh;
        private static int seen;

        /// <summary>How many times a rule fired this play session (for the cards' "fired 3×").</summary>
        public static int? FiredCount(LogicOwner owner, int index)
        {
            if (owner == null) return null;
            string id = owner.Kind == OwnerKind.Attach || owner.Kind == OwnerKind.Place ? owner.Id : owner.Kind == OwnerKind.Level ? "level" : null;
            if (id == null) return null;
            return fired.TryGetValue($"{id}|rules[{index}]", out int n) ? n : null;
        }

        public static void OnTraced(TraceEntry e)
        {
            if (e == null) return;
            trace.Add(e);
            if (trace.Count > Keep) trace.RemoveRange(0, trace.Count - Keep);
            if (e.Outcome == TraceOutcome.Fired)
            {
                string key = $"{e.Owner}|{e.Rule}";
                fired[key] = fired.TryGetValue(key, out int n) ? n + 1 : 1;
                if (Pips) Pip(e);
            }
        }

        /// <summary>A new play session: the counts start over.</summary>
        public static void Reset()
        {
            trace.Clear();
            fired.Clear();
            seen = 0;
        }

        public static void Tick()
        {
            if (Studio.Mode != StudioMode.Play) return;
            TickPips();
            if (Time.unscaledTime < nextRefresh || trace.Count == seen && Time.unscaledTime < nextRefresh + 1.5f) return;
            nextRefresh = Time.unscaledTime + 0.5f;
            seen = trace.Count;
            Studio.RefreshPanels();
        }

        // ---- Panel -------------------------------------------------------------------------------------------------

        public static void Build(ModPage page)
        {
            page.FieldIndent = Math.Min(page.FieldIndent, 150f);
            var world = LogicWorld.Current;
            StudioDock.SmallHeader(page, "Logic");
            if (world == null) { page.Text(LogicFields.Dim("Not running (this level has no behaviour, or the engine is off).")); return; }
            Vars(page, world);
            Send(page);
            Trace(page);
            Entities(page, world);
        }

        private static void Vars(ModPage page, LogicWorld world)
        {
            var vars = world.Level.Vars;
            if (vars.Count == 0) return;
            StudioDock.SmallHeader(page, "Variables");
            foreach (var kv in vars.OrderBy(k => k.Key, StringComparer.Ordinal).ToList())
            {
                string name = kv.Key;
                page.TextInput("$" + name, () => Values.Format(world.Level.Vars.TryGetValue(name, out var v) ? v : Value.None), typed =>
                {
                    if (!world.Level.Vars.TryGetValue(name, out var old)) return;
                    var next = Parse(typed, old);
                    if (next.IsNone) { Notify.Show($"${name} is a {old.Kind}; that isn't one", NotifyKind.Info, 2f, "logic"); return; }
                    world.Level.Vars[name] = next;
                }, () => "Live: type a new value to try it.", 80);
            }
        }

        private static Value Parse(string typed, Value like)
        {
            typed = typed?.Trim() ?? "";
            switch (like.Kind)
            {
                case ValueKind.Number: return double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Value.Number(d) : Value.None;
                case ValueKind.Bool: return typed is "true" or "yes" ? Value.Bool(true) : typed is "false" or "no" ? Value.Bool(false) : Value.None;
                case ValueKind.Text: return Value.Text(typed.Trim('\''));
                default: return Value.None;
            }
        }

        private static void Send(ModPage page)
        {
            StudioDock.SmallHeader(page, "Send a signal");
            page.TextInput("Signal", () => signalName, v => signalName = v?.Trim() ?? "", () => "Its name (open, add, spawn…).", 40);
            page.TextInput("To", () => signalTo, v => signalTo = v?.Trim() ?? "", () => "A name (@gate1 or gate1); empty: everyone.", 40,
                () => string.IsNullOrEmpty(signalTo) ? LogicFields.Dim("everyone") : LogicFields.Esc(signalTo));
            LogicFields.Action(page, "Send", () =>
            {
                var world = LogicWorld.Current;
                if (world == null || string.IsNullOrEmpty(signalName)) return;
                IReadOnlyList<LogicEntity> to = null;
                if (!string.IsNullOrEmpty(signalTo))
                {
                    var e = world.FindNamed(signalTo.TrimStart('@'));
                    if (e == null) { Notify.Show($"Nothing is named {signalTo}", NotifyKind.Info, 2f, "logic"); return; }
                    to = [e];
                }
                LogicEvents.Signal(signalName, to);
                Notify.Show($"Sent {signalName}", NotifyKind.Info, 1.5f, "logic");
            });
        }

        private static void Trace(ModPage page)
        {
            StudioDock.SmallHeader(page, "What fired");
            int bad = trace.Count(t => t.Outcome is TraceOutcome.Error or TraceOutcome.Throttled or TraceOutcome.Dropped or TraceOutcome.Capped);
            if (bad > 0) page.Text(LogicFields.Tint($"{bad} throttled, capped or failed", LogicFields.ErrorColor));
            if (trace.Count == 0) { page.Text(LogicFields.Dim("Nothing yet.")); return; }
            foreach (var e in Enumerable.Reverse(trace).Take(40))
            {
                var entry = e;
                var (text, detail) = Describe(e);
                var row = StudioDock.DetailRow(page, text, detail, false, null, () => Jump(entry), () => $"{entry.Owner} · {entry.Rule} · {entry.Event}\n{entry.Path}\n{entry.Message}");
            }
        }

        /// <summary>A trace line in words: "12.4 s  Tower 3: hit → fired", with why/values on the second line.</summary>
        public static (string Text, string Detail) Describe(TraceEntry e)
        {
            var owner = LogicOwner.FromId(e.Owner);
            string who = owner?.Title ?? e.Owner;
            string rule = e.Rule;
            if (owner != null && e.Rule?.StartsWith("rules[") == true && int.TryParse(e.Rule.Substring(6).TrimEnd(']'), out int i) && owner.Rules()?[i] is Newtonsoft.Json.Linq.JObject r)
                rule = RuleText.Summary(r, owner.Kind);
            string outcome = e.Outcome switch
            {
                TraceOutcome.Fired => LogicFields.Tint("fired", LogicFields.ThenColor),
                TraceOutcome.ConditionFalse => LogicFields.Dim("if was false"),
                TraceOutcome.Throttled => LogicFields.Tint("throttled", LogicFields.ErrorColor),
                TraceOutcome.Dropped => LogicFields.Tint("dropped", LogicFields.ErrorColor),
                TraceOutcome.Capped => LogicFields.Tint("hit a limit", LogicFields.ErrorColor),
                _ => LogicFields.Tint("error", LogicFields.ErrorColor),
            };
            string text = $"{e.Time:0.0} s  {LogicFields.Esc(who)}: {LogicFields.Esc(e.Event)} → {outcome}";
            var details = new List<string> { rule };
            if (e.Value != 0) details.Add("value " + Values.FormatNumber(e.Value));
            if (!string.IsNullOrEmpty(e.Other)) details.Add("other " + (LogicOwner.FromId(e.Other)?.Title ?? e.Other));
            if (!string.IsNullOrEmpty(e.Message)) details.Add(e.Message);
            return (text, LogicFields.Esc(string.Join(" · ", details.Where(s => !string.IsNullOrEmpty(s)))));
        }

        private static void Jump(TraceEntry e)
        {
            var owner = LogicOwner.FromId(e.Owner);
            if (owner?.StudioKey == null) return;
            Notify.Show($"{owner.Title}: back in the Studio (F5) to edit this rule", NotifyKind.Info, 2.5f, "logic");
        }

        private static void Entities(ModPage page, LogicWorld world)
        {
            int n = world.Entities.Count(x => x.Spawned && x.Exists);
            StudioDock.SmallHeader(page, "Alive");
            StudioDock.Compact(page.Row("Spawned things", n.ToString(), "Things rules made that are still around."), false, 60f, page.Size.x);
        }

        // ---- Pips --------------------------------------------------------------------------------------------------

        private sealed class PipState { public TextMeshPro Text; public float Until; public Vector3 Pos; }
        private static readonly List<PipState> pips = [];
        private static GameObject pipRoot;

        private static void Pip(TraceEntry e)
        {
            var entity = LogicWorld.Current?.Find(e.Owner);
            if (entity == null || entity.Kind == "level") return;
            var p = entity.Position;
            var pos = new Vector3(p.X, p.Y + 3f, p.Z);
            if (pipRoot == null) pipRoot = new GameObject("PalaceMod logic pips");
            var state = pips.FirstOrDefault(s => s.Text != null && !s.Text.gameObject.activeSelf);
            if (state == null)
            {
                var go = new GameObject("pip");
                go.transform.SetParent(pipRoot.transform, false);
                var t = go.AddComponent<TextMeshPro>();
                t.font = ModStyle.BoldFont;
                t.alignment = TextAlignmentOptions.Center;
                t.fontSize = 8f;
                t.text = "!";
                state = new PipState { Text = t };
                pips.Add(state);
            }
            state.Pos = pos;
            state.Until = Time.unscaledTime + 0.7f;
            state.Text.color = Color.HSVToRGB(Wires.Hue(e.Event), 0.5f, 1f);
            state.Text.gameObject.SetActive(true);
        }

        private static void TickPips()
        {
            var cam = Camera.main;
            foreach (var s in pips)
            {
                if (s.Text == null || !s.Text.gameObject.activeSelf) continue;
                float left = s.Until - Time.unscaledTime;
                if (left <= 0f) { s.Text.gameObject.SetActive(false); continue; }
                s.Text.transform.position = s.Pos + Vector3.up * (0.7f - left) * 2f;
                if (cam != null) s.Text.transform.rotation = Quaternion.LookRotation(s.Text.transform.position - cam.transform.position);
                var c = s.Text.color;
                c.a = Mathf.Clamp01(left / 0.35f);
                s.Text.color = c;
            }
        }

        public static void HidePips()
        {
            if (pipRoot != null) UnityEngine.Object.Destroy(pipRoot);
            pipRoot = null;
            pips.Clear();
        }
    }
}
