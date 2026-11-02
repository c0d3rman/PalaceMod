using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Save profiles: named, complete sets of the game's progression that the player switches between from the main
    menu (Profiles button), plus "reset progress" for the current profile. ProfileStore does the file work and its
    safety (backups, journal, crash recovery); this manager reloads the game's in-memory progression after a change,
    owns the dev commands and the menu (ProfilesMenu).

    Changes only happen on the main menu with no scene transition running: there the game has saved everything (it
    saves at the end of each match) and nothing is mid-run, so after swapping the files the progression singletons
    are reset and re-read from disk and the menu scene is reloaded.
*/
namespace PalaceMod.Profiles
{
    public class ProfilesManager : BaseManager
    {
        public override string FeatureSection => "Profiles";
        public override string FeatureDescription =>
            "Adds a Profiles button to the main menu: keep separate progressions, start fresh, and switch between them. Every change is backed up first.";

        public ProfileStore Store { get; private set; }

        /// <summary>Why profile changes are unavailable (the store failed to load), or null.</summary>
        public string Broken { get; private set; }

        private string recoveryMessage;
        private bool reloadPending;
        private ProfilesMenu menu;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            string live = Application.persistentDataPath;
            string root = Path.Combine(Path.GetDirectoryName(live), Path.GetFileName(live) + " PalaceMod Profiles");
            Store = new ProfileStore(live, root, new PlayerPrefsAdapter(), msg => Log.Info(msg.StartsWith("Profiles: ") ? msg.Substring("Profiles: ".Length) : msg));
            try
            {
                // Before the game reads its save, so an interrupted switch is resolved before anything loads it.
                recoveryMessage = Store.RecoverIfNeeded();
                reloadPending = recoveryMessage != null;
                if (Store.Initialized) Log.Info($"\"{Store.Active.name}\" is active ({Store.Profiles.Count} profiles)");
            }
            catch (Exception e)
            {
                Broken = e.Message;
                Log.Error($"Unavailable, the save folder was left alone", e);
            }
            menu = new ProfilesMenu(this);
            RegisterDevCommands();
        }

        public override void Update()
        {
            if (!OnMainMenu) return;
            if (reloadPending && LevelProgressManager.instance != null && SaveLoadManager.instance != null)
            {
                reloadPending = false;
                ReloadGameState();
            }
            if (recoveryMessage != null && Time.unscaledTime > 5f)
            {
                Notify.Show(recoveryMessage, NotifyKind.Warning, 8f, "profiles");
                recoveryMessage = null;
            }
            menu.Update();
        }

        protected override void OnFeatureDisabled() => menu.Remove();

        /// <summary>True on the idle main menu, the only place profiles can change.</summary>
        public static bool OnMainMenu =>
            SceneTransitionManager.instance != null
            && SceneTransitionManager.instance.CurrentSceneState == SceneTransitionManager.SceneState.MainMenu
            && SceneManager.GetSceneByName("_StartMenu").isLoaded;

        public static bool CanChange =>
            OnMainMenu && SceneTransitionManager.instance != null && !SceneTransitionManager.instance.SceneTransitionIsRunning;

        // ---- Operations used by the menu and dev commands ---------------------------------------------------------

        private void RequireChangeable()
        {
            if (Broken != null) throw new InvalidOperationException($"profiles are unavailable: {Broken}");
            if (!CanChange) throw new InvalidOperationException("profiles can only be changed on the main menu");
        }

        public void SwitchTo(ProfileStore.Profile profile)
        {
            RequireChangeable();
            Guarded(() => Store.Switch(profile.id));
            ReloadGameState();
        }

        public void ResetActive()
        {
            RequireChangeable();
            Guarded(() => Store.ResetActive());
            ReloadGameState();
        }

        // A failed change was rolled back or, past its commit point, finished by the store; either way re-read the files
        // so the game's progression matches the active profile. If that recovery failed too, stop offering changes.
        private void Guarded(Action change)
        {
            try { change(); }
            catch (ProfileStore.OperationFailedException e)
            {
                Log.Error(e.Message, e.InnerException);
                if (Store.Pending) Broken = e.Message;
                ReloadGameState();
                throw new InvalidOperationException(e.Message);
            }
        }

        /// <summary>Most profiles the menu lists; enough for anyone switching by hand, and it keeps the list on screen.</summary>
        public const int MaxProfiles = 6;

        private void RequireRoom()
        {
            if (Store.Profiles.Count >= MaxProfiles) throw new InvalidOperationException($"you have {MaxProfiles} profiles; delete one to make another");
        }

        /// <summary>Creates a fresh profile and switches to it.</summary>
        public ProfileStore.Profile CreateAndSwitch(string name)
        {
            RequireChangeable();
            RequireRoom();
            var profile = Store.CreateFresh(name ?? Store.UniqueName("Profile"));
            SwitchTo(profile);
            return profile;
        }

        public ProfileStore.Profile Duplicate(ProfileStore.Profile source, string name = null)
        {
            RequireChangeable();
            RequireRoom();
            return Store.Duplicate(source.id, name ?? Store.UniqueName(source.name + " copy"));
        }

        public void Rename(ProfileStore.Profile profile, string name)
        {
            RequireChangeable();
            Store.Rename(profile.id, name);
        }

        public void Delete(ProfileStore.Profile profile)
        {
            RequireChangeable();
            Store.Delete(profile.id);
        }

        /// <summary>Resets the progression singletons the save file feeds, re-reads it, and reloads the main menu.</summary>
        private void ReloadGameState()
        {
            var levels = LevelProgressManager.instance;
            levels.SceneNameToLevelData.Clear();
            levels.tipsShown.Clear();
            levels.EternalTrialsHighscore = 0;
            if (PerkManager.instance != null)
            {
                PerkManager.ClearAllEquipped();
                PerkManager.instance.level = 1;
                PerkManager.instance.xp = 0;
            }
            MatchSaveLoadHandler.SetToTutorialMap();
            MatchSaveLoadHandler.OverwriteCurrentSave = false;
            // The Eternal Trials run is cached statically and re-read from its file on next use.
            AccessTools.Field(typeof(EternalTrialsRunManager), "currentRun").SetValue(null, null);
            AccessTools.Field(typeof(EternalTrialsRunManager), "lastDiscardedRun").SetValue(null, null);
            AccessTools.Field(typeof(EternalTrialsRunManager), "freshRunSeed").SetValue(null, 0);
            AccessTools.Method(typeof(SaveLoadManager), "LoadGame").Invoke(SaveLoadManager.instance, [false]);
            Log.Info($"Loaded \"{Store.Active.name}\": perk level {PerkManager.instance?.level}, {levels.SceneNameToLevelData.Count} levels with progress");
            menu.Close();
            SceneTransitionManager.instance.TransitionToMainMenu();
        }

        // While a profile name is being typed, the game's menus mustn't read the keys (WASD navigates, Esc closes).
        [HarmonyPatch(typeof(UIFrame), "Update")]
        private static class UIFrame_Update_Patch
        {
            private static bool Prefix() => !ProfilesMenu.Typing;
        }

        [HarmonyPatch(typeof(UIFrameManager), "Update")]
        private static class UIFrameManager_Update_Patch
        {
            private static bool Prefix() => !ProfilesMenu.Typing;
        }

        // ---- PlayerPrefs ------------------------------------------------------------------------------------------

        /// <summary>The PlayerPrefs that are progression: the wardrobe (the game's reset clears it too) and the Eternal
        /// Trials seed that goes with the Eternal Trials run file. All ints.</summary>
        private sealed class PlayerPrefsAdapter : IProfilePrefs
        {
            private static readonly string[] Keys =
            [
                "wardrobeItem_Crown", "wardrobeItem_Armour", "wardrobeItem_Cape", "wardrobeItem_Mount",
                "Material_Crown", "Material_Armour", "Material_Cape", "Material_Mount",
                "EternalTrials_FreshRunSeed",
            ];

            public List<ProfileStore.PrefValue> Read() =>
                Keys.Select(k => new ProfileStore.PrefValue { key = k, present = PlayerPrefs.HasKey(k), value = PlayerPrefs.GetInt(k) }).ToList();

            public void Write(List<ProfileStore.PrefValue> values)
            {
                foreach (var v in values.Where(v => Keys.Contains(v.key)))
                {
                    if (v.present) PlayerPrefs.SetInt(v.key, v.value);
                    else PlayerPrefs.DeleteKey(v.key);
                }
                PlayerPrefs.Save();
            }
        }

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private ProfileStore.Profile Named(string[] args, int from = 1)
        {
            string name = string.Join(" ", args.Skip(from));
            return Store.Find(name) ?? throw new ArgumentException($"no profile \"{name}\"");
        }

        private void RegisterDevCommands()
        {
            DevCommands.Register("profiles", "profiles [list|new <name>|switch <name>|dup <name>|rename <name>=<new>|delete <name>|reset|open|crash <stage> <name>]   save profiles", args =>
            {
                string op = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
                switch (op)
                {
                    case "list":
                        return $"root: {Store.Root}\n" + string.Join("\n", Store.Profiles.Select(p =>
                            $"{(p.id == Store.Active.id ? "*" : " ")} {p.name} [{p.id}]")) + $"\nlive files: {string.Join(", ", Store.LiveFiles().Select(Path.GetFileName))}" +
                            $"\nperk level {PerkManager.instance?.level}, xp {PerkManager.instance?.xp}, levels {LevelProgressManager.instance?.SceneNameToLevelData.Count}";
                    case "new":
                        return $"switched to new profile \"{CreateAndSwitch(args.Length > 1 ? string.Join(" ", args.Skip(1)) : null).name}\"";
                    case "switch":
                        SwitchTo(Named(args));
                        return $"switched to \"{Store.Active.name}\"";
                    case "dup":
                        return $"created \"{Duplicate(Named(args)).name}\"";
                    case "rename":
                        var parts = string.Join(" ", args.Skip(1)).Split('=');
                        if (parts.Length != 2) throw new ArgumentException("usage: profiles rename <name>=<new name>");
                        Rename(Store.Find(parts[0].Trim()) ?? throw new ArgumentException($"no profile \"{parts[0]}\""), parts[1]);
                        return "renamed";
                    case "delete":
                        Delete(Named(args));
                        return "deleted";
                    case "reset":
                        ResetActive();
                        return $"reset \"{Store.Active.name}\"";
                    case "open":
                        menu.Open();
                        return "opened";
                    case "crash":
                        // Simulates the game dying at a stage of the next switch, then the startup recovery.
                        if (args.Length < 3) throw new ArgumentException("usage: profiles crash <backedup|midapply|applied> <name>");
                        RequireChangeable();
                        Store.CrashAt = args[1];
                        try { Store.Switch(Named(args, 2).id); }
                        catch (ProfileStore.SimulatedCrash) { }
                        finally { Store.CrashAt = null; }
                        string recovered = Store.RecoverIfNeeded() ?? "nothing to recover";
                        ReloadGameState();
                        return $"{recovered}; active: \"{Store.Active.name}\"";
                    default:
                        throw new ArgumentException($"unknown profiles command \"{op}\"");
                }
            });
        }
    }
}
