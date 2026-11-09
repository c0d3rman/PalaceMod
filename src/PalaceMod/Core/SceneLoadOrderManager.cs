using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Scene changes unload the old scene before they load the new one.

    The game's SceneTransitionManager.SceneTransitionAnimation starts UnloadSceneAsync for every open scene (except _UI)
    and LoadSceneAsync for the next one at the same moment, and waits for all of them together. Players always go
    level -> level select -> level, so the scene being loaded is never the one being unloaded. A level started from
    inside a level (Builder play/test/sandbox, `dev.py cmd level`, retry day) unloads and loads the *same* scene at
    once, and that crashes natively now and then (about 1 in 10-400 reloads, depending on what was running): a
    SIGSEGV on the main thread in EarlyUpdate.UpdatePreloading (looptrace), inside UnityPlayer while it walks a
    std::map whose node was freed (UnityPlayer.dylib +0x4ef724, `ldr x8, [x22, #8]`), followed by mono's handler
    dying in exit. This runs the same transition with the unloads finished before the load starts; everything else
    (cloud fade, events, timing) is the game's.
*/
namespace PalaceMod
{
    [ManagerOrder(-90)]
    public class SceneLoadOrderManager : BaseManager
    {
        private static readonly AccessTools.FieldRef<SceneTransitionManager, bool> Running =
            AccessTools.FieldRefAccess<SceneTransitionManager, bool>("sceneTransitionIsRunning");
        private static readonly AccessTools.FieldRef<SceneTransitionManager, Color> CloudsIn =
            AccessTools.FieldRefAccess<SceneTransitionManager, Color>("cloudsIn");
        private static readonly AccessTools.FieldRef<SceneTransitionManager, Color> CloudsOut =
            AccessTools.FieldRefAccess<SceneTransitionManager, Color>("cloudsOut");

        /// <summary>Off: the game's own order (dev `sceneorder vanilla`, to compare).</summary>
        private static bool sequential = true;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            if (System.Environment.GetEnvironmentVariable("PALACEMOD_DEV") != "1") return;
            DevCommands.Register("sceneorder", "sceneorder [sequential|vanilla]   scene changes unload before loading (default) or all at once like the game", args =>
            {
                if (args.Length > 0) sequential = args[0] != "vanilla";
                return sequential ? "sequential" : "vanilla";
            });
        }

        [HarmonyPatch(typeof(SceneTransitionManager), "SceneTransitionAnimation")]
        private static class SceneTransitionAnimation_Patch
        {
            private static bool Prefix(SceneTransitionManager __instance, string _scenename, ref IEnumerator __result)
            {
                if (!sequential) return true;
                __result = Transition(__instance, _scenename);
                return false;
            }
        }

        private static float FadeFormula(float f) => Mathf.Pow(f, 4f);

        // SceneTransitionManager.SceneTransitionAnimation, with the load started only once the unloads are done.
        private static IEnumerator Transition(SceneTransitionManager t, string scene)
        {
            Running(t) = true;
            int sceneCount = SceneManager.sceneCount;
            t.placeholderCloudsTransition.gameObject.SetActive(true);
            float clock = 0f;
            while (clock <= t.cloudFadeInTime)
            {
                clock += Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
                t.placeholderCloudsTransition.color = Color.Lerp(CloudsOut(t), CloudsIn(t), 1f - FadeFormula(1f - clock / t.cloudFadeInTime));
                t.loadingText.alpha = 1f - FadeFormula(1f - clock / t.cloudFadeInTime);
                yield return null;
            }
            t.placeholderCloudsTransition.color = CloudsIn(t);
            t.loadingText.alpha = 1f;

            var unloads = new List<AsyncOperation>();
            for (int i = sceneCount - 1; i >= 0; i--)
            {
                var open = SceneManager.GetSceneAt(i);
                if (open.name == t.uiScene) continue;
                var op = SceneManager.UnloadSceneAsync(open);
                if (op != null) unloads.Add(op);
            }
            LoopTraceManager.Note("scene unload start");
            while (unloads.Exists(op => !op.isDone)) yield return null;
            LoopTraceManager.Note("scene load start");
            var load = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);
            while (load != null && !load.isDone) yield return null;
            LoopTraceManager.Note("scene load done");

            if (scene == "_LevelSelect") Resources.UnloadUnusedAssets();
            t.onSceneChangeMid.Invoke();
            SceneManager.SetActiveScene(SceneManager.GetSceneByName(scene));
            UIFrameManager.instance.CloseAllFrames();
            if (scene == t.mainMenuScene) UIFrameManager.instance.SwitchToTitleFrame();
            clock = 0f;
            while (clock <= t.cloudFadeOutTime)
            {
                clock += Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
                t.placeholderCloudsTransition.color = Color.Lerp(CloudsIn(t), CloudsOut(t), FadeFormula(clock / t.cloudFadeOutTime));
                t.loadingText.alpha = 1f - FadeFormula(1f - clock / t.cloudFadeInTime);
                yield return null;
            }
            t.placeholderCloudsTransition.color = CloudsOut(t);
            t.loadingText.alpha = 0f;
            t.placeholderCloudsTransition.gameObject.SetActive(false);
            Running(t) = false;
        }
    }
}
