using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
    The Builder's design format (see docs/builder-design.md, "Data model"). Everything a player makes is a DesignPack:
    a JSON document of units, wave sets and (later) level overlays. These classes are plain data with no Unity
    references, so the editor, the share codec and tests can use them without a running game.

    Rules every class here follows:
    - Optional values are nullable: null means "keep what the base has", never "zero".
    - Unknown JSON fields land in Extra and are written back unchanged, so a newer pack survives an older editor.
    - A schema change bumps DesignPack.CurrentVersion and adds a step to Migrations.
*/
namespace PalaceMod.Builder.Model
{
    public sealed class DesignPack
    {
        public const string FormatName = "palace-builder";
        public const int CurrentVersion = 1;

        [JsonProperty("format", Order = -10)] public string Format { get; set; } = FormatName;
        [JsonProperty("version", Order = -9)] public int Version { get; set; } = CurrentVersion;

        /// <summary>Globally unique, lowercase "author.name" style id; also the pack's folder name.</summary>
        [JsonProperty("id", Order = -8)] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("author")] public string Author { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        /// <summary>Game version the pack was made with (Application.version); informational.</summary>
        [JsonProperty("gameVersion")] public string GameVersion { get; set; }
        [JsonProperty("requires")] public Requirements Requires { get; set; }

        /// <summary>Files shipped with or referenced by the pack, by key; designs refer to them as "asset:&lt;key&gt;".</summary>
        [JsonProperty("assets")] public Dictionary<string, AssetDef> Assets { get; set; } = [];
        [JsonProperty("units")] public List<UnitDef> Units { get; set; } = [];
        [JsonProperty("waveSets")] public List<WaveSetDef> WaveSets { get; set; } = [];
        [JsonProperty("levels")] public List<LevelDef> Levels { get; set; } = [];

        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        public UnitDef FindUnit(string id) => Units?.Find(u => u.Id == id);
        public WaveSetDef FindWaveSet(string id) => WaveSets?.Find(w => w.Id == id);
        public LevelDef FindLevel(string id) => Levels?.Find(l => l.Id == id);
    }

    public sealed class Requirements
    {
        /// <summary>Minimum PalaceMod version, e.g. "&gt;=1.0".</summary>
        [JsonProperty("palaceMod")] public string PalaceMod { get; set; }
        /// <summary>Other packs this one references units from (by pack id).</summary>
        [JsonProperty("packs")] public List<string> Packs { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A file a pack uses. Local packs set Path; shared designs set Url (fetched and cached by sha256).</summary>
    public sealed class AssetDef
    {
        /// <summary>Path relative to the pack folder, e.g. "assets/goblin.png".</summary>
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        /// <summary>Lowercase hex SHA-256 of the file; required with Url.</summary>
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
}
