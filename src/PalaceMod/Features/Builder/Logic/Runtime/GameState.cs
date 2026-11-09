using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The game side of the expression language's globals (IGameState) and the default candidate source for selectors
    (ISpatial: the Builder registry plus every TagManager object, wrapped lazily).
*/
namespace PalaceMod.Builder.Logic
{
    internal sealed class GameState : IGameState
    {
        public int IncomeAtDawn;
        public float NightStartedAt = -1;

        private static EnemySpawner Spawner => EnemySpawner.instance;

        public bool IsNight => DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        /// <summary>1-based: the running night, or the coming one by day (Wavenumber is -1 on day 1, n-1 during night n
        /// and the day after it).</summary>
        /// Between dusk and the first spawn the game is already in its night state but Wavenumber hasn't moved yet,
        /// so "running" is tracked from StartSpawning to dawn.
        public int Night => Spawner == null ? 1 : NightRunning ? Spawner.Wavenumber + 1 : Spawner.Wavenumber + 2;

        /// <summary>From EnemySpawner.StartSpawning to dawn (LogicManager sets it).</summary>
        public bool NightRunning;
        public int Nights => Spawner != null ? Spawner.WaveCount : 0;
        public double NightTime => IsNight && NightStartedAt >= 0 ? Time.time - NightStartedAt : 0;

        public double NightProgress
        {
            get
            {
                var s = Spawner;
                if (s == null || !IsNight) return 0;
                if (!s.SpawningInProgress) return 1;
                int n = s.Wavenumber;
                if (n < 0 || n >= s.waves.Count) return 0;
                var spawns = s.waves[n].spawns;
                double total = 0, done = 0;
                foreach (var sp in spawns)
                {
                    total += Math.Max(1, sp.count);
                    done += sp.Finished ? Math.Max(1, sp.count) : Math.Min(sp.count, sp.SpawnedUnits);
                }
                return total <= 0 ? 1 : Math.Min(1, done / total);
            }
        }

        private static PlayerInteraction Player => TagManager.instance != null && TagManager.instance.Players.Count > 0 ? TagManager.instance.Players[0].GetComponent<PlayerInteraction>() : null;

        public int Gold => Player != null ? Player.Balance : 0;
        public int Income => IncomeAtDawn;
        public int EnemiesAlive => TagManager.instance != null ? TagManager.instance.CountAllTaggedObjectsWithTag(TagManager.ETag.EnemyOwned) : 0;
        public double Difficulty => 1;

        public LogicEntity King => TagManager.instance != null && TagManager.instance.Players.Count > 0 ? LogicGame.EntityOf(TagManager.instance.Players[0]) : null;
        public LogicEntity Castle => CastleCenter.instance != null ? LogicGame.EntityOf(CastleCenter.instance) : null;

        public int WaveCount(int night, string filter)
        {
            var s = Spawner;
            if (s == null || night < 1 || night > s.waves.Count) return 0;
            Func<LogicContext, LogicEntity, bool> f = string.IsNullOrEmpty(filter) ? null : ExprCompiler.CompileFilter(filter, "waveCount");
            int n = 0;
            foreach (var sp in s.waves[night - 1].spawns)
            {
                if (f != null && !f(new LogicContext { World = LogicWorld.Current }, new LogicEntity { Id = "?", Template = TemplateOf(sp.enemyPrefab), Kind = "unit", Team = "enemy" })) continue;
                n += sp.count;
            }
            return n;
        }

        public IReadOnlyList<string> WaveUnits(int night)
        {
            var s = Spawner;
            if (s == null || night < 1 || night > s.waves.Count) return [];
            return s.waves[night - 1].spawns.Select(sp => TemplateOf(sp.enemyPrefab)).Where(t => t != null).Distinct().ToList();
        }

        public string StrongestUnit(int night)
        {
            var s = Spawner;
            if (s == null || night < 1 || night > s.waves.Count) return null;
            string best = null; float bestScore = -1;
            foreach (var sp in s.waves[night - 1].spawns)
            {
                var hp = sp.enemyPrefab != null ? sp.enemyPrefab.GetComponentInChildren<Hp>(true) : null;
                float score = hp != null ? hp.maxHp : 0;
                if (score > bestScore) { bestScore = score; best = TemplateOf(sp.enemyPrefab); }
            }
            return best;
        }

        /// <summary>A prefab's template id: a Builder unit's id, else "vanilla:&lt;prefab name&gt;".</summary>
        internal static string TemplateOf(GameObject prefab)
        {
            if (prefab == null) return null;
            var be = prefab.GetComponent<BuilderEntity>();
            if (be != null && !string.IsNullOrEmpty(be.TemplateId)) return be.TemplateId;
            return "vanilla:" + prefab.name.Replace("(Clone)", "").Trim();
        }

        public Vec SpawnLine(string name)
        {
            var line = UnityEngine.Object.FindObjectsOfType<EnemySpawnLine>().FirstOrDefault(l => l.name == name);
            if (line == null) throw new LogicError($"no spawn line \"{name}\"");
            return line.transform.position.ToVec();
        }

        public Vec PointNear(Vec p, float r, LogicRandom rng)
        {
            var center = p.ToVector3();
            for (int i = 0; i < 8; i++)
            {
                double ang = rng.NextDouble() * Math.PI * 2, d = r * Math.Sqrt(rng.NextDouble());
                var q = center + new Vector3((float)(Math.Sin(ang) * d), 0, (float)(Math.Cos(ang) * d));
                var astar = AstarPath.active;
                if (astar == null) return q.ToVec();
                var nn = astar.GetNearest(q, Pathfinding.NNConstraint.Default);
                if (nn.node != null && nn.node.Walkable)
                {
                    var hit = nn.position;
                    if (Vector3.Distance(new Vector3(hit.x, 0, hit.z), new Vector3(q.x, 0, q.z)) < 2f) return hit.ToVec();
                }
            }
            return p;
        }
    }

    /// <summary>Selector candidates: the Builder registry, plus the game's tagged objects (wrapped on first sight).</summary>
    internal sealed class TagSpatial : ISpatial
    {
        private readonly List<TaggedObject> buffer = [];

        public void Query(Vec center, float radius, List<LogicEntity> into)
        {
            var world = LogicWorld.Current;
            if (world == null) return;
            var seen = new HashSet<LogicEntity>();
            float r2 = radius * radius;
            foreach (var e in world.Entities)
                if (e.Exists && e.Kind != "level" && (radius <= 0 || Vec.FlatDistanceSq(e.Position, center) <= r2) && seen.Add(e)) into.Add(e);
            var tm = TagManager.instance;
            if (tm == null) return;
            buffer.Clear();
            var c = center.ToVector3();
            foreach (var tag in new[] { TagManager.ETag.PlayerOwned, TagManager.ETag.EnemyOwned })
            {
                tm.FindAllTaggedObjectsWithTag(buffer, tag);
                foreach (var t in buffer)
                {
                    if (t == null) continue;
                    var p = t.transform.position;
                    if (radius > 0 && (p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z) > r2) continue;
                    var e = LogicGame.EntityOf(t);
                    if (e != null && seen.Add(e)) into.Add(e);
                }
                buffer.Clear();
            }
        }
    }
}
