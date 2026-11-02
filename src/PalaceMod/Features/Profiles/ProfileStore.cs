using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

/*
    Storage for save profiles. Only this file touches the player's save files.

    The game keeps progression in the top-level files of Application.persistentDataPath (ThroneSave.sav and its
    timestamped copies, the per-map and Eternal Trials run .json files) plus a few PlayerPrefs (wardrobe and the
    Eternal Trials seed). That set is a profile's data; ControlsConfig_v2.json, steam_autocloud.vdf and the other
    PlayerPrefs are settings and stay shared. This mirrors what the game's own "Reset game progress" treats as
    progress.

    The active profile's data is always the live save directory, so the game, Steam Cloud and the game's own reset
    keep working unchanged. Inactive profiles are stored as snapshots next to the save directory (not inside it:
    Steam Auto-Cloud syncs *.sav and *.json there, and a profile folder must never be uploaded or synced back):

        Grizzly Games/Thronefall PalaceMod Profiles/
            profiles.json                  the list of profiles and which one is live
            journal.json                   present only while an operation that changes the live files is running
            profiles/<id>/                 a snapshot of an inactive profile (stale for the active one)
            backups/<time> <why> <name>/   a snapshot of the live data taken before every switch or reset, and
                                           every deleted profile; never pruned

    A snapshot is a folder with files/, prefs.json and manifest.json (name, size and SHA-256 of every file). It's
    written to a temporary folder, verified against its manifest and only then renamed into place, so a snapshot that
    exists is complete. Every change to the live data goes:

        1. back up the live data (verified) and write the journal: stage "backedup"
        2. replace the live files with the target snapshot (each file staged as *.palacemod-tmp, verified, renamed)
           and set the prefs
        3. journal stage "applied", then record the new active profile and delete the journal

    On startup, a leftover journal means the game stopped part-way: at "backedup" the live data is restored from the
    backup (as if the operation never happened), at "applied" the operation is finished.
*/
namespace PalaceMod.Profiles
{
    /// <summary>PlayerPrefs that belong to a profile. Abstracted so the store doesn't depend on PlayerPrefs directly.</summary>
    public interface IProfilePrefs
    {
        List<ProfileStore.PrefValue> Read();
        void Write(List<ProfileStore.PrefValue> values);
    }

    public sealed class ProfileStore
    {
        public const string DefaultId = "default";
        private const string TempSuffix = ".palacemod-tmp";

        /// <summary>Live files that are settings, not progression (the game's reset skips ControlsConfig too).</summary>
        private static readonly HashSet<string> SharedFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "ControlsConfig_v2.json", "steam_autocloud.vdf", ".DS_Store",
        };

        [Serializable] public class PrefValue { public string key; public bool present; public int value; }

        [Serializable] public class Profile { public string id; public string name; public string created; }

        [Serializable]
        public class Index
        {
            public int version = 1;
            public string active = DefaultId;
            public List<Profile> profiles = new();
        }

        [Serializable] public class ManifestFile { public string name; public long size; public string sha256; }

        [Serializable]
        public class Manifest
        {
            public string profile;
            public string taken;
            public List<ManifestFile> files = new();
        }

        [Serializable] private class PrefsFile { public List<PrefValue> prefs = new(); }

        [Serializable]
        private class Journal
        {
            public string op;       // "switch" | "reset"
            public string from;     // active profile when it started
            public string to;       // profile that becomes active ("reset": same as from)
            public string stage;    // "backedup" | "applied"
            public string backup;   // backup folder of the live data, relative to the root
            public string started;
        }

        public string LiveDir { get; }
        public string Root { get; }
        private readonly IProfilePrefs prefs;
        private readonly Action<string> log;
        private Index index;

        /// <summary>Test hook: the stage at which the next operation throws SimulatedCrash, as if the game died there.</summary>
        internal string CrashAt;
        internal sealed class SimulatedCrash : Exception { public SimulatedCrash(string stage) : base($"simulated crash at {stage}") { } }
        private void MaybeCrash(string stage) { if (CrashAt == stage) throw new SimulatedCrash(stage); }

        private string IndexPath => Path.Combine(Root, "profiles.json");
        private string JournalPath => Path.Combine(Root, "journal.json");
        private string ProfilesDir => Path.Combine(Root, "profiles");
        public string BackupsDir => Path.Combine(Root, "backups");
        private string ProfileDir(string id) => Path.Combine(ProfilesDir, id);

        public ProfileStore(string liveDir, string root, IProfilePrefs prefs, Action<string> log)
        {
            LiveDir = liveDir;
            Root = root;
            this.prefs = prefs;
            this.log = log ?? (_ => { });
        }

        /// <summary>Whether profiles have ever been used. Until then nothing has been written anywhere.</summary>
        public bool Initialized => File.Exists(IndexPath);

        public IReadOnlyList<Profile> Profiles => LoadIndex().profiles;

        public Profile Active
        {
            get
            {
                var idx = LoadIndex();
                return idx.profiles.FirstOrDefault(p => p.id == idx.active) ?? idx.profiles[0];
            }
        }

        public Profile Find(string idOrName)
        {
            var list = LoadIndex().profiles;
            return list.FirstOrDefault(p => p.id == idOrName)
                ?? list.FirstOrDefault(p => string.Equals(p.name, idOrName, StringComparison.OrdinalIgnoreCase));
        }

        // ---- Index -------------------------------------------------------------------------------------------------

        private Index LoadIndex()
        {
            if (index != null) return index;
            if (File.Exists(IndexPath))
            {
                var loaded = JsonUtility.FromJson<Index>(File.ReadAllText(IndexPath));
                if (loaded?.profiles == null || loaded.profiles.Count == 0 || loaded.profiles.All(p => p.id != loaded.active))
                    throw new InvalidDataException($"{IndexPath} is damaged; fix or delete it to start over (the live save is untouched)");
                return index = loaded;
            }
            // Not written until something changes: the live data simply is "Default".
            return index = new Index { profiles = { new Profile { id = DefaultId, name = "Default", created = Now() } } };
        }

        private void SaveIndex()
        {
            Directory.CreateDirectory(Root);
            WriteAllTextAtomic(IndexPath, JsonUtility.ToJson(index, true));
        }

        // ---- Operations -------------------------------------------------------------------------------------------

        /// <summary>Makes another profile live. The current live data is backed up and stored as its profile first.</summary>
        public void Switch(string toId)
        {
            RecoverIfNeeded();
            var idx = LoadIndex();
            var to = idx.profiles.FirstOrDefault(p => p.id == toId) ?? throw new ArgumentException($"no profile {toId}");
            if (to.id == idx.active) return;
            if (!SnapshotComplete(ProfileDir(to.id))) throw new InvalidDataException($"profile \"{to.name}\" has no stored data");
            VerifySnapshot(ProfileDir(to.id));
            string from = idx.active;

            string backup = BackupLive("switch", Active.name);
            WriteJournal(new Journal { op = "switch", from = from, to = to.id, stage = "backedup", backup = Rel(backup), started = Now() });
            // The backup is exactly the live data, so it doubles as the outgoing profile's stored copy.
            MaybeCrash("backedup");
            ReplaceSnapshot(ProfileDir(from), dir => CopySnapshot(backup, dir));
            ApplyToLive(ProfileDir(to.id));
            WriteJournal(new Journal { op = "switch", from = from, to = to.id, stage = "applied", backup = Rel(backup), started = Now() });
            MaybeCrash("applied");
            idx.active = to.id;
            SaveIndex();
            File.Delete(JournalPath);
            log($"Profiles: switched from \"{Name(from)}\" to \"{to.name}\" (backup: {Path.GetFileName(backup)})");
        }

        /// <summary>Clears the live profile's progression, after backing it up.</summary>
        public string ResetActive()
        {
            RecoverIfNeeded();
            var idx = LoadIndex();
            string backup = BackupLive("reset", Active.name);
            WriteJournal(new Journal { op = "reset", from = idx.active, to = idx.active, stage = "backedup", backup = Rel(backup), started = Now() });
            string empty = EmptySnapshot();
            try { ApplyToLive(empty); }
            finally { DeleteDirectory(empty); }
            SaveIndex();
            File.Delete(JournalPath);
            log($"Profiles: reset \"{Active.name}\" (backup: {Path.GetFileName(backup)})");
            return backup;
        }

        /// <summary>Adds a profile with no progression. Doesn't switch to it.</summary>
        public Profile CreateFresh(string name)
        {
            var profile = AddProfile(name);
            ReplaceSnapshot(ProfileDir(profile.id), dir => WriteSnapshot(dir, profile.id, [], EmptyPrefs()));
            SaveIndex();
            log($"Profiles: created \"{profile.name}\"");
            return profile;
        }

        /// <summary>Adds a copy of a profile (the live data if it's the active one). Doesn't switch to it.</summary>
        public Profile Duplicate(string sourceId, string name)
        {
            var idx = LoadIndex();
            var source = idx.profiles.FirstOrDefault(p => p.id == sourceId) ?? throw new ArgumentException($"no profile {sourceId}");
            var profile = AddProfile(name);
            if (source.id == idx.active)
                ReplaceSnapshot(ProfileDir(profile.id), dir => WriteSnapshot(dir, profile.id, LiveFiles(), prefs.Read()));
            else
            {
                VerifySnapshot(ProfileDir(source.id));
                ReplaceSnapshot(ProfileDir(profile.id), dir => CopySnapshot(ProfileDir(source.id), dir));
            }
            SaveIndex();
            log($"Profiles: duplicated \"{source.name}\" as \"{profile.name}\"");
            return profile;
        }

        public void Rename(string id, string name)
        {
            var idx = LoadIndex();
            var profile = idx.profiles.FirstOrDefault(p => p.id == id) ?? throw new ArgumentException($"no profile {id}");
            name = CleanName(name);
            if (idx.profiles.Any(p => p != profile && string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"there's already a profile called \"{name}\"");
            string old = profile.name;
            profile.name = name;
            SaveIndex();
            log($"Profiles: renamed \"{old}\" to \"{name}\"");
        }

        /// <summary>Removes an inactive profile. Its data is moved into the backups, not deleted.</summary>
        public string Delete(string id)
        {
            var idx = LoadIndex();
            var profile = idx.profiles.FirstOrDefault(p => p.id == id) ?? throw new ArgumentException($"no profile {id}");
            if (profile.id == idx.active) throw new InvalidOperationException("switch to another profile before deleting this one");
            string target = null;
            if (Directory.Exists(ProfileDir(id)))
            {
                Directory.CreateDirectory(BackupsDir);
                target = UniqueDir(Path.Combine(BackupsDir, $"{Stamp()} deleted {SafeFileName(profile.name)}"));
                Directory.Move(ProfileDir(id), target);
            }
            idx.profiles.Remove(profile);
            SaveIndex();
            log($"Profiles: deleted \"{profile.name}\"{(target == null ? "" : $" (kept in {Path.GetFileName(target)})")}");
            return target;
        }

        // ---- Crash recovery ---------------------------------------------------------------------------------------

        /// <summary>Finishes or undoes an operation the game was closed in the middle of. Returns a message for the
        /// player if the live data changed, null otherwise.</summary>
        public string RecoverIfNeeded()
        {
            if (!Directory.Exists(Root)) return null;
            foreach (string parent in new[] { Root, ProfilesDir, BackupsDir }.Where(Directory.Exists))
                foreach (string dir in Directory.GetDirectories(parent)) RepairSnapshotSwap(dir);
            if (!File.Exists(JournalPath)) return null;

            var journal = JsonUtility.FromJson<Journal>(File.ReadAllText(JournalPath));
            index = null; // as on a fresh start: trust only what's on disk
            var idx = LoadIndex();
            string message;
            if (journal.stage == "applied")
            {
                idx.active = journal.to;
                SaveIndex();
                message = journal.op == "switch" ? $"Finished switching to profile \"{Name(journal.to)}\"" : $"Finished resetting \"{Name(journal.to)}\"";
            }
            else
            {
                string backup = Path.Combine(Root, journal.backup);
                VerifySnapshot(backup);
                ApplyToLive(backup);
                idx.active = journal.from;
                SaveIndex();
                message = $"The game closed during a profile {journal.op}; \"{Name(journal.from)}\" was restored as it was";
            }
            foreach (var tmp in Directory.GetFiles(LiveDir, "*" + TempSuffix)) File.Delete(tmp);
            File.Delete(JournalPath);
            log($"Profiles: recovered an interrupted {journal.op} ({journal.stage}): {message}");
            return message;
        }

        // ---- Snapshots --------------------------------------------------------------------------------------------

        /// <summary>The live files that make up a profile.</summary>
        public List<string> LiveFiles() =>
            Directory.Exists(LiveDir)
                ? Directory.GetFiles(LiveDir).Where(IsProfileFile).OrderBy(f => f, StringComparer.Ordinal).ToList()
                : [];

        private static bool IsProfileFile(string path)
        {
            string name = Path.GetFileName(path);
            return !SharedFiles.Contains(name) && !name.EndsWith(TempSuffix, StringComparison.Ordinal) && !name.StartsWith("._", StringComparison.Ordinal);
        }

        private string BackupLive(string why, string profileName)
        {
            Directory.CreateDirectory(BackupsDir);
            string dir = UniqueDir(Path.Combine(BackupsDir, $"{Stamp()} {why} {SafeFileName(profileName)}"));
            ReplaceSnapshot(dir, tmp => WriteSnapshot(tmp, profileName, LiveFiles(), prefs.Read()));
            return dir;
        }

        private string EmptySnapshot()
        {
            string dir = UniqueDir(Path.Combine(Root, "empty" + TempSuffix));
            WriteSnapshot(dir, "empty", [], EmptyPrefs());
            return dir;
        }

        private List<PrefValue> EmptyPrefs() => prefs.Read().Select(p => new PrefValue { key = p.key, present = false }).ToList();

        // Writes the snapshot into a temporary sibling, verifies it, then swaps it in (keeping the old one until done).
        private void ReplaceSnapshot(string dir, Action<string> write)
        {
            string tmp = dir + TempSuffix, old = dir + ".old";
            DeleteDirectory(tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(dir));
            write(tmp);
            VerifySnapshot(tmp);
            if (Directory.Exists(dir)) Directory.Move(dir, old);
            Directory.Move(tmp, dir);
            DeleteDirectory(old);
        }

        // Undo a ReplaceSnapshot that was cut off between its renames.
        private static void RepairSnapshotSwap(string path)
        {
            if (path.EndsWith(".old", StringComparison.Ordinal))
            {
                string dir = path.Substring(0, path.Length - 4);
                if (!Directory.Exists(dir)) Directory.Move(path, dir);
                else DeleteDirectory(path);
            }
            else if (path.EndsWith(TempSuffix, StringComparison.Ordinal)) DeleteDirectory(path);
        }

        private static void WriteSnapshot(string dir, string profile, List<string> files, List<PrefValue> prefValues)
        {
            string filesDir = Path.Combine(dir, "files");
            Directory.CreateDirectory(filesDir);
            var manifest = new Manifest { profile = profile, taken = Now() };
            foreach (string file in files)
            {
                string dest = Path.Combine(filesDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: false);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
                manifest.files.Add(new ManifestFile { name = Path.GetFileName(file), size = new FileInfo(dest).Length, sha256 = Hash(dest) });
                if (Hash(file) != manifest.files[manifest.files.Count - 1].sha256) throw new IOException($"{file} changed while it was being copied");
            }
            WriteAllTextAtomic(Path.Combine(dir, "prefs.json"), JsonUtility.ToJson(new PrefsFile { prefs = prefValues }, true));
            WriteAllTextAtomic(Path.Combine(dir, "manifest.json"), JsonUtility.ToJson(manifest, true));
        }

        private static void CopySnapshot(string source, string dest)
        {
            Directory.CreateDirectory(Path.Combine(dest, "files"));
            foreach (string file in Directory.GetFiles(Path.Combine(source, "files")))
            {
                string target = Path.Combine(dest, "files", Path.GetFileName(file));
                File.Copy(file, target);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            }
            File.Copy(Path.Combine(source, "prefs.json"), Path.Combine(dest, "prefs.json"));
            File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(dest, "manifest.json"));
        }

        private static bool SnapshotComplete(string dir) => File.Exists(Path.Combine(dir, "manifest.json"));

        private static Manifest ReadManifest(string dir) =>
            JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));

        /// <summary>Throws unless every file in the snapshot matches its manifest and nothing extra is there.</summary>
        public static void VerifySnapshot(string dir)
        {
            var manifest = ReadManifest(dir);
            var present = Directory.GetFiles(Path.Combine(dir, "files")).Select(Path.GetFileName).ToHashSet();
            foreach (var file in manifest.files)
            {
                string path = Path.Combine(dir, "files", file.name);
                if (!present.Remove(file.name)) throw new InvalidDataException($"{dir}: {file.name} is missing");
                if (new FileInfo(path).Length != file.size || Hash(path) != file.sha256) throw new InvalidDataException($"{dir}: {file.name} is damaged");
            }
            if (present.Count > 0) throw new InvalidDataException($"{dir}: unexpected {string.Join(", ", present)}");
            if (!File.Exists(Path.Combine(dir, "prefs.json"))) throw new InvalidDataException($"{dir}: prefs.json is missing");
        }

        // Makes the live data exactly the snapshot's. Callers back the live data up first.
        private void ApplyToLive(string snapshot)
        {
            VerifySnapshot(snapshot);
            var manifest = ReadManifest(snapshot);
            Directory.CreateDirectory(LiveDir);
            // Stage every incoming file next to its destination first, so a failure here leaves the live files alone.
            var staged = new List<(string tmp, string dest)>();
            foreach (var file in manifest.files)
            {
                string tmp = Path.Combine(LiveDir, file.name + TempSuffix);
                File.Copy(Path.Combine(snapshot, "files", file.name), tmp, overwrite: true);
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(Path.Combine(snapshot, "files", file.name)));
                if (Hash(tmp) != file.sha256) throw new IOException($"copying {file.name} into the save folder failed");
                staged.Add((tmp, Path.Combine(LiveDir, file.name)));
            }
            var incoming = new HashSet<string>(manifest.files.Select(f => f.name));
            foreach (string live in LiveFiles())
                if (!incoming.Contains(Path.GetFileName(live))) File.Delete(live);
            MaybeCrash("midapply");
            foreach (var (tmp, dest) in staged) MoveOver(tmp, dest);
            prefs.Write(JsonUtility.FromJson<PrefsFile>(File.ReadAllText(Path.Combine(snapshot, "prefs.json"))).prefs);
        }

        /// <summary>Perk level and maps beaten, read from the profile's ThroneSave.sav (live for the active one).</summary>
        public (int level, int beaten) Summary(Profile profile)
        {
            string dir = profile.id == LoadIndex().active ? LiveDir : Path.Combine(ProfileDir(profile.id), "files");
            string path = Path.Combine(dir, "ThroneSave.sav");
            int level = 1, beaten = 0;
            try
            {
                if (!File.Exists(path)) return (level, beaten);
                var lines = File.ReadAllLines(path);
                for (int i = 0; i + 1 < lines.Length; i++)
                {
                    if (lines[i] == "Perk Level") int.TryParse(lines[i + 1], out level);
                    else if (lines[i] == "Beaten" && lines[i + 1] == "1") beaten++;
                }
            }
            catch (Exception) { /* only a label */ }
            return (level, beaten);
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private Profile AddProfile(string name)
        {
            var idx = LoadIndex();
            name = CleanName(name);
            if (idx.profiles.Any(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"there's already a profile called \"{name}\"");
            string id;
            do id = Guid.NewGuid().ToString("N").Substring(0, 8); while (idx.profiles.Any(p => p.id == id));
            var profile = new Profile { id = id, name = name, created = Now() };
            idx.profiles.Add(profile);
            return profile;
        }

        /// <summary>A name not used by any profile: "base", "base 2", "base 3"...</summary>
        public string UniqueName(string baseName)
        {
            var names = new HashSet<string>(LoadIndex().profiles.Select(p => p.name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
        }

        public const int MaxNameLength = 24;

        private static string CleanName(string name)
        {
            name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) throw new ArgumentException("a profile needs a name");
            return name.Length > MaxNameLength ? name.Substring(0, MaxNameLength).Trim() : name;
        }

        private string Name(string id) => LoadIndex().profiles.FirstOrDefault(p => p.id == id)?.name ?? id;

        private string Rel(string path) => path.Substring(Root.Length).TrimStart(Path.DirectorySeparatorChar, '/');

        private void WriteJournal(Journal journal)
        {
            Directory.CreateDirectory(Root);
            WriteAllTextAtomic(JournalPath, JsonUtility.ToJson(journal, true));
        }

        private static void WriteAllTextAtomic(string path, string text)
        {
            string tmp = path + TempSuffix;
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(true);
            }
            MoveOver(tmp, path);
        }

        // rename(2) replaces the destination atomically; File.Replace is that on Mono, File.Move when there's nothing to replace.
        private static void MoveOver(string tmp, string dest)
        {
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
        }

        private static void DeleteDirectory(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        private static string UniqueDir(string dir)
        {
            string candidate = dir;
            for (int i = 2; Directory.Exists(candidate) || Directory.Exists(candidate + TempSuffix); i++) candidate = $"{dir} ({i})";
            return candidate;
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }
}
