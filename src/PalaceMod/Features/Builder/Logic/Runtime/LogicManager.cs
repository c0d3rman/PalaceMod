using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The behaviour engine's runtime host (docs/builder-engine.md, "Runtime"). One LogicWorld per load of a Builder
    session's host (custom runs and Studio play; never Studio editing or vanilla runs):
    - starts once the level is up and no savestate restore is running: templates, statuses and presets from the
      session's pack, the level's `logic` block (vars, script, places, attaches), then `levelStart`;
    - ticks in LateUpdate (LogicDriver) on game time, after the game's own Update;
    - turns game moments into events (nested Harmony patches below, and LogicHooks.cs);
    - savestates ("logic" state) and retry-day (a snapshot at each dawn, by wave number);
    - the `logic` dev command and a "Logic" line in the performance summary.
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicManager : BaseManager
    {
        private static LogicWorld World => LogicWorld.Current;
        private static LogicManager instance;

        private readonly GameState game = new();
        private readonly TagSpatial spatial = new();
        private Scene startedScene;
        private int sceneLoadedFrame = -1;
        private bool startFailed;
        private string pendingRestore;
        private BuilderSessionInfo snapshotSession;
        private readonly Dictionary<int, string> dawnSnapshots = [];
        private readonly HashSet<string> loggedTrace = [];
        private DaytimeHook daytime;
        private readonly List<(BuildSlot slot, UnityEngine.Events.UnityAction action)> slotHooks = [];
        private PlayerInteraction goldHooked;
        private bool waveClearedRaised;
        private float purgeAt;
        private long lastFirings, lastActions;
        private float lastSummaryTime;

        private readonly Keybind[] actionKeys = new Keybind[3];
        private readonly Dictionary<string, (double lastUse, int uses, int period)> kingActionState = [];

        public override string FeatureDescription => null;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            instance = this;
            new GameObject("PalaceMod LogicDriver").AddComponent<LogicDriver>().hideFlags = HideFlags.HideAndDontSave;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (mode != LoadSceneMode.Single) return;
                EndWorld("scene loaded");
                sceneLoadedFrame = Time.frameCount;
                startFailed = false;
            };
            SaveStates.RegisterState("logic", CaptureState, RestoreState);
            // King action uses and cooldowns travel with the logic state (savestates and retry day).
            LogicSaveState.Register("kingActions",
                _ => new JObject(kingActionState.Select(kv => new JProperty(kv.Key, new JArray(kv.Value.lastUse, kv.Value.uses, kv.Value.period)))),
                (_, tok) =>
                {
                    kingActionState.Clear();
                    if (tok is JObject o)
                        foreach (var p in o.Properties())
                            if (p.Value is JArray a && a.Count == 3) kingActionState[p.Name] = ((double)a[0], (int)a[1], (int)a[2]);
                });
            RegisterDevCommands();
            for (int i = 0; i < 3; i++)
                actionKeys[i] = Keybinds.Register("Builder", $"level-action-{i + 1}", $"Level action {i + 1}",
                    new KeyboardShortcut(KeyCode.Alpha8 + i), $"A custom level's king action {i + 1} (levels define up to 3).", context: "BuilderLevel");
            PerformanceManager.SummaryLines.Add(SummaryLine);
        }

        // ------------------------------------------------------------------ lifecycle

        private static bool ShouldRun(out BuilderSessionInfo session)
        {
            session = BuilderServices.Sessions.Current;
            if (session == null) return false;
            if (SceneManager.GetActiveScene().name != session.HostScene) return false;
            var launcher = BuilderManager.Launcher;
            if (launcher != null && launcher.StudioKind == StudioSessionKind.Edit) return false;
            return true;
        }

        private static bool LevelReady =>
            TagManager.instance != null && EnemySpawner.instance != null && DayNightCycle.Instance != null && !SaveStates.IsRestoring
            && SceneTransitionManager.instance != null && !SceneTransitionManager.instance.SceneTransitionIsRunning;

        public override void Update()
        {
            bool run = ShouldRun(out var session);
            if (World != null && (!run || SceneManager.GetActiveScene() != startedScene)) EndWorld("session or scene changed");
            if (run && World == null && !startFailed && sceneLoadedFrame >= 0 && Time.frameCount > sceneLoadedFrame + 1 && LevelReady)
            {
                try { StartWorld(session); }
                catch (Exception e)
                {
                    startFailed = true;
                    Log.Error("Couldn't start the level's behaviour", e);
                    EndWorld("start failed");
                }
            }
            if (World == null) return;
            PollGame();
            PollKingActions();
        }

        /// <summary>LateUpdate: run the world on game time (paused = 0).</summary>
        internal void Tick()
        {
            var w = World;
            if (w == null) return;
            try { w.Tick(Time.deltaTime); }
            catch (Exception e) { Log.Error("Behaviour engine tick failed", e); }
            if (snapshotAfterTick)
            {
                snapshotAfterTick = false;
                OnDawnSnapshot();
            }
        }

        private bool snapshotAfterTick;

        private void StartWorld(BuilderSessionInfo session)
        {
            if (snapshotSession != session) { dawnSnapshots.Clear(); snapshotSession = session; kingActionState.Clear(); }
            var pack = JObject.Parse(PackSerializer.WriteCompact(session.Pack));
            pack = LogicPack.Expand(pack, session.Pack, out var expandIssues);
            int levelIndex = -1;
            JObject level = null;
            if (pack["levels"] is JArray levels)
                for (int i = 0; i < levels.Count; i++)
                    if ((string)levels[i]["id"] == session.Level?.Id) { levelIndex = i; level = (JObject)levels[i]; break; }
            var logic = level?["logic"] as JObject;

            var templates = new List<(string, JObject)>();
            // "_path": where the template is in the pack, for the trace and errors.
            if (pack["units"] is JArray units)
                for (int i = 0; i < units.Count; i++)
                    if (units[i] is JObject u)
                    {
                        var t = (JObject)u.DeepClone();
                        t["kind"] ??= "unit";
                        t["_path"] = $"units[{i}]";
                        templates.Add(((string)u["id"], t));
                    }
            if (pack["entities"] is JArray ents)
                for (int i = 0; i < ents.Count; i++)
                    if (ents[i] is JObject en) { en["_path"] = $"entities[{i}]"; templates.Add(((string)en["id"], en)); }
            bool anything = logic != null || templates.Any(t => t.Item2["rules"] is JArray { Count: > 0 }) || pack["entities"] is JArray { Count: > 0 };

            var world = new LogicWorld();
            game.NightRunning = game.IsNight;
            game.NightStartedAt = game.IsNight ? Time.time : -1;
            LogicServices.Game = game;
            LogicServices.Spatial = spatial;
            world.TraceListener = OnTrace;
            LogicWorld.Current = world;
            startedScene = SceneManager.GetActiveScene();
            loggedTrace.Clear();
            world.Load(logic, templates, (pack["statuses"] as JArray)?.OfType<JObject>(), (pack["presets"] as JArray)?.OfType<JObject>(), levelIndex >= 0 ? $"levels[{levelIndex}]" : "level");
            foreach (var issue in expandIssues) world.CompileErrors.Add(issue);

            int places = 0, attaches = 0;
            if (logic?["places"] is JArray placeList)
                for (int i = 0; i < placeList.Count; i++)
                    if (placeList[i] is JObject p && MakePlace(world, p, $"levels[{levelIndex}].logic.places[{i}]")) places++;
            if (logic?["attach"] is JObject attachList)
                foreach (var prop in attachList.Properties())
                    if (prop.Value is JObject a)
                    {
                        a["_path"] = $"levels[{levelIndex}].logic.attach[\"{prop.Name}\"]";
                        if (MakeAttach(world, prop.Name, a)) attaches++;
                    }

            HookGame(world);
            LogicWorld.RaiseStarted(world);

            string restored = null;
            int wave = EnemySpawner.instance.Wavenumber;
            if (pendingRestore != null)
            {
                restored = "savestate: " + world.Restore(JObject.Parse(pendingRestore), SetHp);
                pendingRestore = null;
            }
            else if (wave >= 0 && dawnSnapshots.TryGetValue(wave, out var snap))
                restored = "retry day: " + world.Restore(JObject.Parse(snap), SetHp);
            if (restored == null) world.BeginLevel();
            else world.ClearQueuedSpawns();
            waveClearedRaised = !game.IsNight;

            if (anything || restored != null)
                Log.Info($"Behaviour: {session.Pack.Id}/{session.Level?.Id}: {world.Entities.Count()} entities ({places} placed, {attaches} attached), " +
                    $"{world.Level.Rules.Count} level rules, {world.CompileErrors.Count} problems" + (restored != null ? $"; {restored}" : ""));
            foreach (var err in world.CompileErrors.Distinct().Take(10)) Log.Warn($"Behaviour: {err}");
        }

        private void EndWorld(string why)
        {
            var w = World;
            if (w == null) return;
            try { LogicWorld.RaiseEnding(w); }
            catch (Exception e) { Log.Error("A behaviour Ending handler failed", e); }
            UnhookGame();
            LogicWorld.Current = null;
            LogicServices.Spatial = null;
            Log.Debug($"Behaviour stopped ({why}): {w.FiringsTotal} firings, {w.ActionsTotal} action steps");
        }

        private static Vec ReadPos(JToken t) =>
            t is JArray a && a.Count >= 2
                ? a.Count == 2 ? new Vec((float)a[0], 0, (float)a[1]) : new Vec((float)a[0], (float)a[1], (float)a[2])
                : Vec.Zero;

        private bool MakePlace(LogicWorld world, JObject place, string path)
        {
            string id = (string)place["id"] ?? path;
            place["_path"] = path;
            string template = (string)place["entity"];
            if (template != null && template.StartsWith("@@")) template = template.Substring(2);
            var pos = ReadPos(place["pos"] ?? place["at"]);
            if (place["pos"] is JArray { Count: 2 } && Physics.Raycast(new Vector3(pos.X, 200, pos.Z), Vector3.down, out var hit, 400, ~0, QueryTriggerInteraction.Ignore))
                pos = new Vec(pos.X, hit.point.y, pos.Z);
            float rotY = (float?)place["rotY"] ?? 0;
            var e = world.Describe(template, place, id, (string)place["team"], null);
            e.StaticPos = pos;
            if (e.Kind == "logic" && place["kind"] != null) e.Kind = (string)place["kind"];
            var factory = LogicServices.Factory;
            if (factory != null)
            {
                if (factory.Build(e, pos, rotY, out string error))
                {
                    if (world.Find(e.Id) != e) world.Add(e);
                    return true;
                }
                world.CompileErrors.Add($"{path}: couldn't place \"{id}\": {error}");
                return false;
            }
            world.Add(e);   // no bodies yet: a logic-only entity at its position
            return true;
        }

        private bool MakeAttach(LogicWorld world, string key, JObject attach)
        {
            var go = ResolveKey(key, out string kind, out string team);
            if (go == null)
            {
                world.CompileErrors.Add($"logic.attach[\"{key}\"]: no object \"{key}\" on this map");
                return false;
            }
            var e = world.Describe(null, attach, key, team, null);
            e.Name = (string)attach["name"];
            e.Kind = kind;
            LogicGame.Register(e, go);
            try { LogicServices.Factory?.Attach(e); }
            catch (Exception ex) { Log.Error($"Attach components for {key}", ex); }
            return true;
        }

        /// <summary>A Studio item key ("slot:Tower 3", "add:slot-2", "prop:p12", "line:West", "castle") to its object.</summary>
        internal static GameObject ResolveKey(string key, out string kind, out string team)
        {
            kind = "building"; team = "player";
            var scene = SceneManager.GetActiveScene();
            int colon = key.IndexOf(':');
            string type = colon < 0 ? key : key.Substring(0, colon), name = colon < 0 ? "" : key.Substring(colon + 1);
            switch (type)
            {
                case "castle":
                    kind = "castle";
                    return Level.LayoutScene.Castle(scene)?.gameObject;
                case "slot":
                    return Level.LayoutScene.FindSlot(scene, name)?.gameObject;
                case "add":
                    return Level.LayoutScene.FindSlot(scene, Level.LayoutScene.AddedPrefix + name)?.gameObject;
                case "line":
                    kind = "line"; team = "enemy";
                    return UnityEngine.Object.FindObjectsOfType<EnemySpawnLine>().FirstOrDefault(l => l.name == name)?.gameObject;
                case "prop":
                    kind = "prop"; team = "neutral";
                    string goName = Level.LayoutScene.AddedPrefix + name;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root.name == goName) return root;
                        var t = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == goName);
                        if (t != null) return t.gameObject;
                    }
                    return null;
            }
            return null;
        }

        private static void SetHp(LogicEntity e, float hp)
        {
            var h = e.Hp();
            if (h != null && hp > 0) h.SetHpTo(Mathf.Min(hp, h.maxHp));
        }

        // ------------------------------------------------------------------ savestates and retry-day

        public sealed class LogicStateData { public string Json; }

        private LogicStateData CaptureState()
        {
            var w = World;
            if (w == null) return null;
            return new LogicStateData { Json = w.Capture().ToString(Newtonsoft.Json.Formatting.None) };
        }

        private void RestoreState(LogicStateData data)
        {
            if (data?.Json == null) return;
            pendingRestore = data.Json;
            // The level was rebuilt; a world started on it before the restore finished is replaced.
            if (World != null) EndWorld("savestate restore");
            startFailed = false;
            sceneLoadedFrame = Time.frameCount;
        }

        internal void OnDawnSnapshot()
        {
            var w = World;
            if (w == null || EnemySpawner.instance == null) return;
            try { dawnSnapshots[EnemySpawner.instance.Wavenumber] = w.Capture().ToString(Newtonsoft.Json.Formatting.None); }
            catch (Exception e) { Log.Error("Couldn't snapshot behaviour state at dawn", e); }
        }

        // ------------------------------------------------------------------ trace and summary

        private void OnTrace(TraceEntry t)
        {
            if (t.Outcome is not (TraceOutcome.Error or TraceOutcome.Throttled or TraceOutcome.Dropped or TraceOutcome.Capped)) return;
            string key = $"{t.Outcome}|{t.Path ?? t.Owner}|{t.Message}";
            if (!loggedTrace.Add(key) || loggedTrace.Count > 200) return;
            string line = $"Behaviour {t.Outcome.ToString().ToLowerInvariant()}: {t.Owner} {t.Rule} ({t.Event}) {t.Path}: {t.Message}";
            // A creator's mistake is a warning, not a mod error.
            Log.Warn(line);
        }

        private string SummaryLine()
        {
            var w = World;
            if (w == null) return null;
            float now = Time.unscaledTime, dt = Mathf.Max(0.001f, now - lastSummaryTime);
            var b = w.Budget();
            string s = $"Logic: {(w.FiringsTotal - lastFirings) / dt:0.#} firings/s, {(w.ActionsTotal - lastActions) / dt:0.#} actions/s, " +
                $"{w.Entities.Count()} entities ({b.LiveUnits} units, {b.LiveProjectiles} projectiles spawned), throttled {b.Throttled}, dropped {b.Dropped}, capped {b.CappedSpawns}, errors {b.Errors}";
            lastFirings = w.FiringsTotal; lastActions = w.ActionsTotal; lastSummaryTime = now;
            return s;
        }

        // ------------------------------------------------------------------ polled game moments

        private void PollGame()
        {
            var w = World;
            var spawner = EnemySpawner.instance;
            if (spawner == null) return;
            // waveCleared: spawning done and no enemy left, once per night.
            if (game.IsNight && !waveClearedRaised && !spawner.SpawningInProgress && spawner.NumberOfEnemiesOnTheMap <= 0
                && TagManager.instance.CountAllTaggedObjectsWithTag(TagManager.ETag.EnemyOwned) == 0)
            {
                waveClearedRaised = true;
                w.Raise("waveCleared", w.Level, null, game.Night, null, null);
            }
            if (Time.time >= purgeAt)
            {
                purgeAt = Time.time + 5f;
                w.PurgeGone();
            }
        }

        private void PollKingActions()
        {
            var w = World;
            var defs = KingActionDefs(w);
            for (int i = 0; i < 3; i++)
            {
                if (actionKeys[i] == null || !actionKeys[i].Down) continue;
                if (i >= defs.Count) continue;
                TryKingAction(w, defs[i]);
            }
        }

        private JArray kingActionCache;
        private LogicWorld kingActionWorld;

        private List<JObject> KingActionDefs(LogicWorld w)
        {
            if (kingActionWorld != w) { kingActionWorld = w; kingActionCache = w.LevelLogic?["kingActions"] as JArray; }
            return kingActionCache?.OfType<JObject>().Take(3).ToList() ?? [];
        }

        /// <summary>A king action's key: checks when/cooldown/uses, then raises `kingAction`. Returns why not, or null.</summary>
        internal string TryKingAction(LogicWorld w, JObject def)
        {
            string id = (string)def["id"] ?? "action";
            string when = (string)def["when"] ?? "always";
            if (when == "day" && game.IsNight || when == "night" && !game.IsNight) return Refuse($"{(string)def["name"] ?? id}: only by {when}");
            kingActionState.TryGetValue(id, out var st);
            double cooldown = (double?)def["cooldown"] ?? 0;
            if (st.lastUse > 0 && w.Time - st.lastUse < cooldown) return Refuse($"{(string)def["name"] ?? id}: ready in {cooldown - (w.Time - st.lastUse):0} s");
            var uses = def["uses"] as JObject;
            int period = (string)uses?["per"] switch { "day" or "night" => game.Night * 2 + (game.IsNight ? 1 : 0), _ => 0 };
            if ((string)uses?["per"] == "day" && game.IsNight || (string)uses?["per"] == "night" && !game.IsNight) period = -1;
            if (st.period != period) st.uses = 0;
            int max = (int?)uses?["count"] ?? int.MaxValue;
            if (st.uses >= max) return Refuse($"{(string)def["name"] ?? id}: no uses left");
            kingActionState[id] = (w.Time, st.uses + 1, period);
            w.Raise("kingAction", w.Level, game.King, st.uses + 1, null, id);
            return null;
        }

        private static string Refuse(string why)
        {
            Notify.Show(why, NotifyKind.Info, 2.5f, "builder-king-action");
            return why;
        }

        // ------------------------------------------------------------------ the driver

        private sealed class LogicDriver : MonoBehaviour
        {
            private void Awake() => DontDestroyOnLoad(gameObject);
            private void LateUpdate() => instance?.Tick();
        }

        /// <summary>dusk / dawn / day from the game's own day-night callbacks.</summary>
        private sealed class DaytimeHook : DayNightCycle.IDaytimeSensitive
        {
            public void OnDuskEarly() { }

            public void OnDusk()
            {
                var w = World;
                if (w == null) return;
                instance.game.NightStartedAt = Time.time;
                instance.waveClearedRaised = false;
                w.Raise("dusk", w.Level, null, instance.game.Night, null, null);
            }

            public void OnDawn_BeforeSunrise()
            {
                instance.incomeCounting = true;
                instance.game.IncomeAtDawn = 0;
            }

            public void OnDawn_AfterSunrise()
            {
                var w = World;
                instance.incomeCounting = false;
                int ended = instance.game.Night;
                instance.game.NightRunning = false;
                if (w == null) return;
                // Dawn order: income is paid (buildings' own dawn callbacks run before ours), then `dawn` rules.
                w.Raise("dawn", w.Level, null, ended, null, null);
                w.Raise("day", w.Level, null, instance.game.Night, null, null);
                instance.snapshotAfterTick = true;   // after the `dawn` rules have run
            }
        }

        private bool incomeCounting;
    }
}
