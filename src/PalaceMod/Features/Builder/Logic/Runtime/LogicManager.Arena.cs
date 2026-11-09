using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Enemies;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The Enemy Studio arena (docs/builder-engine.md, "Sessions, the Studio and the arena"): while a fight runs, the
    enemy's rules run in a throwaway world built from the Studio's pack (no level logic; level vars live and die with
    it). Arena units fight in the day, so the arena's game state reports night.
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicManager
    {
        private bool arenaWorld;

        [HarmonyPatch(typeof(EnemyArena), nameof(EnemyArena.Start))]
        private static class ArenaStart_Patch
        {
            private static void Postfix(DesignPack pack)
            {
                if (instance == null || !EnemyArena.Running) return;
                try { instance.StartArenaWorld(pack); }
                catch (Exception e) { Log.For<LogicManager>().Error("Couldn't start the arena's behaviour", e); }
            }
        }

        private void StartArenaWorld(DesignPack pack)
        {
            if (World != null) EndWorld("arena fight starting");
            var json = LogicPack.Expand(JObject.Parse(PackSerializer.WriteCompact(pack)), pack, out var issues);
            var templates = new List<(string, JObject)>();
            if (json["units"] is JArray units)
                for (int i = 0; i < units.Count; i++)
                    if (units[i] is JObject u)
                    {
                        var t = (JObject)u.DeepClone();
                        t["kind"] ??= "unit";
                        t["_path"] = $"units[{i}]";
                        templates.Add(((string)u["id"], t));
                    }
            if (json["entities"] is JArray ents)
                for (int i = 0; i < ents.Count; i++)
                    if (ents[i] is JObject en) { en["_path"] = $"entities[{i}]"; templates.Add(((string)en["id"], en)); }

            var world = new LogicWorld();
            LogicServices.Game = new ArenaGameState(game);
            LogicServices.Spatial = spatial;
            world.TraceListener = OnTrace;
            LogicWorld.Current = world;
            arenaWorld = true;
            startedScene = SceneManager.GetActiveScene();
            loggedTrace.Clear();
            world.Load(null, templates, (json["statuses"] as JArray)?.OfType<JObject>(), (json["presets"] as JArray)?.OfType<JObject>(), "arena");
            foreach (var issue in issues) world.CompileErrors.Add(issue);
            // Arena units already exist; their BuilderEntity.Start registers them on their first frame.
            LogicWorld.RaiseStarted(world);
            world.BeginLevel();
            Log.Info($"Behaviour: arena fight with {templates.Count} templates, {world.CompileErrors.Count} problems");
        }

        /// <summary>The arena's game state: always night (arena units fight in the day), night 1.</summary>
        private sealed class ArenaGameState(IGameState inner) : IGameState
        {
            public int Night => 1;
            public bool IsNight => true;
            public int Nights => 1;
            public double NightTime => LogicWorld.Current?.Time ?? 0;
            public double NightProgress => 0;
            public int Gold => inner.Gold;
            public int Income => 0;
            public int EnemiesAlive => inner.EnemiesAlive;
            public double Difficulty => 1;
            public LogicEntity King => null;
            public LogicEntity Castle => null;
            public int WaveCount(int night, string filter) => 0;
            public IReadOnlyList<string> WaveUnits(int night) => [];
            public string StrongestUnit(int night) => null;
            public Vec SpawnLine(string name) => throw new LogicError("the arena has no spawn lines");
            public Vec PointNear(Vec p, float r, LogicRandom rng) => inner.PointNear(p, r, rng);
        }
    }
}
