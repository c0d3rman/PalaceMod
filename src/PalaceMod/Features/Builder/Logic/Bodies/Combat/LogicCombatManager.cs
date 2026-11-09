using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;

/*
    Combat data for the behaviour engine (docs/builder-engine.md, "Weapons", "Damage types", `bump`): `.weapon` and
    `setWeapon` (Bodies/Combat/Weapons.cs), the damage types of the game's burning ground (a fire arrow's or hot oil's
    burn ticks carry "fire" and the weapon that left it), and `bump` (Bodies/Combat/Bumps.cs). Infrastructure: no
    settings; every patch early-outs outside a logic session.
*/
namespace PalaceMod
{
    public class LogicCombatManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LogicServices.WeaponOf = LogicWeapons.WeaponOf;
            LogicActions.Register("setWeapon", SetWeapon, ActionFlags.PerTarget);
            LogicWorld.Started += _ => { LogicWeapons.Reset(); Bumps.Reset(); };
            LogicWorld.Ending += _ => { LogicWeapons.Reset(); Bumps.Reset(); };
            LogicWorld.Polled += LogicWeapons.Poll;

            DevCommands.Register("logic-weapons", "logic-weapons [name]   every game weapon: id, damage types, damage modifiers, what it leaves on impact, catalog users",
                args => LogicWeapons.Dump(args.FirstOrDefault()));
        }

        public override void Update()
        {
            if (LogicWorld.Current != null) Bumps.Update();
        }

        // ------------------------------------------------------------------ setWeapon

        private static void SetWeapon(LogicContext ctx, ActionParams p)
        {
            var e = p.Target;
            if (e == null) throw new LogicError("setWeapon: no target");
            if (!p.Has("weapon")) throw new LogicError("setWeapon: `weapon` is missing");
            // a text param: literal ("vanilla:...", "none", "base") unless it starts with "=" (an expression: an entity or an id)
            var raw0 = p.Raw("weapon");
            var v = raw0?.Type == JTokenType.String && !((string)raw0).StartsWith("=") ? Value.Text(((string)raw0).Trim()) : p.Get("weapon");
            LogicWeapons.Spec spec;
            if (v.Kind == ValueKind.Entity || v.Kind == ValueKind.Set)
            {
                var src = Values.ToEntity(v) ?? throw new LogicError("setWeapon: no entity to copy");
                spec = LogicWeapons.From(src);
            }
            else
            {
                string id = v.Kind == ValueKind.Text ? v.Str : v.IsNone ? null : Values.Format(v);
                if (string.IsNullOrEmpty(id)) throw new LogicError("setWeapon: `weapon` is empty (no weapon to copy?)");
                if (id == "base") spec = new LogicWeapons.Spec { Base = true };
                else spec = new LogicWeapons.Spec { Weapon = LogicWeapons.Find(id) ?? throw new LogicError($"setWeapon: no game weapon \"{id}\" (`logic-weapons` lists them)") };
            }
            if (p.Has("range")) spec.Range = (float)p.Number("range");
            double? seconds = null;
            string until = null;
            var raw = p.Raw("for");
            if (raw != null && raw.Type != JTokenType.Null)
            {
                if (raw.Type == JTokenType.String && (string)raw is "night" or "day") until = (string)raw;
                else if (p.Number("for") is double s && s > 0) seconds = s;
            }
            LogicWeapons.Set(ctx.World, e, spec, seconds, until);
        }

        // ------------------------------------------------------------------ patches

        /// <summary>The empty weapon "none" does nothing (the attack's swing and sound still play).</summary>
        [HarmonyPatch(typeof(Weapon), nameof(Weapon.Attack))]
        private static class UnarmedAttack
        {
            private static bool Prefix(Weapon __instance) => !ReferenceEquals(__instance, LogicWeapons.UnarmedOrNull);
        }

        [HarmonyPatch(typeof(Weapon), nameof(Weapon.DealDamage))]
        private static class UnarmedDamage
        {
            private static bool Prefix(Weapon __instance) => !ReferenceEquals(__instance, LogicWeapons.UnarmedOrNull);
        }

        /// <summary>What a projectile leaves on impact (a fire arrow's burning ground) remembers the projectile's weapon
        /// and shooter: each impact that spawns something is noted (weapon, shooter, where, which frame), and a burner's
        /// first tick (its Start, a frame later) claims the nearest note whose weapon spawns an object of its name.</summary>
        private sealed class Burner { public Weapon Weapon; public TaggedObject Shooter; }
        private sealed class Impact { public Weapon Weapon; public TaggedObject Shooter; public Vector3 Pos; public int Frame; }
        private static readonly List<Impact> impacts = [];
        private static readonly ConditionalWeakTable<Component, Burner> burners = new();
        private static readonly AccessTools.FieldRef<AimbotProjectile, TaggedObject> firedBy = AccessTools.FieldRefAccess<AimbotProjectile, TaggedObject>("firedBy");

        [HarmonyPatch(typeof(AimbotProjectile), "SpawnWeaponObjectOnHit")]
        private static class SpawnOnHit
        {
            private static void Prefix(AimbotProjectile __instance, Transform t)
            {
                var w = __instance.Weapon;
                if (LogicWorld.Current == null || w == null || (w.spawnOnGroundWhenTargetingGround == null && w.spawnInAirWhenTargetingAir == null)) return;
                impacts.RemoveAll(i => Time.frameCount - i.Frame > 5);
                impacts.Add(new Impact { Weapon = w, Shooter = firedBy(__instance), Pos = t != null ? t.position : __instance.transform.position, Frame = Time.frameCount });
            }
        }

        private static Burner BurnerOf(Component c)
        {
            if (burners.TryGetValue(c, out var b)) return b;
            string name = c.gameObject.name.Replace("(Clone)", "").Trim();
            Impact best = null;
            float bestD = 9f;   // within 3 m
            foreach (var i in impacts)
            {
                if (Time.frameCount - i.Frame > 5) continue;
                var g = i.Weapon.spawnOnGroundWhenTargetingGround;
                var a = i.Weapon.spawnInAirWhenTargetingAir;
                if (!(g != null && g.name == name) && !(a != null && a.name == name)) continue;
                var d = c.transform.position - i.Pos;
                d.y = 0;
                if (d.sqrMagnitude < bestD) { bestD = d.sqrMagnitude; best = i; }
            }
            if (best != null) impacts.Remove(best);
            b = new Burner { Weapon = best?.Weapon, Shooter = best?.Shooter };
            burners.Add(c, b);
            return b;
        }

        /// <summary>A burn tick's hits are "fire" (and the weapon that left the fire), credited to its shooter.</summary>
        private static void PushBurn(Component burner, Weapon weapon, LogicEntity source)
        {
            var types = new HashSet<string>(StringComparer.Ordinal) { "fire" };
            if (weapon != null) LogicWeapons.AddTypes(weapon, types);
            LogicStatsManager.PushDamage(new DamageInfo { Types = types, Source = source, Weapon = LogicWeapons.IdOf(weapon) });
        }

        [HarmonyPatch(typeof(FireArcherBurn), "DamageTick")]
        private static class FireBurnTick
        {
            private static void Prefix(FireArcherBurn __instance, out bool __state)
            {
                __state = LogicWorld.Current != null;
                if (!__state) return;
                var b = BurnerOf(__instance);
                var shooter = b?.Shooter != null ? LogicGame.EntityOf(b.Shooter) : null;
                PushBurn(__instance, b?.Weapon, shooter);
            }

            private static void Finalizer(bool __state) { if (__state) LogicStatsManager.PopDamage(); }
        }

        [HarmonyPatch(typeof(HotOilTower), "DamageTick")]
        private static class HotOilTick
        {
            private static void Prefix(HotOilTower __instance, out bool __state)
            {
                __state = LogicWorld.Current != null;
                if (!__state) return;
                var tower = __instance.GetComponentInParent<TaggedObject>();
                PushBurn(__instance, null, tower != null ? LogicGame.EntityOf(tower) : null);
            }

            private static void Finalizer(bool __state) { if (__state) LogicStatsManager.PopDamage(); }
        }

        /// <summary>Knockbacks, watched for `bump`.</summary>
        [HarmonyPatch(typeof(KnockbackBehavior), nameof(KnockbackBehavior.ApplyKnockback))]
        private static class Knockback
        {
            private static void Prefix(KnockbackBehavior __instance, out (bool on, Vector3 pos) __state) =>
                __state = (LogicWorld.Current != null && !Bumps.Busy(__instance), __instance.transform.position);

            private static void Postfix(KnockbackBehavior __instance, (bool on, Vector3 pos) __state, Vector3 direction, float sqrDistance, float maxRange, float maxForce, AnimationCurve forceFromSource)
            {
                if (!__state.on) return;
                try { Bumps.Started(__instance, __state.pos, direction, sqrDistance, maxRange, maxForce, forceFromSource); }
                catch (Exception ex) { Log.For<LogicCombatManager>().Error("knockback watch", ex); }
            }
        }
    }
}
