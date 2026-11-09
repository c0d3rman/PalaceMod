using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Targeting overrides (docs/builder-engine.md, `targeting`): an ordered list of rules, first match wins, each with
    a filter, an optional range and a preference. One list replaces both the movement and the attack priorities of a
    unit, a vanilla unit or a tower. Runtime changes (`setTargeting`) stack as layers on the base list: a replacement,
    `prepend` rules, or `remove` (drop the rules with those filters), each permanent or timed.

    A unit with no base list (a vanilla unit, a Builder unit without `targeting`) keeps the game's own priorities;
    a `prepend` layer then tries its rules first and falls back to the game's choice.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    internal sealed class TargetRule
    {
        public string Filter = "foe";
        public float Range = float.PositiveInfinity;
        public string Prefer = "nearest";
        public string Score;
        private Func<LogicContext, LogicEntity, bool> filterFn;
        private Func<LogicContext, Value> scoreFn;

        public Func<LogicContext, LogicEntity, bool> FilterFn => filterFn ??= SteerExpr.Filter(Filter);
        public Func<LogicContext, Value> ScoreFn => Score == null ? null : scoreFn ??= SteerExpr.Expr(Score);

        public static readonly string[] Prefers = ["nearest", "farthest", "weakest", "strongest", "random", "best"];

        /// <summary>A targeting list: an array of rules or filter strings, or one of them alone.</summary>
        public static List<TargetRule> ParseList(JToken t)
        {
            var list = new List<TargetRule>();
            if (t == null || t.Type == JTokenType.Null) return list;
            if (t is JArray a) { foreach (var x in a) list.Add(Parse(x)); }
            else list.Add(Parse(t));
            return list;
        }

        public static TargetRule Parse(JToken t)
        {
            if (t.Type == JTokenType.String) return Check(new TargetRule { Filter = (string)t });
            if (t is not JObject o) throw new LogicError($"a targeting rule must be a filter string or an object, not {t.Type}");
            var r = new TargetRule
            {
                Filter = (string)o["filter"] ?? "foe",
                Range = (float?)o["range"] ?? float.PositiveInfinity,
                Prefer = (string)o["prefer"] ?? "nearest",
                Score = (string)o["score"],
            };
            if (!Prefers.Contains(r.Prefer)) throw new LogicError($"targeting prefer \"{r.Prefer}\": expected one of {string.Join(", ", Prefers)}");
            if (r.Prefer == "best" && r.Score == null) throw new LogicError("targeting prefer \"best\" needs a score");
            return Check(r);
        }

        private static TargetRule Check(TargetRule r)
        {
            _ = r.FilterFn;   // compile now so a bad filter is an error where it was written
            _ = r.ScoreFn;
            return r;
        }

        public JToken ToJson()
        {
            if (float.IsPositiveInfinity(Range) && Prefer == "nearest" && Score == null) return Filter;
            var o = new JObject { ["filter"] = Filter };
            if (!float.IsPositiveInfinity(Range)) o["range"] = Range;
            if (Prefer != "nearest") o["prefer"] = Prefer;
            if (Score != null) o["score"] = Score;
            return o;
        }
    }

    internal sealed class TargetLayer
    {
        public string Kind;                   // replace | prepend | remove
        public List<TargetRule> Rules;        // replace, prepend
        public HashSet<string> Filters;       // remove
        public double Until = double.PositiveInfinity;
    }

    /// <summary>One entity's targeting: base list (null = the game's own) plus layers, and a taunt.</summary>
    internal sealed class TargetingState
    {
        public List<TargetRule> Base;
        public readonly List<TargetLayer> Layers = [];
        public LogicEntity Taunt;
        public double TauntUntil;

        public bool Active(double now) => Base != null || Layers.Count > 0 || (Taunt != null && TauntUntil > now);

        /// <summary>The effective rules now, and whether the game's own priorities follow them.</summary>
        public List<TargetRule> Effective(double now, out bool vanillaTail)
        {
            Layers.RemoveAll(l => l.Until <= now);
            var rules = Base;
            vanillaTail = Base == null;
            foreach (var l in Layers)
            {
                switch (l.Kind)
                {
                    case "replace": rules = l.Rules; vanillaTail = false; break;
                    case "prepend": rules = rules == null ? l.Rules : l.Rules.Concat(rules).ToList(); break;
                    case "remove": if (rules != null) rules = rules.Where(r => !l.Filters.Contains(r.Filter)).ToList(); break;
                }
            }
            return rules ?? [];
        }

        /// <summary>
        /// setTargeting: an array/string replaces; an object takes `targeting` (replace), `prepend` and `remove`.
        /// seconds &gt; 0 makes it a timed layer; otherwise a replacement becomes the new base.
        /// </summary>
        public void Apply(JToken t, double seconds, double now)
        {
            double until = seconds > 0 ? now + seconds : double.PositiveInfinity;
            JToken replace = t is JObject o ? o["targeting"] : t;
            if (replace != null && replace.Type != JTokenType.Null)
            {
                var rules = TargetRule.ParseList(replace);
                if (seconds > 0) Layers.Add(new TargetLayer { Kind = "replace", Rules = rules, Until = until });
                else { Base = rules; Layers.RemoveAll(l => double.IsPositiveInfinity(l.Until)); }
            }
            if (t is JObject ob)
            {
                if (ob["prepend"] is { Type: not JTokenType.Null } pre) Layers.Add(new TargetLayer { Kind = "prepend", Rules = TargetRule.ParseList(pre), Until = until });
                if (ob["remove"] is { Type: not JTokenType.Null } rem)
                {
                    var fs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var x in rem is JArray ra ? (IEnumerable<JToken>)ra : [rem]) fs.Add(x.Type == JTokenType.String ? (string)x : (string)x["filter"]);
                    Layers.Add(new TargetLayer { Kind = "remove", Filters = fs, Until = until });
                }
                if (ob["reset"]?.Type == JTokenType.Boolean && (bool)ob["reset"]) { Base = null; Layers.Clear(); }
            }
        }

        public JObject Capture(double now)
        {
            var o = new JObject();
            if (Base != null) o["base"] = new JArray(Base.Select(r => r.ToJson()));
            var layers = new JArray();
            foreach (var l in Layers)
            {
                if (l.Until <= now) continue;
                var lo = new JObject { ["kind"] = l.Kind, ["left"] = double.IsPositiveInfinity(l.Until) ? -1 : l.Until - now };
                if (l.Rules != null) lo["rules"] = new JArray(l.Rules.Select(r => r.ToJson()));
                if (l.Filters != null) lo["filters"] = new JArray(l.Filters);
                layers.Add(lo);
            }
            if (layers.Count > 0) o["layers"] = layers;
            return o;
        }

        public void Restore(JObject o, double now)
        {
            Base = o["base"] != null ? TargetRule.ParseList(o["base"]) : null;
            Layers.Clear();
            foreach (var lo in o["layers"] as JArray ?? [])
            {
                double left = (double)lo["left"];
                Layers.Add(new TargetLayer
                {
                    Kind = (string)lo["kind"],
                    Rules = lo["rules"] != null ? TargetRule.ParseList(lo["rules"]) : null,
                    Filters = lo["filters"] != null ? new HashSet<string>(lo["filters"].Select(x => (string)x)) : null,
                    Until = left < 0 ? double.PositiveInfinity : now + left,
                });
            }
        }
    }

    /// <summary>Picks targets for a unit from the rules, over the live hittable objects.</summary>
    internal static class TargetChooser
    {
        private static readonly List<TaggedObject> alive = [];
        private static readonly List<(TaggedObject t, LogicEntity e, float d)> hits = [];
        private static int aliveFrame = -1;

        /// <summary>Every live hittable object (AUTO_Alive), gathered once per frame.</summary>
        private static List<TaggedObject> Alive()
        {
            if (aliveFrame != Time.frameCount)
            {
                aliveFrame = Time.frameCount;
                alive.Clear();
                TagManager.instance?.FindAllTaggedObjectsWithTag(alive, TagManager.ETag.AUTO_Alive);
            }
            return alive;
        }

        /// <summary>
        /// Up to <paramref name="count"/> targets for <paramref name="self"/> at <paramref name="from"/>, at most
        /// <paramref name="maxRange"/> (and at least <paramref name="minRange"/>) away. Returns false when the game's
        /// own choice should run instead (no override, or only prepended rules that found nothing).
        /// </summary>
        public static bool Choose(SteerState st, Vector3 from, float maxRange, float minRange, int count, List<TaggedObject> into, List<TargetRule> ownRules = null)
        {
            into.Clear();
            var world = LogicWorld.Current;
            if (world == null) return false;
            double now = world.Time;
            var ts = st?.Targeting;
            var self = st?.Entity;

            if (ts != null && ts.Taunt != null)
            {
                if (ts.TauntUntil > now && ts.Taunt.Alive && ts.Taunt.Go() is { } tg && tg.GetComponent<TaggedObject>() is { } tt)
                {
                    if (TagManager.instance.MeasureDistanceToTaggedObject(tt, from) <= maxRange) { into.Add(tt); return true; }
                }
                else if (ts.TauntUntil <= now) ts.Taunt = null;
            }

            bool vanillaTail;
            List<TargetRule> rules;
            if (ownRules != null) { rules = ownRules; vanillaTail = false; }
            else if (ts != null) rules = ts.Effective(now, out vanillaTail);
            else return false;

            var ctx = new LogicContext { World = world, Self = self, Subject = self, RuleLabel = self?.Id + " targeting" };
            var selfGo = st?.Go;
            foreach (var r in rules)
            {
                float range = Mathf.Min(r.Range, maxRange);
                hits.Clear();
                foreach (var t in Alive())
                {
                    if (t == null || (selfGo != null && t.gameObject == selfGo)) continue;
                    float d = TagManager.instance.MeasureDistanceToTaggedObject(t, from);
                    if (d > range || d < minRange) continue;
                    if (StatusFlags.Has(t.gameObject, "untargetable")) continue;
                    var e = LogicGame.EntityOf(t.gameObject);
                    if (e == null || e == self) continue;
                    bool ok;
                    try { ok = r.FilterFn(ctx, e); }
                    catch (LogicError ex) { SteerLog.Once($"targeting {r.Filter}", ex.Message); ok = false; }
                    if (ok) hits.Add((t, e, d));
                }
                if (hits.Count == 0) continue;
                Order(r, hits, ctx, world);
                for (int i = 0; i < hits.Count && into.Count < count; i++) into.Add(hits[i].t);
                return true;
            }
            return !vanillaTail;
        }

        private static void Order(TargetRule r, List<(TaggedObject t, LogicEntity e, float d)> list, LogicContext ctx, LogicWorld world)
        {
            switch (r.Prefer)
            {
                case "farthest": list.Sort((a, b) => b.d.CompareTo(a.d)); break;
                case "weakest": list.Sort((a, b) => (a.e.Body?.Hp ?? 0).CompareTo(b.e.Body?.Hp ?? 0)); break;
                case "strongest": list.Sort((a, b) => (b.e.Body?.Hp ?? 0).CompareTo(a.e.Body?.Hp ?? 0)); break;
                case "random":
                    for (int i = list.Count - 1; i > 0; i--) { int j = world.Random.RangeInt(0, i); (list[i], list[j]) = (list[j], list[i]); }
                    break;
                case "best":
                    {
                        var scores = new Dictionary<LogicEntity, double>();
                        foreach (var h in list)
                        {
                            double s;
                            try { s = SteerExpr.Number(r.ScoreFn(ctx.With(it: h.e))); }
                            catch (LogicError ex) { SteerLog.Once($"targeting score {r.Score}", ex.Message); s = 0; }
                            scores[h.e] = s;
                        }
                        list.Sort((a, b) => scores[b.e] != scores[a.e] ? scores[b.e].CompareTo(scores[a.e]) : a.d.CompareTo(b.d));
                        break;
                    }
                default: list.Sort((a, b) => a.d.CompareTo(b.d)); break;
            }
        }
    }
}
