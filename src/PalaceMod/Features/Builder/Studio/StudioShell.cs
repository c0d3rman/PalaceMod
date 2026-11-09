using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The Builder Studio (docs/builder-ux.md): one in-world tool to build a level on its map. This is its lifecycle and
    per-frame loop; the scene view is StudioScene, edits StudioEdits, tools StudioTools, the UI StudioDock.

    It runs inside a custom session on the host map (nothing touches the player's saves), launched with the level alone
    (Studio.EditPack). The overlay isn't applied to that load; the Studio shows the edits itself. Test play (W2) reloads
    the same host in play state and back (Studio.NextLoad, Studio.SetMode); the Studio keeps its draft, level and
    session across those swaps and rebuilds its scene on each edit load.
*/
namespace PalaceMod.Builder.Studio
{
    internal sealed class StudioShell : IStudioShell
    {
        public static readonly StudioShell Instance = new();

        private enum State { Off, Waiting, Editing }

        private static State state;
        private static PackDraft draft;
        private static string levelId, packId, host;
        private static BuilderSessionInfo session;
        private static int startFrame;
        private static bool claimed;

        public static PackDraft CurrentDraft => draft;
        public static bool Editing => state == State.Editing;
        public static string Host => host;

        // ---- Tools ------------------------------------------------------------------------------------------------

        public static readonly SelectTool Select = new();
        public static readonly LassoTool Lasso = new();
        public static readonly PaintTool Paint = new();
        public static readonly MoveTool Move = new();
        public static readonly RotateTool Rotate = new();
        public static readonly PlaceTool Place = new();
        public static readonly EraseTool Erase = new();
        private static readonly StudioTool[] builtins = [Select, Lasso, Paint, Move, Rotate, Place, Erase];

        public static IEnumerable<StudioTool> AllTools => builtins.Concat(Studio.Tools.Where(t => builtins.All(b => b.Id != t.Id))).OrderBy(t => t.Order);
        public static StudioTool Tool { get; private set; } = Select;
        private static string pendingTool;

        public static void SetTool(string id)
        {
            var next = AllTools.FirstOrDefault(t => t.Id == id) ?? Select;
            if (next == Tool && next != Move) return;
            var prev = Tool;
            try { Tool?.Deactivate(); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio tool {Tool?.Id} Deactivate failed", e); }
            Tool = next;
            if (next == Move && prev != Move) Move.ReturnTo = prev?.Id ?? "select";
            try { next.Activate(); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio tool {next.Id} Activate failed", e); }
            StudioDock.OnToolChanged();
        }

        /// <summary>Switches tool after this frame's tool update (tools call it from their own Update).</summary>
        public static void SetToolLater(string id) => pendingTool = id;

        // Recently placed kinds, newest first (the 1-9 hotbar).
        private static readonly List<StudioPlaceable> recent = [];
        public static IReadOnlyList<StudioPlaceable> Recent => recent;

        public static void Remember(StudioPlaceable p)
        {
            if (p?.Id == null) return;
            recent.RemoveAll(r => r.Id == p.Id);
            recent.Insert(0, p);
            if (recent.Count > 9) recent.RemoveAt(recent.Count - 1);
            StudioDock.OnRecentChanged();
        }

        public static void Arm(StudioPlaceable p, float baseYaw = 0f)
        {
            if (p == null) return;
            if (Tool != Place) SetTool("place");
            Place.Arm(p);
            Place.GhostBaseYaw = baseYaw;
            StudioDock.OnToolChanged();
        }

        // ---- Snapping ---------------------------------------------------------------------------------------------

        public static readonly float[] SnapSteps = [0f, 0.5f, 1f, 2f];
        public static float SnapStep = 1f;
        public static float TurnStep = 15f;

        public static Vector3 Snap(Vector3 p, bool free)
        {
            if (free || SnapStep <= 0f) return StudioScene.Ground(p);
            return StudioScene.Ground(new Vector3(Mathf.Round(p.x / SnapStep) * SnapStep, p.y, Mathf.Round(p.z / SnapStep) * SnapStep));
        }

        public static Vector3 SnapDelta(Vector3 d, bool free) => StudioMath.SnapDelta(d, free ? 0f : SnapStep);

        // ---- Launch -----------------------------------------------------------------------------------------------

        public bool Open(PackDraft d, string level, out string error)
        {
            error = null;
            var lv = d?.Pack.FindLevel(level);
            if (lv == null) { error = "no such level"; return false; }
            DesignPack copy;
            try { copy = Studio.EditPack(d.Pack, level); }
            catch (Exception e) { error = e.Message; return false; }
            Stop();
            draft = d;
            levelId = level;
            packId = d.Pack.Id;
            host = lv.Host;
            state = State.Waiting;
            claimed = false;
            Studio.NextLoad = StudioMode.Edit;
            Studio.SetMode(StudioMode.Loading);
            if (!BuilderManager.Launcher.LaunchStudio(new SessionRequest { Pack = copy, LevelId = level }, StudioSessionKind.Edit, out error))
            {
                Stop();
                return false;
            }
            session = BuilderServices.Sessions.Current;
            EditorReturn.Remember();
            Log.For<BuilderManager>().Info($"Studio: opening {packId}/{level} on {host}");
            return true;
        }

        /// <summary>The overlay hook: true when this host load is the Studio's edit load (then nothing is applied).</summary>
        public static bool ClaimSession(DesignPack pack, LevelDef level)
        {
            if (state == State.Off || session == null || BuilderServices.Sessions.Current != session) return false;
            if (pack.Id != packId || level.Id != levelId) return false;
            if (Studio.NextLoad != StudioMode.Edit) return false;
            ClearScene();
            state = State.Waiting;
            claimed = true;
            startFrame = Time.frameCount;
            claimedAt = Time.realtimeSinceStartup;
            return true;
        }

        private static float claimedAt;

        /// <summary>Leaves the Studio (the map stays loaded until the session ends).</summary>
        public static void Stop()
        {
            ClearScene();
            StudioDock.Destroy();
            StudioDock.HidePlay();
            StudioScene.DropLookCache();
            state = State.Off;
            session = null;
            draft = null;
            levelId = packId = host = null;
            Studio.View = null;
            Studio.SetMode(StudioMode.Off);
        }

        private static void ClearScene()
        {
            if (draft != null) draft.Changed -= OnDraftChanged;
            try { Tool?.Deactivate(); } catch (Exception e) { Log.For<BuilderManager>().Caught("Studio tool Deactivate failed", e); }
            Tool = Select;
            pick = null;
            foreach (var o in Studio.Overlays)
            {
                try { o.Hide(); } catch (Exception e) { Log.For<BuilderManager>().Caught("Studio overlay Hide failed", e); }
            }
            StudioCamera.Release();
            StudioGizmos.Clear();
            StudioLabels.Clear();
            StudioScene.Clear();
            StudioSelection.Clear();
            StudioInput.Reset();
            ModTooltip.Hide(Instance);
            StudioDock.Hide();
            if (!TransitionRunning && ModPopup.Current != null && ModPopup.Current.IsOpen) ModPopup.Current.Close();
        }

        private static bool TransitionRunning => SceneTransitionManager.instance != null && SceneTransitionManager.instance.SceneTransitionIsRunning;

        // ---- Suspension (the Enemy Studio takes over the edit scene in place) --------------------------------------

        /// <summary>While true, the shell's per-frame loop doesn't run (no dock, keys, tools, labels or overlays); the
        /// scene, draft, selection and view are kept. Set by the tool that takes over (EnemyStudio).</summary>
        public static Func<bool> Suspended;

        /// <summary>Hides everything the shell draws over the map (call when a suspension starts).</summary>
        public static void HideForSuspend()
        {
            try { Tool?.Cancel(); } catch (Exception e) { Log.For<BuilderManager>().Caught("Studio tool Cancel failed", e); }
            foreach (var o in Studio.Overlays)
            {
                try { o.Hide(); } catch (Exception e) { Log.For<BuilderManager>().Caught("Studio overlay Hide failed", e); }
            }
            StudioLabels.Clear();
            StudioScene.SetMarkersVisible(false);
            StudioSelection.SetHover(null);
            ModTooltip.Hide(Instance);
            StudioDock.Hide();
        }

        /// <summary>Brings the shell's drawing back after a suspension and restores a view (camera, selection, tool, tabs).</summary>
        public static void ResumeFromSuspend(StudioViewState view)
        {
            StudioScene.SetMarkersVisible(true);
            StudioScene.RefreshMarkers();
            if (view != null) RestoreViewNow(view);
            StudioDock.RefreshAll();
            StudioDock.OnLevelChanged();
        }

        // ---- Per frame ----------------------------------------------------------------------------------------------

        public static void Update()
        {
            if (state == State.Off) return;
            var current = BuilderServices.Sessions.Current;
            if (session != null && current != session) { Stop(); return; }
            if (!Feature<BuilderManager>.On) { BuilderServices.Sessions.End(); Stop(); return; }
            // Test play owns the scene meanwhile (the Studio only hosts its panels); the Studio waits for the next edit load.
            if (Studio.Mode is StudioMode.Play) { StudioInput.Tick(); StudioDock.PlayTick(); return; }
            StudioDock.HidePlay();
            if (state == State.Waiting)
            {
                if (session == null) session = current;
                var s = LayoutScene.Host(host);
                // Wait for the map's Start (build slots deactivate themselves there) and the camera rig.
                if (!claimed || !s.IsValid() || !s.isLoaded || Time.frameCount < startFrame + 3 || UnityEngine.Object.FindObjectOfType<CameraRig>() == null) return;
                if (session != null && current == session) Enter(s);
                return;
            }
            if (!StudioScene.Scene.isLoaded) { ClearScene(); state = State.Waiting; return; }
            if (TransitionRunning) return;
            // Another in-world tool (the Enemy Studio) has the screen; the level stays as it is underneath.
            if (Suspended?.Invoke() == true) return;

            LocalGamestate.Instance?.SetPlayerFreezeState(true);
            StudioDock.Show();
            bool active = StudioDock.Active;
            StudioInput.Tick();
            var p = Pointer(active);
            bool rightClick = StudioCamera.Update(active && !p.OverUI, active && !ModTextInput.Active, StudioSelection.Count == 0 && !Move.Grabbing);
            if (active)
            {
                if (!ModTextInput.Active) UpdateKeys(p);
                if (rightClick) RightClick(p);
                UpdateTool(p);
            }
            foreach (var o in Studio.Overlays)
            {
                try { o.Draw(); } catch (Exception e) { Log.For<BuilderManager>().Caught("Studio overlay Draw failed", e); }
            }
            StudioLabels.Update();
            StudioDock.Tick(p);
            HoverCard(p, active);
        }

        private static void Enter(Scene s)
        {
            var lv = Studio.Level ?? draft?.Pack.FindLevel(levelId);
            if (lv == null) { Notify.Show("The level was deleted", NotifyKind.Warning); BuilderServices.Sessions.End(); Stop(); return; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            float waited = (Time.realtimeSinceStartup - claimedAt) * 1000f;
            int frames = Time.frameCount - startFrame;
            StudioScene.Enter(s);
            long scene = clock.ElapsedMilliseconds;
            var castle = StudioScene.Castle;
            StudioCamera.Setup(StudioScene.Root, castle != null ? castle.Pos : UnityEngine.Object.FindObjectOfType<CameraRig>().transform.position);
            draft.Changed += OnDraftChanged;
            BuilderEditor.BindDraft(draft);
            StudioServices.AssetBrowser ??= new HostSceneryBrowser();
            state = State.Editing;
            long ready = clock.ElapsedMilliseconds;
            Studio.SetMode(StudioMode.Edit);
            StudioScene.Reconcile(draft.Pack.FindLevel(levelId));
            Studio.RaiseItemsChanged();
            var view = Studio.View;
            Studio.View = null;
            if (view != null) RestoreViewNow(view);
            else StudioCamera.Frame(StudioScene.Pickable.Where(i => i.Kind != StudioItemKind.Line).Select(i => i.Pos).ToList());
            StudioDock.Build();
            Log.For<BuilderManager>().Info($"Studio on {s.name}: {StudioScene.Natives.Count} build slots, {StudioScene.Lines.Count} spawn lines, {StudioScene.PropBases.Count} scenery kinds; " +
                $"waited {waited:0} ms / {frames} frames after the claim, scene {scene} ms ({StudioScene.LastTimings}), ready {ready} ms, all {clock.ElapsedMilliseconds} ms");
        }

        private static void OnDraftChanged(bool structural)
        {
            if (state != State.Editing) return;
            if (Studio.Level == null) { Notify.Show("The level was deleted; leaving the Studio", NotifyKind.Warning); BuilderServices.Sessions.End(); Stop(); return; }
            StudioScene.Reconcile(Studio.Level);
            StudioSelection.Prune();
            Studio.RaiseItemsChanged();
            StudioDock.OnLevelChanged();
        }

        public static void OnSelectionChanged() => StudioDock.OnSelectionChanged();

        /// <summary>Test play started a swap: the edit scene goes away with the reload; the session, draft and level stay.</summary>
        public static void OnModeChanged(StudioMode mode)
        {
            // A swap that didn't start goes back to Edit without a reload: rebuild the editor on the scene that's there.
            if (mode == StudioMode.Edit && state == State.Waiting && !claimed && LayoutScene.Host(host) is var s && s.IsValid() && s.isLoaded)
            {
                claimed = true;
                startFrame = Time.frameCount - 3;
                return;
            }
            if (mode is StudioMode.Loading or StudioMode.Play && state == State.Editing)
            {
                ClearScene();
                state = State.Waiting;
                claimed = false;
            }
        }

        // ---- Pointer ------------------------------------------------------------------------------------------------

        /// <summary>Dev stand-in for the mouse: a world point on the ground (and a click on the next frame).</summary>
        public static Vector3? DevCursor;
        public static bool DevClick;

        private static readonly StudioPointer pointer = new();
        private static bool everDragged, downOverUI;

        public static Vector3? CursorGroundNow => pointer.Ground;

        public static bool ScreenGround(Vector2 screen, out Vector3 point)
        {
            point = default;
            var cam = Camera.main;
            if (cam == null) return false;
            var ray = cam.ScreenPointToRay(screen);
            // Sculpted ground: the exact hit (a ground-plane guess lands behind a cliff's top edge).
            if (GroundLayer.TryRaycast(ray, out point)) return true;
            float height = StudioCamera.Ready ? StudioCamera.Pivot.y : 0f;
            for (int i = 0; i < 2; i++)
            {
                if (!new Plane(Vector3.up, new Vector3(0f, height, 0f)).Raycast(ray, out float d)) return false;
                point = StudioScene.Ground(ray.GetPoint(d));
                height = point.y;
            }
            return true;
        }

        public static Vector2? WorldToScreen(Vector3 world)
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var s = cam.WorldToScreenPoint(world);
            return s.z > 0f ? new Vector2(s.x, s.y) : null;
        }

        private static StudioPointer Pointer(bool active)
        {
            var p = pointer;
            bool devClick = DevClick;
            DevClick = false;
            if (DevCursor is Vector3 dev)
            {
                var g = StudioScene.Ground(dev);
                p.Ground = g;
                p.Screen = WorldToScreen(g) ?? Vector2.zero;
                p.OverUI = false;
            }
            else
            {
                p.Screen = StudioInput.Mouse;
                p.OverUI = !active || StudioDock.PointerOverPanel;
                p.Ground = ScreenGround(p.Screen, out var g) ? g : null;
            }
            p.Shift = StudioInput.Shift;
            p.Ctrl = StudioInput.Ctrl;
            p.Alt = StudioInput.Alt;
            p.LeftDown = StudioInput.Down(0) || devClick;
            p.LeftHeld = StudioInput.Held(0);
            p.LeftUp = StudioInput.Up(0) || devClick;
            if (p.LeftDown)
            {
                p.DownScreen = p.Screen;
                p.DownGround = p.Ground;
                everDragged = false;
                downOverUI = p.OverUI;
            }
            if (p.LeftHeld && !everDragged && (p.Screen - p.DownScreen).magnitude > StudioCamera.ClickSlop) everDragged = true;
            p.Dragging = (p.LeftHeld || p.LeftUp) && everDragged && !downOverUI;
            p.Click = p.LeftUp && !everDragged && !downOverUI;
            p.Snapped = p.Ground is Vector3 gr ? Snap(gr, p.Alt) : null;
            var hover = p.OverUI || p.Ground == null ? null : StudioScene.ItemAt(p.Ground.Value)?.Key;
            p.HoverKey = hover;
            if (p.LeftDown) p.DownKey = hover;
            p.RightClick = false;
            StudioSelection.SetHover(hover);
            return p;
        }

        private static void UpdateTool(StudioPointer p)
        {
            if (pick != null)
            {
                if (p.Click && !p.OverUI)
                {
                    var pk = pick;
                    string key = p.HoverKey;
                    if (key != null && (pk.Accept?.Invoke(key) ?? true))
                    {
                        pick = null;
                        try { pk.Picked?.Invoke(key); } catch (Exception e) { Log.For<BuilderManager>().Caught("Studio pick handler failed", e); }
                        StudioDock.OnToolChanged();
                    }
                }
                return;
            }
            try { Tool?.Update(p); }
            catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio tool {Tool?.Id} failed", e); }
            if (pendingTool != null)
            {
                string id = pendingTool;
                pendingTool = null;
                SetTool(id);
            }
        }

        // ---- Right click, Esc ---------------------------------------------------------------------------------------

        private static bool CancelOperation()
        {
            if (pick != null) { pick = null; StudioDock.OnToolChanged(); return true; }
            if (Studio.RunCancelHandlers()) return true;
            try { if (Tool != null && Tool.Cancel()) return true; }
            catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio tool {Tool?.Id} Cancel failed", e); }
            return false;
        }

        private static void RightClick(StudioPointer p)
        {
            if (p.OverUI) return;
            if (CancelOperation()) return;
            p.RightClick = true;
            string key = p.HoverKey;
            if (key != null && !StudioSelection.Contains(key))
            {
                string target = !p.Alt && StudioScene.GroupOf(key) is string g ? g : key;
                StudioSelection.Select([target], SelectOp.Replace);
            }
            StudioMenus.OpenContext(key != null ? StudioSelection.Keys.ToList() : [], p.Screen, p.Ground);
        }

        private static void Escape()
        {
            if (CancelOperation()) return;
            if (StudioSelection.Count > 0) { StudioSelection.Clear(); return; }
            StudioMenus.OpenStudioMenu();
        }

        // ---- Keys ---------------------------------------------------------------------------------------------------

        private static void UpdateKeys(StudioPointer p)
        {
            if (StudioKeys.Cancel?.Down == true) { Escape(); return; }
            if (StudioKeys.Down(StudioKeys.Redo, StudioKeys.RedoCtrlY, StudioKeys.RedoCtrl)) { CancelOperation(); BuilderEditor.Redo(); return; }
            if (StudioKeys.Down(StudioKeys.Undo, StudioKeys.UndoCtrl)) { CancelOperation(); BuilderEditor.Undo(); return; }
            if (StudioKeys.Down(StudioKeys.Save, StudioKeys.SaveCtrl)) { Save(); return; }
            if (StudioKeys.Down(StudioKeys.PlayStart)) { Play(false); return; }
            if (StudioKeys.Down(StudioKeys.Play)) { Play(true); return; }
            if (StudioKeys.Down(StudioKeys.Delete, StudioKeys.DeleteFwd))
            {
                if (StudioSelection.Count > 0) { CancelOperation(); StudioEdits.Delete(StudioSelection.Keys.ToList()); }
                return;
            }
            if (StudioKeys.Down(StudioKeys.Duplicate, StudioKeys.DuplicateCtrl)) { DuplicateAndGrab(); return; }
            if (StudioKeys.Down(StudioKeys.Copy, StudioKeys.CopyCtrl))
            {
                int n = StudioEdits.Copy(StudioSelection.Items);
                Notify.Show(n == 0 ? "Nothing to copy" : $"Copied {StudioEdits.Count(n, "1 object")}", NotifyKind.Info, 2f, "builder-studio");
                return;
            }
            if (StudioKeys.Down(StudioKeys.Paste, StudioKeys.PasteCtrl)) { PasteAtCursor(); return; }
            if (StudioKeys.Down(StudioKeys.SelectAll, StudioKeys.SelectAllCtrl)) { StudioSelection.Select(StudioOutliner.FilteredKeys(), SelectOp.Replace); return; }
            if (StudioKeys.Down(StudioKeys.Ungroup, StudioKeys.UngroupCtrl)) { StudioEdits.Ungroup(StudioSelection.Keys); return; }
            if (StudioKeys.Down(StudioKeys.Group, StudioKeys.GroupCtrl)) { if (StudioEdits.Group(StudioSelection.Keys) is string gk) StudioSelection.Select([gk], SelectOp.Replace); return; }
            if (p.Ctrl) return; // the rest are plain keys; Ctrl/Cmd combinations above
            if (StudioKeys.Down(StudioKeys.ShowAll)) { StudioScene.ShowAll(); return; }
            if (StudioKeys.Down(StudioKeys.Hide) && !p.Alt) { StudioScene.Hide(StudioSelection.Keys); StudioSelection.Clear(); return; }
            if (StudioKeys.Down(StudioKeys.Frame)) { FrameSelection(); return; }
            if (StudioKeys.Down(StudioKeys.FrameAll)) { FrameAll(); return; }
            // X locks a grab to x, and switches the Rotate tool to tilting.
            if (StudioKeys.Down(StudioKeys.XRay) && !Move.Grabbing && Tool != Rotate) { StudioViewOptions.XRay = !StudioViewOptions.XRay; StudioViewOptions.RaiseChanged(); StudioScene.RefreshMarkers(); return; }
            if (StudioKeys.Down(StudioKeys.Palette)) { StudioDock.TogglePalette(); return; }
            float turn = StudioKeys.Down(StudioKeys.RotateBack) ? -TurnStep : StudioKeys.Down(StudioKeys.Rotate) ? TurnStep : 0f;
            if (turn != 0f)
            {
                if (Tool == Place && Place.Armed != null) Place.Turn(turn);
                else if (Tool == Rotate && Rotate.Axis != 0 && StudioSelection.Count > 0) Rotate.Step(turn);
                else if (StudioSelection.Count > 0) StudioEdits.Rotate(StudioSelection.Items.ToList(), turn);
                return;
            }
            if (!p.Shift || StudioKeys.ToolErase.Shortcut.Modifiers.Any())
            {
                if (StudioKeys.Down(StudioKeys.ToolErase)) { SetTool("erase"); return; }
                if (!p.Shift)
                {
                    if (StudioKeys.Down(StudioKeys.ToolSelect)) { SetTool("select"); return; }
                    if (StudioKeys.Down(StudioKeys.ToolLasso)) { SetTool("lasso"); return; }
                    if (StudioKeys.Down(StudioKeys.ToolPaint)) { SetTool("paint"); return; }
                    if (StudioKeys.Down(StudioKeys.ToolMove)) { SetTool("move"); return; }
                    if (StudioKeys.Down(StudioKeys.ToolRotate)) { SetTool("rotate"); return; }
                    foreach (var t in Studio.Tools)
                        if (t.Key?.Down == true) { SetTool(t.Id); return; }
                    for (int i = 0; i < 9; i++)
                    {
                        if (StudioKeys.Hotbar[i]?.Down == true && Tool?.Digit(i) == true) return;
                        if (StudioKeys.Hotbar[i]?.Down == true && i < recent.Count) { Arm(recent[i], recentYaw.TryGetValue(recent[i].Id, out var y) ? y : 0f); return; }
                    }
                }
            }
            // Arrow keys nudge the selection by the snap step (Shift x5), along the view's axes.
            if (StudioSelection.Count > 0 && (Tool == Select || Tool == Move))
            {
                Vector3 dir = Vector3.zero;
                if (Input.GetKeyDown(KeyCode.UpArrow)) dir = AxisOf(StudioCamera.Forward);
                else if (Input.GetKeyDown(KeyCode.DownArrow)) dir = -AxisOf(StudioCamera.Forward);
                else if (Input.GetKeyDown(KeyCode.RightArrow)) dir = AxisOf(StudioCamera.Right);
                else if (Input.GetKeyDown(KeyCode.LeftArrow)) dir = -AxisOf(StudioCamera.Right);
                if (dir != Vector3.zero)
                {
                    float step = (SnapStep > 0f ? SnapStep : 0.5f) * (p.Shift ? 5f : 1f);
                    StudioEdits.Translate(StudioSelection.Items.ToList(), dir * step, "Nudge", "nudge:" + string.Join(",", StudioSelection.Keys));
                }
            }
        }

        private static readonly Dictionary<string, float> recentYaw = [];
        public static void RememberYaw(string id, float yaw) => recentYaw[id] = yaw;

        private static Vector3 AxisOf(Vector3 v) => Mathf.Abs(v.x) >= Mathf.Abs(v.z) ? new Vector3(Mathf.Sign(v.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(v.z));

        public static void DuplicateAndGrab()
        {
            var copies = StudioEdits.Duplicate(StudioSelection.Items.ToList());
            if (copies.Count == 0) { Notify.Show("Nothing to duplicate", NotifyKind.Info, 2f, "builder-studio"); return; }
            StudioSelection.Select(copies, SelectOp.Replace);
            SetTool("move");
        }

        public static void PasteAtCursor(Vector3? at = null)
        {
            if (StudioEdits.ClipboardCount == 0) { Notify.Show("Nothing copied", NotifyKind.Info, 2f, "builder-studio"); return; }
            var where = at ?? pointer.Ground ?? StudioCamera.Pivot;
            var keys = StudioEdits.Paste(Snap(where, false));
            if (keys.Count > 0) StudioSelection.Select(keys, SelectOp.Replace);
        }

        public static void FrameSelection()
        {
            if (StudioSelection.Count == 0) { FrameAll(); return; }
            StudioCamera.FrameItems(StudioSelection.Items);
        }

        public static void FrameAll() => StudioCamera.Frame(StudioScene.Pickable.Select(i => i.Pos).ToList());

        // ---- Hover card ---------------------------------------------------------------------------------------------

        private static string cardKey;
        private static float cardSince;

        private static void HoverCard(StudioPointer p, bool active)
        {
            string key = active && !p.LeftHeld && Tool != Place ? p.HoverKey : null;
            if (key != cardKey) { cardKey = key; cardSince = Time.unscaledTime; ModTooltip.Hide(Instance); }
            if (key == null || Time.unscaledTime - cardSince < 0.45f || ModTooltip.Visible) return;
            var item = StudioScene.Find(key);
            if (item == null) return;
            var anchor = StudioDock.PointAnchor(p.Screen);
            if (anchor != null) ModTooltip.Show(item.Name, StudioInspector.Card(item), anchor, Instance);
        }

        // ---- Actions ------------------------------------------------------------------------------------------------

        public static void Save()
        {
            if (draft == null) return;
            bool ok = draft.Save(out string message);
            Notify.Show(message, ok ? NotifyKind.Success : NotifyKind.Error, key: "builder-studio");
            StudioDock.OnLevelChanged();
        }

        public static void Play(bool fromCursor)
        {
            var play = StudioServices.Play;
            if (play == null) { Notify.Show("Test play isn't available", NotifyKind.Warning, 3f, "builder-studio"); return; }
            if (TransitionRunning) return;
            if (!play.CanSwap(out string why)) { Notify.Show("Can't play: " + why, NotifyKind.Warning, 4f, "builder-studio"); return; }
            Studio.View = CaptureViewNow();
            try { play.EnterPlay(fromCursor); }
            catch (Exception e) { Log.For<BuilderManager>().Error("Studio: entering play failed", e); }
        }

        /// <summary>Leaves the Studio and its map (unsaved changes stay in the draft).</summary>
        public void Leave()
        {
            // End first: menus stay up under the clouds of the trip back (EditorReturn), like the game's Back to Map.
            BuilderServices.Sessions.End();
            Stop();
            if (!TransitionRunning) UIFrameManager.instance?.CloseAllFrames();
        }

        public static void CheckPaths()
        {
            if (!Editing) return;
            try { if (StudioServices.Terrain?.PathsStale == true) StudioServices.Terrain.UpdatePaths(); }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Studio terrain path update failed", e); }
            var solid = new List<GameObject>();
            foreach (var item in StudioScene.Items.Where(i => i.Kind == StudioItemKind.Prop && i.Proxy != null))
                if (StudioEdits.PropDef(item)?.Solid != false) { LayoutScene.SetSolid(item.Proxy, true); solid.Add(item.Proxy); }
            long ms = PathCheck.Rescan();
            var lineItems = StudioScene.Items.Where(i => i.Kind == StudioItemKind.Line).ToDictionary(i => i.Id);
            List<PathCheck.Route> routes;
            try { routes = PathCheck.Check(StudioScene.Scene, l => lineItems.TryGetValue(l.name, out var it) ? it.Pos : LayoutScene.LineCenter(l.transform), withPoints: true,
                include: l => !(lineItems.TryGetValue(l.name, out var it) && it.Removed)); }
            finally { foreach (var go in solid) LayoutScene.SetSolid(go, false); }
            StudioScene.ShowRoutes(routes);
            Log.For<BuilderManager>().Info($"Studio path check (rescan {ms} ms): {PathCheck.Describe(routes)}");
            StudioDock.OnLevelChanged();
        }

        /// <summary>The path check's result in a few words, for a row's value.</summary>
        public static string PathShort
        {
            get
            {
                var routes = StudioScene.Routes;
                if (routes.Count == 0) return "";
                int blocked = routes.Count(r => !r.Reachable);
                return blocked == 0 ? "all reach" : $"{blocked} blocked";
            }
        }

        public static string PathSummary
        {
            get
            {
                var routes = StudioScene.Routes;
                if (routes.Count == 0) return null;
                var blocked = routes.Where(r => !r.Reachable).Select(r => r.Line).ToList();
                return blocked.Count == 0 ? $"All {routes.Count} ground lines reach the castle" : "Blocked: " + string.Join(", ", blocked);
            }
        }

        // ---- Picking on the map -------------------------------------------------------------------------------------

        private sealed class Pick
        {
            public string Prompt;
            public Func<string, bool> Accept;
            public Action<string> Picked;
        }

        private static Pick pick;
        public static string PickPrompt => pick?.Prompt;

        // ---- IStudioShell -------------------------------------------------------------------------------------------

        public PackDraft Draft => draft;
        public string LevelId => levelId;
        Vector3? IStudioShell.CursorGround => pointer.Ground;
        IReadOnlyList<string> IStudioShell.Selection => StudioSelection.Keys;
        public IReadOnlyList<StudioItem> Items => StudioScene.Items;
        public StudioItem Find(string key) => StudioScene.Find(key);
        public StudioItem ItemAt(Vector3 ground) => StudioScene.ItemAt(ground);
        void IStudioShell.Select(IEnumerable<string> keys, SelectOp op) => StudioSelection.Select(keys, op);
        public bool PointerOverUI => StudioDock.PointerOverPanel || StudioDock.PointerOverPlayPanel;
        public float Ground(Vector3 point) => StudioScene.GroundY(point);
        public string ToolId => Tool?.Id;
        void IStudioShell.SetTool(string id) => SetTool(id);
        void IStudioShell.Arm(StudioPlaceable placeable) => Arm(placeable);
        public void Frame(IEnumerable<string> keys) => StudioCamera.FrameItems(keys.Select(StudioScene.Find).Where(i => i != null));
        public void RefreshPanels() => StudioDock.RefreshAll();

        public void Edit(string label, Action<LevelDef> change, string coalesce)
        {
            if (draft == null) return;
            if (Studio.Mode == StudioMode.Play) { Notify.Show("Back in the Studio to edit", NotifyKind.Info, 2f, "builder-studio"); return; }
            string id = levelId;
            draft.Edit(label, p =>
            {
                var lv = p.FindLevel(id) ?? throw new InvalidOperationException("level missing");
                change(lv);
                StudioEdits.Prune(lv);
            }, structural: true, coalesce: coalesce);
        }

        public void PickOnMap(string prompt, Func<string, bool> accept, Action<string> picked)
        {
            pick = new Pick { Prompt = prompt, Accept = accept, Picked = picked };
            StudioDock.OnToolChanged();
        }

        public StudioViewState CaptureView() => CaptureViewNow();

        public static StudioViewState CaptureViewNow() => new()
        {
            CameraPivot = StudioCamera.Pivot,
            Zoom = StudioCamera.Zoom,
            Yaw = StudioCamera.Yaw,
            Selection = StudioSelection.Keys.ToList(),
            ToolId = Tool?.Id,
            Armed = Tool == Place ? Place.Armed?.Id : null,
            ArmedYaw = Place.GhostYaw,
            PaletteTab = StudioDock.PaletteTab,
            PanelTab = StudioDock.PanelTab,
            PaletteScroll = StudioDock.PaletteScroll,
            PanelScroll = StudioDock.PanelScroll,
            Cursor = pointer.Ground ?? StudioCamera.Pivot,
            PaletteCollapsed = StudioDock.PaletteCollapsed,
        };

        public void RestoreView(StudioViewState view) => RestoreViewNow(view);

        private static void RestoreViewNow(StudioViewState view)
        {
            StudioCamera.Set(view.CameraPivot, view.Zoom, view.Yaw);
            StudioSelection.Select(view.Selection ?? [], SelectOp.Replace);
            StudioDock.RestoreTabs(view);
            if (view.Armed != null && recent.FirstOrDefault(r => r.Id == view.Armed) is { } armed)
            {
                Arm(armed, recentYaw.TryGetValue(armed.Id, out var y) ? y : 0f);
                Place.GhostYaw = view.ArmedYaw;
            }
            else if (view.ToolId != null && view.ToolId != "place" && view.ToolId != "move") SetTool(view.ToolId);
        }

        void IStudioShell.Leave() => Leave();

        // ---- Dev ----------------------------------------------------------------------------------------------------

        public static string Describe()
        {
            if (state == State.Off) return "not in the Studio";
            if (state == State.Waiting) return $"waiting for {host} (mode {Studio.Mode})";
            int count(StudioItemKind k) => StudioScene.Items.Count(i => i.Kind == k);
            return $"studio {packId}/{levelId} on {StudioScene.Scene.name}: {count(StudioItemKind.Slot)} slots ({StudioScene.Items.Count(i => i.Removed)} removed, {StudioScene.Items.Count(i => i.Moved)} moved), " +
                $"{count(StudioItemKind.Added)} added, {count(StudioItemKind.Prop)} props, {count(StudioItemKind.Line)} lines, {count(StudioItemKind.Group)} groups; " +
                $"selected=[{string.Join(", ", StudioSelection.Keys)}] tool={Tool?.Id} armed={Place.Armed?.Id ?? "-"} snap={SnapStep} " +
                $"cam={StudioCamera.Pivot} zoom={StudioCamera.Zoom:0.##} yaw={StudioCamera.Yaw:0}; dock active={StudioDock.Active}; dirty={draft?.Dirty}; undo={draft?.History.UndoLabel ?? "-"}";
        }
    }
}
