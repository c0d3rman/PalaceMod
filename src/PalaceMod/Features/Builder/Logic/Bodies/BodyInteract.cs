using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `interact` (docs/builder-engine.md, "Components"): the king's interact key, as with the game's buildings. The
    prompt shows in the game's own tooltip (TooltipManager reads the focused interactor's ReturnTooltip); a `cost` is
    paid coin by coin while the key is held, on a copy of the game's coin-slot display (CostDisplay), and `interact`
    (other = the king) fires once it's full. `when`: day | night | always. `repeat: false` makes it single-use.
    `cost` and `prompt` are re-evaluated whenever the king focuses it ("=expr"; {expr} in the prompt).
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
        private bool used, focused, holding, paid;
        private int cost;
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
            string p = (string)Def["prompt"] ?? (cost > 0 ? "Pay {cost}" : "Interact");
            prompt = EvalText(p.Replace("{cost}", cost.ToString()), Entity);
        }

        public override void Focus(PlayerInteraction player)
        {
            focused = true;
            Refresh();
            if (cost > 0 && CanBeInteractedWith)
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
            if (cost <= 0) { Fire(player); return; }
            holding = true;
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
