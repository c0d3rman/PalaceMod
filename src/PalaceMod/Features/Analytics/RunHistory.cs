using System;
using System.Collections.Generic;
using Newtonsoft.Json;

/*
    What the analytics feature records about a run, one NightRecord per night, plus the in-progress day.

    A "cycle" is the day before night N plus night N. Gold is measured with PlayerInteraction.TrueBalance (balance plus
    coins on the ground or still to spawn), so income counts the moment it's earned, not when the coins reach the king:
        earned = change in TrueBalance + gold spent
    Night 1's day income includes the starting gold.

    Serialized with the game's own Newtonsoft.Json (Unity's JsonUtility drops the list of records from a mod assembly).
    The history is stored inside the game's own match save (MatchSave, under one string key), so it is saved, loaded,
    rolled back by "retry day" and discarded by a fresh start exactly like the run itself.
*/
namespace PalaceMod
{
    [Serializable]
    public class NightRecord
    {
        public int night;               // 1-based

        // The day before the night.
        public float daySeconds;        // game seconds spent building
        public int dayEarned, daySpent;

        // At dusk.
        public int bankAtDusk;          // TrueBalance going into the night
        public int buildings, units;

        // The night.
        public float nightSeconds;      // DayNightCycle.CurrentNightLength at dawn
        public float afterSpawning;     // seconds between the last spawn and dawn (drives the time bonus)
        public int nightEarned, nightSpent;
        public int unitsLost, buildingsKnockedOut;

        // Score awarded at dawn.
        public int scoreBase, scoreTime, scoreProtection;
        public float protectedFraction;
        public int scoreAfter;          // running score after this night
        public int buildingIncome;      // what buildings pay at the following dawn
        public bool complete;           // false while the night is in progress

        [JsonIgnore] public int ScoreTotal => scoreBase + scoreTime + scoreProtection;
        [JsonIgnore] public int Earned => dayEarned + nightEarned;
        [JsonIgnore] public int Spent => daySpent + nightSpent;
    }

    [Serializable]
    public class RunHistory
    {
        public const string SaveKey = "PalaceMod_Analytics_v1";

        public int version = 1;
        public string level;
        public List<NightRecord> nights = new();

        // The day in progress.
        public int dawnBank;            // TrueBalance at the last dawn (before that dawn's income)
        public int spentSinceDawn;
        public float daySeconds;

        [JsonIgnore] public NightRecord Current => nights.Count > 0 && !nights[nights.Count - 1].complete ? nights[nights.Count - 1] : null;

        [JsonIgnore] public int CompletedCount
        {
            get
            {
                int n = 0;
                foreach (var r in nights) if (r.complete) n++;
                return n;
            }
        }

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static RunHistory FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var history = JsonConvert.DeserializeObject<RunHistory>(json);
                return history != null && history.version == 1 ? history : null;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Analytics: couldn't read the run history from the save: {e.Message}");
                return null;
            }
        }
    }
}
