using System.Collections.Generic;
using UnityEngine;

namespace PalaceMod.Builder.Logic.Bodies.Steering
{
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
