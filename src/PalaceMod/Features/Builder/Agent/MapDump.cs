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
            root["scenery"] = new JArray(LayoutScene.Props(scene).Select(p => new JObject { ["base"] = p.Id, ["name"] = p.Name, ["count"] = p.Count }));
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
            var troops = new Dictionary<string, int>();
            int attackers = 0;
            float range = 0;
            foreach (var go in b.objectsToActivate ?? [])
            {
                if (go == null) continue;
                foreach (var unit in go.GetComponentsInChildren<PathfindMovementPlayerunit>(true))
                {
                    string name = Clean(unit.gameObject.name);
                    troops[name] = troops.TryGetValue(name, out int n) ? n + 1 : 1;
                }
                foreach (var a in go.GetComponentsInChildren<AutoAttack>(true))
                {
                    if (a.GetComponentInParent<PathfindMovementPlayerunit>(true) != null) continue;
                    attackers++;
                    if (a.targetPriorities.Count > 0) range = Mathf.Max(range, a.targetPriorities.Max(t => t.range));
                }
            }
            if (troops.Count > 0) o["troops"] = JObject.FromObject(troops);
            if (attackers > 0) { o["attackers"] = attackers; o["range"] = R(range); }
            return o;
        }

        private static string Clean(string name)
        {
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return (paren > 0 ? name.Substring(0, paren) : name).Trim();
        }

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

            var smallNn = NearestNodeConstraint.Walkable;
            smallNn.graphMask = GraphMask.FromGraphName("Enemy Units S");
            NearestNodeConstraint? largeNn = null;
            if (large != null)
            {
                var l = NearestNodeConstraint.Walkable;
                l.graphMask = GraphMask.FromGraphName("Enemy Units L");
                largeNn = l;
            }
            int groundLayer = LayerMask.NameToLayer("Ground");
            int mask = groundLayer >= 0 ? 1 << groundLayer : Physics.DefaultRaycastLayers;
            var heights = new byte[w * h * 2];
            var rows = new JArray();
            float reach = cell * 0.6f;
            for (int z = 0; z < h; z++)
            {
                var row = new char[w];
                for (int x = 0; x < w; x++)
                {
                    var p = new Vector3(minX + (x + 0.5f) * cell, 0f, minZ + (z + 0.5f) * cell);
                    short cm = short.MinValue;
                    if (Physics.Raycast(new Vector3(p.x, 300f, p.z), Vector3.down, out var hit, 600f, mask, QueryTriggerInteraction.Ignore))
                    {
                        cm = (short)Mathf.Clamp(Mathf.RoundToInt(hit.point.y * 100f), short.MinValue + 1, short.MaxValue);
                        p.y = hit.point.y;
                    }
                    bool OnGraph(NearestNodeConstraint? nn)
                    {
                        var info = astar.GetNearest(p + Vector3.up * 0.5f, nn.Value);
                        if (info.node == null) return false;
                        var d = info.position - p;
                        return new Vector2(d.x, d.z).magnitude <= reach && Mathf.Abs(d.y) < 3f;
                    }
                    if (cm == short.MinValue)
                    {
                        // No ground collider (some maps' walkable bridges): the navmesh's own height.
                        var info = astar.GetNearest(p + Vector3.up * 50f, smallNn);
                        if (info.node != null && new Vector2(info.position.x - p.x, info.position.z - p.z).magnitude <= reach)
                        {
                            p.y = info.position.y;
                            cm = (short)Mathf.RoundToInt(p.y * 100f);
                        }
                    }
                    char c = '#';
                    if (cm != short.MinValue)
                    {
                        bool s = OnGraph(smallNn);
                        bool l = largeNn != null && s && OnGraph(largeNn);
                        c = l ? 'w' : s ? 's' : '.';
                    }
                    row[x] = c;
                    int i = (z * w + x) * 2;
                    heights[i] = (byte)(cm & 0xff);
                    heights[i + 1] = (byte)((cm >> 8) & 0xff);
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
