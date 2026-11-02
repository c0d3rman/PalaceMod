using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine.LowLevel;

/*
    Times every engine subsystem in Unity's player loop (scripts, physics, rendering, present, ...) by inserting a
    timestamp marker before and after each one. Used to say where a slow frame spent its time.
*/
namespace PalaceMod
{
    internal class PlayerLoopTimer
    {
        private readonly List<string> names = []; // names[i] = what ran between marker i and marker i + 1
        private long[] stamps;
        private long[] lastFrame;
        private long lastFrameEnd;

        public bool Installed { get; private set; }

        public void Install()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var phases = root.subSystemList;
            int marker = 0;
            for (int p = 0; p < phases.Length; p++)
            {
                var subsystems = phases[p].subSystemList ?? [];
                var instrumented = new List<PlayerLoopSystem>(subsystems.Length * 2 + 1);
                string phase = phases[p].type?.Name ?? $"Phase{p}";
                foreach (var subsystem in subsystems)
                {
                    instrumented.Add(Marker(marker++));
                    names.Add($"{phase}.{subsystem.type?.Name ?? "?"}");
                    instrumented.Add(subsystem);
                }
                instrumented.Add(Marker(marker++));
                // Gap until the next phase's first marker (or, for the last phase, until the next frame starts).
                names.Add(p + 1 < phases.Length ? $"between {phase} and {phases[p + 1].type?.Name}" : "between frames");
                phases[p].subSystemList = instrumented.ToArray();
            }
            stamps = new long[marker];
            lastFrame = new long[marker];
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            Installed = true;
        }

        private PlayerLoopSystem Marker(int index) => new()
        {
            type = typeof(PlayerLoopTimer),
            updateDelegate = index == 0 ? StartFrame : () => stamps[index] = Stopwatch.GetTimestamp(),
        };

        private void StartFrame()
        {
            long now = Stopwatch.GetTimestamp();
            // The previous frame is complete: keep its timestamps for Describe().
            (lastFrame, stamps) = (stamps, lastFrame);
            lastFrameEnd = now;
            stamps[0] = now;
        }

        /// <summary>The slowest subsystems of the last completed frame, e.g. "PostLateUpdate.PresentAfterDraw 48.1".</summary>
        public string DescribeLastFrame(int top = 4)
        {
            if (!Installed || lastFrame[0] == 0) return "";
            double toMs = 1000.0 / Stopwatch.Frequency;
            var spans = new List<(string name, double ms)>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                long end = i + 1 < lastFrame.Length ? lastFrame[i + 1] : lastFrameEnd;
                if (end == 0 || lastFrame[i] == 0) continue;
                spans.Add((names[i], (end - lastFrame[i]) * toMs));
            }
            var text = new StringBuilder();
            foreach (var (name, ms) in spans.OrderByDescending(s => s.ms).Take(top))
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(name).Append(' ').Append(ms.ToString("0.0"));
            }
            return text.ToString();
        }
    }
}
