using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Tests
{
    /// <summary>Entities, statuses and presets as library items (Model/LibraryStore.Logic.cs).</summary>
    public static class LibraryLogicTests
    {
        private static string TempRoot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "palace-liblogic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static RuleDef Rule(string json) => JObject.Parse(json).ToObject<RuleDef>();

        private static DesignPack Pack() => new()
        {
            Id = "a.pack",
            Name = "A",
            Units = [new UnitDef { Id = "treant", Base = "vanilla:E Ogre", Attacks = [new AttackDef { Cooldown = 3, Launch = new LaunchDef { Entity = "tree" } }],
                                   Rules = [Rule(@"{""when"":""death"",""do"":[{""do"":""applyStatus"",""status"":""burning"",""target"":""all('foe', 3)""}]}")] },
                     new UnitDef { Id = "imp", Base = "vanilla:E Racer" }],
            Entities = [new EntityDef { Id = "tree", Kind = "projectile", Rules = [Rule(@"{""when"":""impact"",""do"":[{""do"":""spawn"",""entity"":""imp""}]}")] },
                        new EntityDef { Id = "unused", Kind = "prop" }],
            Statuses = [new StatusDef { Id = "burning", Duration = 4 }, new StatusDef { Id = "cold", Duration = 2 }],
            Presets = [new PresetDef { Id = "glow", Name = "Glow", Rules = [Rule(@"{""when"":""spawn"",""do"":[]}")] }],
            Levels = [new LevelDef { Id = "grove", Name = "Grove", Host = "Nordfels", Logic = new LogicDef { Rules = [Rule(@"{""when"":""dusk"",""do"":[{""do"":""spawn"",""entity"":""treant""}]}")] } }],
        };

        public static void ImportSavesLogicItemsAndExportTakesWhatsUsed()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack(), null);
            Assert.True(File.Exists(lib.LogicFile(LibraryStore.LogicKind.Entity, "tree")), "entity file");
            Assert.True(File.Exists(lib.LogicFile(LibraryStore.LogicKind.Status, "burning")), "status file");
            Assert.True(File.Exists(lib.LogicFile(LibraryStore.LogicKind.Preset, "glow")), "preset file");
            lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Load();
            Assert.Equal("", string.Join("; ", lib.Issues), "loads clean");
            Assert.Equal("projectile", lib.Entity("tree").Kind, "entity reads back");
            Assert.NotNull(lib.Preset("glow"), "preset reads back");
            var composed = lib.Compose();
            Assert.Equal(2, composed.Entities.Count, "composed entities");
            Assert.Equal(1, composed.Presets.Count, "composed presets");

            // The level's script spawns the treant, which launches the tree, which spawns the imp and burns.
            var export = lib.Export(["grove"], [], "x.y", "X", "me", null);
            Assert.Equal("imp,treant", string.Join(",", export.Units.Select(u => u.Id).OrderBy(x => x)), "units through logic");
            Assert.Equal("tree", string.Join(",", export.Entities.Select(e => e.Id)), "only the used entity");
            Assert.Equal("burning", string.Join(",", export.Statuses.Select(e => e.Id)), "only the used status");
            Assert.True(export.Presets == null || export.Presets.Count == 0, "no unused presets");
        }

        public static void WorkspaceSavesChangedLogicItems()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack(), null);
            var baseline = lib.Workspace("grove");
            var now = PackSerializer.Clone(baseline);
            now.Statuses.First(s => s.Id == "cold").Duration = 9;
            now.Entities.RemoveAll(e => e.Id == "unused");
            now.Presets.Add(new PresetDef { Id = "new-one", Name = "New" });
            int written = lib.SaveWorkspace(now, baseline);
            Assert.Equal(3, written, "changed status, trashed entity, new preset");
            Assert.True(!File.Exists(lib.LogicFile(LibraryStore.LogicKind.Entity, "unused")), "trashed");
            Assert.Equal(9.0, lib.Status("cold").Duration.Value, "status saved");
            Assert.Equal(0, lib.SaveWorkspace(PackSerializer.Clone(lib.Workspace("grove")), lib.Workspace("grove")), "nothing changed");
        }

        public static void ImportRenamesClashesAndRewritesReferences()
        {
            string root = TempRoot();
            var lib = new LibraryStore(Path.Combine(root, "library"));
            lib.Import(Pack(), null, origin: "first");
            var other = Pack();
            other.Id = "b.pack";
            other.Statuses[0].Duration = 7;   // a different "burning"
            other.Entities[0].Lifetime = 5;   // a different "tree"
            var r = lib.Import(other, null, origin: "second");
            Assert.Equal(4.0, lib.Status("burning").Duration.Value, "the original stays");
            Assert.Equal(7.0, lib.Status("burning-2").Duration.Value, "the clash is renamed");
            var treant2 = lib.Unit(r.UnitIds.TryGetValue("treant", out var t2) ? t2 : "treant");
            string json = JObject.FromObject(treant2).ToString();
            Assert.True(json.Contains("\"burning-2\""), "the new treant uses the renamed status: " + json);
            Assert.True(json.Contains("\"tree-2\""), "and the renamed entity");
            Assert.True(r.Issues.Any(i => i.Message.Contains("imported as \"burning-2\"")), "says so");
        }

        public static void SavePresetKeepsIdsUnique()
        {
            var lib = new LibraryStore(Path.Combine(TempRoot(), "library"));
            string a = lib.SavePreset(new PresetDef { Id = "trap", Name = "Trap" });
            string b = lib.SavePreset(new PresetDef { Id = "trap", Name = "Other trap" });
            string c = lib.SavePreset(new PresetDef { Id = "trap", Name = "Trap" });
            Assert.Equal("trap", a, "first");
            Assert.Equal("trap-2", b, "different content gets a new id");
            Assert.Equal("trap", c, "same content reuses the id");
            Presets.Library = id => lib.Preset(id);
            try { Assert.Equal("Other trap", Presets.Find(null, "trap-2")?.Name, "Presets.Find sees library presets"); }
            finally { Presets.Library = null; }
        }
    }
}
