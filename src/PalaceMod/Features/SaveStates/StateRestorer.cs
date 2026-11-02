using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Loads a savestate:
    1. Hands the game its own save data for the captured moment, in memory only (the run's MatchSave is swapped, no
       file is written), and restarts the level the way the game's "retry day" does. The game's loader rebuilds
       buildings, upgrades, gold, wave, score, research and shrine progress from it.
    2. Once the level is up, puts back what that format misses: the night (started again, spawn timers and
       progress set to the captured values), enemies, units, king, coins on the ground, per-day stats and RNG.
    3. Points the run's in-memory MatchSave at the captured run's last dawn, so the game's own "retry day" keeps
       meaning what it meant at capture time.
*/
namespace PalaceMod
{
    internal static class StateRestorer
    {
        private static readonly TagManager.ETag[] GroupTags = [TagManager.ETag.Group1, TagManager.ETag.Group2, TagManager.ETag.Group3];

        public static bool Busy { get; private set; }

        public static bool CanRestore(SaveStateInfo state, out string reason)
        {
            reason = null;
            if (state == null) reason = "No savestate";
            else if (Busy) reason = "A savestate is already loading";
            else if (DayNightCycle.Instance == null || EnemySpawner.instance == null || LocalGamestate.Instance == null || SceneTransitionManager.instance == null)
                reason = "Not in a level";
            else if (state.Level != SceneManager.GetActiveScene().name) reason = $"That state is from {state.Level}";
            else if (state.Level == StateUtil.TutorialScene) reason = "Savestates don't work in the tutorial";
            else if (LocalGamestate.SelectedGameMode != LocalGamestate.GameMode.Classic) reason = "Savestates only work in normal levels";
            else if (SceneTransitionManager.instance.SceneTransitionIsRunning || LocalMatchSaveLoad.CurrentlyLoadingMatch) reason = "The level is still loading";
            else if (LocalGamestate.Instance.CurrentState != LocalGamestate.State.InMatch) reason = "The match is over";
            else if (state.WaveCount != EnemySpawner.instance.WaveCount) reason = "That state doesn't match this level's waves";
            return reason == null;
        }

        /// <param name="backup">Capture an undo state of the current moment first.</param>
        public static void Begin(SaveStateInfo state, bool backup = true)
        {
            if (backup && state.Kind != SaveStateKind.BeforeLoad && StateCapture.CanCapture(out _))
            {
                try { SaveStates.Capture(null, SaveStateKind.BeforeLoad); }
                catch (Exception e) { Plugin.Logger.LogWarning($"Savestates: undo state failed: {e.Message}"); }
            }
            Busy = true;
            Plugin.Instance.StartCoroutine(Run(state));
        }

        private static IEnumerator Run(SaveStateInfo state)
        {
            var data = state.Data;
            Plugin.Logger.LogInfo($"Savestates: loading {state}");
            SaveStates.Last = state;
            GameplayImpact.Mark("Savestate loaded");
            Feature<SaveStatesManager>.Instance?.MarkPracticeRun();
            SaveStates.RaiseRestoring(state);

            var transitions = SceneTransitionManager.instance;
            MatchSave injected;
            try
            {
                int? wave = data.night ? data.wave - 1 : null;
                injected = BuildMatchSave(data.native, wave);
                Priv.SetStatic(typeof(MatchSaveLoadHandler), "currentSave", injected);
                MatchSaveLoadHandler.OverwriteCurrentSave = false;
                injected.ApplyLoadout();
                UIFrameManager.instance?.CloseAllFrames();
                if (Feature<SaveStatesManager>.Instance?.FastLoad ?? false) Plugin.Instance.StartCoroutine(FastTransition(transitions));
                transitions.RestartCurrentLevel();
            }
            catch (Exception e)
            {
                Fail(state, "couldn't start loading", e);
                yield break;
            }

            // Wait for the new level, then for the game's loader and the buildings' delayed upgrade pass.
            var oldGamestate = LocalGamestate.Instance;
            float deadline = Time.realtimeSinceStartup + 60f;
            while (LocalGamestate.Instance == null || ReferenceEquals(LocalGamestate.Instance, oldGamestate) ||
                   EnemySpawner.instance == null || DayNightCycle.Instance == null || PlayerMovement.instance == null ||
                   PlayerInteraction.instance == null || TagManager.instance == null ||
                   LocalMatchSaveLoad.CurrentlyLoadingMatch || LocalGamestate.Instance.CurrentState != LocalGamestate.State.InMatch)
            {
                if (Time.realtimeSinceStartup > deadline || SceneManager.GetActiveScene().name != data.level && !transitions.SceneTransitionIsRunning)
                {
                    Fail(state, "the level didn't come back", null);
                    yield break;
                }
                yield return null;
            }
            yield return null;
            yield return null;

            List<Action> deferred;
            try
            {
                int expected = data.night ? data.wave - 1 : data.wave;
                if (EnemySpawner.instance.Wavenumber != expected)
                    Plugin.Logger.LogWarning($"Savestates: the level loaded wave {EnemySpawner.instance.Wavenumber}, expected {expected}; restoring the rest anyway");
                deferred = Apply(data, injected);
            }
            catch (Exception e)
            {
                Fail(state, "restoring failed partway", e);
                yield break;
            }

            // Hp.Start (one frame after an enemy is created) fills its health; set the captured values after it.
            yield return null;
            try
            {
                foreach (var action in deferred) action();
                RestoreRandom(data);
            }
            catch (Exception e)
            {
                Fail(state, "restoring failed partway", e);
                yield break;
            }
            Busy = false;
            Plugin.Logger.LogInfo($"Savestates: loaded {state}; {Summary()}");
            Notify.Show($"Loaded \"{state.Name}\"", NotifyKind.Success, key: "savestates");
            SaveStates.RaiseRestored(state);
        }

        private static void Fail(SaveStateInfo state, string what, Exception e)
        {
            Busy = false;
            Plugin.Logger.LogError($"Savestates: loading {state} failed: {what}{(e != null ? $": {e}" : "")}");
            Notify.Show($"Couldn't load \"{state.Name}\": {what}", NotifyKind.Error, 5f, "savestates");
        }

        // Shortens the cloud transition while a state loads, then puts the game's timings back.
        private static IEnumerator FastTransition(SceneTransitionManager transitions)
        {
            float fadeIn = transitions.cloudFadeInTime, fadeOut = transitions.cloudFadeOutTime;
            transitions.cloudFadeInTime = Mathf.Min(fadeIn, 0.2f);
            transitions.cloudFadeOutTime = Mathf.Min(fadeOut, 0.6f);
            yield return null;
            while (transitions != null && transitions.SceneTransitionIsRunning) yield return null;
            if (transitions == null) yield break;
            transitions.cloudFadeInTime = fadeIn;
            transitions.cloudFadeOutTime = fadeOut;
        }

        // ---- Native save ----------------------------------------------------------------------------------------

        private static MatchSave BuildMatchSave(NativeSave native, int? wavenumber)
        {
            var save = new MatchSave
            {
                hadRestarts = native.hadRestarts,
                etSeed = native.etSeed,
                highestScoreThisRun = native.highestScoreThisRun,
                runComplete = native.runComplete,
            };
            foreach (var e in native.ints) save.AddValue(e.key, e.value);
            foreach (var e in native.floats) save.AddValue(e.key, e.value);
            foreach (var e in native.bools) save.AddValue(e.key, e.value);
            foreach (var e in native.strings) save.AddValue(e.key, e.value);
            foreach (var e in native.intArrays) save.AddValue(e.key, (int[])e.value.Clone());
            foreach (var e in native.tagArrays) save.AddValue(e.key, e.value.Select(v => (TagManager.ETag)v).ToArray());
            if (wavenumber != null)
            {
                var entity = EnemySpawner.instance.GetComponent<SaveLoadEntity>();
                if (entity != null) save.AddValue(MatchSaveLoadHandler.GetPathFromIdentifierAndGUID(entity.GUID, "wavenumber"), wavenumber.Value);
            }
            var all = PerkManager.instance.allEquippables;
            save.currentLoadout = native.loadout
                .Select(name => all.FirstOrDefault(e => e != null && e.displayName == name))
                .Where(e => e != null).ToList();
            if (save.currentLoadout.Count != native.loadout.Count)
                Plugin.Logger.LogWarning($"Savestates: {native.loadout.Count - save.currentLoadout.Count} perk(s) in the state no longer exist");
            return save;
        }

        // ---- Live objects ---------------------------------------------------------------------------------------

        private static List<Action> Apply(SaveStateData data, MatchSave injected)
        {
            var deferred = new List<Action>();
            var paths = new ScenePaths();

            // The run's in-memory save goes back to the captured run's last dawn (what the game's retry-day uses).
            if (data.hasDawn)
            {
                var dawn = BuildMatchSave(data.dawn, null);
                dawn.hadRestarts |= injected.hadRestarts;
                Priv.SetStatic(typeof(MatchSaveLoadHandler), "currentSave", dawn);
            }
            else
            {
                // Captured before the run's first dawn: retrying from here is a fresh start, as it was then.
                MatchSaveLoadHandler.OverwriteCurrentSave = true;
            }

            var levelData = LevelProgressManager.instance?.GetLevelDataForActiveScene();
            if (data.stats.valid && levelData != null)
            {
                levelData.highscore = data.stats.highscore;
                levelData.dayToDayScore = [.. data.stats.dayToDayScore];
                levelData.dayToDayNetworth = [.. data.stats.dayToDayNetworth];
            }

            var cycle = DayNightCycle.Instance;
            var spawner = EnemySpawner.instance;
            if (data.night)
            {
                cycle.SwitchToNight();
                if (data.hasSpawner) RestoreSpawner(data.spawner, spawner);
            }
            Priv.Set(cycle, "currentNightLength", data.time.currentNightLength);
            if (cycle.AutomatedDaytime) Priv.Set(cycle, "remainingAutoDayTime", data.time.remainingAutoDayTime);
            if (cycle.AutomatedNighttime) Priv.Set(cycle, "remainingAutoNightTime", data.time.remainingAutoNightTime);

            RestoreBuildings(data, paths);
            RestoreUnits(data, paths);
            RestoreEnemies(data, paths, deferred);
            RestoreCoins(data);
            RestoreKing(data);

            if (data.night && data.hasSpawner)
            {
                // Without this, the night-over check could see "not spawning, no enemies" before the recount.
                Priv.Set(spawner, "numberOfEnemiesOnTheMap", data.enemies.Count);
                if (!data.spawner.spawning) deferred.Add(() => Priv.Set(spawner, "spawningInProgress", false));
            }
            return deferred;
        }

        private static void RestoreSpawner(SpawnerData saved, EnemySpawner spawner)
        {
            if (spawner.Wavenumber != saved.wavenumber)
                Plugin.Logger.LogWarning($"Savestates: night started as wave {spawner.Wavenumber}, state has {saved.wavenumber}");
            if (spawner.Wavenumber < 0 || spawner.Wavenumber >= spawner.waves.Count) return;
            var spawns = spawner.waves[spawner.Wavenumber].spawns;
            for (int i = 0; i < Math.Min(spawns.Count, saved.spawns.Count); i++)
            {
                var spawn = spawns[i];
                var s = saved.spawns[i];
                spawn.waitBeforeNextSpawn = s.wait;
                Priv.Set(spawn, "spawnedUnits", s.spawned);
                Priv.Set(spawn, "finished", s.finished);
                Priv.Set(spawn, "goldCoinsPerEnemy", (int[])s.coins.Clone());
            }
            Priv.Set(spawner, "lastSpawnPeriodDuration", saved.lastSpawnPeriodDuration);
            Priv.Set(spawner, "lastSpawnPeriodClock", saved.lastSpawnPeriodClock);
            Priv.Set(spawner, "timeSinceSpaningEnded", saved.timeSinceSpawningEnded);
            EnemySpawner.totalEnemiesSpawnedThisWave = saved.totalEnemiesSpawnedThisWave;
        }

        private static void Knockout(Hp hp)
        {
            if (hp.HpValue <= 0f) return;
            bool invulnerable = hp.invulnerable;
            hp.invulnerable = false;
            hp.TakeDamage(hp.HpValue + 1f, null, causedByPlayer: false, invokeFeedbackEvents: false);
            hp.invulnerable = invulnerable;
        }

        private static void SetHp(Hp hp, float value, float max)
        {
            if (value <= 0f)
            {
                Knockout(hp);
                return;
            }
            if (hp.HpValue <= 0f) hp.Revive(callReviveEvent: false);
            if (max > 0f) hp.maxHp = max;
            hp.SetHpTo(Mathf.Min(value, hp.maxHp));
        }

        private static void RestoreBuildings(SaveStateData data, ScenePaths paths)
        {
            var hps = paths.Index(Object.FindObjectsOfType<Hp>(true));
            int missing = 0;
            foreach (var saved in data.playerHp)
            {
                if (!hps.TryGetValue(saved.path, out var hp) || !hp.gameObject.activeInHierarchy) { missing++; continue; }
                if (Mathf.Approximately(hp.HpValue, saved.hp) && Mathf.Approximately(hp.maxHp, saved.maxHp)) continue;
                SetHp(hp, saved.hp, saved.maxHp);
            }
            var interactors = paths.Index(Object.FindObjectsOfType<BuildingInteractor>(true));
            foreach (var flag in data.knockedOutTonight)
            {
                if (!interactors.TryGetValue(flag.path, out var interactor)) continue;
                if (interactor.KnockedOutTonight == flag.value) continue;
                Priv.Set(interactor, "knockedOutTonight", flag.value);
                if (interactor.isActiveAndEnabled) interactor.UpdateInteractionState();
            }
            if (missing > 0) Plugin.Logger.LogInfo($"Savestates: {missing} building/object HP value(s) had no match in the reloaded level");
        }

        private static void RestoreUnits(SaveStateData data, ScenePaths paths)
        {
            var units = paths.Index(Object.FindObjectsOfType<PathfindMovementPlayerunit>(true));
            int missing = 0;
            foreach (var saved in data.units)
            {
                if (!units.TryGetValue(saved.path, out var unit) || !unit.gameObject.activeInHierarchy) { missing++; continue; }
                var hp = unit.GetComponent<Hp>();
                unit.HomePosition = saved.home;
                unit.HoldPosition = saved.hold;
                unit.transform.SetPositionAndRotation(saved.position, saved.rotation);
                unit.ClearCurrentPath();
                if (hp != null) SetHp(hp, saved.hp, saved.maxHp);
                var tagged = unit.GetComponent<TaggedObject>();
                if (tagged != null)
                {
                    foreach (var tag in GroupTags)
                    {
                        if (saved.groups.Contains((int)tag)) tagged.AddTag(tag);
                        else tagged.RemoveTag(tag);
                    }
                }
            }
            if (missing > 0) Plugin.Logger.LogInfo($"Savestates: {missing} unit(s) had no match in the reloaded level");
        }

        private static void RestoreEnemies(SaveStateData data, ScenePaths paths, List<Action> deferred)
        {
            // Enemies the reloaded level starts with: scene-placed ones the state doesn't have were dead or not yet
            // active at capture time.
            var wanted = new HashSet<string>(data.enemies.Where(e => !string.IsNullOrEmpty(e.scenePath)).Select(e => e.scenePath));
            var sceneEnemies = new Dictionary<string, GameObject>();
            foreach (var tagged in TagManager.instance.FindAllTaggedObjectsWithTagDirect_UseWithCare(TagManager.ETag.EnemyOwned).ToList())
            {
                if (tagged == null || !tagged.gameObject.activeInHierarchy) continue;
                var root = StateUtil.EnemyRoot(tagged);
                if (root.GetComponentInChildren<Hp>(true) == null) continue;
                if (StateUtil.IsClone(root)) { Object.Destroy(root); continue; }
                string path = paths.Of(root.transform);
                sceneEnemies[path] = root;
                if (!wanted.Contains(path)) root.SetActive(false);
            }
            // Scene-placed enemies a wave activates (not active yet in the reloaded level).
            foreach (var wave in EnemySpawner.instance.waves)
            {
                foreach (var spawn in wave.spawns)
                {
                    var tagged = spawn.enemyPrefab != null && spawn.enemyPrefab.scene.isLoaded ? spawn.enemyPrefab.GetComponentInChildren<TaggedObject>(true) : null;
                    if (tagged != null)
                    {
                        string path = paths.Of(tagged.transform);
                        if (!sceneEnemies.ContainsKey(path)) sceneEnemies[path] = tagged.gameObject;
                    }
                }
            }

            var prefabs = new EnemyPrefabs();
            var afterlife = PerkManager.instance.godOfAfterlifeEnemy;
            int restored = 0, skipped = 0;
            foreach (var saved in data.enemies)
            {
                GameObject go;
                if (!string.IsNullOrEmpty(saved.scenePath))
                {
                    if (!sceneEnemies.TryGetValue(saved.scenePath, out go)) { skipped++; continue; }
                    go.transform.SetPositionAndRotation(saved.position, saved.rotation);
                    for (var t = go.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                }
                else
                {
                    var prefab = prefabs.Find(saved.prefab);
                    if (prefab == null) { skipped++; Plugin.Logger.LogWarning($"Savestates: no prefab for enemy \"{saved.prefab}\""); continue; }
                    go = Object.Instantiate(prefab, saved.position, saved.rotation);
                    Spawn.AdjustEnemyParametersAfterSpawn(go, saved.hps.Any(h => h.elite));
                }
                restored++;

                var attacks = go.GetComponentsInChildren<AutoAttack>(true);
                for (int i = 0; i < attacks.Length; i++)
                {
                    if (i < saved.damage.Length) attacks[i].DamageMultiplyer = saved.damage[i];
                    if (i < saved.cooldowns.Length) attacks[i].cooldownDuration = saved.cooldowns[i];
                }
                var movers = go.GetComponentsInChildren<PathfindMovementEnemy>(true);
                for (int i = 0; i < movers.Length && i < saved.speeds.Length; i++) movers[i].movementSpeed = saved.speeds[i];

                var hps = go.GetComponentsInChildren<Hp>(true);
                for (int i = 0; i < hps.Length && i < saved.hps.Count; i++)
                {
                    var hp = hps[i];
                    var s = saved.hps[i];
                    hp.coinCount = s.coins;
                    hp.Elite = s.elite;
                    if (s.hpOriginal > 0f) hp.HpOriginal = s.hpOriginal;
                    if (s.afterlife && afterlife != null) hp.enemyToSpawnOnDeath = afterlife;
                    hp.maxHp = s.maxHp;
                    deferred.Add(() =>
                    {
                        if (hp == null) return;
                        hp.maxHp = s.maxHp;
                        if (s.hp <= 0f) Knockout(hp);
                        else hp.SetHpTo(Mathf.Min(s.hp, s.maxHp));
                    });
                }
            }
            if (skipped > 0) Plugin.Logger.LogWarning($"Savestates: {skipped} enemy(ies) couldn't be restored");
            if (restored > 0) Plugin.Logger.LogInfo($"Savestates: restored {restored} enemies");
        }

        private static void RestoreCoins(SaveStateData data)
        {
            if (data.freeCoins.Count == 0) return;
            var prefab = EnemyPrefabs.CoinPrefab();
            if (prefab == null)
            {
                // Can't put them on the ground; bank them instead so no gold is lost.
                PlayerInteraction.instance.AddCoin(data.freeCoins.Count);
                Plugin.Logger.LogWarning($"Savestates: no coin prefab; banked {data.freeCoins.Count} ground coins");
                return;
            }
            foreach (var position in data.freeCoins) Object.Instantiate(prefab, position, Quaternion.identity);
        }

        private static void RestoreKing(SaveStateData data)
        {
            var saved = data.king;
            if (!saved.valid) return;
            var king = PlayerMovement.instance;
            king.TeleportTo(saved.position);
            if (king.meshParent != null) king.meshParent.rotation = saved.meshRotation;
            Priv.Set(king, "desiredMeshRotation", saved.meshRotation);
            var hp = king.GetComponent<Hp>();
            if (hp != null) SetHp(hp, saved.hp, saved.maxHp);
            var revive = king.GetComponent<AutoRevive>();
            if (revive != null)
            {
                Priv.Set(revive, "hasBeenKnockedOutFor", saved.hp <= 0f ? saved.knockedOutFor : 0f);
                Priv.Set(revive, "quickReviveAvailable", saved.quickReviveAvailable);
            }
            var interaction = PlayerInteraction.instance;
            if (interaction.ManualAttack != null) interaction.ManualAttack.Cooldown = saved.manualCooldown;
            if (interaction.AutoAttack != null) interaction.AutoAttack.Cooldown = saved.autoCooldown;
        }

        private static void RestoreRandom(SaveStateData data)
        {
            if (string.IsNullOrEmpty(data.randomState)) return;
            try { UnityEngine.Random.state = JsonUtility.FromJson<UnityEngine.Random.State>(data.randomState); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Savestates: RNG state not restored: {e.Message}"); }
        }

        /// <summary>One line describing the live game, for logs and the dev "savestate dump" command.</summary>
        internal static string Summary()
        {
            if (EnemySpawner.instance == null || DayNightCycle.Instance == null || PlayerInteraction.instance == null) return "not in a level";
            var king = PlayerMovement.instance;
            var kingHp = king != null ? king.GetComponent<Hp>() : null;
            int units = 0, unitsAlive = 0;
            float unitHp = 0f;
            foreach (var u in Object.FindObjectsOfType<PathfindMovementPlayerunit>())
            {
                var h = u.GetComponent<Hp>();
                if (h == null) continue;
                units++;
                if (h.HpValue > 0f) { unitsAlive++; unitHp += h.HpValue; }
            }
            int enemies = 0;
            float enemyHp = 0f;
            var seen = new HashSet<GameObject>();
            foreach (var t in TagManager.instance.FindAllTaggedObjectsWithTagDirect_UseWithCare(TagManager.ETag.EnemyOwned))
            {
                if (t == null || !t.gameObject.activeInHierarchy || !seen.Add(StateUtil.EnemyRoot(t))) continue;
                var hs = StateUtil.EnemyRoot(t).GetComponentsInChildren<Hp>();
                if (hs.Length == 0 || !hs.Any(h => h.HpValue > 0f)) continue;
                enemies++;
                enemyHp += hs.Sum(h => Mathf.Max(0f, h.HpValue));
            }
            var cycle = DayNightCycle.Instance;
            var spawner = EnemySpawner.instance;
            int spawned = 0;
            if (cycle.CurrentTimestate == DayNightCycle.Timestate.Night && spawner.Wavenumber >= 0 && spawner.Wavenumber < spawner.waves.Count)
                spawned = spawner.waves[spawner.Wavenumber].spawns.Sum(s => s.SpawnedUnits);
            return $"wave={spawner.Wavenumber} {cycle.CurrentTimestate} nightTime={cycle.CurrentNightLength:0.0}s spawning={spawner.SpawningInProgress} spawned={spawned} " +
                $"gold={PlayerInteraction.instance.TrueBalance} groundCoins={TagManager.instance.coins.Count(c => c != null && c.IsFree)} score={ScoreManager.Instance?.CurrentScore} " +
                $"king=({(king != null ? king.transform.position.ToString("F1") : "-")}) kingHp={(kingHp != null ? kingHp.HpValue : 0f):0.#} " +
                $"units={unitsAlive}/{units} unitHp={unitHp:0} enemies={enemies} enemyHp={enemyHp:0} " +
                $"buildings={TagManager.instance.playerBuildingInteractors.Count} impact=[{string.Join(", ", GameplayImpact.Reasons)}]";
        }
    }

    /// <summary>Finds enemy and coin prefabs by name: the level's waves first, then any loaded prefab.</summary>
    internal sealed class EnemyPrefabs
    {
        private Dictionary<string, GameObject> byName;

        public GameObject Find(string name)
        {
            if (byName == null) Build();
            if (byName.TryGetValue(name, out var prefab)) return prefab;
            foreach (var hp in Resources.FindObjectsOfTypeAll<Hp>())
            {
                if (hp == null || hp.gameObject.scene.IsValid()) continue;
                var root = hp.transform.root.gameObject;
                if (root.name == name) return byName[name] = root;
            }
            return null;
        }

        private void Build()
        {
            byName = [];
            void Add(GameObject go)
            {
                if (go == null || go.scene.IsValid() || byName.ContainsKey(go.name)) return;
                byName[go.name] = go;
                foreach (var hp in go.GetComponentsInChildren<Hp>(true)) Add(hp.enemyToSpawnOnDeath);
            }
            foreach (var wave in EnemySpawner.instance.waves)
                foreach (var spawn in wave.spawns) Add(spawn.enemyPrefab);
            if (PerkManager.instance != null) Add(PerkManager.instance.godOfAfterlifeEnemy);
        }

        public static GameObject CoinPrefab()
        {
            foreach (var wave in EnemySpawner.instance.waves)
            {
                foreach (var spawn in wave.spawns)
                {
                    var hp = spawn.enemyPrefab != null ? spawn.enemyPrefab.GetComponentInChildren<Hp>(true) : null;
                    if (hp != null && hp.coin != null) return hp.coin;
                }
            }
            return Resources.FindObjectsOfTypeAll<Coin>().Where(c => c != null && !c.gameObject.scene.IsValid() && c.registerInTagManager)
                .Select(c => c.gameObject).FirstOrDefault();
        }
    }
}
