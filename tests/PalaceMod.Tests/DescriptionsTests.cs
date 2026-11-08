using System;
using System.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    /// <summary>Descriptions: every kind has texts, lookups ignore case, spaces and underscores, unknown names give
    /// null, and texts fit in a tooltip.</summary>
    public static class DescriptionsTests
    {
        private const int MaxLength = 220;

        public static void EveryKindHasEntries()
        {
            foreach (DescKind kind in Enum.GetValues(typeof(DescKind)))
                Assert.True(Descriptions.Names(kind).Count > 0, $"{kind} has entries");
        }

        public static void EveryNameResolvesToItsText()
        {
            foreach (DescKind kind in Enum.GetValues(typeof(DescKind)))
                foreach (var name in Descriptions.Names(kind))
                    Assert.NotNull(Descriptions.For(kind, name), $"{kind} {name}");
        }

        public static void LookupIgnoresCaseSpacesAndUnderscores()
        {
            string flying = Descriptions.Tag("Flying");
            Assert.NotNull(flying, "Flying");
            Assert.Equal(flying, Descriptions.Tag("flying"), "lower case");
            Assert.Equal(flying, Descriptions.Tag(" FLY ING "), "spaces");
            Assert.Equal(Descriptions.Tag("ArmoredAgainstRanged"), Descriptions.Tag("armored_against ranged"), "underscores and spaces");
            Assert.Equal(Descriptions.Tag("AUTO_Alive"), Descriptions.Tag("auto alive"), "AUTO_ prefix");
            Assert.Equal(Descriptions.For(DescKind.Stat, "attackCooldown"), Descriptions.For(DescKind.Stat, "Attack cooldown"), "editor label");
            Assert.Equal(Descriptions.For(DescKind.SlotKind, "Wall"), Descriptions.For(DescKind.SlotKind, "Walls and gates"), "slot kind label");
            Assert.Equal(Descriptions.For(DescKind.Upgrade, "Sacrifice"), Descriptions.For(DescKind.Upgrade, "Sacrifice 2"), "tier suffix");
        }

        /// <summary>TagManager.ETag as of game 2.14, spelled out because Assembly-CSharp isn't loadable from tests.</summary>
        private static readonly string[] GameTags =
        [
            "NONE", "PlayerOwned", "EnemyOwned", "Player", "CastleCenter", "MeeleFighter", "RangedFighter", "Flying", "PlayerUnit",
            "Building", "SiegeWeapon", "AUTO_Alive", "AUTO_KnockedOutAndHealOnDawn", "Wall", "InfrastructureEconomy",
            "TakesReducedDamageFromPlayerAttacks", "PracticeTargets", "FastMoving", "ArmoredAgainstRanged", "VulnerableVsRanged", "Monster",
            "House", "WallOrTower", "AUTO_Commanded", "TakesIncreasedDamageFromTowers", "Tower", "AUTO_NoReviveNextMorning",
            "PlayerOwnedPriorityTarget", "BlockableEnemyProjectile", "Boss", "UselessWallThatDoesNotBlockPath", "Healer", "MagicProjectile",
            "NaturallyVulnerableToSplash", "NaturallyHighHealthTarget", "UnreachableForBigEnemiesDueToLevelDesign", "PlayerShrine",
            "VodooCursed", "PlayerHeroUnit", "FireAndExplosionResistant", "Humanoid", "LargeUnit", "Exploding", "TrainingFacility", "Bridge",
            "Group1", "Group2", "Group3", "OnDeathEnemySpawner", "Undead", "Vampire",
        ];

        public static void EveryGameTagIsDescribed()
        {
            foreach (var name in GameTags)
                Assert.NotNull(Descriptions.Tag(name), $"tag {name}");
            Assert.Equal(GameTags.Length, Descriptions.Names(DescKind.Tag).Count, "no tags beyond the game's");
        }

        public static void BuilderKeysAreDescribed()
        {
            foreach (var key in new[] { "maxHp", "moveSpeed", "attackCooldown", "attackRange", "keepDistance", "aggroTime", "damageMult",
                         "projectileSpeed", "scale", "meshScale", "meshRotY", "tint", "damage", "splash" })
                Assert.NotNull(Descriptions.For(DescKind.Stat, key), $"stat {key}");
            foreach (var key in new[] { "Flying", "LargeUnit", "Elite", "Solid", "StartDeactivated", "ActivatorUpgradesThis",
                         "canSpawnFlying", "canSpawnSmallGround", "canSpawnBigGround", "Upgrades ≤ castle level" })
                Assert.NotNull(Descriptions.For(DescKind.Flag, key), $"flag {key}");
            Assert.NotNull(Descriptions.For(DescKind.Behaviour, BehaviourTypes.SpawnOnDeath), "SpawnOnDeath");
            Assert.NotNull(Descriptions.For(DescKind.Weapon, "inherit"), "inherit weapon");
            foreach (var kind in new[] { "Castle", "Economy", "Military", "Wall", "Special", "Spawn", "Scenery", "Income", "Spawn line" })
                Assert.NotNull(Descriptions.For(DescKind.SlotKind, kind), $"slot kind {kind}");
        }

        public static void UnknownGivesNull()
        {
            Assert.Null(Descriptions.Tag("NoSuchTag"), "unknown tag");
            Assert.Null(Descriptions.Tag(null), "null name");
            Assert.Null(Descriptions.Tag(""), "empty name");
            Assert.Null(Descriptions.For(DescKind.Stat, "Flying"), "a name from another kind");
            Assert.Null(Descriptions.For(DescKind.Upgrade, "Nope 3"), "unknown with a tier suffix");
        }

        public static void TextsFitATooltip()
        {
            foreach (DescKind kind in Enum.GetValues(typeof(DescKind)))
                foreach (var name in Descriptions.Names(kind))
                {
                    string text = Descriptions.For(kind, name);
                    Assert.True(!string.IsNullOrWhiteSpace(text), $"{kind} {name} is empty");
                    Assert.True(text.Length <= MaxLength, $"{kind} {name} is {text.Length} chars (max {MaxLength})");
                    Assert.True(text == text.Trim() && text.EndsWith("."), $"{kind} {name} is a trimmed sentence");
                }
        }

        public static void NamesAreDistinctAfterNormalising()
        {
            foreach (DescKind kind in Enum.GetValues(typeof(DescKind)))
            {
                var names = Descriptions.Names(kind).Select(Descriptions.Normalize).ToList();
                Assert.Equal(names.Count, names.Distinct().Count(), $"{kind} names collide");
            }
        }
    }
}
