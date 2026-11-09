using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Enemy Studio inspector's own tabs, made for its narrow panel (short labels, values that wrap rather than run
    over the label):

    - Attack: the weapon (melee or ranged, grouped by whether it fires a projectile), damage multiplier, projectile
      speed, range, cooldown and keep distance, then the damage and splash lists, one row per modifier (a popup edits
      its targets, bonus and multiplier).
    - Tags: the tags the enemy spawns with as chips (click: remove, or restore a removed base tag; hover: what the tag
      does), an add field whose suggestions are grouped by meaning and filtered as you type, and conflicts flagged.
    - Colours (in Look): one row per part of the model (the body, then each other material), each opening the colour
      picker (Core/UI/ModColorPicker.cs), and the subtle tint.
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    internal static class EnemyPanels
    {
        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";
        private static string Warn(string s) => $"<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Warning))}>{s}</color>";
        private static string Esc(string s) => ModFields.TMPEscape(s ?? "");
        private static string Num(float v) => EditorCatalog.Num(v);

        /// <summary>A plain row whose value wraps under/beside its label instead of running over it.</summary>
        private static ModField Line(ModPage page, string label, Func<string> value, Func<string> tip, Action onClick)
        {
            var row = page.Row(label, "", tip, onClick);
            var field = new ModField(row, label, value, arrows: false, page.FieldIndent);
            field.EnableWrap();
            field.Refresh();
            return field;
        }

        /// <summary>A number overriding a base value: dim "(base)" until set; an empty entry goes back to the base.</summary>
        private static void Override(ModPage page, string label, NumberSpec spec, Func<UnitDef, float?> get, Action<UnitDef, float?> set, Func<float?> baseValue,
            string help)
        {
            page.Number(label, () => get(EnemyStudio.Unit) ?? baseValue() ?? spec.Min, v => EnemyStudio.Edit("Set " + label.ToLowerInvariant(), u => set(u, spec.Clamp(v)), coalesce: "panel " + label),
                spec.Min, spec.Max, spec.Step,
                v => get(EnemyStudio.Unit) != null ? spec.Format(v) : Dim(spec.Format(v) + " (base)"),
                () => help + "\nApply to type a value; empty goes back to the base unit's.",
                () => EnemyStudio.Edit("Reset " + label.ToLowerInvariant(), u => set(u, null)));
        }

        private static void Stat(UnitDef u, Action<StatsDef> change)
        {
            u.Stats ??= new StatsDef();
            change(u.Stats);
            if (u.Stats.MaxHp == null && u.Stats.MoveSpeed == null && u.Stats.AttackCooldown == null && u.Stats.AttackRange == null &&
                u.Stats.KeepDistance == null && u.Stats.AggroTime == null && (u.Stats.Extra == null || u.Stats.Extra.Count == 0)) u.Stats = null;
        }

        private static void Weapon(UnitDef u, Action<WeaponDef> change)
        {
            u.Weapon ??= new WeaponDef();
            change(u.Weapon);
            var w = u.Weapon;
            if ((w.Base == null || w.Base == "inherit") && w.Damage == null && w.Splash == null && w.DamageMult == null && w.ProjectileSpeed == null &&
                (w.Extra == null || w.Extra.Count == 0)) u.Weapon = null;
        }

        // ---- Attack -----------------------------------------------------------------------------------------------

        public static void Attack(ModPage page)
        {
            var u = EnemyStudio.Unit;
            if (u == null) return;
            var weapon = EnemyStudio.BaseWeapon();
            var baseUnit = EnemyStudio.BaseUnit;
            bool ranged = weapon != null && weapon.ProjectileSpeed > 0f;
            StudioDock.SmallHeader(page, "Weapon");
            Line(page, "Weapon", () =>
            {
                var w = EnemyStudio.BaseWeapon();
                string name = w?.Id ?? "none";
                string kind = w == null ? "" : w.ProjectileSpeed > 0f ? "ranged" : "melee";
                bool own = EnemyStudio.Unit?.Weapon?.Base is string b && b != "inherit";
                return $"{Esc(name)} {Dim("· " + kind + (own ? "" : " · base unit's"))}";
            }, () => "What it attacks with. A ranged weapon fires a projectile that homes on its target (the archers' and the " +
                "mages' weapons); a melee one hits at once. Apply to choose.", PickWeapon);
            Override(page, "Damage ×", Schema.DamageMult, x => x.Weapon?.DamageMult, (x, v) => Weapon(x, w => w.DamageMult = v), () => 1f,
                "Multiplies all damage it deals (the night's difficulty and elite status still multiply it).");
            if (ranged)
                Override(page, "Projectile speed", Schema.ProjectileSpeed, x => x.Weapon?.ProjectileSpeed, (x, v) => Weapon(x, w => w.ProjectileSpeed = v),
                    () => EnemyStudio.BaseWeapon()?.ProjectileSpeed, "How fast its shots fly, in metres per second. They home on the target.");
            else page.Text(Dim("Melee: no projectile. Pick a ranged weapon above to shoot."));

            StudioDock.SmallHeader(page, "Range and timing");
            Override(page, "Range", Schema.Stats["attackRange"], x => x.Stats?.AttackRange, (x, v) => Stat(x, s => s.AttackRange = v), () => baseUnit?.AttackRange,
                "How far away it picks a target, in metres (every target list of its attack). Ranged enemies: keep it above Keep distance.");
            Override(page, "Cooldown", Schema.Stats["attackCooldown"], x => x.Stats?.AttackCooldown, (x, v) => Stat(x, s => s.AttackCooldown = v), () => baseUnit?.AttackCooldown,
                "Seconds between attacks.");
            Override(page, "Keep distance", Schema.Stats["keepDistance"], x => x.Stats?.KeepDistance, (x, v) => Stat(x, s => s.KeepDistance = v), () => baseUnit?.KeepDistance,
                "How close it walks to what it's heading for before it stops. A ranged enemy stands this far away and shoots; it must be below Range.");
            var e = EnemyStudio.Effective();
            if (ranged && e.AttackRange > 0f && (u.Stats?.KeepDistance ?? baseUnit?.KeepDistance ?? 0f) > e.AttackRange)
                page.Text(Warn("Keep distance is beyond its range: it will stop out of reach."));

            DamageList(page, "Damage", w => w.Damage, (w, l) => w.Damage = l, EnemyStudio.BaseWeapon()?.Damage,
                "What each hit does, by target: a modifier applies when the target has all its tags; damage = (damage + bonus) × multiplier, in order.");
            if ((weapon?.Splash?.Count ?? 0) > 0 || u.Weapon?.Splash != null)
                DamageList(page, "Splash", w => w.Splash, (w, l) => w.Splash = l, weapon?.Splash,
                    "Damage around the hit (melee weapons with an area effect), by target, like the damage list.");
        }

        private static void PickWeapon()
        {
            var catalog = BuilderServices.Catalog;
            if (catalog == null) return;
            var items = new List<Picker.Item> { new() { Id = "inherit", Label = "Base unit's weapon", Tooltip = "Keep the weapon the base unit has." } };
            foreach (var group in catalog.Weapons.GroupBy(w => w.ProjectileSpeed > 0f).OrderBy(g => g.Key ? 0 : 1))
            {
                items.Add(new Picker.Item { Header = true, Label = group.Key ? "Ranged (fires a projectile)" : "Melee (hits at once)" });
                foreach (var w in group.OrderBy(w => w.Id))
                {
                    var users = catalog.Units.Where(x => x.Weapon == w.Id).Select(x => x.DisplayName).Distinct().Take(3).ToList();
                    items.Add(new Picker.Item
                    {
                        Id = ContentRef.Vanilla(w.Id), Label = w.Id, Value = Summary(w.Damage),
                        Tooltip = (users.Count > 0 ? "Used by " + string.Join(", ", users) + ".\n" : "") + (w.ProjectileSpeed > 0f ? $"Projectile speed {Num(w.ProjectileSpeed)}." : "No projectile."),
                    });
                }
            }
            Picker.One("Weapon", items, EnemyStudio.Unit?.Weapon?.Base ?? "inherit",
                id => EnemyStudio.Edit("Change weapon", u => Weapon(u, w => w.Base = id), structural: true));
        }

        private static string Summary(List<DamageModDef> mods) =>
            mods == null || mods.Count == 0 ? "no damage" : string.Join(", ", mods.Select(m => $"{Targets(m)} {Amount(m)}"));

        private static string Targets(DamageModDef m) => m.Tags == null || m.Tags.Count == 0 ? "any" : string.Join(" + ", m.Tags.Select(Schema.Words));

        private static string Amount(DamageModDef m)
        {
            var parts = new List<string>();
            if (m.Add != 0) parts.Add((m.Add > 0 ? "+" : "") + Num(m.Add));
            if (Math.Abs(m.Mult - 1f) > 1e-4f) parts.Add("×" + Num(m.Mult));
            return parts.Count == 0 ? "±0" : string.Join(" ", parts);
        }

        private static void DamageList(ModPage page, string title, Func<WeaponDef, List<DamageModDef>> get, Action<WeaponDef, List<DamageModDef>> set,
            List<DamageModDef> baseList, string help)
        {
            StudioDock.SmallHeader(page, title);
            var own = EnemyStudio.Unit?.Weapon == null ? null : get(EnemyStudio.Unit.Weapon);
            if (own == null)
            {
                var list = baseList ?? [];
                for (int j = 0; j < list.Count; j++)
                {
                    var m = list[j];
                    Line(page, "vs " + Targets(m), () => Dim(Amount(m)), () => help + "\nThe weapon's own list: Edit makes your copy.", null);
                }
                if (list.Count == 0) page.Text(Dim("None"));
                page.Button("Edit " + title.ToLowerInvariant(), () => EnemyStudio.Edit($"Edit {title.ToLowerInvariant()}", u => Weapon(u, w =>
                    set(w, list.Select(x => new DamageModDef { Tags = [.. x.Tags ?? []], Add = x.Add, Mult = x.Mult }).ToList())), structural: true),
                    "Copies the weapon's list so you can change it.");
                return;
            }
            for (int j = 0; j < own.Count; j++)
            {
                int k = j;
                DamageModDef Mod() => EnemyStudio.Unit?.Weapon is { } w && get(w) is { } l && k < l.Count ? l[k] : null;
                Line(page, "vs " + Targets(own[j]), () => Mod() is { } m ? Amount(m) : "", () => help + "\nApply to edit or remove it.", () => EditModifier(title, get, k));
            }
            if (own.Count == 0) page.Text(Dim("None: it deals no " + title.ToLowerInvariant() + "."));
            page.Button("Add " + title.ToLowerInvariant() + " modifier", () =>
            {
                EnemyStudio.Edit($"Add {title.ToLowerInvariant()} modifier", u => Weapon(u, w => get(w).Add(new DamageModDef())), structural: true);
                EditModifier(title, get, own.Count);
            }, "Adds a modifier for any target; then pick its targets and amounts.");
            page.Button("Use the weapon's " + title.ToLowerInvariant(), () => EnemyStudio.Edit($"Reset {title.ToLowerInvariant()}", u => Weapon(u, w => set(w, null)), structural: true),
                "Drops your list and goes back to the weapon's own.");
        }

        private static void EditModifier(string title, Func<WeaponDef, List<DamageModDef>> get, int k)
        {
            DamageModDef Mod() => EnemyStudio.Unit?.Weapon is { } w && get(w) is { } l && k < l.Count ? l[k] : null;
            if (Mod() == null) return;
            void Change(string label, Action<DamageModDef> c, bool structural = false) =>
                EnemyStudio.Edit(label, u => { var l = get(u.Weapon); if (k < l.Count) c(l[k]); }, structural, coalesce: $"mod {title} {k} {label}");
            ModPopup.Open($"{title} {k + 1}", p =>
            {
                p.FieldIndent = 200f;
                Line(p, "Targets", () => Mod() is { } m ? Targets(m) : "", () => "It applies to targets with all these tags (none: any target). Apply to choose.",
                    () => Picker.Many("Targets", TagChoices(), t => Mod()?.Tags?.Contains(t) == true, t => Change("Change target tags", m =>
                    {
                        m.Tags ??= [];
                        if (!m.Tags.Remove(t)) m.Tags.Add(t);
                    }), "Pick the tags a target must all have. None means any target."));
                p.Number("Bonus", () => Mod()?.Add ?? 0f, v => Change("Set bonus damage", m => m.Add = v), Schema.ModAdd.Min, Schema.ModAdd.Max, Schema.ModAdd.Step,
                    v => Num(v), () => Schema.ModAdd.Help);
                p.Number("Multiplier", () => Mod()?.Mult ?? 1f, v => Change("Set damage multiplier", m => m.Mult = v), Schema.ModMult.Min, Schema.ModMult.Max, Schema.ModMult.Step,
                    v => "×" + Num(v), () => Schema.ModMult.Help);
                p.Button("Remove", () =>
                {
                    ModPopup.Current?.Close();
                    EnemyStudio.Edit($"Remove {title.ToLowerInvariant()} modifier", u => { var l = get(u.Weapon); if (k < l.Count) l.RemoveAt(k); }, structural: true);
                }, "Deletes this modifier.");
            }, width: 520f);
        }

        // ---- Tags -------------------------------------------------------------------------------------------------

        /// <summary>Tags grouped by what they mean for an enemy. Tags the game sets itself (AUTO_*, groups, curses) and
        /// player-side ones aren't offered.</summary>
        internal static readonly (string Group, string[] Tags)[] TagGroups =
        [
            ("Defence", ["ArmoredAgainstRanged", "VulnerableVsRanged", "TakesReducedDamageFromPlayerAttacks", "TakesIncreasedDamageFromTowers",
                "FireAndExplosionResistant", "NaturallyVulnerableToSplash", "NaturallyHighHealthTarget"]),
            ("Fighting", ["MeeleFighter", "RangedFighter", "Healer", "SiegeWeapon", "Exploding", "FastMoving", "Boss"]),
            ("Kind", ["Monster", "Humanoid", "Undead", "Vampire"]),
            ("Rarely useful", ["EnemyOwned", "BlockableEnemyProjectile", "MagicProjectile", "OnDeathEnemySpawner", "UnreachableForBigEnemiesDueToLevelDesign", "PracticeTargets"]),
        ];

        /// <summary>Tags a unit can't gain or lose here (its movement graph comes from the base).</summary>
        private static readonly HashSet<string> Fixed = ["Flying", "LargeUnit"];

        /// <summary>Pairs that contradict each other.</summary>
        internal static readonly (string A, string B, string Why)[] Conflicts =
        [
            ("ArmoredAgainstRanged", "VulnerableVsRanged", "both armoured against and vulnerable to ranged attacks: the damage lists apply both"),
            ("MeeleFighter", "RangedFighter", "both a melee and a ranged fighter"),
            ("PlayerOwned", "EnemyOwned", "on both sides: the player's towers and troops ignore it, and enemies may attack it"),
        ];

        public static string TagFilter = "";

        public static void Tags(ModPage page)
        {
            var u = EnemyStudio.Unit;
            if (u == null) return;
            var baseTags = new HashSet<string>(EnemyStudio.BaseUnit?.Tags ?? []);
            var added = new HashSet<string>(u.Tags?.Add ?? []);
            var removed = new HashSet<string>(u.Tags?.Remove ?? []);
            var effective = EnemyStudio.Effective().Tags;

            StudioDock.SmallHeader(page, "Tags");
            var chips = new List<(string tag, string state)>();
            foreach (var t in effective.OrderBy(t => baseTags.Contains(t) ? 0 : 1).ThenBy(t => t)) chips.Add((t, added.Contains(t) && !baseTags.Contains(t) ? "added" : "base"));
            foreach (var t in removed.Where(baseTags.Contains).OrderBy(t => t)) chips.Add((t, "removed"));
            Chips(page, "Tag", chips, c =>
            {
                string desc = Descriptions.Tag(c.tag) ?? "";
                string action = Fixed.Contains(c.tag) ? "Comes from the base unit (its movement); change the base to change it."
                    : c.state == "removed" ? "Removed from the base unit's tags. Click to put it back."
                    : c.state == "added" ? "Added by you. Click to remove." : "From the base unit. Click to remove.";
                return (c.state == "removed" ? $"<s>{Schema.Words(c.tag)}</s>" : Schema.Words(c.tag), c.state == "added", c.state == "removed", desc + "\n" + Dim(action));
            }, c =>
            {
                if (Fixed.Contains(c.tag)) { Notify.Show($"{Schema.Words(c.tag)} comes from the base unit", NotifyKind.Info, 3f, "builder-enemy"); return; }
                EditTags(tags =>
                {
                    if (c.state == "removed") tags.Remove.Remove(c.tag);
                    else if (c.state == "added") tags.Add.Remove(c.tag);
                    else if (!tags.Remove.Contains(c.tag)) tags.Remove.Add(c.tag);
                });
            });
            if (chips.Count == 0) page.Text(Dim("No tags."));

            foreach (var (a, b, why) in Conflicts)
                if (effective.Contains(a) && effective.Contains(b)) page.Text(Warn($"{Schema.Words(a)} + {Schema.Words(b)}: {why}."));

            StudioDock.SmallHeader(page, "Add");
            page.TextInput("Filter", () => TagFilter, v => { TagFilter = v?.Trim() ?? ""; EnemyDock.RefreshAll(); },
                () => "Type part of a tag's name or what it does to narrow the list.", 32);
            string f = TagFilter ?? "";
            bool Match(string t) => f.Length == 0 || Schema.Words(t).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (Descriptions.Tag(t) ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
            int shown = 0;
            foreach (var (group, tags) in TagGroups)
            {
                var offer = tags.Where(t => !effective.Contains(t) && Match(t)).Select(t => (tag: t, state: "offer")).ToList();
                if (offer.Count == 0) continue;
                shown += offer.Count;
                page.Text(Dim(group));
                Chips(page, "Add " + group, offer, c =>
                {
                    var clash = Conflicts.FirstOrDefault(x => (x.A == c.tag && effective.Contains(x.B)) || (x.B == c.tag && effective.Contains(x.A)));
                    return ("+ " + Schema.Words(c.tag), false, false, (Descriptions.Tag(c.tag) ?? "") + (clash.Why != null ? "\n" + Warn("Conflicts: " + clash.Why + ".") : ""));
                }, c => EditTags(tags =>
                {
                    if (tags.Remove.Remove(c.tag)) return; // a removed base tag: back again
                    if (!tags.Add.Contains(c.tag)) tags.Add.Add(c.tag);
                }));
            }
            if (shown == 0) page.Text(Dim(f.Length > 0 ? "No tag matches." : "Every tag is on it."));
        }

        private static void EditTags(Action<TagEditDef> change) => EnemyStudio.Edit("Change tags", u =>
        {
            u.Tags ??= new TagEditDef();
            u.Tags.Add ??= [];
            u.Tags.Remove ??= [];
            change(u.Tags);
            if (u.Tags.Add.Count == 0 && u.Tags.Remove.Count == 0 && (u.Tags.Extra == null || u.Tags.Extra.Count == 0)) u.Tags = null;
        }, structural: true);

        /// <summary>Tags for a damage modifier's targets (any tag can be named there).</summary>
        internal static IEnumerable<Picker.Item> TagChoices()
        {
            var common = new[] { "PlayerOwned", "Player", "PlayerUnit", "Building", "Wall", "Tower", "WallOrTower", "House", "InfrastructureEconomy", "CastleCenter", "PlayerHeroUnit", "Flying" };
            yield return new Picker.Item { Header = true, Label = "Player side" };
            foreach (var t in common) yield return new Picker.Item { Id = t, Label = Schema.Words(t), Tooltip = Descriptions.Tag(t) };
            yield return new Picker.Item { Header = true, Label = "Others" };
            foreach (var t in Enum.GetNames(typeof(TagManager.ETag)).Where(t => !common.Contains(t) && !t.StartsWith("AUTO_") && t != "NONE").OrderBy(t => t))
                yield return new Picker.Item { Id = t, Label = Schema.Words(t), Tooltip = Descriptions.Tag(t) };
        }

        /// <summary>Chips that flow left to right and wrap, each as wide as its text.</summary>
        private static void Chips<T>(ModPage page, string name, IReadOnlyList<T> items, Func<T, (string text, bool on, bool dim, string tip)> look, Action<T> click)
        {
            const float h = 36f, gap = 6f, pad = 22f;
            float width = page.Size.x > 1f ? page.Size.x : 400f;
            var placed = new List<(Vector2 pos, float w)>();
            float x = 0f, y = 0f;
            var looks = items.Select(look).ToList();
            foreach (var l in looks)
            {
                float w = Mathf.Min(width, Measure(l.text) + pad);
                if (x > 0f && x + w > width) { x = 0f; y += h + gap; }
                placed.Add((new Vector2(x, -y), w));
                x += w + gap;
            }
            var area = page.Custom(name, items.Count == 0 ? 0f : y + h + gap);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var l = looks[i];
                var tile = ModTile.Create(area, $"{name} {i}", new Vector2(placed[i].w, h), null, l.text, 18f);
                tile.Key = name + " " + i;
                tile.Label.alignment = TMPro.TextAlignmentOptions.Center;
                tile.Place(placed[i].pos);
                tile.SetOn(l.on);
                if (l.dim) tile.SetDim(true, 0.55f);
                ModTooltip.Attach(tile, StripTags(l.text), () => l.tip);
                tile.Applied += () => click(it);
            }
        }

        private static string StripTags(string s) => NumberMath.StripTags(s ?? "").TrimStart('+', ' ');

        private static TMPro.TextMeshProUGUI measurer;

        private static float Measure(string text)
        {
            if (measurer == null)
            {
                var canvas = GameWidgets.Canvas;
                if (canvas == null) return text.Length * 9f;
                measurer = GameWidgets.Text(canvas, "PalaceMod chip measure", "", ModStyle.BoldFont, 18f, ModStyle.Gold, TMPro.TextAlignmentOptions.Left);
                measurer.gameObject.SetActive(false);
            }
            return measurer.GetPreferredValues(text).x;
        }

        // ---- Colours ----------------------------------------------------------------------------------------------

        /// <summary>A part of the model that can be recoloured: the body (key "body"), or another material by name.</summary>
        private sealed class Part
        {
            public string Key, Label;
            public Color? Original;
        }

        private static List<Part> Parts()
        {
            var parts = new List<Part>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The base unit's own materials give the original colours; the stage's model adds a replacement body's.
            var originals = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            void Collect(GameObject go, bool originalsOnly)
            {
                if (go == null) return;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is not MeshRenderer && r is not SkinnedMeshRenderer) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        string name = UnitFactory.PartName(m);
                        bool body = m.name == "Enemy" || m.name.StartsWith("Player") || m.name.StartsWith("Ally");
                        string key = body ? ModelRef.BodyPart : name;
                        var c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : (Color?)null;
                        if (c != null && !originals.ContainsKey(key)) originals[key] = c.Value;
                        if (originalsOnly || !seen.Add(key)) continue;
                        parts.Add(new Part { Key = key, Label = body ? "Body" : Schema.Words(name.Replace('_', ' ')) });
                    }
                }
            }
            Collect(EnemyStudio.BaseUnit?.Prefab, originalsOnly: true);
            var shared = ColorAndLightManagerEnemy();
            if (shared != null) originals[ModelRef.BodyPart] = shared.Value;
            Collect(EnemyStage.Model, originalsOnly: false);
            foreach (var p in parts) p.Original = originals.TryGetValue(p.Key, out var c) ? c : null;
            // Keys in the design that the model no longer has (another base or model): still listed, so they can be cleared.
            foreach (var key in EnemyStudio.Unit?.Visuals?.Colors?.Keys ?? Enumerable.Empty<string>())
                if (seen.Add(key)) parts.Add(new Part { Key = key, Label = key == ModelRef.BodyPart ? "Body" : Schema.Words(key) });
            return parts.OrderBy(p => p.Key == ModelRef.BodyPart ? 0 : 1).ToList();
        }

        /// <summary>The level's enemy colour (the lit side), if a colour scheme is up.</summary>
        private static Color? ColorAndLightManagerEnemy()
        {
            var m = UnityEngine.Object.FindObjectOfType<ColorAndLightManager>();
            var s = m != null ? m.CurrentColorScheme : null;
            return s != null ? s.enemyLightColor : null;
        }

        private static string Swatch(Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>■■</color>";

        private static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c).ToLowerInvariant();

        public static void Size(ModPage page) =>
            Override(page, "Size", Schema.Scale, x => x.Visuals?.Scale, (x, v) =>
            {
                x.Visuals ??= new VisualsDef();
                x.Visuals.Scale = v;
                if (UnitPage.IsEmpty(x.Visuals)) x.Visuals = null;
            }, () => 1f, "Scales the whole unit (1 = the base unit's size). Visual only.");

        public static void Colours(ModPage page)
        {
            var u = EnemyStudio.Unit;
            if (u == null) return;
            StudioDock.SmallHeader(page, "Colours");
            foreach (var part in Parts())
            {
                var p = part;
                ModField field = null;
                field = Line(page, p.Label, () =>
                {
                    string hex = null;
                    EnemyStudio.Unit?.Visuals?.Colors?.TryGetValue(p.Key, out hex);
                    if (hex != null && ColorTools.TryParseHex(hex, out var c)) return $"{Swatch(c)}  {hex}";
                    return p.Original is Color o ? $"{Swatch(o)}  {Dim("original")}" : Dim("original");
                }, () => $"Replaces the colour of the {(p.Key == ModelRef.BodyPart ? "body (the enemy colour)" : "\"" + Esc(p.Label) + "\" material")} " +
                    "outright: the lit side becomes it and the shaded side follows. Apply to pick.", () =>
                {
                    string hex = null;
                    EnemyStudio.Unit?.Visuals?.Colors?.TryGetValue(p.Key, out hex);
                    Color? cur = hex != null && ColorTools.TryParseHex(hex, out var c) ? c : p.Original;
                    ModColorPicker.Open($"{p.Label} colour", cur, picked => EnemyStudio.EditVisuals(picked == null ? $"Reset {p.Label.ToLowerInvariant()} colour" : $"Colour the {p.Label.ToLowerInvariant()}", v =>
                    {
                        v.Colors ??= new Dictionary<string, string>();
                        if (picked is Color pc) v.Colors[p.Key] = Hex(pc);
                        else v.Colors.Remove(p.Key);
                        if (v.Colors.Count == 0) v.Colors = null;
                    }, coalesce: "color " + p.Key), field?.Row.Button.transform as RectTransform, allowNone: true, noneLabel: "Original colour");
                });
            }
            ModField tint = null;
            tint = Line(page, "Tint", () =>
            {
                string hex = EnemyStudio.Unit?.Visuals?.Tint;
                return hex != null && ColorTools.TryParseHex(hex, out var c) ? $"{Swatch(c)}  {hex}" : Dim("none");
            }, () => "A subtle shift of the body's colour (multiplies it). For a new colour use Body above.", () =>
            {
                string hex = EnemyStudio.Unit?.Visuals?.Tint;
                ModColorPicker.Open("Tint", hex != null && ColorTools.TryParseHex(hex, out var c) ? c : null,
                    picked => EnemyStudio.EditVisuals(picked == null ? "Remove tint" : "Set tint", v => v.Tint = picked is Color pc ? Hex(pc) : null, coalesce: "tint"),
                    tint?.Row.Button.transform as RectTransform, allowNone: true, noneLabel: "No tint");
            });
            if (u.Visuals?.Colors?.Count > 0 || u.Visuals?.Tint != null)
                page.Button("Original colours", () => EnemyStudio.EditVisuals("Reset colours", v => { v.Colors = null; v.Tint = null; }, structural: true),
                    "Drops every colour and the tint.");
        }
    }
}
