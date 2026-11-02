#!/usr/bin/env python3
"""CLI development workflow; private game files stay in ignored directories."""
import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tarfile
import threading
import time
import uuid
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


def stop_game(force=False):
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


def copy_missing_libs():
    """Copies game libraries that PalaceMod.csproj references but lib/ lacks (e.g. one added after setup ran)."""
    project = ET.parse(ROOT / "PalaceMod.csproj")
    names = [item.text.rsplit("/", 1)[-1] for item in project.findall(".//Reference/HintPath")]
    missing = [name for name in names if not (ROOT / "lib" / name).is_file()]
    if not missing:
        return
    managed = managed_dir(game_root())
    for name in missing:
        shutil.copy2(managed / name, ROOT / "lib" / name)
    print(f"Copied newly referenced game libraries into lib/: {', '.join(missing)}")


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
    copy_missing_libs()
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

GAME_BUNDLE = "com.grizzlygames.thronefall"


def frontmost_bundle():
    """Bundle ID of the frontmost app, via lsappinfo (needs no Accessibility permission)."""
    asn = subprocess.run(["lsappinfo", "front"], capture_output=True, text=True).stdout.strip()
    info = subprocess.run(["lsappinfo", "info", "-only", "bundleid", asn], capture_output=True, text=True).stdout
    for line in info.splitlines():
        line = line.strip()
        if line.startswith('bundleID="'):
            return line[len('bundleID="'):].rstrip('"')
    return None


class FocusKeeper:
    """Hands focus back to the app that was in front whenever the game grabs it during startup."""

    def __init__(self, enabled):
        self.previous = frontmost_bundle() if enabled else None
        if self.previous == GAME_BUNDLE:
            self.previous = None

    def check(self):
        if self.previous and frontmost_bundle() == GAME_BUNDLE:
            subprocess.run(["open", "-b", self.previous], capture_output=True)


def launch(game, timeout, fullscreen, keep_focus=True):
    require_stopped()
    if fullscreen:
        restore_display()
    else:
        apply_windowed()
    # Remember the app in front before Steam or the game can take focus.
    focus = FocusKeeper(keep_focus and not fullscreen)
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
        focus.check()
        if log.exists() and log.stat().st_mtime >= started:
            text = log.read_text(errors="replace")
            if "Chainloader startup complete" in text:
                for _ in range(5):  # let managers run Start/Update before reporting
                    time.sleep(1)
                    focus.check()
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
    write_atomic(request, str(target))
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
    """Send a command to the running mod (dev launches only; `cmd help` lists them) and print its result."""
    if not game_pids():
        raise RuntimeError("Thronefall is not running.")
    if game_owner() != str(ROOT):
        raise RuntimeError("The running game was not launched by `dev.py launch`/`run` from this checkout.")
    folder = game / "BepInEx/PalaceMod-dev"
    folder.mkdir(parents=True, exist_ok=True)
    request_id = uuid.uuid4().hex
    response = folder / f"command-{request_id}.response"
    request = folder / "command.request"
    write_atomic(request, f"@{request_id}\n{text.strip()}\n")
    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(0.25)
        if response.exists():
            status, _, result = response.read_text().partition("\n")
            response.unlink(missing_ok=True)
            if status != "ok":
                raise RuntimeError(f"Command failed: {result}")
            print(result or "ok")
            return
    request.unlink(missing_ok=True)
    raise RuntimeError("The mod didn't answer; is this a dev launch (`dev.py run`)? Check `dev.py logs --errors`.")


# --- Game lease -------------------------------------------------------------
#
# Every checkout shares one game install: the running game, BepInEx/plugins/PalaceMod, BepInEx/config and the
# player's preferences. A checkout takes the lease (`run`, `launch`, `install` or `lease acquire`) and keeps it
# until `stop` or `lease release`; other checkouts queue for it in FIFO order. Every dev.py call by the holder
# refreshes its heartbeat, and a lease idle for LEASE_IDLE_LIMIT is stale: the next dev.py call from any checkout
# stops that holder's game and frees it.
#
# BepInEx/config is also the player's real configuration, so it is snapshotted when the lease is taken and put back
# exactly when it is released or expires. A snapshot found with no live lease (a crash) is restored before
# anything else.

LEASE_DIR = DEV_HOME / "lease"
LEASE_LOCK = LEASE_DIR / ".lock"
LEASE_FILE = LEASE_DIR / "lease.json"
QUEUE_DIR = LEASE_DIR / "queue"
USER_CONFIG = LEASE_DIR / "user-config"
LEASE_IDLE_LIMIT = 10 * 60
LEASE_BUSY_EXIT = 75  # EX_TEMPFAIL: the lease didn't become free within --wait
HEARTBEAT_SECONDS = 20
DEFAULT_WAIT = 60 * 60


class LeaseBusy(RuntimeError):
    pass


@contextlib.contextmanager
def lease_lock():
    """Serialises every read-modify-write of the lease state, across processes and checkouts."""
    LEASE_DIR.mkdir(parents=True, exist_ok=True)
    with open(LEASE_LOCK, "a") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


def write_atomic(path, text):
    temp = path.with_name(f".{path.name}.{os.getpid()}.{threading.get_ident()}.tmp")
    temp.write_text(text)
    os.replace(temp, path)


def read_lease():
    try:
        return json.loads(LEASE_FILE.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def lease_idle(lease):
    return time.time() - lease["heartbeat"]


def lease_stale(lease):
    return lease_idle(lease) > LEASE_IDLE_LIMIT


def describe_holder(lease):
    branch = f" ({lease['branch']})" if lease.get("branch") else ""
    return f"{lease['root']}{branch}"


def duration(seconds):
    seconds = int(seconds)
    return f"{seconds // 60}m{seconds % 60:02d}s"


def current_branch(root=ROOT):
    result = subprocess.run(["git", "-C", str(root), "branch", "--show-current"], capture_output=True, text=True)
    return result.stdout.strip() or None


def holds_lease():
    lease = read_lease()
    return lease is not None and lease["root"] == str(ROOT) and not lease_stale(lease)


def heartbeat(command=None):
    """Refresh the holder's heartbeat; a no-op for checkouts that don't hold the lease."""
    with lease_lock():
        lease = read_lease()
        if lease is None or lease["root"] != str(ROOT) or lease_stale(lease):
            return
        lease["heartbeat"] = time.time()
        if command:
            lease["command"] = command
        write_atomic(LEASE_FILE, json.dumps(lease, indent=2) + "\n")


def pid_alive(pid):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        pass
    return True


def live_queue():
    """Waiting checkouts in arrival order, dropping entries whose dev.py process is gone."""
    QUEUE_DIR.mkdir(parents=True, exist_ok=True)
    waiters = []
    for entry in sorted(QUEUE_DIR.glob("*.json")):
        try:
            data = json.loads(entry.read_text())
            fresh = time.time() - entry.stat().st_mtime < 120
        except (FileNotFoundError, json.JSONDecodeError):
            continue
        if pid_alive(data["pid"]) and fresh:
            waiters.append((entry, data))
        else:
            entry.unlink(missing_ok=True)
    return waiters


# The player's config snapshot.

def config_dir(game):
    return game / "BepInEx/config"


def snapshot_user_config(game):
    if USER_CONFIG.exists():
        return
    temp = LEASE_DIR / f".user-config.{os.getpid()}.tmp"
    shutil.rmtree(temp, ignore_errors=True)
    (temp / "files").mkdir(parents=True)
    source = config_dir(game)
    names = sorted(p.name for p in source.iterdir() if p.is_file()) if source.is_dir() else []
    for name in names:
        shutil.copy2(source / name, temp / "files" / name)
    manifest = {"game": str(game), "files": names, "taken": stamp(), "by": str(ROOT)}
    (temp / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    os.rename(temp, USER_CONFIG)  # atomic: the snapshot is either complete or absent


def restore_user_config():
    """Put the player's BepInEx/config back exactly as snapshotted. Idempotent, so a crash part-way is redone."""
    manifest_path = USER_CONFIG / "manifest.json"
    if not manifest_path.exists():
        shutil.rmtree(USER_CONFIG, ignore_errors=True)
        return
    manifest = json.loads(manifest_path.read_text())
    target = config_dir(Path(manifest["game"]))
    target.mkdir(parents=True, exist_ok=True)
    for name in manifest["files"]:
        temp = target / f".{name}.restore.tmp"
        shutil.copy2(USER_CONFIG / "files" / name, temp)
        os.replace(temp, target / name)
    for extra in target.iterdir():
        if extra.is_file() and extra.name not in manifest["files"]:
            extra.unlink()
    shutil.rmtree(USER_CONFIG)
    print("Restored the player's BepInEx config.")


def free_lease(lease, reason):
    """Stop a dev-launched game, restore the player's config and delete the lease. Call under lease_lock."""
    if lease:
        print(f"Releasing the game lease of {describe_holder(lease)}: {reason}", flush=True)
    stop_holders_game(lease)
    restore_user_config()
    LEASE_FILE.unlink(missing_ok=True)


def stop_holders_game(lease, force=False):
    """Stop the game if the lease holder launched it. Anything else (the player's own game, or a checkout whose
    dev.py predates the lease) is left running unless forced."""
    if not game_pids():
        restore_display()  # a dev launch that crashed or was closed still left windowed-mode prefs behind
        return
    owner = game_owner()
    if force or (lease is not None and owner == lease["root"]):
        stop_game(force=True)
    elif owner is None:
        print("Thronefall is running but wasn't launched by dev.py (the player may be playing); leaving it running.")
    else:
        print(f"Thronefall was launched by {owner}, which doesn't hold the lease; leaving it running.")


def recover():
    """Runs before every command: undo what a crashed or abandoned lease left behind."""
    if not LEASE_DIR.exists():
        return
    with lease_lock():
        lease = read_lease()
        if lease is None and USER_CONFIG.exists():
            print("Found the player's config snapshot without a lease (a dev.py crash?); restoring it.", flush=True)
            free_lease(None, "")
        elif lease is not None and lease_stale(lease):
            free_lease(lease, f"idle for {duration(lease_idle(lease))}")


def take_lease(game, command):
    snapshot_user_config(game)
    now = time.time()
    lease = {"root": str(ROOT), "branch": current_branch(), "pid": os.getpid(), "command": command,
             "acquired": now, "heartbeat": now}
    write_atomic(LEASE_FILE, json.dumps(lease, indent=2) + "\n")
    print(f"Acquired the game lease (release it with `dev.py stop`; it expires after {LEASE_IDLE_LIMIT // 60} min idle).",
          flush=True)


def foreign_game():
    """Who has a running game that isn't this checkout's dev launch, or None."""
    if not game_pids():
        return None
    owner = game_owner()
    if owner == str(ROOT):
        return None
    return owner or "the player (not launched by dev.py)"


def acquire_lease(game, command, wait, force=False):
    """Return once this checkout holds the lease, queueing behind other checkouts for up to `wait` seconds."""
    ticket = None
    deadline = time.time() + wait
    shown = None
    try:
        while True:
            with lease_lock():
                lease = read_lease()
                if lease is not None and lease_stale(lease):
                    free_lease(lease, f"idle for {duration(lease_idle(lease))}")
                    lease = None
                if lease is not None and lease["root"] == str(ROOT):
                    lease["heartbeat"] = time.time()
                    lease["command"] = command
                    write_atomic(LEASE_FILE, json.dumps(lease, indent=2) + "\n")
                    return
                waiters = live_queue()
                ahead = [w for w in waiters if ticket is None or w[0].name < ticket.name]
                # A game this lease didn't launch (the player's, or a checkout on an older dev.py) also blocks it:
                # its config would be snapshotted mid-session and later restored over it.
                foreign = None if force else foreign_game()
                if lease is None and not ahead and foreign is None:
                    take_lease(game, command)
                    return
                if ticket is None:
                    ticket = QUEUE_DIR / f"{time.time_ns()}-{os.getpid()}.json"
                    ticket.write_text(json.dumps({"root": str(ROOT), "branch": current_branch(), "pid": os.getpid(),
                                                  "since": time.time()}) + "\n")
                status = (f"held by {describe_holder(lease)}, idle {duration(lease_idle(lease))}" if lease
                          else "free, but other checkouts are ahead" if ahead
                          else f"free, but Thronefall is running for {foreign} (--force takes over)")
                position = len(ahead) + 1
            if wait <= 0 or time.time() >= deadline:
                raise LeaseBusy(f"The game lease is {status}; gave up waiting (exit code {LEASE_BUSY_EXIT}). "
                                "Retry later, or wait longer with --wait SECONDS.")
            if (position, lease and lease["root"]) != shown:
                shown = (position, lease and lease["root"])
                print(f"Waiting for the game lease: {status}. You are #{position} in the queue.", flush=True)
            os.utime(ticket)
            time.sleep(2)
    finally:
        if ticket is not None:
            ticket.unlink(missing_ok=True)


def require_lease():
    if not holds_lease():
        lease = read_lease()
        holder = f"; it is held by {describe_holder(lease)}" if lease else ""
        raise RuntimeError(f"This checkout doesn't hold the game lease{holder}. Start with `dev.py run` "
                           "(or `dev.py lease acquire`).")


@contextlib.contextmanager
def game_lease(game, command, wait=None, force=False):
    """Wraps a command that touches the shared game. wait=None requires the lease already; otherwise it's acquired
    (queueing up to `wait` seconds). The heartbeat is refreshed throughout, so long launches don't go stale."""
    if wait is None:
        require_lease()
        heartbeat(command)
    else:
        acquire_lease(game, command, wait, force)
    done = threading.Event()

    def beat():
        while not done.wait(HEARTBEAT_SECONDS):
            heartbeat()

    thread = threading.Thread(target=beat, daemon=True)
    thread.start()
    try:
        yield
    finally:
        done.set()
        thread.join()
        heartbeat()


def release_lease(force):
    """`stop` / `lease release`: quit the game, restore the player's config and free the lease."""
    with lease_lock():
        lease = read_lease()
        if lease is None:
            if game_pids():
                stop_game(force)
            else:
                print("Thronefall is not running.")
                restore_display()
            restore_user_config()
            return
        if lease["root"] != str(ROOT) and not force:
            raise RuntimeError(f"The game lease is held by {describe_holder(lease)}, idle "
                               f"{duration(lease_idle(lease))}. Pass --force only if that session is gone.")
        if game_pids():
            stop_holders_game(lease, force)
        else:
            print("Thronefall is not running.")
            restore_display()
        restore_user_config()
        LEASE_FILE.unlink(missing_ok=True)
        print("Released the game lease.")


def lease_status():
    lease = read_lease()
    if lease is None:
        print("Lease: free")
    else:
        stale = " (STALE: the next dev.py call frees it)" if lease_stale(lease) else ""
        print(f"Lease: held by {describe_holder(lease)}, last command `{lease.get('command')}`, held "
              f"{duration(time.time() - lease['acquired'])}, idle {duration(lease_idle(lease))} of "
              f"{LEASE_IDLE_LIMIT // 60}m{stale}")
    with lease_lock():
        waiters = live_queue()
    print(f"Queue: {len(waiters)} waiting")
    for i, (_, data) in enumerate(waiters, 1):
        branch = f" ({data['branch']})" if data.get("branch") else ""
        print(f"  {i}. {data['root']}{branch}, waiting {duration(time.time() - data['since'])}")
    print(f"Player config snapshot: {'held' if USER_CONFIG.exists() else 'none'}")
    print(f"Game: {'running, launched from ' + (game_owner() or 'outside dev.py') if game_pids() else 'not running'}")


# Each dev launch starts PalaceMod.cfg from defaults, so one agent's settings never leak into another's test.

def mod_config(game):
    return config_dir(game) / "PalaceMod.cfg"


def reset_mod_config(game, assignments):
    """Start the launch from default settings, plus `Section.Key=value` overrides. BepInEx keeps values it finds in
    the file and fills in defaults for the rest."""
    path = mod_config(game)
    sections = {}
    for assignment in assignments:
        spec, sep, value = assignment.partition("=")
        section, dot, key = spec.partition(".")
        if not sep or not dot:
            raise RuntimeError(f"--set takes Section.Key=value, got {assignment!r}")
        sections.setdefault(section.strip(), []).append((key.strip(), value.strip()))
    if not sections:
        path.unlink(missing_ok=True)
        return
    text = "".join(f"[{section}]\n" + "".join(f"{key} = {value}\n" for key, value in entries) + "\n"
                   for section, entries in sections.items())
    path.parent.mkdir(parents=True, exist_ok=True)
    write_atomic(path, text)


def parse_cfg(text):
    """(section, key, line index, value) for every setting in a BepInEx .cfg file."""
    entries, section = [], None
    for i, line in enumerate(text.splitlines()):
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            section = stripped[1:-1]
        elif section is not None and stripped and not stripped.startswith("#") and "=" in stripped:
            key, _, value = stripped.partition("=")
            entries.append((section, key.strip(), i, value.strip()))
    return entries


def config_command(game, action, spec, value):
    path = mod_config(game)
    if not path.exists():
        raise RuntimeError(f"{path} doesn't exist yet; `dev.py run` creates it.")
    text = path.read_text()
    entries = parse_cfg(text)
    if action == "get" and not spec:
        for section, key, _, current in entries:
            print(f"{section}.{key} = {current}")
        return
    sections = {section for section, *_ in entries}
    if action == "get" and spec in sections:
        for section, key, _, current in entries:
            if section == spec:
                print(f"{section}.{key} = {current}")
        return
    # Sections and keys can contain spaces (and in principle dots), so match the whole "Section.Key" string.
    matches = [(s, k, i, v) for s, k, i, v in entries if spec == f"{s}.{k}"]
    if not matches:
        raise RuntimeError(f"No setting {spec!r} in {path.name}. `dev.py config get` lists them.")
    section, key, index, current = matches[0]
    if action == "get":
        print(current)
        return
    if value is None:
        raise RuntimeError("Usage: dev.py config set <Section>.<Key> <value>")
    lines = text.splitlines(keepends=True)
    if current.lower() in ("true", "false") and value.lower() not in ("true", "false"):
        raise RuntimeError(f"{spec} is a bool; use true or false.")
    indent = lines[index][:len(lines[index]) - len(lines[index].lstrip())]
    lines[index] = f"{indent}{key} = {value}\n"
    write_atomic(path, "".join(lines))
    running = " The running game reloads it within 2 s." if game_pids() else ""
    print(f"{section}.{key}: {current} -> {value}.{running}")


# --- Landing ----------------------------------------------------------------

BASE_BRANCH = "main"
LAND_LOCK = DEV_HOME / "land.lock"


def git(*args, cwd=ROOT, check=True):
    result = subprocess.run(["git", *map(str, args)], cwd=cwd, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"git {' '.join(map(str, args))} failed:\n{result.stdout}{result.stderr}")
    return result


def gh(*args, check=True):
    result = subprocess.run(["gh", *map(str, args)], cwd=ROOT, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"gh {' '.join(map(str, args))} failed:\n{result.stdout}{result.stderr}")
    return result


def main_checkout():
    """The repository's main checkout (the parent of the shared .git directory)."""
    common = Path(git("rev-parse", "--path-format=absolute", "--git-common-dir").stdout.strip())
    return common.parent


def open_pr(branch):
    result = gh("pr", "list", "--head", branch, "--base", BASE_BRANCH, "--state", "open",
                "--json", "number,url", "--limit", "1")
    prs = json.loads(result.stdout or "[]")
    return prs[0] if prs else None


def land(title, body_file, dry_run):
    """Bring the branch up to date with origin/main, build it, push it, open (or reuse) its PR against main, merge
    the PR, and fast-forward the main checkout. One lander at a time."""
    branch = current_branch()
    if not branch:
        raise RuntimeError("Not on a branch; check out your feature branch first.")
    if branch == BASE_BRANCH:
        raise RuntimeError(f"Run `land` from a feature branch, not {BASE_BRANCH}.")
    dirty = git("status", "--porcelain").stdout.strip()
    if dirty:
        raise RuntimeError(f"Commit or remove your changes before landing:\n{dirty}")
    if body_file and not Path(body_file).is_file():
        raise RuntimeError(f"--body-file {body_file} doesn't exist.")
    LAND_LOCK.parent.mkdir(parents=True, exist_ok=True)
    with open(LAND_LOCK, "a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print("Another checkout is landing; waiting...", flush=True)
            fcntl.flock(lock, fcntl.LOCK_EX)

        print(f"Fetching and merging origin/{BASE_BRANCH} into {branch}...", flush=True)
        git("fetch", "origin")
        before = git("rev-parse", "HEAD").stdout.strip()
        merge = git("merge", "--no-edit", f"origin/{BASE_BRANCH}", check=False)
        if merge.returncode != 0:
            conflicts = git("diff", "--name-only", "--diff-filter=U", check=False).stdout.strip()
            git("merge", "--abort", check=False)
            raise RuntimeError(f"origin/{BASE_BRANCH} doesn't merge cleanly into {branch}; nothing was landed.\n"
                               f"Conflicts:\n{conflicts or merge.stdout + merge.stderr}\n"
                               f"Run `git merge origin/{BASE_BRANCH}`, resolve, build, commit, then `dev.py land` again.")
        if git("rev-list", "--count", f"origin/{BASE_BRANCH}..HEAD").stdout.strip() == "0":
            print(f"{branch} has nothing that isn't already in {BASE_BRANCH}; nothing to land.")
            return

        print("Building...", flush=True)
        build_result = subprocess.run([sys.executable, str(ROOT / "scripts/dev.py"), "build"], cwd=ROOT,
                                      capture_output=True, text=True)
        if build_result.returncode != 0:
            git("reset", "--hard", before)  # the tree was clean, so this only drops the merge
            output = build_result.stdout + build_result.stderr
            errors = "\n".join(sorted({line.strip() for line in output.splitlines() if ": error " in line}))
            print(errors or output[-3000:], file=sys.stderr)
            raise RuntimeError(f"{branch} doesn't build with origin/{BASE_BRANCH} merged in, so the merge was undone. "
                               f"Run `git merge origin/{BASE_BRANCH}`, fix the build, commit, then `dev.py land` again.")

        if dry_run:
            print(f"Dry run: {branch} is merged with origin/{BASE_BRANCH} and builds; not pushing or opening a PR.")
            return

        print(f"Pushing {branch}...", flush=True)
        git("push", "-u", "origin", branch)
        pr = open_pr(branch)
        if pr is None:
            if not body_file:
                raise RuntimeError("No open PR for this branch yet: pass --body-file FILE (the PR description, ending "
                                   "with your attribution footer lines) to create one.")
            pr_title = title or branch.replace("-", " ").replace("_", " ").capitalize()
            gh("pr", "create", "--base", BASE_BRANCH, "--head", branch, "--title", pr_title, "--body-file", body_file)
            pr = open_pr(branch)
            if pr is None:
                raise RuntimeError("Created the PR but can't find it; check `gh pr list`.")
            print(f"Opened {pr['url']}")
        else:
            print(f"Reusing {pr['url']}")

        # GitHub computes mergeability asynchronously after a push.
        state = None
        for _ in range(30):
            state = json.loads(gh("pr", "view", pr["url"], "--json", "mergeable,mergeStateStatus").stdout)
            if state["mergeable"] != "UNKNOWN":
                break
            time.sleep(2)
        if state["mergeable"] != "MERGEABLE":
            raise RuntimeError(f"{pr['url']} isn't mergeable ({state['mergeable']}, {state['mergeStateStatus']}). "
                               f"Merge origin/{BASE_BRANCH}, fix, commit, then `dev.py land` again.")
        gh("pr", "merge", pr["url"], "--merge")
        merged = json.loads(gh("pr", "view", pr["url"], "--json", "state,mergeCommit").stdout)
        if merged["state"] != "MERGED":
            raise RuntimeError(f"`gh pr merge` didn't merge {pr['url']} (state {merged['state']}).")
        commit = (merged.get("mergeCommit") or {}).get("oid", "?")

        root = main_checkout()
        if current_branch(root) == BASE_BRANCH and not git("status", "--porcelain", "--untracked-files=no", cwd=root).stdout.strip():
            pull = git("pull", "--ff-only", cwd=root, check=False)
            print(f"Fast-forwarded {root} to {BASE_BRANCH}." if pull.returncode == 0
                  else f"Couldn't fast-forward {root}: {pull.stderr.strip()}")
        else:
            print(f"Left {root} alone (not a clean {BASE_BRANCH} checkout).")
        print(f"Landed: {pr['url']} merged as {commit[:12]}")


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
    lease = read_lease()
    check("Game lease", True, f"held by {describe_holder(lease)}, idle {duration(lease_idle(lease))}" if lease else "free")
    restored = not DISPLAY_SAVED.exists() or bool(game_pids())
    check("Display settings restored", restored, "" if restored else "run `dev.py stop` to restore them")
    log = log_path(game)
    check("BepInEx log", log.exists(), str(log))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["setup", "build", "install", "run", "launch", "stop", "logs",
                                            "screenshot", "backup-saves", "watch", "decompile", "doctor", "native",
                                            "cmd", "config", "lease", "land"])
    parser.add_argument("args", nargs="*",
                        help="native: on|off. cmd: the command, e.g. \"level Neuland\". "
                             "config: get [Section[.Key]] | set Section.Key value. lease: acquire|release|status")
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
                        help="Run/stop/lease release: take over a game or lease held by another checkout")
    parser.add_argument("--focus", action="store_true",
                        help="Launch/run: let the game take focus (by default dev launches hand focus back to your app)")
    parser.add_argument("--sound", action="store_true",
                        help="Launch/run: keep the game's sound on (dev launches start muted)")
    parser.add_argument("--fullscreen", action="store_true",
                        help="Launch/run: use the player's display settings instead of windowed mode")
    parser.add_argument("--wait", type=int, default=DEFAULT_WAIT,
                        help=f"Run/launch/install/native/lease acquire: seconds to queue for the game lease "
                             f"(0 = don't wait); exits with code {LEASE_BUSY_EXIT} if it isn't free in time")
    parser.add_argument("--set", action="append", default=[], metavar="SECTION.KEY=VALUE",
                        help="Run/launch: start the game with this setting instead of its default (repeatable)")
    parser.add_argument("--keep-config", action="store_true",
                        help="Run/launch: keep PalaceMod.cfg as it is instead of starting from defaults")
    parser.add_argument("--title", help="Land: PR title when creating it (default: from the branch name)")
    parser.add_argument("--body-file", help="Land: PR description file, ending with your attribution footer; "
                                            "required when the PR doesn't exist yet")
    parser.add_argument("--dry-run", action="store_true",
                        help="Land: merge origin/main and build, but don't push, open or merge a PR")
    args = parser.parse_args()
    game = game_root(args.game)
    rest = args.args
    mode = rest[0] if rest else None
    if args.sound:
        os.environ["PALACEMOD_SOUND"] = "1"
    if args.set and args.keep_config:
        raise RuntimeError("--set starts from defaults, so it can't be combined with --keep-config.")
    recover()
    heartbeat(args.command)

    def prepare_config():
        if not args.keep_config:
            reset_mod_config(game, args.set)

    if args.command == "setup":
        setup(game)
    elif args.command == "build":
        build(args.configuration)
    elif args.command == "install":
        with game_lease(game, "install", args.wait):
            install(game, args.configuration)
    elif args.command == "run":
        build(args.configuration)
        with game_lease(game, "run", args.wait, args.force):
            stop_game(args.force)
            install(game, args.configuration)
            prepare_config()
            launch(game, args.timeout, args.fullscreen, not args.focus)
    elif args.command == "launch":
        with game_lease(game, "launch", args.wait, args.force):
            require_stopped()
            prepare_config()
            launch(game, args.timeout, args.fullscreen, not args.focus)
    elif args.command == "stop":
        release_lease(args.force)
    elif args.command == "logs":
        logs(game, args.errors, args.follow, args.lines)
    elif args.command == "screenshot":
        with game_lease(game, "screenshot"):
            screenshot(game, args.output)
    elif args.command == "backup-saves":
        print(backup_saves() or f"No save directory at {SAVE_DIR}")
    elif args.command == "watch":
        if args.install:
            with game_lease(game, "watch", args.wait):
                watch(game, args.configuration, True)
        else:
            watch(game, args.configuration, False)
    elif args.command == "decompile":
        decompile(game, args.type_name)
    elif args.command == "doctor":
        doctor(game, args.configuration)
    elif args.command == "cmd":
        if not rest:
            raise RuntimeError("Usage: dev.py cmd \"<command>\" (`dev.py cmd help` lists them)")
        with game_lease(game, "cmd"):
            command(game, " ".join(rest))
    elif args.command == "config":
        if mode not in ("get", "set"):
            raise RuntimeError("Usage: dev.py config get [Section[.Key]] | dev.py config set Section.Key value")
        with game_lease(game, "config"):
            config_command(game, mode, rest[1] if len(rest) > 1 else None,
                           " ".join(rest[2:]) if len(rest) > 2 else None)
    elif args.command == "lease":
        if mode == "acquire":
            with game_lease(game, "lease acquire", args.wait, args.force):
                pass
        elif mode == "release":
            release_lease(args.force)
        elif mode in (None, "status"):
            lease_status()
        else:
            raise RuntimeError("Usage: dev.py lease acquire|release|status")
    elif args.command == "land":
        land(args.title, args.body_file, args.dry_run)
    elif args.command == "native":
        if mode not in (None, "on", "off"):
            raise RuntimeError("Usage: dev.py native [on|off]")
        if mode is None:
            print("native" if is_native(game) else "rosetta")
        else:
            with game_lease(game, "native", args.wait):
                set_native(game, mode == "on")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except LeaseBusy as error:
        print(error, file=sys.stderr)
        sys.exit(LEASE_BUSY_EXIT)
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
