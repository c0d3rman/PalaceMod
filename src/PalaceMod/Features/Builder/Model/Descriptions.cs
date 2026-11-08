using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
    Hover tooltips for names the Builder shows whose meaning isn't obvious: tags, slot kinds, upgrade choices, weapons,
    behaviours, stats and flags. Written from the decompiled game code (and, where the code only reads a value from the
    game's data, from what that data does). Serialized prefab values override code defaults, so the texts describe
    mechanisms; numbers appear only where they are code constants (elite x4 HP / x3 damage, etc.).
*/
namespace PalaceMod.Builder.Model
{
    internal enum DescKind { Tag, SlotKind, Upgrade, Weapon, Behaviour, Stat, Flag }

    internal static class Descriptions
    {
        /// <summary>The description of a name, or null if there is none. Case, spaces, underscores and hyphens are
        /// ignored ("FlyingUnit" == "flying unit"). Upgrade names also match with a trailing tier number ("Sacrifice 1").</summary>
        public static string For(DescKind kind, string name)
        {
            if (name == null || !Tables.TryGetValue(kind, out var table)) return null;
            if (table.TryGetValue(Normalize(name), out var text)) return text;
            if (kind == DescKind.Upgrade)
            {
                string trimmed = name.TrimEnd().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (trimmed.Length > 0 && trimmed.Length < name.TrimEnd().Length && table.TryGetValue(Normalize(trimmed), out text)) return text;
            }
            return null;
        }

        public static string Tag(string name) => For(DescKind.Tag, name);

        /// <summary>The names described for a kind, as written in the tables.</summary>
        public static IReadOnlyCollection<string> Names(DescKind kind) => Sources.TryGetValue(kind, out var source) ? source.Keys : [];

        internal static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (!char.IsWhiteSpace(c) && c != '_' && c != '-') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // ---- Tags (TagManager.ETag) --------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> TagTexts = new()
        {
            ["NONE"] = "Placeholder meaning 'no tag'. Has no effect on a unit.",
            ["PlayerOwned"] = "On the player's side: enemies target it, and if knocked out it comes back at dawn.",
            ["EnemyOwned"] = "On the enemy side: counts as an enemy left on the map; the night ends when none remain.",
            ["Player"] = "The king. Enemies hit by him chase him for their chase time; Magic Armor reflects damage he takes.",
            ["CastleCenter"] = "The castle. Nights can only start while one exists; some enemies target it first.",
            ["MeeleFighter"] = "Melee fighter. Commanded with the melee group; every second melee enemy of a night takes the alternate route.",
            ["RangedFighter"] = "Ranged fighter. Commanded with the ranged group; Magic Armor reflects a different amount at it.",
            ["Flying"] = "Flies: spawns 5 m up and moves on the fliers' graph, ignoring walls and ground paths. Some attacks target fliers specifically.",
            ["PlayerUnit"] = "A player troop: can be commanded, and is revived by effects that revive troops.",
            ["Building"] = "A building: shown as one on the minimap, and some weapons deal less damage to it.",
            ["SiegeWeapon"] = "Siege weapon. Only the generated wave modes (Eternal Trials, endless) read it, to mix wave types.",
            ["AUTO_Alive"] = "Set by the game while the object is alive; most targeting requires it. Don't add it by hand.",
            ["AUTO_KnockedOutAndHealOnDawn"] = "Set by the game on a knocked-out unit or building that gets back up at dawn. Don't add it by hand.",
            ["Wall"] = "A wall. Emergency Repairs heals walls; some weapons deal less damage to it.",
            ["InfrastructureEconomy"] = "Economy building. No game code reads it; enemy data uses it for target choice (Wasps go for it) and damage.",
            ["TakesReducedDamageFromPlayerAttacks"] = "No game code reads it; the player's weapons deal less damage to it through their damage lists (e.g. Racer).",
            ["PracticeTargets"] = "Tutorial practice target.",
            ["FastMoving"] = "Fast: barricades damage and slow it, and so do weapons that slow fast enemies.",
            ["ArmoredAgainstRanged"] = "No game code reads it; ranged weapons' damage lists deal it much less damage.",
            ["VulnerableVsRanged"] = "No game code reads it; ranged weapons' damage lists deal it extra damage.",
            ["Monster"] = "Monster. Only the generated wave modes and weapon damage or target lists read it.",
            ["House"] = "A house. Builder's Guild upgrades houses for free.",
            ["WallOrTower"] = "A wall or tower. Castle Up makes these cheaper.",
            ["AUTO_Commanded"] = "Set by the game while a troop follows a command; enemies don't target commanded troops. Don't add it by hand.",
            ["TakesIncreasedDamageFromTowers"] = "No game code reads it; tower weapons' damage lists deal it extra damage.",
            ["Tower"] = "A tower. Last Stand and the Temple's Overload act on towers.",
            ["AUTO_NoReviveNextMorning"] = "Set by the game on a destroyed building that won't rebuild at the next dawn. Don't add it by hand.",
            ["PlayerOwnedPriorityTarget"] = "No game code reads it; some enemies' target lists go for it first.",
            ["BlockableEnemyProjectile"] = "No game code or enemy data reads it; it only matters where a weapon or target list names it.",
            ["Boss"] = "Boss. While one is alive, the stalled-night tiebreaker doesn't start.",
            ["UselessWallThatDoesNotBlockPath"] = "No game code or enemy data reads it; it only matters where a weapon or target list names it.",
            ["Healer"] = "No game code or enemy data reads it; it only matters where a weapon or target list names it.",
            ["MagicProjectile"] = "No game code or enemy data reads it; it only matters where a weapon or target list names it.",
            ["NaturallyVulnerableToSplash"] = "Weak to splash. Only the generated wave modes read it, to mix wave types.",
            ["NaturallyHighHealthTarget"] = "No game code reads it; marks tanky enemies (Ram, Ogre) for weapons' damage or target lists.",
            ["UnreachableForBigEnemiesDueToLevelDesign"] = "No game code or enemy data reads it; it only matters where a weapon or target list names it.",
            ["PlayerShrine"] = "A shrine. Shrines count each other, and the Temple's Wisdom spares them.",
            ["VodooCursed"] = "Set by the game while a Curse Blast mark is on the target. Don't add it by hand.",
            ["PlayerHeroUnit"] = "A hero troop: commanded with the hero group, not the melee or ranged ones.",
            ["FireAndExplosionResistant"] = "No game code reads it; fire and explosion weapons' damage lists deal it less damage.",
            ["Humanoid"] = "Humanoid. With the God of Afterlife perk, every second one spawned rises again on death.",
            ["LargeUnit"] = "Large: spawns on the large-unit ground graph. Its movement graph comes from the base unit.",
            ["Exploding"] = "Explodes. The God of Chaos perk doesn't pull these toward the castle on spawn.",
            ["TrainingFacility"] = "No game code or enemy data reads it; it only matters where a weapon or target list names it.",
            ["Bridge"] = "A bridge. Its income is counted in the income display.",
            ["Group1"] = "Control group 1. Set by the game when the player assigns the troop; cleared on load.",
            ["Group2"] = "Control group 2. Set by the game when the player assigns the troop; cleared on load.",
            ["Group3"] = "Control group 3. Set by the game when the player assigns the troop; cleared on load.",
            ["OnDeathEnemySpawner"] = "Unused by the game.",
            ["Undead"] = "Unused by the game.",
            ["Vampire"] = "Unused by the game.",
        };

        // ---- Studio slot kinds (Studio/Requirements/SlotKinds.cs), by enum name and by Label() ----------------------

        private const string CastleText = "The castle center. Its level gates other slots' requirements and, with a cap, their upgrades.";
        private const string EconomyText = "Buildings that pay gold at dawn: houses, mills and fields, mines, harbours.";
        private const string MilitaryText = "Towers and troop buildings: archery ranges, barracks, hero's quarters.";
        private const string WallText = "Walls, gates, barricades and bridges: they block or open paths.";
        private const string SpecialText = "Anything else: blacksmith, royal forge, temple, shrine, summoning circle.";
        private const string SpawnText = "A spawn line: enemies of a night appear anywhere along it.";
        private const string SceneryText = "Decoration. Solid scenery blocks units; walk-through scenery doesn't.";

        private static readonly Dictionary<string, string> SlotKindTexts = new()
        {
            ["Castle"] = CastleText,
            ["Economy"] = EconomyText, ["Income"] = EconomyText,
            ["Military"] = MilitaryText,
            ["Wall"] = WallText, ["Walls and gates"] = WallText,
            ["Special"] = SpecialText,
            ["Spawn"] = SpawnText, ["Spawn line"] = SpawnText,
            ["Scenery"] = SceneryText,
        };

        // ---- Building upgrade choices, by the in-game English name ---------------------------------------------------
        // The planner and Upgrades view show the translated choice name and fall back to the choice's asset name when
        // there is none; Temple assets carry a tier suffix ("Sacrifice 1"), which For() strips. Many choices differ only
        // in prefab data (stats, weapon), which the decompiled code doesn't show: those texts stay general.

        private static readonly Dictionary<string, string> UpgradeTexts = new()
        {
            // Castle Center
            ["Royal Training"] = "Makes the king stronger.",
            ["Builder's Guild"] = "Upgrades a house for free right away, then another one each night.",
            ["Magic Armor"] = "Enemies that hurt the king take damage back.",
            ["Assassin's Training"] = "The king's attack recharges more slowly, but timing it right as it comes back makes the next one recharge much faster.",
            ["Royal Mastery"] = "Makes the king stronger.",
            ["Commander"] = "Troops following the king keep fighting and move faster; troops holding a position get more health and damage.",
            ["Castle Up"] = "Walls and towers cost less to upgrade.",
            ["Godly Curse"] = "The king's first hit on each enemy each night also takes away part of its max health.",
            ["Empire Towers"] = "All towers deal more damage.",
            ["Empire Troups"] = "Melee troops deal more damage.",
            ["Empire Armor"] = "Troops take less damage from melee and ranged attacks.",
            ["Empire Growth"] = "More gold every dawn.",
            // Defense Tower (variants are prefab data)
            ["Castle Tower"] = "Tower variant with extra health.",
            ["Sniper Tower"] = "Tower variant with a different attack; see the in-game description.",
            ["Armored Tower"] = "Tower variant with much more health.",
            ["Bunker Tower"] = "Tower variant with a little extra health and a different attack.",
            ["Archers Spire"] = "Spire variant with extra health and a different attack.",
            ["Ballistic Spire"] = "Spire variant with a different attack; see the in-game description.",
            ["Fire Spire"] = "Spire variant with extra health and a fire attack.",
            ["Healing Spire"] = "Stops attacking and heals the king and troops nearby instead. Large health boost.",
            ["Insane Damage"] = "This tower deals much more damage.",
            ["Insane Range"] = "This tower shoots much further.",
            ["Insane Health"] = "This tower gets a huge amount of extra health.",
            ["Hunting Equipment"] = "This tower also earns gold every dawn.",
            // Mill
            ["Improved Plow"] = "Each later mill upgrade earns 1 extra gold per dawn.",
            ["Explosive Trap"] = "The mill and its fields explode when destroyed, damaging nearby enemies; the blast grows with mill level.",
            ["Scarecrows"] = "Adds scarecrows to this mill's fields.",
            ["Wind Spirits"] = "Wind spirits around the mill destroy enemy projectiles nearby, more often at higher mill levels.",
            // Royal Forge (researched over nights)
            ["Lighter Weaponry"] = "The king's automatic attack fires faster.",
            ["Additional Armor"] = "The king gets more max health and faster regeneration.",
            ["Blessed Weaponry"] = "The king's active attack recharges faster.",
            ["Sharper Weaponry"] = "The king deals more damage with every attack.",
            // Blacksmith (researched over nights)
            ["Melee Attack"] = "Melee attacks from the player's side deal more damage.",
            ["Ranged Attack"] = "Ranged attacks from the player's side deal more damage.",
            ["Melee Armor"] = "The player's side takes less damage from melee attacks.",
            ["Ranged Armor"] = "The player's side takes less damage from ranged attacks.",
            // Temple (for the next night only)
            ["Sacrifice"] = "At nightfall all troops die, but they respawn much faster that night.",
            ["Overload"] = "Tonight towers deal much more damage but slowly lose health.",
            ["Blessing"] = "Tonight the king gets more health, regeneration and damage but moves slower.",
            ["Ice Rain"] = "Tonight an ice bolt falls on the enemy nearest the king every few seconds.",
            ["Prayers"] = "Tonight shrines reach much further.",
            ["Wisdom"] = "Tonight buildings other than shrines slowly lose health, but any building destroyed drops gold.",
            // Troop choices
            ["Longbow Archers"] = "Archery Range troops: long-range archers.",
            ["Crossbowpeople"] = "Archery Range troops: crossbows.",
            ["Hunters"] = "Archery Range troops: hunters.",
            ["Fire Archers"] = "Archery Range troops: archers with fire arrows.",
            ["Knights"] = "Barracks troops: knights.",
            ["Speermen"] = "Barracks troops: spearmen.",
            ["Flails"] = "Barracks troops: flail fighters.",
            ["Berserks"] = "Barracks troops: berserkers.",
            ["Golem"] = "Hero's Quarter: a golem hero.",
            ["Support Mage"] = "Hero's Quarter: a support mage hero.",
            ["Firewing"] = "Hero's Quarter: a flying fire hero.",
            ["Lizzard Rider"] = "Hero's Quarter: a mounted lizard rider hero.",
        };

        // ---- Weapons (UnitPage weapon picker) ------------------------------------------------------------------------

        private const string InheritText = "Keeps the base unit's own weapon (its projectile, damage lists and splash).";

        private static readonly Dictionary<string, string> WeaponTexts = new()
        {
            ["inherit"] = InheritText,
            ["Base unit's"] = InheritText,
            ["Base unit's weapon"] = InheritText,
            ["Weapon"] = "Which weapon the unit attacks with. Another unit's weapon brings its projectile, damage lists and splash.",
        };

        // ---- Behaviours (BehaviourTypes, UnitPage) -----------------------------------------------------------------

        private const string SpawnOnDeathText = "When it dies, the chosen unit appears where it fell. The new one is elite if this one was, but skips the night's difficulty.";

        private static readonly Dictionary<string, string> BehaviourTexts = new()
        {
            [BehaviourTypes.SpawnOnDeath] = SpawnOnDeathText,
            ["Spawns on death"] = SpawnOnDeathText,
        };

        // ---- Stats (UnitDef stats / weapon / visuals, WaveSetDef numbers), by JSON key and editor label ---------------

        private const string MaxHpText = "Maximum health. Multiplied on spawn by the night's difficulty, elite status (x4) and perks.";
        private const string MoveSpeedText = "Movement speed in metres per second. Vanilla foot soldiers move about 4 to 6.";
        private const string CooldownText = "Seconds between attacks, for every attack the unit has.";
        private const string RangeText = "How far away the unit picks targets, for every attack it has.";
        private const string KeepDistanceText = "The unit stops moving once this close to its target, so ranged units keep their distance.";
        private const string AggroText = "Seconds the unit chases the king after he hits it. 0: it never chases him.";
        private const string DamageMultText = "Multiplies all damage the unit deals, on top of the night's difficulty, elite status (x3) and perks.";
        private const string ProjectileSpeedText = "How fast the unit's projectiles fly. No effect on melee weapons.";
        private const string ScaleText = "Size of the model relative to the base unit (1 = same). Visual only.";
        private const string DamageListText = "Damage per hit, by target: starting from 0, each modifier whose tags the target all has adds its bonus, then multiplies.";
        private const string SplashListText = "Damage to everything in the hit's blast area, worked out like the damage list. Only weapons whose hits have a blast area use it.";
        private const string ModAddText = "Added to the running damage total when the target has all this modifier's tags.";
        private const string ModMultText = "Multiplies the running damage total (after this modifier's bonus) when the target has all its tags.";
        private const string DifficultyText = "Night strength: enemy health x difficulty, damage x halfway between 1 and it. Shown as skulls.";
        private const string GoldText = "Coins the whole group drops when killed, spread over its enemies.";

        private static readonly Dictionary<string, string> StatTexts = new()
        {
            ["maxHp"] = MaxHpText, ["Hit points"] = MaxHpText, ["HP"] = MaxHpText,
            ["moveSpeed"] = MoveSpeedText,
            ["attackCooldown"] = CooldownText,
            ["attackRange"] = RangeText,
            ["keepDistance"] = KeepDistanceText,
            ["aggroTime"] = AggroText, ["Chase time"] = AggroText,
            ["damageMult"] = DamageMultText, ["Damage multiplier"] = DamageMultText,
            ["projectileSpeed"] = ProjectileSpeedText,
            ["scale"] = ScaleText, ["Size"] = ScaleText,
            ["meshScale"] = "Scale of the imported mesh in its own units, instead of fitting it to the base unit's height.",
            ["meshRotY"] = "Turns the imported mesh around the vertical axis, in degrees, to face it forward.",
            ["tint"] = "Multiplies the body colour. Elite enemies still use the game's golden elite material.",
            ["damage"] = DamageListText, ["Damage modifiers"] = DamageListText,
            ["splash"] = SplashListText, ["Splash modifiers"] = SplashListText,
            ["add"] = ModAddText, ["Bonus damage"] = ModAddText,
            ["mult"] = ModMultText, ["Damage ×"] = ModMultText,
            ["difficulty"] = DifficultyText,
            ["count"] = "How many enemies the group has.",
            ["interval"] = "Seconds between enemies of the group.",
            ["delay"] = "Seconds after nightfall before the group starts.",
            ["gold"] = GoldText,
            ["startingGold"] = "Gold the player starts the run with.",
        };

        // ---- Flags (build slots, spawn lines, spawns, props, unit movement) --------------------------------------------

        private const string CapText = "Upgrades stop at the castle's level (plus the allowance): the slot can be built, but not upgraded past it.";
        private const string FollowsText = "Built and upgraded together with the slot that unlocks it (a mill's fields, a gate's wall pieces); never bought on its own.";

        private static readonly Dictionary<string, string> FlagTexts = new()
        {
            ["Solid"] = "Units walk around it: added to the pathfinding at level start. Off: decoration units walk through.",
            ["StartDeactivated"] = "Hidden until its prerequisite passes the required level. Off: there from the start.",
            ["ActivatorUpgradesThis"] = FollowsText, ["followsActivator"] = FollowsText, ["AddOn"] = FollowsText,
            ["upgradeCap"] = CapText, ["Upgrades ≤ castle level"] = CapText, ["Upgrades ≤ castle"] = CapText,
            ["Allowance"] = "How many levels above the castle's the capped slots may reach.",
            ["canSpawnFlying"] = "Fliers may use this line. Read by the generated wave modes and the Builder's checks; hand-made waves can still use it.",
            ["canSpawnSmallGround"] = "Small ground enemies may use this line. Read by the generated wave modes and the Builder's checks.",
            ["canSpawnBigGround"] = "Large ground enemies may use this line. Read by the generated wave modes and the Builder's checks.",
            ["Ground"] = "Small ground enemies may use this spawn line.",
            ["Large"] = "Large ground enemies may use this spawn line.",
            ["Flying"] = "Flies: spawns in the air, moves on the fliers' graph and ignores walls. Comes from the base unit; pick a flying base to get it.",
            ["LargeUnit"] = "Large: spawns on the large-unit ground graph. Comes from the base unit; pick a large base to get it.",
            ["Elite"] = "Elite enemies have x4 health and x3 damage and look golden. Units they spawn on death are elite too.",
        };

        // ---- Tables ------------------------------------------------------------------------------------------------

        private static readonly Dictionary<DescKind, Dictionary<string, string>> Sources = new()
        {
            [DescKind.Tag] = TagTexts,
            [DescKind.SlotKind] = SlotKindTexts,
            [DescKind.Upgrade] = UpgradeTexts,
            [DescKind.Weapon] = WeaponTexts,
            [DescKind.Behaviour] = BehaviourTexts,
            [DescKind.Stat] = StatTexts,
            [DescKind.Flag] = FlagTexts,
        };

        private static readonly Dictionary<DescKind, Dictionary<string, string>> Tables =
            Sources.ToDictionary(p => p.Key, p => p.Value.ToDictionary(e => Normalize(e.Key), e => e.Value));
    }
}
