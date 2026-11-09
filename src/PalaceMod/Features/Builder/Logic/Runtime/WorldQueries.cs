namespace PalaceMod.Builder.Logic
{
    /// <summary>
    /// Reads of the world under a point (docs/builder-engine.md, "World queries"): the ground's height, material and
    /// water, whether a kind of walker can stand there, and line of sight. Installed by the Bodies (Logic/Bodies/World/
    /// WorldQueryService.cs); tests fake it. Null = not available (the expression functions then answer as on flat,
    /// dry, open grass at y 0).
    /// </summary>
    public interface IWorldQueries
    {
        /// <summary>Height of the walkable ground (terrain, the map's ground, or a walkable deck) under p.</summary>
        double GroundY(Vec p);
        /// <summary>
        /// Whether something can stand at p: <paramref name="who"/> is "small" (ground enemies, the default), "large",
        /// "air", "player" (player troops), "king", or null with <paramref name="unit"/> given (that unit's graph and the
        /// surfaces it may walk).
        /// </summary>
        bool Walkable(Vec p, string who, LogicEntity unit);
        /// <summary>The ground under p: a paint ("grass", "highGrass", "lowGrass", "earth", "sand", "road", "rock"), "water",
        /// "ice", "blocked", "deck" (a walkable body) or "none" off the ground.</summary>
        string Ground(Vec p);
        /// <summary>Metres of water over the ground at p (0 when dry or frozen).</summary>
        double WaterDepth(Vec p);
        /// <summary>The terrain's water level (world y), or NaN if the level has no water.</summary>
        double WaterLevel { get; }
        /// <summary>
        /// The first thing a straight line from a to b hits (ground, cliffs, walls, buildings, solid props), or null if it
        /// gets through. Positions on the ground are lifted 1.5 m (eye height).
        /// </summary>
        Vec? Raycast(Vec a, Vec b);
    }

    public static class WorldQueries
    {
        public static IWorldQueries Service;
    }
}
