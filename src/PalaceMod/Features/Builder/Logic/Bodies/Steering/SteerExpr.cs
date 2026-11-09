using System;
using Newtonsoft.Json.Linq;

/*
    Steering's seam to the expression language (L1's LogicExpr): filters (targeting, kite/flee `from`), expressions
    (`score`, `flee.when`, a convert's `team`, positions) and nested action lists (an attack's `do`).
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal static class SteerExpr
    {
        /// <summary>A compiled filter over candidates (`it`), relative to ctx.Self. Throws LogicError on a syntax error.</summary>
        public static Func<LogicContext, LogicEntity, bool> Filter(string src) => LogicExpr.Filter((src ?? "").Trim(), "targeting");

        /// <summary>A compiled expression (a leading `=` is allowed).</summary>
        public static Func<LogicContext, Value> Expr(string src) => LogicExpr.Compile((src ?? "").Trim());

        public static double Number(Value v) => v.Kind switch
        {
            ValueKind.Number or ValueKind.Bool => v.Num,
            ValueKind.Set => v.AsSet.Count,
            ValueKind.Entity => v.AsEntity != null ? 1 : 0,
            _ => 0,
        };

        public static bool Bool(Value v) => Values.ToBool(v);

        /// <summary>A nested action list from JSON (an attack's `do`).</summary>
        public static ActionList Actions(JArray list, string path) => list == null ? null : ActionCompiler.CompileList(list, path);

        /// <summary>Run actions from game code (a MonoBehaviour): deferred to the world's dispatch, never inside a game call.</summary>
        public static void Run(ActionList list, LogicContext ctx)
        {
            if (list == null || list.Count == 0) return;
            ctx.World?.Defer(list, ctx);
        }
    }
}
