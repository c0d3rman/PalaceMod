"""palace.py's behaviour-engine ops (entity, status, place, rule, wire, var, attach, king_action, hud) and the kit's
behaviour example: python3 -m pytest tests/agent-kit"""
import importlib.util
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("palace", ROOT / "agent-kit" / "palace.py")
palace = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(palace)

sys.path.insert(0, str(Path(__file__).parent))
from test_palace import FakeRef  # noqa: E402


def fresh(level_id="grove"):
    data = {"format": "palace-builder", "version": 3, "id": "library", "units": [], "waveSets": [],
            "levels": [{"id": level_id, "host": "Nordfels"}]}
    return data, palace.Ops(None, FakeRef(), data, level_id)


def fixture(n):
    return json.loads(next((ROOT / "tests" / "logic-examples").glob(f"{n:02d}-*.json")).read_text())["pack"]


def logic_issues(data):
    try:
        return palace.logic_issues(data)
    except palace.Fail:
        return None   # palace_logic.py not there yet


class BehaviourExample(unittest.TestCase):
    """The doc's done-check: palace.py apply builds examples 1, 13 and 22 from ops."""

    def setUp(self):
        self.data, ops = fresh()
        ops.run(json.loads((ROOT / "agent-kit" / "examples" / "behaviour.ops.json").read_text()))
        self.level = self.data["levels"][0]

    def test_example_1_homing_trees(self):
        fx = fixture(1)
        self.assertEqual([e for e in self.data["entities"] if e["id"] == "tree-missile"][0]["projectile"], fx["entities"][0]["projectile"])
        self.assertEqual([e for e in self.data["entities"] if e["id"] == "tree-missile"][0]["rules"], fx["entities"][0]["rules"])
        treant = [u for u in self.data["units"] if u["id"] == "treant"][0]
        self.assertEqual(treant["attacks"], fx["units"][0]["attacks"])

    def test_example_13_mercenary_camp(self):
        fx = fixture(13)
        camp = [p for p in self.level["logic"]["places"] if p["id"] == "camp"][0]
        want = fx["levels"][0]["logic"]["places"][0]
        self.assertEqual(camp["rules"], want["rules"])
        self.assertEqual(camp["area"]["shape"], want["area"]["shape"])
        self.assertEqual(camp["pos"], [10.0, 2, -10.0])   # at -> ground height
        merc = [u for u in self.data["units"] if u["id"] == "merc"][0]
        self.assertEqual([{k: v for k, v in r.items() if k != "note"} for r in merc["rules"]], fx["units"][0]["rules"])

    def test_example_22_soul_counter(self):
        fx = fixture(22)["levels"][0]["logic"]
        lg = self.level["logic"]
        self.assertEqual(lg["vars"], fx["vars"])
        self.assertEqual(lg["hud"], fx["hud"])
        self.assertEqual([{k: v for k, v in r.items() if k != "id"} for r in lg["rules"][:2]], fx["rules"])

    def test_the_example_checks_clean(self):
        issues = logic_issues(self.data)
        if issues is None:
            self.skipTest("palace_logic.py not present")
        self.assertEqual([i for i in issues if i["severity"] == "error"], [])


class Ops(unittest.TestCase):
    def test_rule_owners_and_replace_by_id(self):
        data, ops = fresh()
        ops.run([{"op": "unit", "id": "slime", "base": "vanilla:E Slime"},
                 {"op": "rule", "on": "unit:slime", "rule": {"id": "a", "when": "death", "do": []}},
                 {"op": "rule", "on": "unit:slime", "rule": {"id": "a", "when": "spawn", "do": []}},
                 {"op": "rule", "on": "slot:Tower 3", "rule": {"when": "hit", "do": []}},
                 {"op": "rule", "on": "level", "rule": {"when": "dusk", "do": []}}])
        self.assertEqual(data["units"][0]["rules"], [{"id": "a", "when": "spawn", "do": []}])
        self.assertEqual(data["levels"][0]["logic"]["attach"]["slot:Tower 3"]["rules"][0]["when"], "hit")
        self.assertEqual(len(data["levels"][0]["logic"]["rules"]), 1)
        ops.run([{"op": "remove_rule", "on": "unit:slime", "id": "a"}])
        self.assertEqual(data["units"][0]["rules"], [])
        with self.assertRaisesRegex(palace.Fail, "no unit 'ghost'"):
            ops.run([{"op": "rule", "on": "unit:ghost", "rule": {"when": "death", "do": []}}])

    def test_wire_writes_both_rules(self):
        data, ops = fresh()
        ops.run([{"op": "place", "id": "lever", "kind": "logic", "at": [0, 0], "interact": {"prompt": "Pull"}},
                 {"op": "place", "id": "gate", "kind": "prop", "at": [5, 0]},
                 {"op": "wire", "from": "place:lever", "event": "interact", "to": "gate", "input": "disable"},
                 {"op": "wire", "from": "place:lever", "event": "interact", "to": "gate", "input": "ring",
                  "do": [{"do": "message", "text": "Ding"}]}])
        lever, gate = data["levels"][0]["logic"]["places"]
        self.assertEqual(lever["rules"][0]["do"], [{"do": "signal", "name": "disable", "to": "@gate"}])
        self.assertEqual(len(gate.get("rules") or []), 1)   # only the custom input got a listener
        self.assertEqual(gate["rules"][0]["when"], {"on": "signal", "name": "ring"})

    def test_place_preset_path_and_var(self):
        data, ops = fresh()
        ops.run([{"op": "place", "id": "t1", "preset": "timer", "with": {"seconds": 3}, "at": [0, 0]},
                 {"op": "place", "id": "route", "kind": "path", "points": [[0, 0], [10, 5]]},
                 {"op": "var", "name": "souls", "value": 0},
                 {"op": "place", "id": "box", "kind": "logic", "at": [1, 1]},
                 {"op": "var", "on": "place:box", "name": "n", "value": 2},
                 {"op": "attach", "key": "castle", "name": "keep", "labels": ["home"]},
                 {"op": "king_action", "id": "horn", "name": "Horn"},
                 {"op": "logic", "caps": {"entities": 500}}])
        lg = data["levels"][0]["logic"]
        self.assertEqual(lg["places"][0]["preset"], "timer")
        self.assertEqual(lg["places"][1]["points"], [[0, 0], [10, 5]])
        self.assertEqual(lg["vars"], {"souls": 0})
        self.assertEqual(lg["places"][2]["vars"], {"n": 2})
        self.assertEqual(lg["attach"]["castle"], {"name": "keep", "labels": ["home"]})
        self.assertEqual(lg["caps"], {"entities": 500})
        with self.assertRaisesRegex(palace.Fail, "at most 3"):
            ops.run([{"op": "king_action", "id": f"k{i}"} for i in range(3)])

    def test_attach_keeps_interact_and_pay_and_rejects_unknown_fields(self):
        data, ops = fresh()
        pay = [{"var": "$wood", "amount": 2, "label": "wood", "level": 1}]
        ops.run([{"op": "var", "name": "wood", "value": 0},
                 {"op": "attach", "key": "slot:Tower 3", "name": "t3", "interact": {"prompt": "Pray", "cost": 1}, "pay": pay}])
        a = data["levels"][0]["logic"]["attach"]["slot:Tower 3"]
        self.assertEqual(a["interact"], {"prompt": "Pray", "cost": 1})
        self.assertEqual(a["pay"], pay)
        with self.assertRaisesRegex(palace.Fail, "attach doesn't take 'interract'"):
            ops.run([{"op": "attach", "key": "castle", "interract": {}}])
        with self.assertRaisesRegex(palace.Fail, "pay .* goes on a build slot"):
            ops.run([{"op": "attach", "key": "line:West", "pay": pay}])
        issues = logic_issues(data)
        if issues is not None:
            self.assertEqual([i for i in issues if i["severity"] == "error"], [])
            self.assertEqual([i for i in issues if "unknown field" in i.get("message", "")], [])

    def test_slot_pay_level_only_on_slots(self):
        data, ops = fresh()
        ops.run([{"op": "var", "name": "wood", "value": 0},
                 {"op": "place", "id": "stall", "kind": "prop", "at": [0, 0],
                  "interact": {"pay": [{"var": "$wood", "amount": 1, "level": 2}]}}])
        issues = logic_issues(data)
        if issues is None:
            self.skipTest("palace_logic.py not present")
        self.assertTrue(any("level is for a build slot's pay" in i["message"] for i in issues), issues)

    def test_migrates_v2_behaviours(self):
        pk = {"format": "palace-builder", "version": 2, "units": [{"id": "b", "behaviours": [{"type": "SpawnOnDeath", "unit": "m"}]}]}
        palace.migrate_pack(pk)
        self.assertEqual(pk["version"], 3)
        self.assertEqual(pk["units"][0], {"id": "b", "rules": [{"when": "death", "do": [{"do": "spawn", "entity": "m", "at": "self", "elite": "inherit"}]}]})


class Reference(unittest.TestCase):
    def test_logic_reference_is_current(self):
        schema = json.loads((ROOT / "agent-kit" / "logic-schema.json").read_text())
        have = (ROOT / "agent-kit" / "logic-reference.md").read_text()
        self.assertEqual(have, palace.logic_reference_markdown(schema),
                         "agent-kit/logic-reference.md is stale: python3 agent-kit/palace.py logic doc --kit")


class WaveEstimates(unittest.TestCase):
    class Ref:
        def unit(self, uid):
            return {"id": uid, "name": uid, "maxHp": 10, "flying": False, "large": False, "tags": []}

    def test_death_spawns_count_toward_the_night(self):
        data = {"units": [{"id": "boss", "base": "vanilla:E Ogre", "stats": {"maxHp": 100},
                           "rules": [{"when": "death", "do": [{"do": "spawn", "entity": "imp", "count": 2}, {"do": "gold", "amount": 5}]}]},
                          {"id": "imp", "base": "vanilla:E Racer", "stats": {"maxHp": 20}},
                          {"id": "slime", "base": "vanilla:E Slime", "stats": {"maxHp": 8},
                           "rules": [{"when": "death", "if": "self.$gen < 2", "do": [{"do": "spawn", "entity": "=self.template", "count": 2}]}]}]}
        ws = {"nights": [{"spawns": [{"unit": "boss", "count": 1}, {"unit": "slime", "count": 1}]}]}
        row = palace.wave_summary(self.Ref(), data, ws)[0]
        self.assertEqual(row["enemies"], 1 + 2 + 1 + 2 + 4)        # boss + 2 imps; slime + 2 + 4 (two generations)
        self.assertEqual(row["effectiveHp"], 100 + 40 + 8 * 7)
        self.assertEqual(row["gold"], 5)


if __name__ == "__main__":
    unittest.main()
