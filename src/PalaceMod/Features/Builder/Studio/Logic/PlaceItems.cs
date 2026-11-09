using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The level's placed logic entities (levels[i].logic.places[]: areas, points, paths, timers, levers, doors, spawners…)
    as Studio items ("place:<id>"): picked, selected, moved, turned, duplicated and deleted like slots and props, drawn as
    a coloured ring (areas: their outline) with their name. Their behaviour is edited in the Behaviour tab.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class PlaceItems : IStudioItemSource
    {
        public const string Prefix = "place:";
        public static readonly PlaceItems Instance = new();

        string IStudioItemSource.Prefix => Prefix;

        public static readonly Color AreaColor = new(0.86f, 0.36f, 0.32f);
        public static readonly Color LogicColor = new(0.66f, 0.52f, 0.92f);
        public static readonly Color PropColor = new(0.7f, 0.7f, 0.72f);
        public static readonly Color PickupColor = new(0.98f, 0.82f, 0.3f);
        public static readonly Color PointColor = new(0.45f, 0.8f, 0.95f);

        /// <summary>A place's kind: its own "kind", or its template's, or "entity".</summary>
        public static string KindOf(JObject place)
        {
            if (place == null) return null;
            if ((string)place["kind"] is string k) return k;
            string template = (string)place["entity"];
            var pack = Studio.Draft?.Pack;
            var def = (LogicDoc.Get(pack, "entities") as JArray)?.OfType<JObject>().FirstOrDefault(e => (string)e["id"] == template);
            return (string)def?["kind"] ?? "entity";
        }

        public static string KindOf(string id) => KindOf(LogicDoc.Place(OwnerLogic(), id));

        private static JObject OwnerLogic() => Studio.Level == null ? null : LogicDoc.Logic(Studio.Level);

        public static string Title(string id)
        {
            var place = LogicDoc.Place(OwnerLogic(), id);
            return (string)place?["name"] ?? id;
        }

        public static Vector3 Pos(JObject place) =>
            place?["pos"] is JArray p && p.Count >= 3 ? new Vector3((float)p[0], (float)p[1], (float)p[2])
            : place?["pos"] is JArray q && q.Count == 2 ? new Vector3((float)q[0], 0f, (float)q[1]) : Vector3.zero;

        public static Color ColorOf(string kind) => kind switch
        {
            "area" => AreaColor,
            "logic" => LogicColor,
            "pickup" => PickupColor,
            "point" or "path" => PointColor,
            _ => PropColor,
        };

        public static string KindLabel(string kind) => kind switch
        {
            "area" => "Area",
            "logic" => "Logic",
            "point" => "Point",
            "path" => "Path",
            "pickup" => "Pickup",
            "projectile" => "Projectile",
            "prop" => "Prop",
            _ => "Thing",
        };

        public IEnumerable<StudioItemSpec> Items(LevelDef lv)
        {
            var logic = LogicDoc.Logic(lv);
            foreach (var place in LogicDoc.Places(logic)?.OfType<JObject>() ?? [])
            {
                string id = (string)place["id"];
                if (id == null) continue;
                string kind = KindOf(place);
                var spec = new StudioItemSpec
                {
                    Id = id,
                    Name = (string)place["name"] ?? id,
                    TypeName = KindLabel(kind) + (place["entity"] is JValue t ? $" · {(string)t}" : ""),
                    Pos = Pos(place),
                    Yaw = (float?)place["rotY"] ?? 0f,
                    Color = ColorOf(kind),
                    Size = kind == "point" ? 1.6f : 2.6f,
                    Outline = Outline(place),
                };
                if ((place["active"]?.Type == JTokenType.Boolean) && !(bool)place["active"]) spec.Color = Color.Lerp(spec.Color, Color.gray, 0.6f);
                yield return spec;
            }
        }

        /// <summary>An area's shape (circle, rect, polygon) relative to its position, or a path's points.</summary>
        public static List<Vector3> Outline(JObject place)
        {
            if (place?["points"] is JArray pts && pts.Count >= 2)
            {
                var origin = Pos(place);
                return pts.OfType<JArray>().Select(p => p.Count >= 3 ? new Vector3((float)p[0], (float)p[1], (float)p[2]) - origin : new Vector3((float)p[0], 0f, (float)p[1]) - new Vector3(origin.x, 0f, origin.z)).ToList();
            }
            var shape = place?["area"]?["shape"] as JObject ?? place?["shape"] as JObject;
            if (shape == null) return null;
            if (shape["circle"]?["radius"] is JToken r)
            {
                float radius = (float)r;
                var list = new List<Vector3>();
                for (int i = 0; i <= 32; i++) { float a = i / 32f * Mathf.PI * 2f; list.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius)); }
                return list;
            }
            if (shape["rect"]?["size"] is JArray size && size.Count >= 2)
            {
                float w = (float)size[0] / 2f, d = (float)size[1] / 2f;
                return [new(-w, 0, -d), new(w, 0, -d), new(w, 0, d), new(-w, 0, d), new(-w, 0, -d)];
            }
            if (shape["polygon"]?["points"] is JArray poly && poly.Count >= 3)
            {
                var list = poly.OfType<JArray>().Select(p => new Vector3((float)p[0], 0f, (float)p[p.Count - 1])).ToList();
                list.Add(list[0]);
                return list;
            }
            return null;
        }

        public void SetPose(LevelDef lv, string id, Vector3 pos, float yaw) => LogicDoc.EditLogic(lv, logic =>
        {
            var place = LogicDoc.Place(logic, id);
            if (place == null) return;
            var old = Pos(place);
            place["pos"] = new JArray(LayoutScene.Round(pos.x), LayoutScene.Round(pos.y), LayoutScene.Round(pos.z));
            float ry = LayoutScene.Round(Mathf.Repeat(yaw, 360f));
            if (Mathf.Abs(Mathf.DeltaAngle(ry, 0f)) < 0.05f) place.Remove("rotY"); else place["rotY"] = ry;
            // A path moves with its points.
            if (place["points"] is JArray pts)
            {
                var delta = pos - old;
                foreach (var p in pts.OfType<JArray>())
                {
                    p[0] = LayoutScene.Round((float)p[0] + delta.x);
                    if (p.Count >= 3) { p[1] = LayoutScene.Round((float)p[1] + delta.y); p[2] = LayoutScene.Round((float)p[2] + delta.z); }
                    else p[1] = LayoutScene.Round((float)p[1] + delta.z);
                }
            }
        });

        public void Delete(LevelDef lv, string id) => LogicDoc.EditLogic(lv, logic =>
        {
            var places = LogicDoc.Places(logic);
            var place = LogicDoc.Place(logic, id);
            place?.Remove();
            if (places?.Count == 0) logic.Remove("places");
        });

        public string Duplicate(LevelDef lv, string id, Vector3 offset)
        {
            string made = null;
            LogicDoc.EditLogic(lv, logic =>
            {
                var place = LogicDoc.Place(logic, id);
                if (place == null) return;
                var copy = (JObject)place.DeepClone();
                string prefix = id.Contains('-') ? id.Substring(0, id.LastIndexOf('-')) : id;
                made = LogicDoc.NewPlaceId(logic, prefix);
                copy["id"] = made;
                if (copy["name"] != null) copy["name"] = LogicDoc.NameFor((string)copy["name"], LogicDoc.Names(logic));
                var pos = Pos(place) + offset;
                copy["pos"] = new JArray(LayoutScene.Round(pos.x), LayoutScene.Round(pos.y), LayoutScene.Round(pos.z));
                ((JArray)logic["places"]).Add(copy);
            });
            return made;
        }

        /// <summary>Adds a place from a palette object at a pose (one undo step); returns its Studio key.</summary>
        public static string Add(LogicObject obj, Vector3 pos, float yaw)
        {
            string key = null;
            Studio.Edit($"Place {obj.Name}", lv => LogicDoc.EditLogic(lv, logic =>
            {
                var place = (JObject)obj.Place.DeepClone();
                string id = LogicDoc.NewPlaceId(logic, obj.IdPrefix);
                place.AddFirst(new JProperty("id", id));
                place["pos"] = new JArray(LayoutScene.Round(pos.x), LayoutScene.Round(pos.y), LayoutScene.Round(pos.z));
                if (Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) > 0.05f) place["rotY"] = LayoutScene.Round(Mathf.Repeat(yaw, 360f));
                if (place["points"] is JArray pts)
                    foreach (var p in pts.OfType<JArray>())
                    {
                        p[0] = LayoutScene.Round((float)p[0] + pos.x);
                        p[1] = LayoutScene.Round((float)p[1] + pos.y);
                        p[2] = LayoutScene.Round((float)p[2] + pos.z);
                    }
                if (logic["places"] is not JArray places) logic["places"] = places = new JArray();
                places.Add(place);
                key = Prefix + id;
                Studio.RunPlaceHooks(lv, key);
            }));
            if (key != null) Studio.RaiseItemPlaced(key);
            return key;
        }
    }
}
