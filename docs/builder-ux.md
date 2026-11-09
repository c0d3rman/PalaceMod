# Builder: level studio (UX overhaul)

The Builder's data model, runtime, sessions, assets and sharing (`docs/builder-design.md`) stay. This replaces how a
level is made: the layout editor and the menu pages around it become one in-world tool, the **Studio**, built around
the edit → play → edit loop. Units and wave sets keep their pack-editor pages for now (see "Out of scope").

Everything below was worked out from the code (no game was run). Line numbers are as of `main` at 6829787.

## Decisions

1. **One place to build a level.** A level opens straight into the Studio on its map. Layout, requirements,
   scenery, spawns, terrain, level settings and play-testing all happen there. The pack editor's level page goes
   away; the Levels tab opens the Studio.
2. **A docked, non-modal UI.** A toolbar, a palette, an inspector and an outliner stay on screen around the map, in
   the game's panel look. Menus become popups over the map. The bottom-of-screen key paragraph, the Help tab and both
   first-use hints are removed.
3. **Play is one key each way.** F5 drops the king at the cursor with a sandbox panel, unlimited gold and debug
   controls. F5 again comes back to the exact editor state. Each switch is a fast reload of the host, without the
   cloud transition (about 1 to 2 s; see "Why not play in place").
4. **Requirements on the real mechanism.** "Needs castle level N" and "needs slot X first" are the game's own
   `BuildSlot.activatorBuilding`/`activatorLevel`. They are edited with a link tool, drawn as arrows, and set in bulk
   with presets, including the house chain.
5. **Studio owns its input.** While the Studio is active, only its own keys act. That fixes Del, which today reaches
   UndoUpgrade's Backspace binding instead of the editor's.
6. **Empty map, two kinds.**
   - **Cleared:** a host map with its ground kept and every slot and piece of scenery removed except the castle.
     It's cheap, and W3 builds it.
   - **Flat field:** the host's ground replaced by a generated, sculptable ground layer. W4 builds it, and it is
     also the base for terrain editing.

## The workflow

| Stage | What the designer does | Where |
|---|---|---|
| 1. Start | New level → pick a base: **Empty** or a map (thumbnail tiles) | Builder home → Start popup |
| 2. Shape | Terrain: ground height, cliffs, water, walkable area (stage 1: blockers and scenery) | Studio, Terrain tools |
| 3. Castle and slots | Place build slots from the palette, move, rotate, duplicate, delete | Studio, Select/Move/Place |
| 4. Economy | Costs per level, castle-level requirements, prerequisite links, house chains | Studio, Inspector + Link tool |
| 5. Decorate | Scenery from the palette and the asset browser | Studio, Place + palette |
| 6. Enemies | Spawn lines (move, add, remove), nights (wave set) | Studio (lines), Nights popup → wave page |
| 7. Test | F5 in at the cursor, sandbox (gold, nights, spawn, teleport), F5 out | Studio play mode |
| 8. Share | Save, share code, export | Studio menu → existing `SharingUI` |

Stages 3 to 7 repeat many times, so their actions are one or two inputs each, with the map always visible.

## Screen map

```text
Title / pause menu
  └ Builder home (My Levels · My Enemies)      ModFrame, wide tabs of preview tiles (Designs/LibraryHome.cs)
      ├ level tile ─────────────────────────►  STUDIO (in-world, on the level's map)
      ├ "New level" → Start popup ──────────►  STUDIO
      ├ enemy tile / "New enemy" ───────────►  ENEMY STUDIO (StudioServices.Enemies; else the unit page)
      ├ right-click: Rename · Duplicate · Delete · Export
      └ Import · Export (packs exist only here)
STUDIO
  ├ Edit mode: toolbar · palette · inspector/outliner/level · top bar · status line
  ├ popups: context menu, requirement picker, chain preset, start/base, nights, problems, Studio menu
  ├ Nights → WavePage (ModFrame over the Studio; Back returns to the Studio)
  └ F5 ⇄ Play mode: the level as it plays + sandbox panel
```

Leaving the Studio (Studio menu → Leave) returns to the home (`EditorReturn`; `BuilderEditor.CurrentPlace` counts the home).

## The Studio (edit mode)

Reference 1920×1080 at UI scale 1. There are 24 px gutters at every screen edge, and panels never touch each other
or the edge.

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ [≡] Goblin Pass · My Pack  ●unsaved   [↶][↷]   Snap 1 m ▾   View ▾   ⚠ 2        [▶ Play (F5)]           │ top bar
├────┬──────────────────┬───────────────────────────────────────────────────────┬───────────────────────────┤
│ ▣  │ Slots Scenery    │                                                       │ Inspector Outliner Level  │
│ ⬚  │ Spawns Library   │                                                       │ ───────────────────────── │
│ ◌  │ ┌──┐┌──┐┌──┐┌──┐ │                   the map (3D)                        │ 3 Houses · 1 Tower        │
│ ✥  │ │⌂ ││♜ ││⚒ ││⛏ │ │     slot labels, rings, link arrows, ghosts          │ Cost        —  (mixed)    │
│ ⟳  │ └──┘└──┘└──┘└──┘ │                                                       │ Needs  ♜ Castle lv 2  ▾   │
│ ⤳  │ House   2◉ +1    │                                                       │ Upgrades ≤ castle  ☐      │
│ ▲  │ Tower   3◉       │                                                       │ [Chain…] [Group] [Delete] │
│ ⌫  │ … (scrolls)      │                                                       │ … (scrolls)               │
├────┴──────────────────┴───────────────────────────────────────────────────────┴───────────────────────────┤
│ House 7 · 2◉/6◉/12◉ · needs House 6 · x 41 z -12        3 selected        Click place · Shift keep · R turn│ status
└──────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Top bar** (56 px high): the Studio menu (≡), the level and pack names, a save state dot, undo and redo (with
  their labels in the tooltip), the Snap dropdown (Off / 0.5 / 1 / 2 m, plus a turn step of 15° or 45°), the View
  dropdown (Labels: off / names / costs / requirements; Link arrows: off / selection / all; Built preview; X-ray;
  Scenery dim; Routes), the Problems badge (hidden at 0; opens the Problems popup, where each row selects and frames
  its object), and Play.
- **Toolbar** (left, 56 px tiles in one column): the tools, each a `ModTile` with its icon, the key in the corner
  badge, and its name in the tooltip.
- **Palette** (300 px, collapsible with Tab). Tabs:
  - **Slots:** every slot kind on the host, plus every other kind as the game's standard building (see "Building
    kinds the host lacks"). Shows the icon, the cost chain and the count on the map.
  - **Scenery:** the host's scenery meshes, with a text filter.
  - **Spawns:** spawn lines: moving the host's, adding new ones, and removing the host's (`spawnLines.remove`: deactivated at load, so no enemies, routes or markers; Studio Delete toggles it, undoable).
  - **Library:** the asset browser slot (see "Asset browser slot").

  Clicking a tile arms the Place tool with that kind. Keys 1 to 9 recall the last nine kinds placed (a hotbar).
- **Right panel** (340 px). Tabs:
  - **Inspector:** the selection.
  - **Outliner:** every object, filterable.
  - **Level:** name and description (each value under its label at the panel's full width), the map, base and
    nights, the path check with its result, and the changes as one row per count ("Slots added 36").

  The panel scrolls (`ModScrollArea`).
- **Status line** (32 px): on the left, what is under the cursor (name, cost chain, requirement, coordinates). In the
  middle, the selection count. On the right, the active tool's one-line hint. Nothing else.
- Panels hide during play mode. With nothing selected, the Inspector shows the level summary (counts by kind, "3
  unreachable", "2 without requirements").

### Tools

| Tool | Key | Does |
|---|---|---|
| Select | V | Click selects. Shift-click adds, Ctrl/Cmd-click toggles. **Dragging from empty ground draws a marquee**, using the same Shift/Ctrl rules. Dragging a selected object moves the whole selection. |
| Lasso | L | Drag a free shape. Selects the objects whose ground point falls inside it, with Shift/Ctrl as above. |
| Paint select | B | A ground**Dev:** `builder-terrain flat [cell] | host [cell] | brush (wheel+Alt sizes it). Dragging adds what it passes over, and Alt+drag removes. |
| Move | G | Grab: the selection follows the cursor. Click commits; Esc or a right click (without a drag) cancels. X/Z lock an axis, and arrow keys nudge by theking <x> <z> | walk <dx> <dz> <s> | probe <x> <z> [y] [r] | snap | scan | grid | encode | save <pack> | clear`.
`host` puts a sculptable grid on the map's own ground; `walk` moves the king through his CharacterController (what he
can climb); `probe` lists the colliders he meets around a point. `save` writesstep (Shift ×5). |
| Rotate | R | Turns the selection about its centre by mouse. `,` and `.` step by the turn step (they also work in Select). X switches to Tilt and Z to Roll (again, or Esc, back to Turn; the status line shows it): dragging or `,`/`.` then tips scenery about its own sideways (Tilt) or forward (Roll) axis, in place; slots and lines don't tilt. The Inspector has Tilt and Roll fields for scenery. |
| Place | (palette) | A ghost of the kind follows the cursor. Click places. Shift keeps placing. `,`/`.` turn the ghost. |
| Link | K | Click dependents (or use the selection), then click their prerequisite: a slot or the castle. See "Requirements". |
| Terrain | T | The terrain brushes (see "Terrain"). |
| Erase | E | Click or drag over objects to delete them. |

Snapping: positions use the Snap step (default 1 m); hold Alt while dragging or placing to place freely. Turns use
the turn step.

### Keys (rebindable, Builder section, context `BuilderStudio`)

| Key | Action |
|---|---|
| Del, Backspace | Delete the selection. On a host slot that means "remove from the map"; on a removed one, restore. |
| Ctrl/Cmd+Z, Ctrl+Y, Cmd+Shift+Z | Undo, redo (the draft's `EditHistory`). |
| Ctrl/Cmd+D | Duplicate the selection and start Move with the copies. |
| Ctrl/Cmd+C / V | Copy; paste at the cursor (keeps relative layout). |
| Ctrl/Cmd+A | Select all (of the outliner filter, if one is set). |
| Ctrl/Cmd+G, Ctrl/Cmd+Shift+G | Group, ungroup. |
| F / Home | Frame the selection / the whole level. |
| H, Alt+H | Hide the selection, show all (editor view only). |
| X | X-ray (markers and labels through scenery, scenery at 35 %). |
| Esc | Cancel the operation in progress, else clear the selection, else open the Studio menu. |
| F5 / Shift+F5 | Play from the cursor / play from the start (king at the castle, normal day 1). |
| Ctrl/Cmd+S | Save. |
| WASD / arrows (nothing selected) | Pan. Ignored while Ctrl, Cmd or Alt is held, so Ctrl+A and Ctrl+D never pan. |
| Q / E | Turn the view (replaces K/L). |
| Wheel | Zoom. |
| Right-drag, middle-drag | Pan (grab the ground). |
| Right-click (no drag) | Context menu for the object under the cursor (selecting it first if it isn't selected), or for the selection on empty ground. In Place, Move, Rotate or Link it cancels the operation instead. |

**Right-click vs pan.** On right-button down, record the screen position. Once the pointer moves more than 6 px
while the button is held, it becomes a pan (grab the ground point under the press) and no menu opens on release. A
release without crossing 6 px is a click: it cancels the operation in progress, else opens the context menu. Middle
drag always pans. This also lets the designer pan while placing without losing the armed kind.

### Input ownership (fixes Del)

Today `Keys.Delete` is `KeyCode.Delete` (`Level/BuilderLevelManager.cs:224`). That key is forward-delete, fn+⌫ on a
Mac. The key a Mac labels "delete" sends `KeyCode.Backspace`, which is UndoUpgrade's key
(`Features/Buildings/UndoUpgradeManager.cs:301`, no context). In the layout editor it's daytime and no menu is open,
so UndoUpgrade's `Update` runs its undo with an empty stack and says "Nothing to undo today."
(`UndoUpgradeManager.cs:686-694`). Nothing keeps other features' keys out of the editor.

The Studio owns input this way:

1. Every Studio key is registered with `context: "BuilderStudio"` and `KeybindFlags.AllowInMenus`, because the dock
   is a `UIFrame`. Delete is bound to Backspace, with Delete as a second binding.
2. **New in `Core/Keybinds.cs` (additive):** `Keybinds.ClaimExclusive(context, whileActive)`. While it's true,
   *every* keybind outside that context is inert (`Keybind.Active` returns false), and Rewired game actions on the
   claimed context's inputs are blocked, as `ClaimContext` already does. That's the general fix. `ClaimContext` only
   blocks game actions on the same input, not other mod keys (`KeybindsManager.cs:73`).
3. The king is frozen (`SetPlayerFreezeState(true)`, as today), so game actions on other keys do nothing.
4. In play mode the claim is dropped (the level plays normally); only F5 and the sandbox keys keep a play-context
   claim.

### Text and hover descriptions

- Text never clips or is cut into uselessness: popups widen to their longest row (up to the screen less 48 px) and a
  row still too long wraps; side-panel rows wrap onto a second line (`StudioDock.Compact` with the page width); tile
  labels shrink to 60 % before an ellipsis.
- Names with a hidden meaning (tags, slot kinds, upgrade choices, weapons, behaviours, stats, flags) get their tooltip
  from one table, `Features/Builder/Model/Descriptions.cs` (`Descriptions.For(kind, name)`), written from the
  decompiled game; the Enemy Studio and agent kit read the same table.

### Mouse on the UI never reaches the game

The game's menus read the mouse at the controller level, but gameplay reads Rewired actions, and some of it ignores
the frozen player (`PlayerScept` raises the sceptre on every "Interact"). `KeybindsManager.MouseGate` switches off the
mouse bindings of every game action while the pointer is on a `ModDock` panel or a mod menu (ModFrame, ModPopup, the
Studio's frozen dock) is the active frame, and back on once no mouse button is held.

### Placement audit

`dev.py cmd "builder-studio audit"` places every palette kind of the open map (Slots, Scenery, every Library item),
checks the placed object has the ghost's meshes at the ghost's size, resizes one of two copies of each prop to check
the other stays put, and undoes everything. Root causes it caught: `LayoutScene.Props` counted the Studio's own prop
copies (renamed to their item keys) as host scenery, so a resized copy set the host base's scale for every copy of
that mesh; and the library ghost was drawn at scale 1 while placing used the start scale.

"Placing X made Y" was the click, not the placement: hovering a tile selects it, and `ModScroll` scrolled the
selection into view, so a tile near the palette's edge scrolled up under the still pointer, the tile now under it was
selected and scrolled to in turn, and the click landed one or two rows further on (Defense Tower lvl 1 → Stepping
Stone, Rock A Alternate: the tiles 3 and 6 after it, three per row). Hover selection no longer scrolls. The library
tiles' click path is checked with the simulated mouse (`uikit mouse <tile>`, `uikit click`, then `builder-studio
state`); `builder-studio focus` shows the active frame's selection, focus and last applied element.

### Selection and multi-edit

- The selection is an ordered list of item keys (`slot:<name>`, `add:<id>`, `prop:<id>`, `line:<name>`,
  `group:<id>`). The order is click order (the house chain's "in selection order" uses it). Marquee and lasso
  append in distance order from the press point.
- Picking: the item whose ground point is nearest within its pick radius (today's `ItemAt`). Overlaps are resolved
  by the smallest pick radius first, so a house next to the castle is picked rather than the castle.
- Highlight: the selected ring is bright with an outline on the proxy (renderer copy with an emissive rim material),
  hover is half-bright, and a removed host slot shows the cross.
- **Inspector with several objects:** shows the fields they share. A field whose values differ shows "—" dimmed. Any
  change applies to all and is one undo step ("Set build cost on 6 slots").
  - Costs per level show when all selected slots have the same number of levels. Otherwise a "Costs differ by kind:
    select Houses only ▸" row narrows the selection.
- Moving or rotating many keeps their relative layout. Castle and locked pieces (`ActivatorUpgradesThis` add-ons such
  as a mill's fields) are skipped with a one-line notice ("Castle stays put"). Add-ons move with their owner's
  proxy: dragging a mill moves its fields.

### Groups

- `LevelDef.groups` (editor metadata, ignored when playing): `[{ "id": "g1", "name": "North farm", "members":
  ["add:slot-3", "prop:prop-7"] }]`.
- Clicking a member selects the group, and Alt-click selects the member alone. The Outliner shows groups as
  collapsible rows.
- Deleting a member removes it from the group. A group with fewer than 2 members is dropped.

### Outliner

- Filter chips: Slots, Castle, Scenery, Spawns, Groups, Changed, Problems, No requirement. There's also a text filter.
- Rows show the icon and name on the first line, and the kind, cost chain, requirement and state on a smaller second
  line (side by side, a 340 px panel cut both to a few letters). Clicking selects (Shift/Ctrl as on the map) and
  double-click frames. It scrolls (`ModScrollArea`) and is never cut short.

### Context menu (ModPopup at the cursor)

Object: Properties (focuses the Inspector), Needs ▸ (Castle level 1-5 / pick a slot… / none), Chain ▸, Duplicate,
Group, Select all of this kind, Reset to the map's (host slots that were moved), Delete or Remove/Restore, Play from
here.

Empty ground: Paste, Place last kind here, Play from here, Frame all.

### Studio menu (≡ or Esc with nothing to cancel)

Save / Save a copy, Share… (`SharingUI.OpenPack`), Pack editor (opens the pack screen over the Studio; closing it
returns), Leave (back to the pack screen, map unloaded), Studio settings (label density, edge pan on/off).

### "What's what": labels, colour, previews

- **Colour by kind** (ring and label edge):
  - castle: gold
  - houses and income buildings (house, mill, mine, harbour, field): warm yellow
  - military (tower, barracks, archery range, hero's quarter): red
  - walls and gates: grey
  - special (temple, shrine, blacksmith and the like): violet
  - spawn lines: crimson
  - scenery: pale green
  - selected: white

  The kind comes from the slot's building name, using the Economy planner's icon table. The colour key is in the
  View dropdown.
- **Labels** are one pooled screen-space canvas, as in `Features/Previews/IncomeLabelManager.cs`. Each slot gets an
  icon (`Economy/PlannerIcons.Kind(typeName)`), a short name, the cost chain ("2·6·12◉") and a requirement badge
  ("Castle 2" or "after House 6"; the game's fonts have no chess or arrow glyphs).
  - Density follows zoom: icons only when zoomed out, full labels when zoomed in, and always full on hover and
    selection.
  - The View dropdown picks what the text shows.
  - Labels never overlap: lower-priority labels hide (selection > hover > castle > others by distance).
- **Built preview** (View ▾ "Built preview", or hold the "Studio: built preview" key, Alt): each slot proxy shows its
  built building at level 1, a ghost in the game's `BuildingInteractor.previewMaterial`. With one slot selected, the
  inspector's Preview stepper shows it at any level, solid. `LayoutScene.Proxy` keeps the stripped building hidden
  with a `ProxyLook` (per-upgrade `replacementMesh`, `objectsToActivate`/`objectsToDisable` of the first branch,
  original materials; serialized, so `Instantiate` copies keep it); `LayoutScene.ShowBuilt(proxy, level, ghost)` and
  `BuiltLevels(proxy)` drive it (`Studio/Requirements/BuiltPreview.cs`).
- **Hover card** (`StudioLabel.Card` from `SlotKinds.Provide`): kind, costs per level, total income per level, what it
  needs (with the castle level it waits for through its chain), what it unlocks, and its problems (a loop, a
  prerequisite that isn't on the map).
- **X-ray** (X, `StudioViewOptions.XRay`): the scenery (`EmptyBase.Scenery`, the same set an empty base hides) is drawn
  in the game's see-through preview material instead of at 35 % alpha (FlatKit materials have no alpha); the shell
  draws rings with ZTest Always, and labels are UI.
- **Requirement arrows** (View ▾ Links, `RequirementArrows`): curved ground arrows from prerequisite to dependent,
  verdigris for slot links and gold for castle-level links (thicker for higher castle levels). The level shows in each
  slot's badge ("Castle 2", "after House 6 2") instead of a label on the arrow. Off / the selection's (and the hovered slot's) /
  all; all while the Link tool is active, where the hovered slot's chain to the castle is brightened and the rest
  dimmed. Add-ons' links (walls to their gate) aren't drawn.

### Path check

The "Check enemy paths" button is in the Level tab and in View ▾ Routes. It runs automatically, debounced 1.5 s
after an edit that moves solid things, while Routes is on. Each check is a rescan (120 to 220 ms warm on
Nordfels). The result is a status chip in the Level tab and the top bar's Problems badge ("Blocked: Forest").

### Unit positions

A training building's units (barracks, archery range, hero's quarter...) are children of its slot, at offsets the
map's author placed around the building. `PathfindMovementPlayerunit.Start` makes wherever a unit stands its home
(`homePosition`, `homePositionOriginal`): it returns there each morning and respawns next to it, and the build preview
draws it there. A slot the Builder moves, turns or copies carries the offsets along, so on a river bank the units
stood (and were previewed, and appeared) in the water until they first walked.

- **Play** (`Level/UnitHomes.cs`, pure part `Level/UnitSpread.cs`, unit-tested): right after the level-start rescan
  (`GroundGraphs.Scan`, so on the graphs the level plays on), every unit home of every slot on a Builder level that
  is more than 0.6 m off its own graph (`Seeker.graphMask`) moves to the nearest walkable point, 0.8 m further in,
  or, if a unit placed before it is within 1.3 m, the nearest free walkable spot in rings around it: a formation
  stays spread out. Never across water: a new home must be reachable from its building by a walk at most 1.5x the
  straight line + 4 m (`UnitSpread.Flood`: Dijkstra on a 1 m lattice of the unit's walkable ground, 24 m round the
  slot), so the far bank of a river, reachable only round by a ford, doesn't count; a unit whose nearest ground is
  over there walks back towards its building to the first ground on the way (one standing in the water within
  0.6 m of the far bank counts as off the ground too). The Studio's "Move units onto ground" does the same.
  The unit's transform moves (its Start, the build preview and respawns use it); units that
  already started (a building loaded built) get `homePosition`/`homePositionOriginal` too. Logged with the rescan;
  `builder-units [live] | settle` (dev).
- **Data:** `units: [[x, z], ...]` on a `buildSlots` edit or add: per unit spot (an Outpost holds 5 upgrade choices
  x 12 units on 12 shared spots; units on one spot move together, and play moves them to one new spot), metres from the
  slot's centre in its frame (x right, z forward; turns with the slot); null entries and absent = the map's. Applied
  by `LevelOverlay` before Start; play's settle still runs after.
- **Studio** (`Studio/Units/`): a selected or hovered training slot shows a disc per unit (verdigris); a unit on
  unwalkable ground (terrain water, blocked or steeper than 30° on the grid, else off its graph) is a red disc with a
  cross, shown at all times and listed in Problems ("Outpost camp: 2 of 4 units stand off walkable ground"). With
  the Select tool, dragging a selected slot's disc moves that unit (one undo step). The Inspector's Units section
  says how many stand off the ground and has "Move units onto ground" (the same spread as play, on the Studio's
  ground) and "Reset unit positions"; the context menu has "Move units onto ground". Dev: `builder-units studio |
  ground | set <key> <i> <x> <z> | reset`.
- **Agent kit:** the reference lists each map slot's `units`; `check` warns about units off walkable ground;
  `set_units` places them.

### Removed

- `LayoutHud`'s three-line key paragraph (`LayoutEditor.HudText`) is replaced by the status line's one-line tool hint.
- `LayoutMenus` is deleted: the Add/Level/Help tabs move into the palette, the top bar, the Studio menu and the Level
  tab, and the Selected frame becomes the inspector.
- The first-use hints go: `LayoutMenus.FirstUseHint` and `BuilderEditorManager.FirstUseHint`. Their hidden config
  keys stay bound (harmless) or are dropped through `ConfigMigrations`.
- The "Custom run: … Kept apart from your saves and scores." notice (`SessionLauncher.OnSceneLoaded`) doesn't show for
  Studio sessions, edit or play.
- The game's in-level tips (formations, zoom, sprint, "hold to preview", unit counts, enemy descriptions) come from
  `AfterTutorialTipManager.Update` (decompiled `AfterTutorialTipManager.cs:66-78`). A prefix that returns false while
  `Studio.Running` (edit and play) suppresses them. Don't disable the component: its `OnDusk` saves the game through
  `DayNightCycle`, and the Update prefix leaves that alone. The tutorial level's `TutorialManager` is never a host.

## Requirements (castle level, prerequisites, house chains)

### The game's mechanism (decompiled `BuildSlot.cs`)

- **One activator per slot:** `activatorBuilding` (a single `BuildSlot`) plus `activatorLevel`.
  - With `startDeactivated`, `Start()` hides the slot and listens to the activator's `OnUpgrade`.
  - `Activate()` shows it once `activatorBuilding.Level > activatorLevel` (lines 589-631).
  - So `activatorLevel = 0` means "once it's built", and `N` means "once it reaches level N+1".
- **"Needs castle level N"** is therefore activator = the castle slot, `activatorLevel = N-1`. The castle's build is
  level 1.
- **Locked slots are hidden entirely.** The only hint is the ghost the game draws while the player previews the
  activator's upgrade (`GetBlueprintPreviewsThatWillUnlockWhenUpgraded`, 558-578).
- **Upgrade cap:** `requiredRootLevelDifference` (default -100 = no cap). The root is the top of the activator chain,
  in practice the castle. `CanBeUpgraded` (251-277) blocks going past `root.Level - difference`; 0 means "never above
  the castle's level". The initial build is never capped.
- **`activatorUpgradesThis`:** an add-on (a mill's fields, wall pieces) built and upgraded through its activator. It
  is not editable as a requirement and moves with its owner.
- **One prerequisite per slot (decided).** The Studio uses only the native mechanism: a slot needs one slot or the
  castle at a level. "Castle level 3 *and* House 6" is expressed by chaining: link to House 6, and if House 6 needs
  castle level 3, the dependent inherits it (shown as "needs House 6 (castle 3 via House 6)",
  `LevelRequirements.CastleLevelVia`). There is no mod-side AND gate. The Link tool replaces a slot's prerequisite.

### Data (additive; `DesignPack` version 2 with a migration)

`BuildSlotEdit` and `BuildSlotAdd` get:

```jsonc
"requires": { "slot": "castle" | "<host slot name>" | "add:<id>" | null, "level": 0 },   // null object = keep the host's / base's
"upgradeCap": 0                                                                            // requiredRootLevelDifference; null = keep, -100 = none
```

- `slot` absent with the object present means "available from the start" (`startDeactivated = false`, no activator).
- `"castle"` resolves to the host's castle slot.
- `level` is the activator level, so 0 = built. The UI shows "Castle level N" as `level = N-1`.
- `upgradeCap` takes -10..10 or -100. A cap only means something on a slot whose chain reaches the castle (the root is
  the top of the activator chain; a slot available from the start is its own root, so cap 0 stops it at level 1).
- `Migrations.Steps[1]` (`MoveActivatorsToRequires`) turns `add.activator`/`add.activatorLevel` into `add.requires`
  (an activator naming another added slot becomes `add:<id>`) and drops the old fields. Tested in
  `tests/PalaceMod.Tests/RequirementsTests.cs`.
- Code: `Model/Requirements.cs` (plain C#): `SlotRefs` (refs and Studio keys), `LevelBases`, `HostSlots` (the host's
  own prerequisites, from `LayoutScene.HostSlots(scene)`), and `LevelRequirements`: `Explicit`/`Set`/`SetCap` for
  one slot, `Effective` (every slot's prerequisite as it will play), `ChainUp`, `Dependents`, `WouldCycle`, `Cycles`,
  `CastleLevelVia`, `ForgetSlot`.
- The validator refuses loops among the level's own requirements, unknown added slots, a slot needing itself, and
  out-of-range levels and caps; it warns about a slot that needs a removed (or cleared-away) host slot. Host slots'
  own prerequisites, the castle and add-ons are only known with the map loaded, so the overlay checks those.
- **Applying (`LevelOverlay.ApplyRequirements`):** after cloning and before activation, set `activatorBuilding`,
  `activatorLevel`, `startDeactivated` and `requiredRootLevelDifference` on host slots and clones alike.
  `BuildSlot.Start` then builds `requiredRoot`, `isActivatorOf` and the rest itself. At load, with the whole graph
  known (host prerequisites included), a requirement on the castle or an add-on is skipped, a missing prerequisite
  becomes "castle level 1", a level past the prerequisite's last is clamped, and a requirement that would close a
  loop is replaced by "castle level 1" (`BuildSlot.Start` walks the activator chain and would never return), each
  with a warning.
- Checked in game (`builder-req`, Nordfels): castle level 2 and slot links appear exactly when the game's own
  preview (`GetBuildSlotsThatWillUnlockWhenUpgraded`) lists them; a loop through a host slot's own prerequisite is
  broken with a warning.
- **Removing a prerequisite:** deleting a slot that others need offers (popup, default first): "Link its dependents
  to what it needs" (the chain closes up) / "Make them available from the start" / Cancel. Today removal silently
  hides every slot the removed one unlocks (`LevelOverlay` removal comment); the validator now warns about any
  dependent of a removed slot.

### Editing

Code: `Studio/Requirements/` (`BuilderRequirementsManager` registers everything with the Studio in `Awake`).

- **Inspector "Requirements" section** (`RequirementInspector`, order 200): "Needs" (click: From the start / Castle
  level 1-N / A slot on the map…, which uses `Studio.PickOnMap`, then "after: built / level 2 / …"); a "Castle level"
  or "After" stepper when every selected slot needs the same prerequisite; "Upgrades ≤ castle level" (cap 0) with an
  "Allowance" number (cap n); "Chain…"; for one slot, "Unlocks" (click selects them) and "Preview" (built look per
  level). Multi-select sets them all, and mixed values show "—".
- **Link tool (K, `LinkTool`):**
  1. The selection when the tool starts are the dependents; with none, a click picks one. Ctrl/Cmd-click adds or drops
     more.
  2. A click on another slot (or the castle) is the prerequisite. The castle asks for a level; a slot with more than
     one level asks "after: built / level 2 / …". The link replaces each dependent's prerequisite.
  3. Shift on that click keeps the tool for the next link; otherwise it returns to Select. Alt-click makes a slot
     available from the start. Esc or a right click drops the dependents.
  4. A link that would make a loop is refused with a notice (`LevelRequirements.WouldCycle`).

  While K is active, all link arrows show, and hovering a slot highlights its whole chain to the castle.
- **Context menu** rows on slots: Needs… (the same chooser), Chain…, From the start.
- **Deleting a prerequisite** (`Studio.AddDeleteGuard`): "Link them to what it needed" / "Make them available from
  the start" / Cancel (`LevelRequirements.ForgetSlot`), then the delete.
- **House chain preset** (`ChainPopup`, `ChainPreset` is plain C# and unit-tested; Inspector "Chain…" or the context
  menu, on a selection of slots; one slot chains every slot of its kind). A popup:
  - **In selection order:** the first selected needs the castle; each next one needs the previous one.
  - **Nearest neighbour:** a greedy path from the castle: the selected slot nearest the castle needs it, and each next
    one is the nearest unlinked slot to the last.
  - **Branching** (default): a minimum spanning tree (Prim's) over the selection plus the castle, rooted at the
    castle, with each slot needing its tree parent. This matches how vanilla maps fan houses out.
  - Options: "First link: castle level [1]" and "Each link: built / level 2…". The "every N-th link needs castle level
    +1" option is dropped: with one prerequisite per slot, a slot can't need both its neighbour and a castle level.
    The arrows preview the result live, and Apply is one undo step.
- **Auto-link while placing** (View ▾ "Auto-link new slots", on by default; `Studio.AddPlaceHook`, inside the placing
  edit): a new house needs the nearest house within 25 m, else castle level 1; other kinds need castle level 1.
- **Labels** (`SlotKinds.Provide`, a label provider): kind colour, the planner's icon, the cost chain with the level's
  overrides, and the requirement badge ("Castle 2" castle level, "after House 6" a slot, "start" from the start).
- Dev: `builder-req click [ctrl] [shift] [alt] <key>` (the Link tool's click), `link <key>|<key> > <key> [level]`,
  `chain branching|nearest|order [apply|popup]` (over the selection), `select <key>|<key>`, `view links off|selection|all / built on|off / xray on|off`, `label <key>`, `needs`; on a live level
  `slots`, `upgrade <slot>`, `unlocks <slot>`, `proxy <slot> [level]`; `import <pack.json>`, `play <pack> <level>`,
  `base cleared|flat <map>`, `start <pack> [create <map> <0|1|2> <name>]`, `thumb`.

### Empty-map default

On an empty base, new slots default to "needs castle level 1": available once the castle is built (the shell sets
it when placing, and auto-link may pick a house instead). A slot with `requires` absent would otherwise inherit its
base's activator, which is removed on an empty map, so it would never appear: `LevelOverlay` and
`LevelRequirements.Effective` treat such an added slot as "castle level 1", so it plays anyway.

## Play mode (test play)

### Flow

1. **F5** (or ▶) in edit mode stores the editor state in memory: camera pivot, zoom and yaw, the selection, the tool
   and armed kind, panel tabs and scrolls, and the cursor's ground point. The draft and its undo history already live
   in `PackDraft` and survive.
2. It builds the test pack in memory (no save needed): the draft with only this level, its wave set and the units
   that set uses. Problems elsewhere in the pack don't block it. Errors in those parts block it with the Problems
   popup.
3. It reloads the host in **play** state through the fast swap (below). The overlay is applied as for a real run.
4. On load, the king is teleported to the stored cursor point (`PlayerMovement.TeleportTo`, as Teleporters do,
   `TeleporterManager.cs:201`), unless Shift+F5 started it normally. The sandbox panel opens and unlimited gold is on.
5. **F5** in play (or the pause menu's first mod row "Back to the Studio", or the end screen's "Back to the Studio")
   stops the music, then reloads the host in **edit** state and restores the stored editor state.
   - The game's own exits come back too: in Studio play, `TransitionFromLevelToLevelSelect` (pause menu Back to Map,
     the resign popup) and `TransitionFromEndScreenToLevelSelect` (end screen Continue, relabelled "Back to the
     Studio") turn into the same swap, so no path out of a test leaves the editor. The end screen's Try Again restarts
     play as the game does (the king starts at the castle).

### Fast swap and "Why not play in place"

**Play in place, without a reload, is not feasible robustly:**

- The edit scene's real slots have already run `Start()`. They are deactivated and replaced by inert proxies
  (`LayoutEditor.Enter`).
- The overlay must run after Awake and before Start. Clones, activator wiring and removals all depend on it: removal
  before Start keeps a slot out of its activator's `isActivatorOf`; cloning a started slot copies runtime lists.
- Returning from play would also have to undo everything play changed: buildings, gold, day and night, dead units,
  enemies. Savestates can't do that without a reload either (`SaveStates.Restore` reloads the scene).

**So each switch is a scene reload, made fast:**

- One session spans both states. `SessionLauncher.LaunchStudio(request, StudioSessionKind.Edit | Play)` starts it,
  and on the same host only swaps the session's pack, level and wave set in place (no `Ended`, the stand-in scores
  and `EditorReturn`'s return point stay), then reloads through `TransitionFromNullToLevel(host)`. `EditorReturn`
  ignores it because the scene is the session's host. `Studio.NextLoad` tells the shell whether to claim the load
  (Edit) or leave it to the overlay (Play). The play pack is `TestPacks.Play` (the level, its wave set and the units
  it reaches, unit-tested), the edit pack `Studio.EditPack`.
- During a Studio swap, `cloudFadeInTime` and `cloudFadeOutTime` (`SceneTransitionManager`, fields read in
  `SceneTransitionAnimation`) are set to 0.12 s and restored after. The load itself is the cost. Each switch logs
  its time (`Studio: play ready in … ms`, `edit ready in …`). Measured on Nordfels in a pool instance (Apple
  Silicon, native, other instances running), three round trips: edit → play 1.0-1.3 s; play → edit 1.5-1.6 s, of
  which the scene load is 0.85-1.0 s and the shell's edit Enter ~0.6 s (proxies kept across reloads of the same
  host). Load time varies with machine load (one play → edit took 2.6 s with a 1.9 s scene load). Play → play
  (Restart) 0.9 s. From the title the first load takes about 5 s.
- The menu stays visible under the clouds, as today.
- The pre-level loadout, perk screen and "Custom run" notice are skipped. The night-warning `Notify` stays: it's the
  designer's own text, worth seeing in a test. The loadout is `TestLoadout` (last used on the map) as today.

### Sandbox panel (play mode, right side, collapsible with Tab)

Non-modal dock like the Studio's. Keys in brackets (context `BuilderPlay`).

| Row | Control | Backed by |
|---|---|---|
| Gold | Unlimited (on) · number | `PlayerInteraction.SpendCoins` postfix gives every coin back, and the balance is kept at ≥ 999 (set on the field, so networth and income stats don't move); `DebugActions.SetGold` for a set value (a value under 999 turns Unlimited off) |
| Night | Start night / End night [N] · Jump to night [number] | `DebugActions.StartNight`, `EndNight`, `JumpToNight` |
| Speed | 0.5 · 1 · 2 · 4 · 8 [`[` `]`] | `DebugActions.SetSpeed` |
| King | God mode · Revive | `SetGodMode`, `ReviveKing` |
| Kingdom | Invulnerable · Heal all · Build all · Max upgrade nearest | `SetInvulnerableKingdom`, `HealAll`, `BuildAllBlueprints`, `MaxUpgrade(NearestBuilding())` |
| Enemies | Kill all [K] | `KillAllEnemies` |
| Spawn | unit picker (catalog + pack units, icons) × count [1-50] · elite · at: cursor click / a spawn line | new (W2): instantiate like `Spawn.Update` (decompiled `Spawn.cs:140-180`), with `IUnitFactory.Resolve` for pack units, the night's difficulty scaling and elite material, so enemies path to targets as in a wave |
| Teleport | T then click the ground (Shift-click keeps it armed, right click cancels) · Bring followers toggle | `PlayerMovement.TeleportTo` + followers via `GetNearestGroundPosition` (as `TeleporterManager.cs:201-226`) |
| Restart | Restart play from the same spot (R) | the same fast swap, Play → Play |
| Back | Back to the Studio [F5] | swap to Edit |

- Play mode starts in day 1 planning time. "Start night N" jumps straight to that night.
- Spawning drives a one-off game `Spawn` (count, 0.12 s interval, elite) frame by frame with the night's
  `difficultyMulti`, so perks, elite material and the template-aware spawn patch all apply. A click spawn uses a
  stand-in line at the point.
- Speed, god mode and invulnerable buildings are global; the sandbox puts them back when the Studio closes.
- `GameplayImpact` is marked (custom content already marks the run). Scores aren't recorded for Studio play sessions:
  `ScoreStore` is skipped while `StudioSession` is active.
- Using Back during the night, or on the end screen, stops the music first (see bugs).

### Bugs to fix (root causes from the code)

1. **"Leaving the test play took me out of the editor completely."**
   - `LayoutEditor.Play()` (`LayoutEditor.cs:913-927`) launches a brand-new ordinary session for the full pack and
     then calls `Stop()`. That clears the layout editor's state (`state = Off`, `draft = null`, the camera rig
     released). Nothing records that the run came from the layout editor.
   - The only return mechanism is `EditorReturn`. Its pending place was set when the layout editor was first opened
     from the level page (`LevelPage.EditLayout` → `EditorReturn.Remember()`), and it survives the Play transition
     because that transition targets the host (`EditorReturn.cs:297`).
   - So leaving the test run (Back to Map or the end screen) redirects to the title or map select and reopens the
     **pack editor's level page**. The map is unloaded, and the layout editor isn't restarted.
   - Fix: the Studio session above. Play and Edit are one session, and Back reloads the host in edit state.
2. **"Level music kept playing until I backed out to the main menu."**
   - `SceneTransitionManager` never touches music. `MusicManager.PlayMusic` is called only by a destination scene's
     `PlayMusicOnStart`, `CarouselManager` page music, and `AudioDayNightFader` at dusk and dawn (days have no music;
     dawn calls `PlayMusic(null)`).
   - A test run left during or right after a night therefore carries the night music into wherever it lands, until
     something there asks for another clip.
   - `EditorReturn` lands on the Builder's frames, either over the title (redirected from the map select,
     `EditorReturn.cs:302`) or over the map select's pause menu. That never reaches the title carousel page whose
     music would replace it, so it plays until the player backs out to the title page.
   - Inferred from the code, not reproduced.
   - Fix: `MusicManager.instance.PlayMusic(null, 0.5f)` at every Studio swap and Studio exit, and in
     `EditorReturn.OnTransition` whenever it redirects.
3. **Del deletes nothing and says "Nothing to undo today."** See "Input ownership".

## Library: levels and enemies (R2-C1)

The Builder is centred on two libraries, **My Levels** and **My Enemies**; packs exist only at export and import.

**Storage** (`Model/LibraryStore.cs`, pure C#, tested in `tests/PalaceMod.Tests/LibraryTests.cs`):

```text
BepInEx/PalaceBuilder/library/
  levels/<id>.json  levels/<id>.png      a level and the wave set(s) it owns; its saved preview
  enemies/<id>.json enemies/<id>.png     one unit (enemy or custom troop); its saved portrait
  assets.json  assets/                   the shared asset map and files
  trash/                                 deleted and replaced items
  .migrated                              written once the old packs were moved in (lists what moved)
```

- Item files are ordinary pack files (`palace-builder`, same version and migrations) holding one level or one unit,
  plus `"library": {kind, created, origin}`. The file name is the id; ids never change (Rename sets the name).
- **Wave sets belong to their level** (nights are per map: lines and gold are the host's). Their ids are unique across
  the library; SaveWorkspace renames a clash in the workspace too.
- **Enemies are shared**: any level spawns any library enemy by its plain unit id. Custom player troops live in the same
  library (shown with an "ally" badge).
- At runtime the library is one composed pack, id `library` (`LibraryStore.PackId`), registered in `PackRepository`
  with the library root as its folder, so sessions, the unit factory, assets and validation are unchanged. Playing a
  library level validates only that level, its nights and the units they use (`TestPacks.Play`).

**Editing = workspaces** (`Designs/DesignLibrary.cs`). `DesignLibrary.OpenLevel(id)` gives a `PackDraft` holding that
level, its nights and every enemy and asset; `OpenEnemies()` every enemy and no level. Both have pack id `library`
(`draft.IsLibrary`). `draft.Save` writes only items that differ from the workspace's baseline (an untouched stale enemy
never overwrites another workspace's save); enemies removed in the workspace go to trash. A dirty cached workspace is
rebased on reopen (`PackDraft.RebaseLibrary`: untouched enemies follow the library). Level and enemy edits made from
the Level Studio share its undo history and its save.

**Previews** are saved snapshots, never live renders: `PackDraft.Saved` → `DesignLibraryManager` pictures the open
Studio level from above (framing its items) into `levels/<id>.png` on every save; the Enemy Studio saves a portrait
with `DesignLibrary.SavePreview(DesignKind.Enemy, id, texture)`. `DesignLibrary.Preview(kind, id)` loads a sprite,
cached until the file changes.

**Home** (`Designs/LibraryHome.cs`; `BuilderEditor.Open` opens it, the old pack list is `BuilderEditor.OpenPackList` for
dev and installed packs). Two wide tabs, My Levels and My Enemies. A toolbar row (New, Import, Export, a search tile that
types in place, Sort: Recent / Name / Map or Base) over a grid of 268×214 tiles: the saved snapshot (fallback: the map's
thumbnail, or the base enemy's icon), the name, and one line of facts (map · nights; base · levels that use it).
Click opens; right-click gives Open, Name, Duplicate, Export… (levels) or Used in (enemies), Delete… (to trash, with
the levels that use an enemy named). Tiles of installed packs' levels and enemies follow under a header, dimmed; a
click copies one into the library (with the enemies it needs). The Export popup picks levels and extra enemies (the
ones the chosen levels use are listed as included) and saves a `.tfpack` (opens the exports folder) or copies a share
code. In the Level Studio, the Studio menu's Share/Pack editor rows become Export… for library levels, and each spawn
group in the nights page gets Edit enemy… / Customize enemy… / New enemy… (`Designs/EnemyEditing.cs`). Dev:
`library home|open|enemy|new-enemy|rename|duplicate|delete|list|preview|export|code|import`.

**Enemy Studio hook** (`StudioServices.Enemies`, `IStudioEnemyEditor.Open(draft, unitId | null, returnToLevelStudio,
onReturn, out error)`): R2-C2 implements it; the Level Studio's spawn-group picker ("Edit enemy…", "New enemy…") and
the home's enemy tiles call it. `DesignLibrary.NewEnemy(draft, baseRef, name)` adds a unit with a free id (one undo
step).

**Export / import.** `DesignLibrary.ExportFile/ShareCode(levels, enemies, name)` build a pack of the chosen levels, the
chosen enemies, every enemy they use (spawns, troops, behaviours, transitively) and every asset named, in a staging
folder, then write it with the existing `.tfpack`/share-code code. Every import (`IPackRepository.InstallNew`: share
codes, `.tfpack` files, the import folder) adds the pack's levels and enemies to the library: an identical item is
reused, an item from the same pack (`origin`) is replaced (old file to trash), any other clash gets a new id
(`goblin-2`) and every reference (spawns, troops, behaviour params, `asset:` keys) is rewritten. Wave sets no level uses
aren't imported (a warning says so).

**Migration.** On the first scan, every pack folder in `packs/` (the player's own, e.g. `my-designs`, and installed
demo packs) is imported with origin = its pack id and moved to `PalaceBuilder/migrated-packs/` (kept, never deleted).
Unreadable packs stay where they are. Later folders in `packs/`, plugin and built-in packs stay installed packs: the
home lists their levels read-only and copies one into the library to edit it. An old pack id still resolves (agent
bridge `FindPack`, `palace.py`) to the levels that came from it.

**Agent kit.** `palace.py` treats `library` as a pack (load composes the files, save writes changed items);
`new <level> --host` makes a library level, `list` shows the library, `export` writes a `.tfpack` through the game
(`agent library export ...`). `apply` checks only the levels its ops touched.

## Starting a level, and the empty map

**Start popup** (`Studio/Start/StartPopup.cs`, `StartPopup.Open(draft)`; Pack › Levels › "New level"):

```text
 New level
 [Nordfels] [Durststein] [Frostsee] [Uferwind] [Sturmklamm]      thumbnail tiles of the 9 main maps
 [Wildbach] [Moorweg]    [Freifort] [Totend]
 Nordfels                         13 nights · 5 spawn lines · 8◉
 Start from    The map as it is ▸ Cleared ▸ Flat field
 Name          Nordfels (cleared)
 Nights        The map's own ▸ (the pack's wave sets for that map)
 Create and open
```

- The map tiles are the main maps (catalog hosts without a variant suffix). "Start from" picks `base`: null, `cleared`
  or `flat`; Flat field shows only once the terrain layer sets `StartPopup.FlatFieldAvailable`. The map supplies the
  castle position, lighting, colour scheme and sky either way. Create adds the level (one undo step) and opens it in
  the Studio.
- Thumbnails (`Thumbnails.cs`): `LevelInfo` has no preview picture and `LevelCapture` can't render a map that isn't
  loaded, so a main map is pictured around its castle a few seconds after it's first seen as it is (a normal run or
  the Studio on a map as it is) and cached at `BepInEx/PalaceBuilder/cache/thumbs/<scene>.png`. Until then its tile
  shows only its name.

**What "empty" means technically.** Creating a scene from nothing stays out (`builder-design.md`: managers, camera,
day/night and lighting all live in the level scenes). An empty level is the host map loaded as usual, with these
changes applied by the overlay at load (after Awake, before Start):

1. **Both kinds:** every build slot except the castle is removed. They stay available as clone bases, because clones
   are made before removals, as today.
2. **Both kinds:** scenery is hidden.
   - The game's `ColorAndLightManager` names its ground, water and road materials (`groundMaterial`, `groundHigh`,
     `groundLow`, `earthMaterial`, `sandMaterial`, `groundPatches`, `rockMaterial`, water, ocean, `road`;
     decompiled `ColorAndLightManager.cs:66-99`).
   - Every renderer in the host scene is disabled **unless** it uses only those materials, or it belongs to a build
     slot, the spawner, a unit (`Hp`), the king, the camera, UI or the invisible walls (`EmptyBase`). MeshFusion
     combining runs at Start, after the overlay, so every `MeshFusionSource` of an empty base is turned off first
     (the scenery stays separate renderers).
   - Checked on the 9 main maps (`builder-req base cleared <map>`, screenshots): renderers hidden per map: Nordfels
     3067, Durststein 104, Frostsee 103, Uferwind 1288, Sturmklamm 3167, Wildbach 2850, Moorweg 3586, Freifort 2771,
     Totend 1046 (the desert and snow maps are mostly rock, which is ground). Every spawn line still reaches the castle
     on all 9 (Wildbach's "Upper Level Side" only once W4's `GroundGraphs.Scan` replaced the plain rescan). Rocks and cliffs stay: they use `rockMaterial`.
   - Scenery colliders under hidden renderers are disabled too. The host's `invisibleWalls` stay, so the map's
     boundary still holds, and so does its `flyerSurface` (inactive in play but with enabled renderers, which every
     rescan rasterizes): hiding it left Uferwind's fliers' graph 20 nodes instead of 816, and a Saltreach flier line
     over the sea 174 m off it; its furies were put at an infinite position and the night never ended.
3. **Cleared only:** the host ground stays, so cliffs, water and paths keep working with no new geometry. The
   host's spawn lines stay where they are.
4. **Flat field only:** the host's ground renderers and colliders are disabled too. W4's ground layer (see
   "Terrain") supplies a flat heightfield at the castle's height, its collider, boundary walls and the minimap
   rebuild. The host's spawn lines are moved to the field's edges.
5. `spawnLines.add` is implemented: a copy of a host `EnemySpawnLine` (one spawning the same kinds, if any) under the
   spawner, named the line's id, made a straight two-point line of `width` through `pos` turned by `rotY`, with its
   shared/separate attack-path lists cleared. Wave spawns may name it (the validator counts lines the pack's levels
   add for that wave set), and the start-of-level path check includes it.
6. One full rescan after Start, which happens today after any overlay (`BuilderLevelManager.ScanAfterLevelStart`).

Cost: Cleared is S-M and low risk, since it only removes things and keeps the host's ground, walls and graphs
semantics. Flat field is M on top of W4's ground layer.

### Building kinds the host lacks (`building:<kind>`)

An added slot used to be only a copy of a host slot, so a kind the host lacks couldn't be placed (no Mill on
Frostsee). Every player building kind is also a `Resources` prefab in `resources.assets` (the `ResourceManager`
container lists them: `mill variant`, `field variant`, `mine shaft`, `fishing harbour`, `heros quarter variant`,
`archer hut`, `swordsmen hut`, `temple`, ...), and that prefab is the **whole build slot**, not just a mesh: `BuildSlot`
with its upgrade tree, the blueprint and built parents, interactor, cost display, health bar, feedbacks, troops and the
upgrade-effect objects (e.g. the Mill's `Mill_ImprovedPlow`, `Mill_ImprovementExplosiveTrap`,
`Mill_Improvement_Scarecrows`). The maps' slots are instances of these prefabs. So no harvest and no reading of other
level files is needed: `Resources.Load` works in any scene.

- **Data:** `buildSlots.add[].base` takes `"building:<kind>"` besides `"scene:<host slot>"` (`Model/ContentRef.cs`,
  `BuildingRefs`: archery-range, barracks, barricades, blacksmith, bridge, defense-tower, field, gold-mine, harbour,
  heros-quarter, house, mill, royal-forge, shrine, summoning-circle, temple). No version bump; the validator refuses
  an unknown kind. Walls aren't offered: every map has them and a wall piece is part of its gate.
- **Play (`LevelOverlay`):** the prefab is instantiated exactly like a cloned host slot (inactive holder, deterministic
  save GUID, cost edits, requirements, then activation under the castle's parent). The prefab has no map wiring, so
  with no `requires` it needs castle level 1 (`LevelRequirements.Effective` and `ApplyRequirements` agree).
- **Studio:** `StudioScene.Enter` adds a palette base (`NativeSlot.Standard`, named by its base) for each kind the
  host has no copyable slot of, with its proxy look from the prefab; it is never an object on the map. Placing one
  writes `building:<kind>` and the castle level 1 default (then auto-link). Kinds the host has still copy the nearest
  host slot, so the map's own tuning wins where it exists.
- **Agents:** `agent host` adds `buildings` (each standard building in the slots' shape, named `building:<kind>`);
  `palace.py add_slot {"kind": "Mill"}` falls back to it when the host has none.
- Dev: `builder-level buildings` prints each standard building's tree and how the loaded map's own slots of that kind
  differ from it.

## Terrain

### How the map's ground works (from the code)

- **Ground is static meshes, not Unity Terrain.** Nothing in the game references `Terrain`. Meshes are combined at
  runtime by MeshFusionPro, whose combined meshes are new, readable `Mesh`es (decompiled
  `NGS.MeshFusionPro/CombinedMesh.cs:31`).
- **Pathfinding is A\* Pathfinding Project Pro 5.4.4.** It has four graphs: `Enemy Units S`, `Player Units S`,
  `Enemy Units L` and `Enemy Units Air`. The recast graphs rasterize *renderer meshes* (not colliders) on the Ground
  and Obstacle (7) layers, skipping unreadable meshes and disabled renderers.
  - Graphs ship pre-baked. The game never rescans, apart from `NavmeshCut`s on slots and units.
  - The mod already rescans at runtime: `PathCheck.Rescan`, 120-220 ms warm on Nordfels, 0.4-1 s the first time.
  - Local updates (`GraphUpdateObject` with `updatePhysics`) rebuild only the touched tiles of a tiled recast graph.
    The shipped graphs are one tile each, so a local update costs a full scan (see "T0 results").
- **Units and enemies take their height from the navmesh**, snapping to it every frame
  (`PathfindMovementEnemy.cs:321-343`, `PathfindMovementPlayerunit.cs:399-401`). Raised ground carries them as soon as
  the graphs are rescanned.
  - **The king** is a `CharacterController` with gravity, so he needs a real collider. Below y = -10 he's teleported
    back to the start (`PlayerMovement.cs:32, 261-266`).
  - Coins and lobbed projectiles raycast a `groundLayer`.
- **Fliers** use `Enemy Units Air`, spawning 5 m up (`Spawn.cs:276`). It is a recast graph over the "Flyer Surface"
  layer (9): the `flyerSurface` meshes, which are inactive in play. Falkenpass generated an 8 m grid surface 5 m above
  its terrain.
- **Water** has no gameplay script. It is a scrolling material, and is blocked by having no walkable ground plus
  invisible walls.
- **Map bounds** are `PathMesher` meshes and colliders under `NavmeshBakeHelper.invisibleWalls`.
- **The minimap** builds its background once from graph 0, one frame after Start (`MinimapRenderer.cs:89, 119-141`),
  so it must be rebuilt after a terrain rescan.
- **Ground look:** `ColorAndLightManager` shares FlatKit materials (`groundMaterial`, `groundHigh`, `groundLow`,
  `earthMaterial`, `sandMaterial`, `rockMaterial`, water, ocean, road) and recolours them from the level's scheme
  and the daylight. Generated ground that reuses them looks native and follows the day cycle for free.
- Falkenpass did all of this for one hand-coded map: a floor collider, cliffs, its own Air surface and the four
  native graph names in order. It proves the approach works, not the cost of an editor.

### T0 results (measured in a pool instance)

| | Nordfels | Uferwind |
|---|---|---|
| Graphs | 4 × `RecastGraph`, one tile each (`useTiles` off), cell 0.2 m | same |
| Ground graphs | layers Ground (6) + Obstacle (7), meshes only, maxSlope 30°, climb 0.5 m, radius 0.5 (L: 4) | maxSlope 40°, radius 0.5 (L: 2) |
| Air graph | layer Flyer Surface (9), radius 0.5 | layers Obstacle + Flyer Surface, maxSlope 90° |
| Bounds | centre (0, 0, -31.3), size 206 × 15 × 134 | centre (-15.5, 27.9, -78.2), size 236 × 39 × 320 |
| Full scan (warm) | 100-140 ms (400-800 ms with the ground layer and pruning) | 0.7-1.4 s |
| Local 20 m `GraphUpdateObject` | 106-127 ms: the same as a full scan (one tile) | 1 s |
| King | `CharacterController` on layer Player (11), slope 45°, step 0.3 m; collides with Ground, Obstacle, Flyer Surface (9) and Default | same |

**Two bugs in every runtime rescan (plain `AstarPath.Scan()`, which `PathCheck.Rescan` was), fixed by
`Level/GroundGraphs.Scan`:**

1. **The Air graph comes back empty.** `NavmeshBakeHelper.flyerSurface` is inactive in play, so the scanner finds no
   flier surface and fliers have no graph. `GroundGraphs.Scan` activates it (invisibly) for the duration of the scan.
   It can't stay active: its box colliders are on a layer the king collides with.
2. **The walkable area grows past the map.** The bake kept only the areas the map uses; a rescan also turns the tops
   of cliffs, rocks and walls into walkable islands (Nordfels: 541 nodes instead of 132; enemies snap onto them and
   the minimap draws them). The invisible walls hide most of their renderers on Start (`PathMesher._hideOnStart`; no
   map uses `HideMeshRendOnPlay`). `GroundGraphs.Scan` shows the walls' renderers to the scanner, then **prunes
   islands**: on each ground graph, it makes unwalkable every area that holds none of the baked graph's nodes (taken
   before the first rescan in the scene), the castle, or (on a flat field) a ground spawn line. Nordfels after a
   rescan: 133 walkable nodes in one area, as baked.

Known gap: on Uferwind the bake has two small southern areas (89 of 444 nodes) that no runtime rescan reproduces,
with or without the fixes; whatever geometry the bake saw there isn't in the scene at runtime. Custom Uferwind levels
with edits have always lost them. Whether any of its spawn lines lead there is unverified (they're inactive until a
night starts).

### Feasibility

| Feature | Feasible | Cost | Main risk |
|---|---|---|---|
| Walkable/blocked paint (ground units) | built | S-M | Fliers ignore it (by design). Readable obstacle columns, as the solid-prop blockers. |
| Cliff, rock and wall pieces as solid props | yes, now (blocking) | S | Host scenery meshes may be unreadable. Blocking already works through blocker boxes. |
| Walkable ramps and plateaus as pieces | yes | M | Needs a generated readable mesh plus a `MeshCollider` (the king) on the Ground layer. |
| Water planes and shorelines | yes | M | Visual: the ocean or water material. Blocking: a footprint cut from the heightfield, or a layer-7 blocker. |
| Sculpting on a **Flat field** (heightfield replaces the ground) | built | L | Air surface (recast on layer 9: generated), minimap rebuild, rescan time, boundary walls, y ≥ -8. |
| Sculpting **on a vanilla map's ground** | built (R2) | L+ | Sculpted cells replace the host's ground: `Level/HostCut.cs` cuts them out of the host's Ground-layer meshes and colliders (exact clipping), so raise, lower, ramps and water all work. Fliers keep the host's flier surface. |
| Removing parts of a vanilla map piecemeal | partly | M-L | MeshFusion can cut sources from combined meshes (`CombinedObject.cs:109-112`), but whether the scenes keep their sources is unverified. v1 only does all-or-nothing (Cleared). |

### Design (W4, as built)

**Stages:**

| Stage | State | Delivers |
|---|---|---|
| T0 spike | done | "T0 results" above (`builder-terrain spike graphs\|helper\|king\|ground\|update\|scan\|region`). |
| T1 ground layer + Flat field | done | `Level/GroundLayer.cs`: chunked readable meshes on the Ground layer with `MeshCollider`s, the level's own ground materials, walls round the field, a flier surface, the graphs' height range widened, the host's ground hidden; `Level/MinimapRefresh.cs`; `Level/GroundGraphs.cs` (the rescan). |
| T2 brushes | done | Raise/lower, flatten, set height, smooth, material paint; live re-mesh while dragging. |
| T3 walkable paint + pieces | paint done; pieces not built | Block/unblock on any base. Cliffs, plateaus and ramps are brush work on a flat field (a hard-edged Set height makes a plateau with cliff sides; the Ramp brush cuts a walkable slope). Blocking rock and cliff *pieces* on a map's own ground are the existing solid props (`LayoutScene.SetSolid`); walkable ramp pieces on a map's own ground are deferred (see "Not built"). |
| T4 water | done (flat field) | Water brush and water level; the bed is dug 1 m below the surface, banks turn to sand, water cells are blocked for ground units and the king. |
| R2 | done | Sculpting on a map's own ground (HostCut), smooth paint (format 2), cliff walls for the king, live ramp preview and an exact cursor on cliffs, the brush strip as icons, the height ceiling relative to the castle. |
| Later | | Piecemeal scenery removal on vanilla maps; fliers over raised host ground. |

**Data** (`LevelDef.terrain`, `Model/TerrainDef.cs`; encoded by `Level/GroundCodec.cs`, unit-tested):

```jsonc
"terrain": {
  "origin": [x, z],           // world corner of cell (0, 0) (min x, min z)
  "cell": 2.0,                // cell edge in m (0.5-16)
  "size": [w, h],             // cells along +x and +z (1-400 each)
  "y": 0.0,                   // world height of 0 (the castle's height for a new field)
  "heights": "<b64>",         // (w+1)*(h+1) corners, row-major (z*(w+1)+x): int16 cm relative to y, delta-coded along
                              // that order, little-endian, raw deflate
  "format": 2,                // 1 (absent): materials per cell; 2: per paint sample
  "materials": "<b64>",       // byte per paint sample ((w*paintRes) x (h*paintRes), row-major), raw deflate: 0 grass,
                              // 1 high grass, 2 low grass, 3 earth, 4 sand, 5 road, 6 rock
  "paintRes": 2,              // paint samples per cell edge (format 2)
  "blocked": "<b64>",         // bit per cell (LSB first), raw deflate: ground units and the king can't enter; fliers can
  "water": { "level": -1.0, "cells": "<b64 bitmask>" }   // level relative to y; water cells are unwalkable
}
```

- Every array is optional and left out when all zero. A flat level with no `terrain` gets a field over the host's
  graph bounds at the castle's height (Nordfels: origin [-104, -100], 104 × 68 cells).
- Measured sizes: a plain field ~100 characters; the acceptance field (plateau, ramp, hill, pond, road patch, blocked
  strip) ~800. Share codes stay under the 2,000-character chat size for typical sculpting; heavy sculpting goes
  through `.tfpack` as `SharingUI` already suggests.
- Heights reach from world -8 m (below -10 the game respawns the king) to 30 m above `y` (at least world 30 m:
  `TerrainGrid.MaxWorld`), so a high host (Sturmklamm, castle y 31) can still build up.
- On `"base": "flat"` heights are relative to `y`. On any other base they are **offsets from the host's ground**
  (sampled at each corner by raycasting its Ground-layer colliders when the terrain is shown; 0 = untouched), and a
  cell any of whose corners moved is *sculpted*: the terrain draws it and the host's ground is cut away under it.
  Every field works on every base.
- Format 2 (R2): materials per paint sample. Format-1 packs (a material per cell) decode with each cell spread over its
  samples and save as format 2; a format newer than the mod's is refused with "update PalaceMod".

**Runtime (play and the Studio's edit load):** `LevelOverlay` calls `GroundLayer.Apply(scene, pack, level, issues)`
after the empty base's scenery hide, before Start.

- Flat base: the host's renderers drawn only with ground, rock, river, waterfall, road and shadow-shape materials are
  hidden (and MeshFusion is kept off them), with the Ground-layer colliders, the invisible walls and the flier surface.
  Ocean-material planes go too unless they reach past both opposite edges of the field (a sea, e.g. Frostsee's,
  stays so the field reads as an island): Moorweg draws its ponds with the ocean material. The generated ground
  replaces them. Triangles steeper than the ground graphs' max slope (the strictest ground graph's, per host: 20° on
  Frostsee, 30° on Nordfels, 40-50° elsewhere) are drawn with the rock material, so cliffs read as cliffs, and each
  gets an invisible wall (a prism on the Obstacle layer, from below the ground to above the graphs) the king can't
  pass: what the king can walk down, he can walk back up, exactly where enemies can. The prisms also have an
  invisible renderer, so the ground graphs rasterize them as walls: recast's erosion (agent radius) only stops at a
  span with a missing neighbour, and a steep face's voxels are neighbours wherever it drops less than the climb
  (0.5 m) per 0.2 m voxel, so without them a 6 m raised ramp carried the large graph to its very edges. The fliers'
  graph is scanned without them (`LayoutScene.GroundOnlyWall`). (The Far Bank bug: the river bank was a 60° face of one 2 m cell; the king,
  slope limit 45°, slid down it, landed against the water's blocker columns and couldn't climb back; the "invisible
  warrior" to the left was the ford's own bank, another face he couldn't climb.) Slots and the king are put on the
  new ground.
- Paint is drawn as smooth outlines over the ground (`Level/PaintOutline.cs`): per material, the paint samples' "is
  this material" field is blurred (3x3 binomial), sampled bilinearly on a 0.5 m lattice and filled by marching squares
  at 0.42 (just under half, so neighbouring paints overlap instead of leaving a seam); each material is a submesh a
  few cm over the ground (later materials higher), on layer Default (not rasterized, no collider); whole cells use the
  ground's own two triangles. Cliff cells keep their rock. Grass by water and under it is drawn as sand, so the shore
  follows the bank; the water plane also covers the bank cells that cross its level, so the shoreline is where the
  slope meets the water.
- A map's own ground (`Level/HostCut.cs`): when the terrain is shown, the host's Ground-layer renderers (the
  MeshFusion-combined ground the graphs rasterize) and MeshColliders are collected; whenever the sculpted set changes,
  each is swapped for a copy with the sculpted cells' rectangles subtracted from every triangle (convex pieces,
  attributes interpolated). Sculpted cells get a 1 m skirt along edges that meet the host's ground, hiding the few-cm
  seam between the host's surface and the grid's 2 m sampling of it. Paint outside sculpted cells lies on the host's
  ground (raycast). The graphs' height range is widened to the sculpted heights.
- The field draws with copies of the host's seven ground materials (`GroundLayer.Recolour`, re-run on
  `ColorAndLightManager.OnColorChange`): the scheme's colours are copied over, then `GroundPalette.Resolve` moves
  paints apart where the scheme gives several one colour (every scheme has ground = high = low; Moorweg, Nordfels and
  Frostsee have road = earth). Ground keeps the host's grass colour; the others step lighter or darker in the same
  hue (earth may lean towards the sand) until they are a set distance from each other, the road further from the
  grass. The field's base colour is the host's real ground colour: Moorweg's is dark blue, Wildbach's pink, Totend's
  near-black.
- Every base: blocked and water cells become invisible readable columns on the Obstacle layer that reach above the
  graphs' bounds (no walkable top) with a `MeshCollider` (the king). They're named like the props' blockers, so the
  path check sees a spawn walled in by them.
- The flier surface is a mesh 5 m above the highest ground around each corner, on layer Flyer Surface, inactive except
  while `GroundGraphs.Scan` runs (as the game's); the Air graph's max slope and climb are raised so fliers cross cliffs.
- The level-start rescan (`GroundGraphs.Scan`) then builds the graphs, prunes islands and redraws the minimap.

**Brushes** (Terrain tool, T, `Studio/Terrain/TerrainTool.cs`; its strip in the palette's place while active):

| Brush | Drag | Modifiers | Strip |
|---|---|---|---|
| Raise | Adds height (m/s at the centre, falloff) | Shift lowers | Size, Strength, Softness |
| Flatten | Levels to the height under the press | | Size, Softness |
| Set height | Levels to the strip's height (Softness 0: a plateau with cliff sides) | Alt-click samples | Size, Height, Softness |
| Smooth | Averages with neighbours | | Size, Strength |
| Ramp | From the press (top) to the release (bottom): a straight incline between the ground's heights there; the ground previews while dragging, the status bar shows the incline and the guide lines turn red past 30° | | Width |
| Material | Paints the chosen material (smooth edges) | | Size, swatches |
| Block | The walkable mask | Shift unblocks | Size |
| Water | Water cells (the bed is dug to 1 m below the level) | Shift dries | Size, Water level |

- The strip: the eight brushes as icon tiles (1-8 pick them while the tool is active: `StudioTool.Digit`; the lit
  tile is the brush, its number in the corner, a tooltip says what it does), one line with the brush's modifier
  (Shift lowers / unblocks / dries, Alt-click picks a height), its numbers (Size or Width, Strength, Softness, Height,
  Water level), and for Material the seven paints as swatches in the colours the field draws them (`PaintColour`).
- Every brush works on every base (a map's own ground included, see HostCut).
- `[` and `]` or Alt+wheel size the brush. A ring on the ground (an `IStudioOverlay`) shows it, in the brush's colour
  (Shift's inverse colour while Shift is held), with an inner ring where a soft brush's full strength ends; the ramp
  shows its centre line and both sides while dragging. Blocked cells are drawn as a red wash in edit mode only.
- The cursor meets sculpted ground exactly (`TerrainGrid.Raycast` marches the heightfield; on a map's own ground a
  Ground-layer raycast). It used to intersect a horizontal plane at the camera pivot's height and then sample the
  ground there, which put a press near a plateau's far edge on the low ground behind it: the ramp's top came out at
  the bottom's height, so the "ramp" cut a level trench (the R1 ramp bug).
- Each stroke is one undo step (`Studio.Edit` writing `LevelDef.terrain`). Undo, redo and other edits rebuild the
  shown ground from the level when it differs (`Studio.ItemsChanged`, compared by `GroundLayer.KeyOf`).
- Esc during a stroke drops it.

**Editing performance:** a stroke re-meshes only the touched 32 × 32-cell chunks (and their colliders) every frame,
with no graph work; `LayoutScene.Ground` and `IStudioTerrain.Height` read the terrain's own height, so markers follow
at once. On release the graphs are **stale** (`IStudioTerrain.PathsStale`; the strip's "Paths" row says "Out of date"
and updates them on click). A local `GraphUpdateObject` costs a full scan (one-tile graphs), so `UpdatePaths` is a
full `GroundGraphs.Scan`: 400-800 ms on a Nordfels field. Play reloads the map, so F5 needs no rescan of its own.

**Dev:** `builder-terrain flat [cell] | host [cell] | brush <name> [size] | stroke [shift] <x> <z> ... |
raise/flatten/smooth/ramp/paint/block/water ... | waterlevel | marks | king <x> <z> | walk <dx> <dz> <s> |
probe <x> <z> [y] [r] | snap | scan | grid | encode | save <pack> | clear`. `host` puts a sculptable grid on the map's
own ground; `walk` moves the king through his CharacterController (what he can climb); `probe` lists the colliders he
meets around a point. `save` writes the shown terrain into a one-level pack; `builder-req play <pack>` plays it.

**Not built (and why):**

- **Fliers over raised host ground:** on a map's own ground the fliers keep the host's flier surface (a fixed mesh
  5 m over the original ground), so they pass through hills raised more than ~5 m. Replacing it would mean building a
  flier surface over the whole host, which the flat field does.
- **Offline checks of sculpted host ground:** `palace.py check`/`render` model a flat field's heights but not
  sculpting on a map's own ground; `paths` in game is the truth there.

**Acceptance (T1 + T2), verified in a pool instance on Nordfels** (a flat field with a 4 m plateau round the castle,
a ramp, a hill, a pond and a blocked strip, saved as a pack and played):

- The king walks up the ramp onto the plateau (y 0 → 4) and is stopped by the cliff.
- All three ground lines reach the castle; with a 6 m ramp the large-enemy graph (radius 4) is correctly cut off.
- At night a ground enemy was seen on the ramp at (0, 3.3, -23), between the ramp's 0 m foot and 4 m top.
- The minimap shows the new shape. Level-start rescan 470-640 ms.
- In the Studio: strokes through the pointer (set height, ramp, water, raise), the strip, the ring and the red wash
  on blocked cells; undo and redo rebuild the ground; F5 to play and back keeps it; the Studio's path check updates
  stale paths first (423 ms).

## Asset browser slot

A separate effort is designing a cross-map asset library. The Studio leaves it one slot:

- The palette's **Library** tab hosts an `IAssetBrowser` (new, `Features/Builder/Studio/IAssetBrowser.cs`, owned by
  W1):

  ```csharp
  public interface IAssetBrowser
  {
      string Title { get; }                                        // tab label
      IReadOnlyList<AssetBrowserCategory> Categories { get; }      // left column chips
      IReadOnlyList<AssetBrowserItem> Query(string category, string text);   // tiles; may grow while loading
      event Action Changed;                                        // re-query
      // Ghost for the Place tool; null while loading (the tile shows a spinner badge)
      GameObject MakeGhost(AssetBrowserItem item, Transform parent);
      // What a placed item is saved as: a PropDef base string (e.g. "mesh:<name>", "lib:<id>", "asset:<key>")
      string BaseRef(AssetBrowserItem item);
  }
  public sealed class AssetBrowserItem { public string Id, Name, Category; public Sprite Icon; public string Badge; public bool Ready; }
  ```

- The Studio's built-in implementation, `HostSceneryBrowser`, wraps today's `LayoutScene.Props` (this map's scenery)
  and the pack's `asset:` meshes through `BuilderServices.Assets.GetMesh`/`GetState`. The library effort registers
  its own with `StudioServices.AssetBrowser = …`. Placed items go through `LevelDef.props` with the returned `base`
  string. The library effort extends `LevelOverlay`'s prop resolution for its new base kinds; the Studio stays
  agnostic.

### The cross-map scenery library (W5, `Features/Builder/Library/`)

Every scenery mesh of the ten main maps (45 today: trees, rocks, walls, towers, graveyard pieces) can be
placed on any map.

- **No game assets are shipped or copied.** The repo holds only a metadata catalog,
  `src/PalaceMod/Assets/scenery.json`: for each mesh, its name, home map, the maps that use it (with counts), the
  asset file and object id it lives in, its kind, material names, the maps' typical yaw-free rotation and scale, the
  vertex count and the bounds. `python3 scripts/dev.py scenery-catalog` regenerates it from the installed game with
  UnityPy (`scripts/scenery_catalog.py`; the same filter as `LayoutScene.Props`, plus a hand-kept exclusion list and
  kind table). Rerun it after a game update.
- **Runtime:** on first use, `SceneryLibrary` reads the meshes from the player's own install (`AssetFileReader`: a
  minimal reader for serialized format 22 and the Unity 2022.3 Mesh layout; about 400 KB, 85-225 ms once per game
  session), copies the vertex and index buffers into new readable Meshes and keeps them. Every read is checked: the
  file header's Unity version must be the catalog's, the object must be a Mesh with the catalog's name and vertex
  count, and blend shapes, compressed or streamed meshes are refused. An entry that fails is hidden from the browser
  and logged once (Warn); a level that uses it gets the usual "no scenery" warning, and the rest still loads.
- **Colours:** a library mesh is drawn with the loaded level's material of the same name (the FlatKit materials
  `ColorAndLightManager` recolours per level: Trees, Mountain, Buildings, Enemy, Ground Patches), so it takes the host
  map's colours by default. **Original colours** (`Library/MapColors.cs`): the catalog also records where each main
  map's `Colorscheme` asset is (`colorschemes`: all ten live in `sharedassets1.assets`, found through each level's
  `ColorAndLightManager.colorscheme` reference by the generator). `AssetFileReader.ReadColorscheme` reads one from
  the install (fixed layout: the MonoBehaviour header, then the colour fields in declaration order with
  `dayLightColor` skipped as readonly and the particles prefab reference in between; checked by name and exact
  size) into a new `Colorscheme`, cached for the session. A prop's `colors: "<Map>"` swaps each material
  `ColorAndLightManager` recolours (found by reference on its fields) for a clone coloured from that scheme the way
  `ApplyColorScheme` does it; the clones' outlines follow the host's sunlight through dusk and dawn
  (`OnColorChange`). Inspector: "Colours" on any scenery selection toggles between "Level's" and the map it comes
  from (the library entry's "@Map" or home map; the host for its own scenery). The level's `colors` (Level tab,
  "Colours") gives the whole level another map's scheme through the game's own `EasySwitchEntireColorSchemeTo` (set
  before `ColorAndLightManager.Start` in play), keeping the host's weather particles.
- **Refs:** placed library items are ordinary `props` entries with `base: "mesh:<name>"`, optionally
  `"mesh:<name>@<Map>"` should two maps ever ship a mesh by the same name (the catalog generator refuses that case
  today). `SceneryLibrary.Resolve(scene, base)` looks in the host's own scenery first (so existing levels look the
  same; also where a host-only mesh not in the library is found), then the library. `LevelOverlay` (play) and the
  Studio (`StudioServices.PropResolver`) both use it. `.tfpack` and share codes are unchanged: they carry the string.
- **Solid props block with their real shape.** Library meshes are readable, so `LayoutScene.SetSolid` gives them a
  blocker child that uses the mesh itself (and a `MeshCollider`) instead of the bounding box; host meshes, which
  aren't readable, keep the box. A curved ruin wall blocks only along the wall.
- **Library tab** (`LibraryBrowser`, registered as `StudioServices.AssetBrowser` in `SceneryLibraryManager.Awake`,
  wrapping a `HostSceneryBrowser`). The palette's chips are narrow (three to a row), so each is one short word: the
  palette's own "All" (every map: the host browser's items, then every library mesh), "Here" (the host browser's items
  plus the library meshes this map uses, which the live scene mostly no longer shows as separate objects because the
  game merges scenery into combined meshes at load), then one per kind: Trees, Rocks, Walls, Towers, Graves, Other.
  Library tiles are ordered by home map, then kind; the badge is the home map's first three letters, and the tooltip
  lists the kind and the maps that use it with counts. The text filter matches name, kind and map names ("Moorweg"
  lists Moorweg's). A mesh the host also has is listed once. New props start at most about 6 m across, like host
  scenery.
- **Tile pictures** (`LibraryThumbs`): rendered on demand by an offscreen camera (from above on the level sun's lit
  side, transparent background, the level's own materials), four per frame, cached as PNGs in
  `BepInEx/PalaceBuilder/cache/thumbs/<map>/<mesh>.png` (per host map, since colours differ) and in memory. The
  browser raises `Changed` once the queue drains.
- Dev: `dev.py cmd "builder-library status | query [here|kind:<Kind>|-] [text] | place <mesh> [x z] | gallery
  [spacing] | walkable <x> <z> | clear | thumb <mesh> | thumbs [reset]"`. Unit tests (`SceneryLibraryTests`) cover
  the reader on a synthetic file, ref parsing, the catalog, and, where the game is installed, read every catalog mesh.

## Enemy Studio

The custom-enemy editor, rebuilt the way the Level Studio was: an in-world tool around the live model instead of a
settings-shaped page. Code: `Features/Builder/Studio/Enemies/`. An enemy is still a `UnitDef` in a `PackDraft`
(R2-C1's library composes them into the `library` pack), so the runtime, validation and wave sets don't change.

### Decisions

1. **In place, on a loaded map.** The Enemy Studio runs on whatever map the Studio has loaded. Opened from the Level
   Studio it takes over the same edit scene with no reload: the Level Studio's dock, labels and tools are suspended
   (`StudioShell.Suspended`), its view is captured (`StudioViewState`), and Back restores it exactly (camera,
   selection, tool, palette tabs, undo history: the draft is the Level Studio's own `Studio.Draft`, so enemy edits are
   undo steps of the same history and the level sees them at once). Opened on its own (the library's Enemies tiles),
   it first opens a throwaway **arena**: a Studio edit session on Nordfels with the "cleared" base (an in-memory pack
   that is never saved); Back leaves the Studio.
2. **The model is live, in the world.** A stage at a clear ground point (the Level Studio's camera pivot, or the arena's
   castle square): a turntable disc with the enemy standing on it at its real size, lit by the level, seen by the
   Studio's orbit camera (the game camera, turned to perspective while the Enemy Studio is up). Every edit rebuilds the
   model from the same template the game spawns (`IUnitFactory.Resolve`), so what the stage shows is what a wave
   spawns, scripts stripped.
3. **Any asset as the body** (`visuals.mesh`), each with offset, rotation, scale and a material choice:
   - `asset:<key>`: an imported OBJ/glTF of the pack (as before);
   - `mesh:<name>[@Map]`: any scenery mesh of the cross-map library or the map (a tree, a rock, a ruin wall, a tower);
   - `vanilla:<unit id>`: another unit's model (a knight's look on an enemy, a boss's body on a small one).
   Meshes without a rig get a procedural walk (`motion`).
4. **Test in the arena, not in a run.** Fight spawns N copies against defenders on the stage, right there in the edit
   scene. The game's attackers only act at night (`AutoAttack.Update`, `AutoAttackTower.Update` return in the day), so
   while a fight runs those two treat the arena's own units as if it were night; nothing else changes and nothing
   outside the arena wakes up. Numbers (DPS, hits and time to kill against the map's towers) are computed from the
   same damage formula the game uses (`DamageModifyer.CalculateDamageOnTarget`), live and without a fight.
5. **Save snapshots a portrait** for the library tile (`DesignLibrary.SavePreview(DesignKind.Enemy, id, tex)`), taken
   by the stage camera on a transparent background.

### Screen

```text
┌─────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ [← Level]  Goblin Brute · library ●unsaved  [↶][↷]   ⟳ Turn   ⚠ 1             [Save]   [⚔ Fight (F5)]   │ top bar
├──────────────────┬──────────────────────────────────────────────────────────┬────────────────────────────┤
│ Enemies Base Look│                                                          │ Stats Attack Tags Look Abil│
│ ┌──┐┌──┐┌──┐┌──┐ │                                                          │ Name     Goblin Brute      │
│ │  ││  ││  ││+ │ │                 the stage (3D, live)                    │ Base     Brute ▸           │
│ └──┘└──┘└──┘└──┘ │            turntable · model · defenders                │ Hit points  800  (base 600)│
│ Look: Units ·    │                                                          │ Move speed  4.5            │
│ Scenery · Import │                                                          │ … (scrolls)                │
├──────────────────┴──────────────────────────────────────────────────────────┴────────────────────────────┤
│ Arena  [5]× ☐elite  vs [Knights 3][Archers 3][none]  [Fight] [Reset]  │ 800 HP · 24 dmg/1.2 s · Tower 1: 9 s│ readout
└─────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Top bar:** Back (to the level when opened from one, else Leave), name, pack, save dot, undo/redo, Turn (the
  turntable spins; off holds the angle), the Problems badge (this enemy's issues; click: the field), Save, Fight.
- **Palette (left, 300 px), tabs:**
  - **Enemies:** the draft's custom enemies as tiles (portrait if saved, else the base unit's icon). Click switches the
    one being edited (undo stays); "+" makes a new one from the selected base (`DesignLibrary.NewEnemy`, or a plain
    `pack.Units` add before the library lands); right-click: Duplicate, Delete.
  - **Base:** every vanilla enemy as icon tiles grouped Ground / Large / Flying; click sets `base`. Hover: its stats.
  - **Look:** the model source, chips Units / Scenery / Imported plus a filter. Scenery tiles use the library's thumbnails;
    Units the unit icons; Imported the pack's mesh assets. Click sets `visuals.mesh`; the first tile "Base model"
    clears it.
- **Inspector (right, 460 px), tabs.** Stats and Abilities are the pack editor's rows (`UnitPage`); Attack, Tags and
  Look's colours are the Studio's own (`EnemyPanels.cs`), made for the narrow panel: short labels, and values that
  wrap beside or under their label instead of running over it.
  - **Stats:** name, id, description, hit points, move speed, attack cooldown, range, keep distance, chase time.
  - **Attack:** the weapon (its picker groups weapons into ranged, which fire a homing projectile, and melee),
    damage ×, projectile speed (ranged only), range, cooldown and keep distance (with a warning when keep distance is
    beyond the range), then Damage and Splash: one row per modifier, "vs <targets>" and its amount; apply opens a
    popup with the modifier's targets, bonus, multiplier and Remove. Without an own list the weapon's rows show dimmed
    with "Edit damage" to copy them.
  - **Tags:** the tags it spawns with as chips (base tags plain, added ones lit, removed base tags struck through and
    dimmed; click removes or restores; hover: the tag's description from `Model/Descriptions.cs`). Below, an Add
    filter and suggestion chips grouped by meaning (Defence, Fighting, Kind, Rarely useful); tags the game sets itself
    (AUTO_*, groups, curses) and player-side ones aren't offered. Contradicting pairs (armoured and vulnerable vs
    ranged, melee and ranged fighter, both sides) are flagged under the chips and on the suggestion's hover. Movement
    tags (Flying, Large) come from the base.
  - **Look:** Colours (below), size, the model source with Fit (auto / scale), offset X/Y/Z (-50 to 50 m in 0.05 m
    steps, typed or stepped), turn X/Y/Z, materials (own / enemy), motion (auto / bob / none).
  - **Abilities:** behaviours (spawns on death). The behaviour engine is being designed separately; the game's own
    components it can build on are catalogued in `docs/enemy-behaviour-research.md`.
- **Tooltips** never cover the stage: the inspector's and readout's show over the palette, the palette's and arena's
  over the inspector (`ModTooltip.Zone`).
- **Readout (bottom right):** effective HP at the arena's night difficulty, damage per hit and DPS against a building,
  a knight and the king, and time to kill (and hits) by each tower kind on the map at each level. During a fight:
  elapsed time, enemies alive, defenders standing, damage dealt each way, and the result.

### Colours

- **Why a tint barely showed:** the units' FlatKit toon material draws the lit side in `_BaseColor` and the shaded side
  in `_ColorDim`, and the level's colour scheme sets both (`ColorAndLightManager.ApplyColorScheme`: enemy light is a
  pale pink, enemy mid a deep red). A tint multiplied only `_BaseColor`, so the shaded half, often most of a small unit
  under the top-down camera, stayed the scheme's red; and elites lost it to the shared elite material.
- `visuals.colors` (`{"body": "#3a6ee8", "<material>": "#rrggbb"}`) replaces a part's colour: `_BaseColor` becomes it and
  `_ColorDim` the shade the scheme would pair with it (`ColorMath.ShadeFor`: the source pair's brightness ratio,
  saturation change and hue turn, scaled by how colourful the new colour is, so a grey stays grey). Keys, most
  specific first: a piece of the model by its renderer's name ("Bow", "Helmet"; vanilla enemies draw every piece with
  the one `Enemy` material, so pieces are how an archer gets a gold bow), a material by name, then "body" (every body
  material: `Enemy`, `Player*`, `Ally*`). The Look tab lists Body, other materials, then each named piece. The
  clones keep their source's name; `BuilderEliteLook` swaps an elite's shared elite material for the recoloured clone's
  elite twin (same colours, yellow outline) on Start and fixes the hit flash's remembered material. Clones are
  re-painted after every scheme change and daylight outline update (`UnitFactory.ResyncTints`).
- `visuals.tint` stays as the subtle option: it multiplies the body's lit colour and half-way its shade.
- The Look tab lists one row per part (the body first) with its swatch; apply opens `ModColorPicker` (Core/UI): a
  saturation/brightness square and hue bar (click or drag), was/now swatches, a hex field, the level's palette (the
  colour scheme's enemy, ally, king, building, ground, tree, rock... colours) and the map's own colours (its materials'
  base colours, deduplicated, sorted by hue). Edits are live (one undo step per part while dragging); "Original colour"
  clears it. The picker is generic: `ModColorPicker.Open(title, current, changed, near, allowNone, extra)`.
- Dev: `builder-enemy color <part> <#hex|->`, `builder-enemy parts` (the model's renderers, materials and their
  lit/shade colours), `builder-enemy picker`.

### Stage and camera

- Orbit: left-drag turns (yaw and pitch, 3-75°), wheel zooms (1.5-60 m), right/middle-drag pans the focus, F frames
  the model, Home resets.
- As built, the game's own camera is used: the `CameraRig` is paused and the rig itself is moved so its main camera sits
  on the orbit, and every camera of the rig switches from orthographic to a 30° perspective (stacked cameras stay
  aligned, post-processing and outlines unchanged). Back restores the projection, the rig's pose and the rig; the Level
  Studio's markers, labels and overlays are hidden meanwhile (`StudioShell.HideForSuspend`).
- The model is an instance of the template with every `MonoBehaviour` removed except the visual ones (`Animator`
  stays): the base's idle animation plays; a `BuilderVisualMotion` component (also on spawned enemies) adds the
  procedural walk to a body without a rig.
- **Portrait:** an offscreen camera renders the model alone (moved far below the map for the shot) at 256×256 on a
  transparent background, framed on its bounds at 20° pitch from the front-left; Save writes it with
  `DesignLibrary.SavePreview` (a library draft) or to `PalaceBuilder/cache/portraits/<pack>/<unit>.png` (any other).

### Any-asset visuals (data, additive)

```jsonc
"visuals": {
  "mesh": "mesh:Tree_02",            // "asset:<key>" | "mesh:<name>[@Map]" | "vanilla:<unit id>"
  "meshScale": 0.8,                  // null: fit to the base model's height (as before)
  "meshRotY": 180, "meshRotX": 0, "meshRotZ": 0,
  "meshOffset": [0, 0.2, 0],         // metres, after the fit
  "meshLook": "own",                 // "own": the source's materials; "unit": the enemy body material (tint, elite)
                                     // null: own for mesh:/vanilla:, unit for asset: (as before)
  "motion": "auto"                   // "auto" | "bob" | "none"; auto bobs a body that the base's rig doesn't move
}
```

- `mesh:` resolves like a prop (`SceneryLibrary.Resolve`: the host map's own mesh first, then the library), with the
  loaded level's materials, so the tree takes the map's colours. The template's hash includes the scene for these, so
  each level rebuilds them with its own materials.
- `vanilla:` takes every visible renderer of that unit's prefab (skinned ones in their bind pose), with its own
  materials.
- The source is attached as a child of the base model's biggest body part (so a rigid walk animation still moves it);
  a skinned body's parts are hidden (the existing `hiddenMesh` trick, since `GPUInstanced` draws hidden renderers)
  and the child goes on the root. Imported meshes keep the baked path (their UVs and baked colours).
- `motion` (`BuilderVisualMotion`): bob = a hop of 6 % of the height and a ±6° waddle at the unit's step rate while
  it moves (measured from its position each frame), a slow breathe when still. Auto = bob when the body was a skinned
  mesh or the source isn't an imported mesh baked into an animated part.
- Validation: `mesh` must be one of the three forms (asset keys checked as before, unit ids against the catalog);
  rotations in [-360, 360], offset components in [-50, 50], `meshLook`/`motion` from their lists.

### Arena fight

- **Its own ground** (`ArenaZone`): a fight builds a flat 44 m stone platform east of every pathfinding graph's
  bounds (so outside the level, opened from a level or from the library alike) with a `GridGraph` of its own
  ("PalaceBuilder Arena", flat, no collision checks), scanned alone. Every arena unit's `Seeker` and backup graph are
  pointed at it before its Start (when the movement scripts copy the mask into their nearest-node constraint), so they
  path and snap on the platform only. The game's target searches know only where the searcher stands, so while the
  zone exists `TargetPriority`/`TagManager` `Find*` calls remember that point and `FindAllTaggedObjectsWithTags` drops
  whatever is on the other side of the zone's edge (80 m): arena enemies can't pick the king, the castle or a building,
  and the level's troops and towers never see the fight. The turntable is hidden during the fight and the camera frames
  the platform; Stop, an edit that rebuilds the template, or Back removes the units, the graph and the platform, shows
  the turntable and frames the model again. The level's own graphs are never rescanned.
  Checked headless on Nordfels from a Level Studio: the platform at (223, 8, -31), every arena unit inside it, the king
  where he stood and outside it, the turntable hidden during the fight and back after Stop; 3 elite purple Racers kept
  their colour with the yellow elite outline.
- Fight (F5, or the button) spawns `count` (1-50) copies 16 m from the defenders, made like the game's `Spawn` makes
  them (the arena difficulty on hit points and damage, the elite material; no coins), and the chosen defenders
  (catalog player units, default 3 swordsmen and 3 archers) in an arc. Every arena unit gets a `BuilderArenaUnit`
  marker; enemies an arena enemy spawns on death are marked too. Attack scripts of marked units run as at night;
  unmarked ones don't.
- Defenders hold their spot (their home is where they stand); enemies target what their priorities find nearest, which
  is the defenders. The fight ends when one side is down or after 90 s; the units stay until the next fight, Stop (the
  Fight button again), an edit that rebuilds the template, or Back.
- Measured by polling hit points (from each unit's first frame alive): damage each way, the first and last kill, who is
  left. Checked in a pool instance: 4 Iron Brutes (120 HP) beat 3 swordsmen and 3 archers in 33.8 s; 4 Goblin Runners
  (8 HP) lose in 3.1 s without landing a hit.

### Level Studio hooks

- `StudioServices.Enemies` (`IStudioEnemyEditor.Open(draft, unitId, returnToLevelStudio, onReturn, out error)`, R2-C1's
  interface) is `EnemyStudioService`. Callers: the wave page's spawn-group rows, the library's Enemies tiles, and the
  Level Studio's right-click on empty ground ("Enemy Studio…"). With a Level Studio up it opens in place whatever the
  flag says (the scene is there); menus over the Studio (the wave page) are closed first.
- Checked: Level Studio (lasso tool, camera at (10, -20) zoom 1.2 yaw 30) → Enemy Studio → an edit → Back gives the
  same camera, tool and selection, the dock back, and the edit as the undo step "Set maxHp" of the level's draft.
- Keys: context `BuilderEnemyStudio`, claimed exclusive while its dock is the active menu: Esc Back, Cmd/Ctrl+Z, redo,
  Cmd/Ctrl+S, F5 Fight, Space turntable, W walk preview, F frame, Home reset view.
- Dev: `builder-enemy level [unit] | library [unit] | new [base] | state | dock | press <tile> | select <unit> | tab
  <palette> [panel] [look source] | set <field> <value> | look <ref|base> | fit <scale|-> | offset x y z | rot x y z |
  material own|unit | motion auto|bob|none | color <part> <#hex|-> | parts | picker | walk on|off | fight [n] | stop |
  readout | portrait | orbit <yaw> <pitch> <dist> | spin <deg> | undo | redo | save | back`.
- Not built: a building (a slot's built look) as the body; towers and walls are in the scenery library already. Towers
  as arena defenders (they live inside building prefabs with slots).

## Data-model changes (summary)

All are additive except one, `DesignPack.CurrentVersion` 1 → 2: `Migrations.Steps[1]` moves `add.activator` and
`add.activatorLevel` into `add.requires`.

| Field | Where | Owner |
|---|---|---|
| `requires {slot, level}`, `upgradeCap` | `BuildSlotEdit`, `BuildSlotAdd` | W3 |
| `base: "cleared" \| "flat"` | `LevelDef` | W3 (cleared, the field itself), W4 (flat ground) |
| `groups[] {id, name, members[]}` | `LevelDef` (editor metadata) | W1 |
| `spawnLines.add` applied (fields exist) | `LevelOverlay` | W3 |
| `terrain {…}` | `LevelDef` (see "Terrain") | W4 |
| `props[].base`: `"mesh:<name>[@<Map>]"` also resolves from the scenery library | `PropDef.Base` | W5 |
| `buildSlots.add[].base`: `"building:<kind>"` (the game's standard building, any map) | `BuildSlotAdd.Base` | R2-D |
| `units [[x, z], …]` (see "Unit positions") | `BuildSlotEdit`, `BuildSlotAdd` | R2-B |

The validator covers each, with paths so the Problems popup can select the object.

## Edge cases

- **Undo across play:** the history is the draft's, so it survives swaps. Undoing while in play mode is ignored with
  a notice ("Back in the Studio to undo").
- **A pack that can't be written** (built-in or plugin): the Studio works on the draft. Save becomes "Save a copy",
  and play never needs a save.
- **The level is deleted elsewhere** (pack editor over the Studio): the Studio leaves, as today (`OnDraftChanged`).
- **Builder switched off mid-Studio:** the session ends and the Studio leaves (today's behaviour).
- **Two host slots with one name:** only the first is editable (today's limit). The second shows as locked with a
  tooltip saying so.
- **Very large selections** (200+): label density drops to icons, and the inspector caps per-item lists with a scroll
  area (never "and N more").
- **Swap during a transition:** F5 is ignored while `SceneTransitionIsRunning`.
- **Pause menu in play:** the game's own pause works, with "Back to the Studio" added as its first row
  (`PauseMenu.AddButton`, visible only in Studio play).
- **Controller:** tools and panels are reachable with the d-pad (`ModNav`) and the cursor is moved by the right
  stick, but mouse and keyboard are the primary target. Controller polish is out of scope for v1.

## Workstreams

There are four workstreams, with minimal file overlap. W1 lands the shell first, or at least its interfaces, because
W2-W4 plug into it. Each owns its files outright. Shared files are touched only additively and called out in the PR.

### Interfaces (W1 creates them in its first commit, so others build against them)

`Features/Builder/Studio/StudioApi.cs`:

```csharp
internal enum StudioMode { Off, Loading, Edit, Play }
internal static class Studio
{
    public static StudioMode Mode { get; }               // W1
    public static bool Running => Mode != StudioMode.Off;
    public static PackDraft Draft { get; }  public static string LevelId { get; }  public static LevelDef Level { get; }
    public static event Action<StudioMode> ModeChanged;  // after a swap has finished loading
    public static StudioViewState View { get; }          // camera/selection snapshot (W1 fills, W2 stores/restores)
    public static Vector3? CursorGround { get; }         // ground under the pointer (or dev cursor)
    public static IReadOnlyList<string> Selection { get; }  public static void Select(IEnumerable<string> keys, SelectOp op);
    public static void Edit(string label, Action<LevelDef> change, string coalesce = null);   // through PackDraft.Edit + Prune
    // Extension points:
    public static void AddTool(StudioTool tool);          // W3 Link tool, W4 Terrain tool register here
    public static void AddInspectorSection(string id, int order, Func<IReadOnlyList<string>, bool> appliesTo, Action<ModPage, IReadOnlyList<string>> build);
    public static void AddLabelProvider(Func<string, StudioLabel> provider);   // W3: requirement badges, kind colours, icons
    public static void AddOverlay(IStudioOverlay overlay);  // W3 arrows, W4 brush cursor: per-frame world drawing
    public static void AddContextItems(Func<IReadOnlyList<string>, IEnumerable<(string label, Action run)>> items);
}
internal abstract class StudioTool { public abstract string Id, Name, Hint; public abstract Sprite Icon; public abstract Keybind Key;
    public virtual void Activate() {} public virtual void Deactivate() {} public abstract void Update(StudioPointer p); }  // p: ground point, buttons, drag state, modifiers, over-UI
internal interface IStudioPlay   // W2 implements; W1 calls it from F5 / the Play button
{ bool CanSwap(out string why); void EnterPlay(bool fromCursor); void ReturnToEdit(); }
internal static class StudioServices { public static IStudioPlay Play; public static IAssetBrowser AssetBrowser; }
```

As landed (`StudioApi.cs`), beyond the sketch above: `Studio.SetMode` (W2 sets Loading/Play, the shell Edit/Off),
`Studio.NextLoad` (W2 sets Play before a play swap and Edit before returning; the shell claims a host load only for
Edit), `Studio.View` (settable; the shell restores it on the next edit load, then clears it), `Studio.Items`/`Find`/
`ItemAt`/`ItemsChanged`/`SelectionChanged`, `Studio.Arm(StudioPlaceable)` (palette, library and terrain pieces share
the Place tool), `PickOnMap`, `AddViewToggle`, `AddDeleteGuard` (W3's "this slot is a prerequisite" popup),
`AddPanel(id, mode, visible, build)` (W2's sandbox panel), `StudioTool.HasPanel`/`BuildPanel` (W4's brush strip),
`StudioServices.Terrain` (`IStudioTerrain`: height while sculpting, stale paths) and `StudioViewOptions` (View ▾).
`Keybinds.ClaimExclusive(context, whileActive)` is in `Core/Keybinds.cs`.

### W1: Studio shell, tools, selection, inspector and menus

**Owns:**

- New `Features/Builder/Studio/`:
  - `StudioApi.cs`, `StudioManager.cs` (a BaseManager: keys and dev command `builder-studio`)
  - `StudioDock.cs` (top bar, toolbar, palette, right panel, status line)
  - `StudioSelection.cs`, `StudioTools.cs` (Select, Lasso, Paint, Move, Rotate, Place, Erase)
  - `StudioInspector.cs`, `StudioOutliner.cs`, `StudioMenus.cs` (context menu, Studio menu, Problems popup)
  - `StudioLabels.cs` (label layer), `StudioCamera.cs` (pan, right-click rules, zoom, turn, framing, edge pan)
  - `IAssetBrowser.cs`, `HostSceneryBrowser.cs`
- `Level/LayoutEditor.cs`: split into the Studio. The scene-as-a-view `Reconcile` and the edit functions stay, moved
  to `Studio/StudioScene.cs` and `Studio/StudioEdits.cs`.
- `Level/LayoutMenus.cs`, `Level/LayoutHud.cs`: deleted.
- `Level/LayoutMarkers.cs`: kind colours, outline highlight.
- `Level/BuilderLevelManager.cs`: keys and the dev command. Keep the `builder-level` subcommands working as aliases.
- `Editor/LevelPage.cs`: the Levels tab opens the Studio, and the level page is removed. `Editor/BuilderEditorManager.cs`
  loses the hint.
- New `Core/UI/ModDock.cs`, a kit piece: a persistent `UIFrame` with `freezeTime = false`, `freezePlayer = true` and
  `canNotBeEscaped = true` (its Esc goes to the owner). It hosts docked panels built from `ModPage`/`ModTile`/
  `ModScrollArea`, and offers `PointerOverPanel` so world input knows when the mouse is on UI. Popups (`ModPopup`)
  open over it. Document it in CLAUDE.md's UI kit section.
- `Core/Keybinds.cs` (additive): `ClaimExclusive`.

**As built (W1):**

- Files: `Studio/StudioShell.cs` (lifecycle, per-frame loop, keys, `IStudioShell`), `StudioScene.cs` (the scene as a
  view of the level), `StudioEdits.cs` (every change, one undo step each), `StudioMath.cs` (pure logic, unit-tested in
  `tests/PalaceMod.Tests/StudioTests.cs`), `StudioSelection.cs`, `StudioTools.cs`, `StudioCamera.cs`,
  `StudioInput.cs` (pointer and modifiers; the dev command scripts drags through it), `StudioKeys.cs`, `StudioDock.cs`,
  `StudioPalette.cs`, `StudioInspector.cs`, `StudioOutliner.cs`, `StudioMenus.cs`, `StudioLabels.cs`,
  `HostSceneryBrowser.cs`, `StudioManager.cs`. Tool and top-bar icons: `branding/studio/draw_icons.py` →
  `src/PalaceMod/Assets/studio_*.png`.
- Keys that differ from the table above: Erase is **Shift+E** (E turns the view). WASD always pans; the arrow keys pan
  only with nothing selected and otherwise nudge the selection by the snap step (Shift ×5) in the Select and Move
  tools. Tab shows or hides the palette.
- Groups: selecting a group selects its members, so `Studio.Selection` never holds `group:` keys. Clicking a member
  selects the group; Alt-click selects the member alone.
- The dock fades the game's HUD (`UIFrameManager.InGameUIContainer`) while editing.
- X-ray draws the rings over scenery (their material's depth test). W3's overlay fades the scenery.
- Play mode: panels registered with `Studio.AddPanel(id, StudioMode.Play, …)` show in a second dock on the right that
  doesn't freeze the king. It becomes the active menu only while the pointer is on it, so the game keeps its input
  otherwise. `Studio.PointerOverUI` covers both docks.
- `Studio.AddPlaceHook(Action<LevelDef, string>)` runs inside the edit that places a slot or prop (Place, paste,
  duplicate). `Studio.ItemPlaced` fires after it. On an empty base a new slot gets "needs castle level 1" first.
- Not built yet: Home frames all, but there's no "hide" overlay in the outliner. Library tab is the host's scenery
  until the cross-map library registers its browser. Spawns tab lists and selects the host's lines; adding a line is
  W3's (`spawnLines.add`).
- Dev: `builder-studio open|new|state|dock|select|tool|arm|cursor|click|drag|dragw|rclick|press|cam|delete|dup|copy|`
  `paste|group|ungroup|frame|snap|cost|issues|check|play|leave|json`; `builder-level` forwards its editing
  subcommands there. `keybinds press LeftCommand+Z` presses a chord.

**Acceptance:**

- Every key in the table works: Del or Backspace deletes, and no UndoUpgrade notice appears.
- Marquee, lasso and paint select work. Moving 20 objects is one undo step.
- Multi-edit costs show "—" for mixed values.
- Right-drag pans; right-click opens the context menu.
- No first-use hints. Panels keep 24 px gutters at UI scale 0.8 to 1.2.

### W2: test play

**Owns:**

- New `Features/Builder/Studio/Play/`:
  - `StudioPlay.cs` (`IStudioPlay`: the session state machine, fast swap, state store and restore)
  - `SandboxPanel.cs` (a `ModDock` panel)
  - `SandboxActions.cs` (infinite gold, spawn at cursor or line, teleport; everything else calls `DebugActions`)
  - `TestPacks.cs` (the play pack, pure C#, `tests/PalaceMod.Tests/TestPackTests.cs`)
  - `StudioPlayManager.cs` (as built: a manager rather than `BuilderManager.Play.cs`, because play needs its own
    Update and keys; patches: infinite gold, the cloud-time shortening during swaps, skipping the session
    notice and scores in Studio sessions, the pause-menu row, the end-screen row)
- `Session/SessionLauncher.cs` (additive):
  - a `SessionRequest.Studio` flag (or `StudioSession` subtype) that keeps the session across same-host reloads and
    marks it Edit or Play
  - skips `ScoreStore` and the notice
- `Editor/EditorReturn.cs`: stop the music on redirect, and ignore Studio swaps.
- The `AfterTutorialTipManager.Update` prefix (`StudioPlayManager.cs`).

**Uses:** `Studio.View` and `ModeChanged` from W1; `DebugActions` (Features/Debug, called as is, no edits).

**Acceptance:**

- F5 → playing at the cursor with ∞ gold.
- F5 → the same camera, selection, tool and undo history.
- Each way ≤ 1.5 s on Nordfels (log the measured times).
- No music after Back during a night.
- No formation, zoom or sprint tips in either mode.
- Spawning 10 custom enemies at the cursor makes them attack.
- The end screen's "Back to the Studio" works.
- The vanilla Retry and Back to Map still work in non-Studio custom runs.

### W3: requirements, presets, previews and the empty map

**Owns:**

- `Model/LevelDef.cs`: `requires`, `upgradeCap`, `base`.
- `Model/Validation.cs`: the level parts.
- `Model/PackSerializer.cs` and Migrations: the v2 step.
- `Level/LevelOverlay.cs`: requires/cap wiring, empty base, `spawnLines.add`.
- `Level/LayoutScene.cs`: proxies keep a built-look copy, castle lookup, slot kind classification.
- New `Features/Builder/Studio/Requirements/`:
  - `LinkTool.cs` (a `StudioTool`)
  - `RequirementArrows.cs` (an `IStudioOverlay`)
  - `ChainPreset.cs` (the three algorithms, pure C# and unit-testable)
  - `RequirementInspector.cs` (an inspector section)
  - `SlotKinds.cs` (kind → colour and icon, a label provider)
- New `Features/Builder/Studio/Start/StartPopup.cs` and `Thumbnails.cs`.
- `tests/PalaceMod.Tests`: chain and cycle tests.

**Uses:** W1's `AddTool`, `AddInspectorSection`, `AddLabelProvider`, `AddOverlay` and `AddContextItems`.

**Acceptance:**

- A level with castle-level and slot requirements plays with slots appearing exactly when the game's own preview says
  they will.
- The three chain modes produce the drawn arrows, and play confirms them.
- Cycles are refused.
- A Cleared base loads with only the castle and the map's ground on all 10 main maps (with screenshots), and enemies
  still reach the castle.
- A new spawn line spawns enemies.
- Built preview shows level-1 buildings.

### W4: terrain

**Owns:** `Studio/Terrain/` (`TerrainTool.cs`: tool, strip, ring, `IStudioTerrain`; `TerrainBrushes.cs`;
`TerrainSpike.cs`; `BuilderTerrainManager.cs`: registration and the `builder-terrain` command), `Level/GroundLayer.cs`,
`Level/GroundCodec.cs`, `Level/GroundGraphs.cs`, `Level/MinimapRefresh.cs`, `Model/TerrainDef.cs`.

- Hooks in others' files (additive): `LevelOverlay` calls `GroundLayer.Apply(scene, pack, level, issues)` after the
  empty base's look, in play and in the edit load, and `HasEdits` counts terrain; `PathCheck.Rescan` is
  `GroundGraphs.Scan`; `LayoutScene.Ground` reads the terrain first; `LevelDef.terrain`; the Start popup's Flat field
  is switched on by `StartPopup.FlatFieldAvailable`.
- Anything that rescans the graphs must use `GroundGraphs.Scan`, never `AstarPath.Scan` (see "T0 results").

### W6: agent integration (as built)

Players' own coding agents (Claude Code and the like) build, check, picture and play-test levels from a normal install.

- **Agent bridge** (`Features/Builder/Agent/AgentBridgeManager.cs`, Settings > Mods > Builder > "Agent bridge", off by
  default): reads one-line commands from `BepInEx/PalaceMod/Agent/bridge/command.request` and answers in
  `command-<id>.response`, the same protocol and command set as `dev.py cmd` (`DevToolsManager.Execute`), plus
  `status.json` every 2 s. File-only, nothing on the network. While on, the game keeps running unfocused ("Run in
  background"). Commands that change a vanilla run mark its gameplay impact.
- **`agent` command** (`AgentCommands.cs`; JSON results): `status`, `slots`, `catalog`, `host <file>` (`MapDump`: slots
  with upgrade trees, spawn lines, scenery with sizes, and a 2 m ground grid: heights plus walkability rasterised from the
  enemy navmeshes), `packs`/`reload`/`validate`, `play`, `sandbox` (W2's test play), `edit` (W1's Studio), `vanilla`,
  `menu`, `night`, `report` (`NightRecorder`: outcome, castle low, buildings knocked out, units, enemies), `build`
  (`pay` charges gold), cheats, `spawn`, `teleport`, `paths`, `capture` (`MapCapture`: north-up world picture),
  `screenshot`.
- **The agent kit** (`agent-kit/`, embedded in the DLL, written to `BepInEx/PalaceMod/Agent/` when the bridge turns on):
  `level-building.md` (the guide), `SKILL.md` (Claude Code skill), `reference.md` (generated tables), `palace.py`
  (stdlib Python CLI: JSON edit ops, offline checks, PNG renders, economy and wave summaries, in-game tests with
  purchase plans), `examples/` (a worked level). `palace.py ref build` caches the catalog and every map's dump from the
  running game; `ref doc` regenerates `reference.md`.
- Offline path checks run on the cached grid (props and terrain applied; large enemies need the host's large-graph
  radius as clearance: 2 m on most maps, 4 on Nordfels, 1.5 on Durststein; the map dump's `graphs`, else
  `HOST_GRAPHS` in palace.py); the game's `paths` is the authority. Measured widths: level-building.md section 7.

### File-overlap table

| File | W1 | W2 | W3 | W4 |
|---|---|---|---|---|
| `Studio/*` (shell) | **own** | | | |
| `Studio/Play/*` | | **own** | | |
| `Studio/Requirements/*`, `Studio/Start/*` | | | **own** | |
| `Studio/Terrain/*`, `Level/Ground*.cs`, `Level/MinimapRefresh.cs` | | | | **own** |
| `Level/LayoutEditor.cs` (→ Studio) | **own** | | | |
| `Level/LevelOverlay.cs` | | | **own** | additive hook (`ApplyTerrain` call) |
| `Level/LayoutScene.cs` | read | | **own** | read |
| `Model/LevelDef.cs`, `Validation.cs`, Migrations | `groups` only (additive) | | **own** | `terrain` (additive) |
| `Session/SessionLauncher.cs` | | **own (additive)** | | |
| `Editor/EditorReturn.cs` | | **own** | | |
| `Editor/LevelPage.cs`, `BuilderEditorManager.cs` | **own** | | | |
| `Core/UI/ModDock.cs`, `Core/Keybinds.cs` | **own** | | | |
| `CLAUDE.md`, `docs/builder-design.md` | UI kit + Studio section | play section | requirements section | terrain section |

Merge order: W1's `StudioApi.cs` + `ModDock` skeleton first (a small PR), then all four in parallel. W3 lands
`LevelDef` changes before W4 adds `terrain`.

## Out of scope (v1)

- The wave page redesign (it opens from the Studio's Nights popup unchanged).
- Moving the castle.
- AND requirements (decided: chain through other slots instead).
- Removing or moving individual host scenery (only all-or-nothing via the empty base).
- Controller-first editing.
- A web editor.

## Open questions (the user's to decide)

1. **Terrain now or later?** W4's T1 + T2 (a sculptable Flat field) is the largest and riskiest piece (L). Options:
   staff it in parallel now, or ship W1-W3 first and start W4 with the T0 spike. T3 (walkable paint and cliff
   pieces, S-M) gives most of the "shape the playable area" value on any map.
2. ~~Two requirements at once~~ Decided: one native prerequisite per slot; combine by chaining.
3. **Terrain in share codes.** Options: embed it in the level JSON (self-contained, but codes for terrain levels are
   usually over the 2,000-character chat size, so it goes through `.tfpack`), or keep it in an asset file (codes stay
   small, but a code alone doesn't carry the terrain).
