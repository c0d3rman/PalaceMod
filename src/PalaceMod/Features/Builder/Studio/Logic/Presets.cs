using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    Presets (docs/builder-engine.md, "Presets"): saved rule sets with parameters, used as a rule entry
    { "preset": id, "with": {...} }. The picker lists the built-in ones (Assets/builder-presets.json, when the DLL has
    it) and the pack's own ("presets"); "Save as preset" writes a pack preset from a rule, and "Unpack" turns a preset
    entry back into plain rules. Expansion here is for the Studio (previews, unpack); the engine expands at compile.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class PresetInfo
    {
        public string Id, Name, Desc;
        public bool BuiltIn;
        public JObject Def;
        public List<ParamUi> Params = [];

        public string Describe(JObject with)
        {
            string text = Desc ?? "";
            foreach (var p in Params)
            {
                var v = with?[p.Name] ?? (p.Default != null ? new JValue(p.Default) : null);
                text = text.Replace("{" + p.Name + "}", v == null ? "?" : LogicFields.Raw(v));
            }
            return text;
        }
    }

    internal static class Presets
    {
        private static List<PresetInfo> builtIn;

        public static IEnumerable<PresetInfo> All()
        {
            builtIn ??= LoadBuiltIn();
            var pack = Studio.Draft?.Pack ?? Enemies.EnemyStudio.Draft?.Pack;
            var own = (LogicDoc.Get(pack, "presets") as JArray)?.OfType<JObject>().Select(o => Info(o, false)).Where(p => p != null).ToList() ?? [];
            return own.Concat(builtIn.Where(b => own.All(o => o.Id != b.Id)));
        }

        public static PresetInfo Find(string id) => All().FirstOrDefault(p => p.Id == id);

        private static List<PresetInfo> LoadBuiltIn()
        {
            try
            {
                var asm = typeof(Presets).Assembly;
                string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("builder-presets.json", StringComparison.OrdinalIgnoreCase));
                if (name == null) return [];
                using var s = asm.GetManifestResourceStream(name);
                using var r = new StreamReader(s);
                var root = JToken.Parse(r.ReadToEnd());
                var list = root as JArray ?? root["presets"] as JArray;
                return list?.OfType<JObject>().Select(o => Info(o, true)).Where(p => p != null).ToList() ?? [];
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Built-in presets couldn't be read: {e.Message}");
                return [];
            }
        }

        public static PresetInfo Info(JObject o, bool builtIn)
        {
            string id = (string)o["id"];
            if (id == null) return null;
            var info = new PresetInfo { Id = id, Name = (string)o["name"] ?? id, Desc = (string)o["description"], BuiltIn = builtIn, Def = o };
            foreach (var p in (o["params"] as JObject)?.Properties() ?? [])
            {
                var spec = p.Value as JObject;
                string type = (string)spec?["type"] ?? "number";
                var param = LogicCatalog.Param($"{p.Name}:{type}");
                param.Default = spec?["default"] is { } d ? LogicFields.Raw(d) : null;
                param.Desc = (string)spec?["description"];
                info.Params.Add(param);
            }
            return info;
        }

        /// <summary>The preset's rules with its params filled in ("{count}" alone becomes the typed value; inside text,
        /// its text).</summary>
        public static JArray Expand(PresetInfo preset, JObject with)
        {
            var values = new Dictionary<string, JToken>();
            foreach (var p in preset.Params)
            {
                var v = with?[p.Name] ?? LogicDoc.Literal(p);
                if (v != null) values[p.Name] = v;
            }
            var rules = (JArray)(preset.Def["rules"]?.DeepClone() ?? new JArray());
            Substitute(rules, values);
            return rules;
        }

        private static void Substitute(JToken t, Dictionary<string, JToken> values)
        {
            switch (t)
            {
                case JObject o:
                    foreach (var p in o.Properties().ToList()) Substitute(p.Value, values);
                    break;
                case JArray a:
                    foreach (var x in a.ToList()) Substitute(x, values);
                    break;
                case JValue v when v.Type == JTokenType.String:
                    string s = (string)v;
                    if (s.Length > 2 && s[0] == '{' && s[s.Length - 1] == '}' && values.TryGetValue(s.Substring(1, s.Length - 2), out var whole)) { v.Replace(whole.DeepClone()); break; }
                    foreach (var kv in values) s = s.Replace("{" + kv.Key + "}", LogicFields.Raw(kv.Value));
                    v.Value = s;
                    break;
            }
        }

        /// <summary>Replaces a preset entry with its rules (and its vars on the owner), one undo step.</summary>
        public static void Unpack(LogicOwner owner, int index)
        {
            var rule = owner.Rules()?[index] as JObject;
            var preset = Find((string)rule?["preset"]);
            if (preset == null) return;
            var expanded = Expand(preset, rule["with"] as JObject);
            owner.Edit($"Unpack {preset.Name}", node =>
            {
                var rules = LogicDoc.EnsureRules(node);
                rules.RemoveAt(index);
                for (int i = 0; i < expanded.Count; i++) rules.Insert(index + i, expanded[i].DeepClone());
                if (preset.Def["vars"] is JObject pv)
                {
                    if (node["vars"] is not JObject vars) node["vars"] = vars = new JObject();
                    foreach (var p in pv.Properties()) vars[p.Name] ??= p.Value.DeepClone();
                }
            });
        }

        /// <summary>Adds a preset entry to an owner.</summary>
        public static void Use(LogicOwner owner, PresetInfo preset) => owner.Edit($"Add {preset.Name}", node =>
        {
            var entry = new JObject { ["preset"] = preset.Id };
            var with = new JObject();
            foreach (var p in preset.Params.Where(p => p.Required && p.Default == null)) with[p.Name] = "";
            if (with.Properties().Any()) entry["with"] = with;
            LogicDoc.EnsureRules(node).Add(entry);
        });

        /// <summary>"Save as preset": asks for a name, then writes a pack preset holding the rule.</summary>
        public static void SaveRule(LogicOwner owner, int index)
        {
            var rule = owner.Rules()?[index] as JObject;
            if (rule == null) return;
            ModPopup.Open("Save as preset", page =>
            {
                page.Text("A preset is a rule you can add to anything from \"+ preset\". It's saved in this pack.");
                page.TextInput("Name", () => "", name =>
                {
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var draft = Studio.Draft ?? Enemies.EnemyStudio.Draft;
                    if (draft == null) return;
                    draft.Edit($"Save preset {name}", pack =>
                    {
                        var list = LogicDoc.Get(pack, "presets") as JArray ?? new JArray();
                        var taken = new HashSet<string>(list.OfType<JObject>().Select(o => (string)o["id"]).Where(x => x != null));
                        string id = LogicDoc.NameFor(name, taken).ToLowerInvariant();
                        var copy = (JObject)rule.DeepClone();
                        var preset = new JObject { ["id"] = id, ["name"] = name.Trim(), ["rules"] = new JArray(copy) };
                        if (owner.Read()?["vars"] is JObject vars) preset["vars"] = vars.DeepClone();
                        list.Add(preset);
                        LogicDoc.Set(pack, "presets", list);
                    }, structural: true);
                    Notify.Show($"Saved preset \"{name.Trim()}\"", NotifyKind.Info, 2f, "logic");
                    ModPopup.Current?.Close();
                }, () => "What it's called in the preset list.");
            });
        }
    }
}
