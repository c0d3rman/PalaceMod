using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

/*
    The Compendium's numbers: base values as authored on a prefab, and actual values with everything the game applies
    on top. Pure data, no UI.

    Enemies are computed, mirroring Spawn.Update (the code that runs when an enemy spawns): the night's difficulty
    (difficultyMulti for HP, half of it for damage), elite ×4 HP / ×3 damage, and the perks and mutators that change
    enemies. Damage taken by your side is then divided by your Blacksmith armour (Weapon.blacksmithEffect). Player units
    are read from live instances, which the game has already modified at Start (perk HP/speed/range components, upgrades),
    with the AutoAttack damage multiplier and Blacksmith weapon upgrades applied the same way Weapon.DealDamage does.
*/
namespace PalaceMod
{
    public sealed class StatLine
    {
        public float Hp, Speed, Range, Damage, Cooldown, ProjectileSpeed;

        public StatLine Clone() => (StatLine)MemberwiseClone();
    }

    /// <summary>One multiplier the game applies, with what it's for, e.g. ("Elite", hp 4, damage 3).</summary>
    public sealed class StatModifier
    {
        public string Label;
        public float Hp = 1f, Damage = 1f, Cooldown = 1f, Speed = 1f, Range = 1f;

        public bool IsIdentity => Hp == 1f && Damage == 1f && Cooldown == 1f && Speed == 1f && Range == 1f;

        /// <summary>"×4 HP, ×3 damage" style summary.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            void Add(float value, string what) { if (value != 1f) parts.Add($"{CompendiumStats.Percent(value)} {what}"); }
            Add(Hp, "HP");
            Add(Damage, "damage");
            Add(Speed, "speed");
            Add(Range, "range");
            Add(Cooldown, "cooldown");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Damage against one kind of target.</summary>
    public struct DamageVs
    {
        public string Target;
        public float Base, Actual;
    }

    public sealed class CombatantStats
    {
        public string Name;
        public Sprite Icon;
        public bool Elite;
        public StatLine Base = new StatLine();
        public StatLine Actual = new StatLine();
        public readonly List<StatModifier> Modifiers = [];
        public readonly List<DamageVs> DamageByTarget = [];
        public float SplashBase, SplashActual;
        public List<TagManager.ETag> Tags = [];
    }

    public static class CompendiumStats
    {
        public static readonly TagManager.ETag[] FallbackKingTags = [TagManager.ETag.PlayerOwned, TagManager.ETag.Player];
        public static readonly TagManager.ETag[] PlainEnemyTags = [TagManager.ETag.EnemyOwned];

        /// <summary>"+20%" / "-25%" for a multiplier, or "×4" when it's a whole multiple.</summary>
        public static string Percent(float multiplier)
        {
            if (multiplier >= 2f && Mathf.Approximately(multiplier, Mathf.Round(multiplier))) return "×" + Mathf.RoundToInt(multiplier);
            float pct = (multiplier - 1f) * 100f;
            string number = Mathf.Approximately(pct, Mathf.Round(pct)) ? Mathf.RoundToInt(pct).ToString(CultureInfo.InvariantCulture) : pct.ToString("0.#", CultureInfo.InvariantCulture);
            return (pct >= 0 ? "+" : "") + number + "%";
        }

        /// <summary>The game's own number format for the stats panel: no decimals for whole numbers, else one.</summary>
        public static string Format(float value)
        {
            if (Mathf.Approximately(value, Mathf.Round(value))) return Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>Whether two values would print the same, so no "→" is needed.</summary>
        public static bool SameShown(float a, float b) => Format(a) == Format(b);

        public static string Translate(string key)
        {
            try { return TextTranslator.Translate(key); }
            catch { return key; }
        }

        public static string EquippableName(Equippable e) => e == null ? "" : Translate(e.LOCIDENTIFIER_NAME);

        // DamageModifyer.CalculateDamageOnTarget, against a tag list instead of a live TaggedObject.
        public static float DamageAgainst(IList<DamageModifyer> modifiers, ICollection<TagManager.ETag> targetTags, float finalMultiplier = 1f)
        {
            if (modifiers == null) return 0f;
            float damage = 0f;
            foreach (var modifier in modifiers)
            {
                bool applies = true;
                foreach (var tag in modifier.requiredTags)
                    if (!targetTags.Contains(tag)) { applies = false; break; }
                if (!applies) continue;
                damage += modifier.damageAdded;
                damage *= modifier.damageMultiplyer;
            }
            return damage * finalMultiplier;
        }

        /// <summary>Weapon.BlacksmithMulti without throwing outside a level.</summary>
        public static float BlacksmithMulti(Weapon weapon, out string label)
        {
            label = null;
            var smith = BlacksmithUpgrades.instance;
            if (weapon == null || smith == null) return 1f;
            switch (weapon.blacksmithEffect)
            {
                case Weapon.EDamageAffectedByBlacksmithUpgrade.MultiplyBy_MeleeDamage: label = "Melee weapon upgrades"; return smith.meleeDamage;
                case Weapon.EDamageAffectedByBlacksmithUpgrade.MultiplyBy_RangedDamage: label = "Ranged weapon upgrades"; return smith.rangedDamage;
                case Weapon.EDamageAffectedByBlacksmithUpgrade.DivideBy_MeleeResistance: label = "Your melee armor"; return 1f / smith.meleeResistance;
                case Weapon.EDamageAffectedByBlacksmithUpgrade.DivideBy_RangedResistance: label = "Your ranged armor"; return 1f / smith.rangedResistance;
                default: return 1f;
            }
        }

        // The components the pause menu reads, in the same order (root first, then children).
        private static T Find<T>(GameObject go) where T : Component => go.GetComponent<T>() ?? go.GetComponentInChildren<T>(true);

        private static float RangeFor(AutoAttack attack, TagManager.ETag preferred)
        {
            if (attack == null || attack.targetPriorities.Count == 0) return 0f;
            foreach (var priority in attack.targetPriorities)
                if (priority.mustHaveTags.Contains(preferred)) return priority.range;
            return attack.targetPriorities[0].range;
        }

        // ---------------------------------------------------------------- enemies

        /// <summary>The multipliers Spawn.Update applies to an enemy of this prefab spawned in wave
        /// <paramref name="waveIndex"/> (0-based), with the perks equipped now. Order matches the game's.</summary>
        public static List<StatModifier> EnemyModifiers(GameObject prefab, bool elite, int waveIndex)
        {
            var mods = new List<StatModifier>();
            var spawner = EnemySpawner.instance;
            var perks = PerkManager.instance;
            var tags = prefab.GetComponentInChildren<TaggedObject>(true)?.Tags ?? [];
            bool flying = tags.Contains(TagManager.ETag.Flying);

            float difficulty = 1f;
            if (spawner != null && waveIndex >= 0 && waveIndex < spawner.waves.Count) difficulty = spawner.waves[waveIndex].difficultyMulti;
            if (difficulty != 1f)
                mods.Add(new StatModifier { Label = Translate("Menu/DifficultyBoost"), Hp = difficulty, Damage = Mathf.Lerp(1f, difficulty, 0.5f) });
            if (elite)
                mods.Add(new StatModifier { Label = Translate("Menu/Elite Prefix"), Hp = 4f, Damage = 3f });
            if (perks == null) return mods;

            if (PerkManager.IsEquipped(perks.turtleGodPerk))
                mods.Add(new StatModifier { Label = EquippableName(perks.turtleGodPerk), Hp = perks.tauntTheTurtle_hpMultiplyer });
            if (PerkManager.IsEquipped(perks.tigerGodPerk))
                mods.Add(new StatModifier { Label = EquippableName(perks.tigerGodPerk), Damage = perks.tauntTheTiger_damageMultiplyer });
            if (PerkManager.IsEquipped(perks.falconGodPerk))
                mods.Add(new StatModifier { Label = EquippableName(perks.falconGodPerk), Speed = perks.tauntTheFalcon_speedMultiplyer });
            if (PerkManager.IsEquipped(perks.antiAirTelescope) && flying)
                mods.Add(new StatModifier { Label = EquippableName(perks.antiAirTelescope), Hp = perks.antiAir_helathMultiplyer, Damage = perks.antiAir_damageMultiplyer });
            if (PerkManager.IsEquipped(perks.prayToWarGods))
                mods.Add(new StatModifier { Label = EquippableName(perks.prayToWarGods), Hp = perks.prayWarGods_hpMulti, Damage = perks.prayWarGods_dmgMulti });
            if (perks.GrowthGodEquipped && spawner != null)
            {
                int steps = Mathf.Max(1, spawner.WaveCount - 1);
                float dmgStep = Mathf.Pow(perks.growthGodMaxDamageMulti, 1f / steps);
                float hpStep = Mathf.Pow(perks.growthGodMaxHpMulti, 1f / steps);
                int wave = Mathf.Max(0, waveIndex);
                mods.Add(new StatModifier { Label = EquippableName(perks.growthGod), Hp = Mathf.Pow(hpStep, wave), Damage = Mathf.Pow(dmgStep, wave) });
            }
            if (perks.RangeGodEquipped && tags.Contains(TagManager.ETag.RangedFighter) && !flying)
                mods.Add(new StatModifier
                {
                    Label = EquippableName(perks.rangeGod),
                    Hp = perks.rangeGodHealthMultiplyer,
                    Damage = perks.rangeGodDamageMultiplyer,
                    Cooldown = perks.rangeGodAttackCooldownMultiplyer,
                    Speed = perks.rangeGodMoveSpeedMultiplyer,
                    Range = perks.rangeGodRangeMultiplyer,
                });
            mods.RemoveAll(m => m.IsIdentity);
            return mods;
        }

        /// <summary>Base and actual stats for an enemy prefab spawned in wave <paramref name="waveIndex"/>.</summary>
        public static CombatantStats Enemy(GameObject prefab, bool elite, int waveIndex)
        {
            var marker = prefab.GetComponent<ScreenMarkerIcon>();
            var stats = new CombatantStats { Elite = elite, Name = marker != null ? marker.UnitName : prefab.name, Icon = marker?.sprite };
            var hp = Find<Hp>(prefab);
            var move = Find<PathfindMovementEnemy>(prefab);
            var attack = Find<AutoAttack>(prefab);
            stats.Tags = prefab.GetComponentInChildren<TaggedObject>(true)?.Tags?.ToList() ?? [];

            var b = stats.Base;
            if (hp != null) b.Hp = hp.maxHp;
            if (move != null) b.Speed = move.movementSpeed;
            float prefabDamageMulti = 1f;
            Weapon weapon = attack?.weapon;
            if (attack != null)
            {
                b.Cooldown = attack.cooldownDuration;
                b.Range = RangeFor(attack, TagManager.ETag.Player);
                prefabDamageMulti = attack.DamageMultiplyer;
                if (weapon != null) b.ProjectileSpeed = weapon.projectileSpeed;
            }

            stats.Modifiers.AddRange(EnemyModifiers(prefab, elite, waveIndex));
            float hpM = 1f, dmgM = 1f, cdM = 1f, speedM = 1f, rangeM = 1f;
            foreach (var m in stats.Modifiers) { hpM *= m.Hp; dmgM *= m.Damage; cdM *= m.Cooldown; speedM *= m.Speed; rangeM *= m.Range; }
            float armor = BlacksmithMulti(weapon, out var armorLabel);
            if (armor != 1f) stats.Modifiers.Add(new StatModifier { Label = armorLabel, Damage = armor });

            var a = stats.Actual;
            a.Hp = b.Hp * hpM;
            a.Speed = b.Speed * speedM;
            a.Cooldown = b.Cooldown * cdM;
            a.Range = b.Range * rangeM;
            a.ProjectileSpeed = b.ProjectileSpeed;

            if (weapon != null)
            {
                var targets = PlayerSideTargets();
                foreach (var (label, tags) in targets)
                    stats.DamageByTarget.Add(new DamageVs
                    {
                        Target = label,
                        Base = DamageAgainst(weapon.directDamage, tags, prefabDamageMulti),
                        Actual = DamageAgainst(weapon.directDamage, tags, prefabDamageMulti * dmgM * armor),
                    });
                var king = targets[0].Tags;
                // The row shows damage against you; siege weapons that only hit buildings show their building damage.
                var main = stats.DamageByTarget[0];
                if (main.Actual == 0f)
                    foreach (var vs in stats.DamageByTarget)
                        if (vs.Actual > main.Actual) main = vs;
                b.Damage = main.Base;
                a.Damage = main.Actual;
                stats.SplashBase = DamageAgainst(weapon.splashDamage, king, prefabDamageMulti);
                stats.SplashActual = DamageAgainst(weapon.splashDamage, king, prefabDamageMulti * dmgM * armor);
            }
            return stats;
        }

        /// <summary>Representative targets on the player's side: the king first, then a unit and a building.
        /// Tags come from live objects when in a level.</summary>
        public static List<(string Label, HashSet<TagManager.ETag> Tags)> PlayerSideTargets()
        {
            var list = new List<(string, HashSet<TagManager.ETag>)>();
            var tm = TagManager.instance;
            var king = tm != null && tm.Players.Count > 0 ? tm.Players[0] : null;
            list.Add(("You", king != null ? [.. king.Tags] : [.. FallbackKingTags]));
            if (tm == null) return list;
            // One entry per distinct tag set among live units and buildings, labelled by the first one's name.
            var seen = new HashSet<string>();
            void AddFrom(IEnumerable<TaggedObject> objects, string fallback)
            {
                foreach (var obj in objects)
                {
                    if (obj == null || obj.Tags.Contains(TagManager.ETag.Player)) continue;
                    var key = string.Join(",", obj.Tags.OrderBy(t => t));
                    if (!seen.Add(key)) continue;
                    list.Add((NameOf(obj) ?? fallback, [.. obj.Tags]));
                }
            }
            AddFrom(tm.PlayerUnits, "Unit");
            var buildings = new List<TaggedObject>();
            tm.FindAllTaggedObjectsWithTag(buildings, TagManager.ETag.Building);
            AddFrom(buildings, "Building");
            return list;
        }

        private static string NameOf(TaggedObject obj)
        {
            var marker = obj.GetComponent<ScreenMarkerIcon>();
            if (marker != null) return marker.UnitName;
            var slot = obj.GetComponentInParent<BuildSlot>();
            if (slot != null && !string.IsNullOrEmpty(slot.buildingName)) return Translate(slot.LOCIDENTIFIER_NAME);
            return null;
        }

        // ---------------------------------------------------------------- player units

        /// <summary>Live stats of a player unit, with damage against a plain enemy and against this night's enemies.</summary>
        public static CombatantStats Unit(TaggedObject unit, int previewWaveIndex = -1)
        {
            var go = unit.gameObject;
            var marker = go.GetComponent<ScreenMarkerIcon>();
            var stats = new CombatantStats { Name = marker != null ? marker.UnitName : go.name, Icon = marker?.sprite, Tags = unit.Tags.ToList() };
            var hp = go.GetComponent<Hp>();
            var move = go.GetComponent<PathfindMovementPlayerunit>();
            var attack = go.GetComponent<AutoAttack>();
            var a = stats.Actual;
            if (hp != null) a.Hp = hp.maxHp;
            if (move != null) a.Speed = move.movementSpeed;
            Weapon weapon = attack?.weapon;
            if (attack != null)
            {
                a.Cooldown = attack.cooldownDuration;
                a.Range = RangeFor(attack, TagManager.ETag.EnemyOwned);
                if (weapon != null) a.ProjectileSpeed = weapon.projectileSpeed * attack.ProjectileSpeedMultiplyer;
            }
            // Base: the weapon as authored; HP, speed, range and cooldown are only known live, so base = actual.
            stats.Base = a.Clone();
            if (weapon != null)
            {
                stats.Base.ProjectileSpeed = weapon.projectileSpeed;
                float multi = attack.DamageMultiplyer;
                float smith = BlacksmithMulti(weapon, out var smithLabel);
                if (multi != 1f) stats.Modifiers.Add(new StatModifier { Label = "Perks and upgrades", Damage = multi });
                if (smith != 1f) stats.Modifiers.Add(new StatModifier { Label = smithLabel, Damage = smith });
                stats.Base.Damage = DamageAgainst(weapon.directDamage, PlainEnemyTags);
                a.Damage = DamageAgainst(weapon.directDamage, PlainEnemyTags, multi * smith);
                stats.SplashBase = DamageAgainst(weapon.splashDamage, PlainEnemyTags);
                stats.SplashActual = DamageAgainst(weapon.splashDamage, PlainEnemyTags, multi * smith);
                foreach (var (label, tags) in EnemyTargets(previewWaveIndex))
                    stats.DamageByTarget.Add(new DamageVs
                    {
                        Target = label,
                        Base = DamageAgainst(weapon.directDamage, tags),
                        Actual = DamageAgainst(weapon.directDamage, tags, multi * smith),
                    });
            }
            return stats;
        }

        /// <summary>The distinct enemy types of a wave (or the next one), by name with their tags.</summary>
        public static List<(string Label, HashSet<TagManager.ETag> Tags)> EnemyTargets(int waveIndex)
        {
            var list = new List<(string, HashSet<TagManager.ETag>)>();
            var spawner = EnemySpawner.instance;
            if (spawner == null) return list;
            if (waveIndex < 0) waveIndex = NextWaveIndex();
            if (waveIndex < 0 || waveIndex >= spawner.waves.Count) return list;
            var seen = new HashSet<string>();
            foreach (var spawn in spawner.waves[waveIndex].spawns)
            {
                if (spawn.enemyPrefab == null) continue;
                var marker = spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>();
                var tagged = spawn.enemyPrefab.GetComponentInChildren<TaggedObject>(true);
                if (marker == null || tagged == null) continue;
                var name = marker.UnitName;
                if (!seen.Add(name)) continue;
                list.Add((name, [.. tagged.Tags]));
            }
            return list;
        }

        /// <summary>The wave the pause menu previews by default: the current one at night, the next one by day.</summary>
        public static int NextWaveIndex()
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null) return -1;
            bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
            return night ? spawner.Wavenumber : spawner.Wavenumber + 1;
        }

        /// <summary>Finds the spawn prefab in a wave that the pause menu's tile for (name, icon, elite) stands for.</summary>
        public static GameObject FindWavePrefab(int waveIndex, string name, Sprite icon, bool elite)
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null || waveIndex < 0 || waveIndex >= spawner.waves.Count) return null;
            GameObject byName = null;
            foreach (var spawn in spawner.waves[waveIndex].spawns)
            {
                if (spawn.enemyPrefab == null || spawn.eliteEnemies != elite) continue;
                var marker = spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>();
                if (marker == null || marker.UnitName != name) continue;
                var arachno = spawn.enemyPrefab.GetComponent<ScreenMarkerIconArachnophobia>();
                var sprite = arachno != null ? arachno.defaultSprite : marker.sprite;
                if (icon == null || sprite == icon || (arachno != null && arachno.arachnophobiaModeSprite == icon)) return spawn.enemyPrefab;
                byName ??= spawn.enemyPrefab;
            }
            return byName;
        }

        /// <summary>Live player units grouped the way the pause menu groups them (icon + name).</summary>
        public static List<List<TaggedObject>> LiveUnitGroups()
        {
            var groups = new List<List<TaggedObject>>();
            var tm = TagManager.instance;
            if (tm == null) return groups;
            var units = new List<TaggedObject>();
            tm.FindAllTaggedObjectsWithTags(units, [TagManager.ETag.PlayerUnit], []);
            var index = new Dictionary<(Sprite, string), List<TaggedObject>>();
            foreach (var unit in units)
            {
                var marker = unit.GetComponent<ScreenMarkerIcon>();
                if (marker == null) continue;
                var key = (marker.sprite, marker.UnitName);
                if (!index.TryGetValue(key, out var group)) { group = []; index[key] = group; groups.Add(group); }
                group.Add(unit);
            }
            return groups;
        }
    }
}
