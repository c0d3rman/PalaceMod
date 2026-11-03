using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    Player-facing names for enemy spawn lines, shared by the pause menu's spawn panel (EnemyPreviewManager) and the
    Compendium's Waves tab.

    Level designers named most spawn lines after landmarks ("Main Bridge", "Graveyard"); some are generic ("Spawn 3",
    "Marker Pos" under a summoning statue, "SpawnLine" under a boss). Those get the landmark they're attached to, or
    else a compass direction from the castle (north = up the screen). Lines that end up with the same name ("Eldermolde
    Statue (2)" and "(3)", two unnamed lines to the west) read as one place; callers group by the name.
*/
namespace PalaceMod
{
    internal static class SpawnLocations
    {
        private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
            { "", "spawn", "spawnline", "spawn line", "spawnpoint", "spawn point", "marker", "marker pos", "markerpos", "line", "pos", "position" };

        private static readonly string[] Directions = ["North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest"];

        /// <summary>The name to show for a spawn line: its landmark, else a compass direction; "(air)" for flyers.</summary>
        public static string Name(Transform line)
        {
            if (line == null) return "Unknown";
            var (name, air) = Clean(line.name);
            string place = (IsGeneric(name) ? Landmark(line.parent) : name) ?? Compass(MarkerPosition(line));
            return air ? $"{place} (air)" : place;
        }

        /// <summary>The point the game puts the daytime spawn marker at.</summary>
        public static Vector3 MarkerPosition(Transform line) => line.childCount > 0
            ? (line.GetChild(0).position + line.GetChild(line.childCount - 1).position) / 2f
            : line.position;

        private static (string Name, bool Air) Clean(string raw)
        {
            string name = raw.Trim();
            bool air = false;
            if (name.StartsWith("Fly - ", StringComparison.OrdinalIgnoreCase)) { name = name.Substring(6); air = true; }
            if (name.EndsWith(" air", StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - 4); air = true; }
            if (name.Equals("air", StringComparison.OrdinalIgnoreCase)) { name = ""; air = true; }
            name = StripCopySuffix(name);
            if (name.StartsWith("Bot ", StringComparison.Ordinal)) name = "Bottom " + name.Substring(4);
            if (name.Any(char.IsLetter) && !name.Any(char.IsLower)) name = TitleCase(name);
            return (name.Trim(), air);
        }

        // "Eldermolde Statue (2)" -> "Eldermolde Statue"
        private static string StripCopySuffix(string name)
        {
            name = name.Trim();
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && name.EndsWith(")") && name.Substring(open + 2, name.Length - open - 3).All(char.IsDigit)) name = name.Substring(0, open);
            return name;
        }

        private static bool IsGeneric(string name) => Generic.Contains(name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));

        private static string Landmark(Transform parent)
        {
            if (parent == null) return null;
            string name = StripCopySuffix(parent.name);
            if (name.StartsWith("ES ", StringComparison.Ordinal)) return null; // the level's spawn container, not a place
            if (name.StartsWith("E ", StringComparison.Ordinal)) name = name.Substring(2); // enemy prefab prefix
            name = Clean(name).Name;
            return IsGeneric(name) ? null : name;
        }

        private static string TitleCase(string s) =>
            string.Join(" ", s.ToLowerInvariant().Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));

        private static string Compass(Vector3 position)
        {
            var (up, right) = CameraBasis();
            var d = position - CastleCenter.CastleCenterPosition;
            float angle = Mathf.Atan2(Vector3.Dot(d, right), Vector3.Dot(d, up)) * Mathf.Rad2Deg;
            int sector = Mathf.RoundToInt((angle + 360f) % 360f / 45f) % 8;
            return Directions[sector];
        }

        private static (Vector3 Up, Vector3 Right) CameraBasis()
        {
            var cam = Camera.main;
            if (cam == null) return (Vector3.forward, Vector3.right);
            var up = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
            up.Normalize();
            return (up, Vector3.Cross(Vector3.up, up));
        }
    }
}
