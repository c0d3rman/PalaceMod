using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    /// <summary>
    /// agent-kit/palace.py writes packs the mod must read: build a level with it against a small made-up map reference,
    /// then read the pack with PackSerializer, validate it, and decode its terrain with GroundCodec. Skipped (passes)
    /// when python3 isn't installed.
    /// </summary>
    public static class AgentKitTests
    {
        public static void PalacePyPacksReadAndValidate()
        {
            string script = FindScript();
            if (script == null || !HasPython()) { Console.WriteLine("  (skipped: no python3 or agent-kit/palace.py)"); return; }
            string root = Path.Combine(Path.GetTempPath(), "palace-agent-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Path.Combine(root, "game");
                Directory.CreateDirectory(Path.Combine(game, "BepInEx"));
                string refDir = Path.Combine(root, "ref");
                WriteReference(refDir);
                string ops = Path.Combine(root, "ops.json");
                File.WriteAllText(ops, """
                [
                 {"op": "add_slot", "id": "h1", "kind": "House", "at": [6, 5]},
                 {"op": "add_slot", "id": "h2", "kind": "House", "at": [-6, 5]},
                 {"op": "add_slot", "id": "h3", "kind": "House", "at": [0, 10]},
                 {"op": "chain", "slots": ["h1", "h2", "h3"], "mode": "order"},
                 {"op": "set_slot", "slot": "h3", "costs": [5], "upgradeCap": 0},
                 {"op": "add_spawn_line", "id": "north", "at": [0, 30], "width": 12, "flying": false},
                 {"op": "terrain", "mode": "set", "height": 5, "circle": {"center": [0, 0], "radius": 15}, "falloff": 2},
                 {"op": "ramp", "from": [0, -12], "to": [0, -30], "width": 14, "fromHeight": 5, "toHeight": 0},
                 {"op": "water", "level": -1, "rect": {"min": [20, -20], "max": [30, 0]}},
                 {"op": "block", "circle": {"center": [-30, -30], "radius": 4}},
                 {"op": "paint", "material": "road", "line": {"points": [[0, -12], [0, -40]], "width": 4}},
                 {"op": "add_prop", "id": "r1", "mesh": "Rock A", "at": [-20, 30]},
                 {"op": "waves", "startingGold": 9, "nights": [{"spawns": [{"unit": "E Melee", "count": 3, "line": "north", "gold": 4}]}]}
                ]
                """);
                Run(script, game, refDir, "new", "test.pack", "--name", "Test");
                Run(script, game, refDir, "level", "test.pack", "lv", "--host", "Testmap", "--base", "flat");
                string applied = Run(script, game, refDir, "apply", "test.pack", ops);
                Assert.True(JObject.Parse(applied)["errors"]!.Count() == 0, "palace.py's own checks find no errors: " + applied);

                string json = File.ReadAllText(Path.Combine(game, "BepInEx", "PalaceBuilder", "packs", "test.pack", "pack.json"));
                var issues = new List<ValidationIssue>();
                var pack = PackSerializer.Read(json, issues);
                Assert.True(pack != null && issues.All(i => i.Severity != Severity.Error), "reads: " + string.Join("; ", issues));
                var errors = Validator.Validate(pack).Where(i => i.Severity == Severity.Error).ToList();
                Assert.True(errors.Count == 0, "validates: " + string.Join("; ", errors));

                var level = pack.FindLevel("lv");
                Assert.Equal("flat", level.Base, "flat base");
                var adds = level.BuildSlots.Add;
                Assert.Equal("scene:House Variant", adds[0].Base, "copies the host's house");
                Assert.Equal("castle", adds[0].Requires.Slot, "the first link needs the castle");
                Assert.Equal("add:h1", adds[1].Requires.Slot, "h2 needs h1");
                Assert.Equal(0, adds[1].Requires.Level, "once built");
                Assert.Equal(5, adds[2].Upgrades[0].Cost, "cost edit");
                Assert.Equal(0, adds[2].UpgradeCap, "cap");
                Assert.Equal(5f, adds[2].Pos[1], "y from the terrain plateau");

                var grid = GroundCodec.Decode(level.Terrain);
                Assert.True(Math.Abs(grid.HeightAt(0f, 0f) - 5f) < 0.02f, $"plateau height {grid.HeightAt(0f, 0f)}");
                Assert.True(Math.Abs(grid.HeightAt(0f, -21f) - 2.5f) < 0.3f, $"ramp midpoint {grid.HeightAt(0f, -21f)}");
                Assert.True(Math.Abs(grid.HeightAt(-40f, 30f)) < 0.01f, "untouched ground stays at 0");
                Assert.True(grid.CellAt(25f, -10f, out int wx, out int wz) && grid.Water[grid.CellIndex(wx, wz)], "water cell");
                Assert.True(grid.CellAt(-30f, -30f, out int bx, out int bz) && grid.Blocked[grid.CellIndex(bx, bz)], "blocked cell");
                Assert.True(grid.CellAt(0f, -30f, out int rx, out int rz) && grid.Materials[grid.CellIndex(rx, rz)] == (byte)GroundMaterial.Road, "road cell");
                Assert.Equal(9, pack.FindWaveSet(level.WaveSet).StartingGold, "starting gold");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        private static string FindScript()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "agent-kit", "palace.py");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static bool HasPython()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("python3", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Run(string script, string game, string refDir, params string[] args)
        {
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--game");
            psi.ArgumentList.Add(game);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PALACE_REF"] = refDir;
            using var p = Process.Start(psi);
            string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"palace.py {string.Join(" ", args)} failed: {stderr}{stdout}");
            return stdout;
        }

        // A 100 x 100 m map: the castle at the centre, one copyable house, one spawn line, flat walkable ground.
        private static void WriteReference(string dir)
        {
            Directory.CreateDirectory(Path.Combine(dir, "maps"));
            File.WriteAllText(Path.Combine(dir, "catalog.json"), """
            {"units": [{"id": "E Melee", "ref": "vanilla:E Melee", "side": "enemy", "name": "Swordsman", "maxHp": 25, "flying": false, "large": false, "tags": []}],
             "weapons": [], "hosts": [{"scene": "Testmap", "startingGold": 8, "vanillaWaves": {"nights": []}}]}
            """);
            int n = 50;
            string heights = Convert.ToBase64String(new byte[n * n * 2]);
            var walk = new JArray(Enumerable.Range(0, n).Select(_ => new string('w', n)));
            var map = JObject.Parse("""
            {"format": "palace-agent-map", "scene": "Testmap", "castle": {"name": "Castle", "pos": [0, 0, 0]}, "startingGold": 8, "nights": 0,
             "slots": [
              {"name": "Castle", "kind": "Castle Center", "pos": [0, 0, 0], "castle": true, "startDeactivated": false, "canBeBase": false,
               "upgrades": [{"cost": 3, "branches": [{}]}, {"cost": 7, "branches": [{}]}]},
              {"name": "House Variant", "kind": "House", "pos": [5, 0, 8], "startDeactivated": true, "activator": "Castle", "activatorLevel": 0,
               "upgradeCap": 0, "canBeBase": true, "upgrades": [{"cost": 2, "branches": [{"income": 1}]}]}],
             "spawnLines": [{"name": "Gate", "center": [0, 0, -45], "points": [[-5, 0, -45], [5, 0, -45]], "flying": true, "smallGround": true, "bigGround": true}],
             "scenery": [{"base": "mesh:Rock A", "name": "Rock A", "count": 3, "size": [4, 4, 4]}]}
            """);
            map["grid"] = new JObject { ["origin"] = new JArray(-50, -50), ["cell"] = 2, ["size"] = new JArray(n, n), ["heights"] = heights, ["walk"] = walk };
            File.WriteAllText(Path.Combine(dir, "maps", "Testmap.json"), map.ToString());
        }
    }
}
