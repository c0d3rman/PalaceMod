# PalaceMod agent guide

BepInEx 5 mod for Thronefall (Unity 2022.3, Mono), developed on an Apple Silicon Mac. All workflow commands go through `scripts/dev.py`.

## Dev loop

```sh
python3 scripts/dev.py doctor        # check the environment; fix anything marked !!
python3 scripts/dev.py setup         # once per checkout/worktree: restores tools, copies game DLLs into lib/
python3 scripts/dev.py run           # build -> stop game -> install -> launch -> wait for BepInEx -> print errors
python3 scripts/dev.py logs --errors # unique errors with stack traces from the current game session
python3 scripts/dev.py logs --feature Savestates --level warn  # one feature's lines at a level or worse
python3 scripts/dev.py screenshot    # in-game frame capture (works while unfocused); prints the PNG path
python3 scripts/dev.py stop          # quit your game and release your pool instance (or the --real lease)
python3 scripts/dev.py cmd "<command>"  # drive a dev launch: load levels, start nights, fake input, open/navigate menus
python3 scripts/dev.py config get|set <Section>.<Key> [value]  # read/edit PalaceMod.cfg; the game reloads it live
python3 scripts/dev.py land --body-file pr.md  # merge origin/main, push, open and merge the PR (see Landing)
```

Dev launches are windowed, muted (`--sound` to keep audio) and hand focus back to the app that was in front (`--focus` lets the game keep it). Don't launch `--fullscreen` unless you need to: it switches the user to a new Space.

A feature is not done until `run` reports `PalaceMod loaded: yes` and no PalaceMod errors. (`run` looks for the line `PalaceMod startup complete v<version>`, which `Plugin.Awake` logs once every manager has started; don't change its text.) The one expected error is `HookGenPatcher ... Could not load file or assembly 'MonoMod'`, which comes from a BepInEx patcher and is harmless.

Always finish with `dev.py stop`, so the game isn't left running and others can use the instance.

## The game pool

Agents test on a pool of cloned game installs ("instances", 6 by default), so several can run the game at once.

- `run` (also `launch`, `install`, `lease acquire`) takes a free instance and you keep it until `stop` (or `lease release`). `cmd`, `screenshot`, `config` and `logs` then go to your instance. Nothing else changes in how you call dev.py.
- If every instance is busy, `run` waits in a FIFO queue and prints your position. Run it in a background shell so you're woken when it finishes. `--wait SECONDS` caps the wait (default 1 h, `0` = don't wait); if none frees up in time, `run` exits with code 75. `lease status` shows every instance's holder, the queue and idle times.
- Hold an instance briefly: batch your tests, then `stop`; don't hold it while you write code. Every dev.py call refreshes your lease, and so does every `agent-kit/palace.py` command sent to your instance's bridge (it touches `bridge/last-request`). After 10 min idle it expires and the next dev.py call from any checkout stops your game and frees it.
- An instance is fully isolated: its own executable (`ThronefallDev<N>`), BepInEx folder (DLL, `PalaceMod.cfg`, log, command channel), preferences domain and save folder, seeded from a copy of the player's saves and preferences when you take it. Each `run` starts the mod from default settings; use `--set "Section.Key=value"` (repeatable) for other values, or `--keep-config`. Edit settings with `dev.py config set`.
- Instances are rebuilt automatically when the game or its loader changes. `dev.py instances create [COUNT] | list | reset [N|all] | remove [N|all]` manages them by hand.
- `--real` uses the real install instead, behind its own single lease (which also snapshots and restores the player's `BepInEx/config`). Use `--solo` (implies `--real`) for frame-time or other performance measurements: it waits until every pool instance is released and holds off new pool leases until you `stop`.

## Landing

Branch from `origin/main`; that's the base for everything. When your feature is done and verified, commit, write the PR description to a file (end it with your attribution footer lines), and run:

```sh
python3 scripts/dev.py land --body-file /path/to/pr-body.md [--title "Short title"]
```

`land` refuses if you have uncommitted changes. Under a global lock, it fetches, merges `origin/main` into your branch, builds, pushes, opens a PR against `main` (or reuses the open one), checks that it's mergeable, merges it (`gh pr merge --merge`), fast-forwards the main checkout, and prints the PR URL and merge commit. Agents merge their own PRs this way. If the merge conflicts or the build fails, it undoes its merge and stops; run `git merge origin/main` yourself, fix it, commit and land again. `--dry-run` stops after the build. `gh` is authenticated as c0d3rman; never run `gh auth switch`.

## How the Mac setup works

- `libdoorstop.dylib` is x86\_64-only, so the game has to run under Rosetta. `launch` does this itself, and `setup` patches the game's `run_bepinex.sh` so Steam launches use Rosetta too.
- `dev.py native on` (experimental) switches to Doorstop 4.6's universal dylib plus `PalaceMod.Bootstrap`, which makes MonoMod work under Apple Silicon W^X so the game runs natively; `native off` goes back to Rosetta. The bootstrap writes its status to `BepInEx/PalaceMod.Bootstrap.log`.
- `setup` also adds GC tuning to `run_bepinex.sh` (`GC_FREE_SPACE_DIVISOR`, `GC_INITIAL_HEAP_SIZE`) and per-collection timings in `BepInEx/gc-stats.log`. `PerformanceManager` logs frame spikes and a frame-time summary to the BepInEx log at Debug level (`dev.py cmd "log debug Performance"`).
- Steam must be running. `launch` starts it if it isn't.
- Dev launches set `PALACEMOD_DEV=1`, which turns on `Core/DevToolsManager.cs`: the game keeps running in the background and screenshots are taken on request. Dev launches are windowed unless you pass `--fullscreen`.
- Every `--real` launch first backs up save files and preferences to `~/Documents/Misc/Thronefall Backup/PalaceMod dev/backups/saves/`, keeping the last 30.

## Verifying in-game behavior

Drive the game yourself instead of asking the user to click through it. `dev.py cmd` sends commands to the mod and prints the result. `dev.py cmd help` lists them. For example:

```sh
python3 scripts/dev.py cmd scenes            # list scenes
python3 scripts/dev.py cmd "level Nordfels"  # load a level
python3 scripts/dev.py cmd night             # start the next wave now
python3 scripts/dev.py cmd "circle 30"       # run the king in circles for 30 s (camera motion)
python3 scripts/dev.py cmd settings          # open Settings on the Mods tab
python3 scripts/dev.py cmd "ui down"         # navigate the open menu like a controller (up/down/left/right/apply/close)
```

Then check the result with `dev.py logs` (see Logging) and `dev.py screenshot`. With Performance debug logging on (`dev.py cmd "log debug Performance"`), `PerformanceManager` logs a frame-time summary (fps, percentiles, CPU work per frame, frame-rate cap and reason) every "Summary interval"; settings edited in `BepInEx/config/PalaceMod.cfg` are reloaded live within 2 s. Only ask the user to test what genuinely needs a human, like how motion looks.

## Agent kit (players' agents)

`agent-kit/` is shipped inside the DLL and written to `BepInEx/PalaceMod/Agent/` when the player turns on Settings > Mods > Builder > "Agent bridge" (`Features/Builder/Agent/`): a level-building guide, a Claude Code skill, generated `reference.md`, and `palace.py`, a stdlib CLI that edits packs with JSON ops and drives the game through the bridge (the same commands as `dev.py cmd`, through a file channel of its own). To try it on a pool instance: `dev.py run --set "Agent bridge.Enabled=true"`, then `python3 agent-kit/palace.py --game "<instance>/game" ...` (`dev.py instances list` gives the folder; never point it at the real install). When the Builder's data model or the `agent` command changes, update `palace.py` and `level-building.md` with it, and regenerate `reference.md` (`palace.py ref build --force`, then `ref doc -o agent-kit/reference.md`) after a game update.

## Adding a feature

- A feature is one `BaseManager` subclass in its own file under `src/PalaceMod/Features/<Area>/`. It's found automatically; never edit `Plugin.cs`. Harmony patch classes go nested inside the manager, and only those are applied. Managers in `Experimental/` (namespace `PalaceMod.Experimental`) are never loaded.
- Override `FeatureSection` (its settings section, e.g. `"Formations"`) and `FeatureDescription`. That adds a `<Section>.Enabled` switch as the first row of its page in Settings > Mods. While the feature is disabled, `Update` isn't called and its patches are removed. `Awake` and `Start` always run. Undo anything visible in `OnFeatureDisabled()`; the hooks fire live when the setting changes. To keep patches applied, override `UnpatchWhenDisabled => false` and early-out with `if (!Feature<MyManager>.On) return;`. Use `Feature<T>.Instance` to reach a manager from static code. Infrastructure managers leave `FeatureSection` null. A feature that is only a menu the player opens on purpose (Compendium, Debug, Savestates, Profiles, Builder) overrides `HasEnabledSwitch => false`: no Enabled switch, its entry is always there (an `AffectsRanking` one is still hidden by the master switch via `IsEnabled`). Passive parts of such a feature get their own settings, named for what they do; `EnabledLabel` relabels a switch that only covers the passive part (Run analytics: "Record runs").
- Settings: bind `ConfigEntry`s in the feature's own section in `Awake`. Give every entry a real description, shown under the rows when it's selected or hovered; `FeatureDescription` is shown at the top of the section's page. Use bool, enum or `AcceptableValueList`, or `AcceptableValueRange` for sliders. Add a `SettingTags` for units, a label for 0, a slider step, or `Hidden`. Settings > Mods lists pages on the left, grouped by area (`Layout` in `Features/UI/SettingsPaneManager.cs`; add a new section there, either as its own page or on a related page, where its Enabled row is labelled with the feature's name and its settings are indented under it). Pages are display-only, so regrouping never moves a saved value. A page scrolls, but keep a feature to about 7 settings. Every row gets a "Default" button while it differs from its default, and every page a "Reset to defaults" (its keys too). For an action instead of a setting, use `SettingsPane.AddButton(section, label, description, onClick)`, never a bool that unticks itself; `SettingsPane.DescribeSection` describes a section that has no feature.
- Renaming a setting or section: a rename changes the config key, so the saved value would reset to the default. Add the old and new name to `Core/ConfigMigrations.cs` (`Sections` or `Keys`; `Defaults` for a changed default). It runs at startup before anything binds and moves the saved value to the new name, keybinds included.
- Keys: `Keybinds.Register(FeatureSection, "id", "Display name", new KeyboardShortcut(KeyCode.X), "What it does")` returns a `Keybind`; poll `.Down`, `.Held` or `.Up`. These are inert while the feature is disabled, a menu is open (unless `KeybindFlags.AllowInMenus`), or a text field has focus. Never read raw `Input.GetKey` for a feature key. A key that only acts on one screen or in one mode passes `context:` (e.g. `KeybindContext.LoadoutScreen`, or any string); the Controls screen then only flags conflicts between keys that can act at the same time. If the feature knows when that screen is up, `Keybinds.ClaimContext(context, () => onScreen)` makes the context's keys exclusive meanwhile: game or mod actions on the same input (e.g. Zoom on Z) are blocked. A mode that owns the whole keyboard (the Builder Studio) uses `Keybinds.ClaimExclusive(context, () => active)` instead: meanwhile every keybind outside the context is inert as well.
- Messages to the player: `Notify.Show(text, kind, seconds, key)`. A message with the same `key` replaces the previous one. It shows as a small panel in the game's style at the top centre; `kind` only tints its emblem.
- Menus, pause-menu buttons and tooltips: use the UI kit (below), never `new GameObject` + `AddComponent` UI or GameObject-path lookups.
- If the feature changes gameplay (cheats, undo, save states, beyond-vanilla options), call `GameplayImpact.Mark("Reason")` when the player uses it. It resets on a fresh run. Also override `AffectsRanking => true`: the master switch Settings > Mods > General > "Gameplay-changing features" then holds the feature off (as if disabled, with the same hooks, its own Enabled value untouched). If only part of a feature marks, gate that part with `GameplayFeatures.Allowed`, call `GameplayFeatures.RegisterPart("Feature: part")` in `Awake`, and tag its setting `new SettingTags { AffectsRanking = true }`. Dev-only commands don't count.
- Test hooks: `DevCommands.Register("name", "name <args>   what it does", args => "result")` in `Awake`. Throw to report a failure. `dev.py cmd features` shows each manager's enabled state and patch count, and `dev.py cmd impact` shows the run's gameplay impact.
- Logging: `Log.Info(...)` in a manager, `Log.For<MyManager>().Info(...)` elsewhere; see Logging below. Never `Plugin.Logger` directly.

## UI kit (src/PalaceMod/Core/UI)

Everything is cloned from the game's own widgets (found through its UI scripts' fields, not GameObject paths) and carries PalaceMod's mark: a small verdigris gem (the emblem from the logo), and a thin verdigris edge on panels (`ModStyle.Accent`, `ModStyle.AddEmblem`, `ModStyle.MarkPanel`). The logo, palette and textures live in `branding/` (see its README; regenerate with `python3 branding/build.py`). Take the game's palette and fonts from `ModStyle` for anything custom.

```csharp
// In Awake. The frame is built on first open; a tab's page is rebuilt each time it's shown.
var frame = new ModFrame("Compendium")
    .AddTab("Enemies", page =>
    {
        page.Header("Night 3");
        page.Row("Hit points", "120", tooltip: "Base 100, +20% night difficulty");
        page.Text("Wrapped body text.");
        page.Button("Reset", () => ...);
    });
PauseMenu.AddButton("compendium", "Compendium", frame.Open, order: 10, visible: () => IsEnabled);
ModTooltip.Attach(someGameButton, "Title", () => "Body");   // shown while selected or hovered
```

- `PauseMenu.AddButton` is idempotent per id and safe to call before the UI exists. Buttons go under Settings, sorted by `order`; past 3 they move into a "More" entry that opens a "Mods" frame. `visible` is re-checked on every open, so gate it on your feature's `IsEnabled`.
- `ModFrame` runs on the game's `UIFrameManager`: Escape or back returns to the menu it was opened from, the game stays paused, and keyboard, controller and mouse navigation work. Opening a frame that's already open under the active one closes back down to it (never a second copy on the stack). `Refresh()` rebuilds the current tab; `ModRow.SetValue` updates one row in place.
- Page pieces: `Header`, `Text`, `Row` (label, value, tooltip, action, icon), `Button`, `Stepper` (left/right or the clickable arrows), `Custom(name, height)` for your own drawing, and `AddElement` to put your own selectable in the up/down chain. `ModFrame.Activated` fires on open and on return from a menu opened on top. Fields (`Core/UI/ModFields.cs`): `Choice`, `Toggle`, `TextInput`, `Stepper`.
- Numbers: always `page.Number(label, get, set, min, max, step = 1, format = null, tooltip = null, onClear = null)` (int or float overload, `Core/UI/ModNumber.cs`), never a stepper that counts by one. Left/right (or the arrows) step and speed up while held, Shift or the controller's shoulders step x10, Home/End jump to min/max, and apply types a value (Enter commits, Esc cancels; clamped with a notice, junk refused, a unit the field shows is allowed, `10k` works). `set` only gets changed, clamped values and may rebuild the page; `onClear` handles an empty entry (e.g. "back to the default"); `format` must format the value it's given. Settings sliders (`AcceptableValueRange`) get the same speed-up, Shift, Home/End and typing automatically.
- `ModTooltip.Show/Hide/Attach/AttachHover` share one tooltip in the game's tooltip look.
- Wide pages, for screens that aren't a list (boards, maps, grids): `AddTab(label, build, wide: true)` / `SetContent(build, wide: true)` give the page the frame's full width and no aside; `page.Size` is the room it has without scrolling (lay your own things out in `page.Custom("…", page.Size.y)`), `page.Frame` the ModFrame, and `page.Wired += (above, below) => …` runs after the frame has wired its up/down chain (above: the current tab, below: Back) so hand-wired elements can join it. Example: the economy planner (`Features/Economy/PlannerBoard.cs`), which re-renders itself in place and keeps its selection by key instead of calling `Refresh`.
- `ModTile` (`Core/UI/ModTile.cs`): a selectable rounded tile in the game's panel look (the loadout screen's and upgrade popup's tiles): icon, label, corner badge, `SetOn` (lit), `SetDim`, `SetWarning` (red edge), `SetMarker` (the verdigris gem), `SetFill`, `SetCornerIcon` (e.g. a padlock). Events: `Applied` (Enter, A, click), `Shown` (selected or hovered), `RightClicked`; with `Draggable`, `Dragging`/`Dropped` get the pointer's screen position (a press without moving is a click). `Press()` runs it from a dev command. Place it with `Place(topLeft)` or a layout group; `Key` is yours (e.g. to reselect it after a rebuild).
- `ModNav` (`Core/UI/ModNav.cs`) wires keyboard/controller navigation for what the page's up/down list doesn't cover: `Grid(rows, above, below, left, right)`, `Columns(columns, above, below)` (boards), `Spatial(elements, position, edges)` (free layouts: the nearest element in a direction).
- `ModPopup.Open(title, page => …, near: rect)` (`Core/UI/ModPopup.cs`): a small menu over the current screen, which stays visible and keeps updating underneath; built from ModPage pieces (Row, Button, Stepper, Number, TextInput all work). Esc/B or a click outside closes it (on the release, so the screen below doesn't take the click too); `Refresh()` rebuilds it keeping the selected row (and its place, even if what it was opened beside was rebuilt); `ModPopup.Current` is the open one. Close it before opening another frame from it; one left neither on top nor on the frame stack destroys itself.
- `ModScrollArea.Create(parent, size, uiFrame)` (`Core/UI/ModScrollArea.cs`): a scrolling box for hand-placed content (lists, tile grids in wide pages and popups); `SetContentHeight`, `ScrollTo`. Use it instead of cutting a list short with "and N more".
- `ModMap.Create(parent, size, worldPoints)` (`Core/UI/ModMap.cs`): a picture of the level from straight above (`LevelCapture`, turned like the player's camera, released with the map) framing the points, with `AddMarker(key, world, size, icon)` (a ModTile on the spot; overlapping markers each take the pointer only where their centre is nearest, `MarkerAt(screen)`), `AddDot(world, size, color)`, `ClearMarkers()` (redraw them without taking the picture again), `Overlay` (a layer for notes and legends) and `Wire(edges)` (arrows move between markers). `LevelCapture.Take(center, halfWidth, halfHeight, pixels)` renders such a picture on its own; `LevelCapture.Orientation` gives the screen's right and "north" on the ground.
- `ModDock` (`Core/UI/ModDock.cs`): a docked, non-modal UI over the running game (the Builder Studio's panels). It's
  a persistent `UIFrame` that doesn't stop time and can't be escaped: its owner reads Esc. `AddPanel(name, anchorMin,
  anchorMax, offsetMin, offsetMax)` places a panel in the game's look against the screen edges (`ModDock.Gutter` =
  24 px). `panel.Fill(page => …)` builds it as a scrolling `ModPage` that keeps its scroll position, or you can draw
  into `panel.Body`. Call `Show()` every frame; it activates the dock only when no other menu is up. `Over(screen)` /
  `PointerOverPanel` tell world input when the pointer is on the UI. ModPopups open over the dock. While the dock is
  on top, keyboard and controller navigation are off and only the mouse acts on it, so movement keys stay free. For a
  panel over live play, set `FreezePlayer = false` and use `ShowPassive()` plus `Activate()`/`Release()` while the
  pointer is on it. On a narrow page, `ModPage.FieldIndent` sets how much room a field's label keeps.
- `GameWidgets.Clone/Button/Text` are the low-level cloners, for widgets the kit doesn't wrap.
- Dev: `dev.py cmd "uidump <pause|active|hud|tooltip|name> [depth]"` prints a live UI hierarchy with fonts, colours and sprites; `dev.py cmd "uikit frame|notify|buttons <n>|tooltip|numbers"` exercises the kit; `uikit step <left|right> [n] [shift]`, `uikit numkey <min|max|next|prev>` and `uikit type <text|enter|esc|...>` drive the selected number field (or settings slider) and the field being typed into; `uikit mouse <object>|<x> <y>`, `uikit click` and `uikit scroll <delta> [frames]` drive a simulated mouse the game itself sees (hover, click and wheel through its normal input path).
- Scrolling: use `ScrollInput.Pixels` (wheel notches and trackpad swipes evened out, scaled by General > "Scroll speed"), never raw `Input.mouseScrollDelta`. The game's own `ScrollArea` lists (Settings > Controls) are patched to scroll the same way.

## Savestates API (Features/SaveStates)

`SaveStates` (static) captures and restores whole mid-level moments; build "retry this night"-style features on it instead of touching the game's match save.

```csharp
SaveStates.List(level)                  // SaveStateInfo list, newest first (level defaults to SaveStates.CurrentLevel)
SaveStates.Latest(level, SaveStateKind.AutoDusk)   // e.g. right before the last night started
SaveStates.CanCapture(out reason); SaveStates.Capture(name, kind)   // throws InvalidOperationException(reason)
SaveStates.CanRestore(state, out reason); SaveStates.Restore(state) // async: ~1 s scene reload, then Restored fires
SaveStates.Captured / AutoCaptured / Restoring / Restored / Changed  // events
SaveStates.Last, SaveStates.IsRestoring, Delete, Rename, Overwrite
```

- Mod state: a feature with in-level state the game's save doesn't hold (teleporters, patrols, a latch...) registers it in `Awake` so savestates keep it: `SaveStates.RegisterState("id", () => new MyState { ... } /* or null */, saved => ...)`. The state class needs public fields (use `V3` for vectors, `SaveStates.PathOf(component)` for scene objects, found again after a load with `SaveStates.FindByPath<T>()`). Restore runs after the level is rebuilt and before `Restored`, and isn't called for older states without that id. Already registered: teleporters, build-preview latch, patrols, run analytics, gameplay-impact reasons (control groups ride on the units).
- Kinds: `Manual`, `Quick` (one slot, overwritten), `AutoDawn` (1 s after sunrise), `AutoDusk` (in `SwitchToNight`, still daytime), `BeforeLoad` (undo state taken before every load), `NightStart` (Retry night's one slot, taken like `AutoDusk` when automatic dusk states are off; not in the menu). `Wave` is `EnemySpawner.Wavenumber` at capture (-1 on day 1, n during night n+1 and the day after it); `NightNumber`/`Moment` are the player-facing versions.
- Files: `BepInEx/PalaceMod/SaveStates/<level>/<id>.json` + `.png`, never the game's save folder. Restore only works into the same level, Classic mode, not the tutorial, while `InMatch`.
- How it works: capture runs every `SaveLoadEntity.OnSave` into a scratch `MatchSave` (the run's real one is swapped back untouched) and adds what that format misses (night progress and spawn timers, enemies, units, king, ground coins, per-day stats, `Random.state`). Restore injects that `MatchSave` in memory, restarts the level like the game's retry-day, then applies the rest; afterwards the run's in-memory `MatchSave` is the captured run's last dawn, so native retry-day still means "this run's last dawn".
- Every load calls `GameplayImpact.Mark("Savestate loaded")`; with "Protect real save" on, the game's `MatchSaveLoadHandler.SaveRun` is skipped for the rest of that run (until `GameplayImpact.Reset`).
- Not restored: projectiles and effects in flight, enemy/unit AI targets and attack timers, units following the king (they stay where they were), buffs/slows, player units the level spawns at runtime (clones), level-scripted events beyond what the game's own save keeps.
- Dev: `dev.py cmd "savestate save [name] | quick | load <id|name|quick|last> | list | dump | delete <id|name> | menu"`; `dump` prints a one-line summary of the live state (also logged on every capture and load) for comparing.
- Retry popup (pause > Restart, Try Again): `RetryPopup.AddButton(id, label, order, visible, apply, tooltip)` adds a row under the game's "Retry Day" (Retry Night, then Earlier Day); call `RetryPopup.Sync` from the feature's own `UIFrame.Activate` prefix. Retry Night (`RetryNightManager`, `dev.py cmd "retrynight status|load"`) loads with `SaveStates.RestoreAsRetry`: no practice-run save protection, and `MatchSave.hadRestarts` set like the game's retry.

## Logging (Core/Log.cs)

Every line is written as `[Feature] message`, where Feature is the manager's `FeatureSection` (or the type name minus "Manager"), so `dev.py logs --feature <Name>` (repeatable or comma-separated) and `--level error|warn|info|debug` can filter it.

```csharp
Log.Info($"Loaded {state}");                           // in a manager (BaseManager.Log)
Log.For<SaveStatesManager>().Warn($"Skipping {file}: {e.Message}");   // static code, helper classes
Log.Error("Couldn't write the pack", e);               // full stack + scene/day/wave context, once
log.Caught("Click handler failed", e);                 // a callback you called threw: names the feature it came from
```

- Levels: **Error** = a bug, something the player wanted didn't happen (pass the exception). **Warn** = unexpected but handled (bad file, missing prefab). **Info** = what the feature did, at most about once per player action or night. **Debug** = detail for chasing a bug: per-frame or per-unit chatter, layout/template dumps, timings.
- Debug and Info take interpolated strings that aren't formatted when the level is off, so they're free in hot paths. That also means the `{...}` holes aren't evaluated: never put a call with side effects in one. Identical messages within 30 s collapse into one line plus a count; an exception's stack is written once per session, then only counted.
- Settings (Settings > Mods > Diagnostics): "Log level" (default Info), the "Create debug bundle" button; the hidden `Diagnostics."Debug logging for"` takes comma-separated feature names or `*`. All apply live. Dev: `dev.py cmd "log debug Savestates"`, `log level debug`, `log names`, `log test`, `log throw [frames]` (throws in a Harmony patch).
- Unity-logged exceptions whose stack runs through PalaceMod code are logged again as `[Feature] Unhandled exception in PalaceMod code: ...` with context and stack, once. A helper class outside its manager is attributed to the manager whose `Log.For<M>()` it uses (else the only feature manager it references); `dev.py cmd "log owner StateRestorer"` shows the mapping.
- Debug bundle: "Create debug bundle" or `dev.py cmd "log bundle"` writes `BepInEx/PalaceMod/DebugBundles/PalaceMod-debug-<time>.zip` (info.txt with versions, commit, OS, features and plugins; recent.log, the last 2000 mod lines and all warnings/errors with timestamps; LogOutput.log; PalaceMod.cfg; home folder replaced by `~`) and tells the player where it is. Ask players for it with bug reports.

## Code conventions

- `Plugin.Update` isolates managers from each other's exceptions, and each distinct error is logged once (with its stack, then as counts). A manager whose `Awake` or `Start` throws is dropped, along with its patches.
- Game singletons such as `CommandUnits.instance` are null outside a level. Null-check them in `Update`.
- `Plugin.Input` (the Rewired player) is null until Rewired initializes. Use `?.`.
- To read game code: `python3 scripts/dev.py decompile --type <TypeName>` writes `.decompiled/<TypeName>.decompiled.cs`. Leave out `--type` to decompile everything into a project, which is the way to grep for things like PlayerPrefs keys.
- The main menu is the scene `_StartMenu` (`SceneTransitionManager.mainMenuScene`). Use `SceneManager.GetActiveScene().name` to tell which screen you're on.
- Never commit `lib/`, `.decompiled/` or anything else copied from the game; they're in `.gitignore`.
- Rescanning pathfinding at runtime: call `Builder.Level.GroundGraphs.Scan()`, never `AstarPath.active.Scan()`. A plain scan empties the fliers' graph (their surface is inactive in play) and turns cliff and wall tops into walkable islands; `GroundGraphs.Scan` handles both and redraws the minimap (docs/builder-ux.md, "T0 results").
- Builds target `netstandard2.1`. The 18 existing Harmony003 analyzer warnings predate this guide and aren't regressions.

## Keybinds and the Controls screen

`Core/KeybindsManager` turns every keybind registered in a feature's `Awake` into a Rewired action (category `PalaceMod`), so it gets a native row under a "Mods" heading in Settings > Controls and is remapped by the game's own listener (keyboard with modifiers, mouse, controller); conflicts with game or mod keys show in red on both rows, a changed mod key gets a "Default" button on its row, and "Restore Default Configuration" resets mod keys too. Keys the game has no glyph for (Cmd, Backspace, F-keys, punctuation, mouse 4-7) use the mod's keycaps from `branding/key_glyphs.py`. Bindings persist in the BepInEx config (`Keybinds` section, hidden), never in the game's `ControlsConfig_v2.json`, which stays vanilla. A keybind registered after Rewired starts (i.e. outside `Awake`) has no row and reads Unity Input. `dev.py cmd keybinds` lists bindings and conflicts; `keybinds open|select|bind|default|reset` drive the screen, `keybinds press <KeyCode>[+<KeyCode>...]` fakes a physical key press or chord (Rewired sees it), and `keybinds camera` prints the camera zoom. Menu focus is `UIFrameManager.instance.ActiveFrame != null`; the game has no text fields of its own. The game's cheat menu (`CheatMenuIMGUI` and `CheatSystem`) is still compiled in, but nothing opens it.

## Don't

- Don't edit or delete files in `~/Library/Application Support/Grizzly Games/Thronefall` (the player's saves) or `~/Library/Preferences/com.grizzlygames.thronefall.plist` by hand. If something goes wrong, restore from the save backups.
- Don't `kill` the game directly; use `dev.py stop`.
