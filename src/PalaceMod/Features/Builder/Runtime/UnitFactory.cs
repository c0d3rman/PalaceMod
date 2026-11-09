using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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
        /// <summary>Weapon and material clones for in-place application, per unit design and pack revision.</summary>
        private readonly Dictionary<string, Dictionary<Object, Object>> inPlaceClones = [];

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

        /// <summary>A built template by its name ("E Racer [pack/unit]"), e.g. for a savestate's enemy; null if none.</summary>
        internal static GameObject FindTemplate(string name)
        {
            if (templateRoot == null || string.IsNullOrEmpty(name)) return null;
            for (int i = templateRoot.childCount - 1; i >= 0; i--)
            {
                var child = templateRoot.GetChild(i);
                if (child.name == name) return child.gameObject;
            }
            return null;
        }

        /// <summary>Destroys all templates. Only call outside a session: live enemies can reference templates.</summary>
        public void Clear()
        {
            foreach (var (_, t) in templates.Values) if (t != null) Object.Destroy(t);
            foreach (var o in owned) if (o != null) Object.Destroy(o);
            templates.Clear();
            owned.Clear();
            inPlaceClones.Clear();
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

        public bool ApplyTo(GameObject instance, DesignPack context, string unitRef, out string error)
        {
            error = null;
            if (instance == null) { error = "no instance"; return false; }
            if (IsTemplateObject(instance)) { error = "templates can't be modified in place"; return false; }
            var r = ContentRef.Parse(unitRef);
            var pack = r.Kind == RefKind.LocalUnit || (r.Kind == RefKind.PackUnit && r.Pack == context?.Id) ? context
                : r.Kind == RefKind.PackUnit ? findPack(r.Pack)?.Pack : null;
            if (r.Kind != RefKind.LocalUnit && r.Kind != RefKind.PackUnit) { error = $"\"{unitRef}\" isn't a custom unit"; return false; }
            if (pack == null) { error = $"pack \"{r.Pack}\" isn't installed"; return false; }
            var def = pack.FindUnit(r.Id);
            if (def == null) { error = $"no unit \"{r.Id}\" in pack {pack.Id}"; return false; }
            var baseRef = ContentRef.Parse(def.Base);
            var baseUnit = baseRef.Kind == RefKind.Vanilla ? catalog.FindUnit(baseRef.Id) : null;
            if (baseUnit == null) { error = $"{pack.Id}/{def.Id}: unknown base \"{def.Base}\""; return false; }
            if (baseUnit.Side != def.Side) { error = $"{pack.Id}/{def.Id}: base {baseUnit.Id} is a {baseUnit.Side} unit, not {def.Side}"; return false; }

            string key = pack.Id + "/" + def.Id + "|" + PackSerializer.WriteCompact(pack);
            if (!inPlaceClones.TryGetValue(key, out var clones)) inPlaceClones[key] = clones = [];
            var notes = new List<string>();
            Apply(instance, pack, def, clones, notes, inPlace: true);
            if (notes.Count > 0) error = string.Join("; ", notes);
            return true;
        }

        private GameObject GetTemplate(DesignPack pack, UnitDef def, out string error)
        {
            error = null;
            string key = pack.Id + "/" + def.Id;
            // Rebuild when anything in the pack changed (a unit can reference others, e.g. through SpawnOnDeath).
            string hash = PackSerializer.WriteCompact(pack);
            // A scenery model is drawn with the loaded map's materials: rebuild it on each map.
            if (PalaceMod.Builder.Assets.AssetVisuals.SceneDependent(def.Visuals)) hash += "|scene " + SceneManager.GetActiveScene().handle;
            if (templates.TryGetValue(key, out var cached) && cached.template != null && cached.hash == hash) return cached.template;
            if (!building.Add(key)) { error = $"unit {key} spawns itself (through SpawnOnDeath)"; return null; }
            try
            {
                // Live clones may still point at the old template (SpawnOnDeath), so retire it until Clear().
                if (cached.template != null) owned.Add(cached.template);
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
            Apply(go, pack, def, [], notes, inPlace: false);
            Log.For<BuilderManager>().Debug($"Built {pack.Id}/{def.Id} from {baseUnit.Id}: {Describe(go)}" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""));
            return go;
        }

        /// <summary>Writes a design's overrides to a template (inPlace false) or to a live, not yet started instance.
        /// clones maps shared assets (weapons, materials) to their per-design copies.</summary>
        private void Apply(GameObject go, DesignPack pack, UnitDef def, Dictionary<Object, Object> clones, List<string> notes, bool inPlace)
        {
            ApplyStats(go, def.Stats);
            ApplyWeapon(go, def.Weapon, clones, notes);
            ApplyTags(go, def.Tags, inPlace);
            ApplyText(go, pack, def);
            ApplyVisuals(go, pack, def.Visuals, clones, notes);
            foreach (var b in def.Behaviours ?? [])
                ApplyBehaviour(go, pack, def, b, notes);
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

        private void ApplyWeapon(GameObject go, WeaponDef w, Dictionary<Object, Object> clones, List<string> notes)
        {
            if (w == null) return;
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
                Weapon clone;
                if (clones.TryGetValue(source, out var cached)) clone = (Weapon)cached;
                else
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

        private static readonly AccessTools.FieldRef<TaggedObject, long> TagBitfield = AccessTools.FieldRefAccess<TaggedObject, long>("tagBitfield");

        private static void ApplyTags(GameObject go, TagEditDef t, bool inPlace)
        {
            if (t == null) return;
            foreach (var tagged in go.GetComponentsInChildren<TaggedObject>(true))
            {
                var remove = (t.Remove ?? []).Select(n => (TagManager.ETag)Enum.Parse(typeof(TagManager.ETag), n)).ToList();
                var add = (t.Add ?? []).Select(n => (TagManager.ETag)Enum.Parse(typeof(TagManager.ETag), n)).ToList();
                // A live instance that's enabled is registered with the TagManager by tag, so go through its API.
                if (inPlace && tagged.isActiveAndEnabled)
                {
                    foreach (var tag in remove) tagged.RemoveTag(tag);
                    foreach (var tag in add) tagged.AddTag(tag);
                    continue;
                }
                foreach (var tag in remove) tagged.Tags.Remove(tag);
                foreach (var tag in add) if (!tagged.Tags.Contains(tag)) tagged.Tags.Add(tag);
                // OnEnable only ORs tags into the cached bitfield, so drop stale bits from an earlier enable.
                if (inPlace) TagBitfield(tagged) = TaggedObject.CreateBitfieldFromTags(tagged.Tags);
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

        private void ApplyVisuals(GameObject go, DesignPack pack, VisualsDef v, Dictionary<Object, Object> clones, List<string> notes)
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
            // Imported mesh, skin texture and sounds (WP5). Before the tint, so the tint colours an imported mesh too.
            PalaceMod.Builder.Assets.AssetVisuals.Apply(go, findPack(pack.Id), v, notes, owned);
            if (v.Tint != null && Validator.TryParseColor(v.Tint, out var rgb))
            {
                var tint = new Color(rgb.r, rgb.g, rgb.b);
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
                        Material clone;
                        if (clones.TryGetValue(m, out var cached)) clone = (Material)cached;
                        else
                        {
                            clone = new Material(m) { name = m.name }; // same name: the game's elite swap looks for "Enemy"
                            MultiplyColor(clone, tint);
                            tintedMaterials.Add((m, clone, tint));
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

        /// <summary>Tinted clones and their sources. ColorAndLightManager recolours the shared unit materials from the
        /// level's colour scheme on level start and re-tints their outlines with the daylight, so clones follow.</summary>
        private static readonly List<(Material source, Material clone, Color tint)> tintedMaterials = [];

        /// <summary>Recopies every tinted clone from its source and re-applies the tint (BuilderManager.Troops.cs calls
        /// this after the game changes its unit material colours).</summary>
        internal static void ResyncTints()
        {
            tintedMaterials.RemoveAll(t => t.source == null || t.clone == null);
            foreach (var (source, clone, tint) in tintedMaterials)
            {
                clone.CopyPropertiesFromMaterial(source);
                MultiplyColor(clone, tint);
            }
        }

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
            var playerMove = go.GetComponentInChildren<PathfindMovementPlayerunit>(true);
            var attack = go.GetComponentInChildren<AutoAttack>(true);
            var icon = go.GetComponent<ScreenMarkerIcon>();
            var tagged = go.GetComponentInChildren<TaggedObject>(true);
            string dmg = attack?.weapon == null ? "-" : string.Join(",", attack.weapon.directDamage.Select(d => $"{(d.requiredTags.Count == 0 ? "any" : string.Join("+", d.requiredTags))}:+{d.damageAdded}x{d.damageMultiplyer}"));
            return $"name=\"{icon?.UnitName}\" maxHp={hp?.maxHp} speed={move?.movementSpeed ?? playerMove?.movementSpeed} cooldown={attack?.cooldownDuration} " +
                $"dmgMult={attack?.DamageMultiplyer} weapon={attack?.weapon?.name}#{attack?.weapon?.GetInstanceID()} damage=[{dmg}] scale={go.transform.localScale.x:0.##} " +
                $"tags=[{(tagged == null ? "" : string.Join(",", tagged.Tags))}]";
        }
    }
}
