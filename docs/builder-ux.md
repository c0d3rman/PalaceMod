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
| 6. Enemies | Spawn lines (move, add), nights (wave set) | Studio (lines), Nights popup → wave page |
| 7. Test | F5 in at the cursor, sandbox (gold, nights, spawn, teleport), F5 out | Studio play mode |
| 8. Share | Save, share code, export | Studio menu → existing `SharingUI` |

Stages 3 to 7 repeat many times, so their actions are one or two inputs each, with the map always visible.

## Screen map

```text
Title / pause menu
  └ Builder (home: packs)                      ModFrame, as today (PackListPage)
      └ Pack (Units · Wave Sets · Levels · Pack · Problems)
          ├ Levels row "Open"  ───────────────►  STUDIO (in-world, on the level's map)
          └ "New level" → Start popup ────────►  STUDIO
STUDIO
  ├ Edit mode: toolbar · palette · inspector/outliner/level · top bar · status line
  ├ popups: context menu, requirement picker, chain preset, start/base, nights, problems, Studio menu
  ├ Nights → WavePage (ModFrame over the Studio; Back returns to the Studio)
  └ F5 ⇄ Play mode: the level as it plays + sandbox panel
```

Leaving the Studio (Studio menu → Leave) returns to the pack screen where it was opened (today's `EditorReturn`).

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
  - **Slots:** every slot kind on the host. Shows the icon, the cost chain and the count on the map.
  - **Scenery:** the host's scenery meshes, with a text filter.
  - **Spawns:** spawn lines, both moving the host's and adding new ones.
  - **Library:** the asset browser slot (see "Asset browser slot").

  Clicking a tile arms the Place tool with that kind. Keys 1 to 9 recall the last nine kinds placed (a hotbar).
- **Right panel** (340 px). Tabs:
  - **Inspector:** the selection.
  - **Outliner:** every object, filterable.
  - **Level:** name, description, base map, nights, starting gold, and the path check with its result.

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
| Paint select | B | A ground brush (wheel+Alt sizes it). Dragging adds what it passes over, and Alt+drag removes. |
| Move | G | Grab: the selection follows the cursor. Click commits; Esc or a right click (without a drag) cancels. X/Z lock an axis, and arrow keys nudge by the snap step (Shift ×5). |
| Rotate | R | Turns the selection about its centre by mouse. `,` and `.` step by the turn step (they also work in Select). |
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
- Rows show the icon, name, kind, cost chain and requirement. Clicking selects (Shift/Ctrl as on the map) and
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
  ("♜2" or "⤳ House 6").
  - Density follows zoom: icons only when zoomed out, full labels when zoomed in, and always full on hover and
    selection.
  - The View dropdown picks what the text shows.
  - Labels never overlap: lower-priority labels hide (selection > hover > castle > others by distance).
- **Built preview** (View ▾ or hold Alt): each slot proxy shows its built building at level 1, a ghost in the game's
  `BuildingInteractor.previewMaterial`. With one slot selected, the inspector's level stepper previews that level
  (`UpgradeBranch.replacementMesh` / `objectsToActivate`), like the game's preview key. Today the proxy deletes
  `buildingParent` (`LayoutScene.Proxy`); W3 keeps a stripped copy instead.
- **Hover card** (tooltip, `ModTooltip`): kind, costs per level, income per level, what it needs, what it unlocks,
  and its issues.
- **X-ray** (X): scenery at 35 % alpha, rings and labels drawn on top.
- **Requirement arrows** (View ▾ Links): curved ground arrows from prerequisite to dependent. They are verdigris for
  slot links and gold for castle-level links, labelled with the level when it's not "built".

### Path check

The "Check enemy paths" button is in the Level tab and in View ▾ Routes. It runs automatically, debounced 1.5 s
after an edit that moves solid things, while Routes is on. Each check is a rescan (120 to 220 ms warm on
Nordfels). The result is a status chip in the Level tab and the top bar's Problems badge ("Blocked: Forest").

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
- **Two requirements at once** ("castle level 3 *and* House 6") are not native. With one activator, the Studio
  expresses it the native way where possible: link to House 6, and if House 6 itself needs castle level 3, the
  dependent inherits it (shown as "needs House 6 (castle 3 via House 6)"). A true AND gate is out of scope for v1
  (see "Open questions").

### Data (additive; `DesignPack` version 2 with a migration)

`BuildSlotEdit` and `BuildSlotAdd` get:

```jsonc
"requires": { "slot": "castle" | "<host slot name>" | "add:<id>" | null, "level": 0 },   // null object = keep the host's / base's
"upgradeCap": 0                                                                            // requiredRootLevelDifference; null = keep, -100 = none
```

- `slot: null` with the object present means "available from the start" (`startDeactivated = false`, no activator).
- `"castle"` resolves to the host's castle slot.
- `level` is the activator level, so 0 = built. The UI shows "Castle level N" as `level = N-1`.
- `Migrations.Steps[1]` turns `add.activator`/`add.activatorLevel` into `add.requires`. The old fields are read but no
  longer written.
- The validator refuses cycles (`levels[i].buildSlots…requires: A needs B needs A`), unknown slots, a `requires` on
  the castle or on an `ActivatorUpgradesThis` add-on, and `level` past the prerequisite's highest level.
- **Applying (`LevelOverlay`):** after cloning and before activation, set `activatorBuilding`, `activatorLevel`,
  `startDeactivated` and `requiredRootLevelDifference` on host slots and clones alike. This is the same window the
  clone code already uses. `BuildSlot.Start` then builds `requiredRoot`, `isActivatorOf` and the rest itself.
- **Removing a prerequisite:** deleting a slot that others need offers (popup, default first): "Link its dependents
  to what it needs" (the chain closes up) / "Make them available from the start" / Cancel. Today removal silently
  hides every slot the removed one unlocks (`LevelOverlay` removal comment); the validator now warns about any
  dependent of a removed slot.

### Editing

- **Inspector "Needs" row:** a choice of "From the start" / "Castle level ▸ 1-5" / a slot (picked on the map) plus
  "at level ▸ built / 2 / 3…".
  - It also has an "Upgrades ≤ castle level" toggle (cap 0) with an offset stepper (cap n).
  - Multi-select sets them all, and mixed values show "—".
- **Link tool (K):**
  1. Select dependents (or click them with K active).
  2. Click the prerequisite. Clicking the castle opens a level stepper popup; clicking a slot with more than one
     level opens "after: built / level 2 / …" (default: built).
  3. Shift keeps the tool armed. Alt-click a dependent clears its requirement.
  4. A link that would make a cycle is refused with a notice.

  While K is active, all link arrows show, and hovering a slot highlights its whole chain to the castle.
- **Castle-level painting:** context menu "Needs ▸ Castle level N" on a selection. In the Requirements label mode,
  each slot's badge is clickable and cycles 1-5.
- **House chain preset** (Inspector "Chain…" button or context menu Chain ▸, on a selection of slots; the kinds may be
  mixed). A popup:
  - **In selection order:** the first selected needs its current prerequisite (or the castle at level 1). Each next
    one needs the previous one, built.
  - **Nearest neighbour from the castle:** a greedy path. Start at the selected slot nearest the castle (it needs the
    castle), and each next one is the nearest unlinked slot to the last.
  - **Branching from the castle:** a minimum spanning tree (Prim's) over the selection plus the castle, rooted at the
    castle, with each slot needing its tree parent. This matches how vanilla maps fan houses out, and is the default
    for houses.
  - Options: "first link at castle level [1]", "every N-th link needs castle level +1" (off by default), "link level:
    built". There's a live arrow preview before Apply, and Apply is one undo step.
- **Auto-link while placing** (palette toggle, on by default for houses): a new house needs the nearest existing house
  (or the castle if none is within 25 m). Other kinds default to "castle level 1".

### Empty-map default

On an empty base, new slots default to "needs castle level 1": available once the castle is built. A slot with
`requires` absent would otherwise inherit its base's activator, which is removed on an empty map, so it would never
appear. The Outliner's "No requirement" chip and a validator warning catch such slots.

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
5. **F5** in play (or the pause menu's first row "Back to the Studio", or the end screen's "Back to the Studio")
   stops the music, then reloads the host in **edit** state and restores the stored editor state.

### Fast swap and "Why not play in place"

**Play in place, without a reload, is not feasible robustly:**

- The edit scene's real slots have already run `Start()`. They are deactivated and replaced by inert proxies
  (`LayoutEditor.Enter`).
- The overlay must run after Awake and before Start. Clones, activator wiring and removals all depend on it: removal
  before Start keeps a slot out of its activator's `isActivatorOf`; cloning a started slot copies runtime lists.
- Returning from play would also have to undo everything play changed: buildings, gold, day and night, dead units,
  enemies. Savestates can't do that without a reload either (`SaveStates.Restore` reloads the scene).

**So each switch is a scene reload, made fast:**

- One `StudioSession` spans both states (`Mode = Edit | Play`). Switching reloads the same host through
  `SceneTransitionManager.TransitionFromNullToLevel(host)`. `EditorReturn` ignores it because the scene is the
  session's host (`EditorReturn.cs:297`), and the session never ends in between.
- During a Studio swap, `cloudFadeInTime` and `cloudFadeOutTime` (`SceneTransitionManager`, fields read in
  `SceneTransitionAnimation`) are set to 0.12 s and restored after. The load itself is the cost. Measure it, and
  target ≤ 1.5 s per switch on Nordfels.
- The menu stays visible under the clouds, as today.
- The pre-level loadout, perk screen, "Custom run" notice and night-warning `Notify` are skipped. The loadout is
  `TestLoadout` (last used on the map) as today.

### Sandbox panel (play mode, right side, collapsible with Tab)

Non-modal dock like the Studio's. Keys in brackets (context `BuilderPlay`).

| Row | Control | Backed by |
|---|---|---|
| Gold | ∞ toggle (on) · number | new: an infinite-gold patch (W2) (a `TryToBuildOrUpgradeAndPay`/`PlayerInteraction` balance check that treats the balance as enough and doesn't deduct); `DebugActions.SetGold` for a set value |
| Night | ▶ Start night [N] · ■ End night · Jump to night [number] | `DebugActions.StartNight`, `EndNight`, `JumpToNight` |
| Speed | 0.5 · 1 · 2 · 4 · 8 [`[` `]`] | `DebugActions.SetSpeed` |
| King | God mode · Revive | `SetGodMode`, `ReviveKing` |
| Kingdom | Invulnerable · Heal all · Build all · Max upgrade nearest | `SetInvulnerableKingdom`, `HealAll`, `BuildAllBlueprints`, `MaxUpgrade(NearestBuilding())` |
| Enemies | Kill all [K] | `KillAllEnemies` |
| Spawn | unit picker (catalog + pack units, icons) × count [1-50] · elite · at: cursor click / a spawn line | new (W2): instantiate like `Spawn.Update` (decompiled `Spawn.cs:140-180`), with `IUnitFactory.Resolve` for pack units, the night's difficulty scaling and elite material, so enemies path to targets as in a wave |
| Teleport | T then click the ground; Shift+T brings followers | `PlayerMovement.TeleportTo` + followers via `GetNearestGroundPosition` (as `TeleporterManager.cs:201-226`) |
| Restart | Restart play from the same spot (R) | the same fast swap, Play → Play |
| Back | Back to the Studio [F5] | swap to Edit |

- Play mode starts in day 1 planning time. "Start night N" jumps straight to that night.
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

## Starting a level, and the empty map

**Start popup** (Pack › Levels › New level, or Builder home › New level, which creates a pack when there's none):

```text
 New level
 [ Empty ]  [Nordfels]  [Uferwind]  [ …10 main maps, thumbnails ]
 Name  [Goblin Pass        ]          Nights  [The map's own ▾]
                                                    [Create and open]
```

- Thumbnails: `LevelCapture` can't render a map that isn't loaded. So each tile shows the map's own preview sprite
  from its `LevelInfo` where it has one, else its name. A thumbnail is captured the first time the Studio opens a
  host and cached at `PalaceBuilder/cache/thumbs/<scene>.png` for later.
- **Empty**: a second row picks **Cleared** (the map's ground, nothing on it) or **Flat field** (generated flat
  ground), and the map that supplies the castle position, lighting, colour scheme and sky (default Nordfels). It
  creates the level with `"base": "cleared"` or `"base": "flat"` (null = the map as it is).

**What "empty" means technically.** Creating a scene from nothing stays out (`builder-design.md`: managers, camera,
day/night and lighting all live in the level scenes). An empty level is the host map loaded as usual, with these
changes applied by the overlay at load (after Awake, before Start):

1. **Both kinds:** every build slot except the castle is removed. They stay available as clone bases, because clones
   are made before removals, as today.
2. **Both kinds:** scenery is hidden.
   - The game's `ColorAndLightManager` names its ground, water and road materials (`groundMaterial`, `groundHigh`,
     `groundLow`, `earthMaterial`, `sandMaterial`, `groundPatches`, `rockMaterial`, water, ocean, `road`;
     decompiled `ColorAndLightManager.cs:66-99`).
   - Every renderer in the host scene is disabled **unless** it uses only those materials, or it belongs to the
     castle, the spawner, the camera, UI or managers. This works whether or not MeshFusion has combined the scenery,
     because combined chunks are per material. A spike checks it on the 10 main maps first: screenshot and count the
     renderers hidden per map.
   - Scenery colliders under hidden renderers are disabled too. The host's `invisibleWalls` stay, so the map's
     boundary still holds.
3. **Cleared only:** the host ground stays, so cliffs, water and paths keep working with no new geometry. The
   host's spawn lines stay where they are.
4. **Flat field only:** the host's ground renderers and colliders are disabled too. W4's ground layer (see
   "Terrain") supplies a flat heightfield at the castle's height, its collider, boundary walls and the minimap
   rebuild. The host's spawn lines are moved to the field's edges.
5. `spawnLines.add` is implemented (W3; the fields already exist). It clones a host `EnemySpawnLine` under the
   spawner and renames it, so wave spawns can name it. Empty maps need their own lines.
6. One full rescan after Start, which happens today after any overlay (`BuilderLevelManager.ScanAfterLevelStart`).

Cost: Cleared is S-M and low risk, since it only removes things and keeps the host's ground, walls and graphs
semantics. Flat field is M on top of W4's ground layer.

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
    The cost is unmeasured.
- **Units and enemies take their height from the navmesh**, snapping to it every frame
  (`PathfindMovementEnemy.cs:321-343`, `PathfindMovementPlayerunit.cs:399-401`). Raised ground carries them as soon as
  the graphs are rescanned.
  - **The king** is a `CharacterController` with gravity, so he needs a real collider. Below y = -10 he's teleported
    back to the start (`PlayerMovement.cs:32, 261-266`).
  - Coins and lobbed projectiles raycast a `groundLayer`.
- **Fliers** use `Enemy Units Air`, spawning 5 m up (`Spawn.cs:276`). Whether that graph is recast or mesh-sourced
  (the `flyerSurface`) isn't recorded. Falkenpass generated an 8 m grid surface 5 m above its terrain.
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

### Feasibility

| Feature | Feasible | Cost | Main risk |
|---|---|---|---|
| Walkable/blocked paint (ground units) | yes, now | S-M | Fliers ignore it (by design). Uses the solid-prop blocker pattern (`LayoutScene.SetSolid`). |
| Cliff, rock and wall pieces as solid props | yes, now (blocking) | S | Host scenery meshes may be unreadable. Blocking already works through blocker boxes. |
| Walkable ramps and plateaus as pieces | yes | M | Needs a generated readable mesh plus a `MeshCollider` (the king) on the Ground layer. |
| Water planes and shorelines | yes | M | Visual: the ocean or water material. Blocking: a footprint cut from the heightfield, or a layer-7 blocker. |
| Sculpting on a **Flat field** (heightfield replaces the ground) | yes | L | Air surface (unknown graph type), minimap rebuild, rescan time, boundary walls, y ≥ -8. |
| Sculpting **on a vanilla map's ground** | partly | L+ | The host ground is one combined mesh with hand-made walls and colliders. Raising is possible (heightfield patches on top); lowering and digging aren't, without replacing the ground. Later. |
| Removing parts of a vanilla map piecemeal | partly | M-L | MeshFusion can cut sources from combined meshes (`CombinedObject.cs:109-112`), but whether the scenes keep their sources is unverified. v1 only does all-or-nothing (Cleared). |

### Design (W4)

**Stages:**

| Stage | Cost | Delivers |
|---|---|---|
| T0 spike | S | Run `builder-level spike graphs` on Nordfels and Uferwind: the Ground layer number, cell size, tiles and bounds, and the Air graph's type and source. Time a `GraphUpdateObject` over a 20 m box. Check that the invisible walls survive a rescan (they might use `HideMeshRendOnPlay`, which disables renderers the scanner then skips). Write the numbers into this doc. |
| T1 ground layer + Flat field | M | A generated heightfield ground. All of these are needed for the Flat field: chunks, readable meshes, a `MeshCollider` on the ground layer, the host's materials, boundary walls (a mesh and collider around the field, on the invisible-wall layer), an Air surface (if T0 shows it's mesh-sourced: a grid 5 m above the max height, as Falkenpass did), and the minimap rebuild (`MinimapRenderer` private `GenerateGroundMesh` + `RenderBackground` after the rescan). |
| T2 brushes | L | Raise/lower, flatten, smooth, set height, material paint; live re-mesh in the editor. |
| T3 walkable paint + pieces | S-M | Blocked/walkable paint on any base, and cliff/ramp/rock pieces in the palette. |
| T4 water | M | A water level and water paint on the Flat field, with shoreline material. |
| Later | | Patches on vanilla ground (raise only), piecemeal scenery removal on vanilla maps. |

**Data** (`LevelDef.terrain`, additive):

```jsonc
"terrain": {
  "origin": [x, z], "cell": 2.0, "size": [w, h],          // grid in world space; 2 m cells by default
  "heights": "<base64 deflate of int16 cm, row-major>",   // (w+1)*(h+1) samples; absent = flat at the castle's height
  "materials": "<base64 deflate of byte per cell>",       // 0 ground, 1 high, 2 low, 3 earth, 4 sand, 5 road, 6 rock
  "blocked": "<base64 deflate bitmask per cell>",         // ground units can't walk; fliers can
  "water": { "level": -1.5, "cells": "<base64 bitmask>" } // water surface; water cells are unwalkable
}
```

- Size: a 200 × 200 m field at 2 m is 10k samples (20 KB raw), and mostly-flat fields deflate to a few KB.
  - That is too big for chat-sized share codes. `SharingUI.CopyCode` already suggests a `.tfpack` above 2,000
    characters, and terrain levels will usually take that path.
  - Alternative (open question): store terrain in an asset file (`assets/terrain.bin`) referenced from the level, so
    codes stay small, at the cost of a code not carrying the terrain.
- Heights are clamped to [-8, +30] m. Below -10 the game respawns the king.

**Brushes** (Terrain tool, T; a brush strip in the palette area while active):

| Brush | Click/drag | Modifiers |
|---|---|---|
| Raise / lower | Adds height under the brush (falloff) | Shift = the other way |
| Flatten | Sets the height to the height under the press | Alt-click samples a height |
| Smooth | Averages | |
| Set height | To the number in the brush strip | |
| Paint material | The chosen material | |
| Block / unblock | The walkable mask (ground units) | Shift = unblock |
| Water | Marks cells as water, at the water level | Shift = remove |

- Brush size: `[` `]` or Alt+wheel. Strength is in the brush strip. A ring cursor (an `IStudioOverlay`) shows size
  and falloff.
- Each stroke is one undo step (coalesced through `PackDraft.Edit` with a stroke id).
- Slopes over the recast graph's max slope become unwalkable on their own (cliffs). The mesh paints faces steeper than
  that with `rockMaterial`, so cliffs read as cliffs.

**Editing performance:**

- Re-mesh only the touched chunks (32×32 cells) during a stroke: no graph work, and markers snap to the new height
  through `LayoutScene.Ground`. That falls back to a raycast on the ground layer when the stroke leaves the graph
  stale, because `Ground` reads `Enemy Units S`.
- On stroke end, mark the graphs **stale**. "Routes" (path check) and F5 rescan: a local `GraphUpdateObject` over the
  touched bounds if T0 shows it's cheap, else a full `Scan()`.
- A "paths out of date" chip shows in the top bar while stale.

**At play:**

1. The overlay builds the ground layer before Start (meshes, collider, walls, Air surface).
2. The existing full rescan runs at Start+2.
3. Rebuild the minimap.
4. Check reachability (already done).

**Owns:**

- New `Features/Builder/Studio/Terrain/`:
  - `TerrainTool.cs`, `TerrainBrushes.cs` (pure C#, unit-tested: brush maths on a height grid), `TerrainStrip.cs`
- New `Features/Builder/Level/`:
  - `GroundLayer.cs` (heightfield → chunked meshes, colliders, materials, Air surface, walls)
  - `GroundCodec.cs` (pack encoding, unit-tested)
  - `MinimapRefresh.cs`
- An additive `LevelOverlay.ApplyTerrain` call, and `LevelDef.TerrainDef`.

**Acceptance (T1 + T2):**

- A Flat field with a sculpted hill and a cliff ring around the castle with one ramp: ground enemies take the ramp,
  fliers cross the cliff, and the king walks up the ramp and can't walk up the cliff.
- The minimap shows the new shape.
- Path check, rescan and swap times are logged.

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
| `props[].base` kinds from the library | `PropDef.Base` | library effort |

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
  - `BuilderManager.Play.cs` (patches: infinite gold, the cloud-time shortening during swaps, skipping the session
    notice and scores in Studio sessions, the pause-menu row, the end-screen row)
- `Session/SessionLauncher.cs` (additive):
  - a `SessionRequest.Studio` flag (or `StudioSession` subtype) that keeps the session across same-host reloads and
    marks it Edit or Play
  - skips `ScoreStore` and the notice
- `Editor/EditorReturn.cs`: stop the music on redirect, and ignore Studio swaps.
- The `AfterTutorialTipManager.Update` prefix (`BuilderManager.Play.cs`).

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

**Owns:** see "Terrain, Design (W4)". It is a separate workstream because it touches the ground, the graphs and
play-time rescans, none of which the others need.

- It starts with the T0 spike, which needs a pool game instance.
- The Flat field base uses W3's base plumbing (`LevelDef.Base`, the scenery hide). The contract between them is
  `LevelDef.terrain` and one call, `GroundLayer.Apply(scene, level)`, that W3's overlay makes when `base == "flat"` or
  `terrain != null`.
- W4 can be staffed after W1-W3 if only three agents are available. Its T3 (walkable paint) is the cheapest
  user-visible part if the plan has to be cut.

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
- AND requirements.
- Removing or moving individual host scenery (only all-or-nothing via the empty base).
- Controller-first editing.
- A web editor.

## Open questions (the user's to decide)

1. **Terrain now or later?** W4's T1 + T2 (a sculptable Flat field) is the largest and riskiest piece (L). Options:
   staff it in parallel now, or ship W1-W3 first and start W4 with the T0 spike. T3 (walkable paint and cliff
   pieces, S-M) gives most of the "shape the playable area" value on any map.
2. **Two requirements at once** ("castle level 3 and House 6"). The game allows one prerequisite per slot. Options:
   go without (chain through a slot that needs the castle level), or add a mod-side gate (a patch that keeps a slot
   hidden until both are met). The gate would also work in vanilla-less shared levels, but it's beyond-vanilla
   behaviour.
3. **Terrain in share codes.** Options: embed it in the level JSON (self-contained, but codes for terrain levels are
   usually over the 2,000-character chat size, so it goes through `.tfpack`), or keep it in an asset file (codes stay
   small, but a code alone doesn't carry the terrain).
