"""Tests for agent-kit/palace_logic.py (the Python port of Model/Logic: Expr, ExprChecker, Presets, LogicValidator).
The shared vectors and fixtures are the ones tests/PalaceMod.Tests/LogicTests.cs runs; the message assertions are
copied from it. python3 -m pytest -q tests/agent-kit"""
import importlib.util
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("palace_logic", ROOT / "agent-kit" / "palace_logic.py")
pl = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pl)

SCHEMA = pl.load_schema()


def issue_str(i):
    """ValidationIssue.ToString."""
    sev, path, msg = i
    return f"{sev} {path}: {msg}"


def errors(issues):
    return "\n    ".join(issue_str(i) for i in issues if i[0] == "error")


def scope_of(default, over):
    o = dict(default)
    o.update(over or {})
    s = lambda k: None if o.get(k) is None else set(o[k])
    return pl.Scope(event=o.get("event"), level_script=bool(o.get("levelScript") or False), has_it=bool(o.get("it") or False),
                    has_spawned=bool(o.get("spawned") or False), level_vars=s("levelVars"), self_vars=s("selfVars"),
                    any_entity_vars=s("anyVars"), names=s("names"), templates=s("templates"), statuses=s("statuses"),
                    extra_roles=dict(o["extraRoles"]) if isinstance(o.get("extraRoles"), dict) else None)


class SharedExpressionCases(unittest.TestCase):
    """tests/logic-expr-cases.json, with LogicTests.SharedExpressionCases' semantics."""

    @classmethod
    def setUpClass(cls):
        cls.root = json.loads((ROOT / "tests" / "logic-expr-cases.json").read_text(encoding="utf-8"))

    def test_cases(self):
        default = self.root["defaultScope"]
        failures = []
        for c in self.root["cases"]:
            src = c["expr"]
            t, issues, node = pl.check_expr_node(src, scope_of(default, c.get("scope")), SCHEMA)
            if "error" in c:
                hit = next((i for i in issues if c["error"] in i[1]), None)
                if not issues:
                    failures.append(f'{src}: expected error "{c["error"]}", got none (type {t})')
                elif hit is None:
                    failures.append(f'{src}: expected error "{c["error"]}", got "{issues[0][1]}"')
                elif "col" in c and hit[0] + 1 != c["col"]:
                    failures.append(f'{src}: error at col {hit[0] + 1}, expected {c["col"]}')
                continue
            if issues:
                failures.append(f"{src}: unexpected error col {issues[0][0] + 1}: {issues[0][1]}")
                continue
            if "type" in c and t != c["type"]:
                failures.append(f"{src}: type {t}, expected {c['type']}")
            if "ast" in c and pl.node_str(node) != c["ast"]:
                failures.append(f"{src}: ast {pl.node_str(node)}, expected {c['ast']}")
        self.assertEqual(failures, [])

    def test_interp(self):
        failures = []
        for c in self.root["interp"]:
            if "error" in c:
                try:
                    pl.parse_interp(c["text"], SCHEMA)
                    failures.append(f"interp {c['text']}: expected error {c['error']}, got none")
                except pl.ExprError as e:
                    if c["error"] not in e.message:
                        failures.append(f"interp {c['text']}: expected error {c['error']}, got {e.message}")
                continue
            parts = pl.parse_interp(c["text"], SCHEMA)
            got = "|".join(pl.node_str(p) if isinstance(p, pl.ExprNode) else p for p in parts)
            want = "|".join(c["parts"])
            if got != want:
                failures.append(f"interp {c['text']}: {got}, expected {want}")
        self.assertEqual(failures, [])

    def test_filters(self):
        failures = []
        for c in self.root["filters"]:
            if "error" in c:
                try:
                    f = pl.parse_filter(c["filter"], SCHEMA)
                    failures.append(f"filter {c['filter']}: expected error {c['error']}, got {pl.filter_str(f)}")
                except pl.ExprError as e:
                    if c["error"] not in e.message:
                        failures.append(f"filter {c['filter']}: expected error {c['error']}, got {e.message}")
                continue
            try:
                f = pl.parse_filter(c["filter"], SCHEMA)
            except pl.ExprError as e:
                failures.append(f"filter {c['filter']}: {e.message}")
                continue
            if pl.filter_str(f) != c["ast"]:
                failures.append(f"filter {c['filter']}: {pl.filter_str(f)}, expected {c['ast']}")
        self.assertEqual(failures, [])


class ExpressionApi(unittest.TestCase):
    def test_parse_errors_carry_columns(self):
        with self.assertRaises(pl.ExprError) as cm:
            pl.parse_expr("1 +")
        self.assertEqual((cm.exception.message, cm.exception.col), ("unexpected end of expression", 3))

    def test_number_forms(self):
        self.assertEqual(pl.node_str(pl.parse_expr("1.50 + -2")), "(1.5 + -2)")
        self.assertEqual(pl.cs_double(1e16), "10000000000000000")
        self.assertEqual(pl.cs_double(1e17), "1E+17")
        self.assertEqual(pl.cs_double(123456789012345.0), "123456789012345")
        self.assertEqual(pl.cs_double(0.0001), "0.0001")
        self.assertEqual(pl.cs_double(0.00001), "1E-05")
        self.assertEqual(pl.cs_double(1.4), "1.4")

    def test_suggest(self):
        self.assertEqual(pl.suggest("nearst", ["nearest", "farthest"]), ' (did you mean "nearest"?)')
        self.assertEqual(pl.suggest("zz", ["nearest"]), "")
        self.assertEqual(pl.distance("ab", "ba"), 1)


class DocExamples(unittest.TestCase):
    """LogicTests.EveryDocExampleValidates."""

    def test_every_doc_example_validates(self):
        files = sorted((ROOT / "tests" / "logic-examples").glob("*.json"))
        self.assertGreaterEqual(len(files), 23)
        failures = []
        for file in files:
            fx = json.loads(file.read_text(encoding="utf-8"))
            pack = fx["pack"]
            issues = pl.validate_logic(pack, [], schema=SCHEMA)
            errs = errors(issues)
            if errs:
                failures.append(f"{file.name}:\n    {errs}")
            level = pack["levels"][0]
            logic = level.get("logic") or {}
            names = {p.get("id") for p in logic.get("places") or []} | {a.get("name") for a in (logic.get("attach") or {}).values()}
            names.discard(None)
            scope = pl.Scope(event="levelStart", level_script=True, level_vars=set((logic.get("vars") or {}).keys()), names=names)
            for step in (fx.get("play") or {}).get("steps") or []:
                ex = step.get("expect")
                if isinstance(ex, str):
                    t, lst = pl.check_expr(ex, scope, SCHEMA)
                    if lst:
                        failures.append(f'{file.name} play expect "{ex}": col {lst[0][0] + 1}: {lst[0][1]}')
                    elif not pl.accepts("bool", t):
                        failures.append(f'{file.name} play expect "{ex}" is a {t}')
        self.assertEqual(failures, [], "\n  ".join(failures))


def validate_level(logic, extra_pack=None):
    """LogicTests.ValidateLevel."""
    root = {"format": "palace-builder", "version": 3, "id": "a.b", "levels": [{"id": "l", "host": "Nordfels", "logic": logic}]}
    root.update(extra_pack or {})
    return pl.validate_logic(root, [], schema=SCHEMA)


class ValidatorMessages(unittest.TestCase):
    def has(self, issues, text):
        all_ = "\n    ".join(issue_str(i) for i in issues)
        self.assertTrue(any(text in issue_str(i) for i in issues), f'expected "{text}" in\n    {all_}')

    def test_messages_have_paths_and_columns(self):
        logic = json.loads("""{ "vars": { "souls": 0 }, "places": [{ "id": "a", "kind": "logic", "pos": [0,0,0] }, { "id": "b", "kind": "logic", "pos": [0,0,0] },
            { "id": "c", "kind": "logic", "pos": [0,0,0] }, { "id": "d", "kind": "logic", "pos": [0,0,0], "rules": [{ "when": "dusk" },
            { "when": "dusk", "if": "night > 1 and $soul > 2", "do": [] }] }],
            "rules": [{ "when": "levelStart", "do": [{ "do": "damage", "target": "other", "amount": 1 }] },
                        { "when": "death", "do": [] },
                        { "when": "dusk", "do": [{ "do": "pull" }, { "do": "spawn", "entity": "gobln" }, { "do": "setVar", "name": "sols", "value": 1 }] }],
            "attach": { "add:nope": {}, "tower": {} } }""")
        issues = validate_level(logic, {"units": [{"id": "goblin", "base": "vanilla:E Melee"}]})
        self.has(issues, 'levels[0].logic.places[3].rules[1].if: col 15: unknown variable "$soul" (did you mean "$souls"?)')
        self.has(issues, 'levels[0].logic.rules[0].do[0].target: col 1: "other" is not defined for the event "levelStart"')
        self.has(issues, 'levels[0].logic.rules[1].when: the level script hears "death" only from entities: give "of"')
        self.has(issues, 'levels[0].logic.rules[2].do[0].do: unknown action "pull"')
        self.has(issues, "made with a newer PalaceMod?")
        self.has(issues, 'levels[0].logic.rules[2].do[1].entity: no template "gobln" in this pack (did you mean "goblin"?)')
        self.has(issues, 'levels[0].logic.rules[2].do[2].name: unknown variable "$sols" (did you mean "souls"?)')
        self.has(issues, 'levels[0].logic.attach["add:nope"]: no added slot "nope"')
        self.has(issues, 'levels[0].logic.attach["tower"]: attach keys are')

    def test_checks_components_and_caps(self):
        issues = validate_level(
            json.loads("""{ "caps": { "entities": 900, "units": 5000 },
                "kingActions": [{ "id": "a" }, { "id": "b" }, { "id": "c" }, { "id": "d" }] }"""),
            json.loads("""{ "entities": [{ "id": "tm", "kind": "projectile", "projectile": { "homing": 1.4, "face": "sideways", "hits": "foe and Towr" } },
                               { "id": "zone", "kind": "area" }],
                "statuses": [{ "id": "s", "mods": { "moveSped": { "mult": 0.5 }, "moveSpeed": { "mult": 1, "add": 1 } }, "flags": ["stuned"] }] }"""))
        self.has(issues, "warning levels[0].logic.caps.entities: 900 is above the default 300: may lag")
        self.has(issues, "error levels[0].logic.caps.units: 5000 is above the hard limit 1000")
        self.has(issues, "levels[0].logic.kingActions: at most 3 king actions")
        self.has(issues, "entities[0].projectile.homing: 1.4 is outside 0..1")
        self.has(issues, "entities[0].projectile.face: must be one of travel, fixed, spin")
        self.has(issues, 'entities[0].projectile.hits: col 9: unknown filter term "Towr" (did you mean "Tower"?)')
        self.has(issues, "entities[1].area: an area template needs an area block")
        self.has(issues, 'statuses[0].mods.moveSped: unknown stat "moveSped" (did you mean "moveSpeed"?)')
        self.has(issues, "statuses[0].mods.moveSpeed: needs exactly one of mult, add or value")
        self.has(issues, 'statuses[0].flags[0]: unknown flag "stuned" (did you mean "stunned"?)')

    def test_warns_on_signal_loops(self):
        issues = validate_level(json.loads("""{ "places": [
            { "id": "a", "kind": "logic", "pos": [0,0,0], "rules": [{ "when": { "on": "signal", "name": "ping" }, "do": [{ "do": "signal", "name": "pong", "to": "all" }] }] },
            { "id": "b", "kind": "logic", "pos": [0,0,0], "rules": [{ "when": { "on": "signal", "name": "pong" }, "do": [{ "do": "signal", "name": "ping", "to": "all" }] }] },
            { "id": "c", "kind": "logic", "pos": [0,0,0], "rules": [{ "when": { "on": "signal", "name": "tick" }, "do": [{ "do": "wait", "seconds": 1 }, { "do": "signal", "name": "tick", "to": "all" }] }] }] }"""))
        self.has(issues, 'the signal "ping" leads back to itself with no wait')
        self.assertFalse(any('"tick" leads back' in i[2] for i in issues), "a wait breaks the loop")


class PresetTests(unittest.TestCase):
    def test_expansion_substitutes_typed_values(self):
        pack = {"id": "a.b", "units": [{"id": "slime", "base": "vanilla:E Slime", "rules": [{"preset": "split-on-death", "with": {"count": 3}}]}]}
        issues = []
        x = pl.expand_presets(pack, issues, schema=SCHEMA)
        self.assertEqual(errors(issues), "", "expands")
        rule = x["units"][0]["rules"][0]
        self.assertEqual(rule["if"], "self.$gen < 3", "depth default substituted")
        spawn = rule["do"][0]
        self.assertIs(type(spawn["count"]), int, "a whole-string {count} keeps its number type")
        self.assertEqual(spawn["count"], 3)
        self.assertEqual(spawn["entity"], "=self.template", "template default")
        self.assertIn("gen", x["units"][0]["vars"], "preset vars merged")
        self.assertEqual(rule["_src"], "units[0].rules[0] (preset split-on-death).rules[0]")
        self.assertEqual(errors(pl.validate_logic(pack, [], schema=SCHEMA)), "", "validates")
        self.assertNotIn("_src", pack["units"][0]["rules"][0], "the input pack is untouched")

    def test_errors_point_at_the_use(self):
        pack = {"id": "a.b", "units": [{"id": "slime", "base": "vanilla:E Slime", "rules": [
            {"preset": "split-on-deth"},
            {"preset": "split-on-death", "with": {"count": "two", "cuont": 1}}]}]}
        all_ = "\n".join(issue_str(i) for i in pl.validate_logic(pack, [], schema=SCHEMA))
        self.assertIn('units[0].rules[0]: preset "split-on-deth" not found (did you mean "split-on-death"?)', all_)
        self.assertIn('units[0].rules[1].with.count: "count" expects a number', all_)
        self.assertIn('units[0].rules[1].with.cuont: preset "split-on-death" has no param "cuont" (did you mean "count"?)', all_)

    def test_built_in_presets_all_validate(self):
        self.assertGreaterEqual(len(SCHEMA.presets), 15)
        for preset in SCHEMA.presets:
            with_ = {}
            if "status" in (preset.get("params") or {}):
                with_["status"] = "warded"
            pack = {"id": "a.b", "units": [], "statuses": [{"id": "warded", "duration": 1}],
                    "levels": [{"id": "l", "host": "Nordfels", "logic": {"vars": {"count": 0}}}]}
            if preset.get("kind") is not None:
                pack["levels"][0]["logic"]["places"] = [{"id": "p", "preset": preset["id"], "with": with_, "pos": [0, 0, 0]},
                                                        {"id": "path", "kind": "path", "points": [[0, 0], [5, 5]]}]
            else:
                pack["units"] = [{"id": "u", "base": "vanilla:E Slime", "rules": [{"preset": preset["id"], "with": with_}]}]
            self.assertEqual(errors(pl.validate_logic(pack, [], schema=SCHEMA)), "", f"built-in preset {preset['id']}")
            self.assertTrue(preset.get("name") and preset.get("description") and preset.get("category"), preset["id"])
            for k, v in (preset.get("params") or {}).items():
                self.assertIsNotNone(v.get("description"), f"{preset['id']}.{k} has a description")

    def test_library_presets(self):
        lib = {"mine": {"id": "mine", "params": {"n": {"type": "int", "default": 2}},
                        "rules": [{"when": "death", "do": [{"do": "spawn", "entity": "=self.template", "count": "{n}"}]}]}}
        pack = {"id": "a.b", "units": [{"id": "u", "base": "vanilla:E Slime", "rules": [{"preset": "mine"}]}]}
        issues = []
        x = pl.expand_presets(pack, issues, library_presets=lib, schema=SCHEMA)
        self.assertEqual(issues, [])
        self.assertEqual(x["units"][0]["rules"][0]["do"][0]["count"], 2)


class LoweringTests(unittest.TestCase):
    """LogicTests.LoweringIsExact (the JSON half)."""

    def test_lowering_is_exact(self):
        r = lambda s: json.loads(s)
        self.assertEqual(pl.spawn_on_death_unit(r('{"when":"death","do":[{"do":"spawn","entity":"x","at":"self","elite":"inherit"}]}')), "x")
        self.assertEqual(pl.spawn_on_death_unit(r('{"id":"r","when":{"on":"death"},"do":[{"do":"spawn","entity":"x","at":"self","elite":"inherit"}]}')), "x")
        self.assertIsNone(pl.spawn_on_death_unit(r('{"when":"death","if":"chance(0.5)","do":[{"do":"spawn","entity":"x","at":"self","elite":"inherit"}]}')))
        self.assertIsNone(pl.spawn_on_death_unit(r('{"when":"death","do":[{"do":"spawn","entity":"x","count":2,"at":"self","elite":"inherit"}]}')))
        self.assertIsNone(pl.spawn_on_death_unit(r('{"when":{"on":"death","of":"foe"},"do":[{"do":"spawn","entity":"x","at":"self","elite":"inherit"}]}')))
        self.assertIsNone(pl.spawn_on_death_unit(r('{"when":"death","do":[{"do":"spawn","entity":"=self.template","at":"self","elite":"inherit"}]}')))
        rules = r('''[{"when":"death","do":[{"do":"spawn","entity":"me","at":"self","elite":"inherit"}]},
                     {"when":"death","do":[{"do":"spawn","entity":"prop1","at":"self","elite":"inherit"}]},
                     {"when":"death","do":[{"do":"spawn","entity":"b","at":"self","elite":"inherit"}]}]''')
        self.assertEqual(pl.lowered_rule_index(rules, "me", lambda u: u == "b"), 2)


if __name__ == "__main__":
    unittest.main()


class UnknownFieldTests(unittest.TestCase):
    def test_unknown_fields_and_body_tags(self):
        pack = {"format": "palace-builder", "version": 3, "id": "a.b", "levels": [{"id": "l", "host": "Nordfels", "logic": {
            "bogus": 1, "places": [{"id": "t", "kind": "prop", "pos": [0, 0, 0], "body": {"hitable": True, "hittable": True, "tags": ["Biulding"]}}]}}]}
        issues = pl.validate_logic(pack, [])
        text = [f"{s} {p}: {m}" for s, p, m in issues]
        self.assertIn('warning levels[0].logic.bogus: unknown field "bogus" (it\'s kept but does nothing)', text)
        self.assertTrue(any('body.hitable: unknown field "hitable" (did you mean "hittable"?)' in t for t in text), text)
        self.assertTrue(any('body.tags[0]: unknown game tag "Biulding" (did you mean "Building"?)' in t for t in text), text)


class PlaceKindTests(unittest.TestCase):
    def test_place_takes_its_templates_kind(self):
        pack = {"format": "palace-builder", "version": 3, "id": "a.b", "entities": [{"id": "gem", "kind": "pickup", "pickup": {"by": "king"}}],
                "levels": [{"id": "l", "host": "Nordfels", "logic": {"places": [{"id": "g", "entity": "gem", "pos": [0, 0, 0],
                                                                                  "rules": [{"when": "pickup", "do": []}]}]}}]}
        issues = pl.validate_logic(pack, [])
        self.assertFalse([m for _, _, m in issues if "fires on" in m], issues)


def validate_rule(do, names=None):
    """LogicParamTests.Validate: one dusk rule doing `do`."""
    root = {"format": "palace-builder", "version": 3, "id": "a.b", "levels": [{"id": "l", "host": "Nordfels", "logic": {
        "vars": {"n": 0, "seen": []}, "rules": [{"when": "dusk", "do": do}]}}]}
    return pl.validate_logic(root, [], schema=SCHEMA, names=names)


def errs(issues):
    return "\n    ".join(issue_str(i) for i in issues if i[0] == "error")


class ParamFormTests(unittest.TestCase):
    """LogicParamTests: params are read by ParamForms (the runtime's rule) in check too."""

    def test_classify(self):
        self.assertEqual(pl.classify_param("color", "glow", "#40c0ff"), ("Literal", "#40c0ff"))
        self.assertEqual(pl.classify_param("color", "tint", "=$c"), ("Expression", "$c"))
        self.assertEqual(pl.classify_param("slot", "slot", "it"), ("Expression", "it"))
        self.assertEqual(pl.classify_param("slot", "slot", "add:h1")[0], "Literal")
        self.assertEqual(pl.classify_param("bool", "visible", "=true"), ("Expression", "true"))
        self.assertEqual(pl.classify_param("text", "text", "Hi {night}")[0], "Text")
        self.assertEqual(pl.classify_param("signal", "name", "open")[0], "Text")
        self.assertEqual(pl.classify_param("var", "name", "=x")[0], "Literal")
        self.assertEqual(pl.classify_param("entities", "to", "all")[0], "Literal")
        self.assertEqual(pl.classify_param("number", "amount", 3)[0], "Json")
        self.assertEqual(pl.classify_param("actions", "then", [])[0], "Structured")

    def test_colours(self):
        for ok in ("#40c0ff", "40c0ff", "#4cf", "#40c0ff80", "red", "Orange", " grey "):
            self.assertTrue(pl.parse_color(ok), ok)
        for bad in ("", "#40c0f", "nope", "#ggg", None):
            self.assertFalse(pl.parse_color(bad), bad)

    def test_check_accepts_what_the_game_reads(self):
        good = [
            {"do": "setVisual", "glow": "#40c0ff", "tint": "red"},
            {"do": "floatText", "text": "hi {night}", "color": "#4cf", "at": "vec(0, 0)"},
            {"do": "setVisual", "tint": "='#40c0ff'", "glow": True},
            {"do": "building", "slot": "=nearest('unbuilt')", "op": "build"},
            {"do": "building", "target": "all('unbuilt and type:\"Defense Tower\"')", "op": "build"},
        ]
        for a in good:
            self.assertEqual(errs(validate_rule([a])), "", a)
        self.assertIn("must be a colour", errs(validate_rule([{"do": "setVisual", "glow": "#40c0fz"}])))

    def test_effect_and_sound_names(self):
        rule = [{"do": "effect", "fx": "spark"}, {"do": "sound", "sound": "coinSpnd"}, {"do": "effect", "fx": "SMOKE"}]
        self.assertEqual(errs(validate_rule(rule)), "", "offline: not checked")
        e = errs(validate_rule(rule, {"effects": ["smoke", "explosion"], "sounds": ["coinSpend"]}))
        self.assertIn('no game effect "spark"', e)
        self.assertIn('no game sound "coinSpnd"', e)
        self.assertIn('did you mean "coinSpend"', e)
        self.assertNotIn("SMOKE", e)

    def test_foreach_as_and_outer(self):
        ok = [{"do": "forEach", "in": "all('foe')", "as": "m", "actions": [
            {"do": "listAdd", "target": "level", "name": "$seen", "value": "m"},
            {"do": "listAdd", "target": "level", "name": "$seen", "value": "outer"}]}]
        self.assertEqual(errs(validate_rule(ok)), "")
        self.assertIn("already a name", errs(validate_rule([{"do": "forEach", "in": "all('foe')", "as": "night", "actions": []}])))
        self.assertIn('"outer"', errs(validate_rule([{"do": "listAdd", "target": "level", "name": "$seen", "value": "outer"}])))
