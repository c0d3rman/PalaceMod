using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;
using PalaceMod.Builder.Studio.Entities;

namespace PalaceMod.Tests
{
    public static class EntityStudioTests
    {
        public static void NewThingsHaveTheirKindsParts()
        {
            var p = ThingKinds.Make("projectile", "rock", "Rock", "mesh:Rock_01");
            Assert.Equal("projectile", p.Kind, "kind");
            Assert.True(p.Projectile != null && p.Projectile.Hits == "foe", "a projectile flies and hits foes");
            Assert.Equal("owner", p.Team, "a projectile takes its launcher's side");
            Assert.Equal("mesh:Rock_01", p.Visual?.Mesh, "look");
            Assert.True(ThingKinds.Make("prop", "a", "A", null).Body?.Solid == true, "a prop blocks");
            Assert.True(ThingKinds.Make("pickup", "a", "A", null).Pickup?.By == "king", "the king picks up");
            Assert.True(ThingKinds.Make("area", "a", "A", null).Area?.Shape?.Circle != null, "an area has a shape");
            var logic = ThingKinds.Make("logic", "a", "A", null);
            Assert.True(logic.Visual == null && logic.Body == null && logic.Projectile == null, "a logic object has no parts");
            // Every kind the picker offers round-trips through the pack format and validates as an entity.
            foreach (var k in ThingKinds.All)
            {
                var e = ThingKinds.Make(k.Id, "x-" + k.Id, k.Label, null);
                var json = JObject.FromObject(e);
                Assert.Equal(k.Id, (string)json["kind"], "kind written " + k.Id);
                Assert.Equal(k.Id, ThingKinds.Of(json.ToObject<EntityDef>()), "kind read back " + k.Id);
            }
        }

        public static void KindFallsBackToComponents()
        {
            Assert.Equal("projectile", ThingKinds.Of(new EntityDef { Projectile = new ProjectileDef() }), "projectile part");
            Assert.Equal("area", ThingKinds.Of(new EntityDef { Area = new AreaDef() }), "area part");
            Assert.Equal("prop", ThingKinds.Of(new EntityDef()), "plain");
            Assert.Equal("Logic object", ThingKinds.Label("logic"), "label");
        }

        public static void FreeIds()
        {
            Assert.Equal("new-projectile", ThingKinds.FreeId("new-projectile", ["rock"]), "free");
            Assert.Equal("new-projectile-3", ThingKinds.FreeId("new-projectile", ["new-projectile", "new-projectile-2"]), "next");
        }

        public static void UsedByAttacksAndPlaces()
        {
            var pack = new DesignPack { Id = "t" };
            pack.Units.Add(new UnitDef { Id = "treant", Name = "Treant", Attacks = [new AttackDef { Launch = new LaunchDef { Entity = "other" } }, new AttackDef { Launch = new LaunchDef { Entity = "tree" } }] });
            var lv = new LevelDef { Id = "l1", Name = "Level 1", Logic = new LogicDef { Places = [new PlaceDef { Id = "t1", Entity = "tree" }] } };
            pack.Levels.Add(lv);
            pack.Entities = [new EntityDef { Id = "tree", Kind = "projectile" }, new EntityDef { Id = "barrel", Kind = "prop" }];
            var used = ThingKinds.UsedBy(pack, "tree");
            Assert.Equal(2, used.Count, "two users");
            Assert.True(used.Contains("Treant: attack 2"), "the attack");
            Assert.True(used.Contains("Level 1: placed (t1)"), "the place");
            Assert.Equal("tree", ThingKinds.Launchable(pack).First().Id, "projectiles first");
        }
    }
}
