using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    Custom units as templates. A template is a copy of the vanilla prefab, made under an inactive DontDestroyOnLoad
    root so none of its Awake/Start/OnEnable run, with the design's overrides applied to the serialized fields. The
    game then spawns it like any prefab: Object.Instantiate copies the overridden fields and the clone initialises
    itself normally. Shared assets are never edited: weapons and materials are cloned per template.

    Two game assumptions break for templates and are patched in BuilderManager.Spawning.cs:
    - Spawn.Update / EnemySpawner.StartSpawning treat any enemyPrefab whose scene is loaded as a scene-placed enemy
      to activate in place (and the DontDestroyOnLoad scene counts as loaded);
    - GetComponentInChildren<T>() skips inactive objects, and templates are inactive in the hierarchy.
*/
namespace PalaceMod.Builder
{
    public sealed class UnitFactory : IUnitFactory
    {
        private readonly ICatalog catalog;
        private readonly Func<string, PackEntry> findPack;

        private GameObject root;
        private readonly Dictionary<string, (string hash, GameObject template)> templates = [];
        private readonly List<Object> owned = [];
        private readonly HashSet<string> building = [];

        /// <summary>Custom unit names/descriptions by I2 term, answered by the TextTranslator patch.</summary>
        internal static readonly Dictionary<string, string> Texts = [];
        internal const string LocaPrefix = "PalaceBuilder/";

        /// <summary>The scene templates live in (DontDestroyOnLoad), or default before the first template.</summary>
        internal static Scene TemplateScene { get; private set; }
        private static Transform templateRoot;

        public UnitFactory(ICatalog catalog, Func<string, PackEntry> findPack)
        {
            this.catalog = catalog;
            this.findPack = findPack;
        }

        public bool IsTemplate(GameObject go) => IsTemplateObject(go);

        internal static bool IsTemplateObject(GameObject go) => go != null && templateRoot != null && go.transform.IsChildOf(templateRoot);

        public void Clear()
        {
            foreach (var (_, t) in templates.Values) if (t != null) Object.Destroy(t);
            foreach (var o in owned) if (o != null) Object.Destroy(o);
            templates.Clear();
            owned.Clear();
            Texts.Clear();
        }

        public GameObject Resolve(DesignPack context, string unitRef, out string error)
        {
            error = null;
            var r = ContentRef.Parse(unitRef);
            switch (r.Kind)
            {
                case RefKind.Vanilla:
                {
                    var u = catalog.FindUnit(r.Id);
                    if (u == null) error = $"no vanilla unit \"{r.Id}\"";
                    return u?.Prefab;
                }
                case RefKind.LocalUnit:
                {
                    var def = context?.FindUnit(r.Id);
                    if (def == null) { error = $"no unit \"{r.Id}\" in pack {context?.Id}"; return null; }
                    return GetTemplate(context, def, out error);
                }
                case RefKind.PackUnit:
                {
                    var pack = r.Pack == context?.Id ? context : findPack(r.Pack)?.Pack;
                    if (pack == null) { error = $"pack \"{r.Pack}\" isn't installed"; return null; }
                    var def = pack.FindUnit(r.Id);
                    if (def == null) { error = $"no unit \"{r.Id}\" in pack {r.Pack}"; return null; }
                    return GetTemplate(pack, def, out error);
                }
                default:
                    error = $"\"{unitRef}\" isn't a unit reference";
                    return null;
            }
        }

        private GameObject GetTemplate(DesignPack pack, UnitDef def, out string error)
        {
            error = null;
            string key = pack.Id + "/" + def.Id;
            // Rebuild when the design changed (the editor re-tests without restarting).
            string hash = Newtonsoft.Json.JsonConvert.SerializeObject(def);
            if (templates.TryGetValue(key, out var cached) && cached.template != null && cached.hash == hash) return cached.template;
            if (!building.Add(key)) { error = $"unit {key} spawns itself (through SpawnOnDeath)"; return null; }
            try
            {
                if (cached.template != null) Object.Destroy(cached.template);
                var template = Build(pack, def, out error);
                if (template == null) { templates.Remove(key); return null; }
                templates[key] = (hash, template);
                return template;
            }
            finally
            {
                building.Remove(key);
            }
        }

        private Transform Root()
        {
            if (root == null)
            {
                root = new GameObject("PalaceBuilder Templates");
                root.SetActive(false);
                Object.DontDestroyOnLoad(root);
                TemplateScene = root.scene;
                templateRoot = root.transform;
            }
            return root.transform;
        }

        private GameObject Build(DesignPack pack, UnitDef def, out string error)
        {
            error = null;
            var baseRef = ContentRef.Parse(def.Base);
            var baseUnit = baseRef.Kind == RefKind.Vanilla ? catalog.FindUnit(baseRef.Id) : null;
            if (baseUnit == null) { error = $"{pack.Id}/{def.Id}: unknown base \"{def.Base}\""; return null; }
            if (baseUnit.Side != def.Side) { error = $"{pack.Id}/{def.Id}: base {baseUnit.Id} is a {baseUnit.Side} unit, not {def.Side}"; return null; }

            var go = Object.Instantiate(baseUnit.Prefab, Root());
            go.name = $"{baseUnit.Prefab.name} [{pack.Id}/{def.Id}]";
            var notes = new List<string>();

            ApplyStats(go, def.Stats);
            ApplyWeapon(go, def.Weapon, notes);
            ApplyTags(go, def.Tags);
            ApplyText(go, pack, def);
            ApplyVisuals(go, pack, def.Visuals, notes);
            foreach (var b in def.Behaviours ?? [])
                ApplyBehaviour(go, pack, def, b, notes);

            Plugin.Logger.LogInfo($"Builder: built {pack.Id}/{def.Id} from {baseUnit.Id}: {Describe(go)}" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""));
            return go;
        }

        private static void ApplyStats(GameObject go, StatsDef s)
        {
            if (s == null) return;
            if (s.MaxHp is float hp) foreach (var h in go.GetComponentsInChildren<Hp>(true)) h.maxHp = hp;
            foreach (var m in go.GetComponentsInChildren<PathfindMovementEnemy>(true))
            {
                if (s.MoveSpeed is float v) m.movementSpeed = v;
                if (s.KeepDistance is float k) m.keepDistanceOf = k;
                if (s.AggroTime is float a) m.agroTimeWhenAttackedByPlayer = a;
            }
            foreach (var m in go.GetComponentsInChildren<PathfindMovementPlayerunit>(true))
            {
                if (s.MoveSpeed is float v) m.movementSpeed = v;
                if (s.KeepDistance is float k) m.keepDistanceOf = k;
                if (s.AggroTime is float a) m.agroTimeWhenAttackedByPlayer = a;
            }
            foreach (var a in go.GetComponentsInChildren<AutoAttack>(true))
            {
                if (s.AttackCooldown is float c) a.cooldownDuration = c;
                if (s.AttackRange is float r)
                {
                    float old = a.targetPriorities.Count > 0 ? a.targetPriorities.Max(t => t.range) : 0;
                    foreach (var t in a.targetPriorities) t.range = r;
                    if (a is AutoAttackWithWarning w && old > 0) w.breakTargetingDistance *= r / old;
                }
            }
        }

        private void ApplyWeapon(GameObject go, WeaponDef w, List<string> notes)
        {
            if (w == null) return;
            var clones = new Dictionary<Weapon, Weapon>();
            Weapon replacement = null;
            if (w.Base != null && w.Base != "inherit")
            {
                var r = ContentRef.Parse(w.Base);
                replacement = r.Kind == RefKind.Vanilla ? catalog.FindWeapon(r.Id)?.Asset : null;
                if (replacement == null) notes.Add($"weapon {w.Base} not found, kept the base weapon");
            }
            foreach (var a in go.GetComponentsInChildren<AutoAttack>(true))
            {
                if (w.DamageMult is float dm) a.DamageMultiplyer *= dm;
                var source = replacement ?? a.weapon;
                if (source == null) continue;
                if (!clones.TryGetValue(source, out var clone))
                {
                    clone = Object.Instantiate(source);
                    clone.name = source.name;
                    owned.Add(clone);
                    if (w.Damage != null) clone.directDamage = ToGame(w.Damage);
                    if (w.Splash != null) clone.splashDamage = ToGame(w.Splash);
                    if (w.ProjectileSpeed is float ps) clone.projectileSpeed = ps;
                    clones[source] = clone;
                }
                a.weapon = clone;
            }
        }

        private static List<DamageModifyer> ToGame(List<DamageModDef> defs) => defs.Select(d => new DamageModifyer
        {
            requiredTags = (d.Tags ?? []).Select(t => (TagManager.ETag)Enum.Parse(typeof(TagManager.ETag), t)).ToList(),
            damageAdded = d.Add,
            damageMultiplyer = d.Mult,
        }).ToList();

        private static void ApplyTags(GameObject go, TagEditDef t)
        {
            if (t == null) return;
            foreach (var tagged in go.GetComponentsInChildren<TaggedObject>(true))
            {
                foreach (var name in t.Remove ?? []) tagged.Tags.Remove((TagManager.ETag)Enum.Parse(typeof(TagManager.ETag), name));
                foreach (var name in t.Add ?? [])
                {
                    var tag = (TagManager.ETag)Enum.Parse(typeof(TagManager.ETag), name);
                    if (!tagged.Tags.Contains(tag)) tagged.Tags.Add(tag);
                }
            }
        }

        private static void ApplyText(GameObject go, DesignPack pack, UnitDef def)
        {
            var icon = go.GetComponent<ScreenMarkerIcon>();
            if (icon == null || (def.Name == null && def.Description == null)) return;
            string baseName = icon.UnitName, baseDescription = icon.UnitDescription;
            string key = $"{LocaPrefix}{pack.Id}/{def.Id}";
            icon.locaKey = key;
            // ScreenMarkerIcon asks for "<key> Name" + "<key>" for enemies, "<key>" + "<key> Description" for player units.
            string nameKey = icon.isPlayerUnit ? key : key + " Name";
            string descriptionKey = icon.isPlayerUnit ? key + " Description" : key;
            Texts[nameKey] = def.Name ?? baseName;
            Texts[descriptionKey] = def.Description ?? baseDescription;
        }

        private void ApplyVisuals(GameObject go, DesignPack pack, VisualsDef v, List<string> notes)
        {
            if (v == null) return;
            if (v.Scale is float s && s > 0) go.transform.localScale *= s;
            if (v.Icon != null)
            {
                var entry = findPack(pack.Id);
                var sprite = entry != null ? BuilderServices.Assets.GetSprite(entry, v.Icon) : null;
                if (sprite == null) notes.Add($"icon {v.Icon} not available, kept the base icon");
                else
                {
                    var icon = go.GetComponent<ScreenMarkerIcon>();
                    if (icon != null) icon.sprite = sprite;
                    if (icon is ScreenMarkerIconArachnophobia spider) spider.defaultSprite = spider.arachnophobiaModeSprite = sprite;
                }
            }
            if (v.Mesh != null) notes.Add("custom meshes aren't supported yet");
            if (v.Tint != null && Validator.TryParseColor(v.Tint, out var rgb))
            {
                var tint = new Color(rgb.r, rgb.g, rgb.b);
                var clones = new Dictionary<Material, Material>();
                int tinted = 0;
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;
                    var mats = renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || !IsBodyMaterial(m)) continue;
                        if (!clones.TryGetValue(m, out var clone))
                        {
                            clone = new Material(m) { name = m.name }; // same name: the game's elite swap looks for "Enemy"
                            MultiplyColor(clone, tint);
                            owned.Add(clone);
                            clones[m] = clone;
                        }
                        mats[i] = clone;
                        changed = true;
                    }
                    if (changed) { renderer.sharedMaterials = mats; tinted++; }
                }
                if (tinted == 0) notes.Add("tint: no body material found");
            }
        }

        // Unit bodies use the shared "Enemy" material (enemies) or the player colour materials; weapons/FX use others.
        private static bool IsBodyMaterial(Material m) => m.name == "Enemy" || m.name.StartsWith("Player") || m.name.StartsWith("Ally");

        private static void MultiplyColor(Material m, Color tint)
        {
            foreach (var prop in new[] { "_BaseColor", "_Color" })
            {
                if (!m.HasProperty(prop)) continue;
                m.SetColor(prop, m.GetColor(prop) * tint);
                return;
            }
        }

        private void ApplyBehaviour(GameObject go, DesignPack pack, UnitDef def, BehaviourDef b, List<string> notes)
        {
            switch (b.Type)
            {
                case BehaviourTypes.SpawnOnDeath:
                {
                    var spawn = Resolve(pack, b.GetString("unit"), out string err);
                    if (spawn == null) { notes.Add($"SpawnOnDeath: {err}"); return; }
                    foreach (var hp in go.GetComponentsInChildren<Hp>(true)) hp.enemyToSpawnOnDeath = spawn;
                    break;
                }
                default:
                    notes.Add($"unknown behaviour {b.Type} ignored");
                    break;
            }
        }

        /// <summary>One-line stat summary of a unit GameObject (template or live), for logs and `builder` commands.</summary>
        internal static string Describe(GameObject go)
        {
            var hp = go.GetComponentInChildren<Hp>(true);
            var move = go.GetComponentInChildren<PathfindMovementEnemy>(true);
            var attack = go.GetComponentInChildren<AutoAttack>(true);
            var icon = go.GetComponent<ScreenMarkerIcon>();
            var tagged = go.GetComponentInChildren<TaggedObject>(true);
            string dmg = attack?.weapon == null ? "-" : string.Join(",", attack.weapon.directDamage.Select(d => $"{(d.requiredTags.Count == 0 ? "any" : string.Join("+", d.requiredTags))}:+{d.damageAdded}x{d.damageMultiplyer}"));
            return $"name=\"{icon?.UnitName}\" maxHp={hp?.maxHp} speed={move?.movementSpeed} cooldown={attack?.cooldownDuration} " +
                $"dmgMult={attack?.DamageMultiplyer} weapon={attack?.weapon?.name}#{attack?.weapon?.GetInstanceID()} damage=[{dmg}] scale={go.transform.localScale.x:0.##} " +
                $"tags=[{(tagged == null ? "" : string.Join(",", tagged.Tags))}]";
        }
    }
}
