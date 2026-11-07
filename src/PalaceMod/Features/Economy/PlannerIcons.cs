using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    Icons for the planner's chips, tiles and map markers:
      - a choice upgrade: the choice's tile icon (BuildSlot.upgrades[...].upgradeBranches[...].choiceDetails.icon);
      - a building kind: the game has no icon per building, so each kind uses a game sprite that pictures that
        building (houses: Fortified Houses' houses, towers: a tower, harbours: an anchor...) or, where the game has
        none that does (walls, gates, bridges, the mill, the temple...), the mod's own (src/PalaceMod/Assets/planner_*.png,
        drawn by branding/planner/draw_icons.py);
      - a perk: Equippable.icon.
    Sprites are looked up by name among the loaded ones once per level.
*/
namespace PalaceMod.Economy
{
    internal static class PlannerIcons
    {
        // Building kind (a word in its name) -> the game sprites that picture it, best first.
        // Building kind (a word in its name) -> the sprites that picture it, best first ("mod:" = the mod's own).
        // First match wins, so the longer names go before the words they contain ("royal forge" before "forge").
        private static readonly (string Word, string[] Sprites)[] Kinds =
        [
            ("castle", ["014-castle"]),
            ("house", ["008-houses"]),
            ("field", ["007-pumpkin"]),
            ("mill", ["mod:mill"]),
            ("mine", ["017-coal"]),
            ("harb", ["003-anchor"]),
            ("tower", ["014-tower", "004-tower"]),
            ("gate", ["mod:gate"]),
            ("wall", ["mod:wall"]),
            ("barricade", ["mod:barricades"]),
            ("bridge", ["mod:bridge"]),
            ("archer", ["002-archery", "archery"]),
            ("barrack", ["swords", "011-shield"]),
            ("royal forge", ["mod:royal_forge"]),
            ("forge", ["mod:royal_forge"]),
            ("blacksmith", ["mod:blacksmith"]),
            ("hero", ["knight"]),
            ("temple", ["mod:temple"]),
            ("shrine", ["shrine"]),
            ("summon", ["mod:summoning_circle"]),
            ("treasur", ["022-bank"]),
        ];

        /// <summary>A building lost on a night.</summary>
        public static Sprite Lost => Named("graveyard") ?? Named("023-skull");

        /// <summary>The padlock on markers of slots that can't take a step yet.</summary>
        public static Sprite Lock => Mod("lock");

        private static readonly Dictionary<string, Sprite> own = [];

        private static Sprite Mod(string name)
        {
            if (own.TryGetValue(name, out var s) && s != null) return s;
            return own[name] = ModStyle.AssetSprite($"planner_{name}.png");
        }

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
                found = names.Select(n => n.StartsWith("mod:") ? Mod(n.Substring(4)) : Named(n)).FirstOrDefault(s => s != null);
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
            if (lower.Contains("royal forge")) return "Forge";
            if (lower.Contains("summon")) return "Circle";
            var words = typeName.Split(' ');
            return words.Length > 1 && typeName.Length > 9 ? words[words.Length - 1] : typeName;
        }

        public static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };
    }
}
