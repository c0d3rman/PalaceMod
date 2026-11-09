using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PalaceMod.Builder.Model
{
    public static class UnitSide
    {
        public const string Enemy = "enemy";
        public const string Player = "player";
    }

    /// <summary>
    /// A custom unit or enemy: a vanilla prefab ("base") plus overrides. Every override is optional; null keeps the
    /// base prefab's value. Stats are absolute values (the night's difficulty, elite status and perks still scale
    /// them on spawn, exactly as for vanilla enemies).
    /// </summary>
    public sealed class UnitDef
    {
        /// <summary>Unique within the pack: lowercase letters, digits, '_' and '-'.</summary>
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
        /// <summary>"enemy" or "player". Only enemies can be spawned by wave sets.</summary>
        [JsonProperty("side", Order = -9)] public string Side { get; set; } = UnitSide.Enemy;
        /// <summary>Base prefab, "vanilla:&lt;catalog id&gt;", e.g. "vanilla:E Racer". `builder dump` lists the ids.</summary>
        [JsonProperty("base", Order = -8)] public string Base { get; set; }

        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }

        [JsonProperty("visuals")] public VisualsDef Visuals { get; set; }
        [JsonProperty("stats")] public StatsDef Stats { get; set; }
        [JsonProperty("weapon")] public WeaponDef Weapon { get; set; }
        [JsonProperty("tags")] public TagEditDef Tags { get; set; }
        /// <summary>Whitelisted behaviour modules, applied in order (see BehaviourTypes).</summary>
        [JsonProperty("behaviours")] public List<BehaviourDef> Behaviours { get; set; }

        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class VisualsDef
    {
        /// <summary>Night-preview / tooltip icon, "asset:&lt;key&gt;". Resolved by IAssetProvider; missing = base icon.</summary>
        [JsonProperty("icon")] public string Icon { get; set; }
        /// <summary>Multiplies the unit's body colour, "#rrggbb": a subtle shift (the shade keeps the scheme's colour).</summary>
        [JsonProperty("tint")] public string Tint { get; set; }
        /// <summary>Replaces colours outright, by part: "body" (the unit's body material: the enemy or troop colour)
        /// or a material name as the Enemy Studio lists them (e.g. "Metal"), each "#rrggbb". The lit colour becomes
        /// this and the shade follows it the way the game's own scheme pairs them. Elites keep the colour (with the
        /// elite outline).</summary>
        [JsonProperty("colors")] public Dictionary<string, string> Colors { get; set; }
        /// <summary>Uniform scale factor relative to the base prefab (1 = unchanged). Visual only.</summary>
        [JsonProperty("scale")] public float? Scale { get; set; }
        /// <summary>Replacement body model (see ModelRef): "asset:&lt;key&gt;" of an imported OBJ/glTF file,
        /// "mesh:&lt;name&gt;[@Map]" a scenery mesh (the cross-map library or the map's own), or "vanilla:&lt;unit id&gt;"
        /// another unit's model. It's scaled to fit the base unit's size and stands where the base stood.</summary>
        [JsonProperty("mesh")] public string Mesh { get; set; }
        /// <summary>Scale for the model in its own units instead of fitting it to the base's height.</summary>
        [JsonProperty("meshScale")] public float? MeshScale { get; set; }
        /// <summary>Turns the model around the vertical axis, in degrees (to face it forward).</summary>
        [JsonProperty("meshRotY")] public float? MeshRotY { get; set; }
        /// <summary>Tilts the model around X (forward/back), in degrees.</summary>
        [JsonProperty("meshRotX")] public float? MeshRotX { get; set; }
        /// <summary>Tilts the model around Z (sideways), in degrees.</summary>
        [JsonProperty("meshRotZ")] public float? MeshRotZ { get; set; }
        /// <summary>Moves the model after the fit, in metres: [x, y, z] (x right, y up, z forward).</summary>
        [JsonProperty("meshOffset")] public float[] MeshOffset { get; set; }
        /// <summary>"own": the source's own materials; "unit": the unit's body material (takes the tint and the elite
        /// look). Null: own for mesh:/vanilla: models, unit for imported ones.</summary>
        [JsonProperty("meshLook")] public string MeshLook { get; set; }
        /// <summary>"auto", "bob" or "none": a procedural walk (hop and waddle while moving) for a body the base's
        /// animation doesn't move. Null = auto.</summary>
        [JsonProperty("motion")] public string Motion { get; set; }
        /// <summary>Skin texture, "asset:&lt;key&gt;" of a PNG, applied to the body material (uses the mesh's UVs;
        /// meant for imported meshes, since vanilla bodies are flat-coloured).</summary>
        [JsonProperty("texture")] public string Texture { get; set; }
        /// <summary>Replacement sounds; each missing one keeps the base unit's sound.</summary>
        [JsonProperty("sounds")] public SoundsDef Sounds { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public enum ModelKind { Invalid, Asset, Scenery, Unit }

    /// <summary>What a visuals.mesh reference names (see VisualsDef.Mesh), and the choices of meshLook and motion.</summary>
    public static class ModelRef
    {
        public const string LookOwn = "own", LookUnit = "unit";
        public const string MotionAuto = "auto", MotionBob = "bob", MotionNone = "none";
        public static readonly string[] Looks = [LookOwn, LookUnit];
        public static readonly string[] Motions = [MotionAuto, MotionBob, MotionNone];

        public static ModelKind Kind(string reference, out string id)
        {
            id = null;
            if (string.IsNullOrWhiteSpace(reference)) return ModelKind.Invalid;
            string r = reference.Trim();
            int colon = r.IndexOf(':');
            if (colon <= 0 || colon == r.Length - 1) return ModelKind.Invalid;
            string rest = r.Substring(colon + 1).Trim();
            if (rest.Length == 0) return ModelKind.Invalid;
            var kind = r.Substring(0, colon) switch
            {
                "asset" => ModelKind.Asset,
                "mesh" => ModelKind.Scenery,
                "vanilla" => ModelKind.Unit,
                _ => ModelKind.Invalid,
            };
            if (kind != ModelKind.Invalid) id = rest;
            return kind;
        }

        public static ModelKind Kind(string reference) => Kind(reference, out _);

        /// <summary>The effective material choice: "own" or "unit".</summary>
        public static string Look(VisualsDef v) =>
            v?.MeshLook == LookOwn || v?.MeshLook == LookUnit ? v.MeshLook : Kind(v?.Mesh) == ModelKind.Asset ? LookUnit : LookOwn;

        public static string Scenery(string name) => "mesh:" + name;

        /// <summary>The colors key for the unit's body material.</summary>
        public const string BodyPart = "body";
        public static string Unit(string id) => "vanilla:" + id;
    }

    /// <summary>Unit sounds, each "asset:&lt;key&gt;" of a WAV or OGG file (WP5).</summary>
    public sealed class SoundsDef
    {
        [JsonProperty("attack")] public string Attack { get; set; }
        /// <summary>Played when the unit takes damage.</summary>
        [JsonProperty("hurt")] public string Hurt { get; set; }
        [JsonProperty("death")] public string Death { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class StatsDef
    {
        [JsonProperty("maxHp")] public float? MaxHp { get; set; }
        /// <summary>Movement speed in the game's units per second (vanilla foot soldiers are about 4 to 6).</summary>
        [JsonProperty("moveSpeed")] public float? MoveSpeed { get; set; }
        /// <summary>Seconds between attacks, for every attack the unit has.</summary>
        [JsonProperty("attackCooldown")] public float? AttackCooldown { get; set; }
        /// <summary>Targeting range of every attack priority.</summary>
        [JsonProperty("attackRange")] public float? AttackRange { get; set; }
        /// <summary>Distance a ranged enemy keeps from its target.</summary>
        [JsonProperty("keepDistance")] public float? KeepDistance { get; set; }
        /// <summary>Seconds an enemy chases the player after being hit by them.</summary>
        [JsonProperty("aggroTime")] public float? AggroTime { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>
    /// The unit's weapon. The base weapon (a shared ScriptableObject) is cloned per design before editing, so vanilla
    /// units are never affected.
    /// </summary>
    public sealed class WeaponDef
    {
        /// <summary>"inherit" (the base prefab's weapon) or "vanilla:&lt;weapon name&gt;" from the catalog.</summary>
        [JsonProperty("base")] public string Base { get; set; } = "inherit";
        /// <summary>Replaces the direct-damage modifier list when set. Applied in order, like the game does.</summary>
        [JsonProperty("damage")] public List<DamageModDef> Damage { get; set; }
        /// <summary>Replaces the splash-damage modifier list when set.</summary>
        [JsonProperty("splash")] public List<DamageModDef> Splash { get; set; }
        /// <summary>Multiplies all damage the unit deals (stacks with the game's own multipliers).</summary>
        [JsonProperty("damageMult")] public float? DamageMult { get; set; }
        [JsonProperty("projectileSpeed")] public float? ProjectileSpeed { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>One damage modifier: if the target has all <see cref="Tags"/>, damage = (damage + Add) * Mult.</summary>
    public sealed class DamageModDef
    {
        /// <summary>TagManager.ETag names the target must all have; empty = any target.</summary>
        [JsonProperty("tags")] public List<string> Tags { get; set; } = [];
        [JsonProperty("add")] public float Add { get; set; }
        [JsonProperty("mult")] public float Mult { get; set; } = 1f;
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>TagManager.ETag names to add to or remove from the base prefab's tags.</summary>
    public sealed class TagEditDef
    {
        [JsonProperty("add")] public List<string> Add { get; set; } = [];
        [JsonProperty("remove")] public List<string> Remove { get; set; } = [];
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A behaviour module: a type from BehaviourTypes plus its parameters (other JSON fields).</summary>
    public sealed class BehaviourDef
    {
        [JsonProperty("type", Order = -10)] public string Type { get; set; }
        /// <summary>The module's parameters, e.g. {"unit": "vanilla:E Slime"} for SpawnOnDeath.</summary>
        [JsonExtensionData] public IDictionary<string, JToken> Params { get; set; } = new Dictionary<string, JToken>();

        public string GetString(string key) => Params != null && Params.TryGetValue(key, out var v) && v.Type == JTokenType.String ? (string)v : null;
        public float? GetFloat(string key) => Params != null && Params.TryGetValue(key, out var v) && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (float)v : null;
    }

    /// <summary>Behaviour module types the runtime understands. Anything else is a validation error.</summary>
    public static class BehaviourTypes
    {
        /// <summary>{"unit": unit ref} - spawns that unit where this one dies (like the game's slimes).</summary>
        public const string SpawnOnDeath = "SpawnOnDeath";

        public static readonly string[] All = [SpawnOnDeath];
    }
}
