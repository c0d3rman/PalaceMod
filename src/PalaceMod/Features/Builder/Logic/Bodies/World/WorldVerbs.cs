using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    World actions (docs/builder-engine.md, "Actions", World): gold, dropCoins, building, endLevel. Each goes through the
    game's own code path where there is one: PlayerInteraction.AddCoin/SpendCoins, the game's coin prefabs (income
    coins fly to the king; dropped coins wait to be collected), BuildSlot.OnUpgradeChoiceComplete with a chosen branch
    (no popup), Hp.TakeDamage/Revive for knock-outs and repairs, LocalGamestate.SetState for the result.
    Waves and spawnGroup are in WaveVerbs.cs.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class WorldVerbs
    {
        private static readonly AccessTools.FieldRef<BuildSlot, int> SlotLevel = AccessTools.FieldRefAccess<BuildSlot, int>("level");
        private static readonly AccessTools.FieldRef<BuildSlot, List<int>> SlotApplied = AccessTools.FieldRefAccess<BuildSlot, List<int>>("appliedUpgrades");
        private static readonly AccessTools.FieldRef<BuildSlot, BuildSlot.Upgrade> UpgradeSelectedRef = AccessTools.FieldRefAccess<BuildSlot, BuildSlot.Upgrade>("upgradeSelected");
        private static readonly AccessTools.FieldRef<BuildSlot, PlayerInteraction> PlayerSelectedRef = AccessTools.FieldRefAccess<BuildSlot, PlayerInteraction>("playerInteractionSelected");
        private static readonly AccessTools.FieldRef<BuildSlot, MeshFilter> MainMeshRef = AccessTools.FieldRefAccess<BuildSlot, MeshFilter>("mainMesh");
        private static readonly AccessTools.FieldRef<BuildSlot, bool> SlotActivated = AccessTools.FieldRefAccess<BuildSlot, bool>("gotActivated");
        private static readonly System.Reflection.MethodInfo SetBuildingState = AccessTools.Method(typeof(BuildSlot), "ActivateOrDeactivateBuliding");

        /// <summary>Slots locked by `building lock`; those we hid (unbuilt) remember that.</summary>
        internal static readonly HashSet<BuildSlot> Locked = [];
        private static readonly HashSet<BuildSlot> hidden = [];
        /// <summary>`setIncome` values, re-applied after a savestate load (the game recomputes income from upgrades).</summary>
        private static readonly Dictionary<BuildSlot, int> incomes = [];
        private static bool ending;

        public static void Register()
        {
            LogicActions.Register("gold", Gold);
            LogicActions.Register("dropCoins", DropCoins);
            LogicActions.Register("building", Building, ActionFlags.PerTarget);
            LogicActions.Register("endLevel", EndLevel);
            WorldSession.Reset += () => { Locked.Clear(); hidden.Clear(); incomes.Clear(); ending = false; };
        }

        // ------------------------------------------------------------------ gold and coins

        private static void Gold(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("gold");
            int amount = (int)Math.Round(p.Number("amount"));
            var player = PlayerInteraction.instance;
            if (amount < 0)
            {
                int take = Math.Min(player.Balance, -amount);
                if (take > 0) player.SpendCoins(take);
                return;
            }
            if (amount == 0) return;
            Vector3? at = p.Has("at") ? WorldCommon.At(ctx, p) : null;
            var coin = at == null ? null : IncomeCoin();
            if (coin == null) { player.AddCoin(amount); return; }
            // The income coins: they rise where the gold comes from and fly to the king, paying 1 each on arrival.
            const int MaxFlying = 40;
            int flying = Math.Min(amount, MaxFlying);
            if (amount > flying) player.AddCoin(amount - flying);
            Plugin.Instance.StartCoroutine(FlyCoins(coin, at.Value, flying, player));
        }

        private static IEnumerator FlyCoins(GameObject coin, Vector3 at, int count, PlayerInteraction player)
        {
            for (int i = 0; i < count; i++)
            {
                if (player == null || !WorldCommon.InMatch) yield break;
                var go = Object.Instantiate(coin, at, Quaternion.identity);
                go.GetComponent<Coin>()?.SetTarget(player);
                yield return new WaitForSeconds(0.06f);
            }
        }

        /// <summary>The coin a building's income spawns (it flies to the king and pays on arrival).</summary>
        private static GameObject IncomeCoin() =>
            Resources.FindObjectsOfTypeAll<CoinSpawner>().Select(c => c.coinPrefab).FirstOrDefault(c => c != null && c.GetComponent<Coin>() != null);

        /// <summary>The coin a dead enemy drops (it waits on the ground for the king).</summary>
        private static GameObject DropCoin() =>
            Resources.FindObjectsOfTypeAll<Hp>().Select(h => h.coin).FirstOrDefault(c => c != null && c.GetComponent<Coin>() != null);

        private static void DropCoins(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("dropCoins");
            int count = Mathf.Clamp((int)Math.Round(p.Number("count", 1)), 0, 100);
            var at = WorldCommon.At(ctx, p) ?? throw new LogicError("dropCoins: needs \"at\"");
            var coin = DropCoin() ?? throw new LogicError("dropCoins: the game's coin isn't loaded");
            float lift = Resources.FindObjectsOfTypeAll<Hp>().FirstOrDefault(h => h.coin == coin)?.coinSpawnOffset ?? 1f;
            // The same ring a dying enemy drops its coins in (Hp.TakeDamage).
            for (int i = 0; i < count; i++)
            {
                var pos = at + Vector3.up * lift + (i >= 1 ? Quaternion.Euler(0f, 45f * i, 0f) * Vector3.forward * 2f : Vector3.zero);
                Object.Instantiate(coin, pos, Quaternion.identity);
            }
        }

        // ------------------------------------------------------------------ buildings

        private static void Building(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("building");
            string op = p.Text("op") ?? throw new LogicError("building: needs \"op\"");
            var slot = ResolveSlot(ctx, p) ?? throw new LogicError("building: no build slot found for \"slot\"");
            switch (op)
            {
                case "build":
                    if (slot.Level > 0) return;
                    if (!slot.gameObject.activeSelf) slot.ActivateIgrnoringActivatorLevel();
                    Upgrade(ctx, p, slot);
                    break;
                case "upgrade":
                    Upgrade(ctx, p, slot);
                    break;
                case "knockOut":
                {
                    var hp = HpOf(slot);
                    if (hp == null || slot.Level == 0 || hp.KnockedOut) return;
                    bool inv = hp.invulnerable;
                    hp.invulnerable = false;
                    hp.TakeDamage(hp.HpValue + 1f);
                    if (hp != null) hp.invulnerable = inv;
                    break;
                }
                case "repair":
                {
                    var hp = HpOf(slot);
                    if (hp == null || slot.Level == 0) return;
                    hp.Revive(true, 1f);
                    hp.TaggedObj?.RemoveTag(TagManager.ETag.AUTO_NoReviveNextMorning);
                    RefreshInteractors(slot);
                    break;
                }
                case "demolish":
                {
                    if (slot.Level == 0) return;
                    Demolish(slot);
                    int refund = (int)Math.Round(p.Number("value"));
                    if (refund > 0) PlayerInteraction.instance.AddCoin(refund);
                    break;
                }
                case "lock":
                    Lock(slot, true);
                    break;
                case "unlock":
                    Lock(slot, false);
                    break;
                case "setCost":
                {
                    if (!p.Has("value")) throw new LogicError("building setCost: needs \"value\"");
                    var root = Root(slot);
                    if (root.Level >= root.OwnUpgrades.Count) return;   // fully upgraded: nothing to price
                    root.OwnUpgrades[root.Level].cost = Math.Max(0, (int)Math.Round(p.Number("value")));
                    RefreshInteractors(root);
                    break;
                }
                case "setIncome":
                {
                    if (!p.Has("value")) throw new LogicError("building setIncome: needs \"value\"");
                    var root = Root(slot);
                    root.GoldIncome = Math.Max(0, (int)Math.Round(p.Number("value")));
                    incomes[root] = root.GoldIncome;
                    RefreshInteractors(root);
                    break;
                }
                default:
                    throw new LogicError($"building: unknown op \"{op}\" (build, upgrade, knockOut, repair, demolish, lock, unlock, setCost, setIncome)");
            }
        }

        private static BuildSlot Root(BuildSlot slot) => slot.ActivatorUpgradesThis && slot.ActivatorBuilding != null ? slot.ActivatorBuilding : slot;

        private static Scene HostScene
        {
            get
            {
                var host = LayoutScene.Host(WorldCommon.Session?.HostScene);
                return host.IsValid() ? host : SceneManager.GetActiveScene();
            }
        }

        /// <summary>
        /// `slot`: a Studio key ("slot:Tower 3", "add:a1", "castle"), an attach/place name, a slot's object name, or a
        /// selector (a building entity). Without `slot`, the action's target (default self).
        /// </summary>
        internal static BuildSlot ResolveSlot(LogicContext ctx, ActionParams p)
        {
            LogicEntity entity = null;
            if (!p.Has("slot")) entity = p.Target ?? ctx.Self;
            else if (p.Raw("slot") is JValue { Type: JTokenType.String } v && !((string)v).StartsWith("="))
            {
                string s = (string)v;
                if (s == "castle") return LayoutScene.Castle(HostScene);
                if (s.StartsWith("slot:")) return LayoutScene.FindSlot(HostScene, s.Substring(5));
                if (s.StartsWith("add:")) return LayoutScene.FindSlot(HostScene, LayoutScene.AddedPrefix + s.Substring(4));
                entity = ctx.World?.FindNamed(s.TrimStart('@'));
                if (entity == null) return LayoutScene.FindSlot(HostScene, s);
            }
            else entity = p.Entity("slot");
            return SlotOf(entity);
        }

        internal static BuildSlot SlotOf(LogicEntity e)
        {
            var go = e?.Go();
            if (go == null) return null;
            var slot = go.GetComponentInParent<BuildSlot>(true) ?? go.GetComponentInChildren<BuildSlot>(true);
            return slot != null ? Root(slot) : null;
        }

        private static Hp HpOf(BuildSlot slot) =>
            (slot.BuildingParent != null ? slot.BuildingParent.GetComponentInChildren<Hp>(true) : null) ?? slot.GetComponentInChildren<Hp>(true);

        /// <summary>One level up, free, with the given branch (or one picked by the logic random source).</summary>
        private static void Upgrade(LogicContext ctx, ActionParams p, BuildSlot slot)
        {
            slot = Root(slot);
            if (!slot.CanBeUpgraded) return;
            if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) throw new LogicError("building: the player has a choice open");
            int count = slot.Upgrades[slot.Level].upgradeBranches.Count;
            int branch = p.Has("branch") ? (int)Math.Round(p.Number("branch")) : count > 1 ? (ctx.World?.Random.RangeInt(0, count - 1) ?? 0) : 0;
            if (branch < 0 || branch >= count) throw new LogicError($"building: branch must be 0-{count - 1}");
            UpgradeFree(slot, branch);
        }

        /// <summary>Builds or upgrades a slot one level for free with a chosen branch (no popup), like the player would.</summary>
        internal static void UpgradeFree(BuildSlot slot, int branch)
        {
            var upgrade = slot.Upgrades[slot.Level];
            var player = PlayerInteraction.instance;
            foreach (var child in slot.BuiltSlotsThatRelyOnThisBuilding.Where(c => c != null && c.ActivatorUpgradesThis && c.ActivatorBuilding == slot).ToList())
            {
                child.gameObject.SetActive(true);
                child.ExecuteBuildOrUpgrade(player, false);
            }
            UpgradeSelectedRef(slot) = upgrade;
            PlayerSelectedRef(slot) = player;
            slot.OnUpgradeChoiceComplete(upgrade.upgradeBranches[branch].choiceDetails);
            RefreshInteractors(slot);
        }

        /// <summary>
        /// Back to an empty slot: each applied upgrade's switched objects and hp change are undone in reverse, the
        /// building is hidden and the blueprint shown, and buildings upgraded with it go too. Effects an upgrade had
        /// outside the building (castle upgrades' perks) stay.
        /// </summary>
        private static void Demolish(BuildSlot slot)
        {
            foreach (var child in slot.BuiltSlotsThatRelyOnThisBuilding.Where(c => c != null && c.ActivatorUpgradesThis && c.ActivatorBuilding == slot && c.Level > 0).ToList())
                Demolish(child);
            var applied = SlotApplied(slot);
            var hp = HpOf(slot);
            Mesh firstMesh = null;
            for (int i = Math.Min(applied.Count, slot.OwnUpgrades.Count) - 1; i >= 0; i--)
            {
                var branches = slot.OwnUpgrades[i].upgradeBranches;
                if (applied[i] < 0 || applied[i] >= branches.Count) continue;
                var b = branches[applied[i]];
                foreach (var go in b.objectsToActivate) if (go != null) go.SetActive(false);
                foreach (var go in b.objectsToDisable) if (go != null) go.SetActive(true);
                if (hp != null) hp.maxHp -= b.hpChange;
                if (i == 0) firstMesh = b.replacementMesh;
            }
            if (firstMesh != null && MainMeshRef(slot) != null) MainMeshRef(slot).mesh = firstMesh;
            SlotLevel(slot) = 0;
            applied.Clear();
            slot.GoldIncome = 0;
            incomes.Remove(slot);
            if (hp != null) hp.Revive(false, 1f);
            SetBuildingState.Invoke(slot, [BuildSlot.BuildingState.Blueprint]);
            // Unbuilt slots this building switched on (a mill's fields) go away again until it's rebuilt.
            foreach (var child in slot.IsActivatorOf.Where(c => c != null && c.Level == 0 && c.gameObject.activeSelf).ToList())
            {
                SlotActivated(child) = false;
                child.gameObject.SetActive(false);
            }
            RefreshInteractors(slot);
            TooltipManager.instance?.SetInteractorRefreshFlag();
        }

        private static void Lock(BuildSlot slot, bool on)
        {
            slot = Root(slot);
            if (on)
            {
                Locked.Add(slot);
                if (slot.Level == 0 && slot.gameObject.activeSelf)
                {
                    hidden.Add(slot);
                    slot.gameObject.SetActive(false);
                }
            }
            else
            {
                Locked.Remove(slot);
                if (hidden.Remove(slot)) slot.gameObject.SetActive(true);
            }
            RefreshInteractors(slot);
        }

        /// <summary>The game switched a locked, unbuilt slot on (its parent got upgraded): keep it hidden.</summary>
        internal static void AfterActivate(BuildSlot slot)
        {
            if (slot == null || !Locked.Contains(slot) || slot.Level > 0 || !slot.gameObject.activeSelf) return;
            hidden.Add(slot);
            slot.gameObject.SetActive(false);
        }

        private static void RefreshInteractors(BuildSlot slot)
        {
            if (slot == null) return;
            foreach (var child in slot.BuiltSlotsThatRelyOnThisBuilding)
                if (child != null && child.buildingInteractor != null && child.buildingInteractor.isActiveAndEnabled) child.buildingInteractor.UpdateInteractionState();
            if (slot.buildingInteractor != null && slot.buildingInteractor.isActiveAndEnabled) slot.buildingInteractor.UpdateInteractionState();
        }

        // ------------------------------------------------------------------ end

        private static void EndLevel(LogicContext ctx, ActionParams p)
        {
            WorldCommon.RequireMatch("endLevel");
            string result = p.Text("result", "win");
            if (result != "win" && result != "lose") throw new LogicError($"endLevel: result must be \"win\" or \"lose\", not \"{result}\"");
            if (ending) return;
            ending = true;
            string message = p.Has("message") ? p.Text("message") : null;
            if (!string.IsNullOrEmpty(message)) Notify.Show(message, result == "win" ? NotifyKind.Success : NotifyKind.Warning, 4f, "logic-endLevel");
            Plugin.Instance.StartCoroutine(EndAfter(result == "win", string.IsNullOrEmpty(message) ? 0f : 1.5f));
        }

        private static IEnumerator EndAfter(bool win, float seconds)
        {
            if (seconds > 0) yield return new WaitForSecondsRealtime(seconds);
            var gs = LocalGamestate.Instance;
            if (gs == null || gs.CurrentState != LocalGamestate.State.InMatch) yield break;
            WorldCommon.log.Info($"endLevel: {(win ? "victory" : "defeat")}");
            // logic.game's victory/defeat "rules" veto the game's own endings, never this one.
            GameRules.EndingByRule = true;
            try { gs.SetState(win ? LocalGamestate.State.AfterMatchVictory : LocalGamestate.State.AfterMatchDefeat); }
            finally { GameRules.EndingByRule = false; }
        }

        /// <summary>Dev: every slot of the host with level, income, next cost, hp and lock.</summary>
        internal static string DescribeSlots() => string.Join("\n", LayoutScene.Slots(HostScene).Where(s => !s.ActivatorUpgradesThis).Select(s =>
        {
            var hp = HpOf(s);
            return $"{s.name}: active={s.gameObject.activeInHierarchy} level={s.Level} income={s.GoldIncome} cost={(s.Level < s.OwnUpgrades.Count ? s.OwnUpgrades[s.Level].cost : -1)} " +
                $"hp={(hp != null ? $"{hp.HpValue:0}/{hp.maxHp:0}" : "-")} locked={Locked.Contains(s)}";
        }));

        // ------------------------------------------------------------------ savestates

        internal static JToken Capture() => new JObject
        {
            ["locked"] = new JArray(Locked.Where(s => s != null).Select(s => SaveStates.PathOf(s))),
            ["incomes"] = new JObject(incomes.Where(kv => kv.Key != null).Select(kv => new JProperty(SaveStates.PathOf(kv.Key), kv.Value))),
        };

        internal static void Restore(JToken state)
        {
            if (state is JObject o) Plugin.Instance.StartCoroutine(RestoreLater(o));
        }

        // BuildSlot re-applies its loaded upgrades (and their income) a frame after the load: go after that.
        private static IEnumerator RestoreLater(JObject o)
        {
            yield return null;
            yield return null;
            var slots = SaveStates.FindByPath<BuildSlot>();
            foreach (var path in o["locked"]?.Values<string>() ?? [])
                if (path != null && slots.TryGetValue(path, out var s)) Lock(s, true);
            foreach (var prop in (o["incomes"] as JObject)?.Properties() ?? [])
                if (slots.TryGetValue(prop.Name, out var s)) { s.GoldIncome = (int)prop.Value; incomes[s] = s.GoldIncome; }
        }
    }
}
