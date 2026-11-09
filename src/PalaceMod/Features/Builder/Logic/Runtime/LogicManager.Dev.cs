using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

/*
    `logic` dev command (also reachable through the agent bridge): inspect and drive the running world.
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicManager
    {
        private const string Usage =
            "logic status | vars [entity] | trace [n] | budget | entities [filter] | fire <signal> [to-selector] [value] | " +
            "set <var|entity.var> <expr> | eval <expr> | do <action json> | king <1-3> | snapshot   the behaviour engine";

        private void RegisterDevCommands()
        {
            DevCommands.Register("logic", Usage, args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
                string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "";
                var w = World;
                if (w == null)
                {
                    if (sub == "status") return BuilderServices.Sessions.Current == null ? "no logic world: not in a Builder session" : "no logic world yet (Studio editing, or the level is loading)";
                    throw new InvalidOperationException("no logic world (not in a Builder session's play)");
                }
                var ctx = new LogicContext { World = w, Self = w.Level };
                switch (sub)
                {
                    case "status":
                        {
                            var b = w.Budget();
                            return $"time {w.Time:0.0}s frame {w.Frame}, {w.Entities.Count()} entities, {w.Entities.Sum(e => e.Rules.Count) + w.Level.Rules.Count} rule instances, " +
                                $"{w.FiringsTotal} firings, {w.ActionsTotal} action steps, pending tasks {w.PendingTasks}, queued {b.QueuedEvents}; " +
                                $"throttled {b.Throttled}, dropped {b.Dropped}, capped spawns {b.CappedSpawns}, errors {b.Errors}, compile problems {w.CompileErrors.Count}" +
                                (w.CompileErrors.Count > 0 ? "\n  " + string.Join("\n  ", w.CompileErrors.Take(10)) : "");
                        }
                    case "vars":
                        {
                            var e = rest.Length == 0 ? w.Level : w.Evaluate(rest).AsEntity ?? w.Find(rest) ?? throw new ArgumentException($"no entity \"{rest}\"");
                            if (e.Vars.Count == 0) return $"{e.Id}: no vars";
                            return string.Join("\n", e.Vars.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => $"{e.Id}.${kv.Key} = {Values.Format(kv.Value)}"));
                        }
                    case "trace":
                        {
                            int n = int.TryParse(rest, out var nn) ? nn : 30;
                            var lines = w.Trace.Skip(Math.Max(0, w.Trace.Count - n)).Select(t =>
                                $"{t.Time,7:0.00}s {t.Outcome,-14} {t.Owner} {t.Rule} ({t.Event}{(t.Other != null ? ", other " + t.Other : "")}{(t.Value != 0 ? $", value {Values.FormatNumber(t.Value)}" : "")})" +
                                (t.Message != null ? $": {t.Message}" : ""));
                            return string.Join("\n", lines.DefaultIfEmpty("(empty)"));
                        }
                    case "budget":
                        {
                            var b = w.Budget();
                            var c = b.Caps;
                            return $"firings {b.FiringsThisFrame}/{c.FiringsPerFrame} per frame, actions {b.ActionsThisFrame}/{c.ActionsPerFrame}, " +
                                $"spawned entities {b.LiveEntities}/{c.Entities}, units {b.LiveUnits}/{c.Units}, projectiles {b.LiveProjectiles}/{c.Projectiles}, per template {c.PerTemplate}, " +
                                $"chain depth {c.ChainDepth}, same rule {c.SameRulePerSecond}/s; throttled {b.Throttled}, dropped {b.Dropped}, capped {b.CappedSpawns}, errors {b.Errors}";
                        }
                    case "entities":
                        {
                            var list = rest.Length == 0
                                ? w.Entities.Where(e => !e.Id.StartsWith("vanilla:")).ToList()
                                : Values.ToSet(w.Evaluate($"all('{rest.Replace("'", "")}')", ctx), 256).ToList();
                            var sb = new StringBuilder();
                            foreach (var e in list.OrderBy(e => e.Id, StringComparer.Ordinal).Take(100))
                                sb.AppendLine($"{e.Id}{(e.Name != null && e.Name != e.Id ? $" (@{e.Name})" : "")} {e.Kind} {e.Team} {e.Template} at {e.Position}" +
                                    $"{(e.Alive ? "" : " dead")}{(e.Enabled ? "" : " disabled")} rules {e.Rules.Count}{(e.Labels.Count > 0 ? " #" + string.Join(" #", e.Labels) : "")}");
                            return sb.Length == 0 ? "(none)" : sb.ToString().TrimEnd();
                        }
                    case "fire":
                        {
                            if (args.Length < 2) throw new ArgumentException("fire <signal> [to-selector] [value]");
                            string name = args[1];
                            var to = args.Length > 2 && args[2] != "all" ? Values.ToSet(w.Evaluate(args[2]), 256) : null;
                            double value = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
                            w.Signal(name, to, value, null);
                            w.Drain();
                            return $"sent \"{name}\" to {(to == null ? "everyone listening" : string.Join(", ", to.Select(e => e.Id)))}";
                        }
                    case "set":
                        {
                            if (args.Length < 3) throw new ArgumentException("set <var|entity.var> <expr>");
                            string target = args[1].TrimStart('$');
                            var v = w.Evaluate(string.Join(" ", args.Skip(2)));
                            int dot = target.LastIndexOf('.');
                            var e = dot < 0 ? w.Level : w.Evaluate(target.Substring(0, dot)).AsEntity ?? w.Find(target.Substring(0, dot)) ?? throw new ArgumentException($"no entity \"{target.Substring(0, dot)}\"");
                            string name = dot < 0 ? target : target.Substring(dot + 1).TrimStart('$');
                            w.SetVar(e, name, v);
                            w.Drain();
                            return $"{e.Id}.${name} = {Values.Format(v)}";
                        }
                    case "eval":
                        {
                            var v = w.Evaluate(rest, ctx);
                            return v.Kind == ValueKind.Set ? $"set of {v.AsSet.Count}: {string.Join(", ", v.AsSet.Take(20).Select(e => e.Id))}" : Values.Format(v);
                        }
                    case "do":
                        {
                            var tok = JToken.Parse(rest);
                            var arr = tok as JArray ?? new JArray(tok);
                            var list = ActionCompiler.CompileList(arr, "dev");
                            int before = w.Budget().Errors;
                            var errorsBefore = w.Trace.Count(t => t.Outcome == TraceOutcome.Error);
                            ctx.RuleLabel = "level / dev";
                            w.RunList(list, ctx);
                            w.Drain();
                            var newErrors = w.Trace.Where(t => t.Outcome == TraceOutcome.Error).Skip(errorsBefore).Select(t => t.Message).ToList();
                            return newErrors.Count == 0 ? $"ran {arr.Count} action(s)" : "errors: " + string.Join("; ", newErrors);
                        }
                    case "king":
                        {
                            int i = int.Parse(rest) - 1;
                            var defs = KingActionDefs(w);
                            if (i < 0 || i >= defs.Count) throw new ArgumentException($"the level has {defs.Count} king action(s)");
                            return TryKingAction(w, defs[i]) ?? $"king action \"{(string)defs[i]["id"]}\" used";
                        }
                    case "snapshot":
                        return w.Capture().ToString(Newtonsoft.Json.Formatting.None);
                    default:
                        throw new ArgumentException(Usage);
                }
            });
        }
    }
}
