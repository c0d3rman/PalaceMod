#!/usr/bin/env python3
"""Builds a level in the Studio the way a creator would, by mouse, then plays it.

    python3 scripts/studio_mouse.py tests/studio-mouse/ex21.txt [--out DIR] [--fixture-game <instance game dir>]

A script (tests/studio-mouse/*.txt) is one dev command per line, run on your windowed pool instance (`dev.py run`):
only commands that click real UI rows and tiles (`builder-logic press`, `builder-entity press`, `builder-studio press`),
type into the field a click opened (`uikit type ... ` with _ for spaces, then `uikit type enter`; `builder-entity type`)
or drive the Studio's pointer (`builder-studio cursor/click/dragw`). No JSON is pasted. `sleep N` waits, `shot NAME`
saves a screenshot to --out, `#` lines are comments. The Studio's draft (`builder-studio json pack`) is then written
as a logic fixture with the script's play steps (<name>.play.json beside it); with --fixture-game it runs headless
through `palace.py logic fixture` on that instance (`dev.py run --headless --slot fx --set "Agent bridge.Enabled=true"`).
Start each script on a freshly launched instance: reopening the Studio after another level can crash the game.
"""
import argparse, json, shutil, subprocess, sys, time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def cmd(c):
    r = subprocess.run([sys.executable, "scripts/dev.py", "cmd", c], cwd=ROOT, capture_output=True, text=True)
    return (r.stdout + r.stderr).strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("script")
    ap.add_argument("--out", default=str(ROOT / ".local" / "studio-mouse"))
    ap.add_argument("--fixture-game")
    a = ap.parse_args()
    script = Path(a.script)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    for raw in script.read_text().splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("sleep "):
            time.sleep(float(line.split()[1]))
            continue
        if line.startswith("shot "):
            time.sleep(1.5)
            r = subprocess.run([sys.executable, "scripts/dev.py", "screenshot"], cwd=ROOT, capture_output=True, text=True)
            shutil.copy(r.stdout.strip().splitlines()[-1], out / (line.split()[1] + ".png"))
            continue
        res = cmd(line)
        print(f"> {line}\n  {res.splitlines()[0][:160] if res else ''}", flush=True)
        if "Command failed" in res or "isn't running" in res:
            sys.exit(f"failed at: {line}\n{res}")
        time.sleep(0.35)
    res = cmd("builder-studio json pack")
    pack = json.loads(res[res.index("{"):])
    play_file = script.with_suffix(".play.json")
    play = json.loads(play_file.read_text()) if play_file.exists() else {"steps": []}
    fixture = out / (script.stem + ".fixture.json")
    fixture.write_text(json.dumps({"title": f"built by mouse: {script.name}", "pack": pack, "play": play}, indent=1))
    print("wrote", fixture)
    if a.fixture_game:
        r = subprocess.run([sys.executable, "agent-kit/palace.py", "--game", a.fixture_game, "logic", "fixture", str(fixture)], cwd=ROOT, capture_output=True, text=True)
        t = r.stdout
        verdict = json.loads(t[t.index("{"):])
        for s in verdict.get("steps", []):
            print(" ", {k: v for k, v in s.items() if k != "step"})
        print("OK" if verdict.get("ok") else "FAIL")
        sys.exit(0 if verdict.get("ok") else 1)


main()
