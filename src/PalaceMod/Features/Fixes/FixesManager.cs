using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

/*
    Small fixes for vanilla bugs, each behind its own toggle in the "Fixes" section.

    - Destroyed buildings replay destruction: a save (or "retry day") that has buildings at 0 HP (only possible with the
      God of Destruction mutator) re-activates their knocked-out visuals in Hp.Start, whose BuildingDestructionHandler
      OnEnable shakes the camera and plays the debris particles and crash sound again. We skip that OnEnable while
      LocalMatchSaveLoad is loading the match.
    - Shrine particles on load: Shrine.ActivateShrine(_calledFromSaveLoad: true) re-plays the one-off activation burst
      for every already-activated shrine. We stop and clear it right after.
    - Loaded unit health: Hp.OnSave stores Mathf.RoundToInt(hp), so a unit with fractional max HP (e.g. 37.5) loads at
      37 or 38 instead of full. When the loaded value is within rounding distance of max HP, we restore it to max.
*/
namespace PalaceMod
{
    public class FixesManager : BaseManager
    {
        public override string FeatureSection => "Fixes";
        public override string FeatureDescription => "Fixes for small bugs in the base game. Turn off to restore vanilla behavior.";

        private static ConfigEntry<bool> destructionReplayFix;
        private static ConfigEntry<bool> shrineParticlesFix;
        private static ConfigEntry<bool> loadedHealthFix;

        private static readonly AccessTools.FieldRef<Shrine, ParticleSystem> UpgradeParticles =
            AccessTools.FieldRefAccess<Shrine, ParticleSystem>("upgradeParticles");
        private static readonly AccessTools.FieldRef<Hp, float> HpValue = AccessTools.FieldRefAccess<Hp, float>("hp");
        private static readonly AccessTools.FieldRef<Hp, bool> GotLoadedHpValue = AccessTools.FieldRefAccess<Hp, bool>("gotLoadedHpValue");

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            destructionReplayFix = plugin.Config.Bind(FeatureSection, "Destroyed buildings on load", true,
                "Destroyed buildings stay quiet when a save loads or a day is retried, instead of collapsing again with debris, sound and screen shake (God of Destruction).");
            shrineParticlesFix = plugin.Config.Bind(FeatureSection, "Shrine burst on load", true,
                "Already-activated shrines don't replay their activation burst when a save loads or a day is retried.");
            loadedHealthFix = plugin.Config.Bind(FeatureSection, "Full health on load", true,
                "Units with fractional max HP load at full health instead of slightly below or above it (saves round HP to whole numbers).");
            RegisterDevCommands();
        }

        [HarmonyPatch(typeof(BuildingDestructionHandler), "OnEnable")]
        private static class BuildingDestructionHandler_OnEnable
        {
            private static bool Prefix(BuildingDestructionHandler __instance)
            {
                if (!destructionReplayFix.Value || !LocalMatchSaveLoad.CurrentlyLoadingMatch) return true;
                Plugin.Logger.LogInfo($"Fixes: skipped destruction effects of {__instance.transform.root.name} while loading the match");
                return false;
            }
        }

        [HarmonyPatch(typeof(Shrine), "ActivateShrine")]
        private static class Shrine_ActivateShrine
        {
            private static void Postfix(Shrine __instance, bool _calledFromSaveLoad)
            {
                if (!_calledFromSaveLoad || !shrineParticlesFix.Value) return;
                var particles = UpgradeParticles(__instance);
                if (particles == null) return;
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Plugin.Logger.LogInfo($"Fixes: cleared activation burst of loaded shrine {__instance.name}");
            }
        }

        [HarmonyPatch(typeof(Hp), "Start")]
        private static class Hp_Start
        {
            private static void Postfix(Hp __instance)
            {
                if (!loadedHealthFix.Value || !GotLoadedHpValue(__instance)) return;
                ref float hp = ref HpValue(__instance);
                // The save stored Mathf.RoundToInt(hp), so anything within 0.5 of max HP was saved from full health.
                if (hp <= 0f || hp == __instance.maxHp || Mathf.Abs(hp - __instance.maxHp) > 0.5f) return;
                Plugin.Logger.LogInfo($"Fixes: restored loaded HP of {__instance.name} from {hp} to {__instance.maxHp}");
                hp = __instance.maxHp;
                __instance.OnHpChange.Invoke();
            }
        }

        // Test hooks for reproducing the bugs: `dev.py cmd "fixes <sub>"`.
        private static void RegisterDevCommands()
        {
            DevCommands.Register("fixes", "fixes god [on|off] | build | kill <n> | shrines | save | retry | hp | units   drive the Fixes repros", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: fixes <god|build|kill|shrines|save|retry|hp|units>");
                var tags = TagManager.instance ?? throw new InvalidOperationException("not in a level");
                switch (args[0].ToLowerInvariant())
                {
                    case "god":
                    {
                        var perks = PerkManager.instance;
                        if (args.Length > 1) PerkManager.SetEquipped(perks.destructionGod, args[1] != "off");
                        return $"God of Destruction {(perks.DestructionGodActive ? "on" : "off")}";
                    }
                    case "build":
                    {
                        int built = 0;
                        for (int pass = 0; pass < 4; pass++)
                            foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
                                if (slot.CanBeUpgraded) { slot.DEBUGUpgradeToMax(); built++; }
                        return $"upgraded {built} build slots";
                    }
                    case "kill":
                    {
                        int n = args.Length > 1 ? int.Parse(args[1]) : 3;
                        var killed = PlayerObjects(tags, TagManager.ETag.Building)
                            .Where(t => !t.Tags.Contains(TagManager.ETag.CastleCenter) && t.Hp != null && !t.Hp.KnockedOut)
                            .Take(n).ToList();
                        foreach (var t in killed) t.Hp.TakeDamage(float.MaxValue);
                        return "destroyed " + string.Join(", ", killed.Select(t => t.name));
                    }
                    case "shrines":
                    {
                        var activate = AccessTools.Method(typeof(Shrine), "ActivateShrine");
                        var shrines = UnityEngine.Object.FindObjectsOfType<Shrine>();
                        foreach (var s in shrines) activate.Invoke(s, [false]);
                        return $"activated {shrines.Length} shrines";
                    }
                    case "save":
                    {
                        var saver = UnityEngine.Object.FindObjectOfType<LocalMatchSaveLoad>() ?? throw new InvalidOperationException("no LocalMatchSaveLoad");
                        AccessTools.Method(typeof(LocalMatchSaveLoad), "Save").Invoke(saver, null);
                        return "saved";
                    }
                    case "retry":
                    {
                        // Same as the defeat screen's "retry day" (EndscreenRetryHelper.RestartLastDay).
                        if (EnemySpawner.instance.Wavenumber > 0)
                        {
                            MatchSaveLoadHandler.OverwriteCurrentSave = false;
                            MatchSaveLoadHandler.CurrentSave.ApplyLoadout();
                        }
                        SceneTransitionManager.instance.RestartCurrentLevel();
                        return "retrying day";
                    }
                    case "hp":
                        return string.Join("\n", PlayerObjects(tags, TagManager.ETag.PlayerOwned)
                            .Where(t => t.Hp != null && t.Hp.HpValue != t.Hp.maxHp)
                            .Select(t => $"{t.name}: {t.Hp.HpValue}/{t.Hp.maxHp}"));
                    case "units":
                        return string.Join("\n", PlayerObjects(tags, TagManager.ETag.PlayerUnit)
                            .Where(t => t.Hp != null)
                            .Select(t => $"{t.name}: {t.Hp.HpValue}/{t.Hp.maxHp}"));
                    default:
                        throw new ArgumentException("unknown subcommand");
                }
            });
        }

        private static List<TaggedObject> PlayerObjects(TagManager tags, TagManager.ETag tag)
        {
            var found = new List<TaggedObject>();
            tags.FindAllTaggedObjectsWithTags(found, [TagManager.ETag.PlayerOwned, tag], []);
            return found;
        }
    }
}
