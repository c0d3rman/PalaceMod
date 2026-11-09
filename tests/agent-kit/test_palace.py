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


if __name__ == "__main__":
    unittest.main()
