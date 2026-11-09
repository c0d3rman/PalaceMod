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
            // Behaviour engine: clones register as entities of this template (BuilderEntity.Start).
            (go.GetComponent<Logic.BuilderEntity>() ?? go.AddComponent<Logic.BuilderEntity>()).TemplateId = def.Id;
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
            Recolour(go, v, clones, notes);
        }

        // ---- Colours ----------------------------------------------------------------------------------------------

        // Unit bodies use the shared "Enemy" material (enemies) or the player colour materials; weapons/FX use others.
        private static bool IsBodyMaterial(Material m) => m.name == "Enemy" || m.name.StartsWith("Player") || m.name.StartsWith("Ally");

        /// <summary>A material's part name, as visuals.colors keys it ("Enemy", "Metal", ...).</summary>
        internal static string PartName(Material m) => m == null ? null : m.name.Replace(" (Instance)", "").Trim();

        /// <summary>A piece of the model, as visuals.colors keys it: its renderer's name ("Bow", "Helmet"), or null for
        /// unnamed or the mod's own attached model.</summary>
        internal static string PieceName(Renderer r)
        {
            string n = r == null ? null : r.name.Replace("(Clone)", "").Trim();
            return string.IsNullOrEmpty(n) || n.StartsWith("PalaceBuilder") ? null : n;
        }

        /// <summary>What a recoloured material gets: a new lit colour (the shade follows it), and/or a tint.</summary>
        private sealed class Recipe
        {
            public Color? Color, Tint;
        }

        /// <summary>
        /// Recolours a unit (visuals.colors and visuals.tint). FlatKit's toon shader draws the lit side in _BaseColor and
        /// the shaded side in _ColorDim, which the level's colour scheme sets to a deep enemy red (ColorAndLightManager.
        /// ApplyColorScheme), so a tint that only multiplies _BaseColor barely shows. A colour replaces both: _BaseColor
        /// becomes it and _ColorDim the shade the scheme would pair with it (ColorMath.ShadeFor). Clones keep the source's
        /// name, so the game's elite swap still finds "Enemy"; BuilderEliteLook then puts the recoloured elite twin in.
        /// </summary>
        private void Recolour(GameObject go, VisualsDef v, Dictionary<Object, Object> clones, List<string> notes)
        {
            Color? tint = v.Tint != null && Validator.TryParseColor(v.Tint, out var t) ? new Color(t.r, t.g, t.b) : null;
            var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in v.Colors ?? [])
                if (kv.Key != null && Validator.TryParseColor(kv.Value, out var c)) colors[kv.Key.Trim()] = new Color(c.r, c.g, c.b);
            if (tint == null && colors.Count == 0) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Shared across in-place applications of one design (clones is per design and pack revision there).
            var paints = paintsByDesign.GetValue(clones, _ => new Dictionary<(Material, string), Material>());
            bool tinted = false;
            var eliteRenderers = new List<Renderer>();
            var eliteSlots = new List<int>();
            var eliteClones = new List<Material>();
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    bool body = IsBodyMaterial(m);
                    string part = PartName(m);
                    // Most specific first: this piece of the model (its renderer), then the material, then "body".
                    Color? color = null;
                    string key = null;
                    string piece = PieceName(renderer);
                    if (piece != null && colors.TryGetValue(piece, out var byPiece)) { color = byPiece; key = piece; }
                    else if (colors.TryGetValue(part, out var byName)) { color = byName; key = part; }
                    else if (body && colors.TryGetValue(ModelRef.BodyPart, out var byBody)) { color = byBody; key = ModelRef.BodyPart; }
                    if (key != null) used.Add(key);
                    Color? tintHere = body ? tint : null;
                    if (color == null && tintHere == null) continue;
                    var cacheKey = (m, key);
                    if (!paints.TryGetValue(cacheKey, out var clone))
                    {
                        clone = new Material(m) { name = m.name }; // same name: the game's elite swap looks for "Enemy"
                        var recipe = new Recipe { Color = color, Tint = tintHere };
                        Paint(clone, m, recipe);
                        recoloured.Add((m, clone, recipe));
                        owned.Add(clone);
                        paints[cacheKey] = clone;
                    }
                    if (tintHere != null) tinted = true;
                    mats[i] = clone;
                    changed = true;
                    if (m.name == "Enemy")
                    {
                        eliteRenderers.Add(renderer);
                        eliteSlots.Add(i);
                        eliteClones.Add(clone);
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }
            if (tint != null && !tinted) notes.Add("tint: no body material found");
            foreach (var k in colors.Keys.Where(k => !used.Contains(k))) notes.Add($"colors: no \"{k}\" part or material on this model");
            if (eliteRenderers.Count > 0)
            {
                var look = go.GetComponent<BuilderEliteLook>() ?? go.AddComponent<BuilderEliteLook>();
                look.renderers = eliteRenderers.ToArray();
                look.slots = eliteSlots.ToArray();
                look.materials = eliteClones.ToArray();
            }
        }

        /// <summary>Recoloured clones and their sources. ColorAndLightManager recolours the shared unit materials from the
        /// level's colour scheme on level start and re-tints their outlines with the daylight, so clones follow.</summary>
        private static readonly List<(Material source, Material clone, Recipe recipe)> recoloured = [];
        private static readonly Dictionary<Material, Material> eliteTwins = [];
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<Object, Object>, Dictionary<(Material, string), Material>> paintsByDesign = new();

        private static void Paint(Material clone, Material source, Recipe r)
        {
            string baseProp = clone.HasProperty("_BaseColor") ? "_BaseColor" : clone.HasProperty("_Color") ? "_Color" : null;
            if (baseProp == null) return;
            if (r.Color is Color target)
            {
                var light = source.HasProperty(baseProp) ? source.GetColor(baseProp) : Color.white;
                target.a = light.a;
                clone.SetColor(baseProp, target);
                if (clone.HasProperty("_ColorDim") && source.HasProperty("_ColorDim"))
                    clone.SetColor("_ColorDim", ColorTools.ShadeFor(target, light, source.GetColor("_ColorDim")));
            }
            if (r.Tint is Color tint)
            {
                clone.SetColor(baseProp, clone.GetColor(baseProp) * tint);
                // Half of it on the shade too, so a tint reads on the shaded side as well.
                if (clone.HasProperty("_ColorDim"))
                {
                    var dim = clone.GetColor("_ColorDim");
                    clone.SetColor("_ColorDim", Color.Lerp(dim, dim * tint, 0.5f));
                }
            }
        }

        /// <summary>The elite look of a recoloured body clone: the same colours with the game's yellow elite outline.</summary>
        internal static Material EliteTwin(Material clone)
        {
            if (clone == null) return null;
            if (eliteTwins.TryGetValue(clone, out var twin) && twin != null) return twin;
            twin = new Material(clone) { name = clone.name };
            if (twin.HasProperty("_OutlineColor")) twin.SetColor("_OutlineColor", Color.yellow);
            return eliteTwins[clone] = twin;
        }

        /// <summary>Recopies every recoloured clone from its source and paints it again (BuilderManager.Troops.cs calls
        /// this after the game changes its unit material colours).</summary>
        internal static void ResyncTints()
        {
            recoloured.RemoveAll(t => t.source == null || t.clone == null);
            foreach (var (source, clone, recipe) in recoloured)
            {
                clone.CopyPropertiesFromMaterial(source);
                Paint(clone, source, recipe);
                if (eliteTwins.TryGetValue(clone, out var twin) && twin != null)
                {
                    twin.CopyPropertiesFromMaterial(clone);
                    if (twin.HasProperty("_OutlineColor")) twin.SetColor("_OutlineColor", Color.yellow);
                }
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

    /// <summary>
    /// On a recoloured enemy template: the game makes an enemy elite by swapping every MeshRenderer whose material is
    /// named "Enemy" to its shared elite material (Spawn, right after Instantiate), which would drop the custom colour.
    /// On Start (after that), an elite gets the recoloured clone's elite twin (same colours, yellow outline) instead;
    /// the hit flash's remembered material follows.
    /// </summary>
    public sealed class BuilderEliteLook : MonoBehaviour
    {
        public Renderer[] renderers;
        public int[] slots;
        public Material[] materials;

        private static readonly HarmonyLib.AccessTools.FieldRef<MaterialFlasherFX, List<MaterialFlasherFX.RendererMaterialPair>> Pairs =
            HarmonyLib.AccessTools.FieldRefAccess<MaterialFlasherFX, List<MaterialFlasherFX.RendererMaterialPair>>("targetRendererMaterialPairs");

        private void Start()
        {
            var hp = GetComponentInChildren<Hp>();
            if (hp == null || !hp.Elite || renderers == null || slots == null || materials == null) return;
            var elite = ColorAndLightManager.EliteEnemyMaterial;
            var swapped = new Dictionary<Renderer, Material>();
            for (int i = 0; i < renderers.Length && i < slots.Length && i < materials.Length; i++)
            {
                var r = renderers[i];
                if (r == null || materials[i] == null) continue;
                var mats = r.sharedMaterials;
                int k = slots[i];
                if (k < 0 || k >= mats.Length || (mats[k] != elite && mats[k] != materials[i])) continue;
                mats[k] = UnitFactory.EliteTwin(materials[i]);
                r.sharedMaterials = mats;
                if (k == 0) swapped[r] = mats[k];
            }
            foreach (var flasher in GetComponentsInChildren<MaterialFlasherFX>(true))
            {
                List<MaterialFlasherFX.RendererMaterialPair> pairs;
                try { pairs = Pairs(flasher); } catch { continue; }
                foreach (var p in pairs ?? [])
                    if (p?.renderer != null && swapped.TryGetValue(p.renderer, out var m)) p.originalMaterial = m;
            }
        }
    }
}
