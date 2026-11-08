using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
    A level's terrain (docs/builder-ux.md, "Terrain"): a grid of square cells in world space, embedded in the level
    compressed (Level/GroundCodec). On a "flat" base it replaces the host's ground with a generated, sculptable one; on
    any base its blocked cells are walls for ground units and the king. Every array field is optional: absent means
    "all default" (flat at y, plain ground, nothing blocked, no water).
*/
namespace PalaceMod.Builder.Model
{
    public sealed class TerrainDef
    {
        /// <summary>World [x, z] of the grid's corner (cell 0, 0).</summary>
        [JsonProperty("origin")] public float[] Origin { get; set; }
        /// <summary>Cell edge in metres.</summary>
        [JsonProperty("cell")] public float Cell { get; set; } = 2f;
        /// <summary>[columns, rows] of cells.</summary>
        [JsonProperty("size")] public int[] Size { get; set; }
        /// <summary>World height of the flat ground (heights are relative to it).</summary>
        [JsonProperty("y")] public float Y { get; set; }
        /// <summary>(w+1)*(h+1) corner heights in cm relative to y, row-major: base64 deflate of int16 row deltas.</summary>
        [JsonProperty("heights")] public string Heights { get; set; }
        /// <summary>A material per cell (GroundMaterial), base64 deflate.</summary>
        [JsonProperty("materials")] public string Materials { get; set; }
        /// <summary>Cells ground units and the king can't enter (fliers can): base64 deflate bitmask.</summary>
        [JsonProperty("blocked")] public string Blocked { get; set; }
        [JsonProperty("water")] public WaterDef Water { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class WaterDef
    {
        /// <summary>Water surface height relative to the terrain's y.</summary>
        [JsonProperty("level")] public float Level { get; set; } = -1f;
        /// <summary>Water cells (unwalkable), base64 deflate bitmask.</summary>
        [JsonProperty("cells")] public string Cells { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
}
