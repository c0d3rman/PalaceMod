using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

/*
    The Entity Studio's kinds (docs/builder-ux.md, "Entity Studio"): what "New thing" offers, the components a new
    thing of each kind starts with, and who uses a thing (enemies' attacks that launch it, levels that place it).
    Plain data, no Unity (tested in EntityStudioTests).
*/
namespace PalaceMod.Builder.Studio.Entities
{
    internal static class EntityKinds
    {
        public sealed class Kind
        {
            public string Id, Label, Description;
        }

        public static readonly Kind[] All =
        [
            new() { Id = "projectile", Label = "Projectile", Description = "Thrown or shot: it flies at a target, and its rules say what a hit does." },
            new() { Id = "prop", Label = "Prop", Description = "A thing in the world: it can block the way, be walked on, be hit, or carry rules (a gate, a barrel, a turret)." },
            new() { Id = "pickup", Label = "Pickup", Description = "Collected by walking over it: the king, or whoever its filter names." },
            new() { Id = "area", Label = "Area", Description = "A zone that notices what enters it, stays in it and leaves it." },
            new() { Id = "logic", Label = "Logic object", Description = "Invisible: it only holds rules and variables (a timer, a counter, a relay)." },
        ];

        public static Kind Find(string id) => All.FirstOrDefault(k => k.Id == id);

        /// <summary>The kind a template behaves as: its own "kind", else the component it has.</summary>
        public static string Of(EntityDef e) =>
            e == null ? null : e.Kind ?? (e.Projectile != null ? "projectile" : e.Area != null ? "area" : e.Pickup != null ? "pickup" : "prop");

        public static string Label(string kind) => Find(kind)?.Label ?? (string.IsNullOrEmpty(kind) ? "Thing" : char.ToUpperInvariant(kind[0]) + kind.Substring(1));

        /// <summary>A new thing of a kind, with the components that kind needs and sensible starting values.
        /// <paramref name="mesh"/> is its look (null: none).</summary>
        public static EntityDef Make(string kind, string id, string name, string mesh)
        {
            var e = new EntityDef { Id = id, Kind = kind, Name = name };
            if (mesh != null) e.Visual = new VisualsDef { Mesh = mesh };
            switch (kind)
            {
                case "projectile":
                    e.Team = "owner";
                    e.Lifetime = 8;
                    e.Projectile = new ProjectileDef { Speed = 12, Homing = 1, Arc = 0.2, Hits = "foe", HitRadius = 1 };
                    break;
                case "prop":
                    e.Team = "neutral";
                    e.Body = new BodyDef { Radius = 1, Height = 2, Solid = true };
                    break;
                case "pickup":
                    e.Team = "neutral";
                    e.Lifetime = 30;
                    e.Pickup = new PickupDef { By = "king", Radius = 1.5, Magnet = 4 };
                    break;
                case "area":
                    e.Team = "neutral";
                    e.Area = new AreaDef { Shape = new ShapeDef { Circle = new CircleDef { Radius = 4 } }, Watch = "unit", Show = "outline" };
                    break;
            }
            return e;
        }

        /// <summary>Mesh names to try for a new thing's look, in order (scenery names; the first one the map or the
        /// library has wins).</summary>
        public static string[] MeshHints(string kind) => kind switch
        {
            "projectile" => ["Rock", "Stone", "Boulder"],
            "prop" => ["Barrel", "Crate", "Rock", "Stone"],
            "pickup" => ["Chest", "Pot", "Barrel", "Crate"],
            _ => [],
        };

        /// <summary>A free id for a new thing ("new-projectile", "new-projectile-2", ...).</summary>
        public static string FreeId(string baseId, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken.Where(t => t != null), StringComparer.Ordinal);
            if (!set.Contains(baseId)) return baseId;
            for (int i = 2; ; i++) if (!set.Contains($"{baseId}-{i}")) return $"{baseId}-{i}";
        }

        /// <summary>Where a template is used: "Enemy name: attack 2" for units' attacks that launch it, and
        /// "Level name: placed (id)" for places that name it.</summary>
        public static List<string> UsedBy(DesignPack pack, string id)
        {
            var list = new List<string>();
            if (pack == null || id == null) return list;
            foreach (var u in pack.Units ?? [])
            {
                if (u?.Attacks == null) continue;
                for (int i = 0; i < u.Attacks.Count; i++)
                    if (u.Attacks[i]?.Launch?.Entity == id) list.Add($"{u.Name ?? u.Id}: attack {i + 1}");
            }
            foreach (var lv in pack.Levels ?? [])
                foreach (var p in lv?.Logic?.Places ?? [])
                    if (p?.Entity == id) list.Add($"{lv.Name ?? lv.Id}: placed ({p.Id})");
            return list;
        }

        /// <summary>The draft's things a launch can use: projectiles first, then the rest.</summary>
        public static List<EntityDef> Launchable(DesignPack pack) =>
            (pack?.Entities ?? []).Where(e => e?.Id != null).OrderBy(e => Of(e) == "projectile" ? 0 : 1).ThenBy(e => e.Name ?? e.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
