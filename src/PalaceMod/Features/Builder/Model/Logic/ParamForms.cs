using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

/*
    How an action param's JSON value is read, by its schema type (LogicSchema.ParamTypes). This is the one place that
    decides whether a string is an expression, a literal name, interpolated text or a filter: the runtime
    (Logic/Runtime/Actions.cs, CompiledParams) and the validator (LogicValidator.Param, and its Python port
    palace_logic.py) both call it, so whatever `check` accepts is read the same way in the game.

    - number int bool position entity entities any: an expression (a leading `=` is allowed).
    - text signal label: literal text with {expr} parts; `=expr` makes the whole field an expression.
    - color fx sound enum status template: a literal (a colour, a name, an id); `=expr` for an expression.
    - var rule local: always a literal name.
    - filter: a filter string. for: "night"/"day", else an expression. night: "current", else an expression.
    - slot: "castle", "slot:<name>", "add:<id>" are Studio keys; anything else is an expression (a selector, `it`,
      `$var`, `@name`...). entities: "all" is a literal on `signal.to`.
    - actions options targeting movement types keep json: structured JSON, never a string.
*/
namespace PalaceMod.Builder.Model.Logic
{
    public enum ParamForm
    {
        /// <summary>Absent or null.</summary>
        None,
        /// <summary>An expression; Source is it without the leading `=`.</summary>
        Expression,
        /// <summary>Text with {expr} parts.</summary>
        Text,
        /// <summary>A literal name, id, colour or key (Source).</summary>
        Literal,
        /// <summary>A filter string.</summary>
        Filter,
        /// <summary>A JSON number, bool or [x, z] position (read as is).</summary>
        Json,
        /// <summary>Structured JSON (an action list, a targeting list, a movement block...).</summary>
        Structured,
    }

    /// <summary>Optional game context for the validator: the names the game resolves at runtime (Catalog implements
    /// it; palace.py passes its ref cache's lists). Null lists are unknown, so that check is skipped.</summary>
    public interface ILogicNamesContext
    {
        IReadOnlyCollection<string> Effects { get; }
        IReadOnlyCollection<string> Sounds { get; }
    }

    public static class ParamForms
    {
        private static readonly HashSet<string> LiteralTypes = new(StringComparer.Ordinal) { "color", "fx", "sound", "enum", "status", "template" };
        /// <summary>Names that are never expressions (a variable or rule to write or switch).</summary>
        private static readonly HashSet<string> NameTypes = new(StringComparer.Ordinal) { "var", "rule", "local" };
        private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal) { "text", "signal", "label" };
        private static readonly HashSet<string> StructuredTypes = new(StringComparer.Ordinal) { "actions", "options", "targeting", "movement", "types", "keep", "json" };

        /// <summary>How a param of <paramref name="type"/> reads <paramref name="v"/>; <paramref name="source"/> is the
        /// expression (without `=`), the literal, the text or the filter. <paramref name="name"/> is the param's name
        /// (`signal.to` takes "all").</summary>
        public static ParamForm Classify(string type, string name, JToken v, out string source)
        {
            source = null;
            if (v == null || v.Type == JTokenType.Null) return ParamForm.None;
            if (v.Type != JTokenType.String)
                return type != null && StructuredTypes.Contains(type) ? ParamForm.Structured : ParamForm.Json;
            string s = (string)v;
            source = s;
            bool eq = s.StartsWith("=", StringComparison.Ordinal);
            switch (type)
            {
                case null:
                case "number": case "int": case "bool": case "position": case "entity": case "any":
                    break;
                case "entities":
                    if (name == "to" && s == "all") return ParamForm.Literal;
                    break;
                case "for":
                    if (s is "night" or "day") return ParamForm.Literal;
                    break;
                case "night":
                    if (s == "current") return ParamForm.Literal;
                    break;
                case "slot":
                    if (IsSlotKey(s)) return ParamForm.Literal;
                    break;
                case "filter":
                    return ParamForm.Filter;
                default:
                    if (NameTypes.Contains(type)) return ParamForm.Literal;
                    if (TextTypes.Contains(type))
                    {
                        if (!eq) return ParamForm.Text;
                        break;
                    }
                    if (LiteralTypes.Contains(type))
                    {
                        if (!eq) return ParamForm.Literal;
                        break;
                    }
                    if (StructuredTypes.Contains(type)) return ParamForm.Structured;
                    break;
            }
            if (eq) source = s.Substring(1);
            return ParamForm.Expression;
        }

        /// <summary>A Studio key naming a build slot directly: "castle", "slot:&lt;name&gt;", "add:&lt;id&gt;".</summary>
        public static bool IsSlotKey(string s) =>
            s == "castle" || s.StartsWith("slot:", StringComparison.Ordinal) || s.StartsWith("add:", StringComparison.Ordinal);

        // ---- colours ------------------------------------------------------------------------------------------

        /// <summary>The colour names a colour param takes besides "#rrggbb" (Unity's ColorUtility names).</summary>
        public static readonly IReadOnlyDictionary<string, string> ColorNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = "#ff0000", ["cyan"] = "#00ffff", ["blue"] = "#0000ff", ["darkblue"] = "#0000a0", ["lightblue"] = "#add8e6",
            ["purple"] = "#800080", ["yellow"] = "#ffff00", ["lime"] = "#00ff00", ["fuchsia"] = "#ff00ff", ["white"] = "#ffffff",
            ["silver"] = "#c0c0c0", ["grey"] = "#808080", ["gray"] = "#808080", ["black"] = "#000000", ["orange"] = "#ffa500",
            ["brown"] = "#a52a2a", ["maroon"] = "#800000", ["green"] = "#008000", ["olive"] = "#808000", ["navy"] = "#000080",
            ["teal"] = "#008080", ["aqua"] = "#00ffff", ["magenta"] = "#ff00ff",
        };

        /// <summary>Parses a colour: "#rgb", "#rrggbb", "#rrggbbaa" (the "#" optional) or a colour name. The one
        /// parser for colour params, used by the runtime and the validator alike.</summary>
        public static bool TryParseColor(string text, out float r, out float g, out float b, out float a)
        {
            r = g = b = 0; a = 1;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (ColorNames.TryGetValue(s, out var named)) s = named;
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3) s = new string([s[0], s[0], s[1], s[1], s[2], s[2]]);
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            if (s.Length == 6) v = (v << 8) | 0xff;
            r = ((v >> 24) & 255) / 255f;
            g = ((v >> 16) & 255) / 255f;
            b = ((v >> 8) & 255) / 255f;
            a = (v & 255) / 255f;
            return true;
        }

        public const string ColorHelp = "\"#rrggbb\" (or \"#rgb\", \"#rrggbbaa\", or a colour name like \"red\")";
    }
}
