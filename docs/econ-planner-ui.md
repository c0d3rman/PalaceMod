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
popup over it (the board stays visible and updates live), or in the Add screen, which has its own map and its own
preview of the curve.

### Board (Run Analytics > Economy)

The Economy tab uses the frame's full width (a new "wide" tab mode).

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
 Enter: edit · Q/E: move a day · Del: remove · drag to move · right-click: remove
```

- **Columns** are day N (top, sunlit) and night N (bottom, darker). A day's builds sit above the night line; buildings
  lost and gold found that night below it. With a night in progress, a narrow "Tonight" column comes first.
- **Chips** show the game's icon (the choice's own tile icon for a choice upgrade, the matching game icon for a
  building type), a short name, the level as a numeral, and "×5" when several of the same thing happen that day:
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

### Add screen (on top of the board)

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
| Move a chip a day | Q / E (rebindable) | LB / RB | drag to another column |
| Remove a chip | Delete (rebindable) | popup › Remove | right-click |
| Undo | Ctrl+Z (rebindable) | toolbar › Undo | toolbar › Undo |
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
