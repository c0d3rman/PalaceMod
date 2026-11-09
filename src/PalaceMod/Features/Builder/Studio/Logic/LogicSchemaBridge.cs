using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model.Logic;

/*
    Merges the engine's schema registry (Model/Logic/LogicSchema) into the Studio's catalog: the registry's params
    (names, types, required, defaults, options, descriptions) replace the catalog's, which keeps only its phrasing,
    categories and which params a card shows inline. An event or action only the registry has gets a plain card.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class LogicSchemaBridge
    {
        private static bool merged;

        public static ParamKind KindOf(string type, string name) => type switch
        {
            "number" or "int" or "night" => ParamKind.Number,
            "for" => ParamKind.Number,
            "bool" => ParamKind.Bool,
            "text" or "label" or "color" => ParamKind.Text,
            "position" or "offset" => ParamKind.Position,
            "entity" or "slot" => ParamKind.Entity,
            "entities" => ParamKind.Target,
            "template" => ParamKind.Template,
            "filter" => ParamKind.Filter,
            "status" => ParamKind.Status,
            "rule" => ParamKind.Rule,
            "var" => ParamKind.Var,
            "signal" => ParamKind.Signal,
            "enum" => ParamKind.Choice,
            "actions" => ParamKind.Actions,
            "any" => ParamKind.Expr,
            "fx" => ParamKind.Fx,
            "sound" => ParamKind.Sound,
            "types" or "keep" => ParamKind.Labels,
            _ when name == "stat" => ParamKind.Stat,
            _ => ParamKind.Json,
        };

        private static List<ParamUi> Params(IEnumerable<ParamSpec> specs, List<ParamUi> old)
        {
            var list = new List<ParamUi>();
            foreach (var s in specs)
            {
                var was = old.FirstOrDefault(p => p.Name == s.Name);
                var kind = s.Name == "stat" ? ParamKind.Stat : s.Name == "cond" ? ParamKind.Condition : KindOf(s.Type, s.Name);
                // "for" also takes "night"/"day": a number field that accepts them as text.
                list.Add(new ParamUi
                {
                    Name = s.Name,
                    Label = was?.Label ?? LogicCatalog.Humanize(s.Name),
                    Kind = kind,
                    Default = s.Default == null ? was?.Default : LogicFields.Raw(s.Default),
                    Options = s.Options ?? was?.Options ?? [],
                    Required = s.Required,
                    Inline = was?.Inline ?? s.Required,
                    Desc = s.Description ?? was?.Desc,
                });
            }
            return list;
        }

        public static void Merge()
        {
            if (merged) return;
            merged = true;
            foreach (var e in LogicSchema.Events.Values)
            {
                var ui = LogicCatalog.Event(e.Name);
                if (ui == null) { LogicCatalog.AddUnknownEvent(e.Name, e.Description, Params(e.Params, [])); continue; }
                ui.Params = Params(e.Params.Where(p => p.Name is not "of" and not "range"), ui.Params);
                ui.Other ??= e.Other;
                ui.HasPos |= e.HasPos;
                if (string.IsNullOrEmpty(ui.Desc)) ui.Desc = e.Description;
            }
            foreach (var a in LogicSchema.Actions.Values)
            {
                var ui = LogicCatalog.Action(a.Name);
                var ps = Params(a.Params, ui?.Params ?? []);
                if (a.Targeted && ps.All(p => p.Name != "target"))
                    ps.Insert(0, ui?.Params.FirstOrDefault(p => p.Name == "target") ?? new ParamUi { Name = "target", Label = "Target", Kind = ParamKind.Target, Default = "self", Inline = true });
                if (ui == null) { LogicCatalog.AddUnknownAction(a.Name, a.Description, ps); ui = LogicCatalog.Action(a.Name); }
                else ui.Params = ps;
                ui.Nested = ps.Where(p => p.Kind == ParamKind.Actions).Select(p => p.Name).ToArray();
                ui.Params = ps;
                if (string.IsNullOrEmpty(ui.Desc)) ui.Desc = a.Description;
            }
        }
    }
}
