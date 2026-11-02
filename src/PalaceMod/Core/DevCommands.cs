using System;
using System.Collections.Generic;
using System.Linq;

/*
    Test commands that `python3 scripts/dev.py cmd "<name> <args...>"` can run in a dev launch. Register them from any
    manager's Awake, so features add their own without editing DevToolsManager:

        DevCommands.Register("formation", "formation <name>   switch the unit formation", args =>
        {
            ...
            return "now Line";   // printed by dev.py; throw (e.g. ArgumentException) to report a failure
        });

    Handlers run on the main thread, get the words after the command name, and return a result string (or "").
    `dev.py cmd help` lists every command.
*/
namespace PalaceMod
{
    public static class DevCommands
    {
        public sealed class Command
        {
            public string Name { get; internal set; }
            public string Usage { get; internal set; }
            public Func<string[], string> Handler { get; internal set; }
        }

        private static readonly Dictionary<string, Command> commands = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registered commands, by name.</summary>
        public static IEnumerable<Command> All => commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        /// <param name="name">One word, case-insensitive; must not clash with DevToolsManager's built-ins.</param>
        /// <param name="usage">One line for `cmd help`, e.g. "formation &lt;name&gt;   switch the unit formation".</param>
        /// <param name="handler">Gets the arguments after the name; returns the result text or throws on failure.</param>
        public static void Register(string name, string usage, Func<string[], string> handler)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains(' ')) throw new ArgumentException($"invalid dev command name \"{name}\"");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (DevToolsManager.IsBuiltIn(name)) throw new ArgumentException($"dev command \"{name}\" is a DevToolsManager built-in");
            if (commands.ContainsKey(name)) throw new ArgumentException($"dev command \"{name}\" is already registered");
            commands[name] = new Command { Name = name, Usage = usage ?? name, Handler = handler };
        }

        internal static bool TryGet(string name, out Command command) => commands.TryGetValue(name, out command);
    }
}
