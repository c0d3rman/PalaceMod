using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

/*
    Which custom level a savestate belongs to, and which version of it (pure C#: unit-tested in
    tests/PalaceMod.Tests/LevelPinTests.cs). Builder states are filed by Key, not by the host scene, so states of a
    level hosted on Totend never mix with vanilla Totend's or another level's on the same host.

    A restore reloads the host and the session re-applies the level it is running, so within one session the version
    is pinned by the session itself. Across sessions (the level file was edited since), Incompatible says when the
    saved moment can't be put back: another host, another number of nights, or a different set of build slots (the
    game's save finds buildings by their slots). Other edits (props, terrain, moved slots, later nights' enemies) load
    with Edited set, so the caller can say so.
*/
namespace PalaceMod.Builder.Model
{
    public sealed class LevelPin
    {
        public string Pack;
        /// <summary>The level's id, or null for a wave set played on its host unchanged.</summary>
        public string Level;
        public string WaveSet;
        public string Host;
        public int Nights;
        /// <summary>Hash of the build slots (added ids, removed host slots, the base the level starts from).</summary>
        public string Slots;
        /// <summary>Hash of the whole level and its wave set.</summary>
        public string Content;

        /// <summary>Where the session's states are filed: "Builder/&lt;pack&gt;/&lt;level&gt;" (or "waves-&lt;id&gt;").</summary>
        [JsonIgnore] public string Key => KeyOf(Pack, Level, WaveSet);

        public static string KeyOf(string pack, string level, string waveSet) =>
            $"{KeyPrefix}{pack ?? "?"}/{(level != null ? level : "waves-" + (waveSet ?? "?"))}";

        public const string KeyPrefix = "Builder/";

        public static bool IsKey(string level) => level != null && level.StartsWith(KeyPrefix, StringComparison.Ordinal);

        public static LevelPin Of(DesignPack pack, LevelDef level, WaveSetDef waveSet) => new()
        {
            Pack = pack?.Id,
            Level = level?.Id,
            WaveSet = waveSet?.Id,
            Host = level?.Host ?? waveSet?.Host,
            Nights = waveSet?.Nights?.Count ?? 0,
            Slots = Hash(SlotSet(level)),
            Content = Hash(JsonConvert.SerializeObject(level) + "\n" + JsonConvert.SerializeObject(waveSet)),
        };

        private static string SlotSet(LevelDef level)
        {
            if (level == null) return "";
            var added = (level.BuildSlots?.Add ?? []).Where(a => a?.Id != null).Select(a => "+" + a.Id + ":" + a.Base);
            var removed = (level.BuildSlots?.Remove ?? []).Where(r => r != null).Select(r => "-" + r);
            return "base=" + (level.Base ?? "") + ";" + string.Join(";", added.Concat(removed).OrderBy(s => s, StringComparer.Ordinal));
        }

        private static string Hash(string text)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
            return string.Concat(bytes.Take(8).Select(b => b.ToString("x2")));
        }

        /// <summary>Why a state saved at <paramref name="saved"/> can't be loaded into <paramref name="now"/>, or null.</summary>
        public static string Incompatible(LevelPin saved, LevelPin now)
        {
            if (saved == null) return now == null ? null : "That state is from the host map without the custom level";
            if (now == null) return "That state is from a custom level; play it to load the state";
            if (saved.Key != now.Key) return "That state is from another custom level";
            if (!string.Equals(saved.Host, now.Host, StringComparison.OrdinalIgnoreCase)) return $"The level is on {now.Host} now; that state is from {saved.Host}";
            if (saved.Nights != now.Nights) return $"The level has {now.Nights} nights now; that state is from a version with {saved.Nights}";
            if (saved.Slots != null && saved.Slots != now.Slots) return "The level's build slots changed since that state was saved";
            return null;
        }

        /// <summary>The level or its nights were edited since the state was saved (it still loads).</summary>
        public static bool Edited(LevelPin saved, LevelPin now) => saved != null && now != null && saved.Content != null && saved.Content != now.Content;
    }
}
