using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    The single source of truth for the behaviour engine's vocabulary (docs/builder-engine.md, "Runtime"): every event,
    action, stat, flag, function, property, global and filter term, with its params, roles, description, example and
    the PalaceMod version that added it. The validator, the Studio's pickers and hovers, `builder dump`
    (logic-schema.json for agents), agent-kit/reference.md and the "every action has a handler" test all read it.

    Param types (ParamSpec.Type):
      number, int, bool      a literal, or an expression string
      text                   literal text with {expr} parts; "=expr" makes it an expression
      position               an expression (an entity coerces to its position) or [x, y, z]
      entity / entities      a selector expression: one entity / one or a set (actions apply to each member)
      template               a template id (pack unit or entity, "vanilla:<id>"); "=expr" makes it an expression
      filter                 a filter string (see Filters)
      status rule var signal label action   a literal name of that kind
      enum                   one of Options
      actions                a nested action list
      options                choose's [{ "weight", "do" }]
      targeting movement     a targeting list / a movement block (TargetingDef / MovementDef)
      any                    an expression of any type (setVar's value)
      for                    seconds, or "night" / "day" (until the next dawn / dusk)
      night                  a night number expression, or "current"
      slot                   a build slot: an attach name (@name), a selector, or a Studio key ("slot:Tower 3")
      types                  a list of damage-type labels
      keep                   a list of enum Options
      fx sound color offset  an effect name / a sound / "#rrggbb" / [x, y, z] relative to the entity
      json                   a JSON block, checked by the action itself
*/
namespace PalaceMod.Builder.Model.Logic
{
    public sealed class ParamSpec
    {
        public string Name;
        public string Type;
        public bool Required;
        public JToken Default;
        public double? Min, Max;
        public string[] Options;
        public string Description;
        /// <summary>For "actions" params: the extra role this list binds (spawned), if any.</summary>
        public string Binds;
        public string Since = LogicSchema.V1;

        public JObject ToJson()
        {
            var o = new JObject { ["name"] = Name, ["type"] = Type };
            if (Required) o["required"] = true;
            if (Default != null) o["default"] = Default.DeepClone();
            if (Min != null) o["min"] = Min;
            if (Max != null) o["max"] = Max;
            if (Options != null) o["options"] = new JArray(Options);
            if (Binds != null) o["binds"] = Binds;
            o["description"] = Description;
            return o;
        }
    }

    public sealed class EventSpec
    {
        public string Name, Group, Description, Example;
        public ParamSpec[] Params = [];
        /// <summary>What `other` is ("killer"), or null when the event has no other.</summary>
        public string Other;
        /// <summary>Type of event.value, or Void.</summary>
        public LType Value = LType.Void;
        public string ValueMeaning;
        public bool HasPos;
        /// <summary>Extra event fields (event.name, event.choice, event.result) and their types.</summary>
        public Dictionary<string, LType> Fields = new();
        /// <summary>Per-event alias → "other" | "subject".</summary>
        public Dictionary<string, string> Aliases = new();
        /// <summary>Owner kinds that fire it themselves: unit, building, entity kinds, level, any.</summary>
        public string[] Owners = ["any"];
        /// <summary>Fired by the level/world, not an entity (the level script hears it without `of`).</summary>
        public bool Global;
        public string Since = LogicSchema.V1;
    }

    public sealed class ActionSpec
    {
        public string Name, Group, Description, Example;
        public ParamSpec[] Params = [];
        /// <summary>The action acts on `target` (default self).</summary>
        public bool Targeted;
        /// <summary>L1 provides control flow; L2 the world verbs. Informational.</summary>
        public string Owner;
        public string Since = LogicSchema.V1;

        public ParamSpec Param(string name) => Params.FirstOrDefault(p => p.Name == name);
    }

    public sealed class FunctionSpec
    {
        public string Name, Description, Example;
        /// <summary>Arg types (LType names or "filter", "lazy"); a trailing '?' marks an optional arg.</summary>
        public string[] Args = [];
        public LType Returns;
        /// <summary>Can be called as a method on an entity (e.nearest(...) anchors at e).</summary>
        public bool Method;
        /// <summary>Only as a method, on this receiver type.</summary>
        public LType? OnlyOn;
        public int MinArgs => Args.Count(a => !a.EndsWith("?") && a != "...");
        public string Since = LogicSchema.V1;
    }

    public sealed class PropertySpec
    {
        public string Name, Description;
        public LType On, Returns;
        public string Since = LogicSchema.V1;
    }

    public sealed class NamedSpec
    {
        public string Name, Description;
        public LType Type = LType.Void;
        public string Since = LogicSchema.V1;
    }

    public static class LogicSchema
    {
        /// <summary>The PalaceMod version the behaviour engine first shipped in (fills requires.palaceMod).</summary>
        public const string V1 = "0.9.0";

        public static readonly Dictionary<string, EventSpec> Events = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, ActionSpec> Actions = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, FunctionSpec> Functions = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, NamedSpec> Stats = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, NamedSpec> Flags = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, NamedSpec> Globals = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, NamedSpec> Roles = new(StringComparer.Ordinal);
        /// <summary>Filter terms that aren't game tags (those are TagManager.ETag names).</summary>
        public static readonly Dictionary<string, NamedSpec> FilterTerms = new(StringComparer.Ordinal);
        public static readonly List<PropertySpec> Properties = [];
        /// <summary>Every alias any event defines (killer, victim...), for the parser.</summary>
        public static readonly Dictionary<string, string> RoleAliases = new(StringComparer.Ordinal);
        /// <summary>Built-in signal inputs every entity, or an entity with that component, accepts.</summary>
        public static readonly Dictionary<string, string[]> Inputs = new(StringComparer.Ordinal);

        /// <summary>The game's tags (TagManager.ETag minus NONE), usable as filter terms; a test keeps it in step.</summary>
        public static readonly HashSet<string> Tags = new(StringComparer.Ordinal) { "PlayerOwned", "EnemyOwned", "Player", "CastleCenter", "MeeleFighter", "RangedFighter", "Flying", "PlayerUnit", "Building", "SiegeWeapon", "AUTO_Alive", "AUTO_KnockedOutAndHealOnDawn", "Wall", "InfrastructureEconomy", "TakesReducedDamageFromPlayerAttacks", "PracticeTargets", "FastMoving", "ArmoredAgainstRanged", "VulnerableVsRanged", "Monster", "House", "WallOrTower", "AUTO_Commanded", "TakesIncreasedDamageFromTowers", "Tower", "AUTO_NoReviveNextMorning", "PlayerOwnedPriorityTarget", "BlockableEnemyProjectile", "Boss", "UselessWallThatDoesNotBlockPath", "Healer", "MagicProjectile", "NaturallyVulnerableToSplash", "NaturallyHighHealthTarget", "UnreachableForBigEnemiesDueToLevelDesign", "PlayerShrine", "VodooCursed", "PlayerHeroUnit", "FireAndExplosionResistant", "Humanoid", "LargeUnit", "Exploding", "TrainingFacility", "Bridge", "Group1", "Group2", "Group3", "OnDeathEnemySpawner", "Undead", "Vampire" };

        public static PropertySpec Property(LType on, string name) =>
            Properties.FirstOrDefault(p => p.Name == name && (p.On == on || p.On == LType.Any));

        // ---- builders ---------------------------------------------------------------------------------------

        private static ParamSpec P(string name, string type, string desc, bool req = false, JToken def = null, double? min = null, double? max = null, string[] opts = null, string binds = null) =>
            new() { Name = name, Type = type, Description = desc, Required = req, Default = def, Min = min, Max = max, Options = opts, Binds = binds };

        private static readonly ParamSpec Target = P("target", "entities", "Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.", def: "self");

        private static void Ev(string name, string group, string desc, string other = null, LType value = LType.Void, string valueMeaning = null,
            ParamSpec[] ps = null, bool pos = false, string[] owners = null, bool global = false, string example = null,
            Dictionary<string, string> aliases = null, Dictionary<string, LType> fields = null)
        {
            var e = new EventSpec
            {
                Name = name, Group = group, Description = desc, Other = other, Value = value, ValueMeaning = valueMeaning,
                Params = ps ?? [], HasPos = pos, Owners = owners ?? ["any"], Global = global, Example = example,
                Aliases = aliases ?? new(), Fields = fields ?? new(),
            };
            Events[name] = e;
            foreach (var a in e.Aliases) RoleAliases[a.Key] = a.Value;
        }

        private static void Act(string name, string group, string desc, ParamSpec[] ps, bool targeted = false, string owner = "L2", string example = null)
        {
            var list = new List<ParamSpec>();
            if (targeted) list.Add(Target);
            list.AddRange(ps);
            Actions[name] = new ActionSpec { Name = name, Group = group, Description = desc, Params = list.ToArray(), Targeted = targeted, Owner = owner, Example = example };
        }

        private static void Fn(string name, LType returns, string desc, string[] args, bool method = false, LType? onlyOn = null, string example = null) =>
            Functions[name] = new FunctionSpec { Name = name, Returns = returns, Description = desc, Args = args, Method = method, OnlyOn = onlyOn, Example = example };

        private static void Prop(LType on, string name, LType returns, string desc) =>
            Properties.Add(new PropertySpec { On = on, Name = name, Returns = returns, Description = desc });

        private static void Named(Dictionary<string, NamedSpec> into, string name, string desc, LType type = LType.Void) =>
            into[name] = new NamedSpec { Name = name, Description = desc, Type = type };

        private static Dictionary<string, string> A(params string[] pairs)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        static LogicSchema()
        {
            // ---- Roles -----------------------------------------------------------------------------------------
            Named(Roles, "self", "The rule's owner (the level in the level script).", LType.Entity);
            Named(Roles, "subject", "Who the event happened to (self for its own events; the other entity for `of` listeners).", LType.Entity);
            Named(Roles, "other", "The event's counterpart: killer, victim, attacker, entrant, collector, sender...", LType.Entity);
            Named(Roles, "spawned", "Inside spawn/launch/replace `then`: the new entity.", LType.Entity);
            Named(Roles, "it", "The current candidate: inside a filter's where(), best()'s score, forEach, or an action applied to a set.", LType.Entity);
            Named(Roles, "king", "The player's king.", LType.Entity);
            Named(Roles, "castle", "The castle (its build slot's building).", LType.Entity);
            Named(Roles, "level", "The level itself (owner of level variables).", LType.Level);
            Named(Roles, "event", "The event's fields: event.value, event.pos, event.name, event.choice, event.result.", LType.Event);

            // ---- Events ----------------------------------------------------------------------------------------
            var unitish = new[] { "unit", "building", "prop", "projectile", "area", "pickup", "logic", "king" };
            Ev("spawn", "This unit", "It appears: placed ones at level start, spawned ones on creation. `other` is the spawner or launcher, if any.", other: "spawner", aliases: A("spawner", "other"));
            Ev("death", "Combat", "It dies (a unit) or is knocked out (a building or the king). `other` is the killer (may be none).", other: "killer", aliases: A("killer", "other", "victim", "subject"),
                example: "{ \"when\": \"death\", \"do\": [{ \"do\": \"spawn\", \"entity\": \"goblin\", \"count\": 2, \"at\": \"self\" }] }");
            Ev("damaged", "Combat", "It takes damage (not when modifiers reduce it to 0).", other: "attacker", value: LType.Number, valueMeaning: "the damage taken",
                ps: [P("min", "number", "Only damage of at least this much.")], aliases: A("attacker", "other"));
            Ev("healed", "Combat", "It is healed.", other: "healer", value: LType.Number, valueMeaning: "the amount healed", aliases: A("healer", "other"));
            Ev("hpBelow", "Combat", "Its hp crosses below a fraction (edge-triggered; re-arms when healed above).", value: LType.Number, valueMeaning: "hpPct",
                ps: [P("pct", "number", "Fraction of max hp, 0..1.", req: true, min: 0, max: 1)]);
            Ev("attack", "Combat", "It attacks (any attack, vanilla ones included).", other: "target", ps: [P("attack", "text", "Only this attack id.")], aliases: A("target", "other"));
            Ev("hit", "Combat", "Its attack, projectile or damage action damaged something.", other: "victim", value: LType.Number, valueMeaning: "the damage dealt", aliases: A("victim", "other"));
            Ev("kill", "Combat", "Something it damaged died from that damage.", other: "victim", aliases: A("victim", "other"));
            Ev("every", "Time", "Periodically, from spawn (or level start).", value: LType.Number, valueMeaning: "the tick count (1, 2, ...)",
                ps: [P("seconds", "number", "Seconds between firings (min 0.1).", req: true, min: 0.1),
                     P("jitter", "number", "Random extra delay each time, 0..1 of seconds.", min: 0, max: 1),
                     P("phase", "number", "Seconds before the first firing (default: seconds)."),
                     P("during", "enum", "Only at night, by day, or always.", def: "always", opts: ["always", "night", "day"])],
                global: true, example: "{ \"when\": { \"on\": \"every\", \"seconds\": 8, \"during\": \"night\" }, \"do\": [...] }");
            Ev("after", "Time", "Once, that long after spawn (or level start).", ps: [P("seconds", "number", "Delay in seconds.", req: true, min: 0)], global: true);
            Ev("near", "Areas", "A matching entity comes within (or leaves) a range of it.", other: "entity",
                ps: [P("filter", "filter", "What to watch for.", req: true), P("range", "number", "Metres.", req: true, min: 0),
                     P("edge", "enum", "Fire on entering, leaving, or both.", def: "enter", opts: ["enter", "exit", "both"])]);
            Ev("enter", "Areas", "(areas) An entity matching the area's watch filter enters.", other: "entrant", owners: ["area"], aliases: A("entrant", "other"));
            Ev("exit", "Areas", "(areas) An entity leaves.", other: "entity", owners: ["area"]);
            Ev("inside", "Areas", "(areas) For each entity inside, after every full `tick` it has spent inside.", other: "entity", owners: ["area"]);
            Ev("empty", "Areas", "(areas) The last watched entity left.", owners: ["area"]);
            Ev("occupied", "Areas", "(areas) The first watched entity entered.", other: "entity", owners: ["area"]);
            Ev("impact", "Projectiles", "(projectiles) It hit something or the ground. `other` is what it hit (none for ground).", other: "hit entity", pos: true, owners: ["projectile"]);
            Ev("expire", "Time", "Its lifetime (or projectile range) ran out; it's destroyed after its rules run.");
            Ev("arrive", "Movement", "(movers, path followers, moveTo) It reached a point.", value: LType.Number, valueMeaning: "the point index");
            Ev("end", "Movement", "(movers, path followers) It reached the end of its path (before then: destroy acts).");
            Ev("pickup", "Pickups", "(pickups) Collected. `other` is the collector.", other: "collector", owners: ["pickup"], aliases: A("collector", "other"));
            Ev("interact", "King", "(interactables) The king used it, after paying its cost. event.choice is the chosen id with choices.", other: "king", owners: unitish,
                fields: new() { ["choice"] = LType.Text });
            Ev("signal", "Signals", "It receives a signal. event.name is the signal's name, event.value its number.", other: "sender", value: LType.Number, valueMeaning: "the signal's number",
                ps: [P("name", "signal", "Only this signal (omit to hear every signal).")], global: true, aliases: A("sender", "other"),
                fields: new() { ["name"] = LType.Text });
            Ev("varChanged", "Variables", "One of its variables changes (a level var, on the level script).", value: LType.Any, valueMeaning: "the new value",
                ps: [P("name", "var", "Only this variable.")], global: true, fields: new() { ["name"] = LType.Text });
            Ev("statusStart", "Statuses", "A status starts on it.", other: "applier", value: LType.Number, valueMeaning: "stacks",
                ps: [P("status", "status", "Only this status (required when watching one from outside a status).")], aliases: A("applier", "other"), fields: new() { ["name"] = LType.Text });
            Ev("statusEnd", "Statuses", "A status ends (expiry or removal).", other: "applier", value: LType.Number, valueMeaning: "stacks",
                ps: [P("status", "status", "Only this status.")], aliases: A("applier", "other"), fields: new() { ["name"] = LType.Text });
            Ev("statusTick", "Statuses", "Every `tick` seconds of a status (in a status's own rules: of that status).", other: "applier", value: LType.Number, valueMeaning: "stacks",
                ps: [P("status", "status", "Only this status.")], aliases: A("applier", "other"), fields: new() { ["name"] = LType.Text });
            Ev("targetChanged", "Combat", "Its attack or move target changes.", other: "target", aliases: A("target", "other"));
            Ev("built", "Buildings", "(build slots) The player builds it.", value: LType.Number, valueMeaning: "the level", owners: ["building"], ps: [P("level", "int", "Only this level.")]);
            Ev("upgraded", "Buildings", "(build slots) The player upgrades it.", value: LType.Number, valueMeaning: "the new level", owners: ["building"], ps: [P("level", "int", "Only this level.")]);
            Ev("repaired", "Buildings", "(build slots) It's revived at dawn.", value: LType.Number, valueMeaning: "the level", owners: ["building"], ps: [P("level", "int", "Only this level.")]);
            Ev("coinDrop", "Gold", "A coin drops from a death (subject is the coin; listen with `of`).", other: "dead unit", owners: ["coin"], global: false);
            Ev("coinPickup", "Gold", "The king collects a coin (subject is the coin; listen with `of`).", other: "king", owners: ["coin"]);
            Ev("lineSpawn", "Night", "(spawn lines) It spawns an enemy (a wave or spawnGroup). `other` is the new enemy.", other: "new enemy", owners: ["line"],
                ps: [P("line", "text", "Only this spawn line.")]);
            Ev("levelStart", "Night", "The level has loaded (after the overlay, before day 1).", global: true);
            Ev("dusk", "Night", "Night is falling (the game's OnDusk).", value: LType.Number, valueMeaning: "the night number", global: true, ps: [P("night", "int", "Only this night.")]);
            Ev("nightStart", "Night", "The night's first enemies spawn.", value: LType.Number, valueMeaning: "the night number", global: true, ps: [P("night", "int", "Only this night.")]);
            Ev("dawn", "Night", "The sun rises (after income is paid).", value: LType.Number, valueMeaning: "the night just survived", global: true, ps: [P("night", "int", "Only after this night.")]);
            Ev("day", "Night", "The day phase begins (after dawn's sunrise).", value: LType.Number, valueMeaning: "the coming night", global: true, ps: [P("night", "int", "Only before this night.")]);
            Ev("waveCleared", "Night", "Spawning is done and no enemies are left.", value: LType.Number, valueMeaning: "the night", global: true);
            Ev("goldChanged", "Gold", "The player's balance changes.", value: LType.Number, valueMeaning: "the change (negative = spent)", global: true);
            Ev("kingAction", "King", "The player presses one of the level's action keys (logic.kingActions).", other: "king", global: true,
                ps: [P("action", "text", "The king action's id.", req: true)]);
            Ev("levelEnd", "Night", "Win or loss, before the end screen. event.result is 'win' or 'lose'.", global: true,
                ps: [P("result", "enum", "Only this result.", opts: ["win", "lose"])], fields: new() { ["result"] = LType.Text });

            // ---- Actions: Make ---------------------------------------------------------------------------------
            Act("spawn", "Make", "Creates entities from a template (a unit or an entity), e.g. split a slime, summon skeletons.",
            [
                P("entity", "template", "The template (a pack unit or entity, or \"vanilla:<id>\"); \"=self.template\" for a copy of itself.", req: true),
                P("count", "int", "How many.", def: 1, min: 1, max: 100),
                P("at", "position", "Where (an entity or a position).", def: "self"),
                P("spread", "number", "Scatter radius in metres.", def: 0, min: 0),
                P("team", "enum", "Its team (default: the template's; owner = the spawner's).", opts: ["player", "enemy", "neutral", "owner"]),
                P("elite", "enum", "Whether spawned enemies are elite; inherit copies the spawner's.", def: "false", opts: ["inherit", "true", "false"]),
                P("facing", "number", "Yaw in degrees (default: the spawner's facing)."),
                P("then", "actions", "Run for each new entity, with `spawned` bound.", binds: "spawned"),
            ], owner: "L2", example: "{ \"do\": \"spawn\", \"entity\": \"=self.template\", \"count\": 2, \"at\": \"self\", \"spread\": 1.5 }");
            Act("launch", "Make", "Fires an entity as a projectile from the owner toward a target (it uses the template's projectile block).",
            [
                P("entity", "template", "A template with a projectile block.", req: true),
                P("from", "position", "Start: an entity or position, or an [x, y, z] offset from self.", def: "self"),
                P("toward", "position", "The target: an entity (homing follows it) or a position.", req: true),
                P("speed", "number", "Overrides projectile.speed."), P("homing", "number", "Overrides projectile.homing.", min: 0, max: 1),
                P("arc", "number", "Overrides projectile.arc."), P("gravity", "number", "Overrides projectile.gravity."),
                P("hits", "filter", "Overrides projectile.hits."), P("pierce", "int", "Overrides projectile.pierce (-1 = all)."),
                P("range", "number", "Overrides projectile.range."),
                P("onImpact", "actions", "Extra actions on impact (the template's impact rules also run), with `spawned` = the projectile and `other` = what it hit.", binds: "spawned"),
                P("then", "actions", "Run once it's launched, with `spawned` bound.", binds: "spawned"),
            ], owner: "L2");
            Act("spawnGroup", "Make", "A wave-style group of enemies now; they count toward the night.",
            [
                P("unit", "template", "The enemy.", req: true), P("count", "int", "How many.", def: 1, min: 1, max: 200),
                P("interval", "number", "Seconds between spawns.", def: 0.5, min: 0), P("line", "text", "A spawn line's name."),
                P("at", "position", "A position instead of a line."), P("elite", "bool", "Elite enemies.", def: false),
                P("gold", "int", "Coins each drops (default: the unit's)."),
            ], owner: "L2");
            Act("replace", "Make", "Swaps the entity for another template in place (morph, shapeshift).",
            [
                P("with", "template", "The new template.", req: true),
                P("keep", "keep", "What carries over.", def: new JArray("hp", "vars", "statuses", "labels"), opts: ["hp", "vars", "statuses", "labels"]),
                P("then", "actions", "Run with `spawned` = the replacement.", binds: "spawned"),
            ], targeted: true, owner: "L2");

            // ---- Health ---------------------------------------------------------------------------------------
            Act("damage", "Health", "Deals damage.",
            [
                P("amount", "number", "Hit points.", req: true, min: 0),
                P("source", "entity", "Who is credited (fires their hit/kill events). Default self.", def: "self"),
                P("types", "types", "Damage-type labels (fire, ...) that status modifiers test."),
                P("splash", "bool", "Counts as splash damage (the game's splash modifiers).", def: false),
                P("weapon", "text", "Apply a weapon's tag modifiers (\"vanilla:<weapon>\")."),
            ], targeted: true, example: "{ \"do\": \"damage\", \"target\": \"all('foe', 3)\", \"amount\": 25 }");
            Act("heal", "Health", "Heals.", [P("amount", "number", "Hit points.", req: true, min: 0)], targeted: true);
            Act("kill", "Health", "Kills it (fires death).", [P("source", "entity", "Who is credited.")], targeted: true);
            Act("revive", "Health", "Revives a knocked-out building or a dead hittable entity.", [P("hpPct", "number", "Hit points as a fraction of max.", def: 1, min: 0, max: 1)], targeted: true);
            Act("setHp", "Health", "Sets hit points without damaged/healed events (shared health pools).", [P("value", "number", "Hit points.", req: true, min: 0)], targeted: true);

            // ---- Stats ----------------------------------------------------------------------------------------
            Act("setStat", "Stats", "Changes a stat, permanently or for a while. Give exactly one of value, add, mult.",
            [
                P("stat", "enum", "The stat (see Stats).", req: true),
                P("value", "number", "Set to this."), P("add", "number", "Add this."), P("mult", "number", "Multiply by this."),
                P("for", "for", "Seconds, or \"night\"/\"day\" (until the next dawn/dusk); omit for permanent."),
                P("key", "text", "A timed change with the same key replaces the earlier one."),
            ], targeted: true, example: "{ \"do\": \"setStat\", \"target\": \"all('player and Tower')\", \"stat\": \"attackCooldown\", \"mult\": 0.5, \"for\": 5 }");
            Act("applyStatus", "Stats", "Applies a status (a timed, stacking modifier set).",
            [
                P("status", "status", "The status id.", req: true),
                P("for", "number", "Seconds (default: the status's duration)."),
                P("stacks", "int", "Stacks to add.", def: 1, min: 1),
                P("source", "entity", "Who applied it (`other` in its rules). Default self.", def: "self"),
            ], targeted: true);
            Act("removeStatus", "Stats", "Removes a status (all stacks, or some).",
                [P("status", "status", "The status id.", req: true), P("stacks", "int", "Stacks to remove (default all).", min: 1)], targeted: true);

            // ---- Team / Move ----------------------------------------------------------------------------------
            Act("convert", "Team", "Switches team (a dead target is raised as a new entity of that team).",
            [
                P("team", "text", "\"player\" | \"enemy\" | \"neutral\", or an expression such as self.team.", req: true),
                P("keepLook", "bool", "Keep its look.", def: true), P("hpPct", "number", "Hit points as a fraction of max.", min: 0, max: 1),
            ], targeted: true, example: "{ \"do\": \"convert\", \"target\": \"other\", \"team\": \"=self.team\", \"hpPct\": 0.5 }");
            Act("teleport", "Move", "Moves it instantly.", [P("at", "position", "Where.", req: true)], targeted: true);
            Act("push", "Move", "A one-off shove (the game's knockback), on units (flyers included).",
            [
                P("from", "position", "Push away from here (default self)."), P("toward", "position", "Pull toward here instead."),
                P("force", "number", "Metres of travel.", req: true), P("up", "number", "Arc height.", def: 0), P("stun", "number", "Seconds stunned.", def: 0),
            ], targeted: true);
            Act("drift", "Move", "A continuous velocity added to movement: a pull, a current, wind. Different keys add up; the same key replaces.",
            [
                P("dir", "position", "A direction vector (dir(deg) gives one)."), P("toward", "position", "Drift toward this."), P("from", "position", "Drift away from this."),
                P("speed", "number", "m/s.", req: true), P("for", "number", "Seconds (omit for until removed).", min: 0),
                P("key", "text", "Replaces an earlier drift with the same key.", def: "drift"),
            ], targeted: true);
            Act("moveTo", "Move", "Walks it to a point; fires arrive there.",
                [P("at", "position", "Where.", req: true), P("speed", "number", "m/s (default: its own)."), P("then", "enum", "After arriving.", def: "resume", opts: ["resume", "hold"])], targeted: true);
            Act("setMovement", "Move", "Changes its movement block (see Movement).",
                [P("movement", "movement", "The new movement fields (merged).", req: true), P("for", "number", "Seconds; reverts after.")], targeted: true);
            Act("setTargeting", "Move", "Changes who it attacks and walks toward.",
            [
                P("targeting", "targeting", "Replace the whole list."), P("prepend", "targeting", "Put these first."), P("remove", "targeting", "Remove these entries."),
                P("for", "number", "Seconds; reverts after."),
            ], targeted: true, example: "{ \"do\": \"setTargeting\", \"target\": \"subject\", \"prepend\": [\"is:caravan\"] }");
            Act("taunt", "Move", "Forces aggro onto an entity (the game's agro mechanism).",
                [P("toward", "entity", "Who they go for.", req: true), P("for", "number", "Seconds.", def: 5)], targeted: true);
            Act("command", "Move", "Orders player units as the king's own commands.", [P("order", "enum", "The order.", req: true, opts: ["follow", "hold", "home"])], targeted: true);

            // ---- Logic (L1) -----------------------------------------------------------------------------------
            Act("signal", "Logic", "Sends a named signal (optionally with a number) to entities or everyone.",
            [
                P("name", "signal", "The signal's name (a built-in input such as open, enable, destroy, or your own).", req: true),
                P("to", "entities", "Who receives it: a selector, or \"all\" (every listener and the level script).", def: "self"),
                P("value", "number", "A number it carries (event.value).", def: 0),
                P("delay", "number", "Seconds before it arrives.", def: 0, min: 0),
            ], owner: "L1", example: "{ \"do\": \"signal\", \"name\": \"open\", \"to\": \"@gate1\" }");
            Act("setVar", "Logic", "Sets a variable (an entity var on target, or a level var: target level, or a name with $).",
            [
                P("name", "var", "The variable (declared in vars).", req: true), P("value", "any", "The new value (an expression).", req: true),
                P("target", "entities", "Whose variable (default self; \"level\" for a level var).", def: "self"),
            ], owner: "L1");
            Act("addVar", "Logic", "Adds to a number variable.",
            [
                P("name", "var", "The variable.", req: true), P("value", "number", "How much.", def: 1),
                P("target", "entities", "Whose variable (default self; \"level\" for a level var).", def: "self"),
            ], owner: "L1");
            Act("listAdd", "Logic", "Appends to a list variable (max 256; oldest dropped).",
                [P("name", "var", "The list variable.", req: true), P("value", "any", "The item.", req: true), P("target", "entities", "Whose variable.", def: "self")], owner: "L1");
            Act("listRemove", "Logic", "Removes the first equal item, or the item at an index.",
                [P("name", "var", "The list variable.", req: true), P("value", "any", "The item to remove."), P("index", "int", "Or the index to remove."), P("target", "entities", "Whose variable.", def: "self")], owner: "L1");
            Act("listClear", "Logic", "Empties a list variable.", [P("name", "var", "The list variable.", req: true), P("target", "entities", "Whose variable.", def: "self")], owner: "L1");
            Act("addLabel", "Logic", "Adds a label (filters test #label).", [P("label", "label", "The label.", req: true)], targeted: true, owner: "L1");
            Act("removeLabel", "Logic", "Removes a label.", [P("label", "label", "The label.", req: true)], targeted: true, owner: "L1");
            Act("enableRule", "Logic", "Turns a rule (by id) on.", [P("rule", "rule", "The rule's id.", req: true)], targeted: true, owner: "L1");
            Act("disableRule", "Logic", "Turns a rule (by id) off.", [P("rule", "rule", "The rule's id.", req: true)], targeted: true, owner: "L1");
            Act("enable", "Logic", "Switches the entity on: rules, components (solid, hittable) and visibility. On a spawn line, resumes spawning.", [], targeted: true, owner: "L1");
            Act("disable", "Logic", "Switches the entity off. On a spawn line, pauses its spawning.", [], targeted: true, owner: "L1");
            Act("toggle", "Logic", "Switches it on if off, off if on.", [], targeted: true, owner: "L1");
            Act("wait", "Logic", "The rest of this action list resumes later.", [P("seconds", "number", "Seconds (min 0.05).", req: true, min: 0.05)], owner: "L1");
            Act("if", "Logic", "Runs then or else.", [P("cond", "bool", "The condition.", req: true), P("then", "actions", "If true."), P("else", "actions", "If false.")], owner: "L1",
                example: "{ \"do\": \"if\", \"cond\": \"self.$hits % 5 == 0\", \"then\": [...] }");
            Act("repeat", "Logic", "Runs actions several times (event.value = the iteration).",
                [P("times", "int", "How many times (max 100).", req: true, min: 1, max: 100), P("every", "number", "Seconds between (0 = all now).", def: 0, min: 0), P("actions", "actions", "The actions (\"actions\", since \"do\" names the action itself).", req: true)], owner: "L1");
            Act("forEach", "Logic", "Runs actions for each member of a set, with `it` bound.",
                [P("in", "entities", "The set.", req: true), P("actions", "actions", "The actions (\"actions\", since \"do\" names the action itself).", req: true), P("limit", "int", "At most this many (max 256).", min: 1, max: 256)], owner: "L1");
            Act("choose", "Logic", "Picks one weighted option at random.", [P("options", "options", "[{ \"weight\": 2, \"do\": [...] }, ...]", req: true)], owner: "L1");
            Act("stop", "Logic", "Ends this action list.", [], owner: "L1");

            // ---- World ----------------------------------------------------------------------------------------
            Act("gold", "World", "Gives (or takes, negative) gold; floored at 0.", [P("amount", "number", "Gold.", req: true), P("at", "position", "Show the coin effect here.")]);
            Act("dropCoins", "World", "Drops real coins for the king to collect.", [P("count", "int", "How many.", req: true, min: 1, max: 100), P("at", "position", "Where.", def: "self")]);
            Act("building", "World", "Acts on a build slot.",
            [
                P("slot", "slot", "The slot: @name, a selector, or a Studio key (\"slot:Tower 3\").", req: true),
                P("op", "enum", "What to do.", req: true, opts: ["build", "upgrade", "knockOut", "repair", "demolish", "lock", "unlock", "setCost", "setIncome"]),
                P("value", "number", "For setCost/setIncome: the amount; for demolish: gold refunded."),
                P("branch", "int", "For build/upgrade: the upgrade choice (0-based)."),
            ]);
            Act("waves", "World", "Edits the night's waves.",
            [
                P("op", "enum", "What to do.", req: true, opts: ["addSpawn", "removeUnit", "scaleCount", "scaleHp", "clearRemaining", "startNight", "endNight", "skipNight"]),
                P("night", "night", "Which night (default: the next; \"current\" edits the running night)."),
                P("unit", "template", "addSpawn/removeUnit/scale*: the enemy."), P("count", "int", "addSpawn: how many.", min: 1),
                P("interval", "number", "addSpawn: seconds between spawns."), P("delay", "number", "addSpawn: seconds into the night."),
                P("line", "text", "addSpawn/clearRemaining: the spawn line."), P("gold", "int", "addSpawn: coins each."), P("elite", "bool", "addSpawn: elite."),
                P("value", "number", "scaleCount/scaleHp: the factor."),
            ], example: "{ \"do\": \"waves\", \"op\": \"addSpawn\", \"unit\": \"brute\", \"count\": \"night - 2\", \"line\": \"North\" }");
            Act("endLevel", "World", "Ends the run.", [P("result", "enum", "Win or lose.", req: true, opts: ["win", "lose"]), P("message", "text", "Shown on the end screen.")]);

            // ---- Show -----------------------------------------------------------------------------------------
            Act("effect", "Show", "Plays a game effect.", [P("fx", "fx", "The effect (`builder dump` lists them).", req: true), P("at", "position", "Where.", def: "self"), P("scale", "number", "Size.", def: 1)]);
            Act("sound", "Show", "Plays a sound.", [P("sound", "sound", "\"asset:<key>\" or a game sound.", req: true), P("at", "position", "Where (omit for UI sound).")]);
            Act("message", "Show", "Shows a message at the top of the screen.",
                [P("text", "text", "The text ({expr} parts).", req: true), P("kind", "enum", "Its emblem.", def: "info", opts: ["info", "good", "warn", "bad"]), P("seconds", "number", "How long.", def: 4)]);
            Act("floatText", "Show", "Floating text over a spot.", [P("text", "text", "The text.", req: true), P("at", "position", "Where.", def: "self"), P("color", "color", "#rrggbb.")]);
            Act("setVisual", "Show", "Changes its look.",
            [
                P("tint", "color", "#rrggbb."), P("scale", "number", "Size factor."), P("mesh", "text", "A model (mesh:/vanilla:/asset:)."),
                P("visible", "bool", "Shown or hidden."), P("glow", "color", "Glow colour."), P("for", "number", "Seconds; reverts after."),
            ], targeted: true);
            Act("hud", "Show", "Changes a level HUD line (logic.hud).",
                [P("id", "text", "The HUD line's id.", req: true), P("text", "text", "New text."), P("icon", "text", "New icon."), P("visible", "bool", "Shown or hidden.")]);
            Act("shake", "Show", "Shakes the camera.", [P("strength", "number", "How hard.", def: 1), P("seconds", "number", "How long.", def: 0.5)]);
            Act("marker", "Show", "A screen-edge marker like the game's wave markers.",
                [P("on", "entity", "What it points at.", req: true), P("icon", "text", "Icon."), P("for", "number", "Seconds (omit: until it's gone).")]);

            // ---- End ------------------------------------------------------------------------------------------
            Act("destroy", "End", "Removes it.",
                [P("effects", "bool", "Play death effects.", def: true), P("drops", "bool", "Drop its coins.", def: false), P("silent", "bool", "No death event.", def: false)], targeted: true);

            // ---- Stats ----------------------------------------------------------------------------------------
            foreach (var (n, d) in new[]
            {
                ("maxHp", "Maximum hit points."), ("hp", "Current hit points."), ("moveSpeed", "Movement speed, m/s."),
                ("attackCooldown", "Seconds between attacks."), ("attackRange", "Attack range, m."), ("damageMult", "Multiplies damage dealt (mods may carry from/types/chance/when)."),
                ("damageTaken", "Multiplies damage taken (mods may carry from/types/chance/when)."), ("projectileSpeed", "Projectile speed."),
                ("scale", "Size factor (visual and body)."), ("keepDistance", "Distance a ranged unit keeps."), ("aggroTime", "Seconds it chases after being hit."),
                ("income", "Gold a building pays at dawn."), ("timeScale", "Multiplies move speed, attack and rule timers, and its own projectiles."),
            }) Named(Stats, n, d, LType.Number);

            foreach (var (n, d) in new[]
            {
                ("stunned", "Can't move or attack."), ("disarmed", "Can't attack."), ("rooted", "Can't move."),
                ("untargetable", "Nothing targets it."), ("invulnerable", "Takes no damage."), ("silenced", "Its rules don't fire."),
                ("noIncome", "A building pays nothing at dawn."),
            }) Named(Flags, n, d);

            // ---- Globals --------------------------------------------------------------------------------------
            Named(Globals, "night", "The current night (1-based), or the coming one by day.", LType.Number);
            Named(Globals, "isNight", "True during a night.", LType.Bool);
            Named(Globals, "isDay", "True by day.", LType.Bool);
            Named(Globals, "nights", "The level's total number of nights.", LType.Number);
            Named(Globals, "time", "Seconds since level start.", LType.Number);
            Named(Globals, "nightTime", "Seconds since this night began.", LType.Number);
            Named(Globals, "nightProgress", "0..1: the night's spawn schedule elapsed (1 once everything has spawned).", LType.Number);
            Named(Globals, "gold", "The player's gold.", LType.Number);
            Named(Globals, "income", "The last dawn's payout.", LType.Number);
            Named(Globals, "enemiesAlive", "Enemies on the map.", LType.Number);
            Named(Globals, "difficulty", "The run's difficulty multiplier.", LType.Number);

            // ---- Properties -----------------------------------------------------------------------------------
            Prop(LType.Entity, "hp", LType.Number, "Hit points.");
            Prop(LType.Entity, "maxHp", LType.Number, "Maximum hit points.");
            Prop(LType.Entity, "hpPct", LType.Number, "hp / maxHp, 0..1.");
            Prop(LType.Entity, "alive", LType.Bool, "Not dead (a knocked-out building is not alive).");
            Prop(LType.Entity, "pos", LType.Position, "Its position.");
            Prop(LType.Entity, "x", LType.Number, "Its x."); Prop(LType.Entity, "y", LType.Number, "Its y (height)."); Prop(LType.Entity, "z", LType.Number, "Its z.");
            Prop(LType.Entity, "facing", LType.Number, "Its yaw in degrees (0 = north).");
            Prop(LType.Entity, "team", LType.Text, "'player', 'enemy' or 'neutral'.");
            Prop(LType.Entity, "kind", LType.Text, "'unit', 'building', 'prop', 'projectile', 'area', 'pickup', 'logic', 'coin', 'king', 'line', 'point', 'path'.");
            Prop(LType.Entity, "template", LType.Template, "The template it was made from.");
            Prop(LType.Entity, "labels", LType.List, "Its labels.");
            Prop(LType.Entity, "level", LType.Number, "A building's level (0 = built, 1 = first upgrade...).");
            Prop(LType.Entity, "cost", LType.Number, "A slot's next build/upgrade cost.");
            Prop(LType.Entity, "buildingType", LType.Text, "A building's kind, e.g. 'Mill'.");
            Prop(LType.Entity, "age", LType.Number, "Seconds since it appeared.");
            Prop(LType.Entity, "target", LType.Entity, "Its current attack/move target.");
            Prop(LType.Entity, "owner", LType.Entity, "Who spawned or launched it.");
            Prop(LType.Entity, "id", LType.Text, "Its entity id.");
            foreach (var s in Stats.Values)
                if (Property(LType.Entity, s.Name) == null) Prop(LType.Entity, s.Name, LType.Number, s.Description + " (with modifiers)");
            Prop(LType.Position, "x", LType.Number, "x."); Prop(LType.Position, "y", LType.Number, "y."); Prop(LType.Position, "z", LType.Number, "z.");
            Prop(LType.Set, "len", LType.Number, "How many."); Prop(LType.Set, "first", LType.Entity, "The first (nearest)."); Prop(LType.Set, "last", LType.Entity, "The last (farthest).");
            Prop(LType.List, "len", LType.Number, "How many."); Prop(LType.List, "first", LType.Any, "The first item."); Prop(LType.List, "last", LType.Any, "The last item.");
            Prop(LType.Template, "id", LType.Text, "The template's id.");
            Prop(LType.Template, "hp", LType.Number, "The template's max hp (catalog).");
            Prop(LType.Event, "value", LType.Any, "The event's number (see each event).");
            Prop(LType.Event, "pos", LType.Position, "Where it happened.");
            Prop(LType.Event, "name", LType.Text, "signal: the name; varChanged: the var; status events: the status.");
            Prop(LType.Event, "choice", LType.Text, "interact with choices: the chosen id.");
            Prop(LType.Event, "result", LType.Text, "levelEnd: 'win' or 'lose'.");

            // ---- Functions ------------------------------------------------------------------------------------
            Fn("vec", LType.Position, "A position: vec(x, z) on the ground, or vec(x, y, z).", ["number", "number", "number?"]);
            foreach (var (n, d) in new[] { ("nearest", "The nearest match"), ("farthest", "The farthest match"), ("weakest", "The match with the least hp"), ("strongest", "The match with the most hp"), ("random", "A random match") })
                Fn(n, LType.Entity, d + " within r metres (anywhere if omitted), or none. Anchored at self; e." + n + "(...) anchors at e.", ["filter", "number?"], method: true,
                    example: n + "('foe and Tower', 20)");
            Fn("all", LType.Set, "Every match within r metres (anywhere if omitted), nearest first (max 256).", ["filter", "number?"], method: true, example: "all('ally and unit', 6)");
            Fn("best", LType.Entity, "The match with the highest score (an expression over `it`).", ["filter", "number", "lazy"], method: true, example: "best('foe', 20, -it.hp)");
            Fn("count", LType.Number, "How many match within r metres (anywhere if omitted).", ["filter", "number?"], method: true, example: "count('enemy and in:self')");
            Fn("pointNear", LType.Position, "A random walkable point within r metres of p.", ["position", "number"]);
            Fn("ahead", LType.Position, "d metres ahead of e (along its facing).", ["entity", "number"]);
            Fn("toward", LType.Position, "d metres from a toward b.", ["position", "position", "number"]);
            Fn("lerp", LType.Any, "a + (b - a) * t, for numbers or positions.", ["any", "any", "number"]);
            Fn("spawnLine", LType.Position, "The centre of a spawn line by name.", ["text"]);
            Fn("dir", LType.Position, "A unit vector at deg degrees (0 = north, 90 = east).", ["number"]);
            Fn("angle", LType.Number, "Degrees from a to b (0 = north).", ["position", "position"]);
            Fn("dist", LType.Number, "Distance between two positions or entities; e.dist(x) from e.", ["position", "position?"], method: true);
            Fn("min", LType.Number, "The smallest.", ["number", "number", "..."]); Fn("max", LType.Number, "The largest.", ["number", "number", "..."]);
            foreach (var n in new[] { "abs", "floor", "ceil", "round", "sqrt", "sin", "cos" }) Fn(n, LType.Number, n + "(x)" + (n is "sin" or "cos" ? ", x in degrees." : "."), ["number"]);
            Fn("clamp", LType.Number, "x limited to lo..hi.", ["number", "number", "number"]);
            Fn("pow", LType.Number, "x to the power y.", ["number", "number"]);
            Fn("rand", LType.Number, "rand(): 0..1; rand(a, b): a..b (the level's seeded random).", ["number?", "number?"]);
            Fn("randInt", LType.Number, "A whole number a..b inclusive.", ["number", "number"]);
            Fn("chance", LType.Bool, "True with probability p.", ["number"]);
            Fn("waveCount", LType.Number, "Enemies night n will spawn, optionally only templates matching a filter.", ["number", "filter?"]);
            Fn("waveUnits", LType.List, "The templates night n spawns.", ["number"]);
            Fn("strongestUnit", LType.Template, "Night n's strongest template (hp x damage).", ["number"]);
            Fn("stacks", LType.Number, "Stacks of a status on it.", ["status"], method: true, onlyOn: LType.Entity);
            Fn("is", LType.Bool, "Whether it matches a filter.", ["filter"], method: true, onlyOn: LType.Entity);
            Fn("inside", LType.Bool, "Whether it is inside an area.", ["entity"], method: true, onlyOn: LType.Entity);
            Fn("at", LType.Any, "The item at index i (0-based).", ["number"], method: true, onlyOn: LType.List);
            Fn("has", LType.Bool, "Whether the list or set contains x.", ["any"], method: true, onlyOn: LType.List);

            // ---- Filter terms ---------------------------------------------------------------------------------
            foreach (var (n, d) in new[]
            {
                ("foe", "The other side from self (for the level script: enemies)."), ("ally", "Self's side (level script: the player's)."),
                ("player", "The player's side."), ("enemy", "The enemy side."), ("neutral", "Neither side."),
                ("unit", "Units (not buildings)."), ("building", "Buildings."), ("prop", "Props."), ("projectile", "Projectiles."), ("area", "Areas."),
                ("pickup", "Pickups."), ("coin", "The game's coins."), ("king", "The king."), ("castle", "The castle."), ("self", "The rule's owner."),
                ("melee", "Melee fighters."), ("ranged", "Ranged fighters."), ("alive", "Alive."), ("dead", "Dead or knocked out."),
                ("logic", "Logic objects."), ("line", "Spawn lines."), ("point", "Points."), ("path", "Paths."),
                ("#label", "Has a label."), ("is:<template>", "Made from a template."), ("in:@area", "Inside an area (in:self inside this area)."),
                ("has:<status>", "Has a status."), ("type:<Kind>", "A building of that kind (Mill, Tower...)."), ("where(expr)", "An expression over `it`."),
                ("<Tag>", "Any game tag (TagManager.ETag, case-sensitive): Tower, Flying, Wall, ..."),
            }) Named(FilterTerms, n, d);

            // ---- Built-in inputs ------------------------------------------------------------------------------
            Inputs["any"] = ["enable", "disable", "toggle", "destroy"];
            Inputs["mover"] = ["start", "stop", "reverse", "goto"];
            Inputs["area"] = ["show", "hide"];
            Inputs["interact"] = ["lock", "unlock"];
        }

        public static readonly string[] ParamTypes =
        [
            "number", "int", "bool", "text", "position", "entity", "entities", "template", "filter", "status", "rule", "var", "signal",
            "label", "enum", "actions", "options", "targeting", "movement", "any", "for", "night", "slot", "types", "keep", "fx", "sound", "color", "json",
        ];

        /// <summary>The whole registry as JSON (logic-schema.json, written by `builder dump`).</summary>
        public static JObject Dump()
        {
            var o = new JObject { ["version"] = V1 };
            o["events"] = new JArray(Events.Values.Select(e =>
            {
                var j = new JObject { ["name"] = e.Name, ["group"] = e.Group, ["description"] = e.Description, ["params"] = new JArray(e.Params.Select(p => p.ToJson())) };
                if (e.Other != null) j["other"] = e.Other;
                if (e.Value != LType.Void) { j["value"] = TypeName(e.Value); j["valueMeaning"] = e.ValueMeaning; }
                if (e.HasPos) j["pos"] = true;
                if (e.Fields.Count > 0) j["fields"] = new JObject(e.Fields.Select(f => new JProperty(f.Key, TypeName(f.Value))));
                if (e.Aliases.Count > 0) j["aliases"] = JObject.FromObject(e.Aliases);
                j["owners"] = new JArray(e.Owners);
                if (e.Global) j["global"] = true;
                if (e.Example != null) j["example"] = e.Example;
                j["since"] = e.Since;
                return j;
            }));
            o["actions"] = new JArray(Actions.Values.Select(a =>
            {
                var j = new JObject { ["name"] = a.Name, ["group"] = a.Group, ["description"] = a.Description, ["params"] = new JArray(a.Params.Select(p => p.ToJson())) };
                if (a.Name == "setStat") ((JObject)j["params"][1])["options"] = new JArray(Stats.Keys);
                if (a.Example != null) j["example"] = a.Example;
                j["since"] = a.Since;
                return j;
            }));
            o["functions"] = new JArray(Functions.Values.Select(f =>
            {
                var j = new JObject { ["name"] = f.Name, ["args"] = new JArray(f.Args), ["returns"] = TypeName(f.Returns), ["description"] = f.Description };
                if (f.Method) j["method"] = true;
                if (f.OnlyOn is LType on) j["onlyOn"] = TypeName(on);
                if (f.Example != null) j["example"] = f.Example;
                return j;
            }));
            o["properties"] = new JArray(Properties.Select(p => new JObject { ["on"] = TypeName(p.On), ["name"] = p.Name, ["returns"] = TypeName(p.Returns), ["description"] = p.Description }));
            JArray Named(Dictionary<string, NamedSpec> d, bool typed) => new(d.Values.Select(n =>
            {
                var j = new JObject { ["name"] = n.Name, ["description"] = n.Description };
                if (typed) j["type"] = TypeName(n.Type);
                return j;
            }));
            o["globals"] = Named(Globals, true);
            o["roles"] = Named(Roles, true);
            o["stats"] = Named(Stats, false);
            o["flags"] = Named(Flags, false);
            o["filterTerms"] = Named(FilterTerms, false);
            o["inputs"] = JObject.FromObject(Inputs);
            o["paramTypes"] = new JArray(ParamTypes);
            o["tags"] = new JArray(Tags.OrderBy(t => t, StringComparer.Ordinal));
            o["caps"] = new JObject(LogicCapsDef.Defaults.Fields().Select(f => new JProperty(f.Name, new JObject { ["default"] = f.Default, ["max"] = f.Max })));
            return o;
        }

        /// <summary>Dump() plus the built-in presets: agent-kit/logic-schema.json and catalog.json's "logic".</summary>
        public static JObject DumpWithPresets()
        {
            var o = Dump();
            o["presets"] = JArray.FromObject(Presets.BuiltIn, Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }));
            return o;
        }

        public static string TypeName(LType t) => t switch
        {
            LType.Any => "any", LType.Number => "number", LType.Bool => "bool", LType.Text => "text", LType.Position => "position",
            LType.Entity => "entity", LType.Set => "set", LType.List => "list", LType.Template => "template", LType.Level => "level",
            LType.Event => "event", _ => "none",
        };
    }
}
