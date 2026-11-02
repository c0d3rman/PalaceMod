using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/*
    The game's score formulas, mirrored exactly from ScoreManager (game 2.13+) so features can show or project scores
    without waiting for the game to award them. Read-only: nothing here changes the score.

    Night score (ScoreManager.CalculateEndOfNightScore, at dawn before sunrise), all scaled by scoreMultiplyerFromPerks
    (always 1 in the current game) and rounded to int:
        base       = baseScorePerNight
        time       = timeScorePerNight * InverseLerp(maxTime + spawnDuration, minTime + spawnDuration, nightLength)^exponent
        protection = protectionScorePerNight * (1 - knockedOut / buildings)^exponent      (buildings = Building + PlayerOwned)
    End of match (SceneTransitionManager.TransitionFromGameplayToEndScreen), on victory, rounded up:
        gold      = TrueBalance * victoryGoldBonusMultiplyer
        mutators  = (score + gold) * (product of EquippableMutation.scoreMultiplyerOnWin - 1)
        no retry  = (score + gold + mutators) * (noRetryScoreMultiplier - 1)   (InMiniModes variant on Stars levels; 0 after a restart/reload)
    On defeat only the no-retry bonus (on the score alone) is added. Eternal Trials victories count score + gold only.

    ScoreCalculator.Nights records the breakdown of every night scored since the level loaded (the game only keeps
    running totals), and LastNight is the most recent one; the final night's is otherwise never shown.
*/
namespace PalaceMod
{
    /// <summary>One night's score breakdown, as awarded by the game or projected for the current night.</summary>
    public readonly struct NightScore(int night, int baseScore, int timeBonus, float protectionFraction, int protectionBonus,
        float secondsAfterSpawning, int buildings, int buildingsKnockedOut)
    {
        /// <summary>1-based night number.</summary>
        public readonly int Night = night;
        public readonly int Base = baseScore;
        public readonly int Time = timeBonus;
        /// <summary>Share of buildings not knocked out tonight, 0..1 (the game displays it rounded to a whole percent).</summary>
        public readonly float ProtectionFraction = protectionFraction;
        public readonly int Protection = protectionBonus;
        /// <summary>Seconds between the end of spawning and now (or dawn); 0 while enemies are still spawning. -1 if unknown.</summary>
        public readonly float SecondsAfterSpawning = secondsAfterSpawning;
        /// <summary>Building counts behind ProtectionFraction; -1 if unknown (a night recorded from the game's event).</summary>
        public readonly int Buildings = buildings, BuildingsKnockedOut = buildingsKnockedOut;

        public int Total => Base + Time + Protection;
        public int ProtectionPercent => Mathf.RoundToInt(ProtectionFraction * 100f);
    }

    /// <summary>The bonuses the end screen adds to the in-game score.</summary>
    public readonly struct MatchEndScore(int score, int gold, int mutators, int noRetry)
    {
        public readonly int Score = score, Gold = gold, Mutators = mutators, NoRetry = noRetry;
        public int Total => Score + Gold + Mutators + NoRetry;
    }

    public static class ScoreCalculator
    {
        private static readonly AccessTools.FieldRef<ScoreManager, float> perkMultiplier =
            AccessTools.FieldRefAccess<ScoreManager, float>("scoreMultiplyerFromPerks");

        private static readonly List<TagManager.ETag> buildingTags = [TagManager.ETag.Building, TagManager.ETag.PlayerOwned];
        private static readonly List<TaggedObject> buffer = [];
        private static readonly List<NightScore> nights = [];
        private static ScoreManager recordingFrom;

        /// <summary>True inside a level with a running score (ScoreManager, DayNightCycle and EnemySpawner present).</summary>
        public static bool Available => ScoreManager.Instance != null && DayNightCycle.Instance != null && EnemySpawner.instance != null;

        public static bool IsNight => DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        /// <summary>The score banked so far this match (before end-of-match bonuses).</summary>
        public static int CurrentScore => ScoreManager.Instance != null ? ScoreManager.Instance.CurrentScore : 0;

        /// <summary>1-based number of the night in progress (at night) or the next night (by day); waves total.</summary>
        public static int NightNumber => EnemySpawner.instance == null ? 0 : Mathf.Min(EnemySpawner.instance.Wavenumber + (IsNight ? 1 : 2), EnemySpawner.instance.WaveCount);
        public static int NightCount => EnemySpawner.instance == null ? 0 : EnemySpawner.instance.WaveCount;

        /// <summary>True during the last night of the level: surviving it wins.</summary>
        public static bool IsFinalNight => IsNight && EnemySpawner.instance.Wavenumber >= EnemySpawner.instance.WaveCount - 1;

        /// <summary>The score the current night would award if it ended right now. Only meaningful at night.</summary>
        public static NightScore ProjectTonight()
        {
            var sm = ScoreManager.Instance;
            float perks = perkMultiplier(sm);
            float nightLength = DayNightCycle.Instance.CurrentNightLength;
            float spawnDuration = EnemySpawner.instance.LastSpawnPeriodDuration;

            int baseScore = Mathf.RoundToInt(sm.baseScorePerNight * perks);
            float timeT = Mathf.InverseLerp(sm.timeBonusMaxTime + spawnDuration, sm.timeBonusMinTime + spawnDuration, nightLength);
            int time = Mathf.RoundToInt(Mathf.Pow(timeT, sm.scoreExponent) * sm.timeScorePerNight * perks);

            TagManager.instance.FindAllTaggedObjectsWithTags(buffer, buildingTags, null);
            int knockedOut = 0;
            foreach (var building in buffer)
            {
                var interactor = building.transform.parent.GetComponentInChildren<BuildingInteractor>(includeInactive: true);
                if (interactor != null && interactor.KnockedOutTonight) knockedOut++;
            }
            int count = buffer.Count;
            buffer.Clear();
            float protected01 = 1f - Mathf.InverseLerp(0f, count, knockedOut);
            int protection = Mathf.RoundToInt(sm.protectionScorePerNight * Mathf.Pow(protected01, sm.scoreExponent) * perks);

            float afterSpawning = EnemySpawner.instance.SpawningInProgress ? 0f : Mathf.Max(0f, nightLength - spawnDuration);
            return new NightScore(NightNumber, baseScore, time, protected01, protection, afterSpawning, count, knockedOut);
        }

        /// <summary>Seconds after spawning ends at which the time bonus is full / gone.</summary>
        public static float TimeBonusFullUntil => ScoreManager.Instance.timeBonusMinTime;
        public static float TimeBonusGoneAfter => ScoreManager.Instance.timeBonusMaxTime;
        public static int MaxNightScore => ScoreManager.Instance.MaxScorePerNight;

        /// <summary>Equipped mutators and their victory score multipliers (the game multiplies them together).</summary>
        public static List<(string name, float multiplier)> Mutators()
        {
            var list = new List<(string, float)>();
            if (PerkManager.instance == null) return list;
            foreach (var e in PerkManager.instance.CurrentlyEquipped)
                if (e != null && e.GetType() == typeof(EquippableMutation))
                    list.Add((e.displayName, ((EquippableMutation)e).scoreMultiplyerOnWin));
            return list;
        }

        public static float MutatorMultiplier()
        {
            float m = 1f;
            foreach (var (_, multiplier) in Mutators()) m *= multiplier;
            return m;
        }

        /// <summary>The no-retry multiplier this match qualifies for, or 1 if the run had a restart or reload.</summary>
        public static float NoRetryMultiplier()
        {
            var sm = ScoreManager.Instance;
            if (MatchSaveLoadHandler.CurrentSave != null && MatchSaveLoadHandler.CurrentSave.hadRestarts) return 1f;
            bool stars = LevelProgressManager.instance.GetLevelInfoFromCurrentSceneName().contribution == LevelInfo.ProgressionContribution.Stars;
            return stars ? sm.noRetryScoreMultiplierInMiniModes : sm.noRetryScoreMultiplier;
        }

        public static bool IsEternalTrial => LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.EternalTrial;

        /// <summary>The end screen for a victory with the given in-game score and the current gold.</summary>
        public static MatchEndScore Victory(int score)
        {
            var sm = ScoreManager.Instance;
            int gold = Mathf.CeilToInt(PlayerInteraction.instance.TrueBalance * sm.victoryGoldBonusMultiplyer);
            if (IsEternalTrial) return new MatchEndScore(score, gold, 0, 0);
            int mutators = Mathf.CeilToInt((score + gold) * (MutatorMultiplier() - 1f));
            int noRetry = Mathf.CeilToInt((score + gold + mutators) * (NoRetryMultiplier() - 1f));
            return new MatchEndScore(score, gold, mutators, noRetry);
        }

        /// <summary>The end screen for a defeat with the given in-game score.</summary>
        public static MatchEndScore Defeat(int score)
        {
            if (IsEternalTrial) return new MatchEndScore(score, 0, 0, 0);
            return new MatchEndScore(score, 0, 0, Mathf.CeilToInt(score * (NoRetryMultiplier() - 1f)));
        }

        /// <summary>Every night scored since this level loaded, in order.</summary>
        public static IReadOnlyList<NightScore> Nights { get { EnsureRecording(); return nights; } }

        /// <summary>The most recently scored night, or null if none has been scored since the level loaded.</summary>
        public static NightScore? LastNight
        {
            get { EnsureRecording(); return nights.Count > 0 ? nights[nights.Count - 1] : null; }
        }

        /// <summary>Start listening to the game's night-score event (idempotent; call it whenever a level may have loaded).</summary>
        public static void EnsureRecording()
        {
            var sm = ScoreManager.Instance;
            if (sm == recordingFrom) return;
            nights.Clear();
            recordingFrom = sm;
            if (sm != null) sm.OnNightScoreAdd.AddListener(OnNightScored);
        }

        private static void OnNightScored(int baseScore, int timeBonus, float protectionFraction, int protectionBonus)
        {
            // Raised at dawn before the spawner advances, so Wavenumber is still tonight's.
            int night = EnemySpawner.instance != null ? EnemySpawner.instance.Wavenumber + 1 : nights.Count + 1;
            float after = -1f;
            if (DayNightCycle.Instance != null && EnemySpawner.instance != null)
                after = Mathf.Max(0f, DayNightCycle.Instance.CurrentNightLength - EnemySpawner.instance.LastSpawnPeriodDuration);
            nights.Add(new NightScore(night, baseScore, timeBonus, protectionFraction, protectionBonus, after, -1, -1));
        }
    }
}
