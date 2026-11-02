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
          packs/<packId>/pack.json         one pack per folder; folder name = pack id
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
            lastFingerprint = Fingerprint();
            var found = new List<PackEntry>();
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

        public PackEntry InstallNew(DesignPack pack, string sourceFolder = null)
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
