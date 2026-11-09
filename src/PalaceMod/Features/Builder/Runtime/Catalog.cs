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
    - player units: the troops inside the troop buildings' Resources prefabs (TroopIndex: P Knight, P Archer, ...),
      plus any other loaded root prefab with PathfindMovementPlayerunit + Hp;
    - weapons: the Weapon assets of all of the above;
    - hosts: LevelInfos with an enemy spawner, with spawn lines read from the spawner prefab.
    Ids are prefab / asset names ("E Racer"), matched exactly, then case-insensitively.
*/
namespace PalaceMod.Builder
{
    public sealed class Catalog : ICatalog, ITroopValidationContext, IHostWavesContext
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
        // Host names must match the scene name exactly: scene loads and comparisons are case-sensitive.
        public bool HasHost(string scene) => Hosts.Any(h => h.Scene == scene);
        public IReadOnlyCollection<string> SpawnLines(string scene) => Hosts.FirstOrDefault(h => h.Scene == scene)?.SpawnLines.Select(l => l.Name).ToList();
        public bool HasPackUnit(string packId, string unitId) => PackUnitExists?.Invoke(packId, unitId) ?? false;

        public IReadOnlyCollection<string> VanillaWaveLines(string scene) =>
            Hosts.FirstOrDefault(h => h.Scene == scene)?.VanillaWaves?.Nights?.SelectMany(n => n?.Spawns ?? []).Select(s => s?.Line).Where(l => l != null).Distinct().ToList();

        public IReadOnlyCollection<string> TroopBuildings(string unitId)
        {
            var u = FindUnit(unitId);
            return u == null ? null : u.Side == UnitSide.Player ? u.Buildings : [];
        }

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

            // Troops are children of building prefabs, named after their troop type.
            foreach (var (proto, buildings, troopName) in TroopIndex.Prototypes())
            {
                string id = TroopIndex.TroopId(proto);
                var unit = unitList.FirstOrDefault(u => u.Id == id);
                if (unit == null)
                {
                    unit = Describe(proto, weaponMap);
                    if (unit == null || unit.Side != UnitSide.Player) continue;
                    unit.Id = id;
                    unit.Buildings = buildings;
                    unitList.Add(unit);
                }
                // A troop's own marker name can be missing (P Melee) or name its building ("Archery Range" for the
                // archers); the upgrade choice that recruits it (or the starting-troop name) is what the player knows.
                if (!string.IsNullOrEmpty(troopName) && (unit.DisplayName == Humanize(id) || buildings.Contains(unit.DisplayName))) unit.DisplayName = troopName;
            }

            // Two prefabs can share a player-facing name (E Melee and E Defensive Melee Variant are both "Swordsman"):
            // tell them apart by the words their ids don't share.
            foreach (var group in unitList.GroupBy(u => (u.Side, u.DisplayName)).Where(g => g.Count() > 1).ToList())
            {
                var shortest = group.OrderBy(u => u.Id.Length).First();
                var common = new HashSet<string>(Words(shortest.Id));
                foreach (var u in group.Where(u => u != shortest))
                {
                    var extra = Words(u.Id).Where(w => !common.Contains(w) && w != "Variant").ToList();
                    u.DisplayName += $" ({(extra.Count > 0 ? string.Join(" ", extra) : u.Id)})";
                }
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
            Log.For<BuilderManager>().Debug($"Catalog built: {units.Count} units, {weapons.Count} weapons, {hosts.Count} host maps " +
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
                DisplayName = UnitName(icon) ?? Humanize(prefab.name),
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

        /// <summary>The marker icon's translated name, or null when it has none: an empty or missing locaKey translates
        /// to the bare suffix (" Name") or the key itself.</summary>
        private static string UnitName(ScreenMarkerIcon icon)
        {
            if (icon == null || string.IsNullOrWhiteSpace(icon.locaKey)) return null;
            string name = SafeTranslate(() => icon.UnitName)?.Trim();
            string key = icon.locaKey.Trim();
            return string.IsNullOrEmpty(name) || name == key || name == key + " Name" || name == "Name" ? null : name;
        }

        /// <summary>A prefab name as a fallback display name: "P Melee" becomes "Melee".</summary>
        internal static string Humanize(string prefabName) =>
            prefabName.StartsWith("P ") || prefabName.StartsWith("E ") ? prefabName.Substring(2).Trim() : prefabName;

        private static IEnumerable<string> Words(string id) => Humanize(id).Split([' '], StringSplitOptions.RemoveEmptyEntries);

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
                // The behaviour engine's vocabulary (events, actions, functions...) and built-in presets, for agents.
                ["logic"] = PalaceMod.Builder.Model.Logic.LogicSchema.DumpWithPresets(),
                ["units"] = new JArray(Units.Select(u => new JObject
                {
                    ["id"] = u.Id, ["ref"] = ContentRef.Vanilla(u.Id), ["side"] = u.Side, ["name"] = u.DisplayName, ["description"] = u.Description,
                    ["maxHp"] = R(u.MaxHp), ["moveSpeed"] = R(u.MoveSpeed), ["attackCooldown"] = R(u.AttackCooldown), ["attackRange"] = R(u.AttackRange),
                    ["keepDistance"] = R(u.KeepDistance), ["aggroTime"] = R(u.AggroTime), ["weapon"] = u.Weapon,
                    ["flying"] = u.Flying, ["large"] = u.Large, ["difficulty"] = R(u.Difficulty), ["tags"] = new JArray(u.Tags),
                    ["buildings"] = new JArray(u.Buildings),
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
                ["effects"] = new JArray(GameFx.Effects.Values.OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase).Select(f => new JObject { ["id"] = f.Id, ["from"] = f.Source })),
                ["sounds"] = new JArray(GameFx.Sounds.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)),
            };
            return root.ToString(Formatting.Indented);
        }
    }

    /// <summary>
    /// The game's visual effects and sounds, by name, for the behaviour engine's `effect` and `sound` actions
    /// (docs/builder-engine.md, "Show"). Effects are the prefabs the game itself spawns as pure visuals (death and
    /// revive puffs, impact and muzzle effects, coin sparkles), found through the fields that reference them; prefabs
    /// that carry gameplay (damage, hit points, tags, spawns) are left out. Sounds are the clips of the game's AudioSet.
    /// Ids are the prefab names (effects) and the AudioSet property names in camelCase (sounds).
    /// </summary>
    public static class GameFx
    {
        public sealed class Fx
        {
            public string Id;
            public GameObject Prefab;
            public string Source;            // the field it was found through ("Hp.fxToSpawnOnDeath")
        }

        private static Dictionary<string, Fx> effects;
        private static Dictionary<string, Func<AudioSet, object>> sounds;
        private static int builtFrame = -1;

        public static IReadOnlyDictionary<string, Fx> Effects { get { BuildEffects(); return effects; } }

        /// <summary>Sound id -> clip getter (an AudioClip or AudioSet.ClipArray).</summary>
        public static IReadOnlyDictionary<string, Func<AudioSet, object>> Sounds { get { BuildSounds(); return sounds; } }

        /// <summary>An effect by id (exact, then case-insensitive). Rescans once per frame on a miss: levels load more prefabs.</summary>
        public static Fx FindEffect(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int pass = 0; pass < 2; pass++)
            {
                BuildEffects();
                if (effects.TryGetValue(id, out var fx) && fx.Prefab != null) return fx;
                var ci = effects.Values.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase) && f.Prefab != null);
                if (ci != null) return ci;
                if (pass == 1 || builtFrame == Time.frameCount) break;
                effects = null;
            }
            return null;
        }

        /// <summary>A sound's clip (a random one for clip arrays), or null.</summary>
        public static AudioClip FindSound(string id)
        {
            var set = ThronefallAudioManager.Instance != null ? ThronefallAudioManager.Instance.audioContent : null;
            if (set == null || string.IsNullOrEmpty(id)) return null;
            BuildSounds();
            if (!sounds.TryGetValue(id, out var get))
                get = sounds.FirstOrDefault(s => string.Equals(s.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
            return get?.Invoke(set) switch
            {
                AudioClip clip => clip,
                AudioSet.ClipArray arr when arr.clips is { Length: > 0 } => arr.GetRandomClip(),
                _ => null,
            };
        }

        private static void BuildSounds()
        {
            if (sounds != null) return;
            sounds = new Dictionary<string, Func<AudioSet, object>>(StringComparer.Ordinal);
            foreach (var prop in typeof(AudioSet).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(AudioClip) && prop.PropertyType != typeof(AudioSet.ClipArray)) continue;
                string id = char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
                var p = prop;
                sounds[id] = set => p.GetValue(set);
            }
        }

        // Component types that make a prefab gameplay, not a visual.
        private static readonly Type[] Gameplay = [typeof(Hp), typeof(AutoAttack), typeof(TaggedObject), typeof(AimbotProjectile), typeof(Coin),
            typeof(PathfindMovement), typeof(BuildSlot), typeof(PlayerInteraction)];

        internal static bool IsVisualOnly(GameObject go, out string reason)
        {
            reason = null;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var t = c.GetType();
                if (Gameplay.Any(g => g.IsAssignableFrom(t))) { reason = t.Name; return false; }
                for (var b = t; b != null && b.Assembly.GetName().Name == "Assembly-CSharp"; b = b.BaseType)
                {
                    string n = b.Name;
                    if (n.Contains("Attack") || n.Contains("Damage") || n.Contains("Spawn") || n.Contains("Weapon") || n.Contains("Projectile") || n.Contains("Knockback"))
                    { reason = t.Name; return false; }
                }
            }
            return true;
        }

        // Other fields the game spawns visuals from.
        private static readonly (Type, string)[] MoreSources =
        [
            (typeof(OnDamageFeedbackHandler), "onDmgByPlayerFX"), (typeof(BlockProjectiles), "fxBlockProjectile"), (typeof(BoarChargeFX), "endFX"),
            (typeof(CurseBlastMA), "curseBlastFX"), (typeof(CurseBlastMA), "activatePlayerFX"), (typeof(KnockbackBehavior), "staggerVFX"),
            (typeof(Mill_ImprovementWindSpirits), "fxBlockArrow"), (typeof(ProtectionDomeVisualControl), "domeDestroyedVFX"),
        ];

        private static void BuildEffects()
        {
            if (effects != null) return;
            builtFrame = Time.frameCount;
            var found = new Dictionary<GameObject, string>();
            void Add(GameObject go, string source)
            {
                if (go == null || go.scene.IsValid() || go.transform.parent != null || found.ContainsKey(go)) return;
                if (!IsVisualOnly(go, out _)) return;
                found[go] = source;
            }
            foreach (var hp in Resources.FindObjectsOfTypeAll<Hp>())
            {
                Add(hp.fxToSpawnOnDeath, "Hp.fxToSpawnOnDeath");
                Add(hp.fxToSpawnOnRevive, "Hp.fxToSpawnOnRevive");
            }
            foreach (var w in Resources.FindObjectsOfTypeAll<Weapon>())
            {
                Add(w.projectileImpactVisuals, "Weapon.projectileImpactVisuals");
                Add(w.fxSpawnOnAttacker, "Weapon.fxSpawnOnAttacker");
                Add(w.fxSpawnOnVictim, "Weapon.fxSpawnOnVictim");
                Add(w.spawnOnGroundWhenTargetingGround, "Weapon.spawnOnGroundWhenTargetingGround");
                Add(w.spawnInAirWhenTargetingAir, "Weapon.spawnInAirWhenTargetingAir");
            }
            foreach (var p in Resources.FindObjectsOfTypeAll<AimbotProjectile>()) Add(p.spawnOnHit, "AimbotProjectile.spawnOnHit");
            foreach (var c in Resources.FindObjectsOfTypeAll<Coin>()) Add(c.onPickUpParticles, "Coin.onPickUpParticles");
            foreach (var p in Resources.FindObjectsOfTypeAll<PlayerInteraction>()) Add(p.coinSpendFX, "PlayerInteraction.coinSpendFX");
            foreach (var (type, field) in MoreSources)
            {
                var f = HarmonyLib.AccessTools.Field(type, field);
                if (f == null || f.FieldType != typeof(GameObject)) continue;
                foreach (var o in Resources.FindObjectsOfTypeAll(type)) Add(f.GetValue(o) as GameObject, $"{type.Name}.{field}");
            }
            effects = new Dictionary<string, Fx>(StringComparer.Ordinal);
            foreach (var pair in found.OrderBy(f => f.Key.name, StringComparer.Ordinal))
            {
                string id = pair.Key.name;
                for (int n = 2; effects.ContainsKey(id); n++) id = $"{pair.Key.name} #{n}";
                effects[id] = new Fx { Id = id, Prefab = pair.Key, Source = pair.Value };
            }
        }

        /// <summary>Dev: every effect with the game scripts it carries.</summary>
        internal static string Scan()
        {
            effects = null;
            BuildEffects();
            return string.Join("\n", effects.Values.Select(f => $"{f.Id} <- {f.Source}: " + string.Join(",",
                f.Prefab.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Assembly.GetName().Name == "Assembly-CSharp").Select(c => c.GetType().Name).Distinct())));
        }
    }
}
