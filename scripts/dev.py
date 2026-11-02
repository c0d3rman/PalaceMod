#!/usr/bin/env python3
"""CLI development workflow; private game files stay in ignored directories."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / ".local/config.json"
DEFAULT_GAME = Path.home() / "Library/Application Support/Steam/steamapps/common/Thronefall"


def run(*args):
    subprocess.run([str(arg) for arg in args], cwd=ROOT, check=True)


def game_root(value=None):
    if value:
        path = Path(value).expanduser().resolve()
    elif os.environ.get("THRONEFALL_DIR"):
        path = Path(os.environ["THRONEFALL_DIR"]).expanduser().resolve()
    elif CONFIG.exists():
        path = Path(json.loads(CONFIG.read_text())["game"])
    else:
        path = DEFAULT_GAME
    return path.parent if path.suffix == ".app" else path


def managed_dir(game):
    candidates = [game / "Thronefall.app/Contents/Resources/Data/Managed",
                  game / "Thronefall_Data/Managed"]
    for candidate in candidates:
        if (candidate / "Assembly-CSharp.dll").is_file():
            return candidate
    raise RuntimeError(f"Cannot find Thronefall managed assemblies in {game}")


def setup(game):
    managed = managed_dir(game)
    project = ET.parse(ROOT / "PalaceMod.csproj")
    names = [item.text.rsplit("/", 1)[-1] for item in project.findall(".//Reference/HintPath")]
    missing = [name for name in names if not (managed / name).is_file()]
    if missing:
        raise RuntimeError(f"Missing game libraries: {', '.join(missing)}")
    lib = ROOT / "lib"
    lib.mkdir(exist_ok=True)
    for name in names:
        shutil.copy2(managed / name, lib / name)
    CONFIG.parent.mkdir(exist_ok=True)
    CONFIG.write_text(json.dumps({"game": str(game)}, indent=2) + "\n")
    print(f"Copied {len(names)} game libraries to {lib}")
    print("Game libraries and local settings are ignored by Git.")


def build(configuration):
    run("dotnet", "build", "PalaceMod.csproj", "-c", configuration)


def install(game, configuration):
    source = ROOT / f"bin/{configuration}/netstandard2.1"
    if not (source / "PalaceMod.dll").is_file():
        raise RuntimeError("Build PalaceMod before installing it.")
    if not (game / "BepInEx/core/BepInEx.dll").is_file():
        raise RuntimeError("Install a compatible BepInEx 5 loader in the game first.")
    plugins = game / "BepInEx/plugins"
    # Refuse duplicate copies, including an old flat installation.
    destination = plugins / "PalaceMod"
    duplicates = [f for f in plugins.rglob("PalaceMod.dll") if f.parent != destination]
    if duplicates:
        raise RuntimeError(f"Move duplicate PalaceMod DLLs out of plugins first: {duplicates}")
    if destination.exists():
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        backup = ROOT / f".backups/installed-{stamp}"
        backup.parent.mkdir(exist_ok=True)
        shutil.copytree(destination, backup)
        print(f"Previous installed mod backed up to {backup}")
    destination.mkdir(parents=True, exist_ok=True)
    for name in ["PalaceMod.dll", "PalaceMod.pdb"]:
        if (source / name).exists():
            shutil.copy2(source / name, destination / name)
    print(f"Installed to {destination}; restart the game to load changes.")


def snapshot():
    files = list((ROOT / "src").rglob("*")) + [ROOT / "PalaceMod.csproj"]
    return {str(f): hashlib.sha256(f.read_bytes()).hexdigest() for f in files if f.is_file()}


def watch(game, configuration, deploy):
    previous = None
    while True:
        current = snapshot()
        if current != previous:
            previous = current
            try:
                build(configuration)
                if deploy:
                    install(game, configuration)
            except (subprocess.CalledProcessError, RuntimeError) as error:
                print(f"Build/install failed: {error}", file=sys.stderr)
            print("Watching source and assets; Ctrl+C to stop.", flush=True)
        time.sleep(1)


def decompile(game, type_name):
    managed = managed_dir(game)
    assembly = managed / "Assembly-CSharp.dll"
    output = ROOT / ".decompiled"
    output.mkdir(exist_ok=True)
    args = ["dotnet", "tool", "run", "ilspycmd", "--", "-r", managed, "-o", output]
    if type_name:
        args.extend(["-t", type_name])
    else:
        args.append("-p")
    run(*args, assembly)
    print(f"Decompiled output: {output} (ignored by Git)")


def doctor(game):
    print(f"Game: {game}")
    print(f"Managed assemblies: {managed_dir(game)}")
    for name in ["dotnet", "git", "python3"]:
        print(f"{name}: {shutil.which(name) or 'MISSING'}")
    executable = game / "Thronefall.app/Contents/MacOS/Thronefall"
    for path in [executable, game / "libdoorstop.dylib"]:
        if path.exists() and shutil.which("file"):
            run("file", path)
    log = game / "BepInEx/LogOutput.log"
    print(f"BepInEx log: {log} ({'exists' if log.exists() else 'not generated'})")
    print("Architecture output is diagnostic only; loader and Harmony startup need an in-game test.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["setup", "build", "install", "watch", "decompile", "doctor", "logs"])
    parser.add_argument("--game", help="Game directory or Thronefall.app; otherwise saved config or THRONEFALL_DIR")
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Release"])
    parser.add_argument("--type", dest="type_name", help="Decompile a single type instead of the whole assembly")
    parser.add_argument("--install", action="store_true", help="Watch: install after each successful build")
    args = parser.parse_args()
    game = game_root(args.game)
    if args.command == "setup":
        setup(game)
    elif args.command == "build":
        build(args.configuration)
    elif args.command == "install":
        install(game, args.configuration)
    elif args.command == "watch":
        watch(game, args.configuration, args.install)
    elif args.command == "decompile":
        decompile(game, args.type_name)
    elif args.command == "doctor":
        doctor(game)
    elif args.command == "logs":
        log = game / "BepInEx/LogOutput.log"
        if not log.exists():
            raise RuntimeError(f"No BepInEx log yet: {log}")
        print("\n".join(log.read_text(errors="replace").splitlines()[-150:]))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
