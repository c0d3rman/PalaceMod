using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using PalaceMod.Builder.Model;
using UnityEngine;

namespace PalaceMod.Builder.Editor
{
    /// <summary>Catalog lookups for the editor that never throw (the catalog may not be installed or built yet).</summary>
    internal static class EditorCatalog
    {
        public static ICatalog Catalog
        {
            get
            {
                try { return BuilderServices.Catalog; }
                catch (InvalidOperationException) { return null; }
            }
        }

        public static IReadOnlyList<CatalogUnit> Units(string side) =>
            Catalog?.Units.Where(u => side == null || u.Side == side).OrderBy(u => u.Difficulty <= 0 ? float.MaxValue : u.Difficulty).ThenBy(u => u.DisplayName).ToList()
            ?? (IReadOnlyList<CatalogUnit>)[];

        public static IReadOnlyList<CatalogHost> Hosts => Catalog?.Hosts ?? (IReadOnlyList<CatalogHost>)[];

        public static CatalogHost Host(string scene) => scene == null ? null : Catalog?.FindHost(scene);

        /// <summary>The vanilla unit a ref is ultimately based on (following custom units in the pack to their base).</summary>
        public static CatalogUnit BaseOf(DesignPack pack, string unitRef, int depth = 0)
        {
            if (unitRef == null || depth > 8) return null;
            var r = ContentRef.Parse(unitRef);
            switch (r.Kind)
            {
                case RefKind.Vanilla:
                    return Catalog?.FindUnit(r.Id);
                case RefKind.LocalUnit:
                    return BaseOf(pack, pack?.FindUnit(r.Id)?.Base, depth + 1);
                case RefKind.PackUnit:
                    var other = r.Pack == pack?.Id ? pack : BuilderServices.Packs.Find(r.Pack)?.Pack;
                    return BaseOf(other, other?.FindUnit(r.Id)?.Base, depth + 1);
                default:
                    return null;
            }
        }

        /// <summary>A unit ref as the player knows it: the custom unit's name, or the vanilla unit's.</summary>
        public static string UnitName(DesignPack pack, string unitRef)
        {
            if (string.IsNullOrEmpty(unitRef)) return "none";
            var r = ContentRef.Parse(unitRef);
            if (r.Kind == RefKind.LocalUnit || (r.Kind == RefKind.PackUnit && r.Pack == pack?.Id))
            {
                var u = pack?.FindUnit(r.Id);
                return u == null ? $"{r.Id} (missing)" : string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name;
            }
            if (r.Kind == RefKind.PackUnit)
            {
                var u = BuilderServices.Packs.Find(r.Pack)?.Pack?.FindUnit(r.Id);
                return u == null ? $"{r.Pack}/{r.Id} (missing)" : string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name;
            }
            if (r.Kind == RefKind.Vanilla) return Catalog?.FindUnit(r.Id)?.DisplayName ?? r.Id;
            return unitRef;
        }

        public static Sprite UnitIcon(PackDraft draft, string unitRef)
        {
            var r = ContentRef.Parse(unitRef);
            var custom = r.Kind == RefKind.LocalUnit ? draft?.Pack.FindUnit(r.Id) : null;
            if (custom?.Visuals?.Icon != null && draft.Entry != null)
            {
                try
                {
                    var sprite = BuilderServices.Assets.GetSprite(draft.Entry, custom.Visuals.Icon);
                    if (sprite != null) return sprite;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"Builder editor: icon {custom.Visuals.Icon}: {e.Message}"); }
            }
            return BaseOf(draft?.Pack, unitRef)?.Icon;
        }

        public static string Stats(CatalogUnit u) =>
            u == null ? "" : $"HP {Num(u.MaxHp)} · speed {Num(u.MoveSpeed)} · attack every {Num(u.AttackCooldown)} s · range {Num(u.AttackRange)}" +
                (u.Flying ? " · flying" : "") + (u.Large ? " · large" : "");

        public static string Num(float v) => ModFields.FormatNumber(v);

        /// <summary>A deep copy of a model object (vanilla waves from the catalog are shared; never edit them in place).</summary>
        public static T Copy<T>(T value) where T : class =>
            value == null ? null : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));

        /// <summary>An id not in <paramref name="taken"/>: base, base-2, base-3, ...</summary>
        public static string UniqueId(string baseId, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken.Where(t => t != null));
            if (!Ids.IsValid(baseId)) baseId = "item";
            string id = baseId;
            for (int n = 2; set.Contains(id); n++)
            {
                string suffix = $"-{n}";
                id = baseId.Substring(0, Math.Min(baseId.Length, 64 - suffix.Length)).TrimEnd('-', '.', '_') + suffix;
            }
            return id;
        }
    }

    /// <summary>
    /// How numeric model fields are edited: label, help text, range (the validator's), step and the base value they
    /// override. Stats are generated from StatsDef by reflection, so a new stat appears in the editor with a readable
    /// label even before it gets an entry here.
    /// </summary>
    internal sealed class NumberSpec
    {
        public string Label, Help;
        public float Min, Max = 1000f, Step = 1f;
        public bool Integer;
        public string Unit = "";

        public float Clamp(float v)
        {
            v = Mathf.Clamp(v, Min, Max);
            return Integer ? Mathf.Round(v) : (float)Math.Round(v, 3);
        }

        /// <summary>Steps v by delta steps, landing on the step grid.</summary>
        public float Stepped(float v, int delta)
        {
            float next = Mathf.Round(v / Step) * Step + delta * Step;
            return Clamp(next);
        }

        public string Format(float v) => ModFields.FormatNumber(v) + Unit;
    }

    internal static class Schema
    {
        public static readonly Dictionary<string, NumberSpec> Stats = new()
        {
            ["maxHp"] = new() { Label = "Hit points", Help = "Maximum health. The night's difficulty and elite status still multiply it.", Min = 1, Max = 100000, Step = 1, Integer = true },
            ["moveSpeed"] = new() { Label = "Move speed", Help = "Units per second. Vanilla foot soldiers walk at about 4 to 6.", Min = 0, Max = 100, Step = 0.25f },
            ["attackCooldown"] = new() { Label = "Attack cooldown", Help = "Seconds between attacks.", Min = 0.05f, Max = 120, Step = 0.05f, Unit = " s" },
            ["attackRange"] = new() { Label = "Attack range", Help = "How far away the unit picks targets.", Min = 0, Max = 200, Step = 0.25f },
            ["keepDistance"] = new() { Label = "Keep distance", Help = "How far a ranged unit stays from its target.", Min = 0, Max = 200, Step = 0.25f },
            ["aggroTime"] = new() { Label = "Chase time", Help = "Seconds the unit chases the king after he hits it.", Min = 0, Max = 600, Step = 0.5f, Unit = " s" },
        };

        public static readonly NumberSpec DamageMult = new() { Label = "Damage multiplier", Help = "Multiplies all damage this unit deals.", Min = 0, Max = 1000, Step = 0.05f, Unit = "×" };
        public static readonly NumberSpec ProjectileSpeed = new() { Label = "Projectile speed", Help = "How fast the unit's arrows or shots fly.", Min = 0.1f, Max = 1000, Step = 1f };
        public static readonly NumberSpec ModAdd = new() { Label = "Bonus damage", Help = "Added to the weapon's damage against matching targets.", Min = -100000, Max = 100000, Step = 0.5f };
        public static readonly NumberSpec ModMult = new() { Label = "Damage ×", Help = "Multiplies damage against matching targets (after the bonus).", Min = 0, Max = 1000, Step = 0.05f, Unit = "×" };
        public static readonly NumberSpec Scale = new() { Label = "Size", Help = "Scales the model (1 = the base unit's size). Visual only.", Min = 0.1f, Max = 10, Step = 0.05f, Unit = "×" };

        public static readonly NumberSpec StartingGold = new() { Label = "Starting gold", Help = "Gold the player starts the run with.", Min = 0, Max = 100000, Step = 1, Integer = true };
        public static readonly NumberSpec Difficulty = new() { Label = "Difficulty", Help = "The night's health and damage multiplier for every enemy (1 = normal). Shown as skulls in the night preview.", Min = 0.1f, Max = 100, Step = 0.05f, Unit = "×" };
        public static readonly NumberSpec Count = new() { Label = "Count", Help = "How many enemies the group has.", Min = 1, Max = 1000, Step = 1, Integer = true };
        public static readonly NumberSpec Interval = new() { Label = "Interval", Help = "Seconds between enemies of the group.", Min = 0, Max = 600, Step = 0.1f, Unit = " s" };
        public static readonly NumberSpec Delay = new() { Label = "Delay", Help = "Seconds after nightfall before the group starts.", Min = 0, Max = 3600, Step = 0.5f, Unit = " s" };
        public static readonly NumberSpec Gold = new() { Label = "Gold", Help = "Coins the whole group drops, spread over its enemies.", Min = 0, Max = 10000, Step = 1, Integer = true };

        /// <summary>The editable stats of StatsDef: (json name, property, spec). New nullable number properties show up
        /// automatically.</summary>
        public static IEnumerable<(string Key, PropertyInfo Property, NumberSpec Spec)> StatFields()
        {
            foreach (var p in typeof(StatsDef).GetProperties())
            {
                var json = p.GetCustomAttribute<JsonPropertyAttribute>();
                if (json?.PropertyName == null || (p.PropertyType != typeof(float?) && p.PropertyType != typeof(int?))) continue;
                if (!Stats.TryGetValue(json.PropertyName, out var spec))
                    spec = new NumberSpec { Label = Words(json.PropertyName), Help = "", Max = 100000, Step = p.PropertyType == typeof(int?) ? 1 : 0.1f, Integer = p.PropertyType == typeof(int?) };
                yield return (json.PropertyName, p, spec);
            }
        }

        /// <summary>The base unit's value for a stat (CatalogUnit field with the same name), or null.</summary>
        public static float? BaseStat(CatalogUnit unit, string key)
        {
            if (unit == null) return null;
            var f = typeof(CatalogUnit).GetField(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return f?.FieldType == typeof(float) ? (float)f.GetValue(unit) : null;
        }

        /// <summary>"attackCooldown" -> "Attack cooldown".</summary>
        public static string Words(string identifier)
        {
            var s = new System.Text.StringBuilder();
            for (int i = 0; i < identifier.Length; i++)
            {
                char c = identifier[i];
                if (i == 0) s.Append(char.ToUpper(c, CultureInfo.InvariantCulture));
                else if (char.IsUpper(c)) s.Append(' ').Append(char.ToLowerInvariant(c));
                else s.Append(c);
            }
            return s.ToString();
        }

        /// <summary>Common tints offered by the colour field (hex typed values are allowed too).</summary>
        public static readonly (string Name, string Hex)[] Tints =
        [
            ("Moss", "#8fd14f"), ("Gold", "#c9a227"), ("Rust", "#c0573a"), ("Blood", "#a3202a"), ("Frost", "#7fc6e8"),
            ("Violet", "#8a5cc9"), ("Ash", "#8a8a8a"), ("Bone", "#e8dcc0"), ("Pitch", "#2a2a33"),
        ];
    }
}
