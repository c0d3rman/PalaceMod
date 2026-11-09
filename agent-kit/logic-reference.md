# Behaviour engine reference

Generated from the engine's registry (`palace.py logic doc`; schema version 1.0.0). The guide's "Behaviour" chapter explains how the pieces fit; this lists every piece. `palace.py logic schema <kind> <name>` prints one entry as JSON.

## Param types

| Type | Accepts |
|---|---|
| `number` | a number, or an expression string ("night * 2") |
| `int` | a whole number, or an expression string |
| `bool` | true/false, or an expression string |
| `text` | text with {expr} parts ("Souls {$souls}/10"); "=expr" makes the whole field an expression |
| `position` | an expression giving a position or an entity (self, other, @name, vec(x, z), pointNear(self, 3)), or [x, y, z] |
| `entity` | a selector expression giving one entity (self, other, @name, nearest('foe', 10)) |
| `entities` | a selector expression giving one entity or a set (all('foe', 6)); the action runs on each member with `it` bound |
| `template` | a template id: a pack unit, a pack entity, or "vanilla:<id>"; "=expr" for an expression ("=self.template") |
| `filter` | a filter string ("foe and Building and not Wall") |
| `status` | a status id from the pack's statuses |
| `rule` | a rule's id |
| `var` | a variable name (declared in vars; "$name" or target "level" for a level variable) |
| `signal` | a signal name (a built-in input or your own) |
| `label` | a label name (without #) |
| `enum` | one of the listed options |
| `actions` | a nested list of actions |
| `options` | [{ "weight": 2, "do": [actions] }, ...] |
| `targeting` | a targeting list: filter strings or { "filter", "range", "prefer", "score" } (a score picks the highest-scoring match) |
| `movement` | a movement block (mode, path, speed, keepDistance, kite, flee, wander, home) |
| `any` | an expression of any type |
| `for` | seconds, or "night" / "day" (until the next dawn / dusk) |
| `night` | a night number expression, or "current" |
| `slot` | a build slot: any expression giving a building or slot entity (@name, it, $var, a selector), or a Studio key ("slot:Tower 3", "add:<id>", "castle") |
| `types` | a list of damage-type labels (["fire"]) |
| `keep` | a list of the listed options |
| `fx` | a game effect name |
| `sound` | "asset:<key>" or a game sound |
| `color` | "#rrggbb" (or "#rgb", "#rrggbbaa", a colour name like "red"); "=expr" for an expression |
| `json` | a JSON block |
| `local` | a plain name (letters, digits, _) for a value bound inside the action's actions |

## Roles

Names an expression can use for entities in a rule.

| Role | Type | Meaning |
|---|---|---|
| `self` | entity | The rule's owner (the level in the level script). |
| `subject` | entity | Who the event happened to (self for its own events; the other entity for `of` listeners). |
| `other` | entity | The event's counterpart: killer, victim, attacker, entrant, collector, sender... |
| `spawned` | entity | Inside spawn/launch/replace `then`: the new entity. |
| `it` | entity | The current candidate: inside a filter's where(), best()'s score, forEach, or an action applied to a set. |
| `outer` | entity | The `it` one scope out (the forEach member while a nested target, where() or best() rebinds `it`); none at the top. |
| `king` | entity | The player's king. |
| `castle` | entity | The castle (its build slot's building). |
| `level` | level | The level itself (owner of level variables). |
| `event` | event | The event's fields: event.value, event.pos, event.name, event.choice, event.result. |

## Globals

| Name | Type | Meaning |
|---|---|---|
| `night` | number | The current night (1-based), or the coming one by day. |
| `isNight` | bool | True during a night. |
| `isDay` | bool | True by day. |
| `nights` | number | The level's total number of nights. |
| `time` | number | Seconds since level start. |
| `nightTime` | number | Seconds since this night began. |
| `nightProgress` | number | 0..1: the night's spawn schedule elapsed (1 once everything has spawned). |
| `gold` | number | The player's gold. |
| `income` | number | The gold the buildings paid at the last dawn (`income` stats applied), already counted when `dawn` rules run; the coins themselves land over the next seconds. |
| `enemiesAlive` | number | Enemies on the map. |
| `difficulty` | number | The run's difficulty multiplier. |
| `daylight` | number | The level's own light, 0..1: 1 by day, about 0.34 at night, lower in an eclipse (`lighting`). |
| `waterLevel` | number | The terrain's water level (world y); 0 with no water. The `terrain` action's waterLevel op changes it. |
| `none` | any | No entity (what a selector gives when nothing matches, or a role the event doesn't have): `other != none`. |

## Events

`when` takes the name, or `{"on": name, ...params}`; add `"of": "<filter>"` (and `"range"`) to hear the event on other entities (then `subject` is who it happened to). `other` and `event.value` are as listed; aliases name `other`/`subject` per event.

### This unit

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `spawn` |  | spawner (spawner = other) |  | It appears: placed ones at level start, spawned ones on creation. `other` is the spawner or launcher, if any. |

### Combat

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `death` |  | killer (killer = other, victim = subject) |  | It dies (a unit) or is knocked out (a building or the king). `other` is the killer (may be none). event.types / event.weapon: the killing hit's (see damaged). Fields: event.types, event.weapon. |
| `damaged` | `min` | attacker (attacker = other) | number: the damage taken | It takes damage (not when modifiers reduce it to 0). `other` is who dealt it: the king, a unit, a tower or building, a projectile's shooter, or whoever left burning ground or hot oil there; none for scenery hazards and plain `damage` actions without a source. event.types is the hit's damage types (a list: melee, ranged, tower, king, splash, magic, fire, the damage action's own types, and the weapon's id); event.weapon is the weapon that dealt it ("vanilla:<weapon>"), or none. Fields: event.types, event.weapon. |
| `healed` |  | healer (healer = other) | number: the amount healed | It is healed. |
| `hpBelow` | `pct`* |  | number: hpPct | Its hp crosses below a fraction (edge-triggered; re-arms when healed above). Only its own hp: no `of`. |
| `attack` | `attack` | target (target = other) |  | It attacks (any attack, vanilla ones included). |
| `hit` |  | victim (victim = other) | number: the damage dealt | Its attack, projectile or damage action damaged something. event.types / event.weapon as for damaged. Fields: event.types, event.weapon. |
| `kill` |  | victim (victim = other) |  | Something it damaged died from that damage. event.types / event.weapon as for damaged. Fields: event.types, event.weapon. |
| `targetChanged` |  | target (target = other) |  | Its attack or move target changes. |

### Time

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `every` | `seconds`*, `jitter`, `phase`, `during` |  | number: the tick count (1, 2, ...) | Periodically, from spawn (or level start). Level script: no `of` needed. |
| `after` | `seconds`* |  |  | Once, that long after spawn (or level start). Level script: no `of` needed. |
| `expire` |  |  |  | Its lifetime (or projectile range) ran out; it's destroyed after its rules run. |

### Areas

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `near` | `filter`*, `range`*, `edge` | entity |  | A matching entity comes within (or leaves) a range of it. |
| `enter` (area) |  | entrant (entrant = other) |  | (areas) An entity matching the area's watch filter enters. |
| `exit` (area) |  | entity |  | (areas) An entity leaves. |
| `inside` (area) |  | entity |  | (areas) For each entity inside, after every full `tick` it has spent inside. |
| `empty` (area) |  |  |  | (areas) The last watched entity left. |
| `occupied` (area) |  | entity |  | (areas) The first watched entity entered. |

### Projectiles

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `impact` (projectile) |  | hit entity |  | (projectiles) It hit something or the ground. `other` is what it hit (none for ground). |

### Movement

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `arrive` |  |  | number: the point index | (movers, path followers, moveTo) It reached a point. |
| `bump` (unit) |  | obstacle (obstacle = other) | number: the push distance left (metres) | A push (the push action or the game's knockback) was stopped short by something solid: a wall or building, a solid body, a cliff or the map's edge. `other` is what it hit when that's an entity (a building, a wall, a solid body), else none; event.pos is where it stopped. |
| `end` |  |  |  | (movers, path followers) It reached the end of its path (before then: destroy acts). |

### Pickups

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `pickup` (pickup) |  | collector (collector = other) |  | (pickups) Collected. `other` is the collector. |

### King

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `interact` (unit/building/prop/projectile/area/pickup/logic/king) |  | king |  | (interactables) The king used it, after paying its cost. event.choice is the chosen id with choices. Fields: event.choice. |
| `interactRefused` (unit/building/prop/projectile/area/pickup/logic/king) |  | king | number: the gold cost | (interactables) The king tried to use it but was short of gold or a `pay` item. event.name lists what was short. Fields: event.name. |
| `kingAction` | `action`* | king |  | The player presses one of the level's action keys (logic.kingActions). Level script: no `of` needed. |

### Signals

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `signal` | `name` | sender (sender = other) | number: the signal's number | It receives a signal. event.name is the signal's name, event.value its number. Fields: event.name. Level script: no `of` needed. |

### Variables

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `varChanged` | `name` |  | any: the new value | One of its variables changes (a level var, on the level script). Fields: event.name. Level script: no `of` needed. |

### Statuses

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `statusStart` | `status` | applier (applier = other) | number: stacks | A status starts on it. Fields: event.name. |
| `statusEnd` | `status` | applier (applier = other) | number: stacks | A status ends (expiry or removal). Fields: event.name. |
| `statusTick` | `status` | applier (applier = other) | number: stacks | Every `tick` seconds of a status (in a status's own rules: of that status). Fields: event.name. |

### Links

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `linked` |  | parent |  | It was linked to a parent (`link`, a template's parts). `other` is the parent. |
| `unlinked` |  | parent |  | It was unlinked from its parent (`unlink`, or the parent died with onParentDeath drop/stay). `other` is the old parent. |

### Buildings

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `built` (building) | `level` |  | number: the level | (build slots, map or added) It's built: by the king, `palace.py build` or the `building` action. |
| `upgraded` (building) | `level` |  | number: the new level | (build slots, map or added) It's upgraded (any way it's built). |
| `repaired` (building) | `level` |  | number: the level | (build slots) It's revived at dawn. |

### Gold

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `coinDrop` (coin) |  | dead unit |  | A coin drops from a death (subject is the coin; listen with `of`). |
| `coinPickup` (coin) |  | king |  | The king collects a coin (subject is the coin; listen with `of`). |
| `goldChanged` |  |  | number: the change (negative = spent) | The player's balance changes. Level script: no `of` needed. |

### Night

| Event | Params | other | event.value | Fires when |
|---|---|---|---|---|
| `lineSpawn` (line) | `line` | new enemy |  | (spawn lines) It spawns an enemy (a wave or spawnGroup). `other` is the new enemy. |
| `levelStart` |  |  |  | The level has loaded (after the overlay, before day 1). Level script: no `of` needed. |
| `dusk` | `night` |  | number: the night number | Night is falling (the game's OnDusk). Level script: no `of` needed. |
| `nightStart` | `night` |  | number: the night number | The night's first enemies spawn. Level script: no `of` needed. |
| `dawn` | `night` |  | number: the night just survived | The sun rises (after income is paid). Level script: no `of` needed. |
| `day` | `night` |  | number: the coming night | The day phase begins (after dawn's sunrise). Level script: no `of` needed. |
| `waveCleared` |  |  | number: the night | Spawning is done and no enemies are left. Level script: no `of` needed. |
| `levelEnd` | `result` |  |  | Win or loss, before the end screen. event.result is 'win' or 'lose'. Fields: event.result. Level script: no `of` needed. |

`*` = required.

## Actions

Each action is `{"do": "<name>", ...params}`. `target` (where listed) is a selector, default `self`; on a set the action runs once per member with `it` bound to it.

### Make

**`spawn`**: Creates entities from a template (a unit or an entity), e.g. split a slime, summon skeletons.

- `entity` (template; required): The template (a pack unit or entity, or "vanilla:<id>"); "=self.template" for a copy of itself.
- `count` (int; default 1; 1..100): How many.
- `at` (position; default "self"): Where (an entity or a position).
- `spread` (number; default 0; 0..): Scatter radius in metres.
- `team` (enum; one of player, enemy, neutral, owner): Its team (default: the template's; owner = the spawner's).
- `elite` (enum; default "false"; one of inherit, true, false): Whether spawned enemies are elite; inherit copies the spawner's.
- `facing` (number): Yaw in degrees (default: the spawner's facing).
- `then` (actions; binds `spawned`): Run for each new entity, with `spawned` bound.

  `{ "do": "spawn", "entity": "=self.template", "count": 2, "at": "self", "spread": 1.5 }`

**`launch`**: Fires an entity as a projectile from the owner toward a target (it uses the template's projectile block).

- `entity` (template; required): A template with a projectile block.
- `from` (position; default "self"): Start: an entity or position, or an [x, y, z] offset from self.
- `toward` (position; required): The target: an entity (homing follows it) or a position.
- `speed` (number): Overrides projectile.speed.
- `homing` (number; 0..1): Overrides projectile.homing.
- `arc` (number): Overrides projectile.arc.
- `gravity` (number): Overrides projectile.gravity.
- `hits` (filter): Overrides projectile.hits.
- `pierce` (int): Overrides projectile.pierce (-1 = all).
- `range` (number): Overrides projectile.range.
- `onImpact` (actions; binds `spawned`): Extra actions on impact (the template's impact rules also run), with `spawned` = the projectile and `other` = what it hit.
- `then` (actions; binds `spawned`): Run once it's launched, with `spawned` bound.

**`spawnGroup`**: A wave-style group of enemies now; they count toward the night.

- `unit` (template; required): The enemy.
- `count` (int; default 1; 1..200): How many.
- `interval` (number; default 0.5; 0..): Seconds between spawns.
- `line` (text): A spawn line's name.
- `at` (position): A position instead of a line.
- `elite` (bool; default false): Elite enemies.
- `gold` (int): Coins each drops (default: the unit's).

**`replace`**: Swaps the entity for another template in place (morph, shapeshift).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `with` (template; required): The new template.
- `keep` (keep; default ["hp", "vars", "statuses", "labels"]; one of hp, vars, statuses, labels): What carries over.
- `then` (actions; binds `spawned`): Run with `spawned` = the replacement.

### Health

**`damage`**: Deals damage.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `amount` (number; required; 0..): Hit points.
- `source` (entity; default "self"): Who is credited (fires their hit/kill events). Default self.
- `types` (types): Damage-type labels (fire, ...) that status modifiers test.
- `splash` (bool; default false): Counts as splash damage (the game's splash modifiers).
- `weapon` (text): Apply a weapon's tag modifiers ("vanilla:<weapon>").

  `{ "do": "damage", "target": "all('foe', 3)", "amount": 25 }`

**`heal`**: Heals.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `amount` (number; required; 0..): Hit points.

**`kill`**: Kills it (fires death).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `source` (entity): Who is credited.

**`revive`**: Revives a knocked-out building or a dead hittable entity.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `hpPct` (number; default 1; 0..1): Hit points as a fraction of max.

**`setHp`**: Sets hit points without damaged/healed events (shared health pools).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `value` (number; required; 0..): Hit points.

### Stats

**`setStat`**: Changes a stat, permanently or for a while. Give exactly one of value, add, mult.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `stat` (enum; required; one of maxHp, hp, moveSpeed, attackCooldown, attackRange, damageMult, damageTaken, projectileSpeed, scale, keepDistance, aggroTime, income, timeScale): The stat (see Stats).
- `value` (number): Set to this.
- `add` (number): Add this.
- `mult` (number): Multiply by this.
- `for` (for): Seconds, or "night"/"day" (until the next dawn/dusk); omit for permanent.
- `key` (text): A timed change with the same key replaces the earlier one.

  `{ "do": "setStat", "target": "all('player and Tower')", "stat": "attackCooldown", "mult": 0.5, "for": 5 }`

**`applyStatus`**: Applies a status (a timed, stacking modifier set).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `status` (status; required): The status id.
- `for` (number): Seconds (default: the status's duration).
- `stacks` (int; default 1; 1..): Stacks to add.
- `source` (entity; default "self"): Who applied it (`other` in its rules). Default self.

**`removeStatus`**: Removes a status (all stacks, or some).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `status` (status; required): The status id.
- `stacks` (int; 1..): Stacks to remove (default all).

**`setWeapon`**: Gives it another weapon (units, towers, the king): a weapon id, a copy of another entity's weapon, none, or its own back. A ranged weapon makes it shoot: its range follows the weapon.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `weapon` (text; required): "vanilla:<weapon>" (e.g. "=other.weapon"), an entity to copy ("=other": its weapon, range and keep distance), "none" (its attacks do nothing) or "base" (the weapon it started with).
- `range` (number; 0..): Attack range in metres (default: the copied entity's, else the weapon's usual range).
- `for` (for): Seconds, or "night"/"day"; then it gets back the weapon it had before. Omit to keep it.

  `{ "do": "setWeapon", "weapon": "=other" }`

### Team

**`convert`**: Switches team (a dead target is raised as a new entity of that team).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `team` (text; required): "player" \| "enemy" \| "neutral", or an expression such as self.team.
- `keepLook` (bool; default true): Keep its look.
- `hpPct` (number; 0..1): Hit points as a fraction of max.

  `{ "do": "convert", "target": "other", "team": "=self.team", "hpPct": 0.5 }`

### Move

**`teleport`**: Moves it instantly.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `at` (position; required): Where.

**`push`**: A one-off shove (the game's knockback), on units (flyers included).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `from` (position): Push away from here (default self).
- `toward` (position): Pull toward here instead.
- `force` (number; required): Metres of travel.
- `up` (number; default 0): Arc height.
- `stun` (number; default 0): Seconds stunned.

**`drift`**: A continuous velocity added to movement: a pull, a current, wind. Different keys add up; the same key replaces.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `dir` (position): A direction vector (dir(deg) gives one).
- `toward` (position): Drift toward this.
- `from` (position): Drift away from this.
- `speed` (number; required): m/s.
- `for` (number; 0..): Seconds (omit for until removed).
- `key` (text; default "drift"): Replaces an earlier drift with the same key.

**`moveTo`**: Walks it to a point; fires arrive there.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `at` (position; required): Where.
- `speed` (number): m/s (default: its own).
- `then` (enum; default "resume"; one of resume, hold): After arriving.

**`setMovement`**: Changes its movement block (see Movement).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `movement` (movement; required): The new movement fields (merged).
- `for` (number): Seconds; reverts after.

**`setTargeting`**: Changes who it attacks and walks toward.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `targeting` (targeting): Replace the whole list.
- `prepend` (targeting): Put these first.
- `remove` (targeting): Remove these entries.
- `for` (number): Seconds; reverts after.

  `{ "do": "setTargeting", "target": "subject", "prepend": ["is:caravan"] }`

**`taunt`**: Forces aggro onto an entity (the game's agro mechanism).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `toward` (entity; required): Who they go for.
- `for` (number; default 5): Seconds.

**`command`**: Orders player units as the king's own commands.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `order` (enum; required; one of follow, hold, home): The order.

**`turn`**: Turns it to face a direction: aim a beam, a turret, a blade wall, a placed area (its shape, outline, surface and solid footprint turn with it). Units turn their model (they turn back when they walk).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `facing` (number): Yaw in degrees to face (0 = north, 90 = east).
- `by` (number): Degrees to turn by (clockwise; negative = anticlockwise).
- `toward` (position): Face this entity or position.
- `speed` (number; 0..): Degrees per second (empty: at once).

  `{ "do": "turn", "target": "@beam", "toward": "king", "speed": 90 }`

**`setShape`**: Changes an area's shape or size (its outline, fill, membership and surface follow), or a body's footprint (its solid cut and collider follow).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `shape` (enum; one of circle, rect, polygon, sector): The new kind (default: keep the kind it has).
- `radius` (number; 0.1..): circle, sector: the radius (m).
- `angle` (number; 1..360): sector: how wide (degrees), centred on its facing.
- `width` (number; 0.1..): rect: width (m).
- `depth` (number; 0.1..): rect: depth (m).
- `points` (json): polygon: [[x, z], ...] relative to it.
- `part` (enum; one of area, body, both): What to reshape (default: its area, else its body).
- `for` (for): Seconds (or "night"/"day"); then the old shape comes back.

  `{ "do": "setShape", "target": "@beam", "angle": "$beamWidth" }`

### Links

**`link`**: Links it to a parent as a part: it moves (and turns) with the parent every frame, may share its damage, and follows onParentDeath when the parent dies. Re-linking moves it to the new parent.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `to` (entity; required): The parent.
- `offset` (position): Where it sits in the parent's frame: [x, y, z], x right, y up, z ahead (default: where it is now).
- `rotY` (number): Its yaw relative to the parent's (default: as it is now).
- `follow` (enum; default "rigid"; one of rigid, position, none): rigid: moves and turns with the parent every frame; position: moves, keeps its own yaw; none: a relation only (it moves on its own, e.g. movement follow).
- `onParentDeath` (enum; default "die"; one of die, drop, stay): When the parent dies: die with it, drop (unlinked, set on the ground) or stay (unlinked, left where it is).
- `damageToParent` (number; default 0; 0..): This much of every hit it takes is dealt to the parent too (2 = double: a weak point).
- `damageToGroup` (number; default 0; 0..1): This fraction of every hit is shared evenly by its whole linked group, itself included (1 = one shared pool).
- `damageKept` (number; default 1; 0..): The fraction it takes itself, before the group share (0 = a pure proxy hitbox).

  `{ "do": "link", "target": "spawned", "to": "self", "offset": [1.5, 4, 0], "damageToParent": 2 }`

**`unlink`**: Unlinks it from its parent (or, with children, unlinks its parts).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `release` (enum; default "drop"; one of drop, stay): drop: set it on the ground; stay: leave it where it is.
- `children` (bool; default false): Unlink its parts instead of itself.

### Logic

**`signal`**: Sends a named signal (optionally with a number) to entities or everyone.

- `name` (signal; required): The signal's name (a built-in input such as open, enable, destroy, or your own).
- `to` (entities; default "self"): Who receives it: a selector, or "all" (every listener and the level script).
- `value` (number; default 0): A number it carries (event.value).
- `delay` (number; default 0; 0..): Seconds before it arrives.

  `{ "do": "signal", "name": "open", "to": "@gate1" }`

**`setVar`**: Sets a variable (an entity var on target, or a level var: target level, or a name with $).

- `name` (var; required): The variable (declared in vars).
- `value` (any; required): The new value (an expression).
- `target` (entities; default "self"): Whose variable (default self; "level" for a level var).

**`addVar`**: Adds to a number variable.

- `name` (var; required): The variable.
- `value` (number; default 1): How much.
- `target` (entities; default "self"): Whose variable (default self; "level" for a level var).

**`listAdd`**: Appends to a list variable (max 256; oldest dropped).

- `name` (var; required): The list variable.
- `value` (any; required): The item.
- `target` (entities; default "self"): Whose variable.

**`listRemove`**: Removes the first equal item, or the item at an index.

- `name` (var; required): The list variable.
- `value` (any): The item to remove.
- `index` (int): Or the index to remove.
- `target` (entities; default "self"): Whose variable.

**`listClear`**: Empties a list variable.

- `name` (var; required): The list variable.
- `target` (entities; default "self"): Whose variable.

**`addLabel`**: Adds a label (filters test #label).

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `label` (label; required): The label.

**`removeLabel`**: Removes a label.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `label` (label; required): The label.

**`enableRule`**: Turns a rule (by id) on.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `rule` (rule; required): The rule's id.

**`disableRule`**: Turns a rule (by id) off.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `rule` (rule; required): The rule's id.

**`enable`**: Switches the entity on: rules, components (solid, hittable) and visibility. On a spawn line, resumes spawning.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.

**`disable`**: Switches the entity off. On a spawn line, pauses its spawning.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.

**`toggle`**: Switches it on if off, off if on.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.

**`wait`**: The rest of this action list resumes later.

- `seconds` (number; required; 0.05..): Seconds (min 0.05).

**`if`**: Runs then or else.

- `cond` (bool; required): The condition.
- `then` (actions): If true.
- `else` (actions): If false.

  `{ "do": "if", "cond": "self.$hits % 5 == 0", "then": [...] }`

**`repeat`**: Runs actions several times (event.value = the iteration).

- `times` (int; required; 1..100): How many times (max 100).
- `every` (number; default 0; 0..): Seconds between (0 = all now).
- `actions` (actions; required): The actions ("actions", since "do" names the action itself).

**`forEach`**: Runs actions for each member of a set, with `it` bound.

- `in` (entities; required): The set.
- `actions` (actions; required): The actions ("actions", since "do" names the action itself).
- `limit` (int; 1..256): At most this many (max 256).
- `as` (local): A name for the member, readable inside (and in nested actions that rebind `it`): "as": "tower" then `tower.hp`.

**`choose`**: Picks one weighted option at random.

- `options` (options; required): [{ "weight": 2, "do": [...] }, ...]

**`stop`**: Ends this action list.


### World

**`gold`**: Gives (or takes, negative) gold; floored at 0.

- `amount` (number; required): Gold.
- `at` (position): Show the coin effect here.

**`dropCoins`**: Drops real coins for the king to collect.

- `count` (int; required; 1..100): How many.
- `at` (position; default "self"): Where.

**`building`**: Acts on a build slot.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `slot` (slot): The slot: any expression giving a building or slot entity (@name, it, $var, nearest('unbuilt and type:House')...), or a Studio key ("slot:Tower 3", "add:<id>", "castle"). Default: the target.
- `op` (enum; required; one of build, upgrade, knockOut, repair, demolish, lock, unlock, setCost, setIncome): What to do.
- `value` (number): For setCost/setIncome: the amount; for demolish: gold refunded.
- `branch` (int): For build/upgrade: the upgrade choice (0-based).

**`waves`**: Edits the night's waves.

- `op` (enum; required; one of addSpawn, removeUnit, scaleCount, scaleHp, clearRemaining, startNight, endNight, skipNight): What to do.
- `night` (night): Which night (default: the next; "current" edits the running night).
- `unit` (template): addSpawn/removeUnit/scale*: the enemy.
- `count` (int; 1..): addSpawn: how many.
- `interval` (number): addSpawn: seconds between spawns.
- `delay` (number): addSpawn: seconds into the night.
- `line` (text): addSpawn/clearRemaining: the spawn line.
- `gold` (int): addSpawn: coins each.
- `elite` (bool): addSpawn: elite.
- `value` (number): scaleCount/scaleHp: the factor.
- `survivors` (enum; default "remove"; one of remove, stay): endNight: the enemies left vanish (remove) or stay on the map (stay).

  `{ "do": "waves", "op": "addSpawn", "unit": "brute", "count": "night - 2", "line": "North" }`

**`terrain`**: Changes the ground during play: heights, paint, blocked cells, water, ice and the water level. Only the touched area is re-meshed and re-pathed.

- `op` (enum; required; one of raise, lower, set, smooth, paint, block, unblock, water, dry, freeze, thaw, waterLevel): What to do.
- `at` (position; default "self"): The shape's centre.
- `radius` (number; 0.1..): A circle this wide (m).
- `width` (number; 0.1..): A rectangle: across (m).
- `length` (number; 0.1..): A rectangle: along (m).
- `angle` (number): The rectangle's turn (degrees, 0 = length runs north).
- `in` (entity): Use an area's or a body's own shape instead (e.g. "@fault").
- `softness` (number; 0..1): 0..1: how far in from the edge the change fades (heights; default 0.3).
- `amount` (number; default 1): raise/lower: metres.
- `height` (number): set: the ground's new height (world y).
- `material` (enum; one of grass, highGrass, lowGrass, earth, sand, road, rock): paint: the paint.
- `level` (number): waterLevel: the water's new surface (world y).
- `flood` (enum; one of connected, all, none): waterLevel: which low ground fills: joined to the water (default), every cell under the level, or none.
- `over` (number; 0..): Seconds to animate heights or the water level (the paths follow at the end).

  `{ "do": "terrain", "op": "lower", "in": "@fault", "amount": 6, "softness": 0, "over": 2 }`

**`endLevel`**: Ends the run.

- `result` (enum; required; one of win, lose): Win or lose.
- `message` (text): Shown on the end screen.

### Show

**`effect`**: Plays a game effect.

- `fx` (fx; required): The effect (`builder dump` lists them).
- `at` (position; default "self"): Where.
- `scale` (number; default 1): Size.

**`sound`**: Plays a sound.

- `sound` (sound; required): "asset:<key>" or a game sound.
- `at` (position): Where (omit for UI sound).

**`message`**: Shows a message at the top of the screen.

- `text` (text; required): The text ({expr} parts).
- `kind` (enum; default "info"; one of info, good, warn, bad): Its emblem.
- `seconds` (number; default 4): How long.

**`floatText`**: Floating text over a spot.

- `text` (text; required): The text.
- `at` (position; default "self"): Where.
- `color` (color): #rrggbb.

**`setVisual`**: Changes its look.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `tint` (color): #rrggbb.
- `scale` (number): Size factor.
- `mesh` (text): A model (mesh:/vanilla:/asset:).
- `visible` (bool): Shown or hidden.
- `glow` (color): Glow colour; true for the default glow, false for none, or a 0-1 strength.
- `for` (number): Seconds; reverts after.

**`hud`**: Changes a level HUD line (logic.hud).

- `id` (text; required): The HUD line's id.
- `text` (text): New text.
- `icon` (text): New icon.
- `visible` (bool): Shown or hidden.

**`shake`**: Shakes the camera.

- `strength` (number; default 1): How hard.
- `seconds` (number; default 0.5): How long.

**`marker`**: A screen-edge marker like the game's wave markers.

- `on` (entity; required): What it points at.
- `icon` (text): Icon.
- `for` (number): Seconds (omit: until it's gone).

**`lighting`**: Changes the level's light: darken it (an eclipse by day, a pitch-black night), tint it, or make day look like night. Reverts at the level's end, after `for`, or with `reset`.

- `brightness` (number; 0..2): Multiplies the sun's light: 1 = as the game has it, 0.2 = an eclipse, 0 = black. Units stay readable.
- `color` (color): Tints the sunlight, #rrggbb (#ff9060 = a red dusk).
- `phase` (enum; one of auto, day, sunset, night): The base light from the level's colours: day, sunset or night whatever the time; auto follows the game's day and night.
- `fade` (number; default 1; 0..60): Seconds to blend into the new light.
- `for` (for): Seconds (or "night"/"day": until the next dawn/dusk); then it fades back.
- `reset` (bool; default false): Back to the game's own light (the other params are ignored).

  `{ "do": "lighting", "brightness": 0.15, "color": "#8090ff", "fade": 3, "for": 25 }`

**`setLight`**: Gives it a light that follows it (or changes or removes its `light`): a torch on the king, a lantern on a unit, a lighthouse beam. Works on anything: units, the king, buildings, props, projectiles.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `color` (color; default "#ffd890"): #rrggbb.
- `intensity` (number; default 1; 0..4): How bright: 1 lights its range fully.
- `range` (number; default 10; 0.5..200): Radius it lights on the ground (m); a cone's length.
- `cone` (number; 1..179): A beam this many degrees wide along its facing (empty: all round).
- `aim` (number; default 0): A cone's yaw relative to its facing (degrees).
- `height` (number; default 3; 0..60): Metres above it.
- `flicker` (number; default 0; 0..1): 0..1: a fire's flicker (looks only).
- `on` (bool): Switches it on or off (keeps its settings).
- `remove` (bool; default false): Removes the light.
- `for` (for): Seconds (or "night"/"day"); then it goes back to how it was.

  `{ "do": "setLight", "target": "king", "color": "#ffc070", "range": 8 }`

### End

**`destroy`**: Removes it.

- `target` (entities; default "self"): Who it acts on: a selector (one entity or a set; each member in turn, with `it` bound). Default self.
- `effects` (bool; default true): Play death effects.
- `drops` (bool; default false): Drop its coins.
- `silent` (bool; default false): No death event.

## Functions

Selector functions anchor at `self`; call them as methods (`castle.count('enemy', 10)`) to anchor elsewhere. A filter argument is a quoted filter string.

| Function | Returns | Meaning |
|---|---|---|
| `vec(number, number, number?)` | position | A position: vec(x, z) on the ground, or vec(x, y, z). |
| `nearest(filter, number?) (also e.nearest(...))` | entity | The nearest match within r metres (anywhere if omitted), or none. Anchored at self; e.nearest(...) anchors at e. e.g. `nearest('foe and Tower', 20)` |
| `farthest(filter, number?) (also e.farthest(...))` | entity | The farthest match within r metres (anywhere if omitted), or none. Anchored at self; e.farthest(...) anchors at e. e.g. `farthest('foe and Tower', 20)` |
| `weakest(filter, number?) (also e.weakest(...))` | entity | The match with the least hp within r metres (anywhere if omitted), or none. Anchored at self; e.weakest(...) anchors at e. e.g. `weakest('foe and Tower', 20)` |
| `strongest(filter, number?) (also e.strongest(...))` | entity | The match with the most hp within r metres (anywhere if omitted), or none. Anchored at self; e.strongest(...) anchors at e. e.g. `strongest('foe and Tower', 20)` |
| `random(filter, number?) (also e.random(...))` | entity | A random match within r metres (anywhere if omitted), or none. Anchored at self; e.random(...) anchors at e. e.g. `random('foe and Tower', 20)` |
| `all(filter, number?) (also e.all(...))` | set | Every match within r metres (anywhere if omitted), nearest first (max 256). e.g. `all('ally and unit', 6)` |
| `best(filter, number, lazy) (also e.best(...))` | entity | The match with the highest score (an expression over `it`). e.g. `best('foe', 20, -it.hp)` |
| `count(filter, number?) (also e.count(...))` | number | How many match within r metres (anywhere if omitted). e.g. `count('enemy and in:self')` |
| `pointNear(position, number)` | position | A random walkable point within r metres of p. |
| `groundY(position)` | number | Height of the walkable ground at p (terrain, the map's ground or a walkable deck). e.g. `groundY(self) < -3` |
| `walkable(position, any?)` | bool | Whether something can stand at p. who: 'small' (ground enemies, default), 'large', 'air', 'player', or a unit (its own graph and surfaces). e.g. `walkable(ahead(self, 2), self)` |
| `ground(position)` | text | The ground at p: 'grass', 'highGrass', 'lowGrass', 'earth', 'sand', 'road', 'rock', 'water', 'ice', 'blocked', 'deck' or 'none'. e.g. `ground(it) == 'grass'` |
| `waterDepth(position)` | number | Metres of water over the ground at p (0 when dry or frozen). e.g. `waterDepth(it) > 0` |
| `sight(position, position)` | bool | Whether a straight line from a to b (1.5 m up) is clear of ground, cliffs, walls, buildings and solid props. e.g. `sight(self, king)` |
| `raycast(position, position)` | position | Where the line from a to b (1.5 m up) first hits ground, a cliff, wall, building or solid prop; none if clear. |
| `ahead(entity, number)` | position | d metres ahead of e (along its facing). |
| `toward(position, position, number)` | position | d metres from a toward b. |
| `lerp(any, any, number)` | any | a + (b - a) * t, for numbers or positions. |
| `spawnLine(text)` | position | The centre of a spawn line by name. |
| `dir(number)` | position | A unit vector at deg degrees (0 = north, 90 = east). |
| `angle(position, position)` | number | Degrees from a to b (0 = north). |
| `dist(position, position?) (also e.dist(...))` | number | Distance between two positions or entities; e.dist(x) from e. |
| `min(number, number, ...)` | number | The smallest. |
| `max(number, number, ...)` | number | The largest. |
| `abs(number)` | number | abs(x). |
| `floor(number)` | number | floor(x). |
| `ceil(number)` | number | ceil(x). |
| `round(number)` | number | round(x). |
| `sqrt(number)` | number | sqrt(x). |
| `sin(number)` | number | sin(x), x in degrees. |
| `cos(number)` | number | cos(x), x in degrees. |
| `clamp(number, number, number)` | number | x limited to lo..hi. |
| `pow(number, number)` | number | x to the power y. |
| `rand(number?, number?)` | number | rand(): 0..1; rand(a, b): a..b (the level's seeded random). |
| `randInt(number, number)` | number | A whole number a..b inclusive. |
| `chance(number)` | bool | True with probability p. |
| `waveCount(number, filter?)` | number | Enemies night n will spawn, optionally only templates matching a filter. |
| `waveUnits(number)` | list | The templates night n spawns. |
| `lightAt(position)` | number | How lit a point is, 0..1: daylight plus every light that reaches it (entity lights and the game's own, such as the castle's night fire). 0.5 and up counts as lit. e.g. `lightAt(self) < 0.5` |
| `strongestUnit(number)` | template | Night n's strongest template (hp x damage). |
| `<entity>.stacks(status)` | number | Stacks of a status on it. |
| `<entity>.is(filter)` | bool | Whether it matches a filter. |
| `<entity>.inside(entity)` | bool | Whether it is inside an area. |
| `<list>.at(number)` | any | The item at index i (0-based). |
| `<list>.has(any)` | bool | Whether the list or set contains x. |
| `<list>.sample(number?)` | list | k distinct items of the list or set picked at random (the level's seeded random), in random order; no k: all of them, shuffled. A set gives a set. e.g. `['tax', 'walls', 'haste', 'scouts'].sample(3)` |

## Properties

`<value>.<property>`; `.$name` reads an entity variable.

| On | Property | Type | Meaning |
|---|---|---|---|
| entity | `.hp` | number | Hit points. |
| entity | `.maxHp` | number | Maximum hit points. |
| entity | `.hpPct` | number | hp / maxHp, 0..1. |
| entity | `.alive` | bool | Not dead (a knocked-out building is not alive). |
| entity | `.elite` | bool | An elite enemy (the game's elite flag). |
| entity | `.pos` | position | Its position. |
| entity | `.x` | number | Its x. |
| entity | `.y` | number | Its y (height). |
| entity | `.z` | number | Its z. |
| entity | `.facing` | number | Its yaw in degrees (0 = north, clockwise): where a unit's or the king's model faces (its walking direction, or its target while attacking), else its own rotation. |
| entity | `.team` | text | 'player', 'enemy' or 'neutral'. |
| entity | `.kind` | text | 'unit', 'building', 'prop', 'projectile', 'area', 'pickup', 'logic', 'coin', 'king', 'line', 'point', 'path'. |
| entity | `.template` | template | The template it was made from. |
| entity | `.labels` | list | Its labels. |
| entity | `.level` | number | A build slot's level: 0 = empty (unbuilt), 1 = built, 2 = upgraded once... |
| entity | `.cost` | number | A slot's next build/upgrade cost. |
| entity | `.buildingType` | text | A building's kind, e.g. 'Mill'. |
| entity | `.age` | number | Seconds since it appeared. |
| entity | `.target` | entity | Its current attack/move target. |
| entity | `.owner` | entity | Who spawned or launched it. |
| entity | `.parent` | entity | The entity it is linked to (link, parts), or none. |
| entity | `.children` | set | The entities linked to it (its parts). |
| entity | `.root` | entity | The top of its link tree (itself when unlinked). |
| entity | `.group` | set | Its whole linked group: the root and every part below it, itself included. |
| entity | `.id` | text | Its entity id. |
| entity | `.light` | number | How lit it is, 0..1: daylight plus the lights that reach it (lightAt(its position)). |
| entity | `.lit` | bool | Whether it stands in the light (light >= 0.5): day, or near a light at night or in an eclipse. |
| entity | `.weapon` | text | Its current weapon's id ("vanilla:<weapon>"; the king's is his equipped weapon's auto attack), "none" when disarmed by setWeapon, or none. |
| entity | `.moveSpeed` | number | Movement speed, m/s. (with modifiers) |
| entity | `.attackCooldown` | number | Seconds between attacks. (with modifiers) |
| entity | `.attackRange` | number | Attack range, m. (with modifiers) |
| entity | `.damageMult` | number | Multiplies damage dealt (mods may carry from/types/chance/when). (with modifiers) |
| entity | `.damageTaken` | number | Multiplies damage taken (mods may carry from/types/chance/when). (with modifiers) |
| entity | `.projectileSpeed` | number | Projectile speed. (with modifiers) |
| entity | `.scale` | number | Size factor (visual and body). (with modifiers) |
| entity | `.keepDistance` | number | Distance a ranged unit keeps. (with modifiers) |
| entity | `.aggroTime` | number | Seconds it chases after being hit. (with modifiers) |
| entity | `.income` | number | Gold a building pays at dawn. (with modifiers) |
| entity | `.timeScale` | number | Multiplies move speed, attack and rule timers, and its own projectiles. (with modifiers) |
| position | `.x` | number | x. |
| position | `.y` | number | y. |
| position | `.z` | number | z. |
| set | `.len` | number | How many. |
| set | `.first` | entity | The first (nearest). |
| set | `.last` | entity | The last (farthest). |
| list | `.len` | number | How many. |
| list | `.first` | any | The first item. |
| list | `.last` | any | The last item. |
| template | `.id` | text | The template's id. |
| template | `.hp` | number | The template's max hp (catalog). |
| event | `.value` | any | The event's number (see each event). |
| event | `.pos` | position | Where it happened. |
| event | `.name` | text | signal: the name; varChanged: the var; status events: the status. |
| event | `.choice` | text | interact with choices: the chosen id. |
| event | `.result` | text | levelEnd: 'win' or 'lose'. |
| event | `.types` | list | damaged/hit/kill/death: the hit's damage types, e.g. event.types.has('fire'). |
| event | `.weapon` | text | damaged/hit/kill/death: the weapon that dealt the hit ("vanilla:<weapon>"), or none. |

## Fields of each block

What each JSON block accepts (a field not listed draws an "unknown field" warning). `key[]` is a list of that block, `key{}` a map of them.

- **pack**: `format`, `version`, `id`, `name`, `author`, `description`, `gameVersion`, `requires`, `assets`, `units`, `waveSets`, `levels`, `entities`, `statuses`, `presets` (blocks: `units[]` → unit, `entities[]` → entity, `statuses[]` → status, `presets[]` → preset, `levels[]` → level)
- **unit**: `id`, `side`, `base`, `name`, `description`, `visuals`, `stats`, `weapon`, `tags`, `labels`, `vars`, `targeting`, `movement`, `attacks`, `statuses`, `interact`, `parts`, `counterpart`, `light`, `rules` (blocks: `movement` → movement, `attacks[]` → attack, `interact` → interact, `parts[]` → part, `light` → light)
- **level**: `id`, `name`, `description`, `host`, `waveSet`, `base`, `buildSlots`, `spawnLines`, `props`, `terrain`, `colors`, `groups`, `logic` (blocks: `logic` → logic)
- **entity**: `id`, `kind`, `name`, `description`, `visual`, `body`, `team`, `countsAsEnemy`, `labels`, `vars`, `lifetime`, `maxAlive`, `projectile`, `mover`, `area`, `pickup`, `interact`, `light`, `parts`, `attacks`, `targeting`, `movement`, `statuses`, `rules` (blocks: `visual` → visual, `body` → body, `projectile` → projectile, `mover` → mover, `area` → area, `pickup` → pickup, `interact` → interact, `attacks[]` → attack, `movement` → movement, `parts[]` → part, `light` → light)
- **place**: `id`, `entity`, `preset`, `with`, `pos`, `rotY`, `scale`, `active`, `points`, `kind`, `name`, `description`, `visual`, `body`, `team`, `countsAsEnemy`, `labels`, `vars`, `lifetime`, `maxAlive`, `projectile`, `mover`, `area`, `pickup`, `interact`, `light`, `parts`, `attacks`, `targeting`, `movement`, `statuses`, `rules`, `_preset` (blocks: `visual` → visual, `body` → body, `projectile` → projectile, `mover` → mover, `area` → area, `pickup` → pickup, `interact` → interact, `attacks[]` → attack, `movement` → movement, `parts[]` → part, `light` → light)
- **visual**: `icon`, `tint`, `colors`, `scale`, `mesh`, `meshScale`, `meshRotY`, `meshRotX`, `meshRotZ`, `meshOffset`, `meshLook`, `motion`, `texture`, `sounds`
- **body**: `radius`, `height`, `box`, `solid`, `walkable`, `hittable`, `hp`, `flying`, `tags`
- **projectile**: `speed`, `accel`, `homing`, `arc`, `gravity`, `spin`, `face`, `hits`, `hitRadius`, `pierce`, `onGround`, `range`
- **mover**: `path`, `mode`, `speed`, `wait`, `spin`, `swing`, `running`, `pose`, `poses`, `turnSpeed` (blocks: `swing` → swing, `pose{}` → pose, `poses{}` → pose)
- **swing**: `angle`, `speed`
- **pose**: `offset`, `pos`, `rotY`, `scale`
- **area**: `shape`, `watch`, `show`, `color`, `tick`, `surface` (blocks: `shape` → shape)
- **shape**: `circle`, `rect`, `polygon`, `sector` (blocks: `circle` → circle, `rect` → rect, `polygon` → polygon, `sector` → sector)
- **sector**: `radius`, `angle`
- **light**: `color`, `intensity`, `range`, `cone`, `aim`, `height`, `flicker`, `on`
- **circle**: `radius`
- **rect**: `size`
- **polygon**: `points`
- **pickup**: `by`, `radius`, `magnet`, `consume`
- **interact**: `prompt`, `cost`, `when`, `radius`, `repeat`, `pay`, `choices`, `offer` (blocks: `choices[]` → choice, `pay[]` → pay)
- **choice**: `id`, `label`, `description`, `cost`, `pay`, `icon`, `if` (blocks: `pay[]` → pay)
- **pay**: `var`, `amount`, `label`, `level`
- **part**: `entity`, `offset`, `rotY`, `team`, `name`, `follow`, `onParentDeath`, `damageToParent`, `damageToGroup`, `damageKept`
- **attack**: `id`, `replace`, `cooldown`, `jitter`, `range`, `minRange`, `when`, `targeting`, `launch`, `weapon`, `do` (blocks: `launch` → launch)
- **launch**: `entity`, `from`
- **movement**: `mode`, `path`, `then`, `speed`, `keepDistance`, `kite`, `flee`, `wander`, `home`, `leader`, `spacing`, `offset`, `catchUp`, `surfaces` (blocks: `kite` → kite, `flee` → flee, `wander` → wander)
- **kite**: `below`, `for`
- **flee**: `when`, `from`, `to`
- **wander**: `radius`
- **status**: `id`, `name`, `description`, `icon`, `duration`, `stacking`, `maxStacks`, `tick`, `mods`, `flags`, `visual`, `rules` (blocks: `mods{}` → mod, `visual` → statusVisual)
- **mod**: `mult`, `add`, `value`, `types`, `from`, `chance`, `when`, `toward`, `away`, `dir`, `speed`
- **statusVisual**: `tint`, `fx`, `glow`
- **preset**: `id`, `name`, `description`, `category`, `kind`, `params`, `vars`, `labels`, `rules`, `visual`, `body`, `team`, `countsAsEnemy`, `lifetime`, `maxAlive`, `projectile`, `mover`, `area`, `pickup`, `interact`, `light`, `parts`, `attacks`, `targeting`, `movement`, `statuses` (blocks: `params{}` → presetParam)
- **presetParam**: `type`, `default`, `min`, `max`, `options`, `description`
- **logic**: `seed`, `vars`, `rules`, `places`, `attach`, `hud`, `kingActions`, `caps`, `game` (blocks: `places[]` → place, `attach{}` → attach, `hud[]` → hud, `kingActions[]` → kingAction, `caps` → caps, `game` → game)
- **attach**: `name`, `labels`, `vars`, `statuses`, `targeting`, `rules`, `light`, `interact`, `pay` (blocks: `light` → light, `interact` → interact, `pay[]` → pay)
- **hud**: `id`, `text`, `icon`, `visible`
- **kingAction**: `id`, `name`, `icon`, `when`, `cooldown`, `uses` (blocks: `uses` → uses)
- **uses**: `count`, `per`
- **caps**: `entities`, `units`, `projectiles`, `perTemplate`, `firingsPerFrame`, `actionsPerFrame`
- **game**: `victory`, `defeat`, `castle`, `holdNight`, `endNight`, `survivors`, `combat`, `build`, `dayLength`

## Stats

For `setStat` and status `mods` (`{"mult"}` | `{"add"}` | `{"value"}`; damageTaken/damageMult mods may add `from`, `types`, `chance`, `when`).

- `maxHp`: Maximum hit points.
- `hp`: Current hit points.
- `moveSpeed`: Movement speed, m/s.
- `attackCooldown`: Seconds between attacks.
- `attackRange`: Attack range, m.
- `damageMult`: Multiplies damage dealt (mods may carry from/types/chance/when).
- `damageTaken`: Multiplies damage taken (mods may carry from/types/chance/when).
- `projectileSpeed`: Projectile speed.
- `scale`: Size factor (visual and body).
- `keepDistance`: Distance a ranged unit keeps.
- `aggroTime`: Seconds it chases after being hit.
- `income`: Gold a building pays at dawn.
- `timeScale`: Multiplies move speed, attack and rule timers, and its own projectiles.

## Status flags

- `stunned`: Can't move or attack.
- `disarmed`: Can't attack.
- `rooted`: Can't move.
- `untargetable`: Nothing targets it.
- `invulnerable`: Takes no damage.
- `silenced`: Its rules don't fire.
- `noIncome`: A building pays nothing at dawn.

## Filter terms

Combine with `and`, `or`, `not` and parentheses.

- `foe`: The other side from self (for the level script: enemies).
- `ally`: Self's side (level script: the player's).
- `player`: The player's side.
- `enemy`: The enemy side.
- `neutral`: Neither side.
- `unit`: Units (not buildings).
- `building`: Buildings.
- `prop`: Props.
- `projectile`: Projectiles.
- `area`: Areas.
- `pickup`: Pickups.
- `coin`: The game's coins.
- `king`: The king.
- `castle`: The castle.
- `self`: The rule's owner.
- `melee`: Melee fighters.
- `ranged`: Ranged fighters.
- `alive`: Alive (not dead, switched off or an unbuilt slot).
- `dead`: Dead or knocked out. Naming it lets a selector see the dead.
- `elite`: Elite enemies.
- `lit`: Standing in the light (.light >= 0.5).
- `disabled`: Switched off (`disable`, or placed with active: false). Naming it lets a selector see switched-off things.
- `unbuilt`: An empty build slot (level 0). Naming it lets a selector see empty slots, which it otherwise skips.
- `logic`: Logic objects.
- `line`: Spawn lines.
- `point`: Points.
- `path`: Paths.
- `#label`: Has a label (#"two words" for a label with spaces).
- `is:<template>`: Made from a template (is:goblin, is:vanilla:Ogre, is:'vanilla:E Ogre').
- `in:@area`: Inside an area (in:self inside this area).
- `has:<status>`: Has a status.
- `type:<Kind>`: A building of that kind (type:Mill, type:"Defense Tower").
- `include:<what>`: Matches everything, but lets a selector also see what it skips by default: include:dead, include:disabled, include:unbuilt, include:self (repeat it for more).
- `where(expr)`: An expression over `it`.
- `<Tag>`: Any game tag (TagManager.ETag, case-sensitive): Tower, Flying, Wall, ...

Game tags: `AUTO_Alive`, `AUTO_Commanded`, `AUTO_KnockedOutAndHealOnDawn`, `AUTO_NoReviveNextMorning`, `ArmoredAgainstRanged`, `BlockableEnemyProjectile`, `Boss`, `Bridge`, `Building`, `CastleCenter`, `EnemyOwned`, `Exploding`, `FastMoving`, `FireAndExplosionResistant`, `Flying`, `Group1`, `Group2`, `Group3`, `Healer`, `House`, `Humanoid`, `InfrastructureEconomy`, `LargeUnit`, `MagicProjectile`, `MeeleFighter`, `Monster`, `NaturallyHighHealthTarget`, `NaturallyVulnerableToSplash`, `OnDeathEnemySpawner`, `Player`, `PlayerHeroUnit`, `PlayerOwned`, `PlayerOwnedPriorityTarget`, `PlayerShrine`, `PlayerUnit`, `PracticeTargets`, `RangedFighter`, `SiegeWeapon`, `TakesIncreasedDamageFromTowers`, `TakesReducedDamageFromPlayerAttacks`, `Tower`, `TrainingFacility`, `Undead`, `UnreachableForBigEnemiesDueToLevelDesign`, `UselessWallThatDoesNotBlockPath`, `Vampire`, `VodooCursed`, `VulnerableVsRanged`, `Wall`, `WallOrTower`

## Built-in signal inputs

Every entity takes these signals without a listener rule:

- every entity: `enable`, `disable`, `toggle`, `destroy`
- with a mover: `start`, `stop`, `reverse`, `goto`
- with a area: `show`, `hide`
- with a interact: `lock`, `unlock`
- a mover's named poses: the pose's name moves it there

## Caps

Per level in `logic.caps`; above the default draws a "may lag" warning, above the maximum an error.

| Cap | Default | Max |
|---|---|---|
| `entities` | 300 | 2000 |
| `units` | 200 | 1000 |
| `projectiles` | 400 | 2000 |
| `perTemplate` | 100 | 1000 |
| `firingsPerFrame` | 400 | 4000 |
| `actionsPerFrame` | 4000 | 40000 |

## Game rules

Per level in `logic.game` (op `{"op": "logic", "game": {...}}`): the game's own rules. Each is a literal or an expression the level reads again every frame (an enum takes its word or `"=expr"`), so a rule can switch it through a variable. Leave a rule out for vanilla.

| Rule | Type | Vanilla | Meaning |
|---|---|---|---|
| `victory` | enum (lastNight / rules) | `"lastNight"` | How the level is won. lastNight: surviving the last night (vanilla). rules: only an endLevel action wins. |
| `defeat` | enum (castle / rules) | `"castle"` | How the level is lost. castle: the castle falls (vanilla). rules: only an endLevel action (or resigning) loses. |
| `castle` | enum (shown / hidden) | `"shown"` | shown: the castle as usual. hidden: no castle (invisible, not solid, never targeted or hurt, and it can't lose the level). |
| `holdNight` | bool | `false` | While true, the night doesn't end, even with no enemies left (e.g. "count('#nest') > 0"). The last night's victory waits too. |
| `endNight` | bool | `false` | When it turns true at night, the night ends at once (spawning stops; survivors decides the enemies left). |
| `survivors` | enum (stay / remove) | `"stay"` | Enemies left when endNight ends a night (waves endNight has its own). stay: they stay (and keep the next night open until killed). remove: they vanish. |
| `combat` | bool | `"isNight"` | When vanilla fighting happens: units', towers' and the king's attacks, and hits on the player's side. Vanilla: "isNight". "true": by day too. |
| `build` | bool | `"isDay"` | When the king can build and upgrade. Vanilla: "isDay". "true": at night too. |
| `dayLength` | number | `0` | Seconds of day before night falls by itself (0: the player calls the night). |

## Built-in presets

Use one in a rules list as `{"preset": "<id>", "with": {params}}`, or place a logic object with `{"op": "place", "preset": "<id>", "with": {...}}`.

**`timer`** (Logic, places a logic): Sends "{signal}" every {seconds} s ({during}). Inputs: enable, disable, toggle.

- `seconds` (number; default 5): Seconds between signals.
- `signal` (signal; default "tick"): The signal it sends.
- `to` (entities; default "all"): Who receives it ("all" = every listener).
- `during` (enum; default "always"): When it runs.

**`counter`** (Logic, places a logic): Counts "{input}" signals; at {target} it sends "{signal}" and starts again.

- `input` (signal; default "count"): The signal it counts.
- `target` (number; default 3): How many to count.
- `signal` (signal; default "done"): Sent when it reaches the target.
- `to` (entities; default "all"): Who receives it.

**`relay`** (Logic, places a logic): Passes "{input}" on as "{signal}" after {seconds} s.

- `input` (signal; default "in"): The signal it listens for.
- `seconds` (number; default 1): Delay.
- `signal` (signal; default "out"): The signal it sends.
- `to` (entities; default "all"): Who receives it.

**`gate`** (Logic, places a logic): Sends "{signal}" once it has heard each of "{a}" and "{b}" (then resets).

- `a` (signal; default "a"): First input.
- `b` (signal; default "b"): Second input.
- `signal` (signal; default "open"): Sent when both arrived.
- `to` (entities; default "all"): Who receives it.

**`toggle-switch`** (Logic, places a logic): Each "{input}" flips it, sending "{on}" or "{off}".

- `input` (signal; default "flip"): The signal that flips it.
- `on` (signal; default "on"): Sent when it turns on.
- `off` (signal; default "off"): Sent when it turns off.
- `to` (entities; default "all"): Who receives it.

**`random-router`** (Logic, places a logic): Passes "{input}" on as "{a}" or "{b}" at random ({chanceA} for the first).

- `input` (signal; default "in"): The signal it listens for.
- `a` (signal; default "left"): One output.
- `b` (signal; default "right"): The other output.
- `chanceA` (number; default 0.5): Chance of the first output.
- `to` (entities; default "all"): Who receives it.

**`spawner`** (Logic, places a logic): On "{input}", spawns {count} {unit} here.

- `input` (signal; default "spawn"): The signal that spawns.
- `unit` (template; default "vanilla:E Racer"): What it spawns.
- `count` (number; default 1): How many.
- `spread` (number; default 2): Scatter radius.

**`lever`** (Logic, places a logic): The king pulls it ({when}) to send "{signal}".

- `prompt` (text; default "Pull the lever"): Shown when the king is near.
- `signal` (signal; default "pulled"): The signal it sends.
- `to` (entities; default "all"): Who receives it.
- `cost` (number; default 0): Gold the king pays.
- `when` (enum; default "day"): When it can be used.
- `repeat` (bool; default true): Can be pulled again.

**`pressure-plate`** (Logic, places a area): Sends "{pressed}" when a {watch} steps on it and "{released}" when it's empty.

- `size` (number; default 3): Width of the square plate.
- `watch` (filter; default "unit or king"): What presses it.
- `pressed` (signal; default "pressed"): Sent when something steps on.
- `released` (signal; default "released"): Sent when it's empty again.
- `to` (entities; default "all"): Who receives them.

**`door`** (Logic, places a prop): A solid door. Signals "open" and "closed" swing it (poses).

- `angle` (number; default 90): How far it swings open, degrees.
- `startOpen` (bool; default false): Starts open.

**`moving-wall`** (Logic, places a prop): A solid wall that moves along a path ({mode}) at {speed} m/s. Inputs: start, stop, reverse.

- `path` (entity; default "@path"): A placed path (@name).
- `speed` (number; default 2): m/s.
- `mode` (enum; default "pingpong"): How it travels.
- `wait` (number; default 1): Seconds paused at each point.

**`sign`** (Logic, places a prop): Shows "{text}" when the king comes near.

- `text` (text; default "Beware!"): The message.
- `range` (number; default 4): How near, metres.

**`gold-pickup`** (Logic, places a pickup): The king collects it for {amount} gold.

- `amount` (number; default 5): Gold.

**`hud-counter`** (Logic, places a logic): Each "{input}" adds 1 to the level variable ${var}; at {target} it sends "{signal}".

- `var` (var; default "count"): A level variable (declare it in logic.vars).
- `input` (signal; default "add"): The signal that counts.
- `target` (number; default 10): The goal.
- `signal` (signal; default "goal"): Sent at the goal.

**`split-on-death`** (Enemy): Splits into {count} copies, {depth} times.

- `count` (number; default 2): Copies per split.
- `depth` (number; default 3): Generations.
- `into` (template; default "=self.template"): What it splits into (default: itself).
- `shrink` (number; default 0.75): Size of each generation relative to the last.

**`spawn-on-death`** (Enemy): When it dies, {count} {unit} appear where it fell.

- `unit` (template; default "vanilla:E Racer"): What appears.
- `count` (number; default 1): How many.

**`aura`** (Enemy): Every second, gives {status} to {who} within {range} m.

- `status` (status; required): The status (from the pack's statuses).
- `who` (filter; default "ally and unit"): Who gets it.
- `range` (number; default 6): Metres.

**`explode-near`** (Enemy): When a {filter} is within {trigger} m, it explodes for {damage} damage in {radius} m.

- `filter` (filter; default "foe"): What sets it off.
- `trigger` (number; default 2): Trigger range.
- `damage` (number; default 40): Damage.
- `radius` (number; default 3): Blast radius.

**`multiply`** (Enemy): Every {seconds} s at night it makes a copy of itself, up to {max} alive.

- `seconds` (number; default 8): Seconds between copies.
- `max` (number; default 40): Cap on live copies.

**`regenerate`** (Enemy): Heals {amount} hp every {seconds} s.

- `amount` (number; default 5): Hit points per tick.
- `seconds` (number; default 1): Seconds between heals.

**`blink-away`** (Enemy): Below {pct} health, once, it teleports near {to} and heals {heal}.

- `pct` (number; default 0.5): Health fraction.
- `to` (position; default "castle"): Where it blinks to.
- `heal` (number; default 0.3): Fraction of max hp healed.
