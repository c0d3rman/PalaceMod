using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine.SceneManagement;

/*
    Minimal custom-session launcher (WP2 interim; WP3 owns this file and builds the Custom menu, results screen and
    builder score store on top of it). A session is one custom run on a host map:

    - Launch validates the pack, pre-builds its unit templates, queues the wave set and loads the host.
    - While a session is active, BuilderManager.Session.cs isolates it from the player's progress: the host's
      LevelData is swapped for Current.Scores, the save file, mid-match save file and achievements aren't written,
      and perk XP/level are restored when the session ends.
    - Every custom run is marked with GameplayImpact on load.
    - The session ends when any scene other than the host loads (menu, level select, another map).
*/
namespace PalaceMod.Builder
{
    public sealed class SessionLauncher : ISessionLauncher
    {
        public const string ImpactReason = "Builder: custom content";

        public BuilderSessionInfo Current { get; private set; }
        public event Action<BuilderSessionInfo> Started;
        public event Action<BuilderSessionInfo> Ended;

        private int savedPerkLevel, savedPerkXp;

        public bool Launch(SessionRequest request, out string error)
        {
            error = null;
            if (!Feature<BuilderManager>.On) { error = "the Builder is turned off in Settings > Mods"; return false; }
            var pack = request?.Pack;
            if (pack == null) { error = "no pack"; return false; }
            var transitions = SceneTransitionManager.instance;
            if (transitions == null || transitions.SceneTransitionIsRunning) { error = "can't start a level right now"; return false; }

            var errors = BuilderServices.Packs.Validate(pack).Where(i => i.Severity == Severity.Error).ToList();
            if (errors.Count > 0) { error = $"{pack.Id} has {errors.Count} error(s): {string.Join("; ", errors.Take(3))}"; return false; }

            LevelDef level = null;
            WaveSetDef waveSet;
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

            // Build every template now so problems show up before the map loads.
            var problems = new List<string>();
            foreach (var sp in waveSet?.Nights.SelectMany(n => n.Spawns) ?? [])
                if (BuilderServices.Units.Resolve(pack, sp.Unit, out string e) == null) problems.Add(e);
            if (problems.Count > 0) { error = string.Join("; ", problems.Distinct().Take(3)); return false; }

            if (Current != null) End();
            Current = new BuilderSessionInfo
            {
                Pack = pack, WaveSet = waveSet, Level = level, HostScene = host, StartedUtc = DateTime.UtcNow,
                Scores = new LevelData(),
            };
            savedPerkLevel = PerkManager.instance?.level ?? 0;
            savedPerkXp = PerkManager.instance?.xp ?? 0;
            BuilderServices.Waves.SetPending(pack, waveSet);
            LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
            // Never continue the host's vanilla mid-match save; the session's own one stays in memory only.
            MatchSaveLoadHandler.OverwriteCurrentSave = true;
            Plugin.Logger.LogInfo($"Builder: session started: {pack.Id} " + (level != null ? $"level {level.Id}" : $"wave set {waveSet.Id}") + $" on {host}");
            Started?.Invoke(Current);
            transitions.TransitionFromNullToLevel(host);
            return true;
        }

        public void End()
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
            Plugin.Logger.LogInfo($"Builder: session ended: {ended.Pack.Id} on {ended.HostScene}");
            Ended?.Invoke(ended);
        }

        /// <summary>BuilderManager forwards SceneManager.sceneLoaded (single loads only).</summary>
        internal void OnSceneLoaded(Scene scene)
        {
            if (Current == null) return;
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
            Notify.Show($"Custom run: {Current.WaveSet?.Name ?? Current.Level?.Name ?? Current.Pack.Name}. Scores and saves aren't recorded.", NotifyKind.Info, 5f, "builder-session");
        }
    }
}
