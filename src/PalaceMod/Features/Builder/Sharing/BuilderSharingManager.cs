using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using PalaceMod.Builder;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Sharing;
using UnityEngine;

/*
    WP6: sharing designs (docs/builder-design.md). Installs the share codec and the .tfpack archive service, watches
    PalaceBuilder/import for dropped files, and registers the `builder-share` dev command. It follows the Builder
    feature's Enabled switch (Feature<BuilderManager>) rather than having its own. The UI is SharingUI, opened from
    the editor (WP4a).
*/
namespace PalaceMod
{
    public sealed class BuilderSharingManager : BaseManager
    {
        private const float PollSeconds = 2f;

        internal static ConfigEntry<bool> WatchImportFolder;
        private float nextPoll;
        private readonly HashSet<string> warnedLocked = new(StringComparer.Ordinal);

        internal static bool On => Feature<BuilderManager>.On;

        /// <summary>PalaceBuilder/import: drop .tfpack, .zip or share-code .txt files here to import them.</summary>
        internal static string ImportFolder => BuilderServices.Packs.Root == null ? null : Path.Combine(BuilderServices.Packs.Root, "import");

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            BuilderServices.Share = new ShareCodec();
            BuilderServices.Archives = new PackArchive(() => BuilderServices.Packs, SharingUI.MakeThunderstoreIcon);
            WatchImportFolder = plugin.Config.Bind("Builder", "Import folder", true, new ConfigDescription(
                "Automatically import .tfpack files and share codes (.txt) dropped into BepInEx/PalaceBuilder/import."));
            RegisterDevCommands();
        }

        public override void Update()
        {
            if (!On || !WatchImportFolder.Value || BuilderServices.InSession) return;
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + PollSeconds;
            // Wait for the first pack scan (the built-in sample is always there) so id clashes are detected.
            if (BuilderServices.Packs.Packs.Count == 0) return;
            string folder = ImportFolder;
            if (folder == null) return;
            try
            {
                Directory.CreateDirectory(folder);
                var next = PendingImports().FirstOrDefault();
                if (next != null) ImportDropped(next);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Plugin.Logger.LogWarning($"Builder sharing: can't read {folder}: {e.Message}");
                nextPoll = Time.unscaledTime + 30f;
            }
        }

        /// <summary>Files in the import folder waiting to be imported, oldest first.</summary>
        internal static List<string> PendingImports()
        {
            string folder = ImportFolder;
            if (folder == null || !Directory.Exists(folder)) return [];
            return Directory.GetFiles(folder)
                .Where(IsImportable)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();
        }

        private static bool IsImportable(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == PackArchive.Extension || ext == ".zip" || ext == ".txt";
        }

        /// <summary>Imports one dropped file and moves it to import/imported or import/failed.</summary>
        internal void ImportDropped(string path)
        {
            // Skip files still being written (a browser download or a copy in progress).
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(1.5)) return;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            }
            catch (IOException)
            {
                if (warnedLocked.Add(path)) Plugin.Logger.LogInfo($"Builder sharing: {Path.GetFileName(path)} is in use; retrying");
                return;
            }

            var issues = new List<ValidationIssue>();
            PackEntry entry = SharingUI.ImportFile(path, issues);
            string dest = Path.Combine(ImportFolder, entry != null ? "imported" : "failed");
            Directory.CreateDirectory(dest);
            string moved = PackArchive.UniquePath(dest, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
            File.Move(path, moved);
            if (entry == null)
                File.WriteAllText(moved + ".why.txt", string.Join(Environment.NewLine, issues.Select(i => i.ToString())));
        }

        // ------------------------------------------------------------------ dev commands

        private void RegisterDevCommands()
        {
            DevCommands.Register("builder-share",
                "builder-share code <pack>|decode [code]|paste|export <pack>|import <path>|thunderstore <pack>|ui [pack]|selftest   Builder share codes and .tfpack files",
                args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: builder-share code|decode|paste|export|import|thunderstore|ui|selftest");
                var issues = new List<ValidationIssue>();
                switch (args[0].ToLowerInvariant())
                {
                    case "code":
                    {
                        var entry = FindPack(args);
                        string code = BuilderServices.Share.Encode(entry.Pack);
                        GUIUtility.systemCopyBuffer = code;
                        return $"{code.Length} chars (copied to clipboard)\n{code}";
                    }
                    case "decode":
                    {
                        string code = args.Length > 1 ? string.Join(" ", args.Skip(1)) : GUIUtility.systemCopyBuffer;
                        var pack = BuilderServices.Share.Decode(code, issues);
                        if (pack == null) throw new InvalidOperationException(string.Join("; ", issues));
                        issues.AddRange(BuilderServices.Packs.Validate(pack));
                        return $"{pack.Id} \"{pack.Name}\": {pack.Units?.Count ?? 0} units, {pack.WaveSets?.Count ?? 0} wave sets" + Describe(issues);
                    }
                    case "paste":
                    {
                        var entry = SharingUI.ImportCode(GUIUtility.systemCopyBuffer, issues) ?? throw new InvalidOperationException(string.Join("; ", issues));
                        return $"installed {entry.Id} at {entry.Folder}" + Describe(issues);
                    }
                    case "export":
                        return "wrote " + BuilderServices.Archives.Export(FindPack(args));
                    case "thunderstore":
                        return "wrote " + BuilderServices.Archives.ExportThunderstore(FindPack(args));
                    case "import":
                    {
                        if (args.Length < 2) throw new ArgumentException("needs a file path");
                        var entry = SharingUI.ImportFile(string.Join(" ", args.Skip(1)), issues) ?? throw new InvalidOperationException(string.Join("; ", issues));
                        return $"installed {entry.Id} at {entry.Folder}" + Describe(issues);
                    }
                    case "ui":
                        if (args.Length > 1) SharingUI.OpenPack(FindPack(args));
                        else SharingUI.Open();
                        return "opened";
                    case "selftest":
                        return SelfTest();
                    default:
                        throw new ArgumentException($"unknown subcommand \"{args[0]}\"");
                }
            });
        }

        private static string Describe(List<ValidationIssue> issues) =>
            issues.Count == 0 ? "" : "\n" + string.Join("\n", issues.Select(i => "  " + i));

        private static PackEntry FindPack(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("needs a pack id (`builder packs` lists them)");
            var entry = BuilderServices.Packs.Find(args[1]) ?? throw new ArgumentException($"no pack \"{args[1]}\"");
            if (entry.Pack == null) throw new InvalidOperationException($"{entry.Id} couldn't be read");
            return entry;
        }

        /// <summary>In-game checks of the codec under the game's Mono runtime (compression, clipboard).</summary>
        private static string SelfTest()
        {
            var lines = new List<string>();
            void Check(bool ok, string what) { lines.Add((ok ? "ok   " : "FAIL ") + what); }

            var sample = BuilderServices.Packs.Packs.FirstOrDefault(p => p.Pack != null)?.Pack ?? throw new InvalidOperationException("no packs loaded");
            var codec = BuilderServices.Share;
            string code = codec.Encode(sample);
            var back = codec.Decode(code, null);
            Check(back != null && PackSerializer.WriteCompact(back) == PackSerializer.WriteCompact(ShareCodec.ForSharing(sample)), $"round trip of {sample.Id} ({code.Length} chars)");
            Check(codec.Decode(code.Substring(0, code.Length - 10), null) == null, "truncated code rejected");
            Check(codec.Decode("TFB1:" + new string('A', 100), null) == null, "garbage code rejected");
            Check(codec.Decode("TFB9:abcdefgh", null) == null, "newer version rejected");
            string wrapped = "here you go: `" + string.Join("\n", Enumerable.Range(0, (code.Length + 79) / 80).Select(i => code.Substring(i * 80, Math.Min(80, code.Length - i * 80)))) + "` enjoy";
            Check(codec.Decode(wrapped, null) != null, "code wrapped in chat text accepted");
            string old = GUIUtility.systemCopyBuffer;
            GUIUtility.systemCopyBuffer = code;
            Check(GUIUtility.systemCopyBuffer == code, "clipboard round trip");
            GUIUtility.systemCopyBuffer = old;
            return "\n" + string.Join("\n", lines);
        }
    }
}
