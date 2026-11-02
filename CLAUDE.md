# PalaceMod agent guide

BepInEx 5 mod for Thronefall (Unity 2022.3, Mono), developed on an Apple Silicon Mac. All workflow commands go through `scripts/dev.py`.

## Dev loop

```sh
python3 scripts/dev.py doctor        # check the environment; fix anything marked !!
python3 scripts/dev.py setup         # once per checkout/worktree: restores tools, copies game DLLs into lib/
python3 scripts/dev.py run           # build -> stop game -> install -> launch -> wait for BepInEx -> print errors
python3 scripts/dev.py logs --errors # unique errors with stack traces from the current game session
python3 scripts/dev.py screenshot    # in-game frame capture (works while unfocused); prints the PNG path
python3 scripts/dev.py stop          # quit the game and restore the player's display settings
python3 scripts/dev.py cmd "<command>"  # drive a dev launch: load levels, start nights, fake input, open/navigate menus
```

Dev launches are windowed, muted (`--sound` to keep audio) and hand focus back to the app that was in front (`--focus` lets the game keep it). Don't launch `--fullscreen` unless you need to: it switches the user to a new Space.

A feature is not done until `run` reports `PalaceMod loaded: yes` and no PalaceMod errors. The one expected error is `HookGenPatcher ... Could not load file or assembly 'MonoMod'`, which comes from a BepInEx patcher and is harmless.

Always finish with `dev.py stop`, so the game isn't left running and the player's fullscreen settings are restored.

There is one game install, so only one session can test at a time. The checkout that launched the game owns it, and `run`, `stop` and `screenshot` refuse to act on a game another checkout launched. If you're refused, wait and retry. Use `--force` only if the other session is definitely gone.

## How the Mac setup works

- `libdoorstop.dylib` is x86\_64-only, so the game has to run under Rosetta. `launch` does this itself, and `setup` patches the game's `run_bepinex.sh` so Steam launches use Rosetta too.
- `dev.py native on` (experimental) switches to Doorstop 4.6's universal dylib plus `PalaceMod.Bootstrap`, which makes MonoMod work under Apple Silicon W^X so the game runs natively; `native off` goes back to Rosetta. The bootstrap writes its status to `BepInEx/PalaceMod.Bootstrap.log`.
- `setup` also adds GC tuning to `run_bepinex.sh` (`GC_FREE_SPACE_DIVISOR`, `GC_INITIAL_HEAP_SIZE`) and per-collection timings in `BepInEx/gc-stats.log`. `PerformanceManager` logs frame spikes and a frame-time summary to the BepInEx log.
- Steam must be running. `launch` starts it if it isn't.
- Dev launches set `PALACEMOD_DEV=1`, which turns on `Core/DevToolsManager.cs`: the game keeps running in the background and screenshots are taken on request. Dev launches are windowed unless you pass `--fullscreen`.
- Every launch first backs up save files and preferences to `~/Documents/Misc/Thronefall Backup/PalaceMod dev/backups/saves/`, keeping the last 30.

## Verifying in-game behavior

Drive the game yourself instead of asking the user to click through it. `dev.py cmd` sends commands to `Core/DevToolsManager.cs` (the full list is in its header comment), for example:

```sh
python3 scripts/dev.py cmd scenes            # list scenes
python3 scripts/dev.py cmd "level Nordfels"  # load a level
python3 scripts/dev.py cmd night             # start the next wave now
python3 scripts/dev.py cmd "circle 30"       # run the king in circles for 30 s (camera motion)
python3 scripts/dev.py cmd settings          # open Settings on the Mods tab
python3 scripts/dev.py cmd "ui down"         # navigate the open menu like a controller (up/down/left/right/apply/close)
```

Then check the result with `dev.py logs` (log what features do with `Plugin.Logger.LogInfo`) and `dev.py screenshot`. `PerformanceManager` logs a frame-time summary (fps, percentiles, CPU work per frame, frame-rate cap and reason) every "Summary interval"; settings edited in `BepInEx/config/PalaceMod.cfg` are reloaded live within 2 s. Only ask the user to test what genuinely needs a human, like how motion looks.

## Code layout and conventions

- Settings are BepInEx `ConfigEntry`s. `Features/UI/SettingsPaneManager.cs` generates the in-game Settings > Mods tab from them: bool = checkbox, enum or `AcceptableValueList` = selector, `AcceptableValueRange` = slider. Give every entry a description (shown beside the selected row) and, where useful, a `SettingTags` tag for units, a label for 0 and slider steps.
- `src/PalaceMod/Plugin.cs` registers the managers. Harmony patches are applied only for registered managers and the patch classes nested inside them, so new patch classes go inside their manager.
- Each feature is one `BaseManager` subclass under `Features/<Area>/`. Don't register managers from `Experimental/`.
- `Plugin.Update` isolates managers from each other's exceptions, and each distinct error is logged once.
- Game singletons such as `CommandUnits.instance` are null outside a level. Null-check them in `Update`.
- `Plugin.Input` (the Rewired player) is null until Rewired initializes. Use `?.`.
- To read game code: `python3 scripts/dev.py decompile --type <TypeName>` writes `.decompiled/<TypeName>.decompiled.cs`. Leave out `--type` to decompile everything into a project, which is the way to grep for things like PlayerPrefs keys.
- The main menu is the scene `_StartMenu` (`SceneTransitionManager.mainMenuScene`). Use `SceneManager.GetActiveScene().name` to tell which screen you're on.
- Never commit `lib/`, `.decompiled/` or anything else copied from the game; they're in `.gitignore`.
- Builds target `netstandard2.1`. The 19 existing Harmony003 analyzer warnings predate this guide and aren't regressions.

## Don't

- Don't edit or delete files in `~/Library/Application Support/Grizzly Games/Thronefall` (the player's saves) or `~/Library/Preferences/com.grizzlygames.thronefall.plist` by hand. If something goes wrong, restore from the save backups.
- Don't `kill` the game directly; use `dev.py stop`.
