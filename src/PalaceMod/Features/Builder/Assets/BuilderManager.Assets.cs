using HarmonyLib;
using PalaceMod.Builder;
using PalaceMod.Builder.Assets;
using UnityEngine;

// WP5 patches (asset import). See Assets/AssetVisuals.cs.
namespace PalaceMod
{
    public partial class BuilderManager
    {
        /// <summary>Custom sounds: after UnitAudio picks its clips from the game's AudioSet, swap in the pack's.
        /// Only units spawned from templates carrying a BuilderUnitSounds marker are touched.</summary>
        [HarmonyPatch(typeof(UnitAudio), "Start")]
        private static class UnitAudioStartPatch
        {
            private static readonly AccessTools.FieldRef<UnitAudio, AudioSet.ClipArray> OnAttack = AccessTools.FieldRefAccess<UnitAudio, AudioSet.ClipArray>("soundOnAttack");
            private static readonly AccessTools.FieldRef<UnitAudio, AudioSet.ClipArray> OnDmg = AccessTools.FieldRefAccess<UnitAudio, AudioSet.ClipArray>("soundOnDmg");
            private static readonly AccessTools.FieldRef<UnitAudio, AudioSet.ClipArray> OnDeath = AccessTools.FieldRefAccess<UnitAudio, AudioSet.ClipArray>("soundOnDeath");

            private static void Postfix(UnitAudio __instance)
            {
                var marker = __instance.GetComponentInParent<BuilderUnitSounds>();
                if (marker == null) return;
                var entry = BuilderServices.Packs.Find(marker.pack);
                if (entry == null) return;
                Swap(__instance, OnAttack, entry, marker.attack);
                Swap(__instance, OnDmg, entry, marker.hurt);
                Swap(__instance, OnDeath, entry, marker.death);
            }

            private static void Swap(UnitAudio audio, AccessTools.FieldRef<UnitAudio, AudioSet.ClipArray> field, PackEntry entry, string assetRef)
            {
                if (assetRef == null) return;
                var clip = BuilderServices.Assets.GetAudio(entry, assetRef);
                if (clip != null) field(audio) = new AudioSet.ClipArray { clips = [clip] };
            }
        }
    }
}
