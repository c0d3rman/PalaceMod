using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    Index of vanilla content, built from assets that are loaded from the main menu on (no scene loads needed):
    - enemies: every Spawn.enemyPrefab in every LevelInfo.enemySpawner prefab's waves, every EternalTrialEnemySet,
      what they spawn on death, and the God of Afterlife enemy;
    - player units: loaded prefabs with PathfindMovementPlayerunit + Hp (P Knight, P Archer, ...);
    - weapons: the Weapon assets of all of the above;
    - hosts: LevelInfos with an enemy spawner, with spawn lines read from the spawner prefab.
    Ids are prefab / asset names ("E Racer"), matched exactly, then case-insensitively.
*/
namespace PalaceMod.Builder
{
    public sealed class Catalog : ICatalog
    {
        private List<CatalogUnit> units;
        private List<CatalogWeapon> weapons;
        private List<CatalogHost> hosts;
        /// <summary>Set by BuilderManager so validation can see other installed packs.</summary>
        public Func<string, string, bool> PackUnitExists;

        public IReadOnlyList<CatalogUnit> Units { get { Build(); return units; } }
        public IReadOnlyList<CatalogWeapon> Weapons { get { Build(); return weapons; } }
        public IReadOnlyList<CatalogHost> Hosts { get { Build(); return hosts; } }

        /// <summary>Whether the game data the catalog reads exists yet (false during boot).</summary>
        public static bool GameReady => LevelProgressManager.instance != null && LevelProgressManager.instance.levelInfos != null;

        public bool IsBuilt => units != null;

        public void Invalidate() => units = null;

        public CatalogUnit FindUnit(string id) => Find(Units, u => u.Id, id);
        public CatalogWeapon FindWeapon(string id) => Find(Weapons, w => w.Id, id);
        public CatalogHost FindHost(string scene) => Find(Hosts, h => h.Scene, scene);

        public bool HasVanillaUnit(string id, string side)
        {
            var u = FindUnit(id);
            return u != null && (side == null || u.Side == side);
        }

        public bool HasVanillaWeapon(string id) => FindWeapon(id) != null;
        public bool HasHost(string scene) => FindHost(scene) != null;
        public IReadOnlyCollection<string> SpawnLines(string scene) => FindHost(scene)?.SpawnLines.Select(l => l.Name).ToList();
        public bool HasPackUnit(string packId, string unitId) => PackUnitExists?.Invoke(packId, unitId) ?? false;

        private static T Find<T>(IReadOnlyList<T> list, Func<T, string> key, string id) where T : class
        {
            if (id == null) return null;
            return list.FirstOrDefault(x => key(x) == id) ?? list.FirstOrDefault(x => string.Equals(key(x), id, StringComparison.OrdinalIgnoreCase));
        }

        private void Build()
        {
            if (units != null) return;
            if (!GameReady) throw new InvalidOperationException("the game hasn't loaded its level data yet");
            var started = DateTime.UtcNow;
            var unitList = new List<CatalogUnit>();
            var weaponMap = new Dictionary<Weapon, CatalogWeapon>();
            var seenPrefabs = new HashSet<GameObject>();
            var difficulty = new Dictionary<GameObject, float>();

            foreach (var set in Resources.FindObjectsOfTypeAll<EternalTrialEnemySet>())
                foreach (var e in set.enemies ?? [])
                    if (e?.enemyPrefab != null && !difficulty.ContainsKey(e.enemyPrefab)) difficulty[e.enemyPrefab] = e.difficultyValue;

            var queue = new Queue<GameObject>();
            void Enqueue(GameObject go) { if (go != null && !go.scene.IsValid() && go.transform.parent == null && seenPrefabs.Add(go)) queue.Enqueue(go); }

            var levelInfos = LevelProgressManager.instance.levelInfos.Where(l => l != null).ToList();
            foreach (var info in levelInfos)
                foreach (var wave in info.enemySpawner?.waves ?? [])
                    foreach (var spawn in wave.spawns ?? [])
                        Enqueue(spawn.enemyPrefab);
            foreach (var prefab in difficulty.Keys) Enqueue(prefab);
            Enqueue(PerkManager.instance?.godOfAfterlifeEnemy);
            foreach (var mover in Resources.FindObjectsOfTypeAll<PathfindMovementPlayerunit>())
                if (mover.transform.parent == null && mover.GetComponent<Hp>() != null) Enqueue(mover.gameObject);

            while (queue.Count > 0)
            {
                var prefab = queue.Dequeue();
                var unit = Describe(prefab, weaponMap);
                if (unit == null) continue;
                difficulty.TryGetValue(prefab, out unit.Difficulty);
                unitList.Add(unit);
                Enqueue(prefab.GetComponentInChildren<Hp>(true)?.enemyToSpawnOnDeath);
            }

            // Names must be unique to work as ids; vanilla has a few duplicate prefab names, which get a suffix.
            foreach (var group in unitList.GroupBy(u => u.Id).Where(g => g.Count() > 1))
            {
                int n = 1;
                foreach (var u in group.Skip(1)) u.Id = $"{u.Id} #{++n}";
            }

            units = unitList.OrderBy(u => u.Side).ThenBy(u => u.Id, StringComparer.OrdinalIgnoreCase).ToList();
            weapons = weaponMap.Values.GroupBy(w => w.Id).Select(g => g.First()).OrderBy(w => w.Id, StringComparer.OrdinalIgnoreCase).ToList();
            hosts = levelInfos
                .Where(l => l.enemySpawner != null && !string.IsNullOrEmpty(l.sceneName) && !l.sceneName.Contains("Tutorial") && LevelProgressManager.SceneExists(l.sceneName))
                .GroupBy(l => l.sceneName).Select(g => DescribeHost(g.First()))
                .ToList();
            Plugin.Logger.LogInfo($"Builder: catalog built: {units.Count} units, {weapons.Count} weapons, {hosts.Count} host maps " +
                $"in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        }

        private static CatalogUnit Describe(GameObject prefab, Dictionary<Weapon, CatalogWeapon> weapons)
        {
            var tagged = prefab.GetComponentInChildren<TaggedObject>(true);
            var hp = prefab.GetComponentInChildren<Hp>(true);
            if (tagged == null || hp == null) return null;
            bool enemy = tagged.Tags.Contains(TagManager.ETag.EnemyOwned);
            bool player = tagged.Tags.Contains(TagManager.ETag.PlayerOwned);
            if (!enemy && !player) return null;
            var icon = prefab.GetComponent<ScreenMarkerIcon>();
            var attacks = prefab.GetComponentsInChildren<AutoAttack>(true);
            var attack = attacks.FirstOrDefault();
            var enemyMove = prefab.GetComponentInChildren<PathfindMovementEnemy>(true);
            var playerMove = prefab.GetComponentInChildren<PathfindMovementPlayerunit>(true);
            var unit = new CatalogUnit
            {
                Id = prefab.name,
                Side = enemy ? UnitSide.Enemy : UnitSide.Player,
                Prefab = prefab,
                Icon = icon?.sprite,
                DisplayName = SafeTranslate(() => icon?.UnitName) ?? prefab.name,
                Description = SafeTranslate(() => icon?.UnitDescription),
                MaxHp = hp.maxHp,
                MoveSpeed = enemyMove?.movementSpeed ?? playerMove?.movementSpeed ?? 0,
                KeepDistance = enemyMove?.keepDistanceOf ?? playerMove?.keepDistanceOf ?? 0,
                AggroTime = enemyMove?.agroTimeWhenAttackedByPlayer ?? playerMove?.agroTimeWhenAttackedByPlayer ?? 0,
                AttackCooldown = attack?.cooldownDuration ?? 0,
                AttackRange = attack?.targetPriorities.Count > 0 ? attack.targetPriorities.Max(t => t.range) : 0,
                Tags = tagged.Tags.Select(t => t.ToString()).ToList(),
                Flying = tagged.Tags.Contains(TagManager.ETag.Flying),
                Large = tagged.Tags.Contains(TagManager.ETag.LargeUnit),
            };
            foreach (var a in attacks)
            {
                if (a.weapon == null) continue;
                if (!weapons.TryGetValue(a.weapon, out var w))
                    weapons[a.weapon] = w = DescribeWeapon(a.weapon);
                unit.Weapon ??= w.Id;
            }
            return unit;
        }

        private static string SafeTranslate(Func<string> f)
        {
            try
            {
                string s = f();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static List<DamageModDef> ToDefs(List<DamageModifyer> mods) =>
            (mods ?? []).Select(m => new DamageModDef { Tags = m.requiredTags.Select(t => t.ToString()).ToList(), Add = m.damageAdded, Mult = m.damageMultiplyer }).ToList();

        private static CatalogWeapon DescribeWeapon(Weapon weapon) => new()
        {
            Id = weapon.name,
            Asset = weapon,
            Damage = ToDefs(weapon.directDamage),
            Splash = ToDefs(weapon.splashDamage),
            ProjectileSpeed = weapon.projectileSpeed,
        };

        private static CatalogHost DescribeHost(LevelInfo info)
        {
            var spawner = info.enemySpawner;
            var host = new CatalogHost
            {
                Scene = info.sceneName,
                DisplayName = SafeTranslate(() => info.LocalizedDisplayName) ?? info.displayName ?? info.sceneName,
                Info = info,
                StartingGold = spawner.goldBalanceAtStart,
            };
            foreach (var line in spawner.GetComponentsInChildren<EnemySpawnLine>(true))
            {
                var t = line.transform;
                Vector3 center = t.childCount > 0 ? (t.GetChild(0).position + t.GetChild(t.childCount - 1).position) / 2f : t.position;
                float length = 0;
                for (int i = 0; i < t.childCount - 1; i++) length += (t.GetChild(i).position - t.GetChild(i + 1).position).magnitude;
                host.SpawnLines.Add(new CatalogSpawnLine
                {
                    Name = line.name, Center = center, Length = length,
                    Flying = line.canSpawnFlying, SmallGround = line.canSpawnSmallGround, BigGround = line.canSpawnBigGround,
                });
            }
            host.VanillaWaves = new WaveSetDef
            {
                Id = Ids.FromName(info.sceneName) + "-vanilla",
                Name = host.DisplayName,
                Host = info.sceneName,
                StartingGold = spawner.goldBalanceAtStart,
                Nights = spawner.waves.Select(w => new NightDef
                {
                    Warning = string.IsNullOrEmpty(w.warningText) ? null : w.warningText,
                    Difficulty = w.difficultyMulti,
                    Spawns = w.spawns.Where(s => s.enemyPrefab != null && s.spawnLine != null).Select(s => new SpawnDef
                    {
                        Unit = ContentRef.Vanilla(s.enemyPrefab.name),
                        Count = s.count, Interval = s.interval, Delay = s.delay, Line = s.spawnLine.name, Gold = s.goldCoins, Elite = s.eliteEnemies,
                    }).ToList(),
                }).ToList(),
            };
            return host;
        }

        public string ToJson()
        {
            static float R(float f) => (float)Math.Round(f, 3);
            static JArray V(Vector3 v) => [R(v.x), R(v.y), R(v.z)];
            var root = new JObject
            {
                ["format"] = "palace-builder-catalog",
                ["version"] = DesignPack.CurrentVersion,
                ["gameVersion"] = Application.version,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["tags"] = new JArray(Enum.GetNames(typeof(TagManager.ETag))),
                ["behaviours"] = new JArray(BehaviourTypes.All),
                ["units"] = new JArray(Units.Select(u => new JObject
                {
                    ["id"] = u.Id, ["ref"] = ContentRef.Vanilla(u.Id), ["side"] = u.Side, ["name"] = u.DisplayName, ["description"] = u.Description,
                    ["maxHp"] = R(u.MaxHp), ["moveSpeed"] = R(u.MoveSpeed), ["attackCooldown"] = R(u.AttackCooldown), ["attackRange"] = R(u.AttackRange),
                    ["keepDistance"] = R(u.KeepDistance), ["aggroTime"] = R(u.AggroTime), ["weapon"] = u.Weapon,
                    ["flying"] = u.Flying, ["large"] = u.Large, ["difficulty"] = R(u.Difficulty), ["tags"] = new JArray(u.Tags),
                })),
                ["weapons"] = new JArray(Weapons.Select(w => new JObject
                {
                    ["id"] = w.Id, ["ref"] = ContentRef.Vanilla(w.Id), ["projectileSpeed"] = R(w.ProjectileSpeed),
                    ["damage"] = JArray.FromObject(w.Damage), ["splash"] = JArray.FromObject(w.Splash),
                })),
                ["hosts"] = new JArray(Hosts.Select(h => new JObject
                {
                    ["scene"] = h.Scene, ["name"] = h.DisplayName, ["startingGold"] = h.StartingGold, ["nights"] = h.VanillaWaves.Nights.Count,
                    ["spawnLines"] = new JArray(h.SpawnLines.Select(l => new JObject
                    {
                        ["name"] = l.Name, ["center"] = V(l.Center), ["length"] = R(l.Length),
                        ["flying"] = l.Flying, ["smallGround"] = l.SmallGround, ["bigGround"] = l.BigGround,
                    })),
                    ["vanillaWaves"] = JObject.Parse(JsonConvert.SerializeObject(h.VanillaWaves, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })),
                })),
            };
            return root.ToString(Formatting.Indented);
        }
    }
}
