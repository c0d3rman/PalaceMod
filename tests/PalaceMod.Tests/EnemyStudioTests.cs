using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Enemies;

namespace PalaceMod.Tests
{
    public static class EnemyStudioTests
    {
        private static DamageModDef Mod(float add, float mult, params string[] tags) => new() { Add = add, Mult = mult, Tags = [.. tags] };

        public static void DamageFollowsTheGamesFormula()
        {
            // (0 + 10) * 1, then a +5 x2 bonus against towers: (10 + 5) * 2 = 30; x0.5 attacker multiplier = 15.
            var mods = new List<DamageModDef> { Mod(10, 1), Mod(5, 2, "Tower") };
            var tower = new HashSet<string> { "PlayerOwned", "Building", "Tower" };
            var house = new HashSet<string> { "PlayerOwned", "Building", "House" };
            Assert.Equal(30f, EnemyMath.Damage(mods, tower), "tower bonus applies");
            Assert.Equal(10f, EnemyMath.Damage(mods, house), "no bonus on a house");
            Assert.Equal(15f, EnemyMath.Damage(mods, tower, 0.5f), "attacker multiplier last");
            // A modifier needs all its tags.
            Assert.Equal(10f, EnemyMath.Damage([Mod(10, 1), Mod(100, 1, "Tower", "Wall")], tower), "all tags required");
            Assert.Equal(0f, EnemyMath.Damage(null, tower), "no modifiers, no damage");
        }

        public static void TimeToKill()
        {
            var t = EnemyMath.TimeToKill(100f, 30f, 2f);
            Assert.True(t != null, "dies");
            Assert.Equal(4, t.Value.hits, "4 hits for 100 hp at 30");
            Assert.Equal(6f, t.Value.seconds, "first hit at once, then 3 cooldowns");
            Assert.Equal(1, EnemyMath.TimeToKill(10f, 30f, 2f).Value.hits, "one hit");
            Assert.Equal(3, EnemyMath.TimeToKill(90f, 30f, 2f).Value.hits, "exact multiple");
            Assert.True(EnemyMath.TimeToKill(100f, 0f, 1f) == null, "no damage never kills");
            Assert.Equal("never", EnemyMath.Describe(null), "never");
            Assert.Equal("1 hit", EnemyMath.Describe((1, 0f)), "one hit text");
            Assert.Equal("6 s · 4 hits", EnemyMath.Describe((4, 6f)), "text");
        }

        public static void PerSecond()
        {
            var a = new EffectiveUnit { AttackCooldown = 2f, DamageMult = 1.5f, Damage = [Mod(10, 1)] };
            Assert.Equal(7.5f, EnemyMath.PerSecond(a, new HashSet<string>()), "10 * 1.5 / 2");
            Assert.Equal(0f, EnemyMath.PerSecond(new EffectiveUnit { AttackCooldown = 0f, Damage = [Mod(10, 1)] }, new HashSet<string>()), "no cooldown, no dps");
        }

        public static void ModelRefs()
        {
            Assert.Equal(ModelKind.Asset, ModelRef.Kind("asset:goblin", out string id), "asset");
            Assert.Equal("goblin", id, "asset id");
            Assert.Equal(ModelKind.Scenery, ModelRef.Kind("mesh:Tree_02@Nordfels", out id), "scenery");
            Assert.Equal("Tree_02@Nordfels", id, "scenery id keeps the map");
            Assert.Equal(ModelKind.Unit, ModelRef.Kind("vanilla:P Knight", out id), "unit");
            Assert.Equal("P Knight", id, "unit id");
            Assert.Equal(ModelKind.Invalid, ModelRef.Kind("tree", out _), "no prefix");
            Assert.Equal(ModelKind.Invalid, ModelRef.Kind("mesh:", out _), "empty");
            Assert.Equal(ModelKind.Invalid, ModelRef.Kind("scene:x", out _), "other prefix");
            Assert.Equal("own", ModelRef.Look(new VisualsDef { Mesh = "mesh:Tree" }), "scenery keeps its colours");
            Assert.Equal("unit", ModelRef.Look(new VisualsDef { Mesh = "asset:x" }), "imported meshes take the enemy material");
            Assert.Equal("unit", ModelRef.Look(new VisualsDef { Mesh = "mesh:Tree", MeshLook = "unit" }), "explicit");
        }

        public static void ValidatesModelFields()
        {
            var pack = new DesignPack { Id = "p", Name = "P" };
            pack.Units.Add(new UnitDef
            {
                Id = "tree", Base = "vanilla:E Racer", Name = "Tree",
                Visuals = new VisualsDef { Mesh = "mesh:Tree_02", MeshOffset = [0, 0.5f, 0], MeshRotX = 10, MeshLook = "own", Motion = "bob" },
            });
            List<ValidationIssue> issues;
            // Visuals checks touch the validator's tag table, which needs the game's assembly: skipped (passes) without it.
            try { issues = Validator.Validate(pack); }
            catch (System.TypeInitializationException) { return; }
            Assert.True(!issues.Any(i => i.Path.StartsWith("units[0].visuals")), "a scenery model with fit is valid: " + string.Join("; ", issues.Select(i => i.Path + " " + i.Message)));
            pack.Units[0].Visuals = new VisualsDef { Mesh = "tree", MeshOffset = [0, 99], MeshRotZ = 400, MeshLook = "shiny", Motion = "fly" };
            var bad = Validator.Validate(pack).Where(i => i.Severity == Severity.Error).Select(i => i.Path).ToList();
            foreach (var p in new[] { "units[0].visuals.mesh", "units[0].visuals.meshOffset", "units[0].visuals.meshRotZ", "units[0].visuals.meshLook", "units[0].visuals.motion" })
                Assert.True(bad.Contains(p), "error at " + p + " (got " + string.Join(", ", bad) + ")");
        }
    }
}
