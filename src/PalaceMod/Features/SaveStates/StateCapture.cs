using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Reads the live game into a SaveStateData. The game's own save data is produced by the game itself: every
    SaveLoadEntity's OnSave runs into a scratch MatchSave swapped in for the run's real one, which is put back
    untouched, so capturing never changes what the game will save or load.
*/
namespace PalaceMod
{
    internal static class StateCapture
    {
        private static readonly TagManager.ETag[] GroupTags = [TagManager.ETag.Group1, TagManager.ETag.Group2, TagManager.ETag.Group3];

        public static bool CanCapture(out string reason)
        {
            reason = null;
            if (StateRestorer.Busy) reason = "A savestate is loading";
            else if (DayNightCycle.Instance == null || EnemySpawner.instance == null || LocalGamestate.Instance == null ||
                     PlayerMovement.instance == null || PlayerInteraction.instance == null || TagManager.instance == null || ScoreManager.Instance == null)
                reason = "Not in a level";
            else if (SceneManager.GetActiveScene().name == StateUtil.TutorialScene) reason = "Savestates don't work in the tutorial";
            else if (LocalGamestate.SelectedGameMode != LocalGamestate.GameMode.Classic) reason = "Savestates only work in normal levels";
            else if (StateUtil.StudioEditing) reason = "Savestates work in test play, not while editing";
            else if (SceneTransitionManager.instance != null && SceneTransitionManager.instance.SceneTransitionIsRunning) reason = "The level is still loading";
            else if (LocalMatchSaveLoad.CurrentlyLoadingMatch) reason = "The level is still loading";
            else if (LocalGamestate.Instance.CurrentState != LocalGamestate.State.InMatch) reason = "The match is over";
            else if (DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day &&
                     (!DayNightCycle.Instance.AfterSunrise || Priv.Get<bool>(DayNightCycle.Instance, "currentlySwitchingToDay")))
                reason = "Wait for the sunrise to finish";
            else if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineWaiting) reason = "Finish the current choice first";
            else if (PayingForSomething()) reason = "Finish paying first";
            return reason == null;
        }

        // Coins put into a building's cost display are taken from the balance before the purchase completes.
        private static bool PayingForSomething()
        {
            foreach (var display in Object.FindObjectsOfType<CostDisplay>())
            {
                if (Priv.Get<int>(display, "currentlyFilledCoins") > 0) return true;
            }
            return false;
        }

        public static SaveStateData Capture()
        {
            var spawner = EnemySpawner.instance;
            var cycle = DayNightCycle.Instance;
            var player = PlayerInteraction.instance;
            var paths = new ScenePaths();
            var data = new SaveStateData
            {
                modVersion = MyPluginInfo.PLUGIN_VERSION,
                gameVersion = Application.version,
                level = StateUtil.LevelKey(SceneManager.GetActiveScene().name),
                scene = SceneManager.GetActiveScene().name,
                builder = StateUtil.BuilderPin,
                createdUtc = DateTime.UtcNow.ToString("o"),
                wave = spawner.Wavenumber,
                waveCount = spawner.WaveCount,
                night = cycle.CurrentTimestate == DayNightCycle.Timestate.Night,
                gold = player.TrueBalance,
                score = ScoreManager.Instance.CurrentScore,
                randomState = JsonUtility.ToJson(UnityEngine.Random.state),
            };

            // Coins: those flying to the king (and coins still to pop out of buildings) are as good as banked; the
            // ones lying on the ground are put back where they lie.
            int banked = player.Balance + CoinSpawner.AllCoinsLeftToBeSpawned;
            foreach (var coin in TagManager.instance.coins)
            {
                if (coin == null) continue;
                if (coin.IsFree) data.freeCoins.Add(coin.transform.position);
                else banked++;
            }

            data.native = CaptureNative(out var playerEntity);
            if (playerEntity != null) SetInt(data.native, playerEntity.GUID + "_balance", banked);
            var real = MatchSaveLoadHandler.CurrentSave;
            if (real != null && !MatchSaveLoadHandler.OverwriteCurrentSave)
            {
                data.hasDawn = true;
                data.dawn = FromMatchSave(real);
            }

            var levelData = LevelProgressManager.instance?.GetLevelDataForActiveScene();
            if (levelData != null)
            {
                data.stats = new LevelStats
                {
                    valid = true,
                    highscore = levelData.highscore,
                    dayToDayScore = [.. levelData.dayToDayScore],
                    dayToDayNetworth = [.. levelData.dayToDayNetworth],
                    networth = PlayerInteraction.instance.Networth,
                };
            }

            data.time = new TimeData
            {
                currentNightLength = cycle.CurrentNightLength,
                remainingAutoDayTime = cycle.RemainingAutoDayTime,
                remainingAutoNightTime = cycle.RemainingAutoNightTime,
            };

            CaptureKing(data);
            CapturePlayerObjects(data, paths);
            CaptureEnemies(data, paths);
            if (data.night) CaptureSpawner(data, spawner);
            SaveStates.CaptureMods(data);
            return data;
        }

        // ---- Native save ----------------------------------------------------------------------------------------

        private static NativeSave CaptureNative(out SaveLoadEntity playerEntity)
        {
            playerEntity = PlayerInteraction.instance.GetComponentInParent<SaveLoadEntity>();
            var entities = AllSaveLoadEntities();
            var real = MatchSaveLoadHandler.CurrentSave;
            // Start from the run's save, as the game does: some entities (research in progress after a load) only
            // write their entry once, and rely on it staying in the reused MatchSave.
            var scratch = new MatchSave();
            if (real != null && !MatchSaveLoadHandler.OverwriteCurrentSave)
            {
                foreach (var pair in Priv.Get<Dictionary<string, object>>(typeof(MatchSave), "savedEntities", real))
                    scratch.AddValue(pair.Key, pair.Value is int[] a ? (int[])a.Clone() : pair.Value is TagManager.ETag[] t ? (TagManager.ETag[])t.Clone() : pair.Value);
            }
            Priv.SetStatic(typeof(MatchSaveLoadHandler), "currentSave", scratch);
            try
            {
                foreach (var entity in entities)
                {
                    if (entity != null) entity.ExecuteSave();
                }
            }
            finally
            {
                Priv.SetStatic(typeof(MatchSaveLoadHandler), "currentSave", real);
            }
            scratch.FetchLoadout();
            if (real != null)
            {
                scratch.hadRestarts = real.hadRestarts;
                scratch.highestScoreThisRun = real.highestScoreThisRun;
            }
            return FromMatchSave(scratch);
        }

        // The same entity list the game's LocalMatchSaveLoad saves.
        internal static IEnumerable<SaveLoadEntity> AllSaveLoadEntities()
        {
            var loader = Object.FindObjectOfType<LocalMatchSaveLoad>();
            var list = loader != null ? Priv.Get<SaveLoadEntity[]>(loader, "allSaveLoadEntities") : null;
            return list ?? Object.FindObjectsOfType<SaveLoadEntity>(true);
        }

        internal static NativeSave FromMatchSave(MatchSave save)
        {
            var native = new NativeSave
            {
                hadRestarts = save.hadRestarts,
                etSeed = save.etSeed,
                highestScoreThisRun = save.highestScoreThisRun,
                runComplete = save.runComplete,
                loadout = save.currentLoadout.Where(e => e != null).Select(e => e.displayName).ToList(),
            };
            var entries = Priv.Get<Dictionary<string, object>>(typeof(MatchSave), "savedEntities", save);
            foreach (var pair in entries)
            {
                switch (pair.Value)
                {
                    case int i: native.ints.Add(new IntEntry { key = pair.Key, value = i }); break;
                    case float f: native.floats.Add(new FloatEntry { key = pair.Key, value = f }); break;
                    case bool b: native.bools.Add(new BoolEntry { key = pair.Key, value = b }); break;
                    case string s: native.strings.Add(new StringEntry { key = pair.Key, value = s }); break;
                    case int[] a: native.intArrays.Add(new IntArrayEntry { key = pair.Key, value = (int[])a.Clone() }); break;
                    case TagManager.ETag[] t: native.tagArrays.Add(new IntArrayEntry { key = pair.Key, value = t.Select(x => (int)x).ToArray() }); break;
                }
            }
            return native;
        }

        internal static void SetInt(NativeSave native, string key, int value)
        {
            var entry = native.ints.FirstOrDefault(e => e.key == key);
            if (entry != null) entry.value = value;
            else native.ints.Add(new IntEntry { key = key, value = value });
        }

        // ---- Live objects ---------------------------------------------------------------------------------------

        private static void CaptureKing(SaveStateData data)
        {
            var king = PlayerMovement.instance;
            var hp = king.GetComponent<Hp>();
            var revive = king.GetComponent<AutoRevive>();
            var interaction = PlayerInteraction.instance;
            data.king = new KingData
            {
                valid = true,
                position = king.transform.position,
                meshRotation = king.meshParent != null ? king.meshParent.rotation : Quaternion.identity,
                hp = hp != null ? hp.HpValue : 0f,
                maxHp = hp != null ? hp.maxHp : 0f,
                knockedOutFor = revive != null ? Priv.Get<float>(revive, "hasBeenKnockedOutFor") : 0f,
                quickReviveAvailable = revive == null || Priv.Get<bool>(revive, "quickReviveAvailable"),
                manualCooldown = interaction.ManualAttack != null ? interaction.ManualAttack.Cooldown : 0f,
                autoCooldown = interaction.AutoAttack != null ? interaction.AutoAttack.Cooldown : 0f,
            };
        }

        private static void CapturePlayerObjects(SaveStateData data, ScenePaths paths)
        {
            var king = PlayerMovement.instance.gameObject;
            foreach (var hp in Object.FindObjectsOfType<Hp>())
            {
                var tagged = hp.GetComponent<TaggedObject>();
                if (hp.gameObject == king || !StateUtil.Has(tagged, TagManager.ETag.PlayerOwned)) continue;
                if (StateUtil.IsClone(hp.transform.root.gameObject)) continue;
                var unit = hp.GetComponent<PathfindMovementPlayerunit>();
                if (unit == null)
                {
                    data.playerHp.Add(new HpData { path = paths.Of(hp.transform), hp = hp.HpValue, maxHp = hp.maxHp });
                    continue;
                }
                if (hp.HpValue > 0f) data.unitsAlive++;
                data.units.Add(new UnitData
                {
                    path = paths.Of(hp.transform),
                    position = hp.transform.position,
                    rotation = hp.transform.rotation,
                    hp = hp.HpValue,
                    maxHp = hp.maxHp,
                    home = unit.HomePosition,
                    hold = unit.HoldPosition,
                    follow = unit.FollowingPlayer,
                    groups = GroupTags.Where(t => tagged.Tags.Contains(t)).Select(t => (int)t).ToArray(),
                });
            }
            foreach (var interactor in Object.FindObjectsOfType<BuildingInteractor>())
            {
                data.knockedOutTonight.Add(new FlagData { path = paths.Of(interactor.transform), value = interactor.KnockedOutTonight });
            }
            foreach (var temple in Object.FindObjectsOfType<TempleUpgrade>())
            {
                if (Priv.Get<TempleUpgrade.State>(temple, "state") == TempleUpgrade.State.EnabledTonight) data.templesTonight.Add(paths.Of(temple.transform));
            }
        }

        private static void CaptureEnemies(SaveStateData data, ScenePaths paths)
        {
            var afterlife = PerkManager.instance != null ? PerkManager.instance.godOfAfterlifeEnemy : null;
            var seen = new HashSet<GameObject>();
            foreach (var tagged in TagManager.instance.FindAllTaggedObjectsWithTagDirect_UseWithCare(TagManager.ETag.EnemyOwned))
            {
                if (tagged == null || !tagged.gameObject.activeInHierarchy) continue;
                var root = StateUtil.EnemyRoot(tagged);
                if (!seen.Add(root)) continue;
                var hps = root.GetComponentsInChildren<Hp>(true);
                if (hps.Length == 0 || !hps.Any(h => h.HpValue > 0f)) continue;
                bool clone = StateUtil.IsClone(root);
                var enemy = new EnemyData
                {
                    prefab = clone ? StateUtil.StripClone(root.name) : "",
                    scenePath = clone ? "" : paths.Of(root.transform),
                    position = root.transform.position,
                    rotation = root.transform.rotation,
                    hps = hps.Select(h => new EnemyHp
                    {
                        hp = h.HpValue,
                        maxHp = h.maxHp,
                        hpOriginal = h.HpOriginal,
                        coins = h.coinCount,
                        elite = h.Elite,
                        afterlife = afterlife != null && h.enemyToSpawnOnDeath == afterlife,
                    }).ToList(),
                    damage = root.GetComponentsInChildren<AutoAttack>(true).Select(a => a.DamageMultiplyer).ToArray(),
                    cooldowns = root.GetComponentsInChildren<AutoAttack>(true).Select(a => a.cooldownDuration).ToArray(),
                    speeds = root.GetComponentsInChildren<PathfindMovementEnemy>(true).Select(m => m.movementSpeed).ToArray(),
                };
                data.enemies.Add(enemy);
                data.enemiesAlive++;
            }
        }

        private static void CaptureSpawner(SaveStateData data, EnemySpawner spawner)
        {
            data.hasSpawner = true;
            data.spawner = new SpawnerData
            {
                wavenumber = spawner.Wavenumber,
                spawning = spawner.SpawningInProgress,
                lastSpawnPeriodDuration = spawner.LastSpawnPeriodDuration,
                lastSpawnPeriodClock = spawner.LastSpawnPeriodClock,
                timeSinceSpawningEnded = spawner.TimeSinceSpawningEnded,
                totalEnemiesSpawnedThisWave = EnemySpawner.totalEnemiesSpawnedThisWave,
            };
            if (spawner.Wavenumber < 0 || spawner.Wavenumber >= spawner.waves.Count) return;
            foreach (var spawn in spawner.waves[spawner.Wavenumber].spawns)
            {
                var coins = Priv.Get<int[]>(spawn, "goldCoinsPerEnemy");
                data.spawner.spawns.Add(new SpawnData
                {
                    wait = spawn.waitBeforeNextSpawn,
                    spawned = spawn.SpawnedUnits,
                    finished = spawn.Finished,
                    coins = coins != null ? (int[])coins.Clone() : [],
                });
            }
        }

        // ---- Thumbnail ------------------------------------------------------------------------------------------

        /// <summary>A small PNG of this frame as the player sees it. When a menu is open (saving from the
        /// savestates menu) the UI is hidden for that one frame so the picture shows the level, not the menu.</summary>
        public static IEnumerator WriteThumbnail(string path, Action<byte[]> done)
        {
            // Headless (-batchmode) draws no frames: the screen capture would only log a Unity error.
            if (Application.isBatchMode) yield break;
            var hidden = new List<Canvas>();
            if (UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame != null)
            {
                foreach (var canvas in Object.FindObjectsOfType<Canvas>())
                {
                    if (canvas.isRootCanvas && canvas.enabled && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    {
                        canvas.enabled = false;
                        hidden.Add(canvas);
                    }
                }
            }
            yield return new WaitForEndOfFrame();
            byte[] png = null;
            Texture2D shot = null, small = null;
            RenderTexture rt = null;
            var previousActive = RenderTexture.active;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                const int height = 270;
                int width = Mathf.Max(1, Mathf.RoundToInt(height * (float)shot.width / shot.height));
                rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(shot, rt);
                RenderTexture.active = rt;
                small = new Texture2D(width, height, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                small.Apply();
                png = small.EncodeToPNG();
            }
            catch (Exception e)
            {
                Log.For<SaveStatesManager>().Warn($"Thumbnail failed: {e.Message}");
            }
            finally
            {
                foreach (var canvas in hidden) if (canvas != null) canvas.enabled = true;
                RenderTexture.active = previousActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (shot != null) Object.Destroy(shot);
                if (small != null) Object.Destroy(small);
            }
            try { done(png); }
            catch (Exception e) { Log.For<SaveStatesManager>().Warn($"Writing thumbnail {path} failed: {e.Message}"); }
        }
    }
}
