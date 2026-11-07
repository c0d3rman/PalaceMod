# Economy planner: interface design

The planner's simulation (EconomyModel, EconomySnapshot, PlannerStore) stays as it is. This replaces its screens.

## What the player is doing

The planner answers speculative questions fast, while the run is paused:

- "If I upgrade this mill and build that gold mine on day 4, how much gold will I have later?"
- "What if I also build two towers on day 5?" (a branch, compared with the plan)
- "What if these three houses burn on night 6?" / "If I pray at the temple on day 7?"
- "Five more houses on day 3." (bulk)
- "Is Interest or Treasure Hunter worth more with this plan?" (perk loadouts)
- "Which income buildings are worth it at all?"

So the core loop is: pick a day, add or move a few things, look at the gold curve, compare, undo, try again. Every
step of that loop should be one or two inputs, and the gold curve should be on screen while it happens.

## Why the old screens failed

The old planner was a settings-style list: one row per plan item (two lines of text each), a stepper to move it a
day, and a modal frame for everything else (Add to plan, Plan item, Plans, Perks, Income sources).

- **No sense of time.** The plan was a flat list; days were a value at the right edge of each row. Seeing "what
  happens on day 5" meant reading every row.
- **No sense of space.** Buildings were picked from a list of names like "House 7 · north-west, by the Barracks"; the
  only map was a small dot plot beside the list.
- **Modal hops.** Adding, editing, perks and plans each opened a frame over the tab, hiding the gold graph. Cause and
  effect were never on screen together.
- **Overloaded rows.** Each row carried title, cost, income, payback and status in prose.
- **Perks as a list of Yes/No toggles**, with no icons and no feedback until going back.

### Task flows, before

Presses are keyboard/controller inputs (a directional press or apply) from the Economy tab with the selection on the
tab; mouse is clicks (scrolling not counted). N is the number of items already in the plan (8 here). "Screens" counts
modal frames entered.

| Task | Presses | Clicks | Screens | Gold graph visible while doing it |
| --- | ---: | ---: | ---: | --- |
| A. Upgrade the mill (a choice) and build a gold mine, day 4 | ~42 | ~12 | 4 | no |
| B. Branch from day 5 and build two towers there | ~36 | ~10 | 1 | no |
| C. Move an item to another day | N+2 | 2 | 0 | yes |
| D. Mark a building lost on night 6 | ~26 | ~8 | 1 | no |
| E. Temple prayers on day 7 | ~27 | ~8 | 2 | no |
| F. Five houses on day 3 | ~21 | ~8 | 1 | no |
| G. Try two perk loadouts | ~14 + back and forth | ~8 | 1 per look | no |
| H. Which income buildings are worth it | N+9 | 1 | 1 | no |

## The new design

One screen, the **board**: days left to right, the plan's actions as icon chips in their day's column, and the gold
curve directly underneath on the same columns. Everything that changes the plan happens on the board, in a small
popup over it (the board stays visible and updates live), or in a day's view, which has its own map and its own
preview of the curve. (Round 3 changed the Add screen into the day view and moved the planner out of Run Analytics'
tabs; see "Feedback round 3" below, which supersedes the sketches here where they differ.)

### Board (Pause > Run Analytics)

The planner is the Run Analytics frame's only page, full width; the run's score and projection are under More.

```
 Run Analytics
 Score  -Economy-  Projection
 [◆ Plan 1 › towers]  [vs Plan 1]  [Perks · 2 changed]  [Income sources]  [Undo]      End 478◉  +29 vs Plan 1
 ┌──────┬────────┬────────┬────────┬────────┬────────┬────────┬────────┬────────┬────────┬───┐
 │      │ Day 3  │ Day 4  │┊Day 5  │ Day 6  │ Day 7  │ Day 8  │ Day 9  │ Day 10 │ Day 11 │ ▸ │  header: day, gold at
 │      │ 38◉    │ 52◉ -6 │┊71◉ +4 │ 90◉    │ 96◉    │ ...    │        │        │        │   │  sunrise, change vs compare
 │ Day  │[⌂House×5]│[⚒Mill II]│┊[♜Tower×2]│      │[✝Temple]│        │        │        │        │   │
 │      │        │[⛏Mine] │┊       │        │        │        │        │        │        │   │  chips: icon, short name,
 │      │  + add │  + add │┊ + add │  + add │  + add │  + add │  + add │  + add │  + add │   │  level, count
 │ ───  │ ────── │ ────── │┊────── │ ────── │ ────── │ ────── │ ────── │ ────── │ ────── │   │
 │Night │        │        │┊       │[✕House×3]│      │        │        │        │        │   │  night: lost buildings,
 │      │  + lost│  + lost│┊ + lost│  + lost│  + lost│  ...   │        │        │        │   │  gold found
 ├──────┼────────┴────────┴────────┴────────┴────────┴────────┴────────┴────────┴────────┴───┤
 │ 200  │           ·──────·──────·──────·──────·─────·                                      │  gold at sunrise (bright),
 │ 100  │    ·─────·                                         compare line (dim)              │  after building (bronze),
 │   0  │ ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔ │  red below zero
 └──────┴────────────────────────────────────────────────────────────────────────────────────┘
 Mill to level 2: Improved Plow · 8◉ · +1 per dawn · pays for itself by dawn 9
 Enter: edit · ,/.: move a day · Backspace: remove · Z: undo · drag to another day · right-click: remove
```

- **Columns** are day N (top, sunlit) and night N (bottom, darker). A day's builds sit above the night line; buildings
  lost and gold found that night below it. With a night in progress, a narrow "Tonight" column comes first.
- **Chips** show the game's icon (the choice's own tile icon for a choice upgrade; for a plain build, the game's
  icon that pictures that building, borrowed from a perk or choice, since the game has no per-building icons), a short name, the level as a numeral, and "×5" when several of the same thing happen that day:
  identical steps on a day are one chip (bulk houses don't flood the column). Colour carries status: red outline =
  not enough gold that day, dimmed = skipped or already done, faint with a "›" = inherited from the plan this branch
  comes from.
- **The curve** sits on the same columns, so the effect of a chip is right under it. The selected column is lit in
  the graph too. The column header says the gold at sunrise and the change against what's compared.
- **Branches** show as a verdigris line before their fork day.
- **The info line** at the bottom describes what's selected in one sentence (cost, income, payback, why it's red)
  and the inputs that act on it. It replaces the per-row prose.
- **Long levels**: columns keep a readable minimum width; when there are more days than fit, the board scrolls
  sideways with the selection (or the wheel), with "◂ 3"/"3 ▸" markers for the hidden days. Columns taller than the
  board scroll up and down the same way. Identical steps grouping keeps 40+ actions to a few chips per day.

### Add screen (on top of the board; replaced by the day view in round 3)

Opened from a column's "+ add" (day) or "+ lost" (night). It's the one place with a large map.

```
 Add to day 5                                        ◂ Day 5 ▸     [Build]  [Lost on night 5]  [Gold found]
 ┌──────────────────────────── map: the level from above ─────────────────────┐ ┌─ Buildings on day 5 ──────────┐
 │                                                                            │ │ [⌂] House   ×6 free  2◉ +1    │
 │         ◆ ◆              ◇                                                 │ │ [⛏] Mine    ×1       6◉ +4    │
 │      ◆(✓)    ◇  castle  ◆                                                  │ │ [♜] Tower   ×9      3◉        │
 │            ◆(✓)        ◇                                                   │ │  ...  (tiles: apply = +1)     │
 │   ◇ built (upgrade)   ◆ can build   · not yet unlocked   ✓ picked          │ ├─ Picked: 3 ───────────────────┤
 │                                                                            │ │ House 3 · north    build  2◉  │
 └────────────────────────────────────────────────────────────────────────────┘ │ Mill 1 → 2   ◂ Improved Plow ▸│
 ┌ gold curve: the plan, and the plan with what's picked ─────────────────────┐ │ 10◉ · end gold 478 → 503 (+25)│
 └────────────────────────────────────────────────────────────────────────────┘ │ [Add 3 to day 5]   [Clear]    │
                                                                                └───────────────────────────────┘
```

- **The map is a picture of the level from straight above** (rendered when the screen opens), turned the way the
  player's camera looks, with a marker on every build slot. Markers that can take a step that day are bright, built
  ones show their level, locked ones are faint. Clicking a marker picks it (pick as many as you like); clicking again
  drops it. Hovering or selecting shows what it is and its next step.
- **Building tiles** list each kind that can be built or upgraded that day with how many are free, the cost and what
  it pays per dawn. Applying a tile picks the next free one nearest the castle (apply five times for five houses);
  the tile shows how many are picked.
- **The picked list** shows each pick; a choice upgrade gets a left/right stepper through its choices (default: the
  first one not locked), an upgrade a level stepper.
- **The preview** under the map is the gold curve with and without the picks, and the end-gold change is on the Add
  button's line before anything is committed.
- **Lost on night N** mode: the markers are the buildings standing that night; picking marks them lost. A "Every
  building" tile marks them all (for Temple wisdom).
- **Gold found** mode: a number field and an Add button.

### Popups (over the board, which stays visible and updates live)

- **Chip** (apply on a chip): Day (◂ ▸ moves it), How many (for a group: + builds the next free one of that kind, −
  drops the last), Level, Choice, Wisdom gold (number), Earlier/Later that day, Branch from this day, Remove.
- **Day header**: Build on day N…, Lost on night N…, Gold found on night N, Branch from day N, Clear day N.
- **Plan** (toolbar): every plan and branch with its end gold (apply shows it), New, Copy, Rename, Clear, Delete.
- **Compare** (toolbar): doing nothing, or any other plan (the graph's dim line and the headers' deltas).

### Perks screen (on top of the board)

```
 Perks for Plan 1                                                 End 478◉ → 512◉ with these perks (+34)
 ┌ the game's perk tiles, in the loadout screen's order ──────────────────────────────────────────────────┐
 │ [◉] [◉] [◉]+ [◉] [◉] [◉] [◉] [◉] [◉] [◉]      lit = on in this plan; ◆ = differs from your run       │
 │ [░] [░] [░] [░] [◉]- [░] ...                    faint = doesn't change gold (shown, not toggleable)  │
 │ each gold perk shows what toggling it would do to the end gold: "+34", "-12"                          │
 └────────────────────────────────────────────────────────────────────────────────────────────────────────┘
 Interest — 1 gold at dusk per 3 you hold, up to 20.    On in this plan, off in your run.
 [gold curve: this loadout vs your run's loadout]
 [Back to your loadout]  [Try in a branch]
```

### Income sources

Kept as its own screen (it's a comparison, not an edit), opened from the toolbar.

### Inputs

| | Keyboard | Controller | Mouse |
| --- | --- | --- | --- |
| Move around the board | arrows | d-pad / stick | hover |
| Edit a chip / day | Enter (popup) | A | click |
| Move a chip a day | , / . (rebindable) | LB / RB | drag to another column |
| Remove a chip | Backspace (rebindable) | popup › Remove | right-click |
| Undo | Z (rebindable) | toolbar › Undo | toolbar › Undo |
| Add on a day | Enter on "+ add" | A on "+ add" | click "+ add" |
| Map: pick a slot | arrows between markers, Enter | stick, A | click |
| Map: one more of a kind | Enter on its tile | A on its tile | click its tile |
| Choice / level in the picked list | left/right | left/right | arrows beside the value |
| Back | Esc | B | Back |

Every change is undoable (Undo), so right-click remove and drag are safe.

### Task flows, after

| Task | Presses | Clicks | Screens | Gold graph visible |
| --- | ---: | ---: | ---: | --- |
| A. Mill (choice) + gold mine, day 4 | ~10 | 5 | 1 (Add, with its own preview) | yes |
| B. Branch from day 5, two towers | ~11 | 6 | 1 | yes |
| C. Move an item to another day | ~3 (go there, Q/E) | 1 drag | 0 | yes |
| D. Building lost on night 6 | ~8 | 3 | 1 | yes |
| E. Temple prayers on day 7 | ~9 | 4 | 1 | yes |
| F. Five houses on day 3 | ~9 | 7 | 1 | yes |
| G. Try two perk loadouts | ~4 (+1 per toggle) | 1 + 1 per toggle | 1 | yes, plus a per-perk delta |
| H. Income sources | ~4 | 1 | 1 | n/a |

## Components

Reusable, in Core/UI (documented in CLAUDE.md):

- **ModFrame wide tabs / pages**: `AddTab(label, build, wide: true)` / `SetContent(build, wide: true)` gives the page
  the full width (no aside); `page.Size` is its area; `page.Wired` runs after the frame has wired navigation, so a
  page with its own 2D layout can connect to the tabs and Back.
- **ModTile**: a selectable rounded tile in the game's panel look (the loadout screen's tiles): icon, text, a corner
  badge, states (on, dim, warning, accent marker), focus animation, optional drag and right-click.
- **ModNav**: wires navigation for grids (rows), boards (columns) and free layouts (nearest element in a direction).
- **ModPopup**: a small menu over the current screen, which stays visible; built from ModPage pieces (so Number and
  Stepper work in it); Esc/B or a click outside closes it.
- **ModMap** + **LevelCapture**: a picture of the level from above with selectable markers at world positions.

Feature-specific, in Features/Economy: the board (PlannerBoard), the gold strip aligned with its columns, the Add
screen, the perk grid, the chip/day/plan popups, building icons (PlannerIcons), undo.

## Feedback round 1 (as built)

What changed after the first play test, and why.

- **Clicks landing on the wrong thing.** Three causes, all fixed:
  - The board scrolled to whatever was *selected*, and the game selects what the mouse hovers. Hovering a chip in a
    half-hidden column slid the board under a still pointer, so the next frame hovered (and a click hit) whatever slid
    there. The board now scrolls only for keyboard and controller selection, never for mouse hover.
  - A popup closed on the mouse *press* outside it; the board became the active menu in that same frame and took the
    press as its own click (a chip, a tab, or Back, which left Run Analytics: "took me back to the main menu"). Popups
    now close when the button comes back up, which nothing below treats as a click.
  - Map markers of slots close together (a mill's fields, houses) overlap, and the game's menus take the topmost one
    under the pointer, not the one aimed at. Each marker now only takes the pointer where its centre is the nearest
    (`ModMap` adds an `ICanvasRaycastFilter`; `ModMap.MarkerAt`).
- **The castle's build** on day 1 is part of every plan and of "doing nothing" while the castle isn't built: a fixed
  chip ("fixed" badge) that can't be moved or removed. Plans don't store it (`PlannerFile.Fixed`, prepended by
  `Effective`; older plans' copies are dropped).
- **Budget.** Each day's header says what the day has to spend and what's left after its builds ("28 · 5 left"; red
  "short" if it goes below zero). The Add screen's top bar says the same with the picks ("28 to spend · picks 5 ·
  23 left"). A marker or kind the gold left can't pay for is greyed with its cost in red and can't be picked; Add is
  refused if the day would be short. Moving a chip, adding to its count, raising its level or adding fields are
  refused (with a notice) if they'd leave *that* day short. A change that leaves a *later* day short (spending its gold
  earlier) is allowed and flagged: a red "Short on day N · fix" button in the toolbar opens a popup that moves each
  unpaid step to the first day that can pay for it, takes it out, or undoes the change. The graph no longer gives a
  red band to gold below zero.
- **The game's rules for what can be built** (`EconSim.Blocker`, also used by the simulation): unlocked (its activator
  at the level it needs, along the chain), not at its highest level, allowed in this mode, not busy, the castle high
  enough. Every slot has a marker; one that can't take a step that day is greyed and smaller, and hovering it says why
  ("Unlocks once Wall 1 is built", "Unlocks when Castle Center reaches level 2", "Needs the Castle Center at level 3").
  Availability is per day with the plan's earlier steps and the picks so far: plan Nordfels' Wall 1 on day 4 and House 7
  behind it can be picked from day 4 on (or right after picking the wall). Perks that change these rules are read at
  level load and can be tried in a plan: Timber Scaffolding (`PerkUnrestrictedUpgrading`) lifts the castle cap, and
  Architect's Council (`ReduceChildrensBuildRequirementIfPerk`) unlocks everything the castle unlocks one castle
  level sooner (both recorded by `PerkCapture`).
- **Mill fields** are first-class: picking a mill shows its fields as pickable markers straight away (they unlock with
  it), adds a "Field" kind tile (which picks fields of the picked mill first), and puts − / + field buttons on the mill's
  picked row ("Fields 2 · 0 more now, 3 later"). Hovering a mill says how many fields it has built. On the board, a mill
  chip's popup has a "Fields that day" number. Fields are the slots a building unlocks two or more of
  (`EconomyPlannerManager.Fields`).
- The toolbar's summary no longer runs off the edge (shorter buttons, the text shrinks to fit), and the board gives
  its chips the room before the graph, so a night with two buildings lost isn't cut off.

## As built

- Keys are registered through the Keybinds API under "Economy planner" (context "Economy planner", claimed while a
  planner screen is open), so they show in Settings > Controls and can be rebound; the shoulders (the game's
  NextTab/PrevTab) also move a chip, and change the day on the Add screen.
- The map picture is rendered once per Add screen (LevelCapture) and copied into a texture straight away.
- Dev commands drive every screen (`dev.py cmd help` lists them); round 3's are listed below.

## Feedback round 3 (as built)

- **One day view** (`PlannerDay.cs`). A day's header opens it straight away (no popup); "+ lost" opens it on the
  Lost layer. It edits the plan directly, every change undoable: a marker click takes that slot one step further that
  day, so a spot can take several steps in a day (castle 1 → 3, build a house and upgrade it), as the game allows
  (`BuildSlot.TryToBuildOrUpgradeAndPay` has no per-day limit; only a temple prayer, blacksmith/forge research and
  the castle's level stop a next step). When a slot can't go further, the next click takes its steps on that day off
  again; right-click (or Backspace) takes one off. Coming back to a day shows its steps lit on the map and listed with
  − / +, never everything greyed. The list row shows "I → III" and the cost; choice steps show the game's choice tiles,
  a mill its fields' − / +. Two layers on the same map: Build, and Lost that night (with a Temple wisdom prayer that
  day, a Wisdom row: buildings marked lost × the prayer's gold, or a typed amount). Top bar: ‹ Day N-1 / Day N+1 ›,
  the layers, the day's gold, Branch here, Clear day.
- **Markers**: can take a step: bright; unlocked but too dear: normal, with the cost in red; not unlocked (or out of
  reach, below): dark, smaller, with a padlock; planned that day: lit with the level it reaches. A legend sits under
  the map. "Nothing can be built…" is on a dark band over the map.
- **Quick add** (`PlannerQuick.cs`): a day's "+" on the board opens a popup with a tile per kind the day can take
  (icon, how many are free, cost; "House → II" for upgrades), the day's gold, and a link to the day view. Each press
  adds the nearest free one straight away (right-click takes the last off), the board updates underneath, and the
  popup stays open, so several adds take several clicks and no screens. Its tiles scroll.
- **Budget** everywhere is "left/at sunrise": "6/8◉" (red when short).
- **Compare** with any number of plans and doing nothing, each in its own colour (`PlannerVisuals.CompareColor`, a
  categorical set clear of the plan's cream line, copper and red; doing nothing is slate), in the board's graph and the
  day view's. The day headers' +/- are against the first. Files keep `Compares` (older files' `Compare` is read in).
- **One gold line** per plan. Settings > Mods > Pause menu > Economy planner > "Gold on a day" (also More > Gold on a
  day): at sunrise (default) or after that day's builds.
- **Perks**: every perk toggles, gold or not, to plan the loadout; "X/5" (the level's `maxPerkCount`) turns red over
  the limit; more are allowed. Perks that move gold show what toggling them does to the end gold.
- **Run analytics in More**: no tab bar; the planner is the Run Analytics frame, and More opens Score and Projection
  (their own frame).
- **Scroll areas** (`ModScrollArea`) instead of "and N more": the day view's kinds and steps, quick add's kinds.
- **Wisdom, not "gold found"**: the made-up "a chest" estimate is gone. Temple wisdom pays
  `goldForDestroyedBuilding` (1/2/3 for the temple's levels 1/2/3) per Building-tagged object destroyed that night
  (walls, towers, fields and the castle count; bridges and barricades don't). The planner counts the buildings marked
  lost; the player can type their own number. Old plans' "gold found" items still count and show as chips.
- **Reach** (`PlannerReach.cs`): slots a map keeps out of reach until a bridge is built (the game doesn't encode
  these as unlocks) are locked with "Out of reach until … is built" (any one set of bridges).
- **Destroyed buildings**, from the game's code and every level's data: a knocked-out building pays nothing at the next
  dawn. Indestructible Mines (the code's `sustainableMiningPerk`) resets a destroyed mine to 7 a dawn (it pays, then
  drops by 1, floor 1 with or without the perk); without the perk a destroyed mine's income is frozen that dawn.
  Resilient Residences (houses) and Relentless Research (Blacksmith, Royal Forge) keep a destroyed building paying.
  Challenge the God of Destruction keeps a destroyed building down at the next two dawns (it pays at neither, and a mine
  resets or freezes at both) unless it's upgraded on the day between; with Resilient Residences a house pays at both.
  A building still down can't be lost again (or drop wisdom gold) the second night. All of these are perk what-ifs too.
- **Esc**: one layer each. `ModFrame.Open` on a frame already under the active one closes back down to it instead of
  pushing a copy (a copy left on the stack made Esc come back to a closed screen). `ModPopup` keeps its place when
  what it was opened beside is rebuilt, and destroys itself if it's left neither on top nor on the stack. `ModTooltip`
  hides when what it described is destroyed. Income sources' day field no longer rebuilds the page (and no longer has
  a tooltip, which, being the screen's only row, was always selected and covered the side panel).
- Text: no instructions in labels or tooltips; the board's info line keeps only the chip keys nothing else shows.
- Dev: `econ day [day] [build|lost]`, `dayclick <slot#> [right]`, `daykind <kind> [n] [remove]`, `dayday <±n>`,
  `quick <day>`, `quickkind <kind> [remove]`, `moment sunrise|after`, `sourcesday <day>`, and `econ ui` (the active
  frame, the stack under it, popups and the tooltip, for checking Esc and leftovers).
