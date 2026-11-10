using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
    The behaviour engine's data (docs/builder-engine.md, "Data model"). Plain data, no Unity, same rules as the rest of
    the pack format: null keeps the default, unknown fields land in Extra and are written back. Action and event
    parameters stay JSON (ActionDef.Params, WhenDef.Params) and are typed by LogicSchema, so a new action needs no new
    class here.
*/
namespace PalaceMod.Builder.Model.Logic
{
    /// <summary>A number field that may instead be an expression ("=100 * night"), evaluated at spawn.</summary>
    [JsonConverter(typeof(LNumConverter))]
    public sealed class LNum
    {
        public double? Value;
        /// <summary>The expression without its leading '='.</summary>
        public string Expr;

        public static implicit operator LNum(double v) => new() { Value = v };
        public bool IsExpr => Expr != null;
        public override string ToString() => IsExpr ? "=" + Expr : Value?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    internal sealed class LNumConverter : JsonConverter<LNum>
    {
        public override LNum ReadJson(JsonReader reader, Type objectType, LNum existing, bool hasExisting, JsonSerializer serializer)
        {
            var t = JToken.Load(reader);
            switch (t.Type)
            {
                case JTokenType.Null: return null;
                case JTokenType.Integer:
                case JTokenType.Float: return new LNum { Value = (double)t };
                case JTokenType.String:
                    string s = ((string)t).Trim();
                    if (s.StartsWith("=")) return new LNum { Expr = s.Substring(1) };
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return new LNum { Value = v };
                    return new LNum { Expr = s };
                default: throw new JsonSerializationException($"expected a number or an \"=expression\", got {t.Type}");
            }
        }

        public override void WriteJson(JsonWriter writer, LNum value, JsonSerializer serializer)
        {
            if (value == null) writer.WriteNull();
            else if (value.IsExpr) writer.WriteValue("=" + value.Expr);
            else if (value.Value is double d && d == Math.Floor(d) && Math.Abs(d) < 1e15) writer.WriteValue((long)d);
            else writer.WriteValue(value.Value);
        }
    }

    // ---- Level logic ------------------------------------------------------------------------------------------

    /// <summary>LevelDef.logic: the level script, placed entities and the level's variables.</summary>
    public sealed class LogicDef
    {
        /// <summary>Seed of the logic random source; null = a hash of the level id.</summary>
        [JsonProperty("seed")] public int? Seed { get; set; }
        /// <summary>Level variables and their start values: a number, bool, "'text'", "vec(x, z)", [] (a list) or
        /// {"type": "entity" | "list" | ..., "value": ...}.</summary>
        [JsonProperty("vars")] public Dictionary<string, JToken> Vars { get; set; }
        /// <summary>The level script (self is the level).</summary>
        [JsonProperty("rules")] public List<RuleDef> Rules { get; set; }
        [JsonProperty("places")] public List<PlaceDef> Places { get; set; }
        /// <summary>Rules, names, labels and vars on objects the level already has, by Studio item key
        /// ("slot:&lt;name&gt;", "add:&lt;id&gt;", "prop:&lt;id&gt;", "line:&lt;name&gt;", "castle").</summary>
        [JsonProperty("attach")] public Dictionary<string, AttachDef> Attach { get; set; }
        [JsonProperty("hud")] public List<HudDef> Hud { get; set; }
        [JsonProperty("kingActions")] public List<KingActionDef> KingActions { get; set; }
        /// <summary>Raises (or lowers) the engine's caps for this level; null keeps the defaults.</summary>
        [JsonProperty("caps")] public LogicCapsDef Caps { get; set; }
        /// <summary>The game's own rules for this level (win, lose, the castle, night flow, when fighting and building
        /// happen); null keeps vanilla. LogicSchema.GameRules lists them.</summary>
        [JsonProperty("game")] public GameRulesDef Game { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>
    /// logic.game: each field is a literal or an expression the runtime re-evaluates while the level runs, so a rule
    /// can switch it through a level variable. Enum fields take the word or "=expr"; bool and number fields a literal
    /// or an expression string.
    /// </summary>
    public sealed class GameRulesDef
    {
        [JsonProperty("victory")] public JToken Victory { get; set; }
        [JsonProperty("defeat")] public JToken Defeat { get; set; }
        [JsonProperty("castle")] public JToken Castle { get; set; }
        [JsonProperty("holdNight")] public JToken HoldNight { get; set; }
        [JsonProperty("endNight")] public JToken EndNight { get; set; }
        [JsonProperty("survivors")] public JToken Survivors { get; set; }
        [JsonProperty("combat")] public JToken Combat { get; set; }
        [JsonProperty("build")] public JToken Build { get; set; }
        [JsonProperty("dayLength")] public JToken DayLength { get; set; }
        [JsonProperty("spawnedDead")] public JToken SpawnedDead { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>Per-level caps. Null fields keep the default; values above the default draw a "may lag" warning.</summary>
    public sealed class LogicCapsDef
    {
        [JsonProperty("entities")] public int? Entities { get; set; }
        [JsonProperty("units")] public int? Units { get; set; }
        [JsonProperty("projectiles")] public int? Projectiles { get; set; }
        /// <summary>Default maxAlive of a template that doesn't give one.</summary>
        [JsonProperty("perTemplate")] public int? PerTemplate { get; set; }
        [JsonProperty("firingsPerFrame")] public int? FiringsPerFrame { get; set; }
        [JsonProperty("actionsPerFrame")] public int? ActionsPerFrame { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        public static readonly LogicCapsDef Defaults = new() { Entities = 300, Units = 200, Projectiles = 400, PerTemplate = 100, FiringsPerFrame = 400, ActionsPerFrame = 4000 };
        public static readonly LogicCapsDef Max = new() { Entities = 2000, Units = 1000, Projectiles = 2000, PerTemplate = 1000, FiringsPerFrame = 4000, ActionsPerFrame = 40000 };

        /// <summary>(json name, value, default, max) for each cap.</summary>
        public IEnumerable<(string Name, int? Value, int Default, int Max)> Fields()
        {
            yield return ("entities", Entities, Defaults.Entities.Value, Max.Entities.Value);
            yield return ("units", Units, Defaults.Units.Value, Max.Units.Value);
            yield return ("projectiles", Projectiles, Defaults.Projectiles.Value, Max.Projectiles.Value);
            yield return ("perTemplate", PerTemplate, Defaults.PerTemplate.Value, Max.PerTemplate.Value);
            yield return ("firingsPerFrame", FiringsPerFrame, Defaults.FiringsPerFrame.Value, Max.FiringsPerFrame.Value);
            yield return ("actionsPerFrame", ActionsPerFrame, Defaults.ActionsPerFrame.Value, Max.ActionsPerFrame.Value);
        }

        /// <summary>The effective value of a cap: the level's, clamped to 1..Max, or the default.</summary>
        public static int Effective(LogicCapsDef caps, string name)
        {
            var f = (caps ?? new LogicCapsDef()).Fields().First(x => x.Name == name);
            return f.Value is int v ? Math.Max(1, Math.Min(f.Max, v)) : f.Default;
        }
    }

    /// <summary>Logic on an object the level already has (a build slot, prop, spawn line or the castle).</summary>
    public sealed class AttachDef
    {
        /// <summary>The object's name in expressions (@name).</summary>
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("labels")] public List<string> Labels { get; set; }
        [JsonProperty("vars")] public Dictionary<string, JToken> Vars { get; set; }
        [JsonProperty("statuses")] public List<string> Statuses { get; set; }
        [JsonProperty("targeting")] public List<TargetingDef> Targeting { get; set; }
        [JsonProperty("rules")] public List<RuleDef> Rules { get; set; }
        /// <summary>A light on the object (a lantern on a tower, a fire on the castle).</summary>
        [JsonProperty("light")] public LightDef Light { get; set; }
        /// <summary>The king can use it with the interact key (a prop, a slot that has no build prompt of its own).</summary>
        [JsonProperty("interact")] public InteractDef Interact { get; set; }
        /// <summary>A build slot's item costs: level variables paid on its own build prompt with the gold,
        /// [{ "var": "$wood", "amount": 2, "label": "wood", "level": 1 }] (level: 1 = the build, 2 = the first upgrade...;
        /// none = every level).</summary>
        [JsonProperty("pay")] public List<PayDef> Pay { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class HudDef
    {
        [JsonProperty("id")] public string Id { get; set; }
        /// <summary>Text with {expr} parts, e.g. "Souls {$souls}/10".</summary>
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        /// <summary>A bool expression; null = always shown.</summary>
        [JsonProperty("visible")] public string Visible { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class KingActionDef
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        /// <summary>"day" | "night" | "always" (default).</summary>
        [JsonProperty("when")] public string When { get; set; }
        [JsonProperty("cooldown")] public double? Cooldown { get; set; }
        [JsonProperty("uses")] public UsesDef Uses { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class UsesDef
    {
        [JsonProperty("count")] public int Count { get; set; } = 1;
        /// <summary>"day" | "night" | "run".</summary>
        [JsonProperty("per")] public string Per { get; set; } = "run";
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    // ---- Rules ------------------------------------------------------------------------------------------------

    /// <summary>When an event happens, if conditions hold, run actions. Or a preset use: {"preset": id, "with": {...}}.</summary>
    public sealed class RuleDef
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("when")] public WhenDef When { get; set; }
        /// <summary>A bool expression, or a list of them (all must hold).</summary>
        [JsonProperty("if")] public JToken If { get; set; }
        [JsonProperty("do")] public List<ActionDef> Do { get; set; }
        [JsonProperty("enabled")] public bool? Enabled { get; set; }
        [JsonProperty("once")] public bool? Once { get; set; }
        [JsonProperty("cooldown")] public double? Cooldown { get; set; }
        [JsonProperty("note")] public string Note { get; set; }
        /// <summary>A preset to expand here instead of a rule (its rules, vars and components are added).</summary>
        [JsonProperty("preset")] public string Preset { get; set; }
        [JsonProperty("with")] public JObject With { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        [JsonIgnore] public bool IsPresetUse => Preset != null;

        /// <summary>The conditions as a list of expression strings.</summary>
        public IEnumerable<string> Conditions()
        {
            if (If == null) yield break;
            if (If.Type == JTokenType.Array) { foreach (var c in If) if (c.Type == JTokenType.String) yield return (string)c; }
            else if (If.Type == JTokenType.String) yield return (string)If;
            else if (If.Type == JTokenType.Boolean) yield return (bool)If ? "true" : "false";
        }
    }

    /// <summary>The event a rule listens to. JSON: "death" or {"on": "death", "of": "foe", "range": 10, ...params}.</summary>
    [JsonConverter(typeof(WhenConverter))]
    public sealed class WhenDef
    {
        public string On;
        /// <summary>A filter: listen to this event on other entities.</summary>
        public string Of;
        public LNum Range;
        /// <summary>The event's own params (seconds, pct, name, filter...).</summary>
        public JObject Params = new();

        public static implicit operator WhenDef(string on) => new() { On = on };
    }

    internal sealed class WhenConverter : JsonConverter<WhenDef>
    {
        public override WhenDef ReadJson(JsonReader reader, Type objectType, WhenDef existing, bool hasExisting, JsonSerializer serializer)
        {
            var t = JToken.Load(reader);
            if (t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return new WhenDef { On = (string)t };
            if (t is not JObject o) throw new JsonSerializationException("\"when\" must be an event name or {\"on\": ...}");
            var w = new WhenDef { On = (string)o["on"], Of = o["of"]?.Type == JTokenType.String ? (string)o["of"] : null };
            if (o["range"] is JToken r && r.Type != JTokenType.Null) w.Range = r.ToObject<LNum>(serializer);
            foreach (var p in o.Properties())
                if (p.Name != "on" && p.Name != "of" && p.Name != "range") w.Params[p.Name] = p.Value.DeepClone();
            return w;
        }

        public override void WriteJson(JsonWriter writer, WhenDef value, JsonSerializer serializer)
        {
            if (value == null) { writer.WriteNull(); return; }
            if (value.Of == null && value.Range == null && (value.Params == null || value.Params.Count == 0)) { writer.WriteValue(value.On); return; }
            var o = new JObject { ["on"] = value.On };
            if (value.Of != null) o["of"] = value.Of;
            if (value.Range != null) o["range"] = JToken.FromObject(value.Range, serializer);
            foreach (var p in value.Params ?? new JObject()) o[p.Key] = p.Value?.DeepClone();
            o.WriteTo(writer);
        }
    }

    /// <summary>One action: {"do": "&lt;name&gt;", ...params}. Params are typed by LogicSchema.Actions[name].</summary>
    public sealed class ActionDef
    {
        [JsonProperty("do", Order = -10)] public string Do { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Params { get; set; } = new Dictionary<string, JToken>();

        public JToken Get(string key) => Params != null && Params.TryGetValue(key, out var v) ? v : null;
        public string GetString(string key) => Get(key) is JToken t && t.Type == JTokenType.String ? (string)t : null;

        /// <summary>A nested action list param ("then", "do", "else", "onImpact"), or null.</summary>
        public List<ActionDef> Actions(string key) => Get(key) is JArray a ? a.ToObject<List<ActionDef>>() : null;
    }

    // ---- Templates --------------------------------------------------------------------------------------------

    public static class EntityKinds
    {
        public const string Prop = "prop", Projectile = "projectile", Area = "area", Pickup = "pickup", Logic = "logic";
        /// <summary>Kinds only placed inline (logic.places), never templates.</summary>
        public const string Path = "path", Point = "point";
        public static readonly string[] Templates = [Prop, Projectile, Area, Pickup, Logic];
        public static readonly string[] Places = [Prop, Projectile, Area, Pickup, Logic, Path, Point];
    }

    public static class Teams
    {
        public const string Player = "player", Enemy = "enemy", Neutral = "neutral", Owner = "owner";
        public static readonly string[] All = [Player, Enemy, Neutral, Owner];
    }

    /// <summary>The fields shared by a non-unit template (EntityDef) and a placed entity (PlaceDef).</summary>
    public abstract class TemplateFields
    {
        /// <summary>"prop" | "projectile" | "area" | "pickup" | "logic" (and, for places, "path" | "point").</summary>
        [JsonProperty("kind", Order = -9)] public string Kind { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        /// <summary>Any mesh, any unit's model or an imported asset; null for an invisible entity.</summary>
        [JsonProperty("visual")] public VisualsDef Visual { get; set; }
        [JsonProperty("body")] public BodyDef Body { get; set; }
        /// <summary>"player" | "enemy" | "neutral" | "owner" (the spawner's team).</summary>
        [JsonProperty("team")] public string Team { get; set; }
        /// <summary>A hittable enemy-team entity keeps the night open while alive.</summary>
        [JsonProperty("countsAsEnemy")] public bool? CountsAsEnemy { get; set; }
        [JsonProperty("labels")] public List<string> Labels { get; set; }
        [JsonProperty("vars")] public Dictionary<string, JToken> Vars { get; set; }
        /// <summary>Seconds; then "expire", then it's destroyed. Null = forever.</summary>
        [JsonProperty("lifetime")] public LNum Lifetime { get; set; }
        /// <summary>Cap on live copies of this template (default: the level's perTemplate cap, 100).</summary>
        [JsonProperty("maxAlive")] public int? MaxAlive { get; set; }
        [JsonProperty("projectile")] public ProjectileDef Projectile { get; set; }
        [JsonProperty("mover")] public MoverDef Mover { get; set; }
        [JsonProperty("area")] public AreaDef Area { get; set; }
        [JsonProperty("pickup")] public PickupDef Pickup { get; set; }
        [JsonProperty("interact")] public InteractDef Interact { get; set; }
        /// <summary>A light that follows it (docs/builder-engine.md, "Light").</summary>
        [JsonProperty("light")] public LightDef Light { get; set; }
        /// <summary>Entities spawned and linked to it when it appears (Runtime/Links.cs).</summary>
        [JsonProperty("parts")] public List<PartDef> Parts { get; set; }
        [JsonProperty("attacks")] public List<AttackDef> Attacks { get; set; }
        [JsonProperty("targeting")] public List<TargetingDef> Targeting { get; set; }
        [JsonProperty("movement")] public MovementDef Movement { get; set; }
        /// <summary>Statuses it starts with.</summary>
        [JsonProperty("statuses")] public List<string> Statuses { get; set; }
        [JsonProperty("rules")] public List<RuleDef> Rules { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A non-unit template (pack "entities").</summary>
    public sealed class EntityDef : TemplateFields
    {
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
    }

    /// <summary>A placed entity (logic.places): a template by id, a preset, or inline fields (which override the
    /// template's for this copy).</summary>
    public sealed class PlaceDef : TemplateFields
    {
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
        /// <summary>A template id (pack entities or units).</summary>
        [JsonProperty("entity")] public string Entity { get; set; }
        /// <summary>A preset to build this place from (the logic objects: Timer, Lever, ...).</summary>
        [JsonProperty("preset")] public string Preset { get; set; }
        [JsonProperty("with")] public JObject With { get; set; }
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float? RotY { get; set; }
        [JsonProperty("scale")] public float? Scale { get; set; }
        /// <summary>false: placed switched off until something sends it "enable".</summary>
        [JsonProperty("active")] public bool? Active { get; set; }
        /// <summary>For kind "path": the points, [x, z] or [x, y, z], relative to nothing (world).</summary>
        [JsonProperty("points")] public List<float[]> Points { get; set; }
    }

    public sealed class BodyDef
    {
        [JsonProperty("radius")] public LNum Radius { get; set; }
        [JsonProperty("height")] public LNum Height { get; set; }
        /// <summary>[w, d]: a box footprint that turns with the entity, instead of a circle.</summary>
        [JsonProperty("box")] public float[] Box { get; set; }
        /// <summary>Blocks ground movement (a NavmeshCut).</summary>
        [JsonProperty("solid")] public bool? Solid { get; set; }
        /// <summary>Its top is ground units can walk on.</summary>
        [JsonProperty("walkable")] public bool? Walkable { get; set; }
        /// <summary>Has hit points and can be targeted and damaged.</summary>
        [JsonProperty("hittable")] public bool? Hittable { get; set; }
        [JsonProperty("hp")] public LNum Hp { get; set; }
        /// <summary>Targeting treats it as air (the Flying tag) when hittable.</summary>
        [JsonProperty("flying")] public bool? Flying { get; set; }
        /// <summary>Game tags (TagManager.ETag names) a hittable body carries, e.g. ["Building"] so vanilla enemies
        /// attack it like a building (vanilla AI picks targets only by tags).</summary>
        [JsonProperty("tags")] public List<string> Tags { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class ProjectileDef
    {
        [JsonProperty("speed")] public LNum Speed { get; set; }
        [JsonProperty("accel")] public LNum Accel { get; set; }
        /// <summary>0 = straight at where the target was; 1 = always hits; between: turn rate.</summary>
        [JsonProperty("homing")] public LNum Homing { get; set; }
        /// <summary>Parabola height as a fraction of the distance.</summary>
        [JsonProperty("arc")] public LNum Arc { get; set; }
        /// <summary>m/s² for a ballistic shot to a point (overrides arc).</summary>
        [JsonProperty("gravity")] public LNum Gravity { get; set; }
        /// <summary>Degrees per second [x, y, z], visual only.</summary>
        [JsonProperty("spin")] public float[] Spin { get; set; }
        /// <summary>"travel" | "fixed" | "spin".</summary>
        [JsonProperty("face")] public string Face { get; set; }
        /// <summary>A filter of what it can strike.</summary>
        [JsonProperty("hits")] public string Hits { get; set; }
        [JsonProperty("hitRadius")] public LNum HitRadius { get; set; }
        /// <summary>Extra targets it passes through (-1 = all).</summary>
        [JsonProperty("pierce")] public int? Pierce { get; set; }
        /// <summary>"impact" | "stick" | "bounce" | "pass".</summary>
        [JsonProperty("onGround")] public string OnGround { get; set; }
        /// <summary>Metres travelled before "expire".</summary>
        [JsonProperty("range")] public LNum Range { get; set; }
        /// <summary>An effect played at each impact; "none" = off (a lobbed shot's default is "dust_small").</summary>
        [JsonProperty("impactFx")] public string ImpactFx { get; set; }
        /// <summary>Landing marker at the aim point while it flies: a radius in metres, true (default size) or false/0
        /// (off). Lobbed shots (gravity or arc) have one by default.</summary>
        [JsonProperty("warn")] public JToken Warn { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class MoverDef
    {
        /// <summary>"@pathName" or inline [[x, z] | [x, y, z], ...]; null = stays put.</summary>
        [JsonProperty("path")] public JToken Path { get; set; }
        /// <summary>"loop" | "pingpong" | "once".</summary>
        [JsonProperty("mode")] public string Mode { get; set; }
        [JsonProperty("speed")] public LNum Speed { get; set; }
        [JsonProperty("wait")] public LNum Wait { get; set; }
        /// <summary>Degrees per second around Y.</summary>
        [JsonProperty("spin")] public LNum Spin { get; set; }
        [JsonProperty("swing")] public SwingDef Swing { get; set; }
        [JsonProperty("running")] public bool? Running { get; set; }
        /// <summary>Named poses; the signal of a pose's name moves to it.</summary>
        [JsonProperty("pose")] public Dictionary<string, PoseDef> Pose { get; set; }
        /// <summary>Accepted alias of "pose".</summary>
        [JsonProperty("poses")] public Dictionary<string, PoseDef> Poses { get; set; }
        /// <summary>Degrees per second for pose turns (default 90).</summary>
        [JsonProperty("turnSpeed")] public LNum TurnSpeed { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class SwingDef
    {
        [JsonProperty("angle")] public LNum Angle { get; set; }
        [JsonProperty("speed")] public LNum Speed { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class PoseDef
    {
        /// <summary>Offset from the placed position, [x, y, z].</summary>
        [JsonProperty("offset")] public float[] Offset { get; set; }
        /// <summary>Or a world position [x, y, z].</summary>
        [JsonProperty("pos")] public float[] Pos { get; set; }
        [JsonProperty("rotY")] public float? RotY { get; set; }
        [JsonProperty("scale")] public float? Scale { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class AreaDef
    {
        [JsonProperty("shape")] public ShapeDef Shape { get; set; }
        /// <summary>A filter of what it reports (default: units and the king).</summary>
        [JsonProperty("watch")] public string Watch { get; set; }
        /// <summary>"none" | "outline" | "fill".</summary>
        [JsonProperty("show")] public string Show { get; set; }
        [JsonProperty("color")] public string Color { get; set; }
        /// <summary>Seconds between "inside" events; null = no inside events.</summary>
        [JsonProperty("tick")] public LNum Tick { get; set; }
        /// <summary>Makes the area a named pathfinding surface: "mud", or {"name": "mud", "allow": "&lt;filter: who may walk
        /// it&gt;", "cost": &lt;path cost multiplier, default 1&gt;}. Units' movement.surfaces override per unit. At most 31
        /// surface names per level.</summary>
        [JsonProperty("surface")] public JToken Surface { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>One of circle {radius}, rect {size: [w, d]} or polygon {points: [[x, z], ...]}, relative to the entity.</summary>
    public sealed class ShapeDef
    {
        [JsonProperty("circle")] public CircleDef Circle { get; set; }
        [JsonProperty("rect")] public RectDef Rect { get; set; }
        [JsonProperty("polygon")] public PolygonDef Polygon { get; set; }
        /// <summary>A cone on the ground along the entity's facing (a beam, a breath, a field of view).</summary>
        [JsonProperty("sector")] public SectorDef Sector { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A sector: `radius` metres long, `angle` degrees wide, centred on the entity's facing.</summary>
    public sealed class SectorDef
    {
        [JsonProperty("radius")] public LNum Radius { get; set; }
        [JsonProperty("angle")] public LNum Angle { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A light that follows its entity (a unit, the king, a prop, a projectile, an attached building): it lights
    /// the ground round it (or a cone along its facing) and counts for `lightAt`, `.lit` and the `lit` filter term.</summary>
    public sealed class LightDef
    {
        /// <summary>#rrggbb (default a warm #ffd890).</summary>
        [JsonProperty("color")] public string Color { get; set; }
        /// <summary>1 lights its range fully (default 1).</summary>
        [JsonProperty("intensity")] public LNum Intensity { get; set; }
        /// <summary>Radius it lights on the ground, or a cone's length (m, default 10).</summary>
        [JsonProperty("range")] public LNum Range { get; set; }
        /// <summary>A beam this many degrees wide along the entity's facing (null: all round).</summary>
        [JsonProperty("cone")] public LNum Cone { get; set; }
        /// <summary>A cone's yaw relative to the facing (degrees).</summary>
        [JsonProperty("aim")] public LNum Aim { get; set; }
        /// <summary>Metres above the entity (default 3).</summary>
        [JsonProperty("height")] public LNum Height { get; set; }
        /// <summary>0..1: a fire's flicker (looks only).</summary>
        [JsonProperty("flicker")] public LNum Flicker { get; set; }
        /// <summary>false: placed switched off until `setLight` with on: true.</summary>
        [JsonProperty("on")] public bool? On { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class CircleDef { [JsonProperty("radius")] public LNum Radius { get; set; } [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } }
    public sealed class RectDef { [JsonProperty("size")] public float[] Size { get; set; } [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } }
    public sealed class PolygonDef { [JsonProperty("points")] public List<float[]> Points { get; set; } [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } }

    public sealed class PickupDef
    {
        /// <summary>A filter of who collects it ("king", "player and unit").</summary>
        [JsonProperty("by")] public string By { get; set; }
        [JsonProperty("radius")] public LNum Radius { get; set; }
        /// <summary>Range at which it flies toward the collector.</summary>
        [JsonProperty("magnet")] public LNum Magnet { get; set; }
        /// <summary>false: it isn't destroyed on pickup (a rule decides).</summary>
        [JsonProperty("consume")] public bool? Consume { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class InteractDef
    {
        /// <summary>Text with {cost} and {expr} parts; re-evaluated whenever the king focuses it.</summary>
        [JsonProperty("prompt")] public string Prompt { get; set; }
        [JsonProperty("cost")] public LNum Cost { get; set; }
        /// <summary>"day" | "night" | "always".</summary>
        [JsonProperty("when")] public string When { get; set; }
        [JsonProperty("radius")] public LNum Radius { get; set; }
        [JsonProperty("repeat")] public bool? Repeat { get; set; }
        /// <summary>Level variables paid too (items): [{ "var": "$bread", "amount": 1 }].</summary>
        [JsonProperty("pay")] public List<PayDef> Pay { get; set; }
        /// <summary>The choices shown in the game's upgrade-choice popup (up to 4 at once; a longer pool with `offer`
        /// or per-choice `if`).</summary>
        [JsonProperty("choices")] public List<ChoiceDef> Choices { get; set; }
        /// <summary>Expression giving a list of choice ids: which choices the popup shows, in that order (up to 4);
        /// evaluated on each press, e.g. "$offers" set by a dawn rule to "['a','b','c','d'].sample(3)".</summary>
        [JsonProperty("offer")] public string Offer { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class ChoiceDef
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("cost")] public LNum Cost { get; set; }
        [JsonProperty("pay")] public List<PayDef> Pay { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        /// <summary>Bool expression: the choice is shown only while it holds (checked on each press).</summary>
        [JsonProperty("if")] public string If { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A cost in a level variable (an item count): paid with the gold, refused when short.</summary>
    public sealed class PayDef
    {
        /// <summary>A level variable, "$name".</summary>
        [JsonProperty("var")] public string Var { get; set; }
        [JsonProperty("amount")] public LNum Amount { get; set; }
        /// <summary>The word the prompt uses (default: the variable's name).</summary>
        [JsonProperty("label")] public string Label { get; set; }
        /// <summary>A build slot's pay only: the level this item is paid for (1 = the build); none = every level.</summary>
        [JsonProperty("level")] public int? Level { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A part: an entity spawned and linked to its parent when the parent appears (Runtime/Links.cs).</summary>
    public sealed class PartDef
    {
        /// <summary>The template.</summary>
        [JsonProperty("entity")] public string Entity { get; set; }
        /// <summary>[x, y, z] in the parent's frame: x right, y up, z ahead.</summary>
        [JsonProperty("offset")] public float[] Offset { get; set; }
        [JsonProperty("rotY")] public float? RotY { get; set; }
        /// <summary>Default: the parent's team ("owner").</summary>
        [JsonProperty("team")] public string Team { get; set; }
        /// <summary>A label the part gets (#name), to tell parts apart.</summary>
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>"rigid" | "position" | "none".</summary>
        [JsonProperty("follow")] public string Follow { get; set; }
        /// <summary>"die" | "drop" | "stay".</summary>
        [JsonProperty("onParentDeath")] public string OnParentDeath { get; set; }
        [JsonProperty("damageToParent")] public float? DamageToParent { get; set; }
        [JsonProperty("damageToGroup")] public float? DamageToGroup { get; set; }
        [JsonProperty("damageKept")] public float? DamageKept { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class AttackDef
    {
        [JsonProperty("id")] public string Id { get; set; }
        /// <summary>true: the unit's base attacks are removed.</summary>
        [JsonProperty("replace")] public bool? Replace { get; set; }
        [JsonProperty("cooldown")] public LNum Cooldown { get; set; }
        [JsonProperty("jitter")] public LNum Jitter { get; set; }
        [JsonProperty("range")] public LNum Range { get; set; }
        [JsonProperty("minRange")] public LNum MinRange { get; set; }
        /// <summary>"night" (default) | "always" | "day".</summary>
        [JsonProperty("when")] public string When { get; set; }
        [JsonProperty("targeting")] public List<TargetingDef> Targeting { get; set; }
        [JsonProperty("launch")] public LaunchDef Launch { get; set; }
        /// <summary>"vanilla:&lt;weapon&gt;" or "inherit": the game's damage and arrows.</summary>
        [JsonProperty("weapon")] public string Weapon { get; set; }
        /// <summary>Extra actions per attack (other = the target).</summary>
        [JsonProperty("do")] public List<ActionDef> Do { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class LaunchDef
    {
        [JsonProperty("entity")] public string Entity { get; set; }
        /// <summary>Offset from the attacker, [x, y, z].</summary>
        [JsonProperty("from")] public float[] From { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>One targeting rule. JSON: a filter string, or {"filter", "range", "prefer", "score"}.</summary>
    [JsonConverter(typeof(TargetingConverter))]
    public sealed class TargetingDef
    {
        public string Filter;
        public LNum Range;
        /// <summary>nearest (default; best when a score is given) | farthest | weakest | strongest | random | best (+ score).</summary>
        public string Prefer;
        /// <summary>An expression over `it`; highest wins (implies prefer "best").</summary>
        public string Score;
        public JObject Extra;

        public static implicit operator TargetingDef(string filter) => new() { Filter = filter };
    }

    internal sealed class TargetingConverter : JsonConverter<TargetingDef>
    {
        public override TargetingDef ReadJson(JsonReader reader, Type objectType, TargetingDef existing, bool hasExisting, JsonSerializer serializer)
        {
            var t = JToken.Load(reader);
            if (t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return new TargetingDef { Filter = (string)t };
            if (t is not JObject o) throw new JsonSerializationException("a targeting entry is a filter string or {\"filter\": ...}");
            var d = new TargetingDef { Filter = (string)o["filter"], Prefer = (string)o["prefer"], Score = (string)o["score"] };
            if (o["range"] is JToken r && r.Type != JTokenType.Null) d.Range = r.ToObject<LNum>(serializer);
            foreach (var p in o.Properties())
                if (p.Name is not ("filter" or "range" or "prefer" or "score")) (d.Extra ??= new JObject())[p.Name] = p.Value.DeepClone();
            return d;
        }

        public override void WriteJson(JsonWriter writer, TargetingDef value, JsonSerializer serializer)
        {
            if (value == null) { writer.WriteNull(); return; }
            if (value.Range == null && value.Prefer == null && value.Score == null && (value.Extra == null || value.Extra.Count == 0)) { writer.WriteValue(value.Filter); return; }
            var o = new JObject { ["filter"] = value.Filter };
            if (value.Range != null) o["range"] = JToken.FromObject(value.Range, serializer);
            if (value.Prefer != null) o["prefer"] = value.Prefer;
            if (value.Score != null) o["score"] = value.Score;
            foreach (var p in value.Extra ?? new JObject()) o[p.Key] = p.Value?.DeepClone();
            o.WriteTo(writer);
        }
    }

    public sealed class MovementDef
    {
        /// <summary>"pathfind" (default) | "direct" | "hold" | "path" | "follow".</summary>
        [JsonProperty("mode")] public string Mode { get; set; }
        /// <summary>For "path": "@pathName" or inline points.</summary>
        [JsonProperty("path")] public JToken Path { get; set; }
        /// <summary>At a path's end: "pathfind" | "hold" | "destroy".</summary>
        [JsonProperty("then")] public string Then { get; set; }
        [JsonProperty("speed")] public LNum Speed { get; set; }
        [JsonProperty("keepDistance")] public LNum KeepDistance { get; set; }
        [JsonProperty("kite")] public KiteDef Kite { get; set; }
        [JsonProperty("flee")] public FleeDef Flee { get; set; }
        [JsonProperty("wander")] public WanderDef Wander { get; set; }
        /// <summary>"spawn" | [x, y, z] | "@point".</summary>
        [JsonProperty("home")] public JToken Home { get; set; }
        /// <summary>For "follow": the leader, an entity expression re-read 10 times a second ("self.parent").</summary>
        [JsonProperty("leader")] public string Leader { get; set; }
        /// <summary>For "follow": metres behind the leader (a trail), default 2.</summary>
        [JsonProperty("spacing")] public LNum Spacing { get; set; }
        /// <summary>For "follow": a slot [x, z] in the leader's frame (x right, z ahead) instead of the trail.</summary>
        [JsonProperty("offset")] public float[] Offset { get; set; }
        /// <summary>For "follow": speed multiplier while left behind (default 1.5).</summary>
        [JsonProperty("catchUp")] public LNum CatchUp { get; set; }
        /// <summary>Per surface name: true (may walk it), false (may not), or a path cost multiplier.</summary>
        [JsonProperty("surfaces")] public Dictionary<string, JToken> Surfaces { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class KiteDef { [JsonProperty("below")] public LNum Below { get; set; } [JsonProperty("for")] public LNum For { get; set; } [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } }

    public sealed class FleeDef
    {
        /// <summary>A bool expression.</summary>
        [JsonProperty("when")] public string When { get; set; }
        /// <summary>A filter of what it flees from.</summary>
        [JsonProperty("from")] public string From { get; set; }
        /// <summary>A position expression ("@exit").</summary>
        [JsonProperty("to")] public string To { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class WanderDef { [JsonProperty("radius")] public LNum Radius { get; set; } [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } }

    // ---- Statuses and presets ---------------------------------------------------------------------------------

    public sealed class StatusDef
    {
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        /// <summary>Seconds; null = until removed (or the applier's "for").</summary>
        [JsonProperty("duration")] public LNum Duration { get; set; }
        /// <summary>"refresh" (default) | "add" | "extend" | "ignore".</summary>
        [JsonProperty("stacking")] public string Stacking { get; set; }
        [JsonProperty("maxStacks")] public int? MaxStacks { get; set; }
        /// <summary>Seconds between statusTick events.</summary>
        [JsonProperty("tick")] public LNum Tick { get; set; }
        /// <summary>Stat modifiers while active, by stat name (LogicSchema.Stats), plus "drift".</summary>
        [JsonProperty("mods")] public Dictionary<string, ModDef> Mods { get; set; }
        /// <summary>stunned | disarmed | rooted | untargetable | invulnerable | silenced | noIncome.</summary>
        [JsonProperty("flags")] public List<string> Flags { get; set; }
        [JsonProperty("visual")] public StatusVisualDef Visual { get; set; }
        [JsonProperty("rules")] public List<RuleDef> Rules { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class ModDef
    {
        [JsonProperty("mult")] public LNum Mult { get; set; }
        [JsonProperty("add")] public LNum Add { get; set; }
        [JsonProperty("value")] public LNum Value { get; set; }
        /// <summary>damageTaken/damageMult only: damage types it applies to.</summary>
        [JsonProperty("types")] public List<string> Types { get; set; }
        /// <summary>damageTaken/damageMult only: a filter on the attacker.</summary>
        [JsonProperty("from")] public string From { get; set; }
        [JsonProperty("chance")] public LNum Chance { get; set; }
        /// <summary>damageTaken/damageMult only: an expression with attacker, victim and dist.</summary>
        [JsonProperty("when")] public string When { get; set; }
        /// <summary>drift only: a position/entity expression to drift toward or from, or a direction.</summary>
        [JsonProperty("toward")] public string Toward { get; set; }
        [JsonProperty("away")] public string Away { get; set; }
        [JsonProperty("dir")] public string Dir { get; set; }
        [JsonProperty("speed")] public LNum Speed { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public sealed class StatusVisualDef
    {
        [JsonProperty("tint")] public string Tint { get; set; }
        [JsonProperty("fx")] public string Fx { get; set; }
        [JsonProperty("glow")] public string Glow { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>A saved, parameterised list of rules and components, used by {"preset": id, "with": {...}} in a
    /// rules list, or by a place's "preset".</summary>
    public sealed class PresetDef
    {
        [JsonProperty("id", Order = -10)] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>Text; {param} shows a param's value.</summary>
        [JsonProperty("description")] public string Description { get; set; }
        /// <summary>Studio palette group ("Logic", "Enemy", ...).</summary>
        [JsonProperty("category")] public string Category { get; set; }
        /// <summary>For a placeable preset (a logic object): the kind of entity it places.</summary>
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("params")] public Dictionary<string, PresetParamDef> Params { get; set; }
        [JsonProperty("vars")] public Dictionary<string, JToken> Vars { get; set; }
        [JsonProperty("labels")] public List<string> Labels { get; set; }
        [JsonProperty("rules")] public List<RuleDef> Rules { get; set; }
        /// <summary>Components merged under the owner's own (movement, area, interact, mover, body, visual...).</summary>
        [JsonExtensionData] public IDictionary<string, JToken> Components { get; set; }
    }

    public sealed class PresetParamDef
    {
        /// <summary>A LogicSchema param type: number, bool, text, template, entity, position, filter, status, signal, ...</summary>
        [JsonProperty("type")] public string Type { get; set; } = "number";
        [JsonProperty("default")] public JToken Default { get; set; }
        [JsonProperty("min")] public double? Min { get; set; }
        [JsonProperty("max")] public double? Max { get; set; }
        [JsonProperty("options")] public List<string> Options { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
}
