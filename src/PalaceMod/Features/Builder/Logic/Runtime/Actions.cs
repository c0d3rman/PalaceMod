using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/*
    Actions: compilation (JSON -> CompiledAction with lazily compiled params) and the interpreter.

    An action list runs as a task: a stack of frames (list + index + context). The control actions `if`, `repeat`,
    `forEach`, `choose`, `wait` and `stop` are frames in the interpreter, so `wait` (and the per-frame action budget)
    can suspend a task and resume it later where it was. Every other action is a handler from LogicActions; nested
    lists a handler runs itself (`spawn.then`, `launch.onImpact`) run through LogicContext.Run as tasks of their own.
*/
namespace PalaceMod.Builder.Logic
{
    internal sealed class CompiledAction
    {
        public string Type;
        public string Path;
        public JObject Json;
        public CompiledParams Params;
        // Control actions
        public ActionList Then, Else, Body;
        public List<(Func<LogicContext, Value> weight, ActionList list)> Options;
    }

    internal static class ActionCompiler
    {
        public static readonly HashSet<string> Control = new(StringComparer.Ordinal) { "if", "repeat", "forEach", "choose", "wait", "stop" };

        public static ActionList CompileList(JArray arr, string path)
        {
            if (arr == null) return ActionList.Empty;
            var items = new List<CompiledAction>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] is JObject o) items.Add(Compile(o, $"{path}[{i}]"));
                else throw new LogicError($"{path}[{i}]: an action must be an object");
            return new ActionList(items);
        }

        public static CompiledAction Compile(JObject o, string path)
        {
            string type = (string)o["do"] ?? throw new LogicError($"{path}: the action has no \"do\"");
            Model.Logic.LogicSchema.Actions.TryGetValue(type, out var spec);
            var a = new CompiledAction { Type = type, Path = path, Json = o, Params = new CompiledParams(o, path, spec) };
            switch (type)
            {
                case "if":
                    a.Then = CompileList(o["then"] as JArray, path + ".then");
                    a.Else = CompileList(o["else"] as JArray, path + ".else");
                    break;
                case "repeat":
                case "forEach":
                    // The body is "actions": "do" is already the action's type.
                    a.Body = CompileList(o["actions"] as JArray, path + ".actions");
                    break;
                case "choose":
                    a.Options = [];
                    if (o["options"] is JArray opts)
                        for (int i = 0; i < opts.Count; i++)
                        {
                            if (opts[i] is not JObject opt) continue;
                            var w = opt["weight"];
                            Func<LogicContext, Value> wf = w == null ? _ => Value.Number(1)
                                : w.Type == JTokenType.String ? ExprCompiler.Compile((string)w, $"{path}.options[{i}].weight")
                                : _ => Value.Number((double)w);
                            a.Options.Add((wf, CompileList(opt["do"] as JArray, $"{path}.options[{i}].do")));
                        }
                    break;
            }
            return a;
        }
    }

    /// <summary>Lazily compiled params of one action (shared by every run of it).</summary>
    internal sealed class CompiledParams
    {
        public readonly JObject Json;
        public readonly string Path;
        private readonly Dictionary<string, Func<LogicContext, Value>> exprs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<LogicContext, string>> texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActionList> lists = new(StringComparer.Ordinal);

        /// <summary>The action's schema entry (null for an unknown action): its param types decide how each param's
        /// string is read (Model.Logic.ParamForms, the same rules the validator checks).</summary>
        public readonly Model.Logic.ActionSpec Spec;

        public CompiledParams(JObject json, string path, Model.Logic.ActionSpec spec = null) { Json = json; Path = path; Spec = spec; }

        /// <summary>The param's schema type, or null if the schema doesn't list it.</summary>
        public string TypeOf(string name) => Spec?.Param(name)?.Type;

        /// <summary>How the param's value is read (ParamForms.Classify on its schema type).</summary>
        public Model.Logic.ParamForm Form(string name, out string source) => Model.Logic.ParamForms.Classify(TypeOf(name), name, Json[name], out source);

        public Func<LogicContext, Value> Expr(string name)
        {
            if (exprs.TryGetValue(name, out var f)) return f;
            string path = $"{Path}.{name}";
            switch (Form(name, out string src))
            {
                case Model.Logic.ParamForm.Literal:
                case Model.Logic.ParamForm.Filter:
                {
                    var v = TypeOf(name) == "template" ? Value.Template(src.StartsWith("@@") ? src.Substring(2) : src) : Value.Text(src);
                    f = _ => v;
                    break;
                }
                case Model.Logic.ParamForm.Text:
                {
                    var t = Text(name);
                    f = ctx => Value.Text(t(ctx));
                    break;
                }
                case Model.Logic.ParamForm.Expression:
                    f = ExprCompiler.Compile(src, path);
                    break;
                default:
                    f = FromToken(Json[name], path);
                    break;
            }
            exprs[name] = f;
            return f;
        }

        /// <summary>Compiles every param now and throws the LogicError the first run would (the validator's dry
        /// run, so `check` fails exactly where the game would).</summary>
        public void CompileAll()
        {
            foreach (var prop in Json.Properties())
            {
                if (prop.Name == "do" || prop.Name.StartsWith("_")) continue;
                switch (Form(prop.Name, out _))
                {
                    case Model.Logic.ParamForm.Structured:
                        if (TypeOf(prop.Name) == "actions") List(prop.Name);
                        break;
                    case Model.Logic.ParamForm.Filter:
                        ExprCompiler.CompileFilter((string)prop.Value, $"{Path}.{prop.Name}");
                        break;
                    case Model.Logic.ParamForm.Json:
                    case Model.Logic.ParamForm.None:
                        break;
                    default:
                        Expr(prop.Name);
                        break;
                }
            }
        }

        /// <summary>A JSON param as a value: numbers and bools are literals, strings are expressions (a leading `=` is
        /// allowed), [x,z] / [x,y,z] are positions, null/absent is none.</summary>
        public static Func<LogicContext, Value> FromToken(JToken t, string path)
        {
            switch (t?.Type)
            {
                case null:
                case JTokenType.Null:
                    return _ => Value.None;
                case JTokenType.Integer:
                case JTokenType.Float:
                    { var v = Value.Number((double)t); return _ => v; }
                case JTokenType.Boolean:
                    { var v = Value.Bool((bool)t); return _ => v; }
                case JTokenType.String:
                    {
                        string s = (string)t;
                        if (s.StartsWith("=")) s = s.Substring(1);
                        return ExprCompiler.Compile(s, path);
                    }
                case JTokenType.Array:
                    {
                        var a = (JArray)t;
                        if (a.Count is 2 or 3 && a.All(x => x.Type is JTokenType.Integer or JTokenType.Float))
                        {
                            var v = Value.Position(a.Count == 2 ? new Vec((float)a[0], 0, (float)a[1]) : new Vec((float)a[0], (float)a[1], (float)a[2]));
                            return _ => v;
                        }
                        var items = new List<Func<LogicContext, Value>>();
                        for (int i = 0; i < a.Count; i++) items.Add(FromToken(a[i], $"{path}[{i}]"));
                        return ctx => { var l = new List<Value>(items.Count); foreach (var f in items) l.Add(f(ctx)); return Value.List(l); };
                    }
                default:
                    return _ => Value.None;
            }
        }

        /// <summary>A text param: literal with {expr} interpolation, or `=expr`.</summary>
        public Func<LogicContext, string> Text(string name)
        {
            if (texts.TryGetValue(name, out var f)) return f;
            var t = Json[name];
            var form = Form(name, out string src);
            if (form == Model.Logic.ParamForm.None) f = _ => null;
            else if (form is Model.Logic.ParamForm.Literal or Model.Logic.ParamForm.Filter) f = _ => src;
            else if (form == Model.Logic.ParamForm.Expression && (t.Type != JTokenType.String || TypeOf(name) != null || ((string)t).StartsWith("=")))
            {
                var e = ExprCompiler.Compile(src, $"{Path}.{name}");
                f = ctx => Values.Format(e(ctx));
            }
            else if (t.Type == JTokenType.String) f = ExprCompiler.CompileText((string)t, $"{Path}.{name}");
            else { string lit = t.ToString(Newtonsoft.Json.Formatting.None); f = _ => lit; }
            texts[name] = f;
            return f;
        }

        public ActionList List(string name)
        {
            if (lists.TryGetValue(name, out var l)) return l;
            l = Json[name] is JArray arr ? ActionCompiler.CompileList(arr, $"{Path}.{name}") : null;
            lists[name] = l;
            return l;
        }
    }

    /// <summary>The params of one action run, bound to its context.</summary>
    internal sealed class BoundParams : ActionParams
    {
        private readonly CompiledParams p;
        private readonly LogicContext ctx;

        public BoundParams(CompiledParams p, LogicContext ctx, LogicEntity target) { this.p = p; this.ctx = ctx; Target = target; }

        public override bool Has(string name) => p.Json[name] != null && p.Json[name].Type != JTokenType.Null;
        public override JToken Raw(string name) => p.Json[name];
        public override Value Get(string name) => p.Expr(name)(ctx);

        public override double Number(string name, double fallback = 0)
        {
            if (!Has(name)) return fallback;
            var v = Get(name);
            return v.IsNone ? fallback : Values.ToNumber(v, $"{p.Path}.{name}");
        }

        public override bool Bool(string name, bool fallback = false) => Has(name) ? Values.ToBool(Get(name)) : fallback;

        public override string Text(string name, string fallback = null) => Has(name) ? p.Text(name)(ctx) ?? fallback : fallback;

        public override string Template(string name)
        {
            var t = p.Json[name];
            if (t == null || t.Type != JTokenType.String) return null;
            string s = (string)t;
            if (s.StartsWith("=")) return Get(name).Str;
            return s.StartsWith("@@") ? s.Substring(2) : s;
        }

        public override Vec? Position(string name)
        {
            if (!Has(name)) return null;
            var v = Get(name);
            return Values.ToPosition(v);
        }

        public override LogicEntity Entity(string name)
        {
            if (!Has(name)) return null;
            var v = Get(name);
            return v.Kind switch
            {
                ValueKind.Entity => v.AsEntity,
                ValueKind.Set => v.AsSet.Count > 0 ? v.AsSet[0] : null,
                ValueKind.None => null,
                _ => throw new LogicError($"{p.Path}.{name}: expected an entity, got {v.Kind.ToString().ToLowerInvariant()}"),
            };
        }

        public override IReadOnlyList<LogicEntity> Entities(string name)
        {
            if (!Has(name)) return Array.Empty<LogicEntity>();
            return Values.ToSet(Get(name), ctx.World.Caps.Set, $"{p.Path}.{name}");
        }

        public override ActionList Actions(string name) => p.List(name);
    }

    internal static class EnumerableExt
    {
        public static bool All(this JArray a, Func<JToken, bool> f) { foreach (var x in a) if (!f(x)) return false; return true; }
    }

    // ------------------------------------------------------------------ interpreter

    internal sealed class ActionCursor
    {
        public sealed class Frame
        {
            public ActionList List;
            public int Index;
            public LogicContext Ctx;
            // repeat / forEach
            public ActionList Loop;
            public int Iteration, Times;
            public double Every;
            public IReadOnlyList<LogicEntity> Items;
            public LogicContext Base;            // forEach: the context the loop started in (each member binds from it)
            public string As;                    // forEach: the member's name (`as`), or null
        }

        public readonly Stack<Frame> Frames = new();
        public string Owner, Rule, Path;
        public double ResumeAt;              // on Clock's own time (LogicWorld.LocalTime)
        public LogicEntity Clock;            // whose timeScale the task's waits run on: the rule's owner (self)
        public long Seq;                     // deterministic order for tasks due at the same time
    }

    public sealed partial class LogicWorld
    {
        private readonly List<ActionCursor> waiting = [];      // suspended by `wait` / `repeat.every`
        private readonly List<ActionCursor> carried = [];      // suspended by the per-frame action budget
        private long taskSeq;

        internal void RunList(ActionList actions, LogicContext ctx)
        {
            if (actions == null || actions.Count == 0) return;
            var task = new ActionCursor { Owner = ctx.Self?.Id, Rule = ctx.RuleLabel, Seq = ++taskSeq, Clock = ctx.Self };
            task.Frames.Push(new ActionCursor.Frame { List = actions, Ctx = ctx });
            Continue(task);
        }

        /// <summary>Run a task until it ends, waits, or the frame's action budget runs out.</summary>
        private void Continue(ActionCursor task)
        {
            while (task.Frames.Count > 0)
            {
                var f = task.Frames.Peek();
                if (f.Ctx.Stopped) { task.Frames.Clear(); return; }
                if (f.Index >= f.List.Count)
                {
                    if (f.Loop != null && NextIteration(task, f)) { if (task.ResumeAt > LocalTime(task.Clock)) return; continue; }
                    task.Frames.Pop();
                    continue;
                }
                if (actionsThisFrame >= Caps.ActionsPerFrame)
                {
                    if (!throttledThisFrame) { throttledThisFrame = true; throttled++; TraceAdd(task.Owner, task.Rule, task.Path, "actions", TraceOutcome.Throttled, $"over {Caps.ActionsPerFrame} action steps this frame; the rest runs next frame", 0, null); }
                    carried.Add(task);
                    return;
                }
                var a = f.List.Items[f.Index++];
                actionsThisFrame++;
                actionsTotal++;
                task.Path = a.Path;
                try
                {
                    if (!Step(task, f, a)) return;   // suspended
                }
                catch (Exception e) when (e is LogicError or InvalidCastException or FormatException or ArgumentException or NullReferenceException or KeyNotFoundException)
                {
                    errors++;
                    TraceAdd(task.Owner, task.Rule, a.Path, f.Ctx.Event?.Name, TraceOutcome.Error, e is LogicError ? e.Message : $"{a.Type}: {e.GetType().Name}: {e.Message}", f.Ctx.Event?.Value ?? 0, f.Ctx.Event?.Other?.Id);
                    task.Frames.Clear();
                    return;
                }
            }
        }

        /// <summary>Execute one action. Returns false if the task was suspended.</summary>
        private bool Step(ActionCursor task, ActionCursor.Frame f, CompiledAction a)
        {
            var ctx = f.Ctx;
            var bp = new BoundParams(a.Params, ctx, null);
            switch (a.Type)
            {
                case "stop":
                    task.Frames.Clear();
                    return true;
                case "wait":
                    {
                        double s = Math.Max(0.05, bp.Number("seconds", 0.05));
                        task.ResumeAt = LocalTime(task.Clock) + s;
                        waiting.Add(task);
                        return false;
                    }
                case "if":
                    {
                        bool c = Values.ToBool(a.Params.Expr("cond")(ctx));
                        var list = c ? a.Then : a.Else;
                        if (list.Count > 0) task.Frames.Push(new ActionCursor.Frame { List = list, Ctx = ctx });
                        return true;
                    }
                case "choose":
                    {
                        if (a.Options.Count == 0) return true;
                        double total = 0;
                        var ws = new double[a.Options.Count];
                        for (int i = 0; i < ws.Length; i++) { ws[i] = Math.Max(0, Values.ToNumber(a.Options[i].weight(ctx), a.Path)); total += ws[i]; }
                        if (total <= 0) return true;
                        double r = Random.NextDouble() * total;
                        int pick = ws.Length - 1;
                        for (int i = 0; i < ws.Length; i++) { if (r < ws[i]) { pick = i; break; } r -= ws[i]; }
                        task.Frames.Push(new ActionCursor.Frame { List = a.Options[pick].list, Ctx = ctx });
                        return true;
                    }
                case "repeat":
                    {
                        int times = (int)Math.Min(Caps.Loop, Math.Max(0, Math.Floor(bp.Number("times", 1))));
                        double every = bp.Number("every", 0);
                        if (every > 0) every = Math.Max(0.05, every);
                        if (times == 0 || a.Body.Count == 0) return true;
                        var fr = new ActionCursor.Frame { List = a.Body, Ctx = IterationCtx(ctx, 0), Loop = a.Body, Iteration = 0, Times = times, Every = every };
                        task.Frames.Push(fr);
                        return true;
                    }
                case "forEach":
                    {
                        var items = Values.ToSet(a.Params.Expr("in")(ctx), Caps.Set, a.Path + ".in");
                        int limit = bp.Has("limit") ? (int)Math.Max(0, bp.Number("limit")) : Caps.Set;
                        int n = Math.Min(items.Count, Math.Min(limit, Caps.Set));
                        if (n == 0 || a.Body.Count == 0) return true;
                        string loopVar = a.Json["as"] is JValue { Type: JTokenType.String } asTok ? (string)asTok : null;
                        var fr = new ActionCursor.Frame { List = a.Body, Ctx = MemberCtx(ctx, items[0], loopVar), Loop = a.Body, Iteration = 0, Times = n, Items = items, Base = ctx, As = loopVar };
                        task.Frames.Push(fr);
                        return true;
                    }
            }

            if (!LogicActions.Handlers.TryGetValue(a.Type, out var entry))
                throw new LogicError($"{a.Path}: unknown action \"{a.Type}\" (made with a newer PalaceMod, or not built yet)");

            if ((entry.Flags & ActionFlags.PerTarget) != 0)
            {
                IReadOnlyList<LogicEntity> targets = bp.Has("target") ? bp.Entities("target") : (ctx.Self != null ? [ctx.Self] : Array.Empty<LogicEntity>());
                int n = Math.Min(targets.Count, Caps.Set);
                for (int i = 0; i < n; i++)
                {
                    var t = targets[i];
                    if (t == null) continue;
                    var c = ctx.With(it: t);
                    entry.Handler(c, new BoundParams(a.Params, c, t));
                }
            }
            else entry.Handler(ctx, bp);
            return true;
        }

        /// <summary>A forEach member's context: `it` is the member (`outer` the `it` around the loop), and the loop's
        /// `as` name, if any, holds it too.</summary>
        private static LogicContext MemberCtx(LogicContext loop, LogicEntity member, string name)
        {
            var c = loop.With(it: member);
            if (name != null)
            {
                c.Locals ??= new Dictionary<string, Value>(StringComparer.Ordinal);
                c.Locals[name] = Value.Entity(member);
            }
            return c;
        }

        private static LogicContext IterationCtx(LogicContext ctx, int i)
        {
            var c = ctx.With();
            var ev = ctx.Event;
            c.Event = new LogicEvent { Name = ev?.Name, Subject = ev?.Subject, Other = ev?.Other, Value = i, Pos = ev?.Pos, Text = ev?.Text, Seq = ev?.Seq ?? 0, Depth = ev?.Depth ?? 0 };
            return c;
        }

        /// <summary>A loop frame finished one pass: set up the next, possibly after `every`. False when the loop is done.</summary>
        private bool NextIteration(ActionCursor task, ActionCursor.Frame f)
        {
            f.Iteration++;
            if (f.Iteration >= f.Times) return false;
            f.Index = 0;
            var parentCtx = f.Ctx;
            f.Ctx = f.Items != null ? MemberCtx(f.Base, f.Items[f.Iteration], f.As) : IterationCtx(parentCtx, f.Iteration);
            if (f.Every > 0)
            {
                task.ResumeAt = LocalTime(task.Clock) + f.Every;
                waiting.Add(task);
            }
            return true;
        }

        /// <summary>Resume tasks whose wait is over (in due-time, then creation order), then budget carry-overs.</summary>
        private void ResumeTasks()
        {
            if (carried.Count > 0)
            {
                var c = carried.ToArray();
                carried.Clear();
                foreach (var t in c) Continue(t);
            }
            if (waiting.Count == 0) return;
            List<ActionCursor> due = null;
            for (int i = waiting.Count - 1; i >= 0; i--)
                if (waiting[i].ResumeAt <= LocalTime(waiting[i].Clock) + 1e-9) { (due ??= []).Add(waiting[i]); waiting.RemoveAt(i); }
            if (due == null) return;
            // Due order on the world's clock (each task's own due time plus its clock's lag).
            double At(ActionCursor t) => t.ResumeAt + (t.Clock?.Lag ?? 0);
            due.Sort((a, b) => At(a) != At(b) ? At(a).CompareTo(At(b)) : a.Seq.CompareTo(b.Seq));
            foreach (var t in due) { t.ResumeAt = 0; Continue(t); }
        }

        public int PendingTasks => waiting.Count + carried.Count;
    }
}
