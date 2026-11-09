using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;

/*
    Presets in the Studio (docs/builder-engine.md, "Presets"), on the engine's registry (Model/Logic/Presets): the
    "+ preset" picker (the pack's own, the library's, the built-in ones), a preset entry's card and fields, "Unpack" back
    to plain rules, "Save as preset", and the Logic palette's objects (built-in presets of category "Logic"), which are
    placed unpacked, as plain rules and components, so their cards read and edit like any other.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class PresetInfo
    {
        public PresetDef Def;
        public bool BuiltIn;
        public string Id => Def.Id;
        public string Name => Def.Name ?? Def.Id;
        public List<ParamUi> Params = [];

        public string Describe(JObject with) => Presets.Describe(Def, with) ?? "";
    }

    internal static class PresetUi
    {
        private static readonly JsonSerializer Json = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private static DesignPack Pack => Studio.Draft?.Pack ?? Enemies.EnemyStudio.Draft?.Pack;

        public static PresetInfo Info(PresetDef d, bool builtIn)
        {
            var info = new PresetInfo { Def = d, BuiltIn = builtIn };
            foreach (var kv in d.Params ?? [])
            {
                var p = new ParamUi
                {
                    Name = kv.Key, Label = LogicCatalog.Humanize(kv.Key), Kind = LogicSchemaBridge.KindOf(kv.Value?.Type ?? "number", kv.Key),
                    Default = kv.Value?.Default == null ? null : LogicFields.Raw(kv.Value.Default), Options = kv.Value?.Options?.ToArray() ?? [],
                    Desc = kv.Value?.Description, Inline = true,
                };
                info.Params.Add(p);
            }
            return info;
        }

        /// <summary>Presets a rule list can use: the pack's, then the built-in abilities (not the logic objects).</summary>
        public static IEnumerable<PresetInfo> ForRules()
        {
            var own = (Pack?.Presets ?? []).Where(p => p?.Id != null).Select(p => Info(p, false)).ToList();
            return own.Concat(Presets.BuiltIn.Where(b => b.Category != "Logic" && own.All(o => o.Id != b.Id)).Select(b => Info(b, true)));
        }

        public static PresetInfo Find(string id) => Presets.Find(Pack, id) is { } d ? Info(d, Presets.BuiltIn.Contains(d)) : null;

        /// <summary>The palette's logic objects.</summary>
        public static IEnumerable<PresetDef> LogicObjects() => Presets.BuiltIn.Where(p => p.Category == "Logic" && p.Kind != null);

        private static Dictionary<string, JToken> Values(PresetDef d, JObject with)
        {
            var values = new Dictionary<string, JToken>();
            foreach (var kv in d.Params ?? [])
            {
                var v = with?[kv.Key] ?? kv.Value?.Default;
                if (v != null) values[kv.Key] = v;
            }
            return values;
        }

        /// <summary>A logic object as a place: its kind, components, vars, labels and rules with the params' defaults.</summary>
        public static JObject AsPlace(PresetDef d)
        {
            var o = JObject.FromObject(d, Json);
            foreach (var f in new[] { "id", "name", "description", "category", "params" }) o.Remove(f);
            return (JObject)Presets.Subst(o, Values(d, null));
        }

        /// <summary>Replaces a preset entry with its rules (and its vars on the owner), one undo step.</summary>
        public static void Unpack(LogicOwner owner, int index)
        {
            var rule = owner.Rules()?[index] as JObject;
            var info = Find((string)rule?["preset"]);
            if (info == null) return;
            var expanded = (JArray)Presets.Subst(JArray.FromObject(info.Def.Rules ?? [], Json), Values(info.Def, rule["with"] as JObject));
            owner.Edit($"Unpack {info.Name}", node =>
            {
                var rules = LogicDoc.EnsureRules(node);
                rules.RemoveAt(index);
                for (int i = 0; i < expanded.Count; i++) rules.Insert(index + i, expanded[i].DeepClone());
                if (info.Def.Vars?.Count > 0)
                {
                    if (node["vars"] is not JObject vars) node["vars"] = vars = new JObject();
                    foreach (var kv in info.Def.Vars) vars[kv.Key] ??= kv.Value?.DeepClone();
                }
            });
        }

        /// <summary>Adds a preset entry to an owner.</summary>
        public static void Use(LogicOwner owner, PresetInfo preset) => owner.Edit($"Add {preset.Name}", node =>
            LogicDoc.EnsureRules(node).Add(new JObject { ["preset"] = preset.Id }));

        /// <summary>"Save as preset": asks for a name, then writes a pack preset holding the rule (and the owner's vars).</summary>
        public static void SaveRule(LogicOwner owner, int index)
        {
            var rule = owner.Rules()?[index] as JObject;
            if (rule == null) return;
            ModPopup.Open("Save as preset", page =>
            {
                page.Text("Reuse this rule on other things from \"+ preset\". It's kept in this pack.");
                page.TextInput("Name", () => "", name =>
                {
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var draft = Studio.Draft ?? Enemies.EnemyStudio.Draft;
                    if (draft == null) return;
                    var vars = owner.Read()?["vars"] as JObject;
                    draft.Edit($"Save preset {name.Trim()}", pack =>
                    {
                        pack.Presets ??= [];
                        string id = LogicDoc.NameFor(name, new HashSet<string>(pack.Presets.Select(p => p?.Id).Where(x => x != null))).ToLowerInvariant();
                        pack.Presets.Add(new PresetDef
                        {
                            Id = id, Name = name.Trim(), Category = "Mine",
                            Rules = [rule.ToObject<RuleDef>(Json)],
                            Vars = vars?.Properties().ToDictionary(p => p.Name, p => p.Value.DeepClone()),
                        });
                    }, structural: true);
                    Notify.Show($"Saved preset \"{name.Trim()}\"", NotifyKind.Info, 2f, "logic");
                    ModPopup.Current?.Close();
                }, () => "What it's called in the preset list.");
            });
        }
    }
}
