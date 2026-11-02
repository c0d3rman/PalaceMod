using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The sharing screens, built on the UI kit. The editor (WP4a) opens them and calls the one-shot helpers:

        SharingUI.Open();                       // "Share & import": paste a code, the import folder, export a pack
        SharingUI.OpenPack(entry);              // one pack's export actions
        SharingUI.CopyCode(pack);               // share code to the clipboard, with a notice
        SharingUI.ImportFromClipboard();        // installs the pasted code as a new pack, with a notice

    Every action reports through Notify; nothing here throws to the caller.
*/
namespace PalaceMod.Builder.Sharing
{
    public static class SharingUI
    {
        private static ModFrame main;
        private static ModFrame packFrame;
        private static PackEntry selected;
        private static string lastResult;

        /// <summary>The "Share designs" menu: Import (paste code, import folder) and Export (pick a pack).</summary>
        public static void Open()
        {
            if (!Allowed()) return;
            main ??= new ModFrame("Share designs")
                .AddTab("Import", BuildImport)
                .AddTab("Export", BuildExport);
            main.Open();
        }

        /// <summary>Export actions for one pack (share code, .tfpack, Thunderstore).</summary>
        public static void OpenPack(PackEntry entry)
        {
            if (!Allowed() || entry == null) return;
            selected = entry;
            packFrame ??= new ModFrame("Share a pack").SetContent(BuildPack);
            if (packFrame.IsOpen) packFrame.Refresh();
            else packFrame.Open();
        }

        /// <summary>Puts the pack's share code on the clipboard. Returns the code, or null on failure.</summary>
        public static string CopyCode(DesignPack pack)
        {
            if (pack == null) return null;
            try
            {
                string code = BuilderServices.Share.Encode(pack);
                GUIUtility.systemCopyBuffer = code;
                string name = pack.Name ?? pack.Id;
                if (code.Length > ShareCodec.ChatFriendlyChars)
                    Notify.Show($"Copied the code for {name} ({code.Length:N0} characters: long for chat; a .tfpack file may be easier)", NotifyKind.Success, 5f, "builder-share");
                else
                    Notify.Show($"Copied the share code for {name} ({code.Length:N0} characters)", NotifyKind.Success, 4f, "builder-share");
                Plugin.Logger.LogInfo($"Builder sharing: copied code for {pack.Id} ({code.Length} chars)");
                return code;
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is NotSupportedException)
            {
                Report(null, [new ValidationIssue(Severity.Error, "", e.Message)], "Couldn't make a code");
                return null;
            }
        }

        /// <summary>Installs the share code on the clipboard as a new pack. Returns its entry, or null.</summary>
        public static PackEntry ImportFromClipboard()
        {
            var issues = new List<ValidationIssue>();
            var entry = ImportCode(GUIUtility.systemCopyBuffer, issues);
            Report(entry, issues, "Couldn't import the code");
            return entry;
        }

        /// <summary>Decodes a code and installs it as a new pack (never overwrites). Null and issues on failure.</summary>
        public static PackEntry ImportCode(string code, List<ValidationIssue> issues)
        {
            var pack = BuilderServices.Share.Decode(code, issues);
            if (pack == null) return null;
            return Install(() => BuilderServices.Packs.InstallNew(pack), pack.Id, issues);
        }

        /// <summary>Imports a .tfpack/.zip, or a .txt holding a share code. Null and issues on failure. Reports
        /// through Notify.</summary>
        public static PackEntry ImportFile(string path, List<ValidationIssue> issues = null)
        {
            issues ??= [];
            PackEntry entry;
            if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                string text;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > ShareCodec.MaxCodeChars * 2L) throw new IOException("the file is too large to be a share code");
                    text = File.ReadAllText(path);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    issues.Add(new ValidationIssue(Severity.Error, "", $"{Path.GetFileName(path)} can't be read: {e.Message}"));
                    text = null;
                }
                entry = text == null ? null : ImportCode(text, issues);
            }
            else
            {
                entry = BuilderServices.Archives.Import(path, issues);
            }
            Report(entry, issues, $"Couldn't import {Path.GetFileName(path)}");
            return entry;
        }

        // ------------------------------------------------------------------ pages

        private static void BuildImport(ModPage page)
        {
            page.Header("Share code");
            page.Text("Copy a code someone shared (it starts with TFB1:), then paste it here. It's added as a new pack; nothing of yours is replaced.");
            page.Button("Paste code from clipboard", () =>
            {
                var entry = ImportFromClipboard();
                lastResult = entry != null ? $"Imported {entry.Pack.Name ?? entry.Id}." : "The clipboard didn't hold a valid share code.";
                main?.Refresh();
            }, "Reads the clipboard and installs the design as a new pack.");
            if (lastResult != null) page.Text(lastResult);

            page.Header("Pack files");
            string folder = BuilderSharingManager.ImportFolder;
            bool watching = BuilderSharingManager.WatchImportFolder.Value;
            page.Text(watching
                ? "Drop .tfpack files (or .txt files holding a code) into the import folder and they're imported within a few seconds."
                : "Drop .tfpack files into the import folder, then pick them below. (Automatic import is off in Settings > Mods > Builder.)");
            page.Button("Open import folder", () => OpenFolder(folder), folder);
            foreach (var file in BuilderSharingManager.PendingImports())
            {
                string f = file;
                page.Row(Path.GetFileName(f), "Import", "Import this file as a new pack.", () =>
                {
                    ImportFile(f);
                    main?.Refresh();
                });
            }
        }

        private static void BuildExport(ModPage page)
        {
            page.Header("Pick a pack");
            var packs = BuilderServices.Packs.Packs.Where(p => p.Pack != null).ToList();
            if (packs.Count == 0)
            {
                page.Text("No packs yet. Make one in the Builder, or import one.");
                return;
            }
            foreach (var p in packs)
            {
                var entry = p;
                string source = entry.Source switch { PackSource.BuiltIn => "built-in", PackSource.Plugin => "mod", _ => "yours" };
                page.Row(entry.Pack.Name ?? entry.Id, source, () => Summary(entry), () => OpenPack(entry));
            }
        }

        private static void BuildPack(ModPage page)
        {
            var entry = selected == null ? null : BuilderServices.Packs.Find(selected.Id) ?? selected;
            if (entry?.Pack == null)
            {
                page.Text("This pack is no longer available.");
                return;
            }
            page.Header(entry.Pack.Name ?? entry.Id);
            page.Text(Summary(entry));
            page.Button("Copy share code", () => CopyCode(entry.Pack),
                "A short text code for chat. Holds the designs only; imported pictures and sounds stay behind.");
            page.Button("Save as .tfpack file", () => Export(entry, thunderstore: false),
                "One file with the designs and their pictures and sounds, saved to the exports folder.");
            page.Button("Save for Thunderstore", () => Export(entry, thunderstore: true),
                "A zip ready to upload to Thunderstore, with a manifest and README to fill in.");
            page.Button("Open exports folder", () => OpenFolder(BuilderServices.Archives.ExportFolder), BuilderServices.Archives.ExportFolder);
        }

        // ------------------------------------------------------------------ helpers

        private static bool Allowed()
        {
            if (BuilderSharingManager.On) return true;
            Notify.Show("The Builder is switched off (Settings > Mods > Builder)", NotifyKind.Warning, key: "builder-share");
            return false;
        }

        private static string Summary(PackEntry entry)
        {
            var p = entry.Pack;
            string by = string.IsNullOrWhiteSpace(p.Author) ? "" : $" by {p.Author}";
            int assets = p.Assets?.Count ?? 0;
            return $"{p.Units?.Count ?? 0} unit(s), {p.WaveSets?.Count ?? 0} wave set(s){(assets > 0 ? $", {assets} asset(s)" : "")}{by}. Id: {p.Id}";
        }

        private static void Export(PackEntry entry, bool thunderstore)
        {
            try
            {
                string path = thunderstore ? BuilderServices.Archives.ExportThunderstore(entry) : BuilderServices.Archives.Export(entry);
                Notify.Show($"Saved {Path.GetFileName(path)} to the exports folder", NotifyKind.Success, 4f, "builder-share");
                Plugin.Logger.LogInfo($"Builder sharing: exported {entry.Id} to {path}");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is ArgumentException)
            {
                Report(null, [new ValidationIssue(Severity.Error, "", e.Message)], "Couldn't save the pack");
            }
        }

        private static PackEntry Install(Func<PackEntry> install, string id, List<ValidationIssue> issues)
        {
            try
            {
                var entry = install();
                if (entry != null && entry.Id != id)
                    issues.Add(new ValidationIssue(Severity.Warning, "id", $"you already have a pack \"{id}\", so this one was installed as \"{entry.Id}\""));
                return entry;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is ArgumentException)
            {
                issues.Add(new ValidationIssue(Severity.Error, "", "couldn't save the pack: " + e.Message));
                return null;
            }
        }

        /// <summary>One notice for an import or export result; details go to the log.</summary>
        private static void Report(PackEntry entry, List<ValidationIssue> issues, string failure)
        {
            foreach (var i in issues) Plugin.Logger.LogInfo($"Builder sharing: {i}");
            if (entry == null)
            {
                string why = issues.FirstOrDefault(i => i.Severity == Severity.Error)?.Message ?? "unknown error";
                Notify.Show($"{failure}: {why}", NotifyKind.Error, 6f, "builder-share");
                return;
            }
            string name = entry.Pack?.Name ?? entry.Id;
            Plugin.Logger.LogInfo($"Builder sharing: installed {entry.Id} at {entry.Folder}");
            if (entry.HasErrors)
                Notify.Show($"Imported {name}, but it has problems to fix in the Builder before it can be played", NotifyKind.Warning, 6f, "builder-share");
            else
                Notify.Show($"Imported {name}", NotifyKind.Success, 4f, "builder-share");
        }

        private static void OpenFolder(string folder)
        {
            if (folder == null) return;
            try
            {
                Directory.CreateDirectory(folder);
                Application.OpenURL(new Uri(folder).AbsoluteUri);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is UriFormatException)
            {
                Notify.Show($"Couldn't open {folder}", NotifyKind.Error, key: "builder-share");
            }
        }

        /// <summary>A 256x256 PNG for a Thunderstore package: the game's panel colour, a gold frame and PalaceMod's
        /// verdigris diamond. Creators can replace it.</summary>
        internal static byte[] MakeThunderstoreIcon(string name)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[size * size];
                Color32 panel = ModStyle.Panel, gold = ModStyle.Gold, accent = ModStyle.Accent;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int edge = Math.Min(Math.Min(x, y), Math.Min(size - 1 - x, size - 1 - y));
                    bool frame = edge >= 10 && edge < 16;
                    bool diamond = Math.Abs(x - size / 2) + Math.Abs(y - size / 2) < 60;
                    pixels[y * size + x] = frame ? gold : diamond ? accent : panel;
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
