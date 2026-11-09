using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    `waves` and `spawnGroup` (docs/builder-engine.md, "Actions", World).

    Nights are the EnemySpawner's own Wave list (WaveInjector put the level's nights there). A future night is edited
    in place (its Spawn entries), so the night preview and the spawn markers show the change. The running night
    ("current", or the night that is running) is edited through the same Spawn objects: an added Spawn is Reset and
    appended to the running Wave (the spawner resumes if it had finished), and dropped spawns are marked finished.
    scaleHp keeps a multiplier per Spawn, applied around Spawn.Update by scaling the prefab's maxHp for the instantiate
    (LogicWorldVerbsManager.ScaleSpawnHp).

    spawnGroup spawns like the game's death spawns (Hp.enemyToSpawnOnDeath): Instantiate the unit (a Builder template
    or a vanilla prefab), then Spawn.AdjustEnemyParametersAfterSpawn (perks, elite). Enemies are EnemyOwned, so they
    count toward the night; while a group still has spawns left at night, the night stays open
    (LogicWorldVerbsManager.KeepNightOpen).

    Edits to future nights are logged and replayed on savestate load and retry (the reload rebuilds the waves).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class WaveVerbs
    {
        internal sealed class Group
        {
            public GameObject Prefab;
            public string Unit, LineName;
            public Transform Line;
            public Vector3? At;
            public int Left, Spawned;
            public int[] Gold;
            public float Interval, Timer;
            public bool Elite;
        }

        internal static readonly List<Group> Groups = [];
        /// <summary>scaleHp multipliers per game Spawn entry.</summary>
        internal static readonly Dictionary<Spawn, float> HpMult = [];
        /// <summary>Edits of future nights: (wave index, params as resolved), replayed after a reload.</summary>
        private static readonly List<JObject> edits = [];

        public static void Register()
        {
            LogicActions.Register("waves", Waves);
            LogicActions.Register("spawnGroup", SpawnGroup);
            WorldSession.Reset += () => { Groups.Clear(); HpMult.Clear(); edits.Clear(); holdUntil = 0f; };
        }

        // A unit counts as an enemy once its TaggedObject has registered (a frame after the instantiate): the night
        // stays open a moment past the last spawn so it doesn't end in between.
        private static float holdUntil;
        public static bool GroupsPending => Groups.Count > 0 || Time.time < holdUntil;

        // ------------------------------------------------------------------ units and lines

        private static GameObject ResolveUnit(string unit, string action)
        {
            if (string.IsNullOrEmpty(unit)) throw new LogicError($"{action}: needs \"unit\"");
            var pack = WorldCommon.Session?.Pack;
            var prefab = BuilderServices.Units.Resolve(pack, unit, out string error);
            if (prefab == null && !unit.Contains(":")) prefab = BuilderServices.Units.Resolve(pack, "vanilla:" + unit, out _);
            return prefab ?? throw new LogicError($"{action}: unit \"{unit}\": {error}");
        }

        private static Dictionary<string, EnemySpawnLine> Lines()
        {
            var lines = new Dictionary<string, EnemySpawnLine>(StringComparer.Ordinal);
            foreach (var line in EnemySpawner.instance.GetComponentsInChildren<EnemySpawnLine>(true))
                if (!lines.ContainsKey(line.name)) lines[line.name] = line;
            return lines;
        }

        private static Transform ResolveLine(string name, string action)
        {
            if (name == null) return null;
            if (name.StartsWith("line:")) name = name.Substring(5);
            var lines = Lines();
            if (lines.TryGetValue(name, out var l)) return l.transform;
            var ci = lines.Values.FirstOrDefault(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase));
            return ci != null ? ci.transform : throw new LogicError($"{action}: no spawn line \"{name}\" (lines: {string.Join(", ", lines.Keys)})");
        }

        private static bool Matches(Spawn s, GameObject prefab) =>
            prefab == null || s.enemyPrefab == prefab || (s.enemyPrefab != null && s.enemyPrefab.name == prefab.name && !BuilderServices.Units.IsTemplate(prefab) && !BuilderServices.Units.IsTemplate(s.enemyPrefab));

        // ------------------------------------------------------------------ waves

        /// <summary>The night's wave index and whether it's the one running now.</summary>
        private static int NightIndex(ActionParams p, out bool current)
        {
            var spawner = EnemySpawner.instance;
            int wn = spawner.Wavenumber;
            int idx;
            if (p.Raw("night") is JValue { Type: JTokenType.String } v && (string)v == "current")
            {
                if (!WorldCommon.IsNight) throw new LogicError("waves: night \"current\" but it's day");
                idx = wn;
            }
            else if (p.Has("night")) idx = (int)Math.Round(p.Number("night")) - 1;
            else idx = wn + 1;
            current = WorldCommon.IsNight && idx == wn;
            if (idx < 0 || idx >= spawner.WaveCount) throw new LogicError($"waves: there is no night {idx + 1} (the level has {spawner.WaveCount})");
            if (idx < wn || (idx == wn && !current)) throw new LogicError($"waves: night {idx + 1} is over");
            return idx;
        }

        private static void Waves(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("waves");
            string op = p.Text("op") ?? throw new LogicError("waves: needs \"op\"");
            var spawner = EnemySpawner.instance;
            switch (op)
            {
                case "startNight":
                {
                    if (WorldCommon.IsNight || !DayNightCycle.Instance.AfterSunrise) return;
                    if (p.Has("night"))
                    {
                        int n = Mathf.Clamp((int)Math.Round(p.Number("night")), 1, spawner.WaveCount);
                        WorldCommon.WavenumberRef(spawner) = n - 2;
                        spawner.Refresh();
                    }
                    DayNightCycle.Instance.SwitchToNight();
                    return;
                }
                case "endNight":
                {
                    string survivors = p.Text("survivors", "remove");
                    if (survivors != "remove" && survivors != "stay") throw new LogicError($"waves endNight: survivors must be \"stay\" or \"remove\", not \"{survivors}\"");
                    GameRules.EndNight(survivors == "remove");
                    return;
                }
                case "skipNight":
                {
                    if (WorldCommon.IsNight || spawner.SpawningInProgress) throw new LogicError("waves skipNight: only by day (it skips the coming night)");
                    if (spawner.Wavenumber + 2 >= spawner.WaveCount) throw new LogicError("waves skipNight: the coming night is the last one (use endLevel)");
                    WorldCommon.WavenumberRef(spawner)++;
                    spawner.Refresh();
                    return;
                }
            }

            int idx = NightIndex(p, out bool current);
            var edit = new JObject { ["op"] = op, ["night"] = idx };
            Apply(spawner, idx, current, op, edit, p);
            if (!current)
            {
                edits.Add(edit);
                if (idx == spawner.Wavenumber + 1 && !WorldCommon.IsNight && !spawner.SpawningInProgress) spawner.Refresh();
            }
        }

        /// <summary>Applies one op; reads params from <paramref name="p"/> (null when replaying) and records them in <paramref name="edit"/>.</summary>
        private static void Apply(EnemySpawner spawner, int idx, bool current, string op, JObject edit, ActionParams p)
        {
            var wave = spawner.waves[idx];
            string Text(string name) { if (p != null && p.Has(name)) edit[name] = p.Text(name); return (string)edit[name]; }
            double Num(string name, double fallback) { if (p != null && p.Has(name)) edit[name] = p.Number(name); return edit[name] != null ? (double)edit[name] : fallback; }
            bool Flag(string name) { if (p != null && p.Has(name)) edit[name] = p.Bool(name); return edit[name] != null && (bool)edit[name]; }
            string unit = p != null && p.Has("unit") ? (string)(edit["unit"] = p.Template("unit")) : (string)edit["unit"];

            switch (op)
            {
                case "addSpawn":
                {
                    var prefab = ResolveUnit(unit, "waves addSpawn");
                    string lineName = Text("line");
                    var line = lineName != null ? ResolveLine(lineName, "waves addSpawn")
                        : wave.spawns.FirstOrDefault(s => s?.spawnLine != null)?.spawnLine ?? Lines().Values.FirstOrDefault()?.transform
                        ?? throw new LogicError("waves addSpawn: the level has no spawn line");
                    int count = Math.Max(0, (int)Math.Round(Num("count", 1)));
                    if (count == 0) return;
                    var spawn = new Spawn(prefab, line, count, (float)Num("interval", 0.5), (int)Math.Round(Num("gold", 0)), (float)Num("delay", 0), Flag("elite"));
                    if (current)
                    {
                        spawn.Reset();
                        wave.spawns.Add(spawn);
                        if (!spawner.SpawningInProgress) WorldCommon.SpawningRef(spawner) = true;
                    }
                    else wave.spawns.Add(spawn);
                    break;
                }
                case "removeUnit":
                {
                    var prefab = ResolveUnit(unit, "waves removeUnit");
                    foreach (var s in wave.spawns.Where(s => s != null && Matches(s, prefab)).ToList())
                    {
                        if (current) Finish(s);
                        else wave.spawns.Remove(s);
                    }
                    if (current) Groups.RemoveAll(g => g.Prefab == prefab);
                    break;
                }
                case "scaleCount":
                {
                    double k = Num("value", 1);
                    var prefab = unit != null ? ResolveUnit(unit, "waves scaleCount") : null;
                    foreach (var s in wave.spawns.Where(s => s != null && Matches(s, prefab)).ToList())
                    {
                        if (current)
                        {
                            if (s.Finished) continue;
                            int left = s.count - s.SpawnedUnits;
                            s.count = s.SpawnedUnits + Math.Max(0, (int)Math.Round(left * k));
                            if (s.count <= s.SpawnedUnits) Finish(s);
                        }
                        else
                        {
                            s.count = Math.Max(0, (int)Math.Round(s.count * k));
                            if (s.count == 0) wave.spawns.Remove(s);
                        }
                    }
                    break;
                }
                case "scaleHp":
                {
                    float k = (float)Num("value", 1);
                    if (k <= 0) throw new LogicError("waves scaleHp: value must be above 0");
                    var prefab = unit != null ? ResolveUnit(unit, "waves scaleHp") : null;
                    foreach (var s in wave.spawns.Where(s => s != null && Matches(s, prefab) && !(current && s.Finished)))
                        HpMult[s] = (HpMult.TryGetValue(s, out var m) ? m : 1f) * k;
                    break;
                }
                case "clearRemaining":
                {
                    string lineName = Text("line");
                    var line = lineName != null ? ResolveLine(lineName, "waves clearRemaining") : null;
                    foreach (var s in wave.spawns.Where(s => s != null && (line == null || s.spawnLine == line)).ToList())
                    {
                        if (current) Finish(s);
                        else wave.spawns.Remove(s);
                    }
                    if (current) Groups.RemoveAll(g => line == null || g.Line == line);
                    break;
                }
                default:
                    throw new LogicError($"waves: unknown op \"{op}\" (addSpawn, removeUnit, scaleCount, scaleHp, clearRemaining, startNight, endNight, skipNight)");
            }
        }

        private static void Finish(Spawn s)
        {
            WorldCommon.SpawnFinishedRef(s) = true;
            s.count = Math.Min(s.count, s.SpawnedUnits);
        }

        // ------------------------------------------------------------------ spawnGroup

        private static void SpawnGroup(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("spawnGroup");
            string unit = p.Template("unit");
            var prefab = ResolveUnit(unit, "spawnGroup");
            int count = Mathf.Clamp((int)Math.Round(p.Number("count", 1)), 0, 200);
            if (count == 0) return;
            var g = new Group
            {
                Prefab = prefab, Unit = unit, Left = count,
                Interval = Mathf.Max(0f, (float)p.Number("interval", 0.5)),
                Elite = p.Bool("elite"),
            };
            if (p.Has("line"))
            {
                g.LineName = p.Text("line");
                g.Line = ResolveLine(g.LineName, "spawnGroup");
                g.LineName = g.Line.name;
            }
            else g.At = WorldCommon.At(ctx, p) ?? throw new LogicError("spawnGroup: needs \"line\" or \"at\"");
            // The game spreads a spawn's coins over random members (Spawn.Reset); same here, from the logic random source.
            int gold = Math.Max(0, (int)Math.Round(p.Number("gold")));
            g.Gold = new int[count];
            for (int i = 0; i < gold; i++) g.Gold[ctx.World != null ? ctx.World.Random.RangeInt(0, count - 1) : UnityEngine.Random.Range(0, count)]++;
            Groups.Add(g);
            Tick(0f);   // the first one now
        }

        /// <summary>Spawns what's due (game time; nothing while paused).</summary>
        public static void Tick(float dt)
        {
            if (Groups.Count == 0) return;
            if (!WorldCommon.InMatch) { Groups.Clear(); return; }
            for (int i = 0; i < Groups.Count; i++)
            {
                var g = Groups[i];
                g.Timer -= dt;
                int guard = 0;
                while (g.Left > 0 && g.Timer <= 0f && guard++ < 50)
                {
                    SpawnOne(g);
                    g.Left--;
                    g.Timer += g.Interval;
                }
            }
            Groups.RemoveAll(g => g.Left <= 0 || g.Prefab == null);
        }

        private static void SpawnOne(Group g)
        {
            var tagged = g.Prefab.GetComponentInChildren<TaggedObject>(true);
            bool flying = tagged != null && tagged.Tags.Contains(TagManager.ETag.Flying);
            bool large = tagged != null && tagged.Tags.Contains(TagManager.ETag.LargeUnit);
            Vector3 pos;
            if (g.Line != null)
            {
                var spawner = EnemySpawner.instance;
                var constraint = large ? spawner.groundConstraintLarge : flying ? spawner.flyingConstraint : spawner.groundConstraint;
                pos = new Spawn(g.Prefab, g.Line).GetRandomPointOnSpawnLine(flying, constraint);
            }
            else
            {
                pos = EnemySpawner.GetValidSpawnPointForEnemy(large, flying, g.At.Value);
                if (flying) pos += Vector3.up * 5f;
            }
            var go = Object.Instantiate(g.Prefab, pos, Quaternion.identity);
            if (!go.activeSelf) go.SetActive(true);
            var manager = EnemySpawnManager.instance;
            if (manager != null && manager.weaponOnSpawn)
                manager.weaponOnSpawn.Attack(go.transform.position + Vector3.up * manager.weaponAttackHeight, null, Vector3.forward, go.GetComponent<TaggedObject>());
            var hp = go.GetComponentInChildren<Hp>();
            if (hp != null && g.Spawned < g.Gold.Length) hp.coinCount = g.Gold[g.Spawned];
            Spawn.AdjustEnemyParametersAfterSpawn(go, g.Elite);
            if (WorldCommon.IsNight) EnemySpawner.totalEnemiesSpawnedThisWave++;
            g.Spawned++;
            holdUntil = Time.time + 1f;
            if (g.LineName != null && LogicWorld.Current?.Find("line:" + g.LineName) is { } lineEntity)
                LogicEvents.Raise("lineSpawn", lineEntity, LogicGame.EntityOf(go), pos: pos.ToVec());
        }

        // ------------------------------------------------------------------ savestates

        internal static JToken Capture() => new JArray(edits.Select(e => e.DeepClone()));

        internal static void Restore(JToken state)
        {
            edits.Clear();
            HpMult.Clear();
            var spawner = EnemySpawner.instance;
            if (state is not JArray list || spawner == null) return;
            foreach (var e in list.OfType<JObject>())
            {
                int idx = (int?)e["night"] ?? -1;
                if (idx <= spawner.Wavenumber || idx >= spawner.WaveCount) continue;
                try
                {
                    Apply(spawner, idx, false, (string)e["op"], e, null);
                    edits.Add(e);
                }
                catch (LogicError ex) { WorldCommon.log.Warn($"Couldn't replay a waves edit after a load: {ex.Message}"); }
            }
            if (!WorldCommon.IsNight && spawner.Wavenumber + 1 < spawner.WaveCount) spawner.Refresh();
        }
    }
}
