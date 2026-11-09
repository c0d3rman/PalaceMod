using System;
using System.IO;
using System.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Tests
{
    public static class LibraryTests
    {
        private static string TempRoot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "palace-lib-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static DesignPack Pack(string id) => new()
        {
            Id = id,
            Name = id,
            Units =
            [
                new UnitDef { Id = "grunt", Base = "vanilla:E Racer", Name = "Grunt" },
                new UnitDef { Id = "boss", Base = "vanilla:E Ogre", Rules = [PalaceMod.Builder.Model.Logic.LogicLowering.SpawnOnDeath("minion")] },
                new UnitDef { Id = "minion", Base = "vanilla:E Racer" },
                new UnitDef { Id = "unused", Base = "vanilla:E Racer" },
            ],
            WaveSets = [new WaveSetDef { Id = "nights", Host = "Nordfels", Nights = [new NightDef { Spawns = [new SpawnDef { Unit = "grunt", Line = "A" }, new SpawnDef { Unit = "unit:boss", Line = "B" }] }] }],
            Levels = [new LevelDef { Id = "pass", Name = "Pass", Host = "Nordfels", WaveSet = "nights" }],
        };

        public static void ImportSplitsAPackIntoItems()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            var r = lib.Import(Pack("a.pack"), null);
            Assert.Equal("pass", string.Join(",", r.Levels), "levels");
            Assert.Equal(4, r.Units.Count, "units");
            Assert.True(File.Exists(Path.Combine(root, "library", "levels", "pass.json")), "level file");
            Assert.True(File.Exists(Path.Combine(root, "library", "enemies", "boss.json")), "unit file");
            var again = new LibraryStore(Path.Combine(root, "library"));
            again.Load();
            Assert.Equal("nights", again.Level("pass").WaveSet, "level keeps its nights");
            Assert.Equal(1, again.Items(DesignKind.Level).Count, "one level");
            Assert.Equal("pass", string.Join(",", again.UsedBy("minion")), "minion is used through boss");
            Assert.Equal(0, again.UsedBy("unused").Count, "unused");
            var composed = again.Compose();
            Assert.Equal(LibraryStore.PackId, composed.Id, "composed id");
            Assert.Equal(1, composed.WaveSets.Count, "composed wave sets");
        }

        public static void ImportRenamesCollisionsAndRewritesRefs()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack("a.pack"), null);
            var b = Pack("b.pack");
            b.Units[0].Name = "Different grunt";   // grunt differs -> renamed; others identical -> reused
            var r = lib.Import(b, null);
            Assert.Equal("grunt-2", r.UnitIds["grunt"], "grunt renamed");
            Assert.True(r.Reused.Contains("boss"), "boss reused");
            Assert.Equal("pass-2", r.LevelIds["pass"], "level renamed");
            var lv = lib.Level("pass-2");
            Assert.True(lv.WaveSet != "nights", "wave set renamed: " + lv.WaveSet);
            lib.Load();
            var item = lib.Workspace("pass-2");
            var ws = item.FindWaveSet(lv.WaveSet);
            Assert.Equal("grunt-2", ws.Nights[0].Spawns[0].Unit, "spawn follows the renamed unit");
            Assert.Equal("unit:boss", ws.Nights[0].Spawns[1].Unit, "reused unit unchanged");
        }

        public static void ReimportOfTheSamePackReplaces()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack("a.pack"), null);
            var a2 = Pack("a.pack");
            a2.Levels[0].Name = "Pass v2";
            a2.Units[0].Name = "Grunt v2";
            var r = lib.Import(a2, null);
            Assert.Equal(0, r.LevelIds.Count, "no rename");
            Assert.Equal("Pass v2", lib.Level("pass").Name, "level replaced");
            Assert.Equal("Grunt v2", lib.Unit("grunt").Name, "unit replaced");
            Assert.True(Directory.GetFiles(Path.Combine(root, "library", "trash")).Length >= 2, "old files in trash");
            // An identical re-import is a no-op.
            var r3 = lib.Import(a2, null);
            Assert.Equal(0, r3.Levels.Count, "nothing new");
        }

        public static void WorkspaceSavesOnlyWhatChanged()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack("a.pack"), null);
            lib.Load();
            var baseline = lib.Workspace("pass");
            var now = PackSerializer.Clone(baseline);
            now.Levels[0].Name = "Renamed pass";
            now.Units.Add(new UnitDef { Id = "newbie", Base = "vanilla:E Racer" });
            now.Units.RemoveAll(u => u.Id == "unused");
            int written = lib.SaveWorkspace(now, baseline);
            Assert.Equal(3, written, "level + new unit + trashed unit");
            lib.Load();
            Assert.Equal("Renamed pass", lib.Level("pass").Name, "level saved");
            Assert.True(lib.HasUnit("newbie"), "new unit saved");
            Assert.True(!lib.HasUnit("unused"), "removed unit gone");

            // A second workspace with a stale copy of grunt doesn't overwrite grunt unless it edited it.
            var enemies = lib.Workspace(null);
            var stale = PackSerializer.Clone(enemies);
            var edit = PackSerializer.Clone(baseline);
            edit.Units.First(u => u.Id == "grunt").Name = "Edited grunt";
            lib.SaveWorkspace(edit, baseline);
            lib.Load();
            Assert.Equal(0, lib.SaveWorkspace(stale, enemies), "unchanged workspace writes nothing");
            Assert.Equal("Edited grunt", lib.Unit("grunt").Name, "edit kept");
        }

        public static void WaveSetIdsStayUniqueAcrossLevels()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.AddLevel(new LevelDef { Id = "one", Host = "Nordfels", WaveSet = "nights" }, [new WaveSetDef { Id = "nights", Host = "Nordfels" }]);
            string two = lib.AddLevel(new LevelDef { Id = "two", Host = "Nordfels", WaveSet = "nights" }, [new WaveSetDef { Id = "nights", Host = "Nordfels" }]);
            Assert.True(lib.Level(two).WaveSet != "nights", "second level's nights renamed");
            lib.Load();
            var c = lib.Compose();
            Assert.Equal(2, c.WaveSets.Select(w => w.Id).Distinct().Count(), "distinct ids in the composed pack");
        }

        public static void ExportTakesTheLevelsUnitsAndAssets()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            var p = Pack("a.pack");
            p.Units[0].Visuals = new VisualsDef { Icon = "asset:icon" };
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "assets"));
            File.WriteAllBytes(Path.Combine(src, "assets", "icon.png"), [1, 2, 3]);
            p.Assets["icon"] = new AssetDef { Path = "assets/icon.png" };
            p.Assets["stray"] = new AssetDef { Path = "assets/missing.png" };
            lib.Import(p, src);
            lib.Load();
            var files = new System.Collections.Generic.Dictionary<string, string>();
            var pack = lib.Export(["pass"], null, "me.pass", "Pass", "me", null, files);
            Assert.Equal("boss,grunt,minion", string.Join(",", pack.Units.Select(u => u.Id).OrderBy(x => x)), "unit closure");
            Assert.Equal("icon", string.Join(",", pack.Assets.Keys), "asset closure");
            Assert.True(files.ContainsKey("assets/icon.png") && File.Exists(files["assets/icon.png"]), "asset file");
            Assert.True(pack.Extra == null || !pack.Extra.ContainsKey(LibraryStore.MetaField), "no library metadata");
            // Round trip: importing the export into a fresh library gives the same items.
            var other = new LibraryStore(Path.Combine(root, "other"));
            var r = other.Import(pack, Path.Combine(root, "library"));
            Assert.Equal(3, r.Units.Count, "units round trip");
            Assert.True(File.Exists(Path.Combine(root, "other", "assets", "icon.png")), "asset copied");
        }

        public static void MigrationMovesPacksAndKeepsThem()
        {
            string root = TempRoot();
            string packs = Path.Combine(root, "packs");
            Directory.CreateDirectory(Path.Combine(packs, "a.pack"));
            File.WriteAllText(Path.Combine(packs, "a.pack", "pack.json"), PackSerializer.Write(Pack("a.pack")));
            Directory.CreateDirectory(Path.Combine(packs, "broken"));
            File.WriteAllText(Path.Combine(packs, "broken", "pack.json"), "{ nope");
            var lib = new LibraryStore(Path.Combine(root, "library"));
            var log = lib.MigratePacks(packs, Path.Combine(root, "migrated-packs"));
            Assert.Equal(2, log.Count, "two lines");
            Assert.True(Directory.Exists(Path.Combine(root, "migrated-packs", "a.pack")), "pack kept");
            Assert.True(Directory.Exists(Path.Combine(packs, "broken")), "unreadable pack left in place");
            Assert.True(lib.HasLevel("pass"), "level migrated");
        }

        public static void RenameAndDuplicateKeepIds()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack("a.pack"), null);
            lib.Rename(DesignKind.Enemy, "grunt", "Big grunt");
            Assert.Equal("Big grunt", lib.Unit("grunt").Name, "renamed");
            string copy = lib.Duplicate(DesignKind.Level, "pass");
            Assert.Equal("pass-2", copy, "copy id");
            Assert.True(lib.Level(copy).WaveSet != lib.Level("pass").WaveSet, "copy owns its own nights");
            Assert.True(lib.Trash(DesignKind.Level, copy), "trashed");
            Assert.True(!lib.HasLevel(copy), "gone");
        }
    }
}
