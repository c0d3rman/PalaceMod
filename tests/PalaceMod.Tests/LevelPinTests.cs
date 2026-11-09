using System.Collections.Generic;
using Newtonsoft.Json;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    public static class LevelPinTests
    {
        private static (DesignPack pack, LevelDef level, WaveSetDef waves) Level()
        {
            var waves = new WaveSetDef
            {
                Id = "nights", Host = "Totend",
                Nights = [new NightDef { Spawns = [new SpawnDef { Unit = "grunt", Line = "A", Count = 3 }] }, new NightDef(), new NightDef()],
            };
            var level = new LevelDef
            {
                Id = "finale", Host = "Totend", WaveSet = "nights",
                BuildSlots = new BuildSlotOverlay { Add = [new BuildSlotAdd { Id = "tower-1", Base = "Tower", Pos = [1, 0, 2] }], Remove = ["Mill (2)"] },
                Props = [new PropDef { Id = "rock", Base = "Rock", Pos = [5, 0, 5] }],
            };
            return (new DesignPack { Id = "library", Levels = [level], WaveSets = [waves] }, level, waves);
        }

        private static LevelPin Pin((DesignPack pack, LevelDef level, WaveSetDef waves) l) => LevelPin.Of(l.pack, l.level, l.waves);

        public static void KeyIsPerLevelNotPerHost()
        {
            var pin = Pin(Level());
            Assert.Equal("Builder/library/finale", pin.Key, "key");
            Assert.True(LevelPin.IsKey(pin.Key), "is a Builder key");
            Assert.True(!LevelPin.IsKey("Totend"), "a scene name isn't");
            Assert.Equal("Builder/p/waves-w", LevelPin.KeyOf("p", null, "w"), "wave set without a level");
            Assert.Equal(3, pin.Nights, "nights");
            Assert.Equal("Totend", pin.Host, "host");
        }

        public static void SameLevelIsCompatibleAndUnedited()
        {
            var a = Pin(Level());
            var b = Pin(Level());
            Assert.Null(LevelPin.Incompatible(a, b), "same level");
            Assert.True(!LevelPin.Edited(a, b), "not edited");
        }

        public static void SurvivesJsonRoundTrip()
        {
            var a = Pin(Level());
            var back = JsonConvert.DeserializeObject<LevelPin>(JsonConvert.SerializeObject(a));
            Assert.Equal(a.Key, back.Key, "key");
            Assert.Null(LevelPin.Incompatible(back, a), "round trip");
            Assert.True(!LevelPin.Edited(back, a), "round trip not edited");
        }

        public static void LaterNightsAndPropsAreEditsNotIncompatible()
        {
            var a = Pin(Level());
            var l = Level();
            l.waves.Nights[2].Spawns = [new SpawnDef { Unit = "boss", Line = "B", Count = 1 }];
            l.level.Props[0].Pos = [6, 0, 6];
            l.level.Terrain = new TerrainDef();
            var b = Pin(l);
            Assert.Null(LevelPin.Incompatible(a, b), "edits load");
            Assert.True(LevelPin.Edited(a, b), "edited");
        }

        public static void NightCountHostAndSlotsAreIncompatible()
        {
            var a = Pin(Level());
            var fewer = Level();
            fewer.waves.Nights.RemoveAt(2);
            Assert.True(LevelPin.Incompatible(a, Pin(fewer))?.Contains("2 nights") == true, "night count");

            var moved = Level();
            moved.level.Host = moved.waves.Host = "Nordfels";
            Assert.NotNull(LevelPin.Incompatible(a, Pin(moved)), "host");

            var slot = Level();
            slot.level.BuildSlots.Add.Add(new BuildSlotAdd { Id = "tower-2", Base = "Tower", Pos = [3, 0, 3] });
            Assert.NotNull(LevelPin.Incompatible(a, Pin(slot)), "added slot");

            var unremoved = Level();
            unremoved.level.BuildSlots.Remove = [];
            Assert.NotNull(LevelPin.Incompatible(a, Pin(unremoved)), "slot no longer removed");

            var slotMoved = Level();
            slotMoved.level.BuildSlots.Add[0].Pos = [9, 0, 9];
            Assert.Null(LevelPin.Incompatible(a, Pin(slotMoved)), "a moved slot still loads");
        }

        public static void VanillaAndCustomNeverMix()
        {
            var a = Pin(Level());
            Assert.NotNull(LevelPin.Incompatible(null, a), "vanilla state in a custom level");
            Assert.NotNull(LevelPin.Incompatible(a, null), "custom state on the bare host");
            Assert.Null(LevelPin.Incompatible(null, null), "vanilla in vanilla");
            var other = Level();
            other.level.Id = "other";
            Assert.NotNull(LevelPin.Incompatible(a, Pin(other)), "another level on the same host");
        }
    }
}
