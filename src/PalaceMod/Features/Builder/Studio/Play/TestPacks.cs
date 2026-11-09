using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

// The pack a Studio test play loads its host with (the edit load uses Studio.EditPack) (pure C#: unit-tested in tests/PalaceMod.Tests/TestPackTests.cs).
namespace PalaceMod.Builder.Studio.Play
{
    public static class TestPacks
    {
        /// <summary>The pack a test play runs: the level, its wave set, the units that set uses (and the units those
        /// refer to), the pack's assets and requirements. Problems elsewhere in the pack don't block it.</summary>
        private static readonly System.Text.RegularExpressions.Regex TemplateInText = new(@"(?:\bis:|@@)([a-z0-9._-]+)");

        public static DesignPack Play(DesignPack source, string levelId, out string error)
        {
            error = null;
            var copy = PackSerializer.Clone(source);
            var level = copy.FindLevel(levelId);
            if (level == null) { error = "the level is gone"; return null; }
            copy.Levels = [level];
            var waveSet = level.WaveSet != null ? copy.FindWaveSet(level.WaveSet) : null;
            if (level.WaveSet != null && waveSet == null) { error = $"its nights \"{level.WaveSet}\" aren't in the pack"; return null; }
            copy.WaveSets = waveSet == null ? [] : [waveSet];
            // Every string in the wave set (spawns, troops) that names a unit of the pack, and so on through those units.
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<JToken>();
            if (waveSet != null) queue.Enqueue(JToken.FromObject(waveSet));
            // The behaviour engine's references too: the level's logic and every entity, status and preset (kept whole)
            // can spawn or name units ("spawn", "is:goblin", "@@goblin").
            if (level.Logic != null) queue.Enqueue(JToken.FromObject(level.Logic));
            foreach (var x in (copy.Entities ?? []).Cast<object>().Concat(copy.Statuses ?? []).Concat(copy.Presets ?? []))
                queue.Enqueue(JToken.FromObject(x));
            while (queue.Count > 0)
            {
                var token = queue.Dequeue();
                var all = token is JContainer container ? container.DescendantsAndSelf() : [token];
                foreach (var value in all.OfType<JValue>())
                {
                    if (value.Type != JTokenType.String) continue;
                    string s = (string)value;
                    var names = new List<string> { ContentRef.Parse(s) is { Kind: RefKind.LocalUnit } r ? r.Id : null };
                    foreach (System.Text.RegularExpressions.Match m in TemplateInText.Matches(s)) names.Add(m.Groups[1].Value);
                    foreach (var id in names)
                    {
                        if (id == null || copy.FindUnit(id) is not { } unit || !keep.Add(id)) continue;
                        queue.Enqueue(JToken.FromObject(unit));
                    }
                }
            }
            copy.Units = copy.Units.Where(u => u?.Id != null && keep.Contains(u.Id)).ToList();
            return copy;
        }

        /// <summary>The pack a Studio edit load runs with (Studio.EditPack): a copy holding only the level (no wave set,
        /// assets or requirements), so problems elsewhere in the pack never stop the layout from being edited. In the
        /// library it keeps the units the level's own logic names (its spawns need their templates: without them every
        /// entity that spawns a unit failed with "no template"); elsewhere no units.</summary>
        public static DesignPack Edit(DesignPack pack, string levelId)
        {
            var copy = PackSerializer.Clone(pack);
            var only = copy.FindLevel(levelId) ?? throw new ArgumentException("no such level", nameof(levelId));
            only.WaveSet = null;
            // Without its wave set the level would play the map's own nights, which may spawn from a line the level
            // removes (a validation error). An edit load never applies the overlay, so the copy can keep them all.
            if (only.SpawnLines != null) only.SpawnLines.Remove = null;
            var used = copy.Id == LibraryStore.PackId ? Model.Logic.LevelScope.Uses(copy, only)["units"] : new HashSet<string>();
            copy.Units = copy.Units.Where(u => u?.Id != null && used.Contains(u.Id)).ToList();
            copy.WaveSets.Clear();
            copy.Levels = [only];
            copy.Requires = null;
            copy.Assets?.Clear();
            return copy;
        }
    }
}
