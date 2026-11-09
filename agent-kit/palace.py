#!/usr/bin/env python3
"""palace.py: build, check, picture and test PalaceMod Builder levels for Thronefall from the command line.

Made for coding agents. Python 3.8+, standard library only, any OS. Read level-building.md (next to this file) first.

    python3 palace.py where                        # find the game, packs folder and bridge
    python3 palace.py ref build                    # (game running, Agent bridge on) cache map + unit data
    python3 palace.py new gorge --host Nordfels --base cleared --waves empty   # a level in the player's library
    python3 palace.py list                         # the library: levels and enemies
    python3 palace.py apply library ops.json --level gorge   # JSON edit operations (see the guide)
    python3 palace.py check library                # offline checks; --live adds the game's validator
    python3 palace.py render library gorge -o map.png
    python3 palace.py economy library gorge ; python3 palace.py waves library gorge
    python3 palace.py test library gorge --nights 1-3 --plan plan.json --shots shots/
    python3 palace.py export "Gorge Pack" gorge    # a .tfpack of levels (+ the enemies they use), through the game

"library" is the player's design library (one file per level and per enemy); any level can spawn any library enemy by
its id. Old pack ids still work: a pack moved into the library names the levels that came from it, and a pack folder
under packs/ is read and written as before.

Every command prints JSON and exits non-zero on failure (`test`: 1 when the run is lost or stuck).
"""
import argparse
import base64
import copy
import hashlib
import heapq
import json
import math
import os
import random
import re
import struct
import sys
import time
import uuid
import zlib
from pathlib import Path

VERSION = "1.0"
HERE = Path(__file__).resolve().parent
PACK_VERSION = 3


class Fail(Exception):
    pass


def out(obj):
    """JSON to stdout, compact but readable: short values on one line, lists one item per line."""
    print(fmt(obj, 0))


def fmt(v, indent):
    flat = json.dumps(v, ensure_ascii=False)
    if len(flat) + indent <= 120 or not isinstance(v, (dict, list)) or not v:
        return flat
    pad = " " * (indent + 1)
    if isinstance(v, list):
        return "[\n" + ",\n".join(pad + fmt(x, indent + 1) for x in v) + "\n" + " " * indent + "]"
    return "{\n" + ",\n".join(pad + json.dumps(k) + ": " + fmt(x, indent + 1) for k, x in v.items()) + "\n" + " " * indent + "}"


# =====================================================================================================================
# Locations
# =====================================================================================================================

def steam_roots():
    home = Path.home()
    if sys.platform == "darwin":
        return [home / "Library/Application Support/Steam/steamapps/common/Thronefall"]
    if sys.platform.startswith("win"):
        roots = []
        for env in ("ProgramFiles(x86)", "ProgramFiles"):
            if os.environ.get(env):
                roots.append(Path(os.environ[env]) / "Steam/steamapps/common/Thronefall")
        for drive in "CDEFG":
            roots.append(Path(f"{drive}:/SteamLibrary/steamapps/common/Thronefall"))
        return roots
    return [home / ".steam/steam/steamapps/common/Thronefall", home / ".local/share/Steam/steamapps/common/Thronefall"]


def pid_alive(pid):
    """Whether a process exists; None when that can't be told (no pid, or Windows, where os.kill would end it)."""
    if not isinstance(pid, int) or pid <= 0 or sys.platform.startswith("win"):
        return None
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return None
    return True


class Env:
    """Where things are: the game's BepInEx folder, the packs, the kit (this folder) and its reference cache."""

    def __init__(self, game=None):
        self.bepinex = None
        self.source = None
        candidates = []
        if game:
            candidates.append((Path(game).expanduser(), "--game"))
        if os.environ.get("PALACE_GAME"):
            candidates.append((Path(os.environ["PALACE_GAME"]).expanduser(), "PALACE_GAME"))
        # The kit lives in <game>/BepInEx/PalaceMod/Agent when the mod wrote it.
        if HERE.parent.name == "PalaceMod" and HERE.parent.parent.name == "BepInEx":
            candidates.append((HERE.parent.parent.parent, "the game this palace.py was written into"))
        candidates += [(r, "the default Steam install") for r in steam_roots()]
        self.found = []
        for c, why in candidates:
            b = c if c.name == "BepInEx" else c / "BepInEx"
            if b.is_dir() and b.resolve() not in [f.resolve() for f, _ in self.found]:
                self.found.append((b, why))
        self.error = None
        if self.found:
            self.bepinex, self.source = self.found[0]
        explicit = [(c, why) for c, why in candidates if why in ("--game", "PALACE_GAME")]
        if explicit and self.source != explicit[0][1]:
            # Never fall back to another install when one was named: that could be the player's real game.
            c, why = explicit[0]
            self.error = f"{why} points at {c}, which has no BepInEx folder (is it the Thronefall folder?)"
            self.bepinex, self.source = None, None
        self.kit = HERE
        # The reference cache lives with the game's copy of the kit, so every copy of palace.py shares it.
        game_kit = self.bepinex / "PalaceMod" / "Agent" if self.bepinex else self.kit
        self.ref = Path(os.environ.get("PALACE_REF") or (game_kit / "reference"))

    def need_game(self):
        if not self.bepinex:
            raise Fail(self.error or "Can't find the game's BepInEx folder. Pass --game <Thronefall folder> or set PALACE_GAME.")
        return self.bepinex

    @property
    def packs(self):
        return self.need_game() / "PalaceBuilder" / "packs"

    @property
    def library(self):
        return self.need_game() / "PalaceBuilder" / "library"

    @property
    def bridge(self):
        return self.need_game() / "PalaceMod" / "Agent" / "bridge"

    @property
    def log(self):
        return self.need_game() / "LogOutput.log"


# =====================================================================================================================
# Bridge client
# =====================================================================================================================

class Game:
    def __init__(self, env):
        self.env = env

    def status_file(self, tries=4):
        """bridge/status.json plus its age in seconds and whether its process is alive; None if there is none. A read
        that fails (the game rewriting it) is retried, so one unlucky read never looks like "bridge off"."""
        f = self.env.bridge / "status.json"
        for attempt in range(tries):
            if attempt:
                time.sleep(0.25)
            if not f.exists():
                continue
            try:
                s = json.loads(f.read_text())
            except (ValueError, OSError):
                continue
            s["age"] = round(time.time() - s.get("time", 0), 1)
            alive = pid_alive(s.get("pid"))
            if alive is not None:
                s["running"] = alive
            return s
        return None

    def require(self, grace=30):
        """The bridge's status, or a Fail that says what's wrong: bridge off, game not running, or game not answering.
        A game that is running but quiet (a scene load can stall it for a while) gets `grace` seconds to come back."""
        deadline = time.time() + grace
        while True:
            s = self.status_file()
            if s is None:
                raise Fail(f"The Agent bridge isn't on ({self.env.bridge / 'status.json'} doesn't exist). In the game: Settings > "
                           "Mods > Builder > Agent bridge > Enabled (it's off by default), with the game running. Then retry.")
            if s["age"] <= 15 and s.get("running") is not False:
                return s
            if s.get("running") is False:
                raise Fail(f"The game isn't running: its last bridge status is {s['age']} s old and its process ({s.get('pid')}) "
                           f"is gone. Start the game (the bridge stays on), then retry.")
            if time.time() >= deadline:
                raise Fail(f"The game hasn't updated the Agent bridge for {s['age']} s (process {s.get('pid')}"
                           f"{' still running' if s.get('running') else ''}): loading, frozen, or paused? Then retry.")
            time.sleep(1.0)

    def touch_activity(self):
        """Marks client activity (bridge/last-request), so a harness that watches the game, like the PalaceMod dev pool's
        idle lease, knows the game is in use."""
        try:
            (self.env.bridge / "last-request").touch()
        except OSError:
            pass

    def raw(self, text, timeout=60):
        """Sends one command line; returns its text result. Raises Fail with the game's message on an error."""
        self.require()
        self.touch_activity()
        folder = self.env.bridge
        rid = uuid.uuid4().hex
        response = folder / f"command-{rid}.response"
        request = folder / "command.request"
        deadline = time.time() + 5
        while request.exists() and time.time() < deadline:  # another client's request still unread
            time.sleep(0.1)
        tmp = folder / f"command-{rid}.tmp"
        tmp.write_text(f"@{rid}\n{text.strip()}\n")
        os.replace(tmp, request)
        deadline = time.time() + timeout
        next_check = time.time() + 3
        while time.time() < deadline:
            time.sleep(0.1)
            if response.exists():
                try:
                    status, _, result = response.read_text().partition("\n")
                except OSError:
                    continue
                response.unlink(missing_ok=True)
                if status != "ok":
                    raise Fail(f"game: {result.strip()}")
                return result
            if time.time() >= next_check:
                # A game that quit (or crashed) mid-command would leave us waiting out the whole timeout.
                next_check = time.time() + 3
                s = self.status_file()
                if s is not None and s.get("running") is False:
                    request.unlink(missing_ok=True)
                    raise Fail(f"The game isn't running: it exited while handling `{text}` (process {s.get('pid')} is gone). "
                               "If it crashed, the game's Player.log (macOS: ~/Library/Logs/Grizzly Games/Thronefall/) "
                               "has the details until the game starts again.")
        raise Fail(f"The game didn't answer `{text}` within {timeout} s (loading? check `palace.py game log`).")

    LOADS = ("play ", "sandbox ", "edit ", "vanilla ", "menu")

    def agent(self, text, timeout=60):
        if text.startswith(self.LOADS):
            # A scene change can't start while another is running (e.g. right after `menu`).
            deadline = time.time() + 60
            while True:
                st = json.loads(self.raw("agent status", timeout))
                if not st.get("loading") or time.time() >= deadline:
                    break
                time.sleep(0.5)
            # The level loads the game has done so far: wait_level_day waits for the next one, not the level still
            # showing from before (replaying a level looks loaded until its transition starts).
            self.loads_before = st.get("loads")
        result = self.raw("agent " + text, timeout)
        try:
            return json.loads(result)
        except ValueError:
            return {"result": result}

    def status(self):
        return self.agent("status")

    def wait(self, predicate, timeout, what, poll=0.5):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                last = self.status()
                if predicate(last):
                    return last
            except Fail as e:
                if "isn't on" in str(e) or "isn't running" in str(e):
                    raise
            time.sleep(poll)
        raise Fail(f"Timed out after {timeout} s waiting for {what}; last status: {json.dumps(last)}")

    def wait_level_day(self, timeout=90):
        before = getattr(self, "loads_before", None)
        if before is None:
            time.sleep(1.0)   # an older mod without the load counter
        return self.wait(lambda s: s.get("inLevel") and not s.get("loading") and s.get("time") == "day"
                         and (before is None or (s.get("loads") or 0) > before), timeout, "the level to load")

    def log_size(self):
        try:
            return self.env.log.stat().st_size
        except OSError:
            return 0

    def log_since(self, offset, features=None, min_level="warn"):
        try:
            with open(self.env.log, "rb") as f:
                f.seek(offset)
                text = f.read().decode("utf-8", "replace")
        except OSError:
            return []
        levels = {"error": 0, "fatal": 0, "warning": 1, "message": 2, "info": 2, "debug": 3}
        want = {"error": 0, "warn": 1, "info": 2, "debug": 3}[min_level]
        lines = []
        for line in text.splitlines():
            m = re.match(r"\[(\w+)\s*:\s*([^\]]+)\]\s*(.*)", line)
            if not m:
                continue
            lvl = levels.get(m.group(1).strip().lower(), 2)
            if lvl > want:
                continue
            msg = m.group(3)
            if features and not any(f"[{f}]" in msg for f in features):
                continue
            lines.append(f"{m.group(1).strip().lower()}: {msg}")
        return lines

    def screenshot(self, path, timeout=20):
        path = Path(path).resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists():
            path.unlink()
        self.agent(f"screenshot {path}")
        deadline = time.time() + timeout
        size = -1
        while time.time() < deadline:
            time.sleep(0.4)
            if path.exists() and path.stat().st_size == size and size > 0:
                return str(path)
            size = path.stat().st_size if path.exists() else -1
        raise Fail("No screenshot was written (is the game window minimised?)")


# =====================================================================================================================
# Reference data (cached from the game by `ref build`)
# =====================================================================================================================

MAIN_MAPS = ["Nordfels", "Durststein", "Frostsee", "Uferwind", "Sturmklamm", "Wildbach", "Moorweg", "Freifort", "Totend"]
# Maps whose colour scheme a level or prop can take ("colors").
COLOR_MAPS = ["Neuland"] + MAIN_MAPS


class Ref:
    def __init__(self, env):
        self.env = env
        self._maps = {}
        self._catalog = None
        self._scenery = None

    @property
    def catalog(self):
        if self._catalog is None:
            f = self.env.ref / "catalog.json"
            if not f.exists():
                raise Fail(f"No reference data yet ({f}). Run `palace.py ref build` with the game running and the Agent bridge on.")
            self._catalog = json.loads(f.read_text())
        return self._catalog

    def map(self, scene):
        if scene not in self._maps:
            f = self.env.ref / "maps" / f"{scene}.json"
            if not f.exists():
                raise Fail(f"No reference for map {scene!r} ({f}). Run `palace.py ref build --maps {scene}`.")
            self._maps[scene] = json.loads(f.read_text())
        return self._maps[scene]

    def has_map(self, scene):
        return (self.env.ref / "maps" / f"{scene}.json").exists()

    def maps(self):
        d = self.env.ref / "maps"
        return sorted(p.stem for p in d.glob("*.json")) if d.is_dir() else []

    @property
    def scenery(self):
        if self._scenery is None:
            f = self.env.ref / "scenery.json"
            self._scenery = json.loads(f.read_text()) if f.exists() else {"meshes": []}
        return self._scenery

    def unit(self, uid):
        for u in self.catalog.get("units", []):
            if u["id"] == uid:
                return u
        low = uid.lower()
        for u in self.catalog.get("units", []):
            if u["id"].lower() == low:
                return u
        return None

    def host(self, scene):
        for h in self.catalog.get("hosts", []):
            if h["scene"] == scene:
                return h
        return None


class Grid:
    """A map's sampled ground (MapDump.grid): heights and walkability by cell."""

    def __init__(self, g):
        self.ox, self.oz = g["origin"]
        self.cell = g["cell"]
        self.w, self.h = g["size"]
        raw = base64.b64decode(g["heights"])
        self.heights = struct.unpack(f"<{self.w * self.h}h", raw)
        self.walk = g["walk"]

    def index(self, x, z):
        i = int((x - self.ox) // self.cell)
        j = int((z - self.oz) // self.cell)
        if 0 <= i < self.w and 0 <= j < self.h:
            return i, j
        return None

    def height(self, x, z):
        ij = self.index(x, z)
        if ij is None:
            return None
        v = self.heights[ij[1] * self.w + ij[0]]
        return None if v == -32768 else v / 100.0

    def kind(self, x, z):
        ij = self.index(x, z)
        return "#" if ij is None else self.walk[ij[1]][ij[0]]

    def center(self, i, j):
        return self.ox + (i + 0.5) * self.cell, self.oz + (j + 0.5) * self.cell


# =====================================================================================================================
# Packs
# =====================================================================================================================

ID_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$")


LIBRARY_ID = "library"


class LibraryRef:
    """Stands in for a pack.json path: the player's library (levels/<id>.json, enemies/<id>.json, assets.json), seen as
    one pack with the id "library". origin limits the levels to those that came from one migrated or imported pack."""

    def __init__(self, root, origin=None):
        self.root = Path(root)
        self.origin = origin
        self.snapshot = {}
        self.meta = {}
        self.renames = []   # (folder, old id, new id): shared items renamed by ops; save_library moves their pictures
        self.skipped = []   # warnings for library files that could not be read (skipped)

    def exists(self):
        return True

    def __str__(self):
        return str(self.root) + (f" (levels from {self.origin})" if self.origin else "")


def _item_files(root, kind):
    d = Path(root) / kind
    return sorted(d.glob("*.json")) if d.is_dir() else []


def library_origins(root):
    found = set()
    for f in _item_files(root, "levels"):
        try:
            o = (json.loads(f.read_text(encoding="utf-8")).get("library") or {}).get("origin")
        except (OSError, ValueError):
            continue
        if o:
            found.add(o)
    return found


def migrate_pack(obj):
    """Upgrades pack JSON in place to PACK_VERSION, as the mod's Migrations do (Model/PackSerializer.cs)."""
    v = obj.get("version") if isinstance(obj.get("version"), int) else 0
    if v < 2:
        for lv in obj.get("levels") or []:
            adds = ((lv.get("buildSlots") or {}).get("add")) or []
            ids = {a.get("id") for a in adds if isinstance(a, dict)}
            for a in adds:
                act, lvl = a.pop("activator", None), a.pop("activatorLevel", None)
                if isinstance(act, str) and "requires" not in a:
                    a["requires"] = {"slot": "add:" + act if act in ids else act, "level": lvl if isinstance(lvl, int) else 0}
    if v < 3:
        # behaviours [{"type": "SpawnOnDeath", "unit": U}] -> a rule at the front of rules (the unit factory runs it
        # on the game's own enemyToSpawnOnDeath, so it plays as before).
        for u in obj.get("units") or []:
            bs = u.pop("behaviours", None)
            if not isinstance(bs, list):
                continue
            rules, unknown = [], []
            for b in bs:
                if isinstance(b, dict) and b.get("type") == "SpawnOnDeath" and isinstance(b.get("unit"), str):
                    rules.append({"when": "death", "do": [{"do": "spawn", "entity": b["unit"], "at": "self", "elite": "inherit"}]})
                else:
                    unknown.append(b)
            if unknown:
                u["behaviours"] = unknown
            if rules:
                u["rules"] = rules + (u.get("rules") or [])
    if v and v < PACK_VERSION:
        obj["version"] = PACK_VERSION
    return obj


LOGIC_KINDS = ("entities", "statuses", "presets")
LOGIC_KIND_NAME = {"entities": "entity", "statuses": "status", "presets": "preset"}


def _read_item(ref, f, kind):
    """One library file as pack JSON, or None (and a note in ref.skipped) when it can't be read: one broken file never
    stops the rest of the library from loading."""
    try:
        item = migrate_pack(json.loads(f.read_text(encoding="utf-8")))
        if not isinstance(item, dict):
            raise ValueError("not a JSON object")
        return item
    except (OSError, ValueError, AttributeError, TypeError) as e:
        ref.skipped.append({"severity": "warning", "file": f"{kind}/{f.name}", "path": "",
                            "message": f"can't be read, so it's skipped (levels that use it will miss it): {e}"})
        return None


def load_library(ref):
    data = {"format": "palace-builder", "version": PACK_VERSION, "id": LIBRARY_ID, "name": "My library", "assets": {},
            "units": [], "waveSets": [], "levels": [], "entities": [], "statuses": [], "presets": []}
    # The behaviour engine's shared definitions: library/<entities|statuses|presets>/<id>.json, one item each.
    for kind in LOGIC_KINDS:
        for f in _item_files(ref.root, kind):
            item = _read_item(ref, f, kind)
            if item is None:
                continue
            d = (item.get(kind) or [None])[0]
            if not isinstance(d, dict):
                continue
            d["id"] = f.stem
            data[kind].append(d)
            ref.snapshot[(kind, f.stem)] = json.dumps(d, sort_keys=True)
            ref.meta[(kind, f.stem)] = item.get("library") or {}
    for kind in ("levels", "enemies"):
        for f in _item_files(ref.root, kind):
            item = _read_item(ref, f, kind)
            if item is None:
                continue
            meta = item.get("library") or {}
            if kind == "levels":
                if ref.origin and meta.get("origin") != ref.origin:
                    continue
                lv = (item.get("levels") or [None])[0]
                if lv is None:
                    continue
                lv["id"] = f.stem
                sets = item.get("waveSets") or []
                data["levels"].append(lv)
                data["waveSets"].extend(sets)
                ref.snapshot[("levels", f.stem)] = json.dumps([lv, sets], sort_keys=True)
            else:
                u = (item.get("units") or [None])[0]
                if u is None:
                    continue
                u["id"] = f.stem
                data["units"].append(u)
                ref.snapshot[("enemies", f.stem)] = json.dumps(u, sort_keys=True)
            ref.meta[(kind, f.stem)] = meta
    af = ref.root / "assets.json"
    if af.exists():
        data["assets"] = json.loads(af.read_text(encoding="utf-8"))
    ref.snapshot["assets"] = json.dumps(data["assets"], sort_keys=True)
    return data


def _write_json(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def save_library(ref, data):
    """Writes the items that changed since load_library; items that were loaded and are gone go to library/trash."""
    stamp = time.strftime("%Y%m%d-%H%M%S")
    seen = set()
    sets = {w.get("id"): w for w in data.get("waveSets") or []}
    for lv in data.get("levels") or []:
        if not ID_RE.match(lv.get("id") or ""):
            raise Fail(f"level id {lv.get('id')!r}: 1-64 of a-z 0-9 . _ -")
        own = [sets[lv["waveSet"]]] if lv.get("waveSet") in sets else []
        key = ("levels", lv["id"])
        seen.add(key)
        if ref.snapshot.get(key) == json.dumps([lv, own], sort_keys=True):
            continue
        meta = dict(ref.meta.get(key) or {"created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
        meta["kind"] = "level"
        if ref.origin and "origin" not in meta:
            meta["origin"] = ref.origin
        _write_json(ref.root / "levels" / (lv["id"] + ".json"), {"format": "palace-builder", "version": PACK_VERSION, "id": lv["id"],
                    "name": lv.get("name") or lv["id"], "levels": [lv], "waveSets": own, "library": meta})
        ref.snapshot[key] = json.dumps([lv, own], sort_keys=True)
    for u in data.get("units") or []:
        if not ID_RE.match(u.get("id") or ""):
            raise Fail(f"unit id {u.get('id')!r}: 1-64 of a-z 0-9 . _ -")
        key = ("enemies", u["id"])
        seen.add(key)
        if ref.snapshot.get(key) == json.dumps(u, sort_keys=True):
            continue
        meta = dict(ref.meta.get(key) or {"created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
        meta["kind"] = "enemy"
        _write_json(ref.root / "enemies" / (u["id"] + ".json"), {"format": "palace-builder", "version": PACK_VERSION, "id": u["id"],
                    "name": u.get("name") or u["id"], "units": [u], "library": meta})
        ref.snapshot[key] = json.dumps(u, sort_keys=True)
    for kind in LOGIC_KINDS:
        for d in data.get(kind) or []:
            if not ID_RE.match(d.get("id") or ""):
                raise Fail(f"{LOGIC_KIND_NAME[kind]} id {d.get('id')!r}: 1-64 of a-z 0-9 . _ -")
            key = (kind, d["id"])
            seen.add(key)
            if ref.snapshot.get(key) == json.dumps(d, sort_keys=True):
                continue
            meta = dict(ref.meta.get(key) or {"created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
            meta["kind"] = LOGIC_KIND_NAME[kind]
            _write_json(ref.root / kind / (d["id"] + ".json"), {"format": "palace-builder", "version": PACK_VERSION, "id": d["id"],
                        "name": d.get("name") or d["id"], kind: [d], "library": meta})
            ref.snapshot[key] = json.dumps(d, sort_keys=True)
    for key in [k for k in ref.snapshot if isinstance(k, tuple) and k not in seen]:
        kind, iid = key
        f = ref.root / kind / (iid + ".json")
        if f.exists():
            (ref.root / "trash").mkdir(parents=True, exist_ok=True)
            prefix = {"levels": "level", "enemies": "enemy"}.get(kind, kind)
            os.replace(f, ref.root / "trash" / f"{prefix}-{iid}-{stamp}.json")
        del ref.snapshot[key]
    for folder, old, new in ref.renames:
        png, to = ref.root / folder / (old + ".png"), ref.root / folder / (new + ".png")
        if png.exists() and not to.exists():
            os.replace(png, to)
    ref.renames = []
    assets = json.dumps(data.get("assets") or {}, sort_keys=True)
    if assets != ref.snapshot.get("assets"):
        _write_json(ref.root / "assets.json", data.get("assets") or {})
        ref.snapshot["assets"] = assets


def pack_path(env, pack):
    if pack in (LIBRARY_ID, "lib"):
        return LibraryRef(env.library)
    p = Path(pack)
    if p.suffix == ".json" and p.exists():
        return p
    if (p / "pack.json").exists():
        return p / "pack.json"
    if not ID_RE.match(pack):
        raise Fail(f"{pack!r} isn't a pack id (1-64 of a-z 0-9 . _ -), \"library\", or a pack.json path")
    path = env.packs / pack / "pack.json"
    if not path.exists() and env.library.is_dir() and pack in library_origins(env.library):
        return LibraryRef(env.library, origin=pack)
    return path


def load_pack(env, pack):
    path = pack_path(env, pack)
    if isinstance(path, LibraryRef):
        return path, load_library(path)
    if not path.exists():
        raise Fail(f"No pack at {path}. Make a library level with `palace.py new <level> --host <map>`.")
    return path, migrate_pack(json.loads(path.read_text(encoding="utf-8")))


def save_pack(path, data):
    if isinstance(path, LibraryRef):
        save_library(path, data)
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def find_level(data, level_id=None):
    levels = data.get("levels") or []
    if level_id is None:
        if len(levels) == 1:
            return levels[0]
        raise Fail(f"Say which level: {[l.get('id') for l in levels]}")
    for l in levels:
        if l.get("id") == level_id:
            return l
    raise Fail(f"No level {level_id!r} in the pack; levels: {[l.get('id') for l in levels]}")


def find_waveset(data, ws_id):
    for w in data.get("waveSets") or []:
        if w.get("id") == ws_id:
            return w
    return None


# =====================================================================================================================
# The level as it will play: host slots after removals/edits, plus added slots
# =====================================================================================================================

class Slot:
    __slots__ = ("key", "name", "kind", "pos", "rot", "castle", "upgrades", "req", "cap", "follows", "added", "base", "units", "mod")

    def __init__(self, **kw):
        for k in self.__slots__:
            setattr(self, k, kw.get(k))

    @property
    def label(self):
        return "castle" if self.castle else self.key[4:] if self.key.startswith("add:") else self.name


def is_empty_base(level):
    return level.get("base") in ("cleared", "flat")


def host_req(s):
    """The requirement a host slot has in the map data: (activator name or None, level) or 'start'."""
    if not s.get("startDeactivated") or not s.get("activator"):
        return None
    return (s["activator"], s.get("activatorLevel", 0))


def playable(upgrades):
    """A slot's upgrade levels without the hidden top tier (disabled in normal play; it is always the last ones)."""
    return [copy.deepcopy(u) for u in upgrades if not u.get("disabled")]


def effective_slots(ref, level):
    """Returns (slots by key, castle key, warnings). Keys: host slot name, or 'add:<id>'."""
    m = ref.map(level["host"])
    host = {s["name"]: s for s in m["slots"]}
    castle = next((s["name"] for s in m["slots"] if s.get("castle")), None)
    bs = level.get("buildSlots") or {}
    removed = set(bs.get("remove") or [])
    if is_empty_base(level):
        for s in m["slots"]:
            if s.get("castle"):
                continue
            if s.get("followsActivator") and s.get("activator") == castle:
                continue
            removed.add(s["name"])
    slots = {}
    for s in m["slots"]:
        if s["name"] in removed:
            continue
        r = host_req(s)
        slots[s["name"]] = Slot(key=s["name"], name=s["name"], kind=s["kind"], pos=s["pos"], rot=s.get("rotY", 0), castle=bool(s.get("castle")),
                                upgrades=playable(s["upgrades"]), req=r, cap=s.get("upgradeCap"), follows=bool(s.get("followsActivator")),
                                added=False, base=None, units=[list(u) for u in s.get("units") or []],
                                mod=income_modifier(s))
    warnings = []

    def apply_common(slot, e):
        if e.get("pos"):
            slot.pos = e["pos"]
        if e.get("rotY") is not None:
            slot.rot = e["rotY"]
        if "requires" in e and e["requires"] is not None:
            rq = e["requires"]
            slot.req = (rq["slot"], rq.get("level", 0)) if rq.get("slot") else None
        if e.get("upgradeCap") is not None:
            slot.cap = e["upgradeCap"]
        for i, u in enumerate(e.get("units") or []):
            if u and len(u) >= 2 and i < len(slot.units):
                slot.units[i] = [u[0], u[1]]
        for i, u in enumerate(e.get("upgrades") or []):
            if u and u.get("cost") is not None and i < len(slot.upgrades):
                slot.upgrades[i]["cost"] = u["cost"]

    for e in bs.get("edit") or []:
        s = slots.get(e.get("ref"))
        if s is None:
            warnings.append(f"edit of {e.get('ref')!r}: no such slot on the level")
            continue
        apply_common(s, e)
    standard = {b["name"]: b for b in m.get("buildings") or []}
    for a in bs.get("add") or []:
        raw = a.get("base") or ""
        if raw.startswith("building:"):
            # The game's standard building of a kind (any map); it needs castle level 1 unless "requires" says otherwise.
            base, src = raw, standard.get(raw)
            if src is None:
                warnings.append(f"add {a.get('id')!r}: no standard building {raw!r}" +
                                ("" if standard else " (this map's reference predates them: `palace.py ref build --force`)"))
                continue
        else:
            base = raw[6:] if raw.startswith("scene:") else None
            src = host.get(base)
        if src is None:
            warnings.append(f"add {a.get('id')!r}: base {a.get('base')!r} isn't a slot of {level['host']}")
            continue
        key = "add:" + a["id"]
        req = ("castle", 0) if base.startswith("building:") else host_req(src)
        s = Slot(key=key, name="PalaceBuilder " + a["id"], kind=src["kind"], pos=a.get("pos") or src.get("pos"), rot=a.get("rotY", 0), castle=False,
                 upgrades=playable(src["upgrades"]), req=req, cap=src.get("upgradeCap"), follows=False, added=True, base=base,
                 units=[list(u) for u in src.get("units") or []], mod=income_modifier(src))
        if is_empty_base(level) and s.req and s.req[0] != castle and s.req[0] != "castle" and "requires" not in a:
            s.req = ("__removed__", 0)
        apply_common(s, a)
        slots[key] = s
    # Resolve requirement refs to keys.
    for s in slots.values():
        if s.req and s.req[0] == "castle":
            s.req = (castle, s.req[1])
    return slots, castle, warnings


def standard_buildings(ref, maps):
    """The game's standard buildings ("building:<kind>", the same on every map) from the first map reference that has them."""
    for name in maps:
        b = ref.map(name).get("buildings")
        if b:
            return b
    return []


def step_income(step):
    br = step.get("branches") or [{}]
    return max((b.get("income", 0) for b in br), default=0)


# Income that changes each dawn, as every level's data has it (game 1.x), for references built before the map dump
# recorded it: a harbour gains 1 a dawn up to 5, and its level 2 doubles both and adds its current income again
# (FishingHarbour, FishingHarbourUpgrade); a gold mine loses 1 a dawn down to 1 (MineShaft).
DEFAULT_MODIFIERS = {"Harbour": {"kind": "harbour", "perDawn": 1, "max": 5, "doublesAt": 1},
                     "Gold Mine": {"kind": "mine", "perDawn": 1, "min": 1}}


def modifier_text(m):
    if m["kind"] == "harbour":
        return (f"Each dawn: +{m['perDawn']}g income, up to {m['max']}g (starts at 0). "
                f"L{m.get('doublesAt', 1) + 1} adds its current income again and doubles the growth and the cap.")
    if m["kind"] == "mine":
        return f"Each dawn: -{m['perDawn']}g income, down to {m['min']}g."
    return ""


def income_modifier(s):
    m = s.get("incomeModifier") or DEFAULT_MODIFIERS.get(s.get("kind"))
    return dict(m) if m else None


class Earner:
    """One slot's gold per dawn as it's built and the dawns pass (knock-outs and perks aside)."""
    __slots__ = ("slot", "level", "income", "step", "limit")

    def __init__(self, slot):
        self.slot, self.level, self.income = slot, 0, 0
        m = slot.mod or {}
        self.step, self.limit = m.get("perDawn", 0), m.get("max", m.get("min", 0))

    def copy(self):
        e = Earner(self.slot)
        e.level, e.income, e.step, e.limit = self.level, self.income, self.step, self.limit
        return e

    def buy(self):
        m = self.slot.mod or {}
        if m.get("kind") == "harbour" and self.level == m.get("doublesAt", 1):
            self.income += self.income  # the level-2 upgrade's income change is the harbour's current income
            self.step, self.limit = self.step * 2, self.limit * 2
        else:
            self.income += step_income(self.slot.upgrades[self.level])
        self.level += 1

    def dawn(self):
        """Pays, then the dawn's change."""
        paid = self.income
        kind = (self.slot.mod or {}).get("kind")
        if self.level > 0 and kind == "harbour":
            self.income = min(self.income + self.step, self.limit)
        elif self.level > 0 and kind == "mine":
            self.income = max(self.income - self.step, self.limit)
        return paid

    def paid_over(self, dawns):
        e = self.copy()
        return sum(e.dawn() for _ in range(dawns))

    def gain(self, dawns):
        """Extra gold the next step pays over `dawns` dawns."""
        after = self.copy()
        after.buy()
        return after.paid_over(dawns) - self.paid_over(dawns)

    def peak(self):
        """The most it pays a dawn, fully built."""
        e = self.copy()
        while e.level < len(e.slot.upgrades):
            e.buy()
        best = e.income
        for _ in range(30):
            e.dawn()
            best = max(best, e.income)
        return best


# =====================================================================================================================
# Edit operations
# =====================================================================================================================

def parse_req(spec, level, slots_keys):
    """Requirement shorthand -> pack JSON. "start" | "castle" | "castle:3" (castle level 3) | "<slot>" | "<slot>:2"
    (that slot at level 2) | {"slot":..., "level":...} (raw). Returns the "requires" object."""
    if spec is None:
        return None
    if isinstance(spec, dict):
        return spec
    spec = str(spec).strip()
    if spec in ("start", "none", "free", ""):
        return {}
    name, lvl = spec, 1
    m = re.match(r"^(.*?):(\d+)$", spec)
    if m:
        name, lvl = m.group(1), int(m.group(2))
    if lvl < 1:
        raise Fail(f"requirement {spec!r}: level counts from 1 (1 = built)")
    return {"slot": resolve_slot_ref(name, level, slots_keys), "level": lvl - 1}


def resolve_slot_ref(name, level, slots_keys=None):
    if name == "castle":
        return "castle"
    if name.startswith("add:"):
        return name
    adds = {a.get("id") for a in ((level.get("buildSlots") or {}).get("add") or [])}
    if name in adds:
        return "add:" + name
    return name  # a host slot name


def ensure(d, key, default):
    if d.get(key) is None:
        d[key] = default
    return d[key]


class Ops:
    def __init__(self, env, ref, data, level_id=None):
        self.env, self.ref, self.data = env, ref, data
        self.level_id = level_id
        self.log = []
        # Read-only caches. ground_y runs once per placed thing; decoding the level's terrain (and the host's grid) each
        # time made prop-heavy ops files take minutes. A decoded terrain is reused while its encoded content is unchanged.
        self._terrain_ro = {}
        self._grids = {}
        self._prop_ids = None
        self.store = None       # the LibraryRef (or pack.json path) being edited, for ops that rename library files
        self.edited = set()     # ids of shared items (units, entities, statuses, presets) the ops changed

    def level(self, op):
        # "level" names the level; the water op's numeric "level" is its water height, not a level id.
        lid = op.get("level") if isinstance(op.get("level"), str) else self.level_id
        return find_level(self.data, lid)

    def host_ground(self, level):
        """The host's sampled ground as a function (x, z) -> world y or None, for sculpting a map's own ground."""
        g = self.ref.map(level["host"]).get("grid")
        if not g:
            return None
        grid = Grid(g)
        return grid.height

    def terrain_of(self, lv):
        """The level's terrain with absolute heights: on a map's own ground (base not flat) the pack holds offsets from
        the host's ground, made absolute here from the reference's sampled ground."""
        return Terrain(lv["terrain"], host=None if lv.get("base") == "flat" else self.host_ground(lv))

    def grid(self, host):
        if host not in self._grids:
            g = self.ref.map(host).get("grid")
            self._grids[host] = Grid(g) if g else None
        return self._grids[host]

    def terrain_ro(self, level):
        """The level's terrain for reading only (never edit or store it): Terrain(t) on a flat base, else terrain_of.
        Cached on the terrain's encoded content, so an edit in between is always seen."""
        t = level["terrain"]
        w = t.get("water") or {}
        small = json.dumps({k: v for k, v in t.items() if k not in ("heights", "materials", "blocked", "water")}, sort_keys=True)
        key = (level.get("base") == "flat", level.get("host"), small, t.get("heights"), t.get("materials"), t.get("blocked"),
               w.get("cells"), w.get("level"))
        hit = self._terrain_ro.get(id(t))
        if hit is None or hit[0] != key:
            hit = (key, Terrain(t) if key[0] else self.terrain_of(level))
            self._terrain_ro[id(t)] = hit
        return hit[1]

    def ground_y(self, level, x, z):
        """The ground's height at a point, or None where the level has no ground: off a flat field, or outside the
        host map's sampled area."""
        t = level.get("terrain")
        if t and level.get("base") == "flat":
            y = self.terrain_ro(level).height_at(x, z)
            return None if y is None else round(y, 2)
        if t and t.get("heights"):
            T = self.terrain_ro(level)
            if T.sculpted_at(x, z):
                return round(T.height_at(x, z), 2)
        grid = self.grid(level["host"])
        if grid and grid.index(x, z) is None:
            return None
        return self.fallback_y(level, x, z)

    def fallback_y(self, level, x, z):
        """A height where the level may have no ground (props, flyer-only lines): the host's sampled ground, else the
        castle's height."""
        grid = self.grid(level["host"])
        y = grid.height(x, z) if grid else None
        if y is not None:
            return round(y, 2)
        castle = next((s for s in self.ref.map(level["host"])["slots"] if s.get("castle")), None)
        return castle["pos"][1] if castle else 0.0

    def ground_bounds(self, level):
        """(what, x0, z0, x1, z1): the area ground_y answers for."""
        t = level.get("terrain")
        if t and level.get("base") == "flat":
            T = self.terrain_ro(level)
            return "the flat field", T.ox, T.oz, T.ox + T.w * T.cell, T.oz + T.h * T.cell
        g = self.grid(level["host"])
        if g:
            return f"{level['host']}'s ground", g.ox, g.oz, g.ox + g.w * g.cell, g.oz + g.h * g.cell
        return None

    @staticmethod
    def numbers(v, n, field):
        if not isinstance(v, (list, tuple)) or len(v) != n or not all(isinstance(c, (int, float)) and not isinstance(c, bool) and math.isfinite(c) for c in v):
            raise Fail(f"{field} is {['', '', '[x, z]', '[x, y, z]'][n]} (numbers), not {json.dumps(v)}")
        return [float(c) for c in v]

    def point(self, level, x, z, field="at", strict=True):
        """[x, z]'s ground height. Off the level's ground: a precise Fail when strict (slots, ground spawn lines), else
        fallback_y (props and flyer-only lines may stand over the sea or past the field, like the maps' own)."""
        y = self.ground_y(level, x, z)
        if y is None and not strict:
            return self.fallback_y(level, x, z)
        if y is None:
            what, x0, z0, x1, z1 = self.ground_bounds(level)
            bad = ([f"x {x:g} is {'west' if x < x0 else 'east'} of it"] if not x0 <= x <= x1 else []) + \
                  ([f"z {z:g} is {'south' if z < z0 else 'north'} of it"] if not z0 <= z <= z1 else [])
            raise Fail(f"{field} [{x:g}, {z:g}] is off {what} (x {x0:g}..{x1:g}, z {z0:g}..{z1:g}): {' and '.join(bad) or 'outside it'}")
        return y

    def pos(self, level, op, strict=True):
        if op.get("pos") is not None:
            x, y, z = self.numbers(op["pos"], 3, "pos")
            return [round(x, 2), round(y, 2), round(z, 2)]
        if op.get("at") is not None:
            x, z = self.numbers(op["at"], 2, "at")
            return [round(x, 2), self.point(level, x, z, strict=strict), round(z, 2)]
        raise Fail("give \"at\": [x, z] (height from the ground) or \"pos\": [x, y, z]")

    def run(self, ops):
        for i, op in enumerate(ops):
            name = op.get("op")
            fn = getattr(self, "op_" + str(name), None)
            if fn is None:
                raise Fail(f"ops[{i}]: unknown op {name!r}; ops: {', '.join(sorted(n[3:] for n in dir(self) if n.startswith('op_')))}")
            if name != "add_prop":
                self._prop_ids = None
            try:
                r = fn(op)
            except Fail as e:
                raise Fail(f"ops[{i}] ({name}): {e}")
            except (KeyError, TypeError, ValueError, IndexError) as e:
                what = f"missing field {e.args[0]!r}" if isinstance(e, KeyError) and e.args else f"bad field: {e}"
                usage = (fn.__doc__ or "").strip().split("\n")[0]
                raise Fail(f"ops[{i}] ({name}): {what}" + (f". Usage: {usage}" if usage else ""))
            self.log.append(r or name)
        self.resnap()

    def resnap(self):
        """On a flat base the game stands slots, props and lines on the terrain; keep the pack's y the same after
        terrain ops that ran after the things were placed (so checks and pictures agree)."""
        for lv in self.data.get("levels") or []:
            if lv.get("base") != "flat" or not lv.get("terrain"):
                continue
            T = Terrain(lv["terrain"])
            bs = lv.get("buildSlots") or {}
            items = list(bs.get("add") or []) + [e for e in bs.get("edit") or [] if e.get("pos")] + list(lv.get("props") or []) + \
                [a for a in ((lv.get("spawnLines") or {}).get("add") or [])]
            for it in items:
                p = it.get("pos")
                if p and len(p) == 3:
                    y = T.height_at(p[0], p[2])
                    if y is not None:
                        p[1] = round(y, 2)

    # ---- pack and level ----------------------------------------------------------------------------------------------

    def op_set(self, op):
        """{"op":"set","path":"level.colors","value":"Totend"}: raw edit of any field. A path starting with `level.`
        is in the op's level, `nights.` in that level's wave set; `levels[i]`/`waveSets[i]` index the whole pack (in
        the library: every level the player has), so there they must name the op's own level or its nights."""
        path = op["path"]
        lid = op.get("level") if isinstance(op.get("level"), str) else self.level_id
        m = re.match(r"(level|nights)(?=[.\[]|$)", path)
        if m:
            lv = self.level(op)
            if m.group(1) == "level":
                path = f"levels[{self.data['levels'].index(lv)}]" + path[m.end():]
            else:
                sets = self.data.get("waveSets") or []
                ws = next((w for w in sets if w.get("id") == lv.get("waveSet")), None)
                if ws is None:
                    raise Fail(f"level {lv['id']} has no nights yet (add them with the waves op)")
                path = f"waveSets[{sets.index(ws)}]" + path[m.end():]
        else:
            m = re.match(r"(levels|waveSets)\[(\d+)\]", path)
            if m and lid and len(self.data.get("levels") or []) > 1:
                lv = find_level(self.data, lid)
                items = self.data.get(m.group(1)) or []
                i = int(m.group(2))
                item = items[i] if i < len(items) else None
                own = lv if m.group(1) == "levels" else next((w for w in items if w.get("id") == lv.get("waveSet")), None)
                if item is not own:
                    raise Fail(f"{path} isn't level {lid}'s (this pack holds {len(self.data['levels'])} levels): write "
                               f"`level.` or `nights.` instead of `{m.group(0)}.`")
        target, key = self.walk(path, create=True)
        if op.get("delete"):
            if isinstance(target, list):
                target.pop(key)
            else:
                target.pop(key, None)
        else:
            target[key] = op["value"]
        return f"set {path}"

    def walk(self, path, create=False):
        # [3] indexes a list; [id=fire] is the list item with that id (the form check and apply report items in).
        parts = re.findall(r"\[id=[^\]]+\]|[^.\[\]]+|\[\d+\]", path)
        cur = self.data
        for idx, p in enumerate(parts):
            last = idx == len(parts) - 1
            if p.startswith("[id="):
                want = p[4:-1]
                key = next((i for i, x in enumerate(cur if isinstance(cur, list) else []) if isinstance(x, dict) and x.get("id") == want), None)
                if key is None:
                    raise Fail(f"{path}: no item with id {want!r}")
            else:
                key = int(p[1:-1]) if p.startswith("[") else p
            if last:
                return cur, key
            nxt = cur[key] if isinstance(cur, list) else cur.get(key)
            if nxt is None:
                if not create:
                    raise Fail(f"{path}: {p} doesn't exist")
                nxt = [] if parts[idx + 1].startswith("[") else {}
                cur[key] = nxt
            cur = nxt
        raise Fail("empty path")

    def op_level(self, op):
        """Sets level fields: name, description, base, waveSet, host, colors."""
        lv = self.level(op)
        for k in ("name", "description", "waveSet", "host", "colors"):
            if k in op:
                lv[k] = op[k]
        if "base" in op:
            if op["base"] in (None, "map", "none"):
                lv.pop("base", None)
            elif op["base"] in ("cleared", "flat"):
                lv["base"] = op["base"]
            else:
                raise Fail("base is \"cleared\", \"flat\" or null")
        return f"level {lv['id']}"

    # ---- slots -------------------------------------------------------------------------------------------------------

    def host_slot_for(self, level, kind):
        m = self.ref.map(level["host"])
        k = kind.lower()
        def is_kind(s):
            return s["kind"].lower() == k or (s.get("display") or "").lower() == k or s["name"] == "building:" + k
        match = [s for s in m["slots"] if s.get("canBeBase") and is_kind(s)]
        if not match:
            # A kind the map lacks: the game's standard building of that kind.
            std = [b for b in m.get("buildings") or [] if is_kind(b)]
            if std:
                return std[0]
            kinds = sorted({s["kind"] for s in m["slots"] if s.get("canBeBase")} | {b["kind"] for b in m.get("buildings") or []})
            raise Fail(f"no building kind {kind!r}; kinds: {kinds}" +
                       ("" if m.get("buildings") else f" (this map's reference has only its own kinds: `palace.py ref build --force --maps {level['host']}` adds the rest)") +
                       f". Use \"from\": \"<slot name>\" for a specific one (`palace.py ref slots {level['host']}`).")
        # The most common tree for that kind (most upgrade levels first).
        match.sort(key=lambda s: (-len(s["upgrades"]), s["name"]))
        return match[0]

    def op_add_slot(self, op):
        """{"op":"add_slot","id":"h1","kind":"House"|"from":"<host slot>","at":[x,z],"rotY":0,"requires":"castle","costs":[..],"upgradeCap":0}"""
        lv = self.level(op)
        sid = op["id"]
        if not ID_RE.match(sid):
            raise Fail(f"slot id {sid!r}: 1-64 of a-z 0-9 . _ -")
        bs = ensure(lv, "buildSlots", {})
        adds = ensure(bs, "add", [])
        if any(a.get("id") == sid for a in adds):
            raise Fail(f"slot {sid!r} already exists (use move_slot / set_slot; to re-apply a whole ops file, `apply --fresh`)")
        src = self.ref.map(lv["host"])
        if op.get("from"):
            base = next((s for s in src["slots"] if s["name"] == op["from"]), None)
            if base is None:
                raise Fail(f"no slot {op['from']!r} on {lv['host']}")
            if not base.get("canBeBase"):
                raise Fail(f"{op['from']!r} can't be copied (the castle, a wall piece its gate upgrades, or a slot with slots inside)")
        else:
            base = self.host_slot_for(lv, op["kind"])
        standard = base["name"].startswith("building:")
        entry = {"id": sid, "base": base["name"] if standard else "scene:" + base["name"], "pos": self.pos(lv, op), "rotY": float(op.get("rotY", 0))}
        req = op.get("requires", "castle" if is_empty_base(lv) or standard else None)
        if req is not None:
            entry["requires"] = parse_req(req, lv, None)
        if op.get("upgradeCap") is not None:
            entry["upgradeCap"] = int(op["upgradeCap"])
        if op.get("costs"):
            entry["upgrades"] = [{"cost": int(c)} if c is not None else None for c in op["costs"]]
        adds.append(entry)
        return f"added {sid} ({base['kind']}, " + ("the game's standard one: this map has none)" if standard else f"from {base['name']})")

    def find_slot_entry(self, lv, ref_name):
        """('add', entry) for an added slot id, or ('host', edit entry created on demand)."""
        bs = ensure(lv, "buildSlots", {})
        if ref_name == "castle":
            ref_name = next(s["name"] for s in self.ref.map(lv["host"])["slots"] if s.get("castle"))
        r = ref_name[4:] if ref_name.startswith("add:") else ref_name
        for a in bs.get("add") or []:
            if a.get("id") == r:
                return "add", a
        m = self.ref.map(lv["host"])
        if not any(s["name"] == ref_name for s in m["slots"]):
            raise Fail(f"no slot {ref_name!r}: not an added id, not a slot of {lv['host']}")
        edits = ensure(bs, "edit", [])
        for e in edits:
            if e.get("ref") == ref_name:
                return "host", e
        e = {"ref": ref_name}
        edits.append(e)
        return "host", e

    def op_move_slot(self, op):
        lv = self.level(op)
        kind, e = self.find_slot_entry(lv, op["slot"])
        if "at" in op or "pos" in op:
            e["pos"] = self.pos(lv, op)
        if "rotY" in op:
            e["rotY"] = float(op["rotY"])
        return f"moved {op['slot']}"

    def op_set_slot(self, op):
        """{"op":"set_slot","slot":"h1","requires":"castle:2","upgradeCap":0,"costs":[3,null,10]}"""
        lv = self.level(op)
        kind, e = self.find_slot_entry(lv, op["slot"])
        if "requires" in op:
            if op["requires"] is None:
                e.pop("requires", None)
            else:
                e["requires"] = parse_req(op["requires"], lv, None)
        if "upgradeCap" in op:
            if op["upgradeCap"] is None:
                e.pop("upgradeCap", None)
            else:
                e["upgradeCap"] = int(op["upgradeCap"])
        if "costs" in op:
            e["upgrades"] = [{"cost": int(c)} if c is not None else None for c in op["costs"]]
        return f"set {op['slot']}"

    def op_set_units(self, op):
        """{"op":"set_units","slot":"barracks","units":[[x,z],...]}: where a training slot's units stand, metres from the
        slot's centre in its frame (x right, z forward; turns with the slot), one per unit spot as `ref slots` lists them; null
        entries keep the map's. "at": [[x,z],...] gives world points instead. "units": null resets to the map's."""
        lv = self.level(op)
        kind, e = self.find_slot_entry(lv, op["slot"])
        if op.get("at") is not None:
            slots, _, _ = effective_slots(self.ref, lv)
            s = slots["add:" + e["id"] if kind == "add" else e["ref"]]
            a = math.radians(s.rot or 0)
            c, s_ = math.cos(a), math.sin(a)
            units = []
            if not isinstance(op["at"], list):
                raise Fail("at is a list of world points [[x, z], ...] (or null entries)")
            for i, pt in enumerate(op["at"]):
                if pt is None:
                    units.append(None)
                    continue
                px, pz = self.numbers(pt, 2, f"at[{i}]")
                dx, dz = px - s.pos[0], pz - s.pos[2]
                units.append([dx * c - dz * s_, dx * s_ + dz * c])
        else:
            units = op.get("units")
        if units is None:
            e.pop("units", None)
            return f"units of {op['slot']} back to the map's"
        if not isinstance(units, list) or any(u is not None and (not isinstance(u, list) or len(u) != 2) for u in units):
            raise Fail("units is a list of [x, z] (or null entries)")
        e["units"] = [None if u is None else [round(float(u[0]), 2), round(float(u[1]), 2)] for u in units]
        return f"set {len(units)} unit positions of {op['slot']}"

    def op_remove_slot(self, op):
        lv = self.level(op)
        bs = ensure(lv, "buildSlots", {})
        name = op["slot"]
        r = name[4:] if name.startswith("add:") else name
        adds = bs.get("add") or []
        for a in adds:
            if a.get("id") == r:
                adds.remove(a)
                return f"removed added slot {r}"
        m = self.ref.map(lv["host"])
        hs = next((s for s in m["slots"] if s["name"] == name), None)
        if hs is None:
            raise Fail(f"no slot {name!r}")
        if hs.get("castle"):
            raise Fail("the castle can't be removed")
        rem = ensure(bs, "remove", [])
        if name not in rem:
            rem.append(name)
        bs["edit"] = [e for e in bs.get("edit") or [] if e.get("ref") != name]
        return f"removed host slot {name}"

    def op_chain(self, op):
        """{"op":"chain","slots":[...],"mode":"order"|"nearest"|"tree","first":"castle","every":0}
        order: each needs the previous (the first needs `first`). nearest: greedy path from the castle. tree: minimum
        spanning tree rooted at the castle (each needs its parent). every=N: every N-th link also climbs a castle
        level (only in order/nearest mode, by linking to the castle instead)."""
        lv = self.level(op)
        names = op["slots"]
        mode = op.get("mode", "tree")
        slots, castle, _ = effective_slots(self.ref, lv)

        def key(n):
            r = resolve_slot_ref(n, lv)
            if r not in slots:
                raise Fail(f"chain: no slot {n!r}")
            return r

        keys = [key(n) for n in names]
        cpos = slots[castle].pos
        P = {k: slots[k].pos for k in keys}

        def d(a, b):
            return math.hypot(a[0] - b[0], a[2] - b[2])

        parent = {}
        if mode == "order":
            prev = None
            for k in keys:
                parent[k] = prev
                prev = k
        elif mode == "nearest":
            left = set(keys)
            cur = None
            cur_pos = cpos
            while left:
                k = min(left, key=lambda q: d(P[q], cur_pos))
                parent[k] = cur
                left.remove(k)
                cur, cur_pos = k, P[k]
            keys = list(parent.keys())
        elif mode == "tree":
            in_tree = {None: cpos}
            left = set(keys)
            while left:
                best = None
                for q in left:
                    for t, tp in in_tree.items():
                        dd = d(P[q], tp)
                        if best is None or dd < best[0]:
                            best = (dd, q, t)
                _, q, t = best
                parent[q] = t
                in_tree[q] = P[q]
                left.remove(q)
        else:
            raise Fail("mode is order, nearest or tree")
        first = op.get("first", "castle")
        every = int(op.get("every", 0))
        n = 0
        links = []
        castle_level = 1
        m = re.match(r"^castle:(\d+)$", first)
        if m:
            castle_level = int(m.group(1))
        for k in (keys if mode != "tree" else list(parent.keys())):
            p = parent[k]
            n += 1
            if p is None:
                spec = first
            elif every and n % every == 0 and mode != "tree":
                castle_level += 1
                spec = f"castle:{castle_level}"
            else:
                spec = p
            _, e = self.find_slot_entry(lv, k)
            e["requires"] = parse_req(spec, lv, None)
            links.append(f"{str(k).split('add:', 1)[-1]} <- {str(spec).split('add:', 1)[-1]}")
        # Print the links: a plan has to buy each slot's prerequisite first.
        return f"chained {len(keys)} slots ({mode}): " + ", ".join(links)

    # ---- props -------------------------------------------------------------------------------------------------------

    def prop_base(self, lv, mesh):
        if mesh.startswith("mesh:"):
            return mesh
        return "mesh:" + mesh

    def prop_colors(self, lv, base, colors):
        """A prop's "colors": None (the level's), a map name, or "original" (the map the mesh comes from: the library
        entry's "@Map" or home map; the host's own scenery already has the host's colours, so None)."""
        if colors is None:
            return None
        if colors != "original":
            if colors.split("(")[0] not in COLOR_MAPS:
                raise Fail(f"colors {colors!r}: use \"original\" or one of {', '.join(COLOR_MAPS)}")
            return colors
        name, _, at = base[5:].partition("@")
        if any(s["base"] == "mesh:" + name for s in self.ref.map(lv["host"]).get("scenery", [])) and at in ("", lv["host"]):
            return None
        if at:
            return at.split("(")[0]
        entry = next((m for m in self.ref.scenery.get("meshes", []) if m["name"] == name), None)
        return (entry["home"] if entry else lv["host"]).split("(")[0]

    @staticmethod
    def tilt(v, field):
        """A tilt angle (rotX/rotZ) as stored: degrees in -180..180."""
        a = Ops.numbers([v], 1, field)[0]
        return round(((a + 180) % 360) - 180, 2)

    @staticmethod
    def with_tilt(prop, op):
        """Puts op's rotX/rotZ into the prop, right after rotY (left out at 0, as the game writes them)."""
        out_ = {}
        for k, v in prop.items():
            if k in ("rotX", "rotZ"):
                continue
            out_[k] = v
            if k == "rotY":
                for t in ("rotX", "rotZ"):
                    a = Ops.tilt(op[t], t) if t in op else prop.get(t, 0)
                    if a:
                        out_[t] = a
        prop.clear()
        prop.update(out_)
        return prop

    def op_add_prop(self, op):
        """{"op":"add_prop","id":"rock1","mesh":"Rock A","at":[x,z],"rotY":0,"rotX":0,"rotZ":0,"scale":1,"solid":true,"colors":"original"}
        rotY turns it (its heading); rotX tips it about its own sideways axis (+ front down; 90 lays an upright piece on
        its back), rotZ rolls it onto its side. The tilts apply before rotY."""
        lv = self.level(op)
        props = ensure(lv, "props", [])
        pid = op.get("id") or self.new_prop_id(props, op["mesh"])
        if pid in self.prop_ids(props)[2]:
            raise Fail(f"prop {pid!r} exists (to re-apply a whole ops file, `apply --fresh`)")
        base = self.prop_base(lv, op["mesh"])
        prop = self.with_tilt({"id": pid, "base": base, "pos": self.pos(lv, op, strict=False), "rotY": float(op.get("rotY", 0)),
                               "scale": float(op.get("scale", 1)), "solid": bool(op.get("solid", True))}, op)
        if (colors := self.prop_colors(lv, base, op.get("colors"))) is not None:
            prop["colors"] = colors
        props.append(prop)
        self.note_prop(props, pid)
        return f"prop {pid}"

    def prop_ids(self, props):
        """The ids in a level's props list, kept across consecutive add_prop ops (run() drops it before any other op,
        which may rename or remove props): (ids, lowest free number per id stem)."""
        c = self._prop_ids
        if c is None or c[0] is not props or c[1] != len(props):
            c = self._prop_ids = [props, len(props), {p.get("id") for p in props}, {}]
        return c

    def note_prop(self, props, pid):
        """After props.append(...) of a prop with this id."""
        c = self._prop_ids
        if c is not None and c[0] is props and c[1] == len(props) - 1:
            c[2].add(pid)
            c[1] += 1
        else:
            self._prop_ids = None

    def new_prop_id(self, props, mesh):
        stem = re.sub(r"[^a-z0-9]+", "-", mesh.lower().replace("mesh:", "")).strip("-")[:40] or "prop"
        _, _, used, free = self.prop_ids(props)
        n = free.get(stem, 1)
        while f"{stem}-{n}" in used:
            n += 1
        free[stem] = n
        return f"{stem}-{n}"

    def op_set_prop(self, op):
        """{"op":"set_prop","id":"rock1","at":[x,z],"rotY":90,"rotX":90,"rotZ":0,"scale":1.2,"solid":false,"colors":"original"}:
        changes a placed prop, only the fields given ("ids": [...] for several; "pos": [x,y,z] instead of "at")."""
        lv = self.level(op)
        ids = op["ids"] if "ids" in op else [op["id"]]
        found = {p.get("id"): p for p in lv.get("props") or [] if p.get("id") in ids}
        if missing := [i for i in ids if i not in found]:
            raise Fail(f"no prop {', '.join(map(repr, missing))} on this level")
        for p in found.values():
            if "at" in op or "pos" in op:
                p["pos"] = self.pos(lv, op, strict=False)
            if "rotY" in op:
                p["rotY"] = float(op["rotY"])
            if "scale" in op:
                p["scale"] = float(op["scale"])
            if "solid" in op:
                p["solid"] = bool(op["solid"])
            if "colors" in op:
                c = self.prop_colors(lv, p["base"], op["colors"])
                if c is None:
                    p.pop("colors", None)
                else:
                    p["colors"] = c
            self.with_tilt(p, op)
        return f"set {len(found)} prop{'s' if len(found) != 1 else ''}"

    def op_remove_prop(self, op):
        lv = self.level(op)
        props = lv.get("props") or []
        ids = set(op["ids"] if "ids" in op else [op["id"]])
        lv["props"] = [p for p in props if p.get("id") not in ids]
        return f"removed {len(props) - len(lv['props'])} props"

    def op_scatter(self, op):
        """{"op":"scatter","mesh":["Cone Tree","Rock A"],"count":40,"circle":{"center":[x,z],"radius":20} | "rect":{"min":[x,z],"max":[x,z]}
            | "line":{"points":[[x,z],...],"width":6},"spacing":3,"scale":[0.8,1.3],"solid":true,"seed":1,"avoid":8}
        Random props in an area, at least `spacing` apart, `avoid` metres clear of slots and spawn lines."""
        lv = self.level(op)
        rng = random.Random(op.get("seed", 1))
        meshes = op["mesh"] if isinstance(op["mesh"], list) else [op["mesh"]]
        shape = Shape.from_op(op)
        spacing = float(op.get("spacing", 3))
        avoid = float(op.get("avoid", 6))
        smin, smax = (op.get("scale") or [1, 1]) if isinstance(op.get("scale"), list) else (op.get("scale", 1), op.get("scale", 1))
        slots, castle, _ = effective_slots(self.ref, lv)
        keep_clear = [(s.pos[0], s.pos[2]) for s in slots.values()]
        for line in self.lines(lv):
            keep_clear.append(tuple(line["center"][0::2]))
        props = ensure(lv, "props", [])
        placed = [(p["pos"][0], p["pos"][2]) for p in props]
        added = 0
        tries = 0
        x0, z0, x1, z1 = shape.bounds()
        while added < int(op["count"]) and tries < int(op["count"]) * 60:
            tries += 1
            x, z = rng.uniform(x0, x1), rng.uniform(z0, z1)
            if shape.weight(x, z) <= 0:
                continue
            if any(math.hypot(x - a, z - b) < spacing for a, b in placed):
                continue
            if any(math.hypot(x - a, z - b) < avoid for a, b in keep_clear):
                continue
            y = self.point(lv, x, z, strict=False)
            mesh = rng.choice(meshes)
            base = self.prop_base(lv, mesh)
            prop = {"id": self.new_prop_id(props, mesh), "base": base, "pos": [round(x, 2), y, round(z, 2)],
                    "rotY": round(rng.uniform(0, 360), 1), "scale": round(rng.uniform(smin, smax), 2), "solid": bool(op.get("solid", True))}
            if (colors := self.prop_colors(lv, base, op.get("colors"))) is not None:
                prop["colors"] = colors
            props.append(prop)
            self.note_prop(props, prop["id"])
            placed.append((x, z))
            added += 1
        return f"scattered {added} props"

    def lines(self, lv):
        return level_lines(self.ref, lv)

    # ---- spawn lines -------------------------------------------------------------------------------------------------

    def op_add_spawn_line(self, op):
        """{"op":"add_spawn_line","id":"north","at":[x,z],"rotY":0,"width":12,"ground":true,"large":true,"flying":true}"""
        lv = self.level(op)
        sl = ensure(lv, "spawnLines", {})
        adds = ensure(sl, "add", [])
        lid = op["id"]
        if not ID_RE.match(lid):
            raise Fail("spawn line id: a-z 0-9 . _ -")
        if any(a.get("id") == lid for a in adds):
            raise Fail(f"spawn line {lid!r} exists (to re-apply a whole ops file, `apply --fresh`)")
        ground_line = bool(op.get("ground", True)) or bool(op.get("large", True))   # flyer-only lines may sit off the ground
        adds.append({"id": lid, "pos": self.pos(lv, op, strict=ground_line), "rotY": float(op.get("rotY", 0)), "width": float(op.get("width", 10)),
                     "ground": bool(op.get("ground", True)), "large": bool(op.get("large", True)), "flying": bool(op.get("flying", True))})
        return f"spawn line {lid}"

    def op_move_spawn_line(self, op):
        """Moves an added line (by id) or the host's own (by name): "at"/"pos" its middle, "rotY" turns it."""
        lv = self.level(op)
        sl = ensure(lv, "spawnLines", {})
        for a in sl.get("add") or []:
            if a.get("id") == op["line"]:
                if "at" in op or "pos" in op:
                    a["pos"] = self.pos(lv, op, strict=bool(op.get("ground", a.get("ground", True))) or bool(op.get("large", a.get("large", True))))
                for k in ("rotY", "width", "ground", "large", "flying"):
                    if k in op:
                        a[k] = op[k]
                return f"moved {op['line']}"
        if not any(l["name"] == op["line"] for l in self.ref.map(lv["host"])["spawnLines"]):
            raise Fail(f"no spawn line {op['line']!r}")
        if op["line"] in (sl.get("remove") or []):
            raise Fail(f"{op['line']!r} is removed; restore it first ({{\"op\": \"remove_spawn_line\", \"line\": \"{op['line']}\", \"restore\": true}})")
        edits = ensure(sl, "edit", [])
        e = next((x for x in edits if x.get("ref") == op["line"]), None)
        if e is None:
            e = {"ref": op["line"]}
            edits.append(e)
        if "at" in op or "pos" in op:
            e["pos"] = self.pos(lv, op, strict=False)   # the maps' own lines often start off their ground
        if "rotY" in op:
            e["rotY"] = float(op["rotY"])
        return f"moved host line {op['line']}"

    def op_remove_spawn_line(self, op):
        """{"op":"remove_spawn_line","line":"north"}: deletes an added line, or takes a host line off the map
        (spawnLines.remove: no enemies, routes or markers). "restore": true puts a removed host line back."""
        lv = self.level(op)
        name = op["line"]
        sl = ensure(lv, "spawnLines", {})
        adds = sl.get("add") or []
        if any(a.get("id") == name for a in adds):
            if op.get("restore"):
                raise Fail(f"{name!r} is an added line; restore only applies to the map's own lines")
            sl["add"] = [a for a in adds if a.get("id") != name]
            if not sl["add"]:
                del sl["add"]
            result = f"removed {name}"
        else:
            host = [l["name"] for l in self.ref.map(lv["host"])["spawnLines"]]
            if name not in host:
                raise Fail(f"no spawn line {name!r}; the map's lines: {host}, added: {[a.get('id') for a in adds]}")
            removed = sl.get("remove") or []
            if op.get("restore"):
                if name not in removed:
                    raise Fail(f"{name!r} isn't removed")
                removed = [r for r in removed if r != name]
                result = f"restored host line {name}"
            else:
                if name not in removed:
                    removed.append(name)
                result = f"removed host line {name}"
            if removed:
                sl["remove"] = removed
            else:
                sl.pop("remove", None)
        if not sl:
            lv.pop("spawnLines", None)
        if not op.get("restore"):
            ws = find_waveset(self.data, lv.get("waveSet")) if lv.get("waveSet") else None
            n = sum(1 for night in (ws or {}).get("nights", []) for s in night.get("spawns", []) if s.get("line") == name)
            if n:
                result += f" (warning: {n} spawn group(s) in wave set {ws['id']!r} still use it; move them to another line)"
        return result

    # ---- waves -------------------------------------------------------------------------------------------------------

    def waveset_for(self, op, create=True):
        lv = self.level(op) if (op.get("level") or self.level_id or len(self.data.get("levels") or []) == 1) else None
        wsid = op.get("waveSet") or (lv.get("waveSet") if lv else None)
        if not wsid:
            if not lv or not create:
                raise Fail("say which wave set (\"waveSet\")")
            wsid = lv["id"] + "-nights"
            lv["waveSet"] = wsid
        ws = find_waveset(self.data, wsid)
        if ws is None:
            if not create:
                raise Fail(f"no wave set {wsid!r}")
            host = lv["host"] if lv else op["host"]
            ws = {"id": wsid, "name": op.get("name", wsid), "host": host, "nights": []}
            ensure(self.data, "waveSets", []).append(ws)
        return ws

    def op_waves(self, op):
        """{"op":"waves","startingGold":12,"nights":[{"difficulty":1,"warning":"","spawns":[{"unit":"vanilla:E Melee","count":6,
        "interval":0.8,"delay":0,"line":"north","gold":6,"elite":false}]}, ...]}: replaces the level's nights (creates
        the wave set if needed)."""
        ws = self.waveset_for(op)
        if "startingGold" in op:
            ws["startingGold"] = op["startingGold"]
        if "nights" in op:
            ws["nights"] = [self.night(n) for n in op["nights"]]
        if "name" in op:
            ws["name"] = op["name"]
        return f"wave set {ws['id']}: {len(ws['nights'])} nights"

    def night(self, n):
        out_ = {"difficulty": float(n.get("difficulty", 1)), "spawns": []}
        if n.get("warning"):
            out_["warning"] = n["warning"]
        for s in n.get("spawns") or []:
            unit = s["unit"]
            if not unit.startswith("vanilla:") and self.ref.unit(unit) and not self.local_unit(unit):
                unit = "vanilla:" + self.ref.unit(unit)["id"]
            sp = {"unit": unit, "count": int(s.get("count", 1)), "interval": float(s.get("interval", 0.5)), "delay": float(s.get("delay", 0)),
                  "line": s["line"], "gold": int(s.get("gold", 0))}
            if s.get("elite"):
                sp["elite"] = True
            out_["spawns"].append(sp)
        return out_

    def local_unit(self, uid):
        return any(u.get("id") == uid for u in self.data.get("units") or [])

    def op_night(self, op):
        """{"op":"night","night":3,...night fields}: sets (or appends, if night = count+1) one night."""
        ws = self.waveset_for(op)
        i = int(op["night"]) - 1
        n = self.night(op)
        if i == len(ws["nights"]):
            ws["nights"].append(n)
        elif 0 <= i < len(ws["nights"]):
            ws["nights"][i] = n
        else:
            raise Fail(f"night {i + 1}: the set has {len(ws['nights'])} nights")
        return f"night {i + 1}"

    def op_copy_waves(self, op):
        """{"op":"copy_waves","from":"Nordfels","lines":{"Spawn 1":"north"}}: the map's vanilla nights, spawn lines renamed."""
        h = self.ref.host(op["from"])
        if not h:
            raise Fail(f"no vanilla waves for {op['from']!r}")
        ws = self.waveset_for(op)
        rename = op.get("lines") or {}
        nights = copy.deepcopy(h["vanillaWaves"]["nights"])
        for n in nights:
            for s in n.get("spawns", []):
                s["line"] = rename.get(s["line"], s["line"])
        ws["nights"] = nights
        if "startingGold" not in ws:
            ws["startingGold"] = h["startingGold"]
        return f"copied {len(nights)} nights from {op['from']}"

    # ---- units -------------------------------------------------------------------------------------------------------

    def op_unit(self, op):
        """{"op":"unit","id":"brute","side":"enemy","base":"vanilla:E Melee","name":"Brute","stats":{"maxHp":200},...}:
        adds or replaces a custom unit (fields as in the guide)."""
        u = {k: v for k, v in op.items() if k != "op"}
        units = ensure(self.data, "units", [])
        units[:] = [x for x in units if x.get("id") != u["id"]]
        units.append(u)
        self.edited.add(u["id"])
        return f"unit {u['id']}"

    # ---- behaviour engine (level-building.md, "Behaviour"; logic-schema.json) -------------------------------------

    def logic(self, op):
        """The op's level's logic block (created on first use)."""
        return ensure(self.level(op), "logic", {})

    @staticmethod
    def _upsert(lst, item, key="id"):
        for i, x in enumerate(lst):
            if isinstance(x, dict) and x.get(key) is not None and x.get(key) == item.get(key):
                lst[i] = item
                return "replaced"
        lst.append(item)
        return "added"

    def _def(self, op, kind, what):
        d = {k: v for k, v in op.items() if k not in ("op", "level")}
        if not ID_RE.match(str(d.get("id", ""))):
            raise Fail(f"{what} needs an \"id\" (1-64 of a-z 0-9 . _ -)")
        self.edited.add(d["id"])
        return f"{what} {d['id']} " + self._upsert(ensure(self.data, kind, []), d)

    def op_entity(self, op):
        """{"op":"entity","id":"tree-missile","kind":"projectile","visual":{...},"projectile":{...},"rules":[...]}: adds or
        replaces a non-unit template (prop, projectile, area, pickup, logic). Fields: the guide's EntityDef."""
        if op.get("kind") not in ("prop", "projectile", "area", "pickup", "logic"):
            raise Fail("kind is prop | projectile | area | pickup | logic")
        return self._def(op, "entities", "entity")

    def op_status(self, op):
        """{"op":"status","id":"burning","duration":4,"stacking":"refresh","tick":1,"mods":{"moveSpeed":{"mult":0.8}},
        "flags":[],"rules":[...]}: adds or replaces a status."""
        return self._def(op, "statuses", "status")

    def op_preset(self, op):
        """{"op":"preset","id":"my-trap","name":"...","params":{"dmg":{"type":"number","default":10}},"rules":[...]}:
        adds or replaces a pack preset (used as {"preset": id, "with": {...}} in rules, or a place's "preset")."""
        return self._def(op, "presets", "preset")

    # ---- shared items: delete and rename ---------------------------------------------------------------------------

    _SHARED_OPS = {"unit": "units", "entity": "entities", "status": "statuses", "preset": "presets"}
    # Fields whose whole value is a name or prose, never a reference: a rename doesn't touch them.
    _PROSE = {"id", "name", "description", "labels", "label"}

    def _containers(self):
        """(label, json) for every level (with its nights) and shared item."""
        sets = {w.get("id"): w for w in self.data.get("waveSets") or []}
        for lv in self.data.get("levels") or []:
            yield f"level {lv.get('id')}", [lv, sets.get(lv.get("waveSet"))]
        for f in SHARED:
            for x in self.data.get(f) or []:
                if isinstance(x, dict):
                    yield f"{LOGIC_KIND_NAME.get(f, 'unit')} {x.get('id')}", x

    def users(self, field, iid):
        """Where a shared item is named (levels and other shared items), as "level gorge", "entity slime-pool"..."""
        found = []
        for label, tree in self._containers():
            own = isinstance(tree, dict) and tree.get("id") == iid and any(tree is x for x in self.data.get(field) or [])
            if not own and iid in mentions(tree):
                found.append(label)
        return found

    def _shared_item(self, op, verb):
        kind = op["op"][len(verb) + 1:]
        field = self._SHARED_OPS[kind]
        return kind, field

    def _remove_shared(self, op):
        kind, field = self._shared_item(op, "remove")
        iid = op["id"]
        lst = self.data.get(field) or []
        if not any(isinstance(x, dict) and x.get("id") == iid for x in lst):
            raise Fail(f"no {kind} {iid!r} in the {'library' if isinstance(self.store, LibraryRef) else 'pack'}")
        if isinstance(self.store, LibraryRef) and self.store.origin:
            raise Fail("open the whole library (pack \"library\") to delete shared items: other levels may use it")
        users = self.users(field, iid)
        if users and not op.get("force"):
            raise Fail(f"{kind} {iid!r} is used by {', '.join(users)}: change those first, or add \"force\": true to delete it anyway "
                       "(their references then break)")
        self.data[field] = [x for x in lst if not (isinstance(x, dict) and x.get("id") == iid)]
        self.edited.add(iid)
        return f"removed {kind} {iid}" + (f" (still named by {', '.join(users)})" if users else "") + (" (to library/trash)" if isinstance(self.store, LibraryRef) else "")

    def op_remove_unit(self, op):
        """{"op":"remove_unit","id":"brute"} (also remove_entity, remove_status, remove_preset): deletes a shared item.
        Refuses while a level or another item names it, listing them, unless "force": true. Library files go to
        library/trash."""
        return self._remove_shared(op)

    op_remove_entity = op_remove_unit
    op_remove_status = op_remove_unit
    op_remove_preset = op_remove_unit

    @staticmethod
    def _rewrite_text(s, old, new, field):
        """Rewrites the id inside text: is:/@@ (units, entities), has: (statuses) and quoted literals."""
        o = re.escape(old)
        if field in ("units", "entities"):
            s = re.sub(r"(\bis:|@@)" + o + r"(?![a-z0-9._-])", lambda m: m.group(1) + new, s)
        if field == "statuses":
            s = re.sub(r"(\bhas:)" + o + r"(?![a-z0-9._-])", lambda m: m.group(1) + new, s)
        return re.sub(r"(['\"])" + o + r"\1", lambda m: m.group(1) + new + m.group(1), s)

    def _rewrite(self, tree, old, new, field, key=None):
        if isinstance(tree, dict):
            for k in list(tree):
                tree[k] = self._rewrite(tree[k], old, new, field, k)
            return tree
        if isinstance(tree, list):
            for i, x in enumerate(tree):
                tree[i] = self._rewrite(x, old, new, field, key)
            return tree
        if not isinstance(tree, str) or key == "id":
            return tree
        if key not in self._PROSE:
            for pre in ("", "=", "unit:" if field == "units" else None):
                if pre is not None and tree.strip() == pre + old:
                    return pre + new
        return self._rewrite_text(tree, old, new, field)

    def _residue(self, tree, old, where, path=""):
        """JSON paths in tree that still name old (outside prose fields)."""
        if isinstance(tree, dict):
            for k, v in tree.items():
                if k not in self._PROSE:
                    self._residue(v, old, where, f"{path}.{k}" if path else k)
        elif isinstance(tree, list):
            for i, v in enumerate(tree):
                self._residue(v, old, where, f"{path}[{i}]")
        elif isinstance(tree, str) and old in mentions(tree):
            where.append(path)

    def op_rename_unit(self, op):
        """{"op":"rename_unit","from":"brute","to":"ogre-brute"} (also rename_entity, rename_status, rename_preset):
        changes a shared item's id and every reference to it in every level and shared item (spawns, waves, `entity`,
        `status`, `preset`, is:/@@/has: and quoted ids in expressions). Refuses, listing them, if any reference it
        can't rewrite safely remains. Display names don't change."""
        kind, field = self._shared_item(op, "rename")
        old, new = op["from"], op["to"]
        if not ID_RE.match(str(new)):
            raise Fail(f"\"to\" {new!r}: 1-64 of a-z 0-9 . _ -")
        if isinstance(self.store, LibraryRef) and self.store.origin:
            raise Fail("open the whole library (pack \"library\") to rename shared items: other levels may use it")
        lst = self.data.get(field) or []
        item = next((x for x in lst if isinstance(x, dict) and x.get("id") == old), None)
        if item is None:
            raise Fail(f"no {kind} {old!r}")
        clash = ("units", "entities") if field in ("units", "entities") else (field,)
        for f in clash:
            if any(isinstance(x, dict) and x.get("id") == new for x in self.data.get(f) or []):
                raise Fail(f"{new!r} is already a{'n' if f == 'entities' else ''} {LOGIC_KIND_NAME.get(f, 'unit')} id")
        same = [f for f in SHARED if f != field and any(isinstance(x, dict) and x.get("id") == old for x in self.data.get(f) or [])]
        if same:
            raise Fail(f"can't rename {kind} {old!r}: a{'n' if same[0] == 'entities' else ''} {LOGIC_KIND_NAME.get(same[0], 'unit')} has the same id, "
                       f"so its references can't be told apart; rename or remove that one first")
        users = self.users(field, old)
        for label, tree in self._containers():
            self._rewrite(tree, old, new, field)
        item["id"] = new
        left = []
        for label, tree in self._containers():
            where = []
            self._residue(tree, old, where)
            left += [f"{label}: {w}" for w in where]
        if left:
            raise Fail(f"can't rename {kind} {old!r}: these still name it in a way the rename can't rewrite safely (another kind "
                       f"with the same id, or text): {'; '.join(left[:20])}. Nothing was changed.")
        if isinstance(self.store, LibraryRef):
            folder = SHARED_FOLDER[field]
            if (folder, old) in self.store.meta:
                self.store.meta[(folder, new)] = self.store.meta[(folder, old)]
            self.store.renames.append((folder, old, new))
        self.edited |= {old, new}
        return f"renamed {kind} {old} -> {new}" + (f" (rewrote references in {', '.join(users)})" if users else "")

    op_rename_entity = op_rename_unit
    op_rename_status = op_rename_unit
    op_rename_preset = op_rename_unit

    def op_place(self, op):
        """{"op":"place","id":"plate1","entity":"<template>" | "preset":"timer","with":{...} | "kind":"area"|"path"|"point"|"logic"|...,
        "at":[x,z] | "pos":[x,y,z],"rotY":0,"name":"plate","active":true,"vars":{},"rules":[...], ...inline fields}: places
        an entity in the level (replaces one with the same id). A path takes "points":[[x,z],...] instead of at."""
        lv = self.level(op)
        d = {k: v for k, v in op.items() if k not in ("op", "level", "at")}
        if not ID_RE.match(str(d.get("id", ""))):
            raise Fail("place needs an \"id\" (1-64 of a-z 0-9 . _ -)")
        if not (d.get("entity") or d.get("preset") or d.get("kind")):
            raise Fail("give \"entity\" (a template id), \"preset\" (e.g. \"timer\") or an inline \"kind\"")
        if d.get("kind") == "path":
            pts = d.get("points")
            if not isinstance(pts, list) or len(pts) < 2:
                raise Fail("a path needs \"points\": [[x, z], ...] (2 or more)")
            d["points"] = [[round(c, 2) for c in self.numbers(pt, len(pt) if isinstance(pt, list) else 2, "points[]")] for pt in pts]
            if op.get("at") is not None or op.get("pos") is not None:
                d["pos"] = self.pos(lv, op, strict=False)
        else:
            d["pos"] = self.pos(lv, op, strict=False)
        return f"place {d['id']} " + self._upsert(ensure(self.logic(op), "places", []), d)

    def op_remove_place(self, op):
        """{"op":"remove_place","id":"plate1"}"""
        places = self.logic(op).get("places") or []
        n = len(places)
        places[:] = [x for x in places if x.get("id") != op["id"]]
        if len(places) == n:
            raise Fail(f"no place {op['id']!r}")
        return f"removed place {op['id']}"

    ATTACH_PREFIXES = ("slot:", "add:", "prop:", "line:")

    def rule_owner(self, op, create=True):
        """(rules list, description) for "on": level | unit:<id> | entity:<id> | status:<id> | place:<id> | a Studio key."""
        on = op.get("on")
        if not isinstance(on, str):
            raise Fail("\"on\" is level | unit:<id> | entity:<id> | status:<id> | place:<id> | slot:<name> | add:<id> | prop:<id> | line:<name> | castle")
        if on == "level":
            return ensure(self.logic(op), "rules", []), "the level script"
        kind, _, oid = on.partition(":")
        if kind in ("unit", "entity", "status"):
            lst = self.data.get({"unit": "units", "entity": "entities", "status": "statuses"}[kind]) or []
            owner = next((x for x in lst if x.get("id") == oid), None)
            if owner is None:
                raise Fail(f"no {kind} {oid!r} (add it first with the {'unit' if kind == 'unit' else kind} op)")
            return ensure(owner, "rules", []), on
        if kind == "place":
            owner = next((x for x in self.logic(op).get("places") or [] if x.get("id") == oid), None)
            if owner is None:
                raise Fail(f"no place {oid!r} in the level")
            return ensure(owner, "rules", []), on
        if on == "castle" or on.startswith(self.ATTACH_PREFIXES):
            a = ensure(ensure(self.logic(op), "attach", {}), on, {})
            return ensure(a, "rules", []), on
        raise Fail(f"unknown owner {on!r}")

    def op_rule(self, op):
        """{"op":"rule","on":"level"|"unit:<id>"|"entity:<id>"|"status:<id>"|"place:<id>"|"slot:Tower 3"|"castle"...,
        "rule":{"id":"split","when":"death","if":"...","do":[...]}} (or "rules":[...]): adds rules; one with the same
        id as an existing rule replaces it."""
        rules, where = self.rule_owner(op)
        new = op.get("rules") if op.get("rules") is not None else [op["rule"]]
        if not isinstance(new, list) or not all(isinstance(r, dict) for r in new):
            raise Fail("\"rule\" is one rule object, \"rules\" a list of them")
        notes = []
        for r in new:
            if r.get("id") is not None:
                notes.append(self._upsert(rules, r))
            else:
                rules.append(r)
                notes.append("added")
        return f"{len(new)} rule(s) on {where}: {', '.join(notes)}"

    def op_remove_rule(self, op):
        """{"op":"remove_rule","on":"unit:slime","id":"split"}"""
        rules, where = self.rule_owner(op)
        n = len(rules)
        rules[:] = [r for r in rules if r.get("id") != op["id"]]
        if len(rules) == n:
            raise Fail(f"no rule {op['id']!r} on {where}")
        return f"removed rule {op['id']} from {where}"

    BUILT_IN_INPUTS = {"enable", "disable", "toggle", "destroy", "start", "stop", "reverse", "goto", "show", "hide", "lock", "unlock"}

    def op_wire(self, op):
        """{"op":"wire","from":"place:lever"|"slot:Tower 3"|...,"event":"interact","to":"bridge","input":"up","value":0,
        "delay":0,"do":[...]}: a rule on the source that signals the target when the event fires. "to" is a place id or
        name (or an attach name). For an input that isn't built in (enable, disable, toggle, destroy, start, stop,
        reverse, goto, show, hide, lock, unlock, or a mover pose's name), "do" adds the target's listener rule."""
        src_rules, src = self.rule_owner({**op, "on": op["from"]})
        to = op["to"].lstrip("@")
        logic = self.logic(op)
        target = next((x for x in logic.get("places") or [] if to in (x.get("id"), x.get("name"))), None)
        attach = next(((k, a) for k, a in (logic.get("attach") or {}).items() if a.get("name") == to or k == op["to"]), None)
        if target is None and attach is None:
            raise Fail(f"no place or attached object named {to!r} (attach a name first: {{\"op\":\"attach\",\"key\":\"slot:...\",\"name\":\"{to}\"}})")
        if attach is not None and target is None:
            if not attach[1].get("name"):
                raise Fail(f"give {attach[0]!r} a name with the attach op so a wire can reach it")
            to = attach[1]["name"]
        sig = {"do": "signal", "name": op["input"], "to": "@" + to}
        if op.get("value") is not None:
            sig["value"] = op["value"]
        if op.get("delay"):
            sig["delay"] = op["delay"]
        when = op.get("event", "interact")
        src_rules.append({"when": when, "do": [sig], "note": f"wire: {when} -> {to}.{op['input']}"})
        poses = set(((target or {}).get("mover") or {}).get("pose") or {})
        if op["input"] in self.BUILT_IN_INPUTS or op["input"] in poses:
            return f"wire {src} {when} -> {to}.{op['input']} (built-in input)"
        if op.get("do") is None:
            return f"wire {src} {when} -> {to}.{op['input']} (nothing on {to} listens to {op['input']!r} yet: give \"do\")"
        listener = {"when": {"on": "signal", "name": op["input"]}, "do": op["do"], "note": f"wire: on {op['input']}"}
        (ensure(target, "rules", []) if target is not None else ensure(attach[1], "rules", [])).append(listener)
        return f"wire {src} {when} -> {to}.{op['input']} (+ listener)"

    def op_var(self, op):
        """{"op":"var","name":"souls","value":0}: a level variable and its start value (a number, bool, "'text'",
        "vec(x, z)", [] or {"type": ..., "value": ...}); with "on":"unit:<id>"|"entity:<id>"|"place:<id>"|<Studio key>: an
        entity variable's default there."""
        if op.get("on") in (None, "level"):
            ensure(self.logic(op), "vars", {})[op["name"]] = op["value"]
            return f"level var ${op['name']}"
        rules, where = self.rule_owner(op)
        on = op["on"]
        kind, _, oid = on.partition(":")
        if kind in ("unit", "entity"):
            owner = next(x for x in self.data.get({"unit": "units", "entity": "entities"}[kind]) or [] if x.get("id") == oid)
        elif kind == "place":
            owner = next(x for x in self.logic(op).get("places") or [] if x.get("id") == oid)
        else:
            owner = self.logic(op)["attach"][on]
        ensure(owner, "vars", {})[op["name"]] = op["value"]
        return f"var {op['name']} on {where}"

    def op_attach(self, op):
        """{"op":"attach","key":"slot:Tower 3"|"add:<id>"|"prop:<id>"|"line:<name>"|"castle","name":"northTower",
        "labels":["lit"],"vars":{},"statuses":[],"light":{"range":8}}: names, labels, vars and a light on an object the level
        already has."""
        key = op["key"]
        if not (key == "castle" or key.startswith(self.ATTACH_PREFIXES)):
            raise Fail("key is slot:<host slot name> | add:<added slot id> | prop:<prop id> | line:<spawn line> | castle")
        a = ensure(ensure(self.logic(op), "attach", {}), key, {})
        for f in ("name", "labels", "vars", "statuses", "targeting", "light"):
            if op.get(f) is not None:
                a[f] = op[f]
        return f"attach {key}" + (f" as @{a['name']}" if a.get("name") else "")

    def op_king_action(self, op):
        """{"op":"king_action","id":"horn","name":"War horn","when":"day","cooldown":0,"uses":{"count":1,"per":"day"}}:
        one of the level's three action keys (fires kingAction with this id)."""
        d = {k: v for k, v in op.items() if k not in ("op", "level")}
        lst = ensure(self.logic(op), "kingActions", [])
        r = self._upsert(lst, d)
        if len(lst) > 3:
            raise Fail("at most 3 king actions")
        return f"king action {d['id']} {r}"

    def op_hud(self, op):
        """{"op":"hud","id":"souls","text":"Souls {$souls}/10","icon":null,"visible":"$souls > 0"}: a level HUD line."""
        d = {k: v for k, v in op.items() if k not in ("op", "level")}
        return f"hud {d['id']} " + self._upsert(ensure(self.logic(op), "hud", []), d)

    def op_logic(self, op):
        """{"op":"logic","seed":1234,"caps":{"entities":600},"game":{"victory":"rules"}}: level logic settings (caps above
        the defaults may lag). `game` (the game's own rules: win, lose, castle, night flow, combat, building) is merged
        field by field; a null field goes back to vanilla."""
        lg = self.logic(op)
        for f in ("seed", "caps"):
            if f in op:
                lg[f] = op[f]
        if "game" in op:
            game = lg.setdefault("game", {})
            for k, v in (op["game"] or {}).items():
                if v is None:
                    game.pop(k, None)
                else:
                    game[k] = v
            if not game:
                lg.pop("game", None)
        return "logic settings"

    # ---- terrain -----------------------------------------------------------------------------------------------------

    def terrain(self, op):
        lv = self.level(op)
        if not lv.get("terrain"):
            raise Fail("no terrain grid yet: start with {\"op\":\"terrain_init\"}")
        return lv, self.terrain_of(lv)

    @staticmethod
    def missed(t, shape, n):
        """A note for a terrain op that changed nothing because its shape lies off the terrain grid."""
        if n:
            return ""
        x0, z0, x1, z1 = shape.bounds()
        X0, Z0, X1, Z1 = t.ox, t.oz, t.ox + t.w * t.cell, t.oz + t.h * t.cell
        if x1 < X0 or x0 > X1 or z1 < Z0 or z0 > Z1:
            return f" (the shape, x {x0:g}..{x1:g}, z {z0:g}..{z1:g}, is off the terrain grid: x {X0:g}..{X1:g}, z {Z0:g}..{Z1:g})"
        return ""

    def op_terrain_init(self, op):
        """{"op":"terrain_init","origin":[x,z],"cell":2,"size":[W,H],"y":<castle height>}: a flat grid (heights 0 = y)."""
        lv = self.level(op)
        castle = next((s for s in self.ref.map(lv["host"])["slots"] if s.get("castle")), None)
        if "origin" in op:
            origin, size = op["origin"], op["size"]
        else:
            g = self.ref.map(lv["host"]).get("grid")
            if not g:
                raise Fail("give origin and size")
            # The map's walkable area (the dump adds 12 m around it), like the game's own default flat field.
            cell = float(op.get("cell", 2))
            origin = [math.floor((g["origin"][0] + 12) / cell) * cell, math.floor((g["origin"][1] + 12) / cell) * cell]
            size = [min(400, int(math.ceil((g["size"][0] * g["cell"] - 24) / cell))), min(400, int(math.ceil((g["size"][1] * g["cell"] - 24) / cell)))]
        lv["terrain"] = {"origin": [float(origin[0]), float(origin[1])], "cell": float(op.get("cell", 2)), "size": [int(size[0]), int(size[1])],
                         "y": float(op.get("y", castle["pos"][1] if castle else 0)), "format": Terrain.FORMAT}
        if op.get("flat", True):
            lv["base"] = "flat"
        return f"terrain {size[0]}x{size[1]} cells"

    def op_terrain(self, op):
        """Height edits on a shape (circle/rect/line/polygon, with "falloff" metres):
        {"op":"terrain","mode":"set"|"raise"|"lower"|"smooth","height":4,"circle":{"center":[x,z],"radius":10},"falloff":4}
        Heights are metres relative to terrain.y (the castle's height by default)."""
        lv, t = self.terrain(op)
        shape = Shape.from_op(op)
        n = t.edit_heights(shape, op.get("mode", "set"), float(op.get("height", 0)), float(op.get("falloff", 0)))
        t.store(lv)
        return f"terrain: {n} corners" + self.missed(t, shape, n)

    def op_ramp(self, op):
        """{"op":"ramp","from":[x,z],"to":[x,z],"width":6,"fromHeight":0,"toHeight":5,"falloff":2}: a straight slope."""
        lv, t = self.terrain(op)
        a, b = self.numbers(op["from"], 2, "from"), self.numbers(op["to"], 2, "to")
        n = t.ramp(a, b, float(op.get("width", 6)), float(op["fromHeight"]), float(op["toHeight"]), float(op.get("falloff", 2)))
        t.store(lv)
        return f"ramp: {n} corners" + self.missed(t, Shape("line", {"points": [a, b], "width": float(op.get("width", 6))}), n)

    def op_paint(self, op):
        """{"op":"paint","material":"road"|"ground"|"high"|"low"|"earth"|"sand"|"rock", shape...}"""
        lv, t = self.terrain(op)
        mat = Terrain.MATERIALS.index(op["material"]) if op["material"] in Terrain.MATERIALS else int(op["material"])
        shape = Shape.from_op(op)
        n = t.paint_samples(shape, mat)
        t.store(lv)
        return f"painted {n} samples ({Terrain.PAINT_RES}x{Terrain.PAINT_RES} per cell)" + self.missed(t, shape, n)

    def op_block(self, op):
        """{"op":"block","on":true, shape...}: cells ground units (and the king) can't enter; fliers can. Any base."""
        lv = self.level(op)
        if not lv.get("terrain"):
            self.op_terrain_init({"level": lv["id"], "flat": False})
        t = self.terrain_of(lv)
        shape = Shape.from_op(op)
        n = t.paint_cells(shape, "blocked", 1 if op.get("on", True) else 0)
        t.store(lv)
        return f"blocked {n} cells" + self.missed(t, shape, n)

    def op_water(self, op):
        """{"op":"water","level":-1,"on":true, shape...}: water cells (unwalkable); the ground under them is dug to level-1."""
        lv, t = self.terrain(op)
        if "level" in op:
            t.water_level = float(op["level"])
        on = op.get("on", True)
        shape = Shape.from_op(op)
        cells = list(t.cells_in(shape))
        for idx in cells:
            t.water[idx] = 1 if on else 0
        n = len(cells)
        if on and op.get("dig", True):
            t.dig_water()
        elif not on and op.get("fill", True):
            # Land again: corners the water dug (and no remaining water cell touches) go back to "height" (default 0, the
            # terrain's y), so islands and causeways aren't left 2 m down. "fill": false keeps the dug ground.
            n_fill = t.fill_unwatered(cells, float(op.get("height", 0)))
            t.store(lv)
            return f"water off: {n} cells, {n_fill} corners raised back to {float(op.get('height', 0)):g} m"
        t.store(lv)
        return f"water {n} cells" + self.missed(t, shape, n)


# =====================================================================================================================
# Shapes and terrain
# =====================================================================================================================

class Shape:
    EXAMPLES = {"circle": '{"center": [x, z], "radius": 10}', "rect": '{"min": [x, z], "max": [x, z]}',
                "line": '{"points": [[x, z], ...], "width": 6}', "polygon": '{"points": [[x, z], [x, z], [x, z], ...]}'}

    def __init__(self, kind, spec, falloff=0.0):
        self.kind, self.spec, self.falloff = kind, spec, falloff

    @staticmethod
    def from_op(op):
        f = float(op.get("falloff", 0))
        for k in ("circle", "rect", "line", "polygon"):
            if k in op:
                s = op[k]
                if not isinstance(s, dict):
                    raise Fail(f"{k} is an object, e.g. {Shape.EXAMPLES[k]}")
                need = {"circle": ("center", "radius"), "rect": ("min", "max"), "line": ("points",), "polygon": ("points",)}[k]
                if missing := [n for n in need if n not in s]:
                    raise Fail(f"{k} needs {', '.join(missing)}, e.g. {Shape.EXAMPLES[k]}")
                for n in need:
                    if n == "radius":
                        Ops.numbers([s[n]], 1, f"{k}.radius")
                    elif n == "points":
                        pts = s[n]
                        if not isinstance(pts, list) or len(pts) < (3 if k == "polygon" else 1):
                            raise Fail(f"{k}.points is a list of at least {3 if k == 'polygon' else 1} [x, z]")
                        for i, p in enumerate(pts):
                            Ops.numbers(p, 2, f"{k}.points[{i}]")
                    else:
                        Ops.numbers(s[n], 2, f"{k}.{n}")
                return Shape(k, s, f)
        raise Fail("give a shape: circle {center,radius} | rect {min,max} | line {points,width} | polygon {points}")

    def dist(self, x, z):
        """Signed-ish distance outside the shape (<= 0 inside)."""
        s = self.spec
        if self.kind == "circle":
            cx, cz = s["center"]
            return math.hypot(x - cx, z - cz) - float(s["radius"])
        if self.kind == "rect":
            (ax, az), (bx, bz) = s["min"], s["max"]
            dx = max(ax - x, 0, x - bx)
            dz = max(az - z, 0, z - bz)
            return math.hypot(dx, dz) if (dx or dz) else -min(x - ax, bx - x, z - az, bz - z)
        if self.kind == "line":
            pts = s["points"]
            half = float(s.get("width", 4)) / 2
            best = min(seg_dist(x, z, pts[i], pts[i + 1]) for i in range(len(pts) - 1)) if len(pts) > 1 else math.hypot(x - pts[0][0], z - pts[0][1])
            return best - half
        if self.kind == "polygon":
            pts = s["points"]
            inside = point_in_poly(x, z, pts)
            edge = min(seg_dist(x, z, pts[i], pts[(i + 1) % len(pts)]) for i in range(len(pts)))
            return -edge if inside else edge
        raise Fail("bad shape")

    def weight(self, x, z):
        d = self.dist(x, z)
        if d <= 0:
            return 1.0
        if self.falloff <= 0 or d >= self.falloff:
            return 0.0
        t = 1 - d / self.falloff
        return t * t * (3 - 2 * t)

    def bounds(self):
        s = self.spec
        f = self.falloff
        if self.kind == "circle":
            cx, cz = s["center"]
            r = float(s["radius"]) + f
            return cx - r, cz - r, cx + r, cz + r
        if self.kind == "rect":
            return s["min"][0] - f, s["min"][1] - f, s["max"][0] + f, s["max"][1] + f
        pts = s["points"]
        pad = f + float(s.get("width", 0)) / 2
        return min(p[0] for p in pts) - pad, min(p[1] for p in pts) - pad, max(p[0] for p in pts) + pad, max(p[1] for p in pts) + pad


def seg_dist(x, z, a, b):
    ax, az = a
    bx, bz = b
    dx, dz = bx - ax, bz - az
    L = dx * dx + dz * dz
    t = 0 if L == 0 else max(0, min(1, ((x - ax) * dx + (z - az) * dz) / L))
    return math.hypot(x - (ax + t * dx), z - (az + t * dz))


def point_in_poly(x, z, pts):
    inside = False
    j = len(pts) - 1
    for i in range(len(pts)):
        xi, zi = pts[i]
        xj, zj = pts[j]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi + 1e-12) + xi:
            inside = not inside
        j = i
    return inside


def b64deflate(b):
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    return base64.b64encode(c.compress(bytes(b)) + c.flush()).decode()


def b64inflate(s, n):
    data = zlib.decompress(base64.b64decode(s), -15)
    if len(data) != n:
        raise Fail(f"terrain data has {len(data)} bytes, expected {n}")
    return data


class Terrain:
    """LevelDef.terrain: (W+1)x(H+1) corner heights (cm relative to y, delta-coded int16), materials per paint sample
    ((W*paintRes) x (H*paintRes), format 2; format 1 had one per cell), blocked and water bitmasks per cell; raw deflate
    + base64. See the guide's Terrain section."""
    MATERIALS = ["ground", "high", "low", "earth", "sand", "road", "rock"]
    # World floor (below -10 the game respawns the king); the ceiling is MAX_ABOVE over y (at least MAX_Y world).
    MIN_Y, MAX_Y, MAX_ABOVE = -8.0, 30.0, 30.0
    FORMAT, PAINT_RES = 2, 2

    @staticmethod
    def max_world(y):
        return max(Terrain.MAX_Y, y + Terrain.MAX_ABOVE)

    def __init__(self, t, host=None):
        self.ox, self.oz = t["origin"]
        self.cell = float(t["cell"])
        self.w, self.h = t["size"]
        self.y = float(t.get("y", 0))
        nc = (self.w + 1) * (self.h + 1)
        self.hts = [0] * nc
        if t.get("heights"):
            raw = b64inflate(t["heights"], nc * 2)
            hts = self.hts
            prev = 0
            for i, d in enumerate(struct.unpack(f"<{nc}h", raw)):
                prev = ((prev + d + 32768) & 0xFFFF) - 32768
                hts[i] = prev
        # A map's own ground: heights are stored as offsets from the host's ground; work on absolute ones.
        self.host = None
        if host is not None:
            self.host = []
            for j in range(self.h + 1):
                for i in range(self.w + 1):
                    hy = host(*self.corner(i, j))
                    self.host.append(None if hy is None else int(round((hy - self.y) * 100)))
            self.hts = [v + (hc or 0) for v, hc in zip(self.hts, self.host)]
        n = self.w * self.h
        fmt = int(t.get("format", 1))
        if fmt > self.FORMAT:
            raise Fail(f"terrain format {fmt} is newer than this palace.py ({self.FORMAT}): update the agent kit")
        R = self.PAINT_RES
        self.pw, self.ph = self.w * R, self.h * R
        self.materials = bytearray(self.pw * self.ph)
        if t.get("materials"):
            res = 1 if fmt == 1 else int(t.get("paintRes", R))
            raw = b64inflate(t["materials"], self.w * res * self.h * res)
            if res == R:
                self.materials = bytearray(raw.translate(self._MATERIAL_CLAMP))
            else:
                row = self.w * res
                for pz in range(self.ph):
                    for px in range(self.pw):
                        v = raw[(pz * res // R) * row + px * res // R]
                        self.materials[pz * self.pw + px] = v if v <= 6 else 0
        self.blocked = self.unbits(t.get("blocked"), n)
        w = t.get("water") or {}
        self.water_level = float(w.get("level", -1.0))
        self.water = self.unbits(w.get("cells"), n)

    # Unknown material ids read as ground (0); one byte -> its 8 bits, lowest first.
    _MATERIAL_CLAMP = bytes(v if v <= 6 else 0 for v in range(256))
    _BITS = [bytes((v >> k) & 1 for k in range(8)) for v in range(256)]

    @staticmethod
    def unbits(s, n):
        if not s:
            return bytearray(n)
        raw = b64inflate(s, (n + 7) // 8)
        bits = Terrain._BITS
        return bytearray(b"".join(bits[v] for v in raw)[:n])

    @staticmethod
    def bits(cells):
        b = bytearray((len(cells) + 7) // 8)
        for i, on in enumerate(cells):
            if on:
                b[i >> 3] |= 1 << (i & 7)
        return b

    def corner(self, i, j):
        return self.ox + i * self.cell, self.oz + j * self.cell

    def height_at(self, x, z):
        fx, fz = (x - self.ox) / self.cell, (z - self.oz) / self.cell
        if not (0 <= fx <= self.w and 0 <= fz <= self.h):
            return None
        i, j = min(int(fx), self.w - 1), min(int(fz), self.h - 1)
        tx, tz = fx - i, fz - j
        W = self.w + 1
        h00, h10 = self.hts[j * W + i], self.hts[j * W + i + 1]
        h01, h11 = self.hts[(j + 1) * W + i], self.hts[(j + 1) * W + i + 1]
        v = (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz
        return self.y + v / 100.0

    def clamp_cm(self, rel):
        world = max(self.MIN_Y, min(self.max_world(self.y), self.y + rel))
        return int(round((world - self.y) * 100))

    def corners_in(self, shape):
        x0, z0, x1, z1 = shape.bounds()
        i0, i1 = max(0, int((x0 - self.ox) / self.cell)), min(self.w, int(math.ceil((x1 - self.ox) / self.cell)))
        j0, j1 = max(0, int((z0 - self.oz) / self.cell)), min(self.h, int(math.ceil((z1 - self.oz) / self.cell)))
        for j in range(j0, j1 + 1):
            for i in range(i0, i1 + 1):
                x, z = self.corner(i, j)
                wgt = shape.weight(x, z)
                if wgt > 0:
                    yield j * (self.w + 1) + i, wgt

    def edit_heights(self, shape, mode, height, falloff):
        shape.falloff = falloff
        n = 0
        target = height * 100
        changes = []
        for idx, wgt in self.corners_in(shape):
            cur = self.hts[idx]
            if mode == "set":
                v = cur + (target - cur) * wgt
            elif mode == "raise":
                v = cur + target * wgt
            elif mode == "lower":
                v = cur - target * wgt
            elif mode == "smooth":
                W = self.w + 1
                i, j = idx % W, idx // W
                nb = [self.hts[jj * W + ii] for ii, jj in ((i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1)) if 0 <= ii <= self.w and 0 <= jj <= self.h]
                v = cur + (sum(nb) / len(nb) - cur) * wgt
            else:
                raise Fail("mode is set, raise, lower or smooth")
            changes.append((idx, self.clamp_cm(v / 100.0)))
            n += 1
        for idx, v in changes:
            self.hts[idx] = v
        return n

    def ramp(self, a, b, width, ha, hb, falloff):
        shape = Shape("line", {"points": [a, b], "width": width}, falloff)
        ax, az = a
        bx, bz = b
        L2 = (bx - ax) ** 2 + (bz - az) ** 2 or 1
        n = 0
        for idx, wgt in list(self.corners_in(shape)):
            W = self.w + 1
            x, z = self.corner(idx % W, idx // W)
            t = max(0, min(1, ((x - ax) * (bx - ax) + (z - az) * (bz - az)) / L2))
            target = (ha + (hb - ha) * t) * 100
            cur = self.hts[idx]
            self.hts[idx] = self.clamp_cm((cur + (target - cur) * wgt) / 100.0)
            n += 1
        return n

    def cells_in(self, shape):
        x0, z0, x1, z1 = shape.bounds()
        i0, i1 = max(0, int((x0 - self.ox) / self.cell)), min(self.w - 1, int((x1 - self.ox) / self.cell))
        j0, j1 = max(0, int((z0 - self.oz) / self.cell)), min(self.h - 1, int((z1 - self.oz) / self.cell))
        for j in range(j0, j1 + 1):
            for i in range(i0, i1 + 1):
                x, z = self.ox + (i + 0.5) * self.cell, self.oz + (j + 0.5) * self.cell
                if shape.weight(x, z) >= 0.5:
                    yield j * self.w + i

    def paint_samples(self, shape, value):
        """Materials: every paint sample (PAINT_RES per cell edge) whose centre is inside the shape (the game draws the
        result with smooth edges)."""
        step = self.cell / self.PAINT_RES
        x0, z0, x1, z1 = shape.bounds()
        i0, i1 = max(0, int((x0 - self.ox) / step)), min(self.pw - 1, int((x1 - self.ox) / step))
        j0, j1 = max(0, int((z0 - self.oz) / step)), min(self.ph - 1, int((z1 - self.oz) / step))
        n = 0
        for j in range(j0, j1 + 1):
            for i in range(i0, i1 + 1):
                if shape.weight(self.ox + (i + 0.5) * step, self.oz + (j + 0.5) * step) >= 0.5:
                    self.materials[j * self.pw + i] = value
                    n += 1
        return n

    def material_at(self, x, z):
        step = self.cell / self.PAINT_RES
        i, j = int((x - self.ox) // step), int((z - self.oz) // step)
        return self.materials[j * self.pw + i] if 0 <= i < self.pw and 0 <= j < self.ph else 0

    def paint_cells(self, shape, layer, value):
        arr = getattr(self, layer)
        n = 0
        for idx in self.cells_in(shape):
            arr[idx] = value
            n += 1
        return n

    def dig_water(self):
        W = self.w + 1
        limit = int(round((self.water_level - 1.0) * 100))
        for idx, on in enumerate(self.water):
            if not on:
                continue
            i, j = idx % self.w, idx // self.w
            for ci in (j * W + i, j * W + i + 1, (j + 1) * W + i, (j + 1) * W + i + 1):
                if self.hts[ci] > limit:
                    self.hts[ci] = limit

    def fill_unwatered(self, cells, height):
        """After cells stop being water: their corners that sit at or below the dig depth are set to `height` (m relative
        to y), so the new land is level to its edge (the bank's slope falls inside the neighbouring water cells, which
        are unwalkable anyway). Returns how many corners changed."""
        W = self.w + 1
        limit = int(round((self.water_level - 1.0) * 100))
        target = self.clamp_cm(height)
        changed = set()
        for idx in cells:
            i, j = idx % self.w, idx // self.w
            for c in (j * W + i, j * W + i + 1, (j + 1) * W + i, (j + 1) * W + i + 1):
                if self.hts[c] <= limit:
                    self.hts[c] = target
                    changed.add(c)
        return len(changed)

    def slope_deg(self, i, j):
        W = self.w + 1
        h = [self.hts[(j + dj) * W + i + di] / 100.0 for di, dj in ((0, 0), (1, 0), (0, 1), (1, 1))]
        gx = ((h[1] - h[0]) + (h[3] - h[2])) / (2 * self.cell)
        gz = ((h[2] - h[0]) + (h[3] - h[1])) / (2 * self.cell)
        return math.degrees(math.atan(math.hypot(gx, gz)))

    def sculpted_at(self, x, z):
        """Whether the point is on terrain of its own (always on a flat field; on a map's own ground, a moved cell)."""
        if self.host is None:
            return True
        i, j = int((x - self.ox) // self.cell), int((z - self.oz) // self.cell)
        if not (0 <= i < self.w and 0 <= j < self.h):
            return False
        W = self.w + 1
        for c in (j * W + i, j * W + i + 1, (j + 1) * W + i, (j + 1) * W + i + 1):
            if self.host[c] is not None and abs(self.hts[c] - self.host[c]) > 0:
                return True
        return False

    def store(self, lv):
        t = lv["terrain"]
        t["origin"], t["cell"], t["size"], t["y"] = [self.ox, self.oz], self.cell, [self.w, self.h], self.y
        t["format"] = self.FORMAT
        rel = self.hts if self.host is None else [v - (hc or 0) for v, hc in zip(self.hts, self.host)]
        if any(rel):
            deltas = [((v - prev + 32768) & 0xFFFF) - 32768 for v, prev in zip(rel, [0] + rel[:-1])]
            t["heights"] = b64deflate(struct.pack(f"<{len(deltas)}h", *deltas))
        else:
            t.pop("heights", None)
        if any(self.materials):
            t["materials"] = b64deflate(self.materials)
            t["paintRes"] = self.PAINT_RES
        else:
            t.pop("materials", None)
            t.pop("paintRes", None)
        if any(self.blocked):
            t["blocked"] = b64deflate(self.bits(self.blocked))
        else:
            t.pop("blocked", None)
        if any(self.water):
            t["water"] = {"level": self.water_level, "cells": b64deflate(self.bits(self.water))}
        else:
            t.pop("water", None)


# =====================================================================================================================
# Analysis: walk grid for the level, paths, economy, waves, checks
# =====================================================================================================================

# The game's ground graphs (`dev.py cmd "builder-level graphs"`): recast, 0.2 m voxels, agent radius 0.5 m on the
# small-enemy and king graphs; the large-enemy graph's radius and the walkable slope are the host map's own. A level's
# terrain is walkable up to the strictest ground graph's slope (GroundLayer), and its cliffs are walls for every graph.
# The clearances are what the offline model needs to reproduce the widths measured in game (level-building.md
# section 7). A reference built by a newer game copy carries them (map dump "graphs"); HOST_GRAPHS is the fallback,
# measured on game 2.14.
SMALL_CLEARANCE = 0.5
HOST_GRAPHS = {
    # host: (large-enemy radius m, walkable slope degrees)
    "Durststein": (1.5, 45), "Freifort": (2, 50), "Frostsee": (2, 20), "Moorweg": (2, 40), "Nordfels": (4, 30),
    "Sturmklamm": (2, 40), "Totend": (2, 50), "Uferwind": (2, 40), "Wildbach": (2, 40),
}


def host_graphs(ref, host):
    """(large-enemy radius, walkable slope) of a host map's ground graphs."""
    g = (ref.map(host) or {}).get("graphs") if ref else None
    if g and "Enemy Units L" in g:
        ground = [v["slope"] for k, v in g.items() if k != "Enemy Units Air"]
        return float(g["Enemy Units L"]["radius"]), float(min(ground))
    return HOST_GRAPHS.get(host, (4.0, 30.0))


def quat_matrix(q):
    """Rows of the rotation matrix of a Unity quaternion [x, y, z, w]."""
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def euler_matrix(ex, ey, ez):
    """Unity's Quaternion.Euler(ex, ey, ez) as a matrix: z first, then x, then y (degrees)."""
    def m(a, axis):
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        return {"x": [[1, 0, 0], [0, c, -s], [0, s, c]], "y": [[c, 0, s], [0, 1, 0], [-s, 0, c]], "z": [[c, -s, 0], [s, c, 0], [0, 0, 1]]}[axis]

    def mul(a, b):
        return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    return mul(m(ey, "y"), mul(m(ex, "x"), m(ez, "z")))


def box_extents(rot, half):
    """Half extents (world axes) of a box with these half extents turned by rot."""
    return [sum(abs(rot[i][j]) * half[j] for j in range(3)) for i in range(3)]


def prop_radius(ref, level, p):
    """Half the footprint of a placed prop, metres: the host map's own use of the mesh if it has one (map dump,
    footprint x/z), else the scenery library's (mesh size x/y before it's stood upright), else 1.5. A tilted prop
    (rotX/rotZ) gets its tilted bounding box's footprint, centred on its position (an approximation: the mesh's pivot
    isn't known here)."""
    name = p["base"][5:].split("@")[0]
    size = upright = None
    for s in ref.map(level["host"]).get("scenery", []):
        if s["base"] == "mesh:" + name and s.get("size"):
            size = s["size"]   # already stood upright: x, height, z
    if size is None:
        for m_ in ref.scenery.get("meshes", []):
            if m_["name"] == name and m_.get("size"):
                size, upright = m_["size"], m_.get("upright") or [-0.70711, 0, 0, 0.70711]   # mesh space, z up
    scale = float(p.get("scale", 1))
    tx, tz = float(p.get("rotX", 0) or 0), float(p.get("rotZ", 0) or 0)
    if size is None:
        return 1.5 * scale
    if not tx and not tz:
        return (max(size[0], size[1]) if upright else max(size[0], size[2])) / 2 * scale
    half = [v / 2 for v in size]
    if upright:
        half = box_extents(quat_matrix(upright), half)
    e = box_extents(euler_matrix(tx, 0, tz), half)
    return max(e[0], e[2]) * scale


class LevelGround:
    """The level's walkable ground as a grid: the map's sampled ground, or the flat terrain; minus blocked/water cells,
    solid props. Approximate: the game's navmesh is the truth (`palace.py paths` asks the game)."""

    def __init__(self, ref, level, slots):
        m = ref.map(level["host"])
        t = level.get("terrain")
        self.flat = level.get("base") == "flat"
        self.large_r, self.walk_slope = host_graphs(ref, level["host"])
        # Fine model (flat terrain): clearance per half-cell quad, see clearance(). corner_ok refines diagonal steps.
        self.clear = None
        if self.flat and t:
            T = Terrain(t)
            self.ox, self.oz, self.cell, self.w, self.h = T.ox, T.oz, T.cell, T.w, T.h
            self.height = [T.height_at(T.ox + (i + 0.5) * T.cell, T.oz + (j + 0.5) * T.cell) for j in range(T.h) for i in range(T.w)]
            self.clearance(T, level, ref)
        else:
            g = Grid(m["grid"])
            self.ox, self.oz, self.cell, self.w, self.h = g.ox, g.oz, g.cell, g.w, g.h
            self.kind = [list(r) for r in g.walk]
            self.height = [None if v == -32768 else v / 100 for v in g.heights]
            if t and t.get("blocked"):
                T = Terrain(t)
                for k, on in enumerate(T.blocked):
                    if on:
                        x, z = T.ox + (k % T.w + 0.5) * T.cell, T.oz + (k // T.w + 0.5) * T.cell
                        ij = self.index(x, z)
                        if ij and self.kind[ij[1]][ij[0]] in "sw":
                            self.kind[ij[1]][ij[0]] = "."
        # Solid props block, over most of their footprint (the fine model has them already).
        if self.clear is None:
            for p in level.get("props") or []:
                if p.get("solid", True):
                    self.stamp(p["pos"][0], p["pos"][2], prop_radius(ref, level, p) * 0.8, ".")
        # Buildings aren't obstacles here: enemies break through walls and buildings in their way.

    def clearance(self, T, level, ref):
        """Flat terrain, modelled the way the game's recast graphs see it: the ground mesh's triangles (split as
        GroundLayer splits each cell) steeper than the host's walkable slope, water/blocked columns, solid props and the field's edge
        are obstacles; every half-cell quad gets its distance to the nearest obstacle (its clearance), and a 2 m cell is
        open to an enemy size when one of its quads has that size's clearance. Steps between cells need clearance where
        the cells meet, so a diagonal causeway one quad wide still connects."""
        Q = 2
        QW, QH, qs = T.w * Q, T.h * Q, T.cell / Q
        W1 = T.w + 1
        lim = math.tan(math.radians(self.walk_slope)) * T.cell
        blocked = bytearray(QW * QH)

        def mark(i, j, qi, qj):
            blocked[(j * Q + qj) * QW + i * Q + qi] = 1

        for j in range(T.h):
            for i in range(T.w):
                k = j * T.w + i
                if T.water[k] or T.blocked[k]:
                    for qj in range(Q):
                        for qi in range(Q):
                            mark(i, j, qi, qj)
                    continue
                h00, h10 = T.hts[j * W1 + i] / 100.0, T.hts[j * W1 + i + 1] / 100.0
                h01, h11 = T.hts[(j + 1) * W1 + i] / 100.0, T.hts[(j + 1) * W1 + i + 1] / 100.0
                # A quad the cell's diagonal runs through is an obstacle if either triangle is steep: the game walls
                # off every steep triangle (GroundLayer's cliff walls) and erodes its graphs from those walls.
                if abs(h00 - h11) <= abs(h10 - h01):
                    # Split 00-11: triangle (00,01,11) above the diagonal, (00,11,10) below it.
                    a = math.hypot(h11 - h01, h01 - h00) > lim
                    b = math.hypot(h10 - h00, h11 - h10) > lim
                    steep = {(0, 1): a, (1, 0): b, (0, 0): a or b, (1, 1): a or b}
                else:
                    # Split 10-01: triangle (00,01,10) below the anti-diagonal, (10,01,11) above it.
                    c = math.hypot(h10 - h00, h01 - h00) > lim
                    d = math.hypot(h11 - h01, h11 - h10) > lim
                    steep = {(0, 0): c, (1, 1): d, (0, 1): c or d, (1, 0): c or d}
                for (qi, qj), s in steep.items():
                    if s:
                        mark(i, j, qi, qj)
        for p in level.get("props") or []:
            if not p.get("solid", True):
                continue
            r = prop_radius(ref, level, p) * 0.8
            x, z = p["pos"][0], p["pos"][2]
            i0, i1 = max(0, int((x - r - T.ox) / qs)), min(QW - 1, int((x + r - T.ox) / qs))
            j0, j1 = max(0, int((z - r - T.oz) / qs)), min(QH - 1, int((z + r - T.oz) / qs))
            for qj in range(j0, j1 + 1):
                for qi in range(i0, i1 + 1):
                    if math.hypot(T.ox + (qi + 0.5) * qs - x, T.oz + (qj + 0.5) * qs - z) <= r:
                        blocked[qj * QW + qi] = 1
        # Chamfer distance transform (1, sqrt 2) in quads; outside the field counts as an obstacle (its edge is a wall).
        big = 1e9
        d = [0.0 if b else big for b in blocked]
        r2 = math.sqrt(2)
        for j in range(QH):
            row, up = j * QW, (j - 1) * QW
            for i in range(QW):
                v = d[row + i]
                if v == 0.0:
                    continue
                v = min(v, d[row + i - 1] + 1 if i > 0 else 1, d[up + i] + 1 if j > 0 else 1,
                        d[up + i - 1] + r2 if i > 0 and j > 0 else 1, d[up + i + 1] + r2 if j > 0 and i < QW - 1 else 1)
                d[row + i] = v
        for j in range(QH - 1, -1, -1):
            row, dn = j * QW, (j + 1) * QW
            for i in range(QW - 1, -1, -1):
                v = d[row + i]
                if v == 0.0:
                    continue
                v = min(v, d[row + i + 1] + 1 if i < QW - 1 else 1, d[dn + i] + 1 if j < QH - 1 else 1,
                        d[dn + i + 1] + r2 if i < QW - 1 and j < QH - 1 else 1, d[dn + i - 1] + r2 if j < QH - 1 and i > 0 else 1)
                d[row + i] = v
        # Quad centre to the nearest obstacle quad's edge, metres.
        self.clear = [max(0.0, v * qs - qs / 2) for v in d]
        self.QW = QW
        self.kind = []
        for j in range(T.h):
            row = []
            for i in range(T.w):
                best = max(self.qclear(i, j, qi, qj) for qi in range(Q) for qj in range(Q))
                row.append("w" if best >= self.large_r else "s" if best >= SMALL_CLEARANCE else ".")
            self.kind.append(row)

    def qclear(self, i, j, qi, qj):
        return self.clear[(j * 2 + qj) * self.QW + i * 2 + qi]

    def step_ok(self, i, j, di, dj, large):
        """Whether a unit can step from cell (i, j) to its neighbour (i+di, j+dj): in the fine model, the quads on
        each side of the shared edge (or corner) need the clearance; otherwise no corner-cutting past closed cells."""
        ni, nj = i + di, j + dj
        if not (0 <= ni < self.w and 0 <= nj < self.h) or not self.ok(ni, nj, large):
            return False
        if self.clear is None:
            return not (di and dj) or (self.ok(i + di, j, large) and self.ok(i, j + dj, large))
        r = self.large_r if large else SMALL_CLEARANCE
        a = 1 if di > 0 else 0   # this cell's quad column (row) facing the neighbour
        b = 1 if dj > 0 else 0
        if di and dj:
            return self.qclear(i, j, a, b) >= r and self.qclear(ni, nj, 1 - a, 1 - b) >= r
        if di:
            return any(self.qclear(i, j, a, q) >= r and self.qclear(ni, nj, 1 - a, q) >= r for q in (0, 1))
        return any(self.qclear(i, j, q, b) >= r and self.qclear(ni, nj, q, 1 - b) >= r for q in (0, 1))

    def index(self, x, z):
        i, j = int((x - self.ox) // self.cell), int((z - self.oz) // self.cell)
        return (i, j) if 0 <= i < self.w and 0 <= j < self.h else None

    def stamp(self, x, z, r, c):
        n = int(math.ceil(r / self.cell))
        ij = self.index(x, z)
        if ij is None:
            return
        for j in range(ij[1] - n, ij[1] + n + 1):
            for i in range(ij[0] - n, ij[0] + n + 1):
                if 0 <= i < self.w and 0 <= j < self.h:
                    cx, cz = self.ox + (i + 0.5) * self.cell, self.oz + (j + 0.5) * self.cell
                    if math.hypot(cx - x, cz - z) <= r and self.kind[j][i] in "sw":
                        self.kind[j][i] = c

    def ok(self, i, j, large):
        k = self.kind[j][i]
        if k == "w" or (k == "s" and not large):
            return True
        if k != "s" or self.clear is not None:
            return False
        # The large graph is sampled at cell centres, so a narrow passage can drop out: an 's' cell next to a 'w'
        # cell counts for large enemies too.
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ni, nj = i + di, j + dj
            if 0 <= ni < self.w and 0 <= nj < self.h and self.kind[nj][ni] == "w":
                return True
        return False

    def nearest_ok(self, x, z, large, radius=12.0):
        ij = self.index(x, z)
        if ij is None:
            return None
        n = int(radius / self.cell)
        best = None
        for j in range(ij[1] - n, ij[1] + n + 1):
            for i in range(ij[0] - n, ij[0] + n + 1):
                if 0 <= i < self.w and 0 <= j < self.h and self.ok(i, j, large):
                    d = (i - ij[0]) ** 2 + (j - ij[1]) ** 2
                    if best is None or d < best[0]:
                        best = (d, i, j)
        return (best[1], best[2]) if best else None

    # 16 directions: the 8 neighbours plus knight moves, so costs are within ~3% of straight-line distances (8 alone
    # are up to 8% off, enough to pick the wrong one of two fords).
    STEPS = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
             (2, 1), (1, 2), (-1, 2), (-2, 1), (-2, -1), (-1, -2), (1, -2), (2, -1)]

    def move_ok(self, i, j, di, dj, large):
        if abs(di) <= 1 and abs(dj) <= 1:
            return self.step_ok(i, j, di, dj, large)
        # A knight move needs both ways round through the cells it passes.
        sx, sz = (1 if di > 0 else -1), (1 if dj > 0 else -1)
        a, b = ((sx, 0), (sx, sz)) if abs(di) == 2 else ((0, sz), (sx, sz))
        return all(self.step_ok(i, j, p[0], p[1], large) and self.step_ok(i + p[0], j + p[1], di - p[0], dj - p[1], large) for p in (a, b))

    def sight(self, a, b, large):
        """Whether a unit walks straight from cell a to cell b (every cell on the way open, every step allowed)."""
        (i0, j0), (i1, j1) = a, b
        n = max(abs(i1 - i0), abs(j1 - j0)) * 2
        if n == 0:
            return True
        prev = a
        for k in range(1, n + 1):
            c = (int(round(i0 + (i1 - i0) * k / n)), int(round(j0 + (j1 - j0) * k / n)))
            if c != prev:
                if not self.step_ok(prev[0], prev[1], c[0] - prev[0], c[1] - prev[1], large):
                    return False
                prev = c
        return True

    def route(self, start, goal_xz, large):
        """A* over cells (16 directions), then pulled taut. Returns (reachable, length m, polyline [[x,z],...], why)."""
        s = self.nearest_ok(start[0], start[1], large)
        g = self.nearest_ok(goal_xz[0], goal_xz[1], large, radius=14.0)
        if s is None:
            return False, None, [], "spawn point isn't on walkable ground"
        if g is None:
            return False, None, [], "no walkable ground next to the castle"
        dist = {s: 0.0}
        prev = {}
        pq = [(0.0, s)]
        gx, gz = g
        while pq:
            f, cur = heapq.heappop(pq)
            if cur == g:
                break
            ci, cj = cur
            for di, dj in self.STEPS:
                ni, nj = ci + di, cj + dj
                if not (0 <= ni < self.w and 0 <= nj < self.h):
                    continue
                nd = dist[cur] + math.hypot(di, dj)
                if nd < dist.get((ni, nj), 1e18) and self.move_ok(ci, cj, di, dj, large):
                    dist[(ni, nj)] = nd
                    prev[(ni, nj)] = cur
                    heapq.heappush(pq, (nd + math.hypot(ni - gx, nj - gz), (ni, nj)))
        if g not in dist:
            return False, None, [], "no walkable route"
        cells = [g]
        while cells[-1] != s:
            cells.append(prev[cells[-1]])
        cells.reverse()
        # Pull the path taut (as the game's funnel does): keep only the corners it has to turn at.
        taut = [cells[0]]
        k = 0
        while k < len(cells) - 1:
            far = k + 1
            while far + 1 < len(cells) and self.sight(cells[k], cells[far + 1], large):
                far += 1
            taut.append(cells[far])
            k = far
        pts = [[round(self.ox + (c[0] + 0.5) * self.cell, 1), round(self.oz + (c[1] + 0.5) * self.cell, 1)] for c in taut]
        length = sum(math.hypot(pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1]) for i in range(len(pts) - 1))
        return True, round(length, 1), pts, None


def removed_lines(level):
    """Names of the host's spawn lines the level takes off the map (spawnLines.remove)."""
    return list((level.get("spawnLines") or {}).get("remove") or [])


def level_lines(ref, level, include_removed=False):
    """Spawn lines of the level: host lines (moved by edits, minus removed ones) + added ones.
    [{name, center, points, ground, large, flying, added}] (+ "removed": true with include_removed)."""
    m = ref.map(level["host"])
    sl = level.get("spawnLines") or {}
    edits = {e["ref"]: e for e in sl.get("edit") or [] if e.get("ref")}
    removed = set(removed_lines(level))
    lines = []
    for l in m["spawnLines"]:
        if l["name"] in removed and not include_removed:
            continue
        c = list(l["center"])
        pts = [list(p) for p in l["points"]]
        e = edits.get(l["name"])
        if e and e.get("rotY"):
            # As the game turns it (Transform.RotateAround the middle, clockwise seen from above for positive yaw).
            yaw = math.radians(e["rotY"])
            cs, sn = math.cos(yaw), math.sin(yaw)
            pts = [[c[0] + (p[0] - c[0]) * cs + (p[2] - c[2]) * sn, p[1], c[2] - (p[0] - c[0]) * sn + (p[2] - c[2]) * cs] for p in pts]
        if e and e.get("pos"):
            dx, dz = e["pos"][0] - c[0], e["pos"][2] - c[2]
            pts = [[p[0] + dx, p[1], p[2] + dz] for p in pts]
            c = list(e["pos"])
        if l["name"] in removed:
            lines.append({"name": l["name"], "center": c, "points": pts, "ground": l["smallGround"], "large": l["bigGround"], "flying": l["flying"],
                          "added": False, "removed": True})
            continue
        lines.append({"name": l["name"], "center": c, "points": pts, "ground": l["smallGround"], "large": l["bigGround"], "flying": l["flying"], "added": False})
    for a in sl.get("add") or []:
        yaw = math.radians(a.get("rotY", 0))
        rx, rz = math.cos(yaw), -math.sin(yaw)
        hw = a.get("width", 10) / 2
        p = a["pos"]
        lines.append({"name": a["id"], "center": p, "points": [[p[0] - rx * hw, p[1], p[2] - rz * hw], [p[0] + rx * hw, p[1], p[2] + rz * hw]],
                      "ground": a.get("ground", True), "large": a.get("large", True), "flying": a.get("flying", True), "added": True})
    return lines


def used_lines(data, level):
    ws = find_waveset(data, level.get("waveSet")) if level.get("waveSet") else None
    if ws is None:
        return None
    return {s.get("line") for n in ws.get("nights", []) for s in n.get("spawns", [])}


def offline_routes(ref, data, level, slots, castle):
    ground = LevelGround(ref, level, slots)
    cpos = slots[castle].pos
    used = used_lines(data, level)
    routes = []
    for l in level_lines(ref, level):
        if not (l["ground"] or l["large"]):
            continue
        if used is not None and l["name"] not in used:
            continue
        start, goal = (l["center"][0], l["center"][2]), (cpos[0], cpos[2])
        ok, length, pts, why = ground.route(start, goal, False)
        r = {"line": l["name"], "graph": "small", "reachable": ok, "length": length, "why": why, "points": pts}
        if l["large"] and ok:
            # Large enemies (ogres, rams...) need wider passages; vanilla maps sometimes keep them off a plateau on
            # purpose. Like the game's `paths`, a line that spawns them shows their route when they have one.
            lok, llength, lpts, _ = ground.route(start, goal, True)
            r["largeReachable"] = lok
            if lok:
                r.update({"graph": "large", "length": llength, "points": lpts, "smallLength": length})
        elif l["large"]:
            r["largeReachable"] = False
        routes.append(r)
    return routes


def unit_info(ref, data, uref):
    """(name, maxHp, flying, large, boss) for a spawn's unit ref."""
    if uref.startswith("vanilla:"):
        u = ref.unit(uref[8:])
        if not u:
            return uref, 0, False, False, False
        return u["name"] or u["id"], u["maxHp"], u["flying"], u["large"], "Boss" in u.get("tags", [])
    uid = uref.split("/")[-1].replace("unit:", "")
    for u in data.get("units") or []:
        if u.get("id") == uid:
            base = ref.unit((u.get("base") or "")[8:]) or {}
            hp = (u.get("stats") or {}).get("maxHp") or base.get("maxHp", 0)
            return u.get("name") or uid, hp, base.get("flying", False), base.get("large", False), "Boss" in base.get("tags", [])
    return uref, 0, False, False, False


def _expanded_units(data):
    """Units with preset uses expanded (palace_logic), or as they are when the module isn't there."""
    try:
        pl = logic_module()
        return {u.get("id"): u for u in (pl.expand_presets(copy.deepcopy(data), []).get("units") or [])}
    except Exception:   # an estimate: fall back to the raw rules
        return {u.get("id"): u for u in data.get("units") or []}


def death_extras(ref, data, uref, units=None, depth=0, gen=0):
    """What one enemy adds when it dies, from its own `death` rules: (enemies spawned, their HP, gold given), followed
    through the spawned units' rules. Conditions are ignored except `self.$gen < N` (presets like split-on-death), so
    it's an upper estimate for chance-based rules."""
    units = units if units is not None else _expanded_units(data)
    uid = uref.split("/")[-1].replace("unit:", "")
    u = units.get(uid)
    if u is None or depth > 6:
        return 0, 0.0, 0
    count, hp, gold = 0, 0.0, 0
    for r in u.get("rules") or []:
        w = r.get("when")
        if (w if isinstance(w, str) else (w or {}).get("on")) != "death" or (isinstance(w, dict) and w.get("of")):
            continue
        cond = r.get("if") if isinstance(r.get("if"), str) else ""
        m = re.search(r"self\.\$(\w+)\s*<\s*(\d+)", cond)
        if m and gen >= int(m.group(2)):
            continue
        for a in r.get("do") or []:
            if a.get("do") == "gold" and isinstance(a.get("amount"), (int, float)):
                gold += int(a["amount"])
            if a.get("do") not in ("spawn", "spawnGroup"):
                continue
            ent = a.get("entity") or a.get("unit")
            n = a.get("count", 1)
            if not isinstance(ent, str) or not isinstance(n, (int, float)):
                continue
            if ent.startswith("="):
                if ent.replace(" ", "") != "=self.template":
                    continue
                ent = uid
            _, uhp, _, _, _ = unit_info(ref, data, ent if ent.startswith("vanilla:") else "unit:" + ent)
            sub = death_extras(ref, data, ent, units, depth + 1, gen + 1) if not ent.startswith("vanilla:") else (0, 0.0, 0)
            count += int(n) * (1 + sub[0])
            hp += int(n) * (uhp + sub[1])
            gold += int(n) * sub[2]
    return count, hp, gold


def wave_summary(ref, data, ws):
    rows = []
    units = _expanded_units(data) if data else {}
    for i, n in enumerate(ws.get("nights", [])):
        diff = float(n.get("difficulty", 1))
        count = hp = gold = fly = large = boss = elite = 0
        dur = 0.0
        per_line = {}
        kinds = {}
        for s in n.get("spawns", []):
            name, uhp, f, l, b = unit_info(ref, data, s["unit"])
            c = int(s.get("count", 1))
            mult = diff * (4 if s.get("elite") else 1)
            dc, dhp, dgold = death_extras(ref, data, s["unit"], units) if units else (0, 0.0, 0)
            count += c * (1 + dc)
            hp += c * (uhp + dhp) * mult
            gold += c * dgold
            gold += int(s.get("gold", 0))
            fly += c if f else 0
            large += c if l else 0
            boss += c if b else 0
            elite += c if s.get("elite") else 0
            dur = max(dur, float(s.get("delay", 0)) + max(0, c - 1) * float(s.get("interval", 0.5)))
            per_line[s.get("line")] = per_line.get(s.get("line"), 0) + c
            kinds[name] = kinds.get(name, 0) + c
        rows.append({"night": i + 1, "difficulty": diff, "enemies": count, "effectiveHp": round(hp), "gold": gold, "flying": fly, "large": large,
                     "bosses": boss, "elite": elite, "spawnSeconds": round(dur, 1), "lines": per_line, "units": kinds})
    return rows


def economy_sim(slots, castle, start_gold, wave_gold, nights):
    """A greedy 'all-in on economy' player: each day, buys the step with the best income per gold that pays back
    before the run ends (unlock steps count half the income they open up), until nothing affordable is worth it.
    Gold from enemies arrives at night; buildings pay at dawn, harbours growing and mines running dry as they do in
    the game (`Earner`). Returns per-day rows. An upper bound on economy: real players also spend on defence."""
    earners = {k: Earner(s) for k, s in slots.items()}
    level = {k: 0 for k in slots}
    gold = start_gold
    rows = []

    def available(k):
        s = slots[k]
        if s.follows:
            return False
        if s.req is None:
            return True
        a, lv = s.req
        if a not in level:
            return False
        return level[a] > lv

    def cap_ok(k):
        s = slots[k]
        if s.cap is None or s.cap <= -100 or level[k] == 0:
            return True
        return level[k] + 1 <= level.get(castle, 0) + s.cap

    for day in range(1, nights + 1):
        bought = []
        dawns_left = nights - day + 1
        while True:
            best = None
            for k, s in slots.items():
                if not available(k) or level[k] >= len(s.upgrades) or not cap_ok(k):
                    continue
                step = s.upgrades[level[k]]
                if step.get("disabled"):
                    continue
                cost = step["cost"]
                if cost > gold:
                    continue
                # Gold per dawn it adds, on average over the dawns left.
                inc = earners[k].gain(dawns_left) / dawns_left
                unlock = 0
                for k2, s2 in slots.items():
                    if s2.req and s2.req[0] == k and s2.req[1] == level[k] and s2.upgrades:
                        unlock += Earner(s2).gain(dawns_left) / dawns_left
                if k == castle:
                    # A castle level also lifts the upgrade cap of every capped building.
                    for k2, s2 in slots.items():
                        if k2 != castle and s2.cap is not None and s2.cap > -100 and 0 < level[k2] < len(s2.upgrades) \
                                and level[k2] + 1 == level[castle] + 1 + s2.cap:
                            unlock += earners[k2].gain(dawns_left) / dawns_left
                value = inc + 0.5 * unlock
                if k == castle and level[k] == 0:
                    value = 1e6  # the castle first
                if value <= 0 or (k != castle and cost > 0 and cost / max(value, 1e-9) > dawns_left):
                    continue
                score = value / max(cost, 0.5)
                if best is None or score > best[0]:
                    best = (score, k, cost)
            if best is None:
                break
            _, k, cost = best
            gold -= cost
            level[k] += 1
            earners[k].buy()
            bought.append(f"{slots[k].label} L{level[k]}")
        dusk = gold
        gold += wave_gold[day - 1] if day - 1 < len(wave_gold) else 0
        income = sum(e.income for e in earners.values())
        gold += sum(e.dawn() for e in earners.values())
        rows.append({"day": day, "bought": bought, "goldAtDusk": dusk, "incomePerDawn": income, "goldAfterDawn": gold})
    return rows


def level_waveset(ref, data, level):
    ws = find_waveset(data, level["waveSet"]) if level.get("waveSet") else None
    if ws is None:
        h = ref.host(level["host"])
        return (h["vanillaWaves"] if h else {"nights": []}), True
    return ws, False


# =====================================================================================================================
# Checks
# =====================================================================================================================

def unit_world(slot):
    """World (x, z) of a training slot's units: offsets in the slot's frame (x right, z forward) turned by its yaw."""
    a = math.radians(slot.rot or 0)
    c, s_ = math.cos(a), math.sin(a)
    x0, z0 = slot.pos[0], slot.pos[2]
    return [(x0 + ox * c + oz * s_, z0 - ox * s_ + oz * c) for ox, oz in slot.units]


MODEL_LOOKS = ("own", "unit")
HEX_COLOR = re.compile(r"^#?[0-9a-fA-F]{6}$")
MODEL_MOTIONS = ("auto", "bob", "none")


def check_unit(ref, u):
    """(path, message) errors for one UnitDef: its base and its visuals.mesh model (asset:, mesh: or vanilla:)."""
    errs = []
    base = u.get("base") or ""
    if not base.startswith("vanilla:"):
        errs.append((".base", "must be \"vanilla:<id>\" (`palace.py ref enemies`)"))
    elif ref.catalog.get("units") and not ref.unit(base[8:]):
        errs.append((".base", f"no vanilla unit {base[8:]!r} (`palace.py ref enemies`)"))
    v = u.get("visuals") or {}
    mesh = v.get("mesh")
    if mesh is not None:
        kind, _, mid = str(mesh).partition(":")
        if kind == "mesh":
            name = mid.split("@")[0]
            if ref.scenery.get("meshes") and not any(x["name"] == name for x in ref.scenery["meshes"]):
                errs.append((".visuals.mesh", f"no scenery mesh {name!r} in the library (`palace.py ref scenery`; a map's own mesh works on that map only)"))
        elif kind == "vanilla":
            if ref.catalog.get("units") and not ref.unit(mid):
                errs.append((".visuals.mesh", f"no unit {mid!r} (`palace.py ref enemies`, `ref enemies --player`)"))
        elif kind != "asset" or not mid:
            errs.append((".visuals.mesh", "must be \"asset:<key>\", \"mesh:<scenery mesh>\" or \"vanilla:<unit id>\""))
    for key in ("meshRotX", "meshRotY", "meshRotZ"):
        if v.get(key) is not None and not -360 <= float(v[key]) <= 360:
            errs.append((f".visuals.{key}", "between -360 and 360"))
    off = v.get("meshOffset")
    if off is not None and (not isinstance(off, list) or len(off) != 3 or any(not -50 <= float(c) <= 50 for c in off)):
        errs.append((".visuals.meshOffset", "[x, y, z], each between -50 and 50"))
    if v.get("meshLook") is not None and v["meshLook"] not in MODEL_LOOKS:
        errs.append((".visuals.meshLook", f"one of {MODEL_LOOKS}"))
    if v.get("motion") is not None and v["motion"] not in MODEL_MOTIONS:
        errs.append((".visuals.motion", f"one of {MODEL_MOTIONS}"))
    for key in ("tint",):
        if v.get(key) is not None and not HEX_COLOR.match(str(v[key])):
            errs.append((f".visuals.{key}", '"#rrggbb"'))
    colors = v.get("colors")
    if colors is not None:
        if not isinstance(colors, dict):
            errs.append((".visuals.colors", '{"body": "#rrggbb", "<material>": "#rrggbb"}'))
        else:
            for part, hexc in colors.items():
                if not str(part).strip():
                    errs.append((".visuals.colors", "a part name can't be empty"))
                elif not HEX_COLOR.match(str(hexc)):
                    errs.append((f".visuals.colors.{part}", '"#rrggbb"'))
    return errs


# Damage targets the Enemy Studio's readout uses (the game's tags on those buildings and the king).
ENEMY_TARGETS = {
    "house": ["PlayerOwned", "Building", "House", "InfrastructureEconomy"],
    "wall": ["PlayerOwned", "Building", "Wall", "WallOrTower"],
    "tower": ["PlayerOwned", "Building", "Tower", "WallOrTower"],
    "king": ["PlayerOwned", "Player"],
}


def hit_damage(mods, tags, mult=1.0):
    """The game's damage formula (DamageModifyer.CalculateDamageOnTarget) for one hit."""
    d = 0.0
    for m in mods or []:
        if all(t in tags for t in m.get("tags") or []):
            d += float(m.get("add") or 0)
            d *= float(m.get("mult", 1) if m.get("mult") is not None else 1)
    return d * mult


def time_to_kill(hp, per_hit, cooldown):
    """(hits, seconds) or None: the first hit lands at once."""
    if per_hit <= 0 or hp <= 0:
        return None
    hits = max(1, math.ceil(hp / per_hit - 1e-4))
    return hits, round((hits - 1) * max(cooldown, 0), 2)


def enemy_card(ref, u, difficulty=1.0, elite=False):
    """A custom enemy's numbers as it spawns: its overrides over the base unit and weapon, damage per hit and per
    second against buildings and the king, and how long each vanilla troop takes to kill one (as the Enemy Studio's
    readout shows them)."""
    base = ref.unit((u.get("base") or "")[8:]) or {}
    st = u.get("stats") or {}
    w = u.get("weapon") or {}
    wbase = w.get("base") or "inherit"
    wid = base.get("weapon") if wbase == "inherit" else wbase.replace("vanilla:", "")
    cw = next((x for x in ref.catalog.get("weapons", []) if x["id"] == wid), None) or {}
    mods = w.get("damage") if w.get("damage") is not None else cw.get("damage") or []
    tags = set(base.get("tags", [])) - set((u.get("tags") or {}).get("remove") or []) | set((u.get("tags") or {}).get("add") or [])
    hp_mult = difficulty * (4 if elite else 1)
    dmg_mult = float(w.get("damageMult") or 1) * (1 + (difficulty - 1) * 0.5) * (3 if elite else 1)
    hp = float(st.get("maxHp") or base.get("maxHp") or 0) * hp_mult
    cd = float(st.get("attackCooldown") or base.get("attackCooldown") or 0)
    card = {"id": u.get("id"), "name": u.get("name"), "base": u.get("base"), "hp": round(hp, 1),
            "speed": st.get("moveSpeed") or base.get("moveSpeed"), "range": st.get("attackRange") or base.get("attackRange"),
            "cooldown": cd, "flying": base.get("flying", False), "large": base.get("large", False),
            "tags": sorted(t for t in tags if t not in ("EnemyOwned", "AUTO_Alive")), "model": (u.get("visuals") or {}).get("mesh") or "base",
            "deals": {}, "killedBy": {}}
    for name, ttags in ENEMY_TARGETS.items():
        per = hit_damage(mods, ttags, dmg_mult)
        card["deals"][name] = {"perHit": round(per, 2), "perSecond": round(per / cd, 2) if cd > 0 else 0}
    for p in ref.catalog.get("units", []):
        if p.get("side") != "player":
            continue
        pw = next((x for x in ref.catalog.get("weapons", []) if x["id"] == p.get("weapon")), None)
        if not pw or not pw.get("damage"):
            continue  # its damage isn't in the catalog (projectile or spell effects)
        ttk = time_to_kill(hp, hit_damage(pw.get("damage"), tags), float(p.get("attackCooldown") or 0))
        card["killedBy"][p["id"]] = {"hits": ttk[0], "seconds": ttk[1]} if ttk else "never"
        per = hit_damage(mods, set(p.get("tags", [])), dmg_mult)
        card["deals"][p["id"]] = {"perHit": round(per, 2), "perSecond": round(per / cd, 2) if cd > 0 else 0}
    return card


def check_pack(ref, data):
    issues = []

    def err(path, msg):
        issues.append({"severity": "error", "path": path, "message": msg})

    def warn(path, msg):
        issues.append({"severity": "warning", "path": path, "message": msg})

    if data.get("format") != "palace-builder":
        err("format", "must be \"palace-builder\"")
    if not ID_RE.match(str(data.get("id", ""))):
        err("id", "1-64 of a-z 0-9 . _ -")
    unit_ids = {u.get("id") for u in data.get("units") or []}
    for ui, u in enumerate(data.get("units") or []):
        for path, msg in check_unit(ref, u):
            err(f"units[{ui}]" + path, msg)
    for wi, ws in enumerate(data.get("waveSets") or []):
        p = f"waveSets[{wi}]"
        host = ws.get("host")
        if not ref.has_map(host):
            err(p + ".host", f"unknown map {host!r} (or not in the reference; `palace.py ref build --maps {host}`)")
            continue
        level_lines_ = {l["name"] for l in ref.map(host)["spawnLines"]}
        for lv in data.get("levels") or []:
            if lv.get("waveSet") == ws.get("id"):
                level_lines_ |= {a.get("id") for a in ((lv.get("spawnLines") or {}).get("add") or [])}
        if not ws.get("nights"):
            err(p + ".nights", "no nights yet: add them with the waves op (the game refuses a wave set without nights)")
        for ni, n in enumerate(ws.get("nights") or []):
            if not n.get("spawns"):
                err(f"{p}.nights[{ni}].spawns", "needs at least one spawn")
            for si, s in enumerate(n.get("spawns") or []):
                sp = f"{p}.nights[{ni}].spawns[{si}]"
                u = s.get("unit", "")
                if u.startswith("vanilla:"):
                    cu = ref.unit(u[8:])
                    if not cu:
                        err(sp + ".unit", f"no vanilla unit {u[8:]!r} (`palace.py ref enemies`)")
                    elif cu.get("side") != "enemy":
                        err(sp + ".unit", f"{u} is a player unit")
                elif "/" not in u and u.replace("unit:", "") not in unit_ids:
                    err(sp + ".unit", f"no unit {u!r} in this pack")
                if s.get("line") not in level_lines_:
                    err(sp + ".line", f"no spawn line {s.get('line')!r} on {host} or added by a level using this set; lines: {sorted(level_lines_)}")
                if int(s.get("count", 1)) < 1:
                    err(sp + ".count", "at least 1")
    for li, lv in enumerate(data.get("levels") or []):
        p = f"levels[{li}]"
        if not ref.has_map(lv.get("host")):
            err(p + ".host", f"unknown map {lv.get('host')!r}")
            continue
        if lv.get("waveSet") and not find_waveset(data, lv["waveSet"]):
            err(p + ".waveSet", f"no wave set {lv['waveSet']!r}")
        slots, castle, warns = effective_slots(ref, lv)
        for w in warns:
            err(p + ".buildSlots", w)
        m = ref.map(lv["host"])
        host_names = {s["name"] for s in m["slots"]}
        bs = lv.get("buildSlots") or {}
        for ai, a in enumerate(bs.get("add") or []):
            ap = f"{p}.buildSlots.add[{ai}]"
            if not ID_RE.match(str(a.get("id", ""))):
                err(ap + ".id", "1-64 of a-z 0-9 . _ -")
        # Requirements: dangling refs and cycles; slots that can never unlock.
        for k, s in slots.items():
            if s.req and s.req[0] not in slots:
                where = f"{p}.buildSlots" + (f".add[id={k[4:]}]" if k.startswith("add:") else f".edit[ref={k}]")
                if s.req[0] == "__removed__":
                    err(where + ".requires", f"{s.label}: its base slot's prerequisite isn't on this empty map; set \"requires\" (e.g. \"castle\")")
                else:
                    err(where + ".requires", f"{s.label} needs {s.req[0]!r}, which isn't on the level: it will never appear")
        for k in slots:
            seen = []
            cur = k
            while cur in slots and slots[cur].req and cur not in seen:
                seen.append(cur)
                cur = slots[cur].req[0]
            if cur in seen:
                err(f"{p}.buildSlots", f"requirement loop: {' -> '.join(slots[c].label for c in seen)}")
                break
        for k, s in slots.items():
            if s.req and s.req[0] in slots:
                pre = slots[s.req[0]]
                if s.req[1] >= len(pre.upgrades):
                    err(f"{p}.buildSlots", f"{s.label} needs {pre.label} at level {s.req[1] + 1}, but it only has {len(pre.upgrades)} levels")
        # Placement against the ground.
        ground = LevelGround(ref, lv, {})
        flat = lv.get("base") == "flat" and lv.get("terrain")
        T = Terrain(lv["terrain"]) if flat else None
        g = None if flat else Grid(m["grid"])
        # A map's own ground sculpted by the level: the pack holds offsets from the host's ground.
        S = Terrain(lv["terrain"], host=g.height) if not flat and lv.get("terrain") and lv["terrain"].get("heights") else None
        # A Bridge (Uferwind's) is built over water or a gap: its centre is off the ground by design.
        pts = [(s.label, s.pos) for s in slots.values() if s.kind != "Bridge"
               and (s.added or any(e.get("ref") == s.key and e.get("pos") for e in bs.get("edit") or []))]
        for label, pos in pts:
            x, y, z = pos
            gy = T.height_at(x, z) if T else S.height_at(x, z) if S and S.sculpted_at(x, z) else g.height(x, z)
            if gy is None:
                err(f"{p}.buildSlots", f"{label} at ({x:.0f}, {z:.0f}) is off the map's ground")
                continue
            if abs(gy - y) > 1.0:
                warn(f"{p}.buildSlots", f"{label}: y={y:.2f} but the ground there is {gy:.2f} (use \"at\" to set it automatically)")
            # The footprint, not just the centre cell: on a narrow or diagonal causeway the centre cell can be the
            # sloped edge while the king walks right next to it.
            ij = ground.index(x, z)
            if ij and not any(ground.kind[j][i] in "sw" for j in range(max(0, ij[1] - 1), min(ground.h, ij[1] + 2))
                              for i in range(max(0, ij[0] - 1), min(ground.w, ij[0] + 2))):
                warn(f"{p}.buildSlots", f"{label} at ({x:.0f}, {z:.0f}) isn't on walkable ground (cliff, water or blocked): the king may not reach it")
        # Training slots' units (barracks, archery range...): where each stands, from the reference's offsets.
        moved = {e.get("ref") for e in bs.get("edit") or [] if e.get("pos") or e.get("rotY") is not None or e.get("units")}
        for s in slots.values():
            if not s.units or not (s.added or s.key in moved or flat or (lv.get("terrain") or {}).get("blocked")):
                continue
            bad = 0
            for ox, oz in unit_world(s):
                ij = ground.index(ox, oz)
                if ij is None or not ground.ok(ij[0], ij[1], False):
                    bad += 1
            if bad:
                warn(f"{p}.buildSlots", f"{s.label}: {bad} of {len(s.units)} units stand off walkable ground (water, cliff or blocked); "
                                        f"play moves them to the nearest ground, or place them with set_units")
        allp = list(slots.values())
        for i in range(len(allp)):
            for j in range(i + 1, len(allp)):
                a, b = allp[i], allp[j]
                if not (a.added or b.added):
                    continue
                d = math.hypot(a.pos[0] - b.pos[0], a.pos[2] - b.pos[2])
                if d < 4.5:
                    warn(f"{p}.buildSlots", f"{a.label} and {b.label} are {d:.1f} m apart: buildings will overlap (keep >= 5 m; 7+ for big ones)")
        if lv.get("colors") is not None and str(lv["colors"]).split("(")[0] not in COLOR_MAPS:
            err(f"{p}.colors", f"must be a main map ({', '.join(COLOR_MAPS)}) or left out for the host's colours")
        for pi_, pr in enumerate(lv.get("props") or []):
            name = pr.get("base", "")[5:].split("@")[0]
            on_map = any(s["base"] == "mesh:" + name for s in m.get("scenery", []))
            in_lib = any(x["name"] == name for x in ref.scenery.get("meshes", []))
            if not (on_map or in_lib):
                err(f"{p}.props[{pi_}].base", f"no scenery mesh {name!r} (`palace.py ref scenery`)")
            if pr.get("colors") is not None and str(pr["colors"]).split("(")[0] not in COLOR_MAPS:
                err(f"{p}.props[{pi_}].colors", f"must be a main map ({', '.join(COLOR_MAPS)}) or left out")
            for s in slots.values():
                if pr.get("solid", True) and math.hypot(pr["pos"][0] - s.pos[0], pr["pos"][2] - s.pos[2]) < prop_radius(ref, lv, pr) * 0.8 + 2.0:
                    warn(f"{p}.props[{pi_}]", f"solid prop {pr.get('id')} sits on slot {s.label}")
                    break
        # Lines and routes.
        lines = level_lines(ref, lv)
        used = used_lines(data, lv)
        host_lines = [l["name"] for l in m["spawnLines"]]
        added_ids = {a.get("id") for a in ((lv.get("spawnLines") or {}).get("add") or [])}
        for ri, name in enumerate(removed_lines(lv)):
            rp = f"{p}.spawnLines.remove[{ri}]"
            if name in added_ids:
                err(rp, f"{name!r} is an added line: delete it from spawnLines.add instead (remove_spawn_line does)")
            elif name not in host_lines:
                err(rp, f"no spawn line {name!r} on {lv['host']}; its lines: {host_lines}")
            elif used is not None and name in used:
                err(rp, f"spawn line {name!r} is removed, but wave set {lv['waveSet']!r} spawns from it: move those spawns to another line")
            elif used is None and not lv.get("waveSet"):
                h = ref.host(lv["host"])
                if h and any(s.get("line") == name for n in h["vanillaWaves"].get("nights", []) for s in n.get("spawns", [])):
                    err(rp, f"spawn line {name!r} is removed, but the map's own nights spawn from it: give the level a wave set")
        for l in lines:
            if l["added"] and (l["ground"] or l["large"]):  # flyer-only lines may sit over the sea, like the maps' own
                k = ground.index(l["center"][0], l["center"][2])
                if k is None or ground.kind[k[1]][k[0]] == "#":
                    err(f"{p}.spawnLines", f"spawn line {l['name']} is off the map")
            if l["flying"] and lv.get("base") == "flat" and lv.get("terrain"):
                # On a flat field the fliers' surface covers the field only: a flier spawned further out starts at
                # its edge (the game moves it there), not on the line. `palace.py paths` measures it in game ("air").
                T = Terrain(lv["terrain"])
                x0, z0, x1, z1 = T.ox, T.oz, T.ox + T.w * T.cell, T.oz + T.h * T.cell
                off = max(math.hypot(max(x0 - x, 0, x - x1), max(z0 - z, 0, z - z1)) for x, _, z in (l["points"] or [l["center"]]))
                if off > 30:   # the maps' own flier lines sit up to ~15 m out; that only shifts where fliers appear
                    warn(f"{p}.spawnLines", f"flier line {l['name']} reaches {off:.0f} m beyond the field: fliers can only fly over "
                                            "the field, so they start at its edge; move the line over the field")
        if castle in slots:
            for r in offline_routes(ref, data, lv, slots, castle):
                if not r["reachable"]:
                    err(f"{p}.spawnLines", f"enemies from {r['line']} can't reach the castle ({r['why']}; offline estimate, confirm with `palace.py paths`)")
                elif r.get("largeReachable") is False:
                    warn(f"{p}.spawnLines", f"large enemies from {r['line']} can't reach the castle, small ones can (offline estimate; "
                                            "large ones then attack what they can reach; confirm with `palace.py paths`)")
        # Economy sanity.
        incomes = [s for s in slots.values() if any(step_income(u) > 0 for u in s.upgrades)]
        if not incomes:
            warn(p, "no slot on the level earns gold: the player can never grow")
        ws, vanilla = level_waveset(ref, data, lv)
        if vanilla:
            warn(p + ".waveSet", "no wave set: the map's vanilla nights play (their spawn lines must exist)")
    # Names the behaviour engine takes from the map: host slots in attach keys, scenery meshes in visuals.
    known_meshes = {m.get("name") for m in (getattr(ref, "scenery", None) or {}).get("meshes", [])}
    for li, lv in enumerate(data.get("levels") or []):
        logic = lv.get("logic") or {}
        if not ref.has_map(lv.get("host")):
            continue
        host = ref.map(lv["host"])
        slot_names = {x.get("name") for x in host.get("slots") or []}
        own_meshes = {(x.get("base") or "")[5:] for x in host.get("scenery") or []}
        removed = set(((lv.get("buildSlots") or {}).get("remove")) or [])
        for key in (logic.get("attach") or {}):
            if key.startswith("slot:"):
                name = key[5:]
                if name not in slot_names:
                    err(f'levels[{li}].logic.attach["{key}"]', f"no build slot {name!r} on {lv['host']}" +
                        (f" (closest: {', '.join(sorted(slot_names, key=lambda n: _edit_distance(n.lower(), name.lower()))[:3])}; `palace.py ref slots {lv['host']}`)" if slot_names else ""))
                elif name in removed:
                    warn(f'levels[{li}].logic.attach["{key}"]', f"the slot {name!r} is removed by this level")
        if known_meshes:
            def visuals():
                for ei, e in enumerate(data.get("entities") or [] if li == 0 else []):
                    yield f"entities[{ei}]", e
                for pi, pl_ in enumerate(logic.get("places") or []):
                    yield f"levels[{li}].logic.places[{pi}]", pl_
            for path, e in visuals():
                mesh = ((e or {}).get("visual") or {}).get("mesh")
                if isinstance(mesh, str) and mesh.startswith("mesh:"):
                    name = mesh[5:].split("@")[0]
                    if name not in known_meshes and name not in own_meshes:
                        close = sorted(known_meshes | own_meshes, key=lambda n: _edit_distance(n.lower(), name.lower()))[:3]
                        warn(path + ".visual.mesh", f"no scenery mesh {name!r} (closest: {', '.join(close)}; `palace.py ref scenery`); it would be invisible")

    # The behaviour engine's rules, entities, statuses and presets (palace_logic.py, the mod's own checks).
    try:
        issues.extend(logic_issues(data))
    except Fail as e:
        warn("logic", str(e))
    return issues


# =====================================================================================================================
# Pictures (stdlib PNG)
# =====================================================================================================================

FONT = {
    "A": "0E11111F111111", "B": "1E11111E11111E", "C": "0E11101010110E", "D": "1E11111111111E", "E": "1F10101E10101F",
    "F": "1F10101E101010", "G": "0E11101711110F", "H": "1111111F111111", "I": "0E04040404040E",
    "J": "0702020202120C", "K": "11121418141211", "L": "1010101010101F", "M": "111B1515111111",
    "N": "11111915131111", "O": "0E11111111110E", "P": "1E11111E101010", "Q": "0E11111115120D",
    "R": "1E11111E141211", "S": "0F10100E01011E", "T": "1F040404040404", "U": "1111111111110E", "V": "11111111110A04",
    "W": "1111111515150A", "X": "11110A040A1111", "Y": "11110A04040404", "Z": "1F01020408101F", "0": "0E11131519110E",
    "1": "040C040404040E", "2": "0E11010204081F", "3": "1F02040201110E", "4": "02060A121F0202", "5": "1F101E0101110E",
    "6": "0608101E11110E", "7": "1F010204080808", "8": "0E11110E11110E", "9": "0E11110F01020C", " ": "00000000000000",
    "-": "0000001F000000", ".": "00000000000C0C", ":": "000C0C000C0C00", "/": "01010204081010", "(": "02040808080402",
    ")": "08040202020408", "+": "0004041F040400", "_": "0000000000001F", "#": "0A0A1F0A1F0A0A", "%": "18190204081303",
    ",": "000000000C0408", ">": "08040201020408", "<": "02040810080402", "=": "00001F001F0000", "?": "0E110102040004",
    "!": "04040404040004", "^": "040A1100000000", "'": "04040800000000", "*": "0004150E150400", "[": "0E08080808080E", "]": "0E02020202020E",
}


class Canvas:
    def __init__(self, w, h, bg=(30, 32, 36)):
        self.w, self.h = w, h
        self.px = bytearray(bytes(bg) * (w * h))

    def put(self, x, y, c, a=1.0):
        if 0 <= x < self.w and 0 <= y < self.h:
            i = (y * self.w + x) * 3
            if a >= 1:
                self.px[i:i + 3] = bytes(c)
            else:
                p = self.px
                p[i] = int(p[i] * (1 - a) + c[0] * a)
                p[i + 1] = int(p[i + 1] * (1 - a) + c[1] * a)
                p[i + 2] = int(p[i + 2] * (1 - a) + c[2] * a)

    def rect(self, x0, y0, x1, y1, c, a=1.0):
        x0, x1 = max(0, int(x0)), min(self.w, int(x1))
        y0, y1 = max(0, int(y0)), min(self.h, int(y1))
        if x1 <= x0 or y1 <= y0:
            return
        if a >= 1:
            row = bytes(c) * (x1 - x0)
            for y in range(y0, y1):
                i = (y * self.w + x0) * 3
                self.px[i:i + len(row)] = row
        else:
            for y in range(y0, y1):
                for x in range(x0, x1):
                    self.put(x, y, c, a)

    def disc(self, cx, cy, r, c, a=1.0):
        r2 = r * r
        for y in range(int(cy - r), int(cy + r) + 1):
            for x in range(int(cx - r), int(cx + r) + 1):
                if (x - cx) ** 2 + (y - cy) ** 2 <= r2:
                    self.put(x, y, c, a)

    def ring(self, cx, cy, r, c, t=2):
        for y in range(int(cy - r - t), int(cy + r + t) + 1):
            for x in range(int(cx - r - t), int(cx + r + t) + 1):
                d = math.hypot(x - cx, y - cy)
                if r - t / 2 <= d <= r + t / 2:
                    self.put(x, y, c)

    def line(self, x0, y0, x1, y1, c, t=2, a=1.0, dash=0):
        n = int(max(abs(x1 - x0), abs(y1 - y0))) + 1
        for k in range(n + 1):
            if dash and (k // dash) % 2:
                continue
            f = k / max(n, 1)
            x, y = x0 + (x1 - x0) * f, y0 + (y1 - y0) * f
            if t <= 1:
                self.put(int(x), int(y), c, a)
            else:
                self.rect(x - t / 2, y - t / 2, x + t / 2, y + t / 2, c, a)

    def arrow(self, x0, y0, x1, y1, c, t=2, head=9):
        self.line(x0, y0, x1, y1, c, t)
        ang = math.atan2(y1 - y0, x1 - x0)
        for s in (-0.45, 0.45):
            self.line(x1, y1, x1 - head * math.cos(ang + s), y1 - head * math.sin(ang + s), c, t)

    def text(self, x, y, s, c=(255, 255, 255), scale=2, bg=(0, 0, 0), bga=0.6):
        s = str(s).upper()
        w = len(s) * 6 * scale
        x = max(2, min(x, self.w - w - 3))
        y = max(2, min(y, self.h - 7 * scale - 3))
        if bg:
            self.rect(x - 2, y - 2, x + w + 1, y + 7 * scale + 2, bg, bga)
        for k, ch in enumerate(s):
            g = FONT.get(ch, FONT["?"])
            for row in range(7):
                bits = int(g[row * 2:row * 2 + 2], 16)
                for col in range(5):
                    if bits & (1 << (4 - col)):
                        self.rect(x + (k * 6 + col) * scale, y + row * scale, x + (k * 6 + col + 1) * scale, y + (row + 1) * scale, c)
        return w

    def poly(self, pts, c, a=1.0):
        """Fills a polygon (even-odd scanlines through pixel centres)."""
        if len(pts) < 3:
            return
        ya, yb = max(0, int(min(p[1] for p in pts))), min(self.h - 1, int(max(p[1] for p in pts)) + 1)
        for y in range(ya, yb + 1):
            yc = y + 0.5
            xs = []
            for i in range(len(pts)):
                (x0, y0), (x1, y1) = pts[i], pts[(i + 1) % len(pts)]
                if (y0 <= yc) != (y1 <= yc):
                    xs.append(x0 + (yc - y0) * (x1 - x0) / (y1 - y0))
            xs.sort()
            for k in range(0, len(xs) - 1, 2):
                xa, xb = max(0, int(math.ceil(xs[k] - 0.5))), min(self.w - 1, int(math.floor(xs[k + 1] - 0.5)))
                if xb < xa:
                    continue
                if a >= 1:
                    i = (y * self.w + xa) * 3
                    self.px[i:i + (xb - xa + 1) * 3] = bytes(c) * (xb - xa + 1)
                else:
                    for x in range(xa, xb + 1):
                        self.put(x, y, c, a)

    def outline(self, pts, c, t=2, a=1.0, dash=0):
        for i in range(len(pts)):
            (x0, y0), (x1, y1) = pts[i], pts[(i + 1) % len(pts)]
            self.line(x0, y0, x1, y1, c, t, a, dash)

    def save(self, path):
        raw = bytearray()
        stride = self.w * 3
        for y in range(self.h):
            raw.append(0)
            raw += self.px[y * stride:(y + 1) * stride]

        def chunk(tag, data):
            return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)

        png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", self.w, self.h, 8, 2, 0, 0, 0))
        png += chunk(b"IDAT", zlib.compress(bytes(raw), 6)) + chunk(b"IEND", b"")
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        Path(path).write_bytes(png)


def read_png(path):
    """Decodes an 8-bit RGB/RGBA non-interlaced PNG (what the game writes) into a Canvas."""
    data = Path(path).read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise Fail(f"{path} isn't a PNG")
    pos = 8
    idat = b""
    w = h = ctype = 0
    while pos < len(data):
        (n,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + n]
        pos += 12 + n
        if tag == b"IHDR":
            w, h, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype not in (2, 6) or interlace:
                raise Fail("unsupported PNG")
        elif tag == b"IDAT":
            idat += body
    bpp = 3 if ctype == 2 else 4
    raw = zlib.decompress(idat)
    stride = w * bpp
    c = Canvas(w, h)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            cc = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - cc), abs(a - cc), abs(a + b - 2 * cc)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else cc)) & 255
        prev = line
        # The PNG's first row is the top of the picture.
        for x in range(w):
            c.px[(y * w + x) * 3:(y * w + x) * 3 + 3] = line[x * bpp:x * bpp + 3]
    return c


KIND_COLORS = [
    (("castle",), (240, 196, 60)),
    (("house", "mill", "mine", "harbour", "harbor", "field", "fishing", "farm"), (236, 210, 120)),
    (("tower", "barracks", "archery", "range", "hero", "knight", "fort", "outpost", "garrison"), (225, 80, 70)),
    (("wall", "gate", "barricade", "bridge"), (170, 170, 175)),
]


def kind_color(kind, castle=False):
    if castle:
        return KIND_COLORS[0][1]
    k = (kind or "").lower()
    for words, col in KIND_COLORS[1:]:
        if any(w in k for w in words):
            return col
    return (170, 120, 230)


# Route colours, one per spawn line (none of them the blocked-route red).
ROUTE_COLORS = [(255, 140, 30), (60, 220, 255), (255, 90, 220), (160, 255, 60), (255, 240, 80), (140, 150, 255), (255, 255, 255),
                (60, 255, 170), (255, 170, 170), (200, 120, 60)]

# Placed logic objects (levels[].logic.places).
PLACE_AREA = (235, 120, 60)
PLACE_PATH = (90, 235, 235)
PLACE_BODY = (205, 165, 110)
PLACE_MARK = (120, 220, 255)


def _num(v, default=0.0):
    """A literal number field; an "=expr" or a preset's leftover "{param}" counts as the default."""
    return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else default


def _hex_color(s):
    if isinstance(s, str) and re.fullmatch(r"#?[0-9a-fA-F]{6}([0-9a-fA-F]{2})?", s):
        s = s.lstrip("#")
        return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))
    return None


def _merge_under(base, over):
    """over's fields win; nested objects merge (the place's inline component fields over its template's)."""
    out = copy.deepcopy(base)
    for k, v in over.items():
        out[k] = _merge_under(out[k], v) if isinstance(v, dict) and isinstance(out.get(k), dict) else copy.deepcopy(v)
    return out


def _turn(points, cx, cz, rot, scale=1.0):
    """Local [x, z] points turned by rotY (degrees, clockwise seen from above, as in Unity) about (cx, cz)."""
    c, s = math.cos(math.radians(rot)), math.sin(math.radians(rot))
    return [(cx + (x * c + z * s) * scale, cz + (-x * s + z * c) * scale) for x, z in points]


def _shape(spec, cx, cz, rot, scale):
    """A circle {radius}, rect {size: [w, d]} / box [w, d], or polygon {points} -> ("circle", (cx, cz, r)) |
    ("poly", [(x, z)...]) in world metres; None if it has none."""
    if not isinstance(spec, dict):
        return None
    if isinstance(spec.get("circle"), dict) or "radius" in spec:
        r = _num((spec.get("circle") or {}).get("radius", spec.get("radius")), 1.0)
        return ("circle", (cx, cz, r * scale))
    size = (spec.get("rect") or {}).get("size") if isinstance(spec.get("rect"), dict) else spec.get("box")
    if isinstance(size, list) and len(size) >= 2:
        w, d = _num(size[0], 1.0) / 2, _num(size[-1], 1.0) / 2
        return ("poly", _turn([(-w, -d), (w, -d), (w, d), (-w, d)], cx, cz, rot, scale))
    pts = (spec.get("polygon") or {}).get("points") if isinstance(spec.get("polygon"), dict) else None
    if isinstance(pts, list) and len(pts) >= 3:
        return ("poly", _turn([(_num(p[0]), _num(p[-1])) for p in pts if isinstance(p, list) and len(p) >= 2], cx, cz, rot, scale))
    return None


def logic_places(data, level):
    """The level's logic.places resolved for drawing: templates (entities[]) under the place's own fields, presets
    expanded (palace_logic; raw when that fails). Each: id, label, kind, at (x, z), active, plus what it has of
    area (shape, color, show), body (shape), path [(x, z)...], poses [shape...] (the other mover poses), sweep (a
    spinning mover's reach)."""
    places = (level.get("logic") or {}).get("places") or []
    if not places:
        return []
    try:
        probe = {k: data.get(k) for k in ("format", "version", "id", "entities", "presets", "units") if data.get(k) is not None}
        probe["levels"] = [{"id": level.get("id"), "host": level.get("host"), "logic": {"places": places}}]
        places = logic_module().expand_presets(probe, [])["levels"][0]["logic"]["places"] or places
    except Exception:   # a picture: draw what's there
        pass
    templates = {e.get("id"): e for e in data.get("entities") or [] if isinstance(e, dict)}
    units = {u.get("id") for u in data.get("units") or [] if isinstance(u, dict)}
    named = {}
    for p in places:
        if isinstance(p, dict):
            for k in ("id", "name"):
                if isinstance(p.get(k), str):
                    named.setdefault(p[k], p)

    def path_points(v, depth=0):
        if isinstance(v, str) and v.startswith("@") and depth < 4:
            q = named.get(v[1:])
            return path_points(q.get("points") if q else None, depth + 1)
        if isinstance(v, list):
            return [(_num(p[0]), _num(p[-1])) for p in v if isinstance(p, list) and len(p) >= 2]
        return []

    result = []
    for p in places:
        if not isinstance(p, dict):
            continue
        t = p.get("entity")
        q = _merge_under(templates[t], {k: v for k, v in p.items() if k != "entity"}) if isinstance(t, str) and t in templates else p
        kind = q.get("kind") or ("unit" if t in units or (isinstance(t, str) and t.startswith("vanilla:")) else
                                 "path" if "points" in q and "pos" not in q else "area" if "area" in q else "entity" if t else "logic")
        pos = q.get("pos") if isinstance(q.get("pos"), list) and len(q["pos"]) >= 2 else [0, 0, 0]
        cx, cz = _num(pos[0]), _num(pos[-1])
        rot, scale = _num(q.get("rotY")), _num(q.get("scale"), 1.0) or 1.0
        r = {"id": p.get("id"), "label": p.get("name") or p.get("id") or t or q.get("_preset") or kind, "kind": kind,
             "at": (cx, cz), "active": q.get("active") is not False, "template": t}
        area = q.get("area") if isinstance(q.get("area"), dict) else {}
        shape = _shape(area.get("shape") or (q.get("shape") if kind == "area" else None), cx, cz, rot, scale)
        if shape:
            r["area"] = {"shape": shape, "color": _hex_color(area.get("color")) or PLACE_AREA, "show": area.get("show") or "outline"}
        if kind == "path":
            r["path"] = path_points(q.get("points"))
            if r["path"]:
                r["at"] = r["path"][0]
        mover = q.get("mover") if isinstance(q.get("mover"), dict) else {}
        movement = q.get("movement") if isinstance(q.get("movement"), dict) else {}
        if kind != "path":
            mp = path_points(mover.get("path") if mover.get("path") is not None else movement.get("path"))
            if mp:
                r["path"] = mp
        body = q.get("body") if isinstance(q.get("body"), dict) else {}
        bshape = _shape(body, cx, cz, rot, scale)
        if bshape:
            r["body"] = bshape
            poses = []
            for pose in (mover.get("pose") or {}).values() if isinstance(mover.get("pose"), dict) else []:
                if not isinstance(pose, dict):
                    continue
                # As BuilderMover: rotY is absolute, pos absolute, offset from where it was placed.
                at = pose.get("pos") if isinstance(pose.get("pos"), list) and len(pose["pos"]) >= 3 else None
                off = pose.get("offset") if isinstance(pose.get("offset"), list) and len(pose["offset"]) >= 3 else [0, 0, 0]
                px, pz = (_num(at[0]), _num(at[2])) if at else (cx + _num(off[0]), cz + _num(off[2]))
                ps = _shape(body, px, pz, _num(pose.get("rotY"), rot), scale)
                if ps and ps != bshape:
                    poses.append(ps)
            r["poses"] = poses
            if _num(mover.get("spin")) or isinstance(mover.get("swing"), dict):
                pts = bshape[1] if bshape[0] == "poly" else None
                r["sweep"] = max(math.hypot(x - cx, z - cz) for x, z in pts) if pts else bshape[1][2]
        result.append(r)
    return result


def render(ref, data, level, path, background=None, world=None, routes=None, labels="names", px=1400, route_source="offline"):
    m = ref.map(level["host"])
    slots, castle, _ = effective_slots(ref, level)
    lines = level_lines(ref, level)
    places = logic_places(data or {}, level)
    pts = [(s.pos[0], s.pos[2]) for s in slots.values()] + [(l["center"][0], l["center"][2]) for l in lines]
    pts += [q["at"] for q in places] + [pt for q in places for pt in q.get("path") or []]
    if world:
        x0, x1, z0, z1 = world["minX"], world["maxX"], world["minZ"], world["maxZ"]
    else:
        x0, x1 = min(p[0] for p in pts) - 25, max(p[0] for p in pts) + 25
        z0, z1 = min(p[1] for p in pts) - 25, max(p[1] for p in pts) + 25
        t = level.get("terrain")
        if level.get("base") == "flat" and t:
            x0, z0 = min(x0, t["origin"][0]), min(z0, t["origin"][1])
            x1, z1 = max(x1, t["origin"][0] + t["size"][0] * t["cell"]), max(z1, t["origin"][1] + t["size"][1] * t["cell"])
    if background is not None:
        cv = background
        W, H = cv.w, cv.h
    else:
        W = px
        H = int(px * (z1 - z0) / (x1 - x0))
        cv = Canvas(W, H)
    sx, sz = W / (x1 - x0), H / (z1 - z0)

    def P(x, z):
        return (x - x0) * sx, H - (z - z0) * sz

    if background is None:
        ground = LevelGround(ref, level, {})
        hs = [h for h in ground.height if h is not None]
        lo, hi = (min(hs), max(hs)) if hs else (0, 1)
        for j in range(ground.h):
            for i in range(ground.w):
                k = ground.kind[j][i]
                h = ground.height[j * ground.w + i]
                if k == "#" or h is None:
                    continue
                shade = 0.55 + 0.45 * ((h - lo) / (hi - lo + 1e-6))
                base = {"w": (96, 140, 80), "s": (120, 150, 70), ".": (110, 100, 90)}[k]
                col = tuple(int(c * shade) for c in base)
                xa, ya = P(ground.ox + i * ground.cell, ground.oz + (j + 1) * ground.cell)
                xb, yb = P(ground.ox + (i + 1) * ground.cell, ground.oz + j * ground.cell)
                cv.rect(xa, ya, xb + 1, yb + 1, col)
        t = level.get("terrain")
        if t and (t.get("water") or {}).get("cells"):
            T = Terrain(t)
            for k_, on in enumerate(T.water):
                if on:
                    i, j = k_ % T.w, k_ // T.w
                    xa, ya = P(T.ox + i * T.cell, T.oz + (j + 1) * T.cell)
                    xb, yb = P(T.ox + (i + 1) * T.cell, T.oz + j * T.cell)
                    cv.rect(xa, ya, xb + 1, yb + 1, (60, 110, 170))
        for p in level.get("props") or []:
            x, y = P(p["pos"][0], p["pos"][2])
            cv.disc(x, y, max(2, prop_radius(ref, level, p) * sx), (40, 80, 45) if p.get("solid", True) else (80, 120, 80), 0.85)
    # Grid ticks every 20 m.
    step = 20
    for gx in range(int(math.ceil(x0 / step)) * step, int(x1), step):
        x, _ = P(gx, z0)
        cv.line(x, 0, x, H, (255, 255, 255), 1, a=0.12)
        if gx % 40 == 0:
            cv.text(x + 2, H - 14, str(gx), (220, 220, 220), 1, bga=0.4)
    for gz in range(int(math.ceil(z0 / step)) * step, int(z1), step):
        _, y = P(x0, gz)
        cv.line(0, y, W, y, (255, 255, 255), 1, a=0.12)
        if gz % 40 == 0:
            cv.text(2, y + 2, str(gz), (220, 220, 220), 1, bga=0.4)
    # Routes: one colour per line, named where they start and in the legend (blocked ones red).
    route_keys = []
    for ri, r in enumerate(routes or []):
        col = ROUTE_COLORS[ri % len(ROUTE_COLORS)] if r.get("reachable") else (255, 0, 0)
        p = [P(a, b) for a, b in r.get("points") or []]
        for i in range(len(p) - 1):
            cv.line(p[i][0], p[i][1], p[i + 1][0], p[i + 1][1], col, 3, a=0.9, dash=8)
        if len(p) > 1 and r.get("line"):
            # Named a little way along its first leg, clear of the spawn line's own label.
            lx, ly = p[0][0] + (p[1][0] - p[0][0]) * 0.3, p[0][1] + (p[1][1] - p[0][1]) * 0.3
            cv.text(lx + 6, ly + 4, r["line"], col, 1, bga=0.7)
        length = r.get("length")
        route_keys.append((f"{r.get('line', '?')}" + (f" {length:.0f} m" if r.get("reachable") and length else " BLOCKED" if not r.get("reachable") else "")
                           + (" OFF GRAPH" if r.get("warning") else ""), col))
    # Spawn lines.
    for l in lines:
        p = [P(q[0], q[2]) for q in l["points"]]
        col = (200, 40, 60) if (l["ground"] or l["large"]) else (150, 60, 200)
        for i in range(len(p) - 1):
            cv.line(p[i][0], p[i][1], p[i + 1][0], p[i + 1][1], col, 5)
        if len(p) == 1:
            cv.disc(p[0][0], p[0][1], 5, col)
        cx, cy = P(l["center"][0], l["center"][2])
        tag = "" if l["ground"] and l["large"] and l["flying"] else " (" + "".join(c for c, f in (("G", l["ground"]), ("L", l["large"]), ("F", l["flying"])) if f) + ")"
        cv.text(cx + 8, cy - 8, l["name"] + tag, (255, 200, 200), 2)
    # Placed logic objects: areas, paths, bodies (with a mover's path, other poses and sweep), markers. Labelled after
    # the slots, which keep their places.
    def shape_px(shape):
        if shape[0] == "circle":
            x, y = P(shape[1][0], shape[1][1])
            return "circle", (x, y, max(2.0, shape[1][2] * sx))
        return "poly", [P(a, b) for a, b in shape[1]]

    def draw_shape(shape, col, fill=None, t=2, dash=0, a=1.0):
        k, g = shape_px(shape)
        if k == "circle":
            if fill:
                cv.disc(g[0], g[1], g[2], col, fill)
            if dash:
                n = max(12, int(g[2] * 2 * math.pi / (dash * 2)))
                for i in range(0, n, 2):
                    a0, a1 = 2 * math.pi * i / n, 2 * math.pi * (i + 1) / n
                    cv.line(g[0] + g[2] * math.cos(a0), g[1] + g[2] * math.sin(a0), g[0] + g[2] * math.cos(a1), g[1] + g[2] * math.sin(a1), col, t, a)
            else:
                cv.ring(g[0], g[1], g[2], col, t)
        else:
            if fill:
                cv.poly(g, col, fill)
            cv.outline(g, col, t, a, dash)

    place_labels = []
    for q in places:
        dim = 1.0 if q["active"] else 0.5
        ax, ay = P(*q["at"])
        rad = 6
        if q.get("area"):
            ar = q["area"]
            draw_shape(ar["shape"], ar["color"], fill=(0.4 if ar["show"] == "fill" else 0.15) * dim, t=2, dash=0 if ar["show"] != "none" else 6)
            k, g = shape_px(ar["shape"])
            rad = g[2] if k == "circle" else max(math.hypot(x - ax, y - ay) for x, y in g)
        ppts = [P(a, b) for a, b in q.get("path") or []]
        for i in range(len(ppts) - 1):
            (x0_, y0_), (x1_, y1_) = ppts[i], ppts[i + 1]
            cv.line(x0_, y0_, x1_, y1_, PLACE_PATH, 3, a=0.9 * dim)
            d = math.hypot(x1_ - x0_, y1_ - y0_)
            if d > 14:
                # Direction ticks: a chevron every ~40 px pointing along the path.
                ux, uy = (x1_ - x0_) / d, (y1_ - y0_) / d
                for k_ in range(max(1, int(d // 40))):
                    f = (k_ + 0.5) * d / max(1, int(d // 40))
                    mx, my = x0_ + ux * f + ux * 4, y0_ + uy * f + uy * 4
                    for sgn in (-1, 1):
                        cv.line(mx, my, mx - ux * 8 + sgn * uy * 6, my - uy * 8 - sgn * ux * 6, PLACE_PATH, 2)
        for x_, y_ in ppts:
            cv.disc(x_, y_, 3, PLACE_PATH)
        if q.get("body"):
            for ps in q.get("poses") or []:
                draw_shape(ps, PLACE_BODY, t=2, dash=4)
            if q.get("sweep"):
                draw_shape(("circle", (q["at"][0], q["at"][1], q["sweep"])), PLACE_BODY, t=2, dash=4)
            draw_shape(q["body"], (40, 30, 20), fill=0.85 * dim, t=2)
            draw_shape(q["body"], PLACE_BODY, fill=0.85 * dim, t=1)
            k, g = shape_px(q["body"])
            rad = max(rad if q.get("area") else 0, g[2] if k == "circle" else max(math.hypot(x - ax, y - ay) for x, y in g))
        elif q["kind"] != "path" and not q.get("area"):
            # A point, logic object, unit or unresolved template: a small diamond.
            dia = [(ax, ay - 6), (ax + 6, ay), (ax, ay + 6), (ax - 6, ay)]
            cv.poly(dia, (0, 0, 0))
            cv.poly([(ax, ay - 4), (ax + 4, ay), (ax, ay + 4), (ax - 4, ay)], PLACE_MARK if q["kind"] != "point" else (255, 255, 255), dim)
        lab = PLACE_PATH if q["kind"] == "path" else (q["area"]["color"] if q.get("area") and not q.get("body") else PLACE_MARK)
        place_labels.append((ax, ay, min(rad, 40) if q["kind"] != "path" else 4, str(q["label"]) + ("" if q["active"] else " (off)"), lab))
    # Requirement arrows.
    for s in slots.values():
        if s.req and s.req[0] in slots and not s.follows:
            a = slots[s.req[0]]
            ax, ay = P(a.pos[0], a.pos[2])
            bx, by = P(s.pos[0], s.pos[2])
            d = math.hypot(bx - ax, by - ay) or 1
            col = (240, 196, 60) if a.castle else (80, 200, 180)
            cv.arrow(ax + (bx - ax) * 9 / d, ay + (by - ay) * 9 / d, bx - (bx - ax) * 10 / d, by - (by - ay) * 10 / d, col, 2)
    # Slots (labels avoid each other where they can, and the legend).
    legend = [("castle", KIND_COLORS[0][1]), ("economy", KIND_COLORS[1][1]), ("military", KIND_COLORS[2][1]), ("walls", KIND_COLORS[3][1]),
              ("special", (170, 120, 230)), ("spawn line", (200, 40, 60)), ("needs castle", (240, 196, 60)),
              ("needs slot", (80, 200, 180))]
    if places:
        legend += [(n, c) for n, c, on in (("logic area", PLACE_AREA, any(q.get("area") for q in places)),
                                           ("logic path", PLACE_PATH, any(q.get("path") for q in places)),
                                           ("logic body", PLACE_BODY, any(q.get("body") for q in places)),
                                           ("logic object", PLACE_MARK, any(q["kind"] != "path" and not q.get("body") and not q.get("area") for q in places)))
                   if on]
    taken = [(W - 175, 0, W, 10 + 20 * len(legend)), (0, 0, 520, 30)]
    for q in slots.values():
        qx, qy = P(q.pos[0], q.pos[2])
        taken.append((qx - 7, qy - 7, qx + 7, qy + 7))

    def label_at(x, y, r, text, col, sc):
        # Beside the thing, in the first spot clear of other labels, slots and the legend.
        tw, th = len(text) * 6 * sc, 7 * sc
        for lx, ly in ((x + r + 3, y - th / 2), (x + r + 3, y + r), (x + r + 3, y - r - th), (x - r - 3 - tw, y - th / 2), (x - tw / 2, y + r + 2),
                       (x - tw / 2, y - r - th - 4)):
            box = (lx - 2, ly - 2, lx + tw + 2, ly + th + 2)
            if not any(box[0] < b[2] and b[0] < box[2] and box[1] < b[3] and b[1] < box[3] for b in taken):
                break
        taken.append(box)
        cv.text(lx, ly, text, col, sc)

    for s in sorted(slots.values(), key=lambda q: q.castle):
        x, y = P(s.pos[0], s.pos[2])
        col = kind_color(s.kind, s.castle)
        r = 9 if s.castle else 6
        if s.follows:
            cv.disc(x, y, 3, col)
            continue
        cv.disc(x, y, r, col)
        cv.ring(x, y, r + 1, (0, 0, 0), 2)
        if labels == "none":
            continue
        text = s.label if labels != "costs" else f"{s.label} " + "/".join(str(u["cost"]) for u in s.upgrades if not u.get("disabled"))
        if labels == "reqs" and s.req:
            text = f"{s.label} <- {slots[s.req[0]].label if s.req[0] in slots else s.req[0]}" + (f" L{s.req[1] + 1}" if s.req[1] else "")
        label_at(x, y, r, text, (255, 255, 255), 1 if len(slots) > 60 else 2)
    if labels != "none":
        for x, y, r, text, col in place_labels:
            label_at(x, y, r, text, col, 1 if len(slots) + len(places) > 60 else 2)
    # Legend.
    yy = 8
    for name, col in legend:
        cv.rect(W - 170, yy, W - 156, yy + 14, col)
        cv.text(W - 150, yy, name, (255, 255, 255), 2, bga=0.5)
        yy += 20
    if route_keys:
        # Bottom right: each route's colour, line and length.
        tw = max(len(t) for t, _ in route_keys) * 6 + 30
        yy = H - 18 * len(route_keys) - 30
        cv.text(W - tw, yy, "routes" + (" (offline estimate)" if route_source == "offline" else " (game)"), (255, 255, 255), 1, bga=0.6)
        yy += 16
        for t, col in route_keys:
            cv.rect(W - tw, yy + 2, W - tw + 14, yy + 9, col)
            cv.text(W - tw + 20, yy, t, (255, 255, 255), 1, bga=0.6)
            yy += 18
    if routes and route_source == "offline":
        cv.text(8, H - 28, "routes are an offline estimate: the game may pick another gap. check with: palace.py paths / render --live",
                (255, 230, 160), 1, bga=0.7)
    cv.text(8, 8, f"{level.get('id')} on {level['host']}  x ->  z ^  (m)", (255, 255, 255), 2)
    cv.save(path)
    return {"written": str(Path(path).resolve()), "pixels": [W, H], "world": {"minX": round(x0, 1), "maxX": round(x1, 1), "minZ": round(z0, 1), "maxZ": round(z1, 1)}}


# =====================================================================================================================
# Commands
# =====================================================================================================================

def cmd_where(env, args):
    g = Game(env)
    s = None
    try:
        s = g.status_file()
    except Fail:
        pass
    if s is not None and s.get("age", 0) > 15:
        s["state"] = "game not running" if s.get("running") is False else "not answering (loading or frozen?)"
    result = {"palace.py": VERSION, "kit": str(env.kit), "bepinex": str(env.bepinex) if env.bepinex else None,
              "chosenBecause": env.source, "packs": str(env.packs) if env.bepinex else None, "reference": str(env.ref),
              "referenceMaps": Ref(env).maps(), "bridge": s or "off (Settings > Mods > Builder > Agent bridge)"}
    if env.error:
        result["error"] = env.error
    others = [{"bepinex": str(b), "via": why} for b, why in env.found[1:]]
    if others:
        result["otherInstalls"] = others
    warnings = []
    if others and env.source not in ("--game", "PALACE_GAME"):
        warnings.append(f"{len(env.found)} game installs found; using {env.bepinex} ({env.source}). Pass --game or set "
                        "PALACE_GAME to pick one explicitly.")
    if env.source == "the default Steam install":
        warnings.append("Using the default Steam install because nothing else was given. If that's the player's real game, "
                        "make sure that's the one you mean to edit.")
    if warnings:
        result["warnings"] = warnings
    out(result)


def cmd_game(env, args):
    g = Game(env)
    if args.what == "status":
        out(g.status())
    elif args.what == "cmd":
        print(g.raw(" ".join(args.rest), timeout=args.timeout))
    elif args.what == "log":
        out(g.log_since(max(0, g.log_size() - args.bytes), features=args.feature or None, min_level=args.level))
    elif args.what == "slots":
        out(g.agent("slots"))
    elif args.what == "menu":
        out(g.agent("menu"))
    else:
        raise Fail("game status | cmd <text> | log | slots | menu")


def cmd_ref(env, args):
    ref = Ref(env)
    if args.what == "build":
        g = Game(env)
        env.ref.mkdir(parents=True, exist_ok=True)
        (env.ref / "maps").mkdir(exist_ok=True)
        r = g.agent(f"catalog {env.ref / 'catalog.json'}")
        maps = MAIN_MAPS if not args.maps else args.maps.split(",")
        done = []
        for scene in maps:
            if ref.has_map(scene) and not args.force:
                done.append({"map": scene, "cached": True})
                continue
            g.agent(f"vanilla {scene}")
            g.wait_level_day(timeout=120)
            time.sleep(1.5)
            info = g.agent(f"host {env.ref / 'maps' / (scene + '.json')}", timeout=120)
            done.append({"map": scene, "slots": info.get("slots")})
        g.agent("menu")
        out({"catalog": r, "maps": done, "folder": str(env.ref)})
        return
    if args.what == "maps":
        rows = []
        for scene in ref.maps():
            m = ref.map(scene)
            kinds = {}
            for s in m["slots"]:
                if not s.get("followsActivator"):
                    kinds[s["kind"]] = kinds.get(s["kind"], 0) + 1
            g = m.get("grid") or {}
            ext = None
            if g:
                # The walkable area (the dump samples 12 m beyond it), as terrain_init's default grid covers it.
                ext = {"minX": g["origin"][0] + 12, "minZ": g["origin"][1] + 12,
                       "maxX": round(g["origin"][0] + g["size"][0] * g["cell"] - 12), "maxZ": round(g["origin"][1] + g["size"][1] * g["cell"] - 12)}
            rows.append({"map": scene, "castle": m["castle"]["pos"] if m.get("castle") else None, "extent": ext, "nights": m.get("nights"),
                         "startingGold": m.get("startingGold"), "slots": kinds,
                         "spawnLines": [f"{l['name']} ({'G' if l['smallGround'] else ''}{'L' if l['bigGround'] else ''}{'F' if l['flying'] else ''})" for l in m["spawnLines"]]})
        out(rows)
    elif args.what == "slots":
        m = ref.map(args.name)
        rows = []
        castle_name = (m.get("castle") or {}).get("name")
        dupes = {}
        for s in m["slots"]:
            dupes[s["name"]] = dupes.get(s["name"], 0) + 1
        for s in m["slots"]:
            if args.kind and s["kind"].lower() != args.kind.lower():
                continue
            act = "castle" if s.get("activator") == castle_name else s.get("activator")
            rows.append({"name": s["name"], "kind": s["kind"], "at": [s["pos"][0], s["pos"][2]], "y": s["pos"][1],
                         "needs": f"{act}:{s['activatorLevel'] + 1}" if s.get("activator") and s.get("startDeactivated") else "start",
                         **({"duplicateName": f"{dupes[s['name']]} slots share this name, so ops and plans can't tell them apart: avoid naming it"}
                            if dupes[s["name"]] > 1 else {}),
                         "costs": [u["cost"] for u in s["upgrades"] if not u.get("disabled")],
                         "income": [step_income(u) for u in s["upgrades"] if not u.get("disabled")],
                         **({"incomeEachDawn": modifier_text(income_modifier(s))} if income_modifier(s) else {}),
                         **({"units": s["units"]} if s.get("units") else {}),
                         **({"castle": True} if s.get("castle") else {}), **({"followsActivator": True} if s.get("followsActivator") else {}),
                         **({} if s.get("canBeBase") else {"copyable": False})})
        out(rows)
    elif args.what == "buildings":
        kinds = {}
        for scene in ref.maps():
            for s in ref.map(scene)["slots"]:
                key = (s["kind"], tuple(u["cost"] for u in s["upgrades"]))
                e = kinds.setdefault(key, {"kind": s["kind"], "maps": set(), "count": 0, "hp": s.get("hp"), "levels": [
                    {"cost": u["cost"], **({"hidden": True} if u.get("disabled") else {}),
                     "branches": [{k: v for k, v in b.items() if v not in (None, "")} for b in u["branches"]]} for u in s["upgrades"]]})
                e["maps"].add(scene)
                e["count"] += 1
        rows = sorted(kinds.values(), key=lambda e: (e["kind"], -e["count"]))
        for r in rows:
            r["maps"] = sorted(r["maps"])
        if args.kind:
            rows = [r for r in rows if r["kind"].lower() == args.kind.lower()]
        out(rows)
    elif args.what == "enemies":
        rows = []
        for u in ref.catalog["units"]:
            if u["side"] != ("player" if args.player else "enemy"):
                continue
            w = next((x for x in ref.catalog["weapons"] if x["id"] == u.get("weapon")), None)
            dmg = None
            if w and w["damage"]:
                dmg = [{"tags": d.get("tags"), "add": d.get("add"), "mult": d.get("mult")} for d in w["damage"]]
            rows.append({"ref": u["ref"], "name": u["name"], "hp": u["maxHp"], "speed": u["moveSpeed"], "range": u["attackRange"], "cooldown": u["attackCooldown"],
                         "flying": u["flying"], "large": u["large"], "difficulty": u.get("difficulty"), "damage": dmg,
                         "tags": [t for t in u["tags"] if t not in ("EnemyOwned", "PlayerOwned", "AUTO_Alive")], "description": u.get("description")})
        rows.sort(key=lambda r: (r["flying"], r["hp"]))
        out(rows)
    elif args.what == "waves":
        h = ref.host(args.name)
        if not h:
            raise Fail(f"no map {args.name!r}")
        out({"map": args.name, "startingGold": h["startingGold"], "nights": wave_summary(ref, {}, h["vanillaWaves"]),
             "raw": h["vanillaWaves"]["nights"] if args.raw else "(--raw for the spawn lists)"})
    elif args.what == "scenery":
        rows = {}
        for s in ref.scenery.get("meshes", []):
            if args.kind and s.get("category", "").lower() != args.kind.lower():
                continue
            rows.setdefault(s.get("category", "?"), []).append({"mesh": s["name"], "size": s.get("size"), "maps": sorted(s.get("maps", {}))})
        if args.name:
            m = ref.map(args.name)
            rows[f"on {args.name} (map's own)"] = [{"mesh": x["base"][5:], "count": x["count"], "size": x.get("size")} for x in m.get("scenery", [])]
        out(rows)
    elif args.what == "doc":
        path = Path(args.out or env.ref / "reference.md")
        path.write_text(reference_markdown(ref), encoding="utf-8")
        out({"written": str(path.resolve())})
    elif args.what == "render":
        level = {"id": args.name, "host": args.name}
        slots, castle, _ = effective_slots(ref, level)
        routes = offline_routes(ref, {}, level, slots, castle) if castle in slots else []
        out(render(ref, {}, level, args.out or f"{args.name}.png", routes=routes, labels=args.labels))
    else:
        raise Fail("ref build | maps | slots <map> | buildings | enemies | waves <map> | scenery [map] | render <map>")


def reference_markdown(ref):
    """reference.md: the tables of the guide's Reference section, from the cached game data."""
    L = []
    cat = ref.catalog
    L.append(f"# Thronefall reference for level building\n\nGenerated by `palace.py ref doc` from the game's own data "
             f"(game {cat.get('gameVersion')}). Regenerate after a game update. Positions are world metres (x east, z north).\n")
    maps = [m for m in MAIN_MAPS if ref.has_map(m)]
    L.append("## Maps\n")
    L.append("| Map | Castle (x, y, z) | Walkable extent x / z | Nights | Start gold | Slots by kind | Spawn lines (G small ground, L large, F flying) |")
    L.append("|---|---|---|---|---|---|---|")
    for name in maps:
        m = ref.map(name)
        g = m["grid"]
        ext = f"{g['origin'][0] + 12:.0f}..{g['origin'][0] + g['size'][0] * g['cell'] - 12:.0f} / {g['origin'][1] + 12:.0f}..{g['origin'][1] + g['size'][1] * g['cell'] - 12:.0f}"
        kinds = {}
        for s in m["slots"]:
            if not s.get("followsActivator") and not s.get("castle"):
                kinds[s["kind"]] = kinds.get(s["kind"], 0) + 1
        lines = ", ".join(f"{l['name']} ({'G' if l['smallGround'] else ''}{'L' if l['bigGround'] else ''}{'F' if l['flying'] else ''}) "
                          f"@{l['center'][0]:.0f},{l['center'][2]:.0f}" for l in m["spawnLines"])
        c = m["castle"]["pos"]
        L.append(f"| {name} | {c[0]:.0f}, {c[1]:.1f}, {c[2]:.0f} | {ext} | {m['nights']} | {m['startingGold']} | "
                 f"{', '.join(f'{k} {v}' for k, v in sorted(kinds.items()))} | {lines} |")
    L.append("\nThe castle slot of each map is named in `palace.py ref slots <map>` (`\"castle\": true`); in ops and plans just say `castle`.\n")

    L.append("### What the host decides\n")
    L.append("Even on a `cleared` or `flat` base the host map fixes: **which of its slots `add_slot` copies** (a kind the host "
             "has is a copy of its most complete slot of that kind; a kind it lacks, like a Mill on Frostsee, is the game's "
             "standard building of that kind, `building:<kind>`, the same tree as the table below), the castle's "
             "position, the playable rectangle (`terrain_init`'s default grid is the host's walkable extent), the sky, light and "
             "ground palette, and the starting gold default. Terrain heights go from world y "
             f"{Terrain.MIN_Y:g} m (the king respawns below -10) up to {Terrain.MAX_ABOVE:g} m above the terrain's `y` (the castle's "
             f"height; at least world {Terrain.MAX_Y:g} m), so every host has room to build up (headroom below). Pick "
             "the host from this table before designing.\n")
    all_kinds = sorted({s["kind"] for name in maps for s in ref.map(name)["slots"]
                        if not s.get("followsActivator") and not s.get("castle")})
    L.append("| Kind | " + " | ".join(maps) + " |")
    L.append("|---|" + "---|" * len(maps))
    for kind in all_kinds:
        row = []
        for name in maps:
            row.append("yes" if any(s["kind"] == kind and not s.get("followsActivator") and not s.get("castle")
                                    for s in ref.map(name)["slots"]) else "-")
        L.append(f"| {kind} | " + " | ".join(row) + " |")
    heads = []
    for name in maps:
        m = ref.map(name)
        g = m["grid"]
        c = m["castle"]["pos"]
        x0, x1 = g["origin"][0] + 12, g["origin"][0] + g["size"][0] * g["cell"] - 12
        z0, z1 = g["origin"][1] + 12, g["origin"][1] + g["size"][1] * g["cell"] - 12
        large_r, slope = host_graphs(ref, name)
        heads.append(f"| {name} | {x1 - x0:.0f} x {z1 - z0:.0f} | {c[0] - x0:.0f} / {x1 - c[0]:.0f} / {c[2] - z0:.0f} / {z1 - c[2]:.0f} | "
                     f"{c[1]:.1f} | {Terrain.max_world(c[1]) - c[1]:+.1f} / {Terrain.MIN_Y - c[1]:+.1f} | {large_r:g} | {slope:g} |")
    L.append("\n| Host | Playable rectangle (m) | Castle to west / east / south / north edge (m) | Castle y | Terrain headroom up / down from the castle (m) | Large-enemy clearance (m) | Walkable slope (°) |")
    L.append("|---|---|---|---|---|---|---|")
    L += heads
    L.append("")

    L.append("## Buildings (slot kinds)\n")
    L.append("Per level: cost in gold, then each choice (branch) with its income change (+g per dawn), extra HP and what it adds. "
             "Hidden tiers exist in the data but are switched off in normal play (PalaceMod's optional \"Level 4 buildings\" turns them on). "
             "`add_slot` copies the most complete slot of the kind on the host map, or places the game's standard building of a kind "
             "the host lacks (`building:<kind>`: " + ", ".join(f"`{b['name'][9:]}`" for b in standard_buildings(ref, maps)) + "), "
             "so these trees are what you get.\n")
    seen = {}
    for name in maps:
        for s in ref.map(name)["slots"]:
            if s.get("followsActivator"):
                continue
            key = s["kind"]
            prev = seen.get(key)
            if prev is None or len(s["upgrades"]) > len(prev[1]["upgrades"]):
                seen[key] = (name, s, set())
            seen[key][2].add(name)
    for kind in sorted(seen):
        name, s, maps_with = seen[kind]
        hp = s.get("hp")
        L.append(f"### {kind}\n\nOn: {', '.join(sorted(maps_with))}. Base HP {hp if hp is not None else '-'}.\n")
        mod = income_modifier(s)
        if mod:
            L.append(modifier_text(mod) + "\n")
        for i, u in enumerate(s["upgrades"]):
            parts = []
            for b in u["branches"]:
                bits = []
                if b.get("income"):
                    bits.append(f"+{b['income']}g")
                if b.get("hp"):
                    bits.append(f"+{b['hp']} HP")
                if b.get("troops"):
                    bits.append("troops " + "/".join(t[2:] if t.startswith("P ") else t for t in b["troops"]))
                if b.get("attackers"):
                    bits.append(f"shoots ({b['range']:.0f} m)")
                label = b.get("name") or ""
                parts.append((label + (" " if label and bits else "") + (", ".join(bits))) or "-")
            L.append(f"- L{i + 1}{' (hidden)' if u.get('disabled') else ''}: {u['cost']}g: " + " | ".join(parts))
        L.append("")
    L.append("Notes: a Mill's Fields, Gates' wall pieces and similar are separate slots that *follow* their owner "
             "(`followsActivator`): bought with it, never placed on their own. Gold Mines lose income each dawn (down to a floor); "
             "Harbours gain income each dawn (up to a cap). Every income building pays at dawn unless knocked out that night.\n")

    L.append("## Enemies\n")
    L.append("HP is before the night's difficulty (x difficulty; elite x4 HP, x3 damage). Speed m/s. Diff = the game's own "
             "threat rating (Eternal Trials). Use the ref in wave spawns: `\"unit\": \"vanilla:E Melee\"`.\n")
    L.append("| Ref | Name | HP | Speed | Range | Flying | Large | Diff | Notes |")
    L.append("|---|---|---|---|---|---|---|---|---|")
    for u in sorted((u for u in cat["units"] if u["side"] == "enemy"), key=lambda u: (u["flying"], u["maxHp"])):
        tags = [t for t in u["tags"] if t not in ("EnemyOwned", "AUTO_Alive", "MeeleFighter", "RangedFighter", "Humanoid", "NaturallyVulnerableToSplash")]
        desc = (u.get("description") or "").replace("|", "/").replace("\n", " ")
        L.append(f"| `{u['ref']}` | {u['name']} | {u['maxHp']:g} | {u['moveSpeed']:g} | {u['attackRange']:g} | {'yes' if u['flying'] else ''} | "
                 f"{'yes' if u['large'] else ''} | {u.get('difficulty') or 0:g} | {', '.join(tags)}{'. ' if tags and desc else ''}{desc[:140]} |")
    L.append("\nPlayer troops (custom units' `vanilla:` bases with side player): " +
             ", ".join(f"`{u['id']}`" for u in cat["units"] if u["side"] == "player") + "\n")

    L.append("## Vanilla nights (reference curves)\n")
    L.append("Per night: enemies, effective HP (sum of HP x difficulty, elite x4), gold dropped, flyers. Use these to pace your own.\n")
    for name in maps:
        h = ref.host(name)
        if not h:
            continue
        rows = wave_summary(ref, {}, h["vanillaWaves"])
        L.append(f"**{name}** (start gold {h['startingGold']}): " + "; ".join(
            f"N{r['night']} {r['enemies']}e/{r['effectiveHp'] / 1000:.1f}k hp/{r['gold']}g{'/' + str(r['flying']) + 'fly' if r['flying'] else ''}" for r in rows))
        L.append("")
    L.append("## Vanilla economy (greedy all-economy player, gold income per dawn by day)\n")
    for name in maps:
        h = ref.host(name)
        lv = {"id": name, "host": name}
        sl, c, _ = effective_slots(ref, lv)
        wg = [r["gold"] for r in wave_summary(ref, {}, h["vanillaWaves"])]
        sim = economy_sim(sl, c, h["startingGold"], wg, len(h["vanillaWaves"]["nights"]))
        L.append(f"**{name}**: " + ", ".join(f"d{r['day']} {r['incomePerDawn']}" for r in sim))
        L.append("")
    sc = ref.scenery.get("meshes", [])
    if sc:
        L.append("## Scenery library (prop meshes usable on any map)\n")
        by = {}
        for m in sc:
            by.setdefault(m.get("category", "Other"), []).append(f"{m['name']} ({max(m['size'][:2]):.0f} m)" if m.get("size") else m["name"])
        for k in sorted(by):
            L.append(f"- **{k}**: " + ", ".join(sorted(by[k])))
        L.append("")
    return "\n".join(L) + "\n"


def cmd_new(env, args):
    if args.host:
        # The library model: a new level (its nights in the same file); enemies are shared by every level.
        args.level, args.pack = args.pack, LIBRARY_ID
        if not args.force and (env.library / "levels" / (args.level + ".json")).exists():
            raise Fail(f"library level {args.level!r} exists (--force replaces it)")
        return cmd_level(env, args)
    path = pack_path(env, args.pack)
    if isinstance(path, LibraryRef):
        raise Fail("the library always exists; add a level with `new <level> --host <map>`")
    if path.exists() and not args.force:
        raise Fail(f"{path} exists (--force to overwrite)")
    data = {"format": "palace-builder", "version": PACK_VERSION, "id": args.pack, "name": args.name or args.pack, "author": args.author or "",
            "description": args.description or "", "assets": {}, "units": [], "waveSets": [], "levels": []}
    save_pack(path, data)
    out({"created": str(path)})


def cmd_level(env, args):
    ref = Ref(env)
    path, data = load_pack(env, args.pack)
    levels = data.setdefault("levels", [])
    old = next((l for l in levels if l.get("id") == args.level), None)
    if old is not None:
        if not args.force:
            raise Fail(f"level {args.level!r} exists (--force replaces it, and its wave set if no other level uses it)")
        levels.remove(old)
        ws = old.get("waveSet")
        if ws and not any(l.get("waveSet") == ws for l in levels):
            data["waveSets"] = [w for w in data.get("waveSets") or [] if w.get("id") != ws]
    if not ID_RE.match(args.level):
        raise Fail("level id: a-z 0-9 . _ -")
    ref.map(args.host)
    lv = {"id": args.level, "name": args.name or args.level, "description": "", "host": args.host}
    if args.base != "map":
        lv["base"] = args.base
    levels.append(lv)
    ops = Ops(env, ref, data, args.level)
    if args.waves == "vanilla":
        ops.op_copy_waves({"op": "copy_waves", "from": args.host})
    elif args.waves == "empty":
        ops.op_waves({"op": "waves", "startingGold": ref.map(args.host).get("startingGold", 10), "nights": []})
    if args.base == "flat":
        ops.op_terrain_init({"op": "terrain_init"})
    save_pack(path, data)
    out({"level": lv["id"], "pack": str(path), "waveSet": lv.get("waveSet")})


def cmd_list(env, args):
    ref = LibraryRef(env.library)
    data = load_library(ref)
    used = {}
    for lv in data["levels"]:
        ws = find_waveset(data, lv.get("waveSet")) or {}
        for n in ws.get("nights") or []:
            for sp in n.get("spawns") or []:
                u = (sp.get("unit") or "").replace("unit:", "")
                used.setdefault(u, set()).add(lv["id"])
    out({"library": str(env.library),
         "levels": [{"id": l["id"], "name": l.get("name"), "host": l.get("host"), "base": l.get("base"),
                     "nights": len((find_waveset(data, l.get("waveSet")) or {}).get("nights") or []),
                     "origin": (ref.meta.get(("levels", l["id"])) or {}).get("origin"),
                     "preview": (env.library / "levels" / (l["id"] + ".png")).exists()} for l in data["levels"]],
         "enemies": [{"id": u["id"], "name": u.get("name"), "side": u.get("side", "enemy"), "base": u.get("base"),
                      "usedBy": sorted(used.get(u["id"], []))} for u in data["units"]]})


def cmd_export(env, args):
    g = Game(env)
    name = re.sub(r"[^A-Za-z0-9._-]+", "-", args.name).strip("-") or "levels"
    out(g.agent(f"library export {name} {args.levels}" + (f" {args.enemies}" if args.enemies else "")))


def read_ops(src):
    text = sys.stdin.read() if src == "-" else Path(src).read_text(encoding="utf-8")
    ops = json.loads(text)
    if isinstance(ops, dict):
        ops = ops.get("ops") or [ops]
    return ops


def fresh_level(env, ref, data, level_id):
    """Empties a level back to what `level` made (same id, name, host, base and wave-set link; a flat base gets a new
    default grid). Slots, lines, props and terrain go; the wave set and its nights are kept."""
    lv = find_level(data, level_id)
    keep = {k: lv[k] for k in ("id", "name", "description", "host", "base", "waveSet") if k in lv}
    lv.clear()
    lv.update(keep)
    if lv.get("base") == "flat":
        Ops(env, ref, data, lv["id"]).op_terrain_init({"op": "terrain_init"})
    return lv["id"]


def scope_levels(data, level_ids):
    """A view of a pack with only these levels and their wave sets (all units and assets)."""
    levels = [l for l in data.get("levels") or [] if l.get("id") in level_ids]
    sets = {l.get("waveSet") for l in levels}
    return dict(data, levels=levels, waveSets=[w for w in data.get("waveSets") or [] if w.get("id") in sets])


# ---- Shared items checked where they're used (the mod's Model/Logic/LevelScope.cs) ------------------------------
# Units, entities, statuses and presets are shared by every library level, and a template may name the level it runs
# in ($vars, @names). So a level is checked with the shared items it uses (named anywhere in the level, its nights, or
# an item it uses, transitively), each template in that level's context; items no level uses are checked alone, their
# level references unchecked. One level's template never breaks another level.

SHARED = ("units", "entities", "statuses", "presets")
SHARED_FOLDER = {"units": "enemies", "entities": "entities", "statuses": "statuses", "presets": "presets"}
_IN_TEXT = re.compile(r"(?:\bis:|@@|\bhas:)([a-z0-9._-]+)|['\"]([a-z0-9._-]+)['\"]")


def mentions(tree):
    """Every id a JSON tree names: whole strings (a leading '=' or 'unit:' dropped) and ids written in text (is:, @@,
    has:, quoted literals in expressions)."""
    found = set()
    stack = [tree]
    while stack:
        t = stack.pop()
        if isinstance(t, dict):
            stack.extend(t.values())
        elif isinstance(t, list):
            stack.extend(t)
        elif isinstance(t, str):
            s = t.strip()
            found.add(s[1:].strip() if s.startswith("=") else s)
            if s.startswith("unit:"):
                found.add(s[5:])
            for m in _IN_TEXT.finditer(s):
                found.add(m.group(1) or m.group(2))
    return found


def _shared_index(data):
    idx = {}
    for f in SHARED:
        idx[f] = {}
        for x in data.get(f) or []:
            if isinstance(x, dict) and isinstance(x.get("id"), str) and x["id"] not in idx[f]:
                idx[f][x["id"]] = x
    return idx


def level_uses(data, lv, index=None):
    """{field: set of ids} of the shared items a level uses."""
    index = index or _shared_index(data)
    used = {f: set() for f in SHARED}
    queue = [lv] + [w for w in data.get("waveSets") or [] if lv.get("waveSet") and w.get("id") == lv.get("waveSet")]
    while queue:
        for i in mentions(queue.pop()):
            for f in SHARED:
                if i in index[f] and i not in used[f]:
                    used[f].add(i)
                    queue.append(index[f][i])
    return used


def level_scope(data, level_id):
    """The pack as one library level is checked: that level, its nights and the shared items it uses."""
    lv = find_level(data, level_id)
    used = level_uses(data, lv)
    out_ = dict(data, levels=[lv], waveSets=[w for w in data.get("waveSets") or [] if lv.get("waveSet") and w.get("id") == lv.get("waveSet")])
    for f in SHARED:
        out_[f] = [x for x in data.get(f) or [] if isinstance(x, dict) and x.get("id") in used[f]]
    return out_


def unused_scope(data):
    """The shared items no level uses, with no levels."""
    index = _shared_index(data)
    used = {f: set() for f in SHARED}
    for lv in data.get("levels") or []:
        for f, ids in level_uses(data, lv, index).items():
            used[f] |= ids
    out_ = dict(data, levels=[], waveSets=[])
    for f in SHARED:
        out_[f] = [x for x in data.get(f) or [] if isinstance(x, dict) and x.get("id") not in used[f]]
    return out_


_ITEM_PATH = re.compile(r"^(units|entities|statuses|presets|levels|waveSets)\[(\d+)\]")


def attribute(scoped, issue, level=None):
    """An issue of a scoped pack as the library reports it: the item named by id (units[id=snail].movement.path; a
    level's own paths start with `level.`, its nights' with `nights.`, as the set op takes them) and "file", the library
    file it comes from. A shared item's issue found in a level's context also says "level"."""
    m = _ITEM_PATH.match(issue.get("path") or "")
    if not m:
        return issue
    field, i, rest = m.group(1), int(m.group(2)), issue["path"][m.end():]
    items = scoped.get(field) or []
    iid = items[i].get("id") if i < len(items) and isinstance(items[i], dict) else None
    if iid is None:
        return issue
    if field == "levels":
        return {"severity": issue["severity"], "file": f"levels/{iid}.json", "path": "level" + rest, "message": issue["message"]}
    if field == "waveSets":
        owner = next((l.get("id") for l in scoped.get("levels") or [] if l.get("waveSet") == iid), None)
        return {"severity": issue["severity"], **({"file": f"levels/{owner}.json"} if owner else {}),
                "path": ("nights" if owner else f"waveSets[id={iid}]") + rest, "message": issue["message"]}
    return {"severity": issue["severity"], "file": f"{SHARED_FOLDER[field]}/{iid}.json", **({"level": level} if level else {}),
            "path": f"{field}[id={iid}]{rest}", "message": issue["message"]}


def library_check(ref, data, level_ids=None, only_files=None, skipped=()):
    """check_pack per library level (with the shared items it uses), then the unused items alone; attributed and
    without duplicates. level_ids limits it to those levels (and skips the unused items); only_files keeps the issues
    of those files (and checks every level, to find where those items are used)."""
    issues, seen = list(skipped), set()

    def add(i):
        key = (i["severity"], i.get("file"), i.get("path"), i["message"])
        if key not in seen and (only_files is None or i.get("file") in only_files):
            seen.add(key)
            issues.append(i)
    for lv in data.get("levels") or []:
        if level_ids is not None and lv.get("id") not in level_ids:
            continue
        scoped = level_scope(data, lv["id"])
        for i in check_pack(ref, scoped):
            add(attribute(scoped, i, level=lv["id"]))
    if level_ids is None:
        rest = unused_scope(data)
        for i in check_pack(ref, rest):
            if i.get("path") in ("format", "id"):
                continue
            add(attribute(rest, i))
    return issues


def cmd_apply(env, args):
    ref = Ref(env)
    path, data = load_pack(env, args.pack)
    if args.fresh:
        fresh_level(env, ref, data, args.level)
    ops = Ops(env, ref, data, args.level)
    ops.store = path
    ops_list = read_ops(args.ops)
    ops.run(ops_list)
    if args.no_check:
        issues = []
    elif isinstance(path, LibraryRef):
        # In the library, only the edited levels are checked, each with the shared items it uses (a template that
        # names another level's $vars or @places is fine there). Ops that edit only shared items check those items,
        # in every level that uses them.
        touched = {o.get("level") if isinstance(o.get("level"), str) else args.level for o in ops_list} - {None}
        if touched:
            issues = library_check(ref, data, level_ids={l for l in touched if any(x.get("id") == l for x in data.get("levels") or [])}, skipped=path.skipped)
        else:
            files = {f"{SHARED_FOLDER[f]}/{x['id']}.json" for f in SHARED for x in data.get(f) or []
                     if isinstance(x, dict) and x.get("id") in ops.edited}
            issues = library_check(ref, data, only_files=files, skipped=path.skipped)
    else:
        issues = check_pack(ref, data)
    errors = [i for i in issues if i["severity"] == "error"]
    if not args.dry_run:
        save_pack(path, data)
    out({"applied": ops.log, "saved": None if args.dry_run else str(path), "errors": errors,
         "warnings": [i for i in issues if i["severity"] == "warning"]})


def cmd_check(env, args):
    ref = Ref(env)
    path, data = load_pack(env, args.pack)
    if args.level:
        find_level(data, args.level)
    if isinstance(path, LibraryRef):
        issues = library_check(ref, data, level_ids={args.level} if args.level else None, skipped=path.skipped)
    else:
        issues = check_pack(ref, scope_levels(data, {args.level}) if args.level else data)
    result = {"pack": data.get("id"), "offline": issues}
    if args.live:
        g = Game(env)
        result["game"] = g.agent(f"validate {data['id']}" + (f" {args.level}" if args.level else ""))
        # The level loaded in the game: what its last pathfinding rescan left wrong (an empty graph, a ground line
        # with no route to the castle, a flier line off the fliers' graph). The game logs these as warnings too.
        try:
            status = g.agent("status")
            loaded = (status.get("session") or {}).get("level")
            if loaded and (not args.level or loaded == args.level) and any(l["id"] == loaded for l in data.get("levels", [])):
                problems = g.agent("paths").get("problems") or []
                result["game"]["pathProblems"] = {"level": loaded, "problems": problems}
                if problems:
                    result["game"]["ok"] = False
        except Fail:
            pass
    result["ok"] = not any(i["severity"] == "error" for i in issues) and (not args.live or result["game"].get("ok", False))
    out(result)
    if not result["ok"]:
        sys.exit(1)


def cmd_show(env, args):
    ref = Ref(env)
    _, data = load_pack(env, args.pack)
    lv = find_level(data, args.level)
    slots, castle, warns = effective_slots(ref, lv)
    rows = []
    for s in sorted(slots.values(), key=lambda q: (not q.castle, q.kind, q.label)):
        rows.append({"slot": s.label, "kind": s.kind, "at": [round(s.pos[0], 1), round(s.pos[2], 1)],
                     "needs": "start" if not s.req else (("castle" if s.req[0] == castle else (slots[s.req[0]].label if s.req[0] in slots else s.req[0])) + f":{s.req[1] + 1}"),
                     "costs": [u["cost"] for u in s.upgrades], "income": [step_income(u) for u in s.upgrades],
                     **({"upgradeCap": s.cap} if s.cap is not None else {}), **({"followsActivator": True} if s.follows else {})})
    ws, vanilla = level_waveset(ref, data, lv)
    out({"level": lv["id"], "host": lv["host"], "base": lv.get("base", "map"), "slots": rows, "spawnLines": level_lines(ref, lv),
         **({"removedSpawnLines": removed_lines(lv)} if removed_lines(lv) else {}),
         "props": len(lv.get("props") or []), "terrain": bool(lv.get("terrain")), "waveSet": lv.get("waveSet") or "(vanilla)",
         "nights": len(ws.get("nights", [])), "warnings": warns})


def cmd_economy(env, args):
    ref = Ref(env)
    _, data = load_pack(env, args.pack)
    lv = find_level(data, args.level)
    slots, castle, _ = effective_slots(ref, lv)
    ws, _ = level_waveset(ref, data, lv)
    nights = len(ws.get("nights", []))
    start = ws.get("startingGold", ref.map(lv["host"]).get("startingGold", 10))
    wg = [r["gold"] for r in wave_summary(ref, data, ws)]
    sim = economy_sim(slots, castle, start, wg, nights)
    full = sum(Earner(s).peak() for s in slots.values())
    cost = sum(sum(u["cost"] for u in s.upgrades) for s in slots.values())
    result = {"level": lv["id"], "startingGold": start, "nights": nights, "enemyGoldPerNight": wg, "maxIncomeAllBuilt": full, "costToBuildEverything": cost,
              "greedyEconomyPlayer": sim}
    if args.compare:
        hostlv = {"id": lv["host"], "host": lv["host"]}
        hs, hc, _ = effective_slots(ref, hostlv)
        h = ref.host(lv["host"])
        hw = [r["gold"] for r in wave_summary(ref, {}, h["vanillaWaves"])]
        result["vanilla_" + lv["host"]] = [{k: r[k] for k in ("day", "goldAtDusk", "incomePerDawn")} for r in
                                          economy_sim(hs, hc, h["startingGold"], hw, len(h["vanillaWaves"]["nights"]))]
    if args.report:
        result["fromTest"] = economy_from_report(json.loads(Path(args.report).read_text()), slots)
    out(result)


def economy_from_report(report, slots):
    """What a `test` run's economy actually paid: per night, the income of the buildings standing at dawn vs the income
    knocked-out buildings lost (they pay nothing the next dawn), and which buildings were knocked out most often."""
    by_name = {s.name: s for s in slots.values()}
    nights = list((report.get("catchup") or {}).get("nights") or []) + list(report.get("nights") or [])
    rows, counts = [], {}
    for r in nights:
        ko = r.get("knockedOut") if "knockedOut" in r else (r.get("buildings") or {}).get("knockedOut")
        ko = ko or []
        for name in ko:
            counts[name] = counts.get(name, 0) + 1
        inc = r.get("income")
        if inc:
            row = {"night": r.get("night"), "outcome": r.get("outcome"), "incomeNominal": inc.get("nominal"),
                   "incomeLost": inc.get("blocked"), "incomePaid": inc.get("paid")}
        else:
            # An older report: estimate the loss from the slots' first-level income.
            lost = sum(step_income(by_name[n].upgrades[0]) for n in ko if n in by_name and by_name[n].upgrades)
            nominal = ((r.get("after") or {}).get("buildings") or {}).get("income")
            row = {"night": r.get("night"), "outcome": r.get("outcome"), "incomeNominal": nominal, "incomeLostEstimate": lost,
                   "incomePaidEstimate": None if nominal is None else nominal - lost}
        row["knockedOut"] = len(ko)
        rows.append(row)
    played = max(1, len(nights))
    exposed = sorted(({"building": n.replace("PalaceBuilder ", ""), "knockedOutNights": c, "share": round(c / played, 2),
                       "income": step_income(by_name[n].upgrades[0]) if n in by_name and by_name[n].upgrades else None}
                      for n, c in counts.items()), key=lambda e: -e["knockedOutNights"])
    paid = [r.get("incomePaid", r.get("incomePaidEstimate")) for r in rows]
    nominal = [r.get("incomeNominal") for r in rows]
    total_paid = sum(p for p in paid if p is not None)
    total_nominal = sum(p for p in nominal if p is not None)
    return {"nights": rows, "paidVsNominal": round(total_paid / total_nominal, 2) if total_nominal else None,
            "mostKnockedOut": exposed[:15]}


def cmd_waves(env, args):
    ref = Ref(env)
    _, data = load_pack(env, args.pack)
    lv = find_level(data, args.level)
    ws, vanilla = level_waveset(ref, data, lv)
    rows = wave_summary(ref, data, ws)
    result = {"waveSet": ws.get("id", "(vanilla)"), "nights": rows}
    if args.compare:
        h = ref.host(args.compare if args.compare != "host" else lv["host"])
        result["vanilla"] = [{k: r[k] for k in ("night", "enemies", "effectiveHp", "gold", "flying")} for r in wave_summary(ref, {}, h["vanillaWaves"])]
    out(result)


def cmd_render(env, args):
    ref = Ref(env)
    _, data = load_pack(env, args.pack)
    lv = find_level(data, args.level)
    slots, castle, _ = effective_slots(ref, lv)
    o = args.out or f"{lv['id']}.png"
    if args.live:
        g = Game(env)
        st = g.status()
        sess = st.get("session") or {}
        if sess.get("pack") != data["id"] or sess.get("level") != lv["id"]:
            raise Fail(f"--live needs the level loaded in the game (`palace.py play {data['id']} {lv['id']}`); the game is on {st.get('scene')}")
        raw = Path(o).with_suffix(".raw.png").resolve()
        cap = g.agent(f"capture {raw} {args.pixels}", timeout=60)
        bg = read_png(raw)
        paths = g.agent("paths")
        routes = [{k: r.get(k) for k in ("line", "reachable", "points", "length", "warning")} for r in paths.get("routes", [])]
        out(render(ref, data, lv, o, background=bg, world=cap["world"], routes=routes, labels=args.labels, route_source="game"))
    else:
        routes = offline_routes(ref, data, lv, slots, castle) if castle in slots and not args.no_routes else []
        out(render(ref, data, lv, o, routes=routes, labels=args.labels, px=args.pixels))


def cmd_paths(env, args):
    g = Game(env)
    if args.between:
        x1, z1, x2, z2 = coords(args.between, 4, "paths --between X1,Z1 X2,Z2")
        out(g.agent(f"reach {x1} {z1} {x2} {z2}", timeout=60))
        return
    r = g.agent("paths rescan" if args.rescan else "paths", timeout=60)
    for route in r.get("routes", []):
        if not args.points:
            route.pop("points", None)
    warnings = [f"{route['line']}: {route['warning']}" for route in r.get("routes", []) if route.get("warning")]
    if warnings:
        r["warnings"] = warnings
    out(r)


def cmd_play(env, args):
    g = Game(env)
    _, data = load_pack(env, args.pack)
    find_level(data, args.level)
    v = g.agent(f"validate {data['id']} {args.level}")
    if not v.get("ok"):
        out(v)
        raise Fail("the game's validator found errors (above)")
    mark = g.log_size()
    g.agent(f"play {data['id']} {args.level}")
    st = g.wait_level_day(timeout=args.timeout)
    time.sleep(2.5)  # the level's rescan and path check run a moment after start
    notes = g.log_since(mark, features=["Builder"], min_level="warn")
    out({"status": g.status(), "loadWarnings": notes})


def wait_night(g, timeout, night=None, poll=1.0):
    """Waits for the night's report. `night` (the number the `night` command started) guards against reading the
    previous night's finished report before the game has begun recording the new one."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        r = g.agent("report")
        if r.get("done") and (night is None or r.get("night") == night):
            return r
        time.sleep(poll)
    r = g.agent("report")
    r["outcome"] = r.get("outcome") or "timeout"
    st = g.status()
    r["timeoutStatus"] = {"enemiesLeft": st.get("enemies"), "time": st.get("time")}
    return r


def cmd_night(env, args):
    g = Game(env)
    if args.speed:
        g.agent(f"speed {args.speed}")
    expected = args.night or g.status().get("night")
    g.agent("night" + (f" {args.night}" if args.night else ""))
    if args.no_wait:
        out(g.status())
        return
    out(wait_night(g, args.timeout, expected))


def parse_nights(spec, total):
    if not spec:
        return list(range(1, total + 1))
    if "-" in spec:
        a, b = spec.split("-")
        return list(range(int(a), int(b) + 1))
    return sorted({int(x) for x in spec.split(",")})


class Plan:
    """A test plan: {"1": [items], "2": [...], "options": {"carry": true, "save": false}}. An item is a slot id (one
    level up) or {"slot", "level" (up to; 1 = built), "branch", "carry", "save"}. carry: a refused item is retried at the
    start of the next day (until bought). save: when this item is refused, buy nothing else that day (keep the gold for
    it); with carry the rest of the day's items are carried too. "king": {"3": "t1", "6": [x, z]}: where the king stands
    from that night on (see king_point)."""

    def __init__(self, data, carry=False, save=False):
        data = data or {}
        if not isinstance(data, dict):
            raise Fail("a plan is a JSON object: {\"1\": [\"castle\", \"h1\"], \"2\": [...]}")
        opts = data.get("options") or {}
        self.carry = bool(opts.get("carry", carry))
        self.save = bool(opts.get("save", save))
        self.days = {}
        king = data.get("king") or {}
        if not isinstance(king, dict) or any(not str(k).isdigit() for k in king):
            raise Fail("plan \"king\": {\"<night>\": \"castle\" | \"<slot id>\" | [x, z], ...}")
        self.king = {int(k): v for k, v in king.items()}
        for k, items in data.items():
            if k in ("options", "king"):
                continue
            if not str(k).isdigit() or not isinstance(items, list):
                raise Fail(f"plan key {k!r}: days are \"1\", \"2\", ... each with a list of items")
            self.days[int(k)] = [self.item(x, int(k)) for x in items]

    @staticmethod
    def item(x, day):
        if isinstance(x, str):
            x = {"slot": x}
        if not isinstance(x, dict) or not x.get("slot"):
            raise Fail(f"plan day {day}: an item is a slot id or {{\"slot\": ..., \"level\": N, \"branch\": B}}, not {x!r}")
        return dict(x, day=day)

    def slots(self):
        return {i["slot"] for items in self.days.values() for i in items}


def king_point(spec, slots, castle):
    """Where the king stands for a test: "castle" (just in front of it), a slot id (just in front of that slot, toward
    the castle, so he isn't inside the building), or x,z ([x, z] or "x,z"). Returns (x, z)."""
    if isinstance(spec, (list, tuple)) and len(spec) == 2:
        return float(spec[0]), float(spec[1])
    text = str(spec).strip()
    m = re.match(r"^\s*(-?[\d.]+)\s*[, ]\s*(-?[\d.]+)\s*$", text)
    if m:
        return float(m.group(1)), float(m.group(2))
    c = slots.get(castle) if castle else None
    target = c if text == "castle" else next((s for s in slots.values() if text in (s.label, s.name, s.key) or
                                               text.replace("_", " ") in (s.label, s.name)), None)
    if target is None:
        raise Fail(f"king position {spec!r}: not castle, a slot id on this level, or x,z")
    x, z = target.pos[0], target.pos[2]
    if c is not None:
        dx, dz = c.pos[0] - x, c.pos[2] - z
        d = (dx * dx + dz * dz) ** 0.5
        if target is c or d < 1e-3:
            dx, dz, d = 0.0, -1.0, 1.0  # in front of the castle: a little south of it
        step = 6.0 if target is c else min(5.0, d)
        x, z = x + dx / d * step, z + dz / d * step
    return round(x, 2), round(z, 2)


def plan_command(item, free):
    return f"build {item['slot'].replace(' ', '_')}" + (f" {item['level']}" if "level" in item else "") + \
        (f" {item.get('branch', 0)}" if "branch" in item else "") + ("" if free else " pay")


# Refusals worth retrying later: gold, an unmet requirement, the castle-level cap. A wrong slot name isn't.
RETRYABLE = ("not enough gold", "not available yet", "can't go past level")


def buy_items(g, items, free, carry_default, save_default, day):
    """Buys items in order. Returns (entries, carried): one report entry per item, and the refused items to retry."""
    entries, carried = [], []
    saving = None
    for item in items:
        if saving:
            entry = {"slot": item["slot"], "day": item["day"], "ok": False, "error": f"skipped: saving gold for {saving}"}
            entries.append(entry)
            if item.get("carry", carry_default):
                carried.append(item)
            continue
        try:
            r = g.agent(plan_command(item, free))
        except Fail as e:
            r = {"slot": item["slot"], "ok": False, "error": str(e)}
        if "result" in r and "ok" not in r:  # an older game: no structured build result
            r = {"slot": item["slot"], "ok": True, "result": r["result"]}
        entry = dict(r)
        if item["day"] != day:
            entry["plannedDay"] = item["day"]
        entries.append(entry)
        if entry.get("ok") is False:
            retry = any(k in (entry.get("error") or "") for k in RETRYABLE)
            if retry and item.get("carry", carry_default):
                carried.append(item)
                entry["carried"] = True
            if item.get("save", save_default) and "not enough gold" in (entry.get("error") or ""):
                saving = item["slot"]
    return entries, carried


def wait_settled(g, timeout=30):
    """Waits for the day to be ready for purchases: after sunrise, and every coin (dawn income, enemy drops) in the
    treasury, so the day's gold is all there before the first purchase."""
    deadline = time.time() + timeout
    calm = 0
    last = None
    while time.time() < deadline:
        last = g.status()
        if last.get("state") in ("AfterMatchVictory", "AfterMatchDefeat"):
            return last
        if last.get("time") == "day" and not last.get("coinsPending"):
            calm += 1
            if calm >= 2:  # twice in a row: nothing still being spawned
                return last
        else:
            calm = 0
        time.sleep(0.4)
    return last or g.status()


def progress(args, text):
    if not args.quiet:
        print(text, file=sys.stderr, flush=True)


def night_line(rep, total):
    c = rep.get("castle") or {}
    ko = (rep.get("buildings") or {}).get("knockedOut") or []
    failed = [b for b in rep.get("bought", []) if b.get("ok") is False]
    s = f"night {rep.get('night')}/{total}: {rep.get('outcome')} in {rep.get('seconds')} s, castle {c.get('minHp')}/{c.get('maxHp')}, " \
        f"{len(ko)} knocked out, bought {sum(1 for b in rep.get('bought', []) if b.get('ok'))}"
    if failed:
        s += f", {len(failed)} refused (" + "; ".join(f"{b.get('slot')}: {b.get('error')}" for b in failed[:3]) + ("; ..." if len(failed) > 3 else "") + ")"
    gold = rep.get("gold") or {}
    if gold.get("afterDawn") is not None:
        s += f", gold after dawn {gold['afterDawn']}"
    return s


class ResumePoints:
    """Savestates a full `test` takes at each dawn (before buying), so `test --from-night N` can replay only nights N..
    after an edit. A state is only reused when nothing it depends on changed: the level (terrain, slots, props, spawn
    lines, economy), the waves of nights before N and the library enemies they use, the plan's days before N (and its
    options and king posts before N), and the test flags that change play (--free, --build, --gold, --god,
    --invulnerable, --king, --carry, --save). Waves of nights N.. (and enemies only they use) and the plan from day N on
    may change."""

    FLAGS = ("free", "build", "gold", "god", "invulnerable", "king", "carry", "save")
    WHY = {"level": "the level (terrain, slots, props, spawn lines, costs)", "waves": "the waves of the nights before it (or starting gold)",
           "enemies": "library enemies used in the nights before it", "plan": "the plan's days before it (or its options or king posts)",
           "flags": "test flags (--free/--build/--gold/--god/--invulnerable/--king/--carry/--save)", "assets": "the library's assets"}

    def __init__(self, env, data, lv, plan_raw, args):
        self.env, self.data, self.lv, self.plan_raw, self.args = env, data, lv, plan_raw, args
        self.folder = env.need_game() / "PalaceMod" / "Agent" / "test-states" / f"{data['id']}--{lv['id']}"

    def parts(self, n):
        """What a state at the dawn before night n depends on, by part (each a JSON string)."""
        ws = find_waveset(self.data, self.lv.get("waveSet")) or {}
        early = (ws.get("nights") or [])[:n - 1]
        used = {str(sp.get("unit", "")).split(":")[-1] for night in early for sp in night.get("spawns") or []}
        enemies = sorted((u for u in self.data.get("units") or [] if u.get("id") in used), key=lambda u: u.get("id"))
        plan = {k: v for k, v in self.plan_raw.items() if not str(k).isdigit() or int(k) < n}
        if isinstance(plan.get("king"), dict):
            plan["king"] = {k: v for k, v in plan["king"].items() if str(k).isdigit() and int(k) < n}
        dump = lambda o: json.dumps(o, sort_keys=True)
        return {
            "level": dump(self.lv),
            "waves": dump([{k: v for k, v in ws.items() if k != "nights"}, early]),
            "enemies": dump(enemies),
            "plan": dump(plan),
            "flags": dump({f: getattr(self.args, f, None) for f in self.FLAGS}),
            "assets": dump(self.data.get("assets") or {}),
        }

    def fingerprint(self, n):
        return {k: hashlib.sha1(v.encode()).hexdigest()[:12] for k, v in self.parts(n).items()}

    def state_name(self, n):
        return f"test {self.data['id']}/{self.lv['id']} day {n}"

    def path(self, n):
        return self.folder / f"day{n}.json"

    def save(self, g, n, carried, earlier):
        """Saves the resume point for night n (now: the dawn before it). Returns a warning if it couldn't."""
        name = self.state_name(n)
        try:
            g.raw(f"savestate forget {name}")
        except Fail:
            pass
        try:
            g.raw(f"savestate save {name}")
        except Fail as e:
            # E.g. savestates turned off: say so once, then stop trying for the rest of the run.
            self.path(n).unlink(missing_ok=True)
            self.args.states = False
            return f"no resume states (from night {n} on): {e}"
        _write_json(self.path(n), {
            "state": name, "night": n, "taken": time.strftime("%Y-%m-%dT%H:%M:%S"), "fingerprint": self.fingerprint(n),
            "carried": carried, "speed": self.args.speed, "fast": self.args.fast,
            "nights": [{k: r.get(k) for k in ("night", "outcome", "seconds", "castle", "gold")} for r in earlier]})
        return None

    def check(self, n):
        """The resume point for night n, or a Fail that says why it can't be used."""
        if n < 2:
            raise Fail("--from-night N needs N >= 2 (night 1 is a plain full test)")
        f = self.path(n)
        if not f.exists():
            have = sorted(int(p.stem[3:]) for p in self.folder.glob("day*.json")) if self.folder.is_dir() else []
            raise Fail(f"no resume state for night {n} of {self.lv['id']}" + (f" (have: nights {have})" if have else "") +
                       ": run a full `test` first (it saves one at each dawn)")
        meta = json.loads(f.read_text())
        now = self.fingerprint(n)
        changed = [k for k, v in now.items() if (meta.get("fingerprint") or {}).get(k) != v]
        if changed and not self.args.force_resume:
            raise Fail(f"the night-{n} resume state is stale, taken {meta.get('taken')} before a change to " +
                       "; ".join(self.WHY[k] for k in changed) + ". Run a full test again (or --force-resume to use it anyway).")
        return meta

    def restore(self, g, meta, timeout):
        g.raw(f"savestate load {meta['state']}")
        n = meta["night"]
        deadline = time.time() + timeout
        time.sleep(1.0)
        while time.time() < deadline:
            try:
                restoring = "restoring=True" in g.raw("savestate dump")
                st = g.status()
            except Fail:
                time.sleep(0.5)
                continue
            if not restoring and st.get("inLevel") and not st.get("loading") and st.get("time") == "day" and st.get("night") == n:
                return st
            time.sleep(0.5)
        raise Fail(f"the night-{n} resume state didn't finish loading within {timeout} s")


def cmd_test(env, args):
    """Loads the level, then for each night: waits for the day's gold to land, buys the plan's (or --build's) items for
    that day, starts the night, waits for its end, records the report (and screenshots). Stops at a defeat. Nights
    before the first requested one are played too (catch-up), unless --catchup says otherwise."""
    g = Game(env)
    if not args.fast:
        return run_test(env, args, g)
    # Fast simulation: the same fixed 1/60 s steps as a smooth 60 fps run at --speed, as fast as the CPU allows.
    g.agent(f"fast on {args.fps:g}")
    try:
        return run_test(env, args, g)
    finally:
        try:
            g.agent("fast off")
        except Fail:
            pass


def run_test(env, args, g):
    _, data = load_pack(env, args.pack)
    lv = find_level(data, args.level)
    plan_raw = json.loads(Path(args.plan).read_text()) if args.plan else {}
    plan = Plan(plan_raw, carry=args.carry, save=args.save)
    resume = ResumePoints(env, data, lv, plan_raw, args) if (args.states or args.from_night) else None
    resumed = resume.check(args.from_night) if args.from_night else None
    warnings = []
    slots, castle = {}, None
    try:
        slots, castle, _ = effective_slots(Ref(env), lv)
        known = {s.label for s in slots.values()} | {"castle"}
        unknown = sorted(s for s in plan.slots() if s not in known and s.replace("_", " ") not in known)
        if unknown:
            warnings.append(f"plan slots not on this level: {', '.join(unknown)}")
    except Fail:
        pass
    # Where the king stands each night: --king, then the plan's "king" entries from their night on.
    king_specs = dict(plan.king)
    if args.king:
        king_specs.setdefault(0, args.king)
    king_at = {n: king_point(spec, slots, castle) for n, spec in king_specs.items()}
    if not args.no_load or resumed:
        cmd_play_quiet(g, data, lv["id"], args.timeout_load)
    if resumed:
        resume.restore(g, resumed, args.timeout_load)
    st = g.status()
    total = st.get("nights", 0)
    nights = parse_nights(args.nights or (f"{args.from_night}-{total}" if resumed else None), total)
    if not nights:
        raise Fail("no nights to play")
    if resumed and nights[0] < args.from_night:
        raise Fail(f"--nights {args.nights}: starts before --from-night {args.from_night}")
    catchup = "none" if args.no_catchup else args.catchup
    if args.god:
        g.agent("god on")
    if args.invulnerable:
        g.agent("invulnerable on")
    if args.gold is not None and not resumed:
        g.agent(f"gold {args.gold}")
    if args.speed:
        g.agent(f"speed {args.speed}")
    shots = Path(args.shots) if args.shots else None
    results, briefs, catchup_bought = [], [], []
    carried = [Plan.item(x, x.get("day", 0)) for x in resumed["carried"]] if resumed else []
    current = st.get("night", 1)
    first, last = nights[0], nights[-1]
    jump = first > current and catchup in ("free", "none")
    if jump and catchup == "free":
        # Skip ahead: the earlier days' items are built for free, then the run jumps to the first night.
        for day in range(current, first):
            entries, _ = buy_items(g, plan.days.get(day, []), True, False, False, day)
            catchup_bought += entries
    to_play = list(range(first if jump else current, last + 1))
    for n in to_play:
        st = g.status()
        if st.get("state") in ("AfterMatchDefeat", "AfterMatchVictory"):
            break
        wait_settled(g)
        if resume and args.states and n > 1 and not (resumed and n == args.from_night):
            # Dawn before night n, the day's gold in, nothing bought yet: `--from-night n` starts here.
            note = resume.save(g, n, carried, briefs + results)
            if note:
                warnings.append(note)
        items = carried + plan.days.get(n, [])
        bought, carried = buy_items(g, items, args.free, plan.carry, plan.save, n)
        if args.build == "all":
            bought.append(g.agent("build all"))
        king = None
        spot = max((k for k in king_at if k <= n), default=None)
        if spot is not None:
            # Every night, so a king knocked out the night before (back at the castle) goes back to his post.
            king = {"at": list(king_at[spot]), "stood": (g.agent(f"teleport {king_at[spot][0]:g} {king_at[spot][1]:g}") or {}).get("king")}
        if jump:
            g.agent(f"night {n}")  # jumps to night n and starts it
            jump = False
        else:
            g.agent("night")
        if args.speed:
            g.agent(f"speed {args.speed}")
        mid = None
        if shots and n in nights:
            time.sleep(args.shot_after)
            mid = g.screenshot(shots / f"night{n}.png")
        rep = wait_night(g, args.timeout, n, poll=0.25 if args.fast else 1.0)
        rep["bought"] = bought
        if king:
            rep["kingPost"] = king
        if mid:
            rep["screenshot"] = mid
        st = g.status()
        if rep.get("outcome") == "dawn" and rep.get("night") == total:
            # The last night: the game declares the win a few seconds after dawn.
            try:
                st = g.wait(lambda s: s.get("state") in ("AfterMatchVictory", "AfterMatchDefeat"), 30, "the end screen")
            except Fail:
                pass
        if st.get("state") == "AfterMatchVictory":
            rep["outcome"] = "victory"
        elif st.get("state") == "AfterMatchDefeat":
            rep["outcome"] = "defeat"
        rep.setdefault("gold", {})
        rep["gold"]["afterDawn"] = None
        rep["income"] = None
        if rep.get("outcome") == "dawn":
            # The next morning, once its income and the night's coins are in: what the next day can spend.
            st = wait_settled(g)
            b = st.get("buildings") or {}
            rep["gold"]["afterDawn"] = st.get("gold")
            if "incomeBlocked" in b:
                rep["income"] = {"nominal": b.get("income"), "blocked": b.get("incomeBlocked"),
                                 "paid": b.get("income", 0) - b.get("incomeBlocked", 0)}
        rep["after"] = {k: st.get(k) for k in ("time", "gold", "castle", "buildings", "units", "state")}
        if carried:
            rep["carriedToNextDay"] = [i["slot"] for i in carried]
        if n in nights:
            results.append(rep)
            progress(args, night_line(rep, total))
        else:
            brief = {k: rep.get(k) for k in ("night", "outcome", "seconds", "castle", "gold", "income")}
            brief["knockedOut"] = (rep.get("buildings") or {}).get("knockedOut")
            brief["bought"] = bought
            briefs.append(brief)
            progress(args, "catch-up " + night_line(rep, total))
        if rep.get("outcome") in ("defeat", "timeout", "victory", "left"):
            break
    outcomes = [r.get("outcome") for r in results] + [b.get("outcome") for b in briefs]
    result = next((o for o in ("defeat", "timeout", "left") if o in outcomes), None) or \
        ("victory" if "victory" in outcomes else "survived")
    failed = [dict(b, night=r["night"]) for r in results for b in r.get("bought", []) if b.get("ok") is False]
    report = {"level": f"{data['id']}/{lv['id']}", "result": result, "nightsPlayed": len(results) + len(briefs),
              "refusedPurchases": len(failed), "plan": {"carry": plan.carry, "save": plan.save}}
    if warnings:
        report["warnings"] = warnings
    if briefs or catchup_bought:
        report["catchup"] = {"mode": catchup, "nights": briefs, "bought": catchup_bought}
    if resumed:
        report["resumedFrom"] = {"night": args.from_night, "state": resumed["state"], "taken": resumed["taken"],
                                 "earlierNights": resumed.get("nights")}
    report["summary"] = [{"night": r.get("night"), "outcome": r.get("outcome"), "castleMin": (r.get("castle") or {}).get("minHp"),
                          "knockedOut": len((r.get("buildings") or {}).get("knockedOut") or []),
                          "bought": [f"{b.get('slot')} L{b.get('to')}" for b in r.get("bought", []) if b.get("ok")],
                          "refused": [f"{b.get('slot')}: {b.get('error')}" for b in r.get("bought", []) if b.get("ok") is False],
                          "goldAfterDawn": (r.get("gold") or {}).get("afterDawn"),
                          "incomePaid": (r.get("income") or {}).get("paid")} for r in results]
    report["nights"] = results
    out(report)
    if result in ("defeat", "timeout", "left"):
        sys.exit(1)


def cmd_play_quiet(g, data, level, timeout):
    v = g.agent(f"validate {data['id']} {level}")
    if not v.get("ok"):
        out(v)
        raise Fail("the game's validator found errors (above)")
    g.agent(f"play {data['id']} {level}")
    g.wait_level_day(timeout=timeout)
    time.sleep(2.5)


# Behaviour engine: schema, offline checks, live trace and the example fixtures
# =====================================================================================================================

def logic_schema(env):
    """The behaviour engine's registry: the game's (catalog.json "logic", cached by `ref build`) when it's there, else
    the kit's own logic-schema.json (generated from the same code)."""
    cat = env.ref / "catalog.json"
    if cat.exists():
        try:
            lg = json.loads(cat.read_text()).get("logic")
            if lg:
                return lg
        except ValueError:
            pass
    f = Path(__file__).with_name("logic-schema.json")
    if not f.exists():
        raise Fail(f"no logic-schema.json next to palace.py ({f})")
    return json.loads(f.read_text())


def logic_module():
    sys.path.insert(0, str(Path(__file__).parent))
    try:
        import palace_logic
    except ImportError as e:
        raise Fail(f"palace_logic.py (next to palace.py) is missing: {e}")
    return palace_logic


def logic_issues(data):
    """The behaviour engine's offline checks, as check_pack issues."""
    pl = logic_module()
    raw = []
    pl.validate_logic(data, raw)
    return [{"severity": sev, "path": path, "message": msg} for sev, path, msg in raw]


def _schema_entry(schema, kind, name):
    key = {"event": "events", "action": "actions", "fn": "functions", "function": "functions", "stat": "stats", "flag": "flags",
           "preset": "presets", "global": "globals", "role": "roles", "filter": "filterTerms"}.get(kind)
    if key is None:
        raise Fail("kind is event | action | fn | stat | flag | preset | global | role | filter")
    items = schema.get(key) or []
    hit = next((x for x in items if x.get("name", x.get("id")) == name), None)
    if hit is None:
        names = [x.get("name", x.get("id")) for x in items]
        close = sorted(names, key=lambda n: _edit_distance(n.lower(), name.lower()))[:3]
        raise Fail(f"no {kind} {name!r}; closest: {', '.join(close)}")
    if key == "properties":
        return hit
    return hit


def _edit_distance(a, b):
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def cmd_logic(env, args):
    sub = args.what
    rest = list(args.rest or [])
    if sub in ("-h", "--help", "help") or rest[:1] in (["-h"], ["--help"]):
        out({"usage": "logic schema [kind] [name] | logic doc [file] [--kit] | logic check <pack> [level] | logic fixture <file> | "
                      "logic status|vars [selector]|trace [n]|entities [filter]|budget|eval <expr>|do <action JSON>|fire <signal> [to] [value]|"
                      "raise <event> <on> [other|-] [name|-] [value]|set <var> <expr>|king <1-3> (in the game)",
             "examples": ["logic check library mylevel", "logic schema action spawn", "logic eval \"count('is:slime')\""]})
        return
    for flag in ("--kit", "--json"):   # flags after the subcommand land in rest (argparse REMAINDER)
        if flag in rest:
            rest.remove(flag)
            setattr(args, flag[2:], True)
    if sub == "schema":
        schema = logic_schema(env)
        if len(rest) >= 2:
            out(_schema_entry(schema, rest[0], rest[1]))
            return
        if rest and rest[0] not in ("--json",):
            key = {"events": "events", "actions": "actions", "functions": "functions", "fns": "functions", "stats": "stats", "flags": "flags",
                   "presets": "presets", "globals": "globals", "roles": "roles", "properties": "properties", "filters": "filterTerms",
                   "tags": "tags", "inputs": "inputs", "caps": "caps", "game": "gameRules"}.get(rest[0])
            if key is None:
                raise Fail("logic schema [events|actions|functions|properties|globals|roles|stats|flags|filters|tags|inputs|presets|caps|game] "
                           "| logic schema <event|action|fn|stat|flag|preset|global|role|filter> <name>")
            out(schema.get(key))
            return
        if args.json:
            out(schema)
            return
        out({"version": schema.get("version"),
             "events": {e["name"]: e["description"] for e in schema["events"]},
             "actions": {a["name"]: a["description"] for a in schema["actions"]},
             "functions": [f["name"] for f in schema["functions"]],
             "globals": [g["name"] for g in schema["globals"]],
             "stats": [x["name"] for x in schema["stats"]], "flags": [x["name"] for x in schema["flags"]],
             "presets": {pr["id"]: pr.get("description") for pr in schema.get("presets") or []},
             "more": "palace.py logic schema <event|action|fn|preset|...> <name> for params, roles and examples"})
        return
    if sub == "check":
        if not rest:
            raise Fail("logic check <pack> [level]")
        lpath, data = load_pack(env, rest[0])
        if len(rest) > 1:
            find_level(data, rest[1])
            if isinstance(lpath, LibraryRef):
                scoped = level_scope(data, rest[1])
                issues = [attribute(scoped, i, level=rest[1]) for i in logic_issues(scoped)]
            else:
                issues = logic_issues(scope_levels(data, {rest[1]}))
        else:
            issues = logic_issues(data)
        ok = not any(i["severity"] == "error" for i in issues)
        out({"ok": ok, "errors": [i for i in issues if i["severity"] == "error"], "warnings": [i for i in issues if i["severity"] == "warning"]})
        if not ok:
            sys.exit(1)
        return
    if sub == "doc":
        text = logic_reference_markdown(logic_schema(env) if not args.kit else json.loads(Path(__file__).with_name("logic-schema.json").read_text()))
        target = Path(rest[0]) if rest else Path(__file__).with_name("logic-reference.md")
        target.write_text(text, encoding="utf-8")
        out({"written": str(target.resolve())})
        return
    if sub == "fixture":
        if not rest:
            raise Fail("logic fixture <fixture.json> [--keep]")
        out(run_fixture(env, Path(rest[0]), timeout=args.timeout))
        return
    # Everything else goes to the game's `logic` command (status, vars, trace, entities, budget, eval, do, fire, raise, set).
    g = Game(env)
    text = "logic " + " ".join([sub] + rest)
    out({"command": text, "result": g.raw(text, timeout=args.timeout).strip()})


def _md(text):
    return str(text if text is not None else "").replace("|", "\\|").replace("\n", " ")


def logic_reference_markdown(schema):
    """agent-kit/logic-reference.md: the behaviour engine's whole vocabulary, generated from logic-schema.json."""
    L = ["# Behaviour engine reference", "",
         f"Generated from the engine's registry (`palace.py logic doc`; schema version {schema.get('version')}). The guide's "
         "\"Behaviour\" chapter explains how the pieces fit; this lists every piece. `palace.py logic schema <kind> <name>` "
         "prints one entry as JSON.", ""]
    L += ["## Param types", "", "| Type | Accepts |", "|---|---|"]
    pt = schema.get("paramTypes") or {}
    for k, v in (pt.items() if isinstance(pt, dict) else ((t, "") for t in pt)):
        L.append(f"| `{k}` | {_md(v)} |")
    L += ["", "## Roles", "", "Names an expression can use for entities in a rule.", "", "| Role | Type | Meaning |", "|---|---|---|"]
    L += [f"| `{r['name']}` | {r['type']} | {_md(r['description'])} |" for r in schema["roles"]]
    L += ["", "## Globals", "", "| Name | Type | Meaning |", "|---|---|---|"]
    L += [f"| `{g['name']}` | {g['type']} | {_md(g['description'])} |" for g in schema["globals"]]
    L += ["", "## Events", "", "`when` takes the name, or `{\"on\": name, ...params}`; add `\"of\": \"<filter>\"` (and `\"range\"`) to hear "
          "the event on other entities (then `subject` is who it happened to). `other` and `event.value` are as listed; "
          "aliases name `other`/`subject` per event.", ""]
    groups = {}
    for e in schema["events"]:
        groups.setdefault(e["group"], []).append(e)
    for gname, evs in groups.items():
        L += [f"### {gname}", "", "| Event | Params | other | event.value | Fires when |", "|---|---|---|---|---|"]
        for e in evs:
            ps = ", ".join(f"`{p['name']}`{'*' if p.get('required') else ''}" for p in e["params"]) or ""
            other = e.get("other") or ""
            if e.get("aliases"):
                other += " (" + ", ".join(f"{k} = {v}" for k, v in e["aliases"].items()) + ")"
            val = f"{e['value']}: {e.get('valueMeaning') or ''}" if e.get("value") else ""
            extra = (" Fields: " + ", ".join(f"event.{k}" for k in e["fields"]) + ".") if e.get("fields") else ""
            scope = "" if "any" in e.get("owners", ["any"]) else f" ({'/'.join(e['owners'])})"
            L.append(f"| `{e['name']}`{scope} | {ps} | {_md(other)} | {_md(val)} | {_md(e['description'] + extra)}{' Level script: no `of` needed.' if e.get('global') else ''} |")
        L.append("")
    L += ["`*` = required.", "", "## Actions", "",
          "Each action is `{\"do\": \"<name>\", ...params}`. `target` (where listed) is a selector, default `self`; on a set the action "
          "runs once per member with `it` bound to it.", ""]
    groups = {}
    for a in schema["actions"]:
        groups.setdefault(a["group"], []).append(a)
    for gname, acts in groups.items():
        L += [f"### {gname}", ""]
        for a in acts:
            L.append(f"**`{a['name']}`**: {_md(a['description'])}")
            L.append("")
            for p in a["params"]:
                bits = [p["type"]]
                if p.get("required"):
                    bits.append("required")
                if "default" in p:
                    bits.append("default " + json.dumps(p["default"]))
                if p.get("min") is not None or p.get("max") is not None:
                    bits.append(("" if p.get("min") is None else f"{p['min']:g}") + ".." + ("" if p.get("max") is None else f"{p['max']:g}"))
                if p.get("options"):
                    bits.append("one of " + ", ".join(map(str, p["options"])))
                if p.get("binds"):
                    bits.append(f"binds `{p['binds']}`")
                L.append(f"- `{p['name']}` ({'; '.join(bits)}): {_md(p.get('description'))}")
            if a.get("example"):
                L += ["", f"  `{a['example']}`"]
            L.append("")
    L += ["## Functions", "", "Selector functions anchor at `self`; call them as methods (`castle.count('enemy', 10)`) to anchor elsewhere. "
          "A filter argument is a quoted filter string.", "", "| Function | Returns | Meaning |", "|---|---|---|"]
    for f in schema["functions"]:
        sig = f"{f['name']}({', '.join(f['args'])})"
        if f.get("onlyOn"):
            sig = f"<{f['onlyOn']}>." + sig
        elif f.get("method"):
            sig += " (also e." + f["name"] + "(...))"
        L.append(f"| `{sig}` | {f['returns']} | {_md(f['description'])}{(' e.g. `' + f['example'] + '`') if f.get('example') else ''} |")
    L += ["", "## Properties", "", "`<value>.<property>`; `.$name` reads an entity variable.", "", "| On | Property | Type | Meaning |", "|---|---|---|---|"]
    L += [f"| {p['on']} | `.{p['name']}` | {p['returns']} | {_md(p['description'])} |" for p in schema["properties"]]
    if schema.get("components"):
        L += ["", "## Fields of each block", "", "What each JSON block accepts (a field not listed draws an \"unknown field\" warning). "
              "`key[]` is a list of that block, `key{}` a map of them.", ""]
        for name, c in schema["components"].items():
            kids = ", ".join(f"`{k}` → {v}" for k, v in (c.get("children") or {}).items())
            L.append(f"- **{name}**: " + ", ".join(f"`{f}`" for f in c.get("fields") or []) + (f" (blocks: {kids})" if kids else ""))
    L += ["", "## Stats", "", "For `setStat` and status `mods` (`{\"mult\"}` | `{\"add\"}` | `{\"value\"}`; damageTaken/damageMult mods may add "
          "`from`, `types`, `chance`, `when`).", ""]
    L += [f"- `{x['name']}`: {_md(x['description'])}" for x in schema["stats"]]
    L += ["", "## Status flags", ""] + [f"- `{x['name']}`: {_md(x['description'])}" for x in schema["flags"]]
    L += ["", "## Filter terms", "", "Combine with `and`, `or`, `not` and parentheses.", ""]
    L += [f"- `{x['name']}`: {_md(x['description'])}" for x in schema["filterTerms"]]
    L += ["", "Game tags: " + ", ".join(f"`{t}`" for t in schema.get("tags") or []), ""]
    L += ["## Built-in signal inputs", "", "Every entity takes these signals without a listener rule:", ""]
    for k, v in (schema.get("inputs") or {}).items():
        L.append(f"- {'every entity' if k == 'any' else 'with a ' + k}: " + ", ".join(f"`{x}`" for x in v))
    L += ["- a mover's named poses: the pose's name moves it there", "", "## Caps", "",
          "Per level in `logic.caps`; above the default draws a \"may lag\" warning, above the maximum an error.", "",
          "| Cap | Default | Max |", "|---|---|---|"]
    L += [f"| `{k}` | {v['default']} | {v['max']} |" for k, v in (schema.get("caps") or {}).items()]
    if schema.get("gameRules"):
        L += ["", "## Game rules", "", "Per level in `logic.game` (op `{\"op\": \"logic\", \"game\": {...}}`): the game's own rules. Each is a "
              "literal or an expression the level reads again every frame (an enum takes its word or `\"=expr\"`), so a rule can switch "
              "it through a variable. Leave a rule out for vanilla.", "", "| Rule | Type | Vanilla | Meaning |", "|---|---|---|---|"]
        for g in schema["gameRules"]:
            t = g["type"] + (f" ({' / '.join(g['options'])})" if g.get("options") else "")
            L.append(f"| `{g['name']}` | {t} | `{json.dumps(g.get('default'))}` | {_md(g['description'])} |")
    L += ["", "## Built-in presets", "", "Use one in a rules list as `{\"preset\": \"<id>\", \"with\": {params}}`, or place a logic object with "
          "`{\"op\": \"place\", \"preset\": \"<id>\", \"with\": {...}}`.", ""]
    for pr in schema.get("presets") or []:
        L.append(f"**`{pr['id']}`** ({pr.get('category', '')}{', places a ' + pr['kind'] if pr.get('kind') else ''}): {_md(pr.get('description'))}")
        L.append("")
        for k, v in (pr.get("params") or {}).items():
            d = f"; default {json.dumps(v['default'])}" if "default" in v else "; required"
            L.append(f"- `{k}` ({v.get('type', 'number')}{d}): {_md(v.get('description'))}")
        L.append("")
    return "\n".join(L).rstrip() + "\n"


def _fixture_into_library(env, fx):
    """Copies a fixture's pack (its level, units, entities, statuses, presets) into the library; returns the level id."""
    ref = LibraryRef(env.library)
    data = load_library(ref)
    pk = migrate_pack(copy.deepcopy(fx["pack"]))
    for kind in ("units", "entities", "statuses", "presets", "waveSets"):
        have = data.setdefault(kind, [])
        for item in pk.get(kind) or []:
            have[:] = [x for x in have if x.get("id") != item.get("id")]
            have.append(item)
    lv = pk["levels"][0]
    data["levels"] = [x for x in data["levels"] if x.get("id") != lv["id"]] + [lv]
    save_library(ref, data)
    return lv["id"]


def _truthy(text):
    t = text.strip().lower()
    try:
        v = json.loads(t)
        if isinstance(v, dict):
            v = v.get("value")
        return bool(v) and v not in ("false", "0")
    except ValueError:
        return t in ("true", "1", "yes")


def run_fixture(env, path, timeout=180):
    """Runs one tests/logic-examples fixture in the game: imports its pack into the library, plays its level (fast
    mode), runs its play steps and reports each. Steps: {"cmd": dev command}, {"do": [actions]} (run as the level
    script), {"wait": game seconds}, {"expect": "<bool expression>", "within": s}, {"raise": event, "on": selector,
    "other": selector, "name": event.name}, {"expectLog": text, "within": s}, {"expectState": "AfterMatchVictory", "within": s}
    (the game state `status` reports)."""
    fx = json.loads(path.read_text())
    g = Game(env)
    level = _fixture_into_library(env, fx)
    mark = g.log_size()
    g.agent(f"play {LIBRARY_ID} {level}")
    # The previous fixture's level may still be up for a moment: wait for this one.
    g.wait(lambda st: not st.get("loading") and st.get("inLevel") and (st.get("session") or {}).get("level") == level,
           timeout, f"the level {level} to load")
    g.wait_level_day(timeout=timeout)
    time.sleep(2.5)
    try:
        g.agent("fast on")
    except Fail:
        pass

    def game_time():
        try:
            return float(g.raw("logic eval time", timeout=30).strip())
        except (ValueError, Fail):
            return None

    results, ok = [], True
    for i, step in enumerate(fx.get("play", {}).get("steps") or []):
        r = {"step": i, **{k: v for k, v in step.items()}}
        try:
            if "cmd" in step:
                r["result"] = g.raw(step["cmd"], timeout=timeout).strip()[:300]
            elif "do" in step:
                r["result"] = g.raw("logic do " + json.dumps(step["do"]), timeout=60).strip()[:300]
            elif "raise" in step:
                parts = [step["raise"], step.get("on", "level"), step.get("other") or "-", step.get("name") or "-"] + \
                    ([str(step["value"])] if step.get("value") is not None else [])
                r["result"] = g.raw("logic raise " + " ".join(parts), timeout=60).strip()[:300]
            elif "wait" in step:
                t0 = game_time()
                end = time.time() + max(5.0, float(step["wait"]) * 2 + 5)
                while time.time() < end:
                    t = game_time()
                    if t0 is None or t is None or t - t0 >= float(step["wait"]):
                        break
                    time.sleep(0.2)
                if t0 is None:
                    time.sleep(float(step["wait"]))
            elif "expect" in step or "expectLog" in step or "expectState" in step:
                within = float(step.get("within", 5))
                t0 = game_time()
                wall_end = time.time() + within * 2 + 20
                passed, last = False, None
                while True:
                    if "expect" in step:
                        last = g.raw("logic eval " + step["expect"], timeout=30).strip()
                        passed = _truthy(last)
                    elif "expectState" in step:
                        last = g.status().get("state")
                        passed = last == step["expectState"]
                    else:
                        last = None
                        passed = any(step["expectLog"] in (l.get("message") if isinstance(l, dict) else str(l)) for l in g.log_since(mark))
                    t = game_time()
                    if passed or time.time() > wall_end or (t0 is not None and t is not None and t - t0 > within):
                        break
                    time.sleep(0.3)
                r["passed"] = passed
                if last is not None:
                    r["value"] = last[:300]
                if not passed:
                    ok = False
                    r["error"] = f"not true within {within:g} s"
        except Fail as e:
            r["error"] = str(e)
            ok = False
        results.append(r)
        if not ok:
            break
    errors = g.log_since(mark, min_level="error")
    try:
        trace = g.raw("logic trace 30", timeout=30).strip().splitlines()
    except Fail as e:
        trace = [str(e)]
    # ok is the steps' verdict; log errors (often the game's own, e.g. on a scene change) are listed for a look.
    return {"fixture": path.name, "example": fx.get("example"), "title": fx.get("title"), "ok": ok,
            "steps": results, "logErrors": errors[:10], "trace": trace, "status": g.status()}


def cmd_edit(env, args):
    """Opens the level in the in-game Studio (for the player to look at or edit by hand)."""
    g = Game(env)
    _, data = load_pack(env, args.pack)
    find_level(data, args.level)
    g.agent(f"edit {data['id']} {args.level}")
    g.wait(lambda s: not s.get("loading") and s.get("studio") == "Edit", args.timeout, "the Studio to open")
    out({"studio": "open", "note": "the Studio edits its own copy; Ctrl+S there writes pack.json. Don't apply ops while "
                                   "the player has unsaved Studio changes."})


def cmd_sandbox(env, args):
    """Studio test play: unlimited gold (toggle with --no-infinite), the king at (x, z) if given."""
    g = Game(env)
    _, data = load_pack(env, args.pack)
    find_level(data, args.level)
    v = g.agent(f"validate {data['id']} {args.level}")
    if not v.get("ok"):
        out(v)
        raise Fail("the game's validator found errors (above)")
    mark = g.log_size()
    g.agent(f"sandbox {data['id']} {args.level}" + (f" {args.at[0]} {args.at[1]}" if args.at else ""))
    g.wait_level_day(timeout=args.timeout)
    time.sleep(2.5)
    if args.no_infinite:
        g.agent("infinitegold off")
    result = {}
    if args.at:
        # The king is dropped a few frames after the level loads; confirm he's there.
        x, z = args.at
        near = lambda s: (s.get("king") or {}).get("pos") and math.hypot(s["king"]["pos"][0] - x, s["king"]["pos"][-1] - z) < 3
        try:
            g.wait(lambda s: s.get("sandbox") and near(s), 10, f"the king at ({x:g}, {z:g})")
            result["at"] = "ok"
        except Fail:
            result["at"] = "the king isn't at --at (fell off the map or the spot is outside the level?); see status.king.pos"
    out({**result, "status": g.status(), "loadWarnings": g.log_since(mark, features=["Builder"], min_level="warn")})


def cmd_enemy(env, args):
    """A library enemy's numbers (offline), or the in-game Enemy Studio: open it on the enemy, fight it in the arena,
    take its portrait."""
    ref = Ref(env)
    lib = LibraryRef(env.library)
    data = load_library(lib)
    u = next((x for x in data.get("units") or [] if x.get("id") == args.id), None)
    if not u:
        raise Fail(f"no enemy {args.id!r} in the library (`palace.py list`; make one with an `unit` op)")
    result = {"card": enemy_card(ref, u, args.difficulty, args.elite),
              "problems": [{"path": p, "message": m} for p, m in check_unit(ref, u)]}
    if args.studio or args.fight is not None or args.portrait:
        g = Game(env)
        g.raw("builder-enemy library " + args.id)
        deadline = time.time() + args.timeout
        while time.time() < deadline and not g.raw("builder-enemy state").startswith("enemy studio"):
            time.sleep(0.5)
        state = g.raw("builder-enemy state")
        if not state.startswith("enemy studio"):
            raise Fail("the Enemy Studio didn't open: " + state)
        result["studio"] = state
        if args.fight is not None:
            g.raw(f"builder-enemy fight {max(1, args.fight)}")
            end = time.time() + 100
            while time.time() < end and g.raw("builder-enemy readout").startswith("running"):
                time.sleep(1)
            result["fight"] = g.raw("builder-enemy readout")
        if args.portrait:
            result["portrait"] = g.raw("builder-enemy portrait")
    out(result)


def coords(values, n, usage):
    """Numbers from command-line words: "x,z" pairs, single numbers, or a mix ("-42,10 5,6", "-42 10 5 6")."""
    parts = [p for v in values or [] for p in str(v).strip().replace(",", " ").split()]
    try:
        nums = [float(p) for p in parts]
    except ValueError:
        nums = None
    if nums is None or len(nums) != n:
        raise Fail(f"{usage}: expected {n} numbers, got {' '.join(str(v).strip() for v in values or []) or 'none'!r} (e.g. -42,10; negative numbers are fine)")
    return nums


def cmd_spawn(env, args):
    g = Game(env)
    u = Ref(env).unit(args.unit) if not args.unit.startswith("vanilla:") else None
    unit = "vanilla:" + u["id"] if u else args.unit
    at = coords(args.at, 2, "spawn ... --at X,Z") if args.at else None
    where = f" line {args.line}" if args.line else (f" at {at[0]:g} {at[1]:g}" if at else "")
    out(g.agent(f"spawn {unit.replace(' ', '_')} {args.count}" + (" elite" if args.elite else "") + where))


def cmd_cheat(env, args):
    g = Game(env)
    value = args.value[0] if args.value else None
    if args.what in ("god", "invulnerable", "infinitegold"):
        out(g.agent(f"{args.what} {value or 'on'}"))
    elif args.what in ("gold", "speed"):
        out(g.agent(f"{args.what} {value}"))
    elif args.what in ("kill", "heal", "endnight"):
        out(g.agent(args.what))
    elif args.what == "teleport":
        x, z = coords(args.value, 2, "cheat teleport X,Z")
        out(g.agent(f"teleport {x:g} {z:g}"))
    else:
        raise Fail("cheat god|invulnerable|infinitegold [on|off] | gold <n> | speed <x> | kill | heal | endnight | teleport <x,z>")


def cmd_build(env, args):
    g = Game(env)
    r = g.agent(f"build {args.slot.replace(' ', '_')}" + (f" {args.level}" if args.level is not None else "") + (f" {args.branch}" if args.branch is not None else "") +
                (" pay" if args.pay else ""))
    out(r)
    if r.get("ok") is False:
        sys.exit(1)


def cmd_shot(env, args):
    out({"written": Game(env).screenshot(args.out)})


def cmd_report(env, args):
    out(Game(env).agent("report"))


def cmd_share(env, args):
    """A share code (printed; the clipboard only with --clipboard) or, with --export, a .tfpack file (-o picks the path;
    the default is a new, numbered file in the game's exports folder). Always JSON."""
    g = Game(env)
    _, data = load_pack(env, args.pack)
    if args.export:
        target = Path(args.out).expanduser().resolve() if args.out else None
        if target and target.exists():
            if not args.force:
                raise Fail(f"{target} exists; pass --force to overwrite it")
            target.unlink()
        out(g.agent(f"share export {data['id']}" + (f" {target}" if target else "")))
    else:
        out(g.agent(f"share code {data['id']}" + (" clipboard" if args.clipboard else "")))


def main(argv=None):
    ap = argparse.ArgumentParser(prog="palace.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", help="Thronefall folder (or its BepInEx folder); default: found automatically")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("where", help="paths and bridge status")

    p = sub.add_parser("game", help="talk to the running game: status | cmd <text> | log | slots | menu")
    p.add_argument("what")
    p.add_argument("rest", nargs="*")
    p.add_argument("--timeout", type=float, default=60)
    p.add_argument("--bytes", type=int, default=200000, help="log: how much of the log's end to read")
    p.add_argument("--feature", action="append", help="log: only these [Feature] lines (e.g. Builder)")
    p.add_argument("--level", default="warn", choices=["error", "warn", "info", "debug"])

    p = sub.add_parser("ref", help="reference data: build | maps | slots <map> | buildings | enemies | waves <map> | scenery [map] | render <map> | doc")
    p.add_argument("what")
    p.add_argument("name", nargs="?")
    p.add_argument("--maps", help="build: comma-separated maps (default: all)")
    p.add_argument("--force", action="store_true", help="build: refresh cached maps")
    p.add_argument("--kind", help="slots/buildings: one building kind; scenery: one category")
    p.add_argument("--player", action="store_true", help="enemies: list player units instead")
    p.add_argument("--raw", action="store_true", help="waves: include the spawn lists")
    p.add_argument("-o", "--out")
    p.add_argument("--labels", default="names", choices=["names", "costs", "reqs", "none"])

    p = sub.add_parser("new", help="new <level> --host <map>: a level in the library (without --host: an old-style pack folder)")
    p.add_argument("pack", metavar="level")
    p.add_argument("--host", help="base map scene, e.g. Nordfels")
    p.add_argument("--name")
    p.add_argument("--base", default="map", choices=["map", "cleared", "flat"])
    p.add_argument("--waves", default="empty", choices=["empty", "vanilla", "none"])
    p.add_argument("--author")
    p.add_argument("--description")
    p.add_argument("--force", action="store_true")

    sub.add_parser("list", help="the library's levels and enemies (offline)")

    p = sub.add_parser("export", help="a .tfpack of library levels (+ every enemy and asset they use), through the game")
    p.add_argument("name", help="the pack's name (also its file name)")
    p.add_argument("levels", help="comma-separated level ids")
    p.add_argument("--enemies", default="", help="comma-separated extra enemy ids")

    p = sub.add_parser("level", help="add a level to a pack")
    p.add_argument("pack")
    p.add_argument("level")
    p.add_argument("--host", required=True, help="base map scene, e.g. Nordfels")
    p.add_argument("--name")
    p.add_argument("--base", default="map", choices=["map", "cleared", "flat"])
    p.add_argument("--waves", default="empty", choices=["empty", "vanilla", "none"], help="new wave set: empty, the map's own nights, or none (play vanilla)")
    p.add_argument("--force", action="store_true", help="replace an existing level of that id (and its wave set)")

    p = sub.add_parser("apply", help="apply JSON edit ops (file or - for stdin)")
    p.add_argument("pack")
    p.add_argument("ops")
    p.add_argument("--level", help="default level for ops without \"level\"")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--no-check", action="store_true")
    p.add_argument("--fresh", action="store_true", help="first empty the level (--level, or the pack's only level) back to what "
                                                         "`level` made, keeping its nights, so the same ops file can be applied again")

    p = sub.add_parser("check", help="offline checks (+ --live: the game's validator)")
    p.add_argument("pack")
    p.add_argument("level", nargs="?", help="--live: validate only this level (and its nights)")
    p.add_argument("--live", action="store_true")

    for name, fn_help in (("show", "the level as it will play: slots, requirements, lines"), ("economy", "gold curve of a greedy economy player"),
                          ("waves", "night-by-night enemy strength")):
        p = sub.add_parser(name, help=fn_help)
        p.add_argument("pack")
        p.add_argument("level", nargs="?")
        if name == "economy":
            p.add_argument("--compare", action="store_true", help="also the base map's vanilla curve")
            p.add_argument("--report", help="a saved `test` report (JSON): the income that run actually received, night by "
                                            "night, and the buildings knocked out most often")
        if name == "waves":
            p.add_argument("--compare", nargs="?", const="host", help="also a map's vanilla nights (default: the host)")

    p = sub.add_parser("logic", help="behaviour engine: schema [kind] [name] | check <pack> [level] | fixture <file> | "
                                     "(in the game) status | vars | trace | entities | budget | eval <expr> | do <json> | fire <signal> [to] | raise <event> <on> [other] [name]")
    p.add_argument("what")
    p.add_argument("rest", nargs=argparse.REMAINDER)
    p.add_argument("--json", action="store_true", help="schema: the whole registry")
    p.add_argument("--kit", action="store_true", help="doc: from the kit's logic-schema.json, not the game's")
    p.add_argument("--timeout", type=float, default=180)
    p = sub.add_parser("render", help="top-down picture with slots, requirement arrows, spawn lines and routes")
    p.add_argument("pack")
    p.add_argument("level", nargs="?")
    p.add_argument("-o", "--out")
    p.add_argument("--live", action="store_true", help="the real map from the running game (level must be loaded)")
    p.add_argument("--labels", default="names", choices=["names", "costs", "reqs", "none"])
    p.add_argument("--pixels", type=int, default=1400)
    p.add_argument("--no-routes", action="store_true")

    p = sub.add_parser("paths", help="the game's own path check (level loaded): each ground line's route to the castle")
    p.add_argument("--rescan", action="store_true")
    p.add_argument("--points", action="store_true", help="include each route's waypoints [[x, z], ...]")
    p.add_argument("--between", nargs="+", metavar="X,Z",
                   help="instead: whether each ground graph (small/large enemies, the king's) connects two points: "
                        "X1,Z1 X2,Z2 or X1 Z1 X2 Z2 (negative numbers are fine)")

    p = sub.add_parser("play", help="load the level in the game (a custom run: real gold)")
    p.add_argument("pack")
    p.add_argument("level")
    p.add_argument("--timeout", type=float, default=120)

    p = sub.add_parser("night", help="start the next night (or jump to night N) and wait for its report")
    p.add_argument("night", nargs="?", type=int)
    p.add_argument("--speed", type=float)
    p.add_argument("--timeout", type=float, default=600)
    p.add_argument("--no-wait", action="store_true")

    p = sub.add_parser("test", help="load, buy per plan, play nights, report")
    p.add_argument("pack")
    p.add_argument("level")
    p.add_argument("--nights", help="e.g. 1-3 or 2,4 (default: all)")
    p.add_argument("--plan", help="JSON {\"1\": [\"castle\", \"h1\", {\"slot\":\"t1\",\"level\":2,\"branch\":0}], \"2\": [...]}: buys before each night")
    p.add_argument("--build", choices=["none", "all"], default="none", help="all: also build every available blueprint free each day")
    p.add_argument("--free", action="store_true", help="plan purchases cost nothing (probes defence alone; the economy isn't tested)")
    p.add_argument("--king", metavar="WHERE", help="where the king stands every night: castle | <slot id> (in front of it) | X,Z "
                                                   "(default: where he is; the plan's \"king\" entries change it from a night on)")
    p.add_argument("--carry", action="store_true", help="retry refused plan items at the start of the next day (plan option \"carry\")")
    p.add_argument("--save", action="store_true", help="after an item is refused for gold, buy nothing else that day (plan option \"save\")")
    p.add_argument("--catchup", choices=["play", "free", "none"], default="play",
                   help="starting at night N>1: play nights 1..N-1 with the plan and real income (play, the default; slow but "
                        "faithful), build the earlier days' items free and jump (free), or jump with nothing built (none)")
    p.add_argument("--quiet", action="store_true", help="no per-night progress lines on stderr")
    p.add_argument("--gold", type=int, help="set gold before the first night")
    p.add_argument("--god", action="store_true", help="the king takes no damage")
    p.add_argument("--invulnerable", action="store_true", help="buildings and units take no damage")
    p.add_argument("--speed", type=float, default=3)
    p.add_argument("--fast", action=argparse.BooleanOptionalAction, default=True,
                   help="fast simulation (default on; --no-fast for real time): every frame advances the same fixed "
                        "1/FPS s x --speed, frames run as fast as the CPU allows and aren't drawn, so the result doesn't "
                        "depend on the machine's load")
    p.add_argument("--fps", type=float, default=60, help="--fast: simulation frames per game second at 1x (default 60)")
    p.add_argument("--shots", help="folder for a screenshot of each night")
    p.add_argument("--shot-after", type=float, default=12, help="seconds into the night for the screenshot")
    p.add_argument("--timeout", type=float, default=420, help="seconds per night before giving up")
    p.add_argument("--timeout-load", type=float, default=120)
    p.add_argument("--no-load", action="store_true", help="continue the run already loaded")
    p.add_argument("--no-catchup", action="store_true", help="same as --catchup none")
    p.add_argument("--from-night", type=int, metavar="N", help="start at the dawn before night N from the savestate a "
                   "full test took there (only nights N.. replay); refused if the level, nights 1..N-1's waves or enemies, "
                   "the plan's days 1..N-1 or the test flags changed since")
    p.add_argument("--states", action=argparse.BooleanOptionalAction, default=True,
                   help="save a resume state at each dawn for --from-night (default on)")
    p.add_argument("--force-resume", action="store_true", help="--from-night: use the state even if it looks stale")

    p = sub.add_parser("edit", help="open the level in the in-game Studio")
    p.add_argument("pack")
    p.add_argument("level")
    p.add_argument("--timeout", type=float, default=120)

    p = sub.add_parser("sandbox", help="Studio test play: unlimited gold, start anywhere")
    p.add_argument("pack")
    p.add_argument("level")
    p.add_argument("--at", nargs=2, type=float, metavar=("X", "Z"), help="king's start")
    p.add_argument("--no-infinite", action="store_true", help="normal gold")
    p.add_argument("--timeout", type=float, default=120)

    p = sub.add_parser("enemy", help="a library enemy's numbers (offline); --studio/--fight N/--portrait: the in-game Enemy Studio")
    p.add_argument("id")
    p.add_argument("--difficulty", type=float, default=1.0, help="a night's difficulty for the numbers (default 1)")
    p.add_argument("--elite", action="store_true")
    p.add_argument("--studio", action="store_true", help="open it in the game's Enemy Studio (an arena on Nordfels)")
    p.add_argument("--fight", type=int, metavar="N", help="fight N copies against the default defenders and report")
    p.add_argument("--portrait", action="store_true", help="take its library portrait (library/enemies/<id>.png)")
    p.add_argument("--timeout", type=float, default=60)

    p = sub.add_parser("spawn", help="spawn enemies now (they attack like a wave's)")
    p.add_argument("unit", help="vanilla id (E Melee) or a pack unit id")
    p.add_argument("count", nargs="?", type=int, default=1)
    p.add_argument("--elite", action="store_true")
    p.add_argument("--line", help="at a spawn line")
    p.add_argument("--at", nargs="+", metavar="X,Z", help="X,Z or X Z (negative numbers are fine)")

    p = sub.add_parser("cheat", help="god|invulnerable|infinitegold [on|off] | gold <n> | speed <x> | kill | heal | endnight | teleport <x,z>")
    p.add_argument("what")
    p.add_argument("value", nargs="*", help="teleport: X,Z or X Z (negative numbers are fine)")

    p = sub.add_parser("build", help="build/upgrade a slot in the running level")
    p.add_argument("slot")
    p.add_argument("level", nargs="?", type=int)
    p.add_argument("branch", nargs="?", type=int)
    p.add_argument("--pay", action="store_true")

    p = sub.add_parser("shot", help="screenshot of the game window")
    p.add_argument("-o", "--out", required=True)

    sub.add_parser("report", help="the current/last night's report")

    p = sub.add_parser("share", help="share code (or --export a .tfpack) through the game")
    p.add_argument("pack")
    p.add_argument("--export", action="store_true", help="write a .tfpack file (JSON result: its exact path)")
    p.add_argument("-o", "--out", help="export: the file to write (default: a new numbered file in the game's exports folder)")
    p.add_argument("--force", action="store_true", help="export: overwrite --out")
    p.add_argument("--clipboard", action="store_true", help="code: also copy it to the system clipboard")

    argv = list(sys.argv[1:] if argv is None else argv)
    argv = [" " + a if re.match(r"^-\d[\d.]*,\s*-?\d", a) else a for a in argv]
    args = ap.parse_args(argv)
    env = Env(args.game)
    if len(env.found) > 1 and env.source not in ("--game", "PALACE_GAME") and args.cmd != "where":
        print(f"palace.py: using {env.bepinex} ({env.source}); {len(env.found) - 1} other install(s) found, see `where`",
              file=sys.stderr)
    fn = globals()["cmd_" + args.cmd]
    try:
        fn(env, args)
    except Fail as e:
        print(json.dumps({"error": str(e)}), file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main()
