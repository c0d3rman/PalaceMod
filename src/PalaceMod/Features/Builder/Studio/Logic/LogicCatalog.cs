using System;
using System.Collections.Generic;
using System.Linq;

/*
    The Studio's view of the behaviour engine's vocabulary (docs/builder-engine.md, "Events", "Actions"): for each event
    and action, the words the cards use, the picker category, a one-line description and its typed params.

    The engine's schema registry (Model/Logic/LogicSchema, L4) is the source of truth for names, params and types; this
    adds what only the editor needs (phrasing, categories, which params a card shows inline). Until the registry lands,
    the params here are written from the doc; LogicSchemaBridge merges the registry's entries over them when it exists,
    so an event or action the registry has and this table doesn't still gets a (plainer) card.

    Pure C# (no Unity), so the unit tests cover it.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    /// <summary>How a param is edited.</summary>
    internal enum ParamKind
    {
        Number,     // a number field, with ƒ to switch to an expression
        Bool,
        Text,       // free text with {…} interpolation
        Template,   // a unit or entity template (palette tiles); "=expr" allowed
        Entity,     // one entity: a role, @name, or a selector
        Target,     // one entity or a set (actions' target)
        Position,   // an entity, a role or a position expression
        Filter,     // the filter mini-language
        Condition,  // a bool expression
        Expr,       // any expression
        Choice,     // one of Options
        Actions,    // a nested action list
        Status,     // a status id
        Rule,       // a rule id on the target
        Signal,     // a signal name
        Var,        // a variable name (literal, checked)
        Stat,       // a stat name
        Fx,         // a game effect
        Sound,
        Labels,     // a list of label strings
        Json,       // anything else: edited as JSON text
    }

    internal sealed class ParamUi
    {
        public string Name, Label, Desc;
        public ParamKind Kind;
        public string Default;
        public string[] Options = [];
        public bool Required;
        /// <summary>Shown on the card's line (else only in the "more" fold).</summary>
        public bool Inline;
    }

    internal sealed class EventUi
    {
        public string Name;
        /// <summary>Card phrase after "When": "this dies", "every {seconds} s".</summary>
        public string Phrase;
        public string Category, Desc;
        /// <summary>What `other` is for this event ("killer", "attacker"), or null when it has none.</summary>
        public string Other;
        /// <summary>What `event.value` is, or null.</summary>
        public string Value;
        public bool HasPos;
        /// <summary>Whether `of` (listen to other entities) makes sense.</summary>
        public bool CanOf = true;
        /// <summary>Only on these owner kinds (null: any).</summary>
        public string[] Owners;
        public List<ParamUi> Params = [];
        public bool Common;
    }

    internal sealed class ActionUi
    {
        public string Name, Label, Category, Desc;
        /// <summary>Card sentence with {param} holes: "Spawn {entity} × {count} at {at}".</summary>
        public string Sentence;
        public List<ParamUi> Params = [];
        /// <summary>Has a `target` param (default self).</summary>
        public bool Targeted;
        /// <summary>The nested action lists it carries ("then", "else", "do", "onImpact").</summary>
        public string[] Nested = [];
        /// <summary>Whether it can change things outside its owner (gold, waves, other entities, the level's end): drives
        /// the "affects the level" badge on shared enemies.</summary>
        public bool LevelWide;
        public bool Common;
    }

    internal static class LogicCatalog
    {
        public static readonly string[] EventCategories = ["This", "Combat", "Time", "Night", "Nearby", "Areas", "Movement", "Buildings", "Gold", "King", "Signals", "Variables", "Level"];
        public static readonly string[] ActionCategories = ["Make", "Health", "Stats", "Move", "Links", "Logic", "World", "Show", "End"];

        public static readonly List<EventUi> Events = [];
        public static readonly List<ActionUi> Actions = [];
        private static readonly Dictionary<string, EventUi> eventByName = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ActionUi> actionByName = new(StringComparer.Ordinal);

        public static EventUi Event(string name) => name != null && eventByName.TryGetValue(name, out var e) ? e : null;
        public static ActionUi Action(string name) => name != null && actionByName.TryGetValue(name, out var a) ? a : null;

        /// <summary>Role names that may stand for `other`/`subject` per event (the doc's aliases).</summary>
        public static readonly string[] Aliases = ["killer", "victim", "attacker", "target", "entrant", "collector", "sender"];

        public static readonly string[] Stats = ["maxHp", "hp", "moveSpeed", "attackCooldown", "attackRange", "damageMult", "damageTaken", "projectileSpeed", "scale", "keepDistance", "aggroTime", "income", "timeScale"];
        public static readonly string[] Flags = ["stunned", "disarmed", "rooted", "untargetable", "invulnerable", "silenced", "noIncome"];

        public static readonly Dictionary<string, string> StatDescriptions = new()
        {
            ["maxHp"] = "Most hit points.",
            ["hp"] = "Current hit points.",
            ["moveSpeed"] = "Walking speed, m/s.",
            ["attackCooldown"] = "Seconds between attacks (lower is faster).",
            ["attackRange"] = "How far it attacks from, m.",
            ["damageMult"] = "Multiplies the damage it deals.",
            ["damageTaken"] = "Multiplies the damage it takes.",
            ["projectileSpeed"] = "Speed of its shots.",
            ["scale"] = "Size.",
            ["keepDistance"] = "How far it stands off from its target, m.",
            ["aggroTime"] = "How long it keeps chasing a target.",
            ["income"] = "Gold a building pays at dawn.",
            ["timeScale"] = "Speeds up or slows everything it does.",
        };

        static LogicCatalog()
        {
            // ---- Events ------------------------------------------------------------------------------------------
            Ev("spawn", "appears", "This", "When it appears: placed ones at level start, spawned ones when created.", other: "spawner", common: true);
            Ev("death", "dies", "This", "When it dies (a building: knocked out).", other: "killer", common: true);
            Ev("damaged", "is damaged", "Combat", "When it takes damage.", other: "attacker", value: "damage", ps: ["min:number"], common: true);
            Ev("healed", "is healed", "Combat", "When it is healed.", other: "healer", value: "amount");
            Ev("hpBelow", "drops below {pct} health", "Combat", "Once each time its health falls below a fraction (re-arms when healed above).", value: "health fraction", ps: ["pct:number=0.5!*"]);
            Ev("attack", "attacks", "Combat", "Whenever it attacks (vanilla attacks too).", other: "target", ps: ["attack:text"]);
            Ev("hit", "hits something", "Combat", "When its attack, shot or damage action damages something.", other: "victim", value: "damage", common: true);
            Ev("kill", "kills something", "Combat", "When something it damaged dies from that damage.", other: "victim", common: true);
            Ev("every", "every {seconds} s", "Time", "Repeatedly, from when it appears.", value: "tick count", ps: ["seconds:number=1!*", "during:choice(always|night|day)=always*", "jitter:number", "phase:number"], ofless: true, common: true);
            Ev("after", "{seconds} s after it appears", "Time", "Once, a while after it appears.", ps: ["seconds:number=1!*"], ofless: true);
            Ev("near", "a {filter} comes within {range} m", "Nearby", "When a matching thing comes close (or leaves).", other: "the one that came", ps: ["filter:filter=foe!*", "range:number=6!*", "edge:choice(enter|exit|both)=enter"], ofless: true, common: true);
            Ev("enter", "something enters", "Areas", "Something enters this area.", other: "entrant", owners: ["area"], ofless: true);
            Ev("exit", "something leaves", "Areas", "Something leaves this area.", other: "the one that left", owners: ["area"], ofless: true);
            Ev("inside", "something stays inside", "Areas", "For each thing inside, after every full tick it spends inside.", other: "the one inside", owners: ["area"], ofless: true);
            Ev("empty", "becomes empty", "Areas", "The last thing inside leaves.", owners: ["area"], ofless: true);
            Ev("occupied", "becomes occupied", "Areas", "The first thing enters.", owners: ["area"], ofless: true);
            Ev("impact", "hits (as a shot)", "Combat", "A projectile strikes something or the ground.", other: "thing it hit", pos: true);
            Ev("expire", "runs out", "This", "Its lifetime or range runs out.");
            Ev("arrive", "reaches a point", "Movement", "A mover or path follower reaches a point.", value: "point index");
            Ev("bump", "is stopped by a wall", "Movement", "A push or knockback is stopped short by a wall, building, solid body or cliff.", other: "what it hit", value: "push left (m)");
            Ev("end", "reaches the end", "Movement", "A mover or path follower reaches its end.", value: "point index");
            Ev("pickup", "is picked up", "King", "A pickup is collected.", other: "collector");
            Ev("interact", "is used by the king", "King", "The king uses it (after paying its cost).", other: "king", common: true);
            Ev("signal", "gets signal {name}", "Signals", "It receives a named signal (wires send these).", other: "sender", value: "the signal's number", ps: ["name:signal!*"], ofless: true, common: true);
            Ev("varChanged", "variable {name} changes", "Variables", "One of its variables changes (on the level script: a level variable).", value: "new value", ps: ["name:var*"], ofless: true);
            Ev("statusStart", "gains a status", "This", "A status starts on it.", other: "applier", value: "stacks", ps: ["status:status"]);
            Ev("statusEnd", "loses a status", "This", "A status ends (runs out or is removed).", other: "applier", value: "stacks", ps: ["status:status"]);
            Ev("statusTick", "status ticks", "This", "Every tick of a status it has.", other: "applier", value: "stacks", ps: ["status:status"]);
            Ev("linked", "is linked to a parent", "This", "It becomes a part of another entity (link, a template's parts).", other: "parent");
            Ev("unlinked", "is unlinked", "This", "It stops being a part (unlink, or its parent died and it dropped off).", other: "old parent");
            Ev("interactRefused", "is refused (short of payment)", "King", "The king tried to use it without enough gold or items.", other: "king", value: "gold cost");
            Ev("targetChanged", "changes target", "Combat", "Its attack or move target changes.", other: "new target");
            Ev("built", "is built", "Buildings", "The player builds it (build slots).", value: "level", ps: ["level:number"]);
            Ev("upgraded", "is upgraded", "Buildings", "The player upgrades it.", value: "level", ps: ["level:number"]);
            Ev("repaired", "is repaired", "Buildings", "It is revived at dawn.");
            Ev("coinDrop", "drops a coin", "Gold", "A coin drops from a death.", other: "the dead unit");
            Ev("coinPickup", "coin is collected", "Gold", "The king collects a coin.", other: "king");
            Ev("lineSpawn", "a spawn line spawns", "Night", "A spawn line spawns an enemy.", other: "the new enemy", ps: ["line:text"], ofless: true);
            Ev("levelStart", "the level starts", "Level", "The level has loaded, before day 1.", ofless: true, common: true);
            Ev("dusk", "dusk falls", "Night", "Evening, before the night's enemies come.", value: "night", ps: ["night:number"], ofless: true, common: true);
            Ev("nightStart", "the night starts", "Night", "The first enemy of the night spawns.", value: "night", ps: ["night:number"], ofless: true);
            Ev("dawn", "dawn breaks", "Night", "The night is over (after income is paid).", value: "night", ps: ["night:number"], ofless: true, common: true);
            Ev("day", "the day starts", "Night", "The sun is up.", value: "night", ps: ["night:number"], ofless: true);
            Ev("waveCleared", "the wave is cleared", "Night", "Spawning is done and no enemies are left.", value: "night", ofless: true);
            Ev("goldChanged", "gold changes", "Gold", "The player's balance changes.", value: "change", ofless: true);
            Ev("kingAction", "the king uses {action}", "King", "The player presses one of the level's action keys.", other: "king", ps: ["action:text!*"], ofless: true);
            Ev("levelEnd", "the level ends", "Level", "Win or loss, before the end screen.", ps: ["result:choice(win|lose)"], ofless: true);

            // ---- Actions -----------------------------------------------------------------------------------------
            Act("spawn", "Spawn", "Make", "Spawn {entity} × {count} at {at}", "Creates copies of a template (enemies count toward the night).",
                ["entity:template!*", "count:number=1*", "at:position=self*", "spread:number", "team:choice(template|player|enemy|neutral|owner)", "elite:choice(no|yes|inherit)", "facing:number", "then:actions"], nested: ["then"], levelWide: true, common: true);
            Act("launch", "Launch", "Make", "Launch {entity} at {toward}", "Fires a projectile template from here.",
                ["entity:template!*", "toward:position=other*", "from:position", "speed:number", "homing:number", "arc:number", "pierce:number", "onImpact:actions", "then:actions"], nested: ["onImpact", "then"], common: true);
            Act("spawnGroup", "Spawn a group", "Make", "Spawn group {unit} × {count} on {line}", "A wave-style group now; counts toward the night.",
                ["unit:template!*", "count:number=5*", "interval:number=0.5", "line:text*", "at:position", "elite:choice(no|yes|inherit)", "gold:number"], levelWide: true);
            Act("replace", "Turn into", "Make", "Turn {target} into {with}", "Swaps it for another template in place.", ["with:template!*", "keep:labels"], targeted: true);
            Act("damage", "Damage", "Health", "Damage {target} by {amount}", "Deals damage.", ["amount:number=10!*", "source:entity", "types:labels", "splash:bool", "weapon:text"], targeted: true, common: true);
            Act("heal", "Heal", "Health", "Heal {target} by {amount}", "Restores hit points.", ["amount:number=10!*"], targeted: true, common: true);
            Act("kill", "Kill", "Health", "Kill {target}", "Kills it outright.", [], targeted: true);
            Act("revive", "Revive", "Health", "Revive {target}", "Brings a knocked-out building back.", ["hpPct:number=1"], targeted: true);
            Act("setHp", "Set health", "Health", "Set {target} health to {value}", "Sets hit points without a damaged or healed event.", ["value:number!*"], targeted: true);
            Act("setStat", "Change a stat", "Stats", "Set {target} {stat} {mult}{add}{value} for {for}", "Changes a stat, for a while or for good.",
                ["stat:stat!*", "mult:number*", "add:number*", "value:number*", "for:expr*", "key:text"], targeted: true, common: true);
            Act("applyStatus", "Give a status", "Stats", "Give {target} {status} for {for}", "Applies a status (a buff or debuff).", ["status:status!*", "for:number*", "stacks:number", "source:entity"], targeted: true, common: true);
            Act("removeStatus", "Remove a status", "Stats", "Remove {status} from {target}", "Ends a status.", ["status:status!*"], targeted: true);
            Act("setWeapon", "Change weapon", "Stats", "Give {target} weapon {weapon} for {for}", "Swaps its weapon: vanilla:<weapon>, a copy of another's (=other), none, or base (its own back). Range follows the weapon.",
                ["weapon:text=none!*", "range:number", "for:expr*"], targeted: true);
            Act("convert", "Switch sides", "Stats", "Make {target} join {team}", "Changes its team (a dead target rises on the new side).", ["team:choice(player|enemy|neutral)!*", "keepLook:bool", "hpPct:number"], targeted: true, levelWide: true);
            Act("teleport", "Teleport", "Move", "Teleport {target} to {at}", "Moves it instantly.", ["at:position!*"], targeted: true);
            Act("push", "Push", "Move", "Push {target} {force} m", "A one-off shove (or a pull with toward).", ["force:number=4!*", "from:position", "toward:position", "up:number", "stun:number"], targeted: true);
            Act("drift", "Drift", "Move", "Drift {target} at {speed} m/s", "A steady pull, current or wind.", ["speed:number=2!*", "dir:expr", "toward:position", "from:position", "for:number", "key:text"], targeted: true);
            Act("moveTo", "Walk to", "Move", "Send {target} to {at}", "Walks it to a point; fires arrive there.", ["at:position!*", "speed:number", "then:choice(resume|hold)"], targeted: true);
            Act("setMovement", "Change movement", "Move", "Change how {target} moves", "Replaces its movement block (for a while).", ["movement:json!*", "for:number"], targeted: true);
            Act("setTargeting", "Change targeting", "Move", "Change who {target} goes for", "Replaces or extends its target priorities.", ["targeting:json", "prepend:json", "remove:json", "for:number"], targeted: true);
            Act("taunt", "Taunt", "Move", "Make {target} attack {toward}", "Forces their aggro onto something.", ["toward:entity!*", "for:number=5*"], targeted: true);
            Act("command", "Order troops", "Move", "Order {target} to {order}", "Player units, as the king's own commands.", ["order:choice(follow|hold|home)!*"], targeted: true);
            Act("link", "Attach as a part", "Links", "Attach {target} to {to}", "Makes it a part of another entity: it moves with it, may pass on its damage, and follows it in death.",
                ["to:entity!*", "offset:position", "rotY:number", "follow:choice(rigid|position|none)=rigid*", "onParentDeath:choice(die|drop|stay)=die", "damageToParent:number", "damageToGroup:number", "damageKept:number"], targeted: true);
            Act("unlink", "Detach", "Links", "Detach {target}", "It stops being a part (with children: its parts drop off).", ["release:choice(drop|stay)=drop*", "children:bool"], targeted: true);
            Act("signal", "Send a signal", "Logic", "Send {name} to {to}", "Sends a named signal (with an optional number).", ["name:signal!*", "to:target=self*", "value:expr", "delay:number"], common: true);
            Act("setVar", "Set a variable", "Logic", "Set {name} to {value}", "Sets a level or entity variable.", ["name:var!*", "value:expr!*"], targeted: true, common: true);
            Act("addVar", "Add to a variable", "Logic", "Add {value} to {name}", "Adds to a number variable.", ["name:var!*", "value:expr=1!*"], targeted: true, common: true);
            Act("listAdd", "Add to a list", "Logic", "Add {value} to list {name}", "Appends to a list variable.", ["name:var!*", "value:expr!*"], targeted: true);
            Act("listRemove", "Remove from a list", "Logic", "Remove {value} from list {name}", "Removes an item from a list variable.", ["name:var!*", "value:expr", "index:number"], targeted: true);
            Act("listClear", "Clear a list", "Logic", "Clear list {name}", "Empties a list variable.", ["name:var!*"], targeted: true);
            Act("addLabel", "Add a label", "Logic", "Label {target} #{label}", "Tags it with a label filters can test.", ["label:text!*"], targeted: true);
            Act("removeLabel", "Remove a label", "Logic", "Unlabel {target} #{label}", "Removes a label.", ["label:text!*"], targeted: true);
            Act("enableRule", "Turn a rule on", "Logic", "Turn on rule {rule} of {target}", "Enables a rule by id.", ["rule:rule!*"], targeted: true);
            Act("disableRule", "Turn a rule off", "Logic", "Turn off rule {rule} of {target}", "Disables a rule by id.", ["rule:rule!*"], targeted: true);
            Act("enable", "Switch on", "Logic", "Switch on {target}", "Turns its rules, parts and visibility on.", [], targeted: true);
            Act("disable", "Switch off", "Logic", "Switch off {target}", "Turns its rules, parts and visibility off (a spawn line pauses).", [], targeted: true);
            Act("toggle", "Toggle", "Logic", "Toggle {target}", "Switches it on if off, off if on.", [], targeted: true);
            Act("wait", "Wait", "Logic", "Wait {seconds} s", "The rest of the list runs later.", ["seconds:number=1!*"], common: true);
            Act("if", "If", "Logic", "If {cond}", "Runs one list or the other.", ["cond:condition!*", "then:actions", "else:actions"], nested: ["then", "else"], common: true);
            Act("repeat", "Repeat", "Logic", "Repeat {times}× every {every} s", "Runs a list several times.", ["times:number=3!*", "every:number=0*", "do:actions"], nested: ["do"]);
            Act("forEach", "For each", "Logic", "For each in {in}", "Runs a list for every member of a set (as `it`).", ["in:target!*", "limit:number", "do:actions"], nested: ["do"]);
            Act("choose", "Pick one at random", "Logic", "Pick one at random", "Runs one weighted option.", ["options:json!"]);
            Act("stop", "Stop", "Logic", "Stop", "Ends this action list.", []);
            Act("gold", "Give gold", "World", "Give {amount} gold", "Gives (or with a minus, takes) gold.", ["amount:number=5!*", "at:position"], levelWide: true, common: true);
            Act("dropCoins", "Drop coins", "World", "Drop {count} coins at {at}", "Real coins the king collects.", ["count:number=3!*", "at:position=self*"], levelWide: true);
            Act("building", "Change a building", "World", "{op} {slot}", "Builds, upgrades, knocks out, locks or reprices a slot.",
                ["slot:entity!*", "op:choice(build|upgrade|knockOut|repair|demolish|lock|unlock|setCost|setIncome)=build!*", "value:number", "branch:number"], levelWide: true);
            Act("waves", "Change the nights", "World", "Nights: {op}", "Adds, removes or scales enemies of a night, or starts/ends one.",
                ["op:choice(addSpawn|removeUnit|scaleCount|scaleHp|clearRemaining|startNight|endNight|skipNight)=addSpawn!*", "night:expr", "unit:template", "count:number", "interval:number", "delay:number", "line:text", "gold:number", "elite:bool", "value:number", "survivors:choice(remove|stay)"], levelWide: true);
            Act("endLevel", "End the level", "World", "End the level: {result}", "Wins or loses the run.", ["result:choice(win|lose)=win!*", "message:text"], levelWide: true);
            Act("effect", "Effect", "Show", "Effect {fx} at {at}", "Plays a game effect.", ["fx:fx!*", "at:position=self*", "scale:number"], common: true);
            Act("sound", "Sound", "Show", "Sound {sound}", "Plays a sound.", ["sound:sound!*", "at:position"]);
            Act("message", "Message", "Show", "Say \"{text}\"", "A message at the top of the screen.", ["text:text!*", "kind:choice(info|good|bad)", "seconds:number"], common: true);
            Act("floatText", "Floating text", "Show", "Float \"{text}\" at {at}", "Text that rises from a spot.", ["text:text!*", "at:position=self*", "color:text"]);
            Act("setVisual", "Change look", "Show", "Change how {target} looks", "Tint, size, mesh, visibility, glow.", ["tint:text", "scale:number", "mesh:text", "visible:bool", "glow:bool", "for:number"], targeted: true);
            Act("hud", "HUD line", "Show", "HUD {id}: \"{text}\"", "Changes a line of the level's HUD.", ["id:text!*", "text:text*", "icon:text", "visible:condition"], levelWide: true);
            Act("shake", "Shake", "Show", "Shake the camera", "Shakes the camera.", ["strength:number=1", "seconds:number=0.5"]);
            Act("marker", "Marker", "Show", "Mark {on}", "A screen-edge marker like the wave markers.", ["on:entity!*", "icon:text", "for:number"]);
            Act("destroy", "Destroy", "End", "Destroy {target}", "Removes it (death effects, optional coins).", ["effects:bool", "drops:bool", "silent:bool"], targeted: true, common: true);
        }

        // spec: "name:kind[(a|b)][=default][!][*]"   ! required, * inline on the card
        internal static ParamUi Param(string spec)
        {
            bool inline = spec.EndsWith("*");
            if (inline) spec = spec.Substring(0, spec.Length - 1);
            bool required = spec.EndsWith("!");
            if (required) spec = spec.Substring(0, spec.Length - 1);
            string def = null;
            int eq = spec.IndexOf('=');
            if (eq >= 0) { def = spec.Substring(eq + 1); spec = spec.Substring(0, eq); }
            int colon = spec.IndexOf(':');
            string name = spec.Substring(0, colon), kind = spec.Substring(colon + 1);
            string[] options = [];
            int paren = kind.IndexOf('(');
            if (paren >= 0)
            {
                options = kind.Substring(paren + 1, kind.Length - paren - 2).Split('|');
                kind = kind.Substring(0, paren);
            }
            return new ParamUi
            {
                Name = name, Label = Humanize(name), Kind = KindOf(kind), Default = def, Options = options,
                Required = required, Inline = inline,
            };
        }

        private static ParamKind KindOf(string k) => k switch
        {
            "number" => ParamKind.Number,
            "bool" => ParamKind.Bool,
            "text" => ParamKind.Text,
            "template" => ParamKind.Template,
            "entity" => ParamKind.Entity,
            "target" => ParamKind.Target,
            "position" => ParamKind.Position,
            "filter" => ParamKind.Filter,
            "condition" => ParamKind.Condition,
            "expr" => ParamKind.Expr,
            "choice" => ParamKind.Choice,
            "actions" => ParamKind.Actions,
            "status" => ParamKind.Status,
            "rule" => ParamKind.Rule,
            "signal" => ParamKind.Signal,
            "var" => ParamKind.Var,
            "stat" => ParamKind.Stat,
            "fx" => ParamKind.Fx,
            "sound" => ParamKind.Sound,
            "labels" => ParamKind.Labels,
            _ => ParamKind.Json,
        };

        /// <summary>"hpPct" → "Hp pct", "onImpact" → "On impact".</summary>
        public static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c)) { sb.Append(' '); sb.Append(char.ToLowerInvariant(c)); }
                else sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
            return sb.ToString();
        }

        private static void Ev(string name, string phrase, string category, string desc, string other = null, string value = null, string[] ps = null,
            bool ofless = false, bool pos = false, string[] owners = null, bool common = false)
        {
            var e = new EventUi
            {
                Name = name, Phrase = phrase, Category = category, Desc = desc, Other = other, Value = value, HasPos = pos,
                CanOf = !ofless, Owners = owners, Common = common, Params = (ps ?? []).Select(Param).ToList(),
            };
            Events.Add(e);
            eventByName[name] = e;
        }

        private static void Act(string name, string label, string category, string sentence, string desc, string[] ps, bool targeted = false,
            string[] nested = null, bool levelWide = false, bool common = false)
        {
            var a = new ActionUi
            {
                Name = name, Label = label, Category = category, Sentence = sentence, Desc = desc, Targeted = targeted,
                Nested = nested ?? [], LevelWide = levelWide, Common = common, Params = ps.Select(Param).ToList(),
            };
            if (targeted) a.Params.Insert(0, new ParamUi { Name = "target", Label = "Target", Kind = ParamKind.Target, Default = "self", Inline = true, Desc = "Who it acts on (default: this)." });
            Actions.Add(a);
            actionByName[name] = a;
        }

        /// <summary>Registers an event the engine's registry has and this table doesn't (a plain card).</summary>
        internal static void AddUnknownEvent(string name, string desc, IEnumerable<ParamUi> ps)
        {
            if (eventByName.ContainsKey(name)) return;
            Ev(name, Humanize(name).ToLowerInvariant(), "Level", desc ?? "");
            eventByName[name].Params = ps?.ToList() ?? [];
        }

        internal static void AddUnknownAction(string name, string desc, IEnumerable<ParamUi> ps)
        {
            if (actionByName.ContainsKey(name)) return;
            Act(name, Humanize(name), "Logic", Humanize(name), desc ?? "", []);
            actionByName[name].Params = ps?.ToList() ?? [];
        }
    }
}
