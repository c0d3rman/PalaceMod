using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

/*
    The loadout for custom runs that start straight on a map (the editor's Test play, `builder play`), which skip the
    pre-level menu's loadout screen. Without it the king would start with whatever happened to be equipped, often
    nothing: no weapon, and an empty "Loadout" in the pause menu.

    Choice (the editor's "Test loadout" row): "Last used" takes the loadout of the last run started on that map from
    its pre-level menu (remembered in BepInEx/PalaceBuilder/loadouts.json; else the last one the map was beaten with;
    else what's equipped now), or a weapon picked alone. Either way: locked items are dropped, perks are kept to the
    map's limit, and a weapon is added if there's none. A map with a fixed loadout always gets that.
    Custom Runs from a pre-level menu don't use this: Play goes through the game's own loadout screen.
*/
namespace PalaceMod.Builder
{
    internal static class TestLoadout
    {
        /// <summary>Null: last used on the map. Otherwise the Equippable name of a weapon to play with alone.</summary>
        public static string Choice;

        private static Dictionary<string, List<string>> remembered;

        private static string Path => BuilderServices.Packs.Root is { } root ? System.IO.Path.Combine(root, "loadouts.json") : null;

        private static List<Equippable> All => PerkManager.instance?.allEquippables?.Where(e => e != null).ToList() ?? [];

        /// <summary>Unlocked weapons in the loadout screen's order (the first is the one it equips by default).</summary>
        public static List<Equippable> Weapons => All.Where(e => e is EquippableWeapon && e.IsUnlocked).ToList();

        public static string Name(Equippable e) => e == null ? "?" : TextTranslator.Translate(e.LOCIDENTIFIER_NAME) is { Length: > 0 } t && t != e.LOCIDENTIFIER_NAME ? t : e.displayName ?? e.name;

        private static LevelInfo Info(string host) => LevelProgressManager.instance?.GetLevelInfoFromSceneName(host);

        public static bool IsFixed(string host) => Info(host)?.HasFixedLoadout == true;

        /// <summary>The loadout a test run on <paramref name="host"/> starts with.</summary>
        public static List<Equippable> For(string host)
        {
            var info = Info(host);
            if (info != null && info.HasFixedLoadout) return info.fixedLoadout.Where(e => e != null).ToList();
            List<Equippable> picked;
            var weapon = Choice != null ? Weapons.FirstOrDefault(w => w.name == Choice) : null;
            if (weapon != null) picked = [weapon];
            else picked = LastUsed(host);

            // What the loadout screen would allow: unlocked items, one weapon, the map's perk limit.
            picked = picked.Where(e => e != null && e.IsUnlocked).Distinct().ToList();
            var weapons = picked.OfType<EquippableWeapon>().Cast<Equippable>().ToList();
            foreach (var extra in weapons.Skip(1)) picked.Remove(extra);
            int maxPerks = info?.maxPerkCount ?? 5;
            foreach (var extra in picked.OfType<EquippablePerk>().Skip(maxPerks).Cast<Equippable>().ToList()) picked.Remove(extra);
            if (weapons.Count == 0 && Weapons.FirstOrDefault() is { } first) picked.Insert(0, first);
            return picked;
        }

        /// <summary>Whether "last used" finds anything for the map (else it's the default: the first weapon).</summary>
        public static bool HasLastUsed(string host) => LastUsed(host).Any(e => e != null && e.IsUnlocked);

        private static List<Equippable> LastUsed(string host)
        {
            Load();
            var all = All;
            if (host != null && remembered.TryGetValue(host, out var names))
                return names.Select(n => all.FirstOrDefault(e => e.name == n)).Where(e => e != null).ToList();
            var beatenWith = LevelProgressManager.instance?.GetLevelDataForScene(host)?.levelHasBeenBeatenWith;
            if (beatenWith != null && beatenWith.Count > 0 && beatenWith[beatenWith.Count - 1] is { Count: > 0 } last) return last.ToList();
            return PerkManager.instance?.CurrentlyEquipped?.ToList() ?? [];
        }

        /// <summary>Equips the test loadout for <paramref name="host"/> (before its scene loads).</summary>
        public static void Apply(string host, List<Equippable> loadout)
        {
            if (PerkManager.instance == null) return;
            PerkManager.ClearAllEquipped();
            foreach (var e in loadout) PerkManager.SetEquipped(e, true);
            Log.For<BuilderManager>().Info($"Test loadout on {host}: {Describe(loadout)}");
        }

        public static string Describe(List<Equippable> loadout)
        {
            if (loadout.Count == 0) return "nothing";
            var weapon = loadout.FirstOrDefault(e => e is EquippableWeapon);
            int rest = loadout.Count - (weapon != null ? 1 : 0);
            string head = weapon != null ? Name(weapon) : "no weapon";
            return rest == 0 ? head : $"{head} + {rest} more";
        }

        public static string List(List<Equippable> loadout) => loadout.Count == 0 ? "Nothing equipped." : string.Join(", ", loadout.Select(Name));

        /// <summary>Remembers the loadout a run on <paramref name="host"/> starts with (the pre-level menu's Play).</summary>
        public static void Remember(string host)
        {
            var equipped = PerkManager.instance?.CurrentlyEquipped;
            if (string.IsNullOrEmpty(host) || equipped == null || equipped.Count == 0 || IsFixed(host)) return;
            Load();
            var names = equipped.Where(e => e != null).Select(e => e.name).ToList();
            if (remembered.TryGetValue(host, out var old) && old.SequenceEqual(names)) return;
            remembered[host] = names;
            Save();
        }

        private static void Load()
        {
            if (remembered != null) return;
            remembered = [];
            var path = Path;
            if (path == null || !File.Exists(path)) return;
            try { remembered = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? []; }
            catch (Exception e) { Log.For<BuilderManager>().Warn($"Couldn't read {path}: {e.Message}"); }
        }

        private static void Save()
        {
            var path = Path;
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(remembered, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e) { Log.For<BuilderManager>().Warn($"Couldn't write {path}: {e.Message}"); }
        }
    }
}
