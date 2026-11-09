using System;
using System.Collections.Generic;
using System.Linq;

/*
    Filters for bodies (projectile `hits`, area `watch`, pickup `by`, surface `allow`), compiled once per string.
    Uses the core's filter compiler when it's there (Install), else a small built-in subset: teams relative to self
    (foe, ally, player, enemy, neutral), kinds (unit, building, prop, projectile, area, pickup, coin, king, castle),
    game tags, #label, is:<template>, alive, and/or/not and parentheses.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyFilter
    {
        /// <summary>The core's compiler: filter text → (self, it) → match. Null until installed.</summary>
        public static Func<string, Func<LogicEntity, LogicEntity, bool>> Core = src =>
        {
            var f = LogicExpr.Filter(src);
            return (self, it) => it != null && LogicWorld.Current != null && f(LogicExpr.Context(self, it), it);
        };

        private static readonly Dictionary<string, Func<LogicEntity, LogicEntity, bool>> cache = new(StringComparer.Ordinal);

        public static Func<LogicEntity, LogicEntity, bool> Compile(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return (_, _) => true;
            if (cache.TryGetValue(filter, out var f)) return f;
            f = Core != null ? Core(filter) : Fallback(filter);
            cache[filter] = f;
            return f;
        }

        public static bool Matches(string filter, LogicEntity self, LogicEntity it) => it != null && Compile(filter)(self, it);

        public static void ClearCache() => cache.Clear();

        // ---- fallback ----------------------------------------------------------------------------------------------

        private static Func<LogicEntity, LogicEntity, bool> Fallback(string text)
        {
            var tokens = Tokenize(text);
            int i = 0;
            var f = Or(tokens, ref i);
            if (i != tokens.Count) throw new LogicError($"filter \"{text}\": unexpected \"{tokens[i]}\"");
            return f;
        }

        private static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c is '(' or ')') { list.Add(c.ToString()); i++; continue; }
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ')') i++;
                list.Add(s.Substring(start, i - start));
            }
            return list;
        }

        private static Func<LogicEntity, LogicEntity, bool> Or(List<string> t, ref int i)
        {
            var a = And(t, ref i);
            while (i < t.Count && t[i] == "or") { i++; var b = And(t, ref i); var l = a; a = (s, x) => l(s, x) || b(s, x); }
            return a;
        }

        private static Func<LogicEntity, LogicEntity, bool> And(List<string> t, ref int i)
        {
            var a = Not(t, ref i);
            while (i < t.Count && t[i] == "and") { i++; var b = Not(t, ref i); var l = a; a = (s, x) => l(s, x) && b(s, x); }
            return a;
        }

        private static Func<LogicEntity, LogicEntity, bool> Not(List<string> t, ref int i)
        {
            if (i < t.Count && t[i] == "not") { i++; var inner = Not(t, ref i); return (s, x) => !inner(s, x); }
            if (i < t.Count && t[i] == "(")
            {
                i++;
                var inner = Or(t, ref i);
                if (i >= t.Count || t[i] != ")") throw new LogicError("filter: missing )");
                i++;
                return inner;
            }
            if (i >= t.Count) throw new LogicError("filter: unexpected end");
            return Term(t[i++]);
        }

        private static Func<LogicEntity, LogicEntity, bool> Term(string w)
        {
            switch (w)
            {
                case "foe": return (s, x) => x.Team != "neutral" && s != null && s.Team != "neutral" && x.Team != s.Team;
                case "ally": return (s, x) => s != null && x.Team == s.Team && x != s;
                case "player":
                case "enemy":
                case "neutral": return (_, x) => x.Team == w;
                case "self": return (s, x) => x == s;
                case "alive": return (_, x) => x.Alive;
                case "dead": return (_, x) => !x.Alive;
                case "unit":
                case "building":
                case "prop":
                case "projectile":
                case "area":
                case "pickup":
                case "coin":
                case "king":
                case "castle":
                case "logic": return (_, x) => x.Kind == w || (w == "unit" && x.Kind == "king");
                case "melee": return (_, x) => x.Body?.HasTag("MeeleFighter") == true;
                case "ranged": return (_, x) => x.Body?.HasTag("RangedFighter") == true;
                case "any": return (_, _) => true;
            }
            if (w.StartsWith("#")) { string l = w.Substring(1); return (_, x) => x.Labels.Contains(l); }
            if (w.StartsWith("is:")) { string id = w.Substring(3); return (_, x) => x.Template == id; }
            if (w.StartsWith("type:")) { string k = w.Substring(5); return (_, x) => x.Body?.BuildingType == k; }
            if (LogicTags.TryParse(w, out _)) return (_, x) => x.Body?.HasTag(w) == true;
            throw new LogicError($"filter: unknown term \"{w}\"");
        }
    }
}
