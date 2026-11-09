using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using PalaceMod.Builder.Model.Logic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
    logic.game (docs/builder-engine.md, "Game rules"; LogicSchema.GameRules): the game's own rules made data. Each rule
    is a literal or an expression over the level's globals and variables, read again every frame, so a level switches
    a rule by changing a variable.

    - victory / defeat "rules": the game's own endings (surviving the last night; the castle falling) are vetoed in
      LocalGamestate (SetState for the victory, OnVitalObjectKill for the castle); `endLevel` (and resigning) still end.
    - castle "hidden": the castle's renderers, colliders and navmesh cuts are off, it leaves the TagManager (nothing
      finds or targets it), takes no damage and can't lose the level.
    - holdNight: DayNightCycle ends a night when the spawner isn't spawning and no enemy is left; while the rule holds
      it sees the spawner as spawning (like spawnGroup's KeepNightOpen). The last night's victory waits with it.
    - endNight: when it turns true at night, the night ends now (GameRules.EndNight; survivors stay or vanish).
    - combat / build: the game gates attacks (AutoAttack*.Update, ManualAttack.TryToAttack) and the build prompts
      (BuildingInteractor.UpdateInteractionState) on its day/night state; those calls run with the state switched to
      what the rule wants (GamePhase.Switch). The player's side is made invulnerable at dawn by the game; with combat
      by day that is skipped (and undone/redone when the rule changes during the day).
    - dayLength: night falls by itself that many seconds into the day.

    Without logic.game (or outside a logic session) every patch is a no-op.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class GameRules
    {
        internal static readonly Log log = Log.For<LogicGameRulesManager>();

        /// <summary>Set by `endLevel` around its own SetState, which the victory/defeat rules never veto.</summary>
        internal static bool EndingByRule;

        private static JObject Def => LogicWorld.Current?.LevelLogic?["game"] as JObject;

        /// <summary>A logic session with a logic.game block.</summary>
        public static bool Active => LogicWorld.Current != null && Def != null;

        private static readonly HashSet<string> warned = [];

        private static Value? Eval(string name, string src)
        {
            var w = LogicWorld.Current;
            if (w == null) return null;
            try { return w.Evaluate(src); }
            catch (LogicError e)
            {
                if (warned.Add(name + "|" + e.Message)) log.Warn($"logic.game.{name}: {e.Message}");
                return null;
            }
        }

        public static string Enum(string name, string fallback)
        {
            var t = Def?[name];
            if (t == null || t.Type != JTokenType.String) return fallback;
            string s = (string)t;
            if (!s.StartsWith("=")) return s;
            return Eval(name, s.Substring(1)) is { } v ? Values.Format(v) : fallback;
        }

        public static bool Bool(string name, bool fallback)
        {
            var t = Def?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type is JTokenType.Integer or JTokenType.Float) return (double)t != 0;
            if (t.Type != JTokenType.String) return fallback;
            return Eval(name, (string)t) is { } v ? Values.ToBool(v) : fallback;
        }

        public static double Number(string name, double fallback)
        {
            var t = Def?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type is JTokenType.Integer or JTokenType.Float) return (double)t;
            if (t.Type != JTokenType.String) return fallback;
            try { return Eval(name, (string)t) is { } v ? Values.ToNumber(v) : fallback; }
            catch (LogicError) { return fallback; }
        }

        public static bool Has(string name) => Def?[name] is { Type: not JTokenType.Null };

        // ---- per-frame values (read by the patches; evaluated at most once a frame) ------------------------------

        private static int frame = -1;
        private static bool combat, build, hold;

        private static void Refresh()
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            bool night = GamePhase.IsNight;
            combat = Has("combat") ? Bool("combat", night) : night;
            build = Has("build") ? Bool("build", !night) : !night;
            hold = Has("holdNight") && Bool("holdNight", false);
        }

        /// <summary>Whether vanilla fighting happens now (null: no rule, the game decides).</summary>
        public static bool? Combat
        {
            get
            {
                if (!Active || !Has("combat")) return null;
                Refresh();
                return combat;
            }
        }

        public static bool? Build
        {
            get
            {
                if (!Active || !Has("build")) return null;
                Refresh();
                return build;
            }
        }

        public static bool HoldNight
        {
            get
            {
                if (!Active) return false;
                Refresh();
                return hold;
            }
        }

        public static bool VetoVictory => Active && !EndingByRule && Enum("victory", "lastNight") == "rules";
        public static bool VetoDefeat => Active && (Enum("defeat", "castle") == "rules" || CastleHidden);
        public static bool CastleHidden => Active && Enum("castle", "shown") == "hidden";

        /// <summary>Fresh (not the frame's cached value): the dawn's invulnerability runs right after the state flips.</summary>
        public static bool CombatByDayNow => Active && Has("combat") && !GamePhase.IsNight && Bool("combat", false);

        // ---- ending a night ---------------------------------------------------------------------------------------

        /// <summary>Ends the running night now: spawning stops, spawnGroups are dropped, the enemies left vanish or stay.</summary>
        public static void EndNight(bool removeSurvivors)
        {
            if (!WorldCommon.IsNight) return;
            var spawner = EnemySpawner.instance;
            WaveVerbs.Groups.Clear();
            spawner?.StopSpawnAfterWaveAndReset();
            if (removeSurvivors)
            {
                var enemies = new List<TaggedObject>();
                TagManager.instance.FindAllTaggedObjectsWithTags(enemies, [TagManager.ETag.EnemyOwned], []);
                foreach (var e in enemies) if (e != null) Object.Destroy(e.gameObject);
            }
            log.Info($"Night {(spawner != null ? spawner.Wavenumber + 1 : 0)} ended by the level ({(removeSurvivors ? "enemies removed" : "enemies stay")})");
            DayNightCycle.Instance.SwithToDay();
        }

        // ---- polling (LogicGameRulesManager.Update) ---------------------------------------------------------------

        private static LogicWorld polled;
        private static bool endNightWas, combatByDayWas, buildWas, buildKnown;
        private static float dayStartedAt = -1;
        private static int endedWave = int.MinValue;

        internal static void Reset()
        {
            warned.Clear();
            frame = -1;
            endNightWas = combatByDayWas = buildKnown = false;
            dayStartedAt = -1;
            endedWave = int.MinValue;
            ShowCastle();
        }

        internal static void Poll()
        {
            var w = LogicWorld.Current;
            if (!ReferenceEquals(w, polled))
            {
                polled = w;
                Reset();
            }
            if (!Active || !WorldCommon.InMatch) return;
            var dnc = DayNightCycle.Instance;
            var spawner = EnemySpawner.instance;
            bool night = GamePhase.IsNight;

            // castle
            if (CastleHidden) HideCastle(); else ShowCastle();

            // endNight: edge-triggered, once per night
            bool end = night && Has("endNight") && Bool("endNight", false);
            if (end && !endNightWas && spawner != null && endedWave != spawner.Wavenumber)
            {
                endedWave = spawner.Wavenumber;
                EndNight(Enum("survivors", "stay") == "remove");
            }
            endNightWas = end;

            // combat by day: the player's side loses (or regains) the day's invulnerability when the rule changes
            bool byDay = !night && dnc.AfterSunrise && CombatByDayNow;
            if (byDay != combatByDayWas && !night)
                Hp.SetAllUnitsWithTagInvulnerable([TagManager.ETag.PlayerOwned], [TagManager.ETag.AUTO_NoReviveNextMorning], !byDay);
            combatByDayWas = byDay;

            // build: refresh the build prompts when the rule flips
            if (Build is bool b)
            {
                if (!buildKnown || b != buildWas) RefreshInteractors();
                buildWas = b;
                buildKnown = true;
            }
            else if (buildKnown) { buildKnown = false; RefreshInteractors(); }

            // dayLength
            if (night || !dnc.AfterSunrise) dayStartedAt = -1;
            else
            {
                if (dayStartedAt < 0) dayStartedAt = Time.time;
                double len = Has("dayLength") ? Number("dayLength", 0) : 0;
                if (len > 0 && Time.time - dayStartedAt >= len && spawner != null && !spawner.SpawningInProgress
                    && spawner.Wavenumber + 1 < spawner.WaveCount)
                {
                    dayStartedAt = -1;
                    log.Info($"Day over after {len:0.#} s: night falls");
                    dnc.SwitchToNight();
                }
            }
        }

        internal static void RefreshInteractors()
        {
            if (TagManager.instance == null) return;
            foreach (var bi in TagManager.instance.playerBuildingInteractors.ToList())
                if (bi != null) bi.UpdateInteractionState();
        }

        // ---- the hidden castle ------------------------------------------------------------------------------------

        private sealed class Hidden
        {
            public GameObject Root;
            public TaggedObject Tagged;
            public readonly List<Renderer> Renderers = [];
            public readonly List<GPUInstanced> Instanced = [];
            public readonly List<Collider> Colliders = [];
            public readonly List<Behaviour> Cuts = [];
            public readonly List<Behaviour> Interactors = [];
            public Bounds Bounds;
        }

        private static Hidden hidden;

        internal static TaggedObject HiddenCastle => hidden?.Tagged;

        private static float rescanAt;

        private static void HideCastle()
        {
            if (hidden != null && hidden.Root != null)
            {
                // Builds and upgrades switch parts of the castle on over a few frames: catch them.
                if (Time.time < rescanAt) return;
                rescanAt = Time.time + 0.5f;
                Switch(hidden);
                return;
            }
            var castle = CastleCenter.instance;
            if (castle == null) return;
            var slot = castle.GetComponentInParent<BuildSlot>();
            // No castle to build: it counts as built (so slots that need it open) and disappears from the next frame.
            if (slot != null && slot.Level == 0)
            {
                if (slot.Upgrades.Count > 0 && slot.Upgrades[0].upgradeBranches.Count > 0) WorldVerbs.UpgradeFree(slot, 0);
                return;
            }
            var root = slot != null ? slot.gameObject : castle.gameObject;
            var h = new Hidden { Root = root, Tagged = castle.GetComponentInParent<TaggedObject>() ?? castle.GetComponentInChildren<TaggedObject>() };
            var bounds = new Bounds(root.transform.position, Vector3.one);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(r.bounds);
            h.Bounds = bounds;
            Switch(h);
            rescanAt = Time.time + 0.5f;
            if (h.Tagged != null && TagManager.instance != null) TagManager.instance.RemoveTaggedObject(h.Tagged);
            hidden = h;
            GamePhase.HiddenCastle = h.Tagged;
            if (h.Cuts.Count > 0) Level.GroundGraphs.UpdateArea(bounds);
            log.Info($"Castle hidden ({h.Renderers.Count} renderers, {h.Colliders.Count} colliders, {h.Cuts.Count} navmesh cuts)");
        }

        /// <summary>The hidden castle's root is (or holds) <paramref name="go"/>.</summary>
        internal static bool IsHidden(GameObject go) => hidden?.Root != null && go != null && (go == hidden.Root || go.transform.IsChildOf(hidden.Root.transform) || hidden.Root.transform.IsChildOf(go.transform));

        /// <summary>A building moved (`teleport`): a hidden castle's area (the graphs it updates when shown) moves with it.</summary>
        internal static void Moved(GameObject root, Vector3 delta)
        {
            if (IsHidden(root)) hidden.Bounds = new Bounds(hidden.Bounds.center + delta, hidden.Bounds.size);
        }

        /// <summary>Switches off whatever of the castle is on (and remembers it, to switch back on).</summary>
        private static void Switch(Hidden h)
        {
            var root = h.Root;
            // The map's static meshes (the castle's included) are drawn merged by MeshFusion: take the castle's part
            // out of the merged mesh, which turns its own renderers back on for the switch below.
            BuildingMover.Uncombine(root);
            foreach (var g in root.GetComponentsInChildren<GPUInstanced>(true)) if (g.enabled) { g.enabled = false; h.Instanced.Add(g); }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; h.Renderers.Add(r); }
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (c.enabled) { c.enabled = false; h.Colliders.Add(c); }
            foreach (var cut in root.GetComponentsInChildren<Pathfinding.NavmeshCut>(true))
            {
                var b = (Behaviour)(object)cut;
                if (b.enabled) { b.enabled = false; h.Cuts.Add(b); }
            }
            foreach (var bi in root.GetComponentsInChildren<InteractorBase>(true)) if (bi.enabled) { bi.enabled = false; h.Interactors.Add(bi); }
        }

        private static void ShowCastle()
        {
            var h = hidden;
            if (h == null) return;
            hidden = null;
            GamePhase.HiddenCastle = null;
            if (h.Root == null) return;
            foreach (var r in h.Renderers) if (r != null) r.enabled = true;
            foreach (var g in h.Instanced) if (g != null) g.enabled = true;
            foreach (var c in h.Colliders) if (c != null) c.enabled = true;
            foreach (var cut in h.Cuts) if (cut != null) cut.enabled = true;
            foreach (var bi in h.Interactors) if (bi != null) bi.enabled = true;
            if (h.Tagged != null && TagManager.instance != null) TagManager.instance.AddTaggedObject(h.Tagged);
            if (h.Cuts.Count > 0) Level.GroundGraphs.UpdateArea(h.Bounds);
            log.Info("Castle shown");
        }

        /// <summary>Dev: every drawn renderer within 8 m of the castle, with its path.</summary>
        internal static string NearCastle()
        {
            if (CastleCenter.instance == null) return "no castle";
            var c = CastleCenter.instance.transform.position;
            static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
            var lines = Object.FindObjectsOfType<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && Vector3.Distance(r.bounds.center, c) < 8f)
                .Select(r => $"{PathOf(r.transform)} ({r.GetType().Name})")
                .Concat(Object.FindObjectsOfType<GPUInstanced>().Where(g => g.enabled && Vector3.Distance(g.transform.position, c) < 8f).Select(g => $"{PathOf(g.transform)} (GPUInstanced)"));
            return $"castle at {c} (CastleCenter {PathOf(CastleCenter.instance.transform)}):\n" + string.Join("\n", lines.Take(60));
        }

        internal static string Status()
        {
            if (LogicWorld.Current == null) return "no logic session";
            if (!Active) return "no logic.game (vanilla rules)";
            var castleRoot = CastleCenter.instance != null ? (CastleCenter.instance.GetComponentInParent<BuildSlot>()?.gameObject ?? CastleCenter.instance.gameObject) : null;
            int drawn = castleRoot == null ? 0 : castleRoot.GetComponentsInChildren<Renderer>(false).Count(r => r.enabled)
                + castleRoot.GetComponentsInChildren<GPUInstanced>(false).Count(g => g.enabled);
            return $"victory={Enum("victory", "lastNight")} defeat={Enum("defeat", "castle")} castle={Enum("castle", "shown")}{(hidden != null ? " (hidden)" : "")} castleDrawn={drawn} " +
                $"holdNight={HoldNight} endNight={(Has("endNight") ? Bool("endNight", false).ToString() : "-")} survivors={Enum("survivors", "stay")} " +
                $"combat={Combat?.ToString() ?? "game"} build={Build?.ToString() ?? "game"} dayLength={Number("dayLength", 0):0.#} " +
                $"night={GamePhase.IsNight} dayFor={(dayStartedAt >= 0 ? Time.time - dayStartedAt : 0):0.#}";
        }
    }
}

namespace PalaceMod
{
    /// <summary>logic.game's runtime: polls the rules and owns their patches (GameRules). Infrastructure, no settings.</summary>
    public class LogicGameRulesManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("logic-game", "logic-game [status|castle]   logic.game rules as the level sees them now; castle: what draws near the castle", args =>
                args.FirstOrDefault() == "castle" ? GameRules.NearCastle() : GameRules.Status());
        }

        public override void Update() => GameRules.Poll();

        // ------------------------------------------------------------------ win and lose

        [HarmonyPatch(typeof(LocalGamestate), nameof(LocalGamestate.SetState))]
        private static class VictoryVeto
        {
            private static bool Prefix(LocalGamestate __instance, LocalGamestate.State nextState)
            {
                if (nextState != LocalGamestate.State.AfterMatchVictory || __instance.CurrentState != LocalGamestate.State.InMatch) return true;
                if (!GameRules.VetoVictory) return true;
                GameRules.log.Info("The game's victory vetoed (logic.game.victory = rules)");
                return false;
            }
        }

        [HarmonyPatch(typeof(LocalGamestate), "OnVitalObjectKill")]
        private static class DefeatVeto
        {
            private static bool Prefix()
            {
                if (!GameRules.VetoDefeat) return true;
                GameRules.log.Info("The castle's fall doesn't lose this level (logic.game)");
                return false;
            }
        }

        // The hidden castle takes no damage (the game toggles its invulnerability at dusk and dawn).
        [HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
        private static class HiddenCastleDamage
        {
            [HarmonyPriority(Priority.First + 1)]
            private static bool Prefix(Hp __instance, ref bool __result)
            {
                var c = GameRules.HiddenCastle;
                if (c == null || __instance.TaggedObj != c) return true;
                __result = false;
                return false;
            }
        }

        // ------------------------------------------------------------------ night flow

        [HarmonyPatch(typeof(DayNightCycle), "Update")]
        private static class HoldNight
        {
            private static void Prefix(out bool __state)
            {
                __state = false;
                var spawner = EnemySpawner.instance;
                if (spawner == null || spawner.SpawningInProgress || !GameRules.HoldNight || !GamePhase.IsNight) return;
                WorldCommon.SpawningRef(spawner) = true;
                __state = true;
            }

            private static void Finalizer(bool __state)
            {
                if (__state && EnemySpawner.instance != null) WorldCommon.SpawningRef(EnemySpawner.instance) = false;
            }
        }

        // ------------------------------------------------------------------ combat by day / no combat at night

        [HarmonyPatch]
        private static class CombatPhase
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var t in AccessTools.GetTypesFromAssembly(typeof(AutoAttack).Assembly).Where(t => typeof(AutoAttack).IsAssignableFrom(t)))
                {
                    var m = t.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null && m.GetMethodBody() != null) yield return m;
                }
                yield return AccessTools.Method(typeof(ManualAttack), nameof(ManualAttack.TryToAttack));
            }

            private static void Prefix(out DayNightCycle.Timestate? __state)
            {
                __state = null;
                if (GameRules.Combat is not bool fight) return;
                __state = GamePhase.Switch(fight ? DayNightCycle.Timestate.Night : DayNightCycle.Timestate.Day);
            }

            private static void Finalizer(DayNightCycle.Timestate? __state) => GamePhase.Restore(__state);
        }

        // Dawn makes the player's side invulnerable for the day; not while combat runs by day.
        [HarmonyPatch(typeof(Hp), nameof(Hp.SetAllUnitsWithTagInvulnerable))]
        private static class DayInvulnerability
        {
            private static bool Prefix(bool _invulnerable) => !(_invulnerable && GameRules.CombatByDayNow);
        }

        // ------------------------------------------------------------------ building by night / not by day

        [HarmonyPatch(typeof(BuildingInteractor), nameof(BuildingInteractor.UpdateInteractionState))]
        private static class BuildPhase
        {
            private static void Prefix(out DayNightCycle.Timestate? __state)
            {
                __state = null;
                if (GameRules.Build is not bool allowed) return;
                __state = GamePhase.Switch(allowed ? DayNightCycle.Timestate.Day : DayNightCycle.Timestate.Night);
            }

            private static void Finalizer(DayNightCycle.Timestate? __state) => GamePhase.Restore(__state);
        }

        // ------------------------------------------------------------------ weapons follow their wielder's side

        /*
            A weapon's damage table rows name the side they hurt by team tag (an enemy weapon's rows need PlayerOwned,
            a player weapon's EnemyOwned). A weapon swung by a unit of another side (a neutral beast, a copied or stolen
            weapon) would do 0 to everything it's aimed at. While such a wielder deals damage, a team-tag row means
            "the wielder's foe" instead: the other side, or, for a neutral wielder, whatever it hits.
        */
        private enum Side { None, Player, Enemy, Neutral }

        private static Side wielder = Side.None;

        private static Side SideOf(TaggedObject t) =>
            t.Tags.Contains(TagManager.ETag.PlayerOwned) ? Side.Player : t.Tags.Contains(TagManager.ETag.EnemyOwned) ? Side.Enemy : Side.Neutral;

        private static readonly Dictionary<Weapon, Side> nativeSides = [];

        /// <summary>The side a weapon is made for, from the rows it hurts with: rows naming EnemyOwned make it a player
        /// weapon, PlayerOwned an enemy one; else `isPlayerWeapon` (which towers' weapons don't set).</summary>
        private static Side NativeSide(Weapon w)
        {
            if (nativeSides.TryGetValue(w, out var s)) return s;
            bool Names(TagManager.ETag tag) =>
                (w.directDamage?.Any(m => m?.requiredTags != null && m.requiredTags.Contains(tag)) ?? false)
                || (w.splashDamage?.Any(m => m?.requiredTags != null && m.requiredTags.Contains(tag)) ?? false);
            s = Names(TagManager.ETag.EnemyOwned) ? Side.Player : Names(TagManager.ETag.PlayerOwned) ? Side.Enemy
                : w.isPlayerWeapon ? Side.Player : Side.Enemy;
            return nativeSides[w] = s;
        }

        [HarmonyPatch(typeof(Weapon), nameof(Weapon.DealDamage))]
        private static class WielderSide
        {
            private static void Prefix(Weapon __instance, TaggedObject _attacker, out Side __state)
            {
                __state = wielder;
                if (LogicWorld.Current == null || _attacker == null) return;
                var side = SideOf(_attacker);
                wielder = side == NativeSide(__instance) ? Side.None : side;
            }

            private static void Finalizer(Side __state) => wielder = __state;
        }

        [HarmonyPatch(typeof(DamageModifyer), nameof(DamageModifyer.AppliesTo))]
        private static class WielderRows
        {
            private static bool Prefix(DamageModifyer __instance, TaggedObject _taggedObject, ref bool __result)
            {
                if (wielder == Side.None) return true;
                var target = SideOf(_taggedObject);
                foreach (var tag in __instance.requiredTags)
                {
                    bool ok = tag is TagManager.ETag.PlayerOwned or TagManager.ETag.EnemyOwned
                        ? wielder == Side.Neutral || (target != Side.Neutral && target != wielder)
                        : _taggedObject.Tags.Contains(tag);
                    if (!ok) { __result = false; return false; }
                }
                __result = true;
                return false;
            }
        }

        // Dusk forces every build prompt off; with building at night they come back right after.
        [HarmonyPatch(typeof(BuildingInteractor), nameof(BuildingInteractor.OnDusk))]
        private static class BuildAtDusk
        {
            private static void Postfix(BuildingInteractor __instance)
            {
                if (GameRules.Build == true) __instance.UpdateInteractionState();
            }
        }
    }
}
