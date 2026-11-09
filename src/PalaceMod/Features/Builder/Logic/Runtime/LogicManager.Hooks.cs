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

        private static bool Wants(LogicWorld w, string ev) => w.HasOfListeners(ev) || w.HasListeners(ev);

        /// <summary>The entity that dealt a hit, from what the game passed (a TaggedObject) or what the hooks below
        /// remembered (an attack, a projectile's shooter, a damage-dealing script): a Builder entity always, a
        /// vanilla one when <paramref name="wanted"/>. Only something that is (or belongs to) a game object with a
        /// TaggedObject counts: scenery hazards (water, kill zones) name no attacker.</summary>
        private static LogicEntity AttackerOf(Component from, bool wanted)
        {
            if (from == null) return null;
            if (from.GetComponentInParent<BuilderEntity>() is { Entity: { } be }) return be;
            if (!wanted) return null;
            var tagged = from as TaggedObject ?? from.GetComponentInParent<TaggedObject>();
            return tagged != null ? LogicGame.EntityOf(tagged) : null;
        }

        private void HookGame(LogicWorld world)
        {
            daytime = new DaytimeHook();
            DayNightCycle.Instance?.RegisterDaytimeSensitiveObject(daytime);
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
            if (goldHooked != null)
            {
                goldHooked.onBalanceGain.RemoveListener(OnGoldGain);
                goldHooked.onBalanceSpend.RemoveListener(OnGoldSpend);
            }
            goldHooked = null;
        }

        private void OnGoldGain(int amount)
        {
            if (Listening("goldChanged")) World.Raise("goldChanged", World.Level, null, amount, null, null);
        }

        private void OnGoldSpend(int amount)
        {
            if (Listening("goldChanged")) World.Raise("goldChanged", World.Level, null, -amount, null, null);
        }

        /*
            `built` / `upgraded`: every build or upgrade goes through OnUpgradeChoiceComplete (the king's interact, the
            `building` action, dev builds), for map slots, Builder-added slots and slots made after the session
            started alike. A slot whose level follows its activator's (a gate's wall parts) doesn't raise its own.
            ForceManualUpgradeWithoutFeedbackEffects (load/restore, previews) isn't a build.
        */
        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnUpgradeChoiceComplete))]
        private static class SlotUpgrade_Patch
        {
            private static void Prefix(BuildSlot __instance, out int __state) =>
                __state = World != null && !__instance.ActivatorUpgradesThis ? __instance.Level : -1;

            private static void Postfix(BuildSlot __instance, int __state)
            {
                var w = World;
                if (w == null || __state < 0 || __instance.Level <= __state) return;
                try
                {
                    string ev = __state == 0 ? "built" : "upgraded";
                    var e = Of(__instance, ev);
                    if (e != null) w.Raise(ev, e, null, __instance.Level, null, null);
                }
                catch (Exception ex) { Log.For<LogicManager>().Error("build hook", ex); }
            }
        }

        // ------------------------------------------------------------------ dawn income

        /// <summary>`income`: what the dawn's harvests paid (the coins the buildings' spawners send, `income` stats
        /// applied), counted from the first dawn callback to the last, so the `dawn` rules (dispatched after them)
        /// read the whole payout although the coins themselves land over the next seconds.</summary>
        private static bool incomeCounting;

        [HarmonyPatch(typeof(DayNightCycle), "DawnCallBeforeSunrise")]
        private static class DawnIncomeStart_Patch
        {
            private static void Prefix()
            {
                if (World == null || instance == null) return;
                incomeCounting = true;
                instance.game.IncomeAtDawn = 0;
            }
        }

        [HarmonyPatch(typeof(DayNightCycle), "DawnCallAfterSunrise")]
        private static class DawnIncomeEnd_Patch
        {
            private static Exception Finalizer(Exception __exception)
            {
                incomeCounting = false;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(CoinSpawner), nameof(CoinSpawner.TriggerCoinSpawn))]
        private static class DawnIncomeCoins_Patch
        {
            private static void Prefix(CoinSpawner __instance, int amount)
            {
                if (!incomeCounting || World == null || instance == null || amount <= 0) return;
                if (!__instance.gameObject.activeInHierarchy) return;   // the game spawns nothing then
                instance.game.IncomeAtDawn += amount;
            }
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
                    Component from = _damageComingFrom != null ? _damageComingFrom : currentAttacker;
                    // The victim's damaged/death rules name the attacker whether or not anything listens to hit/kill.
                    bool wanted = victim != null || Wants(w, "hit") || __result && Wants(w, "kill");
                    var source = AttackerOf(from, wanted);
                    if (source == null && _damageComingFrom == null) source = LogicServices.CurrentDamageSource?.Invoke();
                    if (victim == null && source == null) return;
                    // Credit a Builder projectile's hit to its owner as well (doc: hit/kill go to a projectile's owner).
                    var credited = source?.Kind == "projectile" && source.Owner != null ? source.Owner : source;
                    var pos = victim?.Position ?? __instance.transform.position.ToVec();
                    // The hit's damage types and weapon (event.types / event.weapon), from the game's attacker.
                    var (types, weapon) = LogicServices.CurrentHit?.Invoke(_damageComingFrom != null ? _damageComingFrom : from != null ? from.GetComponentInParent<TaggedObject>() : null) ?? default;
                    if (victim != null && dealt > 0) w.Raise("damaged", victim, credited, dealt, pos, null, types: types, weapon: weapon);
                    if (credited != null && dealt > 0) w.Raise("hit", credited, victim ?? LogicGame.EntityOf(__instance), dealt, pos, null, types: types, weapon: weapon);
                    if (__result)
                    {
                        var v = victim ?? LogicGame.EntityOf(__instance);
                        w.Raise("death", v, credited, 0, pos, null, types: types, weapon: weapon);
                        if (credited != null) w.Raise("kill", credited, v, 0, pos, null, types: types, weapon: weapon);
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

        /*
            Attribution. A tower's AutoAttack sits on a child without a TaggedObject, so the game passes a null attacker
            to Weapon.Attack and its arrows' TakeDamage. Without changing what the game passes (that would change aggro),
            the attacking component is remembered here: during OnAttack (direct weapons), and per projectile from Fire
            to its hit (AimbotProjectile.Update), and TakeDamage's postfix falls back to it.
        */
        private static Component currentAttacker;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AimbotProjectile, Component> shooterOf = new();

        [HarmonyPatch]
        private static class AutoAttack_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.GetTypesFromAssembly(typeof(AutoAttack).Assembly)
                    .Where(t => typeof(AutoAttack).IsAssignableFrom(t))
                    .Select(t => t.GetMethod(nameof(AutoAttack.OnAttack), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, [typeof(TaggedObject)], null))
                    .Where(m => m != null && m.GetMethodBody() != null);

            private static void Prefix(AutoAttack __instance, out Component __state)
            {
                __state = currentAttacker;
                if (World != null) currentAttacker = __instance;
            }

            private static void Postfix(AutoAttack __instance, TaggedObject target, Component __state)
            {
                currentAttacker = __state;
                if (!Listening("attack")) return;
                var e = Of(__instance, "attack");
                if (e != null) World.Raise("attack", e, target != null ? LogicGame.EntityOf(target) : null, 0, null, null);
            }
        }

        [HarmonyPatch(typeof(AimbotProjectile), nameof(AimbotProjectile.Fire))]
        private static class ProjectileFire_Patch
        {
            private static void Postfix(AimbotProjectile __instance)
            {
                if (World == null || currentAttacker == null) return;
                shooterOf.Remove(__instance);
                shooterOf.Add(__instance, currentAttacker);
            }
        }

        [HarmonyPatch(typeof(AimbotProjectile), "Update")]
        private static class ProjectileUpdate_Patch
        {
            private static void Prefix(AimbotProjectile __instance, out Component __state)
            {
                __state = currentAttacker;
                if (World != null && shooterOf.TryGetValue(__instance, out var c)) currentAttacker = c;
            }

            private static void Postfix(Component __state) => currentAttacker = __state;
        }

        /// <summary>Who fired a vanilla projectile (the attack that was running when it was fired), or null.</summary>
        internal static Component ShooterOf(AimbotProjectile p) => p != null && shooterOf.TryGetValue(p, out var c) ? c : null;

        /*
            What a hit leaves behind (a fire archer's burning ground, a projectile's spawn-on-hit) deals its damage
            later from its own script, with no attacker. Every object a projectile instantiates on its hit is
            remembered with the shooter, and those scripts' damage is credited to it (DamageScript_Patch).
        */
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameObject, Component> leftBy = new();

        private static void Spawned(GameObject go)
        {
            if (go == null || World == null || currentAttacker == null) return;
            leftBy.Remove(go);
            leftBy.Add(go, currentAttacker);
        }

        [HarmonyPatch]
        private static class ProjectileSpawnOnHit_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(AimbotProjectile), "SpawnWeaponObjectOnHit");
                yield return AccessTools.Method(typeof(AimbotProjectile), "SpawnProjectileObjectOnHit");
            }

            // after every Instantiate that returns a GameObject: dup it and hand it to Spawned
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var spawned = AccessTools.Method(typeof(LogicManager), nameof(Spawned));
                foreach (var ci in code)
                {
                    yield return ci;
                    if ((ci.opcode == System.Reflection.Emit.OpCodes.Call || ci.opcode == System.Reflection.Emit.OpCodes.Callvirt)
                        && ci.operand is MethodInfo m && m.Name == "Instantiate" && m.ReturnType == typeof(GameObject))
                    {
                        yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Dup);
                        yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, spawned);
                    }
                }
            }
        }

        /*
            The game's damage scripts (hot oil, spikes, burning ground, a charging king, self-destructs...) call
            Hp.TakeDamage without an attacker. Every game method that calls it (found by its IL, so new ones are
            covered) credits the hit, unless an attack or projectile already set one, to what left the script (above)
            or else to the object the script sits on. AttackerOf then keeps only objects with a TaggedObject.
        */
        [HarmonyPatch]
        private static class DamageScript_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var takeDamage = AccessTools.Method(typeof(Hp), nameof(Hp.TakeDamage));
                int token = takeDamage.MetadataToken;
                var module = takeDamage.Module;
                var skip = new HashSet<Type> { typeof(Hp), typeof(Weapon) };
                foreach (var t in AccessTools.GetTypesFromAssembly(typeof(Hp).Assembly))
                {
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t) || skip.Contains(t) || t.IsGenericTypeDefinition) continue;
                    if (typeof(AutoAttack).IsAssignableFrom(t) || typeof(ManualAttack).IsAssignableFrom(t) || typeof(AimbotProjectile).IsAssignableFrom(t)) continue;
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition || m.Module != module) continue;
                        byte[] il;
                        try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                        catch { continue; }
                        if (il != null && CallsToken(il, token)) yield return m;
                    }
                }
            }

            private static bool CallsToken(byte[] il, int token)
            {
                for (int i = 0; i + 4 < il.Length; i++)
                    if ((il[i] == 0x28 || il[i] == 0x6F) && BitConverter.ToInt32(il, i + 1) == token) return true;
                return false;
            }

            private static void Prefix(MonoBehaviour __instance, out Component __state)
            {
                __state = currentAttacker;
                if (World == null || currentAttacker != null || __instance == null) return;
                var t = __instance.transform;
                Component by = null;
                for (var x = t; x != null && by == null; x = x.parent) leftBy.TryGetValue(x.gameObject, out by);
                currentAttacker = by != null ? by : __instance;
            }

            private static void Postfix(Component __state) => currentAttacker = __state;
        }

        [HarmonyPatch(typeof(ManualAttack), nameof(ManualAttack.Attack))]
        private static class ManualAttack_Patch
        {
            private static readonly AccessTools.FieldRef<ManualAttack, TaggedObject> target = AccessTools.FieldRefAccess<ManualAttack, TaggedObject>("target");

            private static void Prefix(ManualAttack __instance, out Component __state)
            {
                __state = currentAttacker;
                if (World != null) currentAttacker = __instance;
            }

            private static void Postfix(ManualAttack __instance, Component __state)
            {
                currentAttacker = __state;
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
                instance.game.NightRunning = true;
                w.Raise("nightStart", w.Level, null, __instance.Wavenumber + 1, null, null);
            }
        }

        private static Spawn currentSpawn;

        [HarmonyPatch(typeof(Spawn), nameof(Spawn.Update))]
        private static class SpawnUpdate_Patch
        {
            // A disabled spawn line (an attached `line:` entity switched off by `disable`) pauses its groups: their
            // spawn timers don't run, so the night's schedule (and nightProgress) waits for it.
            private static bool Prefix(Spawn __instance)
            {
                currentSpawn = null;
                if (World == null) return true;
                var line = __instance.spawnLine;
                if (line != null && line.GetComponentInParent<BuilderEntity>() is { Entity: { Enabled: false } }) return false;
                currentSpawn = __instance;
                return true;
            }

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
                var state = LocalGamestate.Instance != null ? LocalGamestate.Instance.CurrentState : LocalGamestate.State.InMatch;
                bool won = state == LocalGamestate.State.AfterMatchVictory || state != LocalGamestate.State.AfterMatchDefeat && (CastleCenter.instance != null && CastleCenter.instance.GetComponentInParent<Hp>() is { } hp ? !hp.KnockedOut
                    : EnemySpawner.instance != null && EnemySpawner.instance.MatchOver);
                w.Raise("levelEnd", w.Level, null, won ? 1 : 0, null, won ? "win" : "lose");
                w.Drain();
            }
        }
    }
}
