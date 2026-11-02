using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PalaceMod.Builder.Level
{
    /// <summary>Dumps what a level layout editor needs to know about the loaded host scene (dev only).</summary>
    internal static class LevelSpike
    {
        public static string Run(string what)
        {
            var sb = new StringBuilder();
            var scene = SceneManager.GetActiveScene();
            sb.AppendLine($"scene {scene.name}");
            switch (what)
            {
                case "graphs":
                {
                    var astar = AstarPath.active;
                    if (astar == null) return "no AstarPath";
                    foreach (var g in astar.graphs)
                    {
                        sb.Append($"{g?.GetType().Name} '{g?.name}'");
                        if (g is RecastGraph r)
                            sb.Append($" mask={r.collectionSettings.layerMask.value} tags={string.Join(",", r.collectionSettings.tagMask ?? new List<string>())} colliders={r.collectionSettings.rasterizeColliders} meshes={r.collectionSettings.rasterizeMeshes} terrain={r.collectionSettings.rasterizeTerrain} mode={r.collectionSettings.collectionMode} tiles={r.tileXCount}x{r.tileZCount} cell={r.cellSize} forcedBounds={r.forcedBoundsCenter}/{r.forcedBoundsSize} radius={r.characterRadius}");
                        if (g is NavMeshGraph n) sb.Append($" source={n.sourceMesh?.name}");
                        sb.AppendLine();
                    }
                    var layers = new List<string>();
                    for (int i = 0; i < 32; i++) if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) layers.Add($"{i}:{LayerMask.LayerToName(i)}");
                    sb.AppendLine("layers " + string.Join(" ", layers));
                    break;
                }
                case "slots":
                {
                    var slots = Resources.FindObjectsOfTypeAll<BuildSlot>().Where(b => b.gameObject.scene == scene).ToList();
                    sb.AppendLine($"{slots.Count} slots; duplicate names: {string.Join(", ", slots.GroupBy(s => s.name).Where(g => g.Count() > 1).Select(g => g.Key + "x" + g.Count()))}");
                    foreach (var s in slots.OrderBy(s => s.name))
                    {
                        int nested = s.GetComponentsInChildren<BuildSlot>(true).Length - 1;
                        var tagged = s.GetComponentInChildren<TaggedObject>(true);
                        sb.AppendLine($"{s.name} bn='{s.buildingName}' pos={s.transform.position} rot={s.transform.eulerAngles.y:0} parent={s.transform.parent?.name} active={s.gameObject.activeSelf} act={s.ActivatorBuilding?.name}@{s.ActivatorLevel} startDeact={s.StartDeactivated} actUp={s.ActivatorUpgradesThis} nested={nested} costs=[{string.Join(",", s.OwnUpgrades.Select(u => u.cost))}] branches=[{string.Join(",", s.OwnUpgrades.Select(u => u.upgradeBranches.Count))}] castle={s.GetComponentInChildren<CastleCenter>(true) != null} guid={s.GetComponent<SaveLoadEntity>()?.GUID}");
                    }
                    break;
                }
                case "lines":
                {
                    foreach (var l in Object.FindObjectsOfType<EnemySpawnLine>())
                        sb.AppendLine($"{l.name} pos={l.transform.position} children={l.transform.childCount} fly={l.canSpawnFlying} small={l.canSpawnSmallGround} big={l.canSpawnBigGround} parent={l.transform.parent?.name}");
                    break;
                }
                case "props":
                {
                    var meshes = new Dictionary<string, (int count, Vector3 size, string layer, bool collider, string example)>();
                    foreach (var mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
                    {
                        if (mf.gameObject.scene != scene || mf.sharedMesh == null) continue;
                        if (mf.GetComponentInParent<BuildSlot>(true) != null || mf.GetComponentInParent<Hp>(true) != null) continue;
                        string key = mf.sharedMesh.name;
                        var size = mf.sharedMesh.bounds.size;
                        bool col = mf.GetComponent<Collider>() != null;
                        meshes[key] = meshes.TryGetValue(key, out var v) ? (v.count + 1, v.size, v.layer, v.collider || col, v.example) : (1, size, LayerMask.LayerToName(mf.gameObject.layer), col, Path(mf.transform));
                    }
                    foreach (var kv in meshes.OrderByDescending(k => k.Value.count))
                        sb.AppendLine($"{kv.Key} x{kv.Value.count} size={kv.Value.size} layer={kv.Value.layer} collider={kv.Value.collider} e.g. {kv.Value.example}");
                    break;
                }
                default:
                    return "usage: builder-level spike graphs|slots|lines|props";
            }
            return sb.ToString();
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null && parts.Count < 4; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
