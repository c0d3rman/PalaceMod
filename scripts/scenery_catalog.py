"""Dev-only: regenerate src/PalaceMod/Assets/scenery.json, the Builder's cross-map scenery library catalog.

Run through `python3 scripts/dev.py scenery-catalog`, which uses the same private UnityPy venv as dump-data.

The catalog is metadata only (names, which file and object each mesh is in, how the maps place it). The mod reads the
meshes themselves from the player's own install at runtime (Features/Builder/Library); no game asset is ever copied
into the repo or the mod. Rerun this after a game update that changes the maps; the runtime refuses entries whose
file header (Unity version) or mesh (name, vertex count) don't match.

What counts as scenery is the same filter as LayoutScene.Props (the host-map palette): mesh objects outside build
slots, units and spawners, without a collider, on a non-UI/FX layer, 0.4-40 m across, not a Unity primitive.

Usage: scenery_catalog.py <Data dir> <output json>
"""
import json
import math
import statistics
import sys
from pathlib import Path

import UnityPy

UNITY_VERSION = "2022.3.62f2"
MAIN = ["Neuland(Tutorial)", "Nordfels", "Durststein", "Frostsee", "Uferwind", "Sturmklamm", "Wildbach", "Moorweg", "Freifort",
        "Totend"]
NON_PROP_LAYERS = {4, 5, 6, 10, 11, 12, 13, 18, 21, 24, 29, 31}
BLOCKING_SCRIPTS = {"BuildSlot", "Hp", "EnemySpawner"}
PRIMITIVES = {"Plane", "Cube", "Quad", "Cylinder", "Sphere", "Capsule"}

# Meshes the filter lets through that aren't scenery a designer would want (unit parts, invisible helpers).
EXCLUDE = {"Humanoid Unit Base Hand L", "Humanoid Unit Base Hand R", "sphere_0", "Totend Stairs Collider",
           "Elder Axe", "Mole Drill"}

# Palette categories (chip labels in a narrow palette, so one short word each). Curated by hand from the mesh list;
# anything new lands in "Other" until it's added here.
CATEGORIES = {
    "Trees": ["Agave Tree", "Blob Tree", "Cone Tree", "Cone Tree High Waist", "Cone Tree Slim High",
              "Dead Classic Conetree", "Freifort Tree 02", "Jungle Tree Trunk", "Mediteranean Tree", "Totend Tree",
              "Twisted Tree", "sphere_shape_24_16"],
    "Rocks": ["Rock A", "Rock A Alternate", "Rock B", "Rock B Alternate", "Rectangular Rock", "Square Rock",
              "SpikyRock", "SpikyRock2", "SpikyRock3", "Icicle Sharp", "Stepping Stone"],
    "Walls": ["Ruin Wall", "Ruin Wall .001", "Ruin Wall.001", "Ruin Wall.002", "Ruin Wall.003",
              "Ruin Wall.004", "Ruin Wall.005", "Ruin Wall.006", "Ruin Wall.007", "Wall Segment lvl2",
              "Broken Down Wall", "Totendwall", "Totend Gate Segment", "Totend Small Stair Wall",
              "Totend Small Wall Straight", "Totend Castle Column"],
    "Towers": ["Defense Tower lvl 1", "Defense Tower lvl 2", "Totend Tower", "Broken Graveyard Tower",
               "Graveyard Tower"],
    "Graves": ["GraveBot", "GraveTop", "Gravestone", "Gravewall", "Graveyard Wall Post"],
    "Other": ["Bridge", "Totend Insignia"],
}
CATEGORY_OF = {name: cat for cat, names in CATEGORIES.items() for name in names}


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def yaw_of(q):
    """Unity's eulerAngles.y of a quaternion (ZXY order): the heading of the rotated forward axis."""
    x, y, z, w = q
    fx = 2 * (x * z + w * y)
    fz = 1 - 2 * (x * x + y * y)
    # For tilted objects (forward pointing up/down) fall back to the rotated right axis.
    if fx * fx + fz * fz < 1e-6:
        rx = 1 - 2 * (y * y + z * z)
        rz = 2 * (x * z - w * y)
        return math.degrees(math.atan2(-rz, rx))
    return math.degrees(math.atan2(fx, fz))


def yaw_quat(deg):
    r = math.radians(deg) / 2
    return (0.0, math.sin(r), 0.0, math.cos(r))


def script_class(obj):
    try:
        return obj.read(check_read=False).m_Script.read().m_ClassName
    except Exception:
        return None


def scan_level(level_file):
    objs = level_file.objects
    layer, comps, transform_of = {}, {}, {}
    for pid, o in objs.items():
        if o.type.name != "GameObject":
            continue
        g = o.read()
        layer[pid] = g.m_Layer
        names = []
        for c in g.m_Components:
            p = c.component if hasattr(c, "component") else c
            if p.file_id != 0 or p.path_id not in objs:
                continue
            co = objs[p.path_id]
            t = co.type.name
            if t in ("Transform", "RectTransform"):
                transform_of[pid] = p.path_id
            elif t == "MonoBehaviour":
                t = script_class(co) or "MonoBehaviour"
            names.append(t)
        comps[pid] = names
    parent, go_of, local = {}, {}, {}
    for pid, o in objs.items():
        if o.type.name in ("Transform", "RectTransform"):
            t = o.read()
            parent[pid] = t.m_Father.path_id if t.m_Father else 0
            go_of[pid] = t.m_GameObject.path_id
            r, s = t.m_LocalRotation, t.m_LocalScale
            local[pid] = ((r.x, r.y, r.z, r.w), (s.x, s.y, s.z))

    def blocked(go):
        tid = transform_of.get(go)
        while tid:
            if any(c in BLOCKING_SCRIPTS for c in comps.get(go_of.get(tid), [])):
                return True
            tid = parent.get(tid, 0)
        return False

    def world(go):
        tid = transform_of.get(go)
        rot, scale = (0.0, 0.0, 0.0, 1.0), [1.0, 1.0, 1.0]
        while tid:
            r, s = local[tid]
            rot = qmul(r, rot)
            scale = [scale[i] * s[i] for i in range(3)]
            tid = parent.get(tid, 0)
        return rot, scale

    found = {}
    for o in objs.values():
        if o.type.name != "MeshFilter":
            continue
        mf = o.read()
        go = mf.m_GameObject.path_id
        if not mf.m_Mesh or mf.m_Mesh.path_id == 0 or layer.get(go) in NON_PROP_LAYERS:
            continue
        cl = comps.get(go, [])
        if "MeshRenderer" not in cl or any("Collider" in c for c in cl) or blocked(go):
            continue
        try:
            mesh_obj = mf.m_Mesh.deref()
            mesh = mesh_obj.read()
        except Exception:
            continue
        name = mesh.m_Name
        if not name or name.startswith("Combined") or name.startswith("quad") or name in PRIMITIVES:
            continue
        rot, scale = world(go)
        c, e = mesh.m_LocalAABB.m_Center, mesh.m_LocalAABB.m_Extent
        size = max(2 * e.x * abs(scale[0]), 2 * e.y * abs(scale[1]), 2 * e.z * abs(scale[2]))
        if size < 0.4 or size > 40:
            continue
        key = (mesh_obj.assets_file.name, mesh_obj.path_id)
        entry = found.setdefault(key, {"name": name, "mesh": mesh, "rots": [], "scales": [], "materials": None})
        entry["rots"].append(rot)
        entry["scales"].append(scale)
        if entry["materials"] is None:
            entry["materials"] = materials_of(objs, go)
    return found


def materials_of(objs, go):
    g = objs[go].read()
    for c in g.m_Components:
        p = c.component if hasattr(c, "component") else c
        if p.file_id == 0 and p.path_id in objs and objs[p.path_id].type.name == "MeshRenderer":
            names = []
            for mp in objs[p.path_id].read().m_Materials:
                try:
                    names.append(mp.deref().read().m_Name)
                except Exception:
                    names.append(None)
            return names
    return []


def main():
    data_dir, out = Path(sys.argv[1]), Path(sys.argv[2])
    settings = UnityPy.load(str(data_dir / "globalgamemanagers"))
    scenes = next(o.read_typetree()["scenes"] for o in next(iter(settings.files.values())).objects.values()
                  if o.type.name == "BuildSettings")
    index_of = {Path(s).stem: i for i, s in enumerate(scenes)}
    files = [str(data_dir / f"level{index_of[m]}") for m in MAIN]
    files += [str(data_dir / "resources.assets")] + [str(p) for p in sorted(data_dir.glob("sharedassets*.assets"))]
    env = UnityPy.load(*files)

    merged = {}
    for map_name in MAIN:
        level = f"level{index_of[map_name]}"
        level_file = next(f for k, f in env.files.items() if k.endswith(level))
        if level_file.unity_version != UNITY_VERSION:
            sys.exit(f"{level} is Unity {level_file.unity_version}, expected {UNITY_VERSION}; update UNITY_VERSION")
        for key, e in scan_level(level_file).items():
            m = merged.setdefault(key, {"name": e["name"], "mesh": e["mesh"], "maps": {}, "rots": [], "scales": [],
                                        "materials": e["materials"]})
            m["maps"][map_name] = len(e["rots"])
            m["rots"] += e["rots"]
            m["scales"] += e["scales"]
        print(f"{map_name}: {sum(1 for m in merged.values() if map_name in m['maps'])} meshes", file=sys.stderr)

    entries, names = [], set()
    for (file, path_id), m in merged.items():
        name = m["name"]
        if name in EXCLUDE:
            continue
        if name in names:
            sys.exit(f"two library meshes are named {name!r}; give one an @<Map> suffix in the runtime refs first")
        names.add(name)
        mesh = m["mesh"]
        if mesh.m_MeshCompression or not mesh.m_VertexData.m_DataSize:
            print(f"skipping {name}: compressed or streamed mesh", file=sys.stderr)
            continue
        home = max(m["maps"].items(), key=lambda kv: kv[1])[0]
        # The typical pose: the median-sized instance's rotation without its yaw, and its scale.
        order = sorted(range(len(m["scales"])), key=lambda i: math.prod(abs(s) for s in m["scales"][i]))
        pick = order[len(order) // 2]
        rot = m["rots"][pick]
        upright = qmul(yaw_quat(-yaw_of(rot)), rot)
        e = mesh.m_LocalAABB.m_Extent
        entries.append({
            "name": name,
            "home": home,
            "maps": {k: m["maps"][k] for k in MAIN if k in m["maps"]},
            "file": file,
            "pathId": path_id,
            "category": CATEGORY_OF.get(name.strip(), "Other"),
            "materials": m["materials"],
            "upright": [round(v, 5) for v in upright],
            "scale": [round(v, 4) for v in m["scales"][pick]],
            "scaleSpread": round(statistics.pstdev([math.prod(abs(s) for s in sc) ** (1 / 3) for sc in m["scales"]]), 3),
            "vertices": mesh.m_VertexData.m_VertexCount,
            "size": [round(2 * e.x, 3), round(2 * e.y, 3), round(2 * e.z, 3)],
        })
    entries.sort(key=lambda x: (MAIN.index(x["home"]), x["category"], x["name"]))
    # One mesh per line keeps the diff after a game update readable.
    lines = ",\n  ".join(json.dumps(e, separators=(",", ":")) for e in entries)
    out.write_text(f'{{\n "unity": "{UNITY_VERSION}",\n "maps": {json.dumps(MAIN)},\n "meshes": [\n  {lines}\n ]\n}}\n')
    print(f"{len(entries)} meshes -> {out}")


if __name__ == "__main__":
    main()
