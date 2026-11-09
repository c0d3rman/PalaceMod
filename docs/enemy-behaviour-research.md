# Enemy behaviour research

Reference for the enemy behaviour engine: what the game's enemy components do, how a ranged attack, targeting and
movement work end to end, and what can be composed onto a custom enemy's template (a copy of a vanilla prefab, built
inactive by `Runtime/UnitFactory.cs`). Read from the decompiled game (`.decompiled/`, `scripts/dev.py decompile`);
file:line pointers are into those files. Values quoted are code defaults: the prefabs' serialized values override them
(dump real values in game before relying on a number).

What the current format already reaches (UnitDef): `stats.attackRange` writes every `AutoAttack.targetPriorities[].range`,
`stats.attackCooldown` `AutoAttack.cooldownDuration`, `stats.keepDistance` `PathfindMovementEnemy.keepDistanceOf`,
`stats.moveSpeed`, `stats.aggroTime`; `weapon.base` swaps the Weapon (and with it the projectile), `weapon.projectileSpeed`,
`weapon.damage` / `weapon.splash` the DamageModifyer lists; `behaviours: SpawnOnDeath` sets `Hp.enemyToSpawnOnDeath`.
Target and movement priorities, homing range, parabola, on-hit effects and every component below are not exposed yet.

## 0. TagManager.ETag — full enum (TagManager.cs:7-60)

| Tag | Value | Tag | Value |
|---|---|---|---|
| PlayerOwned | 0 | AUTO_NoReviveNextMorning | 25 |
| EnemyOwned | 1 | PlayerOwnedPriorityTarget | 26 |
| Player | 2 | BlockableEnemyProjectile | 27 |
| CastleCenter | 3 | Boss | 28 |
| MeeleFighter | 4 | UselessWallThatDoesNotBlockPath | 29 |
| RangedFighter | 5 | Healer | 30 |
| Flying | 6 | MagicProjectile | 31 |
| PlayerUnit | 7 | NaturallyVulnerableToSplash | 32 |
| Building | 8 | NaturallyHighHealthTarget | 33 |
| SiegeWeapon | 9 | UnreachableForBigEnemiesDueToLevelDesign | 34 |
| AUTO_Alive | 10 | PlayerShrine | 35 |
| AUTO_KnockedOutAndHealOnDawn | 11 | VodooCursed | 36 |
| Wall | 12 | PlayerHeroUnit | 37 |
| InfrastructureEconomy | 13 | FireAndExplosionResistant | 38 |
| TakesReducedDamageFromPlayerAttacks | 14 | NONE | 39 |
| PracticeTargets | 15 | Humanoid | 40 |
| FastMoving | 16 | LargeUnit | 41 |
| ArmoredAgainstRanged | 17 | Exploding | 42 |
| VulnerableVsRanged | 18 | TrainingFacility | 43 |
| Monster | 19 | Bridge | 44 |
| House | 20 | Group1 | 45 |
| WallOrTower | 21 | Group2 | 46 |
| AUTO_Commanded | 22 | Group3 | 47 |
| TakesIncreasedDamageFromTowers | 23 | OnDeathEnemySpawner | 48 |
| Tower | 24 | Undead | 49 |
| | | Vampire | 50 |

How code uses the tags:
- Almost all tags only matter through data: TargetPriority and DamageModifyer lists inside prefabs and Weapons. Only a few are read by code:
  - **Player**: aggro, magic armor, and projectile firedByPlayer.
  - **AUTO_Alive / AUTO_KnockedOutAndHealOnDawn**: set by Hp.
  - **Flying**: spawn constraint, AimbotProjectile air/ground spawn.
  - **LargeUnit**: spawn constraint, Spawn.cs:126.
  - **Exploding**: God of Chaos skips it, Spawn.cs:135.
  - **MeeleFighter**: lane split, PathfindMovementEnemy.cs:147.
  - **FastMoving**: Weapon.slowsFastEnemiesFor, BarricadeDamage.
  - **RangedFighter**: magic armor (Weapon.cs:253), range god.
  - **Humanoid**: God of Afterlife spawn-on-death.
  - **AUTO_Commanded**: auto-added to enemy movement's mayNotHave.
  - **BlockableEnemyProjectile**: used by BlockProjectiles through prefab data.
- TaggedObject keeps a bitfield (`1L<<tag`). Tags are registered with TagManager in **OnEnable** (TaggedObject.cs:79-87). `AddTag`/`RemoveTag` update the bitfield and the manager lists at runtime.
- `TaggedObject.colliderForBigOjectsToMeasureDistance` (public Collider): if set, every distance check uses `ClosestPoint` on it (buildings). That affects range checks, movement destination and projectile aim point.

---

## 1. Target finding (TagManager / TargetPriority)

**TargetPriority** (TargetPriority.cs; a `[Serializable]` plain class, not a MonoBehaviour)

| Field | Type | Meaning |
|---|---|---|
| `mustHaveTags` | List<ETag> | target must have every tag (AND) |
| `mayNotHaveTags` | List<ETag> | target must have none of these (bitfield test) |
| `range` | float | max distance, to the closest point if a big collider is set |
| `minRange` | float | only used by `FindFarthestTaggedObjectWithRange` and `FindTaggedObjectsCloseToHomeInRange`; the closest-target finders ignore it |

- **Empty `mustHaveTags` matches nothing.** `FindAllTaggedObjectsWithTags` returns empty when `_mustHaveTags.Count <= 0` (TagManager.cs:215). Always include at least one tag, e.g. PlayerOwned.
- It iterates the smallest per-tag list among the mustHave tags, then bitfield-filters (TagManager.cs:212-262).
- `FindClosestTaggedObject(pos)` (TargetPriority.cs:26-39) takes the globally closest match (`FindClosestTaggedObjectWithTags`, TagManager.cs:275-293), then returns it only if its distance is ≤ `range`. This is the same as "closest within range".
- Other finders, all filtered by `range`:
  - `FindClosestTaggedObjects` (sorted N, TagManager.cs:295)
  - `FindHighestHealthObjectInRange` (HpValue)
  - `FindHighestMaxHealthObjectsInRange` (maxHp)
  - `FindRandomObjectInRange(s)`
  - `FindLowestHealthObjectInRange(s)` (HpPercentage, optional exclude-full)
  - `FindLowestHealthTaggedObjects`
  - `FindTaggedObjectCloseToHome` (TargetPriority.cs:304-327): its `_homeRange` is computed but **ignored**, so only `range` matters. This is the one enemy movement uses.
- Dead and knocked-out objects: Hp removes `AUTO_Alive` on knockout and destroys the object on death (Hp.cs:413-423). Priorities normally include AUTO_Alive in mustHave, or AUTO_KnockedOutAndHealOnDawn in mayNot, to skip knocked-out buildings. Destroyed enemies drop out through OnDisable.

**Priority order** (AutoAttack.cs:123-135, static version :137-148): the first TargetPriority in list order that yields any in-range object wins. Inside it, the closest object wins. A later priority is used only if every earlier one finds nothing in range. "Attack the closest of anything" is a single priority with broad tags. "Prefer X else Y" is [X-priority, Y-priority].

---

## 2. AutoAttack family

### AutoAttack (AutoAttack.cs) : MonoBehaviour
Purpose: the generic auto-attack. Picks a target from priorities and calls `weapon.Attack` on a cooldown, at night only.

| Field | Type / default | Meaning |
|---|---|---|
| `cooldownDuration` | float 1 | seconds between attacks |
| `cooldownAfterSpawn` | float -1 | initial cooldown (Start: `cooldown = cooldownAfterSpawn`) |
| `cooldownRandomization` | float 0, range 0..1 | cooldown × (1 ± r) |
| `recheckTargetInterval` | float 0.5 | wait when no target was found |
| `targetPriorities` | List<TargetPriority> | **attack range lives here** (`range`) |
| `weapon` | Weapon (SO) | what is fired |
| `spawnAttackHeight` | float 0.5 | origin = pos + up×h |
| `optionalAttackOrigin` | Transform | overrides the origin (base OnAttack and Tower only) |
| `damageMultiplyer` | [SF] protected float 1 | property `DamageMultiplyer`; Spawn scales it by difficulty |
| `projectileSpeedMultiplyer` | [SF] protected float 1 | |
| `onAttackTriggered` | [HideInInspector] public UnityEvent | |
| `onCooldown` | [HideInInspector] public bool | |

- **Update** (AutoAttack.cs:87-108): returns if it is **Day**. Otherwise `cooldown -= dt`. When the cooldown is ≤ 0 it finds a target. With no target, `cooldown += recheck`. With a target, `cooldown += duration*rand` and it calls `OnAttack`.
- **OnAttack** (AutoAttack.cs:110-121): `weapon.Attack(origin, target.Hp, Vector3.zero, taggedObject, damageMultiplyer, projectileSpeedMultiplyer)`.
- **Range is measured from `transform.position`**, not from the attack origin.
- Dependencies: `GetComponent<TaggedObject>()` in Start (used as the attacker). Hp caches `GetComponents<AutoAttack>()` (same GameObject only) and enables or disables them on knockout or revive (Hp.cs:215, 269-276).
- Public helpers: `ReduceCooldownBy`, `SetCooldownTo` (also sets cooldownAfterSpawn), `LastTargetPosition`.
- Addable: **yes**. Needs a Weapon (clone a vanilla one), a TargetPriority list, and a TaggedObject plus Hp on the same GameObject. Several AutoAttacks can sit on one GameObject; each runs independently.

### Subclasses

**AutoAttackHighestHealth** (AutoAttackHighestHealth.cs)
- Overrides FindAutoAttackTarget to use `FindHighestHealthObjectInRange` (current HpValue).
- No extra fields.

**AutoAttackLowestHealth** (AutoAttackLowestHealth.cs)
- Extra field: `excludeFullHealthTargets` bool true.
- Targets the lowest HpPercentage. **This is the healer pattern.** `Hp.TakeDamage(amount<=0)` calls `Heal(-amount)` (Hp.cs:377-381), so a weapon with negative `damageAdded` that targets `EnemyOwned` heals allies.
- Catch: Weapon.DealDamage skips additional effects when the result is 0 (Weapon.cs:243).

**AutoAttackRandom** (AutoAttackRandom.cs)
- Targets a random object in range.

**AutoAttackTwoTargets** (AutoAttackTwoTargets.cs)
- Fires at the 2 closest targets, through the private `targets[2]` array.
- **No Day gating.**
- Ignores `optionalAttackOrigin` and `projectileSpeedMultiplyer`.
- Bug: the break check `outAttacks[i]` (AutoAttackTwoTargets.cs:35) indexes by priority index, so it throws IndexOutOfRange with more than 2 priorities.

**AutoAttackLowestInRangeMulti** (AutoAttackLowestInRangeMulti.cs)

| Field | Type / default |
|---|---|
| `attackTargets` | [SF] int 3 |
| `excludeFullHealthTargets` | bool true |
| `randomizeSpawnPosition` | [SF] float |
| `randomizeProjectileSpeed` | [SF] float |

- Day-gated.
- Fires at up to N lowest-% targets per cooldown, with jittered origin and speed. It **always resets the cooldown even with no target** (no recheck path).
- Has the same `outAttacks[i]` bug (line 60), which throws when there are more priorities than `attackTargets`.

**AutoAttackTower** (AutoAttackTower.cs)

| Field | Type |
|---|---|
| `targetingMode` | ETargetingMode {TargetClosest, TargetRandom, TargetHighestHealth (max hp), TargetLowestHealthDamaging, TargetLowestHealthHealing (exclude full)} |
| `simultaneousProjectiles` | int; 0 still means 1 |
| `tempCollectionArray` | [HideInInspector] public TaggedObject[1]; serialized; per-priority cap |

- Day-gated. Collects targets across priorities up to `simultaneousProjectiles`, then calls OnAttack on each (AutoAttackTower.cs:25-53, 55-170).
- Reusable on enemies (it is generic despite the name).

**AutoAttackWithWarning** (AutoAttackWithWarning.cs)

| Field | Type / default | Meaning |
|---|---|---|
| `line` | [SF] Shapes.Line | **required** (Start NREs otherwise; it is un-parented to the world) |
| `breakTargetingDistance` | float 10 | |

- Two-step attack (AutoAttackWithWarning.cs:61-76). The first cooldown hit only marks the target and shows the line. The next hit fires if the same target is found again. If a different target is found, it fires at the old one and marks the new one.
- Targeting breaks if the target loses AUTO_Alive or goes beyond `breakTargetingDistance`.
- The line is hidden for Building targets (:49-52).
- OnDestroy destroys the line.
- Spawn scales `breakTargetingDistance` by the range-god multiplier.
- Addable only if a Shapes `Line` child is copied from a vanilla warning-enemy prefab.

**AutoAttackWithKnockback** (AutoAttackWithKnockback.cs)

| Field | Type / default |
|---|---|
| `maxForce` | [SF] float |
| `maxRange` | [SF] float 10 |
| `knockbackEffect` | [SF] ParticleSystem, optional |
| `areaAngle` | [SF] int 360 |
| `facer` | [SF] Transform, **required** (knockback source) |
| `mustHaveTags` | [SF] List<ETag>, **must be non-null** |
| `mayNotHaveTags` | [SF] List<ETag>, **must be non-null** |

- Base attack, then radial knockback through KnockbackUtils.
- Knockback only affects targets that have an enabled **KnockbackBehavior** component (KnockbackUtils.cs:155-159), and the target needs a PathfindMovement and RVO.

**FinalBossSpellThrower** (FinalBossSpellThrower.cs)
- Calls `weapon.Attack(origin, null, target.pos - origin, …)`. The projectile flies to the target's **position at fire time**, does no homing and no direct damage, and acts only through `spawnOnGroundWhenTargetingGround`. This is the ground-targeted artillery pattern.

---

## 3. Weapon (ScriptableObject, Weapon.cs) — fields

| Group | Field | Type / default | Meaning |
|---|---|---|---|
| | `blacksmithEffect` | EDamageAffectedByBlacksmithUpgrade {Null, MultiplyBy_MeleeDamage, MultiplyBy_RangedDamage, DivideBy_MeleeResistance, DivideBy_RangedResistance} | enemy weapons typically use DivideBy_*Resistance |
| Projectile | `projectileFacingDirection` | {Uniform, FaceVictim, FaceAttacker, Random}; only FaceVictim is implemented | |
| | `projectileSpeed` | float | **>0 together with `projectileVisuals` = projectile mode** |
| | `projectileParabulaFactor`, `projectileParabulaOffset` | float | arc height = max(0, (dist+offset)×factor), parabolic over the flight |
| | `projectileVisuals` | GameObject | the projectile prefab; **must have AimbotProjectile on its root** (no null check: Weapon.cs:127-137 NREs) |
| | `projectileImpactVisuals` | GameObject | unused in code |
| | `raycastLength` | float 1 | unused |
| | `performRaycastBeforeShooting` / `raycastBeforeShootingLayerMask` | | if a collider with Hp blocks the line, it becomes the target (Weapon.cs:100-112) |
| | `maximumChaseRange` | float 10000 | homing distance budget |
| | `shootWithoutTargetRange` | float 20 | unused |
| | `spawnOnGroundWhenTargetingGround` / `spawnInAirWhenTargetingAir` | GameObject | Instantiated (not pooled) on impact; WeaponDamageDealer components on it get `DamageMultiplyer = finalMult×BlacksmithMulti` (AimbotProjectile.cs:198-217) |
| Melee | `fxSpawnOnAttacker` + `parentFxToAttacker` | GameObject, bool | pooled FX at the origin; parenting is a no-op; **its SplashDamageArea children apply splashDamage** |
| | `fxSpawnOnVictim` + `followFxVictim` | GameObject, bool | pooled FX at the victim; also triggers splash |
| Damage | `isPlayerWeapon` | bool | passed as causedByPlayer |
| | `directDamage` | List<DamageModifyer> | |
| | `splashDamage` | List<DamageModifyer> | **only applied by the FX path (no projectile)** |
| | `slowsFastEnemiesFor` | float | slows targets tagged FastMoving that have a PathfindMovement; note `>= 0` means 0 also calls Slow(0) |
| Effects | `additionalWeaponEffects` | [SF] private List<AdditionalWeaponEffectScript> | on-hit effects (§6) |

**DamageModifyer** (DamageModifyer.cs; [Serializable]): `requiredTags` (all must be present; empty means always applies), `damageAdded`, `damageMultiplyer` = 1. The total is computed in list order, `num = (num + added) * mult` for each modifier that applies, then × the final multiplier (DamageModifyer.cs:28-41).

### Weapon.Attack(origin, Hp target, dir, attacker, dmgMult, speedMult) (Weapon.cs:92-153)
1. Aim point = the target's big-collider ClosestPoint, or `target.pos + hitFeedbackHeight·up`. With no target it is `origin + dir`.
2. Optional raycast re-target.
3. **Projectile mode** (`projectileSpeed > 0 && projectileVisuals`):
   - `ObjectPools.GetPooledItemByObject(projectileVisuals)` auto-creates a pool per prefab and Instantiates the clone; Get leaves it inactive.
   - It sets position, clears the AraTrail, and calls SetActive(true).
   - Then `AimbotProjectile.Fire(this, target, maximumChaseRange, backupTarget=aim if target null, attacker, dmgMult, speedMult)`, and **returns**. There is no FX or splash here.
4. **Melee/instant mode**:
   - Spawns `fxSpawnOnAttacker` at the origin and calls `DealSplashDamage(fx)`.
   - Spawns `fxSpawnOnVictim` at the aim point and also calls DealSplashDamage on it.
   - Then `DealDamage(target)`, which is a no-op if the target is null.
   - A Weapon with no projectile, a null target and `fxSpawnOnAttacker` holding a SplashDamageArea is an **AoE at self**. This is how death explosions and landing slams work.

**DealDamage** (Weapon.cs:199-273):
- Applies the blacksmith factor, then `CalculateDirect/SplashDamageOnTarget` → `Hp.TakeDamage(dmg, attacker, isPlayerWeapon)`.
- If the damage is non-zero, it runs `additionalWeaponEffects`.
- Magic armor reflect: if the target is the Player and has magic armor, the attacker takes damage, more against RangedFighter-tagged attackers.
- Then the FastMoving slow.

**SplashDamageArea** (SplashDamageArea.cs; a MonoBehaviour on FX children):

| Field | Type |
|---|---|
| `shape` | {Sphere, Box} |
| `boxSize` | Vector3; half-extents |
| `sphereRadius` | float |
| `layerMaskRecieveDamage` | LayerMask |

It collects `Hp` from physics overlaps. **Friend or foe is decided only by the layer mask plus the splash DamageModifyer `requiredTags`.** A modifier with no matching tags gives 0 damage, so splash with requiredTags [PlayerOwned] spares enemies.

### AimbotProjectile (AimbotProjectile.cs) : MonoBehaviour, on the projectile prefab
| Field | Type | Meaning |
|---|---|---|
| `shakeCameraOnDestroy` | bool | |
| `objectsToUnparentOnDestroy` | List<{GameObject target; float lifetime}> | e.g. trails kept alive after impact |
| `spawnOnHit` | GameObject | Instantiated at the impact point |
| `onHit` | [HideInInspector] UnityEvent | |
| `rememberTarget` | [HideInInspector] public Vector3 | |

- **Fire** (AimbotProjectile.cs:80-115): stores the target and its Flying flag, `remainingRange = max(chaseRange, dist+0.5)`, `speed = weapon.projectileSpeed × speedMult`.
- **Update** (AimbotProjectile.cs:117-178):
  - While the target transform is alive, it re-aims every frame at the target's current point. This is the "aimbot" homing.
  - It moves linearly toward the aim point at `speed` and adds the parabola height.
  - On arrival it calls `weapon.DealDamage(targetHp)` only if the target still exists, then spawns the weapon's ground or air object (chosen by whether the **target** is Flying), then `spawnOnHit`, then `onHit`, then destroys itself or returns to the pool.
  - When `remainingRange` runs out, homing stops (`targetTransform = null`). The projectile then lands at the last point and **does no direct damage**. A projectile whose target died also does no direct damage but still spawns its on-hit objects.
- Projectile splash is done through `spawnOnGround…` objects, e.g. FireArcherBurn, or a SpawnAttackAfter carrying a second instant Weapon. There is no other projectile splash path.
- Related helpers on projectiles:
  - ProjectileImpactParticles (`target`, `particles`)
  - BossProjectileTargetMarker (`projectile`, `targetMarker`)
  - ProjectileAudio, PlayOneShotOnProjectileHit
  - Optionally a TaggedObject with `BlockableEnemyProjectile`, so BlockProjectiles can delete it.

**Other projectile-like classes:**
- **LaunchableProjectile** (LaunchableProjectile.cs): a straight-line box sweep, player-launched.
  - Fields: `speed` 20, `lifetime` 3, `groundedTranslation`, `groundLayer`, `groundingOfffset`, `dmgModifiers`, `hitLayer`, `damageBoxExtents`.
  - It needs `Launch()` to be called, and it hits any Hp whose collider is on `hitLayer`.
- **SpawnAttackAfter** (SpawnAttackAfter.cs): a delayed AoE marker.
  - Fields: `spwanAttackOnDeath` (Weapon), `delay`, `disc` (Shapes.Disc, **required**: Start reads `disc.Radius`), `sound`, `mute`, `isPlayerAttack`, `damageMultiplyer` 1.
  - After `delay`, it calls `weapon.Attack(pos, null, 0, null, mult)` and destroys itself.
  - Ghostqueen fire bolts use it (Ghostqueen.cs:180-184).
  - It is **not** a WeaponDamageDealer, so AimbotProjectile won't scale it.
- **FireArcherBurn : WeaponDamageDealer** (FireArcherBurn.cs): a burning ground patch.
  - Fields: `hotOilGround` (PS), `hotOilGroundBurnTickDuration` 0.2, `hotOilGroundBurnTicks` 5, `range` 3.5, `damageModifyers`, `mustHaveTags`, `mayNotHaveTags`, `timeTillSelfDestroy` 5.
  - Tag-based (not physics). Each tick deals total/ticks to everything in range.
  - Used by projectile ground spawns and the Moleman dig FX.

### End-to-end ranged attack (enemy archer)
1. **Pick the target**: AutoAttack.Update, at night with the cooldown at or below 0, walks `targetPriorities` in order; the first priority with something in `range` wins, closest first.
2. **Fire**: OnAttack calls `Weapon.Attack`, which takes a pooled `projectileVisuals` clone and calls `AimbotProjectile.Fire`.
3. **Flight**: the projectile homes on the target each frame along a parabolic path at `projectileSpeed × mult`, until arrival or until `maximumChaseRange` is used up.
4. **Hit**: on arrival it calls `weapon.DealDamage`, which sums `directDamage` modifiers by target tags × AutoAttack.damageMultiplyer × blacksmith resistance, then `Hp.TakeDamage`, then the additional effects.
5. **Splash**: optional AoE comes from `spawnOnGroundWhenTargetingGround`, `spawnInAirWhenTargetingAir` or `spawnOnHit`.

Range is `TargetPriority.range`, measured from `AutoAttack.transform.position`. Movement's `keepDistanceOf` should be at or below the attack range, otherwise the unit stands out of range.

---

## 4. Movement

### PathfindMovement (PathfindMovement.cs) : abstract MonoBehaviour
- `IsSlowed`, `RVO`, `ClearCurrentPath()`, `Slow(float)`, `GetAgroFromObject(TaggedObject)`.
- Hp finds it with `GetComponent<PathfindMovement>()`.

### PathfindMovementEnemy (PathfindMovementEnemy.cs) : PathfindMovement, IDaytimeSensitive (empty handlers)
**[RequireComponent(Seeker)]**. It also needs an **RVOController** on the same GameObject (used without a null check, :310-321) and a TaggedObject (Start :147).

| Field | Type / default | Meaning |
|---|---|---|
| `targetPriorities` | List<TargetPriority> | **movement** priorities, separate from the attack priorities |
| `keepDistanceOf` | float 2 | stop when this close to the destination point (unless walking home); range god scales it |
| `maximumDistanceFromHome` | float 100000 | effectively unused (homeRange is ignored) |
| `movementSpeed` | float 2 | |
| `recalculatePathInterval` | float 1 | repath every interval when chasing the player, otherwise ×2 |
| `backupMovementGraph` | string "" | A* graph name to use when the main path fails or ends more than 1 unit from the target |
| `agroTimeWhenAttackedByPlayer` | float 5 | seconds spent chasing the king after he damages it |
| `speedWhenSlowed` | [SF] float 0.33 | speed multiplier while slowed |
| `eneableStuckDetection` | [SF] bool | after 3 s stuck, wander on a rotating escape vector for 5 s |

Graph and flying:
- The **graph is chosen by `Seeker.graphMask`**: "Enemy Units S", "Enemy Units L" or "Enemy Units Air" (EnemySpawner.cs:252-254). EternalTrialEnemy reads indices 0/1 as small ground and 2 as large (EternalTrialEnemy.cs:94-106).
- **Flying = Air graph + Flying tag.** The Flying tag makes Spawn use the air constraint and +5 Y. **Large = L graph + LargeUnit tag** (Spawn.cs:124-133).
- There is no separate flying movement code.

**Destination** (`FindMoveToTarget`, PathfindMovementEnemy.cs:207-234):
1. If the aggro timer is above 0 and the aggro player is not knocked out, the destination is the king's position, and `currentlyChasingPlayer` is set.
2. Otherwise, for each movement priority in order, `FindTaggedObjectCloseToHome` returns the closest match within `range`. The first hit gives the destination: the closest point of the target's collider. If that target is the Player, it counts as chasing.
3. With no target it **walks home**, to its spawn position plus a random offset. Movement priorities therefore usually include a huge-range CastleCenter or PlayerOwned entry, so the enemy always heads to the base.

Start and repathing:
- Start (:126-152) adds `AUTO_Commanded` to every movement priority's `mayNotHaveTags`, so enemies don't path to units the player is commanding.
- With the Ice Magic perk it scales the slow values.
- **MeeleFighter-tagged enemies with an odd spawn count use the backup graph as their primary**, which splits them across lanes.
- It snaps to the navmesh.

Update and stopping:
- Update (:236-259) has **no Day gating**. It repaths on the timer through `OriginalPathRequest` (A* `seeker.StartPath`). While chasing, the last path point tracks the player live.
- **Stopping** (FollowPathUpdate :266-345): if the distance to the destination point is below `keepDistanceOf` and it is not walking home, it stands still. This is the only "stop to attack" mechanism; the AutoAttack fires independently whenever something is in its own range.
- Speed is `min(movementSpeed, dist/dt)`, × `speedWhenSlowed` while slowed, then `rvo.SetTarget`. The position is snapped to the navmesh each frame.

What decides which targets it attacks:
- **Buildings, units or the king is decided purely by the tags in both priority lists**: movement chooses where to walk, AutoAttack chooses what to shoot. Examples: [Building] or [PlayerOwned, Building] for siege, [PlayerUnit] for unit hunters, [Player] for the king.
- **Damage tags do not affect choice.** DamageModifyer.requiredTags only scales damage, e.g. bonus vs Building.
- The king also gets targeted through **aggro**: any damage from a Player-tagged attacker (`Hp.TakeDamage` calls `GetAgroFromObject`, Hp.cs:382-385) makes the enemy chase the king for `agroTimeWhenAttackedByPlayer` s. This only redirects movement; the attack still uses its own priorities.

Public API: `OriginalPathRequest()`, `GetNearestGroundPosition(v)`, `SnapToNavmesh()`, `SeekToTargetPosSnappedtoNavmesh`, `SeekToTargetObj`, `HomePosition`.

Addable: already present on every walking enemy; change its fields rather than adding it.

**PathfindMovementPlayerunit**: the player version (hold position, follow player, daytime speed boost). Not usable on enemies.

**MovementFollowPath** (MovementFollowPath.cs; Ghostqueen boss)
- Fields: `pathParent` (Transform whose children are waypoints), `speed` 5.
- Moves through the waypoints directly, sets RVO velocity, needs an RVOController, and stops at the end.
- Usable only with a scene path object.

**SimpleWalk / RacerRoll**: visual bobbing and wheel-roll only.

---

## 5. Hp (Hp.cs) — [RequireComponent(TaggedObject)]

| Field | Type / default | Meaning |
|---|---|---|
| `maxHp` | float | |
| `getsKnockedOutInsteadOfDying` | bool | player units and buildings; enemies die |
| `hitFeedbackHeight` | float | aim height for incoming attacks |
| `aliveVisuals` / `knockedOutVisuals` | GameObject | |
| `fxToSpawnOnDeath` / `fxToSpawnOnRevive` | GameObject | death FX only spawns if on screen |
| `enemyToSpawnOnDeath` | GameObject | Instantiated at death, then `Spawn.AdjustEnemyParametersAfterSpawn(go, elite)` applies perk and elite scaling (Hp.cs:409-412) |
| `deathFxSpawnOffset` | Vector3 | |
| `deathFxSpawnWithPosAndRotOf` | Transform | |
| `coin` / `coinSpawnOffset` / `coinCount` | GameObject / float / int | gold drop; Spawn overrides coinCount |
| `spwanAttackOnDeath` | [SF] private Weapon; property `SpwanAttackOnDeath` | at death: `Attack(pos, null, 0, self, Hp.damageMultiplyer)`. This is the **death explosion**: an instant weapon with `fxSpawnOnAttacker` + SplashDamageArea + splashDamage |
| `invulnerable` | public bool | |

- Events (HideInInspector): `OnHpChange`, `OnRevive`, `OnHeal`, `OnReceiveDamage(bool byPlayer)`, `OnKillOrKnockout`, `OnHPValueChanged(float)`, `OnKillOrKnockoutDetailed(victim, killer)`, `OnReceiveDamageDetailed(attacker)`.
- `Hp.damageMultiplyer` is non-serialized and defaults to 1. **Difficulty scaling does not touch it**, so the death explosion does not scale with difficulty.
- **TakeDamage** (Hp.cs:367-445), in order:
  1. Ignored if dead or invulnerable.
  2. An amount ≤ 0 heals instead.
  3. Aggro.
  4. hp -= amount, events.
  5. At 0: kill events, death FX, spawn-on-death enemy, knockout or `Destroy`, coins, death weapon.
- `Heal`, `Revive`, `SetHpTo`, `SetHpToMaxHp`, `ScaleHp`, `HpPercentage` (settable), `TakeDamageSelfDrained` (cannot kill).
- `Initialize` caches the AutoAttacks, PathfindMovement, RVO and so on with GetComponents **at Start**, so components added to the template are picked up.

---

## 6. On-hit effects (AdditionalWeaponEffectScript ScriptableObjects; create with `ScriptableObject.CreateInstance`, set fields by reflection, add to the cloned Weapon's private `additionalWeaponEffects`)

Signature: `Effect(Hp hit, TaggedObject attacked, TaggedObject attacker, Weapon w, float dmgMult, bool killed, float dmg)`.

| Class | Fields | Effect |
|---|---|---|
| **SlowEffect** (SlowEffect.cs) | `slowDuration` 2 | `hit.PathfindMovement.Slow`. Works on player units, which have PathfindMovementPlayerunit; **does not slow the king** (no PathfindMovement there; use DamagePlayerWhenEnteringRange to slow the king) |
| **KnockbackEffect** | `maxForce`, `maxRange` 10, `areaAngle` 360, `mustHaveTags`, `mayNotHaveTags` (all [SF]) | radial knockback around the attacker; targets need a KnockbackBehavior |
| **HealthStealWeaponEffect** | `regenTargetHp` 2 | attacker heals a flat amount per hit; **NREs if the attacker is null** |
| **PercentageHealthStealWeaponEffect** | `regenPercentage` 0.5 | attacker heals dmg × % (lifesteal) |
| **HealOnKillWeaponEffect** | `healOnKill` 10, `applyPlayerSelfHealMulti` | heal on kill |
| **CurseEffect** | `weaponToDamageEverybody` | relies on the player's CurseBlastMA singleton; player-only |
| **GodlyCurseEffect** | — | player-upgrade gated |
| **BloodWandPassive/ActiveAttEffect** | — | player wand; static state; not for enemies |

The weapon fields `slowsFastEnemiesFor` and the FastMoving-only slow are a weapon-level slow, not an effect.

---

## 7. Special behaviour components (enemy-usable)

### Moleman (Moleman.cs) — burrow/teleport
| Field | Type / default | Meaning |
|---|---|---|
| `nextTeleportIn` | 3 | first dig delay |
| `teleportIntervalMin` / `teleportIntervalMax` | 6 / 6 | |
| `teleportTowardsTargetAmount` | 0.5 (0..1) | lerp toward the movement destination |
| `targetDistanceMin` / `targetDistanceMax` | 0.5 / 20 | random offset radius |
| `maxTunnelingDistance` | 20 | |
| `aliveParent` | Transform, **required** | spun and sunk during the dig animation |
| `meshParent` | GameObject, **required** | hidden while tunnelling |
| `autoAttack` | AutoAttack, **required** | disabled while digging |
| `digDepth` | 2.5 | |
| `spawnOnDigDown` / `spawnOnDigUp` | GameObject, optional | if it has FireArcherBurn, it gets `GetComponent<AutoAttack>().DamageMultiplyer` |
| `digDownFX` / `digUpFX` | ParticleSystem, optional | |
| `digAudioSource` | AudioSource, optional | |
| `digAudioPitchRange` | (1,1) | |
| `animationSpeed` | 2 | |
| `digTrevelingSpeed` | 2 | |

- Update has no day gating. Coroutine (Moleman.cs:96-147): dig down → pick a point toward `enemyMovement.SeekToTargetPosSnappedtoNavmesh`, snapped to ground → lerp under ground → dig up → repath.
- Needs PathfindMovementEnemy (GetComponent).
- **The unit is not invulnerable while underground.**
- Addable: yes. Point `aliveParent` at the model root's child transform and `meshParent` at the mesh GameObject of the cloned prefab. The FX are optional.

### UnitDashJumpEnemy (UnitDashJumpEnemy.cs) — leap attack (Corrupt King)
| Field | Type / default | Meaning |
|---|---|---|
| `recheckInterval` | 0.75 | |
| `jumpCooldown` | 12 | |
| `snapRange` | 1 | aborts if the target isn't within this of the navmesh |
| `autoAttack` | AutoAttack, **required** | |
| `minAttackJumpDistance` / `maxAttackJumpDistance` | float, both default 0 | **must be set, otherwise it never jumps** |
| `jumpDuration` | 1 | |
| `jumpHeight` | 10 | height curve = h·h·(t−t²) |
| `targetPriorities` | public List<TargetPriority> | |
| `additionalInvulnerabilityTime` | float | |
| `weaponOnLanding` | Weapon, **required** | Attack(landPos+h, target.Hp, dir, self, autoAttack.DamageMultiplyer); an instant weapon with FX + splash makes a slam |
| `aliveParent` | Transform, **required** | |
| `meshParent` | GameObject | |
| `hp` | Hp, **required** | |
| `unitMovement` | PathfindMovementEnemy, **required** | |
| `taggedObject` | TaggedObject | attacker |
| `onStartJump` | public UnityEvent<Vector3> | **no initializer: set it** |

- It is invulnerable during the jump, and AutoAttack and movement are disabled.
- Update has no day gating; it checks `hp.Alive`.
- It lands where the target was at takeoff (no tracking).
- Addable: yes, with all references set by reflection.
- **UnitDashJump** is the player version; it needs PathfindMovementPlayerunit, so do not use it on enemies.

### SelfDestructWhenInRangeOf (SelfDestructWhenInRangeOf.cs) — suicide trigger
- Fields: `selfDestructIfInRangeOf` ([SF] TargetPriority, must be non-null with tags and range), `checkInterval` 0.5.
- Calls `hp.TakeDamage(1e9)` when anything matches in range.
- Pair it with `Hp.spwanAttackOnDeath` (explosion weapon) for a bomber. Vanilla exploding enemies also carry the `Exploding` tag.
- No day gating. Addable: **yes, trivially.**

### KillOnTouch (KillOnTouch.cs)
- Fields: `layerMask`, `boxCollider` (**required**), `mustHaveTag` (single ETag).
- Each frame, an overlap box deals 1e13 damage to any overlapping TaggedObject with the tag. It is a level hazard: no cooldown, no day gating.
- Addable with a BoxCollider, but brutal.

### DamagePlayerWhenEnteringRange (DamagePlayerWhenEnteringRange.cs) — thorns/contact damage vs the king
| Field | Meaning |
|---|---|
| `damageWhenEntering` | [SF] |
| `addedDamageWhenEnteringWhileMoving` | [SF] |
| `addedDamageWhenEnteringWhileSprinting` | [SF] |
| `range` | [SF] |
| `slowPlayerFor` | [SF] |
| `myTaggedObject` | [SF] TaggedObject, attacker |

- Triggers once per entry, when the king moves into range moving toward the enemy (dot < 0).
- Scaled by the tiger and war-gods perks. Uses `TagManager.Players[0]`.
- Addable: yes.

### ShieldOnFirstDamaged (ShieldOnFirstDamaged.cs) — one-shot invulnerability shield
- Fields: `hpToShield` (Hp, **required**), `shieldDuration` (float), `shieldVisuals` (GameObject, **required**: SetActive is called without a null check).
- On the first damage it **resets hp to max** and is invulnerable for `shieldDuration`. Resets at dawn.
- Listener added in Start, so it is safe on a template.
- Addable: yes; give it any child GameObject as the visual, e.g. a copied bubble.

### Invulnerable (Invulnerable.cs)
- Heals 1e6 every frame. Damage still lands but is refilled each frame, so only a hit larger than max hp kills.

### LoseHealthOverTime (LoseHealthOverTime.cs)
- Fields: `hp` (**required**), `loseHealthPerSecond`.
- Self-damage every 0.5 s with no feedback. Gives timed or decaying enemies; can kill.

### PukeLogic (PukeLogic.cs) — dragon breath DoT on a fixed Hp
- Fields: `hpToDamage`, `tickCooldown`, `percentageDamage`, `realDamage`, `hpOwn`, `ownDps`.
- Damages one fixed Hp (scene reference), divided by ranged resistance, and drains itself. Not generically useful.

### BlockProjectiles (BlockProjectiles.cs) — projectile blocker aura (player shield unit)
- Fields: `blockInterval` 1, `range` 30, `fxBlockProjectile`, `mustHaveTags` (public; mayNot is always empty).
- Every interval it **destroys or returns to pool the closest object matching the tags**.
- On an enemy, set it to destroy player projectiles only if those carry a TaggedObject; otherwise it would destroy matching units.

### IncreaseAttackSpeedOverTime (IncreaseAttackSpeedOverTime.cs) — berserk ramp
- Fields: `autoAttack` (**required**), `maximumAdditionalCooldownSpeed` 4, `increaseSpeed` 0.1, `decreaseSpeed` 1.
- While it is attacking, it reduces the cooldown at a ramping rate. Night only. Addable: yes.

### TriggerOnHealthLoss (TriggerOnHealthLoss.cs) — phase hook
- Fields: `healthToMonitor` (Hp), `healthSegments` (int), `onHealthSegmentLost` (public UnityEvent, no initializer).
- Fires the event per segment lost. **Listeners added in code on the template are not copied by Instantiate**, so it is only useful with a custom component that subscribes in its own Start.

### Knockback receivers/sources
- **KnockbackBehavior** (KnockbackBehavior.cs, 300 lines) is **required on a target for it to be knocked back**. Fields: `arcHeight`, `knockbackCurve`, `forceByDistance`, `knockbackDuration`, `gravity`, `staggerDuration`, `staggerVFX`. It disables movement and attacks during the arc and stagger.
- **KnockbackTriggeredAuto** (KnockbackTriggeredAuto.cs) is a periodic knockback pulse.
  - Fields: `maxForce`, `minRange` 3.5 (trigger distance), `maxRange` 10, `knockbackEffect` (PS, **required**: Start reads its shape), `defaultCooldown` 5, `checkRate` 1, `areaAngle` 360, `facer` (**required**), `mustHaveTags`, `mayNotHaveTags`.
  - On an enemy it can knock back player units that have KnockbackBehavior. It **cannot push the king** (no PathfindMovement there).

### Spawner/summoner style
| Class | What it does | Reusable? |
|---|---|---|
| **Hp.enemyToSpawnOnDeath** | spawns one prefab at death | the only clean built-in spawn-on-death |
| **Ghostqueen** (Ghostqueen.cs) | boss loop: fire storm (toggles `aaFireStorm`), fire bolts (SpawnAttackAfter prefabs around the king), then `waveToSpawn` (a `Spawn` object), repeat | not reusable: scene-bound lights and many required references |
| **FinalBossDragon** (FinalBossDragon.cs) | per-health-segment animator triggers + waves; stage-2 on death | not reusable |
| **KeyframedBoss** (KeyframedBoss.cs) | Eismoloch keyframes + periodic `Wave`, self-damaging while spawning | not reusable |
| **IronCastleEnemySpawning** (IronCastleEnemySpawning.cs) | `List<Wave> waves` (3), `hp`; runs waves[0..2] at hp below 75/50/25 % | **the closest generic "spawn minions at health thresholds" component**, but it needs fully built `Wave`/`Spawn` objects (enemyPrefab, spawn line Transform) |
| **InstantEnemySpawn** (InstantEnemySpawn.cs) | `public Wave wave`; `SpawnWave()` on call | |
| **SummoningCircle** | level object; adds spawns to the next wave | not an enemy behaviour |

There is **no vanilla "periodically spawn minions" enemy component**. Build it as a mod component that calls `Object.Instantiate(prefab)` + `Spawn.AdjustEnemyParametersAfterSpawn(go, elite)`, copying Hp.cs:411.

### CorruptKingGround (CorruptKingGround.cs) — behaviour switch by HP ratio
- Fields: `hp`, `pathfindMovement`, `dashJump`, and four TargetPriority lists.
- Swaps the movement and jump priorities depending on whether its own HP% is above or below the castle's (±0.15).
- An example of a runtime priority swap. Assigning `pathfindMovement.targetPriorities` at runtime skips the AUTO_Commanded injection.

### CorruptedKingSpawnHelper (CorruptedKingSpawnHelper.cs)
- Fields: `meshParent`, `cloth`, `meshHide` 3, `movementFreeze` 3, `jumpFreeze` 6.
- Spawn-in delay: freezes movement and jumping, hides the mesh, then destroys itself. Needs UnitDashJumpEnemy + PathfindMovementEnemy.
- Usable as a "spawn stun" if a UnitDashJumpEnemy is present; otherwise it NREs.

### PeriodicallyEnableAndDisableDuringEnemySpawn (PeriodicallyEnableAndDisableDuringEnemySpawn.cs)
- Fields: `toEnableAndDisable` (any MonoBehaviour), `enabledDuration` 10, `disabledDuration` 10.
- Duty-cycles a component on `EnemySpawner.LastSpawnPeriodClock`.
- **Generic phase toggler**, e.g. an AutoAttack that is on for 10 s and off for 10 s. Addable: yes.

### Player-side / tower components usable as enemy auras or zones
- **HotOilTower** (HotOilTower.cs): a tag-based AoE DoT pulse.
  - Fields: `hotOilGround` (PS, **required**), `hotOilGroundBurnTickDuration`, `hotOilGroundBurnTicks`, `attackCooldown` 5, `checkRate` 1, `range` 3.5, `damageModifyers`, `mustHaveTags`, `mayNotHaveTags`, `DamageMultiplyer`.
  - Not day-gated. Damage × blacksmith meleeDamage, which is a player buff and odd on enemies.
  - With mustHave [PlayerOwned] it is an **enemy damage aura**. With negative damage and mustHave [EnemyOwned] it is an **enemy heal aura** (negative damage heals).
- **FireArcherBurn**: same idea, but one-shot and self-destroying.
- **BarricadeDamage**: player-only (EnemyOwned + FastMoving hardcoded).
- **SpawnAttackOnCollision**: player-only trap (EnemyOwned hardcoded, needs ProductionBar).
- **RidingDamager**: player only.
- **GlobalHealing**: perk-gated global heal (Phoenix mutator heals all enemies); it destroys itself if the perk is off.

### Visual/helper components that read AutoAttack (copy them when cloning)
- **UnitAttackAnimator**: `unit` (AutoAttack), `attackAnimation`, `additionalAttackAnimations` (OneShotAnimationBase); subscribes to `onAttackTriggered`.
- **RotateForwardToAttackDirection**: `transformToRotate`, `attack`, `attackSmoothTime`, `regularSmoothTime`.
- **UnparentAndDestroyDelayedOnDeath**: `target` (Hp), `extraLifetime`.
- **OnDeathKillAllEnemies**, **OnDeathKillAllPlayerUnits**, **OnDeathStartNextDay**: GetComponent<Hp>, death hooks.
- **AutoKillAllEnemiesBeforeSunrise**.
- **DaytimeSensitiveActivation**: `activeTime`, `target`.

---

## 8. Spawn-time scaling (Spawn.cs:105-271; `AdjustEnemyParametersAfterSpawn` :309-410)

Multipliers applied after Instantiate:
- **Hp**: `maxHp`, then SetHpToMaxHp. Elite: hp ×4, damage ×3, and "Enemy" materials are swapped.
- **Every AutoAttack in children**: `DamageMultiplyer`, `cooldownDuration`, `targetPriorities[].range`, and `breakTargetingDistance` for the warning type.
- **Every PathfindMovementEnemy**: `movementSpeed`, `agroTimeWhenAttackedByPlayer`, `keepDistanceOf`.
- God of Chaos: `SetCooldownTo(3)` and a spawn position lerped toward the castle (not for Exploding).
- God of Afterlife: every second Humanoid gets `enemyToSpawnOnDeath`.
- `EnemySpawnManager.weaponOnSpawn` fires at spawn.

Module components that hold their own damage should read `GetComponent<AutoAttack>().DamageMultiplyer` at use time, as Moleman and DashJump do, so that they scale.

---

## 9. Gotchas for AddComponent on templates
- **G1. Template activation.** AddComponent on an inactive-in-hierarchy template doesn't run Awake/OnEnable/Start. Field initializers do run. Set private [SerializeField] fields by reflection. Instantiate copies serialized fields only.
- **G2. Listeners don't survive cloning.** Non-persistent `AddListener` calls made on the template are lost on Instantiate. Subscribe in a mod component's Start instead.
- **G3. Null lists and events.** Lists and UnityEvents without initializers (e.g. `UnitDashJumpEnemy.onStartJump`, `TriggerOnHealthLoss.onHealthSegmentLost`, Knockback* `mustHaveTags`/`mayNotHaveTags` lists, `SelfDestructWhenInRangeOf.selfDestructIfInRangeOf`) may be null after AddComponent. Assign them explicitly.
- **G4. Shared ScriptableObjects.** Weapon and AdditionalWeaponEffectScript are shared assets, so clone before editing. `additionalWeaponEffects` is private.
- **G5. References that must be copied from vanilla prefabs:**
  - projectile prefab (`projectileVisuals`, needs AimbotProjectile)
  - impact/ground-spawn prefabs
  - melee FX prefabs with SplashDamageArea children and correct layer masks
  - Shapes `Line` (AutoAttackWithWarning) and `Disc` (SpawnAttackAfter)
  - ParticleSystems (HotOilTower, KnockbackTriggeredAuto)
  - the `aliveParent`/`meshParent` transforms of the model (Moleman, DashJump)
  - a shield visual (ShieldOnFirstDamaged)
- **G6. Same-GameObject requirement.** Hp, AutoAttack, PathfindMovement and TaggedObject lookups use `GetComponent` on the same GameObject. Put new AutoAttacks on the root that holds Hp so knockout disables them.
