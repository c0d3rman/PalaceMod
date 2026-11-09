using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic.Bodies.Steering;
using UnityEngine;

/*
    Weapons as data for the behaviour engine (docs/builder-engine.md, "Weapons"): `.weapon`, the `setWeapon` action
    and the weapon-derived damage types on damage events.

    A weapon is the game's Weapon asset, named by its id "vanilla:<asset name>" (Builder units' per-design clones keep
    the name). Who holds one: a unit's AutoAttacks (a tower's sit on its slot), a Builder `attacks` entry with a
    `weapon`, and the king's auto attack (the passive half of his equipped weapon; his active ability is left alone).

    setWeapon swaps the Weapon on every holder and moves the attack range (and a unit's keep distance) with it, so a
    melee unit given a bow shoots. Ranges are written as base values and the stat modifiers re-applied on top
    (StatsService.BaseChanged). "none" is an empty weapon whose attacks do nothing (Weapon.Attack skips it: the swing
    still plays). The first swap remembers the entity's own weapon ("base" restores it); `for` restores what it had
    before the first timed swap still running.

    Damage types a weapon gives its hits: its id, and "fire" when what it leaves on impact burns (FireArcherBurn,
    HotOilTower: the fire archers', the Fire Spire's) or its damage modifiers single out FireAndExplosionResistant
    targets (the game's own word for fire and explosion damage).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class LogicWeapons
    {
        public const string Prefix = "vanilla:";

        private static Weapon unarmed;

        /// <summary>The empty weapon "none": attacks with it do nothing.</summary>
        internal static Weapon Unarmed
        {
            get
            {
                if (unarmed == null)
                {
                    unarmed = ScriptableObject.CreateInstance<Weapon>();
                    unarmed.name = "none";
                    unarmed.hideFlags = HideFlags.HideAndDontSave;
                    unarmed.directDamage = [];
                    unarmed.splashDamage = [];
                    unarmed.slowsFastEnemiesFor = -1;
                }
                return unarmed;
            }
        }

        /// <summary>The empty weapon if it was ever made (for patches: never creates it).</summary>
        internal static Weapon UnarmedOrNull => unarmed;

        public static string IdOf(Weapon w) => w == null ? null : w == unarmed ? "none" : Prefix + w.name;

        // ------------------------------------------------------------------ damage types

        private static readonly Dictionary<Weapon, string[]> typeCache = [];

        /// <summary>The weapon's own damage types (its id, fire...) into <paramref name="into"/>.</summary>
        public static void AddTypes(Weapon w, HashSet<string> into)
        {
            if (w == null || w == unarmed) return;
            foreach (var t in TypesOf(w)) into.Add(t);
        }

        public static string[] TypesOf(Weapon w)
        {
            if (typeCache.TryGetValue(w, out var t)) return t;
            var list = new List<string> { IdOf(w) };
            if (Burns(w.spawnOnGroundWhenTargetingGround) || Burns(w.spawnInAirWhenTargetingAir) || FireMods(w)) list.Add("fire");
            return typeCache[w] = list.ToArray();
        }

        private static bool Burns(GameObject go) =>
            go != null && (go.GetComponentInChildren<FireArcherBurn>(true) != null || go.GetComponentInChildren<HotOilTower>(true) != null);

        private static bool FireMods(Weapon w)
        {
            bool Has(List<DamageModifyer> mods) => mods != null && mods.Any(m => m?.requiredTags != null && m.requiredTags.Contains(TagManager.ETag.FireAndExplosionResistant));
            return Has(w.directDamage) || Has(w.splashDamage);
        }

        // ------------------------------------------------------------------ lookup

        private static readonly Dictionary<string, Weapon> byName = new(StringComparer.Ordinal);

        /// <summary>A weapon by id ("vanilla:&lt;name&gt;" or the bare name): the catalog's, else any loaded Weapon asset.</summary>
        public static Weapon Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id == "none") return Unarmed;
            string name = id.StartsWith(Prefix) ? id.Substring(Prefix.Length) : id;
            if (byName.TryGetValue(name, out var w) && w != null) return w;
            w = BuilderServices.Catalog?.FindWeapon(name)?.Asset;
            if (w == null) w = Resources.FindObjectsOfTypeAll<Weapon>().FirstOrDefault(x => x.name == name && !Made(x));
            if (w != null) byName[name] = w;
            return w;
        }

        /// <summary>The empty weapon: never found by name. (A weapon on the other side's wielder hurts that wielder's
        /// foes: LogicGameRulesManager's WielderSide/WielderRows.)</summary>
        private static bool Made(Weapon w) => w != null && w == unarmed;

        // ------------------------------------------------------------------ holders

        /// <summary>One thing that attacks with a Weapon.</summary>
        private sealed class Holder
        {
            public AutoAttack Auto;
            public ManualAttack Manual;
            public BuilderAttack Builder;

            public Component C => Auto != null ? Auto : Manual != null ? Manual : Builder;

            public Weapon Weapon
            {
                get => Auto != null ? Auto.weapon : Manual != null ? Manual.weapon : Builder != null ? Builder.Weapon : null;
                set { if (Auto != null) Auto.weapon = value; else if (Manual != null) Manual.weapon = value; else if (Builder != null) Builder.Weapon = value; }
            }

            public List<TargetPriority> Priorities => Auto != null ? Auto.targetPriorities : Manual != null ? Manual.targetPriorities : null;
        }

        private static List<Holder> Holders(GameObject go)
        {
            var list = new List<Holder>();
            if (go == null) return list;
            var king = go.GetComponentInChildren<PlayerInteraction>(true) ?? go.GetComponentInParent<PlayerInteraction>();
            if (king != null)
            {
                if (king.AutoAttack != null) list.Add(new Holder { Manual = king.AutoAttack });
                return list;
            }
            var parts = StatWriter.Of(go);
            if (parts != null) foreach (var a in parts.Attacks) if (a != null) list.Add(new Holder { Auto = a });
            foreach (var b in go.GetComponents<BuilderAttack>()) if (b.Weapon != null) list.Add(new Holder { Builder = b });
            return list;
        }

        /// <summary>The weapon an entity attacks with now (its first holder's), or null.</summary>
        public static Weapon Current(LogicEntity e)
        {
            var go = e?.Go();
            if (go == null) return null;
            foreach (var h in Holders(go)) if (h.Weapon != null) return h.Weapon;
            return null;
        }

        /// <summary>`.weapon`.</summary>
        public static string WeaponOf(LogicEntity e) => IdOf(Current(e));

        private static float RangeOf(Holder h)
        {
            if (h.Builder != null) return h.Builder.Range;
            float r = 0;
            foreach (var tp in h.Priorities ?? []) if (tp != null && tp.range > r) r = tp.range;
            return r;
        }

        private static float? KeepOf(GameObject go)
        {
            if (go == null) return null;
            if (go.GetComponent<PathfindMovementEnemy>() is { } en) return en.keepDistanceOf;
            if (go.GetComponent<PathfindMovementPlayerunit>() is { } pu) return pu.keepDistanceOf;
            return null;
        }

        // ------------------------------------------------------------------ state (for base and `for`)

        /// <summary>What an entity's holders carry: weapon, base ranges, keep distance.</summary>
        private sealed class State
        {
            public readonly List<(Holder h, Weapon w, float[] ranges, float builderRange, float breakDistance)> Items = [];
            public float? Keep;
        }

        private sealed class Record
        {
            public LogicEntity E;
            public State Base;
            /// <summary>The state before the first timed swap still running, and when (or at which phase end) it returns.</summary>
            public State Before;
            public double Until = double.PositiveInfinity;
            public string UntilPhase;
            public bool SeenOtherPhase;
        }

        private static readonly Dictionary<LogicEntity, Record> records = [];

        public static void Reset() => records.Clear();

        private static State Capture(GameObject go, List<Holder> holders)
        {
            var s = new State();
            foreach (var h in holders)
            {
                var pr = h.Priorities;
                var ranges = pr == null ? [] : pr.Select(tp => tp == null ? 0f : StatWriter.BaseValue(tp, "range", tp.range)).ToArray();
                float bd = h.Auto is AutoAttackWithWarning w ? w.breakTargetingDistance : 0;
                s.Items.Add((h, h.Weapon, ranges, h.Builder != null ? h.Builder.Range : 0, bd));
            }
            if (go.GetComponent<PathfindMovementEnemy>() is { } en) s.Keep = StatWriter.BaseValue(en, "keepDistanceOf", en.keepDistanceOf);
            else if (go.GetComponent<PathfindMovementPlayerunit>() is { } pu) s.Keep = StatWriter.BaseValue(pu, "keepDistanceOf", pu.keepDistanceOf);
            return s;
        }

        private static void Restore(LogicEntity e, State s)
        {
            var go = e.Go();
            if (go == null) return;
            foreach (var (h, w, ranges, br, bd) in s.Items)
            {
                if (h.C == null) continue;
                h.Weapon = w;
                var pr = h.Priorities;
                if (pr != null) for (int i = 0; i < pr.Count && i < ranges.Length; i++) if (pr[i] != null) pr[i].range = ranges[i];
                if (h.Builder != null) h.Builder.Range = br;
                if (h.Auto is AutoAttackWithWarning aw && bd > 0) aw.breakTargetingDistance = bd;
            }
            WriteKeep(go, s.Keep);
            Rebase(e);
        }

        private static void WriteKeep(GameObject go, float? keep)
        {
            if (keep is not float k) return;
            if (go.GetComponent<PathfindMovementEnemy>() is { } en) en.keepDistanceOf = k;
            else if (go.GetComponent<PathfindMovementPlayerunit>() is { } pu) pu.keepDistanceOf = k;
        }

        /// <summary>The live fields are the new base: re-apply attackRange / keepDistance modifiers on top.</summary>
        private static void Rebase(LogicEntity e)
        {
            StatsService.Instance.BaseChanged(e, "attackRange");
            StatsService.Instance.BaseChanged(e, "keepDistance");
        }

        // ------------------------------------------------------------------ setWeapon

        /// <summary>What to give: a weapon, and the range and keep distance that go with it (null = derive).</summary>
        public sealed class Spec
        {
            public Weapon Weapon;
            public bool Base;
            public float? Range, Keep;
        }

        /// <summary>A copy of another entity's weapon, range and keep distance.</summary>
        public static Spec From(LogicEntity source)
        {
            var go = source?.Go() ?? throw new LogicError("setWeapon: the entity to copy is gone");
            var holders = Holders(go);
            var h = holders.FirstOrDefault(x => x.Weapon != null) ?? throw new LogicError($"setWeapon: {source.Id} has no weapon to copy");
            return new Spec { Weapon = h.Weapon, Range = RangeOf(h), Keep = KeepOf(go) };
        }

        /// <summary>The usual range and keep distance of a weapon: a catalog unit's that uses it, else a live holder's.</summary>
        private static (float? range, float? keep) Usual(Weapon w)
        {
            if (w == null || w == unarmed) return (null, null);
            var cat = BuilderServices.Catalog;
            var u = cat?.Units.FirstOrDefault(x => x.Weapon == w.name && x.AttackRange > 0);
            if (u != null) return (u.AttackRange, u.KeepDistance > 0 ? u.KeepDistance : null);
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AutoAttack>())
                if (a.weapon != null && a.weapon.name == w.name)
                    return (RangeOf(new Holder { Auto = a }), null);
            foreach (var m in UnityEngine.Object.FindObjectsOfType<ManualAttack>())
                if (m.weapon != null && m.weapon.name == w.name)
                    return (RangeOf(new Holder { Manual = m }), null);
            return (null, null);
        }

        public static void Set(LogicWorld world, LogicEntity e, Spec spec, double? seconds, string untilPhase)
        {
            var go = e?.Go();
            if (go == null || !e.Exists) return;
            var holders = Holders(go);
            if (holders.Count == 0) return;   // nothing that attacks with a weapon: skipped, like every verb on members it can't act on
            if (!records.TryGetValue(e, out var rec)) records[e] = rec = new Record { E = e, Base = Capture(go, holders) };

            if (spec.Base)
            {
                Restore(e, rec.Base);
                records.Remove(e);
                return;
            }

            bool timed = seconds != null || untilPhase != null;
            if (timed)
            {
                rec.Before ??= Capture(go, holders);
                rec.Until = seconds is double s ? world.Time + s : double.PositiveInfinity;
                rec.UntilPhase = untilPhase;
                rec.SeenOtherPhase = false;
            }
            else { rec.Before = null; rec.Until = double.PositiveInfinity; rec.UntilPhase = null; }

            var w = spec.Weapon;
            float? range = spec.Range, keep = spec.Keep;
            if (range == null || keep == null)
            {
                var (ur, uk) = Usual(w);
                range ??= ur;
                keep ??= uk;
            }
            foreach (var h in holders)
            {
                h.Weapon = w;
                if (range is not float r || r <= 0) continue;
                if (h.Builder != null) { h.Builder.Range = r; continue; }
                // Written as base values (the old base ranges scaled to the new one); Rebase re-applies modifiers.
                var pr = h.Priorities;
                if (pr == null) continue;
                float old = 0;
                foreach (var tp in pr) if (tp != null) old = Mathf.Max(old, StatWriter.BaseValue(tp, "range", tp.range));
                foreach (var tp in pr) if (tp != null) tp.range = old > 0 ? StatWriter.BaseValue(tp, "range", tp.range) * r / old : r;
                if (h.Auto is AutoAttackWithWarning aw && old > 0) aw.breakTargetingDistance *= r / old;
            }
            // A unit's keep distance follows: the copied unit's, else the weapon's usual unit's, else 60 % of the range.
            if (KeepOf(go) != null && range is float rr && rr > 0) WriteKeep(go, Mathf.Min(keep ?? rr * 0.6f, rr * 0.9f));
            Rebase(e);
        }

        /// <summary>`for` expiry (Polled, 5 Hz).</summary>
        public static void Poll(LogicWorld w, double now)
        {
            if (records.Count == 0) return;
            bool day = DayNightCycle.Instance == null || DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day;
            List<LogicEntity> drop = null;
            foreach (var rec in records.Values)
            {
                if (!rec.E.Exists) { (drop ??= []).Add(rec.E); continue; }
                if (rec.Before == null) continue;
                bool due = now >= rec.Until;
                if (rec.UntilPhase != null)
                {
                    // "night": until the next dawn (by day, the one after the coming night); "day": until the next dusk.
                    bool inPhase = rec.UntilPhase == "night" ? !day : day;
                    if (inPhase) rec.SeenOtherPhase = true;
                    else if (rec.SeenOtherPhase) due = true;
                }
                if (!due) continue;
                Restore(rec.E, rec.Before);
                rec.Before = null;
                rec.Until = double.PositiveInfinity;
                rec.UntilPhase = null;
            }
            if (drop != null) foreach (var e in drop) records.Remove(e);
        }

        // ------------------------------------------------------------------ savestates ("weapons" part)

        /*
            Every entity setWeapon touched: the weapon (by id), base ranges and keep distance each of its holders has now,
            plus its own ("base") and pre-timed-swap states and the timer, by entity id. Restore runs after the core
            rebuilt the entities and their stats, and writes them back holder by holder (the holders' order is the
            components' order, which a rebuild keeps).
        */

        private static JObject StateJson(State s)
        {
            var items = new JArray();
            foreach (var (_, w, ranges, br, bd) in s.Items)
                items.Add(new JObject { ["w"] = IdOf(w), ["ranges"] = new JArray(ranges.Select(r => (object)r)), ["br"] = br, ["bd"] = bd });
            var o = new JObject { ["items"] = items };
            if (s.Keep is float k) o["keep"] = k;
            return o;
        }

        private static State StateFrom(JObject o, List<Holder> holders)
        {
            if (o == null) return null;
            var s = new State { Keep = (float?)o["keep"] };
            var items = o["items"] as JArray ?? [];
            for (int i = 0; i < items.Count && i < holders.Count; i++)
            {
                var it = (JObject)items[i];
                var w = (string)it["w"] is { } id ? Find(id) : null;
                var ranges = (it["ranges"] as JArray ?? []).Select(x => (float)x).ToArray();
                s.Items.Add((holders[i], w, ranges, (float?)it["br"] ?? 0, (float?)it["bd"] ?? 0));
            }
            return s;
        }

        public static JToken CaptureSave(LogicWorld world)
        {
            if (records.Count == 0) return null;
            var all = new JObject();
            foreach (var rec in records.Values)
            {
                var go = rec.E.Go();
                if (go == null || rec.E.Destroyed || !rec.E.Exists) continue;
                var o = new JObject { ["now"] = StateJson(Capture(go, Holders(go))), ["base"] = StateJson(rec.Base) };
                if (rec.Before != null)
                {
                    o["before"] = StateJson(rec.Before);
                    if (!double.IsPositiveInfinity(rec.Until)) o["left"] = rec.Until - world.Time;
                    if (rec.UntilPhase != null) { o["phase"] = rec.UntilPhase; o["seen"] = rec.SeenOtherPhase; }
                }
                all[rec.E.Id] = o;
            }
            return all;
        }

        public static void RestoreSave(LogicWorld world, JToken state)
        {
            records.Clear();
            if (state is not JObject all) return;
            Dictionary<string, TaggedObject> scene = null;
            foreach (var kv in all)
            {
                var e = world.Find(kv.Key);
                // A game object (the king, a tower) is wrapped lazily: find it again by its scene path.
                if (e == null && kv.Key.StartsWith(Prefix))
                {
                    scene ??= SaveStates.FindByPath<TaggedObject>();
                    if (scene.TryGetValue(kv.Key.Substring(Prefix.Length), out var t) && t != null) e = LogicGame.EntityOf(t);
                }
                var go = e?.Go();
                if (go == null || kv.Value is not JObject o) continue;
                var holders = Holders(go);
                if (holders.Count == 0) continue;
                var rec = new Record { E = e, Base = StateFrom(o["base"] as JObject, holders) ?? Capture(go, holders) };
                if (o["before"] is JObject b)
                {
                    rec.Before = StateFrom(b, holders);
                    rec.Until = o["left"] is JValue left && left.Type != JTokenType.Null ? world.Time + (double)left : double.PositiveInfinity;
                    rec.UntilPhase = (string)o["phase"];
                    rec.SeenOtherPhase = (bool?)o["seen"] ?? false;
                }
                records[e] = rec;
                if (StateFrom(o["now"] as JObject, holders) is { } now) Restore(e, now);
            }
        }

        // ------------------------------------------------------------------ dev

        public static string Dump(string filter)
        {
            var sb = new System.Text.StringBuilder();
            var users = new Dictionary<string, List<string>>();
            foreach (var u in BuilderServices.Catalog?.Units ?? [])
                if (u.Weapon != null) { if (!users.TryGetValue(u.Weapon, out var l)) users[u.Weapon] = l = []; l.Add($"{u.Id}({u.AttackRange:0.#}m)"); }
            string Mods(List<DamageModifyer> m) => m == null ? "-" : string.Join(" ", m.Select(d => $"[{string.Join("+", d.requiredTags)}]+{d.damageAdded}x{d.damageMultiplyer}"));
            string Comps(GameObject go) => go == null ? "-" : go.name + "{" + string.Join(",", go.GetComponentsInChildren<MonoBehaviour>(true).Select(c => c.GetType().Name).Distinct()) + "}";
            foreach (var w in Resources.FindObjectsOfTypeAll<Weapon>().Where(x => !Made(x)).GroupBy(x => x.name).Select(g => g.First()).OrderBy(x => x.name))
            {
                if (!string.IsNullOrEmpty(filter) && w.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.Append($"{IdOf(w)} types=[{string.Join(",", TypesOf(w))}] player={w.isPlayerWeapon} proj={(w.projectileSpeed > 0 && w.projectileVisuals != null ? w.projectileSpeed.ToString("0.#") : "-")} ");
                sb.Append($"direct={Mods(w.directDamage)} splash={Mods(w.splashDamage)} ground={Comps(w.spawnOnGroundWhenTargetingGround)} air={Comps(w.spawnInAirWhenTargetingAir)} ");
                sb.Append($"effects={string.Join(",", Traverse(w))} users={(users.TryGetValue(w.name, out var us) ? string.Join(",", us) : "-")}\n");
            }
            foreach (var h in Resources.FindObjectsOfTypeAll<HotOilTower>().Take(3))
                sb.Append($"HotOilTower {h.name}: {Mods(h.damageModifyers)}\n");
            foreach (var f in Resources.FindObjectsOfTypeAll<FireArcherBurn>().Take(3))
                sb.Append($"FireArcherBurn {f.name}: {Mods(f.damageModifyers)}\n");
            return sb.ToString();
        }

        private static IEnumerable<string> Traverse(Weapon w) =>
            (HarmonyLib.Traverse.Create(w).Field("additionalWeaponEffects").GetValue<List<AdditionalWeaponEffectScript>>() ?? []).Where(x => x != null).Select(x => x.GetType().Name);
    }
}
