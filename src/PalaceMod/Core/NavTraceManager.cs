using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

/*
    Dev-only record of menu navigation: every UIFrameManager frame change and every SceneTransitionManager scene
    transition (the game's cloud fade), with the frame number, so a check can prove a menu path switches frames
    instantly and starts at most the transitions it needs. `dev.py cmd "navtrace"` prints the events since the last
    `navtrace clear`. Nothing is recorded outside dev launches.
*/
namespace PalaceMod
{
    public class NavTraceManager : BaseManager
    {
        private static bool tracing;
        private static readonly List<string> Events = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            tracing = Environment.GetEnvironmentVariable("PALACEMOD_DEV") == "1";
            if (!tracing) return;
            DevCommands.Register("navtrace", "navtrace [clear]       frame changes and scene transitions since the last clear", args =>
            {
                if (args.Length > 0 && args[0] == "clear") { Events.Clear(); return "cleared"; }
                int transitions = Events.Count(e => e.Contains("TRANSITION"));
                return $"{Events.Count} events, {transitions} scene transition(s)\n" + string.Join("\n", Events);
            });
        }

        private static void Add(string text)
        {
            if (!tracing) return;
            if (Events.Count > 400) Events.RemoveAt(0);
            Events.Add($"f{Time.frameCount} t{Time.unscaledTime:0.00}: {text}");
        }

        private static string Name(UIFrame f) => f == null ? "none" : f.name;

        [HarmonyPatch(typeof(UIFrameManager), "ProcessFrameChange")]
        private static class FrameChange_Patch
        {
            private static void Prefix(UIFrameManager __instance, UIFrame nextFrame, bool keepOldFrameGameObjectActive)
            {
                if (tracing) Add($"frame {Name(__instance.ActiveFrame)} -> {Name(nextFrame)}{(keepOldFrameGameObjectActive ? " (old kept visible)" : "")}");
            }
        }

        [HarmonyPatch(typeof(SceneTransitionManager), "TransitionToScene")]
        private static class Transition_Patch
        {
            private static void Prefix(string _scenename)
            {
                if (tracing) Add($"TRANSITION to {_scenename}");
            }
        }
    }
}
