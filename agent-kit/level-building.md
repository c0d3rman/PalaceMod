# Building Thronefall levels with PalaceMod: the agent guide

You are building a custom level for **Thronefall** (a minimalist tower-defence / kingdom builder) with the **PalaceMod**
Builder. This guide plus `palace.py` (next to it) is everything you need: the data format, the game's rules and numbers,
the tools, and how to test. `reference.md` holds the generated tables (maps, buildings, enemies, vanilla curves,
scenery); `palace.py ref ...` prints the same data as JSON. Levels and enemies can also be **programmed** (rules, custom
projectiles, areas, statuses, signals, variables): section 12, with every piece listed in `logic-reference.md`.

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
   would, plays the nights at 3x speed in fast simulation, reports each night), or `play` / `sandbox` and drive it
   step by step. After editing only later nights' waves, `test ... --from-night N` replays just nights N.. .
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
- **Fighting is a night thing, building a day thing.** The game's own attacks (towers, troops, houses, the castle,
  enemies and the king's weapon) only fire at night: by day an enemy can stand next to a building and hurt nothing,
  and towers stay idle. Building and upgrading are refused at night ("Only during the day"). A night ends the moment
  its spawning is done and no enemy is left, so anything a level wants to do "after the wave" must happen before the
  last enemy dies or hold the night open (section 12.16).
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
- Props can also tilt: `rotX` tips one about its own sideways axis (+ leans its front down; 90 lays an upright piece
  on its back, e.g. a banner or insignia as a floor emblem), `rotZ` rolls it onto its side. Both are degrees
  (-180..180), applied before `rotY` (so `rotY` stays its heading), and left out at 0. A solid prop's blocker tilts
  with it; offline checks use the tilted bounding box's footprint, centred on the prop's position (a mesh whose pivot
  is at its base sticks out further on one side; check a large tilted solid prop with `paths`). Lower a half-sunk
  piece with `"pos": [x, y, z]`.
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
  "colors": "Frostsee",            // another main map's colour scheme for the whole level (ground, scenery,
                                   // buildings, light); absent = the host's
  "buildSlots": {
    "remove": ["House Variant (3)"],               // host slots to take away (slots they unlock never appear)
    "edit": [{ "ref": "Mill Variant",              // a host slot by name (ref slots <map>)
               "pos": [x,y,z], "rotY": 0,           // move it (the castle can't move)
               "requires": { "slot": "castle", "level": 1 },   // see Requirements; absent = keep the map's
               "upgradeCap": 0,                     // see Requirements; absent = keep
               "units": [[-3, 2], null, [3, 2]],    // training slots: where each unit stands (below); absent = the map's
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
  "props": [{ "id": "rock-1", "base": "mesh:Rock A", "pos": [x,y,z], "rotY": 0, "scale": 1.0, "solid": true,
              "rotX": 90, "rotZ": 0,   // optional tilt (absent = 0): rotX tips it forward, rotZ rolls it
              "colors": "Totend" }],   // optional: keep that map's colours (absent = the level's)
  "terrain": { ... }                                  // section 7
}
```

- On a **cleared/flat** base only the castle (and its own add-ons) remain; host spawn lines stay where they are
  (on flat they may sit outside your ground) until you remove them. Add your own lines, then take off the host's
  you don't use (`remove_spawn_line`), or they keep showing in `paths`, renders and the minimap. A wave set that
  still spawns from a removed line is an error, and so is removing a line the map's own nights use while the level
  has no wave set. Every slot you add defaults to "needs the castle built" when you use ops.
- **The host still decides a lot**, even on `flat`: `add_slot` copies the host's own slot of a kind when it has one, and
  places the game's standard building of a kind it lacks (base `building:<kind>`, e.g. a Mill on Frostsee; it needs
  the castle built unless you say otherwise), the castle position, the playable rectangle
  (`terrain_init`'s default grid), the light and ground palette, and how much room the terrain has above the castle
  (heights are world heights, clamped to [-8, 30] m: Sturmklamm's castle sits at 31 m, so nothing there can rise above
  it). `reference.md` > Maps > "What the host decides" has the kinds-by-host table and each host's rectangle and
  headroom. Choose the host from it before designing.
- `base: null` (the map as it is) keeps all vanilla slots, scenery and routes; you then remove, move, re-price and
  add. Good for remixes; for original designs use `cleared` or `flat`.
- Added slots are named `PalaceBuilder <id>` in the running game; `palace.py build`/plans accept the bare id.
- **Training slots' units** (barracks, archery range, hero's quarter...): each unit stands at an offset from its
  building that moves and turns with the slot: a copy placed on a river bank can put them in the water. `units` on an
  edit or add sets them: one `[x, z]` per unit spot in the order the reference lists them (`ref slots <map>` shows the
  map's as `units`; each upgrade choice's units, Knights or Spears, share the same spots), metres from the slot's centre in its own frame (x right, z forward); `null` entries keep the
  map's. Whatever you set, play moves any unit home that is off walkable ground to the nearest free ground on its building's side of
  any water at level start, keeping them spread out; `check` warns about units off the ground, and `set_units` places them (`at` takes
  world points).
- Props: `mesh:<name>` from the map's own scenery or the cross-map scenery library (`ref scenery`, reference.md);
  `mesh:<name>@<Map>` picks one map's mesh. `solid: true` props block units (pathfinding is rebuilt at level start);
  `scale` 0.1-10, relative to how the map itself uses the mesh (a map's own rocks are often scaled up: "Rock A" on
  Nordfels is ~30 m across at scale 1), so check sizes with `render --live`. Props are decoration and walls of rock;
  slots are the gameplay.
- Colours: every map recolours the same materials, so a library prop takes the level's colours (a Totend tree on
  Frostsee is Frostsee-coloured). `colors: "<Map>"` on a prop keeps that map's colours (the ops' `"colors":
  "original"` picks the map the mesh comes from); `colors` on the level gives the whole level another map's scheme
  (`{"op":"level","colors":"Totend"}`). Maps: Neuland and the nine main maps.

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
  (`Large` in reference.md) from `large: true`. Pure flyer lines can sit off the ground (over water, cliffs), but
  only over the fliers' surface: the host's own (roughly where its own flyer lines are and the land between) or, on
  a `flat` base, the field. A flier spawned beyond it starts at the surface's nearest edge, not on the line (`paths`
  lists each flier line's distance under `air`; `check` warns for a flat field). Ground lines must be on walkable
  ground with a route to the castle.
- Each night needs at least one spawn. A spawn group from a line whose route is blocked never reaches anything and
  the night never ends: `check` and `paths` catch that.

### UnitDef (custom enemies; optional)

```jsonc
{ "id": "brute", "side": "enemy", "base": "vanilla:E Ogre", "name": "Brute", "description": "...",
  "visuals": { "colors": { "body": "#c9a227" }, "scale": 1.3 },
  "stats": { "maxHp": 300, "moveSpeed": 4, "attackCooldown": 1.6, "attackRange": 3, "keepDistance": 0, "aggroTime": 5 },
  "weapon": { "base": "inherit", "damageMult": 1.5,
              "damage": [{ "tags": [], "add": 6 }, { "tags": ["Building"], "mult": 3 }] },   // replaces the list
  "tags": { "add": ["ArmoredAgainstRanged"], "remove": [] },     // not Flying/LargeUnit: pick a base that has them
  "rules": [{ "when": "death", "do": [{ "do": "spawn", "entity": "vanilla:E Slime", "at": "self", "elite": "inherit" }] }] }
```

`rules` is the behaviour engine (see "Behaviour"): that one rule is "spawns a slime where it dies". Packs of version 2
had `"behaviours": [{ "type": "SpawnOnDeath", "unit": ... }]`; reading them turns it into exactly that rule.

**Colours.** `visuals.colors` replaces colours outright, per part: `"body"` is the unit's body (the enemy colour, all
of it), any other key is one piece of the model (`"Bow"`, `"Helmet"`: its renderer's name) or one of its materials
by name; the most specific wins. `builder-enemy parts` through the bridge lists the pieces, their materials and colours
while the Enemy Studio is open. The lit side becomes the colour and the shaded side follows it the way the
level's colour scheme pairs them; elites keep it with the elite outline. `visuals.tint` only multiplies the body's lit
colour (a subtle shift): use `colors` for a new colour.

```jsonc
"visuals": { "colors": { "body": "#3a6ee8", "Bow": "#e8c23a" } }   // an archer base: blue, with a gold bow
```

**Ranged attacks.** A unit attacks with its weapon: a ranged one fires a projectile that homes on its target, a melee
one hits at once. To make a ranged enemy, start from a ranged base (`ref enemies`: archers, mages) or set
`weapon.base` to a ranged unit's weapon (`ref enemies` lists each base's weapon). Then `stats.attackRange` is how far
it picks targets (metres), `weapon.projectileSpeed` how fast its shots fly, `stats.attackCooldown` the seconds between
shots, `weapon.damage` the damage by target, and `stats.keepDistance` how close it walks before it stops (keep it
below the range, or it stops out of reach). What it targets and how it moves come from the base unit (each vanilla
enemy has its own target lists); they aren't editable yet.

Stats are absolute (difficulty, elite and perks still multiply them). Ranges: maxHp 1-100000, moveSpeed 0-100,
attackCooldown 0.05-120. Gold is set per spawn group, not per unit. `palace.py ref enemies` gives every base's stats
and damage list. Use custom units sparingly; vanilla enemies are well balanced and readable.

**Any model as the body.** `visuals.mesh` replaces the base's model (the base still supplies the animations, the AI,
the hit box and every stat you don't set):

```jsonc
"visuals": {
  "mesh": "mesh:Cone Tree",          // a scenery mesh (`palace.py ref scenery`: any map's, drawn in the level's colours),
                                   // "vanilla:P Knight" (another unit's model, `ref enemies [--player]`),
                                   // or "asset:<key>" (an OBJ/glTF in the library's assets)
  "meshScale": 0.8,                // omit to fit it to the base's height
  "meshRotY": 180, "meshRotX": 0, "meshRotZ": 0,   // degrees; meshRotY faces it the way it walks
  "meshOffset": [0, 0.2, 0],       // metres after the fit: right, up, forward
  "meshLook": "own",               // "own" colours (default for mesh:/vanilla:) or "unit" (the enemy material: takes
                                   // tint and the elite look; default for asset:)
  "motion": "auto"                 // "auto"/"bob": a model without its own animation hops and waddles as it walks; "none"
}
```

A tall thin model (a tree) on a small base reads well with `meshScale` omitted; pick a base whose size and attack fit
the model (a Large base for a big one).

**Numbers and testing.** `palace.py enemy <id>` prints what the enemy is as it spawns: hit points, speed, damage per
hit and per second against a house, a wall, a tower, the king and each troop, and how long each troop takes to kill
one (`--difficulty D`, `--elite`). `--studio` opens it in the game's Enemy Studio (an arena on Nordfels), `--fight N`
then fights N copies against swordsmen and archers and reports who won, the damage each way and the first and last
kill; `--portrait` saves its library picture. The player edits enemies the same way in the Enemy Studio (from the
library's Enemies tab, or from a level in the Studio), so keep them in the library format.

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
| `check <pack> [level] [--live]` | offline checks (of that level and its nights only, if given); `--live` adds the game's validator (exit 1 on errors) |
| `show <pack> [level]` | the level as it will play: every slot, what it needs, costs, income, lines |
| `render <pack> [level] -o f.png [--labels names\|costs\|reqs\|none] [--live]` | top-down picture; routes in one colour per line, named, lengths in the legend; offline they're an estimate, `--live` = real game render + the game's routes (level loaded). Also draws `logic.places` (templates and presets resolved): areas in their shape and `color` (dashed when `show: none`), paths with direction chevrons, bodies' footprints (a mover's path, other poses and a spin's reach dashed), points and logic objects as diamonds; labelled by `name` or `id`, "(off)" when `active: false` |
| `economy <pack> [level] [--compare] [--report test.json]` | gold curve of a greedy all-economy player (harbours grow and mines run dry each dawn as in the game; assumes nothing is ever knocked out); vs the base map's vanilla; `--report`: what a saved `test` run actually received per dawn and which buildings were knocked out most |
| `waves <pack> [level] [--compare [Map]]` | per night: enemies, effective HP, gold, flyers; vs vanilla |
| `play <pack> <level>` | load as a normal run (real gold); prints load warnings |
| `sandbox <pack> <level> [--at X Z]` | Studio test play: unlimited gold, king at X,Z (the result has `"at": "ok"` once he stands there) |
| `edit <pack> <level>` | open the level in the in-game Studio (the player's editor: F5 there test-plays); it edits its own copy and Ctrl+S writes pack.json, so don't `apply` while the player has unsaved Studio changes |
| `test <pack> <level> [--nights 1-3] [--plan p.json] [--carry] [--save] [--catchup play\|free\|none] [--free] [--king castle\|<slot>\|X,Z] [--build all] [--god] [--invulnerable] [--gold N] [--speed 3] [--no-fast] [--from-night N] [--no-states] [--shots dir] [--quiet]` | load, buy per plan each day once its gold is in, play nights, report each (one progress line per night on stderr); fast simulation unless `--no-fast`; saves a resume state each dawn for `--from-night` |
| `night [N] [--speed 3]` | start the next night (or jump to night N) and wait for its report |
| `build <slot> [level] [branch] [--pay]` | build/upgrade now (free unless --pay); `castle`, an added id, or a host name; exit 1 if refused |
| `enemy <id> [--difficulty D] [--elite] [--studio] [--fight N] [--portrait]` | a library enemy's numbers (offline: HP, damage per hit/second against buildings, the king and troops, troops' time to kill it); with the game: open it in the Enemy Studio, fight N copies in its arena, save its portrait |
| `spawn <unit> [count] [--elite] [--line L \| --at X Z]` | spawn enemies now |
| `cheat god\|invulnerable\|infinitegold [on\|off]`, `cheat gold N`, `cheat speed X`, `cheat kill`, `cheat heal`, `cheat endnight`, `cheat teleport X,Z` (or `X Z`; negatives are fine) | test helpers |
| `paths [--rescan] [--points]` | the game's own route from every ground line to the castle, on the graph its enemies use (`Enemy Units L` if the line spawns large ones): `length` m from the line itself, `at` (the line's middle), `start` (where that lands on walkable ground), `snap` (m between them) and a `warning` when the line is over 10 m off walkable ground; `--points` adds the waypoints `[[x, z], ...]`. `air`: each flier line's distance from the fliers' surface (at its worst point) and where its fliers start; `problems`: what the last rescan left wrong (an empty graph, a ground line with no route, a flier line off the surface; the game also logs these as a warning, and `check --live` fails on them while the level is loaded) |
| `paths --between X1 Z1 X2 Z2` | whether each ground graph (small enemies, large enemies, the king's `Player Units S`) connects two points, and how far each point is from walkable ground |
| `report` | the current or last night's report |
| `game cmd "savestate save <name>"` / `"savestate load <name\|last\|quick>"` / `"savestate list"` / `"savestate dump"` / `"retrynight load"` | savestates of the level being played (`play`, `test`, `sandbox`): capture a moment (e.g. the dawn before the finale) and load it back to retest from there without replaying the run. Filed per level, not per host map. A load reloads the level (about a second; `dump` says `restoring=False` when done) with its terrain, layout, custom enemies and night progress. It refuses if the level's night count or build slots changed since the save; other edits (later nights' enemies, props, terrain) load |
| `shot -o f.png` | screenshot of the game window |
| `game status` / `game slots` / `game log` / `game menu` / `game cmd "<raw>"` | raw game access |
| `share <pack> [--clipboard]` / `share <pack> --export [-o f.tfpack] [--force]` | share code (JSON; the clipboard only with `--clipboard`) / .tfpack file (`written`: its exact path; without `-o` a new numbered file in the game's `exports/` folder) |

### Ops (JSON list; each `{"op": ..., ...}`; `"level"` optional when the pack has one level or with `--level`)

| Op | Fields |
|---|---|
| `level` | `name`, `description`, `base` (`cleared`/`flat`/null), `waveSet`, `host`, `colors` |
| `add_slot` | `id`, `kind` (e.g. `"House"`, `"Defense Tower"`, `"Barracks"`; copies the host's slot of that kind, or places the game's standard one, base `building:<kind>`, when the host has none) or `from` (a host slot name), `at` [x,z] or `pos`, `rotY`, `requires` (shorthand), `upgradeCap`, `costs` [per level, null keeps] |
| `move_slot` | `slot` (added id, host name, `castle` can't move), `at`/`pos`, `rotY` |
| `set_slot` | `slot` (or `castle`), `requires` (null = back to the map's), `upgradeCap`, `costs` |
| `set_units` | `slot`, `units` [[x,z] in the slot's frame, null keeps that one] or `at` [[x,z] world points]; `units: null` = back to the map's |
| `remove_slot` | `slot` |
| `chain` | `slots` [ids/names], `mode` `tree`\|`nearest`\|`order`, `first` (requirement of the root(s), default `"castle"`), `every` N (order/nearest: every N-th link needs one more castle level instead) |
| `add_prop` | `mesh`, `at`/`pos`, `rotY`, `rotX`/`rotZ` (tilt), `scale`, `solid`, `colors` (a map or `original`), `id` (auto) |
| `set_prop` | `id` or `ids`, then any of `at`/`pos`, `rotY`, `rotX`, `rotZ`, `scale`, `solid`, `colors` (null: the level's) |
| `scatter` | `mesh` (name or list), `count`, shape (`circle`/`rect`/`line`/`polygon`), `spacing` (3), `avoid` (6 m from slots and lines), `scale` [min,max], `solid` (default true: solid scatter can close a lane; `false` for decoration), `colors`, `seed` |
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
| `set` | `path`, `value` (or `delete: true`): raw edit. `level.` starts in the op's level (`"level.colors"`), `nights.` in its wave set (`"nights.startingGold"`); `levels[i]`/`waveSets[i]` index the whole pack, and in the library (every level) must be the op's own |

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
- `--king WHERE`: where the king stands every night: `castle` (just in front of it), a slot id (just in front of that
  slot, on the castle side) or `X,Z`. A plan's `"king": {"4": "t1", "8": [-40, -10]}` moves him from that night on.
  He's put back there before every night (a king knocked out wakes at the castle); each night report's `kingPost`
  says where he was asked to stand and where he landed. Default: he stays where he is (at the castle after loading).
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

**Test fidelity and speed.** `test` runs in *fast simulation* by default: every frame advances the game by the same
fixed step a smooth 60 fps game sees at `--speed` (1/60 s x 3 at the default 3x), and frames run back to back as fast
as the computer allows, without drawing them (the game window shows a picture about 10 times a second). A 5-night
test takes under a minute instead of several. The step matters: bigger steps (a high `--speed`, or a game held to a
low frame rate because it's in the background or on battery) make defences do measurably better than in real play,
so keep `--speed` at 3 or less (5 still matched real time in our tests; 8 did not). `--no-fast` plays in real time
at `--speed` (for watching). Screenshots in fast mode are a render of the game camera: the 3D view without the HUD.
The game has some randomness of its own; for a close call, run the test two or three times.

**Resuming from a later night.** Every full `test` saves a savestate at each dawn (once that morning's gold is in,
before buying). `test ... --from-night N` loads the one before night N and plays only nights N.. with the current
plan's days N.., so after changing night 8's waves you replay nights 8-10 instead of all ten. It refuses (and says
why) when the state no longer matches: the level itself (terrain, slots, props, spawn lines, costs), the waves of
nights 1..N-1 (or starting gold), a library enemy used in those nights, the plan's days 1..N-1 (or its options and
king posts), or the flags `--free --build --gold --god --invulnerable --king --carry --save` changed since. Waves
of nights N.. (and enemies only they use) and the plan from day N on may change freely. The report's
`resumedFrom.earlierNights` has the earlier nights from the run that saved the state. `--force-resume` uses a stale
state anyway; `--no-states` skips saving them. It needs savestates to work in Builder levels (a game that can't save
them says so once in the report's `warnings`).

The king does nothing during `test` (he stands where he was, or where `--king` put him), so a defence that holds in `test` holds without the
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
  Ground steeper than the host's walkable slope is a cliff: unwalkable for ground units **and the king** (it's
  walled off for him too, so he can't slide down a face he can't climb back up), drawn as rock. The slope is the
  host's own (reference.md, "What the host decides": 20° on Frostsee, 30° on Nordfels, 40-50° elsewhere), so the
  same ramp can be a road on Totend and a cliff on Frostsee. A plateau with one ramp is a choke point; fliers ignore
  it. In the Studio the Ramp brush is dragged from the top to the bottom: the ground previews while you drag and the
  guide turns red past the host's slope.
- **Width matters.** The game paths ground units on two graphs: small enemies and the king keep 0.5 m from any
  obstacle, large enemies (ogres, rams, big spiders; every spawn line with `large: true` is path-checked for them)
  keep the host's large-enemy clearance: **2 m on most hosts, 4 m on Nordfels, 1.5 m on Durststein** (reference.md,
  "What the host decides"). Water and blocked cells, every cliff face (up or down: a raised ramp's drop-offs count
  like the cliffs of a cut) and the field's edge are obstacles, and a dug water edge is a slope too. Measured in game
  (`paths --between`), by the `width` of the op's shape (`falloff` 0 or 1; flat field):

  | Passage | Small enemies, king | Large, clearance 2 m | Large, clearance 4 m (Nordfels) |
  |---|---|---|---|
  | Ramp (raised strip, or cut into a plateau), straight | open from 4 | closed ≤ 6, open from 8 | closed ≤ 10, open from 12 |
  | Water causeway, straight (along x or z) | closed ≤ 3, open from 4 | closed ≤ 6, open from 8 | closed ≤ 10, open from 12 |
  | Water causeway, diagonal | closed ≤ 6, open from 8 | closed ≤ 6, open from 8 (offline says 10) | closed ≤ 12, open from 14 |

  On Durststein (1.5 m) a 4 m ramp already lets large enemies up. Causeways: a moat (dug, the default), then
  `water` `on: false` along a `line` of that width. Shapes fill whole 2 m cells, so where the edges fall on the grid
  can move a threshold by a cell. For a lane only small enemies use, make it 4-6 m wide; for one everyone uses, 8 m+
  (Nordfels 14 m+). **A ramp has to run into its terrace**: one laid along a cliff face, meeting the terrace only at
  its tip, opens onto it through a gap no wider than the strip where the two heights match (a 12 m ramp beside a
  face had a 4 m junction: closed to large enemies). End it on the terrace, or give its top a landing (`terrain`
  `set` the terrace height over the ramp's last 8-10 m and the gap to the face). `check` and `render` model the same
  rules offline with the host's clearance and slope (they agreed with the game on 53 of 54 measured passages,
  erring towards "closed"); confirm with `paths` or `paths --between X1 Z1 X2 Z2` in game.
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
| Gold Mine | 5 | +6, then -1 each dawn down to 1 | early burst |
| Harbour | 3 / 7 | 0, then +1 each dawn up to 5; L2 doubles it, the growth and the cap (10) | slow, big |
| Defense Tower | 3 / 5 / 15 | - | 36 m range; L2 and L3 are choices (Castle/Sniper/Armored/Bunker; Archer/Ballistic/Fire/Healing spires) |
| Barracks | 4 / 8 / 16 | - | melee squad (Knights / Spearmen / Flails / Berserkers) |
| Archery Range | 4 / 8 / 16 | - | archer squad (Longbow / Crossbow / Hunters / Fire) |
| Hero's Quarter | 6 / 10 / 20 | - | a hero squad (Golem / Support Mage / Firewing / Lizard Rider) |
| Wall / Gate | 4 / 12 (gate pieces vary) | - | blocks ground enemies; gates let your units through |
| Blacksmith / Royal Forge | 4 / 9 / 16, 4 / 7 / 14 | - | global unit upgrades |
| Temple | 2 / 6 / 25 | - | night-wide spells |
| Shrine | 3 | - | 777 HP decoy/buff |

Vanilla maps for scale: 12-15 nights; starting gold 7-23; 10-18 houses, 2-5 mills, 10-31 towers, 2 barracks, 2
archery ranges per map; an all-economy player (`economy`, an upper bound: real players also buy defence, and mines decline) reaches 5-50 gold per dawn by day 4 and 25-110 by day 6. Night 1 is 3-20 weak enemies
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
   player's kingdom (`--catchup free` jumps there with the earlier days built for free instead). After a full run,
   `test --from-night N` replays only nights N.. from that run's dawn state while you tune the late waves.
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
| enemies spawned by day reach buildings but deal no damage; towers don't fire by day | the game's attacks only work at night: deal daytime damage with Builder `attacks` (`"when": "always"`) or `damage` rules (12.16) |
| a rule meant to run "after the wave" never fires | the night ended with the last enemy's death: hold it open with a `countsAsEnemy` keeper (12.16) |
| a test's `sleep` overshoots or a rule fired "early" | fast mode runs faster than real time: wait on `logic eval time` (12.12) |

## 11. Worked example

See `examples/` in this folder: `watchpost.ops.json` builds a complete small level on a cleared Nordfels from nothing,
`watchpost.plan.json` is its test plan, and `watchpost.md` lists the exact commands and what they printed.

## 12. Behaviour: rules, entities, statuses (the logic engine)

Levels and enemies can be programmed. There is no list of features to pick from: you compose behaviour from a few
generic pieces stored as data in the level and enemy JSON, and the game runs them. A homing tree, a necromancer, a
drawbridge, a toll gate and a splitting slime are all the same pieces arranged differently. If you can describe a
mechanic as "when X happens, if Y, do Z" on some object, you can build it.

`logic-reference.md` (next to this guide, generated from the engine) lists every event, action, function, property,
stat, flag, filter term and built-in preset with its parameters. `palace.py logic schema <event|action|fn|preset|...>
<name>` prints one as JSON. Read this chapter for how the pieces fit; look details up there.

### 12.1 The model

- **Entities** are the things the engine knows: units (vanilla and custom), buildings, the king, the castle, coins, and
  Builder objects: props, projectiles, areas, pickups, logic objects, paths and points.
- **Templates** make entities. A custom unit (`units[]`) is a template; non-unit templates live in `entities[]` with
  a `kind`: `prop`, `projectile`, `area`, `pickup` or `logic` (invisible, just carries rules).
- **Rules** live on templates and placed objects: `when` an event happens (to the owner, or with `of` to others), `if`
  conditions hold, `do` actions run in order.
- Every rule sees **roles**: `self` (the owner; the level in the level script), `subject` (who the event happened to),
  `other` (the counterpart: killer, victim, attacker, entrant, collector, sender), `event.value`, `event.pos`.
- **Selectors** pick entities by filter and distance: `nearest('foe and Tower', 20)`, `all('ally and unit', 6)`,
  `@gate1` (a placed object by name).
- **Statuses** are timed, stacking modifier sets with their own rules: buffs, debuffs, auras, damage over time,
  shields, stuns, invisibility.
- **Signals** wire objects together: any rule can `signal` a name to an entity, a set or everyone; `signal` rules
  listen. Every entity also takes built-in inputs (`enable`, `disable`, `toggle`, `destroy`, and per component
  `open`/pose names, `start`, `stop`, `reverse`, `show`, `hide`, `lock`, `unlock`).
- **Variables** hold state: level-wide (`$souls`) or per entity (`self.$gen`). They must be declared with a start value.
- **Presets** are saved, parameterised rule sets (built-in ones include the logic objects Timer, Counter, Lever,
  Pressure plate, Door, Spawner... and abilities like Split on death and Aura).

### 12.2 Where logic lives

```jsonc
{
  "units": [{ "id": "slime", "base": "vanilla:E Slime",            // a custom unit, as in section 5
              "labels": ["goo"], "vars": { "gen": 0 },             // labels filters test (#goo); entity variables
              "statuses": ["warded"],                               // statuses it starts with
              "targeting": [...], "movement": {...}, "attacks": [...],
              "rules": [...] }],
  "entities": [{ "id": "tree-missile", "kind": "projectile", ... }], // non-unit templates
  "statuses": [{ "id": "burning", ... }],
  "presets":  [{ "id": "split-on-death", ... }],                    // your own presets (built-ins need no entry)
  "levels": [{ "id": "...", ...,
    "logic": {
      "seed": 1234,                                   // optional: the logic random source (default: from the level id)
      "vars": { "souls": 0, "season": "'summer'" },   // level variables and start values
      "rules": [...],                                 // the level script: self is the level
      "places": [...],                                // objects placed on the map (templates, areas, paths, points, logic objects)
      "attach": { "slot:Defense Tower Variant (1)": { "name": "northTower", "vars": { "hits": 0 }, "rules": [...] },
                  "castle": { "rules": [...] } },     // logic on things the level already has
      "hud": [{ "id": "souls", "text": "Souls {$souls}/10" }],
      "kingActions": [{ "id": "horn", "name": "War horn" }],   // up to 3 extra keys for the player
      "caps": { "entities": 600 }                     // raise the engine's caps (warning: may lag)
    } }]
}
```

`attach` keys are the Studio's item keys: `slot:<host slot name>` (names from `palace.py ref slots <map>`),
`add:<id>` (a slot you added), `prop:<id>`, `line:<spawn line name>`, `castle`. Give an attached object a `name` to
reach it from expressions as `@name`.

A **place** (`logic.places[]`) is `{ "id", "entity": "<template>" | "preset": "<preset>", "with": {...} | "kind":
"<kind>" + inline fields, "pos": [x, y, z], "rotY", "scale", "name", "active", "vars", "rules" }`. Its `id` (lowercase)
or `name` is how expressions reach it (`@plate1`). Inline fields override the template's for that copy. `"active":
false` places it switched off until something sends it `enable`. A path is `{ "id", "kind": "path", "points":
[[x, z], ...] }`; a point is `{ "id", "kind": "point", "pos" }`.

Templates and places can be used by every level; units with rules work wherever they spawn (waves, `spawn`,
`spawnGroup`). Entities, statuses and presets are library items like enemies (`library/entities/`, `statuses/`,
`presets/`), shared by all levels, and `export` copies the ones a level uses.

### 12.3 Rules

```jsonc
{ "id": "split",                     // optional; needed by enableRule/disableRule
  "when": "death",                   // or { "on": "every", "seconds": 8, "during": "night" }
  "if": "self.$gen < 3",             // one bool expression, or a list (all must hold)
  "do": [ ...actions... ],
  "once": false,                     // at most once per owner per run
  "cooldown": 0,                     // seconds before it can fire again for the same owner
  "enabled": true,                   // false: off until enableRule
  "note": "Splits into two smaller copies" }
```

- Without `of`, a rule hears only its owner's events. With `"of": "<filter>"` (and optional `"range"`) it hears that
  event on any matching entity: `{ "on": "death", "of": "foe and unit", "range": 12 }` on a necromancer fires when a
  foe dies within 12 m. Then `subject` is the one that died, `other` its killer, `self` still the necromancer.
- The level script hears time and night events (`levelStart`, `every`, `dusk`, `dawn`, `waveCleared`, `goldChanged`,
  `kingAction`, `signal`, `varChanged`...) directly; entity events (`death`, `damaged`, `spawn`, `enter`...) need `of`.
  In the level script `foe`/`ally` mean enemy/player.
- `near` takes its own `filter` and `range`: `{ "on": "near", "filter": "king", "range": 2 }`.
- Event params narrow it: `{ "on": "signal", "name": "open" }`, `{ "on": "hpBelow", "pct": 0.5 }`, `{ "on": "dusk",
  "night": 4 }`, `{ "on": "damaged", "min": 20 }`. Events with a value: `event.value` (damage, tick count, night...).
- Rules run one frame after the event, in order; actions that raise events queue them after. Waits resume later.

### 12.4 Expressions (the one formula language)

One small, pure language everywhere: conditions, numbers, selectors, positions.

- **Which fields are expressions.** A param whose type isn't text is an expression when it's a JSON string:
  `"amount": 25` and `"amount": "night * 5"` both work; `"target": "all('foe', 3)"`. A **text** field (messages, HUD,
  prompts) is literal text with `{expr}` parts (`"Souls {$souls}/10"`); start it with `=` to make the whole field an
  expression. A **template** field (`spawn.entity`) is an id (`"goblin"`, `"vanilla:E Slime"`) unless it starts with
  `=` (`"=self.template"`). Text inside an expression is quoted: `'summer'`, `count('foe')`.
- **Literals**: `12`, `0.5`, `true`, `'text'`, `vec(x, z)` (a ground position), `vec(x, y, z)`.
- **Names**: roles (`self subject other spawned it king castle level`), per-event aliases (`killer`, `victim`,
  `attacker`, `target`, `entrant`, `collector`, `sender`...), `event.value` / `event.pos` / `event.name` /
  `event.choice`, `@name` (a place or attached object), `@@id` (a template), `$var` (a level variable), `x.$var` (an
  entity variable), globals (`night`, `isNight`, `isDay`, `time`, `nightTime`, `nightProgress`, `gold`, `income`,
  `enemiesAlive`, `difficulty`, `nights`).
- **Properties**: `.hp .maxHp .hpPct .alive .elite .pos .x .y .z .facing .team .kind .template .labels .level .cost
  .buildingType .age .target .owner .id` (`.maxHp` is the live value, after the night's difficulty and elite; `@@x.hp`
  is the template's catalog value), every stat by name (`.moveSpeed`, `.damageMult`, `.income`...), methods
  `.is('filter')`, `.dist(e)`, `.inside(@area)`, `.stacks('status')`; lists and sets: `.len .first .last .at(i)
  .has(x)`.
- **Selectors**: `nearest(f, r?)`, `farthest`, `weakest`, `strongest`, `random`, `all(f, r?)` (a set, nearest first),
  `best(f, r, score)` (score is an expression over `it`), `count(f, r?)`. They anchor at `self`; call them as methods
  to anchor elsewhere: `castle.count('enemy', 10)`, `other.nearest('ally')`. A selector never returns the anchor itself
  or dead entities unless the filter says `self` / `dead`. No match is `none` (actions on none do nothing); test it with
  `other != none`. A role the event doesn't have is `none` too.
- **Positions**: an entity is its position wherever a position is wanted. `pointNear(p, r)` (a random walkable point),
  `ahead(e, d)`, `toward(a, b, d)`, `lerp(a, b, t)`, `dir(deg)` (0 = north, 90 = east), `angle(a, b)`,
  `spawnLine('Name')`; positions add, subtract and scale (`self.pos + dir(90) * 3`).
- **Maths and logic**: `+ - * / %`, `== != < <= > >=`, `and or not` (also `&& || !`), `c ? a : b`, `min max abs floor
  ceil round clamp sqrt pow sin cos`, `rand()`, `rand(a, b)`, `randInt(a, b)`, `chance(p)` (seeded: the same events
  give the same results).
- **Waves**: `waveCount(n, filter?)`, `waveUnits(n)`, `strongestUnit(n)`.
- **Types are checked** before the game runs (`palace.py check`): an unknown variable, a role the event doesn't have
  (`other` in `levelStart`), a misspelled filter term or a number compared with text are errors with the column.

### 12.5 Filters

A filter is a string: `"foe and Building and not Wall"`, `"#lantern"`, `"in:@moat"`. Terms: teams `foe ally player
enemy neutral` (foe/ally relative to `self`); kinds `unit building prop projectile area pickup coin king castle line
point path logic self`; `melee ranged alive dead elite`; any game tag, case-sensitive (`Tower`, `Flying`, `Wall`, `Boss`,
`Undead`...; see `logic-reference.md`); `#label`; `is:<template>`; `in:@area` / `in:self` (inside an area); `has:<status>`;
`type:<building kind>` (`type:Mill`); `where(<expression over it>)`. Combine with `and or not ( )`.

### 12.6 Actions

Each action is `{ "do": "<name>", ...params }`. `target` (default `self`) is a selector; when it's a set the action
runs once per member and the other params are evaluated per member with `it` bound (`"amount": "it.maxHp * 0.1"`).

| Group | Actions |
|---|---|
| Make | `spawn` (any template; `then` runs with `spawned`), `launch` (a projectile template toward a target; `onImpact`), `spawnGroup` (a wave-style group that counts toward the night), `replace` (morph into another template) |
| Health | `damage` (`amount`, `source` credited, `types` like `["fire"]`), `heal`, `kill`, `revive`, `setHp` |
| Stats | `setStat` (`stat`, one of `value`/`add`/`mult`, `for` seconds or `"night"`/`"day"`), `applyStatus`, `removeStatus` |
| Team, move | `convert` (switch side), `teleport`, `push` (knockback), `drift` (continuous pull/wind), `moveTo`, `setMovement`, `setTargeting` (`prepend`, `remove`, `for`), `taunt`, `command` (player units: follow/hold/home) |
| Logic | `signal`, `setVar`, `addVar`, `listAdd`/`listRemove`/`listClear`, `addLabel`/`removeLabel`, `enableRule`/`disableRule`, `enable`/`disable`/`toggle`, `wait`, `if` (`cond`, `then`, `else`), `repeat` (`times`, `every`, `actions`), `forEach` (`in`, `actions`, `limit`), `choose` (`options: [{weight, do}]`), `stop` |
| World | `gold` (give/take), `dropCoins`, `building` (build, upgrade, knockOut, repair, demolish, lock, unlock, setCost, setIncome a slot), `waves` (addSpawn, removeUnit, scaleCount, scaleHp, clearRemaining, startNight, endNight, skipNight), `endLevel` |
| Show | `effect`, `sound`, `message`, `floatText`, `setVisual`, `hud`, `shake`, `marker` |
| End | `destroy` (`drops`, `effects`, `silent`) |

`repeat` and `forEach` take their body as `actions` (the key `do` already names the action). `setVar`/`addVar` write
`self`'s variable unless `target` names another entity; a level variable is `"name": "$souls"` or `"target":
"level"` (in the level script plain names are level variables).

### 12.7 Components (what a template is)

| Component | For | Key fields |
|---|---|---|
| `visual` | any | `mesh` (`mesh:<scenery>`, `vanilla:<unit>`, `asset:<key>`), `meshScale`, `tint`; null = invisible |
| `body` | any | `radius` or `box: [w, d]` (w across its facing, d along it; turns with `rotY`), `height`, `solid` (blocks ground units: a cut in the ground pathfinding, also when spawned at runtime, ~0.25 s to update; units on it are pushed out), `walkable` (a bridge or raft top), `hittable` + `hp` (at 0 hp it's destroyed: `death` fires, it can't be revived), `flying`, `tags` (game tags for a hittable body: `["Building"]` makes vanilla enemies attack it like a building; without tags they ignore it unless `setTargeting` points them at it) |
| `team` | any | `player`, `enemy`, `neutral` (vanilla AI ignores it), `owner` (the spawner's). `countsAsEnemy: true` keeps a hittable enemy-team entity holding the night open |
| `lifetime`, `maxAlive` | any | seconds before `expire` + destroy; cap on live copies |
| `projectile` | flying things | `speed`, `homing` 0..1, `arc`, `gravity`, `spin`, `face`, `hits` (filter), `hitRadius`, `pierce`, `onGround`, `range`; events `impact`, `expire` |
| `mover` | moving props | `path` (`@path` or points), `mode` loop/pingpong/once, `speed`, `wait`, `spin`, `swing`, `running`, `pose` (or `poses`) {name: {`offset` [x,y,z] or world `pos`, `rotY`}}, `turnSpeed` (deg/s, 90); inputs `start stop reverse goto` and each pose's name; events `arrive`, `end`. A walkable mover snaps between poses |
| `area` | zones | `shape` (`circle {radius}` / `rect {size: [w, d]}` / `polygon {points}`), `watch` (filter), `show`, `color`, `tick`, `surface` (below); events `enter exit inside empty occupied`; filters `in:@area` |
| `pickup` | collectibles | `by` (filter, e.g. `king`), `radius`, `magnet`, `consume`; event `pickup` |
| `interact` | king-usable | `prompt` (`{cost}` shows the cost), `cost`, `when` day/night/always, `radius`, `repeat`, `choices` (up to 4 `{id, label, description, cost, icon}`: the game's upgrade-choice popup; the chosen cost is paid and `event.choice` is its id); event `interact` |
| `attacks` | turrets, units | `[{ "cooldown", "range", "targeting", "launch": {"entity", "from"} \| "weapon", "do", "replace", "when" }]`; the unit's base attacks stay unless `replace` |
| `targeting` | attackers | ordered list of filters or `{filter, range, prefer, score}`; works on vanilla towers too (`setTargeting`) |
| `movement` | units | `mode` pathfind/direct/hold/path, `path`, `then`, `speed`, `keepDistance`, `kite`, `flee`, `wander`, `home`, `surfaces` |

A number field may be an expression evaluated when the entity appears: `"hp": "=100 * night"`.

**Surfaces (who may walk where).** An area with `"surface": "mud"` or `{"name": "ladder", "allow": "melee", "cost": 1}`
becomes a named pathfinding surface: only units matching `allow` path through it, and `cost` multiplies its path cost
(2 = units avoid it unless it's much shorter). A unit's `movement.surfaces` overrides per unit: `{"ladder": true,
"mud": 3, "ice": false}`. At most 31 surface names per level. A `body.walkable` object (a bridge, a raft) is best
placed in `logic.places`; spawned or destroyed at runtime it rebuilds the pathfinding round it (30-100 ms), fine now
and then. Its top should be within 2.5 m of the ground it meets: an edge that touches walkable ground opens (with a
ramp when the step is over 0.45 m), and the map's invisible walls or terrain water under it give way; every other
edge gets an invisible rail, so units can't step off into the river. Let a bridge overlap each bank by 1 m or more,
and make it over 8 m wide for large enemies (they keep 4 m from every edge).

### 12.8 Statuses

```jsonc
{ "id": "burning", "name": "Burning", "duration": 4, "stacking": "refresh",   // refresh | add | extend | ignore
  "maxStacks": 5, "tick": 1,
  "mods": { "moveSpeed": { "mult": 0.8 },                                   // per stack: mult compounds, add sums
            "damageTaken": { "mult": 1.25, "types": ["fire"], "from": "foe", "chance": 1, "when": "dist > 10" } },
  "flags": [],                       // stunned disarmed rooted untargetable invulnerable silenced noIncome
  "visual": { "tint": "#ff7020", "fx": "fire_small" },
  "rules": [{ "when": "statusTick", "do": [{ "do": "damage", "amount": "3 * self.stacks('burning')", "types": ["fire"], "source": "other" }] }] }
```

In a status's rules `self` is the carrier and `other` whoever applied it. `damageTaken` works on anything with hit
points, buildings and the castle included (`setStat` on `castle` with `"for": "night"` is a one-night shield). `drift` is a mod too (`"drift": {"toward":
"@well", "speed": 3}`). Vanilla damage carries types `melee`/`ranged`, `tower`, `king`, `splash`, `magic`.

**How long a status lasts.** `duration` is in seconds (0.05 to 36000). Leave it out and the status is permanent: it
stays until `removeStatus`, the way to give something a lasting buff, curse or flag.
`applyStatus` `for` overrides the duration for that application only.

**Damage modifier conditions.** A `damageTaken`/`damageMult` mod's `from` is a filter (relative to the carrier) on
the attacker; its `when` is an expression where `self` is the carrier, `attacker` (also `other`, `it`) who deals the
hit, `victim` (also `subject`) who takes it, and `dist` the ground distance between them. A hit with no known attacker
fails `from`, and `attacker` is `none` in `when`. `chance` is rolled per hit. These keys only work on the two damage
stats.

### 12.9 Variables, HUD, king actions, caps

- Declare every variable with a start value: level ones in `logic.vars`, entity ones in the template's (or place's,
  or attach's) `vars`. Values: a number, `true`/`false`, `"'text'"`, `"vec(x, z)"`, `[]` (a list), or `{"type":
  "entity", "value": null}`. Using an undeclared one is an error. Changing one fires `varChanged`.
- HUD lines show text with `{expr}` (`"Souls {$souls}/10"`, `"visible": "$souls > 0"`); the `hud` action changes them.
- King actions (`logic.kingActions`, at most 3): `{ "id", "name", "when": "day", "cooldown", "uses": {"count", "per":
  "day"|"night"|"run"} }`. The player presses Level action 1-3; the level script hears `{"on": "kingAction", "action":
  "<id>"}`.
- Caps keep levels playable: 300 live spawned entities (units 200, projectiles 400), 100 copies per template, 400
  rule firings and 4000 actions per frame, chain depth 12, `every` ≥ 0.1 s, `wait` ≥ 0.05 s, `repeat` ≤ 100, sets ≤ 256.
  Over a cap the extra spawn or firing is skipped (the trace shows it). `logic.caps` raises them per level (warning).

### 12.10 Presets

Use a preset in any rules list: `{ "preset": "split-on-death", "with": { "count": 3 } }` (its rules, vars, labels and
components are merged in), or place a logic object: `{ "op": "place", "id": "t1", "preset": "timer", "with":
{ "seconds": 10, "signal": "wave" }, "at": [x, z] }`. Built-ins (see `logic-reference.md` for params): `timer`,
`counter`, `relay`, `gate`, `toggle-switch`, `random-router`, `spawner`, `lever`, `pressure-plate`, `door`,
`moving-wall`, `sign`, `gold-pickup`, `hud-counter`, `split-on-death`, `spawn-on-death`, `aura`, `explode-near`,
`multiply`, `regenerate`, `blink-away`.

Your own preset: `{ "id", "name", "description": "Splits into {count} copies", "params": { "count": { "type":
"number", "default": 2, "min": 1, "max": 6 } }, "vars": {...}, "rules": [...], ...components }`. `{param}` anywhere in
its strings is replaced by the value; a string that is exactly `"{count}"` becomes the typed value (a number stays a
number). Errors in a preset's rules are reported at the place that uses it.

### 12.11 Ops

| Op | Fields |
|---|---|
| `entity` | a whole EntityDef (`id`, `kind`, components, `rules`...): adds or replaces |
| `status` / `preset` | a whole StatusDef / PresetDef |
| `place` | `id`, `entity` \| `preset` + `with` \| inline `kind` + fields, `at` [x, z] (or `pos`), `rotY`, `scale`, `name`, `active`, `vars`, `rules`; a path takes `points` |
| `remove_place` | `id` |
| `rule` | `on`: `level` \| `unit:<id>` \| `entity:<id>` \| `status:<id>` \| `place:<id>` \| `slot:<name>` \| `add:<id>` \| `prop:<id>` \| `line:<name>` \| `castle`; `rule` (or `rules`): one with an existing `id` replaces it |
| `remove_rule` | `on`, `id` |
| `wire` | `from` (an `on` value), `event` (default `interact`), `to` (a place id/name or attach name), `input` (signal), `value`, `delay`, `do` (the target's listener, for an input that isn't built in) |
| `var` | `name`, `value` (a level variable), or `on` + `name` + `value` (an entity default) |
| `attach` | `key`, `name`, `labels`, `vars`, `statuses`, `targeting` |
| `king_action` | `id`, `name`, `when`, `cooldown`, `uses` |
| `hud` | `id`, `text`, `icon`, `visible` |
| `logic` | `seed`, `caps` |

Units take `rules`, `vars`, `labels`, `targeting`, `movement`, `attacks`, `statuses` in the `unit` op, or rules later
with `{"op": "rule", "on": "unit:<id>", ...}`.

### 12.12 Checking and testing behaviour

1. `palace.py check library <level>` (also run by `apply`) checks every rule offline: unknown events and actions
   (with "did you mean"), params, types, roles per event, variables, names, templates, statuses, filters, signal
   loops, caps. Each issue has a JSON path and, for expressions, a column. `palace.py logic check` runs only these.
2. In the game (`play` or `test` the level, then):
   - `palace.py logic trace [n]`: the last rule firings (time, owner, rule, event, outcome; throttles and errors).
   - `logic vars [selector]`, `logic entities [filter]`, `logic status`, `logic budget`.
   - `logic eval "<expression>"` evaluates in the level script's scope: `logic eval "count('is:slime')"`.
   - `logic do '<action JSON>'` runs actions as the level script, e.g. to set up a test:
     `logic do '{"do":"spawn","entity":"necromancer","at":"vec(-4,-14)"}'`.
   - `logic fire <signal> [to]`, `logic raise <event> <on> [other] [name] [value]` (selector arguments without spaces),
     `logic set <var> <expr>`, `logic king <1-3>`.
3. Test a mechanic deliberately: spawn what it needs with `logic do`, wait, assert with `logic eval`.

**Testing helpers and traps.**

- Measure waits in game time, not wall time: `test` and `game cmd "agent fast on"` run the game several times faster
  than real time, so a `sleep` overshoots. Read `logic eval time` (seconds since level start) before and after and
  poll until the difference is reached; poll conditions with `logic eval` the same way.
- `logic raise interact <place>` fires the `interact` rules without charging the `cost` (the king pays only when he
  really interacts). To test a paid interaction, check `gold` before and after a real one, or charge it with
  `logic do '{"do": "gold", "amount": -5}'`.
- Keep a probe alive: `logic do '{"do": "setStat", "target": "castle", "stat": "damageTaken", "value": 0}'` makes the
  castle take no damage (any hittable target likewise); `cheat god` and `cheat invulnerable` protect the king and the
  whole kingdom, so `logic do` `damage`/`kill` on your own units does nothing while they're on: `cheat invulnerable
  off` before testing damage to your side.
- `game cmd "run <h> <v> <seconds>"` holds the movement stick (`h`, `v` in -1..1; the camera's axes) to ride the king
  into something; `cheat teleport X,Z` first puts him at the start. `shot` frames the camera, which follows the king:
  teleport him to what you want in the picture.
- A pool or fresh game has no reference cache: `ref build --maps <host>` (works from inside a level; it ends the
  session and loads each map) before `ref slots <host>`, and before an `add_slot` of a building kind the host lacks.
- The `effect` names (`fx`) and `sound` names are listed by `game cmd "builder dump"` (it writes `catalog.json`).

**Fixtures (`palace.py logic fixture <file.json>`).** A fixture is a level plus a scripted check, run in the game
(fast mode) as one command: it copies the fixture's pack into the library, plays its level, runs the steps in order
and stops at the first failure. The output lists every step with `passed`/`value`/`error`, then errors from the game
log and the last 30 trace lines; `"ok": true` means every step passed.

```jsonc
{ "title": "Necromancer raises skeletons",
  "pack": { "format": "palace-builder", "version": 3, "id": "fx", "units": [...], "entities": [...], "statuses": [...],
            "levels": [{ "id": "fx-necro", "name": "Necro test", "host": "Nordfels", "logic": {...} }] },  // one level
  "play": { "steps": [
    { "cmd": "agent build castle" },                  // any game command (what `game cmd` takes)
    { "cmd": "night" },
    { "do": [{ "do": "spawn", "entity": "necromancer", "at": "vec(20, 10)" }] },   // actions, as the level script
    { "raise": "death", "on": "@plate", "other": "king", "name": "-", "value": 1 },   // a synthetic event
    { "wait": 1 },                                     // game seconds
    { "expect": "count('is:skeleton') >= 1", "within": 5 },   // a bool expression, polled for `within` game s (default 5)
    { "expectLog": "Raised", "within": 5 },          // text in a game log line since the fixture started
    { "expectState": "AfterMatchVictory", "within": 60 } ] } }   // the game state `game status` reports
```

Write one per mechanic you want to keep working, next to your ops, and re-run it after every change.

### 12.13 Limits (what the engine can't do)

- Vanilla arrows in flight aren't affected by `drift` or `timeScale` (Builder projectiles are).
- Per-unit walkability is by surfaces (above), not by terrain: a "ladder" is an area surface only some units may
  path through. Walkable movers snap between poses; they don't carry units while gliding. Terrain height doesn't
  change at runtime.
- Three teams; vanilla units never attack `neutral` things (Builder targeting can).
- Game tags are a fixed set; your own labels work through Builder targeting and filters.
- No new scenes, lighting or arbitrary code. Pending `wait`s and Builder projectiles in flight aren't kept by
  savestates; keep state that matters in variables.
- No world queries beyond entities: expressions can't ask for the ground height at a point (`vec(x, z).y` is 0, not
  the ground), whether a point is walkable, the terrain material under it, or how lit it is. Track such state yourself
  (areas, labels, variables).
- Game coins on the ground aren't entities (`count('coin')` doesn't see `dropCoins` or kill drops; `game status`
  `coinsPending` does). A pickup entity is a coin you can see and count.
- Ranges the checker enforces: a status `duration` 0.05 to 36000 s (leave it out for a permanent status, 12.8), `every`
  at least 0.1 s with `jitter` 0..1, `wait` at least 0.05 s, `repeat` at most 100 times.

### 12.14 Cookbook

Each is plain data; adapt names and numbers. (Full, tested versions: `examples/behaviour.ops.json` builds several on
one level.)

**Hurls homing trees** (an enemy with a projectile attack):

```jsonc
{"op": "entity", "id": "tree-missile", "kind": "projectile", "visual": {"mesh": "mesh:Cone Tree", "meshScale": 0.4}, "lifetime": 6,
 "projectile": {"speed": 9, "homing": 0.6, "arc": 0.2, "spin": [0, 0, 360], "hits": "foe"},
 "rules": [{"when": "impact", "do": [{"do": "damage", "target": "all('foe', 2)", "amount": 25, "source": "self.owner"},
                                     {"do": "effect", "fx": "dust_large", "at": "self"}]}]}
{"op": "unit", "id": "treant", "base": "vanilla:E Ogre", "name": "Treant",
 "attacks": [{"replace": true, "cooldown": 3, "range": 18, "targeting": ["foe and Building", "foe"],
              "launch": {"entity": "tree-missile", "from": [0, 3, 0]}}]}
```

**Kills convert the victim**: `{ "when": "kill", "if": "other.is('unit and not king')", "do": [{ "do": "convert",
"target": "other", "team": "=self.team", "hpPct": 0.5 }] }`

**Multiplies every 8 s, capped**: `{ "when": {"on": "every", "seconds": 8, "during": "night"}, "if": "count('ally and
is:spore') < 40", "do": [{"do": "spawn", "entity": "=self.template", "at": "pointNear(self, 2)"}] }`

**Necromancer**: `{ "when": {"on": "death", "of": "foe and unit", "range": 12}, "if": "chance(0.3)", "do": [{"do":
"spawn", "entity": "skeleton", "at": "subject"}] }`

**Splitting slime**: `"rules": [{"preset": "split-on-death", "with": {"count": 2, "depth": 3}}]`

**Spike plate** (an area that hurts enemies standing on it):

```jsonc
{"op": "place", "id": "plate1", "kind": "area", "at": [12, -4], "area": {"shape": {"rect": {"size": [4, 4]}}, "watch": "enemy", "show": "fill"},
 "rules": [{"when": {"on": "every", "seconds": 1}, "if": "count('enemy and in:self') > 0",
            "do": [{"do": "damage", "target": "all('unit and in:@plate1')", "amount": 30}, {"do": "effect", "fx": "spikes", "at": "self"}]}]}
```

**Drawbridge on a lever**: a prop template with `"mover": {"pose": {"up": {"offset": [0, 4, 0]}, "down": {}}}` placed as
`bridge`; a lever: `{"op": "place", "id": "lever", "preset": "lever", "with": {"signal": "up", "to": "@bridge"}, "at":
[14, -6]}`; and on the bridge `{"when": "dusk", "do": [{"do": "signal", "name": "down", "to": "self"}]}`.

**Aura**: a status `{"id": "warded", "duration": 1.2, "mods": {"damageTaken": {"mult": 0.7}}}` and on the bearer
`{"when": {"on": "every", "seconds": 1}, "do": [{"do": "applyStatus", "status": "warded", "target": "all('ally and unit', 6)"}]}`.

**Kamikaze**: `"targeting": [{"filter": "king"}], "movement": {"mode": "direct"}` and `{"when": {"on": "near", "filter":
"king", "range": 2}, "do": [{"do": "damage", "target": "all('foe', 3)", "amount": 40}, {"do": "destroy", "drops": true}]}`.

**Merchant caravan**: a path place `{"id": "route", "kind": "path", "points": [[30, -10], [-4, -8]]}`, a unit with
`"movement": {"mode": "path", "path": "@route", "then": "destroy"}` and `{"when": "end", "do": [{"do": "gold", "amount":
15}]}`, spawned with `"team": "neutral"`; enemies prefer it: level rule `{"when": {"on": "spawn", "of": "enemy"}, "do":
[{"do": "setTargeting", "target": "subject", "prepend": ["is:caravan"]}]}`.

**Charging tower** (every fifth hit fires a bolt), on an attached slot (Nordfels' slot names: `palace.py ref slots Nordfels`):
`{"op": "rule", "on": "slot:Defense Tower Variant (1)", "rule": {"when": "hit", "do": [{"do": "addVar", "name": "hits", "value": 1},
{"do": "if", "cond": "self.$hits % 5 == 0", "then": [{"do": "launch", "entity": "bolt", "toward": "other", "pierce": -1}]}]}}`
plus `{"op": "var", "on": "slot:Defense Tower Variant (1)", "name": "hits", "value": 0}`.

**Volcano**: a logic place with `{"when": {"on": "every", "seconds": 12, "during": "night"}, "do": [{"do": "launch",
"entity": "lava-rock", "toward": "pointNear(self, 15)"}]}`; `lava-rock` (projectile, `gravity`) on `impact` damages
`all('unit', 3)` with `types: ["fire"]` and spawns `fire-area` (an area entity with `lifetime: 5`, `tick: 1`, rule
`inside` → `applyStatus burning` to `other`).

**Mercenary camp**: an area watching the king, `tick: 3`, rule `inside` if `isDay and gold >= 10`, `cooldown: 60`: `gold
-10`, `spawn merc ×3 team player then command follow`; the merc template's rule `dawn → destroy silent`.

**Revenge**: level rule `{"when": {"on": "damaged", "of": "castle"}, "cooldown": 5, "do": [{"do": "setStat", "target":
"all('player and Tower')", "stat": "attackCooldown", "mult": 0.5, "for": 5}]}`.

**Escalation**: `{"when": "dusk", "if": "night >= 4 and count('#shrine and alive') >= 2", "do": [{"do": "waves", "op":
"addSpawn", "night": "night", "unit": "brute", "count": "night - 2", "line": "Main Bridge"}]}`.

**Rotating blade wall**: a prop with `"mover": {"spin": 90}`, `"body": {"box": [6, 1], "solid": true}` and `every 0.5 →
damage all('unit and not Flying', 3) 8`.

**Lantern ghost** (invulnerable away from lanterns): status `veiled` with `flags: ["invulnerable"]`; on the ghost
`every 0.5 → if count('#lantern', 8) == 0 then applyStatus veiled for 0.6`.

**Blinks away once**: `{"when": {"on": "hpBelow", "pct": 0.5}, "once": true, "do": [{"do": "teleport", "at":
"pointNear(castle, 10)"}, {"do": "heal", "amount": "self.maxHp * 0.3"}]}`.

**Chain lightning**: a projectile `spark` with vars `{"jumps": 3, "power": 30}`; on `impact`: damage `other` by
`self.$power`; if `self.$jumps > 0`, `launch spark from other toward nearest('foe and not has:shocked', 8)` with `then`
setting `spawned`'s `jumps` to `self.$jumps - 1` and `power` to `self.$power * 0.7` (a short `shocked` status stops it
jumping back).

**Day/night shapeshifter**: `{"when": "dusk", "do": [{"do": "replace", "with": "werewolf", "keep": ["hp", "vars"]}]}`, and
the mirror rule at `dawn` on the werewolf.

**Toll gate**: a door place with `"interact": {"prompt": "Pay {cost} to open", "cost": 5, "when": "day"}` and rules
`interact → signal open to self`, `dusk → signal closed to self`.

**Soul objective**: `"vars": {"souls": 0}`, `"hud": [{"id": "souls", "text": "Souls {$souls}/10"}]`, level rules
`{"when": {"on": "death", "of": "enemy and in:@ritual"}, "do": [{"do": "addVar", "name": "souls", "value": 1}]}` and
`{"when": "varChanged", "if": "$souls >= 10", "once": true, "do": [{"do": "endLevel", "result": "win"}]}`.

**War horn** (a king action): `{"op": "king_action", "id": "horn", "name": "War horn", "when": "day", "uses": {"count":
1, "per": "day"}}` and a level rule on `{"on": "kingAction", "action": "horn"}`: `command follow` and `setStat
moveSpeed mult 1.2 for "night"` on `all('player and unit', 20)`.

### 12.15 Pitfalls

- A text field is literal: `"text": "souls"` shows the word; `"text": "{$souls}"` shows the number. A non-text field
  is an expression: `"value": "summer"` is an unknown name; write `"value": "'summer'"`.
- `foe`/`ally` are relative to `self`; in the level script they mean enemy/player. A selector skips `self` and dead
  entities unless the filter says `self`/`dead`.
- The level script needs `of` for entity events; `other` exists only for events that have one (see the reference).
- Declare variables first; set entity variables on the right `target` (`"target": "spawned"` inside `then`).
- `@names` are place ids/names and attach names; give attached slots a `name` before wiring them.
- Bound anything that repeats: `once`, `cooldown`, a variable counter or a `count(...)` cap, so a level can't snowball
  into the caps.
- `repeat`/`forEach` bodies are `actions`, not `do`.
- An enemy-team entity that should hold the night open needs `body.hittable` and `countsAsEnemy: true`.

### 12.16 How the game and the engine behave (facts to design around)

**Day and night.**

- The game's own attacks only fire at night (section 3). Builder damage doesn't care: the `damage` action, status
  ticks, projectile `impact` rules and Builder `attacks` with `"when": "day"` or `"always"` hurt by day too. A daytime
  raid is enemies whose damage comes from those, not from their vanilla weapons.
- The player can't build or upgrade at night (the game refuses with "Only during the day").
- A night ends in the frame its spawning is finished and no enemy is left. A hittable enemy-team entity with
  `countsAsEnemy: true` (a "keeper", e.g. an invisible logic body) counts as an enemy: while it lives the night stays
  open, so rules can still act after the wave; `destroy` it to let dawn come.
- `waves endNight` stops the spawning and removes every enemy at once, silently (no `death` events, no drops, no
  gold), keepers included; the night then ends. `waves skipNight` works only by day, and not when the coming night
  is the last.
- `every` runs on its owner's clock from level start (or from when the owner appeared), not from nightfall: with
  `"during": "night"` it only skips the ticks that fall by day, so the first night tick comes anywhere from 0 to
  `seconds` after dusk and `event.value` counts only the ticks that ran. For "N seconds into each night" use a `dusk`
  rule with a `wait` or a `repeat` (`every` seconds). `jitter` (0..1) varies each interval by up to that share of
  `seconds`.

**Gold.**

- `gold` with a positive `amount` and no `at` pays at once. With `at`, up to 40 coins rise there and fly to the king,
  paying 1 each as they arrive (a few seconds); the rest is paid at once. A negative amount takes gold at once (never
  below 0).
- Dawn income and dropped gold (kills, `dropCoins`) reach the treasury as the game's coins, over a few seconds.
  `game status` `coinsPending` counts coins still being spawned, flying or lying on the ground: wait until it's 0
  before checking `gold`.
- `game status` `buildings.income` and `game slots` `income` are what each building pays at dawn, `income` modifiers
  (`setStat`, statuses, `noIncome`) included.

**Projectiles** (Builder projectiles; the `projectile` component and `launch`).

- A projectile hits anything matching its `hits` filter (default `foe`, relative to whoever launched it) that comes
  within `hitRadius` of its flight, not only its target. Only its launcher is never hit. `homing` only steers: 0 flies
  straight at where the target stood at launch, 1 re-aims every frame, in between it turns part of the way per second.
  Each hit raises `impact` (`other` = what it hit) and runs the launch's `onImpact`; with `pierce` 0 the first hit ends
  it, `pierce: -1` hits everything on its way.
- Launched at a position (`toward` a point, or a target that's gone), reaching that point is a ground hit: `impact`
  with `other` none, then it ends (`onGround` `stick`/`bounce`/`pass` change that).
- After `range` metres of flight (default 60) it ends with `expire` and no impact; `lifetime` likewise.
- `launch` with `"from": "other"` starts it at that entity, which isn't excluded: if it matches `hits` it's hit
  immediately. Exclude it in `hits` (`"foe and not has:shocked"`, a label) or start a little away from it
  (`"from": "toward(other, nearest('foe and not has:shocked', 8), 1.5)"`).

**Builder attacks (`attacks[]`).** Every `cooldown` the unit picks a target (the attack's own `targeting`, else the
unit's, else `foe`) within `range` (and beyond `minRange`), fires its `launch` or `weapon`, raises `attack` on itself
(`other` = the target) and runs `do` with `self` and `subject` = the attacker and `other` = the target. `when` is
`"night"` (default), `"day"` or `"always"`, nothing else; a stunned, disarmed or knocked-out unit doesn't attack. How
units and towers choose targets (first matching rule, `prefer`, `score`) is in the `targeting` row of 12.7.

**Who vanilla attackers hit.** Towers, troops, the castle and the king attack only what the game counts as an enemy:
enemy units, and enemy-team hittable entities with `countsAsEnemy` (which also hold the night open). Any other
enemy-team entity, even one tagged `Building`, is reached only by Builder attacks, projectiles and `damage` rules. (A
body's game `tags` such as `Building` are for the other side: they make *enemies* attack a player-team body.)

**Movement.** `"wander": {"radius": 6}` makes a unit stroll to random points within `radius` of its home (a new point
every 4-7 s) whenever it has nothing to go for: in the default `pathfind` mode, when its targeting finds nothing, or
for a player unit, when it's idle at home and not following the king. `hold`, `path` and a `moveTo` come first.

**What changes the routes at runtime.**

- A `solid` body cuts the ground pathfinding where it stands, and units re-path within about 0.25 s whenever one is
  placed, spawned, enabled, disabled, destroyed or teleported, or a mover changes pose. That's the tool for opening
  and closing a region at runtime (a gate, a collapsing bridge, rising water): a solid body you toggle. Place it with
  `at` at the size of the blocked ground and hide or sink only its look (`"visual": null`, or
  `"visual": {"meshOffset": [0, -2, 0]}`); its body keeps blocking where the entity stands.
- A `walkable` body spawned or destroyed rebuilds the pathfinding round it (12.7, "Surfaces").
- Terrain height and water don't change at runtime; `paths` (and `paths --between`) shows the routes after a change.
