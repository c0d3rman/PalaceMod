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
| WP1 Data model and loader | **Landed** (PR #8). `Model/`, `Contracts/`, `PackRepository`, `EditHistory`. |
| WP2 Catalog and runtime | **Landed** (feat/builder-core). `Runtime/`, `BuilderManager`, `builder` dev command, sample pack, interim `SessionLauncher`, leaderboard guard. |
| Player troops | **Landed** (feat/builder-player-units). `Troops/`: troops in the catalog, `waveSets[].troops` replacements, `IUnitFactory.ApplyTo`. See "Player troops". |
| WP3 Session and level select | **Landed.** `Session/`: Custom runs entry in every map's pre-level menu, session lifecycle, isolation, `scores.json`, `builder-session` dev command. |
| WP4a Editor UI | **Landed** (stage 1): `Editor/`, see "Editor (WP4a, as built)". Troops section added by stage 2 (see there). |
| WP4b / WP4c Level overlay, placement editor | **Landed** (feat/builder-level-editor): `Level/`, see "Levels and the layout editor (stage 2, as built)". |
| WP5 Asset import | **Landed.** `Assets/`: PNG icons and skins, WAV/OGG sounds, OBJ meshes, url assets with sha256 cache. glTF/GLB landed in the follow-up (feat/builder-gltf, see "glTF import"). |
| Agent integration | **Landed.** `Agent/` + `agent-kit/`: opt-in Agent bridge, `agent` command, guide, skill, `palace.py` (docs/builder-ux.md, "W6"). |
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
  Session/SessionLauncher.cs           WP3    ISessionLauncher: Launch, Arm (pre-level menu), End, results
  Session/BuilderManager.Session.cs    WP3    patches: isolation, end screen, leaderboards, pre-level menu
  Session/SessionMenu.cs               WP3    "Custom runs" entry and the Custom Runs frame (UI kit)
  Session/ScoreStore.cs                WP3    scores.json: best/last run per design
  Session/SessionDevCommands.cs        WP3    `builder-session` dev command
  Assets/AssetProvider.cs              WP5    IAssetProvider: resolve, cache, downloads, background OGG decode
  Assets/Loaders.cs                    WP5    PNG/WAV/OGG/OBJ decoders with signature sniffing and limits
  Assets/AssetVisuals.cs               WP5    mesh/texture/sound swaps on templates (called from UnitFactory)
  Assets/BuilderAssetsManager.cs       WP5    installs the provider, pumps it, `builder-assets` dev command
  Assets/BuilderManager.Assets.cs      WP5    patch: UnitAudio.Start (pack sounds)
  Assets/GltfCheck.cs                  WP5    glTF/GLB validator (pure C#, unit-tested): container, JSON, bounds, refs, limits
  Assets/GltfLoader.cs                 WP5    glTFast import: download provider, placeholder materials, logger, SceneRecorder
  Assets/GltfMerge.cs                  WP5    merges a glTF scene into one mesh + one texture (atlas) for a unit body
src/PalaceMod.glTFast/                 WP5    builds Unity glTFast 6.8.0 (Apache-2.0) as PalaceMod.glTFast.dll (+ LICENSE, NOTICE)
  Troops/TroopIndex.cs                 troops where troops live: troop buildings, ids, background prefetch
  Troops/TroopManager.cs               troops applies `troops` on the host map, `builder-troops` dev command
  Troops/BuilderManager.Troops.cs      troops patch: tinted materials follow the level colour scheme
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
    "troops": [{ "unit": "royal-guard", "building": "Barracks" }],   // player units standing in for vanilla troops
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
- **`warning` is shown by the Builder, not the game.** The game never displays `Wave.warningText`; during a session WP3 shows a night's `warning` with `Notify` when that night starts.
- **`levels`** are validated for shape (ids, positions, `scene:`/`mesh:` bases, cost ranges). Names on the host (slots, spawn lines, meshes) are checked when the level loads; an unknown one is skipped with a logged warning. See "Levels and the layout editor".
- **`troops`** (on a wave set) replace vanilla player troops for that run. Each entry names a custom player unit; its `base` is the troop it replaces (`vanilla:P Knight`), and an optional `building` (the game's building name, e.g. "Barracks") limits it to that building. Vanilla refs, enemy units, bases no building trains, buildings that don't train the base, and the same troop and building twice are errors.
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

- **Catalog.** Built lazily on first use from loaded assets, from the main menu on (no scene loads). It reads every `LevelInfo.enemySpawner` prefab's waves, every `EternalTrialEnemySet` (for `difficulty`), their `enemyToSpawnOnDeath`, the God of Afterlife enemy, the player troops inside the troop buildings' Resources prefabs (see "Player troops"), their weapons, and hosts (LevelInfos with a spawner, tutorial excluded) with spawn lines read from the spawner prefab's `EnemySpawnLine` children. `CatalogHost.VanillaWaves` gives each host's vanilla waves as a `WaveSetDef`, as an editor starting point.
- **Unit templates.** `IUnitFactory.Resolve(pack, ref)` returns the vanilla prefab, or for custom units a template: `Instantiate(basePrefab, inactiveDontDestroyOnLoadRoot)`, so no Awake, Start or OnEnable runs, with overrides written to serialized fields. Weapons and tinted materials are cloned per template, so vanilla assets are never modified. Templates are cached per `<pack>/<unit>` and rebuilt when the unit's JSON changes, and are cleared when packs change (outside sessions). The game spawns them with its normal `Object.Instantiate`, so the clone initialises itself and every perk and difficulty multiplier applies.
- **Names.** A custom name or description sets the template's `ScreenMarkerIcon.locaKey` to `PalaceBuilder/<pack>/<unit>`, answered by a `TextTranslator.Translate` prefix. The night preview, the pause menu wave list and tooltips show it with no further work.
- **Template-aware spawning** (a transpiler on `Spawn.Update`, `EnemySpawner.StartSpawning` and `EnemySpawner.GetWaveInfo`): `scene.isLoaded` becomes "loaded and not the template scene", and `GameObject.GetComponentInChildren<T>()` becomes `GetComponentInChildren<T>(true)` for templates only. Vanilla behaviour is unchanged. If a game update removes the `isLoaded` check, the transpiler logs an error.
- **Wave injection.** A prefix on `EnemySpawner.Start` replaces `waves` and `goldBalanceAtStart` when a wave set is pending, the scene is its host, and the mode is Classic. Spawn lines are resolved by GameObject name among the spawner's `EnemySpawnLine` children; unresolvable spawns are skipped with a logged issue. The game's own Start logic then applies Elite God, Rat God, Loan, Royal Mint and Cheese God to the custom waves.

### Sessions (WP3)

A session is one custom run of a design (a pack's wave set or level) on its host map. `SessionLauncher` has two ways in:

- **`Launch(request)`** (editor "Test", `builder play`): validates (errors block), pre-builds every template, queues the wave set, sets Classic mode, points the match-save context at the host (`MatchSaveLoadHandler` `currentMap` = host, no current save, `OverwriteCurrentSave`), and calls `TransitionFromNullToLevel(host)`. It accepts any in-memory pack (it needn't be saved) and works from the main menu, the overworld or the in-match pause menu. Launching while a session runs replaces it.
- **The pre-level menu** (players): walking onto a map shows the game's pre-level menu with an extra **Custom runs** entry under "Show Leaderboard" (the panel grows by one row). It opens the **Custom Runs** frame (UI kit `ModFrame`): this map's designs first, then other maps', each with its custom best and a tooltip (pack, author, nights, description, best, plays and wins, or the errors that block it; packs with errors are dimmed and can't be picked). Picking one "arms" the pre-level menu (`SessionLauncher.Arm`): the title gets "Custom: <name>", the highscore box shows the design's custom best, there is no "Continue" save, and Play starts the session (a `TransitionFromLevelSelectToLevel` prefix calls `BeginArmed`, which re-reads the pack and refuses if it now has errors or another host). Picking a design for another map switches the pre-level menu to that map. "Play <map> normally" in the frame, walking onto a map, or closing the menus disarms. The game's in-map perk selection applies as usual. The entry shows on main maps, and on challenge maps only when a design targets them.

While `Current != null`:

- `GetLevelDataForScene(host)` returns the session's `LevelData` (`Current.Scores`), seeded with the design's custom best, so "new record" and the end-screen highscore compare against custom runs only. Its `beatenBest` stays the host's vanilla value (also after a win), because campaign unlocks (`Equippable.IsUnlocked`) read it.
- `SaveLoadManager.SaveGame` is skipped. `MatchSaveLoadHandler.SaveRun` only converts in memory, so retry and retry day work without writing the host's mid-match file.
- Achievements and every platform's `UploadHighscore(int, string)` are skipped. The end screen gives no XP (`FillUpXPBar`) and no "map beaten" unlock rewards (`ShowCampaignLevelBeatenRewards`). Perk level and XP are also snapshotted at launch and restored at the end, as a backstop.
- `GameplayImpact.Mark("Builder: custom content")` on host load, plus a short notice. A night's `warning` text shows when it starts.
- On `TransitionFromGameplayToEndScreen` (Classic), the result goes to `ScoreStore`: `BepInEx/PalaceBuilder/scores.json`, one record per design key (`<pack>/<waveSet>` or `<pack>/level:<level>`) with `plays`, `wins`, `best` and `last` (`score`, `beaten`, `dateUtc`, `perks`, `nights` reached, and `assists`: other GameplayImpact reasons). A notice shows the score and whether it's a new custom best.
- Restart and retry keep the session (same host). The session ends when any other scene loads (the game loads scenes additively, so only `_UI` is ignored). `End()` with the host still loaded first takes the player to level select and keeps isolation until the map has unloaded. At the end the match-save context is left on "overwrite" and GameplayImpact's level is cleared, so the next vanilla run on the host starts clean.
- Perks are allowed: they're the game's own difficulty and score system and apply to custom waves; the result records them.

**Studio sessions** (`docs/builder-ux.md`, "Play mode"): `LaunchStudio(request, StudioSessionKind.Edit | Play)` starts a
session marked as the Studio's (`StudioKind`), or, on the same host, swaps the running one's pack, level and wave set in
place and reloads the host, so edit and play are one session that never ends in between. Studio sessions show no
"Custom run" notice and record nothing in `ScoreStore`; the isolation above applies as for any session. Test play
itself (the swap, sandbox, unlimited gold, music, the way back) is `Features/Builder/Studio/Play/` (`builder-play` dev
command).

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

`python3 scripts/dev.py cmd "builder-session <sub>"` (WP3):

| Sub | Does |
|---|---|
| `prelevel <map>` | opens a map's pre-level menu (in level select) as if the king walked onto it |
| `menu` | opens the Custom Runs frame |
| `arm <pack> <waveSet>` / `disarm` | arms or disarms a design for the pre-level menu |
| `play` | presses the pre-level menu's Play |
| `finish win\|lose` | ends the match now (end screen) |
| `scores` | prints `scores.json` |
| `state` | session, armed design, scene, frame, host LevelData, perk level and XP, match-save flag, impact, loadout |

## Spike results

Run on 2026-11-02 with `builder spike ...` and `builder play palacemod.sample` in a dev launch (pool instance, native arm64 loader).

| Spike | Result | Consequence |
|---|---|---|
| **Spawning cloned enemy templates** | **Works.** Templates are made under an inactive DontDestroyOnLoad root. The game's `Spawn.Update` instantiates them once its `scene.isLoaded` check is transpiled to exclude the template scene, and `GetComponentInChildren` to include inactive objects for templates. `builder live` during night 1 showed `8x E Racer [palacemod.sample/goblin-runner](Clone): name="Goblin Runner" maxHp=7 speed=9 ... scale=0.8` and `1x E Melee [palacemod.sample/iron-brute](Clone): maxHp=120 speed=2.5 cooldown=1.6 damage=[any:+6x1,Building:+0x3] scale=1.5 tags=[...,ArmoredAgainstRanged]`. They path to the castle and attack. The night preview in the pause menu lists them by line ("Forest: 8", "Main Bridge: 1") with the gold total. | The `IUnitFactory` contract stands as designed. |
| **Cloning weapons before editing** | **Works.** `spike weapon`: the template's weapon is `E Melee#-988` with damage `+6x1,+0x3`; the vanilla prefab's is `E Melee#32002` with damage `+2.5x1`; `shared=False`. | Vanilla enemies are never affected by designs. |
| **Wave injection before level start** | **Works.** A prefix on `EnemySpawner.Start` injected 3 nights and 68 enemies with starting gold 20. Perk modifiers then ran as normal. After leaving, a plain `level Nordfels` had 13 vanilla waves, starting gold 8 and no gameplay impact. | `IWaveInjector` stands. Spawn lines are resolved by name; names come from the spawner prefab, so the catalog needs no scene load. |
| **Runtime pathfinding rescan** | `AstarPath.active.Scan()` of all 4 graphs on Nordfels took **954 ms** the first time and **426 ms** the second (native). | Fine once at level start (behind the loading clouds) for WP4b overlays. Too slow per edit: use `NavmeshCut` or `GraphUpdateObject` for live placement in WP4c. |
| **How unit meshes are coloured** (tint/skin) | Enemy bodies use the shared material `Enemy`: shader `FlatKit/Stylized Surface`, `_BaseColor` (1, 0.90, 0.86), shadow colour `_ColorDim` (0.69, 0, 0.16), **no texture** (`_BaseMap` none), and `GPUInstanced` renderers. Meshes aren't readable, so vertex colours are unknown. | Tint = a per-template material clone with `_BaseColor` multiplied (works: goblins render green). The clone keeps the name `Enemy` so the elite swap still applies (elites then lose the tint). Skins need a texture-capable FlatKit setup (WP5: set `_BaseMap` on the clone) rather than palette tricks. |
| **Session isolation** | On the host: `GameplayImpact` "Builder: custom content". `SaveGame` was skipped ("skipped writing the save file during a custom run"). Leaving to level select ended the session; perk level and XP were unchanged. | WP3 builds on this. Codex found that `builder end` mid-run and launches from the main menu needed work; WP3 has fixed both on its branch. |
| **Catalog** | 39 enemies, 37 weapons and 36 hosts (10 main maps plus mini modes), built in 89 ms at the main menu. **0 player units**: player unit prefabs aren't loaded at the menu (they live inside building prefabs). | Fixed by the troops package: 14 troops from the troop buildings' Resources prefabs (see "Player troops"). |

Cloning a `BuildSlot` before Start: **works** (stage 2): an inactive clone gets its GUID, costs and activator, then is reparented active so the game's own Awake/Start register it. Rescan after a layout: 120-220 ms on Nordfels (warm). glTFast (WP5) is under "WP5: asset import", "glTF import".

## Interfaces

All are in `Features/Builder/Contracts/Contracts.cs` and reached through `BuilderServices` (never through a concrete class). They are main-thread only. A package installs its implementation in its own manager's `Awake` (any `ManagerOrder` above -30, the default 0 is fine) with `BuilderServices.X = new MyImpl();`. Until then a stub answers, so callers never null-check.

| Service | Interface | Owner | Implementation now |
|---|---|---|---|
| `BuilderServices.Packs` | `IPackRepository`: `Root`, `Packs`, `Find`, `Refresh`, `Changed`, `Save`, `InstallNew(pack, sourceFolder)`, `Delete`, `Validate` | WP1 | `PackRepository` |
| `BuilderServices.Catalog` | `ICatalog : IValidationContext`: `Units`, `Weapons`, `Hosts`, `FindUnit/Weapon/Host`, `ToJson` | WP2 | `Catalog` |
| `BuilderServices.Units` | `IUnitFactory`: `Resolve(pack, unitRef, out error)`, `IsTemplate`, `Clear`, `ApplyTo(instance, pack, unitRef, out error)` (added by troops) | WP2 | `UnitFactory` |
| `BuilderServices.Waves` | `IWaveInjector`: `SetPending(pack, waveSet)`, `Pending`, `BuildWaves(...)` | WP2 | `WaveInjector` |
| `BuilderServices.Sessions` | `ISessionLauncher`: `Current`, `Launch(SessionRequest, out error)`, `End`, `Started`, `Ended` | WP3 | `SessionLauncher` |
| `BuilderServices.Overlay` | `ILevelOverlay`: `Apply(pack, level, issues)` | WP4b | `LevelOverlay` |
| `BuilderServices.Assets` | `IAssetProvider`: `GetSprite`, `GetAudio`, `GetMesh(PackEntry, assetRef)`; added by WP5: `GetTexture`, `GetState(pack, ref, use, out message)` (`AssetState` Missing/Loading/Ready/Failed, `AssetUse` constants), `Prefetch(pack)`. `GetMesh` returns no materials for OBJ or glTF (units keep their own material); a glTF mesh's colours come as a texture from `AssetProvider.GetMeshTexture` (internal to `Assets/`, applied by `AssetVisuals`) | WP5 | `AssetProvider` |
| `BuilderServices.Share` | `IShareCodec`: `Encode(pack)`, `Decode(code, issues)` | WP6 | `ShareCodec` |
| `BuilderServices.Archives` | `IPackArchive`: `ExportFolder`, `Export(entry, path)`, `Import(path, issues)`, `ExportThunderstore(entry, path)` (**added by WP6**, additive; stub `NullPackArchive`) | WP6 | `PackArchive` |

Interfaces are frozen. If a package needs a change, add a member (don't change existing signatures), keep the stub compiling, and note it in the PR and in this table.

Shared data types (also in `Contracts.cs`): `PackEntry {Pack, Folder, Source, ReadOnly, Issues, HasErrors, Id}`, `PackSource {User, Plugin, BuiltIn}`, `CatalogUnit`, `CatalogWeapon`, `CatalogHost`, `CatalogSpawnLine`, `SessionRequest {Pack, WaveSetId, LevelId}`, `BuilderSessionInfo {Pack, WaveSet, Level, HostScene, StartedUtc, Scores}`. The model is in `Model/`: `DesignPack`, `UnitDef` (+ `VisualsDef`, `StatsDef`, `WeaponDef`, `DamageModDef`, `TagEditDef`, `BehaviourDef`), `WaveSetDef` (+ `NightDef`, `SpawnDef`, `TroopDef`), `LevelDef` (+ overlay types), `ContentRef`, `Ids`, `Validator`, `ValidationIssue`, `PackSerializer`, `Migrations`, `EditHistory`.

### Player troops (as built)

**Where troops live.** Troops aren't standalone prefabs. Each building that trains them carries every troop it can field as inactive children, one set per upgrade choice, and `UpgradeBranch.objectsToActivate` switches a set on when the player picks it. `UnitRespawnerForBuildings` revives knocked-out ones. Level scenes hold instances of these building prefabs, and the building prefabs are also in Resources (`archer hut`, `swordsmen hut`, `heros quarter variant`; a UnityPy scan found no troop anywhere else, apart from `P Scarecrow`, which doesn't move or respawn). A troop's id is its GameObject name without Unity's ` (n)` suffix.

| Building (`buildingName`) | Troops |
|---|---|
| Archery Range | P Archer, P Longbows, P Crossbows, P Hunters, P Fire Archer |
| Barracks | P Melee, P Knight, P Speer, P Morningstar, P Berserk |
| Hero's Quarter | P Melee, P Golem, P Support Mage, P Firewing, P Lizzard Rider |

The level-1 choices in this game version are Longbow, Crossbow, Hunters, Fire Archers (Archery Range) and Knights, Spearmen, Flails, Berserks (Barracks). `P Archer` and `P Melee` are in the hierarchy but no main-map choice switches them on, so designs should replace the others.

**Catalog.** `TroopIndex.Prefetch()` loads the three buildings with `Resources.LoadAsync`, because a synchronous load takes about 2 s. `BuilderManager` waits for it before it first validates packs (it finishes during boot). If anything asks the catalog earlier, the load finishes synchronously. Troops then cost about 140 ms of the catalog build. `CatalogUnit.Buildings` lists the buildings that train each troop, and `catalog.json` has it as `buildings`. `Catalog` also implements `ITroopValidationContext.TroopBuildings(id)` (in `Model/Validation.cs`) for the validator.

**Runtime.** Nothing is spawned. When a session's host map loads (after Awake, before any Start), `TroopManager` finds every troop in the scene, picks the matching `troops` entry (a building-specific entry beats a general one) and calls `IUnitFactory.ApplyTo`. That writes the design's stats, weapon, tags, name, icon, scale, tint and WP5 visuals onto the scene object, cloning each weapon and material once per design. The game then starts the troops normally, so perks, blacksmith upgrades, respawning, commands and the pause menu's unit list all see the custom values. A restart or retry reloads the scene and applies the overrides again. Vanilla runs are never touched.

- Tags on an enabled troop go through `TaggedObject.AddTag/RemoveTag`, so `TagManager` stays in sync.
- A perk that swaps a troop's weapon (`PerkWeaponModifyer`, e.g. fire arrows) still replaces a design's weapon on Start.
- Swapping one troop type for another (knights from an Archery Range) isn't supported: overrides are applied in place, so the design's base must be the troop it replaces.

**Tint fix (affects enemies too).** `ColorAndLightManager` writes the level's colour scheme into the shared unit materials (`Enemy`, `Ally`, `Ally Ranged`, ...) in its `Start` and re-tints their outlines with the daylight. A tint clone is a snapshot, so a troop cloned on scene load came out black (`Ally Ranged` is all zeros until the scheme is applied), and enemy templates built at the menu kept the asset's default colours and outlines. `UnitFactory` now records each tint clone with its source, and a postfix on `ApplyColorScheme` and `AdjustAllOutlineColorsBasedOnLight` recopies the source and re-applies the tint. `TroopManager` also applies the level's scheme just before it clones, so WP5 skin clones start from the level's colours. WP5 skin clones aren't resynced afterwards, so their outlines don't follow the daylight. Fixing that would mean registering them the same way.

**Editor:** the Units tab has "New player unit" (pick a troop as the base); a wave set page has a **Troops** section (pick one of the pack's player units and, optionally, the building from `CatalogUnit.Buildings` of its base); a player unit's Test plays the first map's own nights with the buildings training it.

**Dev command** `builder-troops`:

| Sub | Does |
|---|---|
| `list` | catalog troops with stats and the buildings that train them |
| `scene` | every troop on the loaded map by type and building (active count), and which were overridden |
| `live` | active troops grouped by name with their actual stats, weapon instance and material colours |
| `build <building>[:<choice>]` | builds or upgrades the lowest-level matching building, picking the named upgrade choice (dev only) |

### File ownership rules

- Each package owns its folder under `Features/Builder/` and may add `BuilderManager.<Package>.cs` there for Harmony patches. Patch classes must be nested in `BuilderManager`, which is why it's partial.
- Don't edit another package's folder. `BuilderManager.cs`, `Contracts/` and `Model/` are WP1/WP2's. Additive changes there (a new interface member, a new model field with a migration if needed) are fine, but call them out in the PR.
- New settings go in the `Builder` section (max 6 more rows; split into pages beyond that) through `Plugin.Config.Bind("Builder", ...)` in your manager's `Awake`.
- New `builder` subcommands: register a separate dev command (`builder-ui`, `builder-share`, ...) rather than editing the `builder` switch.

## Work package briefs

### WP3: session and level-select integration

**Goal:** players start custom runs from the game's own UI, and results are kept apart from vanilla progress.

**Owns:** `Features/Builder/Session/` (`SessionLauncher.cs`, `BuilderManager.Session.cs`), plus any new files there. Replace or extend `SessionLauncher` freely; keep it implementing `ISessionLauncher`.

**Built** (see "Sessions (WP3)" above): the Custom runs entry and frame, armed pre-level menu, direct `Launch`, isolation, end-screen and leaderboard guards, `scores.json`, night warnings, `builder-session` dev command. Verified in game: arm from the menu, play, custom waves spawn, victory records the result with no XP or save written, Try Again keeps the session, `builder end` leaves the map first, vanilla runs are unaffected, and turning the Builder off hides the entry.

**Hooks for other packages:** the editor sets `SessionMenu.EditorAvailable`, `SessionMenu.OpenEditor` and `SessionMenu.OpenPackInEditor` to get "Create your own" and per-pack "Edit" rows; `SessionMenu.Open()` opens the list from anywhere (a pick then launches directly). `ScoreStore.Get(ScoreStore.Key(pack, waveSet, level))` gives a design's record.

**Open:** level-overlay sessions (`LevelId`) are verified with stage 2 (Custom Runs lists levels and applies their layout). Retry day was not exercised in game (it uses the in-memory match save, unchanged from the interim). A changed design keeps its old best (records aren't versioned by content).

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

**Status:** landed. Owns `Features/Builder/Assets/`. `BuilderAssetsManager` (infrastructure, `[ManagerOrder(-20)]`) installs `AssetProvider` and pumps it every frame.

**Pack fields** (all optional, additive, no version bump):

```jsonc
"assets": { "shroom": { "path": "assets/shroom.obj" }, "skin": { "path": "assets/skin.png" },
            "knight": { "path": "assets/knight/knight.glb" },          // .glb or .gltf (adjacent files allowed)
            "hit": { "url": "https://.../hit.ogg", "sha256": "<64 hex>" } },
"visuals": { "icon": "asset:icon", "mesh": "asset:shroom", "meshScale": null, "meshRotY": 45,
             "texture": "asset:skin", "sounds": { "attack": "asset:hit", "hurt": "asset:...", "death": "asset:..." } }
```

**How it works:**

- **Resolution.** `path` is relative to the pack folder and must resolve inside it (checked again at load, beyond the validator's `..` check). `url` assets download once (http/https, 30 s timeout, 24 MB cap) into `cache/<sha256>` and are used only if the hash matches; a mismatch is permanent, a network failure retries after a minute. Downloads and OGG decodes are async: the getters return null until ready, and finishing one clears templates (outside sessions) so they rebuild with it. Prefetch runs on `Packs.Changed` and when a session starts. Setting `Builder.Download pack assets` (default on) turns downloads off.
- **Decoding.** The file type comes from its first bytes, not its extension. PNG ≤ 8 MB and ≤ 2048 px a side (read from the header before decoding). WAV: PCM 8/16/24/32-bit or float, 1 to 8 channels, ≤ 60 s, parsed by us. OGG: Vorbis only, ≤ 8 MB, every page's CRC verified before Unity's decoder sees it (Unity logs an FMOD error on damaged files otherwise). OBJ ≤ 16 MB, ≤ 100k vertices and 150k triangles: positions, UVs, normals, any polygon, negative indices and `v x y z r g b` vertex colours; `mtllib` is ignored. Malformed files produce a player-facing message (`GetState`) and a warning in the log, never an exception into the game. Loaded objects are cached by file path, size, mtime and use.
- **Meshes.** Inside-out meshes are flipped automatically (signed volume), and normals that oppose the faces are flipped: the cel shader renders an inside-out mesh solid black. OBJ vertex colours are read but not shown: the `Enemy` material doesn't enable FlatKit's vertex-colour mode, so use a texture for colour. In the template, the biggest renderer with a body material keeps its renderer, `GPUInstanced` and material and gets the mesh, baked into its local space (so it bobs with the walk animation). It's scaled to fit inside the base model's box (or `meshScale`), turned by `meshRotY`, and stands where the model stood. The other parts get a one-triangle invisible mesh rather than being disabled, because `GPUInstanced` draws them even when the renderer is off. So tint, elite material and hit flashes still work.
- **Texture.** Clones the body material (keeping its name, for the elite swap), sets `_BaseMap` (the shader multiplies it, `_TEXTUREBLENDINGMODE_MULTIPLY`) and resets `_BaseColor` to white. Vanilla meshes have UVs, but they're laid out for flat colour, so a texture is mainly for imported meshes. Elites switch to the elite material and lose the texture, as with tints.
- **Sounds.** A `BuilderUnitSounds` marker on the template (pack id + refs) is copied to every spawned clone; a postfix on `UnitAudio.Start` replaces `soundOnAttack`, `soundOnDmg` and `soundOnDeath` with the pack clips. Footsteps keep the base unit's.
- **glTF/GLB.** See "glTF import" below: validated by `GltfCheck`, imported asynchronously by glTFast, merged into one mesh plus one colour texture, then swapped in exactly like an OBJ mesh.
- **Not supported:** animation (skinned glTF meshes show their rest pose and bob like any mesh), multi-material OBJ (one submesh, one material), MTL colours, assets in built-in packs (no folder).

**Dev command** `builder-assets`: `check <pack>` (loads every asset for each use and prints its state), `status` (cache), `inspect <vanilla unit>` (hierarchy, meshes, materials and shader keywords), `show <pack> <unit>...` (spawns base, custom and elite copies in front of the player), `sounds` (clips that live custom units will play), `gltf <file or folder>` / `gltf result` (validates and imports every .glb/.gltf under a path one after another, outside any pack, and reports each outcome with timings; for robustness runs).

### glTF import (as built)

Shipped by feat/builder-gltf after the WP5 spike (which proved glTFast 6.8 runs in the game once its Newtonsoft parser is built in; `JsonUtility` can't fill types from a late-loaded assembly). Static glTF 2.0 models, `.glb` or `.gltf`, through the same `asset:` refs, provider, validation and limits as OBJ.

- **Library.** `src/PalaceMod.glTFast/PalaceMod.glTFast.csproj` downloads the `com.unity.cloud.gltfast` 6.8.0 package from Unity's registry into `lib/` (sha256-pinned; nothing third-party is committed) and compiles its unmodified runtime sources into **PalaceMod.glTFast.dll** (about 220 KB). The assembly rename means another mod's `glTFast.dll` can't clash with it. Defines: the Unity 2022.3 player set, `UNITY_IMAGECONVERSION`, `NEWTONSOFT_JSON`, `GLTFAST_REPORT`; no URP, animation, physics, web-request textures, Draco, KTX or meshopt; `DEBUG` is never defined, so glTFast's debug-only `Debug.Log` calls compile out. `PalaceMod.csproj` references it (and lists Burst, Collections and Mathematics so `setup` copies them into `lib/`), and `dev.py install` copies the DLL with `PalaceMod.glTFast.LICENSE.md` and `PalaceMod.glTFast.NOTICE.txt` into `BepInEx/plugins/PalaceMod/`. If the DLL is missing, glTF assets fail with a clear message; nothing else is affected.
- **Validation first (`GltfCheck`, pure C#, `tests/PalaceMod.Tests`).** glTFast trusts its input (its jobs read accessors through raw pointers), so every file is checked before glTFast sees it: GLB container and chunk bounds; strict UTF-8 JSON with depth 64; glTF 2.0; Draco, meshopt, KTX2/Basis and WebP refused with a "re-export without it" message; buffers from the GLB chunk, a base64 `data:` URI, or a file that must resolve inside the pack folder (no schemes, no rooted paths; a downloaded `url` model must be self-contained); buffer view, accessor and sparse bounds, sparse index values, index values against vertex counts, attribute types and per-primitive counts, morph targets; node refs, one parent per node, no loops, depth ≤ 256; skins, scenes, textures, materials; images must be PNG or JPEG ≤ 4096 px a side and three 4096² images' worth in total (sizes read from headers). Limits: the model (the .glb, or the .gltf plus its files) ≤ 24 MB, and the vertices and triangles the scene instances ≤ 100k / 150k, as for OBJ (shared vertex lists are counted once). A node listed both as a scene root and as a child is placed once.
- **Import (`GltfLoader`).** `GLTFast.Newtonsoft.GltfImport` with a download provider that serves only the validated files, placeholder materials, a `TimeBudgetPerFrameDeferAgent` (a third of a frame per frame), and an `IInstantiator` (`SceneRecorder`) that records node transforms and primitives instead of creating GameObjects. glTFast's messages go to PalaceMod's log at debug level (never Unity errors); its first error becomes the player-facing reason. `GltfImport.Dispose` then destroys everything glTFast created. The provider treats it like OGG: `GetState` says "importing", Prefetch starts it when packs change, and arrival rebuilds templates (outside sessions).
- **Merge (`GltfMerge`).** A unit body is one GPU-instanced renderer with one material (`GPUInstancingManager` draws submesh 0 only), so the scene becomes one mesh: every triangle primitive baked into model space (node transforms, mirrored nodes with their winding fixed, `EXT_mesh_gpu_instancing`, skinned meshes posed by joint world matrices x bind poses). Colour goes into one texture: a model with one textured material keeps that texture (times its base colour; tiling UVs still repeat); otherwise an atlas of texture tiles (≤ 1024 px each, 8 px edge padding, UVs wrapped into the tile per triangle where a material's UVs tile) and 8x8 swatches for base colours and vertex colours (one colour per triangle). `KHR_texture_transform` and `texCoord` are applied. Unwelding for tiling or vertex colours stays within the vertex limit, cheapest group first; the rest use an average colour. The model's own first UV set is kept in UV2: a unit that also sets `visuals.texture` is painted with that PNG through it. Metallic, roughness, normal, occlusion and emissive maps, alpha and lights are ignored (the game's cel shader has no use for them).
- **On the unit.** `AssetVisuals` swaps the mesh as for OBJ, then applies the colour texture through the same body-material clone as `visuals.texture`, so tint, elite swap, hit flashes and the walk bob behave as for OBJ. Elites lose the texture (they switch to the elite material), as with tints.
- **Verified** (pool instance, Rosetta-free native launch): 12 real CC0 models (Kenney character (skinned, external texture), fence gate (hierarchy, 3 untextured materials), trebuchet; Khronos BoomBox (2048 px PBR), ToyCar (77k vertices, 3 materials), BoxVertexColors (embedded), TriangleWithoutIndices, SimpleInstancing, TextureTransformTest (9 materials, 5 textures); Poly Haven tree stump (.gltf + .bin + JPEGs in a subfolder)) import in 2 to 600 ms; MetalRoughSpheres (1M triangles) and a Poly Haven sapling (406k vertices) are refused by the limits; Draco and KTX2 variants are refused with their message; 33 malformed files (truncation, bad magic and versions, invalid and deeply nested JSON, out-of-range accessors, views, indices and sparse indices, bad component types, mismatched counts, node loops, missing refs, path traversal and http URIs, non-image and 20000x20000 PNGs, random bytes) are each refused with a specific reason, none reaching glTFast and no exception or Unity error. A custom wave on Nordfels with 11 glTF enemies spawned, walked and fought with their models and textures.
- **Not supported:** animation (rest pose only), morph target weights (base shape), Draco/meshopt/KTX2/WebP, materials beyond base colour, alpha. A glTF asset changed during a running custom session is picked up after it ends, like other assets.
- **Changes outside `Assets/`** (additive): `Validator.MeshExtensions` gains `.glb` and `.gltf` (`Model/Validation.cs`); `PalaceMod.csproj` (project reference and three `lib/` references); `scripts/dev.py` `install` copies the glTFast DLL, license and notice; `tests/PalaceMod.Tests` compiles `GltfCheck.cs` and `Loaders.cs`, and runs a file corpus when `PALACEMOD_GLTF_CORPUS` is set.
- **UniGLTF** (MIT) was assessed from source only during the spike: more assemblies, and hard `Shader.Find` calls for texture conversion shaders that aren't in the game.

### WP6: sharing (as built)

**Goal:** trade designs as copy/paste codes and files, with no server.

**Owns:** `Features/Builder/Sharing/`: `ShareCodec.cs`, `PackArchive.cs` (no Unity references, unit-tested), `BuilderSharingManager.cs` (installs `BuilderServices.Share` and `BuilderServices.Archives`, the import folder watcher, the `builder-share` dev command), `SharingUI.cs` (the frames). Tests: `tests/PalaceMod.Tests` (`dotnet run --project tests/PalaceMod.Tests`; needs `dev.py setup` for `lib/`). It follows `Builder.Enabled` (`Feature<BuilderManager>.On`); its one setting is `Builder.Import folder`.

**Share code:** `TFB1:` + base64url(raw deflate(`PackSerializer.WriteCompact(pack)`) ++ CRC-32 of the JSON, 4 bytes big-endian). The digit is the code format version (a newer one is refused with "update PalaceMod"); the pack schema version is inside the JSON and migrated by `PackSerializer`. Assets keep `path`, `url` and `sha256`, never bytes, so a receiver without the file falls back to base visuals. The sample pack (2,500 bytes of JSON) is a 1,051-character code; `SharingUI.CopyCode` suggests a `.tfpack` above 2,000.

Gotcha for anyone using `System.IO.Compression` in the game: Unity's Mono `DeflateStream` throws `IOException` ("Corrupted data ReadInternal") on bad input, where .NET throws `InvalidDataException`. Catch both. `builder-share selftest` checks this path in-game.

Decoding treats input as hostile and never throws: it finds the `TFB<n>:` prefix anywhere in the text (so a whole chat message can be pasted), skips whitespace and line breaks, stops at the first non-base64 character (closing quotes or backticks), and accepts the standard base64 alphabet too. It caps the code at 512K characters and the inflated JSON at 2 MB (counted while inflating), checks the CRC, requires strict UTF-8, parses with a JSON depth cap of 32, then migrates and reads. Null lists become empty. Any failure is one player-facing error issue. Decode doesn't validate against the catalog; installing does (`InstallNew` → `Refresh`), and a pack with errors still installs so the editor can fix it.

**.tfpack:** a zip of `tfpack.json` (`{"format":"tfpack","version":1,"pack":"<id>"}`, the archive layout version), `pack.json` and `assets/...`. Export writes to `PalaceBuilder/exports/<id>.tfpack` (never overwriting: `-2`, `-3`, ...) and only includes assets with media extensions (`PackArchive.AssetExtensions`: png, jpg, wav, ogg, mp3, obj, mtl, gltf, glb, bin, json, txt; WP5: add any new type there). Because `InstallNew` copies only `assets/`, an `assets` entry whose `path` points elsewhere in the pack folder (e.g. `art/skin.png`) is moved to `assets/art/skin.png` in the archive and its path is rewritten. Export also checks the finished file against the import limits, so anything it writes imports again. Import:

- Caps: 64 MB archive, 1,000 entries, 32 MB per entry, 128 MB total, 2 MB `pack.json`. Sizes are counted while inflating, never taken from the headers, and a partial file is deleted.
- Zip-slip: every entry name is checked first, and one bad name rejects the whole archive: no rooted paths, drive letters or `:`, `\`, `..`, `.` or empty segments, trailing dots or spaces, or control characters. Each target path is also checked to resolve inside the staging folder.
- Layout: exactly one `pack.json` anywhere in the archive; its folder is the pack root. So a zip of a pack folder and a Thunderstore package (`PalaceBuilder/packs/<id>/pack.json`) import too. Only `assets/` files with known extensions are unpacked; anything else is skipped with a warning.
- Migration hooks: `PackArchive.Steps[n]` upgrades an extracted staging folder from layout n to n+1, and a newer layout is refused. A missing `tfpack.json` counts as layout 1.
- It extracts to `PalaceBuilder/staging/<guid>`, then `InstallNew(pack, staging)` (never overwrites; a clashing id becomes `<id>-2`, reported as a warning), then deletes staging.

**Thunderstore:** `ExportThunderstore` writes `exports/<Name>-thunderstore.zip` with `manifest.json` (name made Thunderstore-safe, `version_number` 1.0.0, empty `dependencies`), a README with a publishing checklist, a generated 256x256 `icon.png` and `PalaceBuilder/packs/<id>/`. Mod managers install loose files to `BepInEx/plugins/<package>/`, where `PackRepository` finds the pack read-only. Creators must add PalaceMod's Thunderstore id to `dependencies` (it isn't published yet; see `PackArchive.ThunderstoreManifest`).

**Import UX:** `SharingUI.Open()` is a "Share designs" frame with two tabs. **Import** has "Paste code from clipboard", the import folder (an "Open import folder" button and a row per waiting file) and the last result. **Export** lists packs, and each opens `OpenPack(entry)`: copy share code, save `.tfpack`, save for Thunderstore, open exports folder. With `Builder.Import folder` on (the default), files dropped into `PalaceBuilder/import` (`.tfpack`, `.zip`, or `.txt` holding a code) are imported within about 2 s (paused during a custom session and while the Builder is off). The watcher waits until a file has been unchanged for 1.5 s and isn't locked, then moves it to `import/imported/` or `import/failed/` (with `<file>.why.txt`). Every result is a `Notify`. There is no pause-menu entry of its own: WP4a's Builder menu opens it.

**Dev command** `builder-share`: `code <pack>` (copies too), `decode [code]` (clipboard by default; doesn't install), `paste` (installs the clipboard), `export <pack>`, `thunderstore <pack>`, `import <path>`, `ui [pack]`, `selftest` (codec round trip, rejections and the clipboard under the game's Mono).

### Editor (WP4a, as built)

Open it from **Builder** in the title menu or the in-match pause menu, from WP3's custom-runs menu ("Create your own", "Edit"), or with `builder-ui open`. Files in `Features/Builder/Editor/`:

| File | Does |
|---|---|
| `BuilderEditorManager.cs` | menu entries, undo/redo keys (Cmd+Z, Cmd+Shift+Z, Ctrl+Z/Ctrl+Y), first-use hint, `builder-ui` dev command, sets WP3's `SessionMenu` hooks |
| `BuilderEditor.cs` | public entry points `Open()`, `OpenPack(id)`, `IsAvailable`; drafts; save, undo, test-play, share code, problem navigation |
| `PackDraft.cs` | the working copy of one pack: `EditHistory`, dirty state, validation (`Packs.Validate` plus editor-only warnings), save / save a copy / id rename |
| `EditorPage.cs` | base screen: fields bound to JSON paths, so issues mark the field and its tooltip; `Picker` (full-screen pick-one / toggle-many list) |
| `PackPages.cs` | pack list; pack screen with Units, Wave Sets, Pack (details and actions) and Problems tabs |
| `UnitPage.cs` | unit property panel: identity and base prefab, stats (generated from `StatsDef` by reflection, base values from `CatalogUnit`), weapon and damage modifier lists, tags, look (tint, size, icon), behaviours, test, duplicate, delete |
| `WavePage.cs` | wave set timeline: map, starting gold, nights as rows with their groups as chips; applying a night or chip opens its fields underneath |
| `EditorCatalog.cs` | never-throwing catalog lookups, and `Schema` (labels, help, ranges and steps for every numeric field) |

Behaviour worth knowing:

- **Drafts.** Opening a pack copies it into a `PackDraft` kept until the game quits, so closing the editor never loses work; the pack list shows "unsaved". Built-in and plugin packs are editable and saved as a new user pack (`InstallNew`). Changing a saved pack's id saves the new folder, then moves the old one to trash. A draft with no unsaved changes picks up edits made on disk.
- **Undo.** Every change goes through `PackDraft.Edit(label, change)`, which calls `EditHistory.Record` first. Held steppers coalesce into one step per field (1.5 s window).
- **Fields** (UI kit `Core/UI/ModFields.cs`, additive): `Stepper` (left/right, accelerating while held), `Choice`, `Toggle` and `TextInput`, all `ModPage.Row`s. Text entry blocks the game's menu input while typing (prefixes on `UIFrame.Update` and `UIFrameManager.Update`), and `Keybinds` treats it as a focused text field. A null override shows the base value dimmed ("100 (base)"); typing an empty value resets it.
- **Validation feedback.** Issues are matched to fields by path prefix: a red (error) or amber (warning) diamond on the label, the message in the tooltip, and a Problems tab whose rows jump to the field. Editor-only warning: a group on a spawn line that doesn't allow its unit (flying on ground lines, large on small-only lines).
- **Entry points.** "Builder" in the title menu (UI kit `Core/UI/TitleMenu.cs`, added for this: `TitleMenu.AddButton(id, label, onClick, visible)`) and the in-match pause menu; both hide while `Builder.Enabled` is off, and switching it off closes an open editor.
- **Test play.** Wave sets: save (writable packs without errors), then `Sessions.Launch` with the draft. Units: an unsaved copy of the pack with a generated two-night wave set "editor-test" on the pack's first map (or Nordfels). The editor closes before the map loads.
- **Share codes.** "Copy share code" and "Import share code" call `BuilderServices.Share` (WP6) and `Packs.InstallNew`; with WP6's stub they explain that codes aren't available yet.
- **Not built:** the live 3D preview model (planned: a preview camera rendering a stripped `Instantiate` of the template into a `RawImage`; components other than renderers removed before activation so no game script runs); editing `warning` text is stored but not yet shown in play.

### Levels and the layout editor (stage 2, as built; superseded by the Studio)

The layout editor below was replaced by the Builder Studio (`Features/Builder/Studio/`, docs/builder-ux.md):
`LayoutEditor.cs`, `LayoutMenus.cs`, `LayoutHud.cs` and the level screen are gone, the Levels tab opens a level
straight in the Studio, and `builder-level` forwards its editing subcommands to `builder-studio`. The data and
applying sections still hold.

A level is a host map with layout edits and optionally one of the pack's wave sets (null = the map's own nights). Files in `Features/Builder/Level/` (namespace `PalaceMod.Builder.Level`, plus `LevelPage` in `PalaceMod.Builder.Editor`):

| File | Does |
|---|---|
| `BuilderLevelManager.cs` | installs `BuilderServices.Overlay`, runs the one rescan after a custom level starts, drives the layout editor, registers its keys (Builder section) and the `builder-level` dev command |
| `LevelOverlay.cs` | `ILevelOverlay`: applies a level on host load (after Awake, before Start) |
| `LayoutEditor.cs` | the in-level editing mode (state, camera, pointer, keys, edits through `PackDraft.Edit`, path check, play/leave) |
| `LayoutMenus.cs`, `LayoutHud.cs`, `LayoutMarkers.cs` | its menus (UI kit frames), status panel (cloned game tooltip panel) and ground markers |
| `LayoutScene.cs` | host scene queries (slots, spawn lines, scenery meshes), inert copies, GUIDs |
| `PathCheck.cs` | rescan and spawn-line-to-castle reachability (`PathUtilities.IsPathPossible` on `Enemy Units L`/`S`) |
| `LevelPage.cs` | the pack screen's Levels tab and the level screen |

**Data** (additive to v1, no version bump): `buildSlots.edit[]` gained `pos`/`rotY` (move a host slot); `buildSlots.add[]` gained `upgrades` (cost changes); `props` is now `PropDef {id, base: "mesh:<scenery mesh>", pos, rotY, scale, solid}` (later, additive: `rotX`/`rotZ` tilt, left out at 0) (the host's scenery, or any main map's through the scenery library: builder-ux.md, "The cross-map scenery library"); `spawnLines.edit[] {ref, pos, rotY}` moves a host spawn line (pos = its new middle, rotY turns it about the middle). `spawnLines.add` is still not applied (validator warning).

**Applying (play).** In order: host slot moves and costs; clones (`base: "scene:<slot>"`, inactive under a holder, deterministic save GUID from `<pack>/<level>/<id>`, costs, optional `activator`/`activatorLevel`, then reparented active so Awake/Start run and register it; with no activator it unlocks like its base); removals (deactivated before Start, so slots they unlock never appear; the castle can't be removed or moved); spawn line moves; props (plain mesh objects, layer Obstacle when solid, which the host's recast graphs rasterize: layers Ground and Obstacle, meshes on, colliders off). Two frames later, one `AstarPath.Scan()` and a reachability check from each ground spawn line to the castle; blocked lines get a warning notice and a log line. Restart and retry reapply it. Custom Runs lists levels next to wave sets; picking one arms the pre-level menu as for wave sets.

**Layout editor.** "Edit the layout on the map" (level screen) launches a session with a trimmed copy of the pack (only the level, no wave set), so problems elsewhere in the pack don't block editing. `LevelOverlay.Apply` sees it's the edit session and applies nothing; instead every host slot but the castle is hidden and shown as an inert copy in its blueprint state (scripts, colliders and the built building stripped before activation), and the scene is redrawn from the `LevelDef` after every change (`Reconcile`), so undo/redo (Cmd/Ctrl+Z, the editor's own history) just work. The player is frozen; the camera follows a pivot through `CameraRig.overrideCameraTarget` (pan with the game's move keys or middle-drag, K/L turn, wheel zoom). Click a ring to select, drag to move (1 m grid snap and 15° turns, G toggles), . turns (, back), Delete (fn+Backspace on a Mac) removes/restores, I opens costs and options, B opens the menu: Add (build slot kinds, each new slot copying the nearest slot of that kind so it unlocks with its neighbours; the map's scenery meshes), Level (save, undo, redo, check enemy paths, snap, play, pack editor, leave) and Help. "Check enemy paths" rescans with the edits (props as obstacles) and draws each ground line's route (green, red if blocked): the only rescan in the editor. Play saves (writable pack, no errors) and launches the level for real. Markers: gold host slots (big ring = castle, locked), green added slots, pale green scenery, red spawn lines, a cross for removed.

**Sharing in the editor** (WP6 hook): the Pack tab has "Export or share" (`SharingUI.OpenPack`, for the saved pack) and the pack list has "Share designs" (`SharingUI.Open`).

**Limits.** Moving the castle, adding spawn lines, removing or moving the map's own scenery (MeshFusion-combined), changing a slot's upgrade branches, income or HP, and picking another activator in the UI (the data supports `activator`) aren't built. Slots are found by name; when a host has two of a name, only the first is editable.

`python3 scripts/dev.py cmd "builder-level <sub>"`: `new <pack> <map>` (test pack), `edit <pack> <level>`, `state`, `add slot <kind|slot> <x> <z> [yaw]`, `add prop <name|index> <x> <z> [yaw]`, `select|remove <key>`, `move <key> <x> <z>`, `rotate <key> <deg>`, `cost <key> <level> <gold|base>`, `cursor <x> <z>|off` + `tool <id>|none` + `click` (stand-ins for the mouse), `cam <x> <z> [zoom] [yaw]`, `menu`, `details`, `check`, `paths`, `play`, `leave`, `json`, `spike graphs|slots|lines|props`. Keys look like `slot:<name>`, `add:<id>`, `prop:<id>`, `line:<name>`.

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
| PNG icons and skins | `Texture2D.LoadImage` and `Sprite.Create` | **Built (WP5).** Vanilla bodies are flat-coloured (no `_BaseMap`), so skins are for imported meshes. |
| WAV/OGG | Own WAV parser; OGG via `UnityWebRequestMultimedia` after a page CRC check | **Built (WP5).** Postfix on `UnitAudio.Start`. |
| Recolour and rescale | Per-design material clone, `transform.localScale` | **Robust**, MVP. Must update `GPUInstanced`. |
| OBJ | Pure-C# parser (Dummiesman-style); assign the game's FlatKit material plus a tint or vertex colours | **Built (WP5)** for rigid units: the mesh replaces the body and bobs with its walk animation. No rigs and no animation, so it suits props and rigid units ("rolling", "hovering"). |
| glTF/GLB | glTFast 5.x/6.x (2019.4+, netstandard). Its Burst, Mathematics and Collections dependencies already ship with the game, but versions must match. Its shaders are **not** in the build, so override material generation with game materials. | **Built** (glTFast 6.8 as PalaceMod.glTFast.dll, Newtonsoft parser); see "glTF import". Static models; animated skinned units would also need the game's animation setup (enemies use simple transform animation), so stage 3+. |
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
