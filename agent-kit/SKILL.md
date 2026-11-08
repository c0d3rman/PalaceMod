---
name: thronefall-levels
description: Design, build, check, picture and play-test custom Thronefall levels (maps, build slots, requirements, terrain, scenery, enemy nights) with the PalaceMod Builder and its palace.py tool. Use when asked to make, edit, balance or test a Thronefall level, map, pack, wave set or custom enemy.
---

# Thronefall levels with PalaceMod

Install: copy this folder (the one the mod writes to `<Thronefall>/BepInEx/PalaceMod/Agent/`) to
`~/.claude/skills/thronefall-levels/`, or point the agent at that folder directly. The kit's files:

- `level-building.md`: the guide. **Read it fully before the first build**: format, rules, ops, design heuristics.
- `reference.md`: generated tables (maps, buildings, enemies, vanilla nights and economy curves, scenery).
- `palace.py`: the CLI (Python 3.8+, stdlib only). Run it from the game's copy so it finds the game, or pass
  `--game "<Thronefall folder>"` / set `PALACE_GAME`.
- `examples/`: a complete worked level (ops, plan, commands).

## Workflow

1. `python3 palace.py where`. If the bridge is off, ask the player to enable Settings > Mods > Builder > Agent bridge
   with the game running. If no reference maps are listed: `python3 palace.py ref build`.
2. Study the base map: `ref maps`, `ref slots <Map>`, `ref render <Map> -o base.png` (read the PNG).
3. Plan the level (lanes, choke points, castle rings, economy, night curve), then create it:
   `new <level> --host <Map> --base cleared|flat|map` (a level in the player's library; `list` shows the library).
4. Edit only through ops files: `apply library ops.json --level <level>`. Fix every `errors` entry it prints.
5. Inspect: `render library <level> -o map.png --labels reqs`, `show`, `economy --compare`, `waves --compare`.
6. Test in the game: `play` then `paths`; `test library <level> --plan plan.json --shots shots/`; read each night's
   report (outcome, castle min HP, buildings knocked out). `render --live` for the real map with game routes.
7. Iterate until early nights are survivable with half the gold, late nights are close, and every lane reaches the
   castle.

## Rules that bite

- Positions are world metres, x east, z north; give ops `"at": [x, z]` so heights come from the ground.
- One prerequisite per slot: `requires` = castle level or one slot (chain for more). `"castle:3"` = castle level 3.
- On `cleared`/`flat` bases every slot you add needs the castle (default) or a chain to it, or it never appears.
- Almost every building can't be upgraded past the castle's level (upgradeCap 0).
- Each night needs at least one spawn group; ground groups need a line with a route to the castle.
- The host decides which building kinds exist, the castle position and the playable rectangle, even on `flat`
  (reference.md > "What the host decides"). Pick it first.
- Re-apply a changed ops file with `apply --fresh` (ops aren't idempotent).
- In `test` reports a refused purchase is `"ok": false` with an `error`; read `summary[].refused`. Use plan options
  `carry`/`save` so a refused castle upgrade isn't silently lost.
- Enemies hit the nearest building on their lane every night; `economy --report test.json` shows what was really paid.
- Never hand-edit other folders or the game's saves; the library lives in `BepInEx/PalaceBuilder/library/` (one
  file per level and per enemy). Any level can spawn any library enemy by its id. Packs are only for sharing:
  `export "<name>" <level,...>` writes a .tfpack with the enemies and assets those levels use.
