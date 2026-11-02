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
```

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

The scripts can launch the game, read the log and capture frames, but they can't play. To check gameplay features:

1. Log what the feature does (`Plugin.Logger.LogInfo`), then check it with `dev.py logs`.
2. Take a `dev.py screenshot` and look at the image.
3. For anything that needs real input (starting a level, building, fighting a wave), use computer use if you have it. Otherwise, ask the user to test it, with exact steps to follow and what they should see.

## Code layout and conventions

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
