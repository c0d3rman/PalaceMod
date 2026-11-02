using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

/*
    Replaces the host's waves. BuilderManager's prefix on EnemySpawner.Start calls Apply, which runs before the
    spawner's own Start logic, so the game's perk modifiers (Elite God, Rat God, Loan, Royal Mint, Cheese God),
    starting gold payout and night preview all work on the custom waves unchanged.
*/
namespace PalaceMod.Builder
{
    public sealed class WaveInjector : IWaveInjector
    {
        private readonly IUnitFactory units;

        public (DesignPack pack, WaveSetDef waveSet) Pending { get; private set; }

        public WaveInjector(IUnitFactory units) => this.units = units;

        public void SetPending(DesignPack pack, WaveSetDef waveSet) => Pending = waveSet == null ? default : (pack, waveSet);

        /// <summary>Called from the EnemySpawner.Start prefix. Returns whether the spawner's waves were replaced.</summary>
        internal bool Apply(EnemySpawner spawner)
        {
            var (pack, waveSet) = Pending;
            if (waveSet == null) return false;
            string scene = spawner.gameObject.scene.name;
            if (!string.Equals(scene, waveSet.Host, System.StringComparison.OrdinalIgnoreCase))
            {
                Log.For<BuilderManager>().Warn($"Wave set {pack.Id}/{waveSet.Id} is for {waveSet.Host} but {scene} loaded; playing vanilla waves");
                return false;
            }
            var mode = LocalGamestate.GameModeSet ? LocalGamestate.SelectedGameMode : spawner.currentMode;
            if (mode != LocalGamestate.GameMode.Classic)
            {
                Log.For<BuilderManager>().Warn("Custom waves only replace Classic mode waves; playing vanilla waves");
                return false;
            }
            var issues = new List<ValidationIssue>();
            var waves = BuildWaves(pack, waveSet, spawner, issues);
            foreach (var i in issues) Log.For<BuilderManager>().Warn($"{pack.Id}/{waveSet.Id}: {i}");
            if (waves.Count == 0)
            {
                Log.For<BuilderManager>().Error($"Wave set {pack.Id}/{waveSet.Id} produced no nights; playing vanilla waves");
                return false;
            }
            spawner.waves = waves;
            if (waveSet.StartingGold is int gold) spawner.goldBalanceAtStart = gold;
            Log.For<BuilderManager>().Info($"Injected wave set {pack.Id}/{waveSet.Id} on {scene}: {waves.Count} nights, " +
                $"{waves.Sum(w => w.spawns.Sum(s => s.count))} enemies, starting gold {spawner.goldBalanceAtStart}");
            return true;
        }

        public List<Wave> BuildWaves(DesignPack pack, WaveSetDef waveSet, EnemySpawner spawner, List<ValidationIssue> issues)
        {
            var lines = new Dictionary<string, EnemySpawnLine>();
            foreach (var line in spawner.GetComponentsInChildren<EnemySpawnLine>(true))
                if (!lines.ContainsKey(line.name)) lines[line.name] = line;

            var waves = new List<Wave>();
            for (int n = 0; n < (waveSet.Nights?.Count ?? 0); n++)
            {
                var night = waveSet.Nights[n];
                if (night == null) continue;
                var wave = new Wave { warningText = night.Warning ?? "", difficultyMulti = night.Difficulty };
                for (int s = 0; s < (night.Spawns?.Count ?? 0); s++)
                {
                    var sp = night.Spawns[s];
                    string path = $"nights[{n}].spawns[{s}]";
                    var prefab = units.Resolve(pack, sp.Unit, out string error);
                    if (prefab == null) { issues.Add(new ValidationIssue(Severity.Error, path + ".unit", error + "; skipped")); continue; }
                    if (sp.Line == null || !lines.TryGetValue(sp.Line, out var spawnLine))
                    {
                        issues.Add(new ValidationIssue(Severity.Error, path + ".line", $"no spawn line \"{sp.Line}\" on {waveSet.Host}; skipped"));
                        continue;
                    }
                    wave.spawns.Add(new Spawn(prefab, spawnLine.transform, sp.Count, sp.Interval, sp.Gold, sp.Delay, sp.Elite));
                }
                if (wave.spawns.Count == 0) issues.Add(new ValidationIssue(Severity.Warning, $"nights[{n}]", "has no spawns left; the night ends at once"));
                waves.Add(wave);
            }
            return waves;
        }
    }
}
