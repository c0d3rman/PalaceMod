using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pathfinding;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.SceneManagement;
using static PalaceMod.Builder.Agent.AgentCommands;

/*
    `agent host <file>`: everything an agent needs to lay out a level on the loaded map, as one JSON file. Run on a
    vanilla map it describes the base map (palace.py caches one per map as its reference); run in a custom level it
    describes the level as built (added slots are named "PalaceBuilder <id>").

    - slots: every build slot, with its upgrade tree as the scene data has it (costs, income and HP per branch, troops
      and attackers each branch switches on), what unlocks it (activator, activatorLevel) and its upgrade cap.
    - buildings: the game's standard building of each kind (Level/BuildingPrefabs), in the slots' shape, named by
      their base "building:<kind>": what `add_slot` places for a kind the map doesn't have.
    - spawnLines: name, points and which enemies each may spawn.
    - grid: the ground sampled every `cell` metres over the enemy navmesh's bounds: height (cm, int16 little-endian,
      base64; -32768 = no ground) and walkability per cell as a row string per z (row 0 = smallest z):
      '#' no ground, '.' ground enemies can't walk, 's' small enemies only, 'w' every ground enemy.
    - scenery: the map's own scenery meshes (prop bases "mesh:<name>").
    - camera: the player's screen "north" and "right" on the ground, so a picture can be turned like the game's view.
*/
namespace PalaceMod.Builder.Agent
{
    internal static class MapDump
    {
        public static JObject Take(float cell)
        {
            var scene = EnemySpawner.instance != null ? EnemySpawner.instance.gameObject.scene : SceneManager.GetActiveScene();
            if (EnemySpawner.instance == null) throw new InvalidOperationException("no level is loaded");
            var slots = LayoutScene.Slots(scene);
            var castle = slots.FirstOrDefault(LayoutScene.IsCastle);
            LevelCapture.Orientation(out var right, out var up);
            var session = BuilderServices.Sessions?.Current;
            var root = new JObject
            {
                ["format"] = "palace-agent-map",
                ["version"] = 1,
                ["scene"] = scene.name,
                ["gameVersion"] = Application.version,
                ["session"] = session == null ? null : new JObject { ["pack"] = session.Pack?.Id, ["level"] = session.Level?.Id },
                ["camera"] = new JObject { ["north"] = new JArray(R(up.x), R(up.z)), ["right"] = new JArray(R(right.x), R(right.z)) },
                ["castle"] = castle == null ? null : new JObject { ["name"] = castle.name, ["pos"] = V(castle.transform.position) },
                ["startingGold"] = EnemySpawner.instance.goldBalanceAtStart,
                ["nights"] = EnemySpawner.instance.WaveCount,
            };
            root["slots"] = new JArray(slots.Select(Slot));
            // The game's standard building of every kind (the same on every map): bases "building:<kind>" for kinds
            // the map lacks. Named by their base; no position.
            root["buildings"] = new JArray(BuildingPrefabs.All().Select(b =>
            {
                var o = Slot(b.Slot);
                o["name"] = Model.BuildingRefs.Ref(b.Id);
                o.Remove("pos");
                o.Remove("rotY");
                return o;
            }));
            root["spawnLines"] = new JArray(LayoutScene.Lines(scene).Select(l =>
            {
                var pts = new JArray();
                for (int i = 0; i < l.transform.childCount; i++) pts.Add(V(l.transform.GetChild(i).position));
                if (pts.Count == 0) pts.Add(V(l.transform.position));
                return new JObject
                {
                    ["name"] = l.name, ["center"] = V(LayoutScene.LineCenter(l.transform)), ["points"] = pts,
                    ["flying"] = l.canSpawnFlying, ["smallGround"] = l.canSpawnSmallGround, ["bigGround"] = l.canSpawnBigGround,
                };
            }));
            root["scenery"] = new JArray(LayoutScene.Props(scene).Select(p => new JObject
            {
                ["base"] = p.Id, ["name"] = p.Name, ["count"] = p.Count,
                // Size of a prop of this base at scale 1 (as the map uses it), metres: [x, height, z].
                ["size"] = PropSize(p),
            }));
            root["grid"] = Grid(cell);
            return root;
        }

        private static JObject Slot(BuildSlot s)
        {
            var hp = BuildingHp(s);
            var o = new JObject
            {
                ["name"] = s.name,
                ["kind"] = s.buildingName,
                ["display"] = LayoutScene.TypeName(s),
                ["pos"] = V(s.transform.position),
                ["rotY"] = R(s.transform.eulerAngles.y),
            };
            if (LayoutScene.IsCastle(s)) o["castle"] = true;
            if (s.ActivatorBuilding != null)
            {
                o["activator"] = s.ActivatorBuilding.name;
                o["activatorLevel"] = s.ActivatorLevel;
            }
            o["startDeactivated"] = s.StartDeactivated;
            if (s.ActivatorUpgradesThis) o["followsActivator"] = true;
            if (s.RequiredRootLevelDifference > -100) o["upgradeCap"] = s.RequiredRootLevelDifference;
            o["canBeBase"] = LayoutScene.CanBeBase(s);
            if (hp != null) o["hp"] = R(hp.maxHp);
            // A training slot's units: where each stands, [x, z] metres in the slot's frame (LevelDef buildSlots units).
            var units = UnitHomes.Spots(s);
            if (units.Count > 0) o["units"] = new JArray(units.Select(u => new JArray(R(u.X), R(u.Z))));
            o["upgrades"] = new JArray(s.OwnUpgrades.Select(u => new JObject
            {
                ["cost"] = u.cost,
                ["disabled"] = u.disableInThisMode ? true : null,
                ["branches"] = new JArray((u.upgradeBranches ?? []).Select(Branch)),
            }));
            return o;
        }

        private static JObject Branch(BuildSlot.UpgradeBranch b)
        {
            var o = new JObject { ["name"] = b.choiceDetails != null ? b.choiceDetails.name : null };
            if (b.goldIncomeChange != 0) o["income"] = b.goldIncomeChange;
            if (b.hpChange != 0) o["hp"] = b.hpChange;
            var troops = new SortedSet<string>();
            int attackers = 0;
            float range = 0;
            foreach (var go in b.objectsToActivate ?? [])
            {
                if (go == null) continue;
                foreach (var unit in go.GetComponentsInChildren<PathfindMovementPlayerunit>(true))
                {
                    troops.Add(Clean(unit.gameObject.name));
                }
                foreach (var a in go.GetComponentsInChildren<AutoAttack>(true))
                {
                    if (a.GetComponentInParent<PathfindMovementPlayerunit>(true) != null) continue;
                    attackers++;
                    if (a.targetPriorities.Count > 0) range = Mathf.Max(range, a.targetPriorities.Max(t => t.range));
                }
            }
            // Troop types the branch switches on (how many of them fight depends on the building; not counted here).
            if (troops.Count > 0) o["troops"] = new JArray(troops);
            if (attackers > 0) { o["attackers"] = attackers; o["range"] = R(range); }
            return o;
        }

        private static JArray PropSize(PropBase p)
        {
            if (p.Mesh == null) return null;
            var b = p.Mesh.bounds;
            var m = Matrix4x4.TRS(Vector3.zero, p.Upright, p.Scale);
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var c = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }
            var s = max - min;
            return [R(s.x), R(s.y), R(s.z)];
        }

        private static string Clean(string name)
        {
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return (paren > 0 ? name.Substring(0, paren) : name).Trim();
        }

        private static readonly float[,] Sample = { { 0.5f, 0.5f }, { 0.25f, 0.25f }, { 0.75f, 0.25f }, { 0.25f, 0.75f }, { 0.75f, 0.75f } };

        private static JObject Grid(float cell)
        {
            var astar = AstarPath.active;
            if (astar == null) return null;
            var small = astar.data.FindGraph(g => g.name == "Enemy Units S");
            var large = astar.data.FindGraph(g => g.name == "Enemy Units L");
            if (small == null) return null;
            // Bounds: the small enemy graph's nodes, plus a margin.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            small.GetNodes(n =>
            {
                var p = (Vector3)n.position;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            });
            if (minX > maxX) return null;
            const float margin = 12f;
            minX = Mathf.Floor((minX - margin) / cell) * cell;
            minZ = Mathf.Floor((minZ - margin) / cell) * cell;
            int w = Mathf.CeilToInt((maxX + margin - minX) / cell), h = Mathf.CeilToInt((maxZ + margin - minZ) / cell);
            // Keep huge maps to a sane size.
            while ((long)w * h > 250_000) { cell *= 1.5f; w = Mathf.CeilToInt((maxX + margin - minX) / cell); h = Mathf.CeilToInt((maxZ + margin - minZ) / cell); }

            // Ground height from the Ground layer, then the enemy navmeshes rasterised on top: a cell is walkable when its
            // centre lies in a walkable triangle (its height is then the navmesh's, which is what units stand on).
            var hts = new float[w * h];
            var kind = new char[w * h];
            int groundLayer = LayerMask.NameToLayer("Ground");
            int mask = groundLayer >= 0 ? 1 << groundLayer : Physics.DefaultRaycastLayers;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    kind[i] = '#';
                    hts[i] = float.NaN;
                    if (Physics.Raycast(new Vector3(minX + (x + 0.5f) * cell, 300f, minZ + (z + 0.5f) * cell), Vector3.down, out var hit, 600f, mask, QueryTriggerInteraction.Ignore))
                    {
                        hts[i] = hit.point.y;
                        kind[i] = '.';
                    }
                }
            void Rasterise(NavGraph graph, bool isLarge)
            {
                var onLarge = new bool[w * h];
                graph.GetNodes(node =>
                {
                    if (!node.Walkable || node is not TriangleMeshNode tri) return;
                    Vector3 a = (Vector3)tri.GetVertex(0), b = (Vector3)tri.GetVertex(1), c = (Vector3)tri.GetVertex(2);
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) - minX) / cell)), x1 = Mathf.Min(w - 1, Mathf.FloorToInt((Mathf.Max(a.x, b.x, c.x) - minX) / cell));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, b.z, c.z) - minZ) / cell)), z1 = Mathf.Min(h - 1, Mathf.FloorToInt((Mathf.Max(a.z, b.z, c.z) - minZ) / cell));
                    float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(den) < 1e-6f) return;
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            // The centre or any quarter point inside the triangle (thin bridges fall between centres).
                            float l1 = 0, l2 = 0, l3 = -1;
                            for (int k = 0; k < 5 && (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f); k++)
                            {
                                float px = minX + (x + Sample[k, 0]) * cell, pz = minZ + (z + Sample[k, 1]) * cell;
                                l1 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / den;
                                l2 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / den;
                                l3 = 1f - l1 - l2;
                            }
                            if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                            int i = z * w + x;
                            if (isLarge) { onLarge[i] = true; continue; }
                            float y = l1 * a.y + l2 * b.y + l3 * c.y;
                            if (kind[i] != 's' || y > hts[i]) hts[i] = y;
                            kind[i] = 's';
                        }
                });
                if (isLarge)
                    for (int i = 0; i < w * h; i++)
                        if (onLarge[i] && kind[i] == 's') kind[i] = 'w';
            }
            Rasterise(small, false);
            if (large != null) Rasterise(large, true);
            var heights = new byte[w * h * 2];
            var rows = new JArray();
            for (int z = 0; z < h; z++)
            {
                var row = new char[w];
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    row[x] = kind[i];
                    short cm = float.IsNaN(hts[i]) ? short.MinValue : (short)Mathf.Clamp(Mathf.RoundToInt(hts[i] * 100f), short.MinValue + 1, short.MaxValue);
                    heights[2 * i] = (byte)(cm & 0xff);
                    heights[2 * i + 1] = (byte)((cm >> 8) & 0xff);
                }
                rows.Add(new string(row));
            }
            return new JObject
            {
                ["origin"] = new JArray(R(minX), R(minZ)),
                ["cell"] = R(cell),
                ["size"] = new JArray(w, h),
                ["heights"] = Convert.ToBase64String(heights),
                ["walk"] = rows,
            };
        }
    }
}
