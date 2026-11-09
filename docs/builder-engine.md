# Builder: the behaviour engine

A creator-programmable gameplay layer for Builder levels and enemies. Creators compose behaviour from a closed set of
generic primitives: entities, events, conditions, actions, statuses, signals and variables. There is no list of
features. A homing tree, a necromancer, a drawbridge, a toll gate and a splitting slime are all the same few
primitives arranged differently, stored as data in the level or enemy JSON. They are authored in the Studios and by
agents through `palace.py`.

This is a design. Nothing here is built yet. It was worked out from the code on `main` at 3bef6cc and from the
decompiled game; no game was run. `docs/builder-design.md` (data model, runtime, sessions) and `docs/builder-ux.md`
(the Studios) still apply.

## Recommendation

1. **Rules on things.** Every placeable or spawnable object is an **entity** made from a **template**. Templates and
   placed entities carry **rules**: *When* an event happens, *If* conditions hold, *Then* run actions. This is the
   Warcraft III/StarCraft II trigger shape, attached to objects the way Architect attaches scripts to prefabs, so a
   behaviour travels with the enemy or prop that owns it.
2. **Context roles, not ids.** Each event carries typed roles: `self` (the rule's owner), `subject` (who it happened
   to), `other` (the counterpart: killer, victim, attacker, entrant), `event.value` and `event.pos`. Actions take
   **selectors** built from those roles plus spatial queries (`nearest("foe and Tower", 20)`). That makes rules
   reusable, which is what lets presets work.
3. **Abilities as data trees** (StarCraft II's effect/behaviour model): actions nest (`launch` → `onImpact` → `all(...)`
   → `damage`). **Statuses** are timed, stacking modifiers with their own rules, which covers buffs, debuffs, auras,
   damage over time, shields, stuns and invisibility. **Projectiles, movers, areas and pickups** are entity
   components, not special cases.
4. **Signals for wiring** (Architect's broadcaster/listener, with an optional number as in Factorio): any rule can
   `signal` a name to an entity, a set or everyone. A **Wire tool** draws these between objects in the Studio. Every
   entity has built-in inputs (`enable`, `disable`, `toggle`, `destroy`), plus the inputs its components add (`open`,
   `start`, `reverse`).
5. **One small, safe expression language** for numbers, conditions, filters and positions. It has no loops and no
   assignment; it is statically type-checked and gives precise errors. In the Studio it is optional (pickers cover the
   common cases), and it is how agents write behaviour compactly. This avoids Roblox's "no-code, then a cliff into
   code" problem.
6. **Presets are saved rule sets with parameters.** That includes the engine's own logic objects (Timer, Counter,
   Relay, Gate, Lever, Spawner, Moving wall); they ship as built-in JSON, not code.
7. **Bounded by construction.** Entity, projectile and spawn caps; a per-frame action budget; a maximum chain depth;
   floors on timers and waits; capped loops and set sizes; a seeded random source. The Studio's debug panel shows what
   fired and what was throttled.

## Prior art

| System | Model | What we take | What we avoid |
|---|---|---|---|
| **Architect** (Silksong, cometcake575; [docs](https://starshooter.gitbook.io/architect/architect-silksong/), [Thunderstore](https://thunderstore.io/c/hollow-knight-silksong/p/cometcake575/Architect/), about 234k downloads) | Objects with per-object config. The legacy system: a Broadcaster (event, plus a free-text message) on one object, and a Listener (message, trigger, "Times" count) on another. Every object accepts Enable/Disable and has an "Active" flag. Now also a node-graph Script Editor: event, process (If, Counter, Variables, Compare, Operation, Logic, Delay, Random) and trigger blocks, with control wires and value wires. Local, global and prefab scripts; prefabs carry their scripts and each instance has its own copy. Levels are JSON, shared in-game and through Thunderstore. | Message-named signals with a receive count; universal enable/disable inputs; "starts inactive"; scripts that travel with prefabs; separate level and object scopes; first-time vs. reloaded events (our savestate restore) | A free-floating node graph as the only view. It's hard to read in a 3D world and hard for agents to write. Our wires are a view onto rules. |
| **Warcraft III / StarCraft II triggers** | Event, then Condition, then Action. Event-scoped handles ("Triggering Unit", "Dying Unit"), unit groups, Pick Every Unit, typed variables | The rule shape; context roles; sets plus `forEach`; typed vars | Global-only triggers (ours attach to objects) |
| **StarCraft II data editor** | Effect trees (Set, Search Area, Launch Missile, Apply Behavior, Create Unit, Damage, Modify Unit, Persistent); Behaviours (buffs with duration, stacks, stat changes, periodic and event effects); Validators | Nested actions with source/target roles; statuses as data; validators as filter expressions; finite periodic effects | Its catalogue sprawl (one record type per verb variant) |
| **LittleBigPlanet / Dreams** | Switches, sensors and gates; emitters; microchips and elements packaged as reusable black boxes; a thermometer budget | Presets as packaged rule sets; emitter equals the `spawn` action; a visible budget | Analog signal plumbing for everything |
| **Factorio circuits / Wiremod** | Named numeric signals on wires; combinators; typed ports; Expression 2 as the text escape hatch | Signals carry an optional number; a text expression escape hatch that's bounded | Per-tick wire maths as the main way to build logic |
| **Mario Maker** | Orthogonal modifiers (wings, parachute, size) that stack on any object; pipes spawn anything | Any template as a body for anything (a tree as a projectile, a boss model on a gnat); `spawn` takes any template | — |
| **Roblox** | Lua scripts on parts | — | The cliff from no-code to full code, and unbounded scripts |

## Concepts

| Term | Meaning |
|---|---|
| **Entity** | A live object the engine knows about. Placed ones are build slots, props, spawn lines, placed templates, areas, paths and points. Spawned ones are units, projectiles and pickups. Vanilla units and buildings are entities too (selectors and filters see them), but rules attach only to Builder objects. |
| **Template** | What an entity is made from: a custom unit (`units[]`, as today) or a non-unit template (`entities[]`: prop, projectile, area, pickup, logic). |
| **Component** | A data block on a template: `visual`, `body`, `team`, `stats`, `attacks`, `targeting`, `movement`, `projectile`, `mover`, `area`, `interact`, `pickup`. |
| **Rule** | `when` (an event) + `if` (conditions) + `do` (actions), with options (`id`, `enabled`, `once`, `cooldown`). |
| **Selector** | An expression that yields one entity or a set: `self`, `other`, `@gate1`, `nearest("foe", 12)`, `all("ally and #undead", 8)`. |
| **Filter** | A string test on a candidate entity: `"foe and Building and not Wall"`, `"#pylon and alive"`, `"in:@moat"`. |
| **Expression** | A typed formula: `self.hpPct < 0.5 and night >= 3`. |
| **Status** | A named, timed, stacking modifier set with its own rules (a buff or debuff). |
| **Signal** | A named message, optionally carrying a number, sent to one entity, a set or everyone. |
| **Variable** | A number, bool, text, position or entity: level-wide (`$souls`) or per entity (`self.$gen`). |
| **Preset** | A saved, parameterised list of rules and components, applied by reference. |
| **Level script** | Rules owned by the level itself (`self` is the level; no position). |

## Data model

Everything is additive to the pack JSON; the pack version goes to 3 (see "Migration"). The rules in
`docs/builder-design.md` hold: null keeps the base's value, unknown fields are kept, and ids are `Ids.IsValid`.

### Where logic lives

```jsonc
{
  "units": [{ "id": "slime", "base": "vanilla:E Melee", /* existing fields */
              "labels": ["goo"], "vars": { "gen": 0 },
              "targeting": [...], "movement": {...}, "attacks": [...],
              "rules": [...] }],                      // NEW on UnitDef (replaces "behaviours")
  "entities": [{ "id": "tree-missile", "kind": "projectile", ... }],   // NEW: non-unit templates
  "statuses": [{ "id": "burning", ... }],             // NEW
  "presets":  [{ "id": "split-on-death", ... }],      // NEW: pack-local presets (library ones are copied in at export)
  "levels": [{ "id": "...", /* existing fields */
    "logic": {                                       // NEW on LevelDef
      "seed": 1234,                                  // optional; default: hash of the level id
      "vars": { "souls": 0, "season": "'summer'" },  // level variables and their start values
      "rules": [...],                                // the level script
      "places": [...],                               // placed entities: templates, areas, paths, points, logic objects
      "attach": { "slot:Tower 3": { "name": "northTower", "labels": ["lit"], "vars": { "hits": 0 }, "rules": [...] },
                  "prop:p12":     { "name": "bigRock", "rules": [...] },
                  "line:Spawn 1": { "name": "westLine" } },
      "hud": [{ "id": "souls", "text": "Souls {$souls}/10", "icon": "asset:skull", "visible": "$souls > 0" }],
      "kingActions": [...]                           // see "King actions"
    } }]
}
```

`attach` gives rules, a name, labels and vars to objects the level already has, by their Studio item key (`slot:<name>`,
`add:<id>`, `prop:<id>`, `line:<name>`, `castle`). That keeps the existing classes untouched: the Studio's `Edit`
writes there, and deleting the object prunes its entry (`PackDraft.Prune`).

### Templates

A **unit** (`UnitDef`, as today) gains `labels`, `vars`, `rules`, `targeting`, `movement`, `attacks` and `statuses` (statuses it starts
with). Its body, team and look come from its base and `visuals`, as now.

A **non-unit template** (`EntityDef`):

```jsonc
{
  "id": "tree-missile",
  "kind": "projectile",            // "prop" | "projectile" | "area" | "pickup" | "logic"
  "name": "Hurled tree",
  "visual": { "mesh": "mesh:Tree_02", "meshScale": 0.4, "tint": null, "motion": "none" },   // VisualsDef: any mesh,
                                    // any unit's model, or an imported asset; null for an invisible entity
  "body": { "radius": 0.8, "height": 2,     // footprint: a circle, or "box": [w, d] (turns with the entity)
            "solid": false,         // blocks ground movement (a NavmeshCut on the ground graphs; flyers pass;
                                    // moving ones update it, see "Movers")
            "walkable": false,      // its top is ground units can walk on (see "Walkable surfaces")
            "hittable": false,      // has hit points and can be targeted and damaged
            "hp": 100,
            "flying": false },      // targeting treats it as air (the Flying tag) when hittable
  "team": "neutral",                // "player" | "enemy" | "neutral" | "owner" (the spawner's team)
  "countsAsEnemy": false,           // a hittable enemy-team entity keeps the night open while alive (default false)
  "labels": ["missile"],
  "vars": {},
  "lifetime": 8,                    // seconds; then the "expire" event, then it's destroyed. null = forever
  "maxAlive": 200,                  // cap on live copies of this template (default 100; engine-wide caps still apply)
  "projectile": { ... }, "mover": { ... }, "area": { ... }, "pickup": { ... }, "interact": { ... },
  "attacks": [...], "targeting": [...],             // a hittable prop can be a turret
  "statuses": ["burning"],                          // statuses it starts with
  "rules": [ ... ]
}
```

A placed entity (`logic.places[]`) is `{ "id", "entity": "<template>" | inline template fields, "pos": [x,y,z],
"rotY", "scale", "name", "active": true, "vars": {...overrides}, "rules": [...extra rules] }`. An area, path, point or
logic object is placed inline (`"kind": "area", "shape": {...}`), since it rarely needs a template. `active: false`
places it switched off until something sends it `enable` (Architect's "Active"). A place may name a template and
also give inline component fields; those override the template's for that copy (`{ "entity": "door", "interact":
{...} }`).

Component fields are literals by default. A string field starting with `=` is an expression, evaluated at spawn
(`"hp": "=100 * night"`); `interact.cost` and `interact.prompt` are re-evaluated whenever the king focuses it.

### Components

**`projectile`**: any entity can fly. `launch` sets its owner, target and start.

```jsonc
"projectile": {
  "speed": 12, "accel": 0,            // m/s, m/s²
  "homing": 0.8,                      // 0 = straight at where the target was; 1 = always hits (the game's own arrows);
                                      // between: turn rate as a fraction of a full re-aim per second
  "arc": 0.3,                         // parabola height as a fraction of the distance (the game's parabula factor)
  "gravity": 0,                       // m/s² for a ballistic shot to a point (overrides arc)
  "spin": [0, 360, 0],                // degrees per second, visual only
  "face": "travel",                   // "travel" | "fixed" | "spin"
  "hits": "foe",                      // filter of what it can strike
  "hitRadius": 1.0,
  "pierce": 0,                        // extra targets it passes through (-1 = all)
  "onGround": "impact",               // "impact" | "stick" (stays until its lifetime ends) | "bounce" | "pass"
  "range": 40                         // m travelled before "expire"
}
```

Events: `impact` (`other` is what it hit, or none for ground; `event.pos`), `expire`.

**`mover`**: moving platforms, rotating walls, patrols and gates.

```jsonc
"mover": {
  "path": "@riverRoute",              // a placed path, or inline [[x,z] | [x,y,z], ...]; null = stays put
  "mode": "pingpong",                 // "loop" | "pingpong" | "once"
  "speed": 3, "wait": 1,              // m/s; seconds paused at each point
  "spin": 0,                          // deg/s around Y (a rotating blade wall); "swing": {"angle": 90, "speed": 45} (a door)
  "running": true,                    // inputs: start, stop, reverse, goto <signal value = point index>, toggle
  "pose": { "open": { "rotY": 90 }, "closed": { "rotY": 0 } }   // named poses; signal "open"/"closed" moves to one
}
```

Events: `arrive` (at each point; `event.value` is the index), `end` (once/pingpong end, and the end of a
`movement.path`; it fires before `then: "destroy"` acts). A solid mover drags its
NavmeshCut; the graph updates at most 4 times a second per mover. Units it would trap are pushed to the nearest
walkable point, the same fix as the terrain stuck bug.

**Walkable surfaces** (`body.walkable`): a prop whose top ground units walk on, such as a bridge, a raft or a frozen
lake's ice. Ground graphs can't gain triangles locally, so a walkable surface appearing, disappearing or reaching a
pose re-runs `GroundGraphs.Scan()`. A static one is scanned once at level start. A mover with `walkable` only snaps
between poses (no gliding), and carries nothing while moving: units on it are moved with it at the snap.
**L2 measures the rescan first.** If a scan of an edited map takes over about 150 ms, runtime walkable changes are
limited to one per 5 s, with a validator warning.

**`area`**: a zone that detects what's inside it.

```jsonc
"area": { "shape": { "circle": { "radius": 6 } },   // or rect {size: [w, d]}, polygon {points}, relative to its position
          "watch": "unit",                           // filter of what it reports (default: units and the king)
          "show": "outline", "color": "#d04040",     // "none" | "outline" | "fill"
          "tick": 1 }                                // seconds between "inside" events (null = no inside events)
```

Events: `enter` / `exit` (`other` is the entity), `inside` (for each entity inside, after every full `tick` it has
spent inside), `empty`, `occupied`.
In filters, `in:@moat` tests membership.

**`pickup`**: `{ "by": "king", "radius": 1.5, "magnet": 4 }` (`by` is a filter: `"king"`, `"player and unit"`).
It fires `pickup` (`other` is the collector), then destroys itself unless `"consume": false` (then a rule decides,
e.g. with `destroy`).

**`interact`**: the king's interact key, the same as the game's buildings. `{ "prompt": "Pay {cost} to open",
"cost": 5, "when": "day", "radius": 3, "repeat": true, "choices": null }`. `choices` (up to 4 `{ "id", "label",
"cost" }`; each `cost` replaces the top-level one and may be an `=` expression) shows the game's upgrade-choice popup
instead, and `event.choice` is the chosen id: a loan of 100, 250 or
500, or "rebuild as tower / house". It fires `interact` (`other` is the king) after the cost is
paid; with `cost` it uses the game's own pay-hold interaction.

**`attacks`**: an automatic attack, like the game's `AutoAttack` but generic. A unit's base attacks stay unless
`"replace": true`.

```jsonc
"attacks": [{
  "id": "hurl", "cooldown": 3, "jitter": 0.1, "range": 18, "minRange": 0,
  "when": "night",                    // "night" (the game's rule) | "always" | "day"
  "targeting": ["foe and Building", "foe"],   // priority list of filters, or full targeting rules (below)
  "launch": { "entity": "tree-missile", "from": [0, 2, 0] },   // OR "weapon": "vanilla:<weapon>" / "inherit" (the
                                                               // game's damage and arrows)
  "do": [ ... ]                       // optional extra actions per attack (other = the target)
}]
```

The game fires `attack` (`other` is the target) for any attack, including vanilla ones. Separately, a unit's own
weapon can keep the game's arrows with a new look: `weapon.projectileLook: "mesh:Tree_02"`.

**`targeting`**: who the unit attacks and walks toward. It's an ordered list (first match wins), as with the game's
`TargetPriority` lists, but with filters and a choice of how to prefer.

```jsonc
"targeting": [
  { "filter": "foe and Tower", "range": 25, "prefer": "nearest" },          // nearest | farthest | weakest |
  { "filter": "foe and Building", "prefer": "best", "score": "-it.hp" },    //   strongest | random | best (+score)
  { "filter": "foe" }
]
```

On a template, `targeting` replaces both the movement and the attack priorities. It works on anything with an
attack, vanilla towers and troops included (`setTargeting` on `all('player and Tower')`). Rules can change it at runtime
(`setTargeting`), including `prepend` and `remove` forms, for "enemies prefer the decoy for 8 s".

**`movement`**: how a unit moves.

```jsonc
"movement": {
  "mode": "pathfind",                 // "pathfind" (the game's A*, default) | "direct" (straight line; flyers,
                                      // ghosts, movers) | "hold" (stays put) | "path" (follows a placed path)
  "path": "@caravanRoute", "then": "pathfind",   // for "path": what to do at the end ("pathfind" | "hold" | "destroy")
  "speed": 5,
  "keepDistance": 12,                 // stand off from the target (the game's keepDistanceOf)
  "kite": { "below": 6, "for": 1.5 }, // if a foe is closer than 6 m, step away for 1.5 s, then re-engage
  "flee": { "when": "self.hpPct < 0.25", "from": "foe", "to": "@exit" },   // retreat rule (expression-gated)
  "wander": { "radius": 8 },          // idle drift around home when nothing to target
  "home": "spawn"                     // "spawn" | a position | "@point"
}
```

It is implemented on the game's own movement components (see "Runtime"), so pathfinding, avoidance and animation keep
working. A ranged enemy that kites and prefers towers is just `targeting` plus `movement.keepDistance` plus `kite`.

### Rules

```jsonc
{
  "id": "split",                      // optional; needed to enable or disable it by name
  "when": { "on": "death" },          // or the shorthand "death"; params per event (below)
  "if": "self.$gen < 3",              // an expression, or a list (all must hold)
  "do": [                             // actions, run in order
    { "do": "spawn", "entity": "=self.template", "count": 2, "at": "self", "spread": 1.5,
      "then": [ { "do": "setVar", "target": "spawned", "name": "gen", "value": "self.$gen + 1" },
                { "do": "setStat", "target": "spawned", "stat": "scale", "mult": 0.75 } ] }
  ],
  "enabled": true,                    // false: off until "enableRule"
  "once": false,                      // fire at most once per owner (per run)
  "cooldown": 0,                      // seconds before this rule can fire again for the same owner
  "note": "Splits into two smaller copies, three times"   // shown on the card and in hovers
}
```

`when` with `of` listens to events of **other** entities. This is WC3's generic unit event:
`{ "on": "death", "of": "foe", "range": 10 }` on a necromancer fires when any foe within 10 m dies. In it, `self` is the
necromancer, `subject` the dead foe, and `other` its killer. `range` is optional (no range = anywhere on the map).
Without `of`, a rule hears only its owner's events. The level script must give `of` for entity events (filters there
treat `foe`/`ally` as relative to the player).

### Events

Every event has `self` and `subject`. The table gives `other`, `event.value` and `event.pos` where they apply. Aliases
(`killer`, `victim`, `attacker`, `target`, `entrant`, `collector`, `sender`) name `other` or `subject` per event, to
keep rules readable.

| Event | Params | Fires when | `other` / value |
|---|---|---|---|
| `spawn` | | the entity appears (placed: at level start; spawned: on creation) | the spawner or launcher, if any |
| `death` | | it dies (a unit) or is knocked out (a building, or the king) | killer / — |
| `damaged` | `min` | it takes damage | attacker (may be none) / amount |
| `healed` | | it is healed | healer / amount |
| `hpBelow` | `pct` | its hp crosses below pct (edge-triggered; re-arms when healed above) | — / hpPct |
| `attack` | `attack` (id) | it attacks (any attack, including vanilla) | target |
| `hit` | | its attack, projectile or `damage` action damaged something | victim / amount |
| `kill` | | something it damaged died from that damage | victim |
| `every` | `seconds`, `jitter`, `phase`, `during` (`night`/`day`/`always`) | periodically, from spawn | — / tick count |
| `after` | `seconds` | once, that long after spawn | |
| `near` | `filter`, `range`, `edge` (`enter`/`exit`/`both`) | a matching entity comes within or leaves the range | the entity |
| `enter` / `exit` / `inside` / `empty` / `occupied` | | (areas) see "area" | entity |
| `impact` / `expire` | | (projectiles, or anything with a lifetime) | hit entity / — |
| `arrive` / `end` | | (movers, path followers, `moveTo`) | — / point index |
| `pickup` / `interact` | | (pickups, interactables) | collector / king |
| `signal` | `name` | it receives the signal | sender / the signal's number (`event.name` is the signal's name) |
| `varChanged` | `name` | one of its variables changes (or a level var, on the level script) | — / new value |
| `statusStart` / `statusEnd` / `statusTick` | `status` (when watching one from outside) | a status starts, ends, or every `tick` seconds of the status | the applier / stacks |
| `targetChanged` | | its attack or move target changes | the new target |
| `built` / `upgraded` / `repaired` | `level` | (build slots) the player builds or upgrades it, or it's revived at dawn | — / level |
| `coinDrop` / `coinPickup` | | (the game's coins: entities of the built-in kind `coin`, so `subject` is the coin) a coin drops from a death / the king collects it, as in the game | the dead unit / the king |
| `lineSpawn` | `line` | a spawn line spawns an enemy (wave or `spawnGroup`) | the new enemy |
| `levelStart` | | the level has loaded (after the overlay, before day 1) | |
| `dusk` / `nightStart` / `dawn` / `day` | `night` | the game's day-night hooks (`OnDusk`, the first spawn, `OnDawn_AfterSunrise`) | — / night number |
| `waveCleared` | | spawning is done and no enemies are left | — / night |
| `goldChanged` | | the player's balance changes | — / delta |
| `kingAction` | `action` | the player presses one of the level's action keys (see "King actions") | king |
| `levelEnd` | `result` | win or loss, before the end screen | — |

The "any unit" variants are `of`-filters on the same events: "any building destroyed" is
`{"on": "death", "of": "player and Building"}`.

### Expressions

One grammar is used everywhere. A param is an expression whenever its type isn't text. A text or template param
becomes an expression with a leading `=`. Text interpolates `{expr}`.

```text
literals    12  0.5  true  'text'  vec(x, z)  vec(x, y, z)
variables   $souls (level)   self.$gen  other.$charges  spawned.$x   (an unknown var is a validation error)
roles       self subject other spawned it (the candidate inside a filter or forEach) king castle level
            event.value event.pos event.name   + the per-event aliases (killer, victim, ...)
named       @gate1 (a place or attach "name")  @@goblin (a template, for spawn/compare)
properties  .hp .maxHp .hpPct .alive .pos .x .y .z .facing (deg) .team .kind .template .labels
            every stat by name (.moveSpeed .attackCooldown .damageMult .income ...), the value with modifiers
            .level .cost .buildingType (slots: e.g. 'Mill') .age (s since spawn) .target .owner .stacks('burning')
            .$var .is('filter') .dist(e) .inside(@area)
lists       .len .at(i) .has(x) .first .last   (sets and list vars)
globals     night (1-based: the current night, or the coming one by day) isNight isDay nights (total)
            time (s since level start) nightTime (s since this night began) nightProgress (0..1: the night's spawn
            schedule elapsed, from the spawner's delays and intervals; 1 once everything has spawned)
            gold income (the last dawn's payout) enemiesAlive difficulty
waves       waveCount(n, f?) (enemies night n will spawn, optionally of a template filter) waveUnits(n) (its
            templates, as a list) strongestUnit(n) (by hp x damage, from the catalog)
selectors   nearest(f, r?) farthest(f, r?) weakest(f, r?) strongest(f, r?) random(f, r?) all(f, r?)
            best(f, r, scoreExpr) count(f, r?)    anchored at self; e.nearest(...), e.count(...) etc. anchor at e
positions   pointNear(p, r) (random walkable) ahead(e, d) toward(a, b, d) lerp(a, b, t) spawnLine('West')
            dir(deg) (a unit vector; 0 = north) angle(a, b) + - and * by a number
maths       + - * / % min max abs floor ceil round clamp sqrt pow sin cos lerp rand() rand(a, b) randInt(a, b) chance(p)
logic       == != < <= > >= and or not  c ? a : b
```

A filter is a string in a smaller grammar, used by selectors, `of`, `hits`, `targeting` and `.is()`:

```text
filter := term | filter and filter | filter or filter | not filter | ( filter )
term   := foe | ally | player | enemy | neutral       teams (foe/ally are relative to self)
        | unit | building | prop | projectile | area | pickup | coin | king | castle | self
        | melee | ranged                              the game's MeeleFighter / RangedFighter
        | type:Mill                                   a building of that kind (the `building:<kind>` names)
        | alive | dead | Flying | Tower | ...         game tags (TagManager.ETag names, case-sensitive)
        | #label                                      labels
        | is:goblin                                   template
        | in:@zone                                    inside an area
        | has:burning                                 has a status
        | where(expr)                                 any expression over `it`
```

Selectors never return `self` unless the filter says `self`. Inside `where(...)`, `self` is still the rule's owner and
`it` the candidate; `in:self` means "inside this area" (for presets). Area membership uses an entity's position, or a
building's footprint collider. Types are number, bool, text, position, entity, set, list and template. An entity coerces to its position, and a set coerces to its count in numeric contexts. The validator
type-checks every expression against the event's roles: `other` in a `levelStart` rule is an error, and `event.value`
has the event's type. Evaluation is pure: functions read state and never change it. `rand` draws from the logic
random source.

### Actions

Every action takes `target` (a selector; default `self`) where it acts on entities. A set applies to each member,
capped at 256; the other params are evaluated once per member, with `it` bound to that member. Positions take `at`,
which accepts an entity or a position.

| Group | Action | Key params |
|---|---|---|
| **Make** | `spawn` | `entity` (template), `count`, `at`, `spread`, `team` (default: the template's; `owner`), `elite` (`inherit`/bool), `facing`, `then` (actions with `spawned` bound) |
| | `launch` | `entity`, `from`, `toward` (entity or position), the `projectile` fields as overrides, `onImpact` (actions; the template's `impact` rules also run), `then` |
| | `spawnGroup` | `unit`, `count`, `interval`, `line` (a spawn line) or `at`, `elite`, `gold`: a wave-style group now (enemies count toward the night) |
| | `replace` | `with` (template), `keep` (`hp`, `vars`, `statuses`, `labels`): swaps the entity for another in place (morph, shapeshift) |
| **Health** | `damage` | `amount`, `source` (credited attacker; default self), `types` (labels such as `fire`, which status modifiers test), `splash` (bool), `weapon` (apply a weapon's tag modifiers) |
| | `heal` / `kill` / `revive` | `amount` / — / `hpPct` |
| | `setHp` | `value`: sets hit points without a `damaged`/`healed` event (shared pools) |
| **Stats** | `setStat` | `stat`, `value` \| `add` \| `mult`, `for` (a timed modifier: seconds, or `"night"`/`"day"` = until the next dawn/dusk) |
| | `applyStatus` / `removeStatus` | `status`, `for`, `stacks`, `source` |
| **Team** | `convert` | `team`, `keepLook` (true), `hpPct`. A dead target is raised as a new entity of the new team. |
| **Move** | `teleport` | `at` |
| | `push` | `from` (or `toward` for a pull; entity or position), `force` (metres of travel), `up` (arc), `stun`: a one-off shove on units (flyers included, not buildings), through the game's knockback |
| | `drift` | `dir` (a vector) or `toward`/`from` (an expression; `other`/`self` work in a status), `speed` (m/s), `for`, `key`: a continuous velocity added to movement (units and Builder projectiles): a pull, a current, wind. Drifts with different keys add up; the same key replaces |
| | `moveTo` | `at`, `speed`, `then` (`resume`/`hold`): fires `arrive` when there |
| | `setMovement` / `setTargeting` | a movement block / a targeting list, or `prepend`, `remove`, `for` (seconds; reverts after) |
| | `taunt` | `toward` (an entity), `for`: forces the targets' aggro onto it (the game's agro mechanism) |
| | `command` | `order` (`follow`/`hold`/`home`): player units, as the king's own commands |
| **Logic** | `signal` | `name`, `to` (selector, or `"all"`), `value`, `delay` |
| | `setVar` / `addVar` | `name` (a literal, so it's checked), `value` (on `target` for entity vars; level vars when `target` is `level` or the name has `$`) |
| | `listAdd` / `listRemove` / `listClear` | `name`, `value` (add; or remove the first equal item), `index` (remove at): list vars of one element type (max 256 items, oldest dropped past that). Lists keep insertion order; sets from selectors are ordered by distance from the anchor, then entity id |
| | `addLabel` / `removeLabel` | `label` |
| | `enableRule` / `disableRule` | `rule` (id), `target` |
| | `enable` / `disable` / `toggle` | the entity's rules, components (its solid cut and hittability too) and visibility; on a spawn line, pauses or resumes its spawning (the night's schedule waits for it) |
| | `wait` | `seconds`: the rest of the list resumes later (min 0.05 s) |
| | `if` | `cond`, `then`, `else` |
| | `repeat` | `times` (≤ 100), `every` (seconds, 0 = now), `actions` (with `event.value` = the iteration; not `do`, which names the action) |
| | `forEach` | `in` (a set), `actions` (with `it`), `limit` |
| | `choose` | `options: [{ "weight": 2, "do": [...] }, ...]` |
| | `stop` | ends this action list |
| **World** | `gold` | `amount` (negative takes, floored at 0), `at` (shows the coin effect there) |
| | `dropCoins` | `count`, `at` (real coins the king collects) |
| | `building` | `slot` (an attach name, a selector or a Studio key), `op`: `build` \| `upgrade` \| `knockOut` \| `repair` \| `demolish` (back to an empty slot, gold not refunded unless `value`) \| `lock` (hides an unbuilt slot; a built one keeps standing but can't be upgraded) \| `unlock` \| `setCost` \| `setIncome`, `value`, `branch` |
| | `waves` | `op` with its fields: `addSpawn` (`unit`, `count`, `interval`, `delay`, `line`, `gold`, `elite`), `removeUnit` (`unit`), `scaleCount` / `scaleHp` (`value`, optional `unit`), `clearRemaining` (optional `line`: drops what the running night hasn't spawned yet), `startNight`, `endNight`, `skipNight`. `night`: default the next night; `"current"` edits the running night's remaining schedule |
| | `endLevel` | `result` (`win`/`lose`), `message` |
| **Show** | `effect` | `fx` (a game effect from the catalog), `at`, `scale` |
| | `sound` | `sound` (`asset:` or a game sound), `at` |
| | `message` | `text`, `kind`, `seconds`: `Notify` |
| | `floatText` | `text`, `at`, `color` |
| | `setVisual` | `tint`, `scale`, `mesh`, `visible`, `glow`, `for` |
| | `hud` | `id`, `text`, `icon`, `visible`: a level HUD line (the `logic.hud` defaults, changed at runtime) |
| | `shake` | `strength`, `seconds` |
| | `marker` | `on`, `icon`, `for`: a screen-edge marker like the game's wave markers |
| **End** | `destroy` | `effects` (death effects, true), `drops` (coins, false), `silent` (no `death` event) |

The action set is closed, and each action is generic. A new action needs a mod update, so additions are judged by
whether they unlock many behaviours rather than one.

### Statuses

```jsonc
"statuses": [{
  "id": "burning", "name": "Burning", "icon": "asset:fire",
  "duration": 4, "stacking": "refresh",        // "refresh" | "add" (stacks, each its own timer) | "extend" | "ignore"
  "maxStacks": 5, "tick": 1,                   // seconds between statusTick events
  "mods": {                                    // applied while active; per stack, "mult" compounds (0.8^n) and
                                               //   "add" sums (n x add)
    "moveSpeed":   { "mult": 0.8 },
    "damageTaken": { "mult": 1.25, "types": ["fire"] }        // only from damage with those types
  },
  "flags": [],                                 // "stunned" | "disarmed" | "rooted" | "untargetable" |
                                               //   "invulnerable" | "silenced" (its rules don't fire) | "noIncome"
  "visual": { "tint": "#ff7020", "fx": "fire_small" },
  "rules": [ { "when": "statusTick", "do": [
      { "do": "damage", "amount": "3 * self.stacks('burning')", "types": ["fire"], "source": "other" } ] } ]
}]
```

Stats a status (or `setStat`) can change: `maxHp`, `hp`, `moveSpeed`, `attackCooldown`, `attackRange`, `damageMult`,
`damageTaken`, `projectileSpeed`, `scale`, `keepDistance`, `aggroTime`, `income` (buildings), `timeScale` (multiplies
move speed, attack and rule timers, and its own projectiles). `damageTaken` and `damageMult` modifiers may carry
`from` (a filter on the attacker), `types`, `chance` and `when` (an expression with the extra roles `attacker`,
`victim` and `dist`; `self` is the carrier).
That covers armour, dodges, "immune to arrows", "misses at long range" and "vulnerable to fire".

- **Damage types.** Builder damage carries the `types` its action or projectile gives. Vanilla damage gets derived
  types: `melee` or `ranged` (the attacker's fighter tag), `tower`, `king`, `splash` and `magic` (`MagicProjectile`).
- **Status rules** run with `self` = the carrier and `other` = whoever applied it. `statusEnd` fires on expiry and
  on removal.
- **Timed `setStat`s** stack independently; give `key` to make a new one replace an earlier one with the same key.
- `damaged` doesn't fire when modifiers reduce the damage to 0, so an invulnerable door doesn't spam rules.
- `drift` is a status mod as well (`"drift": { "toward": "@well", "speed": 3 }`).

### Signals, wires and built-in inputs

- `signal` delivers `{name, value, sender}` to targets. Their `signal` rules with that name fire. `to: "all"`
  broadcasts to every entity with a matching listener and to the level script, which works as channels.
- **Built-in inputs**, which need no listener rule: `enable`, `disable`, `toggle`, `destroy`. Movers add `start`,
  `stop`, `reverse`, `goto`, and one per named pose (`open`, `closed`). Areas add `show` and `hide`. Interactables add
  `lock` and `unlock`. They appear in the Wire tool's input list like listener rules do.
- A **wire** in the Studio is exactly a rule on the source (`when: <event>, do: [signal <name> to @target]`), plus a
  listener on the target unless it's a built-in input. There is no separate wire table, so agents and the Studio
  write the same thing.

### Variables

- Level vars are declared in `logic.vars` with a start value: number, bool, `'text'`, `vec(...)`, `[]` (a list), or
  `{ "type": "entity" | "list" | ..., "value": ... }` when the start value doesn't say the type (an entity var
  starts empty). Entity vars are declared in the template's `vars`, and a placed entity can override them.
- `setVar` on an undeclared name is a validation error, which catches typos. Changing a var fires `varChanged`.
- HUD lines and messages interpolate vars, so counters and objectives need no extra primitive.

### King actions

`logic.kingActions: [{ "id": "horn", "name": "Blow the horn", "icon": "asset:horn", "when": "day", "cooldown": 0,
"uses": { "count": 1, "per": "day" } }]` (at most 3; `per`: `day` | `night` | `run`). They map to three rebindable keys, "Level action 1-3" (context `BuilderLevel`,
registered in `Awake` so they get Controls-screen rows). The key fires `kingAction` with that id, and the HUD shows
the action with its cooldown.

### Presets

```jsonc
"presets": [{
  "id": "split-on-death", "name": "Split on death", "description": "Splits into {count} copies, {depth} times.",
  "params": { "count": { "type": "number", "default": 2, "min": 1, "max": 6 },
              "depth": { "type": "number", "default": 3 },
              "into":  { "type": "template", "default": "=self.template" } },
  "vars": { "gen": 0 },
  "rules": [ { "when": "death", "if": "self.$gen < {depth}", "do": [
      { "do": "spawn", "entity": "{into}", "count": "{count}", "at": "self", "spread": 1.5,
        "then": [ { "do": "setVar", "target": "spawned", "name": "gen", "value": "self.$gen + 1" } ] } ] } ]
}]
```

Using a preset is an entry in `rules`: `{ "preset": "split-on-death", "with": { "count": 3 } }`. It's expanded when
the pack is compiled, with `{param}` substituted as a typed value, so the validator checks the result with paths
into the preset. Presets can also carry components (`"movement"`, `"area"`, ...), merged under the entity's own.

- **Built-in presets** ship in the DLL (`Assets/builder-presets.json`) and are just data. They include the logic
  objects: Timer, Counter, Relay (delay), Gate (AND of N signals), Toggle, Random router, Spawner, Lever (interact),
  Pressure plate (area), Moving wall (mover), Door (poses), Sign (message), Pickup (gold).
- **Library presets** live in `PalaceBuilder/library/presets/<id>.json`, next to levels and enemies (R2-C1's store).
  "Save as preset" on any card or selection writes one, and an export copies every preset that's used into the pack.

## Runtime

### Shape

```text
Features/Builder/Logic/
  Runtime/LogicManager.cs        BaseManager: lifecycle per session, Harmony patches (nested), dev command `logic`
  Runtime/LogicWorld.cs          the session's world: registry, bus, scheduler, vars, rng, budgets, trace
  Runtime/BuilderEntity.cs       MonoBehaviour on every Builder entity: id, template, team, labels, vars, statuses,
                                 compiled rules, listeners
  Runtime/EventBus.cs            typed events → listeners (own rules + `of` listeners indexed by event type)
  Runtime/Scheduler.cs           timers (`every`/`after`), waits, timed modifiers, repeat
  Runtime/Compiler.cs            RuleDef/expressions (Model) → closures over the world; preset expansion
  Runtime/Hooks.cs               game → events (table below)
  Actions/*.cs                   one file per action group
  Bodies/EntityFactory.cs        non-unit templates → GameObjects (AssetVisuals for looks)
  Bodies/BuilderProjectile.cs, BuilderMover.cs, BuilderArea.cs, BuilderPickup.cs, BuilderInteractor.cs, BuilderAttack.cs
  Bodies/Stats.cs                modifier stacks → game fields; statuses
  Bodies/Steering.cs             targeting and movement overrides
Model/Logic/                     pure C#: defs, expression parser + type checker, LogicSchema (the registry), validation
```

The **schema registry** (`Model/Logic/LogicSchema.cs`) is the single source of truth for every event, action,
component field, stat, flag and function: name, params (type, required, default, range), roles, description, example
and the `since` version. The validator, the Studio pickers and hovers, `builder dump` (which writes `logic-schema.json`
for agents), `reference.md` and a test that every entry has a runtime handler all read it.

### Hooks: where events come from

| Event | Hook |
|---|---|
| `spawn` | `BuilderEntity.Start`; vanilla units for `of` listeners: a `TagManager.AddTaggedObject` postfix |
| `damaged`, `death`, `kill`, `hit` | a `Hp.TakeDamage` prefix/postfix pair (hp before and after, source, `__result` = killed). `hit` and `kill` go to the source's entity, or to a projectile's owner. |
| `healed` | `Hp.Heal` postfix |
| `attack` | `AutoAttack.OnAttack` postfix and the overrides (`AutoAttackTower`, `...WithKnockback`, `...WithWarning`), `ManualAttack` (the king), and `BuilderAttack` |
| `dusk`/`dawn`/`day` | one `DayNightCycle.IDaytimeSensitive` registered by the world |
| `nightStart`, `waveCleared` | `EnemySpawner` wave number change; `SpawningInProgress` false and `NumberOfEnemiesOnTheMap == 0` |
| `built`/`upgraded` | `BuildSlot.OnUpgrade` subscriptions at level start (and on added slots) |
| `death`/`repaired` (buildings) | a building's `Hp` knock-out (it has `getsKnockedOutInsteadOfDying`) / `Hp.Revive` postfix |
| `goldChanged` | `PlayerInteraction.onBalanceGain` / `onBalanceSpend` |
| `near`, areas | the world's spatial poller (below) |
| `interact` | `BuilderInteractor : InteractorBase` on the interactor layer |
| `kingAction` | the three level keybinds |
| `levelStart`/`levelEnd` | the session's overlay-applied hook / `TransitionFromGameplayToEndScreen` prefix |

Synchronous modifiers (not rules) run inside the game's own calls:

- `damageTaken` with its `from`, `types` and `chance` filters, `invulnerable`, and shields: a `Hp.TakeDamage` prefix
  scales `_amount`.
- `untargetable`: a check in a `TagManager.Find*` postfix that removes flagged objects. It's a HashSet lookup per
  candidate, and only while any entity has the flag.
- `stunned`, `disarmed` and `rooted`: `AutoAttack.Update` and movement prefixes return early.
- `noIncome` and `income`: the dawn income computation.
- `timeScale`: folded into speed and cooldown.

### Execution model

- **Deferred, ordered dispatch.** Hooks only enqueue events, with a sequence number. The world drains the queue in
  `LateUpdate`, in order. Nothing runs inside `Hp.TakeDamage` or a game iteration, so rules can't break the game's
  loops, and a rule that kills something sees a consistent world. Latency is at most one frame. Events that actions
  raise go to the back of the queue and are drained in the same `LateUpdate`, up to the budget.
- **Entity ids.** A place uses its own id. An attached object uses its Studio key. A spawned entity gets
  `<template>#<n>` from a per-session counter. A vanilla entity gets `SaveStates.PathOf`. All are deterministic, so
  they survive savestates.
- **Teams.** `player` and `enemy` are the game's `PlayerOwned`/`EnemyOwned` tags. `neutral` has neither, so vanilla
  AI ignores it, and only Builder targeting rules can target it. Only hittable enemy entities with `countsAsEnemy`
  carry `EnemyOwned`, because the game ends a night when no `EnemyOwned` object is left.
- **Converting a team.** A live unit's side is more than a tag: it has a different movement component, attack
  priorities and materials. So `convert` replaces it with the counterpart template, then restores look, hp %, vars,
  labels and statuses:
  - the counterpart is the template's `counterpart` field if given, or else the catalog base of the other side with
    the same role: melee becomes a swordsman-like unit, ranged an archer-like unit, flying a flying unit;
  - a converted player unit has no building, so it holds where it was converted, or follows the king if `order` says
    so;
  - converting a dead target is the same operation, without the original.
- **Time words.** By day, `night` is the coming night, and an action's `night` param defaults to it (the "next" night
  is the same one). Pausing a spawn line (`disable`) freezes `nightProgress` while every line with remaining spawns is
  paused; `clearRemaining` drops those spawns, so a night with everything cleared and no enemies left ends normally.
- **Dawn order.** At dawn: modifiers `for: "night"` expire, the game pays income (`income` stats and `noIncome`
  apply), then `dawn` rules run. `income` in a `dawn` rule is today's payout.
- **Spawning enemies.** `spawn` and `spawnGroup` of enemy units go through `Spawn.AdjustEnemyParametersAfterSpawn`
  (perks, difficulty, elite) and the template-aware path that exists today, so they count toward the night like wave
  enemies.

### Safety and performance limits

| Limit | Value | When hit |
|---|---|---|
| Live Builder-spawned entities | 300 (units 200, projectiles 400 counted separately), per-template `maxAlive` (default 100) | the spawn is skipped, and the debug trace and `logic status` count it |
| Rule firings per frame | 400; action steps per frame 4000 | the rest is carried over to the next frame; a "throttled" mark appears in the trace |
| Chain depth (an event raised by an action raised by an event...) | 12 | the event is dropped, logged once per rule |
| Same rule on the same owner | 30 per second | further firings that second are dropped |
| `every` / `wait` / `repeat.every` floor | 0.1 s / 0.05 s / 0.05 s | clamped by the validator (warning) |
| `repeat.times`, `forEach`, set size | 100, 256, 256 | clamped |
| Area and `near` polling | 10 Hz, on a 4 m spatial hash of the tagged objects | — |
| Solid mover navmesh updates | 4 per second per mover, 20 per second in total | queued |
| Expression size | 512 characters | validation error |

The validator also does a static loop check. A rule whose `signal` reaches a listener that, with no `wait`, signals
back to the first rule is a warning ("this loop runs until the chain-depth cap"). Target cost: under 0.5 ms per frame
with 200 entities and 3 rules each. A `Logic` line in `PerformanceManager`'s summary reports events, actions and
throttles per second.

### Determinism, savestates and retry

- **Randomness.** `rand`, `chance`, `random(...)`, `pointNear` and `choose` use a xorshift source seeded from
  `logic.seed`, never `UnityEngine.Random`. Polling and timers advance on fixed 0.1 s steps of game time, and
  same-frame events are ordered by sequence number and entity id.
  - The game itself isn't deterministic (frame times, its own `Random`), so this promises "same events in give the
    same rule outcomes", not replays.
  - It is what makes the core testable: `tests/` drives `LogicWorld` with a fake world at fixed ticks.
- **Savestates.** `SaveStates.RegisterState("logic", ...)` captures:
  - level and entity vars, rule `enabled` / `once` / cooldown state, scheduler phases, and statuses (remaining time
    and stacks);
  - the RNG state and HUD lines;
  - every live Builder-spawned entity (template, position, rotation, team, hp, vars, statuses, mover phase), which is
    respawned on restore.

  Pending `wait`s and projectiles in flight are not restored. This matches the existing "not restored" list, and
  rules use `once`/vars, not waits, for state that matters.
- **Retry day.** The world keeps an in-memory copy of the same state at each dawn. The game's "Retry Day" restores the
  copy for that dawn, so logic state matches the rewound match.

### Sessions, the Studio and the arena

- Logic runs only in Builder sessions (custom runs and Studio play), on the session's pack. Vanilla runs never load
  it, and `GameplayImpact` is already marked for custom content.
- In the Studio's edit mode nothing runs. Areas, paths, wires and movers' paths are drawn as overlays instead.
- The Enemy Studio arena runs an enemy's rules during a fight. Arena-marked entities get `isNight` true, as their
  attacks already do, and level vars live in a throwaway world.

## Validation and errors

`Validator` gains `ValidateLogic`. Every issue has the existing JSON path, plus a column for expressions, so the
Problems popup selects the object and opens the right card. Examples:

```text
levels[0].logic.places[3].rules[1].if: col 9: unknown variable "$soul" (did you mean "$souls"?)
units[2].rules[0].do[1].target: "other" is not defined for the event "levelStart"
levels[0].logic.attach["slot:Tower 9"]: no build slot "Tower 9" on Nordfels
entities[0].projectile.homing: 1.4 is outside 0..1
units[1].rules[0]: preset "split-on-deth" not found (did you mean "split-on-death"?)
units[1].rules[0].do[0].entity: "=self.template" is a template, but "count" expects a number: "{count}" → "two"
```

Unknown actions or events are errors: "unknown action "pull" (made with a newer PalaceMod?)". The schema's `since`
fields fill `requires.palaceMod` on save, so an older mod refuses the pack with the right message.

## Authoring

### Agents (`palace.py`)

New ops (JSON, as the existing ones):

| Op | Fields |
|---|---|
| `entity` | a whole EntityDef (adds or replaces) |
| `status` / `preset` | a whole StatusDef / PresetDef |
| `place` | `id`, `entity` or inline `kind` + fields, `at`/`pos`, `rotY`, `name`, `active`, `vars`, `rules` |
| `rule` | `on`: `level` \| `unit:<id>` \| `entity:<id>` \| `place:<id>` \| a Studio key (`slot:Tower 3`); `rule` (a RuleDef, replacing one with the same `id`) |
| `remove_rule` | `on`, `id` |
| `wire` | `from`, `event`, `to`, `input` (a built-in input or a signal name), `value`, `delay`: writes both rules |
| `var` | `name`, `value` (level) or `on` + `name` + `value` (entity default) |
| `attach` | `key`, `name`, `labels` |
| `king_action` | `id`, `name`, `when`, `cooldown`, `uses` |

Commands:

- `palace.py logic schema [--json] [event|action|stat|fn <name>]`: from `logic-schema.json`, cached by `ref build`.
- `palace.py logic check <pack> [level]`: offline structure and expression checks. A Python port of the expression
  parser and type checker is kept in step with C# by a shared vector file, `tests/logic-expr-cases.json`, which both
  test suites run.
- `palace.py logic trace`: the live debug trace (rules fired, throttles and errors per night). `test` night reports
  gain a `logic` section.
- `waves` and `economy` read `spawn`-on-`death` rules (and `gold` actions on `death`) when estimating a night.

`level-building.md` gets a "Behaviour" chapter (the concepts, the cookbook from "Expressiveness check" below, and the
limits), and `reference.md` gets the generated schema.

### Creators (the Studios)

```text
┌ Inspector ─ Slot · Behaviour · Units ────────────────────────────┐
│ Name  northTower          Labels  [lit ×] [+]                     │
│ Variables  charges 0  [+]                                         │
│ ┌ When  [dies ▾]  of [this ▾]                            ⋮  ☑    ┐│
│ │ If    [self.$gen < 3]                     [+ condition]          ││
│ │ Then  1  Spawn  [Slime ▾] × [2]  at [this ▾]  spread [1.5]     ││
│ │           then  Set var [gen] on [spawned] = [ƒ self.$gen + 1]││
│ │       [+ action]                                               ││
│ └ cooldown 0 s · once ☐ · fired 3× this night (play)            ┘│
│ [+ rule]  [+ preset ▾]                                            │
└───────────────────────────────────────────────────────────────────┘
```

- **Behaviour tab.** Every selected thing gets one: a slot, a prop, a line, a placed entity, an area or the castle,
  through `Studio.AddInspectorSection`. The Enemy Studio's "Abilities" tab becomes the same Behaviour tab, plus
  Targeting, Movement and Attacks sections. Rules are cards with When / If / Then rows.
- **Chip pickers.** Each chip opens a `ModPopup` picker: categories (This unit, Combat, Time, Night, Areas,
  Buildings, Gold, King, Signals, Variables), a search box, and a one-line description of each entry from the schema.
- **Typed fields.**
  - Numbers are `page.Number`, with a ƒ toggle to switch to an expression.
  - Entities offer the roles valid for this event, "Pick on map" (`Studio.PickOnMap`), and "Nearest / All / Random…"
    (a filter builder popup with team, kind, tag and label chips).
  - Templates are palette tiles.
  - Positions offer roles, "Pick on map" and offsets.
  - Text is a text field with a `{…}` inserter.
  - Expressions are validated as you type, with the error under the field.
- **Card actions.** Cards drag to reorder. Right-click offers Duplicate, Disable, Copy/Paste (JSON on the clipboard,
  the same form agents write) and Save as preset.
- **Wire tool** (a toolbar tool beside Link). Drag from one object to another and a popup opens: on the left, the
  source's events, common ones first; on the right, the target's inputs, built-ins first, plus "new signal…". It
  writes the two rules.
  - Wires draw as arcs, in the style of `RequirementArrows`, labelled `dies → open` and coloured by signal name.
    Hovering one shows both rules; Del removes them.
  - "Show wires" in View ▾ draws all of them. Selecting an object always shows its own.
- **Logic palette tab.** Area, Path, Point, Pressure plate, Timer, Counter, Relay, Gate, Toggle, Random, Spawner,
  Lever, Door, Moving wall, Sign, Pickup and HUD counter, each a built-in preset placed like any other piece. The
  Place tool draws areas (circle/rect/polygon) and paths (click points; Enter ends).
- **Level tab.** "Variables" (name, type, start value), "Level script" (cards with `self` = level) and "King actions".
- **Templates.** Non-unit templates (projectiles, pickups, props with logic) open in the Enemy Studio's stage, which
  becomes "Entity Studio" with a kind picker. Its Look tab already takes any mesh. Projectiles get a "Launch test"
  button in the arena.
- **Play mode.** The sandbox panel gets a "Logic" section:
  - live variables (editable);
  - the trace: the last 100 rule firings with time, owner, rule note and outcome, and throttles and errors in red;
  - a budget meter and a "Send signal" field.
  - In the world, a small pip flashes over an entity when one of its rules fires (toggle in View ▾).

## Migration

- Pack version 2 → 3. `Migrations.Steps[2]`:
  - moves each `behaviours: [{ "type": "SpawnOnDeath", "unit": U }]` to
    `rules: [{ "when": "death", "do": [{ "do": "spawn", "entity": U, "at": "self", "elite": "inherit" }] }]`;
  - drops `behaviours`.

  The runtime's `spawn` of an enemy on `death` uses the same `Spawn.AdjustEnemyParametersAfterSpawn` path as the
  game's `enemyToSpawnOnDeath`, so play doesn't change. `UnitFactory.ApplyBehaviour` and `BehaviourTypes` go away.
- **Readers.** `LibraryStore` (enemies that reference other enemies), `UnitPage`/`EnemyDock` ("spawns on death") and
  the agent kit's `waves` read references from rules: any template param.
- `Catalog` keeps listing vanilla `enemyToSpawnOnDeath`, for the Enemy Studio's "what the base already does".

## Expressiveness check

Each behaviour below is pure data in the format above. Unit bases and meshes are illustrative.

**1. An enemy that hurls homing trees** (one of the user's examples)

```jsonc
"entities": [{ "id": "tree-missile", "kind": "projectile", "visual": { "mesh": "mesh:Tree_02", "meshScale": 0.4 },
  "lifetime": 6, "projectile": { "speed": 9, "homing": 0.6, "arc": 0.2, "spin": [0, 0, 360], "hits": "foe" },
  "rules": [{ "when": "impact", "do": [
    { "do": "damage", "target": "all('foe', 2)", "amount": 25, "source": "self.owner" },
    { "do": "effect", "fx": "dust_large", "at": "self" } ] }] }],
"units": [{ "id": "treant", "base": "vanilla:E Ogre",
  "attacks": [{ "replace": true, "cooldown": 3, "range": 18, "targeting": ["foe and Building", "foe"],
                "launch": { "entity": "tree-missile", "from": [0, 3, 0] } }] }]
```

**2. Kills convert the victim** (the user's example)

```jsonc
{ "when": "kill", "if": "other.is('unit and not king')", "do": [{ "do": "convert", "target": "other", "team": "self.team", "hpPct": 0.5 }] }
```

**3. A unit that multiplies every 8 s** (the user's example, capped)

```jsonc
{ "when": { "on": "every", "seconds": 8, "during": "night" }, "if": "count('ally and is:spore') < 40",
  "do": [{ "do": "spawn", "entity": "=self.template", "at": "pointNear(self, 2)" }] }
```

**4. Necromancer: a foe dying within 12 m has a 30 % chance to rise as a skeleton enemy**

```jsonc
{ "when": { "on": "death", "of": "foe and unit", "range": 12 }, "if": "chance(0.3)",
  "do": [{ "do": "spawn", "entity": "skeleton", "at": "subject" }, { "do": "effect", "fx": "summon", "at": "subject" }] }
```

**5. A splitting slime, three generations deep:** the `split-on-death` preset shown above.

**6. A pressure-plate trap** (an area; it damages everything inside once a second while enemies stand on it)

```jsonc
{ "kind": "area", "id": "plate1", "pos": [12, 0, -4], "area": { "shape": { "rect": { "size": [4, 4] } }, "watch": "enemy", "show": "fill" },
  "rules": [{ "when": { "on": "every", "seconds": 1 }, "if": "count('enemy and in:self') > 0", "do": [
    { "do": "wait", "seconds": 0.5 },
    { "do": "damage", "target": "all('unit and in:@plate1')", "amount": 30 },
    { "do": "effect", "fx": "spikes", "at": "self" } ] }] }
```

**7. A drawbridge:** it rises when the "Lever" place is pulled, and lowers at dusk.

```jsonc
{ "id": "bridge", "entity": "drawbridge", "pos": [...],     // template: mesh, body.solid, mover.pose {up, down}
  "rules": [{ "when": "dusk", "do": [{ "do": "signal", "name": "down", "to": "self" }] }] },
{ "id": "lever", "kind": "logic", "pos": [...], "interact": { "prompt": "Raise the bridge", "when": "day" },
  "rules": [{ "when": "interact", "do": [{ "do": "signal", "name": "up", "to": "@bridge" }] }] }
```

**8. Shield-bearer aura:** allies within 6 m take 30 % less damage, refreshed every second.

```jsonc
"statuses": [{ "id": "warded", "duration": 1.2, "stacking": "refresh", "mods": { "damageTaken": { "mult": 0.7 } }, "visual": { "fx": "ward" } }],
"rules": [{ "when": { "on": "every", "seconds": 1 }, "do": [{ "do": "applyStatus", "status": "warded", "target": "all('ally and unit', 6)" }] }]
```

**9. A kamikaze bat** that goes for the king and explodes when it is close:
`targeting: [{ "filter": "king" }]`, `movement.mode: "direct"`, plus

```jsonc
{ "when": { "on": "near", "filter": "king", "range": 2 }, "do": [
  { "do": "damage", "target": "all('foe', 3)", "amount": 40 }, { "do": "effect", "fx": "explosion", "at": "self" },
  { "do": "destroy", "drops": true } ] }
```

**10. A merchant caravan:** a neutral unit walks a path by day and pays out if it arrives. At night, enemies go for
it first.

```jsonc
"movement": { "mode": "path", "path": "@tradeRoute", "then": "destroy" },
"rules": [ { "when": "end", "do": [{ "do": "gold", "amount": 15, "at": "self" }, { "do": "message", "text": "The caravan arrived: +15 gold" }] },
           { "when": { "on": "spawn", "of": "enemy" }, "do": [{ "do": "setTargeting", "target": "subject", "prepend": ["is:caravan"] }] } ]
```

**11. A charging tower:** every fifth hit fires a piercing bolt. This is an `attach` on a slot.

```jsonc
"attach": { "slot:Tower 3": { "vars": { "hits": 0 }, "rules": [{ "when": "hit", "do": [
  { "do": "addVar", "name": "hits", "value": 1 },
  { "do": "if", "cond": "self.$hits % 5 == 0", "then": [{ "do": "launch", "entity": "bolt", "toward": "other", "pierce": -1 }] } ] }] } }
```

**12. A volcano:** at night, every 12 s it lobs lava at a random spot within 15 m. Each impact burns both sides and
leaves a 5 s fire area.

```jsonc
{ "id": "volcano", "kind": "logic", "pos": [...], "rules": [{ "when": { "on": "every", "seconds": 12, "during": "night" },
  "do": [{ "do": "launch", "entity": "lava-rock", "toward": "pointNear(self, 15)" }] }] }
// lava-rock: projectile { gravity: 9.8 }, rules: impact → damage all('unit', 3) 35 types [fire];
//            spawn "fire-area" at self   (an area with lifetime 5, tick 1: inside → applyStatus burning to other)
```

**13. Mercenary camp:** the king stands in a camp by day for 3 s and pays 10 gold; three mercenaries join until dawn.

```jsonc
{ "kind": "area", "id": "camp", "area": { "shape": { "circle": { "radius": 4 } }, "watch": "king", "tick": 3 },
  "rules": [{ "when": "inside", "if": "isDay and gold >= 10", "cooldown": 60, "do": [
    { "do": "gold", "amount": -10 },
    { "do": "spawn", "entity": "merc", "count": 3, "at": "self", "spread": 2, "team": "player",
      "then": [{ "do": "command", "target": "spawned", "order": "follow" }] } ] }] }
// merc template: rules [{ when: "dawn", do: [{ do: "destroy", silent: true }] }]
```

**14. Revenge:** when the castle is hit, every tower fires twice as fast for 5 s (level script).

```jsonc
{ "when": { "on": "damaged", "of": "castle" }, "cooldown": 5,
  "do": [{ "do": "setStat", "target": "all('player and Tower')", "stat": "attackCooldown", "mult": 0.5, "for": 5 }] }
```

**15. Escalation:** if two or more shrines are still standing at dusk of night 4 or later, add a brute group to the
night.

```jsonc
{ "when": "dusk", "if": "night >= 4 and count('#shrine and alive') >= 2",
  "do": [{ "do": "waves", "op": "addSpawn", "night": "night", "unit": "brute", "count": "night - 2", "line": "North" }] }
```

**16. A rotating blade wall:** `mover.spin: 90`, `body.solid: true`, and
`{ when: { on: "every", seconds: 0.5 }, do: [{ do: "damage", target: "all('unit and not Flying', 3)", amount: 8 }] }`.

**17. A lantern ghost:** it's invulnerable unless it's within 8 m of a lantern.

```jsonc
"rules": [{ "when": { "on": "every", "seconds": 0.5 }, "do": [{ "do": "if", "cond": "count('#lantern', 8) == 0",
  "then": [{ "do": "applyStatus", "status": "veiled", "for": 0.6 }] }] }]   // veiled: flags [invulnerable], tint pale
```

**18. An assassin that blinks away:** below half health, once, it teleports near the castle and heals 30 %.

```jsonc
{ "when": { "on": "hpBelow", "pct": 0.5 }, "once": true, "do": [
  { "do": "effect", "fx": "smoke", "at": "self" }, { "do": "teleport", "at": "pointNear(castle, 10)" }, { "do": "heal", "amount": "self.maxHp * 0.3" } ] }
```

**19. Chain lightning:** a bolt that jumps to up to 3 more enemies, each jump weaker.

```jsonc
{ "id": "spark", "kind": "projectile", "vars": { "jumps": 3, "power": 30 }, "projectile": { "speed": 30, "homing": 1, "hits": "foe" },
  "rules": [{ "when": "impact", "do": [
    { "do": "damage", "target": "other", "amount": "self.$power", "source": "self.owner" },
    { "do": "if", "cond": "self.$jumps > 0 and count('foe and not has:shocked', 8) > 0", "then": [
      { "do": "applyStatus", "target": "other", "status": "shocked", "for": 0.5 },
      { "do": "launch", "entity": "spark", "from": "other", "toward": "nearest('foe and not has:shocked', 8)",
        "then": [{ "do": "setVar", "target": "spawned", "name": "jumps", "value": "self.$jumps - 1" },
                 { "do": "setVar", "target": "spawned", "name": "power", "value": "self.$power * 0.7" }] } ] } ] }] }
```

**20. A day-night shapeshifter:** at dusk it becomes a werewolf, and at dawn it turns back.

```jsonc
[{ "when": "dusk", "do": [{ "do": "replace", "with": "werewolf", "keep": ["hp", "vars"] }] }]   // werewolf has the mirror rule at dawn
```

**21. A toll gate:** a solid door opens for 5 gold paid by the king, and closes at dusk.

```jsonc
{ "id": "toll", "entity": "door", "interact": { "prompt": "Pay {cost} to open", "cost": 5, "when": "day" },
  "rules": [{ "when": "interact", "do": [{ "do": "signal", "name": "open", "to": "self" }] },
            { "when": "dusk", "do": [{ "do": "signal", "name": "closed", "to": "self" }] }] }
```

**22. A soul counter objective:** each enemy killed in the ritual circle adds a soul. At 10, the run is won.

```jsonc
"vars": { "souls": 0 }, "hud": [{ "id": "souls", "text": "Souls {$souls}/10" }],
"rules": [{ "when": { "on": "death", "of": "enemy and in:@ritual" }, "do": [{ "do": "addVar", "name": "souls", "value": 1 }] },
          { "when": "varChanged", "if": "$souls >= 10", "once": true, "do": [{ "do": "endLevel", "result": "win", "message": "The ritual is complete" }] }]
```

**23. A war horn** (a king action): all troops within 20 m follow the king, 20 % faster, for the night.

```jsonc
"kingActions": [{ "id": "horn", "name": "War horn", "when": "day", "uses": { "count": 1, "per": "day" } }],
"rules": [{ "when": { "on": "kingAction", "action": "horn" }, "do": [
  { "do": "command", "target": "all('player and unit', 20)", "order": "follow" },
  { "do": "setStat", "target": "all('player and unit', 20)", "stat": "moveSpeed", "mult": 1.2, "for": "night" } ] }]
```

### Blind check

The user asked for a check that the design isn't fitted to the three examples. It was run as follows:

1. A blind agent was given only a description of Thronefall and of a level editor, not this design. It invented 30
   mechanics.
2. A second agent, given only this document, tried to express each one in its data format and graded it. It was
   told to grade strictly and not to invent primitives.

The first pass scored **11 yes, 17 partly, 2 no**, and it listed 27 gaps in the specification.

- **New primitives.** The ones that unlocked the most mechanics are now part of the design:
  - `drift` (continuous velocity), and `dir`/`angle`/`sin`/`cos`;
  - `nightProgress`, current-night `waves` ops, and pausing spawn lines;
  - wave queries (`waveCount`, `waveUnits`, `strongestUnit`);
  - coins as entities;
  - building-kind and fighter filter terms, and `.income`/`.cost`;
  - derived vanilla damage types and modifier `when`;
  - list vars;
  - labels at runtime;
  - `setHp`;
  - interact choices;
  - box footprints and walkable surfaces.
- **Ambiguities.** Each one was resolved in the text.

On the revised document, the same agent re-graded the mechanics at **25 yes, 5 partly, 0 no**. It found ten more
ambiguities, which were also fixed in the text.

| # | Mechanic | Grade | How (or what limits it) |
|---|---|---|---|
| 1 | Tidal moat: floods every 2 nights, slows, chests when drained | Yes | var at dusk; `enable`/`disable` a `box` blocker; areas apply slow statuses; chests are pickups |
| 2 | Sundial gate: open between 20 % and 60 % of the night | Yes | `every 0.25` → `nightProgress` window → door poses + `invulnerable` status |
| 3 | Gravity well: pull, damage at the centre, overheats | Yes | `drift toward self` on `all('foe and unit', 8)`; heat var → `disableRule`/`wait`/`enableRule` |
| 4 | Debt collector: steals gold, flees, refund on kill | Yes | `near` building → `gold` −, `moveTo @exit`; `death` → `gold` ×1.5 |
| 5 | Lantern fog: towers only target lit enemies | Yes | `setTargeting` on towers with `where(it.count('has:lit', 6) > 0)`; a dim status elsewhere |
| 6 | Rotating bridge that carries units | Yes (snaps) | walkable mover snapping between poses carries its riders; no smooth turn |
| 7 | Tribute altar removes the next night's strongest enemy | Yes | `interact cost "=100+50*$uses"` → `waves removeUnit "=strongestUnit(night)"` |
| 8 | Echo barracks replays the king's last 10 s | Yes (approx.) | list vars sample `king.pos` at 10 Hz; a ghost replays them with `moveTo` |
| 9 | Magnetized armour | Yes | `drift` + a held status (`disarmed`, `rooted`) + an entity var per tower |
| 10 | Plague cart | Yes | trail areas with lifetime; `noIncome`; `damageTaken {mult 0, types [ranged, tower]}` |
| 11 | Collapse tiles | Yes | an area preset using `in:self`, a blocker and a stun status; reset at `dawn` |
| 12 | Swap totem | Yes | position vars + two `teleport`s |
| 13 | Time-dilation field | Partly | `timeScale` on units and Builder projectiles; vanilla arrows in flight aren't slowed |
| 14 | Siege ladder: only melee climb the wall | Partly | no per-unit walkability; a teleport or a ramp for everyone |
| 15 | Dawn bell income streak | Yes | `dawn` + `income` + a streak var |
| 16 | Mimic chests | Yes | `interact` + `choose`; mimics with explode-on-`near` |
| 17 | Seasons: ice, income, spreading fire | Yes | walkable ice enabled in winter; `income` on `type:Mill`; fire spreads by status to `#flammable` |
| 18 | War horn | Yes | example 23 |
| 19 | Sacrificial pit | Partly → Yes | the second pass lacked a demolish op; `building op demolish` was added after it |
| 20 | Hostage convoy | Yes | path follower; `end` → reward; `death` → `waves scaleCount 1.3` |
| 21 | Chain lightning pylons | Yes | polygon area; shared pool with `setHp`; enemies `prepend` targeting |
| 22 | Decoy banner | Yes | `taunt` all enemies toward it for 8 s |
| 23 | Reverse spawn at 80 % of the night | Yes | `nightProgress` → `waves clearRemaining` (current) + `spawnGroup at @inner` |
| 24 | Cursed coins | Yes | `coinDrop` counter → `addLabel cursed`; `coinPickup` of `#cursed` → gold + `taunt` |
| 25 | Wind gusts | Partly | flyers and Builder projectiles `drift`; vanilla arrows don't |
| 26 | Shapeshifting shrine | Yes | cycling var; area `inside` applies the state's status |
| 27 | Rain | Yes | slow status; modifier `when: dist > 15` with `chance`; `income` on mills |
| 28 | Reclaimed ruins | Yes | interact `choices` with costs from `.cost`; locked slots unlocked by `choose`; vanilla AI ignores a neutral "haunted" ruin |
| 29 | Dream stage | Partly | a walled-off arena on the same map; no second scene |
| 30 | Loan banker | Yes | `choices` 100/250/500; dusk check → collector targeting `where(it.income > 0)` |

The remaining "partly" items all hit the same three engine limits: vanilla projectiles, per-unit walkability and
scenes. None of the mechanics needed a feature coded for it.

## Known limits

- **Movement physics is limited.**
  - Units ride a walkable mover only when it snaps between poses, not while it glides.
  - There is no per-unit walkability (one navmesh per size class, as in the game), so "only melee can climb this wall"
    is a teleport, not a path.
  - Terrain height doesn't change at runtime; walkable props and blockers change where units can go.
- **Vanilla projectiles in flight** (the game's `AimbotProjectile`) aren't affected by `timeScale`, `drift` or
  redirection. Builder projectiles are.
- **Team model.** There are three teams. There's no "hostile to all": Builder targeting can make a thing attack
  both sides, but vanilla AI sees only `PlayerOwned`/`EnemyOwned`, so vanilla units never attack a neutral. A converted unit uses the other side's nearest equivalent brain.
- **Game tags are a closed set** (51 `ETag`s, kept in a 64-bit field). Vanilla AI only targets by them, so labels
  work only through Builder targeting, which can be set on any attacker (Builder units, vanilla units and towers).
- **No new scenes, no fog or lighting changes** beyond the level's colour scheme and tints. Recording is only what
  list vars can hold: up to 256 sampled values, such as positions.
- **King actions:** 3 keys. The day phase has no timer in Thronefall, so "shorter day" has no meaning.
- **No arbitrary code**, by design. A missing verb needs a mod update; the registry's `since` makes that explicit.
- **Savestates** don't keep pending `wait`s or Builder projectiles in flight.

## Workstreams

Four workstreams, built against interfaces that land first. Each owns its files outright. Shared files are touched
only additively and noted in the PR.

### Interfaces (first commits)

- **L4 commit 1, `Model/Logic/`** (pure C#, no Unity):
  - `LogicDefs.cs`: `LogicDef`, `RuleDef`, `WhenDef`, `ActionDef` (the type plus a `JObject` of params), `EntityDef`,
    the component defs, `StatusDef`, `PresetDef`, `KingActionDef`, `HudDef`;
  - `Expr.cs`: the AST, parser and type checker;
  - `LogicSchema.cs`: the registry API, `LogicSchema.Events/Actions/Stats/Functions/Filters`, each with `ParamSpec`.
- **L1 commit 1, `Logic/Runtime/LogicApi.cs`** (as built; the file's comments are the reference). The core is pure
  C# with no UnityEngine types, so `tests/` can drive it with a fake world: positions are `Vec`, and an entity is a
  `LogicEntity` class (one per object per session; `null` = none) instead of a struct with Unity fields.

```csharp
public sealed class LogicEntity { string Id, Name, Template, Kind, Team; HashSet<string> Labels; Dictionary<string, Value> Vars;
                                  bool Enabled, Spawned, Destroyed; LogicEntity Owner; IEntityBody Body; object Game; JObject Def; object Ext; ... }
public interface IEntityBody { bool Exists, Alive; Vec Position; float Facing, Hp, MaxHp; bool HasTag(string etag); ... }
public static class LogicActions { public static void Register(string name, ActionHandler h, ActionFlags flags = None); }  // PerTarget: core fans out `target`
public delegate void ActionHandler(LogicContext ctx, ActionParams p);   // p.Number("amount"), p.Entities("target"), p.Actions("then"), p.Target
public sealed class LogicContext { LogicWorld World; LogicEntity Self, Subject, Other, Spawned, It; LogicEvent Event; int Depth;
                                   LogicContext With(spawned, it, other); void Run(ActionList); void Raise(...); }
public static class LogicEvents { static void Raise(string name, LogicEntity subject, LogicEntity other = null, double value = 0, Vec? pos = null, string text = null);
                                  static void Signal(string name, IReadOnlyList<LogicEntity> to, double value = 0, LogicEntity sender = null);
                                  static event Action<TraceEntry> Traced; }
public interface IEntityFactory { LogicEntity Spawn(string template, Vec pos, float rotY, string team, LogicEntity owner, out string error); void Despawn(...); ... }
public interface IStatModifiers { void Add(LogicEntity e, StatModifier m); double Get(LogicEntity e, string stat); int Stacks(...); ApplyStatus/RemoveStatus; Capture/Restore }
public interface ITargeting { void Set(LogicEntity e, JToken targeting, double seconds); }
public interface IAreas { bool? Contains(LogicEntity area, LogicEntity e); }     // null: use the area's `shape`
public interface ISpatial { void Query(Vec center, float radius, List<LogicEntity> into); }
public interface IGameState { int Night; bool IsNight; double NightProgress; int Gold; LogicEntity King, Castle; ... }
public static class LogicServices { IEntityFactory Factory; IStatModifiers Stats; ITargeting Targeting; IAreas Areas; ISpatial Spatial; IGameState Game; }
public static class LogicSaveState { static void Register(string id, Func<LogicWorld, JToken> capture, Action<LogicWorld, JToken> restore); }
// Unity side (LogicGame.cs): e.Go(), e.Hp(), e.Tagged(), LogicGame.EntityOf(GameObject), LogicGame.Register(entity, go), Vec <-> Vector3
// Studio (L3): LogicWorld.Current, TraceEntry / LogicEvents.Traced, BudgetSnapshot, LogicCaps (+ level override `logic.caps`)
```

### L1: Runtime core and events

- **Owns:** `Features/Builder/Logic/Runtime/` (LogicManager with its nested Harmony patches for every hook in the
  table, LogicWorld, BuilderEntity, EventBus, Scheduler, Compiler, Hooks, budgets, LogicRandom, the trace), the
  savestate and retry-day registration, the `logic` dev command (`status | vars | trace | fire <signal> [to] | set
  <var> <v> | budget | entities`), the PerformanceManager `Logic` line, and `tests/` for the scheduler, the dispatch
  order and the budgets (fake world).
- **Provides** the control-flow actions itself (`wait`, `if`, `repeat`, `forEach`, `choose`, `stop`, `signal`, `setVar`,
  `addVar`, `enableRule`, `disableRule`, `enable`, `disable`, `toggle`).
- **Done when:**
  - examples 3, 4, 6 (with a stub area), 11, 15, 22 and 23 run in a pool instance from JSON;
  - the budget caps are proven with a deliberate signal loop and a 1000-spawn bomb;
  - a savestate round-trip keeps vars, statuses and spawned entities.

### L2: World verbs: actions, bodies, projectiles, movers, targeting

- **Owns:** `Features/Builder/Logic/Actions/` (every non-control action), `Logic/Bodies/` (EntityFactory,
  BuilderProjectile, BuilderMover with its NavmeshCut, BuilderArea and the spatial poller feeding L1's bus,
  BuilderPickup, BuilderInteractor, BuilderAttack, Stats and statuses with the synchronous `TakeDamage` / `Find*` /
  `Update` prefixes, Steering with the targeting and movement overrides on `FindAutoAttackTarget` / `FindMoveToTarget`,
  convert and replace), the effects and sounds catalog in `Runtime/Catalog.cs` (additive), and the `UnitFactory`
  hooks that put `BuilderEntity` and the compiled components on unit templates (additive).
- **Starts with two spikes:**
  - a moving solid mover with a NavmeshCut on the host's graphs. It measures the update cost and confirms the
    stuck-unit push-out. If cuts can't move, solid movers fall back to snap-moving between poses with one graph
    update per move;
  - the cost of `GroundGraphs.Scan()` on an edited map, which sets the walkable-surface rate limit.
- **Reads** `docs/enemy-behaviour-research.md` (being written by the Enemy Studio round) for how each vanilla
  attack, projectile and movement component behaves, before patching them.
- **Done when:** examples 1, 2, 7, 8, 9, 10, 12, 16, 17, 19, 20 and 21 run, and the user's three are recorded as
  screenshots.

### L3: Studio logic editor

- **Owns:** `Features/Builder/Studio/Logic/`:
  - the Behaviour inspector section (cards, chip pickers, typed and expression fields, the filter builder);
  - the Wire tool and wire overlay;
  - the Logic palette tab and the area/path drawing in the Place tool;
  - the Level tab sections (Variables, Level script, King actions);
  - the sandbox Logic panel and the rule-fire pips;
  - "Save as preset" and the preset picker.

  It also owns the Enemy Studio Behaviour, Targeting, Movement and Attacks tabs, and the Entity Studio kind picker
  (additive edits to `Studio/Enemies/EnemyDock.cs`/`EnemyStudio.cs`).
- **Builds on** `Studio.AddInspectorSection`, `AddTool`, `AddOverlay`, `AddPanel` and `PickOnMap`. It needs no shell
  edits.
- **Done when:** examples 6, 7, 11 and 21 are built by mouse in a pool instance from an empty level, played with F5,
  and saved to the same JSON as the doc. Esc closes one layer, and no click reaches the game.

### L4: Data, validation, migration, presets, agent kit

- **Owns:**
  - `Model/Logic/` (defs, expressions, the schema registry);
  - `Model/Validation.cs` logic checks (additive);
  - the `Model/UnitDef.cs` and `LevelDef.cs` fields;
  - the `PackSerializer` migration 2 → 3;
  - `Assets/builder-presets.json` (the built-in logic objects and common presets);
  - the presets kind in `Model/LibraryStore.cs` (additive, coordinated with R2-C1);
  - `builder dump` → `logic-schema.json`;
  - `agent-kit/palace.py` (ops, the `logic` commands, the Python expression port);
  - `agent-kit/level-building.md` (the Behaviour chapter and cookbook) and the `reference.md` regeneration;
  - `tests/` (parser, type checker, validator messages, migration, preset expansion, `logic-expr-cases.json`, and
    every example in this document as `tests/logic-examples/*.json`, validated in CI).
- **Done when:** every example here validates, the migrated sample pack plays the same, and `palace.py apply` builds
  examples 1, 13 and 22 from ops.

### File overlap

| File | Owner | Others (additive only) |
|---|---|---|
| `Model/UnitDef.cs`, `LevelDef.cs`, `DesignPack.cs`, `PackSerializer.cs`, `Validation.cs` | L4 | — |
| `Runtime/UnitFactory.cs` | (existing) | L2: attach BuilderEntity and components; L4: remove `ApplyBehaviour` after migration |
| `Runtime/Catalog.cs` | (existing) | L2: effects and sounds; L4: the schema dump |
| `Studio/Enemies/EnemyDock.cs`, `EnemyStudio.cs` | R2-C2 / existing | L3: tabs and the kind picker |
| `Studio/Play/SandboxPanel.cs` | (existing) | none: L3 uses `Studio.AddPanel` |
| `Model/LibraryStore.cs` | R2-C1 | L4: the presets kind |
| `Features/SaveStates/*` | (existing) | none: L1 uses `RegisterState` |

**Order:** L4's and L1's interface commits land first, on the same day. L2 and L3 then run in parallel; L3 can build
every card against the schema with no runtime. L1's done-check needs L2's areas for example 6, so it uses a stub until
then.

## Open questions (the user's)

None block the design. Each has a default and can be changed later:

1. **Caps.** 300 spawned entities, 400 projectiles and 400 rule firings per frame. Should creators be able to raise
   them per level, behind a "may lag" warning? The default is no, for v1.
2. **Shared enemies with level-wide effects.** An enemy from someone else's library can carry rules that give gold or
   end the level. The default is to allow it and show a badge in the Studio ("this enemy affects the level"), rather
   than restricting what enemy rules may do.
