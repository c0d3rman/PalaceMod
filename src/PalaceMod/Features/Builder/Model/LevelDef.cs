using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
    Level overlays (stage 2, owned by WP4b). Data shape is fixed here so packs written now stay valid; nothing
    applies these yet except LevelDef.WaveSet, which a session can use to pick the level's waves.
    Positions are world coordinates on the host map ([x, y, z]); rotY is degrees around the vertical axis.
*/
namespace PalaceMod.Builder.Model
{
    public sealed class LevelDef
    {
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        /// <summary>Scene name of the host map.</summary>
        [JsonProperty("host")] public string Host { get; set; }
        /// <summary>Id of a wave set in this pack; null plays the host's vanilla waves.</summary>
        [JsonProperty("waveSet")] public string WaveSet { get; set; }
        [JsonProperty("buildSlots")] public BuildSlotOverlay BuildSlots { get; set; }
        [JsonProperty("spawnLines")] public SpawnLineOverlay SpawnLines { get; set; }
        /// <summary>Prop placements; schema owned by WP4b (kept as raw JSON until then).</summary>
        [JsonProperty("props")] public List<JObject> Props { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class BuildSlotOverlay
    {
        /// <summary>Names of BuildSlot GameObjects on the host to remove.</summary>
        [JsonProperty("remove")] public List<string> Remove { get; set; }
        [JsonProperty("edit")] public List<BuildSlotEdit> Edit { get; set; }
        [JsonProperty("add")] public List<BuildSlotAdd> Add { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class BuildSlotEdit
    {
        /// <summary>Name of the BuildSlot GameObject on the host.</summary>
        [JsonProperty("ref")] public string Ref { get; set; }
        /// <summary>Per upgrade level (index 0 = build), the fields to change; null entries keep that level.</summary>
        [JsonProperty("upgrades")] public List<UpgradeEdit> Upgrades { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class UpgradeEdit
    {
        [JsonProperty("cost")] public int? Cost { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class BuildSlotAdd
    {
        /// <summary>Unique within the level; seeds the slot's deterministic save GUID.</summary>
        [JsonProperty("id")] public string Id { get; set; }
        /// <summary>"scene:&lt;BuildSlot name on host&gt;" to clone.</summary>
        [JsonProperty("base")] public string Base { get; set; }
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float RotY { get; set; }
        /// <summary>BuildSlot that unlocks this one (name on host or an added slot id).</summary>
        [JsonProperty("activator")] public string Activator { get; set; }
        [JsonProperty("activatorLevel")] public int ActivatorLevel { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class SpawnLineOverlay
    {
        [JsonProperty("add")] public List<SpawnLineAdd> Add { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class SpawnLineAdd
    {
        /// <summary>Name of the new line; wave spawns refer to it in "line".</summary>
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float RotY { get; set; }
        [JsonProperty("width")] public float Width { get; set; } = 10f;
        [JsonProperty("ground")] public bool Ground { get; set; } = true;
        [JsonProperty("large")] public bool Large { get; set; } = true;
        [JsonProperty("flying")] public bool Flying { get; set; } = true;
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
}
