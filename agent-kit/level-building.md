# Building Thronefall levels with PalaceMod: the agent guide

You are building a custom level for **Thronefall** (a minimalist tower-defence / kingdom builder) with the **PalaceMod**
Builder. This guide plus `palace.py` (next to it) is everything you need: the data format, the game's rules and numbers,
the tools, and how to test. `reference.md` holds the generated tables (maps, buildings, enemies, vanilla curves,
scenery); `palace.py ref ...` prints the same data as JSON.

Everything you make goes into the player's **library**: each level is one file (`library/levels/<id>.json`, the level
and its nights) and each custom enemy is one file (`library/enemies/<id>.json`). Any level can spawn any library enemy by
its id. `palace.py` shows the whole library as one pack named `library` (pass `library` wherever a command takes a
pack); the game reloads it within ~2 s of a change. Packs only exist for sharing: `palace.py export` writes a `.tfpack`
of chosen levels with every enemy and asset they use. You never edit the game; you write the library (through
`palace.py apply`), then test it in the running game through the **Agent bridge**.

Old packs: packs the player had before the library existed were moved into it (the folders are kept in
`PalaceBuilder/migrated-packs/`); their old pack id still works in every command and names the levels that came from
that pack. A folder under `packs/` (an installed pack) is read and written as before.

## 1. The loop

1. `python3 palace.py where` – finds the game, the packs folder and whether the bridge is on.
2. `python3 palace.py ref build` – once (game running, bridge on): caches every map's layout and the enemy data
   (about a minute; it loads each map). Then `ref maps`, `ref slots <map>`, `ref render <map> -o base.png`.
3. Design on paper first (section 9): map, entrances, castle defence, economy, nights.
4. `python3 palace.py new <level-id> --host <Map> --base cleared` (its new nights are empty: the game refuses to play
   the level until you add them with the `waves` op). `python3 palace.py list` shows the library.
5. Write ops to a JSON file and `python3 palace.py apply library ops.json --level <level-id>`. It saves, then prints
   `errors` and `warnings` (offline checks of the levels the ops touched). Fix errors before anything else.
6. Look: `python3 palace.py render library <level-id> -o map.png --labels reqs` (then read the PNG), `show`,
   `economy --compare`, `waves --compare`.
7. Test in the game: `python3 palace.py test library <level-id> --plan plan.json --shots shots/` (buys what a player
   would, plays the nights at 3x speed, reports each night), or `play` / `sandbox` and drive it step by step.
   `render --live` draws your overlay on a real top-down picture and the game's own enemy routes.
8. Iterate: change the ops file and `apply --fresh` it (the level is emptied back to what `level` made, its nights kept,
   then the whole file runs again; ops aren't idempotent, so a plain second `apply` of the same file fails at the first
   `add_slot`). Packs reload in the game within ~2 s; reload the level (`play` again) to see changes.

All commands print JSON. Errors go to stderr as `{"error": "..."}` with exit code 2; `test` exits 1 when the run is lost.

## 2. Setup

- The player has Thronefall with BepInEx 5 and PalaceMod installed. In the game: **Settings > Mods > Builder >
  Agent bridge > Enabled** (off by default). That also writes this kit to `<Thronefall>/BepInEx/PalaceMod/Agent/`.
  Settings > Mods > General > "Gameplay-changing features" must be on (custom levels are gameplay-changing).
- `palace.py` finds the game itself when it runs from that folder; otherwise pass `--game "<Thronefall folder>"` or
  set `PALACE_GAME`. Python 3.8+, no packages. `where` says which install it picked and why (`chosenBecause`), lists
  any others it found, and warns when it fell back to the default Steam install; a `--game` or `PALACE_GAME` that
  doesn't exist is an error, never a silent fallback.
- The bridge is a file channel in `BepInEx/PalaceMod/Agent/bridge/`: `palace.py` writes a request file, the game
  answers in a response file (and rewrites `status.json` every 2 s). Nothing listens on the network.
- With the bridge on, the game keeps running when another window has focus. If a command times out, the game may be
  loading, paused in a menu, or minimised (screenshots need a visible window).
- `palace.py game status` shows the scene, day/night, gold, castle HP. `palace.py game cmd "<raw command>"` sends any
  command (`game cmd help` lists them all). `palace.py game log --feature Builder` shows the mod's recent warnings.
- Claude Code: copy this folder's `SKILL.md` into `~/.claude/skills/thronefall-levels/` (see the skill's own header).

## 3. How Thronefall plays (what your level must serve)

- **Day / night.** Each day the king (the player, on horseback) walks to build slots and spends gold to build or
  upgrade. He presses "start night" when ready. Each night enemies spawn from **spawn lines** at the map's edges and
  walk (or fly) toward the player's buildings and the castle. The night ends when every enemy is dead; then dawn.
  The run is won after the last night. It is lost when the **castle** (its center building) is destroyed.
- **Gold.** Buildings with income pay at dawn (houses +1 each, mills, mines, harbours, fields). Enemies drop gold
  (the spawn group's `gold`, spread over its enemies). A night's leftover gold carries over. Day 1 starts with the
  wave set's `startingGold`.
- **Build slots** are fixed spots, each with one building kind and an **upgrade tree**: level 1 = build, level 2..n =
  upgrades; some levels are a **choice** of 2-4 branches (e.g. Barracks: Knights / Spearmen / Flails / Berserkers;
  Tower: Castle / Sniper / Armored / Bunker). Each level has a cost. Costs are per slot and you can change them.
- **Unlocking.** Most slots are hidden until a prerequisite is met: "castle at level N" or "slot X built (or at level
  N)". That is how a map reveals itself: build the castle, houses appear around it; upgrade the castle, towers appear
  further out. One prerequisite per slot (the game's native mechanism); combine conditions by chaining.
- **Knock-outs.** Buildings brought to 0 HP are knocked out, not destroyed: they come back at dawn, but pay no income
  that dawn. Units knocked out revive at dawn. Walls block enemies until broken.
- **The castle** shoots (35 m) from level 2 and is the lose condition. Upgrading it unlocks perks (choice per level)
  and, through requirements, most of the map. Castle levels cost 3 / 7 / 20 gold on every map.
- **Units.** Barracks, Archery Ranges and Hero's Quarters train squads that guard near their building and that the
  player can command. Towers shoot. Houses and mills defend themselves weakly.
- **Enemies** pick targets by type: most attack the **nearest** player building or unit as they walk; spiders and
  wasps go for economy, racers for the castle, siege for buildings, hunterlings for the king. Flyers ignore terrain and
  walls. Large enemies need wider paths. See `reference.md` > Enemies. So whatever stands first on a lane is hit by
  every wave on that lane, every night: a farm on the spawn side of a river isn't raided "sometimes", it's the first
  contact (`economy --report` shows how often each building was knocked out in a test).
- **Difficulty** per night multiplies enemy HP (x difficulty) and damage (x halfway to it); elite enemies have x4 HP,
  x3 damage.

## 4. Coordinates and units

- World metres. **x = east, z = north, y = up.** Map extents differ per map (`ref maps`: "extent", castle position).
  Pictures from `render` put +x right and +z up, with grid lines every 20 m and labels every 40 m.
- Positions in the pack are `[x, y, z]`. Ops take `"at": [x, z]` and fill `y` from the ground (the cached map
  grid, or your terrain). Use `"pos": [x, y, z]` only to force a height.
- `rotY`: yaw in degrees around the vertical axis (copies keep their base's tilt). Turn buildings to face the
  lane they guard; for most buildings it is cosmetic.
- **Which way lines and walls run**: at `rotY` 0 a spawn line's segment, and a wall or gate copied from a host's
  "Gate Variant", runs **east-west** (along x), so it faces, or blocks, traffic moving north or south; at `rotY` 90 it
  runs north-south. To close an east-west pass, use `rotY` 90. A gate's length varies by host piece; check it with
  `render --live` or a screenshot before relying on it to seal a gap.
- Build slots need ~5 m between centres (7+ for large buildings: castle, barracks, temple); the checker warns under
  4.5 m. Keep slots on walkable ground (green in renders) so the king can reach them.
- The game camera looks at the map from the south-ish at an angle; a picture's "up" is +z, not the player's view.

## 5. The pack format (every field)

The library's files and shared packs use this one format. A library level file holds one level and the wave set it
uses (`levels: [one]`, `waveSets: [its nights]`) plus a `"library": {kind, created, origin}` field; an enemy file holds
`units: [one]`; the library's assets map is `library/assets.json`. Wave set ids are unique across the library
(`palace.py` names a level's nights `<level-id>-nights`). `palace.py` reads and writes them through the `library` pack,
so you edit the composed view below.

```jsonc
{
  "format": "palace-builder", "version": 2,          // fixed
  "id": "author.pack-name",                          // 1-64 of a-z 0-9 . _ -; also the folder name
  "name": "Shown name", "author": "...", "description": "...",
  "assets": {},                                       // files (icons, meshes, sounds) the pack ships; optional
  "units": [ UnitDef ],                               // custom enemies/troops; optional
  "waveSets": [ WaveSetDef ],                         // the nights
  "levels": [ LevelDef ]                              // the maps
}
```

Rules: optional values may be absent; absent or `null` means "keep the base's value", never zero. Unknown fields are
kept. Ids everywhere: 1-64 characters of `a-z 0-9 . _ -`, starting with a letter or digit.

### LevelDef

```jsonc
{
  "id": "gorge", "name": "The Gorge", "description": "...",
  "host": "Nordfels",              // base map (scene name; see ref maps). Gives lighting, sky, ground, castle position
  "base": "cleared",               // absent = the map as it is; "cleared" = the map's ground with every slot except the
                                   // castle removed and scenery hidden; "flat" = cleared + the map's ground replaced by
                                   // your terrain grid (section 7)
  "waveSet": "gorge-nights",       // a wave set id in this pack; absent = the map's vanilla nights
  "buildSlots": {
    "remove": ["House Variant (3)"],               // host slots to take away (slots they unlock never appear)
    "edit": [{ "ref": "Mill Variant",              // a host slot by name (ref slots <map>)
               "pos": [x,y,z], "rotY": 0,           // move it (the castle can't move)
               "requires": { "slot": "castle", "level": 1 },   // see Requirements; absent = keep the map's
               "upgradeCap": 0,                     // see Requirements; absent = keep
               "upgrades": [{ "cost": 2 }, null, { "cost": 9 }] }],   // per level (index 0 = build); null keeps
    "add": [{ "id": "h1",                           // new slot, unique in the level
              "base": "scene:House Variant",        // the host slot it copies: kind, tree, costs, look
              "pos": [x,y,z], "rotY": 0,
              "requires": { "slot": "add:h0", "level": 0 },
              "upgradeCap": null, "upgrades": [ ... ] }]
  },
  "spawnLines": {
    "edit": [{ "ref": "Main Bridge", "pos": [x,y,z], "rotY": 30 }],   // move a host line (pos = its middle)
    "add": [{ "id": "north", "pos": [x,y,z], "rotY": 0, "width": 12,  // new line: a segment width m long;
              "ground": true, "large": true, "flying": true }],      // rotY 0 runs east-west (along x), 90
                                                                     // north-south; which enemies may use it
    "remove": ["Forest", "Right Bridge"]   // host lines taken off the map: no enemies, routes or markers
  },
  "props": [{ "id": "rock-1", "base": "mesh:Rock A", "pos": [x,y,z], "rotY": 0, "scale": 1.0, "solid": true }],
  "terrain": { ... }                                  // section 7
}
```

- On a **cleared/flat** base only the castle (and its own add-ons) remain; host spawn lines stay where they are
  (on flat they may sit outside your ground) until you remove them. Add your own lines, then take off the host's
  you don't use (`remove_spawn_line`), or they keep showing in `paths`, renders and the minimap. A wave set that
  still spawns from a removed line is an error, and so is removing a line the map's own nights use while the level
  has no wave set. Every slot you add defaults to "needs the castle built" when you use ops.
- **The host still decides a lot**, even on `flat`: which building kinds exist (`add_slot` copies a host slot of that
  kind, so a kind the host lacks can't be placed: Frostsee has no Mill), the castle position, the playable rectangle
  (`terrain_init`'s default grid), the light and ground palette, and how much room the terrain has above the castle
  (heights are world heights, clamped to [-8, 30] m: Sturmklamm's castle sits at 31 m, so nothing there can rise above
  it). `reference.md` > Maps > "What the host decides" has the kinds-by-host table and each host's rectangle and
  headroom. Choose the host from it before designing.
- `base: null` (the map as it is) keeps all vanilla slots, scenery and routes; you then remove, move, re-price and
  add. Good for remixes; for original designs use `cleared` or `flat`.
- Added slots are named `PalaceBuilder <id>` in the running game; `palace.py build`/plans accept the bare id.
- Props: `mesh:<name>` from the map's own scenery or the cross-map scenery library (`ref scenery`, reference.md);
  `mesh:<name>@<Map>` picks one map's mesh. `solid: true` props block units (pathfinding is rebuilt at level start);
  `scale` 0.1-10, relative to how the map itself uses the mesh (a map's own rocks are often scaled up: "Rock A" on
  Nordfels is ~30 m across at scale 1), so check sizes with `render --live`. Props are decoration and walls of rock;
  slots are the gameplay.

### Requirements (castle level, prerequisites, house chains)

- `requires: { "slot": S, "level": L }`: the slot is hidden until S's level is **greater than L**. `level` counts from
  0: `0` = once S is built, `1` = once S reaches level 2, ... `S` is `"castle"`, a host slot name, or `"add:<id>"`.
- "Needs castle level N" = `{ "slot": "castle", "level": N-1 }`. The castle is unbuilt on day 1; level 1 = built.
- `requires: {}` (no slot) = available from the start. `requires` absent = the base map's/base slot's own.
- **One prerequisite per slot.** For "castle level 3 and house 6", require house 6 and give house 6 the castle-3
  requirement (the dependent inherits it). Loops are errors; a prerequisite that isn't on the level means the slot
  never appears (error).
- **House chains**: vanilla maps fan houses out from the castle: the first houses need the castle, each further
  house needs a nearby house. The `chain` op builds these (modes `tree` (default, minimum spanning tree from the
  castle), `nearest`, `order`).
- `upgradeCap`: how far above the castle's level this slot may be upgraded: `0` = never above the castle's level,
  `n` = up to n above, `-100` = no cap. The first build is never capped. Vanilla maps give almost every slot 0 (walls
  1): a building is never upgraded past the castle's level, which is what makes castle upgrades the pacing lever.
  Added slots copy their base's cap.
- Ops shorthand (palace.py): `"castle"` (castle built), `"castle:3"` (castle level 3), `"h1"` (slot h1 built),
  `"h1:2"` (h1 at level 2), `"start"` (from the start). Levels in the shorthand count from 1 = built, and `show`,
  `ref slots` and `test` reports use the same counting (`"needs": "h1:1"` = h1 built, a build result's `from`/`to` 0 =
  unbuilt, 1 = built). Only the raw pack JSON counts from 0 (`"level": 0` = built).
- `chain` decides the links itself (tree mode joins each slot to its nearest chained neighbour, not always the castle);
  `apply` prints every link (`h3 <- h6`), and a plan must buy a slot's prerequisite first.

### WaveSetDef (the nights)

```jsonc
{
  "id": "gorge-nights", "name": "...", "host": "Nordfels",    // host must equal the level's
  "startingGold": 10,                                            // day-1 gold; absent = the map's (ref maps)
  "nights": [{                                                   // one per night, in order; the run is won after the last
    "difficulty": 1.0,      // 0.1-100: HP x difficulty, damage x (1+difficulty)/2. Vanilla: 1.0 early, up to ~1.5-2 late
    "warning": "Flyers!",   // optional text shown as the night starts
    "spawns": [{            // groups; all run in parallel from nightfall
      "unit": "vanilla:E Melee",  // enemy ref (reference.md > Enemies) or a custom unit id in this pack
      "count": 6, "interval": 0.8, // enemies, seconds between them
      "delay": 0,                  // seconds after nightfall before the first
      "line": "north",             // spawn line (host line name or a level's added id)
      "gold": 6,                   // coins the whole group drops (not per enemy)
      "elite": false }]
  }],
  "troops": [{ "unit": "my-knight", "building": "Barracks" }]   // optional: custom player units replace vanilla troops
}
```

- Match units to the line: flyers from a line with `flying: true`, ground enemies from `ground: true`, large ones
  (`Large` in reference.md) from `large: true`. Pure flyer lines can sit off the ground (over water, cliffs); ground
  lines must be on walkable ground with a route to the castle.
- Each night needs at least one spawn. A spawn group from a line whose route is blocked never reaches anything and
  the night never ends: `check` and `paths` catch that.

### UnitDef (custom enemies; optional)

```jsonc
{ "id": "brute", "side": "enemy", "base": "vanilla:E Ogre", "name": "Brute", "description": "...",
  "visuals": { "tint": "#c9a227", "scale": 1.3 },
  "stats": { "maxHp": 300, "moveSpeed": 4, "attackCooldown": 1.6, "attackRange": 3, "keepDistance": 0, "aggroTime": 5 },
  "weapon": { "base": "inherit", "damageMult": 1.5,
              "damage": [{ "tags": [], "add": 6 }, { "tags": ["Building"], "mult": 3 }] },   // replaces the list
  "tags": { "add": ["ArmoredAgainstRanged"], "remove": [] },     // not Flying/LargeUnit: pick a base that has them
  "behaviours": [{ "type": "SpawnOnDeath", "unit": "vanilla:E Slime" }] }
```

Stats are absolute (difficulty, elite and perks still multiply them). Ranges: maxHp 1-100000, moveSpeed 0-100,
attackCooldown 0.05-120. Gold is set per spawn group, not per unit. `palace.py ref enemies` gives every base's stats
and damage list. Use custom units sparingly; vanilla enemies are well balanced and readable.

## 6. palace.py commands and ops

| Command | Does |
|---|---|
| `where` | paths, reference maps cached, bridge status |
| `ref build [--maps A,B] [--force]` | cache catalog + map dumps from the game (needed once) |
| `ref maps` / `ref slots <map> [--kind House]` / `ref buildings [--kind]` / `ref enemies [--player]` / `ref waves <map> [--raw]` / `ref scenery [map]` | reference data |
| `ref render <map> -o f.png` / `ref doc` | base map picture (with offline enemy routes) / regenerate reference.md |
| `new <level> --host M [--base map\|cleared\|flat] [--waves empty\|vanilla\|none] [--name N] [--force]` | a new library level (`--base flat` also runs a default `terrain_init`; `--force` replaces it). `level library <level> --host M ...` is the same; `new <pack>` without `--host` / `level <pack> ...` still make an old-style pack folder |
| `list` | the library: levels (map, nights, where they came from, whether a preview was saved) and enemies (base, which levels use them) |
| `export "<name>" <level,...> [--enemies a,b]` | through the game: a `.tfpack` with those levels, the extra enemies, and every enemy and asset they use (JSON: `written` path) |
| `apply <pack> ops.json [--level L] [--dry-run] [--fresh]` | run ops; saves; prints offline errors/warnings. `--fresh` first empties the level (nights kept) so the same file can be re-applied |
| `check <pack> [--live]` | offline checks; `--live` adds the game's validator (exit 1 on errors) |
| `show <pack> [level]` | the level as it will play: every slot, what it needs, costs, income, lines |
| `render <pack> [level] -o f.png [--labels names\|costs\|reqs\|none] [--live]` | top-down picture; routes in one colour per line, named, lengths in the legend; offline they're an estimate, `--live` = real game render + the game's routes (level loaded) |
| `economy <pack> [level] [--compare] [--report test.json]` | gold curve of a greedy all-economy player (assumes nothing is ever knocked out); vs the base map's vanilla; `--report`: what a saved `test` run actually received per dawn and which buildings were knocked out most |
| `waves <pack> [level] [--compare [Map]]` | per night: enemies, effective HP, gold, flyers; vs vanilla |
| `play <pack> <level>` | load as a normal run (real gold); prints load warnings |
| `sandbox <pack> <level> [--at X Z]` | Studio test play: unlimited gold, king at X,Z |
| `edit <pack> <level>` | open the level in the in-game Studio (the player's editor: F5 there test-plays); it edits its own copy and Ctrl+S writes pack.json, so don't `apply` while the player has unsaved Studio changes |
| `test <pack> <level> [--nights 1-3] [--plan p.json] [--carry] [--save] [--catchup play\|free\|none] [--free] [--build all] [--god] [--invulnerable] [--gold N] [--speed 3] [--shots dir] [--quiet]` | load, buy per plan each day once its gold is in, play nights, report each (one progress line per night on stderr) |
| `night [N] [--speed 3]` | start the next night (or jump to night N) and wait for its report |
| `build <slot> [level] [branch] [--pay]` | build/upgrade now (free unless --pay); `castle`, an added id, or a host name; exit 1 if refused |
| `spawn <unit> [count] [--elite] [--line L \| --at X Z]` | spawn enemies now |
| `cheat god\|invulnerable\|infinitegold [on\|off]`, `cheat gold N`, `cheat speed X`, `cheat kill`, `cheat heal`, `cheat endnight`, `cheat teleport X,Z` (or `X Z`; negatives are fine) | test helpers |
| `paths [--rescan] [--points]` | the game's own route from every ground line to the castle, on the graph its enemies use (`Enemy Units L` if the line spawns large ones): `length` m from the line itself, `at` (the line's middle), `start` (where that lands on walkable ground), `snap` (m between them) and a `warning` when the line is over 10 m off walkable ground; `--points` adds the waypoints `[[x, z], ...]` |
| `paths --between X1 Z1 X2 Z2` | whether each ground graph (small enemies, large enemies, the king's `Player Units S`) connects two points, and how far each point is from walkable ground |
| `report` | the current or last night's report |
| `shot -o f.png` | screenshot of the game window |
| `game status` / `game slots` / `game log` / `game menu` / `game cmd "<raw>"` | raw game access |
| `share <pack> [--clipboard]` / `share <pack> --export [-o f.tfpack] [--force]` | share code (JSON; the clipboard only with `--clipboard`) / .tfpack file (`written`: its exact path; without `-o` a new numbered file in the game's `exports/` folder) |

### Ops (JSON list; each `{"op": ..., ...}`; `"level"` optional when the pack has one level or with `--level`)

| Op | Fields |
|---|---|
| `level` | `name`, `description`, `base` (`cleared`/`flat`/null), `waveSet`, `host` |
| `add_slot` | `id`, `kind` (e.g. `"House"`, `"Defense Tower"`, `"Barracks"`; copies the host's slot of that kind) or `from` (a host slot name), `at` [x,z] or `pos`, `rotY`, `requires` (shorthand), `upgradeCap`, `costs` [per level, null keeps] |
| `move_slot` | `slot` (added id, host name, `castle` can't move), `at`/`pos`, `rotY` |
| `set_slot` | `slot` (or `castle`), `requires` (null = back to the map's), `upgradeCap`, `costs` |
| `remove_slot` | `slot` |
| `chain` | `slots` [ids/names], `mode` `tree`\|`nearest`\|`order`, `first` (requirement of the root(s), default `"castle"`), `every` N (order/nearest: every N-th link needs one more castle level instead) |
| `add_prop` | `mesh`, `at`/`pos`, `rotY`, `scale`, `solid`, `id` (auto) |
| `scatter` | `mesh` (name or list), `count`, shape (`circle`/`rect`/`line`/`polygon`), `spacing` (3), `avoid` (6 m from slots and lines), `scale` [min,max], `solid`, `seed` |
| `remove_prop` | `id` or `ids` |
| `add_spawn_line` | `id`, `at`/`pos`, `rotY`, `width`, `ground`, `large`, `flying` |
| `move_spawn_line` | `line` (added id or host name), `at`/`pos`, `rotY` (added lines also `width`, flags) |
| `remove_spawn_line` | `line`: an added id deletes it; a host line's name takes it off the map (`spawnLines.remove`); `restore: true` puts a removed host line back |
| `waves` | `startingGold`, `nights` [night objects; unit refs may omit `vanilla:`], `name` (creates/links the level's wave set) |
| `night` | `night` (1-based; = count+1 appends), plus night fields |
| `copy_waves` | `from` (map), `lines` {old name: new name} |
| `unit` | a whole UnitDef (adds or replaces) |
| `terrain_init` | `origin` [x,z], `cell` (2), `size` [W,H] cells (≤400), `y` (default castle height), `flat` (true: sets base flat; false: sculpt the map's own ground) |
| `terrain` | `mode` `set`\|`raise`\|`lower`\|`smooth`, `height` (m, relative to terrain y), shape, `falloff` (m); any base |
| `ramp` | `from` [x,z], `to` [x,z], `width`, `fromHeight`, `toHeight`, `falloff` |
| `paint` | `material` `ground`\|`high`\|`low`\|`earth`\|`sand`\|`road`\|`rock`, shape; painted per 1 m sample and drawn with smooth edges; any base |
| `block` | `on` (true), shape: ground units and the king can't enter; works on any base |
| `water` | `level` (m relative to y, default -1), `on`, shape; digs the ground to level-1 m. `on: false` makes the cells land again and raises the dug ground back to `height` (default 0; `fill: false` keeps it dug) |
| `set` | `path` (e.g. `"levels[0].waveSet"`), `value` (or `delete: true`): raw edit |

Shapes: `"circle": {"center": [x,z], "radius": r}`, `"rect": {"min": [x,z], "max": [x,z]}`,
`"line": {"points": [[x,z], ...], "width": w}`, `"polygon": {"points": [[x,z], ...]}`; `"falloff": m` softens edges.

### Plans (for `test`)

```json
{ "1": ["castle", "h1", "t1"],
  "2": ["h2", "mill", {"slot": "t1", "level": 2, "branch": 0}],
  "3": [{"slot": "castle", "level": 2, "branch": 1}, "bar"] }
```

Keys are days (the day before night N). Items are slot ids (one level up) or `{slot, level, branch}` (up to that
level, counting from 1 = built; `branch` picks a choice, 0-based). Each day `test` first waits until the dawn income
and the night's coins are in the treasury, then buys in list order with the run's gold. Write the plan a real player
would follow; it proves the economy works, not just the defence.

- A refused item (`"ok": false` with an `error`: not enough gold, requirement not met, castle level too low) is an
  error in the report, never silent. By default it's dropped. `"options": {"carry": true}` (or `--carry`, or
  `"carry": true` on one item) retries it at the start of the next day until it's bought.
- `"options": {"save": true}` (or `--save`, or `"save": true` on one item): when an item is refused for gold, buy
  nothing else that day, so the gold is kept for it (with carry, the rest of the day moves to the next day too). That's
  how a plan "saves up" for castle level 3 instead of spending the 20 g on houses.
- `--free`: purchases cost nothing (a defence-only probe; the economy isn't tested).
- `--nights N-M`: the earlier nights are played first (`--catchup play`, the default: the plan with real income, so
  night N starts with what a player would have; listed under `catchup`), or skipped: `--catchup free` builds the
  earlier days' items for free and jumps to night N, `--catchup none` jumps with nothing built (add `--gold X`).

```json
{ "options": {"carry": true},
  "1": ["castle", "h1", "h2"],
  "3": [{"slot": "castle", "level": 2, "branch": 0, "save": true}, "t1", "h3"] }
```

### Reading a night report

The `test` output: `result` (`victory`, `survived` (every requested night, not the last), `defeat`, `timeout`),
`refusedPurchases`, a `summary` row per night (outcome, castle low, knock-outs, bought, refused, gold after dawn,
income paid), then the full `nights` reports. Each night report:

- `outcome`: `dawn` (survived), `victory` (survived the last night), `defeat` (castle fell), `timeout` (enemies still
  alive: stuck or unreachable; see `timeoutStatus`).
- `bought`: the purchases made on the day **before** this night, one entry per plan item, always the same shape:
  `{slot (the plan's id: an added slot's bare id, castle, or a host name), name (the game object), kind, from, to,
  target, spent, gold (after it), ok, error}`; `plannedDay` when a carried item was bought later.
- `castle.minHp`/`maxHp`, `buildings.knockedOut` (names; they pay nothing the next dawn), `units` standing,
  `king.knockouts`, `enemies.seen`, timed `events`.
- `gold`: `atDusk` (as the night began, after the day's purchases), `atEnd` (as it ended, before dawn), `afterDawn`
  (the next morning once income and the night's coins are in: what the next day can spend).
- `income`: the next dawn's `nominal` income (every building), `blocked` (knocked out that night) and `paid`.
- `after`: the game status at that same moment (the next morning; the end screen after the last night).

The king does nothing during `test` (he stands where he was), so a defence that holds in `test` holds without the
player's help; a real player adds his own damage (roughly one strong unit). Use `--god` to keep the king alive,
`--invulnerable` to test pathing alone.

## 7. Terrain

A flat base (`"base": "flat"`) replaces the map's ground with a heightfield you sculpt; the sky, light and castle
position stay the host's. The castle keeps its host x/z and stands on whatever height you give the ground there;
slots snap to the ground too. On any other base (`map`, `cleared`) the same ops sculpt **the map's own ground**: every
cell you move off the host's ground is replaced by the terrain's (the host's ground is cut out under it, so lowering
and digging work as well as raising); untouched cells stay the host's own.

- Grid: `origin` [x,z] is the min corner, `cell` metres (default 2), `size` [W,H] cells (each ≤ 400). Corner heights,
  metres relative to `y` (default: the castle's height). Heights reach from world y -8 (the king respawns below -10)
  up to **30 m above `y`** (at least world +30), so a high host like Sturmklamm (castle y 31) can still build up.
  reference.md > "What the host decides" lists each host's headroom. A flat field's edge is a wall. `terrain_init`
  without origin/size covers the map's walkable area.
- On a map's own ground the pack stores heights as **offsets from the host's ground** (0 = untouched). `raise`/`lower`
  are exact; `set`, `ramp` and `water` aim at a height relative to `y`, worked out from the reference's sampled ground
  (a few cm off where the host's ground is uneven). `check`/`render` don't model sculpted host ground yet: confirm
  routes with `paths` in game. Fliers fly over the host's own flier surface there, not over raised ground.
- Heights: `terrain` with `set` (flat plateau/basin to a height), `raise`/`lower` (add), `smooth`; `ramp` for slopes.
  Ground steeper than the graphs' max slope (30°) is a cliff: unwalkable for ground units **and the king** (it's
  walled off for him too, so he can't slide down a face he can't climb back up), drawn as rock. A plateau with one
  ramp is a choke point; fliers ignore it. In the Studio the Ramp brush is dragged from the top to the bottom: the
  ground previews while you drag and the guide turns red past 30°.
- **Width matters.** The game paths ground units on two graphs: small enemies and the king keep 0.5 m from any
  obstacle, large enemies (ogres, rams, big spiders; every spawn line with `large: true` is path-checked for them)
  keep 4 m. Water and blocked cells, cliff faces steeper than 30° and the field's edge are obstacles, and a dug water
  edge is a slope too, so a causeway needs more width than a ramp. Measured in game (`paths --between`), by the
  `width` of the op's shape:

  | Passage | Small enemies, king | Large enemies |
  |---|---|---|
  | Water causeway, straight (along x or z) | closed ≤ 3, open from 6 (4-5 depends on the grid) | closed ≤ 9, open from 12 (10-11 depends on the grid) |
  | Water causeway, diagonal | closed ≤ 5, open from 6 | closed ≤ 11, open from 14 (12-13 depends on the grid) |
  | Ramp between cliffs, straight | open from 4 | closed ≤ 9, open from 11 (10 depends on the grid) |
  | Ramp between cliffs, diagonal | open from 4 | closed ≤ 8, open from 11 (9-10 depends on the grid) |

  Causeways: a water ring (dug, the default), then `water` `on: false` along a `line` of that width (it raises the
  dug ground back to height 0, like a `terrain` `set` 0 over the same line). Ramps: a 6 m plateau (`falloff` 1) and a `ramp` (`falloff` 1). "Depends on the grid": shapes fill
  whole 2 m cells, so where the edges fall on the grid moves the threshold. For a lane only small enemies use, make
  it 6-8 m wide; for one everyone uses, 14 m+. `check` and `render` model the same rules offline (they agreed with
  the game on 140 of 144 measured passages, and erred towards "closed"); confirm with `paths` or
  `paths --between X1 Z1 X2 Z2` in game.
- Example (tested): `{"op": "terrain", "mode": "set", "height": 6, "circle": {"center": [-4, 1], "radius": 22},
  "falloff": 1}` then `{"op": "ramp", "from": [-4, -18], "to": [-4, -40], "width": 14, "fromHeight": 6, "toHeight": 0,
  "falloff": 1}` on a flat Nordfels: a castle mesa with one southern ramp.
- The field is drawn in the host's own ground palette, which is not always green: teal on Nordfels, dark blue on
  Moorweg, pink on Wildbach, near-black on Totend, orange on Freifort, white on Frostsee (the vanilla maps look the
  same). Look at the host first if colour matters.
- `paint`: looks only, painted per paint sample (2 per cell edge: 1 m on the default grid) and drawn as smooth
  outlines, so a 4 m road is a road, not a staircase of 2 m squares. All seven are distinct on every host: `ground` the host's grass (the unpainted field), `high` a
  lighter and `low` a darker grass shade, `earth` a bank colour, `sand` the host's sand, `road` a path that stands out
  from the grass, `rock` the host's pale cliff colour. `block`: invisible no-walk cells (works on any base, also on
  vanilla ground). `water`: unwalkable cells drawn as water, the ground dug under it to `level` - 1 m; `on: false`
  makes the cells land again and raises the dug ground back to `height` (default 0; `"fill": false` keeps it dug).
  The shore is drawn where the bank meets the water, not along the cell edges.
- Format: `terrain.format` 2 (written by the game and `palace.py`) stores `materials` per paint sample with
  `paintRes`; older packs (one material per cell) are read and upgraded on their next save.
- On a flat base the host's own rivers, lakes and ponds are hidden (only the field's `water` cells show); a sea all
  round the map (Frostsee) stays round the field. On `cleared` the host's water stays.
- Each level stores terrain compressed in the pack (a few KB); big terrain levels share as `.tfpack` files rather
  than chat codes.
- Check routes after every terrain change (`check`, then `paths` in game): a ramp too steep or a missed gap in a
  cliff ring changes everything.

## 8. Reference numbers (summary; full tables in reference.md)

Buildings (cost per level; +income per dawn):

| Kind | Levels (cost) | Income | Notes |
|---|---|---|---|
| Castle Center | 3 / 7 / 20 | - | lose condition; shoots from L2; each level a perk choice; gates most of a map |
| House | 2 / 2 | +1 / +1 | the basic economy; cheap, fragile (20 HP) |
| Mill | 3 / 4 / 6 | +1 / +1 / +4 | unlocks its Fields (1 g each, +1); fields follow the mill |
| Gold Mine | 5 | +6, falling each dawn | early burst |
| Harbour | 3 / 7 | grows each dawn to a cap | slow, big |
| Defense Tower | 3 / 5 / 15 | - | 36 m range; L2 and L3 are choices (Castle/Sniper/Armored/Bunker; Archer/Ballistic/Fire/Healing spires) |
| Barracks | 4 / 8 / 16 | - | melee squad (Knights / Spearmen / Flails / Berserkers) |
| Archery Range | 4 / 8 / 16 | - | archer squad (Longbow / Crossbow / Hunters / Fire) |
| Hero's Quarter | 6 / 10 / 20 | - | a hero squad (Golem / Support Mage / Firewing / Lizard Rider) |
| Wall / Gate | 4 / 12 (gate pieces vary) | - | blocks ground enemies; gates let your units through |
| Blacksmith / Royal Forge | 4 / 9 / 16, 4 / 7 / 14 | - | global unit upgrades |
| Temple | 2 / 6 / 25 | - | night-wide spells |
| Shrine | 3 | - | 777 HP decoy/buff |

Vanilla maps for scale: 12-15 nights; starting gold 7-23; 10-18 houses, 2-5 mills, 10-31 towers, 2 barracks, 2
archery ranges per map; an all-economy player (`economy`, an upper bound: real players also buy defence, and mines decline) reaches 13-60 gold per dawn by day 4 and 40-90 by day 6. Night 1 is 3-20 weak enemies
(0.1-0.9k effective HP); nights grow roughly 1.3x per night with a dip every 3-4 nights and a final night 2-4x the
one before it (Nordfels: 0.1k → 0.5k by N4 → 2.9k N10 → 6.8k N13). Flyers appear from about night 4-7.

## 9. Designing a good level

**Shape and readability**

- Give the castle a clear centre. Build outward in rings: houses close (protected), economy behind defence, towers
  and walls on the approaches, the far reaches (mines, extra mills) as risky late expansions.
- 2-4 ground entrances. Each entrance is a lane the player learns to defend; make them visibly different (a long
  open road, a narrow bridge, a winding forest path). Name your spawn lines after the place ("north-pass").
- Choke points are the heart of Thronefall: a narrow gap (cliffs, water, rock props) where towers and a wall can
  hold. Give each lane one good choke and one tempting place to over-extend.
- Keep enemy routes 80-200 m from spawn to castle (vanilla range). Under ~60 m the player has no time; over ~250 m
  nights drag.
- Flyers ignore terrain: put flyer lines over cliffs or water, and keep some economy where they will cross so towers
  near it matter.
- Leave room: the king must reach every slot; slots 5-7 m apart; don't bury slots in solid props.

**Level shapes**

The tools support many kinds of level; pick the one that serves the idea instead of a default. Some shapes, with the
tools that make them:

- *Open field*: few obstacles, lanes made by distance and enemy mix; wide approaches, towers and units do the work.
- *Valley or canyon*: `terrain` plateaus and cliffs (`ramp` for the ways up) funnel lanes into chokes.
- *Highland keep*: the castle on a mesa with one or two ramps; the economy sprawls below where it's exposed.
- *River or delta*: `water` bands with fords and bridges (`water on:false` lines) decide the crossings.
- *Island or causeways*: the castle ringed by water, joined to the shore by a few causeways.
- *Forest maze*: solid props (`scatter`) wall off lanes; gaps in the trees are the chokes.
- *Split kingdom*: two or three economic districts in different directions, each with its own lane to hold.
- *Long road*: one long winding route (or two) with layered defences along it, fliers to punish over-commitment.
- *Remix*: `base: map` keeps a vanilla map and reshapes it: remove, move and re-price slots, add lines.

Mix them, and let the host's palette and shape suggest one. Independent designs tend to converge on the castle
island with three causeways; if that's where yours is heading, check whether another shape fits the idea better.

**Economy vs defence**

- The core decision each day is "grow or defend". Make both possible every day: early nights must be beatable with
  ~half the gold, so the rest can go into houses.
- Starting gold ~ castle (3) + 2-4 houses or one tower. Vanilla: 7-23.
- A realistic player (half the gold into economy) should reach about 10-25 per dawn by night 4 and 30-50 by night 8. Run
  `economy --compare`: your curve should sit near the host's vanilla one, unless the level is deliberately lean.
- Gate expansions with castle levels (2 and 3) and with chains, so the map unfolds over the first 4-6 nights.
- Put the best economy where it's exposed (far mills, mines on an entrance) so greed has a cost. Mind the targeting
  rule (section 3): a building that is the first thing on a lane is hit every night, so it rarely pays at all. Give
  exposed economy some cover (a tower in range, a wall in front), and check the real income with
  `economy --report test.json` rather than `economy`'s all-standing curve.
- Every enemy gold drop matters early (5-15 per night in vanilla); late nights drop 15-60.

**Nights**

- Teach, then test: night 1 tiny (3-8 weak melee from one lane), night 2-3 a second lane or archers, a flyer night
  around 4-7, mixed lanes from mid game, a big final night (2-4x the previous). Vary: a quiet night after a hard one.
- Stagger groups with `delay` so a night has phases (opening, main push, a late surprise); spread lanes so the player
  splits his defence. Long `interval` streams are easier than bursts.
- Difficulty multiplier 1.0-1.2 early, 1.3-2 late; prefer more or tougher enemies over huge multipliers.
- Bosses (`E Eldermole`, `E Thing`, `E Big Spider`) once, late. Elite groups are spice, not the meal.
- Compare `waves --compare`: effective HP per night vs vanilla tells you if you're far off.
- Number of nights: 8-10 for a short level, 12-15 like vanilla. Fewer, denser nights for small maps.

**Testing checklist**

1. `apply` / `check`: zero errors. Warnings understood.
2. `render --labels reqs`: requirement arrows make sense (everything reachable from the castle, nothing orphaned).
3. `play`, then `paths`: every ground line reaches the castle (the game's own check).
4. `test --plan` with a realistic plan for all nights: survives with some damage early, close calls late. If night
   1-3 are lost, they're too hard; if nothing is ever knocked out, too easy.
5. `test --nights N --plan` to probe a late night: it plays the earlier nights first, so night N starts with a real
   player's kingdom (slow; `--catchup free` jumps there with the earlier days built for free instead).
6. Screenshots (`--shots`) of the first and last night: does it read well?

## 10. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `The Agent bridge isn't on` | enable it in Settings > Mods > Builder > Agent bridge; the game must be running |
| `The game isn't running` / `hasn't updated the Agent bridge` | the game was closed (start it again; the bridge stays on), or it's loading or frozen |
| `slot 'x' already exists` on a second `apply` | ops aren't idempotent: `apply --fresh ops.json` |
| a plan item `isn't available yet` | its prerequisite isn't built yet: check `apply`'s chain links or `show`, buy the prerequisite first |
| `test` says the economy is fine but the run starves | buildings knocked out pay nothing: `economy --report test.json` |
| `No reference data yet` | `ref build` (game running) |
| `the game's validator found errors` | read the listed `issues` (JSON paths into the pack) |
| a slot never appears | its prerequisite isn't on the level, isn't reachable, or needs a castle level that's never bought; `show` lists `needs` |
| night `timeout` | enemies can't reach anything: blocked lane (props, terrain, blocked cells) or a spawn line off the ground; `paths` |
| load warnings in `play` | names that don't exist on the host (slot, line, mesh); fix the ops |
| buildings float or sink | an added slot's y is off the ground; use `at` instead of `pos` |
| king can't reach a slot | the slot is on unwalkable ground (cliff, water, inside props) |

## 11. Worked example

See `examples/` in this folder: `watchpost.ops.json` builds a complete small level on a cleared Nordfels from nothing,
`watchpost.plan.json` is its test plan, and `watchpost.md` lists the exact commands and what they printed.
