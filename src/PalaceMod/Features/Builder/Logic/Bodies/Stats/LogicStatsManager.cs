using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;

/*
    Stats and statuses of the behaviour engine (docs/builder-engine.md, "Statuses", L2): installs LogicServices.Stats
    and StatusFlags.Has, registers the Health and Stats actions, and patches the game's own calls for the synchronous
    modifiers (damage, flags, untargetable, income). Infrastructure: no settings; every patch early-outs outside a logic
    session, so vanilla runs are untouched.
*/
namespace PalaceMod
{
    public class LogicStatsManager : BaseManager
    {
        private static StatsService Stats => StatsService.Instance;

        /// <summary>
        /// The damage call in progress from a `damage` action (or another L2 part's <see cref="Deal"/>): its types and
        /// credited source, or null. L1's damage hooks can read <c>Source</c> to credit `hit`/`kill` to a body-less
        /// source (a logic object, the level).
        /// </summary>
        internal static DamageInfo CurrentDamage => scope.Count > 0 ? scope[scope.Count - 1] : null;
        private static readonly List<DamageInfo> scope = [];

        /// <summary>A scripted `kill` is running (not a hit: damage modifiers and link damage shares skip it).</summary>
        internal static bool killing;
        /// <summary>A hit already modified elsewhere (a link's damage share): this victim's modifiers don't apply again.</summary>
        internal static int rawHits;
        private static int weaponDepth;
        private static readonly bool[] weaponSplash = new bool[8], weaponMagic = new bool[8];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LogicServices.Stats = Stats;
            StatusFlags.Has = (go, flag) => LogicWorld.Current != null && Stats.HasFlag(go, flag);
            LogicServices.CurrentDamageSource = () => CurrentDamage?.Source;

            LogicActions.Register("damage", DamageAction, ActionFlags.PerTarget);
            LogicActions.Register("heal", (ctx, p) => { if (HpOf(p.Target) is { } hp) hp.Heal((float)Math.Max(0, p.Number("amount"))); }, ActionFlags.PerTarget);
            LogicActions.Register("kill", (ctx, p) => Kill(p.Target, p.Has("source") ? p.Entity("source") : ctx.Self), ActionFlags.PerTarget);
            LogicActions.Register("revive", Revive, ActionFlags.PerTarget);
            LogicActions.Register("setHp", (ctx, p) => SetHp(p.Target, p.Number("value"), ctx.Self), ActionFlags.PerTarget);
            LogicActions.Register("setStat", SetStat, ActionFlags.PerTarget);
            LogicActions.Register("applyStatus", ApplyStatus, ActionFlags.PerTarget);
            LogicActions.Register("removeStatus", (ctx, p) => Stats.Remove(ctx, p.Target, Required(p, "status"), (int)p.Number("stacks", 0)), ActionFlags.PerTarget);

            LogicWorld.Started += _ => { Stats.Reset(); StatusVisuals.Clear(); started.Clear(); };
            LogicWorld.Ending += _ => { Stats.Reset(); StatusVisuals.Clear(); started.Clear(); };
            LogicWorld.Polled += (w, t) => Stats.Poll(w, t);
            BodyEvents.UnitSpawned += (e, _) => StartingStatuses(e);
            BodyEvents.EntityBuilt += (e, _) => StartingStatuses(e);

            DevCommands.Register("logic-stats", "logic-stats <role>   an entity's stats, statuses and flags (roles as in `logic-l2 act`)", args =>
            {
                if (LogicWorld.Current == null) return "no logic session";
                var ctx = new LogicContext { World = LogicWorld.Current, Self = LogicWorld.Current.Level };
                var list = DevParams.Resolve(args.FirstOrDefault() ?? "king", ctx);
                if (list == null || list.Count == 0) return "no entity";
                return string.Join("\n", list.Take(20).Select(Stats.Describe));
            });
        }

        // ------------------------------------------------------------------ actions

        private static Hp HpOf(LogicEntity e) => e == null ? null : (e.Body as GameBody)?.Hp ?? e.Hp();

        private static string Required(ActionParams p, string name) =>
            p.Text(name) is { Length: > 0 } s ? s : throw new LogicError($"\"{name}\" is required");

        private static HashSet<string> Types(ActionParams p)
        {
            var raw = p.Raw("types");
            if (raw is JArray a) return new HashSet<string>(a.Where(t => t.Type == JTokenType.String).Select(t => (string)t), StringComparer.Ordinal);
            if (raw?.Type == JTokenType.String) return new HashSet<string>(StringComparer.Ordinal) { (string)raw };
            return null;
        }

        private static void DamageAction(LogicContext ctx, ActionParams p)
        {
            var victim = p.Target;
            double amount = p.Number("amount");
            var source = p.Has("source") ? p.Entity("source") : ctx.Self;
            var types = Types(p);
            bool splash = p.Bool("splash");
            if (p.Text("weapon") is { Length: > 0 } wname && HpOf(victim)?.TaggedObj is { } vt)
                amount *= WeaponFactor(wname, vt, splash);
            Deal(victim, amount, source, types, splash);
        }

        /// <summary>
        /// Damage <paramref name="victim"/> through the game's Hp.TakeDamage, so modifiers, flags, kill credit,
        /// coins and every event apply as for the game's own damage. For other L2 parts (projectile impacts, areas).
        /// Returns whether it killed.
        /// </summary>
        /// <summary>The game's "caused by player" (hit flash and effect on enemies): a player-side source hitting an enemy.</summary>
        private static bool ByPlayer(LogicEntity source, LogicEntity victim) =>
            source?.Team == "player" && source.Kind != "level" && victim?.Team == "enemy";

        internal static bool Deal(LogicEntity victim, double amount, LogicEntity source, HashSet<string> types, bool splash)
        {
            var hp = HpOf(victim);
            if (hp == null || amount <= 0 || hp.HpValue <= 0) return false;
            if (splash) (types ??= new HashSet<string>(StringComparer.Ordinal)).Add("splash");
            var from = source?.Go() != null ? source.Tagged() ?? source.Go().GetComponentInChildren<TaggedObject>() : null;
            scope.Add(new DamageInfo { Types = types, Splash = splash, Source = source });
            try { return hp.TakeDamage((float)amount, from, ByPlayer(source, victim), true); }
            finally { scope.RemoveAt(scope.Count - 1); }
        }

        private static readonly Dictionary<string, Weapon> weapons = new(StringComparer.Ordinal);

        /// <summary>`weapon`: the multipliers of a game weapon's damage modifiers that apply to the victim's tags.</summary>
        private static double WeaponFactor(string name, TaggedObject victim, bool splash)
        {
            if (name.StartsWith("vanilla:")) name = name.Substring(8);
            if (!weapons.TryGetValue(name, out var w) || w == null)
                weapons[name] = w = Resources.FindObjectsOfTypeAll<Weapon>().FirstOrDefault(x => x.name == name);
            if (w == null) throw new LogicError($"no game weapon \"{name}\"");
            double f = 1;
            foreach (var m in splash ? w.splashDamage : w.directDamage)
                if (m.requiredTags.All(victim.Tags.Contains)) f *= m.damageMultiplyer;
            return f;
        }

        private static void Kill(LogicEntity e, LogicEntity source)
        {
            var hp = HpOf(e);
            if (hp == null)
            {
                if (e?.Exists == true && e.Kind is not ("level" or "king" or "castle")) LogicServices.Factory?.Despawn(e, true, false);
                return;
            }
            if (hp.HpValue <= 0) return;
            bool inv = hp.invulnerable;
            hp.invulnerable = false;
            killing = true;
            var from = source?.Go() != null ? source.Tagged() : null;
            scope.Add(new DamageInfo { Source = source });
            try { hp.TakeDamage(hp.HpValue + hp.maxHp + 1f, from, ByPlayer(source, e), true); }
            finally { killing = false; scope.RemoveAt(scope.Count - 1); if (hp != null) hp.invulnerable = inv; }
        }

        private static void Revive(LogicContext ctx, ActionParams p)
        {
            var hp = HpOf(p.Target);
            if (hp == null || hp.HpValue > 0) return;
            hp.Revive(true, Mathf.Clamp01((float)p.Number("hpPct", 1)));
        }

        private static void SetHp(LogicEntity e, double value, LogicEntity source)
        {
            var hp = HpOf(e);
            if (hp == null || hp.HpValue <= 0) return;
            if (value <= 0) { Kill(e, source); return; }
            hp.SetHpTo(Mathf.Min((float)value, hp.maxHp));
        }

        private static (double? seconds, string until) For(ActionParams p)
        {
            var raw = p.Raw("for");
            if (raw == null || raw.Type == JTokenType.Null) return (null, null);
            if (raw.Type == JTokenType.String && (string)raw is "night" or "day") return (null, (string)raw);
            double s = p.Number("for");
            return (s > 0 ? s : null, null);
        }

        private static void SetStat(LogicContext ctx, ActionParams p)
        {
            string stat = Required(p, "stat");
            if (!StatMath.IsStat(stat)) throw new LogicError($"\"{stat}\" isn't a stat (stats: {string.Join(", ", StatMath.Names)})");
            double? set = p.Has("value") ? p.Number("value") : null, add = p.Has("add") ? p.Number("add") : null, mult = p.Has("mult") ? p.Number("mult") : null;
            if (set == null && add == null && mult == null) throw new LogicError("setStat needs value, add or mult");
            if (stat == "hp")
            {
                // Hit points aren't a modifier: change them now, without damaged/healed (like setHp).
                var hp = HpOf(p.Target);
                if (hp == null) return;
                SetHp(p.Target, ((set ?? hp.HpValue) + (add ?? 0)) * (mult ?? 1), ctx.Self);
                return;
            }
            if (double.IsNaN(Stats.Get(p.Target, stat))) return;   // it has no such stat (a tower's moveSpeed): nothing to change
            var (seconds, until) = For(p);
            var extra = new JObject();
            foreach (var k in new[] { "from", "types", "chance", "when" }) if (p.Raw(k) is { } v) extra[k] = v.DeepClone();
            Stats.Add(p.Target, new Builder.Logic.StatModifier
            {
                Stat = stat, Set = set, Add = add, Mult = mult, Key = p.Text("key"), Seconds = seconds ?? 0, Until = until,
                Source = ctx.Self, Extra = extra.Count > 0 ? extra : null,
            });
        }

        private static void ApplyStatus(LogicContext ctx, ActionParams p)
        {
            var (seconds, until) = For(p);
            var source = p.Has("source") ? p.Entity("source") : ctx.Self;
            Stats.Apply(ctx, p.Target, Required(p, "status"), seconds, until, Math.Max(1, (int)p.Number("stacks", 1)), source);
        }

        private static readonly HashSet<LogicEntity> started = [];

        /// <summary>A template's `statuses`: statuses it starts with.</summary>
        private static void StartingStatuses(LogicEntity e)
        {
            if (e?.Def?["statuses"] is not JArray list || list.Count == 0 || !started.Add(e)) return;
            foreach (var id in list.Where(t => t.Type == JTokenType.String).Select(t => (string)t))
            {
                try { Stats.Apply(null, e, id, null, null, 1, null); }
                catch (LogicError err) { Log.For<LogicStatsManager>().Warn($"{e.Id}: starting status: {err.Message}"); }
            }
        }

        // ------------------------------------------------------------------ damage types

        /// <summary>The damage types of the hit in progress: the action's, plus the game's derived ones.</summary>
        private static HashSet<string> HitTypes(TaggedObject attacker)
        {
            var info = CurrentDamage;
            var t = info?.Types != null ? new HashSet<string>(info.Types, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            if (info?.Splash == true) t.Add("splash");
            if (weaponDepth > 0 && weaponDepth <= weaponSplash.Length)
            {
                if (weaponSplash[weaponDepth - 1]) t.Add("splash");
                if (weaponMagic[weaponDepth - 1]) t.Add("magic");
            }
            if (attacker != null)
            {
                var tags = attacker.Tags;
                if (tags.Contains(TagManager.ETag.MeeleFighter)) t.Add("melee");
                if (tags.Contains(TagManager.ETag.RangedFighter)) t.Add("ranged");
                if (tags.Contains(TagManager.ETag.Tower)) t.Add("tower");
                if (tags.Contains(TagManager.ETag.Player)) t.Add("king");
            }
            return t;
        }

        private static readonly Dictionary<Weapon, bool> magicWeapons = [];

        private static bool IsMagic(Weapon w)
        {
            if (w == null) return false;
            if (!magicWeapons.TryGetValue(w, out bool m))
                magicWeapons[w] = m = w.projectileVisuals != null && w.projectileVisuals.GetComponentInChildren<TaggedObject>(true) is { } t && t.Tags.Contains(TagManager.ETag.MagicProjectile);
            return m;
        }

        // ------------------------------------------------------------------ patches

        /// <summary>
        /// damageTaken / damageMult / invulnerable, inside the game's own damage call. First, so L1's hp-before/after
        /// hooks see the final amount; a hit reduced to 0 skips the call (no `damaged`, and the game's "≤ 0 heals"
        /// path isn't taken).
        /// </summary>
        [HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
        private static class TakeDamagePatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Hp __instance, ref float _amount, TaggedObject _damageComingFrom, ref bool __result)
            {
                if (LogicWorld.Current == null || killing || rawHits > 0 || _amount <= 0 || !Stats.AnyDamageMods) return true;
                if (__instance.HpValue <= 0) return true;
                var vs = Stats.SheetOfGo(__instance.gameObject);
                var asheet = _damageComingFrom != null ? Stats.SheetOfGo(_damageComingFrom.gameObject) : null;
                if (vs == null && asheet == null) return true;
                if (vs != null && vs.MyFlags.Contains("invulnerable")) { __result = false; return false; }
                var victim = vs?.E ?? LogicGame.EntityOf(__instance.gameObject);
                var attacker = asheet?.E ?? (_damageComingFrom != null ? LogicGame.EntityOf(_damageComingFrom.gameObject) : CurrentDamage?.Source);
                double f = Stats.DamageFactor(vs, asheet, victim, attacker, () => HitTypes(_damageComingFrom));
                if (Math.Abs(f - 1) < 1e-9) return true;
                _amount = (float)(_amount * Math.Max(0, f));
                if (_amount <= 0) { __result = false; return false; }
                return true;
            }
        }

        /// <summary>Which hits are splash and which come from magic projectiles (derived damage types).</summary>
        [HarmonyPatch(typeof(Weapon), nameof(Weapon.DealDamage))]
        private static class WeaponScope
        {
            private static void Prefix(Weapon __instance, bool splashDamage)
            {
                if (LogicWorld.Current == null) return;
                if (weaponDepth < weaponSplash.Length) { weaponSplash[weaponDepth] = splashDamage; weaponMagic[weaponDepth] = IsMagic(__instance); }
                weaponDepth++;
            }

            private static void Finalizer()
            {
                if (weaponDepth > 0) weaponDepth--;
            }
        }

        /// <summary>stunned / disarmed: the game's auto attacks (every override that doesn't call the base) skip their update.</summary>
        [HarmonyPatch]
        private static class AttackGate
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(AutoAttack), nameof(AutoAttack.Update));
                yield return AccessTools.Method(typeof(AutoAttackTower), nameof(AutoAttackTower.Update));
                yield return AccessTools.Method(typeof(AutoAttackTwoTargets), nameof(AutoAttackTwoTargets.Update));
                yield return AccessTools.Method(typeof(AutoAttackLowestInRangeMulti), nameof(AutoAttackLowestInRangeMulti.Update));
                yield return AccessTools.Method(typeof(ManualAttack), nameof(ManualAttack.Update));
            }

            private static bool Prefix(Component __instance)
            {
                if (LogicWorld.Current == null || !Stats.AnyFlags) return true;
                var s = Stats.SheetOfGo(__instance.gameObject);
                return s == null || !(s.MyFlags.Contains("stunned") || s.MyFlags.Contains("disarmed"));
            }
        }

        /// <summary>untargetable: every TagManager finder goes through this one; flagged objects are dropped from it.</summary>
        [HarmonyPatch(typeof(TagManager), nameof(TagManager.FindAllTaggedObjectsWithTags))]
        private static class Untargetable
        {
            private static void Postfix(List<TaggedObject> _listToPolulate)
            {
                if (Stats.Untargetable.Count == 0 || LogicWorld.Current == null) return;
                _listToPolulate.RemoveAll(t => t != null && Stats.Untargetable.Contains(t));
            }
        }

        /// <summary>income / noIncome: a building's payout (the king's harvest and the day's coin total).</summary>
        private static Sheet SlotSheet(BuildSlot slot)
        {
            if (slot == null || LogicWorld.Current == null) return null;
            foreach (var t in slot.GetComponentsInChildren<TaggedObject>(true))
                if (Stats.SheetOfGo(t.gameObject) is { } s) return s;
            return null;
        }

        private static int Income(Sheet s, int raw)
        {
            double v = Stats.Get(s.E, "income");
            return double.IsNaN(v) ? raw : (int)v;
        }

        [HarmonyPatch(typeof(BuildingInteractor), nameof(BuildingInteractor.GoldIncome), MethodType.Getter)]
        private static class IncomeShown
        {
            private static void Postfix(BuildingInteractor __instance, ref int __result)
            {
                if (LogicWorld.Current == null || SlotSheet(__instance.targetBuilding) is not { } s) return;
                __result = Income(s, __result);
            }
        }

        [HarmonyPatch(typeof(BuildingInteractor), nameof(BuildingInteractor.Harvest))]
        private static class IncomeHarvest
        {
            private static void Prefix(BuildingInteractor __instance, out int? __state)
            {
                __state = null;
                if (LogicWorld.Current == null || SlotSheet(__instance.targetBuilding) is not { } s) return;
                var slot = __instance.targetBuilding;
                __state = slot.GoldIncome;
                slot.GoldIncome = Income(s, slot.GoldIncome);
            }

            private static void Finalizer(BuildingInteractor __instance, int? __state)
            {
                if (__state is int raw && __instance.targetBuilding != null) __instance.targetBuilding.GoldIncome = raw;
            }
        }

        /// <summary>`for: "night"` ends at dawn (before the day's payout), `for: "day"` at dusk.</summary>
        [HarmonyPatch(typeof(DayNightCycle), "DawnCallBeforeSunrise")]
        private static class Dawn
        {
            private static void Prefix() { if (LogicWorld.Current != null) Stats.EndPhase("night"); }
        }

        [HarmonyPatch(typeof(DayNightCycle), "DuskCall")]
        private static class Dusk
        {
            private static void Prefix() { if (LogicWorld.Current != null) Stats.EndPhase("day"); }
        }
    }
}
