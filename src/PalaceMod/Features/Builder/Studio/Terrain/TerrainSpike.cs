using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The terrain spike (docs/builder-ux.md, Terrain T0): what the ground layer has to fit into on a loaded map. Dev only.
*/
namespace PalaceMod.Builder.Level
{
    internal static class TerrainSpike
    {
        /// <summary>x0, z0, x1, z1 for "region".</summary>
        public static Vector4 Region;

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null && parts.Count < 4; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        public static string Run(string what)
        {
            var sb = new StringBuilder();
            var scene = SceneManager.GetActiveScene();
            sb.AppendLine($"scene {scene.name}");
            var astar = AstarPath.active;
            switch (what)
            {
                case "graphs":
                {
                    if (astar == null) return "no AstarPath";
                    for (int gi = 0; gi < astar.graphs.Length; gi++)
                    {
                        var g = astar.graphs[gi];
                        int nodes = 0;
                        g?.GetNodes(_ => nodes++);
                        sb.Append($"[{gi}] {g?.GetType().Name} '{g?.name}' nodes={nodes}");
                        if (g is RecastGraph r)
                        {
                            var c = r.collectionSettings;
                            sb.Append($" layers=[{Layers(c.layerMask.value)}] colliders={c.rasterizeColliders} meshes={c.rasterizeMeshes} mode={c.collectionMode} " +
                                      $"tiles={r.tileXCount}x{r.tileZCount} useTiles={r.useTiles} tileSize={r.editorTileSize} cell={r.cellSize} " +
                                      $"bounds={r.forcedBoundsCenter}/{r.forcedBoundsSize} rot={r.rotation} radius={r.characterRadius} " +
                                      $"maxSlope={r.maxSlope} climb={r.walkableClimb} height={r.walkableHeight} minRegion={r.minRegionSize}");
                        }
                        if (g is NavMeshGraph n)
                            sb.Append($" source={n.sourceMesh?.name} readable={n.sourceMesh?.isReadable} verts={n.sourceMesh?.vertexCount} offset={n.offset} rot={n.rotation} scale={n.scale}");
                        sb.AppendLine();
                    }
                    var layers = new List<string>();
                    for (int i = 0; i < 32; i++) if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) layers.Add($"{i}:{LayerMask.LayerToName(i)}");
                    sb.AppendLine("layers " + string.Join(" ", layers));
                    break;
                }
                case "helper":
                {
                    foreach (var h in Object.FindObjectsOfType<NavmeshBakeHelper>())
                    {
                        var t = Traverse.Create(h);
                        var walls = t.Field("invisibleWalls").GetValue<GameObject>();
                        var flyer = t.Field("flyerSurface").GetValue<GameObject>();
                        sb.AppendLine($"helper {h.name} turnOff={t.Field("_turnOffMeshRendsOnBake").GetValue()} center={t.Field("center").GetValue()} size={t.Field("size").GetValue()}");
                        sb.AppendLine("walls: " + Describe(walls));
                        sb.AppendLine("flyer: " + Describe(flyer));
                    }
                    sb.AppendLine($"HideMeshRendOnPlay: {Object.FindObjectsOfType<HideMeshRendOnPlay>(true).Length} ({string.Join(", ", Object.FindObjectsOfType<HideMeshRendOnPlay>(true).Take(8).Select(x => x.name))})");
                    break;
                }
                case "king":
                {
                    var pm = PlayerMovement.instance;
                    if (pm == null) return "no king";
                    var cc = pm.GetComponent<CharacterController>();
                    int layer = pm.gameObject.layer;
                    sb.AppendLine($"king layer {layer}:{LayerMask.LayerToName(layer)} cc={cc != null} slope={cc?.slopeLimit} step={cc?.stepOffset} radius={cc?.radius} height={cc?.height}");
                    sb.AppendLine("collides with: " + string.Join(",", Enumerable.Range(0, 32).Where(l => !string.IsNullOrEmpty(LayerMask.LayerToName(l)) && !Physics.GetIgnoreLayerCollision(layer, l)).Select(l => $"{l}:{LayerMask.LayerToName(l)}")));
                    // What the king stands on.
                    if (Physics.Raycast(pm.transform.position + Vector3.up, Vector3.down, out var hit, 20f, ~0, QueryTriggerInteraction.Ignore))
                        sb.AppendLine($"under king: {hit.collider.name} ({hit.collider.GetType().Name}) layer {hit.collider.gameObject.layer} y={hit.point.y}");
                    foreach (var f in typeof(PlayerMovement).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                        if (f.FieldType == typeof(LayerMask)) sb.AppendLine($"PlayerMovement.{f.Name} = [{Layers(((LayerMask)f.GetValue(pm)).value)}]");
                    break;
                }
                case "ground":
                {
                    // Renderers using the ground materials, and colliders on the ground layer.
                    var mats = GroundMaterials.All();
                    sb.AppendLine("materials: " + string.Join(", ", mats.Select(kv => $"{kv.Key}={kv.Value?.name}")));
                    var set = new HashSet<Material>(mats.Values.Where(m => m != null));
                    var byLayer = new Dictionary<string, int>();
                    foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
                    {
                        if (r.gameObject.scene != scene || !r.sharedMaterials.Any(set.Contains)) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        string key = $"layer {LayerMask.LayerToName(r.gameObject.layer)} mats={string.Join("+", r.sharedMaterials.Select(m => m?.name))} readable={mf?.sharedMesh?.isReadable} collider={r.GetComponent<Collider>()?.GetType().Name} enabled={r.enabled} root={r.transform.root.name} comps={string.Join(",", r.GetComponents<Component>().Select(c => c.GetType().Name).Where(n => n != "Transform" && n != "MeshFilter" && n != "MeshRenderer"))}";
                        byLayer[key] = byLayer.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                    foreach (var kv in byLayer.OrderByDescending(k => k.Value)) sb.AppendLine($"{kv.Value}x {kv.Key}");
                    int groundLayer = LayerMask.NameToLayer("Ground");
                    var cols = Object.FindObjectsOfType<Collider>().Where(c => c.gameObject.scene == scene && c.gameObject.layer == groundLayer).ToList();
                    sb.AppendLine($"ground-layer colliders: {cols.Count} ({string.Join(", ", cols.GroupBy(c => c.GetType().Name).Select(g => g.Key + "x" + g.Count()))}) e.g. {string.Join(", ", cols.Take(5).Select(c => c.name))}");
                    break;
                }
                case "update":
                {
                    // Time a local graph update over a 20 m box at the castle, then a full scan.
                    if (astar == null) return "no AstarPath";
                    var castle = PathCheck.Castle(scene) ?? Vector3.zero;
                    var before = Counts();
                    var sw = Stopwatch.StartNew();
                    var guo = new GraphUpdateObject(new Bounds(castle + new Vector3(25f, 0f, 0f), new Vector3(20f, 20f, 20f))) { updatePhysics = true };
                    astar.UpdateGraphs(guo);
                    astar.FlushGraphUpdates();
                    long local = sw.ElapsedMilliseconds;
                    var mid = Counts();
                    sw.Restart();
                    astar.Scan();
                    long full = sw.ElapsedMilliseconds;
                    var after = Counts();
                    sb.AppendLine($"local 20 m update {local} ms; full scan {full} ms");
                    sb.AppendLine($"nodes before {before}; after local {mid}; after scan {after}");
                    break;
                }
                case "at":
                {
                    // Renderers over the king's spot (what draws there).
                    var p = PlayerMovement.instance?.transform.position ?? Vector3.zero;
                    foreach (var r in Object.FindObjectsOfType<Renderer>())
                    {
                        if (!r.enabled || r.gameObject.scene != scene) continue;
                        var b = r.bounds;
                        if (p.x < b.min.x - 3 || p.x > b.max.x + 3 || p.z < b.min.z - 3 || p.z > b.max.z + 3 || b.size.x < 150) continue;
                        sb.AppendLine($"{r.name} layer={LayerMask.LayerToName(r.gameObject.layer)} mats={string.Join("+", r.sharedMaterials.Select(m => m?.name))} y={b.min.y:0.0}..{b.max.y:0.0} parent={r.transform.parent?.name}");
                    }
                    break;
                }
                case "region":
                {
                    // Ground/Obstacle-layer renderers in a box (args: x0 z0 x1 z1 via Run's caller), enabled or not.
                    var box = Region;
                    int n = 0;
                    foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
                    {
                        if (r == null || r.gameObject.scene != scene || (r.gameObject.layer != 6 && r.gameObject.layer != 7)) continue;
                        var b = r.bounds;
                        if (b.max.x < box.x || b.min.x > box.z || b.max.z < box.y || b.min.z > box.w) continue;
                        if (n++ > 40) break;
                        var mf = r.GetComponent<MeshFilter>();
                        sb.AppendLine($"{Path(r.transform)} layer={r.gameObject.layer} enabled={r.enabled} active={r.gameObject.activeInHierarchy} force={r.forceRenderingOff} readable={mf?.sharedMesh?.isReadable} mesh={mf?.sharedMesh?.name} y={b.min.y:0.0}..{b.max.y:0.0} comps={string.Join(",", r.GetComponents<Component>().Select(c => c.GetType().Name))}");
                    }
                    break;
                }
                case "scan":
                {
                    if (astar == null) return "no AstarPath";
                    var before = Counts();
                    long ms = GroundGraphs.Scan();
                    sb.AppendLine($"scan as baked {ms} ms");
                    sb.AppendLine($"before {before}");
                    sb.AppendLine($"after  {Counts()}");
                    sb.AppendLine(GroundGraphs.LastPrune);
                    break;
                }
                default:
                    return "usage: builder-terrain spike graphs|helper|king|ground|update|scan";
            }
            return sb.ToString();
        }

        public static string Counts()
        {
            var parts = new List<string>();
            foreach (var g in AstarPath.active.graphs)
            {
                int n = 0, walk = 0;
                var min = new Vector3(float.MaxValue, 0, float.MaxValue);
                var max = new Vector3(float.MinValue, 0, float.MinValue);
                g.GetNodes(node =>
                {
                    n++;
                    if (!node.Walkable) return;
                    walk++;
                    var p = (Vector3)node.position;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                });
                var areas = new Dictionary<uint, int>();
                g.GetNodes(node => { if (node.Walkable) areas[node.Area] = areas.TryGetValue(node.Area, out int c) ? c + 1 : 1; });
                parts.Add($"{g.name}:{walk}/{n} x[{min.x:0}..{max.x:0}] z[{min.z:0}..{max.z:0}] areas[{string.Join(",", areas.Values.OrderByDescending(v => v).Take(8))}]");
            }
            return string.Join(" ", parts);
        }

        private static string Layers(int mask) => string.Join(",", Enumerable.Range(0, 32).Where(l => (mask & (1 << l)) != 0).Select(l => $"{l}:{LayerMask.LayerToName(l)}"));

        private static string Describe(GameObject go)
        {
            if (go == null) return "none";
            var rs = go.GetComponentsInChildren<MeshRenderer>(true);
            var cs = go.GetComponentsInChildren<Collider>(true);
            var mfs = go.GetComponentsInChildren<MeshFilter>(true);
            return $"{go.name} active={go.activeInHierarchy} layer={go.layer}:{LayerMask.LayerToName(go.layer)} children={go.transform.childCount} " +
                   $"renderers={rs.Length} enabled={rs.Count(r => r.enabled)} readable={mfs.Count(m => m.sharedMesh != null && m.sharedMesh.isReadable)}/{mfs.Length} " +
                   $"colliders={string.Join(",", cs.GroupBy(c => c.GetType().Name + (c.enabled ? "" : "(off)") + "@" + c.gameObject.layer).Select(g => g.Key + "x" + g.Count()))} " +
                   $"hide={go.GetComponentsInChildren<HideMeshRendOnPlay>(true).Length} pathMesher={go.GetComponentsInChildren<PathMesher>(true).Length} " +
                   $"bounds={(rs.Length > 0 ? rs.Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; }).ToString() : "-")}";
        }
    }

    /// <summary>The level's shared ground materials (ColorAndLightManager recolours them from the scheme and daylight).</summary>
    internal static class GroundMaterials
    {
        public static readonly string[] Fields = ["groundMaterial", "groundHigh", "groundLow", "earthMaterial", "sandMaterial", "roadMaterial", "rockMaterial", "groundPatches", "waterMaterial", "oceanMaterial"];

        public static Dictionary<string, Material> All()
        {
            var clm = ColorAndLightManager.Instance ?? Object.FindObjectOfType<ColorAndLightManager>();
            var d = new Dictionary<string, Material>();
            if (clm == null) return d;
            var t = Traverse.Create(clm);
            foreach (var f in Fields) d[f] = t.Field(f).GetValue<Material>();
            return d;
        }

        public static Material Get(string field)
        {
            var clm = ColorAndLightManager.Instance ?? Object.FindObjectOfType<ColorAndLightManager>();
            return clm == null ? null : Traverse.Create(clm).Field(field).GetValue<Material>();
        }
    }
}
