using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PalaceMod.Builder;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Sharing;

namespace PalaceMod.Tests
{
    /// <summary>Just enough of IPackRepository for imports: InstallNew with the real never-overwrite renaming.</summary>
    internal sealed class FakeRepository(string root) : IPackRepository
    {
        public string Root { get; } = root;
        private readonly List<PackEntry> packs = [];
        public IReadOnlyList<PackEntry> Packs => packs;
        public PackEntry Find(string packId) => packs.FirstOrDefault(p => p.Id == packId);
        public void Refresh() { }
        public event Action Changed { add { } remove { } }
        public string Save(DesignPack pack) => throw new NotSupportedException();
        public bool Delete(string packId) => false;
        public List<ValidationIssue> Validate(DesignPack pack) => [];

        public PackEntry InstallNew(DesignPack pack, string sourceFolder = null)
        {
            pack = PackSerializer.Clone(pack);
            string id = pack.Id;
            for (int n = 2; Find(id) != null; n++) id = $"{pack.Id}-{n}";
            pack.Id = id;
            string folder = Path.Combine(Root, "packs", id);
            Directory.CreateDirectory(folder);
            if (sourceFolder != null && Directory.Exists(Path.Combine(sourceFolder, "assets")))
                Copy(Path.Combine(sourceFolder, "assets"), Path.Combine(folder, "assets"));
            File.WriteAllText(Path.Combine(folder, "pack.json"), PackSerializer.Write(pack));
            var entry = new PackEntry { Pack = pack, Folder = folder, Source = PackSource.User };
            packs.Add(entry);
            return entry;
        }

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    public static class PackArchiveTests
    {
        private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4];

        /// <summary>A sample pack folder on disk with two asset files and one non-asset file.</summary>
        private static PackEntry SampleFolder(string dir)
        {
            var pack = ShareCodecTests.Sample();
            pack.Assets = new Dictionary<string, AssetDef> { ["icon"] = new() { Path = "assets/icons/goblin.png" }, ["roar"] = new() { Path = "assets/roar.ogg" } };
            string folder = Path.Combine(dir, "src", pack.Id);
            Directory.CreateDirectory(Path.Combine(folder, "assets", "icons"));
            File.WriteAllBytes(Path.Combine(folder, "assets", "icons", "goblin.png"), Png);
            File.WriteAllBytes(Path.Combine(folder, "assets", "roar.ogg"), [9, 8, 7]);
            File.WriteAllBytes(Path.Combine(folder, "assets", "evil.dll"), [0x4d, 0x5a]);
            File.WriteAllText(Path.Combine(folder, "pack.json"), PackSerializer.Write(pack));
            return new PackEntry { Pack = pack, Folder = folder, Source = PackSource.User };
        }

        private static byte[] Zip(params (string name, byte[] data)[] entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, data) in entries)
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(data, 0, data.Length);
                }
            return ms.ToArray();
        }

        private static byte[] PackJson() => Encoding.UTF8.GetBytes(ShareCodecTests.SampleJson());

        private static (DesignPack pack, List<ValidationIssue> issues, string staging) ExtractBytes(TempDir dir, byte[] zip)
        {
            string staging = Path.Combine(dir.Path, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var issues = new List<ValidationIssue>();
            using var ms = new MemoryStream(zip);
            return (PackArchive.Extract(ms, staging, issues), issues, staging);
        }

        private static string Rejected(TempDir dir, byte[] zip)
        {
            var (pack, issues, staging) = ExtractBytes(dir, zip);
            Assert.Null(pack, "pack");
            Assert.True(issues.Any(i => i.Severity == Severity.Error), "an error issue");
            return issues.First(i => i.Severity == Severity.Error).Message;
        }

        private static IEnumerable<string> FilesUnder(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : [];

        public static void ExportImportRoundTripWithAssets()
        {
            using var dir = new TempDir();
            var repo = new FakeRepository(Path.Combine(dir.Path, "root"));
            var archive = new PackArchive(() => repo);
            var entry = SampleFolder(dir.Path);

            string file = archive.Export(entry);
            Assert.Equal(Path.Combine(repo.Root, "exports", entry.Id + ".tfpack"), file, "default export path");
            using (var zip = ZipFile.OpenRead(file))
            {
                var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
                Assert.Equal("assets/icons/goblin.png,assets/roar.ogg,pack.json,tfpack.json", string.Join(",", names), "archive entries (no .dll)");
            }
            Assert.True(archive.Export(entry) != file, "second export doesn't overwrite the first");

            var issues = new List<ValidationIssue>();
            var imported = archive.Import(file, issues);
            Assert.NotNull(imported, "imported: " + string.Join("; ", issues));
            Assert.Equal(entry.Id, imported.Id, "id kept");
            Assert.Equal(PackSerializer.WriteCompact(entry.Pack), PackSerializer.WriteCompact(imported.Pack), "pack JSON");
            Assert.True(File.ReadAllBytes(Path.Combine(imported.Folder, "assets", "icons", "goblin.png")).SequenceEqual(Png), "png bytes");
            Assert.True(File.Exists(Path.Combine(imported.Folder, "assets", "roar.ogg")), "ogg copied");
            Assert.True(!Directory.Exists(Path.Combine(repo.Root, "staging")) || !Directory.EnumerateFileSystemEntries(Path.Combine(repo.Root, "staging")).Any(), "staging cleaned up");

            // Importing again never overwrites: it gets a new id and says so.
            issues.Clear();
            var again = archive.Import(file, issues);
            Assert.Equal(entry.Id + "-2", again.Id, "renamed id");
            Assert.True(issues.Any(i => i.Severity == Severity.Warning && i.Message.Contains("installed as")), "rename warning");
        }

        public static void AssetsOutsideAssetsFolderAreCarried()
        {
            using var dir = new TempDir();
            var repo = new FakeRepository(Path.Combine(dir.Path, "root"));
            var archive = new PackArchive(() => repo);
            var entry = SampleFolder(dir.Path);
            Directory.CreateDirectory(Path.Combine(entry.Folder, "art"));
            File.WriteAllBytes(Path.Combine(entry.Folder, "art", "skin.png"), Png);
            File.WriteAllBytes(Path.Combine(dir.Path, "outside.png"), Png);
            entry.Pack.Assets["skin"] = new AssetDef { Path = "art/skin.png" };
            entry.Pack.Assets["sneaky"] = new AssetDef { Path = "../outside.png" };
            var imported = archive.Import(archive.Export(entry), new List<ValidationIssue>());
            Assert.Equal("assets/art/skin.png", imported.Pack.Assets["skin"].Path, "path rewritten under assets/");
            Assert.True(File.Exists(Path.Combine(imported.Folder, "assets", "art", "skin.png")), "file carried");
            Assert.Equal("../outside.png", imported.Pack.Assets["sneaky"].Path, "unsafe path left alone");
            Assert.True(!File.Exists(Path.Combine(imported.Folder, "assets", "outside.png")) && FilesUnder(imported.Folder).All(f => !f.EndsWith("outside.png")), "file outside the pack not exported");
            Assert.Equal("art/skin.png", entry.Pack.Assets["skin"].Path, "source pack untouched");
        }

        public static void BuiltInPackWithoutFolderExports()
        {
            using var dir = new TempDir();
            var repo = new FakeRepository(dir.Path);
            var archive = new PackArchive(() => repo);
            var entry = new PackEntry { Pack = ShareCodecTests.Sample(), Folder = null, Source = PackSource.BuiltIn };
            var imported = archive.Import(archive.Export(entry), new List<ValidationIssue>());
            Assert.NotNull(imported, "imported");
        }

        public static void ThunderstoreExportIsImportable()
        {
            using var dir = new TempDir();
            var repo = new FakeRepository(Path.Combine(dir.Path, "root"));
            var archive = new PackArchive(() => repo, _ => Png);
            var entry = SampleFolder(dir.Path);
            string file = archive.ExportThunderstore(entry);
            using (var zip = ZipFile.OpenRead(file))
            {
                var names = zip.Entries.Select(e => e.FullName).ToHashSet();
                foreach (var n in new[] { "manifest.json", "README.md", "icon.png", $"PalaceBuilder/packs/{entry.Id}/pack.json", $"PalaceBuilder/packs/{entry.Id}/assets/roar.ogg" })
                    Assert.True(names.Contains(n), "contains " + n);
                var manifest = Newtonsoft.Json.Linq.JObject.Parse(new StreamReader(zip.GetEntry("manifest.json").Open()).ReadToEnd());
                Assert.Equal("Sample_Goblin_Night", (string)manifest["name"], "Thunderstore-safe name");
                Assert.Equal("1.0.0", (string)manifest["version_number"], "version");
            }
            var issues = new List<ValidationIssue>();
            var imported = archive.Import(file, issues);
            Assert.NotNull(imported, "Thunderstore zip imports: " + string.Join("; ", issues));
            Assert.True(File.Exists(Path.Combine(imported.Folder, "assets", "icons", "goblin.png")), "assets from nested folder");
            Assert.True(issues.Any(i => i.Message.Contains("skipped")), "manifest/readme/icon reported as skipped");
        }

        public static void AcceptsHandMadeZipOfPackFolder()
        {
            using var dir = new TempDir();
            var (pack, issues, staging) = ExtractBytes(dir, Zip(("my-pack/pack.json", PackJson()), ("my-pack/assets/a.png", Png)));
            Assert.NotNull(pack, "pack: " + string.Join("; ", issues));
            Assert.True(File.Exists(Path.Combine(staging, "assets", "a.png")), "asset extracted");
        }

        public static void RejectsZipSlipPaths()
        {
            string[] evil =
            [
                "../evil.png", "assets/../../evil.png", "assets/../../../tmp/evil.png", "/etc/evil.png", "C:/evil.png",
                "assets\\..\\..\\evil.png", "assets/./x.png", "assets//x.png", "assets/x.png:stream", "assets/a\u0001.png",
                "assets/..", "assets/x../y.png",
            ];
            foreach (var name in evil)
            {
                using var dir = new TempDir();
                string msg = Rejected(dir, Zip(("pack.json", PackJson()), (name, Png)));
                Assert.True(msg.Contains("unsafe"), $"{name}: {msg}");
                Assert.True(!File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path), "evil.png")), $"{name}: nothing written outside");
                Assert.Equal(0, Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories).Length, $"{name}: nothing written at all");
            }
        }

        public static void SafeEntryNames()
        {
            foreach (var ok in new[] { "pack.json", "assets/a.png", "PalaceBuilder/packs/x/assets/sub dir/b.ogg" })
                Assert.True(PackArchive.IsSafeEntryName(ok), ok);
            foreach (var bad in new[] { "", "/a", "a/../b", "..", "a\\b", "c:x", "a/", "a//b", "x/.", new string('a', 300) })
                Assert.True(!PackArchive.IsSafeEntryName(bad), bad);
        }

        public static void RejectsMissingOrDuplicatePackJson()
        {
            using var dir = new TempDir();
            Assert.True(Rejected(dir, Zip(("assets/a.png", Png))).Contains("no pack.json"), "missing");
            Assert.True(Rejected(dir, Zip(("a/pack.json", PackJson()), ("b/pack.json", PackJson()))).Contains("more than one"), "two packs");
        }

        public static void RejectsNonZipAndDamagedFiles()
        {
            using var dir = new TempDir();
            Assert.True(Rejected(dir, Encoding.UTF8.GetBytes("definitely not a zip file")).Contains("isn't a valid zip"), "text");
            Assert.True(Rejected(dir, []).Contains("isn't a valid zip"), "empty");
            var zip = Zip(("pack.json", PackJson()));
            Rejected(dir, zip.Take(zip.Length / 2).ToArray());
        }

        public static void SkipsUnknownFileTypes()
        {
            using var dir = new TempDir();
            var (pack, issues, staging) = ExtractBytes(dir, Zip(("pack.json", PackJson()), ("assets/run.dll", [1]), ("assets/run.sh", [1]), ("notes.md", [1]), ("assets/ok.png", Png)));
            Assert.NotNull(pack, "pack");
            Assert.Equal(1, FilesUnder(staging).Count(), "only the png");
            Assert.True(issues.Any(i => i.Severity == Severity.Warning && i.Message.Contains("skipped 3")), "skip warning");
        }

        public static void RejectsBadPackJson()
        {
            using var dir = new TempDir();
            Rejected(dir, Zip(("pack.json", Encoding.UTF8.GetBytes("{oops"))));
            Rejected(dir, Zip(("pack.json", [0xff, 0xfe, 0x00, 0x80])));
            Assert.True(Rejected(dir, Zip(("pack.json", Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":42}")))).Contains("update PalaceMod"), "pack version");
            Assert.True(Rejected(dir, Zip(("pack.json", new byte[PackArchive.MaxPackJsonBytes + 1]))).Contains("too large"), "oversized pack.json");
        }

        public static void ArchiveLayoutVersionHook()
        {
            using var dir = new TempDir();
            byte[] Manifest(int v) => Encoding.UTF8.GetBytes($"{{\"format\":\"tfpack\",\"version\":{v}}}");
            Assert.NotNull(ExtractBytes(dir, Zip(("tfpack.json", Manifest(1)), ("pack.json", PackJson()))).pack, "v1");
            Assert.True(Rejected(dir, Zip(("tfpack.json", Manifest(2)), ("pack.json", PackJson()))).Contains("newer PalaceMod"), "v2");
            Assert.True(Rejected(dir, Zip(("tfpack.json", Encoding.UTF8.GetBytes("{\"format\":\"zip\",\"version\":1}")), ("pack.json", PackJson()))).Contains("manifest"), "wrong format");
        }

        public static void HostileManifestValuesDontThrow()
        {
            using var dir = new TempDir();
            byte[] M(string json) => Encoding.UTF8.GetBytes(json);
            Assert.True(Rejected(dir, Zip(("tfpack.json", M("{\"format\":\"tfpack\",\"version\":2147483648}")), ("pack.json", PackJson()))).Contains("newer"), "int overflow");
            Assert.True(Rejected(dir, Zip(("tfpack.json", M("{\"format\":\"tfpack\",\"version\":99999999999999999999999}")), ("pack.json", PackJson()))).Contains("newer"), "bigint");
            Rejected(dir, Zip(("tfpack.json", M("{\"format\":{\"x\":1},\"version\":1}")), ("pack.json", PackJson())));
            Rejected(dir, Zip(("tfpack.json", M("[1]")), ("pack.json", PackJson())));
            Rejected(dir, Zip(("pack.json", M("{\"format\":\"palace-builder\",\"version\":2147483648}"))));
            Rejected(dir, Zip(("pack.json", M("{\"format\":{},\"version\":1}"))));
        }

        public static void ImportNeverThrowsOnHostileFiles()
        {
            using var dir = new TempDir();
            var archive = new PackArchive(() => new FakeRepository(dir.Path));
            string file = Path.Combine(dir.Path, "bad.tfpack");
            File.WriteAllBytes(file, Zip(("tfpack.json", Encoding.UTF8.GetBytes("{\"format\":\"tfpack\",\"version\":2147483648}")), ("pack.json", PackJson())));
            var issues = new List<ValidationIssue>();
            Assert.Null(archive.Import(file, issues), "import");
            Assert.True(issues.Any(i => i.Severity == Severity.Error), "error issue");
        }

        public static void ExportRefusesWhatImportWouldReject()
        {
            using var dir = new TempDir();
            var repo = new FakeRepository(Path.Combine(dir.Path, "root"));
            var archive = new PackArchive(() => repo);
            var entry = SampleFolder(dir.Path);
            // 999 assets + tfpack.json + pack.json = 1,001 entries, one more than import accepts.
            string assets = Path.Combine(entry.Folder, "assets");
            foreach (var f in Directory.GetFiles(assets, "*", SearchOption.AllDirectories)) File.Delete(f);
            for (int i = 0; i < PackArchive.MaxEntries - 1; i++) File.WriteAllBytes(Path.Combine(assets, $"{i}.png"), [1]);
            try
            {
                archive.Export(entry);
                throw new AssertException("export should have refused");
            }
            catch (InvalidOperationException e)
            {
                Assert.True(e.Message.Contains("more than"), e.Message);
            }
            Assert.Equal(0, FilesUnder(Path.Combine(repo.Root, "exports")).Count(), "no file left behind");
            // One fewer fits, and round-trips.
            File.Delete(Path.Combine(assets, "0.png"));
            Assert.NotNull(archive.Import(archive.Export(entry), new List<ValidationIssue>()), "998 assets round-trip");
        }

        public static void RejectsDecompressionBombEntries()
        {
            using var dir = new TempDir();
            var zeros = new byte[PackArchive.MaxEntryBytes + 1];
            var zip = Zip(("pack.json", PackJson()), ("assets/bomb.png", zeros));
            Assert.True(zip.Length < 1024 * 1024, "bomb archive is small");
            var (pack, issues, staging) = ExtractBytes(dir, zip);
            Assert.Null(pack, "pack");
            Assert.True(issues.Any(i => i.Message.Contains("too large")), "size message");
            Assert.Equal(0, FilesUnder(staging).Count(), "partial file removed");
        }

        public static void RejectsTooManyEntries()
        {
            using var dir = new TempDir();
            var entries = new List<(string, byte[])> { ("pack.json", PackJson()) };
            for (int i = 0; i < PackArchive.MaxEntries + 1; i++) entries.Add(($"assets/{i}.png", [1]));
            Assert.True(Rejected(dir, Zip(entries.ToArray())).Contains("more than"), "entry count");
        }

        public static void ImportRejectsOversizedArchiveFile()
        {
            using var dir = new TempDir();
            var archive = new PackArchive(() => new FakeRepository(dir.Path));
            string file = Path.Combine(dir.Path, "huge.tfpack");
            using (var fs = File.Create(file)) fs.SetLength(PackArchive.MaxArchiveBytes + 1);
            var issues = new List<ValidationIssue>();
            Assert.Null(archive.Import(file, issues), "import");
            Assert.True(issues[0].Message.Contains("larger than"), issues[0].Message);
            Assert.Null(archive.Import(Path.Combine(dir.Path, "missing.tfpack"), issues), "missing file");
        }

        public static void ThunderstoreNames()
        {
            Assert.Equal("Sample_Goblin_Night", PackArchive.ThunderstoreName("Sample: Goblin Night"), "punctuation");
            Assert.Equal("PalaceBuilderPack", PackArchive.ThunderstoreName("!!!"), "empty");
            Assert.Equal("ber_Pack", PackArchive.ThunderstoreName("Über Pack"), "non-ASCII");
        }
    }
}
