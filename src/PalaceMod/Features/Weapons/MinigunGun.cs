using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/*
    Runtime half of the minigun: turns the king's Long Bow rig into a minigun for one level, and undoes it.

    The Long Bow's WeaponEquipper is reused (MinigunManager redirects it to the minigun equippable before its Start),
    so the passive ManualAttack keeps its targeting, range, cooldown HUD and perk hooks. On top of it this component:
    - spins up while there is something to shoot and down otherwise; the shot interval goes from SlowInterval to
      1 / fire rate, scaled by whatever cooldown perks did to the bow's cooldown;
    - fires its own bullets (tracers that home on the target) instead of arrows; a missed shot flies past an offset
      point and deals nothing. Hits go through a private clone of the bow's Weapon, so blacksmith, perk and weapon
      effects still apply while the shared asset stays untouched;
    - slows the king while spun up (MinigunManager's MoveScript prefix reads MoveFactor);
    - replaces the bow's stab with Overdrive: full spin, double rate and no slowdown for a few seconds;
    - plays a pitched-up native shot sound per bullet and swaps the bow model for a small gatling built at runtime.
*/
namespace PalaceMod
{
    internal class MinigunGun : MonoBehaviour
    {
        public static MinigunGun Active { get; private set; }

        private const float SlowInterval = 0.45f;      // shot interval when not spun up at all
        private const float SpinDownTime = 1.2f;       // seconds from full spin to stopped
        private const float FiringGrace = 0.3f;        // keep spinning this long after the last shot
        private const float OverdriveSeconds = 4f;
        private const float OverdriveCooldown = 18f;   // before cooldown perks
        private const float BulletSpeed = 70f;
        private const float TracerLength = 1.5f;

        private ManualAttack passive, active;
        private Weapon originalWeapon, bulletWeapon;
        private float originalPassiveCooldown, originalActiveCooldown, originalStabMax;
        private PlayerWeaponAudio weaponAudio;
        private float originalAudioVolume;
        private readonly List<Behaviour> disabled = new();
        private Transform originalActiveMarker;
        private TaggedObject owner;
        private GameObject bowVisuals;
        private readonly List<Renderer> hiddenBowRenderers = new();
        private GameObject gunModel;
        private Transform barrels, muzzle, facing;
        private readonly List<Material> materials = new();

        private float spin;
        private float lastTargetTime = -10f;
        private float nextTargetCheck;
        private bool hasTarget;
        private Hp ownerHp;
        private float overdriveUntil = -10f;
        private float barrelAngle;
        private float shotClock;

        private static readonly AccessTools.FieldRef<ManualAttack, Vector3> LastTargetPos = AccessTools.FieldRefAccess<ManualAttack, Vector3>("lastTargetPos");

        public float Spin => spin;
        public bool Overdrive => Time.time < overdriveUntil;
        public float MoveFactor => Overdrive ? 1f : Mathf.Lerp(1f, MinigunManager.MoveSpeedWhileFiring, Mathf.Clamp01(spin * 1.5f));
        public bool Owns(ManualAttack attack) => attack != null && (attack == passive || attack == active);

        public static MinigunGun Install(WeaponEquipper equipper)
        {
            var gun = equipper.passiveWeapon.gameObject.AddComponent<MinigunGun>();
            gun.Setup(equipper);
            Active = gun;
            return gun;
        }

        private void Setup(WeaponEquipper equipper)
        {
            passive = equipper.passiveWeapon;
            active = equipper.activeWeapon;
            owner = passive.GetComponentInParent<TaggedObject>();
            ownerHp = passive.GetComponentInParent<Hp>();
            facing = equipper.facer != null ? equipper.facer.targetTransform : null;
            originalPassiveCooldown = passive.cooldownTime;
            originalActiveCooldown = active.cooldownTime;
            originalStabMax = active is StabMA stab ? stab.maximumCooldownTime : 0f;

            originalWeapon = passive.weapon;
            bulletWeapon = Instantiate(originalWeapon);
            bulletWeapon.name = "PalaceMod Minigun Bullet";
            passive.weapon = bulletWeapon;
            ApplyDamage();

            weaponAudio = active.GetComponent<PlayerWeaponAudio>();
            if (weaponAudio != null) { originalAudioVolume = weaponAudio.volume; weaponAudio.volume = 0f; }

            // The bow can't shoot while moving; the minigun can (it slows you instead).
            foreach (var delay in equipper.GetComponentsInChildren<DelayManualAttackWhileMoving>(true))
                if (delay.enabled && delay.manualAttack == passive) { delay.enabled = false; disabled.Add(delay); }

            // The stab's "lowest-health enemy" marker means nothing for Overdrive.
            originalActiveMarker = active.attackMarkerPosition;
            if (originalActiveMarker != null) originalActiveMarker.gameObject.SetActive(false);
            active.attackMarkerPosition = null;

            bowVisuals = equipper.visuals;
            BuildModel();
        }

        /// <summary>Puts the bow back exactly as it was. Called when the feature is turned off mid-level.</summary>
        public void Revert()
        {
            if (passive != null) passive.weapon = originalWeapon;
            if (active != null) active.cooldownTime = originalActiveCooldown;
            if (weaponAudio != null) weaponAudio.volume = originalAudioVolume;
            foreach (var b in disabled) if (b != null) b.enabled = true;
            if (active != null) active.attackMarkerPosition = originalActiveMarker;
            foreach (var r in hiddenBowRenderers) if (r != null) r.enabled = true;
            if (gunModel != null) Destroy(gunModel);
            foreach (var m in materials) if (m != null) Destroy(m);
            materials.Clear();
            foreach (var b in bullets) if (b.line != null) Destroy(b.line.gameObject);
            bullets.Clear();
            foreach (var l in pool) if (l != null) Destroy(l.gameObject);
            pool.Clear();
            if (Active == this) Active = null;
            Destroy(this);
        }

        private void OnDestroy()
        {
            if (Active == this) Active = null;
            if (bulletWeapon != null) Destroy(bulletWeapon);
            foreach (var m in materials) if (m != null) Destroy(m);
            foreach (var l in pool) if (l != null) Destroy(l.gameObject);
            foreach (var b in bullets) if (b.line != null) Destroy(b.line.gameObject);
        }

        // Cooldown perks (Light Materials, Royal Forge) scale the bow's cooldownTime; apply the same factor to ours.
        private float PerkCooldownMultiplier() => originalPassiveCooldown > 0f && passive != null ? passive.cooldownTime / originalPassiveCooldown : 1f;

        public void ApplyDamage()
        {
            if (bulletWeapon == null) return;
            float scale = MinigunManager.DamagePerBullet;
            bulletWeapon.directDamage = Scale(originalWeapon.directDamage, scale);
            bulletWeapon.splashDamage = Scale(originalWeapon.splashDamage, scale);
        }

        private static List<DamageModifyer> Scale(List<DamageModifyer> source, float scale)
        {
            var list = new List<DamageModifyer>();
            if (source == null) return list;
            foreach (var m in source)
                list.Add(new DamageModifyer { requiredTags = new List<TagManager.ETag>(m.requiredTags), damageAdded = m.damageAdded * scale, damageMultiplyer = m.damageMultiplyer });
            return list;
        }

        public int Shots { get; private set; }
        public int Hits { get; private set; }

        public void SetModelScale(float scale)
        {
            if (gunModel == null) return;
            gunModel.transform.localScale = Vector3.one * (scale / Mathf.Max(0.0001f, bowVisuals.transform.lossyScale.x));
        }

        public string Describe() =>
            $"shots={Shots} hits={Hits} spin={spin:0.00} overdrive={Overdrive} interval={CurrentInterval():0.000}s move={MoveFactor:0.00} bulletsInFlight={bullets.Count} " +
            $"dmg=[{string.Join(",", bulletWeapon.directDamage.ConvertAll(d => $"{string.Join("+", d.requiredTags)}:{d.damageAdded:0.##}x{d.damageMultiplyer:0.##}"))}] " +
            $"bowCooldown={originalPassiveCooldown} range={passive.targetPriorities[0].range} activeCooldown={active.cooldownTime}";

        private float CurrentInterval()
        {
            float fast = 1f / MinigunManager.FireRate;
            float interval = Mathf.Lerp(SlowInterval, fast, spin * spin);
            if (Overdrive) interval = fast * 0.5f;
            return interval * PerkCooldownMultiplier();
        }

        // ---- Firing (called from the ManualAttack.Attack prefix instead of the bow's attack) ----

        public void PassiveAttack()
        {
            var target = passive.FindAttackTarget(_choosePreferredTargetIfPossible: true);
            if (target == null || target.Hp == null)
            {
                passive.Cooldown = 0f;
                return;
            }
            lastTargetTime = Time.time;
            LastTargetPos(passive) = target.transform.position;

            // At high fire rates the interval can be shorter than a frame; fire what the frame owes (up to 4).
            float interval = CurrentInterval();
            int count = Mathf.Clamp(Mathf.FloorToInt(shotClock / interval), 1, 4);
            shotClock = Mathf.Min(Mathf.Max(0f, shotClock - count * interval), interval);
            passive.Cooldown = interval - shotClock;

            var hp = target.Hp;
            Vector3 origin = muzzle != null ? muzzle.position : passive.transform.position + passive.spawnAttackHeight * Vector3.up;
            for (int i = 0; i < count; i++)
            {
                bool hit = Random.value < MinigunManager.Accuracy || Overdrive && Random.value < 0.5f;
                Vector3 aim = AimPoint(hp, origin);
                Vector3 offset = Vector3.zero;
                if (!hit)
                {
                    Vector2 r = Random.insideUnitCircle.normalized * Random.Range(0.8f, 2.2f);
                    offset = new Vector3(r.x, Random.Range(-0.3f, 0.6f), r.y);
                }
                Shots++;
                // Stagger same-frame bullets along the barrel so they don't overlap.
                Vector3 dir = (aim + offset - origin).normalized;
                Launch(origin + dir * (i * BulletSpeed * interval), hit ? hp : null, aim + offset, dir);
            }
            MuzzleFlash();
            PlayShotSound();
            passive.onAttack.Invoke();
        }

        public void ActiveAttack()
        {
            // Cooldown perks shorten the stab via maximumCooldownTime; scale Overdrive's cooldown the same way.
            float perk = active is StabMA stab && originalStabMax > 0f ? stab.maximumCooldownTime / originalStabMax : 1f;
            active.cooldownTime = OverdriveCooldown * perk;
            active.Cooldown = active.cooldownTime;
            overdriveUntil = Time.time + OverdriveSeconds;
            spin = 1f;
            var audio = ThronefallAudioManager.Instance;
            if (audio != null)
                audio.PlaySoundAsOneShot(audio.audioContent.BallistaShot, 0.9f, 0.7f, audio.audioContent.mixGroupFX, 5);
            Notify.Show("Overdrive!", NotifyKind.Info, 1.5f, "minigun.overdrive");
        }

        private static Vector3 AimPoint(Hp hp, Vector3 from)
        {
            var tagged = hp.TaggedObj;
            if (tagged != null && tagged.colliderForBigOjectsToMeasureDistance != null)
                return tagged.colliderForBigOjectsToMeasureDistance.ClosestPoint(from);
            return hp.transform.position + hp.hitFeedbackHeight * Vector3.up;
        }

        private void PlayShotSound()
        {
            var audio = ThronefallAudioManager.Instance;
            if (audio == null) return;
            var content = audio.audioContent;
            float volume = MinigunManager.Volume;
            if (volume <= 0f) return;
            audio.PlaySoundAsOneShot(content.CrossbowShot ?? content.PlayerBow, volume, Random.Range(1.75f, 2.15f) + (Overdrive ? 0.2f : 0f), content.mixGroupFX, 20);
        }

        // ---- Spin, bullets, model ----

        private void Update()
        {
            if (passive == null) { Destroy(this); return; }
            bool alive = ownerHp != null && ownerHp.HpValue > 0f;
            // Spin while there's something in range (checked a few times a second, not every frame).
            if (Time.time >= nextTargetCheck)
            {
                nextTargetCheck = Time.time + 0.1f;
                hasTarget = alive && passive.FindAttackTarget(_choosePreferredTargetIfPossible: true) != null;
            }
            bool firing = hasTarget || Time.time - lastTargetTime < FiringGrace;
            float spinUp = Mathf.Max(0.05f, MinigunManager.SpinUpTime);
            spin = Mathf.Clamp01(spin + Time.deltaTime * (firing ? 1f / spinUp : -1f / SpinDownTime));
            if (Overdrive) spin = 1f;
            shotClock += Time.deltaTime;
            if (!hasTarget) shotClock = Mathf.Min(shotClock, CurrentInterval());

            // A target appearing while the gun is still: start spinning right away instead of waiting for SlowInterval.
            if (!firing && passive.Cooldown > CurrentInterval()) passive.Cooldown = CurrentInterval();

            if (barrels != null)
            {
                barrelAngle += Time.deltaTime * Mathf.Lerp(0f, Overdrive ? 2200f : 1500f, spin);
                barrels.localRotation = Quaternion.Euler(0f, 0f, barrelAngle);
            }
            UpdateFlash();
            UpdateBullets();
        }

        private struct Bullet
        {
            public LineRenderer line;
            public Vector3 position, direction, end;
            public Hp target;
            public float remaining;
        }

        private readonly List<Bullet> bullets = new();
        private readonly List<LineRenderer> pool = new();
        private static Material tracerMaterial;

        private void Launch(Vector3 origin, Hp target, Vector3 end, Vector3 direction)
        {
            var line = TakeLine();
            float distance = Vector3.Distance(origin, end);
            // Misses keep flying a bit past the target point before fading.
            float remaining = target != null ? distance : distance + 6f;
            bullets.Add(new Bullet { line = line, position = origin, direction = direction, end = end, target = target, remaining = remaining });
        }

        private void UpdateBullets()
        {
            float step = BulletSpeed * Time.deltaTime;
            for (int i = bullets.Count - 1; i >= 0; i--)
            {
                var b = bullets[i];
                if (b.target != null)
                {
                    // Home on the target like the game's arrows do.
                    if (b.target.HpValue <= 0f || b.target.gameObject == null || !b.target.gameObject.activeInHierarchy) { b.target = null; b.remaining = 4f; }
                    else
                    {
                        Vector3 aim = AimPoint(b.target, b.position);
                        Vector3 to = aim - b.position;
                        if (to.magnitude <= step)
                        {
                            bulletWeapon.DealDamage(b.target, passive.AttackDamageMultiplyer, owner);
                            Hits++;
                            Recycle(b.line);
                            bullets.RemoveAt(i);
                            continue;
                        }
                        b.direction = to.normalized;
                        b.remaining = to.magnitude;
                    }
                }
                b.position += b.direction * step;
                b.remaining -= step;
                if (b.target == null && b.remaining <= 0f)
                {
                    Recycle(b.line);
                    bullets.RemoveAt(i);
                    continue;
                }
                b.line.SetPosition(0, b.position - b.direction * TracerLength);
                b.line.SetPosition(1, b.position);
                bullets[i] = b;
            }
        }

        private LineRenderer TakeLine()
        {
            LineRenderer line;
            if (pool.Count > 0) { line = pool[pool.Count - 1]; pool.RemoveAt(pool.Count - 1); line.gameObject.SetActive(true); return line; }
            var go = new GameObject("Minigun Tracer");
            line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.widthCurve = new AnimationCurve(new Keyframe(0f, 0.02f), new Keyframe(1f, 0.15f));
            var color = new Color(1f, 0.86f, 0.45f);
            line.startColor = new Color(color.r, color.g, color.b, 0f);
            line.endColor = color;
            line.sharedMaterial = TracerMaterial();
            return line;
        }

        private void Recycle(LineRenderer line)
        {
            if (line == null) return;
            line.gameObject.SetActive(false);
            pool.Add(line);
        }

        internal static Material TracerMaterial()
        {
            if (tracerMaterial != null) return tracerMaterial;
            foreach (var name in new[] { "Sprites/Default", "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Particles/Unlit", "Unlit/Color" })
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;
                tracerMaterial = new Material(shader) { name = "PalaceMod Minigun Tracer" };
                if (tracerMaterial.HasProperty("_BaseColor")) tracerMaterial.SetColor("_BaseColor", Color.white);
                if (tracerMaterial.HasProperty("_Color")) tracerMaterial.SetColor("_Color", Color.white);
                Plugin.Logger.LogInfo($"Minigun: tracer shader {name}");
                return tracerMaterial;
            }
            Plugin.Logger.LogWarning("Minigun: no unlit shader found for tracers");
            tracerMaterial = new Material(Shader.Find("Hidden/InternalErrorShader"));
            return tracerMaterial;
        }

        // ---- Muzzle flash: a short, wide-based streak at the barrel tip ----

        private LineRenderer flash;
        private float flashUntil;

        private void MuzzleFlash()
        {
            if (muzzle == null) return;
            if (flash == null)
            {
                flash = new GameObject("Minigun Muzzle Flash").AddComponent<LineRenderer>();
                flash.transform.SetParent(muzzle, false);
                flash.useWorldSpace = false;
                flash.positionCount = 2;
                flash.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                flash.sharedMaterial = TracerMaterial();
                flash.startColor = new Color(1f, 0.95f, 0.7f, 1f);
                flash.endColor = new Color(1f, 0.6f, 0.2f, 0f);
            }
            float length = Random.Range(0.35f, 0.6f);
            flash.SetPosition(0, Vector3.zero);
            flash.SetPosition(1, new Vector3(0f, 0f, length));
            flash.widthCurve = new AnimationCurve(new Keyframe(0f, Random.Range(0.22f, 0.32f)), new Keyframe(1f, 0f));
            flash.enabled = true;
            flashUntil = Time.time + 0.04f;
        }

        private void UpdateFlash()
        {
            if (flash != null && flash.enabled && Time.time > flashUntil) flash.enabled = false;
        }

        // ---- Model: a small six-barrel gatling in place of the bow, using the bow's own material ----

        private void BuildModel()
        {
            if (bowVisuals == null) return;
            Material material = null;
            foreach (var r in bowVisuals.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (material == null) material = r.sharedMaterial;
                if (r.enabled) { r.enabled = false; hiddenBowRenderers.Add(r); }
            }

            // Sit where the bow sits, pointing the way the king faces; the bow's parent bone carries it with the rig.
            Transform anchor = bowVisuals.transform;
            foreach (var r in hiddenBowRenderers) { anchor = r.transform; break; }
            gunModel = new GameObject("PalaceMod Minigun Model");
            gunModel.transform.SetParent(bowVisuals.transform, false);
            gunModel.transform.position = anchor.position;
            var forward = facing != null ? facing.forward : passive.transform.forward;
            forward.y = 0f;
            gunModel.transform.rotation = Quaternion.LookRotation(forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward, Vector3.up);
            float parentScale = Mathf.Max(0.0001f, bowVisuals.transform.lossyScale.x);
            gunModel.transform.localScale = Vector3.one * (MinigunManager.ModelScale / parentScale);
            Plugin.Logger.LogInfo($"Minigun: model at {anchor.name} pos={anchor.position} parentScale={parentScale} facing={forward}");

            var body = Part(PrimitiveType.Cube, gunModel.transform, new Vector3(0f, 0f, -0.05f), new Vector3(0.28f, 0.26f, 0.42f), material, new Color(0.35f, 0.33f, 0.36f));
            body.name = "Body";
            Part(PrimitiveType.Cube, gunModel.transform, new Vector3(0f, -0.2f, -0.12f), new Vector3(0.09f, 0.22f, 0.1f), material, new Color(0.45f, 0.3f, 0.2f)).name = "Grip";

            barrels = new GameObject("Barrels").transform;
            barrels.SetParent(gunModel.transform, false);
            barrels.localPosition = new Vector3(0f, 0f, 0.15f);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f;
                var barrel = Part(PrimitiveType.Cylinder, barrels, new Vector3(Mathf.Cos(a) * 0.075f, Mathf.Sin(a) * 0.075f, 0.32f), new Vector3(0.05f, 0.32f, 0.05f), material, new Color(0.22f, 0.22f, 0.25f));
                barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            var ring = Part(PrimitiveType.Cylinder, barrels, new Vector3(0f, 0f, 0.5f), new Vector3(0.22f, 0.025f, 0.22f), material, new Color(0.55f, 0.5f, 0.42f));
            ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(gunModel.transform, false);
            muzzle.localPosition = new Vector3(0f, 0f, 0.82f);
        }

        private GameObject Part(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Material material, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            if (material != null)
            {
                var m = new Material(material) { name = "PalaceMod Minigun Part" };
                materials.Add(m);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                if (m.HasProperty("_Color")) m.SetColor("_Color", color);
                renderer.sharedMaterial = m;
            }
            return go;
        }
    }
}
