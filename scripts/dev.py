#!/usr/bin/env python3
"""CLI development workflow; private game files stay in ignored directories."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tarfile
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / ".local/config.json"
# Shared across worktrees and outside the repo so backups survive worktree cleanup.
DEV_HOME = Path(os.environ.get("PALACEMOD_DEV_HOME",
                               Path.home() / "Documents/Misc/Thronefall Backup/PalaceMod dev"))
BACKUPS = DEV_HOME / "backups"
DEFAULT_GAME = Path.home() / "Library/Application Support/Steam/steamapps/common/Thronefall"
SAVE_DIR = Path.home() / "Library/Application Support/Grizzly Games/Thronefall"
PREFS = Path.home() / "Library/Preferences/com.grizzlygames.thronefall.plist"
PROCESS = "Thronefall"
SAVE_BACKUPS_KEPT = 30
PREFS_DOMAIN = "com.grizzlygames.thronefall"
# The game applies its Video_* settings at startup; Unity persists Screenmanager * on quit.
WINDOWED_PREFS = {"Video_Fullscreen": 0, "Screenmanager Fullscreen mode": 3}
DISPLAY_KEYS = [*WINDOWED_PREFS, "Video_ResWidth", "Video_ResHeight", "Screenmanager Resolution Width",
                "Screenmanager Resolution Height", "Screenmanager Resolution Use Native",
                "Screenmanager Window Position X", "Screenmanager Window Position Y"]
DISPLAY_SAVED = DEV_HOME / "display-prefs.json"
ROSETTA_MARKER = "# PalaceMod: run under Rosetta"
ROSETTA_BLOCK = f"""{ROSETTA_MARKER} because libdoorstop.dylib is x86_64-only.
if [ "$(uname -m)" = "arm64" ] && [ -z "$PALACEMOD_ROSETTA" ]; then
    export PALACEMOD_ROSETTA=1
    exec /usr/bin/arch -x86_64 /bin/sh "$0" "$@"
fi
"""
PRELOADER_TARGET = 'target_assembly="BepInEx/core/BepInEx.Preloader.dll"'
BOOTSTRAP_TARGET = 'target_assembly="BepInEx/core/PalaceMod.Bootstrap.dll"'
DOORSTOP_URL = "https://github.com/NeighTools/UnityDoorstop/releases/download/v4.6.0/doorstop_macos_release_4.6.0.zip"
DOORSTOP_SHA256 = "8282e8ec37abc81bbf736d12c315fdb78b0418654fb57be9e40858649953ccb7"
GC_MARKER = "# PalaceMod: garbage collector tuning"
GC_BLOCK = f"""{GC_MARKER}. Boehm reads these at startup; override any of them in the environment.
# A lower free-space divisor collects less often, and a larger initial heap avoids mid-level expansions.
export GC_FREE_SPACE_DIVISOR="${{GC_FREE_SPACE_DIVISOR:-1}}"
export GC_INITIAL_HEAP_SIZE="${{GC_INITIAL_HEAP_SIZE:-512M}}"
# Per-collection timings, overwritten each launch, so spikes can be matched to GC pauses.
if [ -z "$PALACEMOD_NO_GC_LOG" ]; then
    export GC_PRINT_STATS=1 GC_LOG_FILE="$(cd "$(dirname "$0")" && pwd)/BepInEx/gc-stats.log"
    rm -f "$GC_LOG_FILE"
fi
"""


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


def log_path(game):
    return game / "BepInEx/LogOutput.log"


def stamp():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")


# --- Game process -----------------------------------------------------------

def game_pids():
    result = subprocess.run(["pgrep", "-x", PROCESS], capture_output=True, text=True)
    return [int(pid) for pid in result.stdout.split()]


def require_stopped():
    if game_pids():
        raise RuntimeError("Thronefall is running; run `dev.py stop` first (or use `dev.py run`).")


# The game install, its log and its preferences are shared by every checkout and worktree,
# so the checkout that launched the running game owns it until it stops it.
OWNER = DEV_HOME / "owner.json"


def game_owner():
    if not game_pids() or not OWNER.exists():
        return None
    return json.loads(OWNER.read_text())["root"]


def check_owner(force):
    if not game_pids() or force:
        return
    owner = game_owner()
    if owner is None:
        raise RuntimeError("Thronefall was not launched by dev.py, so the player may be playing it. "
                           "Ask them to quit, or pass --force once they agree.")
    if owner != str(ROOT):
        raise RuntimeError(f"The running game was launched by another checkout ({owner}), so another "
                           "session is probably testing. Wait until it stops the game, or pass --force "
                           "if that session is gone.")


def claim_game():
    OWNER.parent.mkdir(parents=True, exist_ok=True)
    OWNER.write_text(json.dumps({"root": str(ROOT), "started": stamp()}) + "\n")


def stop(force=False):
    check_owner(force)
    pids = game_pids()
    if not pids:
        print("Thronefall is not running.")
        restore_display()
        OWNER.unlink(missing_ok=True)
        return
    for pid in pids:
        os.kill(pid, signal.SIGTERM)
    for _ in range(30):
        if not game_pids():
            print("Thronefall stopped.")
            break
        time.sleep(0.5)
    else:
        for pid in game_pids():
            os.kill(pid, signal.SIGKILL)
        time.sleep(1)
        print("Thronefall force-stopped.")
    restore_display()
    OWNER.unlink(missing_ok=True)


def save_display():
    """Remember the player's display settings before a windowed dev launch overwrites them."""
    if DISPLAY_SAVED.exists():
        return
    values = {}
    for key in DISPLAY_KEYS:
        result = subprocess.run(["defaults", "read", PREFS_DOMAIN, key], capture_output=True, text=True)
        if result.returncode == 0:
            values[key] = int(result.stdout.strip())
    DISPLAY_SAVED.parent.mkdir(parents=True, exist_ok=True)
    DISPLAY_SAVED.write_text(json.dumps(values, indent=2) + "\n")


def apply_windowed():
    save_display()
    for key, value in WINDOWED_PREFS.items():
        subprocess.run(["defaults", "write", PREFS_DOMAIN, key, "-int", str(value)], check=True)


def restore_display():
    """Put back the display settings saved by save_display (Unity persists the windowed mode on quit)."""
    if not DISPLAY_SAVED.exists() or game_pids():
        return
    for key, value in json.loads(DISPLAY_SAVED.read_text()).items():
        subprocess.run(["defaults", "write", PREFS_DOMAIN, key, "-int", str(value)], check=True)
    DISPLAY_SAVED.unlink()
    print("Restored the game's display settings.")


def ensure_steam():
    if subprocess.run(["pgrep", "-x", "steam_osx"], capture_output=True).returncode == 0:
        return
    print("Starting Steam (required for the game to boot)...", flush=True)
    subprocess.run(["open", "-a", "Steam"], check=True)
    for _ in range(60):
        if subprocess.run(["pgrep", "-x", "steam_osx"], capture_output=True).returncode == 0:
            time.sleep(15)  # let the client finish signing in
            return
        time.sleep(1)
    raise RuntimeError("Steam did not start.")


# --- Safety -----------------------------------------------------------------

def backup_saves():
    """Snapshot save files and preferences before anything runs the game."""
    if not SAVE_DIR.exists():
        return None
    folder = BACKUPS / "saves"
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / f"saves-{stamp()}.tar.gz"
    with tarfile.open(target, "w:gz") as archive:
        archive.add(SAVE_DIR, arcname="Thronefall")
        if PREFS.exists():
            archive.add(PREFS, arcname=PREFS.name)
    for old in sorted(folder.glob("saves-*.tar.gz"))[:-SAVE_BACKUPS_KEPT]:
        old.unlink()
    return target


# --- Setup ------------------------------------------------------------------

def patch_loader(game):
    """Patch run_bepinex.sh: re-exec under Rosetta (so Steam launches load the mod) and tune the GC.

    Returns the descriptions of the blocks that were added."""
    script = game / "run_bepinex.sh"
    if not script.is_file():
        raise RuntimeError(f"Missing {script}; install BepInEx 5 for macOS into the game first.")
    text = script.read_text()
    blocks = [(GC_MARKER, GC_BLOCK, "tune the garbage collector")]
    if not is_native(game):
        blocks.append((ROSETTA_MARKER, ROSETTA_BLOCK, "run under Rosetta"))
    missing = [(block, label) for marker, block, label in blocks if marker not in text]
    if not missing:
        return []
    BACKUPS.mkdir(parents=True, exist_ok=True)
    shutil.copy2(script, BACKUPS / f"run_bepinex.sh.orig-{stamp()}")
    first, _, rest = text.partition("\n")
    # Each block goes right after the shebang; the GC exports survive the Rosetta re-exec.
    for block, _ in missing:
        rest = block + rest
    script.write_text(f"{first}\n{rest}")
    return [label for _, label in missing]


# --- Native Apple Silicon ---------------------------------------------------

def is_native(game):
    script = game / "run_bepinex.sh"
    return script.is_file() and BOOTSTRAP_TARGET in script.read_text()


def universal_doorstop():
    """Doorstop 4.6's universal libdoorstop.dylib, downloaded once and checksummed."""
    cached = DEV_HOME / "doorstop-4.6.0/libdoorstop.dylib"
    if cached.is_file():
        return cached
    import io
    import urllib.request
    import zipfile
    data = urllib.request.urlopen(DOORSTOP_URL, timeout=60).read()
    if hashlib.sha256(data).hexdigest() != DOORSTOP_SHA256:
        raise RuntimeError(f"Checksum mismatch for {DOORSTOP_URL}")
    cached.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        cached.write_bytes(archive.read("universal/libdoorstop.dylib"))
    return cached


def set_native(game, enabled):
    """Switch the loader between native arm64 (PalaceMod.Bootstrap) and Rosetta (stock BepInEx entrypoint)."""
    require_stopped()
    script = game / "run_bepinex.sh"
    text = script.read_text()
    BACKUPS.mkdir(parents=True, exist_ok=True)
    shutil.copy2(script, BACKUPS / f"run_bepinex.sh.orig-{stamp()}")
    if enabled:
        dylib = game / "libdoorstop.dylib"
        if "arm64" not in subprocess.run(["lipo", "-archs", str(dylib)], capture_output=True, text=True).stdout.split():
            shutil.copy2(dylib, BACKUPS / f"libdoorstop.dylib.orig-{stamp()}")
            # The universal build still loads under Rosetta, so `native off` doesn't need to restore it.
            temp = dylib.with_suffix(".new")
            shutil.copy2(universal_doorstop(), temp)
            os.replace(temp, dylib)
        run("dotnet", "build", "src/PalaceMod.Bootstrap/PalaceMod.Bootstrap.csproj", "-c", "Release",
            "-nologo", "-clp:NoSummary")
        shutil.copy2(ROOT / "src/PalaceMod.Bootstrap/bin/Release/netstandard2.0/PalaceMod.Bootstrap.dll",
                     game / "BepInEx/core/PalaceMod.Bootstrap.dll")
        # Native helper that writes to JIT memory; clang ad-hoc signs arm64 dylibs, which is enough to load it.
        native = ROOT / "bin/native/libpalacejit.dylib"
        native.parent.mkdir(parents=True, exist_ok=True)
        run("clang", "-arch", "arm64", "-O2", "-dynamiclib", "-o", native, "src/PalaceMod.Bootstrap/native/palacejit.c")
        shutil.copy2(native, game / "BepInEx/core/libpalacejit.dylib")
        if PRELOADER_TARGET not in text and BOOTSTRAP_TARGET not in text:
            raise RuntimeError(f"Couldn't find {PRELOADER_TARGET} in {script}")
        text = text.replace(PRELOADER_TARGET, BOOTSTRAP_TARGET)
        start = text.find(ROSETTA_MARKER)
        if start != -1:
            end = text.index("fi\n", start) + 3
            text = text[:start] + text[end:]
        script.write_text(text)
        patch_loader(game)
        print("Loader set to native arm64. Check BepInEx/PalaceMod.Bootstrap.log after launching.")
    else:
        script.write_text(text.replace(BOOTSTRAP_TARGET, PRELOADER_TARGET))
        patch_loader(game)
        print("Loader set to Rosetta.")


def setup(game):
    run("dotnet", "tool", "restore")
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
    added = patch_loader(game)
    if added:
        print(f"Patched run_bepinex.sh to {' and '.join(added)} (original backed up in {BACKUPS}).")
    print("Game libraries and local settings are ignored by Git.")


# --- Build / install --------------------------------------------------------

def build(configuration):
    if not (ROOT / "lib/Assembly-CSharp.dll").is_file():
        raise RuntimeError("Game libraries missing from lib/; run `dev.py setup` first.")
    run("dotnet", "build", "PalaceMod.csproj", "-c", configuration, "-nologo", "-clp:NoSummary")


def install(game, configuration):
    require_stopped()
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
        backup = BACKUPS / f"installed-{stamp()}"
        BACKUPS.mkdir(parents=True, exist_ok=True)
        shutil.copytree(destination, backup)
        for old in sorted(BACKUPS.glob("installed-*"))[:-10]:
            shutil.rmtree(old)
    destination.mkdir(parents=True, exist_ok=True)
    for name in ["PalaceMod.dll", "PalaceMod.pdb"]:
        if (source / name).exists():
            shutil.copy2(source / name, destination / name)
    print(f"Installed to {destination}")


# --- Launch / verify --------------------------------------------------------

def launch(game, timeout, fullscreen):
    require_stopped()
    if fullscreen:
        restore_display()
    else:
        apply_windowed()
    ensure_steam()
    saved = backup_saves()
    if saved:
        print(f"Saves backed up to {saved}")
    log = log_path(game)
    started = time.time()
    out = ROOT / ".local/launch.log"
    out.parent.mkdir(exist_ok=True)
    with out.open("w") as handle:
        # run_bepinex.sh re-execs itself under Rosetta unless the loader is native.
        command = ["/bin/sh", str(game / "run_bepinex.sh")]
        env = {**os.environ, "PALACEMOD_DEV": "1"}
        subprocess.Popen(command, cwd=game, stdout=handle, stderr=subprocess.STDOUT,
                         stdin=subprocess.DEVNULL, start_new_session=True, env=env)
    claim_game()
    arch = "natively" if is_native(game) else "under Rosetta"
    print(f"Launching Thronefall {arch} ({'fullscreen' if fullscreen else 'windowed'})...", flush=True)
    deadline = started + timeout
    while time.time() < deadline:
        time.sleep(1)
        if log.exists() and log.stat().st_mtime >= started:
            text = log.read_text(errors="replace")
            if "Chainloader startup complete" in text:
                time.sleep(5)  # let managers run Start/Update before reporting
                report(game)
                return
        if time.time() - started > 20 and not game_pids():
            raise RuntimeError(f"Thronefall exited during startup; see {out}")
    raise RuntimeError(f"BepInEx did not finish loading within {timeout}s; see {out} and {log}")


def error_blocks(text):
    """Group each [Error]/[Fatal] log line with its stack trace."""
    blocks, current = [], None
    for line in text.splitlines():
        if line.startswith(("[Error", "[Fatal")):
            current = [line]
            blocks.append(current)
        elif current is not None and line.strip() and not line.startswith("["):
            current.append(line)
        else:
            current = None
    return ["\n".join(block) for block in blocks]


def report(game):
    log = log_path(game)
    text = log.read_text(errors="replace")
    loaded = "PalaceMod loaded!" in text
    print(f"PalaceMod loaded: {'yes' if loaded else 'NO'}  (game running: {'yes' if game_pids() else 'no'})")
    print_errors(text)
    if not loaded:
        raise RuntimeError(f"PalaceMod did not load; see {log}")


def print_errors(text):
    counts = {}
    for block in error_blocks(text):
        counts[block] = counts.get(block, 0) + 1
    if not counts:
        print("Errors: none")
        return
    print(f"Errors: {len(counts)} unique, {sum(counts.values())} total")
    for block, count in counts.items():
        lines = block.splitlines()
        print(f"--- x{count}")
        print("\n".join(lines[:8] + (["  ..."] if len(lines) > 8 else [])))


def logs(game, errors, follow, lines):
    log = log_path(game)
    if not log.exists():
        raise RuntimeError(f"No BepInEx log yet: {log}")
    if follow:
        subprocess.run(["tail", "-n", str(lines), "-F", str(log)])
    elif errors:
        print_errors(log.read_text(errors="replace"))
    else:
        print("\n".join(log.read_text(errors="replace").splitlines()[-lines:]))


def screenshot(game, output, timeout=15):
    """Ask the running mod (dev launches only) to capture the game frame; works without focus."""
    if not game_pids():
        raise RuntimeError("Thronefall is not running.")
    if game_owner() != str(ROOT):
        raise RuntimeError("The running game was not launched by `dev.py launch`/`run` from this checkout; "
                           "relaunch it with `dev.py run`.")
    target = (Path(output) if output else ROOT / f".local/screenshots/{stamp()}.png").resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    target.unlink(missing_ok=True)
    request = game / "BepInEx/PalaceMod-dev/screenshot.request"
    request.parent.mkdir(parents=True, exist_ok=True)
    request.write_text(str(target))
    deadline = time.time() + timeout
    size = -1
    while time.time() < deadline:
        time.sleep(0.5)
        if target.exists() and target.stat().st_size == size and size > 0:
            print(target)
            return
        size = target.stat().st_size if target.exists() else -1
    request.unlink(missing_ok=True)
    raise RuntimeError("No screenshot produced within the timeout; check `dev.py logs --errors`.")


def command(game, text, timeout=30):
    """Send a command to the running mod (dev launches only; see Core/DevToolsManager.cs) and print its result."""
    if not game_pids():
        raise RuntimeError("Thronefall is not running.")
    if game_owner() != str(ROOT):
        raise RuntimeError("The running game was not launched by `dev.py launch`/`run` from this checkout.")
    log = log_path(game)
    start = log.stat().st_size if log.exists() else 0
    request = game / "BepInEx/PalaceMod-dev/command.request"
    request.parent.mkdir(parents=True, exist_ok=True)
    request.write_text(text.strip() + "\n")
    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(0.25)
        with log.open("rb") as handle:
            handle.seek(start)
            new = handle.read().decode(errors="replace")
        for line in new.splitlines():
            for prefix in (f"Dev command done: {text.strip()}", f"Dev command failed: {text.strip()}"):
                if prefix in line:
                    print(line.split("] ", 1)[-1])
                    if "failed" in prefix:
                        raise RuntimeError("Command failed.")
                    return
    request.unlink(missing_ok=True)
    raise RuntimeError("The mod didn't answer; is this a dev launch (`dev.py run`)?")


# --- Misc -------------------------------------------------------------------

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
    if type_name:
        print(f"Decompiled to {output / (type_name + '.decompiled.cs')}")
    else:
        print(f"Decompiled project written to {output} (ignored by Git)")


def file_hash(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def doctor(game, configuration):
    def check(label, ok, detail=""):
        print(f"[{'ok' if ok else '!!'}] {label}{': ' + detail if detail else ''}")

    check("Game", game.exists(), str(game))
    check("Managed assemblies", True, str(managed_dir(game)))
    for name in ["dotnet", "git", "python3"]:
        check(name, bool(shutil.which(name)), shutil.which(name) or "MISSING")
    libs = (ROOT / "lib/Assembly-CSharp.dll").is_file()
    check("Game libraries in lib/", libs, "" if libs else "run `setup`")
    check("BepInEx 5 installed", (game / "BepInEx/core/BepInEx.dll").is_file())
    rosetta = subprocess.run(["/usr/bin/arch", "-x86_64", "/usr/bin/true"], capture_output=True).returncode == 0
    check("Rosetta available", rosetta, "softwareupdate --install-rosetta" if not rosetta else "")
    script = game / "run_bepinex.sh"
    patched = script.is_file() and ROSETTA_MARKER in script.read_text()
    if is_native(game):
        check("Loader", True, "native arm64 (PalaceMod.Bootstrap)")
    else:
        check("run_bepinex.sh runs under Rosetta", patched, "" if patched else "run `setup` to patch")
    tuned = script.is_file() and GC_MARKER in script.read_text()
    check("run_bepinex.sh tunes the GC", tuned, "" if tuned else "run `setup` to patch")
    localconfigs = list((Path.home() / "Library/Application Support/Steam/userdata").glob("*/config/localconfig.vdf"))
    steam_ok = any("run_bepinex.sh" in f.read_text(errors="replace") for f in localconfigs)
    check("Steam launch options use run_bepinex.sh", steam_ok,
          "" if steam_ok else f"set to: \"{script}\" %command%")
    built = ROOT / f"bin/{configuration}/netstandard2.1/PalaceMod.dll"
    installed = game / "BepInEx/plugins/PalaceMod/PalaceMod.dll"
    check("Installed plugin matches this build", file_hash(built) is not None and file_hash(built) == file_hash(installed))
    check("Game running", True, f"yes, launched from {game_owner() or 'outside dev.py'}" if game_pids() else "no")
    restored = not DISPLAY_SAVED.exists() or bool(game_pids())
    check("Display settings restored", restored, "" if restored else "run `dev.py stop` to restore them")
    log = log_path(game)
    check("BepInEx log", log.exists(), str(log))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["setup", "build", "install", "run", "launch", "stop", "logs",
                                            "screenshot", "backup-saves", "watch", "decompile", "doctor", "native", "cmd"])
    parser.add_argument("mode", nargs="?", help="native: on|off. cmd: the command, e.g. \"level Neuland\"")
    parser.add_argument("--game", help="Game directory or Thronefall.app; otherwise saved config or THRONEFALL_DIR")
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Release"])
    parser.add_argument("--type", dest="type_name", help="Decompile a single type instead of the whole assembly")
    parser.add_argument("--install", action="store_true", help="Watch: install after each successful build")
    parser.add_argument("--errors", action="store_true", help="Logs: show unique errors with stack traces")
    parser.add_argument("--follow", action="store_true", help="Logs: stream the log")
    parser.add_argument("--lines", type=int, default=150, help="Logs: number of trailing lines")
    parser.add_argument("--timeout", type=int, default=120, help="Launch: seconds to wait for BepInEx")
    parser.add_argument("--output", help="Screenshot: output PNG path")
    parser.add_argument("--force", action="store_true",
                        help="Run/stop: take over a game launched by another checkout")
    parser.add_argument("--fullscreen", action="store_true",
                        help="Launch/run: use the player's display settings instead of windowed mode")
    args = parser.parse_args()
    game = game_root(args.game)
    if args.command == "setup":
        setup(game)
    elif args.command == "build":
        build(args.configuration)
    elif args.command == "install":
        install(game, args.configuration)
    elif args.command == "run":
        build(args.configuration)
        stop(args.force)
        install(game, args.configuration)
        launch(game, args.timeout, args.fullscreen)
    elif args.command == "launch":
        launch(game, args.timeout, args.fullscreen)
    elif args.command == "stop":
        stop(args.force)
    elif args.command == "logs":
        logs(game, args.errors, args.follow, args.lines)
    elif args.command == "screenshot":
        screenshot(game, args.output)
    elif args.command == "backup-saves":
        print(backup_saves() or f"No save directory at {SAVE_DIR}")
    elif args.command == "watch":
        watch(game, args.configuration, args.install)
    elif args.command == "decompile":
        decompile(game, args.type_name)
    elif args.command == "doctor":
        doctor(game, args.configuration)
    elif args.command == "cmd":
        if not args.mode:
            raise RuntimeError("Usage: dev.py cmd \"<command>\" (see src/PalaceMod/Core/DevToolsManager.cs)")
        command(game, args.mode)
    elif args.command == "native":
        if args.mode not in (None, "on", "off"):
            raise RuntimeError("Usage: dev.py native [on|off]")
        if args.mode is None:
            print("native" if is_native(game) else "rosetta")
        else:
            set_native(game, args.mode == "on")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
