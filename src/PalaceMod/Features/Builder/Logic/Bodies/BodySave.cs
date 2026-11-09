using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    Savestate part "bodies": what the core's respawn doesn't know about Builder bodies — a mover's place in its path
    or pose (and the object's position/heading), an area's `show`, and an interactable's lock and single use. The
    core restores entities first (placed ones rebuilt by the level, spawned ones respawned), then this runs.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodySave
    {
        public static JToken Capture(LogicWorld world)
        {
            var all = new JObject();
            foreach (var e in world.Entities)
            {
                var go = e.Go();
                if (go == null || go.GetComponent<BuilderBody>() == null && go.GetComponent<BuilderArea>() == null) continue;
                var o = new JObject
                {
                    ["pos"] = new JArray(go.transform.position.x, go.transform.position.y, go.transform.position.z),
                    ["rotY"] = go.transform.eulerAngles.y,
                };
                if (go.GetComponent<BuilderMover>() is { } m) o["mover"] = m.Capture();
                if (go.GetComponent<BuilderArea>() is { } a) o["show"] = a.Show;
                if (go.GetComponentInChildren<BuilderInteractor>() is { } i) { o["locked"] = i.Locked; o["used"] = i.Used; }
                all[e.Id] = o;
            }
            return all;
        }

        public static void Restore(LogicWorld world, JToken state)
        {
            if (state is not JObject all) return;
            foreach (var kv in all)
            {
                var e = world.Find(kv.Key);
                var go = e?.Go();
                if (go == null || kv.Value is not JObject o) continue;
                if (o["pos"] is JArray p && p.Count == 3)
                    go.transform.SetPositionAndRotation(new Vector3((float)p[0], (float)p[1], (float)p[2]), Quaternion.Euler(0f, (float?)o["rotY"] ?? 0f, 0f));
                if (o["mover"] is JObject mo && go.GetComponent<BuilderMover>() is { } m) m.Restore(mo);
                if ((string)o["show"] is { } show && go.GetComponent<BuilderArea>() is { } a) a.SetShow(show);
                if (go.GetComponentInChildren<BuilderInteractor>() is { } i) { i.Locked = (bool?)o["locked"] ?? false; i.Used = (bool?)o["used"] ?? false; }
            }
        }
    }
}
