using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using PalaceMod.Builder.Model;

/*
    Packs folder layout (docs/builder-design.md, "Packs on disk"):

        BepInEx/PalaceBuilder/
          library/...                      the player's levels and enemies, one file each (Model/LibraryStore.cs);
                                           listed as the pack "library"
          packs/<packId>/pack.json         installed packs, one per folder; folder name = pack id. Packs here before
                                           the library existed are moved into it once (then kept in migrated-packs/)
          packs/<packId>/assets/...        files the pack's "assets" point at
          trash/<packId>-<timestamp>/      deleted packs (never removed outright)
          cache/<sha256>                   downloaded assets (WP5)
          scores.json                      per-design results (WP3)
          catalog.json                     written by `builder dump`
        BepInEx/plugins/<mod>/PalaceBuilder/packs/<packId>/   read-only packs shipped by Thunderstore mods
        PalaceMod.dll embedded resources PalaceMod.Assets.builder-*.json   read-only built-in samples
*/
namespace PalaceMod.Builder
{
    public sealed class PackRepository : IPackRepository
    {
        public const string PackFile = "pack.json";
        private const string BuiltInPrefix = "PalaceMod.Assets.builder-";

        public string Root { get; }
        public string PacksDir => Path.Combine(Root, "packs");
        public string LibraryDir => Path.Combine(Root, "library");
        private const string MigratedMarker = ".migrated";

        /// <summary>The player's design library (levels and enemies); listed as the pack LibraryStore.PackId.</summary>
        public LibraryStore Library { get; }

        private List<PackEntry> packs = [];
        public IReadOnlyList<PackEntry> Packs => packs;
        public event Action Changed;

        private readonly Func<IValidationContext> context;
        private string lastFingerprint = "";

        /// <param name="context">The live catalog for validation, or null before it exists.</param>
        public PackRepository(Func<IValidationContext> context, string root = null)
        {
            this.context = context;
            Root = root ?? Path.Combine(Paths.BepInExRootPath, "PalaceBuilder");
            Library = new LibraryStore(LibraryDir);
            // Rules may use library presets by id (Model/Logic/Presets.cs).
            PalaceMod.Builder.Model.Logic.Presets.Library = id => Library.Preset(id);
        }

        public PackEntry Find(string packId) => packs.FirstOrDefault(p => p.Pack != null && p.Pack.Id == packId);

        public List<ValidationIssue> Validate(DesignPack pack) => Validator.Validate(pack, context());

        /// <summary>Rescans if any pack.json changed since the last scan (cheap: file times only).</summary>
        public void RefreshIfChanged()
        {
            if (Fingerprint() != lastFingerprint) Refresh();
        }

        public void Refresh()
        {
            MigrateOnce();
            lastFingerprint = Fingerprint();
            var found = new List<PackEntry>();
            Library.Load();
            found.Add(LibraryEntry());
            foreach (var (name, json) in BuiltInPacks())
                found.Add(Load(json, null, PackSource.BuiltIn, name));
            foreach (var folder in PluginPackFolders())
                found.Add(LoadFolder(folder, PackSource.Plugin));
            if (Directory.Exists(PacksDir))
                foreach (var folder in Directory.GetDirectories(PacksDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    found.Add(LoadFolder(folder, PackSource.User));

            // Duplicate ids: the first one (built-in, then plugin, then user) wins; later ones are flagged.
            var seen = new HashSet<string>();
            foreach (var entry in found)
            {
                if (entry.Pack == null) continue;
                if (!seen.Add(entry.Pack.Id))
                    entry.Issues = entry.Issues.Append(new ValidationIssue(Severity.Error, "id", $"another pack already uses the id \"{entry.Pack.Id}\"")).ToList();
                else if (entry.Source != PackSource.BuiltIn && entry.Folder != null && Path.GetFileName(entry.Folder) != entry.Pack.Id)
                    entry.Issues = entry.Issues.Append(new ValidationIssue(Severity.Warning, "id", $"folder name \"{Path.GetFileName(entry.Folder)}\" differs from the pack id")).ToList();
            }
            packs = found;
            foreach (var e in packs.Where(e => e.Issues.Count > 0))
                Log.For<BuilderManager>().Info($"Pack {e.Id} ({e.Source}): {string.Join("; ", e.Issues)}");
            Log.For<BuilderManager>().Info($"{packs.Count} pack(s) loaded ({packs.Count(p => !p.HasErrors)} playable)");
            Changed?.Invoke();
        }

        /// <summary>The library as a pack entry (its folder is the library root, so its assets resolve).</summary>
        private PackEntry LibraryEntry()
        {
            var pack = Library.Compose();
            var issues = new List<ValidationIssue>(Library.Issues);
            issues.AddRange(Validate(pack));
            return new PackEntry { Pack = pack, Folder = LibraryDir, Source = PackSource.User, Issues = issues };
        }

        /// <summary>Re-reads the library alone (after a library write) and raises Changed.</summary>
        public void RefreshLibrary()
        {
            Library.Load();
            int i = packs.FindIndex(p => p.Pack?.Id == LibraryStore.PackId);
            var entry = LibraryEntry();
            if (i >= 0) packs[i] = entry; else packs.Insert(0, entry);
            lastFingerprint = Fingerprint();
            Changed?.Invoke();
        }

        // The packs the player had before the library existed (their own and installed demo packs) move into it once;
        // each folder is kept in migrated-packs/. Later packs in packs/ stay installed packs (and can be imported).
        private void MigrateOnce()
        {
            string marker = Path.Combine(LibraryDir, MigratedMarker);
            if (File.Exists(marker)) return;
            try
            {
                Library.Load();
                var log = Library.MigratePacks(PacksDir, Path.Combine(Root, "migrated-packs"));
                Directory.CreateDirectory(LibraryDir);
                File.WriteAllText(marker, $"Packs moved into the library on {DateTime.Now:yyyy-MM-dd HH:mm}:\n" + string.Join("\n", log) + "\n");
                foreach (var line in log) Log.For<BuilderManager>().Info("Library migration: " + line);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Log.For<BuilderManager>().Error("Moving packs into the library failed; they stay where they are", e);
            }
        }

        /// <summary>Re-validates loaded packs (e.g. once the catalog is available) without rereading files.</summary>
        public void Revalidate()
        {
            foreach (var e in packs.Where(e => e.Pack != null))
                e.Issues = Validate(e.Pack);
            Changed?.Invoke();
        }

        public string Save(DesignPack pack)
        {
            if (!Ids.IsValid(pack.Id)) throw new ArgumentException($"invalid pack id \"{pack.Id}\"");
            if (pack.Id == LibraryStore.PackId) throw new InvalidOperationException("the library is saved through its workspaces (PackDraft.Save)");
            var existing = Find(pack.Id);
            if (existing != null && existing.ReadOnly) throw new InvalidOperationException($"pack \"{pack.Id}\" is read-only ({existing.Source}); save a copy under a new id");
            string folder = Path.Combine(PacksDir, pack.Id);
            Directory.CreateDirectory(folder);
            pack.Format = DesignPack.FormatName;
            pack.Version = DesignPack.CurrentVersion;
            string path = Path.Combine(folder, PackFile);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, PackSerializer.Write(pack));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            Refresh();
            return path;
        }

        /// <summary>Imports a received pack into the library (its levels and enemies become library items). Returns an
        /// entry for the pack as imported (ids after collision handling), whose folder is the library's.</summary>
        public PackEntry InstallNew(DesignPack pack, string sourceFolder = null) => Import(pack, sourceFolder, out _);

        public PackEntry Import(DesignPack pack, string sourceFolder, out LibraryImportResult result)
        {
            Library.Load();
            result = Library.Import(pack, sourceFolder);
            RefreshLibrary();
            var imported = Library.Compose();
            var levelIds = new HashSet<string>(result.Levels);
            var unitIds = new HashSet<string>(result.Units.Concat(result.Reused));
            imported.Levels = imported.Levels.Where(l => levelIds.Contains(l.Id)).ToList();
            imported.Units = imported.Units.Where(u => unitIds.Contains(u.Id)).ToList();
            imported.WaveSets = imported.WaveSets.Where(w => imported.Levels.Any(l => l.WaveSet == w.Id)).ToList();
            imported.Name = pack.Name;
            imported.Author = pack.Author;
            return new PackEntry { Pack = imported, Folder = LibraryDir, Source = PackSource.User, Issues = result.Issues };
        }

        /// <summary>Installs a pack as its own folder under packs/ (the pre-library behaviour: dev and test packs).</summary>
        public PackEntry InstallPack(DesignPack pack, string sourceFolder = null)
        {
            pack = PackSerializer.Clone(pack) ?? throw new ArgumentException("pack can't be serialised");
            string baseId = Ids.IsValid(pack.Id) ? pack.Id : Ids.FromName(pack.Name);
            string id = baseId;
            for (int n = 2; Find(id) != null || Directory.Exists(Path.Combine(PacksDir, id)); n++)
                id = $"{baseId}-{n}";
            pack.Id = id;
            string folder = Path.Combine(PacksDir, id);
            Directory.CreateDirectory(folder);
            if (sourceFolder != null && Directory.Exists(Path.Combine(sourceFolder, "assets")))
                CopyDirectory(Path.Combine(sourceFolder, "assets"), Path.Combine(folder, "assets"));
            Save(pack);
            return Find(id);
        }

        public bool Delete(string packId)
        {
            var entry = packs.FirstOrDefault(p => p.Id == packId && p.Source == PackSource.User);
            if (entry?.Folder == null || !Directory.Exists(entry.Folder)) return false;
            string trash = Path.Combine(Root, "trash");
            Directory.CreateDirectory(trash);
            Directory.Move(entry.Folder, Path.Combine(trash, $"{Path.GetFileName(entry.Folder)}-{DateTime.Now:yyyyMMdd-HHmmss}"));
            Refresh();
            return true;
        }

        private PackEntry LoadFolder(string folder, PackSource source)
        {
            string file = Path.Combine(folder, PackFile);
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new PackEntry { Folder = folder, Source = source, Issues = [new ValidationIssue(Severity.Error, PackFile, "can't be read: " + e.Message)] };
            }
            return Load(json, folder, source, Path.GetFileName(folder));
        }

        private PackEntry Load(string json, string folder, PackSource source, string label)
        {
            var issues = new List<ValidationIssue>();
            var pack = PackSerializer.Read(json, issues);
            if (pack != null) issues.AddRange(Validate(pack));
            return new PackEntry { Pack = pack, Folder = folder, Source = source, Issues = issues };
        }

        private static IEnumerable<(string name, string json)> BuiltInPacks()
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(BuiltInPrefix) && n.EndsWith(".json")).OrderBy(n => n))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                yield return (name, reader.ReadToEnd());
            }
        }

        private static IEnumerable<string> PluginPackFolders()
        {
            if (!Directory.Exists(Paths.PluginPath)) yield break;
            foreach (var mod in Directory.GetDirectories(Paths.PluginPath))
            {
                string dir = Path.Combine(mod, "PalaceBuilder", "packs");
                if (!Directory.Exists(dir)) continue;
                foreach (var folder in Directory.GetDirectories(dir)) yield return folder;
            }
        }

        private string Fingerprint()
        {
            var parts = new List<string>();
            foreach (var dir in new[] { Library.LevelsDir, Library.EnemiesDir, Library.LogicDir(LibraryStore.LogicKind.Entity),
                                        Library.LogicDir(LibraryStore.LogicKind.Status), Library.LogicDir(LibraryStore.LogicKind.Preset) })
                if (Directory.Exists(dir))
                    foreach (var file in Directory.GetFiles(dir, "*.json")) parts.Add(file + "|" + File.GetLastWriteTimeUtc(file).Ticks);
            string assetsFile = Path.Combine(LibraryDir, "assets.json");
            if (File.Exists(assetsFile)) parts.Add(assetsFile + "|" + File.GetLastWriteTimeUtc(assetsFile).Ticks);
            foreach (var folder in PluginPackFolders().Concat(Directory.Exists(PacksDir) ? Directory.GetDirectories(PacksDir) : []))
            {
                string file = Path.Combine(folder, PackFile);
                parts.Add(folder + "|" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
            foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
