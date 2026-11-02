# PalaceMod

A BepInEx 5 mod for Thronefall, with build and enemy previews, unit formations,
range displays, income and score displays, loadout randomization, and bug fixes.

## Development

Requires .NET SDK 10, Python 3.9+, and an installed copy of Thronefall.

```sh
dotnet tool restore
python3 scripts/dev.py setup --game "/path/to/Thronefall"
python3 scripts/dev.py build
```

On macOS, omitting `--game` uses the default Steam installation. Game libraries
are copied into the ignored `lib/` directory and are not included in this repository.

```sh
# Build, install, launch the game (windowed, under Rosetta) and report load errors
python3 scripts/dev.py run

# Unique errors from the running session, or stream the log
python3 scripts/dev.py logs --errors
python3 scripts/dev.py logs --follow

# Capture a frame from the running game; quit and restore display settings
python3 scripts/dev.py screenshot
python3 scripts/dev.py stop

# Inspect a game type with ILSpy CLI, or check the environment
python3 scripts/dev.py decompile --type BuildSlot
python3 scripts/dev.py doctor
```

On Apple Silicon the game must run under Rosetta because the Doorstop loader is
x86\_64-only; `setup` patches `run_bepinex.sh` so Steam launches (launch options
`"<game>/run_bepinex.sh" %command%`) load the mod too. Every launch backs up saves to
`~/Documents/Misc/Thronefall Backup/PalaceMod dev/` first. `CLAUDE.md` describes the
loop for coding agents.

## Project layout

- `src/PalaceMod/Core/`: manager lifecycle and shared helpers
- `src/PalaceMod/Features/`: feature implementations
- `src/PalaceMod/Experimental/`: inactive managers and development experiments
- `src/PalaceMod/Assets/`: embedded assets
- `scripts/dev.py`: setup, build, install, launch, logs, screenshot and decompile commands

Every `BaseManager` outside `Experimental/` is registered automatically. The wiki feature is
unfinished. Builds target .NET Standard 2.1; compilation alone does not establish
compatibility with a particular game version or BepInEx runtime; use `dev.py run` to
verify in-game.
