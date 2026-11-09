using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/*
    The control actions the core provides itself (docs/builder-engine.md, "Actions", Logic group). `if`, `repeat`,
    `forEach`, `choose`, `wait` and `stop` are interpreter frames (Actions.cs); these are ordinary handlers.
*/
namespace PalaceMod.Builder.Logic
{
    internal static class CoreActions
    {
        private static bool registered;

        public static void EnsureRegistered()
        {
            if (registered) return;
            registered = true;

            LogicActions.Register("signal", Signal);
            LogicActions.Register("setVar", (ctx, p) => SetVar(ctx, p, add: false), ActionFlags.PerTarget);
            LogicActions.Register("addVar", (ctx, p) => SetVar(ctx, p, add: true), ActionFlags.PerTarget);
            LogicActions.Register("listAdd", ListAdd, ActionFlags.PerTarget);
            LogicActions.Register("listRemove", ListRemove, ActionFlags.PerTarget);
            LogicActions.Register("listClear", (ctx, p) => ListOp(ctx, p, l => l.Clear()), ActionFlags.PerTarget);
            LogicActions.Register("addLabel", (ctx, p) => p.Target.Labels.Add(Label(p)), ActionFlags.PerTarget);
            LogicActions.Register("removeLabel", (ctx, p) => p.Target.Labels.Remove(Label(p)), ActionFlags.PerTarget);
            LogicActions.Register("enableRule", (ctx, p) => ctx.World.SetRuleEnabled(p.Target, RuleId(p), true), ActionFlags.PerTarget);
            LogicActions.Register("disableRule", (ctx, p) => ctx.World.SetRuleEnabled(p.Target, RuleId(p), false), ActionFlags.PerTarget);
            LogicActions.Register("enable", (ctx, p) => ctx.World.SetEnabled(p.Target, true, ctx.Self), ActionFlags.PerTarget);
            LogicActions.Register("disable", (ctx, p) => ctx.World.SetEnabled(p.Target, false, ctx.Self), ActionFlags.PerTarget);
            LogicActions.Register("toggle", (ctx, p) => ctx.World.SetEnabled(p.Target, !p.Target.Enabled, ctx.Self), ActionFlags.PerTarget);
        }

        private static string Literal(ActionParams p, string name)
        {
            var t = p.Raw(name);
            if (t == null || t.Type != JTokenType.String) throw new LogicError($"`{name}` must be a name");
            return (string)t;
        }

        private static string Label(ActionParams p) => p.Text("label") ?? throw new LogicError("`label` is missing");
        private static string RuleId(ActionParams p) => Literal(p, "rule");

        private static void Signal(LogicContext ctx, ActionParams p)
        {
            string name = p.Text("name") ?? throw new LogicError("signal: `name` is missing");
            double value = 0;
            if (p.Has("value"))
            {
                var v = p.Get("value");
                value = v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num : 0;
            }
            IReadOnlyList<LogicEntity> to;
            var raw = p.Raw("to");
            if (raw == null || raw.Type == JTokenType.Null) to = ctx.Self != null ? [ctx.Self] : null;
            else if (raw.Type == JTokenType.String && (string)raw == "all") to = null;
            else to = p.Entities("to");
            double delay = p.Number("delay", 0);
            if (delay > 0) ctx.World.SignalLater(Math.Max(0.05, delay), name, to, value, ctx.Self, ctx.Depth + 1);
            else ctx.World.Signal(name, to, value, ctx.Self, ctx.Depth + 1);
        }

        private static void SetVar(LogicContext ctx, ActionParams p, bool add)
        {
            string name = Literal(p, "name");
            var target = name.StartsWith("$") ? ctx.World.Level : p.Target;
            var v = p.Get("value");
            if (add)
            {
                var cur = ctx.World.GetVar(target, name);
                v = Value.Number(Values.ToNumber(cur, name) + (p.Has("value") ? Values.ToNumber(v, "value") : 1));
            }
            ctx.World.SetVar(target, name, v, ctx.Depth + 1);
        }

        private static void ListOp(LogicContext ctx, ActionParams p, Action<List<Value>> op)
        {
            string name = Literal(p, "name");
            var target = name.StartsWith("$") ? ctx.World.Level : p.Target;
            var cur = ctx.World.GetVar(target, name);
            var list = cur.Kind == ValueKind.List ? new List<Value>(cur.AsList) : [];
            op(list);
            ctx.World.SetVar(target, name, Value.List(list), ctx.Depth + 1);
        }

        private static void ListAdd(LogicContext ctx, ActionParams p)
        {
            var v = p.Get("value");
            int cap = ctx.World.Caps.Set;
            ListOp(ctx, p, l =>
            {
                l.Add(v);
                while (l.Count > cap) l.RemoveAt(0);
            });
        }

        private static void ListRemove(LogicContext ctx, ActionParams p)
        {
            if (p.Has("index"))
            {
                int i = (int)p.Number("index");
                ListOp(ctx, p, l => { if (i >= 0 && i < l.Count) l.RemoveAt(i); });
                return;
            }
            var v = p.Get("value");
            ListOp(ctx, p, l =>
            {
                for (int i = 0; i < l.Count; i++)
                    if (Values.Same(l[i], v)) { l.RemoveAt(i); return; }
            });
        }
    }

    public sealed partial class LogicWorld
    {
        private readonly List<(double at, long seq, string name, IReadOnlyList<LogicEntity> to, double value, LogicEntity sender, int depth)> laterSignals = [];

        internal void SignalLater(double delay, string name, IReadOnlyList<LogicEntity> to, double value, LogicEntity sender, int depth) =>
            laterSignals.Add((Time + delay, ++seq, name, to, value, sender, depth));

        private void RunLaterSignals()
        {
            if (laterSignals.Count == 0) return;
            var due = laterSignals.FindAll(s => s.at <= Time + 1e-9);
            if (due.Count == 0) return;
            laterSignals.RemoveAll(s => s.at <= Time + 1e-9);
            due.Sort((a, b) => a.at != b.at ? a.at.CompareTo(b.at) : a.seq.CompareTo(b.seq));
            foreach (var s in due) Signal(s.name, s.to, s.value, s.sender, s.depth);
        }

        /// <summary>Switch an entity on or off (rules, and through the `enable`/`disable` inputs L2 registers, its
        /// components, solid cut, hittability and visibility).</summary>
        public void SetEnabled(LogicEntity e, bool on, LogicEntity by)
        {
            if (e == null) return;
            e.Enabled = on;
            if (LogicActions.Inputs.TryGetValue(on ? "enable" : "disable", out var input))
                input(e, new LogicEvent { Name = "signal", Text = on ? "enable" : "disable", Other = by, Subject = e });
        }
    }
}
