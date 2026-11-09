/*
    Light for expressions (docs/builder-engine.md, "Light"): the `daylight` global, `lightAt(p)`, `.light`, `.lit` and
    the `lit` filter term read it. The Unity side (Logic/Bodies/World/LightVerbs.cs) installs the service; without it
    (tests, before a level) everything reads as daylight 1 and fully lit.
*/
namespace PalaceMod.Builder.Logic
{
    public interface ILighting
    {
        /// <summary>The level's own light, 0..1: 1 by day, about 0.34 at night, lower in an eclipse (`lighting`).</summary>
        double Daylight { get; }

        /// <summary>How lit a point is, 0..1: daylight plus every light that reaches it (entity lights and the game's own,
        /// such as the castle's night fire), capped at 1.</summary>
        double LightAt(Vec p);
    }

    public static class LogicLighting
    {
        public static ILighting Service;

        /// <summary>A light level reads as "lit" from here up.</summary>
        public const double LitThreshold = 0.5;

        public static double Daylight => Service?.Daylight ?? 1;
        public static double LightAt(Vec p) => Service?.LightAt(p) ?? 1;
        public static bool Lit(Vec p) => LightAt(p) >= LitThreshold;
    }
}
