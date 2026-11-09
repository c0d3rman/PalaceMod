using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    The Logic palette's objects (docs/builder-engine.md, "Logic palette tab"): each is a place written as plain rules
    and components, so its card reads like any other and nothing about it is coded. Outputs are "gets signal <name>"
    rules with nothing in Then yet: wire from them (Wire tool) or add actions. Inputs are signals: wire to them.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class LogicObject
    {
        public string Id, Name, IdPrefix, Desc, Icon;
        public JObject Place;
    }

    internal static class LogicObjects
    {
        public static readonly List<LogicObject> All =
        [
            Obj("area", "Area", "area", "A zone that notices what's inside: enter, exit, inside, empty.",
                @"{ ""kind"": ""area"", ""area"": { ""shape"": { ""circle"": { ""radius"": 4 } }, ""watch"": ""unit"", ""show"": ""outline"" } }"),
            Obj("plate", "Pressure plate", "plate", "An area that fires once a second while enemies stand on it.",
                @"{ ""kind"": ""area"", ""area"": { ""shape"": { ""rect"": { ""size"": [4, 4] } }, ""watch"": ""enemy"", ""show"": ""fill"", ""tick"": 1 },
                     ""rules"": [ { ""when"": { ""on"": ""every"", ""seconds"": 1 }, ""if"": ""count('enemy and in:self') > 0"", ""do"": [] } ] }"),
            Obj("point", "Point", "point", "A named spot to teleport, spawn or walk to (@name).", @"{ ""kind"": ""point"" }"),
            Obj("path", "Path", "path", "A route for movers and walkers. Move it whole; edit its points in the Behaviour tab.",
                @"{ ""kind"": ""path"", ""points"": [[0, 0, 0], [6, 0, 0], [6, 0, 6]] }"),
            Obj("timer", "Timer", "timer", "Does something every few seconds.",
                @"{ ""kind"": ""logic"", ""rules"": [ { ""when"": { ""on"": ""every"", ""seconds"": 5 }, ""do"": [] } ] }"),
            Obj("counter", "Counter", "counter", "Counts \"add\" signals; at the target it fires \"done\" and starts over.",
                @"{ ""kind"": ""logic"", ""vars"": { ""count"": 0, ""target"": 3 },
                     ""rules"": [ { ""when"": { ""on"": ""signal"", ""name"": ""add"" }, ""do"": [
                                  { ""do"": ""addVar"", ""name"": ""count"", ""value"": 1 },
                                  { ""do"": ""if"", ""cond"": ""self.$count >= self.$target"", ""then"": [
                                      { ""do"": ""setVar"", ""name"": ""count"", ""value"": 0 },
                                      { ""do"": ""signal"", ""name"": ""done"", ""to"": ""self"" } ] } ] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""done"" }, ""do"": [] } ] }"),
            Obj("relay", "Relay", "relay", "Passes \"in\" on as \"out\" after a delay.",
                @"{ ""kind"": ""logic"", ""rules"": [ { ""when"": { ""on"": ""signal"", ""name"": ""in"" }, ""do"": [ { ""do"": ""signal"", ""name"": ""out"", ""to"": ""self"", ""delay"": 1 } ] },
                                                 { ""when"": { ""on"": ""signal"", ""name"": ""out"" }, ""do"": [] } ] }"),
            Obj("gate", "Gate", "gate", "Fires \"done\" once both \"a\" and \"b\" have arrived (AND).",
                @"{ ""kind"": ""logic"", ""vars"": { ""a"": false, ""b"": false },
                     ""rules"": [ { ""when"": { ""on"": ""signal"", ""name"": ""a"" }, ""do"": [ { ""do"": ""setVar"", ""name"": ""a"", ""value"": ""true"" }, { ""do"": ""if"", ""cond"": ""self.$b"", ""then"": [ { ""do"": ""signal"", ""name"": ""done"", ""to"": ""self"" } ] } ] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""b"" }, ""do"": [ { ""do"": ""setVar"", ""name"": ""b"", ""value"": ""true"" }, { ""do"": ""if"", ""cond"": ""self.$a"", ""then"": [ { ""do"": ""signal"", ""name"": ""done"", ""to"": ""self"" } ] } ] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""done"" }, ""do"": [] } ] }"),
            Obj("toggle", "Toggle", "toggle", "Each \"flip\" switches between \"on\" and \"off\".",
                @"{ ""kind"": ""logic"", ""vars"": { ""on"": false },
                     ""rules"": [ { ""when"": { ""on"": ""signal"", ""name"": ""flip"" }, ""do"": [ { ""do"": ""setVar"", ""name"": ""on"", ""value"": ""not self.$on"" },
                                  { ""do"": ""if"", ""cond"": ""self.$on"", ""then"": [ { ""do"": ""signal"", ""name"": ""on"", ""to"": ""self"" } ], ""else"": [ { ""do"": ""signal"", ""name"": ""off"", ""to"": ""self"" } ] } ] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""on"" }, ""do"": [] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""off"" }, ""do"": [] } ] }"),
            Obj("random", "Random", "random", "Each \"in\" goes out as \"out1\" or \"out2\" at random.",
                @"{ ""kind"": ""logic"", ""rules"": [ { ""when"": { ""on"": ""signal"", ""name"": ""in"" }, ""do"": [ { ""do"": ""choose"", ""options"": [
                                  { ""weight"": 1, ""do"": [ { ""do"": ""signal"", ""name"": ""out1"", ""to"": ""self"" } ] },
                                  { ""weight"": 1, ""do"": [ { ""do"": ""signal"", ""name"": ""out2"", ""to"": ""self"" } ] } ] } ] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""out1"" }, ""do"": [] },
                                { ""when"": { ""on"": ""signal"", ""name"": ""out2"" }, ""do"": [] } ] }"),
            Obj("spawner", "Spawner", "spawner", "Spawns a unit here on \"spawn\" (pick which in its rule).",
                @"{ ""kind"": ""logic"", ""rules"": [ { ""when"": { ""on"": ""signal"", ""name"": ""spawn"" }, ""do"": [ { ""do"": ""spawn"", ""entity"": ""vanilla:E Racer"", ""count"": 1, ""at"": ""self"" } ] } ] }"),
            Obj("lever", "Lever", "lever", "The king uses it by day; wire its \"is used\" to something.",
                @"{ ""kind"": ""logic"", ""interact"": { ""prompt"": ""Pull the lever"", ""when"": ""day"", ""radius"": 3, ""repeat"": true },
                     ""rules"": [ { ""when"": ""interact"", ""do"": [] } ] }"),
            Obj("door", "Door", "door", "A solid door with open and closed poses (inputs: open, closed).",
                @"{ ""kind"": ""prop"", ""body"": { ""solid"": true, ""box"": [4, 1], ""height"": 3 },
                     ""mover"": { ""pose"": { ""open"": { ""rotY"": 90 }, ""closed"": { ""rotY"": 0 } } } }"),
            Obj("wall", "Moving wall", "wall", "A solid wall that slides back and forth (inputs: start, stop, reverse).",
                @"{ ""kind"": ""prop"", ""body"": { ""solid"": true, ""box"": [6, 1], ""height"": 3 },
                     ""mover"": { ""path"": [[0, 0], [6, 0]], ""mode"": ""pingpong"", ""speed"": 2, ""wait"": 1 } }"),
            Obj("sign", "Sign", "sign", "Shows a message when the king reads it.",
                @"{ ""kind"": ""logic"", ""interact"": { ""prompt"": ""Read"", ""radius"": 3, ""repeat"": true },
                     ""rules"": [ { ""when"": ""interact"", ""do"": [ { ""do"": ""message"", ""text"": ""Hello!"" } ] } ] }"),
            Obj("pickup", "Gold pickup", "pickup", "The king walks over it for gold.",
                @"{ ""kind"": ""pickup"", ""pickup"": { ""by"": ""king"", ""radius"": 1.5 }, ""rules"": [ { ""when"": ""pickup"", ""do"": [ { ""do"": ""gold"", ""amount"": 5, ""at"": ""self"" } ] } ] }"),
        ];

        private static LogicObject Obj(string id, string name, string prefix, string desc, string json) =>
            new() { Id = id, Name = name, IdPrefix = prefix, Desc = desc, Place = JObject.Parse(json) };

        public static LogicObject Find(string id) => All.FirstOrDefault(o => o.Id == id);
    }
}
