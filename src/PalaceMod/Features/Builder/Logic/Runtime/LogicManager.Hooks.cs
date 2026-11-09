using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Game -> events (docs/builder-engine.md, "Hooks: where events come from"). Every hook only enqueues; the world
    dispatches in LateUpdate. Each is a no-op outside a logic session, and cheap when no rule listens: vanilla objects
    are wrapped only when an event about them has a listener.
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicManager
    {
        private static bool Listening(string ev) => World != null && World.HasListeners(ev);

        /// <summary>Entity for a game object if a rule could care: always for Builder objects, for vanilla ones only
        /// when <paramref name="ev"/> has an `of` listener (or the object's entity already exists).</summary>
        private static LogicEntity Of(Component c, string ev)
        {
            if (c == null) return null;
            var w = World;
            if (w == null) return null;
            if (c.GetComponentInParent<BuilderEntity>() is { Entity: { } be }) return be;
            if (!w.HasOfListeners(ev) && !w.HasListeners(ev)) return null;
            return LogicGame.EntityOf(c);
        }

        private void HookGame(LogicWorld world)
        {
            daytime = new DaytimeHook();
            DayNightCycle.Instance?.RegisterDaytimeSensitiveObject(daytime);
            slotHooks.Clear();
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>())
            {
                var s = slot;
                UnityEngine.Events.UnityAction a = () => OnSlotUpgraded(s);
                s.OnUpgrade.AddListener(a);
                slotHooks.Add((s, a));
            }
            var tm = TagManager.instance;
            goldHooked = tm != null && tm.Players.Count > 0 ? tm.Players[0].GetComponent<PlayerInteraction>() : null;
            if (goldHooked != null)
            {
                goldHooked.onBalanceGain.AddListener(OnGoldGain);
                goldHooked.onBalanceSpend.AddListener(OnGoldSpend);
            }
        }

        private void UnhookGame()
        {
            if (daytime != null && DayNightCycle.Instance != null) DayNightCycle.Instance.UnregisterDaytimeSensitiveObject(daytime);
            daytime = null;
            foreach (var (slot, a) in slotHooks) if (slot != null) slot.OnUpgrade.RemoveListener(a);
            slotHooks.Clear();
            if (goldHooked != null)
            {
                goldHooked.onBalanceGain.RemoveListener(OnGoldGain);
                goldHooked.onBalanceSpend.RemoveListener(OnGoldSpend);
            }
            goldHooked = null;
        }

        private void OnGoldGain(int amount)
        {
            if (incomeCounting) game.IncomeAtDawn += amount;
            if (Listening("goldChanged")) World.Raise("goldChanged", World.Level, null, amount, null, null);
        }

        private void OnGoldSpend(int amount)
        {
            if (Listening("goldChanged")) World.Raise("goldChanged", World.Level, null, -amount, null, null);
        }

        private void OnSlotUpgraded(BuildSlot slot)
        {
            var w = World;
            if (w == null || slot == null) return;
            bool built = slot.Level <= 1;
            string ev = built ? "built" : "upgraded";
            var e = Of(slot, ev);
            if (e != null) w.Raise(ev, e, null, slot.Level, null, null);
        }

        // ------------------------------------------------------------------ damage, death, heal, revive

        private struct DamageState { public float Hp; public bool Active; }

        [HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
        private static class TakeDamage_Patch
        {
            // Last: modifiers (L2's prefix at First) have already set the amount.
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Hp __instance, out DamageState __state)
            {
                __state = default;
                if (World == null) return;
                __state = new DamageState { Hp = __instance.HpValue, Active = true };
            }

            private static void Postfix(Hp __instance, TaggedObject _damageComingFrom, bool __result, DamageState __state)
            {
                if (!__state.Active) return;
                var w = World;
                if (w == null) return;
                try
                {
                    float dealt = __state.Hp - __instance.HpValue;
                    if (dealt <= 0f && !__result) return;   // nothing happened (skipped, invulnerable, already dead)
                    var victim = Of(__instance, __result ? "death" : "damaged");
                    var source = _damageComingFrom != null ? Of(_damageComingFrom, __result ? "kill" : "hit") : null;
                    if (victim == null && source == null) return;
                    // Credit a Builder projectile's hit to its owner as well (doc: hit/kill go to a projectile's owner).
                    var credited = source?.Kind == "projectile" && source.Owner != null ? source.Owner : source;
                    var pos = victim?.Position ?? __instance.transform.position.ToVec();
                    if (victim != null && dealt > 0) w.Raise("damaged", victim, credited, dealt, pos, null);
                    if (credited != null && dealt > 0) w.Raise("hit", credited, victim ?? LogicGame.EntityOf(__instance), dealt, pos, null);
                    if (__result)
                    {
                        var v = victim ?? LogicGame.EntityOf(__instance);
                        w.Raise("death", v, credited, 0, pos, null);
                        if (credited != null) w.Raise("kill", credited, v, 0, pos, null);
                    }
                }
                catch (Exception e) { Log.For<LogicManager>().Error("damage hook", e); }
            }
        }

        [HarmonyPatch(typeof(Hp), nameof(Hp.Heal))]
        private static class Heal_Patch
        {
            private static void Prefix(Hp __instance, out float __state) => __state = World != null ? __instance.HpValue : -1f;

            private static void Postfix(Hp __instance, float __state)
            {
                if (__state < 0 || World == null) return;
                float healed = __instance.HpValue - __state;
                if (healed <= 0) return;
                var e = Of(__instance, "healed");
                if (e != null) World.Raise("healed", e, null, healed, null, null);
            }
        }

        [HarmonyPatch(typeof(Hp), nameof(Hp.Revive))]
        private static class Revive_Patch
        {
            private static void Prefix(Hp __instance, out float __state) => __state = World != null ? __instance.HpValue : 1f;

            private static void Postfix(Hp __instance, float __state)
            {
                if (World == null) return;
                if (__state <= 0f)
                {
                    var e = Of(__instance, "repaired");
                    if (e != null) World.Raise("repaired", e, null, e.Body?.Level ?? 0, null, null);
                }
                else if (__instance.HpValue > __state)
                {
                    var e = Of(__instance, "healed");
                    if (e != null) World.Raise("healed", e, null, __instance.HpValue - __state, null, null);
                }
            }
        }

        // ------------------------------------------------------------------ attacks

        [HarmonyPatch]
        private static class AutoAttack_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.GetTypesFromAssembly(typeof(AutoAttack).Assembly)
                    .Where(t => typeof(AutoAttack).IsAssignableFrom(t))
                    .Select(t => t.GetMethod(nameof(AutoAttack.OnAttack), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, [typeof(TaggedObject)], null))
                    .Where(m => m != null && m.GetMethodBody() != null);

            private static void Postfix(AutoAttack __instance, TaggedObject target)
            {
                if (!Listening("attack")) return;
                var e = Of(__instance, "attack");
                if (e != null) World.Raise("attack", e, target != null ? LogicGame.EntityOf(target) : null, 0, null, null);
            }
        }

        [HarmonyPatch(typeof(ManualAttack), nameof(ManualAttack.Attack))]
        private static class ManualAttack_Patch
        {
            private static readonly AccessTools.FieldRef<ManualAttack, TaggedObject> target = AccessTools.FieldRefAccess<ManualAttack, TaggedObject>("target");

            private static void Postfix(ManualAttack __instance)
            {
                if (!Listening("attack")) return;
                var e = Of(__instance, "attack");
                if (e == null) return;
                var t = target(__instance);
                World.Raise("attack", e, t != null ? LogicGame.EntityOf(t) : null, 0, null, "king");
            }
        }

        // ------------------------------------------------------------------ nights and spawns

        [HarmonyPatch(typeof(EnemySpawner), nameof(EnemySpawner.StartSpawning))]
        private static class StartSpawning_Patch
        {
            private static void Postfix(EnemySpawner __instance)
            {
                var w = World;
                if (w == null) return;
                instance.waveClearedRaised = false;
                w.Raise("nightStart", w.Level, null, __instance.Wavenumber + 1, null, null);
            }
        }

        private static Spawn currentSpawn;

        [HarmonyPatch(typeof(Spawn), nameof(Spawn.Update))]
        private static class SpawnUpdate_Patch
        {
            private static void Prefix(Spawn __instance) => currentSpawn = World != null ? __instance : null;
            private static void Postfix() => currentSpawn = null;
        }

        [HarmonyPatch(typeof(TagManager), nameof(TagManager.AddTaggedObject))]
        private static class AddTagged_Patch
        {
            private static void Postfix(TaggedObject _taggedObject)
            {
                var w = World;
                if (w == null || _taggedObject == null) return;
                try
                {
                    var spawn = currentSpawn;
                    if (spawn != null && w.HasListeners("lineSpawn"))
                    {
                        string line = spawn.spawnLine != null ? spawn.spawnLine.name : null;
                        var lineEntity = spawn.spawnLine != null ? Of(spawn.spawnLine, "lineSpawn") : null;
                        w.Raise("lineSpawn", lineEntity ?? w.Level, LogicGame.EntityOf(_taggedObject), 0, null, line);
                    }
                    // Vanilla objects' `spawn`, for `of` listeners (Builder entities raise their own on register).
                    if (w.HasOfListeners("spawn") && _taggedObject.GetComponentInParent<BuilderEntity>() == null)
                        w.Raise("spawn", LogicGame.EntityOf(_taggedObject), null, 0, null, null);
                }
                catch (Exception e) { Log.For<LogicManager>().Error("spawn hook", e); }
            }
        }

        // ------------------------------------------------------------------ coins

        [HarmonyPatch(typeof(Coin), "Start")]
        private static class CoinStart_Patch
        {
            private static void Postfix(Coin __instance)
            {
                var w = World;
                if (w == null || !w.HasListeners("coinDrop")) return;
                var coin = LogicGame.EntityOf(__instance);
                if (coin == null) return;
                coin.Kind = "coin";
                coin.Team = "neutral";
                w.Raise("coinDrop", coin, null, 0, coin.Position, null);
            }
        }

        private static Coin currentCoin;

        [HarmonyPatch(typeof(Coin), "Update")]
        private static class CoinUpdate_Patch
        {
            private static void Prefix(Coin __instance) => currentCoin = World != null ? __instance : null;
            private static void Postfix() => currentCoin = null;
        }

        [HarmonyPatch(typeof(PlayerInteraction), nameof(PlayerInteraction.AddCoin))]
        private static class AddCoin_Patch
        {
            private static void Postfix(PlayerInteraction __instance)
            {
                var w = World;
                var c = currentCoin;
                if (w == null || c == null || !w.HasListeners("coinPickup")) return;
                var coin = LogicGame.EntityOf(c);
                if (coin == null) return;
                coin.Kind = "coin";
                coin.Team = "neutral";
                w.Raise("coinPickup", coin, LogicGame.EntityOf(__instance), 1, coin.Position, null);
            }
        }

        // ------------------------------------------------------------------ end

        [HarmonyPatch(typeof(SceneTransitionManager), nameof(SceneTransitionManager.TransitionFromGameplayToEndScreen))]
        private static class EndScreen_Patch
        {
            private static void Prefix()
            {
                var w = World;
                if (w == null) return;
                bool won = CastleCenter.instance != null && CastleCenter.instance.GetComponentInParent<Hp>() is { } hp ? !hp.KnockedOut
                    : EnemySpawner.instance != null && EnemySpawner.instance.MatchOver;
                w.Raise("levelEnd", w.Level, null, won ? 1 : 0, null, won ? "win" : "lose");
                w.Drain();
            }
        }
    }
}
