#!/usr/bin/env python3
"""palace.py: build, check, picture and test PalaceMod Builder levels for Thronefall from the command line.

Made for coding agents. Python 3.8+, standard library only, any OS. Read level-building.md (next to this file) first.

    python3 palace.py where                        # find the game, packs folder and bridge
    python3 palace.py ref build                    # (game running, Agent bridge on) cache map + unit data
    python3 palace.py new my.pack --name "My Pack"
    python3 palace.py level my.pack gorge --host Nordfels --base cleared --waves empty
    python3 palace.py apply my.pack ops.json       # JSON edit operations (see the guide)
    python3 palace.py check my.pack                # offline checks; --live adds the game's validator
    python3 palace.py render my.pack gorge -o map.png
    python3 palace.py economy my.pack gorge ; python3 palace.py waves my.pack gorge
    python3 palace.py test my.pack gorge --nights 1-3 --plan plan.json --shots shots/

Every command prints JSON (or a text table with --text where offered) and exits non-zero on failure.
"""
import argparse
import base64
import copy
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
PACK_VERSION = 2


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


class Env:
    """Where things are: the game's BepInEx folder, the packs, the kit (this folder) and its reference cache."""

    def __init__(self, game=None):
        self.bepinex = None
        candidates = []
        if game:
            candidates.append(Path(game).expanduser())
        if os.environ.get("PALACE_GAME"):
            candidates.append(Path(os.environ["PALACE_GAME"]).expanduser())
        # The kit lives in <game>/BepInEx/PalaceMod/Agent when the mod wrote it.
        if HERE.parent.name == "PalaceMod" and HERE.parent.parent.name == "BepInEx":
            candidates.append(HERE.parent.parent.parent)
        candidates += steam_roots()
        for c in candidates:
            b = c if c.name == "BepInEx" else c / "BepInEx"
            if b.is_dir():
                self.bepinex = b
                break
        self.kit = HERE
        # The reference cache lives with the game's copy of the kit, so every copy of palace.py shares it.
        game_kit = self.bepinex / "PalaceMod" / "Agent" if self.bepinex else self.kit
        self.ref = Path(os.environ.get("PALACE_REF") or (game_kit / "reference"))

    def need_game(self):
        if not self.bepinex:
            raise Fail("Can't find the game's BepInEx folder. Pass --game <Thronefall folder> or set PALACE_GAME.")
        return self.bepinex

    @property
    def packs(self):
        return self.need_game() / "PalaceBuilder" / "packs"

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

    def status_file(self):
        f = self.env.bridge / "status.json"
        if not f.exists():
            return None
        try:
            s = json.loads(f.read_text())
        except (ValueError, OSError):
            return None
        s["age"] = round(time.time() - s.get("time", 0), 1)
        return s

    def require(self):
        s = self.status_file()
        if s is None:
            raise Fail("The Agent bridge isn't on. In the game: Settings > Mods > Builder > Agent bridge > Enabled (it's off by "
                       "default), with the game running. Then retry.")
        if s["age"] > 15:
            raise Fail(f"The Agent bridge last answered {s['age']} s ago: is the game running (and not frozen)?")
        return s

    def raw(self, text, timeout=60):
        """Sends one command line; returns its text result. Raises Fail with the game's message on an error."""
        self.require()
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
        raise Fail(f"The game didn't answer `{text}` within {timeout} s (loading? check `palace.py game log`).")

    LOADS = ("play ", "sandbox ", "edit ", "vanilla ", "menu")

    def agent(self, text, timeout=60):
        if text.startswith(self.LOADS):
            # A scene change can't start while another is running (e.g. right after `menu`).
            deadline = time.time() + 60
            while time.time() < deadline and json.loads(self.raw("agent status", timeout)).get("loading"):
                time.sleep(0.5)
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
                if "isn't on" in str(e):
                    raise
            time.sleep(poll)
        raise Fail(f"Timed out after {timeout} s waiting for {what}; last status: {json.dumps(last)}")

    def wait_level_day(self, timeout=90):
        time.sleep(1.0)
        return self.wait(lambda s: s.get("inLevel") and not s.get("loading") and s.get("time") == "day", timeout, "the level to load")

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


def pack_path(env, pack):
    p = Path(pack)
    if p.suffix == ".json" and p.exists():
        return p
    if (p / "pack.json").exists():
        return p / "pack.json"
    if not ID_RE.match(pack):
        raise Fail(f"{pack!r} isn't a pack id (1-64 of a-z 0-9 . _ -) or a pack.json path")
    return env.packs / pack / "pack.json"


def load_pack(env, pack):
    path = pack_path(env, pack)
    if not path.exists():
        raise Fail(f"No pack at {path}. Create it with `palace.py new {pack}`.")
    return path, json.loads(path.read_text(encoding="utf-8"))


def save_pack(path, data):
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
    __slots__ = ("key", "name", "kind", "pos", "rot", "castle", "upgrades", "req", "cap", "follows", "added", "base")

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
                                added=False, base=None)
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
        for i, u in enumerate(e.get("upgrades") or []):
            if u and u.get("cost") is not None and i < len(slot.upgrades):
                slot.upgrades[i]["cost"] = u["cost"]

    for e in bs.get("edit") or []:
        s = slots.get(e.get("ref"))
        if s is None:
            warnings.append(f"edit of {e.get('ref')!r}: no such slot on the level")
            continue
        apply_common(s, e)
    for a in bs.get("add") or []:
        base = (a.get("base") or "")[6:] if (a.get("base") or "").startswith("scene:") else None
        src = host.get(base)
        if src is None:
            warnings.append(f"add {a.get('id')!r}: base {a.get('base')!r} isn't a slot of {level['host']}")
            continue
        key = "add:" + a["id"]
        s = Slot(key=key, name="PalaceBuilder " + a["id"], kind=src["kind"], pos=a.get("pos") or src["pos"], rot=a.get("rotY", 0), castle=False,
                 upgrades=playable(src["upgrades"]), req=host_req(src), cap=src.get("upgradeCap"), follows=False, added=True, base=base)
        if is_empty_base(level) and s.req and s.req[0] != castle and "requires" not in a:
            s.req = ("__removed__", 0)
        apply_common(s, a)
        slots[key] = s
    # Resolve requirement refs to keys.
    for s in slots.values():
        if s.req and s.req[0] == "castle":
            s.req = (castle, s.req[1])
    return slots, castle, warnings


def step_income(step):
    br = step.get("branches") or [{}]
    return max((b.get("income", 0) for b in br), default=0)


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

    def level(self, op):
        # "level" names the level; the water op's numeric "level" is its water height, not a level id.
        lid = op.get("level") if isinstance(op.get("level"), str) else self.level_id
        return find_level(self.data, lid)

    def ground_y(self, level, x, z):
        t = level.get("terrain")
        if t and level.get("base") == "flat":
            y = Terrain(t).height_at(x, z)
            if y is not None:
                return round(y, 2)
        g = self.ref.map(level["host"]).get("grid")
        if g:
            y = Grid(g).height(x, z)
            if y is not None:
                return round(y, 2)
        castle = next((s for s in self.ref.map(level["host"])["slots"] if s.get("castle")), None)
        return castle["pos"][1] if castle else 0.0

    def pos(self, level, op):
        if op.get("pos"):
            p = op["pos"]
            if len(p) != 3:
                raise Fail("pos is [x, y, z]")
            return [round(float(v), 2) for v in p]
        if op.get("at"):
            x, z = op["at"]
            return [round(float(x), 2), self.ground_y(level, x, z), round(float(z), 2)]
        raise Fail(f"{op.get('op')}: give \"at\": [x, z] (height from the ground) or \"pos\": [x, y, z]")

    def run(self, ops):
        for i, op in enumerate(ops):
            name = op.get("op")
            fn = getattr(self, "op_" + str(name), None)
            if fn is None:
                raise Fail(f"ops[{i}]: unknown op {name!r}; ops: {', '.join(sorted(n[3:] for n in dir(self) if n.startswith('op_')))}")
            try:
                r = fn(op)
            except Fail as e:
                raise Fail(f"ops[{i}] ({name}): {e}")
            except (KeyError, TypeError, ValueError, IndexError) as e:
                raise Fail(f"ops[{i}] ({name}): bad or missing field: {e!r}")
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
        """{"op":"set","path":"waveSets[0].startingGold","value":30}: raw edit of any field."""
        target, key = self.walk(op["path"], create=True)
        if op.get("delete"):
            if isinstance(target, list):
                target.pop(key)
            else:
                target.pop(key, None)
        else:
            target[key] = op["value"]
        return f"set {op['path']}"

    def walk(self, path, create=False):
        parts = re.findall(r"[^.\[\]]+|\[\d+\]", path)
        cur = self.data
        for idx, p in enumerate(parts):
            last = idx == len(parts) - 1
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
        """Sets level fields: name, description, base, waveSet, host."""
        lv = self.level(op)
        for k in ("name", "description", "waveSet", "host"):
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
        match = [s for s in m["slots"] if s.get("canBeBase") and (s["kind"].lower() == k or (s.get("display") or "").lower() == k)]
        if not match:
            kinds = sorted({s["kind"] for s in m["slots"] if s.get("canBeBase")})
            raise Fail(f"no slot of kind {kind!r} on {level['host']} to copy; kinds there: {kinds}. "
                       f"Use \"from\": \"<slot name>\" for a specific one (`palace.py ref slots {level['host']}`).")
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
            raise Fail(f"slot {sid!r} already exists (use move_slot / set_slot)")
        src = self.ref.map(lv["host"])
        if op.get("from"):
            base = next((s for s in src["slots"] if s["name"] == op["from"]), None)
            if base is None:
                raise Fail(f"no slot {op['from']!r} on {lv['host']}")
            if not base.get("canBeBase"):
                raise Fail(f"{op['from']!r} can't be copied (the castle, a wall piece its gate upgrades, or a slot with slots inside)")
        else:
            base = self.host_slot_for(lv, op["kind"])
        entry = {"id": sid, "base": "scene:" + base["name"], "pos": self.pos(lv, op), "rotY": float(op.get("rotY", 0))}
        req = op.get("requires", "castle" if is_empty_base(lv) else None)
        if req is not None:
            entry["requires"] = parse_req(req, lv, None)
        if op.get("upgradeCap") is not None:
            entry["upgradeCap"] = int(op["upgradeCap"])
        if op.get("costs"):
            entry["upgrades"] = [{"cost": int(c)} if c is not None else None for c in op["costs"]]
        adds.append(entry)
        return f"added {sid} ({base['kind']} from {base['name']})"

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
        return f"chained {len(keys)} slots ({mode})"

    # ---- props -------------------------------------------------------------------------------------------------------

    def prop_base(self, lv, mesh):
        if mesh.startswith("mesh:"):
            return mesh
        return "mesh:" + mesh

    def op_add_prop(self, op):
        """{"op":"add_prop","id":"rock1","mesh":"Rock A","at":[x,z],"rotY":0,"scale":1,"solid":true}"""
        lv = self.level(op)
        props = ensure(lv, "props", [])
        pid = op.get("id") or self.new_prop_id(props, op["mesh"])
        if any(p.get("id") == pid for p in props):
            raise Fail(f"prop {pid!r} exists")
        props.append({"id": pid, "base": self.prop_base(lv, op["mesh"]), "pos": self.pos(lv, op), "rotY": float(op.get("rotY", 0)),
                      "scale": float(op.get("scale", 1)), "solid": bool(op.get("solid", True))})
        return f"prop {pid}"

    @staticmethod
    def new_prop_id(props, mesh):
        stem = re.sub(r"[^a-z0-9]+", "-", mesh.lower().replace("mesh:", "")).strip("-")[:40] or "prop"
        used = {p.get("id") for p in props}
        n = 1
        while f"{stem}-{n}" in used:
            n += 1
        return f"{stem}-{n}"

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
            mesh = rng.choice(meshes)
            props.append({"id": self.new_prop_id(props, mesh), "base": self.prop_base(lv, mesh), "pos": [round(x, 2), self.ground_y(lv, x, z), round(z, 2)],
                          "rotY": round(rng.uniform(0, 360), 1), "scale": round(rng.uniform(smin, smax), 2), "solid": bool(op.get("solid", True))})
            placed.append((x, z))
            added += 1
        return f"scattered {added} props"

    def lines(self, lv):
        out_ = []
        for l in self.ref.map(lv["host"])["spawnLines"]:
            out_.append({"name": l["name"], "center": l["center"]})
        for a in ((lv.get("spawnLines") or {}).get("add") or []):
            out_.append({"name": a["id"], "center": a["pos"]})
        return out_

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
            raise Fail(f"spawn line {lid!r} exists")
        adds.append({"id": lid, "pos": self.pos(lv, op), "rotY": float(op.get("rotY", 0)), "width": float(op.get("width", 10)),
                     "ground": bool(op.get("ground", True)), "large": bool(op.get("large", True)), "flying": bool(op.get("flying", True))})
        return f"spawn line {lid}"

    def op_move_spawn_line(self, op):
        """Moves an added line (by id) or the host's own (by name): "at"/"pos" its middle, "rotY" turns it."""
        lv = self.level(op)
        sl = ensure(lv, "spawnLines", {})
        for a in sl.get("add") or []:
            if a.get("id") == op["line"]:
                if "at" in op or "pos" in op:
                    a["pos"] = self.pos(lv, op)
                for k in ("rotY", "width", "ground", "large", "flying"):
                    if k in op:
                        a[k] = op[k]
                return f"moved {op['line']}"
        if not any(l["name"] == op["line"] for l in self.ref.map(lv["host"])["spawnLines"]):
            raise Fail(f"no spawn line {op['line']!r}")
        edits = ensure(sl, "edit", [])
        e = next((x for x in edits if x.get("ref") == op["line"]), None)
        if e is None:
            e = {"ref": op["line"]}
            edits.append(e)
        if "at" in op or "pos" in op:
            e["pos"] = self.pos(lv, op)
        if "rotY" in op:
            e["rotY"] = float(op["rotY"])
        return f"moved host line {op['line']}"

    def op_remove_spawn_line(self, op):
        lv = self.level(op)
        sl = lv.get("spawnLines") or {}
        before = len(sl.get("add") or [])
        sl["add"] = [a for a in sl.get("add") or [] if a.get("id") != op["line"]]
        if len(sl["add"]) == before:
            raise Fail("only added spawn lines can be removed (move a host line out of the way instead, or don't spawn from it)")
        return f"removed {op['line']}"

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
        return f"unit {u['id']}"

    # ---- terrain -----------------------------------------------------------------------------------------------------

    def terrain(self, op):
        lv = self.level(op)
        if not lv.get("terrain"):
            raise Fail("no terrain grid yet: start with {\"op\":\"terrain_init\"}")
        return lv, Terrain(lv["terrain"])

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
                         "y": float(op.get("y", castle["pos"][1] if castle else 0))}
        if op.get("flat", True):
            lv["base"] = "flat"
        return f"terrain {size[0]}x{size[1]} cells"

    def op_terrain(self, op):
        """Height edits on a shape (circle/rect/line/polygon, with "falloff" metres):
        {"op":"terrain","mode":"set"|"raise"|"lower"|"smooth","height":4,"circle":{"center":[x,z],"radius":10},"falloff":4}
        Heights are metres relative to terrain.y (the castle's height by default)."""
        lv, t = self.terrain(op)
        n = t.edit_heights(Shape.from_op(op), op.get("mode", "set"), float(op.get("height", 0)), float(op.get("falloff", 0)))
        t.store(lv)
        return f"terrain: {n} corners"

    def op_ramp(self, op):
        """{"op":"ramp","from":[x,z],"to":[x,z],"width":6,"fromHeight":0,"toHeight":5,"falloff":2}: a straight slope."""
        lv, t = self.terrain(op)
        n = t.ramp(op["from"], op["to"], float(op.get("width", 6)), float(op["fromHeight"]), float(op["toHeight"]), float(op.get("falloff", 2)))
        t.store(lv)
        return f"ramp: {n} corners"

    def op_paint(self, op):
        """{"op":"paint","material":"road"|"ground"|"high"|"low"|"earth"|"sand"|"rock", shape...}"""
        lv, t = self.terrain(op)
        mat = Terrain.MATERIALS.index(op["material"]) if op["material"] in Terrain.MATERIALS else int(op["material"])
        n = t.paint_cells(Shape.from_op(op), "materials", mat)
        t.store(lv)
        return f"painted {n} cells"

    def op_block(self, op):
        """{"op":"block","on":true, shape...}: cells ground units (and the king) can't enter; fliers can. Any base."""
        lv = self.level(op)
        if not lv.get("terrain"):
            self.op_terrain_init({"level": lv["id"], "flat": False})
        t = Terrain(lv["terrain"])
        n = t.paint_cells(Shape.from_op(op), "blocked", 1 if op.get("on", True) else 0)
        t.store(lv)
        return f"blocked {n} cells"

    def op_water(self, op):
        """{"op":"water","level":-1,"on":true, shape...}: water cells (unwalkable); the ground under them is dug to level-1."""
        lv, t = self.terrain(op)
        if "level" in op:
            t.water_level = float(op["level"])
        on = op.get("on", True)
        shape = Shape.from_op(op)
        n = t.paint_cells(shape, "water", 1 if on else 0)
        if on and op.get("dig", True):
            t.dig_water()
        t.store(lv)
        return f"water {n} cells"


# =====================================================================================================================
# Shapes and terrain
# =====================================================================================================================

class Shape:
    def __init__(self, kind, spec, falloff=0.0):
        self.kind, self.spec, self.falloff = kind, spec, falloff

    @staticmethod
    def from_op(op):
        f = float(op.get("falloff", 0))
        for k in ("circle", "rect", "line", "polygon"):
            if k in op:
                return Shape(k, op[k], f)
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
    """LevelDef.terrain: (W+1)x(H+1) corner heights (cm relative to y, delta-coded int16), W*H cell materials,
    blocked and water bitmasks; raw deflate + base64. See the guide's Terrain section."""
    MATERIALS = ["ground", "high", "low", "earth", "sand", "road", "rock"]
    MIN_Y, MAX_Y = -8.0, 30.0

    def __init__(self, t):
        self.ox, self.oz = t["origin"]
        self.cell = float(t["cell"])
        self.w, self.h = t["size"]
        self.y = float(t.get("y", 0))
        nc = (self.w + 1) * (self.h + 1)
        self.hts = [0] * nc
        if t.get("heights"):
            raw = b64inflate(t["heights"], nc * 2)
            prev = 0
            for i, (d,) in enumerate(struct.iter_unpack("<h", raw)):
                v = ((prev + d + 32768) % 65536) - 32768
                self.hts[i] = v
                prev = v
        n = self.w * self.h
        self.materials = bytearray(b64inflate(t["materials"], n)) if t.get("materials") else bytearray(n)
        self.blocked = self.unbits(t.get("blocked"), n)
        w = t.get("water") or {}
        self.water_level = float(w.get("level", -1.0))
        self.water = self.unbits(w.get("cells"), n)

    @staticmethod
    def unbits(s, n):
        if not s:
            return bytearray(n)
        raw = b64inflate(s, (n + 7) // 8)
        return bytearray((raw[i >> 3] >> (i & 7)) & 1 for i in range(n))

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
        world = max(self.MIN_Y, min(self.MAX_Y, self.y + rel))
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

    def slope_deg(self, i, j):
        W = self.w + 1
        h = [self.hts[(j + dj) * W + i + di] / 100.0 for di, dj in ((0, 0), (1, 0), (0, 1), (1, 1))]
        gx = ((h[1] - h[0]) + (h[3] - h[2])) / (2 * self.cell)
        gz = ((h[2] - h[0]) + (h[3] - h[1])) / (2 * self.cell)
        return math.degrees(math.atan(math.hypot(gx, gz)))

    def store(self, lv):
        t = lv["terrain"]
        t["origin"], t["cell"], t["size"], t["y"] = [self.ox, self.oz], self.cell, [self.w, self.h], self.y
        if any(self.hts):
            out_ = bytearray()
            prev = 0
            for v in self.hts:
                d = ((v - prev + 32768) % 65536) - 32768
                out_ += struct.pack("<h", d)
                prev = v
            t["heights"] = b64deflate(out_)
        else:
            t.pop("heights", None)
        if any(self.materials):
            t["materials"] = b64deflate(self.materials)
        else:
            t.pop("materials", None)
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

SLOPE_LIMIT = 32.0


def prop_radius(ref, level, p):
    """Half the footprint of a placed prop, metres: the host map's own use of the mesh if it has one (map dump,
    footprint x/z), else the scenery library's (mesh size x/y before it's stood upright), else 1.5."""
    name = p["base"][5:].split("@")[0]
    size = None
    for s in ref.map(level["host"]).get("scenery", []):
        if s["base"] == "mesh:" + name and s.get("size"):
            size = (s["size"][0], s["size"][2])
    if size is None:
        for m_ in ref.scenery.get("meshes", []):
            if m_["name"] == name and m_.get("size"):
                size = (m_["size"][0], m_["size"][1])
    return (max(size) / 2 if size else 1.5) * float(p.get("scale", 1))


class LevelGround:
    """The level's walkable ground as a grid: the map's sampled ground, or the flat terrain; minus blocked/water cells,
    solid props. Approximate: the game's navmesh is the truth (`palace.py paths` asks the game)."""

    def __init__(self, ref, level, slots):
        m = ref.map(level["host"])
        t = level.get("terrain")
        self.flat = level.get("base") == "flat"
        if self.flat and t:
            T = Terrain(t)
            self.ox, self.oz, self.cell, self.w, self.h = T.ox, T.oz, T.cell, T.w, T.h
            self.kind = []
            self.height = []
            for j in range(T.h):
                row = []
                for i in range(T.w):
                    k = j * T.w + i
                    if T.water[k] or T.blocked[k]:
                        row.append(".")
                    elif T.slope_deg(i, j) > SLOPE_LIMIT:
                        row.append(".")
                    else:
                        row.append("w")
                    self.height.append(T.height_at(T.ox + (i + 0.5) * T.cell, T.oz + (j + 0.5) * T.cell))
                self.kind.append(row)
            # Large enemies need room: within ~4 m of a cliff, water or a blocked cell only small ones fit (measured:
            # an 8 m ramp between cliffs is closed to the large graph, a 14 m one is open).
            n = max(1, int(round(4.0 / T.cell)))
            edge = [[k == "." for k in r] for r in self.kind]
            for j in range(T.h):
                for i in range(T.w):
                    if self.kind[j][i] != "w":
                        continue
                    if any(edge[jj][ii] for jj in range(max(0, j - n), min(T.h, j + n + 1))
                           for ii in range(max(0, i - n), min(T.w, i + n + 1))):
                        self.kind[j][i] = "s"
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
        # Solid props block, over most of their footprint.
        for p in level.get("props") or []:
            if p.get("solid", True):
                self.stamp(p["pos"][0], p["pos"][2], prop_radius(ref, level, p) * 0.8, ".")
        # Buildings aren't obstacles here: enemies break through walls and buildings in their way.

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
        if k != "s":
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

    def route(self, start, goal_xz, large):
        """A* over 8-neighbour cells. Returns (reachable, length m, polyline [[x,z],...])."""
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
            for di, dj, c in ((1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)):
                ni, nj = ci + di, cj + dj
                if not (0 <= ni < self.w and 0 <= nj < self.h) or not self.ok(ni, nj, large):
                    continue
                if di and dj and not (self.ok(ci + di, cj, large) and self.ok(ci, cj + dj, large)):
                    continue
                nd = dist[cur] + c
                if nd < dist.get((ni, nj), 1e18):
                    dist[(ni, nj)] = nd
                    prev[(ni, nj)] = cur
                    heapq.heappush(pq, (nd + math.hypot(ni - gx, nj - gz), (ni, nj)))
        if g not in dist:
            return False, None, [], "no walkable route"
        pts = []
        cur = g
        while cur in prev or cur == s:
            pts.append([round(self.ox + (cur[0] + 0.5) * self.cell, 1), round(self.oz + (cur[1] + 0.5) * self.cell, 1)])
            if cur == s:
                break
            cur = prev[cur]
        pts.reverse()
        return True, round(dist[g] * self.cell, 1), pts[::3] + [pts[-1]], None


def level_lines(ref, level):
    """Spawn lines of the level: host lines (moved by edits) + added ones. [{name, center, points, ground, large, flying, added}]"""
    m = ref.map(level["host"])
    sl = level.get("spawnLines") or {}
    edits = {e["ref"]: e for e in sl.get("edit") or [] if e.get("ref")}
    lines = []
    for l in m["spawnLines"]:
        c = list(l["center"])
        pts = [list(p) for p in l["points"]]
        e = edits.get(l["name"])
        if e and e.get("pos"):
            dx, dz = e["pos"][0] - c[0], e["pos"][2] - c[2]
            pts = [[p[0] + dx, p[1], p[2] + dz] for p in pts]
            c = list(e["pos"])
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
        r = {"line": l["name"], "reachable": ok, "length": length, "why": why, "points": pts}
        if l["large"]:
            # Large enemies (ogres, rams...) need wider passages; vanilla maps sometimes keep them off a plateau on purpose.
            r["largeReachable"] = ground.route(start, goal, True)[0] if ok else False
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


def wave_summary(ref, data, ws):
    rows = []
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
            count += c
            hp += c * uhp * mult
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
    Gold from enemies arrives at night; buildings pay at dawn. Returns per-day rows. An upper bound on economy:
    real players also spend on defence."""
    level = {k: 0 for k in slots}
    gold = start_gold
    rows = []
    income = 0

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
                inc = step_income(step)
                unlock = 0
                for k2, s2 in slots.items():
                    if s2.req and s2.req[0] == k and s2.req[1] == level[k] and s2.upgrades:
                        unlock += step_income(s2.upgrades[0])
                if k == castle:
                    # A castle level also lifts the upgrade cap of every capped building.
                    for k2, s2 in slots.items():
                        if k2 != castle and s2.cap is not None and s2.cap > -100 and 0 < level[k2] < len(s2.upgrades) \
                                and level[k2] + 1 == level[castle] + 1 + s2.cap:
                            unlock += step_income(s2.upgrades[level[k2]])
                value = inc + 0.5 * unlock
                if k == castle and level[k] == 0:
                    value = 1e6  # the castle first
                if value <= 0 or (k != castle and cost > 0 and cost / max(value, 1e-9) > dawns_left):
                    continue
                score = value / max(cost, 0.5)
                if best is None or score > best[0]:
                    best = (score, k, cost, inc)
            if best is None:
                break
            _, k, cost, inc = best
            gold -= cost
            level[k] += 1
            income += inc
            bought.append(f"{slots[k].label} L{level[k]}")
        dusk = gold
        gold += wave_gold[day - 1] if day - 1 < len(wave_gold) else 0
        gold += income
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
            err(p + ".nights", "no nights")
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
        pts = [(s.label, s.pos) for s in slots.values() if s.added or any(e.get("ref") == s.key and e.get("pos") for e in bs.get("edit") or [])]
        for label, pos in pts:
            x, y, z = pos
            gy = T.height_at(x, z) if T else g.height(x, z)
            if gy is None:
                err(f"{p}.buildSlots", f"{label} at ({x:.0f}, {z:.0f}) is off the map's ground")
                continue
            if abs(gy - y) > 1.0:
                warn(f"{p}.buildSlots", f"{label}: y={y:.2f} but the ground there is {gy:.2f} (use \"at\" to set it automatically)")
            ij = ground.index(x, z)
            if ij and ground.kind[ij[1]][ij[0]] in "#.":
                warn(f"{p}.buildSlots", f"{label} at ({x:.0f}, {z:.0f}) isn't on walkable ground (cliff, water or blocked): the king may not reach it")
        allp = list(slots.values())
        for i in range(len(allp)):
            for j in range(i + 1, len(allp)):
                a, b = allp[i], allp[j]
                if not (a.added or b.added):
                    continue
                d = math.hypot(a.pos[0] - b.pos[0], a.pos[2] - b.pos[2])
                if d < 4.5:
                    warn(f"{p}.buildSlots", f"{a.label} and {b.label} are {d:.1f} m apart: buildings will overlap (keep >= 5 m; 7+ for big ones)")
        for pi_, pr in enumerate(lv.get("props") or []):
            name = pr.get("base", "")[5:].split("@")[0]
            on_map = any(s["base"] == "mesh:" + name for s in m.get("scenery", []))
            in_lib = any(x["name"] == name for x in ref.scenery.get("meshes", []))
            if not (on_map or in_lib):
                err(f"{p}.props[{pi_}].base", f"no scenery mesh {name!r} (`palace.py ref scenery`)")
            for s in slots.values():
                if pr.get("solid", True) and math.hypot(pr["pos"][0] - s.pos[0], pr["pos"][2] - s.pos[2]) < prop_radius(ref, lv, pr) * 0.8 + 2.0:
                    warn(f"{p}.props[{pi_}]", f"solid prop {pr.get('id')} sits on slot {s.label}")
                    break
        # Lines and routes.
        lines = level_lines(ref, lv)
        used = used_lines(data, lv)
        for l in lines:
            if l["added"]:
                k = ground.index(l["center"][0], l["center"][2])
                if k is None or ground.kind[k[1]][k[0]] == "#":
                    err(f"{p}.spawnLines", f"spawn line {l['name']} is off the map")
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


def render(ref, data, level, path, background=None, world=None, routes=None, labels="names", px=1400):
    m = ref.map(level["host"])
    slots, castle, _ = effective_slots(ref, level)
    lines = level_lines(ref, level)
    pts = [(s.pos[0], s.pos[2]) for s in slots.values()] + [(l["center"][0], l["center"][2]) for l in lines]
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
    # Routes.
    for r in routes or []:
        col = (255, 120, 40) if r.get("reachable") else (255, 0, 0)
        p = [P(a, b) for a, b in r.get("points") or []]
        for i in range(len(p) - 1):
            cv.line(p[i][0], p[i][1], p[i + 1][0], p[i + 1][1], col, 2, a=0.8, dash=6)
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
    taken = [(W - 175, 0, W, 190), (0, 0, 520, 30)]
    for q in slots.values():
        qx, qy = P(q.pos[0], q.pos[2])
        taken.append((qx - 7, qy - 7, qx + 7, qy + 7))
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
        sc = 1 if len(slots) > 60 else 2
        tw, th = len(text) * 6 * sc, 7 * sc
        for lx, ly in ((x + r + 3, y - th / 2), (x + r + 3, y + r), (x + r + 3, y - r - th), (x - r - 3 - tw, y - th / 2), (x - tw / 2, y + r + 2),
                       (x - tw / 2, y - r - th - 4)):
            box = (lx - 2, ly - 2, lx + tw + 2, ly + th + 2)
            if not any(box[0] < b[2] and b[0] < box[2] and box[1] < b[3] and b[1] < box[3] for b in taken):
                break
        taken.append(box)
        cv.text(lx, ly, text, (255, 255, 255), sc)
    # Legend.
    yy = 8
    for name, col in (("castle", KIND_COLORS[0][1]), ("economy", KIND_COLORS[1][1]), ("military", KIND_COLORS[2][1]), ("walls", KIND_COLORS[3][1]),
                      ("special", (170, 120, 230)), ("spawn line", (200, 40, 60)), ("route", (255, 120, 40)), ("needs castle", (240, 196, 60)),
                      ("needs slot", (80, 200, 180))):
        cv.rect(W - 170, yy, W - 156, yy + 14, col)
        cv.text(W - 150, yy, name, (255, 255, 255), 2, bga=0.5)
        yy += 20
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
    out({"palace.py": VERSION, "kit": str(env.kit), "bepinex": str(env.bepinex) if env.bepinex else None,
         "packs": str(env.packs) if env.bepinex else None, "reference": str(env.ref), "referenceMaps": Ref(env).maps(),
         "bridge": s or "off (Settings > Mods > Builder > Agent bridge)"})


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
                ext = [g["origin"][0], g["origin"][1], round(g["origin"][0] + g["size"][0] * g["cell"]), round(g["origin"][1] + g["size"][1] * g["cell"])]
            rows.append({"map": scene, "castle": m["castle"]["pos"] if m.get("castle") else None, "extent": ext, "nights": m.get("nights"),
                         "startingGold": m.get("startingGold"), "slots": kinds,
                         "spawnLines": [f"{l['name']} ({'G' if l['smallGround'] else ''}{'L' if l['bigGround'] else ''}{'F' if l['flying'] else ''})" for l in m["spawnLines"]]})
        out(rows)
    elif args.what == "slots":
        m = ref.map(args.name)
        rows = []
        for s in m["slots"]:
            if args.kind and s["kind"].lower() != args.kind.lower():
                continue
            rows.append({"name": s["name"], "kind": s["kind"], "at": [s["pos"][0], s["pos"][2]], "y": s["pos"][1],
                         "needs": f"{s['activator']}:{s['activatorLevel'] + 1}" if s.get("activator") and s.get("startDeactivated") else "start",
                         "costs": [u["cost"] for u in s["upgrades"] if not u.get("disabled")],
                         "income": [step_income(u) for u in s["upgrades"] if not u.get("disabled")],
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

    L.append("## Buildings (slot kinds)\n")
    L.append("Per level: cost in gold, then each choice (branch) with its income change (+g per dawn), extra HP and what it adds. "
             "Hidden tiers exist in the data but are switched off in normal play (PalaceMod's optional \"Level 4 buildings\" turns them on). "
             "`add_slot` copies the most complete slot of the kind on the host map, so these trees are what you get.\n")
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
    path = pack_path(env, args.pack)
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
    if any(l.get("id") == args.level for l in levels):
        raise Fail(f"level {args.level!r} exists")
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


def read_ops(src):
    text = sys.stdin.read() if src == "-" else Path(src).read_text(encoding="utf-8")
    ops = json.loads(text)
    if isinstance(ops, dict):
        ops = ops.get("ops") or [ops]
    return ops


def cmd_apply(env, args):
    ref = Ref(env)
    path, data = load_pack(env, args.pack)
    ops = Ops(env, ref, data, args.level)
    ops.run(read_ops(args.ops))
    issues = [] if args.no_check else check_pack(ref, data)
    errors = [i for i in issues if i["severity"] == "error"]
    if not args.dry_run:
        save_pack(path, data)
    out({"applied": ops.log, "saved": None if args.dry_run else str(path), "errors": errors,
         "warnings": [i for i in issues if i["severity"] == "warning"]})


def cmd_check(env, args):
    ref = Ref(env)
    path, data = load_pack(env, args.pack)
    issues = check_pack(ref, data)
    result = {"pack": data.get("id"), "offline": issues}
    if args.live:
        g = Game(env)
        result["game"] = g.agent(f"validate {data['id']}")
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
    full = sum(sum(step_income(u) for u in s.upgrades) for s in slots.values())
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
    out(result)


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
        routes = [{"reachable": r["reachable"], "points": r["points"]} for r in paths.get("routes", [])]
        out(render(ref, data, lv, o, background=bg, world=cap["world"], routes=routes, labels=args.labels))
    else:
        routes = offline_routes(ref, data, lv, slots, castle) if castle in slots and not args.no_routes else []
        out(render(ref, data, lv, o, routes=routes, labels=args.labels, px=args.pixels))


def cmd_paths(env, args):
    g = Game(env)
    r = g.agent("paths rescan" if args.rescan else "paths", timeout=60)
    for route in r.get("routes", []):
        if not args.points:
            route.pop("points", None)
    out(r)


def cmd_play(env, args):
    g = Game(env)
    _, data = load_pack(env, args.pack)
    find_level(data, args.level)
    v = g.agent(f"validate {data['id']}")
    if not v.get("ok"):
        out(v)
        raise Fail("the game's validator found errors (above)")
    mark = g.log_size()
    g.agent(f"play {data['id']} {args.level}")
    st = g.wait_level_day(timeout=args.timeout)
    time.sleep(2.5)  # the level's rescan and path check run a moment after start
    notes = g.log_since(mark, features=["Builder"], min_level="warn")
    out({"status": g.status(), "loadWarnings": notes})


def wait_night(g, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        r = g.agent("report")
        if r.get("done"):
            return r
        time.sleep(1.0)
    r = g.agent("report")
    r["outcome"] = r.get("outcome") or "timeout"
    st = g.status()
    r["timeoutStatus"] = {"enemiesLeft": st.get("enemies"), "time": st.get("time")}
    return r


def cmd_night(env, args):
    g = Game(env)
    if args.speed:
        g.agent(f"speed {args.speed}")
    g.agent("night" + (f" {args.night}" if args.night else ""))
    if args.no_wait:
        out(g.status())
        return
    out(wait_night(g, args.timeout))


def parse_nights(spec, total):
    if not spec:
        return list(range(1, total + 1))
    if "-" in spec:
        a, b = spec.split("-")
        return list(range(int(a), int(b) + 1))
    return [int(x) for x in spec.split(",")]


def cmd_test(env, args):
    """Loads the level, then for each night: the plan's (or --build's) purchases for that day, start the night, wait
    for its end, record the report (and screenshots). Stops at a defeat."""
    g = Game(env)
    _, data = load_pack(env, args.pack)
    lv = find_level(data, args.level)
    plan = json.loads(Path(args.plan).read_text()) if args.plan else {}
    if not args.no_load:
        cmd_play_quiet(g, data, lv["id"], args.timeout_load)
    st = g.status()
    total = st.get("nights", 0)
    nights = parse_nights(args.nights, total)
    if args.god:
        g.agent("god on")
    if args.invulnerable:
        g.agent("invulnerable on")
    if args.gold is not None:
        g.agent(f"gold {args.gold}")
    shots = Path(args.shots) if args.shots else None
    results = []
    first = True
    for n in nights:
        st = g.status()
        if st.get("state") in ("AfterMatchDefeat", "AfterMatchVictory"):
            break
        # Purchases for the day before night n.
        days = [str(d) for d in range(1, n + 1)] if first and nights[0] > 1 and not args.no_catchup else [str(n)]
        bought = []
        for day in days:
            for item in plan.get(day, []):
                if isinstance(item, str):
                    item = {"slot": item}
                cmdline = f"build {item['slot'].replace(' ', '_')}" + (f" {item['level']}" if "level" in item else "") + \
                          (f" {item.get('branch', 0)}" if "branch" in item else "") + ("" if args.free else " pay")
                try:
                    bought.append(g.agent(cmdline))
                except Fail as e:
                    bought.append({"slot": item["slot"], "error": str(e)})
        if args.build == "all":
            bought.append(g.agent("build all"))
        if first and n > st.get("night", 1):
            g.agent(f"night {n}")
        else:
            g.agent("night")
        first = False
        if args.speed:
            g.agent(f"speed {args.speed}")
        mid = None
        if shots:
            time.sleep(args.shot_after)
            mid = g.screenshot(shots / f"night{n}.png")
        rep = wait_night(g, args.timeout)
        rep["bought"] = bought
        if mid:
            rep["screenshot"] = mid
        st = g.status()
        if rep.get("outcome") == "dawn" and rep.get("night") == total:
            # The last night: the game declares the win a few seconds after dawn.
            try:
                st = g.wait(lambda s: s.get("state") in ("AfterMatchVictory", "AfterMatchDefeat"), 30, "the end screen")
            except Fail:
                pass
        rep["after"] = {k: st.get(k) for k in ("time", "gold", "castle", "buildings", "units", "state") if st.get(k) is not None}
        if st.get("state") == "AfterMatchVictory":
            rep["outcome"] = "victory"
        elif st.get("state") == "AfterMatchDefeat":
            rep["outcome"] = "defeat"
        results.append(rep)
        if rep.get("outcome") in ("defeat", "timeout", "victory", "left"):
            break
        # Let dawn finish (income paid, day starts) before the next purchases.
        try:
            g.wait(lambda s: s.get("time") == "day" or s.get("state") in ("AfterMatchVictory", "AfterMatchDefeat"), 60, "the next day")
        except Fail:
            pass
    out({"level": f"{data['id']}/{lv['id']}", "nights": results})


def cmd_play_quiet(g, data, level, timeout):
    v = g.agent(f"validate {data['id']}")
    if not v.get("ok"):
        out(v)
        raise Fail("the game's validator found errors (above)")
    g.agent(f"play {data['id']} {level}")
    g.wait_level_day(timeout=timeout)
    time.sleep(2.5)


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
    v = g.agent(f"validate {data['id']}")
    if not v.get("ok"):
        out(v)
        raise Fail("the game's validator found errors (above)")
    mark = g.log_size()
    g.agent(f"sandbox {data['id']} {args.level}" + (f" {args.at[0]} {args.at[1]}" if args.at else ""))
    g.wait_level_day(timeout=args.timeout)
    time.sleep(2.5)
    if args.no_infinite:
        g.agent("infinitegold off")
    out({"status": g.status(), "loadWarnings": g.log_since(mark, features=["Builder"], min_level="warn")})


def cmd_spawn(env, args):
    g = Game(env)
    u = Ref(env).unit(args.unit) if not args.unit.startswith("vanilla:") else None
    unit = "vanilla:" + u["id"] if u else args.unit
    where = f" line {args.line}" if args.line else (f" at {args.at[0]} {args.at[1]}" if args.at else "")
    out(g.agent(f"spawn {unit.replace(' ', '_')} {args.count}" + (" elite" if args.elite else "") + where))


def cmd_cheat(env, args):
    g = Game(env)
    if args.what in ("god", "invulnerable", "infinitegold"):
        out(g.agent(f"{args.what} {args.value or 'on'}"))
    elif args.what in ("gold", "speed"):
        out(g.agent(f"{args.what} {args.value}"))
    elif args.what in ("kill", "heal", "endnight"):
        out(g.agent(args.what))
    elif args.what == "teleport":
        x, z = args.value.split(",")
        out(g.agent(f"teleport {x} {z}"))
    else:
        raise Fail("cheat god|invulnerable|infinitegold [on|off] | gold <n> | speed <x> | kill | heal | endnight | teleport <x,z>")


def cmd_build(env, args):
    g = Game(env)
    out(g.agent(f"build {args.slot.replace(' ', '_')}" + (f" {args.level}" if args.level is not None else "") + (f" {args.branch}" if args.branch is not None else "") +
                (" pay" if args.pay else "")))


def cmd_shot(env, args):
    out({"written": Game(env).screenshot(args.out)})


def cmd_report(env, args):
    out(Game(env).agent("report"))


def cmd_share(env, args):
    g = Game(env)
    _, data = load_pack(env, args.pack)
    if args.export:
        print(g.raw(f"builder-share export {data['id']}"))
    else:
        print(g.raw(f"builder-share code {data['id']}"))


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

    p = sub.add_parser("new", help="create a pack")
    p.add_argument("pack")
    p.add_argument("--name")
    p.add_argument("--author")
    p.add_argument("--description")
    p.add_argument("--force", action="store_true")

    p = sub.add_parser("level", help="add a level to a pack")
    p.add_argument("pack")
    p.add_argument("level")
    p.add_argument("--host", required=True, help="base map scene, e.g. Nordfels")
    p.add_argument("--name")
    p.add_argument("--base", default="map", choices=["map", "cleared", "flat"])
    p.add_argument("--waves", default="empty", choices=["empty", "vanilla", "none"], help="new wave set: empty, the map's own nights, or none (play vanilla)")

    p = sub.add_parser("apply", help="apply JSON edit ops (file or - for stdin)")
    p.add_argument("pack")
    p.add_argument("ops")
    p.add_argument("--level", help="default level for ops without \"level\"")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--no-check", action="store_true")

    p = sub.add_parser("check", help="offline checks (+ --live: the game's validator)")
    p.add_argument("pack")
    p.add_argument("--live", action="store_true")

    for name, fn_help in (("show", "the level as it will play: slots, requirements, lines"), ("economy", "gold curve of a greedy economy player"),
                          ("waves", "night-by-night enemy strength")):
        p = sub.add_parser(name, help=fn_help)
        p.add_argument("pack")
        p.add_argument("level", nargs="?")
        if name == "economy":
            p.add_argument("--compare", action="store_true", help="also the base map's vanilla curve")
        if name == "waves":
            p.add_argument("--compare", nargs="?", const="host", help="also a map's vanilla nights (default: the host)")

    p = sub.add_parser("render", help="top-down picture with slots, requirement arrows, spawn lines and routes")
    p.add_argument("pack")
    p.add_argument("level", nargs="?")
    p.add_argument("-o", "--out")
    p.add_argument("--live", action="store_true", help="the real map from the running game (level must be loaded)")
    p.add_argument("--labels", default="names", choices=["names", "costs", "reqs", "none"])
    p.add_argument("--pixels", type=int, default=1400)
    p.add_argument("--no-routes", action="store_true")

    p = sub.add_parser("paths", help="the game's own path check (level loaded)")
    p.add_argument("--rescan", action="store_true")
    p.add_argument("--points", action="store_true")

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
    p.add_argument("--free", action="store_true", help="plan purchases cost nothing")
    p.add_argument("--gold", type=int, help="set gold before the first night")
    p.add_argument("--god", action="store_true", help="the king takes no damage")
    p.add_argument("--invulnerable", action="store_true", help="buildings and units take no damage")
    p.add_argument("--speed", type=float, default=3)
    p.add_argument("--shots", help="folder for a screenshot of each night")
    p.add_argument("--shot-after", type=float, default=12, help="seconds into the night for the screenshot")
    p.add_argument("--timeout", type=float, default=420, help="seconds per night before giving up")
    p.add_argument("--timeout-load", type=float, default=120)
    p.add_argument("--no-load", action="store_true", help="continue the run already loaded")
    p.add_argument("--no-catchup", action="store_true", help="starting at night N>1: don't apply earlier days' plan")

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

    p = sub.add_parser("spawn", help="spawn enemies now (they attack like a wave's)")
    p.add_argument("unit", help="vanilla id (E Melee) or a pack unit id")
    p.add_argument("count", nargs="?", type=int, default=1)
    p.add_argument("--elite", action="store_true")
    p.add_argument("--line", help="at a spawn line")
    p.add_argument("--at", nargs=2, type=float, metavar=("X", "Z"))

    p = sub.add_parser("cheat", help="god|invulnerable|infinitegold [on|off] | gold <n> | speed <x> | kill | heal | endnight | teleport <x,z>")
    p.add_argument("what")
    p.add_argument("value", nargs="?")

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
    p.add_argument("--export", action="store_true")

    args = ap.parse_args(argv)
    env = Env(args.game)
    fn = globals()["cmd_" + args.cmd]
    try:
        fn(env, args)
    except Fail as e:
        print(json.dumps({"error": str(e)}), file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main()
