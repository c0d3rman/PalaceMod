using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    The behaviour engine's World and Show actions (docs/builder-engine.md, "Actions": World, Show; workstream L2):
    gold, dropCoins, building, waves, spawnGroup, endLevel, effect, sound, message, floatText, setVisual, hud, shake,
    marker. The actions live in WorldVerbs, WaveVerbs, ShowVerbs and LevelHud; this manager registers them, ticks
    their timed parts, owns their patches (which do nothing outside a logic session) and the dev command
    `logic-world`. Infrastructure: no settings; acts only in Builder sessions.
*/
namespace PalaceMod
{
    public class LogicWorldVerbsManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            WorldVerbs.Register();
            WorldQueries.Service = WorldQueryService.Instance;
            TerrainVerbs.Register();
            // A level whose rules change the terrain gets its grid and tiled graphs at the start (no stall later).
            LogicWorld.Started += w => { if (UsesAction(w.LevelLogic, "terrain") || w.Templates.Values.Any(t => UsesAction(t, "terrain"))) TerrainVerbs.Prepare(); };
            WaveVerbs.Register();
            ShowVerbs.Register();
            LevelHud.Register();
            // Status visuals (`visual.fx`) use the same catalog as the `effect` action, parented to the carrier.
            StatusVisuals.SpawnFx = (id, parent) =>
            {
                var fx = GameFx.FindEffect(id) ?? throw new LogicError($"no game effect \"{id}\"{WorldCommon.Near(GameFx.Effects.Keys, id)}");
                return parent == null ? null : Object.Instantiate(fx.Prefab, parent.position, fx.Prefab.transform.rotation, parent);
            };
            LogicSaveState.Register("world", _ => new JObject
            {
                ["buildings"] = WorldVerbs.Capture(),
                ["waves"] = WaveVerbs.Capture(),
                ["hud"] = LevelHud.Capture(),
                ["terrain"] = TerrainVerbs.Capture(),
            }, (_, state) =>
            {
                WorldSession.Sync();
                WorldVerbs.Restore(state?["buildings"]);
                WaveVerbs.Restore(state?["waves"]);
                LevelHud.Restore(state?["hud"]);
                TerrainVerbs.Restore(state?["terrain"]);
            });
            DevCommands.Register("logic-world", "logic-world status | fx | sounds | hud | terrain   world and show actions (L2): state, the effect and sound catalogs", args =>
            {
                WorldSession.Sync();
                return (args.FirstOrDefault() ?? "status") switch
                {
                    "fx" => GameFx.Scan(),
                    "sounds" => string.Join(", ", GameFx.Sounds.Keys.OrderBy(k => k)),
                    "hud" => LevelHud.Status(),
                    "terrain" => TerrainVerbs.Status(),
                    "slots" => WorldVerbs.DescribeSlots(),
                    "gallery" => ShowVerbs.Gallery(),
                    _ => $"world={(LogicWorld.Current != null ? "yes" : "no")} groups={WaveVerbs.Groups.Count} (left {WaveVerbs.Groups.Sum(g => g.Left)}) " +
                         $"hpScaled={WaveVerbs.HpMult.Count} locked={WorldVerbs.Locked.Count} {ShowVerbs.Status()}" +
                         (EnemySpawner.instance != null ? $" wave={EnemySpawner.instance.Wavenumber}/{EnemySpawner.instance.WaveCount} spawning={EnemySpawner.instance.SpawningInProgress} " +
                             $"next=[{NightSummary(EnemySpawner.instance.Wavenumber + (WorldCommon.IsNight ? 0 : 1))}]" : ""),
                };
            });
        }

        private static string NightSummary(int idx)
        {
            var s = EnemySpawner.instance;
            if (idx < 0 || idx >= s.WaveCount) return "-";
            return string.Join(", ", s.waves[idx].spawns.Where(x => x != null).Select(x =>
                $"{x.count}x {x.enemyPrefab?.name}@{x.spawnLine?.name}{(x.Finished ? " done" : "")}{(WaveVerbs.HpMult.TryGetValue(x, out var m) ? $" hp x{m:0.##}" : "")}"));
        }

        public override void Update()
        {
            WorldSession.Sync();
            if (!WorldSession.Active) return;
            WaveVerbs.Tick(Time.deltaTime);
            ShowVerbs.Tick();
            LevelHud.Tick();
            TerrainVerbs.Tick(Time.deltaTime);
        }

        /// <summary>Whether a rules tree anywhere has an action {"do": name}.</summary>
        private static bool UsesAction(JToken t, string name) => t != null && t.SelectTokens("..do").Any(d => d.Type == JTokenType.String && (string)d == name);

        public override void OnDestroy()
        {
            LevelHud.Destroy();
            WorldOverlay.Destroy();
        }

        // ------------------------------------------------------------------ patches

        // A spawnGroup with spawns left keeps the night open: DayNightCycle ends the night when the spawner isn't
        // spawning and no enemy is left, so it sees the spawner as spawning for that check.
        [HarmonyPatch(typeof(DayNightCycle), "Update")]
        private static class KeepNightOpen
        {
            private static void Prefix(out bool __state)
            {
                __state = false;
                var spawner = EnemySpawner.instance;
                if (LogicWorld.Current == null || !WaveVerbs.GroupsPending || spawner == null || spawner.SpawningInProgress) return;
                WorldCommon.SpawningRef(spawner) = true;
                __state = true;
            }

            private static void Postfix(bool __state)
            {
                if (__state && EnemySpawner.instance != null) WorldCommon.SpawningRef(EnemySpawner.instance) = false;
            }
        }

        // `building lock`: no builds or upgrades while locked.
        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.CanBeUpgraded), MethodType.Getter)]
        private static class LockedSlots
        {
            private static void Postfix(BuildSlot __instance, ref bool __result)
            {
                if (__result && LogicWorld.Current != null && WorldVerbs.Locked.Count > 0 && WorldVerbs.Locked.Contains(__instance)) __result = false;
            }
        }

        // A locked, unbuilt slot stays hidden when the game switches it on (its parent was upgraded).
        [HarmonyPatch(typeof(BuildSlot))]
        private static class LockedSlotActivation
        {
            [HarmonyPatch(nameof(BuildSlot.Activate)), HarmonyPostfix]
            private static void Activate(BuildSlot __instance)
            {
                if (LogicWorld.Current != null && WorldVerbs.Locked.Count > 0) WorldVerbs.AfterActivate(__instance);
            }

            [HarmonyPatch(nameof(BuildSlot.ActivateIgrnoringActivatorLevel)), HarmonyPostfix]
            private static void ActivateIgnoring(BuildSlot __instance)
            {
                if (LogicWorld.Current != null && WorldVerbs.Locked.Count > 0) WorldVerbs.AfterActivate(__instance);
            }
        }

        // `waves scaleHp`: the spawn's prefab carries the multiplied max hp for the instantiate inside Spawn.Update (the
        // game then applies perks, elite and difficulty on top and fills hp), and gets its own value back right after.
        [HarmonyPatch(typeof(Spawn), nameof(Spawn.Update))]
        private static class ScaleSpawnHp
        {
            private static void Prefix(Spawn __instance, out (Hp hp, float max) __state)
            {
                __state = default;
                if (LogicWorld.Current == null || WaveVerbs.HpMult.Count == 0 || !WaveVerbs.HpMult.TryGetValue(__instance, out var mult)) return;
                var prefab = __instance.enemyPrefab;
                if (prefab == null || prefab.scene.isLoaded && !UnitFactory.IsTemplateObject(prefab)) return;
                var hp = prefab.GetComponentInChildren<Hp>(true);
                if (hp == null) return;
                __state = (hp, hp.maxHp);
                hp.maxHp *= mult;
            }

            private static void Postfix((Hp hp, float max) __state)
            {
                if (__state.hp != null) __state.hp.maxHp = __state.max;
            }
        }
    }
}
