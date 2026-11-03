using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

/*
    The game shows every build slot's upgrade preview only while "Preview Build Options" is held: PlayerInteraction.
    RunInteraction writes BuildingInteractor.displayAllBuildPreviews from the button every frame. This feature adds a
    latch on top: a quick tap of the same button keeps previews on until the next tap, and optionally they switch on
    by themselves each dawn. Holding the button still peeks as in vanilla.

    Because the game overwrites the flag every frame, the latch has to be OR'd in right after RunInteraction; that
    postfix is a single bool write. Dawn is picked up through the game's own IDaytimeSensitive callbacks.
*/
namespace PalaceMod
{
    public class BuildPreviewManager : BaseManager, DayNightCycle.IDaytimeSensitive
    {
        public override string FeatureSection => "Build previews";
        public override string FeatureDescription => "Tap the game's Preview Build Options button to keep build previews on; hold it to peek as usual.";

        private const string PreviewAction = "Preview Build Options";
        // Presses shorter than this toggle the latch; longer ones are a vanilla hold-to-peek.
        private const float TapSeconds = 0.3f;

        private ConfigEntry<bool> showAtDawn;

        private bool latched;
        private float pressedAt = -1f;
        private DayNightCycle registeredWith;

        /// <summary>Whether build previews are currently being drawn (latched or held).</summary>
        public static bool PreviewsShown => BuildingInteractor.displayAllBuildPreviews;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            showAtDawn = plugin.Config.Bind(FeatureSection, "Show at dawn", true,
                "Turns build previews on every morning. Tap the preview button to hide them again.");
            DevCommands.Register("previews", "previews [on|off]   show or set the build preview latch", args =>
            {
                if (args.Length > 0) SetLatched(args[0] == "on");
                return $"latched={latched} shown={BuildingInteractor.displayAllBuildPreviews}";
            });
            // The latch resets with each level, so a savestate load would drop it; keep it in the state.
            SaveStates.RegisterState("build-preview-latch",
                () => IsEnabled ? new LatchState { latched = latched } : null,
                saved => { if (IsEnabled) SetLatched(saved.latched); });
        }

        internal sealed class LatchState { public bool latched; }

        public override void Update()
        {
            // Follow the level's DayNightCycle; it's a new instance every level and absent on the menu.
            var cycle = DayNightCycle.Instance;
            if (cycle != registeredWith)
            {
                if (registeredWith != null) registeredWith.UnregisterDaytimeSensitiveObject(this);
                if (cycle != null) cycle.RegisterDaytimeSensitiveObject(this);
                registeredWith = cycle;
                latched = false;
                pressedAt = -1f;
            }
            if (cycle == null) return;

            var input = Plugin.Input;
            if (input == null) return;
            if (input.GetButtonDown(PreviewAction)) pressedAt = Time.unscaledTime;
            if (input.GetButtonUp(PreviewAction) && pressedAt >= 0f)
            {
                if (Time.unscaledTime - pressedAt < TapSeconds) SetLatched(!latched);
                pressedAt = -1f;
            }
        }

        private void SetLatched(bool on)
        {
            latched = on;
            if (on) BuildingInteractor.displayAllBuildPreviews = true;
        }

        protected override void OnFeatureDisabled()
        {
            if (registeredWith != null) registeredWith.UnregisterDaytimeSensitiveObject(this);
            registeredWith = null;
            latched = false;
            pressedAt = -1f;
            BuildingInteractor.displayAllBuildPreviews = false;
        }

        // IDaytimeSensitive. The cycle calls these on every registered object; only dawn matters here.
        public void OnDawn_AfterSunrise()
        {
            if (IsEnabled && showAtDawn.Value) SetLatched(true);
        }
        public void OnDawn_BeforeSunrise() { }
        public void OnDuskEarly() { }
        public void OnDusk() { }

        [HarmonyPatch(typeof(PlayerInteraction), "RunInteraction")]
        private static class PlayerInteraction_RunInteraction_Patch
        {
            private static void Postfix()
            {
                if (Feature<BuildPreviewManager>.Instance.latched) BuildingInteractor.displayAllBuildPreviews = true;
            }
        }
    }
}
