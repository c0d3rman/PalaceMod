using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Studio's public surface (docs/builder-ux.md, "Interfaces"): what the other Studio workstreams build against.

    - W1 (the shell) implements IStudioShell and sets Studio.Shell; every call here forwards to it, and is a harmless
      no-op (or empty result) while the shell isn't there or the Studio isn't running.
    - W2 (test play) implements IStudioPlay (StudioServices.Play) and drives the mode through Studio.SetMode.
    - W3 (requirements, previews) registers a tool (Link), an inspector section, a label provider, an overlay (arrows),
      context items, a view toggle (Built preview) and a delete guard (removing a prerequisite).
    - W4 (terrain) registers a tool (Terrain) with a tool panel (its brush strip), an overlay (brush ring) and
      StudioServices.Terrain (ground height while sculpting, stale paths).
    - The asset library sets StudioServices.AssetBrowser.

    Registrations are kept for the whole game session: register once, in a manager's Awake. Item keys are
    "slot:<host slot name>", "add:<id>", "prop:<id>", "line:<host line name>" and "group:<id>".
*/
namespace PalaceMod.Builder.Studio
{
    internal enum StudioMode { Off, Loading, Edit, Play }

    /// <summary>How Studio.Select combines keys with the current selection.</summary>
    internal enum SelectOp { Replace, Add, Toggle, Remove }

    internal enum StudioItemKind { Slot, Added, Prop, Line, Group }

    /// <summary>Keybind contexts (Keybinds.Register's context:).</summary>
    internal static class StudioContexts
    {
        /// <summary>Edit mode: exclusive (every other key is inert while the Studio edits).</summary>
        public const string Edit = "BuilderStudio";
        /// <summary>Play mode: F5 and the sandbox keys.</summary>
        public const string Play = "BuilderPlay";
    }

    /// <summary>One object of the level as the Studio shows it. Read-only for other workstreams: change the level with
    /// Studio.Edit and the item follows.</summary>
    internal sealed class StudioItem
    {
        public string Key, Id;
        public StudioItemKind Kind;
        /// <summary>Display name ("House 7", "New Tower", a prop's mesh name, a line's name).</summary>
        public string Name;
        /// <summary>The building's type name (slots and added slots), else a kind label.</summary>
        public string TypeName;
        public Vector3 Pos;
        public float Yaw;
        /// <summary>A host slot that the level removes (shown crossed out).</summary>
        public bool Removed;
        /// <summary>Can't be moved or removed (the castle, ActivatorUpgradesThis add-ons).</summary>
        public bool Locked;
        /// <summary>A host slot or line that the level moved or turned.</summary>
        public bool Moved;
        public bool IsCastle;
        /// <summary>Slots: the host slot this is (Slot) or is a copy of (Added). Null otherwise.</summary>
        public string HostSlot;
        /// <summary>The host's costs per level (build first), before the level's overrides. Empty for non-slots.</summary>
        public IReadOnlyList<int> BaseCosts = [];
        /// <summary>The proxy shown in the editor (may be null: lines, the castle).</summary>
        public GameObject Proxy;
        /// <summary>Groups: the member keys.</summary>
        public IReadOnlyList<string> Members = [];
    }

    /// <summary>What the pointer is doing this frame, as seen by the active tool. Ground is null when the pointer isn't
    /// over the ground (or is over the Studio's panels: OverUI).</summary>
    internal sealed class StudioPointer
    {
        public Vector2 Screen;
        public Vector3? Ground;
        /// <summary>The pointer is over a Studio panel or popup: world clicks shouldn't act.</summary>
        public bool OverUI;
        /// <summary>The item under the pointer (Studio's picking), or null.</summary>
        public string HoverKey;

        public bool LeftDown, LeftHeld, LeftUp;
        /// <summary>The left button moved more than 6 px since LeftDown (a drag, not a click).</summary>
        public bool Dragging;
        /// <summary>A left click: released without dragging.</summary>
        public bool Click;
        public Vector2 DownScreen;
        public Vector3? DownGround;
        public string DownKey;

        /// <summary>A right click without a drag (right-drag pans the camera and never reaches tools). Tools see it
        /// only if their Cancel() returned false.</summary>
        public bool RightClick;

        public bool Shift, Ctrl, Alt;
        /// <summary>The snapped point under the pointer (Ground with the Snap step applied unless Alt is held).</summary>
        public Vector3? Snapped;
    }

    /// <summary>A tool in the left column. The shell owns Select, Lasso, Paint, Move, Rotate, Place and Erase; W3 adds
    /// Link, W4 Terrain.</summary>
    internal abstract class StudioTool
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        /// <summary>The status line's one-line hint while active (e.g. "Click a prerequisite · Shift keep · Alt clear").</summary>
        public abstract string Hint { get; }
        public abstract Sprite Icon { get; }
        /// <summary>The key that switches to it (registered with context StudioContexts.Edit and
        /// KeybindFlags.AllowInMenus), or null.</summary>
        public abstract Keybind Key { get; }
        /// <summary>Its place in the tool column (Select 0 ... Erase 900; Link 600, Terrain 700).</summary>
        public virtual int Order => 500;

        public virtual void Activate() { }
        public virtual void Deactivate() { }
        /// <summary>Every edit-mode frame while active (also while the pointer is over a panel: check p.OverUI).</summary>
        public abstract void Update(StudioPointer p);
        /// <summary>Esc or a right click: cancel the operation in progress. Return true if there was one (the key is
        /// consumed); false lets the Studio clear the selection / open its menu.</summary>
        public virtual bool Cancel() => false;

        /// <summary>A panel shown in the palette's place while the tool is active (W4's brush strip), or false.</summary>
        public virtual bool HasPanel => false;
        public virtual void BuildPanel(ModPage page) { }
    }

    /// <summary>Something the Place tool can put down: a palette or library tile.</summary>
    internal sealed class StudioPlaceable
    {
        /// <summary>Stable id (for the 1-9 hotbar and the stored view state), e.g. "slot:House 7", "mesh:Rock_01".</summary>
        public string Id;
        public string Name;
        public Sprite Icon;
        /// <summary>An inert ghost under parent (no colliders, no game scripts), at the origin with yaw 0.</summary>
        public Func<Transform, GameObject> MakeGhost;
        /// <summary>Places one at a snapped pose and returns the new item's key (null if it couldn't). It should make
        /// one Studio.Edit.</summary>
        public Func<Vector3, float, string> Place;
    }

    /// <summary>A label's parts. Providers fill what they know (null = no opinion); the shell merges them in
    /// registration order, later non-null fields winning.</summary>
    internal sealed class StudioLabel
    {
        public Sprite Icon;
        /// <summary>Ring and label edge colour (the colour by kind).</summary>
        public Color? Color;
        public string Name;
        /// <summary>The cost chain, e.g. "2·6·12◉".</summary>
        public string Costs;
        /// <summary>The requirement badge, e.g. "♜2" or "⤳ House 6".</summary>
        public string Badge;
        /// <summary>Clicking the badge (Requirements label mode), or null.</summary>
        public Action BadgeClick;
        /// <summary>Hover card body lines (the ModTooltip under the pointer), or null.</summary>
        public string Card;
    }

    /// <summary>Per-frame world drawing in edit mode (link arrows, the brush ring).</summary>
    internal interface IStudioOverlay
    {
        /// <summary>Every edit-mode frame, after the tool's Update.</summary>
        void Draw();
        /// <summary>When the Studio leaves edit mode (play, leave): destroy or hide what was drawn.</summary>
        void Hide();
    }

    /// <summary>Test play (W2). The shell calls it from F5, Shift+F5, the Play button and the context menu.</summary>
    internal interface IStudioPlay
    {
        bool CanSwap(out string why);
        /// <summary>From the cursor (F5) or normally from the castle (Shift+F5). Studio.View is filled just before.</summary>
        void EnterPlay(bool fromCursor);
        void ReturnToEdit();
    }

    /// <summary>Terrain (W4): the ground while it's being sculpted.</summary>
    internal interface IStudioTerrain
    {
        /// <summary>The ground height at a point if terrain covers it (the navmesh may be stale while sculpting), else
        /// null for the host's ground.</summary>
        float? Height(Vector3 point);
        /// <summary>Strokes since the last rescan: the top bar shows "paths out of date".</summary>
        bool PathsStale { get; }
        /// <summary>Brings the graphs up to date (the path check and F5 call it while stale).</summary>
        void UpdatePaths();
    }

    internal static class StudioServices
    {
        public static IStudioPlay Play;
        /// <summary>The Library tab's browser; the shell defaults it to HostSceneryBrowser.</summary>
        public static IAssetBrowser AssetBrowser;
        public static IStudioTerrain Terrain;
        /// <summary>The asset library's prop resolution (cross-map scenery): a PropBase for a PropDef base string in a
        /// scene, or null to fall back to the host's own scenery.</summary>
        public static Func<UnityEngine.SceneManagement.Scene, string, Level.PropBase> PropResolver;
        /// <summary>The Enemy Studio (R2-C2 installs it). Null: callers fall back to the pack editor's unit page.</summary>
        public static IStudioEnemyEditor Enemies;
    }

    /// <summary>
    /// The Enemy Studio as the Level Studio and the library home call it. Open edits a unit of a draft: from the Level
    /// Studio that's Studio.Draft (its level workspace, so enemy and level edits share one undo history and one save);
    /// from the home it's DesignLibrary.OpenEnemies(). onReturn runs when the player goes back, with the unit's id
    /// (null if they cancelled a new one), after the caller's screen is restored.
    /// </summary>
    internal interface IStudioEnemyEditor
    {
        /// <param name="unitId">The unit to edit, or null to start a new enemy (the editor adds it to the draft).</param>
        /// <param name="returnToLevelStudio">Opened from a running Level Studio: come back to it exactly as it was.</param>
        bool Open(PackDraft draft, string unitId, bool returnToLevelStudio, Action<string> onReturn, out string error);
    }

    /// <summary>The editor state F5 keeps: W1 captures and restores it, W2 stores it across the swap.</summary>
    internal sealed class StudioViewState
    {
        public Vector3 CameraPivot;
        public float Zoom = 1.6f, Yaw;
        public List<string> Selection = [];
        public string ToolId;
        /// <summary>The Place tool's armed StudioPlaceable.Id, or null.</summary>
        public string Armed;
        public float ArmedYaw;
        public string PaletteTab, PanelTab;
        public float PaletteScroll, PanelScroll;
        /// <summary>The ground point under the cursor when it was captured (F5's drop point).</summary>
        public Vector3? Cursor;
        public bool PaletteCollapsed;
    }

    /// <summary>View ▾ options; Changed fires on any change.</summary>
    internal static class StudioViewOptions
    {
        public enum LabelMode { Off, Names, Costs, Requirements }
        public enum LinkMode { Off, Selection, All }

        public static LabelMode Labels = LabelMode.Costs;
        public static LinkMode Links = LinkMode.Selection;
        public static bool BuiltPreview, XRay, SceneryDim, Routes;
        public static event Action Changed;

        public static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Studio view options: Changed handler failed", e); }
        }
    }

    /// <summary>The shell (W1) behind Studio's forwarding calls.</summary>
    internal interface IStudioShell
    {
        PackDraft Draft { get; }
        string LevelId { get; }
        Vector3? CursorGround { get; }
        IReadOnlyList<string> Selection { get; }
        IReadOnlyList<StudioItem> Items { get; }
        StudioItem Find(string key);
        StudioItem ItemAt(Vector3 ground);
        void Select(IEnumerable<string> keys, SelectOp op);
        void Edit(string label, Action<LevelDef> change, string coalesce);
        bool Open(PackDraft draft, string levelId, out string error);
        void Leave();
        StudioViewState CaptureView();
        void RestoreView(StudioViewState view);
        void SetTool(string id);
        string ToolId { get; }
        void Arm(StudioPlaceable placeable);
        void Frame(IEnumerable<string> keys);
        void PickOnMap(string prompt, Func<string, bool> accept, Action<string> picked);
        void RefreshPanels();
        float Ground(Vector3 point);
        bool PointerOverUI { get; }
    }

    internal static class Studio
    {
        /// <summary>Set by the shell when it starts.</summary>
        internal static IStudioShell Shell;

        // ---- State ----------------------------------------------------------------------------------------------

        public static StudioMode Mode { get; private set; }
        public static bool Running => Mode != StudioMode.Off;
        public static PackDraft Draft => Running ? Shell?.Draft : null;
        public static string LevelId => Running ? Shell?.LevelId : null;
        public static LevelDef Level => Draft?.Pack.FindLevel(LevelId);

        /// <summary>After the mode changed (a swap has finished loading, or the Studio closed: Off).</summary>
        public static event Action<StudioMode> ModeChanged;

        /// <summary>The shell sets Edit (editor entered) and Off (left); W2 sets Loading before a swap and Play once
        /// the play scene has started. Raises ModeChanged.</summary>
        public static void SetMode(StudioMode mode)
        {
            if (Mode == mode) return;
            Mode = mode;
            try { ModeChanged?.Invoke(mode); }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Studio.ModeChanged handler failed", e); }
        }

        /// <summary>What the next load of the host inside the Studio session becomes. W2 sets Play before a play swap
        /// and Edit before returning; the shell claims the load (no overlay, editor proxies) only when it's Edit.</summary>
        public static StudioMode NextLoad = StudioMode.Edit;

        /// <summary>The view snapshot W2 keeps across a swap: the shell fills it before EnterPlay and restores it on the
        /// next edit load when it's set (then clears it).</summary>
        public static StudioViewState View;

        public static StudioViewState CaptureView() => Shell?.CaptureView();

        /// <summary>The pointer is over a Studio panel or a popup (edit and play): world clicks shouldn't act.</summary>
        public static bool PointerOverUI => Running && Shell?.PointerOverUI == true;

        /// <summary>The pack an edit load runs with: a copy holding only the level (no wave set, units, assets or
        /// requirements), so problems elsewhere in the pack never stop the layout from being edited.</summary>
        public static DesignPack EditPack(DesignPack pack, string levelId)
        {
            var copy = PackSerializer.Clone(pack);
            var only = copy.FindLevel(levelId) ?? throw new ArgumentException("no such level", nameof(levelId));
            only.WaveSet = null;
            // Without its wave set the level would play the map's own nights, which may spawn from a line the level
            // removes (a validation error). An edit load never applies the overlay, so the copy can keep them all.
            if (only.SpawnLines != null) only.SpawnLines.Remove = null;
            copy.Units.Clear();
            copy.WaveSets.Clear();
            copy.Levels = [only];
            copy.Requires = null;
            copy.Assets?.Clear();
            return copy;
        }

        /// <summary>The ground under the pointer (or the dev cursor) in edit mode.</summary>
        public static Vector3? CursorGround => Mode == StudioMode.Edit ? Shell?.CursorGround : null;

        /// <summary>The ground height at a point (terrain first, then the host's ground).</summary>
        public static float Ground(Vector3 point) => Shell?.Ground(point) ?? point.y;

        // ---- Items and selection ----------------------------------------------------------------------------------

        public static IReadOnlyList<StudioItem> Items => Shell?.Items ?? [];
        public static StudioItem Find(string key) => key == null ? null : Shell?.Find(key);
        /// <summary>The item picked at a ground point (smallest pick radius first), or null.</summary>
        public static StudioItem ItemAt(Vector3 ground) => Shell?.ItemAt(ground);
        /// <summary>After the scene was reconciled with the level (every edit, undo and redo).</summary>
        public static event Action ItemsChanged;

        /// <summary>The selected keys in click order.</summary>
        public static IReadOnlyList<string> Selection => Shell?.Selection ?? [];
        public static event Action SelectionChanged;
        public static void Select(IEnumerable<string> keys, SelectOp op = SelectOp.Replace) => Shell?.Select(keys ?? [], op);

        internal static void RaiseItemsChanged() => Raise(ItemsChanged, "ItemsChanged");
        internal static void RaiseSelectionChanged() => Raise(SelectionChanged, "SelectionChanged");

        private static void Raise(Action handlers, string name)
        {
            if (handlers == null) return;
            foreach (Action h in handlers.GetInvocationList())
            {
                try { h(); }
                catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio.{name} handler failed", e); }
            }
        }

        // ---- Editing ----------------------------------------------------------------------------------------------

        /// <summary>One undo step on the open level (PackDraft.Edit, then the shell prunes empty entries). Edits with
        /// the same coalesce key in a row merge into one step (a drag, a stroke).</summary>
        public static void Edit(string label, Action<LevelDef> change, string coalesce = null) => Shell?.Edit(label, change, coalesce);

        /// <summary>Opens a level of a draft in the Studio (loads its map).</summary>
        public static bool Open(PackDraft draft, string levelId, out string error)
        {
            error = "the Studio isn't available";
            return Shell != null && Shell.Open(draft, levelId, out error);
        }

        /// <summary>Leaves the Studio and its map (back to the pack screen).</summary>
        public static void Leave() => Shell?.Leave();

        /// <summary>Switches tool by id ("select", "lasso", "paint", "move", "rotate", "place", "erase", or a
        /// registered tool's Id).</summary>
        public static void SetTool(string id) => Shell?.SetTool(id);
        public static string ToolId => Shell?.ToolId;

        /// <summary>Arms the Place tool with a placeable (palette tiles, the library, W4's terrain pieces).</summary>
        public static void Arm(StudioPlaceable placeable) => Shell?.Arm(placeable);

        /// <summary>Moves the camera to show the items.</summary>
        public static void Frame(IEnumerable<string> keys) => Shell?.Frame(keys ?? []);

        /// <summary>Asks for a click on the map (an inspector's "pick a slot…"): the status line shows the prompt, a
        /// click on an item that <paramref name="accept"/> allows calls <paramref name="picked"/>; Esc cancels.</summary>
        public static void PickOnMap(string prompt, Func<string, bool> accept, Action<string> picked) => Shell?.PickOnMap(prompt, accept, picked);

        /// <summary>Rebuilds the inspector, palette and the active tool's panel (after a change they don't see).</summary>
        public static void RefreshPanels() => Shell?.RefreshPanels();

        // ---- Extension points ---------------------------------------------------------------------------------------

        internal sealed class InspectorSection
        {
            public string Id;
            public int Order;
            public Func<IReadOnlyList<string>, bool> AppliesTo;
            public Action<ModPage, IReadOnlyList<string>> Build;
        }

        internal sealed class ViewToggle
        {
            public string Id, Label;
            public Func<bool> Get;
            public Action<bool> Set;
        }

        internal sealed class Panel
        {
            public string Id;
            public StudioMode Mode;
            public Func<bool> Visible;
            public Action<ModPage> Build;
        }

        private static readonly List<StudioTool> tools = [];
        private static readonly List<InspectorSection> sections = [];
        private static readonly List<Func<string, StudioLabel>> labelProviders = [];
        private static readonly List<IStudioOverlay> overlays = [];
        private static readonly List<Func<IReadOnlyList<string>, IEnumerable<(string label, Action run)>>> contextItems = [];
        private static readonly List<ViewToggle> viewToggles = [];
        private static readonly List<Func<IReadOnlyList<string>, Action, bool>> deleteGuards = [];
        private static readonly List<Panel> panels = [];

        internal static IReadOnlyList<StudioTool> Tools => tools;
        internal static IReadOnlyList<InspectorSection> InspectorSections => sections;
        internal static IReadOnlyList<Func<string, StudioLabel>> LabelProviders => labelProviders;
        internal static IReadOnlyList<IStudioOverlay> Overlays => overlays;
        internal static IReadOnlyList<Func<IReadOnlyList<string>, IEnumerable<(string label, Action run)>>> ContextItems => contextItems;
        internal static IReadOnlyList<ViewToggle> ViewToggles => viewToggles;
        internal static IReadOnlyList<Func<IReadOnlyList<string>, Action, bool>> DeleteGuards => deleteGuards;
        internal static IReadOnlyList<Panel> Panels => panels;

        /// <summary>Adds a tool to the column (W3 Link, W4 Terrain). Same Id again replaces it.</summary>
        public static void AddTool(StudioTool tool)
        {
            tools.RemoveAll(t => t.Id == tool.Id);
            tools.Add(tool);
            tools.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        /// <summary>A section of the Inspector for selections it applies to, below the shell's own (order 0-99 is the
        /// shell's: name, pose, costs). Built with the selected keys; rebuilt on selection and level changes.</summary>
        public static void AddInspectorSection(string id, int order, Func<IReadOnlyList<string>, bool> appliesTo, Action<ModPage, IReadOnlyList<string>> build)
        {
            sections.RemoveAll(s => s.Id == id);
            sections.Add(new InspectorSection { Id = id, Order = order, AppliesTo = appliesTo, Build = build });
            sections.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        /// <summary>Contributes to each item's label (by key); return null for no opinion.</summary>
        public static void AddLabelProvider(Func<string, StudioLabel> provider) => labelProviders.Add(provider);

        public static void AddOverlay(IStudioOverlay overlay)
        {
            if (!overlays.Contains(overlay)) overlays.Add(overlay);
        }

        /// <summary>Context-menu rows for a selection (empty list: right-click on empty ground, see CursorGround).</summary>
        public static void AddContextItems(Func<IReadOnlyList<string>, IEnumerable<(string label, Action run)>> items) => contextItems.Add(items);

        /// <summary>A switch in View ▾ (W3: Built preview).</summary>
        public static void AddViewToggle(string id, string label, Func<bool> get, Action<bool> set)
        {
            viewToggles.RemoveAll(v => v.Id == id);
            viewToggles.Add(new ViewToggle { Id = id, Label = label, Get = get, Set = set });
        }

        /// <summary>Runs before a delete (Del, Erase, the context menu) with the keys and the delete itself. Return true
        /// to take over: then call proceed later (or never, to cancel). W3 uses it for "this slot is a prerequisite".</summary>
        public static void AddDeleteGuard(Func<IReadOnlyList<string>, Action, bool> guard) => deleteGuards.Add(guard);

        /// <summary>A docked panel on the right side in a mode (W2's sandbox panel in Play), built from ModPage pieces
        /// and rebuilt by RefreshPanels. In Edit mode the shell's own right panel takes the side, so Edit panels show
        /// as an extra tab there.</summary>
        public static void AddPanel(string id, StudioMode mode, Func<bool> visible, Action<ModPage> build)
        {
            panels.RemoveAll(p => p.Id == id);
            panels.Add(new Panel { Id = id, Mode = mode, Visible = visible ?? (() => true), Build = build });
        }

        /// <summary>The merged label for a key from every provider.</summary>
        public static StudioLabel LabelFor(string key)
        {
            var merged = new StudioLabel();
            foreach (var provider in labelProviders)
            {
                StudioLabel l;
                try { l = provider(key); }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Studio label provider failed", e); continue; }
                if (l == null) continue;
                merged.Icon = l.Icon ?? merged.Icon;
                merged.Color = l.Color ?? merged.Color;
                merged.Name = l.Name ?? merged.Name;
                merged.Costs = l.Costs ?? merged.Costs;
                merged.Badge = l.Badge ?? merged.Badge;
                merged.BadgeClick = l.BadgeClick ?? merged.BadgeClick;
                merged.Card = l.Card ?? merged.Card;
            }
            return merged;
        }

        /// <summary>The context rows every registered source gives for a selection.</summary>
        internal static List<(string label, Action run)> ContextRows(IReadOnlyList<string> keys)
        {
            var rows = new List<(string, Action)>();
            foreach (var source in contextItems)
            {
                try { rows.AddRange(source(keys) ?? []); }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Studio context items failed", e); }
            }
            return rows;
        }

        private static readonly List<Action<LevelDef, string>> placeHooks = [];

        /// <summary>Runs inside the edit that places a slot or prop (Place tool, paste, duplicate), with the level and the
        /// new item's key, so a change it makes (W3: auto-link) is part of the same undo step.</summary>
        public static void AddPlaceHook(Action<LevelDef, string> hook) => placeHooks.Add(hook);

        /// <summary>After a slot or prop was placed (its edit is done).</summary>
        public static event Action<string> ItemPlaced;

        internal static void RunPlaceHooks(LevelDef level, string key)
        {
            foreach (var hook in placeHooks)
            {
                try { hook(level, key); }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Studio place hook failed", e); }
            }
        }

        internal static void RaiseItemPlaced(string key)
        {
            if (ItemPlaced == null) return;
            foreach (Action<string> h in ItemPlaced.GetInvocationList())
            {
                try { h(key); }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Studio.ItemPlaced handler failed", e); }
            }
        }

        /// <summary>Runs the delete guards; true if one took over.</summary>
        internal static bool GuardDelete(IReadOnlyList<string> keys, Action proceed)
        {
            foreach (var guard in deleteGuards.ToList())
            {
                try { if (guard(keys, proceed)) return true; }
                catch (Exception e) { Log.For<BuilderManager>().Caught("Studio delete guard failed", e); }
            }
            return false;
        }
    }
}
