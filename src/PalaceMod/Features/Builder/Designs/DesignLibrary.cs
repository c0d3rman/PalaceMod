using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The player's levels and enemies (docs/builder-ux.md, "Library"), for the Builder home, the Studios and the agent
    bridge. Storage and the pure logic are Model/LibraryStore.cs; this is the game-side facade:

    - Items(kind): what the home lists. Changed fires after any library write.
    - OpenLevel(id) / OpenEnemies(): a workspace draft to edit (PackDraft, pack id "library"): one level with its nights
      plus every unit and asset, or every unit and no level. draft.Save writes only the items that changed.
    - NewLevel / NewEnemy / Duplicate / Rename / Delete: library items (ids never change; a rename is the display name).
    - SavePreview(kind, id, texture) / Preview(kind, id): saved snapshots shown by the libraries. The Studio captures a
      level's on every save; the Enemy Studio captures an enemy's portrait.
    - Export(...): a pack (levels + the enemies and assets they use) written as a .tfpack or a share code. Imports
      (IPackRepository.InstallNew) add the pack's levels and enemies to the library.
*/
namespace PalaceMod.Builder
{
    public static class DesignLibrary
    {
        public const string PackId = LibraryStore.PackId;

        private static readonly Dictionary<string, PackDraft> workspaces = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, (DateTime stamp, Sprite sprite)> previews = new(StringComparer.Ordinal);
        private const string EnemyWorkspace = "\u0001enemies";

        internal static PackRepository Repo => BuilderServices.Packs as PackRepository;
        /// <summary>The store, freshly read; null before the Builder is up.</summary>
        public static LibraryStore Store => Repo?.Library;
        public static bool Available => Repo != null;

        /// <summary>After any change to the library on disk (and any pack rescan).</summary>
        public static event Action Changed
        {
            add => BuilderServices.Packs.Changed += value;
            remove => BuilderServices.Packs.Changed -= value;
        }

        public static IReadOnlyList<LibraryItem> Items(DesignKind kind) => Store?.Items(kind) ?? [];

        public static LibraryItem Find(DesignKind kind, string id) => Items(kind).FirstOrDefault(i => i.Id == id);

        // ---------------------------------------------------------------- workspaces

        /// <summary>A workspace draft for one level (its nights, plus every enemy and asset). The same draft comes back
        /// while it has unsaved changes (its untouched enemies brought up to date); otherwise a fresh one.</summary>
        internal static PackDraft OpenLevel(string levelId) => Open(levelId);

        /// <summary>A workspace draft with every enemy and no level (the Enemy Studio opened from the library).</summary>
        internal static PackDraft OpenEnemies() => Open(null);

        private static PackDraft Open(string levelId)
        {
            var repo = Repo;
            if (repo == null) return null;
            string key = levelId ?? EnemyWorkspace;
            if (workspaces.TryGetValue(key, out var cached) && cached.Dirty)
            {
                cached.RebaseLibrary();
                return cached;
            }
            repo.Library.Load();
            var pack = repo.Library.Workspace(levelId);
            if (pack == null) return null;
            return workspaces[key] = PackDraft.Workspace(pack);
        }

        /// <summary>A fresh workspace with no level yet, for a new level: once the level is added and saved, Adopt it.</summary>
        internal static PackDraft NewLevelWorkspace()
        {
            var repo = Repo;
            if (repo == null) return null;
            repo.Library.Load();
            return PackDraft.Workspace(repo.Library.Workspace(null));
        }

        /// <summary>Keeps a workspace that now edits one level as that level's workspace.</summary>
        internal static void Adopt(PackDraft draft)
        {
            if (draft?.WorkspaceLevel is { } id) workspaces[id] = draft;
        }

        /// <summary>Workspaces with unsaved changes (the home marks their tiles).</summary>
        internal static bool HasUnsaved(DesignKind kind, string id) => kind == DesignKind.Level
            ? workspaces.TryGetValue(id, out var d) && d.Dirty
            : workspaces.Values.Any(d => d.Dirty && UnitChanged(d, id));

        private static bool UnitChanged(PackDraft d, string id)
        {
            var now = d.Pack.FindUnit(id);
            var saved = d.SavedJson == null ? null : PackSerializer.Read(d.SavedJson, null)?.FindUnit(id);
            if (now == null || saved == null) return (now == null) != (saved == null);
            return PackSerializer.WriteCompact(new DesignPack { Units = [now] }) != PackSerializer.WriteCompact(new DesignPack { Units = [saved] });
        }

        // ---------------------------------------------------------------- items

        /// <summary>Creates a level on disk (with its nights, if any) and returns its id.</summary>
        public static string NewLevel(LevelDef level, WaveSetDef nights = null)
        {
            var repo = Repo ?? throw new InvalidOperationException("the library isn't available");
            repo.Library.Load();
            string id = repo.Library.AddLevel(level, nights == null ? null : [nights]);
            repo.RefreshLibrary();
            return id;
        }

        /// <summary>Creates an enemy on disk and returns its id.</summary>
        public static string NewEnemyOnDisk(UnitDef unit)
        {
            var repo = Repo ?? throw new InvalidOperationException("the library isn't available");
            repo.Library.Load();
            string id = repo.Library.AddUnit(unit);
            repo.RefreshLibrary();
            return id;
        }

        /// <summary>Adds a new unit to a workspace draft (one undo step) under an id free in the draft and the library.
        /// It's written to the library when the draft is saved.</summary>
        internal static UnitDef NewEnemy(PackDraft draft, string baseRef, string name, string side = UnitSide.Enemy)
        {
            var lib = Store;
            lib?.Load();
            string stem = Ids.FromName(string.IsNullOrWhiteSpace(name) ? "enemy" : name);
            string id = stem;
            for (int n = 2; draft.Pack.FindUnit(id) != null || (lib?.HasUnit(id) ?? false); n++) id = $"{stem}-{n}";
            var unit = new UnitDef { Id = id, Side = side, Base = baseRef, Name = name };
            draft.Edit($"New {(side == UnitSide.Player ? "unit" : "enemy")}", p => p.Units.Add(unit), structural: true);
            return draft.Pack.FindUnit(id);
        }

        public static string Duplicate(DesignKind kind, string id) => Write(lib => lib.Duplicate(kind, id));

        public static bool Rename(DesignKind kind, string id, string name)
        {
            bool ok = Write(lib => lib.Rename(kind, id, name) ? id : null) != null;
            // An open workspace that hasn't touched the item follows the new name.
            if (ok) foreach (var d in workspaces.Values.Where(d => !d.Dirty).ToList()) d.RebaseLibrary();
            if (ok && kind == DesignKind.Level) workspaces.Remove(id);
            return ok;
        }

        /// <summary>Moves an item to the library's trash (and forgets its unsaved workspace).</summary>
        public static bool Delete(DesignKind kind, string id)
        {
            bool ok = Write(lib => lib.Trash(kind, id) ? id : null) != null;
            if (ok && kind == DesignKind.Level) workspaces.Remove(id);
            previews.Remove(PreviewKey(kind, id));
            return ok;
        }

        /// <summary>Levels that use the enemy (spawn it, directly or through another enemy).</summary>
        public static List<string> UsedBy(string unitId) => Store?.UsedBy(unitId) ?? [];

        private static string Write(Func<LibraryStore, string> action)
        {
            var repo = Repo;
            if (repo == null) return null;
            try
            {
                repo.Library.Load();
                string result = action(repo.Library);
                repo.RefreshLibrary();
                return result;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException)
            {
                Log.For<BuilderManager>().Error("Library change failed", e);
                Notify.Show("Couldn't change the library: " + e.Message, NotifyKind.Error, 5f, "builder-library");
                return null;
            }
        }

        // ---------------------------------------------------------------- previews

        /// <summary>Saves a snapshot for an item (a level's picture, an enemy's portrait). The texture may be GPU-only.</summary>
        public static bool SavePreview(DesignKind kind, string id, Texture2D texture)
        {
            var lib = Store;
            if (lib == null || texture == null || id == null) return false;
            try
            {
                lib.SavePreview(kind, id, Readable(texture).EncodeToPNG());
                previews.Remove(PreviewKey(kind, id));
                return true;
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Couldn't save the preview of {kind} {id}: {e.Message}");
                return false;
            }
        }

        public static string PreviewPath(DesignKind kind, string id)
        {
            var lib = Store;
            if (lib == null || id == null) return null;
            string file = lib.PreviewFile(kind, id);
            return File.Exists(file) ? file : null;
        }

        /// <summary>The saved snapshot as a sprite (cached until the file changes), or null.</summary>
        public static Sprite Preview(DesignKind kind, string id)
        {
            string file = PreviewPath(kind, id);
            string key = PreviewKey(kind, id);
            if (file == null) { previews.Remove(key); return null; }
            var stamp = File.GetLastWriteTimeUtc(file);
            if (previews.TryGetValue(key, out var p) && p.stamp == stamp && p.sprite != null) return p.sprite;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(file))) return null;
                tex.wrapMode = TextureWrapMode.Clamp;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                previews[key] = (stamp, sprite);
                return sprite;
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Couldn't read the preview {file}: {e.Message}");
                return null;
            }
        }

        private static string PreviewKey(DesignKind kind, string id) => kind + "/" + id;

        private static Texture2D Readable(Texture2D source)
        {
            if (source.isReadable) return source;
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ---------------------------------------------------------------- playing

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PackEntry, Dictionary<string, List<ValidationIssue>>> playIssues = new();

        /// <summary>What stops a design from playing: for a library level only that level, its nights and the enemies
        /// they use (a problem in another level doesn't matter); for a pack (or no level), the pack's own issues.</summary>
        public static IReadOnlyList<ValidationIssue> PlayIssues(PackEntry entry, string levelId)
        {
            if (entry?.Pack == null || entry.Id != PackId || levelId == null) return entry?.Issues ?? [];
            var cache = playIssues.GetOrCreateValue(entry);
            if (cache.TryGetValue(levelId, out var list)) return list;
            var play = Studio.Play.TestPacks.Play(entry.Pack, levelId, out string error);
            list = play == null ? [new ValidationIssue(Severity.Error, "levels", error)] : BuilderServices.Packs.Validate(play);
            return cache[levelId] = list;
        }

        public static bool CanPlay(PackEntry entry, string levelId) => !PlayIssues(entry, levelId).Any(i => i.Severity == Severity.Error);

        // ---------------------------------------------------------------- export

        /// <summary>
        /// The pack for an export: the levels, the enemies chosen and every enemy and asset they use, written to a
        /// staging folder (pack.json + assets/) so the archive carries only those files. Returns its entry.
        /// </summary>
        public static PackEntry ExportEntry(IEnumerable<string> levelIds, IEnumerable<string> unitIds, string name, string author = null, string description = null)
        {
            var repo = Repo ?? throw new InvalidOperationException("the library isn't available");
            repo.Library.Load();
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            string id = Ids.FromName(string.IsNullOrWhiteSpace(author) ? name : author + "." + name);
            var pack = repo.Library.Export(levelIds, unitIds, id, name, author, description, files);
            pack.GameVersion = Application.version;
            string staging = Path.Combine(repo.Root, "staging", "export-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);
            foreach (var kv in files)
            {
                string target = Path.Combine(staging, kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(kv.Value, target, true);
            }
            File.WriteAllText(Path.Combine(staging, PackRepository.PackFile), PackSerializer.Write(pack));
            return new PackEntry { Pack = pack, Folder = staging, Source = PackSource.User, Issues = repo.Validate(pack) };
        }

        /// <summary>Exports to a .tfpack in the exports folder; returns its path.</summary>
        public static string ExportFile(IEnumerable<string> levelIds, IEnumerable<string> unitIds, string name, string author = null, string description = null)
        {
            var entry = ExportEntry(levelIds, unitIds, name, author, description);
            try { return BuilderServices.Archives.Export(entry); }
            finally { TryDelete(entry.Folder); }
        }

        /// <summary>A share code for the levels and enemies (assets must be URL assets to travel in a code).</summary>
        public static string ShareCode(IEnumerable<string> levelIds, IEnumerable<string> unitIds, string name, string author = null)
        {
            var entry = ExportEntry(levelIds, unitIds, name, author);
            try { return BuilderServices.Share.Encode(entry.Pack); }
            finally { TryDelete(entry.Folder); }
        }

        private static void TryDelete(string folder)
        {
            try { if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>Imports a pack (from a share code, a file, or an installed pack) into the library.</summary>
        public static LibraryImportResult Import(DesignPack pack, string folder = null)
        {
            var repo = Repo ?? throw new InvalidOperationException("the library isn't available");
            repo.Import(pack, folder, out var result);
            return result;
        }
    }
}
