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
# Inspect a game type with ILSpy CLI
python3 scripts/dev.py decompile --type BuildSlot

# Inspect the local environment
python3 scripts/dev.py doctor

# Install into an existing BepInEx 5 setup
python3 scripts/dev.py install

# Rebuild and install when source files change
python3 scripts/dev.py watch --install

# Read the latest BepInEx log
python3 scripts/dev.py logs
```

Close the game before installing and restart it to load changes. Installation
backs up the previous plugin locally. Watching files does not hot-reload the game.

## Project layout

- `src/PalaceMod/Core/`: manager lifecycle and shared helpers
- `src/PalaceMod/Features/`: feature implementations
- `src/PalaceMod/Experimental/`: inactive managers and development experiments
- `src/PalaceMod/Assets/`: embedded assets
- `scripts/dev.py`: setup, build, decompilation and installation commands

Active managers are registered in `src/PalaceMod/Plugin.cs`. The wiki feature is
unfinished. Builds target .NET Standard 2.1; compilation alone does not establish
compatibility with a particular game version or BepInEx runtime. On Mac, the game
and native loader architectures must match, and Harmony patching needs runtime testing.
