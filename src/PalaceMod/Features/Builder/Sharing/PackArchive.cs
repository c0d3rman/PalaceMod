using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

/*
    .tfpack files (docs/builder-design.md, "WP6: sharing"): a zip holding

        tfpack.json        {"format":"tfpack","version":1,"pack":"<id>"}   archive layout version (migration hook)
        pack.json          the design pack (its own schema version is migrated by PackSerializer)
        assets/...         files the pack's "assets" entries point at

    Import treats the zip as hostile: it caps the archive size, entry count, per-entry and total unpacked bytes
    (counted while inflating, never trusting the headers), rejects any entry path that is rooted, has a drive letter,
    "..", control characters or would land outside the staging folder (zip-slip), only unpacks pack.json and
    assets/ files with known media extensions, and then installs through IPackRepository.InstallNew, which never
    overwrites. pack.json may sit under a folder (a zip of the pack folder, or a Thunderstore package's
    PalaceBuilder/packs/<id>/), as long as the archive holds exactly one.

    No Unity references here (the Thunderstore icon comes in as PNG bytes), so tests can run it without the game.
*/
namespace PalaceMod.Builder.Sharing
{
    public sealed class PackArchive : IPackArchive
    {
        public const string Extension = ".tfpack";
        public const string PackFile = "pack.json"; // = PackRepository.PackFile (not referenced, so tests needn't load BepInEx)
        public const string ManifestFile = "tfpack.json";
        public const string ManifestFormat = "tfpack";
        public const int CurrentArchiveVersion = 1;

        public const long MaxArchiveBytes = 64L * 1024 * 1024;
        public const int MaxEntries = 1000;
        public const long MaxEntryBytes = 32L * 1024 * 1024;
        public const long MaxTotalBytes = 128L * 1024 * 1024;
        public const int MaxPackJsonBytes = 2 * 1024 * 1024;

        /// <summary>Asset file types an archive may carry (images, sounds, models). Anything else is skipped.</summary>
        public static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".wav", ".ogg", ".mp3", ".obj", ".mtl", ".gltf", ".glb", ".bin", ".json", ".txt",
        };

        /// <summary>
        /// Archive layout migrations: Steps[n] upgrades an extracted staging folder from layout n to n + 1. Empty
        /// while there is only version 1. (Pack JSON migrations are separate, in Model/PackSerializer.cs.)
        /// </summary>
        internal static readonly Dictionary<int, Action<string>> Steps = new();

        private readonly Func<IPackRepository> repository;
        private readonly Func<string, byte[]> thunderstoreIcon;

        /// <param name="repository">Where imports are installed and whose Root holds exports/ and staging.</param>
        /// <param name="thunderstoreIcon">Makes a 256x256 PNG for a pack name (Thunderstore requires one); may be null.</param>
        public PackArchive(Func<IPackRepository> repository, Func<string, byte[]> thunderstoreIcon = null)
        {
            this.repository = repository;
            this.thunderstoreIcon = thunderstoreIcon;
        }

        private string Root => repository().Root ?? throw new InvalidOperationException("the pack folder isn't available");
        public string ExportFolder => repository().Root == null ? null : Path.Combine(repository().Root, "exports");

        // ------------------------------------------------------------------ export

        public string Export(PackEntry pack, string path = null)
        {
            if (pack?.Pack == null) throw new InvalidOperationException("that pack couldn't be read, so it can't be exported");
            path ??= UniquePath(ExportFolder, pack.Id, Extension);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            WriteToFile(path, zip => Write(zip, pack.Pack, pack.Folder, ""));
            return path;
        }

        public string ExportThunderstore(PackEntry pack, string path = null)
        {
            if (pack?.Pack == null) throw new InvalidOperationException("that pack couldn't be read, so it can't be exported");
            string name = ThunderstoreName(pack.Pack.Name ?? pack.Id);
            path ??= UniquePath(ExportFolder, name + "-thunderstore", ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            WriteToFile(path, zip =>
            {
                AddText(zip, "manifest.json", ThunderstoreManifest(pack.Pack, name));
                AddText(zip, "README.md", ThunderstoreReadme(pack.Pack));
                byte[] icon = thunderstoreIcon?.Invoke(pack.Pack.Name ?? pack.Id);
                if (icon != null) AddBytes(zip, "icon.png", icon);
                Write(zip, pack.Pack, pack.Folder, $"PalaceBuilder/packs/{pack.Id}/", manifest: false);
            });
            return path;
        }

        /// <summary>Adds pack.json (+ tfpack.json) and the pack folder's assets/ under prefix.</summary>
        public static void Write(ZipArchive zip, DesignPack pack, string packFolder, string prefix, bool manifest = true)
        {
            var copy = PackSerializer.Clone(pack) ?? throw new InvalidOperationException("the pack can't be serialised");
            copy.Format = DesignPack.FormatName;
            copy.Version = DesignPack.CurrentVersion;
            if (manifest)
                AddText(zip, prefix + ManifestFile, new JObject { ["format"] = ManifestFormat, ["version"] = CurrentArchiveVersion, ["pack"] = copy.Id }.ToString(Formatting.Indented));
            string json = PackSerializer.Write(copy);
            if (Encoding.UTF8.GetByteCount(json) > MaxPackJsonBytes) throw new InvalidOperationException("the pack is too large to export");
            AddText(zip, prefix + PackFile, json);

            string assets = packFolder == null ? null : Path.Combine(packFolder, "assets");
            if (assets == null || !Directory.Exists(assets)) return;
            long total = json.Length;
            int count = manifest ? 2 : 1; // WriteToFile re-checks the finished archive against every import limit
            foreach (var file in Directory.GetFiles(assets, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                if (!AssetExtensions.Contains(info.Extension)) continue;
                if (info.Length > MaxEntryBytes) throw new InvalidOperationException($"{info.Name} is larger than {MaxEntryBytes / (1024 * 1024)} MB");
                total += info.Length;
                if (++count > MaxEntries) throw new InvalidOperationException($"the pack would hold more than {MaxEntries} files");
                if (total > MaxTotalBytes) throw new InvalidOperationException("the pack's assets are too large to export");
                string rel = file.Substring(assets.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                var entry = zip.CreateEntry(prefix + "assets/" + rel, CompressionLevel.Optimal);
                using var src = File.OpenRead(file);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }

        // ------------------------------------------------------------------ import

        public PackEntry Import(string path, List<ValidationIssue> issues)
        {
            issues ??= [];
            string staging = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return Fail(issues, $"{Path.GetFileName(path)} doesn't exist");
                if (info.Length > MaxArchiveBytes) return Fail(issues, $"{info.Name} is larger than {MaxArchiveBytes / (1024 * 1024)} MB");

                staging = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                DesignPack pack;
                using (var stream = File.OpenRead(path))
                    pack = Extract(stream, staging, issues);
                if (pack == null) return null;

                var before = pack.Id;
                var entry = repository().InstallNew(pack, staging);
                if (entry == null) return Fail(issues, "the pack couldn't be installed");
                if (entry.Id != before)
                    issues.Add(new ValidationIssue(Severity.Warning, "id", $"you already have a pack \"{before}\", so this one was installed as \"{entry.Id}\""));
                return entry;
            }
            catch (Exception e) when (!(e is OutOfMemoryException || e is StackOverflowException))
            {
                // Hostile files must come back as an issue, never an exception (the import folder would retry forever).
                return Fail(issues, $"{Path.GetFileName(path)} couldn't be imported: {e.Message}");
            }
            finally
            {
                if (staging != null)
                {
                    try { Directory.Delete(staging, true); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>
        /// Safely unpacks an archive's pack.json and assets/ into staging (which must exist and be empty) and returns
        /// the parsed pack, or null with an error issue. Warnings list skipped entries.
        /// </summary>
        public static DesignPack Extract(Stream archive, string staging, List<ValidationIssue> issues)
        {
            issues ??= [];
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException) // Mono's DeflateStream throws IOException on corrupt data
            {
                return Reject(issues, "that file isn't a pack (it isn't a valid zip)");
            }
            using (zip)
            {
                IReadOnlyCollection<ZipArchiveEntry> entries;
                try
                {
                    entries = zip.Entries;
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException) // Mono's DeflateStream throws IOException on corrupt data
                {
                    return Reject(issues, "that pack file is damaged");
                }
                if (entries.Count > MaxEntries) return Reject(issues, $"that pack has more than {MaxEntries} files");

                // Normalise and check every name first; one bad path rejects the whole archive.
                var named = new List<(ZipArchiveEntry entry, string name)>();
                foreach (var e in entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (name.EndsWith("/")) continue; // directory
                    if (!IsSafeEntryName(name)) return Reject(issues, $"that pack contains an unsafe file path (\"{Truncate(e.FullName)}\"); refusing it");
                    named.Add((e, name));
                }

                var packFiles = named.Where(n => n.name == PackFile || n.name.EndsWith("/" + PackFile)).ToList();
                if (packFiles.Count == 0) return Reject(issues, "that file has no pack.json, so it isn't a design pack");
                if (packFiles.Count > 1) return Reject(issues, "that file holds more than one pack.json; import them one at a time");
                string prefix = packFiles[0].name.Substring(0, packFiles[0].name.Length - PackFile.Length);

                // Archive layout version (absent in hand-made zips: treated as version 1).
                int layout = 1;
                var manifest = named.FirstOrDefault(n => n.name == prefix + ManifestFile).entry;
                if (manifest != null)
                {
                    byte[] bytes = ReadLimited(manifest, 64 * 1024, out string err);
                    if (bytes == null) return Reject(issues, err);
                    try
                    {
                        var m = JObject.Parse(Encoding.UTF8.GetString(bytes));
                        if ((string)m["format"] != ManifestFormat) return Reject(issues, "that file's tfpack.json isn't a pack manifest");
                        layout = m["version"]?.Type == JTokenType.Integer ? (int)m["version"] : 0;
                    }
                    catch (OverflowException)
                    {
                        layout = int.MaxValue; // an absurd version: refused as "newer" below
                    }
                    catch (Exception e) when (e is JsonException || e is ArgumentException || e is InvalidCastException)
                    {
                        return Reject(issues, "that file's tfpack.json is damaged");
                    }
                    if (layout > CurrentArchiveVersion) return Reject(issues, "that pack file was made with a newer PalaceMod; update PalaceMod to import it");
                    if (layout < 1) return Reject(issues, "that pack file's tfpack.json has no valid version");
                }

                string stagingFull = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                long total = 0;
                string packJson = null;
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int skipped = 0;
                foreach (var (entry, name) in named)
                {
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) { skipped++; continue; }
                    string rel = name.Substring(prefix.Length);
                    if (rel == ManifestFile) continue;

                    long cap = rel == PackFile ? MaxPackJsonBytes : Math.Min(MaxEntryBytes, MaxTotalBytes - total);
                    if (rel == PackFile)
                    {
                        byte[] bytes = ReadLimited(entry, cap, out string err);
                        if (bytes == null) return Reject(issues, err);
                        total += bytes.Length;
                        try
                        {
                            packJson = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
                        }
                        catch (ArgumentException)
                        {
                            return Reject(issues, "that pack's pack.json isn't text");
                        }
                        continue;
                    }
                    if (!rel.StartsWith("assets/", StringComparison.Ordinal) || !AssetExtensions.Contains(Path.GetExtension(rel)))
                    {
                        skipped++;
                        continue;
                    }
                    if (!written.Add(rel)) { skipped++; continue; } // duplicate (or case-only duplicate) entry

                    string target = Path.GetFullPath(Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(stagingFull, StringComparison.Ordinal))
                        return Reject(issues, $"that pack contains an unsafe file path (\"{Truncate(name)}\"); refusing it");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    long n = CopyLimited(entry, target, cap, out string copyErr);
                    if (n < 0) return Reject(issues, copyErr ?? "that pack is too large to import");
                    total += n;
                }
                if (packJson == null) return Reject(issues, "that pack's pack.json couldn't be read");
                if (skipped > 0) issues.Add(new ValidationIssue(Severity.Warning, "", $"skipped {skipped} file(s) that aren't part of the pack"));

                for (int v = layout; v < CurrentArchiveVersion; v++)
                {
                    if (!Steps.TryGetValue(v, out var step)) return Reject(issues, $"no upgrade for pack files of version {v}");
                    step(staging);
                }

                return SafeJson.ReadPack(packJson, issues);
            }
        }

        /// <summary>Relative, forward-slash path with no "..", ".", empty segments, drive letters or control characters.</summary>
        public static bool IsSafeEntryName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 260) return false;
            if (name[0] == '/' || name.Contains(':')) return false;
            foreach (char c in name)
                if (c < 0x20 || c == 0x7f || c == '\\' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
            foreach (var seg in name.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") return false;
                if (seg.EndsWith(".") || seg.EndsWith(" ")) return false; // Windows strips these: "a.." aliases "a"
            }
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static PackEntry Fail(List<ValidationIssue> issues, string message)
        {
            issues.Add(new ValidationIssue(Severity.Error, "", message));
            return null;
        }

        private static DesignPack Reject(List<ValidationIssue> issues, string message)
        {
            issues.Add(new ValidationIssue(Severity.Error, "", message));
            return null;
        }

        private static string Truncate(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";

        private static byte[] ReadLimited(ZipArchiveEntry entry, long cap, out string error)
        {
            error = null;
            try
            {
                using var s = entry.Open();
                var bytes = LimitedRead.ReadAll(s, cap);
                if (bytes == null) error = $"{entry.Name} in that pack is too large";
                return bytes;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException) // Mono's DeflateStream throws IOException on corrupt data
            {
                error = $"{entry.Name} in that pack is damaged";
                return null;
            }
        }

        /// <summary>Copies an entry to a new file, or returns -1 (and deletes the partial file) past cap bytes.</summary>
        private static long CopyLimited(ZipArchiveEntry entry, string target, long cap, out string error)
        {
            error = null;
            long n = 0;
            bool ok = false;
            try
            {
                using var src = entry.Open();
                using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    n += read;
                    if (n > cap) { error = $"{entry.Name} in that pack is too large"; return -1; }
                    dst.Write(buffer, 0, read);
                }
                ok = true;
                return n;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException) // Mono's DeflateStream throws IOException on corrupt data
            {
                error = $"{entry.Name} in that pack is damaged";
                return -1;
            }
            finally
            {
                if (!ok) try { File.Delete(target); } catch (IOException) { }
            }
        }

        private static void WriteToFile(string path, Action<ZipArchive> fill)
        {
            string tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    fill(zip);
                // What we share must import again: check the finished file against the importer's limits.
                if (new FileInfo(tmp).Length > MaxArchiveBytes) throw new InvalidOperationException($"the pack file would be larger than {MaxArchiveBytes / (1024 * 1024)} MB");
                using (var check = new ZipArchive(File.OpenRead(tmp), ZipArchiveMode.Read))
                    if (check.Entries.Count > MaxEntries) throw new InvalidOperationException($"the pack would hold more than {MaxEntries} files");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        private static void AddText(ZipArchive zip, string name, string text) => AddBytes(zip, name, new UTF8Encoding(false).GetBytes(text));

        private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(bytes, 0, bytes.Length);
        }

        internal static string UniquePath(string folder, string stem, string extension)
        {
            if (folder == null) throw new InvalidOperationException("the pack folder isn't available");
            string path = Path.Combine(folder, stem + extension);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}-{n}{extension}");
            return path;
        }

        // ------------------------------------------------------------------ Thunderstore

        /// <summary>Thunderstore package names allow only A-Z, a-z, 0-9 and '_'.</summary>
        public static string ThunderstoreName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
                sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
            string s = sb.ToString().Trim('_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            if (s.Length > 64) s = s.Substring(0, 64);
            return s.Length == 0 ? "PalaceBuilderPack" : s;
        }

        internal static string ThunderstoreManifest(DesignPack pack, string name)
        {
            string desc = string.IsNullOrWhiteSpace(pack.Description) ? $"A PalaceMod Builder pack: {pack.Name ?? pack.Id}" : pack.Description.Trim();
            if (desc.Length > 250) desc = desc.Substring(0, 247) + "...";
            return new JObject
            {
                ["name"] = name,
                ["version_number"] = "1.0.0",
                ["website_url"] = "",
                ["description"] = desc,
                // Add PalaceMod's Thunderstore id here once it's published (e.g. "<Team>-PalaceMod-1.0.0").
                ["dependencies"] = new JArray(),
            }.ToString(Formatting.Indented);
        }

        internal static string ThunderstoreReadme(DesignPack pack)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(pack.Name ?? pack.Id).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(pack.Description)) sb.Append(pack.Description.Trim()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(pack.Author)) sb.Append("By ").Append(pack.Author.Trim()).Append(".\n\n");
            sb.Append("A design pack for the PalaceMod Builder (Thronefall). Requires PalaceMod.\n\n");
            if (pack.Units?.Count > 0)
            {
                sb.Append("## Units\n\n");
                foreach (var u in pack.Units) sb.Append("- ").Append(u.Name ?? u.Id).Append('\n');
                sb.Append('\n');
            }
            if (pack.WaveSets?.Count > 0)
            {
                sb.Append("## Wave sets\n\n");
                foreach (var w in pack.WaveSets) sb.Append("- ").Append(w.Name ?? w.Id).Append(" (").Append(w.Host).Append(")\n");
                sb.Append('\n');
            }
            sb.Append("## Publishing checklist\n\n")
              .Append("1. Edit `manifest.json`: set `version_number`, `website_url` and add PalaceMod to `dependencies`.\n")
              .Append("2. Replace `icon.png` with your own 256x256 image if you like.\n")
              .Append("3. Upload this zip at https://thunderstore.io/c/thronefall/create/.\n\n")
              .Append("Installed by a mod manager, the pack lands in `BepInEx/plugins/<package>/PalaceBuilder/packs/").Append(pack.Id).Append("/` and shows up in the Builder as read-only.\n");
            return sb.ToString();
        }
    }
}
