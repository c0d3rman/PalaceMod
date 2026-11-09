using System;
using System.Collections.Generic;
using System.Linq;

/*
    Checks an expression as it's typed, for the error line under an expression field: brackets and quotes, unknown
    names, unknown variables ("did you mean $souls?") and roles this event doesn't have ("other" in a levelStart rule).

    The engine's own parser and type checker (Model/Logic/Expr, L4) is the authority and the validator runs it on save;
    this is the Studio's fast, forgiving first look while typing. Pure C# (unit tested).
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class ExprIssue
    {
        public int Col;          // 1-based
        public string Message;
        public bool Warning;
        public override string ToString() => $"col {Col}: {Message}";
    }

    /// <summary>What names an expression may use where it is.</summary>
    internal sealed class ExprScope
    {
        public EventUi Event;
        public bool IsLevelScript;
        public ISet<string> LevelVars = new HashSet<string>();
        public ISet<string> SelfVars = new HashSet<string>();
        public ISet<string> Names = new HashSet<string>();          // @names
        public bool HasSpawned, HasIt;
    }

    internal static class ExprLint
    {
        public static readonly HashSet<string> Roles = ["self", "subject", "other", "spawned", "it", "king", "castle", "level", "event",
            "killer", "victim", "attacker", "target", "entrant", "collector", "sender"];
        public static readonly HashSet<string> Globals = ["night", "isNight", "isDay", "nights", "time", "nightTime", "nightProgress", "gold", "income", "enemiesAlive", "difficulty"];
        public static readonly HashSet<string> Functions = ["waveCount", "waveUnits", "strongestUnit", "nearest", "farthest", "weakest", "strongest", "random", "all", "best", "count",
            "pointNear", "ahead", "toward", "lerp", "spawnLine", "dir", "angle", "vec", "min", "max", "abs", "floor", "ceil", "round", "clamp", "sqrt", "pow", "sin", "cos",
            "rand", "randInt", "chance"];
        public static readonly HashSet<string> Keywords = ["and", "or", "not", "true", "false"];
        public static readonly HashSet<string> EventFields = ["value", "pos", "name", "choice"];

        public static List<ExprIssue> Check(string text, ExprScope scope = null)
        {
            var issues = new List<ExprIssue>();
            if (string.IsNullOrWhiteSpace(text)) return issues;
            if (text.Length > 512) issues.Add(new ExprIssue { Col = 513, Message = "longer than 512 characters" });
            var stack = new Stack<(char c, int col)>();
            int i = 0;
            char prevSignificant = '\0';
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c is '\'' or '"')
                {
                    int end = text.IndexOf(c, i + 1);
                    if (end < 0) { issues.Add(new ExprIssue { Col = i + 1, Message = "text isn't closed (missing " + c + ")" }); return issues; }
                    i = end + 1;
                    prevSignificant = 'a';
                    continue;
                }
                if (c is '(' or '[') { stack.Push((c, i + 1)); i++; prevSignificant = c; continue; }
                if (c is ')' or ']')
                {
                    char want = c == ')' ? '(' : '[';
                    if (stack.Count == 0 || stack.Peek().c != want) { issues.Add(new ExprIssue { Col = i + 1, Message = $"unexpected \"{c}\"" }); return issues; }
                    stack.Pop();
                    i++;
                    prevSignificant = 'a';
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && prevSignificant != 'a'))
                {
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    prevSignificant = 'a';
                    continue;
                }
                if (c == '$' || c == '@' || char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    bool afterDot = prevSignificant == '.';
                    if (c == '@' && i + 1 < text.Length && text[i + 1] == '@') i++;
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || (c == '@' && text[i] == '-'))) i++;
                    string word = text.Substring(start, i - start);
                    bool call = i < text.Length && NextNonSpace(text, i) == '(';
                    CheckWord(word, start + 1, afterDot, call, scope, issues, text, start);
                    prevSignificant = 'a';
                    continue;
                }
                if (c == '.') { prevSignificant = '.'; i++; continue; }
                if ("+-*/%<>=!?:,".IndexOf(c) >= 0)
                {
                    if (c == '=' && (i + 1 >= text.Length || text[i + 1] != '=') && (i == 0 || "<>!=".IndexOf(text[i - 1]) < 0))
                    {
                        issues.Add(new ExprIssue { Col = i + 1, Message = "use == to compare (= isn't allowed here)" });
                        return issues;
                    }
                    i++;
                    prevSignificant = c;
                    continue;
                }
                issues.Add(new ExprIssue { Col = i + 1, Message = $"unexpected \"{c}\"" });
                return issues;
            }
            if (stack.Count > 0) issues.Add(new ExprIssue { Col = stack.Peek().col, Message = $"\"{stack.Peek().c}\" isn't closed" });
            else if ("+-*/%<>=!?:,.".IndexOf(prevSignificant) >= 0) issues.Add(new ExprIssue { Col = text.TrimEnd().Length, Message = "something is missing at the end" });
            return issues;
        }

        private static char NextNonSpace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i < s.Length ? s[i] : '\0';
        }

        private static void CheckWord(string word, int col, bool afterDot, bool call, ExprScope scope, List<ExprIssue> issues, string text, int start)
        {
            if (word.StartsWith("$"))
            {
                string name = word.Substring(1);
                if (name.Length == 0) { issues.Add(new ExprIssue { Col = col, Message = "a variable name is missing after $" }); return; }
                if (scope == null) return;
                // self.$gen / other.$x: entity vars (only self's are known here); $souls: a level var
                bool entityVar = afterDot;
                if (entityVar)
                {
                    bool onSelf = start >= 5 && text.Substring(0, start).TrimEnd().EndsWith("self.");
                    if (onSelf && !scope.SelfVars.Contains(name))
                        issues.Add(new ExprIssue { Col = col, Message = $"this has no variable \"{name}\"{Suggest(name, scope.SelfVars, "$")}", Warning = scope.SelfVars.Count == 0 });
                }
                else if (!scope.LevelVars.Contains(name))
                    issues.Add(new ExprIssue { Col = col, Message = $"unknown variable \"{word}\"{Suggest(name, scope.LevelVars, "$")}" });
                return;
            }
            if (word.StartsWith("@@")) return; // template
            if (word.StartsWith("@"))
            {
                string name = word.Substring(1);
                if (scope != null && scope.Names.Count > 0 && !scope.Names.Contains(name))
                    issues.Add(new ExprIssue { Col = col, Message = $"nothing is named \"{name}\"{Suggest(name, scope.Names, "@")}", Warning = true });
                return;
            }
            if (afterDot) return; // a property or method: checked by the engine's type checker
            if (Keywords.Contains(word) || Globals.Contains(word)) return;
            if (call)
            {
                if (!Functions.Contains(word)) issues.Add(new ExprIssue { Col = col, Message = $"unknown function \"{word}\"{Suggest(word, Functions, "")}" });
                return;
            }
            if (Roles.Contains(word))
            {
                if (scope == null) return;
                string why = RoleProblem(word, scope);
                if (why != null) issues.Add(new ExprIssue { Col = col, Message = why });
                return;
            }
            issues.Add(new ExprIssue { Col = col, Message = $"unknown name \"{word}\"{Suggest(word, Roles.Concat(Globals), "")}" });
        }

        private static string RoleProblem(string role, ExprScope scope)
        {
            var ev = scope.Event;
            switch (role)
            {
                case "other":
                case "killer" or "victim" or "attacker" or "target" or "entrant" or "collector" or "sender":
                    if (ev != null && ev.Other == null) return $"\"{role}\" is not defined for the event \"{ev.Name}\"";
                    return null;
                case "subject":
                    if (scope.IsLevelScript && ev != null && !ev.CanOf) return $"\"subject\" is not defined for the event \"{ev.Name}\"";
                    return null;
                case "spawned":
                    return scope.HasSpawned ? null : "\"spawned\" only exists in a spawn's or launch's \"then\"";
                default:
                    return null;
            }
        }

        /// <summary>" (did you mean "$souls"?)" for the nearest known name within two edits, else "".</summary>
        public static string Suggest(string word, IEnumerable<string> known, string prefix)
        {
            string best = null;
            int bestD = 3;
            foreach (var k in known)
            {
                int d = Distance(word.ToLowerInvariant(), k.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = k; }
            }
            return best == null ? "" : $" (did you mean \"{prefix}{best}\"?)";
        }

        public static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// <summary>Checks a filter string ("foe and Tower and not #lit").</summary>
        public static List<ExprIssue> CheckFilter(string text, ExprScope scope = null)
        {
            var issues = new List<ExprIssue>();
            if (string.IsNullOrWhiteSpace(text)) return issues;
            int depth = 0, col = 0;
            bool expectTerm = true;
            foreach (var raw in Tokens(text))
            {
                col = raw.col;
                string t = raw.text;
                if (t == "(") { depth++; expectTerm = true; continue; }
                if (t == ")") { if (--depth < 0) { issues.Add(new ExprIssue { Col = col, Message = "unexpected \")\"" }); return issues; } expectTerm = false; continue; }
                if (t is "and" or "or") { if (expectTerm) { issues.Add(new ExprIssue { Col = col, Message = $"\"{t}\" needs something before it" }); return issues; } expectTerm = true; continue; }
                if (t == "not") { expectTerm = true; continue; }
                if (!expectTerm) { issues.Add(new ExprIssue { Col = col, Message = $"join terms with \"and\" or \"or\" before \"{t}\"" }); return issues; }
                expectTerm = false;
                if (t.StartsWith("where(")) { var inner = t.Substring(6, Math.Max(0, t.Length - 7)); foreach (var x in Check(inner, scope)) issues.Add(new ExprIssue { Col = col + 6 + x.Col - 1, Message = x.Message, Warning = x.Warning }); continue; }
                if (t.StartsWith("#") || t.StartsWith("is:") || t.StartsWith("in:") || t.StartsWith("has:") || t.StartsWith("type:")) continue;
                if (FilterWords.Contains(t)) continue;
                if (t.Length > 0 && char.IsUpper(t[0])) continue; // game tags (TagManager.ETag), checked by the engine
                issues.Add(new ExprIssue { Col = col, Message = $"unknown filter word \"{t}\"{Suggest(t, FilterWords, "")}" });
            }
            if (depth > 0) issues.Add(new ExprIssue { Col = text.Length, Message = "\"(\" isn't closed" });
            else if (expectTerm) issues.Add(new ExprIssue { Col = text.TrimEnd().Length, Message = "something is missing at the end" });
            return issues;
        }

        public static readonly HashSet<string> FilterWords = ["foe", "ally", "player", "enemy", "neutral", "unit", "building", "prop", "projectile", "area", "pickup", "coin",
            "king", "castle", "self", "melee", "ranged", "alive", "dead"];

        private static IEnumerable<(string text, int col)> Tokens(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                if (s[i] is '(' or ')') { yield return (s[i].ToString(), i + 1); i++; continue; }
                int start = i;
                if (s.Substring(i).StartsWith("where("))
                {
                    int depth = 0;
                    for (; i < s.Length; i++) { if (s[i] == '(') depth++; else if (s[i] == ')' && --depth == 0) { i++; break; } }
                    yield return (s.Substring(start, i - start), start + 1);
                    continue;
                }
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not '(' and not ')') i++;
                yield return (s.Substring(start, i - start), start + 1);
            }
        }
    }
}
