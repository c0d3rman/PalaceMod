using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using PalaceMod.Builder;
using PalaceMod.Builder.Assets;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    WP5 host: installs the AssetProvider, pumps its downloads and decodes, prefetches when packs change or a session
    starts, and registers the `builder-assets` dev command. Infrastructure (no Enabled switch of its own): the Builder
    feature's switch already gates everything that uses assets. Its patches live in BuilderManager.Assets.cs.
*/
namespace PalaceMod
{
    [ManagerOrder(-20)]
    public class BuilderAssetsManager : BaseManager
    {
        internal static AssetProvider Provider { get; private set; }
        private ConfigEntry<bool> downloads;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            downloads = plugin.Config.Bind("Builder", "Download pack assets", true, new ConfigDescription(
                "Lets packs fetch the images, sounds and meshes they link to from the web (checked against the pack's hash, " +
                "then cached). Off: linked assets are skipped and units keep their base look."));
            Provider = new AssetProvider(() => BuilderServices.Packs.Root) { DownloadsAllowed = () => downloads.Value };
            BuilderServices.Assets = Provider;
            BuilderServices.Packs.Changed += OnPacksChanged;
            Provider.AssetsArrived += () =>
            {
                // A download or decode finished: rebuild templates on next use so they pick it up.
                if (!BuilderServices.InSession) BuilderServices.Units.Clear();
            };
            RegisterDevCommands();
        }

        private BuilderSessionInfo lastSession;

        public override void Update()
        {
            // Polled rather than subscribed: WP3 may replace BuilderServices.Sessions after this Awake.
            var session = BuilderServices.Sessions.Current;
            if (session != lastSession)
            {
                lastSession = session;
                if (session != null) Provider.Prefetch(BuilderServices.Packs.Find(session.Pack?.Id));
            }
            Provider.Update();
        }

        private static void OnPacksChanged()
        {
            if (!BuilderServices.InSession) Provider.Sweep();
            foreach (var p in BuilderServices.Packs.Packs) Provider.Prefetch(p);
        }

        // ------------------------------------------------------------------ dev commands

        private static void RegisterDevCommands()
        {
            DevCommands.Register("builder-assets",
                "builder-assets check <pack>|status|inspect <vanilla unit>|show <pack> <unit>...|sounds|gltf <file.glb> [dir]|gltf result   Builder asset import tools", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: builder-assets check <pack>|status|inspect <unit>|gltf <file>");
                switch (args[0].ToLowerInvariant())
                {
                    case "check": return Check(args);
                    case "status":
                    {
                        var lines = Provider.Status().ToList();
                        return lines.Count == 0 ? "nothing loaded" : "\n" + string.Join("\n", lines);
                    }
                    case "inspect": return Inspect(string.Join(" ", args.Skip(1)));
                    case "show": return Show(args);
                    case "sounds": return Sounds();
                    case "gltf": return GltfSpike(args);
                    default: throw new ArgumentException("unknown builder-assets subcommand");
                }
            });
        }

        /// <summary>Loads every asset of a pack for each use it's referenced by, and reports the result.</summary>
        private static string Check(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("needs a pack id");
            BuilderServices.Packs.Refresh();
            var entry = BuilderServices.Packs.Find(args[1]) ?? throw new ArgumentException($"no pack \"{args[1]}\"");
            var uses = new Dictionary<string, HashSet<string>>();
            void Use(string r, string use) { if (r != null) (uses.TryGetValue(r, out var s) ? s : uses[r] = []).Add(use); }
            foreach (var u in entry.Pack.Units)
            {
                var v = u.Visuals;
                if (v == null) continue;
                Use(v.Icon, AssetUse.Sprite);
                Use(v.Texture, AssetUse.Texture);
                Use(v.Mesh, AssetUse.Mesh);
                Use(v.Sounds?.Attack, AssetUse.Audio);
                Use(v.Sounds?.Hurt, AssetUse.Audio);
                Use(v.Sounds?.Death, AssetUse.Audio);
            }
            foreach (var key in entry.Pack.Assets.Keys) Use(ContentRef.Asset(key), null);
            var lines = new List<string>();
            foreach (var kv in uses.OrderBy(k => k.Key))
                foreach (var use in kv.Value.Where(x => x != null || kv.Value.Count == 1))
                {
                    var state = Provider.GetState(entry, kv.Key, use, out string message);
                    lines.Add($"{kv.Key} as {use ?? "file"}: {state}{(message == null ? "" : " - " + message)}");
                }
            return "\n" + string.Join("\n", lines);
        }

        /// <summary>Spawns custom units (plus the vanilla base and an elite copy) in a row in front of the player, to
        /// screenshot imported visuals. They're live units: they'll walk off and fight.</summary>
        private static string Show(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("usage: builder-assets show <pack> <unit> [<unit>...]");
            var player = PlayerMovement.instance ?? throw new InvalidOperationException("no player (load a level)");
            var entry = BuilderServices.Packs.Find(args[1]) ?? throw new ArgumentException($"no pack \"{args[1]}\"");
            var origin = player.transform.position + player.transform.forward * 6f;
            var right = Vector3.Cross(Vector3.up, player.transform.forward).normalized;
            var spawned = new List<string>();
            int slot = 0;
            foreach (var unitId in args.Skip(2))
            {
                var template = BuilderServices.Units.Resolve(entry.Pack, unitId, out string error) ?? throw new ArgumentException(error);
                var def = entry.Pack.FindUnit(unitId);
                var vanilla = def == null ? null : BuilderServices.Catalog.FindUnit(ContentRef.Parse(def.Base).Id)?.Prefab;
                foreach (var (prefab, elite) in new[] { (vanilla, false), (template, false), (template, true) })
                {
                    if (prefab == null) continue;
                    var go = UnityEngine.Object.Instantiate(prefab, origin + right * (slot++ * 2.5f), Quaternion.LookRotation(-player.transform.forward));
                    if (elite)
                        foreach (var mr in go.GetComponentsInChildren<MeshRenderer>())
                            if (mr.sharedMaterial != null && mr.sharedMaterial.name == "Enemy") mr.sharedMaterial = ColorAndLightManager.EliteEnemyMaterial;
                    spawned.Add($"{go.name}{(elite ? " (elite)" : "")}");
                }
                slot++;
            }
            return "spawned " + string.Join(", ", spawned);
        }

        /// <summary>Live units with pack sounds and the clips their UnitAudio will play.</summary>
        private static string Sounds()
        {
            var clip = new Func<UnitAudio, string, string>((a, field) =>
                (HarmonyLib.Traverse.Create(a).Field(field).GetValue() as AudioSet.ClipArray)?.clips?.FirstOrDefault()?.name ?? "-");
            var lines = UnityEngine.Object.FindObjectsOfType<BuilderUnitSounds>().Select(m =>
            {
                var a = m.GetComponentInChildren<UnitAudio>();
                return a == null ? $"{m.name}: no UnitAudio" :
                    $"{m.name}: attack={clip(a, "soundOnAttack")} hurt={clip(a, "soundOnDmg")} death={clip(a, "soundOnDeath")}";
            }).ToList();
            return lines.Count == 0 ? "no live units with pack sounds" : "\n" + string.Join("\n", lines);
        }

        /// <summary>A vanilla unit's renderers, materials and shaders (what a mesh or texture swap works with).</summary>
        private static string Inspect(string id)
        {
            var unit = BuilderServices.Catalog.FindUnit(id) ?? throw new ArgumentException($"no vanilla unit \"{id}\"");
            var lines = new List<string> { $"{unit.Id} ({unit.Side}):" };
            var root = unit.Prefab.transform;
            foreach (var t in unit.Prefab.GetComponentsInChildren<Transform>(true))
            {
                int depth = 0;
                for (var p = t; p != root; p = p.parent) depth++;
                var comps = t.GetComponents<Component>().Where(c => c != null && c is not Transform).Select(c => c.GetType().Name);
                string line = $"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")} s={t.localScale.x:0.##} [{string.Join(",", comps)}]";
                var r = t.GetComponent<Renderer>();
                if (r is MeshRenderer or SkinnedMeshRenderer)
                {
                    var mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : t.GetComponent<MeshFilter>()?.sharedMesh;
                    line += $" mesh={mesh?.name} b={mesh?.bounds.size} uv={(mesh != null && mesh.isReadable ? mesh.uv.Length.ToString() : "?")} " +
                        $"mats=[{string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : $"{m.name}/{m.shader.name}{(m.HasProperty("_BaseMap") ? $" baseMap={m.GetTexture("_BaseMap")?.name ?? "none"}" : "")}"))}] enabled={r.enabled}";
                }
                lines.Add(line);
            }
            return string.Join("\n", lines);
        }

        // glTF runtime import spike (docs/builder-design.md, "glTF spike"): loads glTFast and a consumer from a
        // folder by reflection and imports a .glb under a new object in front of the camera. Not shipped code.
        private static string GltfSpike(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("usage: builder-assets gltf <file.glb> [dir with glTFast.dll and consumer.dll] | gltf result");
            var entryType = Type.GetType("SpikeConsumer.SpikeEntry, consumer");
            if (args[1] == "result")
                return entryType == null ? "not loaded" : (string)entryType.GetField("Last").GetValue(null);
            if (entryType == null)
            {
                string dir = args.Length > 2 ? args[2] : throw new ArgumentException("first call needs the dll folder");
                Assembly.LoadFrom(Path.Combine(dir, "glTFast.dll"));
                var consumer = Assembly.LoadFrom(Path.Combine(dir, "consumer.dll"));
                entryType = consumer.GetType("SpikeConsumer.SpikeEntry", true);
            }
            var cam = Camera.main;
            var go = new GameObject("PalaceBuilder glTF spike");
            if (cam != null)
            {
                var ray = new Ray(cam.transform.position, cam.transform.forward);
                go.transform.position = Physics.Raycast(ray, out var hit, 500f) ? hit.point : cam.transform.position + cam.transform.forward * 20f;
            }
            go.transform.localScale = Vector3.one * 3f;
            var template = BuilderServices.Catalog.FindUnit("E Melee")?.Prefab.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials).FirstOrDefault(AssetVisuals.IsBodyMaterial)
                ?? throw new InvalidOperationException("no Enemy material to use as template");
            return (string)entryType.GetMethod("Start").Invoke(null, [args[1], go.transform, template]) + $" at {go.transform.position}";
        }
    }
}
