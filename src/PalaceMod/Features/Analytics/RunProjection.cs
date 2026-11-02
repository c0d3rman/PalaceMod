using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/*
    Projects the rest of a run night by night, from the current state plus a few assumptions the player can tweak.

    Each remaining night k is simulated in the game's order:
      day k     spend: the first projected day spends `SpendShare` of everything on hand (bank + already spent today);
                later days spend that share of what came in since the previous day.
      dusk k    Interest perk pays min(max, ceil(balance / each)).
      night k   enemies drop the wave's gold (WaveInfo.goldReward); the night scores base + time bonus (for the assumed
                seconds after the last spawn) + protection bonus (for the assumed share of buildings left standing).
      dawn k    buildings pay IncomeCalculator's income for that dawn, plus the scenario's extra income.
    The final dawn's income counts: the game declares victory after sunrise, and the gold bonus uses TrueBalance,
    which includes coins still to spawn. A night in progress keeps what's already happened (its gold so far, its
    knock-outs, its time past the last spawn).

    Not modelled: Treasure Hunter, Builder's Guild upgrades, mines running dry faster than IncomeCalculator says,
    and anything built later (that's what the scenario's extra income is for).
*/
namespace PalaceMod
{
    /// <summary>The player's what-if knobs.</summary>
    public sealed class Scenario
    {
        /// <summary>Extra building income per dawn (e.g. from houses you plan to build).</summary>
        public int ExtraIncome;
        /// <summary>First night whose dawn pays ExtraIncome (1-based).</summary>
        public int ExtraFromNight;
        /// <summary>Finish each remaining night this many seconds sooner after the last spawn.</summary>
        public float FasterSeconds;
        /// <summary>Share of income spent each day, 0..1; null = the run's own average so far.</summary>
        public float? SpendShare;

        public bool IsBaseline => ExtraIncome == 0 && FasterSeconds == 0f && SpendShare == null;
        public Scenario Clone() => (Scenario)MemberwiseClone();
    }

    public sealed class ProjectedNight
    {
        public int Night;
        public int Enemies;
        public float EnemyHp;
        public int EnemyGold;
        public float Difficulty;
        public int Spent;
        public int Interest;
        public int BankAtDusk;
        public int BuildingIncome;
        public int BankAtDawn;
        public int ScoreBase, ScoreTime, ScoreProtection;
        public int ScoreAfter;
        public bool InProgress;
        /// <summary>Gold earned and spent over the whole cycle (the day before plus the night), like NightRecord's
        /// Earned/Spent: includes the previous dawn's income and, for the first night, what already happened.</summary>
        public int CycleEarned, CycleSpent;
        public int ScoreTotal => ScoreBase + ScoreTime + ScoreProtection;
        public int Income => Interest + EnemyGold + BuildingIncome;
    }

    public sealed class Projection
    {
        public readonly List<ProjectedNight> Nights = new();
        public float AssumedAfterSpawning;
        public float AssumedProtection;
        public float AssumedSpendShare;
        public int FinalScore;
        public int FinalBank;
        public VictoryBonus Victory;
    }

    public static class RunProjection
    {
        public const float DefaultAfterSpawning = 30f;

        /// <summary>1-based number of the night in progress (at night) or coming next (by day).</summary>
        public static int NextNight => EnemySpawner.instance == null ? 0 : EnemySpawner.instance.Wavenumber + (IsNight ? 1 : 2);
        public static int NightCount => EnemySpawner.instance == null ? 0 : EnemySpawner.instance.WaveCount;
        public static bool IsNight => DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        public static Projection Run(RunHistory history, IScoreRules rules, Scenario scenario)
        {
            var p = new Projection();
            if (!rules.Available || PlayerInteraction.instance == null) return p;

            // Assumptions from the run so far.
            float after = 0f, prot = 0f, spent = 0f, earned = 0f;
            int n = 0;
            foreach (var r in history.nights)
            {
                if (!r.complete) continue;
                after += r.afterSpawning;
                prot += r.protectedFraction;
                spent += r.Spent;
                earned += r.Earned;
                n++;
            }
            p.AssumedAfterSpawning = n > 0 ? after / n : DefaultAfterSpawning;
            p.AssumedProtection = n > 0 ? prot / n : 1f;
            p.AssumedSpendShare = scenario.SpendShare ?? (earned > 0f ? Mathf.Clamp01(spent / earned) : 1f);
            float afterSpawning = Mathf.Max(0f, p.AssumedAfterSpawning - scenario.FasterSeconds);

            int score = rules.CurrentScore;
            // During sunrise the night is scored but buildings haven't paid yet.
            int bank = PlayerInteraction.instance.TrueBalance + (IsNight ? 0 : history.PendingDawnIncome);
            int first = NextNight, last = NightCount;
            int extraFrom = Mathf.Clamp(scenario.ExtraFromNight, first, Mathf.Max(first, last));
            bool night = IsNight;
            int incomeSinceLastDay = 0, previousDawnIncome = 0;

            for (int k = first; k <= last; k++)
            {
                var pn = new ProjectedNight { Night = k, InProgress = night && k == first };
                var info = EnemySpawner.GetWaveInfoByNumber(k - 1);
                if (info != null)
                {
                    pn.EnemyGold = info.goldReward;
                    pn.Difficulty = info.difficultyMulti;
                    foreach (var e in info.enemies)
                    {
                        pn.Enemies += e.enemyCount;
                        pn.EnemyHp += e.enemyCount * e.maxHP * info.difficultyMulti * (e.eliteEnemy ? 4f : 1f);
                    }
                }

                if (pn.InProgress)
                {
                    // Already past dusk: only the gold not yet dropped is still to come.
                    var current = history.Current;
                    int earnedTonight = current != null ? bank - current.bankAtDusk + current.nightSpent : 0;
                    pn.BankAtDusk = current?.bankAtDusk ?? bank;
                    int stillToCome = Mathf.Max(0, pn.EnemyGold - earnedTonight);
                    bank += stillToCome;
                    pn.CycleEarned = (current?.dayEarned ?? 0) + earnedTonight + stillToCome;
                    pn.CycleSpent = current != null ? current.daySpent + current.nightSpent : 0;
                }
                else
                {
                    int spentToday = 0, earnedToday = previousDawnIncome;
                    if (k == first)
                    {
                        spentToday = history.spentSinceDawn;
                        earnedToday = bank - history.dawnBank + spentToday;
                        pn.Spent = Mathf.Clamp(Mathf.RoundToInt(p.AssumedSpendShare * (bank + spentToday)) - spentToday, 0, bank);
                    }
                    else
                    {
                        pn.Spent = Mathf.Clamp(Mathf.RoundToInt(p.AssumedSpendShare * incomeSinceLastDay), 0, bank);
                    }
                    bank -= pn.Spent;
                    pn.Interest = Interest(bank);
                    bank += pn.Interest;
                    pn.BankAtDusk = bank;
                    bank += pn.EnemyGold;
                    pn.CycleEarned = earnedToday + pn.Interest + pn.EnemyGold;
                    pn.CycleSpent = spentToday + pn.Spent;
                }

                // Score.
                pn.ScoreBase = rules.BasePerNight;
                if (pn.InProgress)
                {
                    float soFar = EnemySpawner.instance.SpawningInProgress ? 0f
                        : Mathf.Max(0f, DayNightCycle.Instance.CurrentNightLength - EnemySpawner.instance.LastSpawnPeriodDuration);
                    pn.ScoreTime = rules.TimeBonus(Mathf.Max(soFar, afterSpawning));
                    pn.ScoreProtection = rules.ProtectionBonus(Mathf.Min(LiveProtection(), p.AssumedProtection));
                }
                else
                {
                    pn.ScoreTime = rules.TimeBonus(afterSpawning);
                    pn.ScoreProtection = rules.ProtectionBonus(p.AssumedProtection);
                }
                score += pn.ScoreTotal;
                pn.ScoreAfter = score;

                // Dawn.
                int dawnsAhead = k - first;
                pn.BuildingIncome = IncomeCalculator.Total(dawnsAhead) + (scenario.ExtraIncome != 0 && k >= extraFrom ? scenario.ExtraIncome : 0);
                bank += pn.BuildingIncome;
                pn.BankAtDawn = bank;
                incomeSinceLastDay = pn.EnemyGold + pn.BuildingIncome + pn.Interest;
                previousDawnIncome = pn.BuildingIncome;
                p.Nights.Add(pn);
            }

            p.FinalScore = score;
            p.FinalBank = bank;
            p.Victory = rules.Victory(score, bank);
            return p;
        }

        /// <summary>Share of buildings not knocked out tonight (what the protection bonus would use right now).</summary>
        public static float LiveProtection()
        {
            CountBuildings(out int total, out int down);
            return total == 0 ? 1f : 1f - (float)down / total;
        }

        private static readonly List<TaggedObject> buffer = new();
        private static readonly List<TagManager.ETag> buildingTags = new() { TagManager.ETag.Building, TagManager.ETag.PlayerOwned };

        /// <summary>Counts buildings the way the protection bonus does.</summary>
        public static void CountBuildings(out int total, out int knockedOut)
        {
            total = knockedOut = 0;
            if (TagManager.instance == null) return;
            TagManager.instance.FindAllTaggedObjectsWithTags(buffer, buildingTags, null);
            foreach (var b in buffer)
            {
                if (b == null || b.transform.parent == null) continue;
                var interactor = b.transform.parent.GetComponentInChildren<BuildingInteractor>(includeInactive: true);
                if (interactor != null && interactor.KnockedOutTonight) knockedOut++;
            }
            total = buffer.Count;
            buffer.Clear();
        }

        private static int Interest(int balance)
        {
            var perk = InterestPerk.instance;
            if (perk == null) return 0;
            var t = Traverse.Create(perk);
            int each = Mathf.Max(1, t.Field("interestForEach").GetValue<int>());
            int max = t.Field("maximumInterest").GetValue<int>();
            return Mathf.Min(max, Mathf.CeilToInt((float)Mathf.Max(0, balance) / each));
        }
    }
}
