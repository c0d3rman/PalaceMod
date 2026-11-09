"""Unit tests for agent-kit/palace.py (stdlib only): python3 -m unittest discover -s tests/agent-kit"""
import base64
import importlib.util
import struct
import unittest
from pathlib import Path

_spec = importlib.util.spec_from_file_location("palace", Path(__file__).resolve().parents[2] / "agent-kit" / "palace.py")
palace = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(palace)


class FakeRef:
    """A host map 100 x 80 m from (-50, -40), ground at y 2, castle at the origin."""

    def __init__(self):
        w, h = 50, 40
        self._map = {
            "grid": {"origin": [-50, -40], "cell": 2, "size": [w, h],
                     "heights": base64.b64encode(struct.pack(f"<{w * h}h", *([200] * (w * h)))).decode(),
                     "walk": ["w" * w] * h},
            "slots": [{"name": "Castle", "castle": True, "pos": [0, 2, 0]}],
            "spawnLines": [{"name": "north", "ground": True, "large": True}],
            "scenery": [],
        }
        self.scenery = {"meshes": []}

    def map(self, scene):
        return self._map


def level(flat=True, sculpted=False):
    lv = {"id": "t", "host": "Test", "base": "flat" if flat else "map"}
    ops = palace.Ops(None, FakeRef(), {"levels": [lv], "waveSets": []}, "t")
    if flat:
        ops.op_terrain_init({"op": "terrain_init", "origin": [-20, -10], "size": [20, 10], "cell": 2, "y": 1})
        if sculpted:
            ops.op_terrain({"op": "terrain", "mode": "raise", "height": 3, "circle": {"center": [0, 0], "radius": 4}})
    return lv, ops


class Coordinates(unittest.TestCase):
    def test_coords_forms(self):
        for words in (["-42,10", "5,6"], ["-42", "10", "5", "6"], [" -42,10", "5,6"], ["-42, 10", "5, 6"], ["-42,10,5,6"]):
            self.assertEqual(palace.coords(words, 4, "x"), [-42, 10, 5, 6], words)

    def test_coords_wrong_count(self):
        with self.assertRaisesRegex(palace.Fail, "expected 4 numbers, got '-42,10'"):
            palace.coords([" -42,10"], 4, "paths --between")

    def test_paths_between_parses_negative_pairs(self):
        seen = {}
        palace.cmd_paths, real = (lambda env, args: seen.setdefault("v", palace.coords(args.between, 4, "x"))), palace.cmd_paths
        try:
            palace.main(["--game", "/nonexistent", "paths", "--between", "-42,10", "-5,-6"])
        finally:
            palace.cmd_paths = real
        self.assertEqual(seen["v"], [-42, 10, -5, -6])


class OffTheGround(unittest.TestCase):
    def test_spawn_line_off_sculpted_field(self):
        lv, ops = level(sculpted=True)
        with self.assertRaisesRegex(palace.Fail, r"at \[900, 10\] is off the flat field \(x -20..20, z -10..10\): x 900 is east of it"):
            ops.run([{"op": "add_spawn_line", "id": "far", "at": [900, 10]}])

    def test_slot_off_map_ground(self):
        lv, ops = level(flat=False)
        with self.assertRaisesRegex(palace.Fail, r"z -90 is south of it"):
            ops.pos(lv, {"at": [0, -90]})

    def test_flyer_line_and_props_may_stand_off_the_field(self):
        lv, ops = level(sculpted=True)
        self.assertEqual(ops.pos(lv, {"at": [900, 10]}, strict=False), [900, 2, 10])   # castle height
        self.assertEqual(ops.pos(lv, {"at": [40, 0]}, strict=False), [40, 2, 0])       # the host's ground

    def test_malformed_points(self):
        lv, ops = level()
        for at in ([1], "3,4", [1, "a"], [1, 2, 3]):
            with self.assertRaisesRegex(palace.Fail, r"at is \[x, z\] \(numbers\)"):
                ops.pos(lv, {"at": at})
        with self.assertRaisesRegex(palace.Fail, "circle needs center"):
            palace.Shape.from_op({"circle": {"centre": [0, 0], "radius": 3}})

    def test_missing_field_names_it(self):
        lv, ops = level()
        with self.assertRaisesRegex(palace.Fail, r"ops\[0\] \(add_spawn_line\): missing field 'id'"):
            ops.run([{"op": "add_spawn_line", "at": [0, 0]}])


class TerrainCodec(unittest.TestCase):
    def test_round_trip(self):
        lv, ops = level(sculpted=True)
        ops.op_paint({"op": "paint", "material": "road", "rect": {"min": [-5, -5], "max": [5, 5]}})
        ops.op_block({"op": "block", "circle": {"center": [10, 0], "radius": 3}})
        t = palace.Terrain(lv["terrain"])
        before = dict(lv["terrain"])
        t.store(lv)
        self.assertEqual(lv["terrain"], before)
        self.assertGreater(t.height_at(0, 0), 1.0)
        self.assertEqual(t.material_at(0, 0), palace.Terrain.MATERIALS.index("road"))
        self.assertEqual(sum(t.blocked), sum(palace.Terrain(lv["terrain"]).blocked))
        self.assertEqual(palace.Terrain.unbits(base64.b64encode(palace.zlib.compress(bytes([0b10100001]))[2:-4]).decode(), 8),
                         bytearray([1, 0, 0, 0, 0, 1, 0, 1]))

    def test_ground_y_sees_terrain_edits(self):
        lv, ops = level()
        self.assertEqual(ops.ground_y(lv, 0, 0), 1.0)
        ops.op_terrain({"op": "terrain", "mode": "set", "height": 2, "rect": {"min": [-4, -4], "max": [4, 4]}})
        self.assertEqual(ops.ground_y(lv, 0, 0), 3.0)


class PropIds(unittest.TestCase):
    def test_ids_fill_gaps_and_survive_removals(self):
        lv, ops = level()
        ops.run([{"op": "add_prop", "mesh": "Rock A", "at": [0, 0]} for _ in range(3)] +
                [{"op": "remove_prop", "id": "rock-a-2"}, {"op": "add_prop", "mesh": "Rock A", "at": [0, 0]},
                 {"op": "add_prop", "mesh": "Rock A", "at": [0, 0]}])
        self.assertEqual([p["id"] for p in lv["props"]], ["rock-a-1", "rock-a-3", "rock-a-2", "rock-a-4"])
        with self.assertRaisesRegex(palace.Fail, "exists"):
            ops.run([{"op": "add_prop", "id": "rock-a-4", "mesh": "Rock A", "at": [0, 0]}])


class Tilt(unittest.TestCase):
    def test_add_and_set_tilt(self):
        lv, ops = level()
        ops.run([{"op": "add_prop", "id": "flag", "mesh": "Rock A", "at": [0, 0], "rotY": 30, "rotX": 90},
                 {"op": "add_prop", "id": "rock", "mesh": "Rock A", "at": [2, 0]}])
        flag, rock = lv["props"]
        self.assertEqual(list(flag), ["id", "base", "pos", "rotY", "rotX", "scale", "solid"])
        self.assertNotIn("rotX", rock)
        ops.run([{"op": "set_prop", "ids": ["flag", "rock"], "rotX": 0, "rotZ": 270}])
        self.assertEqual((flag.get("rotX"), flag["rotZ"], rock["rotZ"]), (None, -90, -90))
        with self.assertRaisesRegex(palace.Fail, "no prop 'nope'"):
            ops.run([{"op": "set_prop", "id": "nope", "rotX": 1}])

    def test_footprint_follows_tilt(self):
        ref = FakeRef()
        ref._map["scenery"] = [{"base": "mesh:Banner", "size": [2.0, 10.0, 0.5]}]   # 10 m tall, 2 m wide, thin
        ref.scenery = {"meshes": [{"name": "Disc", "size": [20.0, 20.0, 1.0], "upright": [-0.70711, 0, 0, 0.70711]}]}
        lv = {"host": "Test"}
        r = lambda p: palace.prop_radius(ref, lv, dict({"scale": 1}, **p))
        self.assertAlmostEqual(r({"base": "mesh:Banner"}), 1.0)
        self.assertAlmostEqual(r({"base": "mesh:Banner", "rotX": 90}), 5.0, places=4)    # lying on its back
        self.assertAlmostEqual(r({"base": "mesh:Banner", "rotZ": 90}), 5.0, places=4)    # on its side
        self.assertAlmostEqual(r({"base": "mesh:Disc"}), 10.0)                           # library mesh, z up
        self.assertAlmostEqual(r({"base": "mesh:Disc", "rotX": 90}), 10.0, places=3)     # stood on edge: 20 m wide still
        self.assertAlmostEqual(r({"base": "mesh:Disc", "rotX": 90, "rotZ": 90}), 10.0, places=3)


class LogicPlacesRender(unittest.TestCase):
    PACK = {
        "entities": [{"id": "door", "kind": "prop", "body": {"box": [4, 0.6]}, "mover": {"pose": {"open": {"rotY": 90}, "closed": {"rotY": 0}}}},
                     {"id": "cart", "kind": "prop", "body": {"radius": 1.5}, "mover": {"path": "@road"}}],
    }
    PLACES = [
        {"kind": "area", "id": "pit", "pos": [10, 0, 0], "area": {"shape": {"circle": {"radius": 4}}, "show": "fill", "color": "#0020ff"}},
        {"kind": "area", "id": "strip", "pos": [0, 0, 0], "rotY": 90, "area": {"shape": {"rect": {"size": [10, 2]}}}},
        {"id": "road", "kind": "path", "points": [[0, 0], [10, 0]]},
        {"id": "gate", "entity": "door", "pos": [5, 0, 5], "rotY": 0},
        {"id": "c1", "name": "wagon", "entity": "cart", "pos": [0, 0, 0]},
        {"id": "plate", "preset": "pressure-plate", "with": {"size": 2}, "pos": [-10, 0, 0]},
        {"id": "spot", "kind": "point", "pos": [3, 0, -3], "active": False},
    ]

    def places(self):
        lv = {"id": "t", "host": "Test", "logic": {"places": self.PLACES}}
        return {q["id"]: q for q in palace.logic_places(dict(self.PACK, levels=[lv]), lv)}, lv

    def test_resolves_templates_presets_and_paths(self):
        q, _ = self.places()
        self.assertEqual(q["pit"]["area"]["color"], (0, 32, 255))
        self.assertEqual(q["pit"]["area"]["shape"], ("circle", (10.0, 0.0, 4.0)))
        # rotY 90 turns the 10 m side from x onto z.
        xs = [round(x, 6) for x, _ in q["strip"]["area"]["shape"][1]]
        zs = [round(z, 6) for _, z in q["strip"]["area"]["shape"][1]]
        self.assertEqual((min(xs), max(xs), min(zs), max(zs)), (-1, 1, -5, 5))
        self.assertEqual(q["gate"]["kind"], "prop")
        self.assertEqual(len(q["gate"]["poses"]), 1)                 # the open pose; closed is where it stands
        self.assertEqual(q["c1"]["path"], [(0, 0), (10, 0)])        # mover path through @road
        self.assertEqual(q["c1"]["label"], "wagon")
        self.assertEqual(q["plate"]["kind"], "area")                 # from the preset
        self.assertEqual(q["plate"]["area"]["color"], (0x8a, 0x7a, 0x5a))
        self.assertFalse(q["spot"]["active"])

    def test_render_draws_them(self):
        import tempfile
        ref = FakeRef()
        ref._map["slots"][0].update(kind="Castle", upgrades=[])
        ref._map["spawnLines"] = []

        def pixel(places):
            lv, _ = level()
            lv["logic"] = {"places": places}
            with tempfile.TemporaryDirectory() as d:
                res = palace.render(ref, dict(self.PACK, levels=[lv]), lv, Path(d) / "r.png", labels="none", px=400)
                img = palace.read_png(res["written"])
            w = res["world"]
            x = int((10 - w["minX"]) / (w["maxX"] - w["minX"]) * img.w)
            y = int(img.h - (0 - w["minZ"]) / (w["maxZ"] - w["minZ"]) * img.h) + 6   # inside the pit, off the road
            return tuple(img.px[(y * img.w + x) * 3:(y * img.w + x) * 3 + 3])

        bare, drawn = pixel([{"id": "far", "kind": "point", "pos": [-15, 0, 0]}]), pixel(self.PLACES)   # same framing
        self.assertGreater(drawn[2] - drawn[0], bare[2] - bare[0] + 40, (bare, drawn))   # tinted by the pit's blue fill


if __name__ == "__main__":
    unittest.main()
