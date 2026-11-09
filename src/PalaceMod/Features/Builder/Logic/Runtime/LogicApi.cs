using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/*
    The behaviour engine's shared runtime interfaces (docs/builder-engine.md, "Interfaces"). L1 owns this file; L2
    (world verbs and bodies) and L3 (the Studio's play-mode panel) build against it.

    Everything here is pure C# (no UnityEngine types), so the core can be driven by tests with a fake world. Game
    objects hang off an entity through `LogicEntity.Body` (IEntityBody) and `LogicEntity.Game` (the GameObject, opaque
    here); `LogicGame` (Runtime/LogicGame.cs, Unity side) converts between them.

    Lifecycle: LogicManager creates a LogicWorld per Builder session (custom run or Studio play) and installs it as
    `LogicWorld.Current`; null outside one. Handlers must not cache the world across sessions.
*/
namespace PalaceMod.Builder.Logic
{
    // ------------------------------------------------------------------ values

    /// <summary>A position (metres, world space). Kept free of UnityEngine so the core runs in tests.</summary>
    public readonly struct Vec : IEquatable<Vec>
    {
        public readonly float X, Y, Z;
        public Vec(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static readonly Vec Zero = new(0, 0, 0);
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator *(Vec a, float k) => new(a.X * k, a.Y * k, a.Z * k);
        public static Vec operator /(Vec a, float k) => new(a.X / k, a.Y / k, a.Z / k);
        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        /// <summary>Ground-plane (XZ) distance, which is what ranges, `near` and selectors use.</summary>
        public static float FlatDistance(Vec a, Vec b) { float dx = a.X - b.X, dz = a.Z - b.Z; return (float)Math.Sqrt(dx * dx + dz * dz); }
        public static float FlatDistanceSq(Vec a, Vec b) { float dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }
        public bool Equals(Vec o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec v && Equals(v);
        public override int GetHashCode() => (X, Y, Z).GetHashCode();
        public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
    }

    public enum ValueKind { None, Number, Bool, Text, Position, Entity, Set, List, Template }

    /// <summary>
    /// A value of the expression language. Number is double; Template is a template id (text); Set is an ordered list
    /// of entities (selectors order by distance, then id); List is a list var (one element kind).
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        public readonly ValueKind Kind;
        public readonly double Num;
        public readonly Vec Pos;
        public readonly object Ref;   // string (Text, Template), LogicEntity, IReadOnlyList<LogicEntity> (Set), List<Value> (List)

        private Value(ValueKind kind, double num = 0, Vec pos = default, object r = null) { Kind = kind; Num = num; Pos = pos; Ref = r; }

        public static readonly Value None = default;
        public static Value Number(double n) => new(ValueKind.Number, n);
        public static Value Bool(bool b) => new(ValueKind.Bool, b ? 1 : 0);
        public static Value Text(string s) => new(ValueKind.Text, r: s ?? "");
        public static Value Position(Vec p) => new(ValueKind.Position, pos: p);
        public static Value Entity(LogicEntity e) => e == null ? None : new(ValueKind.Entity, r: e);
        public static Value Set(IReadOnlyList<LogicEntity> s) => new(ValueKind.Set, r: s ?? Array.Empty<LogicEntity>());
        public static Value List(List<Value> l) => new(ValueKind.List, r: l ?? []);
        public static Value Template(string id) => new(ValueKind.Template, r: id);

        public bool IsNone => Kind == ValueKind.None;
        public string Str => Ref as string;
        public LogicEntity AsEntity => Ref as LogicEntity;
        public IReadOnlyList<LogicEntity> AsSet => Ref as IReadOnlyList<LogicEntity>;
        public List<Value> AsList => Ref as List<Value>;

        public bool Equals(Value o) => Kind == o.Kind && Num.Equals(o.Num) && Pos.Equals(o.Pos) && Equals(Ref, o.Ref);
        public override bool Equals(object obj) => obj is Value v && Equals(v);
        public override int GetHashCode() => (Kind, Num, Pos, Ref).GetHashCode();
        public override string ToString() => Values.Format(this);
    }

    // ------------------------------------------------------------------ entities

    /// <summary>
    /// What the game side gives the core about an entity's body. L1 implements it for game objects (units, buildings,
    /// the king, Builder bodies); tests use a fake.
    /// </summary>
    public interface IEntityBody
    {
        bool Exists { get; }                 // false once the game object is gone
        bool Alive { get; }                  // a knocked-out building is not alive
        Vec Position { get; }
        float Facing { get; }                // degrees, 0 = north (+Z), clockwise
        float Hp { get; }
        float MaxHp { get; }
        bool HasTag(string etag);            // TagManager.ETag name, case-sensitive ("Flying", "Tower", "MeeleFighter")
        string BuildingType { get; }         // a slot's building kind ("Mill"), else null
        int Level { get; }                   // a slot's level, else 0
    }

    /// <summary>
    /// A live object the engine knows about: Builder entities (placed, attached, spawned) and, lazily, vanilla units,
    /// buildings, the king and coins when an event or selector touches them. One instance per object per session.
    /// </summary>
    public sealed class LogicEntity
    {
        public string Id;                    // deterministic: place id, Studio key ("slot:Tower 3"), "<template>#<n>", or SaveStates.PathOf
        public string Name;                  // the "name" for @name references, or null
        public string Template;              // template id (Builder) or vanilla prefab name, or null
        public string Kind;                  // unit | building | prop | projectile | area | pickup | logic | coin | king | castle | level
        public string Team;                  // player | enemy | neutral
        public readonly HashSet<string> Labels = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Value> Vars = new(StringComparer.Ordinal);
        public bool Enabled = true;          // `enable`/`disable`/`toggle`
        public bool Spawned;                 // made at runtime by `spawn`/`launch` (savestates respawn these)
        public bool Destroyed;               // removed from the world; handles may still point here
        public LogicEntity Owner;            // launcher/spawner, or null
        public double SpawnTime;             // world time it appeared (for .age)
        public IEntityBody Body;             // null for logic-only entities (the level, an invisible logic object)
        public object Game;                  // the GameObject (Unity side), opaque to the core
        public JObject Def;                  // the template/place JSON the entity was built from (components), or null
        public object Ext;                   // free slot for L2 (component state), not touched by the core

        /// <summary>Status flags in force ("stunned", "silenced", "untargetable", ...); L2's statuses maintain it.</summary>
        public readonly HashSet<string> Flags = new(StringComparer.Ordinal);

        /// <summary>The entity's rules with their per-owner state (the world's own).</summary>
        internal List<RuleInstance> Rules = [];

        public bool Exists => !Destroyed && (Body == null || Body.Exists);
        public bool Alive => Exists && (Body == null || Body.Alive);
        public Vec Position => Body?.Position ?? StaticPos;
        public Vec StaticPos;                // position for body-less entities (a logic object or area without a game object)

        public override string ToString() => Id;
    }

    // ------------------------------------------------------------------ events

    /// <summary>One event as rules see it.</summary>
    public sealed class LogicEvent
    {
        public string Name;                  // "death", "signal", "every", ...
        public LogicEntity Subject;          // who it happened to (the owner for own events)
        public LogicEntity Other;            // counterpart: killer, attacker, entrant, sender, target...; may be null
        public double Value;                 // event.value
        public Vec? Pos;                     // event.pos (defaults to the subject's position when read)
        public string Text;                  // event.name for signals; event.choice for interact; the status id for status events
        public Value Payload;                // a non-numeric event.value (e.g. a signal carrying text), else None
        public long Seq;                     // dispatch order
        public int Depth;                    // chain depth (0 = from the game)
        internal object Target;              // internal: a directed delivery (signals, own timers)
    }

    /// <summary>Raise events from game hooks or actions. Events are queued and dispatched in LateUpdate, in order.</summary>
    public static class LogicEvents
    {
        /// <summary>
        /// Queue an event that happened to <paramref name="subject"/>. Own rules of the subject and `of` listeners
        /// whose filter matches the subject hear it. No-op outside a logic session.
        /// </summary>
        public static void Raise(string name, LogicEntity subject, LogicEntity other = null, double value = 0, Vec? pos = null, string text = null) =>
            LogicWorld.Current?.Raise(name, subject, other, value, pos, text);

        /// <summary>Send a named signal (optional number) to an entity, a set, or everyone (`to == null`: broadcast incl. the level script).</summary>
        public static void Signal(string name, IReadOnlyList<LogicEntity> to, double value = 0, LogicEntity sender = null) =>
            LogicWorld.Current?.Signal(name, to, value, sender);

        /// <summary>Raised on the main thread after each trace entry (the Studio's rule-fire pips).</summary>
        public static event Action<TraceEntry> Traced;
        internal static void OnTraced(TraceEntry e) => Traced?.Invoke(e);
    }

    // ------------------------------------------------------------------ actions

    /// <summary>How the core calls an action handler.</summary>
    [Flags]
    public enum ActionFlags
    {
        None = 0,
        /// <summary>The core resolves `target` (default `self`), caps the set at 256, and calls the handler once per
        /// member with `ctx.Target` and `it` bound to it; other params are evaluated per member.</summary>
        PerTarget = 1,
    }

    /// <summary>An action's implementation. Read params through <paramref name="p"/>; they're evaluated in <paramref name="ctx"/>.</summary>
    public delegate void ActionHandler(LogicContext ctx, ActionParams p);

    public static class LogicActions
    {
        internal sealed class Entry { public string Name; public ActionHandler Handler; public ActionFlags Flags; }
        internal static readonly Dictionary<string, Entry> Handlers = new(StringComparer.Ordinal);

        /// <summary>Register (or replace) an action. Call from a manager's Awake; registrations survive sessions.</summary>
        public static void Register(string name, ActionHandler handler, ActionFlags flags = ActionFlags.None) =>
            Handlers[name] = new Entry { Name = name, Handler = handler, Flags = flags };

        public static bool IsRegistered(string name) => Handlers.ContainsKey(name);
        public static IEnumerable<string> Names => Handlers.Keys;

        internal static readonly Dictionary<string, Func<LogicEntity, LogicEvent, bool>> Inputs = new(StringComparer.Ordinal);

        /// <summary>
        /// A built-in input: a signal name an entity answers without a listener rule (movers' `start`/`open`, areas'
        /// `show`, ...). The handler returns true if the entity handled it (e.g. it has a mover). Several handlers may
        /// share a name; the core tries them in registration order. `enable`/`disable`/`toggle`/`destroy` are the core's.
        /// </summary>
        public static void RegisterInput(string name, Func<LogicEntity, LogicEvent, bool> handler)
        {
            if (Inputs.TryGetValue(name, out var prev)) Inputs[name] = (e, ev) => prev(e, ev) || handler(e, ev);
            else Inputs[name] = handler;
        }
    }

    /// <summary>
    /// An action's params, compiled once per rule and evaluated on read in the action's context. Missing optional
    /// params return the given default. Any read can throw LogicError (a type error at runtime, an unknown name),
    /// which the core catches, traces against the rule, and stops that action list.
    /// </summary>
    public abstract class ActionParams
    {
        public abstract bool Has(string name);
        public abstract JToken Raw(string name);                       // the param's JSON as written
        public abstract Value Get(string name);                        // evaluated (expressions, roles, literals)
        public abstract double Number(string name, double fallback = 0);
        public abstract bool Bool(string name, bool fallback = false);
        public abstract string Text(string name, string fallback = null);  // interpolates {expr}
        public abstract string Template(string name);                  // a template id ("slime", "=self.template")
        public abstract Vec? Position(string name);                    // entity -> its position; null if absent/none
        public abstract LogicEntity Entity(string name);               // a set -> its first member
        public abstract IReadOnlyList<LogicEntity> Entities(string name);   // entity -> one-element set; capped at 256
        public abstract ActionList Actions(string name);               // nested actions ("then", "onImpact", "do"); null if absent
        /// <summary>The target of a PerTarget call (also bound as `it`).</summary>
        public LogicEntity Target { get; internal set; }
    }

    /// <summary>A compiled list of actions (`then`, `onImpact`, ...). Run it with <see cref="LogicContext.Run"/>.</summary>
    public sealed class ActionList
    {
        internal readonly List<CompiledAction> Items;
        internal ActionList(List<CompiledAction> items) { Items = items; }
        public int Count => Items.Count;
        public static readonly ActionList Empty = new([]);
    }

    /// <summary>The roles an action sees. Copy-on-write: <see cref="With"/> returns a child context.</summary>
    public sealed class LogicContext
    {
        public LogicWorld World;
        public LogicEntity Self;             // the rule's owner (the level entity for the level script)
        public LogicEntity Subject, Other, Spawned, It;
        public LogicEvent Event;
        public int Depth;                    // chain depth: events raised from here get Depth + 1
        public string RuleLabel;             // for the trace ("slot:Tower 3 / rules[0]")
        /// <summary>Extra named roles (e.g. a status's `attacker`/`victim`, a param binding); null if none.</summary>
        public Dictionary<string, Value> Locals;
        internal bool Stopped;               // `stop` ran
        internal ActionCursor Cursor;        // internal: where a `wait` resumes

        public LogicContext With(LogicEntity spawned = null, LogicEntity it = null, LogicEntity other = null)
        {
            var c = (LogicContext)MemberwiseClone();
            if (spawned != null) c.Spawned = spawned;
            if (it != null) c.It = it;
            if (other != null) c.Other = other;
            if (Locals != null) c.Locals = new Dictionary<string, Value>(Locals);
            c.Cursor = null;
            c.Stopped = false;
            return c;
        }

        public LogicContext WithLocal(string name, Value v)
        {
            var c = With();
            c.Locals ??= new Dictionary<string, Value>(StringComparer.Ordinal);
            c.Locals[name] = v;
            return c;
        }

        /// <summary>Run nested actions now, in this context (or a child from <see cref="With"/>). Budgets apply.</summary>
        public void Run(ActionList actions) => World.RunList(actions, this);

        /// <summary>Raise an event from an action (goes to the back of the queue at Depth + 1).</summary>
        public void Raise(string name, LogicEntity subject, LogicEntity other = null, double value = 0, Vec? pos = null, string text = null) =>
            World.Raise(name, subject, other, value, pos, text, Depth + 1);
    }

    /// <summary>A rule-level runtime error: traced against the rule with its JSON path, never thrown into the game.</summary>
    public sealed class LogicError(string message) : Exception(message);

    // ------------------------------------------------------------------ services L2 installs

    /// <summary>
    /// Makes game objects for entities (L2: Bodies/EntityFactory.cs). The core describes the entity first (id,
    /// template, team, owner, Def, vars) and checks the caps; Build makes its object and calls
    /// <c>LogicGame.Register(e, go)</c>, which compiles its rules and raises `spawn`. Actions spawn through
    /// <see cref="LogicWorld.Spawn"/>, never Build directly.
    /// </summary>
    public interface IEntityFactory
    {
        /// <summary>Build the object for <paramref name="e"/> (e.Template, e.Def: the template JSON merged with a place's
        /// inline fields; e.Team already resolved). False + error if it can't; the core then drops the entity.</summary>
        bool Build(LogicEntity e, Vec pos, float rotY, out string error);
        /// <summary>Remove an entity's game object (destroy action, lifetime end, session end).</summary>
        void Despawn(LogicEntity e, bool effects, bool drops);
        /// <summary>Whether a template id exists in the session (units[] or entities[] or vanilla).</summary>
        bool HasTemplate(string template);
        /// <summary>"unit" or "projectile" (counted against those caps) or "entity".</summary>
        string CapClass(string template);
        /// <summary>An `attach` entry on an existing object (slot, prop, line, castle) was registered: add the
        /// components its JSON carries (area, interact, attacks, ...). e.Def is the attach JSON; e.Game the object.</summary>
        void Attach(LogicEntity e);
    }

    /// <summary>A stat change: one of Set/Add/Mult, optional key (same key replaces), optional filters.</summary>
    public sealed class StatModifier
    {
        public string Stat;
        public double? Set, Add, Mult;
        public string Key;                   // same key on the same entity replaces the earlier one
        public double Seconds;               // > 0: timed; 0 with Until null = permanent
        public string Until;                 // "night" | "day": until the next dawn/dusk
        public LogicEntity Source;
        public JObject Extra;                // from / types / chance / when (damageTaken, damageMult), as written
    }

    /// <summary>Stat modifier stacks and statuses (L2: Bodies/Stats.cs).</summary>
    public interface IStatModifiers
    {
        void Add(LogicEntity e, StatModifier m);
        /// <summary>The stat with modifiers (`.moveSpeed` etc.); NaN if the entity has no such stat.</summary>
        double Get(LogicEntity e, string stat);
        int Stacks(LogicEntity e, string status);                       // `.stacks('x')`, `has:x`
        void ApplyStatus(LogicEntity e, string status, double? seconds, int stacks, LogicEntity source);
        void RemoveStatus(LogicEntity e, string status);
        /// <summary>Savestate: status + timed modifier state of one entity, and its restore.</summary>
        JToken Capture(LogicEntity e);
        void Restore(LogicEntity e, JToken state);
    }

    /// <summary>Targeting overrides (L2: Bodies/Steering.cs). The targeting block is the doc's JSON form.</summary>
    public interface ITargeting
    {
        void Set(LogicEntity e, JToken targeting, double seconds);
    }

    /// <summary>Area membership for `in:@zone` (L2: BuilderArea). The core has a shape-based fallback.</summary>
    public interface IAreas
    {
        /// <summary>null: not an area (the core then answers from the area's `shape`).</summary>
        bool? Contains(LogicEntity area, LogicEntity e);
    }

    /// <summary>
    /// Candidate entities for selectors and `of`/`near` (L1 implements it over TagManager plus the Builder registry;
    /// L2 may replace it with its spatial hash). Must return entities whose Body exists.
    /// </summary>
    public interface ISpatial
    {
        /// <summary>Everything within <paramref name="radius"/> (XZ) of <paramref name="center"/>; radius &lt;= 0 = the whole map.</summary>
        void Query(Vec center, float radius, List<LogicEntity> into);
    }

    /// <summary>Game-wide reads the expression language needs (L1 implements it on the game; tests fake it).</summary>
    public interface IGameState
    {
        int Night { get; }                   // 1-based current night, or the coming one by day
        bool IsNight { get; }
        int Nights { get; }                  // total
        double NightTime { get; }
        double NightProgress { get; }
        int Gold { get; }
        int Income { get; }
        int EnemiesAlive { get; }
        double Difficulty { get; }
        LogicEntity King { get; }
        LogicEntity Castle { get; }
        int WaveCount(int night, string filter);
        IReadOnlyList<string> WaveUnits(int night);
        string StrongestUnit(int night);
        Vec SpawnLine(string name);
        Vec PointNear(Vec p, float r, LogicRandom rng);   // a random walkable point
    }

    /// <summary>Installed by L2 (factory, stats, targeting, areas) and L1 (spatial, game). Null = not available yet.</summary>
    public static class LogicServices
    {
        public static IEntityFactory Factory;
        public static IStatModifiers Stats;
        public static ITargeting Targeting;
        public static IAreas Areas;
        public static ISpatial Spatial;
        public static IGameState Game;
        /// <summary>`.target`: the entity's current attack/move target (L2), or null.</summary>
        public static Func<LogicEntity, LogicEntity> TargetOf;
        /// <summary>Extra selector/`near` candidates beyond the registry and the game's tagged objects (L2: vanilla
        /// projectiles in flight). Called with (center, radius, into); radius &lt;= 0 = everywhere.</summary>
        public static Action<Vec, float, List<LogicEntity>> ExtraCandidates;
    }

    // ------------------------------------------------------------------ savestates

    /// <summary>
    /// Extra per-session state for savestates and retry-day, captured with the core's ("logic" in SaveStates).
    /// Restore runs after the core has respawned spawned entities and restored vars, in registration order.
    /// </summary>
    public static class LogicSaveState
    {
        internal sealed class Part { public string Id; public Func<LogicWorld, JToken> Capture; public Action<LogicWorld, JToken> Restore; }
        internal static readonly List<Part> Parts = [];

        public static void Register(string id, Func<LogicWorld, JToken> capture, Action<LogicWorld, JToken> restore)
        {
            Parts.RemoveAll(p => p.Id == id);
            Parts.Add(new Part { Id = id, Capture = capture, Restore = restore });
        }
    }

    // ------------------------------------------------------------------ trace and budgets (the Studio's Logic panel)

    public enum TraceOutcome { Fired, ConditionFalse, Throttled, Dropped, Error, Capped }

    /// <summary>One line of the play-mode trace.</summary>
    public sealed class TraceEntry
    {
        public double Time;                  // world time (s since level start)
        public int Frame;
        public string Owner;                 // entity id: Studio key ("slot:Tower 3"), place id, "level", "<template>#<n>"
        public string Rule;                  // rule id, or "rules[i]"
        public string Path;                  // JSON path of the rule ("levels[0].logic.places[3].rules[1]") when known
        public string Event;
        public TraceOutcome Outcome;
        public string Message;               // error text, cap name, ...
        public double Value;                 // event.value
        public string Other;                 // other's id
    }

    /// <summary>The effective caps. Defaults per docs/builder-engine.md; a level may raise them in `logic.caps`.</summary>
    public sealed class LogicCaps
    {
        public int Entities = 300, Units = 200, Projectiles = 400, PerTemplate = 100;
        public int FiringsPerFrame = 400, ActionsPerFrame = 4000;
        public int ChainDepth = 12, SameRulePerSecond = 30;
        public int Loop = 100, Set = 256;
        /// <summary>The hard ceiling a level's override is clamped to (the "may lag" range ends here).</summary>
        public static readonly LogicCaps Max = new() { Entities = 2000, Units = 1000, Projectiles = 2000, PerTemplate = 1000, FiringsPerFrame = 4000, ActionsPerFrame = 40000 };
        /// <summary>The `logic.caps` key names: entities, units, projectiles, perTemplate, firingsPerFrame, actionsPerFrame.</summary>
        public static readonly string[] Keys = ["entities", "units", "projectiles", "perTemplate", "firingsPerFrame", "actionsPerFrame"];
    }

    /// <summary>Live budget use, for `logic budget` and the Studio meter.</summary>
    public sealed class BudgetSnapshot
    {
        public LogicCaps Caps;
        public int FiringsThisFrame, ActionsThisFrame;
        public int LiveEntities, LiveUnits, LiveProjectiles;
        public int Throttled, Dropped, CappedSpawns, Errors;   // totals since level start
        public int QueuedEvents;
    }
}
