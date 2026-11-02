# PalaceMod Builder: design

Goal: players create and trade custom units, enemies, waves and levels without writing code, using vanilla assets first and imported assets second.

## Recommendation

1. **MVP:** custom enemies and units (a vanilla prefab plus stat, tag, weapon, name, icon and tint overrides) and custom wave sets, all played on the existing maps. Designs are JSON packs in a folder, shared as files or as copy/paste share codes, with an in-game editor made of property panels. No new terrain and no server.
2. **Stage 2:** level overlays. On a vanilla host map, add, move, remove or retune build slots, upgrade trees and costs, spawn lines and props, then rescan the navmesh. Custom levels appear in a "Custom" list.
3. **Stage 3:** imported assets (PNG icons and skins, WAV/OGG sounds, OBJ meshes, then glTF), a placement editor with gizmos, and generated terrain on a stripped host map, following Falkenpass's approach.
4. **Optional:** a static web editor and browser fed by `catalog.json`, and a small community index. Build a hosted server only if sharing takes off.

Everything is overlaid onto a real shipped scene. Creating a gameplay scene from nothing is not worth it, because the managers, camera, day/night cycle, UI hooks and lighting all live in the level scenes.

## Prior art: takeaways

| Source | What to copy | What to avoid |
|---|---|---|
| **Architect** (Silksong / HK, [repo](https://github.com/cometcake575/Architect-Silksong), [docs](https://starshooter.gitbook.io/architect)) | One JSON file per scene of placements `{type, id, pos, rot, scale, layer, config}`. A typed config schema (Bool/Choice/Colour/Float/Int/Id/String/Vector) drives the property panel automatically. Editor features: ghost preview, hotbar, pick/copy, marquee select, undo/redo, layers and lock, and playing in place by leaving edit mode. Assets are referenced by URL and cached locally. Vanilla objects are preloaded by name, with per-object "fixers". | Downloads overwrite local work. A self-hosted Flask server with password accounts is an ongoing maintenance and moderation burden. |
| **Falkenpass** (the only custom Thronefall map, MIT, [engineering notes](https://github.com/Polymerase-Tensil/falkenpass/blob/main/docs/engineering.md)) | Proof that our approach works. It loads Nordfels as the host, clones 41 native `BuildSlot`s **before `Start()`** so Unity remaps child references and native initialisation registers everything, appends paid upgrades to `BuildSlot.upgrades`, regenerates terrain, and rescans the four native graphs (`Enemy Units S`, `Player Units S`, `Enemy Units L`, `Enemy Units Air`). It uses deterministic IDs for save GUIDs and keeps its saves separate from the game's. | Its procedural map is hand-coded in C#, which is the opposite of no-code. `GPUInstanced` enemies ignore disabled renderers, so visual changes must go through `GPUInstancingManager`. |
| **BTD6 Challenge/Map Editor** | Short share codes, and a "must beat it before sharing" rule. | Codes stored on the vendor's server. |
| **CustomKnight (HK)** | Reskins from PNG folders by naming convention, with no code. | |
| **Lethal Company LLL, RoR2, Rain World** | AssetBundle and Unity-editor pipelines are for power users only. Thunderstore packs work for distribution. | Requiring the Unity editor for ordinary content. |
| **Factorio, DSP, Mindustry** | Share string = prefix + version + base64(deflate(JSON)). Keep it under about 2,000 characters so it fits in a Discord message. | Embedding asset bytes in codes. |
| **Thronefall ecosystem** | Thunderstore has about 10 packages (EasyFall tunes waves and stats through a menu; ThronefallMultiplayer). There is no level editor and no Steam Workshop, and a developer called Workshop "unlikely" in 2023. | |

## Thronefall internals: feasibility map

Evidence comes from the decompiled `Assembly-CSharp`, `AstarPathfindingProject.dll`, and a read-only UnityPy scan of `Thronefall_Data`.

### Key facts

- **Most prefabs are globally loadable.** The `ResourceManager` container in `globalgamemanagers` lists 778 Resources paths. They include every enemy (`e archer`, `e ogre`, `e ghost`, ...), the bosses (`bosses/e eldermole`), the enemy buildings (`enemy buildings/...`) and the player building prefabs (`archer hut`, `swordsmen hut`, `defense tower variant`, `gate variant`, `house variant`, `mill variant`, `temple`, `shrine`, `castle center variant`, ...).
  - So `Resources.Load<GameObject>(path)` works in any scene, with no harvesting from other levels.
  - Player unit prefabs (`P Knight`, `P Archer`, ...) live in `resources.assets` as well.
- **All `LevelInfo`s and their spawner prefabs are loaded from the start menu onward.** `sharedassets1.assets` holds 37 `LevelInfo`s, 36 `EnemySpawner` prefabs (`ES Nordfels`, ...), and `EternalTrialEnemySet "All Enemies"`, which lists every enemy with a `difficultyValue`. `LevelProgressManager.instance.levelInfos` reaches all of them.
- **Scenes:** 41 scenes are in build settings: `_Boot`, `_StartMenu`, `_UI`, `_LevelSelect`, 10 main maps, and the mini modes (`Assets/Scenes/Mini Modes/...`, `NEW MINI MODES/...`). Each level scene has one `AstarPath` (`A_ Manager`), one `NavmeshBakeHelper`, one `EnemySpawner`, 5 to 33 `EnemySpawnLine`s and up to 144 `BuildSlot`s.
- **Pathfinding:** A* Pathfinding Project **Pro 5.4.4** (`AstarPath.Version`). The graphs are triangle-mesh graphs (`MinimapRenderer` reads `TriangleMeshNode`), selected by name through `GraphMask.FromGraphName`.
  - `BuildSlot.navmeshCut` (`NavmeshCut`) means dynamic cutting already happens at runtime.
  - `AstarPath.Scan()` / `ScanAsync()` is available, and Falkenpass rescans at runtime.
- **Other libraries already in `Managed/`:** `Newtonsoft.Json`, `System.IO.Compression`, `Unity.Burst`, `Unity.Mathematics`, `Unity.Collections`, the URP runtime, FlatKit shaders, `UnityWebRequestAudio` and `ImageConversion`.
  - `Unity.Formats.Fbx.Runtime` and `Autodesk.Fbx` are present, but the native FBX SDK is not in `PlugIns`, so there is no FBX import.

### Map by system

| System | Classes and fields | Difficulty | Notes |
|---|---|---|---|
| **Wave sets** | `EnemySpawner.waves: List<Wave>`. `Wave{warningText, spawns, difficultyMulti}`. `Spawn{delay, enemyPrefab, eliteEnemies, count, interval, spawnLine: Transform, goldCoins}`. `goldBalanceAtStart`. | **Easy** | Replace `waves` and `goldBalanceAtStart` in a prefix on `EnemySpawner.Start`; the Eternal Trials and endless modes already do this through `EternalWaveGenerator.GenerateWaves`. Perk modifiers (cheese, rat, loan, elite god) then apply as normal. Reference spawn lines by `EnemySpawnLine` GameObject name ("Spawn 1", "Left Air", ...) inside the scene. Night preview UI (`Wave.GetWaveInfo`) works as-is because it reads `ScreenMarkerIcon` from the prefab. |
| **Enemy routing** | `PathfindMovementEnemy` (Seeker to `targetPriorities`, `backupMovementGraph`), `EnemySpawnLine.canSpawnFlying/SmallGround/BigGround` | Easy to use; hand-authored routes are not supported | Routes emerge from A* toward targets. A designer controls only where spawn lines are and which unit types they allow. Authored waypoint paths would need a new movement component (skip). |
| **Custom enemy/unit stats** | `Hp.maxHp/coinCount/enemyToSpawnOnDeath`, `AutoAttack.cooldownDuration/targetPriorities/weapon`, `PathfindMovementEnemy/Playerunit.movementSpeed/keepDistanceOf/agroTimeWhenAttackedByPlayer`, `Weapon` (a ScriptableObject: `directDamage/splashDamage: List<DamageModifyer{requiredTags, damageAdded, damageMultiplyer}>`, `projectileSpeed`, `projectileVisuals`), `TaggedObject.tags` (private, 50 `TagManager.ETag`s, e.g. `Flying`, `LargeUnit`, `ArmoredAgainstRanged`) | **Easy to medium** | Build a template once per design by `Instantiate`-ing the vanilla prefab under an inactive root and applying overrides **before Awake/Start**. `Weapon` is shared, so clone it before editing. Switching flying to ground also needs the `Seeker.graphMask` changed (`EternalTrialEnemy` reads graphs 0, 1 and 2). **Gotcha:** `Spawn.Update` activates `enemyPrefab` in place when `enemyPrefab.scene.isLoaded`, and an inactive template in DontDestroyOnLoad counts as a loaded scene. Patch `Spawn.Update` to `Instantiate` our templates. |
| **Unit names and descriptions** | `ScreenMarkerIcon.locaKey`, translated through `TextTranslator.Translate(locaKey + " Name")` (I2 Loc). `ScreenMarkerIcon.sprite`. | Easy | Patch `TextTranslator.Translate` to return the design's text for keys of the form `PalaceBuilder/<id>`. Icons are a sprite swap. |
| **Behaviours** | `AutoAttack*` variants (`HighestHealth`, `LowestHealth`, `Random`, `WithKnockback`, ...), `KnockbackBehavior`, `SelfDestructWhenInRangeOf`, `AutoRevive`, `ShieldOnFirstDamaged`, `Invulnerable`, `SlowEffect`, `HealOnKillWeaponEffect`, ... | Medium | Offer a whitelist of "behaviour modules" that add or copy a known component with exposed fields. Never allow arbitrary components, because they break on game updates. |
| **Player units in buildings** | `UnitRespawnerForBuildings.units: List<Hp>`, `timeToRespawnAUnitDependingOnLevel`. Units are children inside the building hierarchy, switched on by `UpgradeBranch.objectsToActivate`. | Medium | A custom unit replaces the stats of those child units, or swaps the child for an instance of our template. Command and commander systems (`CommandUnits`) find units by tag. |
| **Build slots and upgrade trees** | `BuildSlot.upgrades: List<Upgrade{cost, upgradeTooltip, upgradeBranches}>`, `UpgradeBranch{choiceDetails: Choice{name, tooltip, icon}, replacementMesh, goldIncomeChange, hpChange, objectsToActivate/Disable}`, `activatorBuilding`, `activatorLevel`, `requiredRootLevelDifference`, `startDeactivated`, `navmeshCut` | **Medium** | Costs, income and HP are data edits. Moving or adding slots means cloning a scene slot (or a Resources building prefab) **before Start**, remapping `activatorBuilding`, and giving each a deterministic `SaveLoadEntity` GUID, as Falkenpass did. Branches that toggle children are limited to children the cloned building already has, so "new building types" are combinations of existing ones. |
| **Props and decoration** | Scene GameObjects, `MeshFusionPro` (combined meshes), `GPUInstanced` | Medium | Adding props is easy if they are cloned from scene objects or from a mesh and material. Removing or moving vanilla props is harder, because MeshFusion combines static meshes, so whole chunks may need hiding. Props must be on navmesh-relevant layers, and the navmesh rescanned, if they should block movement. |
| **Navmesh** | `AstarPath` graphs `Enemy Units S/L/Air` and `Player Units S`; `NavmeshBakeHelper` (bake settings, `flyerSurface`, `invisibleWalls`); `NavmeshCut` | Medium for small edits, hard for new terrain | Small edits use `NavmeshCut` / `GraphUpdateObject`. Large edits need `AstarPath.active.Scan()` after the overlay. That is proven by Falkenpass, but we must reproduce the graph settings, and Burst may fall back to managed code and be slower. |
| **Terrain** | Static meshes per scene (not Unity Terrain) | **Hard** | Shaping existing terrain is impractical. New terrain means a stripped host map, generated or imported meshes, a collider, and a full rescan (Falkenpass did this in code). Leave it to stage 3+. |
| **Lighting and look** | `DayNightCycle`, `ColorAndLightManager`, `Colorscheme`, FlatKit/URP materials; enemies share the "Enemy" material (elite swaps to `ColorAndLightManager.EliteEnemyMaterial`) | Easy to keep, hard to change | Overlays inherit the host's lighting. Tints need per-design material clones and must notify `GPUInstanced.OnMeshOrMaterialUpdate`. |
| **New scene from nothing** | `SceneManager.CreateScene` plus a copy of every manager | **Not worth it** | `SceneTransitionManager` loads by build-list name. Use a host scene instead. |
| **Level select** | `_LevelSelect` overworld with physical `LevelInteractor{levelInfo}` objects. `LevelSelectManager.PlayButtonPressed` calls `SceneTransitionManager.TransitionFromLevelSelectToLevel(sceneName)`. `LevelProgressManager.levelInfos` (array). | Medium | Add a "Custom levels" UI frame or a single interactor in the overworld rather than injecting physical interactors. Launching sets `BuilderSession.Current`, transitions to the host scene, and applies the overlay on `SceneManager.sceneLoaded` (after Awake, before Start). |
| **Saves, scores and achievements** | `LevelProgressManager.GetLevelDataForScene(sceneName)` (scores keyed by **scene name**). `MatchSaveLoadHandler` (`currentMap + ".json"`, `SaveLoadForbidden`, per-object `SaveLoadEntity.GUID`). `AchievementManager`. `LevelInfo.ignoreSaves`. | Medium | During a custom session, redirect level data to a builder store keyed by design id, and set `SaveLoadForbidden` (mid-match saves) for the MVP. Suppress achievements and quest progress so custom content can't farm vanilla unlocks. |


## Status

| WP | State |
|---|---|
| WP1 Data model and loader | **Landed.** `Model/`, `Contracts/`, `PackRepository`, `EditHistory`. |
| WP2 Catalog and runtime | **Landed.** `Runtime/`, `builder` dev command, sample pack, interim `SessionLauncher`. |
| WP3 Session and level select | Open. Interim launcher and isolation patches are in `Session/` (see below). |
| WP4a Editor UI | Open. |
| WP4b / WP4c Level overlay, placement editor | Stage 2. Data shape fixed (`LevelDef`), stub `ILevelOverlay`. |
| WP5 Asset import | Open. Stub `IAssetProvider` returns null. |
| WP6 Sharing | **Landed.** `Sharing/`: `ShareCodec`, `PackArchive` (`.tfpack` and Thunderstore zips), import folder watcher, `SharingUI`, `builder-share` dev command, `tests/PalaceMod.Tests`. |

## Architecture (as built, WP1 and WP2)

All Builder code lives under `src/PalaceMod/Features/Builder/`, in namespace `PalaceMod.Builder` (data classes in `PalaceMod.Builder.Model`). One feature manager, `BuilderManager` (settings section `Builder`, one `Enabled` row), hosts every Builder Harmony patch. It is a `partial` class: each work package adds `Features/Builder/<Package>/BuilderManager.<Package>.cs` with its nested patch classes, so no package edits another's file.

```text
Features/Builder/
  BuilderManager.cs                    WP1/2  feature manager: installs services, scene hook, `builder` dev command
  Contracts/Contracts.cs               WP1    all service interfaces + catalog/session DTOs (frozen; change by PR note)
  Contracts/BuilderServices.cs         WP1    service locator + stubs for packages not landed yet
  Model/*.cs                           WP1    DesignPack, UnitDef, WaveSetDef, LevelDef, ContentRef, Validator,
                                              PackSerializer + Migrations, EditHistory (undo), PackRepository
  Runtime/Catalog.cs                   WP2    ICatalog: vanilla units, weapons, hosts, spawn lines, vanilla waves
  Runtime/UnitFactory.cs               WP2    IUnitFactory: custom unit templates
  Runtime/WaveInjector.cs              WP2    IWaveInjector: custom waves into EnemySpawner
  Runtime/BuilderManager.Spawning.cs   WP2    patches: EnemySpawner.Start, template-aware spawning, unit names
  Session/SessionLauncher.cs           WP3    ISessionLauncher (interim minimal version by WP2)
  Session/BuilderManager.Session.cs    WP3    patches: score/save/achievement isolation during a session
Assets/builder-sample.json             WP2    built-in sample pack (embedded resource)
```

`BuilderManager` has `[ManagerOrder(-30)]`, so its `Awake` (which installs the services) runs before default-order managers. It keeps its patches applied when the feature is switched off (`UnpatchWhenDisabled => false`): the session isolation patches must keep protecting a custom run that's already in progress. Switching the feature off blocks new launches and clears templates once no session runs.

### Packs on disk

```text
BepInEx/PalaceBuilder/                       IPackRepository.Root
  packs/<packId>/pack.json                   one pack per folder; folder name = pack id (writable "User" packs)
  packs/<packId>/assets/...                  files the pack's "assets" entries point at (WP5 loads them)
  trash/<packId>-<yyyyMMdd-HHmmss>/          Delete() moves packs here; nothing is ever deleted outright
  cache/<sha256>                             downloaded assets (WP5)
  import/                                    drop .tfpack/.zip/.txt (share code) files here; moved to import/imported or
                                             import/failed (with <file>.why.txt) once handled (WP6)
  exports/                                   .tfpack and Thunderstore zips written by Export (WP6)
  staging/<guid>/                            temporary extraction folder during an import (WP6, removed after)
  scores.json                                per-design results (WP3)
  catalog.json                               written by `builder dump`
BepInEx/plugins/<mod>/PalaceBuilder/packs/<packId>/   read-only "Plugin" packs shipped as Thunderstore mods
PalaceMod.dll resource PalaceMod.Assets.builder-*.json read-only "BuiltIn" packs (the sample)
```

The folder is under BepInEx rather than `Application.persistentDataPath`, which is the game's save folder. The repository rescans when any `pack.json` changes (file times polled every 2 s, never during a session), so editing a pack in a text editor while the game runs works. Duplicate ids: built-in wins over plugin over user; the loser is listed with an error. A pack with errors is listed (so the editor can show and fix it) but can't be launched.

### Data model (version 1)

The format is JSON, read with the game's own `Newtonsoft.Json` (13.x, already in `Managed/`). Rules:

- Optional values are nullable. `null` or absent means "keep the base's value", never zero.
- Unknown fields are kept (`[JsonExtensionData] Extra`) and written back, so newer packs survive older editors.
- `PackSerializer.Read` runs `Migrations.Upgrade` first. To change the schema, bump `DesignPack.CurrentVersion` and add a `Steps[oldVersion]` function that edits the `JObject`. A pack from a newer version is refused with "update PalaceMod".
- Ids (pack, unit, wave set, level, asset key): 1 to 64 characters of `a-z 0-9 . _ -`, starting with a letter or digit (`Ids.IsValid`, `Ids.FromName`).

```jsonc
{
  "format": "palace-builder", "version": 1,
  "id": "palacemod.sample", "name": "Sample: Goblin Night", "author": "PalaceMod", "description": "...",
  "gameVersion": "...",                                 // informational
  "requires": { "palaceMod": ">=1.0", "packs": ["other.pack"] },
  "assets": { "goblin_icon": { "path": "assets/goblin.png" } },   // or { "url": "...", "sha256": "<64 hex>" }
  "units": [{
    "id": "iron-brute", "side": "enemy",               // "enemy" | "player"
    "base": "vanilla:E Melee",                         // catalog id; only vanilla bases in v1
    "name": "Iron Brute", "description": "...",        // shown in the night preview and tooltips
    "visuals": { "tint": "#c9a227", "scale": 1.5, "icon": "asset:goblin_icon", "mesh": "asset:..." },
    "stats": { "maxHp": 120, "moveSpeed": 2.5, "attackCooldown": 1.6, "attackRange": 3, "keepDistance": 2, "aggroTime": 5 },
    "weapon": { "base": "inherit",                     // or "vanilla:<weapon id>"
                "damage": [{ "tags": [], "add": 6 }, { "tags": ["Building"], "mult": 3 }],   // replaces the list
                "splash": null, "damageMult": 1.5, "projectileSpeed": 20 },
    "tags": { "add": ["ArmoredAgainstRanged"], "remove": [] },
    "behaviours": [{ "type": "SpawnOnDeath", "unit": "goblin-runner" }]
  }],
  "waveSets": [{
    "id": "goblin-night", "name": "Goblin Night", "host": "Nordfels", "startingGold": 20,
    "nights": [{ "difficulty": 1.0, "warning": "",
      "spawns": [{ "unit": "goblin-runner", "count": 8, "interval": 0.5, "delay": 0, "line": "Forest", "gold": 6, "elite": false }] }]
  }],
  "levels": [{ "id": "...", "host": "Nordfels", "waveSet": "goblin-night",
    "buildSlots": { "remove": [], "edit": [{ "ref": "...", "upgrades": [{ "cost": 2 }] }],
                    "add": [{ "id": "s1", "base": "scene:...", "pos": [0,0,0], "rotY": 90, "activator": "...", "activatorLevel": 1 }] },
    "spawnLines": { "add": [{ "id": "north", "pos": [0,0,80], "rotY": 0, "width": 12, "ground": true, "large": true, "flying": false }] },
    "props": [] }]
}
```

Semantics worth knowing:

- **Stats are absolute.** The night's `difficulty`, elite status and the player's perks still multiply them at spawn, as for vanilla enemies.
- **There is no per-unit gold stat.** The game overwrites `Hp.coinCount` from the spawn group's `gold` at spawn time, so gold belongs to `SpawnDef.gold`.
- **`weapon.damage` replaces the whole direct-damage list.** Each entry applies if the target has all its `tags`: damage = (damage + add) × mult, in order. The base's list is in `catalog.json`, to copy and edit.
- **Movement tags are refused.** Adding or removing `Flying` or `LargeUnit` would need a pathfinding graph change. Pick a base that already has them.
- **`warning` is stored but not shown.** The game never displays `Wave.warningText`. WP3 or WP4a may show it with `Notify` at dusk.
- **`levels`** are validated for shape only. Applying them is WP4b; until then they produce a warning, and a level's `waveSet` still works.
- **Behaviour modules** form a whitelist (`BehaviourTypes`). v1 has only `SpawnOnDeath {unit}`. To add one, add a constant, a validator case and a `UnitFactory.ApplyBehaviour` case.

### References

`ContentRef.Parse(string)` returns `{Kind, Id, Pack}`:

| Form | Kind | Meaning |
|---|---|---|
| `vanilla:E Racer` | Vanilla | catalog unit or weapon (prefab or asset name; exact match, then case-insensitive) |
| `goblin-runner` or `unit:goblin-runner` | LocalUnit | unit in the same pack |
| `other.pack/goblin-runner` | PackUnit | unit in another installed pack (list it in `requires.packs`) |
| `asset:goblin_icon` | Asset | entry of the pack's `assets` |
| `scene:House Variant (2)` | Scene | GameObject on the host map (level overlays) |

### Validation

`Validator.Validate(pack, IValidationContext game)` returns `ValidationIssue{Severity, Path, Message}` with JSON-style paths (`units[1].stats.maxHp`), suitable for highlighting the field in the editor. With `game == null` it checks structure only; `ICatalog` is the game context (vanilla ids, hosts, spawn line names, other packs). `IPackRepository.Validate(pack)` validates against the live catalog. It checks ids and uniqueness, ref resolution, the unit side for wave spawns, tag names, numeric ranges, colours, asset keys and paths, spawn line names per host, and wave/level host agreement.

### Runtime (WP2)

- **Catalog.** Built lazily on first use from loaded assets, from the main menu on (no scene loads). It reads every `LevelInfo.enemySpawner` prefab's waves, every `EternalTrialEnemySet` (for `difficulty`), their `enemyToSpawnOnDeath`, the God of Afterlife enemy, loaded `PathfindMovementPlayerunit` prefabs, their weapons, and hosts (LevelInfos with a spawner, tutorial excluded) with spawn lines read from the spawner prefab's `EnemySpawnLine` children. `CatalogHost.VanillaWaves` gives each host's vanilla waves as a `WaveSetDef`, as an editor starting point.
- **Unit templates.** `IUnitFactory.Resolve(pack, ref)` returns the vanilla prefab, or for custom units a template: `Instantiate(basePrefab, inactiveDontDestroyOnLoadRoot)`, so no Awake, Start or OnEnable runs, with overrides written to serialized fields. Weapons and tinted materials are cloned per template, so vanilla assets are never modified. Templates are cached per `<pack>/<unit>` and rebuilt when the unit's JSON changes, and are cleared when packs change (outside sessions). The game spawns them with its normal `Object.Instantiate`, so the clone initialises itself and every perk and difficulty multiplier applies.
- **Names.** A custom name or description sets the template's `ScreenMarkerIcon.locaKey` to `PalaceBuilder/<pack>/<unit>`, answered by a `TextTranslator.Translate` prefix. The night preview, the pause menu wave list and tooltips show it with no further work.
- **Template-aware spawning** (a transpiler on `Spawn.Update`, `EnemySpawner.StartSpawning` and `EnemySpawner.GetWaveInfo`): `scene.isLoaded` becomes "loaded and not the template scene", and `GameObject.GetComponentInChildren<T>()` becomes `GetComponentInChildren<T>(true)` for templates only. Vanilla behaviour is unchanged. If a game update removes the `isLoaded` check, the transpiler logs an error.
- **Wave injection.** A prefix on `EnemySpawner.Start` replaces `waves` and `goldBalanceAtStart` when a wave set is pending, the scene is its host, and the mode is Classic. Spawn lines are resolved by GameObject name among the spawner's `EnemySpawnLine` children; unresolvable spawns are skipped with a logged issue. The game's own Start logic then applies Elite God, Rat God, Loan, Royal Mint and Cheese God to the custom waves.

### Session isolation (interim, WP3 owns)

`SessionLauncher.Launch` validates, pre-builds every template (so errors surface before loading), queues the wave set, sets Classic mode, sets `MatchSaveLoadHandler.OverwriteCurrentSave` and calls `TransitionFromNullToLevel(host)`. While `Current != null`:

- `LevelProgressManager.GetLevelDataForScene(host)` returns the session's own `LevelData` (`Current.Scores`), so highscores, "beaten", networth and quest state never reach the host's record.
- `SaveLoadManager.SaveGame` is skipped. `MatchSaveLoadHandler.SaveRun` only converts in memory (retry day keeps working) and never writes the host's mid-match file.
- `AchievementManager` unlock calls are skipped. Perk level and XP are snapshotted at launch and restored when the session ends.
- On load of the host, `GameplayImpact.Mark("Builder: custom content")` runs and a short notice says scores and saves aren't recorded.
- The session ends when any other scene loads (single mode): menu, level select or another map.

### Dev commands

`python3 scripts/dev.py cmd "builder <sub>"`:

| Sub | Does |
|---|---|
| `dump [path]` | writes `catalog.json` (default `BepInEx/PalaceBuilder/catalog.json`): units with stats, tags, weapon and difficulty; weapons with damage lists; hosts with spawn lines and vanilla waves; tag and behaviour names |
| `packs` | rescans and lists packs with error and warning counts |
| `validate <pack>` | prints a pack's issues |
| `play <pack> [waveSet]` | launches a custom session (first wave set by default) |
| `end` | ends the session |
| `status` | session, scene, gameplay impact, spawner waves and gold, perk level and XP |
| `live` | live enemies grouped by name with their actual stats (to check overrides after spawn) |
| `spike scan\|weapon\|material` | reruns the feasibility spikes below |

## Spike results

Run with `builder spike ...` and `builder play palacemod.sample` in dev launches (Rosetta), game version GAMEVER.

SPIKES_TBD

Not yet run (not needed for the interfaces): cloning a `BuildSlot` before Start (stage 2, WP4b) and loading glTFast (WP5).

## Interfaces

All are in `Features/Builder/Contracts/Contracts.cs` and reached through `BuilderServices` (never through a concrete class). They are main-thread only. A package installs its implementation in its own manager's `Awake` (any `ManagerOrder` above -30, the default 0 is fine) with `BuilderServices.X = new MyImpl();`. Until then a stub answers, so callers never null-check.

| Service | Interface | Owner | Implementation now |
|---|---|---|---|
| `BuilderServices.Packs` | `IPackRepository`: `Root`, `Packs`, `Find`, `Refresh`, `Changed`, `Save`, `InstallNew(pack, sourceFolder)`, `Delete`, `Validate` | WP1 | `PackRepository` |
| `BuilderServices.Catalog` | `ICatalog : IValidationContext`: `Units`, `Weapons`, `Hosts`, `FindUnit/Weapon/Host`, `ToJson` | WP2 | `Catalog` |
| `BuilderServices.Units` | `IUnitFactory`: `Resolve(pack, unitRef, out error)`, `IsTemplate`, `Clear` | WP2 | `UnitFactory` |
| `BuilderServices.Waves` | `IWaveInjector`: `SetPending(pack, waveSet)`, `Pending`, `BuildWaves(...)` | WP2 | `WaveInjector` |
| `BuilderServices.Sessions` | `ISessionLauncher`: `Current`, `Launch(SessionRequest, out error)`, `End`, `Started`, `Ended` | WP3 | `SessionLauncher` (interim) |
| `BuilderServices.Overlay` | `ILevelOverlay`: `Apply(pack, level, issues)` | WP4b | `NullLevelOverlay` (warns) |
| `BuilderServices.Assets` | `IAssetProvider`: `GetSprite`, `GetAudio`, `GetMesh(PackEntry, assetRef)` | WP5 | `NullAssetProvider` (null) |
| `BuilderServices.Share` | `IShareCodec`: `Encode(pack)`, `Decode(code, issues)` | WP6 | `ShareCodec` |
| `BuilderServices.Archives` | `IPackArchive`: `ExportFolder`, `Export(entry, path)`, `Import(path, issues)`, `ExportThunderstore(entry, path)` (**added by WP6**, additive; stub `NullPackArchive`) | WP6 | `PackArchive` |

Interfaces are frozen. If a package needs a change, add a member (don't change existing signatures), keep the stub compiling, and note it in the PR and in this table.

Shared data types (also in `Contracts.cs`): `PackEntry {Pack, Folder, Source, ReadOnly, Issues, HasErrors, Id}`, `PackSource {User, Plugin, BuiltIn}`, `CatalogUnit`, `CatalogWeapon`, `CatalogHost`, `CatalogSpawnLine`, `SessionRequest {Pack, WaveSetId, LevelId}`, `BuilderSessionInfo {Pack, WaveSet, Level, HostScene, StartedUtc, Scores}`. The model is in `Model/`: `DesignPack`, `UnitDef` (+ `VisualsDef`, `StatsDef`, `WeaponDef`, `DamageModDef`, `TagEditDef`, `BehaviourDef`), `WaveSetDef` (+ `NightDef`, `SpawnDef`), `LevelDef` (+ overlay types), `ContentRef`, `Ids`, `Validator`, `ValidationIssue`, `PackSerializer`, `Migrations`, `EditHistory`.

### File ownership rules

- Each package owns its folder under `Features/Builder/` and may add `BuilderManager.<Package>.cs` there for Harmony patches. Patch classes must be nested in `BuilderManager`, which is why it's partial.
- Don't edit another package's folder. `BuilderManager.cs`, `Contracts/` and `Model/` are WP1/WP2's. Additive changes there (a new interface member, a new model field with a migration if needed) are fine, but call them out in the PR.
- New settings go in the `Builder` section (max 6 more rows; split into pages beyond that) through `Plugin.Config.Bind("Builder", ...)` in your manager's `Awake`.
- New `builder` subcommands: register a separate dev command (`builder-ui`, `builder-share`, ...) rather than editing the `builder` switch.

## Work package briefs

### WP3: session and level-select integration

**Goal:** players start custom runs from the game's own UI, and results are kept apart from vanilla progress.

**Owns:** `Features/Builder/Session/` (`SessionLauncher.cs`, `BuilderManager.Session.cs`), plus any new files there. Replace or extend `SessionLauncher` freely; keep it implementing `ISessionLauncher`.

**Already there (interim, by WP2):**

- `Launch(SessionRequest)`: validates (errors block), pre-builds templates, `BuilderServices.Waves.SetPending`, sets Classic mode, sets `MatchSaveLoadHandler.OverwriteCurrentSave = true`, and calls `SceneTransitionManager.TransitionFromNullToLevel(host)`.
- Ends on any non-host single scene load. Marks `GameplayImpact` on host load. Shows a `Notify` banner.
- Isolation patches: host `LevelData` swapped for `Current.Scores`, `SaveGame` skipped, `SaveRun` in-memory only, achievements skipped, perk level and XP restored at session end.
- Applies `BuilderServices.Overlay` for level sessions.

**To build:**

1. A "Custom" entry in level select. The doc's recommendation is a UI frame (from the pre-level menu or a single overworld interactor) listing playable packs, wave sets and levels from `BuilderServices.Packs.Packs`, with entries that have `HasErrors` greyed out and their issues shown. It launches through `Launch`, and should use the loadout and perk selection of the host's pre-level menu if feasible. Today `Launch` keeps whatever perks are equipped.
2. Results: on `TransitionFromGameplayToEndScreen` during a session, record `{packId, waveSetId/levelId, score, beaten, date, perks}` into `BepInEx/PalaceBuilder/scores.json` (best per design), and make the end screen return to level select. Check which end-screen XP, quest and crown paths still run: XP is restored by the interim code, but verify crowns and quests via `LevelInfo.QuestsComplete`.
3. Restart and retry day: verify that `RestartCurrentLevel` keeps the session (it should: same host) and that retry-day uses the in-memory match save.
4. Decide whether perks should be allowed. They are now, and the game applies them to custom waves.

**Interfaces consumed:** `IPackRepository`, `IWaveInjector.SetPending`, `IUnitFactory.Resolve`, `ILevelOverlay.Apply`.

### WP4a: editor UI

**Goal:** an in-game editor for packs, units and wave sets, with no JSON hand-editing needed.

**Owns:** `Features/Builder/Editor/` (new). Use the shared UI kit (`SettingsPaneManager` patterns, `Features/UI`) for native-looking panels.

**Use:**

- `BuilderServices.Packs`: list, `Save` (user packs only; built-in and plugin packs are `ReadOnly`, so offer "Save a copy" via `InstallNew`), `Delete` (moves to trash), `Validate`.
- `EditHistory` (`Model/EditHistory.cs`): call `Record("label")` before each edit; `Undo`/`Redo` return a new `DesignPack` to rebind to.
- `BuilderServices.Catalog`: base-prefab picker (`Units` filtered by `Side`, with `Icon`, `DisplayName` and stats as defaults for the property panel), weapon picker, host picker, spawn line picker (`CatalogHost.SpawnLines` with `Flying`, `SmallGround` and `BigGround` flags; warn when a flying unit uses a ground-only line), and "start from vanilla waves" (`CatalogHost.VanillaWaves`, a ready `WaveSetDef`).
- `Validator` issues carry a JSON path (`units[0].stats.maxHp`): map them to the field to highlight it.
- `Ids.FromName(name)` for ids, and `Validator.IsTag` for tag pickers (names are `TagManager.ETag`).
- Test button: `BuilderServices.Packs.Save(pack)` then `BuilderServices.Sessions.Launch(new SessionRequest { Pack = pack, WaveSetId = id }, out err)`.
- Export and import buttons: call `PalaceMod.Builder.Sharing.SharingUI` (WP6): `Open()` (the "Share designs" frame: paste a code, import folder, export a pack), `OpenPack(entry)` (one pack's export actions), `CopyCode(pack)` and `ImportFromClipboard()`. They notify the player themselves. For custom flows use `BuilderServices.Share` and `BuilderServices.Archives` directly.
- Live preview model: instantiate `IUnitFactory.Resolve(pack, unitId)` with `Object.Instantiate` under a preview camera, and destroy it after. Never activate or modify the returned template itself.

**Not stubbed:** everything it needs exists except share codes (stub refuses) and icons (stub returns null).

### WP5: asset import

**Goal:** packs bring their own icons, sounds and (later) meshes.

**Owns:** `Features/Builder/Assets/` (new). Install with `BuilderServices.Assets = new AssetProvider();` in your manager's `Awake`.

**Contract:** `IAssetProvider.GetSprite(PackEntry pack, string assetRef)` resolves `"asset:<key>"` to `pack.Pack.Assets[key]` and returns null on any failure (callers keep base visuals). The same goes for `GetAudio` and `GetMesh` (mesh plus materials).

- Local: `Path.Combine(pack.Folder, def.Path)`. `pack.Folder` is null for built-in packs, so built-in assets would need embedded resources if ever wanted. The validator already rejects rooted and `..` paths.
- Remote: `def.Url` plus `def.Sha256`, cached at `BuilderServices.Packs.Root/cache/<sha256>`; verify the hash before use. Downloads are async. `GetSprite` is synchronous, so prefetch on pack load or session launch (subscribe to `BuilderServices.Packs.Changed` and `BuilderServices.Sessions.Started`) and return null until cached.
- Cache loaded objects per `(packId, key, file mtime)`.

**Hooks already wired:** `UnitFactory` calls `GetSprite(entry, visuals.icon)` and sets `ScreenMarkerIcon.sprite` (plus the arachnophobia sprites). `visuals.mesh` is validated but `UnitFactory` only notes "not supported yet". To wire it, add a branch in `UnitFactory.ApplyVisuals` (WP2's file: a small additive change is fine) that swaps `MeshFilter.sharedMesh` and materials, keeping the material named `Enemy` for elite swaps. Sounds: hook `UnitAudio` / `ThronefallAudioManager`; add a model field (`visuals.sounds`?) with no version bump (it's an additive optional field).

**Spike results that matter here:** see "Tint and skin" below (how unit meshes are coloured).

### WP6: sharing (as built)

**Goal:** trade designs as copy/paste codes and files, with no server.

**Owns:** `Features/Builder/Sharing/`: `ShareCodec.cs`, `PackArchive.cs` (no Unity references, unit-tested), `BuilderSharingManager.cs` (installs `BuilderServices.Share` and `BuilderServices.Archives`, the import folder watcher, the `builder-share` dev command), `SharingUI.cs` (the frames). Tests: `tests/PalaceMod.Tests` (`dotnet run --project tests/PalaceMod.Tests`; needs `dev.py setup` for `lib/`). It follows `Builder.Enabled` (`Feature<BuilderManager>.On`); its one setting is `Builder.Import folder`.

**Share code:** `TFB1:` + base64url(raw deflate(`PackSerializer.WriteCompact(pack)`) ++ CRC-32 of the JSON, 4 bytes big-endian). The digit is the code format version (a newer one is refused with "update PalaceMod"); the pack schema version is inside the JSON and migrated by `PackSerializer`. Assets keep `path`, `url` and `sha256`, never bytes, so a receiver without the file falls back to base visuals. The sample pack (2,500 bytes of JSON) is a 1,051-character code; `SharingUI.CopyCode` suggests a `.tfpack` above 2,000.

Decoding treats input as hostile and never throws: it finds the `TFB<n>:` prefix anywhere in the text (so a whole chat message can be pasted), skips whitespace and line breaks, stops at the first non-base64 character (closing quotes or backticks), and accepts the standard base64 alphabet too. It caps the code at 512K characters and the inflated JSON at 2 MB (counted while inflating), checks the CRC, requires strict UTF-8, parses with a JSON depth cap of 32, then migrates and reads. Null lists become empty. Any failure is one player-facing error issue. Decode doesn't validate against the catalog; installing does (`InstallNew` → `Refresh`), and a pack with errors still installs so the editor can fix it.

**.tfpack:** a zip of `tfpack.json` (`{"format":"tfpack","version":1,"pack":"<id>"}`, the archive layout version), `pack.json` and `assets/...`. Export writes to `PalaceBuilder/exports/<id>.tfpack` (never overwriting: `-2`, `-3`, ...) and only includes assets with media extensions (`PackArchive.AssetExtensions`: png, jpg, wav, ogg, mp3, obj, mtl, gltf, glb, bin, json, txt; WP5: add any new type there). Import:

- Caps: 64 MB archive, 1,000 entries, 32 MB per entry, 128 MB total, 2 MB `pack.json`. Sizes are counted while inflating, never taken from the headers, and a partial file is deleted.
- Zip-slip: every entry name is checked first, and one bad name rejects the whole archive: no rooted paths, drive letters or `:`, `\`, `..`, `.` or empty segments, trailing dots or spaces, or control characters. Each target path is also checked to resolve inside the staging folder.
- Layout: exactly one `pack.json` anywhere in the archive; its folder is the pack root. So a zip of a pack folder and a Thunderstore package (`PalaceBuilder/packs/<id>/pack.json`) import too. Only `assets/` files with known extensions are unpacked; anything else is skipped with a warning.
- Migration hooks: `PackArchive.Steps[n]` upgrades an extracted staging folder from layout n to n+1, and a newer layout is refused. A missing `tfpack.json` counts as layout 1.
- It extracts to `PalaceBuilder/staging/<guid>`, then `InstallNew(pack, staging)` (never overwrites; a clashing id becomes `<id>-2`, reported as a warning), then deletes staging.

**Thunderstore:** `ExportThunderstore` writes `exports/<Name>-thunderstore.zip` with `manifest.json` (name made Thunderstore-safe, `version_number` 1.0.0, empty `dependencies`), a README with a publishing checklist, a generated 256x256 `icon.png` and `PalaceBuilder/packs/<id>/`. Mod managers install loose files to `BepInEx/plugins/<package>/`, where `PackRepository` finds the pack read-only. Creators must add PalaceMod's Thunderstore id to `dependencies` (it isn't published yet; see `PackArchive.ThunderstoreManifest`).

**Import UX:** `SharingUI.Open()` is a "Share designs" frame with two tabs. **Import** has "Paste code from clipboard", the import folder (an "Open import folder" button and a row per waiting file) and the last result. **Export** lists packs, and each opens `OpenPack(entry)`: copy share code, save `.tfpack`, save for Thunderstore, open exports folder. With `Builder.Import folder` on (the default), files dropped into `PalaceBuilder/import` (`.tfpack`, `.zip`, or `.txt` holding a code) are imported within about 2 s outside sessions. The watcher waits until a file has been unchanged for 1.5 s and isn't locked, then moves it to `import/imported/` or `import/failed/` (with `<file>.why.txt`). Every result is a `Notify`. There is no pause-menu entry of its own: WP4a's Builder menu opens it.

**Dev command** `builder-share`: `code <pack>` (copies too), `decode [code]` (clipboard by default; doesn't install), `paste` (installs the clipboard), `export <pack>`, `thunderstore <pack>`, `import <path>`, `ui [pack]`, `selftest` (codec round trip, rejections and the clipboard under the game's Mono).

### WP4b / WP4c (stage 2)

`LevelDef` and its overlay types define the data. `BuilderServices.Overlay.Apply(pack, level, issues)` is called by the session on host load (after Awake, before Start), so clone `BuildSlot`s there as Falkenpass does. See the spike results for rescan cost.

## Design notes from research

### Share codes and packs

- **Share code:** `TFB1:` + base64url(raw deflate(minified JSON)). It contains only designs, never asset bytes: assets go in as `{url, sha256}` or are left out, falling back to the base visuals. A unit or wave set is usually 1 to 3 KB of code. Copy and paste happen through `GUIUtility.systemCopyBuffer`.
- **Pack:** a `.tfpack` zip (`System.IO.Compression`) holding the manifest and assets, for files and Thunderstore (a template repo with `manifest.json`).
- **Online browser:** deferred. Cheapest options, in order: a GitHub-repo index (PRs plus a generated `index.json` the game reads); a Cloudflare Worker with R2 (anonymous upload with rate limiting, likes); an Architect-style server with accounts. Downloads always go into a new `packs/<id>` folder and never overwrite one.

### Editor UX

- **In-game, built on our existing UI kit** (`SettingsPaneManager` already generates native-looking panels). The game is keyboard- and controller-driven with a diegetic, low-UI style, so the editor should stay sparse and use native fonts and frames.
  - **MVP:** a "Builder" menu with a unit list, typed property panels (the schema drives the widgets, as Architect does), a base-prefab picker with a live preview model, and a wave timeline (nights as rows, spawns as chips).
  - **MVP actions:** "Test on <map>", export/import share code, and undo (command stack on the model).
- **Stage 2 placement mode:** a free camera (FreeCam already exists on Thunderstore), a palette of `catalog` entries, a ghost preview, snap to navmesh, gizmos for move and rotate, a property panel for the selected item, and test-play as a toggle that reloads the host and applies the overlay. Live edits during play are out of scope.
- **External web editor (optional):** a static page that reads `catalog.json` (dumped by a dev command: units with stats, spawn line names and positions per host, build slots, a top-down minimap PNG per host) and writes the same JSON. It suits balance spreadsheets, power users and browsing, and needs no game build. Hybrid is the end state: the game owns spatial editing and testing, and the web page owns bulk data and the browser.

### Asset import realism

| Asset | Method | Verdict |
|---|---|---|
| PNG icons and skins | `Texture2D.LoadImage` and `Sprite.Create` | **Robust**, MVP+1. Skins depend on how the game's meshes are textured (palette or vertex colour under FlatKit), so check that first. |
| WAV/OGG | `UnityWebRequestMultimedia.GetAudioClip("file://...")` | **Robust**. Hook into `UnitAudio` / `ThronefallAudioManager`. |
| Recolour and rescale | Per-design material clone, `transform.localScale` | **Robust**, MVP. Must update `GPUInstanced`. |
| OBJ | Pure-C# parser (Dummiesman-style); assign the game's FlatKit material plus a tint or vertex colours | **Robust for static props and simple units**. No rigs and no animation, so it suits props and rigid units ("rolling", "hovering"). |
| glTF/GLB | glTFast 5.x/6.x (2019.4+, netstandard). Its Burst, Mathematics and Collections dependencies already ship with the game, but versions must match. Its shaders are **not** in the build, so override material generation with game materials. | **Medium risk**: needs a spike. Nobody has proven it in a BepInEx mod. Animated skinned units would also need the game's animation setup (enemies use simple transform animation), so stage 3+. |
| AssetBundles | Built in Unity 2022.3 with URP | **Robust but power-user only**. Support loading them, and document a template project. |

## MVP scope

The MVP is "custom units and enemies plus custom wave sets on existing maps, with share codes":

1. Load packs from the folder, validate them, and show errors in-game.
2. Units: a vanilla base, stats, damage modifiers, tags, name, description, tint and scale. Enemies first, then player units inside existing buildings.
3. Wave sets for any of the 10 main maps: per-night spawns referencing vanilla or custom units, spawn lines by name, gold, elites and starting gold.
4. A "Custom" entry in the level select pre-level menu: pick a wave set and play. Scores are kept separately; mid-match save and achievements are disabled.
5. An in-game builder with property panels and a wave timeline, plus share code import and export.
6. A dev command `builder dump` that writes `catalog.json` (also used by the docs and the web editor).

Out of scope for the MVP: moving buildings, new terrain, imported meshes, and an online browser.
