using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    What the level overlay and the layout editor know about a loaded host map: its build slots, spawn lines and scenery
    meshes (the prop palette), plus pose and GUID helpers. Everything is found through components, never paths.
*/
namespace PalaceMod.Builder.Level
{
    /// <summary>A scenery mesh of the host that can be placed as a prop.</summary>
    internal sealed class PropBase
    {
        public string Id;            // "mesh:<mesh name>"
        public string Name;          // display name
        public Mesh Mesh;
        public Material[] Materials;
        /// <summary>The source object's rotation with its yaw removed (imported meshes are often tilted -90 on X).</summary>
        public Quaternion Upright;
        public Vector3 Scale;
        public int Count;            // how often the host uses it (palette order)
    }

    internal static class LayoutScene
    {
        public const int ObstacleLayer = 7;   // rasterized by the host's recast graphs (layers Ground and Obstacle)
        public const string AddedPrefix = "PalaceBuilder ";

        public static Scene Host(string name) => string.IsNullOrEmpty(name) ? default : SceneManager.GetSceneByName(name);

        /// <summary>Every BuildSlot of the scene, active or not, by name order.</summary>
        public static List<BuildSlot> Slots(Scene scene) =>
            Resources.FindObjectsOfTypeAll<BuildSlot>().Where(b => b != null && b.gameObject.scene == scene).OrderBy(b => b.name, StringComparer.Ordinal).ToList();

        public static BuildSlot FindSlot(Scene scene, string name) => Slots(scene).FirstOrDefault(s => s.name == name);

        public static List<EnemySpawnLine> Lines(Scene scene) =>
            Resources.FindObjectsOfTypeAll<EnemySpawnLine>().Where(l => l != null && l.gameObject.scene == scene && l.gameObject.activeInHierarchy).OrderBy(l => l.name, StringComparer.Ordinal).ToList();

        public static bool IsCastle(BuildSlot slot) => slot.GetComponentInChildren<CastleCenter>(true) != null;

        /// <summary>Whether a new slot may be a copy of this one: not the castle, not a wall piece its gate upgrades,
        /// and no build slots nested inside it.</summary>
        public static bool CanBeBase(BuildSlot slot) =>
            !IsCastle(slot) && !slot.ActivatorUpgradesThis && slot.GetComponentsInChildren<BuildSlot>(true).Length == 1;

        /// <summary>Whether a slot can be moved or removed (the castle stays where the map puts it).</summary>
        public static bool CanMove(BuildSlot slot) => !IsCastle(slot);

        public static string TypeName(BuildSlot slot)
        {
            if (string.IsNullOrEmpty(slot.buildingName)) return slot.name;
            string t = TextTranslator.Translate(slot.LOCIDENTIFIER_NAME);
            return string.IsNullOrEmpty(t) || t.StartsWith("Building/") ? slot.buildingName : t;
        }

        /// <summary>The middle of a spawn line (its child points), where enemies appear.</summary>
        public static Vector3 LineCenter(Transform line)
        {
            if (line.childCount == 0) return line.position;
            var sum = Vector3.zero;
            for (int i = 0; i < line.childCount; i++) sum += line.GetChild(i).position;
            return sum / line.childCount;
        }

        public static bool IsGroundLine(EnemySpawnLine l) => l.canSpawnSmallGround || l.canSpawnBigGround;

        // ---- Props ---------------------------------------------------------------------------------------------

        private static readonly HashSet<int> NonPropLayers = [4, 5, 6, 10, 11, 12, 13, 18, 21, 24, 29, 31];

        /// <summary>The host's scenery meshes (trees, rocks, ...), most used first.</summary>
        public static List<PropBase> Props(Scene scene)
        {
            var byMesh = new Dictionary<string, PropBase>();
            foreach (var mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (mf == null || mf.gameObject.scene != scene || mf.sharedMesh == null || string.IsNullOrEmpty(mf.sharedMesh.name)) continue;
                if (NonPropLayers.Contains(mf.gameObject.layer) || mf.name.StartsWith(AddedPrefix)) continue;
                if (mf.GetComponentInParent<BuildSlot>(true) != null || mf.GetComponentInParent<Hp>(true) != null) continue;
                if (mf.GetComponentInParent<EnemySpawner>(true) != null || mf.GetComponent<Collider>() != null) continue;
                var renderer = mf.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials.Any(m => m == null)) continue;
                var size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                float max = Mathf.Max(size.x, size.y, size.z);
                if (max < 0.4f || max > 40f) continue;
                string name = mf.sharedMesh.name;
                if (name.StartsWith("Combined") || name is "Plane" or "Cube" or "Quad" or "Cylinder" or "Sphere" or "Capsule" || name.StartsWith("quad")) continue;
                if (byMesh.TryGetValue(name, out var existing)) { existing.Count++; continue; }
                var rot = mf.transform.rotation;
                byMesh[name] = new PropBase
                {
                    Id = "mesh:" + name,
                    Name = Readable(name),
                    Mesh = mf.sharedMesh,
                    Materials = renderer.sharedMaterials,
                    Upright = Quaternion.Euler(0f, -rot.eulerAngles.y, 0f) * rot,
                    Scale = mf.transform.lossyScale,
                    Count = 1,
                };
            }
            return byMesh.Values.OrderByDescending(p => p.Count).ThenBy(p => p.Name).Take(24).ToList();
        }

        private static string Readable(string mesh) => mesh.Replace('_', ' ').Trim();

        /// <summary>A prop: a plain mesh object in the host scene, a solid obstacle when solid.</summary>
        public static GameObject MakeProp(Scene scene, PropBase b, string name, Vector3 pos, float yaw, float scale, bool solid)
        {
            var go = new GameObject(AddedPrefix + name);
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = b.Materials;
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f) * b.Upright);
            go.transform.localScale = b.Scale * scale;
            SetSolid(go, solid);
            return go;
        }

        internal const string BlockerName = AddedPrefix + "blocker";
        private static Mesh blockerMesh;

        /// <summary>Makes a prop block pathfinding and movement, or not. The host's recast graphs rasterize renderer
        /// meshes (not colliders) on the Ground and Obstacle layers, but skip meshes that aren't CPU-readable, which
        /// scenery meshes aren't. So a solid prop gets an invisible child: a readable box mesh around the prop's mesh on
        /// the obstacle layer, with a matching box collider.</summary>
        public static void SetSolid(GameObject go, bool solid)
        {
            go.layer = 0;
            var existing = go.transform.Find(BlockerName);
            if (!solid)
            {
                if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
                return;
            }
            var mesh = go.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || existing != null) return;
            var blocker = new GameObject(BlockerName) { layer = ObstacleLayer };
            blocker.transform.SetParent(go.transform, false);
            blocker.transform.localPosition = mesh.bounds.center;
            blocker.transform.localScale = mesh.bounds.size;
            blocker.AddComponent<MeshFilter>().sharedMesh = BlockerMesh();
            blocker.AddComponent<MeshRenderer>().forceRenderingOff = true;   // enabled (the gatherer checks), never drawn
            blocker.AddComponent<BoxCollider>();
        }

        /// <summary>A readable unit cube centred on the origin.</summary>
        private static Mesh BlockerMesh()
        {
            if (blockerMesh != null) return blockerMesh;
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
            int[] t =
            [
                0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,   // -z, +z
                0, 1, 4, 1, 5, 4,   2, 6, 3, 3, 6, 7,   // -y, +y
                0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,   // -x, +x
            ];
            blockerMesh = new Mesh { name = BlockerName, vertices = v, triangles = t };
            blockerMesh.RecalculateBounds();
            return blockerMesh;
        }

        // ---- Poses ---------------------------------------------------------------------------------------------

        public static Vector3 ToVector(float[] p) => p != null && p.Length == 3 ? new Vector3(p[0], p[1], p[2]) : Vector3.zero;

        public static float[] FromVector(Vector3 v) => [Round(v.x), Round(v.y), Round(v.z)];

        public static float Round(float f) => Mathf.Round(f * 100f) / 100f;

        /// <summary>A rotation with the yaw replaced, keeping any tilt of the original.</summary>
        public static Quaternion WithYaw(Quaternion original, float yaw)
        {
            var e = original.eulerAngles;
            return Quaternion.Euler(e.x, yaw, e.z);
        }

        /// <summary>The walkable ground nearest to a point (the enemy ground graph), or the point itself.</summary>
        public static Vector3 Ground(Vector3 p)
        {
            var astar = AstarPath.active;
            if (astar == null) return p;
            try
            {
                var nn = NearestNodeConstraint.Walkable;
                nn.graphMask = GraphMask.FromGraphName("Enemy Units S");
                var hit = astar.GetNearest(p, nn);
                return hit.node != null && (hit.position - p).sqrMagnitude < 400f ? new Vector3(p.x, hit.position.y, p.z) : p;
            }
            catch (Exception)
            {
                return p;
            }
        }

        // ---- Save GUIDs ----------------------------------------------------------------------------------------

        /// <summary>A stable save GUID for an added slot, so the in-memory match save (retry day) finds it again.</summary>
        public static string Guid(string packId, string levelId, string id)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes($"palacebuilder:{packId}/{levelId}/{id}"));
            return new System.Guid(bytes).ToString();
        }

        public static void SetGuid(GameObject go, string guid)
        {
            var sle = go.GetComponent<SaveLoadEntity>();
            if (sle != null) Traverse.Create(sle).Field("guid").SetValue(guid);
        }

        // ---- Inert copies ----------------------------------------------------------------------------------------

        /// <summary>
        /// A look-only copy of a build slot in its blueprint state: every script, collider and physics part is removed
        /// before it's activated, so no game code runs for it. Used by the layout editor (never in play).
        /// </summary>
        public static GameObject Proxy(GameObject source, Transform parent, string name)
        {
            var holder = new GameObject("PalaceBuilder proxy holder");
            holder.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(source, holder.transform);
            copy.name = name;
            var slot = copy.GetComponent<BuildSlot>();
            if (slot != null)
            {
                Traverse.Create(slot).Field("bluepringParent").GetValue<GameObject>()?.SetActive(true);
                // The built building (and the units inside it) is never shown here.
                var built = Traverse.Create(slot).Field("buildingParent").GetValue<GameObject>();
                if (built != null && built != copy) UnityEngine.Object.DestroyImmediate(built);
            }
            Strip(copy);
            copy.transform.SetParent(parent, true);
            copy.SetActive(true);
            UnityEngine.Object.Destroy(holder);
            return copy;
        }

        /// <summary>Removes everything but transforms, meshes and renderers.</summary>
        public static void Strip(GameObject go)
        {
            // Only remove a component once nothing left on its object requires it (Unity logs an error otherwise).
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                var left = t.GetComponents<Component>().Where(c => c != null && !Keep(c)).ToList();
                for (int guard = 0; left.Count > 0 && guard < 16; guard++)
                {
                    var removable = left.Where(c => !left.Any(o => o != c && Requires(o.GetType(), c.GetType()))).ToList();
                    if (removable.Count == 0) break;
                    foreach (var c in removable)
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                        left.Remove(c);
                    }
                }
            }

            static bool Keep(Component c) => c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer;
        }

        private static readonly Dictionary<(Type, Type), bool> requires = [];

        private static bool Requires(Type owner, Type needed)
        {
            if (requires.TryGetValue((owner, needed), out bool r)) return r;
            r = false;
            foreach (RequireComponent rc in owner.GetCustomAttributes(typeof(RequireComponent), true))
                foreach (var t in new[] { rc.m_Type0, rc.m_Type1, rc.m_Type2 })
                    if (t != null && t.IsAssignableFrom(needed)) r = true;
            return requires[(owner, needed)] = r;
        }
    }
}
