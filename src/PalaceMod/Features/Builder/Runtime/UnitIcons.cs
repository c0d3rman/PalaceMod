using UnityEngine;

/*
    A player unit shows its icon in two places: ScreenMarkerIcon.sprite (the pause menu's unit list, Builder markers
    and HUD lines) and UnitTypeDisplay.icon (the type badge over its head while the command key is held). The game
    serializes both per prefab, and they don't always agree: P Melee, a troop no vanilla building trains, has no
    marker sprite and an archer badge copied from P Archer. A unit the Builder makes (logic spawn, team convert, custom
    unit template) goes through Apply/Repair so both show the unit's own icon.
*/
namespace PalaceMod.Builder
{
    internal static class UnitIcons
    {
        /// <summary>Sets the unit's marker icon and its type badge to <paramref name="sprite"/>.</summary>
        public static void Apply(GameObject go, Sprite sprite)
        {
            if (go == null || sprite == null) return;
            var marker = go.GetComponent<ScreenMarkerIcon>();
            if (marker != null) marker.sprite = sprite;
            if (marker is ScreenMarkerIconArachnophobia spider) spider.defaultSprite = spider.arachnophobiaModeSprite = sprite;
            var display = go.GetComponent<UnitTypeDisplay>();
            if (display != null && display.icon != null) display.icon.sprite = sprite;
        }

        /// <summary>A player unit whose marker icon is missing gets the catalog's icon for <paramref name="unitId"/>
        /// (its own, or for P Melee its enemy twin's), on the marker and the type badge.</summary>
        public static void Repair(GameObject go, string unitId)
        {
            if (go == null) return;
            var marker = go.GetComponent<ScreenMarkerIcon>();
            if (marker == null || marker.sprite != null) return;
            if (go.GetComponent<TaggedObject>()?.Tags.Contains(TagManager.ETag.PlayerOwned) != true) return;
            Apply(go, BuilderServices.Catalog?.FindUnit(unitId)?.Icon);
        }
    }
}
