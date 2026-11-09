using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Dev harness for L2's actions before (and besides) the rule compiler: `logic-l2 act <action> <json params>` runs a
    registered action with literal params. Values: numbers/bools/strings as JSON; entities and positions as role
    words ("self", "other", "king", "castle", "@name", an entity id, "nearest:<ETag>[:range]", "all:<ETag>[:range]")
    or [x, y, z] / [x, z]. Nested action lists aren't supported here (they need the compiler).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class DevParams(JObject json, LogicContext ctx) : ActionParams
    {
        public override bool Has(string name) => json[name] != null;
        public override JToken Raw(string name) => json[name];

        public override Value Get(string name)
        {
            var t = json[name];
            if (t == null) return Value.None;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float: return Value.Number(t.Value<double>());
                case JTokenType.Boolean: return Value.Bool(t.Value<bool>());
                case JTokenType.Array:
                    var a = t.Select(x => x.Value<float>()).ToArray();
                    return Value.Position(a.Length == 2 ? new Vec(a[0], 0, a[1]) : new Vec(a[0], a[1], a[2]));
                case JTokenType.String:
                    var s = t.Value<string>();
                    var set = Resolve(s, ctx);
                    if (set != null) return set.Count == 1 && !s.StartsWith("all:") ? Value.Entity(set[0]) : Value.Set(set);
                    return Value.Text(s);
                default: return Value.None;
            }
        }

        public override double Number(string name, double fallback = 0) { var v = Get(name); return v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num : fallback; }
        public override bool Bool(string name, bool fallback = false) { var v = Get(name); return v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num != 0 : fallback; }
        public override string Text(string name, string fallback = null) => json[name]?.Type == JTokenType.String ? json[name].Value<string>() : json[name]?.ToString() ?? fallback;
        public override string Template(string name) => Text(name);

        public override Vec? Position(string name)
        {
            var v = Get(name);
            return v.Kind switch
            {
                ValueKind.Position => v.Pos,
                ValueKind.Entity => v.AsEntity.Position,
                ValueKind.Set => v.AsSet.Count > 0 ? v.AsSet[0].Position : null,
                _ => null,
            };
        }

        public override LogicEntity Entity(string name)
        {
            var v = Get(name);
            return v.Kind == ValueKind.Entity ? v.AsEntity : v.Kind == ValueKind.Set && v.AsSet.Count > 0 ? v.AsSet[0] : null;
        }

        public override IReadOnlyList<LogicEntity> Entities(string name)
        {
            var v = Get(name);
            return v.Kind == ValueKind.Entity ? [v.AsEntity] : v.Kind == ValueKind.Set ? v.AsSet : Array.Empty<LogicEntity>();
        }

        public override ActionList Actions(string name) => null;

        public void SetTarget(LogicEntity e) => Target = e;

        /// <summary>A role word or selector shorthand, or null when the text isn't one.</summary>
        public static List<LogicEntity> Resolve(string s, LogicContext ctx)
        {
            var world = LogicWorld.Current;
            switch (s)
            {
                case "self": return ctx.Self != null ? [ctx.Self] : [];
                case "other": return ctx.Other != null ? [ctx.Other] : [];
                case "spawned": return ctx.Spawned != null ? [ctx.Spawned] : [];
                case "it": return ctx.It != null ? [ctx.It] : [];
                case "king": return PlayerInteraction.instance != null ? [LogicGame.EntityOf(PlayerInteraction.instance.gameObject)] : [];
                case "castle": return CastleCenter.instance != null ? [LogicGame.EntityOf(CastleCenter.instance.gameObject)] : [];
            }
            if (s.StartsWith("@")) return world?.FindNamed(s.Substring(1)) is { } named ? [named] : [];
            if (s.StartsWith("nearest:") || s.StartsWith("all:"))
            {
                var parts = s.Split(':');
                if (!LogicTags.TryParse(parts[1], out var tag)) return [];
                float range = parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 1e6f;
                var from = (ctx.Self?.Position ?? Vec.Zero).ToVector3();
                var found = new List<TaggedObject>();
                TagManager.instance.FindAllTaggedObjectsWithTag(found, tag);
                var list = found
                    .Where(t => t != null && Vector3.Distance(t.transform.position, from) <= range && (ctx.Self == null || t.gameObject != ctx.Self.Go()))
                    .OrderBy(t => Vector3.Distance(t.transform.position, from))
                    .Select(t => LogicGame.EntityOf(t.gameObject)).ToList();
                return s.StartsWith("nearest:") ? list.Take(1).ToList() : list;
            }
            if (world?.Find(s) is { } byId) return [byId];
            return null;
        }
    }

    internal static class DevAct
    {
        /// <summary>logic-l2 act &lt;action&gt; [json] [self=&lt;role&gt;] [other=&lt;role&gt;]</summary>
        public static string Run(string[] args)
        {
            if (args.Length == 0) return "act <action> [json] [self=<role>] [other=<role>]   roles: self king castle @name <id> nearest:<ETag>[:r] all:<ETag>[:r]";
            string action = args[0];
            if (!LogicActions.Handlers.TryGetValue(action, out var entry)) return $"no action \"{action}\" (registered: {string.Join(", ", LogicActions.Names.OrderBy(n => n))})";
            LogicWorld.Current ??= new LogicWorld();
            string json = string.Join(" ", args.Skip(1).Where(a => !a.StartsWith("self=") && !a.StartsWith("other=")));
            var ctx = new LogicContext { World = LogicWorld.Current, Self = LogicWorld.Current.Level, RuleLabel = "dev" };
            foreach (var a in args.Skip(1))
            {
                if (a.StartsWith("self=")) ctx.Self = DevParams.Resolve(a.Substring(5), ctx)?.FirstOrDefault() ?? throw new ArgumentException($"no entity for {a}");
                if (a.StartsWith("other=")) ctx.Other = DevParams.Resolve(a.Substring(6), ctx)?.FirstOrDefault() ?? throw new ArgumentException($"no entity for {a}");
            }
            var p = new DevParams(json.Length > 0 ? JObject.Parse(json) : new JObject(), ctx);
            try
            {
                if ((entry.Flags & ActionFlags.PerTarget) != 0)
                {
                    var targets = p.Has("target") ? p.Entities("target") : [ctx.Self];
                    foreach (var t in targets.Take(256))
                    {
                        p.SetTarget(t);
                        entry.Handler(ctx.With(it: t), p);
                    }
                    return $"{action} ran on {targets.Count} target(s): {string.Join(", ", targets.Take(8).Select(t => t?.Id))}";
                }
                entry.Handler(ctx, p);
                return $"{action} ran";
            }
            catch (LogicError e)
            {
                return $"{action}: rule error: {e.Message}";
            }
        }
    }
}
