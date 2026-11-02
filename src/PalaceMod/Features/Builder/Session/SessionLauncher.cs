using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine.SceneManagement;

/*
    Custom sessions (WP3). A session is one custom run of a design (a pack's wave set or level) on its host map.

    Two ways in:
    - Launch(request): validates, starts the session and loads the host straight away (editor "Test", `builder play`).
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
            /// <summary>Shown by the pre-level menu (custom best) and handed to the session at launch.</summary>
            public LevelData Scores;
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
            if (!Start(request, null, out error)) return false;
            transitions.TransitionFromNullToLevel(Current.HostScene);
            return true;
        }

        public void End()
        {
            Armed = null;
            var ended = Current;
            if (ended == null) return;
            Current = null;
            BuilderServices.Waves.SetPending(null, null);
            if (PerkManager.instance != null)
            {
                PerkManager.instance.level = savedPerkLevel;
                PerkManager.instance.xp = savedPerkXp;
            }
            Plugin.Logger.LogInfo($"Builder: session ended: {ended.Pack.Id} on {ended.HostScene}");
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
                Scores = SeededScores(record),
            };
            Plugin.Logger.LogInfo($"Builder: armed {request.Pack.Id}/{request.LevelId ?? request.WaveSetId} on {Armed.Host}");
            return true;
        }

        internal void Disarm()
        {
            if (Armed == null) return;
            Plugin.Logger.LogInfo("Builder: disarmed");
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
            if (fresh != null) request = new SessionRequest { Pack = fresh, WaveSetId = request.WaveSetId, LevelId = request.LevelId };
            return Start(request, armed.Scores, out error);
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
                Nights = EnemySpawner.instance?.Wavenumber ?? 0,
            };
            var assists = GameplayImpact.Reasons.Where(r => r != ImpactReason).ToList();
            if (assists.Count > 0) run.Assists = assists;
            var previousBest = ScoreStore.Get(ScoreStore.Key(s))?.Best?.Score;
            var record = ScoreStore.Record(s, run);
            Plugin.Logger.LogInfo($"Builder: result {ScoreStore.Key(s)} score={run.Score} beaten={run.Beaten} nights={run.Nights} best={record.Best.Score} plays={record.Plays}");
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
            if (!Feature<BuilderManager>.On) { error = "the Builder is turned off in Settings > Mods"; return false; }
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

        private bool Start(SessionRequest request, LevelData scores, out string error)
        {
            if (!Resolve(request, out var waveSet, out var level, out error)) return false;
            var pack = request.Pack;

            // Build every template now so problems show up before the map loads.
            var problems = new List<string>();
            foreach (var sp in waveSet?.Nights.SelectMany(n => n.Spawns) ?? [])
                if (BuilderServices.Units.Resolve(pack, sp.Unit, out string e) == null) problems.Add(e);
            if (problems.Count > 0) { error = string.Join("; ", problems.Distinct().Take(3)); return false; }

            if (Current != null) End();
            Armed = null;
            Current = new BuilderSessionInfo
            {
                Pack = pack, WaveSet = waveSet, Level = level, HostScene = level?.Host ?? waveSet.Host, StartedUtc = DateTime.UtcNow,
                Scores = scores ?? SeededScores(ScoreStore.Get(ScoreStore.Key(pack.Id, level == null ? waveSet?.Id : null, level?.Id))),
            };
            resultRecorded = false;
            savedPerkLevel = PerkManager.instance?.level ?? 0;
            savedPerkXp = PerkManager.instance?.xp ?? 0;
            BuilderServices.Waves.SetPending(pack, waveSet);
            LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
            // Never continue the host's vanilla mid-match save; the session's own one stays in memory only.
            MatchSaveLoadHandler.OverwriteCurrentSave = true;
            Plugin.Logger.LogInfo($"Builder: session started: {pack.Id} " + (level != null ? $"level {level.Id}" : $"wave set {waveSet.Id}") + $" on {Current.HostScene}");
            Started?.Invoke(Current);
            return true;
        }

        /// <summary>A LevelData standing in for the host's, seeded with the design's custom best so the game's "new
        /// personal best" and highscore displays compare against custom runs only.</summary>
        private static LevelData SeededScores(DesignRecord record) => new()
        {
            highscoreBest = record?.Best?.Score ?? 0,
            beatenBest = record?.EverBeaten ?? false,
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
                End();
                return;
            }
            GameplayImpact.Mark(ImpactReason);
            if (Current.Level != null)
            {
                var issues = new List<ValidationIssue>();
                BuilderServices.Overlay.Apply(Current.Pack, Current.Level, issues);
                foreach (var i in issues) Plugin.Logger.LogWarning($"Builder: {Current.Pack.Id}/{Current.Level.Id}: {i}");
            }
            Notify.Show($"Custom run: {SessionName(Current)}. Kept apart from your saves and scores.", NotifyKind.Info, 5f, "builder-session");
        }

        internal static string SessionName(BuilderSessionInfo s) => s.Level?.Name ?? s.WaveSet?.Name ?? s.Pack.Name ?? s.Pack.Id;
    }
}
