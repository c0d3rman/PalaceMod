using System.Collections.Generic;
using UnityEngine;

/*
    Candidates near a point for bodies (projectile hits, areas, pickups): the core's spatial query (units, buildings,
    the king, Builder entities) plus the extra candidates (vanilla projectiles). Falls back to the game's TagManager
    when the core's query isn't installed.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodySpatial
    {
        private static readonly List<TaggedObject> tagged = [];

        public static void Query(Vector3 center, float radius, List<LogicEntity> into)
        {
            into.Clear();
            var c = center.ToVec();
            if (LogicServices.Spatial != null) LogicServices.Spatial.Query(c, radius, into);
            else if (TagManager.instance != null)
            {
                tagged.Clear();
                TagManager.instance.FindAllTaggedObjectsWithTag(tagged, TagManager.ETag.AUTO_Alive);
                foreach (var t in tagged)
                {
                    if (t == null) continue;
                    var p = t.transform.position;
                    if (radius > 0 && (new Vector2(p.x - center.x, p.z - center.z)).sqrMagnitude > radius * radius) continue;
                    var e = LogicGame.EntityOf(t.gameObject);
                    if (e != null) into.Add(e);
                }
                if (LogicWorld.Current != null)
                    foreach (var e in LogicWorld.Current.Entities)
                        if (e.Body != null && e.Exists && e.Kind is not ("unit" or "building" or "king" or "castle") && (radius <= 0 || Vec.FlatDistance(e.Position, c) <= radius) && !into.Contains(e))
                            into.Add(e);
            }
            VanillaProjectiles.Query(center, radius, into);
        }
    }
}
