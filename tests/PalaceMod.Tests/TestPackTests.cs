using System.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Play;

namespace PalaceMod.Tests
{
    public static class TestPackTests
    {
        private static DesignPack Pack() => new()
        {
            Id = "p",
            Units =
            [
                new UnitDef { Id = "grunt", Base = "vanilla:E Racer" },
                new UnitDef { Id = "boss", Base = "vanilla:E Ogre", Name = "minion" },   // refers to "minion" (a unit id)
                new UnitDef { Id = "minion", Base = "vanilla:E Racer" },
                new UnitDef { Id = "unused", Base = "vanilla:E Racer" },
                new UnitDef { Id = "other-set", Base = "vanilla:E Racer" },
            ],
            WaveSets =
            [
                new WaveSetDef { Id = "nights", Host = "Neuland", Nights = [new NightDef { Spawns = [new SpawnDef { Unit = "grunt", Line = "A" }, new SpawnDef { Unit = "unit:boss", Line = "B" }] }] },
                new WaveSetDef { Id = "nights2", Host = "Neuland", Nights = [new NightDef { Spawns = [new SpawnDef { Unit = "other-set", Line = "A" }] }] },
            ],
            Levels =
            [
                new LevelDef { Id = "l1", Host = "Neuland", WaveSet = "nights" },
                new LevelDef { Id = "l2", Host = "Neuland", WaveSet = "nights2" },
                new LevelDef { Id = "l3", Host = "Neuland", WaveSet = "missing" },
            ],
        };

        public static void PlayKeepsTheLevelItsNightsAndTheirUnits()
        {
            var play = TestPacks.Play(Pack(), "l1", out string error);
            Assert.NotNull(play, "pack");
            Assert.Null(error, "error");
            Assert.Equal("l1", string.Join(",", play.Levels.Select(l => l.Id)), "levels");
            Assert.Equal("nights", string.Join(",", play.WaveSets.Select(w => w.Id)), "wave sets");
            Assert.Equal("boss,grunt,minion", string.Join(",", play.Units.Select(u => u.Id).OrderBy(x => x)), "units (with the one boss refers to)");
        }

        public static void PlayDoesNotTouchTheSource()
        {
            var source = Pack();
            TestPacks.Play(source, "l1", out _);
            Assert.Equal(3, source.Levels.Count, "source levels");
            Assert.Equal(5, source.Units.Count, "source units");
        }

        public static void PlayRefusesMissingLevelOrNights()
        {
            Assert.Null(TestPacks.Play(Pack(), "nope", out string e1), "missing level");
            Assert.NotNull(e1, "missing level error");
            Assert.Null(TestPacks.Play(Pack(), "l3", out string e2), "missing nights");
            Assert.True(e2.Contains("missing"), "names the missing nights");
        }

        public static void EditKeepsOnlyTheLayout()
        {
            var edit = TestPacks.Edit(Pack(), "l2");
            Assert.Equal(1, edit.Levels.Count, "levels");
            Assert.Null(edit.Levels[0].WaveSet, "no nights");
            Assert.Equal(0, edit.Units.Count, "units");
            Assert.Equal(0, edit.WaveSets.Count, "wave sets");
        }
    }
}
