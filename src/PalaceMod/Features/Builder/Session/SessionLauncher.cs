using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using UnityEngine.SceneManagement;

/*
    Custom sessions (WP3). A session is one custom run of a design (a pack's wave set or level) on its host map.

    Two ways in:
    - Launch(request): validates, starts the session and loads the host straight away (editor "Test", `builder play`).
      There's no loadout screen on this path, so it equips TestLoadout's (last used on the map, or a chosen weapon).
    - Arm(request): the player picked a design in the Custom Runs menu of a map's pre-level menu. The pre-level menu
      then shows the design, keeps the game's own loadout and perk selection, and its Play button starts the session
      (BuilderManager.Session.cs turns TransitionFromLevelSelectToLevel into BeginArmed()).

    While a session runs, BuilderManager.Session.cs keeps it apart from the player's progress: the host's LevelData is
    swapped for Current.Scores (seeded with the design's custom best), the save file, mid-match save file,
    leaderboards and achievements aren't written, the end screen gives no XP or unlocks, and perk XP/level are
    restored when it ends. Results go to ScoreStore (BepInEx/PalaceBuilder/scores.json). Every custom run is marked
    with GameplayImpact. The session ends when any scene other than the host loads (menu, level select, another
    map); restarting or retrying a day keeps it.
*/
namespace PalaceMod.Builder
{
    public sealed class SessionLauncher : ISessionLauncher
    {
        public const string ImpactReason = "Builder: custom content";

        public BuilderSessionInfo Current { get; private set; }
        public event Action<BuilderSessionInfo> Started;
        public event Action<BuilderSessionInfo> Ended;

        /// <summary>The design picked in a pre-level menu, waiting for Play; null otherwise.</summary>
        internal ArmedDesign Armed { get; private set; }

        internal sealed class ArmedDesign
        {
            public SessionRequest Request;
            public string Host;
            public string Name;
            /// <summary>The design's custom best, shown in the pre-level menu's highscore box.</summary>
            public int? Best;
        }

        private int savedPerkLevel, savedPerkXp;
        private bool resultRecorded;

        public SessionLauncher()
        {
            SessionMenu.Init(this);
            SessionDevCommands.Register(this);
        }

        // ------------------------------------------------------------------ ISessionLauncher

        public bool Launch(SessionRequest request, out string error)
        {
            var transitions = SceneTransitionManager.instance;
            if (transitions == null || transitions.SceneTransitionIsRunning) { error = "can't start a level right now"; return false; }
            // No loadout screen on this path: equip the test loadout (read before the session's stand-in scores
            // replace the host's record).
            if (!Resolve(request, out var ws, out var lv, out error)) return false;
            var loadout = TestLoadout.For(lv?.Host ?? ws.Host);
            if (!Start(request, out error)) return false;
            TestLoadout.Apply(Current.HostScene, loadout);
            transitions.TransitionFromNullToLevel(Current.HostScene);
            return true;
        }

        /// <summary>Ends the session. If its map is still loaded, the player is taken to level select first and the
        /// session's isolation stays on until the map has unloaded.</summary>
        public void End()
        {
            Armed = null;
            if (Current == null) return;
            var transitions = SceneTransitionManager.instance;
            if (transitions != null && SceneManager.GetSceneByName(Current.HostScene).isLoaded)
            {
                if (!transitions.SceneTransitionIsRunning) transitions.TransitionToLevelSelect();
                return; // OnSceneLoaded finishes it once another scene is in
            }
            Finish();
        }

        private void Finish()
        {
            var ended = Current;
            if (ended == null) return;
            Current = null;
            BuilderServices.Waves.SetPending(null, null);
            if (PerkManager.instance != null)
            {
                PerkManager.instance.level = savedPerkLevel;
                PerkManager.instance.xp = savedPerkXp;
            }
            // The in-memory match save is the custom run's: never let a vanilla run continue from it.
            MatchSaveLoadHandler.OverwriteCurrentSave = true;
            // GameplayImpact keeps its record when the same map is re-entered without a fresh start (continuing a
            // save). The next run on the host is a different run, so make it count as a new level.
            Traverse.Create(typeof(GameplayImpact)).Field("level").SetValue(null);
            Log.For<BuilderManager>().Info($"Session ended: {ended.Pack.Id} on {ended.HostScene}");
            Ended?.Invoke(ended);
        }

        // ------------------------------------------------------------------ pre-level menu

        /// <summary>Checks a design and arms it for the pre-level menu of its host. Doesn't load anything.</summary>
        internal bool Arm(SessionRequest request, out string error)
        {
            if (!Resolve(request, out var waveSet, out var level, out error)) return false;
            var record = ScoreStore.Get(ScoreStore.Key(request.Pack.Id, level == null ? waveSet?.Id : null, level?.Id));
            Armed = new ArmedDesign
            {
                Request = request,
                Host = level?.Host ?? waveSet.Host,
                Name = level?.Name ?? waveSet?.Name ?? request.Pack.Name,
                Best = record?.Best?.Score,
            };
            Log.For<BuilderManager>().Debug($"Armed {request.Pack.Id}/{request.LevelId ?? request.WaveSetId} on {Armed.Host}");
            return true;
        }

        internal void Disarm()
        {
            if (Armed == null) return;
            Log.For<BuilderManager>().Debug("Disarmed");
            Armed = null;
        }

        /// <summary>The armed design's Play: starts the session for the transition the game is about to make.</summary>
        internal bool BeginArmed(out string error)
        {
            var armed = Armed;
            if (armed == null) { error = "nothing armed"; return false; }
            // Start() clears Armed on success; on failure the pre-level menu stays as it is.
            // Re-read the pack: it may have been edited since it was picked.
            var request = armed.Request;
            var fresh = BuilderServices.Packs.Find(request.Pack.Id)?.Pack;
            if (fresh == null) { error = $"{request.Pack.Name ?? request.Pack.Id} was removed or can't be read any more"; return false; }
            request = new SessionRequest { Pack = fresh, WaveSetId = request.WaveSetId, LevelId = request.LevelId };
            if (!Resolve(request, out var ws, out var lv, out error)) return false;
            if ((lv?.Host ?? ws.Host) != armed.Host) { error = "the design was changed to another map; pick it again"; return false; }
            return Start(request, out error);
        }

        // ------------------------------------------------------------------ results

        /// <summary>Called when the game moves from the map to its end screen (once per run).</summary>
        internal void RecordResult(int totalScore)
        {
            var s = Current;
            if (s == null || resultRecorded) return;
            resultRecorded = true;
            var run = new RunResult
            {
                Score = totalScore,
                Beaten = LocalGamestate.Instance != null && LocalGamestate.Instance.CurrentState == LocalGamestate.State.AfterMatchVictory,
                DateUtc = DateTime.UtcNow,
                Perks = PerkManager.instance?.CurrentlyEquipped.Where(e => e != null).Select(e => e.name).ToList() ?? [],
                Nights = (EnemySpawner.instance?.Wavenumber ?? -1) + 1,
            };
            var assists = GameplayImpact.Reasons.Where(r => r != ImpactReason).ToList();
            if (assists.Count > 0) run.Assists = assists;
            var previousBest = ScoreStore.Get(ScoreStore.Key(s))?.Best?.Score;
            var record = ScoreStore.Record(s, run);
            Log.For<BuilderManager>().Info($"Result {ScoreStore.Key(s)} score={run.Score} beaten={run.Beaten} nights={run.Nights} best={record.Best.Score} plays={record.Plays}");
            string best = previousBest == null || run.Score > previousBest ? "New custom best!" : $"Custom best: {record.Best.Score}.";
            Notify.Show($"{record.Name}: {run.Score}. {best}", NotifyKind.Info, 6f, "builder-result");
        }

        /// <summary>Restarting the map (retry, retry day) is a new attempt within the same session.</summary>
        internal void OnRestart() => resultRecorded = false;

        // ------------------------------------------------------------------ internals

        private bool Resolve(SessionRequest request, out WaveSetDef waveSet, out LevelDef level, out string error)
        {
            waveSet = null;
            level = null;
            error = null;
            if (!Feature<BuilderManager>.On) { error = "the Builder isn't available"; return false; }
            if (!GameplayFeatures.Allowed) { error = "custom runs are off while Settings > Mods > General > Gameplay-changing features is off"; return false; }
            var pack = request?.Pack;
            if (pack == null) { error = "no pack"; return false; }

            var errors = BuilderServices.Packs.Validate(pack).Where(i => i.Severity == Severity.Error).ToList();
            if (errors.Count > 0) { error = $"{pack.Name ?? pack.Id} has {errors.Count} error(s): {string.Join("; ", errors.Take(3).Select(e => e.Message))}"; return false; }

            if (request.LevelId != null)
            {
                level = pack.FindLevel(request.LevelId);
                if (level == null) { error = $"no level \"{request.LevelId}\" in {pack.Id}"; return false; }
                waveSet = level.WaveSet != null ? pack.FindWaveSet(level.WaveSet) : null;
            }
            else
            {
                waveSet = pack.FindWaveSet(request.WaveSetId);
                if (waveSet == null) { error = $"no wave set \"{request.WaveSetId}\" in {pack.Id}"; return false; }
            }
            string host = level?.Host ?? waveSet.Host;
            if (!LevelProgressManager.SceneExists(host)) { error = $"the map \"{host}\" isn't in this version of the game"; return false; }
            return true;
        }

        private bool Start(SessionRequest request, out string error)
        {
            if (!Resolve(request, out var waveSet, out var level, out error)) return false;
            var pack = request.Pack;

            // Build every template now so problems show up before the map loads.
            var problems = new List<string>();
            foreach (var sp in waveSet?.Nights.SelectMany(n => n.Spawns) ?? [])
                if (BuilderServices.Units.Resolve(pack, sp.Unit, out string e) == null) problems.Add(e);
            if (problems.Count > 0) { error = string.Join("; ", problems.Distinct().Take(3)); return false; }

            string host = level?.Host ?? waveSet.Host;
            // Replacing a running session (e.g. the editor's Test from the pause menu): the new one takes over the
            // isolation at once, and the transition that follows unloads the old map.
            if (Current != null) Finish();
            Armed = null;
            // Read the vanilla record before the session's stand-in replaces it.
            bool vanillaBeaten = LevelProgressManager.instance?.GetLevelDataForScene(host)?.beatenBest ?? false;
            Current = new BuilderSessionInfo
            {
                Pack = pack, WaveSet = waveSet, Level = level, HostScene = host, StartedUtc = DateTime.UtcNow,
                Scores = SeededScores(ScoreStore.Get(ScoreStore.Key(pack.Id, level == null ? waveSet?.Id : null, level?.Id)), vanillaBeaten),
            };
            VanillaBeaten = vanillaBeaten;
            resultRecorded = false;
            savedPerkLevel = PerkManager.instance?.level ?? 0;
            savedPerkXp = PerkManager.instance?.xp ?? 0;
            BuilderServices.Waves.SetPending(pack, waveSet);
            LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
            // Never continue the host's vanilla mid-match save; the session's own one stays in memory only. Point the
            // match-save context at the host (the pre-level menu does this for vanilla runs; launches from elsewhere
            // would leave it on the tutorial, where retrying a day is disabled).
            Traverse.Create(typeof(MatchSaveLoadHandler)).Field("currentMap").SetValue(host);
            Traverse.Create(typeof(MatchSaveLoadHandler)).Field("currentSave").SetValue(null);
            MatchSaveLoadHandler.OverwriteCurrentSave = true;
            Log.For<BuilderManager>().Info($"Session started: {pack.Id} " + (level != null ? $"level {level.Id}" : $"wave set {waveSet.Id}") + $" on {Current.HostScene}");
            Started?.Invoke(Current);
            return true;
        }

        /// <summary>The host's vanilla "beaten" state, which campaign unlocks (Equippable.IsUnlocked) read through
        /// the stand-in LevelData; kept so a custom run neither hides nor grants them.</summary>
        internal bool VanillaBeaten { get; private set; }

        /// <summary>A LevelData standing in for the host's, seeded with the design's custom best so the game's "new
        /// personal best" compares against custom runs only. beatenBest stays the vanilla value (see VanillaBeaten).</summary>
        private static LevelData SeededScores(DesignRecord record, bool vanillaBeaten) => new()
        {
            highscoreBest = record?.Best?.Score ?? 0,
            beatenBest = vanillaBeaten,
        };

        /// <summary>BuilderManager forwards SceneManager.sceneLoaded (single loads only).</summary>
        internal void OnSceneLoaded(Scene scene)
        {
            if (Current == null)
            {
                Armed = null;
                return;
            }
            if (scene.name != Current.HostScene)
            {
                Finish();
                return;
            }
            GameplayImpact.Mark(ImpactReason);
            if (Current.Level != null)
            {
                var issues = new List<ValidationIssue>();
                BuilderServices.Overlay.Apply(Current.Pack, Current.Level, issues);
                foreach (var i in issues) Log.For<BuilderManager>().Warn($"{Current.Pack.Id}/{Current.Level.Id}: {i}");
            }
            Notify.Show($"Custom run: {SessionName(Current)}. Kept apart from your saves and scores.", NotifyKind.Info, 5f, "builder-session");
        }

        internal static string SessionName(BuilderSessionInfo s) => s.Level?.Name ?? s.WaveSet?.Name ?? s.Pack.Name ?? s.Pack.Id;
    }
}
