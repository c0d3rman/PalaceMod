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


def install(game, configuration, pids=game_pids, backup=True):
    if pids():
        raise RuntimeError("Thronefall is running; run `dev.py stop` first (or use `dev.py run`).")
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
    if destination.exists() and backup:
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
INSTANCE_BUNDLE = "com.palacemod.dev.thronefall"  # + the pool instance number


def frontmost_bundle():
    """Bundle ID of the frontmost app, via lsappinfo (needs no Accessibility permission)."""
    asn = subprocess.run(["lsappinfo", "front"], capture_output=True, text=True).stdout.strip()
    info = subprocess.run(["lsappinfo", "info", "-only", "bundleid", asn], capture_output=True, text=True).stdout
    for line in info.splitlines():
        line = line.strip()
        if line.startswith('bundleID="'):
            return line[len('bundleID="'):].rstrip('"')
    return None


def is_game_bundle(bundle):
    """The real game or any pool instance (see Game pool)."""
    return bundle is not None and (bundle == GAME_BUNDLE or bundle.startswith(INSTANCE_BUNDLE))


class FocusKeeper:
    """Hands focus back to the app that was in front whenever a game grabs it during startup."""

    def __init__(self, enabled):
        self.previous = frontmost_bundle() if enabled else None
        if is_game_bundle(self.previous):
            self.previous = None

    def check(self):
        if self.previous and is_game_bundle(frontmost_bundle()):
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
    wait_for_load(game, started, timeout, focus, game_pids, out)


def wait_for_load(game, started, timeout, focus, pids, out):
    log = log_path(game)
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
                report(game, pids)
                return
        if time.time() - started > 20 and not pids():
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


def report(game, pids=game_pids):
    log = log_path(game)
    text = log.read_text(errors="replace")
    loaded = "PalaceMod loaded!" in text
    print(f"PalaceMod loaded: {'yes' if loaded else 'NO'}  (game running: {'yes' if pids() else 'no'})")
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


def require_own_game():
    """The real install's game must be running and launched from this checkout."""
    if not game_pids():
        raise RuntimeError("Thronefall is not running.")
    if game_owner() != str(ROOT):
        raise RuntimeError("The running game was not launched by `dev.py launch`/`run` from this checkout; "
                           "relaunch it with `dev.py run`.")


def screenshot(game, output, timeout=15, require=require_own_game):
    """Ask the running mod (dev launches only) to capture the game frame; works without focus."""
    require()
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


def command(game, text, timeout=30, require=require_own_game):
    """Send a command to the running mod (dev launches only; `cmd help` lists them) and print its result."""
    require()
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
# A snapshot whose restore waits for a running game the lease didn't launch (the player's) to quit. Kept apart from
# USER_CONFIG so dev.py copies that predate it don't restore it under the live game.
DEFERRED_CONFIG = LEASE_DIR / "user-config-deferred"
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


def live_queue(queue_dir=QUEUE_DIR):
    """Waiting checkouts in arrival order, dropping entries whose dev.py process is gone."""
    queue_dir.mkdir(parents=True, exist_ok=True)
    waiters = []
    for entry in sorted(queue_dir.glob("*.json")):
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


def restore_user_config(snapshot=USER_CONFIG):
    """Put the player's BepInEx/config back exactly as snapshotted. Idempotent, so a crash part-way is redone."""
    manifest_path = snapshot / "manifest.json"
    if not manifest_path.exists():
        shutil.rmtree(snapshot, ignore_errors=True)
        return
    manifest = json.loads(manifest_path.read_text())
    target = config_dir(Path(manifest["game"]))
    target.mkdir(parents=True, exist_ok=True)
    for name in manifest["files"]:
        temp = target / f".{name}.restore.tmp"
        shutil.copy2(snapshot / "files" / name, temp)
        os.replace(temp, target / name)
    for extra in target.iterdir():
        if extra.is_file() and extra.name not in manifest["files"]:
            extra.unlink()
    shutil.rmtree(snapshot)
    print("Restored the player's BepInEx config.")


def restore_or_defer_user_config():
    """Restore the snapshot now, or, if a game is still running (one the lease didn't launch, so it was left
    alone), keep it until that game quits: restoring under a live game would roll back its settings mid-session."""
    if not USER_CONFIG.exists():
        return
    if not game_pids():
        restore_user_config()
        return
    if DEFERRED_CONFIG.exists():
        shutil.rmtree(USER_CONFIG)  # the older deferred snapshot predates this one, so it's the player's
    else:
        os.rename(USER_CONFIG, DEFERRED_CONFIG)
    print("Thronefall is still running, so the player's BepInEx config will be restored once it quits.")


def restore_deferred_config():
    """Restore a deferred snapshot once no game is running and no lease is held. Call under lease_lock."""
    if DEFERRED_CONFIG.exists() and not game_pids() and read_lease() is None:
        restore_user_config(DEFERRED_CONFIG)


def free_lease(lease, reason):
    """Stop a dev-launched game, restore the player's config and delete the lease. Call under lease_lock."""
    if lease:
        print(f"Releasing the game lease of {describe_holder(lease)}: {reason}", flush=True)
    stop_holders_game(lease)
    restore_or_defer_user_config()
    LEASE_FILE.unlink(missing_ok=True)


def stop_holders_game(lease, force=False):
    """Stop the game if the lease holder launched it. Anything else (the player's own game, or a checkout whose
    dev.py predates the lease) is left running unless forced."""
    if not game_pids():
        # A dev launch that crashed or was closed still left its windowed-mode prefs and its ownership marker; a
        # stale marker would make a later Steam launch look like a dev launch.
        OWNER.unlink(missing_ok=True)
        restore_display()
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
        restore_deferred_config()


def take_lease(game, command):
    restore_deferred_config()  # so the new snapshot is of the player's settings, not a test's
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
                OWNER.unlink(missing_ok=True)
                restore_display()
            restore_or_defer_user_config()
            restore_deferred_config()
            return
        if lease["root"] != str(ROOT) and not force:
            raise RuntimeError(f"The game lease is held by {describe_holder(lease)}, idle "
                               f"{duration(lease_idle(lease))}. Pass --force only if that session is gone.")
        if game_pids():
            stop_holders_game(lease, force)
        else:
            print("Thronefall is not running.")
            OWNER.unlink(missing_ok=True)
            restore_display()
        restore_or_defer_user_config()
        LEASE_FILE.unlink(missing_ok=True)
        restore_deferred_config()
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
    deferred = " (plus one waiting for the running game to quit)" if DEFERRED_CONFIG.exists() else ""
    print(f"Player config snapshot: {'held' if USER_CONFIG.exists() else 'none'}{deferred}")
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


def config_command(game, action, spec, value, pids=game_pids):
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
    running = " The running game reloads it within 2 s." if pids() else ""
    print(f"{section}.{key}: {current} -> {value}.{running}")


# --- Game pool --------------------------------------------------------------
#
# A pool of cloned game installs ("instances") that checkouts lease in parallel. An instance is an APFS clone of the
# real install (near-free on disk) with its own executable name (ThronefallDev<N>, so `pgrep -x Thronefall`, which
# the real-install lease and older dev.py copies rely on, never sees it), its own bundle ID (so its PlayerPrefs are
# a separate preferences domain) and its own home for Unity's per-user files (CFFIXED_USER_HOME points the game's
# persistentDataPath, saves and Player.log at <instance>/home). Its BepInEx folder holds its own DLL, config, log
# and dev command channel. Nothing a pool launch does touches the player's saves, prefs or BepInEx config, so the
# pool needs none of the real-install lease's snapshot-and-restore logic.
#
# Leases work like the real-install lease: `run` takes any free instance (FIFO queue when all are busy), every
# dev.py call from the holder refreshes its heartbeat, and a lease idle for LEASE_IDLE_LIMIT is reaped by the next
# dev.py call from any checkout. The state lives in POOL_DIR under its own lock, apart from the real-install lease.
# `--real` uses the real install and its lease instead; `--solo` also drains the pool first and keeps new pool
# leases waiting until it's released, for measurements that need an otherwise idle machine.

POOL_DIR = DEV_HOME / "pool"
POOL_LOCK = POOL_DIR / ".lock"
POOL_QUEUE = POOL_DIR / "queue"
POOL_CONFIG = POOL_DIR / "config.json"
SOLO_FILE = POOL_DIR / "solo.json"
INSTANCES_HOME = Path(os.environ.get("PALACEMOD_INSTANCES",
                                     Path.home() / "Library/Application Support/PalaceMod-dev/instances"))
POOL_DEFAULT_SIZE = 6  # measured: ~0.35 core and ~1.2 GB per idle-ish instance (background 15 fps cap)
INSTANCE_PROCESS = "ThronefallDev"
LAST_INSTANCE = ROOT / ".local/pool-instance"  # so `logs` still finds the log after `stop`
# Files whose change means the base install changed (a game update, `setup`, `native on|off`), so instances
# are recloned.
BASE_FINGERPRINT_FILES = ["Thronefall.app/Contents/Info.plist", "Thronefall.app/Contents/MacOS/Thronefall",
                          "Thronefall.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll",
                          "Thronefall.app/Contents/Resources/Data/globalgamemanagers", "run_bepinex.sh",
                          "libdoorstop.dylib", "doorstop_config.ini", "BepInEx/core/BepInEx.dll",
                          "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/PalaceMod.Bootstrap.dll",
                          "BepInEx/core/libpalacejit.dylib", "BepInEx/config/BepInEx.cfg"]


class Instance:
    def __init__(self, number):
        self.number = number
        self.dir = INSTANCES_HOME / str(number)
        self.game = self.dir / "game"
        self.home = self.dir / "home"
        self.saves = self.home / SAVE_DIR.relative_to(Path.home())
        self.meta = self.dir / "instance.json"
        self.process = f"{INSTANCE_PROCESS}{number}"
        self.bundle = f"{INSTANCE_BUNDLE}{number}"
        self.lease_file = POOL_DIR / f"lease-{number}.json"

    def __str__(self):
        return f"instance {self.number}"

    def pids(self):
        result = subprocess.run(["pgrep", "-x", self.process], capture_output=True, text=True)
        return [int(pid) for pid in result.stdout.split()]

    def lease(self):
        try:
            return json.loads(self.lease_file.read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            return None

    def write_lease(self, lease):
        write_atomic(self.lease_file, json.dumps(lease, indent=2) + "\n")

    def info(self):
        try:
            return json.loads(self.meta.read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            return {}

    def require_running(self):
        if not self.pids():
            raise RuntimeError(f"Thronefall isn't running on your pool {self}; start it with `dev.py run`.")


def pool_size():
    try:
        return max(1, int(json.loads(POOL_CONFIG.read_text())["size"]))
    except (FileNotFoundError, json.JSONDecodeError, KeyError, ValueError):
        return POOL_DEFAULT_SIZE


def pool_numbers():
    """Every instance number that exists on disk, has a lease, or is within the pool size."""
    numbers = set(range(1, pool_size() + 1))
    if INSTANCES_HOME.is_dir():
        numbers |= {int(p.name) for p in INSTANCES_HOME.iterdir() if p.name.isdigit()}
    if POOL_DIR.is_dir():
        numbers |= {int(p.stem.split("-")[1]) for p in POOL_DIR.glob("lease-*.json") if p.stem.split("-")[1].isdigit()}
    return sorted(numbers)


@contextlib.contextmanager
def file_lock(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


def pool_lock():
    """Serialises every read-modify-write of the pool's leases, queue and solo marker."""
    return file_lock(POOL_LOCK)


def stop_instance(instance):
    pids = instance.pids()
    if not pids:
        return
    for pid in pids:
        with contextlib.suppress(ProcessLookupError):
            os.kill(pid, signal.SIGTERM)
    for _ in range(30):
        if not instance.pids():
            print(f"Stopped Thronefall on pool {instance}.")
            return
        time.sleep(0.5)
    for pid in instance.pids():
        with contextlib.suppress(ProcessLookupError):
            os.kill(pid, signal.SIGKILL)
    time.sleep(1)
    print(f"Force-stopped Thronefall on pool {instance}.")


def pool_reap():
    """Free leases idle past the limit, stopping their games. Call under pool_lock."""
    for number in pool_numbers():
        instance = Instance(number)
        lease = instance.lease()
        if lease is not None and lease_stale(lease):
            print(f"Releasing pool {instance} of {describe_holder(lease)}: idle {duration(lease_idle(lease))}",
                  flush=True)
            stop_instance(instance)
            instance.lease_file.unlink(missing_ok=True)


def my_instance():
    """The pool instance this checkout holds, or None."""
    if not POOL_DIR.is_dir():
        return None
    for number in pool_numbers():
        lease = Instance(number).lease()
        if lease is not None and lease["root"] == str(ROOT) and not lease_stale(lease):
            return Instance(number)
    return None


def pool_heartbeat(command=None):
    if not POOL_DIR.is_dir():
        return
    with pool_lock():
        instance = my_instance()
        if instance is None:
            return
        lease = instance.lease()
        lease["heartbeat"] = time.time()
        if command:
            lease["command"] = command
        instance.write_lease(lease)


def solo_active():
    """Whether a `--solo` session is draining the pool or running. Call under pool_lock."""
    try:
        solo = json.loads(SOLO_FILE.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return None
    lease = read_lease()
    if pid_alive(solo["pid"]) or (lease is not None and lease["root"] == solo["root"] and not lease_stale(lease)):
        return solo
    SOLO_FILE.unlink(missing_ok=True)
    return None


def clear_solo():
    if not POOL_DIR.is_dir():
        return
    with pool_lock():
        solo = solo_active()
        if solo is not None and solo["root"] == str(ROOT):
            SOLO_FILE.unlink(missing_ok=True)


def pool_acquire(command, wait):
    """Return the instance this checkout holds, queueing (FIFO) for a free one for up to `wait` seconds."""
    ticket = None
    deadline = time.time() + wait
    shown = None
    try:
        while True:
            with pool_lock():
                pool_reap()
                mine = my_instance()
                if mine is not None:
                    lease = mine.lease()
                    lease["heartbeat"] = time.time()
                    lease["command"] = command
                    mine.write_lease(lease)
                    return mine
                waiters = live_queue(POOL_QUEUE)
                ahead = [w for w in waiters if ticket is None or w[0].name < ticket.name]
                free = [Instance(n) for n in range(1, pool_size() + 1) if Instance(n).lease() is None]
                solo = solo_active()
                if len(ahead) < len(free) and solo is None:
                    # Prefer the instance this checkout used last: its build and data are already in place.
                    free.sort(key=lambda i: (i.info().get("last_root") != str(ROOT), i.number))
                    instance = free[0]
                    now = time.time()
                    instance.write_lease({"root": str(ROOT), "branch": current_branch(), "pid": os.getpid(),
                                          "command": command, "acquired": now, "heartbeat": now,
                                          "instance": instance.number, "seeded": False})
                    LAST_INSTANCE.parent.mkdir(exist_ok=True)
                    LAST_INSTANCE.write_text(f"{instance.number}\n")
                    print(f"Acquired pool {instance} (release it with `dev.py stop`; it expires after "
                          f"{LEASE_IDLE_LIMIT // 60} min idle).", flush=True)
                    return instance
                if ticket is None:
                    POOL_QUEUE.mkdir(parents=True, exist_ok=True)
                    ticket = POOL_QUEUE / f"{time.time_ns()}-{os.getpid()}.json"
                    ticket.write_text(json.dumps({"root": str(ROOT), "branch": current_branch(), "pid": os.getpid(),
                                                  "since": time.time()}) + "\n")
                status = (f"a --solo session from {solo['root']} has the machine" if solo
                          else f"all {pool_size()} instances are busy" if not free
                          else "other checkouts are ahead")
                position = len(ahead) + 1
            if wait <= 0 or time.time() >= deadline:
                raise LeaseBusy(f"No pool instance free: {status}; gave up waiting (exit code {LEASE_BUSY_EXIT}). "
                                "Retry later, or wait longer with --wait SECONDS.")
            if (position, status) != shown:
                shown = (position, status)
                print(f"Waiting for a pool instance: {status}. You are #{position} in the queue.", flush=True)
            os.utime(ticket)
            time.sleep(2)
    finally:
        if ticket is not None:
            ticket.unlink(missing_ok=True)


@contextlib.contextmanager
def pool_session(command, wait=None):
    """Like game_lease, for a pool instance. wait=None requires one already held."""
    if wait is None:
        instance = my_instance()
        if instance is None:
            raise RuntimeError("This checkout doesn't hold a pool instance. Start with `dev.py run` "
                               "(or `dev.py lease acquire`).")
        pool_heartbeat(command)
    else:
        instance = pool_acquire(command, wait)
    done = threading.Event()

    def beat():
        while not done.wait(HEARTBEAT_SECONDS):
            pool_heartbeat()

    thread = threading.Thread(target=beat, daemon=True)
    thread.start()
    try:
        yield instance
    finally:
        done.set()
        thread.join()
        pool_heartbeat()


def pool_release(instance):
    with pool_lock():
        lease = instance.lease()
        if lease is None or lease["root"] != str(ROOT):
            return
        if instance.pids():
            stop_instance(instance)
        else:
            print(f"Thronefall is not running on pool {instance}.")
        instance.lease_file.unlink(missing_ok=True)
        print(f"Released pool {instance}.")


def drain_pool(wait):
    """--solo: keep new pool leases waiting, then wait until every pool instance is released."""
    deadline = time.time() + wait
    with pool_lock():
        solo = solo_active()
        if solo is not None and solo["root"] != str(ROOT):
            raise LeaseBusy(f"Another --solo session ({solo['root']}) is active; retry once it stops.")
        POOL_DIR.mkdir(parents=True, exist_ok=True)
        write_atomic(SOLO_FILE, json.dumps({"root": str(ROOT), "pid": os.getpid(), "since": time.time()}) + "\n")
    mine = my_instance()
    if mine is not None:
        pool_release(mine)
    shown = None
    while True:
        with pool_lock():
            pool_reap()
            busy = [(n, Instance(n).lease()) for n in pool_numbers() if Instance(n).lease() is not None]
        if not busy:
            return
        if wait <= 0 or time.time() >= deadline:
            clear_solo()
            raise LeaseBusy(f"{len(busy)} pool instance(s) still leased; gave up waiting for the pool to drain "
                            f"(exit code {LEASE_BUSY_EXIT}).")
        if len(busy) != shown:
            shown = len(busy)
            print(f"--solo: new pool leases are on hold; waiting for {len(busy)} leased instance(s): "
                  + ", ".join(f"{n} ({describe_holder(lease)})" for n, lease in busy), flush=True)
        time.sleep(2)


# Building and seeding instances.

def base_fingerprint(base):
    digest = hashlib.sha256()
    for name in BASE_FINGERPRINT_FILES:
        path = base / name
        stat = path.stat() if path.exists() else None
        digest.update(f"{name}:{stat and stat.st_size}:{stat and stat.st_mtime_ns}\n".encode())
    return digest.hexdigest()[:16]


def instance_lock(instance):
    return file_lock(INSTANCES_HOME / f".{instance.number}.lock")


def build_instance(instance, base):
    """Clone the base install into the instance (APFS clone, so ~free) and make it a separate app."""
    fingerprint = base_fingerprint(base)
    instance.dir.mkdir(parents=True, exist_ok=True)
    temp = instance.dir / "game.new"
    shutil.rmtree(temp, ignore_errors=True)
    print(f"Cloning the game into pool {instance}...", flush=True)
    subprocess.run(["cp", "-c", "-R", str(base), str(temp)], check=True)
    # Drop what belongs to whoever uses the real install: the installed mod, the player's mod config, logs and
    # dev channel files. Each run installs its own build and starts from default settings.
    bepinex = temp / "BepInEx"
    for name in ["plugins/PalaceMod", "PalaceMod-dev"]:
        shutil.rmtree(bepinex / name, ignore_errors=True)
    for name in ["LogOutput.log", "gc-stats.log", "PalaceMod.Bootstrap.log", "config/PalaceMod.cfg"]:
        (bepinex / name).unlink(missing_ok=True)
    for crash in temp.glob("mono_crash*"):
        crash.unlink()
    app = temp / "Thronefall.app"
    plist = app / "Contents/Info.plist"
    executable = subprocess.run(["plutil", "-extract", "CFBundleExecutable", "raw", str(plist)],
                                capture_output=True, text=True, check=True).stdout.strip()
    os.rename(app / "Contents/MacOS" / executable, app / "Contents/MacOS" / instance.process)
    subprocess.run(["plutil", "-replace", "CFBundleExecutable", "-string", instance.process, str(plist)], check=True)
    subprocess.run(["plutil", "-replace", "CFBundleIdentifier", "-string", instance.bundle, str(plist)], check=True)
    subprocess.run(["codesign", "--force", "--deep", "-s", "-", str(app)], check=True, capture_output=True)
    shutil.rmtree(instance.game, ignore_errors=True)
    os.rename(temp, instance.game)
    info = instance.info()
    info.update({"fingerprint": fingerprint, "base": str(base), "built": stamp()})
    write_atomic(instance.meta, json.dumps(info, indent=2) + "\n")


def ensure_instance(instance, base):
    """Build the instance if it's missing, or reclone it if the base install changed. Its game must be stopped."""
    with instance_lock(instance):
        info = instance.info()
        if instance.game.is_dir() and info.get("fingerprint") == base_fingerprint(base):
            return False
        if instance.pids():
            raise RuntimeError(f"Pool {instance} is out of date but its game is running; stop it first.")
        build_instance(instance, base)
        return True


def seed_instance(instance):
    """Give the instance a copy of the player's current saves and preferences (so levels are unlocked), in
    windowed mode. The player's files are only read."""
    with instance_lock(instance):
        shutil.rmtree(instance.saves, ignore_errors=True)
        instance.saves.parent.mkdir(parents=True, exist_ok=True)
        if SAVE_DIR.is_dir():
            shutil.copytree(SAVE_DIR, instance.saves)
        else:
            instance.saves.mkdir()
        subprocess.run(["defaults", "delete", instance.bundle], capture_output=True)
        exported = subprocess.run(["defaults", "export", PREFS_DOMAIN, "-"], capture_output=True)
        if exported.returncode == 0 and exported.stdout:
            subprocess.run(["defaults", "import", instance.bundle, "-"], input=exported.stdout, check=True,
                           capture_output=True)
        # The game only accepts resolutions from the display's mode list, so instances keep the player's.
        for key, value in WINDOWED_PREFS.items():
            subprocess.run(["defaults", "write", instance.bundle, key, "-int", str(value)], check=True)
        info = instance.info()
        info["seeded"] = stamp()
        instance.dir.mkdir(parents=True, exist_ok=True)
        write_atomic(instance.meta, json.dumps(info, indent=2) + "\n")


def prepare_instance(instance, base):
    """Before a launch: stop the instance's game, bring it up to date with the base install, and seed its data on
    the first launch of this lease."""
    stop_instance(instance)
    built = ensure_instance(instance, base)
    lease = instance.lease()
    if built or not lease.get("seeded"):
        seed_instance(instance)
        with pool_lock():
            lease = instance.lease()
            lease["seeded"] = True
            instance.write_lease(lease)
    info = instance.info()
    info["last_root"] = str(ROOT)
    write_atomic(instance.meta, json.dumps(info, indent=2) + "\n")


def launch_instance(instance, timeout, keep_focus=True):
    if instance.pids():
        raise RuntimeError(f"Thronefall is already running on pool {instance}; `dev.py run` restarts it.")
    focus = FocusKeeper(keep_focus)
    started = time.time()
    out = ROOT / ".local/launch.log"
    out.parent.mkdir(exist_ok=True)
    with out.open("w") as handle:
        env = {**os.environ, "PALACEMOD_DEV": "1", "CFFIXED_USER_HOME": str(instance.home)}
        subprocess.Popen(["/bin/sh", str(instance.game / "run_bepinex.sh")], cwd=instance.game, stdout=handle,
                         stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True, env=env)
    arch = "natively" if is_native(instance.game) else "under Rosetta"
    print(f"Launching Thronefall on pool {instance} {arch} (windowed)...", flush=True)
    wait_for_load(instance.game, started, timeout, focus, instance.pids, out)


def instances_command(base, action, targets):
    """`dev.py instances create [COUNT] | list | reset [N|all] | remove [N|all]`."""
    if action in (None, "list"):
        pool_status()
        return
    if action == "create":
        if targets:
            POOL_DIR.mkdir(parents=True, exist_ok=True)
            write_atomic(POOL_CONFIG, json.dumps({"size": max(1, int(targets[0]))}) + "\n")
        for number in range(1, pool_size() + 1):
            instance = Instance(number)
            lease = instance.lease()
            if lease is not None and lease["root"] != str(ROOT) and not lease_stale(lease):
                print(f"Pool {instance} is leased by {describe_holder(lease)}; it updates on its next run.")
                continue
            if ensure_instance(instance, base) or not instance.saves.exists():
                seed_instance(instance)
        extra = [n for n in pool_numbers() if n > pool_size() and Instance(n).dir.exists()]
        if extra:
            instances_command(base, "remove", [str(n) for n in extra])
        print(f"Pool size {pool_size()}; all instances are up to date.")
        return
    if action not in ("reset", "remove"):
        raise RuntimeError("Usage: dev.py instances create [COUNT] | list | reset [N|all] | remove [N|all]")
    if not targets or targets == ["all"]:
        numbers = [n for n in pool_numbers() if Instance(n).dir.exists()]
    else:
        numbers = [int(t) for t in targets]
    for number in numbers:
        instance = Instance(number)
        with pool_lock():
            lease = instance.lease()
            if lease is not None and lease["root"] != str(ROOT) and not lease_stale(lease):
                print(f"Skipping pool {instance}: leased by {describe_holder(lease)}.")
                continue
            # Hold it while working so nobody acquires it mid-way.
            now = time.time()
            instance.write_lease({"root": str(ROOT), "branch": current_branch(), "pid": os.getpid(),
                                  "command": f"instances {action}", "acquired": now, "heartbeat": now,
                                  "instance": number, "seeded": False})
        try:
            stop_instance(instance)
            if action == "reset":
                if instance.game.is_dir():
                    seed_instance(instance)
                    print(f"Reset pool {instance}'s saves and preferences from the player's.")
            else:
                with instance_lock(instance):
                    shutil.rmtree(instance.dir, ignore_errors=True)
                    subprocess.run(["defaults", "delete", instance.bundle], capture_output=True)
                print(f"Removed pool {instance}.")
        finally:
            with pool_lock():
                if lease is None or lease["root"] != str(ROOT):
                    instance.lease_file.unlink(missing_ok=True)


def pool_status():
    with pool_lock() if POOL_DIR.is_dir() else contextlib.nullcontext():
        solo = solo_active() if POOL_DIR.is_dir() else None
        waiters = live_queue(POOL_QUEUE) if POOL_DIR.is_dir() else []
    base = game_root()
    fingerprint = base_fingerprint(base)
    print(f"Pool: {pool_size()} instances in {INSTANCES_HOME}")
    for number in pool_numbers():
        instance = Instance(number)
        lease = instance.lease()
        info = instance.info()
        state = ("not built" if not instance.game.is_dir()
                 else "up to date" if info.get("fingerprint") == fingerprint else "outdated (reclones on next run)")
        holder = "free" if lease is None else (
            f"held by {describe_holder(lease)}, last command `{lease.get('command')}`, idle "
            f"{duration(lease_idle(lease))}{' (STALE)' if lease_stale(lease) else ''}")
        running = ", game running" if instance.pids() else ""
        extra = "" if number <= pool_size() else " [beyond pool size]"
        print(f"  {number}: {holder}{running}; {state}{extra}")
    print(f"Pool queue: {len(waiters)} waiting")
    for i, (_, data) in enumerate(waiters, 1):
        branch = f" ({data['branch']})" if data.get("branch") else ""
        print(f"  {i}. {data['root']}{branch}, waiting {duration(time.time() - data['since'])}")
    if solo:
        print(f"Solo: {solo['root']} holds the machine; new pool leases wait.")


def pool_recover():
    if POOL_DIR.is_dir():
        with pool_lock():
            pool_reap()


def stop_command(force, real):
    """`stop` / `lease release`: release this checkout's pool instance and/or its real-install lease."""
    instance = my_instance()
    if instance is not None and not real:
        pool_release(instance)
    lease = read_lease()
    owns_real = (lease is not None and lease["root"] == str(ROOT)) or game_owner() == str(ROOT)
    if real or force or owns_real or (instance is None and lease is None and not game_pids()):
        release_lease(force)
    elif instance is None:
        print("This checkout holds no pool instance or game lease; nothing to stop.")
    clear_solo()


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


def watch(game, configuration, deploy, pids=game_pids):
    previous = None
    while True:
        current = snapshot()
        if current != previous:
            previous = current
            try:
                build(configuration)
                if deploy:
                    install(game, configuration, pids, backup=pids is game_pids)
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
                                            "cmd", "config", "lease", "land", "instances"])
    parser.add_argument("args", nargs="*",
                        help="native: on|off. cmd: the command, e.g. \"level Neuland\". "
                             "config: get [Section[.Key]] | set Section.Key value. lease: acquire|release|status. "
                             "instances: create [COUNT] | list | reset [N|all] | remove [N|all]")
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
                        help="Launch/run (with --real): use the player's display settings instead of windowed mode")
    parser.add_argument("--real", action="store_true",
                        help="Use the real game install and its single lease instead of a pool instance")
    parser.add_argument("--solo", action="store_true",
                        help="Run/launch/lease acquire: like --real, but first wait for every pool instance to be "
                             "released and hold off new pool leases until you stop (for performance measurements)")
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
    real = args.real or args.solo
    recover()
    pool_recover()
    heartbeat(args.command)
    pool_heartbeat(args.command)
    if args.fullscreen and not real and args.command in ("run", "launch"):
        raise RuntimeError("Pool instances are always windowed; add --real to launch the real install fullscreen.")
    # Commands that act on a running game go to this checkout's pool instance if it holds one.
    instance = None if args.real else my_instance()

    def prepare_config(target):
        if not args.keep_config:
            reset_mod_config(target, args.set)

    @contextlib.contextmanager
    def real_lease(command):
        """The real install's lease, after draining the pool for --solo."""
        if args.solo:
            drain_pool(args.wait)
        try:
            with game_lease(game, command, args.wait, args.force):
                yield
        except BaseException:
            if args.solo and not holds_lease():
                clear_solo()
            raise

    if args.command == "setup":
        setup(game)
    elif args.command == "build":
        build(args.configuration)
    elif args.command == "install":
        if real:
            with real_lease("install"):
                install(game, args.configuration)
        else:
            with pool_session("install", args.wait) as inst:
                stop_instance(inst)
                ensure_instance(inst, game)
                install(inst.game, args.configuration, inst.pids, backup=False)
    elif args.command == "run":
        build(args.configuration)
        if real:
            with real_lease("run"):
                stop_game(args.force)
                install(game, args.configuration)
                prepare_config(game)
                launch(game, args.timeout, args.fullscreen, not args.focus)
        else:
            with pool_session("run", args.wait) as inst:
                prepare_instance(inst, game)
                install(inst.game, args.configuration, inst.pids, backup=False)
                prepare_config(inst.game)
                launch_instance(inst, args.timeout, not args.focus)
    elif args.command == "launch":
        if real:
            with real_lease("launch"):
                require_stopped()
                prepare_config(game)
                launch(game, args.timeout, args.fullscreen, not args.focus)
        else:
            with pool_session("launch", args.wait) as inst:
                prepare_instance(inst, game)
                prepare_config(inst.game)
                launch_instance(inst, args.timeout, not args.focus)
    elif args.command == "stop":
        stop_command(args.force, args.real)
    elif args.command == "logs":
        target = game
        if not args.real:
            last = instance.number if instance else (
                int(LAST_INSTANCE.read_text()) if LAST_INSTANCE.exists() and not holds_lease() else None)
            target = Instance(last).game if last else game
        logs(target, args.errors, args.follow, args.lines)
    elif args.command == "screenshot":
        if instance:
            with pool_session("screenshot") as inst:
                screenshot(inst.game, args.output, require=inst.require_running)
        else:
            with game_lease(game, "screenshot"):
                screenshot(game, args.output)
    elif args.command == "backup-saves":
        print(backup_saves() or f"No save directory at {SAVE_DIR}")
    elif args.command == "watch":
        if args.install and real:
            with real_lease("watch"):
                watch(game, args.configuration, True)
        elif args.install:
            with pool_session("watch", args.wait) as inst:
                ensure_instance(inst, game)
                watch(inst.game, args.configuration, True, inst.pids)
        else:
            watch(game, args.configuration, False)
    elif args.command == "decompile":
        decompile(game, args.type_name)
    elif args.command == "doctor":
        doctor(game, args.configuration)
    elif args.command == "cmd":
        if not rest:
            raise RuntimeError("Usage: dev.py cmd \"<command>\" (`dev.py cmd help` lists them)")
        if instance:
            with pool_session("cmd") as inst:
                command(inst.game, " ".join(rest), require=inst.require_running)
        else:
            with game_lease(game, "cmd"):
                command(game, " ".join(rest))
    elif args.command == "config":
        if mode not in ("get", "set"):
            raise RuntimeError("Usage: dev.py config get [Section[.Key]] | dev.py config set Section.Key value")
        spec, value = rest[1] if len(rest) > 1 else None, " ".join(rest[2:]) if len(rest) > 2 else None
        if instance:
            with pool_session("config") as inst:
                config_command(inst.game, mode, spec, value, inst.pids)
        else:
            with game_lease(game, "config"):
                config_command(game, mode, spec, value)
    elif args.command == "lease":
        if mode == "acquire":
            if real:
                with real_lease("lease acquire"):
                    pass
            else:
                with pool_session("lease acquire", args.wait):
                    pass
        elif mode == "release":
            stop_command(args.force, args.real)
        elif mode in (None, "status"):
            lease_status()
            pool_status()
        else:
            raise RuntimeError("Usage: dev.py lease acquire|release|status")
    elif args.command == "instances":
        instances_command(game, mode, rest[1:])
    elif args.command == "land":
        land(args.title, args.body_file, args.dry_run)
    elif args.command == "native":
        if mode not in (None, "on", "off"):
            raise RuntimeError("Usage: dev.py native [on|off]")
        if mode is None:
            print("native" if is_native(game) else "rosetta")
        else:
            with real_lease("native"):
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
