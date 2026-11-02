using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PalaceMod.Builder;
using UnityEngine;
using UnityEngine.SceneManagement;

// WP2 runtime patches: wave injection, template-aware spawning, custom unit names.
namespace PalaceMod
{
    public partial class BuilderManager
    {
        [HarmonyPatch(typeof(EnemySpawner), "Start")]
        private static class EnemySpawner_Start_Patch
        {
            private static void Prefix(EnemySpawner __instance)
            {
                if (Injector.Pending.waveSet == null) return;
                Injector.Apply(__instance);
            }
        }

        // The game spawns scene-placed enemies by activating them in place when enemyPrefab.scene.isLoaded. Our templates
        // live in the (loaded) DontDestroyOnLoad scene and must be instantiated instead, and GetComponentInChildren<T>()
        // must see into them although they're inactive. Swap those calls for template-aware ones; vanilla prefabs and
        // scene enemies behave exactly as before.
        [HarmonyPatch]
        private static class TemplateSpawn_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Spawn), nameof(Spawn.Update));
                yield return AccessTools.Method(typeof(EnemySpawner), nameof(EnemySpawner.StartSpawning));
                yield return AccessTools.Method(typeof(EnemySpawner), "GetWaveInfo");
            }

            private static readonly MethodInfo IsLoaded = AccessTools.PropertyGetter(typeof(Scene), nameof(Scene.isLoaded));
            private static readonly MethodInfo IsLoadedReplacement = AccessTools.Method(typeof(TemplateSpawn_Patch), nameof(IsLoadedNotTemplate));
            private static readonly MethodInfo InChildrenReplacement = AccessTools.Method(typeof(TemplateSpawn_Patch), nameof(InChildren));

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                int scenes = 0, lookups = 0;
                foreach (var ins in instructions)
                {
                    if (ins.Calls(IsLoaded))
                    {
                        // Scene.get_isLoaded is an instance call on a Scene address: same stack shape as a static (ref Scene).
                        ins.opcode = OpCodes.Call;
                        ins.operand = IsLoadedReplacement;
                        scenes++;
                    }
                    else if (ins.operand is MethodInfo m && m.IsGenericMethod && m.Name == nameof(GameObject.GetComponentInChildren)
                        && m.DeclaringType == typeof(GameObject) && m.GetParameters().Length == 0)
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = InChildrenReplacement.MakeGenericMethod(m.GetGenericArguments());
                        lookups++;
                    }
                    yield return ins;
                }
                if (scenes == 0 && original.Name != "GetWaveInfo")
                    Plugin.Logger.LogError($"Builder: {original.DeclaringType.Name}.{original.Name} no longer checks scene.isLoaded; custom units may not spawn");
            }

            private static bool IsLoadedNotTemplate(ref Scene scene) => scene.isLoaded && !(UnitFactory.TemplateScene.IsValid() && scene == UnitFactory.TemplateScene);

            private static T InChildren<T>(GameObject go) where T : Component =>
                UnitFactory.IsTemplateObject(go) ? go.GetComponentInChildren<T>(true) : go.GetComponentInChildren<T>();
        }

        [HarmonyPatch(typeof(TextTranslator), nameof(TextTranslator.Translate))]
        private static class TextTranslator_Translate_Patch
        {
            private static bool Prefix(string locaKey, ref string __result)
            {
                if (locaKey == null || !locaKey.StartsWith(UnitFactory.LocaPrefix)) return true;
                if (!UnitFactory.Texts.TryGetValue(locaKey, out var text)) return true;
                __result = text ?? "";
                return false;
            }
        }
    }
}
