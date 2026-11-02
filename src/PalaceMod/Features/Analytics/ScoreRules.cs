using HarmonyLib;
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

    Everything the shared ScoreCalculator covers comes from it; IScoreRules adds what the projection needs on top.
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
        MatchEndScore Victory(int score, int bank);
    }

    /// <summary>Backed by the shared ScoreCalculator; only the per-night formulas for an assumed finish time and
    /// protection, and the victory bonuses for a projected bank, are worked out here.</summary>
    public sealed class GameScoreRules : IScoreRules
    {
        public static readonly GameScoreRules Instance = new();

        private static readonly AccessTools.FieldRef<ScoreManager, float> perkMultiplier =
            AccessTools.FieldRefAccess<ScoreManager, float>("scoreMultiplyerFromPerks");

        private static ScoreManager Sm => ScoreManager.Instance;
        private static float Perks => perkMultiplier(Sm);

        public bool Available => ScoreCalculator.Available;
        public int CurrentScore => ScoreCalculator.CurrentScore;
        public int BasePerNight => Mathf.RoundToInt(Sm.baseScorePerNight * Perks);
        public float TimeBonusFullUntil => ScoreCalculator.TimeBonusFullUntil;
        public float TimeBonusGoneAfter => ScoreCalculator.TimeBonusGoneAfter;

        public int TimeBonus(float secondsAfterSpawning)
        {
            float t = Mathf.InverseLerp(Sm.timeBonusMaxTime, Sm.timeBonusMinTime, Mathf.Max(0f, secondsAfterSpawning));
            return Mathf.RoundToInt(Mathf.Pow(t, Sm.scoreExponent) * Sm.timeScorePerNight * Perks);
        }

        public int ProtectionBonus(float protectedFraction) =>
            Mathf.RoundToInt(Sm.protectionScorePerNight * Mathf.Pow(Mathf.Clamp01(protectedFraction), Sm.scoreExponent) * Perks);

        // ScoreCalculator.Victory uses the gold held now; this is the same sum for a projected bank.
        public MatchEndScore Victory(int score, int bank)
        {
            int gold = Mathf.CeilToInt(Mathf.Max(0, bank) * Sm.victoryGoldBonusMultiplyer);
            if (ScoreCalculator.IsEternalTrial) return new MatchEndScore(score, gold, 0, 0);
            int mutators = Mathf.CeilToInt((score + gold) * (ScoreCalculator.MutatorMultiplier() - 1f));
            int noRetry = Mathf.CeilToInt((score + gold + mutators) * (ScoreCalculator.NoRetryMultiplier() - 1f));
            return new MatchEndScore(score, gold, mutators, noRetry);
        }
    }
}
