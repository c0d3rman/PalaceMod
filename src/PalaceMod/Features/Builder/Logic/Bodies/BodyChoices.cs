using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `interact.choices` (docs/builder-engine.md, "interact"): { id, label, cost, description, icon, if } shown in the
    game's own upgrade-choice popup (ChoiceManager), up to 4 at once. The list is built on every press: `interact.offer`
    (an expression giving a list of choice ids) picks which and in what order, and a choice's `if` hides it while
    false, so one interactable can offer a different (e.g. random daily) handful of a longer pool. The popup reads each choice's name and description through a
    build slot's localisation ids; while ours are up, those ids are "PalaceBuilder/choice/<n>" and UnitFactory's text
    table answers them. The picked choice's cost (it replaces the top-level one; may be "=expr") is paid at once, then
    `interact` fires with event.choice = its id. Esc cancels without paying.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyChoices
    {
        private static readonly Dictionary<Choice, string> ours = [];
        private static int serial;

        public static bool Present(BuilderInteractor owner, JArray defs, PlayerInteraction player)
        {
            var manager = ChoiceManager.instance;
            var origin = Object.FindObjectOfType<BuildSlot>();
            if (manager == null || origin == null || manager.ChoiceCoroutineRunning) return false;
            ours.Clear();
            var list = new List<Choice>();
            var meta = new Dictionary<Choice, (string id, int price, List<BuilderInteractor.PayItem> pays)>();
            foreach (var c in Offered(owner, defs))
            {
                if (list.Count >= 4) break;
                string id = (string)c["id"] ?? $"choice{list.Count + 1}";
                int price = owner.Price(c["cost"]);
                var pays = owner.Pays(c["pay"]);
                var choice = new Choice(id, (string)c["description"] ?? "", Icon(c));
                string key = UnitFactory.LocaPrefix + "choice/" + (++serial);
                ours[choice] = key;
                string label = BuilderInteractor.EvalText((string)c["label"] ?? id, owner.Entity);
                UnitFactory.Texts[key] = price > 0 || pays.Count > 0 ? $"{label} ({BuilderInteractor.CostText(price, pays, goldWord: true)})" : label;
                UnitFactory.Texts[key + " Description"] = BuilderInteractor.EvalText((string)c["description"] ?? "", owner.Entity);
                list.Add(choice);
                meta[choice] = (id, price, pays);
            }
            if (list.Count == 0)
            {
                Notify.Show("Nothing to choose right now", NotifyKind.Info, 3f, "builder-interact");
                return false;
            }
            shown = list.Select(c => meta[c].id).ToList();
            manager.PresentChoices(list, origin, picked =>
            {
                if (picked != null && meta.TryGetValue(picked, out var m) && owner != null) owner.Chosen(player, m.id, m.price, m.pays);
                ours.Clear();
            });
            return true;
        }

        /// <summary>The choices on offer right now, in order: `offer`'s ids (unknown ids skipped) or every choice, minus
        /// those whose `if` is false.</summary>
        public static List<JObject> Offered(BuilderInteractor owner, JArray defs)
        {
            var all = defs.OfType<JObject>().ToList();
            var result = all;
            if ((string)owner.Def["offer"] is { Length: > 0 } offer)
            {
                result = [];
                Value v;
                try { v = LogicExpr.Compile(offer)(LogicExpr.Context(owner.Entity)); }
                catch (LogicError e)
                {
                    Log.For<LogicBodiesManager>().Warn($"interact.offer \"{offer}\" on {owner.Entity?.Id}: {e.Message}");
                    v = Value.List([]);
                }
                var ids = v.Kind == ValueKind.List ? v.AsList.Select(Values.Format) : v.Kind == ValueKind.Text ? [v.Str] : [];
                foreach (string id in ids)
                {
                    var c = all.Find(x => ((string)x["id"] ?? "") == id);
                    if (c != null && !result.Contains(c)) result.Add(c);
                }
            }
            return result.FindAll(c => (string)c["if"] is not { Length: > 0 } cond || (BuilderInteractor.EvalNumber(cond, owner.Entity) ?? 0) != 0);
        }

        private static Sprite Icon(JObject c)
        {
            string icon = (string)c["icon"];
            if (icon == null) return null;
            var entry = EntityFactory.PackEntry;
            return entry != null ? BuilderServices.Assets.GetSprite(entry, icon) : null;
        }

        private static List<string> shown = [];

        /// <summary>Dev: the ids of our open popup's choices, in order.</summary>
        public static string Shown()
        {
            var manager = ChoiceManager.instance;
            if (manager == null || !manager.ChoiceCoroutineRunning || ours.Count == 0) return "no choice popup";
            return string.Join(" | ", shown);
        }

        /// <summary>Dev: pick choice n (0-based) in the open popup.</summary>
        public static string Pick(int n)
        {
            var manager = ChoiceManager.instance;
            if (manager == null || !manager.ChoiceCoroutineRunning) return "no choice popup";
            if (n < 0 || n >= manager.availableChoices.Count) return $"only {manager.availableChoices.Count} choices";
            manager.choiceToReturn = manager.availableChoices[n];
            return "picked " + manager.choiceToReturn.name;
        }

        /// <summary>The localisation id of one of our choices (BuildSlot.GET_LOCIDENTIFIER_CHOICENAME prefix).</summary>
        public static bool TryKey(Choice choice, out string key) { key = null; return choice != null && ours.TryGetValue(choice, out key); }
    }
}
