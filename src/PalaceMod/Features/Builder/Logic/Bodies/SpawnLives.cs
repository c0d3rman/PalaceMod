using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    How long a spawned entity stays (docs/builder-engine.md, "Actions": spawn `for`; "Game rules": spawnedDead).

    - spawn `for` (seconds, "night" = until the next dawn, "day" = until the next dusk) makes what it spawns temporary.
      When the time is up it leaves, alive or dead: `expire` on it, then it's removed without death effects, coins or
      a `death` event. Dawn and dusk are handled in the game's own DawnCallBeforeSunrise / DuskCall (the
      LogicBodiesManager patches), so a "night" unit is gone before the game revives knocked-out troops after sunrise.
    - Without `for` a spawned unit stays. A player unit the game knocks out (the player side's death) revives at dawn
      like the game's own troops, unless the level's logic.game.spawnedDead is "remove": then it's removed at dawn.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class SpawnLives
    {
        private static readonly Dictionary<LogicEntity, Until> temporary = [];

        public static int Count => temporary.Count;

        public static void Mark(LogicEntity e, Until until)
        {
            if (e != null && until.Set) temporary[e] = until;
        }

        public static void Clear() => temporary.Clear();

        /// <summary>The `for: seconds` ones whose time is up (the bodies manager's 10 Hz poll).</summary>
        public static void Poll(LogicWorld w)
        {
            if (w == null || temporary.Count == 0) return;
            foreach (var kv in temporary.ToList())
            {
                if (!kv.Key.Exists) { temporary.Remove(kv.Key); continue; }
                if (kv.Value.At is double t && w.Time >= t) Leave(kv.Key);
            }
        }

        /// <summary>Dawn (before sunrise): "night" ones leave, and dead spawned player units under spawnedDead "remove".</summary>
        public static void Dawn(LogicWorld w)
        {
            if (w == null) return;
            foreach (var kv in temporary.ToList()) if (kv.Value.Phase == "night") Leave(kv.Key);
            if (GameRules.Enum("spawnedDead", "revive") != "remove") return;
            foreach (var e in w.Entities.Where(e => e.Spawned && e.Kind == "unit" && e.Exists && !e.Alive).ToList())
                Remove(e);
        }

        /// <summary>Dusk: "day" ones leave.</summary>
        public static void Dusk(LogicWorld w)
        {
            if (w == null) return;
            foreach (var kv in temporary.ToList()) if (kv.Value.Phase == "day") Leave(kv.Key);
        }

        private static void Leave(LogicEntity e)
        {
            temporary.Remove(e);
            if (!e.Exists) return;
            BodyDev.Raise("expire", e, null, 0, e.Position);
            Remove(e);
        }

        private static void Remove(LogicEntity e)
        {
            temporary.Remove(e);
            e.StaticPos = e.Position;
            LogicServices.Factory?.Despawn(e, false, false);
        }

        // ---- savestates (part "lifetimes") --------------------------------------------------------------------------

        public static JToken Capture(LogicWorld w)
        {
            var o = new JObject();
            foreach (var kv in temporary.Where(kv => kv.Key.Exists)) o[kv.Key.Id] = kv.Value.ToJson(w.Time);
            return o;
        }

        public static void Restore(LogicWorld w, JToken state)
        {
            temporary.Clear();
            if (state is not JObject o) return;
            foreach (var p in o.Properties())
                if (w.Find(p.Name) is { } e) Mark(e, Until.FromJson(p.Value, w.Time));
        }
    }
}
