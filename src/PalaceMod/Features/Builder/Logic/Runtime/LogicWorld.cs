using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    The session's logic world: entity registry, templates, event queue and dispatch, timers and pollers, vars, the
    random source, budgets and the trace. Pure C#: LogicManager drives it in game (Tick from LateUpdate), tests drive
    it with a fake world at fixed steps.

    Execution model (docs/builder-engine.md, "Execution model"):
    - Hooks only enqueue events (with a sequence number); Tick drains the queue in order. Events raised by actions go
      to the back and are drained in the same Tick, up to the budget; the rest waits for the next frame.
    - Timers (`every`/`after`), `wait`s and polling (`near`) advance on fixed steps of game time (Step), in a
      deterministic order (due time, then creation order).
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicWorld
    {
        /// <summary>The running session's world, or null outside a Builder session.</summary>
        public static LogicWorld Current { get; internal set; }

        /// <summary>Fixed step for timers and polling (s); polling runs every other step (10 Hz).</summary>
        public const double StepSeconds = 0.05;

        /// <summary>Seconds of game time since level start.</summary>
        public double Time { get; internal set; }
        public int Frame { get; internal set; }
        public LogicCaps Caps { get; internal set; } = new();
        public LogicRandom Random { get; } = new(1);
        /// <summary>The level script's owner (`self` in level rules, `level` in expressions). Holds the level vars.</summary>
        public LogicEntity Level { get; } = new() { Id = "level", Kind = "level", Team = "player" };

        /// <summary>Templates by id: `units[]` (kind "unit") and `entities[]`, as JSON.</summary>
        public readonly Dictionary<string, JObject> Templates = new(StringComparer.Ordinal);
        public readonly Dictionary<string, JObject> Presets = new(StringComparer.Ordinal);
        public readonly Dictionary<string, JObject> Statuses = new(StringComparer.Ordinal);
        /// <summary>Compile problems found at start (also traced).</summary>
        public readonly List<string> CompileErrors = [];

        public LogicWorld() { CoreActions.EnsureRegistered(); }

        /// <summary>A world became Current (after Load, places and attaches, before `levelStart` is dispatched).</summary>
        public static event Action<LogicWorld> Started;
        /// <summary>The world is about to stop being Current (session end, level reload); entities still exist.</summary>
        public static event Action<LogicWorld> Ending;
        /// <summary>Every polling step (10 Hz of game time), after the core's `near` polling: L2's area poller.</summary>
        public static event Action<LogicWorld, double> Polled;

        internal static void RaiseStarted(LogicWorld w) => Started?.Invoke(w);
        internal static void RaiseEnding(LogicWorld w) => Ending?.Invoke(w);

        private readonly Dictionary<string, LogicEntity> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LogicEntity> byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CompiledRule>> templateRules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RuleInstance>> byEvent = new(StringComparer.Ordinal);    // every instance, by when.on
        private readonly Dictionary<string, List<RuleInstance>> ofByEvent = new(StringComparer.Ordinal);  // instances with `of`
        private readonly List<RuleInstance> timers = [];
        private readonly List<RuleInstance> nears = [];
        private readonly Queue<LogicEvent> queue = new();
        private readonly Queue<LogicEvent> deferred = new();   // directed deliveries cut off by the firing budget
        private long seq;
        private int spawnCounter;
        private double stepTime;
        private int stepCount;

        public IEnumerable<LogicEntity> Entities => byId.Values;
        public LogicEntity Find(string id) => id != null && byId.TryGetValue(id, out var e) ? e : null;
        /// <summary>`@name`: a place or attach name, or else an entity id.</summary>
        public LogicEntity FindNamed(string name) => name != null && byName.TryGetValue(name, out var e) && !e.Destroyed ? e : Find(name);
        public bool HasListeners(string ev) => byEvent.TryGetValue(ev, out var l) && l.Count > 0;
        public bool HasOfListeners(string ev) => ofByEvent.TryGetValue(ev, out var l) && l.Count > 0;

        // ------------------------------------------------------------------ setup

        /// <summary>
        /// Load the pack's logic for a level: templates, statuses, presets, the level's `logic` block (seed, caps,
        /// vars, level script). Places and attaches are made by the caller (they need game objects): see
        /// <see cref="Describe"/> and <see cref="Add"/>. Call <see cref="BeginLevel"/> after them.
        /// </summary>
        public void Load(JObject logic, IEnumerable<(string id, JObject def)> templates, IEnumerable<JObject> statuses, IEnumerable<JObject> presets, string levelPath)
        {
            foreach (var (id, def) in templates ?? []) if (id != null) Templates[id] = def;
            foreach (var s in statuses ?? []) if ((string)s["id"] is { } sid) Statuses[sid] = s;
            foreach (var p in presets ?? []) if ((string)p["id"] is { } pid) Presets[pid] = p;
            logic ??= new JObject();
            LevelLogic = logic;

            ulong seed = logic["seed"]?.Type is JTokenType.Integer ? (ulong)(long)logic["seed"] : StableHash(levelPath ?? "level");
            Random.Seed(seed);
            Caps = ReadCaps(logic["caps"] as JObject);

            var ctx = new LogicContext { World = this, Self = Level };
            if (logic["vars"] is JObject vars)
                foreach (var p in vars.Properties())
                    Level.Vars[p.Name] = StartValue(p.Value, ctx, $"{levelPath}.logic.vars.{p.Name}");

            AttachRules(Level, logic["rules"] as JArray, $"{levelPath}.logic.rules");
        }

        /// <summary>The level's `logic` block as loaded (after preset expansion): hud, kingActions, ... for L2/L3.</summary>
        public JObject LevelLogic { get; private set; } = new();

        /// <summary>Evaluate an expression (level-script scope unless a context is given). Throws LogicError.</summary>
        public Value Evaluate(string expr, LogicContext ctx = null) => LogicExpr.Compile(expr)(ctx ?? new LogicContext { World = this, Self = Level });

        /// <summary>Interpolate text with {expr} parts (level-script scope unless a context is given). Throws LogicError.</summary>
        public string Interpolate(string text, LogicContext ctx = null) => LogicExpr.Text(text)(ctx ?? new LogicContext { World = this, Self = Level });

        /// <summary>Forget vanilla wraps whose object is gone (dead units), so the registry doesn't grow all night.</summary>
        public int PurgeGone()
        {
            var gone = byId.Values.Where(e => !e.Spawned && e.Body != null && !e.Body.Exists && e.Rules.Count == 0).ToList();
            foreach (var e in gone) Forget(e);
            return gone.Count;
        }

        /// <summary>Drop queued `spawn` and `levelStart` events (a restored level is not a fresh start).</summary>
        public void ClearQueuedSpawns()
        {
            var keep = queue.Where(e => e.Name is not ("spawn" or "levelStart")).ToList();
            queue.Clear();
            foreach (var e in keep) queue.Enqueue(e);
        }

        public static LogicCaps ReadCaps(JObject o)
        {
            var c = new LogicCaps();
            if (o == null) return c;
            int Get(string key, int def, int max) => o[key]?.Type is JTokenType.Integer or JTokenType.Float ? Math.Max(1, Math.Min(max, (int)(double)o[key])) : def;
            c.Entities = Get("entities", c.Entities, LogicCaps.Max.Entities);
            c.Units = Get("units", c.Units, LogicCaps.Max.Units);
            c.Projectiles = Get("projectiles", c.Projectiles, LogicCaps.Max.Projectiles);
            c.PerTemplate = Get("perTemplate", c.PerTemplate, LogicCaps.Max.PerTemplate);
            c.FiringsPerFrame = Get("firingsPerFrame", c.FiringsPerFrame, LogicCaps.Max.FiringsPerFrame);
            c.ActionsPerFrame = Get("actionsPerFrame", c.ActionsPerFrame, LogicCaps.Max.ActionsPerFrame);
            return c;
        }

        /// <summary>FNV-1a, stable across runs and platforms (the default seed is the level's id).</summary>
        public static ulong StableHash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>A var's start value: a literal, an expression string ("'summer'", "vec(1, 2)"), [] (a list), or
        /// {"type": ..., "value": ...}.</summary>
        public Value StartValue(JToken t, LogicContext ctx, string path)
        {
            if (t is JObject o && o["type"] != null)
            {
                var inner = o["value"];
                if (inner == null || inner.Type == JTokenType.Null)
                    return (string)o["type"] == "list" ? Value.List([]) : Value.None;
                return StartValue(inner, ctx, path);
            }
            if (t is JArray a && a.Count == 0) return Value.List([]);
            try { return CompiledParams.FromToken(t, path)(ctx); }
            catch (Exception e) when (e is LogicError or InvalidCastException or FormatException)
            {
                CompileErrors.Add($"{path}: {e.Message}");
                return Value.None;
            }
        }

        /// <summary>
        /// A new entity described from a template and/or a place (inline fields override the template's): id, name,
        /// kind, team, labels, Def. Not registered yet: build its body (Factory) or call <see cref="Add"/>.
        /// </summary>
        public LogicEntity Describe(string template, JObject place, string id, string team, LogicEntity owner)
        {
            JObject def = null;
            if (template != null && Templates.TryGetValue(template, out var tdef)) def = (JObject)tdef.DeepClone();
            if (place != null)
            {
                def ??= new JObject();
                foreach (var p in place.Properties())
                    if (p.Name is not ("rules" or "vars" or "entity" or "pos" or "rotY" or "id" or "name")) def[p.Name] = p.Value.DeepClone();
            }
            var e = new LogicEntity
            {
                Id = id,
                Name = (string)place?["name"] ?? (place != null ? (string)place["id"] : null),
                Template = template,
                Kind = (string)def?["kind"] ?? (template != null && IsUnitTemplate(template) ? "unit" : "logic"),
                Team = team ?? (string)def?["team"] ?? "neutral",
                Owner = owner,
                Def = def,
                Enabled = (bool?)place?["active"] ?? true,
            };
            if (e.Team == "owner") e.Team = owner?.Team ?? "neutral";
            if (place != null) pendingPlace[e] = place;
            return e;
        }

        private readonly Dictionary<LogicEntity, JObject> pendingPlace = [];

        public bool IsUnitTemplate(string template) => Templates.TryGetValue(template, out var d) && (string)d["kind"] is null or "unit";

        /// <summary>
        /// Register an entity: assigns an id if it has none ("&lt;template&gt;#&lt;n&gt;"), indexes its name, fills
        /// vars and labels (template, then preset, then place overrides), compiles its rules (template, then the
        /// place's or attach's own), and raises `spawn`. LogicGame.Register calls this; body-less entities (logic
        /// objects, areas without a look, the level) are added directly. Vanilla wraps get no rules and no `spawn`.
        /// </summary>
        public void Add(LogicEntity e, bool vanilla = false)
        {
            if (string.IsNullOrEmpty(e.Id)) e.Id = $"{e.Template ?? e.Kind ?? "entity"}#{++spawnCounter}";
            if (byId.TryGetValue(e.Id, out var prev) && prev != e && !prev.Destroyed) Forget(prev);
            e.SpawnTime = Time;
            byId[e.Id] = e;
            if (!string.IsNullOrEmpty(e.Name)) byName[e.Name] = e;
            if (vanilla) return;

            pendingPlace.TryGetValue(e, out var place);
            pendingPlace.Remove(e);
            JObject tdef = e.Template != null && Templates.TryGetValue(e.Template, out var td) ? td : null;
            var ctx = new LogicContext { World = this, Self = e };

            void Labels(JToken t) { if (t is JArray a) foreach (var l in a) if (l.Type == JTokenType.String) e.Labels.Add((string)l); }
            void Vars(JToken t, string path)
            {
                if (t is not JObject o) return;
                foreach (var p in o.Properties()) e.Vars[p.Name] = StartValue(p.Value, ctx, $"{path}.{p.Name}");
            }
            Labels(tdef?["labels"]);
            Labels(e.Def?["labels"]);
            if (tdef?["rules"] is JArray tr) foreach (var r in tr) if (r is JObject ro && ro["preset"] is JValue pv && Presets.TryGetValue((string)pv ?? "", out var pre)) Vars(Logic.Presets.Vars(pre, ro["with"] as JObject), $"presets.{pv}.vars");
            Vars(tdef?["vars"], $"templates.{e.Template}.vars");
            Vars(place?["vars"], $"{e.Id}.vars");

            if (e.Template != null && tdef != null)
            {
                if (!templateRules.TryGetValue(e.Template, out var rules))
                {
                    rules = RuleCompiler.CompileAll(tdef["rules"] as JArray, $"templates.{e.Template}.rules", Presets, CompileErrors);
                    templateRules[e.Template] = rules;
                }
                foreach (var r in rules) AddInstance(e, r, null);
            }
            if (place?["rules"] is JArray pr) AttachRules(e, pr, $"{e.Id}.rules");

            if (e.Spawned) { liveSpawned++; CountCap(e, +1); }
            Raise("spawn", e, e.Owner, 0, null, null, 0);
        }

        /// <summary>
        /// Attach rules to an entity (the level script, place and attach rules, a status's rules while it lasts).
        /// Returns a handle for <see cref="DetachRules"/>.
        /// </summary>
        public object AttachRules(LogicEntity e, JArray rules, string path)
        {
            if (rules == null || rules.Count == 0) return null;
            var handle = new object();
            foreach (var r in RuleCompiler.CompileAll(rules, path, Presets, CompileErrors)) AddInstance(e, r, handle);
            return handle;
        }

        public void DetachRules(LogicEntity e, object handle)
        {
            if (handle == null) return;
            foreach (var ri in e.Rules.Where(r => r.AttachedBy == handle).ToList()) RemoveInstance(ri);
        }

        private void AddInstance(LogicEntity e, CompiledRule r, object handle)
        {
            var ri = new RuleInstance { Rule = r, Owner = e, Enabled = r.EnabledAtStart, AttachedBy = handle };
            e.Rules.Add(ri);
            if (r.Event == null) return;
            Index(byEvent, r.Event).Add(ri);
            if (r.Of != null) Index(ofByEvent, r.Event).Add(ri);
            switch (r.Event)
            {
                case "every":
                    ri.NextTime = Time + r.Phase + r.Seconds;
                    timers.Add(ri);
                    break;
                case "after":
                    ri.NextTime = Time + r.Seconds;
                    timers.Add(ri);
                    break;
                case "near":
                    ri.NearInside = [];
                    nears.Add(ri);
                    break;
            }
        }

        private void RemoveInstance(RuleInstance ri)
        {
            ri.Owner.Rules.Remove(ri);
            var ev = ri.Rule.Event;
            if (ev == null) return;
            if (byEvent.TryGetValue(ev, out var l)) l.Remove(ri);
            if (ofByEvent.TryGetValue(ev, out l)) l.Remove(ri);
            timers.Remove(ri);
            nears.Remove(ri);
        }

        private static List<RuleInstance> Index(Dictionary<string, List<RuleInstance>> d, string key)
        {
            if (!d.TryGetValue(key, out var l)) d[key] = l = [];
            return l;
        }

        /// <summary>The entity's object is gone: drop it from the registry and its rules from the indices. Its own
        /// queued events (its `death`) still reach its rules.</summary>
        public void Forget(LogicEntity e)
        {
            if (e.Destroyed) return;
            e.Destroyed = true;
            if (byId.TryGetValue(e.Id, out var cur) && cur == e) byId.Remove(e.Id);
            if (e.Name != null && byName.TryGetValue(e.Name, out cur) && cur == e) byName.Remove(e.Name);
            if (e.Spawned) { liveSpawned--; CountCap(e, -1); }
            foreach (var ri in e.Rules)
            {
                var ev = ri.Rule.Event;
                if (ev == null) continue;
                if (byEvent.TryGetValue(ev, out var l)) l.Remove(ri);
                if (ofByEvent.TryGetValue(ev, out l)) l.Remove(ri);
            }
            timers.RemoveAll(ri => ri.Owner == e);
            nears.RemoveAll(ri => ri.Owner == e);
            foreach (var n in nears) n.NearInside?.Remove(e);
        }

        /// <summary>After places and attaches exist: raise `levelStart`.</summary>
        public void BeginLevel()
        {
            foreach (var err in CompileErrors.Distinct().Take(50)) TraceAdd("level", null, err.Split(':')[0], "compile", TraceOutcome.Error, err, 0, null);
            Raise("levelStart", Level, null, 0, null, null, 0);
        }

        // ------------------------------------------------------------------ spawning and caps

        private int liveSpawned;
        private readonly Dictionary<string, int> liveByClass = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> liveByTemplate = new(StringComparer.Ordinal);
        private int cappedSpawns;

        private string CapClass(LogicEntity e) => e.Kind == "projectile" ? "projectile" : e.Kind == "unit" ? "unit" : "entity";

        private void CountCap(LogicEntity e, int d)
        {
            string c = CapClass(e);
            liveByClass[c] = (liveByClass.TryGetValue(c, out var n) ? n : 0) + d;
            if (e.Template != null) liveByTemplate[e.Template] = (liveByTemplate.TryGetValue(e.Template, out n) ? n : 0) + d;
        }

        private int Live(string cls) => liveByClass.TryGetValue(cls, out var n) ? n : 0;

        /// <summary>
        /// Spawn an entity from a template at runtime (the `spawn`/`launch` actions). Checks the caps (engine-wide,
        /// per class, the template's `maxAlive`), describes it, has the factory build it, and returns it (registered,
        /// `spawn` queued), or null with a trace line when it was capped or failed.
        /// </summary>
        public LogicEntity Spawn(string template, Vec pos, float rotY, string team, LogicEntity owner, LogicContext ctx = null)
        {
            if (template == null) throw new LogicError("spawn: no template");
            if (template.StartsWith("@@")) template = template.Substring(2);
            JObject tdef = Templates.TryGetValue(template, out var td) ? td : null;
            string kind = (string)tdef?["kind"] ?? "unit";
            string cls = kind == "projectile" ? "projectile" : kind == "unit" ? "unit" : "entity";
            int perTemplate = (int?)tdef?["maxAlive"] ?? Caps.PerTemplate;
            perTemplate = Math.Min(perTemplate, LogicCaps.Max.PerTemplate);
            string capped =
                cls == "projectile" ? (Live("projectile") >= Caps.Projectiles ? $"projectiles ({Caps.Projectiles})" : null)
                : liveSpawned - Live("projectile") >= Caps.Entities ? $"spawned entities ({Caps.Entities})"
                : cls == "unit" && Live("unit") >= Caps.Units ? $"units ({Caps.Units})"
                : null;
            if (capped == null && (liveByTemplate.TryGetValue(template, out var nt) ? nt : 0) >= perTemplate) capped = $"\"{template}\" maxAlive ({perTemplate})";
            if (capped != null)
            {
                cappedSpawns++;
                if (cappedThisFrame++ == 0) TraceAdd(ctx?.Self?.Id ?? "?", ctx?.RuleLabel, null, ctx?.Event?.Name, TraceOutcome.Capped, $"spawn of \"{template}\" skipped: at the cap on {capped}", 0, null);
                return null;
            }
            if (LogicServices.Factory == null) throw new LogicError($"spawn: entity bodies aren't available yet");
            if (!LogicServices.Factory.HasTemplate(template)) throw new LogicError($"spawn: no template \"{template}\"");

            var e = Describe(template, null, null, team, owner);
            e.Kind = kind;
            e.Spawned = true;
            e.StaticPos = pos;
            if (!LogicServices.Factory.Build(e, pos, rotY, out string error))
            {
                pendingPlace.Remove(e);
                throw new LogicError($"spawn \"{template}\": {error}");
            }
            if (!byId.ContainsKey(e.Id ?? "")) Add(e);   // the factory registers it; a body-less build may not
            return e;
        }

        // ------------------------------------------------------------------ events

        private int droppedThisFrame;

        public void Raise(string name, LogicEntity subject, LogicEntity other, double value, Vec? pos, string text, int depth = 0)
        {
            if (depth > Caps.ChainDepth) { Drop(name, subject); return; }
            queue.Enqueue(new LogicEvent { Name = name, Subject = subject, Other = other, Value = value, Pos = pos, Text = text, Depth = depth, Seq = ++seq });
        }

        public void Signal(string name, IReadOnlyList<LogicEntity> to, double value, LogicEntity sender, int depth = 0)
        {
            if (depth > Caps.ChainDepth) { Drop("signal " + name, sender); return; }
            queue.Enqueue(new LogicEvent { Name = "signal", Text = name, Other = sender, Subject = sender, Value = value, Depth = depth, Seq = ++seq, Target = to ?? (object)Broadcast });
        }

        private static readonly object Broadcast = new();

        private void Drop(string what, LogicEntity e)
        {
            dropped++;
            if (droppedThisFrame++ == 0) TraceAdd(e?.Id ?? "?", null, null, what, TraceOutcome.Dropped, $"chain deeper than {Caps.ChainDepth} (a loop of events?); dropped", 0, null);
        }

        /// <summary>Deliver one event to every rule that hears it.</summary>
        private void Deliver(LogicEvent e)
        {
            switch (e.Target)
            {
                case RuleInstance ri:
                    TryFire(ri, e);
                    return;
                case IReadOnlyList<LogicEntity> to when e.Name == "signal":
                    foreach (var t in to) if (t != null) DeliverSignal(t, e);
                    return;
            }
            if (e.Name == "signal")   // broadcast: listeners only (and the level script)
            {
                if (byEvent.TryGetValue("signal", out var all))
                    foreach (var ri in all.ToArray()) if (ri.Rule.Of == null) TryFire(ri, e);
                return;
            }
            if (RuleCompiler.GlobalEvents.Contains(e.Name))
            {
                if (byEvent.TryGetValue(e.Name, out var all)) foreach (var ri in all.ToArray()) TryFire(ri, e);
                return;
            }

            var s = e.Subject;
            if (s != null)
            {
                if (s.Rules.Count > 0)
                    foreach (var ri in s.Rules.ToArray())
                        if (ri.Rule.Of == null && ri.Rule.Event == e.Name) TryFire(ri, e);
                if (e.Name is "damaged" or "healed") HpCrossings(s, e);
            }
            if (ofByEvent.TryGetValue(e.Name, out var ofs) && ofs.Count > 0 && s != null)
                foreach (var ri in ofs.ToArray())
                {
                    if (ri.Owner == s && ri.Rule.OfSource != null && !ri.Rule.OfSource.Contains("self")) continue;
                    if (ri.Rule.OfRange > 0 && ri.Owner.Kind != "level" && Vec.FlatDistance(ri.Owner.Position, e.Pos ?? s.Position) > ri.Rule.OfRange) continue;
                    var fctx = new LogicContext { World = this, Self = ri.Owner, It = s, Subject = s, Other = e.Other, Event = e };
                    bool match;
                    try { match = ri.Rule.Of(fctx, s); }
                    catch (LogicError err) { TraceRuleError(ri, e, err.Message); continue; }
                    if (match) TryFire(ri, e);
                }
        }

        private void DeliverSignal(LogicEntity t, LogicEvent e)
        {
            // Built-in inputs first; they need no listener.
            switch (e.Text)
            {
                case "enable": SetEnabled(t, true, e.Other); break;
                case "disable": SetEnabled(t, false, e.Other); break;
                case "toggle": SetEnabled(t, !t.Enabled, e.Other); break;
                case "destroy":
                    if (LogicServices.Factory != null) LogicServices.Factory.Despawn(t, true, false);
                    else Forget(t);
                    break;
                default:
                    if (LogicActions.Inputs.TryGetValue(e.Text ?? "", out var input)) input(t, e);
                    break;
            }
            foreach (var ri in t.Rules.ToArray())
                if (ri.Rule.Event == "signal" && ri.Rule.Of == null) TryFire(ri, e);
        }

        /// <summary>`hpBelow`: edge-triggered on the owner's damage, re-armed when healed above.</summary>
        private void HpCrossings(LogicEntity s, LogicEvent e)
        {
            if (s.Body == null || s.Body.MaxHp <= 0) return;
            double pct = s.Body.Hp / s.Body.MaxHp;
            foreach (var ri in s.Rules.ToArray())
            {
                if (ri.Rule.Event != "hpBelow" || ri.Rule.Of != null) continue;
                if (pct >= ri.Rule.Pct) { ri.HpArmed = true; continue; }
                if (!ri.HpArmed || e.Name != "damaged") continue;
                ri.HpArmed = false;
                TryFire(ri, new LogicEvent { Name = "hpBelow", Subject = s, Other = e.Other, Value = pct, Depth = e.Depth, Seq = e.Seq });
            }
        }

        private void TryFire(RuleInstance ri, LogicEvent e)
        {
            var r = ri.Rule;
            var owner = ri.Owner;
            if (r.CompileError != null)
            {
                if (!ri.ErrorTraced) { ri.ErrorTraced = true; TraceAdd(owner.Id, r.Label, r.Path, e.Name, TraceOutcome.Error, r.CompileError, e.Value, e.Other?.Id); }
                return;
            }
            // A destroyed owner still hears its own events that were queued before it went (its death); nothing else.
            if (owner.Destroyed && e.Subject != owner) return;
            if (!ri.Enabled || ri.Done || !owner.Enabled) return;
            if (owner.Flags.Count > 0 && owner.Flags.Contains("silenced")) return;
            if (Time + 1e-3 < ri.CooldownUntil) return;
            if (e.Target is not RuleInstance && !RuleCompiler.ParamsMatch(r, e)) return;

            if (firingsThisFrame >= Caps.FiringsPerFrame)
            {
                if (!throttledThisFrame) { throttledThisFrame = true; throttled++; TraceAdd(owner.Id, r.Label, r.Path, e.Name, TraceOutcome.Throttled, $"over {Caps.FiringsPerFrame} rule firings this frame; the rest run next frame", e.Value, e.Other?.Id); }
                deferred.Enqueue(new LogicEvent { Name = e.Name, Subject = e.Subject, Other = e.Other, Value = e.Value, Pos = e.Pos, Text = e.Text, Payload = e.Payload, Seq = e.Seq, Depth = e.Depth, Target = ri });
                return;
            }
            if (Time - ri.WindowStart >= 1) { ri.WindowStart = Time; ri.WindowCount = 0; }
            if (++ri.WindowCount > Caps.SameRulePerSecond)
            {
                dropped++;
                if (ri.WindowCount == Caps.SameRulePerSecond + 1) TraceAdd(owner.Id, r.Label, r.Path, e.Name, TraceOutcome.Dropped, $"fired over {Caps.SameRulePerSecond} times this second on this owner; dropped", e.Value, e.Other?.Id);
                return;
            }

            var ctx = new LogicContext { World = this, Self = owner, Subject = e.Subject, Other = e.Other, Event = e, Depth = e.Depth, RuleLabel = $"{owner.Id} / {r.Label}" };
            try
            {
                foreach (var c in r.Conditions)
                    if (!c(ctx))
                    {
                        if (!RuleCompiler.TimerEvents.Contains(e.Name) && e.Name is not ("inside" or "statusTick"))
                            TraceAdd(owner.Id, r.Label, r.Path, e.Name, TraceOutcome.ConditionFalse, null, e.Value, e.Other?.Id);
                        return;
                    }
            }
            catch (Exception ex) when (ex is LogicError or InvalidCastException or FormatException or ArgumentException or NullReferenceException)
            {
                TraceRuleError(ri, e, ex.Message);
                return;
            }

            firingsThisFrame++;
            firingsTotal++;
            if (r.Once) ri.Done = true;
            if (r.Cooldown > 0) ri.CooldownUntil = Time + r.Cooldown;
            TraceAdd(owner.Id, r.Label, r.Path, e.Name, TraceOutcome.Fired, r.Note, e.Value, e.Other?.Id);
            RunList(r.Do, ctx);
        }

        private void TraceRuleError(RuleInstance ri, LogicEvent e, string msg)
        {
            errors++;
            TraceAdd(ri.Owner.Id, ri.Rule.Label, ri.Rule.Path, e.Name, TraceOutcome.Error, msg, e.Value, e.Other?.Id);
        }

        // ------------------------------------------------------------------ the frame

        private int firingsThisFrame, actionsThisFrame, cappedThisFrame;
        private bool throttledThisFrame;
        private long firingsTotal, actionsTotal;
        private int throttled, dropped, errors;

        /// <summary>Advance by <paramref name="dt"/> seconds of game time and run everything due. Once per frame.</summary>
        public void Tick(double dt)
        {
            Frame++;
            firingsThisFrame = actionsThisFrame = cappedThisFrame = droppedThisFrame = 0;
            throttledThisFrame = false;
            if (dt > 0) Time += dt;

            // Deliveries the firing budget cut off last frame come first, in their original order.
            int n = deferred.Count;
            for (int i = 0; i < n && firingsThisFrame < Caps.FiringsPerFrame; i++) Deliver(deferred.Dequeue());

            while (stepTime + StepSeconds <= Time + 1e-9)
            {
                stepTime += StepSeconds;
                stepCount++;
                RunTimers(stepTime);
                if (stepCount % 2 == 0)
                {
                    PollNear();
                    try { Polled?.Invoke(this, stepTime); }
                    catch (Exception ex) { TraceAdd("level", null, null, "poll", TraceOutcome.Error, ex.Message, 0, null); }
                }
            }
            RunLaterSignals();
            RunDeferred();
            ResumeTasks();
            Drain();
        }

        private readonly Queue<(ActionList list, LogicContext ctx, Action action)> deferredRuns = new();

        /// <summary>
        /// Run an action list (in its context) at the next drain, under the budgets: for game callbacks outside the
        /// drain (a projectile's `onImpact` from its Update). Safe to call any time while the world is current.
        /// </summary>
        public void Defer(ActionList list, LogicContext ctx) { if (list != null && list.Count > 0) deferredRuns.Enqueue((list, ctx, null)); }

        /// <summary>Run arbitrary engine work at the next drain (counted as one rule firing).</summary>
        public void Defer(Action action) { if (action != null) deferredRuns.Enqueue((null, null, action)); }

        private void RunDeferred()
        {
            int n = deferredRuns.Count;
            for (int i = 0; i < n && firingsThisFrame < Caps.FiringsPerFrame; i++)
            {
                var (list, ctx, action) = deferredRuns.Dequeue();
                firingsThisFrame++;
                try
                {
                    if (action != null) action();
                    else RunList(list, ctx);
                }
                catch (Exception ex) when (ex is LogicError or InvalidCastException or ArgumentException or NullReferenceException)
                {
                    errors++;
                    TraceAdd(ctx?.Self?.Id ?? "?", ctx?.RuleLabel, null, ctx?.Event?.Name, TraceOutcome.Error, ex.Message, 0, null);
                }
            }
        }

        /// <summary>Drain the queue (events actions raise land at the back and run now too), up to the budget.</summary>
        public void Drain()
        {
            while (queue.Count > 0)
            {
                if (firingsThisFrame >= Caps.FiringsPerFrame)
                {
                    if (!throttledThisFrame) { throttledThisFrame = true; throttled++; TraceAdd("?", null, null, queue.Peek().Name, TraceOutcome.Throttled, $"over {Caps.FiringsPerFrame} rule firings this frame; {queue.Count} events wait for the next", 0, null); }
                    return;
                }
                Deliver(queue.Dequeue());
            }
        }

        private void RunTimers(double now)
        {
            if (timers.Count == 0) return;
            var game = LogicServices.Game;
            foreach (var ri in timers.ToArray())
            {
                if (double.IsNaN(ri.NextTime) || ri.NextTime > now + 1e-9) continue;
                var r = ri.Rule;
                if (r.Event == "after") { ri.NextTime = double.NaN; timers.Remove(ri); }
                else
                {
                    double next = r.Seconds;
                    if (r.Jitter > 0) next += Random.Range(-r.Jitter, r.Jitter);
                    ri.NextTime += Math.Max(0.1, next);
                    if (ri.NextTime <= now) ri.NextTime = now + Math.Max(0.1, r.Seconds);
                }
                if (!ri.Owner.Alive) continue;
                if (r.During == "night" && game != null && !game.IsNight) continue;
                if (r.During == "day" && game != null && game.IsNight) continue;
                ri.Ticks++;
                queue.Enqueue(new LogicEvent { Name = r.Event, Subject = ri.Owner, Value = ri.Ticks, Seq = ++seq, Target = ri });
            }
        }

        private readonly List<LogicEntity> scratch = [];

        private void PollNear()
        {
            if (nears.Count == 0) return;
            foreach (var ri in nears.ToArray())
            {
                var owner = ri.Owner;
                if (!owner.Alive || !owner.Enabled) continue;
                scratch.Clear();
                Query(owner.Position, ri.Rule.NearRange, scratch);
                var now = new HashSet<LogicEntity>();
                var fctx = new LogicContext { World = this, Self = owner };
                foreach (var c in scratch)
                {
                    if (c == owner || !c.Alive) continue;
                    if (Vec.FlatDistance(c.Position, owner.Position) > ri.Rule.NearRange) continue;
                    fctx.It = c;
                    bool ok;
                    try { ok = ri.Rule.NearFilter(fctx, c); }
                    catch (LogicError err) { TraceAdd(owner.Id, ri.Rule.Label, ri.Rule.Path, "near", TraceOutcome.Error, err.Message, 0, null); break; }
                    if (ok) now.Add(c);
                }
                string edge = ri.Rule.NearEdge;
                foreach (var c in now.OrderBy(x => x.Id, StringComparer.Ordinal))
                    if (!ri.NearInside.Contains(c) && edge is "enter" or "both")
                        queue.Enqueue(new LogicEvent { Name = "near", Subject = owner, Other = c, Value = 1, Text = "enter", Seq = ++seq, Target = ri });
                foreach (var c in ri.NearInside.OrderBy(x => x.Id, StringComparer.Ordinal))
                    if (!now.Contains(c) && edge is "exit" or "both")
                        queue.Enqueue(new LogicEvent { Name = "near", Subject = owner, Other = c, Value = 0, Text = "exit", Seq = ++seq, Target = ri });
                ri.NearInside = now;
            }
        }

        /// <summary>Candidates within <paramref name="radius"/> (0 = everywhere): the installed ISpatial, else the registry.</summary>
        public void Query(Vec center, float radius, List<LogicEntity> into)
        {
            if (LogicServices.Spatial != null) LogicServices.Spatial.Query(center, radius, into);
            else
            {
                float r2 = radius * radius;
                foreach (var e in byId.Values)
                    if (e.Exists && e.Kind != "level" && (radius <= 0 || Vec.FlatDistanceSq(e.Position, center) <= r2)) into.Add(e);
            }
            LogicServices.ExtraCandidates?.Invoke(center, radius, into);
        }

        // ------------------------------------------------------------------ vars

        /// <summary>Set a var on an entity (or the level), firing `varChanged` when the value changes.</summary>
        public void SetVar(LogicEntity e, string name, Value v, int depth = 0)
        {
            e ??= Level;
            if (name.StartsWith("$")) name = name.Substring(1);
            bool changed = !e.Vars.TryGetValue(name, out var old) || !old.Equals(v);
            e.Vars[name] = v;
            if (changed) Raise("varChanged", e, null, v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num : 0, null, name, depth);
        }

        public Value GetVar(LogicEntity e, string name) => (e ?? Level).Vars.TryGetValue(name.TrimStart('$'), out var v) ? v : Value.None;

        // ------------------------------------------------------------------ rules by id

        public int SetRuleEnabled(LogicEntity e, string ruleId, bool on)
        {
            int n = 0;
            foreach (var ri in e.Rules)
                if (ri.Rule.Id == ruleId) { ri.Enabled = on; n++; }
            return n;
        }

        // ------------------------------------------------------------------ trace and budget

        public const int TraceSize = 100;
        private readonly LinkedList<TraceEntry> trace = new();

        /// <summary>The last <see cref="TraceSize"/> rule firings, throttles and errors, oldest first.</summary>
        public IReadOnlyList<TraceEntry> Trace => trace.ToList();

        internal void TraceAdd(string owner, string rule, string path, string ev, TraceOutcome outcome, string msg, double value, string other)
        {
            var t = new TraceEntry { Time = Time, Frame = Frame, Owner = owner, Rule = rule, Path = path, Event = ev, Outcome = outcome, Message = msg, Value = value, Other = other };
            trace.AddLast(t);
            while (trace.Count > TraceSize) trace.RemoveFirst();
            if (outcome != TraceOutcome.ConditionFalse) TraceListener?.Invoke(t);
            try { LogicEvents.OnTraced(t); }
            catch (Exception) { /* a listener's problem, not the rule's */ }
        }

        /// <summary>The host's log for errors/throttles (LogicManager logs them once each).</summary>
        public Action<TraceEntry> TraceListener;

        public BudgetSnapshot Budget() => new()
        {
            Caps = Caps,
            FiringsThisFrame = firingsThisFrame,
            ActionsThisFrame = actionsThisFrame,
            LiveEntities = liveSpawned - Live("projectile"),
            LiveUnits = Live("unit"),
            LiveProjectiles = Live("projectile"),
            Throttled = throttled,
            Dropped = dropped,
            CappedSpawns = cappedSpawns,
            Errors = errors,
            QueuedEvents = queue.Count + deferred.Count,
        };

        public long FiringsTotal => firingsTotal;
        public long ActionsTotal => actionsTotal;
    }
}
