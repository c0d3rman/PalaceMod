"""Dev-only: dump every level's waves and enemy stats from the installed game into JSON.

Run through `python3 scripts/dev.py dump-data`, which sets up a private venv with UnityPy. Output goes to
.local/gamedata/ (gitignored). Never commit or ship it: it's the game's data.

Uses: checking the Compendium's runtime numbers against an independent source, diffing game updates, and looking at
other levels' waves (the mod can only read the loaded level's at runtime).

Usage: dump_data.py <Data dir> <output dir> [scene name filter]
"""
import json
import sys
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

UNITY_VERSION = "2022.3.62f2"
ELITE_HP, ELITE_DAMAGE = 4.0, 3.0

# TagManager.ETag
TAGS = {0: "PlayerOwned", 1: "EnemyOwned", 2: "Player", 3: "CastleCenter", 4: "MeeleFighter", 5: "RangedFighter",
        6: "Flying", 7: "PlayerUnit", 8: "Building", 9: "SiegeWeapon", 10: "AUTO_Alive",
        11: "AUTO_KnockedOutAndHealOnDawn", 12: "Wall", 13: "InfrastructureEconomy",
        14: "TakesReducedDamageFromPlayerAttacks", 15: "PracticeTargets", 16: "FastMoving",
        17: "ArmoredAgainstRanged", 18: "VulnerableVsRanged", 19: "Monster", 20: "House", 21: "WallOrTower",
        22: "AUTO_Commanded", 23: "TakesIncreasedDamageFromTowers", 24: "Tower", 25: "AUTO_NoReviveNextMorning",
        26: "PlayerOwnedPriorityTarget", 27: "BlockableEnemyProjectile", 28: "Boss",
        29: "UselessWallThatDoesNotBlockPath", 30: "Healer", 31: "MagicProjectile",
        32: "NaturallyVulnerableToSplash", 33: "NaturallyHighHealthTarget",
        34: "UnreachableForBigEnemiesDueToLevelDesign", 35: "PlayerShrine", 36: "VodooCursed",
        37: "PlayerHeroUnit", 39: "NONE"}
BLACKSMITH = {0: None, 1: "MultiplyBy_MeleeDamage", 2: "MultiplyBy_RangedDamage", 3: "DivideBy_MeleeResistance",
              4: "DivideBy_RangedResistance"}


class Reader:
    def __init__(self, data_dir: Path):
        self.gen = TypeTreeGenerator(UNITY_VERSION)
        self.gen.load_local_dll_folder(str(data_dir / "Managed"))
        self.nodes = {}

    def script_name(self, obj):
        data = obj.read(check_read=False)
        script = data.m_Script.read() if data.m_Script else None
        return script, data

    def mono(self, obj):
        """Typetree dict for a MonoBehaviour, plus its class name."""
        script, _ = self.script_name(obj)
        if script is None:
            return None, None
        full = f"{script.m_Namespace}.{script.m_ClassName}" if script.m_Namespace else script.m_ClassName
        key = (script.m_AssemblyName, full)
        if key not in self.nodes:
            try:
                self.nodes[key] = self.gen.get_nodes_up(script.m_AssemblyName, full)
            except Exception:
                self.nodes[key] = None
        if self.nodes[key] is None:
            return script.m_ClassName, None
        try:
            return script.m_ClassName, obj.read_typetree(self.nodes[key])
        except (ValueError, EOFError):
            # A few classes' layouts don't match the generated tree; none of them are ones we read.
            return script.m_ClassName, None


def components(go_reader, reader: Reader):
    """{class name: [typetree]} for a GameObject's MonoBehaviours."""
    go = go_reader.read()
    out = {}
    for comp in go.m_Components:
        ptr = comp.component if hasattr(comp, "component") else comp
        try:
            obj = ptr.deref()
        except Exception:
            continue
        if obj.type.name != "MonoBehaviour":
            continue
        name, tree = reader.mono(obj)
        if name and tree is not None:
            out.setdefault(name, []).append(tree)
    return out, go


def children(go):
    """Child GameObjects of a GameObject, depth first."""
    transform = None
    for comp in go.m_Components:
        ptr = comp.component if hasattr(comp, "component") else comp
        obj = ptr.deref()
        if obj.type.name in ("Transform", "RectTransform"):
            transform = obj.read()
            break
    if transform is None:
        return
    for child in transform.m_Children:
        child_t = child.deref().read()
        child_go_reader = child_t.m_GameObject.deref()
        yield child_go_reader
        yield from children(child_go_reader.read())


def find_component(go_reader, reader, name):
    comps, go = components(go_reader, reader)
    if name in comps:
        return comps[name][0], comps
    for child in children(go):
        c, _ = components(child, reader)
        if name in c:
            return c[name][0], comps
    return None, comps


def deref_tree(env_file, pptr):
    """Resolve a typetree PPtr dict {m_FileID, m_PathID} relative to the file that holds it."""
    if not pptr or pptr.get("m_PathID", 0) == 0:
        return None
    file_id = pptr["m_FileID"]
    target = env_file if file_id == 0 else env_file.externals[file_id - 1]
    if file_id != 0:
        name = Path(target.path).name
        target = env_file.environment.files.get(name) or next(
            (f for k, f in env_file.environment.files.items() if k.endswith(name)), None)
        if target is None:
            return None
    return target.objects.get(pptr["m_PathID"])


def modifiers(env_file, tree_list):
    return [{"requiredTags": [TAGS.get(t, t) for t in m["requiredTags"]], "damageAdded": m["damageAdded"],
             "damageMultiplyer": m["damageMultiplyer"]} for m in tree_list or []]


def enemy_stats(go_obj, reader):
    """Base stats of an enemy prefab, read the same way EnemySpawner.GetWaveInfo does."""
    comps, go = components(go_obj, reader)
    marker = (comps.get("ScreenMarkerIcon") or [None])[0]
    hp, _ = find_component(go_obj, reader, "Hp")
    move, _ = find_component(go_obj, reader, "PathfindMovementEnemy")
    tagged, _ = find_component(go_obj, reader, "TaggedObject")
    attack = None
    for cls in ("AutoAttack", "AutoAttackWithWarning", "AutoAttackTwoTargets", "AutoAttackLowestInRangeMulti",
                "FinalBossSpellThrower"):
        attack, _ = find_component(go_obj, reader, cls)
        if attack:
            break
    stats = {"prefab": go.m_Name, "locaKey": marker["locaKey"] if marker else None,
             "tags": [TAGS.get(t, t) for t in (tagged or {}).get("tags", [])],
             "maxHp": hp["maxHp"] if hp else None, "speed": move["movementSpeed"] if move else None}
    if attack:
        stats["cooldown"] = attack["cooldownDuration"]
        stats["damageMultiplyer"] = attack.get("damageMultiplyer", 1.0)
        stats["targetPriorities"] = [{"mustHave": [TAGS.get(t, t) for t in p["mustHaveTags"]], "range": p["range"]}
                                     for p in attack["targetPriorities"]]
        weapon_obj = deref_tree(go_obj.assets_file, attack["weapon"])
        if weapon_obj is not None:
            _, weapon = reader.mono(weapon_obj)
            if weapon:
                stats["weapon"] = {"blacksmithEffect": BLACKSMITH.get(weapon["blacksmithEffect"]),
                                   "projectileSpeed": weapon["projectileSpeed"],
                                   "directDamage": modifiers(weapon_obj.assets_file, weapon["directDamage"]),
                                   "splashDamage": modifiers(weapon_obj.assets_file, weapon["splashDamage"])}
    return stats


def dump_level(data_dir, reader, index, scene):
    env = UnityPy.load(str(data_dir / f"level{index}"), str(data_dir / "resources.assets"),
                       *[str(p) for p in sorted(data_dir.glob("sharedassets*.assets"))])
    level_file = env.files[f"level{index}"] if f"level{index}" in env.files else next(
        f for k, f in env.files.items() if k.endswith(f"level{index}"))
    enemies, waves = {}, []
    for obj in level_file.objects.values():
        if obj.type.name != "MonoBehaviour":
            continue
        name, tree = reader.mono(obj)
        if name != "EnemySpawner" or tree is None:
            continue
        for wave in tree["waves"]:
            spawns = []
            for spawn in wave["spawns"]:
                prefab = deref_tree(level_file, spawn["enemyPrefab"])
                key = None
                if prefab is not None:
                    key = f"{prefab.assets_file.name}:{prefab.path_id}"
                    if key not in enemies:
                        enemies[key] = enemy_stats(prefab, reader)
                line = deref_tree(level_file, spawn["spawnLine"])
                spawns.append({"enemy": key, "count": spawn["count"], "elite": bool(spawn["eliteEnemies"]),
                               "delay": spawn["delay"], "interval": spawn["interval"],
                               "goldCoins": spawn["goldCoins"],
                               "spawnLine": line.read().m_GameObject.deref().read().m_Name if line is not None else None})
            waves.append({"difficultyMulti": wave["difficultyMulti"], "spawns": spawns})
        break
    return {"scene": scene, "levelFile": f"level{index}", "eliteMultipliers": {"hp": ELITE_HP, "damage": ELITE_DAMAGE},
            "waves": waves, "enemies": enemies}


def main():
    data_dir, out_dir = Path(sys.argv[1]), Path(sys.argv[2])
    only = sys.argv[3].lower() if len(sys.argv) > 3 else None
    out_dir.mkdir(parents=True, exist_ok=True)
    reader = Reader(data_dir)
    settings = UnityPy.load(str(data_dir / "globalgamemanagers"))
    scenes = next(o.read_typetree()["scenes"] for o in settings.objects if o.type.name == "BuildSettings")
    for index, path in enumerate(scenes):
        scene = Path(path).stem
        if scene.startswith("_") or (only and only not in scene.lower()):
            continue
        result = dump_level(data_dir, reader, index, scene)
        if not result["waves"]:
            continue
        target = out_dir / f"{scene}.json"
        target.write_text(json.dumps(result, indent=1))
        print(f"{scene}: {len(result['waves'])} waves, {len(result['enemies'])} enemy types -> {target}")


if __name__ == "__main__":
    main()
