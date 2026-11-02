using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using UnityEngine.SceneManagement;
using BepInLevel = BepInEx.Logging.LogLevel;

/*
    PalaceMod's logging. Every line goes to the BepInEx log (BepInEx/LogOutput.log) as "[Feature] message", so
    `dev.py logs --feature X --level warn` can filter it, and into a ring buffer that the debug bundle writes out.

        Log.Info($"loaded {count} packs");            // in a manager: its own logger, named after FeatureSection
        static readonly Log log = Log.For<SaveStatesManager>();   // in static code / helper classes
        log.Debug($"restored {n} units");            // formatted only if debug is on for this feature
        log.Warn("no match save; using defaults");   // + scene/day/wave context
        log.Error("couldn't write the pack", e);     // + context and the full stack, deduplicated

    Levels: Debug (detail for chasing a bug; off unless Diagnostics."Log level" is Debug or the feature is listed in
    Diagnostics."Debug logging for"), Info (what the feature did, once per player action or less), Warn (unexpected
    but handled), Error (a bug: something the player wanted didn't happen). Identical messages within a short window
    are collapsed into one line plus a count. Interpolated strings passed to Debug/Info aren't built when the level is
    off. Debug lines are written at BepInEx's Info level (its disk log drops Debug) with a "(debug)" marker.
*/
namespace PalaceMod
{
    /// <summary>How much PalaceMod writes to the log.</summary>
    public enum LogVerbosity { Errors, Warnings, Info, Debug }

    public sealed class Log
    {
        // ---- Configuration (set by LogManager) -------------------------------------------------------------------

        private static LogVerbosity verbosity = LogVerbosity.Info;
        private static string[] debugFeatures = [];
        private static bool debugAll;
        private static int generation;

        internal static LogVerbosity Verbosity
        {
            get => verbosity;
            set { verbosity = value; generation++; }
        }

        /// <summary>Feature names with debug logging on regardless of the global level; "*" means all.</summary>
        internal static IReadOnlyList<string> DebugFeatures
        {
            get => debugFeatures;
            set
            {
                debugFeatures = (value ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                debugAll = debugFeatures.Contains("*");
                generation++;
            }
        }

        // ---- Loggers ---------------------------------------------------------------------------------------------

        private static readonly object sync = new();
        private static readonly Dictionary<string, Log> byName = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Type, Log> byType = [];
        private static readonly Dictionary<Type, string> managerNames = [];
        private static readonly HashSet<Type> featureManagers = [];

        /// <summary>Logger for mod infrastructure (Plugin, Core) that isn't a feature.</summary>
        public static readonly Log Core = For("PalaceMod");

        /// <summary>A logger with a fixed name, e.g. for a subsystem shared by several managers.</summary>
        public static Log For(string name)
        {
            lock (sync)
            {
                if (!byName.TryGetValue(name, out var log)) byName[name] = log = new Log(name, null);
                return log;
            }
        }

        /// <summary>The logger of a manager (named after its FeatureSection) or of any other type (its name).
        /// Cheap enough to call inline: <c>Log.For&lt;SaveStatesManager&gt;().Debug($"...")</c>.</summary>
        public static Log For<T>() => Cache<T>.Value;

        private static class Cache<T>
        {
            public static readonly Log Value = For(typeof(T));
        }

        public static Log For(Type type)
        {
            lock (sync)
            {
                if (!byType.TryGetValue(type, out var log)) byType[type] = log = new Log(null, type);
                return log;
            }
        }

        /// <summary>Every logger name seen so far (managers' names once registered).</summary>
        internal static IEnumerable<string> KnownNames
        {
            get { lock (sync) return byName.Keys.Concat(managerNames.Values).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(); }
        }

        internal static void RegisterManager(BaseManager manager)
        {
            lock (sync)
            {
                managerNames[manager.GetType()] = manager.FeatureSection ?? DerivedName(manager.GetType());
                if (manager.FeatureSection != null) featureManagers.Add(manager.GetType());
            }
            generation++;
        }

        private static string DerivedName(Type type)
        {
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            return name.EndsWith("Manager") && name.Length > 7 ? name.Substring(0, name.Length - 7) : name;
        }

        private readonly string fixedName;
        private readonly Type type;
        private string resolvedName;
        private int cachedGeneration = -1;
        private bool debugOn;

        private Log(string name, Type type)
        {
            fixedName = name;
            this.type = type;
        }

        /// <summary>The prefix this logger writes, e.g. "Savestates".</summary>
        public string Name
        {
            get
            {
                if (fixedName != null) return fixedName;
                if (resolvedName != null) return resolvedName;
                lock (sync)
                {
                    // A manager's name is final once Plugin registers it; until then use the derived one uncached.
                    if (managerNames.TryGetValue(type, out var name)) return resolvedName = name;
                }
                return DerivedName(type);
            }
        }

        /// <summary>Whether Debug messages from this logger are written.</summary>
        public bool IsDebugEnabled
        {
            get
            {
                if (verbosity == LogVerbosity.Debug) return true;
                if (cachedGeneration == generation) return debugOn;
                int now = generation;
                bool on = debugAll || (debugFeatures.Length > 0 && debugFeatures.Contains(Name, StringComparer.OrdinalIgnoreCase));
                debugOn = on;
                if (fixedName != null || resolvedName != null) cachedGeneration = now;
                return on;
            }
        }

        public bool IsInfoEnabled => verbosity >= LogVerbosity.Info || IsDebugEnabled;
        public bool IsWarnEnabled => verbosity >= LogVerbosity.Warnings || IsDebugEnabled;

        // ---- Writing ---------------------------------------------------------------------------------------------

        public void Debug(string message)
        {
            if (IsDebugEnabled) Write(Level.Debug, message, null, false);
        }

        public void Debug([InterpolatedStringHandlerArgument("")] ref DebugMessage message)
        {
            if (message.Enabled) Write(Level.Debug, message.ToStringAndClear(), null, false);
        }

        public void Info(string message)
        {
            if (IsInfoEnabled) Write(Level.Info, message, null, false);
        }

        public void Info([InterpolatedStringHandlerArgument("")] ref InfoMessage message)
        {
            if (message.Enabled) Write(Level.Info, message.ToStringAndClear(), null, false);
        }

        /// <summary>Something unexpected that was handled. Adds the game context (scene, day/night, wave).</summary>
        public void Warn(string message, Exception exception = null)
        {
            if (IsWarnEnabled) Write(Level.Warn, message, exception, true);
        }

        /// <summary>A bug: something didn't happen that should have. Adds the game context and the exception's full
        /// stack. Always written (repeats are collapsed).</summary>
        public void Error(string message, Exception exception = null) => Write(Level.Error, message, exception, true);

        /// <summary>Logs an exception caught around someone else's code (a callback, an event handler), naming the
        /// feature whose code threw if its stack shows one, otherwise this logger.</summary>
        public void Caught(string what, Exception exception) => (Attribute(exception) ?? this).Error(what, exception);

        /// <summary>Runs <paramref name="action"/>, logging (once per distinct failure) instead of throwing.</summary>
        public void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Caught(what, e); }
        }

        internal enum Level { Debug, Info, Warn, Error }

        internal void Write(Level level, string message, Exception exception, bool context, string stack = null)
        {
            try
            {
                string name = Name;
                stack ??= exception?.ToString();
                // Exceptions collapse by where they were thrown, so a message with a changing number still collapses.
                string key = exception != null ? $"{name}|{message}|{exception.GetType().FullName}|{exception.StackTrace}"
                    : stack != null ? $"{name}|{message}|{stack}" : $"{name}|{(int)level}|{message}";
                // A stack is written once per session; later repeats are only counted (see Repeats.Flush).
                if (!Repeats.Allow(key, level, name, message, stack != null, out string note)) return;

                var text = new StringBuilder(message.Length + 64);
                text.Append('[').Append(name).Append("] ");
                if (level == Level.Debug) text.Append("(debug) ");
                text.Append(message);
                if (note != null) text.Append(' ').Append(note);
                if (context) text.Append(" | ").Append(GameContext.Describe());
                if (stack != null) text.Append('\n').Append(stack);
                var source = Plugin.Logger;
                var bepinLevel = level switch { Level.Error => BepInLevel.Error, Level.Warn => BepInLevel.Warning, _ => BepInLevel.Info };
                if (source != null) source.Log(bepinLevel, text.ToString());
                else UnityEngine.Debug.Log(text.ToString());
            }
            catch
            {
                // Logging must never break the caller.
            }
        }

        // ---- Exception attribution -------------------------------------------------------------------------------

        private static Dictionary<string, Type> modTypes;

        /// <summary>The logger of the feature whose code appears first in a stack trace, or null if none does.</summary>
        internal static Log Attribute(Exception exception)
        {
            if (exception == null) return null;
            Log found = null;
            for (var e = exception; e != null && found == null; e = e.InnerException) found = AttributeStack(e.StackTrace);
            return found;
        }

        /// <summary>Same, for a stack trace as Unity or .NET prints it. Prefers a feature's frame over Core's.</summary>
        internal static Log AttributeStack(string stack)
        {
            if (string.IsNullOrEmpty(stack) || stack.IndexOf("PalaceMod.", StringComparison.Ordinal) < 0) return null;
            Type first = null;
            foreach (var raw in stack.Split('\n'))
            {
                var t = FrameType(raw);
                if (t == null) continue;
                first ??= t;
                var manager = OwningManager(t);
                if (manager == null) continue;
                lock (sync)
                {
                    if (featureManagers.Contains(manager)) return For(manager);
                }
            }
            return first == null ? null : For(OwningManager(first) ?? NamespaceOwner(first) ?? first);
        }

        private static Type FrameType(string line)
        {
            var s = line.Trim();
            if (s.StartsWith("at ")) s = s.Substring(3);
            if (!s.StartsWith("PalaceMod.")) return null;
            int paren = s.IndexOf(" (", StringComparison.Ordinal);
            if (paren < 0) paren = s.IndexOf('(');
            if (paren > 0) s = s.Substring(0, paren);
            s = s.Trim();
            int bracket = s.IndexOf('[');
            if (bracket > 0) s = s.Substring(0, bracket);
            if (s.EndsWith("..ctor") || s.EndsWith(".cctor")) s = s.Substring(0, s.LastIndexOf("..", StringComparison.Ordinal));
            else
            {
                int dot = s.LastIndexOf('.');
                if (dot <= 0) return null;
                s = s.Substring(0, dot);
            }
            var types = modTypes;
            if (types == null)
            {
                types = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var t in HarmonyLib.AccessTools.GetTypesFromAssembly(typeof(Log).Assembly))
                {
                    if (t.FullName == null) continue;
                    string full = t.FullName;
                    int tick = full.IndexOf('`');
                    types[full] = t;
                    types[full.Replace('+', '/')] = t;
                    if (tick > 0 && !full.Contains('+')) types[full.Substring(0, tick)] = t;
                }
                modTypes = types;
            }
            return types.TryGetValue(s, out var type) ? type : null;
        }

        private static Type OwningManager(Type type)
        {
            for (var t = type; t != null; t = t.DeclaringType)
            {
                if (typeof(BaseManager).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        // Features in their own namespace (PalaceMod.Builder.*, PalaceMod.Profiles) map their helpers to the manager
        // of that namespace when there's exactly one.
        private static Type NamespaceOwner(Type type)
        {
            var ns = type.Namespace;
            if (ns == null || ns == "PalaceMod" || !ns.StartsWith("PalaceMod.")) return null;
            string top = "PalaceMod." + ns.Substring(10).Split('.')[0];
            var owners = modTypes.Values.Distinct().Where(t => !t.IsAbstract && typeof(BaseManager).IsAssignableFrom(t)
                && t.Namespace != null && (t.Namespace == top || t.Namespace.StartsWith(top + "."))).Take(2).ToList();
            return owners.Count == 1 ? owners[0] : null;
        }

        // ---- Repeats ---------------------------------------------------------------------------------------------

        /// <summary>Collapses identical messages: the first is written, repeats within the window are counted and
        /// reported once the window ends (by Flush, or with the next repeat).</summary>
        internal static class Repeats
        {
            private const double WindowSeconds = 30;
            private const int MaxKeys = 1024;
            private static readonly Stopwatch clock = Stopwatch.StartNew();

            private sealed class Entry
            {
                public double WindowEnd;
                public int Suppressed;
                public bool Once;
                public Level Level;
                public string Name, Message;
            }

            private static readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

            public static bool Allow(string key, Level level, string name, string message, bool once, out string note)
            {
                note = null;
                double now = clock.Elapsed.TotalSeconds;
                lock (entries)
                {
                    if (entries.TryGetValue(key, out var entry))
                    {
                        if (entry.Once || now < entry.WindowEnd)
                        {
                            entry.Suppressed++;
                            return false;
                        }
                        if (entry.Suppressed > 0) note = $"(and {entry.Suppressed} more like it in the last {WindowSeconds:0} s)";
                        entry.Suppressed = 0;
                        entry.WindowEnd = now + WindowSeconds;
                        return true;
                    }
                    if (entries.Count >= MaxKeys) Prune(now);
                    entries[key] = new Entry { WindowEnd = now + WindowSeconds, Level = level, Name = name, Message = message, Once = once };
                    return true;
                }
            }

            /// <summary>Writes a count for every collapsed message whose window has ended.</summary>
            public static void Flush()
            {
                List<Entry> due = null;
                double now = clock.Elapsed.TotalSeconds;
                lock (entries)
                {
                    foreach (var entry in entries.Values)
                    {
                        if (entry.Suppressed == 0 || now < entry.WindowEnd) continue;
                        (due ??= []).Add(new Entry { Suppressed = entry.Suppressed, Level = entry.Level, Name = entry.Name, Message = entry.Message });
                        entry.Suppressed = 0;
                        entry.WindowEnd = now + WindowSeconds;
                    }
                }
                if (due == null) return;
                foreach (var entry in due)
                {
                    string message = $"Repeated {entry.Suppressed} more time{(entry.Suppressed == 1 ? "" : "s")}: {entry.Message}";
                    var level = entry.Level == Level.Error ? BepInLevel.Warning : BepInLevel.Info;
                    Plugin.Logger?.Log(level, $"[{entry.Name}] {(entry.Level == Level.Debug ? "(debug) " : "")}{message}");
                }
            }

            private static void Prune(double now)
            {
                foreach (var key in entries.Where(p => !p.Value.Once && p.Value.Suppressed == 0 && now >= p.Value.WindowEnd).Select(p => p.Key).ToList())
                    entries.Remove(key);
                if (entries.Count >= MaxKeys)
                {
                    foreach (var key in entries.Where(p => p.Value.Suppressed == 0).Select(p => p.Key).Take(MaxKeys / 2).ToList()) entries.Remove(key);
                }
            }
        }

        // ---- Interpolated string handlers ------------------------------------------------------------------------

        [InterpolatedStringHandler]
        public ref struct DebugMessage
        {
            private StringBuilder builder;
            internal bool Enabled => builder != null;

            public DebugMessage(int literalLength, int formattedCount, Log log, out bool enabled)
            {
                enabled = log.IsDebugEnabled;
                builder = enabled ? new StringBuilder(literalLength + formattedCount * 8) : null;
            }

            public void AppendLiteral(string value) => builder.Append(value);
            public void AppendFormatted<T>(T value) => Format.Append(builder, value, null, 0);
            public void AppendFormatted<T>(T value, string format) => Format.Append(builder, value, format, 0);
            public void AppendFormatted<T>(T value, int alignment) => Format.Append(builder, value, null, alignment);
            public void AppendFormatted<T>(T value, int alignment, string format) => Format.Append(builder, value, format, alignment);
            internal string ToStringAndClear() => builder.ToString();
        }

        [InterpolatedStringHandler]
        public ref struct InfoMessage
        {
            private StringBuilder builder;
            internal bool Enabled => builder != null;

            public InfoMessage(int literalLength, int formattedCount, Log log, out bool enabled)
            {
                enabled = log.IsInfoEnabled;
                builder = enabled ? new StringBuilder(literalLength + formattedCount * 8) : null;
            }

            public void AppendLiteral(string value) => builder.Append(value);
            public void AppendFormatted<T>(T value) => Format.Append(builder, value, null, 0);
            public void AppendFormatted<T>(T value, string format) => Format.Append(builder, value, format, 0);
            public void AppendFormatted<T>(T value, int alignment) => Format.Append(builder, value, null, alignment);
            public void AppendFormatted<T>(T value, int alignment, string format) => Format.Append(builder, value, format, alignment);
            internal string ToStringAndClear() => builder.ToString();
        }

        private static class Format
        {
            public static void Append<T>(StringBuilder builder, T value, string format, int alignment)
            {
                // Same culture as ordinary string interpolation, so migrated messages read the same.
                string text = value is IFormattable f ? f.ToString(format, CultureInfo.CurrentCulture) : value?.ToString();
                text ??= "";
                if (alignment > 0 && text.Length < alignment) builder.Append(' ', alignment - text.Length);
                builder.Append(text);
                if (alignment < 0 && text.Length < -alignment) builder.Append(' ', -alignment - text.Length);
            }
        }
    }

    /// <summary>Where the game is, for warnings, errors and the debug bundle: "scene Nordfels, night 3 (wave 2)".</summary>
    internal static class GameContext
    {
        private static int mainThread = -1;

        internal static void CaptureMainThread() => mainThread = Thread.CurrentThread.ManagedThreadId;

        public static string Describe()
        {
            if (mainThread >= 0 && Thread.CurrentThread.ManagedThreadId != mainThread) return "background thread";
            try
            {
                var parts = new List<string> { $"scene {SceneManager.GetActiveScene().name}" };
                var state = LocalGamestate.Instance;
                if (state != null && state.CurrentState != LocalGamestate.State.InMatch) parts.Add(state.CurrentState.ToString());
                var cycle = DayNightCycle.Instance;
                var spawner = EnemySpawner.instance;
                if (cycle != null && spawner != null)
                {
                    int wave = spawner.Wavenumber;
                    parts.Add(cycle.CurrentTimestate == DayNightCycle.Timestate.Night ? $"night {wave + 1} (wave {wave})" : $"day {wave + 2}");
                }
                if (UnityEngine.Time.timeScale == 0f) parts.Add("paused");
                return string.Join(", ", parts);
            }
            catch
            {
                return "context unavailable";
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    // C# 10 interpolated string handler support; netstandard2.1 doesn't ship these attributes.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument) => Arguments = [argument];
        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments) => Arguments = arguments;
        public string[] Arguments { get; }
    }
}
