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

## Architecture

```text
BepInEx plugins/PalaceMod/            (code)
<persistentDataPath>/PalaceBuilder/
  packs/<packId>/pack.json            (manifest + designs)
  packs/<packId>/assets/...           (png/wav/ogg/obj/glb)
  cache/<sha256>                      (downloaded assets)
  scores.json                         (per-design results, separate from vanilla)
```

Each work package below lives in its own namespace and folder under `src/PalaceMod/Features/Builder/`.

### Design format (JSON, versioned)

```json
{
  "format": "palace-builder", "version": 1,
  "id": "c0d3rman.goblin-night", "name": "Goblin Night", "author": "c0d3rman", "gameVersion": "...",
  "requires": { "palaceMod": ">=1.4" },
  "assets": { "goblin_icon": { "path": "assets/goblin.png", "sha256": "..." } },
  "units": [{
    "id": "goblin_runner", "side": "enemy", "base": "vanilla:e racer",
    "name": "Goblin Runner", "description": "Fast, fragile, steals gold.",
    "icon": "asset:goblin_icon", "tint": "#7fbf3f", "scale": 0.8,
    "stats": { "maxHp": 6, "moveSpeed": 9, "coins": 1, "attackCooldown": 0.8 },
    "weapon": { "base": "inherit", "damage": [{ "tags": [], "add": 2 }, { "tags": ["Building"], "mult": 2 }] },
    "tags": { "add": ["FastMoving"], "remove": [] },
    "behaviours": [{ "type": "SpawnOnDeath", "unit": "vanilla:e slime" }]
  }],
  "waveSets": [{
    "id": "goblin_waves", "host": "Nordfels", "startingGold": 15,
    "nights": [{ "warning": "", "spawns": [
      { "unit": "goblin_runner", "count": 12, "interval": 0.4, "delay": 0, "line": "Spawn 1", "gold": 6, "elite": false }
    ]}]
  }],
  "levels": [{
    "id": "nordfels-remix", "host": "Nordfels", "waveSet": "goblin_waves",
    "buildSlots": { "remove": ["Gate Variant (4)"], "edit": [{ "ref": "House Variant (2)", "upgrades": [{ "cost": 2 }] }],
                    "add": [{ "id": "s1", "base": "scene:Defense Tower Variant", "pos": [10, 0, 4], "rotY": 90, "activator": "Castle Center Variant", "activatorLevel": 1 }] },
    "spawnLines": { "add": [{ "id": "north", "pos": [0, 0, 80], "rotY": 0, "width": 12, "ground": true, "flying": false }] },
    "props": []
  }]
}
```

Asset references are typed strings: `vanilla:<resources path>`, `scene:<object name in host>`, `asset:<key>`, `unit:<id>`, `<packId>/<unitId>`. Unknown fields are preserved. A missing reference produces a visible "missing content" report rather than being silently dropped. Each schema change bumps `version` and comes with a migration function.

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

## Work packages

Interfaces are C# contracts in `Features/Builder/Contracts/`, owned by WP1 and frozen at the start, so the other packages can work in parallel against stubs.

| WP | Owns | Provides | Consumes |
|---|---|---|---|
| **WP1 Data model and loader** | `Builder/Model`, `Builder/Contracts` | POCOs (`DesignPack`, `UnitDef`, `WaveSetDef`, `LevelDef`, `AssetRef`), Newtonsoft serialisation, `version` migrations, `Validator` returning `ValidationIssue[]`, `IPackRepository` (scan, load, save, watch the folder), the command/undo stack | none |
| **WP2 Catalog and runtime spawning** | `Builder/Runtime` | `ICatalog` (vanilla units, buildings and Weapons from Resources paths and `LevelProgressManager.levelInfos` / `EternalTrialEnemySet`; per-host spawn lines and build slots), `IUnitFactory.GetTemplate(UnitDef) -> GameObject` (overrides, cloned Weapon, tags, names, the `Spawn.Update` template patch, `TextTranslator` patch), `IWaveInjector.Apply(WaveSetDef)` (prefix on `EnemySpawner.Start`), `builder dump` | WP1 model, WP5 `IAssetProvider` (stub returns null, so base visuals are used) |
| **WP3 Session and level-select integration** | `Builder/Session` | `BuilderSession.Launch(designRef, host)`, the Custom menu or frame, the `sceneLoaded` hook that calls the WP2 and WP4b appliers, save, score and achievement isolation, a results screen hook | WP1, WP2 interfaces |
| **WP4a Editor UI (data)** | `Builder/Editor` | Builder menu, typed property panels from a schema attribute map, wave timeline, undo, "Test" button calling `BuilderSession.Launch` | WP1, `ICatalog` (for pickers), WP6 (export/import buttons) |
| **WP4b Level overlay (stage 2)** | `Builder/Overlay` | `ILevelOverlay.Apply(LevelDef)`: clone, remove or edit `BuildSlot`s before Start, deterministic GUIDs, spawn line add/move, props, `AstarPath` rescan | WP1, WP2 catalog |
| **WP4c Placement editor (stage 2)** | `Builder/Editor/Placement` | Free camera, palette, ghost, gizmos, selection | WP1, WP4b |
| **WP5 Asset import** | `Builder/Assets` | `IAssetProvider.GetSprite/GetAudio/GetMesh(AssetRef)` with a sha256 cache and URL fetch, PNG/WAV/OGG/OBJ loaders, tint and material helpers that are `GPUInstanced`-aware; glTF spike | WP1 `AssetRef` |
| **WP6 Sharing** | `Builder/Sharing` | `ShareCode.Encode/Decode` (`TFB1:`), `.tfpack` zip import/export, clipboard, conflict-free install into `packs/<id>`, Thunderstore pack template; later an index client | WP1 |

Suggested order: start WP1 contracts on day 1. WP2, WP5, WP6 and WP4a can then run in parallel, followed by WP3 and the end-to-end MVP. WP4b and WP4c come after that.

## Spikes to run first (each about half a day, in a dev launch)

1. Create a custom enemy template from `Resources.Load("e racer")`, patch `Spawn.Update`, and confirm it spawns, paths, drops coins and shows in the night preview.
2. Replace `EnemySpawner.waves` on Nordfels and run three nights with perks equipped.
3. Clone a `BuildSlot` before Start in Nordfels at a new position, then build and upgrade it, and check that the navmesh cut updates.
4. Run `AstarPath.active.Scan()` on a host map and measure the time under Rosetta and natively.
5. Check how unit meshes are coloured (palette texture, vertex colours or material colour) to decide how tints and skins work.
6. Load a glTFast DLL next to the game's Burst, Mathematics and Collections versions.
