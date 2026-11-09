using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model.Logic;

/*
    Level overlays (stage 2, WP4b/c). Features/Builder/Level/LevelOverlay applies them when a session loads the host
    (after Awake, before Start) and then rescans pathfinding once; the layout editor (Features/Builder/Level) writes
    them. Positions are world coordinates on the host map ([x, y, z]); rotY is the world yaw in degrees.
    Requirements (version 2): every slot has at most one prerequisite, the game's own BuildSlot.activatorBuilding /
    activatorLevel; "needs castle level N" is the castle slot at level N-1. See docs/builder-ux.md, "Requirements".
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
        /// <summary>null plays the host as it is; "cleared" keeps its ground and castle and removes every other build
        /// slot and the scenery; "flat" also replaces the ground with a generated field (LevelBases).</summary>
        [JsonProperty("base")] public string Base { get; set; }
        [JsonProperty("buildSlots")] public BuildSlotOverlay BuildSlots { get; set; }
        [JsonProperty("spawnLines")] public SpawnLineOverlay SpawnLines { get; set; }
        /// <summary>Decoration placed on the map (copies of the host's own scenery meshes).</summary>
        [JsonProperty("props")] public List<PropDef> Props { get; set; }
        /// <summary>Generated or edited ground (Model/TerrainDef.cs); null keeps the host's.</summary>
        [JsonProperty("terrain")] public TerrainDef Terrain { get; set; }
        /// <summary>A main map whose colour scheme the whole level uses (ground, scenery, buildings, light);
        /// null keeps the host's.</summary>
        [JsonProperty("colors")] public string Colors { get; set; }
        /// <summary>Editor groups (Studio metadata, ignored when playing).</summary>
        [JsonProperty("groups")] public List<GroupDef> Groups { get; set; }
        /// <summary>The behaviour engine: level script, variables, placed entities, attached rules, HUD, king actions
        /// (Model/Logic/LogicDefs.cs, docs/builder-engine.md).</summary>
        [JsonProperty("logic")] public LogicDef Logic { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>The maps whose colour schemes a level or prop can take ("colors").</summary>
    public static class LevelColors
    {
        public static readonly string[] Maps = ["Neuland", "Nordfels", "Durststein", "Frostsee", "Uferwind", "Sturmklamm", "Wildbach", "Moorweg", "Freifort", "Totend"];

        /// <summary>A display name from Maps, or a scene name such as "Neuland(Tutorial)".</summary>
        public static bool IsMap(string map) => map != null && System.Array.IndexOf(Maps, map.Split('(')[0]) >= 0;
    }

    /// <summary>A named set of a level's objects the Studio selects together. Members are Studio item keys
    /// ("slot:&lt;name&gt;", "add:&lt;id&gt;", "prop:&lt;id&gt;", "line:&lt;name&gt;").</summary>
    public sealed class GroupDef
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("members")] public List<string> Members { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class BuildSlotOverlay
    {
        /// <summary>Names of BuildSlot GameObjects on the host to remove (slots they unlock never appear).</summary>
        [JsonProperty("remove")] public List<string> Remove { get; set; }
        [JsonProperty("edit")] public List<BuildSlotEdit> Edit { get; set; }
        [JsonProperty("add")] public List<BuildSlotAdd> Add { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class BuildSlotEdit
    {
        /// <summary>Name of the BuildSlot GameObject on the host.</summary>
        [JsonProperty("ref")] public string Ref { get; set; }
        /// <summary>New position; null keeps the host's.</summary>
        [JsonProperty("pos")] public float[] Pos { get; set; }
        /// <summary>New yaw in degrees; null keeps the host's.</summary>
        [JsonProperty("rotY")] public float? RotY { get; set; }
        /// <summary>What unlocks the slot; null keeps the host's (or, for an added slot, its base's).</summary>
        [JsonProperty("requires")] public SlotRequirement Requires { get; set; }
        /// <summary>BuildSlot.requiredRootLevelDifference: 0 = never above the castle's level, n = up to n above;
        /// -100 = no cap; null keeps the host's (or the base's).</summary>
        [JsonProperty("upgradeCap")] public int? UpgradeCap { get; set; }
        /// <summary>Per upgrade level (index 0 = build), the fields to change; null entries keep that level.</summary>
        [JsonProperty("upgrades")] public List<UpgradeEdit> Upgrades { get; set; }
        /// <summary>Where the units of a training building (barracks, archery range...) stand: one [x, z] per unit
        /// spot (the places the map's units stand, shared by every upgrade choice's units; the reference's "units"), metres from the slot's centre in the slot's frame (x right, z forward; turns with the
        /// slot). Null keeps the map's; null or missing entries keep the map's too. In play every unit home is also moved onto
        /// walkable ground.</summary>
        [JsonProperty("units")] public List<float[]> Units { get; set; }
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
        /// <summary>What unlocks the slot; null keeps the host's (or, for an added slot, its base's).</summary>
        [JsonProperty("requires")] public SlotRequirement Requires { get; set; }
        /// <summary>BuildSlot.requiredRootLevelDifference: 0 = never above the castle's level, n = up to n above;
        /// -100 = no cap; null keeps the host's (or the base's).</summary>
        [JsonProperty("upgradeCap")] public int? UpgradeCap { get; set; }
        /// <summary>Per upgrade level (index 0 = build), changes against the base's; null entries keep that level.</summary>
        [JsonProperty("upgrades")] public List<UpgradeEdit> Upgrades { get; set; }
        /// <summary>Where the units of a training building (barracks, archery range...) stand: one [x, z] per unit
        /// spot (the places the map's units stand, shared by every upgrade choice's units; the reference's "units"), metres from the slot's centre in the slot's frame (x right, z forward; turns with the
        /// slot). Null keeps the map's; null or missing entries keep the map's too. In play every unit home is also moved onto
        /// walkable ground.</summary>
        [JsonProperty("units")] public List<float[]> Units { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A slot's one prerequisite (BuildSlot.activatorBuilding + activatorLevel).</summary>
    public sealed class SlotRequirement
    {
        /// <summary>"castle", the name of a build slot on the host, or "add:&lt;id&gt;" for an added slot; null = available
        /// from the start.</summary>
        [JsonProperty("slot")] public string Slot { get; set; }
        /// <summary>The prerequisite's level after which this slot appears: 0 = once it's built, n = once it reaches
        /// level n+1. "Castle level N" is level N-1.</summary>
        [JsonProperty("level")] public int Level { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        public SlotRequirement Copy() => new() { Slot = Slot, Level = Level };
    }

    public sealed class SpawnLineOverlay
    {
        /// <summary>New spawn lines: copies of one of the host's lines, under its enemy spawner.</summary>
        [JsonProperty("add")] public List<SpawnLineAdd> Add { get; set; }
        /// <summary>Moves the host's own spawn lines.</summary>
        [JsonProperty("edit")] public List<SpawnLineEdit> Edit { get; set; }
        /// <summary>Names of the host's spawn lines to take off the map (no enemies, routes or markers); a wave that
        /// spawns from one is an error.</summary>
        [JsonProperty("remove")] public List<string> Remove { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class SpawnLineEdit
    {
        /// <summary>Name of the EnemySpawnLine GameObject on the host (what spawns' "line" refers to).</summary>
        [JsonProperty("ref")] public string Ref { get; set; }
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float? RotY { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class SpawnLineAdd
    {
        /// <summary>Name of the new line; wave spawns refer to it in "line".</summary>
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float RotY { get; set; }
        /// <summary>Length of the line in metres (enemies appear anywhere along it).</summary>
        [JsonProperty("width")] public float Width { get; set; } = 10f;
        [JsonProperty("ground")] public bool Ground { get; set; } = true;
        [JsonProperty("large")] public bool Large { get; set; } = true;
        [JsonProperty("flying")] public bool Flying { get; set; } = true;
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class PropDef
    {
        /// <summary>Unique within the level.</summary>
        [JsonProperty("id")] public string Id { get; set; }
        /// <summary>"mesh:&lt;mesh name&gt;[@&lt;Map&gt;]": a scenery mesh of the host map (drawn with that object's
        /// materials) or, if the host has none by that name, of the cross-map scenery library (drawn with the host's
        /// material of the same name). "@Map" picks one map's mesh should two maps ever have one by the same name.</summary>
        [JsonProperty("base")] public string Base { get; set; }
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float RotY { get; set; }
        /// <summary>Tilt in degrees about the prop's own sideways axis (+ tips its front down; 90 lays an upright
        /// thing on its back) and about its own forward axis (roll). Applied before rotY, so rotY stays the heading:
        /// the rotation is Euler(rotX, rotY, rotZ). Left out when 0 (files from before tilting have neither).</summary>
        [JsonProperty("rotX", DefaultValueHandling = DefaultValueHandling.Ignore)] public float RotX { get; set; }
        [JsonProperty("rotZ", DefaultValueHandling = DefaultValueHandling.Ignore)] public float RotZ { get; set; }
        [JsonProperty("scale")] public float Scale { get; set; } = 1f;
        /// <summary>Whether units walk around it (it's added to the pathfinding graphs at level start).</summary>
        [JsonProperty("solid")] public bool Solid { get; set; } = true;
        /// <summary>A main map whose colour scheme the prop keeps ("Frostsee"), e.g. the map a library prop comes
        /// from; null takes the level's colours like the map's own scenery.</summary>
        [JsonProperty("colors")] public string Colors { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
}
