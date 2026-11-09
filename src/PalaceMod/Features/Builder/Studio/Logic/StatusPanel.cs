using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

/*
    Statuses (docs/builder-engine.md, "Statuses"): timed, stacking modifiers with their own rules: a buff, a debuff, an
    aura's mark, burning. The pack's statuses are listed in the level's Behaviour tab and in every status picker ("+ New
    status"); one opens in the Behaviour tab in place of the selection (LogicStudio.Focus), with a way back.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class StatusPanel
    {
        private static string Esc(string s) => LogicFields.Esc(s);
        private static string Dim(string s) => LogicFields.Dim(s);

        public static string NameOf(string id) => (string)LogicDoc.StatusNode(LogicOwner.DraftPack, id)?["name"] ?? id;

        /// <summary>Adds a status to the draft (one undo step) and returns its id.</summary>
        public static string New(string name = null)
        {
            var draft = LogicOwner.Draft;
            if (draft == null) return null;
            string id = null;
            draft.Edit("New status", p =>
            {
                p.Statuses ??= [];
                var taken = new System.Collections.Generic.HashSet<string>(p.Statuses.Select(s => s.Id).Where(x => x != null));
                id = LogicDoc.NameFor(name ?? "status", taken);
                p.Statuses.Add(new PalaceMod.Builder.Model.Logic.StatusDef { Id = id, Name = name ?? "New status", Duration = 5 });
            }, structural: true);
            LogicStudio.Changed();
            return id;
        }

        /// <summary>The pack's statuses to pick from, plus a new one.</summary>
        public static void Picker(ModPage page, Action<string> picked)
        {
            var list = LogicOwner.DraftPack?.Statuses ?? [];
            if (list.Count == 0) page.Text(Dim("No statuses yet."));
            foreach (var s in list.Where(s => s?.Id != null))
            {
                string id = s.Id;
                StudioDock.DetailRow(page, Esc(s.Name ?? id), Esc(Describe(LogicDoc.StatusNode(LogicOwner.DraftPack, id))), false, null, () => { picked(id); RuleEditor.ClosePicker(); });
            }
            page.TextInput("+ New status", () => "", v =>
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                string id = New(v.Trim());
                if (id == null) return;
                picked(id);
                RuleEditor.ClosePicker();
                LogicStudio.FocusOn(LogicOwner.Status(id));
            }, () => "A name (Burning, Warded…). It opens in the Behaviour tab to set what it does.", 40, () => Dim("name…"));
        }

        /// <summary>"5 s · damage taken ×0.7 · stunned".</summary>
        public static string Describe(JObject s)
        {
            if (s == null) return "";
            var parts = new System.Collections.Generic.List<string>();
            if (s["duration"] != null) parts.Add(LogicFields.Raw(s["duration"]) + " s");
            foreach (var m in (s["mods"] as JObject)?.Properties() ?? [])
            {
                var o = m.Value as JObject;
                string how = o?["mult"] != null ? "×" + LogicFields.Raw(o["mult"]) : o?["add"] != null ? "+" + LogicFields.Raw(o["add"]) : o?["value"] != null ? "= " + LogicFields.Raw(o["value"]) : "";
                parts.Add($"{LogicCatalog.Humanize(m.Name).ToLowerInvariant()} {how}".Trim());
            }
            foreach (var f in (s["flags"] as JArray)?.Select(t => (string)t) ?? []) parts.Add(f);
            int rules = (s["rules"] as JArray)?.Count ?? 0;
            if (rules > 0) parts.Add(rules == 1 ? "1 rule" : $"{rules} rules");
            return string.Join(" · ", parts);
        }

        private static ParamUi N(string name, string label, string desc, string def = null) => new() { Name = name, Label = label, Kind = ParamKind.Number, Desc = desc, Default = def };

        /// <summary>A status's fields and rules (the Behaviour tab while it's focused).</summary>
        public static void Build(ModPage page, LogicOwner owner)
        {
            var node = owner.Read();
            if (node == null) { page.Text(Dim("That status is gone.")); return; }
            var scope = owner.Scope(null);
            page.TextInput("Name", () => (string)owner.Read()?["name"] ?? "", v => owner.Edit("Rename status", n => { if (string.IsNullOrWhiteSpace(v)) n.Remove("name"); else n["name"] = v.Trim(); }),
                () => $"Its name (rules use its id, {owner.Id}).", 60);
            LogicFields.NumberField(page, "Lasts (s)", N("duration", "Lasts", ""), () => owner.Read()?["duration"],
                (v, l) => owner.Edit(l, n => { if (v == null) n.Remove("duration"); else n["duration"] = v; }, "duration"), scope, "Seconds it lasts. Empty: until removed (or the applier's \"for\").");
            string[] stacking = ["refresh", "add", "extend", "ignore"];
            page.Choice("Again", ["Restarts", "Stacks", "Lasts longer", "Ignored"], () => Math.Max(0, Array.IndexOf(stacking, (string)owner.Read()?["stacking"] ?? "refresh")),
                i => owner.Edit("Set stacking", n => { if (i == 0) n.Remove("stacking"); else n["stacking"] = stacking[i]; }), tooltip: () => "What applying it again does.");
            if ((string)node["stacking"] == "add")
                LogicFields.NumberField(page, "   Most stacks", N("maxStacks", "Most stacks", ""), () => owner.Read()?["maxStacks"],
                    (v, l) => owner.Edit(l, n => { if (v == null) n.Remove("maxStacks"); else n["maxStacks"] = v; }, "maxStacks"), scope, "Stacks stop adding past this.");
            LogicFields.NumberField(page, "Ticks every (s)", N("tick", "Tick", ""), () => owner.Read()?["tick"],
                (v, l) => owner.Edit(l, n => { if (v == null) n.Remove("tick"); else n["tick"] = v; }, "tick"), scope, "Seconds between its \"status ticks\" events (burning damage). Empty: none.");

            StudioDock.SmallHeader(page, "Changes");
            var mods = node["mods"] as JObject;
            if (mods == null || !mods.Properties().Any()) page.Text(Dim("Nothing yet."));
            foreach (var m in mods?.Properties().ToList() ?? [])
            {
                string stat = m.Name;
                var o = m.Value as JObject;
                string mode = o?["add"] != null ? "add" : o?["value"] != null ? "value" : "mult";
                string[] modes = ["mult", "add", "value"];
                page.Choice(LogicCatalog.Humanize(stat), ["Times", "Plus", "Set to"], () => Array.IndexOf(modes, mode), i => owner.Edit("Set change", n =>
                {
                    var mo = (JObject)n["mods"][stat];
                    var val = mo[mode] ?? (i == 0 ? 1 : 0);
                    mo.Remove(mode);
                    mo[modes[i]] = val;
                }), tooltip: () => LogicCatalog.StatDescriptions.TryGetValue(stat, out var d) ? d : stat);
                LogicFields.NumberField(page, "   by", N(mode, "Amount", "", mode == "mult" ? "1" : "0"), () => owner.Read()?["mods"]?[stat]?[mode],
                    (v, l) => owner.Edit(l, n => ((JObject)n["mods"][stat])[mode] = v ?? (mode == "mult" ? 1 : 0), "mod." + stat), scope, "A number or a formula (stacks: self.stacks('" + owner.Id + "')).");
                if (stat is "damageTaken" or "damageMult")
                    LogicFields.ExprField(page, "   only from", new ParamUi { Name = "from", Label = "From", Kind = ParamKind.Filter }, () => owner.Read()?["mods"]?[stat]?["from"],
                        (v, l) => owner.Edit(l, n => { if (v == null) ((JObject)n["mods"][stat]).Remove("from"); else ((JObject)n["mods"][stat])["from"] = v; }), scope, "Only damage from these (a filter); empty: all.");
                LogicFields.Action(page, Dim("   Remove"), () => owner.Edit("Remove change", n => (n["mods"] as JObject)?.Remove(stat)));
            }
            LogicFields.Buttons(page, ("+ change", () => LogicStudio.OpenSide("Change…", p => Pickers.Stat(p, v => owner.Edit("Add change", n =>
            {
                if (n["mods"] is not JObject mo) n["mods"] = mo = new JObject();
                mo[(string)v] ??= new JObject { ["mult"] = 1 };
            }))), "A stat it changes while it lasts."));

            StudioDock.SmallHeader(page, "While it lasts it's…");
            foreach (var f in LogicCatalog.Flags)
            {
                string flag = f;
                page.Toggle(LogicCatalog.Humanize(flag), () => (owner.Read()?["flags"] as JArray)?.Any(t => (string)t == flag) == true, on => owner.Edit(on ? $"Add {flag}" : $"Remove {flag}", n =>
                {
                    if (n["flags"] is not JArray a) n["flags"] = a = new JArray();
                    foreach (var t in a.Where(t => (string)t == flag).ToList()) t.Remove();
                    if (on) a.Add(flag);
                }));
            }

            StudioDock.SmallHeader(page, "Look");
            LogicFields.ExprField(page, "Tint", new ParamUi { Name = "tint", Label = "Tint", Kind = ParamKind.Text }, () => owner.Read()?["visual"]?["tint"],
                (v, l) => owner.Edit(l, n => { if (n["visual"] is not JObject vis) n["visual"] = vis = new JObject(); if (v == null) vis.Remove("tint"); else vis["tint"] = v; }), scope, "Tints whoever has it, #rrggbb.");
            LogicFields.ExprField(page, "Effect", new ParamUi { Name = "fx", Label = "Effect", Kind = ParamKind.Text }, () => owner.Read()?["visual"]?["fx"],
                (v, l) => owner.Edit(l, n => { if (n["visual"] is not JObject vis) n["visual"] = vis = new JObject(); if (v == null) vis.Remove("fx"); else vis["fx"] = v; }), scope, "An effect shown on it (fire_small, ward…).");

            BehaviourPanel.Rules(page, owner, "Rules (self: who has it)");
        }
    }
}
