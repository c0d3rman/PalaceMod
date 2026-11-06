using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PalaceMod.Builder;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The Builder feature: custom units and wave sets from design packs (docs/builder-design.md).

    BuilderManager is partial so each work package keeps its Harmony patches in its own file under
    Features/Builder/<Package>/BuilderManager.<Package>.cs (patch classes must be nested in the manager). It installs
    the core services in Awake (it runs before default-order managers), so other packages can replace stubs in
    BuilderServices from their own Awake or from a partial's InstallServices hook.
*/
namespace PalaceMod
{
    [ManagerOrder(-30)]
    public partial class BuilderManager : BaseManager
    {
        public override string FeatureSection => "Builder";
        public override string FeatureDescription =>
            "Custom units and wave sets from design packs in BepInEx/PalaceBuilder. Custom runs never touch your scores or saves.";

        protected override bool HasEnabledSwitch => false; // a menu: custom runs only start from it

        // Session guards must outlive a mid-run switch-off, so patches stay applied and check Feature<BuilderManager>.On.
        protected override bool UnpatchWhenDisabled => false;

        internal static Catalog CatalogImpl { get; private set; }
        internal static PackRepository Repository { get; private set; }
        internal static UnitFactory Factory { get; private set; }
        internal static WaveInjector Injector { get; private set; }
        internal static SessionLauncher Launcher { get; private set; }

        private float nextPackPoll;
        private bool packsLoaded;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            GameplayFeatures.RegisterPart("Builder: custom runs");
            CatalogImpl = new Catalog();
            Repository = new PackRepository(() => Catalog.GameReady ? CatalogImpl : null);
            CatalogImpl.PackUnitExists = (packId, unitId) => Repository.Find(packId)?.Pack?.FindUnit(unitId) != null;
            Factory = new UnitFactory(CatalogImpl, Repository.Find);
            Injector = new WaveInjector(Factory);
            Launcher = new SessionLauncher();
            BuilderServices.Catalog = CatalogImpl;
            BuilderServices.Packs = Repository;
            BuilderServices.Units = Factory;
            BuilderServices.Waves = Injector;
            BuilderServices.Sessions = Launcher;
            Repository.Changed += () => { if (!BuilderServices.InSession) Factory.Clear(); };
            SceneManager.sceneLoaded += OnSceneLoaded;
            RegisterDevCommands();
        }

        public override void Update()
        {
            // Packs are validated against the catalog, which needs the game's level data (there from the main menu on)
            // and the troop buildings, loaded in the background so the menu doesn't stall.
            if (!packsLoaded)
            {
                if (!Catalog.GameReady || !TroopIndex.Prefetch()) return;
                packsLoaded = true;
                Repository.Refresh();
            }
            if (Time.unscaledTime >= nextPackPoll)
            {
                nextPackPoll = Time.unscaledTime + 2f;
                if (!BuilderServices.InSession) Repository.RefreshIfChanged();
            }
        }

        protected override void OnFeatureDisabled()
        {
            // A running custom run keeps its isolation until the player leaves the map; only new launches are blocked.
            if (!BuilderServices.InSession) Factory.Clear();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // SceneTransitionManager loads every menu and level additively (then unloads the old one), so mode tells us
            // nothing; skip only the persistent UI scene.
            if (scene.name == (SceneTransitionManager.instance?.uiScene ?? "_UI")) return;
            try
            {
                Launcher.OnSceneLoaded(scene);
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Error($"Scene load handling failed", e);
            }
        }

        // ------------------------------------------------------------------ dev commands

        private void RegisterDevCommands()
        {
            DevCommands.Register("builder",
                "builder dump|packs|validate <pack>|play <pack> <waveSet>|end|status|live|spike <scan|weapon|material>   Builder tools", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: builder dump|packs|validate|play|end|status|live|spike");
                switch (args[0].ToLowerInvariant())
                {
                    case "dump":
                    {
                        string path = args.Length > 1 ? args[1] : Path.Combine(Repository.Root, "catalog.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, CatalogImpl.ToJson());
                        return $"wrote {path}: {CatalogImpl.Units.Count} units ({CatalogImpl.Units.Count(u => u.Side == UnitSide.Enemy)} enemies), " +
                            $"{CatalogImpl.Weapons.Count} weapons, {CatalogImpl.Hosts.Count} hosts";
                    }
                    case "packs":
                        Repository.Refresh();
                        return "\n" + string.Join("\n", Repository.Packs.Select(p =>
                            $"{p.Id} [{p.Source}] units={p.Pack?.Units.Count ?? 0} waveSets={p.Pack?.WaveSets.Count ?? 0} " +
                            $"errors={p.Issues.Count(i => i.Severity == Severity.Error)} warnings={p.Issues.Count(i => i.Severity == Severity.Warning)}"));
                    case "validate":
                    {
                        var entry = FindPack(args);
                        var issues = entry.Pack == null ? entry.Issues.ToList() : Repository.Validate(entry.Pack);
                        return issues.Count == 0 ? $"{entry.Id}: no issues" : "\n" + string.Join("\n", issues);
                    }
                    case "play":
                    {
                        var entry = FindPack(args);
                        if (entry.Pack == null) throw new InvalidOperationException($"{entry.Id} couldn't be read: {string.Join("; ", entry.Issues)}");
                        string waveSet = args.Length > 2 ? args[2] : entry.Pack.WaveSets.FirstOrDefault()?.Id;
                        if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = entry.Pack, WaveSetId = waveSet }, out string error))
                            throw new InvalidOperationException(error);
                        return $"launching {entry.Id}/{waveSet}";
                    }
                    case "end":
                        BuilderServices.Sessions.End();
                        return "session ended";
                    case "status":
                    {
                        var s = BuilderServices.Sessions.Current;
                        var spawner = EnemySpawner.instance;
                        return (s == null ? "no session" : $"session {s.Pack.Id} waveSet={s.WaveSet?.Id} host={s.HostScene}") +
                            $"; scene={SceneManager.GetActiveScene().name}; impact={(GameplayImpact.Any ? string.Join(", ", GameplayImpact.Reasons) : "none")}" +
                            (spawner == null ? "" : $"; spawner waves={spawner.WaveCount} wave={spawner.Wavenumber} startGold={spawner.goldBalanceAtStart}") +
                            $"; perk level={PerkManager.instance?.level} xp={PerkManager.instance?.xp}";
                    }
                    case "live":
                        return DescribeLiveEnemies();
                    case "spike":
                        return Spike(args.Length > 1 ? args[1] : "");
                    default:
                        throw new ArgumentException("unknown builder subcommand");
                }
            });
        }

        private static PackEntry FindPack(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("needs a pack id (`builder packs` lists them)");
            if (Repository.Packs.Count == 0) Repository.Refresh();
            return Repository.Packs.FirstOrDefault(p => p.Id == args[1]) ?? throw new ArgumentException($"no pack \"{args[1]}\"");
        }

        /// <summary>Live enemies grouped by name with stats, so a test can confirm spawned overrides.</summary>
        private static string DescribeLiveEnemies()
        {
            var enemies = TagManager.instance == null ? [] : UnityEngine.Object.FindObjectsOfType<Hp>()
                .Where(h => h.TaggedObj != null && h.TaggedObj.Tags.Contains(TagManager.ETag.EnemyOwned) && h.GetComponent<PathfindMovementEnemy>() != null).ToList();
            if (enemies.Count == 0) return "no live enemies";
            return "\n" + string.Join("\n", enemies.GroupBy(e => e.gameObject.name).Select(g =>
                $"{g.Count()}x {g.Key}: {UnitFactory.Describe(g.First().gameObject)} hp={g.First().HpValue:0.#}/{g.First().maxHp:0.#} elite={g.First().Elite}"));
        }

        // Feasibility spikes from docs/builder-design.md, kept as dev commands so they can be rerun after game updates.
        private static string Spike(string which)
        {
            switch (which)
            {
                case "scan":
                {
                    var astar = AstarPath.active ?? throw new InvalidOperationException("no AstarPath (load a level)");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    astar.Scan();
                    sw.Stop();
                    return $"AstarPath.Scan() of {astar.graphs.Length} graphs ({string.Join(", ", astar.graphs.Select(g => g?.name))}) took {sw.ElapsedMilliseconds} ms";
                }
                case "weapon":
                {
                    // A custom unit's weapon must be a clone: compare it with the vanilla prefab's.
                    var lines = new List<string>();
                    foreach (var entry in Repository.Packs.Where(p => p.Pack != null && !p.HasErrors))
                        foreach (var u in entry.Pack.Units.Where(u => u.Weapon != null))
                        {
                            var template = Factory.Resolve(entry.Pack, u.Id, out _);
                            var vanilla = CatalogImpl.FindUnit(ContentRef.Parse(u.Base).Id)?.Prefab;
                            var tw = template?.GetComponentInChildren<AutoAttack>(true)?.weapon;
                            var vw = vanilla?.GetComponentInChildren<AutoAttack>(true)?.weapon;
                            lines.Add($"{entry.Id}/{u.Id}: template weapon {tw?.name}#{tw?.GetInstanceID()} damage={Dmg(tw)}; " +
                                $"vanilla weapon {vw?.name}#{vw?.GetInstanceID()} damage={Dmg(vw)}; shared={(tw != null && tw == vw)}");
                        }
                    return lines.Count == 0 ? "no custom units with weapon overrides" : "\n" + string.Join("\n", lines);
                    static string Dmg(Weapon w) => w == null ? "-" : string.Join(",", w.directDamage.Select(d => $"+{d.damageAdded}x{d.damageMultiplyer}"));
                }
                case "material":
                {
                    var unit = CatalogImpl.FindUnit("E Racer") ?? CatalogImpl.Units.First(u => u.Side == UnitSide.Enemy);
                    var lines = new List<string>();
                    foreach (var r in unit.Prefab.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials.Where(m => m != null))
                        {
                            var props = new List<string>();
                            for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                            {
                                string p = m.shader.GetPropertyName(i);
                                if (m.shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Color) props.Add($"{p}={m.GetColor(p)}");
                                else if (m.shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture) props.Add($"{p}={(m.GetTexture(p) == null ? "none" : m.GetTexture(p).name)}");
                            }
                            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                            lines.Add($"{r.GetType().Name} {r.name}: material {m.name} shader {m.shader.name} [{string.Join(" ", props)}] " +
                                $"mesh={mesh?.name} vertexColors={(mesh != null && mesh.isReadable ? mesh.colors.Length.ToString() : "unreadable")} gpuInstanced={r.GetComponent<GPUInstanced>() != null}");
                        }
                    return $"{unit.Id}:\n" + string.Join("\n", lines);
                }
                default:
                    throw new ArgumentException("usage: builder spike scan|weapon|material");
            }
        }
    }
}
