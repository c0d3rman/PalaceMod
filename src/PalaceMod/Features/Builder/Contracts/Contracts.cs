using System;
using System.Collections.Generic;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    Builder extension points. Each work package implements one interface and installs it in BuilderServices from its
    manager's Awake (or a partial of BuilderManager); everyone else calls through BuilderServices, never through a
    concrete class. Until a package lands, BuilderServices holds a stub that degrades gracefully (see Stubs.cs).
    Owners and status: docs/builder-design.md, "Interfaces".

    Threading: everything here is main-thread only (it touches Unity objects).
*/
namespace PalaceMod.Builder
{
    // ---------------------------------------------------------------- WP1: packs on disk

    public enum PackSource
    {
        /// <summary>BepInEx/PalaceBuilder/packs/&lt;id&gt;: the player's own and installed packs; writable.</summary>
        User,
        /// <summary>BepInEx/plugins/&lt;mod&gt;/PalaceBuilder/packs/&lt;id&gt;: shipped by a Thunderstore mod; read-only.</summary>
        Plugin,
        /// <summary>Embedded in PalaceMod (samples); read-only.</summary>
        BuiltIn,
    }

    public sealed class PackEntry
    {
        /// <summary>The parsed pack, or null if it couldn't be read (see Issues).</summary>
        public DesignPack Pack { get; internal set; }
        /// <summary>Folder holding pack.json and assets/; null for built-in packs.</summary>
        public string Folder { get; internal set; }
        public PackSource Source { get; internal set; }
        public bool ReadOnly => Source != PackSource.User;
        /// <summary>Load and validation problems; packs with errors are listed but can't be played.</summary>
        public IReadOnlyList<ValidationIssue> Issues { get; internal set; } = [];
        public bool HasErrors { get { foreach (var i in Issues) if (i.Severity == Severity.Error) return true; return false; } }
        public string Id => Pack?.Id ?? System.IO.Path.GetFileName(Folder ?? "");
    }

    public interface IPackRepository
    {
        /// <summary>Root of the player's packs: BepInEx/PalaceBuilder.</summary>
        string Root { get; }
        IReadOnlyList<PackEntry> Packs { get; }
        PackEntry Find(string packId);
        /// <summary>Rescans all pack folders (also happens automatically when files change).</summary>
        void Refresh();
        /// <summary>Raised after a rescan that changed anything.</summary>
        event Action Changed;
        /// <summary>Writes a user pack to packs/&lt;id&gt;/pack.json (creating the folder). Throws for read-only packs.</summary>
        string Save(DesignPack pack);
        /// <summary>Installs a pack received from elsewhere without ever overwriting: if the id is taken by a different
        /// pack, the copy gets a new id ("&lt;id&gt;-2", ...). Returns the installed pack's entry. Assets are copied
        /// from sourceFolder when given.</summary>
        PackEntry InstallNew(DesignPack pack, string sourceFolder = null);
        /// <summary>Moves a user pack to PalaceBuilder/trash (never deletes outright).</summary>
        bool Delete(string packId);
        /// <summary>Validates against the live catalog; what Refresh stores in PackEntry.Issues.</summary>
        List<ValidationIssue> Validate(DesignPack pack);
    }

    // ---------------------------------------------------------------- WP2: vanilla content

    public sealed class CatalogUnit
    {
        /// <summary>Id used in "vanilla:&lt;id&gt;": the prefab name, e.g. "E Racer".</summary>
        public string Id;
        public string Side;               // UnitSide.Enemy / UnitSide.Player
        public string DisplayName;        // translated, e.g. "Racer"
        public string Description;
        public GameObject Prefab;         // never modify; use IUnitFactory for variants
        public Sprite Icon;
        public float MaxHp, MoveSpeed, AttackCooldown, AttackRange, KeepDistance, AggroTime;
        public string Weapon;             // catalog weapon id, or null
        public List<string> Tags = [];
        public bool Flying, Large;
        /// <summary>Eternal Trials difficulty value (a balance hint), or 0 if unknown.</summary>
        public float Difficulty;
        /// <summary>Player troops: names of the buildings that train it (BuildSlot.buildingName, e.g. "Archery Range").</summary>
        public List<string> Buildings = [];
    }

    public sealed class CatalogWeapon
    {
        public string Id;                 // the Weapon asset name
        public Weapon Asset;              // never modify; IUnitFactory clones it
        public List<DamageModDef> Damage = [];
        public List<DamageModDef> Splash = [];
        public float ProjectileSpeed;
    }

    public sealed class CatalogSpawnLine
    {
        public string Name;               // what SpawnDef.Line refers to
        public Vector3 Center;
        public float Length;
        public bool Flying, SmallGround, BigGround;
    }

    public sealed class CatalogHost
    {
        public string Scene;              // SpawnDef host / WaveSetDef.Host, e.g. "Nordfels"
        public string DisplayName;
        public LevelInfo Info;
        public int StartingGold;
        public List<CatalogSpawnLine> SpawnLines = [];
        /// <summary>The host's vanilla waves as a WaveSetDef (a starting point for the editor).</summary>
        public WaveSetDef VanillaWaves;
    }

    /// <summary>Read-only index of vanilla content. Built lazily from loaded assets; valid from the main menu on.</summary>
    public interface ICatalog : IValidationContext
    {
        IReadOnlyList<CatalogUnit> Units { get; }
        IReadOnlyList<CatalogWeapon> Weapons { get; }
        IReadOnlyList<CatalogHost> Hosts { get; }
        CatalogUnit FindUnit(string id);
        CatalogWeapon FindWeapon(string id);
        CatalogHost FindHost(string scene);
        /// <summary>The whole catalog as JSON (what `builder dump` writes to catalog.json).</summary>
        string ToJson();
    }

    // ---------------------------------------------------------------- WP2: runtime

    public interface IUnitFactory
    {
        /// <summary>
        /// The spawnable GameObject for a unit ref as seen from a pack: a vanilla prefab for "vanilla:", or a template
        /// with the design's overrides applied for custom units. Templates are cached per pack revision, live under an
        /// inactive DontDestroyOnLoad root, and are spawned with Object.Instantiate (the game's Spawn already does
        /// this). Returns null and fills error if the ref can't be resolved.
        /// </summary>
        GameObject Resolve(DesignPack context, string unitRef, out string error);
        /// <summary>Whether go is one of our templates (never activate or modify a template in place).</summary>
        bool IsTemplate(GameObject go);
        /// <summary>
        /// Applies a custom unit's overrides in place to an existing instance of its base unit that hasn't started yet
        /// (e.g. a troop inside a building, on scene load). Weapons and materials are cloned once per unit design and
        /// shared by every instance. Returns false and fills error if the ref isn't a custom unit or doesn't match.
        /// </summary>
        bool ApplyTo(GameObject instance, DesignPack context, string unitRef, out string error);
        /// <summary>Destroys all templates (they're rebuilt on demand). Call when packs change.</summary>
        void Clear();
    }

    public interface IWaveInjector
    {
        /// <summary>Wave set to apply when the next EnemySpawner starts; null restores vanilla waves.</summary>
        void SetPending(DesignPack pack, WaveSetDef waveSet);
        (DesignPack pack, WaveSetDef waveSet) Pending { get; }
        /// <summary>Builds the game's Wave list for a wave set on the loaded host (spawn lines resolved by name).
        /// Fills issues for anything it had to skip.</summary>
        List<Wave> BuildWaves(DesignPack pack, WaveSetDef waveSet, EnemySpawner spawner, List<ValidationIssue> issues);
    }

    // ---------------------------------------------------------------- WP3: sessions

    public sealed class SessionRequest
    {
        public DesignPack Pack;
        /// <summary>Wave set to play (its Host is the map). Either this or LevelId.</summary>
        public string WaveSetId;
        /// <summary>Level overlay to play (stage 2).</summary>
        public string LevelId;
    }

    public sealed class BuilderSessionInfo
    {
        public DesignPack Pack;
        public WaveSetDef WaveSet;
        public LevelDef Level;
        public string HostScene;
        public DateTime StartedUtc;
        /// <summary>The isolated score record for this run (never the vanilla one for the host).</summary>
        public LevelData Scores;
    }

    public interface ISessionLauncher
    {
        /// <summary>The running custom session, or null when playing vanilla.</summary>
        BuilderSessionInfo Current { get; }
        /// <summary>Validates, starts a session and loads its host map. Returns false with a player-facing error.</summary>
        bool Launch(SessionRequest request, out string error);
        /// <summary>Ends the session (restores vanilla waves and progress isolation). Called on leaving the host.</summary>
        void End();
        event Action<BuilderSessionInfo> Started;
        event Action<BuilderSessionInfo> Ended;
    }

    // ---------------------------------------------------------------- WP4b: level overlays

    public interface ILevelOverlay
    {
        /// <summary>Applies a level's edits to the freshly loaded host scene (after Awake, before Start).</summary>
        void Apply(DesignPack pack, LevelDef level, List<ValidationIssue> issues);
    }

    // ---------------------------------------------------------------- WP5: assets

    public enum AssetState
    {
        /// <summary>The ref isn't "asset:&lt;key&gt;", the key isn't in the pack, or the file doesn't exist.</summary>
        Missing,
        /// <summary>Downloading or decoding in the background; ask again later.</summary>
        Loading,
        Ready,
        /// <summary>The file is malformed, too large or of the wrong type (see the message).</summary>
        Failed,
    }

    public interface IAssetProvider
    {
        /// <summary>Sprite for "asset:&lt;key&gt;" in pack, or null (callers then keep the base visuals).</summary>
        Sprite GetSprite(PackEntry pack, string assetRef);
        /// <summary>WAV loads synchronously; OGG decodes in the background, so this is null until it's ready.</summary>
        AudioClip GetAudio(PackEntry pack, string assetRef);
        /// <summary>Mesh plus material(s) to render it with, or null. OBJ meshes come with no materials (an empty
        /// array): the caller renders them with the unit's own material.</summary>
        (Mesh mesh, Material[] materials)? GetMesh(PackEntry pack, string assetRef);

        // Added by WP5 (additive):
        /// <summary>A PNG as a mipmapped, repeating texture (for skins), or null.</summary>
        Texture2D GetTexture(PackEntry pack, string assetRef);
        /// <summary>Whether an asset can be used, with a player-facing reason when it can't (for the editor).
        /// <paramref name="use"/> is one of the AssetUse constants, or null for "any".</summary>
        AssetState GetState(PackEntry pack, string assetRef, string use, out string message);
        /// <summary>Starts downloads and background decodes for a pack's assets (also done automatically when
        /// packs change and when a session starts).</summary>
        void Prefetch(PackEntry pack);
    }

    /// <summary>What an asset is used as (IAssetProvider.GetState).</summary>
    public static class AssetUse
    {
        public const string Sprite = "sprite", Texture = "texture", Audio = "audio", Mesh = "mesh";
    }

    // ---------------------------------------------------------------- WP6: sharing

    public interface IShareCodec
    {
        /// <summary>"TFB1:" + base64url(deflate(compact JSON)). Asset bytes are never embedded.</summary>
        string Encode(DesignPack pack);
        /// <summary>Parses a share code (whitespace tolerated). Returns null and fills issues on failure.</summary>
        DesignPack Decode(string code, List<ValidationIssue> issues);
    }

    /// <summary>
    /// .tfpack files: a zip of pack.json plus the pack's assets/ folder (WP6; added after the freeze, additive).
    /// Imports always go through IPackRepository.InstallNew, so they never overwrite an existing pack.
    /// </summary>
    public interface IPackArchive
    {
        /// <summary>Folder exports go to by default: PalaceBuilder/exports.</summary>
        string ExportFolder { get; }
        /// <summary>Writes the pack and its assets to a .tfpack (default ExportFolder/&lt;id&gt;.tfpack, never
        /// overwriting). Returns the file written. Throws IOException/InvalidOperationException with a player-facing
        /// message on failure.</summary>
        string Export(PackEntry pack, string path = null);
        /// <summary>Imports a .tfpack (or a Thunderstore zip holding one pack) as a new user pack. Returns null and
        /// fills issues on failure; warnings (skipped files, renamed id) are added on success too.</summary>
        PackEntry Import(string path, List<ValidationIssue> issues);
        /// <summary>Writes a Thunderstore-ready zip (manifest.json, README.md, icon.png and
        /// PalaceBuilder/packs/&lt;id&gt;/) to ExportFolder. Returns the file written.</summary>
        string ExportThunderstore(PackEntry pack, string path = null);
    }
}
