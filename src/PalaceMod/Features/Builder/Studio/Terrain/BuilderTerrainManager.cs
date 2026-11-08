using System;
using System.Globalization;
using System.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Builder terrain (docs/builder-ux.md, "Terrain"): the dev command `builder-terrain`, which drives the ground layer
    directly on whatever level is loaded (no Studio needed) for testing. Part of the Builder feature.
*/
namespace PalaceMod
{
    public class BuilderTerrainManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            Builder.Studio.Terrain.TerrainTool.Register();
            Builder.Studio.Start.StartPopup.FlatFieldAvailable = true;
            DevCommands.Register("builder-terrain",
                "builder-terrain spike graphs|helper|king|ground|mats|paints|water|update|scan | flat [cell] | grid | raise <x> <z> <r> <m> [soft] | flatten <x> <z> <r> <h> [soft] | " +
                "smooth <x> <z> <r> | ramp <ax> <az> <ah> <bx> <bz> <bh> <width> | paint <x> <z> <r> <material> | block|unblock|water|dry <x> <z> <r> | " +
                "waterlevel <m> | scan | encode | clear   terrain dev tools", args =>
                {
                    string sub = args.FirstOrDefault()?.ToLowerInvariant();
                    var g = GroundLayer.Grid;
                    Dirty d;
                    switch (sub)
                    {
                        case "spike":
                            if (args.Length > 5) TerrainSpike.Region = new Vector4(F(args, 2), F(args, 3), F(args, 4), F(args, 5));
                            return TerrainSpike.Run(args.Length > 1 ? args[1] : "");
                        case "flat":
                        {
                            var scene = SceneManager.GetActiveScene();
                            var grid = GroundLayer.NewFlat(scene, args.Length > 1 ? F(args, 1) : 2f);
                            GroundLayer.Show(scene, grid, true);
                            return $"flat field {grid.W}x{grid.H} cells of {grid.Cell} m at y {grid.Y}, origin {grid.OriginX},{grid.OriginZ}";
                        }
                        case "grid":
                            return g == null ? "no terrain" : $"{g.W}x{g.H} cells of {g.Cell} m at y {g.Y}, origin {g.OriginX},{g.OriginZ}, flat={GroundLayer.Flat}, blocked {g.Blocked.Count(b => b)}, water {g.Water.Count(b => b)} at {g.WaterLevel}, heights {g.Heights.Min()}..{g.Heights.Max()}";
                        case "raise": Need(g); d = TerrainBrushes.Raise(g, F(args, 1), F(args, 2), F(args, 3), args.Length > 5 ? F(args, 5) : 0.5f, F(args, 4)); break;
                        case "flatten": Need(g); d = TerrainBrushes.Flatten(g, F(args, 1), F(args, 2), F(args, 3), args.Length > 5 ? F(args, 5) : 0f, F(args, 4)); break;
                        case "smooth": Need(g); d = TerrainBrushes.Smooth(g, F(args, 1), F(args, 2), F(args, 3), 0.5f, 1f); break;
                        case "ramp": Need(g); d = TerrainBrushes.Ramp(g, F(args, 1), F(args, 2), F(args, 3), F(args, 4), F(args, 5), F(args, 6), F(args, 7)); break;
                        case "paint":
                            Need(g);
                            d = TerrainBrushes.Paint(g, F(args, 1), F(args, 2), F(args, 3), CellLayer.Material, (byte)(GroundMaterial)Enum.Parse(typeof(GroundMaterial), args[4], true));
                            break;
                        case "block": case "unblock":
                            Need(g); d = TerrainBrushes.Paint(g, F(args, 1), F(args, 2), F(args, 3), CellLayer.Blocked, (byte)(sub == "block" ? 1 : 0)); break;
                        case "water": case "dry":
                            Need(g); d = TerrainBrushes.Water(g, F(args, 1), F(args, 2), F(args, 3), sub == "water"); break;
                        case "waterlevel":
                            Need(g);
                            g.WaterLevel = F(args, 1);
                            d = new Dirty { X0 = 0, Z0 = 0, X1 = g.W - 1, Z1 = g.H - 1 };
                            break;
                        case "scan":
                        {
                            long ms = GroundGraphs.Scan();
                            var scene = SceneManager.GetActiveScene();
                            return $"scan {ms} ms; {TerrainSpike.Counts()}\npaths: {PathCheck.Describe(PathCheck.Check(scene))}";
                        }
                        case "encode":
                        {
                            Need(g);
                            var def = GroundCodec.Encode(g);
                            string json = Newtonsoft.Json.JsonConvert.SerializeObject(def, new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
                            return $"{json.Length} chars: {(json.Length > 400 ? json.Substring(0, 400) + "..." : json)}";
                        }
                        case "brush":
                        {
                            // The Terrain tool's brush, as the strip sets it: brush <name> [size].
                            var tool = Builder.Studio.Terrain.TerrainTool.Instance;
                            if (args.Length > 1)
                            {
                                string want = string.Join(" ", args.Skip(1).TakeWhile(a => !float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _)));
                                int i = Array.FindIndex(Builder.Studio.Terrain.TerrainTool.BrushNames, n => n.Equals(want, StringComparison.OrdinalIgnoreCase));
                                if (i < 0) throw new ArgumentException($"brushes: {string.Join(", ", Builder.Studio.Terrain.TerrainTool.BrushNames)}");
                                tool.Brush = (Builder.Studio.Terrain.TerrainBrush)i;
                                if (float.TryParse(args[args.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float size)) tool.Radius = size;
                                Builder.Studio.Studio.RefreshPanels();
                            }
                            return $"brush {tool.Brush}, size {tool.Radius} m, available: {string.Join(", ", tool.Available)}";
                        }
                        case "stroke":
                        {
                            // A stroke through the Terrain tool: stroke [shift] <x> <z> [<x> <z>]... (0.1 s per point).
                            var tool = Builder.Studio.Terrain.TerrainTool.Instance;
                            bool shift = args.Length > 1 && args[1] == "shift";
                            var nums = args.Skip(shift ? 2 : 1).Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToList();
                            if (nums.Count < 2 || nums.Count % 2 != 0) throw new ArgumentException("stroke [shift] <x> <z> [<x> <z>]...");
                            var points = Enumerable.Range(0, nums.Count / 2).Select(i => new Vector3(nums[2 * i], 0f, nums[2 * i + 1])).ToList();
                            tool.BeginStroke(points[0], shift, false);
                            foreach (var p in points.Skip(1)) tool.ContinueStroke(p, 0.1f);
                            tool.EndStroke();
                            return $"stroke of {points.Count} points; paths stale={tool.PathsStale}; {(Builder.Studio.Studio.Level?.Terrain is Builder.Model.TerrainDef t ? $"level terrain {t.Size?[0]}x{t.Size?[1]}" : "no level terrain")}";
                        }
                        case "marks":
                            GroundLayer.ShowMarks = args.Length < 2 || args[1] != "off";
                            return "blocked cells " + (GroundLayer.ShowMarks ? "shown" : "hidden");
                        case "snap":
                            GroundLayer.SnapToGround(SceneManager.GetActiveScene());
                            return "slots and king on the ground";
                        case "king":
                        {
                            var pm = PlayerMovement.instance ?? throw new InvalidOperationException("no king");
                            if (args.Length > 2)
                            {
                                var p = new Vector3(F(args, 1), 0f, F(args, 2));
                                p.y = GroundLayer.TryHeight(p, out float y) ? y + 0.1f : pm.transform.position.y;
                                pm.TeleportTo(p);
                            }
                            var k = pm.transform.position;
                            return $"king at {k.x:0.0} {k.y:0.00} {k.z:0.0}" + (GroundLayer.TryHeight(k, out float gy) ? $" (ground {gy:0.00})" : "");
                        }
                        case "save":
                        {
                            // A user pack with one level on this map carrying the shown terrain (test setup; play it with
                            // `builder-req play <pack>`).
                            Need(g);
                            string id = args.Length > 1 ? args[1] : throw new ArgumentException("missing pack id");
                            string scene = SceneManager.GetActiveScene().name;
                            var pack = new Builder.Model.DesignPack { Id = id, Name = "Terrain " + id, Author = "dev" };
                            pack.Levels.Add(new Builder.Model.LevelDef
                            {
                                Id = Builder.Model.Ids.FromName(scene + "-terrain"),
                                Name = scene + " Terrain",
                                Host = scene,
                                Base = GroundLayer.Flat ? Builder.Model.LevelBases.Flat : null,
                                Terrain = GroundCodec.Encode(g),
                            });
                            Builder.BuilderServices.Packs.Save(pack);
                            Builder.BuilderServices.Packs.Refresh();
                            return $"saved {id} with level {pack.Levels[0].Id}";
                        }
                        case "clear":
                            GroundLayer.Clear();
                            return "cleared";
                        default:
                            throw new ArgumentException("see `help` for builder-terrain usage");
                    }
                    GroundLayer.Rebuild(d);
                    return d.Empty ? "nothing changed" : $"changed cells {d.X0},{d.Z0}..{d.X1},{d.Z1}";
                });
        }

        private static void Need(TerrainGrid g)
        {
            if (g == null) throw new InvalidOperationException("no terrain (builder-terrain flat)");
        }

        private static float F(string[] args, int i) =>
            args.Length > i ? float.Parse(args[i], CultureInfo.InvariantCulture) : throw new ArgumentException("missing argument");
    }
}
