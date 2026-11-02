using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Thronefall.SaveServices;
using TMPro;
using UnityEngine;

/*
    The no-restart score bonus (10%, 1% in mini modes) is lost when MatchSave.hadRestarts is set. The game sets it in
    ScoreManager.OnLoad on every load of a level's match save, whatever the reason: retrying a day after a defeat, or
    just quitting to the menu (or the desktop) and continuing later.

    The match save is only written at dawn (LocalMatchSaveLoad.Save, a few frames after sunrise), so a load always puts
    the player back at the start of a day. When nothing has happened since that save was written, loading it changes
    nothing and rerolls nothing, so it shouldn't cost the bonus. This feature keeps the bonus in exactly that case.

    How it decides:
    - When the dawn save is written, the session is "clean" and a sidecar file next to the saves records the save's
      content hash as bonus-safe. The native save file is never touched.
    - While the day goes on, the session turns dirty (and the sidecar entry is rewritten as not safe, immediately, so
      force-quitting doesn't help) on the first of: gold changing (spending it on anything), any value the save would
      record changing (buildings, upgrades, shrines, tags, free builds...; checked twice a second and on pause/quit),
      the night starting, or the match ending. Unit commands and walking around aren't recorded in the save and are
      undone by a load, so they don't count. Nothing random is decided during the day before one of those happens.
    - On load, if the loaded file's hash matches a clean sidecar entry for that level and the save didn't already
      carry hadRestarts, hadRestarts is put back to false.
    Anything unexpected (no sidecar, a different file, the feature off at the time) falls back to vanilla: bonus lost.
    Classic mode only; Eternal Trials has no no-restart bonus.
*/
namespace PalaceMod
{
    public class BonusSafeSaveManager : BaseManager
    {
        public override string FeatureSection => "Bonus-safe saves";
        public override string FeatureDescription => "Quitting at the very start of a day, before spending, building or starting the night, keeps your no-restart score bonus when you continue.";

        // Bookkeeping (binding the level, invalidating records) has to keep running while the feature is off, or a
        // save played on with the feature off could later be continued as if untouched. Behaviour is gated on On.
        protected override bool UnpatchWhenDisabled => false;

        private static bool On => Feature<BonusSafeSaveManager>.On;

        private const string SidecarFileName = "PalaceMod.BonusSafeSaves.json";
        private const float DiffInterval = 0.5f;

        private ConfigEntry<bool> pauseHint;

        private static readonly AccessTools.FieldRef<string> CurrentMap = AccessTools.StaticFieldRefAccess<string>(AccessTools.Field(typeof(MatchSaveLoadHandler), "currentMap"));
        private static readonly AccessTools.FieldRef<MatchSave> CurrentSaveRef = AccessTools.StaticFieldRefAccess<MatchSave>(AccessTools.Field(typeof(MatchSaveLoadHandler), "currentSave"));
        private static readonly AccessTools.FieldRef<MatchSave, Dictionary<string, object>> SavedEntities = AccessTools.FieldRefAccess<MatchSave, Dictionary<string, object>>("savedEntities");
        private static readonly AccessTools.FieldRef<LocalMatchSaveLoad, SaveLoadEntity[]> AllEntities = AccessTools.FieldRefAccess<LocalMatchSaveLoad, SaveLoadEntity[]>("allSaveLoadEntities");

        // The level and content hash of the match save currently in memory (it always equals the file after a load or a save).
        private static string memoryMap;
        private static string memoryHash;

        // The current level session.
        private static LocalMatchSaveLoad saveLoad;
        private static string sessionMap;
        private static bool sessionClean;
        private static bool pendingBaseline;
        private static string dirtyReason = "no save yet";
        private static int baselineBalance;
        private static Dictionary<string, string> baseline;
        private static float nextDiff;
        private static bool inDaySave;
        private static bool hadRestartsBeforeLoad;
        private static string loadOutcome;

        private static GameObject hintObject;
        private static TextMeshProUGUI hintText;

        private static BonusSafeSaveManager Instance => Feature<BonusSafeSaveManager>.Instance;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            pauseHint = plugin.Config.Bind(FeatureSection, "Pause menu hint", true, new ConfigDescription(
                "Shows in the pause menu whether quitting now keeps the no-restart bonus."));
            pauseHint.SettingChanged += (_, _) => { if (!pauseHint.Value) HideHint(); };
            Application.quitting += () => { if (IsEnabled) CheckNow(); };

            DevCommands.Register("bonus", "bonus [continue <scene>|fresh <scene>|quit]   bonus-safe save status, or continue/fresh-start a level or quit to the map like the menus do", args =>
            {
                if (args.Length == 0) return Status();
                switch (args[0].ToLowerInvariant())
                {
                    case "continue":
                    case "fresh":
                        if (args.Length < 2) throw new ArgumentException("usage: bonus continue|fresh <scene>");
                        string scene = string.Join(" ", args.Skip(1));
                        MatchSaveLoadHandler.TryLoadRun(scene);
                        MatchSaveLoadHandler.OverwriteCurrentSave = args[0].Equals("fresh", StringComparison.OrdinalIgnoreCase);
                        LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
                        (SceneTransitionManager.instance ?? throw new InvalidOperationException("no SceneTransitionManager")).TransitionFromNullToLevel(scene);
                        return $"save in memory: {(MatchSaveLoadHandler.CurrentSave != null ? "yes" : "no")}, overwrite: {MatchSaveLoadHandler.OverwriteCurrentSave}";
                    case "quit":
                        (SceneTransitionManager.instance ?? throw new InvalidOperationException("no SceneTransitionManager")).TransitionFromLevelToLevelSelect();
                        return "";
                    default:
                        throw new ArgumentException("usage: bonus [continue <scene>|fresh <scene>|quit]");
                }
            });
        }

        protected override void OnFeatureEnabled()
        {
            // Whatever happened while the feature was off is unknown: stay unsafe until the next dawn save.
            MarkDirty("feature was off");
        }

        protected override void OnFeatureDisabled()
        {
            // The rest of this day isn't tracked, so its save can't be vouched for any more.
            MarkDirty("feature was off");
            HideHint();
        }

        public override void Update()
        {
            if (saveLoad == null || !sessionClean) return;
            if (pendingBaseline)
            {
                if (LocalMatchSaveLoad.CurrentlyLoadingMatch || PlayerInteraction.instance == null) return;
                TakeBaseline();
                return;
            }
            CheckCheap();
            if (sessionClean && Time.unscaledTime >= nextDiff)
            {
                nextDiff = Time.unscaledTime + DiffInterval;
                CheckDiff();
            }
        }

        // ---- Tracking ----

        private static bool Applicable =>
            LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.Classic && !MatchSaveLoadHandler.SaveLoadForbidden;

        private static void CheckNow()
        {
            if (saveLoad == null || !sessionClean || pendingBaseline) return;
            CheckCheap();
            if (sessionClean) CheckDiff();
        }

        private static void CheckCheap()
        {
            var cycle = DayNightCycle.Instance;
            if (cycle != null && cycle.CurrentTimestate != DayNightCycle.Timestate.Day) { MarkDirty("the night started"); return; }
            var state = LocalGamestate.Instance;
            if (state != null && state.CurrentState != LocalGamestate.State.InMatch) { MarkDirty("the match ended"); return; }
            var player = PlayerInteraction.instance;
            if (player != null && player.TrueBalance != baselineBalance)
                MarkDirty(player.TrueBalance < baselineBalance ? "gold was spent" : "gold was gained");
        }

        private static void CheckDiff()
        {
            var now = Snapshot();
            if (now == null || baseline == null) return;
            foreach (var kv in now)
            {
                if (!baseline.TryGetValue(kv.Key, out var before)) { MarkDirty($"{kv.Key} appeared"); return; }
                if (before != kv.Value) { MarkDirty($"{kv.Key} changed from {before} to {kv.Value}"); return; }
            }
            foreach (var key in baseline.Keys)
            {
                if (!now.ContainsKey(key)) { MarkDirty($"{key} disappeared"); return; }
            }
        }

        private static void TakeBaseline()
        {
            pendingBaseline = false;
            baseline = Snapshot();
            baselineBalance = SavedBalance() ?? PlayerInteraction.instance?.TrueBalance ?? 0;
            nextDiff = Time.unscaledTime + DiffInterval;
            if (baseline == null) MarkDirty("couldn't read the game state");
            else Plugin.Logger.LogInfo($"Bonus-safe saves: tracking {sessionMap} from a clean start of day ({baseline.Count} saved values, {baselineBalance} gold)");
        }

        private static void MarkDirty(string reason)
        {
            if (!sessionClean) return;
            sessionClean = false;
            pendingBaseline = false;
            dirtyReason = reason;
            Plugin.Logger.LogInfo($"Bonus-safe saves: {sessionMap} is no longer bonus-safe: {reason}");
            if (sessionMap != null && memoryMap == sessionMap && memoryHash != null)
                WriteEntry(sessionMap, memoryHash, false, reason);
        }

        // Runs every OnSave into a scratch MatchSave, i.e. exactly what a save written now would record.
        private static Dictionary<string, string> Snapshot()
        {
            if (saveLoad == null) return null;
            var entities = AllEntities(saveLoad);
            if (entities == null) return null;
            var real = CurrentSaveRef();
            var scratch = new MatchSave();
            CurrentSaveRef() = scratch;
            try
            {
                foreach (var entity in entities)
                {
                    if (entity != null) entity.ExecuteSave();
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Bonus-safe saves: reading the game state failed: {e.Message}");
                return null;
            }
            finally
            {
                CurrentSaveRef() = real;
            }
            var result = new Dictionary<string, string>();
            foreach (var kv in SavedEntities(scratch))
            {
                if (Volatile(kv.Key)) continue;
                result[kv.Key] = Canonical(kv.Value);
            }
            return result;
        }

        // Values that drift on their own during the day without the player doing anything.
        private static bool Volatile(string key) => key.EndsWith("_hp", StringComparison.Ordinal) || key.EndsWith("_researchTimeLeft", StringComparison.Ordinal);

        private static string Canonical(object value) => value switch
        {
            null => "null",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            int[] a => string.Join(",", a),
            TagManager.ETag[] t => string.Join(",", t.Select(x => (int)x)),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

        private static int? SavedBalance()
        {
            var save = MatchSaveLoadHandler.CurrentSave;
            var entity = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponentInParent<SaveLoadEntity>() : null;
            if (save == null || entity == null) return null;
            int balance = 0;
            return MatchSaveLoadHandler.TryLoadValue(entity.GUID, "balance", ref balance) ? balance : null;
        }

        // ---- Patches ----

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.TryLoadRun))]
        private class TryLoadRun_Patch
        {
            private static void Postfix(string mapName)
            {
                memoryMap = mapName;
                memoryHash = MatchSaveLoadHandler.CurrentSave != null ? HashSaveFile(mapName) : null;
            }
        }

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "Save")]
        private class Save_Patch
        {
            private static void Prefix() => inDaySave = true;
            private static void Finalizer() => inDaySave = false;
        }

        [HarmonyPatch(typeof(MatchSaveLoadHandler), nameof(MatchSaveLoadHandler.SaveRun))]
        private class SaveRun_Patch
        {
            private static void Postfix()
            {
                string map = CurrentMap();
                string hash = HashSaveFile(map);
                memoryMap = map;
                memoryHash = hash;
                if (hash == null) return;
                if (inDaySave && On && saveLoad != null && map == sessionMap && Applicable && SaveWritten(map))
                {
                    WriteEntry(map, hash, true, null);
                    sessionClean = true;
                    dirtyReason = null;
                    pendingBaseline = false;
                    TakeBaseline();
                    Plugin.Logger.LogInfo($"Bonus-safe saves: day {EnemySpawner.instance?.Wavenumber + 2} of {map} saved; bonus-safe until something changes");
                }
                else
                {
                    sessionClean = false;
                    WriteEntry(map, hash, false, !inDaySave ? "the match ended" : !On ? "saved while the feature was off" : "not tracked");
                }
            }
        }

        [HarmonyPatch(typeof(ScoreManager), nameof(ScoreManager.OnLoad))]
        private class ScoreManager_OnLoad_Patch
        {
            private static void Prefix() => hadRestartsBeforeLoad = MatchSaveLoadHandler.CurrentSave?.hadRestarts ?? true;

            private static void Postfix()
            {
                var save = MatchSaveLoadHandler.CurrentSave;
                if (save == null || !Applicable) return;
                string map = CurrentMap();
                var entry = ReadSidecar().entries.FirstOrDefault(e => e.map == map);
                bool safe = entry != null && entry.clean && memoryMap == map && memoryHash != null && entry.hash == memoryHash
                    && HashSaveFile(map) == memoryHash;
                if (!On)
                {
                    loadOutcome = "lost (feature off)";
                }
                else if (hadRestartsBeforeLoad)
                {
                    loadOutcome = "already lost";
                }
                else if (safe)
                {
                    save.hadRestarts = false;
                    loadOutcome = "kept";
                }
                else
                {
                    loadOutcome = entry == null || entry.hash != memoryHash ? "lost (no bonus-safe record for this save)" : $"lost ({entry.reason ?? "not bonus-safe"})";
                }
                Plugin.Logger.LogInfo($"Bonus-safe saves: loaded {map}; no-restart bonus {loadOutcome}");
            }
        }

        [HarmonyPatch(typeof(LocalMatchSaveLoad), "Awake")]
        private class LocalMatchSaveLoad_Awake_Patch
        {
            private static void Postfix(LocalMatchSaveLoad __instance)
            {
                if (__instance.disableAutomaticSaveLoading) return;
                saveLoad = __instance;
                sessionMap = CurrentMap();
                baseline = null;
                sessionClean = false;
                pendingBaseline = false;
                dirtyReason = "no save yet";
                if (!Applicable) return;
                if (!On)
                {
                    // Played with the feature off: whatever happens this session isn't tracked.
                    var stale = ReadSidecar().entries.FirstOrDefault(e => e.map == sessionMap);
                    if (stale != null && stale.clean) WriteEntry(stale.map, stale.hash, false, "played while the feature was off");
                    loadOutcome = null;
                    return;
                }
                if (LocalMatchSaveLoad.CurrentlyLoadingMatch)
                {
                    if (loadOutcome == "kept")
                    {
                        sessionClean = true;
                        pendingBaseline = true;
                        dirtyReason = null;
                        Notify.Show("No-restart bonus kept: you continued at the start of the day.", NotifyKind.Success, 4f, "bonus-safe");
                    }
                    else
                    {
                        dirtyReason = "this save wasn't bonus-safe";
                        if (loadOutcome != null && loadOutcome.StartsWith("lost", StringComparison.Ordinal))
                            Notify.Show("No-restart bonus lost: this day had already been played on before quitting.", NotifyKind.Warning, 5f, "bonus-safe");
                    }
                }
                else
                {
                    // A new run: an older save of this level, if any, belongs to an abandoned run and isn't safe to go back to.
                    var entry = ReadSidecar().entries.FirstOrDefault(e => e.map == sessionMap);
                    if (entry != null && entry.clean) WriteEntry(entry.map, entry.hash, false, "a new run was started");
                }
                loadOutcome = null;
            }
        }

        // Leaving the level (to the map, the menu or a restart): check precisely before the scene goes away.
        [HarmonyPatch(typeof(SceneTransitionManager), "TransitionToScene")]
        private class TransitionToScene_Patch
        {
            private static void Prefix() => CheckNow();
        }

        [HarmonyPatch(typeof(PauseUILoadoutHelper), "OnEnable")]
        private class Pause_OnEnable_Patch
        {
            private static void Postfix(PauseUILoadoutHelper __instance)
            {
                if (!On) return;
                CheckNow();
                Instance?.ShowHint(__instance);
            }
        }

        // ---- Pause menu hint ----

        private void ShowHint(PauseUILoadoutHelper pause)
        {
            string text = HintText();
            if (!pauseHint.Value || text == null || pause == null || pause.backToMenuElement == null) { HideHint(); return; }
            if (hintText == null || hintObject == null || hintObject.transform.parent != pause.transform) BuildHint(pause);
            hintText.text = text;
            hintObject.SetActive(true);
        }

        private static string HintText()
        {
            var save = MatchSaveLoadHandler.CurrentSave;
            if (saveLoad == null || !Applicable || save == null || save.hadRestarts) return null;
            // No save for this run yet (day 1): quitting just starts the level over, which costs nothing.
            if (memoryMap != sessionMap || memoryHash == null || (EnemySpawner.instance != null && EnemySpawner.instance.Wavenumber < 0)) return null;
            if (sessionClean) return "<color=#9fd08a>Bonus-safe:</color> nothing has changed since this day began, so you can quit and continue later without losing the no-restart bonus.";
            return $"<color=#e0a060>Quitting now forfeits the no-restart bonus</color> ({dirtyReason ?? "this day is under way"}).";
        }

        private static void BuildHint(PauseUILoadoutHelper pause)
        {
            HideHint();
            if (hintObject != null) UnityEngine.Object.Destroy(hintObject);
            var source = pause.nightPreviewText;
            hintObject = new GameObject("PalaceMod BonusSafeHint", typeof(RectTransform));
            hintObject.transform.SetParent(pause.transform, false);
            var rect = (RectTransform)hintObject.transform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 40f);
            rect.sizeDelta = new Vector2(1100f, 60f);
            hintText = hintObject.AddComponent<TextMeshProUGUI>();
            if (source != null)
            {
                hintText.font = source.font;
                hintText.fontSharedMaterial = source.fontSharedMaterial;
                hintText.color = source.color;
            }
            hintText.fontSize = 24;
            hintText.alignment = TextAlignmentOptions.Center;
            hintText.enableWordWrapping = true;
            hintText.raycastTarget = false;
        }

        private static void HideHint()
        {
            if (hintObject != null) hintObject.SetActive(false);
        }

        // ---- Sidecar ----

        [Serializable]
        private class SidecarEntry
        {
            public string map;
            public string hash;
            public bool clean;
            public string reason;
            public string written;
        }

        [Serializable]
        private class Sidecar
        {
            public int version = 1;
            public List<SidecarEntry> entries = [];
        }

        private static string SidecarPath => SaveServicesLocator.Instance.GetFullSavePath(SidecarFileName);

        private static Sidecar ReadSidecar()
        {
            try
            {
                if (File.Exists(SidecarPath))
                    return JsonUtility.FromJson<Sidecar>(File.ReadAllText(SidecarPath)) ?? new Sidecar();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Bonus-safe saves: couldn't read {SidecarFileName}, treating every save as not bonus-safe: {e.Message}");
            }
            return new Sidecar();
        }

        private static void WriteEntry(string map, string hash, bool clean, string reason)
        {
            try
            {
                var sidecar = ReadSidecar();
                sidecar.entries ??= [];
                sidecar.entries.RemoveAll(e => e == null || e.map == map);
                sidecar.entries.Add(new SidecarEntry { map = map, hash = hash, clean = clean, reason = reason, written = DateTime.UtcNow.ToString("o") });
                string path = SidecarPath;
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(sidecar, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Bonus-safe saves: couldn't write {SidecarFileName}: {e.Message}");
            }
        }

        // SaveRun swallows write failures, so check the file now holds exactly what it was asked to write.
        private static bool SaveWritten(string map)
        {
            try
            {
                var services = SaveServicesLocator.Instance;
                string path = services.GetFullSavePath(map + ".json");
                return services.Exists(path) && services.ReadAllText(path) == JsonUtility.ToJson(MatchSaveLoadHandler.CurrentSave);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string HashSaveFile(string map)
        {
            try
            {
                var services = SaveServicesLocator.Instance;
                string path = services.GetFullSavePath(map + ".json");
                if (!services.Exists(path)) return null;
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(services.ReadAllText(path)));
                return string.Concat(bytes.Select(b => b.ToString("x2")));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Bonus-safe saves: couldn't read the save for {map}: {e.Message}");
                return null;
            }
        }

        private static string Status()
        {
            var save = MatchSaveLoadHandler.CurrentSave;
            string map = CurrentMap();
            var entry = ReadSidecar().entries.FirstOrDefault(e => e.map == map);
            string fileHash = HashSaveFile(map);
            return string.Join("\n", new[]
            {
                $"map: {map}  session: {sessionMap ?? "-"}  in level: {saveLoad != null}",
                $"hadRestarts: {(save == null ? "no save in memory" : save.hadRestarts.ToString())}",
                $"session: {(pendingBaseline ? "clean (settling)" : sessionClean ? "clean" : "dirty: " + dirtyReason)}",
                $"save file: {Short(fileHash)}  in memory: {Short(memoryHash)} ({memoryMap ?? "-"})",
                $"sidecar: {(entry == null ? "none" : $"{(entry.clean ? "clean" : "dirty: " + entry.reason)} for {Short(entry.hash)}")}",
                $"hint: {HintText() ?? "(none)"}",
            });
        }

        private static string Short(string hash) => hash == null ? "-" : hash.Substring(0, 8);
    }
}
