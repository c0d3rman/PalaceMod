using System;
using System.Collections.Generic;
using UnityEngine;

/*
    On-disk shape of a savestate (JsonUtility, so plain [Serializable] classes with public fields; JsonUtility can't
    write null objects, hence the Has* flags).

    A state is the game's own mid-level save data captured at that moment (every SaveLoadEntity's OnSave: buildings,
    upgrades, building HP and tags, gold, wave, score, research timers, shrine XP, ...) plus what the native format
    misses: time within the day/night, enemies, spawn progress, units, king, coins on the ground, RNG, per-day stats.
*/
namespace PalaceMod
{
    public enum SaveStateKind
    {
        /// <summary>Saved by the player from the savestates menu (or the API with no kind).</summary>
        Manual,
        /// <summary>The single quick-save slot (quick save key).</summary>
        Quick,
        /// <summary>Taken automatically shortly after each sunrise.</summary>
        AutoDawn,
        /// <summary>Taken automatically right before each night starts (still daytime).</summary>
        AutoDusk,
        /// <summary>Taken automatically right before a state is loaded, so a load can be undone.</summary>
        BeforeLoad,
    }

    [Serializable]
    internal class SaveStateData
    {
        public const int CurrentFormat = 1;

        public int format = CurrentFormat;
        public string modVersion;
        public string gameVersion;

        // Metadata (also shown in the browser).
        public string name;
        public string kind;
        public string level;
        public string createdUtc;
        /// <summary>EnemySpawner.Wavenumber at capture: -1 on the first day, n during night n+1 and the day after it.</summary>
        public int wave;
        public int waveCount;
        public bool night;
        public int gold;
        public int score;
        public int unitsAlive;
        public int enemiesAlive;

        public NativeSave native = new();
        public bool hasDawn;
        public NativeSave dawn = new();

        public string randomState;
        public LevelStats stats = new();
        public TimeData time = new();
        public KingData king = new();
        public List<HpData> playerHp = [];
        public List<UnitData> units = [];
        public List<FlagData> knockedOutTonight = [];
        public List<EnemyData> enemies = [];
        public List<Vector3> freeCoins = [];
        public bool hasSpawner;
        public SpawnerData spawner = new();
    }

    [Serializable]
    internal class NativeSave
    {
        public List<IntEntry> ints = [];
        public List<FloatEntry> floats = [];
        public List<BoolEntry> bools = [];
        public List<StringEntry> strings = [];
        public List<IntArrayEntry> intArrays = [];
        public List<IntArrayEntry> tagArrays = [];
        public List<string> loadout = [];
        public bool hadRestarts;
        public int etSeed;
        public int highestScoreThisRun;
        public bool runComplete;
    }

    [Serializable] internal class IntEntry { public string key; public int value; }
    [Serializable] internal class FloatEntry { public string key; public float value; }
    [Serializable] internal class BoolEntry { public string key; public bool value; }
    [Serializable] internal class StringEntry { public string key; public string value; }
    [Serializable] internal class IntArrayEntry { public string key; public int[] value; }

    [Serializable]
    internal class LevelStats
    {
        public bool valid;
        public int highscore;
        public List<int> dayToDayScore = [];
        public List<int> dayToDayNetworth = [];
    }

    [Serializable]
    internal class TimeData
    {
        public float currentNightLength;
        public float remainingAutoDayTime;
        public float remainingAutoNightTime;
    }

    [Serializable]
    internal class KingData
    {
        public bool valid;
        public Vector3 position;
        public Quaternion meshRotation;
        public float hp;
        public float maxHp;
        public float knockedOutFor;
        public bool quickReviveAvailable = true;
        public float manualCooldown;
        public float autoCooldown;
    }

    [Serializable]
    internal class HpData
    {
        public string path;
        public float hp;
        public float maxHp;
    }

    [Serializable]
    internal class UnitData
    {
        public string path;
        public Vector3 position;
        public Quaternion rotation;
        public float hp;
        public float maxHp;
        public Vector3 home;
        public bool hold;
        public bool follow;
        public int[] groups = [];
    }

    [Serializable]
    internal class FlagData
    {
        public string path;
        public bool value;
    }

    [Serializable]
    internal class EnemyData
    {
        /// <summary>Prefab name for spawned enemies; empty for enemies placed in the scene.</summary>
        public string prefab;
        /// <summary>Hierarchy path for enemies placed in the scene; empty for spawned ones.</summary>
        public string scenePath;
        public Vector3 position;
        public Quaternion rotation;
        public List<EnemyHp> hps = [];
        public float[] damage = [];
        public float[] cooldowns = [];
        public float[] speeds = [];
    }

    [Serializable]
    internal class EnemyHp
    {
        public float hp;
        public float maxHp;
        public float hpOriginal;
        public int coins;
        public bool elite;
        public bool afterlife;
    }

    [Serializable]
    internal class SpawnerData
    {
        public int wavenumber;
        public bool spawning;
        public float lastSpawnPeriodDuration;
        public float lastSpawnPeriodClock;
        public float timeSinceSpawningEnded;
        public int totalEnemiesSpawnedThisWave;
        public List<SpawnData> spawns = [];
    }

    [Serializable]
    internal class SpawnData
    {
        public float wait;
        public int spawned;
        public bool finished;
        public int[] coins = [];
    }
}
