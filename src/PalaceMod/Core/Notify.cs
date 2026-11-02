namespace PalaceMod
{
    /// <summary>What a notification is about; the presentation (colour, icon, sound) is up to the UI.</summary>
    public enum NotifyKind { Info, Success, Warning, Error }

    /// <summary>
    /// Short on-screen messages for the player. Every message is also written to the BepInEx log.
    /// Presentation lives in NotificationManager (Core/UI): a small panel in the game's style, marked as PalaceMod's,
    /// at the top centre of the screen. Callers only say what to show.
    /// </summary>
    public static class Notify
    {
        public const float DefaultSeconds = 3f;
        private static readonly Log log = Log.For("Notify");

        /// <param name="text">The message.</param>
        /// <param name="kind">What the message is about.</param>
        /// <param name="seconds">How long it stays before fading; 0 or less uses the default.</param>
        /// <param name="key">Messages with the same non-null key replace each other instead of stacking, e.g. a
        /// "Formation: Line" message replaced by "Formation: Circle".</param>
        public static void Show(string text, NotifyKind kind = NotifyKind.Info, float seconds = DefaultSeconds, string key = null)
        {
            // Logged as info whatever the kind: a message for the player isn't a mod error, so it shouldn't show up in
            // `dev.py logs --errors`. Log real errors yourself.
            log.Info($"({kind}) {text}");
            Feature<NotificationManager>.Instance?.ShowNotification(text, kind, seconds > 0 ? seconds : DefaultSeconds, key);
        }
    }
}
