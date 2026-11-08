using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

/*
    The Enemy Studio's numbers, without a fight: a unit's effective stats (its overrides over the base unit's), the
    damage one hit does to a target with given tags (the game's DamageModifyer.CalculateDamageOnTarget: for each
    modifier whose required tags the target all has, damage = (damage + add) * mult, then the attacker's multiplier),
    damage per second, and how long and how many hits something takes to kill. Plain C#, unit-tested
    (tests/PalaceMod.Tests/EnemyStudioTests.cs).
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    /// <summary>A unit as it will spawn (before the night's difficulty and elite status).</summary>
    internal sealed class EffectiveUnit
    {
        public string Name;
        public float MaxHp, MoveSpeed, AttackCooldown, AttackRange, DamageMult = 1f;
        public List<DamageModDef> Damage = [], Splash = [];
        public HashSet<string> Tags = new(StringComparer.Ordinal);
        public bool Flying, Large;
        /// <summary>Simultaneous targets per attack (towers fire several projectiles).</summary>
        public int Shots = 1;
    }

    /// <summary>Something that takes hits, by its tags.</summary>
    internal sealed class TargetProfile
    {
        public string Name;
        public HashSet<string> Tags;
        public float Hp;

        public TargetProfile(string name, float hp, params string[] tags)
        {
            Name = name;
            Hp = hp;
            Tags = new HashSet<string>(tags, StringComparer.Ordinal);
        }
    }

    internal static class EnemyMath
    {
        /// <summary>The game's damage formula for one hit.</summary>
        public static float Damage(IEnumerable<DamageModDef> mods, ICollection<string> targetTags, float multiplier = 1f)
        {
            float d = 0f;
            foreach (var m in mods ?? [])
            {
                if (m == null) continue;
                if ((m.Tags ?? []).Any(t => !targetTags.Contains(t))) continue;
                d += m.Add;
                d *= m.Mult;
            }
            return d * multiplier;
        }

        /// <summary>Damage per second of an attacker on a target (direct damage only).</summary>
        public static float PerSecond(EffectiveUnit attacker, ICollection<string> targetTags)
        {
            if (attacker == null || attacker.AttackCooldown <= 0f) return 0f;
            return Damage(attacker.Damage, targetTags, attacker.DamageMult) / attacker.AttackCooldown;
        }

        /// <summary>Hits and seconds to bring hp to 0 at perHit every cooldown s (the first hit lands at once). Null when
        /// it never dies (no damage).</summary>
        public static (int hits, float seconds)? TimeToKill(float hp, float perHit, float cooldown)
        {
            if (perHit <= 0f || hp <= 0f) return null;
            int hits = (int)Math.Ceiling(hp / perHit - 1e-4);
            if (hits < 1) hits = 1;
            return (hits, (hits - 1) * Math.Max(cooldown, 0f));
        }

        /// <summary>The unit's overrides over its base unit and base weapon (either may be null).</summary>
        public static EffectiveUnit Effective(UnitDef def, CatalogUnit baseUnit, CatalogWeapon baseWeapon)
        {
            var e = new EffectiveUnit
            {
                Name = def?.Name ?? baseUnit?.DisplayName ?? def?.Id,
                MaxHp = def?.Stats?.MaxHp ?? baseUnit?.MaxHp ?? 0f,
                MoveSpeed = def?.Stats?.MoveSpeed ?? baseUnit?.MoveSpeed ?? 0f,
                AttackCooldown = def?.Stats?.AttackCooldown ?? baseUnit?.AttackCooldown ?? 0f,
                AttackRange = def?.Stats?.AttackRange ?? baseUnit?.AttackRange ?? 0f,
                DamageMult = def?.Weapon?.DamageMult ?? 1f,
                Damage = Copy(def?.Weapon?.Damage ?? baseWeapon?.Damage),
                Splash = Copy(def?.Weapon?.Splash ?? baseWeapon?.Splash),
                Flying = baseUnit?.Flying ?? false,
                Large = baseUnit?.Large ?? false,
            };
            foreach (var t in baseUnit?.Tags ?? []) e.Tags.Add(t);
            foreach (var t in def?.Tags?.Remove ?? []) e.Tags.Remove(t);
            foreach (var t in def?.Tags?.Add ?? []) e.Tags.Add(t);
            return e;
        }

        private static List<DamageModDef> Copy(List<DamageModDef> mods) =>
            mods?.Where(m => m != null).Select(m => new DamageModDef { Tags = [.. m.Tags ?? []], Add = m.Add, Mult = m.Mult }).ToList() ?? [];

        /// <summary>Targets the readout measures the enemy's damage against. Tags as the game gives them (Thronefall
        /// buildings carry PlayerOwned + Building + their kind).</summary>
        public static readonly TargetProfile[] Targets =
        [
            new("House", 0f, "PlayerOwned", "Building", "House", "InfrastructureEconomy"),
            new("Wall", 0f, "PlayerOwned", "Building", "Wall", "WallOrTower"),
            new("Tower", 0f, "PlayerOwned", "Building", "Tower", "WallOrTower"),
            new("King", 0f, "PlayerOwned", "Player"),
        ];

        /// <summary>One line: how fast an attacker kills a target, e.g. "9.6 s · 9 hits", or "never".</summary>
        public static string Describe((int hits, float seconds)? ttk) =>
            ttk is not { } t ? "never" : t.hits == 1 ? "1 hit" : $"{t.seconds:0.#} s · {t.hits} hits";
    }
}
