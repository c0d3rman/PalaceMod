using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `interact` (docs/builder-engine.md, "Components"): the king's interact key, as with the game's buildings. The
    prompt shows in the game's own tooltip (TooltipManager reads the focused interactor's ReturnTooltip); a `cost` is
    paid coin by coin while the key is held, on a copy of the game's coin-slot display (CostDisplay), and `interact`
    (other = the king) fires once it's full. `when`: day | night | always. `repeat: false` makes it single-use.
    `cost` and `prompt` are re-evaluated whenever the king focuses it ("=expr"; {expr} in the prompt).
    `pay` (on the block or a choice): [{ "var": "$bread", "amount": 1, "label": "bread" }]: level variables paid as well
    as (or instead of) gold, all at once on the press; refused with a notice (and `interactRefused`) when any is
    short. {cost} in the prompt then reads "2 gold + 1 bread". Units carry the block too (LogicLinksManager).
    Inputs: lock, unlock.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class BodyInteract
    {
        public static BuilderInteractor Add(GameObject go, LogicEntity e, JObject def)
        {
            // The interactor lives on its own child on the game's interactor layer, so the body's layer is untouched.
            var holder = new GameObject("PalaceBuilder Interactor");
            holder.transform.SetParent(go.transform, false);
            int layer = Layer();
            if (layer >= 0) holder.layer = layer;
            var i = holder.AddComponent<BuilderInteractor>();
            i.Entity = e;
            i.Def = def;
            var col = holder.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = def.Value<float?>("radius") ?? 3f;
            return i;
        }

        private static int Layer()
        {
            var pi = PlayerInteraction.instance;
            if (pi == null) return -1;
            int mask = pi.interactorLayer.value;
            for (int l = 0; l < 32; l++) if ((mask & (1 << l)) != 0) return l;
            return -1;
        }
    }

    internal sealed class BuilderInteractor : InteractorBase
    {
        public LogicEntity Entity;
        public JObject Def;
        public bool Locked;
        public bool Used { get => used; set => used = value; }
        private bool used, focused, holding, paid;
        private int cost;
        private List<PayItem> pays = [];
        private string prompt;
        private CostDisplay display;

        public static string EvalText(string text, LogicEntity self)
        {
            try { return LogicExpr.Text(text)(LogicExpr.Context(self)); }
            catch (LogicError e) { return text + " (" + e.Message + ")"; }
        }

        public static double? EvalNumber(string text, LogicEntity self)
        {
            try { var v = LogicExpr.Compile(text)(LogicExpr.Context(self)); return v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num : null; }
            catch (LogicError) { return null; }
        }

        private string When => (string)Def["when"] ?? "always";

        /// <summary>One `pay` item, evaluated: a level variable and how much of it.</summary>
        public readonly struct PayItem
        {
            public readonly string Var, Label;
            public readonly double Amount;
            public PayItem(string v, double a, string l) { Var = v; Amount = a; Label = l; }
        }

        public List<PayItem> Pays(JToken pay)
        {
            var list = new List<PayItem>();
            if (pay is not JArray a) return list;
            foreach (var t in a)
            {
                if (t is not JObject o || (string)o["var"] is not { Length: > 1 } name || !name.StartsWith("$")) continue;
                var amt = o["amount"];
                double n = amt == null ? 1 : amt.Type is JTokenType.Integer or JTokenType.Float ? (double)amt : amt.Type == JTokenType.String ? EvalNumber((string)amt, Entity) ?? 0 : 0;
                if (n <= 0) continue;
                list.Add(new PayItem(name, n, (string)o["label"] ?? name.Substring(1)));
            }
            return list;
        }

        private static double Have(string var)
        {
            var w = LogicWorld.Current;
            if (w == null) return 0;
            var v = w.GetVar(w.Level, var);
            return v.Kind is ValueKind.Number or ValueKind.Bool ? v.Num : 0;
        }

        /// <summary>"2 gold + 1 bread"; with gold only, the number alone (as {cost} always read).</summary>
        public static string CostText(int gold, List<PayItem> pays, bool goldWord = false)
        {
            if (pays == null || pays.Count == 0) return goldWord ? $"{gold} gold" : gold.ToString();
            var parts = new List<string>();
            if (gold > 0) parts.Add($"{gold} gold");
            foreach (var p in pays) parts.Add($"{p.Amount:0.##} {p.Label}");
            return string.Join(" + ", parts);
        }

        /// <summary>Pay gold and items at once; false (with a notice) if anything is short.</summary>
        private bool PayAll(PlayerInteraction player, int gold, List<PayItem> pays)
        {
            var shortOf = new List<string>();
            if (gold > 0 && player.Balance < gold) shortOf.Add($"{gold} gold");
            foreach (var p in pays) if (Have(p.Var) < p.Amount - 1e-9) shortOf.Add($"{p.Amount:0.##} {p.Label}");
            if (shortOf.Count > 0)
            {
                Notify.Show($"Not enough: {string.Join(", ", shortOf)} needed", NotifyKind.Warning, 3f, "builder-interact");
                BodyDev.Raise("interactRefused", Entity, LogicGame.EntityOf(player.gameObject), gold, transform.position.ToVec(), string.Join(", ", shortOf));
                return false;
            }
            if (gold > 0) player.SpendCoins(gold);
            var w = LogicWorld.Current;
            foreach (var p in pays) w?.SetVar(w.Level, p.Var, Value.Number(Have(p.Var) - p.Amount));
            return true;
        }

        public int Price(JToken c)
        {
            if (c == null) return cost;
            if (c.Type is JTokenType.Integer or JTokenType.Float) return Mathf.Max(0, (int)c);
            if (c.Type == JTokenType.String) return Mathf.Max(0, (int)(EvalNumber((string)c, Entity) ?? 0));
            return cost;
        }

        public override bool CanBeInteractedWith
        {
            get
            {
                if (Locked || Entity == null || !Entity.Enabled || (used && !(Def.Value<bool?>("repeat") ?? true))) return false;
                bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
                return When switch { "day" => !night, "night" => night, _ => true };
            }
        }

        public override string ReturnTooltip() => CanBeInteractedWith ? prompt ?? "" : "";

        private void Refresh()
        {
            var c = Def["cost"];
            cost = 0;
            if (c != null && c.Type is JTokenType.Integer or JTokenType.Float) cost = Mathf.Max(0, (int)c);
            else if (c?.Type == JTokenType.String) cost = Mathf.Max(0, (int)(EvalNumber((string)c, Entity) ?? 0));
            pays = Pays(Def["pay"]);
            string p = (string)Def["prompt"] ?? (cost > 0 || pays.Count > 0 ? "Pay {cost}" : "Interact");
            prompt = EvalText(p.Replace("{cost}", CostText(cost, pays)), Entity);
        }

        public override void Focus(PlayerInteraction player)
        {
            focused = true;
            Refresh();
            if (cost > 0 && pays.Count == 0 && CanBeInteractedWith)
            {
                display ??= MakeDisplay();
                if (display != null) { display.gameObject.SetActive(true); display.UpdateDisplay(cost); }
            }
        }

        public override void Unfocus(PlayerInteraction player)
        {
            focused = false;
            holding = false;
            if (display != null)
            {
                if (!display.CompletelyEmpty && !paid) display.CancelFill(player);
                display.Hide();
            }
            paid = false;
        }

        public override void InteractionBegin(PlayerInteraction player)
        {
            if (!CanBeInteractedWith) return;
            Refresh();
            paid = false;
            if (Def["choices"] is JArray choices && choices.Count > 0) { BodyChoices.Present(this, choices, player); return; }
            if (pays.Count > 0) { if (PayAll(player, cost, pays)) Fire(player); return; }
            if (cost <= 0) { Fire(player); return; }
            holding = true;
        }

        /// <summary>A choice was picked in the game's choice popup: pay its cost (all at once), then fire.</summary>
        public void Chosen(PlayerInteraction player, string id, int price, List<PayItem> choicePays = null)
        {
            if (!PayAll(player, price, choicePays ?? [])) return;
            used = true;
            BodyDev.Raise("interact", Entity, LogicGame.EntityOf(player.gameObject), price, transform.position.ToVec(), id);
            if (focused) Refresh();
        }

        public override void InteractionHold(PlayerInteraction player)
        {
            if (!holding || paid || display == null) return;
            if (display.CompletelyFilled)
            {
                paid = true;
                holding = false;
                display.OnCompletion();
                Fire(player);
            }
            else if (player.Balance > 0)
            {
                if (display.FillUp()) player.SpendCoins(1);
            }
            else display.Deny();
        }

        public override void InteractionEnd(PlayerInteraction player)
        {
            if (holding && !paid && display != null) display.CancelFill(player);
            holding = false;
        }

        private void Fire(PlayerInteraction player)
        {
            used = true;
            BodyDev.Raise("interact", Entity, LogicGame.EntityOf(player.gameObject), cost, transform.position.ToVec());
            if (focused) Refresh();
        }

        /// <summary>A copy of a build slot's coin-slot display, at the same height above this body.</summary>
        private CostDisplay MakeDisplay()
        {
            var source = FindObjectsOfType<BuildingInteractor>().Select(b => b.costDisplay).FirstOrDefault(d => d != null);
            if (source == null) return null;
            var offset = source.transform.position - source.GetComponentInParent<BuildingInteractor>().transform.position;
            var copy = Instantiate(source.gameObject, transform);
            copy.transform.position = transform.position + offset;
            copy.SetActive(false);
            return copy.GetComponent<CostDisplay>();
        }

        private void Update()
        {
            if (!focused && display != null && display.gameObject.activeSelf) display.Hide();
        }
    }
}
