"""The behaviour engine's expression language, preset expansion and logic validator, in Python (stdlib only).

A faithful port of src/PalaceMod/Features/Builder/Model/Logic/{Expr,ExprChecker,Presets,LogicValidator}.cs, which are
the source of truth: the same grammar, the same parenthesised forms, the same messages, paths and columns. The
vocabulary (events, actions, functions, properties, globals, filter terms, tags, built-in presets) is read from
logic-schema.json (generated from LogicSchema.cs), never re-typed here. tests/logic-expr-cases.json and
tests/agent-kit/test_palace_logic.py keep the two in step.

    import palace_logic as pl
    schema = pl.load_schema()
    t, issues = pl.check_expr("self.hpPct < 0.5", pl.Scope(event="damaged"), schema)
    issues = []
    pl.validate_logic(pack_json, issues)        # [(severity, path, message), ...]

Types are their LogicSchema.TypeName strings: any number bool text position entity set list template level event,
and "none" for void.
"""
import copy
import dataclasses
import decimal
import json
import math
import re
import unicodedata
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Set, Tuple, Union

__all__ = [
    "Schema", "load_schema", "ExprError", "ExprNode", "FilterNode", "Scope",
    "parse_expr", "node_str", "parse_filter", "filter_str", "parse_interp", "walk", "filter_terms",
    "check_expr", "check_expr_node", "check_filter", "accepts", "suggest", "distance",
    "expand_presets", "validate_logic", "has_logic",
    "try_parse_color", "ids_is_valid", "content_ref", "spawn_on_death_unit", "lowered_rule_index", "cs_double",
]

ANY, NUMBER, BOOL, TEXT, POSITION, ENTITY, SET, LIST, TEMPLATE, LEVEL, EVENT, VOID = (
    "any", "number", "bool", "text", "position", "entity", "set", "list", "template", "level", "event", "none")
_TYPES = {ANY, NUMBER, BOOL, TEXT, POSITION, ENTITY, SET, LIST, TEMPLATE, LEVEL, EVENT, VOID}

MAX_LENGTH = 512
ROLES = ["self", "subject", "other", "spawned", "it", "king", "castle", "level", "event"]

# Model/Logic/LogicDefs.cs (EntityKinds, Teams): not part of LogicSchema, so not in logic-schema.json.
ENTITY_KIND_TEMPLATES = ["prop", "projectile", "area", "pickup", "logic"]
ENTITY_KIND_PLACES = ["prop", "projectile", "area", "pickup", "logic", "path", "point"]
TEAMS = ["player", "enemy", "neutral", "owner"]

_MISSING = object()


# ---- C# compatibility helpers ---------------------------------------------------------------------------------

def _is_ws(c: str) -> bool:
    """char.IsWhiteSpace."""
    return c in "\t\n\v\f\r \x85\xa0" or unicodedata.category(c) in ("Zs", "Zl", "Zp")


def _is_digit(c: str) -> bool:
    """char.IsDigit (Unicode Nd)."""
    return unicodedata.category(c) == "Nd"


def _is_letter(c: str) -> bool:
    """char.IsLetter."""
    return unicodedata.category(c) in ("Lu", "Ll", "Lt", "Lm", "Lo")


def _is_letter_or_digit(c: str) -> bool:
    return _is_letter(c) or _is_digit(c)


def _is_hex(c: str) -> bool:
    return c in "0123456789abcdefABCDEF"


def cs_double(v: float) -> str:
    """double.ToString(CultureInfo.InvariantCulture) on .NET Core 3.0+: the shortest round-trip digits, scientific
    notation ("1E+17", "1E-05") when the exponent is >= 17 or < -4."""
    if isinstance(v, bool):
        v = float(v)
    if isinstance(v, int):
        v = float(v)
    if math.isnan(v):
        return "NaN"
    if math.isinf(v):
        return "∞" if v > 0 else "-∞"
    if v == 0:
        return "-0" if math.copysign(1.0, v) < 0 else "0"
    sign = "-" if v < 0 else ""
    d = decimal.Decimal(repr(abs(v)))
    _, digits, exp = d.as_tuple()
    ds = "".join(map(str, digits)).rstrip("0")
    exp += len(digits) - len(ds)
    if not ds:
        return "0"
    x = len(ds) - 1 + exp  # scientific exponent
    if x >= 17 or x < -4:
        mant = ds[0] + ("." + ds[1:] if len(ds) > 1 else "")
        return f"{sign}{mant}E{'+' if x >= 0 else '-'}{abs(x):02d}"
    if exp >= 0:
        return sign + ds + "0" * exp
    point = len(ds) + exp
    if point > 0:
        return sign + ds[:point] + "." + ds[point:]
    return sign + "0." + "0" * (-point) + ds


def _num_text(v) -> str:
    """A JSON number as C# prints it: integers as long, floats as double."""
    if isinstance(v, int) and not isinstance(v, bool):
        return str(v)
    return cs_double(float(v))


def _is_num(v) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _cs_cast_str(v) -> Optional[str]:
    """(string)JToken: a string as is, a number or bool as text, null/missing as None."""
    if v is _MISSING or v is None:
        return None
    if isinstance(v, str):
        return v
    if isinstance(v, bool):
        return "True" if v else "False"
    if _is_num(v):
        return _num_text(v)
    return None


def _json_type_name(v) -> str:
    """JTokenType.ToString().ToLowerInvariant()."""
    if v is None:
        return "null"
    if isinstance(v, bool):
        return "boolean"
    if isinstance(v, int):
        return "integer"
    if isinstance(v, float):
        return "float"
    if isinstance(v, str):
        return "string"
    if isinstance(v, list):
        return "array"
    if isinstance(v, dict):
        return "object"
    return "undefined"


def _json_compact(v) -> str:
    """JToken.ToString(Formatting.None)."""
    if isinstance(v, bool):
        return "true" if v else "false"
    if _is_num(v):
        return _num_text(v)
    if isinstance(v, list):
        return "[" + ",".join(_json_compact(x) for x in v) + "]"
    if isinstance(v, dict):
        return "{" + ",".join(json.dumps(k, ensure_ascii=False) + ":" + _json_compact(x) for k, x in v.items()) + "}"
    return json.dumps(v, ensure_ascii=False)


def _json_equal(a, b) -> bool:
    """JToken.DeepEquals (a bool never equals a number)."""
    if isinstance(a, bool) or isinstance(b, bool):
        return isinstance(a, bool) and isinstance(b, bool) and a == b
    if _is_num(a) and _is_num(b):
        return a == b
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(_json_equal(x, y) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(_json_equal(a[k], b[k]) for k in a)
    return type(a) is type(b) and a == b


_FLOAT_RE = re.compile(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\Z", re.ASCII)


def _try_parse_double(s: str) -> Optional[float]:
    """double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture)."""
    t = s.strip(" \t\n\v\f\r")
    if _FLOAT_RE.match(t):
        return float(t)
    if t in ("Infinity", "+Infinity", "∞", "+∞"):
        return math.inf
    if t in ("-Infinity", "-∞"):
        return -math.inf
    if t == "NaN":
        return math.nan
    return None


def try_parse_color(hex_: Optional[str]) -> bool:
    """Validator.TryParseColor: "#rrggbb" or "rrggbb"."""
    if not isinstance(hex_, str):
        return False
    h = hex_.lstrip("#")
    if len(h) != 6:
        return False
    t = h.strip("\t\n\v\f\r ")
    return len(t) > 0 and all(_is_hex(c) for c in t)


def ids_is_valid(id_: Optional[str]) -> bool:
    """Ids.IsValid: 1-64 chars of a-z, 0-9, '_', '-', '.', starting with a letter or digit."""
    if not isinstance(id_, str) or not id_ or len(id_) > 64:
        return False
    if not _is_letter_or_digit(id_[0]):
        return False
    return all(("a" <= c <= "z") or ("0" <= c <= "9") or c in "_-." for c in id_)


_VAR_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def _var_name_ok(s: str) -> bool:
    return isinstance(s, str) and _VAR_NAME.match(s) is not None


def content_ref(raw: Optional[str]) -> Tuple[str, Optional[str], Optional[str]]:
    """ContentRef.Parse: (kind, id, pack) with kind one of invalid vanilla scene asset building local pack."""
    if not isinstance(raw, str) or not raw.strip():
        return ("invalid", None, None)
    raw = raw.strip()
    colon = raw.find(":")
    if colon > 0:
        prefix, rest = raw[:colon], raw[colon + 1:].strip()
        if not rest:
            return ("invalid", None, None)
        kind = {"vanilla": "vanilla", "scene": "scene", "asset": "asset", "unit": "local", "building": "building"}.get(prefix)
        return (kind, rest, None) if kind else ("invalid", None, None)
    slash = raw.find("/")
    if 0 < slash < len(raw) - 1 and raw.find("/", slash + 1) < 0:
        return ("pack", raw[slash + 1:], raw[:slash])
    return ("local", raw, None) if ids_is_valid(raw) else ("invalid", None, None)


def _model_ref_kind(reference: Optional[str]) -> str:
    """ModelRef.Kind: asset, scenery, unit or invalid."""
    if not isinstance(reference, str) or not reference.strip():
        return "invalid"
    r = reference.strip()
    colon = r.find(":")
    if colon <= 0 or colon == len(r) - 1:
        return "invalid"
    if not r[colon + 1:].strip():
        return "invalid"
    return {"asset": "asset", "mesh": "scenery", "vanilla": "unit"}.get(r[:colon], "invalid")


# ---- Schema ---------------------------------------------------------------------------------------------------

class ParamSpec:
    def __init__(self, d: dict):
        self.name = d.get("name")
        self.type = d.get("type")
        self.required = bool(d.get("required", False))
        self.default = d.get("default", _MISSING)
        self.min = d.get("min")
        self.max = d.get("max")
        self.options = d.get("options")
        self.description = d.get("description")
        self.binds = d.get("binds")

    @classmethod
    def make(cls, name, type_, min_=None, max_=None):
        return cls({"name": name, "type": type_, "min": min_, "max": max_})


class EventSpec:
    def __init__(self, d: dict):
        self.name = d["name"]
        self.params = [ParamSpec(p) for p in d.get("params") or []]
        self.other = d.get("other")
        self.value = d.get("value", VOID)
        self.fields = dict(d.get("fields") or {})
        self.aliases = dict(d.get("aliases") or {})
        self.owners = list(d.get("owners") or ["any"])
        self.global_ = bool(d.get("global", False))


class ActionSpec:
    def __init__(self, d: dict):
        self.name = d["name"]
        self.params = [ParamSpec(p) for p in d.get("params") or []]
        # LogicSchema.Act(targeted: true) prepends the shared "target" param; the dump has no "targeted" flag.
        self.targeted = bool(self.params) and self.params[0].name == "target" and self.params[0].type == "entities" \
            and (self.params[0].description or "").startswith("Who it acts on")

    def param(self, name):
        for p in self.params:
            if p.name == name:
                return p
        return None


class FunctionSpec:
    def __init__(self, d: dict):
        self.name = d["name"]
        self.args = list(d.get("args") or [])
        self.returns = d.get("returns", ANY)
        self.method = bool(d.get("method", False))
        self.only_on = d.get("onlyOn")

    @property
    def min_args(self):
        return sum(1 for a in self.args if not a.endswith("?") and a != "...")


class PropertySpec:
    def __init__(self, d: dict):
        self.on = d["on"]
        self.name = d["name"]
        self.returns = d["returns"]


class Schema:
    """logic-schema.json, indexed like LogicSchema."""

    def __init__(self, data: dict):
        self.data = data
        self.events: Dict[str, EventSpec] = {e["name"]: EventSpec(e) for e in data.get("events", [])}
        self.actions: Dict[str, ActionSpec] = {a["name"]: ActionSpec(a) for a in data.get("actions", [])}
        self.functions: Dict[str, FunctionSpec] = {f["name"]: FunctionSpec(f) for f in data.get("functions", [])}
        self.properties: List[PropertySpec] = [PropertySpec(p) for p in data.get("properties", [])]
        self.globals: Dict[str, str] = {g["name"]: g.get("type", VOID) for g in data.get("globals", [])}
        self.roles: Dict[str, str] = {r["name"]: r.get("type", VOID) for r in data.get("roles", [])}
        self.stats: List[str] = [s["name"] for s in data.get("stats", [])]
        self.flags: List[str] = [f["name"] for f in data.get("flags", [])]
        self.filter_terms: List[str] = [f["name"] for f in data.get("filterTerms", [])]
        # LogicSchema.Tags is a HashSet in source order; the dump sorts it (only suggestion tie-breaks can differ).
        self.tags: List[str] = list(data.get("tags", []))
        self.tag_set: Set[str] = set(self.tags)
        self.inputs: Dict[str, List[str]] = dict(data.get("inputs", {}))
        self.param_types: List[str] = list(data.get("paramTypes", []))
        self.caps: Dict[str, Tuple[int, int]] = {k: (v["default"], v["max"]) for k, v in data.get("caps", {}).items()}
        self.presets: List[dict] = list(data.get("presets", []))
        self.role_aliases: Dict[str, str] = {}
        for e in self.events.values():
            self.role_aliases.update(e.aliases)
        self.plain_terms: List[str] = [k for k in self.filter_terms if k and all(_is_letter(c) for c in k)]
        self.plain_term_set = set(self.plain_terms)

    def property(self, on: str, name: str) -> Optional[PropertySpec]:
        for p in self.properties:
            if p.name == name and (p.on == on or p.on == ANY):
                return p
        return None


_DEFAULT_SCHEMA: Optional[Schema] = None


def load_schema(path: Union[str, Path, None] = None) -> Schema:
    """Loads logic-schema.json (default: next to this file). The default one is cached."""
    global _DEFAULT_SCHEMA
    if path is None:
        if _DEFAULT_SCHEMA is None:
            _DEFAULT_SCHEMA = Schema(json.loads((Path(__file__).resolve().parent / "logic-schema.json").read_text(encoding="utf-8")))
        return _DEFAULT_SCHEMA
    return Schema(json.loads(Path(path).read_text(encoding="utf-8")))


def _schema(schema: Optional[Schema]) -> Schema:
    return schema if schema is not None else load_schema()


# ---- Expressions: AST -----------------------------------------------------------------------------------------

class ExprError(Exception):
    """A parse error: message and 0-based column."""

    def __init__(self, message: str, col: int):
        super().__init__(message)
        self.message = message
        self.col = col


class ExprNode:
    """kind: Number Bool Text Role Global LevelVar Named Template Member Call Unary Binary Cond ListLit."""
    __slots__ = ("kind", "op", "name", "literal", "args", "target", "col", "type", "filter")

    def __init__(self, kind, op=None, name=None, literal=None, args=None, target=None, col=0):
        self.kind = kind
        self.op = op
        self.name = name
        self.literal = literal
        self.args = args if args is not None else []
        self.target = target
        self.col = col
        self.type = ANY
        self.filter = None

    def __str__(self):
        return node_str(self)

    def __repr__(self):
        return f"ExprNode({node_str(self)!r})"


def node_str(n: "ExprNode") -> str:
    """ExprNode.ToString: the parenthesised form."""
    k = n.kind
    if k == "Number":
        return cs_double(n.literal)
    if k == "Bool":
        return "true" if n.literal else "false"
    if k == "Text":
        return "'" + n.literal + "'"
    if k in ("Role", "Global"):
        return n.name
    if k == "LevelVar":
        return "$" + n.name
    if k == "Named":
        return "@" + n.name
    if k == "Template":
        return "@@" + n.name
    if k == "Member":
        return node_str(n.target) + "." + n.name
    if k == "Call":
        return (node_str(n.target) + "." if n.target is not None else "") + n.name + "(" + ", ".join(node_str(a) for a in n.args) + ")"
    if k == "Unary":
        return "(not " + node_str(n.args[0]) + ")" if n.op == "not" else "(-" + node_str(n.args[0]) + ")"
    if k == "Binary":
        return "(" + node_str(n.args[0]) + " " + n.op + " " + node_str(n.args[1]) + ")"
    if k == "Cond":
        return "(" + node_str(n.args[0]) + " ? " + node_str(n.args[1]) + " : " + node_str(n.args[2]) + ")"
    if k == "ListLit":
        return "[" + ", ".join(node_str(a) for a in n.args) + "]"
    return "?"


def walk(n: Optional[ExprNode]):
    """Expr.Walk: every node depth-first."""
    if n is None:
        return
    yield n
    if n.target is not None:
        yield from walk(n.target)
    for a in n.args:
        yield from walk(a)


# ---- Expressions: lexer and parser ----------------------------------------------------------------------------

class _Tok:
    __slots__ = ("kind", "text", "col", "num")

    def __init__(self, kind, text, col, num=0.0):
        self.kind, self.text, self.col, self.num = kind, text, col, num

    def __str__(self):
        return "the end" if self.kind == "End" else f'"{self.text}"'


def _lex(s: str) -> List[_Tok]:
    toks = []
    i, n = 0, len(s)
    while i < n:
        c = s[i]
        if _is_ws(c):
            i += 1
            continue
        start = i
        if _is_digit(c) or (c == "." and i + 1 < n and _is_digit(s[i + 1])):
            while i < n and (_is_digit(s[i]) or s[i] == "."):
                i += 1
            t = s[start:i]
            v = _try_parse_double(t) if t.isascii() else None
            if v is None:
                raise ExprError(f'bad number "{t}"', start)
            toks.append(_Tok("Num", t, start, v))
            continue
        if c in "'\"":
            j = s.find(c, i + 1)
            if j < 0:
                raise ExprError("unclosed quote", start)
            toks.append(_Tok("Str", s[i + 1:j], start))
            i = j + 1
            continue
        if _is_letter(c) or c == "_":
            while i < n and (_is_letter_or_digit(s[i]) or s[i] == "_"):
                i += 1
            toks.append(_Tok("Ident", s[start:i], start))
            continue
        if c == "$":
            i += 1
            ns = i
            while i < n and (_is_letter_or_digit(s[i]) or s[i] in "_-"):
                i += 1
            if i == ns:
                raise ExprError("'$' needs a variable name", start)
            toks.append(_Tok("Dollar", s[ns:i], start))
            continue
        if c == "@":
            two = i + 1 < n and s[i + 1] == "@"
            i += 2 if two else 1
            ns = i
            while i < n and (_is_letter_or_digit(s[i]) or s[i] in "_-"):
                i += 1
            if i == ns:
                raise ExprError(f"'{'@@' if two else '@'}' needs a name", start)
            toks.append(_Tok("AtAt" if two else "At", s[ns:i], start))
            continue
        single = {"(": "LParen", ")": "RParen", "[": "LBrack", "]": "RBrack", ",": "Comma", ".": "Dot", "?": "Question", ":": "Colon"}
        if c in single:
            toks.append(_Tok(single[c], c, i))
            i += 1
            continue
        two2 = s[i:i + 2] if i + 1 < n else None
        if two2 in ("==", "!=", "<=", ">=", "&&", "||"):
            toks.append(_Tok("Op", {"&&": "and", "||": "or"}.get(two2, two2), i))
            i += 2
            continue
        if c in "+-*/%<>!":
            toks.append(_Tok("Op", "not" if c == "!" else c, i))
            i += 1
            continue
        if c == "=":
            raise ExprError("use \"==\" to compare (a leading '=' only marks a text field as an expression)", i)
        raise ExprError(f"unexpected character '{c}'", i)
    toks.append(_Tok("End", "", n))
    return toks


class _Parser:
    def __init__(self, src: str, schema: Schema):
        self.toks = _lex(src)
        self.pos = 0
        self.schema = schema

    @property
    def peek(self) -> _Tok:
        return self.toks[self.pos]

    def next(self) -> _Tok:
        t = self.toks[self.pos]
        self.pos += 1
        return t

    def is_op(self, op):
        p = self.peek
        return (p.kind == "Op" and p.text == op) or (p.kind == "Ident" and p.text == op and op in ("and", "or", "not"))

    def expect(self, kind, what):
        if self.peek.kind != kind:
            raise ExprError(f"expected {what}, found {self.peek}", self.peek.col)
        return self.next()

    def expect_end(self):
        if self.peek.kind != "End":
            raise ExprError(f"unexpected {self.peek}", self.peek.col)

    def parse_expr(self):
        return self.parse_cond()

    def parse_cond(self):
        c = self.parse_or()
        if self.peek.kind != "Question":
            return c
        col = self.next().col
        a = self.parse_cond()
        self.expect("Colon", "':'")
        b = self.parse_cond()
        return ExprNode("Cond", args=[c, a, b], col=col)

    def parse_or(self):
        l = self.parse_and()
        while self.is_op("or"):
            col = self.next().col
            l = ExprNode("Binary", op="or", args=[l, self.parse_and()], col=col)
        return l

    def parse_and(self):
        l = self.parse_not()
        while self.is_op("and"):
            col = self.next().col
            l = ExprNode("Binary", op="and", args=[l, self.parse_not()], col=col)
        return l

    def parse_not(self):
        if self.is_op("not"):
            col = self.next().col
            return ExprNode("Unary", op="not", args=[self.parse_not()], col=col)
        return self.parse_cmp()

    def parse_cmp(self):
        l = self.parse_add()
        while self.peek.kind == "Op" and self.peek.text in ("==", "!=", "<", "<=", ">", ">="):
            t = self.next()
            l = ExprNode("Binary", op=t.text, args=[l, self.parse_add()], col=t.col)
        return l

    def parse_add(self):
        l = self.parse_mul()
        while self.peek.kind == "Op" and self.peek.text in ("+", "-"):
            t = self.next()
            l = ExprNode("Binary", op=t.text, args=[l, self.parse_mul()], col=t.col)
        return l

    def parse_mul(self):
        l = self.parse_unary()
        while self.peek.kind == "Op" and self.peek.text in ("*", "/", "%"):
            t = self.next()
            l = ExprNode("Binary", op=t.text, args=[l, self.parse_unary()], col=t.col)
        return l

    def parse_unary(self):
        if self.peek.kind == "Op" and self.peek.text == "-":
            col = self.next().col
            inner = self.parse_unary()
            if inner.kind == "Number":
                inner.literal = -inner.literal
                inner.col = col
                return inner
            return ExprNode("Unary", op="-", args=[inner], col=col)
        if self.peek.kind == "Op" and self.peek.text == "+":
            self.next()
            return self.parse_unary()
        return self.parse_postfix(self.parse_primary())

    def parse_postfix(self, n):
        while self.peek.kind == "Dot":
            self.next()
            t = self.peek
            if t.kind == "Ident":
                name = self.next().text
            elif t.kind == "Dollar":
                name = "$" + self.next().text
            else:
                raise ExprError(f"expected a property after '.', found {t}", t.col)
            if self.peek.kind == "LParen":
                n = ExprNode("Call", name=name, target=n, args=self.parse_args(), col=t.col)
            else:
                n = ExprNode("Member", name=name, target=n, col=t.col)
        return n

    def _list(self, close):
        items = []
        if self.peek.kind != close:
            while True:
                items.append(self.parse_expr())
                if not (self.peek.kind == "Comma" and self.next().kind == "Comma"):
                    break
        return items

    def parse_args(self):
        self.expect("LParen", "'('")
        args = self._list("RParen")
        self.expect("RParen", "')'")
        return args

    def parse_primary(self):
        t = self.peek
        k = t.kind
        if k == "Num":
            self.next()
            return ExprNode("Number", literal=t.num, col=t.col)
        if k == "Str":
            self.next()
            return ExprNode("Text", literal=t.text, col=t.col)
        if k == "Dollar":
            self.next()
            return ExprNode("LevelVar", name=t.text, col=t.col)
        if k == "At":
            self.next()
            return ExprNode("Named", name=t.text, col=t.col)
        if k == "AtAt":
            self.next()
            return ExprNode("Template", name=t.text, col=t.col)
        if k == "LParen":
            self.next()
            e = self.parse_expr()
            self.expect("RParen", "')'")
            return e
        if k == "LBrack":
            self.next()
            items = self._list("RBrack")
            self.expect("RBrack", "']'")
            return ExprNode("ListLit", args=items, col=t.col)
        if k == "Ident":
            self.next()
            if t.text in ("true", "false"):
                return ExprNode("Bool", literal=t.text == "true", col=t.col)
            if self.peek.kind == "LParen":
                return ExprNode("Call", name=t.text, args=self.parse_args(), col=t.col)
            if t.text in ROLES or t.text in self.schema.role_aliases:
                return ExprNode("Role", name=t.text, col=t.col)
            return ExprNode("Global", name=t.text, col=t.col)
        raise ExprError("unexpected end of expression" if k == "End" else f"unexpected {t}", t.col)


def _parse(src: Optional[str], schema: Optional[Schema] = None) -> Tuple[Optional[ExprNode], Optional[str], int]:
    """Expr.Parse(src, out error, out col)."""
    if src is None:
        return None, "empty expression", 0
    if len(src) > MAX_LENGTH:
        return None, f"longer than {MAX_LENGTH} characters", MAX_LENGTH
    try:
        p = _Parser(src, _schema(schema))
        node = p.parse_expr()
        p.expect_end()
        return node, None, 0
    except ExprError as e:
        return None, e.message, e.col


def parse_expr(src: str, schema: Optional[Schema] = None) -> ExprNode:
    """Parses an expression; raises ExprError(message, col) with a 0-based column."""
    node, err, col = _parse(src, schema)
    if node is None:
        raise ExprError(err, col)
    return node


def _shift(n: Optional[ExprNode], by: int):
    if n is None:
        return
    n.col += by
    _shift(n.target, by)
    for a in n.args:
        _shift(a, by)


def _find_close(s: str, open_: int) -> int:
    depth, quote = 0, None
    for i in range(open_, len(s)):
        c = s[i]
        if quote is not None:
            if c == quote:
                quote = None
            continue
        if c in "'\"":
            quote = c
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _parse_interp(text: Optional[str], schema: Optional[Schema] = None):
    """Expr.ParseInterp: (parts, error, col)."""
    parts: List[Union[str, ExprNode]] = []
    if text is None:
        return parts, None, 0
    sb = []
    i = 0
    while i < len(text):
        c = text[i]
        if c == "{" and i + 1 < len(text) and text[i + 1] == "{":
            sb.append("{")
            i += 2
            continue
        if c == "}" and i + 1 < len(text) and text[i + 1] == "}":
            sb.append("}")
            i += 2
            continue
        if c != "{":
            sb.append(c)
            i += 1
            continue
        end = _find_close(text, i)
        if end < 0:
            return None, "unclosed '{'", i
        if sb:
            parts.append("".join(sb))
            sb = []
        node, err, col = _parse(text[i + 1:end], schema)
        if node is None:
            return None, err, col + i + 1
        _shift(node, i + 1)
        parts.append(node)
        i = end + 1
    if sb:
        parts.append("".join(sb))
    return parts, None, 0


def parse_interp(text: Optional[str], schema: Optional[Schema] = None) -> List[Union[str, ExprNode]]:
    """Splits interpolated text ("Souls {$souls}/10") into str and ExprNode parts ("{{"/"}}" are literal braces);
    raises ExprError on a bad part."""
    parts, err, col = _parse_interp(text, schema)
    if parts is None:
        raise ExprError(err, col)
    return parts


# ---- Filters --------------------------------------------------------------------------------------------------

class FilterNode:
    """kind: And Or Not Term. term: a plain term, or label is in has type where (with arg / where)."""
    __slots__ = ("kind", "args", "term", "arg", "where", "col")

    def __init__(self, kind, args=None, term=None, arg=None, where=None, col=0):
        self.kind, self.args, self.term, self.arg, self.where, self.col = kind, args or [], term, arg, where, col

    def __str__(self):
        return filter_str(self)

    def __repr__(self):
        return f"FilterNode({filter_str(self)!r})"


def filter_str(f: FilterNode) -> str:
    """FilterNode.ToString."""
    if f.kind == "And":
        return "(" + filter_str(f.args[0]) + " and " + filter_str(f.args[1]) + ")"
    if f.kind == "Or":
        return "(" + filter_str(f.args[0]) + " or " + filter_str(f.args[1]) + ")"
    if f.kind == "Not":
        return "(not " + filter_str(f.args[0]) + ")"
    if f.term == "label":
        return "#" + f.arg
    if f.term == "where":
        return "where(" + node_str(f.where) + ")"
    if f.term in ("is", "in", "has", "type"):
        return f.term + ":" + f.arg
    return f.term


def filter_terms(n: Optional[FilterNode]):
    """Filter.Terms: every term."""
    if n is None:
        return
    if n.kind == "Term":
        yield n
        return
    for a in n.args:
        yield from filter_terms(a)


class _FilterParser:
    def __init__(self, s: str, schema: Schema):
        self.s = s
        self.i = 0
        self.schema = schema

    def skip_ws(self):
        while self.i < len(self.s) and _is_ws(self.s[self.i]):
            self.i += 1

    def word(self, w):
        self.skip_ws()
        s = self.s
        if s[self.i:self.i + len(w)] != w:
            return False
        end = self.i + len(w)
        if end < len(s) and (_is_letter_or_digit(s[end]) or s[end] in "_:"):
            return False
        self.i = end
        return True

    def or_(self):
        l = self.and_()
        while True:
            col = self.i
            if not self.word("or"):
                return l
            l = FilterNode("Or", args=[l, self.and_()], col=col)

    def and_(self):
        l = self.not_()
        while True:
            col = self.i
            if not self.word("and"):
                return l
            l = FilterNode("And", args=[l, self.not_()], col=col)

    def not_(self):
        self.skip_ws()
        col = self.i
        if self.word("not"):
            return FilterNode("Not", args=[self.not_()], col=col)
        return self.atom()

    def atom(self):
        s = self.s
        self.skip_ws()
        col = self.i
        if self.i >= len(s):
            raise ExprError("unexpected end of filter", self.i)
        if s[self.i] == "(":
            self.i += 1
            n = self.or_()
            self.skip_ws()
            if self.i >= len(s) or s[self.i] != ")":
                raise ExprError("expected ')'", self.i)
            self.i += 1
            return n
        if s[self.i] == "#":
            self.i += 1
            label = self.name()
            if not label:
                raise ExprError("'#' needs a label", col)
            return FilterNode("Term", term="label", arg=label, col=col)
        word = self.name()
        if not word:
            raise ExprError(f"unexpected '{s[self.i]}'", self.i)
        if word == "where":
            self.skip_ws()
            if self.i >= len(s) or s[self.i] != "(":
                raise ExprError("where needs (expression)", self.i)
            depth, start, quote = 0, self.i + 1, None
            while self.i < len(s):
                c = s[self.i]
                if quote is not None:
                    if c == quote:
                        quote = None
                    self.i += 1
                    continue
                if c in "'\"":
                    quote = c
                elif c == "(":
                    depth += 1
                elif c == ")":
                    depth -= 1
                    if depth == 0:
                        break
                self.i += 1
            if self.i >= len(s):
                raise ExprError("unclosed where(", start - 1)
            inner = s[start:self.i]
            self.i += 1
            e, err, ecol = _parse(inner, self.schema)
            if e is None:
                raise ExprError("in where(...): " + err, start + ecol)
            return FilterNode("Term", term="where", where=e, col=col)
        if self.i < len(s) and s[self.i] == ":" and word in ("is", "in", "has", "type"):
            self.i += 1
            if word == "in" and self.i < len(s) and s[self.i] == "@":
                self.i += 1
                arg = "@" + self.name()
            else:
                arg = self.name()
            if not arg or arg == "@":
                raise ExprError(f'"{word}:" needs a name', col)
            return FilterNode("Term", term=word, arg=arg, col=col)
        return FilterNode("Term", term=word, col=col)

    def name(self):
        s = self.s
        start = self.i
        while self.i < len(s) and (_is_letter_or_digit(s[self.i]) or s[self.i] in "_-."):
            self.i += 1
        return s[start:self.i]


def _parse_filter(src: Optional[str], schema: Optional[Schema] = None):
    """Filter.Parse: (node, error, col)."""
    if src is None or all(_is_ws(c) for c in src):
        return None, "empty filter", 0
    if len(src) > MAX_LENGTH:
        return None, f"longer than {MAX_LENGTH} characters", 0
    try:
        p = _FilterParser(src, _schema(schema))
        n = p.or_()
        p.skip_ws()
        if p.i < len(src):
            raise ExprError(f'unexpected "{src[p.i:]}"', p.i)
        return n, None, 0
    except ExprError as e:
        return None, e.message, e.col


def parse_filter(src: str, schema: Optional[Schema] = None) -> FilterNode:
    """Parses a filter string; raises ExprError(message, col). Term names aren't checked here (check_filter does)."""
    f, err, col = _parse_filter(src, schema)
    if f is None:
        raise ExprError(err, col)
    return f


# ---- Type checker ---------------------------------------------------------------------------------------------

@dataclasses.dataclass
class Scope:
    """CheckScope: what an expression can see. None for a set means "unknown here": that check is skipped.
    event is an event name or an EventSpec (None for a component field); extra_roles maps role -> type name."""
    event: Any = None
    level_script: bool = False
    has_it: bool = False
    has_spawned: bool = False
    extra_roles: Optional[Dict[str, str]] = None
    level_vars: Optional[Set[str]] = None
    self_vars: Optional[Set[str]] = None
    spawned_vars: Optional[Set[str]] = None
    any_entity_vars: Optional[Set[str]] = None
    named_vars: Optional[Dict[str, Set[str]]] = None
    names: Optional[Set[str]] = None
    templates: Optional[Set[str]] = None
    statuses: Optional[Set[str]] = None

    def with_(self, **changes) -> "Scope":
        return dataclasses.replace(self, **changes)


def accepts(expected: str, actual: str) -> bool:
    """ExprChecker.Accepts: with the documented coercions."""
    if expected == ANY or actual == ANY or expected == actual:
        return True
    return {POSITION: ENTITY, NUMBER: SET, SET: ENTITY, TEMPLATE: TEXT, TEXT: TEMPLATE, LIST: SET}.get(expected) == actual


def distance(a: str, b: str) -> int:
    """ExprChecker.Distance: optimal string alignment (Damerau-style) distance."""
    d = [[0] * (len(b) + 1) for _ in range(len(a) + 1)]
    for i in range(len(a) + 1):
        d[i][0] = i
    for j in range(len(b) + 1):
        d[0][j] = j
    for i in range(1, len(a) + 1):
        for j in range(1, len(b) + 1):
            d[i][j] = min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (0 if a[i - 1] == b[j - 1] else 1))
            if i > 1 and j > 1 and a[i - 1] == b[j - 2] and a[i - 2] == b[j - 1]:
                d[i][j] = min(d[i][j], d[i - 2][j - 2] + 1)
    return d[len(a)][len(b)]


def suggest(name: Optional[str], options, prefix: str = "") -> str:
    """ExprChecker.Suggest: ' (did you mean "x"?)' or ''."""
    if name is None or options is None:
        return ""
    best, best_d = None, None
    ln = name.lower()
    for o in options:
        if o is None:
            continue
        dd = distance(ln, o.lower())
        if best_d is None or dd < best_d:
            best_d, best = dd, o
    return f' (did you mean "{prefix}{best}"?)' if best is not None and best_d <= max(1, len(name) // 3) else ""


class _Checker:
    def __init__(self, scope: Optional[Scope], schema: Schema):
        self.scope = scope if scope is not None else Scope()
        self.schema = schema
        self.issues: List[Tuple[int, str]] = []
        ev = self.scope.event
        self.ev: Optional[EventSpec] = schema.events[ev] if isinstance(ev, str) else ev

    def err(self, n, msg):
        self.issues.append((n.col if n is not None else 0, msg))

    def want(self, n, expected, actual, what):
        if not accepts(expected, actual):
            self.err(n, f'{what} needs a {expected}, but "{node_str(n)}" is a {actual}')

    def visit(self, n: ExprNode) -> str:
        n.type = self._visit(n)
        return n.type

    def _visit(self, n: ExprNode) -> str:
        s, sc = self.scope, self.schema
        k = n.kind
        if k == "Number":
            return NUMBER
        if k == "Bool":
            return BOOL
        if k == "Text":
            return TEXT
        if k == "ListLit":
            for a in n.args:
                self.visit(a)
            return LIST
        if k == "Role":
            return self.role(n)
        if k == "Global":
            if n.name in sc.globals:
                return sc.globals[n.name]
            if s.extra_roles is not None and n.name in s.extra_roles:
                return s.extra_roles[n.name]
            if s.level_vars is not None and n.name in s.level_vars:
                self.err(n, f'unknown name "{n.name}" (level variables need a $: "${n.name}")')
            else:
                self.err(n, f'unknown name "{n.name}"' + suggest(n.name, list(sc.globals) + ROLES))
            return ANY
        if k == "LevelVar":
            if s.level_vars is not None and n.name not in s.level_vars:
                self.err(n, f'unknown variable "${n.name}"' + suggest(n.name, s.level_vars, "$") + " (declare it in logic.vars)")
            return ANY
        if k == "Named":
            if s.names is not None and n.name not in s.names:
                self.err(n, f'nothing is named "@{n.name}"' + suggest(n.name, s.names, "@"))
            return ENTITY
        if k == "Template":
            if s.templates is not None and n.name not in s.templates and not n.name.startswith("vanilla:"):
                self.err(n, f'no template "{n.name}"' + suggest(n.name, s.templates, "@@"))
            return TEMPLATE
        if k == "Member":
            return self.member(n)
        if k == "Call":
            return self.call(n)
        if k == "Unary":
            t = self.visit(n.args[0])
            if n.op == "not":
                self.want(n.args[0], BOOL, t, '"not"')
                return BOOL
            if t in (POSITION, ENTITY):
                return POSITION
            self.want(n.args[0], NUMBER, t, '"-"')
            return NUMBER
        if k == "Binary":
            return self.binary(n)
        if k == "Cond":
            self.want(n.args[0], BOOL, self.visit(n.args[0]), "a condition")
            a = self.visit(n.args[1])
            b = self.visit(n.args[2])
            if a == b:
                return a
            if a == ANY:
                return b
            if b == ANY:
                return a
            if accepts(a, b):
                return a
            if accepts(b, a):
                return b
            self.err(n, f"the two branches have different types ({a} and {b})")
            return ANY
        return ANY

    def role(self, n):
        s, ev = self.scope, self.ev
        name = n.name
        if s.extra_roles is not None and name in s.extra_roles:
            return s.extra_roles[name]
        if name in self.schema.role_aliases and name not in ROLES:
            if ev is None or name not in ev.aliases:
                where = "this field" if ev is None else f'the event "{ev.name}"'
                self.err(n, f'"{name}" is not defined for {where}' + (f" (other is the {ev.other} here)" if ev is not None and ev.other is not None else ""))
                return ENTITY
            name = ev.aliases[name]
        if name == "self":
            return LEVEL if s.level_script else ENTITY
        if name in ("king", "castle"):
            return ENTITY
        if name == "level":
            return LEVEL
        if name == "subject":
            if ev is None:
                self.err(n, '"subject" is only defined in a rule')
            return ENTITY
        if name == "event":
            if ev is None:
                self.err(n, '"event" is only defined in a rule')
            return EVENT
        if name == "other":
            if ev is None:
                self.err(n, '"other" is only defined in a rule')
            elif ev.other is None and not (s.extra_roles is not None and "other" in s.extra_roles):
                self.err(n, f'"other" is not defined for the event "{ev.name}"')
            return ENTITY
        if name == "spawned":
            if not s.has_spawned:
                self.err(n, '"spawned" is only defined in a spawn, launch or replace action\'s then')
            return ENTITY
        if name == "it":
            if not s.has_it:
                self.err(n, '"it" is only defined inside where(), best(), forEach, targeting, or an action applied to a set')
            return ENTITY
        return ENTITY

    def member(self, n):
        s, sc = self.scope, self.schema
        target = n.target
        tt = self.visit(target)
        if n.name.startswith("$"):
            v = n.name[1:]
            if tt == LEVEL:
                if s.level_vars is not None and v not in s.level_vars:
                    self.err(n, f'unknown variable "${v}"' + suggest(v, s.level_vars, "$") + " (declare it in logic.vars)")
                return ANY
            if not accepts(ENTITY, tt):
                self.err(n, f'only entities have variables, but "{node_str(target)}" is a {tt}')
                return ANY
            whose = node_str(target)
            if target.kind == "Role" and target.name == "self" and not s.level_script:
                known = s.self_vars
            elif target.kind == "Role" and target.name == "spawned":
                known = s.spawned_vars if s.spawned_vars is not None else s.any_entity_vars
            elif target.kind == "Named" and s.named_vars is not None and target.name in s.named_vars:
                known = s.named_vars[target.name]
            else:
                known = s.any_entity_vars
            if known is not None and v not in known:
                self.err(n, f'unknown variable "{whose}.${v}"' + suggest(v, known, "$") +
                         (" (no template declares it)" if known is s.any_entity_vars else " (declare it in vars)"))
            return ANY
        if tt == ANY:
            return ANY
        if tt == EVENT:
            ev = self.ev
            if n.name == "value":
                if ev is not None and ev.value == VOID and ev.name != "signal":
                    self.err(n, f'the event "{ev.name}" has no value')
                    return ANY
                return ev.value if ev is not None else ANY
            if n.name == "pos":
                return POSITION
            if n.name in ("name", "choice", "result"):
                if ev is not None and n.name not in ev.fields:
                    self.err(n, f'the event "{ev.name}" has no {n.name}')
                return TEXT
            self.err(n, f'events have value, pos, name, choice and result, not "{n.name}"')
            return ANY
        on = tt
        p = sc.property(on, n.name)
        if p is not None:
            return p.returns
        options = [x.name for x in sc.properties if x.on == on]
        f = sc.functions.get(n.name)
        self.err(n, f'a {tt} has no property "{n.name}"' + suggest(n.name, options) +
                 (f" (call it: .{n.name}(...))" if f is not None and f.method else ""))
        return ANY

    def call(self, n):
        s, sc = self.scope, self.schema
        f = sc.functions.get(n.name)
        if f is None:
            self.err(n, f'unknown function "{n.name}"' + suggest(n.name, list(sc.functions)))
            for a in n.args:
                self.visit(a)
            if n.target is not None:
                self.visit(n.target)
            return ANY
        if n.target is not None:
            rt = self.visit(n.target)
            if not f.method:
                self.err(n, f'"{n.name}" isn\'t a method; call it as {n.name}(...)')
            elif f.only_on is not None and not accepts(f.only_on, rt) and not (f.only_on == LIST and rt == SET):
                self.err(n, f'.{n.name}() works on a {f.only_on}, but "{node_str(n.target)}" is a {rt}')
            elif f.only_on is None and not accepts(POSITION, rt):
                self.err(n, f'.{n.name}() needs an entity or position to anchor at, but "{node_str(n.target)}" is a {rt}')
        elif f.only_on is not None:
            self.err(n, f'"{n.name}" is a method: call it on a {f.only_on}, e.g. self.{n.name}(...)')
        args = f.args
        inf = math.inf
        mn, mx = f.min_args, (inf if "..." in args else len(args))
        if n.name == "dist" and n.target is not None:
            mn, mx = 1, 1
        if n.name == "rand" and len(n.args) == 1:
            self.err(n, "rand takes no arguments (0..1) or two (a..b)")
        if len(n.args) < mn or len(n.args) > mx:
            cnt = str(mn) if mn == mx else (f"at least {mn}" if mx == inf else f"{mn} to {mx}")
            self.err(n, f"{n.name} takes {cnt} argument{'' if mx == 1 else 's'}, got {len(n.args)}")
        first = ANY
        for i, a in enumerate(n.args):
            if i < len(args):
                spec = args[i]
            else:
                spec = next((x for x in reversed(args) if x != "..."), None) or "any"
            if spec == "...":
                spec = args[max(0, args.index("...") - 1)]
            spec = spec.rstrip("?")
            if n.name == "dist" and n.target is not None:
                spec = "position"
            if spec == "filter":
                t = self.visit(a)
                self.want(a, TEXT, t, n.name + "'s filter")
                if a.kind == "Text":
                    fn, err, col = _parse_filter(a.literal, sc)
                    if fn is None:
                        self.issues.append((a.col + 1 + col, "filter: " + err))
                    else:
                        self.visit_filter(fn, a.col + 1)
                        a.filter = fn
            elif spec == "lazy":
                inner = _Checker(s.with_(has_it=True), sc)
                inner.ev = self.ev
                self.want(a, NUMBER, inner.visit(a), n.name + "'s score")
                self.issues.extend(inner.issues)
            elif spec == "status":
                t = self.visit(a)
                self.want(a, TEXT, t, "a status id")
                if a.kind == "Text" and s.statuses is not None and a.literal not in s.statuses:
                    self.err(a, f'no status "{a.literal}"' + suggest(a.literal, s.statuses))
            else:
                t = self.visit(a)
                if i == 0:
                    first = t
                want = spec if spec in (NUMBER, POSITION, ENTITY, TEXT, BOOL) else ANY
                self.want(a, want, t, f"{n.name}'s argument {i + 1}")
        if n.name == "lerp":
            return POSITION if first in (POSITION, ENTITY) else NUMBER
        if n.name == "at" and n.target is not None and n.target.type == SET:
            return ENTITY
        return f.returns

    def binary(self, n):
        a = self.visit(n.args[0])
        b = self.visit(n.args[1])
        op = n.op
        if op in ("and", "or"):
            self.want(n.args[0], BOOL, a, f'"{op}"')
            self.want(n.args[1], BOOL, b, f'"{op}"')
            return BOOL
        if op in ("==", "!="):
            return BOOL
        if op in ("<", "<=", ">", ">="):
            self.want(n.args[0], NUMBER, a, f'"{op}"')
            self.want(n.args[1], NUMBER, b, f'"{op}"')
            return BOOL
        if op in ("+", "-"):
            if op == "+" and (a == TEXT or b == TEXT):
                return TEXT
            if a == POSITION or b == POSITION:
                if not (a in (POSITION, ANY, ENTITY)) or not (b in (POSITION, ANY, ENTITY)):
                    self.err(n, f'"{op}" needs two positions or two numbers ({a} {op} {b})')
                return POSITION
            self.want(n.args[0], NUMBER, a, f'"{op}"')
            self.want(n.args[1], NUMBER, b, f'"{op}"')
            return NUMBER
        if op in ("*", "/"):
            if a == POSITION and b != POSITION:
                self.want(n.args[1], NUMBER, b, f'"{op}"')
                return POSITION
            if b == POSITION and op == "*":
                self.want(n.args[0], NUMBER, a, '"*"')
                return POSITION
        self.want(n.args[0], NUMBER, a, f'"{op}"')
        self.want(n.args[1], NUMBER, b, f'"{op}"')
        return NUMBER

    def visit_filter(self, f: Optional[FilterNode], base_col: int):
        if f is None:
            return
        s, sc = self.scope, self.schema
        if f.kind != "Term":
            for a in f.args:
                self.visit_filter(a, base_col)
            return
        col = base_col + f.col
        term = f.term
        if term == "label":
            return
        if term == "where":
            inner = _Checker(s.with_(has_it=True), sc)
            inner.ev = self.ev
            t = inner.visit(f.where)
            for c, m in inner.issues:
                self.issues.append((col + 6 + c, "in where(...): " + m))
            if not accepts(BOOL, t):
                self.issues.append((col, f'where(...) needs a bool, but "{node_str(f.where)}" is a {t}'))
            return
        if term == "is":
            if s.templates is not None and f.arg not in s.templates and not f.arg.startswith("vanilla"):
                self.issues.append((col, f'no template "{f.arg}" (in is:{f.arg})' + suggest(f.arg, s.templates)))
            return
        if term == "has":
            if s.statuses is not None and f.arg not in s.statuses:
                self.issues.append((col, f'no status "{f.arg}" (in has:{f.arg})' + suggest(f.arg, s.statuses)))
            return
        if term == "in":
            if f.arg == "self":
                return
            if not f.arg.startswith("@"):
                self.issues.append((col, f'in: takes @name or self, not "{f.arg}"'))
                return
            if s.names is not None and f.arg[1:] not in s.names:
                self.issues.append((col, f'nothing is named "{f.arg}"' + suggest(f.arg[1:], s.names, "@")))
            return
        if term == "type":
            return
        if term in sc.plain_term_set or term in sc.tag_set:
            return
        lower = next((t for t in sc.tags if t.lower() == term.lower()), None)
        msg = f'unknown filter term "{term}"'
        msg += f' (game tags are case-sensitive: "{lower}")' if lower is not None else suggest(term, sc.plain_terms + sc.tags)
        if term in ROLES and term not in ("self", "king", "castle"):
            msg += " (filters test each candidate; use where(it == " + term + "))"
        self.issues.append((col, msg))


def check_expr_node(src: Optional[str], scope: Optional[Scope] = None, schema: Optional[Schema] = None):
    """ExprChecker.Check: (type, issues, node); a parse error gives ("any", [its issue], None)."""
    sc = _schema(schema)
    node, err, col = _parse(src, sc)
    if node is None:
        return ANY, [(col, err)], None
    c = _Checker(scope, sc)
    t = c.visit(node)
    return t, c.issues, node


def check_expr(src: Optional[str], scope: Optional[Scope] = None, schema: Optional[Schema] = None) -> Tuple[str, List[Tuple[int, str]]]:
    """Parses and type-checks src: (type name, [(col0, message), ...])."""
    t, issues, _ = check_expr_node(src, scope, schema)
    return t, issues


def check_filter(src: Optional[str], scope: Optional[Scope] = None, schema: Optional[Schema] = None):
    """ExprChecker.CheckFilter: (FilterNode or None, [(col0, message), ...])."""
    sc = _schema(schema)
    f, err, col = _parse_filter(src, sc)
    if f is None:
        return None, [(col, err)]
    c = _Checker(scope, sc)
    c.visit_filter(f, 0)
    return f, c.issues


# ---- Presets --------------------------------------------------------------------------------------------------

MAX_PRESET_DEPTH = 8
SOURCE_FIELD = "_src"
_PRESET_DECLARED = ("id", "name", "description", "category", "kind", "params", "vars", "labels", "rules")


def _subst(t, values: Dict[str, Any]):
    """Presets.Subst: a whole-string "{p}" becomes the value itself; inside a longer string it's inserted as text."""
    if isinstance(t, dict):
        return {k: _subst(v, values) for k, v in t.items()}
    if isinstance(t, list):
        return [_subst(x, values) for x in t]
    if isinstance(t, str):
        s = t
        if "{" not in s:
            return s
        if len(s) > 2 and s[0] == "{" and s[-1] == "}" and s[1:-1] in values:
            return copy.deepcopy(values[s[1:-1]])
        for k, v in values.items():
            key = "{" + k + "}"
            if key not in s:
                continue
            s = s.replace(key, _as_text(v))
        return s
    return copy.deepcopy(t)


def _as_text(v) -> str:
    if isinstance(v, str):
        return "(" + v[1:] + ")" if v.startswith("=") else v
    if isinstance(v, bool):
        return "true" if v else "false"
    if _is_num(v):
        return _num_text(v)
    return _json_compact(v)


def _looks_like_expression(s: str, schema: Schema) -> bool:
    _, issues = check_expr(s, Scope(event=schema.events["damaged"], has_it=True, has_spawned=True), schema)
    return not issues


class _Inst:
    def __init__(self, id_, kind):
        self.id, self.kind = id_, kind
        self.rules = self.labels = self.vars = None
        self.components: Dict[str, Any] = {}


class _PresetCtx:
    def __init__(self, pack: dict, issues, schema: Schema, library):
        self.pack, self.issues, self.schema, self.library = pack, issues, schema, library

    def error(self, path, msg):
        if self.issues is not None:
            self.issues.append(("error", path, msg))

    def warn(self, path, msg):
        if self.issues is not None:
            self.issues.append(("warning", path, msg))

    def find(self, id_):
        if id_ is None:
            return None
        for p in _list(self.pack.get("presets")) or []:
            if isinstance(p, dict) and p.get("id") == id_:
                return p
        lib = self.library
        if lib is not None:
            found = None
            if callable(lib):
                found = lib(id_)
            elif isinstance(lib, dict):
                found = lib.get(id_)
            else:
                found = next((p for p in lib if isinstance(p, dict) and p.get("id") == id_), None)
            if found is not None:
                return found
        return next((p for p in self.schema.presets if p.get("id") == id_), None)

    def known_ids(self):
        out = []
        for p in (_list(self.pack.get("presets")) or []) + self.schema.presets:
            i = p.get("id") if isinstance(p, dict) else None
            if i is not None and i not in out:
                out.append(i)
        return out

    def owner(self, owner: dict, path: str, components: bool, typed_preset: bool = False):
        stack: List[str] = []
        # The owner's own rules first, so their _src paths keep their own indexes.
        if isinstance(owner.get("rules"), list):
            owner["rules"] = self.expand_rules(owner["rules"], path + ".rules", owner, components, stack, 0, top=True)
        pv = owner.get("preset")
        if typed_preset and not isinstance(pv, (dict, list)):
            pv = _cs_cast_str(pv)  # PlaceDef.Preset is a string property (other owners keep it in Extra)
        if isinstance(pv, str) and "when" not in owner:
            id_ = pv
            w = owner.get("with")
            inst = self.instantiate(id_, w if isinstance(w, dict) else None, path, stack)
            owner.pop("preset", None)
            owner.pop("with", None)
            owner["_preset"] = id_
            if inst is not None:
                if "kind" not in owner and inst.kind is not None:
                    owner["kind"] = inst.kind
                self.merge_into(owner, inst, path, components)
                if inst.rules is not None:
                    own = owner.get("rules")
                    owner["rules"] = self.expand_rules(inst.rules, path + f" (preset {id_})", owner, components, stack, 1) + \
                        (own if isinstance(own, list) else [])

    def expand_rules(self, rules: list, path: str, owner: dict, components: bool, stack: List[str], depth: int, top=False):
        result = []
        for i, rule in enumerate(rules):
            rp = f"{path}[{i}]" if top else f"{path}.rules[{i}]"
            if not isinstance(rule, dict):
                result.append(rule)
                continue
            pv = _cs_cast_str(rule.get("preset"))  # RuleDef.Preset is a string property
            if pv is not None:
                id_ = pv
                if depth >= MAX_PRESET_DEPTH:
                    self.error(rp, f"presets nest deeper than {MAX_PRESET_DEPTH}")
                    continue
                if id_ in stack:
                    self.error(rp, f'preset "{id_}" uses itself ({" > ".join(stack + [id_])})')
                    continue
                w = rule.get("with")
                inst = self.instantiate(id_, w if isinstance(w, dict) else None, rp, stack)
                if inst is None:
                    continue
                stack.append(id_)
                self.merge_into(owner, inst, rp, components)
                result.extend(self.expand_rules(inst.rules or [], rp + f" (preset {id_})", owner, components, stack, depth + 1))
                stack.pop()
                continue
            c = copy.deepcopy(rule)
            if SOURCE_FIELD not in c:
                c[SOURCE_FIELD] = rp
            result.append(c)
        return result

    def merge_into(self, owner: dict, inst: _Inst, path: str, components: bool):
        if inst.vars is not None:
            vars_ = owner.get("vars") if isinstance(owner.get("vars"), dict) else {}
            for k, v in inst.vars.items():
                if k not in vars_:
                    vars_[k] = copy.deepcopy(v)
            owner["vars"] = vars_
        if inst.labels is not None:
            labels = owner.get("labels") if isinstance(owner.get("labels"), list) else []
            for l in inst.labels:
                if not any(_json_equal(x, l) for x in labels):
                    labels.append(copy.deepcopy(l))
            owner["labels"] = labels
        if not inst.components:
            return
        if not components:
            self.warn(path, f'preset "{inst.id}" carries components ({", ".join(inst.components)}), which only apply on a unit, entity or place')
            return
        _merge_under(owner, inst.components)

    def instantiate(self, id_: str, with_: Optional[dict], path: str, stack) -> Optional[_Inst]:
        preset = self.find(id_)
        if preset is None:
            self.error(path, f'preset "{id_}" not found' + suggest(id_, self.known_ids()))
            return None
        values: Dict[str, Any] = {}
        ps = preset.get("params") if isinstance(preset.get("params"), dict) else {}
        for w in (with_ or {}):
            if w not in ps:
                self.error(f"{path}.with.{w}", f'preset "{id_}" has no param "{w}"' + suggest(w, list(ps)))
        for key, spec in ps.items():
            spec = spec if isinstance(spec, dict) else {}
            v = with_.get(key) if with_ is not None else None
            vp = f"{path}.with.{key}"
            if v is None:
                v = spec.get("default")
                vp = f"{path} (preset {id_}, param {key} default)"
            if v is None:
                t = _param_type(spec)
                desc = _cs_cast_str(spec.get("description"))
                self.error(path, f'preset "{id_}" needs "{key}" ({t if t is not None else ""}{": " + desc if desc is not None else ""})')
                continue
            self.check_value(spec, key, v, vp)
            values[key] = v
        inst = _Inst(id_, preset.get("kind") if isinstance(preset.get("kind"), str) else None)
        r = _subst(preset.get("rules"), values)
        inst.rules = r if isinstance(r, list) else None
        l = _subst(preset.get("labels"), values)
        inst.labels = l if isinstance(l, list) else None
        v = _subst(preset.get("vars"), values)
        inst.vars = v if isinstance(v, dict) else None
        for k, c in preset.items():
            if k not in _PRESET_DECLARED:
                inst.components[k] = _subst(c, values)
        return inst

    def check_value(self, spec: dict, name: str, v, path: str):
        type_ = _param_type(spec) or "number"
        is_expr = isinstance(v, str) and v.startswith("=")
        if type_ in ("number", "int"):
            if _is_num(v):
                d = float(v)
                lo, hi = spec.get("min"), spec.get("max")
                if (_is_num(lo) and d < lo) or (_is_num(hi) and d > hi):
                    self.error(path, f"{name} must be between {cs_double(lo) if _is_num(lo) else '-∞'} and {cs_double(hi) if _is_num(hi) else '∞'}")
                if type_ == "int" and d != math.floor(d):
                    self.error(path, f"{name} must be a whole number")
            elif not isinstance(v, str):
                self.error(path, f'"{name}" expects a number, got {_json_type_name(v)} {_json_compact(v)}')
            elif not is_expr and not _looks_like_expression(v, self.schema):
                self.error(path, f'"{name}" expects a number: "{v}" isn\'t a number or an expression')
        elif type_ == "bool":
            if not isinstance(v, (bool, str)):
                self.error(path, f'"{name}" expects true or false')
        elif type_ == "enum":
            opts = spec.get("options")
            if isinstance(opts, list) and isinstance(v, str) and not is_expr and v not in opts:
                self.error(path, f'"{name}" must be one of {", ".join(str(o) for o in opts)}')
        else:
            if not isinstance(v, (str, list, dict, int, float, bool)):
                self.error(path, f'"{name}" has an unusable value')


def _param_type(spec: dict) -> Optional[str]:
    """PresetParamDef.Type: "number" when absent, else the value as text (None for null)."""
    return _cs_cast_str(spec["type"]) if "type" in spec else "number"


def _merge_under(to: dict, from_: dict):
    """Presets.MergeUnder: from's fields where to has none; objects merge (to wins); statuses/attacks concatenate."""
    for k, fv in from_.items():
        mine = to.get(k, _MISSING)
        if mine is _MISSING or mine is None:
            to[k] = copy.deepcopy(fv)
        elif isinstance(mine, dict) and isinstance(fv, dict):
            _merge_under(mine, fv)
        elif isinstance(mine, list) and isinstance(fv, list) and k in ("statuses", "attacks"):
            for x in fv:
                if not any(_json_equal(x, y) for y in mine):
                    mine.append(copy.deepcopy(x))


def _expand_json(root: dict, original: dict, issues, schema: Schema, library):
    ctx = _PresetCtx(original, issues, schema, library)

    def each(arr, f):
        if isinstance(arr, list):
            for i, o in enumerate(arr):
                if isinstance(o, dict):
                    f(o, f"[{i}]")

    each(root.get("units"), lambda o, p: ctx.owner(o, "units" + p, True))
    each(root.get("entities"), lambda o, p: ctx.owner(o, "entities" + p, True))
    each(root.get("statuses"), lambda o, p: ctx.owner(o, "statuses" + p, False))

    def level(lv, lp):
        logic = lv.get("logic")
        if not isinstance(logic, dict):
            return
        p = "levels" + lp + ".logic"
        ctx.owner(logic, p, False)
        each(logic.get("places"), lambda o, pp: ctx.owner(o, p + ".places" + pp, True, typed_preset=True))
        attach = logic.get("attach")
        if isinstance(attach, dict):
            for name, ao in list(attach.items()):
                if isinstance(ao, dict):
                    ctx.owner(ao, f'{p}.attach["{name}"]', True)

    each(root.get("levels"), level)


def expand_presets(pack_json: dict, issues: Optional[list] = None, library_presets=None, schema: Optional[Schema] = None) -> Optional[dict]:
    """Presets.Expand: a copy of the pack with every preset use expanded; issues ((severity, path, message)) get paths
    into the original pack. library_presets: a dict id -> preset, a list of presets, or a callable id -> preset."""
    if pack_json is None:
        return None
    root = copy.deepcopy(pack_json)
    _expand_json(root, pack_json, issues, _schema(schema), library_presets)
    return root


# ---- Lowering (LogicRefs.cs) ----------------------------------------------------------------------------------

def spawn_on_death_unit(rule) -> Optional[str]:
    """LogicLowering.SpawnOnDeathUnit: the unit a rule spawns on death if it's exactly the lowered shape, else None."""
    if not isinstance(rule, dict):
        return None
    for k, v in rule.items():
        if v is None and k in ("id", "note", "enabled", "once", "cooldown", "preset"):
            continue  # null typed fields aren't written back
        if k == "when":
            w = _when(v)
            if w is None or not (w["on"] == "death" and w["of"] is None and w["range"] is None and not w["params"]):
                return None
        elif k in ("do", "id", "note", "_src"):
            pass
        elif k == "enabled":
            if v is not True:
                return None
        else:
            return None
    actions = rule.get("do")
    if not isinstance(actions, list) or len(actions) != 1 or not isinstance(actions[0], dict):
        return None
    entity = None
    for k, v in actions[0].items():
        if k == "do":
            if v != "spawn":
                return None
        elif k == "entity":
            entity = v if isinstance(v, str) else None
        elif k == "at":
            if v != "self":
                return None
        elif k == "elite":
            if v != "inherit":
                return None
        elif k == "count":
            if not (isinstance(v, int) and not isinstance(v, bool) and v == 1):
                return None
        else:
            return None
    return None if entity is None or entity.startswith("=") else entity


def lowered_rule_index(rules, own_id: Optional[str], is_unit: Optional[Callable[[str], bool]] = None) -> int:
    """LogicLowering.LoweredRuleIndex."""
    if not isinstance(rules, list):
        return -1
    for i, r in enumerate(rules):
        u = spawn_on_death_unit(r)
        if u is None:
            continue
        kind, id_, _ = content_ref(u)
        if kind == "local" and id_ == own_id:
            continue
        if kind == "invalid" or (is_unit is not None and kind != "vanilla" and not is_unit(u)):
            continue
        return i
    return -1


def _is_unit_in(pack: dict) -> Callable[[str], bool]:
    def f(r):
        kind, id_, _ = content_ref(r)
        if kind in ("vanilla", "pack"):
            return True
        return kind == "local" and any(isinstance(u, dict) and u.get("id") == id_ for u in _list(pack.get("units")) or [])
    return f


# ---- JSON readers mirroring the typed model -------------------------------------------------------------------

def _list(v) -> Optional[list]:
    return v if isinstance(v, list) else None


def _dict(v) -> Optional[dict]:
    return v if isinstance(v, dict) else None


def _s(o, key) -> Optional[str]:
    """A typed string property (null or absent = None; Newtonsoft reads a number or bool into it as text)."""
    if not isinstance(o, dict):
        return None
    return _cs_cast_str(o.get(key))


def _strs(v) -> Optional[list]:
    """A List<string> property: its items as text (null items stay None)."""
    return [_cs_cast_str(x) for x in v] if isinstance(v, list) else None


def _b(o, key) -> Optional[bool]:
    """A bool? property: Newtonsoft also reads a number (non-zero = true) or "true"/"false"."""
    if not isinstance(o, dict):
        return None
    v = o.get(key)
    if isinstance(v, bool):
        return v
    if _is_num(v):
        return v != 0
    if isinstance(v, str) and v.strip().lower() in ("true", "false"):
        return v.strip().lower() == "true"
    return None


def _num(o, key) -> Optional[float]:
    if not isinstance(o, dict):
        return None
    v = o.get(key)
    return float(v) if _is_num(v) else None


def _int(o, key) -> Optional[int]:
    if not isinstance(o, dict):
        return None
    v = o.get(key)
    if isinstance(v, bool) or not _is_num(v):
        return None
    if isinstance(v, float):
        return int(round(v)) if math.isfinite(v) else None  # Convert.ToInt32: to nearest, ties to even
    return v


def _lnum(v):
    """LNumConverter: None, ("v", value) or ("e", expression without '=')."""
    if v is None or v is _MISSING or isinstance(v, bool):
        return None
    if _is_num(v):
        return ("v", float(v))
    if isinstance(v, str):
        s = v.strip()
        if s.startswith("="):
            return ("e", s[1:])
        d = _try_parse_double(s)
        return ("v", d) if d is not None else ("e", s)
    return None


def _when(v):
    """WhenConverter: {on, of, range, params} or None."""
    if isinstance(v, str):
        return {"on": v, "of": None, "range": None, "params": {}}
    if not isinstance(v, dict):
        return None
    of = v.get("of")
    return {"on": _cs_cast_str(v.get("on", _MISSING)), "of": of if isinstance(of, str) else None, "range": _lnum(v.get("range")),
            "params": {k: x for k, x in v.items() if k not in ("on", "of", "range")}}


def _targeting(v):
    """TargetingConverter: {filter, range, prefer, score} or None."""
    if isinstance(v, str):
        return {"filter": v, "range": None, "prefer": None, "score": None}
    if not isinstance(v, dict):
        return None
    return {"filter": _cs_cast_str(v.get("filter", _MISSING)), "prefer": _cs_cast_str(v.get("prefer", _MISSING)),
            "score": _cs_cast_str(v.get("score", _MISSING)), "range": _lnum(v.get("range"))}


def _jt(o: dict, key):
    """A JToken property of a typed class: absent or null is _MISSING (C# null)."""
    v = o.get(key)
    return _MISSING if v is None else v


def _keys(d) -> List[str]:
    return list(d.keys()) if isinstance(d, dict) else []


def _var_set(vars_) -> "_OSet":
    return _OSet(_keys(vars_))


def _conditions(if_):
    if if_ is _MISSING or if_ is None:
        return []
    if isinstance(if_, list):
        return [c for c in if_ if isinstance(c, str)]
    if isinstance(if_, str):
        return [if_]
    if isinstance(if_, bool):
        return ["true" if if_ else "false"]
    return []


# ---- Validator ------------------------------------------------------------------------------------------------

def has_logic(pack: dict) -> bool:
    """LogicValidator.HasLogic: rules, logic, templates, statuses or presets anywhere."""
    def count(k):
        v = _list(pack.get(k))
        return len(v) if v else 0
    if count("entities") > 0 or count("statuses") > 0 or count("presets") > 0:
        return True
    for u in _list(pack.get("units")) or []:
        if isinstance(u, dict) and any(u.get(k) is not None for k in ("rules", "vars", "labels", "targeting", "movement", "attacks", "statuses", "counterpart")):
            return True
    return any(isinstance(l, dict) and l.get("logic") is not None for l in _list(pack.get("levels")) or [])


class _OSet(dict):
    """An insertion-ordered set (C# HashSet<string> enumerates in insertion order when nothing is removed, which
    decides which suggestion wins a tie)."""

    def __init__(self, items=()):
        super().__init__()
        for x in items:
            self[x] = None

    def add(self, x):
        self[x] = None

    def update(self, items):
        for x in items:
            self[x] = None

    def __ior__(self, items):
        self.update(items)
        return self

    def __or__(self, items):
        r = _OSet(self)
        r.update(items)
        return r


class _Owner:
    def __init__(self, path, kind, vars_=None, level_script=False, level=None, rules=None):
        self.path, self.kind, self.vars, self.level_script, self.level, self.rules = path, kind, vars_, level_script, level, rules


class _LevelCtx:
    def __init__(self, path, def_):
        self.path, self.def_ = path, def_
        self.vars = _OSet()
        self.names = _OSet()
        self.named_vars: Dict[str, _OSet] = {}

    @property
    def logic(self) -> dict:
        return self.def_["logic"]


_ID_MSG = "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'"


class _Walker:
    def __init__(self, original: dict, pack: dict, issues: list, schema: Schema):
        self.original, self.pack, self.issues, self.schema = original, pack, issues, schema
        self.units = _OSet()
        self.entities = _OSet()
        self.statuses = _OSet()
        self.templates = _OSet()
        self.any_entity_vars = _OSet()
        self.all_level_vars = _OSet()
        self.rule_ids = _OSet()
        self.template_vars: Dict[str, _OSet] = {}
        self.levels: List[_LevelCtx] = []
        self.signal_edges: List[Tuple[str, Optional[str], Set[str]]] = []

    def error(self, path, msg):
        self.issues.append(("error", path, msg))

    def warn(self, path, msg):
        self.issues.append(("warning", path, msg))

    def lst(self, key) -> list:
        return _list(self.pack.get(key)) or []

    def run(self):
        self.collect()
        is_unit = _is_unit_in(self.pack)
        for i, u in enumerate(self.lst("units")):
            if not isinstance(u, dict):
                continue
            p = f"units[{i}]"
            owner = _Owner(p, "unit", self.template_vars.get(_s(u, "id") or ""), rules=_list(u.get("rules")))
            self.check_labels(u.get("labels"), p + ".labels")
            self.check_var_decls(u.get("vars"), p + ".vars")
            self.check_targeting(_list(u.get("targeting")), p + ".targeting", owner)
            self.check_movement(_dict(u.get("movement")), p + ".movement", owner)
            self.check_attacks(_list(u.get("attacks")), p + ".attacks", owner)
            self.check_status_refs(u.get("statuses"), p + ".statuses")
            if _s(u, "counterpart") is not None:
                self.check_template(_s(u, "counterpart"), p + ".counterpart", self.scope(owner, None))
            self.check_rules(owner, lowered_rule_index(_list(u.get("rules")), _s(u, "id"), is_unit))
        for i, e in enumerate(self.lst("entities")):
            if not isinstance(e, dict):
                self.error(f"entities[{i}]", "is empty")
                continue
            self.check_template_fields(e, f"entities[{i}]", self.template_vars.get(_s(e, "id") or ""), None, place=False)
        for i, s in enumerate(self.lst("statuses")):
            self.check_status(s if isinstance(s, dict) else None, f"statuses[{i}]")
        for i, pr in enumerate(self.lst("presets")):
            self.check_preset_def(pr if isinstance(pr, dict) else None, f"presets[{i}]")
        for lv in self.levels:
            self.check_level(lv)
        self.check_loops()

    # ---- Declarations

    def collect(self):
        for u in self.lst("units"):
            uid = _s(u, "id")
            if uid is None:
                continue
            self.units.add(uid)
            self.template_vars[uid] = _var_set(u.get("vars"))
        for i, e in enumerate(self.lst("entities")):
            p = f"entities[{i}].id"
            if not isinstance(e, dict):
                continue
            eid = _s(e, "id")
            if not ids_is_valid(eid):
                self.error(p, _ID_MSG)
                continue
            if eid in self.units:
                self.error(p, f'"{eid}" is also a unit\'s id; templates share one namespace')
            elif eid in self.entities:
                self.error(p, f'"{eid}" is used twice')
            else:
                self.entities.add(eid)
            self.template_vars[eid] = _var_set(e.get("vars"))
        self.templates |= self.units
        self.templates |= self.entities
        for i, s in enumerate(self.lst("statuses")):
            if not isinstance(s, dict):
                continue
            sid = _s(s, "id")
            if not ids_is_valid(sid):
                self.error(f"statuses[{i}].id", _ID_MSG)
            elif sid in self.statuses:
                self.error(f"statuses[{i}].id", f'"{sid}" is used twice')
            else:
                self.statuses.add(sid)
        preset_ids = set()
        for i, pr in enumerate(_list(self.original.get("presets")) or []):
            if not isinstance(pr, dict):
                continue
            pid = _s(pr, "id")
            if not ids_is_valid(pid):
                self.error(f"presets[{i}].id", _ID_MSG)
            elif pid in preset_ids:
                self.error(f"presets[{i}].id", f'"{pid}" is used twice')
            else:
                preset_ids.add(pid)
        for s in self.template_vars.values():
            self.any_entity_vars |= s
        for r in self.all_rules():
            rid = _s(r, "id")
            if rid is not None:
                self.rule_ids.add(rid)
        for i, l in enumerate(self.lst("levels")):
            if not isinstance(l, dict) or not isinstance(l.get("logic"), dict):
                continue
            logic = l["logic"]
            ctx = _LevelCtx(f"levels[{i}].logic", l)
            ctx.vars |= _keys(logic.get("vars"))
            self.all_level_vars |= ctx.vars
            for place in _list(logic.get("places")) or []:
                if not isinstance(place, dict):
                    continue
                vars_ = _var_set(place.get("vars"))
                ent = _s(place, "entity")
                if ent is not None and ent in self.template_vars:
                    vars_ |= self.template_vars[ent]
                self.any_entity_vars |= vars_
                for n in (_s(place, "id"), _s(place, "name")):
                    if n is not None:
                        ctx.names.add(n)
                        ctx.named_vars[n] = vars_
            for a in (_dict(logic.get("attach")) or {}).values():
                vars_ = _var_set(a.get("vars") if isinstance(a, dict) else None)
                self.any_entity_vars |= vars_
                an = _s(a, "name")
                if an is not None:
                    ctx.names.add(an)
                    ctx.named_vars[an] = vars_
            self.levels.append(ctx)

    def all_rules(self):
        for key in ("units", "entities", "statuses"):
            for o in self.lst(key):
                for r in _list(o.get("rules") if isinstance(o, dict) else None) or []:
                    yield r
        for l in self.lst("levels"):
            logic = _dict(l.get("logic")) if isinstance(l, dict) else None
            if logic is None:
                continue
            for r in _list(logic.get("rules")) or []:
                yield r
            for p in _list(logic.get("places")) or []:
                for r in _list(p.get("rules") if isinstance(p, dict) else None) or []:
                    yield r
            for a in (_dict(logic.get("attach")) or {}).values():
                for r in _list(a.get("rules") if isinstance(a, dict) else None) or []:
                    yield r

    def check_var_decls(self, vars_, path):
        for k, v in (_dict(vars_) or {}).items():
            p = f"{path}.{k}"
            if not _var_name_ok(k):
                self.error(p, "variable names use letters, digits and '_' and don't start with a digit")
            if isinstance(v, (bool, list)) or _is_num(v):
                pass
            elif isinstance(v, str):
                _, issues, _ = check_expr_node(v, Scope(), self.schema)
                self.report(p, issues)
            elif isinstance(v, dict):
                t = _cs_cast_str(v.get("type", _MISSING))
                if t not in ("number", "bool", "text", "position", "entity", "set", "list", "template"):
                    self.error(p + ".type", "must be number, bool, text, position, entity, set, list or template")
            else:
                self.error(p, 'start value must be a number, bool, "expression", [] or {"type", "value"}')

    def check_labels(self, labels, path):
        for i, l in enumerate(_strs(labels) or []):
            if l is None or not l.strip() or all(_is_ws(c) for c in l) or any(_is_ws(c) or c == "#" for c in l):
                self.error(f"{path}[{i}]", "a label is one word without '#' (filters write it #label)")

    def check_status_refs(self, refs, path):
        for i, r in enumerate(_strs(refs) or []):
            if r not in self.statuses:
                self.error(f"{path}[{i}]", f'no status "{r}"' + suggest(r if isinstance(r, str) else None, self.statuses))

    # ---- Scopes

    def scope(self, owner: _Owner, ev: Optional[EventSpec]) -> Scope:
        if owner.level is not None:
            level_vars = owner.level.vars
        elif any(isinstance(l, dict) and l.get("logic") is not None for l in self.lst("levels")):
            level_vars = self.all_level_vars
        else:
            level_vars = None
        if owner.level_script:
            self_vars = None
        elif owner.vars is not None:
            self_vars = owner.vars
        else:
            self_vars = self.any_entity_vars if owner.kind == "status" else None
        if owner.level is not None:
            names = owner.level.names
        elif self.levels:
            names = _OSet(n for l in self.levels for n in l.names)
        else:
            names = None
        return Scope(event=ev, level_script=owner.level_script, level_vars=level_vars, self_vars=self_vars,
                     any_entity_vars=self.any_entity_vars, names=names,
                     named_vars=owner.level.named_vars if owner.level is not None else None,
                     templates=self.templates, statuses=self.statuses)

    def report(self, path, lst):
        for col, msg in lst:
            self.issues.append(("error", path, f"col {col + 1}: {msg}"))

    def expression(self, src, path, scope, want, what=None):
        t, lst, node = check_expr_node(src, scope, self.schema)
        self.report(path, lst)
        if node is not None and not lst and not accepts(want, t) and not (want == SET and t == LEVEL):
            self.error(path, f'{what if what is not None else "this"} needs a {want}, but "{src}" is a {t}')
        return t

    def filter_string(self, src, path, scope):
        _, lst = check_filter(src, scope, self.schema)
        self.report(path, lst)

    def text(self, src, path, scope):
        if src is None:
            return
        if src.startswith("="):
            self.expression(src[1:], path, scope, ANY)
            return
        parts, err, col = _parse_interp(src, self.schema)
        if parts is None:
            self.error(path, f"col {col + 1}: {err}")
            return
        for node in parts:
            if isinstance(node, ExprNode):
                c = _Checker(scope, self.schema)
                c.visit(node)
                self.report(path, c.issues)

    # ---- Rules

    def check_rules(self, owner: _Owner, skip_index=-1):
        ids = set()
        for i, r in enumerate(owner.rules or []):
            src = r.get(SOURCE_FIELD) if isinstance(r, dict) else None
            p = _cs_cast_str(src) if isinstance(r, dict) and SOURCE_FIELD in r else f"{owner.path}.rules[{i}]"
            if not isinstance(r, dict):
                self.error(p, "is empty")
                continue
            rid = _s(r, "id")
            if rid is not None:
                if rid in ids:
                    self.warn(p + ".id", f'another rule here has the id "{rid}" (enableRule/disableRule act on both)')
                ids.add(rid)
            if i == skip_index:
                continue
            self.check_rule(r, p, owner)

    def check_rule(self, r: dict, p: str, owner: _Owner):
        sc = self.schema
        if _s(r, "preset") is not None:
            return
        w = _when(r.get("when"))
        if w is None or w["on"] is None:
            self.error(p + ".when", 'needs an event ("when": "death" or {"on": ...})')
            return
        ev = sc.events.get(w["on"])
        if ev is None:
            self.error(p + ".when", f'unknown event "{w["on"]}"' + suggest(w["on"], list(sc.events)) + " (made with a newer PalaceMod?)")
            return
        scope = self.scope(owner, ev)
        if w["of"] is not None:
            self.filter_string(w["of"], p + ".when.of", scope)
        own_range = any(x.name == "range" for x in ev.params)
        if w["range"] is not None:
            self.num(w["range"], p + ".when.range", scope, 0, None)
        if w["range"] is not None and w["of"] is None and not own_range:
            self.warn(p + ".when.range", 'range only applies with "of"')
        params = w["params"]
        for name, val in params.items():
            spec = next((x for x in ev.params if x.name == name), None)
            if spec is None:
                self.warn(f"{p}.when.{name}", f'the event "{ev.name}" has no param "{name}"' + suggest(name, [x.name for x in ev.params]))
                continue
            self.param(spec, val, f"{p}.when.{name}", scope, owner, None)
        for spec in ev.params:
            if spec.required and spec.name not in params and not (spec.name == "range" and w["range"] is not None):
                self.error(p + ".when", f'the event "{ev.name}" needs "{spec.name}" ({spec.description})')
        if ev.name == "every" and _is_num(params.get("seconds")) and params["seconds"] < 0.1:
            self.warn(p + ".when.seconds", "every is clamped to 0.1 s")
        if owner.level_script and not ev.global_ and w["of"] is None:
            self.error(p + ".when", f'the level script hears "{ev.name}" only from entities: give "of" (e.g. {{"on": "{ev.name}", "of": "enemy"}})')
        elif not owner.level_script and w["of"] is None and "any" not in ev.owners and owner.kind not in ev.owners and owner.kind != "status":
            self.warn(p + ".when", f'"{ev.name}" fires on {"/".join(ev.owners)} entities, not on this {owner.kind}; add "of" to hear others\'')
        ka = params.get("action")
        if ev.name == "kingAction" and owner.level is not None and isinstance(ka, str):
            kas = _list(owner.level.logic.get("kingActions")) or []
            if not any(_s(k, "id") == ka for k in kas):
                self.error(p + ".when.action", f'no king action "{ka}" in logic.kingActions')
        vn = params.get("name")
        if ev.name == "varChanged" and isinstance(vn, str):
            known = scope.level_vars if owner.level_script else owner.vars
            name = vn.lstrip("$")
            if known is not None and name not in known:
                self.error(p + ".when.name", f'unknown variable "{name}"' + suggest(name, known))
        if_ = _jt(r, "if")
        for idx, c in enumerate(_conditions(if_)):
            self.expression(c, f"{p}.if[{idx}]" if isinstance(if_, list) else p + ".if", scope, BOOL, "a condition")
        if if_ is not _MISSING and not isinstance(if_, (str, list, bool)):
            self.error(p + ".if", "must be an expression or a list of expressions")
        cd = _num(r, "cooldown")
        if cd is not None and cd < 0:
            self.error(p + ".cooldown", "must be 0 or more")
        do = _list(r.get("do"))
        if not do:
            self.warn(p + ".do", "does nothing")
        sends: Set[str] = set()
        self.actions(do, p + ".do", scope, owner, sends)
        self.signal_edges.append((p, _cs_cast_str(params.get("name", _MISSING)) if ev.name == "signal" else None, sends))

    def actions(self, lst, path, scope, owner, sends):
        waited = False
        for i, a in enumerate(lst or []):
            p = f"{path}[{i}]"
            if not isinstance(a, dict):
                self.error(p, "is empty")
                continue
            if a.get("do") == "wait":
                waited = True
            self.action(a, p, scope, owner, None if waited else sends)

    def action(self, a: dict, p: str, scope: Scope, owner: _Owner, sends):
        sc = self.schema
        do = _cs_cast_str(a.get("do", _MISSING))
        if do is None:
            self.error(p, 'needs "do": the action\'s name')
            return
        spec = sc.actions.get(do)
        if spec is None:
            self.error(p + ".do", f'unknown action "{do}"' + suggest(do, list(sc.actions)) + " (made with a newer PalaceMod?)")
            return
        params = {k: v for k, v in a.items() if k != "do"}
        for k in params:
            if k != SOURCE_FIELD and spec.param(k) is None:
                self.warn(f"{p}.{k}", f'"{do}" has no param "{k}"' + suggest(k, [x.name for x in spec.params]))
        for ps in spec.params:
            if ps.required and ps.name not in params:
                self.error(p, f'"{do}" needs "{ps.name}" ({ps.description})')
        inner = scope.with_(has_it=True) if "target" in params and spec.targeted else scope
        for ps in spec.params:
            v = params.get(ps.name)
            if v is None:
                continue
            self.param(ps, v, f"{p}.{ps.name}", scope if ps.name == "target" else inner, owner, a, sends)

        def gs(k):
            v = params.get(k)
            return v if isinstance(v, str) else None
        if do == "setStat":
            if sum(1 for k in ("value", "add", "mult") if k in params) != 1:
                self.error(p, "setStat needs exactly one of value, add or mult")
        elif do == "signal":
            if sends is not None and gs("name") is not None:
                sends.add(gs("name"))
        elif do == "launch":
            e = gs("entity")
            if e is not None and not e.startswith("="):
                ent = next((x for x in self.lst("entities") if isinstance(x, dict) and x.get("id") == e), None)
                if ent is not None and _dict(ent.get("projectile")) is None:
                    self.warn(p + ".entity", f'"{e}" has no projectile block, so it flies with the defaults')
        elif do == "drift":
            if not any(k in params for k in ("dir", "toward", "from")):
                self.error(p, "drift needs dir, toward or from")
        elif do == "setTargeting":
            if not any(k in params for k in ("targeting", "prepend", "remove")):
                self.error(p, "setTargeting needs targeting, prepend or remove")
        elif do == "listRemove":
            if "value" not in params and "index" not in params:
                self.error(p, "listRemove needs value or index")
        elif do == "waves":
            op = gs("op")
            if op == "addSpawn" and "unit" not in params:
                self.error(p, 'addSpawn needs "unit"')
            if op in ("scaleCount", "scaleHp") and "value" not in params:
                self.error(p, f'{op} needs "value"')
            if op == "removeUnit" and "unit" not in params:
                self.error(p, 'removeUnit needs "unit"')
        elif do == "building":
            op = gs("op")
            if op in ("setCost", "setIncome") and "value" not in params:
                self.error(p, f'{op} needs "value"')

    def param(self, ps: ParamSpec, v, p: str, scope: Scope, owner: _Owner, action: Optional[dict], sends=None):
        is_string = isinstance(v, str)
        s = v if is_string else None
        t = ps.type

        def act_str(k):
            if action is None:
                return None
            x = action.get(k)
            return x if isinstance(x, str) else None
        if t in ("number", "int"):
            if is_string:
                self.expression(s[1:] if s.startswith("=") else s, p, scope, NUMBER, ps.name)
            elif _is_num(v):
                d = float(v)
                if _is_num(ps.min) and d < ps.min:
                    if ps.name in ("seconds", "every", "times"):
                        self.warn(p, f"clamped to {cs_double(ps.min)}")
                    else:
                        self.error(p, f"must be at least {cs_double(ps.min)}")
                if _is_num(ps.max) and d > ps.max:
                    if ps.name in ("times", "count", "limit"):
                        self.warn(p, f"clamped to {cs_double(ps.max)}")
                    else:
                        self.error(p, f"must be at most {cs_double(ps.max)}")
                if t == "int" and d != math.floor(d):
                    self.error(p, "must be a whole number")
            else:
                self.error(p, "must be a number or an expression")
        elif t == "bool":
            if is_string:
                self.expression(s, p, scope, BOOL, ps.name)
            elif not isinstance(v, bool):
                self.error(p, "must be true, false or an expression")
        elif t == "text":
            if is_string:
                self.text(s, p, scope)
            elif _is_num(v) or isinstance(v, bool):
                pass
            else:
                self.error(p, "must be text")
        elif t == "position":
            if is_string:
                self.expression(s, p, scope, POSITION, ps.name)
            elif isinstance(v, list):
                if len(v) < 2 or len(v) > 3 or any(not _is_num(x) for x in v):
                    self.error(p, "must be [x, z] or [x, y, z]")
            else:
                self.error(p, "must be an expression (an entity or vec(...)) or [x, y, z]")
        elif t == "entity":
            if is_string:
                self.expression(s, p, scope, ENTITY, ps.name)
            else:
                self.error(p, "must be a selector expression (self, other, @name, nearest('foe', 10)...)")
        elif t == "entities":
            if not is_string:
                self.error(p, "must be a selector expression")
            elif not (action is not None and action.get("do") == "signal" and s == "all"):
                self.expression(s, p, scope, SET, ps.name)
        elif t == "template":
            if not is_string:
                self.error(p, 'must be a template id or "=expression"')
            else:
                self.check_template(s, p, scope)
        elif t == "filter":
            if not is_string:
                self.error(p, "must be a filter string")
            else:
                self.filter_string(s, p, scope)
        elif t == "status":
            if not is_string:
                self.error(p, "must be a status id")
            elif s.startswith("="):
                self.expression(s[1:], p, scope, TEXT)
            elif s not in self.statuses:
                self.error(p, f'no status "{s}"' + suggest(s, self.statuses) + " (add it to the pack's statuses)")
        elif t == "rule":
            if not is_string:
                self.error(p, "must be a rule id")
            elif s not in self.rule_ids:
                self.warn(p, f'no rule has the id "{s}"' + suggest(s, self.rule_ids))
        elif t == "var":
            if not is_string:
                self.error(p, "must be a variable name")
                return
            name = s.lstrip("$")
            target = act_str("target")
            level = s.startswith("$") or target == "level" or (owner.level_script and target is None)
            if level:
                known = scope.level_vars
            elif target is None or target == "self":
                known = owner.vars if owner.vars is not None else (self.any_entity_vars if owner.kind == "status" else None)
            else:
                known = self.any_entity_vars
            if not _var_name_ok(name):
                self.error(p, "variable names use letters, digits and '_'")
            elif known is not None and name not in known:
                self.error(p, f'unknown variable "{"$" if level else ""}{name}"' + suggest(name, known) +
                           (" (declare it in logic.vars)" if level else " (declare it in vars)"))
        elif t in ("signal", "label"):
            if not is_string or not s.strip() or all(_is_ws(c) for c in s):
                self.error(p, f"must be a {t} name")
        elif t == "enum":
            opts = self.schema.stats if (action is not None and action.get("do") == "setStat" and ps.name == "stat") else ps.options
            val = s if is_string else (("true" if v else "false") if isinstance(v, bool) else None)
            if val is None or (opts is not None and val not in opts):
                self.error(p, f"must be one of {', '.join(opts or [])}" + (suggest(val, opts) if val is not None else ""))
        elif t == "actions":
            if not isinstance(v, list):
                self.error(p, "must be a list of actions")
                return
            inner = scope
            if ps.binds == "spawned":
                ent = act_str("entity") or act_str("with")
                vars_ = None
                if ent is not None and not ent.startswith("="):
                    _, rid, _ = content_ref(ent)
                    vars_ = self.template_vars.get(rid if rid is not None else ent)
                if ent is not None and ent.startswith("="):
                    vars_ = self.any_entity_vars
                changes = {"has_spawned": True, "spawned_vars": vars_}
                ev = scope.event
                ev_other = (self.schema.events[ev].other if isinstance(ev, str) else ev.other) if ev is not None else None
                if action is not None and action.get("do") == "launch" and ps.name == "onImpact" and ev_other is None:
                    changes["extra_roles"] = {"other": ENTITY}
                inner = scope.with_(**changes)
            if action is not None and action.get("do") == "forEach":
                inner = scope.with_(has_it=True)
            self.actions(v, p, inner, owner, sends)
        elif t == "options":
            if not isinstance(v, list):
                self.error(p, 'must be [{ "weight": n, "do": [...] }, ...]')
                return
            for i, o in enumerate(v):
                if not isinstance(o, dict):
                    self.error(f"{p}[{i}]", 'must be { "weight": n, "do": [...] }')
                    continue
                if "weight" in o:
                    self.param(ParamSpec.make("weight", "number", 0), o["weight"], f"{p}[{i}].weight", scope, owner, None)
                if isinstance(o.get("do"), list):
                    self.actions(o["do"], f"{p}[{i}].do", scope, owner, sends)
                else:
                    self.error(f"{p}[{i}].do", "needs a list of actions")
        elif t == "targeting":
            if not isinstance(v, list):
                self.error(p, 'must be a list of filters or {"filter", "range", "prefer"}')
            else:
                self.check_targeting(v, p, owner, scope)
        elif t == "movement":
            if not isinstance(v, dict):
                self.error(p, "must be a movement block")
            else:
                self.check_movement(v, p, owner, scope)
        elif t == "any":
            if is_string:
                self.expression(s[1:] if s.startswith("=") else s, p, scope, ANY)
        elif t == "for":
            if not (is_string and s in ("night", "day")):
                self.param(ParamSpec.make(ps.name, "number", 0), v, p, scope, owner, action)
        elif t == "night":
            if not (is_string and s == "current"):
                self.param(ParamSpec.make(ps.name, "int", 1), v, p, scope, owner, action)
        elif t == "slot":
            if not is_string:
                self.error(p, 'must be @name, a selector or a Studio key ("slot:Tower 3")')
            elif not (s.startswith("slot:") or s.startswith("add:") or s == "castle"):
                self.expression(s, p, scope, ENTITY, "slot")
        elif t == "types":
            if not isinstance(v, list) or any(not isinstance(x, str) for x in v):
                self.error(p, 'must be a list of labels, e.g. ["fire"]')
        elif t == "keep":
            if not isinstance(v, list) or any(not isinstance(x, str) or (ps.options is not None and x not in ps.options) for x in v):
                self.error(p, f"must be a list of {', '.join(ps.options or [])}")
        elif t == "color":
            if not is_string or (not s.startswith("=") and not try_parse_color(s)):
                self.error(p, 'must be "#rrggbb"')
        elif t in ("fx", "sound"):
            if not is_string or not s.strip() or all(_is_ws(c) for c in s):
                self.error(p, f"must be a {'effect' if t == 'fx' else 'sound'} name")

    def check_template(self, s: str, p: str, scope: Scope):
        if s.startswith("="):
            self.expression(s[1:], p, scope, TEMPLATE, "a template")
            return
        kind, rid, rpack = content_ref(s)
        if kind == "vanilla":
            pass  # needs the game (IValidationContext)
        elif kind == "local" or (kind == "pack" and rpack == _s(self.pack, "id")):
            if rid not in self.templates:
                self.error(p, f'no template "{rid}" in this pack' + suggest(rid, self.templates) + ' (a unit or entity id, or "vanilla:<id>")')
        elif kind == "pack":
            pass  # needs the game
        else:
            self.error(p, f'"{s}" isn\'t a template (a unit or entity id, "vanilla:<id>", or "=expression")')

    def num(self, n, p, scope, mn, mx):
        if n is None:
            return
        if n[0] == "e":
            self.expression(n[1], p, scope, NUMBER)
            return
        d = n[1]
        if mn is not None and d < mn:
            self.error(p, f"{cs_double(d)} is below {cs_double(mn)}")
        if mx is not None and d > mx:
            self.error(p, f"{cs_double(d)} is outside {cs_double(mn if mn is not None else 0)}..{cs_double(mx)}")

    def enum(self, v, p, options):
        if v is not None and v not in options:
            self.error(p, f"must be one of {', '.join(options)}" + suggest(v, options))

    # ---- Components

    def check_targeting(self, lst, p, owner, scope=None):
        scope = scope if scope is not None else self.scope(owner, None)
        for i, raw in enumerate(lst or []):
            t = _targeting(raw)
            tp = f"{p}[{i}]"
            if t is None or t["filter"] is None:
                self.error(tp, "needs a filter")
                continue
            self.filter_string(t["filter"], tp + ".filter", scope)
            self.num(t["range"], tp + ".range", scope, 0, None)
            self.enum(t["prefer"], tp + ".prefer", ["nearest", "farthest", "weakest", "strongest", "random", "best"])
            if t["prefer"] == "best" and t["score"] is None:
                self.error(tp + ".score", "prefer best needs a score (an expression over `it`)")
            if t["score"] is not None:
                self.expression(t["score"], tp + ".score", scope.with_(has_it=True), NUMBER, "score")

    def check_movement(self, m, p, owner, scope=None):
        if m is None:
            return
        scope = scope if scope is not None else self.scope(owner, None)
        mode = _s(m, "mode")
        self.enum(mode, p + ".mode", ["pathfind", "direct", "hold", "path"])
        self.enum(_s(m, "then"), p + ".then", ["pathfind", "hold", "destroy"])
        path = _jt(m, "path")
        if mode == "path" and path is _MISSING:
            self.error(p + ".path", "mode path needs a path (@name or [[x, z], ...])")
        self.check_path(path, p + ".path", scope)
        self.num(_lnum(m.get("speed")), p + ".speed", scope, 0, 100)
        self.num(_lnum(m.get("keepDistance")), p + ".keepDistance", scope, 0, 200)
        kite = _dict(m.get("kite"))
        if kite is not None:
            self.num(_lnum(kite.get("below")), p + ".kite.below", scope, 0, 200)
            self.num(_lnum(kite.get("for")), p + ".kite.for", scope, 0, 60)
        flee = _dict(m.get("flee"))
        if flee is not None:
            if _s(flee, "when") is not None:
                self.expression(_s(flee, "when"), p + ".flee.when", scope, BOOL, "flee.when")
            if _s(flee, "from") is not None:
                self.filter_string(_s(flee, "from"), p + ".flee.from", scope)
            if _s(flee, "to") is not None:
                self.expression(_s(flee, "to"), p + ".flee.to", scope, POSITION, "flee.to")
        wander = _dict(m.get("wander"))
        if wander is not None:
            self.num(_lnum(wander.get("radius")), p + ".wander.radius", scope, 0, 200)
        home = m.get("home")
        if isinstance(home, str) and home != "spawn":
            self.expression(home, p + ".home", scope, POSITION, "home")

    def check_path(self, path, p, scope):
        if path is _MISSING:
            return
        if isinstance(path, str):
            self.expression(path, p, scope, ENTITY, "path")
            return
        if not isinstance(path, list) or len(path) < 1 or any(not isinstance(x, list) or len(x) < 2 or len(x) > 3 for x in path):
            self.error(p, "must be @name or [[x, z] | [x, y, z], ...]")

    def check_attacks(self, lst, p, owner):
        scope = self.scope(owner, None)
        for i, a in enumerate(lst or []):
            ap = f"{p}[{i}]"
            if not isinstance(a, dict):
                self.error(ap, "is empty")
                continue
            self.num(_lnum(a.get("cooldown")), ap + ".cooldown", scope, 0.05, 600)
            self.num(_lnum(a.get("jitter")), ap + ".jitter", scope, 0, 1)
            self.num(_lnum(a.get("range")), ap + ".range", scope, 0, 200)
            self.num(_lnum(a.get("minRange")), ap + ".minRange", scope, 0, 200)
            self.enum(_s(a, "when"), ap + ".when", ["night", "always", "day"])
            self.check_targeting(_list(a.get("targeting")), ap + ".targeting", owner)
            launch, weapon, do = _dict(a.get("launch")), _s(a, "weapon"), _list(a.get("do"))
            if launch is None and weapon is None and do is None:
                self.error(ap, "needs launch, weapon or do")
            if launch is not None:
                if _s(launch, "entity") is None:
                    self.error(ap + ".launch.entity", "needs a template")
                else:
                    self.check_template(_s(launch, "entity"), ap + ".launch.entity", scope)
                frm = _list(launch.get("from"))
                if frm is not None and len(frm) != 3:
                    self.error(ap + ".launch.from", "must be [x, y, z]")
            if weapon is not None and weapon != "inherit":
                if content_ref(weapon)[0] != "vanilla":
                    self.error(ap + ".weapon", 'must be "inherit" or "vanilla:<weapon>"')
            if do is not None:
                self.actions(do, ap + ".do", self.scope(owner, self.schema.events["attack"]), owner, set())

    def check_template_fields(self, e: dict, p: str, vars_, level: Optional[_LevelCtx], place: bool):
        kind = _s(e, "kind")
        if not place and kind is None:
            self.error(p + ".kind", f"needs a kind: {', '.join(ENTITY_KIND_TEMPLATES)}")
        elif kind is not None:
            self.enum(kind, p + ".kind", ENTITY_KIND_PLACES if place else ENTITY_KIND_TEMPLATES)
        owner = _Owner(p, kind if kind is not None else "prop", vars_, level=level, rules=_list(e.get("rules")))
        scope = self.scope(owner, None)
        self.check_labels(e.get("labels"), p + ".labels")
        self.check_var_decls(e.get("vars"), p + ".vars")
        team = _s(e, "team")
        self.enum(team, p + ".team", TEAMS)
        self.num(_lnum(e.get("lifetime")), p + ".lifetime", scope, 0, 3600)
        ma = _int(e, "maxAlive")
        per_template_max = self.schema.caps.get("perTemplate", (100, 1000))[1]
        if ma is not None and (ma < 1 or ma > per_template_max):
            self.error(p + ".maxAlive", f"must be 1..{per_template_max}")
        vis = _dict(e.get("visual"))
        if vis is not None:
            if _s(vis, "tint") is not None and not try_parse_color(_s(vis, "tint")):
                self.error(p + ".visual.tint", 'must be "#rrggbb"')
            if _s(vis, "mesh") is not None and _model_ref_kind(_s(vis, "mesh")) == "invalid":
                self.error(p + ".visual.mesh", 'must be "asset:<key>", "mesh:<scenery mesh>" or "vanilla:<unit id>"')
        body = _dict(e.get("body"))
        if body is not None:
            self.num(_lnum(body.get("radius")), p + ".body.radius", scope, 0.05, 100)
            self.num(_lnum(body.get("height")), p + ".body.height", scope, 0, 100)
            self.num(_lnum(body.get("hp")), p + ".body.hp", scope, 1, 1000000)
            box = _list(body.get("box"))
            if box is not None and len(box) != 2:
                self.error(p + ".body.box", "must be [width, depth]")
            if _lnum(body.get("hp")) is not None and _b(body, "hittable") is not True:
                self.warn(p + ".body.hp", "hp only matters when hittable is true")
        if _b(e, "countsAsEnemy") is True and (_b(body, "hittable") is not True or team != "enemy"):
            self.warn(p + ".countsAsEnemy", "only a hittable enemy-team entity keeps the night open")
        pr = _dict(e.get("projectile"))
        if pr is not None:
            self.num(_lnum(pr.get("speed")), p + ".projectile.speed", scope, 0, 500)
            self.num(_lnum(pr.get("homing")), p + ".projectile.homing", scope, 0, 1)
            self.num(_lnum(pr.get("arc")), p + ".projectile.arc", scope, 0, 10)
            self.num(_lnum(pr.get("gravity")), p + ".projectile.gravity", scope, 0, 200)
            self.num(_lnum(pr.get("hitRadius")), p + ".projectile.hitRadius", scope, 0, 50)
            self.num(_lnum(pr.get("range")), p + ".projectile.range", scope, 0, 1000)
            self.enum(_s(pr, "face"), p + ".projectile.face", ["travel", "fixed", "spin"])
            self.enum(_s(pr, "onGround"), p + ".projectile.onGround", ["impact", "stick", "bounce", "pass"])
            if _s(pr, "hits") is not None:
                self.filter_string(_s(pr, "hits"), p + ".projectile.hits", scope)
            pi = _int(pr, "pierce")
            if pi is not None and pi < -1:
                self.error(p + ".projectile.pierce", "must be -1 (all) or more")
            spin = _list(pr.get("spin"))
            if spin is not None and len(spin) != 3:
                self.error(p + ".projectile.spin", "must be [x, y, z] degrees per second")
        mv = _dict(e.get("mover"))
        if mv is not None:
            mpath = _jt(mv, "path")
            self.check_path(mpath, p + ".mover.path", scope)
            self.enum(_s(mv, "mode"), p + ".mover.mode", ["loop", "pingpong", "once"])
            self.num(_lnum(mv.get("speed")), p + ".mover.speed", scope, 0, 100)
            self.num(_lnum(mv.get("wait")), p + ".mover.wait", scope, 0, 600)
            if _b(body, "walkable") is True and mpath is not _MISSING:
                self.warn(p + ".mover", "a walkable mover only snaps between poses; it can't glide along a path")
            for key, pose in (_dict(mv.get("pose")) or {}).items():
                if any(key in x for x in self.schema.inputs.values()):
                    self.error(f"{p}.mover.pose.{key}", f'"{key}" is a built-in input; name the pose something else')
                off = _list(pose.get("offset")) if isinstance(pose, dict) else None
                if off is not None and len(off) != 3:
                    self.error(f"{p}.mover.pose.{key}.offset", "must be [x, y, z]")
        ar = _dict(e.get("area"))
        if ar is not None:
            sh = _dict(ar.get("shape"))
            circle = _dict(sh.get("circle")) if sh else None
            rect = _dict(sh.get("rect")) if sh else None
            poly = _dict(sh.get("polygon")) if sh else None
            shapes = 0 if sh is None else sum(1 for x in (circle, rect, poly) if x is not None)
            if shapes != 1:
                self.error(p + ".area.shape", "needs exactly one of circle {radius}, rect {size: [w, d]} or polygon {points}")
            if circle is not None:
                self.num(_lnum(circle.get("radius")), p + ".area.shape.circle.radius", scope, 0.1, 500)
            if rect is not None and (_list(rect.get("size")) is None or len(rect["size"]) != 2):
                self.error(p + ".area.shape.rect.size", "must be [width, depth]")
            if poly is not None:
                pts = _list(poly.get("points"))
                if pts is None or len(pts) < 3 or any(not isinstance(x, list) or len(x) != 2 for x in pts):
                    self.error(p + ".area.shape.polygon.points", "must be at least 3 [x, z] points")
            if _s(ar, "watch") is not None:
                self.filter_string(_s(ar, "watch"), p + ".area.watch", scope)
            self.enum(_s(ar, "show"), p + ".area.show", ["none", "outline", "fill"])
            if _s(ar, "color") is not None and not try_parse_color(_s(ar, "color")):
                self.error(p + ".area.color", 'must be "#rrggbb"')
            self.num(_lnum(ar.get("tick")), p + ".area.tick", scope, 0.1, 600)
        elif kind == "area" and not place:
            self.error(p + ".area", "an area template needs an area block")
        pk = _dict(e.get("pickup"))
        if pk is not None:
            if _s(pk, "by") is not None:
                self.filter_string(_s(pk, "by"), p + ".pickup.by", scope)
            self.num(_lnum(pk.get("radius")), p + ".pickup.radius", scope, 0.1, 50)
            self.num(_lnum(pk.get("magnet")), p + ".pickup.magnet", scope, 0, 100)
        it = _dict(e.get("interact"))
        if it is not None:
            if _s(it, "prompt") is not None:
                self.text(_s(it, "prompt"), p + ".interact.prompt", scope.with_(extra_roles={"cost": NUMBER}))
            self.num(_lnum(it.get("cost")), p + ".interact.cost", scope, 0, 100000)
            self.enum(_s(it, "when"), p + ".interact.when", ["day", "night", "always"])
            self.num(_lnum(it.get("radius")), p + ".interact.radius", scope, 0.5, 30)
            choices = _list(it.get("choices"))
            if choices is not None:
                if len(choices) < 1 or len(choices) > 4:
                    self.error(p + ".interact.choices", "1 to 4 choices")
                ids = set()
                for i, c in enumerate(choices):
                    cid = _s(c, "id")
                    if cid is None or cid in ids:
                        self.error(f"{p}.interact.choices[{i}].id", "each choice needs its own id")
                    else:
                        ids.add(cid)
                    self.num(_lnum(c.get("cost")) if isinstance(c, dict) else None, f"{p}.interact.choices[{i}].cost", scope, 0, 100000)
        self.check_attacks(_list(e.get("attacks")), p + ".attacks", owner)
        self.check_targeting(_list(e.get("targeting")), p + ".targeting", owner)
        self.check_movement(_dict(e.get("movement")), p + ".movement", owner)
        self.check_status_refs(e.get("statuses"), p + ".statuses")
        self.check_rules(owner)

    def check_status(self, s: Optional[dict], p: str):
        if s is None:
            self.error(p, "is empty")
            return
        owner = _Owner(p, "status", rules=_list(s.get("rules")))
        scope = self.scope(owner, None)
        self.num(_lnum(s.get("duration")), p + ".duration", scope, 0.05, 36000)
        self.num(_lnum(s.get("tick")), p + ".tick", scope, 0.1, 3600)
        self.enum(_s(s, "stacking"), p + ".stacking", ["refresh", "add", "extend", "ignore"])
        ms = _int(s, "maxStacks")
        if ms is not None and (ms < 1 or ms > 100):
            self.error(p + ".maxStacks", "must be 1..100")
        for key, m in (_dict(s.get("mods")) or {}).items():
            mp = f"{p}.mods.{key}"
            m = m if isinstance(m, dict) else None
            if key == "drift":
                if _s(m, "toward") is None and _s(m, "away") is None and _s(m, "dir") is None:
                    self.error(mp, "drift needs toward, away or dir")
                for k in ("toward", "away", "dir"):
                    if _s(m, k) is not None:
                        self.expression(_s(m, k), f"{mp}.{k}", scope.with_(extra_roles={"other": ENTITY}), POSITION, k)
                if (_lnum(m.get("speed")) if m is not None else None) is None:
                    self.error(mp + ".speed", "drift needs a speed (m/s)")
                continue
            if key not in self.schema.stats:
                self.error(mp, f'unknown stat "{key}"' + suggest(key, self.schema.stats + ["drift"]))
                continue
            if m is None:
                self.error(mp, "is empty")
                continue
            n = sum(1 for k in ("mult", "add", "value") if _lnum(m.get(k)) is not None)
            if n != 1:
                self.error(mp, "needs exactly one of mult, add or value")
            dmg = key in ("damageTaken", "damageMult")
            if not dmg and (_s(m, "from") is not None or _s(m, "when") is not None or _list(m.get("types")) is not None or _lnum(m.get("chance")) is not None):
                self.error(mp, "from, types, chance and when only apply to damageTaken and damageMult")
            mod_scope = scope.with_(extra_roles={"attacker": ENTITY, "victim": ENTITY, "dist": NUMBER})
            if _s(m, "from") is not None:
                self.filter_string(_s(m, "from"), mp + ".from", scope)
            if _s(m, "when") is not None:
                self.expression(_s(m, "when"), mp + ".when", mod_scope, BOOL, "when")
            self.num(_lnum(m.get("chance")), mp + ".chance", scope, 0, 1)
        for i, f in enumerate(_strs(s.get("flags")) or []):
            if f not in self.schema.flags:
                self.error(f"{p}.flags[{i}]", f'unknown flag "{f}"' + suggest(f if isinstance(f, str) else None, self.schema.flags))
        vis = _dict(s.get("visual"))
        if vis is not None and _s(vis, "tint") is not None and not try_parse_color(_s(vis, "tint")):
            self.error(p + ".visual.tint", 'must be "#rrggbb"')
        self.check_rules(owner)

    def check_preset_def(self, pr: Optional[dict], p: str):
        if pr is None:
            return
        for key, spec in (_dict(pr.get("params")) or {}).items():
            if not _var_name_ok(key):
                self.error(f"{p}.params.{key}", "param names use letters, digits and '_'")
            t = _param_type(spec) if isinstance(spec, dict) else None
            if t is not None and t not in self.schema.param_types:
                self.error(f"{p}.params.{key}.type", f'unknown type "{t}"' + suggest(t, self.schema.param_types))
        if _s(pr, "kind") is not None:
            self.enum(_s(pr, "kind"), p + ".kind", ENTITY_KIND_PLACES)

    # ---- Levels

    def check_level(self, lv: _LevelCtx):
        logic = lv.logic
        p = lv.path
        self.check_var_decls(logic.get("vars"), p + ".vars")
        caps = _dict(logic.get("caps"))
        if caps is not None:
            for name, (dflt, mx) in self.schema.caps.items():
                v = _int(caps, name)
                if v is None:
                    continue
                if v < 1:
                    self.error(f"{p}.caps.{name}", "must be at least 1")
                elif v > mx:
                    self.error(f"{p}.caps.{name}", f"{v} is above the hard limit {mx}")
                elif v > dflt:
                    self.warn(f"{p}.caps.{name}", f"{v} is above the default {dflt}: may lag")
        level_owner = _Owner(p, "level", level_script=True, level=lv, rules=_list(logic.get("rules")))
        level_scope = self.scope(level_owner, None)
        hud_ids = set()
        for i, h in enumerate(_list(logic.get("hud")) or []):
            hp = f"{p}.hud[{i}]"
            hid = _s(h, "id")
            if hid is None or hid in hud_ids:
                self.error(hp + ".id", "each HUD line needs its own id")
            else:
                hud_ids.add(hid)
            if _s(h, "text") is not None:
                self.text(_s(h, "text"), hp + ".text", level_scope)
            if _s(h, "visible") is not None:
                self.expression(_s(h, "visible"), hp + ".visible", level_scope, BOOL, "visible")
        kas = _list(logic.get("kingActions")) or []
        if len(kas) > 3:
            self.error(p + ".kingActions", "at most 3 king actions (three keys)")
        ka_ids = set()
        for i, k in enumerate(kas):
            kp = f"{p}.kingActions[{i}]"
            kid = _s(k, "id")
            if kid is None or kid in ka_ids:
                self.error(kp + ".id", "each king action needs its own id")
            else:
                ka_ids.add(kid)
            self.enum(_s(k, "when"), kp + ".when", ["day", "night", "always"])
            uses = _dict(k.get("uses")) if isinstance(k, dict) else None
            if uses is not None:
                per = uses.get("per", "run")
                self.enum(per if isinstance(per, str) else None, kp + ".uses.per", ["day", "night", "run"])
                cnt = _int(uses, "count") if "count" in uses else 1
                if cnt is not None and cnt < 1:
                    self.error(kp + ".uses.count", "must be at least 1")
            c = _num(k, "cooldown")
            if c is not None and c < 0:
                self.error(kp + ".cooldown", "must be 0 or more")
        place_ids, names = set(), set()
        for i, pl in enumerate(_list(logic.get("places")) or []):
            pp = f"{p}.places[{i}]"
            if not isinstance(pl, dict):
                self.error(pp, "is empty")
                continue
            pid, pname, pkind = _s(pl, "id"), _s(pl, "name"), _s(pl, "kind")
            if not ids_is_valid(pid):
                self.error(pp + ".id", _ID_MSG)
            elif pid in place_ids:
                self.error(pp + ".id", f'"{pid}" is used twice')
            else:
                place_ids.add(pid)
            if pname is not None and pname != pid:
                if pname in names:
                    self.error(pp + ".name", f'another object is named "{pname}"')
                names.add(pname)
            for n, key in ((pid, "id"), (pname, "name")):
                if n is not None and "." in n:
                    parts = n.split(".")
                    self.warn(f"{pp}.{key}", f"\"{n}\" has a '.', so expressions can't name it (@{n} reads as @{parts[0]} . {parts[1]})")
            pos = _list(pl.get("pos"))
            if pos is None and pkind != "path":
                self.error(pp + ".pos", "needs a position [x, y, z]")
            elif pos is not None and (len(pos) < 2 or len(pos) > 3):
                self.error(pp + ".pos", "must be [x, z] or [x, y, z]")
            if pkind == "path":
                pts = _list(pl.get("points"))
                if pts is None or len(pts) < 2 or any(not isinstance(x, list) or len(x) < 2 or len(x) > 3 for x in pts):
                    self.error(pp + ".points", "a path needs at least 2 [x, z] or [x, y, z] points")
            vars_ = _OSet(_var_set(pl.get("vars")))
            ent = _s(pl, "entity")
            if ent is not None:
                if ent not in self.templates and content_ref(ent)[0] != "vanilla":
                    self.error(pp + ".entity", f'no template "{ent}"' + suggest(ent, self.templates))
                tv = self.template_vars.get(ent)
                if tv is not None:
                    for v in _keys(pl.get("vars")):
                        if v not in tv:
                            self.warn(f"{pp}.vars.{v}", f'"{ent}" doesn\'t declare "{v}"; it\'s added for this copy only')
                    vars_ |= tv
            elif pkind is None and "_preset" not in pl:
                self.error(pp, 'needs "entity" (a template), "preset", or an inline "kind"')
            self.check_template_fields(pl, pp, vars_, lv, place=True)
        for key, a in (_dict(logic.get("attach")) or {}).items():
            ap = f'{p}.attach["{key}"]'
            if not isinstance(a, dict):
                self.error(ap, "is empty")
                continue
            kind, err = self.attach_kind(key, lv.def_)
            if err is not None:
                self.error(ap, err)
            an = _s(a, "name")
            if an is not None:
                if not ids_is_valid(an) and not _var_name_ok(an):
                    self.error(ap + ".name", "names use letters, digits, '-' and '_'")
                if an in place_ids or an in names:
                    self.error(ap + ".name", f'another object is named "{an}"')
                else:
                    names.add(an)
            self.check_labels(a.get("labels"), ap + ".labels")
            self.check_var_decls(a.get("vars"), ap + ".vars")
            self.check_status_refs(a.get("statuses"), ap + ".statuses")
            owner = _Owner(ap, kind if kind is not None else "building", _var_set(a.get("vars")), level=lv, rules=_list(a.get("rules")))
            self.check_targeting(_list(a.get("targeting")), ap + ".targeting", owner)
            self.check_rules(owner)
        self.check_rules(level_owner)

    @staticmethod
    def attach_kind(key: str, level: dict):
        if key == "castle":
            return "building", None
        colon = key.find(":")
        prefix = key[:colon] if colon > 0 else None
        id_ = key[colon + 1:] if colon > 0 else None
        if prefix == "slot":
            return "building", None  # host slot names need the game
        if prefix == "add":
            adds = _list((_dict(level.get("buildSlots")) or {}).get("add")) or []
            if not any(_s(a, "id") == id_ for a in adds):
                return "building", f'no added slot "{id_}" on this level'
            return "building", None
        if prefix == "prop":
            props = _list(level.get("props")) or []
            if not any(_s(a, "id") == id_ for a in props):
                return "prop", f'no prop "{id_}" on this level'
            return "prop", None
        if prefix == "line":
            return "line", None
        return None, 'attach keys are "slot:<name>", "add:<id>", "prop:<id>", "line:<name>" or "castle"'

    def check_loops(self):
        graph: Dict[str, Set[str]] = {}
        for _, hears, sends in self.signal_edges:
            if hears is not None:
                graph.setdefault(hears, set()).update(sends)
        reported = set()
        for path, hears, _ in self.signal_edges:
            if hears is None or hears in reported:
                continue
            seen = set()
            stack = list(graph.get(hears, ()))
            while stack:
                s = stack.pop()
                if s == hears:
                    self.warn(path, f'the signal "{hears}" leads back to itself with no wait: this loop runs until the chain-depth cap (add a wait, a cooldown or a condition)')
                    reported.add(hears)
                    break
                if s in seen:
                    continue
                seen.add(s)
                stack.extend(graph.get(s, ()))


def validate_logic(pack_json: dict, issues: list, library_presets=None, schema: Optional[Schema] = None) -> list:
    """LogicValidator.Validate with no game context: expands presets, then checks every rule, action, component,
    status, preset and expression, appending (severity, path, message) tuples ("error" / "warning") to issues.
    Checks that need the game (vanilla units and weapons, other installed packs, host slot names) are skipped."""
    sc = _schema(schema)
    if not isinstance(pack_json, dict) or not has_logic(pack_json):
        return issues
    expanded = expand_presets(pack_json, issues, library_presets, sc)
    if expanded is None:
        return issues
    _Walker(pack_json, expanded, issues, sc).run()
    return issues
