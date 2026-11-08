using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Test play (docs/builder-ux.md, "Play mode"): the Studio's edit -> play -> edit loop.

    One Studio session spans both states. Each switch reloads the same host through SessionLauncher.LaunchStudio,
    which keeps the session (no Ended, the editor's return point stays) and only swaps what the load is for:
    - Play: a test pack built in memory from the draft (this level, its wave set, the units that set uses; nothing
      saved). Studio.NextLoad = Play, so the shell doesn't claim the load and the overlay is applied as for a real run.
      Once the level has started, the king is dropped at the point that was under the cursor (F5) or left at the castle
      (Shift+F5), and the sandbox panel shows.
    - Edit: the level alone, without its wave set (as the shell opens it). Studio.NextLoad = Edit, and Studio.View
      holds the editor state captured before play, which the shell restores (camera, selection, tool, panels). The
      draft and its undo history never leave memory.
    Every switch stops the music first, and runs under shortened clouds (cloudFadeInTime/OutTime at 0.12 s, put back
    once the transition is over). Times are logged ("play ready in ... ms").

    Leaving play another way also comes back here: the pause menu's Back to Map and the end screen's Continue (both
    relabelled "Back to the Studio" in StudioPlayManager) turn into ReturnToEdit, so a test never drops the player out
    of the editor. Try Again on the end screen restarts play as the game does.
*/
namespace PalaceMod.Builder.Studio.Play
{
    internal sealed class StudioPlay : IStudioPlay
    {
        public static readonly StudioPlay Instance = new();

        private enum Phase { Idle, ToPlay, Playing, ToEdit }

        private Phase phase;
        private PackDraft draft;
        private string levelId;
        private StudioViewState view;
        private Vector3? spot;
        private DesignPack playPack;
        private bool arrivePending, teleportOnArrive;
        private int loadedFrame;
        private readonly Stopwatch swapTimer = new();
        private long readyMs = -1, loadedMs = -1;
        private float? savedCloudIn, savedCloudOut;
        private bool sandboxTouched;

        private const float FastCloud = 0.12f;

        private static SessionLauncher Launcher => BuilderManager.Launcher;

        /// <summary>A Studio play load is running (the level as it plays).</summary>
        public static bool InPlay => Launcher?.StudioKind == StudioSessionKind.Play && Launcher.Current != null;

        /// <summary>Any Studio session (edit or play) is running.</summary>
        public static bool InStudioSession => Launcher?.StudioKind is StudioSessionKind.Edit or StudioSessionKind.Play && Launcher.Current != null;

        public DesignPack PlayPack => playPack;
        public DesignPack DraftPack => draft?.Pack ?? playPack;
        public Vector3? Spot => spot;
        public long LastSwapMs { get; private set; } = -1;
        public bool Swapping => phase is Phase.ToPlay or Phase.ToEdit;

        private static bool TransitionRunning => SceneTransitionManager.instance != null && SceneTransitionManager.instance.SceneTransitionIsRunning;

        // ---- IStudioPlay ------------------------------------------------------------------------------------------

        public bool CanSwap(out string why)
        {
            why = null;
            if (SceneTransitionManager.instance == null || TransitionRunning) why = "loading";
            else if (Swapping) why = "already switching";
            else if (!Feature<BuilderManager>.On) why = "the Builder isn't available";
            else if (Studio.Draft == null && draft == null) why = "no level open";
            return why == null;
        }

        public void EnterPlay(bool fromCursor)
        {
            if (!CanSwap(out string why)) { Notify.Show("Can't play: " + why, NotifyKind.Warning, 4f, "builder-play"); return; }
            draft = Studio.Draft ?? draft;
            levelId = Studio.LevelId ?? levelId;
            view = Studio.View ?? Studio.CaptureView() ?? view;
            spot = fromCursor ? view?.Cursor ?? Studio.CursorGround : null;
            if (!Play(fromCursor ? spot : null)) return;
        }

        /// <summary>Starts (or restarts) play of the current draft's level; spot null = the king starts at the castle.</summary>
        internal bool Play(Vector3? at)
        {
            if (draft == null || levelId == null) { Notify.Show("Can't play: no level open", NotifyKind.Warning, 4f, "builder-play"); return false; }
            var pack = TestPacks.Play(draft.Pack, levelId, out string error);
            if (pack == null) { Notify.Show("Can't play: " + error, NotifyKind.Error, 6f, "builder-play"); return false; }
            spot = at;
            teleportOnArrive = at != null;
            playPack = pack;
            return Swap(new SessionRequest { Pack = pack, LevelId = levelId }, StudioSessionKind.Play);
        }

        /// <summary>Starts play from outside the shell (dev command, or before the shell exists): the pack's level.</summary>
        internal bool PlayLevel(PackDraft d, string level, Vector3? at)
        {
            draft = d;
            levelId = level;
            view = null;
            return Play(at);
        }

        public void ReturnToEdit()
        {
            if (!InPlay) return;
            if (TransitionRunning || Swapping) return;
            if (draft == null || draft.Pack.FindLevel(levelId) == null)
            {
                // The level is gone (deleted from the pack editor meanwhile): leave instead.
                Notify.Show("The level was deleted", NotifyKind.Warning, 4f, "builder-play");
                StopMusic();
                BuilderServices.Sessions.End();
                return;
            }
            Studio.View = view;
            Swap(new SessionRequest { Pack = Studio.EditPack(draft.Pack, levelId), LevelId = levelId }, StudioSessionKind.Edit);
        }

        public void Restart()
        {
            if (!InPlay || TransitionRunning || Swapping) return;
            Play(spot);
        }

        private bool Swap(SessionRequest request, StudioSessionKind kind)
        {
            var before = Studio.Mode;
            var beforeNext = Studio.NextLoad;
            StopMusic();
            ShortClouds();
            Studio.NextLoad = kind == StudioSessionKind.Play ? StudioMode.Play : StudioMode.Edit;
            swapTimer.Restart();
            readyMs = -1;
            arrivePending = false;
            phase = kind == StudioSessionKind.Play ? Phase.ToPlay : Phase.ToEdit;
            if (before != StudioMode.Off) Studio.SetMode(StudioMode.Loading);
            if (!Launcher.LaunchStudio(request, kind, out string error))
            {
                phase = InPlay ? Phase.Playing : Phase.Idle;
                Studio.NextLoad = beforeNext;
                RestoreClouds();
                if (before != StudioMode.Off) Studio.SetMode(before);
                Notify.Show((kind == StudioSessionKind.Play ? "Can't play: " : "Can't go back: ") + error, NotifyKind.Error, 6f, "builder-play");
                return false;
            }
            Log.For<BuilderManager>().Info($"Studio: switching to {kind} on {Launcher.Current.HostScene}" + (kind == StudioSessionKind.Play ? $" ({(spot is Vector3 p ? $"king at {p}" : "from the castle")})" : ""));
            return true;
        }

        // ---- Per frame --------------------------------------------------------------------------------------------

        /// <summary>BuilderManager forwards host loads (SceneManager.sceneLoaded, the session's host only).</summary>
        internal void OnHostLoaded(Scene scene)
        {
            if (Swapping && scene.name == Launcher.Current?.HostScene) loadedMs = swapTimer.ElapsedMilliseconds;
            if (Launcher.StudioKind != StudioSessionKind.Play || scene.name != Launcher.Current?.HostScene) return;
            // A load we didn't start (the end screen's Try Again, a retry): play goes on, from the castle.
            if (phase != Phase.ToPlay) teleportOnArrive = false;
            arrivePending = true;
            loadedFrame = Time.frameCount;
        }

        internal void Update()
        {
            if (savedCloudIn != null && !TransitionRunning) RestoreClouds();

            if (arrivePending && Time.frameCount >= loadedFrame + 3 && PlayerInteraction.instance != null && DebugActions.InLevel) Arrive();

            if (phase == Phase.Playing && readyMs < 0 && !TransitionRunning)
            {
                readyMs = swapTimer.ElapsedMilliseconds;
                LastSwapMs = readyMs;
                Log.For<BuilderManager>().Info($"Studio: play ready in {readyMs} ms (scene loaded at {loadedMs} ms)");
            }
            if (phase == Phase.ToEdit && !TransitionRunning && Launcher.StudioKind == StudioSessionKind.Edit &&
                (Studio.Mode == StudioMode.Edit || Studio.Shell == null) && SceneManager.GetActiveScene().name == Launcher.Current?.HostScene)
            {
                phase = Phase.Idle;
                LastSwapMs = swapTimer.ElapsedMilliseconds;
                Log.For<BuilderManager>().Info($"Studio: edit ready in {LastSwapMs} ms (scene loaded at {loadedMs} ms)");
            }
            if (InPlay) SandboxActions.KeepGold();
        }

        private void Arrive()
        {
            arrivePending = false;
            if (teleportOnArrive && spot is Vector3 at)
            {
                try { SandboxActions.Teleport(Level.LayoutScene.Ground(at), followers: false); }
                catch (Exception e) { Log.For<BuilderManager>().Warn($"Couldn't drop the king at {at}: {e.Message}"); }
            }
            teleportOnArrive = false;
            TipManager.instance?.UpdateTipRaw("", "");
            SandboxActions.KeepGold();
            if (phase == Phase.ToPlay)
            {
                Log.For<BuilderManager>().Info($"Studio: play started in {swapTimer.ElapsedMilliseconds} ms (level running)");
                readyMs = -1;
            }
            phase = Phase.Playing;
            if (Studio.Mode != StudioMode.Off || Studio.Shell != null) Studio.SetMode(StudioMode.Play);
            SandboxPanel.Refresh();
        }

        /// <summary>The Studio closed (Off) or the session ended: put back what the sandbox changed.</summary>
        internal void Reset()
        {
            if (phase == Phase.Idle && draft == null && !sandboxTouched) return;
            phase = Phase.Idle;
            arrivePending = teleportOnArrive = false;
            draft = null;
            levelId = null;
            view = null;
            playPack = null;
            spot = null;
            Studio.NextLoad = StudioMode.Edit;
            SandboxActions.DevCursor = null;
            SandboxPanel.Disarm();
            RestoreClouds();
            StopMusic();
            if (sandboxTouched)
            {
                sandboxTouched = false;
                if (!DebugActions.IsVanillaSpeed(DebugActions.Speed)) DebugActions.SetSpeed(1f);
                if (DebugActions.GodMode) DebugActions.SetGodMode(false);
                if (DebugActions.InvulnerableKingdom) DebugActions.SetInvulnerableKingdom(false);
            }
        }

        /// <summary>The sandbox changed something global (speed, god mode, invulnerable buildings): undone on Reset.</summary>
        internal void Touched() => sandboxTouched = true;

        // ---- Music and clouds -------------------------------------------------------------------------------------

        /// <summary>The pointer is over the sandbox panel or a popup: clicks there aren't map clicks.</summary>
        internal static bool PointerOverUI() => Studio.PointerOverUI || ModPopup.Current != null;

        /// <summary>Fades the music out (night music would otherwise carry on into the editor or the menus).</summary>
        internal static void StopMusic()
        {
            var music = MusicManager.instance;
            if (music == null) return;
            // A fade already running (a night's music fading in over 4 s) finishes before the manager looks at a new
            // request; stop it so the fade-out starts now.
            var fade = HarmonyLib.Traverse.Create(music).Field("fadeControllerCoroutine");
            if (fade.GetValue() is Coroutine running)
            {
                music.StopCoroutine(running);
                fade.SetValue(null);
                HarmonyLib.Traverse.Create(music).Field("requestedFadeDuration").SetValue(-1f);
            }
            music.PlayMusic(null, 0.5f);
        }

        private void ShortClouds()
        {
            var stm = SceneTransitionManager.instance;
            if (stm == null) return;
            if (savedCloudIn == null)
            {
                savedCloudIn = stm.cloudFadeInTime;
                savedCloudOut = stm.cloudFadeOutTime;
            }
            stm.cloudFadeInTime = FastCloud;
            stm.cloudFadeOutTime = FastCloud;
        }

        private void RestoreClouds()
        {
            var stm = SceneTransitionManager.instance;
            if (stm == null || savedCloudIn == null) return;
            stm.cloudFadeInTime = savedCloudIn.Value;
            stm.cloudFadeOutTime = savedCloudOut.Value;
            savedCloudIn = savedCloudOut = null;
        }

        public string Describe() =>
            $"phase={phase} kind={Launcher?.StudioKind} mode={Studio.Mode} nextLoad={Studio.NextLoad} level={levelId ?? "-"} spot={spot?.ToString() ?? "-"} " +
            $"lastSwap={LastSwapMs} ms gold={(SandboxActions.InfiniteGold ? "inf" : "normal")} balance={PlayerInteraction.instance?.Balance} " +
            $"clouds={SceneTransitionManager.instance?.cloudFadeInTime}/{SceneTransitionManager.instance?.cloudFadeOutTime} scene={SceneManager.GetActiveScene().name}";
    }
}
