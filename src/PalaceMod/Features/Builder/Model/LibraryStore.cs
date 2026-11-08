using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

/*
    The player's design library (docs/builder-ux.md, "Library"): levels and enemies are first-class items, each its own
    file; packs only exist at export and import. Pure C# (no Unity): unit-tested in tests/PalaceMod.Tests/LibraryTests.cs.

        BepInEx/PalaceBuilder/library/
          levels/<levelId>.json     a one-level pack file: the level and the wave sets (nights) it owns
          levels/<levelId>.png      its preview, captured by the Studio on save
          enemies/<unitId>.json     a one-unit pack file (enemies and custom player troops)
          enemies/<unitId>.png      its portrait, captured by the Enemy Studio on save
          assets.json               the shared "assets" map (key -> { path: "assets/..." })
          assets/...                asset files
          trash/                    deleted or replaced items (never removed outright)

    Item files use the pack format ("palace-builder", same version and migrations), so every pack tool reads them.
    Item metadata lives in the pack's "library" field: { kind, created, origin }.

    At runtime the whole library is one composed pack with the id PackId ("library") whose folder is the library root,
    so the session launcher, unit factory, asset provider and validator treat it like any user pack. Levels refer to
    enemies by their plain unit id; ids never change after creation (rename changes the display name only).

    Editing goes through workspaces: a pack holding one level (plus its wave sets) and every unit and asset, or no level
    (the enemy workspace). SaveWorkspace writes only the items that differ from the workspace's baseline, so two
    workspaces never overwrite each other's untouched items.
*/
namespace PalaceMod.Builder.Model
{
    public enum DesignKind { Level, Enemy }

    /// <summary>One item of the library as the home lists it.</summary>
    public sealed class LibraryItem
    {
        public DesignKind Kind;
        public string Id, Name, Description;
        /// <summary>Levels: host scene. Enemies: base ref ("vanilla:E Racer").</summary>
        public string Host, Base;
        /// <summary>Enemies: UnitSide.Enemy or UnitSide.Player.</summary>
        public string Side;
        /// <summary>Levels: night count (0 = the host's own nights). Enemies: levels that use it.</summary>
        public int Nights;
        public DateTime Created, Modified;
        /// <summary>The pack it was imported or migrated from, or null.</summary>
        public string Origin;
        public string File;
        /// <summary>Path of the saved preview PNG, or null if none was captured yet.</summary>
        public string Preview;
    }

    public sealed class LibraryImportResult
    {
        /// <summary>Old id -> library id (only entries that changed).</summary>
        public Dictionary<string, string> LevelIds = [], UnitIds = [], WaveSetIds = [], AssetKeys = [];
        public List<string> Levels = [], Units = [];
        /// <summary>Units that were already in the library unchanged (reused, not copied).</summary>
        public List<string> Reused = [];
        /// <summary>Items an import of the same pack replaced (the old file went to trash).</summary>
        public List<string> Replaced = [];
        public List<ValidationIssue> Issues = [];

        public string Summary()
        {
            var parts = new List<string>();
            if (Levels.Count > 0) parts.Add(Levels.Count == 1 ? "1 level" : $"{Levels.Count} levels");
            if (Units.Count > 0) parts.Add(Units.Count == 1 ? "1 enemy" : $"{Units.Count} enemies");
            return parts.Count == 0 ? "nothing new" : string.Join(" and ", parts);
        }
    }

    public sealed class LibraryStore
    {
        public const string PackId = "library";
        public const string MetaField = "library";
        private const string AssetsFile = "assets.json";

        public string Root { get; }
        public string LevelsDir => Path.Combine(Root, "levels");
        public string EnemiesDir => Path.Combine(Root, "enemies");
        public string TrashDir => Path.Combine(Root, "trash");

        private sealed class Entry
        {
            public DesignKind Kind;
            public string Id;
            public DesignPack Pack;  // the item file (one level + wave sets, or one unit)
            public string Json;      // compact JSON of the item as on disk
            public string File;
            public DateTime Modified;
        }

        private readonly Dictionary<string, Entry> levels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> units = new(StringComparer.Ordinal);
        private Dictionary<string, AssetDef> assets = [];
        private string assetsJson = "{}";

        /// <summary>Problems found reading the files (unreadable items are skipped, never deleted).</summary>
        public List<ValidationIssue> Issues { get; } = [];

        public LibraryStore(string root) { Root = root; }

        // ---------------------------------------------------------------- reading

        public void Load()
        {
            levels.Clear();
            units.Clear();
            Issues.Clear();
            LoadDir(LevelsDir, DesignKind.Level, levels);
            LoadDir(EnemiesDir, DesignKind.Enemy, units);
            assets = [];
            assetsJson = "{}";
            string file = Path.Combine(Root, AssetsFile);
            if (File.Exists(file))
            {
                try
                {
                    assets = JObject.Parse(File.ReadAllText(file)).ToObject<Dictionary<string, AssetDef>>() ?? [];
                    assetsJson = Compact(JObject.FromObject(assets));
                }
                catch (Exception e) when (e is IOException || e is Newtonsoft.Json.JsonException || e is UnauthorizedAccessException)
                {
                    Issues.Add(new ValidationIssue(Severity.Error, AssetsFile, "can't be read: " + e.Message));
                }
            }
        }

        private void LoadDir(string dir, DesignKind kind, Dictionary<string, Entry> into)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                var issues = new List<ValidationIssue>();
                DesignPack pack = null;
                try { pack = PackSerializer.Read(File.ReadAllText(file), issues); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { issues.Add(new ValidationIssue(Severity.Error, "", e.Message)); }
                string where = $"{(kind == DesignKind.Level ? "levels" : "enemies")}/{Path.GetFileName(file)}";
                if (pack == null) { Issues.Add(new ValidationIssue(Severity.Error, where, issues.FirstOrDefault()?.Message ?? "can't be read")); continue; }
                if (kind == DesignKind.Level ? pack.Levels?.Count != 1 : pack.Units?.Count != 1)
                {
                    Issues.Add(new ValidationIssue(Severity.Error, where, kind == DesignKind.Level ? "must hold exactly one level" : "must hold exactly one unit"));
                    continue;
                }
                // The file name is the id; a hand-edited id inside follows it.
                if (kind == DesignKind.Level) pack.Levels[0].Id = id; else pack.Units[0].Id = id;
                pack.Id = id;
                into[id] = new Entry { Kind = kind, Id = id, Pack = pack, Json = Compact(pack), File = file, Modified = File.GetLastWriteTimeUtc(file) };
            }
        }

        public IEnumerable<string> LevelIds => levels.Keys;
        public IEnumerable<string> UnitIds => units.Keys;
        public bool HasLevel(string id) => id != null && levels.ContainsKey(id);
        public bool HasUnit(string id) => id != null && units.ContainsKey(id);
        public LevelDef Level(string id) => id != null && levels.TryGetValue(id, out var e) ? e.Pack.Levels[0] : null;
        public UnitDef Unit(string id) => id != null && units.TryGetValue(id, out var e) ? e.Pack.Units[0] : null;
        public IReadOnlyDictionary<string, AssetDef> Assets => assets;

        public string ItemFile(DesignKind kind, string id) => Path.Combine(kind == DesignKind.Level ? LevelsDir : EnemiesDir, id + ".json");
        public string PreviewFile(DesignKind kind, string id) => Path.Combine(kind == DesignKind.Level ? LevelsDir : EnemiesDir, id + ".png");

        public IReadOnlyList<LibraryItem> Items(DesignKind kind)
        {
            var list = new List<LibraryItem>();
            if (kind == DesignKind.Level)
                foreach (var e in levels.Values)
                {
                    var lv = e.Pack.Levels[0];
                    var ws = lv.WaveSet == null ? null : e.Pack.FindWaveSet(lv.WaveSet);
                    list.Add(Item(e, lv.Name, lv.Description, lv.Host, null, null, ws?.Nights?.Count ?? 0));
                }
            else
                foreach (var e in units.Values)
                {
                    var u = e.Pack.Units[0];
                    list.Add(Item(e, u.Name, u.Description, null, u.Base, u.Side ?? UnitSide.Enemy, UsedBy(u.Id).Count));
                }
            return list;
        }

        private LibraryItem Item(Entry e, string name, string description, string host, string baseRef, string side, int nights)
        {
            var meta = Meta(e.Pack);
            string preview = PreviewFile(e.Kind, e.Id);
            return new LibraryItem
            {
                Kind = e.Kind, Id = e.Id, Name = string.IsNullOrWhiteSpace(name) ? e.Id : name, Description = description,
                Host = host, Base = baseRef, Side = side, Nights = nights,
                Created = (DateTime?)meta?["created"] is DateTime c ? c.ToUniversalTime() : e.Modified,
                Modified = e.Modified, Origin = (string)meta?["origin"], File = e.File,
                Preview = File.Exists(preview) ? preview : null,
            };
        }

        private static JObject Meta(DesignPack pack) => pack.Extra != null && pack.Extra.TryGetValue(MetaField, out var m) ? m as JObject : null;

        /// <summary>Levels whose nights spawn the unit (directly, or through a unit that spawns it, e.g. on death).</summary>
        public List<string> UsedBy(string unitId)
        {
            var result = new List<string>();
            foreach (var e in levels.Values)
            {
                var closure = UnitClosure(LevelRoots(e.Pack), id => Unit(id));
                if (closure.Contains(unitId)) result.Add(e.Id);
            }
            return result;
        }

        // ---------------------------------------------------------------- composing

        /// <summary>The whole library as one pack (what sessions and validation see).</summary>
        public DesignPack Compose()
        {
            var pack = NewPack(PackId, "My library");
            foreach (var e in levels.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                var copy = PackSerializer.Clone(e.Pack);
                pack.Levels.AddRange(copy.Levels);
                pack.WaveSets.AddRange(copy.WaveSets ?? []);
            }
            foreach (var e in units.Values.OrderBy(e => e.Id, StringComparer.Ordinal)) pack.Units.Add(PackSerializer.Clone(e.Pack).Units[0]);
            foreach (var kv in assets) pack.Assets[kv.Key] = Clone(kv.Value);
            return pack;
        }

        /// <summary>A pack to edit: one level with its wave sets (levelId null: none) plus every unit and asset.</summary>
        public DesignPack Workspace(string levelId)
        {
            var pack = NewPack(PackId, "My library");
            if (levelId != null)
            {
                if (!levels.TryGetValue(levelId, out var e)) return null;
                var copy = PackSerializer.Clone(e.Pack);
                pack.Levels.AddRange(copy.Levels);
                pack.WaveSets.AddRange(copy.WaveSets ?? []);
                pack.Name = copy.Levels[0].Name ?? levelId;
            }
            foreach (var u in units.Values.OrderBy(e => e.Id, StringComparer.Ordinal)) pack.Units.Add(PackSerializer.Clone(u.Pack).Units[0]);
            foreach (var kv in assets) pack.Assets[kv.Key] = Clone(kv.Value);
            return pack;
        }

        /// <summary>Which level a workspace edits (null for the enemy workspace).</summary>
        public static string WorkspaceLevel(DesignPack workspace) => workspace?.Levels?.Count == 1 ? workspace.Levels[0].Id : null;

        // ---------------------------------------------------------------- writing

        /// <summary>
        /// Writes what changed in a workspace since <paramref name="baseline"/> (the workspace as opened or last saved;
        /// null = everything is new): changed or new units and the level are written, units and levels that were in the
        /// baseline and are gone go to trash, and the asset map is merged. Returns the number of files written.
        /// </summary>
        public int SaveWorkspace(DesignPack now, DesignPack baseline)
        {
            int written = 0;
            var baseUnits = (baseline?.Units ?? []).Where(u => u?.Id != null).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => Compact(g.First()));
            var nowUnits = (now.Units ?? []).Where(u => u?.Id != null).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var kv in nowUnits)
            {
                if (!Ids.IsValid(kv.Key)) throw new ArgumentException($"invalid unit id \"{kv.Key}\"");
                string json = Compact(kv.Value);
                bool changed = !baseUnits.TryGetValue(kv.Key, out var before) || before != json;
                if (!changed && units.ContainsKey(kv.Key)) continue;
                WriteUnit(kv.Value, units.TryGetValue(kv.Key, out var old) ? Meta(old.Pack) : null);
                written++;
            }
            foreach (var id in baseUnits.Keys.Where(id => !nowUnits.ContainsKey(id)).ToList())
                if (Trash(DesignKind.Enemy, id)) written++;

            var baseLevels = (baseline?.Levels ?? []).Where(l => l?.Id != null).Select(l => l.Id).ToList();
            foreach (var lv in now.Levels ?? [])
            {
                if (lv?.Id == null) continue;
                if (!Ids.IsValid(lv.Id)) throw new ArgumentException($"invalid level id \"{lv.Id}\"");
                var owned = OwnedWaveSets(now, lv);
                // Wave set ids are library-wide (the composed pack holds every level's); a clash is renamed in the
                // workspace too, so it keeps matching the file.
                var taken = WaveSetIds(lv.Id);
                foreach (var ws in owned.Where(w => w.Id != null && taken.Contains(w.Id)))
                {
                    string fresh = Unique(ws.Id, x => taken.Contains(x) || owned.Any(o => o != ws && o.Id == x));
                    if (lv.WaveSet == ws.Id) lv.WaveSet = fresh;
                    ws.Id = fresh;
                }
                var file = LevelFile(lv, owned, levels.TryGetValue(lv.Id, out var old) ? Meta(old.Pack) : null);
                string json = Compact(file);
                if (old != null && old.Json == json) continue;
                WriteItem(DesignKind.Level, lv.Id, file);
                written++;
            }
            foreach (var id in baseLevels.Where(id => now.FindLevel(id) == null))
                if (Trash(DesignKind.Level, id)) written++;

            // Assets: keys added or changed in the workspace are merged; keys the workspace dropped since its baseline go.
            var merged = new Dictionary<string, AssetDef>(assets);
            foreach (var kv in now.Assets ?? []) merged[kv.Key] = Clone(kv.Value);
            foreach (var key in (baseline?.Assets ?? []).Keys.Where(k => now.Assets == null || !now.Assets.ContainsKey(k))) merged.Remove(key);
            if (Compact(JObject.FromObject(merged)) != assetsJson) { WriteAssets(merged); written++; }
            return written;
        }

        /// <summary>The wave sets a level owns in a workspace: every wave set in it when it holds one level; else the
        /// level's own and any its id prefix claims.</summary>
        private static List<WaveSetDef> OwnedWaveSets(DesignPack workspace, LevelDef level)
        {
            if (workspace.Levels.Count == 1) return (workspace.WaveSets ?? []).Where(w => w != null).ToList();
            return (workspace.WaveSets ?? []).Where(w => w?.Id != null && w.Id == level.WaveSet).ToList();
        }

        private DesignPack LevelFile(LevelDef level, List<WaveSetDef> waveSets, JObject meta)
        {
            var pack = NewPack(level.Id, level.Name);
            pack.Levels.Add(PackSerializer.Clone(new DesignPack { Id = "x", Levels = [level] }).Levels[0]);
            foreach (var ws in waveSets) pack.WaveSets.Add(PackSerializer.Clone(new DesignPack { Id = "x", WaveSets = [ws] }).WaveSets[0]);
            SetMeta(pack, DesignKind.Level, meta);
            return pack;
        }

        private void WriteUnit(UnitDef unit, JObject meta)
        {
            var pack = NewPack(unit.Id, unit.Name);
            pack.Units.Add(PackSerializer.Clone(new DesignPack { Id = "x", Units = [unit] }).Units[0]);
            SetMeta(pack, DesignKind.Enemy, meta);
            WriteItem(DesignKind.Enemy, unit.Id, pack);
        }

        private static void SetMeta(DesignPack pack, DesignKind kind, JObject meta)
        {
            meta = meta == null ? new JObject() : (JObject)meta.DeepClone();
            meta["kind"] = kind == DesignKind.Level ? "level" : "enemy";
            meta["created"] ??= DateTime.UtcNow.ToString("o");
            pack.Extra ??= new Dictionary<string, JToken>();
            pack.Extra[MetaField] = meta;
        }

        private void WriteItem(DesignKind kind, string id, DesignPack pack)
        {
            string file = ItemFile(kind, id);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            AtomicWrite(file, PackSerializer.Write(pack));
            var copy = PackSerializer.Read(File.ReadAllText(file), null);
            (kind == DesignKind.Level ? levels : units)[id] = new Entry { Kind = kind, Id = id, Pack = copy, Json = Compact(copy), File = file, Modified = File.GetLastWriteTimeUtc(file) };
        }

        private void WriteAssets(Dictionary<string, AssetDef> map)
        {
            Directory.CreateDirectory(Root);
            var obj = JObject.FromObject(map);
            AtomicWrite(Path.Combine(Root, AssetsFile), obj.ToString(Newtonsoft.Json.Formatting.Indented));
            assets = map;
            assetsJson = Compact(obj);
        }

        private static void AtomicWrite(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Moves an item's file (and preview) to trash. False if it wasn't there.</summary>
        public bool Trash(DesignKind kind, string id)
        {
            var map = kind == DesignKind.Level ? levels : units;
            string file = ItemFile(kind, id);
            if (!File.Exists(file)) { map.Remove(id); return false; }
            Directory.CreateDirectory(TrashDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string prefix = kind == DesignKind.Level ? "level" : "enemy";
            File.Move(file, Path.Combine(TrashDir, $"{prefix}-{id}-{stamp}.json"));
            string preview = PreviewFile(kind, id);
            if (File.Exists(preview)) File.Move(preview, Path.Combine(TrashDir, $"{prefix}-{id}-{stamp}.png"));
            map.Remove(id);
            return true;
        }

        // ---------------------------------------------------------------- item actions

        public string UniqueLevelId(string wanted) => Unique(Ids.IsValid(wanted) ? wanted : Ids.FromName(wanted), id => levels.ContainsKey(id) || File.Exists(ItemFile(DesignKind.Level, id)));
        public string UniqueUnitId(string wanted) => Unique(Ids.IsValid(wanted) ? wanted : Ids.FromName(wanted), id => units.ContainsKey(id) || File.Exists(ItemFile(DesignKind.Enemy, id)));

        /// <summary>A wave set id no other level's nights use (for a new wave set in a level's workspace).</summary>
        public string UniqueWaveSetId(string wanted, string levelId, IEnumerable<string> alsoTaken = null)
        {
            var taken = WaveSetIds(levelId);
            foreach (var t in alsoTaken ?? []) taken.Add(t);
            return Unique(Ids.IsValid(wanted) ? wanted : Ids.FromName(wanted), taken.Contains);
        }

        private HashSet<string> WaveSetIds(string exceptLevel = null) =>
            new(levels.Values.Where(e => e.Id != exceptLevel).SelectMany(e => e.Pack.WaveSets ?? []).Where(w => w?.Id != null).Select(w => w.Id), StringComparer.Ordinal);

        private static string Unique(string id, Func<string, bool> taken)
        {
            if (!taken(id)) return id;
            for (int n = 2; ; n++)
            {
                string suffix = "-" + n;
                string candidate = (id.Length + suffix.Length > 64 ? id.Substring(0, 64 - suffix.Length) : id) + suffix;
                if (!taken(candidate)) return candidate;
            }
        }

        /// <summary>Adds a new level (with the wave sets it owns) under a free id; returns the id.</summary>
        public string AddLevel(LevelDef level, IEnumerable<WaveSetDef> waveSets = null, string origin = null)
        {
            var copy = new DesignPack { Id = "x", Levels = [level], WaveSets = (waveSets ?? []).ToList() };
            copy = PackSerializer.Clone(copy);
            var lv = copy.Levels[0];
            lv.Id = UniqueLevelId(lv.Id ?? lv.Name ?? "level");
            RenameWaveSets(copy, lv, WaveSetIds(), null);
            WriteItem(DesignKind.Level, lv.Id, LevelFile(lv, copy.WaveSets, origin == null ? null : new JObject { ["origin"] = origin }));
            return lv.Id;
        }

        /// <summary>Adds a new unit under a free id; returns the id.</summary>
        public string AddUnit(UnitDef unit, string origin = null)
        {
            var u = PackSerializer.Clone(new DesignPack { Id = "x", Units = [unit] }).Units[0];
            u.Id = UniqueUnitId(u.Id ?? u.Name ?? "enemy");
            WriteUnit(u, origin == null ? null : new JObject { ["origin"] = origin });
            return u.Id;
        }

        public string Duplicate(DesignKind kind, string id)
        {
            if (kind == DesignKind.Level)
            {
                if (!levels.TryGetValue(id, out var e)) return null;
                var copy = PackSerializer.Clone(e.Pack);
                var lv = copy.Levels[0];
                lv.Name = CopyName(lv.Name ?? id);
                lv.Id = UniqueLevelId(id);
                RenameWaveSets(copy, lv, WaveSetIds(), null);
                WriteItem(DesignKind.Level, lv.Id, LevelFile(lv, copy.WaveSets, null));
                CopyPreview(kind, id, lv.Id);
                return lv.Id;
            }
            if (!units.TryGetValue(id, out var ue)) return null;
            var u = PackSerializer.Clone(ue.Pack).Units[0];
            u.Name = CopyName(u.Name ?? id);
            u.Id = UniqueUnitId(id);
            WriteUnit(u, null);
            CopyPreview(kind, id, u.Id);
            return u.Id;
        }

        private static string CopyName(string name) => name.EndsWith(" (copy)") ? name : name + " (copy)";

        private void CopyPreview(DesignKind kind, string from, string to)
        {
            string src = PreviewFile(kind, from);
            if (File.Exists(src)) File.Copy(src, PreviewFile(kind, to), true);
        }

        /// <summary>Changes the display name (ids never change, so references stay valid).</summary>
        public bool Rename(DesignKind kind, string id, string name)
        {
            if (kind == DesignKind.Level)
            {
                if (!levels.TryGetValue(id, out var e)) return false;
                var copy = PackSerializer.Clone(e.Pack);
                copy.Levels[0].Name = name;
                copy.Name = name;
                WriteItem(kind, id, copy);
                return true;
            }
            if (!units.TryGetValue(id, out var ue)) return false;
            var ucopy = PackSerializer.Clone(ue.Pack);
            ucopy.Units[0].Name = name;
            ucopy.Name = name;
            WriteItem(kind, id, ucopy);
            return true;
        }

        public void SavePreview(DesignKind kind, string id, byte[] png)
        {
            string file = PreviewFile(kind, id);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            File.WriteAllBytes(tmp, png);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        // ---------------------------------------------------------------- import

        /// <summary>
        /// Adds a pack's levels (with their wave sets), units and assets to the library. Ids that are taken get a
        /// new one ("-2", ...) and every reference is rewritten; a unit identical to the library's is reused; an item
        /// that came from the same pack (origin) with the same id is replaced, the old file going to trash. Wave sets
        /// no level uses become levels' nights only if a level refers to them; otherwise they're dropped with a note.
        /// </summary>
        public LibraryImportResult Import(DesignPack source, string sourceFolder, string origin = null)
        {
            var result = new LibraryImportResult();
            var pack = PackSerializer.Clone(source) ?? throw new ArgumentException("the pack can't be read");
            origin ??= pack.Id;

            // Assets first: their keys appear inside units and levels.
            foreach (var key in (pack.Assets ?? []).Keys.ToList())
            {
                var def = pack.Assets[key];
                string newKey = ImportAsset(key, def, sourceFolder, origin, result);
                if (newKey != key) result.AssetKeys[key] = newKey;
            }
            if (result.AssetKeys.Count > 0) RewriteStrings(pack, s => s.StartsWith("asset:", StringComparison.Ordinal) && result.AssetKeys.TryGetValue(s.Substring(6), out var k) ? "asset:" + k : null);

            // Units: identical -> reuse; same origin and id -> replace; else a free id.
            var unitMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var u in pack.Units ?? [])
            {
                if (u?.Id == null) continue;
                var existing = Unit(u.Id);
                if (existing == null) { unitMap[u.Id] = u.Id; continue; }
                if (Compact(existing) == Compact(WithId(u, u.Id))) { unitMap[u.Id] = u.Id; result.Reused.Add(u.Id); continue; }
                if (OriginOf(DesignKind.Enemy, u.Id) == origin) { unitMap[u.Id] = u.Id; result.Replaced.Add("enemy " + u.Id); continue; }
                unitMap[u.Id] = Unique(u.Id, id => units.ContainsKey(id) || unitMap.Values.Contains(id) || File.Exists(ItemFile(DesignKind.Enemy, id)));
            }
            foreach (var kv in unitMap.Where(kv => kv.Key != kv.Value)) result.UnitIds[kv.Key] = kv.Value;
            if (result.UnitIds.Count > 0) RewriteUnitRefs(pack, result.UnitIds);
            foreach (var u in pack.Units ?? [])
            {
                if (u?.Id == null || result.Reused.Contains(u.Id) && !result.UnitIds.ContainsKey(u.Id)) continue;
                string id = result.UnitIds.TryGetValue(u.Id, out var nid) ? nid : u.Id;
                bool replacing = units.ContainsKey(id);
                if (replacing) Trash(DesignKind.Enemy, id);
                u.Id = id;
                WriteUnit(u, new JObject { ["origin"] = origin });
                result.Units.Add(id);
            }

            // Levels with the wave sets they use.
            var usedSets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lv in pack.Levels ?? [])
            {
                if (lv?.Id == null) continue;
                string id = lv.Id;
                bool replace = false;
                if (levels.ContainsKey(id))
                {
                    var file = LevelFile(lv, SetsOf(pack, lv), Meta(levels[id].Pack));
                    if (Compact(WithoutMeta(file)) == Compact(WithoutMeta(levels[id].Pack))) { result.Reused.Add("level " + id); continue; }
                    replace = OriginOf(DesignKind.Level, id) == origin;
                    if (!replace) id = UniqueLevelId(id);
                }
                if (replace) { Trash(DesignKind.Level, id); result.Replaced.Add("level " + id); }
                if (id != lv.Id) result.LevelIds[lv.Id] = id;
                var sets = SetsOf(pack, lv).Select(w => PackSerializer.Clone(new DesignPack { Id = "x", WaveSets = [w] }).WaveSets[0]).ToList();
                foreach (var w in sets) usedSets.Add(w.Id);
                var item = new DesignPack { Id = "x", Levels = [lv], WaveSets = sets };
                lv.Id = id;
                var renamed = RenameWaveSets(item, lv, WaveSetIds(id), null);
                foreach (var kv in renamed) result.WaveSetIds[kv.Key] = kv.Value;
                WriteItem(DesignKind.Level, id, LevelFile(lv, item.WaveSets, new JObject { ["origin"] = origin }));
                result.Levels.Add(id);
            }
            foreach (var ws in (pack.WaveSets ?? []).Where(w => w?.Id != null && !usedSets.Contains(w.Id)))
                result.Issues.Add(new ValidationIssue(Severity.Warning, "waveSets", $"the nights \"{ws.Name ?? ws.Id}\" aren't used by any level, so they weren't imported"));
            return result;
        }

        private static UnitDef WithId(UnitDef u, string id)
        {
            var c = PackSerializer.Clone(new DesignPack { Id = "x", Units = [u] }).Units[0];
            c.Id = id;
            return c;
        }

        private static DesignPack WithoutMeta(DesignPack p)
        {
            var c = PackSerializer.Clone(p);
            c.Extra?.Remove(MetaField);
            c.Name = null;
            c.GameVersion = null;
            return c;
        }

        private static List<WaveSetDef> SetsOf(DesignPack pack, LevelDef lv) =>
            lv.WaveSet == null ? [] : (pack.WaveSets ?? []).Where(w => w?.Id == lv.WaveSet).Take(1).ToList();

        private string OriginOf(DesignKind kind, string id)
        {
            var map = kind == DesignKind.Level ? levels : units;
            return map.TryGetValue(id, out var e) ? (string)Meta(e.Pack)?["origin"] : null;
        }

        /// <summary>Gives the item's wave sets ids no other level uses, and points the level at the renamed one.</summary>
        private static Dictionary<string, string> RenameWaveSets(DesignPack item, LevelDef lv, HashSet<string> taken, string unused)
        {
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ws in item.WaveSets ?? [])
            {
                if (ws == null) continue;
                string id = Ids.IsValid(ws.Id) ? ws.Id : lv.Id + "-nights";
                string fresh = Unique(id, x => taken.Contains(x));
                taken.Add(fresh);
                if (fresh != ws.Id)
                {
                    if (ws.Id != null) renamed[ws.Id] = fresh;
                    if (lv.WaveSet == ws.Id) lv.WaveSet = fresh;
                    ws.Id = fresh;
                }
            }
            return renamed;
        }

        private string ImportAsset(string key, AssetDef def, string sourceFolder, string origin, LibraryImportResult result)
        {
            if (def == null) return key;
            string srcFile = def.Path != null && sourceFolder != null ? Path.Combine(sourceFolder, def.Path) : null;
            bool hasFile = srcFile != null && File.Exists(srcFile);
            string sha = hasFile ? Sha256(srcFile) : def.Sha256;
            // Same key with the same content: reuse.
            if (assets.TryGetValue(key, out var mine))
            {
                string mineFile = mine.Path != null ? Path.Combine(Root, mine.Path) : null;
                string mineSha = mineFile != null && File.Exists(mineFile) ? Sha256(mineFile) : mine.Sha256;
                if (mineSha != null && mineSha == sha) return key;
            }
            string newKey = Unique(key, k => assets.ContainsKey(k));
            var copy = Clone(def);
            if (hasFile)
            {
                string rel = "assets/" + Path.GetFileName(def.Path);
                string target = Path.Combine(Root, rel);
                if (File.Exists(target) && Sha256(target) != sha)
                {
                    string stem = Path.GetFileNameWithoutExtension(def.Path), ext = Path.GetExtension(def.Path);
                    for (int n = 2; File.Exists(target); n++) { rel = $"assets/{stem}-{n}{ext}"; target = Path.Combine(Root, rel); }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!File.Exists(target)) File.Copy(srcFile, target);
                copy.Path = rel;
            }
            else if (def.Path != null && def.Url == null)
                result.Issues.Add(new ValidationIssue(Severity.Warning, "assets." + key, $"its file \"{def.Path}\" wasn't included"));
            var merged = new Dictionary<string, AssetDef>(assets) { [newKey] = copy };
            WriteAssets(merged);
            return newKey;
        }

        private static string Sha256(string file)
        {
            using var sha = SHA256.Create();
            using var s = File.OpenRead(file);
            return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
        }

        /// <summary>Rewrites unit references (spawns, troops, behaviour parameters) through the map.</summary>
        public static void RewriteUnitRefs(DesignPack pack, IReadOnlyDictionary<string, string> map)
        {
            string Map(string raw)
            {
                if (raw == null) return null;
                var r = ContentRef.Parse(raw);
                if (r.Kind != RefKind.LocalUnit || !map.TryGetValue(r.Id, out var to)) return raw;
                return raw.StartsWith("unit:", StringComparison.Ordinal) ? "unit:" + to : to;
            }
            foreach (var ws in pack.WaveSets ?? [])
            {
                foreach (var n in ws?.Nights ?? [])
                    foreach (var s in n?.Spawns ?? []) if (s != null) s.Unit = Map(s.Unit);
                foreach (var t in ws?.Troops ?? []) if (t != null) t.Unit = Map(t.Unit);
            }
            foreach (var u in pack.Units ?? [])
                foreach (var b in u?.Behaviours ?? [])
                    foreach (var key in (b?.Params?.Keys ?? []).ToList())
                        if (b.Params[key]?.Type == JTokenType.String) b.Params[key] = Map((string)b.Params[key]);
        }

        private static void RewriteStrings(DesignPack pack, Func<string, string> map)
        {
            var root = JObject.FromObject(pack, Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }));
            foreach (var v in root.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
                if (map((string)v) is { } to) v.Value = to;
            var back = PackSerializer.FromJObject(root, null);
            pack.Units = back.Units;
            pack.WaveSets = back.WaveSets;
            pack.Levels = back.Levels;
        }

        // ---------------------------------------------------------------- export

        /// <summary>
        /// A shareable pack of the chosen levels (with their nights), the chosen units, every unit those use (through
        /// spawns, troops and behaviours, transitively) and every asset any of it names. assetFiles gets each asset's
        /// file on disk (pack-relative path -> absolute path) for the archive.
        /// </summary>
        public DesignPack Export(IEnumerable<string> levelIds, IEnumerable<string> unitIds, string packId, string name, string author, string description,
            Dictionary<string, string> assetFiles = null)
        {
            var pack = NewPack(packId, name);
            pack.Author = author;
            pack.Description = description;
            var roots = new List<string>(unitIds ?? []);
            foreach (var id in levelIds ?? [])
            {
                if (!levels.TryGetValue(id, out var e)) throw new ArgumentException($"no level \"{id}\"");
                var copy = PackSerializer.Clone(e.Pack);
                pack.Levels.AddRange(copy.Levels);
                pack.WaveSets.AddRange(copy.WaveSets ?? []);
                roots.AddRange(LevelRoots(copy));
            }
            foreach (var id in UnitClosure(roots, Unit).OrderBy(x => x, StringComparer.Ordinal))
                pack.Units.Add(PackSerializer.Clone(units[id].Pack).Units[0]);
            var json = JObject.FromObject(pack);
            var keys = new HashSet<string>(json.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String)
                .Select(v => (string)v).Where(s => s.StartsWith("asset:", StringComparison.Ordinal)).Select(s => s.Substring(6)), StringComparer.Ordinal);
            foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!assets.TryGetValue(key, out var def)) continue;
                pack.Assets[key] = Clone(def);
                if (assetFiles != null && def.Path != null && File.Exists(Path.Combine(Root, def.Path))) assetFiles[def.Path.Replace('\\', '/')] = Path.Combine(Root, def.Path);
            }
            pack.Extra?.Remove(MetaField);
            return pack;
        }

        /// <summary>Unit refs a level's nights name (spawns and troops).</summary>
        private static IEnumerable<string> LevelRoots(DesignPack item)
        {
            foreach (var ws in item.WaveSets ?? [])
            {
                foreach (var n in ws?.Nights ?? [])
                    foreach (var s in n?.Spawns ?? []) if (s?.Unit != null) yield return s.Unit;
                foreach (var t in ws?.Troops ?? []) if (t?.Unit != null) yield return t.Unit;
            }
        }

        /// <summary>Library unit ids reachable from the refs (behaviour parameters included).</summary>
        public static HashSet<string> UnitClosure(IEnumerable<string> refs, Func<string, UnitDef> find)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(refs);
            while (queue.Count > 0)
            {
                var r = ContentRef.Parse(queue.Dequeue());
                if (r.Kind != RefKind.LocalUnit || find(r.Id) is not { } unit || !keep.Add(r.Id)) continue;
                foreach (var b in unit.Behaviours ?? [])
                    foreach (var v in b?.Params?.Values ?? [])
                        if (v?.Type == JTokenType.String) queue.Enqueue((string)v);
            }
            return keep;
        }

        // ---------------------------------------------------------------- migration

        /// <summary>
        /// Imports every pack folder under packsDir (the old per-pack layout) into the library, then moves the folder to
        /// movedDir (kept as a backup, never deleted). Returns one line per pack.
        /// </summary>
        public List<string> MigratePacks(string packsDir, string movedDir, Func<string, bool> skip = null)
        {
            var log = new List<string>();
            if (!Directory.Exists(packsDir)) return log;
            foreach (var folder in Directory.GetDirectories(packsDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                string file = Path.Combine(folder, "pack.json");
                if (!File.Exists(file) || skip?.Invoke(Path.GetFileName(folder)) == true) continue;
                var issues = new List<ValidationIssue>();
                DesignPack pack;
                try { pack = PackSerializer.Read(File.ReadAllText(file), issues); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { log.Add($"{Path.GetFileName(folder)}: can't be read ({e.Message}); left in place"); continue; }
                if (pack == null) { log.Add($"{Path.GetFileName(folder)}: {issues.FirstOrDefault()?.Message}; left in place"); continue; }
                var result = Import(pack, folder, pack.Id ?? Path.GetFileName(folder));
                Directory.CreateDirectory(movedDir);
                string target = Path.Combine(movedDir, Path.GetFileName(folder));
                if (Directory.Exists(target)) target += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.Move(folder, target);
                log.Add($"{Path.GetFileName(folder)}: {result.Summary()} (pack kept in {Path.GetFileName(movedDir)}/{Path.GetFileName(target)})");
            }
            return log;
        }

        // ---------------------------------------------------------------- helpers

        public static DesignPack NewPack(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Format = DesignPack.FormatName,
            Version = DesignPack.CurrentVersion,
        };

        private static AssetDef Clone(AssetDef def) => def == null ? null : JObject.FromObject(def).ToObject<AssetDef>();

        private static string Compact(object o)
        {
            var s = new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore, Formatting = Newtonsoft.Json.Formatting.None };
            return Newtonsoft.Json.JsonConvert.SerializeObject(o, s);
        }

        /// <summary>A stable short hash of a string (for change detection).</summary>
        public static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(8).Select(b => b.ToString("x2")));
        }
    }
}
