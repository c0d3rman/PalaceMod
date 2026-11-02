using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PalaceMod.Builder.Model
{
    /// <summary>
    /// The nights of a run on one host map. Replaces the map's vanilla waves before the level starts; the player's
    /// perks (Cheese God, Rat God, Loan, Elite God, ...) then apply to it exactly as they do to vanilla waves.
    /// </summary>
    public sealed class WaveSetDef
    {
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>Scene name of the host map, e.g. "Nordfels". `builder dump` lists hosts and their spawn lines.</summary>
        [JsonProperty("host")] public string Host { get; set; }
        /// <summary>Gold the player starts with; null keeps the host's.</summary>
        [JsonProperty("startingGold")] public int? StartingGold { get; set; }
        /// <summary>One entry per night, in order. The run is won after the last one.</summary>
        [JsonProperty("nights")] public List<NightDef> Nights { get; set; } = [];
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class NightDef
    {
        /// <summary>Text shown before the night starts (the game's "warningText"); empty for none.</summary>
        [JsonProperty("warning")] public string Warning { get; set; }
        /// <summary>The game's per-night HP/damage multiplier (1 = normal). Shown as skulls in the night preview.</summary>
        [JsonProperty("difficulty")] public float Difficulty { get; set; } = 1f;
        [JsonProperty("spawns")] public List<SpawnDef> Spawns { get; set; } = [];
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A group of identical enemies coming from one spawn line.</summary>
    public sealed class SpawnDef
    {
        /// <summary>Unit ref: a unit id in this pack, "vanilla:&lt;catalog id&gt;", or "&lt;packId&gt;/&lt;unitId&gt;".</summary>
        [JsonProperty("unit")] public string Unit { get; set; }
        [JsonProperty("count")] public int Count { get; set; } = 1;
        /// <summary>Seconds between enemies of this group.</summary>
        [JsonProperty("interval")] public float Interval { get; set; } = 0.5f;
        /// <summary>Seconds after nightfall before the first enemy of this group.</summary>
        [JsonProperty("delay")] public float Delay { get; set; }
        /// <summary>Name of an EnemySpawnLine on the host map, e.g. "Spawn 1".</summary>
        [JsonProperty("line")] public string Line { get; set; }
        /// <summary>Gold coins dropped by the whole group, spread over its enemies.</summary>
        [JsonProperty("gold")] public int Gold { get; set; }
        [JsonProperty("elite")] public bool Elite { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
}
