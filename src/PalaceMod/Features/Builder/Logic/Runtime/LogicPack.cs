using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Builder.Logic
{
    /// <summary>The session pack as the runtime reads it (JSON): presets expanded (L4's Presets.ExpandJson; every rule
    /// it produces carries `_src`, its path for the trace), and the SpawnOnDeath rules UnitFactory lowers onto the
    /// game's own `enemyToSpawnOnDeath` removed, so they don't run twice.</summary>
    internal static class LogicPack
    {
        public static JObject Expand(JObject json, DesignPack pack, out List<string> issues)
        {
            var found = new List<ValidationIssue>();
            Model.Logic.Presets.ExpandJson(json, pack, found);
            issues = found.Select(i => i.ToString()).ToList();

            var isUnit = LogicLowering.IsUnitIn(pack);
            foreach (var u in (json["units"] as JArray)?.OfType<JObject>() ?? [])
            {
                if (u["rules"] is not JArray rules) continue;
                string id = (string)u["id"];
                for (int i = rules.Count - 1; i >= 0; i--)
                    if (LogicLowering.IsLoweredSpawnOnDeath(rules, i, id, isUnit)) rules.RemoveAt(i);
            }
            return json;
        }
    }
}
