/*
    Optional presentation hints for a ConfigEntry, passed as a tag in its ConfigDescription and read by the in-game
    settings pane (SettingsPaneManager). BepInEx's ConfigurationManager ignores tags it doesn't recognise.
*/
namespace PalaceMod
{
    public class SettingTags
    {
        /// <summary>What to show instead of 0, e.g. "Off" or "Display".</summary>
        public string ZeroLabel;

        /// <summary>Slider step for numeric ranges; derived from the range when 0.</summary>
        public float Step;

        /// <summary>Unit shown after numeric values, e.g. " fps".</summary>
        public string Unit = "";

        /// <summary>Leave the setting out of the in-game pane.</summary>
        public bool Hidden;
    }
}
