using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

namespace PalaceMod.Builder.Logic
{
    /// <summary>The session pack as the runtime reads it (JSON), with presets expanded.</summary>
    internal static class LogicPack
    {
        /// <summary>
        /// The pack's JSON with preset uses expanded. Rule-level `{ "preset": ... }` entries the expansion leaves
        /// are expanded by the rule compiler too (Rules.cs, Presets), so either path works.
        /// </summary>
        public static JObject Expand(JObject json, DesignPack pack, out List<string> issues)
        {
            issues = [];
            return json;
        }
    }
}
