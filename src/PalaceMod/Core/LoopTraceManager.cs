using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.LowLevel;

/*
    Dev-only black box for native crashes: a native crash (SIGSEGV in UnityPlayer) leaves a stripped stack and no
    managed frames, so this says which engine subsystem was running. `looptrace on [file]` puts a marker before every
    subsystem of Unity's player loop; each marker writes (frame, marker) straight to a ring file (no buffering, so the
    last records survive the process dying). `looptrace read [file]` prints the last records with the subsystem names;
    a crash's last record names the subsystem that was running. Named events (`LoopTrace.Note`) go in the ring too.
*/
namespace PalaceMod
{
    public class LoopTraceManager : BaseManager
    {
        private const int Slots = 4096, Record = 12;
        private static FileStream file;
        private static readonly byte[] buffer = new byte[Record];
        private static int next;
        private static readonly List<string> names = [];
        private static PlayerLoopSystem? original;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            if (Environment.GetEnvironmentVariable("PALACEMOD_DEV") != "1") return;
            DevCommands.Register("looptrace", "looptrace on [file] | off | read [file] [n]   ring file of player-loop markers (which subsystem a native crash hit)", args =>
            {
                string sub = args.Length > 0 ? args[0] : "read";
                string path = args.Length > 1 && !int.TryParse(args[1], out _) ? args[1] : Path.Combine(Application.temporaryCachePath, "looptrace.bin");
                switch (sub)
                {
                    case "on": return On(path);
                    case "off": Off(); return "off";
                    case "read": return Read(path, args.Length > 1 && int.TryParse(args[^1], out int n) ? n : 40);
                    default: throw new ArgumentException("looptrace on [file] | off | read [file] [n]");
                }
            });
        }

        private static string On(string path)
        {
            Off();
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1);
            file.SetLength(Slots * Record);
            next = 0;
            names.Clear();
            var root = PlayerLoop.GetCurrentPlayerLoop();
            original = root;
            var phases = root.subSystemList;
            for (int p = 0; p < phases.Length; p++)
            {
                string phase = phases[p].type?.Name ?? $"Phase{p}";
                var list = new List<PlayerLoopSystem>();
                foreach (var s in phases[p].subSystemList ?? [])
                {
                    list.Add(Marker($"{phase}.{s.type?.Name ?? "?"}"));
                    list.Add(s);
                }
                list.Add(Marker($"{phase} end"));
                phases[p].subSystemList = list.ToArray();
            }
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            File.WriteAllLines(path + ".names", names);
            return $"tracing {names.Count} markers to {path}";
        }

        private static void Off()
        {
            if (original is PlayerLoopSystem loop) PlayerLoop.SetPlayerLoop(loop);
            original = null;
            file?.Dispose();
            file = null;
        }

        private static PlayerLoopSystem Marker(string name)
        {
            int index = names.Count;
            names.Add(name);
            return new PlayerLoopSystem { type = typeof(LoopTraceManager), updateDelegate = () => Write(index) };
        }

        /// <summary>A named event in the ring (e.g. "overlay applied"), when tracing.</summary>
        internal static void Note(string name)
        {
            if (file == null) return;
            int index = names.IndexOf(name);
            if (index < 0)
            {
                index = names.Count;
                names.Add(name);
                try { File.AppendAllLines(file.Name + ".names", [name]); } catch (IOException) { }
            }
            Write(index);
        }

        private static void Write(int marker)
        {
            if (file == null) return;
            int frame = Time.frameCount;
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), next + 1);
            BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), frame);
            BitConverter.TryWriteBytes(buffer.AsSpan(8, 4), marker);
            file.Position = (next++ % Slots) * (long)Record;
            file.Write(buffer, 0, Record);
        }

        private static string Read(string path, int count)
        {
            var bytes = File.ReadAllBytes(path);
            var labels = File.Exists(path + ".names") ? File.ReadAllLines(path + ".names") : [];
            var records = Enumerable.Range(0, bytes.Length / Record)
                .Select(i => (seq: BitConverter.ToInt32(bytes, i * Record), frame: BitConverter.ToInt32(bytes, i * Record + 4), marker: BitConverter.ToInt32(bytes, i * Record + 8)))
                .Where(r => r.seq != 0).ToList();
            var ordered = records.OrderBy(r => r.seq).ToList();
            return string.Join("\n", ordered.Skip(Math.Max(0, ordered.Count - count))
                .Select(r => $"f{r.frame} {(r.marker >= 0 && r.marker < labels.Length ? labels[r.marker] : "#" + r.marker)}"));
        }
    }
}
