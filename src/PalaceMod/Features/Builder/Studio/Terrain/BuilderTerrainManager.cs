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
            DevCommands.Register("builder-terrain",
                "builder-terrain spike graphs|helper|king|ground|update|scan | flat [cell] | grid | raise <x> <z> <r> <m> [soft] | flatten <x> <z> <r> <h> [soft] | " +
                "smooth <x> <z> <r> | ramp <ax> <az> <ah> <bx> <bz> <bh> <width> | paint <x> <z> <r> <material> | block|unblock|water|dry <x> <z> <r> | " +
                "waterlevel <m> | scan | encode | clear   terrain dev tools", args =>
                {
                    string sub = args.FirstOrDefault()?.ToLowerInvariant();
                    var g = GroundLayer.Grid;
                    Dirty d;
                    switch (sub)
                    {
                        case "spike":
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
