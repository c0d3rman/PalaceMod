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
            while (queue.Count > 0)
            {
                var token = queue.Dequeue();
                var all = token is JContainer container ? container.DescendantsAndSelf() : [token];
                foreach (var value in all.OfType<JValue>())
                {
                    if (value.Type != JTokenType.String) continue;
                    var r = ContentRef.Parse((string)value);
                    if (r.Kind != RefKind.LocalUnit || copy.FindUnit(r.Id) is not { } unit || !keep.Add(r.Id)) continue;
                    queue.Enqueue(JToken.FromObject(unit));
                }
            }
            copy.Units = copy.Units.Where(u => u?.Id != null && keep.Contains(u.Id)).ToList();
            return copy;
        }
    }
}
