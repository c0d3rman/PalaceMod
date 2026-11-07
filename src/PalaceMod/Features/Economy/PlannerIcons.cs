using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    Icons for the planner's chips and tiles, all the game's own sprites:
      - a choice upgrade: the choice's tile icon (BuildSlot.upgrades[...].upgradeBranches[...].choiceDetails.icon);
      - a building kind: the game has no icon per building, so each kind borrows the perk or choice icon that shows it
        (houses: Fortified Houses' houses, towers: Power Tower's tower, harbours: Big Harbours' anchor...);
      - a perk: Equippable.icon.
    Sprites are looked up by name among the loaded ones once per level.
*/
namespace PalaceMod.Economy
{
    internal static class PlannerIcons
    {
        // Building kind (a word in its name) -> the game sprites that picture it, best first.
        private static readonly (string Word, string[] Sprites)[] Kinds =
        [
            ("castle", ["014-castle", "crown"]),
            ("house", ["008-houses"]),
            ("field", ["007-pumpkin"]),
            ("mill", ["wind", "plow"]),
            ("mine", ["017-coal"]),
            ("harb", ["003-anchor"]),
            ("tower", ["014-tower", "015-watchtower"]),
            ("wall", ["explosive_walls", "013-blueprint"]),
            ("gate", ["explosive_walls", "013-blueprint"]),
            ("archer", ["archery", "006-target"]),
            ("barrack", ["011-shield", "003-spear"]),
            ("blacksmith", ["001-sword"]),
            ("hero", ["016-fist"]),
            ("temple", ["009-cross", "shrine"]),
            ("shrine", ["shrine"]),
            ("outpost", ["circus-2"]),
            ("bridge", ["timeber_scaffolding", "feather"]),
            ("stable", ["012-horse-head", "horse-2"]),
            ("mage", ["ancient-scroll"]),
            ("treasur", ["013-treasure-chest"]),
        ];

        private static Dictionary<string, Sprite> sprites;
        private static string spritesScene;
        private static readonly Dictionary<string, Sprite> byKind = [];

        private static Sprite Named(string name)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sprites == null || spritesScene != scene)
            {
                sprites = [];
                spritesScene = scene;
                byKind.Clear();
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s != null && !sprites.ContainsKey(s.name)) sprites[s.name] = s;
            }
            return sprites.TryGetValue(name, out var sprite) ? sprite : null;
        }

        /// <summary>The icon for a building kind (by its name), or null.</summary>
        public static Sprite Kind(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            Named(""); // refresh per level
            if (byKind.TryGetValue(typeName, out var cached)) return cached;
            string lower = typeName.ToLowerInvariant();
            Sprite found = null;
            foreach (var (word, names) in Kinds)
            {
                if (!lower.Contains(word)) continue;
                found = names.Select(Named).FirstOrDefault(s => s != null);
                if (found != null) break;
            }
            byKind[typeName] = found;
            return found;
        }

        /// <summary>The icon of a step's choice in the live level, or null (not a choice, or not found).</summary>
        public static Sprite Choice(EconSnapshot snap, EconSlot slot, int level, string choice)
        {
            if (snap == null || slot == null || level < 1 || level > slot.Steps.Count) return null;
            var step = slot.Steps[level - 1];
            if (!step.IsChoice) return null;
            if (!snap.Objects.TryGetValue(slot.Index, out var obj) || obj == null || obj.upgrades == null || level - 1 >= obj.upgrades.Count) return null;
            int branch = EconSim.BranchIndex(step, choice);
            var branches = obj.upgrades[level - 1].upgradeBranches;
            if (branches == null || branch >= branches.Count || branches[branch].choiceDetails == null) return null;
            return UpgradeInfo.Icon(branches[branch].choiceDetails);
        }

        /// <summary>A step's icon: its choice's if it's a choice, else its building kind's.</summary>
        public static Sprite Step(EconSnapshot snap, EconSlot slot, int level, string choice) =>
            Choice(snap, slot, level, choice) ?? Kind(slot?.TypeName);

        /// <summary>The perk with this key (Equippable.name) in the game's list, or null.</summary>
        public static Equippable Perk(string key) =>
            PerkManager.instance == null ? null : PerkManager.instance.allEquippables.FirstOrDefault(e => e != null && e.name == key);

        /// <summary>Short name for a chip: "Defense Tower" -> "Tower", "Castle Center" -> "Castle".</summary>
        public static string Short(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "?";
            string lower = typeName.ToLowerInvariant();
            if (lower.Contains("castle")) return "Castle";
            if (lower.Contains("tower")) return "Tower";
            if (lower.Contains("archer")) return "Archers";
            if (lower.Contains("hero")) return "Heroes";
            if (lower.Contains("mine")) return "Mine";
            if (lower.Contains("blacksmith")) return "Smith";
            var words = typeName.Split(' ');
            return words.Length > 1 && typeName.Length > 9 ? words[words.Length - 1] : typeName;
        }

        public static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };
    }
}
