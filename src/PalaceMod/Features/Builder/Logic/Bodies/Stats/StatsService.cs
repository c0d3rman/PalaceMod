using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    IStatModifiers (LogicServices.Stats): modifier stacks, statuses and flags per entity, written to the game's fields
    (StatWriter), and the damage factor the Hp.TakeDamage prefix applies (LogicStatsManager). docs/builder-engine.md,
    "Statuses". Timers run on the world's 10 Hz poll of game time; `for: "night"/"day"` end at the dawn / dusk hooks.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    /// <summary>Per-entity state: modifiers, statuses, flags this part set, the fields it has written.</summary>
    internal sealed class Sheet
    {
        public LogicEntity E;
        public readonly List<Mod> Mods = [];
        public readonly List<StatusState> Statuses = [];
        public readonly HashSet<string> Touched = new(StringComparer.Ordinal);
        public readonly HashSet<string> MyFlags = new(StringComparer.Ordinal);
        public StatWriter.Parts Parts;
        public readonly List<int> GoKeys = [];
        public TaggedObject Tagged;

        public StatusState Status(string id) => Statuses.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>A conditional modifier's compiled `from` / `when`.</summary>
    internal sealed class ModCondition
    {
        public Func<LogicContext, LogicEntity, bool> From;
        public Func<LogicContext, Value> When;
        public string FromSrc, WhenSrc;
    }

    /// <summary>The game side's per-status state: its rules handle, drift key, tint and fx.</summary>
    internal sealed class StatusExt
    {
        public object RulesHandle;
        public string DriftKey;
        public StatusVisuals.Applied Visual;
    }

    /// <summary>What a damage call carries besides its amount (types; the action that made it).</summary>
    internal sealed class DamageInfo
    {
        public HashSet<string> Types;
        public bool Splash;
        public LogicEntity Source;
    }

    internal sealed class StatsService : IStatModifiers
    {
        public static readonly StatsService Instance = new();
        private static Log log => Log.For<LogicStatsManager>();

        private readonly Dictionary<LogicEntity, Sheet> sheets = [];
        private readonly List<Sheet> order = [];
        private readonly Dictionary<int, Sheet> byGo = [];
        private long modOrder;
        private int damageMods;          // live damageTaken / damageMult modifiers, all entities
        private int flagged;             // entities with any flag
        internal readonly HashSet<TaggedObject> Untargetable = [];

        public bool AnyDamageMods => damageMods > 0 || flagged > 0;
        public bool AnyFlags => flagged > 0;

        public void Reset()
        {
            foreach (var s in order.ToList()) Drop(s, restore: false);
            sheets.Clear();
            order.Clear();
            byGo.Clear();
            Untargetable.Clear();
            damageMods = flagged = 0;
        }

        // ------------------------------------------------------------------ sheets

        private Sheet SheetOf(LogicEntity e, bool create)
        {
            if (e == null) return null;
            if (sheets.TryGetValue(e, out var s)) return s;
            if (!create) return null;
            s = new Sheet { E = e };
            sheets[e] = s;
            order.Add(s);
            Index(s);
            return s;
        }

        private void Index(Sheet s)
        {
            foreach (var k in s.GoKeys) if (byGo.TryGetValue(k, out var cur) && cur == s) byGo.Remove(k);
            s.GoKeys.Clear();
            var go = s.E.Go();
            if (go == null) return;
            void Add(GameObject g) { if (g != null && !s.GoKeys.Contains(g.GetInstanceID())) { s.GoKeys.Add(g.GetInstanceID()); byGo[g.GetInstanceID()] = s; } }
            Add(go);
            s.Tagged = go.GetComponentInChildren<TaggedObject>(true);
            if (s.Tagged != null) Add(s.Tagged.gameObject);
            if (go.GetComponentInChildren<Hp>(true) is { } hp) Add(hp.gameObject);
        }

        /// <summary>The sheet of the entity a game object belongs to (no entity is made for it).</summary>
        public Sheet SheetOfGo(GameObject go)
        {
            if (go == null || order.Count == 0) return null;
            if (byGo.TryGetValue(go.GetInstanceID(), out var s)) return s;
            var t = go.GetComponentInParent<TaggedObject>();
            return t != null && byGo.TryGetValue(t.gameObject.GetInstanceID(), out s) ? s : null;
        }

        private void Drop(Sheet s, bool restore)
        {
            foreach (var st in s.Statuses.ToList()) EndStatus(s, st, raise: false, write: false);
            damageMods -= s.Mods.Count(IsDamageMod);
            s.Mods.Clear();
            SetFlags(s, []);
            if (restore) Write(s);
            foreach (var k in s.GoKeys) if (byGo.TryGetValue(k, out var cur) && cur == s) byGo.Remove(k);
            sheets.Remove(s.E);
            order.Remove(s);
        }

        private static bool IsDamageMod(Mod m) => m.Stat is "damageTaken" or "damageMult";

        // ------------------------------------------------------------------ IStatModifiers

        public void Add(LogicEntity e, StatModifier m)
        {
            if (e == null || m == null) return;
            if (!StatMath.IsStat(m.Stat)) throw new LogicError($"\"{m.Stat}\" isn't a stat (stats: {string.Join(", ", StatMath.Names)})");
            var s = SheetOf(e, true);
            var now = LogicWorld.Current?.Time ?? 0;
            if (m.Key != null)
            {
                damageMods -= s.Mods.Count(x => x.Owner == null && x.Key == m.Key && IsDamageMod(x));
                s.Mods.RemoveAll(x => x.Owner == null && x.Key == m.Key);
            }
            var mod = new Mod
            {
                Stat = m.Stat, Set = m.Set, Add = m.Add, Mult = m.Mult, Key = m.Key, Source = m.Source,
                ExpireAt = m.Seconds > 0 ? now + m.Seconds : double.PositiveInfinity,
                Until = m.Until is "night" or "day" ? m.Until : null,
                Order = ++modOrder,
            };
            ApplyConditions(mod, m.Extra, $"setStat {m.Stat}");
            s.Mods.Add(mod);
            if (IsDamageMod(mod)) damageMods++;
            Write(s);
        }

        public double Get(LogicEntity e, string stat)
        {
            if (e == null || !StatMath.IsStat(stat)) return double.NaN;
            sheets.TryGetValue(e, out var s);
            double b;
            if (StatMath.Multipliers.Contains(stat)) b = e.Kind == "level" ? double.NaN : 1;
            else if (stat == "hp") b = e.Body != null && e.Body.MaxHp > 0 ? e.Body.Hp : double.NaN;
            else b = StatWriter.BaseOf(PartsOf(s, e), stat);
            if (double.IsNaN(b)) return double.NaN;
            if (stat == "hp") return b;
            if (stat == "income" && e.Flags.Contains("noIncome")) return 0;
            if (s == null || s.Mods.Count == 0) return b;
            double v = StatMath.Combine(b, s.Mods, m => m.Count, m => m.Stat == stat && !m.Conditional);
            if (stat == "income") v = Math.Max(0, Math.Round(v));
            return v;
        }

        public int Stacks(LogicEntity e, string status) =>
            e != null && sheets.TryGetValue(e, out var s) && s.Status(status) is { } st ? st.Stacks : 0;

        public void ApplyStatus(LogicEntity e, string status, double? seconds, int stacks, LogicEntity source) =>
            Apply(null, e, status, seconds, null, stacks, source);

        public void RemoveStatus(LogicEntity e, string status) => Remove(null, e, status, 0);

        // ------------------------------------------------------------------ statuses

        /// <summary>Apply a status (the `applyStatus` action and IStatModifiers). <paramref name="until"/>: "night"/"day".</summary>
        public void Apply(LogicContext ctx, LogicEntity e, string status, double? seconds, string until, int stacks, LogicEntity source)
        {
            var world = LogicWorld.Current;
            if (e == null || world == null) return;
            if (!world.Statuses.TryGetValue(status ?? "", out var def)) throw new LogicError($"no status \"{status}\"");
            var s = SheetOf(e, true);
            var st = s.Status(status);
            bool fresh = st == null;
            if (fresh)
            {
                st = new StatusState
                {
                    Id = status,
                    Stacking = (string)def["stacking"] ?? "refresh",
                    MaxStacks = Math.Max(1, (int?)def["maxStacks"] ?? 1),
                    Tick = Math.Max(0, (double?)def["tick"] ?? 0),
                };
                if (st.Tick > 0) st.Tick = Math.Max(0.1, st.Tick);
            }
            double? dur = seconds ?? (def["duration"]?.Type is JTokenType.Integer or JTokenType.Float ? (double)def["duration"] : null);
            int before = st.Stacks;
            bool started = st.Apply(world.Time, until != null ? null : dur, stacks);
            if (until != null) st.Until = until;
            if (source != null) st.Source = source;
            if (started)
            {
                if (fresh) s.Statuses.Add(st);
                Start(s, st, def, ctx);
                Raise(ctx, "statusStart", e, source, st.Stacks, status);
            }
            if (started || st.Stacks != before) Refresh(s);
        }

        /// <summary>Remove <paramref name="stacks"/> stacks (all when &lt;= 0).</summary>
        public void Remove(LogicContext ctx, LogicEntity e, string status, int stacks)
        {
            if (e == null || !sheets.TryGetValue(e, out var s) || s.Status(status) is not { } st) return;
            int before = st.Stacks;
            st.RemoveStacks(stacks);
            if (!st.Active) EndStatus(s, st, raise: true, write: true, ctx: ctx, endedStacks: before);
            else Refresh(s);
        }

        private void Start(Sheet s, StatusState st, JObject def, LogicContext ctx)
        {
            var ext = new StatusExt();
            st.Ext = ext;
            if (def["mods"] is JObject mods)
                foreach (var p in mods.Properties())
                {
                    if (p.Name == "drift") { ext.DriftKey = StartDrift(s.E, st, p.Value as JObject); continue; }
                    if (!StatMath.IsStat(p.Name) || p.Value is not JObject mo) { log.Warn($"status \"{st.Id}\": mods.{p.Name} isn't a stat; ignored"); continue; }
                    var mod = new Mod
                    {
                        Stat = p.Name,
                        Set = Num(mo["value"]) ?? Num(mo["set"]),
                        Add = Num(mo["add"]),
                        Mult = Num(mo["mult"]),
                        Owner = st,
                        Source = st.Source,
                        Order = ++modOrder,
                    };
                    ApplyConditions(mod, mo, $"statuses.{st.Id}.mods.{p.Name}");
                    s.Mods.Add(mod);
                    if (IsDamageMod(mod)) damageMods++;
                }
            var world = LogicWorld.Current;
            if (def["rules"] is JArray rules && rules.Count > 0 && world != null)
                ext.RulesHandle = world.AttachRules(s.E, ScopeRules(rules, st.Id), $"statuses.{st.Id}.rules");
            if (def["visual"] is JObject vis) ext.Visual = StatusVisuals.Apply(s.E, vis);
        }

        private void EndStatus(Sheet s, StatusState st, bool raise, bool write, LogicContext ctx = null, int endedStacks = 0)
        {
            s.Statuses.Remove(st);
            damageMods -= s.Mods.Count(m => m.Owner == st && IsDamageMod(m));
            s.Mods.RemoveAll(m => m.Owner == st);
            if (st.Ext is StatusExt ext)
            {
                if (ext.RulesHandle != null) LogicWorld.Current?.DetachRules(s.E, ext.RulesHandle);
                if (ext.DriftKey != null) Drifts.Remove(s.E, ext.DriftKey);
                StatusVisuals.Remove(ext.Visual);
            }
            st.Ext = null;
            st.Stacks = 0;
            if (raise) Raise(ctx, "statusEnd", s.E, st.Source as LogicEntity, endedStacks, st.Id);
            if (write) Refresh(s);
        }

        /// <summary>
        /// A status's rules hear only their own status's statusStart/statusTick/statusEnd: their `when` gets
        /// `status: id` unless it names one.
        /// </summary>
        private static JArray ScopeRules(JArray rules, string id)
        {
            var copy = (JArray)rules.DeepClone();
            foreach (var r in copy.OfType<JObject>())
            {
                var when = r["when"];
                string on = when?.Type == JTokenType.String ? (string)when : (string)when?["on"];
                if (on is not ("statusStart" or "statusTick" or "statusEnd")) continue;
                if (when is JObject wo) { if (wo["of"] == null && wo["status"] == null) wo["status"] = id; }
                else r["when"] = new JObject { ["on"] = on, ["status"] = id };
            }
            return copy;
        }

        private static string StartDrift(LogicEntity e, StatusState st, JObject d)
        {
            if (d == null) return null;
            string key = "status:" + st.Id;
            double speed = Num(d["speed"]) ?? 1;
            Func<LogicContext, Value> toward = Compile(d["toward"], $"statuses.{st.Id}.mods.drift.toward");
            Func<LogicContext, Value> from = Compile(d["from"], $"statuses.{st.Id}.mods.drift.from");
            Vector3 fixedDir = d["dir"] is JArray a && a.Count >= 2
                ? (a.Count == 2 ? new Vector3((float)a[0], 0, (float)a[1]) : new Vector3((float)a[0], (float)a[1], (float)a[2])).normalized
                : Vector3.zero;
            Drifts.Set(e, key, () =>
            {
                var world = LogicWorld.Current;
                if (world == null || !e.Exists) return Vector3.zero;
                var ctx = new LogicContext { World = world, Self = e, Other = st.Source as LogicEntity, It = e };
                Vector3 here = e.Position.ToVector3();
                Vector3 dir = fixedDir;
                if (toward != null && PosOf(toward(ctx)) is { } t) dir = Flat(t - here);
                else if (from != null && PosOf(from(ctx)) is { } f) dir = Flat(here - f);
                return dir * (float)speed;
            }, 0);
            return key;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero; }

        private static Vector3? PosOf(Value v) => v.Kind switch
        {
            ValueKind.Position => v.Pos.ToVector3(),
            ValueKind.Entity => v.AsEntity.Position.ToVector3(),
            ValueKind.Set => v.AsSet.Count > 0 ? v.AsSet[0].Position.ToVector3() : null,
            _ => null,
        };

        private static Func<LogicContext, Value> Compile(JToken t, string path)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return LogicExpr.Compile((string)t, path);
            if (t is JArray a && a.Count >= 2)
            {
                var v = a.Count == 2 ? new Vec((float)a[0], 0, (float)a[1]) : new Vec((float)a[0], (float)a[1], (float)a[2]);
                return _ => Value.Position(v);
            }
            return null;
        }

        private static double? Num(JToken t) => t?.Type is JTokenType.Integer or JTokenType.Float ? (double)t : null;

        private static void ApplyConditions(Mod mod, JObject extra, string path)
        {
            if (extra == null) return;
            var cond = new ModCondition();
            if (extra["types"] is JArray types) mod.Types = types.Select(t => (string)t).Where(t => t != null).ToArray();
            else if (extra["types"]?.Type == JTokenType.String) mod.Types = [(string)extra["types"]];
            if (Num(extra["chance"]) is double c) mod.Chance = Math.Max(0, Math.Min(1, c));
            if (extra["from"]?.Type == JTokenType.String) { cond.FromSrc = (string)extra["from"]; cond.From = LogicExpr.Filter(cond.FromSrc, path + ".from"); }
            if (extra["when"]?.Type == JTokenType.String) { cond.WhenSrc = (string)extra["when"]; cond.When = LogicExpr.Compile(cond.WhenSrc, path + ".when"); }
            mod.Conditional = mod.Types != null || mod.Chance < 1 || cond.From != null || cond.When != null;
            if (cond.From != null || cond.When != null) mod.Condition = cond;
        }

        private static void Raise(LogicContext ctx, string ev, LogicEntity subject, LogicEntity other, double value, string text)
        {
            if (ctx != null) ctx.Raise(ev, subject, other, value, null, text);
            else LogicEvents.Raise(ev, subject, other, value, null, text);
        }

        // ------------------------------------------------------------------ flags, writing

        private void Refresh(Sheet s)
        {
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var world = LogicWorld.Current;
            foreach (var st in s.Statuses)
                if (st.Active && world != null && world.Statuses.TryGetValue(st.Id, out var def) && def["flags"] is JArray fa)
                    foreach (var f in fa) if (f.Type == JTokenType.String) flags.Add((string)f);
            SetFlags(s, flags);
            Write(s);
        }

        private void SetFlags(Sheet s, HashSet<string> flags)
        {
            bool had = s.MyFlags.Count > 0;
            foreach (var f in s.MyFlags) if (!flags.Contains(f)) s.E.Flags.Remove(f);
            foreach (var f in flags) s.E.Flags.Add(f);
            s.MyFlags.Clear();
            s.MyFlags.UnionWith(flags);
            bool has = s.MyFlags.Count > 0;
            if (had != has) flagged += has ? 1 : -1;
            if (s.Tagged == null) Index(s);
            if (s.Tagged != null)
            {
                if (flags.Contains("untargetable")) Untargetable.Add(s.Tagged);
                else Untargetable.Remove(s.Tagged);
            }
            if (flags.Contains("stunned") || flags.Contains("rooted")) s.Touched.Add("frozen");
        }

        private static StatWriter.Parts PartsOf(Sheet s, LogicEntity e)
        {
            var go = e.Go();
            if (s == null) return go != null ? StatWriter.Of(go) : null;
            if (s.Parts == null || s.Parts.Go != go) s.Parts = go != null ? StatWriter.Of(go) : null;
            return s.Parts;
        }

        private void Write(Sheet s)
        {
            foreach (var m in s.Mods) s.Touched.Add(m.Stat);
            var p = PartsOf(s, s.E);
            if (p == null) return;
            bool frozen = s.E.Flags.Contains("stunned") || s.E.Flags.Contains("rooted");
            try { StatWriter.Write(p, stat => Get(s.E, stat), s.Touched.Contains, frozen); }
            catch (Exception ex) { log.Error($"writing stats of {s.E.Id} failed", ex); }
        }

        /// <summary>Re-write an entity's stats (e.g. after another part replaced its attack or movement).</summary>
        public void Rewrite(LogicEntity e)
        {
            if (e != null && sheets.TryGetValue(e, out var s)) { s.Parts = null; Write(s); }
        }

        /// <summary>
        /// Another part just wrote a new base value into the game fields of <paramref name="stat"/> (Steering's
        /// `movement.speed`, `setMovement`): remember the live values as the base and apply the modifiers on top.
        /// </summary>
        public void BaseChanged(LogicEntity e, string stat)
        {
            if (e == null || !sheets.TryGetValue(e, out var s)) return;
            StatWriter.Forget(PartsOf(s, e), stat);
            if (s.Touched.Contains(stat) || s.Touched.Contains("timeScale") || s.Touched.Contains("frozen")) Write(s);
        }

        /// <summary>
        /// The factor modifiers put on <paramref name="stat"/> for fields this part doesn't write (Builder attacks'
        /// Range/Cooldown): adds and mults over a base of 1 (a `set` needs a base, so it's left out); attackCooldown
        /// and moveSpeed include timeScale. 1 when nothing modifies it.
        /// </summary>
        public double Factor(LogicEntity e, string stat)
        {
            if (e == null || !sheets.TryGetValue(e, out var s) || s.Mods.Count == 0) return 1;
            double f = StatMath.Combine(1, s.Mods, m => m.Count, m => m.Stat == stat && !m.Conditional && m.Set == null);
            double ts = StatMath.Combine(1, s.Mods, m => m.Count, m => m.Stat == "timeScale" && !m.Conditional);
            if (ts <= 0) ts = 0.01;
            if (stat == "attackCooldown") f /= ts;
            if (stat == "moveSpeed") f *= ts;
            return Math.Max(0, f);
        }

        public bool HasFlag(GameObject go, string flag) => flagged > 0 && SheetOfGo(go) is { } s && s.MyFlags.Contains(flag);

        // ------------------------------------------------------------------ time

        /// <summary>The world's 10 Hz poll: expire modifiers and stacks, fire statusTick.</summary>
        public void Poll(LogicWorld world, double now)
        {
            if (order.Count == 0) return;
            foreach (var s in order.ToArray())
            {
                if (!s.E.Exists) { Drop(s, restore: false); continue; }
                bool changed = false;
                int expired = s.Mods.RemoveAll(m => m.Owner == null && m.ExpireAt <= now + 1e-9 && Dec(m));
                changed |= expired > 0;
                foreach (var st in s.Statuses.ToArray())
                {
                    int ticks = st.TicksDue(now);
                    for (int i = 0; i < ticks; i++) LogicEvents.Raise("statusTick", s.E, st.Source as LogicEntity, st.Stacks, null, st.Id);
                    int before = st.Stacks;
                    if (st.Expire(now))
                    {
                        if (!st.Active) EndStatus(s, st, raise: true, write: false, endedStacks: before);
                        changed = true;
                    }
                }
                if (changed) Refresh(s);
            }
        }

        private bool Dec(Mod m)
        {
            if (IsDamageMod(m)) damageMods--;
            return true;
        }

        /// <summary>Dawn (<paramref name="until"/> "night") or dusk ("day"): end what lasts until then.</summary>
        public void EndPhase(string until)
        {
            foreach (var s in order.ToArray())
            {
                if (!s.E.Exists) continue;
                bool changed = s.Mods.RemoveAll(m => m.Owner == null && m.Until == until && Dec(m)) > 0;
                foreach (var st in s.Statuses.Where(x => x.Until == until).ToArray())
                {
                    EndStatus(s, st, raise: true, write: false, endedStacks: st.Stacks);
                    changed = true;
                }
                if (changed) Refresh(s);
            }
        }

        // ------------------------------------------------------------------ damage

        /// <summary>
        /// The factor a hit is scaled by: the victim's damageTaken and the attacker's damageMult, each with their
        /// conditional modifiers tested against this hit. <paramref name="victimSheet"/>/<paramref name="attackerSheet"/>
        /// may be null.
        /// </summary>
        public double DamageFactor(Sheet victimSheet, Sheet attackerSheet, LogicEntity victim, LogicEntity attacker, Func<HashSet<string>> types)
        {
            double f = 1;
            if (victimSheet != null && victimSheet.Mods.Count > 0) f *= Side(victimSheet, "damageTaken", victim, attacker, victim, types);
            if (attackerSheet != null && attackerSheet.Mods.Count > 0) f *= Side(attackerSheet, "damageMult", attacker, attacker, victim, types);
            return f;
        }

        private static double Side(Sheet s, string stat, LogicEntity carrier, LogicEntity attacker, LogicEntity victim, Func<HashSet<string>> types)
        {
            bool any = false;
            foreach (var m in s.Mods) if (m.Stat == stat) { any = true; break; }
            if (!any) return 1;
            var world = LogicWorld.Current;
            LogicContext ctx = null;
            HashSet<string> t = null;
            return StatMath.Combine(1, s.Mods, m => m.Count, m =>
            {
                if (m.Stat != stat) return false;
                if (!m.Conditional) return true;
                if (m.Types != null) { t ??= types(); if (!StatMath.TypesMatch(m.Types, t)) return false; }
                if (m.Condition is ModCondition c)
                {
                    if (world == null) return false;
                    ctx ??= DamageContext(world, carrier, attacker, victim);
                    try
                    {
                        if (c.From != null && (attacker == null || !c.From(ctx, attacker))) return false;
                        if (c.When != null && !Values.ToBool(c.When(ctx))) return false;
                    }
                    catch (LogicError err)
                    {
                        log.Warn($"{carrier.Id}: {stat} condition: {err.Message}");
                        return false;
                    }
                }
                if (m.Chance < 1 && (world == null || !world.Random.Chance(m.Chance))) return false;
                return true;
            });
        }

        private static LogicContext DamageContext(LogicWorld world, LogicEntity carrier, LogicEntity attacker, LogicEntity victim)
        {
            var ctx = new LogicContext { World = world, Self = carrier, Subject = victim, Other = attacker, It = attacker };
            ctx.Locals = new Dictionary<string, Value>(StringComparer.Ordinal)
            {
                ["attacker"] = Value.Entity(attacker),
                ["victim"] = Value.Entity(victim),
                ["dist"] = Value.Number(attacker != null && victim != null ? Vec.FlatDistance(attacker.Position, victim.Position) : 0),
            };
            return ctx;
        }

        // ------------------------------------------------------------------ savestates

        public JToken Capture(LogicEntity e)
        {
            if (e == null || !sheets.TryGetValue(e, out var s) || (s.Mods.Count == 0 && s.Statuses.Count == 0)) return null;
            double now = LogicWorld.Current?.Time ?? 0;
            var mods = new JArray();
            foreach (var m in s.Mods.Where(m => m.Owner == null))
                mods.Add(new JObject
                {
                    ["stat"] = m.Stat, ["set"] = m.Set, ["add"] = m.Add, ["mult"] = m.Mult, ["key"] = m.Key, ["until"] = m.Until,
                    ["left"] = double.IsPositiveInfinity(m.ExpireAt) ? null : m.ExpireAt - now,
                    ["source"] = (m.Source as LogicEntity)?.Id,
                    ["extra"] = m.Conditional ? ExtraOf(m) : null,
                });
            var statuses = new JArray();
            foreach (var st in s.Statuses.Where(x => x.Active))
                statuses.Add(new JObject
                {
                    ["id"] = st.Id, ["stacks"] = st.Stacks, ["until"] = st.Until,
                    ["left"] = double.IsPositiveInfinity(st.ExpireAt) ? null : st.ExpireAt - now,
                    ["stackLeft"] = st.StackExpiry.Count > 0 ? new JArray(st.StackExpiry.Select(x => double.IsPositiveInfinity(x) ? (JToken)null : x - now)) : null,
                    ["nextTick"] = double.IsPositiveInfinity(st.NextTick) ? null : st.NextTick - now,
                    ["source"] = (st.Source as LogicEntity)?.Id,
                });
            return new JObject { ["mods"] = mods, ["statuses"] = statuses };
        }

        private static JObject ExtraOf(Mod m)
        {
            var o = new JObject();
            if (m.Types != null) o["types"] = new JArray(m.Types);
            if (m.Chance < 1) o["chance"] = m.Chance;
            if (m.Condition is ModCondition c) { if (c.FromSrc != null) o["from"] = c.FromSrc; if (c.WhenSrc != null) o["when"] = c.WhenSrc; }
            return o;
        }

        public void Restore(LogicEntity e, JToken state)
        {
            var world = LogicWorld.Current;
            if (e == null || state is not JObject o || world == null) return;
            if (sheets.TryGetValue(e, out var old)) Drop(old, restore: true);
            double now = world.Time;
            foreach (var m in (o["mods"] as JArray ?? []).OfType<JObject>())
            {
                double? left = Num(m["left"]);
                if (left is <= 0) continue;
                Add(e, new StatModifier
                {
                    Stat = (string)m["stat"], Set = Num(m["set"]), Add = Num(m["add"]), Mult = Num(m["mult"]), Key = (string)m["key"],
                    Until = (string)m["until"], Seconds = left ?? 0, Source = world.Find((string)m["source"]), Extra = m["extra"] as JObject,
                });
            }
            foreach (var so in (o["statuses"] as JArray ?? []).OfType<JObject>())
            {
                string id = (string)so["id"];
                if (!world.Statuses.TryGetValue(id ?? "", out var def)) continue;
                var s = SheetOf(e, true);
                var st = new StatusState
                {
                    Id = id,
                    Stacking = (string)def["stacking"] ?? "refresh",
                    MaxStacks = Math.Max(1, (int?)def["maxStacks"] ?? 1),
                    Tick = Math.Max(0, (double?)def["tick"] ?? 0),
                    Stacks = Math.Max(1, (int?)so["stacks"] ?? 1),
                    Until = (string)so["until"],
                    Source = world.Find((string)so["source"]),
                };
                st.ExpireAt = Num(so["left"]) is double l ? now + l : double.PositiveInfinity;
                if (so["stackLeft"] is JArray sl)
                {
                    foreach (var x in sl) st.StackExpiry.Add(Num(x) is double xl ? now + xl : double.PositiveInfinity);
                    st.Stacks = st.StackExpiry.Count;
                }
                st.NextTick = Num(so["nextTick"]) is double nt ? now + nt : st.Tick > 0 ? now + st.Tick : double.PositiveInfinity;
                if (st.Stacks <= 0) continue;
                s.Statuses.Add(st);
                Start(s, st, def, null);
                Refresh(s);
            }
        }

        // ------------------------------------------------------------------ debug

        public string Describe(LogicEntity e)
        {
            if (e == null) return "none";
            var parts = new List<string>();
            foreach (var stat in StatMath.Names)
            {
                double v = Get(e, stat);
                if (!double.IsNaN(v)) parts.Add($"{stat}={v:0.###}");
            }
            if (sheets.TryGetValue(e, out var s))
            {
                double now = LogicWorld.Current?.Time ?? 0;
                if (s.Statuses.Count > 0) parts.Add("statuses=[" + string.Join(", ", s.Statuses.Select(st => $"{st.Id}x{st.Stacks}{(double.IsPositiveInfinity(st.ExpireAt) ? "" : $" {st.Remaining(now):0.0}s")}")) + "]");
                if (s.MyFlags.Count > 0) parts.Add("flags=[" + string.Join(",", s.MyFlags) + "]");
                int timed = s.Mods.Count(m => m.Owner == null);
                if (timed > 0) parts.Add($"setStat mods={timed}");
            }
            return $"{e.Id}: {string.Join(" ", parts)}";
        }
    }
}
