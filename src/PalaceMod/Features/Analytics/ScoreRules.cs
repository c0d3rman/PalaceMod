using UnityEngine;

/*
    The game's scoring rules, as the analytics projection needs them: what a night would award for a given finish
    time and protection, and what the end screen adds on a victory for a given score and bank.

    Mirrors the game (2.13), decompiled:
      ScoreManager.CalculateEndOfNightScore (at dawn, before sunrise), each part scaled by scoreMultiplyerFromPerks
      (always 1) and rounded:
        base       = baseScorePerNight
        time       = timeScorePerNight * InverseLerp(timeBonusMaxTime, timeBonusMinTime, secondsAfterSpawning)^exponent
                     (the game writes it as nightLength against maxTime/minTime + LastSpawnPeriodDuration; same thing)
        protection = protectionScorePerNight * (1 - knockedOut / buildings)^exponent
      Victory (ScoreManager.Victory*Bonus), each rounded up:
        gold     = TrueBalance * victoryGoldBonusMultiplyer          TrueBalance includes coins still on the ground
                                                                     or waiting to spawn, so the final dawn's income counts
        mutators = (score + gold) * (product of mutator scoreMultiplyerOnWin - 1)
        no retry = (score + gold + mutators) * (noRetryScoreMultiplier - 1)   (InMiniModes on Stars levels; 0 once the
                   run had a restart or a reload)
      Eternal Trials victories count score + gold only.

    It's behind IScoreRules so it can be swapped for a shared score calculator (the pause-menu score feature's
    ScoreCalculator) once that's on main, without touching the projection.
*/
namespace PalaceMod
{
    public interface IScoreRules
    {
        /// <summary>True inside a level with a score.</summary>
        bool Available { get; }
        int CurrentScore { get; }
        int BasePerNight { get; }
        int TimeBonus(float secondsAfterSpawning);
        int ProtectionBonus(float protectedFraction);
        /// <summary>Seconds after the last spawn up to which the time bonus is full, and after which it's gone.</summary>
        float TimeBonusFullUntil { get; }
        float TimeBonusGoneAfter { get; }
        VictoryBonus Victory(int score, int bank);
    }

    public readonly struct VictoryBonus
    {
        public readonly int Score, Gold, Mutators, NoRetry;
        public VictoryBonus(int score, int gold, int mutators, int noRetry)
        {
            Score = score; Gold = gold; Mutators = mutators; NoRetry = noRetry;
        }
        public int Total => Score + Gold + Mutators + NoRetry;
    }

    public sealed class GameScoreRules : IScoreRules
    {
        public static readonly GameScoreRules Instance = new();

        private static ScoreManager Sm => ScoreManager.Instance;
        private static float Perks => Sm == null ? 1f : HarmonyLib.Traverse.Create(Sm).Field("scoreMultiplyerFromPerks").GetValue<float>() is var p && p > 0f ? p : 1f;

        public bool Available => Sm != null && EnemySpawner.instance != null && DayNightCycle.Instance != null;
        public int CurrentScore => Sm != null ? Sm.CurrentScore : 0;
        public int BasePerNight => Mathf.RoundToInt(Sm.baseScorePerNight * Perks);
        public float TimeBonusFullUntil => Sm.timeBonusMinTime;
        public float TimeBonusGoneAfter => Sm.timeBonusMaxTime;

        public int TimeBonus(float secondsAfterSpawning)
        {
            float t = Mathf.InverseLerp(Sm.timeBonusMaxTime, Sm.timeBonusMinTime, Mathf.Max(0f, secondsAfterSpawning));
            return Mathf.RoundToInt(Mathf.Pow(t, Sm.scoreExponent) * Sm.timeScorePerNight * Perks);
        }

        public int ProtectionBonus(float protectedFraction) =>
            Mathf.RoundToInt(Sm.protectionScorePerNight * Mathf.Pow(Mathf.Clamp01(protectedFraction), Sm.scoreExponent) * Perks);

        public VictoryBonus Victory(int score, int bank)
        {
            int gold = Mathf.CeilToInt(Mathf.Max(0, bank) * Sm.victoryGoldBonusMultiplyer);
            if (LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.EternalTrial) return new VictoryBonus(score, gold, 0, 0);
            float mutator = 1f;
            if (PerkManager.instance != null)
            {
                foreach (var e in PerkManager.instance.CurrentlyEquipped)
                    if (e != null && e.GetType() == typeof(EquippableMutation)) mutator *= ((EquippableMutation)e).scoreMultiplyerOnWin;
            }
            int mutators = Mathf.CeilToInt((score + gold) * (mutator - 1f));
            int noRetry = Mathf.CeilToInt((score + gold + mutators) * (NoRetryMultiplier() - 1f));
            return new VictoryBonus(score, gold, mutators, noRetry);
        }

        private static float NoRetryMultiplier()
        {
            if (MatchSaveLoadHandler.CurrentSave != null && MatchSaveLoadHandler.CurrentSave.hadRestarts) return 1f;
            var info = LevelProgressManager.instance?.GetLevelInfoFromCurrentSceneName();
            bool stars = info != null && info.contribution == LevelInfo.ProgressionContribution.Stars;
            return stars ? Sm.noRetryScoreMultiplierInMiniModes : Sm.noRetryScoreMultiplier;
        }
    }
}
