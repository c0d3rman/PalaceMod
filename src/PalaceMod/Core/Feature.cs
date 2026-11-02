namespace PalaceMod
{
    /// <summary>
    /// Allocation-free access to a registered manager from Harmony patches and other static code:
    /// <c>if (!Feature&lt;FormationManager&gt;.On) return;</c> or <c>Feature&lt;FormationManager&gt;.Instance</c>.
    /// </summary>
    public static class Feature<T> where T : BaseManager
    {
        /// <summary>The registered manager, or null if it isn't registered (e.g. it failed to start).</summary>
        public static T Instance { get; internal set; }

        /// <summary>True when the manager is registered and its Enabled setting is on.</summary>
        public static bool On => Instance != null && Instance.IsEnabled;
    }
}
