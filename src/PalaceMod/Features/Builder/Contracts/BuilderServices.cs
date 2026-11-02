using System;
using System.Collections.Generic;
using PalaceMod.Builder.Model;
using UnityEngine;

namespace PalaceMod.Builder
{
    /// <summary>
    /// The Builder's service locator. Packages install their implementation in Awake (BuilderManager runs first, see
    /// its ManagerOrder), e.g. <c>BuilderServices.Assets = new AssetProvider();</c>. Properties are never null: until a
    /// package installs its implementation, a stub answers.
    /// </summary>
    public static class BuilderServices
    {
        private static IPackRepository packs = new NullPackRepository();
        private static ICatalog catalog;
        private static IUnitFactory units;
        private static IWaveInjector waves;
        private static ISessionLauncher sessions = new NullSessionLauncher();
        private static ILevelOverlay overlay = new NullLevelOverlay();
        private static IAssetProvider assets = new NullAssetProvider();
        private static IShareCodec share = new NullShareCodec();
        private static IPackArchive archives = new NullPackArchive();

        /// <summary>WP1 (installed by BuilderManager).</summary>
        public static IPackRepository Packs { get => packs; set => packs = value ?? throw new ArgumentNullException(nameof(value)); }
        /// <summary>WP2 (installed by BuilderManager).</summary>
        public static ICatalog Catalog { get => catalog ?? throw new InvalidOperationException("Builder catalog not installed"); set => catalog = value; }
        /// <summary>WP2 (installed by BuilderManager).</summary>
        public static IUnitFactory Units { get => units ?? throw new InvalidOperationException("Builder unit factory not installed"); set => units = value; }
        /// <summary>WP2 (installed by BuilderManager).</summary>
        public static IWaveInjector Waves { get => waves ?? throw new InvalidOperationException("Builder wave injector not installed"); set => waves = value; }
        /// <summary>WP3. BuilderManager installs a minimal launcher (dev command path); WP3 replaces it.</summary>
        public static ISessionLauncher Sessions { get => sessions; set => sessions = value ?? throw new ArgumentNullException(nameof(value)); }
        /// <summary>WP4b. Stub: reports that level edits aren't supported yet.</summary>
        public static ILevelOverlay Overlay { get => overlay; set => overlay = value ?? throw new ArgumentNullException(nameof(value)); }
        /// <summary>WP5. Stub: returns null (base visuals are kept).</summary>
        public static IAssetProvider Assets { get => assets; set => assets = value ?? throw new ArgumentNullException(nameof(value)); }
        /// <summary>WP6. Stub: encoding/decoding report "not available".</summary>
        public static IShareCodec Share { get => share; set => share = value ?? throw new ArgumentNullException(nameof(value)); }
        /// <summary>WP6. Stub: export throws, import reports "not available".</summary>
        public static IPackArchive Archives { get => archives; set => archives = value ?? throw new ArgumentNullException(nameof(value)); }

        /// <summary>Whether a custom session is running (custom waves/units are live).</summary>
        public static bool InSession => sessions.Current != null;
    }

    // ---------------------------------------------------------------- stubs

    internal sealed class NullPackRepository : IPackRepository
    {
        public string Root => null;
        public IReadOnlyList<PackEntry> Packs => [];
        public PackEntry Find(string packId) => null;
        public void Refresh() { }
        public event Action Changed { add { } remove { } }
        public string Save(DesignPack pack) => throw new InvalidOperationException("Builder pack repository not installed");
        public PackEntry InstallNew(DesignPack pack, string sourceFolder = null) => throw new InvalidOperationException("Builder pack repository not installed");
        public bool Delete(string packId) => false;
        public List<ValidationIssue> Validate(DesignPack pack) => Validator.Validate(pack);
    }

    internal sealed class NullSessionLauncher : ISessionLauncher
    {
        public BuilderSessionInfo Current => null;
        public bool Launch(SessionRequest request, out string error) { error = "custom sessions aren't available"; return false; }
        public void End() { }
        public event Action<BuilderSessionInfo> Started { add { } remove { } }
        public event Action<BuilderSessionInfo> Ended { add { } remove { } }
    }

    internal sealed class NullLevelOverlay : ILevelOverlay
    {
        public void Apply(DesignPack pack, LevelDef level, List<ValidationIssue> issues)
        {
            if (level.BuildSlots != null || level.SpawnLines != null || (level.Props?.Count ?? 0) > 0)
                issues?.Add(new ValidationIssue(Severity.Warning, $"levels.{level.Id}", "level edits aren't supported yet; playing the host map unchanged"));
        }
    }

    internal sealed class NullAssetProvider : IAssetProvider
    {
        public Sprite GetSprite(PackEntry pack, string assetRef) => null;
        public AudioClip GetAudio(PackEntry pack, string assetRef) => null;
        public (Mesh mesh, Material[] materials)? GetMesh(PackEntry pack, string assetRef) => null;
    }

    internal sealed class NullPackArchive : IPackArchive
    {
        public string ExportFolder => null;
        public string Export(PackEntry pack, string path = null) => throw new InvalidOperationException("pack files aren't available yet");
        public PackEntry Import(string path, List<ValidationIssue> issues)
        {
            issues?.Add(new ValidationIssue(Severity.Error, "", "pack files aren't available yet"));
            return null;
        }
        public string ExportThunderstore(PackEntry pack, string path = null) => throw new InvalidOperationException("pack files aren't available yet");
    }

    internal sealed class NullShareCodec : IShareCodec
    {
        public string Encode(DesignPack pack) => throw new NotSupportedException("share codes aren't available yet");
        public DesignPack Decode(string code, List<ValidationIssue> issues)
        {
            issues?.Add(new ValidationIssue(Severity.Error, "", "share codes aren't available yet"));
            return null;
        }
    }
}
