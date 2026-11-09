using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*
    The behaviour engine's expression language (docs/builder-engine.md, "Expressions"): one small, pure grammar for
    numbers, conditions, selectors and positions, plus the filter grammar used by selectors, `of`, `hits`, targeting
    and `.is()`. Pure C#: the validator type-checks the AST (ExprChecker), the runtime compiles it to closures.
    agent-kit/palace.py carries a Python port; tests/logic-expr-cases.json keeps the two in step.
*/
namespace PalaceMod.Builder.Model.Logic
{
    public enum LType { Any, Number, Bool, Text, Position, Entity, Set, List, Template, Level, Event, Void }

    public enum ExprKind
    {
        Number, Bool, Text,
        /// <summary>Name: self, subject, other, spawned, it, king, castle, level, event (aliases are resolved by the checker).</summary>
        Role,
        /// <summary>Name: a global such as night, isNight, time, gold (LogicSchema.Globals).</summary>
        Global,
        /// <summary>$name: a level variable.</summary>
        LevelVar,
        /// <summary>@name: a placed or attached object by its name.</summary>
        Named,
        /// <summary>@@name: a template id.</summary>
        Template,
        /// <summary>Target.Name. A Name starting with '$' is an entity variable.</summary>
        Member,
        /// <summary>Name(Args); with Target it's a method call (e.nearest(...), list.at(i)).</summary>
        Call,
        /// <summary>Op "-" or "not" on Args[0].</summary>
        Unary,
        /// <summary>Op on Args[0], Args[1]: + - * / % == != &lt; &lt;= &gt; &gt;= and or.</summary>
        Binary,
        /// <summary>Args[0] ? Args[1] : Args[2].</summary>
        Cond,
        /// <summary>A list literal [a, b, ...] (only in var start values).</summary>
        ListLit,
    }

    public sealed class ExprNode
    {
        public ExprKind Kind;
        public string Op;
        public string Name;
        /// <summary>double for Number, bool for Bool, string for Text.</summary>
        public object Literal;
        public ExprNode[] Args = [];
        public ExprNode Target;
        /// <summary>0-based column in the source.</summary>
        public int Col;
        /// <summary>Set by ExprChecker.</summary>
        public LType Type = LType.Any;
        /// <summary>Set by ExprChecker when this text literal is a filter argument (nearest('foe', 3)).</summary>
        public FilterNode Filter;

        public override string ToString() => Kind switch
        {
            ExprKind.Number => ((double)Literal).ToString(CultureInfo.InvariantCulture),
            ExprKind.Bool => (bool)Literal ? "true" : "false",
            ExprKind.Text => "'" + Literal + "'",
            ExprKind.Role or ExprKind.Global => Name,
            ExprKind.LevelVar => "$" + Name,
            ExprKind.Named => "@" + Name,
            ExprKind.Template => "@@" + Name,
            ExprKind.Member => Target + "." + Name,
            ExprKind.Call => (Target != null ? Target + "." : "") + Name + "(" + string.Join(", ", (object[])Args) + ")",
            ExprKind.Unary => Op == "not" ? "(not " + Args[0] + ")" : "(-" + Args[0] + ")",
            ExprKind.Binary => "(" + Args[0] + " " + Op + " " + Args[1] + ")",
            ExprKind.Cond => "(" + Args[0] + " ? " + Args[1] + " : " + Args[2] + ")",
            ExprKind.ListLit => "[" + string.Join(", ", (object[])Args) + "]",
            _ => "?",
        };
    }

    public sealed class ExprException(string message, int col) : Exception(message)
    {
        public int Col { get; } = col;
    }

    public static class Expr
    {
        /// <summary>The longest expression or filter source: only a guard against pathological input. The parser is
        /// recursive, so nesting has its own cap (<see cref="MaxDepth"/>).</summary>
        public const int MaxLength = 8192;

        /// <summary>The deepest an expression tree may nest (brackets, calls, chained operators).</summary>
        public const int MaxDepth = 100;

        public static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
        {
            "self", "subject", "other", "spawned", "it", "outer", "king", "castle", "level", "event",
        };

        /// <summary>A plain name: a letter or '_', then letters, digits or '_' (forEach's `as`).</summary>
        public static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        /// <summary>The tree's depth, measured without recursion.</summary>
        public static int Depth(ExprNode root)
        {
            int max = 0;
            var stack = new Stack<(ExprNode n, int d)>();
            if (root != null) stack.Push((root, 1));
            while (stack.Count > 0)
            {
                var (n, d) = stack.Pop();
                if (d > max) max = d;
                if (d > MaxDepth) break;
                if (n.Target != null) stack.Push((n.Target, d + 1));
                foreach (var a in n.Args) if (a != null) stack.Push((a, d + 1));
            }
            return max;
        }

        /// <summary>Parses an expression. Returns null and sets error/col on failure.</summary>
        public static ExprNode Parse(string src, out string error, out int col)
        {
            error = null; col = 0;
            if (src == null) { error = "empty expression"; return null; }
            if (src.Length > MaxLength) { error = $"longer than {MaxLength} characters"; col = MaxLength; return null; }
            try
            {
                var p = new Parser(src);
                var node = p.ParseExpr();
                p.ExpectEnd();
                if (Depth(node) > MaxDepth) throw new ExprException($"nested more than {MaxDepth} deep (split it up with a variable)", 0);
                return node;
            }
            catch (ExprException e)
            {
                error = e.Message; col = e.Col;
                return null;
            }
        }

        public static ExprNode Parse(string src) => Parse(src, out string e, out int c) ?? throw new ExprException(e, c);

        /// <summary>Splits interpolated text ("Souls {$souls}/10") into string and ExprNode parts. "{{" and "}}" are
        /// literal braces. Returns null and sets error on a bad part.</summary>
        public static List<object> ParseInterp(string text, out string error, out int col)
        {
            error = null; col = 0;
            var parts = new List<object>();
            if (text == null) return parts;
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i++; continue; }
                if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i++; continue; }
                if (c != '{') { sb.Append(c); continue; }
                int end = FindClose(text, i);
                if (end < 0) { error = "unclosed '{'"; col = i; return null; }
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                string inner = text.Substring(i + 1, end - i - 1);
                var node = Parse(inner, out error, out col);
                if (node == null) { col += i + 1; return null; }
                Shift(node, i + 1);
                parts.Add(node);
                i = end;
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }

        private static int FindClose(string s, int open)
        {
            int depth = 0; char quote = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static void Shift(ExprNode n, int by)
        {
            if (n == null) return;
            n.Col += by;
            if (n.Target != null) Shift(n.Target, by);
            foreach (var a in n.Args) Shift(a, by);
        }

        /// <summary>Visits every node depth-first.</summary>
        public static IEnumerable<ExprNode> Walk(ExprNode n)
        {
            if (n == null) yield break;
            yield return n;
            if (n.Target != null) foreach (var t in Walk(n.Target)) yield return t;
            foreach (var a in n.Args) foreach (var t in Walk(a)) yield return t;
        }

        // ---- Lexer and parser ---------------------------------------------------------------------------------

        internal enum Tok { End, Num, Str, Ident, Dollar, At, AtAt, Op, LParen, RParen, LBrack, RBrack, Comma, Dot, Question, Colon }

        internal readonly struct Token(Tok kind, string text, int col, double num = 0)
        {
            public readonly Tok Kind = kind;
            public readonly string Text = text;
            public readonly int Col = col;
            public readonly double Num = num;
            public override string ToString() => Kind == Tok.End ? "the end" : $"\"{Text}\"";
        }

        internal static List<Token> Lex(string s)
        {
            var toks = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                int start = i;
                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    // "1.5" or "3" - but "a.at(1).x"'s dots after an identifier are handled by the parser.
                    string t = s.Substring(start, i - start);
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        throw new ExprException($"bad number \"{t}\"", start);
                    toks.Add(new Token(Tok.Num, t, start, v));
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    int j = s.IndexOf(c, i + 1);
                    if (j < 0) throw new ExprException("unclosed quote", start);
                    toks.Add(new Token(Tok.Str, s.Substring(i + 1, j - i - 1), start));
                    i = j + 1;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    toks.Add(new Token(Tok.Ident, s.Substring(start, i - start), start));
                    continue;
                }
                if (c == '$')
                {
                    i++;
                    int ns = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;   // no '-': $a-1 is a minus
                    if (i == ns) throw new ExprException("'$' needs a variable name", start);
                    toks.Add(new Token(Tok.Dollar, s.Substring(ns, i - ns), start));
                    continue;
                }
                if (c == '@')
                {
                    bool two = i + 1 < s.Length && s[i + 1] == '@';
                    i += two ? 2 : 1;
                    int ns = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-')) i++;   // ids may have '-': @fire-area
                    if (i == ns) throw new ExprException($"'{(two ? "@@" : "@")}' needs a name", start);
                    toks.Add(new Token(two ? Tok.AtAt : Tok.At, s.Substring(ns, i - ns), start));
                    continue;
                }
                switch (c)
                {
                    case '(': toks.Add(new Token(Tok.LParen, "(", i++)); continue;
                    case ')': toks.Add(new Token(Tok.RParen, ")", i++)); continue;
                    case '[': toks.Add(new Token(Tok.LBrack, "[", i++)); continue;
                    case ']': toks.Add(new Token(Tok.RBrack, "]", i++)); continue;
                    case ',': toks.Add(new Token(Tok.Comma, ",", i++)); continue;
                    case '.': toks.Add(new Token(Tok.Dot, ".", i++)); continue;
                    case '?': toks.Add(new Token(Tok.Question, "?", i++)); continue;
                    case ':': toks.Add(new Token(Tok.Colon, ":", i++)); continue;
                }
                string two2 = i + 1 < s.Length ? s.Substring(i, 2) : null;
                if (two2 is "==" or "!=" or "<=" or ">=" or "&&" or "||")
                {
                    toks.Add(new Token(Tok.Op, two2 switch { "&&" => "and", "||" => "or", _ => two2 }, i));
                    i += 2;
                    continue;
                }
                if ("+-*/%<>!".IndexOf(c) >= 0)
                {
                    toks.Add(new Token(Tok.Op, c == '!' ? "not" : c.ToString(), i++));
                    continue;
                }
                if (c == '=') throw new ExprException("use \"==\" to compare (a leading '=' only marks a text field as an expression)", i);
                throw new ExprException($"unexpected character '{c}'", i);
            }
            toks.Add(new Token(Tok.End, "", s.Length));
            return toks;
        }

        private sealed class Parser
        {
            private readonly List<Token> toks;
            private int pos;

            public Parser(string src) { toks = Lex(src); }

            private Token Peek => toks[pos];
            private Token Next() => toks[pos++];
            private bool IsOp(string op) => Peek.Kind == Tok.Op && Peek.Text == op || Peek.Kind == Tok.Ident && Peek.Text == op && (op == "and" || op == "or" || op == "not");

            private Token Expect(Tok kind, string what)
            {
                if (Peek.Kind != kind) throw new ExprException($"expected {what}, found {Peek}", Peek.Col);
                return Next();
            }

            public void ExpectEnd()
            {
                if (Peek.Kind != Tok.End) throw new ExprException($"unexpected {Peek}", Peek.Col);
            }

            private int nesting;

            public ExprNode ParseExpr() => Deeper(ParseCond);

            /// <summary>Brackets, calls, ?: and prefix operators recurse: cap it before the stack does.</summary>
            private ExprNode Deeper(Func<ExprNode> parse)
            {
                if (++nesting > MaxDepth) throw new ExprException($"nested more than {MaxDepth} deep (split it up with a variable)", Peek.Col);
                try { return parse(); }
                finally { nesting--; }
            }

            private ExprNode ParseCond()
            {
                var c = ParseOr();
                if (Peek.Kind != Tok.Question) return c;
                int col = Next().Col;
                var a = ParseCond();
                Expect(Tok.Colon, "':'");
                var b = ParseCond();
                return new ExprNode { Kind = ExprKind.Cond, Args = [c, a, b], Col = col };
            }

            private ExprNode ParseOr()
            {
                var l = ParseAnd();
                while (IsOp("or")) { int col = Next().Col; l = Bin("or", l, ParseAnd(), col); }
                return l;
            }

            private ExprNode ParseAnd()
            {
                var l = ParseNot();
                while (IsOp("and")) { int col = Next().Col; l = Bin("and", l, ParseNot(), col); }
                return l;
            }

            private ExprNode ParseNot()
            {
                if (IsOp("not")) { int col = Next().Col; return new ExprNode { Kind = ExprKind.Unary, Op = "not", Args = [Deeper(ParseNot)], Col = col }; }
                return ParseCmp();
            }

            private ExprNode ParseCmp()
            {
                var l = ParseAdd();
                while (Peek.Kind == Tok.Op && Peek.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
                {
                    var t = Next();
                    l = Bin(t.Text, l, ParseAdd(), t.Col);
                }
                return l;
            }

            private ExprNode ParseAdd()
            {
                var l = ParseMul();
                while (Peek.Kind == Tok.Op && Peek.Text is "+" or "-") { var t = Next(); l = Bin(t.Text, l, ParseMul(), t.Col); }
                return l;
            }

            private ExprNode ParseMul()
            {
                var l = ParseUnary();
                while (Peek.Kind == Tok.Op && Peek.Text is "*" or "/" or "%") { var t = Next(); l = Bin(t.Text, l, ParseUnary(), t.Col); }
                return l;
            }

            private ExprNode ParseUnary()
            {
                if (Peek.Kind == Tok.Op && Peek.Text == "-")
                {
                    int col = Next().Col;
                    var inner = Deeper(ParseUnary);
                    if (inner.Kind == ExprKind.Number) { inner.Literal = -(double)inner.Literal; inner.Col = col; return inner; }
                    return new ExprNode { Kind = ExprKind.Unary, Op = "-", Args = [inner], Col = col };
                }
                if (Peek.Kind == Tok.Op && Peek.Text == "+") { Next(); return Deeper(ParseUnary); }
                return ParsePostfix(ParsePrimary());
            }

            private ExprNode ParsePostfix(ExprNode n)
            {
                while (Peek.Kind == Tok.Dot)
                {
                    Next();
                    var t = Peek;
                    string name;
                    if (t.Kind == Tok.Ident) name = Next().Text;
                    else if (t.Kind == Tok.Dollar) name = "$" + Next().Text;
                    else throw new ExprException($"expected a property after '.', found {t}", t.Col);
                    if (Peek.Kind == Tok.LParen)
                        n = new ExprNode { Kind = ExprKind.Call, Name = name, Target = n, Args = ParseArgs(), Col = t.Col };
                    else
                        n = new ExprNode { Kind = ExprKind.Member, Name = name, Target = n, Col = t.Col };
                }
                return n;
            }

            private ExprNode[] ParseArgs()
            {
                Expect(Tok.LParen, "'('");
                var args = new List<ExprNode>();
                if (Peek.Kind != Tok.RParen)
                {
                    do args.Add(ParseExpr());
                    while (Peek.Kind == Tok.Comma && Next().Kind == Tok.Comma);
                }
                Expect(Tok.RParen, "')'");
                return args.ToArray();
            }

            private ExprNode ParsePrimary()
            {
                var t = Peek;
                switch (t.Kind)
                {
                    case Tok.Num: Next(); return new ExprNode { Kind = ExprKind.Number, Literal = t.Num, Col = t.Col };
                    case Tok.Str: Next(); return new ExprNode { Kind = ExprKind.Text, Literal = t.Text, Col = t.Col };
                    case Tok.Dollar: Next(); return new ExprNode { Kind = ExprKind.LevelVar, Name = t.Text, Col = t.Col };
                    case Tok.At: Next(); return new ExprNode { Kind = ExprKind.Named, Name = t.Text, Col = t.Col };
                    case Tok.AtAt: Next(); return new ExprNode { Kind = ExprKind.Template, Name = t.Text, Col = t.Col };
                    case Tok.LParen:
                    {
                        Next();
                        var e = ParseExpr();
                        Expect(Tok.RParen, "')'");
                        return e;
                    }
                    case Tok.LBrack:
                    {
                        Next();
                        var items = new List<ExprNode>();
                        if (Peek.Kind != Tok.RBrack)
                        {
                            do items.Add(ParseExpr());
                            while (Peek.Kind == Tok.Comma && Next().Kind == Tok.Comma);
                        }
                        Expect(Tok.RBrack, "']'");
                        return new ExprNode { Kind = ExprKind.ListLit, Args = items.ToArray(), Col = t.Col };
                    }
                    case Tok.Ident:
                    {
                        Next();
                        if (t.Text == "true" || t.Text == "false") return new ExprNode { Kind = ExprKind.Bool, Literal = t.Text == "true", Col = t.Col };
                        if (Peek.Kind == Tok.LParen) return new ExprNode { Kind = ExprKind.Call, Name = t.Text, Args = ParseArgs(), Col = t.Col };
                        if (Roles.Contains(t.Text) || LogicSchema.RoleAliases.ContainsKey(t.Text))
                            return new ExprNode { Kind = ExprKind.Role, Name = t.Text, Col = t.Col };
                        return new ExprNode { Kind = ExprKind.Global, Name = t.Text, Col = t.Col };
                    }
                }
                throw new ExprException(t.Kind == Tok.End ? "unexpected end of expression" : $"unexpected {t}", t.Col);
            }

            private static ExprNode Bin(string op, ExprNode l, ExprNode r, int col) => new() { Kind = ExprKind.Binary, Op = op, Args = [l, r], Col = col };
        }
    }

    // ---- Filters ----------------------------------------------------------------------------------------------

    public enum FilterKind { And, Or, Not, Term }

    public sealed class FilterNode
    {
        public FilterKind Kind;
        public FilterNode[] Args = [];
        /// <summary>For a term: a team (foe ally player enemy neutral), a kind (unit building prop projectile area
        /// pickup coin king castle self), melee/ranged, alive/dead, a game tag (Tower, Flying, ...), or a prefixed term:
        /// "label" (#x), "is" (template), "in" (@area or self), "has" (status), "type" (building kind), "where".</summary>
        public string Term;
        /// <summary>The prefixed term's argument: label, template id, "@name" or "self", status id, building kind.</summary>
        public string Arg;
        public ExprNode Where;
        public int Col;

        public override string ToString() => Kind switch
        {
            FilterKind.And => "(" + Args[0] + " and " + Args[1] + ")",
            FilterKind.Or => "(" + Args[0] + " or " + Args[1] + ")",
            FilterKind.Not => "(not " + Args[0] + ")",
            _ => Term switch
            {
                "label" => "#" + Filter.QuoteArg(Arg),
                "where" => "where(" + Where + ")",
                _ when Filter.Prefixes.Contains(Term) => Term + ":" + Filter.QuoteArg(Arg),
                _ => Term,
            },
        };
    }

    public static class Filter
    {
        /// <summary>The prefixed terms (term:argument). `include:` widens what a selector looks at (dead, disabled,
        /// unbuilt, self) without filtering.</summary>
        public static readonly HashSet<string> Prefixes = new(StringComparer.Ordinal) { "is", "in", "has", "type", "include" };

        /// <summary>What `include:` takes.</summary>
        public static readonly string[] Includes = ["dead", "disabled", "unbuilt", "self"];

        private static bool IsPrefix(string word) => Prefixes.Contains(word);

        /// <summary>An argument as written back: quoted when it isn't a plain name.</summary>
        public static string QuoteArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return arg;
            foreach (char c in arg)
                if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '@'))
                    return arg.IndexOf('"') < 0 ? "\"" + arg + "\"" : "'" + arg + "'";
            return arg;
        }

        /// <summary>Parses a filter string. Returns null and sets error/col on failure. Term names aren't checked
        /// here (ExprChecker.CheckFilter does that against LogicSchema).</summary>
        public static FilterNode Parse(string src, out string error, out int col)
        {
            error = null; col = 0;
            if (string.IsNullOrWhiteSpace(src)) { error = "empty filter"; return null; }
            if (src.Length > Expr.MaxLength) { error = $"longer than {Expr.MaxLength} characters"; return null; }
            try
            {
                var p = new P(src);
                var n = p.Or();
                p.SkipWs();
                if (p.I < src.Length) throw new ExprException($"unexpected \"{src.Substring(p.I)}\"", p.I);
                return n;
            }
            catch (ExprException e)
            {
                error = e.Message; col = e.Col;
                return null;
            }
        }

        private sealed class P(string s)
        {
            public int I;

            public void SkipWs() { while (I < s.Length && char.IsWhiteSpace(s[I])) I++; }

            private bool Word(string w)
            {
                SkipWs();
                if (string.CompareOrdinal(s, I, w, 0, w.Length) != 0) return false;
                int end = I + w.Length;
                if (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_' || s[end] == ':')) return false;
                I = end;
                return true;
            }

            public FilterNode Or()
            {
                var l = And();
                while (true)
                {
                    int col = I;
                    if (!Word("or")) return l;
                    l = new FilterNode { Kind = FilterKind.Or, Args = [l, And()], Col = col };
                }
            }

            private FilterNode And()
            {
                var l = Not();
                while (true)
                {
                    int col = I;
                    if (!Word("and")) return l;
                    l = new FilterNode { Kind = FilterKind.And, Args = [l, Not()], Col = col };
                }
            }

            private int nesting;

            private FilterNode Not()
            {
                SkipWs();
                int col = I;
                if (++nesting > Expr.MaxDepth) throw new ExprException($"nested more than {Expr.MaxDepth} deep", col);
                try
                {
                    if (Word("not")) return new FilterNode { Kind = FilterKind.Not, Args = [Not()], Col = col };
                    return Atom();
                }
                finally { nesting--; }
            }

            private FilterNode Atom()
            {
                SkipWs();
                int col = I;
                if (I >= s.Length) throw new ExprException("unexpected end of filter", I);
                if (s[I] == '(')
                {
                    I++;
                    var n = Or();
                    SkipWs();
                    if (I >= s.Length || s[I] != ')') throw new ExprException("expected ')'", I);
                    I++;
                    return n;
                }
                if (s[I] == '#')
                {
                    I++;
                    string label = Arg();
                    if (label.Length == 0) throw new ExprException("'#' needs a label", col);
                    return new FilterNode { Kind = FilterKind.Term, Term = "label", Arg = label, Col = col };
                }
                string word = Name();
                if (word.Length == 0) throw new ExprException($"unexpected '{s[I]}'", I);
                if (word == "where")
                {
                    SkipWs();
                    if (I >= s.Length || s[I] != '(') throw new ExprException("where needs (expression)", I);
                    int depth = 0, start = I + 1; char quote = '\0';
                    for (; I < s.Length; I++)
                    {
                        char c = s[I];
                        if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                        if (c == '\'' || c == '"') quote = c;
                        else if (c == '(') depth++;
                        else if (c == ')' && --depth == 0) break;
                    }
                    if (I >= s.Length) throw new ExprException("unclosed where(", start - 1);
                    string inner = s.Substring(start, I - start);
                    I++;
                    var e = Expr.Parse(inner, out string err, out int ecol);
                    if (e == null) throw new ExprException("in where(...): " + err, start + ecol);
                    return new FilterNode { Kind = FilterKind.Term, Term = "where", Where = e, Col = col };
                }
                if (I < s.Length && s[I] == ':' && IsPrefix(word))
                {
                    I++;
                    string arg = word == "in" && I < s.Length && s[I] == '@' ? "@" + Skip1() : Arg();
                    if (arg.Length == 0 || arg == "@") throw new ExprException($"\"{word}:\" needs a name", col);
                    return new FilterNode { Kind = FilterKind.Term, Term = word, Arg = arg, Col = col };
                }
                return new FilterNode { Kind = FilterKind.Term, Term = word, Col = col };
            }

            private string Skip1() { I++; return Arg(); }

            /// <summary>A prefixed term's argument: a name (letters, digits, _ - . and :, so is:vanilla:Ogre works), or
            /// a quoted one for names with spaces: type:"Defense Tower", is:'vanilla:E Ogre', #"my label".</summary>
            private string Arg()
            {
                if (I < s.Length && (s[I] == '"' || s[I] == '\''))
                {
                    char q = s[I];
                    int start = ++I;
                    while (I < s.Length && s[I] != q) I++;
                    if (I >= s.Length) throw new ExprException($"unclosed {q}", start - 1);
                    return s.Substring(start, I++ - start);
                }
                int from = I;
                while (I < s.Length && (char.IsLetterOrDigit(s[I]) || s[I] is '_' or '-' or '.' or ':')) I++;
                return s.Substring(from, I - from);
            }

            private string Name()
            {
                int start = I;
                while (I < s.Length && (char.IsLetterOrDigit(s[I]) || s[I] == '_' || s[I] == '-' || s[I] == '.')) I++;
                return s.Substring(start, I - start);
            }
        }

        /// <summary>Visits every term.</summary>
        public static IEnumerable<FilterNode> Terms(FilterNode n)
        {
            if (n == null) yield break;
            if (n.Kind == FilterKind.Term) { yield return n; yield break; }
            foreach (var a in n.Args) foreach (var t in Terms(a)) yield return t;
        }
    }
}
