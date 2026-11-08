using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Builder.Editor
{
    /// <summary>
    /// The property panel for one custom unit. Stats are generated from StatsDef (see Schema), each showing the base
    /// unit's value until overridden; the rest are the model's typed fields: base prefab, weapon and damage modifiers,
    /// tags, visuals and behaviour modules.
    /// </summary>
    internal sealed class UnitPage : EditorPage
    {
        public int Index;

        public UnitPage() : base("Edit Unit")
        {
            Frame.SetContent(Page(Build));
        }

        private UnitDef Unit => Index >= 0 && Index < (Pack?.Units.Count ?? 0) ? Pack.Units[Index] : null;
        private string P => $"units[{Index}]";
        private CatalogUnit BaseUnit => EditorCatalog.Catalog?.FindUnit(ContentRef.Parse(Unit?.Base).Id);

        private void Edit(string label, Action<UnitDef> change, bool structural = false, string coalesce = null)
        {
            int i = Index;
            Draft.Edit(label, p => change(p.Units[i]), structural, coalesce);
        }

        private void Build(ModPage page)
        {
            var u = Unit;
            if (u == null) { page.Text("This unit no longer exists."); return; }
            Frame.SetTitle(string.IsNullOrWhiteSpace(u.Name) ? "Edit Unit" : u.Name);
            BuildIdentity(page, u);
            BuildStats(page);
            BuildWeapon(page, u);
            BuildTags(page, u);
            BuildVisuals(page, u);
            BuildBehaviours(page, u);
            BuildActions(page, u);
        }

        /// <summary>One section of this page, for the Enemy Studio's inspector tabs: "identity", "stats", "weapon",
        /// "tags", "visuals", "behaviours", "actions".</summary>
        internal void BuildSection(ModPage page, params string[] sections)
        {
            BuildInto(page, p =>
            {
                var u = Unit;
                if (u == null) { p.Text("This unit no longer exists."); return; }
                foreach (string section in sections)
                switch (section)
                {
                    case "identity": BuildIdentity(p, u); break;
                    case "stats": BuildStats(p); break;
                    case "weapon": BuildWeapon(p, u); break;
                    case "tags": BuildTags(p, u); break;
                    case "visuals": BuildVisuals(p, u); break;
                    case "behaviours": BuildBehaviours(p, u); break;
                    case "actions": BuildActions(p, u); break;
                }
            });
        }

        // ---- Identity ----------------------------------------------------------------------------------------------

        private void BuildIdentity(ModPage page, UnitDef u)
        {
            page.Header("Unit");
            Text(page, "Name", P + ".name", () => Unit?.Name, (p, v) =>
            {
                var unit = p.Units[Index];
                // A fresh unit's id follows its name until the id is used elsewhere.
                string oldDerived = Ids.FromName(unit.Name ?? "");
                unit.Name = v;
                // Library ids never change: other levels refer to the enemy by it.
                if (v != null && !Draft.IsLibrary && (unit.Id == oldDerived || unit.Id.StartsWith(oldDerived + "-")) && !Referenced(p, unit.Id))
                    unit.Id = EditorCatalog.UniqueId(Ids.FromName(v), p.Units.Where(o => o != unit).Select(o => o.Id));
            }, "Shown in the night preview and tooltips.");
            if (!Draft.IsLibrary) Text(page, "Id", P + ".id", () => Unit?.Id, (p, v) =>
            {
                var unit = p.Units[Index];
                if (v == null || v == unit.Id) return;
                RenameRefs(p, unit.Id, v);
                unit.Id = v;
            }, "How wave sets refer to this unit: lowercase letters, digits, '.', '_' or '-'. Renaming updates this pack's references.", isId: true);
            Choice(page, "Base unit", P + ".base", EditorCatalog.Units(u.Side).Select(c => (ContentRef.Vanilla(c.Id), c.DisplayName)).ToList(),
                () => Unit?.Base, (p, v) => p.Units[Index].Base = v,
                "The vanilla unit this one is built from: its model, animations and every stat you don't override. Apply for the full list.", structural: true,
                picker: () => Picker.One("Base Unit", BaseItems(Pack, Unit.Side), ContentRef.Parse(Unit.Base).Id,
                    id => Edit("Change base unit", unit => unit.Base = ContentRef.Vanilla(id), structural: true)),
                display: () => BaseUnit?.DisplayName ?? ContentRef.Parse(Unit?.Base).Id ?? "none");
            // Player units aren't in the catalog yet (they live inside building prefabs), so only show Side for packs
            // that already have one.
            if (u.Side != UnitSide.Enemy) Choice(page, "Side", P + ".side", [(UnitSide.Enemy, "Enemy"), (UnitSide.Player, "Player")], () => Unit?.Side,
                (p, v) => p.Units[Index].Side = v, "Enemies come in wave sets. Player units stand in for the troops buildings train (a wave set's Troops).");
            Text(page, "Description", P + ".description", () => Unit?.Description, (p, v) => p.Units[Index].Description = v,
                "One or two sentences for its tooltip.", 300, multiline: true);
        }

        private static bool Referenced(DesignPack pack, string unitId) =>
            pack.WaveSets.Any(w => w.Nights.Any(n => n.Spawns.Any(s => IsRef(pack, s.Unit, unitId))) || (w.Troops?.Any(t => IsRef(pack, t?.Unit, unitId)) ?? false)) ||
            pack.Units.Any(o => o.Behaviours?.Any(b => IsRef(pack, b.GetString("unit"), unitId)) == true);

        /// <summary>Whether a unit ref names this pack's unit: "id", "unit:id" or "&lt;this pack&gt;/id".</summary>
        private static bool IsRef(DesignPack pack, string reference, string unitId)
        {
            if (reference == null) return false;
            var r = ContentRef.Parse(reference);
            return r.Id == unitId && (r.Kind == RefKind.LocalUnit || (r.Kind == RefKind.PackUnit && r.Pack == pack.Id));
        }

        private static void RenameRefs(DesignPack pack, string from, string to)
        {
            foreach (var s in pack.WaveSets.SelectMany(w => w.Nights).SelectMany(n => n.Spawns))
                if (IsRef(pack, s.Unit, from)) s.Unit = to;
            foreach (var t in pack.WaveSets.SelectMany(w => w.Troops ?? []).Where(t => t != null))
                if (IsRef(pack, t.Unit, from)) t.Unit = to;
            foreach (var b in pack.Units.Where(o => o.Behaviours != null).SelectMany(o => o.Behaviours))
                if (IsRef(pack, b.GetString("unit"), from)) b.Params["unit"] = to;
        }

        internal static IEnumerable<Picker.Item> BaseItems(DesignPack pack, string side)
        {
            var units = EditorCatalog.Units(side);
            foreach (var group in units.GroupBy(c => c.Flying ? "Flying" : c.Large ? "Large" : "Ground").OrderBy(g => g.Key == "Ground" ? 0 : g.Key == "Large" ? 1 : 2))
            {
                yield return new Picker.Item { Header = true, Label = group.Key };
                foreach (var c in group)
                    yield return new Picker.Item
                    {
                        Id = c.Id, Label = c.DisplayName, Icon = c.Icon,
                        Value = $"HP {EditorCatalog.Num(c.MaxHp)}",
                        Tooltip = (string.IsNullOrWhiteSpace(c.Description) ? "" : c.Description + "\n") + EditorCatalog.Stats(c) + $"\nId: {c.Id}",
                    };
            }
        }

        // ---- Stats -------------------------------------------------------------------------------------------------

        private void BuildStats(ModPage page)
        {
            page.Header("Stats");
            foreach (var (key, property, spec) in Schema.StatFields())
            {
                string path = $"{P}.stats.{key}";
                Number(page, spec, path,
                    get: () => Unit?.Stats == null ? null : ToFloat(property.GetValue(Unit.Stats)),
                    set: (p, v) =>
                    {
                        var unit = p.Units[Index];
                        unit.Stats ??= new StatsDef();
                        property.SetValue(unit.Stats, property.PropertyType == typeof(int?) ? (object)(v is float f ? (int?)Math.Round(f) : null) : v);
                        if (AllNull(unit.Stats)) unit.Stats = null;
                    },
                    baseValue: () => Schema.BaseStat(BaseUnit, key));
            }
        }

        private static float? ToFloat(object value) => value switch { float f => f, int i => i, _ => null };

        private static bool AllNull(StatsDef s) =>
            Schema.StatFields().All(f => f.Property.GetValue(s) == null) && (s.Extra == null || s.Extra.Count == 0);

        // ---- Weapon ------------------------------------------------------------------------------------------------

        private void BuildWeapon(ModPage page, UnitDef u)
        {
            page.Header("Weapon");
            string wp = P + ".weapon";
            var catalog = EditorCatalog.Catalog;
            var weapons = new List<(string, string)> { ("inherit", "Base unit's") };
            if (catalog != null) weapons.AddRange(catalog.Weapons.OrderBy(w => w.Id).Select(w => (ContentRef.Vanilla(w.Id), w.Id)));
            Choice(page, "Weapon", wp + ".base", weapons, () => Unit?.Weapon?.Base ?? "inherit", (p, v) =>
            {
                var unit = p.Units[Index];
                unit.Weapon ??= new WeaponDef();
                unit.Weapon.Base = v;
                TrimWeapon(unit);
            }, "Which weapon the unit attacks with. Another unit's weapon brings its projectile and damage.", structural: true,
                picker: () => Picker.One("Weapon", WeaponItems(), Unit?.Weapon?.Base ?? "inherit", id => Edit("Change weapon", unit =>
                {
                    unit.Weapon ??= new WeaponDef();
                    unit.Weapon.Base = id;
                    TrimWeapon(unit);
                }, structural: true)));
            Number(page, Schema.DamageMult, wp + ".damageMult", () => Unit?.Weapon?.DamageMult, (p, v) =>
            {
                var unit = p.Units[Index];
                unit.Weapon ??= new WeaponDef();
                unit.Weapon.DamageMult = v;
                TrimWeapon(unit);
            }, baseValue: () => 1f);
            Number(page, Schema.ProjectileSpeed, wp + ".projectileSpeed", () => Unit?.Weapon?.ProjectileSpeed, (p, v) =>
            {
                var unit = p.Units[Index];
                unit.Weapon ??= new WeaponDef();
                unit.Weapon.ProjectileSpeed = v;
                TrimWeapon(unit);
            }, baseValue: () => BaseWeapon()?.ProjectileSpeed);
            DamageList(page, "Damage", wp + ".damage", w => w.Damage, (w, list) => w.Damage = list, BaseWeapon()?.Damage);
            if (BaseWeapon()?.Splash?.Count > 0 || u.Weapon?.Splash != null)
                DamageList(page, "Splash", wp + ".splash", w => w.Splash, (w, list) => w.Splash = list, BaseWeapon()?.Splash);
        }

        private CatalogWeapon BaseWeapon()
        {
            var catalog = EditorCatalog.Catalog;
            if (catalog == null) return null;
            string b = Unit?.Weapon?.Base;
            if (b != null && b != "inherit") return catalog.FindWeapon(ContentRef.Parse(b).Id);
            return BaseUnit?.Weapon == null ? null : catalog.FindWeapon(BaseUnit.Weapon);
        }

        private static IEnumerable<Picker.Item> WeaponItems()
        {
            yield return new Picker.Item { Id = "inherit", Label = "Base unit's weapon", Tooltip = "Keep the weapon the base unit has." };
            var catalog = EditorCatalog.Catalog;
            if (catalog == null) yield break;
            foreach (var w in catalog.Weapons.OrderBy(w => w.Id))
            {
                var users = catalog.Units.Where(u => u.Weapon == w.Id).Select(u => u.DisplayName).Distinct().Take(3).ToList();
                yield return new Picker.Item
                {
                    Id = ContentRef.Vanilla(w.Id), Label = w.Id, Value = DamageSummary(w.Damage),
                    Tooltip = (users.Count > 0 ? "Used by " + string.Join(", ", users) + ".\n" : "") + $"Projectile speed {EditorCatalog.Num(w.ProjectileSpeed)}.",
                };
            }
        }

        private static string DamageSummary(List<DamageModDef> mods) =>
            mods == null || mods.Count == 0 ? "no damage" : string.Join(", ", mods.Select(DescribeMod));

        private static string DescribeMod(DamageModDef m)
        {
            string target = m.Tags == null || m.Tags.Count == 0 ? "any" : string.Join("+", m.Tags);
            var parts = new List<string>();
            if (m.Add != 0) parts.Add((m.Add > 0 ? "+" : "") + EditorCatalog.Num(m.Add));
            if (!Mathf_Approx(m.Mult, 1f)) parts.Add("×" + EditorCatalog.Num(m.Mult));
            return $"{target} {string.Join(" ", parts.DefaultIfEmpty("±0"))}";
        }

        private static bool Mathf_Approx(float a, float b) => Math.Abs(a - b) < 0.0001f;

        /// <summary>A damage modifier list: either the base weapon's (shown, read-only) or the unit's own copy.</summary>
        private void DamageList(ModPage page, string title, string path, Func<WeaponDef, List<DamageModDef>> get,
            Action<WeaponDef, List<DamageModDef>> set, List<DamageModDef> baseList)
        {
            var own = Unit?.Weapon == null ? null : get(Unit.Weapon);
            if (own == null)
            {
                Marked(page, $"{title} modifiers", () => Dim("base: ") + DamageSummary(baseList),
                    $"How much damage each hit does, by target. Apply to make your own copy of the {title.ToLowerInvariant()} list and edit it.", path,
                    () => Edit($"Edit {title.ToLowerInvariant()} modifiers", unit =>
                    {
                        unit.Weapon ??= new WeaponDef();
                        set(unit.Weapon, baseList?.Select(m => new DamageModDef { Tags = [.. m.Tags ?? []], Add = m.Add, Mult = m.Mult }).ToList() ?? []);
                    }, structural: true));
                return;
            }
            for (int j = 0; j < own.Count; j++)
            {
                int k = j;
                string mp = $"{path}[{j}]";
                Marked(page, $"{title} {j + 1}: targets", () => TagList(Mod(get, k)?.Tags, "any target"),
                    "The modifier applies to targets that have all these tags. Apply to choose.", mp + ".tags",
                    () => Picker.Many("Targets", TagItems(), tag => Mod(get, k)?.Tags?.Contains(tag) == true,
                        tag => Edit("Change target tags", unit =>
                        {
                            var m = get(unit.Weapon)[k];
                            m.Tags ??= [];
                            if (!m.Tags.Remove(tag)) m.Tags.Add(tag);
                        }), "Pick the tags a target must all have. None means any target."));
                Number(page, Schema.ModAdd, mp + ".add", () => Mod(get, k)?.Add, (p, v) => get(p.Units[Index].Weapon)[k].Add = v ?? 0f, nullable: false,
                    label: $"{title} {j + 1}: bonus");
                Number(page, Schema.ModMult, mp + ".mult", () => Mod(get, k)?.Mult, (p, v) => get(p.Units[Index].Weapon)[k].Mult = v ?? 1f, nullable: false,
                    label: $"{title} {j + 1}: multiplier");
                page.Button($"Remove {title.ToLowerInvariant()} {j + 1}", () => Edit($"Remove {title.ToLowerInvariant()} modifier",
                    unit => get(unit.Weapon).RemoveAt(k), structural: true), "Deletes this modifier.");
            }
            page.Button($"Add {title.ToLowerInvariant()} modifier", () => Edit($"Add {title.ToLowerInvariant()} modifier",
                unit => get(unit.Weapon).Add(new DamageModDef()), structural: true), "Adds a modifier that applies to any target.");
            page.Button($"Use the base {title.ToLowerInvariant()} list", () => Edit($"Reset {title.ToLowerInvariant()} modifiers", unit =>
            {
                set(unit.Weapon, null);
                TrimWeapon(unit);
            }, structural: true), "Drops your list and goes back to the weapon's own.");
        }

        private DamageModDef Mod(Func<WeaponDef, List<DamageModDef>> get, int k)
        {
            var list = Unit?.Weapon == null ? null : get(Unit.Weapon);
            return list != null && k < list.Count ? list[k] : null;
        }

        private static void TrimWeapon(UnitDef unit)
        {
            var w = unit.Weapon;
            if (w != null && (w.Base == null || w.Base == "inherit") && w.Damage == null && w.Splash == null && w.DamageMult == null &&
                w.ProjectileSpeed == null && (w.Extra == null || w.Extra.Count == 0))
                unit.Weapon = null;
        }

        // ---- Tags --------------------------------------------------------------------------------------------------

        private static readonly HashSet<string> MovementTags = ["Flying", "LargeUnit"];

        /// <summary>Tag choices. Movement tags can't be added to or removed from a unit, but damage can target them.</summary>
        private static IEnumerable<Picker.Item> TagItems(Func<string, bool> filter = null, bool unitTags = false) =>
            Enum.GetNames(typeof(TagManager.ETag)).Where(t => !(unitTags && MovementTags.Contains(t)) && (filter?.Invoke(t) ?? true)).OrderBy(t => t)
                .Select(t => new Picker.Item { Id = t, Label = Schema.Words(t), Tooltip = Descriptions.Tag(t) });

        private static string TagList(List<string> tags, string none) =>
            tags == null || tags.Count == 0 ? Dim(none) : string.Join(", ", tags.Select(Schema.Words));

        private void BuildTags(ModPage page, UnitDef u)
        {
            page.Header("Tags");
            var baseTags = BaseUnit?.Tags ?? [];
            page.Text(Dim("Base unit: ") + (baseTags.Count == 0 ? Dim("no tags") : string.Join(", ", baseTags.Select(Schema.Words))));
            string tp = P + ".tags";
            Marked(page, "Add tags", () => TagList(Unit?.Tags?.Add, "none"),
                "Tags change how the game treats the unit, e.g. Armored Against Ranged. Flying and Large come from the base unit.", tp,
                () => Picker.Many("Add Tags", TagItems(t => !baseTags.Contains(t), unitTags: true), t => Unit?.Tags?.Add?.Contains(t) == true,
                    t => EditTags(tags => { if (!tags.Add.Remove(t)) tags.Add.Add(t); tags.Remove.Remove(t); })));
            if (baseTags.Count > 0)
                Marked(page, "Remove tags", () => TagList(Unit?.Tags?.Remove, "none"), "Takes tags away from the base unit.", tp,
                    () => Picker.Many("Remove Tags", TagItems(baseTags.Contains, unitTags: true), t => Unit?.Tags?.Remove?.Contains(t) == true,
                        t => EditTags(tags => { if (!tags.Remove.Remove(t)) tags.Remove.Add(t); tags.Add.Remove(t); })));
        }

        private void EditTags(Action<TagEditDef> change) => Edit("Change tags", unit =>
        {
            unit.Tags ??= new TagEditDef();
            unit.Tags.Add ??= [];
            unit.Tags.Remove ??= [];
            change(unit.Tags);
            if (unit.Tags.Add.Count == 0 && unit.Tags.Remove.Count == 0 && (unit.Tags.Extra == null || unit.Tags.Extra.Count == 0)) unit.Tags = null;
        });

        // ---- Visuals -----------------------------------------------------------------------------------------------

        private void BuildVisuals(ModPage page, UnitDef u)
        {
            page.Header("Look");
            string vp = P + ".visuals";
            var tints = new List<(string, string)> { (null, "None") };
            tints.AddRange(Schema.Tints.Select(t => (t.Hex, t.Name)));
            ModField tint = null;
            tint = Choice(page, "Tint", vp + ".tint", tints, () => Unit?.Visuals?.Tint, (p, v) => SetVisual(p, vis => vis.Tint = v),
                "Colours the unit's body. Left/right picks a preset; apply to type a hex colour like #8fd14f.",
                picker: () => ModTextInput.Begin(tint, Unit?.Visuals?.Tint ?? "#", text =>
                {
                    text = text.Trim();
                    if (text.Length > 0 && !text.StartsWith("#")) text = "#" + text;
                    Draft.Edit("Set tint", p => SetVisual(p, vis => vis.Tint = text.Length <= 1 ? null : text.ToLowerInvariant()));
                }, 7),
                display: () =>
                {
                    string hex = Unit?.Visuals?.Tint;
                    if (hex == null) return Dim("none");
                    string name = Schema.Tints.FirstOrDefault(t => t.Hex == hex).Name ?? hex;
                    return Validator.TryParseColor(hex, out _) ? $"<color={hex}>■</color>  {name}" : Escape(hex);
                });
            Number(page, Schema.Scale, vp + ".scale", () => Unit?.Visuals?.Scale, (p, v) => SetVisual(p, vis => vis.Scale = v), baseValue: () => 1f);
            var assets = (Pack.Assets ?? []).Keys.OrderBy(k => k).ToList();
            if (assets.Count > 0 || u.Visuals?.Icon != null)
            {
                var options = new List<(string, string)> { (null, "Base unit's") };
                options.AddRange(assets.Select(k => (ContentRef.Asset(k), k)));
                Choice(page, "Icon", vp + ".icon", options, () => Unit?.Visuals?.Icon, (p, v) => SetVisual(p, vis => vis.Icon = v),
                    "Night-preview icon: one of the pack's image files (listed under \"assets\" in pack.json).");
            }
        }

        private void SetVisual(DesignPack p, Action<VisualsDef> change)
        {
            var unit = p.Units[Index];
            unit.Visuals ??= new VisualsDef();
            change(unit.Visuals);
            if (IsEmpty(unit.Visuals)) unit.Visuals = null;
        }

        /// <summary>Visuals with nothing set (they can be dropped from the unit).</summary>
        internal static bool IsEmpty(VisualsDef v) =>
            v.Tint == null && v.Scale == null && v.Icon == null && v.Mesh == null && v.MeshScale == null && v.MeshRotY == null && v.MeshRotX == null &&
            v.MeshRotZ == null && v.MeshOffset == null && v.MeshLook == null && v.Motion == null && v.Texture == null &&
            (v.Sounds == null || (v.Sounds.Attack ?? v.Sounds.Hurt ?? v.Sounds.Death) == null) && (v.Extra == null || v.Extra.Count == 0);

        // ---- Behaviours --------------------------------------------------------------------------------------------

        /// <summary>Parameters of each behaviour module, for its fields. Unknown modules show their raw type.</summary>
        private static readonly Dictionary<string, (string Name, string Help)> BehaviourInfo = new()
        {
            [BehaviourTypes.SpawnOnDeath] = ("Spawns on death", "When this unit dies, another unit appears where it fell (like the game's slimes)."),
        };

        private void BuildBehaviours(ModPage page, UnitDef u)
        {
            page.Header("Behaviours");
            var list = u.Behaviours ?? [];
            if (list.Count == 0) page.Text(Dim("None. Behaviours add abilities, like splitting into smaller enemies on death."));
            for (int j = 0; j < list.Count; j++)
            {
                int k = j;
                string bp = $"{P}.behaviours[{j}]";
                var b = list[j];
                string name = b?.Type != null && BehaviourInfo.TryGetValue(b.Type, out var info) ? info.Name : b?.Type ?? "?";
                if (b?.Type == BehaviourTypes.SpawnOnDeath)
                {
                    Marked(page, name, () => EditorCatalog.UnitName(Pack, Unit?.Behaviours?[k]?.GetString("unit")),
                        BehaviourInfo[BehaviourTypes.SpawnOnDeath].Help + " Apply to choose the unit.", bp,
                        () => Picker.One("Spawns on Death", WavePage.UnitItems(Draft, exclude: Unit.Id), Unit.Behaviours[k].GetString("unit"),
                            id => Edit("Change spawned unit", unit => unit.Behaviours[k].Params["unit"] = id, structural: true)));
                }
                else Marked(page, name, () => Dim("not editable here"), "This behaviour isn't known to this version of PalaceMod.", bp, null);
                page.Button($"Remove \"{name}\"", () => Edit("Remove behaviour", unit =>
                {
                    unit.Behaviours.RemoveAt(k);
                    if (unit.Behaviours.Count == 0) unit.Behaviours = null;
                }, structural: true), "Deletes this behaviour.");
            }
            page.Button("Add: spawns on death", () => Picker.One("Spawns on Death", WavePage.UnitItems(Draft, exclude: Unit.Id), null, id =>
                Edit("Add behaviour", unit =>
                {
                    unit.Behaviours ??= [];
                    var bh = new BehaviourDef { Type = BehaviourTypes.SpawnOnDeath };
                    bh.Params["unit"] = id;
                    unit.Behaviours.Add(bh);
                }, structural: true)), BehaviourInfo[BehaviourTypes.SpawnOnDeath].Help);
        }

        // ---- Actions -----------------------------------------------------------------------------------------------

        private void BuildActions(ModPage page, UnitDef u)
        {
            page.Header("Actions");
            if (u.Side == UnitSide.Enemy)
            {
                var test = page.Row("Test play", Dim("2 nights"), () => "Plays two short nights of just this enemy on " +
                    (EditorCatalog.Host(Pack.WaveSets.FirstOrDefault()?.Host)?.DisplayName ?? "Nordfels") + ". Scores and saves aren't recorded." +
                    (Draft.ErrorCount > 0 ? "\nFix the pack's problems first." : ""), () => BuilderEditor.TestUnit(Index));
            }
            page.Button("Duplicate", () =>
            {
                int copyIndex = Pack.Units.Count;
                Draft.Edit("Duplicate unit", p =>
                {
                    var copy = EditorCatalog.Copy(p.Units[Index]);
                    copy.Name = (copy.Name ?? copy.Id) + " Copy";
                    copy.Id = EditorCatalog.UniqueId(Ids.FromName(copy.Name), p.Units.Select(o => o.Id));
                    p.Units.Add(copy);
                }, structural: true);
                Index = copyIndex;
                Frame.Refresh();
            }, "Makes a copy of this unit to vary.");
            Danger(page, "Delete unit", Referenced(Pack, u.Id) ? "Deletes the unit. Wave sets that use it will show a problem until you change them." : "Deletes the unit.", () =>
            {
                Draft.Edit("Delete unit", p => p.Units.RemoveAt(Index), structural: true);
                Frame.Close();
            });
        }
    }
}
