#!/usr/bin/env python3
"""Runs several `palace.py test`s of one library level at once, each on its own headless pool instance.

    python3 scripts/partest.py <level> --plans a.json b.json c.json [--from N] [--out DIR] [-- <palace.py test args>]

Each plan gets a pool lease of its own (`dev.py --slot partest-<i>`), launched with `--headless` (no window) and the
Agent bridge on, with this checkout's installed build. The level (and the library enemies it uses) is copied from
the library of instance N (`--from`, default: the instance this checkout holds) into each test instance's library;
a different file of the same name there is moved to library/trash first. Tests run with `--fast` unless the extra
arguments say `--no-fast`. Results go to DIR/<plan>.json (+ .err with the per-night lines); a summary is printed and
every lease is released at the end (`--keep` leaves the instances running for another batch).
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEV = [sys.executable, str(ROOT / "scripts/dev.py")]
PALACE = [sys.executable, str(ROOT / "agent-kit/palace.py")]
sys.path.insert(0, str(ROOT / "scripts"))
import dev  # noqa: E402  (paths and pool layout only)


def dev_run(*args, slot=None):
    cmd = DEV + list(args) + (["--slot", slot] if slot else [])
    return subprocess.run(cmd, capture_output=True, text=True)


def library(game):
    return game / "BepInEx" / "PalaceBuilder" / "library"


def copy_item(source, target):
    """Copies a library file, moving a different file of the same name out of the way (library/trash)."""
    if target.exists():
        if target.read_bytes() == source.read_bytes():
            return
        trash = target.parent.parent / "trash"
        trash.mkdir(parents=True, exist_ok=True)
        shutil.move(target, trash / f"{target.parent.name[:-1]}-{target.stem}-{time.strftime('%Y%m%d-%H%M%S')}.json")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def level_files(lib, level):
    """The level's file plus the library enemies its waves use."""
    f = lib / "levels" / f"{level}.json"
    if not f.exists():
        raise SystemExit(f"No library level {level!r} in {lib}")
    data = json.loads(f.read_text())
    units = {str(sp.get("unit", "")).split(":")[-1] for ws in data.get("waveSets") or []
             for night in ws.get("nights") or [] for sp in night.get("spawns") or []}
    enemies = [lib / "enemies" / f"{u}.json" for u in sorted(units) if (lib / "enemies" / f"{u}.json").exists()]
    return [f] + enemies


def summary_line(path):
    try:
        r = json.loads(path.read_text())
    except (OSError, ValueError):
        return "no report"
    nights = " ".join(f"n{n['night']}:{(n.get('outcome') or '?')[0]} castle {round(n.get('castleMin') or 0)} ko {n.get('knockedOut')}"
                      for n in r.get("summary", []))
    return f"{r.get('result')} after {r.get('nightsPlayed')} nights | {nights}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("level", help="library level id")
    ap.add_argument("--plans", nargs="+", required=True, help="plan files, one test (and instance) each")
    ap.add_argument("--from", dest="source", type=int, help="pool instance whose library has the level (default: yours)")
    ap.add_argument("--out", default=str(ROOT / ".local/partest"), help="folder for the reports")
    ap.add_argument("--keep", action="store_true", help="leave the instances running (release with dev.py stop --slot)")
    ap.add_argument("--wait", type=int, default=600, help="seconds to queue for each instance")
    argv = sys.argv[1:]
    extra = []
    if "--" in argv:
        i = argv.index("--")
        argv, extra = argv[:i], argv[i + 1:]
    args = ap.parse_args(argv)
    if args.source:
        source = dev.Instance(args.source).game
    else:
        mine = dev.my_instance()
        if mine is None:
            raise SystemExit("This checkout holds no pool instance; say which library to copy with --from N.")
        source = mine.game
    files = level_files(library(source), args.level)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    if "--no-fast" not in extra and "--fast" not in extra:
        extra.append("--fast")

    built = subprocess.run(DEV + ["build"], capture_output=True, text=True)
    if built.returncode != 0:
        raise SystemExit("build failed:\n" + built.stdout[-2000:])

    results = {}
    lock = threading.Lock()

    def say(text):
        with lock:
            print(text, flush=True)

    def one(i, plan):
        slot = f"partest-{i + 1}"
        name = Path(plan).stem
        started = time.time()
        r = dev_run("install", "--wait", str(args.wait), slot=slot)
        if r.returncode == 0:
            r = dev_run("launch", "--headless", "--wait", "0", "--set", "Agent bridge.Enabled=true", slot=slot)
        m = re.search(r"on pool instance (\d+)", r.stdout)
        if r.returncode != 0 or not m or "PalaceMod loaded: yes" not in r.stdout:
            results[plan] = f"launch failed: {(r.stdout + r.stderr).strip()[-400:]}"
            say(f"{name}: {results[plan]}")
            return
        game = dev.Instance(int(m.group(1))).game
        for f in files:
            copy_item(f, library(game) / f.parent.name / f.name)
        say(f"{name}: testing on pool instance {m.group(1)}")
        report = out / f"{name}.json"
        with open(report, "w") as stdout, open(out / f"{name}.err", "w") as stderr:
            subprocess.run(PALACE + ["--game", str(game), "test", "library", args.level, "--plan", str(Path(plan).resolve()),
                                     *extra], stdout=stdout, stderr=stderr)
        results[plan] = f"{summary_line(report)} ({time.time() - started:.0f} s)"
        say(f"{name}: {results[plan]}")
        if not args.keep:
            dev_run("stop", slot=slot)

    # Stagger the launches a little (each clones/seeds its instance); the tests themselves overlap.
    threads = []
    for i, plan in enumerate(args.plans):
        t = threading.Thread(target=one, args=(i, plan))
        t.start()
        threads.append(t)
        time.sleep(5)
    for t in threads:
        t.join()
    print(f"\nReports in {out}:")
    for plan in args.plans:
        print(f"  {Path(plan).stem}: {results.get(plan)}")


if __name__ == "__main__":
    main()
